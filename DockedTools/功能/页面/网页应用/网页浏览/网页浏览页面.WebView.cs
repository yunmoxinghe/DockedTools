using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Browser.Managers;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        /// <summary>
        /// 取本页当前的内核引用。
        /// WebView 是 XAML 私有元素，外部（WebViewManager / Cookie 服务）要借用内核能力
        /// 只能走这里。刻意不做任何初始化 —— 没初始化就返回 null，调用方按「拿不到」处理，
        /// 而不是替它偷偷 EnableCoreWebView2（那会凭空拉起一个浏览器进程）。
        /// </summary>
        public CoreWebView2? GetCoreWebView2() => WebView?.CoreWebView2;

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
                
                // ⭐ 网页通知 → 系统通知的桥接（内部幂等，重复订阅安全）
                Services.WebNotificationBridge.Attach(WebView.CoreWebView2);
                
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
                
                // 常驻取色脚本必须赶在导航之前注入 —— AddScriptToExecuteOnDocumentCreated
                // 只对【注入之后才创建的文档】生效，等 NavigationCompleted 之后再注入的话，
                // 用户看到的第一个页面永远拿不到脚本：那个页面上「动态刷新」等于关着的，
                // 只剩进站时那两次一次性采样。
                // probeCurrentDocument: false —— 这个分支里文档可能已经加载完了，
                // 注入对它无效，交给下面的一次性探测补。
                _ = EnsureAdaptiveColourSourceAsync(probeCurrentDocument: false);

                _isWebViewReady = true;
                
                // ⭐ 透明背景实验室：即使走重新配置路径也要同步背景色与探针
                ApplyWebViewTransparency();
                ApplyWebViewTransparencyProbe();
                
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
                
                // ⭐ 透明背景实验室：环境变量必须在任何一个 CoreWebView2 被创建之前设置，否则不生效
                ApplyWebViewTransparencyEnvironmentVariable();
                
                // ⭐ PreInit 策略：在控制器创建之前就把底色写好，从源头消除白闪
                ApplyTransparencyBeforeControllerCreation();
                
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
                
                // 按实验室策略设置 WebView2 背景色
                ApplyWebViewTransparency();
                
                // 按实验室探针开关渲染 WebView 底色块
                ApplyWebViewTransparencyProbe();

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
                    
                    // ⭐ 网页通知 → 系统通知的桥接（内部幂等，重复订阅安全）
                    Services.WebNotificationBridge.Attach(WebView.CoreWebView2);
                    
                    // ⭐ 任务 3.2：订阅 ProcessFailed 事件
                    WebView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
                    
                    // 根据设置配置右键菜单
                    UpdateContextMenuConfiguration(useWinUIContextMenu);
                    
                    // 常驻取色脚本必须赶在首次导航之前注入：
                    // AddScriptToExecuteOnDocumentCreated 只对【注入之后才创建的文档】生效，
                    // 等到 NavigationCompleted 之后再注入的话，用户看到的第一个页面
                    // 永远拿不到脚本 —— 那个页面上「动态刷新」等于关着的，
                    // 只剩进站时那两次一次性采样，页面滚动/换肤都不会再更新。
                    // probeCurrentDocument: false —— 这里还没导航，探测只会取到空白页；
                    // 第一次导航的 NavigationCompleted 会补上探测。
                    _ = EnsureAdaptiveColourSourceAsync(probeCurrentDocument: false);

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

        /// <summary>
        /// 透明背景实验室：在 CoreWebView2 创建之前设置或清理 WEBVIEW2_DEFAULT_BACKGROUND_COLOR
        /// 
        /// 【为什么必须放在这里】
        /// 该环境变量只在「第一个 CoreWebView2 被创建」时被读取一次，晚调用等于没设。
        /// 
        /// 【为什么非环境变量模式下要显式删掉】
        /// 环境变量是进程级的，一旦设了会影响之后所有新建的 WebView。
        /// 所以切回其它策略时必须主动删除，否则会污染 LRU 里后续新建的实例。
        /// </summary>
        private void ApplyWebViewTransparencyEnvironmentVariable()
        {
            const string BackgroundColorVariable = "WEBVIEW2_DEFAULT_BACKGROUND_COLOR";
            WebViewTransparencyMode mode = ExperimentalSettings.WebViewTransparencyMode;

            if (mode == WebViewTransparencyMode.EnvironmentVariable ||
                mode == WebViewTransparencyMode.EnvironmentVariableAndPreInit)
            {
                Environment.SetEnvironmentVariable(BackgroundColorVariable, "00000000");
                System.Diagnostics.Debug.WriteLine($"[TransparencyLab] 已设置 {BackgroundColorVariable}=00000000");
            }
            else if (Environment.GetEnvironmentVariable(BackgroundColorVariable) != null)
            {
                Environment.SetEnvironmentVariable(BackgroundColorVariable, null);
                System.Diagnostics.Debug.WriteLine($"[TransparencyLab] 已清除 {BackgroundColorVariable}");
            }
        }

        /// <summary>
        /// 透明背景实验室：只有 PreInit 与双保险两种策略需要在控制器创建之前定色
        /// </summary>
        private void ApplyTransparencyBeforeControllerCreation()
        {
            WebViewTransparencyMode mode = ExperimentalSettings.WebViewTransparencyMode;

            if (mode is WebViewTransparencyMode.PreInit or WebViewTransparencyMode.EnvironmentVariableAndPreInit)
            {
                ApplyWebViewTransparency();
            }
        }

        /// <summary>
        /// 透明背景实验室：应用 XAML 层可见的背景色
        /// 
        /// 注意这里是「给 WebView 自己刷什么底色」，不代表能看见下面的 XAML。
        /// 按微软 Visual layer 文档，WebView2 属于 external content，XAML 合成器会在它区域挖洞，
        /// 因此 z 序更低的 Grid/Border 一律不可见，只有同为 external content 的
        /// SystemBackdrop（Mica / 桌面亚克力）或纯窗口底色能透出来。
        /// </summary>
        private void ApplyWebViewTransparency()
        {
            if (WebView == null)
            {
                return;
            }

            WebViewTransparencyMode mode = ExperimentalSettings.WebViewTransparencyMode;
            WebView.DefaultBackgroundColor = mode == WebViewTransparencyMode.Opaque
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Transparent;

            System.Diagnostics.Debug.WriteLine($"[TransparencyLab] DefaultBackgroundColor = {mode}");
        }

        /// <summary>
        /// 透明背景实验室：熏染 WebView 底色块，作为「能否看到 Z 轴更低内容」的肉眼探针
        /// 
        /// 判读方法：
        /// - 看到品红 → XAML 合成器内容可见（与微软文档结论相反，值得记录）
        /// - 看不到品红 → 确认挖洞成立，透明只可能穿透到 SystemBackdrop 或窗口底色
        /// </summary>
        private void ApplyWebViewTransparencyProbe()
        {
            if (WebPageWebViewBackground == null)
            {
                return;
            }

            if (ExperimentalSettings.WebViewTransparencyProbe)
            {
                // 品红：Z 序更低的合成器内容如果能被看见，肉眼一眼就能分辨
                WebPageWebViewBackground.Background = new SolidColorBrush(new Windows.UI.Color
                {
                    A = 255,
                    R = 255,
                    G = 0,
                    B = 255
                });
            }
            else if (Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] is Brush themeBrush)
            {
                WebPageWebViewBackground.Background = themeBrush;
            }

            System.Diagnostics.Debug.WriteLine($"[TransparencyLab] 探针 = {ExperimentalSettings.WebViewTransparencyProbe}");
        }

        private string BuildBrowserArguments()
        {
            var args = new List<string>
            {
                "--enable-smooth-scrolling",
                "--enable-zero-copy"

                // ⚠️ 已于 2026-10-03 移除：--disable-features=msExperimentalScrolling
                //
                // 它关掉的是 Edge Scrolling Personality（即 edge://flags/#edge-experimental-scrolling，
                // Windows 上 Edge 默认开启）。按 Edge 团队公开说明，这套 personality 包含三件事：
                //   1. 改进的动量 / touch fling 动画曲线
                //   2. 百分比滚动（用 scroller 高度计算 delta，而非固定 100px/tick）
                //   3. 根滚动器上的 overscroll bounce —— 官方明确说对 touch 与 touchpad 都生效
                // 这三项正是 Edge 相对标准 Chromium 在精密触摸板上「跟手」的直接来源。
                // 关掉它 = 退回标准 Chromium 滚动 → 内容滞后于手指、手感发钝。
                // 来源：Microsoft Tech Community 讨论（HotCakeX 说明该 flag 在 Edge 中默认开启）、
                //       Thurrott 汇总的 Edge 团队官方博文、microsoft-ui-xaml#11408。
                //
                // ⚠️ 若要复现「标准 Chromium 手感」做对照，把这行加回列表即可（一行 A/B）。
            };

            // 🚀 启动速度优化（零内存成本）
            args.Add("--dns-prefetch-disable=false");  // 启用 DNS 预解析
            args.Add("--enable-tcp-fast-open");        // 启用 TCP Fast Open

            // 🎨 消除白闪（无论是否快速启动模式都启用）
            args.Add("--disable-backgrounding-occluded-windows");  // 禁用窗口遮挡时的背景化
            args.Add("--disable-renderer-backgrounding");          // 禁用渲染器后台化
            
            // 🎨 透明背景实验室：砍掉创建时的隐式 about:blank 导航，消除首屏白闪
            if (ExperimentalSettings.WebViewCancelInitialNavigation)
            {
                args.Add("--msWebView2CancelInitialNavigation");
            }
            
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
            
            // 页面都要拆了，没理由再让宽度动画继续写组件
            StopBottomBarWidthAnimation();

            Loaded -= WebBrowserPage_Loaded;
            Unloaded -= WebBrowserPage_Unloaded;
            Pages.Settings.SettingsPage.WinUIContextMenuSettingsChanged -= OnWinUIContextMenuSettingsChanged;
            Pages.Settings.SettingsPage.WebViewPerformanceSettingsChanged -= OnWebViewPerformanceSettingsChanged;
            Pages.Lab.LabPage.WebViewTransparencySettingsChanged -= OnWebViewTransparencySettingsChanged;
            
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
