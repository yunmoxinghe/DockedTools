using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 智能标题：一行代码完成顶栏标题 + 大标题注册 + 滚动显隐联动
/// 用法：在 OnNavigatedTo 中调用 智能标题.Setup(...)，在 OnNavigatedFrom 中调用 智能标题.Cleanup()
/// </summary>
public sealed class 智能标题
{
    private ScrollViewer? _scrollViewer;
    private Page? _page;
    private bool _titleVisible = true;
    // 顶栏是否已"浮现"（滚动过 = true）。与 _titleVisible 互补：
    // 页面在顶部时显示大标题、顶栏收回；滚动后顶栏带着亚克力浮现、大标题淡出。
    private bool _emerged;

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

        // 再把本页自己的 state 归零：清上一轮的内容残留、把顶栏整条收回到"未浮现"态
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

        var scrolled = sv.VerticalOffset > 0;

        // 顶栏整条浮现/收回：必须走 SetEmerged —— 它同时翻"整栏可见"与"底衬可见"，
        // 只改 IsVisible 的话浮现出来的是一条没有亚克力的裸标题。
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
}
