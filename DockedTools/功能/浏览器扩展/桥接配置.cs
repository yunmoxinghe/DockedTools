using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// Origin 校验的结论。放行还分两种，是因为「放行」不等于「验明身份」。
    /// </summary>
    public enum OriginVerdict
    {
        /// <summary>放行，且身份已核验（Chromium 系精确命中白名单，或宽松模式下确认是扩展来源）</summary>
        Allowed,

        /// <summary>
        /// 放行，但**没能核验身份**。
        /// 只在 Firefox / Safari 上出现：它们的扩展 Origin 含每机随机的 UUID，技术上无法白名单。
        /// 调用方应当为此打一条告警日志——这是降级，不是正常状态。
        /// </summary>
        AllowedUnverifiable,

        /// <summary>拒绝</summary>
        Rejected,
    }

    /// <summary>
    /// 扩展来源能否被精确识别
    /// </summary>
    public enum ExtensionOriginKind
    {
        /// <summary>Origin 里的 ID 全局稳定，可以精确白名单（Chromium 系）</summary>
        ExactMatchable,

        /// <summary>Origin 里的 UUID 每台机器随机，无法白名单（Gecko / WebKit）</summary>
        Unverifiable,
    }

    /// <summary>
    /// 浏览器扩展桥接服务的配置
    ///
    /// 【定位】
    /// app 在本机回环地址上起一个 WebSocket 服务端，浏览器扩展作为客户端连进来。
    /// 传输层选 WebSocket 而不是 URI Scheme，是因为要承载双向、持续、可带二进制的数据；
    /// URI Scheme 只适合"唤起 + 传几个参数"。
    ///
    /// 【端口发现】
    /// 扩展读不到本地文件、也读不到注册表，所以无法由 app 主动告知端口。
    /// 只能约定一个基准端口，被占用则递增；扩展侧并发探测这一段区间。
    /// </summary>
    public static class BridgeConfig
    {
        /// <summary>日志模块名</summary>
        public const string ModuleName = "浏览器扩展桥接";

        /// <summary>基准端口</summary>
        public const int BasePort = 17829;

        /// <summary>端口探测区间长度（17829 ~ 17839）</summary>
        public const int PortRange = 11;

        /// <summary>协议版本，扩展与 app 通过 bridge.hello 协商</summary>
        public const int ProtocolVersion = 1;

        /// <summary>WebSocket 终结点路径</summary>
        public const string BridgePath = "/bridge";

        /// <summary>
        /// 是否启用桥接服务【当前：关闭】
        ///
        /// 2026-10 起暂停接收端：近期不接新功能，回环 WebSocket（Kestrel）不再拉起，
        /// 端口 17829~17839 不再监听，浏览器扩展连不进来。代码整体保留，随时可恢复。
        ///
        /// 要恢复：把这里改回 true 即可。应用入口的启动/停止两处都读这个开关，
        /// 不需要再动别的地方（停止侧本身就是幂等的没启动就什么都不做）。
        ///
        /// 顺带提醒：重新打开前先确认 StrictOriginWhitelist / StrictWhitelist 的取值 ——
        /// 宽松模式下机器上任意一个浏览器扩展都能连进来增删网页应用。
        /// </summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// 是否启用严格来源白名单。
        ///
        /// false（M0 默认，宽松）：只要 Origin 来自浏览器扩展（chrome-extension:// / moz-extension://）就放行。
        /// true（正式）：必须是 StrictWhitelist 里精确登记的扩展 ID。
        ///
        /// 为什么要看 Origin：WebSocket 握手时浏览器强制附加 Origin 头，页面 JS 无法伪造。
        /// 这是挡住"其他网页冒充客户端"的核心手段。
        /// </summary>
        public static bool StrictOriginWhitelist { get; set; } = false;

        /// <summary>
        /// 通知界面刷新的委托。
        ///
        /// 桥接命令跑在 Kestrel 的后台线程上，直接碰 UI 集合会崩。
        /// 这个委托由应用入口在 UI 线程注入，内部负责 TryEnqueue 回 UI 线程。
        /// 未注入时命令照常落盘，只是界面不刷新。
        /// </summary>
        public static Action? RequestUiRefresh { get; set; }

        /// <summary>
        /// 严格模式下的来源白名单。
        /// 发布前填入商店分配的固定 ID：
        ///   Chrome/Edge → chrome-extension://&lt;32 位 ID&gt;
        /// </summary>
        /// <remarks>
        /// ⚠️ **只填 Chrome/Edge 的 ID，Firefox 和 Safari 填不进去——不是没填，是填了也没用。**
        ///
        /// Firefox 扩展的 Origin 是 `moz-extension://&lt;UUID&gt;`，这个 UUID 由 Firefox
        /// **在每台机器上随机生成**（Mozilla 明确说是出于隐私考虑），
        /// **与 manifest 里 `browser_specific_settings.gecko.id` 无关**——
        /// 固定 gecko id 只能保证扩展身份稳定，改变不了 Origin 里的 UUID。
        /// Safari 的 `safari-web-extension://` 是同样的处境。
        ///
        /// 所以这两个浏览器上「精确白名单」在技术上不成立：服务端无法预知也无法枚举那个 UUID，
        /// 白名单里无论写什么都匹配不上。硬要精确匹配，结果就是 Firefox 100% 连不上。
        ///
        /// 详见 <see cref="CheckOrigin"/> 的分级处理。
        /// </remarks>
        public static readonly List<string> StrictWhitelist = new();

        /// <summary>
        /// 校验 WebSocket 握手的 Origin 是否放行
        /// </summary>
        /// <param name="origin">握手请求的 Origin 头，可能为 null</param>
        /// <param name="reason">拒绝原因（仅拒绝时有值）</param>
        public static bool IsAllowedOrigin(string? origin, out string reason)
        {
            return CheckOrigin(origin, out reason) != OriginVerdict.Rejected;
        }

        /// <summary>
        /// 校验 WebSocket 握手的 Origin，并区分「放行且已验明身份」与「放行但没验成」。
        /// </summary>
        /// <param name="origin">握手请求的 Origin 头，可能为 null</param>
        /// <param name="reason">拒绝原因（仅 <see cref="OriginVerdict.Rejected"/> 时有值）</param>
        /// <returns>
        /// 见 <see cref="OriginVerdict"/>。调用方对 <see cref="OriginVerdict.AllowedUnverifiable"/>
        /// 应该打一条告警日志——放行是无奈之举，得留在日志里而不是无声无息。
        /// </returns>
        /// <remarks>
        /// 【为什么要分级，而不是一刀切精确匹配】
        /// 之前的严格模式对所有浏览器都做精确匹配，等于**在 Firefox 上根本开不起来**：
        /// Origin 里的 UUID 每机随机，白名单永远命中不了，一开严格模式 Firefox 就 100% 连不上。
        /// 结果就是 StrictOriginWhitelist 这个开关没人敢开，安全能力等于没有。
        ///
        /// 分级之后这个开关才真的可用：能验的（Chromium）严格验，验不了的（Gecko / WebKit）
        /// 按前缀放行并留下告警。宁可降级也不要全员躺平。
        ///
        /// 【威胁模型：残余风险有多大】
        /// 前缀放行意味着**无法区分「你的扩展」和「同机器上任意其他 Firefox 扩展」**。
        /// 这是精确身份校验的降级，但值得把代价说清楚：
        ///   - 主威胁是**恶意网页**想连 127.0.0.1 操纵数据。这一条已经被前缀校验挡死了：
        ///     Origin 头由浏览器强制附加，页面 JS 无法伪造 `moz-extension://` 前缀。
        ///   - 残余威胁是**本机其他进程**。但本机进程能伪造任意 Origin 头，
        ///     而且它们有更省事的路——直接改写 %LOCALAPPDATA% 里的 web-shortcuts.json。
        ///     也就是说精确白名单挡不住真正想搞事的本机进程，收益被高估了。
        ///   - 真要挡本机进程，得靠 <see cref="HelloPayload.RequiresPairing"/> 的配对令牌，
        ///     那是唯一跨浏览器可用的手段，不是白名单。
        /// </remarks>
        public static OriginVerdict CheckOrigin(string? origin, out string reason)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                // 浏览器发起的 WebSocket 一定带 Origin；没有 Origin 说明是脚本/命令行客户端。
                // M0 宽松模式先放行但记录，方便探针阶段观察真实行为。
                if (StrictOriginWhitelist)
                {
                    reason = "缺少 Origin 头";
                    return OriginVerdict.Rejected;
                }

                reason = string.Empty;
                return OriginVerdict.Allowed;
            }

            var kind = ClassifyOrigin(origin);
            if (kind is null)
            {
                // 网页、curl、别的本地程序。这是最该挡的一类（恶意网页正是这个形态）。
                reason = $"Origin 不是浏览器扩展: {origin}";
                return OriginVerdict.Rejected;
            }

            if (!StrictOriginWhitelist)
            {
                reason = string.Empty;
                return OriginVerdict.Allowed;
            }

            // 严格模式：验不了身份的那两家只能前缀放行
            if (kind == ExtensionOriginKind.Unverifiable)
            {
                reason = string.Empty;
                return OriginVerdict.AllowedUnverifiable;
            }

            foreach (var allowed in StrictWhitelist)
            {
                if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    reason = string.Empty;
                    return OriginVerdict.Allowed;
                }
            }

            reason = $"Origin 不在白名单: {origin}";
            return OriginVerdict.Rejected;
        }

        /// <summary>
        /// 判断 Origin 属于哪家浏览器扩展；不是扩展来源时返回 null。
        /// </summary>
        private static ExtensionOriginKind? ClassifyOrigin(string origin)
        {
            if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                // Chromium 系：ID 由商店分配、全机一致，可以精确白名单
                return ExtensionOriginKind.ExactMatchable;
            }

            if (origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("safari-web-extension://", StringComparison.OrdinalIgnoreCase))
            {
                // Gecko / WebKit：Origin 里的 UUID 每台机器随机生成，
                // 与 manifest 里的扩展 ID 无关，服务端无从预知，只能前缀放行。
                return ExtensionOriginKind.Unverifiable;
            }

            return null;
        }

        /// <summary>
        /// 在端口区间内找一个当前可绑定的端口。
        ///
        /// 注意：这里有 TOCTOU 竞态（探测完到 Kestrel 真正监听之间可能被别的进程抢走），
        /// 但对单机本机服务来说概率极低，且上层已对启动失败做了兜底。
        /// </summary>
        /// <returns>可用端口；0 表示整段区间都被占用</returns>
        public static int FindAvailablePort()
        {
            for (var offset = 0; offset < PortRange; offset++)
            {
                var port = BasePort + offset;
                TcpListener? probe = null;

                try
                {
                    probe = new TcpListener(IPAddress.Loopback, port);
                    probe.ExclusiveAddressUse = true;
                    probe.Start();
                    return port;
                }
                catch (SocketException)
                {
                    // 端口被占用，试下一个
                }
                finally
                {
                    probe?.Stop();
                }
            }

            return 0;
        }
    }
}
