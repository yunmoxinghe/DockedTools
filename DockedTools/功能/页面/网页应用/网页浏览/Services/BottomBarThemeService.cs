using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 底部操作栏主题服务
/// 一行调用实现：主题模式（亮/暗/跟随系统）+ 背景颜色（自定义/跟随系统）
///
/// <para><b>为什么按宿主实例分账（而不是一个静态单例）。</b>
/// 网页浏览页面走 <c>NavigationCacheMode="Disabled"</c> + Frame 外的 LRU 缓存，
/// <b>多个 WebBrowserPage 实例会同时存活</b>（增量访问 www / 邮件 / 设置，三个页面都在内存里）。
/// 早先这里是「一个静态 <c>_bottomBarHost</c>，后注册的覆盖先注册的」，于是：
/// ① 从缓存切回旧 page 时它不会重新构造 ⇒ 注册权还留在最后一次构造的那个 page 上，
///    它的取色回传被 <c>IsBottomBarHostOwner()</c> 挡住，底栏永久停在默认色；
/// ② 更糟的那个方向 —— 宿主 A 的主人还没卸任，但 UI 上展示的其实是 B 的 Border，
///    A 的回传通过校验后写的是 A 自己那份引用，颜色就落在错误的页面上。
///
/// 现在每个 Border 一份独立状态（主题、自定义画刷、颜色动画），谁注册谁拥有自己的那份，
/// 一个页面写自己的底栏天然不可能碰到别人的。旧 API（不传宿主）退化为「写最近活跃的那一份」，
/// 给还在用一行调用的代码留了兼容。</para>
/// </summary>
public static class BottomBarThemeService
{
    /// <summary>
    /// 一个底栏宿主的全部可变状态。以前躺在静态字段上，现在每位宿主一份。
    /// </summary>
    private sealed class BottomBarState
    {
        /// <summary>XAML 上原本挂的 ThemeResource 画刷资源键，复位时按它取回默认值</summary>
        public string BackgroundResourceKey = DefaultBackgroundResourceKey;

        /// <summary>自定义背景画刷；null 表示当前跟随系统</summary>
        public SolidColorBrush? CustomBrush;

        /// <summary>最近一次写入的实际颜色（自定义态下才有意义）</summary>
        public Color? CustomColor;
    }

    /// <summary>BottomBarHost 在 XAML 上挂的默认 ThemeResource 键</summary>
    private const string DefaultBackgroundResourceKey = "ApplicationPageBackgroundThemeBrush";

    /// <summary>自适应取色默认的背景过渡时长（毫秒）</summary>
    public const int DefaultColourTransitionMs = 300;

    /// <summary>
    /// 宿主 → 状态。
    ///
    /// <para>⚠️ 必须是 <see cref="ConditionalWeakTable{TKey,TValue}"/>，不能用 Dictionary：
    /// 「页面 Unloaded 一定会走到」是个想当然 —— <see cref="Unregister(Border?)"/> 只在
    /// Unloaded 里被调一次，而 <c>DisposeWebView</c>（LRU 淘汰 / 删除当前网页应用）会把
    /// Unloaded 处理器先摘掉再关内核，那条路根本不走 Unloaded。</para>
    ///
    /// <para>于是 Dictionary 有一条必然泄漏的路径：
    /// <c>_hosts → Border → SizeChanged lambda → WebBrowserPage → WebView2</c>
    /// （<c>BottomBarHost.SizeChanged += (s,e) => UpdateBottomBarLayout()</c> 捕获了 this），
    /// 整张页面连着它的浏览器进程一起永远驻留。换成弱键表之后，Border 被回收条目就自动消失，
    /// 谁忘了显式注销都不会漏。</para>
    ///
    /// <para>BottomBarState 里刻意不放 Border 引用 —— 弱键表的 value 若反过来强引用 key，
    /// 条目就永远不会被回收，等于白换。</para>
    /// </summary>
    private static readonly ConditionalWeakTable<Border, BottomBarState> _hosts = new();

    /// <summary>
    /// 最近一次注册/认领的宿主。只为兼容「不传宿主」的旧调用而保留 ——
    /// 新代码请一律显式传自己的 Border。
    /// </summary>
    private static Border? _activeHost;

    /// <summary>
    /// 兼容旧语义：当前注册的宿主。多宿主化之后它等于「最近活跃的那一位」，
    /// 不再代表「唯一的一位」—— 判断能不能写请改用 <see cref="IsRegistered(Border)"/>。
    /// </summary>
    public static Border? RegisteredHost => _activeHost;

    /// <summary>
    /// 是否已经注册了至少一个底栏宿主。
    /// 弱键表没有 Count，这里只能枚举判空 —— 它只用在诊断路径上，不值得为它另记一个计数
    /// （多记一个计数就要和弱键表的自动回收保持同步，反而更容易错）。
    /// </summary>
    public static bool IsRegistered => _hosts.Any();

    /// <summary>某个具体的 Border 是否已经注册为底栏宿主（幂等：空/null 一律 false）</summary>
    public static bool IsHostRegistered(Border? host) => host is not null && _hosts.TryGetValue(host, out _);

    /// <summary>
    /// 注册底部栏容器实例（幂等，重复调用只更新资源键与「最近活跃」身份）。
    /// 由网页浏览页面在初始化时调用。
    /// </summary>
    /// <param name="bottomBarHost">底栏容器</param>
    /// <param name="defaultBackgroundResourceKey">
    /// XAML 上 Background 用的 ThemeResource 资源键。复位时必须按它把默认画刷取回来，
    /// 不能靠 ClearValue —— ThemeResource 同样是本地值，清掉就直接变透明回不来了。
    /// </param>
    public static void Register(
        Border bottomBarHost,
        string defaultBackgroundResourceKey = DefaultBackgroundResourceKey)
    {
        if (bottomBarHost is null)
        {
            return;
        }

        // GetValue 是原子的 get-or-add。用 TryGetValue + Add 的话两步之间不是原子的，
        // 而现在 ClaimForeground 也会补注册（缓存页复用路径），调用点变多了，
        // 万一将来有一条落到后台线程上就会撞出重复 Add。
        BottomBarState state = _hosts.GetValue(bottomBarHost, static _ => new BottomBarState());

        state.BackgroundResourceKey = defaultBackgroundResourceKey;
        _activeHost = bottomBarHost;

        System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 底部栏容器已注册");
    }

    /// <summary>
    /// 认领「最近活跃宿主」身份。
    ///
    /// 页面被 LRU 缓存后从缓存切回来不会重新构造 ⇒ <see cref="Register"/> 不会被再调用一次，
    /// 而没有这一步，后续不传宿主的旧调用就会写到别的页面上。
    /// 页面回到前台时（OnNavigatedTo）调一次即可，幂等。
    /// </summary>
    public static void ClaimForeground(Border? bottomBarHost)
    {
        if (bottomBarHost is null)
        {
            return;
        }

        // ⭐ 没注册就顺手补注册 —— 这是缓存页复用路径的救命绳。
        // 页面从 LRU 缓存被切回来时【不会重新构造】，所以构造函数里那次 Register 不会再跑；
        // 而它上次离开前台时 Unloaded 已经调过 Unregister 把它摘掉了。
        // 只认领不补注册的话，这个宿主的 IsHostRegistered 恒为 false，
        // 于是 IsBottomBarHostOwner() 恒 false，底栏自适应色【永久写不进去】——
        // 表现不是串色，是「这张页面完全没有取色」，而且没有任何报错。
        if (!_hosts.TryGetValue(bottomBarHost, out _))
        {
            Register(bottomBarHost);
            return;
        }

        _activeHost = bottomBarHost;
    }

    /// <summary>
    /// 注销底部栏容器（页面卸载时清理）
    /// </summary>
    /// <param name="bottomBarHost">
    /// 建议显式传入自己的宿主。不传时注销「最近活跃」的那一位。
    /// 多宿主化之后不再有「非宿主页误注销」的顾虑：没人能注销不是自己的东西。
    /// </param>
    public static void Unregister(Border? bottomBarHost = null)
    {
        Border? target = bottomBarHost ?? _activeHost;

        if (target is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 无可注销的容器");
            return;
        }

        if (!_hosts.TryGetValue(target, out BottomBarState? state))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 容器未注册，忽略注销");
            return;
        }

        // ⭐ 顺序要点：**先复位，再断引用**。
        // 和以前一样 —— 一旦把自己从表里摘掉，复位就成了空操作，
        // 而这个 Border 可能马上被下一张网页复用（页面缓存 / 宿主原地重注册），
        // 上一张网页的 RequestedTheme 与自适应背景色会原封不动地残留下来。
        bool restored = RestoreToDefault(target, state);

        _hosts.Remove(target);

        if (ReferenceEquals(_activeHost, target))
        {
            _activeHost = null;
        }

        System.Diagnostics.Debug.WriteLine(
            restored
                ? "[BottomBarThemeService] 底部栏容器已注销（已复位为系统默认）"
                : "[BottomBarThemeService] 底部栏容器已注销");
    }

    #region 一行调用 API

    /// <summary>
    /// 【推荐】【一行调用】给指定宿主设置主题与背景颜色。
    /// </summary>
    /// <param name="bottomBarHost">目标底栏容器（页面自己的 BottomBarHost）</param>
    /// <param name="theme">主题模式：Light（亮）、Dark（暗）、Default（跟随系统）</param>
    /// <param name="backgroundColor">背景颜色：传入颜色值，或 null 表示跟随系统</param>
    /// <param name="transitionMs">
    /// 颜色过渡时长（毫秒）。&lt;= 0 表示立即赋值。
    /// 自适应取色在页面里 250ms 一跳，给 300ms 左右的过渡才不会硬切；
    /// 复位 / 首次布局这类不需要过渡的场合传 0。
    /// </param>
    /// <example>
    /// // 亮色主题 + 跟随系统背景
    /// BottomBarThemeService.SetBottomBar(BottomBarHost, ElementTheme.Light, null);
    ///
    /// // 暗色主题 + 网页主题色（带 300ms 淡入）
    /// BottomBarThemeService.SetBottomBar(BottomBarHost, ElementTheme.Dark, frame, 300);
    /// </example>
    public static void SetBottomBar(
        Border? bottomBarHost,
        ElementTheme theme,
        Color? backgroundColor = null,
        int transitionMs = 0)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out BottomBarState? state))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return;
        }

        // 设置主题模式
        host!.RequestedTheme = theme;

        if (backgroundColor is { } colour)
        {
            SetCustomBackground(host, state!, colour, transitionMs);
            return;
        }

        ResetBackground(host, state!);
    }

    /// <summary>
    /// 【兼容】【一行调用】写「最近活跃」的那份底栏。新代码请用带宿主的重载。
    /// </summary>
    public static void SetBottomBar(ElementTheme theme, Color? backgroundColor = null, int transitionMs = 0)
        => SetBottomBar(_activeHost, theme, backgroundColor, transitionMs);

    #endregion

    #region 单独控制 API（兼容旧代码）

    /// <summary>
    /// 设置指定宿主的局部主题（仅控制主题，不改变背景颜色）
    /// </summary>
    public static void SetTheme(Border? bottomBarHost, ElementTheme theme)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out _))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册，无法设置主题");
            return;
        }

        host!.RequestedTheme = theme;
    }

    /// <summary>写「最近活跃」宿主的局部主题</summary>
    public static void SetTheme(ElementTheme theme) => SetTheme(_activeHost, theme);

    /// <summary>
    /// 设置背景颜色（仅控制背景，不改变主题）
    /// </summary>
    /// <param name="bottomBarHost">目标宿主</param>
    /// <param name="color">背景颜色，null 表示恢复跟随系统</param>
    /// <param name="transitionMs">颜色过渡时长；&lt;= 0 立即赋值</param>
    public static void SetBackgroundColor(Border? bottomBarHost, Color? color, int transitionMs = 0)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out BottomBarState? state))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return;
        }

        if (color is { } colour)
        {
            SetCustomBackground(host!, state!, colour, transitionMs);
            return;
        }

        ResetBackground(host!, state!);
    }

    /// <summary>写「最近活跃」宿主的背景色</summary>
    public static void SetBackgroundColor(Color? color, int transitionMs = 0)
        => SetBackgroundColor(_activeHost, color, transitionMs);

    /// <summary>
    /// 切换指定宿主的局部主题（Light ↔ Dark）
    /// </summary>
    public static void ToggleTheme(Border? bottomBarHost)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out _))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册，无法切换主题");
            return;
        }

        ElementTheme current = host!.ActualTheme;
        ElementTheme next = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

        SetTheme(host, next);
        System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] 🔄 主题已切换: {current} → {next}");
    }

    /// <summary>切换「最近活跃」宿主的局部主题</summary>
    public static void ToggleTheme() => ToggleTheme(_activeHost);

    #endregion

    #region 内部实现

    private static bool TryGetState(Border? bottomBarHost, out Border? host, out BottomBarState? state)
    {
        Border? target = bottomBarHost ?? _activeHost;

        if (target is not null && _hosts.TryGetValue(target, out BottomBarState? found))
        {
            host = target;
            state = found;
            return true;
        }

        host = null;
        state = null;
        return false;
    }

    /// <summary>
    /// 铺自定义色，并且始终复用同一支画刷：
    /// 一是没必要每次都多分配一个 GC 对象，二是<b>常驻画刷才能做颜色过渡</b> ——
    /// 每次 new 一支的话，<see cref="BrushColourTransition"/> 里「停掉上一次没跑完的动画」就失去意义了，
    /// 两支画刷各滚各自的，反而会看到两次收拾不干净的中间色。
    /// </summary>
    private static void SetCustomBackground(Border host, BottomBarState state, Color colour, int transitionMs)
    {
        if (state.CustomBrush is null)
        {
            state.CustomBrush = new SolidColorBrush(colour);
            host.Background = state.CustomBrush;
        }
        else if (transitionMs > 0)
        {
            BrushColourTransition.AnimateTo(state.CustomBrush, colour, transitionMs);
        }
        else
        {
            BrushColourTransition.SnapTo(state.CustomBrush, colour);
        }

        state.CustomColor = colour;

        System.Diagnostics.Debug.WriteLine(
            $"[BottomBarThemeService] ✅ 底部栏背景: 自定义({colour}), 过渡={transitionMs}ms");
    }

    private static void ResetBackground(Border host, BottomBarState state)
    {
        // 还有动画在跑就停掉：动画依赖属性那条链是按画刷实例走的，
        // 换Background 之后它仍在后台改一支没人看的 brush，虽然无害但是白烧 CPU，也污染下一次 SnapTo。
        if (state.CustomBrush is { } brush)
        {
            BrushColourTransition.Stop(brush);
        }

        state.CustomBrush = null;
        state.CustomColor = null;

        ApplyDefaultBackground(host, state);
    }

    /// <summary>
    /// 把指定宿主还原到「跟随系统」默认态：Default 主题 + XAML 上挂的 ThemeResource 背景画刷。
    /// 供 <see cref="Unregister"/> 在断开引用之前调用，避免复位变成空操作。
    /// </summary>
    /// <returns>是否真的写过东西。false 表示本来就已经是默认态，一次都不必写</returns>
    private static bool RestoreToDefault(Border host, BottomBarState state)
    {
        bool changed = false;

        // RequestedTheme 是本服务里最贵的一步：改它会让整棵子树的 ThemeResource 重新求值，
        // 按钮样式与 VisualState 全都跟着重刷。多比一次远比无脑重写便宜。
        if (host.RequestedTheme != ElementTheme.Default)
        {
            host.RequestedTheme = ElementTheme.Default;
            changed = true;
        }

        // 只有铺过自定义画刷才需要把背景换回 ThemeResource；本来就在默认态就别动它。
        if (state.CustomBrush is { } brush)
        {
            BrushColourTransition.Stop(brush);
            state.CustomBrush = null;
            state.CustomColor = null;
            ApplyDefaultBackground(host, state);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 把底栏背景还原成 XAML 上挂的 ThemeResource 画刷。
    ///
    /// ⚠️ 这里**不能**用 ClearValue 代替：
    /// <c>Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"</c> 在 XAML 里
    /// 同样是以本地值形式落到 Background 上的，ClearValue 会把它连同 theme-resource 引用
    /// 一起擦掉，结果是「恢复跟随系统」实际变成了「全透明」，且之后切主题再也不会跟着变。
    /// 必须显式按资源键取一次当前主题下的画刷重新赋值，才等价于复位。
    /// 顶栏那套逻辑（ResetAdaptiveBarColour）早就这么处理了，这里补齐同一条规则。
    /// </summary>
    private static void ApplyDefaultBackground(Border host, BottomBarState state)
    {
        if (!string.IsNullOrEmpty(state.BackgroundResourceKey)
            && Application.Current.Resources.TryGetValue(state.BackgroundResourceKey, out object? resource)
            && resource is Brush defaultBrush)
        {
            host.Background = defaultBrush;
            return;
        }

        // 资源取不到才退化到 ClearValue，总好过留着上一张网页的自适应色
        host.ClearValue(Border.BackgroundProperty);
    }

    #endregion

    #region 查询 API

    /// <summary>获取指定宿主当前实际生效的主题</summary>
    public static ElementTheme GetActualTheme(Border? bottomBarHost)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out _))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return ElementTheme.Default;
        }

        return host!.ActualTheme;
    }

    /// <summary>获取「最近活跃」宿主当前实际生效的主题</summary>
    public static ElementTheme GetActualTheme() => GetActualTheme(_activeHost);

    /// <summary>获取指定宿主请求的主题</summary>
    public static ElementTheme GetRequestedTheme(Border? bottomBarHost)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out _))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return ElementTheme.Default;
        }

        return host!.RequestedTheme;
    }

    /// <summary>获取「最近活跃」宿主请求的主题</summary>
    public static ElementTheme GetRequestedTheme() => GetRequestedTheme(_activeHost);

    /// <summary>获取指定宿主当前背景颜色（自定义态；跟随系统时返回 null）</summary>
    public static Color? GetBackgroundColor(Border? bottomBarHost)
        => TryGetState(bottomBarHost, out _, out BottomBarState? state) ? state!.CustomColor : null;

    /// <summary>获取「最近活跃」宿主的当前背景颜色</summary>
    public static Color? GetBackgroundColor() => GetBackgroundColor(_activeHost);

    /// <summary>
    /// 重置指定底栏为默认状态（跟随系统主题 + 跟随系统背景）
    /// </summary>
    public static void Reset(Border? bottomBarHost)
    {
        if (!TryGetState(bottomBarHost, out Border? host, out BottomBarState? state))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return;
        }

        // 走 RestoreToDefault（带比脏）而不是无条件 SetBottomBar，日志也如实反映有没有真改动，
        // 不再自欺欺人地无条件打印「已重置」
        bool changed = RestoreToDefault(host!, state!);
        System.Diagnostics.Debug.WriteLine(
            changed
                ? "[BottomBarThemeService] 🔄 底部栏已重置为完全跟随系统"
                : "[BottomBarThemeService] 底部栏本就处于跟随系统状态，无需重置");
    }

    /// <summary>重置「最近活跃」的那份底栏</summary>
    public static void Reset() => Reset(_activeHost);

    #endregion
}
