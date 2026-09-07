using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - WebView 生命周期管理模块
    /// 包含初始化、配置、进程恢复、清理等核心生命周期方法
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private async Task EnsureWebViewInitializedAsync()
        {
            if (WebView == null)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] WebView 为 null，无法初始化");
                return;
            }

            // ⭐ 如果 WebView 已经 ready 且 CoreWebView2 存在，直接返回
            if (_isWebViewReady && WebView.CoreWebView2 != null)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] WebView 已就绪，跳过初始化");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 开始初始化 WebView");
            
            // ⭐ 检查 CoreWebView2 是否已经初始化（可能是首次加载，CoreWebView2 还未初始化）
            if (WebView.CoreWebView2 != null)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] CoreWebView2 已存在，重新配置");
                
                // 重新配置设置
                bool useWinUIContextMenu = ExperimentalSettings.EnableWinUIContextMenu;
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !useWinUIContextMenu;
                WebView.CoreWebView2.Settings.IsSwipeNavigationEnabled = true;
                WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                
                // 应用内存模式设置
                ApplyMemoryModeSettings();
                
                // 重新订阅事件
                WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                WebView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                WebView.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
                WebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                
                // ⭐ 订阅新窗口请求事件
                WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                
                // ⭐ 任务 3.2：订阅 ProcessFailed 事件（防止重复订阅）
                WebView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
                WebView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
                
                // ⭐ 任务 3.2：订阅 BrowserProcessExited 事件（如果 environment 已存在）
                if (_webViewEnvironment != null)
                {
                    _webViewEnvironment.BrowserProcessExited -= CoreWebView2Environment_BrowserProcessExited;
                    _webViewEnvironment.BrowserProcessExited += CoreWebView2Environment_BrowserProcessExited;
                }
                
                // 根据设置配置右键菜单
                UpdateContextMenuConfiguration(useWinUIContextMenu);
                
                _isWebViewReady = true;
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ✅ WebView 重新配置完成");
                return;
            }

            try
            {
                // 检查 WebView2 Runtime 是否可用
                string? runtimeVersion = null;
                try
                {
                    runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] WebView2 Runtime 版本: {runtimeVersion}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ❌ WebView2 Runtime 未安装或不可用: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 请从以下地址下载并安装 WebView2 Runtime:");
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] https://developer.microsoft.com/microsoft-edge/webview2/");
                    
                    // 显示用户友好的错误消息
                    await ShowWebView2RuntimeMissingDialogAsync();
                    return;
                }

                CoreWebView2EnvironmentOptions options = new()
                {
                    Language = GetWebViewLanguage(),
                    // 优化触摸板滚动体验的浏览器参数
                    AdditionalBrowserArguments = BuildBrowserArguments()
                };
                
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 创建 CoreWebView2Environment...");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: null,
                    options: options);
                
                // ⭐ 保存 environment 引用（用于后续订阅 BrowserProcessExited）
                _webViewEnvironment = environment;
                
                // ⭐ 任务 3.2：订阅 BrowserProcessExited 事件（防止重复订阅）
                _webViewEnvironment.BrowserProcessExited -= CoreWebView2Environment_BrowserProcessExited;
                _webViewEnvironment.BrowserProcessExited += CoreWebView2Environment_BrowserProcessExited;
                
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 初始化 CoreWebView2...");
                await WebView.EnsureCoreWebView2Async(environment);
                
                // 设置 WebView2 背景透明
                WebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;

                if (WebView.CoreWebView2 is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ✅ CoreWebView2 初始化成功");
                    
                    WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                    
                    // 优化触摸板和滚动体验
                    WebView.CoreWebView2.Settings.IsSwipeNavigationEnabled = true;
                    
                    // 禁用触摸板缩放
                    WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    
                    // 禁用状态栏（悬停链接时左下角不显示 URL）
                    WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    
                    // 根据设置决定是否禁用默认右键菜单
                    bool useWinUIContextMenu = ExperimentalSettings.EnableWinUIContextMenu;
                    WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !useWinUIContextMenu;
                    
                    // 应用内存模式设置
                    ApplyMemoryModeSettings();
                    
                    WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                    WebView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                    WebView.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
                    WebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                    WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                    
                    // ⭐ 订阅新窗口请求事件
                    WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                    
                    // ⭐ 任务 3.2：订阅 ProcessFailed 事件
                    WebView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
                    
                    // 根据设置配置右键菜单
                    UpdateContextMenuConfiguration(useWinUIContextMenu);
                    
                    // 只有在 CoreWebView2 成功初始化后才设置为 ready
                    _isWebViewReady = true;
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ✅ WebView 初始化完成，准备导航");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ❌ CoreWebView2 为 null，初始化失败");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] ❌ WebView 初始化失败: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 错误消息: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[EnsureWebViewInitializedAsync] 堆栈跟踪: {ex.StackTrace}");
                _isWebViewReady = false;
                
                // 显示用户友好的错误消息
                await ShowWebViewInitializationErrorDialogAsync(ex);
            }
        }

        private async Task ShowWebView2RuntimeMissingDialogAsync()
        {
            try
            {
                if (DispatcherQueue == null)
                {
                    return;
                }

                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    var dialog = new DockedTools.Features.UnifiedCalls.InAppDialog.UnifiedInAppDialog();
                    dialog.Configure(
                        Features.Localization.LocalizationHelper.GetString("WebView2_NotInstalled_Title"),
                        Features.Localization.LocalizationHelper.GetString("WebView2_NotInstalled_Content"),
                        closeButtonText: Features.Localization.LocalizationHelper.GetString("WebView2_NotInstalled_CloseButton")
                    );

                    await DockedTools.Features.UnifiedCalls.InAppDialog.InAppDialogService.ShowAsync(dialog, this);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShowWebView2RuntimeMissingDialogAsync] 显示对话框失败: {ex.Message}");
            }
        }

        private async Task ShowWebViewInitializationErrorDialogAsync(Exception ex)
        {
            try
            {
                if (DispatcherQueue == null)
                {
                    return;
                }

                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    var dialog = new DockedTools.Features.UnifiedCalls.InAppDialog.UnifiedInAppDialog();
                    dialog.Configure(
                        Features.Localization.LocalizationHelper.GetString("WebView2_InitFailed_Title"),
                        string.Format(Features.Localization.LocalizationHelper.GetString("WebView2_InitFailed_Content"), ex.GetType().Name, ex.Message),
                        closeButtonText: Features.Localization.LocalizationHelper.GetString("WebView2_InitFailed_CloseButton")
                    );

                    await DockedTools.Features.UnifiedCalls.InAppDialog.InAppDialogService.ShowAsync(dialog, this);
                });
            }
            catch (Exception dialogEx)
            {
                System.Diagnostics.Debug.WriteLine($"[ShowWebViewInitializationErrorDialogAsync] 显示对话框失败: {dialogEx.Message}");
            }
        }

        private static string GetWebViewLanguage()
        {
            return CultureInfo.CurrentUICulture.Name;
        }

        private string BuildBrowserArguments()
        {
            var args = new List<string>
            {
                "--enable-smooth-scrolling",
                "--enable-zero-copy",
                "--disable-features=msExperimentalScrolling"
            };

            // 🚀 启动速度优化（零内存成本）
            args.Add("--dns-prefetch-disable=false");  // 启用 DNS 预解析
            args.Add("--enable-tcp-fast-open");        // 启用 TCP Fast Open

            // 🎨 消除白闪（无论是否快速启动模式都启用）
            args.Add("--disable-backgrounding-occluded-windows");  // 禁用窗口遮挡时的背景化
            args.Add("--disable-renderer-backgrounding");          // 禁用渲染器后台化
            
            // 🎯 进程模型优化
            if (ExperimentalSettings.SingleProcessMode)
            {
                // 单进程模式：将所有服务合并到主进程
                args.Add("--single-process");  // 完全单进程（最激进）
            }
            else
            {
                // 多进程模式：优化辅助进程
                args.Add("--in-process-gpu");              // GPU 进程合并到主进程
                args.Add("--disable-gpu-process-crash-limit");  // 禁用 GPU 进程崩溃限制
                
                // 将 Network Service 和 Storage Service 合并到主进程
                args.Add("--enable-features=NetworkServiceInProcess");
            }

            // 构建 enable-features 列表
            var enableFeatures = new List<string>
            {
                "msEdgeFluentOverlayScrollbar"  // 细滚动条
            };

            // GPU 优化设置
            if (ExperimentalSettings.EnableHardwareAcceleration)
            {
                args.Add("--enable-accelerated-2d-canvas");
                args.Add("--enable-gpu-rasterization");
            }
            else
            {
                // 完全禁用 GPU 进程
                args.Add("--disable-gpu");
                args.Add("--disable-gpu-compositing");
                args.Add("--disable-accelerated-2d-canvas");
            }

            if (ExperimentalSettings.EnableHardwareOverlays)
            {
                args.Add("--enable-hardware-overlays");
            }

            if (ExperimentalSettings.EnableHardwareVideoDecoder)
            {
                enableFeatures.Add("VaapiVideoDecoder");
                args.Add("--enable-accelerated-video-decode");
            }

            if (ExperimentalSettings.DisableSoftwareRasterizer)
            {
                args.Add("--disable-software-rasterizer");
            }

            // 应用性能优化设置
            if (ExperimentalSettings.DisableBackgroundNetwork)
            {
                args.Add("--disable-background-networking");
                args.Add("--disable-sync");
                // ❌ 移除 --disable-preconnect，它严重影响首次加载速度
                args.Add("--no-pings");
            }
            else
            {
                // ✅ 显式启用预连接优化
                args.Add("--enable-preconnect");
            }

            if (ExperimentalSettings.DisableExtensions)
            {
                args.Add("--disable-extensions");
            }

            if (ExperimentalSettings.DisablePlugins)
            {
                args.Add("--disable-plugins");
            }

            // 磁盘缓存大小限制
            int cacheSizeMB = ExperimentalSettings.DiskCacheSize;
            int cacheSizeBytes = cacheSizeMB * 1024 * 1024;
            args.Add($"--disk-cache-size={cacheSizeBytes}");
            args.Add($"--media-cache-size={cacheSizeBytes}");

            // 快速启动模式：减少启动时的检查和初始化
            if (ExperimentalSettings.FastStartupMode)
            {
                args.Add("--disable-breakpad");              // 禁用崩溃报告
                args.Add("--disable-component-update");      // 禁用组件更新检查
                args.Add("--disable-domain-reliability");    // 禁用域名可靠性监控
                args.Add("--disable-background-timer-throttling");  // 减少后台定时器
                args.Add("--disable-features=CalculateNativeWinOcclusion");  // 禁用窗口遮挡计算
            }

            // 合并所有 enable-features
            if (enableFeatures.Count > 0)
            {
                args.Add($"--enable-features={string.Join(",", enableFeatures)}");
            }

            return string.Join(" ", args);
        }

        private void ApplyMemoryModeSettings()
        {
            if (WebView?.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                var memoryMode = ExperimentalSettings.MemoryMode;
                WebView.CoreWebView2.MemoryUsageTargetLevel = memoryMode == WebViewMemoryMode.Low
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;

                System.Diagnostics.Debug.WriteLine($"[ApplyMemoryModeSettings] 内存模式设置为: {memoryMode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyMemoryModeSettings] 设置内存模式失败: {ex.Message}");
            }
        }

        private async Task ClearBrowsingDataAsync()
        {
            if (WebView?.CoreWebView2?.Profile == null)
            {
                return;
            }

            try
            {
                await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.DownloadHistory
                );
                System.Diagnostics.Debug.WriteLine($"[ClearBrowsingDataAsync] 缓存已清理");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClearBrowsingDataAsync] 清理缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理并释放 WebView 资源（公开方法，供 PageCacheManager 和 WebViewManager 调用）
        /// </summary>
        /// <param name="skipUnlink">是否跳过 Unlink 操作（LRU 淘汰时已经移除，不需要再次 Unlink）</param>
        public void DisposeWebView(bool skipUnlink = false)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            
            System.Diagnostics.Debug.WriteLine($"[DisposeWebView] 开始清理 WebView: {_currentShortcut?.Id ?? "null"}, skipUnlink: {skipUnlink}");
            
            // ⭐ 取消链接 WebView（防护：只在有 shortcut 且不跳过时调用）
            if (_currentShortcut != null && !skipUnlink)
            {
                WebViewManager.Unlink(_currentShortcut.Id);
                System.Diagnostics.Debug.WriteLine($"[DisposeWebView] 已取消链接 WebView: {_currentShortcut.Id}");
                WebViewManager.DiagnoseState();
            }
            
            Loaded -= WebBrowserPage_Loaded;
            Unloaded -= WebBrowserPage_Unloaded;
            Pages.Settings.SettingsPage.WinUIContextMenuSettingsChanged -= OnWinUIContextMenuSettingsChanged;
            Pages.Settings.SettingsPage.WebViewPerformanceSettingsChanged -= OnWebViewPerformanceSettingsChanged;
            
            // 清理 WebView 实例
            CleanupAndCloseWebView(WebView);
            
            // ⭐ 标记需要重新创建 WebView
            _needsWebViewRecreation = true;
            
            _pendingNavigationUri = null;
            // ⭐ 不清空 _currentShortcut，因为恢复时需要它来重新导航
            // _currentShortcut = null;
            _isWebViewReady = false;
            
            System.Diagnostics.Debug.WriteLine($"[DisposeWebView] 清理完成，标记需要重新创建 WebView");
        }
        
        /// <summary>
        /// 清理 WebView 实例（完全释放资源以节省内存）
        /// </summary>
        private void CleanupAndCloseWebView(Microsoft.UI.Xaml.Controls.WebView2? webView)
        {
            if (webView?.CoreWebView2 != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] 清理并关闭 WebView 实例");
                    
                    // 移除事件订阅
                    webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    webView.CoreWebView2.DocumentTitleChanged -= CoreWebView2_DocumentTitleChanged;
                    webView.CoreWebView2.HistoryChanged -= CoreWebView2_HistoryChanged;
                    webView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
                    webView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                    webView.CoreWebView2.ContextMenuRequested -= CoreWebView2_ContextMenuRequested;
                    
                    // ⭐ 取消订阅新窗口请求事件
                    webView.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;
                    
                    // ⭐ 任务 3.2：取消订阅 ProcessFailed 事件
                    webView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;

                    // 停止当前导航
                    webView.CoreWebView2.Stop();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] 清理事件失败: {ex.Message}");
                }
            }
            
            // ⭐ 取消订阅 BrowserProcessExited 事件（避免重复订阅）
            if (_webViewEnvironment != null)
            {
                try
                {
                    _webViewEnvironment.BrowserProcessExited -= CoreWebView2Environment_BrowserProcessExited;
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] 已取消订阅 BrowserProcessExited");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] 取消订阅 BrowserProcessExited 失败: {ex.Message}");
                }
                _webViewEnvironment = null;
            }

            if (webView != null)
            {
                try
                {
                    // ⭐ 完全关闭 WebView 以释放内存
                    webView.Close();
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] WebView 已关闭并释放资源");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CleanupAndCloseWebView] 关闭 WebView 失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 重新创建 WebView 控件（在 LRU 清理后恢复页面时使用）
        /// </summary>
        private void RecreateWebView()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[RecreateWebView] 开始重新创建 WebView");
                
                // 找到 WebView 的父容器（Grid，Row=1）
                if (Content is Grid rootGrid && rootGrid.Children.Count > 0)
                {
                    // 查找旧的 WebView 并移除
                    Microsoft.UI.Xaml.Controls.WebView2? oldWebView = null;
                    foreach (var child in rootGrid.Children)
                    {
                        if (child is Microsoft.UI.Xaml.Controls.WebView2 wv)
                        {
                            oldWebView = wv;
                            break;
                        }
                    }
                    
                    if (oldWebView != null)
                    {
                        rootGrid.Children.Remove(oldWebView);
                        System.Diagnostics.Debug.WriteLine("[RecreateWebView] 已移除旧的 WebView");
                    }
                    
                    // 创建新的 WebView
                    var newWebView = new Microsoft.UI.Xaml.Controls.WebView2
                    {
                        Name = "WebView",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        DefaultBackgroundColor = Microsoft.UI.Colors.Transparent
                    };
                    
                    // 设置 Grid.Row
                    Grid.SetRow(newWebView, 1);
                    
                    // 配置右键菜单
                    bool useWinUIContextMenu = ExperimentalSettings.EnableWinUIContextMenu;
                    if (useWinUIContextMenu)
                    {
                        newWebView.ContextFlyout = WebViewContextMenu;
                    }
                    
                    // 添加到 Grid
                    rootGrid.Children.Add(newWebView);
                    
                    // 更新字段引用
                    WebView = newWebView;
                    
                    System.Diagnostics.Debug.WriteLine("[RecreateWebView] ✅ WebView 重新创建成功");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[RecreateWebView] ❌ 无法找到根 Grid");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecreateWebView] ❌ 重新创建 WebView 失败: {ex.Message}");
            }
        }
    }
}
