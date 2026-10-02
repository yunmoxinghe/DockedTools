using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 顶栏「搜索框铬层」：走 WinUI 官方的<b>轻量样式</b>（lightweight styling），
/// 覆盖官方 <c>SubtleButtonStyle</c> 在 <c>CommonStates</c> 里使用的资源键。
/// </summary>
/// <remarks>
/// <b>为什么不能直接改画笔。</b>官方 <c>SubtleButtonStyle</c> 的悬停/按下外观不是
/// Setter，而是 <c>ObjectAnimationUsingKeyFrames</c>，动画目标写死为模板内的
/// <c>ContentPresenter.Background</c>。依赖属性优先级里 <b>动画 &gt; 本地值</b>，
/// 所以无论用 <c>.InteractionStates(background:)</c> 还是直接写 <c>Button.Background</c>，
/// 一旦真实悬停，官方动画就会整个接管，我们的颜色<b>一个像素都渲染不出来</b>。
/// <para>
/// 实测（亮色，进 PointerOver 后读模板内 ContentPresenter 的实际画笔）：
/// <code>
/// normal                              presenterBg = #00FFFFFF（跟随 TemplateBinding）
/// 本地设 #80F9F9F9                     presenterBg = #80F9F9F9（仍跟随）
/// GoToState(PointerOver)              presenterBg = #09000000  ← 被动画夺走
/// </code>
/// 那个 <c>#09000000</c> 正是官方 <c>SubtleButtonBackgroundPointerOver</c>
/// （=<c>SubtleFillColorSecondaryBrush</c>），与搜索框语义要的
/// <c>ControlFillColorSecondaryBrush</c>(#80F9F9F9) 是<b>两套不同的键</b>。
/// </para>
/// <para>
/// <b>正确做法</b>就是官方文档里的 lightweight styling：把官方键在按钮的
/// <c>Resources</c> 上覆盖掉，官方 Storyboard 照常运行、只是取到我们的颜色。
/// 好处是模板结构不动，官方模板自带的 <c>BrushTransition Duration="0:0:0.083"</c>
/// 也一并保留 —— 悬停过渡由官方免费提供，不必自己补 curve。
/// </para>
/// <para>
/// 用 <c>Theme.Ref(键)</c>（而非解析好的 <c>Brush</c> 实例）是为了让 Reactor 自己
/// 跟踪主题：写死画笔就得自己按 <c>isDark</c> 重算并缓存实例，把跟随主题的责任
/// 揽到手上。交给 <c>ThemeRef</c> 后，亮暗切换由框架重解析。
/// </para>
/// </remarks>
internal static class SearchChrome
{
    /// <summary>
    /// 把搜索框铬层注入按钮的 <c>Resources</c>。作为
    /// <c>Button(...).Resources(SearchChrome.Apply)</c> 使用。
    /// </summary>
    public static void Apply(ResourceBuilder r)
    {
        r.Set(KeyBackgroundPointerOver, Theme.Ref(TokenHoverFill))
         .Set(KeyBackgroundPressed, Theme.Ref(TokenFocusedFill))
         .Set(KeyBorderBrushPointerOver, Theme.Ref(TokenHoverStroke))
         .Set(KeyBorderBrushPressed, Theme.Ref(TokenFocusedStroke))
         // 官方 SubtleButtonForegroundPressed = TextFillColorSecondaryBrush，按下瞬间标题
         // 会整体变淡。搜索框聚焦时前景不变（TextControlForegroundFocused 同 Normal），
         // 这里保持主文本色，避免按下时文字闪一下。
         .Set(KeyForegroundPressed, Theme.Ref(TokenFocusedText));
    }

    // ── 被覆盖的官方 SubtleButtonStyle 资源键（见 generic.xaml 的 SubtleButton 段）──
    private const string KeyBackgroundPointerOver = "SubtleButtonBackgroundPointerOver";
    private const string KeyBackgroundPressed = "SubtleButtonBackgroundPressed";
    private const string KeyBorderBrushPointerOver = "SubtleButtonBorderBrushPointerOver";
    private const string KeyBorderBrushPressed = "SubtleButtonBorderBrushPressed";
    private const string KeyForegroundPressed = "SubtleButtonForegroundPressed";

    // ── 替换用的官方主题键（全部走 TextControl 语义，与 XAML 里 {ThemeResource} 同源）──
    // 整体映射：搜索框是「按下即聚焦」，所以标题按钮的 Pressed 态取的是输入框的
    // **Focused** 资源，不是 Button 的 Pressed 资源 —— 两者值不同：
    //   TextControlBackgroundFocused = ControlFillColorInputActiveBrush（聚焦底，不透明）
    //   ButtonBackgroundPressed      = ControlFillColorTertiaryBrush（按下底，半透明灰）
    // 用错会表现成「按下时比悬停还淡」，而不是「按下即进入输入态」。
    //
    // 悬停底 = TextControlBackgroundPointerOver（= ControlFillColorSecondaryBrush，
    //          亮色 #80F9F9F9，50% 白【半透明】）
    //
    // 悬停必须取 PointerOver 这一档，不能取「未聚焦」的 TextControlBackground
    // （= ControlFillColorDefaultBrush，亮色 #B3FFFFFF，70% 白）。两者只差 20% 白，
    // 但后果完全不同：
    //   · 官方 TextBox 的 Normal 底本就是 70% 白，PointerOver 是【从实底变淡】；
    //     标题按钮的 Normal 是【全透明】，70% 白一悬停就是个突兀的跳变。
    //   · 更要命的是 70% 白已经逼近聚焦态的纯白（ControlFillColorInputActive
    //     #FFFFFFFF），三态递进会塌成「透明 / 白 / 白」两级 —— 悬停和按下分不开。
    // 半透明 50% 白叠在顶栏上只提亮约 8 个灰阶，正符合悬停「轻微预告」的定位；
    // 「框感」由底边那条 elevation 描边提供，不靠底色。
    private const string TokenHoverFill = "TextControlBackgroundPointerOver";
    // 悬停边 = TextControlBorderBrushPointerOver（= TextControlElevationBorderBrush，
    //          中性灰渐变：底边强描边、向上渐隐）。
    //          注：官方把 TextControlBorderBrush 与 TextControlBorderBrushPointerOver
    //          都指向同一个 TextControlElevationBorderBrush，取哪个值都一样，
    //          这里取 PointerOver 键名只为与 TokenHoverFill 状态配套、语义自明。
    private const string TokenHoverStroke = "TextControlBorderBrushPointerOver";
    // 聚焦底 = TextControlBackgroundFocused（= ControlFillColorInputActiveBrush）
    private const string TokenFocusedFill = "TextControlBackgroundFocused";
    // 聚焦边 = TextControlBorderBrushFocused（= TextControlElevationBorderFocusedBrush，
    //          **强调色**渐变：SystemAccentColorLight2 → ControlStrokeColorDefault）
    private const string TokenFocusedStroke = "TextControlBorderBrushFocused";
    // 聚焦前景 = TextControlForegroundFocused（同 Normal，聚焦时文字不变色）
    private const string TokenFocusedText = "TextControlForegroundFocused";
}
