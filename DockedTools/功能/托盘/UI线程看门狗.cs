using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using DockedTools.Features.AppEntry;
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.Features.Tray
{
    /// <summary>
    /// UI 线程看门狗 - 监控主 UI 线程健康状态
    /// 
    /// 【核心功能】
    /// 1. 定期探测主 UI 线程还在不在处理窗口消息
    /// 2. 连续多次探测无响应时判定为卡死
    /// 3. 卡死时不询问用户，静默重启到托盘（保留托盘图标，不弹主窗口）
    /// 4. 短时间内反复卡死则停在通知告警上，避免重启风暴
    /// 
    /// 【设计原理】
    /// - 看门狗运行在独立的后台线程上
    /// - 用 Win32 SendMessageTimeout 探测（不用 DispatcherQueue.TryEnqueue，
    ///   原因见 ProbeUiThread 注释）
    /// - 告知用户的通知由 PowerShell 独立进程投递（不受卡死进程影响）
    /// </summary>
    public partial class UIThreadWatchdog : IDisposable
    {
        // ⭐ 可变而非 readonly：探测目标会随 keep-alive 窗口的重建而换 HWND，
        //    见 Retarget()。锁在启动那一刻的句柄，是"退出/自愈被误判成卡死"的根源之一。
        private IntPtr _mainWindowHandle;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _timeout;

        // 探测用的私有消息（WM_APP 区间，不会和系统消息撞车）
        private const uint WM_UI_HEARTBEAT = 0x8000 + 200;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        // 单次探测的等待上限：线程只要还在泵消息，微秒级就会返回
        private const uint ProbeTimeoutMs = 1500;
        // 系统正在结束当前会话（关机 / 重启 / 注销）时为真。
        // SM_SHUTTINGDOWN = 0x2000，官方定义"Nonzero if the current session is shutting down"。
        // 这是唯一能在不挂钩子的前提下、从任意线程廉价问出"系统要不要收了这台会话"的方法。
        private const int SM_SHUTTINGDOWN = 0x2000;
        // 探测目标失效（HWND 已销毁）时的告警节流，避免每 2 秒刷一条同样的 Warning
        private static readonly TimeSpan InvalidTargetWarningInterval = TimeSpan.FromSeconds(60);
        private CancellationTokenSource? _cts;
        private Task? _watchdogTask;
        private bool _disposed;
        private DateTime _lastHeartbeat;
        private readonly object _heartbeatLock = new object();

        // Stop() 已发出、但循环可能还停在一次探测里（最长 ProbeTimeoutMs）。
        // 处置动作前必须再看一眼这个标志：否则"正在退出"会被判成"卡死"，
        // 用户点退出反而被重启回托盘。
        private volatile bool _shutdownRequested;

        // 本轮抑制判定的原因只记一次，别每 2 秒刷日志。
        private bool _suppressionLogged;
        private DateTime _lastInvalidTargetWarning = DateTime.MinValue;

        // 进程级的"外部要求本进程让路"抑制：存的是**抑制截止时刻**（UTC Ticks，0 = 无）。
        // 由托盘线程在 WM_QUERYENDSESSION 里按 lParam 分流置位 —— 那个消息比 SM_SHUTTINGDOWN
        // 更早、更确定：系统已经明确在问"你能不能走"，此刻任何自动重启都是往枪口上撞。
        // 用静态 + long 是为了让看门狗不必依赖 App/窗口的对象图（托盘和它隔着一个线程），
        // 同时保证跨线程读写是原子的。
        private static long _suppressUntilTicks;

        // 抑制原因（仅用于日志）。字符串引用的读写在 CLR 下是原子的，
        // 且这里只用来打日志，读到旧值无害。
        private static string? _suppressNote;

        // 两类让路的有效期。WM_QUERYENDSESSION 之后**没有任何消息**会告诉我们"这事取消了"
        // （用户或其它应用否决关机时，系统根本不发 WM_ENDSESSION，本进程继续活着），
        // 所以抑制绝不能永久生效 —— 永久抑制等于看门狗被一次取消的关机彻底废掉。
        //
        // 会话结束（关机/重启/注销）给 120 秒：会话拆除本身就几十秒量级，
        //   这段时间里 UI 线程可能一直在被系统收尸，探测必然超时。
        // 包替换 / servicing（ENDSESSION_CLOSEAPP）给 30 秒：这种场合系统要的是
        //   "把文件腾出来"，我们的进程随后会被终止；万一替换失败或被取消，
        //   30 秒后恢复判定，不至于把自愈能力关掉两分钟。
        private static readonly TimeSpan SessionEndSuppressWindow = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan AppReplacementSuppressWindow = TimeSpan.FromSeconds(30);

        // 一次卡死只处置一次的闸门。
        // 看门狗每 2 秒一轮，不节流的话会连发通知 + 弹出一堆对话框（用户关不完）。
        // UI 线程恢复后由 WatchdogLoop 复位，下次再卡死仍会处置。
        private int _recoveryLaunched;

        // 启动宽限期：冷启动期间要建窗口、加载 XAML、初始化页面，主线程忙上好几秒是常态。
        // 实测启动阶段会出现 5 秒以上无心跳，直接判定会每次冷启动都误报并弹对话框。
        private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(20);

        // 连续多少轮没心跳才真的判定卡死。
        // 单轮（约 7 秒）可能只是一次长任务或 GC 停顿；连续两轮（约 12 秒）
        // 才对得上用户感知里的"程序未响应"。
        private const int FrozenStreakThreshold = 2;

        private DateTime _graceUntil;
        private int _frozenStreak;

        /// <summary>
        /// UI 线程卡死事件
        /// </summary>
        public event EventHandler<UIThreadFrozenEventArgs>? UIThreadFrozen;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="mainWindowHandle">主 UI 线程上的窗口句柄（探测目标）</param>
        /// <param name="checkInterval">检测间隔（默认 2 秒）</param>
        /// <param name="timeout">判定为可疑的最短时间（默认 5 秒）</param>
        public UIThreadWatchdog(
            IntPtr mainWindowHandle,
            TimeSpan? checkInterval = null,
            TimeSpan? timeout = null)
        {
            _mainWindowHandle = mainWindowHandle;
            _checkInterval = checkInterval ?? TimeSpan.FromSeconds(2);
            _timeout = timeout ?? TimeSpan.FromSeconds(5);
            _lastHeartbeat = DateTime.UtcNow;
        }

        /// <summary>
        /// 换绑探测目标 HWND。
        ///
        /// 【为什么必须有这个口子】
        /// keep-alive 窗口一旦被关掉又重建（自愈路径），新的 XAML 窗口是另一个 HWND。
        /// SendMessageTimeout 打到已销毁的句柄上只会返回 0，和"线程真的冻住"在返回值
        /// 上完全一样 —— 不换绑的话，宽限期一过必然误判卡死并自动重启。
        /// 由 UI 线程在窗口创建成功后调用（WinUI 对象的句柄只该在 UI 线程上取）。
        /// </summary>
        public void Retarget(IntPtr newMainWindowHandle)
        {
            if (newMainWindowHandle == IntPtr.Zero || newMainWindowHandle == _mainWindowHandle)
            {
                return;
            }

            var previous = _mainWindowHandle;
            _mainWindowHandle = newMainWindowHandle;

            // 换绑等于重新拿到一个健康的探测目标，之前的"无响应" streak 全部作废
            lock (_heartbeatLock)
            {
                _lastHeartbeat = DateTime.UtcNow;
                _frozenStreak = 0;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[UIThreadWatchdog] Probe target retargeted: 0x{previous.ToInt64():X} -> 0x{newMainWindowHandle.ToInt64():X}");
        }

        /// <summary>
        /// 是否有"外部要求本进程让路"的抑制仍在有效期内（会话结束 / 包替换 servicing）。
        /// 到期自动失效，避免一次被取消的关机或被回退的安装把看门狗永久废掉。
        /// </summary>
        public static bool IsExternalShutdownPending
        {
            get
            {
                var untilTicks = Volatile.Read(ref _suppressUntilTicks);
                return untilTicks != 0 && DateTime.UtcNow.Ticks <= untilTicks;
            }
        }

        /// <summary>
        /// 声明"会话正在结束"（关机 / 重启 / 注销）：停止卡死判定与自动重启。
        /// </summary>
        public static void SuppressForSessionEnd(string reason)
            => SuppressFor(SessionEndSuppressWindow, "会话结束", reason);

        /// <summary>
        /// 声明"本进程正在被要求让路以替换文件 / 系统 servicing"
        /// （WM_QUERYENDSESSION 的 lParam 带 ENDSESSION_CLOSEAPP）。
        ///
        /// 【为什么要和真关机分开】
        /// 官方对这条位的定义就是"应用占用的文件需要被替换、系统正在被 servicing、
        /// 或系统资源耗尽"—— 它不是关机，是安装/服务流程在腾文件。日志必须能区分这两种，
        /// 否则事后看到"会话结束"会误以为是用户关机，把一次失败的更新盖过去。
        /// 更要紧的是：这种场合**绝不能拉起新实例** —— 自己复活等于又把刚被松开
        /// 的文件占用住，让那次更新原地再失败一次。
        /// </summary>
        public static void SuppressForAppReplacement(string reason)
            => SuppressFor(AppReplacementSuppressWindow, "包替换/servicing", reason);

        /// <summary>
        /// 置/续抑制窗口（取更晚的截止时刻，重复通知只会往后延，不会提前放开）。
        /// </summary>
        private static void SuppressFor(TimeSpan window, string note, string reason)
        {
            var until = DateTime.UtcNow + window;

            // 是否"新起一段抑制"要在 CAS 之前一次定死，不然并发重试会把同一段
            // 抑制重复记成两条日志。
            var current = Volatile.Read(ref _suppressUntilTicks);
            var newlySuppressed = current == 0 || DateTime.UtcNow.Ticks > current;

            while (true)
            {
                // 取更晚的截止时刻：只往后延，绝不因为一次新的通知把窗口收短
                var target = Math.Max(current, until.Ticks);
                var previous = Interlocked.CompareExchange(ref _suppressUntilTicks, target, current);

                if (previous == current)
                {
                    break;
                }

                current = previous;
            }

            _suppressNote = note;

            if (!newlySuppressed)
            {
                return;
            }

            try
            {
                LogService.Info("看门狗.停止判定", $"外部要求本进程让路（{note}），停止卡死判定与自动重启：{reason}");
            }
            catch
            {
                // 会话正在拆除，日志写不下去也不能把异常抛回 native 回调
            }
        }

        /// <summary>
        /// 当前是否处于"不该做任何卡死判定"的状态。
        ///
        /// 【为什么这四条要一起看】
        /// ① 外部让路通知（会话结束 / 包替换 servicing）：系统已经在问我们要不要走，
        ///    此时任何处置都是添乱。
        /// ② SM_SHUTTINGDOWN：不依赖任何钩子的兜底 —— 我们没给 XAML 窗口挂
        ///    WM_QUERYENDSESSION 处理，注销/关机时这条是唯一能主动问出来的信号。
        /// ③ 本进程正在退出：ExitApplication 会先把狗停掉，但停的那一刻循环
        ///    可能正卡在一次探测里，处置前必须再确认一次。
        /// ④ 已被 Dispose。
        /// 关机时 UI 线程被 WM_QUERYENDSESSION / WM_ENDSESSION 的处理占住，
        /// 天然不泵消息，探测必然超时；不拦的话"关机"这件事本身就会被当成卡死，
        /// 于是狗在会话拆除阶段拉起一个新实例 —— 轻则注销变慢、留个僵尸进程挨强杀，
        /// 重则被系统列进"阻止关机"名单，还会把重启冷却戳记白白污染一轮。
        /// </summary>
        /// <param name="reason">抑制原因，用于日志</param>
        /// <param name="stopLoop">true = 本进程自己要没了，循环直接退出；
        /// false = 只是暂时不能判定（会话结束），空转等下一轮，关机被取消时能自动恢复</param>
        private bool ShouldSkipJudging(out string reason, out bool stopLoop)
        {
            stopLoop = false;

            if (IsExternalShutdownPending)
            {
                var note = _suppressNote;
                reason = string.IsNullOrEmpty(note)
                    ? "外部要求本进程让路（已收到 WM_QUERYENDSESSION）"
                    : $"外部要求本进程让路（{note}，已收到 WM_QUERYENDSESSION）";
                return true;
            }

            if (_shutdownRequested || _disposed)
            {
                reason = "本进程正在退出";
                stopLoop = true;
                return true;
            }

            if (IsSystemShuttingDown())
            {
                reason = "系统正在关机 / 重启 / 注销（SM_SHUTTINGDOWN）";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        /// <summary>
        /// 当前会话是否正在结束。取值失败时按"没在结束"处理，
        /// 不能因为一个查询失败就把真正的卡死锁在原地。
        /// </summary>
        private static bool IsSystemShuttingDown()
        {
            try
            {
                return GetSystemMetrics(SM_SHUTTINGDOWN) != 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] GetSystemMetrics(SM_SHUTTINGDOWN) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 抑制判定：静默退出循环，不再探测、不再处置。
        /// 会话结束 / 进程退出这两种情况下，狗继续跑没有任何价值。
        /// </summary>
        private void LogSuppressionOnce(string reason)
        {
            if (_suppressionLogged)
            {
                return;
            }
            _suppressionLogged = true;

            System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Judging suppressed: {reason}");

            try
            {
                LogService.Info("看门狗.停止判定", $"卡死判定已抑制，看门狗退出循环：{reason}");
            }
            catch
            {
                // 同上：拆除阶段的日志失败不值得惊动任何人
            }
        }

        /// <summary>
        /// 探测主 UI 线程是否还在处理窗口消息
        ///
        /// 【为什么用 SendMessageTimeout 而不是 DispatcherQueue.TryEnqueue】
        /// XAML 的 DispatcherQueue 在所有窗口都不可见时会进入 quiesce（暂停派发排队的任务），
        /// 此时 TryEnqueue 虽然返回 true，任务却一直不执行 —— "没被执行"被误判成"卡死"。
        /// 托盘模式下窗口长期隐藏，会一路误报，看门狗变成骚扰工具。
        ///
        /// SendMessageTimeout 走的是 Win32 消息通道：线程只要还在泵消息就会立刻响应，
        /// 只有在真卡死（死循环、同步 IO 阻塞）时才会超时 —— 与系统判定
        /// "程序未响应"用的是同一套机制。
        /// </summary>
        private bool ProbeUiThread()
        {
            var hwnd = _mainWindowHandle;

            if (hwnd == IntPtr.Zero)
            {
                // 没有探测目标时放弃判定（宁可不报警，也不要误报）
                return true;
            }

            // ⭐ 先确认句柄还是个窗口。
            // SendMessageTimeout 打到已销毁的 HWND 上返回 0，和"线程冻住"一模一样 ——
            // 不先问 IsWindow，句柄失效就会被记进无响应 streak，宽限期后必然误判卡死。
            if (!IsWindow(hwnd))
            {
                WarnInvalidTargetOnce(hwnd);
                return true;
            }

            // 官方文档：SendMessageTimeout 不保证失败时调用 SetLastError，
            // 想知道"到底是超时还是别的失败"必须自己先清零。
            // 这里清零只为多一条能看的东西，判定仍只看返回值。
            SetLastError(0);

            var responsive = SendMessageTimeout(
                hwnd,
                WM_UI_HEARTBEAT,
                IntPtr.Zero,
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                ProbeTimeoutMs,
                out _);

            if (!responsive)
            {
                // 把系统自己的口径留在日志里：IsHungAppWindow 用的正是"5 秒没泵消息"
                // 这条判定，也是任务栏"程序未响应"背后的同一套机制。
                // 只作诊断，绝不参与判定 —— 微软明确标注这个函数"not intended for
                // general use"，且对隐藏窗口可能永远返回 FALSE，拿它当闸门会把狗锁死。
                _lastProbeWin32Error = Marshal.GetLastPInvokeError();
                _lastProbeSystemReportsHung = SafeIsHungAppWindow(hwnd);
            }

            return responsive;
        }

        /// <summary>
        /// 探测目标已不是有效窗口时的节流告警。
        /// 这是"句柄失效"而非"UI 卡死"，等 App 在窗口重建后调用 Retarget 自愈。
        /// </summary>
        private void WarnInvalidTargetOnce(IntPtr hwnd)
        {
            var now = DateTime.UtcNow;
            if (now - _lastInvalidTargetWarning < InvalidTargetWarningInterval)
            {
                System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Probe target is no longer a window, abstaining");
                return;
            }

            _lastInvalidTargetWarning = now;
            LogService.Warning(
                "看门狗.探测目标失效",
                $"句柄 0x{hwnd.ToInt64():X} 已不是有效窗口（多为 keep-alive 窗口被关闭/重建），" +
                "本轮弃权不判定卡死；等待应用侧 Retarget 换绑");
        }

        private static bool SafeIsHungAppWindow(IntPtr hwnd)
        {
            try
            {
                return IsHungAppWindow(hwnd);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] IsHungAppWindow unavailable: {ex.Message}");
                return false;
            }
        }

        // 最近一次失败探测的现场，仅供处置时写进日志
        private int _lastProbeWin32Error;
        private bool _lastProbeSystemReportsHung;

        [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "IsHungAppWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsHungAppWindow(IntPtr hWnd);

        [LibraryImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static partial void SetLastError(uint dwErr);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        /// <summary>
        /// 启动看门狗
        /// </summary>
        public void Start()
        {
            // 只看"还在跑的任务"，不看"曾经创建过任务"：
            // 循环可能因为会话结束 / 退出而自行 break，此时 _watchdogTask 非空但已结束。
            // 用 _watchdogTask != null 当闸门的话，Stop()→Start() 永远起不来，
            // 一次被取消的关机就能把这只狗永久废掉。
            if (_watchdogTask is { IsCompleted: false })
            {
                System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Already started");
                return;
            }

            _cts = new CancellationTokenSource();
            // 起点放在这里而不是构造函数：构造和实际启动之间可能隔了一段时间
            _lastHeartbeat = DateTime.UtcNow;
            _graceUntil = DateTime.UtcNow + StartupGracePeriod;
            _shutdownRequested = false;
            _suppressionLogged = false;
            _watchdogTask = Task.Run(() => WatchdogLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Started (grace period: {StartupGracePeriod.TotalSeconds:F0}s)");
        }

        /// <summary>
        /// 停止看门狗（异步版本，推荐使用）
        /// </summary>
        public async Task StopAsync()
        {
            // 先置位再判断：处置动作前会读它，越早挡下越不会在退出途中重启自己
            _shutdownRequested = true;

            if (_cts == null || _watchdogTask == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopping...");
            _cts.Cancel();
            
            try
            {
                // 异步等待任务完成或超时
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));
                var completedTask = await Task.WhenAny(_watchdogTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] WARNING: Watchdog task did not complete in time");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Stop error: {ex.Message}");
            }
            
            _cts.Dispose();
            _cts = null;
            _watchdogTask = null;
            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopped");
        }

        /// <summary>
        /// 停止看门狗（同步版本，仅用于 Dispose）
        /// </summary>
        public void Stop()
        {
            // ⭐ 第一件事就是挡处置。
            // 原来的顺序是"关掉窗口 → 后台清理时才 Stop"，中间那段窗口期里
            // 探测目标已经失效、主线程正在收尾，狗完全可能判它卡死 ——
            // 表现就是"点退出反而被重启回托盘"。现在退出链一开头就调到这里。
            _shutdownRequested = true;

            if (_cts == null || _watchdogTask == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopping (sync)...");
            _cts.Cancel(); // 仅发送取消信号，不等待完成
        }

        /// <summary>
        /// 看门狗循环
        /// </summary>
        private async Task WatchdogLoop(CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Watchdog loop started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 等待检测间隔
                    await Task.Delay(_checkInterval, cancellationToken);

                    // ⭐ 会话结束 / 进程退出时不做任何判定。
                    // 放在探测之前：关机时主线程被 WM_QUERYENDSESSION 占住，
                    // 探测必然超时，让这一轮跑到判定里去就是误报的源头。
                    if (ShouldSkipJudging(out var suppressionReason, out bool appIsLeaving))
                    {
                        LogSuppressionOnce(suppressionReason);

                        if (appIsLeaving)
                        {
                            // 本进程正在退出，继续轮询没有意义
                            break;
                        }

                        // 只是"暂时不能判"：会话结束可能被其它应用或用户取消，
                        // 而系统不会告诉我们取消了 —— 所以这里空转等下一轮，
                        // 并把心跳基准推到当下、清空 streak，
                        // 免得恢复判定后的第一轮被一段"抑制期"的旧账算成冻结。
                        lock (_heartbeatLock)
                        {
                            _lastHeartbeat = DateTime.UtcNow;
                            _frozenStreak = 0;
                        }

                        continue;
                    }

                    // 又能正常判定了：复位"只记一次"的闸门，下次抑制重新留一条日志
                    _suppressionLogged = false;

                    // 探测主 UI 线程还在不在处理消息
                    bool responsive = ProbeUiThread();
                    var now = DateTime.UtcNow;

                    if (responsive)
                    {
                        lock (_heartbeatLock)
                        {
                            _lastHeartbeat = now;
                            _frozenStreak = 0;
                        }

                        // 复位卡死处置闸门，下次再卡死仍然会弹对话框，而不是永久哑火
                        if (Interlocked.Exchange(ref _recoveryLaunched, 0) == 1)
                        {
                            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] ✅ UI thread recovered, recovery gate reset");

                            // 只在「处置过 → 恢复」这一条边上落一条 Info（不是每轮都写）。
                            // 有了它，日志里的时间线才完整：卡死 → 重启 → 多久之后真的缓过来了。
                            LogService.Info("看门狗.UI线程恢复", "主 UI 线程重新响应探测，卡死处置闸门已复位");
                        }

                        continue;
                    }

                    if (now < _graceUntil)
                    {
                        // 启动宽限期内主线程忙是正常现象，连计数都不该累积
                        lock (_heartbeatLock)
                        {
                            _frozenStreak = 0;
                        }

                        System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] No response during startup grace period, ignoring");
                        continue;
                    }

                    int streak;
                    TimeSpan sinceLastResponse;
                    lock (_heartbeatLock)
                    {
                        _frozenStreak++;
                        streak = _frozenStreak;
                        sinceLastResponse = now - _lastHeartbeat;
                    }

                    if (streak < FrozenStreakThreshold || sinceLastResponse <= _timeout)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] ⚠️ UI thread slow to respond: streak={streak}/{FrozenStreakThreshold}, {sinceLastResponse.TotalSeconds:F1}s since last response");
                    }
                    else
                    {
                        // 连续多轮无响应，判定为真的卡死
                        System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] ⚠️ UI thread frozen! {sinceLastResponse.TotalSeconds:F1}s without response (streak={streak})");
                        OnUIThreadFrozen(sinceLastResponse);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Error in watchdog loop: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Watchdog loop exited");
        }

        /// <summary>
        /// 触发 UI 线程卡死事件
        /// </summary>
        private void OnUIThreadFrozen(TimeSpan frozenDuration)
        {
            var args = new UIThreadFrozenEventArgs(frozenDuration);
            UIThreadFrozen?.Invoke(this, args);

            // 如果没有订阅者，执行默认操作
            if (UIThreadFrozen == null || UIThreadFrozen.GetInvocationList().Length == 0)
            {
                HandleFrozenUIThreadDefault(frozenDuration);
            }
        }

        /// <summary>
        /// 默认的 UI 线程卡死处理逻辑
        /// </summary>
        private void HandleFrozenUIThreadDefault(TimeSpan frozenDuration)
        {
            System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Handling frozen UI thread (duration: {frozenDuration.TotalSeconds:F1}s)");

            // ⭐ 处置前的第二次确认。
            // 判定要连续 2 轮、约 4~7 秒，这段时间足够用户按下"关机"或者走完退出链 ——
            // 只在循环开头查一次的话，仍然会把"正在被系统收掉"当成"卡死需要抢救"。
            // 这里的代价是一次 GetSystemMetrics，收益是不在会话拆除阶段拉起新进程。
            if (ShouldSkipJudging(out var suppressionReason, out _))
            {
                LogSuppressionOnce(suppressionReason);
                LogService.Warning(
                    "看门狗.取消处置",
                    $"判定为冻结（{frozenDuration.TotalSeconds:F1} 秒无响应）但环境已变化，放弃通知与重启：{suppressionReason}");
                return;
            }

            // 同一次卡死只处置一次（看门狗每 2 秒一轮，不节流会连发通知 + 弹一堆对话框）
            if (Interlocked.Exchange(ref _recoveryLaunched, 1) == 1)
            {
                return;
            }

            // ⭐ 判定必须落文件日志（2026-10-15）。
            // 之前这里只有 Debug.WriteLine —— Release 版里等于什么都没记，
            // 于是「软件为什么自己重启了」这件事在事后完全无法还原：
            // 到底是 UI 线程真冻结了、还是WebView2 恢复路径把它按住了，查不到证据。
            // 落在 Error 级别，和 WebView2.ProcessFailed 是同一条时间线，能直接对表。
            // 顺带把系统口径（IsHungAppWindow）与 Win32 错误码带上：
            // 二者一起为真才像"任务栏那种未响应"，只有超时而没有 hung 标记，多半是别的原因。
            LogService.Error(
                "看门狗.UI线程卡死",
                $"判定主 UI 线程冻结：{frozenDuration.TotalSeconds:F1} 秒无消息响应" +
                $"（探测超时 {ProbeTimeoutMs}ms，连续 {FrozenStreakThreshold} 轮无响应，" +
                $"探测句柄 0x{_mainWindowHandle.ToInt64():X}，" +
                $"系统口径未响应={_lastProbeSystemReportsHung}，Win32 错误={_lastProbeWin32Error}）");

            // 告知用户即将发生什么。通知由 PowerShell 独立进程投递，
            // 即便本进程随后被强杀，它也不受影响。
            ShowWindowsNotification(
                "DockedTools 无响应",
                $"应用程序已无响应 {frozenDuration.TotalSeconds:F0} 秒，正在自动重启到托盘。");

            // 给 PowerShell 一点时间把通知发出去——父进程立刻死的话它虽然仍能跑完，
            // 但进程刚创建就被父进程退出波及的概率不为零，宁可慢一拍。
            Thread.Sleep(1200);

            RestartToTray();
        }

        /// <summary>
        /// 以独立进程执行一段 PowerShell 脚本
        /// 
        /// 【为什么要落盘而不是 -Command 内联】
        /// Windows PowerShell 5.1 按 ANSI 解析命令行参数，内联的中文（标题、按钮文案）
        /// 会变成乱码；写成 UTF-8（带 BOM）文件再用 -File 执行，PowerShell 会按 BOM 解码。
        /// </summary>
        private static void RunPowerShellScript(string fileName, string script, params (string Name, string Value)[] environment)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);

            if (environment != null)
            {
                foreach (var (name, value) in environment)
                {
                    psi.Environment[name] = value;
                }
            }

            Process.Start(psi);
        }

        /// <summary>
        /// 获取本应用的 toast AUMID（AppUserModelID）。
        ///
        /// 【为什么不能写死】
        /// toast 必须用系统注册过的 AUMID 才能正常弹横幅：带应用名、图标、
        /// 点击可激活应用、用户能在系统通知设置里管理它。
        /// 打包运行时 AUMID = 包 FamilyName + "!App"（实测："边栏助手"注册的就是它）；
        /// 写死一个未注册的名字（如 "DockedTools"）会变成通知平台的"黑户"：
        /// 通知进了数据库却渲染不出正确的归属，横幅行为不可靠。
        /// </summary>
        private static string GetToastAumid()
        {
            try
            {
                return Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
            }
            catch
            {
                // 非打包运行的退化值：能用性不受保证，但比抛异常强
                return "DockedTools";
            }
        }

        /// <summary>
        /// 显示 Windows 系统通知
        /// </summary>
        private void ShowWindowsNotification(string title, string message)
        {
            try
            {
                // 使用 PowerShell 显示 Windows 通知（不依赖 UI 线程）
                // 标题/正文走环境变量注入：既避免 C# 插值把换行塞进 XML，
                // 也避免中文在 -Command 命令行里被 ANSI 解析成乱码。
                var script = @"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$template = @""
<toast duration='long'>
    <visual>
        <binding template='ToastGeneric'>
            <text>$env:DT_TOAST_TITLE</text>
            <text>$env:DT_TOAST_BODY</text>
        </binding>
    </visual>
</toast>
""@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:DT_TOAST_AUMID).Show($toast)
";

                RunPowerShellScript(
                    "DockedTools_FrozenToast.ps1",
                    script,
                    ("DT_TOAST_TITLE", EscapeForXml(title)),
                    ("DT_TOAST_BODY", EscapeForXml(message)),
                    ("DT_TOAST_AUMID", GetToastAumid()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Failed to show notification: {ex.Message}");
            }
        }

        /// <summary>
        /// 转义 Toast XML 里的特殊字符（标题/正文是动态拼的，含 &amp; &lt; &gt; 会破坏 XML）
        /// </summary>
        private static string EscapeForXml(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// 卡死时的静默恢复：拉起一个新实例（仅托盘模式，不弹窗口），然后结束自己。
        ///
        /// 【为什么不能用常规重启流程】
        /// AppRestartService.RestartWithArgs 最后会调 App.ExitApplicationPublic()，
        /// 归宿是 Application.Current.Exit() —— 要靠 UI 线程的消息泵配合才能跑完。
        /// 卡死时消息泵已停摆，那条路径必然挂住。所以这里必须绕开 XAML，
        /// 等新进程安稳落地后主动结束自己。
        ///
        /// 【为什么必须先起新进程再自杀】
        /// 命令行里的 --restart 让新实例跳过"把激活重定向给已有实例"这条分支
        /// （见 App 构造函数），它会轮询等待本进程释放 Mutex，最多 3 秒后接管。
        /// 反过来先自杀的话，旧实例的退出状态不可控，反而更难预后。
        ///
        /// 【重启风暴保护】
        /// 若应用每次启动就卡死（某页面一加载就死锁），会退化成无限重启循环。
        /// 因此把"最后一次由看门狗触发的重启时刻"持久化，间隔过近就只通知、不重启。
        /// </summary>
        private void RestartToTray()
        {
            // 最后一道闸：必须在抢占冷却名额之前查。
            // TryClaimRecoverySlot 会把连续重启次数写进戳记文件，
            // 会话结束这种"根本不是卡死"的场合一旦写进去，
            // 下一次真卡死的冷却就从 90 秒被抬到 3 分钟 —— 白扣自愈能力。
            if (ShouldSkipJudging(out var suppressionReason, out _))
            {
                LogService.Warning(
                    "看门狗.取消重启",
                    $"放弃自动重启（{suppressionReason}），不写入重启冷却戳记");
                return;
            }

            if (!TryClaimRecoverySlot(out var sinceLastText, out int consecutiveRestarts, out int cooldownSeconds))
            {
                System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Recent watchdog restart detected, aborting to avoid infinite loop");

                // 这一条必须落文件：它是「为什么软件停在托盘不再自己复活」的唯一凭证。
                // 没有它，用户只看到「通知说不再重启了」，事后无从还原冻了几轮、间隔多久。
                LogService.Warning(
                    "看门狗.重启风暴保护",
                    $"距上次看门狗重启仅 {sinceLastText}，本次冷却需要 {cooldownSeconds} 秒" +
                    $"（已连续第 {consecutiveRestarts} 次）—— 只通知，不重启");

                ShowWindowsNotification(
                    "DockedTools 反复无响应",
                    $"距上次自动重启仅 {sinceLastText}，已停止自动重启以免陷入循环，请手动检查。");
                return;
            }

            try
            {
                // ⭐ 必须走包唤起：MSIX 桌面 App 不能靠 Process.Start(exe) 复活，
                // 否则新实例少了正确的 AppUserModelID / Launch 激活，跟开始菜单里的
                // 那个应用不是同一个身份。详见 PackagedActivationService 的注释。
                LogService.Warning(
                    "看门狗.自动重启",
                    $"执行静默重启到托盘（连续第 {consecutiveRestarts} 次，参数 --restart --tray-only）");

                var launch = PackagedActivationService.Launch("--restart --tray-only");

                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Launch: {launch.Detail}");

                LogService.Warning("看门狗.自动重启", $"包唤起结果：Success={launch.Success}，{launch.Detail}");

                if (!launch.Success)
                {
                    System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Replacement instance not launched, staying alive");
                    return;
                }

                // 给新实例一点时间进入启动流程、排到 Mutex 等待队列上
                Thread.Sleep(800);

                // ⭐ 日志改成异步落盘之后，硬退出会把后台写线程一起带走 ——
                //    而这几条恰恰是排查「为什么重启」最需要的。这里必须等它压干。
                //    放在 Sleep(800) 之后：那 800ms 已经够正常排空，Flush 基本立刻返回。
                LogService.Flush(600);

                // 兜底：UI 线程已卡死的情况下 Environment.Exit 有可能被 finalizer 拖住，
                // 到点直接 Kill，保证 Mutex 一定释放、新实例一定能接管。
                var exitGuard = new Thread(() =>
                {
                    Thread.Sleep(3000);
                    try { Process.GetCurrentProcess().Kill(); } catch { }
                })
                { IsBackground = true, Name = "WatchdogExitGuard" };
                exitGuard.Start();

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Failed to restart: {ex.Message}");
            }
        }

        // ── 重启风暴保护：从「固定 90 秒」改成「按连续次数指数递增的冷却」────────
        //
        // 【旧实现为什么会漏】
        // 只有一道 90 秒硬闸门，判的是「距上次看门狗重启是否短于 90 秒」。
        // 于是「每次启动 2 分钟后才冻结」这种节律完全打不到它 —— 每轮都放行，
        // 用户看到的就是无限次反复重启，而这正是最容易撞上 WebView2 恢复风暴的节律。
        //
        // 【现在的做法】
        // ① 冷却时长随连续次数指数增长：90s → 3min → 6min → 12min …… 封顶 30 分钟。
        //    自愈能力保留（不会彻底不再复活），但风暴被压成低频事件。
        // ② 距离上次重启超过 RestartHealthResetSeconds（默认 30 分钟）说明中间一直健康，
        //    连续次数归零，冷却重新从 90 秒起算 —— 偶发一次卡死不会被历史拖累。
        // ③ 每一次判定（放行 / 拦截）都落日志，事后能还原完整节律。
        private const int RestartCooldownBaseSeconds = 90;
        private const int RestartCooldownMaxSeconds = 1800;
        private const int RestartHealthResetSeconds = 1800;

        /// <summary>
        /// 抢占一次"自动重启名额"，顺带把距上次重启的时长带出来给调用方写文案。
        ///
        /// 读不到文件或写不进去时一律放行——宁可重启，也不要因为一个标记文件
        /// 把真正的卡死锁在原地。
        /// </summary>
        /// <param name="sinceLastText">距上次看门狗重启的可读时长（拦截时写进通知文案）</param>
        /// <param name="consecutiveRestarts">连续重启次数（含本次；放行后为已写入的新值）</param>
        /// <param name="cooldownSeconds">本次要求的最小间隔，拦截时用于说明为什么被拦</param>
        private static bool TryClaimRecoverySlot(
            out string sinceLastText, out int consecutiveRestarts, out int cooldownSeconds)
        {
            sinceLastText = string.Empty;
            consecutiveRestarts = 0;
            cooldownSeconds = RestartCooldownBaseSeconds;

            try
            {
                var stampPath = GetRecoveryStampPath();
                var directory = Path.GetDirectoryName(stampPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                DateTime lastRestart = DateTime.UtcNow;
                int previousStreak = 0;

                if (File.Exists(stampPath))
                {
                    // 新格式 "{:o}|{连续次数}"；旧格式只有一个时间戳，读到时按连续 1 次处理。
                    // 迁移期不能因为格式变了就把历史清空——清空等于冷却重新从 90 秒起，
                    // 正好在升级后的第一次卡死上放开一轮重启风暴。
                    string raw = File.ReadAllText(stampPath).Trim();
                    string[] parts = raw.Split('|');

                    if (DateTime.TryParse(parts[0], null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        lastRestart = parsed.ToUniversalTime();

                        if (parts.Length > 1 && int.TryParse(parts[1], out int storedStreak))
                        {
                            previousStreak = Math.Max(1, storedStreak);
                        }
                        else
                        {
                            // 旧格式：只知道"上次重启过"，不知道连续几次，保守当作 1 次
                            previousStreak = 1;
                        }
                    }
                }

                var since = DateTime.UtcNow - lastRestart;
                int streak = since > TimeSpan.FromSeconds(RestartHealthResetSeconds)
                    ? 0            // 中间一直健康，重新起算
                    : previousStreak;

                int requiredSeconds = ComputeCooldownSeconds(streak);
                cooldownSeconds = requiredSeconds;

                if (since < TimeSpan.FromSeconds(requiredSeconds))
                {
                    consecutiveRestarts = streak + 1;
                    sinceLastText = FormatDuration(since);
                    return false;
                }

                consecutiveRestarts = streak + 1;
                File.WriteAllText(stampPath, $"{DateTime.UtcNow:o}|{consecutiveRestarts}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Cannot access recovery stamp ({ex.Message}), allowing restart");
                return true;
            }
        }

        /// <summary>
        /// 冷却时长：基数按连续次数指数翻倍，封顶 <see cref="RestartCooldownMaxSeconds"/>。
        /// </summary>
        private static int ComputeCooldownSeconds(int streak)
        {
            int shifts = Math.Min(streak, 10); // 卡住移位，避免溢出成负数
            long seconds = (long)RestartCooldownBaseSeconds << shifts;
            return (int)Math.Min(seconds, RestartCooldownMaxSeconds);
        }

        /// <summary>把时长写成人话，用于通知与日志文案</summary>
        private static string FormatDuration(TimeSpan since)
        {
            if (since.TotalMinutes >= 1)
            {
                return $"{since.TotalMinutes:F1} 分钟";
            }

            return $"{since.TotalSeconds:F0} 秒";
        }

        /// <summary>
        /// 重启标记文件路径。用 LocalApplicationData 而非 Temp：
        /// MSIX 下它会被重定向到本包私有目录，重启后仍读得到同一个位置。
        /// </summary>
        private static string GetRecoveryStampPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DockedTools",
                "WatchdogRecovery.stamp");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            Stop();
        }
    }

    /// <summary>
    /// UI 线程卡死事件参数
    /// </summary>
    public class UIThreadFrozenEventArgs : EventArgs
    {
        public TimeSpan FrozenDuration { get; }

        public UIThreadFrozenEventArgs(TimeSpan frozenDuration)
        {
            FrozenDuration = frozenDuration;
        }
    }
}
