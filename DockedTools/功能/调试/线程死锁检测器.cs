using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DockedTools.Features.Debug
{
    /// <summary>
    /// 线程死锁检测器
    /// 定期检测线程状态，当发现可疑情况时输出详细信息
    /// 
    /// 【使用方法】
    /// 在 App.xaml.cs 的 OnLaunched 中启动：
    /// ThreadDeadlockDetector.Start();
    /// 
    /// 【检测内容】
    /// 1. 线程总数异常增长
    /// 2. 等待状态的线程过多
    /// 3. UI 线程无响应
    /// </summary>
    public static class ThreadDeadlockDetector
    {
        private static CancellationTokenSource? _cts;
        private static Task? _monitorTask;
        private static int _baselineThreadCount;
        private static DateTime _lastLogTime = DateTime.MinValue;

        // CPU 使用率的两次采样点（TimeSpan 不是原子类型，但这条只有轮询线程自己读写）
        private static DateTime _lastCpuSampleAt = DateTime.MinValue;
        private static TimeSpan _lastCpuTotal;

        /// <summary>
        /// 启动死锁检测器
        /// </summary>
        /// <param name="checkInterval">检测间隔（默认 5 秒）</param>
        public static void Start(TimeSpan? checkInterval = null)
        {
            if (_monitorTask != null)
            {
                System.Diagnostics.Debug.WriteLine("[ThreadDeadlockDetector] Already started");
                return;
            }

            _baselineThreadCount = Process.GetCurrentProcess().Threads.Count;
            System.Diagnostics.Debug.WriteLine($"[ThreadDeadlockDetector] Baseline thread count: {_baselineThreadCount}");

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(checkInterval ?? TimeSpan.FromSeconds(5), _cts.Token));
            
            System.Diagnostics.Debug.WriteLine("[ThreadDeadlockDetector] Started");
        }

        /// <summary>
        /// 停止死锁检测器
        /// </summary>
        public static void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _monitorTask = null;
            System.Diagnostics.Debug.WriteLine("[ThreadDeadlockDetector] Stopped");
        }

        /// <summary>
        /// 监控循环
        /// </summary>
        private static async Task MonitorLoop(TimeSpan checkInterval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(checkInterval, cancellationToken);
                    CheckThreadHealth();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ThreadDeadlockDetector] Monitor error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 检查线程健康状态
        /// </summary>
        private static void CheckThreadHealth()
        {
            var process = Process.GetCurrentProcess();
            var threads = process.Threads;
            var threadCount = threads.Count;

            // 统计各种状态的线程
            int waitingThreads = 0;
            int runningThreads = 0;
            int otherThreads = 0;

            foreach (ProcessThread thread in threads)
            {
                switch (thread.ThreadState)
                {
                    case System.Diagnostics.ThreadState.Wait:
                        waitingThreads++;
                        break;
                    case System.Diagnostics.ThreadState.Running:
                        runningThreads++;
                        break;
                    default:
                        otherThreads++;
                        break;
                }
            }

            // 检测异常情况
            bool suspicious = false;
            var report = new StringBuilder();
            report.AppendLine($"[ThreadDeadlockDetector] Thread Health Report ({DateTime.Now:HH:mm:ss})");
            report.AppendLine($"  Total threads: {threadCount} (baseline: {_baselineThreadCount})");
            report.AppendLine($"  Running: {runningThreads}");
            report.AppendLine($"  Waiting: {waitingThreads}");
            report.AppendLine($"  Other: {otherThreads}");

            // 异常1：线程数暴增（超过基线 50%）
            if (threadCount > _baselineThreadCount * 1.5)
            {
                suspicious = true;
                report.AppendLine($"  ⚠️ WARNING: Thread count increased by {((threadCount - _baselineThreadCount) * 100.0 / _baselineThreadCount):F0}%");
            }

            // 异常2：等待线程过多（超过 80%）
            if (threadCount > 0 && (waitingThreads * 100.0 / threadCount) > 80)
            {
                suspicious = true;
                report.AppendLine($"  ⚠️ WARNING: {waitingThreads * 100.0 / threadCount:F0}% threads are waiting (possible deadlock)");
            }

            // 异常3：线程一大堆却几乎不耗 CPU ⇒ 集体在等什么东西。
            //
            // ⚠️ 这条判据原先是坏的：它拿 process.TotalProcessorTime 除以核数当「CPU 使用率」，
            // 可那是**进程启动至今的累计毫秒数**，不是使用率 —— 程序跑满几秒就恒大于 100，
            // 「低 CPU」这个条件一次都成立不了，整段等于白写。
            // 真使用率只能两次采样作差、再除以采样间隔，所以下面要留上一次的读数。
            try
            {
                process.Refresh();
                var sampleAt = DateTime.UtcNow;
                var cpuTotal = process.TotalProcessorTime;

                if (_lastCpuSampleAt != DateTime.MinValue)
                {
                    double elapsedMs = (sampleAt - _lastCpuSampleAt).TotalMilliseconds;
                    double cpuMs = (cpuTotal - _lastCpuTotal).TotalMilliseconds;

                    if (elapsedMs > 0)
                    {
                        // 100% = 一个核跑满；除核数后是全机口径的使用率
                        double usagePercent = cpuMs / elapsedMs / Environment.ProcessorCount * 100.0;

                        if (threadCount > 20 && usagePercent < 5.0)
                        {
                            suspicious = true;
                            report.AppendLine($"  ⚠️ WARNING: {threadCount} threads but only {usagePercent:F1}% CPU (possible blocking)");
                        }
                    }
                }

                _lastCpuSampleAt = sampleAt;
                _lastCpuTotal = cpuTotal;
            }
            catch
            {
                // 忽略 CPU 统计错误
            }

            // 只在发现可疑情况时输出完整报告
            if (suspicious)
            {
                UnifiedCalls.Logging.LogService.Warning("线程健康", report.ToString());
                
                // 输出详细线程栈信息（每 30 秒最多一次，避免日志爆炸）
                if ((DateTime.Now - _lastLogTime).TotalSeconds > 30)
                {
                    _lastLogTime = DateTime.Now;
                    DumpDetailedThreadInfo();
                }
            }
        }

        /// <summary>
        /// 给「已经判定主 UI 线程冻结」的调用方抓一份一次性现场快照。
        ///
        /// 【为什么单独开这个入口】这个类原先只有 <see cref="Start"/> 那条 5 秒轮询，
        /// 而全仓没有任何地方调用它 —— 纯死代码。看门狗已经把「什么时候算冻结」判完了，
        /// 这里只补一件事：冻结那一刻进程里每个线程在干嘛。所以做成一次性取报告，
        /// 不引入第二条常驻轮询 —— 托盘应用常年挂后台，多一个每 5 秒醒来的线程就是多一份功耗。
        ///
        /// 【这份报告能回答什么】主 UI 线程是卡在 Executive / LpcReceive（等内核、跨进程调用）
        /// 还是 UserRequest（等消息）；有没有另一个线程占着 CPU 不放；线程数是不是涨疯了。
        ///
        /// 【拿不到什么】托管调用栈。本机实测过：打包 + AOT 的进程连 dotnet-stack 都 attach 不动，
        /// 所以这里只用 ProcessThread 的公共属性 —— 那不需要调试权限，
        /// 但足够把「哪一类阻塞」先分开，比原来只有一条「冻结了 N 秒」强得多。
        /// </summary>
        /// <param name="uiThreadId">主 UI 线程的 Win32 线程 id，用于在报告里点名；0 = 不点名</param>
        public static string CaptureFreezeSnapshot(int uiThreadId) => BuildThreadReport(uiThreadId);

        /// <summary>
        /// 线程快照的唯一实现：条数统计 + 按累计 CPU 排序的线程清单，可选点名某条线程。
        /// 纯读 <see cref="ProcessThread"/> 的公共属性，不碰任何 UI 线程亲和对象，任何线程上都能调。
        /// </summary>
        private static string BuildThreadReport(int highlightThreadId)
        {
            var builder = new StringBuilder();

            try
            {
                using var process = Process.GetCurrentProcess();
                var threads = process.Threads.Cast<ProcessThread>().ToList();

                int waiting = threads.Count(t => t.ThreadState == System.Diagnostics.ThreadState.Wait);
                int running = threads.Count(t => t.ThreadState == System.Diagnostics.ThreadState.Running);

                builder.Append($"线程 {threads.Count} 条（Wait {waiting} / Running {running} / 其它 {threads.Count - waiting - running}）");

                if (_baselineThreadCount > 0 && threads.Count > _baselineThreadCount)
                {
                    builder.Append($"，比基线 {_baselineThreadCount} 多 {threads.Count - _baselineThreadCount} 条");
                }

                const int listedThreads = 12;

                // 点名的那条永远要出现，哪怕它 CPU 最低 —— 冻结住的 UI 线程恰恰是最不耗 CPU 的那条，
                // 只按 CPU 取前 N 名正好会把它筛掉。
                var ordered = threads.OrderByDescending(t => t.TotalProcessorTime).ToList();
                builder.AppendLine();

                ProcessThread? highlighted = highlightThreadId == 0
                    ? null
                    : ordered.FirstOrDefault(t => t.Id == highlightThreadId);

                if (highlighted != null)
                {
                    ordered.Remove(highlighted);
                    AppendThreadLine(builder, highlighted, " <<< 主 UI 线程");
                }

                foreach (var thread in ordered.Take(listedThreads))
                {
                    AppendThreadLine(builder, thread, string.Empty);
                }

                if (ordered.Count > listedThreads)
                {
                    builder.Append($"（其余 {ordered.Count - listedThreads} 条未列出）");
                }
            }
            catch (Exception ex)
            {
                // 现场采集绝不能反过来把调用方带崩；但也不能静默 —— 那就变成「什么都没记」的第二个坑
                builder.Append($"采集线程快照失败: {ex.GetType().Name}: {ex.Message}");
            }

            return builder.ToString();
        }

        private static void AppendThreadLine(StringBuilder builder, ProcessThread thread, string marker)
        {
            try
            {
                string waitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                    ? $", {thread.WaitReason}"
                    : string.Empty;

                builder.AppendLine(
                    $"  tid={thread.Id} {thread.ThreadState}{waitReason} " +
                    $"cpu={thread.TotalProcessorTime.TotalMilliseconds:F0}ms 优先级={thread.PriorityLevel}{marker}");
            }
            catch
            {
                // 个别线程的属性读不到不影响整份报告
            }
        }

        /// <summary>
        /// 输出详细线程信息（用于死锁分析）
        /// </summary>
        /// <summary>
        /// 把详细线程清单记进日志。
        ///
        /// 原先这里逐条 <c>Debug.WriteLine</c>，而它带 <c>[Conditional("DEBUG")]</c> ——
        /// Release 里整段等于什么都没记，和看门狗当年「软件为什么老是无响应事后完全无法还原」
        /// 是同一个坑。现在统一走 <see cref="BuildThreadReport"/> + LogService，
        /// 两种配置都留痕，字段也和冻结快照完全一致（省掉一份平行实现）。
        /// </summary>
        private static void DumpDetailedThreadInfo()
        {
            // 这条路径没有「哪一个是 UI 线程」的信息，所以不点名，只按 CPU 排序列出来
            UnifiedCalls.Logging.LogService.Warning("线程清单", BuildThreadReport(0));
        }

        /// <summary>
        /// 手动触发线程健康检查并输出报告
        /// </summary>
        public static void TriggerHealthCheck()
        {
            System.Diagnostics.Debug.WriteLine("[ThreadDeadlockDetector] Manual health check triggered");
            CheckThreadHealth();
            DumpDetailedThreadInfo();
        }
    }
}
