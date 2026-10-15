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

        // ── WebView2 进程失败恢复的节流参数（2026-10-15 新增）────────────────
        // 【为什么必须有上限和退避】
        // 旧策略是「见到事件就立刻 Reload / 当场 Close+重建控件」。一个必崩的页面
        // 会被无限次 Reload；而浏览器进程组一旦整组消失（Runtime 后台自更新是最常见的
        // 外部诱因），LRU 里每个缓存页都会在同一时刻各自重建控件、各自拉起一棵进程树 ——
        // 全部发生在 UI 线程上。UI 线程被按住不泵消息，托盘看门狗就判卡死并静默重启，
        // 用户看到的正是「软件反复重启」。所以这里改成：有限次自愈 + 指数退避 + 后台页不动作。
        private const int MaxConsecutiveRecoveryAttempts = 3; // 连续自愈次数上限，达到即停止自动恢复
        private const int RecoveryBackoffBaseMs = 1500;       // 首次退避时长，之后每次翻倍
        private const int RecoveryBackoffMaxMs = 30000;       // 退避封顶，避免指数增长后等出分钟级空洞
    }
}
