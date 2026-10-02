using System;
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

        public HomePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;

            // 订阅统一删除服务事件
            WebAppDeletionService.DeletionStarting += OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted += OnDeletionCompleted;
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

            // 取消订阅删除服务事件
            WebAppDeletionService.DeletionStarting -= OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted -= OnDeletionCompleted;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateVisualState();
            await LoadWebAppsAsync();
            WebAppEventBus.ShortcutCreated += OnShortcutCreated;
            WebAppEventBus.ShortcutsRefreshRequested += OnShortcutsRefreshRequested;
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

        private async Task LoadWebAppsAsync()
        {
            var shortcuts = await WebAppShortcutStore.LoadAsync();
            WebAppsList.Children.Clear();

            if (shortcuts.Count == 0)
            {
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            EmptyText.Visibility = Visibility.Collapsed;

            foreach (var shortcut in shortcuts)
            {
                var card = new SettingsCard
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = shortcut.Name,
                    Description = shortcut.Url,
                    IsClickEnabled = true,
                    Tag = shortcut // ⭐ 添加 Tag 用于删除时识别
                };

                card.Click += (sender, e) => OnCardClick(shortcut);

                if (shortcut.IconBytes != null && shortcut.IconBytes.Length > 0)
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        using var stream = new InMemoryRandomAccessStream();
                        await stream.WriteAsync(shortcut.IconBytes.AsBuffer());
                        stream.Seek(0);
                        await bitmap.SetSourceAsync(stream);
                        card.HeaderIcon = new ImageIcon { Source = bitmap };
                    }
                    catch
                    {
                        card.HeaderIcon = new SymbolIcon(Symbol.Globe);
                    }
                }
                else
                {
                    card.HeaderIcon = new SymbolIcon(Symbol.Globe);
                }

                WebAppsList.Children.Add(card);
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
