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
        /// <summary>亮色方案兜底色</summary>
        public Windows.UI.Color FallbackLight { get; set; } = FromCss("#ffffff");

        /// <summary>暗色方案兜底色</summary>
        public Windows.UI.Color FallbackDark { get; set; } = FromCss("#2b2a33");

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

        /// <summary>浮层亮度偏移（ATBC: popup，默认 5）</summary>
        public double Popup { get; set; } = 5d;

        /// <summary>选中态亮度偏移（ATBC: tabSelected，默认 15）</summary>
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
        /// <summary>栏体（顶栏/底栏）背景色</summary>
        public Windows.UI.Color Frame { get; init; }

        /// <summary>浮层（弹出菜单等）背景色</summary>
        public Windows.UI.Color Popup { get; init; }

        /// <summary>选中态/激活态背景色</summary>
        public Windows.UI.Color Selected { get; init; }

        /// <summary>前景色（文字/图标）</summary>
        public Windows.UI.Color Foreground { get; init; }

        public AdaptiveScheme Scheme { get; init; }

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
            string? url = null)
        {
            options ??= new AdaptiveBarColourOptions();

            AdaptiveTabColourData? data = await PageColourProbe.ProbeAsync(coreWebView, options.Query);

            return Evaluate(data, scheme, options, url);
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
            string? url = null)
        {
            options ??= new AdaptiveBarColourOptions();

            Windows.UI.Color fallback = scheme == AdaptiveScheme.Light
                ? options.FallbackLight
                : options.FallbackDark;

            AdaptiveColour chosen;
            string reason;

            if (data is null)
            {
                chosen = AdaptiveColour.FromColor(fallback);
                reason = "FALLBACK_COLOUR";
            }
            else
            {
                AdaptiveColour pageColour = ParsePageColour(data.Page, fallback);
                string special = ResolveSpecial(data, url);

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

                // ATBC: QUERY_SELECTOR 规则 —— 显式指定选择器时优先级最高
                AdaptiveColour? queryColour = null;
                if (data.Query is { } queryElement)
                {
                    AdaptiveColour candidate = queryElement.Colour.Opacity(queryElement.Opacity);
                    if (candidate.IsOpaque)
                    {
                        queryColour = candidate;
                    }
                }

                if (queryColour is { } query)
                {
                    chosen = query;
                    reason = "QS_USED";
                }
                else if (special == "image")
                {
                    chosen = AdaptiveColour.FromColor(options.ImageViewer);
                    reason = "IMAGE_VIEWER";
                }
                else if (special == "svg")
                {
                    chosen = AdaptiveColour.FromColor(options.Svg);
                    reason = "IMAGE_VIEWER";
                }
                else if (special == "plaintext")
                {
                    chosen = AdaptiveColour.FromColor(
                        scheme == AdaptiveScheme.Light ? options.PlainTextLight : options.PlainTextDark);
                    reason = "TEXT_VIEWER";
                }
                else if (hasThemeColour && !options.NoThemeColour)
                {
                    chosen = themeColour;
                    reason = "THEME_USED";
                }
                else
                {
                    chosen = pageColour;
                    reason = hasThemeColour ? "THEME_IGNORED" : "COLOUR_PICKED";
                }
            }

            AdaptiveContrastResult correction = chosen.ContrastCorrection(
                scheme,
                options.AllowDarkLight,
                options.MinContrastLightX10,
                options.MinContrastDarkX10);

            // ATBC: css(value) = colour.brightness((light ? -1.5 : 1) * value)
            double direction = correction.Scheme == AdaptiveScheme.Light ? -1.5d : 1d;

            return new AdaptiveBarColourResult
            {
                Frame = correction.Colour.Brightness(direction * options.TabBar).ToColor(),
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
                result = result.Mix(element.Colour.Opacity(element.Opacity));
                if (result.IsOpaque)
                {
                    return result;
                }
            }

            return result.Mix(AdaptiveColour.FromColor(fallback));
        }

        private static string ResolveSpecial(AdaptiveTabColourData data, string? url)
        {
            if (data.Special != "none")
            {
                return data.Special;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return "none";
            }

            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                return "image";
            }

            int queryIndex = url.IndexOf('?');
            string path = queryIndex >= 0 ? url.Substring(0, queryIndex) : url;

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
