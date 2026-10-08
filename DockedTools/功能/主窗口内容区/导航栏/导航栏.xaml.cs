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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

        /// <summary>
        /// appId → 这一条当前「已经在显示」的图标标识（缓存文件路径 / 在线 favicon Uri / "globe"）。
        ///
        /// 用途只有一个：图标内容没变就一次都别碰 <c>NavigationViewItem.Icon</c>。
        /// WinUI 的 Image 换 Source 必然经过一个空白帧（microsoft-ui-xaml#8750），
        /// 而 favicon 更新通知是会重复来的（同一张图也会被反复推送），
        /// 不去重的话就是「同一张图反复重挂」—— 官方 issue 里点名的、最容易看见的闪烁源。
        /// </summary>
        private readonly Dictionary<string, string> _webShortcutIconKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>兜底地球图标在 _webShortcutIconKeys 里的标识</summary>
        private const string GlobeIconKey = "globe";
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

            // ⭐ 主题变了要重建图标：单色 SVG 是「按当前主题上色后再落盘」的，
            //    主题一换文件内容就变（文件名带内容哈希 ⇒ 路径必变），
            //    但常驻的侧边栏不会自己再去取一次 —— 不主动刷新的话
            //    图标就停在旧主题的颜色上（深色底下是黑图标，等于看不见）。
            ActualThemeChanged += OnActualThemeChanged;

            Unloaded += (_, _) =>
            {
                WebAppEventBus.ShortcutCreated -= OnShortcutCreated;
                WebAppEventBus.ShortcutsRefreshRequested -= OnShortcutsRefreshRequested;
                WebAppDeletionService.DeletionStarting -= OnDeletionStarting;
                WebAppDeletionService.DeletionCompleted -= OnDeletionCompleted;
                WebAppUpdateService.UpdateCompleted -= OnUpdateCompleted;
                ActualThemeChanged -= OnActualThemeChanged;
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

        /// <summary>
        /// 主题切换后重建全部网页应用图标。
        ///
        /// 为什么必须清一次去重记录：那张表的语义是「内容没变就一次都别动」，
        /// 而这里恰恰是靠「落盘内容变了 ⇒ 路径变了」来触发重建的 ——
        /// 不清的话每条都会命中「跟当前一样」直接 return，一条都不会更新。
        /// </summary>
        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            _webShortcutIconKeys.Clear();

            foreach (KeyValuePair<string, NavigationViewItem> pair in _webShortcutItems)
            {
                if (!_webShortcuts.TryGetValue(pair.Key, out WebAppShortcut? shortcut))
                {
                    continue;
                }

                // 首屏那套参数：批量刷新不等解码、不淡入（几十个一起淡太吵）
                _ = UpdateShortcutIconAsync(pair.Value, shortcut, fade: false, waitForDecode: false);
            }
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
                _ = UpdateShortcutIconAsync(existingItem, shortcut, fade: true, waitForDecode: false);
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
                // ⭐ 占位必须是「有尺寸但看不见」的 IconElement：
                //   直接留 null 的话条目里没有图标槽，文字会往左顶，等真图标上来又往右跳 —— 抖得比闪还难看。
                //   用透明地球占住位置，解码完了再换成真图标。
                Icon = new FontIcon { Glyph = "\uE774", Opacity = 0 }
            };

            // 首屏不等解码：几十个条目一起等只会堆出一堆并发任务和 UI 线程延续，
            //   而这会儿根本没有"旧图被换掉"这回事，没有可闪的。
            _ = UpdateShortcutIconAsync(navItem, shortcut, fade: false, waitForDecode: false);

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

        /// <summary>
        /// 换图标并淡入。
        ///
        /// 前提是传进来的图标已经解码完成（见 <see cref="WebAppIconCache.WaitUntilReadyAsync"/>）：
        /// Image 换 Source 本身就有一帧空白，未解码的源会把这帧拉长成整个解码耗时 —— 那就是「闪」。
        /// 解码完再换，剩下的是一次同帧替换，这里的淡入只是让它出现得柔和一点。
        /// </summary>
        /// <param name="fade">false 用于首次创建（启动时几十个图标一起淡入反而吵）</param>
        private static void SetNavItemIcon(NavigationViewItem navItem, IconElement icon, bool fade)
        {
            if (!fade)
            {
                navItem.Icon = icon;
                return;
            }

            icon.Opacity = 0;
            navItem.Icon = icon;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                // Opacity 属于依赖式动画（dependent），不显式开这个开关 WinUI 会静默忽略它
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(animation, icon);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        /// <summary>
        /// 算出这个快捷方式该用什么图标，按需等它解码完，再挂到条目上。
        /// </summary>
        /// <param name="fade">要不要淡入（首次创建 / 批量加载时不要，几十个一起淡太吵）</param>
        /// <param name="waitForDecode">
        /// 要不要等解码完成再上树。
        /// <b>只有运行中的单条 favicon 更新才该开</b> —— 那才是会闪的场景。
        /// 启动时几十个条目一起等就是几十个并发任务 + 几十次延续回 UI 线程，
        /// 首屏本来就一次性铺完，没有"旧图被换掉"这回事，等它纯属自找负载。
        /// </param>
        private async Task UpdateShortcutIconAsync(NavigationViewItem navItem, WebAppShortcut shortcut, bool fade, bool waitForDecode)
        {
            try
            {
                (IconElement icon, string key) = await BuildShortcutIconAsync(shortcut, waitForDecode);

                // ⭐ 后面要动 NavigationViewItem，必须在 UI 线程。
                //    await 之后线程不保证还是 UI 线程（被调用的库方法随时可能 ConfigureAwait(false)），
                //    跨线程碰 XAML 元素会抛异常，而这个方法是 fire-and-forget 调用的，
                //    异常一被吞掉表现出来就是「图标全没了」—— 踩过一次。
                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => ApplyShortcutIcon(navItem, shortcut.Id, icon, key, fade));
                    return;
                }

                ApplyShortcutIcon(navItem, shortcut.Id, icon, key, fade);
            }
            catch (Exception ex)
            {
                // fire-and-forget：不接住的话异常静悄悄消失，界面上只看到"图标没了"
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新图标异常: {shortcut.Id}, {ex}");
            }
        }

        private void ApplyShortcutIcon(NavigationViewItem navItem, string shortcutId, IconElement icon, string key, bool fade)
        {
            // ⭐ 内容没变就一次都别动 —— 理由见 _webShortcutIconKeys 的注释。
            //    favicon 通知会重复推同一张图，不去重就是反复重挂同一个源，闪得最厉害的正是这种情况。
            if (_webShortcutIconKeys.TryGetValue(shortcutId, out string? current) &&
                string.Equals(current, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _webShortcutIconKeys[shortcutId] = key;
            SetNavItemIcon(navItem, icon, fade);
        }

        /// <summary>
        /// 返回「图标 + 它对应的标识」。标识用来判断内容到底变没变：
        /// 本地缓存用文件路径（文件名带内容哈希，内容一变路径必变），在线的用 Uri，都没有就是 globe。
        /// </summary>
        private async Task<(IconElement Icon, string Key)> BuildShortcutIconAsync(WebAppShortcut shortcut, bool waitForDecode)
        {
            // 尝试从 IconBytes 加载。文件名带内容哈希：图标一换路径就换，
            // BitmapImage 那个「同 Uri 不重新解码」的坑自动就没了。
            if (shortcut.IconBytes is { Length: > 0 })
            {
                try
                {
                    // 走 SaveAsync（而不是 Save）：光栅化出来的 PNG 里那层颜色是按旧主题算的，
                    // 这一步会按需用当前主题重画一张 —— 换主题后图标跟着变色全靠它。
                    //
                    // ⭐ 按【侧边栏自己的主题】落盘，而不是顶栏主题：
                    //    图标是落盘时染好色的，按谁的主题染就得给谁看。
                    //    侧边栏跟的是元素自己的 ActualTheme，跟顶栏那份页面级主题未必一样。
                    string? iconPath = await WebAppIconCache.SaveAsync(shortcut.Id, shortcut.IconBytes, ActualTheme);
                    if (iconPath is not null)
                    {
                        ImageIcon? icon = await TryCreateImageIconAsync(new Uri(iconPath), shortcut.Id, waitForDecode);
                        if (icon is not null)
                        {
                            return (icon, iconPath);
                        }

                        // 能落盘却解不出来 = 坏文件，删掉，否则下次又把它翻出来
                        WebAppIconCache.Delete(shortcut.Id);
                    }

                    // ⭐ 有 IconBytes 却拿不到图标时不再去网上兜底：
                    //    缓存是我们自己按内容写的，解不出来基本就是坏了，
                    //    再发一次网络请求只会把「图标位空着」的时间拖得更长。
                    return (new FontIcon { Glyph = "\uE774" }, GlobeIconKey);
                }
                catch (Exception ex)
                {
                    // ⭐ 落盘失败（磁盘 / 权限之类）时直接交地球图标，不要往下走：
                    //    下面第一件事就是 Delete 缓存，好好的缓存会因为一次 IO 失败被清掉。
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 保存图标失败: {shortcut.Id}, {ex.Message}");
                    return (new FontIcon { Glyph = "\uE774" }, GlobeIconKey);
                }
            }

            // ⭐ 走到这里说明没有 IconBytes（图标被重置 / 还没抓到）。
            // 必须把磁盘缓存一起清掉，否则下一次又把上一张图翻出来，
            // 「重置图标」看起来就像没生效。清完就走下面的在线兜底 / 地球图标。
            WebAppIconCache.Delete(shortcut.Id);

            // 尝试从网站 favicon 加载
            if (Uri.TryCreate(shortcut.Url, UriKind.Absolute, out Uri? websiteUri))
            {
                try
                {
                    Uri faviconUri = new Uri(websiteUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
                    ImageIcon? icon = await TryCreateImageIconAsync(faviconUri, shortcut.Id, waitForDecode);
                    if (icon is not null)
                    {
                        return (icon, faviconUri.AbsoluteUri);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 创建 Favicon URI 失败: {shortcut.Url}, {ex.Message}");
                }
            }

            // 所有方法都失败时，返回地球图标作为后备
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 所有图标加载方法失败，使用地球图标: {shortcut.Name}");
            return (new FontIcon { Glyph = "\uE774" }, GlobeIconKey);
        }

        private async Task<ImageIcon?> TryCreateImageIconAsync(Uri imageUri, string shortcutId, bool waitForDecode)
        {
            try
            {
                // SVG 走 SvgImageSource，其余走 BitmapImage —— 分叉在 WebAppIconCache 里，
                // 失败回调对两种源是同一份（位图的 ImageFailed / SVG 的 OpenFailed 都接到这里）
                ImageSource source = WebAppIconCache.CreateImageSource(imageUri, () => FallbackToGlobeIcon(imageUri, shortcutId));

                // ⚠️ 只对「运行中用新图换旧图」这条路径等解码 —— 千万别顺手把本地文件也等上。
                //
                //    实测教训：试过 waitForDecode || imageUri.IsFile，结果启动日志里每张图标
                //    都来一条「等图像就绪超时（1200ms）」，26 个应用 26 条。
                //    原因是我们 await 的时候 ImageIcon 还没挂上可视树，而 WinUI 对
                //    BitmapImage 的解码是【等有人要画它才开始】的 —— 没人要 ⇒ 不解码
                //    ⇒ ImageOpened / ImageFailed 都不来 ⇒ 必然等到超时。
                //    也就是：这种做法注定只能拿到 Timeout，白等 1.2 秒还拖慢图标上树。
                //
                //    那张图到底画不画得出来，交给 CreateImageSource 的失败回调去判
                //    （位图 ImageFailed / SVG OpenFailed → FallbackToGlobeIcon），
                //    它是上树之后才触发的，才是真正可靠的信号。
                if (waitForDecode)
                {
                    // ⭐ 等解码真正完成再交出 ImageIcon（microsoft-ui-xaml#8750：
                    //    换 Source 必然经过一个空白帧，未解码的源会把这帧拉长成整个解码耗时）。
                    ImageLoadState state = await WebAppIconCache.WaitUntilReadyAsync(source, 1500);

                    if (state == ImageLoadState.Failed)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NavigationBar] 图标加载失败: {imageUri}");
                        return null;
                    }

                    if (state == ImageLoadState.Timeout)
                    {
                        // ⭐ 没确认好坏就别当坏的处理：照旧上树，最坏也就是跟"不等"一样（可能闪一下），
                        //    总比为了防闪把图标整没了强。
                        System.Diagnostics.Debug.WriteLine($"[NavigationBar] 图标就绪状态未知，按原样使用: {imageUri}");
                    }
                }

                return new ImageIcon { Source = source };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationBar] 创建 ImageIcon 失败: {imageUri}, {ex.Message}");
                return null;
            }
        }

        private void FallbackToGlobeIcon(Uri imageUri, string shortcutId)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigationBar] 图标加载失败: {imageUri}");

            // ⭐ 坏文件必须从磁盘缓存里删掉：留着的话下次又会把它翻出来，
            //    表现就是「这应用的图标一直是坏的」。
            if (imageUri.IsFile && File.Exists(imageUri.LocalPath))
            {
                WebAppIconCache.Delete(shortcutId);
            }

            // 在 UI 线程上切换到地球图标（也走淡入，跟正常换图标一个观感）
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_webShortcutIconKeys.TryGetValue(shortcutId, out string? current) &&
                    string.Equals(current, GlobeIconKey, StringComparison.OrdinalIgnoreCase))
                {
                    return; // 已经是地球了，别再挂一次
                }

                if (_webShortcutItems.TryGetValue(shortcutId, out NavigationViewItem? navItem))
                {
                    _webShortcutIconKeys[shortcutId] = GlobeIconKey;
                    SetNavItemIcon(navItem, new FontIcon { Glyph = "\uE774" }, fade: true);
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 已切换到地球图标: {shortcutId}");
                }
            });
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
                    // ⭐ 只有这里是"运行中用新图换掉旧图"，会闪的就是它 —— 走完整防闪链路：
                    //    先等解码完成，确认内容真变了才换，换了再淡入。
                    //    缓存文件名带内容哈希，内容变了路径必变，所以「内容变没变」这个判断是准的。
                    _ = UpdateShortcutIconAsync(navItem, updatedShortcut, fade: true, waitForDecode: true);
                    System.Diagnostics.Debug.WriteLine($"[NavigationBar] 更新图标: {e.AppId}");
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
                _webShortcutIconKeys.Remove(shortcutId);
                WebAppIconCache.Delete(shortcutId);       // ⭐ 顺带清掉磁盘上的图标文件

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
