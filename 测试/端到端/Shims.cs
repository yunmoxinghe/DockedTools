// 替身：真 Store 依赖 Windows.Storage 取本地目录，键盘配置依赖 VirtualKey 枚举。
// 本工程是纯 net10.0 控制台（没有 Windows SDK 引用），所以自己声明同名同形的类型，
// 把产品源码原样编进来。被测对象是桥接协议与 Store 落盘，不依赖这两个类型的真实实现。
//
// 和 测试/性能基准/Shims.cs 的唯一区别：日志默认开着。
// 端到端只跑几十条命令，日志 IO 不影响结果，而且服务端日志是本测试的重要观测手段。
using System;

namespace Windows.Storage
{
    public sealed class StorageFolder
    {
        /// <summary>替身专用：可写的目录路径，测试里指向临时目录</summary>
        public string Path { get; set; } = string.Empty;
    }

    public sealed class ApplicationData
    {
        private static readonly ApplicationData Instance = new();

        public static ApplicationData Current => Instance;

        public StorageFolder LocalFolder { get; } = new();
    }
}

namespace Windows.System
{
    /// <summary>只列出 键盘映射按钮配置.cs 里 switch 用到的那几个键，值对齐 Win32 VK_*</summary>
    public enum VirtualKey
    {
        None = 0,
        Back = 0x08,
        Tab = 0x09,
        Enter = 0x0D,
        Escape = 0x1B,
        Space = 0x20,
        PageUp = 0x21,
        PageDown = 0x22,
        End = 0x23,
        Home = 0x24,
        Left = 0x25,
        Up = 0x26,
        Right = 0x27,
        Down = 0x28,
        Delete = 0x2E,
        F1 = 0x70,
        F2 = 0x71,
        F3 = 0x72,
        F4 = 0x73,
        F5 = 0x74,
        F6 = 0x75,
        F7 = 0x76,
        F8 = 0x77,
        F9 = 0x78,
        F10 = 0x79,
        F11 = 0x7A,
        F12 = 0x7B,
        S = 0x53
    }
}

namespace DockedTools.Features.UnifiedCalls.Logging
{
    public static class LogService
    {
        public static int ErrorCount;

        /// <summary>端到端默认开着（false）。需要静音时设 true。</summary>
        public static bool Quiet { get; set; }

        private static void Write(string level, string module, string message)
        {
            if (Quiet)
            {
                return;
            }

            // 走 stderr：stdout 被 READY/STOPPED 这类协议行独占，Node 靠它同步启动时序。
            Console.Error.WriteLine($"        [{level}] {module}: {message}");
        }

        public static void Debug(string module, string message) => Write("DEBUG", module, message);

        public static void Info(string module, string message) => Write("INFO ", module, message);

        public static void Warning(string module, string message) => Write("WARN ", module, message);

        public static void Error(string module, string message)
        {
            ErrorCount++;
            Write("ERROR", module, message);
        }

        public static void Error(string module, string message, Exception ex)
        {
            ErrorCount++;
            Write("ERROR", module, $"{message} | {ex.GetType().Name}: {ex.Message}");
        }

        public static void CleanupOldLogs(int days) { }
    }
}
