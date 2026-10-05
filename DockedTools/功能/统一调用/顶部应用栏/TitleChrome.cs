using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ElementRef = Microsoft.UI.Reactor.Input.ElementRef;
using static Microsoft.UI.Reactor.Factories;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

// ============================================================================
// 顶栏居中那个「长得像输入框的按钮」——它的【外观】全在这一份文件里：
//   ① 三态画刷（走官方 SubtleButton 的轻量样式覆盖，见下面的资源键一节）；
//   ② 边框厚度（官方没给厚度留资源键，只能走本地值 + 自己的状态机）；
//   ③ 按下 / 悬停的锁定与自愈（进入与退出搜索时要把铬层钉住不许塌）。
//
// 为什么从 AppTopBar 里搬出来：这三件事互相咬得很紧（厚度跟着按下标志走、
// 锁定态又要反过来改写按下标志），而 AppTopBar 关心的是"搜索形变怎么编排"。
// 两边混在一个 3000 行的 Render 里，改一个悬停色要在形变编排里翻半天。
// 搬出来之后 AppTopBar 只剩【什么时候调用】(EnterSearch / ExitSearch / 动画完成)，
// 本类负责【调用之后怎么落到像素上】。
//
// 它不是 Component： chromium 的状态不能走 UseState（按下若触发重渲染会打断
// ConnectedAnimation，见 Pressed 那一节注释），所以它只是一个由 AppTopBar
// 用 UseMemo 长期持有的普通对象 —— 生命周期与 AppTopBar 实例一致。
// ============================================================================
internal sealed class TitleChrome
{
    // ── 边框厚度：与官方 TextControlBorderThemeThickness 对齐 ──────────────
    //   未聚焦（含悬停）= 1；聚焦（= 我们的 Pressed）底边加粗到 2。
    //   「底边加粗到 2px」是**聚焦独有**的特征，悬停仍是 1px。之前写成恒 1,1,1,2，
    //   悬停就顶着一条聚焦态的粗底边 —— 那是「悬停底边太粗」的来源。
    //   SubtleButtonStyle 的 CommonStates 只换画刷、不改厚度，官方也没给厚度留资源键
    //   （ResourceBuilder.Set 无 Thickness 重载），所以只能在指针事件里手动切。
    public static Thickness BorderNormal { get; } = new(1);
    public static Thickness BorderFocused { get; } = new(1, 1, 1, 2);

    // REACTOR_POOL_001 会报「BorderThickness 在池回收时被重置，.Set 写的值会丢」——
    // 这是真的，但在这里无害，故就地抑制（不能用它建议的 .BorderThickness(常量)，原因见 Build）：
    //   ① 重置后的默认值就是未按下态（1px），与未按下一致，不会张冠李戴；
    //   ② 元素回到树上时 .Set 会再跑一次，按当前标志重新写回；
    //   ③ 真正按下时指针事件里也会即时改一次，不依赖渲染时机。
    // 即：厚度的唯一真相源是 Pressed 标志，池重置只是清了缓存，不是把状态改错。
    private readonly bool[] _pressed = new bool[1];

    // 按钮的原生引用（由 Build 挂上，供立即改厚度 / 复位视觉态用）
    private readonly ElementRef _buttonRef = new();

    // 已挂过指针订阅的按钮实例。AddHandler **不去重**（实测：同实例同委托每次调用都
    // 会再加一条，日志里一次按下能触发 25→60→72 次，随渲染次数单调增长），而 .Set()
    // 每次渲染都执行 —— 所以必须自己按元素实例做一次门控，否则订阅无限累积。
    // 元素被池回收后若换了个实例回来，引用不等 ⇒ 自动重新挂上（自愈）。
    private Button? _hooked;

    // 上游的两个「不许复位」闸门，每次 Build 灌最新进来（见 Build 参数处注释）
    private Func<bool> _enterPending = static () => false;
    private Func<bool> _exitHolding = static () => false;

    // 委托实例必须缓存：AddHandler 不按委托去重，每次 Build 传新 lambda 只会让订阅
    // 越积越多。顺序必须与下面 AddHandler 那几行一致：
    //   [0]=Pressed [1]=Released [2]=Exited [3]=Canceled
    private readonly PointerEventHandler[] _handlers;

    public TitleChrome()
    {
        _handlers = new[]
        {
            new PointerEventHandler((_, _) => SetPressed(true)),
            new PointerEventHandler((_, _) => SetPressed(false)),
            new PointerEventHandler((_, _) => SetPressed(false)),
            new PointerEventHandler((_, _) => SetPressed(false)),
        };
    }

    /// <summary>当前是否处于按下（= 搜索框聚焦）外观。</summary>
    public bool Pressed => _pressed[0];

    /// <summary>
    /// 构造标题按钮本身（内容由宿主给，这里只包「输入框的那层壳」）。
    /// </summary>
    /// <param name="content">按钮内容（图标 + 文本的整体，换字动画作用在它上面）。</param>
    /// <param name="onClick">左键点击（由宿主决定是不是进入搜索）。</param>
    /// <param name="enterPending">
    /// 是否正处于「进入搜索的前导期」的真值源（读的是宿主 ref 的当前值）。
    /// 每次 Build 都重灌一遍：本对象是跨渲染长期存活的，而 ref 是稳定的 ——
    /// 不这么做的话闭包抓到的会是首次渲染那一刻的旧值。
    /// </param>
    /// <param name="exitHolding">同上，是否正处于「退出形变的播放期」。</param>
    public Element Build(
        Element content,
        Action onClick,
        Func<bool> enterPending,
        Func<bool> exitHolding)
    {
        _enterPending = enterPending;
        _exitHolding = exitHolding;

#pragma warning disable REACTOR_POOL_001
        return Button(content, onClick)
            .Ref(_buttonRef)
            .AutomationName("TopBarTitle")
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            // SubtleButtonStyle = 官方「无铬」按钮：透明底、无边框，主题切换自动跟随。
            // 它同时提供官方的悬停/按下 CommonStates，作为画笔取不到时的兜底外观。
            .SubtleButton()
            // 原样式 Setter 里的形状值，这里显式补回（内容对齐默认即 Center，不用设）
            .Padding(12, 0, 12, 0)
            .CornerRadius(4)
            // 边框厚度【跟着状态走】，不能写死：
            //
            // ⚠️ 这里【不能】写 .BorderThickness(常量)：那是 render 期修饰器，每次
            // 重渲染都会把值写回常量，把事件里刚设的 2px 冲掉 —— 按下期间只要发生
            // 任何一次渲染（例如进入搜索态），厚度就悄悄退回 1px。
            // 所以改成「render 期按当前标志统一写」：事件只负责改标志 + 即时改一次，
            // 之后无论渲染多少次，厚度都由同一个标志推出来，天然自愈。
            .Set(b =>
            {
                b.BorderThickness = _pressed[0] ? BorderFocused : BorderNormal;

                // 订阅按【元素实例】只做一次
                if (!ReferenceEquals(_hooked, b))
                {
                    _hooked = b;
                    // handledEventsToo: true —— ButtonBase 在 OnPointerPressed 里把事件
                    // 标成 Handled，普通 += 订阅（Reactor 的 .OnPointerPressed 就是这种）
                    // 根本收不到按下。
                    b.AddHandler(UIElement.PointerPressedEvent, _handlers[0], true);
                    b.AddHandler(UIElement.PointerReleasedEvent, _handlers[1], true);
                    b.AddHandler(UIElement.PointerExitedEvent, _handlers[2], true);
                    b.AddHandler(UIElement.PointerCanceledEvent, _handlers[3], true);
                }
            })
            // 三态换刷走【轻量样式覆盖】：官方 CommonStates 用 Storyboard 动画打在
            // ContentPresenter.Background 上，而动画优先级高于 TemplateBinding —— 所以任何
            // 去写 Button.Background 的做法（例如 .InteractionStates(background:)）在悬停/按下
            // 期间都会被官方动画整个遮蔽。覆盖资源键才是正路。
            .Resources(ApplySearchBoxTokens);
#pragma warning restore REACTOR_POOL_001
    }

    /// <summary>
    /// 强制回到 Normal（透明）：进入/退出搜索会切 IsHitTestVisible，命中测试切换后
    /// WinUI 不补发 PointerExited，CommonStates 会卡在 PointerOver。
    /// 退出搜索走这个：形变终点是【标题】，必须以 Normal（透明铬层）定格。
    /// </summary>
    public void Reset()
    {
        if (_buttonRef.Current is Control titleCtl)
        {
            VisualStateManager.GoToState(titleCtl, "Normal", false);
        }
        SetPressedCore(false);
    }

    /// <summary>
    /// 退出形变的【播放期间】反向操作：把铬层锁在【悬停态】（PointerOver）。
    ///
    /// 为什么是悬停而不是 Normal：退出是「搜索框 → 标题」的形变。源端（搜索框）
    /// 无论静置还是聚焦都有实底，而标题的 Normal 是【完全透明】的 —— 若终点以 Normal
    /// 定格，形变最后一帧等于把实底抹掉，观感是"闪一下消失"。悬停态有底，与源端一致，
    /// 形变全程只剩尺寸/位置在变；退出动作本身也暗示指针正落在顶栏（点返回键 / 提交），
    /// 悬停正是此刻应有的态。
    ///
    /// 为什么不是常态：动画播完必须退回 Normal（宿主那边形变 Completed 会调用），
    /// 否则铬层会被永久钉在悬停态 —— 之后指针移开也不会复原。
    ///
    /// ⚠️ 不能只在这里压一次就完事：ButtonBase 的 UpdateVisualState 随时可能把状态
    /// 打回去（与进入侧同一个坑），所以 <see cref="SetPressedCore"/> 里还有一道
    /// 悬停闸，且宿主在 TryStart 成功的同帧会再压一次（那是最后一个能改状态的点）。
    /// </summary>
    public void HoldHover()
    {
        if (_buttonRef.Current is Control titleCtl)
        {
            VisualStateManager.GoToState(titleCtl, "PointerOver", false);
        }
        // 直接写，不走入口方法：后者在悬停闸为真时会回头调本方法
        SetPressedCore(false, writeThickness: true);
    }

    /// <summary>
    /// 进入搜索的前导期反向操作：把铬层【锁在按下态】（= 搜索框聚焦外观）。
    /// 进入动画是从「标题」形变到「搜索框」，而搜索框一进来就是聚焦态（实底 + 强调色
    /// 边框 + 底边 2px）。若前导期退回 Normal（透明），就会看到
    ///   按下(实底+强调边) → 松开瞬间透明 → 150ms 后形变 → 聚焦(实底+强调边)
    /// 中间那一拍是凭空闪一下。源端保持聚焦外观，两端像素一致，形变只剩尺寸/位置变化。
    /// </summary>
    public void HoldPressed()
    {
        if (_buttonRef.Current is Control titleCtl)
        {
            VisualStateManager.GoToState(titleCtl, "Pressed", false);
        }
        SetPressedCore(true, writeThickness: true);
    }

    /// <summary>
    /// 厚度只能走本地值（见 <see cref="BorderNormal"/> 处的注释）。
    /// 两步走：先记标志（供 render 期 .Set 自愈），再即时改一次（不等下次渲染）。
    /// 用 ref.Current 现场解析，不要抓快照 —— 抓到的是上一帧的元素引用，闭包一旦
    /// 把它捕获下来就永远过期。
    /// </summary>
    public void SetPressed(bool pressed)
    {
        // 进入搜索的前导期：铬层锁定聚焦外观，任何「松开/移出」都不许把它复位。
        //
        // 时序坑（已实测）：Click 是 ButtonBase 在 OnPointerReleased 的【覆写】里抛出的，
        // 宿主的 EnterSearch 因此先跑（实测 reset 比 raw-release 早约 1ms）；但 ButtonBase
        // 抛完 Click 之后还会 UpdateVisualState 回落 PointerOver/Normal，把我们在 Click 里
        // 刚压上的 Pressed 冲掉 —— 所以只在 EnterSearch 里压一次是不够的。
        // 我们的订阅（handledEventsToo）排在那个覆写之后，正好是最后一个能改状态的
        // 位置：这里再压一次，之后直到快照定格都没有人再动它。
        if (!pressed && _enterPending())
        {
            HoldPressed();
            return;
        }
        // 退出形变播放期间：任何复位都夺回成悬停态（ButtonBase 的 UpdateVisualState
        // 会在退出编排里把我们压的 PointerOver 打回 Normal，与进入侧同一个坑）
        if (!pressed && _exitHolding())
        {
            HoldHover();
            return;
        }
        SetPressedCore(pressed, writeThickness: true);
    }

    // 真正的写入口：不判闸门（给 Hold* 用），闸门判定在 SetPressed 里
    private void SetPressedCore(bool pressed, bool writeThickness = false)
    {
        _pressed[0] = pressed;
        if (writeThickness && _buttonRef.Current is Button titleBtn)
        {
            titleBtn.BorderThickness = pressed ? BorderFocused : BorderNormal;
        }
    }

    // ========================================================================
    // 三态画刷：走官方 lightweight styling —— 覆盖 SubtleButtonStyle 在
    // CommonStates 里使用的资源键。
    //
    // 为什么不能直接改画笔：官方 SubtleButton 的悬停/按下外观不是 Setter，而是
    // ObjectAnimationUsingKeyFrames，动画目标写死为模板内的 ContentPresenter.Background。
    // 依赖属性优先级里【动画 > 本地值】，所以无论用 .InteractionStates(background:)
    // 还是直接写 Button.Background，一旦真实悬停，官方动画就会整个接管，我们的颜色
    // 一个像素都渲染不出来。实测（亮色，进 PointerOver 后读模板内 ContentPresenter
    // 的实际画笔）：
    //   normal                   presenterBg = #00FFFFFF（跟随 TemplateBinding）
    //   本地设 #80F9F9F9         presenterBg = #80F9F9F9（仍跟随）
    //   GoToState(PointerOver)   presenterBg = #09000000  ← 被动画夺走
    // 那个 #09000000 正是官方 SubtleFillColorSecondaryBrush，与搜索框语义要的
    // ControlFillColorSecondaryBrush 是两套不同的键。
    //
    // 正确做法就是官方文档里的 lightweight styling：把官方键在按钮的 Resources 上
    // 覆盖掉，官方 Storyboard 照常运行、只是取到我们的颜色。好处是模板结构不动，
    // 官方模板自带的 BrushTransition Duration="0:0:0.083" 也一并保留 —— 悬停过渡由
    // 官方免费提供，不必自己补 curve。
    //
    // 用 Theme.Ref(键)（而非解析好的 Brush 实例）是为了让框架自己跟踪主题：
    // 写死画笔就得自己按亮暗重算并缓存实例，把跟随主题的责任揽到手上。
    // ========================================================================
    private static void ApplySearchBoxTokens(ResourceBuilder r)
    {
        // ── 背景 ──
        r.Set(KeyBackgroundPointerOver, Theme.Ref(TokenHoverFill))
         .Set(KeyBackgroundPressed, Theme.Ref(TokenFocusedFill))
         // 官方 SubtleButtonBackground（= 静置态）【保持透明，刻意不动】：
         // 这个按钮在没被招惹时应当只是"一行字"，一有底色就变成"顶栏上摆了个空输入框"。
         // 真正有别的是按下 / 悬停 —— 那两态才是"这是可以点的输入框"的提示。
         // 这与官方 SubtleButton 的语义一致（它本来就是"无铬按钮"），我们不改它。

         // ── 描边 ──
         // 只覆盖 PointerOver / Pressed 两态。【静置态刻意不覆盖】——
         // SubtleButtonBorderBrush 保持官方默认的 SubtleFillColorTransparentBrush，
         // 于是没被碰的时候那一圈 1px 描边画的是透明色：静态无边框，只有一行字。
         // 这条是产品取舍（不是漏覆盖）：顶栏标题平时就该融进去，把"这是个输入框"
         // 的信息留给悬停/按下再给出，常态挂着一圈灰边会显得顶栏多了一道格栅。
         // 代价是亮色主题下静置时完全读不出输入框语义 —— 这是明确接受的。
         //
         // ⚠️ 与【背景】那条（静置保持透明）配套看：两者的静置态都走官方原值，
         // 需要的是"无事发生时什么都不画"。改动时别只改一半，否则会变成
         // "有框无底"或"有底无框"的半吊子铬层。
         .Set(KeyBorderBrushPointerOver, Theme.Ref(TokenHoverStroke))
         .Set(KeyBorderBrushPressed, Theme.Ref(TokenFocusedStroke))

         // ── 前景 ──
         // 官方 SubtleButtonForegroundPressed = TextFillColorSecondaryBrush，按下瞬间
         // 标题会整体变淡。搜索框聚焦时前景不变（TextControlForegroundFocused 同
         // Normal），这里保持主文本色，避免按下时文字闪一下。
         .Set(KeyForegroundPressed, Theme.Ref(TokenFocusedText));
    }

    // ── 被覆盖的官方 SubtleButtonStyle 资源键（见 generic.xaml 的 SubtleButton 段）──
    private const string KeyBackgroundPointerOver = "SubtleButtonBackgroundPointerOver";
    private const string KeyBackgroundPressed = "SubtleButtonBackgroundPressed";
    // 注：没有 KeyBorderBrush / KeyBackground —— 静置态走官方原值（透明），
    //      见 ApplySearchBoxTokens 里「刻意不覆盖」的说明。
    private const string KeyBorderBrushPointerOver = "SubtleButtonBorderBrushPointerOver";
    private const string KeyBorderBrushPressed = "SubtleButtonBorderBrushPressed";
    private const string KeyForegroundPressed = "SubtleButtonForegroundPressed";

    // ── 替换用的官方主题键（全部走 TextControl 语义，与 XAML 里 {ThemeResource} 同源）──
    // 整体映射：搜索框是「按下即聚焦」，所以标题按钮的 Pressed 态取的是输入框的
    // **Focused** 资源，不是 Button 的 Pressed 资源 —— 两者值不同：
    //   TextControlBackgroundFocused = ControlFillColorInputActiveBrush（聚焦底，不透明）
    //   ButtonBackgroundPressed      = ControlFillColorTertiaryBrush（按下底，半透明灰）
    // 用错会表现成「按下时比悬停还淡」，而不是「按下即进入输入态」。

    // 悬停底 = TextControlBackgroundPointerOver（= ControlFillColorSecondaryBrush）
    private const string TokenHoverFill = "TextControlBackgroundPointerOver";
    // 聚焦底 = TextControlBackgroundFocused（= ControlFillColorInputActiveBrush）
    private const string TokenFocusedFill = "TextControlBackgroundFocused";

    // 悬停边 = TextControlBorderBrushPointerOver
    //          注：官方把 TextControlBorderBrush 与 TextControlBorderBrushPointerOver
    //          都指向同一个 TextControlElevationBorderBrush，取哪个值都一样，
    //          这里取 PointerOver 键名只为与 TokenHoverFill 状态配套、语义自明。
    private const string TokenHoverStroke = "TextControlBorderBrushPointerOver";
    // 聚焦边 = TextControlBorderBrushFocused（= TextControlElevationBorderFocusedBrush，
    //          **强调色**渐变：SystemAccentColorLight2 → ControlStrokeColorDefault）
    private const string TokenFocusedStroke = "TextControlBorderBrushFocused";
    // 聚焦前景 = TextControlForegroundFocused（同 Normal，聚焦时文字不变色）
    private const string TokenFocusedText = "TextControlForegroundFocused";
}
