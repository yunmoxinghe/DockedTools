using System;
using System.IO;
using System.Linq;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.Localization;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using DockedTools.Features.MainWindow.Entry;
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
            _智能标题.Setup(this, PageScrollViewer, PageTitleBlock);
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();

            // 离开画面就撤掉实时文本回调：它是一条抱住本页实例的委托，
            // 不收的话这页会被 scope 一直钉着 GC 不走。
            TopAppBarService.ClearLiveTextCallback();
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

            // 顶栏输入框测试卡：填候选值，并把默认形态下发一次
            InitializeSearchBoxCard();

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

        // 实验室页面那枚"设置"按钮的固定 Id（演示用）
        private const string LabSettingsButtonId = "__lab_settings";

        private void OnSetRightButtonClick(object sender, RoutedEventArgs e)
        {
            // 顶栏按钮现在是【数据】而不是 UIElement：给一个 TopBarButton 就完事，
            // 外观（40×40、圆角、悬停/按下色、tooltip）全部由 AppTopBar 负责。
            TopAppBarService.SetRightIconButton(
                id: LabSettingsButtonId,
                glyph: "Setting",
                tooltip: LocalizationHelper.GetString("LabPage_TopBarRightSetButton"),
                onClick: () =>
                {
                    TopAppBarService.SetRightButtons(null);
                    RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightButtonCleared");
                });
            RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightButtonSet");
        }

        private void OnClearRightButtonClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.SetRightButtons(null);
            RightButtonStatus.Text = LocalizationHelper.GetString("LabPage_RightContentCleared");
        }

        private void OnSetCenterTitleClick(object sender, RoutedEventArgs e)
        {
            var text = CenterTitleInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) text = LocalizationHelper.GetString("LabPage_DefaultTitle");
            TopAppBarService.SetTitle(text);
        }

        private void OnClearCenterClick(object sender, RoutedEventArgs e)
        {
            TopAppBarService.ClearCenter();
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

        // ── 顶栏输入框测试卡 ────────────────────────────────────────
        //
        // 这张卡的存在意义：把「居中输入框」那两个新能力掰成一个能当场看见的东西 ——
        //   ① 页面能不能实时拿到正在输入的内容（Search.LiveText）；
        //   ② 右缘那枚确认按钮能不能换样子（TopBarAcceptIcon 联合类型）。
        // 两者都在 <see cref="TopAppBarService"/> 上有对应入口，这里只是个遥控器。

        // 组合框的候选值，同时也是"要哪种形态"的内部代号（字符串 code-behind 直写，
        // 不走本地化：这是调试面板，读的人就是写的人）
        private const string AcceptKindFind = "find";
        private const string AcceptKindAccept = "accept";
        private const string AcceptKindSend = "send";
        private const string AcceptKindStatic = "static";
        private const string AcceptKindNone = "none";

        // 最近一次能在卡片上打出来的状态（顺带作为"回调真的打过来了"的证据）
        private int _liveTicks;

        private void InitializeSearchBoxCard()
        {
            AcceptIconCombo.Items.Clear();
            AcceptIconCombo.Items.Add(new ComboBoxItem { Content = "动画放大镜（默认）", Tag = AcceptKindFind });
            AcceptIconCombo.Items.Add(new ComboBoxItem { Content = "动画对勾", Tag = AcceptKindAccept });
            AcceptIconCombo.Items.Add(new ComboBoxItem { Content = "动画右箭头", Tag = AcceptKindSend });
            AcceptIconCombo.Items.Add(new ComboBoxItem { Content = "静态字形（用左边输入框里的码点）", Tag = AcceptKindStatic });
            AcceptIconCombo.Items.Add(new ComboBoxItem { Content = "不要确认按钮", Tag = AcceptKindNone });
            AcceptIconCombo.SelectedIndex = 0;

            LiveTextToggle.IsOn = true;
            AcceptGlyphInput.Text = "Accept";

            // 显式下发一次：不能把"顶栏变成搜索框"寄托在 ToggleSwitch.IsOn 的 setter
            // 会顺带触发 Toggled 上 —— 那属于副作用，一旦将来初值就是 true 便不再触发，
            // 顶栏会停在纯标题形态，表现就是"输入框不出现 / 右缘按钮不出现"。
            ApplyTopBarSearchBox();
        }

        /// <summary>按卡片上的选择重发一份居中位快照。</summary>
        private void ApplyTopBarSearchBox()
        {
            var kind = (AcceptIconCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? AcceptKindFind;

            // 联合类型的价值就在这一小段：每换一种形态只是多一个分支，
            // 不用加 bool、不用动签名；新形态同理。
            var accept = kind switch
            {
                AcceptKindAccept => TopBarAcceptIcon.Accept,
                AcceptKindSend => TopBarAcceptIcon.Send,
                AcceptKindNone => TopBarAcceptIcon.None,
                AcceptKindStatic => TopBarAcceptIcon.OfGlyph(
                    string.IsNullOrWhiteSpace(AcceptGlyphInput.Text)
                        ? "Accept"
                        : AcceptGlyphInput.Text.Trim()),
                _ => TopBarAcceptIcon.Find,
            };

            // 必须在下发居中位【之前】认领：智能标题盯的是页面大标题 Text 的变化，
            // 那串文本走 x:Uid，落地时间晚于本方法 —— 不认领的话，我们刚设好的搜索框
            // 会在随后那一拍被它覆盖回纯标题（现象就是"设了没生效"/"输入框没文本"）。
            TopAppBarService.SetSmartTitleSuppressed(true);

            // 【为什么必须给非空文本】TopBarCenter.Search.Text 同时是"标题文字"和"输入框
            // 当前内容"。给空串的话居中位宽度为 0 —— 不但看不见字，连"点一下展开搜索"
            // 都点不到（命中区域就是那个 TextBlock），于是输入框永远出不来，右缘那枚确认
            // 按钮也跟着不出来。想让输入框空着，是靠 Placeholder 提示，不是靠空标题。
            var text = string.IsNullOrWhiteSpace(PageTitleBlock.Text)
                ? LocalizationHelper.GetString("LabPage_DefaultTitle")
                : PageTitleBlock.Text;

            if (LiveTextToggle.IsOn)
            {
                TopAppBarService.SetLiveSearchTitle(
                    text: text,
                    onTextChanged: OnTopBarTextChanged,
                    accept: accept,
                    placeholder: "打字试试，页面这边会实时收到");
            }
            else
            {
                // 关掉实时回传 = 退化成普通可搜索标题（那条老路径应当毫发无伤）
                TopAppBarService.ClearLiveTextCallback();
                TopAppBarService.SetSearchableTitle(text, "打字不会被页面收到（除非回车）");
            }

            _liveTicks = 0;
            SearchBoxStatusText.Text = kind == AcceptKindNone
                ? "已切换到该形态；输入框只能用回车提交。"
                : "已切换到该形态；点一下顶栏中间开始打字。";
        }

        // 实时文本的落点：每敲一个字都进这里一次。
        // 注意【不要】在这里把文本回写进快照 —— 那会让每敲一个字触发一次全量重渲染
        //（见 TopBarEvent.TextChanged 的注释）。只是读，就够了。
        private void OnTopBarTextChanged(string text)
        {
            _liveTicks++;
            SearchBoxStatusText.Text = string.IsNullOrEmpty(text)
                ? $"收到第 {_liveTicks} 次通知（当前为空）"
                : $"收到第 {_liveTicks} 次通知：{text}";
        }

        private void OnAcceptIconChanged(object sender, SelectionChangedEventArgs e) => ApplyTopBarSearchBox();

        private void OnLiveTextToggled(object sender, RoutedEventArgs e) => ApplyTopBarSearchBox();

        private void OnApplySearchBoxClick(object sender, RoutedEventArgs e) => ApplyTopBarSearchBox();

        private void OnCloseSearchBoxClick(object sender, RoutedEventArgs e)
        {
            // 回调跟着页面 scope 走，但 explicit 收掉一次更保险：scope 是长期存活的，
            // 留着一个抱着页面的委托，等于把本页钉在内存里。
            TopAppBarService.ClearLiveTextCallback();
            // 交还所有权：关掉测试之后，智能标题应当重新掌管页面大标题
            TopAppBarService.SetSmartTitleSuppressed(false);
            TopAppBarService.ClearCenter();
            SearchBoxStatusText.Text = "已关闭输入框测试。";
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
        /// Toast 通知测试：发送单条通知
        /// </summary>
        private void OnSendToastClick(object sender, RoutedEventArgs e)
        {
            SendTestToast($"单条 · {DateTime.Now:HH:mm:ss}");
        }

        /// <summary>
        /// Toast 通知测试：连发 3 条，验证排队/覆盖行为
        /// </summary>
        private void OnSendToastBurstClick(object sender, RoutedEventArgs e)
        {
            for (int i = 1; i <= 3; i++)
            {
                SendTestToast($"连发 {i}/3 · {DateTime.Now:HH:mm:ss}");
            }
        }

        /// <summary>
        /// 走 DebugNotificationHelper（AppNotificationManager）发一条系统通知，并把结果写回卡片
        /// </summary>
        /// <remarks>
        /// Show() 没有返回值，所以这里只能确认「调用成功」；
        /// 真正弹没弹出来取决于系统通知开关 / 专注助手，卡片文字里已提示。
        /// </remarks>
        private void SendTestToast(string message)
        {
            try
            {
                DebugNotificationHelper.Initialize();
                DebugNotificationHelper.SendNotification("🧪 Toast 测试", message);
                ToastStatusText.Text = $"✅ 已调用发送：{message}\n没弹出来就是被系统通知开关或专注助手拦了。";
            }
            catch (Exception ex)
            {
                ToastStatusText.Text = $"❌ 发送异常：{ex.Message}";
            }
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
