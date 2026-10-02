using System;
using System.Text.Json;
using System.Threading.Tasks;
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// 命令路由：把方法名分发到具体处理逻辑
    ///
    /// 【M0】bridge.hello / bridge.ping —— 用来验连通性和握手 Origin
    /// 【M1】webapp.list / webapp.add —— 真正干活：把浏览器里的网页存成网页应用
    ///
    /// 【后续扩展位】
    /// webapp.open / webapp.remove
    /// sidebar.show / sidebar.hide / sidebar.toggle
    /// bookmark.import（批量带图标，顺带绕开 EdgeFaviconReader 的 SQLITE_BUSY 老问题）
    ///
    /// 【线程约束】
    /// 这些命令跑在 Kestrel 的后台线程上，凡是碰 UI 的动作都必须经
    /// BridgeConfig.RequestUiRefresh 切回 UI 线程，不能直接操作 UI 集合。
    /// </summary>
    public static class BridgeCommandRouter
    {
        /// <summary>当前支持的方法清单，会随 hello 返回给扩展</summary>
        public static readonly string[] SupportedMethods =
        {
            "bridge.hello",
            "bridge.ping",
            "webapp.list",
            "webapp.add"
        };

        /// <summary>
        /// 分发一次请求
        /// </summary>
        /// <param name="method">方法名</param>
        /// <param name="payload">原始负载</param>
        /// <returns>已序列化的响应负载；失败时抛 BridgeCommandException</returns>
        public static async Task<JsonElement?> DispatchAsync(string method, JsonElement? payload)
        {
            switch (method)
            {
                case "bridge.hello":
                    return JsonSerializer.SerializeToElement(
                        HandleHello(), BridgeJsonContext.Default.HelloPayload);

                case "bridge.ping":
                    return JsonSerializer.SerializeToElement(
                        HandlePing(payload), BridgeJsonContext.Default.PingPayload);

                case "webapp.list":
                    return await WebAppBridgeCommands.ListAsync();

                case "webapp.add":
                    var request = payload.HasValue
                        ? payload.Value.Deserialize(BridgeJsonContext.Default.AddWebAppRequest)
                        : null;
                    return await WebAppBridgeCommands.AddAsync(request);

                default:
                    throw new BridgeCommandException("METHOD_NOT_FOUND", $"不支持的方法: {method}");
            }
        }

        private static HelloPayload HandleHello()
        {
            return new HelloPayload
            {
                ProtocolVersion = BridgeConfig.ProtocolVersion,
                AppName = "边栏助手",
                AppVersion = "1.0.0",
                Port = BridgeService.ActualPort,
                Methods = SupportedMethods,
                RequiresPairing = false
            };
        }

        private static PingPayload HandlePing(JsonElement? payload)
        {
            string echo = string.Empty;

            if (payload.HasValue &&
                payload.Value.ValueKind == JsonValueKind.Object &&
                payload.Value.TryGetProperty("echo", out var echoElement) &&
                echoElement.ValueKind == JsonValueKind.String)
            {
                echo = echoElement.GetString() ?? string.Empty;
            }

            LogService.Debug(BridgeConfig.ModuleName, $"ping: echo={echo}");

            return new PingPayload
            {
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Echo = echo
            };
        }
    }

    /// <summary>
    /// 命令执行失败，携带结构化错误码
    /// </summary>
    public sealed class BridgeCommandException : Exception
    {
        public string ErrorCode { get; }

        public BridgeCommandException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
