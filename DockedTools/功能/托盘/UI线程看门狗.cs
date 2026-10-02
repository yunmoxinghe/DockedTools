using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using DockedTools.Features.AppEntry;

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
        private readonly IntPtr _mainWindowHandle;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _timeout;

        // 探测用的私有消息（WM_APP 区间，不会和系统消息撞车）
        private const uint WM_UI_HEARTBEAT = 0x8000 + 200;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        // 单次探测的等待上限：线程只要还在泵消息，微秒级就会返回
        private const uint ProbeTimeoutMs = 1500;
        private CancellationTokenSource? _cts;
        private Task? _watchdogTask;
        private bool _disposed;
        private DateTime _lastHeartbeat;
        private readonly object _heartbeatLock = new object();

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
            if (_mainWindowHandle == IntPtr.Zero)
            {
                // 没有探测目标时放弃判定（宁可不报警，也不要误报）
                return true;
            }

            return SendMessageTimeout(
                _mainWindowHandle,
                WM_UI_HEARTBEAT,
                IntPtr.Zero,
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                ProbeTimeoutMs,
                out _);
        }

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
            if (_watchdogTask != null)
            {
                System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Already started");
                return;
            }

            _cts = new CancellationTokenSource();
            // 起点放在这里而不是构造函数：构造和实际启动之间可能隔了一段时间
            _lastHeartbeat = DateTime.UtcNow;
            _graceUntil = DateTime.UtcNow + StartupGracePeriod;
            _watchdogTask = Task.Run(() => WatchdogLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Started (grace period: {StartupGracePeriod.TotalSeconds:F0}s)");
        }

        /// <summary>
        /// 停止看门狗（异步版本，推荐使用）
        /// </summary>
        public async Task StopAsync()
        {
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

            // 同一次卡死只处置一次（看门狗每 2 秒一轮，不节流会连发通知 + 弹一堆对话框）
            if (Interlocked.Exchange(ref _recoveryLaunched, 1) == 1)
            {
                return;
            }

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
            if (!TryClaimRecoverySlot(out var sinceLastText))
            {
                System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Recent watchdog restart detected, aborting to avoid infinite loop");

                ShowWindowsNotification(
                    "DockedTools 反复无响应",
                    $"距上次自动重启仅 {sinceLastText}，已停止自动重启以免陷入循环，请手动检查。");
                return;
            }

            try
            {
                // ⚠️ 必须走包唤起：MSIX 桌面 App 不能靠 Process.Start(exe) 复活，
                // 否则新实例少了正确的 AppUserModelID / Launch 激活，跟开始菜单里的
                // 那个应用不是同一个身份。详见 PackagedActivationService 的注释。
                var launch = PackagedActivationService.Launch("--restart --tray-only");

                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Launch: {launch.Detail}");

                if (!launch.Success)
                {
                    System.Diagnostics.Debug.WriteLine("[UIThreadWatchdog] Replacement instance not launched, staying alive");
                    return;
                }

                // 给新实例一点时间进入启动流程、排到 Mutex 等待队列上
                Thread.Sleep(800);

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

        /// <summary>
        /// 两次由看门狗触发的重启之间至少间隔这么久，短于此判定为启动期就卡死，不再自动重启
        /// </summary>
        private const int RestartLoopGuardSeconds = 90;

        /// <summary>
        /// 抢占一次"自动重启名额"，顺带把距上次重启的时长带出来给调用方写文案。
        ///
        /// 读不到文件或写不进去时一律放行——宁可重启，也不要因为一个标记文件
        /// 把真正的卡死锁在原地。
        /// </summary>
        private static bool TryClaimRecoverySlot(out string sinceLastText)
        {
            sinceLastText = string.Empty;

            try
            {
                var stampPath = GetRecoveryStampPath();
                var directory = Path.GetDirectoryName(stampPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(stampPath) &&
                    DateTime.TryParse(File.ReadAllText(stampPath).Trim(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var lastRestart))
                {
                    var since = DateTime.UtcNow - lastRestart.ToUniversalTime();
                    if (since < TimeSpan.FromSeconds(RestartLoopGuardSeconds))
                    {
                        sinceLastText = $"{since.TotalSeconds:F0} 秒";
                        return false;
                    }
                }

                File.WriteAllText(stampPath, DateTime.UtcNow.ToString("o"));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UIThreadWatchdog] Cannot access recovery stamp ({ex.Message}), allowing restart");
                return true;
            }
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
