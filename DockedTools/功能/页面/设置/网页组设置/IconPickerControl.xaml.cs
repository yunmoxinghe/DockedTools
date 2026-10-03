using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.Localization;

namespace DockedAI.功能.页面.设置.网页组设置
{
    /// <summary>
    /// 图标选择器用户控件（使用 ItemsView + UniformGridLayout 实现虚拟化）
    /// </summary>
    public sealed partial class IconPickerControl : UserControl
    {
        private List<IconData> _allIcons = new();
        private string _currentSearch = string.Empty;
        private string? _selectedIconCode; // 存储十六进制 Code 而不是 Glyph

        public IconPickerControl()
        {
            this.InitializeComponent();
            this.Loaded += IconPickerControl_Loaded;
        }

        /// <summary>
        /// 获取当前选中的图标十六进制 Code（例如：E8FB）
        /// </summary>
        public string? SelectedIconCode => _selectedIconCode;

        /// <summary>
        /// 设置初始选中的图标（使用十六进制 Code）
        /// </summary>
        public void SetInitialSelection(string? code)
        {
            _selectedIconCode = code;
        }

        /// <summary>
        /// 异步加载图标数据
        /// </summary>
        private async void IconPickerControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 异步加载图标数据（避免阻塞 UI 线程）
                var dataSource = IconsDataSource.Instance;
                _allIcons = await dataSource.LoadIconsAsync();

                System.Diagnostics.Debug.WriteLine($"[IconPickerControl] 成功加载 {_allIcons.Count} 个图标");

                // 在 UI 线程上更新界面
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                {
                    IconsItemsView.ItemsSource = _allIcons;
                    CountTextBlock.Text = string.Format(LocalizationHelper.GetString("IconPicker_CountFormat"), _allIcons.Count);

                    // 恢复之前的选中状态（根据 Code 查找）
                    if (!string.IsNullOrEmpty(_selectedIconCode))
                    {
                        var index = _allIcons.FindIndex(icon => icon.Code.Equals(_selectedIconCode, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0)
                        {
                            IconsItemsView.Select(index);
                            System.Diagnostics.Debug.WriteLine($"[IconPickerControl] 恢复选中: index={index}, code={_selectedIconCode}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[IconPickerControl] 未找到匹配的图标: code={_selectedIconCode}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconPickerControl] 加载图标失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 搜索框文本变化事件
        /// </summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            Filter(textBox.Text);
        }

        /// <summary>
        /// 过滤图标（多线程搜索，参考 WinUI Gallery 实现）
        /// </summary>
        private void Filter(string search)
        {
            // 清空列表，给用户搜索中的反馈
            IconsItemsView.ItemsSource = null;
            CountTextBlock.Text = LocalizationHelper.GetString("IconPicker_Searching");

            // 更新当前搜索词（用于中断旧的搜索线程）
            _currentSearch = search;

            string[] filter = search.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            // 启动后台线程进行搜索（避免阻塞 UI）
            // ⚠️ 必须显式 IsBackground = true：new Thread 默认是前台线程，
            // 只要它还活着进程就不会退出 —— 这正是「点了退出进程却吊着」的经典成因。
            new Thread(() =>
            {
                var newItems = new List<IconData>();

                foreach (var item in _allIcons)
                {
                    // 如果搜索词已变化，中断当前线程
                    if (search != _currentSearch) return;

                    // 检查是否匹配搜索词
                    var fitsFilter = string.IsNullOrEmpty(search) ||
                        filter.All(entry =>
                            item.Code.Contains(entry, StringComparison.CurrentCultureIgnoreCase) ||
                            item.Name.Contains(entry, StringComparison.CurrentCultureIgnoreCase) ||
                            (item.Tags != null && item.Tags.Any(tag =>
                                !string.IsNullOrEmpty(tag) &&
                                tag.Contains(entry, StringComparison.CurrentCultureIgnoreCase))));

                    if (fitsFilter)
                    {
                        newItems.Add(item);
                    }
                }

                // 再次检查搜索词是否已变化
                if (search != _currentSearch) return;

                // 在 UI 线程上更新结果
                DispatcherQueue.TryEnqueue(() =>
                {
                    IconsItemsView.ItemsSource = newItems;

                    var count = newItems.Count;
                    CountTextBlock.Text = count > 0
                        ? string.Format(LocalizationHelper.GetString("IconPicker_CountFormat"), count)
                        : LocalizationHelper.GetString("IconPicker_NotFound");

                    // 自动选中第一个结果
                    if (count > 0)
                    {
                        IconsItemsView.Select(0);
                    }
                });
            })
            { IsBackground = true, Name = "IconPickerSearch" }
            .Start();
        }

        /// <summary>
        /// 选择变化事件
        /// </summary>
        private void IconsItemsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
        {
            if (IconsItemsView.ItemsSource is IList<IconData> currentItems)
            {
                if (IconsItemsView.CurrentItemIndex != -1 && IconsItemsView.CurrentItemIndex < currentItems.Count)
                {
                    var selectedIcon = currentItems[IconsItemsView.CurrentItemIndex];
                    _selectedIconCode = selectedIcon.Code;
                    System.Diagnostics.Debug.WriteLine($"[IconPickerControl] 选中: {selectedIcon.Name} (Code={_selectedIconCode})");
                }
            }
        }
    }
}
