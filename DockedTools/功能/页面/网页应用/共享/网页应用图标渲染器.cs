using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 站点图标渲染器 —— 一个专用的、进程内共享的隐藏 WebView2，把 SVG 光栅化成 PNG。
    ///
    /// 为什么需要它（参照 Chromium 自己的做法）：
    /// Chromium 处理 favicon 时从不把矢量交给宿主 —— 它由 renderer 侧 <c>WebLocalFrame::DownloadImage</c>
    /// 下载，用 Blink 的图片解码器渲染（SVG 走 SVGImage，完整支持 CSS &lt;style&gt; 和
    /// <c>@media (prefers-color-scheme)</c>），最后光栅化成 <c>SkBitmap</c>；
    /// 交给宿主（也就是 WebView2 的 <c>GetFaviconAsync</c>）时已经是位图。
    /// 我们之前反过来做：自己拿 SVG 文本去猜 CSS、自己补颜色，所以补丁越打越多。
    /// 这个服务就是把「让 Chromium 画」这一步独立出来 —— 输入任何 SVG，输出 PNG。
    ///
    /// 为什么不在宿主页面里跑（这是关键）：
    /// 之前把光栅化脚本跑在页面自己的 window 上，槽位 <c>window.__dtSvgRaster</c> 会被
    /// SPA 导航直接带走（ChatGPT 那次就是轮询 40 次全是 done:false，干等到超时）。
    /// 这里的文档是我们自己 NavigateToString 出来的，永不导航，槽位不会丢。
    ///
    /// 失败策略：任何一步出错都返回 null，调用方退回自己那套逻辑 ——
    /// 这个服务是「更好的路」，不是「唯一的路」，它挂了不会让图标变差。
    /// </summary>
    internal sealed class WebAppIconRasterizer
    {
        /// <summary>进程内唯一实例。常驻，不销毁（每次重建要几百毫秒）</summary>
        public static WebAppIconRasterizer Instance { get; } = new();

        /// <summary>
        /// 默认输出边长。跟 favicon 那条链路里给矢量的虚拟尺寸 <c>VectorIconSize</c> 对齐 ——
        /// 光栅化产物是位图，尺寸按真实像素算，给小了会在防退化比较里输给旧的那张矢量图。
        /// </summary>
        public const int DefaultRasterSize = 256;

        /// <summary>
        /// 渲染内核的启动参数。
        /// 隐藏窗口上的 renderer 会被 Chromium 判定为「后台」而降优先级，
        /// 图片解码和定时器都可能被推迟 —— 这三条是标准的反节流开关。
        /// </summary>
        private const string RasterBrowserArguments =
            "--disable-renderer-backgrounding " +
            "--disable-backgrounding-occluded-windows " +
            "--disable-background-timer-throttling " +
            "--disable-renderer-priority-management";

        /// <summary>
        /// 渲染内核专属的 user data folder。
        /// 必须跟浏览器页面那个 WebView2 分开 —— 见 <see cref="EnsureReadyCoreAsync"/> 里那段说明。
        /// </summary>
        private const string UserDataFolderName = "EBWebView-IconRaster";

        /// <summary>
        /// 把一个字符串变成 **带引号的 JS 字符串字面量**。
        ///
        /// ⚠️ 千万别用 <c>JsonEncodedText.Encode(text).ToString()</c> 顶替 —— 它出来的
        /// 是「已转义的内容」而【不带两侧引号】，拼进脚本就成了：
        /// <c>const svgText = &lt;svg xmlns=…&gt;;</c> —— 语法错误。
        /// 而 WebView2 对「脚本没能通过解析」的处理是静默返回 null（不抛异常），
        /// 于是症状就变成了「ExecuteScriptAsync 返回空」，极难察觉。
        /// 本项目里旧的归一化 / 宿主页光栅化两条路，就是死在这上面。
        ///
        /// 手写这一份而不是走 JsonSerializer.Serialize&lt;string&gt;：这是 PublishAot 工程，
        /// 反射式序列化会招来 IL2026/IL3050 警告。
        /// </summary>
        internal static string ToJsStringLiteral(string value)
        {
            StringBuilder builder = new(value.Length + 16);
            builder.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        // 控制字符、以及 JS 里的行分隔符 U+2028/U+2029 必须转义，
                        // 后者在旧引擎里会被当成换行，直接把一个字符串断成两半。
                        builder.Append(c < ' ' || c == '\u2028' || c == '\u2029'
                            ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)
                            : c.ToString());
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// 宿主文档。空白页就行 —— 图片是 data: URL，不需要任何网络请求，
        /// 也不需要 CSP（这个文档本来就是我们自己的）。
        /// </summary>
        private const string HostDocument =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>dt-icon-raster</title></head><body></body></html>";

        /// <summary>
        /// 常驻 helper。导航之前就用 <c>AddScriptToExecuteOnDocumentCreatedAsync</c> 装上，
        /// 之后每次渲染只是调用它：同步返回 'started'，真正的 PNG 由 <c>postMessage</c> 回推。
        /// 为什么不放 <c>window</c> 槽位：宿主文档一旦被换掉，槽位跟着蒸发，
        /// 轮询只能干等到超时（旧版在 ChatGPT 身上就是这么空的）。
        /// 也不能 return Promise —— ExecuteScriptAsync 不等 Promise（WebView2Feedback #416）。
        /// </summary>
        private const string RasterHelperScript =
            "window.__dtRaster = function (svgText, size) {\n" +
            "  var settled = false;\n" +
            "  // 结果一律 postMessage 回宿主：轮询 window 槽位那套在宿主忽然换文档时会白等，\n" +
            "  // 回推则不受「ExecuteScriptAsync 只拿得到同步结果」的限制。\n" +
            "  function post(v, e) {\n" +
            "    if (settled) { return; }\n" +
            "    settled = true;\n" +
            "    try { window.chrome.webview.postMessage(v ? 'ok|' + v : 'err|' + (e || 'unknown')); }\n" +
            "    catch (_) {}\n" +
            "  }\n" +
            "  try {\n" +
            "    var img = new Image();\n" +
            "    img.onload = function () {\n" +
            "      try {\n" +
            "        var nw = img.naturalWidth, nh = img.naturalHeight;\n" +
            "        if (!nw || !nh) { post('', 'no-intrinsic-size'); return; }\n" +
            "        var canvas = document.createElement('canvas');\n" +
            "        canvas.width = size; canvas.height = size;\n" +
            "        var ctx = canvas.getContext('2d');\n" +
            "        if (!ctx) { post('', 'no-2d-context'); return; }\n" +
            "        // 等比缩放并居中：favicon 基本都是正方形，但保一手，别把非正方形的拉变形\n" +
            "        var scale = Math.min(size / nw, size / nh);\n" +
            "        var w = Math.max(1, Math.round(nw * scale));\n" +
            "        var h = Math.max(1, Math.round(nh * scale));\n" +
            "        ctx.drawImage(img, (size - w) / 2, (size - h) / 2, w, h);\n" +
            "        post(canvas.toDataURL('image/png'), '');\n" +
            "      } catch (e) { post('', 'draw|' + (e && e.name ? e.name : 'Error')); }\n" +
            "    };\n" +
            "    img.onerror = function () { post('', 'img-error'); };\n" +
            "    img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgText);\n" +
            "    setTimeout(function () { post('', 'timeout'); }, 5000);\n" +
            "    return 'started';\n" +
            "  } catch (e) { post('', 'sync|' + (e && e.name ? e.name : 'Error')); return 'error'; }\n" +
            "}; 'helper-installed'";

        /// <summary>等内核回图的上限。脚本自身还有一道 5 秒自兜底，两者谁先到都行</summary>
        private const int RasterTimeoutMs = 6000;

        /// <summary>
        /// UI 线程的调度器。必须在 UI 线程上抓一次 ——
        /// 见 <see cref="EnsureReadyAsync"/> 里为什么不能靠当前线程现场取。
        /// </summary>
        private static DispatcherQueue? _uiQueue;

        /// <summary>初始化失败上限。到顶之后才彻底停用。</summary>
        private const int MaxInitAttempts = 3;

        /// <summary>0=未初始化 1=就绪 2=失败过（还能重试几次）</summary>
        private int _state;

        /// <summary>已经失败过几次</summary>
        private int _failCount;

        /// <summary>
        /// 在 UI 线程上调用一次，把 UI 线程的调度器交给本服务。
        ///
        /// 为什么非要这一步：取 favicon 的整条链前面都是 HttpClient / WebView2 的异步操作，
        /// 跑到这儿时上下文早就是线程池线程了。而 <c>new Window()</c> 只能建在 UI 线程上
        /// （线程池上建会直接抛 "The application called an interface that was marshalled
        /// for a different thread" 一类的 COM 异常）—— 抓不到主队列就只能眼看着内核建不起来。
        /// 幂等，随便调几次都行。
        /// </summary>
        public static void CaptureUiThread()
        {
            _uiQueue ??= DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>
        /// 宿主窗口。必须一直持有引用 —— 它是渲染内核的父窗口，
        /// 被 GC 掉的话内核也就没了。全程不 Activate，所以它永远不显示。
        /// </summary>
        private Window? _hostWindow;

        private CoreWebView2Controller? _controller;

        private CoreWebView2? _core;

        /// <summary>内核回图的等待者。同一时刻只可能有一个（<see cref="_gate"/> 保证）</summary>
        private TaskCompletionSource<byte[]?>? _pendingRaster;

        /// <summary>同一时刻只渲染一张 —— 内核只有一个</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// 把 SVG 光栅化成 PNG。画不出来返回 null。
        /// </summary>
        /// <param name="svgBytes">SVG 原文</param>
        /// <param name="size">输出边长（px）</param>
        /// <param name="preferDark">
        /// 按哪个主题求值。Chromium 渲染 SVG 时会真的去算 <c>@media (prefers-color-scheme)</c>，
        /// 所以只要把这个值告诉内核，单色图标的明暗就自动对了 —— 不用我们再拆 CSS。
        /// </param>
        public async Task<byte[]?> RasterizeSvgAsync(byte[] svgBytes, int size, bool preferDark)
        {
            if (svgBytes is null || svgBytes.Length == 0)
            {
                return null;
            }

            // ⭐ 同一份 SVG + 同尺寸 + 同主题，画一次就够。
            //    实测同一张图被画了两遍：顶栏发布一次（契约只认位图 Uri），
            //    主页卡片 / 侧边栏的重画又一次，每次几百毫秒纯属白烧。
            string cacheKey = BuildCacheKey(svgBytes, size, preferDark);
            if (_resultCache.TryGetValue(cacheKey, out byte[]? hit))
            {
                return hit;
            }

            await _gate.WaitAsync();
            try
            {
                // 排队期间可能已经被别人画好了（并发打同一张图），再查一次
                if (_resultCache.TryGetValue(cacheKey, out byte[]? raced))
                {
                    return raced;
                }

                if (!await EnsureReadyAsync())
                {
                    return null;
                }

                // 主题要在点火前设好：SVG 里的 @media 是解码那一刻求值的
                ApplyPreferredColourScheme(preferDark);

                byte[]? png = await RenderOnceAsync(svgBytes, size);
                if (png is { Length: > 0 })
                {
                    // 失败不入缓存：那多半是内核当时还没热好，记进去会把它永久钉死成「画不出来」
                    _resultCache[cacheKey] = png;
                    TrimResultCache();
                }

                return png;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 渲染异常: {ex.Message}");
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>渲染结果的记忆化缓存：同一份 SVG 只画一次</summary>
        private readonly Dictionary<string, byte[]> _resultCache = new(StringComparer.Ordinal);

        /// <summary>
        /// 缓存键 = 内容哈希 + 尺寸 + 主题。
        /// 用哈希而不是存原文当 key：SVG 动辄几 KB，字典里多躺几十份没意义。
        /// </summary>
        private static string BuildCacheKey(byte[] svgBytes, int size, bool preferDark) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(svgBytes)) +
            "|" + size.ToString(CultureInfo.InvariantCulture) +
            "|" + (preferDark ? "d" : "l");

        /// <summary>缓存上限。图标就那么几十个，超了整表清掉比搞 LRU 划算</summary>
        private const int MaxCachedResults = 64;

        private void TrimResultCache()
        {
            if (_resultCache.Count > MaxCachedResults)
            {
                _resultCache.Clear();
            }
        }

        /// <summary>
        /// 点火一次、等内核把 PNG 回推回来。
        /// 返回 null 表示「这张没画出来」——调用方会退回自己那套旧逻辑，不会让图标变差。
        /// </summary>
        private async Task<byte[]?> RenderOnceAsync(byte[] svgBytes, int size)
        {
            if (_core is null)
            {
                return null;
            }

            string argument = ToJsStringLiteral(Encoding.UTF8.GetString(svgBytes));

            TaskCompletionSource<byte[]?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<byte[]?>? stale = Interlocked.Exchange(ref _pendingRaster, tcs);
            stale?.TrySetResult(null);

            try
            {
                // ⭐ helper 必须在导航之前装上 —— AddScriptToExecuteOnDocumentCreatedAsync 只对
                //    注入之后才创建的文档生效。这是默认路径；万一它当时没装成（内核重建、
                //    文档被换掉），这里补一次一次性注入再点，保证这次请求还能成。
                //
                // ⚠️ 点火成功就绝不能再做体检：体检期间等待者会被临时摘走，
                //    而 helper 每张图只回推一次，撞在这个窗口里的回推会被直接丢掉 ——
                //    表现就是「点火一切正常、然后莫名其妙等到超时」。
                if (!await IgniteAsync(argument, size))
                {
                    System.Diagnostics.Debug.WriteLine("[IconRasterizer] helper 不在，补注入一次再重试");

                    string? installed = DecodeScriptString(
                        await ExecuteScriptOnUiThreadAsync(RasterHelperScript) ?? string.Empty);
                    System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 补注入返回: {installed}");

                    if (!await IgniteAsync(argument, size))
                    {
                        return null;
                    }
                }

                Task timeout = Task.Delay(RasterTimeoutMs);
                if (await Task.WhenAny(tcs.Task, timeout) == timeout)
                {
                    System.Diagnostics.Debug.WriteLine("[IconRasterizer] 等内核回图超时");
                    return null;
                }

                byte[]? png = await tcs.Task;
                if (png is null)
                {
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 光栅化成功 {png.Length} 字节");
                return png;
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingRaster, null, tcs);
            }
        }

        /// <summary>
        /// 调用 helper 让它开始画。返回 false = 脚本压根没跑起来（helper 缺失 / 抛异常）。
        /// 脚本本身自带 try/catch，异常名会被拼进返回值里 —— 不用猜。
        /// </summary>
        private async Task<bool> IgniteAsync(string encodedSvg, int size)
        {
            if (_core is null)
            {
                return false;
            }

            string script =
                "(function(){" +
                "  try {" +
                "    if (typeof window.__dtRaster !== 'function') return 'missing';" +
                "    return 'started|' + window.__dtRaster(" + encodedSvg + ", " +
                size.ToString(CultureInfo.InvariantCulture) + ");" +
                "  } catch (e) {" +
                "    return 'throw|' + (e && e.name ? e.name : 'Error') + '|' + (e && e.message ? e.message : '');" +
                "  }" +
                "})()";

            string raw = await ExecuteScriptOnUiThreadAsync(script) ?? string.Empty;
            string? result = DecodeScriptString(raw);

            if (result is null || !result.StartsWith("started|", StringComparison.Ordinal))
            {
                DebugLog(
                    $"[IconRasterizer] 点火返回 [{result ?? "<null>"}] 原文 [{raw}] 脚本长度 {script.Length}");

                await DiagnoseChannelAsync(encodedSvg);
            }

            return result is not null && result.StartsWith("started|", StringComparison.Ordinal);
        }

        /// <summary>
        /// 点火失败时的一次性体检，把「通道」「脚本体积」两个变量一次问清楚：
        /// ① ping —— 一句极短的脚本，专门验证 ExecuteScript 这条通道还活不活；
        /// ② 同尺寸探针 —— 把同样的巨大 SVG 字面量塞进去，只返回它的长度、不画任何东西，
        ///    用来区分「脚本太长 / 内容有字符问题」和「通道本身哑了」。
        /// 期间把等待者暂时摘走，免得探针的结果被误当成本次渲染的产物。
        /// </summary>
        private async Task DiagnoseChannelAsync(string encodedSvg)
        {
            TaskCompletionSource<byte[]?>? saved = Interlocked.Exchange(ref _pendingRaster, null);
            try
            {
                string? ping = await ExecuteScriptOnUiThreadAsync("'ping'");
                DebugLog($"[IconRasterizer] 体检 ping = [{ping ?? "<null>"}]");

                string? bulk = await ExecuteScriptOnUiThreadAsync(
                    "(function(){ return 'len:' + " + encodedSvg + ".length; })()");
                DebugLog($"[IconRasterizer] 体检 大脚本回执 = [{bulk ?? "<null>"}]");
            }
            catch (Exception ex)
            {
                DebugLog($"[IconRasterizer] 体检本身抛异常: {ex.Message}");
            }
            finally
            {
                if (saved is not null)
                {
                    Interlocked.CompareExchange(ref _pendingRaster, saved, null);
                }
            }
        }

        private static void DebugLog(string message) => System.Diagnostics.Debug.WriteLine(message);

        /// <summary>
        /// 内核把结果 postMessage 回来的地方。
        /// 载荷刻意做成 <c>ok|dataURL</c> / <c>err|原因</c> 这种前缀格式 —— 省掉 JSON 序列化，
        /// AOT 下也安全（这工程是 PublishAot，反射式反序列化是要躲开的）。
        /// </summary>
        private void OnRasterMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            TaskCompletionSource<byte[]?>? tcs = _pendingRaster;
            if (tcs is null)
            {
                // 来得不是时候（上一轮超时后被丢弃的结果、体检窗口里的回推）——
                // 以前这种情况是全静默的，排查时只能看到「超时」，不知道图其实画出来了。
                string head = e.TryGetWebMessageAsString();
                DebugLog($"[IconRasterizer] 回推到了但没人等，丢弃（前 60 字符）: {head[..Math.Min(60, head.Length)]}");
                return;
            }

            try
            {
                string raw = e.TryGetWebMessageAsString();
                string? payload = DecodeScriptString(raw);

                if (payload is null)
                {
                    return;
                }

                if (payload.StartsWith("ok|", StringComparison.Ordinal))
                {
                    byte[]? png = DecodePngDataUrl(payload["ok|".Length..]);
                    if (png is null)
                    {
                        System.Diagnostics.Debug.WriteLine("[IconRasterizer] 回推的不是合法 PNG data URL");
                    }

                    tcs.TrySetResult(png);
                    return;
                }

                if (payload.StartsWith("err|", StringComparison.Ordinal))
                {
                    System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 内核没画出来: {payload["err|".Length..]}");
                    tcs.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 处理回推消息失败: {ex.Message}");
                tcs.TrySetResult(null);
            }
        }

        /// <summary>懒初始化：确保内核是在 UI 线程上建起来的</summary>
        private async Task<bool> EnsureReadyAsync()
        {
            if (_state == 1)
            {
                return true;
            }

            if (_state == 2 && _failCount >= MaxInitAttempts)
            {
                return false;
            }

            // 内核的一切（宿主 Window、WebViewController）只能在 UI 线程上建，
            // 而 favicon 链路到这儿时基本已经在线程池上了 —— 切回去。
            // App.UIDispatcherQueue 是 OnLaunched 里在主线程上专门存的一份（托盘跑独立 UI 线程，
            // GetForCurrentThread() 拿到未必是主线程的），优先用它。
            DispatcherQueue? queue = _uiQueue ??= App.UIDispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

            if (queue is null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[IconRasterizer] 拿不到 UI 线程调度器（没在 UI 线程上来最高层的调用），本次放弃");
                return false;
            }

            return queue.HasThreadAccess
                ? await EnsureReadyCoreAsync()
                : await RunOnUiThreadAsync(queue, EnsureReadyCoreAsync, false);
        }

        /// <summary>
        /// 在 UI 线程上执行一段脚本。
        ///
        /// 为什么要多此一举：一次获取均未成功 —— 宿主文档自检（那时人在 UI 线程上）实实在在
        /// 拿到了结果，而 favicon 链上（线程池线程）发同样的 ExecuteScriptAsync 却只拿回 null。
        /// WebView2 的 CoreWebView2 并不保证任意线程调用都有效，
        /// 宁可多切一次线程，也不要去赌它的线程模型。
        /// </summary>
        private async Task<string?> ExecuteScriptOnUiThreadAsync(string script)
        {
            CoreWebView2? core = _core;
            if (core is null)
            {
                return null;
            }

            async Task<string?> Run() => await core.ExecuteScriptAsync(script);

            DispatcherQueue? queue = _uiQueue ?? App.UIDispatcherQueue;
            return queue is not null && !queue.HasThreadAccess
                ? await RunOnUiThreadAsync(queue, Run, null)
                : await Run();
        }

        /// <summary>把一段异步操作扔到 UI 线程上跑，并等它结束</summary>
        private static Task<T> RunOnUiThreadAsync<T>(DispatcherQueue queue, Func<Task<T>> action, T fallback)
        {
            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await action());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IconRasterizer] UI 线程上执行抛异常: {ex.Message}");
                    tcs.TrySetResult(fallback);
                }
            });

            if (!enqueued)
            {
                System.Diagnostics.Debug.WriteLine("[IconRasterizer] 入队失败（UI 线程已停止泵消息）");
                return Task.FromResult(fallback);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 找一个能让 WebView2 挂上去的宿主窗口句柄。
        ///
        /// 优先主窗口：它是用户真正看得见、早就 Activate 过的窗口，
        /// XAML 的状态对 WebView2 来说是「合法的」。
        /// 拿不到（比如主窗口还没建出来）才退而自建一个隐藏 Window 当纯容器 ——
        /// 那条路要以 controller 创建失败报错，但至少不会连累主窗口的焦点。
        /// </summary>
        private IntPtr ResolveHostWindowHandle()
        {
            try
            {
                if (Microsoft.UI.Xaml.Application.Current is App app &&
                    app.MainWindow is Window main)
                {
                    IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(main);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 取主窗口句柄失败: {ex.Message}");
            }

            _hostWindow = new Window
            {
                Content = new Microsoft.UI.Xaml.Controls.Grid()
            };

            return WinRT.Interop.WindowNative.GetWindowHandle(_hostWindow);
        }

        /// <summary>真正建内核的地方。调用方保证在 UI 线程上</summary>
        private async Task<bool> EnsureReadyCoreAsync()
        {
            if (_state == 1)
            {
                return true;
            }

            try
            {
                CoreWebView2EnvironmentOptions options = new()
                {
                    AdditionalBrowserArguments = RasterBrowserArguments
                };

                // ⭐ 专属 user data folder —— 这一行就是 0x8007139F 的解药。
                //    官方文档（ICoreWebView2Environment）写得很明确：
                //    「同一份 user data folder 上已经有一个实例在跑，而新 Environment 的
                //     EnvironmentOptions 跟它不同，CreateCoreWebView2Controller 会以
                //     HRESULT_FROM_WIN32(ERROR_INVALID_STATE) 失败」。
                //    我们前两版传的是 null（= 默认那份），跟浏览器页面里那个 WebView2 撞车了，
                //    而反节流参数正是两边 EnvironmentOptions 不同的那一项 ——
                //    于是每次都在 controller 这一步被顶回来。给个独立目录，互不干涉。
                string userDataFolder = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, UserDataFolderName);

                System.Diagnostics.Debug.WriteLine("[IconRasterizer] 建 WebView2 环境…");
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);

                // ⭐ 宿主必须是一个「已经被 XAML 真正初始化过」的窗口 —— 对着一个
                //    新建但还没激活的 Window 建 controller，内核会回 0x8007139F
                //    （ERROR_INVALID_STATE：「组或资源的状态不是执行请求操作的正确状态」），实机已复现。
                //    主窗口天然满足这个条件，而且不新建窗口就不会抢焦点 ——
                //    之前自建设 Activate 的做法会把主窗口顶到停用，触发它自动收起。
                IntPtr hwnd = ResolveHostWindowHandle();
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 建内核控制器… hwnd=0x{hwnd.ToInt64():X}");

                // ⚠️ 新版 SDK（1.0.3719）不再吃裸 HWND：要包成 CoreWebView2ControllerWindowReference，
                //    而且它收的是 ulong，不是 IntPtr —— 得先转一道
                _controller = await environment.CreateCoreWebView2ControllerAsync(
                    CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd.ToInt64()));
                // 挂在屏幕外：controller 得保持「可见」—— IsVisible=false 会让 Chromium
                // 把整个 renderer 暂停，canvas 解码根本不跑。挪到主窗口坐标 (-32000,-32000)
                // 就等于人眼看不见，同时 compositor 还认它活着。
                _controller.Bounds = new Rect(-32000, -32000, 1, 1);
                // IsVisible 不放 false：Chromium 会把整个 renderer 停掉，图片解码根本不跑。
                // 人眼看不见是靠坐标挪到屏幕外实现的，跟这个是两回事。
                _controller.IsVisible = true;
                _core = _controller.CoreWebView2;
                System.Diagnostics.Debug.WriteLine("[IconRasterizer] 控制器已建好，装常驻 helper…");

                // ⭐ 注入必须赶在首次导航之前：AddScriptToExecuteOnDocumentCreatedAsync 只对
                //    注入之后才创建的文档生效。放在 NavigationCompleted 里补（旧版就是这么写的）
                //    当前文档一个字都收不到 —— 后面点火自然只能拿到空。
                await _core.AddScriptToExecuteOnDocumentCreatedAsync(RasterHelperScript);
                _core.WebMessageReceived -= OnRasterMessageReceived;
                _core.WebMessageReceived += OnRasterMessageReceived;

                TaskCompletionSource navigationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _core!.NavigationCompleted -= OnNavigationCompleted;
                    navigationDone.TrySetResult();
                }

                _core.NavigationCompleted += OnNavigationCompleted;
                _core.NavigateToString(HostDocument);

                // 兜底：导航万一卡住也不能把取图流程拖死
                Task timeout = Task.Delay(10000);
                if (await Task.WhenAny(navigationDone.Task, timeout) == timeout)
                {
                    _core.NavigationCompleted -= OnNavigationCompleted;
                    _failCount++;
                    _state = 2;
                    System.Diagnostics.Debug.WriteLine(
                        $"[IconRasterizer] 宿主文档导航超时（第 {_failCount} 次失败）");
                    return false;
                }

                // 自检：确认路径真的通了 —— helper 在不在、ExecuteScript 通道有没有回音。
                // 这条打印出来基本就等于「能光栅化了」，后面再出问题看它就够了。
                string? probe = DecodeScriptString(
                    await _core.ExecuteScriptAsync("'probe:' + (typeof window.__dtRaster)"));
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 宿主文档自检 typeof __dtRaster = {probe ?? "<null>"}");

                _state = 1;
                System.Diagnostics.Debug.WriteLine("[IconRasterizer] 渲染内核就绪");
                return true;
            }
            catch (Exception ex)
            {
                _failCount++;
                _state = 2;

                // 半初始化状态不能留 —— 下次重试得从干净的地方重来
                _core = null;
                _controller = null;
                _hostWindow = null;

                System.Diagnostics.Debug.WriteLine(
                    $"[IconRasterizer] 初始化失败（第 {_failCount}/{MaxInitAttempts} 次）: " +
                    $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
                return false;
            }
        }

        /// <summary>
        /// 告诉内核按哪个主题求值。SVG 里 <c>@media (prefers-color-scheme: dark)</c>
        /// 那条分支由 Chromium 自己算 —— 这比我们拆 CSS 再补颜色准得多。
        /// </summary>
        private void ApplyPreferredColourScheme(bool preferDark)
        {
            try
            {
                if (_core?.Profile is null)
                {
                    return;
                }

                _core.Profile.PreferredColorScheme = preferDark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch (Exception ex)
            {
                // 设不上不影响别的：最坏就是按内核默认（跟随系统）求值
                System.Diagnostics.Debug.WriteLine($"[IconRasterizer] 主题设置失败: {ex.Message}");
            }
        }

        /// <summary>把 <c>data:image/png;base64,xxx</c> 解成字节</summary>
        private static byte[]? DecodePngDataUrl(string dataUrl)
        {
            int comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                return bytes.Length > 0 ? bytes : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ExecuteScript 返回的是 JSON，字符串会带引号 —— 去掉</summary>
        private static string? DecodeScriptString(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            try
            {
                // 走 JsonDocument 而不是 JsonSerializer.Deserialize<string>：
                // 后者在 AOT 下会报 IL2026/IL3050（反射式序列化），这里没必要引入
                using JsonDocument doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind == JsonValueKind.String
                    ? doc.RootElement.GetString()
                    : null;
            }
            catch
            {
                return raw.Trim('"');
            }
        }
    }
}
