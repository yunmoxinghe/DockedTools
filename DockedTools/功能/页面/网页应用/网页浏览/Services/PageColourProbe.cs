using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser.Services
{
    /// <summary>
    /// 页面元素颜色数据（对应 ATBC 的 TabElementColourData）
    /// </summary>
    public sealed class AdaptiveElementColour
    {
        public AdaptiveColour Colour { get; set; } = AdaptiveColour.Transparent;

        /// <summary>
        /// 元素自身不透明度。
        /// null 表示页面给的值不是数字 —— ATBC 的 parsePageColour 遇到 NaN 会
        /// continue 跳过该元素，这里同理，不能默认成 1 让它参与合成。
        /// </summary>
        public double? Opacity { get; set; }

        /// <summary>元素 filter（ATBC 保留该字段用于扩展）</summary>
        public string? Filter { get; set; }
    }

    /// <summary>
    /// meta theme-color 数据（对应 ATBC 的 TabThemeColourData）
    /// </summary>
    public sealed class AdaptiveThemeColour
    {
        public string? Light { get; set; }
        public string? Dark { get; set; }
    }

    /// <summary>
    /// 一次探测拿到的全部颜色数据（对应 ATBC 的 TabColourData）
    /// </summary>
    public sealed class AdaptiveTabColourData
    {
        public List<AdaptiveElementColour> Page { get; } = new();

        /// <summary>
        /// 底栏专用：视口<b>底端</b>那一带的元素栈（采样点 y = innerHeight - 3）。
        /// 与 <see cref="Page"/> 同一套规则，只是采样点纵坐标不同 ——
        /// 顶栏与底栏据此各取各的颜色，底栏不再复制顶栏。
        /// 老版本脚本没有这个字段时为空列表，托管侧据此退化成「底栏跟随顶栏」。
        /// </summary>
        public List<AdaptiveElementColour> PageBottom { get; } = new();

        public AdaptiveThemeColour Theme { get; } = new();

        public AdaptiveElementColour? Query { get; set; }

        /// <summary>特殊页面类型：none / image / plaintext / svg</summary>
        public string Special { get; set; } = "none";

        /// <summary>
        /// 这份数据采样时页面的 <c>location.href</c>（不含 hash）。
        /// <b>跨页面串色的闸门就在这里</b>：常驻脚本的回传是异步的，
        /// 切换 / 导航之后，旧文档的回传有可能慢半拍到达 —— 若不加甄别直接上色，
        /// 就会出现「页 A 的颜色刷到了页 B 的顶栏/底栏上」。
        /// 托管侧用它和 <c>CoreWebView2.Source</c> 比对，对不上就丢弃。
        /// null 表示拿不到（老版本脚本 / 一次性探测），此时不做这道校验。
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// 这份数据所属文档的 <c>performance.timeOrigin</c>。
        ///
        /// <para><b>光比 URL 是不够的。</b>刷新（F5）或再次导航到同一个地址时，
        /// 新旧两个文档的 <c>location.href</c> <b>完全一致</b>，URL 闸门必然放行 ——
        /// 于是旧文档 250ms 节流窗口里那一发迟到回传会先刷一次旧色，
        /// 紧接着才被新文档自己的采样纠正，观感是「栏子闪一下」。</para>
        ///
        /// <para><c>performance.timeOrigin</c> 是文档创建时刻，同一台机器上单调递增，
        /// 既能区分「同一 URL 的两次文档」，也能区分「不同 URL 的两个文档」。
        /// 托管侧记下最近接受过的那个值，比它小的就是过期文档。</para>
        ///
        /// <para>为什么不用脚本自己生成的随机 id：随机值之间无法比较先后，
        /// 而这里恰恰需要「谁更新」。为什么不用 Date.now()：精度只到毫秒，
        /// 连续两次导航落在同一毫秒内就分不出来了（虽然概率极低，但没必要冒险）。</para>
        ///
        /// <para>0 表示拿不到（老版本脚本），此时不做这道校验。</para>
        /// </summary>
        public double DocOrigin { get; set; }
    }

    /// <summary>
    /// 页面取色探针。
    /// 移植自 Adaptive Tab Bar Colour（ATBC）src/entrypoints/atbc.content.ts：
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/atbc.content.ts
    ///
    /// 两种工作方式：
    /// 1. <see cref="ProbeAsync"/> —— 一次性 ExecuteScriptAsync 探测（兜底用）；
    /// 2. <see cref="BuildMonitorScript"/> —— 常驻监控脚本，对齐上游 enableDynamic()：
    ///    click / resize / scroll（捕获阶段）/ visibilitychange 四个事件，
    ///    以及 darkReader、meta theme-color 属性、meta 标签增删、STYLE 标签增删、
    ///    ⭐换肤属性（html/body 的 class 与 data-* 主题属性）五个 MutationObserver，
    ///    外加 ⭐一个盯文档根元素的 ResizeObserver，统一走 250ms trailing 节流后回传。
    ///    后两项是上游没有的 —— 上游漏了它们，站点自己换肤、页面内部布局变化时栏色不刷新。
    ///
    /// 与上游的差异：
    /// 1. 回传通道用 WebView2 的 chrome.webview.postMessage，替代 browser.runtime.sendMessage；
    /// 2. 颜色归一化加哨兵色校验，避免非法值被静默解析成黑色；
    /// 3. 特殊页面判定去掉 Firefox 专属资源（chrome://、resource://），改用 SVG / 纯文本文档判定；
    /// 4. 常驻脚本在 readyState='loading' 时挂到 DOMContentLoaded 再启动
    ///    （AddScriptToExecuteOnDocumentCreated 注入时 document.head/body 还不存在）；
    /// 5. 回传数据多带一个 <c>url</c>（采样时刻的 location.href）—— 上游不需要，因为 Firefox
    ///    天然按 tab 隔离；这里同一个 WebView 会在一次次导航里跨越多个文档，而回传是异步的，
    ///    没有它就只能靠时序猜这份颜色属于哪一个文档（详见 AdaptiveTabColourData.Url）；
    /// 6. 刻意不监听上游的 transition{end,cancel} / animation{end,cancel} ——
    ///    带 CSS 动画的页面每播完一段就重跑一整条取色流水线（取色 → SetBottomBar →
    ///    RequestedTheme → 整棵子树 ThemeResource 重求值 → VisualState → 布局），
    ///    而且动画刚结束时采样到的往往是过渡态中间色，本身就不可用。
    ///    SPA 路由切换有 click + MutationObserver 兜底，主题切换有 STYLE / darkReader 观察器兜底，
    ///    不依赖这两个补不了的场景。
    /// 7. <b>scroll 走捕获阶段</b> —— scroll 事件不冒泡，上游挂在 document 上的冒泡监听
    ///    收不到「页面主体在 overflow:auto 的容器里滚动」这类滚动（SPA 极常见），取色会整段哑掉。
    ///    非冒泡事件仍有捕获阶段，加 <c>capture: true</c> 才能收到任意后代元素的滚动。
    /// 8. <b>多一个换肤属性 MutationObserver</b> —— 站点自己的亮/暗切换几乎不增删 STYLE 节点，
    ///    而是改 html/body 的 class（Tailwind 的 dark）或 data-* 主题属性（Bootstrap 的 data-bs-theme），
    ///    上游四个观察者全都不覆盖，点了站内深色模式按钮栏色不动。
    /// 9. <b>多一个 ResizeObserver</b> —— window 级 resize 只在窗口缩放时发，
    ///    侧栏收起 / 折叠面板展开这类页面内部布局变化不动窗口，栏色同样不刷新。
    /// </summary>
    public static class PageColourProbe
    {
        private const string QueryToken = "__ATBC_QUERY__";

        /// <summary>脚本里读的运行时取色选择器变量名（托管侧可随时改写）</summary>
        private const string RuntimeQueryVariable = "__dockedToolsColourQuery";

        /// <summary>
        /// 脚本里读的运行时「动态刷新」开关变量名。
        /// 脚本一旦注入就撤不掉，只能靠变量控制。
        /// 注意它在 dispatch（每次回传前）里生效，不是在 start（文档创建时）里 ——
        /// 后者会导致「关掉再打开」必须导航一次才能恢复。
        /// </summary>
        private const string RuntimeDynamicVariable = "__dockedToolsColourDynamic";

        /// <summary>
        /// 脚本里读的运行时「挂起」开关变量名。
        /// 对齐 ATBC 的 SETUP_SCRIPT / mode:"suspend"：命中 COLOUR 规则时页面外观完全不参与，
        /// 上游会调 disableDynamic() 把监听撤掉。这里同理 —— 不止是不回传，而是真的撤监听，
        /// 否则从普通站点导航到规则站点后，上一页留下的脚本仍会持续回传并覆盖规则指定的颜色。
        /// </summary>
        private const string RuntimeSuspendedVariable = "__dockedToolsColourSuspended";

        /// <summary>常驻脚本回传消息的标识</summary>
        public const string MessageHeader = "DockedTools_adaptive_colour";

        /// <summary>
        /// 在当前页面执行一次取色探测
        /// </summary>
        /// <param name="coreWebView">WebView2 内核</param>
        /// <param name="query">可选的 CSS 选择器（指定后额外返回该元素的颜色）</param>
        public static async Task<AdaptiveTabColourData?> ProbeAsync(CoreWebView2 coreWebView, string? query = null)
        {
            if (coreWebView is null)
            {
                return null;
            }

            try
            {
                string? raw = await coreWebView.ExecuteScriptAsync(BuildScript(query));
                return Parse(raw);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PageColourProbe] 取色失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>构建一次性探测脚本</summary>
        public static string BuildScript(string? query)
            => InjectQuery(ProbeScript, query);

        /// <summary>
        /// 构建常驻监控脚本。配合 AddScriptToExecuteOnDocumentCreatedAsync 使用，
        /// 每个新文档都会自动生效。
        /// 对应 ATBC 的 enableDynamic() + sendColour()：
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/atbc.content.ts
        /// </summary>
        public static string BuildMonitorScript(string? query)
            => InjectQuery(MonitorScript, query);

        /// <summary>
        /// 构建「把运行时参数推给当前文档」的脚本（取色选择器 + 动态刷新开关 + 挂起开关）。
        /// 内核没有移除已注入脚本的 API，改这几项时对已加载的页面只能改运行时变量，
        /// 新文档则由注入时带的默认值 + 每次导航后的推送兜底。
        /// </summary>
        public static string BuildRuntimeOptionsScript(string? query, bool dynamic, bool suspended)
            => "window." + RuntimeQueryVariable + " = " + QueryLiteral(query) + ";"
                + "window." + RuntimeDynamicVariable + " = " + (dynamic ? "true" : "false") + ";"
                + "window." + RuntimeSuspendedVariable + " = " + (suspended ? "true" : "false") + ";"
                // 挂起要立刻撤掉已有监听（对齐 ATBC 的 disableDynamic），
                // 只置标志位的话已经排程的定时器还会再回传一次。
                + (suspended ? SuspendCall + ";" : string.Empty);

        /// <summary>
        /// 构建「挂起当前文档里的常驻脚本」的脚本。
        /// 脚本在新文档里会重新执行并重新注册监听，所以挂起只对当前文档生效 —— 这正是上游的语义。
        /// </summary>
        public static string BuildSuspendScript()
            => SuspendCall + ";"
                + "window." + RuntimeSuspendedVariable + " = true;";

        private const string SuspendCall =
            "if (window.__dockedToolsColourSuspend) { try { window.__dockedToolsColourSuspend(); } catch (e) { } }";

        /// <summary>
        /// 构建「读取常驻脚本触发埋点」的脚本。
        ///
        /// <para>常驻脚本跑在网页里，它的 <c>console</c> 输出<b>不会</b>进托管侧的日志通道
        /// （winapp 的 --debug-output 只转发 App 进程的 <c>Debug.WriteLine</c>），
        /// 托管侧原本又只看得见「最终上色」那一条 —— 触发了但被去重吃掉、被闸门挡掉的
        /// 那些完全不留痕。要回答「取色到底触发了几次 / 为什么没刷新」，只能靠
        /// ExecuteScript 把脚本里那个环形缓冲读出来。</para>
        /// </summary>
        public static string BuildLogScript()
            => "JSON.stringify(window.__dockedToolsColourLog || []);";

        /// <summary>
        /// 构建「读取采样链快照」的脚本。
        ///
        /// <para>触发埋点只能回答「取色触发了几次」，回答不了「为什么取到这个色」——
        /// 最终色块是整条元素栈合成出来的，中间哪一层被过滤、哪一层盖在上面，托管侧全瞎。
        /// 这份快照把中线那一列元素的 tag / 尺寸 / 背景色 / 是否通过过滤都带出来，
        /// 是定位「页面明明是浅黄，栏色却是白」这类问题的唯一手段。</para>
        /// </summary>
        public static string BuildTraceScript()
            => "JSON.stringify(window.__dockedToolsColourTraces || []);";

        /// <summary>
        /// 把采样链快照格式化成若干行可直接打印的文本（一条快照一行）。没有快照时返回 null。
        /// </summary>
        public static List<string>? FormatTrace(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument outer = JsonDocument.Parse(raw);
                string json = outer.RootElement.ValueKind == JsonValueKind.String
                    ? outer.RootElement.GetString() ?? string.Empty
                    : raw;

                if (string.IsNullOrWhiteSpace(json) || json == "[]")
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var lines = new List<string>();

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string source = item.TryGetProperty("src", out JsonElement src) ? src.ToString() : "?";
                    string time = item.TryGetProperty("t", out JsonElement stamp) ? stamp.ToString() : "?";
                    string stack = item.TryGetProperty("stack", out JsonElement chain) ? chain.ToString() : "?";

                    lines.Add($"{source}@{time}: {stack}");
                }

                return lines.Count == 0 ? null : lines;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PageColourProbe] 解析采样链快照失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 把触发埋点格式化成可直接打印的一行文本。没有埋点时返回 null（调用方据此不打日志）。
        /// </summary>
        public static string? FormatLog(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument outer = JsonDocument.Parse(raw);
                string json = outer.RootElement.ValueKind == JsonValueKind.String
                    ? outer.RootElement.GetString() ?? string.Empty
                    : raw;

                if (string.IsNullOrWhiteSpace(json) || json == "[]")
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var builder = new StringBuilder();

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string source = item.TryGetProperty("src", out JsonElement src) ? src.ToString() : "?";
                    string action = item.TryGetProperty("act", out JsonElement act) ? act.ToString() : "?";
                    string time = item.TryGetProperty("t", out JsonElement t) ? t.ToString() : "?";

                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(source).Append('/').Append(action).Append('@').Append(time);
                }

                return builder.Length == 0 ? null : builder.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PageColourProbe] 解析取色埋点失败: {ex.Message}");
                return null;
            }
        }

        private static string InjectQuery(string script, string? query)
            => script.Replace(QueryToken, QueryLiteral(query));

        private static string QueryLiteral(string? query)
        {
            if (query is null)
            {
                return "null";
            }

            return "'" + query
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", " ")
                .Replace("\n", " ") + "'";
        }

        /// <summary>解析一次性探测的返回值（ExecuteScriptAsync 结果外层还套了一层引号）</summary>
        private static AdaptiveTabColourData? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument outer = JsonDocument.Parse(raw);
                string json = outer.RootElement.ValueKind == JsonValueKind.String
                    ? outer.RootElement.GetString() ?? string.Empty
                    : raw;

                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                return ParseData(document.RootElement);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PageColourProbe] 解析取色结果失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>解析常驻脚本回传的 WebMessage</summary>
        public static AdaptiveTabColourData? ParseMessage(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("header", out JsonElement header) ||
                    header.ValueKind != JsonValueKind.String ||
                    !string.Equals(header.GetString(), MessageHeader, StringComparison.Ordinal))
                {
                    return null;
                }

                return root.TryGetProperty("colour", out JsonElement colour)
                    ? ParseData(colour)
                    : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PageColourProbe] 解析取色消息失败: {ex.Message}");
                return null;
            }
        }

        private static AdaptiveTabColourData? ParseData(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var data = new AdaptiveTabColourData();

            if (root.TryGetProperty("page", out JsonElement page) && page.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in page.EnumerateArray())
                {
                    var element = ReadElementColour(item);
                    if (element is not null)
                    {
                        data.Page.Add(element);
                    }
                }
            }

            if (root.TryGetProperty("pageBottom", out JsonElement pageBottom) &&
                pageBottom.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in pageBottom.EnumerateArray())
                {
                    var element = ReadElementColour(item);
                    if (element is not null)
                    {
                        data.PageBottom.Add(element);
                    }
                }
            }

            if (root.TryGetProperty("theme", out JsonElement theme))
            {
                data.Theme.Light = ReadString(theme, "light");
                data.Theme.Dark = ReadString(theme, "dark");
            }

            if (root.TryGetProperty("query", out JsonElement queryElement) &&
                queryElement.ValueKind == JsonValueKind.Object)
            {
                data.Query = ReadElementColour(queryElement);
            }

            if (root.TryGetProperty("special", out JsonElement special) &&
                special.ValueKind == JsonValueKind.String)
            {
                data.Special = special.GetString() ?? "none";
            }

            data.Url = StripFragment(ReadString(root, "url"));

            // 老版本脚本没有 doc 字段，保持 0 —— 那时代次闸门自动让位给 URL 闸门。
            if (root.TryGetProperty("doc", out JsonElement doc) &&
                doc.ValueKind == JsonValueKind.Number &&
                doc.TryGetDouble(out double docOrigin))
            {
                data.DocOrigin = docOrigin;
            }

            return data;
        }

        /// <summary>
        /// 掐掉 URL 的 hash 段（含 '#' 本身）。
        /// SPA 里 <c>#/route</c> 是常见的路由形式，CoreWebView2.Source 与 location.href
        /// 双方都保留 hash，理论上对得上；但同一文档内点锚点链接时它俩的更新时机并不一致，
        /// 比 hash 没有意义、只会制造误判 —— 颜色本来也不跟着锚点变。
        /// </summary>
        private static string? StripFragment(string? url)
        {
            if (url is null)
            {
                return null;
            }

            int hash = url.IndexOf('#');
            string trimmed = hash >= 0 ? url.Substring(0, hash) : url;

            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// 两条 URL 是否指向同一份文档（忽略 hash 与末尾斜杠差异、大小写不敏感）。
        /// 用宽松比对是因为服务器重定向会让 Source 与 href 在表现形式上略有出入
        /// （多了斜杠、query 顺序不同则另说），硬比对会把正常的导航一次扣损失掉。
        /// </summary>
        public static bool SameLocation(string? a, string? b)
        {
            if (a is null || b is null)
            {
                // 拿不到就别拦 —— 老脚本 / 一次性探测本来就没有 url 字段
                return true;
            }

            return string.Equals(
                Normalise(a),
                Normalise(b),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalise(string url)
        {
            int hash = url.IndexOf('#');
            string value = hash >= 0 ? url.Substring(0, hash) : url;

            // 只拔掉一根末尾斜杠：http://x.com vs http://x.com/ 同文档；
            // 多根不处理 —— 那是不同路径语义。
            if (value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }

            return value;
        }

        private static AdaptiveElementColour? ReadElementColour(JsonElement item)
        {
            string? colourText = ReadString(item, "colour");
            if (colourText is null || !AdaptiveColour.TryParse(colourText, out AdaptiveColour colour))
            {
                return null;
            }

            string? opacityText = ReadString(item, "opacity");
            double? opacity = null;

            // ATBC 的 parseFloat 拿到 NaN 就跳过该元素，所以解析失败要保持 null 而不是兜 1
            if (opacityText is not null &&
                double.TryParse(opacityText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedOpacity))
            {
                opacity = parsedOpacity;
            }

            return new AdaptiveElementColour
            {
                Colour = colour,
                Opacity = opacity,
                Filter = ReadString(item, "filter")
            };
        }

        private static string? ReadString(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// 取色核心函数，一次性探测与常驻监控共用。
        /// 逐段对齐 ATBC atbc.content.ts 的 getColourData / getPageColourData / getElementColour。
        /// </summary>
        private const string ColourHelpers = @"
    // canvas 复用：颜色归一化在常驻模式下每 250ms 可能跑几十次，
    // 每次都 createElement('canvas') 会白扔一个 DOM 节点 + 一次 2D 上下文初始化。
    var __atbcCanvas = null;
    var __atbcCanvasContext = null;

    var __atbcNormalise = function (value) {
        if (!value) { return null; }
        try {
            if (!__atbcCanvasContext) {
                __atbcCanvas = document.createElement('canvas');
                __atbcCanvas.width = 1;
                __atbcCanvas.height = 1;
                __atbcCanvasContext = __atbcCanvas.getContext('2d');
            }
            var context = __atbcCanvasContext;
            if (!context) { return null; }
            var sentinel = '#123456';
            context.fillStyle = sentinel;
            context.fillStyle = value;
            var parsed = context.fillStyle;
            if (!parsed || parsed === sentinel) { return null; }
            if (parsed.charAt(0) === '#') { return parsed; }
            var numbers = parsed.match(/[\d.]+/g);
            if (!numbers || numbers.length < 3) { return null; }
            var alpha = numbers.length > 3 ? parseFloat(numbers[3]) : 1;
            return 'rgba(' + Math.round(parseFloat(numbers[0])) + ', ' +
                Math.round(parseFloat(numbers[1])) + ', ' +
                Math.round(parseFloat(numbers[2])) + ', ' + alpha + ')';
        } catch (e) { return null; }
    };

    // backgroundColor 透明时退到 background-image。
    // 现代站点的大片底色经常是 linear-gradient 而不是纯色 —— 浅黄、浅灰、米白这类
    // 「明明有底色」的页面尤其常见。只读 backgroundColor 的话整条采样链会全空，
    // 最后混上兜底色（Light 方案下是纯白）就变成「浅黄页面取到白」。
    // 顶栏取第一个色标、底栏取最后一个：渐变是有方向的，取错一端等于取到页面另一头。
    var __atbcGradientColour = function (style, last) {
        var image = style.backgroundImage;
        if (!image || image === 'none') { return null; }

        // url(...) 里常塞着 svg data URI，内容里也会有 #rgb / rgb() 字样，
        // 先把整段 url() 挖掉再找颜色，否则会取到一个跟页面外观毫无关系的字面色。
        var stops = image.replace(/url\([^)]*\)/g, '')
            .match(/rgba?\([^)]*\)|#[0-9a-fA-F]{3,8}/g);
        if (!stops || stops.length === 0) { return null; }

        var value = __atbcNormalise(last ? stops[stops.length - 1] : stops[0]);
        // 渐变本身也可能是透明的（rgba(0,0,0,0) 起步的遮罩层），那跟没取到一样
        if (!value || value === 'rgba(0, 0, 0, 0)' || value === 'transparent') { return null; }
        return value;
    };

    var __atbcElementColour = function (element, fromEnd) {
        if (!(element instanceof Element)) { return null; }
        var style = getComputedStyle(element);
        var background = style.backgroundColor;
        if (!background || background === 'rgba(0, 0, 0, 0)' || background === 'transparent') {
            background = __atbcGradientColour(style, fromEnd === true);
            if (!background) { return null; }
        }
        if (style.opacity === '0') { return null; }
        return { colour: background, opacity: style.opacity, filter: style.filter };
    };

    var __atbcThemeColour = function () {
        var metaThemeColour = document.querySelector('meta[name=""theme-color""]:not([media])');
        var metaLight = document.querySelector('meta[name=""theme-color""][media=""(prefers-color-scheme: light)""]') || metaThemeColour;
        var metaDark = document.querySelector('meta[name=""theme-color""][media=""(prefers-color-scheme: dark)""]') || metaThemeColour;
        return {
            light: metaLight ? __atbcNormalise(metaLight.content) : null,
            dark: metaDark ? __atbcNormalise(metaDark.content) : null
        };
    };

    // 采样点纵坐标参数化：顶栏取视口顶端（y=3），底栏取视口底端（y=innerHeight-3）。
    // 两处共用同一套过滤与回退规则，只有 y 不同 —— 底栏不该是顶栏的复制品，
    // 但也不该用另一套判定，否则同一个页面上下两栏会给出不可比的结果。
    // fromEnd：渐变背景取哪个色标。顶栏（y=3）取第一个，底栏（y=innerHeight-3）取最后一个。
    var __atbcPageColourAt = function (y, fromEnd) {
        return document.elementsFromPoint(window.innerWidth / 2, y)
            .filter(function (element) {
                return element instanceof HTMLElement &&
                    element.offsetWidth >= window.innerWidth * 0.9 &&
                    element.offsetHeight >= 20;
            })
            .map(function (element) { return __atbcElementColour(element, fromEnd); })
            .concat([
                __atbcElementColour(document.body, fromEnd),
                __atbcElementColour(document.documentElement, fromEnd)
            ])
            .filter(function (data) { return data !== null; });
    };

    var __atbcPageColour = function () {
        return __atbcPageColourAt(3, false);
    };

    // 底栏专用：视口底端那一带的元素栈。
    // 页面极短（innerHeight < 3）时 elementsFromPoint 越界返回空数组，
    // 但下面 concat 的 body / html 仍在 —— 于是退化成「和顶栏同色」，正是想要的兜底。
    var __atbcPageBottomColour = function () {
        return __atbcPageColourAt(window.innerHeight - 3, true);
    };

    var __atbcQueryColour = function (query) {
        if (!query) { return null; }
        try { return __atbcElementColour(document.querySelector(query)); } catch (e) { return null; }
    };

    var __atbcSpecial = function () {
        if (document.documentElement instanceof SVGSVGElement) { return 'svg'; }
        if (document.body && document.body.children.length === 1 &&
            document.body.firstElementChild && document.body.firstElementChild.tagName === 'PRE' &&
            document.head && document.head.querySelectorAll('link[rel=""stylesheet""]').length === 0) {
            return 'plaintext';
        }
        return 'none';
    };

    var __atbcColourData = function (query) {
        var page = __atbcPageColour();
        return {
            page: page,
            // 底栏那一带的采样。托管侧据此给底栏单独上色，不再让它复制顶栏。
            pageBottom: __atbcPageBottomColour(),
            theme: __atbcThemeColour(),
            query: __atbcQueryColour(query),
            special: page.length > 0 ? 'none' : __atbcSpecial(),
            // 带上当前 href：托管侧靠它识别「这份颜色属于哪个文档」。
            // 少了它，常驻脚本的迟到回传就没法和新文档的回传区分，会互相覆盖。
            url: location.href,
            // 文档代次：performance.timeOrigin 是本文档的创建时刻，单调递增。
            // 刷新 / 重新导航到同一地址时 href 完全一致，光靠 url 分不出新旧，
            // 靠它才能把旧文档那一发迟到的回传挡在门外。
            doc: (typeof performance !== 'undefined' && performance.timeOrigin) || 0
        };
    };
";

        private const string ProbeScript = @"
(function () {
    var query = __ATBC_QUERY__;
    // 全屏（视频播放等）时取到的是视频画面而不是页面外观。
    // 不能返回 null —— 托管侧会把它当成「探测失败」去复位栏色，
    // 而常驻脚本遇到全屏是「保持不动」，两边语义必须一致。
    // 用 special 标记把「拿不到真实外观」这件事传给托管侧，让它也选择不动。
    if (document.fullscreenElement) {
        return JSON.stringify({ page: [], theme: { light: null, dark: null }, query: null, special: 'fullscreen' });
    }
" + ColourHelpers + @"
    return JSON.stringify(__atbcColourData(query));
})();
";

        private const string MonitorScript = @"
(function () {
    // 脚本会被注入到主文档和所有 iframe（内核没有只注入主文档的开关），
    // 但页面外观只该由主文档决定 —— iframe 里既不该取色也不该注册监听。
    if (window.top !== window) { return; }

    // 内核没有「移除已注入脚本」的 API，脚本只能叠加、不能替换。
    // 改设置（例如换取色选择器）重新注入时，如果这里靠一个布尔守卫直接 return，
    // 生效的就还是旧脚本，新设置永远不生效 —— 所以改成「接管」：
    // 先执行上一个脚本留下的清理函数，把它注册的监听 / MutationObserver 全部撤掉。
    if (window.__dockedToolsColourCleanup) {
        try { window.__dockedToolsColourCleanup(); } catch (e) { }
    }

    var cleanups = [];
    var dispatchTimeout = null;
    var fallbackTimeout = null;

    // 取色选择器可以在运行时改（托管侧用 ExecuteScript 直接改这个变量），
    // 当前页面立刻生效，不必等下一次导航；新文档则用注入时的值。
    var currentQuery = function () {
        return typeof window.__dockedToolsColourQuery !== 'undefined'
            ? window.__dockedToolsColourQuery
            : __ATBC_QUERY__;
    };

    var cleanup = function () {
        cleanups.forEach(function (fn) { try { fn(); } catch (e) { } });
        cleanups.length = 0;
        if (dispatchTimeout) { clearTimeout(dispatchTimeout); dispatchTimeout = null; }
        if (fallbackTimeout) { clearTimeout(fallbackTimeout); fallbackTimeout = null; }
    };
    window.__dockedToolsColourCleanup = cleanup;

    // ATBC 的 SETUP_SCRIPT 挂起模式（suspend）：命中 COLOUR 规则时页面外观完全不参与取色。
    // 撤掉当前文档的全部监听与待发定时器；脚本在新文档里会重新执行并重新注册，
    // 所以这只影响当前文档，正好是上游想要的粒度。
    window.__dockedToolsColourSuspend = function () {
        window.__dockedToolsColourSuspended = true;
        cleanup();
    };

    var throttleIntervalMs = 250;
    var lastSentAt = 0;
    var lastPayload = null;

    // ⭐ 诊断埋点：这段脚本跑在网页里，console 输出既不会进 winapp 的 --debug-output
    // （那条通道只转发 App 进程的 Debug.WriteLine），托管侧也只能看见「最终上色」那一条 ——
    // 触发了但被去重吃掉、被闸门挡掉的那些完全不留痕。
    // 于是「取色到底触发了几次 / 为什么没刷新」这个问题，光看 App 日志根本回答不了。
    // 这里把最近若干次触发记进环形缓冲，托管侧用 ExecuteScript 读出来打进 Debug 日志。
    // 只记一个固定长度的缓冲：常驻脚本在页面整个生命周期里活着，不能让它无限增长。
    var colourLog = [];
    var colourLogLimit = 40;
    var log = function (source, action) {
        colourLog.push({ t: Date.now() % 100000, src: source, act: action });
        if (colourLog.length > colourLogLimit) { colourLog.shift(); }
    };
    window.__dockedToolsColourLog = colourLog;

    // 触发源包装：只多记一笔就转交 sendColour。
    // 返回值要留着做 removeEventListener —— 每次调用 trigger() 都是一个新的函数对象，
    // 现调现传的话卸载时摘不掉，脚本叠加会留下僵尸监听。
    var pendingSource = '?';
    var trigger = function (source) {
        return function () {
            pendingSource = source;
            log(source, 'hit');
            sendColour();
        };
    };

    // ⭐ 采样链快照：回答「这个色是从哪一层元素来的」。
    // 光看最终色块只知道结果，看不出路径 —— 「浅黄页面取到白」可能是白色 header 盖在上面
    // （那时取白是对的，该改的是期望），也可能是渐变没解析（那是 bug）。两种的根治办法
    // 完全不同，没这份快照就只能靠猜。
    // 记下中线上那一列元素的 tag / 尺寸 / 背景色，以及它是否通过了宽高过滤，
    // 末尾附上 body 与 html 的底色（采样链的无条件兜底项）。
    // 只在真正发出消息时记：被 dedup 掉的说明颜色没变，没必要重复占缓冲。
    var sampleTraces = [];
    var sampleTraceLimit = 8;
    var traceSample = function (source) {
        try {
            var minWidth = window.innerWidth * 0.9;
            var parts = [];
            document.elementsFromPoint(window.innerWidth / 2, 3).slice(0, 6).forEach(function (element) {
                var style = null;
                try { style = getComputedStyle(element); } catch (e) { }
                var background = style ? style.backgroundColor : '?';
                if (!background || background === 'rgba(0, 0, 0, 0)' || background === 'transparent') {
                    background = style ? (__atbcGradientColour(style, false) || 'none') : '?';
                }

                var kept = element instanceof HTMLElement &&
                    element.offsetWidth >= minWidth &&
                    element.offsetHeight >= 20;

                parts.push((element.tagName || '?').toLowerCase() +
                    '[' + Math.round(element.offsetWidth) + 'x' + Math.round(element.offsetHeight) + ']' +
                    ' ' + background + (kept ? ' KEEP' : ' SKIP'));
            });

            var bodyBg = '?';
            var htmlBg = '?';
            try { bodyBg = getComputedStyle(document.body).backgroundColor || 'none'; } catch (e) { }
            try { htmlBg = getComputedStyle(document.documentElement).backgroundColor || 'none'; } catch (e) { }

            sampleTraces.push({
                t: Date.now() % 100000,
                src: source,
                stack: parts.join(' | ') + ' || body=' + bodyBg + ' html=' + htmlBg
            });
            if (sampleTraces.length > sampleTraceLimit) { sampleTraces.shift(); }
        } catch (e) { }
    };
    window.__dockedToolsColourTraces = sampleTraces;
" + ColourHelpers + @"
    var dispatch = function () {
        // 动态刷新（ATBC: dynamic）在 dispatch 里查，不在 start() 里查：
        // start() 只在文档创建时跑一次，在那里判断的话，开关关掉再打开就再也装不回监听了
        // （内核没有「让脚本在当前文档重跑一遍」的入口，只有导航到新文档才会重新执行），
        // 结果就是「关掉再打开 → 必须导航一次才恢复」。
        // 放到 dispatch 里就是纯运行时开关：监听照装，发不发由变量说了算，
        // 关→开、开→关两个方向都能立刻生效。
        if (window.__dockedToolsColourDynamic === false) { log('dispatch', 'off'); return; }
        if (window.__dockedToolsColourSuspended === true) { log('dispatch', 'suspended'); return; }
        if (document.visibilityState !== 'visible') { log('dispatch', 'hidden'); return; }
        // 全屏（视频播放等）时取到的是视频画面而不是页面外观，保持当前栏色不动。
        if (document.fullscreenElement) { log('dispatch', 'fullscreen'); return; }
        // 刻意放在去重之前：被 lastPayload 吃掉的那次也算「采样过一次」，
        // 下一次采样仍要等满 250ms。放到去重之后的话，高频触发源（滚动、class 抖动）
        // 会让每一帧都跑一整条取色流水线 —— 节流要挡的是【取色本身】的开销，不只是发消息。
        lastSentAt = Date.now();
        try {
            // 去重：scroll / resize / click 会反复触发，但页面颜色往往没变。
            // 颜色没变就不跨进程发消息，也免掉托管侧一轮 Evaluate + XAML 失效。
            var colourJson = JSON.stringify(__atbcColourData(currentQuery()));
            if (colourJson === lastPayload) { log('dispatch', 'dedup'); return; }
            lastPayload = colourJson;
            window.chrome.webview.postMessage('{""header"":""" + MessageHeader + @""",""colour"":' + colourJson + '}');
            log('dispatch', 'sent');
            traceSample(pendingSource);
        } catch (e) { log('dispatch', 'error'); }
    };

    var sendColour = function () {
        if (dispatchTimeout) { clearTimeout(dispatchTimeout); dispatchTimeout = null; }
        // 页面不可见时不排程：反正 dispatch 也会因为 visibilityState 直接返回
        if (document.visibilityState !== 'visible') { return; }
        var remaining = throttleIntervalMs + lastSentAt - Date.now();
        if (remaining <= 0) {
            dispatch();
        } else {
            dispatchTimeout = setTimeout(function () {
                dispatchTimeout = null;
                dispatch();
            }, remaining);
        }
    };

    var start = function () {
        // 挂起中（命中 COLOUR 规则）：连监听都不装，页面外观完全不参与
        if (window.__dockedToolsColourSuspended === true) { return; }

        var darkReaderObserver = new MutationObserver(trigger('darkreader'));
        var metaThemeColourObserver = new MutationObserver(trigger('meta-attr'));
        var metaTagObserver = new MutationObserver(function (mutationList) {
            mutationList.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node instanceof HTMLMetaElement && node.name === 'theme-color') {
                        pendingSource = 'meta-add';
                        log('meta-add', 'hit');
                        sendColour();
                        metaThemeColourObserver.observe(node, { attributes: true });
                    }
                });
            });
        });
        var styleTagObserver = new MutationObserver(function (mutationList) {
            var touched = mutationList.some(function (mutation) {
                var nodes = [];
                mutation.addedNodes.forEach(function (n) { nodes.push(n); });
                mutation.removedNodes.forEach(function (n) { nodes.push(n); });
                return nodes.some(function (n) { return n.nodeName === 'STYLE'; });
            });
            if (touched) { pendingSource = 'style-tag'; log('style-tag', 'hit'); sendColour(); }
        });

        // passive：这几个监听不会 preventDefault，声明成 passive 让滚动不必等我们的回调
        ['click', 'resize'].forEach(function (event) {
            var handler = trigger(event);
            document.addEventListener(event, handler, { passive: true });
            cleanups.push(function () { document.removeEventListener(event, handler); });
        });

        // ⭐ scroll 必须走捕获阶段。
        // scroll 事件【不冒泡】—— 页面主体放在 overflow:auto 的 div 里滚动时（SPA 极常见的布局），
        // 事件只派发到那个 div 本身，document 上的冒泡监听一次都收不到，取色就彻底哑了：
        // 用户滚了半天，栏色一直停在进站那一次。上游同样是 document.addEventListener('scroll')，
        // 同样的洞 —— 这是「感觉取色刷新不频繁」的头号根因。
        // 非冒泡事件仍然有捕获阶段（window → document → … → target），
        // 在 document 上用 capture 才能收到任意后代元素的滚动。
        // 卸载同样要带 capture:true —— 布尔与 options 两种写法对 removeEventListener 而言
        // 是同一个 capture 标志，两边不一致就摘不掉，脚本叠加时会留下僵尸监听。
        var scrollHandler = trigger('scroll');
        document.addEventListener('scroll', scrollHandler, { passive: true, capture: true });
        cleanups.push(function () {
            document.removeEventListener('scroll', scrollHandler, { capture: true });
        });

        var visibilityHandler = trigger('visibility');
        document.addEventListener('visibilitychange', visibilityHandler);
        cleanups.push(function () { document.removeEventListener('visibilitychange', visibilityHandler); });
        // 刻意不监听 transition{end,cancel} / animation{end,cancel}：见类注释「与上游的差异」第 5 条。

        darkReaderObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['data-darkreader-mode']
        });
        cleanups.push(function () { darkReaderObserver.disconnect(); });
        document.querySelectorAll('meta[name=theme-color]').forEach(function (metaTag) {
            metaThemeColourObserver.observe(metaTag, { attributes: true });
        });
        cleanups.push(function () { metaThemeColourObserver.disconnect(); });
        if (document.head) { metaTagObserver.observe(document.head, { childList: true }); }
        cleanups.push(function () { metaTagObserver.disconnect(); });
        styleTagObserver.observe(document.documentElement, { childList: true });
        if (document.head) { styleTagObserver.observe(document.head, { childList: true }); }
        cleanups.push(function () { styleTagObserver.disconnect(); });

        // ⭐ 换肤观察者：站点自己的亮/暗切换基本【不增删 STYLE 节点】，
        // 而是改 <html> / <body> 的 class（Tailwind 的 dark）或 data-* 主题属性
        // （Bootstrap 5.3 的 data-bs-theme 等）。上游只有 STYLE 增删 / meta 属性 /
        // darkReader 那几个观察者，这类换肤一个都抓不到 —— 于是「点了站内的深色模式按钮，
        // 栏色还停在亮色」，得手动滚一下或者点一下才刷新。
        // 只盯文档级这两个元素：具体元素的 class 变化（hover 态、入场动画）量太大，
        // 而它们几乎不影响顶栏那一带的颜色，盯了纯属白烧 CPU。
        // 刻意不盯 style：内联样式动画每帧都在写，盯进去等于把 250ms 节流窗口常年打满。
        var themeSwitchObserver = new MutationObserver(trigger('theme-switch'));
        var themeSwitchFilter = ['class', 'data-theme', 'data-bs-theme', 'data-color-scheme', 'data-mode'];
        themeSwitchObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: themeSwitchFilter
        });
        // start() 要么在 DOMContentLoaded 之后跑（body 必然存在），要么在 readyState 已过 loading 时跑，
        // 两种情况下 body 都在；留个判空只为极端时序下别抛异常。
        if (document.body) {
            themeSwitchObserver.observe(document.body, {
                attributes: true,
                attributeFilter: themeSwitchFilter
            });
        }
        cleanups.push(function () { themeSwitchObserver.disconnect(); });

        // ⭐ 尺寸观察者：window 级 resize 只在【窗口本身】大小变化时才发，
        // 而页面内部的布局变化（侧栏收起、图片撑开、虚拟列表换页、折叠面板展开）
        // 根本不动窗口尺寸 —— 顶栏那一带的元素换了、高度变了，栏色却不刷新。
        // 盯文档根元素即可覆盖这类变化（html 的盒高随内容走）。
        // 回调里只有读操作（getComputedStyle / elementsFromPoint），不写任何样式，
        // 不会触发 ResizeObserver 的循环告警；高频抖动由 250ms 节流 + 去重兜住。
        if (typeof ResizeObserver !== 'undefined') {
            var layoutObserver = new ResizeObserver(trigger('layout'));
            layoutObserver.observe(document.documentElement);
            cleanups.push(function () { layoutObserver.disconnect(); });
        }

        // 首屏：DOMContentLoaded 时页面常常还只有浏览器的默认白底（CSS / 图片 / 字体没到位），
        // 这时取色会让栏色先刷成白的、等真实外观出来再跳一次 —— 就是首屏白闪。
        // 推到 load 之后再发第一次；load 之后的迟到渲染由下面那个兜底采样兜住。
        var loadHandler = trigger('load');
        if (document.readyState === 'complete') {
            loadHandler();
        } else {
            window.addEventListener('load', loadHandler, { once: true });
            cleanups.push(function () { window.removeEventListener('load', loadHandler); });
            // SPA 常在 load 之后才渲染出真实外观（数据回来了才上色），补一次迟到但准的采样。
            // 颜色没变的话 dispatch 里的去重会把它吃掉，不会多刷一次。
            fallbackTimeout = setTimeout(function () {
                fallbackTimeout = null;
                trigger('fallback')();
            }, 1000);
        }
    };

    if (document.readyState === 'loading') {
        var onDomReady = function () { start(); };
        document.addEventListener('DOMContentLoaded', onDomReady, { once: true });
        cleanups.push(function () { document.removeEventListener('DOMContentLoaded', onDomReady); });
    } else {
        start();
    }
})();
";
    }
}
