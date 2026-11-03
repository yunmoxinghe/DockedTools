namespace Svg
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 【DockedTools 补丁文件】上游 wieslawsoltes/Svg.Skia commit 7910666415a9 (MIT)。
    // 本文件的改动只有 PrefersColorScheme 属性、两个方案常量和 Clone 里的一行 ——
    // 给 @media (prefers-color-scheme) 提供由调用方强制求值的入口，
    // 配套求值逻辑见 Compatibility\SvgCssCompatibilityProcessor.cs 的同源补丁注释。
    // 升级这份源码时必须把两处补丁一起重放，否则图标深浅版会全部退回浅色分支。
    // ─────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Parser/model options for SVG 2 static-subset loading.
    /// </summary>
    public sealed class SvgDocumentLoadOptions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SvgDocumentLoadOptions"/> class.
        /// </summary>
        public SvgDocumentLoadOptions()
        {
            ProcessingMode = SvgProcessingMode.Static;
            ExternalResources = SvgExternalResourcePolicy.Enabled;
            PreserveUnknownElements = true;
            PreferSvg2Href = true;
            PrefersColorScheme = ColorSchemeLight;
        }

        /// <summary>Canonical value for the light color scheme (CSS default).</summary>
        public const string ColorSchemeLight = "light";

        /// <summary>Canonical value for the dark color scheme.</summary>
        public const string ColorSchemeDark = "dark";

        /// <summary>
        /// Gets or sets the SVG processing mode.
        /// </summary>
        public SvgProcessingMode ProcessingMode { get; set; }

        /// <summary>
        /// Gets or sets the external resource policy.
        /// </summary>
        public SvgExternalResourcePolicy ExternalResources { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unsupported elements should be preserved in the model.
        /// </summary>
        public bool PreserveUnknownElements { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether SVG 2 unnamespaced href should win over xlink:href.
        /// </summary>
        public bool PreferSvg2Href { get; set; }

        /// <summary>
        /// Gets or sets the color scheme used when evaluating the
        /// <c>prefers-color-scheme</c> media feature inside <c>&lt;style&gt;</c> blocks
        /// and <c>@media</c> rules. Recognized values are <see cref="ColorSchemeLight"/>
        /// and <see cref="ColorSchemeDark"/>; anything else (including the historical
        /// <c>no-preference</c>) resolves to light, matching the CSS specification default.
        /// </summary>
        public string PrefersColorScheme { get; set; }

        /// <summary>
        /// Creates a copy of this options object.
        /// </summary>
        /// <returns>A detached copy with the same option values.</returns>
        public SvgDocumentLoadOptions Clone()
        {
            return new SvgDocumentLoadOptions
            {
                ProcessingMode = ProcessingMode,
                ExternalResources = ExternalResources,
                PreserveUnknownElements = PreserveUnknownElements,
                PreferSvg2Href = PreferSvg2Href,
                PrefersColorScheme = PrefersColorScheme
            };
        }
    }
}
