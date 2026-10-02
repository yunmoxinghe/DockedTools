using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.AspNetCore.Http;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// 单条 WebSocket 连接的生命周期与消息循环
    ///
    /// 【握手阶段做两件事】
    /// 1. 记录 Origin —— 这是 M0 探针最关键的一条：确认浏览器扩展发起的
    ///    WebSocket 握手到底有没有带 Origin 头。整个认证方案都建立在这个前提上，
    ///    如果实测发现不带，白名单方案就得推倒重做。
    /// 2. 校验 Origin —— 挡住"别的网页/别的扩展冒充客户端"。
    ///
    /// 【M0 只处理文本帧】
    /// 二进制帧的位置留着，将来批量传 favicon 时再启用。
    /// </summary>
    public static class BridgeSession
    {
        private const int ReceiveBufferSize = 16 * 1024;

        /// <summary>单条消息上限，给将来批量传图标留余量</summary>
        private const int MaxMessageBytes = 8 * 1024 * 1024;

        /// <summary>
        /// WebSocket 终结点入口
        /// </summary>
        public static async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                LogService.Warning(BridgeConfig.ModuleName,
                    $"非 WebSocket 请求，已拒绝: {context.Request.Path}");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("expected a websocket request");
                return;
            }

            // 探针 4：确认浏览器是否真的把 Origin 带过来了
            var origin = context.Request.Headers.Origin.ToString();
            var hasOrigin = !string.IsNullOrEmpty(origin);

            LogService.Info(BridgeConfig.ModuleName,
                $"握手请求: 远端={context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} " +
                $"Origin={(hasOrigin ? origin : "<无 Origin 头>")}");

            var verdict = BridgeConfig.CheckOrigin(origin, out var reason);
            if (verdict == OriginVerdict.Rejected)
            {
                // 注意：一旦 AcceptWebSocketAsync 就不能再改状态码，所以校验必须在 Accept 之前
                LogService.Warning(BridgeConfig.ModuleName, $"拒绝连接: {reason}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (verdict == OriginVerdict.AllowedUnverifiable)
            {
                // Firefox / Safari 的扩展 Origin 含每机随机 UUID，精确白名单技术上不成立。
                // 放行是无奈之举，必须在日志里留痕，不能放行得无声无息。
                LogService.Warning(BridgeConfig.ModuleName,
                    $"放行但无法核验身份: {origin}（该浏览器扩展 Origin 含每机随机 UUID，" +
                    "无法白名单，已降级为前缀放行。要精确识别客户端请启用配对令牌）");
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            LogService.Info(BridgeConfig.ModuleName, "连接已建立");

            await ReceiveLoopAsync(socket, context.RequestAborted);
        }

        private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[ReceiveBufferSize];

            // 消息累积缓冲按连接复用，别每条消息都 new 一个从 256 字节开始涨的 MemoryStream：
            // 一条 100KB 的消息会让它连续扩容十几次，每次扩容都是「新分配 + 全量拷贝」。
            using var message = new MemoryStream(ReceiveBufferSize);

            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    bool isBinary;

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            LogService.Info(BridgeConfig.ModuleName, "客户端请求关闭连接");
                            await socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            return;
                        }

                        isBinary = result.MessageType == WebSocketMessageType.Binary;

                        if (message.Length + result.Count > MaxMessageBytes)
                        {
                            LogService.Warning(BridgeConfig.ModuleName, "消息超限，断开连接");
                            await socket.CloseAsync(
                                WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (isBinary)
                    {
                        // M0 不解析二进制帧，留给将来的图标批量传输
                        LogService.Debug(BridgeConfig.ModuleName, $"收到二进制帧 {message.Length} 字节（暂未处理）");
                        continue;
                    }

                    // 直接拿 MemoryStream 的内部缓冲反序列化，
                    // 省掉 ToArray() 的拷贝和 GetString() 的 string 分配。
                    // （MemoryStream 是无参构造之外的普通实例，GetBuffer() 可用）
                    var bytes = new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length);
                    await ProcessMessageAsync(socket, bytes, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 连接被取消，正常退出
            }
            catch (WebSocketException ex)
            {
                LogService.Warning(BridgeConfig.ModuleName, $"连接异常断开: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogService.Error(BridgeConfig.ModuleName, "消息循环异常", ex);
            }
        }

        private static async Task ProcessMessageAsync(
            WebSocket socket,
            ReadOnlyMemory<byte> utf8Json,
            CancellationToken cancellationToken)
        {
            BridgeFrame? frame = null;

            try
            {
                frame = JsonSerializer.Deserialize(utf8Json.Span, BridgeJsonContext.Default.BridgeFrame);
            }
            catch (JsonException ex)
            {
                LogService.Warning(BridgeConfig.ModuleName, $"JSON 解析失败: {ex.Message}");
                await SendAsync(socket, new BridgeFrame
                {
                    Kind = "err",
                    Ok = false,
                    Code = "PARSE_ERROR",
                    Message = "无法解析消息"
                }, cancellationToken);
                return;
            }

            if (frame is null || string.IsNullOrEmpty(frame.Method))
            {
                await SendAsync(socket, new BridgeFrame
                {
                    Kind = "err",
                    Id = frame?.Id,
                    Ok = false,
                    Code = "BAD_REQUEST",
                    Message = "缺少 method 字段"
                }, cancellationToken);
                return;
            }

            try
            {
                var payload = await BridgeCommandRouter.DispatchAsync(frame.Method, frame.Payload);

                await SendAsync(socket, new BridgeFrame
                {
                    Kind = "res",
                    Id = frame.Id,
                    Method = frame.Method,
                    Ok = true,
                    Payload = payload
                }, cancellationToken);
            }
            catch (BridgeCommandException ex)
            {
                await SendAsync(socket, new BridgeFrame
                {
                    Kind = "err",
                    Id = frame.Id,
                    Method = frame.Method,
                    Ok = false,
                    Code = ex.ErrorCode,
                    Message = ex.Message
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                LogService.Error(BridgeConfig.ModuleName, $"执行命令失败: {frame.Method}", ex);

                await SendAsync(socket, new BridgeFrame
                {
                    Kind = "err",
                    Id = frame.Id,
                    Method = frame.Method,
                    Ok = false,
                    Code = "INTERNAL_ERROR",
                    Message = ex.Message
                }, cancellationToken);
            }
        }

        private static async Task SendAsync(
            WebSocket socket,
            BridgeFrame frame,
            CancellationToken cancellationToken)
        {
            // 直接序列化成 UTF8 字节，跳过「先成 string 再 GetBytes」那一步：
            // 省一次字符串分配 + 一次编码拷贝。
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, BridgeJsonContext.Default.BridgeFrame);

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
    }
}
