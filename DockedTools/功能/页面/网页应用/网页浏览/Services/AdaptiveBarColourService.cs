using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser.Services
{
    /// <summary>
    /// 自适应栏色配置（对齐 ATBC 的 defaultPreferenceContent）
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/constants.ts
    /// </summary>
    public sealed class AdaptiveBarColourOptions
    {
        /// <summary>
        /// 亮色方案兜底色。
        /// 取 WinUI 3 的 <c>ApplicationPageBackgroundThemeBrush</c>（= <c>SolidBackgroundFillColorBase</c>
        /// 在 Light 字典里的值 <c>#F3F3F3</c>，见 WindowsAppSDK 2.5.1 的 Microsoft.WinUI/Themes/generic.xaml），
        /// 而不是 ATBC 上游的 <c>#ffffff</c>：这条兜底色最终要刷在 WinUI 控件上，用浏览器那套白
        /// 会跟周围的应用背景对不上。与页面复位时读的是同一个主题资源，因此"取不到色"和
        /// "没开自适应"两种状态在视觉上完全一致。
        /// </summary>
        public Windows.UI.Color FallbackLight { get; set; } = FromCss("#f3f3f3");

        /// <summary>
        /// 暗色方案兜底色。同 Light 的口径，取 <c>SolidBackgroundFillColorBase</c> 在 Dark 字典里的
        /// <c>#202020</c>。ATBC 上游那版 <c>#2b2a33</c> 是 Firefox 的暗色底（偏紫），落在 WinUI 里很扎眼。
        /// </summary>
        public Windows.UI.Color FallbackDark { get; set; } = FromCss("#202020");

        /// <summary>亮色方案最小对比度 ×10（ATBC 默认 90，即 9.0）</summary>
        public double MinContrastLightX10 { get; set; } = 90d;

        /// <summary>暗色方案最小对比度 ×10（ATBC 默认 45，即 4.5）</summary>
        public double MinContrastDarkX10 { get; set; } = 45d;

        /// <summary>是否允许在暗方案下使用亮色背景（或反之）</summary>
        public bool AllowDarkLight { get; set; } = true;

        /// <summary>
        /// 是否忽略 meta theme-color（ATBC 默认 true）。
        /// 为 true 时优先使用页面真实外观色，theme-color 仅作为品牌色被忽略。
        /// </summary>
        public bool NoThemeColour { get; set; } = true;

        /// <summary>栏体亮度偏移（ATBC: tabbar，默认 0）</summary>
        public double TabBar { get; set; } = 0d;

        /// <summary>
        /// 浮层亮度偏移（ATBC: popup，默认 5）。
        /// <b>当前无消费点</b>：ATBC 用它给浏览器弹出菜单着色，我们没有对应浮层 UI，
        /// 设置页也不再暴露该项。保留字段只为与上游默认值一致，将来接浮层时直接可用。
        /// </summary>
        public double Popup { get; set; } = 5d;

        /// <summary>
        /// 选中态亮度偏移（ATBC: tabSelected，默认 15）。
        /// <b>当前无消费点</b>：ATBC 用它给「当前激活标签页」着色，我们没有标签页条。
        /// </summary>
        public double TabSelected { get; set; } = 15d;

        /// <summary>可选 CSS 选择器：命中时优先使用该元素颜色（ATBC 的 QUERY_SELECTOR 规则）</summary>
        public string? Query { get; set; }

        /// <summary>图片查看器背景（ATBC: IMAGE_VIEWER）</summary>
        public Windows.UI.Color ImageViewer { get; set; } = FromCss("#212121");

        /// <summary>纯文本文档背景（亮，ATBC: PLAINTEXT）</summary>
        public Windows.UI.Color PlainTextLight { get; set; } = FromCss("#ffffff");

        /// <summary>纯文本文档背景（暗，ATBC: PLAINTEXT）</summary>
        public Windows.UI.Color PlainTextDark { get; set; } = FromCss("#1c1b22");

        /// <summary>SVG 文档背景（ATBC: SVG）</summary>
        public Windows.UI.Color Svg { get; set; } = FromCss("#ffffff");

        /// <summary>
        /// PDF 阅读器背景。ATBC 旧版对 PDF viewer 硬编码 rgb(56,56,61)；
        /// WebView2 自带的 PDF 预览同样会走这套，不给固定色就会取到阅读器那层灰底。
        /// </summary>
        public Windows.UI.Color PdfViewer { get; set; } = FromCss("#38383d");

        private static Windows.UI.Color FromCss(string css)
        {
            AdaptiveColour.TryParse(css, out AdaptiveColour colour);
            return colour.ToColor();
        }
    }

    /// <summary>
    /// 自适应栏色计算结果
    /// </summary>
    public sealed class AdaptiveBarColourResult
    {
        /// <summary>顶栏背景色（取自页面<b>顶端</b>那一带）</summary>
        public Windows.UI.Color Frame { get; init; }

        /// <summary>
        /// 底栏背景色（取自页面<b>底端</b>那一带）。
        /// 只有顶栏这一发色来自页面外观时才会与 <see cref="Frame"/> 不同；
        /// 特殊文档（图片 / 纯文本 / PDF）与规则指定色下两者相同 —— 那些场景整页就该一个色。
        /// </summary>
        public Windows.UI.Color Bottom { get; init; }

        /// <summary>浮层（弹出菜单等）背景色</summary>
        public Windows.UI.Color Popup { get; init; }

        /// <summary>选中态/激活态背景色</summary>
        public Windows.UI.Color Selected { get; init; }

        /// <summary>顶栏前景色（文字/图标）</summary>
        public Windows.UI.Color Foreground { get; init; }

        /// <summary>顶栏配色方案</summary>
        public AdaptiveScheme Scheme { get; init; }

        /// <summary>
        /// 底栏配色方案。<b>由底栏自己的背景色独立判定</b>，不跟顶栏走。
        ///
        /// <para>分别取色之后，顶栏与底栏是两块可能完全不同的背景 —— 顶栏白、底栏近黑
        /// 是完全正常的组合。前景（文字/图标）必须各按各的背景亮度来选，
        /// 让底栏沿用顶栏的方案就会出现「深色底栏 + 深色图标」。</para>
        ///
        /// <para>顶栏与底栏同色时（特殊文档 / 规则指定色 / 页面上下同色），
        /// 同一份颜色 + 同一份参数判出来的方案必然一致，不会打架。</para>
        /// </summary>
        public AdaptiveScheme BottomScheme { get; init; }

        /// <summary>是否经过对比度校正</summary>
        public bool Corrected { get; init; }

        /// <summary>取色来源（对齐 ATBC 的 TabMetaReason）</summary>
        public string Reason { get; init; } = "FALLBACK_COLOUR";
    }

    /// <summary>
    /// 自适应栏色服务。
    /// 移植自 Adaptive Tab Bar Colour（ATBC）background.ts 的
    /// parseTabColourData + setFrameColour + applyTheme 三段决策链：
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/background.ts
    /// </summary>
    public static class AdaptiveBarColourService
    {
        /// <summary>会以纯文本渲染的扩展名（ATBC: plainTextExtension）</summary>
        private static readonly string[] PlainTextExtensions =
        {
            ".css", ".ftl", ".js", ".locale", ".mjs", ".txt"
        };

        /// <summary>图片类扩展名（ATBC 的 getSourcePageMeta 只列了 png / jpg，这里按 WebView 常见格式补齐）</summary>
        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".avif", ".ico"
        };

        /// <summary>
        /// 计算当前页面应使用的栏色
        /// </summary>
        /// <param name="coreWebView">WebView2 内核</param>
        /// <param name="scheme">当前期望的方案（由系统/应用主题决定）</param>
        /// <param name="options">配置，null 时使用默认值</param>
        /// <param name="url">当前页面地址，用于图片类页面判定</param>
        public static async Task<AdaptiveBarColourResult> EvaluateAsync(
            CoreWebView2 coreWebView,
            AdaptiveScheme scheme,
            AdaptiveBarColourOptions? options = null,
            string? url = null,
            AdaptiveColourRule? rule = null)
        {
            options ??= new AdaptiveBarColourOptions();

            string? query = ResolveQuery(options, rule);
            AdaptiveTabColourData? data = await PageColourProbe.ProbeAsync(coreWebView, query);

            return Evaluate(data, scheme, options, url, rule);
        }

        /// <summary>
        /// 本次取色用哪个 CSS 选择器：站点规则的 QUERY_SELECTOR 优先于全局选择器。
        /// 对应 ATBC：query = rule?.type === "QUERY_SELECTOR" ? rule.value : undefined
        /// </summary>
        public static string? ResolveQuery(AdaptiveBarColourOptions options, AdaptiveColourRule? rule)
        {
            if (rule is { Type: AdaptiveRuleType.QuerySelector } && !string.IsNullOrWhiteSpace(rule.Value))
            {
                return rule.Value;
            }

            // 上游只有 QUERY_SELECTOR 规则会给脚本派选择器
            // （background: rule?.type === "QUERY_SELECTOR" ? rule.value : undefined）。
            // THEME_COLOUR / COLOUR 规则下决策链根本不看 query，
            // 让脚本去 querySelector 一次纯属白跑 —— 直接不派。
            // 没规则命中时才用设置里的全局选择器（这一项是我们比上游多的）。
            return rule is null ? options.Query : null;
        }

        /// <summary>
        /// 用一份已经探测到的颜色数据计算栏色（常驻监控回传的消息走这里，
        /// 与一次性探测共用同一条决策链）
        /// 对应 ATBC 的 parseTabColourData + setFrameColour + applyTheme 三段决策链：
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/background.ts
        /// </summary>
        public static AdaptiveBarColourResult Evaluate(
            AdaptiveTabColourData? data,
            AdaptiveScheme scheme,
            AdaptiveBarColourOptions? options = null,
            string? url = null,
            AdaptiveColourRule? rule = null)
        {
            options ??= new AdaptiveBarColourOptions();

            // ATBC：COLOUR 规则直接定色，连 content script 都要挂起（"SETUP_SCRIPT", mode: "suspend"），
            // 页面外观完全不参与 —— 这里同理，压根不看 data。
            if (rule is { Type: AdaptiveRuleType.Colour } &&
                AdaptiveColour.TryParse(rule.Value, out AdaptiveColour specified))
            {
                return Build(specified, scheme, options, "COLOUR_SPECIFIED");
            }

            Windows.UI.Color fallback = scheme == AdaptiveScheme.Light
                ? options.FallbackLight
                : options.FallbackDark;

            // PDF 提前按 URL 定色，不看探测结果。
            // WebView2 的 PDF 预览是内嵌的阅读器页面，探针脚本在那里要么拿不到可用的
            // 页面色、要么整个探测超时（data 为 null），而 URL 后缀是确凿的 ——
            // 放在 data 判定之前，PDF 才不会退化成兜底色。
            if (ResolveSpecialByUrl(url) == "pdf")
            {
                return Build(AdaptiveColour.FromColor(options.PdfViewer), scheme, options, "PDF_VIEWER");
            }

            if (data is null)
            {
                return Build(AdaptiveColour.FromColor(fallback), scheme, options, "FALLBACK_COLOUR");
            }

            AdaptiveColour pageColour = ParsePageColour(data.Page, fallback);
            string special = ResolveSpecial(data, url);

            // ⭐ 底栏分别取色：只有顶栏这一发色【来自页面外观】时才另采页面底端。
            // 图片查看器 / 纯文本 / PDF 这类整页一个底色的特殊文档，以及 COLOUR / THEME_COLOUR /
            // QUERY_SELECTOR 这些「站点或用户指定」的色，底栏都跟着顶栏走 ——
            // 跑去取页面底端只会把本该统一的外观撕成两半。
            AdaptiveColour? bottomColour = special == "none"
                ? ResolveBottomColour(data, fallback)
                : null;

            string? themeText = scheme == AdaptiveScheme.Light ? data.Theme.Light : data.Theme.Dark;
            AdaptiveColour themeColour = AdaptiveColour.Transparent;
            bool hasThemeColour = false;

            if (!string.IsNullOrWhiteSpace(themeText) &&
                AdaptiveColour.TryParse(themeText, out AdaptiveColour parsedTheme) &&
                parsedTheme.IsOpaque)
            {
                hasThemeColour = true;
                themeColour = parsedTheme;
            }

            // 对齐上游 parseQueryColour：只取 colour、不乘元素 opacity（只有 page 分支才乘）。
            // 乘了的话，命中元素只要不是完全不透明就会判定成 QS_FAILED，
            // 用户专门指定的选择器等于白写 —— 那比半透明带来的轻微偏色糟糕得多。
            AdaptiveColour? queryColour = data.Query is { } queryElement && queryElement.Colour.IsOpaque
                ? queryElement.Colour
                : null;

            // ATBC 的 getFallbackColour()：按 special 分派，只有 none 才落到页面色。
            // 判定顺序对齐上游 —— 先问 theme-color 在不在，special 只在回落里起作用。
            // 反过来的话（special 抢在前面），一个声明了品牌色的图片页会用错色。
            (AdaptiveColour fallbackColour, string fallbackReason) =
                ResolveFallback(special, scheme, options, pageColour);

            // 上游 parseTabColourData 是按 rule.type 分派的三段 switch，query 与 theme 互不串台：
            // THEME_COLOUR 分支压根不看 query，QUERY_SELECTOR 分支压根不看 theme-color。
            // 早先这里把 query 判定放在 switch 之前，THEME_COLOUR 规则会被 query 抢掉 ——
            // 站点规则等于失效，所以按上游结构重排。
            if (rule is { Type: AdaptiveRuleType.ThemeColour })
            {
                //   theme 存在 + value=true  → 用它（全局忽略时 reason 记 THEME_UNIGNORED）
                //   theme 存在 + value=false → 回落，THEME_IGNORED
                //   theme 缺失 + value=true  → 回落，THEME_MISSING（写了规则但页面没声明）
                //   theme 缺失 + value=false → 回落，COLOUR_PICKED
                bool useThemeColour = AdaptiveColourRuleTable.ParseThemeColourValue(rule.Value);

                if (useThemeColour && hasThemeColour)
                {
                    return Build(themeColour, scheme, options,
                        options.NoThemeColour ? "THEME_UNIGNORED" : "THEME_USED");
                }

                return Build(fallbackColour, scheme, options,
                    useThemeColour ? "THEME_MISSING"
                        : hasThemeColour ? "THEME_IGNORED" : "COLOUR_PICKED");
            }

            if (rule is { Type: AdaptiveRuleType.QuerySelector })
            {
                return queryColour is { } queried
                    ? Build(queried, scheme, options, "QS_USED")
                    : Build(fallbackColour, scheme, options, "QS_FAILED");
            }

            if (hasThemeColour)
            {
                return options.NoThemeColour
                    // 全局忽略 theme-color：拿页面外观色，此时全局选择器补位（我们比上游多的一项，
                    // 上游没有全局选择器概念，只有规则级 QUERY_SELECTOR）
                    ? queryColour is { } globalQuery
                        ? Build(globalQuery, scheme, options, "QS_USED")
                        : Build(fallbackColour, scheme, options, "THEME_IGNORED", bottomColour)
                    : Build(themeColour, scheme, options, "THEME_USED");
            }

            return queryColour is { } fallbackQuery
                ? Build(fallbackQuery, scheme, options, "QS_USED")
                : Build(fallbackColour, scheme, options, fallbackReason, bottomColour);
        }

        /// <summary>
        /// ATBC 的 getFallbackColour()：按 special 分派，默认回页面色。
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/background.ts
        /// </summary>
        private static (AdaptiveColour Colour, string Reason) ResolveFallback(
            string special,
            AdaptiveScheme scheme,
            AdaptiveBarColourOptions options,
            AdaptiveColour pageColour)
        {
            if (special == "image")
            {
                return (AdaptiveColour.FromColor(options.ImageViewer), "IMAGE_VIEWER");
            }

            // svg 上游单独给了色值，但 reason 与 image 同归 IMAGE_VIEWER
            if (special == "svg")
            {
                return (AdaptiveColour.FromColor(options.Svg), "IMAGE_VIEWER");
            }

            if (special == "plaintext")
            {
                return (AdaptiveColour.FromColor(
                        scheme == AdaptiveScheme.Light ? options.PlainTextLight : options.PlainTextDark),
                    "TEXT_VIEWER");
            }

            if (special == "pdf")
            {
                return (AdaptiveColour.FromColor(options.PdfViewer), "PDF_VIEWER");
            }

            return (pageColour, "COLOUR_PICKED");
        }

        /// <summary>
        /// 对比度校正 + 亮度偏移，产出最终结果（对应 ATBC 的 setFrameColour + applyTheme）
        /// </summary>
        private static AdaptiveBarColourResult Build(
            AdaptiveColour chosen,
            AdaptiveScheme scheme,
            AdaptiveBarColourOptions options,
            string reason,
            AdaptiveColour? bottom = null)
        {
            AdaptiveContrastResult correction = chosen.ContrastCorrection(
                scheme,
                options.AllowDarkLight,
                options.MinContrastLightX10,
                options.MinContrastDarkX10);

            // ATBC: css(value) = colour.brightness((light ? -1.5 : 1) * value)
            double direction = correction.Scheme == AdaptiveScheme.Light ? -1.5d : 1d;

            // 底栏走【自己那一整套】判定：用自己的颜色、传进来的期望方案各判一次，
            // 结果与顶栏无关。分别取色之后两块栏子的背景可能完全不同（顶栏白 / 底栏近黑
            // 是正常组合），前景必须各按各的背景亮度来选 —— 让底栏沿用顶栏方案
            // 就会出现「深色底栏 + 深色图标」。
            // 顶底同色时同一份输入判出来的方案必然一致，不会打架。
            AdaptiveContrastResult bottomCorrection = (bottom ?? chosen).ContrastCorrection(
                scheme,
                options.AllowDarkLight,
                options.MinContrastLightX10,
                options.MinContrastDarkX10);

            double bottomDirection = bottomCorrection.Scheme == AdaptiveScheme.Light ? -1.5d : 1d;

            return new AdaptiveBarColourResult
            {
                Frame = correction.Colour.Brightness(direction * options.TabBar).ToColor(),
                Bottom = bottomCorrection.Colour.Brightness(bottomDirection * options.TabBar).ToColor(),
                BottomScheme = bottomCorrection.Scheme,
                Popup = correction.Colour.Brightness(direction * options.Popup).ToColor(),
                Selected = correction.Colour.Brightness(direction * options.TabSelected).ToColor(),
                Foreground = correction.Scheme == AdaptiveScheme.Light
                    ? AdaptiveColour.Black.ToColor()
                    : AdaptiveColour.White.ToColor(),
                Scheme = correction.Scheme,
                Corrected = correction.Corrected,
                Reason = reason
            };
        }

        /// <summary>
        /// 页面色合成：从最上层元素往下做 alpha 合成，直到不透明；仍不透明则与兜底色混合。
        /// 对应 ATBC 的 parsePageColour：
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/background.ts
        /// </summary>
        private static AdaptiveColour ParsePageColour(List<AdaptiveElementColour> page, Windows.UI.Color fallback)
        {
            AdaptiveColour result = AdaptiveColour.Transparent;

            foreach (AdaptiveElementColour element in page)
            {
                // ATBC: if (isNaN(opacity)) continue; —— 拿不到有效不透明度的元素不参与合成
                if (element.Opacity is not { } opacity)
                {
                    continue;
                }

                result = result.Mix(element.Colour.Opacity(opacity));
                if (result.IsOpaque)
                {
                    return result;
                }
            }

            return result.Mix(AdaptiveColour.FromColor(fallback));
        }

        /// <summary>
        /// 底栏取色：解析页面<b>底端</b>那一带的采样。
        /// 与顶部共用 <see cref="ParsePageColour"/>，过滤与合成规则完全一致，只有输入不同。
        /// 采不到（老版本脚本没有 pageBottom 字段）时返回 null —— 调用方退化成「底栏跟随顶栏」。
        /// </summary>
        private static AdaptiveColour? ResolveBottomColour(
            AdaptiveTabColourData data,
            Windows.UI.Color fallback)
            => data.PageBottom.Count == 0 ? null : ParsePageColour(data.PageBottom, fallback);

        private static string ResolveSpecial(AdaptiveTabColourData data, string? url)
        {
            if (data.Special != "none")
            {
                return data.Special;
            }

            return ResolveSpecialByUrl(url);
        }

        /// <summary>
        /// 只按 URL 判特殊文档类型 —— 探测失败（data 为 null）时也能独立用。
        /// </summary>
        private static string ResolveSpecialByUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "none";
            }

            // PDF 不是 HTML 文档，探针脚本在 PDF 预览页里基本拿不到可用的页面色
            // （取到的是阅读器那层灰底，还会随滚动/翻页乱跳）。URL 带不带 query 都要认，
            // 所以除了后缀判定，data:application/pdf 也在这里一并兜住。
            if (url.StartsWith("data:application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                return "pdf";
            }

            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                return "image";
            }

            int queryIndex = url.IndexOf('?');
            string path = queryIndex >= 0 ? url.Substring(0, queryIndex) : url;

            // 判定顺序对齐上游 getSourcePageMeta：纯文本扩展名在图片之前。
            // PDF 排在纯文本之前 —— .pdf 不会撞上 plaintext / image 两张表，
            // 放前面只是为了读起来「特殊文档类型」聚在一起。
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return "pdf";
            }

            foreach (string extension in PlainTextExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return "plaintext";
                }
            }

            foreach (string extension in ImageExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return "image";
                }
            }

            return "none";
        }
    }
}
