using DockedTools.Features.MainWindowContent.ContentArea;
using DockedTools.Features.Pages.Home;
using DockedTools.Features.Pages.New;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp;
using DockedTools.Features.Pages.WebApp.Browser;
using DockedTools.Features.Pages.WebApp.Browser.Managers;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using NavBarControl = DockedTools.Features.MainWindowContent.NavigationBar.NavigationBar;
using NavRequest = DockedTools.Features.MainWindowContent.NavigationBar.NavigationRequest;

namespace DockedTools.Features.MainWindowContent.Linker
{
    public sealed partial class Linker : UserControl
    {
        public NavBarControl NavBarInstance => NavBar;

        // 导航历史由 ContentHost 的 Frame.BackStack 内置管理，无需自定义栈
        // 注意：此字段用于跟踪后退导航状态，虽然当前未读取，但保留用于未来扩展
#pragma warning disable CS0414
        private bool _isNavigatingBack = false;
#pragma warning restore CS0414
        private bool _isNavigationBarOnLeft;
        private bool _isPinnedOrMaximized;
        private double _contentTopMargin = 6;
        private double _contentOutsideMargin = 4;
        private const double CompactContentOutsideMargin = 4;
        private const double ContentNavigationSideMargin = 0;
        private const double NavigationGap = 2;
        private bool _isContentLoaded = false;

        public Linker()
        {
            InitializeComponent();
            TopAppBarService.Register(ContentHost);
            UnifiedCalls.ContentArea.ContentAreaService.Register(ContentHost);
            
            // ⭐ 不在构造函数中导航到首页，延迟到 LoadContent() 调用
            // ContentHost.Navigate(typeof(HomePage));
            
            ContentHost.Navigated += ContentHost_Navigated;
            ContentHost.CachedPageNavigated += ContentHost_CachedPageNavigated;
            ContentHost.PageCloseRequested += OnPageCloseRequested;
            NavBar.NavigationRequested += OnNavigationRequested;
            NavBar.ShortcutRemoved += OnShortcutRemoved;
            NavBar.WebAppRestartRequested += OnWebAppRestartRequested;
            NavBar.BackRequested += OnBackRequested;
            ContentHost.TopBarDoubleTapped += OnTopBarDoubleTapped;
            
            // 订阅 AI 实验室设置变化事件
            Pages.Settings.SettingsPage.AILabSettingsChanged += OnAILabSettingsChanged;
            Pages.Settings.SettingsPage.DockSideSettingsChanged += OnDockSideSettingsChanged;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            ApplyNavigationBarPlacement();
        }

        /// <summary>
        /// 加载内容（延迟初始化，在启动屏幕结束后调用）
        /// </summary>
        public void LoadContent()
        {
            if (_isContentLoaded)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[Linker] Loading content...");
            
            // ⭐ 修复 Bug: 解除 SelectionChanged 抑制，允许导航触发
            // 这样可以避免构造函数初始化时的过早导航
            if (NavBar != null)
            {
                // 使用反射或公共方法解除抑制（需要在 NavigationBar 中添加公共方法）
                NavBar.EnableNavigation();
            }
            
            // 导航到首页
            ContentHost.Navigate(typeof(HomePage));
            _isContentLoaded = true;
            
            System.Diagnostics.Debug.WriteLine("[Linker] Content loaded successfully");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnifiedCalls.ContentArea.ContentAreaService.Unregister();
            Pages.Settings.SettingsPage.AILabSettingsChanged -= OnAILabSettingsChanged;
            Pages.Settings.SettingsPage.DockSideSettingsChanged -= OnDockSideSettingsChanged;
            SizeChanged -= OnSizeChanged;
            Unloaded -= OnUnloaded;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateContentOutsideMargin(e.NewSize.Width);
            ApplyContentHostMargin();
        }

        private void OnAILabSettingsChanged(object? sender, EventArgs e)
        {
            // 通知导航栏更新 AI 导航项的可见性
            NavBar.UpdateAINavigationItemVisibility();
        }

        private void OnDockSideSettingsChanged(object? sender, EventArgs e)
        {
            ApplyNavigationBarPlacement();
        }

        private void ApplyNavigationBarPlacement()
        {
            bool placeOnLeft =
                ExperimentalSettings.DockSide == WindowDockSide.Left &&
                ExperimentalSettings.PlaceNavigationBarOnLeftWhenDockedLeft;

            _isNavigationBarOnLeft = placeOnLeft;
            LeftNavigationColumn.Width = new GridLength(placeOnLeft ? 48 : 0);
            LeftNavigationGapColumn.Width = new GridLength(placeOnLeft ? NavigationGap : 0);
            RightNavigationGapColumn.Width = new GridLength(placeOnLeft ? 0 : NavigationGap);
            RightNavigationColumn.Width = new GridLength(placeOnLeft ? 0 : 48);

            Grid.SetColumn(NavBar, placeOnLeft ? 0 : 4);
            NavBar.SetNavigationBarPlacement(placeOnLeft);
            UpdateContentOutsideMargin(ActualWidth);
            ApplyContentHostMargin();
        }

        private void ApplyContentHostMargin()
        {
            double leftMargin = _isNavigationBarOnLeft ? ContentNavigationSideMargin : _contentOutsideMargin;
            double rightMargin = _isNavigationBarOnLeft ? _contentOutsideMargin : ContentNavigationSideMargin;
            ContentHost.Margin = new Thickness(leftMargin, _contentTopMargin, rightMargin, 4);
            System.Diagnostics.Debug.WriteLine(
                $"[Linker] NavigationBarOnLeft={_isNavigationBarOnLeft}, ContentHost.Margin={ContentHost.Margin}");
        }

        private void UpdateContentOutsideMargin(double width)
        {
            // 固定外边距，不随窗口宽度变化
            _contentOutsideMargin = CompactContentOutsideMargin;
        }

        private void OnBackRequested(object? sender, EventArgs e)
        {
            if (!ContentHost.CanGoBack) return;

            _isNavigatingBack = true;
            // 官方推荐：不传参数，Frame 自动使用反向动画
            ContentHost.GoBack();
        }

        private void OnPageCloseRequested(object? sender, string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[Linker] 收到页面关闭请求: {shortcutId}");
            
            // 诊断：关闭前的状态
            WebViewManager.DiagnoseState();
            
            ContentHost.RemoveCachedPage(shortcutId);
            
            // 修改为返回到上一个导航记录，而不是强制跳转到主页
            if (ContentHost.CanGoBack)
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] 返回到上一个导航记录");
                ContentHost.GoBack();
            }
            else
            {
                // 如果没有历史记录，则导航到主页
                System.Diagnostics.Debug.WriteLine($"[Linker] 无历史记录，导航到主页");
                ContentHost.Navigate(typeof(HomePage));
                NavBar.SelectHomeItem();
            }
            
            // 更新返回按钮可见性
            NavBar.UpdateBackButtonVisibility(ContentHost.CanGoBack);
            
            // 诊断：关闭后的状态
            WebViewManager.DiagnoseState();
        }

        private void OnShortcutRemoved(object? sender, string shortcutId)
        {
            // 清除对应的缓存页面
            ContentHost.RemoveCachedPage(shortcutId);
        }

        private async void OnWebAppRestartRequested(object? sender, string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[Linker] 收到重启请求: {shortcutId}");
            // ⭐ 修复 Bug: 传递 shortcutId，重启指定的标签而不是当前标签
            await ContentHost.RestartTabAsync(shortcutId);
        }

        private void ContentHost_Navigated(object? sender, NavigationEventArgs e)
        {
            SyncNavigationBarSelection(e.SourcePageType, e.Parameter);
            _isNavigatingBack = false;
            NavBar.UpdateBackButtonVisibility(ContentHost.CanGoBack);
            // 顶栏返回按钮由 ContentArea 内部在 Navigated 时自动刷新，无需在此处理
            SyncTopAppBarVisibility(e.SourcePageType);
        }

        private void ContentHost_CachedPageNavigated(object? sender, (Type PageType, object? Parameter) e)
        {
            System.Diagnostics.Debug.WriteLine($"[Linker] 🔥 收到 CachedPageNavigated 事件: {e.PageType.Name}, Parameter={(e.Parameter?.ToString() ?? "null")}");
            SyncNavigationBarSelection(e.PageType, e.Parameter);
            _isNavigatingBack = false;
            NavBar.UpdateBackButtonVisibility(ContentHost.CanGoBack);
            // 顶栏返回按钮由 ContentArea 内部在 CachedPageNavigated 时自动刷新，无需在此处理
            SyncTopAppBarVisibility(e.PageType);
        }

        /// <summary>
        /// 同步顶部应用栏的可见性
        /// </summary>
        private void SyncTopAppBarVisibility(Type pageType)
        {
            // WebBrowserPage 现在使用统一顶部栏，不需要特殊处理
            // 顶部栏的显示由页面自己控制
        }

        private void OnNavigationRequested(object? sender, NavRequest request)
        {
            ContentHost.Navigate(request.PageType, request.Parameter);
        }

        private void PushCurrentPageToHistory() { } // 已废弃，由 Frame.BackStack 内置管理

        private void OnTopBarDoubleTapped(object? sender, EventArgs e)
        {
            // 双击顶部栏切换最大化状态
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.RequestToggleMaximize();
        }

        public void NavigateToNewPage(string url)
        {
            System.Diagnostics.Debug.WriteLine($"Linker.NavigateToNewPage called with URL: {url}");
            ContentHost.Navigate(typeof(NewPage), url);
            NavBar.SelectNewPageItem();
        }

        public void UpdateContentCornerRadius(bool isPinned)
        {
            ContentHost.SetCornerRadius(isPinned);
        }

        public void UpdateContentTopMargin(bool isPinnedOrMaximized)
        {
            _isPinnedOrMaximized = isPinnedOrMaximized;
            _contentTopMargin = isPinnedOrMaximized ? 4 : 6;
            UpdateContentOutsideMargin(ActualWidth);
            ApplyContentHostMargin();
        }

        public void SyncNavigationBarSelection(Type pageType, object? parameter)
        {
            System.Diagnostics.Debug.WriteLine($"[Linker] 🔥 SyncNavigationBarSelection 被调用: {pageType.Name}, Parameter={(parameter?.ToString() ?? "null")}");
            
            if (pageType == typeof(WebBrowserPage) && parameter is WebAppShortcut shortcut)
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 选中 WebApp 标签: {shortcut.Id}");
                NavBar.SelectWebAppItem(shortcut.Id);
            }
            else if (pageType == typeof(HomePage))
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 选中首页");
                NavBar.SelectHomeItem();
            }
            else if (pageType == typeof(SettingsPage))
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 选中设置页");
                NavBar.SelectSettingsItem();
            }
            else if (pageType == typeof(Pages.AI.AIPage))
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 选中 AI 页");
                NavBar.SelectAIItem();
            }
            else if (pageType == typeof(NewPage))
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 选中新建页");
                NavBar.SelectNewPageItem();
            }
            // ⭐ 修复：处理子页面（WebAppManagementPage、WebAppDetailPage）
            // 这些页面是设置页的子页面，应该选中设置页的导航项
            else if (pageType.Name == "WebAppManagementPage" || pageType.Name == "WebAppDetailPage")
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ✅ 子页面 {pageType.Name} → 选中设置页");
                NavBar.SelectSettingsItem();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Linker] ⚠️ 未知页面类型，无法同步选中项");
            }
        }

        /// <summary>
        /// 重启当前标签页
        /// </summary>
        public async System.Threading.Tasks.Task RestartCurrentTabAsync()
        {
            await ContentHost.RestartCurrentTabAsync();
        }

        // ==================== 快捷键支持方法 ====================

        /// <summary>
        /// 根据索引切换到对应的网页应用标签（Ctrl+1~9）
        /// </summary>
        /// <param name="index">标签索引（0-based）</param>
        public void SwitchToWebAppByIndex(int index)
        {
            NavBar.SwitchToWebAppByIndex(index);
        }

        /// <summary>
        /// 切换到下一个网页应用标签（Ctrl+Tab）
        /// </summary>
        public void SwitchToNextWebApp()
        {
            NavBar.SwitchToNextWebApp();
        }
    }
}
