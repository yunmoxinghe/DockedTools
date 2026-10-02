using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Browser.Services;
using DockedTools.Features.UnifiedCalls.TopAppBar;
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
    /// 顶栏文字/图标怎么跟着变：Reactor 版 AppTopBar 自带局部主题
    /// （TopAppBarService.SetTheme → AppTopBar 根元素 RequestedTheme），
    /// 子树里的文字/图标/输入框前景色全部靠主题资源跟随，所以本模块不用下发前景画刷，
    /// 只按取到的亮暗切主题即可。写之前先用 IsWritingTarget 确认顶栏还归本页 ——
    /// 取色回调（常驻脚本回传、导航完成）完全可能在本页已经切走之后才到。
    ///
    /// 取色时机对齐上游 dynamic 模式：注入常驻脚本，由 click / resize / scroll / visibilitychange、
    /// transition 与 animation 结束事件、以及四个 MutationObserver 驱动，250ms trailing 节流后回传；
    /// 页面 DOM 变化、SPA 切换、主题切换都能跟着更新。
    ///
    /// 作用范围：只写本页面自己的 WebPageTopAppBarBackground 色块和底部栏（都是 page 的一部分），
    /// 不触碰 TopAppBarService / TopAppBarControl —— 顶栏正在其他 worktree 大修。
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>一次性探测：首次采样前的渲染等待（毫秒）</summary>
        private const int AdaptiveBarColourFirstDelayMs = 250;

        /// <summary>
        /// 一次性探测：迟到采样相对首次的额外等待（毫秒）。
        /// 对齐常驻脚本的 load + 1s 双采样 —— SPA 常在首屏之后才渲染出真实外观
        /// （数据回来才上色），只采一次会取到白。两次合计约 1s。
        /// </summary>
        private const int AdaptiveBarColourLateDelayMs = 750;

        /// <summary>顶部色块的默认背景资源键（XAML 里也是这个值）</summary>
        private const string TopBarBackgroundResourceKey = "ApplicationPageBackgroundThemeBrush";

        /// <summary>
        /// 取色配置。每次取用时从设置现造（<see cref="AdaptiveColourSettings.CreateOptions"/>），
        /// 设置页改完即时生效，不用等页面重建。
        /// </summary>
        private AdaptiveBarColourOptions AdaptiveOptions => AdaptiveColourSettings.CreateOptions();

        private readonly SolidColorBrush _adaptiveTopBarBrush = new();

        private CancellationTokenSource? _adaptiveBarColourCts;

        /// <summary>常驻取色脚本是否已注入（每个 CoreWebView2 实例只需注入一次）</summary>
        private bool _adaptiveMonitorInstalled;

        /// <summary>常驻脚本当前挂在哪个内核实例上（内核被重建后要重新注入）</summary>
        private CoreWebView2? _adaptiveMonitorCore;

        /// <summary>
        /// 常驻脚本当前生效的取色选择器（规则选择器优先于全局）。
        /// 缓存的取色数据是按它探出来的 —— 它变了就必须重新探一次，
        /// 只把新选择器推给脚本是不够的，当前页面会一直停在旧结果上。
        /// </summary>
        private string? _adaptiveInjectedQuery;

        /// <summary>
        /// 上次取色时的 URL。用来识别 SPA 的 pushState 换路由 ——
        /// 那不会触发 NavigationCompleted，只触发 HistoryChanged。
        /// </summary>
        private string? _adaptiveLastUrl;

        /// <summary>当前文档里的常驻脚本是否处于挂起状态（命中 COLOUR 规则时）</summary>
        private bool _adaptiveSuspended;

        /// <summary>
        /// 当前 URL 命中的站点规则（ATBC 的 Rule）。导航完成 / 设置变化时重算。
        /// </summary>
        private AdaptiveColourRule? _adaptiveRule;

        /// <summary>
        /// 最近一次探测到的页面颜色数据（主题切换时用它重算，不必重新探测）</summary>
        private AdaptiveTabColourData? _lastAdaptiveData;

        /// <summary>
        /// 按当前 URL 和方案匹配站点规则。
        /// 对应 ATBC 的 pref.getRule(url, scheme)：
        /// https://github.com/atbc-org/Adaptive-Tab-Bar-Colour/blob/main/src/utils/preference.ts
        /// </summary>
        private AdaptiveColourRule? ResolveAdaptiveRule()
            => AdaptiveColourRuleTable.Match(
                AdaptiveColourSettings.Rules,
                WebView?.CoreWebView2?.Source,
                ResolveAdaptiveScheme());

        /// <summary>本次取色用哪个选择器：站点规则的 QUERY_SELECTOR 优先于全局选择器</summary>
        private string? ResolveEffectiveQuery()
            => AdaptiveBarColourService.ResolveQuery(AdaptiveOptions, _adaptiveRule);

        /// <summary>最近一次真正写到 UI 上的结果，用于应用前去重</summary>
        private AdaptiveBarColourResult? _appliedAdaptiveBarColour;

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
        /// <param name="probeOnly">
        /// true 时跳过常驻脚本，只对当前文档做一次性探测。
        /// 用于 Chromium 内部页面（错误页）这类 AddScriptToExecuteOnDocumentCreated 不生效、
        /// 但仍能 ExecuteScript 读取的文档。
        /// </param>
        private void ScheduleAdaptiveBarColourUpdate(bool probeOnly = false)
        {
            if (!AdaptiveColourSettings.Enabled)
            {
                return;
            }

            _adaptiveLastUrl = WebView?.CoreWebView2?.Source;
            _adaptiveRule = ResolveAdaptiveRule();
            _ = EnsureAdaptiveColourSourceAsync(probeOnly);
        }

        /// <summary>
        /// URL 变化（HistoryChanged）后重新取色。
        ///
        /// SPA 用 history.pushState 换路由时 <c>NavigationCompleted</c> 不触发，
        /// 光靠页面里的 DOM 监听也靠不住（换的是内容，未必增删 STYLE / meta 节点），
        /// 结果是「在 SPA 里点链接翻页，栏色一直停在进站那一次」。
        /// 上游 Firefox 端由 URL 变化事件驱动 background 重新向页面要色，这里对齐这个语义。
        ///
        /// 前进/后退会同时触发 HistoryChanged 和 NavigationCompleted —— 用 URL 去重，只跑一次。
        /// </summary>
        private void ScheduleAdaptiveBarColourUpdateForUrlChange()
        {
            if (!AdaptiveColourSettings.Enabled)
            {
                return;
            }

            CoreWebView2? core = WebView?.CoreWebView2;
            if (core is null)
            {
                return;
            }

            string url = core.Source ?? string.Empty;
            if (string.Equals(url, _adaptiveLastUrl, StringComparison.Ordinal))
            {
                return;
            }

            // 先把 URL 落下来再干活：EnsureAdaptiveColourSourceAsync 是异步的，
            // 这段时间内又来一次 HistoryChanged 就靠它挡掉重复探测。
            _adaptiveLastUrl = url;

            // 换 URL 可能换掉命中的站点规则（不同站点的 COLOUR / THEME_COLOUR 规则不同）
            _adaptiveRule = ResolveAdaptiveRule();

            // 即便选择器没变，页面外观也整个换了 —— 缓存数据作废，强制重探一次
            _ = EnsureAdaptiveColourSourceAsync(forceProbe: true);
        }

        /// <param name="probeOnly">
        /// true 时跳过常驻脚本，只对当前文档做一次性探测。
        /// 用于 Chromium 内部页面（错误页）这类 AddScriptToExecuteOnDocumentCreated 不生效、
        /// 但仍能 ExecuteScript 读取的文档。
        /// </param>
        /// <param name="forceProbe">
        /// true 时即便常驻脚本已生效、选择器也没变，也强制重探一次。
        /// 用于 SPA 换路由：页面外观整个换了，但选择器没变，去重逻辑认不出来。
        /// </param>
        /// <param name="probeCurrentDocument">
        /// false 时只注入常驻脚本，不对当前文档做一次性探测。
        /// 用于 CoreWebView2 刚就绪、还没导航的时机 —— 那时探测只会取到空白页。
        /// </param>
        private async Task EnsureAdaptiveColourSourceAsync(
            bool probeOnly = false,
            bool forceProbe = false,
            bool probeCurrentDocument = true)
        {
            // 总开关关着就一个脚本都别注入。检查放在这里而不是只放在调用点：
            // WebView 初始化完成时会直接调本方法（要赶在首次导航前注入），那条路径不经
            // ScheduleAdaptiveBarColourUpdate，只靠调用点检查会漏。
            if (!AdaptiveColourSettings.Enabled)
            {
                return;
            }

            CoreWebView2? core = WebView?.CoreWebView2;
            if (core is null)
            {
                return;
            }

            // ATBC：COLOUR 规则直接定色，并且会把 content script 挂起（mode: "suspend"）——
            // 页面外观完全不参与。这里同理：不注入、不探测，直接按规则色上栏。
            // 必须先挂起页面里已经存在的脚本：从普通站点导航过来时它还活着，
            // 不撤掉的话会持续回传页面颜色，把规则指定的颜色覆盖掉。
            if (_adaptiveRule is { Type: AdaptiveRuleType.Colour })
            {
                await PushRuntimeOptionsAsync();

                AdaptiveBarColourResult specified = AdaptiveBarColourService.Evaluate(
                    data: null,
                    ResolveAdaptiveScheme(),
                    AdaptiveOptions,
                    core.Source,
                    _adaptiveRule);

                DispatcherQueue.TryEnqueue(() => ApplyAdaptiveBarColour(specified));
                _adaptiveSuspended = true;
                return;
            }

            // 从挂起恢复（导航到了普通站点 / 用户删掉了 COLOUR 规则）：
            // 脚本在当前文档里已经执行过、因挂起直接 return 没注册监听，
            // 光推运行时变量救不回来 —— 清注入标记重新注入，让新文档重新注册监听，
            // 当前文档则由下面的一次性探测补上。
            if (_adaptiveSuspended)
            {
                _adaptiveSuspended = false;
                _adaptiveMonitorInstalled = false;
                _adaptiveMonitorCore = null;
            }

            // 动态刷新关掉时（ATBC 的 dynamic=false）：不注入常驻脚本，只在每次导航后取一次色。
            // probeOnly：当前文档是错误页之类拿不到常驻脚本的文档，同样只能一次性探测。
            if (probeOnly || !AdaptiveColourSettings.Dynamic)
            {
                // 动态刷新关掉时脚本里的 dispatch 会自己挡掉回传（开关在回传前查，不是启动时查），
                // 所以这里不用操心卸载监听 —— 推一次变量就够了，开关再打开时监听还在，立刻能恢复。
                await PushRuntimeOptionsAsync();

                if (probeCurrentDocument)
                {
                    await RestartOneShotProbeAsync();
                }

                return;
            }

            // 常驻脚本已生效，且还是同一个内核实例 —— 后续颜色由 OnAdaptiveColourMessageReceived 驱动。
            // 内核被重建过（页面恢复 / 浏览器进程崩溃后的 RecreateWebView）时必须重新注入：
            // 脚本和消息订阅都挂在旧实例上，只认布尔标记会让取色永久静默失效。
            if (_adaptiveMonitorInstalled && ReferenceEquals(_adaptiveMonitorCore, core))
            {
                // 脚本只在注入时带一次默认值，每个新文档的运行时变量都是空的 ——
                // 这里按当前页面的规则推一次，否则规则里的选择器永远不生效。
                string? query = ResolveEffectiveQuery();
                await PushRuntimeOptionsAsync();

                // 选择器换了（设置里改了 / 导航到了另一条规则的站点）：
                // 缓存数据是按旧选择器探的，重算也还是旧结果，必须重新探一次。
                // forceProbe：SPA 换路由，选择器没变但页面整个换了，同样必须重探。
                if (forceProbe || !string.Equals(query, _adaptiveInjectedQuery, StringComparison.Ordinal))
                {
                    _adaptiveInjectedQuery = query;
                    await RestartOneShotProbeAsync();
                }

                return;
            }

            try
            {
                string? injectedQuery = ResolveEffectiveQuery();

                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    PageColourProbe.BuildMonitorScript(injectedQuery));

                core.WebMessageReceived -= OnAdaptiveColourMessageReceived;
                core.WebMessageReceived += OnAdaptiveColourMessageReceived;

                _adaptiveMonitorInstalled = true;
                _adaptiveMonitorCore = core;
                _adaptiveInjectedQuery = injectedQuery;

                // 脚本只在注入时带一次默认值，每个新文档的运行时变量都是空的 ——
                // 这里按当前页面的规则推一次，否则规则里的选择器永远不生效。
                await PushRuntimeOptionsAsync();

                // AddScriptToExecuteOnDocumentCreated 只对【注入之后才创建的文档】生效。
                // 正常流程下我们在 CoreWebView2 就绪后、首次导航之前就注入（见 WebView.cs），
                // 当前文档拿得到脚本，不需要补探测；
                // 只有事后补注入（内核重建、错误页恢复等）才需要 —— 那时文档早就创建完了，
                // 不补一次就会一直不取色，直到用户再导航一次。
                if (probeCurrentDocument)
                {
                    await RestartOneShotProbeAsync();
                }

                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 常驻取色脚本注入失败，回退一次性探测: {ex.Message}");
            }

            // 兜底：注入不成时退化为导航后一次性探测
            if (probeCurrentDocument)
            {
                await RestartOneShotProbeAsync();
            }
        }

        /// <summary>
        /// 重开一次性探测（取消上一次未完成的等待，避免旧页面的取色结果后到覆盖新页面）
        /// </summary>
        private async Task RestartOneShotProbeAsync()
        {
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
                // 首屏采样：尽快给个结果，别让栏色长时间停在默认色上
                await Task.Delay(AdaptiveBarColourFirstDelayMs, token);
                await ProbeOnceAsync(token, resetOnFailure: true);

                // 迟到采样：SPA 常在首屏之后才渲染出真实外观。
                // 颜色没变会被 ApplyAdaptiveBarColour 的去重吃掉，不会多刷一次；
                // 这次失败也不复位 —— 第一次已经给了个能用的结果，别把它清掉。
                await Task.Delay(AdaptiveBarColourLateDelayMs, token);
                await ProbeOnceAsync(token, resetOnFailure: false);
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

        /// <summary>在当前文档取一次色并应用</summary>
        private async Task ProbeOnceAsync(CancellationToken token, bool resetOnFailure)
        {
            if (token.IsCancellationRequested || WebView?.CoreWebView2 is null)
            {
                return;
            }

            // 探测走 EvaluateAsync 拿不到数据，这里拆开自己探，好在主题切换时复用。
            // 选择器按站点规则走（规则的 QUERY_SELECTOR 优先于全局选择器）
            AdaptiveTabColourData? data = await PageColourProbe.ProbeAsync(
                WebView.CoreWebView2,
                ResolveEffectiveQuery());

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 探测不到（错误页上 ExecuteScript 被拒 / 内核还没准备好）：
            // 不要用兜底色去刷栏子，直接回落系统默认，顺带清掉上一个网页残留的颜色与主题。
            if (data is null)
            {
                if (resetOnFailure)
                {
                    DispatcherQueue.TryEnqueue(ResetAdaptiveBarColour);
                }

                return;
            }

            // 全屏（视频播放等）：取到的是视频画面而不是页面外观，保持当前栏色不动。
            // 与常驻脚本侧一致 —— 那边遇到全屏也是直接 return 不回传。
            // 一次性探测原本是返回 null，会被当成「探测失败」去复位栏色，两边语义打架。
            if (string.Equals(data.Special, "fullscreen", StringComparison.Ordinal))
            {
                return;
            }

            var result = AdaptiveBarColourService.Evaluate(
                data,
                ResolveAdaptiveScheme(),
                AdaptiveOptions,
                WebView.CoreWebView2.Source,
                _adaptiveRule);

            DispatcherQueue.TryEnqueue(() => ApplyAdaptiveBarColour(result, data));
        }

        /// <summary>
        /// 常驻脚本回传的取色消息。事件在非 UI 线程触发，主题读取与 UI 更新都要回 UI 线程。
        /// </summary>
        private void OnAdaptiveColourMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // 动态刷新关掉时脚本不该回传（脚本侧也有同样的开关，这里是双保险）；
            // COLOUR 规则下页面外观不参与取色，同样忽略（对应上游的 suspend）。
            if (!AdaptiveColourSettings.Enabled ||
                !AdaptiveColourSettings.Dynamic ||
                _adaptiveRule is { Type: AdaptiveRuleType.Colour })
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
                var scheme = ResolveAdaptiveScheme();

                var result = AdaptiveBarColourService.Evaluate(
                    data,
                    scheme,
                    AdaptiveOptions,
                    WebView?.CoreWebView2?.Source,
                    _adaptiveRule);

                ApplyAdaptiveBarColour(result, data);
            });
        }

        /// <summary>
        /// 主题切换后按新方案重算栏色。
        ///
        /// 先用最近一次的取色数据立刻重算一次（省一次 ExecuteScript，手感即时），
        /// 再安排一次探测把数据刷新掉 —— 切主题不只是我们这边换个方案，
        /// 页面自己也会跟着换肤（prefers-color-scheme），缓存的 page 采样是旧主题下取的，
        /// 光重算等于拿过期数据换个系数再加工一遍。
        /// 页面换肤时也不指望常驻脚本回传：那四个 MutationObserver 盯的是
        /// STYLE 节点增删 / meta 属性 / darkReader 标记，不覆盖媒体查询生效时的重绘。
        /// </summary>
        private void ReapplyAdaptiveBarColourForThemeChange()
        {
            if (!AdaptiveColourSettings.Enabled)
            {
                return;
            }

            // 方案可能按 scheme 区分，主题换了要重新匹配规则
            _adaptiveRule = ResolveAdaptiveRule();

            if (_lastAdaptiveData is null)
            {
                // 还没有取色数据（常驻脚本没注入成功 / 页面还没回传），退化为重新安排一次探测
                ScheduleAdaptiveBarColourUpdate();
                return;
            }

            var scheme = ResolveAdaptiveScheme();

            var result = AdaptiveBarColourService.Evaluate(
                _lastAdaptiveData,
                scheme,
                AdaptiveOptions,
                WebView?.CoreWebView2?.Source,
                _adaptiveRule);

            ApplyAdaptiveBarColour(result);

            // 即时那次已经上完了，这里补一次探测刷新数据（见方法注释）。
            // 会被后续导航 / 设置变化触发的同款探测取消，不会叠加。
            _ = RestartOneShotProbeAsync();
        }

        /// <summary>
        /// 当前期望的配色方案：默认跟随系统/应用主题，设置里强制浅/深时以设置为准。
        /// </summary>
        private AdaptiveScheme ResolveAdaptiveScheme()
            => AdaptiveColourSettings.Scheme switch
            {
                AdaptiveColourSchemeMode.Light => AdaptiveScheme.Light,
                AdaptiveColourSchemeMode.Dark => AdaptiveScheme.Dark,
                _ => ActualTheme == ElementTheme.Dark ? AdaptiveScheme.Dark : AdaptiveScheme.Light
            };

        private void ApplyAdaptiveBarColour(AdaptiveBarColourResult result, AdaptiveTabColourData? data = null)
        {
            if (data is not null)
            {
                _lastAdaptiveData = data;
            }

            CurrentAdaptiveBarColour = result;

            // 去重：scroll / resize / click 会反复触发回传，色值和方案都没变时不必再写一次画刷，
            // 也不必再切一次底栏 RequestedTheme（它会让底栏整棵子树重新主题化）
            if (_appliedAdaptiveBarColour is { } applied &&
                applied.Scheme == result.Scheme &&
                SameColor(applied.Frame, result.Frame))
            {
                return;
            }

            _appliedAdaptiveBarColour = result;

            var theme = result.Scheme == AdaptiveScheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

            // 只改本页面的顶部色块，顶栏控件本身的背景一律不动
            _adaptiveTopBarBrush.Color = result.Frame;
            WebPageTopAppBarBackground.Background = _adaptiveTopBarBrush;

            // 顶栏文字/图标：按取到的亮暗切顶栏局部主题，前景色由主题资源自动跟上。
            // 沉浸式下顶栏没有自己的底衬（SetChromeVisible(false)），背景就是上面那个色块，
            // 所以色块变了而前景不跟着切，就会出现"白底白字 / 黑底黑字"。
            if (TopAppBarService.IsWritingTarget(this))
            {
                TopAppBarService.SetTheme(theme);
            }

            if (IsBottomBarHostOwner())
            {
                Services.BottomBarThemeService.SetBottomBar(theme, result.Frame);
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
            _lastAdaptiveData = null;
            _appliedAdaptiveBarColour = null;
            _adaptiveRule = null;
            _adaptiveSuspended = false;
            _adaptiveMonitorCore = null;
            _adaptiveInjectedQuery = null;
            _adaptiveLastUrl = null;

            if (WebView?.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnAdaptiveColourMessageReceived;
            }

            // 常驻脚本挂在 CoreWebView2 上，随内核一起走；这里只清标记，
            // 之后再导航会重新注入（脚本内部有 __dockedToolsColourMonitor 去重）。
            _adaptiveMonitorInstalled = false;

            // 开关可能是页面开着的时候被关掉的，画刷上还留着网页色 ——
            // 所以不按当前开关状态提前返回，一律走完整复位（幂等，重复调用无副作用）。
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

            // 顶栏主题复位：不复位的话退出网页页后顶栏会一直顶着上一个网页的亮/暗。
            // 走 ScopeFor(this) 而不是 SetTheme —— 后者写的是【当前写入目标】，
            // 页面切走之后那已经是新前台页，照写会把人家的顶栏主题改掉。
            TopAppBarService.ScopeFor(this).ThemeMode = TopBarThemeMode.System;

            if (IsBottomBarHostOwner())
            {
                Services.BottomBarThemeService.SetBottomBar(ElementTheme.Default, null);
            }

            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 自适应栏色已复位");
        }

        private static bool SameColor(Windows.UI.Color a, Windows.UI.Color b)
            => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

        /// <summary>
        /// BottomBarThemeService 是静态单例，一次只认一个底部栏宿主。
        /// 页面被缓存 / 重建时可能有多个实例并存，写之前先确认宿主是本页的，
        /// 否则会把别的页面的底部栏改成本页的网页色。
        /// </summary>
        private bool IsBottomBarHostOwner()
            => ReferenceEquals(Services.BottomBarThemeService.RegisteredHost, BottomBarHost);

        /// <summary>
        /// 订阅设置页改动（先减后加，重复调用安全 —— 页面可能被 LRU 缓存后重新进入）。
        /// </summary>
        private void SubscribeAdaptiveColourSettings()
        {
            AdaptiveColourSettings.Changed -= OnAdaptiveColourSettingsChanged;
            AdaptiveColourSettings.Changed += OnAdaptiveColourSettingsChanged;
        }

        private void UnsubscribeAdaptiveColourSettings()
        {
            AdaptiveColourSettings.Changed -= OnAdaptiveColourSettingsChanged;
        }

        private void OnAdaptiveColourSettingsChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 关掉总开关：立刻复位栏色（含顶栏局部主题、底栏），不留上一个网页的亮/暗
                if (!AdaptiveColourSettings.Enabled)
                {
                    ResetAdaptiveBarColour();
                    return;
                }

                // 规则/选择器/亮度参数都可能变了，重新匹配一次
                _adaptiveRule = ResolveAdaptiveRule();
                _appliedAdaptiveBarColour = null;

                // 有缓存数据时先按新参数重算一次，手感即时
                ReapplyAdaptiveBarColourForThemeChange();

                // 再按新设置重建取色来源：命中 COLOUR 规则就直接上色，
                // 否则把新的选择器 / 动态开关推给当前文档，必要时注入常驻脚本并补一次探测。
                _ = EnsureAdaptiveColourSourceAsync();
            });
        }

        /// <summary>
        /// 把取色选择器、动态刷新开关、挂起开关推给当前文档。
        /// 常驻脚本只在注入时带一次默认值，之后每个新文档的运行时变量都是空的，
        /// 而内核又没有「移除已注入脚本」的 API —— 改这三项只能靠改运行时变量。
        /// </summary>
        private async Task PushRuntimeOptionsAsync()
        {
            CoreWebView2? core = WebView?.CoreWebView2;
            if (core is null)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    PageColourProbe.BuildRuntimeOptionsScript(
                        ResolveEffectiveQuery(),
                        AdaptiveColourSettings.Dynamic,
                        _adaptiveRule is { Type: AdaptiveRuleType.Colour }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 推送取色运行参数失败: {ex.Message}");
            }
        }
    }
}
