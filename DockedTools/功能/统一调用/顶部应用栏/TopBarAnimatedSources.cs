using System;
using System.Collections.Generic;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;
using Microsoft.UI.Xaml.Media;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

// ============================================================================
// 动画图标源注册表：字符串键 → WinUI 内置的 IAnimatedVisualSource2。
//
// 契约层（TopBarAnimatedIcon）只带【键名】，不抱 WinRT 对象 —— 一旦抱了，
// 每次翻译都新建一个源，按引用比较会让快照永远判定"变了"，必然陷入重渲染闭环。
// 解析放这里，并配一份进程级缓存作为值语义的担保：同一个键永远拿到同一个实例，
// 渲染层的 ReferenceEquals 比较因此能命中（见 TopBarAction.Equals）。
//
// 多个 AnimatedIcon 共享同一个源是安全的：源本身无状态，
// 它只是个造 IAnimatedVisual player 的工厂。
// ============================================================================

/// <summary>解析结果：动画源 + 静态回落的 IconSource。</summary>
internal sealed record TopBarResolvedAnimatedIcon(
    IAnimatedVisualSource2 Source,
    IconSource Fallback);

/// <summary>
/// 「确认图标」的解析结果：四种渲染走向【互斥】，用哪个字段非 null 表达走哪条路。
///   <see cref="Source"/> 非 null   → 动画图标（回落用 <see cref="Fallback"/>）；
///   <see cref="Static"/> 非 null   → 纯静态图标（Reactor 的 IconData 路径）；
///   <see cref="Hidden"/> 为 true   → 连按钮都不用画。
/// </summary>
internal sealed record TopBarResolvedAcceptIcon(
    IAnimatedVisualSource2? Source = null,
    IconSource? Fallback = null,
    IconData? Static = null,
    bool Hidden = false);

/// <summary>
/// 渲染侧的动画图标解析器。internal：只有同 Assembly 的 AppTopBar 用得上，
/// 对外契约依然只是 <see cref="TopBarAnimatedIcon"/> 那个字符串键。
/// </summary>
internal static class TopBarAnimatedSources
{
    // 静态单例 + lock：懒建议在 UI 线程完成，但 CaptureFrame 之类的后台诊断也可能碰它，
    // 一把小锁比"再说"便宜。
    private static readonly Dictionary<string, TopBarResolvedAnimatedIcon?> Cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 把「确认图标」的联合类型翻译成渲染侧要的东西。
    ///
    /// 为什么单独一个方法而不是塞进 <see cref="TryResolve"/>：那个方法的契约是
    /// "解析不出来就返回 null"（调用方自行回落），而这里四种走向（动画 / 静态 / 隐藏 / 默认）
    /// 各有默认值，返回 null 反而会让每个调用方都要写一遍兜底。
    ///
    /// null 入参 = "没说要什么" ⇒ 给默认形态（动画放大镜），保持与历史行为一致。
    /// </summary>
    public static TopBarResolvedAcceptIcon ResolveAccept(TopBarAcceptIcon? icon)
    {
        switch (icon ?? TopBarAcceptIcon.Find)
        {
            case NoAcceptIcon:
                return new TopBarResolvedAcceptIcon(Hidden: true);

            case GlyphAcceptIcon glyph when !string.IsNullOrEmpty(glyph.Glyph):
                // 静态字形走 Reactor 的 IconData：内部做码点/轮廓变体映射，
                // 手写 FontIcon 会丢掉那层映射。
                return new TopBarResolvedAcceptIcon(Static: new SymbolIconData(glyph.Glyph));

            case AnimatedAcceptIcon animated
                when !string.IsNullOrWhiteSpace(animated.SourceKey):
            {
                var fallbackGlyph = string.IsNullOrEmpty(animated.FallbackGlyph)
                    ? TopBarAcceptIcon.SearchAcceptKeys.FindFallback
                    : animated.FallbackGlyph;

                // 键不认识时 TryResolve 返回 null ⇒ 退化成静态回落不会变空白框
                var resolved = TryResolve(new TopBarAnimatedIcon(animated.SourceKey, fallbackGlyph));
                return resolved is null
                    ? new TopBarResolvedAcceptIcon(Static: new SymbolIconData(fallbackGlyph))
                    : new TopBarResolvedAcceptIcon(resolved.Source, resolved.Fallback);
            }

            default:
                // 兜底：形态认不出来（比如未来加了新派生 record 却忘了这里）
                // 就走默认放大镜 —— 宁可画出旧样子，也不要空按钮。
                return ResolveAccept(null);
        }
    }

    /// <summary>
    /// 按动画图标描述解析出可用源；键不认识时返回 null（调用方回落到静态字形）。
    /// </summary>
    public static TopBarResolvedAnimatedIcon? TryResolve(TopBarAnimatedIcon icon)
    {
        if (icon is null || string.IsNullOrWhiteSpace(icon.SourceKey))
        {
            return null;
        }

        var key = icon.SourceKey.Trim();

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var source = Create(key);
            var resolved = source is null
                ? null
                : new TopBarResolvedAnimatedIcon(source, CreateFallback(icon.FallbackGlyph));

            Cache[key] = resolved;
            return resolved;
        }
    }

    /// <summary>WinUI 自带的那一套动画视觉源，键就是它们的类名。</summary>
    private static IAnimatedVisualSource2? Create(string key) => key switch
    {
        "AnimatedAcceptVisualSource" => new AnimatedAcceptVisualSource(),
        "AnimatedBackVisualSource" => new AnimatedBackVisualSource(),
        "AnimatedChevronDownSmallVisualSource" => new AnimatedChevronDownSmallVisualSource(),
        "AnimatedChevronRightDownSmallVisualSource" => new AnimatedChevronRightDownSmallVisualSource(),
        "AnimatedChevronUpDownSmallVisualSource" => new AnimatedChevronUpDownSmallVisualSource(),
        "AnimatedFindVisualSource" => new AnimatedFindVisualSource(),
        "AnimatedGlobalNavigationButtonVisualSource" => new AnimatedGlobalNavigationButtonVisualSource(),
        "AnimatedSettingsVisualSource" => new AnimatedSettingsVisualSource(),
        _ => null,
    };

    /// <summary>
    /// 回落图标：动画不可用时（系统关闭动态效果等）显示的东西。
    /// Symbol 枚举名映射到 SymbolIconSource，其余（码点字符串）走 FontIconSource ——
    /// Symbol 名塞进 FontIconSource 会被当成字面文本画出来。
    /// </summary>
    private static IconSource CreateFallback(string glyph) =>
        Enum.TryParse<Symbol>(glyph, ignoreCase: true, out var symbol)
            ? new SymbolIconSource { Symbol = symbol }
            : new FontIconSource { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons") };
}
