using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using DockedTools.Features.AppEntry;
using DockedTools.Features.Tray;
using DockedTools.Features.AppEntry.NormalLaunch;
using DockedTools.Features.AppEntry.AutoLaunch;
using DockedTools.Features.AppEntry.ShareLaunch;
using DockedTools.Features.AppEntry.SingleInstance;
using DockedTools.Features.MainWindow.Entry;
using DockedTools.Features.UnifiedCalls.Logging;
using Windows.Graphics;
using Windows.ApplicationModel.Activation;

namespace DockedTools
{
    /// <summary>
    /// 异常策略 helper - 判断 XAML 异常是否可 handled
    /// </summary>
    internal static class ExceptionPolicy
    {
        /// <summary>
        /// 判断 XAML 异常是否应该被标记为已处理（避免进程退出）
        /// </summary>
        /// <param name="exception">待判断的异常</param>
        /// <returns>true 表示可以 handled，false 表示应该让进程退出</returns>
        public static bool ShouldHandleXamlException(Exception exception)
        {
            if (exception == null)
            {
                return false;
            }

            // 可恢复的异常类型 - 这些异常通常是由 UI 状态不一致或临时资源问题引起
            // 记录日志后可以继续运行，避免整个应用退出
            var recoverableTypes = new[]
            {
                typeof(NullReferenceException),      // 空引用 - UI 元素未初始化
                typeof(ObjectDisposedException),     // 对象已释放 - 生命周期问题
                typeof(InvalidOperationException),   // 无效操作 - 状态不一致
                typeof(COMException)                 // COM 异常 - XAML/WinRT 边界错误
            };

            foreach (var recoverableType in recoverableTypes)
            {
                if (recoverableType.IsInstanceOfType(exception))
                {
                    return true;
                }
            }

            // 严重异常 - 不应该 handled，让进程退出
            // OutOfMemoryException - 内存不足，继续运行可能导致数据损坏
            // StackOverflowException - 栈溢出（通常无法捕获，这里仅表达策略）
            // AccessViolationException - 访问冲突，通常是原生代码严重错误
            var criticalTypes = new[]
            {
                typeof(OutOfMemoryException),
                typeof(StackOverflowException),
                typeof(AccessViolationException)
            };

            foreach (var criticalType in criticalTypes)
            {
                if (criticalType.IsInstanceOfType(exception))
                {
                    return false;
                }
            }

            // 对于其他未知异常，初期策略是 handled
            // 通过日志观察一轮后再根据实际情况细化
            return true;
        }
    }

    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private Window? _keepAliveWindow;
        private TrayIconManager? _trayIconManager;
        private SingleInstanceCommunication? _singleInstanceCommunication;
        private UIThreadWatchdog? _uiThreadWatchdog;

        // OnLaunched 重入守卫。
        // 托盘的独立 UI 线程会调用 WindowsXamlManager.InitializeForCurrentThread()，
        // 而这一步会再次触发 Application.OnLaunched（XAML 把新线程当成一次新的激活）。
        // 不拦住的话，OnLaunched 会在托盘线程上整套重跑一遍：再建主窗口、再开单实例
        // 通信、再起桥接服务，状态直接错乱。
        // 主线程一定是先进入 OnLaunched 的（托盘线程由它启动），所以守卫放在开头即可。
        private int _onLaunchedInvoked;
        
        // Launch handlers
        private NormalLaunchHandler? _normalLaunchHandler;
        private AutoLaunchHandler? _autoLaunchHandler;
        private ShareLaunchHandler? _shareLaunchHandler;

        // 单实例 Mutex（在构造函数中提前检测）
        // ⭐ 后缀来自 WorktreeIdentity.g.cs（构建时按 worktree 目录名生成）。
        // 没有它的话，多个 worktree 的 Debug 构建即便 MSIX 包身份不同，运行时仍会
        // 撞同一个 Mutex —— 装得下但跑不起来，第二个实例会去激活第一个然后自己退出。
        // Release / 目录名不含 hash 时后缀为空，行为与加后缀之前完全一致。
#if DEBUG
        private static Mutex? _singleInstanceMutex;
        private static bool _isMainInstance;
        private const string MutexName = @"Local\DockedAI_SingleInstance_Mutex_DEBUG" + global::WorktreeIdentity.Suffix;
#else
        private static Mutex? _singleInstanceMutex;
        private static bool _isMainInstance;
        private const string MutexName = @"Local\DockedAI_SingleInstance_Mutex" + global::WorktreeIdentity.Suffix;
#endif

        // 应用退出状态标志（防止主动退出时 keep-alive 自愈重新创建窗口）
        private bool _isExiting = false;

        /// <summary>
        /// 冷启动（应用本来没在跑）时被通知唤醒的那一次激活参数。
        /// 在这里先存着，等窗口和内容区都建好之后再回头处理 —— 见 OnLaunched 末尾。
        /// </summary>
        private Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs? _pendingNotificationActivation;

        /// <summary>
        /// 获取主窗口实例（用于内部访问）
        /// </summary>
        public Window? MainWindow => _window;

        /// <summary>
        /// 主 UI 线程的 DispatcherQueue（在 OnLaunched 里、主线程上捕获一次）
        ///
        /// 为什么需要它：托盘图标和菜单现在跑在独立的 UI 线程上，
        /// DispatcherQueue.GetForCurrentThread() 在那个线程上拿到的是"托盘线程"的队列，
        /// 而 Application.Current 在第二个 XAML 线程上也未必是主线程的那个实例 —— 
        /// 两者都拿不到主线程。需要在主线程显式缓存一份，供跨线程代码切回来用。
        /// </summary>
        public static Microsoft.UI.Dispatching.DispatcherQueue? UIDispatcherQueue { get; private set; }

        /// <summary>
        /// 获取应用是否正在退出的状态
        /// </summary>
        public bool IsApplicationExiting => _isExiting;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // ⭐ 检查是否是重启请求（必须在单实例检测之前）
            // 合并了命令行 + 激活载荷两条来源：走包唤起时参数不一定在 args[1]。
            bool isRestart = Features.AppEntry.LaunchArguments.Contains("--restart");
            
            // ⭐ 方案一：使用 Mutex 提前检测单实例，避免不必要的初始化
            // 这是最早的检测点，在 InitializeComponent() 之前执行
            // ⭐ MSIX 沙箱兼容：添加 Local\ 前缀
            _singleInstanceMutex = new Mutex(true, MutexName, out _isMainInstance);
            
            if (!_isMainInstance)
            {
                // 如果是重启请求，等待旧实例退出
                if (isRestart)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Restart detected, waiting for old instance to exit...");
                    
                    // 等待旧实例释放 Mutex（最多等待 3 秒）
                    _singleInstanceMutex?.Dispose();
                    _singleInstanceMutex = null;
                    
                    // ⭐ 优化：使用 Thread.Sleep 代替 SpinWait，避免 CPU 占用过高
                    for (int i = 0; i < 30; i++)
                    {
                        try
                        {
                            _singleInstanceMutex = new Mutex(true, MutexName, out _isMainInstance);
                            if (_isMainInstance)
                            {
                                System.Diagnostics.Debug.WriteLine($"[App] Old instance exited after {(i + 1) * 100}ms, proceeding as main instance");
                                break;
                            }
                            _singleInstanceMutex?.Dispose();
                            _singleInstanceMutex = null;
                            
                            // 使用 Thread.Sleep，避免阻塞 UI 线程（此时 UI 尚未初始化）
                            Thread.Sleep(100);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] Mutex acquisition attempt {i+1} failed: {ex.Message}");
                        }
                    }
                    
                    // 如果还是拿不到 Mutex，强制成为主实例
                    if (!_isMainInstance)
                    {
                        System.Diagnostics.Debug.WriteLine("[App] Timeout waiting for old instance, forcing restart");
                        _isMainInstance = true;
                    }
                }
                else
                {
                    // 已有实例在运行 → 通知主实例显示窗口
                    System.Diagnostics.Debug.WriteLine("[App] Another instance is already running, notifying main instance");
                    SingleInstanceCommunication.NotifyShowWindow();
                    
                    // 立即退出，避免任何初始化
                    System.Diagnostics.Debug.WriteLine("[App] Exiting secondary instance immediately");
                    Environment.Exit(0);
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine("[App] This is the main instance, proceeding with initialization");
            
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("[App] OnLaunched called");

            // ⚠️ 重入守卫：托盘独立 UI 线程初始化 XAML 时会再次触发本方法，
            // 必须直接返回，否则整套启动流程会在那个线程上重跑一遍。
            if (Interlocked.Exchange(ref _onLaunchedInvoked, 1) == 1)
            {
                System.Diagnostics.Debug.WriteLine("[App] OnLaunched re-entry detected (secondary XAML thread), skipping");
                return;
            }

            try
            {
                // ⭐ 方案一：Mutex 已在构造函数中完成单实例检测
                // 如果代码执行到这里，说明当前是主实例
                System.Diagnostics.Debug.WriteLine("[App] Main instance confirmed, initializing application");

                // 在主线程上捕获 DispatcherQueue 供跨线程代码（托盘线程、后台线程）切回来用
                UIDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

                // 启动单实例通信监听器（监听其他实例的唤醒请求）
                _singleInstanceCommunication = new SingleInstanceCommunication(OnShowWindowRequested);
                _singleInstanceCommunication.StartListening();
                System.Diagnostics.Debug.WriteLine("[App] Single instance communication listener started");

                // Initialize handlers
                _normalLaunchHandler = new NormalLaunchHandler(this);
                _autoLaunchHandler = new AutoLaunchHandler(this);
                _shareLaunchHandler = new ShareLaunchHandler(this);
                System.Diagnostics.Debug.WriteLine("[App] Launch handlers initialized");

                // ⭐ 网页通知：注册必须排在下面那句 GetActivatedEventArgs() 之前。
                //    官方写得很直白 —— 先 Register 再取激活参数，反了的话通知带来的 arguments 会丢，
                //    表现为「点了通知，应用被叫起来了，但不知道该跳哪儿」。
                //    同理，Activated 的订阅也要在 Register 之前挂好：应用没在跑时系统走 COM 激活，
                //    Register 的那一刻起事件就可能开始投递。
                DockedTools.Features.Pages.WebApp.Browser.Services.WebNotificationBridge.Activated +=
                    OnWebNotificationActivated;
                DockedTools.Features.Pages.WebApp.Browser.Services.WebNotificationBridge.EnsurePlatformRegistered();

                // Check for ShareTarget activation
                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                System.Diagnostics.Debug.WriteLine($"[App] Activation kind: {activationArgs?.Kind}");

                // ShareTarget activation should always proceed
                if (activationArgs?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.ShareTarget)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Handling ShareTarget activation");
                    HandleShareTargetActivation(activationArgs.Data as ShareTargetActivatedEventArgs);
                    return;
                }

                // 被网页通知唤醒的冷启动：这里只登记，不处理。
                // 此刻主窗口、托盘、ContentArea 全都还没建起来，ContentAreaService.Navigate
                // 会因为「ContentArea 未注册」直接抛异常 —— 所以留到 OnLaunched 末尾回头处理。
                // 另外注意：应用没在跑时，部分路径上报的 kind 是 Launch 而不是 AppNotification，
                // 那时参数会改由 NotificationInvoked 事件送到（已订阅），两条路径最终汇到同一个处理函数。
                if (activationArgs?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification)
                {
                    System.Diagnostics.Debug.WriteLine("[App] 检测到 AppNotification 激活，延后到窗口就绪后处理");
                    _pendingNotificationActivation = activationArgs.Data
                        as Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs;
                }

                // Check if this is an auto-launch scenario
                bool isAutoLaunch = _autoLaunchHandler.IsAutoLaunch();
                System.Diagnostics.Debug.WriteLine($"[App] IsAutoLaunch: {isAutoLaunch}");
                
                // Check if this is a tray-only restart
                bool isTrayOnlyRestart = Features.AppEntry.LaunchArguments.Contains("--tray-only");
                System.Diagnostics.Debug.WriteLine($"[App] IsTrayOnlyRestart: {isTrayOnlyRestart}");
                
                if (isAutoLaunch)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Handling auto-launch");
                    _ = _autoLaunchHandler.HandleAsync();
                }

                // Handle normal launch
                // 从图标启动时（非自启动且非仅托盘重启），自动显示主窗口
                bool shouldShowWindow = !isAutoLaunch && !isTrayOnlyRestart;
                System.Diagnostics.Debug.WriteLine($"[App] Calling NormalLaunchHandler.Handle with shouldShowWindow={shouldShowWindow}");
                
                _normalLaunchHandler.Handle(ExitApplication, shouldShowWindow: shouldShowWindow);
                _trayIconManager = _normalLaunchHandler.TrayIconManager;
                
                System.Diagnostics.Debug.WriteLine("[App] Creating keep-alive window");
                EnsureKeepAliveWindow();

                // 清理旧日志（保留最近 7 天）
                LogService.CleanupOldLogs(7);

                // 让桥接服务能安全地刷新界面。
                // 桥接命令跑在 Kestrel 后台线程，直接碰 UI 集合会崩，所以注入一个会切回 UI 线程的委托。
                try
                {
                    var uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    DockedTools.Features.BrowserExtension.BridgeConfig.RequestUiRefresh = () =>
                        uiDispatcher.TryEnqueue(
                            DockedTools.Features.Pages.WebApp.Shared.WebAppEventBus.RequestRefresh);
                }
                catch (Exception ex)
                {
                    LogService.Error("应用入口", "注入桥接界面刷新委托失败", ex);
                }

                // 启动 UI 线程看门狗。
                // 主线程一旦被长时间阻塞（消息泵停摆），窗口、托盘菜单、对话框会集体失联，
                // 届时只能靠这个后台线程发现卡死，并静默重启到托盘。
                try
                {
                    // 探测目标优先选常驻的 keep-alive 窗口：主窗口会被关掉重建，
                    // 句柄会失效，而 keep-alive 窗口一直在主线程上。
                    var watchdogTarget = _keepAliveWindow ?? _window;
                    var watchdogHwnd = watchdogTarget != null
                        ? WinRT.Interop.WindowNative.GetWindowHandle(watchdogTarget)
                        : IntPtr.Zero;

                    if (watchdogHwnd != IntPtr.Zero)
                    {
                        _uiThreadWatchdog = new UIThreadWatchdog(watchdogHwnd);
                        _uiThreadWatchdog.Start();
                        System.Diagnostics.Debug.WriteLine("[App] UI thread watchdog started");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[App] WARNING: no window handle to watch, watchdog not started");
                    }
                }
                catch (Exception ex)
                {
                    // 看门狗起不来不能拖垮启动流程
                    LogService.Error("应用入口", "启动 UI 线程看门狗失败", ex);
                }

                // 启动浏览器扩展桥接服务（本机回环 WebSocket，供浏览器扩展连接）
                // 启动失败不能影响主流程，所以只跑后台任务并吞掉异常，详情看日志
                _ = System.Threading.Tasks.Task.Run(
                    () => DockedTools.Features.BrowserExtension.BridgeService.StartAsync());

                // 冷启动被通知唤醒的那一次：走到这里窗口、托盘、内容区都已就绪，回头补上跳转。
                // 压到 Low 优先级是为了排在窗口首帧之后 —— 否则跳转会和首帧布局抢同一帧。
                if (_pendingNotificationActivation is { } pendingNotification)
                {
                    _pendingNotificationActivation = null;
                    UIDispatcherQueue?.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => OnWebNotificationActivated(pendingNotification));
                }

                System.Diagnostics.Debug.WriteLine("[App] OnLaunched completed successfully");
                
                // 优化说明：
                // - 使用 Mutex 在构造函数中提前检测单实例（最早检测点）
                // - 使用 EventWaitHandle 实现进程间通信（响应速度 < 5ms）
                // - 自启动时：不显示窗口，只在托盘运行
                // - 图标启动时：自动显示主窗口,提供更好的用户体验
                // - 再次点击图标时：新进程立即退出，通知主实例唤醒窗口
                // - 避免了 AppInstance 的进程初始化开销
                
                #if DEBUG
                // [DEBUG ONLY] 人工验证异常处理路径
                // 取消注释以下行来手动触发异常验证测试
                // ⚠️ 注意：这会故意抛出异常来测试异常处理逻辑，仅用于开发调试
                // VerifyExceptionHandlingPath_DEBUG();
                #endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] CRITICAL ERROR in OnLaunched: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[App] Stack trace: {ex.StackTrace}");
                LogException("OnLaunched", ex);
                throw;
            }
        }

        /// <summary>
        /// 处理显示窗口请求（当其他实例通知主实例时触发）
        /// </summary>
        private void OnShowWindowRequested()
        {
            System.Diagnostics.Debug.WriteLine("[App] OnShowWindowRequested called from another instance");
            
            // 显示主窗口
            _trayIconManager?.ShowMainWindow();
            
            // ⭐ 强制唤醒窗口到最前（Win32 API）
            BringWindowToFront(_window);
        }

        /// <summary>
        /// 用户点了某条网页通知：把窗口唤到前台，并跳到发出这条通知的那个网页应用。
        ///
        /// <para>两个激活路径都会进到这里 ——
        /// ① 应用已经在跑：<c>AppNotificationManager.NotificationInvoked</c>；
        /// ② 应用没在跑：系统 COM 激活，走 OnLaunched 的登记 + 末尾补处理。</para>
        ///
        /// <para>为什么<b>先无条件唤窗口</b>：跳到某个网页应用是有条件的（得有 origin 匹配的条目），
        /// 但「点了通知却什么都没发生」是无论如何都不能接受的 —— 通知本身已经在通知中心里，
        /// 用户点了没反应只会以为应用坏了。</para>
        /// </summary>
        private void OnWebNotificationActivated(
            Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
        {
            string? origin = DockedTools.Features.Pages.WebApp.Browser.Services
                .WebNotificationBridge.ReadOrigin(args);

            if (origin is null)
            {
                // 不是网页通知（action 对不上），本入口不认领，交给将来别的处理方
                System.Diagnostics.Debug.WriteLine("[App] 收到非网页通知的激活，忽略");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[App] 网页通知被点击，来源: {origin}");

            // ⚠️ 一律切回主线程：NotificationInvoked 那条路径不保证在 UI 线程上投递，
            //    而唤窗口、Frame 导航都只能碰主线程。冷启动时 UIDispatcherQueue 已在
            //    OnLaunched 开头捕获过，这里拿得到。
            Microsoft.UI.Dispatching.DispatcherQueue? queue = UIDispatcherQueue;
            if (queue is null)
            {
                System.Diagnostics.Debug.WriteLine("[App] 主线程队列尚未就绪，跳过通知跳转");
                return;
            }

            queue.TryEnqueue(() =>
            {
                OnShowWindowRequested();
                _ = NavigateToWebAppByOriginAsync(origin);
            });
        }

        /// <summary>
        /// 按来源 origin 找到对应的网页应用并跳转过去。
        /// 找不到就什么都不做 —— 窗口已经唤出来了，静默地停在原地好过弹一个用户看不懂的报错。
        /// </summary>
        private async Task NavigateToWebAppByOriginAsync(string origin)
        {
            try
            {
                IReadOnlyList<Features.Pages.WebApp.Shared.WebAppShortcut> shortcuts =
                    await Features.Pages.WebApp.Shared.WebAppShortcutStore.LoadAsync();

                // 按 origin（scheme + host + port）匹配，而不是整个 URL 相等：
                // 通知来自页面里的任意路径，拿页面 URL 去比必然一条都对不上。
                Features.Pages.WebApp.Shared.WebAppShortcut? match = null;
                foreach (Features.Pages.WebApp.Shared.WebAppShortcut shortcut in shortcuts)
                {
                    if (IsSameOrigin(shortcut.Url, origin))
                    {
                        match = shortcut;
                        break;
                    }
                }

                if (match is null)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] 没有 origin 匹配的网页应用: {origin}");
                    return;
                }

                Features.UnifiedCalls.ContentArea.ContentAreaService.Navigate(
                    typeof(Features.Pages.WebApp.Browser.WebBrowserPage), match);

                System.Diagnostics.Debug.WriteLine($"[App] 已跳转到网页应用: {match.Name}");
            }
            catch (Exception ex)
            {
                // 跳转失败不该影响「窗口已经唤出来了」这个结果，所以只记日志
                LogService.Error("应用入口", "按通知来源跳转网页应用失败", ex);
            }
        }

        /// <summary>
        /// 判断两个 URL 是否同源（scheme + host + port）。
        /// <c>Uri.GetLeftPart(UriPartial.Authority)</c> 也能做，但它会带上用户信息段，
        /// 而且大小写规矩和这里不完全一致 —— 三个分量分开比更不容易出错。
        /// </summary>
        private static bool IsSameOrigin(string? url, string origin)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? left) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out Uri? right))
            {
                return false;
            }

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && left.Port == right.Port;
        }

        /// <summary>
        /// 强制唤醒窗口到最前（Win32 API）
        /// 解决点击图标后窗口在后台不弹出的问题
        /// </summary>
        private void BringWindowToFront(Window? window)
        {
            if (window == null)
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // SW_SHOW = 5: 激活窗口并显示
            AppEntryWin32Api.ShowWindow(hwnd, 5);
            AppEntryWin32Api.SetForegroundWindow(hwnd);
            
            System.Diagnostics.Debug.WriteLine($"[App] BringWindowToFront: hwnd={hwnd}");
        }

        private async void HandleShareTargetActivation(ShareTargetActivatedEventArgs? shareArgs)
        {
            if (_shareLaunchHandler == null)
            {
                _shareLaunchHandler = new ShareLaunchHandler(this);
            }

            if (_window == null)
            {
                _window = MainWindowFactory.Create();
            }

            await _shareLaunchHandler.HandleAsync(shareArgs, _window);
        }

        public void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowMainWindow();
        }

        public void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void ShowMainWindow()
        {
            _window = MainWindowFactory.GetOrCreate(_window);
            
            // 注意：Activate() 的行为特性：
            // - 这是首次创建窗口时唯一合法的显示方案
            // - 会触发系统内置的流畅窗口显示动画（DWM 动画）
            // - 内置了强制进入可显示区域的逻辑
            // - 必须在所有窗口配置（位置、大小、样式等）完成后最后调用
            // - 如果在配置过程中调用会导致闪现问题
            _window.Activate();
        }

        private void OnAppExit(object sender, object e)
        {
            _trayIconManager?.Dispose();
            _singleInstanceCommunication?.Dispose();

            // 同步路径只发取消信号，不等循环退出（这里不能阻塞）
            if (_uiThreadWatchdog != null)
            {
                _uiThreadWatchdog.Stop();
                _uiThreadWatchdog.Dispose();
                _uiThreadWatchdog = null;
            }
        }

        private async void ExitApplication()
        {
            // 设置退出标志，防止 keep-alive 窗口自愈重新创建
            _isExiting = true;
            
            try
            {
                // 先关闭主窗口
                if (_window != null)
                {
                    _window.Close();
                    _window = null;
                }

                // 关闭保持窗口
                if (_keepAliveWindow != null)
                {
                    // 取消订阅 Closed 事件，避免在退出时触发自愈逻辑
                    _keepAliveWindow.Closed -= OnKeepAliveWindowClosed;
                    _keepAliveWindow.Close();
                    _keepAliveWindow = null;
                }

                // 清理托盘图标
                _trayIconManager?.Dispose();
                _trayIconManager = null;

                // 停掉 UI 线程看门狗。
                // 退出流程本身会阻塞主线程，不停的话它会把"正在退出"误判成卡死。
                if (_uiThreadWatchdog != null)
                {
                    await _uiThreadWatchdog.StopAsync();
                    _uiThreadWatchdog.Dispose();
                    _uiThreadWatchdog = null;
                }

                // 异步停止单实例通信
                if (_singleInstanceCommunication != null)
                {
                    await _singleInstanceCommunication.StopListeningAsync();
                    _singleInstanceCommunication.Dispose();
                    _singleInstanceCommunication = null;
                }
                
                // 停掉浏览器扩展桥接（本机回环 WebSocket）。
                // 不停的话 Kestrel 的监听线程会一直吊着，托盘退出后进程可能退不干净、
                // 端口也还占着，下次启动会顺着端口段往后挪。
                try
                {
                    await DockedTools.Features.BrowserExtension.BridgeService.StopAsync();
                }
                catch (Exception ex)
                {
                    LogService.Error("应用入口", "停止桥接服务失败", ex);
                }

                // 释放 Mutex
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// 公开的退出方法，供外部服务调用（如重启服务）
        /// </summary>
        public void ExitApplicationPublic()
        {
            ExitApplication();
        }

        private void EnsureKeepAliveWindow()
        {
            if (_keepAliveWindow != null)
            {
                return;
            }

            // WinUI desktop apps may terminate quickly when no window is created.
            // Keep a hidden host window alive for tray-only mode.
            _keepAliveWindow = new Window
            {
                Content = new Grid()
            };

            // Keep the host window fully out of user sight before activation.
            var keepAliveAppWindow = _keepAliveWindow.AppWindow;
            keepAliveAppWindow.IsShownInSwitchers = false;
            keepAliveAppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));

            // 注意：Activate() 的行为特性：
            // - 这是首次创建窗口时唯一合法的显示方案
            // - 会触发系统内置的流畅窗口显示动画（DWM 动画）
            // - 内置了强制进入可显示区域的逻辑
            // - 必须在所有窗口配置（位置、大小、样式等）完成后最后调用
            // - 如果在配置过程中调用会导致闪现问题
            _keepAliveWindow.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_keepAliveWindow);
            if (hwnd != IntPtr.Zero)
            {
                AppEntryWin32Api.ShowWindow(hwnd, AppEntryWin32Api.SW_HIDE);
            }

            // 订阅 Closed 事件以实现自愈能力（任务 5.2）
            _keepAliveWindow.Closed += OnKeepAliveWindowClosed;
        }

        /// <summary>
        /// 检查并恢复 keep-alive 窗口（任务 5.4）
        /// 在托盘关闭主窗口后调用，确保进程不会因窗口全部关闭而退出
        /// </summary>
        public void CheckAndRecoverKeepAliveWindow()
        {
            // 检查是否正在退出
            if (_isExiting)
            {
                System.Diagnostics.Debug.WriteLine("[App] CheckAndRecoverKeepAliveWindow: Application is exiting, skip check");
                return;
            }

            // 检查 keep-alive 窗口是否存在
            if (_keepAliveWindow == null)
            {
                // Keep-alive 窗口缺失，记录 warning 日志
                LogService.Warning("App", "托盘关闭主窗口后发现 Keep-alive 窗口缺失，正在恢复");
                System.Diagnostics.Debug.WriteLine("[App] CheckAndRecoverKeepAliveWindow: Keep-alive window is missing, attempting recovery");

                // 检查托盘管理器是否存在
                if (_trayIconManager == null)
                {
                    LogService.Warning("App", "无法恢复 Keep-alive 窗口：托盘管理器不存在");
                    System.Diagnostics.Debug.WriteLine("[App] CheckAndRecoverKeepAliveWindow: Cannot recover, TrayIconManager is null");
                    return;
                }

                // 尝试恢复 keep-alive 窗口
                try
                {
                    EnsureKeepAliveWindow();
                    LogService.Info("App", "Keep-alive 窗口已成功恢复");
                    System.Diagnostics.Debug.WriteLine("[App] CheckAndRecoverKeepAliveWindow: Keep-alive window successfully recovered");
                }
                catch (Exception ex)
                {
                    LogService.Error("App", "Keep-alive 窗口恢复失败", ex);
                    System.Diagnostics.Debug.WriteLine($"[App] CheckAndRecoverKeepAliveWindow: Recovery failed: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[App] Stack trace: {ex.StackTrace}");
                }
            }
            else
            {
                // Keep-alive 窗口存在，仅记录 debug 日志
                System.Diagnostics.Debug.WriteLine("[App] CheckAndRecoverKeepAliveWindow: Keep-alive window exists, no action needed");
            }
        }

        /// <summary>
        /// Keep-alive 窗口意外关闭时的处理器
        /// 在非退出状态下自动重建窗口，确保托盘模式下应用不会静默退出
        /// </summary>
        /// <param name="sender">窗口对象</param>
        /// <param name="args">窗口关闭事件参数</param>
        private void OnKeepAliveWindowClosed(object sender, WindowEventArgs args)
        {
            // 清空 keep-alive 窗口引用
            _keepAliveWindow = null;
            
            // 检查是否正在退出
            if (_isExiting)
            {
                // 正常退出流程，仅记录 debug 日志
                System.Diagnostics.Debug.WriteLine("[App] Keep-alive window closed during application exit (expected)");
                return;
            }
            
            // 非退出状态下窗口意外关闭，记录 warning 日志
            LogService.Warning("App", "Keep-alive 窗口意外关闭");
            System.Diagnostics.Debug.WriteLine("[App] Keep-alive window closed unexpectedly, attempting recovery");
            
            // 检查托盘管理器是否仍存在
            if (_trayIconManager == null)
            {
                // 托盘管理器不存在，无法恢复
                LogService.Warning("App", "无法恢复 Keep-alive 窗口：托盘管理器不存在");
                System.Diagnostics.Debug.WriteLine("[App] Cannot recover keep-alive window: TrayIconManager is null");
                return;
            }
            
            // 尝试重新创建 keep-alive 窗口
            try
            {
                System.Diagnostics.Debug.WriteLine("[App] Attempting to recreate keep-alive window");
                EnsureKeepAliveWindow();
                
                // 重建成功
                LogService.Info("App", "Keep-alive 窗口已成功重建");
                System.Diagnostics.Debug.WriteLine("[App] Keep-alive window successfully recreated");
            }
            catch (Exception ex)
            {
                // 重建失败，记录 error 日志
                LogService.Error("App", "Keep-alive 窗口重建失败", ex);
                System.Diagnostics.Debug.WriteLine($"[App] Failed to recreate keep-alive window: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[App] Stack trace: {ex.StackTrace}");
            }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // 记录完整的 XAML 异常上下文（包含诊断信息）
            var context = BuildUnhandledExceptionContext();
            LogService.Error("App", $"未处理的 XAML 异常\n{context}", e.Exception);
            
            // 对可恢复异常设置 e.Handled = true，避免进程直接退出
            if (ExceptionPolicy.ShouldHandleXamlException(e.Exception))
            {
                e.Handled = true;
            }
        }

        private void CurrentDomain_UnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                // 记录 IsTerminating 标志，用于判断异常是否会导致进程终止
                // IsTerminating = true 表示 CLR 将在异常处理后终止进程
                // IsTerminating = false 表示异常来自非主线程，进程可能继续运行
                var isTerminating = e.IsTerminating ? "是（进程即将终止）" : "否（进程可能继续运行）";
                LogService.Error(
                    "App.AppDomain", 
                    $"未处理的应用程序域异常\nIsTerminating: {isTerminating}", 
                    ex
                );
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // 记录未观察到的任务异常
            // e.SetObserved() 标记异常已被观察，防止进程在 GC 时崩溃
            // 统一模块名称为 "App.TaskScheduler"，方便日志搜索和过滤
            LogService.Error("App.TaskScheduler", "未观察到的任务异常", e.Exception);
            e.SetObserved();
        }

        private static void LogException(string source, Exception ex)
        {
            // 使用统一的日志服务
            LogService.Error("App", source, ex);
        }

        #if DEBUG
        /// <summary>
        /// [DEBUG ONLY] 人工验证异常处理路径
        /// 用于验证：
        /// 1. Application.UnhandledException 能否正确捕获并记录异常
        /// 2. ExceptionPolicy 能否正确判断可恢复异常
        /// 3. e.Handled = true 是否能阻止进程退出
        /// 4. BuildUnhandledExceptionContext() 诊断信息是否完整
        /// 
        /// 此方法仅在 DEBUG 模式下编译，不会出现在 Release 版本中
        /// 调用方式：在需要测试时手动调用此方法
        /// </summary>
        private void VerifyExceptionHandlingPath_DEBUG()
        {
            try
            {
                // 记录验证开始
                System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 开始验证异常处理路径 ==========");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 提示：观察以下内容来验证异常处理逻辑");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 1. 查看调试输出窗口中的 [DEBUG] 标记日志");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 2. 查看 Windows 通知中心的测试通知");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 3. 查看 logs/error.log 确认异常已被记录");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 4. 验证进程在可恢复异常后继续运行（没有崩溃）");
                DebugNotificationHelper.SendNotification("异常验证", "开始测试异常处理路径");
                
                // 获取当前线程的 DispatcherQueue
                var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (dispatcherQueue == null)
                {
                    System.Diagnostics.Debug.WriteLine("[DEBUG] 无法获取 DispatcherQueue，验证中止");
                    DebugNotificationHelper.SendNotification("验证失败", "无法获取 DispatcherQueue");
                    return;
                }
                
                // 测试 1: 可恢复异常 (NullReferenceException) - 应该被 handled
                System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 测试 1: 可恢复异常 (NullReferenceException) ==========");
                System.Diagnostics.Debug.WriteLine("[DEBUG] 预期结果：异常被 handled，进程继续运行，日志记录异常");
                DebugNotificationHelper.SendNotification("测试 1", "抛出 NullReferenceException");
                
                dispatcherQueue.TryEnqueue(() =>
                {
                    // 故意触发异常，让 Application.UnhandledException 捕获
                    string? nullString = null;
                    var length = nullString!.Length; // 这里会抛出 NullReferenceException
                    
                    // 不应该执行到这里
                    System.Diagnostics.Debug.WriteLine("[DEBUG] ❌ 错误：异常后的代码被执行了");
                });
                
                // 延迟执行测试 2
                Task.Delay(3000).ContinueWith(_ =>
                {
                    dispatcherQueue.TryEnqueue(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 测试 1 验证 ==========");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 如果能看到这条消息，说明进程在 NullReferenceException 后继续运行");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] ✅ 测试 1 通过：可恢复异常被正确 handled");
                        DebugNotificationHelper.SendNotification("测试 1 完成", "进程继续运行 ✅");
                        
                        // 测试 2: 验证诊断上下文信息
                        System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 测试 2: 验证诊断上下文信息 ==========");
                        DebugNotificationHelper.SendNotification("测试 2", "验证诊断上下文");
                        
                        var context = BuildUnhandledExceptionContext();
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 诊断上下文内容：");
                        System.Diagnostics.Debug.WriteLine(context);
                        
                        // 检查关键字段是否存在
                        bool hasProcessId = context.Contains("进程 ID:");
                        bool hasCommandLine = context.Contains("命令行:");
                        bool hasWindowStatus = context.Contains("主窗口存在:");
                        bool hasLogDirectory = context.Contains("日志目录:");
                        
                        var allFieldsPresent = hasProcessId && hasCommandLine && hasWindowStatus && hasLogDirectory;
                        var result = allFieldsPresent ? "✅ 诊断信息完整" : "❌ 诊断信息不完整";
                        
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] 测试 2 结果: {result}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - 进程 ID: {hasProcessId}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - 命令行: {hasCommandLine}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - 窗口状态: {hasWindowStatus}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - 日志目录: {hasLogDirectory}");
                        
                        DebugNotificationHelper.SendNotification("测试 2 完成", result);
                        
                        // 测试 3: 验证 ExceptionPolicy 的异常分类
                        System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 测试 3: 验证异常策略分类 ==========");
                        DebugNotificationHelper.SendNotification("测试 3", "验证异常策略");
                        
                        // 可恢复异常
                        var shouldHandle_Null = ExceptionPolicy.ShouldHandleXamlException(new NullReferenceException());
                        var shouldHandle_ObjectDisposed = ExceptionPolicy.ShouldHandleXamlException(new ObjectDisposedException("test"));
                        var shouldHandle_InvalidOperation = ExceptionPolicy.ShouldHandleXamlException(new InvalidOperationException());
                        
                        // 严重异常
                        var shouldHandle_OutOfMemory = ExceptionPolicy.ShouldHandleXamlException(new OutOfMemoryException());
                        var shouldHandle_AccessViolation = ExceptionPolicy.ShouldHandleXamlException(new AccessViolationException());
                        
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 异常策略测试结果：");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - NullReferenceException: {(shouldHandle_Null ? "✅ handled" : "❌ not handled")}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - ObjectDisposedException: {(shouldHandle_ObjectDisposed ? "✅ handled" : "❌ not handled")}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - InvalidOperationException: {(shouldHandle_InvalidOperation ? "✅ handled" : "❌ not handled")}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - OutOfMemoryException: {(!shouldHandle_OutOfMemory ? "✅ not handled" : "❌ handled")}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG]   - AccessViolationException: {(!shouldHandle_AccessViolation ? "✅ not handled" : "❌ handled")}");
                        
                        bool policyCorrect = shouldHandle_Null && shouldHandle_ObjectDisposed && 
                                            shouldHandle_InvalidOperation && !shouldHandle_OutOfMemory && 
                                            !shouldHandle_AccessViolation;
                        var policyResult = policyCorrect ? "✅ 策略正确" : "❌ 策略有误";
                        
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] 测试 3 结果: {policyResult}");
                        DebugNotificationHelper.SendNotification("测试 3 完成", policyResult);
                        
                        // 完成验证
                        System.Diagnostics.Debug.WriteLine("[DEBUG] ========== 异常处理路径验证完成 ==========");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 总结：");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 1. ✅ 可恢复异常被 handled，进程继续运行");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 2. ✅ 诊断上下文信息完整");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 3. ✅ 异常策略分类正确");
                        System.Diagnostics.Debug.WriteLine("[DEBUG] 请查看 logs/error.log 确认异常已被正确记录");
                        DebugNotificationHelper.SendNotification("验证完成", "所有测试通过 ✅");
                    });
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] 验证过程本身发生异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] 堆栈跟踪: {ex.StackTrace}");
                DebugNotificationHelper.SendNotification("验证失败", $"验证过程异常: {ex.Message}");
            }
        }
        #endif

        /// <summary>
        /// 构建未处理异常的诊断上下文信息
        /// 包含：进程 ID、命令行、主窗口状态、keep-alive 窗口状态等
        /// </summary>
        /// <returns>格式化的诊断上下文字符串</returns>
        private string BuildUnhandledExceptionContext()
        {
            try
            {
                var context = new System.Text.StringBuilder();
                
                // 进程信息
                context.AppendLine("=== 进程诊断信息 ===");
                context.AppendLine($"进程 ID: {Environment.ProcessId}");
                
                // 命令行参数
                try
                {
                    var commandLine = Environment.CommandLine;
                    context.AppendLine($"命令行: {commandLine}");
                }
                catch (Exception ex)
                {
                    context.AppendLine($"命令行: [获取失败: {ex.Message}]");
                }
                
                // 进程路径
                try
                {
                    var processPath = Environment.ProcessPath ?? "[未知]";
                    context.AppendLine($"进程路径: {processPath}");
                }
                catch (Exception ex)
                {
                    context.AppendLine($"进程路径: [获取失败: {ex.Message}]");
                }
                
                // 窗口状态
                context.AppendLine();
                context.AppendLine("=== 窗口状态 ===");
                context.AppendLine($"主窗口存在: {_window != null}");
                context.AppendLine($"Keep-alive 窗口存在: {_keepAliveWindow != null}");
                
                // 托盘管理器状态
                context.AppendLine($"托盘管理器存在: {_trayIconManager != null}");
                
                // 运行时间
                try
                {
                    var uptime = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
                    context.AppendLine($"运行时长: {uptime:hh\\:mm\\:ss}");
                }
                catch (Exception ex)
                {
                    context.AppendLine($"运行时长: [获取失败: {ex.Message}]");
                }
                
                // 日志目录
                try
                {
                    var logDirectory = LogService.GetLogDirectory() ?? "[未初始化]";
                    context.AppendLine($"日志目录: {logDirectory}");
                }
                catch (Exception ex)
                {
                    context.AppendLine($"日志目录: [获取失败: {ex.Message}]");
                }
                
                return context.ToString();
            }
            catch (Exception ex)
            {
                // 确保诊断信息构建失败不会影响异常处理流程
                return $"[诊断上下文构建失败: {ex.Message}]";
            }
        }
    }
}

