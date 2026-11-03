using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.Features.Tray
{
    /// <summary>
    /// UI 线程探测器 - 监控主 UI 线程健康状态
    ///
    /// 【本类现在只做一件事：发现并记录】
    /// 1. 定期探测主 UI 线程还在不在处理窗口消息
    /// 2. 连续多次探测无响应时判定为卡死，并把现场写进文件日志
    /// 3. **不做任何自动处置** —— 不重启、不通知、不退进程
    ///
    /// 【为什么把自愈整段拿掉（2026-10-18）】
    /// 原来卡死时会「静默重启到托盘」，还带重启风暴冷却戳记。实测下来它是纯负收益：
    ///   · 主 UI 线程被 WebView2 的 CDP 往返阻塞是**可恢复的**（日志里同一实例
    ///     多次在几十秒内自行恢复响应），重启只是把一次卡顿放大成一次进程替换；
    ///   · 重启后的新实例照样会卡（实测重启后 27 秒内再次冻结）—— 根因在主线程，
    ///     换进程不解决任何问题，只是让「卡」这件事变得更频繁、更难查；
    ///   · 重启要唤起新进程、交还单实例 Mutex、旧进程再自杀，这一串动作本身就是
    ///     「双实例并存」「点了重启没反应」这类问题的来源；
    ///   · 用户反馈的「老是无响应」在这个机制下被放大成「老是自己重启」，
    ///     掩盖了真正需要修的那个阻塞点。
    ///
    /// 所以现在的契约是：**发现了就如实记下来，剩下的交给托盘**。
    /// 托盘跑在独立 UI 线程上（见 TrayUIThreadHost），主线程卡死时它照样能收消息、
    /// 能弹菜单、能点退出 —— 用户自己决定要不要退出或重启，这比程序替他重启可控得多。
    ///
    /// 【设计原理】
    /// - 探测跑在独立的后台线程上，不依赖主 UI 线程
    /// - 用 Win32 SendMessageTimeout 探测（不用 DispatcherQueue.TryEnqueue，
    ///   原因见 ProbeUiThread 注释）
    /// - 只读不写：探测器从不碰窗口、从不碰进程生命周期
    /// </summary>
    public partial class UIThreadWatchdog : IDisposable
    {
        // ⭐ 可变而非 readonly：探测目标会随 keep-alive 窗口的重建而换 HWND，
        //    见 Retarget()。锁在启动那一刻的句柄，会让重建后的健康实例被误判成卡死。
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
        private const int SM_SHUTTINGDOWN = 0x2000;
        // 探测目标失效（HWND 已销毁）时的告警节流，避免每 2 秒刷一条同样的 Warning
        private static readonly TimeSpan InvalidTargetWarningInterval = TimeSpan.FromSeconds(60);
        private CancellationTokenSource? _cts;
        private Task? _probeTask;
        private bool _disposed;
        private DateTime _lastHeartbeat;
        private readonly object _heartbeatLock = new object();

        // Stop() 已发出、但循环可能还停在一次探测里（最长 ProbeTimeoutMs）。
        private volatile bool _shutdownRequested;

        // 本轮抑制判定的原因只记一次，别每 2 秒刷日志。
        private bool _suppressionLogged;
        private DateTime _lastInvalidTargetWarning = DateTime.MinValue;

        // 进程级的"外部要求本进程让路"抑制：存的是**抑制截止时刻**（UTC Ticks，0 = 无）。
        // 由托盘线程在 WM_QUERYENDSESSION 里按 lParam 分流置位 —— 那个消息比 SM_SHUTTINGDOWN
        // 更早、更确定：系统已经明确在问"你能不能走"，此刻记录一次"卡死"纯属噪音。
        // 用静态 + long 是为了让探测器不必依赖 App/窗口的对象图（托盘和它隔着一个线程），
        // 同时保证跨线程读写是原子的。
        private static long _suppressUntilTicks;

        // 抑制原因（仅用于日志）。字符串引用的读写在 CLR 下是原子的，
        // 且这里只用来打日志，读到旧值无害。
        private static string? _suppressNote;

        // 两类让路的有效期。WM_QUERYENDSESSION 之后**没有任何消息**会告诉我们"这事取消了"
        // （用户或其它应用否决关机时，系统根本不发 WM_ENDSESSION，本进程继续活着），
        // 所以抑制绝不能永久生效 —— 永久抑制等于探测器被一次取消的关机彻底废掉。
        //
        // 会话结束（关机/重启/注销）给 120 秒：会话拆除本身就几十秒量级，
        //   这段时间里 UI 线程可能一直在被系统收尸，探测必然超时。
        // 包替换 / servicing（ENDSESSION_CLOSEAPP）给 30 秒：这种场合系统要的是
        //   "把文件腾出来"，我们的进程随后会被终止。
        private static readonly TimeSpan SessionEndSuppressWindow = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan AppReplacementSuppressWindow = TimeSpan.FromSeconds(30);

        // 一次卡死只记一次日志的闸门。
        private int _frozenLogged;

        // 启动宽限期：冷启动期间要建窗口、加载 XAML、初始化页面，主线程忙上好几秒是常态。
        private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(20);

        // 连续多少轮没心跳才真的判定卡死。
        private const int FrozenStreakThreshold = 2;

        private DateTime _graceUntil;
        private int _frozenStreak;

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
        /// 【为什么仍然需要这个口子】
        /// keep-alive 窗口一旦被关掉又重建，新的 XAML 窗口是另一个 HWND。
        /// SendMessageTimeout 打到已销毁的句柄上只会返回 0，和"线程真的冻住"在返回值
        /// 上完全一样 —— 不换绑的话，宽限期一过必然误报一次"卡死"。
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
        /// 到期自动失效，避免一次被取消的关机或被回退的安装把探测器永久废掉。
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
        /// 声明"会话正在结束"（关机 / 重启 / 注销）：停止卡死判定。
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
                LogService.Info("探测.停止判定", $"外部要求本进程让路（{note}），停止卡死判定：{reason}");
            }
            catch
            {
                // 会话正在拆除，日志写不下去也不能把异常抛回 native 回调
            }
        }

        /// <summary>
        /// 当前是否处于"不该做卡死判定"的状态。
        ///
        /// 【为什么这四条要一起看】
        /// ① 外部让路通知（会话结束 / 包替换 servicing）：系统已经在问我们要不要走，
        ///    此时记录"卡死"纯属噪音。
        /// ② SM_SHUTTINGDOWN：不依赖任何钩子的兜底 —— 我们没给 XAML 窗口挂
        ///    WM_QUERYENDSESSION 处理，注销/关机时这条是唯一能主动问出来的信号。
        /// ③ 本进程正在退出：Stop() 会先把狗停掉，但停的那一刻循环可能正卡在一次探测里。
        /// ④ 已被 Dispose。
        /// 关机时 UI 线程被 WM_QUERYENDSESSION / WM_ENDSESSION 的处理占住，
        /// 天然不泵消息，探测必然超时；不拦的话"关机"这件事本身就会被记成卡死。
        /// </summary>
        /// <param name="reason">抑制原因，用于日志</param>
        /// <param name="stopLoop">true = 本进程自己要没了，循环直接退出；
        /// false = 只是暂时不判定（会话结束），空转等下一轮，关机被取消时能自动恢复</param>
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
        /// 不能因为一个查询失败就把真正的卡死漏掉。
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
        /// 抑制判定：静默退出循环，不再探测。
        /// 会话结束 / 进程退出这两种情况下，继续探测没有任何价值。
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
                LogService.Info("探测.停止判定", $"卡死判定已抑制，探测循环退出：{reason}");
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
        /// XAML 的 DispatcherQueue 在所有窗口都不可见时会进入 quiesce（暂停派发的任务），
        /// 此时 TryEnqueue 虽然返回 true，任务却一直不执行 —— "没被执行"被误判成"卡死"。
        /// 托盘模式下窗口长期隐藏，这会一路误报。
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
            // 不先问 IsWindow，句柄失效就会被记进无响应 streak，宽限期后必然误报。
            if (!IsWindow(hwnd))
            {
                WarnInvalidTargetOnce(hwnd);
                return true;
            }

            // 官方文档：SendMessageTimeout 不保证失败时调用 SetLastError，
            // 想知道"到底是超时还是别的失败"必须自己先清零。
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
                // general use"，且对隐藏窗口可能永远返回 FALSE。
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
                "探测.探测目标失效",
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

        // 最近一次失败探测的现场，仅供判定时写进日志
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
        /// 启动探测循环
        /// </summary>
        public void Start()
        {
            // 只看"还在跑的任务"，不看"曾经创建过任务"：
            // 循环可能因为会话结束 / 退出而自行 break，此时 _probeTask 非空但已结束。
            // 用 _probeTask != null 当闸门的话，Stop()→Start() 永远起不来，
            // 一次被取消的关机就能把这只狗永久废掉。
            if (_probeTask is { IsCompleted: false })
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
            _probeTask = Task.Run(() => ProbeLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Started (grace period: {StartupGracePeriod.TotalSeconds:F0}s)");
        }

        /// <summary>
        /// 停止探测（异步版本，推荐使用）
        /// </summary>
        public async Task StopAsync()
        {
            // 先置位再判断
            _shutdownRequested = true;

            if (_cts == null || _probeTask == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopping...");
            _cts.Cancel();

            try
            {
                // 异步等待任务完成或超时
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));
                var completedTask = await Task.WhenAny(_probeTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] WARNING: Probe task did not complete in time");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Stop error: {ex.Message}");
            }

            _cts.Dispose();
            _cts = null;
            _probeTask = null;
            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopped");
        }

        /// <summary>
        /// 停止探测（同步版本，仅用于 Dispose）
        /// </summary>
        public void Stop()
        {
            // ⭐ 第一件事就是置位。原来它在退出链一开头调用，是为了挡住"处置动作"
            //    （当时的自动重启）；现在虽然不再有处置，留着它仍然有用：
            //    它保证"正在退出"这件事不会在日志里留下一条假的"卡死"。
            _shutdownRequested = true;

            if (_cts == null || _probeTask == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Stopping (sync)...");
            _cts.Cancel(); // 仅发送取消信号，不等待完成
        }

        /// <summary>
        /// 探测循环
        /// </summary>
        private async Task ProbeLoop(CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Probe loop started");

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

                        // 只是"暂时不判"：会话结束可能被其它应用或用户取消，
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

                    // 又能正常判定了：复位"只记一次"的闸门
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

                        // 复位"只记一次"的闸门，下次再卡死仍然会留下一条日志
                        if (Interlocked.Exchange(ref _frozenLogged, 0) == 1)
                        {
                            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] ✅ UI thread recovered, log gate reset");

                            // 只在「记录过 → 恢复」这一条边上落一条 Info。
                            // 有了它，日志里的时间线才完整：卡死 → 多久之后真的缓过来了。
                            LogService.Info("探测.UI线程恢复", "主 UI 线程重新响应探测");
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
                        // 连续多轮无响应，判定为真的卡死 —— 记下来，不做任何处置
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
                    System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Error in probe loop: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Probe loop exited");
        }

        /// <summary>
        /// 处理一次卡死判定：**只记日志**。
        ///
        /// 【为什么必须落文件日志】
        /// 这里原本只有 Debug.WriteLine —— Release 版里等于什么都没记，
        /// 于是「软件为什么老是无响应」这件事在事后完全无法还原。
        /// 现在自动重启已经拿掉，这条 Error 就是定位那个阻塞点的**唯一线索**，
        /// 落 Error 级别，和 WebView2.ProcessFailed 处在同一条时间线上，能直接对表。
        /// 顺带把系统口径（IsHungAppWindow）与 Win32 错误码带上：
        /// 二者一起为真才像"任务栏那种未响应"，只有超时而没有 hung 标记，多半是别的原因。
        ///
        /// 【为什么不再弹通知】
        /// 通知文案原本是"正在自动重启到托盘"，自愈拿掉之后这句话已经不成立；
        /// 换成"检测到无响应"对用户也没有可执行的下一步 —— 卡死时他能看见的就是托盘，
        /// 而托盘一直都在。所以不打扰，只留记录。
        /// </summary>
        private void OnUIThreadFrozen(TimeSpan frozenDuration)
        {
            // 同一次卡死只记一次（每 2 秒一轮，不节流会刷屏）
            if (Interlocked.Exchange(ref _frozenLogged, 1) == 1)
            {
                return;
            }

            LogService.Error(
                "探测.UI线程卡死",
                $"判定主 UI 线程冻结：{frozenDuration.TotalSeconds:F1} 秒无消息响应" +
                $"（探测超时 {ProbeTimeoutMs}ms，连续 {FrozenStreakThreshold} 轮无响应，" +
                $"探测句柄 0x{_mainWindowHandle.ToInt64():X}，" +
                $"系统口径未响应={_lastProbeSystemReportsHung}，Win32 错误={_lastProbeWin32Error}）。" +
                "不自动重启；托盘运行在独立 UI 线程，此时仍可操作。");
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
}