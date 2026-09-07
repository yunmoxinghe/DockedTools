using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.MainWindowContent.ContentArea;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using DockedTools.Features.Localization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 主文件(构造函数和协调逻辑)
    /// 已拆分为多个 partial class 文件以提高可维护性:
    /// - Constants.cs: 常量定义
    /// - Fields.cs: 字段声明
    /// - WebView.cs: WebView生命周期管理
    /// - Events.cs: 事件处理
    /// - Navigation.cs: 导航和INavigationAware
    /// - Theme.cs/Tint.cs/Sampling.cs: 主题色管理
    /// - UI.cs/TopBar.cs: UI组件
    /// - KeyboardMapping.cs: 键盘映射
    /// - ContextMenu.cs: 右键菜单
    /// - BottomBar.cs: Reactor底部按钮栏
    /// - Helpers.cs: 辅助方法
    /// </summary>
    public sealed partial class WebBrowserPage : Page, INavigationAware
    {
        // ⚠️ 常量定义已移至 网页浏览页面.Constants.cs
        // ⚠️ 字段声明已移至 网页浏览页面.Fields.cs

        public WebBrowserPage()
        {
            InitializeComponent();

            _instanceId = Guid.NewGuid().ToString();

            bool useWinUIContextMenu = ExperimentalSettings.EnableWinUIContextMenu;
            if (!useWinUIContextMenu)
            {
                WebView.ContextFlyout = null;
            }

            InitializeForegroundColors();
            InitializeTopBar();
            InitializeBottomBarReactor(); // ✅ 初始化 Reactor 底部按钮栏

            TopBarTintHost.Background = _topBarBackgroundBrush;
            // ⚠️ 不设置 BottomBarHost.Background，让 XAML 的 ThemeResource 生效

            BottomBarHost.SizeChanged += (s, e) => UpdateBottomBarLayout();

            Loaded += WebBrowserPage_Loaded;
            Unloaded += WebBrowserPage_Unloaded;
            
            // ✅ 监听系统主题变化
            ActualThemeChanged += OnSystemThemeChanged;
            
            // ✅ 订阅网页应用更新事件
            Shared.WebAppUpdateService.UpdateCompleted += OnWebAppUpdated;
            
            Pages.Settings.SettingsPage.WinUIContextMenuSettingsChanged += OnWinUIContextMenuSettingsChanged;
            Pages.Settings.SettingsPage.WebViewPerformanceSettingsChanged += OnWebViewPerformanceSettingsChanged;
        }

        // ⚠️ InitializeTopBar、SetupTopBar、UpdateTopBarContent、SetupRightContent已移至 网页浏览页面.TopBar.cs

        // ⚠️ Reactor底部按钮栏初始化和布局方法已移至 网页浏览页面.BottomBar.cs

        // ⚠️ InitializeForegroundColors、UpdateForegroundColorsFromTheme、OnSystemThemeChanged、ApplySystemThemeColors已移至 网页浏览页面.ForegroundColors.cs

        // ⚠️ 旧方法已废弃：SetButtonStateColors, ApplyBottomBarResponsiveLayout, UpdateButtonResources
        // 现在使用 Reactor 组件管理按钮


 // ⚠️ OnWinUIContextMenuSettingsChanged、OnWebViewPerformanceSettingsChanged、UpdateContextMenuConfiguration、UpdateContextMenuForWebView、EnsureTintScriptInstalledAsync已移至 网页浏览页面.WebViewConfig.cs

        // ⚠️ CoreWebView2 导航事件已移至 网页浏览页面.Events.cs
        
        /// <summary>
        /// WebView2 浏览器进程退出事件处理器（占位，任务 3.2-3.4 将实现完整功能）
        /// </summary>


        // ⚠️ CoreWebView2Environment_BrowserProcessExited、CoreWebView2_ProcessFailed已移至 网页浏览页面.ProcessManagement.cs

        // ⚠️ HideLoadingProgressBarSmoothlyAsync已移至 网页浏览页面.ProgressBar.cs
        
        // ⚠️ CoreWebView2_DocumentTitleChanged、CoreWebView2_WebMessageReceived、CoreWebView2WebMessageReceivedAsync已移至 网页浏览页面.MessageHandling.cs

        // ⚠️ ApplyBarTint、RestoreSharedTopAppBarBackground已移至 网页浏览页面.TintApplication.cs

        // ⚠️ CreateStateOverlayColor、CalculateLuminance、AdjustColorBrightness、AnimateColorChange、GetContrastingForeground、TryParseCssColor、TryParseByte已移至 网页浏览页面.ColorUtils.cs

        // ⚠️ CopyLinkMenuItem_Click、CopyUrlMenuItem_Click、OpenExternalMenuItem_Click已移至 网页浏览页面.ContextMenu.cs

        // ⚠️ HandleDoubleClick、GetMainWindowInstance已移至 网页浏览页面.Helpers.cs

        // ⚠️ SetupRightContent(重复)、SetupLeftMappingButton、SetupRightMappingButton、CreateAnimatedIcon、OnLeftMappingButtonClick、OnRightMappingButtonClick、SendHotkeyToWebViewAsync、GetKeyString、GetKeyCode已移至 网页浏览页面.KeyMappingButtons.cs

        // ⚠️ 取色采样策略方法已移至 网页浏览页面.Sampling.cs
    }
}







