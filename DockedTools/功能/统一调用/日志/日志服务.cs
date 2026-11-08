using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace DockedTools.Features.UnifiedCalls.Logging
{
    /// <summary>
    /// 统一日志服务
    ///
    /// 【功能】
    /// 1. 统一的异常记录接口
    /// 2. 自动写入本地日志文件（有大小上限 + 滚动保留，不会无限长）
    /// 3. 同时输出到调试控制台
    /// 4. 支持不同日志级别，且级别可以在**运行期**被外部开关改变（不必重新打包）
    ///
    /// 【使用方法】
    /// LogService.Error("模块名", "操作描述", exception);
    /// LogService.Warning("模块名", "警告信息");
    /// LogService.Info("模块名", "信息");
    /// LogService.Debug("模块名", "调试信息");   // 默认不落盘，见「运行期级别开关」
    ///
    /// 【落盘位置】
    /// LocalState\logs\app.log（Debug/Info/Warning）与 error.log（Error）。
    /// 单文件超过上限后滚成 app.log.1 … app.log.{N}，最旧的一份被替换掉。
    /// 读取请用 脚本工具\读取日志.ps1（边写边读，不会阻塞本服务）。
    ///
    /// 【运行期级别开关】
    /// 往 logs\level.txt 里写 Debug / Info / Warning / Error 之一即可，
    /// 写盘线程最多 5 秒后生效；文件或内容不合法时按 Info。
    /// 之所以用文件而不是应用内设置：用户出问题时报错往往在设置页能打开之前，
    /// 而 CLI / 记事本改一个文件就能让下一次复现带上完整细节。
    /// Debug 档默认只在 Debug 编译下走控制台；开了这个开关才会进日志链路。
    ///
    /// 【Debug 档的内存缓冲】
    /// 开了 Debug 档也不等于逐条落盘：Debug 先进内存环形缓冲，**只有 Error 来了才把
    /// 缓冲吐到磁盘**，并排在那条 Error 前面 —— 现场保住，平时的盘和 IO 都不付。
    /// 这是官方 .NET log buffering 的同一套取舍。吐完之后 30 秒内 Debug 恢复直写。
    /// 想退回「Debug 逐条落盘」的旧行为（例如要实时 tail），往 logs\buffer.txt 写 off。
    /// Info / Warning / Error 不参与缓冲，行为与改造前一致。
    ///
    /// 【为什么全程只有一条写线程】
    /// 旧实现是 File.AppendAllText + 全局 lock，直接在调用线程上同步落盘。
    /// 而 WebView2 的 ProcessFailed 处理器、页面初始化失败路径都在 UI 线程上调
    /// Error()，于是「磁盘停顿 / 杀软扫描 / MSIX 容器里的重定向写入」会经由日志
    /// 把 UI 线程按住 —— 而 UI 线程一停泵，托盘看门狗就判卡死并静默重启。
    /// 日志本身不值得为此冒无响应风险，所以这里改成：调用方只做一次入队，
    /// 真正的磁盘 IO 全部由唯一一条写线程串行完成。
    /// </summary>
    public static class LogService
    {
        private static string? _logDirectory;

        // 【有界队列】宁可丢日志，也不能让积压吃掉内存或在关闭时无限等待。
        // 溢出的取舍是「保 Error、丢普通条目」，理由见 Enqueue；丢掉的条数会记下来，
        // 并在维护轮里补一条告警，免得「文件里什么都没记」变成更难查的第二个坑。
        private const int MaxQueuedEntries = 4096;

        private static readonly BlockingCollection<LogEntry> _queue =
            new(new ConcurrentQueue<LogEntry>(), MaxQueuedEntries);

        /// <summary>写线程只启一次（首次真正写日志时惰性启动，不拖累启动阶段）</summary>
        private static int _writerStarted;

        /// <summary>因队列溢出被丢弃的条数（近似值，只用于告警）</summary>
        private static long _droppedEntries;

        /// <summary>
        /// 写线程是否正处在「已取走条目、还没写完」的那一段（1 = 正在写）。
        /// Flush 必须连这个状态一起等 —— 队列 Count 归零并不等于最后一条已经落盘，
        /// 出队和写完之间有一个窄窗口，进程正好在这里退出就会截断最后一行。
        /// 所以记账放在取用之前；空闲阻塞在 TryTake 上的那 5 秒不算 busy，
        /// 否则 Flush 每次都会白等满超时（它多半跑在 UI 线程的退出链上）。
        /// </summary>
        private static int _writerBusy;

        // ── 有界与滚动 ────────────────────────────────────────────────
        // 官方口径里 .NET 并没有内置的「滚动文件」日志提供程序（内置只有
        // Console / Debug / EventSource / EventLog），要限体积只能自己滚。
        // 单文件上限 5MB、滚动保留 4 份 ⇒ 每种日志最多约 25MB，
        // 托盘应用挂在后台几周也不会把用户的盘撑爆，同时又够留下事故现场。
        // 为什么不是「按天切」：托盘应用常年不重启，按天切等于又一个无界文件。
        private const long MaxLogBytesPerFile = 5L * 1024 * 1024;
        private const int RolledFileCount = 4;

        // 【单条上限必须独立于单文件上限】滚动之后新文件的记账从 0 开始，
        // 所以「一条日志有多大」和「一个文件有多大」是两个必须分别守住的量：
        // 只拿单文件上限（5MB）当裁剪门槛时，一条 3~4.9MB 的日志根本不会被裁，
        // 可它每来一条都要撞一次滚动 —— 4 份历史档几轮之内就被冲干净，
        // 想保住的事故现场恰恰是被它毁掉的。
        // 为什么是 256KB：正常条目（带模块、时间戳、堆栈）多在一两 KB 以内，
        // 一条把整个 JSON 正文回显出来的异常也在几十 KB 量级；256KB 既装得下
        // 完整堆栈 + 内部异常，又保证连着 20 条才够滚一次，历史档不会被单条吃掉。
        private const int MaxLogBytesPerEntry = 256 * 1024;

        // ── Debug 档的内存缓冲 ──────────────────────────────────────────
        // 官方口径把这个矛盾明说了：排查时想要详细日志，又不想为「从没看过的日志」
        // 付存储代价。解法不是砍掉详细档，而是先进内存环形缓冲、出错时再落盘。
        //
        // 【为什么只缓冲 Debug】本机实测过一轮：Release 的 app.log 里 98% 是
        // AotSafeSettings 的逐次读盘流水账，全是 Debug 档；Info/Warning 一次启动
        // 才几十条，把它们也压进缓冲只会让 tail 看不到进展。
        //
        // 【为什么按字符数记账、不算 UTF-8 真实字节】记账发生在调用线程上，
        // 逐条 Encoding.UTF8.GetByteCount 等于给每条日志多扫一遍字符串 ——
        // 而这正是这次要省的开销。两个量都要说清，别混成一谈：
        //   内存这边 .NET 字符串是 UTF-16，2M 字符就是约 4MB，这是缓冲的常驻上限；
        //   落盘那边 UTF-8 最坏 3 字节一码点，所以同一批内容真写出来最多约 6MB，
        //   仍低于 app.log 单档上限（5MB）× 滚动保留数，不会把历史档顶穿。
        private const int MaxBufferedEntryChars = 8000;
        private const int MaxBufferedChars = 2_000_000;
        private const int MaxBufferedEntries = 2048;

        // Error 把缓冲吐完之后，这段时间内 Debug 改为直接落盘。
        // 出错的那一刻人多半正在盯现场，这时再把细节藏回缓冲，
        // 等于在最要紧的半分钟里什么都看不见。
        private const long AutoFlushQuietMs = 30000;

        // 运行期缓冲开关，和 level.txt 同目录、同一轮询周期。
        // 为什么还要多一个文件而不是复用 level.txt：读取日志.ps1 的 -SetLevel Debug
        // 走的是「开了就能立刻 tail」的既有习惯，缓冲一开这条习惯就断了；
        // 真需要实时细节时写一个 off 就退回旧行为，不用重新打包。
        private const string BufferFileName = "buffer.txt";

        // 运行期级别开关所在的文件名（放在日志目录里，和日志一起被导出）。
        private const string LevelFileName = "level.txt";

        /// <summary>旧版本 AotDebugLogger 自己写的文件，现在只作为历史遗留被清理</summary>
        private const string LegacyDebugLogFileName = "debug.log";

        // 写线程空闲时的等待粒度，同时也是 level.txt 的轮询周期。
        // 这里刻意不用 GetConsumingEnumerable：它一阻塞就没人去碰 level.txt，
        // 用户把开关打开后可能长时间不生效。
        //
        // 为什么是 5 秒而不是 1 秒：托盘应用常年挂在后台，这条线程醒一次就去
        // 读一个小文件，1 秒 = 每天 8 万多次无意义唤醒。级别开关是「手动排查」
        // 用的，改完等 5 秒完全能接受，不值得为它牺牲整机的空闲功耗。
        private const int TakeTimeoutMs = 5000;

        // 一轮最多压这么多条就歇一下（顺带补溢出告警），
        // 免得日志洪峰把磁盘一直占在一条超长的批里。
        private const int DrainBatchLimit = 128;

        // 待执行的旧档清理（-1 = 没有待办）。清理只在写线程上做，见 CleanupOldLogs。
        private const int NoPendingCleanup = -1;
        private static int _pendingCleanupDays = NoPendingCleanup;

        // 滚动被外部句柄挡住（例如有人用记事本打开了 app.log）之后的重试冷却。
        // 没有冷却的话，超限期间的每条日志都要白撞一轮「改名 → 拒绝访问」，
        // 磁盘已经不对劲的时候再去放大 IO。冷却期内照旧追加，占用一松开就回到限额内。
        // 只在写线程上读写，不需要原子。
        private const long RollRetryDelayMs = 30000;
        private static long _rollRetryAfterTick;

        // 当前最低落盘级别。默认 Info —— 保持原有行为，不因为这次改造
        // 让既有排查习惯突然少日志。低于该级别的调用连字符串都不拼。
        // 用 int + Volatile 读写：调用线程与写线程都要碰，不能上锁。
        private static int _minimumLevel = (int)LogLevel.Info;

        // ── Debug 缓冲的运行时状态 ──────────────────────────────────────
        // 多线程读写（各条调用线程 + 唯一的写线程），一把只保护内存的小锁。
        // 铁律和 Enqueue 一样：锁里绝不碰磁盘，也不做任何可能等待的事。
        private static readonly object _bufferLock = new();
        private static readonly Queue<string> _buffer = new();

        /// <summary>缓冲内已计账的字符总数（与 _buffer 同步，只在 _bufferLock 内读写）</summary>
        private static int _bufferChars;

        /// <summary>缓冲总开关（1 = 开，buffer.txt 写 off 关掉）。默认开。</summary>
        private static int _bufferEnabled = 1;

        /// <summary>Error 吐完缓冲之后的「直写期」截止 tick（Volatile，长读长写都要原子）</summary>
        private static long _quietUntilTick;

        // ── 只在写线程上访问，除了 Flush ──────────────────────────────
        // Flush 想替写线程把缓冲压下去，必须和它抢同一把锁（TryEnter，等不起就放弃）。
        private static readonly object _ioLock = new();

        // 每个日志文件一条常驻追加流。为什么必须常驻：原先每条日志一次
        // File.AppendAllText，等于每条都开-写-关一遍句柄，MSIX 容器里还要
        // 叠加路径重定向与杀软扫描，放大「磁盘停顿按住 UI 线程」的风险。
        // FileShare.ReadWrite 是硬要求：日志要能被外部 CLI 边写边读（tail）。
        private readonly record struct LogTarget(string FileName, StreamWriter Writer, long BytesWritten);

        private static Dictionary<string, LogTarget>? _openTargets;

        /// <summary>正在写的活文件，清理逻辑绝不碰它们</summary>
        private static readonly HashSet<string> LiveFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "app.log",
            "error.log",
        };

        /// <summary>一条待落盘的日志</summary>
        private readonly struct LogEntry
        {
            public LogEntry(string message, string fileName, bool requiresImmediateFlush)
            {
                Message = message;
                FileName = fileName;
                RequiresImmediateFlush = requiresImmediateFlush;
            }

            public string Message { get; }

            public string FileName { get; }

            /// <summary>
            /// 这条写完必须立刻刷盘。Error 档为真 ——
            /// 缓冲是为省 IO，不是为把崩溃前最后几条留在内存里陪进程一起没。
            /// </summary>
            public bool RequiresImmediateFlush { get; }
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

        /// <summary>当前最低落盘级别（受 logs\level.txt 运行期控制）</summary>
        public static LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

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

            WriteLog(LogLevel.Error, message, "error.log", requiresImmediateFlush: true);
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

            WriteLog(LogLevel.Error, logMessage, "error.log", requiresImmediateFlush: true);
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
            if (!IsEnabled(LogLevel.Warning))
            {
                return;
            }

            var logMessage = FormatMessage(
                LogLevel.Warning,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(LogLevel.Warning, logMessage, "app.log", requiresImmediateFlush: false);
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
            if (!IsEnabled(LogLevel.Info))
            {
                return;
            }

            var logMessage = FormatMessage(
                LogLevel.Info,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            WriteLog(LogLevel.Info, logMessage, "app.log", requiresImmediateFlush: false);
        }

        /// <summary>
        /// 记录调试信息。
        ///
        /// 控制台：Debug 编译下始终输出，保持原来的开发习惯（Release 里近乎无开销）。
        /// 落盘：只有把 logs\level.txt 设成 Debug 才会进入日志链路 —— 官方口径里
        /// Trace 级别不进生产环境，Debug 也是「量大、只给复现现场用」的档，
        /// 默认落盘等于把用户的盘拿来存开发噪音。
        /// 而且进了链路也不等于立刻写盘：默认先进内存缓冲，
        /// 等有 Error 时再整段吐到 app.log（要实时 tail 就往 buffer.txt 写 off）。
        /// </summary>
        public static void Debug(
            string module,
            string message,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            bool toFile = IsEnabled(LogLevel.Debug);

#if DEBUG
            bool toConsole = true;
#else
            bool toConsole = false;
#endif

            if (!toFile && !toConsole)
            {
                return;
            }

            var logMessage = FormatMessage(
                LogLevel.Debug,
                module,
                message,
                null,
                callerMemberName,
                callerFilePath,
                callerLineNumber);

            if (toConsole)
            {
                System.Diagnostics.Debug.WriteLine(logMessage);
            }

            if (toFile)
            {
                // 进内存缓冲，不等 Error；真要立刻看见细节时 buffer.txt 写 off 退回直写
                BufferOrEnqueueDebug(logMessage);
            }
        }

        /// <summary>这条级别现在要不要记录（调用线程无锁读）</summary>
        private static bool IsEnabled(LogLevel level)
        {
            return (int)level >= Volatile.Read(ref _minimumLevel);
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
        /// 判级别 + 出控制台 + 入队。调用线程在任何情况下都不做磁盘 IO，也不允许阻塞。
        /// Error 是最高级别，永远通得过，所以不在这上面加判断。
        /// </summary>
        private static void WriteLog(LogLevel level, string message, string fileName, bool requiresImmediateFlush)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            // 总是输出到调试控制台（Release 下等价于近乎无开销）
            System.Diagnostics.Debug.WriteLine(message);

            // Error 是「出事了」的唯一信号：先把 Debug 缓冲吐进队列，排在它前面，
            // 这条 Error 才有前因可查。顺序天然正确 —— 同一条队列、同一条写线程，
            // 而且 app.log 与 error.log 本来就是两个文件，不存在互相插队的问题。
            if (level == LogLevel.Error)
            {
                FlushBufferToQueue();
            }

            Enqueue(level, message, fileName, requiresImmediateFlush);
        }

        /// <summary>
        /// Debug 档这一条到底怎么走：进内存缓冲，还是直接排队落盘。
        /// 只在调用线程上做内存操作，任何情况下不碰磁盘 —— 和 Enqueue 同一条纪律。
        /// </summary>
        private static void BufferOrEnqueueDebug(string message)
        {
            // 两种情况直写：开关被关掉；或刚被 Error 触发过一次吐盘、还在静默期内
            bool directWrite = Volatile.Read(ref _bufferEnabled) == 0 ||
                Environment.TickCount64 < Volatile.Read(ref _quietUntilTick);

            if (directWrite)
            {
                Enqueue(LogLevel.Debug, message, "app.log", requiresImmediateFlush: false);
                return;
            }

            // 单条过长不进缓冲（官方口径：过大的记录照常 emit）。
            // 超长的那条多半是带正文回显的流水账，直写让它至少留在盘上，
            // 也避免它一条就把字符预算吃光、把真正有用的上下文挤掉。
            if (message.Length > MaxBufferedEntryChars)
            {
                Enqueue(LogLevel.Debug, message, "app.log", requiresImmediateFlush: false);
                return;
            }

            lock (_bufferLock)
            {
                _buffer.Enqueue(message);
                _bufferChars += message.Length;

                // 环形语义：超预算就从最旧的一端丢，保住的一定是「离事故最近的那段」。
                // 至少留 1 条，避免记账和条数两个上限互相顶出死循环。
                while ((_bufferChars > MaxBufferedChars || _buffer.Count > MaxBufferedEntries) &&
                    _buffer.Count > 1)
                {
                    if (_buffer.TryDequeue(out string? stale))
                    {
                        _bufferChars -= stale.Length;
                    }
                }
            }
        }

        /// <summary>
        /// 把 Debug 缓冲吐进落盘队列，排在即将入队的那条 Error 前面。
        ///
        /// 【为什么敢在调用线程上做】这里只把内存里的字符串搬到队列，
        /// 磁盘 IO 仍然全部由唯一的写线程串行完成；Enqueue 用的是非阻塞 TryAdd，
        /// 队列满就丢缓冲条目并计入 _droppedEntries，绝不把调用方（通常是 UI 线程）按住。
        /// 【为什么要留一条说明】缓冲是「按需落盘」的，盘上会突然出现一大段 Debug。
        /// 不写清是谁触发的、上限多少，事后读日志的人只会疑惑这些哪来的。
        /// </summary>
        private static void FlushBufferToQueue()
        {
            string[] snapshot;

            lock (_bufferLock)
            {
                if (_buffer.Count == 0)
                {
                    return;
                }

                snapshot = _buffer.ToArray();
                _buffer.Clear();
                _bufferChars = 0;
            }

            Volatile.Write(ref _quietUntilTick, Environment.TickCount64 + AutoFlushQuietMs);

            // 出锁再入队：Enqueue 会碰队列自己的锁，不能和 _bufferLock 套在一起。
            foreach (var message in snapshot)
            {
                Enqueue(LogLevel.Debug, message, "app.log", requiresImmediateFlush: false);
            }

            Enqueue(
                LogLevel.Debug,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Debug] [LogService] " +
                $"以上是出错前排入内存缓冲的 {snapshot.Length} 条 Debug 现场" +
                $"（缓冲上限 {MaxBufferedEntries} 条 / 约 {MaxBufferedChars / 1000000}M 字符），" +
                $"此后 {AutoFlushQuietMs / 1000} 秒内的 Debug 直接落盘",
                "app.log",
                requiresImmediateFlush: false);
        }

        /// <summary>
        /// 入队（队列满时按「保 Error、丢普通条目」取舍，绝不等待）。
        /// level 只用来决定溢出时该丢谁，落盘级别判断在 WriteLog 里已经做过。
        /// </summary>
        private static void Enqueue(
            LogLevel level,
            string message,
            string fileName,
            bool requiresImmediateFlush)
        {
            EnsureWriterStarted();

            var entry = new LogEntry(message, fileName, requiresImmediateFlush);

            try
            {
                if (_queue.TryAdd(entry))
                {
                    return;
                }

                // 官方口径：有界队列已满时 TryAdd(T) 立即返回 false 且**不会**添加项，
                // 所以想让新这条进队，必须自己腾位。
                //
                // 【为什么要按级别取舍】磁盘停顿期间涌进来的多半是 Debug/Info 流水账，
                // 而堵在队头等不到落盘的，往往正是崩溃前那几条 Error —— 它们是这次
                // 改造最想保住的东西，却会被后面的流水账一条条挤出队列。所以：
                //
                // 1) 来的是 Error ⇒ 腾掉队头再把这条放进来。被腾掉的也可能是 Error，
                //    这是有意的：队列已经满到挤不进第二条 Error 时，紧挨崩溃的那条比
                //    最早的那条更有取证价值。关键是这种淘汰只可能由 Error 触发。
                // 2) 来的是普通条目 ⇒ 直接丢新来的这条，绝不碰队头。
                //    为什么不是「一直丢队头直到找到非 Error」：BlockingCollection 只能
                //    从头部取，取出来的 Error 只能回到队尾，error.log 的时间顺序就被
                //    打乱了；为一条 Debug 牺牲 Error 的顺序不划算。
                //    代价是说清楚过的：长时间停顿期间，队列保持满状态，后续普通条目
                //    会连续被丢（原先是丢最旧、保最新）。这部分靠 _droppedEntries 告警
                //    留下痕迹，不至于静默。
                if (level == LogLevel.Error)
                {
                    if (_queue.TryTake(out _))
                    {
                        Interlocked.Increment(ref _droppedEntries);
                    }

                    if (!_queue.TryAdd(entry))
                    {
                        // 多个线程同时在挤这一位，重试仍可能落空
                        Interlocked.Increment(ref _droppedEntries);
                    }

                    return;
                }

                Interlocked.Increment(ref _droppedEntries);
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
        ///
        /// 用「带超时的 TryTake」而不是 GetConsumingEnumerable：空闲的这段时间正好用来
        /// 轮询 level.txt 和执行待办的清理，运行期级别开关才可能生效。
        ///
        /// 维护动作（补丢日志告警 / 读 level.txt / 清理旧档）按**时间**调度，不看写了多少条：
        /// 日志洪峰下 TryTake 会立刻返回，用条数计数的话轮询会被挤到不知多久以后才发生。
        /// </summary>
        private static void WriterLoop()
        {
            int entriesSinceBatchReport = 0;
            long nextMaintenanceAt = Environment.TickCount64;

            while (true)
            {
                bool gotEntry;
                LogEntry entry;
                try
                {
                    // busy 必须在条目离开队列**之前**记账，否则 Flush 会看见
                    // 「Count 已归零、条目却还在写线程手里」的空隙，最后一行就可能截在缓冲里。
                    // 本服务只有一条写线程（生产者从不 Take），所以 Count>0 时零超时取用必成。
                    // 反过来，空闲那 5 秒绝不能挂着 busy —— Flush 是在 UI 线程上等它的，
                    // 挂上去就等于每次退出都白等满一遍超时。
                    if (_queue.Count > 0)
                    {
                        Interlocked.Exchange(ref _writerBusy, 1);
                        gotEntry = _queue.TryTake(out entry, 0);
                        if (!gotEntry)
                        {
                            Interlocked.Exchange(ref _writerBusy, 0);
                        }
                    }
                    else
                    {
                        gotEntry = _queue.TryTake(out entry, TakeTimeoutMs);
                        if (gotEntry)
                        {
                            Interlocked.Exchange(ref _writerBusy, 1);
                        }
                    }

                    try
                    {
                        if (gotEntry)
                        {
                            int written = 0;
                            do
                            {
                                AppendToFile(entry);
                                written++;
                                entriesSinceBatchReport++;

                                // 溢出告警：避免「文件里什么都没记」变成更难查的第二个坑
                                if (entriesSinceBatchReport >= DrainBatchLimit)
                                {
                                    ReportDroppedEntries();
                                    entriesSinceBatchReport = 0;
                                }
                            }
                            while (written < DrainBatchLimit && _queue.TryTake(out entry, 0));
                        }
                        else
                        {
                            // 队列空了 ⇒ 把 StreamWriter 缓冲压下去，
                            // 这样 tail 到的最后一行不会卡在内存里。
                            // 必须持锁：外部的 Flush(timeoutMs) 也会抢同一批流去刷，
                            // 两个线程同时 StreamWriter.Flush() 是会互相踩坏内部状态的。
                            lock (_ioLock)
                            {
                                FlushStreamsLocked();
                            }
                        }

                        if (!gotEntry || Environment.TickCount64 >= nextMaintenanceAt)
                        {
                            ReportDroppedEntries();
                            PollLevelFile();
                            PollBufferFile();
                            RunPendingCleanup();
                            nextMaintenanceAt = Environment.TickCount64 + TakeTimeoutMs;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _writerBusy, 0);
                    }
                }
                catch (InvalidOperationException)
                {
                    // 队列被标记完成（进程正在收尾）—— 没有更多日志了
                    break;
                }
                catch (Exception ex)
                {
                    // 写线程一旦死掉，之后所有日志都会静默堆积在队列里，
                    // 所以这里只报告不退出；真出问题的话下一轮大概率能继续写。
                    System.Diagnostics.Debug.WriteLine($"[LogService] 写线程本轮异常: {ex.Message}");
                    Thread.Sleep(TakeTimeoutMs);
                }
            }
        }

        /// <summary>补一条丢日志告警（只在写线程上调用）</summary>
        private static void ReportDroppedEntries()
        {
            long dropped = Interlocked.Read(ref _droppedEntries);
            if (dropped <= 0)
            {
                return;
            }

            Interlocked.Exchange(ref _droppedEntries, 0);
            AppendToFile(new LogEntry(
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Warning] [LogService] " +
                $"写盘跟不上产生速度，此前丢弃 {dropped} 条日志（队列上限 {MaxQueuedEntries}）",
                "app.log",
                requiresImmediateFlush: false));
        }

        /// <summary>真正碰磁盘的地方。内部自带 _ioLock，调用方不需要再加锁。</summary>
        private static void AppendToFile(LogEntry entry)
        {
            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null)
                {
                    return; // 无法创建日志目录，只输出控制台
                }

                lock (_ioLock)
                {
                    var targets = _openTargets ??= new Dictionary<string, LogTarget>(StringComparer.OrdinalIgnoreCase);

                    if (!targets.TryGetValue(entry.FileName, out var target))
                    {
                        var opened = OpenTarget(entry.FileName);
                        if (opened == null)
                        {
                            return;
                        }

                        target = opened.Value;
                        targets[entry.FileName] = target;
                    }

                    // 用字节数而不是字符数判上限：中文一条日志 UTF-8 是 3 字节/字，
                    // 按字符算会把体积估小三分之一。
                    int payloadBytes = Encoding.UTF8.GetByteCount(entry.Message) + Environment.NewLine.Length;

                    // 先按**单条**上限裁，再按**单文件**上限判滚动 —— 两个量不能混用，
                    // 理由见 MaxLogBytesPerEntry 的注释。裁剪后的重算只遍历那 15000 字符，
                    // 不会再碰原始的大串。
                    if (payloadBytes > MaxLogBytesPerEntry)
                    {
                        entry = ClampOversizedEntry(entry, payloadBytes);
                        payloadBytes = Encoding.UTF8.GetByteCount(entry.Message) + Environment.NewLine.Length;
                    }

                    if (target.BytesWritten + payloadBytes > MaxLogBytesPerFile &&
                        Environment.TickCount64 >= _rollRetryAfterTick)
                    {
                        var rotated = RotateTarget(target);
                        if (rotated == null)
                        {
                            return;
                        }

                        target = rotated.Value;

                        // 滚动出来的新句柄必须马上登记：写失败时的清理是按字典找句柄的，
                        // 登记晚了这条就成了没人认领的漏句柄，下一条还会再开一个流写到同一个文件上。
                        targets[entry.FileName] = target;
                    }

                    try
                    {
                        target.Writer.WriteLine(entry.Message);

                        if (entry.RequiresImmediateFlush)
                        {
                            target.Writer.Flush();
                        }

                        targets[entry.FileName] = target with
                        {
                            BytesWritten = target.BytesWritten + payloadBytes,
                        };
                    }
                    catch
                    {
                        // 流已经进入不可用状态，丢掉等下一轮重开，
                        // 不能让一条坏日志把整个写线程带崩。
                        CloseAndRemoveTarget(entry.FileName);
                    }
                }
            }
            catch
            {
                // 写入日志失败不应影响程序运行
            }
        }

        /// <summary>
        /// 单条日志超过 <see cref="MaxLogBytesPerEntry"/> 时，只掐中间。
        ///
        /// 【为什么要截，而不是直接丢】异常消息把整个 JSON 正文回显出来这种事真会发生，
        /// 一条几 MB 的日志只要连着来几条，就能让「每条都触发一次滚动」成立，
        /// 几轮之内把 app.log.1 … .4 全冲掉 —— 为了保住事故现场，代价反而是毁掉全部历史。
        /// 直接丢掉这一条更不行，它多半正是本次要查的那条。
        /// 【为什么留头也留尾】头部是时间戳/级别/模块，尾部那行「位置: 」是读取日志.ps1
        /// 判定条目结束的唯一标志，把它截没了 CLI 就会把后面所有条目连成一条。
        /// 【为什么这里不按字节精算】保留的两段合计不超过 15000 字符，UTF-8 最坏
        /// （全是 3 字节汉字）约 45KB，仍不到单条上限 256KB 的五分之一，
        /// 所以只要保证「远小于上限」就够，真实字节数由调用方重算一次拿到。
        /// </summary>
        private static LogEntry ClampOversizedEntry(LogEntry entry, int originalBytes)
        {
            const int headChars = 12000;
            const int tailChars = 3000;

            var text = entry.Message;
            var marker = $"\n[LogService] 本条原始 {originalBytes / 1024}KB，超过单条上限，中间已省略\n";

            if (text.Length <= headChars + tailChars)
            {
                return entry;
            }

            // 阈值下调后这条路径会被经常走到，正好把「劈开代理对」补上：
            // 半个代理项编成 UTF-8 会变成一个 '?'，排查时看着像正文本身坏了。
            // 留头这边退一格，起尾那边进一格，两头都落在完整的码点上。
            int head = headChars;
            if (char.IsHighSurrogate(text[head - 1]))
            {
                head--;
            }

            int tailStart = text.Length - tailChars;
            if (char.IsLowSurrogate(text[tailStart]))
            {
                tailStart++;
            }

            var clamped = text.Substring(0, head) + marker + text.Substring(tailStart);

            return new LogEntry(clamped, entry.FileName, entry.RequiresImmediateFlush);
        }

        /// <summary>打开（或重开）某个日志文件的常驻追加流；只在写线程 + _ioLock 内调用</summary>
        private static LogTarget? OpenTarget(string fileName)
        {
            try
            {
                var path = Path.Combine(_logDirectory!, fileName);

                // FileMode.Append：重启后接着上次的文件写，不覆盖上一场事故的现场。
                // FileShare.ReadWrite：让外部 CLI 能边写边读（tail / -Follow）。
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096);

                return new LogTarget(fileName, writer, stream.Length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 打开 {fileName} 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 关流 ⇒ 文件名后移一格 ⇒ 重开。
        ///
        /// 滚动失败（通常是外部读者还握着句柄，改名要求文件没有被打开）时降级为
        /// 「继续往原文件追加」：宁可让文件暂时超出上限，也不能把这一条日志弄丢。
        /// 因为重开走 FileMode.Append 并按真实长度记账，下一轮还会再试一次，
        /// 所以占用一松开就自动回到限额内。读取日志的 CLI 用
        /// FileShare.ReadWrite | Delete 打开文件，就是为了不触发这个降级。
        /// </summary>
        private static LogTarget? RotateTarget(LogTarget current)
        {
            CloseAndRemoveTarget(current.FileName);

            if (!TryRollFiles(current.FileName))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LogService] {current.FileName} 已达上限但滚动失败（可能被外部占用），本条改为继续追加");
                _rollRetryAfterTick = Environment.TickCount64 + RollRetryDelayMs;
            }

            return OpenTarget(current.FileName);
        }

        /// <summary>app.log → app.log.1，.1 → .2 …；最旧的一份被替换掉</summary>
        private static bool TryRollFiles(string fileName)
        {
            try
            {
                // 从后往前移，并且用带覆盖的 Move：
                // 先删最旧再改名会在中途少一份文件，覆盖式改名没有这个窗口。
                for (int i = RolledFileCount - 1; i >= 1; i--)
                {
                    var from = Path.Combine(_logDirectory!, $"{fileName}.{i}");
                    if (File.Exists(from))
                    {
                        File.Move(from, Path.Combine(_logDirectory!, $"{fileName}.{i + 1}"), overwrite: true);
                    }
                }

                var live = Path.Combine(_logDirectory!, fileName);
                if (File.Exists(live))
                {
                    File.Move(live, $"{live}.1", overwrite: true);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 滚动 {fileName} 出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>关掉并忘掉某个文件句柄；只在 _ioLock 内调用</summary>
        private static void CloseAndRemoveTarget(string fileName)
        {
            if (_openTargets != null && _openTargets.TryGetValue(fileName, out var target))
            {
                _openTargets.Remove(fileName);
                try
                {
                    target.Writer.Dispose();
                }
                catch
                {
                    // 已经坏了的句柄，关不掉也不影响后面重开
                }
            }
        }

        /// <summary>把所有常驻流的缓冲压下去；调用者必须已经持有 _ioLock</summary>
        private static void FlushStreamsLocked()
        {
            if (_openTargets == null)
            {
                return;
            }

            foreach (var target in _openTargets.Values)
            {
                try
                {
                    // 刷到操作系统就够了：进程崩了不会丢 OS 缓存里的内容，
                    // 只有断电才丢，而那不是日志该管的事。
                    target.Writer.Flush();
                }
                catch
                {
                    // 单个坏流不影响其它文件
                }
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
            if (timeoutMs < 0)
            {
                timeoutMs = 0;
            }

            long deadline = Environment.TickCount64 + timeoutMs;

            // 两个条件都满足才算排干：队列里没有待写的条目，且写线程不在
            // 「已出队、正在写」的窗口里。只等第一个会把最后一行截在写入途中。
            // 这里只轮询等待，不参与写入，不会和写线程抢文件。
            while ((_queue.Count > 0 || Volatile.Read(ref _writerBusy) == 1) &&
                Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }

            // 排干之后把 StreamWriter 缓冲也压下去。拿不到锁说明写线程正被磁盘按住，
            // 这时放弃这次强制刷盘（它自己会写完），绝不能把调用方 —— 通常是 UI 线程 ——
            // 拖进超出预算的等待：这个方法的调用点全在退出链和崩溃路径上。
            // 真正要紧的 Error 行本来就是逐条刷盘的，不依赖这一步。
            long remaining = deadline - Environment.TickCount64;
            bool lockTaken = false;
            try
            {
                lockTaken = Monitor.TryEnter(_ioLock, remaining > 0 ? (int)remaining : 0);
                if (lockTaken)
                {
                    FlushStreamsLocked();
                }
            }
            catch
            {
                // 刷盘失败不该影响退出流程
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_ioLock);
                }
            }
        }

        /// <summary>
        /// 读 logs\level.txt，更新运行期级别。只在写线程上调用。
        ///
        /// 文件不存在 / 内容不合法 / 读不了 ⇒ 一律按 Info：
        /// 排查开关不能反过来变成新的故障来源。
        /// </summary>
        private static void PollLevelFile()
        {
            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null)
                {
                    return;
                }

                var path = Path.Combine(_logDirectory, LevelFileName);
                if (!File.Exists(path))
                {
                    Volatile.Write(ref _minimumLevel, (int)LogLevel.Info);
                    return;
                }

                string text;
                // FileShare.ReadWrite：CLI 或记事本正在写的时候也要能读到已经落下的部分
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    text = reader.ReadToEnd();
                }

                ApplyLevelText(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 读取 {LevelFileName} 失败: {ex.Message}");
            }
        }

        /// <summary>解析级别文本（去掉 BOM/空白），只在写线程上调用</summary>
        private static void ApplyLevelText(string? text)
        {
            LogLevel parsed = LogLevel.Info;

            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim().TrimStart('\uFEFF').Trim();

                if (Enum.TryParse(trimmed, ignoreCase: true, out LogLevel candidate) &&
                    Enum.IsDefined(typeof(LogLevel), candidate))
                {
                    parsed = candidate;
                }
            }

            int previous = Interlocked.Exchange(ref _minimumLevel, (int)parsed);
            if (previous != (int)parsed)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 落盘级别已切换为 {parsed}");
            }
        }

        /// <summary>级别文件的完整路径，便于诊断信息和 CLI 提示</summary>
        public static string? GetLevelFilePath()
        {
            var directory = GetLogDirectory();
            return directory == null ? null : Path.Combine(directory, LevelFileName);
        }

        /// <summary>
        /// 读 logs\buffer.txt，更新 Debug 缓冲开关。只在写线程上调用，和 level.txt 同一轮。
        ///
        /// 文件不存在 / 读不了 ⇒ 回到默认（开缓冲）。这个开关是「出问题时的逃生口」，
        /// 不能因为自己读不到就悄悄切成更吵的那一路 —— 那样日志目录反而会被流水账撑爆。
        /// </summary>
        private static void PollBufferFile()
        {
            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null)
                {
                    return;
                }

                var path = Path.Combine(_logDirectory, BufferFileName);
                if (!File.Exists(path))
                {
                    ApplyBufferText(null);
                    return;
                }

                string text;
                // FileShare.ReadWrite：记事本正开着也能读到已经落下的部分
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    text = reader.ReadToEnd();
                }

                ApplyBufferText(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] 读取 {BufferFileName} 失败: {ex.Message}");
            }
        }

        /// <summary>解析缓冲开关文本：off / false / 0 / 关闭 = 退回直写，其它一律按默认（开）</summary>
        private static void ApplyBufferText(string? text)
        {
            bool enabled = true;

            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim().TrimStart('\uFEFF').Trim();

                if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    trimmed == "0" ||
                    trimmed == "关闭")
                {
                    enabled = false;
                }
            }

            int target = enabled ? 1 : 0;
            int previous = Interlocked.Exchange(ref _bufferEnabled, target);
            if (previous != target)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LogService] Debug 缓冲已{(enabled ? "开启（只有 Error 才落盘）" : "关闭（Debug 逐条落盘）")}");
            }
        }

        /// <summary>缓冲开关文件的完整路径，便于诊断信息和 CLI 提示</summary>
        public static string? GetBufferFilePath()
        {
            var directory = GetLogDirectory();
            return directory == null ? null : Path.Combine(directory, BufferFileName);
        }

        /// <summary>
        /// 清理历史日志（保留最近 N 天）。
        ///
        /// 【为什么不能按「所有 *.log 都按时间删」】
        /// 旧实现就是这样，两个坑：
        /// 1. 托盘应用可能一周不开，app.log 恰好是「没被写过」的活文件，
        ///    于是清理把最后一次事故现场删掉了 —— 而用户回来时最需要它的正是那一刻。
        /// 2. Win32 的 *.log 通配匹配不到 app.log.1 这种滚动档，
        ///    也就是说该限体积的地方一直没被限住。
        /// 现在只删历史档：滚动出来的 .log.1 … .log.N，以及旧版本遗留的 debug.log。
        /// 活文件由滚动自己控制体积，level.txt 是用户手动开关，都不动。
        ///
        /// 【为什么只是登记待办、由写线程去删】
        /// 调用点在启动链上（UI 线程）。直接删就是「调用线程做磁盘 IO」，
        /// 与这个服务「调用方不碰磁盘」的立身之本冲突；更要命的是它和滚动改名的
        /// 是同目录同一批文件，两边无锁就会互相抢：清理正把 app.log.1 删掉，
        /// 滚动那边正要把它改名成 .2，结果历史档白白少一档。
        /// 挪到写线程上做，天然和滚动串行，最多晚一个轮询周期执行。
        /// </summary>
        public static void CleanupOldLogs(int keepDays = 7)
        {
            Volatile.Write(ref _pendingCleanupDays, Math.Max(0, keepDays));

            // 写线程还没起来时（启动阶段一条日志都没写过）先把它唤起，
            // 否则这份待办要等到第一条日志才有人认领，清理一直不执行。
            EnsureWriterStarted();
        }

        /// <summary>执行待办的清理；只在写线程上调用</summary>
        private static void RunPendingCleanup()
        {
            int keepDays = Interlocked.Exchange(ref _pendingCleanupDays, NoPendingCleanup);
            if (keepDays == NoPendingCleanup)
            {
                return;
            }

            try
            {
                EnsureLogDirectory();

                if (_logDirectory == null || !Directory.Exists(_logDirectory))
                {
                    return;
                }

                var cutoffDate = DateTime.Now.AddDays(-keepDays);

                foreach (var logFile in Directory.EnumerateFiles(_logDirectory))
                {
                    var name = Path.GetFileName(logFile);
                    if (!IsRemovableHistoryFile(name))
                    {
                        continue;
                    }

                    try
                    {
                        if (new FileInfo(logFile).LastWriteTime < cutoffDate)
                        {
                            File.Delete(logFile);
                            System.Diagnostics.Debug.WriteLine($"[LogService] Deleted old log file: {name}");
                        }
                    }
                    catch
                    {
                        // 被占用（多半是 CLI 正在 tail）就下次再说
                    }
                }
            }
            catch
            {
                // 清理失败不影响程序运行
            }
        }

        /// <summary>这个文件是不是「可以按年龄删掉的历史档」</summary>
        private static bool IsRemovableHistoryFile(string fileName)
        {
            // 两个运行期开关文件都不是「历史档」：它们的 LastWriteTime 天然会一直变旧
            // （用户设好就不再碰），按年龄清理会把排查设置一起清没。
            if (string.Equals(fileName, LevelFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, BufferFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (LiveFileNames.Contains(fileName))
            {
                return false;
            }

            if (string.Equals(fileName, LegacyDebugLogFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 滚动档必须是「活文件名 + .数字」：只看后缀是不是数字就删，
            // 会把别的模块或用户顺手放进同一个目录的编号文件一起带走。
            int lastDot = fileName.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == fileName.Length - 1)
            {
                return false;
            }

            var stem = fileName.Substring(0, lastDot);
            if (!LiveFileNames.Contains(stem))
            {
                return false;
            }

            var suffix = fileName.Substring(lastDot + 1);
            return int.TryParse(suffix, out int index) && index >= 1 && index <= RolledFileCount;
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
