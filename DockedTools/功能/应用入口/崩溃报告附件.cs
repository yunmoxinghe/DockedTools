using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DockedTools.Features.AppEntry
{
    /// <summary>
    /// 把应用自身的日志挂进 Windows 错误报告（WER），让崩溃回传时能带上上下文。
    ///
    /// 【为什么必须有这一层】
    /// Partner Center 的健康报告数据源就是 WER，而 WER 报告里默认只有：异常码、故障模块、
    /// 内存/minidump、以及一堆系统数据。它<b>不会</b>自动带上我们自己写的日志。
    /// 于是 Partner Center 上看到的往往只有一条 Studio Retail Proxy / FailFast 记录和一个
    /// 堆栈 —— 知道崩在哪一行，但不知道崩之前在干什么。
    ///
    /// 本轮实测的那次 0xc0000409（WinAppSDK fail-fast / __fastfail）尤其典型：
    /// __fastfail 不经过 SEH，进程是被内核直接终止的，托管侧
    /// Application.UnhandledException / AppDomain.UnhandledException 一个都跑不到。
    /// 也就是说"进程内自己上报"这条路对这类崩溃是死路，只剩 WER 附件这一条。
    ///
    /// 【官方依据】
    /// Learn · werapi.h · WerRegisterFile：
    ///   "If you use this function to register a file, the operating system will add the
    ///    file to the error report created at the time of a crash or non-response."
    /// 备注里同时写了两条硬约束：
    ///   ① 完整路径不得超过 MAX_PATH；
    ///   ② 已注册的内存块 + 文件数量超限会返回 ERROR_INSUFFICIENT_BUFFER。
    ///
    /// 【它解决不了什么 —— 别指望太多】
    /// 1. 用户必须在「设置 → 隐私和安全性 → 诊断和反馈」里开启<b>可选诊断数据</b>，
    ///    WER 才会把报告发给 Microsoft。没开的话崩溃只留在本地事件日志里，
    ///    Partner Center 永远看不到 —— 这是系统级前提，应用侧无解。
    /// 2. 官方备注写明"只有当服务器请求额外数据时才会真正收集"，所以附件<b>不保证</b>
    ///    每次崩溃都能捞回来。能捞回来就是赚的。
    /// 3. 堆栈能否被还原成函数名/行号，取决于提交包里有没有匹配的 .appxsym 符号，
    ///    跟这里无关 —— 那是 csproj 里 GenerateAppxSymbolPackage 那几行的事。
    /// </summary>
    internal static partial class CrashReportAttachment
    {
        /// <summary>
        /// 单个附件的大小上限（字节）。
        /// 超过就不挂：WER 报告本身有体积约束，塞一个几十 MB 的日志会把整份报告的
        /// 上传拖垮，结果连原本能拿到的 minidump 都丢了，属于捡芝麻丢西瓜。
        /// </summary>
        private const long MaxAttachmentBytes = 512 * 1024;

        /// <summary>Win32 MAX_PATH。WerRegisterFile 明确要求路径不超过这个长度。</summary>
        private const int MaxPath = 260;

        /// <summary>
        /// WER_REGISTER_FILE_TYPE.WerRegFileTypeOther —— 既不属于"当时正在编辑的文档"，
        /// 也不是 WER 自己会默认收的那几类，归到 Other 最贴切。
        /// （与之相对，WerRegFileTypeUserDocument 只在 Watson 服务器显式索取时才收集，
        ///  概率更低。）
        /// </summary>
        private const int WerRegFileTypeOther = 2;

        /// <summary>
        /// 最多注册几个附件。
        /// 官方没给具体数字，但返回 ERROR_INSUFFICIENT_BUFFER 就是数量超了；
        /// 我们只用两个，留一点余量给系统自己和将来可能加的内存块。
        /// </summary>
        private const int AttachCandidateCount = 3;

        private const string AppLogFileName = "app.log";
        private const string ErrorLogFileName = "error.log";

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WerRegisterFile(
            [MarshalAs(UnmanagedType.LPWStr)] string pwzFile,
            int regFileType,
            uint dwFlags);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WerUnregisterFile(
            [MarshalAs(UnmanagedType.LPWStr)] string pwzFile);

        private static int _attachedCount;

        /// <summary>
        /// 把日志目录里值得带走的几个文件注册给 WER。幂等，失败完全静默。
        ///
        /// 【为什么允许失败】
        /// 这只是"崩溃时能多一点线索"的可选增强。它一旦影响启动或者抛异常，
        /// 就成了负资产。所以这里全部 swallow，只在 Debug 输出里留痕。
        /// </summary>
        public static void Attach()
        {
            try
            {
                string? logDirectory = DockedTools.Features.UnifiedCalls.Logging.LogService.GetLogDirectory();
                if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory))
                {
                    System.Diagnostics.Debug.WriteLine("[CrashReportAttachment] 日志目录不可用，跳过 WER 附件注册");
                    return;
                }

                // app.log 记了模块 / 操作 / 异常栈，是复盘崩溃前现场最有用的一份；
                // error.log 单列了所有 Error 级条目，量小但全是关键节点。
                AttachOne(Path.Combine(logDirectory!, AppLogFileName));
                AttachOne(Path.Combine(logDirectory!, ErrorLogFileName));

                System.Diagnostics.Debug.WriteLine(
                    $"[CrashReportAttachment] 已向 WER 注册 {_attachedCount} 个日志附件");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CrashReportAttachment] 注册失败（不影响运行）: {ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>注册单个文件；不存在、太大、路径过长都会安静跳过。</summary>
        private static void AttachOne(string path)
        {
            try
            {
                if (_attachedCount >= AttachCandidateCount)
                {
                    return;
                }

                // 路径长度是官方硬约束，超了直接返回它也不报错 —— 但那等于没注册，
                // 所以这里提前量好，避免后面白等一场。
                if (path.Length > MaxPath)
                {
                    System.Diagnostics.Debug.WriteLine($"[CrashReportAttachment] 路径超过 MAX_PATH，跳过");
                    return;
                }

                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return;
                }

                if (info.Length > MaxAttachmentBytes)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CrashReportAttachment] {Path.GetFileName(path)} 已达 {info.Length / 1024}KB，超过上限不上报");
                    return;
                }

                if (info.Length == 0)
                {
                    // 空文件上传上去没有意义，还会平白占用报告配额
                    return;
                }

                // dwFlags 传 0：不声明 Anonymous。日志里可能夹着网页标题、
                // 用户起的快捷方式名字这类半个人信息，标记为"绝对匿名"是不诚实的。
                if (!WerRegisterFile(path, WerRegFileTypeOther, 0))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CrashReportAttachment] WerRegisterFile 返回 false: {Path.GetFileName(path)}");
                    return;
                }

                _attachedCount++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CrashReportAttachment] 注册 {Path.GetFileName(path)} 失败: {ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>
        /// 退出时解除注册。
        ///
        /// 不是必须的（进程没了注册自然失效），但退出流程里主动摘掉更干净：
        /// 尤其我们有一条 Environment.Exit 的硬兜底路径，那之后 WER 仍可能对
        /// 残留的注册状态做一次数据收集，绕开一个不必要的文件读取。
        /// </summary>
        public static void Detach()
        {
            if (_attachedCount == 0)
            {
                return;
            }

            try
            {
                string? logDirectory = DockedTools.Features.UnifiedCalls.Logging.LogService.GetLogDirectory();
                if (string.IsNullOrEmpty(logDirectory))
                {
                    return;
                }

                WerUnregisterFile(Path.Combine(logDirectory!, AppLogFileName));
                WerUnregisterFile(Path.Combine(logDirectory!, ErrorLogFileName));
                _attachedCount = 0;
            }
            catch
            {
                // 退出路径上不值得为它兜任何东西
            }
        }
    }
}
