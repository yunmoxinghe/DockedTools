// 真实 Store（网页应用快捷方式存储.cs）的并发与落盘测试。
//
// 和「桥接后端」那个工程的区别：
//   那边测的是桥接逻辑（Store 用的是内存替身）
//   这边把 Store 源码原样编进来，测的是 Store 自己：
//     - 并发读改写会不会丢更新
//     - 落盘是不是原子的（不会写出半个 JSON）
//
// 覆盖的为什么必须是真源码：丢更新这类 bug 只在「Load 与 Save 之间有真实 IO 间隙」时出现，
// 用替身测出来的绿只能证明调用方写对了，证明不了 Store 自己是对的。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DockedTools.Features.Pages.WebApp.Shared;
using Windows.Storage;

namespace DockedTools.StoreTests
{
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static string _dir = string.Empty;

        private static string FilePath => Path.Combine(_dir, "web-shortcuts.json");

        private static string TempPath => FilePath + ".tmp";

        private static void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                _fail++;
                Console.WriteLine($"  FAIL  {name}{(detail is null ? string.Empty : "  ->  " + detail)}");
            }
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {title} --");
        }

        private static void Clear()
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }

        private static WebAppShortcut Item(int index, byte[]? icon = null)
        {
            return new WebAppShortcut(
                $"id-{index}",
                $"条目 {index}",
                $"https://item{index}.example/",
                icon,
                new KeyboardMappingButtonConfig
                {
                    IsEnabled = index % 2 == 0,
                    Key = Windows.System.VirtualKey.S,
                    Ctrl = true
                });
        }

        private static async Task<int> Main()
        {
            Console.WriteLine("=== 真实 Store 测试：并发读改写 + 落盘原子性 ===");

            _dir = Path.Combine(Path.GetTempPath(), "dockedtools-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ApplicationData.Current.LocalFolder.Path = _dir;
            Console.WriteLine($"        临时目录 {_dir}");

            await TestRoundtripAsync();
            await TestCorruptFileAsync();
            await TestUpdateNoWriteAsync();
            await TestConcurrentUpdateAsync();
            await TestMixedConcurrentAsync();

            Directory.Delete(_dir, recursive: true);

            Console.WriteLine();
            Console.WriteLine($"=== 结果：{_pass} 通过 / {_fail} 失败 ===");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>基础存取：条数、字段、图标字节、键盘配置往返一致</summary>
        private static async Task TestRoundtripAsync()
        {
            Section("基础存取");

            Clear();

            var empty = await WebAppShortcutStore.LoadAsync();
            Check("目录里没文件: Load 返回空列表", empty.Count == 0, empty.Count.ToString());

            var icon = new byte[4096];
            new Random(7).NextBytes(icon);

            var items = new List<WebAppShortcut> { Item(0, icon), Item(1), Item(2) };
            await WebAppShortcutStore.SaveAsync(items);

            Check("Save 后主文件存在", File.Exists(FilePath));
            Check("Save 后不留临时文件", !File.Exists(TempPath));

            var loaded = await WebAppShortcutStore.LoadAsync();
            Check("Load 回 3 条", loaded.Count == 3, loaded.Count.ToString());

            if (loaded.Count == 3)
            {
                Check("Id/Name/Url 往返一致",
                    loaded[0].Id == "id-0" && loaded[0].Name == "条目 0" &&
                    loaded[2].Url == "https://item2.example/");

                var iconItem = loaded[0];
                Check("图标字节往返一致",
                    iconItem.IconBytes is not null && iconItem.IconBytes.SequenceEqual(icon),
                    iconItem.IconBytes is null ? "图标丢了" : $"长度 {iconItem.IconBytes.Length}");

                Check("键盘配置往返一致",
                    loaded[0].LeftButtonConfig is { IsEnabled: true, Ctrl: true } &&
                    loaded[1].LeftButtonConfig is { IsEnabled: false, Ctrl: true });

                Check("没传键盘配置的条目: 读回来是 null（用默认配置兜底）",
                    loaded[0].RightButtonConfig is null);
            }

            // 全量覆盖语义：Save 传 1 条，就是 1 条
            await WebAppShortcutStore.SaveAsync(new List<WebAppShortcut> { Item(9) });
            var overwritten = await WebAppShortcutStore.LoadAsync();
            Check("SaveAsync 是全量覆盖（传 1 条就只剩 1 条）",
                overwritten.Count == 1 && overwritten[0].Id == "id-9",
                overwritten.Count.ToString());
        }

        /// <summary>坏文件：不能崩、不能静默，而且不能把原文件删掉（留抢救余地）</summary>
        private static async Task TestCorruptFileAsync()
        {
            Section("坏文件处理");

            Clear();
            await File.WriteAllTextAsync(FilePath, string.Empty);
            var emptyFile = await WebAppShortcutStore.LoadAsync();
            Check("空文件: 返回空列表不抛", emptyFile.Count == 0);

            Clear();
            await File.WriteAllTextAsync(FilePath, "{这不是 JSON 而且被截断了");
            var before = DockedTools.Features.UnifiedCalls.Logging.LogService.ErrorCount;
            var corrupt = await WebAppShortcutStore.LoadAsync();
            Check("坏 JSON: 返回空列表不抛", corrupt.Count == 0);
            Check("坏 JSON: 记了错误日志（不静默吞掉）",
                DockedTools.Features.UnifiedCalls.Logging.LogService.ErrorCount > before);
            Check("坏 JSON: 原文件没被删掉（留人工抢救余地）", File.Exists(FilePath));

            // 文件里是合法 JSON 但条目缺 id/url，应该被过滤掉而不是原样带出来
            Clear();
            await File.WriteAllTextAsync(
                FilePath,
                "[{\"id\":\"a\",\"url\":\"https://a.example/\"},{\"id\":\"\",\"url\":\"\"},{\"name\":\"没 id 没 url\"}]");
            var filtered = await WebAppShortcutStore.LoadAsync();
            Check("缺 id/url 的脏条目被过滤", filtered.Count == 1 && filtered[0].Id == "a", filtered.Count.ToString());
        }

        /// <summary>UpdateAsync 回调返回 null 表示「不改盘」</summary>
        private static async Task TestUpdateNoWriteAsync()
        {
            Section("UpdateAsync 不写盘分支");

            Clear();
            await WebAppShortcutStore.SaveAsync(new List<WebAppShortcut> { Item(1) });
            var stamp = File.GetLastWriteTimeUtc(FilePath);

            await Task.Delay(20);
            await WebAppShortcutStore.UpdateAsync(_ => null);

            Check("回调返回 null: 文件时间戳没变（确实没写盘）",
                File.GetLastWriteTimeUtc(FilePath) == stamp);
            Check("回调返回 null: 内容没变", (await WebAppShortcutStore.LoadAsync()).Count == 1);
            Check("回调返回 null: 也没留下临时文件", !File.Exists(TempPath));

            var got = await WebAppShortcutStore.UpdateAsync(all => (all, all.Count));
            Check("带返回值的 UpdateAsync: 结果正确带回", got == 1, got.ToString());

            // 这里必须显式写类型：元组里放裸 null 的话 TResult 推断不出来，
            // 重载会掉到「不带返回值」那一个上去，直接编译不过。
            var gotNoWrite = await WebAppShortcutStore.UpdateAsync<int>(
                all => ((IReadOnlyList<WebAppShortcut>?)null, all.Count));
            Check("带返回值且不写盘: 结果照样带回", gotNoWrite == 1, gotNoWrite.ToString());
        }

        /// <summary>
        /// 核心：20 个并发 UpdateAsync，每个人都「读出来 + 加一条 + 写回」。
        /// 锁下沉之前这里只会活下来 1 条。
        /// </summary>
        private static async Task TestConcurrentUpdateAsync()
        {
            Section("并发 UpdateAsync（核心）");

            Clear();

            const int count = 20;
            var tasks = new List<Task>();

            for (var i = 0; i < count; i++)
            {
                var index = i;
                tasks.Add(WebAppShortcutStore.UpdateAsync(all =>
                {
                    var list = all.ToList();
                    list.Add(Item(index));
                    return list;
                }));
            }

            await Task.WhenAll(tasks);

            var loaded = await WebAppShortcutStore.LoadAsync();
            Check($"并发 {count} 次 UpdateAsync: 期望 {count} 条全活",
                loaded.Count == count,
                $"实际 {loaded.Count} 条 —— 少于 {count} 说明读改写被并发覆盖了");

            Check("并发写入的 id 互不重复",
                loaded.Select(s => s.Id).Distinct().Count() == loaded.Count,
                $"去重后 {loaded.Select(s => s.Id).Distinct().Count()} 个");

            var raw = await File.ReadAllTextAsync(FilePath);
            var parses = true;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                parses = doc.RootElement.GetArrayLength() == count;
            }
            catch (JsonException)
            {
                parses = false;
            }

            Check("磁盘上的文件是完整合法 JSON（没有写出半个文件）", parses);
            Check("并发结束后无临时文件残留", !File.Exists(TempPath));
        }

        /// <summary>真实场景：桥接在写的同时 UI 在读，两边都不能崩、不能互相覆盖</summary>
        private static async Task TestMixedConcurrentAsync()
        {
            Section("读写混合并发（桥接写 + UI 读）");

            Clear();
            await WebAppShortcutStore.SaveAsync(new List<WebAppShortcut> { Item(0) });

            const int writers = 12;
            const int readers = 12;

            var tasks = new List<Task>();
            var readErrors = 0;
            var readCounts = new List<int>();

            for (var i = 0; i < writers; i++)
            {
                var index = i + 100;
                tasks.Add(WebAppShortcutStore.UpdateAsync(all =>
                {
                    var list = all.ToList();
                    list.Add(Item(index));
                    return list;
                }));
            }

            for (var i = 0; i < readers; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var snapshot = await WebAppShortcutStore.LoadAsync();
                        lock (readCounts)
                        {
                            readCounts.Add(snapshot.Count);
                        }
                    }
                    catch
                    {
                        System.Threading.Interlocked.Increment(ref readErrors);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Check("并发读没有抛异常", readErrors == 0, $"{readErrors} 次抛异常");
            Check("并发读到的条数都在合理区间（1 ~ 13）",
                readCounts.All(c => c >= 1 && c <= writers + 1),
                string.Join(",", readCounts.OrderBy(c => c)));

            var final = await WebAppShortcutStore.LoadAsync();
            Check($"混合并发后条数正确: 期望 {writers + 1} 条",
                final.Count == writers + 1,
                $"实际 {final.Count} 条");
        }
    }
}
