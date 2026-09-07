using DockedTools.Features.MainWindowContent.ContentArea;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 导航和生命周期模块
    /// 包含页面导航、INavigationAware实现、WebView恢复等逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // ⭐ 订阅窗口状态完成事件
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.StateCompleted += OnMainWindowStateCompleted;
            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 已订阅主窗口状态完成事件 (OnNavigatedTo)");

            if (e.Parameter is not WebAppShortcut shortcut)
            {
                return;
            }

            if (!Uri.TryCreate(shortcut.Url, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            _currentShortcut = shortcut;
            if (_topBarTitle != null)
            {
                _topBarTitle.Text = string.IsNullOrWhiteSpace(shortcut.Name) ? uri.Host : shortcut.Name;
            }
            _ = ShowShortcutIconAsync(shortcut.IconBytes);

            _pendingNavigationUri = uri;
            TryNavigatePendingUri();
            
            // ⭐ 链接 WebView 到 LRU 管理器（在 _currentShortcut 设置之后）
            if (_currentShortcut != null)
            {
                if (!WebViewManager.IsLinked(_currentShortcut.Id))
                {
                    var result = WebViewManager.RequestLink(_currentShortcut.Id, this);
                    if (result.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 已链接到 LRU: {_currentShortcut.Id}");
                        if (result.EvictedOldest)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] LRU 淘汰了旧的 WebView");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 链接失败: {result.ErrorMessage}");
                    }
                }
                else
                {
                    // 已经链接，更新访问顺序
                    WebViewManager.RequestLink(_currentShortcut.Id, this);
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 已链接，更新访问顺序: {_currentShortcut.Id}");
                }
                
                WebViewManager.DiagnoseState();
            }
            
            // 首次导航时设置顶部栏
            SetupTopBar();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // ⭐ 取消订阅主窗口状态完成事件
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.StateCompleted -= OnMainWindowStateCompleted;
            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 已取消订阅主窗口状态完成事件 (OnNavigatedFrom)");
            
            RestoreSharedTopAppBarBackground();
        }

        // INavigationAware 实现
        void INavigationAware.OnNavigatedTo(object? parameter)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] INavigationAware.OnNavigatedTo called");
            
            // ⭐ 订阅窗口状态完成事件，当窗口恢复显示动画完成后给 WebView 焦点
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.StateCompleted += OnMainWindowStateCompleted;
            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 已订阅主窗口状态完成事件");
            
            // ⭐ 如果页面被 LRU 清理过，需要重置 _isDisposed 标志以允许重新初始化
            if (_isDisposed)
            {
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 页面之前被清理过，重置状态以允许恢复");
                _isDisposed = false;
                _isWebViewReady = false;
                
                // 重新订阅事件
                Loaded += WebBrowserPage_Loaded;
                Unloaded += WebBrowserPage_Unloaded;
                Pages.Settings.SettingsPage.WinUIContextMenuSettingsChanged += OnWinUIContextMenuSettingsChanged;
                Pages.Settings.SettingsPage.WebViewPerformanceSettingsChanged += OnWebViewPerformanceSettingsChanged;
                
                // ⭐ 恢复待导航的 URI（如果有 _currentShortcut）
                if (_currentShortcut != null && Uri.TryCreate(_currentShortcut.Url, UriKind.Absolute, out Uri? uri))
                {
                    _pendingNavigationUri = uri;
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 恢复待导航 URI: {uri}");
                }
            }
            
            // ⭐ 如果需要重新创建 WebView，先重新创建
            if (_needsWebViewRecreation)
            {
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 需要重新创建 WebView");
                
                // ⭐ 任务 3.5：检查是否正在恢复中
                if (!_isRecoveringWebView)
                {
                    RecreateWebView();
                    _needsWebViewRecreation = false;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[WebBrowserPage] ⚠️ 正在恢复中，跳过 RecreateWebView");
                }
            }
            
            // ⭐ 重新链接到 LRU（页面恢复时必须重新加入 LRU 管理）
            if (_currentShortcut != null)
            {
                var result = WebViewManager.RequestLink(_currentShortcut.Id, this);
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 页面恢复，重新链接到 LRU: {_currentShortcut.Id}");
                    if (result.EvictedOldest)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] LRU 淘汰了旧的 WebView");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 重新链接失败: {result.ErrorMessage}");
                }
                WebViewManager.DiagnoseState();
            }
            
            // 重新设置顶部栏（因为可能被其他页面清除了）
            SetupTopBar();
            
            // 如果 WebView 被暂停，恢复它
            if (ExperimentalSettings.SuspendInactiveWebView && WebView?.CoreWebView2 != null)
            {
                try
                {
                    WebView.CoreWebView2.Resume();
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 已恢复");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 恢复 WebView 失败: {ex.Message}");
                }
            }
            
            // 检查 WebView 状态并初始化
            if (!_isWebViewReady || WebView?.CoreWebView2 == null)
            {
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] WebView 需要初始化");
                _ = EnsureWebViewInitializedAsync().ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        System.Diagnostics.Debug.WriteLine("[WebBrowserPage] WebView 初始化完成");
                        TryNavigatePendingUri();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 初始化失败: {t.Exception?.Message}");
                    }
                });
            }
            else if (WebView.Source == null && _currentShortcut != null)
            {
                // WebView 已初始化但为空白页，恢复导航
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] WebView 为空白页，恢复导航");
                if (Uri.TryCreate(_currentShortcut.Url, UriKind.Absolute, out Uri? uri))
                {
                    _pendingNavigationUri = uri;
                    TryNavigatePendingUri();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 状态正常，当前 URL: {WebView.Source}");
            }
        }

        void INavigationAware.OnNavigatedFrom()
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] INavigationAware.OnNavigatedFrom called");
            
            // ⭐ 取消订阅主窗口状态完成事件
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.StateCompleted -= OnMainWindowStateCompleted;
            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 已取消订阅主窗口状态完成事件");
            
            RestoreSharedTopAppBarBackground();
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 已恢复统一顶部栏背景");
            
            // 如果启用了暂停不活跃 WebView 的功能
            if (ExperimentalSettings.SuspendInactiveWebView && WebView?.CoreWebView2 != null)
            {
                try
                {
                    _ = WebView.CoreWebView2.TrySuspendAsync();
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] WebView 已暂停");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 暂停 WebView 失败: {ex.Message}");
                }
            }
            
            // 如果启用了自动清理缓存
            if (ExperimentalSettings.AutoClearCache && WebView?.CoreWebView2 != null)
            {
                _ = ClearBrowsingDataAsync();
            }
            
            // 注意：不在这里 Unlink，因为页面可能被缓存并稍后恢复
            // Unlink 由 DisposeWebView 负责（当页面真正被销毁时）
        }

        private void TryNavigatePendingUri()
        {
            System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] 被调用");
            System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] _isWebViewReady={_isWebViewReady}");
            System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] _pendingNavigationUri={_pendingNavigationUri}");
            System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] WebView={WebView != null}");
            System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] WebView.CoreWebView2={WebView?.CoreWebView2 != null}");
            
            if (!_isWebViewReady || _pendingNavigationUri is null || WebView == null)
            {
                System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] 跳过导航：条件不满足");
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] ✅ 开始导航到: {_pendingNavigationUri}");
                WebView.Source = _pendingNavigationUri;
                _pendingNavigationUri = null;
                System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] ✅ 导航请求已发送");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TryNavigatePendingUri] ❌ 导航失败: {ex.Message}");
            }
        }
    }
}
