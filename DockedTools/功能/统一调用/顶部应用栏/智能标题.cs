using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 智能标题：一行代码完成顶栏标题 + 大标题注册 + 滚动显隐联动
/// 用法：在 OnNavigatedTo 中调用 智能标题.Setup(...)，在 OnNavigatedFrom 中调用 智能标题.Cleanup()
/// </summary>
public sealed class 智能标题
{
    // ── 浮现阈值的【死区 + 滞后】（px）────────────────────────────
    //   · 进入：滚过 EnterEmergePx ⇒ 顶栏带亚克力接管、大标题淡出；
    //   · 退出：退到 ExitEmergePx 以内 ⇒ 顶栏收回、大标题回来；
    //   · 中间那段是死区：滚动停在阈值附近不会来回切换（否则顶栏会闪）。
    //
    // 为什么不能用 offset > 0：回到顶部是个【浮点过程】。惯性 / 回弹的末段经常停在
    // 0.4、1.6 这类亚像素值上，"大于 0" 就永远算"滚过了" —— 明明已经到顶，
    // 顶栏却还挂着、大标题还藏着（用户看到的就是这个）。
    private const double EnterEmergePx = 8;
    private const double ExitEmergePx = 2;

    private ScrollViewer? _scrollViewer;
    private Page? _page;
    private bool _titleVisible = true;
    // 顶栏是否已"浮现"（滚动过 = true）。与 _titleVisible 互补：
    // 页面在顶部时显示大标题、顶栏收回；滚动后顶栏带着亚克力浮现、大标题淡出。
    private bool _emerged;

    // 结算是否已排进队列（见 QueueSettle）
    private bool _settleQueued;

    // 页面大标题元素 + 它的 Text 订阅令牌（见 Setup 处注释）
    private Microsoft.UI.Xaml.Controls.TextBlock? _titleElement;
    private long _titleTextToken;

    /// <summary>
    /// 初始化智能标题，绑定滚动视图和大标题元素，顶栏标题自动从大标题读取
    /// </summary>
    /// <param name="page">宿主页面实例（通常是 <c>this</c>）——必须在 OnNavigatedTo 里就认领，
    /// 否则此刻的顶栏写入会记到上一个页面名下（见 TopAppBarService.EnterPage）。</param>
    /// <param name="scrollViewer">页面的 ScrollViewer</param>
    /// <param name="pageTitleElement">页面大标题 TextBlock（滚动时淡出，顶栏标题从其 Text 读取）</param>
    public void Setup(Page page, ScrollViewer scrollViewer, TextBlock pageTitleElement)
    {
        _scrollViewer = scrollViewer;
        _page = page;
        _emerged = false;

        // 先认领：本页后面所有顶栏写入（含 SetTitle）都落到自己那份 scope 上，
        // 不等导航层切换前台身份。
        TopAppBarService.EnterPage(page);

        // 再把本页自己的 state 归零：清上一轮的内容残留，并把顶栏收回到"未浮现"态
        //（没有亚克力、居中位不写字 —— 页面在顶部，这句话由大标题来说）。
        // 注意收回只收这两样：返回按钮与左右图标是常驻铬，不跟着滚动走。
        TopAppBarService.ClearAll();
        TopAppBarService.SetEmerged(false);

        // 标题主演两个角色：① 顶栏居中文本的【数据源】；② 滚动淡出/淡入的动画目标。
        // 前者必须跟住 Text 的【变化】，不能只在 Setup 时读一次 —— 页面的大标题普遍用
        // x:Uid（本地化资源）写文本，而 x:Uid 的落地时机不在我们的控制之内：有些页面的
        // ResourceCandidate 是到 XAML 加载后期才生效的，Setup 跑在 OnNavigatedTo，此刻
        // element.Text 完全可能还是空串。旧写法在这里做了「空则等 Loaded」，但 Loaded
        // 早在本次 Setup 之前就抛过了 ⇒ 那次订阅永远等不到 ⇒ 顶栏一条的文本位始终空着
        //（表现就是：浮现出来了、亚克力也在，就是没字）。改监听 Text 依赖属性后，无论
        // 本地化什么时候落地、页面后来又改了几次标题，顶栏都跟得上。
        // 标题直接写进【本页自己的】作用域，不走前台/认领路由：
        // 文本可能在本页早已失宠之后才落地，按 Current 路由会写到别人那份 state 上。
        void ApplyCenterTitle()
        {
            var text = pageTitleElement.Text;
            if (!string.IsNullOrEmpty(text))
            {
                TopAppBarService.SetTitleFor(page, text);
            }
        }

        DetachTitleText();
        _titleElement = pageTitleElement;
        _titleTextToken = pageTitleElement.RegisterPropertyChangedCallback(
            Microsoft.UI.Xaml.Controls.TextBlock.TextProperty, OnPageTitleTextChanged);
        ApplyCenterTitle();

        TopAppBarService.SetPageTitle(pageTitleElement);
        _scrollViewer.ViewChanged += OnScrollViewerViewChanged;

        // 别假设"刚进来一定在顶部"：缓存页被恢复时滚动位置可能还停在中间，而 Frame
        // 恢复滚动位置未必再抛一次 ViewChanged —— 那时就成了"人在中间、却顶着大标题"。
        // 进来先按当下的位移对一次，再排一次结算兜住"位置稍后才恢复"的情况。
        ApplyScrollState(_scrollViewer.VerticalOffset);
        QueueSettle();
    }

    private void OnPageTitleTextChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_page is not null && _titleElement is { } el && !string.IsNullOrEmpty(el.Text))
        {
            TopAppBarService.SetTitleFor(_page, el.Text);
        }
    }

    private void DetachTitleText()
    {
        if (_titleElement is { } el && _titleTextToken != 0)
        {
            el.UnregisterPropertyChangedCallback(
                Microsoft.UI.Xaml.Controls.TextBlock.TextProperty, _titleTextToken);
        }
        _titleElement = null;
        _titleTextToken = 0;
    }

    public void Cleanup()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
            _scrollViewer = null;
        }

        // 标志复位：留在 true 会让下一轮 Setup 少排一次结算。
        // 已入队的那次回调自己会认出 _scrollViewer 没了并直接返回。
        _settleQueued = false;

        TopAppBarService.SetPageTitle(null);
        // 文本订阅必须摘：页面实例若是被缓存的，下次 Setup 会重新挂一条，
        // 不摘就会一条旧的 + 一条新的同时往同一份 state 里写
        DetachTitleText();

        // 收尾只动【自己那份】state：按 Current 改写会误伤下一个页面刚设好的内容
        // （OnNavigatedFrom 发生在导航层切换前台之前，认领权此时已可能属于下一页）。
        if (_page is not null)
        {
            TopAppBarService.ScopeFor(_page).Clear();
        }

        _page = null;
        _titleVisible = true;
        _emerged = false;
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // 即时响应：滚动过程中越过阈值就立刻切换，不等静止
        ApplyScrollState(sv.VerticalOffset);

        // 再排一次【结算】，兜住 "最后一次 ViewChanged 早于 offset 落定" 那种情况
        QueueSettle();
    }

    /// <summary>
    /// 按当前滚动位移决定"浮现 / 收回"。幂等 —— 状态没跨过阈值就什么都不做，
    /// 所以无论是滚动中每帧调用、还是结算时再调一次，都不会抖动或重复下发。
    /// </summary>
    private void ApplyScrollState(double offset)
    {
        // 死区 + 滞后，判据见本类顶部那两个常量的注释
        var scrolled = _emerged ? offset > ExitEmergePx : offset > EnterEmergePx;

        // 顶栏浮现/收回：必须走 SetEmerged —— 它一次翻"亚克力底衬"与"居中位文本"两样，
        // 只改底衬的话收回去的是一条光秃秃的标题（字还在、背景没了）；
        // 更不能去翻整栏 Visible —— 那会把左右图标一起抽走，而图标与滚动无关。
        if (scrolled != _emerged)
        {
            _emerged = scrolled;
            TopAppBarService.SetEmerged(scrolled);
        }

        if (scrolled == _titleVisible)
        {
            _titleVisible = !scrolled;
            TopAppBarService.SetPageTitleVisible(!scrolled);
        }
    }

    /// <summary>
    /// 排一次【结算】：滚动停下之后的那一帧，再读一次真实位移复核状态。
    ///
    /// <b>为什么需要它。</b><c>ViewChanged</c> 不保证在滚动【真正终止】时再抛最后一次 ——
    /// 惯性衰减到极慢速度、回弹被新的输入打断、或滚动条拖拽松手，都会出现
    /// "最后一次回调时的 offset 还不是终值，之后再没有回调了"。状态就此卡在旧值上：
    /// 表现就是"下拉一段距离后顶栏卡住"、"明明回到了顶部却还藏着大标题"。
    /// 这不是我们能改的 WinUI 行为，只能在自己这边补一次复核。
    ///
    /// 为什么不干脆改成"只在静止时判定"：<c>e.IsIntermediate=false</c> 同样不保证派发，
    /// 而且那样浮现会滞后到滚动结束，手感是错的。这里【即时 + 兜底】两条路并存，
    /// 兜底那次因为 ApplyScrollState 幂等，重复执行没有副作用。
    ///
    /// 入队时机也正好：滚动繁忙时 dispatcher 队列里排着各帧的 ViewChanged，
    /// 结算被排在它们之后 —— 天然就是"静止后那一帧"。
    /// </summary>
    private void QueueSettle()
    {
        if (_settleQueued || _scrollViewer is not { } sv)
        {
            return;
        }

        if (sv.DispatcherQueue is not { } queue)
        {
            return;
        }

        _settleQueued = true;
        queue.TryEnqueue(() =>
        {
            _settleQueued = false;

            // 已经 Cleanup（或换了页面）就什么都不做
            if (_scrollViewer is not { } current)
            {
                return;
            }

            ApplyScrollState(current.VerticalOffset);
        });
    }
}
