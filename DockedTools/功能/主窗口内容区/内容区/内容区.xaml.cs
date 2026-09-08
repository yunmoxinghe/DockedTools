using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Pages.WebApp.Browser;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml.Media.Animation;

namespace DockedTools.Features.MainWindowContent.ContentArea
{
    public sealed partial class ContentArea : UserControl
    {
        private const float DefaultCornerRadius = 4f;
        private const float PinnedCornerRadius = 8f;
        private float _currentCornerRadius = DefaultCornerRadius;
        private CompositionRoundedRectangleGeometry? _clipGeometry;
        private CompositionRoundedRectangleGeometry? _gridClipGeometry;
        private CompositionRoundedRectangleGeometry? _backdropClipGeometry;
        private readonly PageCacheManager _pageCacheManager;
        private readonly NavigationService _navigationService;

        public event EventHandler<NavigationEventArgs>? Navigated;

        /// <summary>
        /// 顶部应用栏中间空白区域双击事件
        /// </summary>
        public event EventHandler? TopBarDoubleTapped;

        /// <summary>
        /// 缓存页面导航完成事件（缓存命中时 Frame 不触发 Navigated，由此事件补充通知）
        /// </summary>
        public event EventHandler<(Type PageType, object? Parameter)>? CachedPageNavigated;

        /// <summary>
        /// 当前显示的页面类型
        /// </summary>
        public Type? CurrentPageType => _navigationService.CurrentPageType;

        /// <summary>
        /// 当前显示的页面参数
        /// </summary>
        public object? CurrentPageParameter => _navigationService.CurrentPageParameter;

        /// <summary>
        /// 是否可以返回（Frame 内置 BackStack）
        /// </summary>
        public bool CanGoBack => _navigationService.CanGoBack;

        /// <summary>
        /// 返回上一页（使用缓存机制）
        /// </summary>
        public void GoBack()
        {
            _navigationService.GoBack();
        }

        private const double TopBarHeight = 48.0;

        /// <summary>
        /// 获取覆盖层容器，用于添加通用控件和装饰
        /// </summary>
        public Grid OverlayContainer => OverlayLayer;

        /// <summary>
        /// 顶部应用栏独立控件
        /// </summary>
        public TopAppBarControl TopAppBar => TopAppBarHost;

        /// <summary>
        /// 顶部应用栏背景容器，保留给需要定制背景的页面使用
        /// </summary>
        public Grid TopAppBarBackground => TopAppBarHost.AppBarBackground;

        /// <summary>
        /// 顶部应用栏左侧面板
        /// </summary>
        public StackPanel TopBarLeft => TopAppBarHost.LeftContentPanel;

        /// <summary>
        /// 顶部应用栏右侧面板
        /// </summary>
        public StackPanel TopBarRight => TopAppBarHost.RightContentPanel;

        /// <summary>
        /// 顶部应用栏中间内容
        /// </summary>
        public ContentPresenter TopBarCenter => TopAppBarHost.CenterContent;

        /// <summary>
        /// 显示或隐藏顶部应用栏（带淡入淡出动画）
        /// </summary>
        public bool IsTopBarVisible
        {
            get => TopAppBarHost.IsAppBarVisible;
            set => TopAppBarHost.IsAppBarVisible = value;
        }

        private UIElement? _pageTitle;

        /// <summary>
        /// 注册页面大标题元素，滚动时由服务统一控制其淡入淡出
        /// </summary>
        public void SetPageTitle(UIElement? element)
        {
            _pageTitle = element;
        }

        /// <summary>
        /// 设置页面大标题的显示状态（带动画）
        /// </summary>
        public void SetPageTitleVisible(bool visible)
        {
            if (_pageTitle is null) return;

            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = visible ? 0.0 : 1.0,
                To = visible ? 1.0 : 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(visible ? 200 : 150)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = visible
                        ? Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                        : Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn
                }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, _pageTitle);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }

        public ContentArea()
        {
            System.Diagnostics.Debug.WriteLine("[ContentArea] 构造函数开始");
            
            InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[ContentArea] InitializeComponent 完成");
            
            _pageCacheManager = new PageCacheManager(maxCacheSize: 20);
            _pageCacheManager.PageAutoRemoved += OnPageAutoRemoved;
            System.Diagnostics.Debug.WriteLine("[ContentArea] PageCacheManager 初始化完成");
            
            _navigationService = new NavigationService(ContentFrame, _pageCacheManager);
            _navigationService.Navigated += OnNavigationServiceNavigated;
            _navigationService.CachedPageNavigated += OnNavigationServiceCachedPageNavigated;
            System.Diagnostics.Debug.WriteLine("[ContentArea] NavigationService 初始化完成");
            
            ContentGrid.Loaded += ContentGrid_Loaded;
            TopAppBarHost.BackButtonClicked += TopAppBarHost_BackButtonClicked;
            TopAppBarHost.MenuButtonClicked += TopAppBarHost_MenuButtonClicked;
            System.Diagnostics.Debug.WriteLine("[ContentArea] 事件订阅完成");
            
            // 订阅 WebViewManager 的淘汰事件
            Pages.WebApp.Browser.WebViewManager.WebViewEvicted += OnWebViewEvicted;
            System.Diagnostics.Debug.WriteLine("[ContentArea] WebViewManager 事件订阅完成");
            
            // 初始化 Frame 动画
            UpdateFrameAnimation();
            System.Diagnostics.Debug.WriteLine("[ContentArea] Frame 动画初始化完成");
            
            // 订阅设置变化事件
            Pages.Settings.SettingsPage.FrameAnimationSettingsChanged += OnFrameAnimationSettingsChanged;
            Pages.Settings.SettingsPage.ContentAreaBackdropSettingsChanged += OnContentAreaBackdropSettingsChanged;
            System.Diagnostics.Debug.WriteLine("[ContentArea] 设置事件订阅完成");
            
            // 初始化背景材质
            Loaded += (s, e) => 
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] Loaded 事件触发，开始应用背景设置");
                ApplyBackdropSettings();
                System.Diagnostics.Debug.WriteLine("[ContentArea] 背景设置应用完成");
            };
            
            System.Diagnostics.Debug.WriteLine("[ContentArea] 构造函数完成");
        }

        #region 顶栏按钮控制

        /// <summary>
        /// 菜单按钮点击事件
        /// </summary>
        public event EventHandler? MenuButtonClicked;

        /// <summary>
        /// 智能刷新返回按钮：根据 CanGoBack 自动显示/隐藏（带淡入淡出动画）。
        /// 返回按钮在独立的第四层，不依赖顶栏背景容器，顶栏显隐由页面自行控制。
        /// </summary>
        public void RefreshBackButton()
        {
            TopAppBarHost.SetBackButtonVisible(ContentFrame.CanGoBack);
        }

        /// <summary>
        /// 强制设置返回按钮可见性，用于页面自行管理顶部栏时覆盖默认行为。
        /// </summary>
        public void SetBackButtonVisible(bool visible)
        {
            TopAppBarHost.SetBackButtonVisible(visible);
        }

        /// <summary>
        /// 设置菜单按钮的可见性
        /// </summary>
        public void SetMenuButtonVisible(bool visible)
        {
            TopAppBarHost.SetMenuButtonVisible(visible);
        }

        /// <summary>
        /// 设置更多按钮的可见性
        /// </summary>
        public void SetMoreButtonVisible(bool visible)
        {
            TopAppBarHost.SetMoreButtonVisible(visible);
        }

        /// <summary>
        /// 获取更多按钮的菜单，用于动态添加菜单项
        /// </summary>
        public MenuFlyout? GetMoreMenu()
        {
            return TopAppBarHost.MoreMenu;
        }

        private void TopAppBarHost_BackButtonClicked(object? sender, EventArgs e)
        {
            // 优先让当前页面接管返回逻辑
            if (_navigationService.CurrentPage is IBackHandler handler && handler.OnBackRequested())
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] 返回被页面接管");
                return;
            }

            // 页面未接管，执行默认返回（使用缓存机制）
            System.Diagnostics.Debug.WriteLine("[ContentArea] 执行默认返回");
            GoBack();
        }

        private DateTime _lastTopBarTapTime = DateTime.MinValue;
        private const double TopBarDoubleTapMaxMs = 400;

        private void ContentGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(ContentGrid).Position;
            // 只响应顶部栏高度范围内（48px）
            if (pt.Y > TopBarHeight) return;
            // 排除左右按钮区域（各约 8px margin + 按钮宽度，粗略排除两端 56px）
            if (pt.X < 56 || pt.X > ContentGrid.ActualWidth - 56) return;

            var now = DateTime.Now;
            if ((now - _lastTopBarTapTime).TotalMilliseconds <= TopBarDoubleTapMaxMs)
            {
                TopBarDoubleTapped?.Invoke(this, EventArgs.Empty);
                _lastTopBarTapTime = DateTime.MinValue; // 重置，避免三击再触发
            }
            else
            {
                _lastTopBarTapTime = now;
            }
        }

        private void TopAppBarHost_MenuButtonClicked(object? sender, EventArgs e)
        {
            MenuButtonClicked?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        private void OnFrameAnimationSettingsChanged(object? sender, EventArgs e)
        {
            // 设置改变时更新 Frame 动画
            UpdateFrameAnimation();
        }

        private void UpdateFrameAnimation()
        {
            var animationType = ExperimentalSettings.FrameNavigationAnimation;
            var transitionInfo = GetNavigationTransitionInfo(animationType);
            
            // 更新 Frame 的 ContentTransitions
            var transition = new NavigationThemeTransition
            {
                DefaultNavigationTransitionInfo = transitionInfo
            };
            
            // 确保 TransitionCollection 不为空，避免性能问题
            if (ContentFrame.ContentTransitions == null)
            {
                ContentFrame.ContentTransitions = new TransitionCollection();
            }
            
            // 清除旧的过渡效果并添加新的
            // 注意：频繁修改 ContentTransitions 可能影响性能，建议在设置更改时调用
            ContentFrame.ContentTransitions.Clear();
            ContentFrame.ContentTransitions.Add(transition);
            
            System.Diagnostics.Debug.WriteLine($"[ContentArea] Frame 动画已更新为: {animationType}");
        }

        private NavigationTransitionInfo GetNavigationTransitionInfo(FrameAnimationType animationType)
        {
            return animationType switch
            {
                FrameAnimationType.None => new SuppressNavigationTransitionInfo(),
                FrameAnimationType.EntranceTransition => new EntranceNavigationTransitionInfo(),
                FrameAnimationType.SlideFromRight => new SlideNavigationTransitionInfo 
                { 
                    Effect = SlideNavigationTransitionEffect.FromRight 
                },
                FrameAnimationType.SlideFromLeft => new SlideNavigationTransitionInfo 
                { 
                    Effect = SlideNavigationTransitionEffect.FromLeft 
                },
                FrameAnimationType.SlideFromBottom => new SlideNavigationTransitionInfo 
                { 
                    Effect = SlideNavigationTransitionEffect.FromBottom 
                },
                FrameAnimationType.DrillIn => new DrillInNavigationTransitionInfo(),
                FrameAnimationType.FadeInOut => CreateFadeTransition(),
                FrameAnimationType.ScaleAnimation => CreateScaleTransition(),
                _ => new EntranceNavigationTransitionInfo()
            };
        }

        /// <summary>
        /// 创建淡入淡出过渡效果
        /// </summary>
        private NavigationTransitionInfo CreateFadeTransition()
        {
            // WinUI 3 中没有直接的 FadeTransitionInfo，我们使用 EntranceTransition 作为替代
            // EntranceTransition 包含了淡入效果
            return new EntranceNavigationTransitionInfo();
        }

        /// <summary>
        /// 创建缩放过渡效果
        /// </summary>
        private NavigationTransitionInfo CreateScaleTransition()
        {
            // WinUI 3 中没有直接的 ScaleTransitionInfo，我们使用 DrillIn 作为替代
            // DrillIn 有轻微的缩放效果
            return new DrillInNavigationTransitionInfo();
        }

        private void OnPageAutoRemoved(object? sender, string cacheKey)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] 页面被 LRU 自动移除: {cacheKey}");
            
            // PageCacheManager 已经调用了 DisposeWebView，这里只需要记录日志
            // WebView 的 Unlink 由 WebBrowserPage.DisposeWebView 自动处理
        }

        /// <summary>
        /// WebView 被 LRU 淘汰事件处理
        /// </summary>
        private void OnWebViewEvicted(object? sender, Pages.WebApp.Browser.WebViewEvictedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] WebView 被 LRU 淘汰: {e.InstanceId}");
            
            // ⭐ 直接从 LRU 缓存中删除，不调用 DisposeWebView（已经被 WebViewManager 调用过了）
            string cacheKey = $"WebBrowserPage_{e.InstanceId}";
            bool removed = _pageCacheManager.RemovePageWithoutDispose(cacheKey);
            
            if (removed)
            {
                System.Diagnostics.Debug.WriteLine($"[ContentArea] 已从 PageCache 删除: {cacheKey}");
            }
        }

        private void ContentGrid_Loaded(object sender, RoutedEventArgs e)
        {
            // 为 ContentGrid 应用圆角裁切
            ApplyGridClip();
            
            // 为 BackdropContainer 应用圆角裁切
            ApplyBackdropClip();
        }

        private void ApplyGridClip()
        {
            var visual = ElementCompositionPreview.GetElementVisual(ContentGrid);
            var compositor = visual.Compositor;
            
            _gridClipGeometry = compositor.CreateRoundedRectangleGeometry();
            _gridClipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
            _gridClipGeometry.Offset = Vector2.Zero;
            _gridClipGeometry.Size = new Vector2((float)ContentGrid.ActualWidth, (float)ContentGrid.ActualHeight);
            
            var clip = compositor.CreateGeometricClip(_gridClipGeometry);
            visual.Clip = clip;
            
            // 启用抗锯齿（通过设置 visual 的合成模式）
            visual.IsPixelSnappingEnabled = false; // 禁用像素对齐以获得更平滑的边缘
            
            System.Diagnostics.Debug.WriteLine($"[ContentArea] Applied grid clip with anti-aliasing: Size={_gridClipGeometry.Size}, CornerRadius={_gridClipGeometry.CornerRadius}");
        }

        private void ApplyBackdropClip()
        {
            var visual = ElementCompositionPreview.GetElementVisual(BackdropContainer);
            var compositor = visual.Compositor;
            
            _backdropClipGeometry = compositor.CreateRoundedRectangleGeometry();
            _backdropClipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
            _backdropClipGeometry.Offset = Vector2.Zero;
            _backdropClipGeometry.Size = new Vector2((float)BackdropContainer.ActualWidth, (float)BackdropContainer.ActualHeight);
            
            var clip = compositor.CreateGeometricClip(_backdropClipGeometry);
            visual.Clip = clip;
            
            // 启用抗锯齿
            visual.IsPixelSnappingEnabled = false;
            
            System.Diagnostics.Debug.WriteLine($"[ContentArea] Applied backdrop clip with anti-aliasing: Size={_backdropClipGeometry.Size}, CornerRadius={_backdropClipGeometry.CornerRadius}");
        }

        public void SetCornerRadius(bool isPinned)
        {
            _currentCornerRadius = isPinned ? PinnedCornerRadius : DefaultCornerRadius;
            ContentBorder.CornerRadius = new CornerRadius(_currentCornerRadius);
            
            System.Diagnostics.Debug.WriteLine($"[ContentArea] SetCornerRadius: isPinned={isPinned}, radius={_currentCornerRadius}");
            
            // 确保 Grid 的裁切几何体已创建
            if (_gridClipGeometry == null && ContentGrid.ActualWidth > 0 && ContentGrid.ActualHeight > 0)
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] Grid clip geometry not created yet, creating now");
                ApplyGridClip();
            }
            
            // 确保 Backdrop 的裁切几何体已创建
            if (_backdropClipGeometry == null && BackdropContainer.ActualWidth > 0 && BackdropContainer.ActualHeight > 0)
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] Backdrop clip geometry not created yet, creating now");
                ApplyBackdropClip();
            }
            
            // 更新 Frame 的裁切
            if (_clipGeometry != null)
            {
                _clipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
                System.Diagnostics.Debug.WriteLine($"[ContentArea] Updated Frame clip corner radius: {_clipGeometry.CornerRadius}");
            }
            
            // 更新 Grid 的裁切
            if (_gridClipGeometry != null)
            {
                _gridClipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
                System.Diagnostics.Debug.WriteLine($"[ContentArea] Updated Grid clip corner radius: {_gridClipGeometry.CornerRadius}");
            }
            
            // 更新 Backdrop 的裁切
            if (_backdropClipGeometry != null)
            {
                _backdropClipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
                System.Diagnostics.Debug.WriteLine($"[ContentArea] Updated Backdrop clip corner radius: {_backdropClipGeometry.CornerRadius}");
            }
            
            // 同时更新 SystemBackdropElement 的 XAML CornerRadius 属性
            MicaBaseBackdropLayer.CornerRadius = new CornerRadius(_currentCornerRadius);
            MicaAltBackdropLayer.CornerRadius = new CornerRadius(_currentCornerRadius);
            AcrylicBackdropLayer.CornerRadius = new CornerRadius(_currentCornerRadius);
            
            // ✅ 通知圆角变化事件
            UnifiedCalls.ContentArea.ContentAreaService.NotifyCornerRadiusChanged(ContentBorder.CornerRadius);
        }
        
        /// <summary>
        /// 获取当前圆角
        /// </summary>
        public CornerRadius GetCurrentCornerRadius()
        {
            return ContentBorder.CornerRadius;
        }

        public void Navigate(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pageType,
            object? parameter = null,
            Microsoft.UI.Xaml.Media.Animation.NavigationTransitionInfo? transitionInfo = null)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] Navigate 被调用: {pageType.Name}");
            
            // 为 AI 页面设置特殊的反向钻取动画
            NavigationTransitionInfo? customTransition = transitionInfo; // 外部传入优先
            if (customTransition == null && pageType.Name == "AIPage")
            {
                customTransition = new DrillInNavigationTransitionInfo();
            }
            
            // 使用导航服务进行导航
            _navigationService.Navigate(pageType, parameter, customTransition);
        }

        private void OnNavigationServiceNavigated(object? sender, NavigationEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] NavigationService.Navigated 事件触发: {e.SourcePageType.Name}");
            
            // 如果是 WebBrowserPage，订阅关闭事件
            if (ContentFrame.Content is WebBrowserPage webBrowserPage)
            {
                webBrowserPage.PageCloseRequested += OnPageCloseRequested;
            }

            // 智能刷新返回按钮
            RefreshBackButton();
            
            // 转发导航事件
            Navigated?.Invoke(this, e);
        }

        private void OnNavigationServiceCachedPageNavigated(object? sender, (Type PageType, object? Parameter) e)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] NavigationService.CachedPageNavigated 事件触发: {e.PageType.Name}");
            
            // 智能刷新返回按钮
            RefreshBackButton();
            
            // 转发缓存导航事件
            CachedPageNavigated?.Invoke(this, e);
        }

        private void OnPageCloseRequested(object? sender, string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] 收到页面关闭请求: {shortcutId}");
            
            // 触发关闭事件，通知 Linker
            PageCloseRequested?.Invoke(this, shortcutId);
        }

        // 页面关闭请求事件
        public event EventHandler<string>? PageCloseRequested;

        /// <summary>
        /// 移除指定的缓存页面
        /// </summary>
        public void RemoveCachedPage(string shortcutId)
        {
            string cacheKey = $"WebBrowser_{shortcutId}";
            
            // PageCacheManager.RemovePage 会自动调用 DisposeWebView
            // DisposeWebView 会自动调用 WebViewManager.Unlink
            _pageCacheManager.RemovePage(cacheKey);
            System.Diagnostics.Debug.WriteLine($"[ContentArea] 移除缓存页面: {shortcutId}");
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public int GetCachedPageCount() => _pageCacheManager.CachedPageCount;

        /// <summary>
        /// 重启当前标签页（销毁并重建 WebView）
        /// </summary>
        public async System.Threading.Tasks.Task RestartCurrentTabAsync()
        {
            System.Diagnostics.Debug.WriteLine("[ContentArea] RestartCurrentTabAsync 被调用");
            
            // 检查当前页面是否是 WebBrowserPage
            if (_navigationService.CurrentPage is not WebBrowserPage currentWebBrowserPage)
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] 当前页面不是 WebBrowserPage，无法重启");
                return;
            }

            // 获取当前页面的 shortcutId
            string? currentShortcutId = null;
            
            // 从缓存管理器中找到当前页面的缓存键
            foreach (var cacheKey in _pageCacheManager.GetCachedPageKeys())
            {
                var cachedPage = _pageCacheManager.GetCachedPage(cacheKey);
                if (ReferenceEquals(cachedPage, currentWebBrowserPage))
                {
                    // 从缓存键提取 shortcutId
                    if (cacheKey.StartsWith("WebBrowser_"))
                    {
                        currentShortcutId = cacheKey.Substring("WebBrowser_".Length);
                    }
                    break;
                }
            }

            if (currentShortcutId == null)
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] 无法找到当前标签的 shortcutId");
                return;
            }

            // 调用新方法重启指定标签
            await RestartTabAsync(currentShortcutId);
        }

        /// <summary>
        /// 重启指定的标签页（销毁并重建 WebView）
        /// ⭐ 新增方法：支持重启非当前显示的标签页
        /// </summary>
        public async System.Threading.Tasks.Task RestartTabAsync(string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentArea] RestartTabAsync 被调用: {shortcutId}");
            
            string cacheKey = $"WebBrowser_{shortcutId}";

            // 从存储中加载 shortcut
            var shortcuts = await WebAppShortcutStore.LoadAsync();
            var shortcut = shortcuts.FirstOrDefault(s => s.Id == shortcutId);

            if (shortcut == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ContentArea] 无法找到 shortcut: {shortcutId}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[ContentArea] 准备重启标签: {shortcut.Name} ({shortcut.Id})");

            // ⭐ 修复 Bug: 先清理旧实例，再导航创建新实例
            // Step 1: 移除旧的缓存页面（会自动调用 DisposeWebView 和 Unlink）
            bool wasRemoved = _pageCacheManager.RemovePage(cacheKey);
            if (wasRemoved)
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] 已清理旧实例");
                
                // 给一点时间让旧实例完全释放
                await System.Threading.Tasks.Task.Delay(100);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ContentArea] 旧实例不存在，直接创建新实例");
            }

            // Step 2: 重新导航到同一个页面（会创建新实例）
            System.Diagnostics.Debug.WriteLine("[ContentArea] 创建新实例");
            Navigate(typeof(WebBrowserPage), shortcut);
            
            System.Diagnostics.Debug.WriteLine("[ContentArea] 标签重启完成");
        }

        private void ContentFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            // 更新 Grid 的裁切大小
            if (_gridClipGeometry != null)
            {
                _gridClipGeometry.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            }
            
            // 更新 Backdrop 的裁切大小（假设与 ContentGrid 同尺寸）
            if (_backdropClipGeometry != null)
            {
                _backdropClipGeometry.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            }

            // 更新 Frame 的裁切
            var visual = ElementCompositionPreview.GetElementVisual(ContentFrame);
            if (_clipGeometry == null)
            {
                var compositor = visual.Compositor;
                _clipGeometry = compositor.CreateRoundedRectangleGeometry();
                _clipGeometry.CornerRadius = new Vector2(_currentCornerRadius, _currentCornerRadius);
                _clipGeometry.Offset = Vector2.Zero;
                visual.Clip = compositor.CreateGeometricClip(_clipGeometry);
                
                // 启用抗锯齿
                visual.IsPixelSnappingEnabled = false;
            }

            _clipGeometry.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }

        private void OnContentAreaBackdropSettingsChanged(object? sender, EventArgs e)
        {
            ApplyBackdropSettings();
        }

        private void ApplyBackdropSettings()
        {
            var backdropType = ExperimentalSettings.ContentAreaBackdrop;
            
            System.Diagnostics.Debug.WriteLine($"[ContentArea] Applying backdrop: {backdropType}");
            
            // 隐藏所有背景层
            SolidColorBackdrop.Visibility = Visibility.Collapsed;
            MicaBaseBackdropLayer.Visibility = Visibility.Collapsed;
            MicaAltBackdropLayer.Visibility = Visibility.Collapsed;
            AcrylicBackdropLayer.Visibility = Visibility.Collapsed;
            
            // 根据设置显示对应的背景层
            switch (backdropType)
            {
                case ContentAreaBackdropType.SolidColor:
                    SolidColorBackdrop.Visibility = Visibility.Visible;
                    break;
                    
                case ContentAreaBackdropType.MicaBase:
                    MicaBaseBackdropLayer.Visibility = Visibility.Visible;
                    break;
                    
                case ContentAreaBackdropType.MicaAlt:
                    MicaAltBackdropLayer.Visibility = Visibility.Visible;
                    break;
                    
                case ContentAreaBackdropType.DesktopAcrylic:
                    AcrylicBackdropLayer.Visibility = Visibility.Visible;
                    break;
            }
        }
    }
}
