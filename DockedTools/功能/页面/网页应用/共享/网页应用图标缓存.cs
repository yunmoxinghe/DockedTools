using Microsoft.UI.Xaml;          // RoutedEventHandler / ExceptionRoutedEventHandler
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using DockedTools.Features.UnifiedCalls.TopAppBar;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 网页应用图标的磁盘缓存（<c>ApplicationData\LocalFolder\web-icons</c>）。
    ///
    /// 为什么文件名要带内容哈希：
    ///   侧边栏的图标是 <see cref="Microsoft.UI.Xaml.Controls.ImageIcon"/> + BitmapImage，
    ///   BitmapImage 默认开图像缓存 —— 内容换了但文件路径没换的话，同一个 Uri 重新赋值
    ///   不会触发重新解码，视觉上还是旧图（"改了图标不生效"）。
    ///   文件名跟内容绑定 ⇒ 内容一变路径必变 ⇒ 必然重新加载。
    ///   反过来，内容没变时路径也不变，磁盘上就那一份，不会重复写。
    ///
    /// 这也是 WebView2 社区处理 favicon 的通用套路：拿 favicon 的 URI 算哈希做缓存文件名，
    /// 命中就不重新下载（见 WebView2Feedback/specs/GetFavicon.md 里
    /// "Loading the same Favicon twice does re-raise the FaviconChanged event" 那条：
    /// 事件会重复来，去重得自己做）。
    /// 这里换成内容哈希而不是 URI 哈希，是因为同一个 URI 的内容也可能换（站点改图标）。
    /// </summary>
    /// <summary>图像源的加载结果。<see cref="Timeout"/> 是"没确认"，不是"坏了"</summary>
    public enum ImageLoadState
    {
        /// <summary>解码完成，可以放心上树</summary>
        Ready,

        /// <summary>明确失败：坏文件 / 格式不支持</summary>
        Failed,

        /// <summary>等超时了：既没确认成功也没确认失败，调用方应按"照常用"处理</summary>
        Timeout
    }

    public static class WebAppIconCache
    {
        private const string CacheFolderName = "web-icons";

        /// <summary>
        /// appId → 已知缓存路径。导航栏启动时每个快捷方式都要过一遍 Save，
        /// 没有这层记忆的话每个都要枚举一次目录去清旧文件（N 个应用 = N 次全目录扫描）。
        /// </summary>
        private static readonly Dictionary<string, string> KnownPaths = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object KnownPathsLock = new();

        /// <summary>缓存目录，跟导航栏原来用的是同一个（web-icons）</summary>
        public static string CacheDirectory =>
            Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, CacheFolderName);

        /// <summary>
        /// 把图标字节落到缓存目录，返回可直接当 UriSource 用的文件路径。
        /// 内容没变时只返回已有路径、不重写盘；同时清理同一个 appId 的旧哈希文件
        /// （否则扩展名/内容一变就留一份垃圾，备份时整个目录一起打包）。
        /// </summary>
        public static string? Save(string appId, byte[]? bytes)
        {
            if (bytes is not { Length: > 0 } || string.IsNullOrEmpty(appId))
            {
                return null;
            }

            try
            {
                string directory = CacheDirectory;
                Directory.CreateDirectory(directory);

                // ⭐ 单色图标按当前主题上色后再落盘：
                //    内容变了 ⇒ 内容哈希变了 ⇒ 文件名变了 ⇒ ImageIcon 拿到新 Uri 重新解码。
                //    「切主题图标跟着变」就是这条链路撑起来的 ——
                //    既不用改持久化的数据（IconBytes 存的是原样），也不用两份文件互相打架。
                bytes = ApplyMonochromeTheme(bytes);

                string path = Path.Combine(directory, BuildFileName(appId, bytes));

                // 路径没变 + 文件还在 ⇒ 内容和上次一样，连目录枚举都省了
                lock (KnownPathsLock)
                {
                    if (KnownPaths.TryGetValue(appId, out string? known) &&
                        string.Equals(known, path, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(path))
                    {
                        return path;
                    }
                }

                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);
                }

                DeleteStaleFiles(appId, path);

                lock (KnownPathsLock)
                {
                    KnownPaths[appId] = path;
                }

                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 写入失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>取某个应用当前缓存里的图标路径；没有就返回 null</summary>
        public static string? TryGetCachedPath(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            try
            {
                string directory = CacheDirectory;
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                // 正常情况只该有一份（Save 会清掉同 appId 的其它哈希文件）。
                // 万一真撞上多份，按最后写入时间取最新的 —— 按文件名排序取到的是
                // 哈希字典序最大的那份，跟"哪个是当前的"没有任何关系。
                var newest = new DirectoryInfo(directory)
                    .EnumerateFiles(SanitizeId(appId) + "-*")
                    // 旁路存的 SVG 原件不是「当前图标」，别把它当成缓存命中
                    .Where(f => !f.Name.EndsWith(SourceSuffix, StringComparison.OrdinalIgnoreCase))
                    // 卡片档是另一套尺寸，别被通用档当成「当前图标」取走
                    .Where(f => !f.Name.Contains(CardSegment, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                return newest?.FullName;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 读取失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>把某个应用当前的图标缓存文件整个读进内存；没有缓存就返回 null</summary>
        public static byte[]? TryReadCachedBytes(string appId)
        {
            string? path = TryGetCachedPath(appId);
            if (path is null)
            {
                return null;
            }

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 读缓存字节失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>把卡片档文件整个读进内存；没有就返回 null</summary>
        public static byte[]? TryReadCardBytes(string appId)
        {
            string? path = TryGetCardPath(appId);
            if (path is null)
            {
                return null;
            }

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 读卡片档失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 把图标字节【在内存里解码成已经就绪的图像源】，不走文件 Uri。
        ///
        /// 为什么主页要走这条路（git 里 6c51d5a 那一版就是这么做的）：
        ///   走 Uri 的话每张图都要自己开一次文件、异步解码，25 张 = 25 次排队，
        ///   实测「整列 Add 完 +114ms → 最后一张就绪 +865ms」—— 中间 750ms 卡片全是空图标位，
        ///   然后一张张亮起来，看着就是「某个图标最后一个蹦出来」。
        ///   内存解码可以并发跑，全解完再一起上树，卡片出现那一刻图标就已经在内存里了。
        ///
        /// SVG 也走同一条路（<c>SvgImageSource.SetSourceAsync</c>），一样不吃文件 Uri。
        /// </summary>
        public static async Task<ImageSource?> DecodeAsync(byte[]? bytes)
        {
            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            try
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

                // ⭐ DataWriter 会【接管】它底下那条流的所有权 —— 直接 using 掉 writer，
                //    它 Dispose 时会把 stream 一起关掉，后面的 Seek / SetSourceAsync
                //    就全打在已释放的对象上（实测 25 张全部 "Cannot access a disposed object"，
                //    主页整列退化成地球图标）。先用 DetachStream 把所有权要回来再放掉 writer。
                var writer = new Windows.Storage.Streams.DataWriter(stream);
                try
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                finally
                {
                    writer.DetachStream();
                    writer.Dispose();
                }

                stream.Seek(0);

                if (IsSvgContent(bytes))
                {
                    var svg = new SvgImageSource();
                    await svg.SetSourceAsync(stream);
                    return svg;
                }

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 内存解码失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 主页卡片那一档图标的边长。
        ///
        /// 定的依据：CommunityToolkit 的 <c>SettingsCard</c> 把 <c>HeaderIcon</c> 塞进一个
        /// <c>Viewbox</c>，它的 <c>MaxWidth/MaxHeight</c> 绑的是
        /// <c>SettingsCardHeaderIconMaxSize</c>（源码里就是 20）—— 卡片图标最大只有 20×20。
        /// 高 DPI 下（150% / 200%）这一格要 ~40~48px 的源才不发虚，
        /// 而顶栏 / 侧边栏那套是为 16~20px 准备的，拿来放大就是「糊」的来源。
        /// </summary>
        public const int CardIconSize = 48;

        /// <summary>卡片档文件名里的标记段，用来跟通用档区分开（两边互不干扰）</summary>
        private const string CardSegment = "-card-";

        /// <summary>取某个应用【当前主题】的卡片档图标路径；没有就返回 null</summary>
        public static string? TryGetCardPath(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            try
            {
                string directory = CacheDirectory;
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                return Directory
                    .EnumerateFiles(directory, SanitizeId(appId) + "-" + ThemeTag + CardSegment + "*")
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 卡片档探测失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 把一张已经处理好的位图存成卡片档。
        /// 跟通用档完全分开：通用档那边换图 / 清旧文件都不会动到它，反之亦然。
        /// </summary>
        public static string? SaveCard(string appId, byte[]? bytes)
        {
            if (bytes is not { Length: > 0 } || string.IsNullOrEmpty(appId))
            {
                return null;
            }

            try
            {
                string directory = CacheDirectory;
                Directory.CreateDirectory(directory);

                string path = Path.Combine(
                    directory,
                    SanitizeId(appId) + "-" + ThemeTag + CardSegment + ComputeContentHash(bytes) + ".png");

                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);
                }

                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 卡片档写入失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 备出这个应用的卡片档图标；已经有了就直接返回，没有才真的去做一张。
        ///
        /// 两条取材路线：
        ///   ① 存着 SVG 原件 ⇒ 它是矢量，按当前主题光栅化到 <see cref="CardIconSize"/>，
        ///      比任何位图都清楚（Copilot / DeepSeek 这类 SVG 站点走这条）；
        ///   ② 只有位图 ⇒ 拿最大那一帧缩到卡片尺寸。
        ///      必须挑最大帧：ICO 里常常把 16×16 排在第一帧，而 <c>BitmapImage</c> 解码 ICO
        ///      认的就是第一帧 —— Google 翻译 / 文心一言 那两张糊图就是这么来的。
        ///
        /// ⚠️ 只在后台调用：路线 ① 要等渲染内核（冷启动 ~1.4s），路线 ② 是纯 WIC 解码。
        /// </summary>
        public static async Task<string?> EnsureCardAsync(string appId, byte[]? fallbackBytes)
        {
            string? existing = TryGetCardPath(appId);
            if (existing is not null)
            {
                return existing;
            }

            byte[]? card = null;

            // ① 矢量原件优先。旁路那份没存也不要紧 ——
            //    很多站点（Copilot / DeepSeek）快捷方式里带的就是 SVG 原件本身
            byte[]? svg = TryGetIconSource(appId) ?? (IsSvgContent(fallbackBytes) ? fallbackBytes : null);
            if (svg is { Length: > 0 })
            {
                try
                {
                    card = await WebAppIconRasterizer.Instance.RasterizeSvgAsync(
                        svg,
                        CardIconSize,
                        TopAppBarService.GetActualTheme() == ElementTheme.Dark);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 卡片档光栅化失败: {appId}, {ex.Message}");
                }
            }

            // ② 位图：取最大帧，太大就缩到卡片尺寸，比卡片小的不硬拉（拉了只会更糊）
            card ??= await RasterizeBitmapToCardAsync(fallbackBytes);

            return card is { Length: > 0 } ? SaveCard(appId, card) : null;
        }

        /// <summary>
        /// 位图 → 卡片档：挑尺寸最大的那一帧，超过 <see cref="CardIconSize"/> 就等比缩小。
        /// 出来的统一是 PNG（ICO 那种多帧容器交给 WIC 拆，别让 ImageIcon 自己去猜帧）。
        /// </summary>
        private static async Task<byte[]?> RasterizeBitmapToCardAsync(byte[]? bytes)
        {
            if (bytes is not { Length: > 0 } || IsSvgContent(bytes))
            {
                return null;
            }

            try
            {
                using var input = new Windows.Storage.Streams.InMemoryRandomAccessStream();

                // ⭐ 同 DecodeAsync 那个坑：DataWriter 会接管流的所有权，
                //    直接 using 掉 writer 会把 input 一起关掉。DetachStream 先要回来。
                var writer = new Windows.Storage.Streams.DataWriter(input);
                try
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                finally
                {
                    writer.DetachStream();
                    writer.Dispose();
                }

                input.Seek(0);

                Windows.Graphics.Imaging.BitmapDecoder decoder =
                    await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);

                if (decoder.FrameCount == 0)
                {
                    return null;
                }

                // 取像素最多的那一帧 —— ICO 的第一帧常常是最小的那份
                uint bestFrame = 0;
                uint bestPixels = 0;
                for (uint i = 0; i < decoder.FrameCount; i++)
                {
                    Windows.Graphics.Imaging.BitmapFrame frame = await decoder.GetFrameAsync(i);
                    uint pixelCount = frame.PixelWidth * frame.PixelHeight;
                    if (pixelCount > bestPixels)
                    {
                        bestPixels = pixelCount;
                        bestFrame = i;
                    }
                }

                Windows.Graphics.Imaging.BitmapFrame chosen = await decoder.GetFrameAsync(bestFrame);
                uint width = chosen.PixelWidth;
                uint height = chosen.PixelHeight;

                uint longest = Math.Max(width, height);
                if (longest == 0)
                {
                    return null;
                }

                uint targetWidth = width;
                uint targetHeight = height;

                if (longest > CardIconSize)
                {
                    double scale = (double)CardIconSize / longest;
                    targetWidth = (uint)Math.Max(1, Math.Round(width * scale));
                    targetHeight = (uint)Math.Max(1, Math.Round(height * scale));
                }

                var transform = new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth = targetWidth,
                    ScaledHeight = targetHeight,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                };

                Windows.Graphics.Imaging.PixelDataProvider pixels = await chosen.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

                byte[] raw = pixels.DetachPixelData();

                using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                Windows.Graphics.Imaging.BitmapEncoder encoder =
                    await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);

                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    targetWidth,
                    targetHeight,
                    96,
                    96,
                    raw);

                await encoder.FlushAsync();

                var result = new byte[output.Size];
                output.Seek(0);
                var reader = new Windows.Storage.Streams.DataReader(output);
                try
                {
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(result);
                }
                finally
                {
                    reader.DetachStream();
                    reader.Dispose();
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 卡片档位图处理失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>删掉某个应用的图标缓存（取消固定 / 删除网页应用时用）</summary>
        public static void Delete(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return;
            }

            try
            {
                string directory = CacheDirectory;
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(directory, SanitizeId(appId) + "-*"))
                {
                    File.Delete(file);
                }

                lock (KnownPathsLock)
                {
                    KnownPaths.Remove(appId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 删除失败: {appId}, {ex.Message}");
            }
        }

        private static void DeleteStaleFiles(string appId, string keepPath)
        {
            string directory = CacheDirectory;

            foreach (string file in Directory.EnumerateFiles(directory, SanitizeId(appId) + "-*"))
            {
                // SVG 原件要留着：主题一变就得靠它重画，被当成陈年旧文件删掉的话
                // 切主题就再也换不了色了
                // 卡片档同理：它是主页单独那一套，通用档换图不该把它带走
                if (string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(SourceSuffix, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Contains(CardSegment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 清理旧文件失败: {file}, {ex.Message}");
                }
            }
        }

        /// <summary>浅色主题下单色图标该用的颜色</summary>
        private const string LightIconColour = "#000000";

        /// <summary>
        /// 把光栅化那张图的 SVG 原件旁路存一份。
        ///
        /// 为什么非要留它：光栅化的产物是 PNG，<see cref="ApplyMonochromeTheme"/> 改不动位图；
        /// 而 PNG 里那层颜色是 Chromium 抓图那一刻按主题求出来的，
        /// 之后换主题只能「拿原件重画一次」，没有原件就永远定死在那个颜色上了。
        /// </summary>
        public static void SaveIconSource(string? appId, byte[]? svgBytes)
        {
            if (string.IsNullOrEmpty(appId) || svgBytes is not { Length: > 0 })
            {
                return;
            }

            if (!IsSvgContent(svgBytes))
            {
                return;
            }

            try
            {
                string directory = CacheDirectory;
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, SanitizeId(appId) + SourceSuffix);

                // 内容没变就不重写：这会被 Save 之后的清理路径反复走到
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(svgBytes))
                {
                    return;
                }

                File.WriteAllBytes(path, svgBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 存 SVG 原件失败: {appId}, {ex.Message}");
            }
        }

        /// <summary>取回之前存下的 SVG 原件；没有 / 不是合法 SVG 就返回 null</summary>
        public static byte[]? TryGetIconSource(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            try
            {
                string path = Path.Combine(CacheDirectory, SanitizeId(appId) + SourceSuffix);
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);
                return bytes.Length > 0 && IsSvgContent(bytes) ? bytes : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 读 SVG 原件失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// <see cref="Save"/> 的主题感知版本：需要在落盘前补做的事，都在这一步做完。
        ///
        /// 什么时候真的会动：手上这张是位图、而这个 appId 又存着 SVG 原件 ——
        /// 说明它是光栅化产物，里头的颜色是「抓图时的那个主题」决定的。
        /// 让专用渲染内核按当前主题重新画一遍，出来的 PNG 字节自然不同，
        /// 内容哈希变 ⇒ 文件名（本身就带主题标记）变 ⇒ ImageIcon 重新解码，颜色就对了。
        ///
        /// ⚠️ 调用点必须是不阻塞 UI 线程的异步上下文：
        ///    内部要 await 渲染内核，而内核初始化会往 UI 线程排队。
        /// </summary>
        public static async Task<string?> SaveAsync(string appId, byte[]? bytes)
        {
            try
            {
                if (bytes is { Length: > 0 } &&
                    !IsSvgContent(bytes) &&
                    NeedsRepaint(appId, out byte[]? source) &&
                    source is { Length: > 0 })
                {
                    byte[]? repaint = await WebAppIconRasterizer.Instance.RasterizeSvgAsync(
                        source,
                        WebAppIconRasterizer.DefaultRasterSize,
                        TopAppBarService.GetActualTheme() == ElementTheme.Dark);

                    if (repaint is { Length: > 0 })
                    {
                        bytes = repaint;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 按主题重画失败，用现有图: {appId}, {ex.Message}");
            }

            return Save(appId, bytes);
        }

        /// <summary>
        /// 这个 appId 的图标需不需要按当前主题重画。同步判断，只比文件时间，不做任何 IO 以外的活。
        ///
        /// 给「先出整列、再补画」那条路用：调用方拿它决定要不要给这张图排一次后台重画。
        /// </summary>
        public static bool NeedsThemeRepaint(string appId) => NeedsRepaint(appId, out _);

        /// <summary>当前主题这一版的缓存文件路径；磁盘上没有就返回 null</summary>
        private static string? TryGetCurrentThemeCachePath(string appId)
        {
            try
            {
                string directory = CacheDirectory;
                if (string.IsNullOrEmpty(appId) || !Directory.Exists(directory))
                {
                    return null;
                }

                // EnumerateFiles 吐的是路径字符串（不是 FileInfo），没有 .Name —— 直接拿尾巴比后缀
                return Directory.EnumerateFiles(directory, SanitizeId(appId) + "-" + ThemeTag + "-*")
                    .FirstOrDefault(f => !f.EndsWith(SourceSuffix, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 主题缓存探测失败: {appId}, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 要不要拿 SVG 原件重画一张（原件顺带从 <c>out</c> 带出来，省一次读盘）。
        ///
        /// 满足其一就重画：
        ///   ① 磁盘上没有「当前主题这一版」—— 换主题时文件名里的主题标记对不上，正好命中；
        ///   ② 原件比现有缓存新 —— 站点换了图标，重抓的 SVG 已经覆盖上去，
        ///      而缓存里那张还是按旧原件画的。
        ///
        /// ⚠️ 以前只看 ①，于是「有缓存就永不重画」：站点改图标、甚至第一次光栅化失败
        ///    留下的小图，都会被这份缓存一直供着，看着就是「图标不更新」。
        ///    同时这里也是省开销的闸门 —— 都不满足时，启动 / 重建侧边栏不必把每个图标
        ///    都送进渲染内核跑一遍（一次几百毫秒，串行）。
        /// </summary>
        private static bool NeedsRepaint(string appId, out byte[]? source)
        {
            source = null;

            if (string.IsNullOrEmpty(appId))
            {
                return false;
            }

            byte[]? svg = TryGetIconSource(appId);
            if (svg is not { Length: > 0 })
            {
                return false;
            }

            string? cached = TryGetCurrentThemeCachePath(appId);
            if (cached is null)
            {
                source = svg;
                return true;
            }

            try
            {
                // SaveIconSource 是「内容没变就不重写」，所以原件的 mtime 变了 = 原件真换了
                string sourcePath = Path.Combine(CacheDirectory, SanitizeId(appId) + SourceSuffix);
                if (File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(cached))
                {
                    source = svg;
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 比对原件时间失败: {appId}, {ex.Message}");
            }

            return false;
        }

        /// <summary>深色主题下单色图标该用的颜色</summary>
        private const string DarkIconColour = "#FFFFFF";

        /// <summary>这几个值都不是「颜色」，不能参与单色判定</summary>
        private static readonly string[] NonPaintValues = { "none", "currentColor", "inherit", "transparent" };

        /// <summary>
        /// 单色 SVG 的主题化：把图标里唯一那一种颜色换成当前主题该用的图标色。
        ///
        /// 为什么要这一道：站点为了适配 <c>prefers-color-scheme</c>，常常把颜色写在
        /// &lt;style&gt; 里（实测 chatgpt / astro.build / rust-lang 都是这么干的），
        /// 而 Direct2D 不执行 CSS。抢救链路能把颜色内联出来，但内联出来的
        /// 是「站点为某个主题挑的颜色」，我们自己的主题切了它不会跟着变。
        ///
        /// 只动「整张图只有一种颜色」的图标：
        ///   · 0 种 —— 没写颜色，靠 SVG 默认的 black，也不需要动；
        ///   · 2 种以上 —— 品牌 logo（vuejs 就是双色），刷成纯色剪影是帮倒忙。
        /// </summary>
        public static byte[] ApplyMonochromeTheme(byte[] bytes)
        {
            // 位图没有可靠的换色手段（也不该动品牌色），只处理矢量
            if (!IsSvgContent(bytes))
            {
                return bytes;
            }

            try
            {
                XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.PreserveWhitespace);
                if (doc.Root is null)
                {
                    return bytes;
                }

                var colours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement element in doc.Root.DescendantsAndSelf())
                {
                    CollectPaint(element.Attribute("fill")?.Value, colours);
                    CollectPaint(element.Attribute("stroke")?.Value, colours);

                    string? inline = element.Attribute("style")?.Value;
                    if (inline is null)
                    {
                        continue;
                    }

                    foreach (Match m in Regex.Matches(inline, @"(?:^|;)\s*(?:fill|stroke)\s*:\s*([^;]+)"))
                    {
                        CollectPaint(m.Groups[1].Value.Trim(), colours);
                    }
                }

                if (colours.Count != 1)
                {
                    return bytes;
                }

                string old = colours.First();
                string target = TopAppBarService.GetActualTheme() == ElementTheme.Dark
                    ? DarkIconColour
                    : LightIconColour;

                if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                {
                    return bytes;
                }

                foreach (XElement element in doc.Root.DescendantsAndSelf())
                {
                    ReplacePaint(element.Attribute("fill"), old, target);
                    ReplacePaint(element.Attribute("stroke"), old, target);

                    XAttribute? style = element.Attribute("style");
                    if (style is not null)
                    {
                        style.Value = Regex.Replace(
                            style.Value,
                            $@"((?:^|;)\s*(?:fill|stroke)\s*:\s*){Regex.Escape(old)}",
                            "${1}" + target,
                            RegexOptions.IgnoreCase);
                    }
                }

                byte[] result = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
                return result.Length > 0 ? result : bytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 单色图标主题化失败，用原图: {ex.Message}");
                return bytes;
            }
        }

        private static void CollectPaint(string? value, HashSet<string> colours)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string v = value.Trim();

            // 渐变 / 图案引用（fill="url(#a)"）不是颜色，也换不了
            if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("var(", StringComparison.OrdinalIgnoreCase) ||
                NonPaintValues.Contains(v, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            colours.Add(v);
        }

        private static void ReplacePaint(XAttribute? attribute, string old, string target)
        {
            if (attribute is not null &&
                string.Equals(attribute.Value.Trim(), old, StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = target;
            }
        }

        private static string BuildFileName(string appId, byte[] bytes) =>
            SanitizeId(appId) + "-" + ThemeTag + "-" + ComputeContentHash(bytes) + DetectExtension(bytes);

        /// <summary>SVG 原件旁路文件的后缀。跟缓存文件用同一个 "{appId}-" 前缀以便统一清理</summary>
        private const string SourceSuffix = "-source.svg";

        /// <summary>
        /// 缓存文件名里带的主题标记（d=深色 / l=浅色）。
        ///
        /// 为什么要掺进文件名：光栅化出来的图标是 PNG，<see cref="ApplyMonochromeTheme"/>
        /// 只认矢量、改不动它 —— 而它里面那层颜色，是 Chromium 按抓图那一刻的主题，
        /// 拿 SVG 里 <c>@media (prefers-color-scheme)</c> 求出来的。主题换了但文件名不变的话，
        /// BitmapImage 会因为「同一个 Uri」直接复用旧解码结果，视觉上就是「切了主题图标不变色」。
        /// 把主题写进文件名 ⇒ 换主题必然换路径 ⇒ 必然重新加载。
        /// </summary>
        private static string ThemeTag =>
            TopAppBarService.GetActualTheme() == ElementTheme.Dark ? "d" : "l";

        /// <summary>SHA256 前 16 位十六进制，跟顶栏 PublishShortcutIconAsync 保持一致</summary>
        public static string ComputeContentHash(byte[] bytes) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

        /// <summary>按文件头猜扩展名。BitmapImage 靠扩展名认格式，猜错就加载不出来</summary>
        public static string DetectExtension(byte[] bytes)
        {
            // SVG 是文本，没有魔数，得单独认。放最前面：它是纯 ASCII 文本，
            // 不可能跟下面任何一种二进制头冲突。
            if (IsSvgContent(bytes))
            {
                return ".svg";
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return ".png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return ".jpg";
            }

            if (bytes.Length >= 4 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return ".gif";
            }

            if (bytes.Length >= 2 &&
                bytes[0] == 0x42 && bytes[1] == 0x4D)
            {
                return ".bmp";
            }

            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return ".webp";
            }

            if (bytes.Length >= 4 &&
                bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            {
                return ".ico";
            }

            return ".png";
        }

        /// <summary>
        /// 按 Uri 建一个能直接塞进 <c>ImageIcon.Source</c> / <c>Image.Source</c> 的图像源。
        ///
        /// 为什么要分叉：BitmapImage 只认位图，SVG 得走 <see cref="SvgImageSource"/>。
        /// 以前的做法是一看到 SVG 就拒收（"不支持 SVG 格式"），结果 GitHub 这类
        /// 只提供 SVG 图标的站点只能掉到 16x16 的内核兜底图，糊得最明显。
        ///
        /// 失败回调两边都有：位图是 <c>ImageFailed</c>，SVG 是 <c>OpenFailed</c>，
        /// 名字不一样但语义一致 —— 统一成一个 Action，调用方不用关心自己拿到的是哪一种。
        /// </summary>
        public static ImageSource CreateImageSource(Uri uri, Action? onFailed = null)
        {
            if (IsSvgUri(uri))
            {
                var svg = new SvgImageSource(uri);

                if (onFailed is not null)
                {
                    svg.OpenFailed += (_, _) => onFailed();
                }

                return svg;
            }

            var bitmap = new BitmapImage
            {
                // 不走 XAML 的图像缓存：图标文件可能被同名覆盖，缓存会让我们看到上一张图
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };

            if (onFailed is not null)
            {
                bitmap.ImageFailed += (_, _) => onFailed();
            }

            // UriSource 必须最后赋值：赋上去的那一刻就开始加载，事件要在此之前挂好
            bitmap.UriSource = uri;
            return bitmap;
        }

        /// <summary>
        /// 等一个图像源加载完（成功 / 失败 / 超时三态）。
        ///
        /// 为什么需要它：WinUI 的 <c>Image</c> 换 Source 时中间必然经过一个"什么都没画"的帧
        /// —— 官方 issue microsoft-ui-xaml#8750《Image flickers when source is updated》
        /// 记的就是这件事，而且特别指出"新旧图是同一张时最明显"。
        /// 把还没解码完的 ImageIcon 挂上树，那个空白帧会被拉长成整个下载 + 解码的时长，
        /// 这就是侧边栏 favicon 实时更新时"闪一下"的根因。
        ///
        /// 官方推荐的做法正是：先等 <c>ImageOpened</c> / <c>Opened</c>，确认位图已经在内存里，
        /// 再把它交给控件。MS 文档也明确 BitmapImage 的 ImageOpened
        /// "fires at a time that is potentially before you've assigned your BitmapImage
        /// to be the source of an Image" —— 所以这里不用先挂到可视树上等。
        ///
        /// 超时单独算一种状态（<see cref="ImageLoadState.Timeout"/>）：没确认好也没确认坏 ——
        /// 调用方遇到超时要按"没这回事"继续用这个源，最坏也只是跟不等一样，
        /// 绝不能当成失败把图标整没了。防闪是为了好看，把图标弄丢是事故。
        /// </summary>
        public static async Task<ImageLoadState> WaitUntilReadyAsync(ImageSource source, int timeoutMs = 2500)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            switch (source)
            {
                case BitmapImage bitmap:
                {
                    // 拿进来就已经解好了（PixelWidth 有值）的话事件不会再来，别傻等
                    if (bitmap.PixelWidth > 0 || bitmap.PixelHeight > 0)
                    {
                        return ImageLoadState.Ready;
                    }

                    RoutedEventHandler onOpened = null!;
                    ExceptionRoutedEventHandler onFailed = null!;

                    void Detach()
                    {
                        bitmap.ImageOpened -= onOpened;
                        bitmap.ImageFailed -= onFailed;
                    }

                    onOpened = (_, _) => { Detach(); tcs.TrySetResult(true); };
                    onFailed = (_, _) => { Detach(); tcs.TrySetResult(false); };

                    bitmap.ImageOpened += onOpened;
                    bitmap.ImageFailed += onFailed;
                    break;
                }

                case SvgImageSource svg:
                {
                    Windows.Foundation.TypedEventHandler<SvgImageSource, SvgImageSourceOpenedEventArgs> onOpened = null!;
                    Windows.Foundation.TypedEventHandler<SvgImageSource, SvgImageSourceFailedEventArgs> onFailed = null!;

                    void Detach()
                    {
                        svg.Opened -= onOpened;
                        svg.OpenFailed -= onFailed;
                    }

                    onOpened = (_, _) => { Detach(); tcs.TrySetResult(true); };
                    onFailed = (_, _) => { Detach(); tcs.TrySetResult(false); };

                    svg.Opened += onOpened;
                    svg.OpenFailed += onFailed;
                    break;
                }

                default:
                    // 别的 ImageSource 类型（WriteableBitmap 之类）本身就没有异步加载这一步
                    return ImageLoadState.Ready;
            }

            Task delay = Task.Delay(timeoutMs);

            // ⚠️ 这里千万别 ConfigureAwait(false)：
            //    调用方在 await 之后要动 NavigationViewItem 这类 XAML 元素，
            //    一旦延续被甩到线程池线程，跨线程访问直接抛异常 —— 而且这里是
            //    fire-and-forget，异常被吞掉，表现就是"图标全没了"。
            Task finished = await Task.WhenAny(tcs.Task, delay);

            if (finished != tcs.Task)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 等图像就绪超时({timeoutMs}ms)，按'没确认'处理");
                return ImageLoadState.Timeout;
            }

            ImageLoadState state = tcs.Task.Result ? ImageLoadState.Ready : ImageLoadState.Failed;

            // 留个探针：看得到 Ready 就说明"未上树的源也会自己解码"这条假设成立；
            // 全是 Timeout 的话，下一步就得上隐藏宿主预加载了。
            System.Diagnostics.Debug.WriteLine($"[WebAppIconCache] 图像加载 {state}，用时 {sw.ElapsedMilliseconds}ms");

            return state;
        }

        /// <summary>Uri 指向的是不是 .svg 文件（只看扩展名，缓存文件名是我们自己起的）</summary>
        public static bool IsSvgUri(Uri uri) =>
            uri.IsAbsoluteUri &&
            (uri.IsFile
                ? uri.LocalPath
                : uri.AbsoluteUri).EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 判断一段字节是不是 SVG（而不是位图）。
        /// SVG 没有魔数，只能看开头是不是 <c>&lt;?xml</c> 或 <c>&lt;svg</c> ——
        /// 跳过 UTF-8 BOM 和前导空白再比，大小写不敏感（XML 声明的写法不统一）。
        ///
        /// 为什么需要它：BitmapDecoder 解不了 SVG，但 WinUI 的 SvgImageSource 能直接显示，
        /// 所以图标管道不能像以前那样一看到 SVG 就拒收，得认出来走另一条渲染路径。
        /// </summary>
        public static bool IsSvgContent(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return false;
            }

            int i = 0;

            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                i = 3;
            }

            // 前导空白 / 换行
            while (i < bytes.Length && bytes[i] <= 0x20)
            {
                i++;
            }

            return StartsWithAscii(bytes, i, "<?xml") || StartsWithAscii(bytes, i, "<svg");
        }

        private static bool StartsWithAscii(byte[] bytes, int offset, string ascii)
        {
            if (bytes.Length - offset < ascii.Length)
            {
                return false;
            }

            for (int k = 0; k < ascii.Length; k++)
            {
                byte actual = bytes[offset + k];

                // 只把 A-Z 折成小写，别的字节原样比 —— 非字母被折会误判
                if (actual >= 'A' && actual <= 'Z')
                {
                    actual += 32;
                }

                if (actual != ascii[k])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Id 理论上是我们自己生成的，但拼进文件名前还是把非法字符剔掉</summary>
        private static string SanitizeId(string appId)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(appId.Where(c => !invalid.Contains(c)).ToArray());
        }
    }
}
