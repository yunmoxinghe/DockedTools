using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;

namespace DockedTools.Features.Pages.Settings
{
    public sealed partial class WebViewPerformancePage : Page
    {
        private readonly 智能标题 _智能标题 = new();
        private const double MinResponsiveWidth = 320;
        private const double MaxResponsiveWidth = 760;
        private const double MinHorizontalMargin = 16;
        private const double MaxHorizontalMargin = 36;
        private double _lastAppliedMargin = -1;
        private double _lastMeasuredWidth = -1;

        public WebViewPerformancePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
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
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadWebViewPerformanceSettings();
            UpdateMargin();
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

        private void LoadWebViewPerformanceSettings()
        {
            FastStartupModeToggle.Toggled -= OnFastStartupModeToggled;
            FastStartupModeToggle.IsOn = ExperimentalSettings.FastStartupMode;
            FastStartupModeToggle.Toggled += OnFastStartupModeToggled;

            SingleProcessModeToggle.Toggled -= OnSingleProcessModeToggled;
            SingleProcessModeToggle.IsOn = ExperimentalSettings.SingleProcessMode;
            SingleProcessModeToggle.Toggled += OnSingleProcessModeToggled;

            MemoryModeComboBox.SelectionChanged -= OnMemoryModeChanged;
            MemoryModeComboBox.SelectedIndex = (int)ExperimentalSettings.MemoryMode;
            MemoryModeComboBox.SelectionChanged += OnMemoryModeChanged;

            AutoClearCacheToggle.Toggled -= OnAutoClearCacheToggled;
            AutoClearCacheToggle.IsOn = ExperimentalSettings.AutoClearCache;
            AutoClearCacheToggle.Toggled += OnAutoClearCacheToggled;

            SuspendInactiveToggle.Toggled -= OnSuspendInactiveToggled;
            SuspendInactiveToggle.IsOn = ExperimentalSettings.SuspendInactiveWebView;
            SuspendInactiveToggle.Toggled += OnSuspendInactiveToggled;

            DisableBackgroundNetworkToggle.Toggled -= OnDisableBackgroundNetworkToggled;
            DisableBackgroundNetworkToggle.IsOn = ExperimentalSettings.DisableBackgroundNetwork;
            DisableBackgroundNetworkToggle.Toggled += OnDisableBackgroundNetworkToggled;

            DisableExtensionsToggle.Toggled -= OnDisableExtensionsToggled;
            DisableExtensionsToggle.IsOn = ExperimentalSettings.DisableExtensions;
            DisableExtensionsToggle.Toggled += OnDisableExtensionsToggled;

            DisablePluginsToggle.Toggled -= OnDisablePluginsToggled;
            DisablePluginsToggle.IsOn = ExperimentalSettings.DisablePlugins;
            DisablePluginsToggle.Toggled += OnDisablePluginsToggled;

            DiskCacheSizeBox.ValueChanged -= OnDiskCacheSizeChanged;
            DiskCacheSizeBox.Value = ExperimentalSettings.DiskCacheSize;
            DiskCacheSizeBox.ValueChanged += OnDiskCacheSizeChanged;

            WebNotificationsToggle.Toggled -= OnWebNotificationsToggled;
            WebNotificationsToggle.IsOn = ExperimentalSettings.WebNotificationsEnabled;
            WebNotificationsToggle.Toggled += OnWebNotificationsToggled;

            EnableHardwareAccelerationToggle.Toggled -= OnEnableHardwareAccelerationToggled;
            EnableHardwareAccelerationToggle.IsOn = ExperimentalSettings.EnableHardwareAcceleration;
            EnableHardwareAccelerationToggle.Toggled += OnEnableHardwareAccelerationToggled;

            EnableHardwareOverlaysToggle.Toggled -= OnEnableHardwareOverlaysToggled;
            EnableHardwareOverlaysToggle.IsOn = ExperimentalSettings.EnableHardwareOverlays;
            EnableHardwareOverlaysToggle.Toggled += OnEnableHardwareOverlaysToggled;

            EnableHardwareVideoDecoderToggle.Toggled -= OnEnableHardwareVideoDecoderToggled;
            EnableHardwareVideoDecoderToggle.IsOn = ExperimentalSettings.EnableHardwareVideoDecoder;
            EnableHardwareVideoDecoderToggle.Toggled += OnEnableHardwareVideoDecoderToggled;

            DisableSoftwareRasterizerToggle.Toggled -= OnDisableSoftwareRasterizerToggled;
            DisableSoftwareRasterizerToggle.IsOn = ExperimentalSettings.DisableSoftwareRasterizer;
            DisableSoftwareRasterizerToggle.Toggled += OnDisableSoftwareRasterizerToggled;
        }

        private void OnMemoryModeChanged(object sender, SelectionChangedEventArgs e)
        {
            // ✅ AOT 修复：使用 SelectedIndex 而不是类型转换
            if (sender is ComboBox comboBox && comboBox.SelectedIndex >= 0)
            {
                ExperimentalSettings.MemoryMode = (WebViewMemoryMode)comboBox.SelectedIndex;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnFastStartupModeToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.FastStartupMode = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnSingleProcessModeToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.SingleProcessMode = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnAutoClearCacheToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.AutoClearCache = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnSuspendInactiveToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.SuspendInactiveWebView = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnDisableBackgroundNetworkToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.DisableBackgroundNetwork = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnDisableExtensionsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.DisableExtensions = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnWebNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.WebNotificationsEnabled = toggle.IsOn;

                // 不用 RaiseWebViewPerformanceSettingsChanged：
                // 桥接是在 CoreWebView2 初始化时挂的事件订阅，开关本身不需要重建 WebView ——
                // 权限 requests 与回传都靠运行时读这个设置，下一次通知立刻生效。
            }
        }

        private void OnDisablePluginsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.DisablePlugins = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnDiskCacheSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsNaN(args.NewValue))
            {
                int newValue = (int)args.NewValue;
                ExperimentalSettings.DiskCacheSize = newValue;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnEnableHardwareAccelerationToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableHardwareAcceleration = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnEnableHardwareOverlaysToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableHardwareOverlays = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnEnableHardwareVideoDecoderToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.EnableHardwareVideoDecoder = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void OnDisableSoftwareRasterizerToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch toggle)
            {
                ExperimentalSettings.DisableSoftwareRasterizer = toggle.IsOn;
                RaiseWebViewPerformanceSettingsChanged();
            }
        }

        private void RaiseWebViewPerformanceSettingsChanged()
        {
            SettingsPage.RaiseWebViewPerformanceSettingsChanged();
        }
    }
}
