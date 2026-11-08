using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
using ImageIcon = Microsoft.UI.Xaml.Controls.ImageIcon;
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;

namespace DockedTools.Features.Pages.Home
{
    public sealed partial class HomePage : Page, ICloseReleasable
    {
        private readonly 智能标题 _智能标题 = new();
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        // ==================== 入口列表：真虚拟化 ====================
        //
        // 【跟上一版的区别】
        // 上一版是「首屏 4 条 + 后台每批 6 条往 StackPanel.Children 里塞」，虽然把补批和滚动
        // 解耦了，但元素还是全量上树：120 条就是 120 个 SettingsCard + 120 张图，滚动时
        // 每一帧都在拖着一整棵与视口无关的树。
        //
        // 现在交给 ListView：ItemsSource 一次性给全量（ExtentHeight 首帧就按全量估算，
        // 滚动条行程稳定），实际元素只有视口附近那十几个，滚出视口的容器被回收复用。
        //
        // ⚠️ 【补批 / 解码仍然不能挂在 ScrollViewer.ViewChanged 上】
        //    旧版「滚到距底 240px 才补」的教训：拖到底 → 触发补批 → ExtentHeight 变大 →
        //    滑块被顶回去，实测连续四次「拖到底」只到 23.5% → 51.9% → 65.1% → 77.8%。
        //    这里数据一次性给全，图标解码完全在后台跑，跟滚动位置无关。
        //
        // 【图标与容器复用】
        // 每个数据项自己是「图标的所有者」（HomeShortcutItem.Icon），模板用 x:Bind 单向绑定。
        // 容器被回收给另一项时，x:Bind 跟着新数据项重新求值，旧项的解码结果只会写回旧项，
        // 不会串到当前显示这一项上；也不存在「闭包捕获旧项」的处理器泄漏。
        //
        // 【首屏可交互】
        // 先把首屏那一屏的图标解码完，再把 ItemsSource 交给 ListView —— 首帧就是「带图标的卡片」，
        // 不会重现"卡片先到、图标最后才亮"。剩下的图标后台按批解码，只写回各自的数据项。

        /// <summary>首屏先解码多少张图标（解码完才把列表交给 ListView）</summary>
        private const int FirstScreenIconCount = 8;

        /// <summary>后台每批解码多少张图标</summary>
        private const int IconBatchSize = 6;

        /// <summary>本轮列表用的图标注色主题（换主题会重建整列，那时才变）</summary>
        private ElementTheme _listIconTheme = ElementTheme.Default;

        /// <summary>列表世代：每次重建 +1，用来作废旧世代还在飞的解码 / 补抓</summary>
        private int _listGeneration;

        /// <summary>当前绑定到 ListView 的数据项</summary>
        private ObservableCollection<HomeShortcutItem> _items = new();

        /// <summary>appId → 数据项，图标更新 / 删除时按 Id 找项，不用遍历视口</summary>
        private Dictionary<string, HomeShortcutItem> _itemById = new(StringComparer.Ordinal);

        /// <summary>
        /// 已解码的图标源（appId → ImageSource）。只在 UI 线程读写。
        /// 解码结果缓存在这里，容器被复用 / 再次滚回视口时能立刻拿到图，不用重复解码。
        /// </summary>
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.Ordinal);

        /// <summary>
        /// 正在解码的 (appId, 世代)。同一个应用同时只解一次，避免首屏预热和后台批次重复解码。
        /// 带上世代是为了让「重建前发出的解码」不会挡住「重建后同一应用」的解码。
        /// </summary>
        private readonly HashSet<(string Id, int Generation)> _iconRequests = new();

        /// <summary>ListView 内部那个 ScrollViewer（顶栏智能标题要挂它的 ViewChanged）</summary>
        private ScrollViewer? _homeScrollViewer;

        /// <summary>正在播放的删除淡出动画（容器会被复用，收尾时必须把 Opacity 还原）</summary>
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _deletionStoryboard;

        /// <summary>正在淡出的卡片（删除完成后还原它的 Opacity）</summary>
        private SettingsCard? _deletionCard;

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
            HomeList.ItemsSource = _items;
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

            ActualThemeChanged += OnActualThemeChanged;
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

            ActualThemeChanged -= OnActualThemeChanged;
        }

        /// <summary>
        /// 出缓存 / 主窗口关闭时的彻底放手：本页面挂在静态事件上的那几条订阅必须摘掉。
        ///
        /// 【为什么不能只靠 Unloaded】
        /// 退订原来只写在 Unloaded 里。窗口关闭那一拍 Unloaded 不保证触发
        /// （见 <see cref="ICloseReleasable"/>），于是这座主页会被 WebAppEventBus /
        /// WebAppDeletionService / WebAppUpdateService 三个静态事件源钉住，
        /// 连带它 Loaded 时灌进来的整份快捷方式列表和图标。
        /// <see cref="UnsubscribeEvents"/> 本身带 _eventsSubscribed 标志，重复调用是空操作。
        /// </summary>
        public void ReleaseOnClose()
        {
            UnsubscribeEvents();
            ResetDeletionAnimation();
            _listGeneration++;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // ListView 的 ScrollViewer 在它的模板里，不在页面命名空间里。
            // 先 ApplyTemplate 把模板实例化出来；万一这拍还没有（极端情况），退到 Loaded 再挂一次。
            if (GetHomeScrollViewer() is { } scrollViewer)
            {
                _智能标题.Setup(this, scrollViewer, PageTitleBlock);
            }
            else
            {
                HomeList.Loaded += OnHomeListLoadedForTitle;
            }
        }

        private void OnHomeListLoadedForTitle(object sender, RoutedEventArgs e)
        {
            HomeList.Loaded -= OnHomeListLoadedForTitle;

            if (GetHomeScrollViewer() is { } scrollViewer)
            {
                _智能标题.Setup(this, scrollViewer, PageTitleBlock);
            }
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

            // 出树就把世代作废：还在飞的解码 / 补抓回来时不该再往已经离屏的列表里写。
            ResetDeletionAnimation();
            HomeList.Loaded -= OnHomeListLoadedForTitle;
            _listGeneration++;
        }

        /// <summary>找到 ListView 模板里的那个 ScrollViewer（顶栏标题的滚动联动要用）</summary>
        private ScrollViewer? GetHomeScrollViewer()
        {
            if (_homeScrollViewer is not null)
            {
                return _homeScrollViewer;
            }

            HomeList.ApplyTemplate();
            _homeScrollViewer = FindDescendant<ScrollViewer>(HomeList);
            return _homeScrollViewer;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                if (FindDescendant<T>(child) is { } found)
                {
                    return found;
                }
            }

            return null;
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

        /// <summary>重新取这一个应用的图标并换到它的数据项上；这一项不在列表里就什么都不做</summary>
        private async Task RefreshCardIconAsync(string appId)
        {
            var shortcuts = await WebAppShortcutStore.LoadAsync();
            WebAppShortcut? updated = shortcuts.FirstOrDefault(s => s.Id == appId);
            if (updated is null)
            {
                return;
            }

            byte[]? bytes = updated.IconBytes is { Length: > 0 } b ? b : null;

            // 落盘一份（顶栏 / 侧边栏也按路径取图），卡片自己走内存解码
            if (bytes is not null)
            {
                await WebAppIconCache.SaveAsync(updated.Id, bytes, ActualTheme);
            }

            // ⭐ 读【本页主题那一版】，别直接解码 IconBytes ——
            //    那份是没染过色的原始字节，深色主题下会是黑图标。
            ImageSource? source = await WebAppIconCache.DecodeAsync(
                WebAppIconCache.TryReadCachedBytes(updated.Id, ActualTheme) ?? bytes);

            if (!_itemById.TryGetValue(appId, out HomeShortcutItem? item))
            {
                return;
            }

            item.Shortcut = updated;

            if (source is not null)
            {
                _iconCache[appId] = source;
                item.Icon = new ImageIcon { Source = source };
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
                // ⚠️ 左右留白必须套在【视口内部】（ListView.Padding），不能套在 ListView.Margin 上。
                //    ListView 的滚动条属于它内部那个 ScrollViewer，滚动条是贴在 ScrollViewer 视口的
                //    右边缘"覆盖"显示的（官方文档：可见时覆盖内容 16px）。给 ListView 加 Margin
                //    会把整个视口一起缩窄，滚动条也就跟着离窗口右边缘越来越远 —— 实测 36 DIP 的
                //    Margin 让滚动条右边缘比内容区右边缘内缩了 54px（150% DPI）。
                //    Padding 只缩内容区，ScrollViewer 仍然铺满，滚动条才会贴在右边缘。
                //    注意：实测 ListView.Padding 同时作用于 Header / Footer（标题左边缘和卡片
                //    左边缘本来就对齐），所以这里不要再给 Header / Footer 加 Margin，否则二次内缩。
                HomeList.Padding = new Thickness(horizontalMargin, 0, horizontalMargin, 0);

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
            int generation = ++_listGeneration;
            _iconProbe.Restart();

            var shortcuts = await WebAppShortcutStore.LoadAsync();

            // 期间又触发了一轮重建（连点新建 / 换主题 / 刷新）：这一轮作废，别去动新列表
            if (generation != _listGeneration)
            {
                return;
            }

            _listIconTheme = ActualTheme;
            _iconCache.Clear();
            _iconRequests.Clear();

            if (shortcuts.Count == 0)
            {
                _itemById = new Dictionary<string, HomeShortcutItem>(StringComparer.Ordinal);
                _items = new ObservableCollection<HomeShortcutItem>();
                HomeList.ItemsSource = _items;
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            EmptyText.Visibility = Visibility.Collapsed;

            var items = new List<HomeShortcutItem>(shortcuts.Count);
            foreach (WebAppShortcut shortcut in shortcuts)
            {
                items.Add(new HomeShortcutItem(shortcut));
            }

            // ⭐ 首屏图标先解码完，再把 ItemsSource 交出去 —— 首帧就是「带图标的卡片」，
            //    不会出现卡片先到、图标后亮。解码数量控制在一屏左右，进页面的等待跟以前的首批相当。
            int warm = Math.Min(FirstScreenIconCount, items.Count);
            await Task.WhenAll(items.Take(warm).Select(item => PrimeIconAsync(item, generation)));

            if (generation != _listGeneration)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 首屏 {warm} 张图标就绪，{items.Count} 条交给虚拟化列表");

            var lookup = new Dictionary<string, HomeShortcutItem>(items.Count, StringComparer.Ordinal);
            foreach (HomeShortcutItem item in items)
            {
                lookup[item.Shortcut.Id] = item;
            }

            _itemById = lookup;
            _items = new ObservableCollection<HomeShortcutItem>(items);

            // ⭐ 一次性给全量：ExtentHeight 首帧就按全量估算，滚动条的可拖行程固定，
            //    不会像「边滚边补」那样越拖越长、滑块被顶回去。
            HomeList.ItemsSource = _items;

            // 首屏这批的缺图补抓 / 卡片档升级
            var warmItems = items.GetRange(0, warm);
            BackfillMissingIcons(warmItems.Select(item => item.Shortcut).ToList());
            _ = UpgradeCardIconsAsync(warmItems, generation);

            // 剩下的图标后台按批解码，跟滚动位置无关
            _ = DecodeRemainingIconsAsync(items, warm, generation);
        }

        /// <summary>
        /// 后台把剩余图标按批解码到各自的数据项上。
        ///
        /// 【为什么不挂在滚动上】见文件头那段：跟着滚动触发会让 ExtentHeight 一直变长。
        /// 这里一次性把全量图标解完，只写数据项、不碰视口；容器滚回来时直接命中缓存。
        /// </summary>
        private async Task DecodeRemainingIconsAsync(
            IReadOnlyList<HomeShortcutItem> items, int start, int generation)
        {
            try
            {
                for (int index = start; index < items.Count; )
                {
                    if (generation != _listGeneration)
                    {
                        return;
                    }

                    int end = Math.Min(items.Count, index + IconBatchSize);
                    var decoding = new Task[end - index];
                    for (int i = index; i < end; i++)
                    {
                        decoding[i - index] = PrimeIconAsync(items[i], generation);
                    }

                    await Task.WhenAll(decoding);

                    var batch = new List<HomeShortcutItem>(end - index);
                    var shortcuts = new List<WebAppShortcut>(end - index);
                    for (int i = index; i < end; i++)
                    {
                        batch.Add(items[i]);
                        shortcuts.Add(items[i].Shortcut);
                    }

                    BackfillMissingIcons(shortcuts);
                    _ = UpgradeCardIconsAsync(batch, generation);

                    index = end;
                    await Task.Yield();
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 全部图标就绪 {items.Count} 条");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomePage] 后台解码图标失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解一张图标并挂到【它自己的数据项】上（不落任何容器）。
        ///
        /// 容器复用安全的关键在这里：结果只写 item.Icon，由 x:Bind 决定它此刻显不显示。
        /// 即使这一项已经被滚出视口、容器被复用给别的项，这里也不会摸到别人的界面。
        /// </summary>
        private async Task PrimeIconAsync(HomeShortcutItem item, int generation)
        {
            string appId = item.Shortcut.Id;

            if (_iconCache.TryGetValue(appId, out ImageSource? cached))
            {
                if (cached is not null)
                {
                    item.Icon = new ImageIcon { Source = cached };
                }
                return;
            }

            (string Id, int Generation) request = (appId, generation);
            if (!_iconRequests.Add(request))
            {
                return;
            }

            try
            {
                ImageSource? source = await DecodeIconAsync(item.Shortcut, _listIconTheme);

                // 解码期间换了主题 / 刷新了列表：这份结果属于旧世代，丢掉
                if (generation != _listGeneration)
                {
                    return;
                }

                _iconCache[appId] = source;

                if (source is null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 地球 {item.Shortcut.Name}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[HomeIcon] +{_iconProbe.ElapsedMilliseconds}ms 挂上(已解码) {item.Shortcut.Name}");
                item.Icon = new ImageIcon { Source = source };
            }
            finally
            {
                _iconRequests.Remove(request);
            }
        }

        /// <summary>
        /// 主题变了要整列重建图标：图标是【落盘时按主题染好色】的，
        /// 主题一换，取的就该是另一个 ThemeTag 那份 —— 常驻的主页不会自己再去取一次，
        /// 不主动刷新的话卡片就停在旧主题的颜色上（深色底下是黑图标，等于看不见）。
        /// 导航栏那边也有同样一条（见 NavigationBar.OnActualThemeChanged）。
        /// </summary>
        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            AsyncSafety.TryEnqueue(
                DispatcherQueue,
                async () => await LoadWebAppsAsync(),
                "HomePage",
                "ThemeChanged");
        }

        /// <summary>
        /// 后台把【卡片档】补出来 —— 主页卡片单独用的那一套大图。
        ///
        /// 串行是刻意的：矢量那条路要过渲染内核（进程内单例，本来也会排成一路）。
        ///
        /// ⭐ 只对【矢量源】生成卡片档。位图源一律返回 null（见 WebAppIconCache.EnsureCardAsync），
        ///    卡片继续用通用档 —— 位图那套源最大才 32×32，生成一份同尺寸的卡片档
        ///    既没变清楚、又白占一份磁盘。
        ///
        /// 卡片档到位后换上去只是「变清楚」，不是「从无到有」——
        /// 列表早就用通用档完整显示着了，这里不会重现「某张最后蹦出来」。
        ///
        /// ⚠️ 【探测那一段必须显式 Task.Run】位图源走到 EnsureCardAsync 里就是
        ///    「读一次原件 + 清一次旧卡片档 + 返回 null」，全程没有真正的异步步骤 ——
        ///    这样的任务 await 下去不会挂起，调用线程一路把整批跑完。而里面每一步
        ///    TryGetCardPath / DeleteCardFiles 都是一次 web-icons 全目录枚举，
        ///    压在 UI 线程上就是"进主页整页无响应"。解码那一步相反，必须留在 UI 线程
        ///    （BitmapImage / SvgImageSource 有线程亲和），所以只把磁盘那半段挪走。
        /// </summary>
        private async Task UpgradeCardIconsAsync(
            IReadOnlyList<HomeShortcutItem> items, int generation)
        {
            foreach (HomeShortcutItem item in items)
            {
                // 列表已经重建（换主题 / 刷新 / 新建）：这一轮画的都是旧主题，别接着往下跑
                if (generation != _listGeneration)
                {
                    return;
                }

                WebAppShortcut shortcut = item.Shortcut;
                if (shortcut.IconBytes is not { Length: > 0 } iconBytes)
                {
                    continue;
                }

                string appId = shortcut.Id;
                Microsoft.UI.Xaml.ElementTheme theme = _listIconTheme;

                (bool Changed, string? After, byte[]? Bytes) probe = await Task.Run(async () =>
                {
                    string? before = WebAppIconCache.TryGetCardPath(appId, theme);

                    // 顺带做两件事：矢量源 ⇒ 画一张 48px 的卡片档；位图源 ⇒ 返回 null，
                    // 并把位图时代留下的旧卡片档清掉（见 WebAppIconCache.EnsureCardAsync）。
                    _ = await WebAppIconCache.EnsureCardAsync(appId, iconBytes, theme);

                    string? after = WebAppIconCache.TryGetCardPath(appId, theme);

                    // 只有卡片档「从无到有」或「从有到无」时才需要换图：
                    //   前者 = 矢量源刚画好，换上去变清楚；
                    //   后者 = 旧档被作废，界面上挂的那张已经没了，必须换回通用档。
                    // 两边都没变说明卡片上挂的已经是对的，不动它（省一次解码 + 一次重挂）。
                    if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, after, (byte[]?)null);
                    }

                    byte[]? bytes = after is not null
                        ? WebAppIconCache.TryReadCardBytes(appId, theme)
                        : WebAppIconCache.TryReadCachedBytes(appId, theme);

                    return (true, after, bytes);
                });

                if (!probe.Changed || generation != _listGeneration)
                {
                    continue;
                }

                ImageSource? source = await WebAppIconCache.DecodeAsync(probe.Bytes);
                if (source is null)
                {
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[HomeIcon] 卡片档{(probe.After is not null ? "就绪" : "已作废，退回通用档")} {shortcut.Name}");

                // 和首屏解码走同一条路：写回缓存 + 写回【这一项自己】。
                // 不用去找容器 —— 容器此刻显示的是不是这一项，由 x:Bind 保证。
                _iconCache[appId] = source;
                item.Icon = new ImageIcon { Source = source };
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
            // 必须 await：下面 UpdateAsync 之后别处会按路径取图，写没完成就是空图标。
            await WebAppIconCache.SaveToDiskAsync(appId, bytes, ActualTheme);

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

            ImageSource? source = await WebAppIconCache.DecodeAsync(bytes);
            if (source is null)
            {
                return;
            }

            _iconCache[appId] = source;

            // 只写回数据项自己 —— 它现在在不在视口里、容器有没有被复用，都与这里无关
            if (_itemById.TryGetValue(appId, out HomeShortcutItem? item))
            {
                item.Icon = new ImageIcon { Source = source };
            }
        }

        /// <summary>
        /// 取一个应用的图标源：优先磁盘缓存，没有就用快捷方式自带的字节（顺手补写一份缓存）。
        /// 全程不碰渲染内核 —— 主页出列表不该被光栅化拖住。
        /// </summary>
        private static async Task<Microsoft.UI.Xaml.Media.ImageSource?> DecodeIconAsync(
            WebAppShortcut shortcut, Microsoft.UI.Xaml.ElementTheme theme)
        {
            // ⭐ 按【本页自己的主题】取那一版，而不是顶栏主题：
            //    图标是落盘时按主题染好色的，取的那份跟显示它的容器对不上，
            //    深色容器里就是白图标、浅色容器里就是黑图标 —— 两端都是「看不见」。
            //
            // ⭐ 卡片档优先：主页 SettingsCard 的图标位被 Viewbox 卡在 20×20，
            //    高 DPI 下得用更大那套才不发虚（尺寸依据见 WebAppIconCache.CardIconSize）。
            //    顶栏 / 侧边栏那套是给 16~20px 用的，拿过来放大就是「糊」。
            //
            // ⚠️ 这两个 TryRead 名字看着像内存读，其实各带一次 web-icons 全目录枚举
            //    （TryGetCardPath / TryGetCachedPath）。原先它们在批循环里于 UI 线程上
            //    同步跑完 —— 一批 12 条就是 24 次目录扫描，磁盘停顿 / 杀软扫描没有上限。
            //    挪进 Task.Run 后一批的探测既离开了 UI 线程，彼此也真的并发。
            string appId = shortcut.Id;

            byte[]? bytes = await Task.Run(() =>
                WebAppIconCache.TryReadCardBytes(appId, theme)
                ?? WebAppIconCache.TryReadCachedBytes(appId, theme));

            if (bytes is null && shortcut.IconBytes is { Length: > 0 } iconBytes)
            {
                // 缓存缺失才落一次盘。原先这里是同步写（注释还写着「毫秒级」），
                // 可磁盘停顿 / 杀软扫描 / MSIX 容器重定向写入都没有上限，而它坐在 UI 线程上 ——
                // 主页出列表正好是它最忙的时候。改成 await，落盘挪到线程池。
                await WebAppIconCache.SaveToDiskAsync(appId, iconBytes, theme);

                // ⭐ 落盘那一步会把单色图标按主题染色，所以这里要读【染过色的那一版】，
                //    而不是直接用 IconBytes —— 那份是原色，深色主题下会是黑图标。
                bytes = await Task.Run(() => WebAppIconCache.TryReadCachedBytes(appId, theme)) ?? iconBytes;
            }

            return await WebAppIconCache.DecodeAsync(bytes);
        }

        /// <summary>
        /// 卡片点击：数据项从 DataContext 取。
        ///
        /// 不用闭包捕获项（旧写法 card.Click += (s, e) => OnCardClick(shortcut)）——
        /// 容器会被复用，闭包会把旧项钉在事件上，既串项又泄漏。
        /// </summary>
        private void OnHomeCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is SettingsCard { DataContext: HomeShortcutItem item })
            {
                OnCardClick(item.Shortcut);
            }
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
            // 上一次删除的动画如果还没收尾，先还原（容器是复用的，Opacity 不能留在 0）
            ResetDeletionAnimation();

            if (!_itemById.TryGetValue(appId, out HomeShortcutItem? item))
            {
                return;
            }

            // 只有这一项当前被实现（在视口内）才有卡片可淡出；不在视口里就等删除完成后直接移除
            if (HomeList.ContainerFromItem(item) is not ListViewItem container ||
                container.ContentTemplateRoot is not SettingsCard card)
            {
                return;
            }

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

            _deletionStoryboard = storyboard;
            _deletionCard = card;
            storyboard.Begin();
        }

        private void OnDeletionCompleted(object? sender, string appId)
        {
            // ⚠️ 必须先还原淡出：卡片所属的容器马上会被回收给别的项，
            //    Opacity 停在 0 的话，下一项会「有卡片但看不见」。
            ResetDeletionAnimation();

            if (_itemById.Remove(appId, out HomeShortcutItem? item))
            {
                _items.Remove(item);
            }

            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResetDeletionAnimation()
        {
            _deletionStoryboard?.Stop();
            _deletionStoryboard = null;

            if (_deletionCard is not null)
            {
                _deletionCard.Opacity = 1;
                _deletionCard = null;
            }
        }
    }

    /// <summary>
    /// 主页列表的数据项：把「图标」放在数据项自己身上，而不是放在会被复用的容器上。
    ///
    /// 这样容器复用后不存在「串图」：
    ///   · 容器滚出视口再回来、或被复用给另一项，x:Bind 跟着 DataContext 重新求值；
    ///   · 异步解码完成时只写回它自己的 item.Icon，不会摸到当前容器上显示的别项。
    /// </summary>
    public sealed class HomeShortcutItem : INotifyPropertyChanged
    {
        private IconElement _icon = new SymbolIcon(Symbol.Globe);

        public HomeShortcutItem(WebAppShortcut shortcut)
        {
            Shortcut = shortcut;
            Name = shortcut.Name;
            Url = shortcut.Url;
        }

        public WebAppShortcut Shortcut { get; set; }

        public string Name { get; }

        public string Url { get; }

        /// <summary>卡片图标；没解出来之前是地球占位</summary>
        public IconElement Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value))
                {
                    return;
                }

                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
