using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace DockedTools.Features.Pages.Settings.WebSettings
{
    public sealed partial class WebAppManagementPage : Page, INotifyPropertyChanged
    {
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;
        private bool _isFirstLoad = true; // ⭐ 添加首次加载标志

        private readonly 智能标题 _智能标题 = new();
        
        public bool HasApps => AppsListPanel?.Children.Count > 0;
        public bool HasNoApps => AppsListPanel?.Children.Count == 0;

        public WebAppManagementPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;

            // 订阅删除服务事件
            WebAppDeletionService.DeletionStarting += OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted += OnDeletionCompleted;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, PageScrollViewer, PageTitleBlock);
            
            // ⭐ 重新启用缓存（因为从详情页返回时需要保持状态）
            if (NavigationCacheMode == NavigationCacheMode.Disabled)
            {
                NavigationCacheMode = NavigationCacheMode.Required;
                System.Diagnostics.Debug.WriteLine("[WebAppManagementPage] 重新启用页面缓存");
            }
            
            // ⭐ 重新订阅删除服务事件（因为 OnNavigatedFrom 中取消了订阅）
            WebAppDeletionService.DeletionStarting += OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted += OnDeletionCompleted;
            
            // ⭐ 只有在从详情页返回时才刷新数据（NavigationMode.Back）
            if (!_isFirstLoad && e.NavigationMode == NavigationMode.Back)
            {
                System.Diagnostics.Debug.WriteLine("[WebAppManagementPage] 从详情页返回，智能刷新数据");
                
                // ⭐ 临时禁用动画，避免返回时的弹出动画
                var transitions = AppsListPanel.ChildrenTransitions;
                AppsListPanel.ChildrenTransitions = null;
                
                // ⭐ 在 UI 线程异步刷新，完成后恢复动画
                _ = DispatcherQueue.TryEnqueue(async () =>
                {
                    await RefreshAppsAsync();
                    
                    // ⭐ 刷新完成后恢复动画
                    AppsListPanel.ChildrenTransitions = transitions;
                    System.Diagnostics.Debug.WriteLine("[WebAppManagementPage] 刷新完成，动画已恢复");
                });
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();

            // 取消订阅删除服务事件
            WebAppDeletionService.DeletionStarting -= OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted -= OnDeletionCompleted;

            // ⭐ 当返回到上级页面（设置页）时，清除页面缓存
            // 这样下次进入时会重新加载最新数据，避免显示过期信息
            if (e.NavigationMode == NavigationMode.Back)
            {
                NavigationCacheMode = NavigationCacheMode.Disabled;
                System.Diagnostics.Debug.WriteLine("[WebAppManagementPage] 返回上级页面，已清除页面缓存");
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // ⭐ 只在首次加载时加载数据
            if (_isFirstLoad)
            {
                // ⭐ 首次加载时：先隐藏内容区域，避免加载过程中的闪现
                CardsPanel.Visibility = Visibility.Collapsed;
                
                // ⭐ 临时禁用动画，避免入场弹出动画
                var transitions = AppsListPanel.ChildrenTransitions;
                AppsListPanel.ChildrenTransitions = null;
                
                await LoadAppsAsync();
                _isFirstLoad = false;
                
                // ⭐ 显示内容区域
                CardsPanel.Visibility = Visibility.Visible;
                
                // ⭐ 延迟恢复动画（在下一帧）
                AppsListPanel.DispatcherQueue.TryEnqueue(() =>
                {
                    AppsListPanel.ChildrenTransitions = transitions;
                });
            }
            UpdateVisualState();
        }

        private async Task LoadAppsAsync()
        {
            try
            {
                var shortcuts = await WebAppShortcutStore.LoadAsync();
                
                AppsListPanel.Children.Clear();

                if (shortcuts.Count == 0)
                {
                    OnPropertyChanged(nameof(HasApps));
                    OnPropertyChanged(nameof(HasNoApps));
                    return;
                }

                foreach (var shortcut in shortcuts)
                {
                    var card = new CommunityToolkit.WinUI.Controls.SettingsCard
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Header = shortcut.Name,
                        Description = shortcut.Url,
                        IsClickEnabled = true,
                        Tag = shortcut
                    };

                    // 设置点击事件
                    card.Click += OnAppCardClick;

                    // 设置图标（完全照抄主页）
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

                    // 设置右键菜单
                    var contextMenu = new MenuFlyout();
                    var deleteItem = new MenuFlyoutItem
                    {
                        Text = "删除",
                        Tag = shortcut,
                        Icon = new FontIcon { Glyph = "\uE74D" } // 删除图标
                    };
                    deleteItem.Click += OnDeleteAppClick;
                    contextMenu.Items.Add(deleteItem);
                    card.ContextFlyout = contextMenu;

                    AppsListPanel.Children.Add(card);
                }

                // 通知属性变化
                OnPropertyChanged(nameof(HasApps));
                OnPropertyChanged(nameof(HasNoApps));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] Failed to load apps: {ex}");
            }
        }

        /// <summary>
        /// ⭐ 智能刷新：只更新修改过的卡片，保持滚动位置
        /// </summary>
        private async Task RefreshAppsAsync()
        {
            try
            {
                var shortcuts = await WebAppShortcutStore.LoadAsync();
                
                // 更新现有卡片
                foreach (var child in AppsListPanel.Children)
                {
                    if (child is CommunityToolkit.WinUI.Controls.SettingsCard card && 
                        card.Tag is WebAppShortcut oldShortcut)
                    {
                        // 查找最新数据
                        var newShortcut = shortcuts.FirstOrDefault(s => s.Id == oldShortcut.Id);
                        if (newShortcut != null)
                        {
                            // 更新标题和描述
                            if (card.Header?.ToString() != newShortcut.Name)
                            {
                                card.Header = newShortcut.Name;
                            }
                            if (card.Description?.ToString() != newShortcut.Url)
                            {
                                card.Description = newShortcut.Url;
                            }

                            // ⭐ 更新图标（检测三种情况）
                            bool iconChanged = false;
                            if (newShortcut.IconBytes == null && oldShortcut.IconBytes != null)
                            {
                                // 情况1: 有图标 → 无图标（重置）
                                iconChanged = true;
                            }
                            else if (newShortcut.IconBytes != null && oldShortcut.IconBytes == null)
                            {
                                // 情况2: 无图标 → 有图标（新增）
                                iconChanged = true;
                            }
                            else if (newShortcut.IconBytes != null && oldShortcut.IconBytes != null &&
                                     !newShortcut.IconBytes.SequenceEqual(oldShortcut.IconBytes))
                            {
                                // 情况3: 有图标 → 有图标（替换）
                                iconChanged = true;
                            }

                            if (iconChanged)
                            {
                                if (newShortcut.IconBytes != null && newShortcut.IconBytes.Length > 0)
                                {
                                    // 设置自定义图标
                                    try
                                    {
                                        var bitmap = new BitmapImage();
                                        using var stream = new InMemoryRandomAccessStream();
                                        await stream.WriteAsync(newShortcut.IconBytes.AsBuffer());
                                        stream.Seek(0);
                                        await bitmap.SetSourceAsync(stream);
                                        card.HeaderIcon = new ImageIcon { Source = bitmap };
                                        System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] Updated custom icon for {newShortcut.Name}");
                                    }
                                    catch
                                    {
                                        card.HeaderIcon = new SymbolIcon(Symbol.Globe);
                                        System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] Failed to decode icon, using default for {newShortcut.Name}");
                                    }
                                }
                                else
                                {
                                    // 重置为默认图标
                                    card.HeaderIcon = new SymbolIcon(Symbol.Globe);
                                    System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] Reset to default icon for {newShortcut.Name}");
                                }
                            }

                            // 更新 Tag
                            card.Tag = newShortcut;
                        }
                    }
                }

                // 通知属性变化
                OnPropertyChanged(nameof(HasApps));
                OnPropertyChanged(nameof(HasNoApps));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] Failed to refresh apps: {ex}");
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

        private void OnAddNewAppClick(object sender, RoutedEventArgs e)
        {
            // 导航到新建页面（复用现有的网页应用页面）
            var animationType = ExperimentalSettings.SubPageNavigationAnimation;
            var transitionInfo = GetNavigationTransitionInfo(animationType);
            Frame.Navigate(typeof(WebApp.WebAppPage), null, transitionInfo);
        }

        private void OnAppCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is WebAppShortcut shortcut)
            {
                // ⭐ 增强调试：详细记录导航信息
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] OnAppCardClick 被调用");
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] shortcut.Id = {shortcut.Id}");
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] shortcut.Name = {shortcut.Name}");
                
                // 导航到详情页面，传递应用 ID
                var animationType = ExperimentalSettings.SubPageNavigationAnimation;
                var transitionInfo = GetNavigationTransitionInfo(animationType);
                
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] 开始导航到 WebAppDetailPage，参数类型: {shortcut.Id.GetType().FullName}");
                Frame.Navigate(typeof(WebAppDetailPage), shortcut.Id, transitionInfo);
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] 导航完成");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage] ❌ OnAppCardClick: sender 或 Tag 类型不匹配");
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage]    sender 类型: {sender?.GetType().FullName ?? "null"}");
                System.Diagnostics.Debug.WriteLine($"[WebAppManagementPage]    Tag 类型: {(sender as FrameworkElement)?.Tag?.GetType().FullName ?? "null"}");
            }
        }

        private async void OnDeleteAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is WebAppShortcut shortcut)
            {
                // 直接删除，无需确认对话框
                await WebAppDeletionService.DeleteWithAnimationAsync(shortcut.Id);
            }
        }

        private void OnDeletionStarting(object? sender, string appId)
        {
            // ⭐ AddDeleteThemeTransition 会自动处理删除动画，无需手动代码
        }

        private void OnDeletionCompleted(object? sender, string appId)
        {
            // 找到卡片并移除（AddDeleteThemeTransition 自动播放平滑的淡出+重排动画）
            CommunityToolkit.WinUI.Controls.SettingsCard? cardToRemove = null;
            foreach (var child in AppsListPanel.Children)
            {
                if (child is CommunityToolkit.WinUI.Controls.SettingsCard card && 
                    card.Tag is WebAppShortcut shortcut && 
                    shortcut.Id == appId)
                {
                    cardToRemove = card;
                    break;
                }
            }

            if (cardToRemove != null)
            {
                // 取消订阅事件
                cardToRemove.Click -= OnAppCardClick;

                // 移除元素（AddDeleteThemeTransition 自动播放删除动画：淡出 + 其他元素平滑上移）
                AppsListPanel.Children.Remove(cardToRemove);

                // 更新空状态
                OnPropertyChanged(nameof(HasApps));
                OnPropertyChanged(nameof(HasNoApps));
            }
        }

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
                _ => new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo()
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
