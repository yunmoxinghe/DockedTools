using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DockedTools.Features.Pages.WebApp.Browser.Services
{
    /// <summary>
    /// 站点规则类型（对齐 ATBC 的 Rule["type"]）
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/types.ts
    /// </summary>
    public enum AdaptiveRuleType
    {
        /// <summary>直接指定颜色（ATBC: COLOUR）—— 命中后不再取色</summary>
        Colour,

        /// <summary>强制使用 / 忽略 meta theme-color（ATBC: THEME_COLOUR，值为 true / false）</summary>
        ThemeColour,

        /// <summary>用 CSS 选择器取色（ATBC: QUERY_SELECTOR）</summary>
        QuerySelector,
    }

    /// <summary>
    /// 规则生效的配色方案（对齐 ATBC 的 Rule["scheme"]：both / light / dark）
    /// </summary>
    public enum AdaptiveRuleScheme
    {
        Both,
        Light,
        Dark,
    }

    /// <summary>
    /// 一条站点规则。对齐 ATBC 的 ColourRule / ThemeColourRule / QuerySelectorRule。
    /// 上游 Rule 还有 ADDON_ID 匹配（Firefox 扩展页专属），我们没有扩展体系，只保留 URL 匹配。
    /// </summary>
    public sealed class AdaptiveColourRule
    {
        /// <summary>
        /// 匹配用的 URL（ATBC: header）。支持四种匹配方式，见 <see cref="AdaptiveColourRuleTable.Match"/>。
        /// </summary>
        public string Host { get; set; } = string.Empty;

        public AdaptiveRuleType Type { get; set; }

        /// <summary>
        /// 规则值：COLOUR → CSS 颜色；THEME_COLOUR → true / false；QUERY_SELECTOR → CSS 选择器。
        /// </summary>
        public string Value { get; set; } = string.Empty;

        public AdaptiveRuleScheme Scheme { get; set; } = AdaptiveRuleScheme.Both;

        /// <summary>内置规则（随 ATBC 默认值一起提供），用户可覆盖但列表里会标注来源</summary>
        public bool BuiltIn { get; set; }

        public string TypeKey => Type switch
        {
            AdaptiveRuleType.Colour => "COLOUR",
            AdaptiveRuleType.ThemeColour => "THEME_COLOUR",
            _ => "QUERY_SELECTOR",
        };

        public string SchemeKey => Scheme switch
        {
            AdaptiveRuleScheme.Light => "light",
            AdaptiveRuleScheme.Dark => "dark",
            _ => "both",
        };

        /// <summary>列表里显示的摘要（类型 · 值 · 方案）</summary>
        public string Summary => Scheme == AdaptiveRuleScheme.Both
            ? TypeKey + " · " + DisplayValue
            : TypeKey + " · " + DisplayValue + " · " + SchemeKey;

        private string DisplayValue => string.IsNullOrWhiteSpace(Value) ? "—" : Value;

        public AdaptiveColourRule Clone() => new()
        {
            Host = Host,
            Type = Type,
            Value = Value,
            Scheme = Scheme,
            BuiltIn = BuiltIn,
        };
    }

    /// <summary>
    /// 站点规则表。规则求值（匹配 + 应用）对齐 ATBC：
    /// 匹配见 src/utils/preference.ts 的 getRule，应用见 src/entrypoints/background.ts 的 parseTabColourData。
    /// </summary>
    public static class AdaptiveColourRuleTable
    {
        /// <summary>正则匹配的超时，防病态表达式拖死 UI 线程</summary>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// 内置默认规则。取自 ATBC 早期版本的 default_reservedColor_cs
        /// （新版把规则交给用户配置、默认留空，只保留 Firefox 内部页的硬编码色）。
        /// IGNORE_THEME → THEME_COLOUR:false；CLASS_xxx → QUERY_SELECTOR:.xxx；#hex → COLOUR。
        /// </summary>
        public static IReadOnlyList<AdaptiveColourRule> Defaults { get; } = new List<AdaptiveColourRule>
        {
            New("developer.mozilla.org", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
            New("github.com", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
            New("mail.google.com", AdaptiveRuleType.QuerySelector, ".wl", builtIn: true),
            New("open.spotify.com", AdaptiveRuleType.Colour, "#000000", builtIn: true),
            New("www.bbc.com", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
            New("www.instagram.com", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
            New("www.spiegel.de", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
            New("www.youtube.com", AdaptiveRuleType.ThemeColour, "false", builtIn: true),
        };

        private static AdaptiveColourRule New(string host, AdaptiveRuleType type, string value, bool builtIn)
            => new()
            {
                Host = host,
                Type = type,
                Value = value,
                Scheme = AdaptiveRuleScheme.Both,
                BuiltIn = builtIn,
            };

        /// <summary>
        /// 找出当前 URL 命中的规则。对齐上游 getRule：**遍历全部，最后一个命中的胜出**，
        /// 所以后面（用户后加）的规则能覆盖前面的。
        /// </summary>
        public static AdaptiveColourRule? Match(
            IReadOnlyList<AdaptiveColourRule> rules,
            string? url,
            AdaptiveScheme scheme)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string cleanUrl = TrimTrailingSlash(url);
            string schemeKey = scheme == AdaptiveScheme.Dark ? "dark" : "light";
            AdaptiveColourRule? match = null;

            foreach (AdaptiveColourRule rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Host))
                {
                    continue;
                }

                if (rule.Scheme != AdaptiveRuleScheme.Both &&
                    !string.Equals(rule.SchemeKey, schemeKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string header = TrimTrailingSlash(rule.Host.Trim());

                if (string.Equals(cleanUrl, header, StringComparison.OrdinalIgnoreCase) ||
                    TestRegex(cleanUrl, header) ||
                    TestWildcard(cleanUrl, header) ||
                    TestHostname(cleanUrl, header))
                {
                    match = rule;
                }
            }

            return match;
        }

        /// <summary>
        /// ATBC: <c>url.replace(/\/$/, "")</c> —— 只去掉末尾【一个】斜杠。
        /// 用 TrimEnd 会把 "https://a.com//" 连根拔成 "https://a.com"，跟上游对不上。
        /// </summary>
        private static string TrimTrailingSlash(string value)
            => value.Length > 0 && value[value.Length - 1] == '/'
                ? value.Substring(0, value.Length - 1)
                : value;

        /// <summary>ATBC: #testRegex —— 规则串当正则用（^...$）</summary>
        private static bool TestRegex(string url, string test)
        {
            try
            {
                return Regex.IsMatch(url, "^" + test + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// ATBC: #testWildcard —— * 匹配一层（不含 / . :），** 跨层，? 单字符；协议前缀可选。
        /// </summary>
        private static bool TestWildcard(string url, string test)
        {
            if (!test.Contains('*') && !test.Contains('?'))
            {
                return false;
            }

            try
            {
                string pattern = Regex.Replace(test, @"[.+^${}()|[\]\\]", "\\$&")
                    .Replace("**", "\0")
                    .Replace("*", "[^/.:]*")
                    .Replace("?", ".")
                    .Replace("\0", ".*");

                // 不以 protocol:// 开头时，允许省略协议（"github.com/*" 也能匹配 https://github.com/...）
                pattern = Regex.Replace(pattern, @"^((?![a-z]+://).)", "(?:[a-z]+://)?$1",
                    RegexOptions.IgnoreCase);

                return Regex.IsMatch(url, "^" + pattern + "/?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// ATBC: #testHostname —— 主机（允许子域）+ 可选路径前缀。
        /// </summary>
        private static bool TestHostname(string url, string test)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    return false;
                }

                string hostPart = test.Split('/')[0];
                string host = uri.Host;

                if (!string.Equals(host, hostPart, StringComparison.OrdinalIgnoreCase) &&
                    !host.EndsWith("." + hostPart, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string urlPath = hostPart + uri.AbsolutePath;
                return string.Equals(urlPath, test, StringComparison.OrdinalIgnoreCase) ||
                    urlPath.StartsWith(test + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------- 存取（PublishAot：只用手写的 Utf8JsonWriter / JsonDocument，不走反射序列化） ----------

        public static string ToJson(IEnumerable<AdaptiveColourRule> rules)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (AdaptiveColourRule rule in rules)
                {
                    writer.WriteStartObject();
                    writer.WriteString("host", rule.Host);
                    writer.WriteString("type", rule.TypeKey);
                    writer.WriteString("value", rule.Value);
                    writer.WriteString("scheme", rule.SchemeKey);
                    writer.WriteBoolean("builtIn", rule.BuiltIn);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static List<AdaptiveColourRule> FromJson(string? json)
        {
            var rules = new List<AdaptiveColourRule>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return rules;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return rules;
                }

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    rules.Add(new AdaptiveColourRule
                    {
                        Host = ReadString(item, "host") ?? string.Empty,
                        Type = ParseType(ReadString(item, "type")),
                        Value = ReadString(item, "value") ?? string.Empty,
                        Scheme = ParseScheme(ReadString(item, "scheme")),
                        BuiltIn = item.TryGetProperty("builtIn", out JsonElement builtIn) &&
                            builtIn.ValueKind == JsonValueKind.True,
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AdaptiveColourRuleTable] 规则解析失败: {ex.Message}");
            }

            return rules;
        }

        private static string? ReadString(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static AdaptiveRuleType ParseType(string? key)
            => key switch
            {
                "COLOUR" => AdaptiveRuleType.Colour,
                "THEME_COLOUR" => AdaptiveRuleType.ThemeColour,
                "QUERY_SELECTOR" => AdaptiveRuleType.QuerySelector,
                _ => AdaptiveRuleType.ThemeColour,
            };

        private static AdaptiveRuleScheme ParseScheme(string? key)
            => key switch
            {
                "light" => AdaptiveRuleScheme.Light,
                "dark" => AdaptiveRuleScheme.Dark,
                _ => AdaptiveRuleScheme.Both,
            };

        /// <summary>把 THEME_COLOUR 规则的值解析成布尔（不认识的一律当 false，即忽略 theme-color）</summary>
        public static bool ParseThemeColourValue(string? value)
            => bool.TryParse(value, out bool parsed) && parsed;
    }
}
