using DockedTools.Features.Pages.WebApp.Browser.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 自适应栏色模块
    /// 移植自 Adaptive Tab Bar Colour（ATBC）：根据网页外观动态调整页面顶部色块与底部栏颜色。
    /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/entrypoints/atbc.content.ts
    ///
    /// TODO(顶栏大修合并后对接)：
    /// 顶栏控件目前不归本模块管，所以前景色只算不发。
    /// 合并后要做的三件事：
    ///   1. TopAppBarControl 补一个真正可用的 ResetForeground/ResetBackground（现在是空实现）；
    ///   2. 在 TopAppBarService 上加一个入口（例如 SetAdaptiveForeground(Color)），
    ///      由本模块在 ApplyAdaptiveBarColour 里调用，色值取 CurrentAdaptiveBarColour.Foreground；
    ///   3. ResetAdaptiveBarColour 里对应复位，避免退出网页页后顶栏残留网页色。
    /// 对接点已经备好，见 CurrentAdaptiveBarColour。
    ///
    /// 取色时机对齐上游 dynamic 模式：注入常驻脚本，由 click / resize / scroll / visibilitychange
    /// 与四个 MutationObserver 驱动，250ms trailing 节流后回传；
    /// 页面 DOM 变化、SPA 切换、主题切换都能跟着更新。
    ///
    /// ⚠️ 上游还会监听 transition{end,cancel} / animation{end,cancel}，本机分支刻意移除了 ——
    ///    页面每播完一段 CSS 动画就重跑一次整条流水线，且取到的是过渡态中间色。
    ///    详见 <see cref="Services.PageColourProbe.BuildMonitorScript"/> 的说明。
    ///    本机侧的兜底：单次写入前做脏值拦截（<see cref="ApplyAdaptiveBarColour"/>）。
    ///
    /// 作用范围：只写本页面自己的 WebPageTopAppBarBackground 色块和底部栏（都是 page 的一部分），
    /// 不触碰 TopAppBarService / TopAppBarControl —— 顶栏正在其他 worktree 大修。
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>一次性兜底探测前的渲染等待时间（毫秒）</summary>
        private const int AdaptiveBarColourDelayMs = 250;

        /// <summary>
        /// 总开关：置 false 即完全回退到系统默认栏色
        /// </summary>
        private static readonly bool AdaptiveBarColourEnabled = true;

        /// <summary>顶部色块的默认背景资源键（XAML 里也是这个值）</summary>
        private const string TopBarBackgroundResourceKey = "ApplicationPageBackgroundThemeBrush";

        private readonly AdaptiveBarColourOptions _adaptiveBarColourOptions = new();

        private readonly SolidColorBrush _adaptiveTopBarBrush = new();

        private CancellationTokenSource? _adaptiveBarColourCts;

        /// <summary>常驻取色脚本是否已注入（每个 CoreWebView2 实例只需注入一次）</summary>
        private bool _adaptiveMonitorInstalled;

        /// <summary>
        /// 最近一次自适应取色结果。
        /// 前景色不在这里下发（顶栏归顶栏自己管），但把算好的 Scheme / Foreground 暴露出来，
        /// 顶栏大修完成后可以直接对接，不用重算。
        /// </summary>
        public AdaptiveBarColourResult? CurrentAdaptiveBarColour { get; private set; }

        /// <summary>
        /// 安排一次自适应栏色更新（导航完成、主题变化等时机调用）。
        /// 优先确保常驻取色脚本已注入，之后颜色更新完全由页面事件驱动。
        /// </summary>
        private void ScheduleAdaptiveBarColourUpdate()
        {
            if (!AdaptiveBarColourEnabled)
            {
                return;
            }

            _ = EnsureAdaptiveColourSourceAsync();
        }

        private async Task EnsureAdaptiveColourSourceAsync()
        {
            CoreWebView2? core = WebView?.CoreWebView2;
            if (core is null)
            {
                return;
            }

            // 常驻脚本已生效，后续颜色由 OnAdaptiveColourMessageReceived 驱动
            if (_adaptiveMonitorInstalled)
            {
                return;
            }

            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    PageColourProbe.BuildMonitorScript(_adaptiveBarColourOptions.Query));

                core.WebMessageReceived -= OnAdaptiveColourMessageReceived;
                core.WebMessageReceived += OnAdaptiveColourMessageReceived;

                _adaptiveMonitorInstalled = true;
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 常驻取色脚本注入失败，回退一次性探测: {ex.Message}");
            }

            // 兜底：注入不成时退化为导航后一次性探测
            _adaptiveBarColourCts?.Cancel();
            _adaptiveBarColourCts?.Dispose();

            var cts = new CancellationTokenSource();
            _adaptiveBarColourCts = cts;
            await RunOneShotProbeAsync(cts.Token);
        }

        private async Task RunOneShotProbeAsync(CancellationToken token)
        {
            try
            {
                // 等首屏渲染完成再取色
                await Task.Delay(AdaptiveBarColourDelayMs, token);

                if (token.IsCancellationRequested || WebView?.CoreWebView2 is null)
                {
                    return;
                }

                var scheme = ActualTheme == ElementTheme.Dark
                    ? AdaptiveScheme.Dark
                    : AdaptiveScheme.Light;

                var result = await AdaptiveBarColourService.EvaluateAsync(
                    WebView.CoreWebView2,
                    scheme,
                    _adaptiveBarColourOptions,
                    WebView.CoreWebView2.Source);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() => ApplyAdaptiveBarColour(result));
            }
            catch (OperationCanceledException)
            {
                // 被后续导航取消，忽略
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 自适应栏色更新失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 常驻脚本回传的取色消息。事件在非 UI 线程触发，主题读取与 UI 更新都要回 UI 线程。
        /// </summary>
        private void OnAdaptiveColourMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!AdaptiveBarColourEnabled)
            {
                return;
            }

            AdaptiveTabColourData? data = PageColourProbe.ParseMessage(e.TryGetWebMessageAsString());
            if (data is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                var scheme = ActualTheme == ElementTheme.Dark
                    ? AdaptiveScheme.Dark
                    : AdaptiveScheme.Light;

                var result = AdaptiveBarColourService.Evaluate(
                    data,
                    scheme,
                    _adaptiveBarColourOptions,
                    WebView?.CoreWebView2?.Source);

                ApplyAdaptiveBarColour(result);
            });
        }

        /// <summary>
        /// 把取色结果写到本页面的顶部色块与底部栏。
        ///
        /// ⭐ 脏值拦截（即使上游重复触发也不往下污染）
        /// 这条链路的终点不是一次简单的笔刷赋值，而是一条连锁反应：
        ///     写 Background
        ///     → 改变 BottomBarHost.RequestedTheme
        ///     → 整棵子树的 ThemeResource 重新求值
        ///     → VisualState / 按钮样式跟着重刷
        ///     → 再一次布局测量
        /// 上游由 MutationObserver / scroll / click 驱动，同一结果重复回传非常常见。
        /// 因此在写入前逐个比对现值，值没变就完全不碰它 —— 这是整条链路上最廉价、
        /// 也最靠后的一道去重，无论上游怎么抖都不会穿透到这里。
        /// </summary>
        private void ApplyAdaptiveBarColour(AdaptiveBarColourResult result)
        {
            // 即使不落地也要刷新：这是对外暴露的查询结果，顶栏对接时会读它
            CurrentAdaptiveBarColour = result;

            var theme = result.Scheme == AdaptiveScheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

            bool applied = false;

            // 顶部色块：色值相同就不碰 Background（赋值会切断 ThemeResource 绑定）
            if (_adaptiveTopBarBrush.Color != result.Frame)
            {
                _adaptiveTopBarBrush.Color = result.Frame;
                WebPageTopAppBarBackground.Background = _adaptiveTopBarBrush;
                applied = true;
            }

            // 底部栏：服务内部同样按「主题 + 背景色」两个值比脏，返回是否真的写了东西
            // ⚠️ 用 |= 而非 ||，保证 SetBottomBar 一定会被调用
            applied |= Services.BottomBarThemeService.SetBottomBar(theme, result.Frame);

            if (!applied)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[WebBrowserPage] 自适应栏色: 方案={result.Scheme}, 来源={result.Reason}, " +
                $"色块={result.Frame}, 建议前景={result.Foreground}, 校正={result.Corrected}");
        }

        /// <summary>
        /// 恢复系统默认栏色（页面关闭或开关关闭时调用）
        /// </summary>
        private void ResetAdaptiveBarColour()
        {
            _adaptiveBarColourCts?.Cancel();
            _adaptiveBarColourCts?.Dispose();
            _adaptiveBarColourCts = null;

            CurrentAdaptiveBarColour = null;

            if (WebView?.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnAdaptiveColourMessageReceived;
            }

            // 常驻脚本挂在 CoreWebView2 上，随内核一起走；这里只清标记，
            // 之后再导航会重新注入（脚本内部有 __dockedToolsColourMonitor 去重）。
            _adaptiveMonitorInstalled = false;

            if (!AdaptiveBarColourEnabled)
            {
                return;
            }

            // 覆盖 Background 会切断 XAML 的 ThemeResource 绑定，而 ClearValue 同样回不到
            // ThemeResource（它也是本地值），所以显式取一次当前主题下的默认画刷重新赋值。
            if (Application.Current.Resources.TryGetValue(TopBarBackgroundResourceKey, out object? resource)
                && resource is Brush defaultBrush)
            {
                WebPageTopAppBarBackground.Background = defaultBrush;
            }
            else
            {
                WebPageTopAppBarBackground.ClearValue(Border.BackgroundProperty);
            }

            Services.BottomBarThemeService.SetBottomBar(ElementTheme.Default, null);

            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 自适应栏色已复位");
        }
    }
}
