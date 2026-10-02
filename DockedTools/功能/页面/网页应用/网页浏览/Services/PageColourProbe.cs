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

        /// <summary>元素自身不透明度（字符串原值解析后的数值）</summary>
        public double Opacity { get; set; } = 1d;

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
    /// ⚠️ 刻意不去监听 transition{end,cancel} / animation{end,cancel}：
    ///    上游 enableDynamic() 确实会挂这四个事件（且要求 document.hasFocus()），初衷是抓 SPA 的
    ///    主题过渡。但代价是**页面每播完一段 CSS 动画，整条取色流水线就重跑一遍** ——
    ///    而 CSS 动画在真实站点上是高频事件：轮播图、骨架屏、进度条、光标闪烁都在持续触发。
    ///    更糟的是动画刚结束时页面往往还停在过渡态，取到的是中间色而不是稳定色，
    ///    这个中间色会被一路写到色块与底栏上，视觉上就是「颜色跳回默认」。
    ///    移除后剩下的触发源已覆盖真正需要刷新的场景：
    ///       · SPA 路由切换      → click + MutationObserver
    ///       · 主题切换          → MutationObserver（STYLE 标签增删 + darkReader 属性变更）
    ///       · meta theme-color  → metaThemeColourObserver
    ///       · 布局 / 滚动变化   → resize / scroll
    ///    本机侧还有第二道防线：写入前的脏值拦截（见 WebBrowserPage.ApplyAdaptiveBarColour
    ///    与 BottomBarThemeService.SetBottomBar）。一道减少无效工作，一道保证落地幂等，互不冲突。
    ///
    /// 与上游的差异：
    /// 1. 回传通道用 WebView2 的 chrome.webview.postMessage，替代 browser.runtime.sendMessage；
    /// 2. 颜色归一化加哨兵色校验，避免非法值被静默解析成黑色；
    /// 3. 特殊页面判定去掉 Firefox 专属资源（chrome://、resource://），改用 SVG / 纯文本文档判定；
    /// 4. 常驻脚本在 readyState='loading' 时挂到 DOMContentLoaded 再启动
    ///    （AddScriptToExecuteOnDocumentCreated 注入时 document.head/body 还不存在）。
    /// </summary>
    public static class PageColourProbe
    {
        private const string QueryToken = "__ATBC_QUERY__";

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

        private static string InjectQuery(string script, string? query)
        {
            string injected = query is null
                ? "null"
                : "'" + query.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", " ").Replace("\n", " ") + "'";

            return script.Replace(QueryToken, injected);
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

            return data;
        }

        private static AdaptiveElementColour? ReadElementColour(JsonElement item)
        {
            string? colourText = ReadString(item, "colour");
            if (colourText is null || !AdaptiveColour.TryParse(colourText, out AdaptiveColour colour))
            {
                return null;
            }

            string? opacityText = ReadString(item, "opacity");
            double opacity = 1d;
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
    var __atbcNormalise = function (value) {
        if (!value) { return null; }
        try {
            var canvas = document.createElement('canvas');
            canvas.width = 1;
            canvas.height = 1;
            var context = canvas.getContext('2d');
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
            special: page.length > 0 ? 'none' : __atbcSpecial()
        };
    };
";

        private const string ProbeScript = @"
(function () {
    var query = __ATBC_QUERY__;
" + ColourHelpers + @"
    return JSON.stringify(__atbcColourData(query));
})();
";

        private const string MonitorScript = @"
(function () {
    if (window.__dockedToolsColourMonitor) { return; }
    window.__dockedToolsColourMonitor = true;

    var query = __ATBC_QUERY__;
    var throttleIntervalMs = 250;
    var dispatchTimeout = null;
    var lastSentAt = 0;
" + ColourHelpers + @"
    var dispatch = function () {
        if (document.visibilityState !== 'visible') { return; }
        lastSentAt = Date.now();
        try {
            window.chrome.webview.postMessage(JSON.stringify({
                header: '" + MessageHeader + @"',
                colour: __atbcColourData(query)
            }));
        } catch (e) { }
    };

    var sendColour = function () {
        if (dispatchTimeout) { clearTimeout(dispatchTimeout); dispatchTimeout = null; }
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

        ['click', 'resize', 'scroll', 'visibilitychange'].forEach(function (event) {
            document.addEventListener(event, sendColour);
        });

        darkReaderObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['data-darkreader-mode']
        });
        document.querySelectorAll('meta[name=theme-color]').forEach(function (metaTag) {
            metaThemeColourObserver.observe(metaTag, { attributes: true });
        });
        if (document.head) { metaTagObserver.observe(document.head, { childList: true }); }
        styleTagObserver.observe(document.documentElement, { childList: true });
        if (document.head) { styleTagObserver.observe(document.head, { childList: true }); }

        sendColour();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
";
    }
}
