using DockedTools.Features.Pages.WebApp.Browser.Managers;
using Microsoft.UI.Xaml;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 圆角管理模块
    /// 负责动态同步父容器圆角到背景色块
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// 同步动态圆角到顶栏和 WebView 背景色块
        /// </summary>
        private void SyncCornerRadius()
        {
            try
            {
                // 使用 CornerRadiusManager 同步圆角
                CornerRadiusManager.SyncDynamicCorners(
                    this, 
                    WebPageTopAppBarBackground, 
                    BottomBarHost
                );
                
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 动态圆角同步成功");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 圆角同步失败: {ex.Message}");
            }
        }
    }
}
