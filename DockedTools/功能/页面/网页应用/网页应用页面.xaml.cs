using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Localization;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace DockedTools.Features.Pages.WebApp
{
    /// <summary>
    /// 网页应用创建页面
    /// Native AOT 兼容：使用 Regex 源生成器代替运行时编译
    /// </summary>
    public sealed partial class WebAppPage : Page
    {
        private const float IconCornerRadius = 14f;
        private static readonly HttpClient HttpClient = CreateHttpClient();

        // ✅ AOT 兼容：使用 Regex 源生成器（避免运行时编译）
        [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex TitleRegex();

        [GeneratedRegex("\\s+")]
        private static partial Regex WhitespaceRegex();

        [GeneratedRegex("<link\\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex LinkTagRegex();

        [GeneratedRegex("(\\d+)x(\\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex SizeFormatRegex();

        [GeneratedRegex("(['\"])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex HtmlAttributeRegex();

        private CancellationTokenSource? _urlLookupCts;
        private CompositionRoundedRectangleGeometry? _iconClipGeometry;
        private bool _hasCustomIcon;
        private bool _isProgrammaticNameUpdate;
        private bool _isAppNameManuallyEdited;
        private string _lastAutoAppName = string.Empty;
        private byte[]? _currentIconBytes;

        public WebAppPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            System.Diagnostics.Debug.WriteLine($"WebAppPage.OnNavigatedTo called with parameter: {e.Parameter}");

            if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                System.Diagnostics.Debug.WriteLine($"WebAppPage: setting URL to: {url}");
                WebsiteUrlTextBox.Text = url;
            }
        }

        private async void WebsiteUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshWebsiteMetadataAsync(withDelay: true);
        }

        private void AppNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isProgrammaticNameUpdate)
            {
                return;
            }

            string current = AppNameTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(current))
            {
                _isAppNameManuallyEdited = false;
                return;
            }

            _isAppNameManuallyEdited = !string.Equals(current, _lastAutoAppName, StringComparison.Ordinal);
        }

        private async void RestoreAutoIconButton_Click(object sender, RoutedEventArgs e)
        {
            _hasCustomIcon = false;
            LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_IconRestored");
            await RefreshWebsiteMetadataAsync(withDelay: false);
        }

        private async Task RefreshWebsiteMetadataAsync(bool withDelay)
        {
            _urlLookupCts?.Cancel();
            _urlLookupCts?.Dispose();

            string rawInput = WebsiteUrlTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                if (!_hasCustomIcon)
                {
                    ShowFallbackIcon();
                }

                LookupStatusText.Text = string.Empty;
                return;
            }

            if (!TryNormalizeWebsiteUrl(rawInput, out Uri websiteUri))
            {
                if (!_hasCustomIcon)
                {
                    ShowFallbackIcon();
                }

                LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_InvalidUrl");
                return;
            }

            SetAppNameIfAutoMode(websiteUri.Host);

            var cts = new CancellationTokenSource();
            _urlLookupCts = cts;

            try
            {
                LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_FetchingInfo");
                if (withDelay)
                {
                    await Task.Delay(450, cts.Token);
                }

                WebsiteMetadata metadata = await FetchWebsiteMetadataAsync(websiteUri, cts.Token);

                if (!ReferenceEquals(_urlLookupCts, cts))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    SetAppNameIfAutoMode(metadata.Title);
                }

                if (metadata.IconBytes is { Length: > 0 })
                {
                    if (!_hasCustomIcon)
                    {
                        await ShowWebsiteIconAsync(metadata.IconBytes);
                    }

                    LookupStatusText.Text = _hasCustomIcon
                        ? LocalizationHelper.GetString("WebAppPage_FetchedWithCustomIcon")
                        : LocalizationHelper.GetString("WebAppPage_FetchedWithIcon");
                }
                else
                {
                    if (!_hasCustomIcon)
                    {
                        ShowFallbackIcon();
                    }

                    LookupStatusText.Text = string.IsNullOrWhiteSpace(metadata.Error)
                        ? LocalizationHelper.GetString("WebAppPage_FetchedNoIcon")
                        : string.Format(LocalizationHelper.GetString("WebAppPage_PartialSuccess"), metadata.Error);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_hasCustomIcon)
                {
                    ShowFallbackIcon();
                }

                LookupStatusText.Text = string.Format(LocalizationHelper.GetString("WebAppPage_FetchFailed"), ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_urlLookupCts, cts))
                {
                    _urlLookupCts = null;
                }

                cts.Dispose();
            }
        }

        private async void ChooseIconButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".ico");
                picker.FileTypeFilter.Add(".svg"); // 矢量图标，显示侧走 SvgImageSource

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_CannotOpenPicker");
                    return;
                }

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                IBuffer buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = buffer.ToArray();
                if (bytes.Length == 0)
                {
                    LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_EmptyIconFile");
                    return;
                }

                _hasCustomIcon = true;
                await ShowWebsiteIconAsync(bytes);
                LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_IconReplaced");
            }
            catch (Exception ex)
            {
                LookupStatusText.Text = string.Format(LocalizationHelper.GetString("WebAppPage_IconReplaceFailed"), ex.Message);
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            string name = AppNameTextBox.Text?.Trim() ?? string.Empty;
            string rawUrl = WebsiteUrlTextBox.Text?.Trim() ?? string.Empty;

            if (!TryNormalizeWebsiteUrl(rawUrl, out Uri websiteUri))
            {
                LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_InvalidUrl");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = websiteUri.Host;
            }

            var shortcut = new WebAppShortcut(
                Guid.NewGuid().ToString("N"),
                name,
                websiteUri.AbsoluteUri,
                _currentIconBytes);

            WebAppEventBus.PublishShortcutCreated(shortcut);
            LookupStatusText.Text = LocalizationHelper.GetString("WebAppPage_ShortcutCreated");
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            return client;
        }

        private static bool TryNormalizeWebsiteUrl(string rawInput, out Uri uri)
        {
            uri = null!;
            string candidate = rawInput;
            if (!candidate.Contains("://", StringComparison.Ordinal))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
            {
                return false;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.Host))
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        private void SetAppNameIfAutoMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string current = AppNameTextBox.Text?.Trim() ?? string.Empty;
            if (_isAppNameManuallyEdited && !string.Equals(current, _lastAutoAppName, StringComparison.Ordinal))
            {
                return;
            }

            _isProgrammaticNameUpdate = true;
            try
            {
                AppNameTextBox.Text = value;
            }
            finally
            {
                _isProgrammaticNameUpdate = false;
            }

            _lastAutoAppName = value.Trim();
            _isAppNameManuallyEdited = false;
        }

        private static async Task<WebsiteMetadata> FetchWebsiteMetadataAsync(Uri websiteUri, CancellationToken cancellationToken)
        {
            string html = string.Empty;
            string title = string.Empty;
            string? error = null;

            try
            {
                using HttpResponseMessage pageResponse = await HttpClient.GetAsync(websiteUri, cancellationToken);
                pageResponse.EnsureSuccessStatusCode();
                html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
                title = ParseTitle(html);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = websiteUri.Host;
            }

            byte[]? iconBytes = await TryDownloadBestIconAsync(websiteUri, html, cancellationToken);
            return new WebsiteMetadata(title, iconBytes, error);
        }

        private static string ParseTitle(string html)
        {
            Match titleMatch = TitleRegex().Match(html);
            if (!titleMatch.Success)
            {
                return string.Empty;
            }

            string decoded = WebUtility.HtmlDecode(titleMatch.Groups[1].Value);
            return WhitespaceRegex().Replace(decoded, " ").Trim();
        }

        private static async Task<byte[]?> TryDownloadBestIconAsync(Uri websiteUri, string html, CancellationToken cancellationToken)
        {
            List<Uri> candidates = ParseIconCandidates(websiteUri, html);

            Uri faviconUri = new Uri(websiteUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
            if (!candidates.Contains(faviconUri))
            {
                candidates.Add(faviconUri);
            }

            foreach (Uri candidate in candidates)
            {
                try
                {
                    using HttpResponseMessage response = await HttpClient.GetAsync(candidate, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    string? contentType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrWhiteSpace(contentType))
                    {
                        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        // SVG 不再跳过：显示侧走 SvgImageSource 能直接渲染，矢量图最清晰
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
                    {
                        continue;
                    }

                    // 位图要求能解码；SVG 单独放行（BitmapDecoder 解不了它）
                    if (!WebAppIconCache.IsSvgContent(bytes) && !await CanDecodeBitmapAsync(bytes))
                    {
                        continue;
                    }

                    return bytes;
                }
                catch
                {
                }
            }

            return null;
        }

        private static async Task<bool> CanDecodeBitmapAsync(byte[] bytes)
        {
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

        private static List<Uri> ParseIconCandidates(Uri websiteUri, string html)
        {
            var entries = new List<(Uri Uri, int Score)>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return new List<Uri>();
            }

            MatchCollection linkMatches = LinkTagRegex().Matches(html);
            foreach (Match linkMatch in linkMatches)
            {
                string tag = linkMatch.Value;
                string rel = GetHtmlAttribute(tag, "rel");
                string href = GetHtmlAttribute(tag, "href");
                if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                if (!rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(websiteUri, WebUtility.HtmlDecode(href), out Uri? iconUri))
                {
                    continue;
                }

                int sizeScore = ParseLargestIconSize(GetHtmlAttribute(tag, "sizes"));
                int relScore = rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ? 20000 : 10000;
                entries.Add((iconUri, relScore + sizeScore));
            }

            return entries
                .OrderByDescending(e => e.Score)
                .Select(e => e.Uri)
                .Distinct()
                .ToList();
        }

        private static string GetHtmlAttribute(string tag, string attributeName)
        {
            string pattern = attributeName + "\\s*=\\s*";
            Match match = HtmlAttributeRegex().Match(tag, pattern.Length - 5); // 从属性名后开始匹配
            
            // 手动查找属性值（更精确的匹配）
            int attrIndex = tag.IndexOf(attributeName, StringComparison.OrdinalIgnoreCase);
            if (attrIndex == -1)
                return string.Empty;

            int equalsIndex = tag.IndexOf('=', attrIndex);
            if (equalsIndex == -1)
                return string.Empty;

            int startQuote = -1;
            char quoteChar = '\0';
            
            // 查找起始引号
            for (int i = equalsIndex + 1; i < tag.Length; i++)
            {
                if (tag[i] == '"' || tag[i] == '\'')
                {
                    quoteChar = tag[i];
                    startQuote = i;
                    break;
                }
                else if (!char.IsWhiteSpace(tag[i]))
                {
                    break; // 无引号的属性值
                }
            }

            if (startQuote == -1)
                return string.Empty;

            // 查找结束引号
            int endQuote = tag.IndexOf(quoteChar, startQuote + 1);
            if (endQuote == -1)
                return string.Empty;

            return tag.Substring(startQuote + 1, endQuote - startQuote - 1).Trim();
        }

        private static int ParseLargestIconSize(string sizesValue)
        {
            if (string.IsNullOrWhiteSpace(sizesValue))
            {
                return 0;
            }

            int best = 0;
            MatchCollection matches = SizeFormatRegex().Matches(sizesValue);
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out int width) &&
                    int.TryParse(match.Groups[2].Value, out int height))
                {
                    best = Math.Max(best, width * height);
                }
            }

            return best;
        }

        private async Task ShowWebsiteIconAsync(byte[] iconBytes)
        {
            _currentIconBytes = iconBytes.ToArray();

            // SVG 走 SvgImageSource；它也没有位图那种「四角不透明 ⇒ 要圆角」的问题，
            // 圆角判断（要解码像素）对矢量图直接跳过。
            bool shouldRound = !WebAppIconCache.IsSvgContent(iconBytes)
                               && await ShouldRoundIconCornersAsync(iconBytes);

            ImageSource source;
            if (WebAppIconCache.IsSvgContent(iconBytes))
            {
                var svg = new SvgImageSource();
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(iconBytes.AsBuffer());
                stream.Seek(0);
                await svg.SetSourceAsync(stream);
                source = svg;
            }
            else
            {
                var bitmap = new BitmapImage();
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(iconBytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                source = bitmap;
            }

            SiteIconImage.Source = source;
            SiteIconImage.Visibility = Visibility.Visible;
            SiteIconFallback.Visibility = Visibility.Collapsed;
            ApplyIconClip(shouldRound);
        }

        private static async Task<bool> ShouldRoundIconCornersAsync(byte[] iconBytes)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(iconBytes.AsBuffer());
                stream.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = pixelData.DetachPixelData();
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] < 255)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyIconClip(bool shouldRound)
        {
            var visual = ElementCompositionPreview.GetElementVisual(SiteIconImage);
            if (!shouldRound)
            {
                visual.Clip = null;
                _iconClipGeometry = null;
                return;
            }

            if (_iconClipGeometry == null)
            {
                Compositor compositor = visual.Compositor;
                _iconClipGeometry = compositor.CreateRoundedRectangleGeometry();
                _iconClipGeometry.CornerRadius = new Vector2(IconCornerRadius, IconCornerRadius);
                _iconClipGeometry.Offset = Vector2.Zero;
                visual.Clip = compositor.CreateGeometricClip(_iconClipGeometry);
            }

            float width = SiteIconImage.ActualWidth > 0 ? (float)SiteIconImage.ActualWidth : 64f;
            float height = SiteIconImage.ActualHeight > 0 ? (float)SiteIconImage.ActualHeight : 64f;
            _iconClipGeometry.Size = new Vector2(width, height);
        }

        private void ShowFallbackIcon()
        {
            _currentIconBytes = null;
            SiteIconImage.Source = null;
            SiteIconImage.Visibility = Visibility.Collapsed;
            SiteIconFallback.Visibility = Visibility.Visible;
            ApplyIconClip(false);
        }

        /// <summary>
        /// ✅ AOT 兼容：使用 LibraryImport 代替 DllImport
        /// LibraryImport 使用源生成器生成编组代码，完全支持 Native AOT
        /// </summary>
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        private sealed record WebsiteMetadata(string Title, byte[]? IconBytes, string? Error);
    }
}
