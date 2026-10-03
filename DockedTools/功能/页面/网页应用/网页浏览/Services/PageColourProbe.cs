using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
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
    ///    click / resize / scroll / visibilitychange 四个事件，
    ///    以及 darkReader、meta theme-color 属性、meta 标签增删、STYLE 标签增删四个 MutationObserver，
    ///    统一走 250ms trailing 节流后回传。
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

    var __atbcElementColour = function (element) {
        if (!(element instanceof Element)) { return null; }
        var style = getComputedStyle(element);
        var background = style.backgroundColor;
        if (!background || background === 'rgba(0, 0, 0, 0)' || background === 'transparent') { return null; }
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

    var __atbcPageColour = function () {
        return document.elementsFromPoint(window.innerWidth / 2, 3)
            .filter(function (element) {
                return element instanceof HTMLElement &&
                    element.offsetWidth >= window.innerWidth * 0.9 &&
                    element.offsetHeight >= 20;
            })
            .map(function (element) { return __atbcElementColour(element); })
            .concat([__atbcElementColour(document.body), __atbcElementColour(document.documentElement)])
            .filter(function (data) { return data !== null; });
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
" + ColourHelpers + @"
    var dispatch = function () {
        // 动态刷新（ATBC: dynamic）在 dispatch 里查，不在 start() 里查：
        // start() 只在文档创建时跑一次，在那里判断的话，开关关掉再打开就再也装不回监听了
        // （内核没有「让脚本在当前文档重跑一遍」的入口，只有导航到新文档才会重新执行），
        // 结果就是「关掉再打开 → 必须导航一次才恢复」。
        // 放到 dispatch 里就是纯运行时开关：监听照装，发不发由变量说了算，
        // 关→开、开→关两个方向都能立刻生效。
        if (window.__dockedToolsColourDynamic === false) { return; }
        if (window.__dockedToolsColourSuspended === true) { return; }
        if (document.visibilityState !== 'visible') { return; }
        // 全屏（视频播放等）时取到的是视频画面而不是页面外观，保持当前栏色不动。
        if (document.fullscreenElement) { return; }
        lastSentAt = Date.now();
        try {
            // 去重：scroll / resize / click 会反复触发，但页面颜色往往没变。
            // 颜色没变就不跨进程发消息，也免掉托管侧一轮 Evaluate + XAML 失效。
            var colourJson = JSON.stringify(__atbcColourData(currentQuery()));
            if (colourJson === lastPayload) { return; }
            lastPayload = colourJson;
            window.chrome.webview.postMessage('{""header"":""" + MessageHeader + @""",""colour"":' + colourJson + '}');
        } catch (e) { }
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

        var darkReaderObserver = new MutationObserver(sendColour);
        var metaThemeColourObserver = new MutationObserver(sendColour);
        var metaTagObserver = new MutationObserver(function (mutationList) {
            mutationList.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node instanceof HTMLMetaElement && node.name === 'theme-color') {
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
            if (touched) { sendColour(); }
        });

        // passive：这几个监听不会 preventDefault，声明成 passive 让滚动不必等我们的回调
        ['click', 'resize', 'scroll'].forEach(function (event) {
            document.addEventListener(event, sendColour, { passive: true });
            cleanups.push(function () { document.removeEventListener(event, sendColour); });
        });
        document.addEventListener('visibilitychange', sendColour);
        cleanups.push(function () { document.removeEventListener('visibilitychange', sendColour); });
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

        // 首屏：DOMContentLoaded 时页面常常还只有浏览器的默认白底（CSS / 图片 / 字体没到位），
        // 这时取色会让栏色先刷成白的、等真实外观出来再跳一次 —— 就是首屏白闪。
        // 推到 load 之后再发第一次；load 之后的迟到渲染由下面那个兜底采样兜住。
        if (document.readyState === 'complete') {
            sendColour();
        } else {
            window.addEventListener('load', sendColour, { once: true });
            cleanups.push(function () { window.removeEventListener('load', sendColour); });
            // SPA 常在 load 之后才渲染出真实外观（数据回来了才上色），补一次迟到但准的采样。
            // 颜色没变的话 dispatch 里的去重会把它吃掉，不会多刷一次。
            fallbackTimeout = setTimeout(function () {
                fallbackTimeout = null;
                sendColour();
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
