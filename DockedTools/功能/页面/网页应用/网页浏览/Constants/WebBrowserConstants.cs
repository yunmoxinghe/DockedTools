namespace DockedTools.Features.Pages.WebApp.Browser.Constants
{
    /// <summary>
    /// 网页浏览器常量配置
    /// </summary>
    public static class WebBrowserConstants
    {
        /// <summary>
        /// WebView2 浏览器参数
        /// </summary>
        public static class BrowserArguments
        {
            public static readonly string[] OptimizedScrolling = new[]
            {
                "--enable-features=msEdgeFluentOverlayScrollbar",
                "--enable-smooth-scrolling",
                "--enable-gpu-rasterization",
                "--enable-zero-copy",
                "--disable-features=msExperimentalScrolling"
            };
        }

    }
}
