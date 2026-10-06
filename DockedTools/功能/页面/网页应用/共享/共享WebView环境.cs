using DockedTools.Features.Pages.Settings;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 进程内共享的 <see cref="CoreWebView2Environment"/> —— 全应用只建一个。
    ///
    /// <para><b>为什么必须有它：</b>每份 user data folder（UDF）都会拉起一整套
    /// Chromium 进程树：管理器（browser process）、Network Service、Storage Service、
    /// Crashpad，外加各自的 renderer。此前浏览器页面用默认 UDF、图标光栅器用
    /// <c>EBWebView-IconRaster</c>、Cookie 页临时内核又自建一份 —— 三份 UDF 同时跑，
    /// 光是「重复的进程树」就是几十 MB 和四五个进程。
    /// 微软官方文档（Managing the User Data Folder）明确写着：
    /// 「Each WebView2 browser process consumes additional memory and disk space.
    /// Therefore, we recommend not running WebView2s with too many different
    /// user data folders at the same time.」</para>
    ///
    /// <para><b>为什么不能各自创建但传同一个 UDF：</b>同一 UDF 上已有一个实例在跑时，
    /// 新 environment 的 EnvironmentOptions 只要有任何一项不同，
    /// <c>CreateCoreWebView2Controller</c> 就会以
    /// <c>HRESULT_FROM_WIN32(ERROR_INVALID_STATE)</c>（0x8007139F）失败。
    /// 官方原文：WebView creation fails with HRESULT_FROM_WIN32(ERROR_INVALID_STATE)
    /// if a running instance using the same user data folder exists, and the Environment
    /// objects have different EnvironmentOptions.
    /// 唯一的稳妥解法就是「同一个实例，大家共用」—— 也就是本类做的事。</para>
    ///
    /// <para><b>代价：</b>光栅器那套专属的反节流启动参数不能再单独给，
    /// 必须与浏览器页面完全一致（见 <see cref="BuildBrowserArguments"/> 里的说明）。</para>
    /// </summary>
    internal static class SharedWebViewEnvironment
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>创建中的那一次初始化。首次成功后常住，进程组退出后作废</summary>
        private static Task<CoreWebView2Environment>? _pending;

        /// <summary>
        /// 取共享 environment。首次调用会真正创建，之后所有调用方拿到同一个实例。
        /// </summary>
        public static async Task<CoreWebView2Environment> GetAsync()
        {
            Task<CoreWebView2Environment>? existing = Volatile.Read(ref _pending);

            // 快路径：已经建好或正在建，直接等它
            if (existing is not null && !existing.IsFaulted && !existing.IsCanceled)
            {
                return await existing.ConfigureAwait(false);
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pending is null || _pending.IsFaulted || _pending.IsCanceled)
                {
                    _pending = CreateCoreAsync();
                }

                return await _pending.ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>真正创建的地方。调用方保证已持锁</summary>
        private static async Task<CoreWebView2Environment> CreateCoreAsync()
        {
            // ⚠️ 必须赶在任何一个 CoreWebView2 被创建之前：
            //    WEBVIEW2_DEFAULT_BACKGROUND_COLOR 只在环境建立时被读一次，晚了等于没设。
            //    以前这行挂在浏览器页面里，一旦光栅器先起内核就会漏掉 —— 现在统一在这里。
            ApplyTransparencyEnvironmentVariable();

            CoreWebView2EnvironmentOptions options = new()
            {
                Language = CultureInfo.CurrentUICulture.Name,
                AdditionalBrowserArguments = BuildBrowserArguments()
            };

            System.Diagnostics.Debug.WriteLine("[SharedWebViewEnvironment] 创建共享环境…");

            // UDF 传 null = 应用默认那份（备份功能备份的就是它，不要改）
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: null,
                    options: options);

            AttachExitWatcher(environment);

            System.Diagnostics.Debug.WriteLine(
                $"[SharedWebViewEnvironment] ✅ 共享环境就绪，UDF = {environment.UserDataFolder}");

            return environment;
        }

        /// <summary>
        /// 整个进程组没了 ⇒ 缓存的这个 environment 已经是空壳，作废以便下次重建。
        /// 各页面自己的恢复逻辑（重建 / 标记待重建）不受影响，照旧走它们那套。
        /// </summary>
        private static void AttachExitWatcher(CoreWebView2Environment environment)
        {
            try
            {
                environment.BrowserProcessExited -= OnBrowserProcessExited;
                environment.BrowserProcessExited += OnBrowserProcessExited;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SharedWebViewEnvironment] 订阅 BrowserProcessExited 失败: {ex.Message}");
            }
        }

        private static void OnBrowserProcessExited(
            object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SharedWebViewEnvironment] 浏览器进程组退出（{e.BrowserProcessExitKind}），缓存作废");

            Volatile.Write(ref _pending, null);
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
        private static void ApplyTransparencyEnvironmentVariable()
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
        /// 全应用统一的浏览器启动参数。
        ///
        /// <para><b>⚠️ 这里每一项都是「全局」的：</b>同一 UDF 上所有 WebView 必须共用
        /// 完全一致的 EnvironmentOptions，所以光栅器、Cookie 临时内核都吃这一份，
        /// 不能再各自加料 —— 加了就是 0x8007139F。</para>
        ///
        /// <para><b>关于光栅器的反节流：</b>它原本独享四条参数
        /// （<c>--disable-renderer-backgrounding</c>、<c>--disable-backgrounding-occluded-windows</c>、
        /// <c>--disable-background-timer-throttling</c>、<c>--disable-renderer-priority-management</c>），
        /// 因为它的宿主窗口在屏幕外（-32000,-32000），Chromium 会把它判成后台 renderer 而降优先级，
        /// 图片解码可能被推迟。合并后保留的是前三条里最要命的两条 ——
        /// 下面列表里本来就有 <c>--disable-renderer-backgrounding</c> 与
        /// <c>--disable-backgrounding-occluded-windows</c>，正是防止 renderer 被冻结的那两条；
        /// <c>--disable-background-timer-throttling</c> 也从「仅快速启动模式」提为无条件。
        /// 放弃的只有 <c>--disable-renderer-priority-management</c>（进程优先级），
        /// 而光栅化是 ExecuteScript 同步触发 + postMessage 回推，不靠定时器也不怕降优先级。</para>
        /// </summary>
        private static string BuildBrowserArguments()
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

            // ⭐ 原属光栅器专属参数，现提为全局：隐藏/屏幕外的 renderer 不该被冻结，
            //    否则离屏内核（图标光栅化）里的 canvas 解码会被无限推迟。
            args.Add("--disable-background-timer-throttling");

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
                // ⚠️ --disable-background-timer-throttling 已移到上面无条件区（光栅器需要）
                args.Add("--disable-features=CalculateNativeWinOcclusion");  // 禁用窗口遮挡计算
            }

            // 合并所有 enable-features
            if (enableFeatures.Count > 0)
            {
                args.Add($"--enable-features={string.Join(",", enableFeatures)}");
            }

            return string.Join(" ", args);
        }
    }
}
