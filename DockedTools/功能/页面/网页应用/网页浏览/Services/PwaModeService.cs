using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Foundation;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// PWA 模式：让网页认为自己是装在一台手机上的 PWA，而不是跑在桌面浏览器标签页里。
///
/// <para>网页判断「我是不是 PWA」主要看三样东西，这里全部伪造：</para>
/// <list type="number">
///   <item>User-Agent —— 移动端 UA（含 Client Hints / <c>navigator.userAgentData</c>）</item>
///   <item><c>navigator.standalone</c> —— iOS Safari 从 iOS 11.3 起就靠它判断是否已添加到主屏</item>
///   <item><c>matchMedia('(display-mode: standalone)')</c> —— 标准写法，Android 侧基本都走这个</item>
/// </list>
///
/// <para><b>三条通道，各管一段，缺一不可</b>：
/// <list type="bullet">
///   <item><b>请求头</b>（服务端看到的 UA）—— <c>WebResourceRequested</c> 里改 Document /
///     XHR / Fetch 三类请求的 header。
///     这是唯一能保证<b>首次导航</b>就带移动 UA 的通道：请求对象是 Chromium 已经构造好的，
///     改完立即发出，不存在「等下一次导航」的滞后。</item>
///   <item><b>文档（渲染器）</b> —— <c>AddScriptToExecuteOnDocumentCreated</c>。
///     页面 JS 读到的 <c>navigator.userAgent</c> / <c>userAgentData</c>（Client Hints）
///     就是这里改的，另加 <c>navigator.standalone</c> 和 <c>display-mode</c> 媒体查询。</item>
///   <item><b>浏览器进程</b> —— CDP <c>Emulation.setUserAgentOverride</c>。
///     尽力而为的一层：它管的是浏览器进程侧会用到的那份 UA（未经 <c>WebResourceRequested</c>
///     的子资源请求、并非每次都会经过宿主的鉴权重定向等）。成功与否都不影响
///     「网页读到的 UA」，所以它的失败只写日志，不参与「是否生效」的判定。</item>
/// </list>
/// 三条通道互不覆盖，各自的时序要求也不同，所以这里把它们收敛成
/// <see cref="ApplyToCoreAsync"/> 这一个入口，避免调用方漏掉或排错顺序。</para>
///
/// <para><b>为什么 <c>navigator.userAgent</c> 交给注入脚本，而不是交给 CDP</b>：
/// <c>Emulation.setUserAgentOverride</c> 在 Chromium 里落在 <c>blink::InspectorEmulationAgent</c>
/// 上，是一份 <b>per-frame</b> 状态（<c>user_agent_override_</c>），要等到下一次文档创建时
/// 经 inspector instrumentation 才写进 <c>FrameLoader</c>（源码里：
/// <c>if (!user_agent_override_.Get().empty()) *user_agent = user_agent_override_.Get();</c>）。
/// 这条链要成立，前提是 CDP 会话始终挂在那个渲染器的那个 frame 上 —— WebView2 并不保证这一点，
/// 而且它一旦没落地，宿主侧什么都看不到（调用本身不报错）。
/// 注入脚本的时机和它完全相同（都是 document created），却是宿主直接下发的，
/// 中间没有「会话有没有挂在正确的 frame 上」这个变量，所以由它来接管最稳。</para>
///
/// <para><b>为什么 UA 走 CDP 而不是 <c>CoreWebView2Settings.UserAgent</c></b>：
/// 微软官方文档明确写了「Setting this property may clear User Agent Client Hints headers
/// <c>Sec-CH-UA-*</c> and script values from <c>navigator.userAgentData</c>」。
/// 也就是说光设 UA 字符串，Client Hints 反而会被清掉，
/// <c>navigator.userAgentData.mobile</c> 仍是 false —— 现代站点越来越多直接读它。
/// 更关键的是微软在 WebView2Feedback#2862 里确认了该属性<b>天生滞后一次顶层导航</b>：
/// 「the change ... will not be applied immediately, and it needs to wait for the next time
/// navigation」。这是设计行为，不是 bug。</para>
///
/// <para><b>关于纯 CSS 的 <c>@media (display-mode: standalone)</c></b>：
/// CDP 的 <c>Emulation.setEmulatedMedia</c> 确实盖不住它 —— DevTools 官方支持列表只有
/// prefers-* / forced-colors / color-gamut 那几个，<c>display-mode</c> 不在里面。
/// 但样式表里写死的 <c>@media</c> 块可以走 CSSOM：<c>MediaList.mediaText</c> 是可写的
/// （MDN：「updating the list via mediaText will immediately update the behavior of the
/// document」），把永远为假的 <c>display-mode</c> 表达式换成恒真/恒假的等价物即可，
/// 见 <see cref="ShimBody"/> 的第 ③ 段。
/// <b>唯一漏网的是跨域样式表</b> —— JS 读它的 <c>cssRules</c> 会抛 SecurityError，
/// 要补只能上 CDP 的 <c>CSS.setMediaText</c>（浏览器侧操作，不受同源限制，但要常开
/// <c>CSS.enable</c>，有样式表索引开销）。当前没上。</para>
///
/// <para><b>为什么还要伪造 <c>hover</c> / <c>pointer</c></b>：
/// 这两条才是站点判断「你有没有鼠标」的正规途径，而且 CDP 拿它们没办法 ——
/// Chromium 把它们当设备能力（<c>--blink-settings=primaryPointerType=...</c>）而不是媒体覆盖，
/// Playwright 为了模拟它们走的也是 blink 启动参数，协议里没有对应的 Emulation 命令。
/// 好在它们和 <c>display-mode</c> 一样，是写在样式表 / <c>matchMedia</c> 里的媒体查询，
/// 同一套 CSSOM 替换就能顺手带走：<c>hover:none</c> / <c>pointer:coarse</c> 恒真，反过来恒假。
/// 开 PWA 模式就一起开 —— 用户选的就是「我要被当成一台手机」，
/// 只改 UA 却留着 <c>hover:hover</c>，等于一边喊手机一边举着鼠标，站点照样认出来。</para>
///
/// <para><b>跨界提醒</b>：<c>hover</c> / <c>pointer</c> 的替换会命中大量桌面优化样式块
/// （几乎每个按钮的 hover 分支都写在 <c>@media (hover: hover)</c> 里），
/// 所以 ③ 那段扫描的命中率远高于只找 <c>display-mode</c> 的时候。
/// 开销仍然按每条 <c>@media</c> 一次 <c>mediaText</c> 读写算，配额沿用原来的 6 万条。</para>
///
/// <para><b>刻意不碰视口</b>：曾经这里发过 <c>Emulation.setDeviceMetricsOverride</c>，
/// 把视口压成 412×915 / 393×852。实测下来它会把宿主控件里的页面内容裁掉 ——
/// 宿主是停靠栏，尺寸由用户自己拖，硬塞一个手机比例进去，多出来的部分就没了。
/// <b>视口大小是宿主说了算的事，伪装不该替用户决定窗口多大。</b>
/// 想看手机版布局，把窗口拖窄即可；本服务只负责让网页「认为」它是手机。</para>
/// </summary>
public static class PwaModeService
{
    /// <summary>
    /// 日志里的模块名。
    ///
    /// <para>刻意用 <see cref="LogService"/> 而不是 <c>Debug.WriteLine</c>：后者在 Release 下
    /// 被 <c>[Conditional("DEBUG")]</c> 掉，部署出去的产物出了岔子是一个字都查不到的 ——
    /// 上一版「为什么 UA 没生效」查不动，根子就在这。</para>
    /// </summary>
    private const string LogModule = "PWA模式";

    /// <summary>
    /// PWA 模式开关或设备档位发生变化。
    /// 设置是全局的，其它已经打开的网页浏览页靠这个事件同步自己的内核。
    /// 页面必须在 <c>DisposeWebView</c> 里退订，否则静态事件会把页面一直挂在进程里。
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// 通知所有页面 PWA 模式设置变了（由改动设置的那一侧调用）。
    /// </summary>
    public static void NotifyChanged() => Changed?.Invoke();

    // ==================================================================
    // 常驻伪装脚本
    // ==================================================================

    /// <summary>
    /// 脚本本体 —— 不含 IIFE 包装，平台标志由 <see cref="BuildShimScript"/> 在头部塞进来。
    ///
    /// <para><b>为什么要按档位拼出来</b>：早先脚本自己读 <c>navigator.userAgent</c> 判断
    /// 要不要伪装，那是宿主与脚本之间唯一的同步通道。任何一环没同步（CDP 滞后、
    /// 首次导航竞态、内核重建），脚本就<b>静默 return</b> —— 不掉错误、不打日志，
    /// 外面只看到「网页还是桌面版」。现在平台标志由宿主直接下发，
    /// 伪装与否完全由<script 是否被注册>决定，那一整类故障消失了。</para>
    ///
    /// <para><c>display-mode</c> 的替换规则 ③ 和 ④ 共用一份：
    /// standalone / minimal-ui / fullscreen → 恒真，browser → 恒假。
    /// 伪装成 standalone 时，「在浏览器里打开」那一支本来就应当是关的，
    /// 所以 browser 不能保持原样 —— 那会让 JS 说「是 browser」、CSS 说「不是」，自相矛盾。</para>
    /// </summary>
    private const string ShimBody = """
    // ⭐ 探针标记：外部（测试页 / 控制台）靠它确认脚本是否真的进了这个文档。
    //    以前只能靠「网页有没有变手机版」反推，分不清是 UA 没生效还是脚本没进来。
    window.__dockedToolsPwaShim = true;

    // ⑤ 渲染器侧身份：navigator.userAgent / appVersion / userAgentData
    //    这一段必须由注入脚本接管，原因见类注释 —— CDP 那条 per-frame 状态链在 WebView2 里
    //    是否落地没有任何保证，而且不落地时宿主侧一个字都看不到。
    //    ⭐ __pwaRealUserAgent 刻意保留【浏览器层面】的真实值：
    //      它是「CDP 那条通道到底有没有生效」的唯一探针，测试页会把它读出来。
    //      注意它的语义 —— 它记录的是【文档创建那一刻】浏览器给出的 UA，
    //      所以代表的是「CDP 覆盖是否被应用到了新文档」，而不是宿主要伪装的那个值。
    window.__pwaRealUserAgent = navigator.userAgent;

    try {
      Object.defineProperty(navigator, 'userAgent', {
        get: function () { return SPOOF_UA; },
        configurable: true
      });
    } catch (e) {}

    // appVersion 在真实浏览器里就是去掉 "Mozilla/" 前缀的 UA
    try {
      Object.defineProperty(navigator, 'appVersion', {
        get: function () { return SPOOF_UA.replace(/^Mozilla\//, ''); },
        configurable: true
      });
    } catch (e) {}

    // 结构化 UA（Client Hints 的 JS 侧接口）整体换掉：它是只读对象，改不动，只能替换。
    // iOS 档位下 UA_DATA 是 undefined —— 真机 Safari 压根不暴露 userAgentData，
    // 补一个出来反而会露馅。
    try {
      Object.defineProperty(navigator, 'userAgentData', {
        get: function () { return UA_DATA; },
        configurable: true
      });
    } catch (e) {}

    // ① iOS Safari 的 PWA 标志。真实环境里只有 iOS 有这个属性，
    //    但站点经常不区分平台就直接读，统一补上最省事。
    try {
      Object.defineProperty(navigator, 'standalone', {
        get: function () { return true; },
        configurable: true
      });
    } catch (e) {}

    // ② 平台串与触摸能力：站点的移动端分支常用这两个做二次确认
    try {
      Object.defineProperty(navigator, 'platform', {
        get: function () { return isIOS ? 'iPhone' : 'Linux armv8l'; },
        configurable: true
      });
    } catch (e) {}
    try {
      if (!navigator.maxTouchPoints) {
        Object.defineProperty(navigator, 'maxTouchPoints', {
          get: function () { return 5; },
          configurable: true
        });
      }
    } catch (e) {}

    // ⑥ 桌面痕迹清理：这几样既不在 UA 里、也不在媒体查询里，CDP 更没有对应的覆盖命令，
    //    只能靠 defineProperty 一个个改掉。做移动端分支的站点主动读它们的不多，
    //    但指纹类脚本基本必读。
    //    脚本被注入本身就意味着开关是开的，所以这里不再有「要不要改」的分支。

    // plugins：桌面 Chrome 有 PDF viewer 等插件，手机 Chrome 的 plugins.length 是 0。
    // item / namedItem / refresh 要一起补齐，否则按索引或按名字取值的老代码会抛异常。
    try {
      Object.defineProperty(navigator, 'plugins', {
        get: function () {
          return { length: 0, item: function () { return null; },
                   namedItem: function () { return null; }, refresh: function () {} };
        },
        configurable: true
      });
    } catch (e) {}
    try {
      Object.defineProperty(navigator, 'hardwareConcurrency',
        { get: function () { return 8; }, configurable: true });
    } catch (e) {}
    try {
      Object.defineProperty(navigator, 'deviceMemory',
        { get: function () { return 8; }, configurable: true });
    } catch (e) {}

    // ⚠️ 刻意【不】伪造 window.screen / window.devicePixelRatio / innerWidth：
    //    本服务不模拟视口（理由见类注释），窗口该多大是宿主和用户的事。
    //    于是 screen / innerWidth 就是真实值，站点的响应式断点会照着真实宽度走 ——
    //    这是我们要的：布局跟着窗口变，身份跟着开关变，两者互不绑架。
    //    反过来，在 1200px 的窗口里把 screen.width 说成 412，只会得到「布局是宽的、
    //    却自称屏幕只有 412px」的荒谬组合，比什么都不做更糟。

    // ③ 样式表里的 @media 块
    //    下面 ④ 只钩住 JS 的 matchMedia，CSS 里写死的分支得另走一条路。
    //    MediaList.mediaText 是可写的（MDN 原文：「updating the list via mediaText
    //    will immediately update the behavior of the document」），把「答案不对」的那些
    //    feature 换成恒真/恒假的等价物，样式块就跟着翻转。
    //    挑长度型 feature 是因为它在任何视口下都合法；(all) 是 media type 语法，塞不进括号。
    var TRUE_Q = 'min-width: 0px';
    var FALSE_Q = 'min-width: 999999px';

    // 每条规则给出一个 feature「取哪些值时、在伪装目标下应当为真」。
    //    目标身份 = 一台触屏手机上的 standalone PWA，于是：
    //      display-mode  standalone / minimal-ui / fullscreen 真，browser 假
    //      hover         none 真（触屏没有悬停），hover 假
    //      pointer       coarse 真（手指），fine 假
    //      any-hover / any-pointer 同上：它们是「存在任何一种这样的输入设备」，
    //      在伪装成手机的语境里必须和主的那条口径一致，否则会有自相矛盾的样式。
    //
    // ⭐ 正则里的 (?<![\w-]) 不能省：any-hover / any-pointer 必须作为独立单词整条匹配，
    //    否则会被下面 hover / pointer 的正则从单词中间截走，
    //    把 "any-hover: none" 改写成 "any-min-width: 0px"，CSS 当场语法错。
    var FORGED = [
      { re: /(?<![\w-])display-mode\s*:\s*([a-zA-Z][\w-]*)/gi,
        truthy: /^(standalone|minimal-ui|fullscreen)$/i },
      { re: /(?<![\w-])any-hover\s*:\s*([a-zA-Z][\w-]*)/gi,    truthy: /^none$/i },
      { re: /(?<![\w-])hover\s*:\s*([a-zA-Z][\w-]*)/gi,        truthy: /^none$/i },
      { re: /(?<![\w-])any-pointer\s*:\s*([a-zA-Z][\w-]*)/gi,  truthy: /^coarse$/i },
      { re: /(?<![\w-])pointer\s*:\s*([a-zA-Z][\w-]*)/gi,      truthy: /^coarse$/i }
    ];

    // 快速判断：这个媒体查询里有没有我们要动的东西。绝大多数 @media 是宽度断点，
    // 先用一条正则筛掉它们再决定是否逐条替换，能省掉大站上绝大部分开销。
    var ANY_RE = /(?<![\w-])(display-mode|any-hover|hover|any-pointer|pointer)\s*:/i;

    // ③④ 共用同一份替换逻辑。两边必须给出一致的答案 ——
    //    否则会变成「JS 说我是触屏、CSS 说我是鼠标」的自相矛盾。
    //    只替换 feature 名与值，不动括号和 and/or/not 结构，所以
    //    not (pointer: fine) 会变成 not (min-width: 999999px) = 恒真，也正好是对的。
    //    改写本身是幂等的：改完就不再含这些 feature，正则直接不匹配，重复跑无副作用。
    function patchQuery(q) {
      if (typeof q !== 'string' || q.length === 0 || !ANY_RE.test(q)) { return q; }
      var patched = q;
      for (var i = 0; i < FORGED.length; i++) {
        patched = patched.replace(FORGED[i].re, function (m, value) {
          return FORGED[i].truthy.test(value) ? TRUE_Q : FALSE_Q;
        });
      }
      return patched;
    }

    function patchMediaList(media) {
      if (!media) { return; }
      var text;
      try { text = media.mediaText; } catch (e) { return; }
      if (!text) { return; }
      var patched = patchQuery(text);
      if (patched !== text) {
        try { media.mediaText = patched; } catch (e) {}
      }
    }

    function walkRules(rules, depth, budget) {
      if (!rules || depth > 4 || budget.n <= 0) { return; }
      for (var i = 0; i < rules.length && budget.n > 0; i++) {
        budget.n--;
        var rule;
        try { rule = rules[i]; } catch (e) { continue; }
        if (!rule) { continue; }
        if (rule.type === 4) { patchMediaList(rule.media); }  // CSSRule.MEDIA_RULE
        var inner = null;
        try { inner = rule.cssRules; } catch (e) {}
        if (inner) { walkRules(inner, depth + 1, budget); }
      }
    }

    function patchStyleSheets() {
      var sheets;
      try { sheets = document.styleSheets; } catch (e) { return; }
      if (!sheets) { return; }
      // 整次扫描共用一个配额，别按样式表各给一份 —— 页面挂几十张表时总量会失控。
      // 正常站点几千条规则，配额管够；真撞上几万条的病态 CSSOM 就及时收手。
      var budget = { n: 60000 };
      for (var i = 0; i < sheets.length && budget.n > 0; i++) {
        var rules;
        try { rules = sheets[i].cssRules; } catch (e) { continue; }  // 跨域表读不了，跳过
        walkRules(rules, 0, budget);
      }
    }

    // 注入点在 document created，此刻样式表基本还没到位，所以后面几个时机各补一次。
    // 改写是幂等的：改完就不再含 display-mode，正则直接不匹配，重复跑无副作用。
    patchStyleSheets();
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', patchStyleSheets, { once: true });
    }
    window.addEventListener('load', patchStyleSheets, { once: true });

    // SPA 后来插入的 <style> / <link>：只盯 head 的直插子节点，不做全树观察，省开销。
    // 外链 CSS 是异步的，所以 debounce 之后再补一枪，兜住「插进来了但还没 load 完」。
    try {
      var busy = 0;
      new MutationObserver(function () {
        if (busy) { return; }
        busy = 1;
        setTimeout(patchStyleSheets, 100);
        setTimeout(function () { patchStyleSheets(); busy = 0; }, 700);
      }).observe(document.head || document.documentElement, { childList: true });
    } catch (e) {}

    // ④ 媒体查询（JS 侧）
    //    和 ③ 共用同一套替换规则（patchQuery），把查询里的目标 feature 换成
    //    恒真/恒假的等价物，再交给浏览器自己评估。这样 and / or / not 的组合语义天然正确 ——
    //    早先按关键字硬塞 matches 的写法会把 not (display-mode: browser) 判反
    //    （它在伪装成 standalone 时本该为真），而且和 ③ 改出来的 CSS 结果对不上。
    //    同上：这是在伪装 query 的答案，不是在替换浏览器的能力；
    //    真正的悬停行为（鼠标划过时的 :hover 伪类）在所有这层之外，改不掉。
    if (typeof window.matchMedia !== 'function') { return; }
    var realBound = window.matchMedia.bind(window);

    function wrapMedia(q, mql) {
      // 只把 media 还原成调用方传进来的原始查询串；matches 保持原型上那个活的 getter。
      // 方法是 bind 回去的：addEventListener 等会因 this 不是真 MediaQueryList 而抛
      // Illegal invocation。
      if (mql.media === q) { return mql; }
      var proxy = Object.create(mql);
      Object.defineProperty(proxy, 'media', { get: function () { return q; }, configurable: true });
      if (mql.addEventListener) { proxy.addEventListener = mql.addEventListener.bind(mql); }
      if (mql.removeEventListener) { proxy.removeEventListener = mql.removeEventListener.bind(mql); }
      if (mql.addListener) { proxy.addListener = mql.addListener.bind(mql); }
      if (mql.removeListener) { proxy.removeListener = mql.removeListener.bind(mql); }
      return proxy;
    }

    window.matchMedia = function (q) {
      if (typeof q !== 'string') { return realBound(q); }
      var patched = patchQuery(q);
      // 没命中任何一条伪造规则 → 原样返回真的 MediaQueryList，连代理对象都不用建
      if (patched === q) { return realBound(q); }
      return wrapMedia(q, realBound(patched));
    };
""";

    /// <summary>
    /// 按设备档位拼出完整的常驻脚本。
    ///
    /// <para>平台标志由宿主直接下发而不是让脚本运行时去读 UA /
    /// <c>matchMedia</c>，原因见 <see cref="ShimBody"/> 的说明：让脚本自己判断，
    /// 就等于把成败挂在一条宿主观测不到的链路上。</para>
    ///
    /// <para><b>为什么没有「要不要伪装输入能力」这个参数</b>：它曾经跟着手机视口开关走，
    /// 视口模拟删掉后，能注入脚本本身就只意味着一件事 —— 开关是开的，
    /// 用户要的就是「被当成一台手机」，输入能力没有理由单独留一档。</para>
    /// </summary>
    public static string BuildShimScript(PwaDeviceProfile profile, string mobileUserAgent)
    {
        string iosFlag = profile == PwaDeviceProfile.IOS ? "true" : "false";
        return "(function () {\n  \"use strict\";\n" +
               "  var isIOS = " + iosFlag + ";\n" +
               "  var SPOOF_UA = " + JsonString(mobileUserAgent) + ";\n" +
               "  var UA_DATA = " + BuildUserAgentDataScript(profile, mobileUserAgent) + ";\n" +
               "  try {\n" + ShimBody + "\n  } catch (e) {}\n})();";
    }

    /// <summary>
    /// 拼出 <c>navigator.userAgentData</c> 的替身，用 JS 对象字面量表示。
    ///
    /// <para>它不是一个纯值：<c>NavigatorUAData</c> 除了 <c>brands</c> / <c>mobile</c> /
    /// <c>platform</c> 三个属性，还得有 <c>getHighEntropyValues()</c>（返回 Promise）和
    /// <c>toJSON()</c>。站点读 Client Hints 通常走的就是这两个方法，缺一个就露馅。</para>
    ///
    /// <para>iOS 档位返回 <c>undefined</c> —— Safari 至今不实现 <c>NavigatorUAData</c>，
    /// 装一个出来反而是最扎眼的破绽。</para>
    /// </summary>
    private static string BuildUserAgentDataScript(PwaDeviceProfile profile, string mobileUserAgent)
    {
        if (profile == PwaDeviceProfile.IOS)
        {
            return "undefined";
        }

        // Chrome 大版本 / 完整版本跟着 Runtime 走：它们是从默认 UA 里抠出来的，不硬编码。
        string chromeVersion = ExtractChromeVersion(mobileUserAgent, fallback: "140.0.0.0");
        string major = chromeVersion.IndexOf('.') > 0 ? chromeVersion[..chromeVersion.IndexOf('.')] : chromeVersion;

        string fullVersionList =
            "[{\"brand\":\"Chromium\",\"version\":" + JsonString(chromeVersion) + "}," +
            "{\"brand\":\"Google Chrome\",\"version\":" + JsonString(chromeVersion) + "}]";

        string brands =
            "[{\"brand\":\"Chromium\",\"version\":" + JsonString(major) + "}," +
            "{\"brand\":\"Google Chrome\",\"version\":" + JsonString(major) + "}," +
            "{\"brand\":\"Not?A_Brand\",\"version\":\"99\"}]";

        // ⚠️ 这不是 JSON —— 里面带函数，只能当 JS 对象字面量用。
        return "{\"brands\":" + brands +
               ",\"mobile\":true" +
               ",\"platform\":\"Android\"" +
               ",\"platformVersion\":\"14.0.0\"" +
               ",\"architecture\":\"arm64\"" +
               ",\"model\":\"Pixel 8\"" +
               ",\"bitness\":\"64\"" +
               ",\"wow64\":false" +
               ",\"fullVersion\":" + JsonString(chromeVersion) +
               ",\"fullVersionList\":" + fullVersionList +
               ",\"getHighEntropyValues\":function () {" +
               "  return Promise.resolve({brands:this.brands,mobile:true,platform:this.platform," +
               "platformVersion:this.platformVersion,architecture:this.architecture,model:this.model," +
               "uaFullVersion:this.fullVersion,bitness:this.bitness,wow64:false," +
               "fullVersionList:this.fullVersionList});" +
               "}" +
               ",\"toJSON\":function () { return {brands:this.brands,mobile:true,platform:this.platform}; }}";
    }

    // ==================================================================
    // per-core 状态
    // ==================================================================

    /// <summary>
    /// 一个内核上的全部 PWA 状态。
    /// 用弱键表而不是直持引用：内核被回收时条目自动消失，
    /// 不会把废弃的 CoreWebView2 钉死在内存里。这是 AOT 安全的，不走反射。
    /// </summary>
    private sealed class PwaCoreState
    {
        /// <summary>还没被本服务改过的原始 UA（关模式时写回去用）。</summary>
        public string DefaultUserAgent = string.Empty;

        /// <summary>当前注入的脚本 id。null 表示这个内核上没挂脚本。</summary>
        public string? ShimId;

        /// <summary>已注入脚本对应的设备档位。档位变了要换脚本。</summary>
        public PwaDeviceProfile ShimProfile;

        /// <summary>Document 请求头钩子是否已注册。</summary>
        public bool HeaderHookInstalled;
    }

    private static readonly ConditionalWeakTable<CoreWebView2, PwaCoreState> CoreStates = new();

    private static PwaCoreState StateOf(CoreWebView2 core)
    {
        if (CoreStates.TryGetValue(core, out PwaCoreState? existing) && existing is not null)
        {
            return existing;
        }

        PwaCoreState state = new();
        CoreStates.Add(core, state);
        return state;
    }

    /// <summary>
    /// 把 PWA 模式的全部设置下发到一个内核。<b>调用方必须 <c>await</c> 它</b>，
    /// 而且必须在首次导航之前 —— 这三条通道里没有一条能追上「已经发出去的请求」。
    /// </summary>
    /// <returns>本内核上的 PWA 伪装是否<b>真正落在了位</b>。任一步失败都是 false。</returns>
    public static async Task<bool> ApplyToCoreAsync(CoreWebView2 core)
    {
        bool enabled = ExperimentalSettings.EnablePwaMode;
        PwaDeviceProfile profile = ExperimentalSettings.PwaProfile;
        PwaCoreState state = StateOf(core);

        // ⚠️ 必须在任何覆盖之前读。本服务全程不写 Settings.UserAgent，所以这里拿到的
        //    永远是 Edge 默认 UA —— 关模式时靠它显式写回。官方明写了传空字符串
        //    不会恢复默认（"the User Agent will not be updated"）。
        if (string.IsNullOrEmpty(state.DefaultUserAgent))
        {
            state.DefaultUserAgent = core.Settings.UserAgent ?? string.Empty;
        }

        // ⭐⭐ 下面这一段的形状是被一个真实 bug 逼出来的，**不要**顺手改回「逐个 await」。
        //
        //    CoreWebView2 是单线程单元（STA）的 COM 对象：从创建它的线程之外去调它，
        //    抛的是 RPC_E_WRONG_THREAD（中文错误串：「只能从创建对象的线程调用该方法」），
        //    而且是【调用的那一刻】就抛 —— 宿主拿到的只是个普通 .NET 异常，日志里不留痕迹。
        //    2026-10-04 在 app.log 里抓到连着 26 条 Warning，本服务每一次 CDP 调用
        //    （setUserAgentOverride / setTouchEmulationEnabled）
        //    都是这么静默死掉的 —— navigator.userAgent 自然一直停在桌面 UA。
        //
        //    为什么会跑到别的线程上：await 之后续跑在哪，取决于有没有 SynchronizationContext、
        //    以及有没有 ConfigureAwait(false)。本服务此前照搬「类库最佳实践」，
        //    给内部异步方法统统加了 ConfigureAwait(false)，第一次 await 之后整条链就落进线程池了；
        //    而且就算全去掉，没有 SynchronizationContext 时 await 照样会跳过去。
        //
        //    所以这里的规矩只有一条：**core 上的方法必须在同一个线程上发起，中间不许出现 await**。
        //    把所有 IAsyncOperation 一次性发出去，再统一 await —— 有没有 SynchronizationContext
        //    都不会再跨线程。
        //    代价：这几条 CDP 之间不保证顺序（官方原话就提醒过 CDP 可能被乱序处理）。
        //    UA / 触摸两件事互不相干，认了。真有一天需要严格保序，
        //    正解是把每个调用 marshal 到 core.DispatcherQueue 上，而不是靠 await。
        EnsureRequestHeaderHook(core, state, enabled);

        // ⭐ 三处要用同一条移动 UA（脚本里的 navigator.userAgent、Document 请求头、CDP override），
        //    这里算一次往下传。分开各拼一遍迟早会拼出不一致的字符串 ——
        //    那种「请求头说是 A、网页读到 B」的故障最难查。
        string mobileUserAgent = BuildMobileUserAgent(state.DefaultUserAgent, profile);

        IAsyncOperation<string>? shimOp =
            StartShim(core, state, enabled, profile, mobileUserAgent);

        IAsyncOperation<string>? uaOp = StartCdpCall(core, "Emulation.setUserAgentOverride",
            enabled ? BuildMobileOverrideJson(mobileUserAgent, profile)
                    : BuildClearOverrideJson(state.DefaultUserAgent));

        // 触摸能力仍然交给 CDP：它管的是渲染器侧的「这台设备有没有触摸屏」，
        // navigator.maxTouchPoints 会跟着变成 5。脚本里另有 defineProperty 兜底，
        // 所以这条失败也不影响成败判定。
        // ⚠️ 注意它【不是】 setEmitTouchEventsForMouse —— 后者会把鼠标事件改写成触摸事件，
        //    输入语义被改写，鼠标拖拽 / 长按菜单的行为都会变，这里刻意不用。
        IAsyncOperation<string>? touchOp = StartCdpCall(core, "Emulation.setTouchEmulationEnabled",
            enabled ? "{\"enabled\":true,\"maxTouchPoints\":5}" : "{\"enabled\":false}");

        // ---- 到这里才允许 await：上面每个 core 调用都已在同一个线程上发起完了 ----
        bool shimOk = await CompleteShimAsync(shimOp, state, profile);
        bool uaOk = await CompleteCdpCallAsync(uaOp, "Emulation.setUserAgentOverride");
        bool touchOk = await CompleteCdpCallAsync(touchOp, "Emulation.setTouchEmulationEnabled");

        // ⭐ CDP 那两层都不参与判定：UA 已由注入脚本接管，触摸有脚本兜底。
        //    把它们算进成败，会让一次无关痛痒的 CDP 失败把图标打成未生效 ——
        //    而此时网页其实是伪装好的。
        //    ⚠️ 这不等于它们可以坏着不管：失败了 Warning 会落进 app.log，照旧查得到。
        bool ok = shimOk;

        LogService.Info(LogModule,
            $"PWA 模式 = {enabled}（{profile}），" +
            $"请求头钩子 = {state.HeaderHookInstalled}，脚本 = {state.ShimId ?? "无"}，" +
            $"CDP UA 覆盖 = {uaOk}，CDP 触摸 = {touchOk}，生效 = {ok}");

        return ok;
    }

    // ==================================================================
    // 通道 ①：请求头（服务端看到的 UA）
    // ==================================================================

    /// <summary>
    /// 要改写 UA 的请求上下文。
    ///
    /// <para><b>以前只挂 <c>Document</c>，页面里所有 XHR / fetch 出去的请求仍是桌面 UA</b> ——
    /// 服务端按 UA 分支返回数据时就会拿到自相矛盾的信号：HTML 是移动版、
    /// 接口数据却是桌面版结构。前后端分离的站点基本都栽在这一条上。</para>
    ///
    /// <para>为什么不上 <c>All</c>：<c>WebResourceRequested</c> 会让每一个被过滤的请求
    /// 多一次「渲染器 → 宿主 → 渲染器」的往返，官方对这个 filter 是有性能提醒的。
    /// 一整页几十上百张图片全趟一遍，收益却是零 —— 几乎没有 CDN 按 UA 给图片分流。</para>
    ///
    /// <para>而这三个恰好就是服务端会看 UA 做分支的全部场合：
    /// Document（要返回哪个版本的 HTML）、XmlHttpRequest / Fetch（要返回哪种结构的数据）。
    /// 真碰上按 UA 分流 CSS / JS 的站点，往数组里加一项就行。</para>
    /// </summary>
    private static readonly CoreWebView2WebResourceContext[] HeaderContexts =
    {
        CoreWebView2WebResourceContext.Document,
        CoreWebView2WebResourceContext.XmlHttpRequest,
        CoreWebView2WebResourceContext.Fetch,
    };

    /// <summary>
    /// 注册/注销请求头钩子。
    ///
    /// <para>这条通道是「首次导航」唯一的保障：请求已经构造好、还没发出去，宿主直接改 header，
    /// 改完即走，不存在任何滞后。<c>Settings.UserAgent</c> 滞后一次导航、
    /// CDP override 对已加载文档无效 —— 这两个坑它都不占。</para>
    ///
    /// <para>官方 <see href="https://learn.microsoft.com/microsoft-edge/webview2/how-to/webresourcerequested">
    /// Custom management of network requests</see> 明确把「改 User-Agent」列为 WebResourceRequested
    /// 的用途之一：「The host app can modify headers at this point.」</para>
    ///
    /// <para>只在开启时挂。<c>WebResourceRequested</c> 会让被过滤的请求多一次跨进程往返，
    /// 所以关模式必须彻底摘掉 filter，而不是留着 handler 里空 return。</para>
    /// </summary>
    private static void EnsureRequestHeaderHook(CoreWebView2 core, PwaCoreState state, bool enabled)
    {
        if (state.HeaderHookInstalled == enabled)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                // 先挂 handler 再加 filter，顺序反了理论上会漏掉刚开始的那几个请求
                core.WebResourceRequested += OnPwaResourceRequest;
                foreach (CoreWebView2WebResourceContext context in HeaderContexts)
                {
                    core.AddWebResourceRequestedFilter("*", context);
                }
            }
            else
            {
                foreach (CoreWebView2WebResourceContext context in HeaderContexts)
                {
                    core.RemoveWebResourceRequestedFilter("*", context);
                }
                core.WebResourceRequested -= OnPwaResourceRequest;
            }

            state.HeaderHookInstalled = enabled;
        }
        catch (Exception ex)
        {
            LogService.Warning(LogModule,
                $"请求头钩子{(enabled ? "注册" : "注销")}失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 给经过的请求换上移动端 UA。
    /// 静态方法：-= 时按「目标实例 + 方法」比对，静态方法的 target 为 null、方法相同，能正确摘掉。
    /// </summary>
    private static void OnPwaResourceRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            // 本工程引用的是 WebView2 的 .NET projection，事件是 EventHandler<T>（sender 为 object），
            // 不是 WinRT 的 TypedEventHandler<CoreWebView2, ...>。CoreWebView2 才是这里的实际 sender。
            if (sender is not CoreWebView2 core)
            {
                return;
            }

            if (!ExperimentalSettings.EnablePwaMode)
            {
                return;
            }

            // filter 只放了这几个上下文，这里再判一次是双保险 —— 日后有人扩大了 filter 范围也不至于误伤。
            if (Array.IndexOf(HeaderContexts, args.ResourceContext) < 0)
            {
                return;
            }

            if (!CoreStates.TryGetValue(core, out PwaCoreState? state) || state is null)
            {
                return;
            }

            PwaDeviceProfile profile = ExperimentalSettings.PwaProfile;
            string mobileUa = BuildMobileUserAgent(state.DefaultUserAgent, profile);

            args.Request.Headers.SetHeader("User-Agent", mobileUa);

            // Client Hints 请求头：服务端侧的 mobile / platform 信号。
            // 浏览器只在站点先 Accept-CH opt-in 时才自动发，这里手动补上，让服务端侧的探测也能读到。
            args.Request.Headers.SetHeader("Sec-CH-UA-Mobile", "?1");
            args.Request.Headers.SetHeader("Sec-CH-UA-Platform",
                profile == PwaDeviceProfile.IOS ? "\"iOS\"" : "\"Android\"");
        }
        catch (Exception ex)
        {
            // 改 header 失败就让它按原样发出去，绝不能把请求卡死在宿主这里
            LogService.Warning(LogModule, $"改写请求头 UA 失败: {ex.Message}");
        }
    }

    // ==================================================================
    // 通道 ③：常驻脚本（PWA 身份 / 显示模式 / 输入能力）
    // ==================================================================

    /// <summary>
    /// 按开关状态同步常驻脚本的生命周期。
    ///
    /// <para><c>AddScriptToExecuteOnDocumentCreated</c> <b>是</b>有对应 Remove 的
    /// （<c>RemoveScriptToExecuteOnDocumentCreated(id)</c>，WebView2 .NET 1.0.664 起），
    /// 所以「挂一次就永久挂着」这个前提不成立 —— 脚本能不能生效，直接由开关决定，
    /// 不必再让脚本自己去读 UA 猜。</para>
    ///
    /// <para>设备档位变了要先摘旧的再装新的：脚本里的 <c>isIOS</c> 是注入时写死的。</para>
    /// </summary>
    /// <summary>
    /// 在【当前线程】上发起常驻脚本的注入 / 摘除，把未完成的 <c>IAsyncOperation</c> 交回调用方。
    ///
    /// <para>必须和其它 core 调用在同一个线程上发起 —— 原因见 <see cref="ApplyToCoreAsync"/>。</para>
    /// </summary>
    /// <returns>null 表示这次没有发起新的注入（关模式 / 已注入且参数都没变）。</returns>
    private static IAsyncOperation<string>? StartShim(
        CoreWebView2 core, PwaCoreState state, bool enabled, PwaDeviceProfile profile,
        string mobileUserAgent)
    {
        if (!enabled)
        {
            RemoveShim(core, state);
            // 关模式下「没脚本」才是正确结果
            return null;
        }

        if (state.ShimId is not null && state.ShimProfile == profile)
        {
            return null;
        }

        // 档位是注入时写死进脚本的，变了就得摘旧的装新的
        RemoveShim(core, state);

        try
        {
            return core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildShimScript(profile, mobileUserAgent));
        }
        catch (Exception ex)
        {
            LogService.Warning(LogModule, $"伪装脚本注入失败: {ex.Message}");
            state.ShimId = null;
            return null;
        }
    }

    /// <summary>
    /// 收一次注入的结果。
    /// 这之后续跑在哪个线程都无所谓了 —— 这里只 await 和改自己的状态，不再碰任何 core 对象。
    /// </summary>
    private static async Task<bool> CompleteShimAsync(
        IAsyncOperation<string>? op, PwaCoreState state, PwaDeviceProfile profile)
    {
        if (op is null)
        {
            return true;
        }

        try
        {
            // 官方文档：「you must wait for the returned IAsyncOperation to complete before you can
            // be sure that the script is ready to execute on future navigations」
            state.ShimId = await op;
            state.ShimProfile = profile;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warning(LogModule, $"伪装脚本注入失败: {ex.Message}");
            state.ShimId = null;
            return false;
        }
    }

    private static void RemoveShim(CoreWebView2 core, PwaCoreState state)
    {
        string? id = state.ShimId;
        state.ShimId = null;
        if (id is null)
        {
            return;
        }

        try
        {
            core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch (Exception ex)
        {
            // 摘不掉最多是伪装残留（后续导航仍会伪装），不至于崩；
            // 但这也是「关了却还在伪装」的来源，必须留痕。
            LogService.Warning(LogModule, $"伪装脚本摘除失败: {ex.Message}");
        }
    }

    // ==================================================================
    // UA
    // ==================================================================

    /// <summary>
    /// 构造 PWA 模式下的移动端 User-Agent。
    /// Chrome 版本号从默认 UA 里抠出来，跟着 Runtime 走，不用硬编码。
    /// </summary>
    public static string BuildMobileUserAgent(string defaultUserAgent, PwaDeviceProfile profile)
    {
        string chromeVersion = ExtractChromeVersion(defaultUserAgent, fallback: "140.0.0.0");

        return profile == PwaDeviceProfile.IOS
            // iOS 上所有浏览器都是 WebKit，UA 里不会有 Chrome 版本号
            ? "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 " +
              "(KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1"
            : $"Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 " +
              $"(KHTML, like Gecko) Chrome/{chromeVersion} Mobile Safari/537.36";
    }

    private static string ExtractChromeVersion(string userAgent, string fallback)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return fallback;
        }

        int index = userAgent.IndexOf("Chrome/", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return fallback;
        }

        int start = index + "Chrome/".Length;
        int end = start;
        while (end < userAgent.Length && (char.IsAsciiDigit(userAgent[end]) || userAgent[end] == '.'))
        {
            end++;
        }

        string version = userAgent[start..end].Trim('.');
        return string.IsNullOrEmpty(version) ? fallback : version;
    }

    /// <summary>
    /// 拼 Emulation.setUserAgentOverride 的入参（开启：移动 UA + Client Hints 元数据）。
    /// 纯字符串构造，不碰 core —— 这也是它能随处在任何线程上算的原因。
    /// </summary>
    private static string BuildMobileOverrideJson(string mobileUserAgent, PwaDeviceProfile profile)
    {
        bool ios = profile == PwaDeviceProfile.IOS;
        string chromeVersion = ExtractChromeVersion(mobileUserAgent, fallback: "140.0.0.0");
        string major = chromeVersion.IndexOf('.') > 0 ? chromeVersion[..chromeVersion.IndexOf('.')] : chromeVersion;

        // brands 是 Client Hints 的招牌字段，站点靠它认浏览器；
        // mobile=true 才是 navigator.userAgentData.mobile 的真正来源。
        string brands = ios
            ? "[{\"brand\":\"Safari\",\"version\":\"18.0\"}]"
            : "[{\"brand\":\"Chromium\",\"version\":" + JsonString(major) + "}," +
              "{\"brand\":\"Google Chrome\",\"version\":" + JsonString(major) + "}," +
              "{\"brand\":\"Not?A_Brand\",\"version\":\"99\"}]";

        string platform = ios ? "iOS" : "Android";
        string model = ios ? "iPhone" : "Pixel 8";
        string platformVersion = ios ? "18.0.0" : "14.0.0";
        string architecture = ios ? "arm" : "arm64";

        return "{\"userAgent\":" + JsonString(mobileUserAgent) +
               ",\"acceptLanguage\":\"zh-CN,zh;q=0.9\"" +
               ",\"platform\":" + JsonString(platform) +
               ",\"userAgentMetadata\":{\"brands\":" + brands +
               ",\"platform\":" + JsonString(platform) +
               ",\"platformVersion\":" + JsonString(platformVersion) +
               ",\"architecture\":" + JsonString(architecture) +
               ",\"model\":" + JsonString(model) +
               ",\"mobile\":true}}";
    }

    /// <summary>
    /// 拼 Emulation.setUserAgentOverride 的入参（关闭：恢复默认 UA）。
    /// 必须把原始 UA 显式写回去 —— WebView2 里传空字符串是「不更新」而不是「恢复默认」。
    /// </summary>
    private static string BuildClearOverrideJson(string defaultUserAgent)
    {
        // 拿不到原始 UA 就退回「清掉 override」：CDP 里空串代表取消覆盖，
        // 浏览器会用自己的默认 UA。这比留着移动 UA 强。
        return string.IsNullOrEmpty(defaultUserAgent)
            ? "{\"userAgent\":\"\"}"
            : "{\"userAgent\":" + JsonString(defaultUserAgent) + "}";
    }

    /// <summary>
    /// UA / 机型串里不会出现引号和反斜杠，但走一遍转义能防住将来手改常量时踩坑。
    /// </summary>
    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// 在【当前线程】上发起一次 CDP 调用，把未完成的 <c>IAsyncOperation</c> 交回调用方。
    ///
    /// <para>同步阶段抛的话（最常见的就是跨线程 RPC_E_WRONG_THREAD）当场记一笔并返回 null ——
    /// 这条日志是「CDP 到底走没走通」唯一的证据，所以必须是 Warning 而不是 Debug：
    /// Debug 级只进控制台，部署出去的产物里一个字都查不到。</para>
    /// </summary>
    private static IAsyncOperation<string>? StartCdpCall(
        CoreWebView2 core, string method, string parametersAsJson)
    {
        try
        {
            return core.CallDevToolsProtocolMethodAsync(method, parametersAsJson);
        }
        catch (Exception ex)
        {
            LogService.Warning(LogModule, $"{method} 调用失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 收一次 CDP 调用的成败。
    /// 这里不碰任何 core 对象，所以续跑在哪个线程都不影响线程亲和性了。
    /// </summary>
    private static async Task<bool> CompleteCdpCallAsync(IAsyncOperation<string>? op, string method)
    {
        if (op is null)
        {
            // 连发起都没成功（异常已经记过了）
            return false;
        }

        try
        {
            // ⚠️ 不能加 .ConfigureAwait(false)：返回的是 IAsyncOperation<string> 而不是 Task<string>，
            //    上面没有 ConfigureAwait 这个方法（硬写会报 CS1929）。
            await op;
            return true;
        }
        catch (Exception ex)
        {
            // CDP 失败不该把页面搞崩：UA / 触摸是增强项，不是生存项。
            // 但「崩不崩」和「算不算成功」是两件事 —— 这里必须如实返回 false。
            LogService.Warning(LogModule, $"{method} 调用失败: {ex.Message}");
            return false;
        }
    }
}
