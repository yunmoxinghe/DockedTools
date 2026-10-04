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
    /// 取色时机对齐上游 dynamic 模式：注入常驻脚本，由 click / resize / scroll（捕获阶段）/ visibilitychange、
    /// 五个 MutationObserver（换肤属性 / darkReader / meta theme-color 属性 / meta 增删 / STYLE 增删）
    /// 与一个 ResizeObserver 驱动，250ms trailing 节流后回传；
    /// 页面 DOM 变化、SPA 切换、主题切换都能跟着更新。
    /// （刻意不监听 transition / animation 结束事件，理由见 PageColourProbe 类注释差异第 6 条。）
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

        /// <summary>
        /// 顶部色块 / 底栏背景换色的过渡时长（毫秒）。
        ///
        /// <para>取色是 250ms trailing 节流驱动的，一次滚动可能连着来十几个色值；
        /// 没有过渡就是一串硬跳，从一个亮站点切到暗站点尤其刺眼。</para>
        ///
        /// <para>300ms 略长于节流窗口：新色还没算完、上一个动画也没跑完就会被下一个接管，
        /// 视觉上是连续的一段滚色而不是一串首尾相接的短动画。
        /// 代价是颜色落后页面最多 ~0.5s，比跳变可接受得多。</para>
        /// </summary>
        private const int AdaptiveTransitionMs = Services.BottomBarThemeService.DefaultColourTransitionMs;

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
        /// 用于 Chromium 内部页面（错误页）这类文档。
        /// 【实测更正】旧注释说「错误页上 AddScriptToExecuteOnDocumentCreated 不生效」是错的 ——
        /// 错误页（chrome-error://chromewebdata/）上常驻脚本照样注入执行，
        /// window.chrome.webview 也在，ExecuteScript 同样能读。保留这条一次性探测路径只是多一层保险。
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
        /// 用于 Chromium 内部页面（错误页）这类文档。
        /// 【实测更正】旧注释说「错误页上 AddScriptToExecuteOnDocumentCreated 不生效」是错的 ——
        /// 错误页（chrome-error://chromewebdata/）上常驻脚本照样注入执行，
        /// window.chrome.webview 也在，ExecuteScript 同样能读。保留这条一次性探测路径只是多一层保险。
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
            // probeOnly：只对当前文档做一次性探测、不依赖常驻脚本的场合（动态刷新关闭等）。
            // 注：错误页其实拿得到常驻脚本（实测），不再是这一支的必要条件。
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

                // 迟到采样之后页面已经稳定，这时把常驻脚本的触发埋点读出来 ——
                // 能看到 load / fallback / 各类观察者各触发了几次、被去重还是真发出去了。
                await DumpAdaptiveColourLogAsync();
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
        /// 把常驻脚本里那个触发埋点缓冲读出来，打进 Debug 日志。
        ///
        /// <para>脚本跑在网页里，<c>console</c> 输出不进托管侧的日志通道；托管侧原本又只在
        /// 「颜色真的变了并写进画刷」之后才打一条，触发了但被去重 / 闸门挡掉的那些无声无息 ——
        /// 于是「取色刷新到底频不频繁」只能靠猜。这里是唯一的观测口。</para>
        ///
        /// <para>每次导航最多读一次（在一次探测流水线的末尾），一次 ExecuteScript，
        /// 不会影响取色本身的时序。</para>
        /// </summary>
        private async Task DumpAdaptiveColourLogAsync()
        {
            if (WebView?.CoreWebView2 is not { } core)
            {
                return;
            }

            try
            {
                string? raw = await core.ExecuteScriptAsync(PageColourProbe.BuildLogScript());

                if (PageColourProbe.FormatLog(raw) is { } text)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 取色触发埋点: {text}");
                }

                // 采样链快照：看最终色块是从哪一层元素来的。
                // 「页面看着是浅黄、栏色却是白」这类问题的唯一观测口 ——
                // 可能是白色 header 盖在最上层（那取白是对的），也可能是渐变没解析（那是 bug）。
                string? traceRaw = await core.ExecuteScriptAsync(PageColourProbe.BuildTraceScript());

                if (PageColourProbe.FormatTrace(traceRaw) is { } traces)
                {
                    foreach (string line in traces)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 采样链: {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 读取取色埋点失败: {ex.Message}");
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

            // 探测不到（内核还没准备好 / 脚本被 CSP 挡住等；实测错误页上 ExecuteScript 是可用的）：
            // 不要用兜底色去刷栏子，直接回落系统默认，顺带清掉上一个网页残留的颜色与主题。
            if (data is null)
            {
                if (resetOnFailure)
                {
                    DispatcherQueue.TryEnqueue(() => ResetAdaptiveBarColour());
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

            // 一次性探测同样要过这道闸门（此处会在 UI 线程上比一次最终结果）。
            // 与常驻脚本同源的问题：RestartOneShotProbeAsync 只取消 CTS，
            // 但 ExecuteScriptAsync 已经发出去的那一轮不会因为取消而消失，
            // 它的结果仍会在 ProbeOnceAsync 里走到这里。
            CoreWebView2 core = WebView.CoreWebView2!;

            if (!PageColourProbe.SameLocation(data.Url, core.Source))
            {
                return;
            }

            if (IsStaleDocument(data.DocOrigin))
            {
                return;
            }

            var result = AdaptiveBarColourService.Evaluate(
                data,
                ResolveAdaptiveScheme(),
                AdaptiveOptions,
                core.Source,
                _adaptiveRule);

            DispatcherQueue.TryEnqueue(() => ApplyAdaptiveBarColour(result, data));
        }

        /// <summary>最近一次接受的文档代次（<c>performance.timeOrigin</c>）。0 = 还没见过。</summary>
        private double _adaptiveDocOrigin;

        /// <summary>导航发起时被判为过期的那个代次；新文档的第一发回传必须比它更新。</summary>
        private double _adaptiveStaleDocOrigin;

        private long _adaptiveStaleMarkedTicks;

        private bool _adaptiveAwaitingNewDocument;

        /// <summary>
        /// 「等新文档」状态的最长维持时间。超时就放弃拦截 —— 见 <see cref="IsStaleDocument(double)"/>。
        /// </summary>
        private const int AwaitingNewDocumentTimeoutMs = 1500;

        /// <summary>
        /// 导航开始时调用：把当前代次标记为「已过期」。
        /// 由 <c>CoreWebView2.NavigationStarting</c> 触发。
        /// </summary>
        private void MarkAdaptiveDocumentStale()
        {
            _adaptiveStaleDocOrigin = _adaptiveDocOrigin;
            _adaptiveStaleMarkedTicks = Environment.TickCount64;
            _adaptiveAwaitingNewDocument = true;
        }

        /// <summary>
        /// 文档代次闸门：这份回传是否来自一个<b>比当前已接受的文档更旧</b>的文档。
        /// 与 URL 闸门（<see cref="PageColourProbe.SameLocation"/>）是两道不同的闸：
        /// URL 挡「换到别的站点」，代次挡「同一站点刷新 / 重新进入」。
        /// </summary>
        /// <param name="docOrigin">回传里带的 <c>performance.timeOrigin</c></param>
        private bool IsStaleDocument(double docOrigin)
        {
            // 老版本脚本没有 doc 字段（保持 0）：拿不到就不拦，让位给 URL 闸门。
            // 误扣一份本质正常的回传会让自适应彻底不生效，比偶尔串色更难排查。
            if (docOrigin <= 0)
            {
                return false;
            }

            if (_adaptiveAwaitingNewDocument)
            {
                // ⭐ 自愈：等太久还没等到「更新的文档」，说明这次 NavigationStarting
                // 根本没换文档（同文档锚点跳转也会发 NavigationStarting）。
                // 再拦下去就是永久失效 —— 宁可放过一次可能的串色，也不能让取色彻底哑掉。
                if (Environment.TickCount64 - _adaptiveStaleMarkedTicks > AwaitingNewDocumentTimeoutMs)
                {
                    _adaptiveAwaitingNewDocument = false;
                }
                else if (docOrigin > _adaptiveStaleDocOrigin)
                {
                    // 新文档的第一发到了，解除等待
                    _adaptiveAwaitingNewDocument = false;
                    _adaptiveDocOrigin = docOrigin;
                    return false;
                }
                else
                {
                    return true;
                }
            }

            if (_adaptiveDocOrigin <= 0)
            {
                _adaptiveDocOrigin = docOrigin;
                return false;
            }

            // 比已接受的最新的那个还小 ⇒ 旧文档的迟到回传
            if (docOrigin < _adaptiveDocOrigin)
            {
                return true;
            }

            if (docOrigin > _adaptiveDocOrigin)
            {
                _adaptiveDocOrigin = docOrigin;
            }

            return false;
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

            // ⭐ 串色闸门：这份颜色可能属于上一个文档。
            // 常驻脚本是异步的 —— 250ms 节流窗口里的回传完全可能迟到。
            // 典型场景：页面在滚动（正在回传暗色），此时点了链接导航到新站，
            // 老文档的最后一发回传踩在新文档的第一次采样之前到达，
            // 于是「老页面的深色」被刷到了「新页面的栏子」上，而且因为去重还会粘住。
            // 老版本脚本不带 url 字段时 SameLocation 一律放行 —— 拿不到就别拦，
            // 误扣整张本质正常的回传会让自适应彻底不生效，比偶尔串色更难排查。
            string? currentUrl = WebView?.CoreWebView2?.Source;
            if (!PageColourProbe.SameLocation(data.Url, currentUrl))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[WebBrowserPage] 丢弃过期文档的取色回传（URL 不匹配）");
                return;
            }

            // 第二道闸：文档代次。刷新 / 重新进入同一地址时 URL 完全相同，
            // 旧文档 250ms 节流窗口里那一发会先刷一次旧色再被纠正，观感是「栏子闪一下」。
            if (IsStaleDocument(data.DocOrigin))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[WebBrowserPage] 丢弃过期文档的取色回传（文档代次更旧）");
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                // Enqueue 之后才上 UI 线程，这里再看一次 Source：
                // 排队的这一小段时间里又可能已经导航走了，写晚了同样是串色。
                if (!PageColourProbe.SameLocation(data.Url, WebView?.CoreWebView2?.Source))
                {
                    return;
                }

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

        /// <summary>
        /// 把一次取色结果写到 UI 上。
        /// </summary>
        /// <param name="result">取色结果</param>
        /// <param name="data">这次取色的原始数据（主题切换时用它重算，不必重新探测）</param>
        /// <param name="immediate">
        /// true 时不做淡入，直接落色。只用于「切换 page 时用记忆色预热」——
        /// 那一帧必须立刻是对的，带着 300ms 尾巴等于没预热。
        /// </param>
        private void ApplyAdaptiveBarColour(
            AdaptiveBarColourResult result,
            AdaptiveTabColourData? data = null,
            bool immediate = false)
        {
            if (data is not null)
            {
                _lastAdaptiveData = data;
            }

            CurrentAdaptiveBarColour = result;

            // 去重：scroll / resize / click 会反复触发回传，色值和方案都没变时不必再写一次画刷，
            // 也不必再切一次底栏 RequestedTheme（它会让底栏整棵子树重新主题化）
            // Bottom 必须一起比：顶栏底栏分别取色之后，完全可能出现「页面顶部没变、
            // 底部换了一块」的情况 —— 只比 Frame 的话底栏就永远停在旧色上不更新了。
            if (_appliedAdaptiveBarColour is { } applied &&
                applied.Scheme == result.Scheme &&
                applied.BottomScheme == result.BottomScheme &&
                SameColor(applied.Frame, result.Frame) &&
                SameColor(applied.Bottom, result.Bottom))
            {
                return;
            }

            _appliedAdaptiveBarColour = result;

            // 记进站点记忆：下次切回这个站点（哪怕页面实例已被 LRU 淘汰重建）能立刻预热。
            // 放在真正落色之后 —— 去重 return 的那些说明颜色没变，记忆里本来就是这个值。
            AdaptiveBarColourMemory.Remember(CurrentAdaptiveUrl(), result);

            int transitionMs = immediate ? 0 : AdaptiveTransitionMs;

            var theme = result.Scheme == AdaptiveScheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

            // 只改本页面的顶部色块，顶栏控件本身的背景一律不动。
            // 色块是 Border，XAML 的 BackgroundTransition 不支持 Border（只支持
            // Grid / StackPanel / ContentPresenter），所以这里自己驱动常驻画刷做淡入。
            // 两个分支都走过渡：已有常驻画刷就直接滚过去；Background 还停在 ThemeResource 上
            // 就先接住当前可见色、挂上画刷再滚。时长为 0 时 AnimateTo 内部自行退化为立即赋值。
            if (ReferenceEquals(WebPageTopAppBarBackground.Background, _adaptiveTopBarBrush))
            {
                BrushColourTransition.AnimateTo(
                    WebPageTopAppBarBackground, _adaptiveTopBarBrush, result.Frame, transitionMs);
            }
            else
            {
                // 首次挂载：先让常驻画刷接住【当前可见的背景色】，挂上去之后再滚到目标色。
                // 直接把一支全透明的新画刷换上去会先闪一下（白 → 透明 → 网页色），
                // 从当前色起步则是一次干净的淡入，首次上色也就不用退化成硬跳了。
                Windows.UI.Color start = WebPageTopAppBarBackground.Background is SolidColorBrush current
                    ? current.Color
                    : Microsoft.UI.Colors.Transparent;

                BrushColourTransition.SnapTo(_adaptiveTopBarBrush, start);
                WebPageTopAppBarBackground.Background = _adaptiveTopBarBrush;

                // transitionMs 为 0（预热）时 AnimateTo 内部自行退化成立即赋值，
                // 首帧也就不会有「透明 → 目标色」那一段多余的淡入。
                BrushColourTransition.AnimateTo(
                    WebPageTopAppBarBackground, _adaptiveTopBarBrush, result.Frame, transitionMs);
            }

            // 顶栏文字/图标：按取到的亮暗切顶栏局部主题，前景色由主题资源自动跟上。
            // 沉浸式下顶栏没有自己的底衬（SetChromeVisible(false)），背景就是上面那个色块，
            // 所以色块变了而前景不跟着切，就会出现"白底白字 / 黑底黑字"。
            if (TopAppBarService.IsWritingTarget(this))
            {
                TopAppBarService.SetTheme(theme);
            }

            if (IsBottomBarHostOwner())
            {
                // 底栏两件事都按【自己那一发】来：背景取页面底端那一带的色，
                // 前景（文字/图标）按那块背景的亮度独立选方案 —— 不能用 theme（顶栏那个），
                // 否则「顶栏白 / 底栏近黑」时底栏会顶着 Light 主题的深色图标，黑底黑字。
                ElementTheme bottomTheme = result.BottomScheme == AdaptiveScheme.Dark
                    ? ElementTheme.Dark
                    : ElementTheme.Light;

                Services.BottomBarThemeService.SetBottomBar(
                    BottomBarHost, bottomTheme, result.Bottom, transitionMs);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[WebBrowserPage] 自适应栏色: 来源={result.Reason}{(immediate ? "（记忆预热）" : string.Empty)}, " +
                $"顶栏={result.Frame}({result.Scheme}), 底栏={result.Bottom}({result.BottomScheme}), " +
                $"顶栏前景={result.Foreground}, 校正={result.Corrected}");
        }

        /// <summary>
        /// 当前应当参与取色 / 记忆的 URL。
        /// 内核还没就绪（刚开页、还没导航）时退回快捷方式地址 —— 那时
        /// <c>WebView.Source</c> 是 null，而预热恰恰要在内核就绪之前就把色顶上。
        /// </summary>
        private string? CurrentAdaptiveUrl()
            => WebView?.CoreWebView2?.Source ?? _currentShortcut?.Url;

        /// <summary>
        /// 切换 page 时先把这个站点上次的栏色顶上去（页面切换动画期间就会显示它）。
        ///
        /// <para>取色的真实节奏是：常驻脚本注入 → 首屏等 250ms 采样 → 再等 750ms 补一次迟到采样。
        /// 而页面切换动画只有几百毫秒 —— 动画播完了色还没算出来，顶栏/底栏就得露一段系统默认色，
        /// 等真值到了再淡入一次，观感是「先白一下再跳成网页色」。</para>
        ///
        /// <para>这里先用<see cref="AdaptiveBarColourMemory"/>里的上次的色把那一帧填上，
        /// 真值到了自然覆盖：色一样就被上面的去重吃掉（一次多余的写入都没有），
        /// 不一样就淡入过去。没访问过 / 非 http(s) 站点则直接返回，行为与改动前完全一致。</para>
        ///
        /// <para>调用点必须在 <c>TopAppBarService.EnterPage(this)</c> 之后 ——
        /// 顶栏是全局共享控件，没认领就写会被判成「写了别人的顶栏」而跳过。</para>
        /// </summary>
        private void PrefillAdaptiveBarColourFromMemory()
        {
            if (!AdaptiveColourSettings.Enabled)
            {
                return;
            }

            if (AdaptiveBarColourMemory.TryGet(CurrentAdaptiveUrl()) is not { } remembered)
            {
                return;
            }

            ApplyAdaptiveBarColour(remembered, immediate: true);
        }

        /// <summary>
        /// 恢复系统默认栏色（页面离开或开关关闭时调用）。
        /// </summary>
        /// <param name="detachMonitor">
        /// 是否拆掉内核级取色状态（常驻脚本订阅 + _adaptiveMonitorInstalled 标记）。
        ///
        /// <para><b>页面离开（切走 / Unloaded）时传 false</b>：脚本和 <see cref="OnAdaptiveColourMessageReceived"/>
        /// 订阅都挂在 CoreWebView2 内核上，而页面被 LRU 缓存、内核不重建 —— 拆掉的话切回时
        /// 就得重新 <c>AddScriptToExecuteOnDocumentCreatedAsync</c>，而那个 API 对【已经存在的文档】
        /// 不生效，补一次一次性探测又和 <c>Loaded</c> 里的初始化竞态，结果栏色停在默认色，
        /// 要等下一次导航才恢复 —— 这正是「切换 page 后颜色丢失」的根因。</para>
        ///
        /// <para>保留订阅是安全的：切走时 WebView 已不在前台，常驻脚本因
        /// <c>document.visibilityState !== 'visible'</c> 停止回传；即便少数路径回了，
        /// 写的也是本页自己的 <c>WebPageTopAppBarBackground</c>，用户看不到、无害。
        /// 切回后由常驻脚本的 visibilitychange / 事件监听自动恢复颜色。</para>
        ///
        /// <para><b>关闭总开关时传 true</b>：此时要真正停止取色，退订
        /// <see cref="OnAdaptiveColourMessageReceived"/> 让回传无人处理，脚本侧则靠
        /// <see cref="PushRuntimeOptionsAsync"/> 推的 runtime 变量停发。</para>
        /// </param>
        private void ResetAdaptiveBarColour(bool detachMonitor = false)
        {
            _adaptiveBarColourCts?.Cancel();
            _adaptiveBarColourCts?.Dispose();
            _adaptiveBarColourCts = null;

            CurrentAdaptiveBarColour = null;
            _lastAdaptiveData = null;
            _appliedAdaptiveBarColour = null;
            _adaptiveRule = null;
            _adaptiveSuspended = false;
            _adaptiveLastUrl = null;

            // 文档代次闸门的状态也要复位：切走时若恰好「正在等新文档」
            // （_adaptiveAwaitingNewDocument=true，比如导航刚发起就切走了），切回后补探测
            // 取的是同一份文档的 timeOrigin，会被 IsStaleDocument 误判成「旧文档迟到回传」丢弃，
            // 颜色要等 1500ms 自愈才恢复。这里一并清掉，切回后的第一发回传直接放行。
            _adaptiveDocOrigin = 0;
            _adaptiveStaleDocOrigin = 0;
            _adaptiveStaleMarkedTicks = 0;
            _adaptiveAwaitingNewDocument = false;

            // 页面离开（切走/Unloaded）时保留内核级取色状态，切回后自动恢复；只有真正关开关才拆。
            if (detachMonitor)
            {
                _adaptiveMonitorCore = null;
                _adaptiveInjectedQuery = null;

                if (WebView?.CoreWebView2 is { } core)
                {
                    core.WebMessageReceived -= OnAdaptiveColourMessageReceived;
                }

                // 常驻脚本挂在 CoreWebView2 上，随内核一起走；这里只清标记，
                // 之后再导航会重新注入（脚本内部有 __dockedToolsColourMonitor 去重）。
                _adaptiveMonitorInstalled = false;
            }

            // 开关可能是页面开着的时候被关掉的，画刷上还留着网页色，动画也可能还在滚 ——
            // 先停掉动画，否则正在跑的 Storyboard 会在赋值之后继续插值，把复位色又拽回网页色。
            BrushColourTransition.Stop(_adaptiveTopBarBrush);

            // 所以不按当前开关状态提前返回，一律走完整复位（幂等，重复调用无副作用）。
            //
            // ⭐ 复位时【保持 Background 指向 _adaptiveTopBarBrush】，只把画刷颜色设成主题默认色。
            // 不换成 ThemeResource 画刷的原因：ApplyAdaptiveBarColour 靠
            // ReferenceEquals(Background, _adaptiveTopBarBrush) 判断「该淡入还是硬跳」——
            // 一旦换成别的画刷，切回后的首次上色就落进 SnapTo 分支，颜色直接硬跳、没有淡入动画
            // （这正是「切换 page 后颜色没淡入」的根因）。保持常驻画刷，切回时走 AnimateTo，
            // 由 firstPaint 特判保证从默认色淡入到网页色时不会先闪一下黑。
            //
            // 失去 ThemeResource 跟随的代价可以接受：切走时页面不在前台，色块颜色用户看不到；
            // 系统主题切换（页面在前台时）走 OnSystemThemeChanged → ReapplyAdaptiveBarColourForThemeChange，
            // 不经这里，不受影响。
            if (Application.Current.Resources.TryGetValue(TopBarBackgroundResourceKey, out object? resource)
                && resource is SolidColorBrush defaultBrush)
            {
                _adaptiveTopBarBrush.Color = defaultBrush.Color;
            }
            else
            {
                _adaptiveTopBarBrush.Color = Microsoft.UI.Colors.Transparent;
            }

            WebPageTopAppBarBackground.Background = _adaptiveTopBarBrush;

            // 顶栏主题复位：不复位的话退出网页页后顶栏会一直顶着上一个网页的亮/暗。
            // 走 ScopeFor(this) 而不是 SetTheme —— 后者写的是【当前写入目标】，
            // 页面切走之后那已经是新前台页，照写会把人家的顶栏主题改掉。
            TopAppBarService.ScopeFor(this).ThemeMode = TopBarThemeMode.System;

            if (IsBottomBarHostOwner())
            {
                // 复位是粗粒度状态变更，带 300ms 尾巴会迟到地盖住新页面的第一帧 —— 过渡时长传 0
                Services.BottomBarThemeService.SetBottomBar(BottomBarHost, ElementTheme.Default, null, 0);
            }

            System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 自适应栏色已复位");
        }

        private static bool SameColor(Windows.UI.Color a, Windows.UI.Color b)
            => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

        /// <summary>
        /// BottomBarThemeService 现在按宿主实例分账（见该服务的类注释），
        /// 这里就退化成一句「我自己注册过没有」：注册过就只写自己那份，天然碰不到别人。
        /// 以前这里是 <c>ReferenceEquals(RegisteredHost, BottomBarHost)</c> ——
        /// 多页并存时注册权可能被后构造的页面抢走，本页的自适应色反而被自己挡在外面。
        /// </summary>
        private bool IsBottomBarHostOwner()
            => Services.BottomBarThemeService.IsHostRegistered(BottomBarHost);

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
                // 关掉总开关：立刻复位栏色（含顶栏局部主题、底栏），不留上一个网页的亮/暗。
                // detachMonitor: true —— 真正停止取色：退订回传订阅、清注入标记，
                // 脚本侧由 EnsureAdaptiveColourSourceAsync 推的 runtime 变量停发。
                if (!AdaptiveColourSettings.Enabled)
                {
                    ResetAdaptiveBarColour(detachMonitor: true);
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
