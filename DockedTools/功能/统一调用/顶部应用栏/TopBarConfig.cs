using System;
using System.Collections.Generic;
using System.Linq;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

// ============================================================================
// 顶栏契约（2026-10-01 重设计）
//
// 只有两样东西，方向相反、互不重叠：
//
//   下行【快照】TopBarSnapshot —— app 转接层 → 顶栏。整份、不可变、幂等、值语义。
//     顶栏是纯函数：同样的快照永远渲染出同样的样子，不含任何"记住上次"的行为。
//     想改任何东西，转接层重发一整份新快照，不要发增量命令。
//
//   上行【事件】TopBarEvent —— 顶栏 → app 转接层。顶栏只说"发生了什么"，
//     绝不说"该做什么"。是返回还是刷新、要不要弹菜单、搜索词怎么用，
//     全部由转接层在收到事件后自己决定，并（如果需要）重发一份新快照。
//
// 为什么契约里【没有任何委托】：委托按引用比较，一旦进契约，
// "重发快照 → 判定变化 → 重渲染 → 再重发"的闭环就复活了。回调改走事件上行。
// ============================================================================

/// <summary>
/// 底衬意图（顶栏真正消费的字段）：Transparent = 底衬透明、空白区点击穿透到下层页面；
/// Visible = 期望有底衬、空白区拦截点击。
///
/// 注意这是【意图】不是实现：顶栏自己不画任何材质，它把意图再转出成
/// <see cref="TopBarEvent.Material"/>，真正的材质由订阅方在别处实现。
/// </summary>
public enum TopBarBackdrop
{
    Transparent,
    Visible,
}

/// <summary>
/// 材质【提示】：转接层希望用哪种材质，顶栏原样转出，不解释、不实现。
/// None = 不要材质。日后要加 Mica 之类只需加成员。
/// </summary>
public enum TopBarMaterial
{
    None,
    Acrylic,
}

/// <summary>顶栏自身的独立主题模式（不跟随应用/系统全局主题）。</summary>
public enum TopBarThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// 按钮状态。用枚举而不是 bool：日后加 Busy / Progress 只需加成员，不改任何签名；
/// 渲染侧对未知状态按 <see cref="Enabled"/> 处理，向前兼容。
/// </summary>
public enum TopBarButtonState
{
    /// <summary>可用（默认）：可点击，点击照常抛事件。</summary>
    Enabled = 0,

    /// <summary>禁用：呈禁用外观，不接收点击、不抛事件。</summary>
    Disabled,
}

/// <summary>顶栏上固定 Id（转接层在事件里据此分派行为）。</summary>
public static class TopBarIds
{
    /// <summary>返回按钮。转接层收到 <c>Clicked(Back)</c> 后自己决定是不是"返回"。</summary>
    public const string Back = "__back";

    /// <summary>顶栏自带的搜索按钮（点开搜索框），不是转接层下发的普通按钮。</summary>
    public const string Search = "__search";
}

/// <summary>
/// 动画图标【描述】。只含字符串 —— 值语义天然成立，快照比较不受影响。
///
/// 为什么不直接抱 <c>IAnimatedVisualSource2</c>：那是 WinRT 对象，按引用比较。
/// 一旦进契约，"每次翻译都 new 一个源"会让快照永远判定为变化 ⇒ 必然的重渲染闭环。
/// 这里只存键名，真正的源由渲染侧按键解析并缓存（见 <c>TopBarAnimatedSources</c>）。
/// </summary>
/// <param name="SourceKey">动画源键（一般是 WinUI 内置 AnimatedVisual 源的类名）。</param>
/// <param name="FallbackGlyph">
/// 动不起来（系统禁用动画 / 源缺失）时显示的静态字形，默认 Setting 齿轮。
/// </param>
public sealed record TopBarAnimatedIcon(string SourceKey, string FallbackGlyph = "\uE713");

/// <summary>
/// 顶栏上的一个按钮。
/// <b>Id 必须全局唯一且跨次下发保持稳定</b>（左右两组共用一个命名空间）：
/// 顶栏靠 Id 认出"哪一个被删了 / 哪一个还在"，从而只摘掉该摘的那个、保留其它按钮的
/// 视觉状态。Id 重复就退化成按下标匹配 —— 删第一个按钮时，第二个会顶上它的位置和
/// 状态，禁用态与动画态都会跑到错的按钮上。
/// Tooltip = 悬停提示；不填则与 Glyph 同文（见 <see cref="Of"/>）。
///
/// 图标二选一：<see cref="Glyph"/>（静态）或 <see cref="Animated"/>（动画）。
/// 两者都给时按【动画】渲染，Glyph 自动降级为动画的 fallback 字形 ——
/// 卸载/动态效果被系统关掉时看到的就是它，不会变成空白。
/// </summary>
public sealed record TopBarButton(
    string Id,
    string Glyph,
    string Tooltip = "",
    TopBarButtonState State = TopBarButtonState.Enabled,
    TopBarAnimatedIcon? Animated = null)
{
    /// <summary>静态图标按钮；省略 Tooltip 时以 Glyph 作为提示。</summary>
    public static TopBarButton Of(
        string id,
        string glyph,
        string? tooltip = null,
        TopBarButtonState state = TopBarButtonState.Enabled) =>
        new(id, glyph, tooltip ?? glyph, state);

    /// <summary>
    /// 动画图标按钮。<paramref name="sourceKey"/> 是
    /// <see cref="TopBarAnimatedIcon.SourceKey"/>（WinUI 内置源的类名）。
    /// </summary>
    /// <param name="fallbackGlyph">静态回落字形；不填用动画的默认齿轮。</param>
    public static TopBarButton OfAnimated(
        string id,
        string sourceKey,
        string? tooltip = null,
        string? fallbackGlyph = null,
        TopBarButtonState state = TopBarButtonState.Enabled) =>
        new(id, fallbackGlyph ?? DefaultGlyph, tooltip ?? sourceKey, state,
            new TopBarAnimatedIcon(sourceKey, fallbackGlyph ?? DefaultGlyph));

    private const string DefaultGlyph = "\uE713";
}

/// <summary>
/// 居中位图标：二选一 —— 【字形】或【位图】。
///
/// 名字不写死"标题"：Title 与 Search 两种形态共用这一支（搜索框同样可以带图标），
/// 叫 TitleIcon 会让 Search 形态下的图标显得名不正言不顺。
///
/// 为什么是联合类型而不是两个并列字段：居中位只有一个图标，"同时给出两种"是无意义
/// 的输入，用类型把它排除掉；日后加第三种（Stream / byte[]）只需加一个派生 record。
///
/// 为什么带 string 隐式转换：让 <c>icon: "Favorite"</c> 继续成立。隐式转换只往【字形】
/// 这一支走 —— 字符串历来就是字形名，改成"字符串即路径"会静默改变老页面的含义。
/// </summary>
public abstract record TopBarCenterIcon
{
    /// <summary>字形图标：Segoe Fluent Icons 的枚举名（如 "Favorite"）或码点字符。</summary>
    // 注：工厂不能叫 Glyph/Bitmap —— 派生 record 的位置参数会【继承查找】同名成员，
    // 撞上静态方法就报 CS8866。
    public static TopBarCenterIcon OfGlyph(string glyph) => new GlyphCenterIcon(glyph);

    /// <summary>位图图标：应用内 / 本地文件的 Uri，渲染时压到 16×16。</summary>
    public static TopBarCenterIcon OfBitmap(Uri source) => new BitmapCenterIcon(source);

    /// <summary>位图图标（字符串重载）：接受 "ms-appx:///…" 或绝对文件路径。</summary>
    public static TopBarCenterIcon OfBitmap(string source) => new BitmapCenterIcon(ToUri(source));

    public static implicit operator TopBarCenterIcon?(string? glyph) =>
        glyph is null ? null : new GlyphCenterIcon(glyph);

    public static implicit operator TopBarCenterIcon?(Uri? source) =>
        source is null ? null : new BitmapCenterIcon(source);

    private static Uri ToUri(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(source, UriKind.Relative);
}

/// <summary>字形标题图标：走既有的 SymbolIconData 路径。</summary>
public sealed record GlyphCenterIcon(string Glyph) : TopBarCenterIcon;

/// <summary>
/// 位图标题图标。
/// 注：渲染侧用 ImageIcon（而不是 BitmapIcon）—— BitmapIconSource.ShowAsMonochrome
/// 默认 true，会把彩色图压成单色剪影，这里要的是原图。
/// </summary>
public sealed record BitmapCenterIcon(Uri Source) : TopBarCenterIcon;

/// <summary>
/// 搜索态右缘那枚「确认」按钮的图标形态。
///
/// 和 <see cref="TopBarCenterIcon"/> 一个套路：联合类型而不是"一堆 bool + 若干 nullable 字符串"，
/// 因为"既是静态字形又指定动画源"是无意义输入 —— 类型直接排除掉。
///
/// 三种形态：
///   <see cref="GlyphAcceptIcon"/>    静态字形（Segoe Fluent Icons 枚举名或码点），最省事；
///   <see cref="AnimatedAcceptIcon"/> 动画图标，按【键名】引用 WinUI 内置的那一套，
///                                    键不认识时自动回落到静态字形（不会变空白框）；
///   <see cref="NoAcceptIcon"/>       不要这枚按钮（纯回车提交）。
///
/// null 也是合法输入，含义是"用默认形态"（= 动画放大镜），这样老页面的
/// <c>new TopBarCenter.Search(text, icon, placeholder)</c> 三参写法不受影响。
///
/// 日后加第四种（自定义 IAnimatedVisualSource2 实例之类）只需加一个派生 record，
/// 签名一行都不动。
/// </summary>
public abstract record TopBarAcceptIcon
{
    /// <summary>静态字形：Segoe Fluent Icons 的枚举名（如 "Accept"）或码点字符。</summary>
    public static TopBarAcceptIcon OfGlyph(string glyph) => new GlyphAcceptIcon(glyph);

    /// <summary>动画图标：键名见 <c>TopBarAnimatedSources</c> 的注册表。</summary>
    /// <param name="fallbackGlyph">静态回落字形；不填用默认的放大镜。</param>
    public static TopBarAcceptIcon OfAnimated(string sourceKey, string? fallbackGlyph = null) =>
        new AnimatedAcceptIcon(sourceKey, fallbackGlyph);

    /// <summary>默认形态：动画放大镜（强调"这是在搜索"）。</summary>
    public static TopBarAcceptIcon Find { get; } =
        new AnimatedAcceptIcon(SearchAcceptKeys.Find, SearchAcceptKeys.FindFallback);

    /// <summary>动画对勾（强调"确认 / 保存"，适合当作输入框的提交按钮）。</summary>
    public static TopBarAcceptIcon Accept { get; } =
        new AnimatedAcceptIcon(SearchAcceptKeys.Accept, SearchAcceptKeys.AcceptFallback);

    /// <summary>动画右箭头（强调"发送 / 前进"）。</summary>
    public static TopBarAcceptIcon Send { get; } =
        new AnimatedAcceptIcon(SearchAcceptKeys.Send, SearchAcceptKeys.SendFallback);

    /// <summary>不显示这枚按钮（输入框只能靠回车提交）。</summary>
    public static TopBarAcceptIcon None { get; } = new NoAcceptIcon();

    public static implicit operator TopBarAcceptIcon?(string? glyph) =>
        glyph is null ? null : new GlyphAcceptIcon(glyph);

    /// <summary>
    /// 动画源键与回落字形的【唯一来源】。抽成常量是为了让工厂与回落【同源】：
    /// 改一处两边一起变，不会出现"动画是对的、回落画成别的图标"。
    /// </summary>
    public static class SearchAcceptKeys
    {
        public const string Find = "AnimatedFindVisualSource";
        public const string Accept = "AnimatedAcceptVisualSource";
        public const string Send = "AnimatedChevronRightDownSmallVisualSource";

        public const string FindFallback = "Find";
        public const string AcceptFallback = "Accept";
        public const string SendFallback = "Send";
    }
}

/// <summary>静态字形的确认图标：不动，外观最可预测。</summary>
public sealed record GlyphAcceptIcon(string Glyph) : TopBarAcceptIcon;

/// <summary>动画确认图标：键 + 回落字形（系统关闭动态效果时看到的就是回落那个）。</summary>
public sealed record AnimatedAcceptIcon(string SourceKey, string? FallbackGlyph = null) : TopBarAcceptIcon;

/// <summary>没有确认按钮。</summary>
public sealed record NoAcceptIcon : TopBarAcceptIcon;

/// <summary>
/// 居中位的三种互斥形态。用【类型】而不是"文本 + 一堆 bool 开关"表达，
/// 因为"既是标题又是搜索框"是没有意义的输入 —— 类型直接把它排除掉。
/// 日后加第四种（进度条之类）只需加一个派生 record，不改任何签名。
/// </summary>
public abstract record TopBarCenter
{
    /// <summary>居中位空着（只有左右两组按钮）。</summary>
    public sealed record None : TopBarCenter;

    /// <summary>标题：一行文字，左侧可选图标。</summary>
    public sealed record Title(string Text, TopBarCenterIcon? Icon = null) : TopBarCenter;

    /// <summary>
    /// 可搜索的标题：外观与 <see cref="Title"/> 一致，但点一下会展开成搜索框。
    /// Text 既是标题文本，也是输入框的【种子值】；Placeholder 是空态占位提示。
    ///
    /// 与 <see cref="Title"/> 唯一的区别就是【能不能点开搜索】—— 不需要额外的
    /// 布尔开关，形态本身就是开关。
    /// </summary>
    /// <param name="Text">标题 / 输入框的初始文本。</param>
    /// <param name="Icon">左侧可选图标。</param>
    /// <param name="Placeholder">输入框空态占位提示。</param>
    /// <param name="Accept">
    /// 右缘确认按钮的图标形态，见 <see cref="TopBarAcceptIcon"/>；null = 默认（动画放大镜）。
    /// 给 <see cref="TopBarAcceptIcon.None"/> 则不渲染这枚按钮（只能回车提交）。
    /// </param>
    /// <param name="LiveText">
    /// 是否把【每次按键后的文本】上抛成 <see cref="TopBarEvent.TextChanged"/>。
    /// 默认 false —— 理由见 <see cref="TopBarEvent.TextChanged"/>：打字通知会让页面在
    /// 每次按键后再下发一份快照，那是"每敲一个字全量重渲染一遍 3000 行 Render"，
    /// 必须由页面自己明确要。
    /// </param>
    public sealed record Search(
        string Text = "",
        TopBarCenterIcon? Icon = null,
        string Placeholder = "",
        TopBarAcceptIcon? Accept = null,
        bool LiveText = false) : TopBarCenter;

    /// <summary>居中位空着的现成实例（None 无字段，可以共享）。</summary>
    public static readonly None Empty = new();
}

/// <summary>
/// 顶栏的完整状态快照 —— 转接层下发给顶栏的【唯一】输入。
///
/// 硬约束：值语义（手写 Equals，列表逐项比较）。转接层每次都会新建一个实例，
/// 宿主以它判断是否变化；若按引用比较，"内容相同的新实例"会被判定为变化
/// ⇒ 重渲染 ⇒ 转接层再重发 ⇒ 死循环。
///
/// 不可变：所有字段 init-only，改动 = 发一份新快照（<c>with</c>）。
/// 幂等：同一份快照发一万次，结果完全一样。
/// </summary>
public sealed record TopBarSnapshot : IEquatable<TopBarSnapshot>
{
    /// <summary>未作任何配置时的样子：可见、底衬透明、无返回、无按钮、居中空、边距 12。</summary>
    public static TopBarSnapshot Empty { get; } = new();

    /// <summary>一份只显示返回按钮的快照（未配置页 / 回落用）。</summary>
    public static TopBarSnapshot Transparent { get; } =
        new() { Back = TopBarButton.Of(TopBarIds.Back, "Back", "返回") };

    /// <summary>
    /// 整栏是否可见。false = 内容淡出、只留返回按钮（返回不参与淡出，始终可点）。
    /// 顶栏自己会在它变化时做 220ms 淡入淡出。
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>底衬意图（要不要底衬、空白区拦不拦点击），见 <see cref="TopBarBackdrop"/>。</summary>
    public TopBarBackdrop Backdrop { get; init; } = TopBarBackdrop.Transparent;

    /// <summary>材质提示（想要哪种材质），顶栏原样转出，自己不实现。</summary>
    public TopBarMaterial Material { get; init; } = TopBarMaterial.None;

    /// <summary>顶栏独立主题，只作用于本控件子树。</summary>
    public TopBarThemeMode Theme { get; init; } = TopBarThemeMode.System;

    /// <summary>返回按钮；null = 不显示。默认 Id 见 <see cref="TopBarIds.Back"/>。</summary>
    public TopBarButton? Back { get; init; }

    /// <summary>左组按钮（返回按钮之后）；顺序即展示顺序。</summary>
    public IReadOnlyList<TopBarButton> Left { get; init; } = Array.Empty<TopBarButton>();

    /// <summary>居中位形态：空 / 标题 / 搜索框。</summary>
    public TopBarCenter Center { get; init; } = TopBarCenter.Empty;

    /// <summary>右组按钮；顺序即展示顺序。</summary>
    public IReadOnlyList<TopBarButton> Right { get; init; } = Array.Empty<TopBarButton>();

    /// <summary>左右两组按钮是否显示（false = 只留返回与居中位）。</summary>
    public bool ShowActions { get; init; } = true;

    /// <summary>左组与左边缘的间距（像素）。</summary>
    public double LeftPadding { get; init; } = 12;

    /// <summary>右组与右边缘的间距（像素）。</summary>
    public double RightPadding { get; init; } = 12;

    // ---- 便捷构造：保持嵌套结构，同时允许扁平书写 ----

    public static TopBarSnapshot Of(
        bool visible = true,
        TopBarBackdrop backdrop = TopBarBackdrop.Transparent,
        TopBarMaterial material = TopBarMaterial.None,
        TopBarThemeMode theme = TopBarThemeMode.System,
        TopBarButton? back = null,
        IReadOnlyList<TopBarButton>? left = null,
        TopBarCenter? center = null,
        IReadOnlyList<TopBarButton>? right = null,
        bool showActions = true,
        double padding = 12) => new()
        {
            Visible = visible,
            Backdrop = backdrop,
            Material = material,
            Theme = theme,
            Back = back,
            Left = left ?? Array.Empty<TopBarButton>(),
            Center = center ?? TopBarCenter.Empty,
            Right = right ?? Array.Empty<TopBarButton>(),
            ShowActions = showActions,
            LeftPadding = padding,
            RightPadding = padding,
        };

    public bool Equals(TopBarSnapshot? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return Visible == other.Visible
            && Backdrop == other.Backdrop
            && Material == other.Material
            && Theme == other.Theme
            && Back == other.Back
            && ShowActions == other.ShowActions
            && LeftPadding.Equals(other.LeftPadding)
            && RightPadding.Equals(other.RightPadding)
            && Center == other.Center
            && Left.SequenceEqual(other.Left)
            && Right.SequenceEqual(other.Right);
    }

    // 注：Equals(object) 由 record 合成（内部转调上面的强类型重载），这里只做值语义重载。

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Visible);
        hash.Add(Backdrop);
        hash.Add(Material);
        hash.Add(Theme);
        hash.Add(Back);
        hash.Add(ShowActions);
        hash.Add(LeftPadding);
        hash.Add(RightPadding);
        hash.Add(Center);
        foreach (var b in Left)
        {
            hash.Add(b);
        }
        foreach (var b in Right)
        {
            hash.Add(b);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// 顶栏上抛的事件：只说"发生了什么"，不说"该做什么"。
/// 全部带 Id 不带下标 —— 转接层按 Id 分派，不必知道按钮在第几位。
/// </summary>
public abstract record TopBarEvent
{
    /// <summary>
    /// 某个按钮被点击（含返回按钮，其 Id 为 <see cref="TopBarIds.Back"/>）。
    /// 走不到这里的点击：被禁用的按钮（顶栏已拦截）。
    /// </summary>
    public sealed record Clicked(string Id) : TopBarEvent;

    /// <summary>
    /// 输入框里的文本变了（<see cref="TopBarCenter.Search.LiveText"/> 打开后才发）。
    ///
    /// 和 <see cref="Submitted"/> 的关系：<b>TextChanged 是"正在输入"，Submitted 是"确认提交"</b>。
    /// 想要实时搜索建议、想在标题栏里直接改东西而不是"搜索"，用的都是它。
    ///
    /// ⚠️ 这是个【每次按键都发】的事件，务必懂得代价：页面收到后若把文本回写进
    /// <see cref="TopBarCenter.Search.Text"/>，就等于把手伸进了受控回路 —— 快照变化
    /// ⇒ 顶栏整份 Render 重跑。所以这是"要不要"的问题而不是"缺什么"的问题：
    /// 需要就显式打开 <see cref="TopBarCenter.Search.LiveText"/>，不需要就别开。
    ///
    /// 顶栏侧只发了文就撒手：不回写自己的 state、不等页面回应（否则未来任何一次
    /// 下行延迟都会让输入框卡顿）。
    /// </summary>
    public sealed record TextChanged(string Text) : TopBarEvent;

    /// <summary>搜索框提交（回车或点搜索按钮），参数为当前查询文本。</summary>
    public sealed record Submitted(string Text) : TopBarEvent;

    /// <summary>搜索态结束（失焦静置、或搜索态下点了返回）。</summary>
    public sealed record SearchExited : TopBarEvent;

    /// <summary>
    /// 材质意图：顶栏把快照里的底衬意图、材质提示与【自身实际状态】合成后转出。
    /// 顶栏不画任何材质，订阅方收到后在别处实现（宿主窗口 SystemBackdrop、
    /// 元素级 SystemBackdropElement、或一层原生 XAML 材质层均可）。
    /// 整栏隐藏（Visible=false）时 Wanted 为 false。
    /// </summary>
    public sealed record Material(bool Wanted, TopBarMaterial Kind) : TopBarEvent;
}
