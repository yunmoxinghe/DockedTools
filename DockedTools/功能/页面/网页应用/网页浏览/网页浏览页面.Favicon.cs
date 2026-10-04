using DockedTools.Features.Pages.WebApp.EdgeSync;
using DockedTools.Features.Pages.WebApp.Shared;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
                if (_currentShortcut is not null && !IsSameSite(documentUrl, _currentShortcut.Url))
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

                if (cachedHit is not null)
                {
                    await ApplyFaviconAsync(cachedHit);
                    return;
                }

                // 同一个「文档 + favicon」只认真试一次，重复通知直接略过
                string attemptKey = documentUrl + "|" + faviconUri;
                if (string.Equals(_lastFaviconAttemptKey, attemptKey, StringComparison.Ordinal))
                {
                    return;
                }

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

                byte[]? bytes = null;
                try
                {
                    // ① 高清优先：扫 DOM 的 link[rel*=icon] + site.webmanifest，
                    //    按声明尺寸从大到小试。FaviconUri 往往只指向 16x16 那份，
                    //    站点真正清晰的大图（180 / 192 / 512）都在这些声明里。
                    bytes = await TryFetchHighResIconAsync(core, cts.Token);

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

                if (cts.IsCancellationRequested || !ReferenceEquals(_faviconCts, cts))
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

                // ⚠️ 这里是严格「更小」才挡：站点换了张同尺寸的新图标是要生效的，
                //    写成 >= 的话同尺寸换图永远进不来。内容没变的情况由 ApplyFaviconAsync 挡。
                if (oldSize > 0 && oldSize > newSize && !existingIsPaddedBlock)
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
        private static async Task<byte[]?> TryFetchHighResIconAsync(CoreWebView2 core, CancellationToken token)
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
                candidates.Where(c => c.Priority == IconPriorityPlatform),
                MaxPlatformIconCandidates,
                token);

            return platformSize > bestSize ? platformBest : best;
        }

        /// <summary>按声明尺寸从大到小试一组候选，返回其中最大的那份。够大就提前收工</summary>
        private static async Task<(byte[]? Bytes, int Size)> TryFetchCandidateGroupAsync(
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

                (byte[]? bytes, int size) = await TryFetchCandidateAsync(candidate, token);
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
            return (size > 0 ? bytes : null, size);
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

                if (sizes.Contains("any", StringComparison.OrdinalIgnoreCase))
                {
                    return VectorIconSize;
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
