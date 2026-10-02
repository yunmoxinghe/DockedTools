using DockedTools.Features.Pages.Settings;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - WebView配置模块
    /// 包含右键菜单配置、性能设置、脚本注入等
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        // ⚠️ OnWinUIContextMenuSettingsChanged、UpdateContextMenuConfiguration、UpdateContextMenuForWebView已移至 网页浏览页面.ContextMenu.cs

        private void OnWebViewPerformanceSettingsChanged(object? sender, EventArgs e)
        {
            // 性能设置改变时，应用新设置
            // 注意：某些设置需要重启 WebView 才能生效（如浏览器参数）
            ApplyMemoryModeSettings();
            
            System.Diagnostics.Debug.WriteLine("[OnWebViewPerformanceSettingsChanged] 性能设置已更新，某些设置需要重新加载页面才能生效");
        }

        /// <summary>
        /// 透明背景实验室设置变化：把能即时生效的部分应用到当前实例
        /// 
        /// 能即时生效：XAML 属性模式的底色、探针色块
        /// 需要重建 WebView：ControllerOptions / 环境变量两种策略、以及浏览器启动参数
        /// </summary>
        private void OnWebViewTransparencySettingsChanged(object? sender, EventArgs e)
        {
            ApplyWebViewTransparency();
            ApplyWebViewTransparencyProbe();
        }

        // ⚠️ UpdateContextMenuConfiguration、UpdateContextMenuForWebView已移至 网页浏览页面.ContextMenu.cs

        // ⚠️ EnsureTintScriptInstalledAsync已移至 网页浏览页面.WebView.cs
    }
}
