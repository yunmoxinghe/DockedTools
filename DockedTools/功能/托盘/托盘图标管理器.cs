// 引入系统基础类型和异常处理
using System;
// 引入文件路径操作
using System.IO;
// 引入运行时互操作
using System.Runtime.InteropServices;
// SystemTrayIcon 在同一命名空间 DockedTools.Features.Tray 下，无需额外 using
// 引入 WinUI 窗口类型
using Microsoft.UI.Xaml;
// 引入 WinUI 控件（菜单、图标等）
using Microsoft.UI.Xaml.Controls;
// 引入本地化辅助类，用于多语言支持
using DockedTools.Features.Localization;
// 引入全局快捷键管理器
using DockedTools.Features.Hotkey;
// 引入主窗口工厂类
using DockedTools.Features.MainWindow.Entry;
// 引入统一托盘菜单服务
using DockedTools.功能.统一调用.托盘右键菜单;

namespace DockedTools.Features.Tray
{
    /// <summary>
    /// 托盘图标管理器类
    /// 负责管理系统托盘图标、右键菜单和主窗口的显示/隐藏
    /// 实现 IDisposable 接口以正确释放资源
    /// </summary>
    public class TrayIconManager : IDisposable
    {
        // 托盘图标的唯一标识符
        private const uint TrayIconId = 123;

        // 托盘悬停提示的基础文本（构建时可能会追加 worktree 身份后缀）
        private const string TrayTooltipBase = "DockedTools";

        // 系统托盘图标对象，可为空
        private SystemTrayIcon? _trayIcon;
        // 主窗口引用，可为空
        private Window? _mainWindow;
        // 退出应用程序时的回调函数，可为空
        private readonly Action? _exitAction;
        // 全局快捷键管理器，负责处理快捷键注册和监听
        private readonly GlobalHotkeyManager? _hotkeyManager;
        // 缓存的托盘菜单（鼠标模式），避免每次右键都重新创建
        private MenuFlyout? _mouseMenu;
        // 缓存的托盘菜单（触摸模式），避免每次右键都重新创建
        private MenuFlyout? _touchMenu;
        // 窗口工厂方法，用于创建自定义窗口（支持扩展）
        private readonly Func<Window>? _windowFactory;
        // 标记是否已初始化，防止重复初始化
        private bool _initialized;
        // 标记是否已释放资源，防止重复释放
        private bool _isDisposed;
        // 托盘独立 UI 线程宿主（为 null 表示当前跑在主线程降级模式下）
        private TrayUIThreadHost? _trayHost;
        // 主 UI 线程的 DispatcherQueue，用于把菜单点击切回主线程
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _mainDispatcher;

        /// <summary>
        /// 托盘是否运行在独立 UI 线程上（false 表示已降级到主线程，卡死时会跟着一起死）
        /// </summary>
        public bool IsRunningOnIndependentThread => _trayHost != null;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="initialMainWindow">初始主窗口引用</param>
        /// <param name="exitAction">退出应用程序时的回调函数</param>
        /// <param name="windowFactory">窗口工厂方法，用于创建自定义窗口（可选）</param>
        public TrayIconManager(Window? initialMainWindow, Action? exitAction = null, Func<Window>? windowFactory = null)
        {
            // 保存主窗口引用
            _mainWindow = initialMainWindow;
            // 保存退出回调函数
            _exitAction = exitAction;
            // 保存窗口工厂方法
            _windowFactory = windowFactory;

            // 缓存主线程 DispatcherQueue：托盘线程的菜单点击要切回这里操作主窗口
            _mainDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // 创建全局快捷键管理器，使用 lambda 避免方法引用绑定实例
            // 热键回调不一定落在主线程，所以统一走 RunOnMainThread
            _hotkeyManager = new GlobalHotkeyManager(() => RunOnMainThread(ShowMainWindow));
        }

        /// <summary>
        /// 把动作切回主 UI 线程执行
        ///
        /// 托盘图标与菜单跑在独立 UI 线程上，它们的事件回调也在那个线程触发，
        /// 但创建 / 显示 / 关闭主窗口只能在主线程做。
        /// 注意这里是投递而非等待：主线程卡死时调用方不会被拖住，托盘依然可点。
        /// </summary>
        private void RunOnMainThread(Action action)
        {
            var dispatcher = _mainDispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayIconManager] ERROR running action on main thread: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 初始化托盘图标和全局快捷键
        /// </summary>
        public void Initialize()
        {
            // 防止重复初始化
            if (_initialized)
            {
                System.Diagnostics.Debug.WriteLine("[TrayIconManager] Already initialized, skipping.");
                return;
            }
            _initialized = true;

            // 构建图标文件的完整路径（应用程序目录/Assets/Sparkles.ico）
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sparkles.ico");
            
            // 检查图标文件是否存在
            if (!File.Exists(iconPath))
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Tray icon not found at: {iconPath}");
                throw new FileNotFoundException("Tray icon not found", iconPath);
            }

            // 🎯 托盘宿主窗口 + 图标 + 菜单整体搬到独立 UI 线程：
            // 主窗口卡死时主线程消息泵停摆，托盘消息送不进来，菜单就跟着一起死。
            // 搬到独立线程后，主线程卡死时托盘仍然能收消息、能弹菜单、能点退出。
            var tooltip = BuildTrayTooltip();
            System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Tray tooltip: {tooltip}");

            try
            {
                _trayHost = new TrayUIThreadHost();
                _trayHost.Start(() => InitializeTrayOnOwnThread(iconPath, tooltip));
                System.Diagnostics.Debug.WriteLine("[TrayIconManager] Tray icon initialized on independent UI thread.");
            }
            catch (Exception ex)
            {
                // 独立线程方案在这个环境不可用（XAML 多线程初始化失败）时退回主线程。
                // 宁可保留"卡死时一起死"的旧行为，也不能让应用没有托盘图标。
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] WARNING: independent tray thread unavailable ({ex.Message}), falling back to main thread");
                _trayHost?.Dispose();
                _trayHost = null;
                InitializeTrayOnMainThread(iconPath, tooltip);
            }

            // 订阅托盘评价按钮设置变化事件
            DockedTools.Features.Pages.Lab.LabPage.HideTrayRateButtonSettingsChanged += OnHideTrayRateButtonSettingsChanged;

            // 初始化全局快捷键（在托盘图标创建之后）
            try
            {
                _hotkeyManager?.Initialize();
                System.Diagnostics.Debug.WriteLine("[TrayIconManager] Global hotkey initialized successfully.");
            }
            catch (Exception ex)
            {
                // 热键注册失败不应该阻止托盘初始化
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Failed to initialize global hotkey: {ex.Message}");
                // TODO: 未来可以在这里显示通知给用户
            }
        }

        /// <summary>
        /// 构建托盘悬停提示文本
        /// 多 worktree 并行调试时（Debug 配置 + 仓库目录名含 hash）会追加身份后缀，
        /// 例如 "DockedTools [Debug.WT3C53]"，便于在多个托盘图标之间区分实例。
        /// Release 或单 worktree 下 WorktreeIdentity.Suffix 为空，文本保持 "DockedTools"。
        /// </summary>
        /// <returns>托盘悬停提示文本</returns>
        private static string BuildTrayTooltip()
        {
            // 后缀由构建时生成（功能/应用入口/WorktreeIdentity.g.cs），Debug 下形如 ".WT3C53"
            var suffix = global::WorktreeIdentity.Suffix;
            return string.IsNullOrEmpty(suffix)
                ? TrayTooltipBase
                : $"{TrayTooltipBase} [Debug{suffix}]";
        }

        /// <summary>
        /// 在托盘独立 UI 线程上完成托盘图标与菜单的创建
        /// （由 TrayUIThreadHost 在该线程上回调，不能从其它线程调用）
        /// </summary>
        private void InitializeTrayOnOwnThread(string iconPath, string tooltip)
        {
            _trayIcon = new SystemTrayIcon(TrayIconId, iconPath, tooltip);
            _trayIcon.LeftClick += TrayIcon_LeftClick;
            _trayIcon.RightClick += TrayIcon_RightClick;
            _trayIcon.IsVisible = true;

            // 菜单也必须在这个线程上创建：XAML 对象有线程亲和性，
            // 把主线程造出来的 MenuFlyout 拿到这里 ShowAt 会直接崩。
            // 三个回调都会回到托盘线程，所以统一包一层切回主线程。
            _mouseMenu = TrayContextMenuService.CreateMouseTrayMenu(
                onOpenWindow: () => RunOnMainThread(ShowMainWindow),
                onCloseWindow: () => RunOnMainThread(CloseMainWindow),
                onExit: () => RunOnMainThread(ExitApplication));

            _touchMenu = TrayContextMenuService.CreateTouchTrayMenu(
                onOpenWindow: () => RunOnMainThread(ShowMainWindow),
                onCloseWindow: () => RunOnMainThread(CloseMainWindow),
                onExit: () => RunOnMainThread(ExitApplication));
        }

        /// <summary>
        /// 降级路径：在主线程上创建托盘图标（独立线程不可用时的兜底）
        /// </summary>
        private void InitializeTrayOnMainThread(string iconPath, string tooltip)
        {
            _trayIcon = new SystemTrayIcon(TrayIconId, iconPath, tooltip);
            _trayIcon.LeftClick += TrayIcon_LeftClick;
            _trayIcon.RightClick += TrayIcon_RightClick;
            _trayIcon.IsVisible = true;

            System.Diagnostics.Debug.WriteLine("[TrayIconManager] Tray icon initialized on main thread (fallback).");
        }

        private void TrayIcon_LeftClick(SystemTrayIcon sender, SystemTrayIconEventArgs args)
        {
            // 事件在托盘线程上触发，显示主窗口必须切回主线程
            RunOnMainThread(ShowMainWindow);
        }

        /// <summary>
        /// 托盘图标右键点击事件处理函数
        /// </summary>
        /// <param name="sender">托盘图标对象</param>
        /// <param name="args">事件参数</param>
        private void TrayIcon_RightClick(SystemTrayIcon sender, SystemTrayIconEventArgs args)
        {
            // 🔍 根据输入设备类型选择合适的菜单
            bool isTouch = args.InputDevice == InputDeviceType.Touch || args.InputDevice == InputDeviceType.Pen;
            args.Flyout = isTouch ? GetOrCreateTouchMenu() : GetOrCreateMouseMenu();
            
            System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Right click detected, InputDevice: {args.InputDevice}, Using {(isTouch ? "Touch" : "Mouse")} menu");
        }

        /// <summary>
        /// 获取或创建鼠标模式菜单（紧凑间距）
        /// </summary>
        private MenuFlyout GetOrCreateMouseMenu()
        {
            if (_mouseMenu != null)
            {
                return _mouseMenu;
            }

            _mouseMenu = TrayContextMenuService.CreateMouseTrayMenu(
                onOpenWindow: ShowMainWindow,
                onCloseWindow: CloseMainWindow,
                onExit: ExitApplication
            );

            return _mouseMenu;
        }

        /// <summary>
        /// 获取或创建触摸模式菜单（大间距）
        /// </summary>
        private MenuFlyout GetOrCreateTouchMenu()
        {
            if (_touchMenu != null)
            {
                return _touchMenu;
            }

            _touchMenu = TrayContextMenuService.CreateTouchTrayMenu(
                onOpenWindow: ShowMainWindow,
                onCloseWindow: CloseMainWindow,
                onExit: ExitApplication
            );

            return _touchMenu;
        }

        /// <summary>
        /// 清空托盘菜单缓存（用于语言切换等场景）
        /// </summary>
        public void RefreshTrayMenu()
        {
            void ClearMenuCache()
            {
                // 清理鼠标菜单
                _mouseMenu?.Items.Clear();
                _mouseMenu = null;

                // 清理触摸菜单
                _touchMenu?.Items.Clear();
                _touchMenu = null;
            }

            if (_trayHost != null)
            {
                // 菜单是托盘线程上的 XAML 对象，清理和重建都必须回到那个线程
                _trayHost.TryEnqueue(ClearMenuCache);
            }
            else
            {
                RunOnMainThread(ClearMenuCache);
            }
        }

        /// <summary>
        /// 处理托盘评价按钮设置变化
        /// </summary>
        private void OnHideTrayRateButtonSettingsChanged(object? sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconManager] Hide tray rate button setting changed, refreshing menu...");
            RefreshTrayMenu();
        }

        /// <summary>
        /// 显示主窗口
        /// 如果窗口不存在或已关闭，则创建新窗口
        /// 如果窗口已存在，则切换显示/隐藏状态
        /// </summary>
        public void ShowMainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconManager] ShowMainWindow called");
            
            try
            {
                bool isValid = IsWindowValid();
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Window valid check: {isValid}");
                
                if (!isValid)
                {
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Creating and showing new window");
                    CreateAndShowWindow();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Toggling existing window");
                    ToggleExistingWindow();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] ERROR showing main window: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Stack trace: {ex.StackTrace}");
                
                // 发生异常时尝试创建新窗口
                try
                {
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Attempting to create window after error");
                    CreateAndShowWindow();
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayIconManager] CRITICAL ERROR: Failed to create window: {ex2.Message}");
                }
            }
        }

        /// <summary>
        /// 检查窗口是否有效
        /// </summary>
        /// <returns>窗口是否有效</returns>
        private bool IsWindowValid()
        {
            bool isValid = MainWindowFactory.IsWindowValid(_mainWindow);
            if (!isValid)
            {
                _mainWindow = null;
            }
            return isValid;
        }

        /// <summary>
        /// 创建并显示新窗口
        /// </summary>
        private void CreateAndShowWindow()
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconManager] CreateAndShowWindow started");
            
            try
            {
                // 使用窗口工厂创建窗口（如果提供），否则使用主窗口工厂创建默认窗口
                _mainWindow = _windowFactory?.Invoke() ?? MainWindowFactory.Create();
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Window created: {_mainWindow != null}");
                
                if (_mainWindow == null)
                {
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] CRITICAL ERROR: Failed to create window instance");
                    return;
                }
                
                // 标记初始化完成并显示窗口
                if (_mainWindow is IWindowToggle windowToggle)
                {
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Window implements IWindowToggle, calling SetInitializingComplete");
                    windowToggle.SetInitializingComplete();
                    
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Initialization complete, requesting first show");
                    
                    // 触发首次显示，利用 DWM 的创建动画 ✨
                    // 注意：ShowSplash() 现在在 RequestSlideIn() 内部调用，确保在窗口激活后立即显示
                    windowToggle.RequestSlideIn();
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] RequestSlideIn called");
                }
                else
                {
                    // 降级处理：如果窗口不支持 IWindowToggle，直接激活
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] WARNING: Window does not implement IWindowToggle, using fallback activation");
                    
                    // 注意：Activate() 的行为特性：
                    // - 这是首次创建窗口时唯一合法的显示方案
                    // - 会触发系统内置的流畅窗口显示动画（DWM 动画）
                    // - 内置了强制进入可显示区域的逻辑
                    // - 必须在所有窗口配置（位置、大小、样式等）完成后最后调用
                    // - 如果在配置过程中调用会导致闪现问题
                    _mainWindow.Activate();
                    WindowHelper.SetForegroundWindow(_mainWindow);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] CRITICAL ERROR in CreateAndShowWindow: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 切换现有窗口的显示状态
        /// </summary>
        private void ToggleExistingWindow()
        {
            var mainWindow = _mainWindow;
            if (mainWindow == null)
            {
                return;
            }

            // 使用接口解耦，支持多种窗口类型（插件窗口、浮动窗口等）
            if (mainWindow is IWindowToggle toggleWindow)
            {
                toggleWindow.ToggleWindow();

                // 检查窗口是否可见（非隐藏且非未创建状态）
                bool isVisible = toggleWindow.CurrentWindowState != DockedTools.Features.MainWindow.State.WindowState.Hidden &&
                                toggleWindow.CurrentWindowState != DockedTools.Features.MainWindow.State.WindowState.NotCreated;

                if (isVisible)
                {
                    WindowHelper.SetForegroundWindow(mainWindow);
                }
            }
            else
            {
                // 降级处理：如果窗口不支持 IWindowToggle，直接激活窗口
                
                // 注意：Activate() 的行为特性：
                // - 这是首次创建窗口时唯一合法的显示方案
                // - 会触发系统内置的流畅窗口显示动画（DWM 动画）
                // - 内置了强制进入可显示区域的逻辑
                // - 必须在所有窗口配置（位置、大小、样式等）完成后最后调用
                // - 如果在配置过程中调用会导致闪现问题
                mainWindow.Activate();
                WindowHelper.SetForegroundWindow(mainWindow);
            }
        }

        /// <summary>
        /// 关闭主窗口以释放内存，保留托盘图标
        /// 下次点击托盘图标时会重新创建窗口
        /// 根据用户设置，可能会直接销毁窗口或重启到仅托盘
        /// </summary>
        public void CloseMainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconManager] CloseMainWindow called");

            try
            {
                // 读取用户设置的行为
                var behavior = DockedTools.Features.Pages.Settings.ExperimentalSettings.CloseWindowBehavior;
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Close window behavior: {behavior}");

                if (behavior == DockedTools.Features.Pages.Settings.TrayCloseWindowBehavior.RestartToTrayOnly)
                {
                    // 重启到仅托盘
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Restarting to tray only...");
                    DockedTools.功能.统一调用.AppRestartService.RestartWithArgs("--restart", "--tray-only");
                }
                else
                {
                    // 直接销毁窗口（默认行为）
                    if (IsWindowValid())
                    {
                        _mainWindow!.Close();
                        _mainWindow = null;
                        System.Diagnostics.Debug.WriteLine("[TrayIconManager] Main window closed and released");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayIconManager] CloseMainWindow: window already invalid, nothing to do");
                    }

                    // 任务 5.4: 主窗口关闭后检查并恢复 keep-alive 窗口
                    // 确保托盘模式下进程不会因窗口全部关闭而退出
                    System.Diagnostics.Debug.WriteLine("[TrayIconManager] Checking keep-alive window after main window closed");
                    try
                    {
                        // 获取 App 实例并检查 keep-alive 窗口
                        var app = Application.Current as DockedTools.App;
                        if (app != null)
                        {
                            app.CheckAndRecoverKeepAliveWindow();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[TrayIconManager] WARNING: Cannot check keep-alive window - App instance not available");
                        }
                    }
                    catch (Exception keepAliveEx)
                    {
                        // Keep-alive 检查失败不应该影响主窗口关闭流程
                        System.Diagnostics.Debug.WriteLine($"[TrayIconManager] ERROR checking keep-alive window: {keepAliveEx.Message}");
                        System.Diagnostics.Debug.WriteLine($"[TrayIconManager] Stack trace: {keepAliveEx.StackTrace}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayIconManager] ERROR closing main window: {ex.Message}");
                _mainWindow = null;
            }
        }

        /// <summary>
        /// 退出应用程序
        /// 清理所有资源并调用退出回调
        /// </summary>
        public void ExitApplication()
        {
            // 先通知外部（外部可能需要访问托盘/热键/窗口状态）
            _exitAction?.Invoke();
            // 再销毁内部资源
            Dispose();
        }

        /// <summary>
        /// 释放资源（实现 IDisposable 接口）
        /// 在对象被垃圾回收前调用，确保资源正确释放
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 标准 Dispose 模式实现
        /// 支持托管资源和非托管资源的分别释放
        /// </summary>
        /// <param name="disposing">是否正在释放托管资源</param>
        protected virtual void Dispose(bool disposing)
        {
            // 防止重复释放资源
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;

            System.Diagnostics.Debug.WriteLine("[TrayIconManager] Disposing resources...");

            // 释放托管资源
            if (disposing)
            {
                // 取消订阅托盘评价按钮设置变化事件
                DockedTools.Features.Pages.Lab.LabPage.HideTrayRateButtonSettingsChanged -= OnHideTrayRateButtonSettingsChanged;

                // 释放快捷键管理器资源
                _hotkeyManager?.Dispose();

                // 注意：菜单缓存不在这里清。
                // 独立线程模式下菜单属于托盘线程，跨线程清 XAML 集合会崩，
                // 统一交给下面的托盘线程清理分支处理。

                // 注意：不重置 _initialized，防止对象复活导致状态不一致
                // 如果需要复活功能，应该提供专门的 ReInitialize() 方法
            }

            // 释放非托管资源（托盘图标涉及系统资源）
            var icon = _trayIcon;
            _trayIcon = null;

            if (icon != null)
            {
                // 取消订阅左键点击事件
                icon.LeftClick -= TrayIcon_LeftClick;
                // 取消订阅右键点击事件
                icon.RightClick -= TrayIcon_RightClick;

                if (_trayHost != null)
                {
                    // 宿主窗口和菜单都是托盘线程的资源，销毁要回到那个线程。
                    // TryEnqueue 是 FIFO，这条排在下面的退出指令之前，能保证先跑到。
                    _trayHost.TryEnqueue(() =>
                    {
                        _mouseMenu?.Items.Clear();
                        _mouseMenu = null;

                        _touchMenu?.Items.Clear();
                        _touchMenu = null;

                        icon.IsVisible = false;
                        icon.Dispose();
                    });
                }
                else
                {
                    // 主线程模式下菜单本来就属于这个线程，直接清
                    _mouseMenu?.Items.Clear();
                    _mouseMenu = null;

                    _touchMenu?.Items.Clear();
                    _touchMenu = null;

                    // 隐藏托盘图标
                    icon.IsVisible = false;
                    // 释放托盘图标资源
                    icon.Dispose();
                }
            }

            if (_trayHost != null)
            {
                _trayHost.Shutdown();
                _trayHost = null;
            }

            System.Diagnostics.Debug.WriteLine("[TrayIconManager] Resources disposed successfully.");
        }
    }
}