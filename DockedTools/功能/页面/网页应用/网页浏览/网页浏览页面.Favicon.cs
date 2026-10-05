using DockedTools.Features.Pages.WebApp.EdgeSync;
using DockedTools.Features.Pages.WebApp.Shared;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 实时站点图标（Favicon）模块
    ///
    /// 图标从哪来：CoreWebView2 自带的三件套 ——
    ///   · FaviconUri        当前页面声明的图标地址（页面没声明时是空串）
    ///   · FaviconChanged    图标变化时触发，JS/DOM 动态改图标也会触发
    ///   · GetFaviconAsync   内核手里那份已下载的图，Png/Jpeg 两种格式
    ///
    /// 为什么不一上来就用 GetFaviconAsync：它实测恒返回 16x16（WebView2Feedback #4915），
    /// 放在顶栏/主页卡片那种尺寸上直接糊掉。所以策略是：
    ///   ① 优先按 FaviconUri 自己把原件下回来（可能是 192x192 甚至 SVG）；
    ///   ② 原件下不下来、或者是 SVG（BitmapDecoder 解不了）时，才退回内核那份 16x16。
    /// 微软官方在同 issue 里给的也是这个建议。
    ///
    /// 落盘策略（顶栏 / 侧边栏 / 持久化三件事）：
    ///   ① 顶栏：PublishShortcutIconAsync 落临时目录并下发，即时生效；
    ///   ② 持久化：合并 1.2s 内的连续变更（防抖）后写 shortcuts.json，
    ///      字节跟存量一致就不写 —— SPA 反复触发事件也不会反复重写几 MB 的 JSON；
    ///   ③ 侧边栏：写盘成功后广播 WebAppUpdateService.NotifyUpdate(Icon)，
    ///      导航栏 OnUpdateCompleted 重新 Load 并按内容哈希重建图标。
    /// 图标文件本身走 WebAppIconCache（web-icons 目录，文件名 = 内容哈希），
    /// 内容变了路径必变，天然避开 BitmapImage「同 Uri 不重新解码」的坑。
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>图标体积上限，跟详情页/新建页保持一致</summary>
        private const int MaxFaviconBytes = 4 * 1024 * 1024;

        /// <summary>进程级图标缓存上限，超了整包清空（图标体积都小，重建代价可接受）</summary>
        private const int FaviconCacheLimit = 128;

        /// <summary>高清图标的期望边长（px）。达到这个尺寸就直接用，不再试后面的候选</summary>
        private const int PreferredIconSize = 48;

        /// <summary>
        /// SVG 参与「尺寸比较」时用的虚拟边长。
        /// 矢量图没有内在像素尺寸，但渲染时能任意缩放、永远不糊 —— 给它一个比
        /// 常见位图（16/32/48/180/192）都大的值，排序和防退化比较就都自动偏袒它。
        /// </summary>
        private const int VectorIconSize = 256;

        /// <summary>桌面 favicon 最多试几个候选（按尺寸从大到小）</summary>
        private const int MaxIconCandidates = 4;

        /// <summary>手机 / 平台专用图标最多试几个。它们是备胎，不值得花太多请求</summary>
        private const int MaxPlatformIconCandidates = 2;

        /// <summary>
        /// 桌面那份小到这个尺寸（px）以下，才动用手机图标。
        /// 16x16 拉到侧边栏确实糊，但比起一张被 padding 挤成一点点的 180 色块，还是前者认得出。
        /// </summary>
        private const int FallbackToPlatformIconSize = 24;

        /// <summary>
        /// 识别「手机 / PWA 专用图标」的关键词（同时看 rel 和 href）。
        /// 这些图标是给 iOS 主屏 / Android 桌面 / Windows 磁贴用的：整块背景色 + 四周留白，
        /// 缩到侧边栏 20px 时主体只剩一点点，看着比原 favicon 还糊 —— 一律降为备胎。
        /// </summary>
        private static readonly string[] PlatformIconKeywords =
        {
            "apple-touch",      // apple-touch-icon / -precomposed / apple-touch-startup-image
            "android-chrome",   // android-chrome-192x192.png 这类
            "android-icon",
            "msapplication",    // <meta name="msapplication-TileImage">
            "mstile",           // Windows 磁贴
            "mask-icon",        // Safari 固定标签（纯黑 SVG）
            "fluid-icon",
            "touch-icon",
            "app-icon",
            "startup-image",
            "pwa"
        };

        /// <summary>整轮取图的超时（秒）</summary>
        private const int IconFetchTimeoutSeconds = 12;

        /// <summary>
        /// 扫当前文档里所有图标声明的脚本。
        /// 只取 link[rel*=icon]（含 apple-touch-icon / mask-icon）和 rel=manifest 的地址，
        /// href 用元素的绝对地址，省得回 C# 再拼相对路径。
        /// </summary>
        private const string IconProbeScript = """
            (() => {
              const icons = [];
              for (const el of document.querySelectorAll('link')) {
                const rel = (el.getAttribute('rel') || '').toLowerCase();
                if (!rel.includes('icon')) continue;
                icons.push({
                  href: el.href || '',
                  sizes: el.getAttribute('sizes') || '',
                  type: el.getAttribute('type') || '',
                  rel: rel
                });
              }
              return JSON.stringify(icons);
            })()
            """;

        /// <summary>
        /// 光栅化兜底出来的位图边长（px）。
        ///
        /// ⚠️ 必须跟 <see cref="VectorIconSize"/> 同一个量级，别照着「顶栏/侧边栏最大 96」来定。
        ///    它渲染的内容是矢量，但产物是 PNG —— <see cref="GetIconSizeAsync"/> 拿到位图只会解出
        ///    真实像素边长（原来是 96）。于是新救出来的图拿 96 去跟库里存着的那张矢量
        ///    （矢量按 256 参与比较）比大小，被防退化那条「新图比现有小就不覆盖」直接挡回去 ——
        ///    抢救成功也白成功，界面上看着就是「什么都没变」。
        ///    统一到 256，既不触发防退化，顺带高清屏也够清晰（一张 PNG 就几 KB）。
        /// </summary>
        private const int SvgRasterSize = VectorIconSize;

        /// <summary>光栅化结果轮询的次数上限。画一张 SVG 通常几十毫秒，给足余量</summary>
        private const int RasterPollCount = 40;

        /// <summary>光栅化结果轮询的间隔（毫秒）。<see cref="RasterPollCount"/> × 这个值就是最长等待</summary>
        private const int RasterPollIntervalMs = 100;

        /// <summary>
        /// SVG 抢救脚本 ①：让 Chromium 把算好的样式写成显式属性，输出 D2D 认的「普通 SVG」。
        ///
        /// 为什么要它：站点的 favicon.svg 很多把颜色写在 <c>&lt;style&gt;</c> 里
        /// （ChatGPT 就是 <c>:root{fill:#000}</c> + <c>@media(prefers-color-scheme:dark)</c>），
        /// 而 SvgImageSource 底下是 Direct2D，不执行 CSS —— 那部分颜色等于凭空消失。
        /// 但 Chromium 会算 —— 把 CSS 文本喂给一张「构造式样式表」，从 CSSOM 里读出规则，
        /// 自己按选择器写回属性，就能得到一张 D2D 认得的普通 SVG。
        ///
        /// ⭐ 为什么不走「挂 iframe + getComputedStyle」那条看起来更省事的路（实测踩过的坑）：
        ///   · 站点的 CSP 经常是 <c>style-src 'self'</c>，inline &lt;style&gt; 压根不会被应用 ——
        ///     那时 getComputedStyle 求出来的就是初始值，颜色照样丢（ChatGPT 正卡在这里）；
        ///   · <c>about:blank</c> iframe 继承父页 CSP，<c>sandbox</c> 指令还会让
        ///     <c>contentDocument</c> 变成不透明源，直接抛 SecurityError；
        ///   · 往宿主页面塞 iframe 本身就是污染，SPA 页面可能正盯着 DOM 变动。
        ///     构造式样式表 + DOMParser 的文档全程不碰宿主页面，也不看 CSP 脸色。
        ///
        /// 另外 <c>@media (prefers-color-scheme)</c> 用 <c>matchMedia</c> 按当前配色求值，
        /// 浅色拿到黑、深色拿到白 —— 比我们静态挑一个颜色更正确。
        /// </summary>
        private const string SvgNormalizeScript = """
            (() => {
              try {
                const svgText = __SVG_TEXT__;
                const doc = new DOMParser().parseFromString(svgText, 'image/svg+xml');
                const root = doc.documentElement;
                if (!root || doc.querySelector('parsererror')) return '';

                const props = ['fill','stroke','fill-opacity','stroke-opacity','stroke-width','opacity','stop-color','fill-rule','stroke-linecap','stroke-linejoin','stroke-dasharray'];
                const colorProps = { fill: 1, stroke: 1, 'stop-color': 1 };

                // 算出来的颜色一律压成 #rrggbb：D2D 那套 SVG 子集认不认 rgb() 没写进文档，别赌
                const toHex = (v) => {
                  const m = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+))?\s*\)/i.exec(v);
                  if (!m) return null;
                  const c = (n) => Math.max(0, Math.min(255, Math.round(parseFloat(n)))).toString(16).padStart(2, '0');
                  return { hex: '#' + c(m[1]) + c(m[2]) + c(m[3]), a: m[4] === undefined ? -1 : parseFloat(m[4]) };
                };
                const bad = (v) => !v || v === 'none' || v === 'inherit' || v === 'transparent' || v.indexOf('var(') === 0;
                const put = (el, p, v) => {
                  if (colorProps[p]) {
                    const h = toHex(v);
                    if (h) {
                      el.setAttribute(p, h.hex);
                      if (h.a >= 0 && h.a < 1) el.setAttribute(p + '-opacity', String(h.a));
                      return;
                    }
                  }
                  el.setAttribute(p, v);
                };

                // ⭐ 把 <style> 的文本抠出来喂给构造式样式表，从 CSSOM 里读规则自己写回属性。
                //    不依赖浏览器「应用」CSS（CSP 会拦），也不依赖 getComputedStyle（要浏览上下文）。
                let css = '';
                for (const st of root.querySelectorAll('style')) { css += (st.textContent || '') + '\n'; st.remove(); }

                if (css.trim()) {
                  const sheet = new CSSStyleSheet();
                  sheet.replaceSync(css);
                  const targets = (sel) => {
                    const list = [];
                    try {
                      if (root.matches(sel)) list.push(root);   // :root / svg 命中的是根自己，querySelectorAll 不返回它
                      for (const el of root.querySelectorAll(sel)) list.push(el);
                    } catch (e) { /* 选择器认不出来就跳过 */ }
                    return list;
                  };
                  const walk = (rules) => {
                    for (let i = 0; i < rules.length; i++) {
                      const r = rules[i];
                      if (r.style && r.selectorText) {
                        const list = targets(r.selectorText);
                        for (const p of props) {
                          const v = r.style.getPropertyValue(p);
                          if (v === '' || bad(v)) continue;
                          for (const el of list) put(el, p, v);
                        }
                      }
                      // ⚠️ 先查自己再递归：Chromium 里 CSSStyleRule 也带 cssRules（CSS Nesting 引入的），
                      //    拿「有没有 cssRules」当分组规则的判据会把 :root{fill:#000} 整条跳过。
                      if (r.cssRules) {
                        const cond = r.conditionText || (r.media && r.media.mediaText) || '';
                        let ok = true;
                        if (cond) {
                          try { ok = r.constructor.name === 'CSSSupportsRule' ? CSS.supports(cond) : matchMedia(cond).matches; } catch (e) { ok = false; }
                        }
                        if (ok) walk(r.cssRules);
                      }
                    }
                  };
                  walk(sheet.cssRules);
                }

                // 继承补全：根上定了填充色就给没写 fill 的形状补上，别赌 D2D 的属性继承
                const rootFill = root.getAttribute('fill');
                if (rootFill && !bad(rootFill)) {
                  for (const el of root.querySelectorAll('path,rect,circle,ellipse,line,polyline,polygon')) {
                    if (!el.hasAttribute('fill')) el.setAttribute('fill', rootFill);
                  }
                }
                if (bad(root.getAttribute('fill'))) root.removeAttribute('fill');

                return new XMLSerializer().serializeToString(root);
              } catch (e) {
                // ⭐ 别静默返回空串：真机上一旦出错，C# 侧只看到「没吐出东西」，
                //    根本无从下手。把名字和消息带回去。
                return 'ERR|' + ((e && e.name) || 'Error') + '|' + ((e && e.message) || String(e));
              }
            })()
            """;

        /// <summary>
        /// SVG 抢救脚本 ②（兜底）：交给 Chromium 直接画成 PNG 再要回来。
        ///
        /// 归一化的路走不通时（比如图形靠 <c>&lt;filter&gt;</c> / <c>&lt;mask&gt;</c> /
        /// <c>&lt;text&gt;</c> 这些 D2D 干脆不认的元素），画图是最后一招 ——
        /// Chromium 的 SVG 支持是完整的，画出来什么样就是什么样。代价是矢量变固定尺寸位图。
        ///
        /// 用 data: URL 喂给 <c>Image</c>：data URL 算同源，不会污染 canvas，
        /// <c>toDataURL</c> 才拿得到东西（远程地址会直接抛 SecurityError）。
        /// Promise + 超时二选一：脚本自己兜底，绝不让 ExecuteScript 挂住。
        /// </summary>
        private const string SvgRasterizeScript = """
            (() => {
              try {
                const svgText = __SVG_TEXT__;
                const size = __SIZE__;
                const task = window.__dtSvgRaster = { done: false, value: '', err: '' };
                // ⭐ 失败原因也要带回：站点的 CSP 常常连 data: 图片一起禁（img-src），
                //    那种情况 img 直接 onerror，不记一笔就永远查不出来。
                const finish = (v, err) => { task.value = v || ''; task.err = err || ''; task.done = true; };
                try {
                  const img = new Image();
                  img.onload = () => {
                    try {
                      const canvas = document.createElement('canvas');
                      canvas.width = size;
                      canvas.height = size;
                      canvas.getContext('2d').drawImage(img, 0, 0, size, size);
                      finish(canvas.toDataURL('image/png'), '');
                    } catch (e) { finish('', 'draw|' + ((e && e.name) || 'Error') + '|' + ((e && e.message) || String(e))); }
                  };
                  img.onerror = () => finish('', 'img-error');   // 多半是 CSP 的 img-src 拦了 data: URL
                  img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgText);
                  setTimeout(() => { if (!task.done) finish('', 'timeout'); }, 4000);
                } catch (e) { finish('', 'sync|' + ((e && e.name) || 'Error') + '|' + ((e && e.message) || String(e))); }
                return 'started';
              } catch (e) { return ''; }
            })()
            """;

        /// <summary>
        /// 光栅化脚本 ②：轮询槽位。
        ///
        /// ⭐ ExecuteScriptAsync <b>不会等 Promise</b> —— 返回 Promise 的脚本拿到的是 "{}" 或 "null"
        /// （来源：microsoft/WebView2Feedback#416，社区实测一致）。所以不能靠 await，
        /// 只能点火 + 轮询：脚本把结果写进 <c>window.__dtSvgRaster</c>，我们再把它读回来。
        /// 用 JSON 而不是裸字符串，是为了区分「还没画完」和「画完了但失败」。
        /// </summary>
        private const string SvgRasterizePollScript =
            "JSON.stringify(window.__dtSvgRaster || { done: false, value: '' })";

        /// <summary>光栅化脚本 ③：用完把槽位擦掉，别在人家页面上留全局变量</summary>
        private const string SvgRasterizeCleanupScript = "delete window.__dtSvgRaster, ''";

        /// <summary>
        /// 图标写盘的防抖窗口（毫秒）。
        /// shortcuts.json 里图标是 base64，整个文件可能几 MB，
        /// FaviconChanged 在 SPA 里可能连着来好几次 —— 合并成「最后一次生效」再写。
        /// </summary>
        private const int IconPersistDebounceMs = 1200;

        /// <summary>每个应用一个待写入的取消令牌，新的来了就掐掉旧的</summary>
        private static readonly Dictionary<string, CancellationTokenSource> IconPersistDebounce =
            new(StringComparer.Ordinal);

        private static readonly object IconPersistLock = new();

        /// <summary>原件下载用的客户端（静态单例，避免每个页面一个连接池）</summary>
        private static readonly HttpClient FaviconHttpClient = CreateFaviconHttpClient();

        /// <summary>FaviconUri → 图标字节。跨页面/跨导航复用，同一个站点只下一次</summary>
        private static readonly Dictionary<string, byte[]> FaviconCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 守护 FaviconCache。读它的回调是在线程池上跑的（下载 await 之后回不来 UI 线程），
        /// 多个网页应用的页面各跑各的，Dictionary 裸奔会写坏。
        /// </summary>
        private static readonly object FaviconCacheLock = new();

        /// <summary>上一次拉取的取消令牌。图标变化密集时只让最后一次生效</summary>
        private CancellationTokenSource? _faviconCts;

        /// <summary>
        /// 「当前文档 + favicon 地址」的取图去重键。
        /// 官方 spec 明说同一个 favicon 会重复触发 FaviconChanged，而 FaviconCache 只记成功的结果 ——
        /// 失败的话（站点没大图、域名零星 403）每次事件都要重跑一遍「DOM 扫描 + 最多 4 次下载」。
        /// 键里带上文档地址是为了换页后能重新试，不会把一个站点的失败记到另一个站点头上。
        /// </summary>
        private string? _lastFaviconAttemptKey;

        /// <summary>
        /// 「这个快捷方式自己跳过去之后的域」，用来放宽 <see cref="IsSameSite"/>。
        ///
        /// 为什么要它：快捷方式的 URL 常常只是个跳转入口 —— <c>gpt.com</c> 301 到
        /// <c>chatgpt.com</c>，<c>x.ai</c> 到 <c>grok.com</c>。文档域和快捷方式域就此分家，
        /// 同站闸门会把自家站点判成外站，图标一次都抓不到，条目永远是空的。
        /// 而「用户点进外站」必须继续拦住，所以这里只认【初始导航落到哪儿】这一个域，
        /// 而且只认一次 —— 之后用户再跳到别的域，host 对不上照样拦。
        /// </summary>
        private string? _trustedDocumentHost;

        /// <summary>
        /// <see cref="_trustedDocumentHost"/> 是给哪个快捷方式记的。
        /// 页面实例会在不同快捷方式之间复用，换人了就得重新认定，不能拿 A 的信任域给 B 用。
        /// </summary>
        private string? _trustedHostOwnerId;

        private static HttpClient CreateFaviconHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                       | System.Net.DecompressionMethods.Deflate
                                       | System.Net.DecompressionMethods.Brotli
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            // 不少站点对裸 UA 直接 403，装成浏览器
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            return client;
        }

        /// <summary>
        /// CoreWebView2.FaviconChanged 事件入口。
        /// 首次导航（哪怕页面没声明图标）会来一次，之后 JS/DOM 改图标会再来。
        /// </summary>
        private void CoreWebView2_FaviconChanged(object? sender, object e)
        {
            // fire-and-forget：里面自己兜异常，绝不让 favicon 这种装饰性逻辑把事件链打断
            _ = UpdateFaviconAsync();
        }

        private async Task UpdateFaviconAsync()
        {
            CancellationTokenSource? cts = null;

            try
            {
                CoreWebView2? core = WebView?.CoreWebView2;
                if (core is null)
                {
                    return;
                }

                byte[]? cachedHit = null;

                // CoreWebView2 已经销毁时（页面切走 / WebView 重建）读属性会抛，
                // 整段包在 try 里就是为了接住这种窗口期的异常
                string faviconUri = core.FaviconUri ?? string.Empty;

                // 页面没声明图标：FaviconUri 是空串，此时 GetFaviconAsync 给的是一张
                // 「未定义」占位图，拿它盖掉用户自己设好的图标纯属帮倒忙 —— 保持原样。
                if (string.IsNullOrWhiteSpace(faviconUri))
                {
                    return;
                }

                // ⭐ 同站闸门：在网页应用里点进外站时，外站的图标不许盖到这个应用头上。
                //    比的是【当前文档】的域，不是 favicon 文件的域 ——
                //    很多站把图标放在 CDN 上（GitHub 的图标在 github.githubassets.com），
                //    拿图标文件的域比对会误杀自己的站点。
                string documentUrl = core.Source ?? string.Empty;
                if (_currentShortcut is not null && !IsTrustedSite(documentUrl, _currentShortcut))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] 外站图标，不覆盖: {documentUrl}");
                    return;
                }

                lock (FaviconCacheLock)
                {
                    if (FaviconCache.TryGetValue(faviconUri, out byte[]? cached))
                    {
                        cachedHit = cached;
                    }
                }

                // ⭐ 命中缓存也别直接交出去 —— 以前这里「ApplyFavicon + return」，
                //    把后面的空白检测 / 防退化整段跳过了：第一次抓到什么就锁死成什么，
                //    （而第一次常常正是渲染内核还没热起来、只兜到 16x16 的那一次）
                //    表现就是「图标一直不更新」。缓存该省的只是重新下载，判定照做。
                byte[]? bytes = cachedHit;

                // 同一个「文档 + favicon」只认真试一次，重复通知直接略过
                string attemptKey = documentUrl + "|" + faviconUri;
                if (bytes is null &&
                    string.Equals(_lastFaviconAttemptKey, attemptKey, StringComparison.Ordinal))
                {
                    return;
                }

                if (bytes is null)
                {
                    // 图标可能在短时间内连着变（SPA 换路由、页面自己改图标）。
                    // 每个都真的去下会很吵，用 CTS 串起来：新的来了就把旧的掐掉。
                    // ⚠️ 只 Cancel 不 Dispose：旧的那个 task 还拿着它的 Token，
                    //    Dispose 之后再用会抛 ObjectDisposedException。
                    _faviconCts?.Cancel();
                    cts = new CancellationTokenSource();
                    _faviconCts = cts;

                    // 整轮取图的总闸：高清链最坏要打 manifest + 4 个候选，
                    // 每个都顶满 HttpClient 的 8s 超时的话会拖很久，这里统一掐在 12s
                    cts.CancelAfter(TimeSpan.FromSeconds(IconFetchTimeoutSeconds));

                    try
                    {
                        // ① 高清优先：扫 DOM 的 link[rel*=icon] + site.webmanifest，
                        //    按声明尺寸从大到小试。FaviconUri 往往只指向 16x16 那份，
                        //    站点真正清晰的大图（180 / 192 / 512）都在这些声明里。
                        bytes = await TryFetchHighResIconAsync(core, _currentShortcut?.Id, PreferDarkIcon(), cts.Token);

                        // ② FaviconUri 指向的原件
                        if (bytes is null && !cts.IsCancellationRequested)
                        {
                            bytes = await DownloadOriginalIconAsync(faviconUri, cts.Token);
                        }

                        // ③ Edge 已经缓存过的图标。
                        //    纯 SVG 站点（GitHub 就是）上面两级都拿不到能解码的位图，
                        //    而 Edge 的 Favicons 库里存的是位图，这正是「没 Edge 清晰」的那部分缺口。
                        if (bytes is null && !cts.IsCancellationRequested)
                        {
                            bytes = TryGetEdgeFavicon(documentUrl);
                        }

                        // ④ 兜底：内核手里那份（实测恒 16x16）。
                        //    期间内核可能已经被换掉（WebView 重建 / 页面切走），换掉就别再问它要图了。
                        if (bytes is null
                            && !cts.IsCancellationRequested
                            && ReferenceEquals(WebView?.CoreWebView2, core))
                        {
                            bytes = await GetKernelFaviconAsync(core);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Favicon] 拉取失败: {faviconUri}, {ex.Message}");
                    }
                }   // ← 取图只在没命中缓存时才做

                // 只有「正常跑完」的这一轮才记进去重键：超时 / 被后来的事件顶掉的轮次
                // 留着口子，下一次事件还能再试一次，不至于一次网络抖动就永远放弃这个图标。
                if (cts is { IsCancellationRequested: false })
                {
                    _lastFaviconAttemptKey = attemptKey;
                }

                // 被后来的请求顶掉了 / 没拿到东西，都直接放弃，别覆盖现有图标
                if (bytes is null || bytes.Length == 0)
                {
                    return;
                }

                // ⭐ 空内容闸门（兜底）：四条取图路径（高清链 / 原件 / Edge / 内核）
                //    出来的图都过这一道。能解码不等于画得出来，空白图一律不采用，
                //    宁可保持原样 —— 盖上去就是「图标不见了」，比没更新严重。
                if (await IsBlankIconAsync(bytes))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] 图标内容为空，不采用: {faviconUri}");
                    return;
                }

                // cts 为 null = 走的缓存命中那条路，没有「被后来的请求顶掉」这一说
                if (cts is not null &&
                    (cts.IsCancellationRequested || !ReferenceEquals(_faviconCts, cts)))
                {
                    return;
                }

                // 防退化：高清链没拿到、兜底只给了 16x16 时，别把已经存好的大图冲掉。
                // 只有新图确实更大（或原本没图）才覆盖。
                int newSize = await GetIconSizeAsync(bytes);
                byte[]? existingIcon = _currentShortcut?.IconBytes;
                int oldSize = existingIcon is { Length: > 0 } ? await GetIconSizeAsync(existingIcon) : 0;

                // 反方向也挡一道：新抓到的是色块包、而现在这张不是 —— 别用色块把好图冲掉。
                // 只在尺寸够大时才判（色块包都是 128 起步），小图没必要多解一次码。
                if (oldSize > 0 && newSize >= 128 && await IsPaddedBlockIconAsync(bytes))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Favicon] 新图 {newSize}px 是色块包图标，保留现有的 {oldSize}px");
                    return;
                }

                // ⭐ 存量色块图的豁免：防退化规则本来是「新图比现有小就不覆盖」，
                //    但那批 180/192 的手机图标正是靠尺寸大才霸占着位置的 ——
                //    判出来就让路，允许被更小但主体填满的经典 favicon 换掉。
                //    （这同时也是给前几轮已经存进来的污染图标一条自动纠正的路）
                bool existingIsPaddedBlock = existingIcon is { Length: > 0 } &&
                                             await IsPaddedBlockIconAsync(existingIcon);

                // ⭐ 存量空白图的豁免：「小图不许盖大图」这条规则会把已经存进去的
                //    空白 SVG（矢量按 256 参与比较）永久供着 —— 光加空内容闸门，
                //    用户手里那张空 GPT 图标照样换不掉。判出现有图标本身就是空的，
                //    就放行，让任何一张真有内容的图把它替掉（尺寸小也认）。
                bool existingIsBlank = existingIcon is { Length: > 0 } &&
                                       await IsBlankIconAsync(existingIcon);

                // ⚠️ 这里是严格「更小」才挡：站点换了张同尺寸的新图标是要生效的，
                //    写成 >= 的话同尺寸换图永远进不来。内容没变的情况由 ApplyFaviconAsync 挡。
                if (oldSize > 0 && oldSize > newSize && !existingIsPaddedBlock && !existingIsBlank)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Favicon] 新图 {newSize}px 比现有 {oldSize}px 小，保留现有图标");
                    return;
                }

                if (existingIsPaddedBlock)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Favicon] 现有 {oldSize}px 是色块包图标，改用 {newSize}px 的经典图标");
                }

                if (existingIsBlank)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Favicon] 现有图标是空内容，改用 {newSize}px 的新图标");
                }

                CacheFavicon(faviconUri, bytes);
                await ApplyFaviconAsync(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 更新流程异常: {ex.Message}");
            }
            finally
            {
                // 走到这里所有 await 都结束了，没人再拿它的 Token。
                // CancelAfter 会在内部挂一个定时器，不 Dispose 就要等 GC 才释放。
                if (cts is not null)
                {
                    if (ReferenceEquals(_faviconCts, cts))
                    {
                        _faviconCts = null;
                    }

                    cts.Dispose();
                }
            }
        }

        /// <summary>
        /// 把拿到的图标交给顶栏。走 PublishShortcutIconAsync：落盘 + 下发，
        /// 文件名是内容哈希，天然不会撞 BitmapImage 那个「同 Uri 不重新解码」的坑。
        /// </summary>
        private async Task ApplyFaviconAsync(byte[] bytes)
        {
            string? appId = _currentShortcut?.Id;

            // 跟内存里已经是同一张图就别折腾了：重复导航、FaviconChanged 连发
            // （官方 spec 明说同一个 favicon 会重复触发）都会走到这里
            if (_currentShortcut?.IconBytes is { Length: > 0 } existing &&
                existing.AsSpan().SequenceEqual(bytes))
            {
                return;
            }

            // 更新内存里的快捷方式副本，别的读 _currentShortcut 的地方（顶栏等）跟着变。
            if (_currentShortcut is not null)
            {
                _currentShortcut = _currentShortcut with { IconBytes = bytes };
            }

            await PublishShortcutIconAsync(bytes);
            System.Diagnostics.Debug.WriteLine($"[Favicon] 已更新站点图标（{bytes.Length} 字节）");

            if (!string.IsNullOrEmpty(appId))
            {
                _ = PersistIconAsync(appId, bytes);
            }
        }

        /// <summary>
        /// 防抖落盘 + 广播。FaviconChanged 可能连着来好几次（SPA 换路由、页面自己改图标），
        /// 这里只让最后一次真的去写 shortcuts.json；写完再广播，侧边栏才跟着换。
        /// </summary>
        private static async Task PersistIconAsync(string appId, byte[] bytes)
        {
            var cts = new CancellationTokenSource();

            lock (IconPersistLock)
            {
                if (IconPersistDebounce.TryGetValue(appId, out CancellationTokenSource? pending))
                {
                    pending.Cancel();
                }

                IconPersistDebounce[appId] = cts;
            }

            try
            {
                await Task.Delay(IconPersistDebounceMs, cts.Token);
            }
            catch (Exception)
            {
                return; // 后面又来了一张新图，交给它写
            }

            lock (IconPersistLock)
            {
                if (!IconPersistDebounce.TryGetValue(appId, out CancellationTokenSource? current) ||
                    !ReferenceEquals(current, cts))
                {
                    return;
                }

                IconPersistDebounce.Remove(appId);
            }

            if (await PersistIconCoreAsync(appId, bytes))
            {
                // 广播出去：导航栏（侧边栏）的 OnUpdateCompleted 会重新 Load 并重建图标
                WebAppUpdateService.NotifyUpdate(appId, WebAppUpdateType.Icon);
            }
        }

        /// <summary>
        /// 原子地把 IconBytes 写进 shortcuts.json，只改这一个字段。
        /// 字节跟存量完全一致时返回 false（不写盘、不广播）。
        /// </summary>
        private static async Task<bool> PersistIconCoreAsync(string appId, byte[] bytes)
        {
            try
            {
                return await WebAppShortcutStore.UpdateAsync<bool>(list =>
                {
                    // 「不写盘」要写成显式类型的 null：元组里放裸 null 的话 TResult 推断不出来，
                    // 编译器会去挑不带返回值的那个重载然后报错。
                    IReadOnlyList<WebAppShortcut>? noChange = null;

                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!string.Equals(list[i].Id, appId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        WebAppShortcut existing = list[i];
                        if (existing.IconBytes is { Length: > 0 } &&
                            existing.IconBytes.AsSpan().SequenceEqual(bytes))
                        {
                            return (noChange, false);
                        }

                        IReadOnlyList<WebAppShortcut> next = new List<WebAppShortcut>(list)
                        {
                            [i] = existing with { IconBytes = bytes }
                        };

                        return (next, true);
                    }

                    return (noChange, false);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 图标持久化失败: {appId}, {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ① 高清优先：从当前文档的 DOM 里把所有图标声明捞出来，按尺寸从大到小试。
        ///
        /// 为什么要绕这一圈：FaviconUri 通常只指向站点默认那份 16x16/32x32，
        /// 而真正清晰的那份（apple-touch-icon 180、部分站点的 96/128）只写在
        /// HTML 的 link 标签里，GetFaviconAsync 拿不到。
        ///
        /// ⚠️ 只认 link 标签，不认 site.webmanifest —— 见方法里那段说明。
        ///
        /// 拿不到大图时返回已找到的最大那份（哪怕只有 32px），全失败返回 null。
        /// </summary>
        private static async Task<byte[]?> TryFetchHighResIconAsync(
            CoreWebView2 core,
            string? appId,
            bool preferDark,
            CancellationToken token)
        {
            var candidates = new List<IconCandidate>();

            try
            {
                // ExecuteScriptAsync 返回的是「被 JSON 编码了一次的字符串」，要先解一层。
                // 用 JsonDocument 而不是 JsonSerializer.Deserialize<string>：后者带
                // RequiresDynamicCode/RequiresUnreferencedCode，在 AOT 下会告警。
                string raw = await core.ExecuteScriptAsync(IconProbeScript);

                string json;
                using (JsonDocument outer = JsonDocument.Parse(raw))
                {
                    json = outer.RootElement.ValueKind == JsonValueKind.String
                        ? outer.RootElement.GetString() ?? "{}"
                        : "{}";
                }

                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        string href = ReadString(item, "href");
                        string sizes = ReadString(item, "sizes");
                        string rel = ReadString(item, "rel");
                        string type = ReadString(item, "type");

                        if (string.IsNullOrEmpty(href))
                        {
                            continue;
                        }

                        // ⭐ 不再拦 SVG：它是矢量图，比任何位图都清晰，显示侧走
                        //    SvgImageSource 直接渲染。唯一要避开的是 mask-icon（Safari
                        //    固定标签那种纯黑剪影），那个已经在平台关键词里降为备胎档了。
                        int priority = ClassifyIconCandidate(rel, href);
                        candidates.Add(new IconCandidate(href, EstimateIconSize(sizes, rel, href, type), rel, priority));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 扫描图标声明失败: {ex.Message}");
            }

            // ⚠️ 刻意不读 site.webmanifest：PWA 那份 192/512 是「色块包图标」——
            //    给整块背景色 + 四周留白，缩到侧边栏 20px 时主体被 padding 挤得更小，
            //    比原 favicon 还糊。宁可要尺寸小一点但主体填满的图。
            //    （manifestUrl 也不再采集，脚本里一并去掉了）

            if (candidates.Count == 0)
            {
                return null;
            }

            byte[]? best = null;
            int bestSize = 0;

            // ① 桌面 favicon 优先：尺寸从大到小试，够清晰就收工
            (best, bestSize) = await TryFetchCandidateGroupAsync(
                core,
                appId,
                preferDark,
                candidates.Where(c => c.Priority == IconPriorityDesktop),
                MaxIconCandidates,
                token);

            if (bestSize >= PreferredIconSize)
            {
                return best;
            }

            // ② 桌面那份实在太小（典型就是只有 16x16 的 favicon.ico）时才动用手机图标。
            //    只要桌面版到 24px 就绝不碰它们 —— 那批色块包图是用户明确不要的。
            if (bestSize >= FallbackToPlatformIconSize || token.IsCancellationRequested)
            {
                return best;
            }

            (byte[]? platformBest, int platformSize) = await TryFetchCandidateGroupAsync(
                core,
                appId,
                preferDark,
                candidates.Where(c => c.Priority == IconPriorityPlatform),
                MaxPlatformIconCandidates,
                token);

            return platformSize > bestSize ? platformBest : best;
        }

        /// <summary>按声明尺寸从大到小试一组候选，返回其中最大的那份。够大就提前收工</summary>
        private static async Task<(byte[]? Bytes, int Size)> TryFetchCandidateGroupAsync(
            CoreWebView2 core,
            string? appId,
            bool preferDark,
            IEnumerable<IconCandidate> candidates,
            int limit,
            CancellationToken token)
        {
            byte[]? best = null;
            int bestSize = 0;

            IEnumerable<IconCandidate> ordered = candidates
                .Where(c => c.Size > 0 && !string.IsNullOrEmpty(c.Href))
                .OrderByDescending(c => c.Size)
                .DistinctBy(c => c.Href)
                .Take(limit);

            foreach (IconCandidate candidate in ordered)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                (byte[]? bytes, int size) = await TryFetchCandidateAsync(core, appId, preferDark, candidate, token);
                if (bytes is null)
                {
                    continue;
                }

                // 够清晰就收工，不再试后面的
                if (size >= PreferredIconSize)
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] 高清图标命中 {size}px: {candidate.Href}");
                    return (bytes, size);
                }

                if (size > bestSize)
                {
                    best = bytes;
                    bestSize = size;
                }
            }

            return (best, bestSize);
        }

        private const int IconPriorityDesktop = 0;
        private const int IconPriorityPlatform = 1;

        /// <summary>
        /// 0 = 桌面 favicon（首选）；1 = 手机 / 平台专用图标（备胎）。
        /// rel 和 href 一起看：很多站点 rel 就写 "icon"，但文件名是 android-chrome-192x192.png。
        /// </summary>
        private static int ClassifyIconCandidate(string rel, string href)
        {
            string probe = rel + " " + href;

            foreach (string keyword in PlatformIconKeywords)
            {
                if (probe.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return IconPriorityPlatform;
                }
            }

            return IconPriorityDesktop;
        }

        private static async Task<(byte[]? Bytes, int Size)> TryFetchCandidateAsync(
            CoreWebView2 core,
            string? appId,
            bool preferDark,
            IconCandidate candidate,
            CancellationToken token)
        {
            // 有些站点直接内联 data:image/png;base64，不用走网络
            byte[]? bytes = TryDecodeDataUri(candidate.Href)
                            ?? await DownloadOriginalIconAsync(candidate.Href, token);

            if (bytes is null)
            {
                return (null, 0);
            }

            int size = await GetIconSizeAsync(bytes);
            if (size <= 0)
            {
                return (null, 0);
            }

            // ⭐ 空内容图标不许占坑。但 SVG 要先抢救一次再判死刑 ——
            //    站点的 SVG 经常只是「颜色写在 CSS 里」，交给 WebView(Chromium) 算一遍就能救回来，
            //    救回来的还是矢量（比后面那张 48px 位图清晰）。救不回来才作废，继续试后面的候选。
            //    必须在这层挡：上一层的逻辑是「够清晰就提前收工」，
            //    一张 256px 的空白 SVG 会在这里把后面真正能看的位图全顶掉。
            if (await IsBlankIconAsync(bytes))
            {
                if (!WebAppIconCache.IsSvgContent(bytes))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] 候选图标内容为空，跳过: {candidate.Href}");
                    return (null, 0);
                }

                (byte[]? salvaged, int salvagedSize) = await SalvageSvgAsync(core, appId, preferDark, bytes, token);
                if (salvaged is null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] 空白 SVG 抢救失败，跳过: {candidate.Href}");
                    return (null, 0);
                }

                System.Diagnostics.Debug.WriteLine($"[Favicon] 空白 SVG 已抢救 {salvaged.Length} 字节: {candidate.Href}");
                return (salvaged, salvagedSize);
            }

            return (bytes, size);
        }

        /// <summary>
        /// 抢救一张「画出来是空的」SVG，四招按顺序试：
        ///   ① 专用内核光栅化 —— 交给进程内那个隐藏 WebView2 画成 PNG（⭐ 根治路径，产物是位图，
        ///      Chromium 自己算 CSS 和 @media，跟 Chrome 处理 favicon 的路子一致）；
        ///   ② 归一化 —— 让宿主页面把算好的样式写成显式属性，输出 D2D 认得的 SVG（仍是矢量）；
        ///   ③ 内联兜底 —— C# 自己把 &lt;style&gt; 里的颜色抠出来写成属性（仍是矢量，不依赖 WebView）；
        ///   ④ 光栅化 —— 在宿主页面里用 canvas 画成 PNG（受页面环境牵制，排在最后）。
        ///
        /// 四招的产物都要再过一遍空内容判定才算救活：
        /// 也可能算出一张全透明的图（比如 SVG 本身就是个空壳），那种救了也白救。
        /// </summary>
        private static async Task<(byte[]? Bytes, int Size)> SalvageSvgAsync(
            CoreWebView2 core,
            string? appId,
            bool preferDark,
            byte[] svgBytes,
            CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    return (null, 0);
                }

                // ① 专用隐藏内核光栅化：让 Chromium 把 SVG 画成 PNG。
                //    这是根治路径 —— CSS、@media(prefers-color-scheme)、字体、filter 全由内核算，
                //    产物是位图，后面所有判定都只面对位图，不用再拆 SVG。
                //    它挂了会返回 null，自然落回下面几招，不会比原来差。
                byte[]? raster = await WebAppIconRasterizer.Instance
                    .RasterizeSvgAsync(svgBytes, SvgRasterSize, preferDark);

                if (raster is null)
                {
                    System.Diagnostics.Debug.WriteLine("[Favicon] SVG 专用内核光栅化：没拿到图，走后续招式");
                }
                else if (await IsBlankIconAsync(raster))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 专用内核光栅化：产物 {raster.Length} 字节仍然空白");
                }
                else
                {
                    // ⭐ 光栅化产物的颜色是「抓图这一刻的主题」算出来的，换主题要拿原件重画 ——
                    //    没有原件的话那张 PNG 就永远定死在这个颜色上了。留一份在磁盘上。
                    WebAppIconCache.SaveIconSource(appId, svgBytes);
                    return (raster, SvgRasterSize);
                }

                byte[]? normalized = await TryNormalizeSvgAsync(core, svgBytes);
                if (normalized is null)
                {
                    System.Diagnostics.Debug.WriteLine("[Favicon] SVG 归一化：脚本没吐出东西");
                }
                else if (await IsBlankIconAsync(normalized))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 归一化：产物 {normalized.Length} 字节仍然空白，转内联兜底");
                }
                else
                {
                    return (normalized, VectorIconSize);
                }

                // ② C# 内联兜底：不用 WebView，自己把 CSS 里的颜色写回属性
                byte[]? inlined = InlineCssColours(svgBytes, preferDark);
                if (inlined is null)
                {
                    System.Diagnostics.Debug.WriteLine("[Favicon] SVG 内联兜底：CSS 里没找到能用的颜色");
                }
                else if (await IsBlankIconAsync(inlined))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 内联兜底：产物 {inlined.Length} 字节仍然空白");
                }
                else
                {
                    return (inlined, VectorIconSize);
                }

                if (token.IsCancellationRequested)
                {
                    return (null, 0);
                }

                byte[]? rasterized = await TryRasterizeSvgAsync(core, svgBytes, token);
                if (rasterized is null)
                {
                    System.Diagnostics.Debug.WriteLine("[Favicon] SVG 光栅化：没画出东西");
                }
                else if (await IsBlankIconAsync(rasterized))
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 光栅化：产物 {rasterized.Length} 字节仍然空白");
                }
                else
                {
                    return (rasterized, SvgRasterSize);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 抢救失败: {ex.Message}");
            }

            return (null, 0);
        }

        /// <summary>会被补上根填充色的图形元素。跟空内容判定那份形状清单保持一致</summary>
        private static readonly string[] ShapeElementNames =
            { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon" };

        private static bool IsShapeElement(XElement element)
            => ShapeElementNames.Contains(element.Name.LocalName, StringComparer.Ordinal);

        /// <summary>
        /// 抢救招 ②：不依赖 WebView，纯文本把 &lt;style&gt; 里的颜色内联成属性。
        ///
        /// 为什么还要它：招 ① 得走 ExecuteScript，站点 CSP、脚本执行环境都可能让它空手而归；
        /// 而 favicon 的 CSS 就那么几种写法，根本用不着浏览器来算 ——
        /// 自己抠出来写回去，零依赖、100% 可控。
        ///
        /// ⚠️ 选择器远不止 <c>:root</c> 一种（实测抓到的真样本）：
        ///   chatgpt.com  <c>:root{fill:#000}</c>            + @media dark 反色
        ///   astro.build  <c>g{fill:#000}</c>                + @media dark 反色
        ///   rust-lang    <c>#logo{fill:#FFF}</c>            id 选择器
        ///   vuejs.org    <c>.st0{fill:#42B883}</c>          class 选择器（双色品牌 logo，跟主题无关）
        /// 所以要做的是「按选择器匹配元素」。只认 <c>:root</c> 的话后三种全漏，
        /// 等于这招只对 ChatGPT 一家有效。
        ///
        /// 两条硬规矩：
        ///   · 只在元素自己没写这个属性时才补 —— 有声明就不被层叠覆盖，跟 CSS 一个道理。
        ///     否则 vuejs 那种多色 logo 会被一色刷平；
        ///   · @media 分支不跨主题回退：浅色分支里没写 fill 就是没写，绝不拿深色分支的
        ///     白色去填 —— 那正好是「白底白图标」，比不填还糟。
        ///     站点压根没做主题适配（深色分支为空）时才用那唯一一份颜色。
        /// </summary>
        private static byte[]? InlineCssColours(byte[] svgBytes, bool preferDark)
        {
            try
            {
                XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(svgBytes), LoadOptions.PreserveWhitespace);
                if (doc.Root is null)
                {
                    return null;
                }

                List<XElement> styles = doc.Root.Descendants()
                    .Where(e => e.Name.LocalName.Equals("style", StringComparison.Ordinal))
                    .ToList();
                if (styles.Count == 0)
                {
                    return null;
                }

                string css = string.Concat(styles.Select(s => s.Value + "\n"));

                Dictionary<XElement, (string? Fill, string? Stroke)> paint =
                    ResolveCssPaint(doc.Root, css, preferDark);
                if (paint.Count == 0)
                {
                    return null;
                }

                // D2D 不认 <style>，留着只是占地方（还可能让判定误以为有颜色）
                foreach (XElement style in styles)
                {
                    style.Remove();
                }

                int written = 0;
                foreach ((XElement element, (string? fill, string? stroke)) in paint)
                {
                    written += ApplyPaint(element, "fill", fill);
                    written += ApplyPaint(element, "stroke", stroke);
                }

                if (written == 0)
                {
                    return null;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
                return bytes.Length is > 0 and <= MaxFaviconBytes ? bytes : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 内联兜底解析失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 把一个上色值写到元素上，并把后代里「没写这个属性」的形状也一并补上。
        ///
        /// 为什么要往下补：D2D 那套 SVG 子集的属性继承不好赌，父元素定了颜色不代表
        /// 子形状会拿到（招 ① 里也是同样的处理）。只在自己没写时才补，
        /// 这样 <c>fill="none" + stroke</c> 的线条图标不会被填成实心块。
        /// </summary>
        private static int ApplyPaint(XElement element, string attribute, string? value)
        {
            if (value is null)
            {
                return 0;
            }

            int written = 0;
            if (element.Attribute(attribute) is null)
            {
                element.SetAttributeValue(attribute, value);
                written++;
            }

            foreach (XElement shape in element.Descendants().Where(IsShapeElement))
            {
                if (shape.Attribute(attribute) is null)
                {
                    shape.SetAttributeValue(attribute, value);
                    written++;
                }
            }

            return written;
        }

        /// <summary>
        /// 把 &lt;style&gt; 里的规则按选择器落到具体元素上，算出每个元素该拿到的 fill / stroke。
        ///
        /// 规则之间后写的赢（CSS 层叠的近似），同一条规则里同一个属性也是后写的赢。
        /// 深浅两套分开存，最后按 <paramref name="preferDark"/> 挑一套出来。
        /// </summary>
        private static Dictionary<XElement, (string? Fill, string? Stroke)> ResolveCssPaint(
            XElement root,
            string css,
            bool preferDark)
        {
            Dictionary<XElement, (string? Fill, string? Stroke)> light = new();
            Dictionary<XElement, (string? Fill, string? Stroke)> dark = new();

            foreach ((string selector, string decls, bool inDark) in ParseCssRules(css))
            {
                string? fill = LastPaintValue(decls, "fill");
                string? stroke = LastPaintValue(decls, "stroke");
                if (fill is null && stroke is null)
                {
                    continue;
                }

                Dictionary<XElement, (string? Fill, string? Stroke)> bucket = inDark ? dark : light;
                foreach (XElement element in MatchElements(root, selector))
                {
                    bucket.TryGetValue(element, out (string? Fill, string? Stroke) current);
                    bucket[element] = (fill ?? current.Fill, stroke ?? current.Stroke);
                }
            }

            Dictionary<XElement, (string? Fill, string? Stroke)> chosen = preferDark ? dark : light;

            // 站点压根没做主题适配（深色分支是空的）时，那份颜色本来就该两用，照用
            return chosen.Count > 0 ? chosen : light;
        }

        /// <summary>取声明块里某个属性的最后一个有效值（同一条规则里后写的赢）</summary>
        private static string? LastPaintValue(string decls, string property)
        {
            string? result = null;

            foreach (Match match in Regex.Matches(
                decls,
                $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase))
            {
                string value = match.Groups[1].Value.Trim();
                if (IsPaintColor(value))
                {
                    result = value;
                }
            }

            return result;
        }

        /// <summary>
        /// 把 CSS 文本拆成一条条规则。手写扫描而不是正则：@media 的花括号是嵌套的，
        /// 正则数不清层数。带回来的 inDark 表示这条规则是不是写在深色主题分支里。
        /// </summary>
        private static IEnumerable<(string Selector, string Decls, bool InDark)> ParseCssRules(string css)
        {
            var rules = new List<(string, string, bool)>();
            bool inDark = false;
            int headStart = 0;
            int declStart = -1;
            int depth = 0;

            for (int i = 0; i < css.Length; i++)
            {
                if (css[i] != '{' && css[i] != '}')
                {
                    continue;
                }

                if (css[i] == '{')
                {
                    string head = css[headStart..i].Trim();

                    if (head.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
                    {
                        inDark = head.Contains("dark", StringComparison.OrdinalIgnoreCase);
                        depth++;
                        headStart = i + 1;
                        continue;
                    }

                    // 别的 @ 规则（@font-face / @supports / @keyframes…）整块跳过，不逐条解析
                    if (head.StartsWith("@", StringComparison.Ordinal))
                    {
                        depth++;
                        headStart = i + 1;
                        continue;
                    }

                    if (head.Length > 0)
                    {
                        declStart = i;
                        depth++;
                    }

                    continue;
                }

                if (declStart >= 0)
                {
                    rules.Add((css[headStart..declStart].Trim(), css[(declStart + 1)..i], inDark));
                    declStart = -1;
                }

                depth--;
                if (depth <= 0)
                {
                    inDark = false;
                    depth = 0;
                }

                headStart = i + 1;
            }

            return rules;
        }

        /// <summary>
        /// 按 CSS 选择器挑元素。只实现 favicon 用得到的那一小撮：
        /// 标签名 / <c>.class</c> / <c>#id</c> / <c>*</c> / <c>:root</c>，外加空格分隔的后代组合。
        /// 再复杂的（<c>&gt;</c> <c>+</c> <c>~</c>、属性选择器、<c>:nth-child</c>…）一概不认 ——
        /// 认了也只会误伤，favicon 那几 KB 的 CSS 不会玩这些。
        /// </summary>
        private static IEnumerable<XElement> MatchElements(XElement root, string selector)
        {
            foreach (string part in selector.Split(','))
            {
                string sel = part.Trim();
                if (sel.Length == 0)
                {
                    continue;
                }

                foreach (XElement element in root.DescendantsAndSelf())
                {
                    if (MatchesSelector(element, root, sel))
                    {
                        yield return element;
                    }
                }
            }
        }

        private static bool MatchesSelector(XElement element, XElement root, string selector)
        {
            string[] chain = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (chain.Length == 0)
            {
                return false;
            }

            // 最后一段决定自己，前面几段只要求祖先链里出现过 ——
            // 不做严格层级校验，够用，也免得为此写个完整选择器引擎
            if (!SimpleMatches(element, root, chain[^1]))
            {
                return false;
            }

            for (int i = 0; i < chain.Length - 1; i++)
            {
                bool found = false;
                for (XElement? ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (SimpleMatches(ancestor, root, chain[i]))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>单个复合选择器（<c>g.st0#logo</c> 这种）的匹配</summary>
        private static bool SimpleMatches(XElement element, XElement root, string selector)
        {
            if (selector.Length == 0)
            {
                return false;
            }

            // 伪类除了 :root 一律不认（:hover / :nth-child… 硬解只会误伤，而且静态图标压根不触发）
            if (selector.StartsWith(':'))
            {
                return selector.Equals(":root", StringComparison.OrdinalIgnoreCase) &&
                       ReferenceEquals(element, root);
            }

            string tag = string.Empty;
            string? id = null;
            List<string> classes = new();
            System.Text.StringBuilder token = new();
            char mode = 't';   // t=标签 i=id c=class p=伪类（后面的字符全丢弃）

            void Flush()
            {
                string value = token.ToString();
                token.Clear();

                if (value.Length == 0)
                {
                    return;
                }

                switch (mode)
                {
                    case 'i': id = value; break;
                    case 'c': classes.Add(value); break;
                    case 't': tag = value; break;
                }
            }

            foreach (char ch in selector)
            {
                if (ch == '#')
                {
                    Flush();
                    mode = 'i';
                }
                else if (ch == '.')
                {
                    Flush();
                    mode = 'c';
                }
                else if (ch == ':')
                {
                    Flush();
                    mode = 'p';
                }
                else if (mode != 'p')
                {
                    token.Append(ch);
                }
            }

            Flush();

            if (id is not null && !id.Equals(element.Attribute("id")?.Value, StringComparison.Ordinal))
            {
                return false;
            }

            if (classes.Count > 0)
            {
                string[] own = (element.Attribute("class")?.Value ?? string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                foreach (string cls in classes)
                {
                    if (!own.Contains(cls, StringComparer.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return tag.Length == 0 ||
                   tag.Equals("*", StringComparison.Ordinal) ||
                   tag.Equals(element.Name.LocalName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 当前该用深色还是浅色的图标？给 C# 侧兜底挑 @media 分支用。
        /// </summary>
        private bool PreferDarkIcon()
        {
            try
            {
                Microsoft.UI.Xaml.ElementTheme theme = ActualTheme;
                if (theme == Microsoft.UI.Xaml.ElementTheme.Default)
                {
                    theme = Microsoft.UI.Xaml.Application.Current.RequestedTheme == Microsoft.UI.Xaml.ApplicationTheme.Dark
                        ? Microsoft.UI.Xaml.ElementTheme.Dark
                        : Microsoft.UI.Xaml.ElementTheme.Light;
                }

                return theme == Microsoft.UI.Xaml.ElementTheme.Dark;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>① 归一化：Chromium 求样式 → 写回属性 → 删掉 &lt;style&gt; → 序列化回来</summary>
        private static async Task<byte[]?> TryNormalizeSvgAsync(CoreWebView2 core, byte[] svgBytes)
        {
            string text = Encoding.UTF8.GetString(svgBytes);

            // ⚠️ 必须换成带引号的字面量（见 ToJsStringLiteral 的说明）：
            //    JsonEncodedText.ToString() 不带引号，拼出来是语法错误的脚本，
            //    而 WebView2 对脚本解析失败只静默返回 null —— 这条路径曾经白死过好几轮。
            string script = SvgNormalizeScript.Replace(
                "__SVG_TEXT__", WebAppIconRasterizer.ToJsStringLiteral(text), StringComparison.Ordinal);

            string? svg = DecodeScriptString(await core.ExecuteScriptAsync(script));
            if (string.IsNullOrEmpty(svg))
            {
                return null;
            }

            // 脚本把自己抛的异常装在 'ERR|名字|消息' 里带回来了，直接摊开看
            if (svg.StartsWith("ERR|", StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 归一化脚本抛异常: {svg}");
                return null;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(svg);
            return bytes.Length is > 0 and <= MaxFaviconBytes ? bytes : null;
        }

        /// <summary>
        /// ② 光栅化：Chromium 把 SVG 画成 PNG，以 data: URL 的形式交回来。
        ///
        /// 画是异步的，而 ExecuteScriptAsync 不等 Promise —— 所以拆成「点火 + 轮询」两步，
        /// 中间隔 <see cref="RasterPollIntervalMs"/> 读一次槽位，画完（或超时）就收。
        /// </summary>
        private static async Task<byte[]?> TryRasterizeSvgAsync(
            CoreWebView2 core,
            byte[] svgBytes,
            CancellationToken token)
        {
            try
            {
                string text = Encoding.UTF8.GetString(svgBytes);

                string script = SvgRasterizeScript
                    .Replace("__SVG_TEXT__", WebAppIconRasterizer.ToJsStringLiteral(text), StringComparison.Ordinal)
                    .Replace("__SIZE__", SvgRasterSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

                // 点火就走，脚本同步返回 'started'；结果会写进 window.__dtSvgRaster
                await core.ExecuteScriptAsync(script);

                string? dataUrl = null;
                for (int i = 0; i < RasterPollCount; i++)
                {
                    await Task.Delay(RasterPollIntervalMs, token);

                    (bool done, string? value, string? error) =
                        ReadRasterSlot(await core.ExecuteScriptAsync(SvgRasterizePollScript));
                    if (done)
                    {
                        dataUrl = value;
                        if (string.IsNullOrEmpty(dataUrl))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Favicon] SVG 光栅化失败原因: {error}（轮询 {i + 1} 次）");
                        }

                        break;
                    }
                }

                await core.ExecuteScriptAsync(SvgRasterizeCleanupScript);

                if (string.IsNullOrEmpty(dataUrl))
                {
                    return null;
                }

                int comma = dataUrl.IndexOf(',');
                if (comma < 0 || !dataUrl[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                return bytes.Length is > 0 and <= MaxFaviconBytes ? bytes : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读光栅化槽位。脚本给的是 <c>{"done":bool,"value":string,"err":string}</c>。
        /// done 为 false 是「还没画完」（那就再等一轮），true 才代表这轮是最终结果 ——
        /// 两者不能只靠 value 空不空来分，否则「画完了但失败」会被当成「还没画完」干等到超时。
        /// </summary>
        private static (bool Done, string? Value, string? Error) ReadRasterSlot(string raw)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);

                bool done = doc.RootElement.TryGetProperty("done", out JsonElement d) &&
                            d.ValueKind == JsonValueKind.True;

                string? value = doc.RootElement.TryGetProperty("value", out JsonElement v) &&
                                v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                string? error = doc.RootElement.TryGetProperty("err", out JsonElement e) &&
                                e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;

                return (done, value, error);
            }
            catch
            {
                return (false, null, null);
            }
        }

        /// <summary>
        /// 解 ExecuteScript 的返回值。它给的是「被 JSON 编码了一次的字符串」，要先解一层。
        /// 用 JsonDocument 而不是 JsonSerializer.Deserialize&lt;string&gt;：后者带
        /// RequiresDynamicCode/RequiresUnreferencedCode，在 AOT 下会告警。
        /// </summary>
        private static string? DecodeScriptString(string raw)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解出图标的「可比尺寸」：位图取宽高的较小值，SVG 返回 <see cref="VectorIconSize"/>。
        /// 解不了的返回 0。
        /// </summary>
        private static async Task<int> GetIconSizeAsync(byte[] bytes)
        {
            // SVG 用 BitmapDecoder 解不了，但它不是坏图 —— 按矢量尺寸参与比较
            if (WebAppIconCache.IsSvgContent(bytes))
            {
                return VectorIconSize;
            }

            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                return (int)Math.Min(decoder.PixelWidth, decoder.PixelHeight);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>色块包图标的采样边长。缩到这么小再判，512px 原图也不用心疼</summary>
        private const int BlockProbeSample = 16;

        /// <summary>「整张图都看不见」的 alpha 上限：最高 alpha 都低于它就是一张全透明的空图</summary>
        private const int BlankAlphaThreshold = 16;

        /// <summary>纯色判定的容差（RGBA 各通道）。可见像素全在容差内 ⇒ 一块纯色，等于没画东西</summary>
        private const int BlankColorTolerance = 8;

        /// <summary>
        /// SVG 里 Direct2D 认的绘图元素。取自微软《SVG support》那份支持列表 ——
        /// 表外的元素一律被忽略，所以只有这些算「画得出来」的东西。
        /// </summary>
        private static readonly string[] SvgShapeTags =
        {
            "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "use", "image"
        };

        /// <summary>
        /// 能决定「这东西有没有颜色」的两个属性。SVG 里没颜色的图形 = 画不出来。
        /// </summary>
        private static readonly string[] SvgPaintAttributes = { "fill", "stroke" };

        /// <summary>
        /// 判断一张图是不是「手机 / PWA 那种色块包图标」：
        /// 尺寸够大（≥128）＋ 四角是同一块不透明底色 ＋ 底色跟中心主体颜色差得明显。
        ///
        /// 为什么要判：这批图标是给 iOS 主屏 / Android 桌面用的，整块背景色 + 四周留白，
        /// 缩到侧边栏 20px 时主体被 padding 挤得只剩一点点，比原 favicon 还糊。
        /// 判出来的存量图标允许被更小、但主体填满的经典 favicon 覆盖 ——
        /// 这样不用用户手动清，重新访问一次站点就自动纠正回来了。
        /// </summary>
        private static async Task<bool> IsPaddedBlockIconAsync(byte[] bytes)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                // 小图不存在「色块包」这回事，直接放行，也省一次解码
                if (decoder.PixelWidth < 128 || decoder.PixelHeight < 128)
                {
                    return false;
                }

                var transform = new BitmapTransform
                {
                    ScaledWidth = BlockProbeSample,
                    ScaledHeight = BlockProbeSample,
                    InterpolationMode = BitmapInterpolationMode.Cubic
                };

                PixelDataProvider data = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = data.DetachPixelData();
                if (pixels.Length < BlockProbeSample * BlockProbeSample * 4)
                {
                    return false;
                }

                int last = BlockProbeSample - 1;
                var corners = new[]
                {
                    ReadSamplePixel(pixels, 0, 0),
                    ReadSamplePixel(pixels, last, 0),
                    ReadSamplePixel(pixels, 0, last),
                    ReadSamplePixel(pixels, last, last)
                };

                // 四角只要有一处是透明的，说明是镂空 / 圆形图标，那不是色块包
                foreach ((int _, int _, int _, int alpha) in corners)
                {
                    if (alpha < 245)
                    {
                        return false;
                    }
                }

                // 四角得是同一块底色
                for (int i = 1; i < corners.Length; i++)
                {
                    if (!AreClose(corners[0], corners[i], 16))
                    {
                        return false;
                    }
                }

                // 底色跟中心主体要差得明显，否则这就是一张正常的实心图标（比如 GitHub 那种）
                return ColorDistance(corners[0], AverageCenter(pixels)) > 40;
            }
            catch
            {
                return false; // 判不出来就当不是，宁可留着旧图也别乱换
            }
        }

        /// <summary>
        /// 判断一张图标是不是「空内容」—— 能解码，但画出来什么都没有。
        ///
        /// 为什么要这道闸：能解码 ≠ 有画面。真实案例是 ChatGPT 的 favicon.svg：
        /// 根元素写 <c>fill="none"</c>，唯一的 path 不带 fill，颜色全靠
        /// <c>&lt;style&gt;:root { fill:#000 }&lt;/style&gt;</c> 给。
        /// 而 SvgImageSource 底下是 Direct2D，微软《SVG support》里列的
        /// 支持元素表根本没有 &lt;style&gt;（"表外元素一律被忽略"）——
        /// CSS 整段失效，path 继承 fill:none，渲染结果就是一张纯空白图。
        /// 它还是矢量（我们按 256px 参与排序），一命中就提前收工，
        /// 后面真正能看的 48x48 位图连试都没机会。
        ///
        /// 判法分两路：
        ///   · SVG  —— 文本层面看「有图形元素」且「有真实上色来源」；
        ///   · 位图 —— 采样看是不是全透明 / 一整块纯色。
        /// 判不出来一律当「不是空的」：拦错会让站点丢图标，代价远大于留一张空图。
        /// </summary>
        private static async Task<bool> IsBlankIconAsync(byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return true;
            }

            try
            {
                return WebAppIconCache.IsSvgContent(bytes)
                    ? !SvgHasPaint(bytes)
                    : await IsBlankBitmapAsync(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 空内容判定失败，按非空处理: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// SVG 有没有「画得出来的内容」：既要有图形元素，也要有真实的上色来源。
        ///
        /// ⚠️ 关键一步是先挖掉 <c>&lt;style&gt;</c> 块：Direct2D 不认它，
        ///    但里面写的 <c>fill: #000</c> 在纯文本扫描看来是「有颜色」的 ——
        ///    不挖掉就正好把 ChatGPT 这种图放行，等于没判。
        /// </summary>
        private static bool SvgHasPaint(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);

            // ⚠️ <symbol> 是 SVG sprite 的写法，不在 Direct2D 支持表内 —— 它和它里面画的
            //    东西一起被忽略，靠 <use xlink:href="#id"> 引用也救不回来（use 指向的就是 symbol）。
            //    这类图标渲染结果是整张空白，直接判不可靠。
            if (text.Contains("<symbol", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool hasShape = false;
            foreach (string tag in SvgShapeTags)
            {
                // "<path …" / "<path>" / "<path/>" 都以 "<path" 开头，一次覆盖
                if (text.Contains("<" + tag, StringComparison.OrdinalIgnoreCase))
                {
                    hasShape = true;
                    break;
                }
            }

            if (!hasShape)
            {
                return false;
            }

            string body = StripStyleBlocks(text);

            if (HasPaintColor(body))
            {
                return true;
            }

            // ⭐ 一个上色声明都没有 ≠ 空白：SVG 规范里 fill 的初始值就是 black。
            //    实测 rust-lang 的 favicon 浅色分支只写了 fill-rule，颜色全靠默认值 ——
            //    判成空白就白白降级成小图了。
            //    真正的空白是「写了声明但值全是 none / currentColor / var()」
            //    （ChatGPT 那张就是根上写死 fill="none"）。
            return !HasPaintDeclaration(body);
        }

        /// <summary>
        /// 有没有出现过 fill / stroke 的赋值（不管值是什么）。
        ///
        /// 只用来区分「压根没写颜色」和「写了但值画不出来」这两种情况。
        /// 边界写死成 <c>属性名 + 空白* + (= 或 :)</c>，这样 <c>fill-rule</c> /
        /// <c>stroke-width</c> 这类同前缀属性不会被误算成上色声明。
        /// </summary>
        private static bool HasPaintDeclaration(string text)
        {
            foreach (string attribute in SvgPaintAttributes)
            {
                if (Regex.IsMatch(
                        text,
                        $@"(?:^|[\s;""']){Regex.Escape(attribute)}\s*(?:=|:)",
                        RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>挖掉所有 <c>&lt;style&gt;…&lt;/style&gt;</c>：里面的 CSS 不会被执行，不能算数</summary>
        private static string StripStyleBlocks(string text)
        {
            var kept = new StringBuilder(text.Length);
            int cursor = 0;

            while (cursor < text.Length)
            {
                int open = text.IndexOf("<style", cursor, StringComparison.OrdinalIgnoreCase);
                if (open < 0)
                {
                    kept.Append(text, cursor, text.Length - cursor);
                    break;
                }

                kept.Append(text, cursor, open - cursor);

                int openEnd = text.IndexOf('>', open);
                int close = openEnd < 0 ? -1 : text.IndexOf("</style", openEnd, StringComparison.OrdinalIgnoreCase);
                int closeEnd = close < 0 ? -1 : text.IndexOf('>', close);

                if (closeEnd < 0)
                {
                    break; // 没闭合，后面整段都不敢信，直接丢掉
                }

                cursor = closeEnd + 1;
            }

            return kept.ToString();
        }

        /// <summary>
        /// 扫描 fill / stroke 的每一处赋值，找有没有一个真实颜色。
        /// 顺带覆盖 <c>style="fill:#000"</c> 这种内联写法 ——
        /// style【属性】在 Direct2D 支持列表里，跟 &lt;style&gt;【元素】不是一回事。
        /// </summary>
        private static bool HasPaintColor(string text)
        {
            foreach (string attribute in SvgPaintAttributes)
            {
                int index = 0;

                while ((index = text.IndexOf(attribute, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int valueStart = SkipAssignment(text, index + attribute.Length);
                    index += attribute.Length;

                    if (valueStart < 0)
                    {
                        continue;
                    }

                    if (IsPaintColor(ReadToken(text, valueStart)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 跳过 <c>fill</c> 后面的 '=' / ':' 和空白、引号，返回值的起始下标。
        /// 不是一次赋值（比如 <c>fill-rule</c> 的 '-'）就返回 -1。
        /// </summary>
        private static int SkipAssignment(string text, int index)
        {
            int i = index;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || (text[i] != '=' && text[i] != ':'))
            {
                return -1;
            }

            i++;

            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '"' || text[i] == '\''))
            {
                i++;
            }

            return i < text.Length ? i : -1;
        }

        /// <summary>读一个属性值：到引号 / 分号 / 尖括号 / 空白为止</summary>
        private static string ReadToken(string text, int start)
        {
            int i = start;

            while (i < text.Length &&
                   text[i] != '"' && text[i] != '\'' && text[i] != ';' && text[i] != '>' &&
                   !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return text[start..i];
        }

        /// <summary>
        /// 这个值能不能真的把图形画出来。
        /// none / currentColor / inherit / transparent 都是「没有颜色」：
        /// currentColor 要靠宿主 CSS 的 color，在 SvgImageSource 里没有这回事。
        /// </summary>
        /// <summary>这些值都等于「没有颜色」。currentColor 要靠宿主 CSS 的 color，这里没有那回事</summary>
        private static readonly string[] NonPaintValues = { "none", "currentColor", "inherit", "transparent" };

        private static bool IsPaintColor(string value)
        {
            value = value.Trim();

            if (value.Length == 0)
            {
                return false;
            }

            foreach (string noPaint in NonPaintValues)
            {
                if (value.Equals(noPaint, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // CSS 变量：var(--icon-color) 得有样式引擎才求得出来，这里没有 —— 等于没颜色
            return !value.StartsWith("var(", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 位图版空内容判定：采样后看是不是「全透明」或「一整块纯色」。
        ///
        /// 全透明是典型空图（1x1 透明像素那种）；
        /// 纯色判的是「画了等于没画」—— 缩到侧边栏 20px 就是一块色，跟空图没区别。
        /// ⚠️ 纯色判定必须带上 alpha 通道一起比：否则一张白 logo + 透明底
        ///    （RGB 全是白）会被误判成纯色块，白白丢掉正常图标。
        /// </summary>
        private static async Task<bool> IsBlankBitmapAsync(byte[] bytes)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            var transform = new BitmapTransform
            {
                ScaledWidth = BlockProbeSample,
                ScaledHeight = BlockProbeSample,
                InterpolationMode = BitmapInterpolationMode.Cubic
            };

            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = data.DetachPixelData();
            if (pixels.Length < BlockProbeSample * BlockProbeSample * 4)
            {
                return false;
            }

            int maxAlpha = 0;
            bool monochrome = true;
            var reference = ReadSamplePixel(pixels, 0, 0);

            for (int y = 0; y < BlockProbeSample; y++)
            {
                for (int x = 0; x < BlockProbeSample; x++)
                {
                    (int r, int g, int b, int a) = ReadSamplePixel(pixels, x, y);

                    if (a > maxAlpha)
                    {
                        maxAlpha = a;
                    }

                    // RGBA 一起比：镂空图标靠 alpha 差把「纯色」这条否定掉
                    if (Math.Abs(r - reference.R) > BlankColorTolerance ||
                        Math.Abs(g - reference.G) > BlankColorTolerance ||
                        Math.Abs(b - reference.B) > BlankColorTolerance ||
                        Math.Abs(a - reference.A) > BlankColorTolerance)
                    {
                        monochrome = false;
                    }
                }
            }

            // 整张图最高 alpha 都这么低 ⇒ 肉眼根本看不见
            if (maxAlpha < BlankAlphaThreshold)
            {
                return true;
            }

            return monochrome;
        }

        /// <summary>读采样图里一个像素。BGRA 排列</summary>
        private static (int R, int G, int B, int A) ReadSamplePixel(byte[] pixels, int x, int y)
        {
            int i = (y * BlockProbeSample + x) * 4;
            return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
        }

        private static bool AreClose((int R, int G, int B, int A) a, (int R, int G, int B, int A) b, int tolerance) =>
            Math.Abs(a.R - b.R) <= tolerance &&
            Math.Abs(a.G - b.G) <= tolerance &&
            Math.Abs(a.B - b.B) <= tolerance;

        private static int ColorDistance((int R, int G, int B, int A) a, (int R, int G, int B, int A) b) =>
            (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3;

        private static (int R, int G, int B, int A) AverageCenter(byte[] pixels)
        {
            int mid = BlockProbeSample / 2;

            var a = ReadSamplePixel(pixels, mid - 1, mid - 1);
            var b = ReadSamplePixel(pixels, mid, mid - 1);
            var c = ReadSamplePixel(pixels, mid - 1, mid);
            var d = ReadSamplePixel(pixels, mid, mid);

            return (
                (a.R + b.R + c.R + d.R) / 4,
                (a.G + b.G + c.G + d.G) / 4,
                (a.B + b.B + c.B + d.B) / 4,
                255);
        }

        private static byte[]? TryDecodeDataUri(string href)
        {
            if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int comma = href.IndexOf(',');
            if (comma < 0 || !href[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(href[(comma + 1)..]);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从 sizes 属性估尺寸；没写 sizes 时按类型兜底（SVG 按矢量算、apple-touch 惯例 180、其余 16）。
        /// "any"（矢量图常用的写法）也按 <see cref="VectorIconSize"/> 算。
        /// </summary>
        private static int EstimateIconSize(string sizes, string rel, string href, string type)
        {
            if (!string.IsNullOrWhiteSpace(sizes))
            {
                int best = 0;
                foreach (string part in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] wh = part.Split('x', 'X');
                    if (wh.Length == 2 && int.TryParse(wh[0], out int w) && w > best)
                    {
                        best = w;
                    }
                }

                if (best > 0)
                {
                    return best;
                }

                // "any" 在 HTML 规范里是给「可缩放」的资源（也就是矢量）用的。
                // 位图标 any 是站点偷懒（ChatGPT 的 /favicon.ico 就写 sizes="any"，
                // 实际第一帧 16x16）—— 抬到 256 会让它排在所有真大图前面白跑一次下载，
                // 这里按未知尺寸处理，交给后面解码出来的真实尺寸说话。
                if (sizes.Contains("any", StringComparison.OrdinalIgnoreCase))
                {
                    return IsSvg(href, type) ? VectorIconSize : 16;
                }
            }

            // SVG 是矢量，渲染时想多大就多大 —— 排在所有位图前面
            if (IsSvg(href, type))
            {
                return VectorIconSize;
            }

            return rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ? 180 : 16;
        }

        private static bool IsSvg(string href, string type) =>
            type.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
            href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        private static string ReadString(JsonElement item, string name) =>
            item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>DOM 里一个图标声明</summary>
        private readonly record struct IconCandidate(string Href, int Size, string Rel, int Priority);

        /// <summary>
        /// ① 按 FaviconUri 自己下原件。
        /// 拿到不等于能用：SVG 虽然 content-type 是 image/*，但 BitmapDecoder 解不了，
        /// 所以最后必须过一遍解码验证 —— 过不了就返回 null，让调用方走 16x16 兜底。
        /// </summary>
        private static async Task<byte[]?> DownloadOriginalIconAsync(string uri, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            {
                return null;
            }

            try
            {
                // ⚠️ 必须是 ResponseHeadersRead：默认的 ResponseContentRead 会把整个响应体
                //    缓冲进内存之后才返回，站点返回一个几百 MB 的"图标"我们就先吃几百 MB 内存了。
                //    改成拿到头就返回，边读边数，超上限立刻撒手。
                using HttpResponseMessage response = await FaviconHttpClient.GetAsync(
                    parsed, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string? contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(contentType) &&
                    !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // 站点自己声明的长度先挡一道（可信度不高，所以下面还是边读边数）
                long? declared = response.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > MaxFaviconBytes)
                {
                    return null;
                }

                byte[] bytes = await ReadWithLimitAsync(response, cancellationToken);
                if (bytes.Length == 0)
                {
                    return null;
                }

                return await CanDecodeIconAsync(bytes) ? bytes : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 原件下载失败: {uri}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 边读边数地把响应体读进来，超过 <see cref="MaxFaviconBytes"/> 立刻返回空。
        /// 站点给的 Content-Length 不作数，上限得自己卡住才算数。
        /// </summary>
        private static async Task<byte[]> ReadWithLimitAsync(HttpResponseMessage response, CancellationToken token)
        {
            using Stream source = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream(capacity: 64 * 1024);

            byte[] chunk = new byte[81920];
            int read;

            while ((read = await source.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaxFaviconBytes)
                {
                    System.Diagnostics.Debug.WriteLine("[Favicon] 图标超过体积上限，放弃");
                    return Array.Empty<byte>();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// ② 兜底：向内核要它已下载的那份（Png，实测 16x16）。
        /// 页面没图标时它也不给 null，而是给一张「未定义」图 —— 所以调用方要先判 FaviconUri。
        /// </summary>
        private static async Task<byte[]?> GetKernelFaviconAsync(CoreWebView2 core)
        {
            try
            {
                using IRandomAccessStream? stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is null)
                {
                    return null;
                }

                // WinRT 里 IRandomAccessStream.Size 是 ulong，跟 int 常量比要先对齐类型
                ulong size = stream.Size;
                if (size == 0 || size > (ulong)MaxFaviconBytes)
                {
                    return null;
                }

                var buffer = new Windows.Storage.Streams.Buffer((uint)size);
                await stream.ReadAsync(buffer, (uint)size, InputStreamOptions.None);

                byte[] bytes = buffer.ToArray();
                return bytes.Length == 0 ? null : bytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] 内核图标读取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ③ 向 Edge 的 Favicons 库要图标（按 URL 精确匹配，失败再按域名兜底）。
        /// 只在本机装了 Edge 且库可读时有效；Edge 正在跑会 SQLITE_BUSY，直接跳过。
        /// </summary>
        private static byte[]? TryGetEdgeFavicon(string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(pageUrl))
            {
                return null;
            }

            try
            {
                if (!EdgeFaviconReader.IsFaviconsDbAvailable())
                {
                    return null;
                }

                using var reader = new EdgeFaviconReader();
                byte[]? bytes = reader.GetFaviconForUrl(pageUrl) ?? reader.GetFaviconByDomain(pageUrl);

                if (bytes is { Length: > 0 })
                {
                    System.Diagnostics.Debug.WriteLine($"[Favicon] Edge 缓存命中 {bytes.Length} 字节: {pageUrl}");
                }

                return bytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Favicon] Edge 图标读取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 当前文档和快捷方式是否同一个站。只看 Host，忽略 www. 前缀和端口之外的差异。
        /// 用在「外站图标不许覆盖应用图标」这道闸门上。
        /// </summary>
        /// <summary>
        /// 放宽版的同站判定：快捷方式自己的域，加上【初始导航重定向终点】那一个域。
        ///
        /// 重定向终点只在「这个快捷方式第一次走到这儿」时认定一次，之后锁死 ——
        /// 用户点进别的站，host 对不上就被当成外站拦住。
        /// 换快捷方式（页面实例复用）则重新认定。
        /// </summary>
        private bool IsTrustedSite(string documentUrl, WebAppShortcut shortcut)
        {
            if (IsSameSite(documentUrl, shortcut.Url))
            {
                return true;
            }

            string? host = TryGetHost(documentUrl);
            if (host is null)
            {
                return false;
            }

            // ⭐ 再放宽到「同一个注册域」（eTLD+1）：同属一家运营方的多个子域要算自己人。
            //    实测踩到：快捷方式是 yiyan.baidu.com，跳转链 yiyan → wenxin → www，
            //    三步全是 baidu.com 的子域，但逐 host 比全对不上 ⇒ 被判成外站 ⇒
            //    图标一次都抓不到，条目永远空着。
            //    这是静态判定（跟当前跳到哪儿无关），所以不像上面那条「只认一次」需要设防：
            //    用户真点去别的站点，注册域不一样照样拦。
            string? shortcutHost = TryGetHost(shortcut.Url);
            if (shortcutHost is not null &&
                string.Equals(
                    GetRegistrableDomain(host),
                    GetRegistrableDomain(shortcutHost),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 换人了：清掉上一任的信任域，重新认定
            if (!string.Equals(_trustedHostOwnerId, shortcut.Id, StringComparison.Ordinal))
            {
                _trustedHostOwnerId = shortcut.Id;
                _trustedDocumentHost = host;
                System.Diagnostics.Debug.WriteLine($"[Favicon] 记录重定向终点域: {host}（快捷方式 {shortcut.Url}）");
                return true;
            }

            return string.Equals(_trustedDocumentHost, host, StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryGetHost(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? NormalizeHost(uri.Host) : null;

        private static bool IsSameSite(string documentUrl, string shortcutUrl)
        {
            if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out Uri? document) ||
                !Uri.TryCreate(shortcutUrl, UriKind.Absolute, out Uri? shortcut))
            {
                return false;
            }

            return string.Equals(
                NormalizeHost(document.Host),
                NormalizeHost(shortcut.Host),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeHost(string host) =>
            host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

        /// <summary>
        /// 常见「两段式后缀」。取注册域时不能一律只留最后两段 ——
        /// 那样 <c>baidu.com.cn</c> 会被算成 <c>com.cn</c>，同站判定就全乱了。
        /// 只列实际会碰到的那些，上完整 PSL 对这儿来说得不偿失。
        /// </summary>
        private static readonly string[] MultiPartSuffixes =
        {
            "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn",
            "co.jp", "or.jp", "ne.jp", "ac.jp",
            "co.uk", "org.uk", "ac.uk", "gov.uk",
            "com.au", "net.au", "org.au",
            "co.kr", "com.hk", "com.tw", "com.br", "com.mx"
        };

        /// <summary>取注册域（eTLD+1）：<c>wenxin.baidu.com</c> → <c>baidu.com</c></summary>
        private static string GetRegistrableDomain(string host)
        {
            string[] labels = host.TrimEnd('.').Split('.');
            if (labels.Length <= 2)
            {
                return string.Join('.', labels);
            }

            string lastTwo = labels[^2] + "." + labels[^1];
            int take = Array.IndexOf(MultiPartSuffixes, lastTwo) >= 0 ? 3 : 2;

            return string.Join('.', labels[^take..]);
        }

        /// <summary>
        /// 验证字节是不是「能被渲染的图标」：位图要求 BitmapDecoder 能解，SVG 单独放行。
        /// 损坏文件会在这里被挡下。
        /// </summary>
        private static async Task<bool> CanDecodeIconAsync(byte[] bytes)
        {
            if (WebAppIconCache.IsSvgContent(bytes))
            {
                return true;
            }

            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                _ = await BitmapDecoder.CreateAsync(stream);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CacheFavicon(string faviconUri, byte[] bytes)
        {
            lock (FaviconCacheLock)
            {
                if (FaviconCache.Count >= FaviconCacheLimit)
                {
                    FaviconCache.Clear();
                }

                FaviconCache[faviconUri] = bytes;
            }
        }
    }
}
