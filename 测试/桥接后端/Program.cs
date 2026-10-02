// 桥接后端功能测试：源码直编 + 真 WebSocket 客户端打真实端口。
//
// 覆盖：服务启停、四个命令、错误码、数据边界、健壮性、Origin 安全、并发竞态。
// 加 --serve 参数会在跑完测试后继续监听，方便用真浏览器扩展做端到端验证。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.BrowserExtension;
using DockedTools.Features.Pages.WebApp.Shared;

namespace DockedTools.BridgeTests
{
    internal static class Program
    {
        private const string GoodOrigin = "chrome-extension://fjmgihadikbchjepjmfkpoiooeicoicp";

        private static int _pass;
        private static int _fail;
        private static int _port;

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

        private static async Task<ClientWebSocket?> ConnectAsync(string? origin)
        {
            var socket = new ClientWebSocket();
            if (origin is not null)
            {
                socket.Options.SetRequestHeader("Origin", origin);
            }

            try
            {
                await socket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{_port}{BridgeConfig.BridgePath}"),
                    CancellationToken.None);
                return socket;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        （连接被拒: {ex.GetType().Name}: {ex.Message}）");
                return null;
            }
        }

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

        private static string Req(string id, string method, string? payload = null)
        {
            var body = payload is null ? "null" : payload;
            return $"{{\"kind\":\"req\",\"id\":\"{id}\",\"method\":\"{method}\",\"payload\":{body}}}";
        }

        private static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== 桥接后端测试：源码直编，脱离 WinUI 主工程 ===");

            var started = await BridgeService.StartAsync();
            Check("BridgeService.StartAsync 成功", started);
            if (!started)
            {
                return 1;
            }

            _port = BridgeService.ActualPort;
            Check(
                "实际端口落在 17829~17839",
                _port >= BridgeConfig.BasePort && _port < BridgeConfig.BasePort + BridgeConfig.PortRange,
                _port.ToString());
            Console.WriteLine($"        监听 ws://127.0.0.1:{_port}{BridgeConfig.BridgePath}");

            var socket = await ConnectAsync(GoodOrigin);
            Check("扩展 Origin 握手放行", socket is not null);
            if (socket is null)
            {
                return 1;
            }

            await TestProtocolAsync(socket);
            await TestDataBoundaryAsync(socket);
            await TestRobustnessAsync(socket);
            await TestOriginAsync();
            await TestConcurrencyAsync();
            await TestRawLoadSaveHazardAsync();

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            if (Array.IndexOf(args, "--serve") >= 0)
            {
                Console.WriteLine();
                Console.WriteLine($">>> 服务模式：保持监听 120 秒，可用浏览器扩展连 ws://127.0.0.1:{_port}{BridgeConfig.BridgePath}");
                await Task.Delay(TimeSpan.FromSeconds(120));
            }

            await BridgeService.StopAsync();
            Check("StopAsync 后 IsRunning == false", !BridgeService.IsRunning);

            Console.WriteLine();
            Console.WriteLine($"=== 结果：{_pass} 通过 / {_fail} 失败 ===");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>协议与四个命令</summary>
        private static async Task TestProtocolAsync(ClientWebSocket socket)
        {
            Section("协议与命令");

            var hello = await RoundtripAsync(socket, Req("1", "bridge.hello"));
            using (var doc = JsonDocument.Parse(hello))
            {
                var payload = doc.RootElement.GetProperty("payload");
                var methods = payload.GetProperty("methods");
                Check("hello: protocolVersion == 1", payload.GetProperty("protocolVersion").GetInt32() == 1);
                Check("hello: 返回 4 个方法", methods.GetArrayLength() == 4, methods.GetArrayLength().ToString());
                Check("hello: appName 正确", payload.GetProperty("appName").GetString() == "边栏助手");
                Check("hello: 端口号回传", payload.GetProperty("port").GetInt32() == _port);
            }

            var ping = await RoundtripAsync(socket, Req("2", "bridge.ping", "{\"echo\":\"狼崽子\"}"));
            using (var doc = JsonDocument.Parse(ping))
            {
                var echo = doc.RootElement.GetProperty("payload").GetProperty("echo").GetString();
                Check("ping: echo 原样回显", echo == "狼崽子", echo);
            }

            WebAppShortcutStore.ResetForTest();

            var add = await RoundtripAsync(
                socket,
                Req("3", "webapp.add", "{\"url\":\"https://example.com\",\"name\":\"Example Domain\"}"));
            using (var doc = JsonDocument.Parse(add))
            {
                var payload = doc.RootElement.GetProperty("payload");
                Check("webapp.add 新增: duplicate == false", !payload.GetProperty("duplicate").GetBoolean());
                Check("webapp.add 新增: name 正确", payload.GetProperty("name").GetString() == "Example Domain");
            }

            var list = await RoundtripAsync(socket, Req("4", "webapp.list"));
            using (var doc = JsonDocument.Parse(list))
            {
                var items = doc.RootElement.GetProperty("payload");
                Check("webapp.list: 1 条", items.GetArrayLength() == 1, items.GetArrayLength().ToString());
            }
        }

        /// <summary>数据边界：图标、URL 规范化、大小写去重、名称兜底、中文</summary>
        private static async Task TestDataBoundaryAsync(ClientWebSocket socket)
        {
            Section("数据边界");

            WebAppShortcutStore.ResetForTest();

            // 1x1 PNG 的最小合法 base64
            const string tinyPng =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

            var withIcon = await RoundtripAsync(
                socket,
                Req("10", "webapp.add",
                    $"{{\"url\":\"https://a.example/\",\"name\":\"带图标\",\"iconBase64\":\"{tinyPng}\"}}"));
            using (var doc = JsonDocument.Parse(withIcon))
            {
                Check("带图标新增: duplicate == false", !doc.RootElement.GetProperty("payload").GetProperty("duplicate").GetBoolean());
            }

            var badIcon = await RoundtripAsync(
                socket,
                Req("11", "webapp.add", "{\"url\":\"https://b.example/\",\"name\":\"坏图标\",\"iconBase64\":\"!!!不是base64!!!\"}"));
            using (var doc = JsonDocument.Parse(badIcon))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("非法 base64: 返回 INVALID_ICON", code == "INVALID_ICON", code);
            }

            var dup = await RoundtripAsync(
                socket,
                Req("12", "webapp.add", "{\"url\":\"HTTPS://A.EXAMPLE/\",\"name\":\"大小写不同\"}"));
            using (var doc = JsonDocument.Parse(dup))
            {
                var payload = doc.RootElement.GetProperty("payload");
                Check("URL 大小写不同: 仍判重复（OrdinalIgnoreCase）", payload.GetProperty("duplicate").GetBoolean());
            }

            var noName = await RoundtripAsync(socket, Req("13", "webapp.add", "{\"url\":\"https://c.example/\"}"));
            using (var doc = JsonDocument.Parse(noName))
            {
                var name = doc.RootElement.GetProperty("payload").GetProperty("name").GetString();
                Check("不传 name: 回落成域名", name == "c.example", name);
            }

            var httpOk = await RoundtripAsync(socket, Req("14", "webapp.add", "{\"url\":\"http://d.example/\",\"name\":\"纯 http\"}"));
            using (var doc = JsonDocument.Parse(httpOk))
            {
                Check("http:// 也算合法", !doc.RootElement.GetProperty("payload").GetProperty("duplicate").GetBoolean());
            }

            var chinese = await RoundtripAsync(
                socket,
                Req("15", "webapp.add", "{\"url\":\"https://e.example/\",\"name\":\"中文名 测试 · 狼崽子\"}"));

            var listWithIcon = await RoundtripAsync(socket, Req("16", "webapp.list"));
            using (var doc = JsonDocument.Parse(listWithIcon))
            {
                var items = doc.RootElement.GetProperty("payload");
                Check("webapp.list: 4 条（去重后）", items.GetArrayLength() == 4, items.GetArrayLength().ToString());

                var foundIcon = false;
                var foundChinese = false;
                var noIconCount = 0;

                foreach (var item in items.EnumerateArray())
                {
                    var hasIcon = item.GetProperty("hasIcon").GetBoolean();
                    var name = item.GetProperty("name").GetString();

                    if (hasIcon)
                    {
                        foundIcon = true;
                    }
                    else
                    {
                        noIconCount++;
                    }

                    if (name == "中文名 测试 · 狼崽子")
                    {
                        foundChinese = true;
                    }
                }

                Check("图标: 带图标的条目 hasIcon == true", foundIcon);
                Check("图标: 其余条目 hasIcon == false", noIconCount == 3, noIconCount.ToString());
                Check("中文名: 存取不乱码", foundChinese);
            }

            _ = chinese;
        }

        /// <summary>健壮性：坏 JSON、未知方法、错误码、二进制帧、无 id、event 帧、超大消息</summary>
        private static async Task TestRobustnessAsync(ClientWebSocket socket)
        {
            Section("健壮性");

            var badJson = await RoundtripAsync(socket, "{这不是 JSON");
            using (var doc = JsonDocument.Parse(badJson))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("坏 JSON: PARSE_ERROR", code == "PARSE_ERROR", code);
            }

            var unknown = await RoundtripAsync(socket, Req("20", "sidebar.nuke"));
            using (var doc = JsonDocument.Parse(unknown))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("未知方法: METHOD_NOT_FOUND", code == "METHOD_NOT_FOUND", code);
            }

            var badUrl = await RoundtripAsync(socket, Req("21", "webapp.add", "{\"url\":\"ftp://hack.example\"}"));
            using (var doc = JsonDocument.Parse(badUrl))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("非 http(s) URL: INVALID_URL", code == "INVALID_URL", code);
            }

            var noUrl = await RoundtripAsync(socket, Req("22", "webapp.add"));
            using (var doc = JsonDocument.Parse(noUrl))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("缺 url: INVALID_ARGUMENT", code == "INVALID_ARGUMENT", code);
            }

            var noMethod = await RoundtripAsync(socket, "{\"kind\":\"req\",\"id\":\"23\"}");
            using (var doc = JsonDocument.Parse(noMethod))
            {
                var code = doc.RootElement.GetProperty("code").GetString();
                Check("缺 method: BAD_REQUEST", code == "BAD_REQUEST", code);
            }

            // 二进制帧：服务端记为「暂未处理」并继续，连接不该断
            var binary = Encoding.UTF8.GetBytes(new string('x', 64));
            await socket.SendAsync(new ArraySegment<byte>(binary), WebSocketMessageType.Binary, true, CancellationToken.None);
            var afterBinary = await RoundtripAsync(socket, Req("24", "bridge.ping", "{\"echo\":\"还活着\"}"));
            using (var doc = JsonDocument.Parse(afterBinary))
            {
                var echo = doc.RootElement.GetProperty("payload").GetProperty("echo").GetString();
                Check("二进制帧后连接仍可用", echo == "还活着", echo);
            }

            // 无 id 的请求：响应里不会有 id 字段（null 被忽略）
            var noId = await RoundtripAsync(socket, "{\"kind\":\"req\",\"method\":\"bridge.ping\",\"payload\":{}}");
            using (var doc = JsonDocument.Parse(noId))
            {
                Check("无 id 的请求: 响应没有 id 字段（客户端会配对不上）",
                    !doc.RootElement.TryGetProperty("id", out _));
            }

            // event 帧：协议里不该有响应，但当前实现按 method 照回
            var eventFrame = await RoundtripAsync(socket, "{\"kind\":\"event\",\"method\":\"bridge.ping\",\"payload\":{\"echo\":\"event\"}}");
            using (var doc = JsonDocument.Parse(eventFrame))
            {
                var kind = doc.RootElement.GetProperty("kind").GetString();
                Check("event 帧: 当前实现仍按请求回 res（已知行为，非阻塞）", kind == "res", kind);
            }
        }

        /// <summary>Origin 安全：宽松模式与严格模式</summary>
        private static async Task TestOriginAsync()
        {
            Section("Origin 安全（宽松模式）");

            var evil = await ConnectAsync("http://evil.example.com");
            Check("普通网页 Origin 被 403 拦下", evil is null);
            if (evil is not null)
            {
                await evil.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var moz = await ConnectAsync("moz-extension://1234-5678");
            Check("Firefox 扩展 Origin 放行", moz is not null);
            if (moz is not null)
            {
                await moz.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var noOrigin = await ConnectAsync(null);
            Check("无 Origin：宽松模式仍放行（M0 设定）", noOrigin is not null);
            if (noOrigin is not null)
            {
                await noOrigin.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            Section("Origin 安全（严格模式）");

            BridgeConfig.StrictOriginWhitelist = true;
            BridgeConfig.StrictWhitelist.Clear();
            BridgeConfig.StrictWhitelist.Add("chrome-extension://trusted-extension-id");

            var strictReject = await ConnectAsync(GoodOrigin);
            Check("严格模式：白名单外被拒", strictReject is null);
            if (strictReject is not null)
            {
                await strictReject.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var strictAllow = await ConnectAsync("chrome-extension://trusted-extension-id");
            Check("严格模式：白名单内放行", strictAllow is not null);
            if (strictAllow is not null)
            {
                await strictAllow.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var strictNoOrigin = await ConnectAsync(null);
            Check("严格模式：无 Origin 被拒", strictNoOrigin is null);
            if (strictNoOrigin is not null)
            {
                await strictNoOrigin.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            // Firefox / Safari 的扩展 Origin 含每台机器随机生成的 UUID，
            // 服务端既不能预知也不能枚举，精确白名单在技术上不成立。
            // 严格模式必须对这两家**降级放行**，而不是一刀切拒绝 ——
            // 否则 StrictOriginWhitelist 一打开，所有 Firefox 用户 100% 连不上，
            // 这个开关就没人敢开，等于安全能力归零。
            var strictMoz = await ConnectAsync("moz-extension://aaaa-bbbb-cccc");
            Check("严格模式：Firefox Origin 前缀放行（UUID 不可白名单，只能降级）", strictMoz is not null);
            if (strictMoz is not null)
            {
                await strictMoz.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var strictSafari = await ConnectAsync("safari-web-extension://1111-2222");
            Check("严格模式：Safari Origin 前缀放行（同上）", strictSafari is not null);
            if (strictSafari is not null)
            {
                await strictSafari.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            var strictWeb = await ConnectAsync("https://evil.example.com");
            Check("严格模式：普通网页 Origin 被拒", strictWeb is null);
            if (strictWeb is not null)
            {
                await strictWeb.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }

            Section("Origin 分级结论（CheckOrigin 直接判定）");

            Check("Chromium 白名单内 → Allowed",
                BridgeConfig.CheckOrigin("chrome-extension://trusted-extension-id", out _) == OriginVerdict.Allowed);

            Check("Chromium 白名单外 → Rejected 且带原因",
                BridgeConfig.CheckOrigin(GoodOrigin, out var rejectReason) == OriginVerdict.Rejected &&
                rejectReason.Length > 0, rejectReason);

            Check("Firefox → AllowedUnverifiable（放行但没验成身份）",
                BridgeConfig.CheckOrigin("moz-extension://zzz", out _) == OriginVerdict.AllowedUnverifiable);

            Check("Safari → AllowedUnverifiable",
                BridgeConfig.CheckOrigin("safari-web-extension://zzz", out _) == OriginVerdict.AllowedUnverifiable);

            Check("网页 Origin → Rejected",
                BridgeConfig.CheckOrigin("https://evil.example.com", out _) == OriginVerdict.Rejected);

            BridgeConfig.StrictOriginWhitelist = false;
        }

        /// <summary>
        /// 并发写：8 条连接同时 add 不同 URL。
        /// 桥接走的是 Store.UpdateAsync（持锁完成 读→去重→写），所以 8 条必须全活。
        ///
        /// 注意：一条连接上并发发两个请求会让 SendAsync 互相踩踏，
        /// 所以这里一连接一请求，靠连接数堆并发。
        /// </summary>
        private static async Task TestConcurrencyAsync()
        {
            Section("并发写（Store.UpdateAsync 原子读改写）");

            WebAppShortcutStore.ResetForTest();

            const int count = 8;
            var sockets = new List<ClientWebSocket>();

            for (var i = 0; i < count; i++)
            {
                var socket = await ConnectAsync(GoodOrigin);
                if (socket is null)
                {
                    Check($"并发测试：第 {i + 1} 条连接建立", false);
                    return;
                }

                sockets.Add(socket);
            }

            var tasks = new List<Task<string>>();
            for (var i = 0; i < count; i++)
            {
                string request = Req(
                    $"4{i}",
                    "webapp.add",
                    $"{{\"url\":\"https://x{i}.example/\",\"name\":\"X{i}\"}}");
                tasks.Add(RoundtripAsync(sockets[i], request));
            }

            await Task.WhenAll(tasks);

            var list = await RoundtripAsync(sockets[0], Req("50", "webapp.list"));
            using (var doc = JsonDocument.Parse(list))
            {
                var items = doc.RootElement.GetProperty("payload");
                Check(
                    $"并发 add {count} 条不同 URL: 期望 {count} 条全活",
                    items.GetArrayLength() == count,
                    $"实际 {items.GetArrayLength()} 条");
            }

            foreach (var socket in sockets)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                socket.Dispose();
            }
        }

        /// <summary>
        /// 对照实验：证明「为什么必须走 UpdateAsync」。
        /// 调用方自己 Load → 改 → Save，即使 Load / Save 各自都加锁，
        /// 中间那一段是裸的，8 个并发写最后只剩 1 条。
        /// 这条断言的是「危险确实存在」，不是「实现正确」——改测试前先想清楚。
        /// </summary>
        private static async Task TestRawLoadSaveHazardAsync()
        {
            Section("对照实验：裸 Load→Save 并发必然丢更新");

            WebAppShortcutStore.ResetForTest();

            const int count = 8;
            var tasks = new List<Task>();

            for (var i = 0; i < count; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var all = (await WebAppShortcutStore.LoadAsync()).ToList();
                    all.Add(new WebAppShortcut(
                        $"raw{index}", $"Raw{index}", $"https://raw{index}.example/", null));
                    await WebAppShortcutStore.SaveAsync(all);
                }));
            }

            await Task.WhenAll(tasks);

            var survived = await WebAppShortcutStore.LoadAsync();
            Check(
                $"裸 Load→Save 并发 {count} 条：确实丢更新（所以写路径必须走 UpdateAsync）",
                survived.Count < count,
                $"实际存活 {survived.Count} 条" + (survived.Count == count ? " —— 竞态没复现，检查 IoDelayMs 是否被改成 0" : string.Empty));

            // 同一个场景换成 UpdateAsync，必须一条不丢
            WebAppShortcutStore.ResetForTest();

            var safe = new List<Task>();
            for (var i = 0; i < count; i++)
            {
                var index = i;
                safe.Add(WebAppShortcutStore.UpdateAsync(all =>
                {
                    var list = all.ToList();
                    list.Add(new WebAppShortcut(
                        $"safe{index}", $"Safe{index}", $"https://safe{index}.example/", null));
                    return list;
                }));
            }

            await Task.WhenAll(safe);

            var survivedSafe = await WebAppShortcutStore.LoadAsync();
            Check(
                $"同样的并发换成 UpdateAsync：{count} 条全活",
                survivedSafe.Count == count,
                $"实际 {survivedSafe.Count} 条");

            WebAppShortcutStore.ResetForTest();
        }
    }
}
