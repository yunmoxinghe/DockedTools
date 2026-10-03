using System;
using System.Collections.Generic;
using Windows.Storage;
using DockedTools.Features.Shared.AotOptimization;
using DockedTools.Features.Pages.WebApp.Browser.Services;

namespace DockedTools.Features.Pages.Settings
{
    /// <summary>
    /// 自适应栏色设置（AOT 安全存储）
    ///
    /// <para>
    /// 键名与默认值对齐 ATBC 的 <c>defaultPreferenceContent</c>：
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/constants.ts
    /// 我们只实现了「栏色」这一条产品线，所以 ATBC 里那些浏览器专属项
    /// （sidebar / toolbar / toolbarField / *Border / accentColour / homeBackground / compatibilityMode）
    /// 不搬运 —— 没有对应的 UI 可写。
    /// </para>
    ///
    /// <para>
    /// 改任何一项都会触发 <see cref="Changed"/>，网页浏览页面订阅它即时重算栏色
    /// （复用最近一次取色数据，不重新探测页面）。
    /// </para>
    /// </summary>
    public static class AdaptiveColourSettings
    {
        private const string EnabledKey = "AdaptiveColour_Enabled";
        private const string DynamicKey = "AdaptiveColour_Dynamic";
        private const string SchemeKey = "AdaptiveColour_Scheme";

        private const string NoThemeColourKey = "AdaptiveColour_NoThemeColour";
        private const string AllowDarkLightKey = "AdaptiveColour_AllowDarkLight";

        private const string TabBarKey = "AdaptiveColour_TabBar";

        private const string MinContrastLightKey = "AdaptiveColour_MinContrastLight";
        private const string MinContrastDarkKey = "AdaptiveColour_MinContrastDark";

        private const string FallbackLightKey = "AdaptiveColour_FallbackLight";
        private const string FallbackDarkKey = "AdaptiveColour_FallbackDark";

        private const string QueryKey = "AdaptiveColour_Query";

        /// <summary>
        /// 站点规则表（ATBC: ruleList）。
        /// 上游默认是空的（站点特例交给用户配），我们额外预置了 ATBC 早期版本那批内置规则。
        /// </summary>
        private const string RuleListKey = "AdaptiveColour_RuleList";

        /// <summary>规则表是否已经落盘（没落盘时读取内置默认值）</summary>
        private const string RuleListInitializedKey = "AdaptiveColour_RuleListInitialized";

        private static readonly ApplicationDataContainer LocalSettings = ApplicationData.Current.LocalSettings;

        /// <summary>任一设置项发生变化（页面据此即时重算栏色）</summary>
        public static event EventHandler? Changed;

        /// <summary>总开关。关闭后完全回退到系统默认栏色（ATBC 无对应项，是我们自己的总闸）</summary>
        public static bool Enabled
        {
            get => AotSafeSettingsHelper.GetBool(LocalSettings, EnabledKey, defaultValue: true);
            set => SetBool(EnabledKey, value);
        }

        /// <summary>动态刷新（ATBC: dynamic，默认 true）。关闭后只在导航完成时取一次色</summary>
        public static bool Dynamic
        {
            get => AotSafeSettingsHelper.GetBool(LocalSettings, DynamicKey, defaultValue: true);
            set => SetBool(DynamicKey, value);
        }

        /// <summary>配色方案（ATBC 由浏览器主题决定；这里允许强制浅/深）</summary>
        public static AdaptiveColourSchemeMode Scheme
        {
            get => AotSafeSettingsHelper.GetEnum(LocalSettings, SchemeKey, AdaptiveColourSchemeMode.System);
            set => SetEnum(SchemeKey, value);
        }

        /// <summary>忽略 meta theme-color（ATBC: noThemeColour，默认 true）</summary>
        public static bool NoThemeColour
        {
            get => AotSafeSettingsHelper.GetBool(LocalSettings, NoThemeColourKey, defaultValue: true);
            set => SetBool(NoThemeColourKey, value);
        }

        /// <summary>允许亮背景配暗方案 / 暗背景配亮方案（ATBC: allowDarkLight，默认 true）</summary>
        public static bool AllowDarkLight
        {
            get => AotSafeSettingsHelper.GetBool(LocalSettings, AllowDarkLightKey, defaultValue: true);
            set => SetBool(AllowDarkLightKey, value);
        }

        /// <summary>栏体亮度偏移（ATBC: tabbar，默认 0）</summary>
        public static double TabBar
        {
            get => AotSafeSettingsHelper.GetDouble(LocalSettings, TabBarKey, 0d);
            set => SetDouble(TabBarKey, value);
        }

        /// <summary>浅色方案最小对比度（ATBC: minContrast_light = 90，即 9.0。这里存真实比值）</summary>
        public static double MinContrastLight
        {
            get => AotSafeSettingsHelper.GetDouble(LocalSettings, MinContrastLightKey, 9d);
            set => SetDouble(MinContrastLightKey, value);
        }

        /// <summary>暗色方案最小对比度（ATBC: minContrast_dark = 45，即 4.5。这里存真实比值）</summary>
        public static double MinContrastDark
        {
            get => AotSafeSettingsHelper.GetDouble(LocalSettings, MinContrastDarkKey, 4.5d);
            set => SetDouble(MinContrastDarkKey, value);
        }

        /// <summary>浅色兜底色（ATBC: fallbackColour_light，默认 #ffffff）</summary>
        public static string FallbackLight
        {
            get => AotSafeSettingsHelper.GetString(LocalSettings, FallbackLightKey, "#ffffff");
            set => SetString(FallbackLightKey, value);
        }

        /// <summary>暗色兜底色（ATBC: fallbackColour_dark，默认 #2b2a33）</summary>
        public static string FallbackDark
        {
            get => AotSafeSettingsHelper.GetString(LocalSettings, FallbackDarkKey, "#2b2a33");
            set => SetString(FallbackDarkKey, value);
        }

        /// <summary>
        /// 自定义取色选择器（CSS 选择器，留空表示不启用）。
        /// 对应 ATBC 规则里的 QUERY_SELECTOR：命中时优先用该元素的颜色。
        /// </summary>
        public static string Query
        {
            get => AotSafeSettingsHelper.GetString(LocalSettings, QueryKey, string.Empty);
            set => SetString(QueryKey, value);
        }

        /// <summary>
        /// 规则表缓存。常驻脚本最多每 250ms 回传一次，每次回传都要匹配规则 ——
        /// 不缓存的话等于每秒解析四遍规则表 JSON。
        /// </summary>
        private static IReadOnlyList<AdaptiveColourRule>? _rulesCache;

        /// <summary>
        /// 站点规则表（ATBC: ruleList）。
        /// 首次读取时以内置默认规则（ATBC 早期版本的 default_reservedColor_cs）填充并落盘，
        /// 之后以用户存的为准 —— 内置规则可以被改、被删。
        /// </summary>
        public static IReadOnlyList<AdaptiveColourRule> Rules
        {
            get
            {
                if (_rulesCache is { } cached)
                {
                    return cached;
                }

                bool initialised = AotSafeSettingsHelper.GetBool(
                    LocalSettings, RuleListInitializedKey, defaultValue: false);

                if (!initialised)
                {
                    var defaults = new List<AdaptiveColourRule>(AdaptiveColourRuleTable.Defaults);
                    SaveRules(defaults, raiseChanged: false);
                    return defaults;
                }

                _rulesCache = AdaptiveColourRuleTable.FromJson(
                    AotSafeSettingsHelper.GetString(LocalSettings, RuleListKey, "[]"));
                return _rulesCache;
            }
        }

        /// <summary>整体替换规则表（设置页增删规则后调用）</summary>
        public static void SaveRules(IReadOnlyList<AdaptiveColourRule> rules, bool raiseChanged = true)
        {
            AotSafeSettingsHelper.SetString(LocalSettings, RuleListKey, AdaptiveColourRuleTable.ToJson(rules));
            AotSafeSettingsHelper.SetBool(LocalSettings, RuleListInitializedKey, true);
            _rulesCache = rules;

            if (raiseChanged)
            {
                RaiseChanged();
            }
        }

        /// <summary>
        /// 按当前设置生成一份求值配置。
        /// 每次取用时现造 —— 设置页改完立刻生效，不必等页面重建。
        /// </summary>
        public static AdaptiveBarColourOptions CreateOptions()
        {
            string query = Query;

            return new AdaptiveBarColourOptions
            {
                FallbackLight = ParseColour(FallbackLight, "#ffffff"),
                FallbackDark = ParseColour(FallbackDark, "#2b2a33"),
                MinContrastLightX10 = MinContrastLight * 10d,
                MinContrastDarkX10 = MinContrastDark * 10d,
                AllowDarkLight = AllowDarkLight,
                NoThemeColour = NoThemeColour,
                TabBar = TabBar,
                // Popup / TabSelected 保持 ATBC 默认值（Build() 仍会算，但目前无人消费）
                Query = string.IsNullOrWhiteSpace(query) ? null : query.Trim()
            };
        }

        private static Windows.UI.Color ParseColour(string? value, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(value) && AdaptiveColour.TryParse(value, out AdaptiveColour parsed))
            {
                return parsed.ToColor();
            }

            AdaptiveColour.TryParse(fallback, out AdaptiveColour fallbackColour);
            return fallbackColour.ToColor();
        }

        private static void SetBool(string key, bool value)
        {
            if (AotSafeSettingsHelper.GetBool(LocalSettings, key, defaultValue: !value) == value)
            {
                return;
            }

            AotSafeSettingsHelper.SetBool(LocalSettings, key, value);
            RaiseChanged();
        }

        private static void SetEnum<TEnum>(string key, TEnum value)
            where TEnum : struct, Enum
        {
            AotSafeSettingsHelper.SetEnum(LocalSettings, key, value);
            RaiseChanged();
        }

        private static void SetDouble(string key, double value)
        {
            double current = AotSafeSettingsHelper.GetDouble(LocalSettings, key, double.NaN);
            if (!double.IsNaN(current) && Math.Abs(current - value) < 0.0001)
            {
                return;
            }

            AotSafeSettingsHelper.SetDouble(LocalSettings, key, value);
            RaiseChanged();
        }

        private static void SetString(string key, string value)
        {
            string current = AotSafeSettingsHelper.GetString(LocalSettings, key, string.Empty);
            if (string.Equals(current, value ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            AotSafeSettingsHelper.SetString(LocalSettings, key, value ?? string.Empty);
            RaiseChanged();
        }

        private static void RaiseChanged()
            => Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 自适应栏色的配色方案
    /// </summary>
    public enum AdaptiveColourSchemeMode
    {
        /// <summary>跟随系统/应用主题（默认）</summary>
        System = 0,

        /// <summary>强制浅色方案</summary>
        Light = 1,

        /// <summary>强制深色方案</summary>
        Dark = 2
    }
}
