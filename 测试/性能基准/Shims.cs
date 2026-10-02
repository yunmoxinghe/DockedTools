// 替身：真 Store 只依赖 Windows.Storage（取本地目录）和键盘配置的 VirtualKey 枚举。
// 这个测试工程是纯 net10.0 控制台，没有 Windows SDK 引用，
// 所以自己声明这两个类型把真源码原样编进来。
//
// 注意：这里声明的是「同名同形」的替身，不是 WinRT 本体。
// 被测对象是 Store 的并发与落盘逻辑，不依赖 ApplicationData 的真实实现。
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

        /// <summary>
        /// 压测时必须关掉。
        /// 桥接每处理一条命令就打一行日志，几千次下来控制台 IO 会比被测代码本身还慢，
        /// 测出来的就不是桥接的性能而是 Console.WriteLine 的性能。
        /// </summary>
        public static bool Quiet { get; set; }

        public static void Debug(string module, string message) { }

        public static void Info(string module, string message) { }

        public static void Warning(string module, string message) { }

        public static void Error(string module, string message)
        {
            ErrorCount++;
            if (!Quiet)
            {
                Console.WriteLine($"        [ERROR] {module}: {message}");
            }
        }

        public static void Error(string module, string message, Exception ex)
        {
            ErrorCount++;
            if (!Quiet)
            {
                Console.WriteLine($"        [ERROR] {module}: {message} | {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void CleanupOldLogs(int days) { }
    }
}
