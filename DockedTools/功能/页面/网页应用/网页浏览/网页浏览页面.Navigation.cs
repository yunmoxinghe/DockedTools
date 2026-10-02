using DockedTools.Features.MainWindowContent.ContentArea;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
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
        /// <summary>
        /// 页面当前是否在前台。
        /// TrySuspendAsync 是异步的，await 期间用户可能已经把窗口滑回来了，
        /// 用它阻断「异步返回后又把 Visibility 改成 Collapsed」的竞态。
        /// </summary>
        private bool _isPageActive;

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
            
            // ⭐ 页面回到前台：按「收起时模式」把 WebView 拉回完全可用状态
            _isPageActive = true;
            RestoreWebViewFromIdleState();
            
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
            
            // ⭐ 页面被收起：按「收起时模式」降功耗（普通 / 高效 / 挂起）
            _isPageActive = false;
            _ = ApplyIdlePowerModeAsync();
            
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

        /// <summary>
        /// 页面被收起时按省电模式降低 WebView 功耗
        /// 
        /// <list type="bullet">
        /// <item>普通：什么都不做，保持完全性能</item>
        /// <item>高效：MemoryUsageTargetLevel = Low，脚本照跑、网络不断，但内存可被换出到磁盘</item>
        /// <item>挂起：TrySuspendAsync，脚本与网络一并暂停，省电最猛</item>
        /// </list>
        /// 
        /// ⚠️ WebView2 官方要求 MemoryUsageTargetLevel 与 TrySuspend/Resume 二选一混用会带来不可预期行为，
        /// 所以「高效」与「挂起」两条路径严格分开。
        /// </summary>
        private async Task ApplyIdlePowerModeAsync()
        {
            CoreWebView2? core = WebView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            WebViewIdlePowerMode mode = ExperimentalSettings.IdlePowerMode;
            try
            {
                switch (mode)
                {
                    case WebViewIdlePowerMode.Efficient:
                        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                        System.Diagnostics.Debug.WriteLine("[ApplyIdlePowerMode] 高效：MemoryUsageTargetLevel = Low");
                        break;

                    case WebViewIdlePowerMode.Suspend:
                        await SuspendWebViewAsync(core);
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine("[ApplyIdlePowerMode] 普通：不处理");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyIdlePowerMode] 应用模式 {mode} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 真正尝试把 WebView 挂起（不是只调用 API，而是保证前提成立）
        ///
        /// ⚠️ WebView2 官方硬性前提（WebView2Feedback/specs/Freeze.md 与 API 文档一致）：
        ///    调用 TrySuspendAsync 之前 CoreWebView2Controller.IsVisible 必须为 false，
        ///    否则直接抛 COMException，错误码 HRESULT_FROM_WIN32(ERROR_INVALID_STATE) = 0x8007139F。
        ///    WinUI 里 controller 由 WebView2 控件内部持有、拿不到句柄，唯一入口是把控件
        ///    Visibility 设成 Collapsed —— 这正是官方 WinUI 示例的写法。
        ///
        /// TrySuspend 本身是 best effort：命中 Sleeping Tabs 阻止条件（持有 IndexedDB 事务 /
        /// Web Lock / 共享 BrowsingInstance / 正在播放音频 / 被 DevTools 检查等）时不抛异常，
        /// 而是返回 false。此时降级到 MemoryUsageTargetLevel.Low，保住一部分省电效果。
        /// </summary>
        private async Task SuspendWebViewAsync(CoreWebView2 core)
        {
            // 竞态检查：await 期间窗口可能已被滑回来，此时不该再把 WebView 藏起来
            if (_isPageActive)
            {
                System.Diagnostics.Debug.WriteLine("[SuspendWebViewAsync] 页面已回到前台，放弃挂起");
                return;
            }

            WebView!.Visibility = Visibility.Collapsed;

            // ⭐⭐ 关键：XAML 的 Visibility 变化到 CoreWebView2Controller.IsVisible 是异步传播的，
            // 赋值语句返回时 controller 侧很可能还停留在 Visible。
            // 官方 WebView2 WinUI Sample 在这一步放的是 `await Task.Delay(1000)`，注释原文：
            //   "Wait for visibility change to take effect."
            // 这里不傻等固定 1 秒，改成按错误码重试：只有 0x8007139F 才继续等，
            // 传播到位通常第一两次就成功，总耗时反而比固定 Delay 更短。
            const int MaxAttempts = 4;
            const int RetryDelayMs = 250;
            const uint ErrorInvalidState = 0x8007139F;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // 每轮都重查，防止等待期间用户把窗口滑回来
                if (_isPageActive)
                {
                    System.Diagnostics.Debug.WriteLine($"[SuspendWebViewAsync] 等待期间页面已回到前台，放弃挂起（第 {attempt} 次尝试前）");
                    return;
                }

                // 防御：万一别处把可见性改回来，压回去
                if (WebView.Visibility != Visibility.Collapsed)
                {
                    WebView.Visibility = Visibility.Collapsed;
                }

                bool suspended;
                try
                {
                    suspended = await core.TrySuspendAsync();
                }
                catch (Exception ex) when (unchecked((uint)ex.HResult) == ErrorInvalidState && attempt < MaxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SuspendWebViewAsync] IsVisible 尚未传播（0x8007139F），{RetryDelayMs}ms 后重试 {attempt + 1}/{MaxAttempts}");
                    await Task.Delay(RetryDelayMs);
                    continue;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SuspendWebViewAsync] ❌ TrySuspendAsync 抛异常: 0x{ex.HResult:X8} {ex.Message}");
                    RestoreVisibilityAfterFailedSuspend(core);
                    return;
                }

                if (suspended)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SuspendWebViewAsync] ✅ 挂起成功（第 {attempt} 次尝试），IsSuspended={core.IsSuspended}");
                    return;
                }

                // TrySuspend 是 best effort：不抛异常直接返回 false 表示命中了 Sleeping Tabs 阻止条件
                System.Diagnostics.Debug.WriteLine(
                    "[SuspendWebViewAsync] ⚠️ TrySuspendAsync 返回 false（命中 Sleeping Tabs 阻止条件），降级为高效模式");
                RestoreVisibilityAfterFailedSuspend(core);
                return;
            }
        }

        /// <summary>
        /// 挂起失败后的兜底：把可见性还回去并把内存档位降到 Low
        /// 不还可见性的话，用户滑回来看到的就是一块空白，比没省电更糟
        /// </summary>
        private void RestoreVisibilityAfterFailedSuspend(CoreWebView2 core)
        {
            try
            {
                WebView!.Visibility = Visibility.Visible;
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                System.Diagnostics.Debug.WriteLine("[SuspendWebViewAsync] 已还原可见性并降级为 MemoryUsageTargetLevel.Low");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SuspendWebViewAsync] 降级失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 页面回到前台：如被挂起则 Resume，并把 MemoryUsageTargetLevel 还原到设置页选定的档位
        /// </summary>
        private void RestoreWebViewFromIdleState()
        {
            CoreWebView2? core = WebView?.CoreWebView2;

            bool wasSuspended = false;

            try
            {
                // 官方推荐顺序：先 Resume，再把控件变回 Visible
                // （控件变可见本身会触发自动 Resume，显式调用只是让时序更可控）
                if (core != null && core.IsSuspended)
                {
                    core.Resume();
                    wasSuspended = true;
                    System.Diagnostics.Debug.WriteLine("[RestoreWebViewFromIdleState] WebView 已 Resume");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestoreWebViewFromIdleState] Resume 失败: {ex.Message}");
            }

            try
            {
                // ⚠️ 挂起时 Visibility 被设成了 Collapsed，这里必须还原，否则滑回来是一块空白
                if (WebView != null && WebView.Visibility != Visibility.Visible)
                {
                    WebView.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine("[RestoreWebViewFromIdleState] 已恢复 WebView 可见性");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestoreWebViewFromIdleState] 恢复可见性失败: {ex.Message}");
            }

            if (core == null || !wasSuspended)
            {
                return;
            }

            try
            {
                // ⚠️ 只在真的 Resume 过才还原内存档位。
                // WebView2 官方要求 MemoryUsageTargetLevel 与 TrySuspend/Resume 不可混用，
                // 所以「普通 / 高效」这两条路径全程不碰 target level，
                // 也就不需要在这里写回去 —— 少一次无谓写入，少一次混用风险。
                ApplyMemoryModeSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestoreWebViewFromIdleState] 还原内存档位失败: {ex.Message}");
            }
        }
    }
}
