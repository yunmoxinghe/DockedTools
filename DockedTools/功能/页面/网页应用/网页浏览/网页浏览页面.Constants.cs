namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 常量定义模块
    /// 包含所有魔术数字和配置常量
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private const double LuminanceThreshold = 0.179; // WCAG 标准阈值（归一化后）
        private const double MinOpacity = 0.01;
        private const double PercentageMax = 100.0;
        private const double ColorChannelMax = 255.0;
        private const int ColorTransitionDurationMs = 300; // 颜色过渡动画时长
        
        // 响应式布局间距比例（视觉平衡最佳实践）
        private const double ContainerPaddingMultiplier = 1.0; // 容器边距 = 按钮间距（所有间距一致）
        
        // 按钮状态叠加层强度（Material Design 最佳实践）
        private const double ButtonHoverOverlayStrength = 0.08;   // Hover 叠加 8%
        private const double ButtonPressedOverlayStrength = 0.12; // Pressed 叠加 12%
        private const double ButtonDisabledOpacity = 0.38;        // Disabled 透明度 38%
        private const double ButtonHoverBackgroundOpacity = 0.08; // Hover 背景叠加 8%
        private const double ButtonPressedBackgroundOpacity = 0.12; // Pressed 背景叠加 12%
        
        // 加载进度条相关
        private const int IndeterminateAnimationCycleMs = 500; // 不确定模式动画周期时长（估算）
        
        // 双击检测相关
        private const int DoubleClickMaxDelayMs = 500; // 双击最大间隔时间（毫秒）
        
        // 重载防抖相关
        private const int ReloadDebounceMs = 500; // 重载防抖时间（毫秒）
        
        // WebView 无响应检测相关
        private const int MaxUnresponsiveCountBeforeReload = 3; // ⭐ 连续无响应多少次后触发 Reload
    }
}
