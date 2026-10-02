using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using XamlAnimatedIcon = Microsoft.UI.Xaml.Controls.AnimatedIcon;
// ButtonBase.IsPressed —— 按压态的唯一真相源（见 HookPress 处注释）
using XamlButtonBase = Microsoft.UI.Xaml.Controls.Primitives.ButtonBase;
// Reactor 的元素引用实际类型在 Input 命名空间（Core 下另有同名类型，需消歧）
using ElementRef = Microsoft.UI.Reactor.Input.ElementRef;
// 与 Windows.System.DispatcherQueueTimer 消歧
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using static Microsoft.UI.Reactor.Factories;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

// <summary>
// 顶栏上的一个操作按钮描述（左侧组 / 右侧组通用），数量不限。
// 二选一提供图标：
//   - StaticIcon：静态图标（Reactor 的 IconData，如 new SymbolIconData("Add")）
//   - AnimatedSource：动画图标（WinUI 的 IAnimatedVisualSource2，如 new AnimatedSettingsVisualSource()）
// </summary>
// internal：渲染层的实现细节，不是对外契约（对外只有 TopBarSnapshot / TopBarEvent）。
// 与 AppTopBarProps 一起收口，避免外部绕开快照直接构造 props 走老路。
//
// 【值语义必须手写】record 合成的 Equals 会把委托字段（OnClick）也按引用比一遍，
// 而 ToAction 每次翻译都是新 lambda ⇒ 只要左右两组有按钮，props 就永远判定为"变了"
// ⇒ 宿主每次 Changed 都全量重渲染，注释里"刻意忽略委托"的意图完全落空。
// 这里显式实现 IEquatable，把 OnClick 排除在外。
internal sealed record TopBarAction(
    string AutomationName,
    IconData? StaticIcon = null,
    IAnimatedVisualSource2? AnimatedSource = null,
    IconSource? FallbackIconSource = null,
    Action? OnClick = null,
    // 可用 / 禁用。Enabled 之外的状态由本组件负责呈现与拦截点击，
    // 因此回调的调用方（宿主）不需要自己判断状态。
    TopBarButtonState State = TopBarButtonState.Enabled,
    // 悬停提示。契约里 TopBarButton.Tooltip 缺省时会退化成 Glyph 同文（见 TopBarButton.Of）。
    string Tooltip = "") : IEquatable<TopBarAction>
{
    public bool Equals(TopBarAction? other) =>
        other is not null
        && AutomationName == other.AutomationName
        && State == other.State
        && Tooltip == other.Tooltip
        && Equals(StaticIcon, other.StaticIcon)
        // 动画视觉源是 WinRT 对象，不做值比较；同一次构建里复用同一实例即可命中
        && ReferenceEquals(AnimatedSource, other.AnimatedSource)
        && ReferenceEquals(FallbackIconSource, other.FallbackIconSource);

    // Equals 忽略 OnClick 与上面两个引用字段不同权：hash 只用值语义那几个，保持一致。
    public override int GetHashCode() => HashCode.Combine(AutomationName, State, Tooltip, StaticIcon);
}

// <summary>
// AppTopBar 的可配置属性（不可变 record，父组件用 with 更新）。
//
// 【这是组件内部的实现细节，不是对外契约】
// 对外契约只有两样：<see cref="TopBarSnapshot"/>（下行）与 <see cref="TopBarEvent"/>（上行）。
// 转接层只跟那两样打交道；这里的字段是渲染层需要的形态，由
// <see cref="FromSnapshot"/> 从快照翻译而来 —— 翻译包括把统一的事件出口
// 包成各个具体的 OnBack / OnSearch / OnClick，渲染代码因此不必知道事件的存在。
//
// 比较：<see cref="Equals"/> 只比数据字段，【刻意忽略 OnEvent】。
// 委托按引用比较，若参与比较，宿主每次重建委托都会让顶栏白白重渲染一次。
// </summary>
internal sealed record AppTopBarProps : IEquatable<AppTopBarProps>
{
    // 居中位【当前文本】：Title 形态下是标题文字，Search 形态下是输入框里的当前文本
    // （受控）。两种形态共用一个字段，因为它们在视觉上是同一个位置互相形变，
    // 换形态时不该走"换字动画"。
    public string CenterText { get; init; } = string.Empty;

    // 整个顶栏是否可见：隐藏时底衬层与所有内容（标题、自定义按钮、搜索按钮）
    // 做 220ms 淡入淡出；返回按钮不参与淡出，始终显示且可点击
    public bool IsVisible { get; init; } = true;

    // 底衬是否【可见】，与 IsVisible 正交（沉浸式页面给 false）：
    //   true  = 期望顶栏有底衬，且空白区拦截点击；
    //   false = 空白区点击穿透到下层内容，标题/按钮/返回全部保留，文字浮在沉浸背景上。
    // 整栏隐藏（IsVisible=false）时此开关无附加效果。
    //
    // 【默认 false】与契约 <see cref="TopBarSnapshot"/> 的默认值（Backdrop = Transparent）一致：
    // 没人明确要底衬就别画；转接层想显示底衬必须显式表态。
    //
    // 【注意：名字里的"可见"指意图，不是本组件真的画出了什么】
    // Reactor 里挂 AcrylicBrush 始终做不出正常材质（in-app 采样源落空 ⇒ 只剩 tint ⇒ 发白），
    // 所以本组件渲染出的底衬层恒【透明】：它不承担外观，只承担两件事 ——
    //   ① 空白区的命中测试开关（真正被渲染消费的那一项）；
    //   ② 与 Material 一起合成 TopBarEvent.Material 转出去（只是意图，不是实现）。
    // 真正的材质由订阅方在别处实现，与本组件解耦。
    public bool IsBackdropVisible { get; init; }

    // 顶栏独立主题（亮 / 暗 / 跟随系统），只作用于本控件子树，不影响应用其余部分；
    // 底衬、文字/图标/输入框等全部通过主题资源随该设置切换。
    public TopBarThemeMode Theme { get; init; } = TopBarThemeMode.System;

    // 左侧组与窗口左边缘的间距（像素）
    public double LeftPadding { get; init; } = 12;

    // 右侧组与窗口右边缘的间距（像素）
    public double RightPadding { get; init; } = 12;

    // 左侧组自定义图标按钮（返回按钮之后），不限数量；null/空 = 没有
    public IReadOnlyList<TopBarAction>? LeftActions { get; init; }

    // 右侧组图标按钮，默认 null（不显示）；搜索模式下右侧组只保留搜索按钮
    public IReadOnlyList<TopBarAction>? RightActions { get; init; }

    // 左右两组的自定义按钮是否显示（false = 这一份快照不要按钮，只留返回按钮）
    public bool ShowActions { get; init; } = true;

    // 中间标题区是否显示（false = 这份快照没有居中内容，底衬与返回按钮照常）
    public bool ShowCenter { get; init; } = true;

    // 居中位文本左侧的可选图标（null = 只有文字）。
    // 名字不写死"标题"：Search 形态下它是搜索框的图标。
    public IconData? CenterIcon { get; init; }

    // 材质【提示】：快照给什么这里就是什么，顶栏原样转出（TopBarEvent.Material），
    // 自己不解释也不实现。
    public TopBarMaterial Material { get; init; } = TopBarMaterial.None;

    // 返回按钮是否显示（快照的 Back 为 null 时 false）。
    // 注：整栏隐藏（IsVisible=false）时它【不参与淡出】，始终可点 —— 那是另一码事。
    public bool ShowBack { get; init; } = true;

    // Search 形态下输入框的空态占位提示（来自 TopBarCenter.Search.Placeholder，缺省回落到"搜索"）
    public string CenterPlaceholder { get; init; } = string.Empty;

    // Search 形态下右缘「确认」按钮的图标形态（来自 TopBarCenter.Search.Accept）。
    // null = 没指定 ⇒ 渲染侧按默认（动画放大镜）处理，历史行为不变。
    // 它是【联合类型】而不是"一个字符串 + 两个 bool"：静态字形 / 动画 / 不要按钮
    // 三者互斥，交给类型去排，日后加第四种形态不用动这里。
    public TopBarAcceptIcon? AcceptIcon { get; init; }

    // Search 形态下是否把【每次按键后的文本】上抛成 TopBarEvent.TextChanged。
    // 默认 false —— 见 TopBarEvent.TextChanged 的注释：打开就等于"每敲一个字就让页面
    // 有机会下发一份快照"，那是 3000 行 Render 的重渲染，必须是页面显式要的东西。
    public bool LiveText { get; init; }

    // 顶栏【唯一】的对外出口：按钮点击 / 搜索提交 / 退出搜索 / 材质意图全部从这里出去。
    // 下面两个 OnBack / OnSearch 只是它的包装（见 FromSnapshot）。
    // 它只是通道，不含任何业务行为 —— 收到之后做什么由转接层决定。
    // 刻意不参与 Equals 比较（见类型注释）。
    public Action<TopBarEvent>? OnEvent { get; init; }

    // 居中位是否【可搜索】：由快照的居中形态翻译而来（Search 形态 = true）。
    //   true  = 标题可点开搜索框（悬停呈输入框外观）；
    //   false = 标题为不可点击的纯文本，且【静置铬层与悬停铬层都透明】，
    //           文字保持非悬停态外观（只看得见一行字）。
    public bool Searchable { get; init; } = true;

    // 返回按钮：搜索模式下点击 = 退出搜索；普通模式下回调此委托。
    // 【由 FromSnapshot 生成】—— 它只是把 OnEvent 包成 Clicked(返回按钮 Id)。
    public Action? OnBack { get; init; }

    // 输入框文本每次变化时回调（把 LiveText 的口子单独开出来，不混进 OnSearch ——
    // 两者语义不同：TextChanged 是"正在输入"，Submitted 是"确认提交"）。
    // 未开启 LiveText 时为 null，渲染侧据此走最省的那条路。
    public Action<string>? OnTextChanged { get; init; }

    // 提交搜索（回车或点击搜索按钮）时回调，参数为查询文本。
    // 【由 FromSnapshot 生成】—— 它只是把 OnEvent 包成 Submitted(文本)。
    public Action<string>? OnSearch { get; init; }

    // ---- 值语义：只比数据，忽略 OnEvent（理由见类型注释）----

    public bool Equals(AppTopBarProps? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return CenterText == other.CenterText
            && Material == other.Material            && ShowBack == other.ShowBack
            && IsVisible == other.IsVisible
            && IsBackdropVisible == other.IsBackdropVisible
            && Theme == other.Theme
            && LeftPadding.Equals(other.LeftPadding)
            && RightPadding.Equals(other.RightPadding)
            && ShowActions == other.ShowActions
            && ShowCenter == other.ShowCenter
            && CenterPlaceholder == other.CenterPlaceholder
            && Searchable == other.Searchable
            && LiveText == other.LiveText
            && Equals(AcceptIcon, other.AcceptIcon)
            && Equals(CenterIcon, other.CenterIcon)
            && SameActions(LeftActions, other.LeftActions)
            && SameActions(RightActions, other.RightActions);
    }

    // 列表必须【逐项值比较】。静态 object.Equals 落到 List/Array 上就是引用比较，
    // 而 ToAction 每次翻译都 new 一份 ⇒ 引用永远不等 ⇒ 上面这段 Equals 整体失效。
    private static bool SameActions(IReadOnlyList<TopBarAction>? a, IReadOnlyList<TopBarAction>? b) =>
        a is null || b is null
            ? ReferenceEquals(a, b)
            : a.SequenceEqual(b);

    // HashCode.Combine 最多 8 个参数，这里老老实实用 HashCode 实例累加
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CenterText);
        hash.Add(Material);
        hash.Add(ShowBack);
        hash.Add(IsVisible);
        hash.Add(IsBackdropVisible);
        hash.Add(Theme);
        hash.Add(LeftPadding);
        hash.Add(RightPadding);
        hash.Add(ShowActions);
        hash.Add(ShowCenter);
        hash.Add(Searchable);
        hash.Add(LiveText);
        hash.Add(AcceptIcon);
        hash.Add(CenterIcon);
        return hash.ToHashCode();
    }

    // ---- 快照 → props 的翻译（组件对外唯一入口）----

    /// <summary>
    /// 把对外契约（一份快照 + 一个事件出口）翻译成渲染层需要的形态。
    /// 转接层不直接摆弄上面这些字段：它给快照，这里负责翻译。
    /// </summary>
    /// <param name="snapshot">下行快照。</param>
    /// <param name="onEvent">上行事件出口（通常是 <c>TopBarChannel.Raise</c>）。</param>
    public static AppTopBarProps FromSnapshot(
        TopBarSnapshot snapshot,
        Action<TopBarEvent>? onEvent = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        void Raise(TopBarEvent e) => onEvent?.Invoke(e);

        // 居中位决定标题与搜索两件事：
        //   Title  = 纯标题（点了没反应）；
        //   Search = 可点开搜索的标题；
        //   None   = 居中位空着。
        // 两种形态互斥 —— 不需要额外的 Searchable 开关，形态本身就是开关。
        var center = snapshot.Center;
        var search = center as TopBarCenter.Search;
        var centerIcon = center switch
        {
            TopBarCenter.Title t => t.Icon,
            TopBarCenter.Search s => s.Icon,
            _ => null,
        };
        var centerText = center switch
        {
            TopBarCenter.Title t => t.Text,
            TopBarCenter.Search s => s.Text,
            _ => string.Empty,
        };

        TopBarAction? ToAction(TopBarButton? button)
        {
            if (button is null)
            {
                return null;
            }

            // 动画图标优先：同时给了 Glyph 与 Animated 时按动画渲染，Glyph 自动降级成
            // 动画的 fallback（系统关闭动态效果时看到的就是它，不会变空白框）。
            // 解析结果带缓存 ⇒ 同键永远同一实例，TopBarAction 的引用比较因此能命中。
            var animated = button.Animated is { } desc
                ? TopBarAnimatedSources.TryResolve(desc)
                : null;

            return new TopBarAction(
                AutomationName: button.Id,
                StaticIcon: string.IsNullOrEmpty(button.Glyph)
                    ? null
                    : new SymbolIconData(button.Glyph),
                AnimatedSource: animated?.Source,
                FallbackIconSource: animated?.Fallback,
                // 点击统一出口：顶栏不知道这个按钮"是"什么，只把 Id 抛出去
                OnClick: () => Raise(new TopBarEvent.Clicked(button.Id)),
                State: button.State,
                // 悬停提示：契约里缺省即与 Glyph 同文（见 TopBarButton.Of），这里原样透传
                Tooltip: button.Tooltip);
        }

        return new AppTopBarProps
        {
            CenterText = centerText,
            // 标题图标：字形 → SymbolIconData（沿用既有路径）；位图 → ImageIconData。
            // 位图分支不走 BitmapIconData：它的 ShowAsMonochrome 默认 true，会把彩色
            // 位图压成单色剪影，这里要的是原图。
            CenterIcon = centerIcon switch
            {
                null => null,
                GlyphCenterIcon g when !string.IsNullOrEmpty(g.Glyph) => new SymbolIconData(g.Glyph),
                BitmapCenterIcon b => new ImageIconData(b.Source),
                _ => null,
            },
            ShowBack = snapshot.Back is not null,
            Material = snapshot.Material,
            IsVisible = snapshot.Visible,
            // 只是【意图】：顶栏自己不画材质，会把它转成 TopBarEvent.Material 转出，
            // 真正的材质由订阅方在别处实现（见 TopBarMessageHub 注释）。
            IsBackdropVisible = snapshot.Backdrop == TopBarBackdrop.Visible,
            Theme = snapshot.Theme,
            ShowActions = snapshot.ShowActions,
            // 两种形态都显示标题（Search 形态下它就是"可点开的那个标题"），
            // 只有 None 才把居中位留空。
            ShowCenter = search is not null || center is TopBarCenter.Title,
            LeftPadding = snapshot.LeftPadding,
            RightPadding = snapshot.RightPadding,
            LeftActions = snapshot.Left.Count > 0
                ? snapshot.Left.Select(b => ToAction(b)!).ToArray()
                : null,
            RightActions = snapshot.Right.Count > 0
                ? snapshot.Right.Select(b => ToAction(b)!).ToArray()
                : null,
            Searchable = search is not null,
            // 搜索框占位提示（来自 TopBarCenter.Search.Placeholder；空则由渲染侧回落默认）
            CenterPlaceholder = search?.Placeholder ?? string.Empty,
            // 确认按钮图标：原样透传给渲染层解析（联合类型自己知道该怎么画）
            AcceptIcon = search?.Accept,
            LiveText = search?.LiveText ?? false,
            OnEvent = onEvent,
            OnBack = snapshot.Back is null
                ? null
                : () => Raise(new TopBarEvent.Clicked(snapshot.Back.Id)),
            OnSearch = q => Raise(new TopBarEvent.Submitted(q)),
            // 没开 LiveText 就给 null：渲染侧连判断都省掉（也不必为此多背一个 lambda）
            OnTextChanged = search?.LiveText == true
                ? q => Raise(new TopBarEvent.TextChanged(q))
                : null,
        };
    }
}

// <summary>
// 类似手机 App 的顶部栏（无拖动功能）。
//
// 结构（Y 轴三层叠放，48px 高，宽度跟随外部容器）：
//   下层：底衬层（恒透明，不挂任何材质画刷，只负责空白区的命中测试；
//         真正的材质由订阅 TopBarEvent.Material 的一方在别处实现）；
//   中层：左 / 中 / 右三组 FlexRow（中间为弹性占位，标题在覆盖层绝对居中）；
//   上层：铺满整栏的覆盖层，内含标题宿主 / 搜索输入框 / 右缘固定的搜索按钮（互为兄弟）。
// 标题平时为与输入框等大的透明按钮（悬停呈输入框外观、右键复制），点击后以
// 两阶段（静置前导 → 直线共享元素形变）切换为搜索输入框；退出对称（失焦静置 → 形变）。
// </summary>
internal sealed class AppTopBar : Component<AppTopBarProps>
{
    // 在子树里找第一个指定类型的元素。复位按钮视觉态时用它把里面的 AnimatedIcon
    // 一并拉回 Normal —— Button 的模板本该用 Setter 把 AnimatedIcon 绑到 CommonStates
    // 上，但池复用 / 延后渲染的路径下那次绑定不一定跑，会出现"背景回到 Normal、
    // 图标还停在 PointerOver"的错位（见 HookButton 内 OnInteractivityChanged 注释）。
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }


    private const string BackButtonKey = "__back";
    private const string SearchButtonKey = "__search";

    // 快照没给 Placeholder 时的默认搜索框占位提示
    private const string DefaultSearchPlaceholder = "搜索";

    // 标题图标格的固定边长（px）。
    // 【写死】而不由内容（尤其位图的自然尺寸 / 解码状态）决定：位图没解完时自然尺寸是 0，
    // 靠内容撑开会让图标格在 0 ↔ 16 之间抖一下，顺带把标题文本的位置带着重排
    //（titleHost.OnSizeChanged → setTitleMeasuredW → 又一轮渲染 + 几何补间）。
    private const double TitleIconSize = 16;

    // 字形图标的字号。必须走 FontIcon + FontSize=16，不能像位图那样把元素框压到 16×16：
    // 16px 的 SymbolIcon 内置字号是 20，压进 16×16 会把部分字形裁掉（实测 Favorite
    // 星形右侧被切出直边）。FontIcon 的自然尺寸就是字号，16 正好落进 16×16 的格子里。
    private const double TitleIconFontSize = 16;

    // 标题图标的三种互斥形态（无 / 字形 / 位图）。
    // IconData 是判别联合，渲染层先用 ResolveCenterIcon 把它压成这个纯数据三态，
    // 之后【只】按 Kind 切可见性、按 Glyph / Source 改属性 —— 不再按 IconData 现场
    // 挑选并创建原生控件。
    private enum CenterIconKind
    {
        None,
        Glyph,
        Bitmap,
    }

    private readonly record struct CenterIconPlan(
        CenterIconKind Kind,
        string? Glyph,
        Uri? Source);

    // <summary>
    // 把 IconData 解析成 <see cref="CenterIconPlan"/>。
    // 【纯函数】：不创建任何原生控件，可以在逐帧 Render 里随便调。
    //
    // 取代旧的 BuildCenterIcon —— 那个函数按 IconData 现场 new 一个
    // Core.IconElement（Reactor 的装饰元素，挂载时才解析出真正的原生
    // Microsoft.UI.Xaml.Controls.IconElement 子类），而顶栏在几何补间期间是逐帧
    // 重渲染的（StartTitleShift 16ms/帧）⇒ 原生图标被逐帧销毁重建；位图那支还要
    // 重新解码，解出来之前控件是空的 —— 就是“标题图标闪”。
    //
    // 各分支与旧实现的对应关系：
    //   null                                     → None（旧实现渲染一个空字形占位；
    //                                              现在整格 Collapsed，宽 0，效果一致）
    //   ImageIconData / BitmapIconData           → Bitmap（旧实现两者都走 ImageIcon：
    //                                              BitmapIcon 的 ShowAsMonochrome 默认
    //                                              true，会把彩色位图压成单色剪影）
    //   SymbolIconData                           → Glyph（Segoe Fluent Icons 的码点 =
    //                                              Symbol 枚举的数值，官方枚举即按码点
    //                                              定义，可直接换算）
    //   FontIconData                             → Glyph（原样取 Glyph，字号统一压到
    //                                              TitleIconFontSize）
    //   其余（PathIconData / 无法解析的 Symbol 名）→ None。转接层（FromSnapshot 的
    //   CenterIcon 翻译）只会下发上面前三种，这里不再为其余形态准备原生控件。
    // </summary>
    private static CenterIconPlan ResolveCenterIcon(IconData? data) => data switch
    {
        null => default,
        ImageIconData img when img.Source is not null
            => new CenterIconPlan(CenterIconKind.Bitmap, null, img.Source),
        BitmapIconData bmp when bmp.Source is not null
            => new CenterIconPlan(CenterIconKind.Bitmap, null, bmp.Source),
        SymbolIconData sym when Enum.TryParse<Symbol>(sym.Symbol, ignoreCase: true, out var s)
            => new CenterIconPlan(CenterIconKind.Glyph, char.ConvertFromUtf32((int)s), null),
        FontIconData font when !string.IsNullOrEmpty(font.Glyph)
            => new CenterIconPlan(CenterIconKind.Glyph, font.Glyph, null),
        _ => default,
    };

    // 注（2026-10-01）：亚克力的一整套参数（TintColor / TintOpacity /
    // TintLuminosityOpacity / FallbackColor）已随实现一起删除——本组件不再画材质，
    // 只把意图转成消息转出，参数由真正实现材质的一方自己决定。
    // 需要参考值时见 .workbuddy/memory 里"亚克力"相关条目（DockedTools 那份对齐值）。

    // <summary>
    // 把任意来源的查询文本规范化为【单行 TextBox 能原样承载】的形式。
    // 这是受控 TextBox 的硬性前提：写入 query 的值必须恒等于 TextBox.Text 回读的值，
    // 一旦失配，Reactor 受控属性的回声抑制令牌就会泄漏（它按计数吞事件、不比值），
    // 之后用户每次击键的 TextChanged 都被吞掉，渲染又把旧 query 覆盖回输入框
    // —— 即「粘贴后编辑内容被粘贴值覆盖」。
    // </summary>
    private static string SanitizeQuery(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        // 统一换行符后取首行：AcceptsReturn=false 的单行框承载多行文本时 WinUI 只保留
        // 第一行（microsoft-ui-xaml#10956，官方已收进 backlog），直接写原文必然失配
        var t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var i = t.IndexOf('\n');
        if (i >= 0)
        {
            t = t.Substring(0, i);
        }

        // 剪贴板 / TSF 偶尔夹带 NUL，单行框同样无法承载
        return t.Replace("\0", string.Empty);
    }

    public override Element Render()
    {
        var p = Props;

        // 搜索模式与查询文本（状态与渲染共存，Reactor 函数式 MVU）
        var (searching, setSearching) = UseState(false);
        var (query, setQuery) = UseState(string.Empty);

        // 当前处于按压态的动画按钮标识（同一时刻只有一个）
        var (pressedKey, setPressedKey) = UseState<string?>(null);

        // pressedKey 是【渲染期】快照，指针回调里捕获到的永远是当时那一轮的副本；
        // 松开的那一刻拿它做"该不该清"的判断会过期。这里放一份跨渲染稳定的同义盒子。
        var pressOwner = UseRef<string?>(null);

        // 返回按钮上一轮是否"可交互"（既显示、整栏也可见）。用它捕捉
        // 可交互 ⇄ 不可交互 的跨越点 —— 见 BuildBackButton 里的复位。
        var backInteractable = UseRef(true);

        // 指针是否停在搜索按钮上：松开时决定回到 PointerOver 还是 Normal
        var searchHovering = UseRef(false);
        // 按【按钮 Id】缓存的按压订阅：AddHandler 不去重，必须自己按元素实例做门控。
        // 静态图标按钮也进这本字典 —— 它们不需要按压订阅，但需要同样那道上树复位。
        var pressHooks = UseRef(new Dictionary<string, (
            Button Owner,
            Microsoft.UI.Xaml.Input.PointerEventHandler? Pressed,
            Microsoft.UI.Xaml.Input.PointerEventHandler? Released,
            Microsoft.UI.Xaml.Input.PointerEventHandler? Exited,
            Microsoft.UI.Xaml.Input.PointerEventHandler? Canceled,
            long PressToken)>());
        // 搜索按钮专用的一套（它要额外维护 PointerOver，所以不走 pressHooks）
        var searchHook = UseRef<(Button Owner,
            Microsoft.UI.Xaml.Input.PointerEventHandler Entered,
            Microsoft.UI.Xaml.Input.PointerEventHandler Exited,
            Microsoft.UI.Xaml.Input.PointerEventHandler Pressed,
            Microsoft.UI.Xaml.Input.PointerEventHandler Released,
            Microsoft.UI.Xaml.Input.PointerEventHandler Canceled,
            long PressToken)?>(null);

        void SetPress(string? key)
        {
            pressOwner.Current = key;
            setPressedKey(key);
        }

        // 只清【发起方自己】的按压标志：多个动画按钮不该互相抹；
        // 也避免某个按钮迟到的松开把刚按下的一颗打回去。
        void ReleasePress(string key)
        {
            if (pressOwner.Current == key)
            {
                SetPress(null);
            }
        }

        // ── 上树即复位：CommonStates 的悬停/按压残留的根治 ───────────────
        // Button 的 PointerOver / Pressed 是 WinUI 自己的视觉状态机，我们改不到也读不全；
        // 而一旦交互没走到 PointerExited（卸载、命中测试被关、页面被换掉、元素从池里被
        // 复用给另外一个按钮…），它就永远停在那一帧 —— 表现就是"回来时还是悬停态"。
        //
        // 时机必须是 Loaded 之后：.Set 在渲染期跑，此刻控件可能还没 ApplyTemplate，
        // GoToState 会直接失败（返回 false）。而 Loaded 每次重新上树都会再抛一次，
        // 正好覆盖"池回收后复用"这条路径，所以这里【不】在触发一次后就摘掉监听。
        void ResetVisualOnLoad(Button button)
        {
            void OnLoaded(object? sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            {
                VisualStateManager.GoToState(button, "Normal", false);
            }

            button.Loaded += OnLoaded;
            if (button.IsLoaded)
            {
                VisualStateManager.GoToState(button, "Normal", false);
            }
        }

        // ── 把按钮的视觉状态整体拉回 Normal ──────────────────────────
        // Button 的 PointerOver / Pressed 是控件模板内部的 CommonStates，读不到也改不了，
        // 显式 GoToState 是唯一的复位手段。
        void ResetButtonVisual(Button button)
        {
                void Reset()
                {
                    if (button.XamlRoot is null)
                    {
                        return;
                    }

                    VisualStateManager.GoToState(button, "Normal", false);

                // 图标态一并拉回：Button 的模板本该用 Setter 把 AnimatedIcon 绑到
                // CommonStates，但池复用 / 延后渲染下那次绑定不一定跑，会出现
                // "背景回到 Normal、图标还停在 PointerOver"的错位。
                if (FindChild<XamlAnimatedIcon>(button) is { } icon
                    && XamlAnimatedIcon.GetState(icon) != "Normal")
                {
                    XamlAnimatedIcon.SetState(icon, "Normal");
                }
            }

            // 延后一拍：调用点都落在 Reactor 的渲染过程中，同一轮渲染里后续的修饰符
            // 可能又把状态写回去（第一版没延后就白复位了），等本帧结束再动手才稳。
            var queue = button.DispatcherQueue;
            if (queue is null || queue.HasThreadAccess)
            {
                Reset();
                return;
            }

            queue.TryEnqueue(Reset);
        }

        // ── 回到顶级：把按钮的【视觉树】整个销毁重建 ────────────────────
        // 【为什么不做 Reactor 的条件渲染（把按钮从子元素数组里摘掉）】
        //   实测必崩，两个框架机制互相打架：
        //     Reconciler.RemoveChildWithExitTransition → 给元素挂 OpacityTransition 做退出动画
        //     → UnmountAndPool → ElementPool.CleanElement → ClearValue(RenderTransform)
        //     → WinUI 拒绝访问：正在用 OpacityTransition 的对象不允许改 RenderTransform。
        //   而且在作者层关不掉池化 —— PoolPolicy<T>.IsPoolable=false 是【handler】在
        //   RentControl 时附加的（Reactor 官方 XML 注释原文："External and built-in V1
        //   handlers attach a PoolPolicy<TControl> to their RentControl / ReturnControl
        //   calls"），内置 Button handler 我们够不着。
        //
        // 【所以改成只销毁"视觉部分"】Button 实例本身留在 Reactor 树里 —— 不卸载、不进池，
        // 自然不会触发 CleanElement；销毁的是它【内部那棵模板树】：
        //     Style = null  → 旧模板树被拆掉，ContentPresenter 及其上的一切随之消失，
        //                     包括 PointerOver 那支 HoldEnd 动画咬住的 Foreground / Background；
        //     Style = style → 重新 ApplyTemplate，长出一棵全新的、干净的、处于 Normal 的视觉树。
        //   两行同步执行，中间态不上屏，看不到闪。
        //
        //   语义上就是"回到顶级 ⇒ 这个按钮被销毁重建"，只是销毁的粒度是视觉树而非控件对象。
        void RebuildButtonVisualTree(Button button)
        {
            var style = button.Style;
            if (style is null)
            {
                return;
            }

            button.Style = null;
            button.Style = style;
        }

        // ── 动画按钮的按压订阅：统一挂号 + 统一复位 ───────────────────────
        // 【硬性约束】官方 AnimatedIcon 文档 Tip（本机已实测吻合）：
        //   Button 会把 PointerPressed / PointerReleased 标成 Handled —— 普通 += 订阅
        //   （Reactor 的 .OnPointerPressed 就是这种）一次都不会触发，必须改走
        //   AddHandler(routedEvent, handler, handledEventsToo: true)。
        //
        // 【为什么以前会"点击后残留按压态"】
        //   旧实现只听了 Pressed / Released / Exited 三档。只要这一次交互没能走到 Released
        //   —— 点击回调里同步触发重渲染把元素换掉、按钮变禁用/被卸载、窗口失焦、触摸序列被
        //   判定为平移… —— pressedKey 就永久钉在那个 Id 上；而每轮渲染都会按它把
        //   AnimatedIcon 重新按下去（见 BuildActionButton 的 .Set），于是再也弹不回来。
        //
        // 【三重兜底】
        //   ① AddHandler(handledEventsToo)  ：保证按下/松开都收得到；
        //   ② Canceled + Exited             ：触摸序列被劫走 / 指针被移出按钮；
        //   ③ ButtonBase.IsPressed 属性回调 ：IsPressed 由 ButtonBase 自己维护，无论以何种方式
        //      结束按压都必然回到 false —— 唯一不会说谎的复位信号。这是听 PointerCaptureLost
        //      的更稳替代：后者会在按下即捕获的那一拍误判为松开，反而把按压态抹掉。
        //   ④ 新元素实例上树时强制回到 Normal：见 ResetVisualOnLoad 处注释。
        //   ⑤ 同一个实例被藏起来（Collapsed / IsHitTestVisible=false）时也强制回 Normal：
        //      见 HookButton 内 OnInteractivityChanged 处注释。
        void HookButton(Button button, string key, bool animated)
        {
            var hooks = pressHooks.Current;
            if (hooks.TryGetValue(key, out var old) && ReferenceEquals(old.Owner, button))
            {
                // 同一实例已挂过 —— AddHandler 不按委托去重，重复订阅会随渲染次数单调增长
                return;
            }

            // 旧实例（被池回收/替换）的订阅必须摘掉，否则回调会打到已下树的按钮上
            if (old.Owner is { } stale)
            {
                if (old.Pressed is { } op)
                {
                    stale.RemoveHandler(UIElement.PointerPressedEvent, op);
                }
                if (old.Released is { } orr)
                {
                    stale.RemoveHandler(UIElement.PointerReleasedEvent, orr);
                }
                if (old.Exited is { } oe)
                {
                    stale.RemoveHandler(UIElement.PointerExitedEvent, oe);
                }
                if (old.Canceled is { } oc)
                {
                    stale.RemoveHandler(UIElement.PointerCanceledEvent, oc);
                }
                if (old.PressToken != 0)
                {
                    stale.UnregisterPropertyChangedCallback(
                        XamlButtonBase.IsPressedProperty, old.PressToken);
                }
            }

            // ① 【所有按钮，含静态图标】上树强制把 WinUI 的 CommonStates 拉回 Normal。
            //    关闭 / 返回 / 未固定的Tab 这类按钮点完就换页，PointerExited 常常压根没机会
            //    发出去；元素再从 Reactor 的池里被捞回来复用时，就带着上次的悬停态一起出现。
            ResetVisualOnLoad(button);

            // ①b【所有按钮】只要"可见性 / 命中测试"一有变化就把 CommonStates 拉回 Normal。
            //    与 ① 同源、但时机不同：① 只在【新实例上树】时跑，而这里管的是
            //    【同一个实例被藏起来又放出来】的情况 —— 返回按钮不显示时走 .IsVisible(false)
            //    （= Collapsed，实例仍在树里，不会重新 Loaded），整栏淡出时走
            //    FadeWithBar 的 IsHitTestVisible=false。两条路都让元素收不到指针事件
            //    ⇒ PointerExited 永远不来 ⇒ 视觉状态机停在 PointerOver；等它再出现时
            //    鼠标很可能已经不在上面，WinUI 也不会补发 PointerEntered，于是
            //    "刚出现的那一帧带着悬停底色"。CommonStates 是控件模板内部的状态，
            //    我们读不到也改不了，显式 GoToState 是唯一的复位手段。
            //    不登记 token：这个回调只做一次幂等复位，即便打到被池复用走的旧实例
            //    上也无害（对已下树的元素 GoToState 直接返回 false）。
            void OnInteractivityChanged(DependencyObject sender, DependencyProperty dp)
            {
                // 管的是按钮【自身】的 Visibility / IsHitTestVisible 变化
                //（返回按钮 Collapsed 那一条路）。
                // 整栏淡出关的是【父级】的命中测试，按钮自身属性不变 → 这条收不到，
                // 那一路由 BuildBackButton 按 props 判断（见那里注释）。
                if (sender is Button b)
                {
                    ResetButtonVisual(b);
                }
            }

            button.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, OnInteractivityChanged);
            button.RegisterPropertyChangedCallback(
                UIElement.IsHitTestVisibleProperty, OnInteractivityChanged);

            // ② 自己那份按压标志一并交还：按钮实例都换了，旧标志不可能还有效
            ReleasePress(key);

            if (!animated)
            {
                // 静态图标不需要任何指针订阅，CommonStates 由 Button 自己给
                hooks[key] = (button, null, null, null, null, 0);
                return;
            }

            var pressed = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => SetPress(key));
            var released = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ReleasePress(key));
            var exited = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ReleasePress(key));
            var canceled = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ReleasePress(key));

            void OnIsPressedChanged(DependencyObject sender, DependencyProperty dp)
            {
                if (sender is XamlButtonBase b && !b.IsPressed)
                {
                    ReleasePress(key);
                }
            }

            var token = button.RegisterPropertyChangedCallback(
                XamlButtonBase.IsPressedProperty, OnIsPressedChanged);

            button.AddHandler(UIElement.PointerPressedEvent, pressed, true);
            button.AddHandler(UIElement.PointerReleasedEvent, released, true);
            button.AddHandler(UIElement.PointerExitedEvent, exited, true);
            button.AddHandler(UIElement.PointerCanceledEvent, canceled, true);

            hooks[key] = (button, pressed, released, exited, canceled, token);
        }

        // ---- 顶栏独立主题 ----
        // 只有 ElementTheme 需要自己算：子树里的颜色（标题/图标前景、控件底色等）全部由
        // 主题资源跟着它走。原先那段"用 UISettings 实时探测系统亮/暗"的逻辑是专为亚克力
        // 画刷按主题选 tint 色而写的，材质移出本组件后随之删除（System 模式下
        // ElementTheme.Default 本来就跟随系统，不需要我们手动探测）。
        //
        // 【2026-10-02】ElementTheme 的【延后提交】编排已随亚克力一并删除：
        // 那段延后是为了对齐"背景 tint 300ms 连续渐变 vs 前景瞬间翻转"的低对比度空窗，
        // 而本组件内已经没有任何背景渐变需要对它。现在主题一变就同帧提交，
        // 材质层（外部实现方）也靠下面那条 Material 意图同步换肤，不会再出现
        // "文字已经变深、亚克力还停在旧色调"。
        var elementTheme = p.Theme switch
        {
            TopBarThemeMode.Light => ElementTheme.Light,
            TopBarThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // ---- 底衬（亚克力）：本组件【不再实现材质】，只把意图转出给外部 ----
        //
        // 为什么删：Reactor 里挂 AcrylicBrush 始终做不出正常材质。in-app 亚克力要采
        // 「本窗口 XAML 合成图」里的像素，而这棵树上能提供的采样源要么落空、要么就是
        // 它自己，结果只剩 tint 一层 —— 表现就是整栏【发白】（同一链路缺陷见
        // microsoft-ui-xaml #8118 / #12064）。叠层、补 TintLuminosityOpacity、
        // 换宿主元素、加不透明背衬都试过，全部无效。
        //
        // 新契约：本组件【只认意图、不认材质】——
        //   快照里的底衬意图 + 材质提示，与【自身实际状态】（整栏是否可见）合成后，
        //   从唯一的事件出口转出 ⇒ 由订阅方在别处实现真正的材质
        //   （宿主窗口 SystemBackdrop / 元素级 SystemBackdropElement / 原生 XAML 材质层，
        //    想怎么调参数都行，与本组件解耦）。
        //
        // 依赖只写这两个值 ⇒ 挂载时先送一条（实现方能拿到初始状态），之后仅在意图
        // 真的翻转时再送，不会每帧刷。
        // 依赖里带上 elementTheme：顶栏可以自己切一套局部主题（TopAppBarService.SetTheme），
        // 那之后材质层的 tint 也得跟着换肤，而更换动作是由【外部实现方】完成的
        // （本组件不画材质）—— 主题变了就再送一条意图，实现方据此重新选画刷。
        // 少了这个依赖就会出现"顶栏文字已经变深、亚克力还停在旧色调"的错配。
        var materialWanted = p.IsVisible && p.IsBackdropVisible;
        UseEffect(() =>
        {
            p.OnEvent?.Invoke(new TopBarEvent.Material(materialWanted, p.Material));
            return () => { };
        }, materialWanted, p.Material, elementTheme);

        // 内置动画视觉源（WinUI 自带的返回 / 搜索动画）；
        // hooks 必须在 Render 顶层无条件调用，不能放进会按条件执行的构建函数里
        var backSource = UseMemo(() => new AnimatedBackVisualSource(), 1);
        var findSource = UseMemo(() => new AnimatedFindVisualSource(), 2);

        // 【程序注入文本】后需要把光标（脱字符）落到末尾的一次性标记（见 PlaceCaretAtEndIfPending）。
        // 只在"右键粘贴到搜索"这类由代码写入 query 的路径置位：那时 TextBox 处于未聚焦状态，
        // WinUI 写入 Text 会把 SelectionStart 归零，随后的 Programmatic 聚焦沿用该值，
        // 光标停在开头，用户继续输入会插到最前面（实测粘贴 hello 后敲 X 得到 "Xhello"）。
        // 用户自己点击进入搜索时不置位，不干预其光标位置。
        var caretToEndOnFocus = UseRef(false);

        // 搜索输入框挂载后自动聚焦（扩展方法需以 this. 显式调用）
        var (inputRef, requestFocus) = this.UseElementFocus(FocusState.Programmatic);
        UseEffect(() =>
        {
            if (searching)
            {
                requestFocus();
                // 兜底：若聚焦时 GotFocus 未走（例如 requestFocus 是异步派发、或输入框早已聚焦），
                // 这里再收一次。与 GotFocus 里的调用共用同一个一次性标记，最多只生效一次。
                PlaceCaretAtEndIfPending();
            }
        }, searching);

        // 标题按钮（动画源）的宿主引用 + 悬停态；标题/输入框直线形变（共享元素动画）的 key。
        // 标题与输入框都【常驻挂载】，只切透明度/命中测试，以便状态切换前手动
        // PrepareToAnimate、提交后以 DirectConnectedAnimationConfiguration 直线 TryStart。
        const string SearchMorphKey = "TopBar.TitleSearch";
        // 退出搜索时的中间过渡态停留时长（ms）：输入框先失焦回到【静置样式】并保持一拍，
        // 再以该样式定格快照做直线形变——避免“聚焦态（强调色边框+光标）”直接形变为标题
        const int ExitSettleMs = 200;
        // 【进入搜索没有前导期】：直接在 Click 回调栈内同步提交（见 EnterSearch）。
        // 旧实现有一段"前导静置"（源端先稳定在聚焦外观再定格快照），实测它整段都是
        // 【没有视觉位移的干等】，唯一还服务的目标是"让动作按钮先淡完"——
        // 而那条淡出与形变是串行的，等于用总时长换一个装饰性淡出。
        // 去掉后：松手即可见形变起步；动作按钮卸载即消失，改由常驻的【幽灵层】
        // 在原地把它们淡出（与形变并行，不占用时间）——见 ghostActions。
        // 标题文本交换（旧文淡出/新文淡入）与铬层淡出时长（ms）
        const int TitleSwapMs = 150;
        // 换字淡入前等待【文本就绪 + 几何收敛】的最大帧数（约 500ms；超时应直接显示而非继续等）
        // 注：只等文本，不等图标 —— 图标格尺寸恒为 16×16（见 TitleIconSize），
        // 而位图解码是异步的，等它等于让标题显隐去等网络/磁盘 IO（详见 IsTitleContentReady）。
        const int TitleReadyMaxFrames = 30;
        // 周围动作按钮淡入淡出时长（ms）。
        // 进入方向：真实按钮组在提交那一帧即被卸载，淡出交给并行的幽灵层（同值）；
        // 退出方向：动作按钮在形变启动帧以本值淡入。
        const int ButtonsFadeMs = 100;
        // 标题按钮边框厚度：与官方 TextControlBorderThemeThickness 对齐，未聚焦（含悬停）
        // 为 1；聚焦（= 我们的 Pressed）底边加粗到 2。见 titleButton 处注释。
        var ChromeBorderNormal = new Thickness(1);
        var ChromeBorderFocused = new Thickness(1, 1, 1, 2);
        // 「是否处于按下」的可变盒子。用数组而非 UseState：按下不该触发重渲染
        //（会打断 morph / ConnectedAnimation），只要一个跨渲染存活的标志位即可。
        var chromePressed = UseMemo(() => new bool[1], 0);
        // 已挂过指针订阅的按钮实例。AddHandler **不去重**（实测：同实例同委托每次调用都
        // 会再加一条，日志里一次按下能触发 25→60→72 次，随渲染次数单调增长），而 .Set()
        // 每次渲染都执行 —— 所以必须自己按元素实例做一次门控，否则订阅无限累积。
        var chromeHooked = UseRef<Button?>(null);
        // 关键：ref 必须在多次渲染间【稳定】。元素常驻挂载时，Reactor 只在原生元素
        // 初次挂载时给 ref 赋 Current；若每次 render 都 new ElementRef()，退出搜索
        // 那次渲染里的新 ref.Current 永远为 null → GetAnimation 拿到已登记动画却
        // 无法 TryStart → 源快照冻结在所有 UI 之上，3 秒后才被系统丢弃（重合残影）。
        var titleRef = UseMemo(() => new ElementRef(), 0);
        // 标题内容（图标+文本整体）的原生引用：换字淡入淡出的动画目标——
        // 动画必须落在“包含图标的整体”上，否则换字时文字淡出、图标钉在原地
        var titleSwapRef = UseMemo(() => new ElementRef(), 3);
        // 换字淡入的【就绪判据】只确认文本，只看 titleSwapRef（外层包装）不够：
        // 它常驻挂载、不随内容重建，里面的文本却可能是旧内容、或刚挂载还没测量
        // （ActualWidth=0）。此时启动淡入等于把动画打在"还没画完"的内容上。
        var titleTextRef = UseMemo(() => new ElementRef(), 5);
        // 图标格引用：只用于几何快照（SnapshotTitleGeom）判布局收敛，【不参与就绪判据】
        // —— 图标格是写死的 16×16，它的尺寸不再是"内容有没有到位"的信号，
        // 而位图解码是异步的，拿它当判据会让淡入去等解码（见 IsTitleContentReady）。
        var titleIconRef = UseMemo(() => new ElementRef(), 6);
        // 所有仍在等待的淡入，各自的取消委托。
        // 必须是【列表】而不是单一槽位：显隐等待与换字等待会同时存在，
        // 单一槽位会让后一轮把前一轮取消掉（实测整块标题因此永远停在透明态）。
        var activeWaits = UseRef<List<Action>?>(null);
        // 已换成新内容、正等待"就绪后淡入"的标记。淡入由【渲染后的 effect】触发，
        // 而不是在换内容的那一行里立刻启动 —— 那时新内容还没提交到原生树，
        // 轮询会在同一帧内空转到底（实测 30 帧 23ms 就跑完，永远等不到）。
        var pendingFadeIn = UseRef(false);
        // ShowCenter 显隐淡入淡出的包装层引用：不直接动 titleHost（它是搜索形变的源元素，
        // MorphEffect 会直接写它的 Opacity），动画落在外层包装上互不干扰
        var titlePresenceRef = UseMemo(() => new ElementRef(), 4);
        // ShowCenter 淡入淡出 Storyboard（可被下一次显隐变化打断）
        var titlePresenceSb = UseRef<Microsoft.UI.Xaml.Media.Animation.Storyboard?>(null);
        // 退出形变静置期的单次定时器【必须由 ref 持有强引用】：
        // DispatcherQueueTimer 是 WinRT 投影对象，方法内局部变量在 GC 后会被提前释放，
        // 定时器静默消失、Tick 永不到达（官方示例同样存为字段）。
        // 注：进入方向已改为 Click 栈内同步提交，不再需要定时器（见 EnterSearch）。
        var morphTimer = UseRef<DispatcherQueueTimer?>(null);
        // 退出两阶段编排期间为 true：阻止重复触发退出/提交
        var exitPending = UseRef(false);
        // 进入前导编排期间为 true：阻止重复触发进入
        var enterPending = UseRef(false);
        // 退出形变【播放期间】为 true：铬层锁定悬停外观，任何复位都要被重新夺回；
        // 动画 Completed（或兜底显形）后先置 false 才能真正退回 Normal。
        var exitHolding = UseRef(false);
        // 右缘搜索按钮的放大镜图标引用：在指针事件回调里【直接】改 AnimatedIcon 的
        // state，避免用状态重渲染——在 PointerEntered/Exited 里 setState 会触发重渲染，
        // 干扰 WinUI 对 Button 自身 PointerOver 视觉状态的管理（实测会卡在悬停态）。
        var searchIconRef = UseMemo(() => new ElementRef(), 0);
        // 搜索按钮本身的引用：进入搜索时用 VisualStateManager.GoToState 强制重置
        // 按钮的 CommonStates（Normal/PointerOver/Pressed），避免“退出时 IsHitTestVisible=false
        // → 再进入时 true，WinUI 不重发 PointerEntered”导致的 PointerOver 状态残留。
        var searchBtnRef = UseMemo(() => new ElementRef(), 0);
        // 标题按钮本身的引用：① 样式实例随主题重建后要重新赋给已挂载的按钮；
        // ② 进入/退出搜索时用 GoToState 强制复位 CommonStates（与搜索按钮同一理由）。
        // 声明点必须在所有引用它的局部函数之前（编译器按调用点做确定性赋值分析）。
        var titleBtnRef = UseMemo(() => new ElementRef(), 0);
        // 标题按钮的【指针按下】必须走 AddHandler(handledEventsToo: true)：
        // ButtonBase.OnPointerPressed 会把事件标成 Handled，普通 += 订阅（Reactor 的
        // .OnPointerPressed 就是这种）根本收不到按下；而 Released/Exited 没被标，所以
        // 之前能看到“松开/移出”的日志、却永远看不到“按下”——表现就是按下态不生效。
        //
        // 委托实例必须缓存（UseMemo）：AddHandler **不按委托去重**，每次 render 传新 lambda
        // 只会让订阅越积越多。顺序必须与下面 AddHandler 那几行一致：
        //   [0]=Pressed [1]=Released [2]=Exited [3]=Canceled
        var chromePointerHandlers = UseMemo(() => new[]
        {
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => SetTitlePressed(true)),
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => SetTitlePressed(false)),
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => SetTitlePressed(false)),
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => SetTitlePressed(false)),
        }, 8);
        // 形变终点的可见性由【动画真正启动】控制：状态提交后终点先保持不可见，
        // TryStart 成功（同一调度帧内显形+启动）才显示——否则“提交渲染 → TryStart”
        // 之间有 1-2 帧终点已完全可见而动画未启动，表现为展开动画开头闪一下终点。
        var (titleShown, setTitleShown) = UseState(true);
        var (boxShown, setBoxShown) = UseState(false);
        // 周围动作按钮当前是否可见：进入前导期淡出（仍挂载占位），退出形变启动帧淡入
        var (actionsShown, setActionsShown) = UseState(true);
        // 右缘搜索按钮是否可见：挂载即 0，进入形变启动帧淡入；退出静置期先淡出
        var (searchBtnShown, setSearchBtnShown) = UseState(false);
        // 退出搜索的静置期（定格快照之前）：动作按钮以 0 透明度【提前挂载占位】，
        // 让 barMetrics/titleHost 在 PrepareToAnimate 之前收敛到普通态终态几何。
        var (exitSettling, setExitSettling) = UseState(false);
        // 搜索按钮释放延迟定时器（必须 ref 强引用，否则 GC 回收后 Tick 不到达）
        var releaseBtnTimer = UseRef<DispatcherQueueTimer?>(null);
        // 标题宿主实测宽（入状态）：定位 clamp 必须用与渲染同步的宽度——
        // 若在 Render 里直接读 ref.ActualWidth，标题文本变化后无状态变更、
        // 不触发重渲染，margin 将永远滞后一拍（错位/压按钮的根因）
        var (titleMeasuredW, setTitleMeasuredW) = UseState(0d);

        // ---- 按钮挂载/卸载 ⇒ 标题几何补间（长标题的位移与截断变化都要动起来） ----
        // 左右按钮组挂载/卸载会改变左右"禁区"，长标题的【左缘】与【可用宽度（截断）】
        // 都要跟着变；直接把新值写进 Margin/MaxWidth 会瞬跳一下。这里让渲染用的
        // 值由补间驱动：逐帧把当前值朝目标值指数逼近，收敛后落到终值。
        // 为什么不用 Storyboard/变换：① 标题位置是 Margin + MaxWidth 的布局结果，
        // Margin（Thickness）无法用 DoubleAnimation 动画；② titleHost 是搜索形变的
        // 源元素，往它或它的子树上挂变换/过渡会与 ConnectedAnimation 抢 RenderTransform
        // （本文件已实测抛 UnauthorizedAccessException）。补间只改布局值，零冲突。
        // 同一个定时器同时补间【两侧间距】与【标题几何】，两套值严格同帧推进，
        // 不会出现"行先收紧、标题后跟上"的错位。
        const double TitleShiftAlpha = 0.28;  // 每帧向目标推进的比例（16ms/帧 ⇒ 约 250ms 收敛）
        const double TitleShiftEps = 0.5;     // 收敛阈值（px）
        bool NearTween(double a, double b) => Math.Abs(a - b) < TitleShiftEps;
        var (shiftLeft, setShiftLeft) = UseState(0d);
        var (shiftSpan, setShiftSpan) = UseState(0d);
        // 两侧间距的【渲染用值】（目标来自 props.LeftPadding / RightPadding）
        var (padLeft, setPadLeft) = UseState(p.LeftPadding);
        var (padRight, setPadRight) = UseState(p.RightPadding);
        // 补间是否已可用：首帧 / 不可见期渲染直接取目标值，避免"从 0 滑进来"
        var shiftReady = UseRef(false);
        // 标题几何是否【允许】补间：搜索态 / 尚未测量时取终值（位置由形变编排决定）
        var shiftGeoLive = UseRef(false);
        // 逐帧定时器（WinRT 投影对象，必须 ref 强引用，否则 GC 后 Tick 不到达）
        var shiftTimer = UseRef<DispatcherQueueTimer?>(null);
        // 目标与当前值也存 ref：Tick 要读【最新】目标（动画期间目标会随实测宽度漂移）
        var shiftTarget = UseRef((Left: 0d, Span: 0d));
        var shiftCur = UseRef((Left: 0d, Span: 0d));
        var padTarget = UseRef((L: 0d, R: 0d));
        var padCur = UseRef((L: 0d, R: 0d));


        // ---- 居中文本更新动画：外部改 CenterText 时 旧文本淡出 → 透明时换字 → 新文本淡入 ----
        // 标题当前在栏内是否可见（ShowCenter=false / 搜索态都算不可见）。
        // 它被下面的换字动画用做【是否值得动画】的判据：切页常常是
        // 甲页(有标题) → 乙页(无标题，标题透明) → 丙页(有标题)，中途经过透明态时
        // Storyboard 的 Completed 可能不回调（WinUI 对不可见元素上的动画不保证派发），
        // 换字就会被永久卡住 —— 表现为“切了页标题还是上一页的字”。
        var titleInBar = p.ShowCenter && !searching;
        var (displayedTitle, setDisplayedTitle) = UseState(p.CenterText);
        // 图标与文本【同步】参与换字：淡出的是旧标题整体（旧图标+旧文本），淡入的是新标题整体。
        // 若图标直接跟 props 走，淡出还没结束图标就已经换成新的了，淡入也就没什么可等的。
        var (displayedIcon, setDisplayedIcon) = UseState(p.CenterIcon);
        // 标题整块的显隐【本地值】：ShowCenter 转显示时先保持 0，等内容就绪后才提交 1 并
        // 同时启动淡入。若让本地值直接跟 p.ShowCenter 走而动画延后几帧启动，元素会先
        // 被渲染成"完全显示"，接着动画 From=0 又把它拉回 0 再淡入 —— 就是那一下闪烁。
        var (presenceShown, setPresenceShown) = UseState(p.ShowCenter);
        var (titleRevealed, setTitleRevealed) = UseState(true);

        var titleSwapSb = UseRef<Microsoft.UI.Xaml.Media.Animation.Storyboard?>(null);
        // 换字兜底定时器（必须 ref 强引用，否则 GC 回收后 Tick 不到达）
        var swapFallbackTimer = UseRef<DispatcherQueueTimer?>(null);
        // 显式走 Func<Action> 重载（带 cleanup）；标题未变时返回空清理
        UseEffect(() => TitleSwapEffect(p.CenterText, p.CenterIcon, displayedTitle, displayedIcon, titleInBar),
            p.CenterText, p.CenterIcon);

        // ---- 换字的后半程：新内容【已提交到原生树】后才可能淡入 ----
        // 依赖 displayedTitle/displayedIcon：effect 在 Reactor 提交渲染之后运行，
        // 因此这里读到的原生元素已经是新内容 —— 这才是"就绪"的可靠起点，
        // 剩下的只是等布局收敛（几何连续两帧不变）。
        UseEffect(() => TitleRevealEffect(displayedTitle, displayedIcon), displayedTitle, displayedIcon);

        // ---- ShowCenter 显隐动画：淡入/淡出（动画目标在外层包装上，见 titlePresenceRef）----
        UseEffect(() => TitlePresenceEffect(p.ShowCenter), p.ShowCenter);


        // ---- 配置中途取消订阅搜索 / 切到不可搜索的页：若正处于搜索态则自动退出 ----
        // 否则输入框会悬在“没有任何入口能关掉”的状态（标题已不可点、Esc 也无处可去）。
        // enterPending 期间先不动作：等 searching 真正置 true 后本 effect 会随依赖重跑再退出。
        UseEffect(() =>
        {
            if (!p.Searchable && searching && !exitPending.Current && !enterPending.Current)
            {
                ExitSearch();
            }
        }, p.Searchable, searching);

        // ---- 受控回灌：让 TopBarCenter.Search.Text 真的能管到输入框里的内容 ----
        // 契约注释写的是「Text 同时是标题文本与搜索框里的当前查询词（受控）」，但 query 是
        // 本组件内部 state，若不回灌，受控就只剩"出去"这一半：
        //   搜 "abc" → 提交 → 抛 SearchExited → 转接层照规矩重发 Search(Text: "")
        //   ⇒ 标题变了，输入框里还留着 abc，下次点开就是旧词。
        //
        // 闸门是 !searching：搜索进行中用户在敲字，那时回灌会抢光标、吞击键（与
        // SanitizeQuery 那条注释里"写入值必须恒等于回读值"是同一类的坑）。
        // 退出搜索后本 effect 会随依赖重跑一次，正好把外部最新的 Text 接进来。
        UseEffect(() =>
        {
            if (searching)
            {
                return () => { };
            }
            // 不可搜索的居中形态下 CenterText 是标题，不是查询词 —— 别把标题灌进输入框
            var incoming = p.Searchable ? SanitizeQuery(p.CenterText) : string.Empty;
            if (!string.Equals(query, incoming, StringComparison.Ordinal))
            {
                setQuery(incoming);
            }
            return () => { };
        }, p.CenterText, p.Searchable, searching);

        // 注：旧实现里还有一条「换页自动退出搜索」（靠 props.PageKey 判页身份）。
        // 新契约下顶栏是纯函数，它不该知道"页"这个概念 —— 换页由转接层重发快照表达：
        //   新页不想要搜索 ⇒ 下发 Center=Title ⇒ 上面这条 effect 退出；
        //   新页也要搜索   ⇒ 下发 Center=Search(Text="") ⇒ 搜索框带着空文本留着，正合预期。
        // 两条路径都不需要顶栏认识"页"。


        // ---- 绝对居中布局测量 ----
        // 标题相对【整条 Bar】居中（Material3 CenterAlignedTopAppBar 模式），
        // 不随左右按钮数量差异偏移；宽度由标题内容决定，仅在内容超出
        // 对称安全区时才允许向较空一侧平移。需要实测 整栏/左组/右组 三个宽度。
        var barRef = UseMemo(() => new ElementRef(), 0);
        var leftGroupRef = UseMemo(() => new ElementRef(), 0);
        var rightGroupRef = UseMemo(() => new ElementRef(), 0);
        var (barMetrics, setBarMetrics) = UseState((Bar: 0d, Left: 0d, Right: 0d));
        void UpdateMetrics()
        {
            var bar = barRef.Current?.ActualWidth ?? 0;
            var left = leftGroupRef.Current?.ActualWidth ?? 0;
            var right = rightGroupRef.Current?.ActualWidth ?? 0;
            // 相等不更新，避免 SizeChanged→重渲染→SizeChanged 抖动循环
            if (Math.Abs(bar - barMetrics.Bar) > 0.1
                || Math.Abs(left - barMetrics.Left) > 0.1
                || Math.Abs(right - barMetrics.Right) > 0.1)
            {
                setBarMetrics((bar, left, right));
            }
        }

        // 状态切换提交后启动直线共享元素动画：
        //  · 几何稳定门控——提交当帧按钮挂载/卸载、FlexRow 重排，barMetrics 经
        //    SizeChanged→setState→重渲染回灌（晚 1-2 帧）；逐帧轮询五个实测宽度，
        //    连续两帧变化 ≤0.5px 才 TryStart（官方 TryStartConnectedAnimationAsync 语义：
        //    目标容器创建/布局完成后再启动），最多等 MorphMaxFrames；
        //  · cleanup 用 cancelled 作废上一轮轮询链，快速进/出时旧链不污染新状态；
        //  · 退出方向 TryStart 成功后按 DefaultDuration 调度铬层淡出。
        UseEffect(() => MorphEffect(searching), searching);
        const int MorphMaxFrames = 8;

        // 动作按钮是否挂载到布局行：普通态挂载；搜索态卸载；退出静置期【提前挂载】
        // （0 透明度占位，让实测宽度/标题禁区在定格快照前收敛到普通态终态）。
        var leftVisible = p.LeftActions ?? Array.Empty<TopBarAction>();
        var rightVisible = p.RightActions ?? Array.Empty<TopBarAction>();

        // ---- 动作按钮的「幽灵」层（进入搜索时接手动作按钮的淡出）----
        // 进入搜索时动作按钮必须【立即卸载】：它一旦继续占位，搜索框（形变终点）的左边界
        // 就要等淡出结束才能让出来 —— 终点几何在动画中途跳变，形变会明显抖一下。
        // 所以占位立即交出去，视觉上改由这份【常驻只读副本】在原地把按钮淡出：
        // 淡出与 ConnectedAnimation 并行，不额外占用时间；副本 IsHitTestVisible=false 且挂在
        // 覆盖层里（不参与 Flex 测量），对布局零影响。
        //
        // 为什么用 Storyboard 而不是 Reactor 的 .Animate / XAML OpacityTransition：
        //   · .Animate 是「值变化即播」，幽灵从 0 变 1 那一拍也会播淡入 —— 变成淡入再淡出；
        //   · OpacityTransition 会禁用元素的 RenderTransform（本项目踩过，见 FadeWithBar 注释）；
        //   · Storyboard 可以「先瞬时置 1、再播 1→0」，且不碰 RenderTransform。
        //
        // ⚠️ 声明位置：必须早于任何【间接】调用点。C# 对局部函数捕获的局部变量按调用链做
        // 确定赋值分析 —— PasteToSearch（789 行的 lambda）→ EnterSearch → PlayGhostFade 也算，
        // 声明晚于它会直接 CS0165。
        var ghostActionsRef = UseMemo(() => new ElementRef(), 0);
        var ghostFadeSb = UseRef<Storyboard?>(null);
        void PlayGhostFade()
        {
            // 左组没有自定义动作按钮时没有可淡出的东西
            if (ghostActionsRef.Current is not FrameworkElement el || leftVisible.Count == 0)
            {
                return;
            }
            // 上一次动画若还在跑：先停掉，否则 HoldEnd 会把 Opacity 钉死在 0
            ghostFadeSb.Current?.Stop();
            el.Opacity = 1;
            var da = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(ButtonsFadeMs),
                FillBehavior = FillBehavior.HoldEnd, // 停在 0，不回弹（本地值本来也是 0）
            };
            Storyboard.SetTarget(da, el);
            Storyboard.SetTargetProperty(da, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(da);
            ghostFadeSb.Current = sb;
            sb.Begin();
        }

        var leftActionsMounted = p.ShowActions && (!searching || exitSettling) && leftVisible.Count > 0;
        var rightActionsMounted = p.ShowActions && (!searching || exitSettling) && rightVisible.Count > 0;

        // ---- 左组：返回按钮（始终显示，顶栏隐藏时也保留）+ 自定义按钮（随动作淡入淡出） ----
        var leftActionElements = leftActionsMounted
            ? leftVisible.Select(BuildActionButton).ToArray()
            : Array.Empty<Element>();
        var leftActionWrap = FadeWithBar(
                Border(HStack(4, leftActionElements))
                    // 有按钮时与返回键保持 4px；空内容时外壳塌缩为 0，不留间距
                    .Margin(leftActionsMounted ? 4 : 0, 0),
                p.IsVisible && leftActionsMounted && actionsShown,
                ButtonsFadeMs);
        // 与中组的 8px 间距放这里（行的 ColumnGap 已置 0，避免空右组占位造成标题偏移）；
        // leftGroupRef 实测整组宽度供标题绝对居中推导
        // ⚠️ 返回按钮【只能】用 .IsVisible(false)（= Collapsed），不能改成条件渲染
        //    （ShowBack=false 时干脆不放进子元素数组）。后者实测直接把顶栏打成红屏，
        //    异常由 TopBarHost 上的 ErrorBoundary 抓到（[TopBar][render-throw]）：
        //        System.UnauthorizedAccessException: 拒绝访问。
        //        Calling RenderTransform API is not allowed on this object at this time,
        //        as this object currently has the OpacityTransition property in use.
        //          at DependencyObject.ClearValue(dp)
        //          at ElementPool.CleanElement(fe) → ElementPool.Return(element)
        //          at Reconciler.UnmountAndPool(control)
        //          at Reconciler.RemoveChildWithExitTransition(...)
        //    即：条件渲染让元素被【卸载并回收进池】，CleanElement 必调
        //    ClearValue(RenderTransform)，而元素正在用 OpacityTransition ⇒ WinUI 拒绝访问。
        //    这正是 BuildSearchButton 注释里早就警告过的那个坑。Collapsed 则不会进池。
        //    代价：Collapsed 不清 IsPointerOver，"重新出现带着悬停底色"仍需另想办法。
        var leftGroup = HStack(0, BuildBackButton().IsVisible(p.ShowBack), leftActionWrap)
            .Margin(0, 0, 8, 0)
            .Ref(leftGroupRef)
            .OnSizeChanged((_, _) => UpdateMetrics());

        // ---- 剪贴板状态 ----
        // 官方（Copy and paste 文档 “Track changes to the clipboard”）推荐的跟踪方式是
        // Clipboard.ContentChanged。桌面版只在应用处于前台时才触发，所以这里让它只负责
        // 【驱动重渲染】，真正取值仍在渲染期同步读一次：即使某次事件没送达，菜单打开时
        // 拿到的也一定是当前值，不会停留在过期的缓存上。
        var (clipboardTick, setClipboardTick) = UseState(false);
        UseEffect(() =>
        {
            void OnContentChanged(object? sender, object? args) => setClipboardTick(!clipboardTick);
            Clipboard.ContentChanged += OnContentChanged;
            return () => Clipboard.ContentChanged -= OnContentChanged;
        }, Array.Empty<object>());
        _ = clipboardTick; // 仅用于让剪贴板变化触发一次重渲染

        // 剪贴板当前是否有文本（决定“粘贴”项可用性；读取可能被其他进程锁定，失败按不可用处理）
        bool canPaste;
        try
        {
            canPaste = Clipboard.GetContent().Contains(StandardDataFormats.Text);
        }
        catch
        {
            canPaste = false;
        }
        var canCopyTitle = !string.IsNullOrEmpty(p.CenterText);

        // ---- 由实测宽度推导的布局参数（全部相对【整条 Bar】，保证标题绝对居中） ----
        // 左/右“禁区”宽度：Props 边距 + 按钮组实测宽 + 组间距（ActualWidth 不含 margin）
        var leftInset = p.LeftPadding + barMetrics.Left + 8;
        var rightInset = p.RightPadding + barMetrics.Right + (rightActionsMounted ? 8 : 0);
        var barW = barMetrics.Bar;
        // 【目标值】由实测宽度直接算出；实际参与渲染的是下面补间后的值。
        var titleSpanTarget = Math.Max(0, barW - leftInset - rightInset);
        var titlePositioned = barW > 0 && titleMeasuredW > 0;
        double titleLeftTarget = 0;
        if (titlePositioned)
        {
            // 左缘 = clamp(整栏中心 − W/2, 左禁区, 右禁区 − W)：
            // 对称时绝对居中；超宽时向较空一侧平移但绝不压住按钮
            titleLeftTarget = Math.Clamp(
                barW / 2 - titleMeasuredW / 2,
                leftInset,
                Math.Max(leftInset, barW - rightInset - titleMeasuredW));
        }
        // 渲染用值：补间已启动（且标题在栏内可见）时用补间值，否则直接取目标值
        var titleLeft = shiftReady.Current ? shiftLeft : titleLeftTarget;
        var titleSpan = shiftReady.Current ? shiftSpan : titleSpanTarget;

        // ---- 标题几何补间的驱动 ----
        // 依赖里带 titleInBar：进入/退出搜索时标题几何由形变编排与快照决定，必须
        // 【瞬间】取终值（否则快照落在旧位置、或与 ConnectedAnimation 抢位置），
        // 所以不可见期间直接复位成"未就绪"，下次可见时重新以终值起步。
        // 两侧间距的【目标值】：就是 props 上的边距（TopBarChrome.Padding），
        // 页间切换会变（例如 12 → 24），渲染用补间值 padLeft / padRight。
        var padLeftTarget = p.LeftPadding;
        var padRightTarget = p.RightPadding;
        UseEffect(() =>
        {
            shiftTarget.Current = (titleLeftTarget, titleSpanTarget);
            padTarget.Current = (padLeftTarget, padRightTarget);

            // 标题几何在不可见 / 未测量时【取终值不补间】：进入与退出搜索时位置由
            // 形变编排与定格快照决定，补间会与 ConnectedAnimation 抢位置。
            var geoLive = titleInBar && titlePositioned;
            if (!geoLive)
            {
                if (shiftGeoLive.Current)
                {
                    shiftGeoLive.Current = false;
                    shiftCur.Current = (titleLeftTarget, titleSpanTarget);
                    setShiftLeft(titleLeftTarget);
                    setShiftSpan(titleSpanTarget);
                }
            }
            else
            {
                shiftGeoLive.Current = true;
            }

            if (!shiftReady.Current)
            {
                // 首次可用：直接落在目标值上，不从 0 滑入
                shiftReady.Current = true;
                shiftCur.Current = (titleLeftTarget, titleSpanTarget);
                padCur.Current = (padLeftTarget, padRightTarget);
                setShiftLeft(titleLeftTarget);
                setShiftSpan(titleSpanTarget);
                setPadLeft(padLeftTarget);
                setPadRight(padRightTarget);
                return;
            }
            // 已全部到位：不用动（也避免实测宽度抖动引发无意义的补间）
            if (NearTween(titleLeftTarget, shiftCur.Current.Left)
                && NearTween(titleSpanTarget, shiftCur.Current.Span)
                && NearTween(padLeftTarget, padCur.Current.L)
                && NearTween(padRightTarget, padCur.Current.R))
            {
                return;
            }
            StartTitleShift();
        }, titleLeftTarget, titleSpanTarget, titleInBar, titlePositioned, padLeftTarget, padRightTarget);

        // 搜索框在搜索态的边距用【确定性常量】而非异步的 barMetrics：
        // 搜索态左组恒为 40px 返回按钮 + 8px 组距，右缘恒为 8px 组距 + 40px 搜索按钮。
        // 这样 setSearching(true) 提交的【第一帧布局】搜索框即在终态位置。
        var boxLeftInset = padLeft + 40 + 8;
        var boxRightInset = padRight + 8 + 40;

        // 搜索框外观的铬层已【并入标题按钮自身】：见 SearchChrome ——
        // 既不重写 ControlTemplate，也不去改按钮自己的 Background，而是走 WinUI 官方
        // 的【轻量样式】(lightweight styling)：把官方 SubtleButtonStyle 在 CommonStates
        // 里使用的资源键，在按钮的 Resources 上覆盖掉。官方 Storyboard 照常运行，只是
        // 取到的颜色变成了搜索框语义色；模板结构、过渡动画一概不动。
        // 不可搜索时（Searchable=false）：标题宿主关掉命中测试（见 titleHost），
        // 指针进不来 ⇒ 按钮恒停在 Normal 态（透明），与「纯文本」观感一致。

        // 标题文本：单行省略；换字的淡入淡出落在 titleBody 包装层（图标与文字一体参与）
        var titleText = TextBlock(displayedTitle)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            // 就绪判据用（见 IsTitleContentReady）：换字淡入前确认文本已换成新值并完成测量
            .Ref(titleTextRef);
        // 注：WinUI 3（Windows App SDK 2.5）的 TextBlock 没有 ForegroundTransition，
        // 文字/图标前景色只能随 RequestedTheme 瞬时切换，没有插值的余地。
        // 旧实现为此把 RequestedTheme 延后到背景渐变过半再提交，那段延后已随亚克力
        // 一起删除（本组件不再有任何背景渐变需要对齐），现在主题一变就同帧提交。

        // 标题是只读文本，右键菜单只提供【复制 + 粘贴】：按官方 text box 文档的命令-状态表，
        // “剪切”只在存在可删除的选中文本时出现，标题不可编辑，给出它会误导用户。
        // 菜单仍用 MenuFlyout：官方建议含 复制/剪切/粘贴 这类通用命令时改用 CommandBarFlyout
        //（把它们作为 primary commands 排成一行水平图标），但 Reactor 的
        // CommandBarFlyout(target, ...) 是【点击 target 触发】的装饰器，会与标题按钮
        // “左键进入搜索”的主行为冲突；挂 ContextFlyout 才是正确的右键语义
        //（Shift+F10 / 菜单键由 WinUI 内建支持，无需额外处理）。
        // 两个命令用 Command 声明：CanExecute 只写一处，绑定到的菜单项自动灰显。
        var copyTitleCommand = new Command
        {
            Label = "复制",
            Icon = SymbolIcon("Copy"),
            Execute = CopyTitle,
            CanExecute = canCopyTitle,
        };
        var pasteToSearchCommand = new Command
        {
            Label = "粘贴",
            Icon = SymbolIcon("Paste"),
            // PasteToSearch 是 async void，包一层再作为 Action 传入
            Execute = () => PasteToSearch(),
            CanExecute = canPaste,
        };

        // ---- 标题图标：两个【常驻】的原生图标（字形 / 位图各一），只改属性与可见性 ----
        // 为什么不再按 IconData 现场创建图标元素（旧 BuildCenterIcon）：
        // Reactor 的 Core.IconElement 是【按 IconData 现场解析并创建原生
        // Microsoft.UI.Xaml.Controls.IconElement 子类】的装饰元素；顶栏在几何补间期间
        // 逐帧重渲染（StartTitleShift，16ms/帧），加上 SizeChanged / setState，
        // 原生图标就被逐帧销毁重建。字形那支重建是同步的、肉眼看不出；位图那支
        //（ImageIcon + BitmapImage(Uri)）解码是异步的，解出来之前控件是空的 —— 闪。
        //
        // 现在：FontIcon 与 ImageIcon 【同时常驻在树上】，切图标只切 IsVisible 并改
        // Glyph / Source，原生控件自始至终只有一个实例，永不重建。
        //
        // 顺带：图标解析走纯函数 ResolveCenterIcon（见类内），Render 路径里不再有任何
        // "按 IconData 挑控件类型" 的分支。
        var iconPlan = ResolveCenterIcon(displayedIcon);

        // 位图的 ImageSource 按 Uri 缓存到【本组件】作用域（不是全局缓存）：
        // 每帧 new BitmapImage 会让 ImageIcon 重新解码一次，又回到"空一拍"的闪。
        // Uri 不变 ⇒ 拿到的是同一个实例 ⇒ 写进 ImageIcon.Source 是同值写入，不触发重新解码。
        var iconBitmap = UseMemo(
            () => iconPlan.Source is { } uri ? new BitmapImage(uri) : null,
            iconPlan.Source);

        // 字形图标：FontIcon 自撑自然尺寸（= 字号），不写死 16×16，见 TitleIconFontSize。
        // 非字形形态时传 null ⇒ 描述项的 shouldWrite 为假 ⇒ 【不写】，旧的 Glyph 留着
        //（此时整格是 Collapsed，看不见）。
        var titleFontIcon = FontIconElement.FontIcon(
                glyph: iconPlan.Kind == CenterIconKind.Glyph ? iconPlan.Glyph : null,
                fontSize: TitleIconFontSize)
            .Center()
            .IsVisible(iconPlan.Kind == CenterIconKind.Glyph)
            .WithKey("title-font-icon");

        // 位图图标：ImageIcon 按【位图自然尺寸】渲染（.ico 甚至会取最大帧，实测 128px），
        // 顶栏这一格只有 16px —— 必须显式压到 16×16，否则会把标题顶开、行高变形。
        var titleImageIcon = ImageIconElement.ImageIcon(
                source: iconPlan.Kind == CenterIconKind.Bitmap ? iconBitmap : null)
            .Width(TitleIconSize)
            .Height(TitleIconSize)
            .Center()
            .IsVisible(iconPlan.Kind == CenterIconKind.Bitmap)
            .WithKey("title-image-icon");

        // 标题内容：可选图标 + 文本。
        // 图标+文本用【Auto/Star 两列 Grid】而非 HStack（StackPanel）：StackPanel 以自然
        // 宽度测量文本、不收缩，长标题会整体溢出宿主被两端裁剪（图标被切、无省略号）；
        // Star 列让文本在宽度不足时正确截断出省略号。
        // 外层 Grid 承载换字淡入淡出的 Opacity（动画目标含图标，见 TitleSwapEffect）。
        // 注意：这里用 displayedIcon（换字状态）而不是 p.CenterIcon（props），
        // 图标才会和文本一起在淡出结束后切换、一起作为新内容被淡入。
        // 结构恒定为【图标列 + 文本列】：无图标时图标列整格 Collapsed（宽 0），
        // 文本不留左边距。两个图标元素【始终在树上】，结构永不变化 ⇒ Reactor 不会
        // 因为"图标类型变了"重建子树。
        var titleBodyInner = Grid(
                new[] { GridSize.Auto, GridSize.Star() },
                Array.Empty<GridSize>(),
                // 图标格：边长【写死 16×16】，与里面位图的加载/解码状态无关。
                // 位图没解完时自然尺寸是 0，若让它撑开这一格，图标列会在 0 ↔ 16 之间跳，
                // 带着标题文本重排（titleHost.OnSizeChanged → setTitleMeasuredW
                // → 又一轮渲染 + 几何补间），形成"重建→解码→尺寸变→再渲染"的正反馈。
                Grid(
                    Array.Empty<GridSize>(),
                    Array.Empty<GridSize>(),
                    titleFontIcon,
                    titleImageIcon)
                    .Width(TitleIconSize)
                    .Height(TitleIconSize)
                    .IsVisible(iconPlan.Kind != CenterIconKind.None)
                    .Ref(titleIconRef)
                    .HAlign(HorizontalAlignment.Left)
                    .Grid(row: 0, column: 0),
                titleText
                    .Margin(iconPlan.Kind == CenterIconKind.None ? 0 : 6, 0, 0, 0)
                    .Grid(row: 0, column: 1));
        var titleBody = Grid(
                Array.Empty<GridSize>(),
                Array.Empty<GridSize>(),
                titleBodyInner)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Opacity(titleRevealed ? 1 : 0)
            .Ref(titleSwapRef);

        // 标题按钮：填满标题宿主；左键进入搜索，右键弹出 复制/粘贴 菜单。
        // 未订阅搜索的页面：标题不可点击（宿主层也已关掉命中测试），只作为静态文本存在。
        Action onTitleClick = p.Searchable ? EnterSearch : static () => { };

        // REACTOR_POOL_001 会报「BorderThickness 在池回收时被重置，.Set 写的值会丢」——
        // 这是真的，但在这里无害，故就地抑制（不能用它建议的 .BorderThickness(常量)，原因见下）：
        //   ① 重置后的默认值就是未按下态（1px），与 chromePressed=false 一致，不会张冠李戴；
        //   ② 元素回到树上时 .Set 会再跑一次，按当前标志重新写回；
        //   ③ 真正按下时指针事件里也会即时改一次，不依赖渲染时机。
        // 即：厚度的唯一真相源是 chromePressed 标志，池重置只是清了缓存，不是把状态改错。
        // 该警告报在【元素根节点】这一行，所以 pragma 必须罩在语句开头，不能只罩 .Set 那一段。
#pragma warning disable REACTOR_POOL_001
        var titleButton = Button(titleBody, onTitleClick)
            .Ref(titleBtnRef)
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
            //   官方 TextControlBorderThemeThickness        = 1        （未聚焦/悬停）
            //   官方 TextControlBorderThemeThicknessFocused = 1,1,1,2  （聚焦：底边加粗）
            // 即「底边加粗到 2px」是**聚焦独有**的特征，悬停仍是 1px。之前把它写成恒
            // 1,1,1,2，悬停就顶着一条聚焦态的粗底边 —— 这就是「悬停底边太粗」的来源。
            // SubtleButtonStyle 的 CommonStates 只换画刷、不改厚度，官方也没给厚度留
            // 资源键（ResourceBuilder.Set 无 Thickness 重载），所以只能在指针事件里手动切。
            //
            // ⚠️ 这里【不能】写 .BorderThickness(常量)：那是 render 期修饰器，每次
            // 重渲染都会把值写回常量，把事件里刚设的 2px 冲掉 —— 按下期间只要发生
            // 任何一次渲染（例如进入搜索态），厚度就悄悄退回 1px。
            // 所以改成「render 期按当前标志统一写」：事件只负责改标志 + 即时改一次，
            // 之后无论渲染多少次，厚度都由同一个标志推出来，天然自愈。
            .Set(b =>
            {
                b.BorderThickness =
                    chromePressed[0] ? ChromeBorderFocused : ChromeBorderNormal;
                // 订阅按【元素实例】只做一次：AddHandler 不去重，.Set 却每次渲染都跑，
                // 不加门控的话一次按下会被处理几十次，且订阅随渲染次数无限增长。
                // 元素被池回收后若换了个实例回来，引用不等 ⇒ 自动重新挂上（自愈）。
                if (!ReferenceEquals(chromeHooked.Current, b))
                {
                    chromeHooked.Current = b;
                    // handledEventsToo: true —— 见 chromePointerHandlers 处注释
                    b.AddHandler(UIElement.PointerPressedEvent, chromePointerHandlers[0], true);
                    b.AddHandler(UIElement.PointerReleasedEvent, chromePointerHandlers[1], true);
                    b.AddHandler(UIElement.PointerExitedEvent, chromePointerHandlers[2], true);
                    b.AddHandler(UIElement.PointerCanceledEvent, chromePointerHandlers[3], true);
                }
#pragma warning restore REACTOR_POOL_001
            })
            // 松开 / 移出 / 取消：一律退回 1px。Released 后指针多半仍在按钮上（回到悬停），
            // 悬停就应该是 1px；Exited 与 Canceled 更是必须复位，否则会卡在加粗态。
            //
            // ⚠️ 这里【不能】用 Reactor 的 .OnPointerPressed：ButtonBase 在 OnPointerPressed
            // 里把事件标成 Handled，普通 += 订阅收不到按下。实测对照：同一按钮上
            // .OnPointerPressed 的探针一次都不触发，.OnPointerExited 的探针正常触发 ⇒
            // 按下必须走上面的 AddHandler(handledEventsToo: true)。
            // 也不监听 OnPointerCaptureLost：Button 按下时会捕获指针，捕获发生/转移期间
            // 可能抛一次 CaptureLost，把刚加粗的厚度立刻打回 1px。
            .WithContextFlyout(MenuItems(
                MenuItem(copyTitleCommand),
                MenuItem(pasteToSearchCommand)));

        // 三态换刷走【轻量样式覆盖】：官方 CommonStates 用 Storyboard 动画打在
        // ContentPresenter.Background 上，而动画优先级高于 TemplateBinding —— 所以任何
        // 去写 Button.Background 的做法（例如 .InteractionStates(background:)）在悬停/按下
        // 期间都会被官方动画整个遮蔽。实测：进 PointerOver 后 presenterBg 从我们设的
        // #80F9F9F9 变成 SubtleFillColorSecondaryBrush(#09000000)。覆盖资源键才是正路。
        titleButton = titleButton.Resources(SearchChrome.Apply);

        // 强制标题按钮回到 Normal（透明）：进入/退出搜索会切 IsHitTestVisible，
        // 命中测试切换后 WinUI 不补发 PointerExited，CommonStates 会卡在 PointerOver
        //（与搜索按钮同一手法，见 searchBtnRef 处的注释）。
        // 退出搜索走这个：形变终点是【标题】，必须以 Normal（透明铬层）定格。
        void ResetTitleButtonVisualState()
        {
            if (titleBtnRef.Current is Control titleCtl)
            {
                VisualStateManager.GoToState(titleCtl, "Normal", false);
            }
            SetTitlePressed(false);
        }

        // 退出形变的【播放期间】反向操作：把铬层锁在【悬停态】（PointerOver）。
        //
        // 为什么是悬停而不是 Normal：
        //   退出是「搜索框 → 标题」的形变。源端（搜索框）无论静置还是聚焦都有实底，
        //   而标题的 Normal 是【完全透明】的 —— 若终点以 Normal 定格，形变最后一帧
        //   等于把实底抹掉，观感是"闪一下消失"。悬停态有 50% 白底，与源端有实底这一点
        //   一致，形变全程只剩尺寸/位置在变。
        //   退出动作本身也暗示指针正落在顶栏（点返回键 / 提交），悬停正是此刻应有的态。
        //
        // 为什么不是常态：动画播完必须退回 Normal（见 MorphEffect 的 Completed），
        //   否则铬层会被永久钉在悬停态 —— 之后指针移开也不会复原。
        //
        // ⚠️ 不能只在这里压一次就完事：ButtonBase 的 UpdateVisualState 随时可能把状态
        //   打回去（与进入侧同一个坑），所以 SetTitlePressed 里还有一道 exitHolding 闸，
        //   且 TryStart 成功的同帧会再压一次（那是最后一个能改状态的点）。
        void HoldTitleChromeHover()
        {
            if (titleBtnRef.Current is Control titleCtl)
            {
                VisualStateManager.GoToState(titleCtl, "PointerOver", false);
            }
            // 直接写，不走 SetTitlePressed：后者在 exitHolding 为真时会回头调本方法
            chromePressed[0] = false;
            if (titleBtnRef.Current is Button titleBtn)
            {
                titleBtn.BorderThickness = ChromeBorderNormal;
            }
        }

        // 解除悬停锁并退回 Normal。顺序要紧：必须先清标志再复位，
        // 否则 SetTitlePressed(false) 会撞上 exitHolding 闸、又把悬停夺回来。
        void ReleaseTitleChromeHover()
        {
            exitHolding.Current = false;
            ResetTitleButtonVisualState();
        }


        // 进入搜索的前导期反向操作：把铬层【锁在按下态】（= 搜索框聚焦外观）。
        // 进入动画是从「标题」形变到「搜索框」，而搜索框一进来就是聚焦态（白底 + 强调色
        // 边框 + 底边 2px）。若前导期退回 Normal（透明），就会看到
        //   按下(白+蓝边) → 松开瞬间透明 → 150ms 后形变 → 聚焦(白+蓝边)
        // 中间那一拍是凭空闪一下。源端保持聚焦外观，两端像素一致，形变只剩尺寸/位置变化。
        void HoldTitleChromePressed()
        {
            if (titleBtnRef.Current is Control titleCtl)
            {
                VisualStateManager.GoToState(titleCtl, "Pressed", false);
            }
            // 同上：直接写，不经过 SetTitlePressed
            chromePressed[0] = true;
            if (titleBtnRef.Current is Button titleBtn)
            {
                titleBtn.BorderThickness = ChromeBorderFocused;
            }
        }

        // 厚度只能走本地值：官方没给厚度留资源键，ResourceBuilder.Set 也没有 Thickness
        // 重载，而 .InteractionStates() 只认 visual/brush、不认 layout 属性。
        // 两步走：先记标志（供 render 期 .Set 自愈），再即时改一次（不等下次渲染）。
        // 用 ref.Current 现场解析，不要在 Render 期抓快照 —— Render 里抓到的是上一帧的
        // 元素引用，闭包一旦把它捕获下来就永远过期。
        void SetTitlePressed(bool pressed)
        {
            // 进入搜索的前导期：铬层锁定聚焦外观，任何「松开/移出」都不许把它复位。
            //
            // 时序坑（已实测）：Click 是 ButtonBase 在 OnPointerReleased 的【覆写】里抛出的，
            // EnterSearch 因此先跑（实测 reset 比 raw-release 早约 1ms）；但 ButtonBase 抛完
            // Click 之后还会 UpdateVisualState 回落 PointerOver/Normal，把我们在 Click 里刚
            // 压上的 Pressed 冲掉 —— 所以只在 EnterSearch 里压一次是不够的。
            // 我们的订阅（handledEventsToo）排在那个覆写之后，正好是最后一个能改状态的
            // 位置：这里再压一次，之后直到快照定格都没有人再动它。
            if (!pressed && enterPending.Current)
            {
                HoldTitleChromePressed();
                return;
            }
            // 退出形变播放期间：任何复位都夺回成悬停态（ButtonBase 的 UpdateVisualState
            // 会在退出编排里把我们压的 PointerOver 打回 Normal，与进入侧同一个坑）
            if (!pressed && exitHolding.Current)
            {
                HoldTitleChromeHover();
                return;
            }
            chromePressed[0] = pressed;
            if (titleBtnRef.Current is Button titleBtn)
            {
                titleBtn.BorderThickness = pressed ? ChromeBorderFocused : ChromeBorderNormal;
            }
        }

        // 居中位无内容（ShowCenter=false）时：整块标题宿主透明且不参与命中测试，
        // 背景层与返回按钮照常（这正是“未配置/只有返回”那一页的样子）
        //
        // ⚠️ 这里【不能】写 p.ShowCenter：ShowCenter 转 false 时它会把内层瞬间置 0，
        // 外层包装（titleHostWrap）的淡出动画就跑了个寂寞——视觉上仍是瞬跳。
        // ShowCenter 的显隐动画统一由外层包装承担（见 titlePresenceRef / presenceShown），
        // 内层只负责【搜索形变】的可见性（进入搜索定格后置 false）。

        // 标题宿主（按钮自带搜索框铬层）：与输入框是【兄弟节点】，互不嵌套
        //（连接动画源/目标若为父子，TryStart 到祖先会冻结整棵子树）。
        var titleHost = Grid(
                Array.Empty<GridSize>(),
                Array.Empty<GridSize>(),
                titleButton)
            .Ref(titleRef)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Height(32)
            .Opacity(!searching && titleShown ? 1 : 0)
            .IsHitTestVisible(!searching && titleShown && p.Searchable)
            .OnSizeChanged((_, e) =>
            {
                // 宽度入状态驱动 clamp 定位（位置变化不影响宽度，收敛后无振荡）
                if (Math.Abs(e.NewSize.Width - titleMeasuredW) > 0.1)
                {
                    setTitleMeasuredW(e.NewSize.Width);
                }
            });
        if (titlePositioned)
        {
            titleHost = titleHost
                .HAlign(HorizontalAlignment.Left)
                .Margin(titleLeft, 0, 0, 0)
                .MaxWidth(titleSpan);
        }
        else if (barW > 0)
        {
            // 已测得整栏但标题尚未完成布局：先按可用宽居中，下一帧收敛为左缘定位
            titleHost = titleHost.MaxWidth(titleSpan);
        }

        // ShowCenter 显隐淡入淡出的包装层（铺满覆盖层，titleHost 在其中的定位不变）：
        // 不直接动 titleHost 的 Opacity —— 它是搜索形变的源元素，MorphEffect 会直接写
        // destination.Opacity，挂上动画会互相打架；动画落在这层互不干扰。
        // 本地值始终 = Reactor 写入的目标值，Storyboard FillBehavior=Stop 播完即回落。
        var titleHostWrap = Grid(
                Array.Empty<GridSize>(),
                Array.Empty<GridSize>(),
                titleHost)
            .Ref(titlePresenceRef)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            // 本地值由 presenceShown 驱动（见该状态的注释）：与淡入动画的启动同帧提交，
            // 动画用 FillBehavior=Stop 播完即回落到这个本地值，不会出现"先显示再拉回"。
            .Opacity(presenceShown ? 1 : 0)
            .IsHitTestVisible(p.ShowCenter);


        // 确认按钮的图标形态：整帧解析一次，BuildSearchButton 直接用。
        // 解析结果里的 Source / Fallback 都是【进程级缓存的单例】（见 TopBarAnimatedSources），
        // 每帧重解也恒等价，不必为它再套一层 UseMemo —— 套了反而多一个依赖项要维护。
        var acceptIcon = TopBarAnimatedSources.ResolveAccept(p.AcceptIcon);

        // 【实时文本】的上抛口：先把文本写进本组件的 state，再通知页面。
        //
        // 为什么经 DispatcherQueue 绕一圈而不是同步抛：这一步会落到页面手里，而页面
        // 大概率【同步】重发一份快照（把文本回写进 TopBarCenter.Search.Text 做成受控）。
        // 那就是在原生 TextBox 的 TextChanged 事件【进行中】驱动一轮 reconciler ——
        // 正好撞上既有那条回声抑制回路（ChangeEchoSuppressor）的地盘：光标位置、
        // 选区、甚至"下一次编辑会不会覆盖已有文本"全要看那条回路当时的计数器。
        // 挂一次 dispatcher 把它挪出事件的调用栈：代价一帧（~16ms）延迟，换来
        // 【完全不改变任何既有时序】—— 新能力不该让老路径承担风险。
        void NotifyLiveText(string text)
        {
            var raise = p.OnTextChanged;
            if (raise is null)
            {
                return;
            }

            if (inputRef.Current?.DispatcherQueue is { } queue)
            {
                queue.TryEnqueue(() => raise(text));
            }
            else
            {
                raise(text);
            }
        }

        // 搜索输入框：铺满左右按钮边界之间；非搜索态/动画未启动时透明且不参与命中测试
        // onChange 也过一遍 SanitizeQuery：框内原生粘贴 / Ctrl+V 由 WinUI 规范化后回传本就是
        // 安全值，但 IME 组字等路径仍可能带进换行，统一在入口收口。
        // 占位提示来自快照（TopBarCenter.Search.Placeholder）；没给就回落到默认文案，
        // 不再写死 —— 契约里既然有这个字段，它就得真的能改。
        var searchBox = TextBox(
                query,
                v =>
                {
                    var text = SanitizeQuery(v);
                    setQuery(text);
                    NotifyLiveText(text);
                },
                string.IsNullOrEmpty(p.CenterPlaceholder) ? DefaultSearchPlaceholder : p.CenterPlaceholder)
            .Ref(inputRef)
            .AutomationName("TopBarSearchBox")
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Height(32)
            .Margin(boxLeftInset, 0, boxRightInset, 0)
            .Opacity(searching && boxShown ? 1 : 0)
            .IsHitTestVisible(searching && boxShown)
            // 光标落末尾的【主路径】：聚焦事件在 Focus() 处理过程中同步派发，
            // 此刻文本早已提交（粘贴是先写 query、150ms 后才进入搜索），
            // 在这里设置 SelectionStart 才是"聚焦后光标位置"的最终值。
            .OnGotFocus((_, _) => PlaceCaretAtEndIfPending())
            .OnKeyDown((_, e) =>
            {
                if (e.Key == VirtualKey.Escape)
                {
                    e.Handled = true;
                    ExitSearch();
                }
                else if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    SubmitSearch();
                }
            });

        // 搜索按钮固定在覆盖层右缘（不参与 Flex 测量）：进/出搜索不改变布局行宽度，
        // 搜索框几何跨态恒定。
        // 采用【常驻挂载】而非条件渲染：若搜索按钮条件渲染，Grid 子元素数量变化会
        // 触发 Reactor 重建整棵子树，导致 ConnectedAnimation 的源/目标元素（titleHost/
        // searchBox）在 PrepareToAnimate → TryStart 之间被替换，TryStart 抛
        // "the source element is not in the element tree"。常驻挂载仅切 opacity/命中测试，
        // 子树稳定。
        var searchButton = BuildSearchButton()
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, padRight, 0);

        var ghostActions = Border(HStack(4, leftVisible.Select(BuildActionButton).ToArray()))
            // 与真实左组里的动作按钮组同位置：FlexPadding 左 = padLeft，返回按钮固定 40 宽，
            // 动作按钮组自身左右 Margin = 4 ⇒ padLeft + 40 + 4。
            .Margin(padLeft + 44, 0, 0, 0)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center)
            .Opacity(0)
            .IsHitTestVisible(false)
            .Ref(ghostActionsRef);

        // 中组覆盖层（铺满整栏，位于布局行之上）：搜索框 / 标题宿主 / 搜索按钮互为兄弟。
        // 幽灵层常驻为第 4 个兄弟 —— ⚠️ 必须常驻：Grid 子元素数量一旦随状态变化，Reactor
        // 会重建整棵子树，ConnectedAnimation 的源/目标会在 PrepareToAnimate→TryStart 之间被
        // 替换（见 searchButton 处注释）。这里只切 Opacity，子树结构恒定。
        var centerOverlay = FadeWithBar(
                Grid(
                    Array.Empty<GridSize>(),
                    Array.Empty<GridSize>(),
                    searchBox,
                    titleHostWrap,
                    searchButton,
                    ghostActions)
                    .HAlign(HorizontalAlignment.Stretch)
                    .VAlign(VerticalAlignment.Stretch),
                p.IsVisible);

        // ---- 右组：只承载普通态的自定义动作按钮（搜索按钮在覆盖层右缘） ----
        var rightActionElements = rightActionsMounted
            ? rightVisible.Select(BuildActionButton).ToArray()
            : Array.Empty<Element>();
        var rightActionWrap = FadeWithBar(
                HStack(4, rightActionElements),
                p.IsVisible && rightActionsMounted && actionsShown,
                ButtonsFadeMs);
        var rightGroup = FadeWithBar(
                Border(rightActionWrap)
                    // 只给【左侧】留 8px 组间距，右边留 0 —— 右侧净空由行的 FlexPadding
                    // （padRight，与左侧 padLeft 同值）提供。
                    // 之前这里左右都写 8（.Margin(8, 0) 是左右各 8），最末一枚按钮到边缘
                    // 变成 padRight + 8 = 20px，而左边返回按钮只有 padLeft = 12px，
                    // 两侧不对称 —— 看着就是"右侧间距不对"。
                    // 现在左 = padLeft 12 / 右 = padRight 12，组内 4px，左右组各留 8px 与中组间隔。
                    .Margin(rightActionsMounted ? 8 : 0, 0, 0, 0)
                    .Ref(rightGroupRef)
                    .OnSizeChanged((_, _) => UpdateMetrics()),
                p.IsVisible && rightActionsMounted);

        // 中组弹性占位：无背景不拦截点击，把左右两组推向两边，覆盖层在其上方绝对定位标题
        var middleSpacer = Border(Empty())
            .Flex(grow: 1, shrink: 1, basis: 0)
            .HAlign(HorizontalAlignment.Stretch);

        // FlexRow 不接受 spacing 首参；间距已用 margin 落在各组上
        var contentRow = (FlexRow(new Element[] { leftGroup, middleSpacer, rightGroup }) with
        {
            AlignItems = FlexAlign.Center,
            ColumnGap = 0,
        })
            .FlexPadding(padLeft, 0, padRight, 0)
            .HAlign(HorizontalAlignment.Stretch)
            .Height(48);

        // ---- 下层：底衬（恒透明，不含任何内容，只负责空白区的命中测试）----
        //
        // 【2026-10-01：亚克力实现已整体移出本组件，见 Render 里"底衬（亚克力）"那一段】
        // 这一层【不再挂任何材质画刷】：整栏渲染为透明，底下是什么就透出什么
        // （窗口级 Mica / 页面自己的沉浸背景 / 外部实现的材质层，都能直接看到）。
        // 之所以还留着它，是为了沿用原有的命中语义：
        //   底衬"显示" ⇒ 空白区拦截点击；底衬"透明" ⇒ 空白区点击穿透到下层页面。
        // 内容层在它之上，标题 / 按钮 / 返回不受影响。
        //
        // 【2026-10-02 又改回来了：不再给它透明画刷】
        // 旧注释说"要命中就必须有画刷"，那条在 WinUI 里并不成立 ——
        // 空白 Border 的 Background 为 null 时，命中测试落到 Border 自身而非其子元素，
        // 空白区照样被拦住（真正让它穿透的是 IsHitTestVisible=false）。
        // 多挂那支 alpha=0 的 SolidColorBrush 反而有代价：非 null 的 Background 会给
        // Grid 多留一层可合成的背景表面，而 Reactor 的 ElementPool 在回收时要把元素
        // 摘出/重挂父容器 —— 这类"有自身背景"的元素正是 ForceDetach 里
        // UIElementCollection.Add 抛 COMException 的高发对象（实测 0x800F1000，
        // 触发时机是切页时 keyed 子树整体卸载）。
        // 透明就该是真透明：视觉无差别，合成层少一层。
        var backgroundLayer = Border(Empty())
            .IsHitTestVisible(materialWanted)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);

        // 根容器三层叠放；barRef 实测整栏宽度驱动标题绝对居中
        return Grid(
                Array.Empty<GridSize>(),
                Array.Empty<GridSize>(),
                backgroundLayer,
                contentRow,
                centerOverlay)
            .Ref(barRef)
            .OnSizeChanged((_, _) => UpdateMetrics())
            .Height(48)
            .HAlign(HorizontalAlignment.Stretch)
            .RequestedTheme(elementTheme)
            .AutomationName("AppTopBar");

        // ---- 局部元素构建与编排（局部函数，闭包本组件状态） ----

        // 可淡出层：visible=false 时透明度归零并禁用命中测试（时长由调用方指定）。
        // 返回按钮【不经过】此包装，故永远存在。
        Element FadeWithBar(Element el, bool visible, int fadeMs = 220) => el
            .Opacity(visible ? 1 : 0)
            .OpacityTransition(TimeSpan.FromMilliseconds(fadeMs))
            .IsHitTestVisible(visible);

        // 返回按钮：动画返回箭头；普通模式执行 OnBack，搜索模式退出搜索
        Element BuildBackButton()
        {
            var state = pressedKey == BackButtonKey ? "Pressed" : "Normal";

            return Button(
                AnimatedIcon(
                    backSource,
                    new SymbolIconSource { Symbol = Symbol.Back })
                    .Size(20, 20)
                    .Set(icon =>
                    {
                        if (XamlAnimatedIcon.GetState(icon) != state)
                        {
                            XamlAnimatedIcon.SetState(icon, state);
                        }
                    }),
                () =>
                {
                    if (searching)
                    {
                        ExitSearch();
                    }
                    else
                    {
                        p.OnBack?.Invoke();
                    }
                })
                .SubtleButton()
                // 纯图标按钮清零 WinUI Button 默认内边距，让 20px 图标在 40x40 点击区完整居中
                .Padding(0)
                .Size(40, 40)
                // ⭐【悬停残留的真正修法】命中测试挂在【本按钮自己】身上，且【只绑 ShowBack】。
                //    为什么必须挂自身：XAML 内部那枚 IsPointerOver 标志，只在元素【自身】的
                //    IsHitTestVisible 由 true→false 时才被清除并补发 PointerExited；父级被关
                //    命中、或元素 Collapsed，都不会清它（实测打点：隐藏时本按钮恒 hit=True）。
                //    标志清不掉 ⇒ 每轮渲染 Button.UpdateVisualState() 都按它把状态拉回
                //    PointerOver，外面再怎么 GoToState("Normal") 都会被覆盖（实测 goto=True
                //    却无效）。搜索按钮走的就是"挂自身"这条路，见 .IsHitTestVisible(searchBtnShown)。
                //
                //    为什么【只】绑 ShowBack、绝不掺 p.IsVisible：契约见
                //    AppTopBarProps.IsVisible 注释 —— 整栏淡出时「返回按钮不参与淡出，始终显示
                //    且可点击」。掺进去会让淡出期间 hit=false ⇒ 返回键直接点不动（曾这么改过，
                //    属于改坏契约，已撤回）。而整栏淡出时按钮本来就【没隐藏】，也就不需要复位。
                //    于是两条路径各得其所：
                //      · ShowBack=false ⇒ Collapsed + 自身 hit=false ⇒ 标志清零 ⇒ 回 Normal；
                //      · 整栏淡出(IsVisible=false) ⇒ hit 仍 true ⇒ 始终可点，符合契约。
                .IsHitTestVisible(p.ShowBack)
                .AutomationName("BackButton")
                // 按压订阅统一走 HookButton：不能用 Reactor 的 .OnPointerPressed
                //（Button 把它标成 Handled，普通 += 订阅收不到），理由见 HookButton 注释
                .Set(b =>
                {
                    HookButton(b, BackButtonKey, animated: true);

                    // 【只有一条路会让返回按钮消失】：p.ShowBack=false ⇒ 本按钮 Collapsed。
                    // 别把 p.IsVisible 掺进来 —— 按 AppTopBarProps.IsVisible 的契约，整栏淡出时
                    // 返回按钮「不参与淡出，始终显示且可点击」，它那时压根没隐藏，不需要复位；
                    // 掺进来还会误伤（曾据此关命中测试，导致淡出期间返回键点不动）。
                    // 跨过"可交互 ⇄ 不可交互"这条线就复位一次：Collapsed 期间收不到
                    // PointerExited ⇒ 状态机停在 PointerOver，重新 Visible 时也不补发
                    // PointerEntered ⇒ "出现那一帧带着悬停底色"。
                    var interactable = p.ShowBack;
                    if (interactable != backInteractable.Current)
                    {
                        backInteractable.Current = interactable;
                        ResetButtonVisual(b);

                        // 回到顶级（ShowBack=false）⇒ 直接把视觉树销毁重建。
                        // 新长出来的树没有任何动画残留，下次再出现就是干净的 Normal。
                        // 放在"隐藏"这一侧而不是"出现"那一侧：此刻按钮不可见，重建零视觉代价，
                        // 且能顺带把本轮交互留下的按压/悬停痕迹一起埋掉。
                        if (!interactable)
                        {
                            RebuildButtonVisualTree(b);
                        }
                    }
                });
        }

        // 搜索模式下右缘唯一的那枚「确认 / 提交」按钮。
        //
        // 图标形态由快照决定（TopBarCenter.Search.Accept），默认是动画放大镜。
        // AnimatedFindVisualSource 的标记是 Normal / PointerOver / Pressed 三态及六段过渡。
        //
        // 状态分层：
        //   - Button（SubtleButton）：保留悬停/按下背景，负责命中与点击反馈；
        //   - AnimatedIcon：通过 ref 直接设置 state，不触发重渲染。
        // 状态缓存的根因：退出时 IsHitTestVisible=false → 再进入 true，WinUI 不会重发
        // PointerEntered，Button 的 PointerOver VisualState 残留。
        // 修复：进入搜索时显式调用 VisualStateManager.GoToState(btn, "Normal", false)
        // 重置按钮 CommonStates，同时重置 AnimatedIcon 为 Normal。
        Element BuildSearchButton()
        {
            void SetIconState(string s)
            {
                if (searchIconRef.Current is { } icon
                    && icon.XamlRoot is not null
                    && XamlAnimatedIcon.GetState(icon) != s)
                {
                    XamlAnimatedIcon.SetState(icon, s);
                }
            }

            // 按钮内容：动画 / 静态二选一，由 acceptIcon 解析结果决定。
            //
            // 为什么"不要这枚按钮"时（Hidden）也照样 build 一份内容：覆盖层的子元素数量
            // 一旦随状态变化，Reactor 会重建整棵子树，ConnectedAnimation 的源/目标会在
            // PrepareToAnimate → TryStart 之间被替换 ⇒ 抛 "source element is not in the
            // element tree"（本项目实测，见下面 searchButton 处的注释）。所以一律常驻
            // 挂载，只用 Opacity / 命中测试切。
            Element BuildAcceptContent()
            {
                // 静态字形：交给原生 SymbolIcon（内部做码点/轮廓变体映射），
                // 不要手转成 FontIcon —— 会丢掉那层映射。
                if (acceptIcon.Static is { } staticIcon)
                {
                    return Icon(staticIcon);
                }

                if (acceptIcon.Hidden)
                {
                    // 隐藏态：内容选什么都看不见，给个最省事的占位
                    return Icon(new SymbolIconData("Find"));
                }

                return AnimatedIcon(
                    acceptIcon.Source ?? findSource,
                    acceptIcon.Fallback ?? new SymbolIconSource { Symbol = Symbol.Find })
                    .Size(20, 20)
                    .Ref(searchIconRef)
                    .Set(icon =>
                    {
                        // 挂载/重渲染时始终落到 Normal：悬停/按压由事件实时驱动
                        if (XamlAnimatedIcon.GetState(icon) != "Normal")
                        {
                            XamlAnimatedIcon.SetState(icon, "Normal");
                        }
                    });
            }

            // 这枚按钮此刻该不该露出来：既要"处在搜索态"（searchBtnShown），
            // 也要"形态认账"（TopBarAcceptIcon.None 就永远不露）。
            var acceptShown = searchBtnShown && !acceptIcon.Hidden;

            return Button(
                BuildAcceptContent(),
                SubmitSearch)
                .SubtleButton()
                .Ref(searchBtnRef)
                .Padding(0)
                .Size(40, 40)
                .Opacity(acceptShown ? 1 : 0)
                // 不用 XAML 的 OpacityTransition：它会禁用本元素的 RenderTransform API，
                // 而 Reactor 回收元素时 ElementPool.CleanElement 必调
                // ClearValue(RenderTransform) ⇒ 抛 UnauthorizedAccessException
                //（本项目在 ConnectedAnimation 子树上已验证过，见 TitlePresenceEffect 注释）。
                // 改挂 Reactor 的合成器隐式动画：语义等价（Opacity 变化按曲线过渡），
                // 且只作用于 Opacity，全程不触碰 RenderTransform。
                .Animate(Microsoft.UI.Reactor.Animation.Curve.Linear(ButtonsFadeMs),
                         Microsoft.UI.Reactor.Animation.AnimateProperty.Opacity)
                .IsHitTestVisible(acceptShown)
                // ⚠️ 不能用 .OnPointerPressed / .OnPointerReleased：Button 把这两个事件标成
                // Handled，普通 += 订阅收不到（本机实测：.OnPointerExited 有反应，那两个没有）。
                // 所以整套（含 Entered/Exited）统一交由 HookSearch 走 AddHandler，并附带
                // IsPressed 属性回调做复位兜底 —— 理由同 HookPress。
                .Set(HookSearch)
                .AutomationName("SearchButton");

            string ReleaseState() => searchHovering.Current ? "PointerOver" : "Normal";

            // 同一原生实例只挂一次：AddHandler 不去重，每次渲染新 lambda 会让订阅堆积，
            // 一次按下能被处理几十次。元素被池回收换新实例时，旧订阅先摘再重挂。
            void HookSearch(Button b)
            {
                if (searchHook.Current is { } cur && ReferenceEquals(cur.Owner, b))
                {
                    return;
                }

                if (searchHook.Current is { } old)
                {
                    old.Owner.RemoveHandler(UIElement.PointerEnteredEvent, old.Entered);
                    old.Owner.RemoveHandler(UIElement.PointerExitedEvent, old.Exited);
                    old.Owner.RemoveHandler(UIElement.PointerPressedEvent, old.Pressed);
                    old.Owner.RemoveHandler(UIElement.PointerReleasedEvent, old.Released);
                    old.Owner.RemoveHandler(UIElement.PointerCanceledEvent, old.Canceled);
                    old.Owner.UnregisterPropertyChangedCallback(
                        XamlButtonBase.IsPressedProperty, old.PressToken);
                }

                // 同上：从池里回来的实例必须先把 CommonStates 拉回 Normal，
                // 顺带把"指针是否停在按钮上"这份跨渲染记忆也交还 —— 新实例不该继承悬停态。
                ResetVisualOnLoad(b);
                searchHovering.Current = false;

                var entered = new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => { searchHovering.Current = true; SetIconState("PointerOver"); });
                var exited = new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => { searchHovering.Current = false; SetIconState("Normal"); });
                var pressed = new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => SetIconState("Pressed"));
                var released = new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => SetIconState(ReleaseState()));
                var canceled = new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => { searchHovering.Current = false; SetIconState("Normal"); });

                // IsPressed 回到 false 就是"这次按压结束了"的最强证据：
                // Released / Exited / Canceled 任何一个迟到或丢失，它都不会缺席。
                void OnIsPressedChanged(DependencyObject sender, DependencyProperty dp)
                {
                    if (sender is XamlButtonBase bb && !bb.IsPressed)
                    {
                        SetIconState(ReleaseState());
                    }
                }

                var token = b.RegisterPropertyChangedCallback(
                    XamlButtonBase.IsPressedProperty, OnIsPressedChanged);

                b.AddHandler(UIElement.PointerEnteredEvent, entered, true);
                b.AddHandler(UIElement.PointerExitedEvent, exited, true);
                b.AddHandler(UIElement.PointerPressedEvent, pressed, true);
                b.AddHandler(UIElement.PointerReleasedEvent, released, true);
                b.AddHandler(UIElement.PointerCanceledEvent, canceled, true);

                searchHook.Current = (b, entered, exited, pressed, released, canceled, token);
            }
        }

        // 重置搜索按钮的全部视觉状态：Button 的 CommonStates 回到 Normal，
        // AnimatedIcon 回到 Normal。在进入搜索（setSearchBtnShown(true)）时调用，
        // 清除上次退出时 IsHitTestVisible=false 残留的 PointerOver/Pressed 状态。
        void ResetSearchButtonVisualState()
        {
            if (searchBtnRef.Current is { } btn
                && btn.XamlRoot is not null
                && btn is Microsoft.UI.Xaml.Controls.Control ctrl)
            {
                Microsoft.UI.Xaml.VisualStateManager.GoToState(ctrl, "Normal", false);
            }
            if (searchIconRef.Current is { } icon && icon.XamlRoot is not null)
            {
                XamlAnimatedIcon.SetState(icon, "Normal");
            }
        }

        // 左/右组的自定义按钮：静态图标或动画图标
        Element BuildActionButton(TopBarAction action)
        {
            var disabled = action.State == TopBarButtonState.Disabled;

            Element content;
            if (action.AnimatedSource is not null)
            {
                var state = pressedKey == action.AutomationName ? "Pressed" : "Normal";
                content = AnimatedIcon(
                    action.AnimatedSource,
                    action.FallbackIconSource ?? new SymbolIconSource { Symbol = Symbol.Setting })
                    .Size(20, 20)
                    .Set(icon =>
                    {
                        if (XamlAnimatedIcon.GetState(icon) != state)
                        {
                            XamlAnimatedIcon.SetState(icon, state);
                        }
                    });
            }
            else
            {
                // 静态图标交给原生 SymbolIcon（内部做码点/轮廓变体映射），不要手转 FontIcon
                content = Icon(action.StaticIcon ?? new SymbolIconData("Placeholder"));
            }

            // Key：动作按钮运行时动态增删，必须给稳定身份，Reactor 才能正确复用/卸载原生按钮
            var button = Button(content, () => InvokeAction(action))
                .SubtleButton()
                .Padding(0)
                .Size(40, 40)
                // 禁用：IsEnabled=false 同时给出禁用外观并挡掉点击（不必再自己判状态）
                .IsEnabled(!disabled)
                // 悬停提示：走 render 期修饰器（而不是 .Set + 附加属性），
                // 元素被池回收时不会丢（REACTOR_POOL_001 会警告后者）。
                // 空提示时给 null —— 修饰器自己处理"摘掉提示"，不需要我们判空。
                .ToolTip(action.Tooltip)
                .AutomationName(action.AutomationName) with
            {
                Key = action.AutomationName,
            };

            // 所有按钮都过一遍 HookButton：
            //   动画图标 —— 需要按压缩订把 AnimatedIcon 打到 Pressed；
            //   静态图标 —— 不需要订阅，但同样要那道上树复位（关闭按钮点完就换页，
            //               PointerExited 从没机会发出，回来时元素带着上次的悬停态复用）。
            return button.Set(b => HookButton(b, action.AutomationName, action.AnimatedSource is not null));
        }

        // 按钮点击的统一入口：状态在这里拦截，宿主传进来的 OnClick 不需要自己判断。
        void InvokeAction(TopBarAction action)
        {
            if (action.State == TopBarButtonState.Disabled)
            {
                return;
            }

            action.OnClick?.Invoke();
        }

        static double RefWidth(ElementRef? r) =>
            (r?.Current as FrameworkElement)?.ActualWidth ?? -1;

        double[] SnapshotGeom() => new[]
        {
            RefWidth(barRef),
            RefWidth(leftGroupRef),
            RefWidth(rightGroupRef),
            RefWidth(titleRef),
            RefWidth(inputRef),
        };

        // 几何门控 + 动画启动（详见 Render 内注释）
        Action MorphEffect(bool isSearching)
        {
            var cancelled = false;

            // 兜底显形（无快照 / TryStart 失败 / 超时）：无动画播放，铬层无需保留
            void RevealDestination()
            {
                if (cancelled)
                {
                    return;
                }
                if (isSearching)
                {
                    setBoxShown(true);
                    setSearchBtnShown(true);
                    ResetSearchButtonVisualState();
                }
                else
                {
                    // 退出方向兜底：没有动画播放 ⇒ 悬停锁必须立刻解除，否则铬层被永久钉住
                    ReleaseTitleChromeHover();
                    setTitleShown(true);
                    setActionsShown(true);
                }
            }

            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(SearchMorphKey);
            var destination = (FrameworkElement?)(isSearching ? inputRef.Current : titleRef.Current);
            if (animation is null || destination is null)
            {
                animation?.Cancel();
                RevealDestination();
                return () => { };
            }
            animation.Configuration = new DirectConnectedAnimationConfiguration();

            var attempts = 0;
            double[]? prevGeom = null;
            Action? poll = null;
            poll = () =>
            {
                // 新一轮切换已发生：本轮链立即作废，不 Cancel（新 effect 接管新快照）
                if (cancelled)
                {
                    return;
                }

                var geom = SnapshotGeom();
                var stable = false;
                if (prevGeom is not null)
                {
                    stable = true;
                    for (var i = 0; i < geom.Length; i++)
                    {
                        if (Math.Abs(geom[i] - prevGeom[i]) > 0.5)
                        {
                            stable = false;
                            break;
                        }
                    }
                }
                prevGeom = geom;

                if (stable)
                {
                    // 退出方向：铬层必须在【TryStart 之前】就压好悬停外观。
                    // TryStart 之后到终点显形之间，ButtonBase 仍有 UpdateVisualState 的机会
                    // （IsHitTestVisible / 可见性变化都会触发），压晚了会被打回 Normal。
                    if (!isSearching)
                    {
                        HoldTitleChromeHover();
                    }
                    if (animation.TryStart(destination))
                    {
                        // 成功的同一调度帧内才显形终点：门控等待期间终点保持不可见，
                        // 不透明静置快照浮于动画层，与显形终点同帧无缝交接
                        destination.Opacity = 1;
                        if (isSearching)
                        {
                            setBoxShown(true);
                            setSearchBtnShown(true);
                            ResetSearchButtonVisualState();
                        }
                        else
                        {
                            // 形变【播完】才退回非悬停：悬停锁若不解，铬层会被永久钉住
                            animation.Completed += (_, _) =>
                            {
                                if (!cancelled)
                                {
                                    ReleaseTitleChromeHover();
                                }
                            };
                            // 标题显形承接快照；动作按钮同步淡入
                            setTitleShown(true);
                            setActionsShown(true);
                        }
                        return;
                    }
                    // 几何已稳定仍无法启动（多为快照已失效）：直接兜底
                    animation.Cancel();
                    RevealDestination();
                    return;
                }

                if (++attempts < MorphMaxFrames)
                {
                    destination.DispatcherQueue?.TryEnqueue(() => poll?.Invoke());
                }
                else
                {
                    animation.Cancel();
                    RevealDestination();
                }
            };
            destination.DispatcherQueue?.TryEnqueue(() => poll());

            // searching 再次变化时作废本轮链
            return () =>
            {
                cancelled = true;
            };
        }

        // 标题文本更新动画：旧文本淡出 TitleSwapMs → 透明时换字 → 新文本淡入
        // 用 Storyboard/DoubleAnimation 而非 OpacityTransition：后者与 ConnectedAnimation
        // 操作源/目标 RenderTransform 冲突（UnauthorizedAccessException）。
        // 换目标题为 incomingTitle：可见时才走“淡出→换字→淡入”编排。
        // visible=false（居中位无内容 / 处于搜索态）：直接换字并恢复不透明，
        // 因为这时动画既看不见，也不可靠（WinUI 不保证不可见元素上动画的 Completed 回调）。
        Action TitleSwapEffect(
            string incomingTitle,
            IconData? incomingIcon,
            string currentDisplayed,
            IconData? currentIcon,
            bool visible)
        {
            // IconData 是 abstract record，用静态 Equals 判值相等（宿主任一帧 new 一个也稳定）
            if (currentDisplayed == incomingTitle && Equals(currentIcon, incomingIcon))
            {
                // 内容已一致，但可能正卡在透明态：上一次淡出被本轮 effect 打断（快速连切页），
                // 换内容那一步没执行、revealed 还是 false。这时必须补一次淡入，
                // 否则标题会永久停在透明。
                if (pendingFadeIn.Current)
                {
                    var stuck = titleSwapRef.Current as Microsoft.UI.Xaml.UIElement;
                    if (stuck is null)
                    {
                        pendingFadeIn.Current = false;
                        setTitleRevealed(true);
                        return () => { };
                    }
                    return RunWhenTitleContentReady(
                        stuck,
                        incomingTitle,
                        () => { pendingFadeIn.Current = false; StartTitleFadeIn(stuck); },
                        () => { pendingFadeIn.Current = false; setTitleRevealed(true); });
                }
                return () => { };
            }

            var target = titleSwapRef.Current as Microsoft.UI.Xaml.UIElement;

            // ① 标题不可见（整栏隐藏 / 处于搜索态）：动画看不见也不可靠，直接换内容。
            //    WinUI 不保证不可见元素上动画的 Completed 回调，等下去会永久卡住。
            if (target is null || !visible)
            {
                CancelAllTitleWaits();
                // 残留的 HoldEnd 动画（淡出钉 0 / 淡入钉 1）必须停掉，
                // 否则它盖住本地值，标题要么隐形要么顶着旧透明度
                titleSwapSb.Current?.Stop();
                titleSwapSb.Current = null;
                pendingFadeIn.Current = false;
                setDisplayedTitle(incomingTitle);
                setDisplayedIcon(incomingIcon);
                setTitleRevealed(true);
                return () => { };
            }

            // ② 标题【看得见但当前是透明的】：上一次换字还没淡入完（连续切页时常见），
            //    或旧内容为空（首次建立标题）。此时没有可淡出的东西，但也不能"直接显示"
            //    ——那正是突然冒出来的那一下抖动。正确做法：换内容 → 等就绪 → 淡入。
            if (target.Opacity < 0.01 || currentDisplayed.Length == 0)
            {
                CancelAllTitleWaits();
                titleSwapSb.Current?.Stop();
                titleSwapSb.Current = null;
                setTitleRevealed(false);
                pendingFadeIn.Current = true;
                setDisplayedTitle(incomingTitle);
                setDisplayedIcon(incomingIcon);
                return () => { };
            }

            // 淡出前同样清掉残留动画，避免两段同属性动画叠加互相覆盖
            titleSwapSb.Current?.Stop();
            titleSwapSb.Current = null;
            // 淡出：1 → 0
            var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(TitleSwapMs),
                EnableDependentAnimation = true
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOut, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOut, "Opacity");
            var sbOut = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sbOut.Children.Add(fadeOut);
            titleSwapSb.Current = sbOut;

            var completed = false;

            // 兜底收敛：窗口不可见 / 最小化时，XAML 可能不推进依赖动画（Completed 永不派发），
            // 换字就会卡在旧文本。用单次定时器在动画时长的两倍后强制收敛到目标文本。
            // 定时器必须由 ref 强引用，否则 GC 提前回收后 Tick 静默消失（与形变定时器同理）。
            var swapFallback = target.DispatcherQueue.CreateTimer();
            swapFallbackTimer.Current = swapFallback;
            swapFallback.Interval = TimeSpan.FromMilliseconds(TitleSwapMs * 2 + 50);
            swapFallback.IsRepeating = false;
            swapFallback.Tick += (_, _) =>
            {
                swapFallback.Stop();
                if (!completed && swapFallbackTimer.Current == swapFallback)
                {
                    completed = true;
                    // ⚠️ 必须先 Stop 再丢引用：Completed 不派发时（实测会发生），Storyboard
                    // 以默认 FillBehavior=HoldEnd 永久钉住终值 0 —— 之后无论渲染多少次、
                    // revealed 置回多少次 true，实际 Opacity 都被动画压在 0，标题永久隐形。
                    // 先把本地值顶到 1 再 Stop，回落瞬间不会闪出 0。
                    if (target is Microsoft.UI.Xaml.UIElement swapTarget)
                    {
                        swapTarget.Opacity = 1;
                    }
                    sbOut.Stop();
                    titleSwapSb.Current = null;
                    CancelAllTitleWaits();
                    setDisplayedTitle(incomingTitle);
                    setDisplayedIcon(incomingIcon);
                    setTitleRevealed(true);
                }
            };

            sbOut.Completed += (_, _) =>
            {
                if (completed) return;
                completed = true;
                sbOut.Stop();
                titleSwapSb.Current = null;
                // 淡出完成：换成新内容（图标+文本），但【先保持透明】——
                // 必须等图标与文本都真正挂载并完成布局后才启动淡入，否则动画会打在
                // 尚未测量（或还是旧值）的元素上，表现为新标题直接跳变/淡入被打断。
                pendingFadeIn.Current = true;
                setDisplayedTitle(incomingTitle);
                setDisplayedIcon(incomingIcon);
            };
            setTitleRevealed(false);
            swapFallback.Start();
            sbOut.Begin();

            return () =>
            {
                swapFallback.Stop();
                if (swapFallbackTimer.Current == swapFallback)
                {
                    swapFallbackTimer.Current = null;
                }
                if (!completed)
                {
                    completed = true;
                    sbOut.Stop();
                    titleSwapSb.Current = null;
                }
            };
        }

        // 换字后半程：本轮 effect 在 displayedTitle/displayedIcon 渲染提交后运行，
        // 所以此刻原生元素已是新内容。这里只做两件事：
        //   ① 确认内容真的同步到了最新（图标与文本可能分批提交，未同步就等下一轮）；
        //   ② 等布局收敛（几何连续两帧不变）后启动淡入。
        Action TitleRevealEffect(string text, IconData? icon)
        {
            var target = titleSwapRef.Current as Microsoft.UI.Xaml.UIElement;
            if (!pendingFadeIn.Current)
            {
                return () => { };
            }
            if (target is null)
            {
                pendingFadeIn.Current = false;
                setTitleRevealed(true);
                return () => { };
            }

            // 内容尚未同步到目标值：本轮不做淡入，等下一轮 effect（内容补齐后再跑）。
            // 否则会出现"文本是新的、图标还是旧的"这种半新半旧的淡入。
            if (!string.Equals(text, p.CenterText, StringComparison.Ordinal) || !Equals(icon, p.CenterIcon))
            {
                return () => { };
            }

            return RunWhenTitleContentReady(
                target,
                text,
                () =>
                {
                    pendingFadeIn.Current = false;
                    StartTitleFadeIn(target);
                },
                () =>
                {
                    pendingFadeIn.Current = false;
                    setTitleRevealed(true);
                });
        }

        // 作废所有仍在等待的淡入（换字被打断 / 进入搜索 / 不可见快捷路径）。
        // 注意：这里取消的是"等待"，不是动画本身；动画由各自的 Storyboard 引用管理。
        void CancelAllTitleWaits()
        {
            var waits = activeWaits.Current;
            if (waits is null || waits.Count == 0)
            {
                return;
            }
            foreach (var cancel in waits.ToArray())
            {
                cancel();
            }
        }

        // 标题内容是否“就绪”：**文本已挂载并完成一次布局**。
        // 只有这时启动淡入才有意义 —— 淡入不该发生在“元素刚换、还没测量”的那一帧。
        //
        // ⚠️ 这里【刻意不判图标的尺寸】（旧实现判 icon.ActualWidth/Height > 0）：
        // 图标格现在是写死的 16×16（见 TitleIconSize），而里面的位图（BitmapImage）
        // 解码是异步的 —— 拿"图标有没有测出宽度"当判据，等于让标题显隐动画等待位图解码：
        // 解码慢一点就轮询满 TitleReadyMaxFrames（30 帧 ≈ 500ms）超时，走 fallback
        // 直接 setTitleRevealed(true) ⇒ 整块标题【瞬跳】出现，而不是淡入。
        // 图标格尺寸恒定，它不再是"内容有没有到位"的信号，判它只会误伤。
        bool IsTitleContentReady(string expectedText)
        {
            var host = titleSwapRef.Current;
            if (host is null)
            {
                return false;
            }

            // 文本：已挂载、文本已换成期望值、并完成测量（空文本没有可测宽度，跳过该判据）
            if (titleTextRef.Current is not Microsoft.UI.Xaml.Controls.TextBlock tb)
            {
                return false;
            }
            // 只判"已完成测量"：内容是否同步到期望值由 TitleRevealEffect 用状态比较保证
            // （原生 Text 的写入时机不受控，拿它当判据会把就绪误判成永不就绪）
            if (expectedText.Length > 0 && (tb.ActualWidth <= 0 || tb.ActualHeight <= 0))
            {
                return false;
            }

            return true;
        }

        // 标题几何快照（用于判断布局是否已收敛）：宿主宽高 + 内容宽高 + 文本宽高 + 图标宽高。
        // 换字时文本/图标一变，titleHost 的宽度和 clamp 后的位置都会跟着变；若在布局
        // 收敛前就开始淡入，就会看到"淡入的同时位置还在跳"——即抖动。
        double[] SnapshotTitleGeom()
        {
            var host = titleRef.Current;
            var body = titleSwapRef.Current;
            var tb = titleTextRef.Current;
            var icon = titleIconRef.Current;
            // 位置也必须纳入：宽度入状态（setTitleMeasuredW）会在宽度测量之后才
            // 触发重渲染改 Margin，只判宽度会漏掉"淡入开始后位置才补跳一下"这种情况。
            var hostOffset = host?.ActualOffset ?? default;
            return new[]
            {
                host?.ActualWidth ?? -1,
                host?.ActualHeight ?? -1,
                hostOffset.X,
                hostOffset.Y,
                body?.ActualWidth ?? -1,
                tb?.ActualWidth ?? -1,
                tb?.ActualHeight ?? -1,
                icon?.ActualWidth ?? -1,
            };
        }

        // 等标题内容就绪【且布局收敛】后执行 start；始终等不到（窗口不可见、内容异常）时
        // 执行 fallback，保证任何情况下标题都收敛到可见，不会永久停在透明态。
        // 返回取消委托：新一轮换字 / effect 重跑时调用它作废本轮等待。
        Action RunWhenTitleContentReady(
            Microsoft.UI.Xaml.UIElement target,
            string expectedText,
            Action start,
            Action fallback)
        {
            var waits = activeWaits.Current ??= new List<Action>();

            var timer = target.DispatcherQueue?.CreateTimer();
            if (timer is null)
            {
                // 拿不到调度器（极罕见）：不等待，直接执行，避免卡住
                start();
                return () => { };
            }

            var cancelled = false;
            var attempts = 0;
            double[]? prevGeom = null;
            Action? cancel = null;
            cancel = () =>
            {
                cancelled = true;
                timer.Stop();
                waits.Remove(cancel!);
            };
            // 逐帧定时器（而非 TryEnqueue 递归）：递归在空闲队列里会在同一帧内被连续派发，
            // "连续两帧几何一致"就退化成恒真、等不到真正的布局收敛。
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                if (cancelled) return;

                // 布局收敛判据：连续两帧几何完全一致（与形变前的几何轮询同一套路）
                var geom = SnapshotTitleGeom();
                var stable = false;
                if (prevGeom is not null)
                {
                    stable = true;
                    for (var i = 0; i < geom.Length; i++)
                    {
                        if (Math.Abs(geom[i] - prevGeom[i]) > 0.5)
                        {
                            stable = false;
                            break;
                        }
                    }
                }
                prevGeom = geom;

                if (stable && IsTitleContentReady(expectedText))
                {
                    cancel();
                    start();
                    return;
                }

                if (++attempts < TitleReadyMaxFrames)
                {
                    return;
                }


                // 兜底：等不到就绪/收敛也要显示，避免标题永久透明
                cancel();
                fallback();
            };
            // 定时器由 cancel 闭包（又被 waits 引用）强引用，不会被 GC 提前回收
            waits.Add(cancel);
            timer.Start();
            return cancel!;
        }

        // 启动标题几何补间（左缘 + 可用宽度）。
        // 已在跑时【不重启】：只更新 shiftTarget（Tick 每帧读最新目标）。这一点很关键——
        // 动画期间实测宽度会随 MaxWidth 变化而变，目标本身也在移动；若每帧重启一段
        // 固定时长动画，就退化成"永远走不到终点"的慢速爬行，且时长不可控。
        void StartTitleShift()
        {
            if (shiftTimer.Current is not null)
            {
                return;
            }
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var timer = queue.CreateTimer();
            shiftTimer.Current = timer;
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                // Tick 内未处理异常在 WinUI 桌面下会终止进程：必须兜底
                try
                {
                    var (tl, ts) = shiftTarget.Current;
                    var (cl, cs) = shiftCur.Current;
                    // 标题几何不补间时直接取终值（间距仍照常补间）
                    var nl = !shiftGeoLive.Current ? tl
                        : NearTween(tl, cl) ? tl : cl + (tl - cl) * TitleShiftAlpha;
                    var ns = !shiftGeoLive.Current ? ts
                        : NearTween(ts, cs) ? ts : cs + (ts - cs) * TitleShiftAlpha;

                    var (pl, pr) = padTarget.Current;
                    var (pcl, pcr) = padCur.Current;
                    var npl = NearTween(pl, pcl) ? pl : pcl + (pl - pcl) * TitleShiftAlpha;
                    var npr = NearTween(pr, pcr) ? pr : pcr + (pr - pcr) * TitleShiftAlpha;

                    shiftCur.Current = (nl, ns);
                    padCur.Current = (npl, npr);
                    setShiftLeft(nl);
                    setShiftSpan(ns);
                    setPadLeft(npl);
                    setPadRight(npr);
                    if (nl == tl && ns == ts && npl == pl && npr == pr)
                    {
                        StopTitleShift();
                    }
                }
                catch
                {
                    StopTitleShift();
                    var (tl, ts) = shiftTarget.Current;
                    var (pl, pr) = padTarget.Current;
                    shiftCur.Current = (tl, ts);
                    padCur.Current = (pl, pr);
                    setShiftLeft(tl);
                    setShiftSpan(ts);
                    setPadLeft(pl);
                    setPadRight(pr);
                }
            };
            timer.Start();
        }

        void StopTitleShift()
        {
            shiftTimer.Current?.Stop();
            shiftTimer.Current = null;
        }

        // 标题淡入 0 → 1（换字的后半程、或 ShowCenter 由隐藏转显示且内容已就绪时）
        void StartTitleFadeIn(Microsoft.UI.Xaml.UIElement target)
        {
            // 先停掉可能还在 HoldEnd 的旧动画（例如被兜底收敛的淡出）：两段动画同属性时
            // 后来者胜，但旧动画被 Stop 前会一直钉着终值，一旦新动画结束旧值就会露出来。
            titleSwapSb.Current?.Stop();
            var fadeIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(TitleSwapMs),
                EnableDependentAnimation = true
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeIn, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeIn, "Opacity");
            var sbIn = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sbIn.Children.Add(fadeIn);
            var fadeInCompleted = false;
            sbIn.Completed += (_, _) =>
            {
                if (fadeInCompleted)
                {
                    return;
                }
                fadeInCompleted = true;
                titleSwapSb.Current = null;
                setTitleRevealed(true);
            };
            titleSwapSb.Current = sbIn;
            sbIn.Begin();

            // 兜底：Completed 不派发时（与换字淡出同一坑），HoldEnd 会钉住 1 —— 视觉无恙，
            // 但 revealed 状态停在 false，之后的任何重渲染都会把本地值写回 0，
            // 动画一停标题就隐形。定时器到点强制收敛（先顶本地值再 Stop，不闪帧）。
            var fadeInFallback = target.DispatcherQueue.CreateTimer();
            fadeInFallback.Interval = TimeSpan.FromMilliseconds(TitleSwapMs * 2 + 50);
            fadeInFallback.IsRepeating = false;
            fadeInFallback.Tick += (_, _) =>
            {
                fadeInFallback.Stop();
                if (fadeInCompleted || !ReferenceEquals(titleSwapSb.Current, sbIn))
                {
                    return;
                }
                fadeInCompleted = true;
                target.Opacity = 1;
                sbIn.Stop();
                titleSwapSb.Current = null;
                setTitleRevealed(true);
            };
            fadeInFallback.Start();
        }

        // ShowCenter 显隐淡入淡出：把包装层的 Opacity 从【当前有效值】动画到目标值。
        // 与原先的悬停铬层同一套约束：Storyboard + FillBehavior=Stop（播完回落本地值
        // = 目标值），不用 OpacityTransition（ConnectedAnimation 子树内会抛
        // UnauthorizedAccessException）；窗口不可见导致 Completed 不派发时，Stop 语义
        // 仍会在时长结束自动回落，不会永久卡住。
        Action TitlePresenceEffect(bool show)
        {
            var target = titlePresenceRef.Current as Microsoft.UI.Xaml.UIElement;
            if (target is null)
            {
                // 原生元素尚未挂载：没有可动画的目标，直接提交本地值
                setPresenceShown(show);
                return () => { };
            }

            var from = target.Opacity;
            var to = show ? 1.0 : 0.0;
            if (Math.Abs(from - to) < 0.001)
            {
                setPresenceShown(show);
                return () => { };
            }

            // 提交本地值 + 启动动画，二者必须在【同一帧】完成：
            // 本地值是动画播完后的回落目标（FillBehavior=Stop），动画 From 用提交前的实际值。
            Action Reveal()
            {
                setPresenceShown(show);
                return TitlePresenceAnimation(target, from, to);
            }

            // 淡入（由隐藏转显示）必须等文本就绪、且标题几何已稳定：
            // 否则整块标题会在内容还没测量完成/位置还没收敛时就开始淡入，
            // 看到的是"先淡入一个空壳、内容随后跳出来"，位置还会跟着抖。
            // 隐藏方向无需等待，立即淡出。
            if (show)
            {
                return RunWhenTitleContentReady(
                    target, displayedTitle,
                    () => { Reveal(); },
                    () => { Reveal(); });
            }

            return Reveal();
        }

        // ShowCenter 显隐动画本体：从【当前有效值】动画到目标值（Storyboard + FillBehavior=Stop）
        Action TitlePresenceAnimation(Microsoft.UI.Xaml.UIElement target, double from, double to)
        {
            titlePresenceSb.Current?.Stop();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(TitleSwapMs),
                EnableDependentAnimation = true,
                FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sb.Children.Add(anim);
            titlePresenceSb.Current = sb;
            sb.Completed += (_, _) =>
            {
                if (titlePresenceSb.Current == sb)
                {
                    titlePresenceSb.Current = null;
                }
            };
            sb.Begin();

            return () =>
            {
                if (titlePresenceSb.Current == sb)
                {
                    sb.Stop();
                    titlePresenceSb.Current = null;
                }
            };
        }

        // 立即完成可能在进行中的标题文本交换（停 Storyboard、跳到最新文本并恢复可见），
        // 避免 PrepareToAnimate 定格到半透明标题
        void FinishTitleSwap()
        {
            if (titleSwapSb.Current is { } pendingSb)
            {
                pendingSb.Stop();
                titleSwapSb.Current = null;
            }
            // 仍在等待"内容就绪"的那一轮淡入必须作废，否则它会在进入搜索之后
            // 异步启动，把已经定格的标题又淡出来。
            CancelAllTitleWaits();
            if (displayedTitle != p.CenterText)
            {
                setDisplayedTitle(p.CenterText);
            }
            if (!Equals(displayedIcon, p.CenterIcon))
            {
                setDisplayedIcon(p.CenterIcon);
            }
            if (!titleRevealed)
            {
                setTitleRevealed(true);
            }
        }

        // 进入搜索（单阶段，同步提交）：
        //   ① 铬层锁在聚焦（按下）外观 —— 形变终点是聚焦态搜索框，源端用同一套外观才
        //      不会在松手那一拍闪一下透明（见 HoldTitleChromePressed）；
        //   ② 就地定格连接动画快照、切搜索状态，MorphEffect 门控后 TryStart；
        //   ③ 动作按钮同帧卸载，由幽灵层在原地把它们淡出（与形变并行）。
        //
        // 旧实现还有一段"前导静置"（先干等一段再提交），已删除 —— 见上方
        // 【进入搜索没有前导期】处注释。
        void EnterSearch()
        {
            if (searching || exitPending.Current || enterPending.Current)
            {
                return;
            }

            FinishTitleSwap();
            // 退出形变还没播完就又进入搜索：悬停锁必须清掉，否则 SetTitlePressed 的
            // 复位会被 exitHolding 闸夺回成悬停，按下态就压不住了。
            exitHolding.Current = false;
            enterPending.Current = true;
            // 铬层【保持按下态】而不是复位：形变终点是聚焦态搜索框，源端用同一套
            // 外观才不会在松开那一拍闪一下透明（见 HoldTitleChromePressed 处注释）。
            // 必须在 enterPending.Current = true 之后调用 —— SetTitlePressed 靠它挡住
            // 紧随其后的 release/exit 复位。
            HoldTitleChromePressed();
            if (searchIconRef.Current is { } si && si.XamlRoot is not null) XamlAnimatedIcon.SetState(si, "Normal");
            setPressedKey(null);
            // 动作按钮的目标透明度先落 0：本轮提交后它们就随 searching=true 卸载，
            // 视觉上由幽灵层接手淡出，回到普通态时不会带着 1 硬闪一下。
            setActionsShown(false);
            setSearchBtnShown(false);

            // 提交失败的兜底：强制收敛到搜索态，避免标志卡在半路
            void Fallback()
            {
                enterPending.Current = false;
                ResetTitleButtonVisualState();
                if (searchIconRef.Current is { } sicon && sicon.XamlRoot is not null) XamlAnimatedIcon.SetState(sicon, "Normal");
                setPressedKey(null);
                setExitSettling(false);
                setTitleShown(false);
                setBoxShown(true);
                setSearchBtnShown(true);
                setSearching(true);
                ResetSearchButtonVisualState();
            }

            void Commit()
            {
                // 提交前已被转接层改主意（Searchable 变 false，即快照的居中位换成了
                // 标题）：放弃这次进入，回到普通态。
                // 顶栏不再认识"页"，只认当前这一份快照 —— 提交时若它已经不允许搜索，
                // 就不该凭空冒出一个搜索框。退出由上面那条 Searchable effect 兜底
                // （searching 一旦为 true 它就会跑）。
                if (!p.Searchable)
                {
                    enterPending.Current = false;
                    // 放弃进入：铬层要退回 Normal，否则标题会一直顶着聚焦外观
                    ResetTitleButtonVisualState();
                    setExitSettling(false);
                    setActionsShown(true);
                    setSearchBtnShown(false);
                    return;
                }

                enterPending.Current = false;

                // 源（标题宿主）仍可见：定格不透明快照
                if (titleRef.Current is not null && titleShown)
                {
                    ConnectedAnimationService.GetForCurrentView()
                        .PrepareToAnimate(SearchMorphKey, titleRef.Current);
                }
                // 快照已定格，铬层可以松锁：此后标题宿主即将隐藏，退出时由
                // ResetTitleButtonVisualState 再复位一次（双保险，标志不残留）。
                SetTitlePressed(false);
                // 终点（输入框）先不可见，TryStart 成功同帧再显形（见 MorphEffect）
                setTitleShown(false);
                setBoxShown(false);
                setSearching(true);
            }

            // 直接在 Click 回调栈内提交，连一次定时器往返都省掉。
            // 此刻反而【最有利于快照】：ButtonBase 的两处状态回落（OnPointerReleased 里
            // 的 IsPressed=FALSE→UpdateVisualState，以及 ReleasePointerCapture 抛出的
            // CaptureLost→UpdateVisualState）都发生在 Click【之后】，所以这里定格到的
            // 必然是 Pressed（聚焦）外观 —— 不必再靠 SetTitlePressed 的重压去抢。
            try
            {
                Commit();
            }
            catch
            {
                Fallback();
            }
            // 真实动作按钮在这一帧之后就被卸载了，交给幽灵层在原地接着淡出（与形变并行）
            PlayGhostFade();
        }

        // 退出搜索（Esc / 返回键 / 提交），两阶段：
        //   阶段一：输入框失焦回到【静置样式】，动作按钮以 0 透明度提前挂载占位，
        //          保持 ExitSettleMs 让视觉状态可感知、目标布局提前收敛；
        //   阶段二：CommitExitSearch 定格静置快照并切回标题态，MorphEffect 门控后 TryStart。
        void ExitSearch()
        {
            if (!searching || exitPending.Current)
            {
                return;
            }

            // 退出搜索是顶栏自己的交互流程（失焦 / Esc / 返回 / 提交 / 配置不允许），
            // 但转接层必须知道 —— 它通常要清空查询词、把列表切回全量。
            // 放在门控之后：exitPending 保证一次退出只抛一条。
            p.OnEvent?.Invoke(new TopBarEvent.SearchExited());

            var box = inputRef.Current;
            if (box is null)
            {
                CommitExitSearch();
                return;
            }

            exitPending.Current = true;
            // 退出形变【播放期间】铬层锁在悬停态。源端（搜索框）无论如何都有实底，
            // 终点若以 Normal（完全透明）定格，最后一帧等于把实底抹掉 —— 观感是
            // "闪一下消失"。悬停态有 50% 白底，与源端一致，形变只剩尺寸/位置在变。
            // 动画播完由 MorphEffect 的 Completed 退回 Normal（见 HoldTitleChromeHover）。
            exitHolding.Current = true;
            HoldTitleChromeHover();
            // 搜索按钮在静置期先淡出（常驻覆盖层，仅切透明度），同步清掉悬停/按压图标态
            if (searchIconRef.Current is { } si && si.XamlRoot is not null) XamlAnimatedIcon.SetState(si, "Normal");
            setPressedKey(null);
            // 延迟 50ms 再切 IsHitTestVisible=false：
            // 此处处于搜索按钮的 Click 回调链中，ButtonBase 刚收到 PointerReleased、
            // IsPressed 尚未来得及从 true 复位为 false；若此刻立即 IsHitTestVisible=false，
            // ButtonBase 的状态机被打断、IsPressed 永久卡在 true，下次进入搜索时按钮
            // 复用、视觉仍为按下态（“缓存”点击状态）。延迟 50ms，ButtonBase 已完成复位。
            // 用 DispatcherQueueTimer 而非 TryEnqueue：TryEnqueue 仅延迟到当前事件循环结束，
            // 对某些指针状态复位时序仍不够；50ms 定时器更稳妥。
            // 必须存入 UseRef 强引用：DispatcherQueueTimer 是 WinRT 投影对象，
            // 方法内局部变量持有时可能被 GC 提前释放，Tick 静默永不到达。
            releaseBtnTimer.Current?.Stop();
            releaseBtnTimer.Current = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            releaseBtnTimer.Current.Interval = TimeSpan.FromMilliseconds(50);
            releaseBtnTimer.Current.IsRepeating = false;
            releaseBtnTimer.Current.Tick += (s, _) =>
            {
                ((DispatcherQueueTimer)s).Stop();
                if (releaseBtnTimer.Current == s) releaseBtnTimer.Current = null;
                setSearchBtnShown(false);
            };
            releaseBtnTimer.Current.Start();
            // 动作按钮即刻以 0 透明度提前挂载占位：ExitSettleMs 内布局反馈全部收敛
            setExitSettling(true);

            // 阶段一：解除焦点（Normal 视觉态：中性边框、无光标、占位提示文本）
            // 注意：WinUI 3 中 Focus(FocusState.Unfocused) 是非法调用，会抛
            // ArgumentException（"Value does not fall within the expected range"）。
            // 改用 FocusManager.TryMoveFocus 把焦点从输入框移走，输入框自然失焦。
            try
            {
                if (box is Control ctl && ctl.FocusState != FocusState.Unfocused)
                {
                    // 优先把焦点移到标题宿主（退出后标题可见且可交互）
                    if (!Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(
                            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Down,
                            new Microsoft.UI.Xaml.Input.FindNextElementOptions
                            {
                                SearchRoot = titleRef.Current
                            }))
                    {
                        // 兜底：尝试任意方向移焦
                        Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(
                            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next);
                    }
                }
            }
            catch
            {
                // 失焦失败不应阻塞退出流程
            }

            var dispatcher = box.DispatcherQueue;
            if (dispatcher is null)
            {
                CommitExitSearch();
                return;
            }

            morphTimer.Current?.Stop();
            var timer = dispatcher.CreateTimer();
            morphTimer.Current = timer;
            timer.Interval = TimeSpan.FromMilliseconds(ExitSettleMs);
            timer.IsRepeating = false;
            timer.Tick += (sender, _) =>
            {
                ((DispatcherQueueTimer)sender).Stop();
                if (morphTimer.Current == sender)
                {
                    morphTimer.Current = null;
                }
                CommitExitSearch();
            };
            timer.Start();
        }

        // 阶段二：输入框已处于静置样式——登记快照并切回标题态
        void CommitExitSearch()
        {
            try
            {
                exitPending.Current = false;
                // 源（输入框）必须仍可见才能定格快照；若进入动画尚未启动完就被退出
                //（boxShown=false），跳过快照，MorphEffect 直接显示标题兜底
                if (inputRef.Current is not null && boxShown)
                {
                    ConnectedAnimationService.GetForCurrentView()
                        .PrepareToAnimate(SearchMorphKey, inputRef.Current);
                }
                // 终点（标题）先不可见，动画启动帧内再显形
                setTitleShown(false);
                // 挂载条件从 exitSettling 同帧移交 !searching：零卸载、零布局变化
                setExitSettling(false);
                setSearching(false);
                // 搜索按钮在 50ms 定时器中已切 IsHitTestVisible=false；
                // 此处兜底确保即使定时器被 GC 回收，按钮也能隐藏。
                setSearchBtnShown(false);
            }
            catch
            {
                // 任何异常都强制收敛回普通态，防止标志/UI 永久卡在中间态
                exitPending.Current = false;
                // 悬停锁一并解除：异常路径不会有 Completed 回调来解它
                exitHolding.Current = false;
                ResetTitleButtonVisualState();
                if (searchIconRef.Current is { } si && si.XamlRoot is not null) XamlAnimatedIcon.SetState(si, "Normal");
                setPressedKey(null);
                setExitSettling(false);
                setSearching(false);
                setBoxShown(false);
                setTitleShown(true);
                setActionsShown(true);
                setSearchBtnShown(false);
            }
        }

        void SubmitSearch()
        {
            // 退出编排进行中时忽略重复提交（如连击搜索图标）
            if (exitPending.Current)
            {
                return;
            }
            // 状态已在各入口规范化，提交前再收口一次，防未来新增入口灌入脏值
            p.OnSearch?.Invoke(SanitizeQuery(query));
            ExitSearch();
        }

        // 右键菜单“剪切/复制”：复制标题文本到剪贴板（标题为只读文本，剪切等价于复制）
        void CopyTitle()
        {
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetText(p.CenterText);
            Clipboard.SetContent(package);
        }

        // 把输入框光标（脱字符）移到文本末尾、并清掉选中区。
        // 一次性：由 caretToEndOnFocus 标记门控，只有【代码注入文本】后的那一次聚焦生效。
        // 不触碰 Text，因此不会触发 TextChanged，也就不影响受控 TextBox 的回声抑制机制。
        void PlaceCaretAtEndIfPending()
        {
            if (!caretToEndOnFocus.Current)
            {
                return;
            }
            if (inputRef.Current is not TextBox tb)
            {
                return;
            }
            caretToEndOnFocus.Current = false;
            tb.SelectionStart = tb.Text.Length;
            tb.SelectionLength = 0;
        }

        // 右键菜单“粘贴”：取剪贴板文本作为查询词，直接进入搜索（形变为输入框并填入）
        async void PasteToSearch()
        {
            DataPackageView view;
            try
            {
                view = Clipboard.GetContent();
            }
            catch
            {
                return;
            }
            if (!view.Contains(StandardDataFormats.Text))
            {
                return;
            }

            string text;
            try
            {
                text = await view.GetTextAsync();
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // 剪贴板原文几乎总带尾部换行，入状态前必须规范化（见 SanitizeQuery）
            var applied = SanitizeQuery(text);

            // WinUI 3 桌面投影下不保证 await 续体回到 UI 线程；显式经 DispatcherQueue 封送
            var dispatcher = titleRef.Current?.DispatcherQueue
                ?? inputRef.Current?.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(() =>
                {
                    // 文本由代码注入 ⇒ 进入搜索后要把光标带到末尾（否则停在开头）
                    caretToEndOnFocus.Current = true;
                    setQuery(applied);
                    EnterSearch();
                    // 极端情况：输入框此刻【已经】提交了该文本且已聚焦（将来若新增
                    // "搜索态下注入文本"的入口，就不会再有 GotFocus）⇒ 立即落一次光标。
                    // 必须判 Text 已等于目标值：setQuery 只是排队一次重渲染，此刻原生
                    // 控件里往往还是旧文本，拿它算末尾位置会得到错的 SelectionStart。
                    if (inputRef.Current is TextBox tbx
                        && tbx.Text == applied
                        && tbx.FocusState != FocusState.Unfocused)
                    {
                        PlaceCaretAtEndIfPending();
                    }

                    // 回读校正：万一 WinUI 仍对写入值做了规范化（换行截断、MaxLength、
                    // CharacterCasing 等），用控件实际 Text 纠正状态，彻底消灭失配。
                    // 此处写入的值与控件当前值相等，受控属性入口的相等短路会直接 return，
                    // 不会再发放抑制令牌，因此这步自身无副作用且能一次收敛。
                    dispatcher.TryEnqueue(() =>
                    {
                        if (inputRef.Current is TextBox tb && tb.Text != applied)
                        {
                            setQuery(tb.Text);
                        }
                    });
                });
            }
            else
            {
                caretToEndOnFocus.Current = true;
                setQuery(applied);
                EnterSearch();
            }
        }
    }
}

// <summary>
// 标题图标用的两个 Reactor 元素包装：原生 <see cref="Microsoft.UI.Xaml.Controls.FontIcon"/>
// 与 <see cref="Microsoft.UI.Xaml.Controls.ImageIcon"/>。
//
// 【为什么要有它们】Reactor 的 Core.IconElement 是按 IconData 现场解析并【创建】原生
// IconElement 子类的装饰元素；顶栏逐帧重渲染时（几何补间 16ms/帧）原生图标被反复
// 销毁重建，位图那支还要重新解码 —— 就是“标题图标闪”。这里把两个原生控件做成
// 【一等公民元素】：挂载一次，之后只走属性 diff（Glyph / Source），永不重建。
//
// 用法：FontIconElement.FontIcon(glyph: …) / ImageIconElement.ImageIcon(source: …)。
// 工厂名与 Factories 里的 FontIcon(...) / ImageIcon(...)（那两个返回的是 IconData，
// 不是元素）同名但挂在不同类型上 —— 必须写成 <c>XxxElement.FontIcon(...)</c> 以免歧义。
//
// 由 Reactor.Wrappers.Generator 在编译期生成另一半（属性 + ControlDescriptor +
// 处理器注册）；声明处只写这一行，不手写任何包装代码。
// </summary>
[GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.FontIcon))]
internal partial record FontIconElement;

[GenerateReactorWrapper(typeof(Microsoft.UI.Xaml.Controls.ImageIcon))]
internal partial record ImageIconElement;
