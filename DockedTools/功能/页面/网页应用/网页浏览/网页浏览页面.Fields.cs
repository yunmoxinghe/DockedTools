using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 字段定义模块
    /// 包含所有私有字段声明
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        // 双击检测相关
        private DateTime _lastClickTime = DateTime.MinValue;
        
        // 重载防抖相关
        private DateTime _lastReloadTime = DateTime.MinValue;
        private bool _isReloading = false;

        private Uri? _pendingNavigationUri;
        private bool _isWebViewReady;
        private Shared.WebAppShortcut? _currentShortcut;
        private string? _contextMenuSelectedText;
        private string? _contextMenuLinkUrl;
        private bool _needsWebViewRecreation; // ⭐ 标记是否需要重新创建 WebView
        private CoreWebView2Environment? _webViewEnvironment; // ⭐ 保存 WebView2 environment 引用（用于订阅 BrowserProcessExited）
        private int _unresponsiveCount; // ⭐ 任务 3.4：记录 RenderProcessUnresponsive 连续次数
        private bool _isRecoveringWebView; // ⭐ 任务 3.5：防重入 guard，多个进程事件同时触发时只执行一次恢复

        // 键盘映射按钮：2026-10-02 起顶栏收的是【按钮数据】而不是 Button 实例，
        // 页面不再自己 new Button / 自己配悬停色 —— 外观统一由 AppTopBar 负责。
        private TopBarButton? _leftMappingButton;
        private TopBarButton? _rightMappingButton;

        // ✅ 修复：初始背景色完全透明，避免黑色闪现
        // 首次采样后会立即设置为正确的颜色
        private readonly SolidColorBrush _topBarBackgroundBrush = new(Colors.Transparent);
        private readonly SolidColorBrush _bottomBarBackgroundBrush = new(Colors.Transparent);
        private readonly SolidColorBrush _topBarForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarForegroundBrush = new();
        private readonly SolidColorBrush _topBarSecondaryForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarDisabledForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarHoverForegroundBrush = new();
        private bool _isDisposed;
        private string? _instanceId;
        
        // Reactor 底部按钮栏
        private Microsoft.UI.Reactor.Hosting.ReactorHostControl? _reactorHostControl;
        private Components.BottomButtonBar? _bottomButtonBarComponent;

        /// <summary>上一次参与按钮宽度计算的宿主宽度（<see cref="UpdateBottomBarLayout"/> 去抖用）</summary>
        private double _lastBottomBarHostWidth = double.NaN;

        /// <summary>上一次实际下发的按钮宽度（<see cref="UpdateBottomBarLayout"/> 去抖用）</summary>
        private double _lastAppliedButtonWidth = double.NaN;

        /// <summary>
        /// 底部栏按钮宽度的平滑驱动（离散跳变时才滑，连续拖拽直接跟）。
        /// 在构造函数里随底栏一起建，页面被 LRU 淘汰时随页面一起回收 ——
        /// 停着的 DispatcherQueueTimer 不会被队列持有，不需要显式 Dispose。
        /// </summary>
        private Services.BottomBarWidthTransition? _bottomBarWidthTransition;
        
        // 顶部栏状态：页面只有【数据】（标题文本 + 图标文件路径），
        // 换行/省略号/换字动画这些渲染细节都在 AppTopBar 那一侧。
        private string _topBarTitleText = string.Empty;
        private string? _topBarIconPath;
    }
}
