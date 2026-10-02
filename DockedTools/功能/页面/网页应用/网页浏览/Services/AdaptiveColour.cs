using System;
using System.Globalization;

namespace DockedTools.Features.Pages.WebApp.Browser.Services
{
    /// <summary>
    /// 颜色方案（亮/暗），对应 ATBC 的 Scheme
    /// </summary>
    public enum AdaptiveScheme
    {
        Light,
        Dark
    }

    /// <summary>
    /// 对比度校正结果（对应 ATBC 的 ColourCorrectionResult）
    /// </summary>
    public readonly struct AdaptiveContrastResult
    {
        public AdaptiveContrastResult(AdaptiveColour colour, AdaptiveScheme scheme, bool corrected)
        {
            Colour = colour;
            Scheme = scheme;
            Corrected = corrected;
        }

        /// <summary>校正后的颜色</summary>
        public AdaptiveColour Colour { get; }

        /// <summary>该颜色适配的方案（决定前景色用黑还是白）</summary>
        public AdaptiveScheme Scheme { get; }

        /// <summary>是否发生了亮度校正（原始色对比度不达标）</summary>
        public bool Corrected { get; }
    }

    /// <summary>
    /// 自适应栏色运算核心。
    /// 移植自 Adaptive Tab Bar Colour（ATBC）src/utils/colour.ts
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/colour.ts
    ///
    /// 与上游的差异：相对亮度使用 WCAG 2.x 精确公式（上游为分段线性近似，仅用于提速）。
    /// </summary>
    public readonly struct AdaptiveColour
    {
        private const double ChannelMax = 255d;

        public AdaptiveColour(double r, double g, double b, double a = 1d)
        {
            R = Math.Clamp(r, 0d, ChannelMax);
            G = Math.Clamp(g, 0d, ChannelMax);
            B = Math.Clamp(b, 0d, ChannelMax);
            A = Math.Clamp(a, 0d, 1d);
        }

        public double R { get; }
        public double G { get; }
        public double B { get; }
        public double A { get; }

        public static AdaptiveColour Transparent => new(0d, 0d, 0d, 0d);
        public static AdaptiveColour Black => new(0d, 0d, 0d);
        public static AdaptiveColour White => new(ChannelMax, ChannelMax, ChannelMax);

        public bool IsOpaque => A >= 1d;

        public static AdaptiveColour FromColor(Windows.UI.Color color)
            => new(color.R, color.G, color.B, color.A / ChannelMax);

        public Windows.UI.Color ToColor()
            => Windows.UI.Color.FromArgb(
                (byte)Math.Round(A * ChannelMax),
                (byte)Math.Round(R),
                (byte)Math.Round(G),
                (byte)Math.Round(B));

        /// <summary>应用不透明度系数（ATBC: opacity）</summary>
        public AdaptiveColour Opacity(double opacity)
            => new(R, G, B, A * Math.Clamp(opacity, 0d, 1d));

        /// <summary>
        /// 将当前色作为上层，与下层颜色做 source-over 合成（ATBC: mix）
        /// 注意：参数扮演的是「下面那层」，调用链上先出现的元素在最上层。
        /// </summary>
        public AdaptiveColour Mix(AdaptiveColour under)
        {
            double a = A + under.A * (1d - A);
            if (a <= 0d)
            {
                return Transparent;
            }

            return new AdaptiveColour(
                (A * R + under.A * (1d - A) * under.R) / a,
                (A * G + under.A * (1d - A) * under.G) / a,
                (A * B + under.A * (1d - A) * under.B) / a,
                a);
        }

        /// <summary>
        /// 调整明暗（ATBC: brightness）
        /// 正值向白靠近（100 = 纯白），负值向黑靠近（-100 = 纯黑），0 保持不变。
        /// </summary>
        public AdaptiveColour Brightness(double percentage)
        {
            double cent = percentage / 100d;
            if (cent > 1d)
            {
                return new AdaptiveColour(ChannelMax, ChannelMax, ChannelMax, A);
            }

            if (cent > 0d)
            {
                return new AdaptiveColour(
                    cent * ChannelMax + (1d - cent) * R,
                    cent * ChannelMax + (1d - cent) * G,
                    cent * ChannelMax + (1d - cent) * B,
                    A);
            }

            if (cent == 0d)
            {
                return this;
            }

            if (cent >= -1d)
            {
                return new AdaptiveColour((cent + 1d) * R, (cent + 1d) * G, (cent + 1d) * B, A);
            }

            return new AdaptiveColour(0d, 0d, 0d, A);
        }

        /// <summary>WCAG 相对亮度（0~1）</summary>
        public double RelativeLuminance()
            => 0.2126 * ChannelLuminance(R) + 0.7152 * ChannelLuminance(G) + 0.0722 * ChannelLuminance(B);

        /// <summary>相对亮度 × 255（ATBC 内部以该尺度做对比度运算）</summary>
        public double LuminanceX255() => RelativeLuminance() * ChannelMax;

        /// <summary>
        /// 与另一色的对比度（ATBC: #contrastRatio）
        /// </summary>
        public double ContrastRatio(AdaptiveColour other)
        {
            double l1 = LuminanceX255();
            double l2 = other.LuminanceX255();
            return l1 > l2
                ? (l1 + 12.75) / (l2 + 12.75)
                : (l2 + 12.75) / (l1 + 12.75);
        }

        /// <summary>
        /// 对比度校正（ATBC: contrastCorrection）
        /// 亮色方案要求前景黑字对比度达标，暗色方案要求前景白字对比度达标；
        /// 不达标时按方案把背景提亮（亮色方案）或压暗（暗色方案）。
        /// </summary>
        /// <param name="preferredScheme">当前期望的方案（来自系统/应用主题）</param>
        /// <param name="allowDarkLight">是否允许在暗方案下采用亮色背景（或反之）</param>
        /// <param name="minContrastLightX10">亮色方案最小对比度 ×10（ATBC 默认 90）</param>
        /// <param name="minContrastDarkX10">暗色方案最小对比度 ×10（ATBC 默认 45）</param>
        /// <remarks>
        /// 对应 ATBC 的 Colour.contrastCorrection：
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/colour.ts
        /// </remarks>
        public AdaptiveContrastResult ContrastCorrection(
            AdaptiveScheme preferredScheme,
            bool allowDarkLight,
            double minContrastLightX10,
            double minContrastDarkX10)
        {
            double ratioLight = ContrastRatio(Black);
            double ratioDark = ContrastRatio(White);
            bool eligibleLight = ratioLight > minContrastLightX10 / 10d;
            bool eligibleDark = ratioDark > minContrastDarkX10 / 10d;

            if (eligibleLight &&
                (preferredScheme == AdaptiveScheme.Light ||
                 (preferredScheme == AdaptiveScheme.Dark && allowDarkLight)))
            {
                return new AdaptiveContrastResult(this, AdaptiveScheme.Light, false);
            }

            if (eligibleDark &&
                (preferredScheme == AdaptiveScheme.Dark ||
                 (preferredScheme == AdaptiveScheme.Light && allowDarkLight)))
            {
                return new AdaptiveContrastResult(this, AdaptiveScheme.Dark, false);
            }

            if (preferredScheme == AdaptiveScheme.Light)
            {
                double luminance = LuminanceX255();
                double denominator = ChannelMax - luminance;
                if (denominator <= 0d)
                {
                    denominator = 1e-6; // 纯白时避免除零
                }

                double dim = 100d *
                    ((minContrastLightX10 / (10d * ratioLight) - 1d) * (luminance + 12.75)) /
                    denominator;
                return new AdaptiveContrastResult(Brightness(dim), AdaptiveScheme.Light, true);
            }

            double dimDark = 100d * (10d * ratioDark) / minContrastDarkX10 - 100d;
            return new AdaptiveContrastResult(Brightness(dimDark), AdaptiveScheme.Dark, true);
        }

        private static double ChannelLuminance(double value)
        {
            double c = Math.Clamp(value, 0d, ChannelMax) / ChannelMax;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// 解析 CSS 颜色字符串：#rgb / #rgba / #rrggbb / #rrggbbaa / rgb() / rgba()（逗号或空格语法）/ transparent。
        /// 颜色关键字请在 JS 侧先归一化。
        /// </summary>
        public static bool TryParse(string? css, out AdaptiveColour colour)
        {
            colour = Transparent;
            if (string.IsNullOrWhiteSpace(css))
            {
                return false;
            }

            string s = css.Trim();

            if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (s.StartsWith('#'))
            {
                return TryParseHex(s.Substring(1), out colour);
            }

            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            bool isRgbFunction =
                s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);

            if (open > 0 && close > open && isRgbFunction)
            {
                return TryParseRgb(s.Substring(open + 1, close - open - 1), out colour);
            }

            return false;
        }

        private static bool TryParseHex(string hex, out AdaptiveColour colour)
        {
            colour = Transparent;

            if (hex.Length == 3 || hex.Length == 4)
            {
                string expanded = string.Empty;
                foreach (char c in hex)
                {
                    expanded += new string(c, 2);
                }

                hex = expanded;
            }

            if (hex.Length != 6 && hex.Length != 8)
            {
                return false;
            }

            if (!byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return false;
            }

            byte a = byte.MaxValue;
            if (hex.Length == 8 &&
                !byte.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
            {
                return false;
            }

            colour = new AdaptiveColour(r, g, b, a / ChannelMax);
            return true;
        }

        private static bool TryParseRgb(string inner, out AdaptiveColour colour)
        {
            colour = Transparent;

            string[] tokens = inner.Split(new[] { ',', '/', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return false;
            }

            if (!TryParseChannel(tokens[0], out double r) ||
                !TryParseChannel(tokens[1], out double g) ||
                !TryParseChannel(tokens[2], out double b))
            {
                return false;
            }

            double a = 1d;
            if (tokens.Length > 3 && TryParseAlpha(tokens[3], out double parsedAlpha))
            {
                a = parsedAlpha;
            }

            colour = new AdaptiveColour(r, g, b, a);
            return true;
        }

        private static bool TryParseChannel(string token, out double value)
        {
            value = 0d;
            string trimmed = token.Trim();

            if (trimmed.EndsWith("%", StringComparison.Ordinal))
            {
                if (!double.TryParse(trimmed.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    return false;
                }

                value = Math.Clamp(percent, 0d, 100d) / 100d * ChannelMax;
                return true;
            }

            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            {
                return false;
            }

            value = Math.Clamp(raw, 0d, ChannelMax);
            return true;
        }

        private static bool TryParseAlpha(string token, out double value)
        {
            value = 1d;
            string trimmed = token.Trim();

            if (trimmed.EndsWith("%", StringComparison.Ordinal))
            {
                if (!double.TryParse(trimmed.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    return false;
                }

                value = Math.Clamp(percent, 0d, 100d) / 100d;
                return true;
            }

            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            {
                return false;
            }

            value = Math.Clamp(raw, 0d, 1d);
            return true;
        }
    }
}
