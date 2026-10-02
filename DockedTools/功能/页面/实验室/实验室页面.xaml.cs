using System;
using System.IO;
using System.Linq;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.Localization;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using DockedTools.功能.WebView备份.Components;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace DockedTools.Features.Pages.Lab
{
    /// <summary>
    /// 辅助扩展方法
    /// </summary>
    internal static class ControlExtensions
    {
        public static T Apply<T>(this T control, Action<T> action)
        {
            action(control);
            return control;
        }
    }

    public sealed partial class LabPage : Page
    {
        private readonly 智能标题 _智能标题 = new();
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        public LabPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            
            // 订阅窗口最大化状态变化事件
            WindowMaximizedStateChanged += OnWindowMaximizedStateChanged;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(PageScrollViewer, PageTitleBlock);
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 初始化顶部应用栏菜单按钮设置
            TopBarMenuButtonToggle.IsOn = ExperimentalSettings.EnableTopBarMenuButton;

            // 初始化顶部应用栏可见性测试控件状态
            TopBarVisibilityToggle.IsOn = TopAppBarService.IsVisible;
            TopBarVisibilityToggle.Toggled += OnTopBarVisibilityToggled;

            // 应用当前设置（返回按钮由 CanGoBack 自动驱动，无需手动设置）
            TopAppBarService.SetMenuButtonVisible(ExperimentalSettings.EnableTopBarMenuButton);

            // 初始化托盘评价按钮设置
            HideTrayRateButtonToggle.IsOn = ExperimentalSettings.HideTrayRateButton;

            // 初始化 AI 实验室设置
            AILabToggle.IsOn = ExperimentalSettings.EnableAILab;

            // 初始化 WinUI 右键菜单设置
            WinUIContextMenuToggle.IsOn = ExperimentalSettings.EnableWinUIContextMenu;

            // 请求刷新监听器状态
            RequestRefreshMonitorState();

            // 更新局部主题状态显示
            UpdateThemeStatus();

            // 初始化 WebView2 透明背景实验室设置
            InitializeWebViewTransparencyLab();

            UpdateMargin();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 取消订阅事件
            WindowMaximizedStateChanged -= OnWindowMaximizedStateChanged;
        }

        /// <summary>
        /// 窗口最大化状态变化处理
        /// </summary>
        private void OnWindowMaximizedStateChanged(object? sender, bool isMaximized)
        {
            // 确保在 UI 线程上更新
            DispatcherQueue.TryEnqueue(() =>
            {
                if (isMaximized)
                {
                    MaximizedStateIcon.Glyph = "\uE740"; // 最大化图标
                    MaximizedStateIcon.Foreground = new SolidColorBrush(Colors.Orange);
                    MaximizedStateText.Text = LocalizationHelper.GetString("LabPage_WindowMaximized");
                }
                else
                {
                    MaximizedStateIcon.Glyph = "\uE73F"; // 还原图标
                    MaximizedStateIcon.Foreground = new SolidColorBrush(Colors.Green);
                    MaximizedStateText.Text = LocalizationHelper.GetString("LabPage_WindowNotMaximized");
                }
                
                System.Diagnostics.Debug.WriteLine($"[LabPage] UI updated: isMaximized={isMaximized}");
            });
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (System.Math.Abs(e.NewSize.Width - _lastMeasuredWidth) < 1) return;
            UpdateMargin();
        }

        private void UpdateMargin()
        {
            double width = RootGrid?.ActualWidth ?? ActualWidth;
            if (width <= 0) return;
            double normalized = System.Math.Clamp((width - MinResponsiveWidth) / (MaxResponsiveWidth - MinResponsiveWidth), 0, 1);
            double margin = System.Math.Round(MinHorizontalMargin + (MaxHorizontalMargin - MinHorizontalMargin) * normalized);
            if (System.Math.Abs(margin - _lastAppliedMargin) > 0.01)
            {
                PageContentPanel.Margin = new Thickness(margin, 0, margin, 0);
                _lastAppliedMargin = margin;
            }
            _lastMeasuredWidth = width;
        }

        private void OnTopBarVisibilityToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
                TopAppBarService.IsVisible = toggle.IsOn;
        }

        private void OnSetRightButtonClick(object sender, RoutedEventArgs e)
        {
            var btn = new Button
            {
                Content = new FontIcon { Glyph = "\uE713", FontSize = 16 },
                Style = (Style)Application.Current.Resources["NavigationBackButtonNormalStyle"],
                Width = 36,
                Height = 36,
            };
            btn.Click += (_, _) =>
            {
                TopAppBarService.SetRightContent(null);
                RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightButtonCleared");
            };
            TopAppBarService.SetRightContent(btn);
            RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightButtonSet");
        }

        private void OnClearRightButtonClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.SetRightContent(null);
            RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightContentCleared");
        }

        private void OnSetCenterTitleClick(object sender, RoutedEventArgs e)
        {
            var text = CenterTitleInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) text = LocalizationHelper.GetString("LabPage_DefaultTitle");
            TopAppBarService.SetCenterContent(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        private void OnClearCenterClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.SetCenterContent(null);
        }

        private void OnToggleTopBarThemeClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.ToggleTheme();
            UpdateThemeStatus();
        }

        private void OnResetTopBarThemeClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.SetTheme(ElementTheme.Default);
            UpdateThemeStatus();
        }

        private void UpdateThemeStatus()
        {
            var actualTheme = TopAppBarService.GetActualTheme();
            var requestedTheme = TopAppBarService.GetRequestedTheme();
            
            string themeText = requestedTheme switch
            {
                ElementTheme.Light => "🌞 亮色模式",
                ElementTheme.Dark => "🌙 深色模式",
                _ => "🔄 跟随系统"
            };
            
            string actualText = actualTheme == ElementTheme.Dark ? "深色" : "亮色";
            
            ThemeStatusText.Text = $"{themeText} (实际: {actualText})";
        }

        private void OnTopBarMenuButtonToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableTopBarMenuButton = toggle.IsOn;
                TopAppBarService.SetMenuButtonVisible(toggle.IsOn);
            }
        }

        private void OnHideTrayRateButtonToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.HideTrayRateButton = toggle.IsOn;
                RaiseHideTrayRateButtonSettingsChanged();
            }
        }

        private void OnHideTrayRateButtonCardClick(object sender, RoutedEventArgs e)
        {
            // 点击卡片时切换 ToggleSwitch 状态
            HideTrayRateButtonToggle.IsOn = !HideTrayRateButtonToggle.IsOn;
        }

        private void OnAILabToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableAILab = toggle.IsOn;
                SettingsPage.RaiseAILabSettingsChanged();
            }
        }

        private void OnAILabCardClick(object sender, RoutedEventArgs e)
        {
            // 点击卡片时切换 ToggleSwitch 状态
            AILabToggle.IsOn = !AILabToggle.IsOn;
        }

        private void OnWinUIContextMenuToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableWinUIContextMenu = toggle.IsOn;
                SettingsPage.RaiseWinUIContextMenuSettingsChanged();
            }
        }

        private void OnWinUIContextMenuCardClick(object sender, RoutedEventArgs e)
        {
            // 点击卡片时切换 ToggleSwitch 状态
            WinUIContextMenuToggle.IsOn = !WinUIContextMenuToggle.IsOn;
        }

        /// <summary>
        /// 快速备份
        /// </summary>
        private async void OnQuickBackupClick(object sender, RoutedEventArgs e)
        {
            // 🔍 自动检测 WebView2 数据路径
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packageFamily = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            
            string[] possiblePaths = { 
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalState", "EBWebView"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "EBWebView"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "Microsoft", "Edge", "User Data"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "Microsoft", "WebView2"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "WebView2"),
            };
            
            string? dataPath = null;
            
            foreach (var testPath in possiblePaths)
            {
                if (System.IO.Directory.Exists(testPath) && System.IO.Directory.GetFileSystemEntries(testPath).Length > 0)
                {
                    dataPath = testPath;
                    System.Diagnostics.Debug.WriteLine($"[WebView备份] 找到数据目录: {dataPath}");
                    break;
                }
            }

            if (dataPath == null || !System.IO.Directory.Exists(dataPath))
            {
                var errorDialog = new UnifiedInAppDialog();
                errorDialog.Configure(
                    "💡 未找到 WebView2 数据",
                    new TextBlock
                    {
                        Text = "还没有 WebView2 数据可以备份哦！\n\n" +
                               "📋 快速开始：\n" +
                               "1️⃣ 打开应用中的任意网页应用（如 ChatGPT、Claude）\n" +
                               "2️⃣ 登录账号并使用一段时间\n" +
                               "3️⃣ 返回此处点击「备份」按钮\n\n" +
                               $"🔍 已搜索路径：\n{string.Join("\n", possiblePaths.Take(3))}\n\n" +
                               $"💡 提示：如果确实已使用过，可能需要重启应用后再试。",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                    },
                    closeButtonText: "知道了"
                );
                await InAppDialogService.ShowAsync(errorDialog, this);
                return;
            }

            // 📁 使用文件夹选择器
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");

            // 🔧 获取窗口句柄 - 使用 XamlRoot 遍历到 Window
            IntPtr hwnd = WindowHandleHelper.GetWindowHandleFromPage(this);
            
            if (hwnd == IntPtr.Zero)
            {
                var errorDialog = new UnifiedInAppDialog();
                errorDialog.Configure(
                    "❌ 无法打开文件选择器",
                    "无法获取窗口句柄，请重启应用后重试。",
                    closeButtonText: "确定"
                );
                await InAppDialogService.ShowAsync(errorDialog, this);
                return;
            }
            
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            var loadingDialog = new UnifiedInAppDialog();
            loadingDialog.Configure(
                "正在备份...",
                new ProgressRing { IsActive = true, Width = 48, Height = 48 }
            );

            var dialogTask = InAppDialogService.ShowAsync(loadingDialog, this);

            try
            {
                var backupService = new DockedTools.功能.WebView备份.Services.WebViewBackupServiceV2(folder.Path);
                var zipPath = await backupService.BackupUserDataFolderAsync(dataPath, "WebView配置");

                loadingDialog.Hide();

                var fileInfo = new System.IO.FileInfo(zipPath);
                var successDialog = new UnifiedInAppDialog();
                successDialog.Configure(
                    "✅ 备份成功",
                    new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"✨ 配置已安全保存！\n\n" +
                                       $"📂 文件位置：\n{zipPath}\n\n" +
                                       $"📦 备份大小：{fileInfo.Length / 1024 / 1024:F2} MB\n" +
                                       $"📅 创建时间：{fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss}",
                                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                            },
                            new Microsoft.UI.Xaml.Controls.Button
                            {
                                Content = "📁 打开文件位置",
                                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch
                            }.Apply(btn => btn.Click += (s, e) =>
                            {
                                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
                            })
                        }
                    },
                    closeButtonText: "完成"
                );
                await InAppDialogService.ShowAsync(successDialog, this);
            }
            catch (System.Exception ex)
            {
                loadingDialog.Hide();

                var errorMessage = ex.Message;
                var helpText = "💡 常见解决方案：\n";
                
                if (ex is UnauthorizedAccessException || ex.Message.Contains("being used"))
                {
                    helpText += "• 关闭所有网页应用窗口\n" +
                               "• 等待 3-5 秒后重试\n" +
                               "• 如仍失败，请重启应用";
                }
                else if (ex is IOException)
                {
                    helpText += "• 检查磁盘空间是否充足\n" +
                               "• 确保有写入权限\n" +
                               "• 尝试选择其他保存位置";
                }
                else
                {
                    helpText += "• 重启应用后重试\n" +
                               "• 检查系统权限设置";
                }

                var errorDialog = new UnifiedInAppDialog();
                errorDialog.Configure(
                    "❌ 备份失败",
                    new TextBlock
                    {
                        Text = $"错误详情：{errorMessage}\n\n{helpText}",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                    },
                    closeButtonText: "确定"
                );
                await InAppDialogService.ShowAsync(errorDialog, this);
            }
        }

        /// <summary>
        /// 快速恢复
        /// </summary>
        private async void OnQuickRestoreClick(object sender, RoutedEventArgs e)
        {
            // 📁 使用文件选择器
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            picker.FileTypeFilter.Add(".网页状态备份");

            // 🔧 获取窗口句柄 - 使用 XamlRoot 遍历到 Window
            IntPtr hwnd = WindowHandleHelper.GetWindowHandleFromPage(this);
            
            if (hwnd == IntPtr.Zero)
            {
                var errorDialog = new UnifiedInAppDialog();
                errorDialog.Configure(
                    "❌ 无法打开文件选择器",
                    "无法获取窗口句柄，请重启应用后重试。",
                    closeButtonText: "确定"
                );
                await InAppDialogService.ShowAsync(errorDialog, this);
                return;
            }
            
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            // 确认恢复
            var result = await InAppDialogService.ShowAsync(new ContentDialog
            {
                Title = "⚠️ 确认恢复配置？",
                Content = new TextBlock
                {
                    Text = $"📦 备份文件：{file.Name}\n" +
                           $"📅 创建时间：{System.IO.File.GetCreationTime(file.Path):yyyy-MM-dd HH:mm:ss}\n\n" +
                           "⚠️ 重要提示：\n" +
                           "• 当前所有 WebView2 数据将被覆盖（包括登录状态、缓存等）\n" +
                           "• 恢复后需要重启应用才能生效\n" +
                           "• 建议在恢复前先备份当前配置\n\n" +
                           "确定要继续吗？",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "确定恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            }, this);

            if (result != ContentDialogResult.Primary) return;

            // 🔍 使用智能路径检测
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packageFamily = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            
            string[] possiblePaths = { 
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalState", "EBWebView"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "EBWebView"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "Microsoft", "Edge", "User Data"),
                System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalCache", "Local", "Microsoft", "WebView2"),
            };
            
            string? dataPath = null;
            
            foreach (var testPath in possiblePaths)
            {
                if (System.IO.Directory.Exists(testPath) && System.IO.Directory.GetFileSystemEntries(testPath).Length > 0)
                {
                    dataPath = testPath;
                    break;
                }
            }
            
            // 如果仍未找到，创建默认目录（LocalState\EBWebView 是实际使用的位置）
            if (dataPath == null)
            {
                dataPath = System.IO.Path.Combine(localAppData, "Packages", packageFamily, "LocalState", "EBWebView");
                System.Diagnostics.Debug.WriteLine($"[WebView恢复] 使用默认路径: {dataPath}");
            }

            var loadingDialog = new ContentDialog
            {
                Title = "正在恢复...",
                Content = new ProgressRing { IsActive = true, Width = 48, Height = 48 }
            };

            var dialogTask = InAppDialogService.ShowAsync(loadingDialog, this);

            try
            {
                var backupService = new DockedTools.功能.WebView备份.Services.WebViewBackupServiceV2();
                await backupService.RestoreUserDataFolderAsync(file.Path, dataPath);

                loadingDialog.Hide();

                var restartResult = await InAppDialogService.ShowAsync(new ContentDialog
                {
                    Title = "✅ 恢复成功",
                    Content = new TextBlock
                    {
                        Text = "🎉 配置已成功恢复！\n\n" +
                               "⚠️ 重要提示：\n" +
                               "• 请完全退出应用（而非最小化）\n" +
                               "• 重新启动应用以使更改生效\n" +
                               "• 重启后，登录状态和设置将恢复到备份时的状态\n\n" +
                               "💡 建议现在就重启应用！",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                    },
                    PrimaryButtonText = "立即重启",
                    CloseButtonText = "稍后手动重启"
                }, this);
                
                if (restartResult == ContentDialogResult.Primary)
                {
                    DockedTools.功能.统一调用.AppRestartService.Restart();
                }
            }
            catch (System.Exception ex)
            {
                loadingDialog.Hide();

                await InAppDialogService.ShowAsync(new ContentDialog
                {
                    Title = "❌ 恢复失败",
                    Content = $"{ex.Message}\n\n💡 提示：请关闭所有网页应用页面后重试。",
                    CloseButtonText = "确定"
                }, this);
            }
        }

        // Event to notify when hide tray rate button settings change
        public static event System.EventHandler? HideTrayRateButtonSettingsChanged;
        internal static void RaiseHideTrayRateButtonSettingsChanged() => HideTrayRateButtonSettingsChanged?.Invoke(null, System.EventArgs.Empty);

        // Event to notify when window maximized state changes
        public static event System.EventHandler<bool>? WindowMaximizedStateChanged;
        internal static void RaiseWindowMaximizedStateChanged(bool isMaximized)
        {
            System.Diagnostics.Debug.WriteLine($"[LabPage] RaiseWindowMaximizedStateChanged: isMaximized={isMaximized}");
            WindowMaximizedStateChanged?.Invoke(null, isMaximized);
        }

        // Event to request refresh of monitor state
        public static event System.EventHandler? RefreshMonitorStateRequested;
        internal static void RequestRefreshMonitorState()
        {
            System.Diagnostics.Debug.WriteLine("[LabPage] RequestRefreshMonitorState called");
            RefreshMonitorStateRequested?.Invoke(null, System.EventArgs.Empty);
        }
    }
}
