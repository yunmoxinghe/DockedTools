using DockedTools.Features.Pages.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace DockedTools.Features.Pages.Lab
{
    /// <summary>
    /// 实验室页面 - WebView2 透明背景实验室模块
    ///
    /// 【这个实验在验证什么】
    /// 微软 Visual layer 文档写明：WebView2 属于 external content，XAML 合成器看不见它的像素，
    /// 只能在合成结果上挖洞让它露出来。因此唯一能位于 WebView2 后面的，
    /// 只有同为 external content 的 SystemBackdrop（Mica / 桌面亚克力）和窗口底色，
    /// 普通 XAML 内容（Grid、Border、按钮）永远不可能透出来。
    ///
    /// 本模块把「透明怎么施加」做成 5 档策略 + 1 个底层探针，供逐组对照验证。
    /// </summary>
    public sealed partial class LabPage
    {
        /// <summary>
        /// 透明背景实验室设置变化通知（即时生效的部分由 WebBrowserPage 自行应用）
        /// </summary>
        public static event EventHandler? WebViewTransparencySettingsChanged;

        /// <summary>
        /// 触发透明背景实验室设置变化通知
        /// </summary>
        internal static void RaiseWebViewTransparencySettingsChanged() =>
            WebViewTransparencySettingsChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>
        /// 初始化透明背景实验室的控件状态（由 OnLoaded 调用）
        /// </summary>
        private void InitializeWebViewTransparencyLab()
        {
            WebViewTransparencyModeComboBox.SelectionChanged -= OnWebViewTransparencyModeChanged;
            WebViewTransparencyModeComboBox.SelectedIndex = (int)ExperimentalSettings.WebViewTransparencyMode;
            WebViewTransparencyModeComboBox.SelectionChanged += OnWebViewTransparencyModeChanged;

            WebViewTransparencyProbeToggle.IsOn = ExperimentalSettings.WebViewTransparencyProbe;
            WebViewCancelInitialNavigationToggle.IsOn = ExperimentalSettings.WebViewCancelInitialNavigation;

            UpdateWebViewTransparencyStatus();
        }

        private void OnWebViewTransparencyModeCardClick(object sender, RoutedEventArgs e)
        {
            WebViewTransparencyModeComboBox.IsDropDownOpen = true;
        }

        private void OnWebViewTransparencyModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WebViewTransparencyModeComboBox.SelectedIndex < 0)
            {
                return;
            }

            ExperimentalSettings.WebViewTransparencyMode =
                (WebViewTransparencyMode)WebViewTransparencyModeComboBox.SelectedIndex;

            RaiseWebViewTransparencySettingsChanged();
            UpdateWebViewTransparencyStatus();

            System.Diagnostics.Debug.WriteLine(
                $"[TransparencyLab] 策略切换为 {ExperimentalSettings.WebViewTransparencyMode}");
        }

        private void OnWebViewTransparencyProbeCardClick(object sender, RoutedEventArgs e)
        {
            WebViewTransparencyProbeToggle.IsOn = !WebViewTransparencyProbeToggle.IsOn;
        }

        private void OnWebViewTransparencyProbeToggled(object sender, RoutedEventArgs e)
        {
            ExperimentalSettings.WebViewTransparencyProbe = WebViewTransparencyProbeToggle.IsOn;
            RaiseWebViewTransparencySettingsChanged();
        }

        private void OnWebViewCancelInitialNavigationCardClick(object sender, RoutedEventArgs e)
        {
            WebViewCancelInitialNavigationToggle.IsOn = !WebViewCancelInitialNavigationToggle.IsOn;
        }

        private void OnWebViewCancelInitialNavigationToggled(object sender, RoutedEventArgs e)
        {
            ExperimentalSettings.WebViewCancelInitialNavigation = WebViewCancelInitialNavigationToggle.IsOn;
            RaiseWebViewTransparencySettingsChanged();
        }

        /// <summary>
        /// 刷新当前策略的判读说明
        /// </summary>
        private void UpdateWebViewTransparencyStatus()
        {
            WebViewTransparencyStatus.Text = ExperimentalSettings.WebViewTransparencyMode switch
            {
                WebViewTransparencyMode.Opaque =>
                    "白色不透明，作为 A/B 对照基线。",
                WebViewTransparencyMode.PostInit =>
                    "控制器创建完成后写入 DefaultBackgroundColor，可即时切换，代价是可能闪一帧白底。",
                WebViewTransparencyMode.PreInit =>
                    "在 EnsureCoreWebView2Async 之前定色，从源头消除白闪；切换后需重建 WebView 才生效。",
                WebViewTransparencyMode.EnvironmentVariable =>
                    "进程级环境变量，创建时读取一次，且会覆盖其它写法成为初始值；切换后需重建 WebView 才生效。",
                _ =>
                    "环境变量 + 创建前属性双保险，用于确认环境变量是否真正接手；切换后需重建 WebView 才生效。"
            };
        }
    }
}
