using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 顶部应用栏服务：本项目对顶栏的<b>转接层</b>，同时也是「谁现在说了算」的裁判。
///
/// 【2026-10-02 重写：命令式搬 UI → 下发快照 → 按页面分作用域】
/// 三代演进：
///   ① XAML 时代：服务直接搬 UIElement（Left/Center/Right 三个 Panel），页面自己拼按钮样式；
///   ② 快照时代：服务保留一份"当前想要的样子"，合成一份 <see cref="TopBarSnapshot"/> 下发；
///   ③ 现在：<b>每页各持一份 <see cref="TopBarPageScope"/></b>，由本服务裁决谁在前台。
///
/// 本服务的职责因此收缩成三件事：
///   1. 决定全局部分 —— 返回按钮（<b>由导航层 CanGoBack 决定，页面无权干预</b>）、
///      菜单按钮（实验开关）；
///   2. 切换前台 —— 导航层告诉它"现在是哪个页面"，它把那个页面的 scope 压上
///      作用域栈顶、把上一个摘下来（见 <see cref="SetForegroundPage"/>）；
///   3. 转发 —— 老 API 名字保持不变，但统统转给当前前台 scope；
///      页面不必知道自己持有 scope，改动被压到最小。
///
/// 由此免费得到三条保证：切页自动清理、回到缓存页自动恢复、点击只送给前台页。
/// </summary>
public static class TopAppBarService
{
    // ── 宿主 / 通道 ─────────────────────────────────────────────
    private static MainWindowContent.ContentArea.ContentArea? _contentArea;
    private static TopBarChannel? _channel;

    // ── 全局部分（不属于任何页面）─────────────────────────────────
    // 返回按钮完全由 CanGoBack 决定 —— 页面没有任何途径改它。
    private static bool _menuVisible;

    /// <summary>菜单按钮的固定 Id（实验特性开关控制它是否下发）。</summary>
    private const string MenuButtonId = "__menu";

    // ── 每页一份作用域 ──────────────────────────────────────────
    // ConditionalWeakTable：页面实例一旦被 GC，它那份 scope 连同 state 一起消失，
    // 不会变成跨导航的内存堆积（缓存淘汰与页‮回收都自动生效）。
    private static readonly ConditionalWeakTable<object, TopBarPageScope> PageScopes = new();

    // 没有页面在前台时的承接者（内容区刚建好、还没导航的时候用的就是它）
    private static readonly TopBarPageScope FallbackScope = new(null);

    // 【写入目标】页面在自己的生命周期开头（OnNavigatedTo）先"认领"顶栏，之后的写入
    // 就落在这份 scope 上。为什么需要它：Frame 的事件顺序是
    //       ① Navigating → ② 页面构造 → ③ 页面 OnNavigatedTo → ④ Frame.Navigated
    // 而导航层要到 ④ 才知道"现在是谁前台"（见 SetForegroundPage）。也就是说 ③ 里所有
    // 顶栏写入（智能标题.Setup、SetTitle、SetRightIconButton…）若按"当前前台"路由，
    // 会全部落到【上一个页面】的 scope 上 —— 表现为：本页设了隐藏却仍有标题、
    // 设了按钮却在切走的瞬间消失。认领之后写入目标与前台身份解耦，各写各的。
    //
    // 这两个身份只用来【路由写入】，不是"我要留住这个页面"，所以是弱引用：
    // 页面被 GC / 被缓存淘汰后身份自动失效（getter 返回 null），写入随即落到
    // FallbackScope 上，而不是继续往一个已经销毁的页面 state 里写。
    // 用强引用时，NavigationCacheMode="Disabled" 的页面（如网页浏览页）从 Frame 移除后
    // 仍会被这里钉住 —— 整棵树连同 WebView2 一起不释放，直到下一次 SetForegroundPage；
    // 更糟的是那条"顶栏按钮回调打到已清理页面"的路径（点击 → FindAction → 页面方法
    // → 访问已 Close 的 WebView2）：只要某次 Navigated 没走到 SetForegroundPage，
    // 陈旧的认领就一直指向那个已经 Unload 干净的页面。
    private static WeakReference<object>? _foregroundPageRef;
    private static WeakReference<object>? _writingPageRef;

    private static object? _foregroundPage
    {
        get => _foregroundPageRef is { } r && r.TryGetTarget(out var p) ? p : null;
        set => _foregroundPageRef = value is null ? null : new WeakReference<object>(value);
    }

    private static object? _writingPage
    {
        get => _writingPageRef is { } r && r.TryGetTarget(out var p) ? p : null;
        set => _writingPageRef = value is null ? null : new WeakReference<object>(value);
    }

    /// <summary>
    /// 当前说了算的作用域：页面认领过就写它那一份，否则写前台那份。
    /// 所有对外 API 都往这里转 —— 这是唯一写入路径。
    /// </summary>
    private static TopBarPageScope Current => Scope(_writingPage ?? _foregroundPage);

    /// <summary>
    /// 【页面专用】在 OnNavigatedTo 开头认领顶栏（通常由 <c>智能标题.Setup</c> 代劳）。
    /// 认领后本页对顶栏的全部写入都记在自己名下，不必等待导航层切换前台身份。
    ///
    /// <b>没有 ScrollViewer 的页面必须自己调一次</b>：它们用不了 <c>智能标题</c>
    /// （如网页浏览页这类沉浸式页面），而认领平时是它代做的。忘了认领，本页在
    /// OnNavigatedTo 里的写入就会落到【上一个页面】的作用域上 —— 本页顶栏空白，
    /// 而上一个页面反过来带着本页的标题与按钮。
    /// </summary>
    public static void EnterPage(object page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _writingPage = page;
    }

    /// <summary>
    /// 此刻的顶栏【写入目标】是不是这个页面。页面在<b>异步回调</b>里改顶栏之前先问一句。
    ///
    /// 为什么需要它：像"网页标题变了"这种回调会在页面已经切走之后才跑，那时写入目标
    /// 已经是新前台页 —— 直接下发会<b>永久覆盖新页的标题</b>（state 是跟页面长期存活的）。
    /// 认领期（_writingPage == page）与已上台（_foregroundPage == page）都算命中，
    /// 所以本页首帧的写入不会被自己挡掉。
    /// </summary>
    public static bool IsWritingTarget(object page) =>
        ReferenceEquals(_writingPage ?? _foregroundPage, page);

    /// <summary>
    /// 【顶栏功能模块内部专用】绕过"前台路由"直接取某个页面自己的作用域。
    /// 给 <c>智能标题</c> 这类知道宿主的 helper 用：它在 OnNavigatedFrom 里收尾时，
    /// 认领权可能已经属于下一个页面，按 Current 改写会误伤人家刚设好的标题。
    /// </summary>
    internal static TopBarPageScope ScopeFor(object page) => Scope(page);

    private static TopBarPageScope Scope(object? page) =>
        page is null ? FallbackScope : PageScopes.GetValue(page, static _ => new TopBarPageScope(_));

    /// <summary>注册内容区实例（由链接器初始化时调用），并取用它的那一条通道。</summary>
    public static void Register(MainWindowContent.ContentArea.ContentArea contentArea)
    {
        _contentArea = contentArea;
        Attach(contentArea.TopBarChannel);
        contentArea.TopBarDoubleTapped += OnTopBarDoubleTapped;
    }

    /// <summary>
    /// 【导航层专用】切换前台页面。
    /// 先让新页上台（避免出现没有 Owner 的空档导致顶栏闪成 Empty），
    /// 再把旧页摘下来 —— 此时旧页已不在栈顶，摘除不会伤到任何人。
    /// </summary>
    public static void SetForegroundPage(object? page)
    {
        if (ReferenceEquals(_foregroundPage, page))
        {
            return;
        }

        // 前台身份既已确定，写入目标交还给"前台"这一常规路由
        _writingPage = null;

        var previous = Scope(_foregroundPage);
        _foregroundPage = page;

        if (_channel is null)
        {
            return;
        }

        Scope(page).Attach(_channel);
        previous.Detach();
    }

    /// <summary>页面被缓存淘汰时显式收尾（不调也不会泄漏，只是早点放手）。</summary>
    public static void DisposePageScope(object? page)
    {
        if (page is not null && PageScopes.TryGetValue(page, out var scope))
        {
            PageScopes.Remove(page);
            scope.Dispose();
        }
    }

    private static void Attach(TopBarChannel channel)
    {
        if (ReferenceEquals(_channel, channel))
        {
            return;
        }

        Scope(_foregroundPage).Detach();   // 旧通道里的凭证先作废
        _channel = channel;
        Scope(_foregroundPage).Attach(channel);
    }

    // ── 全局部分：返回按钮与菜单按钮 ────────────────────────────

    private static TopBarButton BackButton => TopBarButton.Of(TopBarIds.Back, "Back", "返回");

    /// <summary>
    /// 返回按钮：<b>只看 CanGoBack</b>。页面没有任何 API 能盖住这个判定 ——
    /// 顶栏上的返回与窗口左上角的返回必须永远一致，否则用户会困惑于"为什么这边能退那边不能"。
    /// </summary>
    internal static TopBarButton? ComposeBackButton() => CanGoBack ? BackButton : null;

    /// <summary>
    /// 全局左前缀（排在页面自己的左组之前）。目前只有菜单按钮，
    /// 由实验开关驱动，同样不属于页面能管的东西。
    /// </summary>
    internal static IReadOnlyList<TopBarButton> GlobalLeftPrefix =>
        _menuVisible
            ? new[] { TopBarButton.Of(MenuButtonId, "GlobalNavigationButton", "菜单") }
            : Array.Empty<TopBarButton>();

    /// <summary>全局状态变了（返回栈、菜单开关）：让前台 scope 重发一份快照。</summary>
    private static void PublishCurrent() => Current.Publish();

    // ── 上行事件收口 ────────────────────────────────────────────

    internal static void HandleScopedEvent(TopBarEvent topBarEvent)
    {
        switch (topBarEvent)
        {
            case TopBarEvent.Clicked clicked:
                // Id 分派：只在前台页自己的 Actions 里找，旧页的回调收不到
                // （通道只把事件送给 Owner，这里再收一层保险）
                if (clicked.Id == TopBarIds.Back)
                {
                    BackButtonClicked?.Invoke(null, EventArgs.Empty);
                }
                else if (clicked.Id == MenuButtonId)
                {
                    MenuButtonClicked?.Invoke(null, EventArgs.Empty);
                }
                else if (Current.FindAction(clicked.Id) is { } action)
                {
                    action();
                }
                break;

            case TopBarEvent.Submitted submitted:
                SearchSubmitted?.Invoke(typeof(TopAppBarService), submitted.Text);
                break;

            case TopBarEvent.TextChanged changed:
                // 两条并行路线，各有各的用途：
                //   · 页面自己的回调（跟随前台 scope）—— 日常就该用它，页面失宠自动失联；
                //   · 静态全局事件 —— 给"不在前台也要听"的订阅方（如全局搜索历史记录）。
                // 两条都发，收不收由订阅方决定；顶栏侧不关心谁在听。
                SearchTextChanged?.Invoke(typeof(TopAppBarService), changed.Text);
                Current.TextChanged?.Invoke(changed.Text);
                break;

            case TopBarEvent.SearchExited:
                break;

            case TopBarEvent.Material:
                // 顶栏不画材质：需要落地的一方订阅 TopBarMessageHub。
                break;
        }
    }

    // ── 对外 API：名字不变，统统转给前台 scope ──────────────────

    /// <summary>显示或隐藏整条顶栏（组件内部自带淡入淡出）。</summary>
    public static bool IsVisible
    {
        get => Current.Visible;
        set => Current.Visible = value;
    }

    /// <summary>底衬是否可见；false = 内容浮在页面背景上、空白区点击穿透（默认）。</summary>
    public static void SetChromeVisible(bool visible) => Current.ChromeVisible = visible;

    /// <summary>复位到底衬的默认状态（= 透明）。</summary>
    public static void ResetChromeVisibility() => Current.ChromeVisible = false;

    /// <summary>
    /// 【滚动联动专用】顶栏整条<b>浮现 / 收回</b>（见 <see cref="TopBarPageScope.SetEmerged"/>）：
    /// false = 页面在顶部，顶栏整条不显示、也没有底衬（页面大标题在此时显示）；
    /// true  = 页面滚动过，顶栏连亚克力底衬一起出现，接管页面大标题的角色。
    ///
    /// 页面的滚动处理必须走这一个入口 —— 只切 IsVisible 的话，浮现出来的顶栏没有
    /// 亚克力（底衬意图没跟着翻），正是"向上滚动后亚克力不出来"的根因。
    /// </summary>
    public static void SetEmerged(bool emerged) => Current.SetEmerged(emerged);

    /// <summary>设置居中位形态（标题 / 搜索 / 空）。</summary>
    public static void SetCenter(TopBarCenter center) => Current.SetCenter(center);

    /// <summary>居中位显示标题（可选字形/位图图标）。</summary>
    public static void SetTitle(string text, TopBarCenterIcon? icon = null) =>
        Current.SetCenter(new TopBarCenter.Title(text, icon));

    /// <summary>
    /// 【智能标题专用】把标题直接写进【指定页面自己】的作用域，绕过前台 / 认领路由。
    /// 为什么需要它：页面大标题的文本（x:Uid 本地化）完全可能在 Setup 之后才落地，
    /// 那时认领权可能已经转给下一个页面 —— 按 Current 路由会写到别人那份 state 上。
    /// 而 scope 本来就是本页自己的 state，直接写它永远是对的，显示与否由前台身份决定。
    /// </summary>
    internal static void SetTitleFor(object page, string text)
    {
        var scope = Scope(page);

        // 页面已经把居中位认领走了就别再写 —— 否则会把它设的搜索框覆盖回纯标题。
        // 这不是时序问题而是所有权问题，理由见 TopBarPageScope.SuppressSmartTitle。
        if (scope.SuppressSmartTitle)
        {
            return;
        }

        scope.SetCenter(new TopBarCenter.Title(text));
    }

    /// <summary>
    /// 明确宣称"本页的居中位由我自己管"，智能标题此后再也不往这里写页面大标题。
    /// 要在 setCenter 之前调 —— 次序错了那一次写入就已经抢先发生了。
    /// </summary>
    public static void SetSmartTitleSuppressed(bool suppressed) =>
        Current.SuppressSmartTitle = suppressed;

    /// <summary>居中位显示可点开搜索的标题。</summary>
    public static void SetSearchableTitle(string text, string placeholder = "", TopBarCenterIcon? icon = null) =>
        Current.SetCenter(new TopBarCenter.Search(text, icon, placeholder));

    /// <summary>
    /// 居中位开一个【实时回传文本】的搜索框：每敲一个字就回调 <paramref name="onTextChanged"/>。
    ///
    /// 与 <see cref="SetSearchableTitle"/> 的区别只有两点：LiveText 打开（这是本方法的全部意义），
    /// 以及右侧确认按钮的图标可以换（<see cref="TopBarAcceptIcon"/>，不填仍是动画放大镜）。
    ///
    /// 回调注册在【当前前台页的 scope】上，跟着页面一起失宠 —— 切走之后不会有人替它应答。
    /// 不写回自己的 state：顶栏那边的文本是它自己的（详见
    /// <see cref="TopBarEvent.TextChanged"/> 注释）；页面想把输入框内容改回去，
    /// 用 <see cref="SetSearchableTitle"/> 重新给一份 Text 即可（[todo] 未来若需要
    /// "由页面完全受控"，那是另一回事，届时要在顶栏侧加单向 inhibit 标记）。
    /// </summary>
    public static void SetLiveSearchTitle(
        string text,
        Action<string> onTextChanged,
        TopBarAcceptIcon? accept = null,
        string placeholder = "",
        TopBarCenterIcon? icon = null)
    {
        Current.TextChanged = onTextChanged;
        Current.SetCenter(new TopBarCenter.Search(text, icon, placeholder, accept, LiveText: true));
    }

    /// <summary>
    /// 撤掉实时文本回调（保留搜索框本身）。
    /// 页面用不着了就该调它 —— scope 是长期存活的，回调挂着不放等于钉住页面实例。
    /// </summary>
    public static void ClearLiveTextCallback() => Current.TextChanged = null;

    /// <summary>清空居中位。</summary>
    public static void ClearCenter() => Current.SetCenter(TopBarCenter.Empty);

    /// <summary>左组按钮（返回按钮之后）；null/空 = 不显示。</summary>
    public static void SetLeftButtons(IReadOnlyList<TopBarButton>? buttons) => Current.SetLeftButtons(buttons);

    /// <summary>右组按钮；null/空 = 不显示。</summary>
    public static void SetRightButtons(IReadOnlyList<TopBarButton>? buttons) => Current.SetRightButtons(buttons);

    /// <summary>右组设置一枚图标按钮（保存/完成这类最常见的用法）。</summary>
    public static void SetRightIconButton(string id, string glyph, Action onClick, string? tooltip = null) =>
        Current.SetRightIconButton(id, glyph, onClick, tooltip);

    /// <summary>
    /// 注册/覆盖某个 Id 的点击回调。委托放在这里而不是按钮数据里 ——
    /// 进契约会让值语义比较失效（见 TopBarPageScope 注释）。
    /// </summary>
    public static void RegisterAction(string id, Action action) => Current.RegisterAction(id, action);

    /// <summary>注销某个 Id 的点击回调。</summary>
    public static void UnregisterAction(string id) => Current.UnregisterAction(id);

    /// <summary>按 Id 改按钮可用/禁用（禁用态外观与点击拦截由顶栏负责）。</summary>
    public static void SetButtonEnabled(string id, bool enabled) => Current.SetButtonEnabled(id, enabled);

    /// <summary>清空当前页面对顶栏的全部要求。</summary>
    public static void ClearAll() => Current.Clear();

    #region 顶栏按钮控制

    /// <summary>当前 Frame 是否可以返回。</summary>
    public static bool CanGoBack => _contentArea?.CanGoBack ?? false;

    /// <summary>
    /// 【导航层专用】返回栈变了之后刷新返回按钮 —— 它是唯一能改变返回按钮的东西。
    /// </summary>
    public static void RefreshBackButton() => PublishCurrent();

    /// <summary>菜单按钮是否显示（实验特性开关驱动）。</summary>
    public static void SetMenuButtonVisible(bool visible)
    {
        if (_menuVisible == visible)
        {
            return;
        }
        _menuVisible = visible;
        PublishCurrent();
    }

    /// <summary>返回按钮被点击（由顶栏上行的 <see cref="TopBarEvent.Clicked"/> 转出）。</summary>
    public static event EventHandler? BackButtonClicked;

    /// <summary>菜单按钮被点击。</summary>
    public static event EventHandler? MenuButtonClicked;

    /// <summary>搜索框提交（回车 / 点确认按钮）。</summary>
    public static event EventHandler<string>? SearchSubmitted;

    /// <summary>
    /// 输入框里的文本【每次变化】都会敲这里（只有 <see cref="TopBarCenter.Search.LiveText"/>
    /// 打开时才发）。想做实时搜索建议、或把顶栏输入框当成"改东西的地方"而不是
    /// "搜索框"，用的都是它。
    ///
    /// ⚠️ 代价见 <see cref="TopBarEvent.TextChanged"/>：每敲一个字都意味着一次跨层投递，
    /// 页面若再把文本回写进快照，就是一次全量重渲染。需要才开。
    /// </summary>
    public static event EventHandler<string>? SearchTextChanged;

    /// <summary>顶部应用栏中间空白区域双击事件（供窗口做最大化/还原）。</summary>
    public static event EventHandler? TopBarDoubleTapped;

    private static void OnTopBarDoubleTapped(object? sender, EventArgs e) =>
        TopBarDoubleTapped?.Invoke(sender, e);

    #endregion

    #region 页面大标题

    /// <summary>注册页面大标题元素，滚动时统一控制其淡入淡出。</summary>
    public static void SetPageTitle(UIElement? element) => _contentArea?.SetPageTitle(element);

    /// <summary>设置页面大标题的显示状态（带动画）。</summary>
    public static void SetPageTitleVisible(bool visible) => _contentArea?.SetPageTitleVisible(visible);

    #endregion

    #region 局部主题控制

    /// <summary>设置顶栏的局部主题（Default = 跟随系统）。</summary>
    public static void SetTheme(ElementTheme theme) =>
        Current.ThemeMode = theme switch
        {
            ElementTheme.Light => TopBarThemeMode.Light,
            ElementTheme.Dark => TopBarThemeMode.Dark,
            _ => TopBarThemeMode.System,
        };

    /// <summary>在亮 / 暗之间切换。</summary>
    public static void ToggleTheme()
    {
        Current.ThemeMode = GetRequestedTheme() == ElementTheme.Dark
            ? TopBarThemeMode.Light
            : TopBarThemeMode.Dark;
    }

    /// <summary>顶栏请求的主题。</summary>
    public static ElementTheme GetRequestedTheme() => Current.ThemeMode switch
    {
        TopBarThemeMode.Light => ElementTheme.Light,
        TopBarThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// 顶栏实际生效的主题：显式 Light/Dark 即它本身，System 按系统当前亮暗推导
    /// （等价于旧版读 ActualTheme）。
    /// </summary>
    public static ElementTheme GetActualTheme()
    {
        var requested = GetRequestedTheme();
        if (requested != ElementTheme.Default)
        {
            return requested;
        }

        var background = new Windows.UI.ViewManagement.UISettings()
            .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return (background.R * 299 + background.G * 587 + background.B * 114) / 1000 < 128
            ? ElementTheme.Dark
            : ElementTheme.Light;
    }

    #endregion
}
