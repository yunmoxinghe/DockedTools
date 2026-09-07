using DockedTools.Features.MainWindow.KeyboardManagement;
using DockedTools.Features.MainWindow.State;
using DockedTools.Features.MainWindow.Visibility;
using DockedTools.Features.MainWindowContent.Linker;
using DockedTools.Features.Tray;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DockedTools
{
    /// <summary>
    /// 主窗口类 - 应用程序的核心 UI 容器
    /// 
    /// 【文件职责】
    /// 1. 作为应用的主窗口入口，协调 ViewModel、Controller 和 UI 组件
    /// 2. 管理窗口生命周期事件（激活、关闭、尺寸变化）
    /// 3. 响应用户交互（固定/取消固定、最大化/还原）
    /// 4. 同步 ViewModel 状态到 UI 表现（图标、圆角、边距）
    /// 
    /// 【核心逻辑流程】
    /// 初始化阶段：
    ///   1. 构造函数中通过 DWM API 设置纯色背景，避免亚克力在启动屏幕前显示
    ///   2. 创建 MainWindowViewModel（状态容器）和 WindowHostController（状态转换执行器）
    ///   3. 订阅 Linker 事件（用户交互）、ViewModel 属性变化（状态同步）、AppWindow 事件（OS 窗口状态）
    ///   4. 初始化 UI 状态（图标、圆角、边距）
    /// 
    /// 启动屏幕流程：
    ///   1. 窗口激活时显示纯色背景（黑/白）
    ///   2. ShowSplash() 被调用，立即播放淡入动画（纯色 → 启动屏幕图片）
    ///   3. 启动屏幕淡出完成后，设置亚克力背景
    /// 
    /// 运行时状态同步：
    ///   - ViewModel.CurrentState 变化 → 触发 OnViewModelPropertyChanged → 刷新 UI（图标、圆角、边距）
    ///   - AppWindow.Changed 事件 → 检测 OS 窗口状态变化 → 同步到 Controller
    ///   - Linker 事件（用户点击按钮）→ 调用 Controller 方法 → 触发状态转换
    /// 
    /// 【关键依赖关系】
    /// - MainWindowViewModel: 状态容器，持有 CurrentState（Windowed/Pinned/Maximized/Hidden）
    /// - WindowHostController: 状态转换执行器，负责动画、样式、布局的实际操作
    /// - Linker: UI 组件桥接器，提供 NavBar 和内容区的访问接口
    /// - AppWindow: WinUI 窗口对象，提供 OS 级别的窗口状态（最大化/还原）
    /// 
    /// 【潜在副作用】
    /// 1. DwmSetWindowAttribute 在构造函数和 ShowSplash 中调用，修改窗口 DWM 属性（不可逆）
    /// 2. ViewModel.PropertyChanged 事件触发 UI 更新（可能导致布局重排）
    /// 3. AppWindow.Changed 事件可能在动画执行期间触发，需要防重入
    /// 4. Linker 事件订阅/取消订阅必须成对，否则导致内存泄漏
    /// 
    /// 【重构风险点】
    /// 1. 事件订阅顺序：必须在 InitializeComponent() 之后订阅，否则 RootGrid 为 null
    /// 2. DWM API 调用时机：
    ///    - 构造函数中设置纯色背景（避免亚克力在启动屏幕前显示）
    ///    - ShowSplash 淡出后设置亚克力背景（确保平滑过渡）
    /// 3. RefreshViewModelDrivenState 和 RefreshWindowChromeState 的调用时机：
    ///    - 前者依赖 ViewModel.CurrentState，后者依赖 AppWindow.Presenter.State
    ///    - 两者必须分开调用，避免状态不一致
    /// 4. OnAppWindowChanged 中的 SyncFromOSWindowState：
    ///    - 仅在 DidPresenterChange 时调用，避免尺寸变化时误触发状态同步
    /// 5. 窗口关闭时必须取消所有事件订阅，否则导致内存泄漏
    /// </summary>
    public sealed partial class MainWindow : Window, IWindowToggle
    {
        // ==================== 常量定义 ====================
        
        /// <summary>
        /// 单帧延迟时间（毫秒）- 基于 60 FPS 计算
        /// 用于确保 UI 渲染完成后再执行下一步操作
        /// </summary>
        private const int FRAME_DELAY_MS = 16;
        
        /// <summary>
        /// 启动屏幕淡入动画时长（毫秒）
        /// </summary>
        private const int SPLASH_FADE_IN_MS = 400;
        
        /// <summary>
        /// 启动屏幕显示时长（毫秒）
        /// </summary>
        private const int SPLASH_DISPLAY_MS = 500;
        
        /// <summary>
        /// 启动屏幕总延迟时间（毫秒）= 淡入时长 + 显示时长
        /// </summary>
        private const int SPLASH_TOTAL_DELAY_MS = SPLASH_FADE_IN_MS + SPLASH_DISPLAY_MS;
        
        /// <summary>
        /// 焦点检测延迟时间（毫秒）
        /// 在启动屏幕结束后延迟检测窗口焦点状态
        /// </summary>
        private const int FOCUS_CHECK_DELAY_MS = 100;
        
        // ==================== 字段定义 ====================
        
        // 核心依赖：状态容器、控制器、UI 桥接器、快捷键管理器
        private readonly MainWindowViewModel _viewModel;
        private readonly WindowHostController _windowController;
        private readonly Linker? _linker;
        private readonly KeyboardShortcutManager _keyboardManager;
        private bool _isContentInitialized = false;

        /// <summary>
        /// 条件编译的调试日志方法
        /// 仅在 DEBUG 模式下执行，Release 版本完全移除，避免字符串分配开销
        /// </summary>
        /// <param name="message">调试消息</param>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] {message}");
        }

        /// <summary>
        /// 公开当前窗口状态，供外部组件（如托盘管理器）查询
        /// </summary>
        public WindowState CurrentWindowState => _viewModel.CurrentState;

        /// <summary>
        /// 窗口激活事件处理器 - 检测焦点状态
        /// 仅在内容初始化完成后才响应失焦事件
        /// </summary>
        private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
        {
            // 只在内容初始化完成后检测焦点
            if (!_isContentInitialized)
            {
                return;
            }

            // 如果窗口失去焦点（Deactivated），且未处于固定模式时自动隐藏
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                LogDebug("Window deactivated after content initialized");
                // 使用 ToggleWindow 来隐藏窗口（如果当前是显示状态且未固定）
                if (_viewModel.CurrentState != WindowState.Hidden && 
                    _viewModel.CurrentState != WindowState.Pinned)
                {
                    LogDebug("Hiding window (not pinned)");
                    _windowController.ToggleWindow();
                }
                else if (_viewModel.CurrentState == WindowState.Pinned)
                {
                    LogDebug("Window is pinned, ignoring deactivation");
                }
            }
        }

        /// <summary>
        /// 构造函数 - 初始化窗口、ViewModel、Controller 和事件订阅
        /// 
        /// 【关键设计决策】
        /// 1. 为什么构造函数中不设置任何 DWM 背景效果？
        ///    - 启动时使用默认的纯色背景（由 XAML 的 Background 属性控制）
        ///    - 亚克力效果在启动屏幕淡出后由 ShowSplash() 设置
        ///    - 避免在启动屏幕显示前出现任何透明或特殊效果
        /// 
        /// 2. 为什么不在构造函数中调用 Activate()？
        ///    - Activate() 会触发窗口显示动画，应由 WindowHostController.RequestSlideIn() 控制
        ///    - 过早调用会导致窗口在未完成布局配置时显示，产生闪烁
        /// 
        /// 3. 为什么先创建 ViewModel 再创建 Controller？
        ///    - Controller 需要 ViewModel 引用来订阅状态变化
        ///    - ViewModel 是纯数据容器，不依赖 Controller
        /// 
        /// 【副作用】
        /// - 订阅多个事件（必须在 OnWindowClosed 中取消订阅）
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            // 不设置任何 DWM 背景效果，使用默认的纯色背景
            // 亚克力效果将在启动屏幕淡出后由 ShowSplash() 设置

            // 创建 ViewModel（状态容器）和 Controller（状态转换执行器）
            _viewModel = new MainWindowViewModel();
            if (Content is FrameworkElement rootElement)
            {
                rootElement.DataContext = _viewModel;
            }

            // 获取 Linker（UI 桥接器），但不立即初始化内容
            _linker = MainLinker;
            _windowController = new WindowHostController(this, _viewModel);

            // ⭐ 注册主窗口服务（统一调用）
            Features.UnifiedCalls.MainWindow.MainWindowService.Register(_windowController, _viewModel);
            System.Diagnostics.Debug.WriteLine("[MainWindow] MainWindowService 已注册");

            // 创建快捷键管理器
            _keyboardManager = new KeyboardShortcutManager(
                switchToTab: (index) => _linker?.SwitchToWebAppByIndex(index),
                switchToNextTab: () => _linker?.SwitchToNextWebApp(),
                togglePinnedDock: () => TogglePinnedDock()
            );

            // 订阅事件：用户交互、状态变化、窗口事件
            SubscribeToLinkerEvents();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            AppWindow.Changed += OnAppWindowChanged;
            Closed += OnWindowClosed;
            Activated += OnWindowActivated;
            
            // 订阅实验室页面的刷新请求事件
            DockedTools.Features.Pages.Lab.LabPage.RefreshMonitorStateRequested += OnRefreshMonitorStateRequested;
            
            // ⭐ 订阅窗口最大化状态变化事件，转发到实验室页面
            DockedTools.Features.Pages.Lab.LabPage.WindowMaximizedStateChanged += OnLabPageWindowMaximizedStateChanged;

            // ⭐ 订阅 RootGrid.Loaded 事件（使用具名方法，AOT 友好）
            RootGrid.Loaded += OnRootGridLoaded;

            // 初始化 UI 状态（图标、圆角、边距）
            RefreshViewModelDrivenState();
            RefreshWindowChromeState();
        }

        /// <summary>
        /// 订阅 Linker 事件 - 响应用户交互（固定/取消固定、最大化/还原）
        /// 
        /// 【设计原因】
        /// Linker 是 UI 组件桥接器，封装了 NavBar 和内容区的访问接口
        /// 通过事件机制解耦 UI 交互和业务逻辑，避免直接依赖 UI 控件
        /// 
        /// 【重构风险】
        /// 如果 Linker 未在 XAML 中定义，_linker 为 null，事件订阅失败
        /// 必须在 XAML 中确保 Linker 存在于 RootGrid.Children 中
        /// </summary>
        private void SubscribeToLinkerEvents()
        {
            if (_linker is null)
            {
                Debug.WriteLine("MainWindow: Linker not found in RootGrid.");
                return;
            }

            // Linker 的 DockToggleRequested 和 WindowStateToggleRequested 事件已废弃
            // 现在统一使用 MainWindowService 处理固定和最大化切换
        }

        /// <summary>
        /// 取消订阅 Linker 事件 - 防止内存泄漏
        /// 
        /// 【重要性】
        /// 必须在窗口关闭时调用,否则 Linker 持有 MainWindow 引用,导致内存泄漏
        /// </summary>
        private void UnsubscribeFromLinkerEvents()
        {
            // Linker 的 DockToggleRequested 和 WindowStateToggleRequested 事件已废弃
            // 无需取消订阅
        }

        /// <summary>
        /// AppWindow.Changed 事件处理器 - 同步 OS 窗口状态到内部状态
        /// 
        /// 【触发时机】
        /// - 用户通过 Win+↑/↓ 快捷键最大化/还原窗口
        /// - 用户拖动窗口到屏幕顶部触发最大化
        /// - 窗口尺寸变化（需要过滤，避免误触发状态同步）
        /// 
        /// 【核心逻辑】
        /// 1. 仅在 DidPresenterChange 时同步状态（避免尺寸变化时误触发）
        /// 2. 调用 DetermineOSWindowState() 获取 OS 窗口状态
        /// 3. 调用 Controller.SyncFromOSWindowState() 同步到内部状态
        /// 4. 刷新窗口 Chrome 状态（图标、边距）
        /// 
        /// 【重构风险】
        /// 如果移除 DidPresenterChange 检查，尺寸变化会触发状态同步，导致状态抖动
        /// </summary>
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPresenterChange && !args.DidSizeChange)
            {
                return;
            }

            // 刷新窗口 Chrome 状态（图标、边距）
            RefreshWindowChromeState();

            // 仅在 Presenter 状态变化时同步（避免尺寸变化时误触发）
            if (args.DidPresenterChange)
            {
                _windowController.SyncFromOSWindowState(DetermineOSWindowState());
            }
        }

        /// <summary>
        /// 从 OS 窗口状态映射到内部状态
        /// 
        /// 【映射规则】
        /// - OverlappedPresenterState.Maximized → WindowState.Maximized
        /// - OverlappedPresenterState.Restored → WindowState.Windowed
        /// - OverlappedPresenterState.Minimized → WindowState.Hidden
        /// - 其他状态 → 保持当前状态（避免状态丢失）
        /// 
        /// 【设计原因】
        /// OS 窗口状态和内部状态不完全一致，需要映射：
        /// - OS 没有 Pinned 状态，Pinned 是应用自定义状态
        /// - OS 的 Minimized 映射到 Hidden（应用不使用最小化）
        /// </summary>
        private WindowState DetermineOSWindowState()
        {
            return AppWindow.Presenter is OverlappedPresenter presenter
                ? presenter.State switch
                {
                    OverlappedPresenterState.Maximized => WindowState.Maximized,
                    OverlappedPresenterState.Restored => WindowState.Windowed,
                    OverlappedPresenterState.Minimized => WindowState.Hidden,
                    _ => _viewModel.CurrentState
                }
                : _viewModel.CurrentState;
        }

        /// <summary>
        /// 检查窗口是否处于最大化状态
        /// 用于 UI 更新（图标、边距）
        /// </summary>
        private bool IsWindowMaximized()
        {
            return AppWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Maximized
            };
        }

        /// <summary>
        /// 刷新窗口 Chrome 状态 - 更新图标、圆角和边距
        /// 
        /// 【调用时机】
        /// - AppWindow.Changed 事件触发时（OS 窗口状态变化）
        /// - 构造函数初始化时
        /// 
        /// 【副作用】
        /// - 调用 Linker 方法更新 NavBar 图标
        /// - 调用 Linker 方法更新内容区圆角（最大化状态变化时）
        /// - 调用 Linker 方法更新内容区边距
        /// </summary>
        private void RefreshWindowChromeState()
        {
            UpdateWindowStateIcon();
            UpdateContentCornerRadius();
            UpdateContentTopMargin();
        }

        /// <summary>
        /// 刷新 ViewModel 驱动的状态 - 更新图标、圆角、边距
        /// 
        /// 【调用时机】
        /// - ViewModel.CurrentState 变化时（通过 PropertyChanged 事件）
        /// - 构造函数初始化时
        /// 
        /// 【副作用】
        /// - 调用 Linker 方法更新 NavBar 图标（固定/取消固定）
        /// - 调用 Linker 方法更新内容区圆角（固定/最大化模式下 8px 圆角，窗口化模式下 4px 圆角）
        /// - 调用 Linker 方法更新内容区边距（固定/最大化模式下无边距）
        /// </summary>
        private void RefreshViewModelDrivenState()
        {
            bool isPinned = _viewModel.CurrentState == WindowState.Pinned;
            UpdateDockToggleIcon(isPinned);
            UpdateContentCornerRadius();
            UpdateContentTopMargin();
        }

        /// <summary>
        /// 更新窗口状态图标（最大化/还原）
        /// 委托给 Linker.NavBarInstance 处理
        /// </summary>
        private void UpdateWindowStateIcon()
        {
            _linker?.NavBarInstance?.UpdateWindowStateIcon(IsWindowMaximized());
        }

        /// <summary>
        /// 切换窗口状态（最大化/还原）
        /// 公开方法，供外部组件（如网页浏览页面）调用
        /// </summary>
        public void ToggleWindowState()
        {
            _windowController.ToggleMaximize();
        }

        /// <summary>
        /// ViewModel.PropertyChanged 事件处理器 - 同步状态到 UI
        /// 
        /// 【触发时机】
        /// - WindowStateManager 提交状态转换后，ViewModel.CurrentState 变化
        /// 
        /// 【核心逻辑】
        /// 仅响应 CurrentState 属性变化，刷新 UI 状态（图标、圆角、边距）
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentState))
            {
                RefreshViewModelDrivenState();
            }
        }

        /// <summary>
        /// 更新固定/取消固定图标
        /// 委托给 Linker.NavBarInstance 处理
        /// </summary>
        private void UpdateDockToggleIcon(bool isPinned)
        {
            _linker?.NavBarInstance?.UpdateDockToggleIcon(isPinned);
        }

        /// <summary>
        /// 更新内容区圆角
        /// 固定模式或最大化模式下 8px 圆角，窗口化模式下 4px 圆角
        /// </summary>
        private void UpdateContentCornerRadius()
        {
            bool isPinnedOrMaximized = _viewModel.CurrentState == WindowState.Pinned || IsWindowMaximized();
            _linker?.UpdateContentCornerRadius(isPinnedOrMaximized);
        }

        /// <summary>
        /// 更新内容区顶部边距
        /// 固定模式或最大化模式下无边距（充满整个窗口），其他模式有边距
        /// </summary>
        private void UpdateContentTopMargin()
        {
            bool isPinnedOrMaximized = _viewModel.CurrentState == WindowState.Pinned || IsWindowMaximized();
            _linker?.UpdateContentTopMargin(isPinnedOrMaximized);
        }

        // ==================== IWindowToggle 接口实现 ====================
        // 这些方法由托盘管理器调用，用于控制窗口显示/隐藏和固定状态

        /// <summary>
        /// 切换窗口显示/隐藏状态
        /// 由托盘图标点击触发
        /// </summary>
        public void ToggleWindow()
        {
            _windowController.ToggleWindow();
        }

        /// <summary>
        /// 切换固定/取消固定状态
        /// 由 Linker 事件触发（用户点击固定按钮）
        /// </summary>
        public void TogglePinnedDock()
        {
            _windowController.TogglePinnedDock();
        }

        /// <summary>
        /// 标记初始化完成
        /// 由托盘管理器在窗口创建完成后调用，解除事件屏蔽
        /// </summary>
        public void SetInitializingComplete()
        {
            _windowController.SetInitializingComplete();
        }

        /// <summary>
        /// 请求执行首次显示动画
        /// 由托盘管理器在窗口创建完成后调用
        /// 利用 Activate() 的内置动画，这是首次创建窗口时唯一不会闪现的方案
        /// </summary>
        public void RequestSlideIn()
        {
            _windowController.RequestSlideIn();
        }

        /// <summary>
        /// 导航到新页面
        /// 由外部组件（如网页浏览页面）调用，用于打开新标签页
        /// </summary>
        /// <param name="url">要导航的 URL</param>
        public void NavigateToNewPage(string url)
        {
            Debug.WriteLine($"MainWindow.NavigateToNewPage called with URL: {url}");

            if (_linker is null)
            {
                Debug.WriteLine("MainWindow.NavigateToNewPage aborted: Linker not found.");
                return;
            }

            _linker.NavigateToNewPage(url);
        }

        /// <summary>
        /// 显示启动屏幕动画
        /// 由应用入口在窗口激活后调用
        /// 
        /// 【动画流程】
        /// 1. 立即播放淡入动画：纯色 → 启动屏幕图片（400ms）
        /// 2. 等待显示时间（1500ms）
        /// 3. 淡出动画：启动屏幕 → 主界面（300ms）
        /// 4. 淡出完成后设置亚克力背景并将 RootGrid 改为透明
        /// 5. 隐藏启动屏幕遮罩
        /// </summary>
        /// <summary>
        /// 显示启动屏幕（入口方法）
        /// 
        /// 【重构说明】
        /// 1. 保留 async void 签名以符合事件处理器要求
        /// 2. 使用 AsyncSafety.Run() 包装异步逻辑
        /// 3. 实际逻辑移到 ShowSplashAsync() 方法
        /// 
        /// 【职责】
        /// 1. 播放启动屏幕淡入动画（纯色 → 启动屏幕图片）
        /// 2. 显示启动屏幕一段时间
        /// 3. 播放淡出动画
        /// 4. 淡出完成后设置亚克力背景
        /// 5. 隐藏启动屏幕遮罩
        /// </summary>
        public void ShowSplash()
        {
            AsyncSafety.Run(ShowSplashAsync, "MainWindow", "ShowSplash");
        }

        /// <summary>
        /// 显示启动屏幕的实际异步逻辑
        /// 
        /// 【执行流程】
        /// 1. 检查窗口状态（确保未关闭且 Content 可用）
        /// 2. 设置 ColorOverlay 初始不透明度
        /// 3. 播放淡入动画（纯色 → 启动屏幕图片）
        /// 4. 等待显示时间
        /// 5. 播放淡出动画
        /// 6. 设置亚克力背景
        /// 7. 加载内容并标记初始化完成
        /// 8. 检查焦点并决定是否隐藏窗口
        /// 
        /// 【安全性】
        /// - 在访问 UI 元素前检查 Content 是否为 null
        /// - 捕获 SystemBackdrop 设置异常
        /// - 异常由 AsyncSafety.Run() 统一记录
        /// </summary>
        private async Task ShowSplashAsync()
        {
            LogDebug("ShowSplash started");
            
            // ⭐ 安全检查：确保窗口未关闭且 Content 可用
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content is null (window may be closed)");
                return;
            }
            
            // 确保 ColorOverlay 初始状态为完全不透明（纯色遮罩）
            // ⭐ 再次检查 Content（防止竞态条件）
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null during initialization");
                return;
            }
            
            ColorOverlay.Opacity = 1;
            
            // 等待一帧，确保 UI 渲染完成
            await Task.Delay(FRAME_DELAY_MS);
            
            // ⭐ 检查窗口状态
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null after initial delay");
                return;
            }
            
            // 立即播放淡入动画（纯色 -> 启动屏幕）
            var fadeInStoryboard = (Storyboard)SplashOverlay.Resources["SplashFadeIn"];
            fadeInStoryboard.Begin();
            LogDebug("Fade-in animation started");

            // 等待淡入完成 + 显示时间
            await Task.Delay(SPLASH_TOTAL_DELAY_MS);

            // ⭐ 检查窗口状态
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null during display");
                return;
            }
            
            // 使用 TaskCompletionSource 等待淡出动画完全完成
            var tcs = new TaskCompletionSource<bool>();
            
            var fadeOutStoryboard = (Storyboard)SplashOverlay.Resources["SplashFadeOut"];
            fadeOutStoryboard.Completed += (s, e) =>
            {
                tcs.SetResult(true);
            };

            fadeOutStoryboard.Begin();
            LogDebug("Fade-out animation started");
            
            // 等待淡出动画完成
            await tcs.Task;
            LogDebug("Fade-out animation completed");
            
            // ⭐ 检查窗口状态
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null after fade-out");
                return;
            }
            
            // 淡出完成后设置亚克力背景（使用 WinUI SystemBackdrop API）
            try
            {
                this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                LogDebug("Acrylic backdrop set after splash screen");
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to set acrylic backdrop: {ex.Message}");
            }
            
            // ⭐ 检查窗口状态
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null after backdrop setup");
                return;
            }
            
            // 将 RootGrid 背景改为透明，让亚克力效果透过来
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            
            // 确保启动屏幕完全隐藏
            SplashOverlay.Visibility = Visibility.Collapsed;

            // ⭐ 启动屏幕结束后加载内容（导航到首页）
            _linker?.LoadContent();
            LogDebug("Linker content loaded");

            // ⭐ 标记内容初始化完成（启动屏幕结束后）
            _isContentInitialized = true;
            LogDebug("Content initialization completed");

            // ⭐ 延迟检测焦点，如果失去焦点则自动隐藏（除非处于固定模式）
            await Task.Delay(FOCUS_CHECK_DELAY_MS);
            
            // ⭐ 最后检查窗口状态
            if (this.Content == null)
            {
                LogDebug("ShowSplash aborted: Content became null during focus check");
                return;
            }
            
            // 检查窗口是否仍然有焦点
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var foregroundWindow = GetForegroundWindow();
            
            if (hwnd != foregroundWindow)
            {
                LogDebug("Window lost focus after initialization");
                // 使用 ToggleWindow 来隐藏窗口（如果当前是显示状态且未固定）
                if (_viewModel.CurrentState != WindowState.Hidden && 
                    _viewModel.CurrentState != WindowState.Pinned)
                {
                    LogDebug("Hiding window (not pinned)");
                    _windowController.ToggleWindow();
                }
                else if (_viewModel.CurrentState == WindowState.Pinned)
                {
                    LogDebug("Window is pinned, ignoring focus loss");
                }
            }
            else
            {
                LogDebug("Window has focus after initialization, keeping visible");
            }
        }

        // Win32 API 用于获取前台窗口
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        /// <summary>
        /// 处理实验室页面的刷新监听器状态请求
        /// 调用 WindowController 手动触发监听器状态刷新
        /// </summary>
        private void OnRefreshMonitorStateRequested(object? sender, EventArgs e)
        {
            LogDebug("OnRefreshMonitorStateRequested triggered");
            _windowController.RequestRefreshMonitorState();
        }

        /// <summary>
        /// 处理实验室页面的窗口最大化状态变化通知
        /// 当其他应用窗口的最大化状态改变时触发
        /// </summary>
        private void OnLabPageWindowMaximizedStateChanged(object? sender, bool isMaximized)
        {
            LogDebug($"OnLabPageWindowMaximizedStateChanged: isMaximized={isMaximized}");
            // 这里可以根据需要添加额外的处理逻辑
            // 目前事件主要用于实验室页面自身的 UI 更新
        }

        /// <summary>
        /// RootGrid.Loaded 事件处理器 - 确保 RootGrid 可以接收键盘输入
        /// 
        /// 【设计原因】
        /// 给 RootGrid 设置焦点，使 KeyboardAccelerator 可以正常工作
        /// 使用具名方法而非 Lambda，符合 .NET 10 AOT 最佳实践
        /// 
        /// 【AOT 兼容性】
        /// - 具名方法：AOT 编译器可以静态分析，不会被 trimmer 移除
        /// - Lambda 表达式：会生成匿名类，可能导致内存泄漏且 AOT 不友好
        /// </summary>
        private void OnRootGridLoaded(object sender, RoutedEventArgs e)
        {
            // 给 RootGrid 设置焦点，使 KeyboardAccelerator 可以工作
            RootGrid.Focus(FocusState.Programmatic);
            LogDebug("RootGrid 已获取焦点，快捷键已启用");
        }

        // ==================== 快捷键处理方法 ====================
        // 所有快捷键逻辑由 KeyboardShortcutManager 管理

        /// <summary>
        /// PreviewKeyDown 事件处理 - 委托给快捷键管理器
        /// </summary>
        private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            _keyboardManager.HandlePreviewKeyDown(e);
        }

        /// <summary>
        /// Ctrl + 1~9: 切换到对应的网页应用标签
        /// </summary>
        private void OnSwitchToTab1(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(0, args);

        private void OnSwitchToTab2(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(1, args);

        private void OnSwitchToTab3(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(2, args);

        private void OnSwitchToTab4(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(3, args);

        private void OnSwitchToTab5(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(4, args);

        private void OnSwitchToTab6(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(5, args);

        private void OnSwitchToTab7(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(6, args);

        private void OnSwitchToTab8(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(7, args);

        private void OnSwitchToTab9(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToTab(-1, args); // -1 表示最后一个标签

        /// <summary>
        /// Ctrl + Tab: 切换到下一个网页应用标签（循环）
        /// </summary>
        private void OnSwitchToNextTab(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
            => _keyboardManager.HandleSwitchToNextTab(args);

        /// <summary>
        /// Ctrl + D: 固定/取消固定侧边栏 (Dock)
        /// </summary>
        private void OnTogglePinnedDock(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            _keyboardManager.HandleTogglePinnedDock(args);
        }

        /// <summary>
        /// 窗口关闭事件处理器 - 清理资源和取消事件订阅
        /// 
        /// 【重要性】
        /// 必须取消所有事件订阅，否则导致内存泄漏：
        /// - ViewModel.PropertyChanged
        /// - AppWindow.Changed
        /// - Closed
        /// - Activated
        /// - RootGrid.Loaded
        /// - Linker 事件
        /// - LabPage.RefreshMonitorStateRequested
        /// - LabPage.WindowMaximizedStateChanged
        /// 
        /// 【AOT 兼容性】
        /// 所有事件订阅使用具名方法，确保可以正确取消订阅
        /// 
        /// 【重构风险】
        /// 如果添加新的事件订阅，必须在此处取消订阅
        /// </summary>
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            Closed -= OnWindowClosed;
            Activated -= OnWindowActivated;
            RootGrid.Loaded -= OnRootGridLoaded;
            DockedTools.Features.Pages.Lab.LabPage.RefreshMonitorStateRequested -= OnRefreshMonitorStateRequested;
            DockedTools.Features.Pages.Lab.LabPage.WindowMaximizedStateChanged -= OnLabPageWindowMaximizedStateChanged;
            UnsubscribeFromLinkerEvents();
            
            // ⭐ 取消注册主窗口服务
            Features.UnifiedCalls.MainWindow.MainWindowService.Unregister();
            System.Diagnostics.Debug.WriteLine("[MainWindow] MainWindowService 已取消注册");
        }
    }
}
