using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// 桥接消息帧（入站）
    ///
    /// 四种 kind：
    ///   req   请求，必须带 id，服务端必须回一条同 id 的 res 或 err
    ///   res   响应（app 侧目前不主动发 req，保留给未来反向调用）
    ///   event 单向事件，不需要响应
    ///   err   错误响应
    /// </summary>
    public sealed class BridgeFrame
    {
        /// <summary>req / res / event / err</summary>
        public string? Kind { get; set; }

        /// <summary>请求标识，用于 req 与 res 配对</summary>
        public string? Id { get; set; }

        /// <summary>方法名，如 bridge.hello、webapp.add</summary>
        public string? Method { get; set; }

        /// <summary>负载，原样透传给命令路由</summary>
        public JsonElement? Payload { get; set; }

        /// <summary>响应是否成功（仅 res/err 有意义）</summary>
        public bool Ok { get; set; }

        /// <summary>错误码（仅 err 有意义）</summary>
        public string? Code { get; set; }

        /// <summary>错误信息（仅 err 有意义）</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// bridge.hello 的响应负载
    /// </summary>
    public sealed class HelloPayload
    {
        public int ProtocolVersion { get; set; }

        public string AppName { get; set; } = "边栏助手";

        public string AppVersion { get; set; } = "1.0.0";

        public int Port { get; set; }

        /// <summary>本端支持的方法名，扩展据此做能力降级</summary>
        public string[] Methods { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 是否要求配对令牌。
        ///
        /// 【它为什么重要——不是"第三关还没做"，而是唯一能跨浏览器识别客户端的手段】
        /// 白名单这条路在 Firefox / Safari 上是死的：它们的扩展 Origin 含每台机器随机生成的
        /// UUID，服务端既不能预知也不能枚举（见 BridgeConfig.StrictWhitelist 的注释）。
        /// 能区分「你的扩展」和「别的扩展 / 别的本机进程」的，只剩客户端自己拿得出、
        /// 别人拿不出的一个令牌。
        ///
        /// 【为什么现在还是 false】
        /// 配对要两端都实现才有意义，只开服务端会把扩展直接锁在外面。
        /// 而且按威胁模型算，本机进程本来就能直接改写 web-shortcuts.json，
        /// 配对挡不住真正想搞事的本机进程；它真正补的是"事后冒充"这一格。
        /// 要开就得连扩展侧一起做：扩展生成随机 token 存 storage.local，
        /// 首次经用户在 popup 里确认后发给服务端，服务端持久化并之后逐次校验。
        /// </summary>
        public bool RequiresPairing { get; set; }
    }

    /// <summary>
    /// bridge.ping 的响应负载
    /// </summary>
    public sealed class PingPayload
    {
        public long ServerTime { get; set; }

        public string Echo { get; set; } = string.Empty;
    }

    /// <summary>
    /// bridge.ping 的请求负载
    /// </summary>
    public sealed class PingRequestPayload
    {
        public string? Echo { get; set; }
    }

    /// <summary>
    /// webapp.list 返回的单条网页应用
    /// </summary>
    public sealed class WebAppItemPayload
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        /// <summary>是否已有图标（不回传图标本体，避免列表消息过大）</summary>
        public bool HasIcon { get; set; }
    }

    /// <summary>
    /// webapp.add 的请求负载
    /// </summary>
    public sealed class AddWebAppRequest
    {
        public string? Url { get; set; }

        /// <summary>可空，留空则用域名兜底</summary>
        public string? Name { get; set; }

        /// <summary>可空，PNG 字节的 base64。将来批量导入书签时靠这个带上 favicon</summary>
        public string? IconBase64 { get; set; }
    }

    /// <summary>
    /// webapp.add 的响应负载
    /// </summary>
    public sealed class AddWebAppResult
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>true 表示 URL 已存在，返回的是既有条目</summary>
        public bool Duplicate { get; set; }
    }

    /// <summary>
    /// AOT 源生成上下文。
    ///
    /// 项目启用了 Native AOT（PublishAot=true），禁止使用基于反射的序列化。
    /// 所有新增的可序列化类型都必须登记在这里。
    /// ⚠️ 漏登记的后果是运行时抛异常，编译期不报错，加新 DTO 时务必同步补这里。
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(BridgeFrame))]
    [JsonSerializable(typeof(HelloPayload))]
    [JsonSerializable(typeof(PingPayload))]
    [JsonSerializable(typeof(PingRequestPayload))]
    [JsonSerializable(typeof(WebAppItemPayload))]
    [JsonSerializable(typeof(List<WebAppItemPayload>))]
    [JsonSerializable(typeof(AddWebAppRequest))]
    [JsonSerializable(typeof(AddWebAppResult))]
    [JsonSerializable(typeof(string[]))]
    internal partial class BridgeJsonContext : JsonSerializerContext
    {
    }
}
