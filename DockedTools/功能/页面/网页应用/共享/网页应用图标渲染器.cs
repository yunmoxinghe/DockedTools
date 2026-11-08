using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 站点图标渲染器 —— 进程内把 SVG 光栅化成 PNG，主题可由调用方指定。
    ///
    /// <para><b>2026-10-26 源码集成换芯：</b>内核从「常驻隐藏 WebView2（屏外 renderer）」
    /// 换成仓库自带的 Svg.Skia 源码（<c>第三方源码\Svg.Skia</c>，
    /// 上游 https://github.com/wieslawsoltes/Svg.Skia commit 7910666415a9，MIT）。
    /// 换掉的东西：宿主窗口、离屏 renderer、导航与回推协议、内核冷启动（~1.4s）、
    /// UI 线程调度器，以及上一轮刚做的 IconRaster 独立 Profile ——
    /// 现在既没有实例需要 30 秒看门狗，也没有常驻 renderer 吃内存。</para>
    ///
    /// <para><b>为什么当年要借 Chromium：</b>站点真实 SVG 把颜色写在
    /// <c>&lt;style&gt;</c> 里（实测 chatgpt / astro.build / rust-lang），而 D2D 的
    /// <c>SvgImageSource</c> 不执行 CSS——「让真引擎算 CSS」这个需求没变，变的是引擎：
    /// Svg.Skia 的 <c>Svg.Custom</c> 自己就是一个执行 <c>&lt;style&gt;</c>/选择器/级联的
    /// 样式处理器，同一次求值下与 Chromium 同图同色（深色分支与老内核落盘同色坐实；
    /// 浅色分支按 CSS 语义即基础规则，老内核那份没法拿来对照，见下一段）。</para>
    ///
    /// <para><b><c>@media (prefers-color-scheme)</c> 靠自带补丁：</b>上游把未知 media
    /// feature 一律按不匹配处理（深浅分支全灭，且不跟系统），我们在
    /// <c>SvgDocumentLoadOptions.PrefersColorScheme</c> 加了强制配色入口，
    /// 由 <c>EvaluateMediaFeature</c> 离散求值——调用方传什么主题就按什么主题画。
    /// 判定过程见 <c>Compatibility\SvgCssCompatibilityProcessor.cs</c> 的补丁注释。
    /// 保真度实测（chatgpt 型样本，2026-11-02 在包内容器里复验）：浅=黑(0,0,0)、
    /// 深=白(255,255,255)，两份产物内容哈希不同 ⇒ 主题真的分开；多色品牌图标无损。</para>
    ///
    /// <para><b>别拿老 Chromium 内核的落盘图当「浅色基准」：</b>Release 容器里那张
    /// chatgpt 图标 <c>-l-</c> 与 <c>-d-</c> 是<b>同一张全白图</b>（内容哈希一字不差，
    /// 平均 RGB 255,255,255）—— 老内核的主题根本没分岔，浅色模式下就是浅底白图隐形。
    /// 所以深色这一路是「同色」，浅色这一路是「修好了」，不是「一模一样」。</para>
    ///
    /// <para><b>失败策略不变：</b>任何一步出错都返回 null，调用方退回自己那套逻辑 ——
    /// 这个服务是「更好的路」，不是「唯一的路」，它挂了不会让图标变差。</para>
    /// </summary>
    internal sealed class WebAppIconRasterizer
    {
        /// <summary>进程内唯一实例。无内核状态，随时可用</summary>
        public static WebAppIconRasterizer Instance { get; } = new();

        /// <summary>
        /// 默认输出边长。跟 favicon 那条链路里给矢量的虚拟尺寸 <c>VectorIconSize</c> 对齐 ——
        /// 光栅化产物是位图，尺寸按真实像素算，给小了会在防退化比较里输给旧的那张矢量图。
        /// </summary>
        public const int DefaultRasterSize = 256;

        /// <summary>
        /// 把 SVG 光栅化成 PNG。画不出来返回 null。
        /// </summary>
        /// <param name="svgBytes">SVG 原文</param>
        /// <param name="size">输出边长（px）</param>
        /// <param name="preferDark">
        /// 按哪个主题求值。<c>@media (prefers-color-scheme)</c> 由渲染引擎按这里
        /// 指定的方案求值（自带补丁通道 <c>PrefersColorScheme</c>），
        /// 单色图标的明暗自动就对 —— 不用我们再拆 CSS。
        /// </param>
        /// <remarks>
        /// 签名保持 Task 纯粹是为了不动既有调用点（当年 WebView2 内核必须异步等待）；
        /// 实际是进程内同步画，Skia 在锁内串行。
        /// </remarks>
        public Task<byte[]?> RasterizeSvgAsync(byte[] svgBytes, int size, bool preferDark)
        {
            if (svgBytes is null || svgBytes.Length == 0)
            {
                return Task.FromResult<byte[]?>(null);
            }

            // ⚠️ 这个方法原先是「假异步」：在调用线程上 lock + 画完，再 Task.FromResult 包一层，
            //    所以 await 它一点都不会挂起 —— SHA256、Svg.Skia 求值 CSS、SKSurface 绘制、PNG 编码
            //    全压在调用线程上（主页出卡片档 / 顶栏 / 侧边栏那些时候就是 UI 线程），
            //    而且一张畸形 SVG 要画多久没有任何上限。
            //    官方口径说得很直接：await 并不保证工作离开 UI 线程，自己的代码要显式交给 Task.Run
            //    （Learn《Keep the UI thread responsive》，页面更新 2026-07-17）；
            //    异步准则里那条「一次别让 UI 线程阻塞超过 50ms」也正是这条改动的判据。
            //
            //    ⚠️ 但要说清一个例外：同一批权威指南也写着「不要在可复用的库代码里用 Task.Run
            //    伪装异步，应该由调用方决定线程」。那条针对的是对外发布的类库和 ASP.NET Core 的
            //    吞吐场景。本类是应用内部单例、5 个调用点全在页面层且都已 await，
            //    所以收在这里换调用方零改动是划得来的 —— 但别把它当成通用范式往外抄。
            //
            // 代价说清楚：缓存命中现在也要过一次线程池（原先命中是同步返回）；
            // 连缓存键的 SHA256 也一起挪走了（几 KB SVG 做一次哈希再转 64 个十六进制字符，
            // 本来就不该在 UI 线程做）。这里调用方全是 await、没人拿结果去画下一帧，
            // 跳一次线程池是微秒级，换 UI 线程干净是赚的。
            return Task.Run(() => RenderWithCache(svgBytes, size, preferDark));
        }

        /// <summary>
        /// 线程池上执行的这一份：先查缓存，没命中才进串行锁真画。
        /// 双重检查保留 —— 并发打同一张图时，排队期间可能已经被别人画好了。
        /// </summary>
        private byte[]? RenderWithCache(byte[] svgBytes, int size, bool preferDark)
        {
            // ⭐ 同一份 SVG + 同尺寸 + 同主题，画一次就够。
            //    实测同一张图被画了两遍：顶栏发布一次（契约只认位图 Uri），
            //    主页卡片 / 侧边栏的重画又一次。
            string cacheKey = BuildCacheKey(svgBytes, size, preferDark);
            if (_resultCache.TryGetValue(cacheKey, out byte[]? hit))
            {
                return hit;
            }

            lock (_renderGate)
            {
                // 排队期间可能已经被别人画好了（并发打同一张图），再查一次
                if (_resultCache.TryGetValue(cacheKey, out byte[]? raced))
                {
                    return raced;
                }

                try
                {
                    byte[]? png = RenderCore(svgBytes, size, preferDark);
                    if (png is { Length: > 0 })
                    {
                        // 失败不入缓存：某张畸形 SVG 画不出来是常态，
                        // 记进去会把它永久钉死成「画不出来」
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
            }
        }

        /// <summary>
        /// 渲染结果的记忆化缓存：同一份 SVG 只画一次。
        /// 换成 ConcurrentDictionary 是因为本类现在真的会被线程池线程访问 ——
        /// 原先普通 Dictionary 的读在锁外（快路径）、写在锁内，同一线程下侥幸没事先，
        /// 一旦有后台线程参与就是竞态；现在读写都容得下并发，
        /// SKSvg 本身仍然由 <see cref="_renderGate"/> 串行保护。
        /// </summary>
        private readonly ConcurrentDictionary<string, byte[]> _resultCache = new(StringComparer.Ordinal);

        /// <summary>SKSvg 非线程安全，全局串行一把锁（单张 48px 光栅化是毫秒级）</summary>
        private readonly object _renderGate = new();

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
        /// 真渲染：Svg.Skia 按指定配色求值 CSS 并画成 size×size 的 PNG。
        /// 等比缩放 + 居中，跟旧 Chromium canvas 路径的构图一致，非正方形不被拉扁。
        /// 画不出来返回 null。
        /// </summary>
        private static byte[]? RenderCore(byte[] svgBytes, int size, bool preferDark)
        {
            SvgParameters parameters = new()
            {
                LoadOptions = new SvgDocumentLoadOptions
                {
                    // 自带补丁的入口：media 条件按这里给的方案求值，不跟系统
                    PrefersColorScheme = preferDark
                        ? SvgDocumentLoadOptions.ColorSchemeDark
                        : SvgDocumentLoadOptions.ColorSchemeLight
                }
            };

            using MemoryStream stream = new(svgBytes, writable: false);
            using SKSvg svg = new();

            SKPicture? picture = svg.Load(stream, parameters);
            if (picture is null)
            {
                System.Diagnostics.Debug.WriteLine("[IconRasterizer] Svg.Skia 没能解析这份 SVG");
                return null;
            }

            SKRect cull = picture.CullRect;
            float longest = Math.Max(cull.Width, cull.Height);
            if (longest <= 0f || size <= 0)
            {
                // 画布为空（比如整张图都在 viewBox 外）——等同画不出来
                return null;
            }

            float scale = size / longest;
            float scaledWidth = cull.Width * scale;
            float scaledHeight = cull.Height * scale;

            SKImageInfo info = new(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(info);
            SKCanvas canvas = surface.Canvas;

            // 透明底：跟旧版 NavigateToString 空白页 + canvas 的行为对齐，
            // 不给图标垫任何背景色
            canvas.Clear(SKColors.Transparent);
            canvas.Translate((size - scaledWidth) / 2f, (size - scaledHeight) / 2f);
            canvas.Scale(scale);
            canvas.Translate(-cull.Left, -cull.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.Size > 0 ? data.ToArray() : null;
        }
    }
}

