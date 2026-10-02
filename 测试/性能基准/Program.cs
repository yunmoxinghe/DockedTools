// 桥接 + Store 的性能基准：全真源码（Kestrel + WebSocket + 真 Store 落盘）。
//
// 数字只有在「跑的是真正要发布的那份代码」时才可信，所以这里不用任何替身
// （只 Shims.cs 里把 Windows.Storage 的路径指向临时目录）。
//
// 测什么：
//   1. 冷启动        —— 从 StartAsync 到能接受连接
//   2. 往返延迟      —— 单连接串行 ping，p50 / p95 / p99 / max
//   3. 并发吞吐      —— 多连接并发 ping / add 的 QPS
//   4. 大消息        —— 带 100KB 图标的 add
//   5. Store 直测    —— 不同条数下 Load / Update 的耗时与落盘体积
//   6. 分配          —— 一轮压测的托管分配总量
//
// 跑法：dotnet run -c Release --project 测试/性能基准/Benchmarks.csproj
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.BrowserExtension;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.Logging;
using Windows.Storage;

namespace DockedTools.Benchmarks
{
    internal static class Program
    {
        private const string GoodOrigin = "chrome-extension://benchmark-extension-id";

        private static int _port;
        private static string _dir = string.Empty;

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {title} --");
        }

        private static async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", GoodOrigin);
            await socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{_port}{BridgeConfig.BridgePath}"), CancellationToken.None);
            return socket;
        }

        private static string Req(string id, string method, string? payload = null)
        {
            return $"{{\"kind\":\"req\",\"id\":\"{id}\",\"method\":\"{method}\",\"payload\":{payload ?? "null"}}}";
        }

        /// <summary>发一条并等回包，返回耗时（毫秒）与响应文本</summary>
        private static async Task<string> RoundtripAsync(ClientWebSocket socket, string request)
        {
            var bytes = Encoding.UTF8.GetBytes(request);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            return text.ToString();
        }

        private static void Report(string name, List<double> samplesMs, long? extraBytes = null)
        {
            if (samplesMs.Count == 0)
            {
                Console.WriteLine($"  {name,-34} 无样本");
                return;
            }

            var sorted = samplesMs.OrderBy(x => x).ToList();
            double P(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * sorted.Count))];

            var total = sorted.Sum();
            var qps = total > 0 ? sorted.Count / (total / 1000.0) : 0;

            Console.WriteLine(
                $"  {name,-34} " +
                $"p50={P(0.50),7:F3}ms  p95={P(0.95),7:F3}ms  p99={P(0.99),7:F3}ms  " +
                $"max={sorted[^1],8:F3}ms  QPS={qps,9:F0}");

            if (extraBytes.HasValue)
            {
                Console.WriteLine($"  {"",-34} 分配 {extraBytes.Value / 1024.0 / 1024.0:F2} MB（{extraBytes.Value / (double)sorted.Count:F0} B/次）");
            }
        }

        private static void ReportOnce(string name, double ms, string? note = null)
        {
            Console.WriteLine($"  {name,-34} {ms,9:F2}ms{(note is null ? string.Empty : "  " + note)}");
        }

        private static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== 桥接 + Store 性能基准（全真源码，Release）===");
            Console.WriteLine($"    运行时 {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"    处理器 {Environment.ProcessorCount} 核");

            _dir = Path.Combine(Path.GetTempPath(), "dockedtools-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ApplicationData.Current.LocalFolder.Path = _dir;

            // 只跑写盘拆解：整套基准要几分钟，调这个开关能几秒内拿到单项数据
            if (args.Contains("--write"))
            {
                LogService.Quiet = true;
                await BenchWriteStrategiesAsync();
                Directory.Delete(_dir, recursive: true);
                Console.WriteLine();
                Console.WriteLine("=== 基准结束（仅写盘拆解）===");
                return 0;
            }

            // ---- 1. 冷启动 ----
            Section("冷启动");
            var sw = Stopwatch.StartNew();
            var started = await BridgeService.StartAsync();
            sw.Stop();

            if (!started)
            {
                Console.WriteLine("  StartAsync 失败");
                return 1;
            }

            _port = BridgeService.ActualPort;
            ReportOnce("BridgeService.StartAsync（含 Kestrel 起监听）", sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            var warm = await ConnectAsync();
            sw.Stop();
            ReportOnce("首次 WebSocket 握手", sw.Elapsed.TotalMilliseconds);

            // 日志必须关：桥接每处理一条命令打一行，几千次下来控制台 IO 比被测代码还慢
            LogService.Quiet = true;

            await BenchPingSerialAsync(warm);
            await BenchPingConcurrentAsync();
            await BenchAddAsync();
            await BenchListAsync();
            await BenchMixedReadWriteAsync();
            await BenchLargePayloadAsync();
            await BenchWriteStrategiesAsync();
            await BenchStoreAsync();

            await warm.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            warm.Dispose();

            await BridgeService.StopAsync();
            LogService.Quiet = false;

            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 临时目录删不掉无所谓
            }

            Console.WriteLine();
            Console.WriteLine("=== 基准结束 ===");
            return 0;
        }

        /// <summary>单连接串行 ping：纯协议栈开销（不含磁盘）</summary>
        private static async Task BenchPingSerialAsync(ClientWebSocket socket)
        {
            Section("ping 往返延迟（单连接串行，n=3000，含 500 次预热）");

            for (var i = 0; i < 500; i++)
            {
                await RoundtripAsync(socket, Req("w", "bridge.ping", "{\"echo\":\"warm\"}"));
            }

            var samples = new List<double>(3000);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var sw = new Stopwatch();

            for (var i = 0; i < 3000; i++)
            {
                sw.Restart();
                await RoundtripAsync(socket, Req(i.ToString(), "bridge.ping", "{\"echo\":\"x\"}"));
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            Report("ping 往返", samples, GC.GetTotalAllocatedBytes(precise: false) - allocBefore);
        }

        /// <summary>并发吞吐：多连接同时打</summary>
        private static async Task BenchPingConcurrentAsync()
        {
            Section("ping 并发吞吐（8 连接 × 500 次 = 4000 次）");

            const int conns = 8;
            const int per = 500;

            var sockets = new List<ClientWebSocket>();
            for (var i = 0; i < conns; i++)
            {
                sockets.Add(await ConnectAsync());
            }

            // 预热
            foreach (var s in sockets)
            {
                for (var i = 0; i < 100; i++)
                {
                    await RoundtripAsync(s, Req("w", "bridge.ping", "{\"echo\":\"warm\"}"));
                }
            }

            var samples = new List<double>(conns * per);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var swAll = Stopwatch.StartNew();

            var tasks = new List<Task>();
            foreach (var s in sockets)
            {
                var socket = s;
                tasks.Add(Task.Run(async () =>
                {
                    var local = new List<double>(per);
                    var sw = new Stopwatch();
                    for (var i = 0; i < per; i++)
                    {
                        sw.Restart();
                        await RoundtripAsync(socket, Req(i.ToString(), "bridge.ping", "{\"echo\":\"x\"}"));
                        sw.Stop();
                        local.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    lock (samples)
                    {
                        samples.AddRange(local);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            swAll.Stop();

            Report("ping 并发往返", samples, GC.GetTotalAllocatedBytes(precise: false) - allocBefore);
            Console.WriteLine($"  {"",-34} 墙钟 {swAll.Elapsed.TotalMilliseconds:F0}ms，整体吞吐 {samples.Count / (swAll.Elapsed.TotalMilliseconds / 1000.0):F0} QPS");

            foreach (var s in sockets)
            {
                await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                s.Dispose();
            }
        }

        /// <summary>webapp.add 吞吐：真落盘，最重的一条路径</summary>
        private static async Task BenchAddAsync()
        {
            Section("webapp.add 并发吞吐（8 连接 × 100 次 = 800 次，真落盘）");

            const int conns = 8;
            const int per = 100;

            var sockets = new List<ClientWebSocket>();
            for (var i = 0; i < conns; i++)
            {
                sockets.Add(await ConnectAsync());
            }

            // 预热（这些 URL 会被真的写进去，压测结束后不影响，目录是临时的）
            for (var i = 0; i < 20; i++)
            {
                await RoundtripAsync(sockets[0], Req("w", "webapp.add", $"{{\"url\":\"https://warm{i}.example/\",\"name\":\"W{i}\"}}"));
            }

            var samples = new List<double>(conns * per);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var swAll = Stopwatch.StartNew();

            var tasks = new List<Task>();
            for (var c = 0; c < conns; c++)
            {
                var socket = sockets[c];
                var conn = c;
                tasks.Add(Task.Run(async () =>
                {
                    var local = new List<double>(per);
                    var sw = new Stopwatch();
                    for (var i = 0; i < per; i++)
                    {
                        var url = $"https://c{conn}-{i}.example/";
                        sw.Restart();
                        await RoundtripAsync(socket, Req($"{conn}-{i}", "webapp.add", $"{{\"url\":\"{url}\",\"name\":\"N{conn}-{i}\"}}"));
                        sw.Stop();
                        local.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    lock (samples)
                    {
                        samples.AddRange(local);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            swAll.Stop();

            Report("webapp.add 并发", samples, GC.GetTotalAllocatedBytes(precise: false) - allocBefore);
            Console.WriteLine($"  {"",-34} 墙钟 {swAll.Elapsed.TotalMilliseconds:F0}ms，整体吞吐 {samples.Count / (swAll.Elapsed.TotalMilliseconds / 1000.0):F0} QPS");

            var file = Path.Combine(_dir, "web-shortcuts.json");
            if (File.Exists(file))
            {
                Console.WriteLine($"  {"",-34} 落盘体积 {new FileInfo(file).Length / 1024.0:F1} KB");
            }

            foreach (var s in sockets)
            {
                await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                s.Dispose();
            }
        }

        /// <summary>webapp.list：条目数对序列化的影响</summary>
        private static async Task BenchListAsync()
        {
            Section("webapp.list（当前条目数下的列表延迟，n=500）");

            var socket = await ConnectAsync();

            var count = await WebAppShortcutStore.LoadAsync();
            Console.WriteLine($"  {"",-34} 当前库里 {count.Count} 条");

            for (var i = 0; i < 100; i++)
            {
                await RoundtripAsync(socket, Req("w", "webapp.list"));
            }

            var samples = new List<double>(500);
            var sw = new Stopwatch();
            for (var i = 0; i < 500; i++)
            {
                sw.Restart();
                await RoundtripAsync(socket, Req(i.ToString(), "webapp.list"));
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            Report("webapp.list", samples);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            socket.Dispose();
        }

        /// <summary>
        /// 读写混合：写连接疯狂 add（每次都要落盘），读连接同时 list。
        /// 用来验证「读走快照快路径，不被写盘排队拖住」。
        /// 如果读也被 Gate 卡住，这里的 list 延迟会跟 add 一样烂。
        /// </summary>
        private static async Task BenchMixedReadWriteAsync()
        {
            Section("读写混合（6 连接并发 add + 3 连接并发 list）");

            const int writers = 6;
            const int readers = 3;
            const int per = 60;

            var writeSockets = new List<ClientWebSocket>();
            var readSockets = new List<ClientWebSocket>();
            for (var i = 0; i < writers; i++)
            {
                writeSockets.Add(await ConnectAsync());
            }

            for (var i = 0; i < readers; i++)
            {
                readSockets.Add(await ConnectAsync());
            }

            var writeSamples = new List<double>();
            var readSamples = new List<double>();
            var sw = new Stopwatch();
            var wall = Stopwatch.StartNew();

            var tasks = new List<Task>();

            for (var c = 0; c < writers; c++)
            {
                var socket = writeSockets[c];
                var conn = c;
                tasks.Add(Task.Run(async () =>
                {
                    var local = new List<double>(per);
                    for (var i = 0; i < per; i++)
                    {
                        sw.Restart();
                        await RoundtripAsync(socket, Req($"m{conn}-{i}", "webapp.add",
                            $"{{\"url\":\"https://m{conn}-{i}.example/\",\"name\":\"M\"}}"));
                        sw.Stop();
                        local.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    lock (writeSamples)
                    {
                        writeSamples.AddRange(local);
                    }
                }));
            }

            for (var c = 0; c < readers; c++)
            {
                var socket = readSockets[c];
                var conn = c;
                tasks.Add(Task.Run(async () =>
                {
                    var local = new List<double>(per);
                    for (var i = 0; i < per; i++)
                    {
                        sw.Restart();
                        await RoundtripAsync(socket, Req($"r{conn}-{i}", "webapp.list"));
                        sw.Stop();
                        local.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    lock (readSamples)
                    {
                        readSamples.AddRange(local);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            wall.Stop();

            Report("写侧 webapp.add（被 Gate 串行化，必然排队）", writeSamples);
            Report("读侧 webapp.list（走快照快路径）", readSamples);
            Console.WriteLine($"  {"",-34} 读/写 p50 比值 = {Median(readSamples) / Median(writeSamples):F4}" +
                              "  （越接近 0 说明读越不受写影响）");

            foreach (var s in writeSockets)
            {
                await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                s.Dispose();
            }

            foreach (var s in readSockets)
            {
                await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                s.Dispose();
            }
        }

        private static double Median(List<double> samples)
        {
            if (samples.Count == 0)
            {
                return 0;
            }

            var sorted = samples.OrderBy(x => x).ToList();
            return sorted[sorted.Count / 2];
        }

        /// <summary>大消息：带图标的 add（图标走 base64，JSON 会膨胀）</summary>
        private static async Task BenchLargePayloadAsync()
        {
            Section("大消息：带 100KB 图标的 webapp.add（n=100）");

            var socket = await ConnectAsync();

            // 100KB 随机字节 —— 用固定种子保证可复现，且不可压缩（避免走捷径）
            var icon = new byte[100 * 1024];
            new Random(42).NextBytes(icon);
            var iconBase64 = Convert.ToBase64String(icon);

            for (var i = 0; i < 5; i++)
            {
                await RoundtripAsync(socket, Req("w", "webapp.add",
                    $"{{\"url\":\"https://big-warm{i}.example/\",\"name\":\"BW\",\"iconBase64\":\"{iconBase64}\"}}"));
            }

            var samples = new List<double>(100);
            var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var sw = new Stopwatch();

            for (var i = 0; i < 100; i++)
            {
                sw.Restart();
                await RoundtripAsync(socket, Req($"b{i}", "webapp.add",
                    $"{{\"url\":\"https://big-{i}.example/\",\"name\":\"B{i}\",\"iconBase64\":\"{iconBase64}\"}}"));
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            Report("add + 100KB 图标（入站~137KB）", samples,
                GC.GetTotalAllocatedBytes(precise: false) - allocBefore);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            socket.Dispose();
        }

        /// <summary>
        /// 拆解一次落盘的固定开销。
        /// 基线里 UpdateAsync 写 6KB 就要 52ms，且与条目数无关 —— 典型的固定成本，
        /// 得分清是 CreateDirectory、WriteAllText 还是 File.Move 在拖后腿。
        /// </summary>
        private static async Task BenchWriteStrategiesAsync()
        {
            Section("写盘开销拆解（6KB JSON × 200 次，逐项计时）");

            var dir = Path.Combine(_dir, "wtest");
            Directory.CreateDirectory(dir);

            var payload = new string('x', 6 * 1024);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var target = Path.Combine(dir, "a.json");
            var tmp = target + ".tmp";

            const int n = 200;
            var sw = new Stopwatch();

            async Task<List<double>> Measure(Func<Task> action)
            {
                for (var i = 0; i < 20; i++)
                {
                    await action();
                }

                var s = new List<double>(n);
                for (var i = 0; i < n; i++)
                {
                    sw.Restart();
                    await action();
                    sw.Stop();
                    s.Add(sw.Elapsed.TotalMilliseconds);
                }

                return s;
            }

            Report("A 裸 Directory.CreateDirectory(已存在)",
                await Measure(() =>
                {
                    Directory.CreateDirectory(dir);
                    return Task.CompletedTask;
                }));

            Report("B File.WriteAllTextAsync 直接覆盖",
                await Measure(() => File.WriteAllTextAsync(target, payload)));

            Report("C 写 tmp",
                await Measure(() => File.WriteAllTextAsync(tmp, payload)));

            // 先保证 tmp 和 target 都存在，再单独测 Move 的替换成本
            await File.WriteAllTextAsync(tmp, payload);
            await File.WriteAllTextAsync(target, payload);

            Report("D File.Move(tmp→target, overwrite)",
                await Measure(() =>
                {
                    File.Move(tmp, target, overwrite: true);
                    File.Copy(target, tmp, overwrite: true); // 补回 tmp，让下一轮条件一致
                    return Task.CompletedTask;
                }));

            Report("E File.Replace(tmp→target, 无备份)",
                await Measure(() =>
                {
                    File.Replace(tmp, target, null);
                    File.Copy(target, tmp, overwrite: true);
                    return Task.CompletedTask;
                }));

            Report("F FileStream 覆盖写 + Flush(true)",
                await Measure(async () =>
                {
                    using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await fs.WriteAsync(bytes);
                    await fs.FlushAsync();
                }));

            Report("G 完整 tmp+Move（当前实现）",
                await Measure(async () =>
                {
                    await File.WriteAllTextAsync(tmp, payload);
                    File.Move(tmp, target, overwrite: true);
                }));

            Report("H 完整直接覆盖（优化候选）",
                await Measure(async () =>
                {
                    using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await fs.WriteAsync(bytes);
                }));

            // ---- 下面拆 Store.SaveCoreAsync 的各段，看写盘之外还有多少 ----
            Section("SaveCoreAsync 分段拆解（50 条无图标）");

            var ctx = WebAppShortcutJsonContext.Default;
            var items = Enumerable.Range(0, 50)
                .Select(i => new WebAppShortcut($"id{i}", $"站点{i}", $"https://x{i}.example/", null))
                .ToList();

            var stored = items
                .Select(s => new WebAppShortcutStore.StoredWebAppShortcut
                {
                    Id = s.Id, Name = s.Name, Url = s.Url
                })
                .ToList();

            Report("K ToList 物化",
                await Measure(() =>
                {
                    var _ = items.ToList();
                    return Task.CompletedTask;
                }));

            Report("L 序列化到 byte[]（SerializeToUtf8Bytes）",
                await Measure(() =>
                {
                    var _ = JsonSerializer.SerializeToUtf8Bytes(stored, ctx.ListStoredWebAppShortcut);
                    return Task.CompletedTask;
                }));

            var packed = JsonSerializer.SerializeToUtf8Bytes(stored, ctx.ListStoredWebAppShortcut);

            Report("M 序列化直接进 FileStream（SerializeAsync）",
                await Measure(async () =>
                {
                    using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                    await JsonSerializer.SerializeAsync(fs, stored, ctx.ListStoredWebAppShortcut);
                    await fs.FlushAsync();
                }));

            Report("N FileInfo 取指纹（ReadStamp 的成本）",
                await Measure(() =>
                {
                    var info = new FileInfo(target);
                    var _ = info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 32) : 0;
                    return Task.CompletedTask;
                }));

            Report("O 序列化到 byte[] + WriteAllBytes + Move",
                await Measure(async () =>
                {
                    var b = JsonSerializer.SerializeToUtf8Bytes(stored, ctx.ListStoredWebAppShortcut);
                    await File.WriteAllBytesAsync(tmp, b);
                    File.Move(tmp, target, overwrite: true);
                }));

            // ⚠️ 这里必须用块级 using，不能用 using var：
            // using var 的作用域一直到 lambda 方法体结束，File.Move 执行时句柄还没释放，
            // FileShare.None 下 Windows 会直接抛 "文件被另一个进程占用"。
            Report("P 序列化进流 + Move（旧实现）",
                await Measure(async () =>
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                    {
                        await JsonSerializer.SerializeAsync(fs, stored, ctx.ListStoredWebAppShortcut);
                        await fs.FlushAsync();
                    }

                    File.Move(tmp, target, overwrite: true);
                }));

            Report("Q 序列化到 byte[] + WriteAllBytes + Move（现实现）",
                await Measure(async () =>
                {
                    var b = JsonSerializer.SerializeToUtf8Bytes(stored, ctx.ListStoredWebAppShortcut);
                    await File.WriteAllBytesAsync(tmp, b);
                    File.Move(tmp, target, overwrite: true);
                }));

            // Move 的成本跟文件大小有没有关系？
            // 如果无关，那「A/B 双文件 + 小指针文件」的优化思路就走不通
            var tiny = Path.Combine(dir, "ptr.tmp");
            var tinyTarget = Path.Combine(dir, "ptr.json");
            await File.WriteAllBytesAsync(tinyTarget, new byte[] { 1 });

            Report("R 8 字节 tmp+Move（测 Move 是否与大小无关）",
                await Measure(async () =>
                {
                    await File.WriteAllBytesAsync(tiny, new byte[] { 1 });
                    File.Move(tiny, tinyTarget, overwrite: true);
                }));

            Console.WriteLine($"  {"",-34} （序列化产物 {packed.Length} 字节）");

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 无所谓
            }
        }

        /// <summary>Store 直测：条数与图标体积对读写的影响</summary>
        private static async Task BenchStoreAsync()
        {
            Section("Store 直测（脱离 WebSocket，纯落盘开销）");

            foreach (var (count, iconKb) in new[] { (50, 0), (200, 0), (200, 8), (500, 8) })
            {
                byte[]? icon = iconKb > 0 ? new byte[iconKb * 1024] : null;
                if (icon is not null)
                {
                    new Random(9).NextBytes(icon);
                }

                var items = new List<WebAppShortcut>(count);
                for (var i = 0; i < count; i++)
                {
                    items.Add(new WebAppShortcut($"s{i}", $"站点{i}", $"https://s{i}.example/", icon));
                }

                await WebAppShortcutStore.SaveAsync(items);

                // UpdateAsync 只改一条 —— 这是 UI 保存 / 桥接新增的真实开销
                var samples = new List<double>(100);
                var sw = new Stopwatch();
                for (var i = 0; i < 100; i++)
                {
                    var index = i % count;
                    sw.Restart();
                    await WebAppShortcutStore.UpdateAsync(all =>
                    {
                        var list = all.ToList();
                        list[index] = new WebAppShortcut($"s{index}", $"改名{i}", list[index].Url, icon);
                        return list;
                    });
                    sw.Stop();
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }

                var file = Path.Combine(_dir, "web-shortcuts.json");
                var sizeKb = File.Exists(file) ? new FileInfo(file).Length / 1024.0 : 0;

                Console.WriteLine($"  {count} 条{(iconKb > 0 ? $" / 每条 {iconKb}KB 图标" : " / 无图标")}" +
                                  $"  （文件 {sizeKb:F0} KB）:");
                Report("  UpdateAsync 改一条", samples);

                // 先清一次 GC：上一组可能留下大量垃圾，
                // 不清的话测出来的是 GC 的停顿而不是落盘的开销
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var hot = new List<double>(100);
                for (var i = 0; i < 100; i++)
                {
                    sw.Restart();
                    await WebAppShortcutStore.LoadAsync();
                    sw.Stop();
                    hot.Add(sw.Elapsed.TotalMilliseconds);
                }

                Report("  LoadAsync 命中快照（热）", hot);

                // 冷读：绕过 Store 改一下文件时间戳，让快照指纹对不上，强制走真读盘
                var cold = new List<double>(100);
                for (var i = 0; i < 100; i++)
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1 + i));
                    sw.Restart();
                    await WebAppShortcutStore.LoadAsync();
                    sw.Stop();
                    cold.Add(sw.Elapsed.TotalMilliseconds);
                }

                Report("  LoadAsync 真读盘（冷）", cold);
            }
        }
    }
}
