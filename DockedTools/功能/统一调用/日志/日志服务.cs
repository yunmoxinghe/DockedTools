using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DockedTools.Features.UnifiedCalls.Logging
{
    /// <summary>
    /// 统一日志服务
    /// 
    /// 【功能】
    /// 1. 统一的异常记录接口
    /// 2. 自动写入本地日志文件
    /// 3. 同时输出到调试控制台
    /// 4. 支持不同日志级别
    /// 
    /// 【使用方法】
    /// LogService.Error("模块名", "操作描述", exception);
    /// LogService.Warning("模块名", "警告信息");
    /// LogService.Info("模块名", "信息");
    /// LogService.Debug("模块名", "调试信息");
    /// </summary>
    public static class LogService
    {
        private static string? _logDirectory;

        // ⭐ 为什么必须把写盘挪出调用线程（2026-10-15）
        // 旧实现是 File.AppendAllText + 全局 lock，直接在调用线程上同步落盘。
        // 而 WebView2 的 ProcessFailed 处理器、页面初始化失败路径都在 UI 线程上调
        // Error()，于是「磁盘停顿 / 杀软扫描 / MSIX 容器里的重定向写入」会经由日志
        // 把 UI 线程按住 —— 而 UI 线程一停泵，托盘看门狗就判卡死并静默重启。
        // 日志本身不值得为此冒无响应风险，所以这里改成：调用方只做一次入队，
        // 真正的磁盘 IO 全部由唯一一条写线程串行完成。
        //
        // 【有界队列】宁可丢日志，也不能让积压吃掉内存或在关闭时无限等待。
        // 溢出按「丢最旧」处理并计数，计数会在下一次成功入队时补一条告警。
        private const int MaxQueuedEntries = 4096;

        private static readonly BlockingCollection<LogEntry> _queue =
            new(new ConcurrentQueue<LogEntry>(), MaxQueuedEntries);

        /// <summary>写线程只启一次（首次真正写日志时惰性启动，不拖累启动阶段）</summary>
        private static int _writerStarted;

        /// <summary>因队列溢出被丢弃的条数（近似值，只用于告警）</summary>
        private static long _droppedEntries;

        /// <summary>
        /// 写线程是否正处在「已出队、还没写完」的那一段（1 = 正在写）。
        /// Flush 必须连这个状态一起等 —— 队列 Count 归零并不等于最后一条已经落盘，
        /// 出队和写完之间有一个窄窗口，进程正好在这里退出就会截断最后一行。
        /// </summary>
        private static int _writerBusy;

        /// <summary>一条待落盘的日志</summary>
        private readonly struct LogEntry
        {
            public LogEntry(string message, string fileName)
            {
                Message = message;
                FileName = fileName;
            }

            public string Message { get; }

            public string FileName { get; }
        }

        /// <summary>
        /// 日志级别
        /// </summary>
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error
        }

        /// <summary>
        /// 初始化日志目录
        /// </summary>
        private static void EnsureLogDirectory()
        {
            if (_logDirectory != null)
            {
                return;
            }

            try
            {
                _logDirectory = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "logs");
                Directory.CreateDirectory(_logDirectory);
            }
            catch
            {
                // 如果无法创建日志目录，使用临时目录
                _logDirectory = Path.Combine(Path.GetTempPath(), "DockedAI_Logs");
                try
                {
                    Directory.CreateDirectory(_logDirectory);
                }
                catch
                {
                    // 完全失败，只输出到控制台
                    _logDirectory = null;
                }
            }
        }

        /// <summary>
        /// 记录错误（带异常）
        /// </summary>
        /// <param name="module">模块名称</param>
        /// <param name="operation">操作描述</param>
        /// <param name="exception">异常对象</param>
        /// <param name="callerMemberName">调用方法名（自动填充）</param>
        /// <param name="callerFilePath">调用文件路径（自动填充）</param>
        /// <param name="callerLineNumber">调用行号（自动填充）</param>
        public static void Error(
            string module,
            string operation,
            Exception exception,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var message = FormatMessage(
                LogLevel.Error,
                module,
                operation,
                exception,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(message, "error.log");
        }

        /// <summary>
        /// 记录错误（无异常）
        /// </summary>
        public static void Error(
            string module,
            string message,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var logMessage = FormatMessage(
                LogLevel.Error,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(logMessage, "error.log");
        }

        /// <summary>
        /// 记录警告
        /// </summary>
        public static void Warning(
            string module,
            string message,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var logMessage = FormatMessage(
                LogLevel.Warning,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(logMessage, "app.log");
        }

        /// <summary>
        /// 记录信息
        /// </summary>
        public static void Info(
            string module,
            string message,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            var logMessage = FormatMessage(
                LogLevel.Info,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(logMessage, "app.log");
        }

        /// <summary>
        /// 记录调试信息
        /// </summary>
        public static void Debug(
            string module,
            string message,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
#if DEBUG
            var logMessage = FormatMessage(
                LogLevel.Debug,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            // 调试信息只输出到控制台，不写文件
            System.Diagnostics.Debug.WriteLine(logMessage);
#endif
        }

        /// <summary>
        /// 格式化日志消息
        /// </summary>
        private static string FormatMessage(
            LogLevel level,
            string module,
            string message,
            Exception? exception,
            string callerMemberName,
            string callerFilePath,
            int callerLineNumber)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var fileName = Path.GetFileName(callerFilePath);
            var location = $"{fileName}:{callerLineNumber} ({callerMemberName})";

            var formattedMessage = $"[{timestamp}] [{level}] [{module}] {message}";

            if (exception != null)
            {
                formattedMessage += $"\n异常类型: {exception.GetType().FullName}";
                formattedMessage += $"\n异常消息: {exception.Message}";
                formattedMessage += $"\n堆栈跟踪:\n{exception.StackTrace}";
                
                if (exception.InnerException != null)
                {
                    formattedMessage += $"\n\n内部异常: {exception.InnerException.GetType().FullName}";
                    formattedMessage += $"\n内部异常消息: {exception.InnerException.Message}";
                    formattedMessage += $"\n内部异常堆栈:\n{exception.InnerException.StackTrace}";
                }
            }

            formattedMessage += $"\n位置: {location}\n";

            return formattedMessage;
        }

        /// <summary>
        /// 写入日志：调用线程只负责入队，落盘由写线程完成。
        /// 本方法在任何情况下都不做磁盘 IO，也不允许阻塞。
        /// </summary>
        private static void WriteLog(string message, string fileName)
        {
            // 总是输出到调试控制台（Release 下等价于近乎无开销）
            System.Diagnostics.Debug.WriteLine(message);

            EnsureWriterStarted();

            try
            {
                if (!_queue.TryAdd(new LogEntry(message, fileName)))
                {
                    // 队列满：丢最旧的一条，给最新的让位。
                    // 积压到这个程度说明磁盘已经不对劲了，绝不能在这里等它。
                    Interlocked.Increment(ref _droppedEntries);
                    try
                    {
                        _queue.TryTake(out _);
                    }
                    catch
                    {
                        // 队列已被 CompleteAdding 之类情况拒绝，忽略
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 队列已关闭（进程正在退出），放弃这条即可
            }
            catch
            {
                // 日志路径上的任何异常都不该外溢到业务代码
            }
        }

        /// <summary>首次写入时惰性起一条串行写线程（长寿命、后台，不阻止进程退出）</summary>
        private static void EnsureWriterStarted()
        {
            if (Interlocked.CompareExchange(ref _writerStarted, 1, 0) != 0)
            {
                return;
            }

            var thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LogService.Writer",
                Priority = ThreadPriority.Lowest
            };
            thread.Start();
        }

        /// <summary>
        /// 唯一的落盘线程。串行消费 ⇒ 天然保证写入顺序，也保证不会有两个线程抢同一个文件。
        /// </summary>
        private static void WriterLoop()
        {
            try
            {
                foreach (LogEntry entry in _queue.GetConsumingEnumerable())
                {
                    // 出队之后、写完之前算「忙」—— Flush 要连这一段一起等，
                    // 否则「Count 已归零」和「最后一行真的落盘」之间还差一次写。
                    Interlocked.Exchange(ref _writerBusy, 1);
                    try
                    {
                        AppendToFile(entry);

                        long dropped = Interlocked.Read(ref _droppedEntries);
                        if (dropped > 0)
                        {
                            // 补一条丢日志告警，避免「文件里什么都没记」变成更难查的第二个坑
                            Interlocked.Exchange(ref _droppedEntries, 0);
                            AppendToFile(new LogEntry(
                                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Warning] [LogService] " +
                                $"写盘跟不上产生速度，此前丢弃 {dropped} 条日志（队列上限 {MaxQueuedEntries}）",
                                "app.log"));
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _writerBusy, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 写线程异常退出: {ex.Message}");
            }
        }

        /// <summary>真正碰磁盘的地方，只在写线程上调用</summary>
        private static void AppendToFile(LogEntry entry)
        {
            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null)
                {
                    return; // 无法创建日志目录，只输出控制台
                }

                var logFilePath = Path.Combine(_logDirectory, entry.FileName);
                File.AppendAllText(logFilePath, entry.Message + Environment.NewLine);
            }
            catch
            {
                // 写入日志失败不应影响程序运行
            }
        }

        /// <summary>
        /// 把队列里已排队的日志压到磁盘上，最多等 timeoutMs。
        ///
        /// 【什么时候必须调】进程要用 Environment.Exit / TerminateProcess 硬退的时候。
        /// 后台线程会被一起带走，不 flush 就等于「崩溃当场的最后几条日志没落盘」，
        /// 而那几条恰恰是排查依据（也是 WER 附件想要的东西）。
        /// 正常退出链同样建议在关闭前调一次，成本只是等几百毫秒内排空。
        /// </summary>
        public static void Flush(int timeoutMs = 2000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            // 两个条件都满足才算排干：队列里没有待写的条目，且写线程不在
            // 「已出队、正在写」的窗口里。只等第一个会把最后一行截在写入途中。
            // 这里只轮询等待，不参与写入，不会和写线程抢文件。
            while ((_queue.Count > 0 || Volatile.Read(ref _writerBusy) == 1) &&
                Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// 清理旧日志（保留最近 N 天）
        /// </summary>
        public static void CleanupOldLogs(int keepDays = 7)
        {
            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null || !Directory.Exists(_logDirectory))
                {
                    return;
                }

                var cutoffDate = DateTime.Now.AddDays(-keepDays);
                var logFiles = Directory.GetFiles(_logDirectory, "*.log");

                foreach (var logFile in logFiles)
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.LastWriteTime < cutoffDate)
                    {
                        try
                        {
                            File.Delete(logFile);
                            System.Diagnostics.Debug.WriteLine($"[LogService] Deleted old log file: {fileInfo.Name}");
                        }
                        catch
                        {
                            // 删除失败，忽略
                        }
                    }
                }
            }
            catch
            {
                // 清理失败不影响程序运行
            }
        }

        /// <summary>
        /// 获取日志目录路径
        /// </summary>
        public static string? GetLogDirectory()
        {
            EnsureLogDirectory();
            return _logDirectory;
        }
    }
}
