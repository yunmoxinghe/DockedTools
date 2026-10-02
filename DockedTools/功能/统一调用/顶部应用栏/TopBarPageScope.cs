using System;
using System.Collections.Generic;
using System.Linq;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

// ============================================================================
// 一个页面对顶栏的【作用域】。
//
// 为什么要有这一层：契约（TopBarChannel + TopBarScope）本来就是为"多个页面各自登记一
// 份快照、按作用域栈交接所有权"而设计的。若所有页面共用一个全局单例服务的一条 scope，
// 这份能力等于没用：页面离开时没人交还所有权，它的标题/按钮会残留到下一个页面上，
// 只能靠每个页面自觉 ClearAll() 兜底。
//
// 于是把"页面想要的样子"下沉到这里：
//   · state 按【页面实例】长期存活（ConditionalWeakTable，页面被 GC 自动回收）；
//   · 前台身份按【谁在栈顶】决定：Attach 把自己压上栈顶，Detach 把自己摘下来。
// 由此得到的三条保证：
//   ① 切页时旧页自动失宠 —— 它的东西不会漏到新页上（迟到 Dispose 也伤不到别人）；
//   ② 回到缓存页时它的标题/按钮原样恢复（state 还在，只是重新压栈）；
//   ③ 点击不会错乱 —— 通道只把事件送给栈顶的 Owner，旧页的回调收不到。
//
// 页面不需要亲手 Dispose 它：由导航层切换前台身份，页面实例销毁时随 table 消失。
// ============================================================================
public sealed class TopBarPageScope : IDisposable
{
    // ── 页面"想要的样子"（只有页面自己关心的那部分）──────────────
    private bool _visible = true;
    // 【默认透明】没人显式要底衬就不画那 48px 亚克力（见 TopBarBackdrop 注释）
    private bool _chromeVisible;
    private TopBarThemeMode _themeMode = TopBarThemeMode.System;
    private TopBarCenter _center = TopBarCenter.Empty;
    private IReadOnlyList<TopBarButton> _left = Array.Empty<TopBarButton>();
    private IReadOnlyList<TopBarButton> _right = Array.Empty<TopBarButton>();

    // 本页的点击回调。跟着 scope 走：页面消失后不会有人替它应答。
    private readonly Dictionary<string, Action> _actions = new();

    // 本页对「输入框实时文本」的回调（TopBarCenter.Search.LiveText 打开后才会有）。
    // 也跟着 scope 走：走这条路由总是只想应答【当前这页】，跟着页面一起失宠。
    private Action<string>? _textChanged;

    /// <summary>
    /// 输入框文本每次变化时的回调。设 null 即取消订阅。
    ///
    /// 与 <see cref="RegisterAction"/> 不同：那是个按 Id 分派的字典，因为顶栏上可以同时
    /// 有很多按钮；而居中位【只有一个输入框】，做成集合只会让两个回调互相踩、
    /// 还不知道该轮到谁应答。
    /// </summary>
    public Action<string>? TextChanged
    {
        get => _textChanged;
        set => _textChanged = value;
    }

    // 登记到通道的所有权凭证；null = 当前没在前台
    private TopBarScope? _channelScope;

    internal TopBarPageScope(object? owner) => Owner = owner;

    /// <summary>本页面实例（fallback 作用域为 null）。</summary>
    public object? Owner { get; }

    /// <summary>本页是否正占据顶栏（在作用域栈顶）。</summary>
    public bool IsForeground => _channelScope is not null;

    // 本页是否已【明确认领】居中位（见 SuppressSmartTitle 的注释）
    private bool _smartTitleSuppressed;

    /// <summary>
    /// true = 本页要自己管居中位，智能标题（<c>智能标题</c>）不许再把页面大标题自动写进来。
    ///
    /// <b>为什么需要这么个开关。</b>智能标题盯的是页面大标题 TextBlock 的 <c>Text</c>
    /// 【变化】（Setup 时必须盯变化而不是读一次 —— 那串文本走 x:Uid 本地化，而 Setup
    /// 跑在 OnNavigatedTo，此刻多半还是空串）。由此形成一条【晚到的写入】：
    /// 页面在 Loaded 里 setCenter(Search) ⇒ 若干毫秒后本地化文本落地 ⇒ 智能标题
    /// 顺理成章地 setCenter(Title) ⇒ 页面刚设好的搜索框凭空消失。
    ///
    /// 这不是时序没调好，是<b>所有权没划清</b>：同一个槽位有两个主人，谁写谁赢、
    /// 还取决于本地化什么时候落地 —— 这类 bug 必然时灵时不灵。
    /// 所以正解不是"延后一点再设"，而是让页面能明说"这一位是我的"。
    ///
    /// <see cref="Clear"/> 会复位它：认领跟着"这一轮的内容"走，不该跨清屏延续。
    /// </summary>
    public bool SuppressSmartTitle
    {
        get => _smartTitleSuppressed;
        set => _smartTitleSuppressed = value;
    }

    // ── 前台交接 ────────────────────────────────────────────────

    /// <summary>把自己压上作用域栈顶（幂等）。上行事件仍由服务统一收口。</summary>
    internal void Attach(TopBarChannel channel)
    {
        if (_channelScope is not null)
        {
            return;
        }

        _channelScope = channel.Apply(Compose());
        _channelScope.OnEvent = TopAppBarService.HandleScopedEvent;
    }

    /// <summary>从栈顶摘下来（幂等）。不是 Owner 时摘除无害。</summary>
    internal void Detach()
    {
        var scope = _channelScope;
        _channelScope = null;
        scope?.Dispose();
    }

    public void Dispose()
    {
        Detach();
        _actions.Clear();
        _textChanged = null;
        _smartTitleSuppressed = false;
    }

    // ── 快照合成 ────────────────────────────────────────────────

    /// <summary>
    /// 本页的完整快照：自己想要的部分 + 由内容区/导航决定的全局部分
    /// （返回按钮由 CanGoBack 决定、菜单按钮由实验开关决定 —— 两者都不归页面管）。
    /// </summary>
    public TopBarSnapshot Compose()
    {
        var left = new List<TopBarButton>();
        left.AddRange(TopAppBarService.GlobalLeftPrefix);
        left.AddRange(_left);

        return TopBarSnapshot.Of(
            visible: _visible,
            // 底衬意图：必须先——整栏不可见时底衬跟着走
            backdrop: _visible && _chromeVisible ? TopBarBackdrop.Visible : TopBarBackdrop.Transparent,
            material: TopBarMaterial.Acrylic,
            theme: _themeMode,
            back: TopAppBarService.ComposeBackButton(),
            left: left,
            center: _center,
            right: _right,
            padding: 12);
    }

    /// <summary>把当前 state 重发一份快照（前台与否都不报错，只是不在前台时不产生效果）。</summary>
    internal void Publish() => _channelScope?.Update(Compose());

    // ── 页面能操作的那一小部分 ──────────────────────────────────

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }
            _visible = value;
            Publish();
        }
    }

    public bool ChromeVisible
    {
        get => _chromeVisible;
        set
        {
            if (_chromeVisible == value)
            {
                return;
            }
            _chromeVisible = value;
            Publish();
        }
    }

    /// <summary>
    /// 【滚动联动专用】把"整栏浮现 / 收回"作为一件事切换：
    /// 滚动前整栏不显示（也没有底衬），滚动后整栏连亚克力底衬一起出现。
    ///
    /// 为什么必须合成一个入口：分两次设 <see cref="Visible"/> 与
    /// <see cref="ChromeVisible"/> 会<b>各下发一份快照</b>，中间态（整栏可见但还没
    /// 有底衬）用户真的会看到一帧。蒸腾/收回必须是一次偏序的状态变更。
    /// </summary>
    public void SetEmerged(bool emerged)
    {
        if (_visible == emerged && _chromeVisible == emerged)
        {
            return;
        }

        _visible = emerged;
        _chromeVisible = emerged;
        Publish();
    }

    /// <summary>顶栏局部主题。</summary>
    public TopBarThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (_themeMode == value)
            {
                return;
            }
            _themeMode = value;
            Publish();
        }
    }

    public void SetCenter(TopBarCenter center)
    {
        if (_center == center)
        {
            return;
        }
        _center = center;
        Publish();
    }

    public void SetLeftButtons(IReadOnlyList<TopBarButton>? buttons)
    {
        _left = buttons ?? Array.Empty<TopBarButton>();
        Publish();
    }

    public void SetRightButtons(IReadOnlyList<TopBarButton>? buttons)
    {
        _right = buttons ?? Array.Empty<TopBarButton>();
        Publish();
    }

    /// <summary>右组设置一枚图标按钮（最常见用法，给个糖）。</summary>
    public void SetRightIconButton(string id, string glyph, Action onClick, string? tooltip = null)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _actions[id] = onClick;
        }
        SetRightButtons(new[] { TopBarButton.Of(id, glyph, tooltip) });
    }

    public void RegisterAction(string id, Action action)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _actions[id] = action;
        }
    }

    public void UnregisterAction(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _actions.Remove(id);
        }
    }

    /// <summary>按 Id 取本页注册的点击回调。</summary>
    public Action? FindAction(string id) => _actions.TryGetValue(id, out var a) ? a : null;

    /// <summary>按 Id 改按钮可用/禁用（禁用态外观与点击拦截由顶栏负责）。</summary>
    public void SetButtonEnabled(string id, bool enabled)
    {
        var state = enabled ? TopBarButtonState.Enabled : TopBarButtonState.Disabled;

        if (_left.FirstOrDefault(b => b.Id == id) is { } left)
        {
            _left = _left.Select(b => ReferenceEquals(b, left) ? b with { State = state } : b).ToArray();
            Publish();
            return;
        }

        if (_right.FirstOrDefault(b => b.Id == id) is { } right)
        {
            _right = _right.Select(b => ReferenceEquals(b, right) ? b with { State = state } : b).ToArray();
            Publish();
        }
    }

    /// <summary>
    /// 清空本页面对顶栏的<b>内容</b>要求（标题 / 左右按钮 / 底衬 / 局部主题）。
    ///
    /// 注意：<b>不动整栏显隐</b> —— "占不占顶栏"与"顶栏上放什么"是两件事，
    /// 混在一起会踩顺序坑（曾经 ClearAll() 把刚设好的 IsVisible=false 又拽回 true，
    /// 于是"默认透明"的页面一进去就把标题顶了出来）。
    /// </summary>
    public void Clear()
    {
        _actions.Clear();
        _textChanged = null;
        _smartTitleSuppressed = false;
        _chromeVisible = false;
        _themeMode = TopBarThemeMode.System;
        _center = TopBarCenter.Empty;
        _left = Array.Empty<TopBarButton>();
        _right = Array.Empty<TopBarButton>();
        Publish();
    }
}
