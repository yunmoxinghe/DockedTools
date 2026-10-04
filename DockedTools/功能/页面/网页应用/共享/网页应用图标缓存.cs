using Microsoft.UI.Xaml;          // RoutedEventHandler / ExceptionRoutedEventHandler
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
                if (string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
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

        private static string BuildFileName(string appId, byte[] bytes) =>
            SanitizeId(appId) + "-" + ComputeContentHash(bytes) + DetectExtension(bytes);

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
