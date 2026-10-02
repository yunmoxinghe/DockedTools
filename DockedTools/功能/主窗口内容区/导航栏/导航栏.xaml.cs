using DockedTools.Features.Pages.Home;
using DockedTools.Features.Pages.New;
using DockedTools.Features.Pages.AI;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.Lab;
using DockedTools.Features.Pages.WebApp.Browser;
using DockedTools.Features.Pages.WebApp.Browser.Managers;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Localization;
using DockedTools.Features.MainWindowContent.ContentArea;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DockedTools.Features.MainWindowContent.NavigationBar
{
    public sealed partial class NavigationBar : UserControl
    {
        private readonly Dictionary<string, WebAppShortcut> _webShortcuts = new();
        private readonly Dictionary<string, NavigationViewItem> _webShortcutItems = new();
        private readonly Dictionary<string, ImageIcon> _webShortcutIconCache = new(); // ⭐ 图标缓存
        private NavigationViewItemBase? _lastSelectedNavigationItem;
        private bool _suppressSelectionChanged;
        
        // 导航防抖器（使用 Stopwatch 实现线程安全）
        private readonly NavigationDebouncer _navigationDebouncer = new(300);

        public event EventHandler<NavigationRequest>? NavigationRequested;
        public event EventHandler<string>? ShortcutRemoved; // 快捷方式被移除事件
        public event EventHandler<string>? WebAppRestartRequested; // 网页应用重启请求事件
        public event EventHandler? BackRequested; // 返回请求事件

        private bool _isNavigationBarOnLeft = false;

        public void UpdateDockToggleIcon(bool isPinned)
        {
            // 根据导航栏位置选择不同的图标
            if (_isNavigationBarOnLeft)
            {
                // 左侧模式：使用 Pin/Unpin 图标
                DockToggleIcon.Glyph = isPinned ? "\uEA5B" : "\uEA49";
            }
            else
            {
                // 右侧模式：使用原来的图标
                DockToggleIcon.Glyph = isPinned ? "\uE8A0" : "\uE89F";
            }
        }

        public void SetNavigationBarPlacement(bool isOnLeft)
        {
            _isNavigationBarOnLeft = isOnLeft;
            
            TopNavView.HorizontalAlignment = isOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            TopNavView.FlowDirection = isOnLeft ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
            
            NavView.HorizontalAlignment = isOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            NavView.FlowDirection = isOnLeft ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        }

        public void SelectNewPageItem()
        {
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 🔥 SelectNewPageItem 被调用");
            _suppressSelectionChanged = true;
            NavView.SelectedItem = CreateNavigationItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = CreateNavigationItem; // ⭐ 修复：同步更新选中记录
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中新建页，SelectedItem={NavView.SelectedItem?.GetType().Name}");
            
            // ⭐ 滚动到选中的项（如果不可见）
            ScrollToSelectedItem(CreateNavigationItem);
        }

        public void SelectHomeItem()
        {
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 🔥 SelectHomeItem 被调用");
            _suppressSelectionChanged = true;
            NavView.SelectedItem = HomeNavigationItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = HomeNavigationItem; // ⭐ 修复：同步更新选中记录
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中首页，SelectedItem={NavView.SelectedItem?.GetType().Name}");
            
            // ⭐ 滚动到选中的项（如果不可见）
            ScrollToSelectedItem(HomeNavigationItem);
        }

        public void SelectSettingsItem()
        {
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 🔥 SelectSettingsItem 被调用");
            _suppressSelectionChanged = true;
            NavView.SelectedItem = SettingsNavigationItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = SettingsNavigationItem; // ⭐ 修复：同步更新选中记录
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中设置页，SelectedItem={NavView.SelectedItem?.GetType().Name}");
            
            // ⭐ 滚动到选中的项（如果不可见）
            ScrollToSelectedItem(SettingsNavigationItem);
        }

        public void SelectAIItem()
        {
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 🔥 SelectAIItem 被调用");
            _suppressSelectionChanged = true;
            NavView.SelectedItem = AINavigationItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = AINavigationItem; // ⭐ 修复：同步更新选中记录
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中 AI 页，SelectedItem={NavView.SelectedItem?.GetType().Name}");
            
            // ⭐ 滚动到选中的项（如果不可见）
            ScrollToSelectedItem(AINavigationItem);
        }

        public void SelectLabItem()
        {
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 🔥 SelectLabItem 被调用");
            _suppressSelectionChanged = true;
            NavView.SelectedItem = LabNavigationItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = LabNavigationItem;
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中实验室页，SelectedItem={NavView.SelectedItem?.GetType().Name}");
            
            // ⭐ 滚动到选中的项（如果不可见）
            ScrollToSelectedItem(LabNavigationItem);
        }

        /// <summary>
        /// 启用导航（解除 SelectionChanged 抑制）
        /// 在 LoadContent() 调用后启用，允许用户导航触发
        /// </summary>
        public void EnableNavigation()
        {
            _suppressSelectionChanged = false;
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 导航已启用");
        }

        /// <summary>
        /// 获取所有网页应用快捷方式列表（按添加顺序）
        /// 用于快捷键切换标签页功能
        /// </summary>
        public List<WebAppShortcut> GetWebAppShortcuts()
        {
            return _webShortcuts.Values.ToList();
        }

        /// <summary>
        /// 根据索引切换到对应的网页应用（0-based，用于 Ctrl+1~9）
        /// </summary>
        /// <param name="index">标签索引（0 对应第一个标签，-1 对应最后一个标签）</param>
        /// <returns>如果索引有效且切换成功返回 true，否则返回 false</returns>
        public bool SwitchToWebAppByIndex(int index)
        {
            var shortcuts = GetWebAppShortcuts();
            
            // 处理负数索引：-1 表示最后一个标签
            if (index < 0)
            {
                index = shortcuts.Count + index;
            }
            
            if (index >= 0 && index < shortcuts.Count)
            {
                var shortcut = shortcuts[index];
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 快捷键切换到标签 {index + 1}: {shortcut.Name}");
                SelectWebAppItem(shortcut.Id);
                
                // ⭐ 滚动到选中的项（如果不可见）
                ScrollToSelectedItem(shortcut.Id);
                
                // 触发导航请求
                NavigationRequested?.Invoke(this, new NavigationRequest(typeof(WebBrowserPage), shortcut));
                return true;
            }
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 标签索引 {index + 1} 超出范围（共 {shortcuts.Count} 个标签）");
            return false;
        }

        /// <summary>
        /// 切换到下一个标签（Ctrl+Tab）
        /// 循环顺序：按 NavigationView.MenuItems 和 FooterMenuItems 的实际顺序切换
        /// 只包含可选中且可见的项
        /// 
        /// 【AOT 兼容性】
        /// 使用显式类型检查而非 OfType<T>()，避免 trimming 警告
        /// 符合 .NET 10 Native AOT 最佳实践
        /// </summary>
        public void SwitchToNextWebApp()
        {
            // ⭐ AOT 优化：使用显式类型检查代替 OfType<T>()
            // 避免 IL2026 trimming 警告
            var menuItems = new List<NavigationViewItem>();
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem navItem && 
                    navItem.SelectsOnInvoked && 
                    navItem.Visibility == Visibility.Visible)
                {
                    menuItems.Add(navItem);
                }
            }
            
            var footerItems = new List<NavigationViewItem>();
            foreach (var item in NavView.FooterMenuItems)
            {
                if (item is NavigationViewItem navItem && 
                    navItem.SelectsOnInvoked && 
                    navItem.Visibility == Visibility.Visible)
                {
                    footerItems.Add(navItem);
                }
            }
            
            // 合并 MenuItems 和 FooterMenuItems
            var allItems = new List<NavigationViewItem>(menuItems.Count + footerItems.Count);
            allItems.AddRange(menuItems);
            allItems.AddRange(footerItems);
            
            if (allItems.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[NavigationBar] 没有可切换的标签");
                return;
            }
            
            // 查找当前选中项的索引
            var currentItem = NavView.SelectedItem as NavigationViewItem;
            int currentIndex = currentItem != null ? allItems.IndexOf(currentItem) : -1;
            
            // 切换到下一个标签（循环）
            int nextIndex = (currentIndex + 1) % allItems.Count;
            var nextItem = allItems[nextIndex];
            
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] Ctrl+Tab 切换: 索引 {currentIndex} → {nextIndex} (Content={nextItem.Content}, Tag={nextItem.Tag})");
            
            // ⭐ 直接设置选中项，触发 SelectionChanged 事件
            // SelectionChanged 事件会根据 Tag 自动处理导航逻辑
            _suppressSelectionChanged = false; // 确保不被抑制
            NavView.SelectedItem = nextItem;
            TopNavView.SelectedItem = null;
            _lastSelectedNavigationItem = nextItem;
        }

        public void SelectWebAppItem(string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 🔥 SelectWebAppItem 被调用: {shortcutId}");
            if (_webShortcutItems.TryGetValue(shortcutId, out NavigationViewItem? navItem))
            {
                _suppressSelectionChanged = true;
                NavView.SelectedItem = navItem;
                TopNavView.SelectedItem = null;
                _lastSelectedNavigationItem = navItem; // ⭐ 修复：同步更新选中记录
                _suppressSelectionChanged = false;
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 已选中 WebApp 标签: {shortcutId}");
                
                // ⭐ 滚动到选中的项（如果不可见）
                ScrollToSelectedItem(shortcutId);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ⚠️ 未找到 WebApp 导航项: {shortcutId}");
            }
        }

        /// <summary>
        /// 滚动到指定的快捷方式项
        /// 使用平滑动画确保用户能看到选中项（符合 UX 最佳实践）
        /// 
        /// 【实现原理】
        /// 使用 WinUI 3 原生的 StartBringIntoView API
        /// 自动处理布局和滚动逻辑，无需等待 LayoutUpdated
        /// 参考：https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.startbringintoview
        /// </summary>
        /// <param name="shortcutId">快捷方式 ID</param>
        private void ScrollToSelectedItem(string shortcutId)
        {
            bool success = NavView.ScrollToItemByTag(shortcutId, animated: true);
            
            if (success)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 成功滚动到项: {shortcutId}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ⚠️ 滚动到项失败或项已在可见区域: {shortcutId}");
            }
        }

        /// <summary>
        /// 滚动到指定的 NavigationViewItem
        /// 使用平滑动画确保用户能看到选中项（符合 UX 最佳实践）
        /// 
        /// 【使用场景】
        /// 用于首页、设置页、AI页等没有 shortcutId 的固定导航项
        /// </summary>
        /// <param name="item">目标 NavigationViewItem</param>
        private void ScrollToSelectedItem(NavigationViewItem item)
        {
            bool success = NavView.ScrollIntoView(item, animated: true);
            
            if (success)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ✅ 成功滚动到项: {item.Content}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] ⚠️ 滚动到项失败或项已在可见区域: {item.Content}");
            }
        }

        public NavigationBar()
        {
            InitializeComponent();
            _lastSelectedNavigationItem = HomeNavigationItem;

            WebAppEventBus.ShortcutCreated += OnShortcutCreated;
            WebAppEventBus.ShortcutsRefreshRequested += OnShortcutsRefreshRequested;
            
            // 订阅统一删除服务事件
            WebAppDeletionService.DeletionStarting += OnDeletionStarting;
            WebAppDeletionService.DeletionCompleted += OnDeletionCompleted;
            
            // 订阅统一更新服务事件
            WebAppUpdateService.UpdateCompleted += OnUpdateCompleted;
            
            Unloaded += (_, _) =>
            {
                WebAppEventBus.ShortcutCreated -= OnShortcutCreated;
                WebAppEventBus.ShortcutsRefreshRequested -= OnShortcutsRefreshRequested;
                WebAppDeletionService.DeletionStarting -= OnDeletionStarting;
                WebAppDeletionService.DeletionCompleted -= OnDeletionCompleted;
                WebAppUpdateService.UpdateCompleted -= OnUpdateCompleted;
            };
            Loaded += NavigationBar_Loaded;
            SizeChanged += NavigationBar_SizeChanged;
            
            // 添加双击空白区域触发固定按钮
            NavView.DoubleTapped += OnNavViewDoubleTapped;
            
            // 根据设置显示或隐藏 AI 导航项
            UpdateAINavigationItemVisibility();
            // 根据 Debug 模式显示或隐藏实验室导航项
            UpdateLabNavigationItemVisibility();
            // 初始化返回按钮（默认隐藏）
            BackNavigationItem.Visibility = Visibility.Collapsed;
            
            // ⭐ 修复 Bug: 初始化时抑制 SelectionChanged 事件，避免过早触发导航
            // HomeNavigationItem 的 IsSelected="True" 会在 XAML 初始化时触发 SelectionChanged
            // 我们需要等到 LoadContent() 调用后才真正导航到首页
            _suppressSelectionChanged = true;
        }

        private void NavigationBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 强制刷新 NavView 布局以修复 FooterMenuItems 显示问题
            NavView.InvalidateMeasure();
            NavView.InvalidateArrange();
        }

        public void UpdateAINavigationItemVisibility()
        {
            AINavigationItem.Visibility = ExperimentalSettings.EnableAILab 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }

        public void UpdateLabNavigationItemVisibility()
        {
#if DEBUG
            LabNavigationItem.Visibility = Visibility.Visible;
#else
            LabNavigationItem.Visibility = Visibility.Collapsed;
#endif
        }

        public void UpdateBackButtonVisibility(bool canGoBack)
        {
            if (!ExperimentalSettings.EnableBackButton)
            {
                BackNavigationItem.Visibility = Visibility.Collapsed;
                return;
            }
            BackNavigationItem.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnNavViewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // 检查是否双击了按钮或图标区域
            // 如果双击的是 NavigationViewItem 或其子元素，则不触发固定功能
            var originalSource = e.OriginalSource as DependencyObject;
            
            // 向上遍历可视树，检查是否点击了 NavigationViewItem
            while (originalSource != null)
            {
                if (originalSource is NavigationViewItem)
                {
                    // 双击了按钮区域，不触发固定功能
                    return;
                }
                
                if (originalSource == NavView)
                {
                    // 已经到达 NavView 根节点，说明是空白区域
                    break;
                }
                
                originalSource = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(originalSource);
            }
            
            // ⭐ 双击侧边栏空白区域时使用 MainWindowService 切换固定状态
            DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.RequestTogglePinned();
            System.Diagnostics.Debug.WriteLine("[NavigationBar] 双击空白区域，通过 MainWindowService 切换固定状态");
        }

        public void UpdateWindowStateIcon(bool isMaximized)
        {
            // E73F: 还原窗口图标
            // E740: 最大化图标
            WindowStateIcon.Glyph = isMaximized ? "\uE73F" : "\uE740";
        }

        private async void NavigationBar_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= NavigationBar_Loaded;
            
            await RestorePersistedShortcutsAsync();
            
            // 延迟刷新布局以修复 FooterMenuItems 显示问题
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                NavView.InvalidateMeasure();
                NavView.InvalidateArrange();
                NavView.UpdateLayout();
            });
        }

        private void OnShortcutCreated(object? sender, WebAppShortcut shortcut)
        {
            AddOrUpdateShortcutNavigationItem(shortcut, selectItem: true);
            _ = PersistShortcutsAsync();
        }

        private async void OnShortcutsRefreshRequested(object? sender, EventArgs e)
        {
            // ⭐ 智能刷新：只更新修改过的项，避免闪烁
            IReadOnlyList<WebAppShortcut> shortcuts = await WebAppShortcutStore.LoadAsync();
            
            // 更新现有项
            foreach (WebAppShortcut shortcut in shortcuts)
            {
                if (_webShortcuts.TryGetValue(shortcut.Id, out WebAppShortcut? existingShortcut))
                {
                    // 检查是否有变化
                    bool hasChanges = existingShortcut.Name != shortcut.Name ||
                                     existingShortcut.Url != shortcut.Url ||
                                     !ByteArrayEquals(existingShortcut.IconBytes, shortcut.IconBytes);
                    
                    if (hasChanges)
                    {
                        // 只更新有变化的项
                        AddOrUpdateShortcutNavigationItem(shortcut, selectItem: false);
                        System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新快捷方式: {shortcut.Name}");
                    }
                }
                else
                {
                    // 新增项
                    AddOrUpdateShortcutNavigationItem(shortcut, selectItem: false);
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 新增快捷方式: {shortcut.Name}");
                }
            }
            
            // 删除不存在的项（已被删除）
            var shortcutIds = new HashSet<string>(shortcuts.Select(s => s.Id));
            var itemsToRemove = _webShortcuts.Keys.Where(id => !shortcutIds.Contains(id)).ToList();
            foreach (string idToRemove in itemsToRemove)
            {
                RemoveShortcut(idToRemove);
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 删除快捷方式: {idToRemove}");
            }
        }
        
        private static bool ByteArrayEquals(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            return a.SequenceEqual(b);
        }

        private async Task RestorePersistedShortcutsAsync()
        {
            IReadOnlyList<WebAppShortcut> shortcuts = await WebAppShortcutStore.LoadAsync();
            foreach (WebAppShortcut shortcut in shortcuts)
            {
                AddOrUpdateShortcutNavigationItem(shortcut, selectItem: false);
            }
        }

        /// <summary>
        /// 把界面上的快捷方式落盘。
        ///
        /// ⚠️ 这里**不能**拿 _webShortcuts 整份覆盖磁盘。
        /// 桥接（浏览器扩展）是后台通道，它新增的条目直接落在磁盘上，
        /// 而界面这边要等下一次刷新才会同步进 _webShortcuts。
        /// 在这个窗口里只要触发一次 Persist，整份覆盖就会把那些条目一起抹掉，
        /// 而且是静默的——界面上本来就没显示，用户根本不知道少了东西。
        ///
        /// 所以改成 UpdateAsync 做合并：「界面副本为准 + 保留磁盘上界面不认识的条目」。
        /// 取舍：极端情况下（磁盘上已删、界面还没同步）可能让一条已被删的条目「复活」，
        /// 但丢数据和多一条之间，多一条的代价明显更小，用户再删一次就行。
        /// </summary>
        private async Task PersistShortcutsAsync()
        {
            try
            {
                await WebAppShortcutStore.UpdateAsync(disk =>
                {
                    var uiIds = new HashSet<string>(_webShortcuts.Keys);
                    var merged = new List<WebAppShortcut>(_webShortcuts.Values);

                    foreach (WebAppShortcut diskItem in disk)
                    {
                        if (!uiIds.Contains(diskItem.Id))
                        {
                            // 界面不知道这条 —— 桥接或其他后台通道刚写进去的，留着
                            merged.Add(diskItem);
                        }
                    }

                    return merged;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist web shortcuts: {ex.Message}");
            }
        }

        private void AddOrUpdateShortcutNavigationItem(WebAppShortcut shortcut, bool selectItem)
        {
            _webShortcuts[shortcut.Id] = shortcut;

            if (_webShortcutItems.TryGetValue(shortcut.Id, out NavigationViewItem? existingItem))
            {
                existingItem.Content = shortcut.Name;
                existingItem.Icon = BuildShortcutIcon(shortcut);
                if (selectItem)
                {
                    NavView.SelectedItem = existingItem;
                }
                return;
            }

            var navItem = new NavigationViewItem
            {
                Content = shortcut.Name,
                Tag = "webapp:" + shortcut.Id,
                Icon = BuildShortcutIcon(shortcut)
            };

            var contextMenu = new MenuFlyout();
            var unpinItem = new MenuFlyoutItem
            {
                Text = LocalizationHelper.GetString("Nav_UnpinShortcut"),
                Tag = shortcut.Id,
                Icon = new FontIcon { Glyph = "\uE77A" }
            };
            unpinItem.Click += OnUnpinShortcutClick;
            contextMenu.Items.Add(unpinItem);
            navItem.ContextFlyout = contextMenu;

            int insertIndex = NavView.MenuItems.IndexOf(CreateNavigationItem);
            if (insertIndex < 0)
            {
                insertIndex = NavView.MenuItems.Count;
            }

            NavView.MenuItems.Insert(insertIndex, navItem);
            _webShortcutItems[shortcut.Id] = navItem;
            if (selectItem)
            {
                NavView.SelectedItem = navItem;
            }
        }

        private IconElement BuildShortcutIcon(WebAppShortcut shortcut)
        {
            string cacheDir = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "web-icons");
            Directory.CreateDirectory(cacheDir);
            string extension = DetectImageExtension(shortcut.IconBytes ?? Array.Empty<byte>());
            string iconPath = Path.Combine(cacheDir, $"{shortcut.Id}{extension}");

            // 尝试从 IconBytes 加载
            if (shortcut.IconBytes is { Length: > 0 })
            {
                try
                {
                    File.WriteAllBytes(iconPath, shortcut.IconBytes);
                    var icon = CreateImageIconWithFallback(new Uri(iconPath), shortcut.Id);
                    if (icon != null) return icon;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 保存图标失败: {iconPath}, {ex.Message}");
                }
            }

            // 尝试从缓存加载
            if (File.Exists(iconPath))
            {
                try
                {
                    var icon = CreateImageIconWithFallback(new Uri(iconPath), shortcut.Id);
                    if (icon != null) return icon;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 读取缓存图标失败: {iconPath}, {ex.Message}");
                }
            }

            // 尝试从网站 favicon 加载
            if (Uri.TryCreate(shortcut.Url, UriKind.Absolute, out Uri? websiteUri))
            {
                try
                {
                    Uri faviconUri = new Uri(websiteUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
                    var icon = CreateImageIconWithFallback(faviconUri, shortcut.Id);
                    if (icon != null) return icon;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 创建 Favicon URI 失败: {shortcut.Url}, {ex.Message}");
                }
            }

            // 所有方法都失败时，返回地球图标作为后备
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 所有图标加载方法失败，使用地球图标: {shortcut.Name}");
            return new FontIcon { Glyph = "\uE774" }; // Globe 地球图标
        }

        private ImageIcon? CreateImageIconWithFallback(Uri imageUri, string shortcutId)
        {
            try
            {
                var bitmapImage = new BitmapImage();
                var imageIcon = new ImageIcon { Source = bitmapImage };
                
                // ⭐ 缓存 ImageIcon 对象（用于后续复用）
                _webShortcutIconCache[shortcutId] = imageIcon;
                
                // 监听图片加载失败事件，失败时切换到地球图标
                bitmapImage.ImageFailed += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 图标加载失败: {imageUri}, 错误: {e.ErrorMessage}");
                    
                    // 在 UI 线程上切换到地球图标
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webShortcutItems.TryGetValue(shortcutId, out var navItem))
                        {
                            navItem.Icon = new FontIcon { Glyph = "\uE774" }; // Globe 地球图标
                            _webShortcutIconCache.Remove(shortcutId); // 清除缓存
                            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 已切换到地球图标: {shortcutId}");
                        }
                    });
                };
                
                // 开始加载图片
                bitmapImage.UriSource = imageUri;
                
                return imageIcon;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 创建 ImageIcon 失败: {imageUri}, {ex.Message}");
                return null;
            }
        }

        private static string DetectImageExtension(byte[] bytes)
        {
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

        // 顶部 NavigationView 的 SelectionChanged 处理
        private void TopNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_suppressSelectionChanged)
            {
                return;
            }

            // 当 TopNavView 中的项被选中时，清除 NavView 的选中状态
            if (args.SelectedItemContainer != null && NavView.SelectedItem != null)
            {
                _suppressSelectionChanged = true;
                NavView.SelectedItem = null;
                _suppressSelectionChanged = false;
            }
        }

        // 顶部 NavigationView 的 ItemInvoked 处理
        private void TopNavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer?.Tag is not string tagText)
            {
                return;
            }

            if (tagText == "windowstate")
            {
                // ⭐ 使用 MainWindowService 切换窗口状态
                DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.RequestToggleMaximize();
                System.Diagnostics.Debug.WriteLine("[NavigationBar] 通过 MainWindowService 切换窗口状态");
                return;
            }
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer?.Tag is not string tagText)
            {
                return;
            }

            // ⭐ 修复 Bug 1: 只在 ItemInvoked 中处理不需要改变选中状态的按钮
            // 普通导航项由 SelectionChanged 统一处理，避免重复导航

            // 处理停靠切换（不改变选中状态）
            if (tagText == "dock")
            {
                // ⭐ 使用 MainWindowService 切换固定状态
                DockedTools.Features.UnifiedCalls.MainWindow.MainWindowService.RequestTogglePinned();
                System.Diagnostics.Debug.WriteLine("[NavigationBar] 通过 MainWindowService 切换固定状态");
                
                // ⭐ 恢复上次选中的导航项
                _suppressSelectionChanged = true;
                NavView.SelectedItem = _lastSelectedNavigationItem;
                _suppressSelectionChanged = false;
                return;
            }

            // 处理返回按钮（不改变选中状态）
            if (tagText == "back")
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
                // ⭐ 修复 Bug 2: 恢复上次选中的导航项
                _suppressSelectionChanged = true;
                NavView.SelectedItem = _lastSelectedNavigationItem;
                _suppressSelectionChanged = false;
                return;
            }

            // ⭐ 修复 Bug 4: 处理 WebApp 快捷方式的重启逻辑（检测双击）
            if (tagText.StartsWith("webapp:"))
            {
                string shortcutId = tagText["webapp:".Length..];
                if (_webShortcuts.TryGetValue(shortcutId, out WebAppShortcut? shortcut))
                {
                    // 检查是否点击的是当前已选中的项
                    bool isAlreadySelected = _lastSelectedNavigationItem == args.InvokedItemContainer;
                    
                    if (isAlreadySelected)
                    {
                        // 已选中，触发重启（不改变选中状态，不触发 SelectionChanged）
                        string navigationKey = $"restart:{shortcutId}";
                        
                        if (_navigationDebouncer.ShouldDebounce(navigationKey))
                        {
                            return;
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[NavigationBar] 点击已选中的标签，触发重启: {shortcut.Name}");
                        WebAppRestartRequested?.Invoke(this, shortcutId);
                        return; // ⭐ 不设置选中项，让 SelectionChanged 被抑制
                    }
                    else
                    {
                        // ⭐ 切换到其他标签，设置选中状态并让 SelectionChanged 处理导航
                        NavView.SelectedItem = args.InvokedItemContainer;
                        return; // ⭐ 让 SelectionChanged 处理导航
                    }
                }
                return;
            }

            // ⭐ 其他导航项（首页、AI、新建、设置）：只设置选中项，让 SelectionChanged 处理导航
            // 这样可以避免 ItemInvoked 和 SelectionChanged 重复触发导航
            NavView.SelectedItem = args.InvokedItemContainer;
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_suppressSelectionChanged)
            {
                return;
            }

            if (args.SelectedItemContainer?.Tag is not string tagText)
            {
                return;
            }

            // 当 NavView 中的项被选中时，清除 TopNavView 的选中状态
            if (TopNavView.SelectedItem != null)
            {
                _suppressSelectionChanged = true;
                TopNavView.SelectedItem = null;
                _suppressSelectionChanged = false;
            }

            // ⭐ 修复 Bug 1: 在 SelectionChanged 中统一处理导航，避免与 ItemInvoked 重复
            
            // 处理设置页面导航
            if (tagText == "settings")
            {
                string navigationKey = "settings";
                
                if (_navigationDebouncer.ShouldDebounce(navigationKey))
                {
                    // 防抖触发，恢复之前的选中状态
                    _suppressSelectionChanged = true;
                    NavView.SelectedItem = _lastSelectedNavigationItem;
                    _suppressSelectionChanged = false;
                    return;
                }
                
                _lastSelectedNavigationItem = args.SelectedItemContainer;
                NavigationRequested?.Invoke(this, new NavigationRequest(typeof(SettingsPage), null));
                return;
            }

            // 处理实验室页面导航
            if (tagText == "lab")
            {
                string navigationKey = "lab";
                
                if (_navigationDebouncer.ShouldDebounce(navigationKey))
                {
                    // 防抖触发，恢复之前的选中状态
                    _suppressSelectionChanged = true;
                    NavView.SelectedItem = _lastSelectedNavigationItem;
                    _suppressSelectionChanged = false;
                    return;
                }
                
                _lastSelectedNavigationItem = args.SelectedItemContainer;
                NavigationRequested?.Invoke(this, new NavigationRequest(typeof(LabPage), null));
                return;
            }

            // 处理 WebApp 快捷方式导航（切换标签）
            if (tagText.StartsWith("webapp:"))
            {
                string shortcutId = tagText["webapp:".Length..];
                if (_webShortcuts.TryGetValue(shortcutId, out WebAppShortcut? shortcut))
                {
                    // 防抖检查：避免快速点击创建多个标签页
                    string navigationKey = $"webapp:{shortcutId}";
                    
                    if (_navigationDebouncer.ShouldDebounce(navigationKey))
                    {
                        // 恢复之前的选中状态
                        _suppressSelectionChanged = true;
                        NavView.SelectedItem = _lastSelectedNavigationItem;
                        _suppressSelectionChanged = false;
                        return;
                    }
                    
                    _lastSelectedNavigationItem = args.SelectedItemContainer;
                    
                    // 触发导航（只在切换标签时，重启由 ItemInvoked 处理）
                    NavigationRequested?.Invoke(this, new NavigationRequest(typeof(WebBrowserPage), shortcut));
                }
                return;
            }

            // 处理普通页面导航（首页、AI、新建）
            if (int.TryParse(tagText, out int sectionIndex))
            {
                Type pageType = sectionIndex switch
                {
                    0 => typeof(HomePage),
                    1 => typeof(NewPage),
                    2 => typeof(AIPage),
                    _ => typeof(HomePage)
                };

                // 防抖检查
                string navKey = $"section:{sectionIndex}";
                
                if (_navigationDebouncer.ShouldDebounce(navKey))
                {
                    // 防抖触发，恢复之前的选中状态
                    _suppressSelectionChanged = true;
                    NavView.SelectedItem = _lastSelectedNavigationItem;
                    _suppressSelectionChanged = false;
                    return;
                }

                _lastSelectedNavigationItem = args.SelectedItemContainer;
                NavigationRequested?.Invoke(this, new NavigationRequest(pageType, null));
                return;
            }

            // 其他情况：只更新选中记录，不触发导航
            _lastSelectedNavigationItem = args.SelectedItemContainer;
        }

        private void SettingsNavigationItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "PointerOver");
        }

        private void SettingsNavigationItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "Normal");
        }

        private void BackNavigationItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackAnimatedIcon, "PointerOver");
        }

        private void BackNavigationItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(BackAnimatedIcon, "Normal");
        }

        private void OnUnpinShortcutClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string shortcutId)
            {
                // 使用统一删除服务
                _ = WebAppDeletionService.DeleteWithAnimationAsync(shortcutId);
            }
        }

        private void OnDeletionStarting(object? sender, string appId)
        {
            // 找到导航项并播放淡出动画
            if (_webShortcutItems.TryGetValue(appId, out NavigationViewItem? navItem))
            {
                var fadeOutAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(250))
                };

                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                storyboard.Children.Add(fadeOutAnimation);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOutAnimation, navItem);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOutAnimation, "Opacity");
                storyboard.Begin();
            }
        }

        private void OnDeletionCompleted(object? sender, string appId)
        {
            // 删除完成后移除导航项
            RemoveShortcut(appId);
        }

        private async void OnUpdateCompleted(object? sender, WebAppUpdateEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 收到更新通知: {e.AppId}, 类型: {e.UpdateType}");

                // 从存储重新加载数据
                var shortcuts = await WebAppShortcutStore.LoadAsync();
                var updatedShortcut = shortcuts.FirstOrDefault(s => s.Id == e.AppId);
                
                if (updatedShortcut == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 未找到更新的快捷方式: {e.AppId}");
                    return;
                }

                // 更新内存缓存
                _webShortcuts[e.AppId] = updatedShortcut;

                // 找到对应的导航项
                if (!_webShortcutItems.TryGetValue(e.AppId, out NavigationViewItem? navItem))
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 未找到导航项: {e.AppId}");
                    return;
                }

                // ⭐ 细粒度更新：只更新变化的属性（避免重新创建图标导致闪烁）
                if (e.UpdateType.HasFlag(WebAppUpdateType.Name))
                {
                    navItem.Content = updatedShortcut.Name;
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新名称: {updatedShortcut.Name}");
                }

                if (e.UpdateType.HasFlag(WebAppUpdateType.Icon))
                {
                    // ⚠️ 尝试复用现有图标对象
                    if (_webShortcutIconCache.TryGetValue(e.AppId, out ImageIcon? cachedIcon) &&
                        cachedIcon.Source is BitmapImage existingBitmap)
                    {
                        // 更新现有 BitmapImage 的 URI（避免重新创建）
                        string cacheDir = Path.Combine(
                            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                            "web-icons");
                        Directory.CreateDirectory(cacheDir);
                        string extension = DetectImageExtension(updatedShortcut.IconBytes ?? Array.Empty<byte>());
                        string iconPath = Path.Combine(cacheDir, $"{updatedShortcut.Id}{extension}");

                        if (updatedShortcut.IconBytes is { Length: > 0 })
                        {
                            File.WriteAllBytes(iconPath, updatedShortcut.IconBytes);
                            existingBitmap.UriSource = new Uri(iconPath);
                            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 复用图标对象，更新 URI: {iconPath}");
                        }
                        else
                        {
                            // 重置为默认图标
                            navItem.Icon = new FontIcon { Glyph = "\uE774" };
                            _webShortcutIconCache.Remove(e.AppId);
                            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 重置为地球图标: {e.AppId}");
                        }
                    }
                    else
                    {
                        // 没有缓存或不是 ImageIcon，重新创建
                        navItem.Icon = BuildShortcutIcon(updatedShortcut);
                        System.Diagnostics.Debug.WriteLine($"[NavigationBar] 重新创建图标: {e.AppId}");
                    }
                }

                // URL 变化不需要更新 UI（只存储在 Tag 中）

                await PersistShortcutsAsync();
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新完成: {e.AppId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新失败: {e.AppId}, {ex}");
            }
        }

        private void RemoveShortcut(string shortcutId)
        {
            if (!_webShortcuts.Remove(shortcutId))
            {
                return;
            }

            if (_webShortcutItems.TryGetValue(shortcutId, out NavigationViewItem? navItem))
            {
                NavView.MenuItems.Remove(navItem);
                _webShortcutItems.Remove(shortcutId);
                _webShortcutIconCache.Remove(shortcutId); // ⭐ 清除图标缓存

                if (NavView.SelectedItem is NavigationViewItem selectedItem && selectedItem == navItem)
                {
                    NavView.SelectedItem = HomeNavigationItem;
                    NavigationRequested?.Invoke(this, new NavigationRequest(typeof(HomePage), null));
                }
            }

            // 触发快捷方式移除事件，通知清除缓存
            ShortcutRemoved?.Invoke(this, shortcutId);

            // 取消链接 WebView 实例
            WebViewManager.Unlink(shortcutId);

            _ = PersistShortcutsAsync();
        }

    }

    public sealed class NavigationRequest
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        public Type PageType { get; }
        public object? Parameter { get; }

        public NavigationRequest(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pageType,
            object? parameter)
        {
            PageType = pageType;
            Parameter = parameter;
        }
    }
}
