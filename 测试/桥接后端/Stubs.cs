// 替身：桥接源码只依赖这三个外部类型（日志 + 网页应用条目 + 存储），
// 用最小实现顶上就能让桥接层脱离 WinUI 主工程单独跑起来。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DockedTools.Features.UnifiedCalls.Logging
{
    public static class LogService
    {
        /// <summary>测试时只看桥接模块的日志，别的一概忽略</summary>
        public static bool Quiet { get; set; }

        public static void Debug(string module, string message) => Write("DEBUG", module, message);

        public static void Info(string module, string message) => Write("INFO ", module, message);

        public static void Warning(string module, string message) => Write("WARN ", module, message);

        public static void Error(string module, string message) => Write("ERROR", module, message);

        public static void Error(string module, string message, Exception ex) =>
            Write("ERROR", module, $"{message} | {ex.GetType().Name}: {ex.Message}");

        public static void CleanupOldLogs(int days) { }

        private static void Write(string level, string module, string message)
        {
            if (!Quiet)
            {
                Console.WriteLine($"        [{level}] {module}: {message}");
            }
        }
    }
}

namespace DockedTools.Features.Pages.WebApp.Shared
{
    public sealed class WebAppShortcut
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public byte[]? IconBytes { get; set; }

        public WebAppShortcut(string id, string name, string url, byte[]? iconBytes)
        {
            Id = id;
            Name = name;
            Url = url;
            IconBytes = iconBytes;
        }
    }

    /// <summary>
    /// 内存版 Store，逐条对齐真 Store（网页应用快捷方式存储.cs）的并发语义：
    ///   - Load / Save 各自持锁，但「读完再写」这一段是裸的 → 照样会丢更新
    ///   - UpdateAsync 持锁跑完「读 → 改 → 写」→ 不会丢更新
    /// 桥接层只能靠后者保证正确性，所以这个替身必须把它实现出来，测试才有意义。
    /// </summary>
    public static class WebAppShortcutStore
    {
        private static readonly List<WebAppShortcut> Items = new();
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>
        /// ⚠️ 故意加上 IO 延迟：真 Store 要读磁盘，Load 与 Save 之间是有真实异步间隙的。
        /// 不加延迟的话 Load/Save 会同步完成，并发竞态根本暴露不出来，测试就成了假绿。
        /// </summary>
        public static int IoDelayMs { get; set; } = 25;

        public static async Task<IReadOnlyList<WebAppShortcut>> LoadAsync()
        {
            await Gate.WaitAsync();
            try
            {
                return await LoadCoreAsync();
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task SaveAsync(IEnumerable<WebAppShortcut> items)
        {
            await Gate.WaitAsync();
            try
            {
                await SaveCoreAsync(items);
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task UpdateAsync(
            Func<IReadOnlyList<WebAppShortcut>, IReadOnlyList<WebAppShortcut>?> mutate)
        {
            await Gate.WaitAsync();
            try
            {
                var current = await LoadCoreAsync();
                var next = mutate(current);
                if (next is not null)
                {
                    await SaveCoreAsync(next);
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<TResult> UpdateAsync<TResult>(
            Func<IReadOnlyList<WebAppShortcut>, (IReadOnlyList<WebAppShortcut>? Items, TResult Result)> mutate)
        {
            await Gate.WaitAsync();
            try
            {
                var current = await LoadCoreAsync();
                var (next, result) = mutate(current);
                if (next is not null)
                {
                    await SaveCoreAsync(next);
                }

                return result;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<IReadOnlyList<WebAppShortcut>> LoadCoreAsync()
        {
            await Task.Delay(IoDelayMs);
            lock (Items)
            {
                return Items.ToList();
            }
        }

        private static async Task SaveCoreAsync(IEnumerable<WebAppShortcut> items)
        {
            await Task.Delay(IoDelayMs);
            var snapshot = items.ToList();
            lock (Items)
            {
                Items.Clear();
                Items.AddRange(snapshot);
            }
        }

        /// <summary>仅供测试：把内存清空，让各用例之间互不污染</summary>
        public static void ResetForTest()
        {
            lock (Items)
            {
                Items.Clear();
            }
        }
    }
}
