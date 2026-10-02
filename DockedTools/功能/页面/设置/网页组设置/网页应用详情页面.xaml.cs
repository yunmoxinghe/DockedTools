using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.Localization;
using DockedTools.Features.MainWindow.Entry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace DockedTools.Features.Pages.Settings.WebSettings
{
    public sealed partial class WebAppDetailPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        private readonly 智能标题 _智能标题 = new();

        /// <summary>
        /// 设置左侧按钮图标类型的可见性
        /// ✅ AOT 友好：直接操作控件 Visibility，无需 x:Bind
        /// </summary>
        private void SetLeftButtonIconVisibility(bool isStatic)
        {
            if (LeftButtonStaticIconCard != null && LeftButtonAnimatedIconCard != null)
            {
                LeftButtonStaticIconCard.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
                LeftButtonAnimatedIconCard.Visibility = isStatic ? Visibility.Collapsed : Visibility.Visible;
                
                LogDebug($"[SetLeftButtonIconVisibility] Static={isStatic}, StaticCard.Visibility={LeftButtonStaticIconCard.Visibility}, AnimatedCard.Visibility={LeftButtonAnimatedIconCard.Visibility}");
            }
        }

        /// <summary>
        /// 设置右侧按钮图标类型的可见性
        /// ✅ AOT 友好：直接操作控件 Visibility，无需 x:Bind
        /// </summary>
        private void SetRightButtonIconVisibility(bool isStatic)
        {
            if (RightButtonStaticIconCard != null && RightButtonAnimatedIconCard != null)
            {
                RightButtonStaticIconCard.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
                RightButtonAnimatedIconCard.Visibility = isStatic ? Visibility.Collapsed : Visibility.Visible;
                LogDebug($"[SetRightButtonIconVisibility] Static={isStatic}, StaticCard.Visibility={RightButtonStaticIconCard.Visibility}, AnimatedCard.Visibility={RightButtonAnimatedIconCard.Visibility}");
            }
        }
        // ✅ AOT 修复：动态图标类型的 Tag 映射数组（对应 XAML 中的 ComboBoxItem 顺序）
        private static readonly string[] AnimatedIconTypes = 
        {
            "AnimatedAcceptVisualSource",                    // 索引 0
            "AnimatedBackVisualSource",                      // 索引 1
            "AnimatedChevronDownSmallVisualSource",          // 索引 2 (默认)
            "AnimatedChevronRightDownSmallVisualSource",     // 索引 3
            "AnimatedChevronUpDownSmallVisualSource",        // 索引 4
            "AnimatedFindVisualSource",                      // 索引 5
            "AnimatedGlobalNavigationButtonVisualSource",    // 索引 6
            "AnimatedSettingsVisualSource"                   // 索引 7
        };

        private static readonly HttpClient HttpClient = CreateHttpClient();

        /// <summary>
        /// 条件编译的调试日志方法
        /// 仅在 DEBUG 模式下执行，Release 版本完全移除，避免字符串分配开销
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[WebAppDetailPage] {message}");
        }

        // 图标 Code 存储（十六进制，如 "E707"）
        private string? _leftButtonIconCode;
        private string? _rightButtonIconCode;

        private string? _appId;
        private string _originalName = string.Empty;
        private string _originalUrl = string.Empty;
        private byte[]? _originalIconBytes;
        private byte[]? _currentIconBytes;
        private KeyboardMappingButtonConfig? _originalLeftButtonConfig;
        private KeyboardMappingButtonConfig? _originalRightButtonConfig;
        private bool _hasChanges;

        // 保存按钮：新顶栏收的是按钮数据，所以这里只记 Id 与"当前是否可用"，
        // 真正的禁用外观与点击拦截由 AppTopBar 负责。
        private const string SaveButtonId = "__webapp_detail_save";
        private bool _saveButtonEnabled;

        private void SetSaveButtonEnabled(bool enabled)
        {
            // 不做"值没变就不下发"的短路：首次进入时要显式把按钮压成禁用，
            // 而快照默认给出的按钮是 Enabled —— 短路会让这次压制丢失。
            // 重复下发是安全的：TopBarSnapshot 是值语义，内容相同不会触发重渲染。
            _saveButtonEnabled = enabled;
            TopAppBarService.SetButtonEnabled(SaveButtonId, enabled);
        }

        public WebAppDetailPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, PageScrollViewer, PageTitleBlock);

            // ⭐ 增强调试：详细记录导航参数
            LogDebug($"OnNavigatedTo 被调用");
            LogDebug($"e.Parameter 类型: {e.Parameter?.GetType().FullName ?? "null"}");
            LogDebug($"e.Parameter 值: {e.Parameter}");

            // 获取传递的应用 ID
            if (e.Parameter is string appId)
            {
                _appId = appId;
                LogDebug($"✅ 成功获取 appId: {_appId}");
            }
            else
            {
                LogDebug($"❌ 无法转换 Parameter 为 string，实际类型: {e.Parameter?.GetType().FullName ?? "null"}");
                
                // ⭐ 尝试其他可能的类型转换
                if (e.Parameter != null)
                {
                    _appId = e.Parameter.ToString();
                    LogDebug($"⚠️ 使用 ToString() 转换: {_appId}");
                }
            }

            // 添加保存按钮到标题栏（使用 C# Unicode 转义格式）
            // 注：新顶栏返回按钮数据而不是 Button 实例，可用/禁用改由 SetButtonEnabled 下发状态。
            TopAppBarService.SetRightIconButton(
                id: SaveButtonId,
                glyph: "\uE74E",
                tooltip: LocalizationHelper.GetString("WebAppDetailPage_SaveButton"),
                // OnSaveClick 是 XAML 事件签名，转接成顶栏要的 Action
                onClick: () => OnSaveClick(null!, null!));
            SetSaveButtonEnabled(false);
            
            LogDebug($"OnNavigatedTo 完成，_appId = {_appId ?? "null"}");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        /// <summary>
        /// 左侧按钮图标类型变更事件（XAML 绑定）
        /// ✅ AOT 友好：使用 SelectedIndex 而不是 Tag 转换
        /// </summary>
        private void OnLeftButtonIconTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedIndex >= 0)
            {
                // 0 = Static, 1 = Animated
                bool isStatic = comboBox.SelectedIndex == 0;
                SetLeftButtonIconVisibility(isStatic);
                CheckForChanges();
            }
        }

        /// <summary>
        /// 右侧按钮图标类型变更事件（XAML 绑定）
        /// ✅ AOT 友好：使用 SelectedIndex 而不是 Tag 转换
        /// </summary>
        private void OnRightButtonIconTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedIndex >= 0)
            {
                // 0 = Static, 1 = Animated
                bool isStatic = comboBox.SelectedIndex == 0;
                SetRightButtonIconVisibility(isStatic);
                CheckForChanges();
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await LoadAppDataAsync();
            UpdateVisualState();
        }
        
        /// <summary>
        /// AOT 调试弹窗（仅 Debug 模式）
        /// </summary>
        private async System.Threading.Tasks.Task ShowDebugDialogAsync(string title, string message)
        {
            #if DEBUG
            try
            {
                if (this.XamlRoot == null) return;
                
                var dialog = new DockedTools.Features.UnifiedCalls.InAppDialog.UnifiedInAppDialog();
                dialog.Configure(
                    $"🔍 {title}",
                    message,
                    closeButtonText: "确定"
                );
                await DockedTools.Features.UnifiedCalls.InAppDialog.InAppDialogService.ShowAsync(dialog, this);
            }
            catch
            {
                // 忽略弹窗错误
            }
            #endif
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private async Task LoadAppDataAsync()
        {
            LogDebug($"LoadAppDataAsync 开始，_appId = {_appId ?? "null"}");
            
            if (string.IsNullOrEmpty(_appId))
            {
                LogDebug("❌ _appId 为空，跳过加载");
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_AppNotFound"), InfoBarSeverity.Error);
                return;
            }

            try
            {
                LogDebug("开始调用 WebAppShortcutStore.LoadAsync()");
                var shortcuts = await WebAppShortcutStore.LoadAsync();
                LogDebug($"✅ 加载了 {shortcuts.Count} 个快捷方式");
                
                // ⭐ 详细记录所有快捷方式的 ID
                foreach (var s in shortcuts)
                {
                    LogDebug($"  - 快捷方式: Id={s.Id}, Name={s.Name}");
                }
                
                LogDebug($"开始查找 _appId={_appId}");
                var app = shortcuts.FirstOrDefault(s => s.Id == _appId);
                
                if (app == null)
                {
                    LogDebug($"❌ 未找到匹配的应用，_appId={_appId}");
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_AppNotFound"), InfoBarSeverity.Error);
                    return;
                }
                
                LogDebug($"✅ 找到应用: Name={app.Name}, Url={app.Url}");

                // 保存原始数据
                _originalName = app.Name;
                _originalUrl = app.Url;
                _originalIconBytes = app.IconBytes;
                _currentIconBytes = app.IconBytes;
                _originalLeftButtonConfig = app.LeftButtonConfig;
                _originalRightButtonConfig = app.RightButtonConfig;
                
                LogDebug($"原始数据已保存: Name={_originalName}, Url={_originalUrl}");

                // 填充界面
                LogDebug("开始填充界面...");
                NameTextBox.Text = app.Name;
                UrlTextBox.Text = app.Url;
                LogDebug($"✅ 文本框已填充");
                
                if (app.IconBytes != null && app.IconBytes.Length > 0)
                {
                    LogDebug($"开始加载图标 ({app.IconBytes.Length} 字节)");
                    await ShowIconAsync(app.IconBytes);
                    LogDebug("✅ 图标已加载");
                }
                else
                {
                    LogDebug("⚠️ 无图标数据");
                }
                
                // 填充左侧按钮配置
                LogDebug("开始加载左侧按钮配置...");
                LoadLeftButtonConfig(app.LeftButton);
                LogDebug("✅ 左侧按钮配置已加载");
                
                // 填充右侧按钮配置
                LogDebug("开始加载右侧按钮配置...");
                LoadRightButtonConfig(app.RightButton);
                LogDebug("✅ 右侧按钮配置已加载");
                
                LogDebug("LoadAppDataAsync 完成");
            }
            catch (Exception ex)
            {
                LogDebug($"❌ LoadAppDataAsync 异常: {ex.GetType().Name}");
                LogDebug($"   消息: {ex.Message}");
                LogDebug($"   堆栈: {ex.StackTrace}");
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_LoadFailed"), InfoBarSeverity.Error);
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

        private void OnFieldChanged(object sender, TextChangedEventArgs e)
        {
            CheckForChanges();
        }

        private void OnNameCardClick(object sender, RoutedEventArgs e)
        {
            NameTextBox?.Focus(FocusState.Programmatic);
        }

        private void OnUrlCardClick(object sender, RoutedEventArgs e)
        {
            UrlTextBox?.Focus(FocusState.Programmatic);
        }

        private void OnIconUrlChanged(object sender, TextChangedEventArgs e)
        {
            string url = IconUrlTextBox.Text?.Trim() ?? string.Empty;
            LoadIconFromUrlButton.IsEnabled = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private void CheckForChanges()
        {
            string currentName = NameTextBox.Text?.Trim() ?? string.Empty;
            string currentUrl = UrlTextBox.Text?.Trim() ?? string.Empty;

            bool nameChanged = currentName != _originalName;
            bool urlChanged = currentUrl != _originalUrl;
            bool iconChanged = !ByteArrayEquals(_currentIconBytes, _originalIconBytes);
            bool leftButtonChanged = IsLeftButtonConfigChanged();
            bool rightButtonChanged = IsRightButtonConfigChanged();

            _hasChanges = nameChanged || urlChanged || iconChanged || leftButtonChanged || rightButtonChanged;

            SetSaveButtonEnabled(_hasChanges
                && !string.IsNullOrWhiteSpace(currentName)
                && !string.IsNullOrWhiteSpace(currentUrl));
        }

        private async void OnChooseLocalIconClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".ico");

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_CannotOpenPicker"), InfoBarSeverity.Error);
                    return;
                }

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                StorageFile? file = await picker.PickSingleFileAsync();
                
                if (file == null)
                {
                    return;
                }

                IBuffer buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = buffer.ToArray();
                
                if (bytes.Length == 0)
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_ImageFileEmpty"), InfoBarSeverity.Warning);
                    return;
                }

                if (bytes.Length > 4 * 1024 * 1024)
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_ImageFileTooLarge"), InfoBarSeverity.Warning);
                    return;
                }

                _currentIconBytes = bytes;
                await ShowIconAsync(bytes);
                CheckForChanges();
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_IconUpdated"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to choose icon: {ex}");
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_SelectIconFailed"), InfoBarSeverity.Error);
            }
        }

        private async void OnLoadIconFromUrlClick(object sender, RoutedEventArgs e)
        {
            string url = IconUrlTextBox.Text?.Trim() ?? string.Empty;
            
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_InvalidUrl"), InfoBarSeverity.Warning);
                return;
            }

            try
            {
                LoadIconFromUrlButton.IsEnabled = false;
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_DownloadingIcon"), InfoBarSeverity.Informational);

                using var response = await HttpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                string? contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_UrlNotValidImage"), InfoBarSeverity.Warning);
                        return;
                    }
                    if (contentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_SvgNotSupported"), InfoBarSeverity.Warning);
                        return;
                    }
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                
                if (bytes.Length == 0)
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_ImageEmpty"), InfoBarSeverity.Warning);
                    return;
                }

                if (bytes.Length > 4 * 1024 * 1024)
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_ImageTooLarge"), InfoBarSeverity.Warning);
                    return;
                }

                // 验证是否可以解码
                if (!await CanDecodeBitmapAsync(bytes))
                {
                    ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_CannotDecodeImage"), InfoBarSeverity.Warning);
                    return;
                }

                _currentIconBytes = bytes;
                await ShowIconAsync(bytes);
                CheckForChanges();
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_IconLoadSuccess"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to load icon from URL: {ex}");
                ShowStatus(string.Format(LocalizationHelper.GetString("WebAppDetailPage_LoadFailedWithReason"), ex.Message), InfoBarSeverity.Error);
            }
            finally
            {
                LoadIconFromUrlButton.IsEnabled = true;
            }
        }

        private void OnResetIconClick(object sender, RoutedEventArgs e)
        {
            LogDebug($"OnResetIconClick: _originalIconBytes={((_originalIconBytes?.Length ?? 0) > 0 ? "有" : "无")}");
            
            _currentIconBytes = null;
            IconPreviewImage.Source = null;
            IconPreviewImage.Visibility = Visibility.Collapsed;
            IconPreviewFallback.Visibility = Visibility.Visible;
            CheckForChanges();
            
            LogDebug($"OnResetIconClick: _hasChanges={_hasChanges}, SaveButton.IsEnabled={_saveButtonEnabled}");
            ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_IconReset"), InfoBarSeverity.Success);
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            LogDebug("OnSaveClick started");
            
            if (string.IsNullOrEmpty(_appId))
            {
                LogDebug("OnSaveClick: _appId is empty");
                return;
            }

            string name = NameTextBox.Text?.Trim() ?? string.Empty;
            string url = UrlTextBox.Text?.Trim() ?? string.Empty;

            LogDebug($"OnSaveClick: name='{name}', url='{url}', iconBytes={((_currentIconBytes?.Length ?? 0) > 0 ? "有" : "无")}");

            if (string.IsNullOrWhiteSpace(name))
            {
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_PleaseEnterName"), InfoBarSeverity.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_PleaseEnterUrl"), InfoBarSeverity.Warning);
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_InvalidUrlFormat"), InfoBarSeverity.Warning);
                return;
            }

            try
            {
                LogDebug("OnSaveClick: Starting save process");

                SetSaveButtonEnabled(false);

                // ⭐ 检测变化类型（用于细粒度更新）
                var updateType = WebAppUpdateType.None;
                if (name != _originalName)
                {
                    updateType |= WebAppUpdateType.Name;
                    LogDebug("检测到名称变化");
                }
                if (uri.AbsoluteUri != _originalUrl)
                {
                    updateType |= WebAppUpdateType.Url;
                    LogDebug("检测到 URL 变化");
                }
                if (!ByteArrayEquals(_currentIconBytes, _originalIconBytes))
                {
                    updateType |= WebAppUpdateType.Icon;
                    LogDebug("检测到图标变化");
                }
                
                // 检测按钮配置变化
                var currentLeftConfig = GetCurrentLeftButtonConfig();
                var currentRightConfig = GetCurrentRightButtonConfig();
                if (!IsButtonConfigEqual(currentLeftConfig, _originalLeftButtonConfig) ||
                    !IsButtonConfigEqual(currentRightConfig, _originalRightButtonConfig))
                {
                    updateType |= WebAppUpdateType.ButtonConfig;
                    LogDebug("检测到按钮配置变化");
                }

                // 读取现有数据
                var shortcuts = await WebAppShortcutStore.LoadAsync();
                var updatedShortcuts = shortcuts.ToList();

                LogDebug($"OnSaveClick: Loaded {updatedShortcuts.Count} shortcuts");

                // 查找并更新
                var index = updatedShortcuts.FindIndex(s => s.Id == _appId);
                if (index >= 0)
                {
                    LogDebug($"OnSaveClick: Found app at index {index}, updating");
                    
                    // 获取当前按钮配置
                    var leftButtonConfig = GetCurrentLeftButtonConfig();
                    var rightButtonConfig = GetCurrentRightButtonConfig();
                    
                    updatedShortcuts[index] = new WebAppShortcut(
                        _appId, 
                        name, 
                        uri.AbsoluteUri, 
                        _currentIconBytes,
                        leftButtonConfig,
                        rightButtonConfig);
                }
                else
                {
                    LogDebug("OnSaveClick: App not found in list!");
                }

                // 保存
                LogDebug("OnSaveClick: Saving shortcuts");
                await WebAppShortcutStore.SaveAsync(updatedShortcuts);

                // ⭐ 使用新的更新服务（细粒度通知）
                if (updateType != WebAppUpdateType.None)
                {
                    LogDebug($"通知更新: {_appId}, 类型: {updateType}");
                    WebAppUpdateService.NotifyUpdate(_appId, updateType);
                }

                // 更新原始数据
                _originalName = name;
                _originalUrl = uri.AbsoluteUri;
                _originalIconBytes = _currentIconBytes;
                _originalLeftButtonConfig = GetCurrentLeftButtonConfig();
                _originalRightButtonConfig = GetCurrentRightButtonConfig();
                _hasChanges = false;

                LogDebug("OnSaveClick: Save successful");
                ShowStatus(LocalizationHelper.GetString("WebAppDetailPage_SaveSuccess"), InfoBarSeverity.Success);

                // 延迟返回上一页
                await Task.Delay(1000);
                LogDebug($"OnSaveClick: CanGoBack={Frame.CanGoBack}");
                if (Frame.CanGoBack)
                {
                    LogDebug("OnSaveClick: Going back");
                    Frame.GoBack();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to save: {ex}");
                ShowStatus(string.Format(LocalizationHelper.GetString("WebAppDetailPage_SaveFailedWithReason"), ex.Message), InfoBarSeverity.Error);
            }
            finally
            {
                SetSaveButtonEnabled(_hasChanges);
                LogDebug("OnSaveClick completed");
            }
        }

        private async Task ShowIconAsync(byte[] iconBytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(iconBytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);

                IconPreviewImage.Source = bitmap;
                IconPreviewImage.Visibility = Visibility.Visible;
                IconPreviewFallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to show icon: {ex}");
                IconPreviewImage.Visibility = Visibility.Collapsed;
                IconPreviewFallback.Visibility = Visibility.Visible;
            }
        }

        private static async Task<bool> CanDecodeBitmapAsync(byte[] bytes)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                _ = await BitmapDecoder.CreateAsync(stream);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ByteArrayEquals(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            return a.SequenceEqual(b);
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            return client;
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();
        
        #region 键盘映射按钮配置

        // 快捷键录制状态
        private VirtualKey _leftButtonRecordedKey = VirtualKey.None;
        private bool _leftButtonRecordedCtrl;
        private bool _leftButtonRecordedShift;
        private bool _leftButtonRecordedAlt;
        
        private VirtualKey _rightButtonRecordedKey = VirtualKey.None;
        private bool _rightButtonRecordedCtrl;
        private bool _rightButtonRecordedShift;
        private bool _rightButtonRecordedAlt;

        private void LoadLeftButtonConfig(KeyboardMappingButtonConfig config)
        {
            LogDebug($"[LoadLeftButtonConfig] config.StaticIconGlyph='{config.StaticIconGlyph}' (长度={config.StaticIconGlyph?.Length ?? 0})");
            LogDebug($"[LoadLeftButtonConfig] config.IconType='{config.IconType}'");
            
            LeftButtonEnabledToggle.Toggled -= OnLeftButtonEnabledToggled;
            LeftButtonEnabledToggle.IsOn = config.IsEnabled;
            LeftButtonEnabledToggle.Toggled += OnLeftButtonEnabledToggled;
            
            // ✅ AOT 修复：直接使用 SelectedIndex 而不是 Tag
            bool isStatic = string.Equals(config.IconType, "Static", StringComparison.OrdinalIgnoreCase);
            LeftButtonIconTypeComboBox.SelectedIndex = isStatic ? 0 : 1;
            
            // ✅ 使用 setter 方法更新可见性
            SetLeftButtonIconVisibility(isStatic);
            LogDebug($"[LoadLeftButtonConfig] 已调用 SetLeftButtonIconVisibility: isStatic={isStatic}");
            
            // 加载静态图标：将 Glyph (Unicode) 转换为 Code (十六进制)
            _leftButtonIconCode = GlyphToCode(config.StaticIconGlyph);
            UpdateLeftButtonIconPreview();
            
            // 加载动态图标类型
            // ✅ AOT 修复：从 Tag 字符串查找对应的索引
            int animatedIconIndex = Array.IndexOf(AnimatedIconTypes, config.AnimatedIconType);
            if (animatedIconIndex < 0) animatedIconIndex = 2; // 默认为 AnimatedChevronDownSmallVisualSource
            LeftButtonAnimatedIconTypeComboBox.SelectedIndex = animatedIconIndex;
            
            // 加载工具提示
            LeftButtonTooltipTextBox.Text = config.Tooltip;
            
            // 加载快捷键
            _leftButtonRecordedKey = config.Key;
            _leftButtonRecordedCtrl = config.Ctrl;
            _leftButtonRecordedShift = config.Shift;
            _leftButtonRecordedAlt = config.Alt;
            
            UpdateLeftButtonHotkeyPreview();
            UpdateLeftButtonExpanderItemsEnabled();
        }

        private void LoadRightButtonConfig(KeyboardMappingButtonConfig config)
        {
            LogDebug($"[LoadRightButtonConfig] config.IconType='{config.IconType}'");
            
            RightButtonEnabledToggle.Toggled -= OnRightButtonEnabledToggled;
            RightButtonEnabledToggle.IsOn = config.IsEnabled;
            RightButtonEnabledToggle.Toggled += OnRightButtonEnabledToggled;
            
            // ✅ AOT 修复：直接使用 SelectedIndex 而不是 Tag
            bool isStatic = string.Equals(config.IconType, "Static", StringComparison.OrdinalIgnoreCase);
            RightButtonIconTypeComboBox.SelectedIndex = isStatic ? 0 : 1;
            
            // ✅ 使用 setter 方法更新可见性
            SetRightButtonIconVisibility(isStatic);
            LogDebug($"[LoadRightButtonConfig] 已调用 SetRightButtonIconVisibility: isStatic={isStatic}");
            
            // 加载静态图标：将 Glyph (Unicode) 转换为 Code (十六进制)
            _rightButtonIconCode = GlyphToCode(config.StaticIconGlyph);
            UpdateRightButtonIconPreview();
            
            // 加载动态图标类型
            // ✅ AOT 修复：从 Tag 字符串查找对应的索引
            int animatedIconIndex = Array.IndexOf(AnimatedIconTypes, config.AnimatedIconType);
            if (animatedIconIndex < 0) animatedIconIndex = 2; // 默认为 AnimatedChevronDownSmallVisualSource
            RightButtonAnimatedIconTypeComboBox.SelectedIndex = animatedIconIndex;
            
            // 加载工具提示
            RightButtonTooltipTextBox.Text = config.Tooltip;
            
            // 加载快捷键
            _rightButtonRecordedKey = config.Key;
            _rightButtonRecordedCtrl = config.Ctrl;
            _rightButtonRecordedShift = config.Shift;
            _rightButtonRecordedAlt = config.Alt;
            
            UpdateRightButtonHotkeyPreview();
            UpdateRightButtonExpanderItemsEnabled();
        }

        private void OnLeftButtonEnabledToggled(object sender, RoutedEventArgs e)
        {
            UpdateLeftButtonExpanderItemsEnabled();
            CheckForChanges();
        }

        private void OnRightButtonEnabledToggled(object sender, RoutedEventArgs e)
        {
            UpdateRightButtonExpanderItemsEnabled();
            CheckForChanges();
        }

        /// <summary>
        /// 左侧按钮 ComboBox 选择变更事件处理
        /// ✅ 使用独立方法名避免 AOT 重载解析问题
        /// </summary>
        private async void OnLeftButtonComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ⭐ AOT 调试弹窗
            await ShowDebugDialogAsync("左侧图标类型", $"事件触发！sender={sender?.GetType().Name}");
            
            // 🔥 关键修复：确保在 UI 线程上执行
            if (DispatcherQueue != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(sender, LeftButtonIconTypeComboBox))
                    {
                        UpdateLeftButtonIconVisibility();
                    }
                });
            }
            
            CheckForChanges();
        }

        /// <summary>
        /// 左侧按钮 TextBox 文本变更事件处理
        /// ✅ 使用独立方法名避免 AOT 重载解析问题
        /// </summary>
        private void OnLeftButtonTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            LogDebug($"[OnLeftButtonTextBoxTextChanged] sender={sender?.GetType().Name}");
            CheckForChanges();
        }

        /// <summary>
        /// 右侧按钮 ComboBox 选择变更事件处理
        /// ✅ 使用独立方法名避免 AOT 重载解析问题
        /// </summary>
        private async void OnRightButtonComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ShowDebugDialogAsync("右侧图标类型", $"事件触发！sender={sender?.GetType().Name}");
            
            if (DispatcherQueue != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(sender, RightButtonIconTypeComboBox))
                    {
                        UpdateRightButtonIconVisibility();
                    }
                });
            }
            
            CheckForChanges();
        }

        /// <summary>
        /// 右侧按钮 TextBox 文本变更事件处理
        /// ✅ 使用独立方法名避免 AOT 重载解析问题
        /// </summary>
        private void OnRightButtonTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            LogDebug($"[OnRightButtonTextBoxTextChanged] sender={sender?.GetType().Name}");
            CheckForChanges();
        }

        private void UpdateLeftButtonIconVisibility()
        {
            // ✅ AOT 兼容：增加 null 检查和默认值处理
            var iconType = GetSelectedComboBoxTag(LeftButtonIconTypeComboBox);
            bool isStatic = string.Equals(iconType, "Static", StringComparison.OrdinalIgnoreCase);
            
            // 如果 iconType 为 null 或空字符串，默认显示静态图标
            if (string.IsNullOrEmpty(iconType))
            {
                isStatic = true;
                LogDebug("[UpdateLeftButtonIconVisibility] iconType 为空，默认显示静态图标");
            }
            
            LogDebug($"[UpdateLeftButtonIconVisibility] iconType='{iconType}', isStatic={isStatic}");
            
            // 🔥 直接设置 Visibility 属性（最 AOT 友好）
            SetLeftButtonIconVisibility(isStatic);
        }

        private void UpdateRightButtonIconVisibility()
        {
            // ✅ AOT 兼容：增加 null 检查和默认值处理
            var iconType = GetSelectedComboBoxTag(RightButtonIconTypeComboBox);
            bool isStatic = string.Equals(iconType, "Static", StringComparison.OrdinalIgnoreCase);
            
            // 如果 iconType 为 null 或空字符串，默认显示静态图标
            if (string.IsNullOrEmpty(iconType))
            {
                isStatic = true;
                LogDebug("[UpdateRightButtonIconVisibility] iconType 为空，默认显示静态图标");
            }
            
            LogDebug($"[UpdateRightButtonIconVisibility] iconType='{iconType}', isStatic={isStatic}");
            
            // 🔥 直接设置 Visibility 属性（最 AOT 友好）
            SetRightButtonIconVisibility(isStatic);
        }

        private async void OnLeftButtonHotkeyButtonClick(object sender, RoutedEventArgs e)
        {
            var result = await HotkeyRecorderHelper.ShowRecorderAsync(
                this,
                _leftButtonRecordedKey,
                _leftButtonRecordedCtrl,
                _leftButtonRecordedShift,
                _leftButtonRecordedAlt);

            if (result != null)
            {
                _leftButtonRecordedKey = result.Key;
                _leftButtonRecordedCtrl = result.Ctrl;
                _leftButtonRecordedShift = result.Shift;
                _leftButtonRecordedAlt = result.Alt;
                
                UpdateLeftButtonHotkeyPreview();
                CheckForChanges();
            }
        }

        private async void OnLeftButtonPickIconClick(object sender, RoutedEventArgs e)
        {
            var result = await IconPickerHelper.ShowPickerAsync(this, _leftButtonIconCode);
            
            if (result != null)
            {
                _leftButtonIconCode = result; // 存储 Code（十六进制）
                UpdateLeftButtonIconPreview(); // 更新预览
                CheckForChanges();
            }
        }

        private async void OnRightButtonHotkeyButtonClick(object sender, RoutedEventArgs e)
        {
            var result = await HotkeyRecorderHelper.ShowRecorderAsync(
                this,
                _rightButtonRecordedKey,
                _rightButtonRecordedCtrl,
                _rightButtonRecordedShift,
                _rightButtonRecordedAlt);

            if (result != null)
            {
                _rightButtonRecordedKey = result.Key;
                _rightButtonRecordedCtrl = result.Ctrl;
                _rightButtonRecordedShift = result.Shift;
                _rightButtonRecordedAlt = result.Alt;
                
                UpdateRightButtonHotkeyPreview();
                CheckForChanges();
            }
        }

        private async void OnRightButtonPickIconClick(object sender, RoutedEventArgs e)
        {
            var result = await IconPickerHelper.ShowPickerAsync(this, _rightButtonIconCode);
            
            if (result != null)
            {
                _rightButtonIconCode = result; // 存储 Code（十六进制）
                UpdateRightButtonIconPreview(); // 更新预览
                CheckForChanges();
            }
        }

        /// <summary>
        /// 更新左侧按钮 Expander 内的子项启用状态
        /// ✅ AOT 优化：使用 for 循环 + 类型缓存，避免重复类型检查
        /// </summary>
        private void UpdateLeftButtonExpanderItemsEnabled()
        {
            bool isEnabled = LeftButtonEnabledToggle.IsOn;
            
            if (LeftButtonExpander?.Items == null)
            {
                return;
            }

            // ✅ 缓存 Count 避免重复访问属性
            int count = LeftButtonExpander.Items.Count;
            
            // ✅ 使用索引访问避免 IEnumerable 装箱
            for (int i = 0; i < count; i++)
            {
                // ✅ 使用模式匹配直接获取类型化对象
                if (LeftButtonExpander.Items[i] is CommunityToolkit.WinUI.Controls.SettingsCard card)
                {
                    card.IsEnabled = isEnabled;
                }
            }
        }

        /// <summary>
        /// 更新右侧按钮 Expander 内的子项启用状态
        /// ✅ AOT 优化：使用 for 循环 + 类型缓存，避免重复类型检查
        /// </summary>
        private void UpdateRightButtonExpanderItemsEnabled()
        {
            bool isEnabled = RightButtonEnabledToggle.IsOn;
            
            if (RightButtonExpander?.Items == null)
            {
                return;
            }

            // ✅ 缓存 Count 避免重复访问属性
            int count = RightButtonExpander.Items.Count;
            
            // ✅ 使用索引访问避免 IEnumerable 装箱
            for (int i = 0; i < count; i++)
            {
                // ✅ 使用模式匹配直接获取类型化对象
                if (RightButtonExpander.Items[i] is CommunityToolkit.WinUI.Controls.SettingsCard card)
                {
                    card.IsEnabled = isEnabled;
                }
            }
        }

        private void UpdateLeftButtonHotkeyPreview()
        {
            if (LeftButtonHotkeyPreview == null)
            {
                return;
            }

            if (_leftButtonRecordedKey == VirtualKey.None)
            {
                LeftButtonHotkeyPreview.Text = LocalizationHelper.GetString("WebAppDetailPage_HotkeyNotSetShort");
                return;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (_leftButtonRecordedCtrl) parts.Add("Ctrl");
            if (_leftButtonRecordedShift) parts.Add("Shift");
            if (_leftButtonRecordedAlt) parts.Add("Alt");
            parts.Add(GetKeyDisplayName(_leftButtonRecordedKey));

            LeftButtonHotkeyPreview.Text = string.Join(" + ", parts);
        }

        private void UpdateRightButtonHotkeyPreview()
        {
            if (RightButtonHotkeyPreview == null)
            {
                return;
            }

            if (_rightButtonRecordedKey == VirtualKey.None)
            {
                RightButtonHotkeyPreview.Text = LocalizationHelper.GetString("WebAppDetailPage_HotkeyNotSetShort");
                return;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (_rightButtonRecordedCtrl) parts.Add("Ctrl");
            if (_rightButtonRecordedShift) parts.Add("Shift");
            if (_rightButtonRecordedAlt) parts.Add("Alt");
            parts.Add(GetKeyDisplayName(_rightButtonRecordedKey));

            RightButtonHotkeyPreview.Text = string.Join(" + ", parts);
        }

        private static string GetKeyDisplayName(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Space => "Space",
                VirtualKey.Enter => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Escape => "Esc",
                VirtualKey.Back => "Backspace",
                VirtualKey.Delete => "Delete",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "PageUp",
                VirtualKey.PageDown => "PageDown",
                VirtualKey.Left => "←",
                VirtualKey.Right => "→",
                VirtualKey.Up => "↑",
                VirtualKey.Down => "↓",
                _ => key.ToString()
            };
        }

        private KeyboardMappingButtonConfig GetCurrentLeftButtonConfig()
        {
            // 防止在控件加载前调用
            if (LeftButtonEnabledToggle == null || 
                LeftButtonIconTypeComboBox == null || 
                LeftButtonTooltipTextBox == null)
            {
                return KeyboardMappingButtonConfig.CreateDefault();
            }

            // ✅ AOT 修复：使用 SelectedIndex 而不是 Tag
            string iconType = (LeftButtonIconTypeComboBox.SelectedIndex == 1) ? "Animated" : "Static";

            // ✅ AOT 修复：使用索引数组映射而不是 GetSelectedComboBoxTag
            int animatedIconIndex = LeftButtonAnimatedIconTypeComboBox?.SelectedIndex ?? 2;
            if (animatedIconIndex < 0 || animatedIconIndex >= AnimatedIconTypes.Length)
                animatedIconIndex = 2; // 默认为 AnimatedChevronDownSmallVisualSource
            string animatedIconType = AnimatedIconTypes[animatedIconIndex];

            return new KeyboardMappingButtonConfig
            {
                IsEnabled = LeftButtonEnabledToggle.IsOn,
                IconType = iconType,
                StaticIconGlyph = CodeToGlyph(_leftButtonIconCode) ?? "\uE92E", // Code → Glyph
                AnimatedIconType = animatedIconType,
                Tooltip = LeftButtonTooltipTextBox.Text?.Trim() ?? LocalizationHelper.GetString("WebAppDetailPage_DefaultTooltip"),
                Key = _leftButtonRecordedKey,
                Ctrl = _leftButtonRecordedCtrl,
                Shift = _leftButtonRecordedShift,
                Alt = _leftButtonRecordedAlt
            };
        }

        private KeyboardMappingButtonConfig GetCurrentRightButtonConfig()
        {
            // 防止在控件加载前调用
            if (RightButtonEnabledToggle == null || 
                RightButtonIconTypeComboBox == null || 
                RightButtonTooltipTextBox == null)
            {
                return KeyboardMappingButtonConfig.CreateDefault();
            }

            // ✅ AOT 修复：使用 SelectedIndex 而不是 Tag
            string iconType = (RightButtonIconTypeComboBox.SelectedIndex == 1) ? "Animated" : "Static";

            // ✅ AOT 修复：使用索引数组映射而不是 GetSelectedComboBoxTag
            int animatedIconIndex = RightButtonAnimatedIconTypeComboBox?.SelectedIndex ?? 2;
            if (animatedIconIndex < 0 || animatedIconIndex >= AnimatedIconTypes.Length)
                animatedIconIndex = 2; // 默认为 AnimatedChevronDownSmallVisualSource
            string animatedIconType = AnimatedIconTypes[animatedIconIndex];

            return new KeyboardMappingButtonConfig
            {
                IsEnabled = RightButtonEnabledToggle.IsOn,
                IconType = iconType,
                StaticIconGlyph = CodeToGlyph(_rightButtonIconCode) ?? "\uE92E", // Code → Glyph
                AnimatedIconType = animatedIconType,
                Tooltip = RightButtonTooltipTextBox.Text?.Trim() ?? LocalizationHelper.GetString("WebAppDetailPage_DefaultTooltip"),
                Key = _rightButtonRecordedKey,
                Ctrl = _rightButtonRecordedCtrl,
                Shift = _rightButtonRecordedShift,
                Alt = _rightButtonRecordedAlt
            };
        }

        private bool IsLeftButtonConfigChanged()
        {
            var current = GetCurrentLeftButtonConfig();
            var original = _originalLeftButtonConfig ?? KeyboardMappingButtonConfig.CreateDefault();
            
            return current.IsEnabled != original.IsEnabled ||
                   current.IconType != original.IconType ||
                   current.StaticIconGlyph != original.StaticIconGlyph ||
                   current.AnimatedIconType != original.AnimatedIconType ||
                   current.Tooltip != original.Tooltip ||
                   current.Key != original.Key ||
                   current.Ctrl != original.Ctrl ||
                   current.Shift != original.Shift ||
                   current.Alt != original.Alt;
        }

        private bool IsRightButtonConfigChanged()
        {
            var current = GetCurrentRightButtonConfig();
            var original = _originalRightButtonConfig ?? KeyboardMappingButtonConfig.CreateDefault();
            
            return current.IsEnabled != original.IsEnabled ||
                   current.IconType != original.IconType ||
                   current.StaticIconGlyph != original.StaticIconGlyph ||
                   current.AnimatedIconType != original.AnimatedIconType ||
                   current.Tooltip != original.Tooltip ||
                   current.Key != original.Key ||
                   current.Ctrl != original.Ctrl ||
                   current.Shift != original.Shift ||
                   current.Alt != original.Alt;
        }

        /// <summary>
        /// 从 ComboBox 中选择指定 Tag 的项
        /// ✅ AOT 兼容：使用索引遍历 + 显式类型检查 + 字符串比较优化
        /// </summary>
        private void SelectComboBoxItemByTag(ComboBox? comboBox, string? tag)
        {
            if (comboBox == null || string.IsNullOrEmpty(tag))
            {
                return;
            }

            // ✅ 使用 Count 避免调用 GetEnumerator()（防止装箱）
            int count = comboBox.Items.Count;
            for (int i = 0; i < count; i++)
            {
                // ✅ 直接使用索引器访问，避免 foreach 的装箱
                object? obj = comboBox.Items[i];
                
                // ✅ 模式匹配 + null 检查合并，减少分支预测失败
                if (obj is ComboBoxItem { Tag: not null } item)
                {
                    string? itemTag = item.Tag.ToString();
                    // ✅ 使用 Ordinal 比较避免文化敏感性能开销
                    if (string.Equals(itemTag, tag, StringComparison.Ordinal))
                    {
                        comboBox.SelectedItem = item;
                        return;
                    }
                }
            }
        }

        // 辅助方法：获取 ComboBox 选中项的 Tag
        private string? GetSelectedComboBoxTag(ComboBox? comboBox)
        {
            return (comboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        }

        // 辅助方法：比较两个按钮配置是否相等
        private static bool IsButtonConfigEqual(KeyboardMappingButtonConfig? a, KeyboardMappingButtonConfig? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            
            return a.IsEnabled == b.IsEnabled &&
                   a.IconType == b.IconType &&
                   a.StaticIconGlyph == b.StaticIconGlyph &&
                   a.AnimatedIconType == b.AnimatedIconType &&
                   a.Tooltip == b.Tooltip &&
                   a.Key == b.Key &&
                   a.Ctrl == b.Ctrl &&
                   a.Shift == b.Shift &&
                   a.Alt == b.Alt;
        }

        #endregion
        
        #region 图标 Code ↔ Glyph 转换和预览更新

        /// <summary>
        /// 将 Glyph (Unicode 字符) 转换为 Code (十六进制字符串)
        /// </summary>
        private static string? GlyphToCode(string? glyph)
        {
            if (string.IsNullOrEmpty(glyph) || glyph.Length == 0)
            {
                LogDebug("[GlyphToCode] 输入为空");
                return null;
            }

            // 获取第一个字符的 Unicode 码点
            int codePoint = char.ConvertToUtf32(glyph, 0);
            string code = codePoint.ToString("X"); // 转换为十六进制（大写）
            
            LogDebug($"[GlyphToCode] 输入: '{glyph}' (长度={glyph.Length}), 输出: {code}, 码点={codePoint}");
            return code;
        }

        /// <summary>
        /// 将 Code (十六进制字符串) 转换为 Glyph (Unicode 字符)
        /// </summary>
        private static string? CodeToGlyph(string? code)
        {
            if (string.IsNullOrEmpty(code))
            {
                LogDebug("[CodeToGlyph] 输入为空");
                return null;
            }

            try
            {
                // 解析十六进制字符串
                int codePoint = Convert.ToInt32(code, 16);
                string glyph = char.ConvertFromUtf32(codePoint);
                
                LogDebug($"[CodeToGlyph] 输入: {code}, 输出: '{glyph}' (长度={glyph.Length}), 码点={codePoint}");
                return glyph;
            }
            catch (Exception ex)
            {
                LogDebug($"[CodeToGlyph] 转换失败: code={code}, 错误={ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 更新左侧按钮图标预览
        /// </summary>
        private void UpdateLeftButtonIconPreview()
        {
            if (LeftButtonStaticIconPreview == null)
            {
                LogDebug("[UpdateLeftButtonIconPreview] LeftButtonStaticIconPreview is null");
                return;
            }

            string? glyph = CodeToGlyph(_leftButtonIconCode);
            LogDebug($"[UpdateLeftButtonIconPreview] _leftButtonIconCode={_leftButtonIconCode}, glyph={(glyph != null ? $"长度={glyph.Length}" : "null")}");
            
            if (!string.IsNullOrEmpty(glyph))
            {
                LeftButtonStaticIconPreview.Glyph = glyph;
                LogDebug($"[UpdateLeftButtonIconPreview] 已设置 Glyph, FontFamily={LeftButtonStaticIconPreview.FontFamily}");
            }
            else
            {
                // 默认图标
                LeftButtonStaticIconPreview.Glyph = "\uE92E";
                LogDebug("[UpdateLeftButtonIconPreview] 使用默认 Glyph E92E");
            }
        }

        /// <summary>
        /// 更新右侧按钮图标预览
        /// </summary>
        private void UpdateRightButtonIconPreview()
        {
            if (RightButtonStaticIconPreview == null)
            {
                LogDebug("[UpdateRightButtonIconPreview] RightButtonStaticIconPreview is null");
                return;
            }

            string? glyph = CodeToGlyph(_rightButtonIconCode);
            LogDebug($"[UpdateRightButtonIconPreview] _rightButtonIconCode={_rightButtonIconCode}, glyph={(glyph != null ? $"长度={glyph.Length}" : "null")}");
            
            if (!string.IsNullOrEmpty(glyph))
            {
                RightButtonStaticIconPreview.Glyph = glyph;
                LogDebug($"[UpdateRightButtonIconPreview] 已设置 Glyph, FontFamily={RightButtonStaticIconPreview.FontFamily}");
            }
            else
            {
                // 默认图标
                RightButtonStaticIconPreview.Glyph = "\uE92E";
                LogDebug("[UpdateRightButtonIconPreview] 使用默认 Glyph E92E");
            }
        }

        #endregion
    }
}
