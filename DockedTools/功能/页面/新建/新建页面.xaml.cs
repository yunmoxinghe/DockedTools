using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using DockedTools.Features.Pages.WebApp;
using DockedTools.Features.Pages.WebApp.EdgeSync;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Pages.Settings; // ⭐ 引用设置页面命名空间以使用 ExperimentalSettings
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using DockedTools.Features.Localization;

namespace DockedTools.Features.Pages.New
{
    /// <summary>
    /// ⭐ NewPage 现在使用主 Frame 导航到子页面（与 SettingsPage 一致），不再需要 IBackHandler
    /// </summary>
    public sealed partial class NewPage : Page
    {
        private readonly 智能标题 _智能标题 = new();
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        public NewPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, CreateScrollViewer, PageTitleBlock);

            System.Diagnostics.Debug.WriteLine($"NewPage.OnNavigatedTo called with parameter: {e.Parameter}");

            // ⭐ 移除 SubPageFrame 相关逻辑，NewPage 现在使用主 Frame 导航
            // 如果有 URL 参数，说明是从其他地方导航过来的，应该继续导航到 WebAppPage
            if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                System.Diagnostics.Debug.WriteLine($"NewPage: navigating to WebAppPage with URL: {url}");
                var animationType = ExperimentalSettings.SubPageNavigationAnimation;
                var transitionInfo = GetNavigationTransitionInfo(animationType);
                Frame.Navigate(typeof(WebAppPage), url, transitionInfo);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateVisualState();
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

        private void PinWebCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // ⭐ 改为使用主 Frame 导航（与设置页面一致）
            var animationType = ExperimentalSettings.SubPageNavigationAnimation;
            var transitionInfo = GetNavigationTransitionInfo(animationType);
            Frame.Navigate(typeof(WebAppPage), null, transitionInfo);
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

        // Edge 收藏夹 - 选择文件夹导入
        private async void OnSelectFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 检查 Edge 收藏夹是否可用
                if (!EdgeBookmarkSyncService.IsEdgeBookmarksAvailable())
                {
                    var errorDialog = CreateMessageDialog(
                        "提示",
                        "未找到 Microsoft Edge 收藏夹文件。\n请确保已安装 Edge 浏览器并至少启动过一次。",
                        closeButtonText: "确定");
                    await InAppDialogService.ShowAsync(errorDialog, this);
                    return;
                }

                // 获取所有文件夹
                var folders = await EdgeBookmarkSyncService.GetBookmarkFoldersAsync();
                
                if (folders.Count == 0)
                {
                    var errorDialog = CreateMessageDialog(
                        "提示",
                        "Edge 收藏夹中没有找到文件夹。",
                        closeButtonText: "确定");
                    await InAppDialogService.ShowAsync(errorDialog, this);
                    return;
                }

                // 创建文件夹选择对话框
                var listView = new ListView
                {
                    ItemsSource = folders,
                    SelectionMode = ListViewSelectionMode.Single,
                    MaxHeight = 400
                };

                var dialog = new UnifiedInAppDialog();
                dialog.Configure(
                    LocalizationHelper.GetString("NewPage_SelectFolderDialogTitle"),
                    listView,
                    LocalizationHelper.GetString("NewPage_ImportButton"),
                    LocalizationHelper.GetString("NewPage_CancelButton"),
                    defaultButton: ContentDialogButton.Primary);

                var result = await InAppDialogService.ShowAsync(dialog, this);
                
                if (result == ContentDialogResult.Primary && listView.SelectedItem is string selectedFolder)
                {
                    await ImportFromFolderAsync(selectedFolder);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NewPage] OnSelectFolderClick error: {ex}");
                
                var errorDialog = CreateMessageDialog(
                    "错误",
                    $"选择文件夹时发生错误：\n{ex.Message}",
                    closeButtonText: "确定");
                await InAppDialogService.ShowAsync(errorDialog, this);
            }
        }

        // Edge 收藏夹 - 导入全部
        private async void OnImportAllClick(object sender, RoutedEventArgs e)
        {
            await ImportFromFolderAsync(null);
        }

        // 执行导入操作
        private async System.Threading.Tasks.Task ImportFromFolderAsync(string? folderPath)
        {
            try
            {
                // 检查 Edge 收藏夹是否可用
                if (!EdgeBookmarkSyncService.IsEdgeBookmarksAvailable())
                {
                    var errorDialog = CreateMessageDialog(
                        "提示",
                        "未找到 Microsoft Edge 收藏夹文件。\n请确保已安装 Edge 浏览器并至少启动过一次。",
                        closeButtonText: "确定");
                    await InAppDialogService.ShowAsync(errorDialog, this);
                    return;
                }

                // 检查 Edge 是否正在运行
                if (IsEdgeRunning())
                {
                    var warningDialog = CreateMessageDialog(
                        "需要关闭 Edge 浏览器",
                        "检测到 Microsoft Edge 浏览器正在运行。\n\n" +
                        "为了成功导入收藏夹图标，请先关闭 Edge 浏览器，然后再继续导入操作。\n\n" +
                        "如果继续，仍然可以导入收藏夹，但可能无法获取完整的图标。",
                        primaryButtonText: "仍然继续",
                        closeButtonText: "取消",
                        defaultButton: ContentDialogButton.Close);
                    
                    var warningResult = await InAppDialogService.ShowAsync(warningDialog, this);
                    if (warningResult != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                // 显示确认对话框
                var confirmMessage = string.IsNullOrEmpty(folderPath)
                    ? "即将导入 Edge 浏览器的所有收藏夹到侧边栏。\n已存在的网址不会重复添加。\n\n是否继续？"
                    : $"即将导入文件夹「{folderPath}」中的收藏夹到侧边栏。\n已存在的网址不会重复添加。\n\n是否继续？";

                var confirmDialog = CreateMessageDialog(
                    "导入 Edge 收藏夹",
                    confirmMessage,
                    primaryButtonText: "导入",
                    closeButtonText: "取消",
                    defaultButton: ContentDialogButton.Primary);
                
                var confirmResult = await InAppDialogService.ShowAsync(confirmDialog, this);
                if (confirmResult != ContentDialogResult.Primary)
                {
                    return;
                }

                // 设置要同步的文件夹路径
                EdgeBookmarkSyncService.SyncFolderPath = folderPath ?? "";

                // 执行导入（不显示进度对话框，直接导入）
                var result = await EdgeBookmarkSyncService.SyncFromEdgeAsync();

                // 显示结果
                if (result.Success)
                {
                    // 如果有新增的书签，触发 UI 刷新
                    if (result.AddedCount > 0)
                    {
                        // 通知主页和侧边栏刷新
                        WebAppEventBus.RequestRefresh();
                    }

                    // 根据结果消息判断是否有图标加载问题
                    string message;
                    if (result.Message.Contains("图标加载失败") || result.Message.Contains("Edge 正在运行"))
                    {
                        message = $"成功导入 {result.AddedCount} 个新书签！\n\n" +
                                 "⚠️ 图标加载失败（Edge 浏览器正在运行）\n" +
                                 "提示：关闭 Edge 后重新导入可获取完整图标";
                    }
                    else if (result.Message.Contains("未找到图标"))
                    {
                        message = $"成功导入 {result.AddedCount} 个新书签！\n\n" +
                                 "提示：部分书签未找到图标";
                    }
                    else
                    {
                        message = $"成功导入 {result.AddedCount} 个新书签！\n\n你可以在主页和侧边栏中看到它们。";
                    }

                    var successDialog = CreateMessageDialog(
                        "导入完成",
                        message,
                        closeButtonText: "确定");
                    await InAppDialogService.ShowAsync(successDialog, this);
                }
                else
                {
                    var errorDialog = CreateMessageDialog(
                        "导入失败",
                        result.Message,
                        closeButtonText: "确定");
                    await InAppDialogService.ShowAsync(errorDialog, this);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NewPage] ImportFromFolderAsync error: {ex}");
                
                var errorDialog = CreateMessageDialog(
                    "错误",
                    $"导入过程中发生错误：\n{ex.Message}",
                    closeButtonText: "确定");
                await InAppDialogService.ShowAsync(errorDialog, this);
            }
        }

        private static UnifiedInAppDialog CreateMessageDialog(
            string title,
            string message,
            string? primaryButtonText = null,
            string? closeButtonText = null,
            ContentDialogButton defaultButton = ContentDialogButton.Close)
        {
            var dialog = new UnifiedInAppDialog();
            dialog.Configure(
                title,
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                },
                primaryButtonText,
                closeButtonText,
                defaultButton: defaultButton);
            return dialog;
        }

        /// <summary>
        /// 检查 Edge 浏览器是否正在运行
        /// </summary>
        private static bool IsEdgeRunning()
        {
            try
            {
                var edgeProcesses = System.Diagnostics.Process.GetProcessesByName("msedge");
                bool isRunning = edgeProcesses.Length > 0;
                
                // 释放进程资源
                foreach (var process in edgeProcesses)
                {
                    process.Dispose();
                }
                
                System.Diagnostics.Debug.WriteLine($"[NewPage] Edge running: {isRunning} ({edgeProcesses.Length} processes)");
                return isRunning;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NewPage] Failed to check Edge process: {ex.Message}");
                // 如果无法检测，假设没有运行
                return false;
            }
        }
    }
}
