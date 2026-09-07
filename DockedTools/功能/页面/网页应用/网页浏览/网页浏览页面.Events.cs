using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 事件处理器模块
    /// 包含页面生命周期、WebView导航、按钮点击等所有事件处理逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        // 页面关闭请求事件
        public event EventHandler<string>? PageCloseRequested;

        /// <summary>
        /// ⭐ 任务 6.3：WebBrowserPage_Loaded 事件入口（委托到异步实现）
        /// </summary>
        private async void WebBrowserPage_Loaded(object sender, RoutedEventArgs e)
        {
            AsyncSafety.Run(
                async () => await WebBrowserPageLoadedAsync(sender, e),
                "WebBrowserPage",
                "Loaded");
        }

        /// <summary>
        /// ⭐ 任务 6.3：WebBrowserPage_Loaded 异步实现
        /// </summary>
        private async Task WebBrowserPageLoadedAsync(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] Loaded 事件触发");
            
            // Loaded 事件只负责初始化 WebView，不干预导航和链接管理
            // 链接管理由 INavigationAware.OnNavigatedTo 负责
            
            await EnsureWebViewInitializedAsync();
            TryNavigatePendingUri();
        }

        private void WebBrowserPage_Unloaded(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] Unloaded 事件触发");
            
            // ✅ 注销底部栏主题服务
            Services.BottomBarThemeService.Unregister();
            
            // 取消订阅更新事件
            Shared.WebAppUpdateService.UpdateCompleted -= OnWebAppUpdated;
            
            // 延迟检查：如果页面真的被移除了（不在可视树中），才清理
            // 使用 DispatcherQueue 延迟执行，让 Loaded 事件有机会先触发
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // 检查页面是否还在可视树中
                if (XamlRoot == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 页面已从可视树移除，但由于缓存机制不清理顶部栏");
                    // 注意：即使页面从可视树移除，也不清理顶部栏
                    // 因为页面可能被缓存，稍后会恢复
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 页面仍在可视树中，Unloaded 是误触发");
                }
            });
        }

        // ⚠️ OnSystemThemeChanged已移至 网页浏览页面.ForegroundColors.cs

        // ⚠️ OnSystemThemeChanged已移至 网页浏览页面.ForegroundColors.cs

        // ⚠️ OnWinUIContextMenuSettingsChanged、OnWebViewPerformanceSettingsChanged已移至 网页浏览页面.WebViewConfig.cs

        /// <summary>
        /// 处理网页应用配置更新事件
        /// </summary>
        private async void OnWebAppUpdated(object? sender, Shared.WebAppUpdateEventArgs e)
        {
            // 只处理当前快捷方式的更新
            if (_currentShortcut == null || e.AppId != _currentShortcut.Id)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 收到更新通知: {e.AppId}, 类型: {e.UpdateType}");

            // 如果按钮配置有变化，重新加载快捷方式并刷新按钮
            if (e.UpdateType.HasFlag(Shared.WebAppUpdateType.ButtonConfig))
            {
                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    try
                    {
                        // 重新加载快捷方式数据
                        var shortcuts = await Shared.WebAppShortcutStore.LoadAsync();
                        var updatedShortcut = shortcuts.FirstOrDefault(s => s.Id == e.AppId);
                        
                        if (updatedShortcut != null)
                        {
                            _currentShortcut = updatedShortcut;
                            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 快捷方式已重新加载: {updatedShortcut.Name}");
                            
                            // 重新设置顶部栏（刷新按钮）
                            SetupTopBar();
                            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 按钮配置已刷新");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 更新按钮配置失败: {ex.Message}");
                    }
                });
            }

            // 如果名称或图标有变化，更新标题栏
            if (e.UpdateType.HasFlag(Shared.WebAppUpdateType.Name) || 
                e.UpdateType.HasFlag(Shared.WebAppUpdateType.Icon))
            {
                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    try
                    {
                        var shortcuts = await Shared.WebAppShortcutStore.LoadAsync();
                        var updatedShortcut = shortcuts.FirstOrDefault(s => s.Id == e.AppId);
                        
                        if (updatedShortcut != null)
                        {
                            _currentShortcut = updatedShortcut;
                            UpdateTopBarContent();
                            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 标题栏已更新");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 更新标题栏失败: {ex.Message}");
                    }
                });
            }
        }

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // 显示加载条
            DispatcherQueue.TryEnqueue(() =>
            {
                if (LoadingProgressBar != null)
                {
                    LoadingProgressBar.IsIndeterminate = true;
                    LoadingProgressBar.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>
        /// ⭐ 任务 6.3：CoreWebView2_NavigationCompleted 事件入口（委托到异步实现）
        /// </summary>
        private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            AsyncSafety.Run(
                async () => await CoreWebView2NavigationCompletedAsync(sender, e),
                "WebBrowserPage",
                "NavigationCompleted");
        }


        /// <summary>
        /// ⭐ 任务 6.3：CoreWebView2_NavigationCompleted 异步实现
        /// </summary>
        private async Task CoreWebView2NavigationCompletedAsync(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            UpdateNavigationButtonStates();
            
            // ⭐ 任务 3.4：导航成功后重置无响应计数器
            _unresponsiveCount = 0;
            
            // 平滑隐藏加载条：先停止动画，等待当前周期完成，再隐藏
            await HideLoadingProgressBarSmoothlyAsync();
        }

        private void CoreWebView2_HistoryChanged(object? sender, object e)
        {
            UpdateNavigationButtonStates();
        }

        private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
        {
            if (WebView?.CoreWebView2 is null)
            {
                return;
            }

            string title = WebView.CoreWebView2.DocumentTitle ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                if (_currentShortcut is not null && !string.IsNullOrWhiteSpace(_currentShortcut.Name))
                {
                    if (_topBarTitle != null)
                    {
                        _topBarTitle.Text = _currentShortcut.Name;
                    }
                }

                return;
            }

            if (_topBarTitle != null)
            {
                _topBarTitle.Text = title;
            }
        }


        // ==================== 按钮点击事件 ====================

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView != null && WebView.CanGoBack)
            {
                WebView.GoBack();
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView != null && WebView.CanGoForward)
            {
                WebView.GoForward();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            TryReloadWebView();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 关闭当前页面
            if (_currentShortcut != null)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 用户点击关闭按钮: {_currentShortcut.Id}");
                
                // 触发关闭事件，通知导航栏和内容区域
                PageCloseRequested?.Invoke(this, _currentShortcut.Id);
            }
        }

        // ⚠️ CopyUrlButton_Click、OpenExternalButton_Click已移至 网页浏览页面.ContextMenu.cs
    }
}

