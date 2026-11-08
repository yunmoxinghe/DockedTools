// 引入系统基础类型和异常处理
using System;
// 引入 DispatcherQueue / DispatcherQueueTimer：重试必须回到热键窗口所属的线程。
// ⚠️ 用别名而不是裸名：本文件同时 using 了 Windows.System，那边也有一个
//    WinRT 投影的 DispatcherQueue，裸名会撞二义性（工程内 AppTopBar 同样处理）。
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
// 引入 NHotkey 库（占用异常 HotkeyAlreadyRegisteredException 在这个命名空间）
using NHotkey;
// 引入 NHotkey 库，用于注册全局快捷键
using NHotkey.WinUI;
// 引入虚拟键修饰符枚举（Ctrl、Alt、Shift、Win）
using Windows.System;
// 引入设置页面类，用于监听快捷键设置变化
using DockedTools.Features.Pages.Settings;
// 引入统一日志服务：注册结果是要落盘取证的对象
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.Features.Hotkey
{
    /// <summary>
    /// 全局快捷键管理器
    /// 负责注册、更新和管理系统级全局快捷键
    /// 使用 NHotkey.WinUI 库实现跨应用程序的快捷键监听
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        // 快捷键在 NHotkey 管理器里的唯一标识
        private const string HotkeyName = "ShowHideWindow";

        /// <summary>
        /// 抢不到快捷键时的重试间隔（毫秒），走完一整套仍失败就放弃。
        ///
        /// 【为什么必须重试，而不是失败就算了】
        /// RegisterHotKey 的作用域是整个登录会话、不是本进程 —— 官方口径：
        /// "Typically, RegisterHotKey also fails if the keystrokes specified for the
        /// hot key have already been registered for another hot key."
        /// （https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey）
        /// 而本应用每次重启，新旧两个实例必然重叠几秒：
        ///   · 托盘重启路径：旧实例唤起新实例后还要睡 1500ms + Flush 600ms 才硬终止自己；
        ///   · 优雅退出路径：单实例 Mutex 那侧的等待上限放宽到 8 秒。
        /// 新实例的注册动作恰好落在这段窗口里，撞的是**上一个自己** → 一次失败就永久失灵，
        /// 而托盘图标因为有交接闸门不受影响 —— 表现即「重启后图标在、快捷键却有概率没反应」。
        ///
        /// 旧进程一死，它名下的热键跟着没了。官方没有「进程退出释放热键」这句原文，
        /// 但 UnregisterHotKey 的口径是按线程归属（"Frees a hot key previously registered
        /// by the calling thread"），线程随进程一起消失，热键也就无从归属；
        /// 2026-11-04 实测坐实：强制结束持有 Ctrl+Alt+Space 的那个实例之后，
        /// 新实例一次注册成功、日志里零条冲突记录。
        /// 所以「等一会儿再试」不但可行，而且是这里唯一可行的解 ——
        /// 没有任何 API 能替别的进程注销热键。
        /// 首轮 500ms 覆盖托盘路径，之后退避到 8s，总计约 15.5 秒覆盖优雅退出路径；
        /// 再拿不到就是真被别的软件占了，继续重试只会变成刷屏。
        /// </summary>
        private static readonly int[] RetryDelaysMs = { 500, 1000, 2000, 4000, 8000 };

        /// <summary>一次注册尝试的结果，决定调用方要不要排重试</summary>
        private enum RegisterResult
        {
            /// <summary>注册成功</summary>
            Registered,

            /// <summary>用户在设置里关了快捷键，属于正常状态，不必重试</summary>
            Disabled,

            /// <summary>组合键已被占用（多半是重启时撞上还没退干净的旧实例），要重试</summary>
            Conflict,

            /// <summary>其它异常（宿主窗口失效等），重试救不回来</summary>
            Failed
        }

        // 快捷键被按下时的回调函数
        private readonly Action? _hotkeyCallback;

        // 热键宿主窗口所在线程的队列。
        // 官方口径：给「别的线程创建的窗口」注册热键会直接失败，而 NHotkey 的隐藏窗口
        // 是首次触碰 HotkeyManager.Current 时（= 本类构造所在的线程）建的，
        // 所以每一次重试都必须投回这条队列，不能在线程池上裸调。
        //
        // ⚠️ 本类必须在托盘独立 UI 线程上构造（见 TrayIconManager.InitializeHotkeyManager）。
        // 原因不只是「注册要同线程」：WM_HOTKEY 的归属看的是**调用 RegisterHotKey 的那条线程**
        // —— 官方原文："The message is placed at the top of the message queue associated with
        //   the thread that registered the hot key."（WM_HOTKEY 页）。
        // 而这条线程又被 RegisterHotKey 限定成窗口的创建线程，所以两个说法在这里重合：
        // 宿主留在主线程 = 热键消息只能等主线程的消息泵，主窗口一卡死就静默失灵，
        // 而托盘还活着。这条队列因此既是线程亲和性的锚，也是存活性的锚。
        private readonly DispatcherQueue? _ownerQueue;

        // 待执行的重试计时器。必须是字段：WinRT 投影对象失去引用会被提前回收，
        // 计时器跟着消失，重试就永远不会发生。
        private DispatcherQueueTimer? _retryTimer;

        // 已经重试到第几档（对应 RetryDelaysMs 的下标）
        private int _retryAttempt;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="hotkeyCallback">快捷键被按下时的回调函数</param>
        public GlobalHotkeyManager(Action? hotkeyCallback = null)
        {
            // 保存回调函数
            _hotkeyCallback = hotkeyCallback;

            // 在创建热键宿主窗口的这条线程上捕获队列。
            // ⚠️ 调用方负责让这条构造调用落在托盘 UI 线程上（见 TrayIconManager.InitializeHotkeyManager）：
            // 本类之后所有的注册、注销、重试计时器都靠这个队列切回这里。
            _ownerQueue = DispatcherQueue.GetForCurrentThread();

            // 订阅快捷键设置变化事件，当用户修改快捷键设置时会触发
            SettingsPage.HotkeySettingsChanged += OnHotkeySettingsChanged;
        }

        /// <summary>
        /// 把动作切回热键宿主线程执行
        ///
        /// 【为什么要有这个方法】
        /// 触碰 NHotkey 的入口不止启动一处：设置页改组合键、退出流程注销都算。
        /// 而这两处的调用线程（主 UI 线程）和宿主线程不是同一条 ——
        /// 跨线程调 AddOrReplace/Remove 就是官方明令会失败的那条路径
        /// （"This function fails if you try to associate a hot key with a window
        ///   created by another thread."；UnregisterHotKey 的口径同样是按线程算：
        ///   "Frees a hot key previously registered by the calling thread."）。
        /// Timer 那条不是文档要求：DispatcherQueueTimer 是 Agile 封送，官方没写跨线程限制，
        /// 但它的 Tick 由队列所在线程触发，跟着一起收拢最省事、也最容易讲清楚。
        /// 所以这里给所有对外入口统一套一层，调用方就不必再关心自己在哪条线程。
        ///
        /// 【投递而不是等待】
        /// 主线程卡死时正是快捷键最需要还能用的时候，这里绝不能反过来等宿主线程的返回值。
        /// </summary>
        private void RunOnOwnerThread(Action action)
        {
            var queue = _ownerQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                action();
                return;
            }

            if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogService.Error("全局快捷键", "切回宿主线程后执行失败", ex);
                }
            }))
            {
                // 队列开始收摊后 TryEnqueue 才会返回 false（官方："Once ShutdownQueueAsync()
                // has been called, the queue will not queue new tasks and this method will
                // return false"）。热键归属于调用 RegisterHotKey 的那条线程，线程随进程消失
                // 就无从触发，所以这只记一条 Warning、不报错。
                LogService.Warning("全局快捷键", "热键宿主线程已不可用，本次操作未执行");
            }
        }

        /// <summary>
        /// 初始化全局快捷键
        /// 在应用程序启动时调用
        /// </summary>
        public void Initialize()
        {
            RunOnOwnerThread(() =>
            {
                _retryAttempt = 0;

                // RegisterHotkey 内部已经把失败分好类、落好日志，这里只负责决定要不要重试
                if (RegisterHotkey() == RegisterResult.Conflict)
                {
                    ScheduleRetry();
                }
            });
        }

        /// <summary>
        /// 注册或更新全局快捷键
        /// 根据用户设置注册快捷键，如果已存在则更新
        /// </summary>
        /// <returns>本次尝试的结果，由调用方决定是否重试</returns>
        private RegisterResult RegisterHotkey()
        {
            // 失败日志里要带上组合键，否则「已被占用」这句话没有主语
            string display = "";

            try
            {
                // 创建快捷键设置对象，从本地存储加载用户配置
                var settings = new HotkeySettings();
                display = settings.GetDisplayText();

                // 如果快捷键功能未启用
                if (!settings.IsEnabled)
                {
                    // 尝试移除已注册的快捷键
                    try
                    {
                        HotkeyManager.Current.Remove(HotkeyName);
                    }
                    catch { } // 忽略移除失败的异常

                    LogService.Info("全局快捷键", $"未启用（{display}），不注册");

                    // 用户刚关掉快捷键时必须作废挂着的重试，
                    // 否则到点又把它注册回来 —— 那是和用户意图正对着干。
                    StopRetry();
                    return RegisterResult.Disabled; // 直接返回，不注册快捷键
                }

                // 构建修饰键组合（初始为无修饰键）
                VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
                // 如果启用了 Ctrl 键，添加 Control 修饰符
                if (settings.Ctrl) modifiers |= VirtualKeyModifiers.Control;
                // 如果启用了 Alt 键，添加 Menu 修饰符
                if (settings.Alt) modifiers |= VirtualKeyModifiers.Menu;
                // 如果启用了 Shift 键，添加 Shift 修饰符
                if (settings.Shift) modifiers |= VirtualKeyModifiers.Shift;
                // 如果启用了 Win 键，添加 Windows 修饰符
                if (settings.Win) modifiers |= VirtualKeyModifiers.Windows;

                // 使用 NHotkey 注册全局快捷键
                // 参数：快捷键名称、按键、修饰键组合、触发时的回调函数
                HotkeyManager.Current.AddOrReplace(
                    HotkeyName,              // 快捷键的唯一标识符
                    settings.Key,            // 主按键（如 VirtualKey.Space）
                    modifiers,               // 修饰键组合（Ctrl、Alt、Shift、Win）
                    OnGlobalHotkeyPressed);  // 快捷键被按下时的回调函数

                // 输出调试信息，显示已注册的快捷键组合
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyManager] Global hotkey registered: {display}");

                // ⭐ 落 Info 而不是只写 Debug.WriteLine：
                //    「快捷键失效」原先在日志里是零证据的 —— 成功不记、失败也不记，
                //    用户说没反应时只能靠猜。这条同时也是重试链路的终点标记。
                LogService.Info("全局快捷键", $"已注册：{display}");
                return RegisterResult.Registered;
            }
            catch (HotkeyAlreadyRegisteredException ex)
            {
                // 组合键被别的进程占着（重启场景下占着的多半就是上一个自己）。
                // 这是本方法唯一「等一等就能自己好」的失败，所以单独分一类出去。
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyManager] Hotkey occupied: {ex}");
                LogService.Warning(
                    "全局快捷键",
                    $"组合键 {display} 已被其它进程占用，暂不可用；本进程会在旧持有者退出后自动重试。" +
                    "（应用重启时新旧实例必然重叠数秒，此刻注册撞上的是上一个自己）");
                return RegisterResult.Conflict;
            }
            catch (Exception ex)
            {
                // 其它失败（宿主窗口问题等）重试也救不回来，直接记错误
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkeyManager] Error registering hotkey: {ex}");
                LogService.Error("全局快捷键", $"注册快捷键失败（{display}）", ex);
                return RegisterResult.Failed;
            }
        }

        /// <summary>
        /// 排一次退避重试
        /// </summary>
        private void ScheduleRetry()
        {
            if (_retryAttempt >= RetryDelaysMs.Length)
            {
                LogService.Warning(
                    "全局快捷键",
                    $"已重试 {RetryDelaysMs.Length} 次（累计约 {TotalRetryDelaySeconds():0.0} 秒）仍没抢到组合键，停止重试。" +
                    "快捷键大概率被其它软件长期占用，请在设置里换一个组合键。");
                _retryAttempt = 0;
                return;
            }

            var queue = _ownerQueue;
            if (queue == null)
            {
                // 拿不到宿主线程队列就无从切回，硬注册会撞「窗口由别的线程创建」那条限制，
                // 宁可放弃也不留一个看起来成功的重试
                LogService.Warning("全局快捷键", "未能取得热键宿主线程的 DispatcherQueue，放弃重试");
                return;
            }

            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(RetryDelaysMs[_retryAttempt]);
            timer.IsRepeating = false;
            timer.Tick += OnRetryTick;

            _retryTimer = timer;
            timer.Start();
        }

        /// <summary>
        /// 重试计时器到点：再试一次，仍被占用就排下一档
        /// </summary>
        private void OnRetryTick(DispatcherQueueTimer sender, object args)
        {
            _retryAttempt++;
            sender.Stop();
            _retryTimer = null;

            var result = RegisterHotkey();

            if (result == RegisterResult.Conflict)
            {
                LogService.Info("全局快捷键", $"第 {_retryAttempt} 次重试仍被占用，继续退避");
                ScheduleRetry();
            }
            else if (result == RegisterResult.Registered)
            {
                LogService.Info("全局快捷键", $"第 {_retryAttempt} 次重试成功，快捷键恢复");
                _retryAttempt = 0;
            }
            // Disabled / Failed 两条分支各自已经落过日志，这里不重复刷屏
        }

        /// <summary>
        /// 作废待执行的重试（注册成功、被关闭、被释放、用户改了设置都要停）
        /// </summary>
        private void StopRetry()
        {
            var timer = _retryTimer;
            _retryTimer = null;

            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= OnRetryTick;
            }
        }

        /// <summary>重试各档间隔的总时长（秒），只用于日志文案</summary>
        private static double TotalRetryDelaySeconds()
        {
            int total = 0;
            foreach (var ms in RetryDelaysMs)
            {
                total += ms;
            }
            return total / 1000d;
        }

        /// <summary>
        /// 快捷键设置变化时的事件处理函数
        /// 当用户在设置页面修改快捷键配置时触发
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="e">事件参数</param>
        private void OnHotkeySettingsChanged(object? sender, EventArgs e)
        {
            // 这个静态事件由设置页（主 UI 线程）抛出，而宿主现在在托盘线程上，
            // 直接在这里调 RegisterHotkey 会撞上「窗口由别的线程创建」那条限制。
            RunOnOwnerThread(() =>
            {
                // 用户改的是**新**组合键，旧的等待必须作废：
                // 否则 pending 的那次重试可能把刚被放弃的组合键又抢回来。
                StopRetry();
                _retryAttempt = 0;

                // 重新注册快捷键，应用新的设置
                if (RegisterHotkey() == RegisterResult.Conflict)
                {
                    ScheduleRetry();
                }
            });
        }

        /// <summary>
        /// 全局快捷键被按下时的回调函数
        /// 由 NHotkey 库在检测到快捷键按下时调用
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="e">快捷键事件参数</param>
        private void OnGlobalHotkeyPressed(object? sender, NHotkey.HotkeyEventArgs e)
        {
            // 输出调试信息
            System.Diagnostics.Debug.WriteLine("[GlobalHotkeyManager] Global hotkey pressed!");
            // 标记事件已处理，防止其他程序接收此快捷键
            e.Handled = true;
            // 调用回调函数（通常是显示/隐藏主窗口）
            _hotkeyCallback?.Invoke();
        }

        /// <summary>
        /// 释放资源（实现 IDisposable 接口）
        /// 在对象被垃圾回收前调用，确保资源正确释放
        /// </summary>
        public void Dispose()
        {
            // 取消订阅快捷键设置变化事件
            SettingsPage.HotkeySettingsChanged -= OnHotkeySettingsChanged;

            RunOnOwnerThread(() =>
            {
                // 先作废重试、再注销：退出流程里晚一步到点的重试会把热键又注册回去
                StopRetry();

                // 移除全局快捷键
                try
                {
                    HotkeyManager.Current.Remove(HotkeyName);
                }
                catch { } // 忽略移除失败的异常
            });
        }
    }
}

