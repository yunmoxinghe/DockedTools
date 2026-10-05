using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Pages.WebApp.Browser;
using DockedTools.Features.Localization;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using DockedTools.Features.MainWindowContent.ContentArea;
using DockedTools.Features.UnifiedCalls.ContentArea;
using DockedTools.Features.Pages.Settings;
using SymbolIcon = Microsoft.UI.Xaml.Controls.SymbolIcon;
using Symbol = Microsoft.UI.Xaml.Controls.Symbol;
using Visibility = Microsoft.UI.Xaml.Visibility;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using ImageIcon = Microsoft.UI.Xaml.Controls.ImageIcon;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace DockedTools.Features.Pages.Home
{
    public sealed partial class HomePage : Page
    {
        private readonly 智能标题 _智能标题 = new();
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        /// <summary>
        /// 事件订阅是不是还挂着。
        ///
        /// 页面实例是被缓存复用的（返回主页时同一个实例重新上树），Loaded 每次都会跑。
        /// 以前只在 OnNavigatedFrom 里摘掉删除服务那两个，EventBus 那两个一直挂着 ——
        /// 于是第 N 次返回就挂了 N 份，之后建一个网页应用会触发 N 次整列重建，
        /// 而且旧实例被静态事件钉住永不释放。这里改成幂等 + 成对摘除。
        /// </summary>
        private bool _eventsSubscribed;

        public HomePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        private void SubscribeEvents()
        {
            if (_eventsSubscribed)
            {
                return;
            }

            _eventsSubscribed = true;

            WebAppDeletionService.DeletionStarting += OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted += OnDeletionCompleted;
            WebAppEventBus.ShortcutCreated += OnShortcutCreated;
            WebAppEventBus.ShortcutsRefreshRequested += OnShortcutsRefreshRequested;

            // ⭐ 图标在运行中被更新（favicon 抓到高清图 / 站点换图标）时也要跟着换，
            //    以前只有导航栏订阅它，主页得等到下次返回才看得到新图标。
            WebAppUpdateService.UpdateCompleted += OnWebAppUpdated;
        }

        private void UnsubscribeEvents()
        {
            if (!_eventsSubscribed)
            {
                return;
            }

            _eventsSubscribed = false;

            WebAppDeletionService.DeletionStarting -= OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted -= OnDeletionCompleted;
            WebAppEventBus.ShortcutCreated -= OnShortcutCreated;
            WebAppEventBus.ShortcutsRefreshRequested -= OnShortcutsRefreshRequested;
            WebAppUpdateService.UpdateCompleted -= OnWebAppUpdated;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, HomeScrollViewer, PageTitleBlock);
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            SubscribeEvents();
            UpdateVisualState();
            await LoadWebAppsAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeEvents();
        }

        /// <summary>
        /// 运行中某个网页应用的图标被换掉了（favicon 抓到更清晰的图 / 站点换了图标）。
        /// 只动那一张卡片，不整列重建 —— 整列重建会把「清空 → 重填」演一遍，看着就是抖。
        /// </summary>
        private void OnWebAppUpdated(object? sender, WebAppUpdateEventArgs e)
        {
            if (!e.UpdateType.HasFlag(WebAppUpdateType.Icon))
            {
                return;
            }

            AsyncSafety.TryEnqueue(
                DispatcherQueue,
                async () => await RefreshCardIconAsync(e.AppId),
                "HomePage",
                "IconUpdated");
        }

        /// <summary>重新取这一个应用的图标并换到它的卡片上；卡片不在列表里就什么都不做</summary>
        private async Task RefreshCardIconAsync(string appId)
        {
            var shortcuts = await WebAppShortcutStore.LoadAsync();
            WebAppShortcut? updated = shortcuts.FirstOrDefault(s => s.Id == appId);
            if (updated is null)
            {
                return;
            }

            foreach (var child in WebAppsList.Children)
            {
                if (child is SettingsCard card &&
                    card.Tag is WebAppShortcut existing &&
                    string.Equals(existing.Id, appId, StringComparison.Ordinal))
                {
                    card.Tag = updated;

                    byte[]? bytes = updated.IconBytes is { Length: > 0 } b ? b : null;

                    // 落盘一份（顶栏 / 侧边栏也按路径取图），卡片自己走内存解码
                    if (bytes is not null)
                    {
                        await WebAppIconCache.SaveAsync(updated.Id, bytes);
                    }

                    ApplyCardIcon(card, await WebAppIconCache.DecodeAsync(bytes));
                    return;
                }
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Width - _lastMeasuredWidth) < 1)
            {
                return;
            }
            UpdateVisualState();
        }

        private void UpdateVisualState()
        {
            double width = RootGrid?.ActualWidth ?? 0;
            if (width <= 0 && RootGrid != null)
            {
                width = RootGrid.ActualWidth;
            }
            if (width <= 0)
            {
                width = ActualWidth;
            }

            double normalized = (width - MinResponsiveWidth) / (MaxResponsiveWidth - MinResponsiveWidth);
            normalized = Math.Clamp(normalized, 0, 1);
            double horizontalMargin = Math.Round(MinHorizontalMargin + ((MaxHorizontalMargin - MinHorizontalMargin) * normalized));

            if (Math.Abs(horizontalMargin - _lastAppliedMargin) > 0.01)
            {
                PageContentPanel.Margin = new Thickness(horizontalMargin, 0, horizontalMargin, 0);
                _lastAppliedMargin = horizontalMargin;
            }
            _lastMeasuredWidth = width;
        }

        /// <summary>
        /// ⭐ 任务 6.4：快捷方式创建事件（使用 AsyncSafety 包装 DispatcherQueue.TryEnqueue）
        /// </summary>
        private void OnShortcutCreated(object? sender, WebAppShortcut shortcut)
        {
            AsyncSafety.TryEnqueue(
                DispatcherQueue,
                async () => await LoadWebAppsAsync(),
                "HomePage",
                "ShortcutCreated");
        }

        /// <summary>
        /// ⭐ 任务 6.4：快捷方式刷新请求事件（使用 AsyncSafety 包装 DispatcherQueue.TryEnqueue）
        /// </summary>
        private void OnShortcutsRefreshRequested(object? sender, EventArgs e)
        {
            AsyncSafety.TryEnqueue(
                DispatcherQueue,
                async () => await LoadWebAppsAsync(),
                "HomePage",
                "ShortcutsRefresh");
        }

        /// <summary>⏱️ 临时探针用的计时起点：每次重建列表都归零</summary>
        private static readonly System.Diagnostics.Stopwatch _iconProbe = System.Diagnostics.Stopwatch.StartNew();

        private async Task LoadWebAppsAsync()
        {
            _iconProbe.Restart();
            var shortcuts = await WebAppShortcutStore.LoadAsync();

            if (shortcuts.Count == 0)
            {
                WebAppsList.Children.Clear();
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            EmptyText.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine($"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 列表数据到手 {shortcuts.Count} 条");

            // ⭐ 图标【全部并发解码完】再一次性建整列 —— 这是 git 里 6c51d5a 那一版的路子
            //    （InMemoryRandomAccessStream 内存解码），只是在此基础上补了 SVG 与磁盘缓存。
            //
            //    踩过的坑一：await 写在构建循环里 ⇒ 每取到一张就 Add 一张，第一张孤零零先蹦。
            //    踩过的坑二：改成 Task.WhenAll 落盘 + Uri 解码 ⇒ 它必须等最慢那张，
            //    而且 SaveAsync 命中「按主题重画」时还要扛渲染内核冷启动（实测 1.4s+）。
            //    踩过的坑三：两阶段（先出整列、后台逐张重画）⇒ 被重画的那张就是换得最晚的那张。
            //    踩过的坑四：只读磁盘缓存 + 每张自己走 Uri 异步解码 ⇒ 整列 Add 完 +114ms，
            //    最后一张就绪 +865ms —— 中间 750ms 卡片全是空图标位，然后一张张亮起来，
            //    观感依旧是「某个图标最后一个蹦出来」（实测日志 dt-debug15）。
            //
            //    所以：解码是并发的（不排队），但上树是一次性的（不逐个亮）。
            //    整列出现那一刻，所有图标已经在内存里了。
            var decoding = new Task<Microsoft.UI.Xaml.Media.ImageSource?>[shortcuts.Count];

            for (int i = 0; i < shortcuts.Count; i++)
            {
                decoding[i] = DecodeIconAsync(shortcuts[i]);
            }

            Microsoft.UI.Xaml.Media.ImageSource?[] icons = await Task.WhenAll(decoding);
            System.Diagnostics.Debug.WriteLine($"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 图标全部解码完");

            // ⭐ 整列换血时先摘掉增删动画：那套淡入 + 重排是给单条增删用的，
            //    整列重建时它会把「清空 → 重填」完整演一遍，抖得比闪还明显。
            Microsoft.UI.Xaml.Media.Animation.TransitionCollection? transitions = WebAppsList.ChildrenTransitions;
            WebAppsList.ChildrenTransitions = null;
            WebAppsList.Children.Clear();

            for (int i = 0; i < shortcuts.Count; i++)
            {
                WebAppShortcut shortcut = shortcuts[i];

                var card = new SettingsCard
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = shortcut.Name,
                    Description = shortcut.Url,
                    IsClickEnabled = true,
                    Tag = shortcut // ⭐ 添加 Tag 用于删除时识别
                };

                card.Click += (sender, e) => OnCardClick(shortcut);

                ApplyCardIcon(card, icons[i]);

                WebAppsList.Children.Add(card);
            }

            WebAppsList.ChildrenTransitions = transitions;
            System.Diagnostics.Debug.WriteLine($"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 整列 Add 完");

            BackfillMissingIcons(shortcuts);
            _ = UpgradeCardIconsAsync(shortcuts);
        }

        /// <summary>
        /// 后台把【卡片档】补出来 —— 主页卡片单独用的那一套大图。
        ///
        /// 串行是刻意的：矢量那条路要过渲染内核（进程内单例，本来也会排成一路），
        /// 位图那条路是 WIC 编解码，25 个一起上会把首屏 CPU 抢光。
        ///
        /// 卡片档到位后换上去只是「变清楚」，不是「从无到有」——
        /// 列表早就用通用档完整显示着了，这里不会重现「某张最后蹦出来」。
        /// </summary>
        private async Task UpgradeCardIconsAsync(IReadOnlyList<WebAppShortcut> shortcuts)
        {
            foreach (WebAppShortcut shortcut in shortcuts)
            {
                if (shortcut.IconBytes is not { Length: > 0 } iconBytes)
                {
                    continue;
                }

                string appId = shortcut.Id;

                if (WebAppIconCache.TryGetCardPath(appId) is not null)
                {
                    continue;
                }

                string? path = await WebAppIconCache.EnsureCardAsync(appId, iconBytes);
                if (path is null)
                {
                    continue;
                }

                Microsoft.UI.Xaml.Media.ImageSource? source =
                    await WebAppIconCache.DecodeAsync(WebAppIconCache.TryReadCardBytes(appId));
                if (source is null)
                {
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"[HomeIcon] 卡片档就绪 {shortcut.Name}");

                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var child in WebAppsList.Children)
                    {
                        if (child is SettingsCard card &&
                            card.Tag is WebAppShortcut tagged &&
                            string.Equals(tagged.Id, appId, StringComparison.Ordinal))
                        {
                            ApplyCardIcon(card, source);
                            return;
                        }
                    }
                });
            }
        }

        /// <summary>补抓过但没抓到的 appId —— 别每次返回主页都再打一次网络</summary>
        private static readonly HashSet<string> IconBackfillMissed = new(StringComparer.Ordinal);

        private static readonly System.Net.Http.HttpClient IconBackfillClient = CreateIconBackfillClient();

        private static System.Net.Http.HttpClient CreateIconBackfillClient()
        {
            var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            // ⭐ 必须装成浏览器：不少站点对裸 UA 直接 403（实测 chatgpt.com / gpt.com 就是，
            //    带这套头是 200 32038B，不带是 403）—— 导航栏那条 "图标加载失败" 就是这个坑。
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");

            return client;
        }

        /// <summary>
        /// 数据层就没图的快捷方式（典型是从没被打开过 ⇒ favicon 压根没抓过）后台补抓一次。
        ///
        /// 只为填「主页上一片地球」这个洞：成功就落盘 + 写回快捷方式 + 换那一张卡片，
        /// 失败就记进 <see cref="IconBackfillMissed"/>，本次进程内不再试第二次。
        /// 全程 fire-and-forget，绝挡列表。
        /// </summary>
        private void BackfillMissingIcons(IReadOnlyList<WebAppShortcut> shortcuts)
        {
            foreach (WebAppShortcut shortcut in shortcuts)
            {
                if (shortcut.IconBytes is { Length: > 0 })
                {
                    continue;
                }

                lock (IconBackfillMissed)
                {
                    if (!IconBackfillMissed.Add(shortcut.Id))
                    {
                        continue;
                    }
                }

                _ = BackfillIconAsync(shortcut.Id, shortcut.Url);
            }
        }

        private async Task BackfillIconAsync(string appId, string url)
        {
            byte[]? bytes = null;

            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    using var response = await IconBackfillClient.GetAsync(new Uri($"https://{uri.Host}/favicon.ico"));

                    if (response.IsSuccessStatusCode)
                    {
                        bytes = await response.Content.ReadAsByteArrayAsync();
                        System.Diagnostics.Debug.WriteLine($"[HomeIcon] 补抓成功 {uri.Host} {bytes.Length} 字节");
                    }
                    else
                    {
                        // 非 2xx 不是异常，不打印的话这类失败是完全静默的，排查时只剩「还是地球」
                        System.Diagnostics.Debug.WriteLine(
                            $"[HomeIcon] 补抓失败 {uri.Host}: {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomeIcon] 补抓失败 {url}: {ex.Message}");
            }

            if (bytes is not { Length: > 0 })
            {
                return;
            }

            // 落盘一份（顶栏 / 侧边栏也按路径取图）
            WebAppIconCache.Save(appId, bytes);

            // 字节写回快捷方式，下次进主页不用再抓
            await WebAppShortcutStore.UpdateAsync(items =>
            {
                var next = new List<WebAppShortcut>(items.Count);
                bool changed = false;

                foreach (WebAppShortcut item in items)
                {
                    if (string.Equals(item.Id, appId, StringComparison.Ordinal) &&
                        item.IconBytes is not { Length: > 0 })
                    {
                        next.Add(item with { IconBytes = bytes });
                        changed = true;
                    }
                    else
                    {
                        next.Add(item);
                    }
                }

                return changed ? next : null;
            });

            Microsoft.UI.Xaml.Media.ImageSource? source = await WebAppIconCache.DecodeAsync(bytes);
            if (source is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var child in WebAppsList.Children)
                {
                    if (child is SettingsCard card &&
                        card.Tag is WebAppShortcut tagged &&
                        string.Equals(tagged.Id, appId, StringComparison.Ordinal))
                    {
                        ApplyCardIcon(card, source);
                        return;
                    }
                }
            });
        }

        /// <summary>
        /// 取一个应用的图标源：优先磁盘缓存，没有就用快捷方式自带的字节（顺手补写一份缓存）。
        /// 全程不碰渲染内核 —— 主页出列表不该被光栅化拖住。
        /// </summary>
        private static async Task<Microsoft.UI.Xaml.Media.ImageSource?> DecodeIconAsync(WebAppShortcut shortcut)
        {
            // ⭐ 卡片档优先：主页 SettingsCard 的图标位被 Viewbox 卡在 20×20，
            //    高 DPI 下得用更大那套才不发虚（尺寸依据见 WebAppIconCache.CardIconSize）。
            //    顶栏 / 侧边栏那套是给 16~20px 用的，拿过来放大就是「糊」。
            byte[]? bytes = WebAppIconCache.TryReadCardBytes(shortcut.Id)
                            ?? WebAppIconCache.TryReadCachedBytes(shortcut.Id);

            if (bytes is null && shortcut.IconBytes is { Length: > 0 } iconBytes)
            {
                // 缓存缺失才落一次盘（同步、纯磁盘写，毫秒级），别处（顶栏 / 侧边栏）也要用
                WebAppIconCache.Save(shortcut.Id, iconBytes);
                bytes = iconBytes;
            }

            return await WebAppIconCache.DecodeAsync(bytes);
        }

        /// <summary>
        /// 把图标挂到卡片上；拿不到 / 解不出来一律用地球图标占位。
        ///
        /// 传进来的是【已经解码完】的源 —— 解码在 DecodeAsync 里做完了，
        /// 解不出来就直接是 null，所以这里不会再出现「挂上去了但图还没解码完」的空图标位。
        /// </summary>
        private void ApplyCardIcon(SettingsCard card, Microsoft.UI.Xaml.Media.ImageSource? source)
        {
            string name = card.Header as string ?? "?";

            if (source is not null)
            {
                System.Diagnostics.Debug.WriteLine($"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 挂上(已解码) {name}");
                card.HeaderIcon = new ImageIcon { Source = source };
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 地球 {name}");
            card.HeaderIcon = new SymbolIcon(Symbol.Globe);
        }

        private void OnCardClick(WebAppShortcut shortcut)
        {
            // ✅ 使用 ContentAreaService 导航，支持页面缓存和单实例
            // 使用用户设置的动画类型
            var animationType = ExperimentalSettings.FrameNavigationAnimation;
            var transitionInfo = GetNavigationTransitionInfo(animationType);
            ContentAreaService.Navigate(typeof(WebBrowserPage), shortcut, transitionInfo);
        }

        /// <summary>
        /// 根据动画类型获取对应的 NavigationTransitionInfo
        /// </summary>
        private Microsoft.UI.Xaml.Media.Animation.NavigationTransitionInfo GetNavigationTransitionInfo(FrameAnimationType animationType)
        {
            return animationType switch
            {
                FrameAnimationType.None => new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo(),
                FrameAnimationType.EntranceTransition => new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo(),
                FrameAnimationType.SlideFromRight => new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo 
                { 
                    Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight 
                },
                FrameAnimationType.SlideFromLeft => new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo 
                { 
                    Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromLeft 
                },
                FrameAnimationType.SlideFromBottom => new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo 
                { 
                    Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromBottom 
                },
                FrameAnimationType.DrillIn => new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo(),
                FrameAnimationType.FadeInOut => new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo(),
                FrameAnimationType.ScaleAnimation => new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo(),
                _ => new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo()
            };
        }

        private void OnDeletionStarting(object? sender, string appId)
        {
            // 找到要删除的卡片并播放淡出动画
            foreach (var child in WebAppsList.Children)
            {
                if (child is SettingsCard card && 
                    card.Tag is WebAppShortcut shortcut && 
                    shortcut.Id == appId)
                {
                    // 播放淡出动画
                    var fadeOutAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(250))
                    };

                    var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                    storyboard.Children.Add(fadeOutAnimation);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOutAnimation, card);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOutAnimation, "Opacity");
                    storyboard.Begin();
                    break;
                }
            }
        }

        private void OnDeletionCompleted(object? sender, string appId)
        {
            // 找到卡片并移除
            SettingsCard? cardToRemove = null;
            foreach (var child in WebAppsList.Children)
            {
                if (child is SettingsCard card && 
                    card.Tag is WebAppShortcut shortcut && 
                    shortcut.Id == appId)
                {
                    cardToRemove = card;
                    break;
                }
            }

            if (cardToRemove != null)
            {
                // 移除元素（触发补位动画）
                WebAppsList.Children.Remove(cardToRemove);

                // 更新空状态显示
                EmptyText.Visibility = WebAppsList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
