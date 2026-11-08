using DockedTools.Features.MainWindow.Appearance; // 引入外观相关 Win32 API 封装（样式、DWM 属性等）
using DockedTools.Features.MainWindow.Placement;   // 引入位置相关 Win32 API 封装（RECT、SetWindowPos 等）
using DockedTools.Features.Pages.Settings;         // 引入设置相关类（ExperimentalSettings、WindowDockSide）
using Microsoft.UI.Xaml;                          // 引入 WinUI 3 的 Window 类型
using System;                                     // 引入 IntPtr、Math 等基础类型

namespace DockedTools.Features.MainWindow.Visibility
{
    /// <summary>
    /// 固定模式控制器 - 封装固定模式（AppBar）的进入和退出逻辑
    ///
    /// 【文件职责】
    /// 1. 进入固定模式：应用无边框窗口样式、注册 AppBar、设置 TopMost
    /// 2. 退出固定模式：还原标准窗口样式、注销 AppBar、取消 TopMost
    /// 3. 管理 AppBar 注册状态，确保注册/注销成对调用
    /// 4. 封装所有与固定模式相关的 Win32 API 调用
    ///
    /// 【架构设计】
    /// 从 WindowHostController 中提取，职责单一：
    /// - WindowHostController 负责协调所有状态转换
    /// - PinnedModeController 只负责固定模式的进入/退出细节
    ///
    /// 【进入固定模式流程】
    ///   1. ApplyPinnedWindowStyle()  - 移除边框/标题栏，设置 DWM 属性
    ///   2. ApplyPinnedBounds()       - 注册 AppBar，计算并应用固定位置
    ///   3. 调用方负责设置背景（Mica）和激活窗口
    ///
    /// 【退出固定模式流程】
    ///   1. RemoveAppBar()            - 注销 AppBar，释放屏幕空间
    ///   2. RestoreStandardWindowStyle() - 还原边框/标题栏样式
    ///   3. 调用方负责还原背景（Acrylic）和标题栏配置
    ///
    /// 【重构风险点】
    /// 1. ApplyPinnedWindowStyle 和 RestoreStandardWindowStyle 必须成对调用
    ///    - 如果只调用进入不调用退出，窗口样式会永久保持固定模式
    /// 2. RegisterAppBarIfNeeded 和 RemoveAppBar 必须成对调用
    ///    - 如果忘记注销，AppBar 会一直占用屏幕右侧空间
    /// 3. _isApplyingPinnedBounds 防重入标志
    ///    - ApplyPinnedBounds 会触发 AppWindow.Changed 事件
    ///    - 必须用此标志防止事件处理器递归调用 ApplyPinnedBounds
    /// 4. 基础样式只捕获一次（_hasCapturedBaseWindowStyle）
    ///    - 首次进入固定模式时捕获，后续复用
    ///    - 如果在捕获前窗口样式已被修改，还原后样式可能不正确
    /// </summary>
    internal sealed class PinnedModeController
    {
        private readonly Window _window;              // WinUI 3 窗口对象，用于访问 AppWindow 等属性
        private readonly WindowLayoutState _state;    // 窗口布局状态（位置、尺寸、工作区等共享数据）
        private readonly uint _appBarMessageId;       // AppBar 回调消息 ID，由外部注册并传入

        // AppBar 注册状态：true 表示已通过 ABM_NEW 注册，需要在退出时调用 ABM_REMOVE
        private bool _isAppBarRegistered;

        // 防重入标志：ApplyPinnedBounds 内部调用 SetWindowPos 会触发 AppWindow.Changed 事件，
        // 外部事件处理器检查此标志以避免递归调用 ApplyPinnedBounds
        internal bool IsApplyingPinnedBounds { get; private set; }

        // 基础窗口样式缓存：首次进入固定模式前捕获，退出时用于还原
        private bool _hasCapturedBaseWindowStyle;         // 是否已捕获 GWL_STYLE
        private bool _hasCapturedBaseExtendedWindowStyle; // 是否已捕获 GWL_EXSTYLE
        private IntPtr _baseWindowStyle;                  // 捕获的原始 GWL_STYLE 值
        private IntPtr _baseExtendedWindowStyle;          // 捕获的原始 GWL_EXSTYLE 值

        // 窗口句柄（HWND），窗口创建后由 WindowHostController 通过 SetWindowHandle 注入
        private IntPtr _hwnd;

        /// <summary>
        /// 构造函数：注入依赖，不执行任何 Win32 操作
        /// </summary>
        public PinnedModeController(Window window, WindowLayoutState state, uint appBarMessageId)
        {
            _window = window;               // 保存 WinUI 3 窗口引用
            _state = state;                 // 保存布局状态引用（共享对象，修改会影响调用方）
            _appBarMessageId = appBarMessageId; // 保存 AppBar 回调消息 ID
        }

        /// <summary>
        /// 提供窗口句柄（在窗口创建后由 WindowHostController 调用）
        /// </summary>
        public void SetWindowHandle(IntPtr hwnd)
        {
            _hwnd = hwnd; // 延迟注入 HWND，窗口创建前此值为 IntPtr.Zero
        }

        // ==================== 进入固定模式 ====================

        /// <summary>
        /// 进入固定模式：应用无边框样式并注册 AppBar
        ///
        /// 【调用顺序】
        /// 必须先调用 ApplyPinnedWindowStyle，再调用 ApplyPinnedBounds
        /// ApplyPinnedBounds 依赖 AppBar 注册，而注册需要窗口句柄有效
        /// </summary>
        public void EnterPinnedMode()
        {
            ApplyPinnedWindowStyle(); // 第一步：修改窗口样式为无边框 POPUP 模式
            ApplyPinnedBounds();      // 第二步：注册 AppBar 并将窗口对齐到屏幕右侧
        }

        // ==================== 固定模式切换动画 ====================

        /// <summary>
        /// 滑出动画：将窗口从当前位置平滑移动到屏幕边缘不可见区域
        ///
        /// 【设计原则】
        /// - 非阻断：立即返回 Task，动画在 CompositionTarget.Rendering 帧回调中执行
        /// - 可取消：通过 CancellationToken 支持打断动画
        /// - 不隐藏窗口：仅移动位置，窗口保持可见（样式切换在屏幕外进行）
        ///
        /// 【动画参数】
        /// - 起点：_state.CurrentX（当前窗口 X 坐标）
        /// - 终点：根据停靠位置决定
        ///   - 左侧停靠：-_state.WindowWidth（屏幕左侧外）
        ///   - 右侧停靠：_state.WorkArea.Right + _state.WindowWidth（屏幕右侧外）
        /// - 时长：360ms，Ease-out quadratic（快速离开，符合"滑走"的直觉）
        /// </summary>
        public System.Threading.Tasks.Task SlideOutAsync(System.Threading.CancellationToken ct = default)
        {
            if (_hwnd == IntPtr.Zero)
                return System.Threading.Tasks.Task.CompletedTask;

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            double startX   = _state.CurrentX;
            
            // 根据停靠位置决定滑出方向
            var dockSide = ExperimentalSettings.DockSide;
            double targetX = dockSide == WindowDockSide.Left 
                ? -_state.WindowWidth  // 左侧停靠：向左侧屏幕外滑出
                : _state.WorkArea.Right + _state.WindowWidth; // 右侧停靠：向右侧屏幕外滑出
            
            int    targetY  = (int)_state.CurrentY;
            int    height   = _state.WindowHeight;
            int    width    = _state.WindowWidth;
            var    duration = TimeSpan.FromMilliseconds(360);
            // 逐帧计时原先用 DateTime.Now —— 它每次都要做时区换算，60fps 下每帧一次不划算，
            // 而 TickCount64 的系统节拍（约 15.6ms）对 360ms 的动画又太粗，会把进度切成台阶。
            // 换成 Stopwatch：高精度、无分配，也是本仓库已有的做法（网页应用图标缓存 WaitUntilReadyAsync 就用了它）。
            System.Diagnostics.Stopwatch? sw = null; // 第一帧时才起表

            EventHandler<object>? onFrame = null;
            onFrame = (_, _) =>
            {
                // 第一帧：起表，避免订阅等待时间被计入
                sw ??= System.Diagnostics.Stopwatch.StartNew();

                // 取消时立即跳到终点并完成
                if (ct.IsCancellationRequested)
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
                    _state.CurrentX = targetX;
                    MoveToOffScreen(targetY, width, height);
                    tcs.TrySetCanceled();
                    return;
                }

                double progress = Math.Min(sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 1.0);
                double eased    = 1 - Math.Pow(1 - progress, 2); // Ease-out quadratic
                _state.CurrentX = startX + (targetX - startX) * eased;

                int newX = (int)Math.Round(_state.CurrentX);
                _ = VisibilityWin32Api.SetWindowPos(
                    _hwnd, VisibilityWin32Api.HWND_TOPMOST,
                    newX, targetY, width, height,
                    VisibilityWin32Api.SWP_NOACTIVATE | VisibilityWin32Api.SWP_NOOWNERZORDER);

                if (progress >= 1.0)
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
                    _state.CurrentX = targetX;
                    tcs.TrySetResult(true);
                }
            };

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
            return tcs.Task;
        }

        /// <summary>
        /// 滑入动画：将窗口从屏幕边缘不可见区域平滑移动到 AppBar 批准的目标位置
        ///
        /// 【前置条件】
        /// 必须在 ApplyPinnedBounds() 之后调用，此时 _state.TargetX/TargetY 已确定
        ///
        /// 【设计原则】
        /// - 非阻断：立即返回 Task，动画在 CompositionTarget.Rendering 帧回调中执行
        /// - 可取消：通过 CancellationToken 支持打断动画（取消时直接跳到终点）
        ///
        /// 【动画参数】
        /// - 起点：根据停靠位置决定
        ///   - 左侧停靠：-_state.WindowWidth（屏幕左侧外）
        ///   - 右侧停靠：_state.WorkArea.Right + _state.WindowWidth（屏幕右侧外）
        /// - 终点：_state.TargetX（AppBar 批准的最终位置）
        /// - 时长：440ms，Ease-out cubic（缓慢停止，符合"停靠"的直觉）
        /// </summary>
        public System.Threading.Tasks.Task SlideInAsync(System.Threading.CancellationToken ct = default)
        {
            if (_hwnd == IntPtr.Zero)
                return System.Threading.Tasks.Task.CompletedTask;

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            
            // 根据停靠位置决定滑入起点
            var dockSide = ExperimentalSettings.DockSide;
            double startX = dockSide == WindowDockSide.Left
                ? -_state.WindowWidth  // 左侧停靠：从屏幕左侧外滑入
                : _state.WorkArea.Right + _state.WindowWidth; // 右侧停靠：从屏幕右侧外滑入
            
            double targetX  = _state.TargetX;
            int    targetY  = (int)_state.TargetY;
            int    height   = _state.WindowHeight;
            int    width    = _state.WindowWidth;
            var    duration = TimeSpan.FromMilliseconds(440);
            System.Diagnostics.Stopwatch? sw = null; // 第一帧时才起表，避免订阅等待时间被计入

            // 先把窗口放到起始位置（屏幕外），避免第一帧出现在错误位置
            _state.CurrentX = startX;
            MoveToOffScreen(targetY, width, height);

            EventHandler<object>? onFrame = null;
            onFrame = (_, _) =>
            {
                // 第一帧：起表（口径与 SlideOutAsync 一致）
                sw ??= System.Diagnostics.Stopwatch.StartNew();

                // 取消时直接跳到终点
                if (ct.IsCancellationRequested)
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
                    _state.CurrentX = targetX;
                    _state.CurrentY = targetY;
                    SnapToTarget(targetX, targetY, width, height);
                    tcs.TrySetCanceled();
                    return;
                }

                double progress = Math.Min(sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 1.0);
                double eased    = 1 - Math.Pow(1 - progress, 3); // Ease-out cubic
                _state.CurrentX = startX + (targetX - startX) * eased;

                int newX = (int)Math.Round(_state.CurrentX);

                // 逐帧日志原先压在这里：440ms 动画 ≈ 二十多帧，每帧一次字符串插值 + 一次
                // OutputDebugString 互操作，而且它带 [Conditional("DEBUG")] ——
                // Release 里根本不存在，等于只有调试时更卡，正好和「调试版更容易无响应」对上。
                // 现在只在动画收尾记一条，信息量不减（progress 到这里必然是 1.0，
                // 剩下的 startX/targetX 才是排查滑入起点算错时真正要看的两个数）。
                _ = VisibilityWin32Api.SetWindowPos(
                    _hwnd, VisibilityWin32Api.HWND_TOPMOST,
                    newX, targetY, width, height,
                    VisibilityWin32Api.SWP_NOACTIVATE | VisibilityWin32Api.SWP_NOOWNERZORDER | VisibilityWin32Api.SWP_SHOWWINDOW);

                if (progress >= 1.0)
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
                    _state.CurrentX = targetX;
                    _state.CurrentY = targetY; // 同步 Y 坐标，确保退出时 SlideOutAsync 起点正确
                    SnapToTarget(targetX, targetY, width, height);

                    System.Diagnostics.Debug.WriteLine(
                        $"SlideInAsync done: startX={startX:F0}, targetX={targetX:F0}, lastFrameX={newX}");

                    tcs.TrySetResult(true);
                }
            };

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
            return tcs.Task;
        }

        /// <summary>
        /// 将窗口移到屏幕边缘不可见区域（不改变 Z 序，保持 TOPMOST）
        /// 根据停靠位置决定移动到左侧还是右侧屏幕外
        /// </summary>
        private void MoveToOffScreen(int y, int width, int height)
        {
            if (_hwnd == IntPtr.Zero) return;
            
            // 根据停靠位置决定屏幕外位置
            var dockSide = ExperimentalSettings.DockSide;
            int offScreenX = dockSide == WindowDockSide.Left
                ? -width  // 左侧停靠：移到屏幕左侧外
                : _state.WorkArea.Right + width; // 右侧停靠：移到屏幕右侧外
            
            _ = VisibilityWin32Api.SetWindowPos(
                _hwnd, VisibilityWin32Api.HWND_TOPMOST,
                offScreenX, y, width, height,
                VisibilityWin32Api.SWP_NOACTIVATE | VisibilityWin32Api.SWP_NOOWNERZORDER);
        }

        /// <summary>
        /// 将窗口精确对齐到目标位置（动画最后一帧，消除浮点误差）
        /// </summary>
        private void SnapToTarget(double targetX, int targetY, int width, int height)
        {
            if (_hwnd == IntPtr.Zero) return;
            _ = VisibilityWin32Api.SetWindowPos(
                _hwnd, VisibilityWin32Api.HWND_TOPMOST,
                (int)Math.Round(targetX), targetY, width, height,
                VisibilityWin32Api.SWP_NOACTIVATE | VisibilityWin32Api.SWP_NOOWNERZORDER | VisibilityWin32Api.SWP_SHOWWINDOW);
        }

        /// <summary>
        /// 应用固定模式窗口样式：移除边框/标题栏，设置 DWM 无圆角/无边框颜色
        ///
        /// 【副作用】
        /// - 修改 GWL_STYLE：移除 WS_OVERLAPPEDWINDOW，添加 WS_POPUP
        /// - 修改 GWL_EXSTYLE：移除所有边框扩展样式
        /// - 调用 DwmExtendFrameIntoClientArea（margins 全为 0）
        /// - 调用 DwmSetWindowAttribute 设置圆角/边框/标题栏颜色
        /// - 调用 RefreshWindowFrame 使样式立即生效
        /// </summary>
        public void ApplyPinnedWindowStyle()
        {
            if (_hwnd == IntPtr.Zero) return; // 句柄无效时提前返回，避免 Win32 调用崩溃

            // 通过 HWND 获取 WinUI 3 的 AppWindow，用于配置 Presenter 属性
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            // 配置 OverlappedPresenter：禁用边框、标题栏、调整大小、最大化、最小化，并置顶
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false); // 移除系统边框和标题栏
                presenter.IsResizable = false;      // 禁止用户拖拽调整窗口大小
                presenter.IsAlwaysOnTop = true;     // 窗口始终显示在其他窗口之上
                presenter.IsMaximizable = false;    // 禁用最大化按钮
                presenter.IsMinimizable = false;    // 禁用最小化按钮
            }

            // 读取当前窗口样式，用于捕获基础值和后续修改
            IntPtr currentStyle = AppearanceWin32Api.GetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_STYLE);
            IntPtr currentExtendedStyle = AppearanceWin32Api.GetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_EXSTYLE);

            // 首次进入固定模式时捕获基础样式，后续重复进入时跳过（保留最初的原始样式）
            if (!_hasCapturedBaseWindowStyle)
            {
                _baseWindowStyle = currentStyle;          // 保存原始 GWL_STYLE，退出时还原
                _hasCapturedBaseWindowStyle = true;
            }
            if (!_hasCapturedBaseExtendedWindowStyle)
            {
                _baseExtendedWindowStyle = currentExtendedStyle; // 保存原始 GWL_EXSTYLE，退出时还原
                _hasCapturedBaseExtendedWindowStyle = true;
            }

            // 修改 GWL_STYLE：逐位清除边框/标题栏相关标志，添加 POPUP + VISIBLE
            int style = currentStyle.ToInt32();
            style &= ~AppearanceWin32Api.WS_OVERLAPPEDWINDOW; // 移除标准重叠窗口样式（含标题栏、边框等）
            style &= ~AppearanceWin32Api.WS_CAPTION;          // 移除标题栏
            style &= ~AppearanceWin32Api.WS_SYSMENU;          // 移除系统菜单（右键标题栏菜单）
            style &= ~AppearanceWin32Api.WS_THICKFRAME;       // 移除可调整大小的粗边框
            style &= ~AppearanceWin32Api.WS_BORDER;           // 移除细边框
            style &= ~AppearanceWin32Api.WS_DLGFRAME;         // 移除对话框风格边框
            style |= AppearanceWin32Api.WS_POPUP;             // 添加弹出窗口样式（无边框）
            style |= AppearanceWin32Api.WS_VISIBLE;           // 确保窗口保持可见

            // 修改 GWL_EXSTYLE：移除所有视觉边框扩展样式
            int extendedStyle = currentExtendedStyle.ToInt32();
            extendedStyle &= ~AppearanceWin32Api.WS_EX_DLGMODALFRAME; // 移除对话框模态边框
            extendedStyle &= ~AppearanceWin32Api.WS_EX_WINDOWEDGE;    // 移除凸起边缘效果
            extendedStyle &= ~AppearanceWin32Api.WS_EX_CLIENTEDGE;    // 移除凹陷客户区边缘
            extendedStyle &= ~AppearanceWin32Api.WS_EX_STATICEDGE;    // 移除静态边缘效果

            // 将修改后的样式写回窗口（此时窗口外观尚未刷新，需后续调用 RefreshWindowFrame）
            _ = AppearanceWin32Api.SetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_STYLE, new IntPtr(style));
            _ = AppearanceWin32Api.SetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_EXSTYLE, new IntPtr(extendedStyle));

            // DWM：将框架延伸到客户区，margins 全为 0 表示消除系统绘制的边框视觉残留
            AppearanceWin32Api.MARGINS margins = new() { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
            _ = AppearanceWin32Api.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            // DWM：禁用窗口圆角（固定模式需要直角对齐屏幕边缘）
            int cornerPreference = AppearanceWin32Api.DWMWCP_DONOTROUND;
            _ = AppearanceWin32Api.DwmSetWindowAttribute(
                _hwnd,
                AppearanceWin32Api.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                sizeof(int));

            // DWM：移除系统绘制的窗口边框颜色（避免出现意外的彩色边框线）
            int borderColor = AppearanceWin32Api.DWMWA_COLOR_NONE;
            _ = AppearanceWin32Api.DwmSetWindowAttribute(
                _hwnd,
                AppearanceWin32Api.DWMWA_BORDER_COLOR,
                ref borderColor,
                sizeof(int));

            // DWM：将标题栏颜色设为几乎透明（Alpha=1），让 Mica 背景材质透过显示
            int captionColor = 0x01000000; // ARGB: Alpha=1, R=0, G=0, B=0（近似透明）
            _ = AppearanceWin32Api.DwmSetWindowAttribute(
                _hwnd,
                AppearanceWin32Api.DWMWA_CAPTION_COLOR,
                ref captionColor,
                sizeof(int));

            // 通知系统刷新窗口框架，使上述所有样式修改立即生效
            RefreshWindowFrame();

            System.Diagnostics.Debug.WriteLine("PinnedModeController.ApplyPinnedWindowStyle: All styles and DWM attributes applied");
        }




        /// <summary>
        /// 查询固定模式边界：注册 AppBar 并查询系统批准的位置，将结果写入 _state
        ///
        /// 【设计说明】
        /// 将原 ApplyPinnedBounds 拆为两步，以支持"先滑入、再推开"的动画流程：
        /// 1. QueryPinnedBounds()  — 得到终点坐标，不移动窗口（供滑入动画使用）
        /// 2. CommitPinnedBounds() — 正式提交 ABM_SETPOS，触发系统推开其他窗口的动画
        ///
        /// 【副作用】
        /// - 注册 AppBar（SHAppBarMessage ABM_NEW，幂等）
        /// - 查询 AppBar 位置（ABM_QUERYPOS）
        /// - 将系统批准的位置和尺寸写入 _state.TargetX/Y、WindowWidth/Height
        /// - 不移动窗口，不调用 SetWindowPos
        /// - 根据用户设置的停靠位置（左侧/右侧）计算 AppBar 边缘
        /// </summary>
        public void QueryPinnedBounds()
        {
            RegisterAppBarIfNeeded(); // 确保 AppBar 已注册（幂等，重复调用安全）

            VisibilityWin32Api.APPBARDATA appBarData = CreateAppBarData();
            int desiredWidth = _state.WindowWidth;

            // 根据停靠位置设置 AppBar 边缘和矩形
            var dockSide = ExperimentalSettings.DockSide;
            if (dockSide == WindowDockSide.Left)
            {
                // 左侧停靠
                appBarData.uEdge = VisibilityWin32Api.ABE_LEFT;
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Left = _state.WorkArea.Left;
                appBarData.rc.Right = appBarData.rc.Left + desiredWidth;
            }
            else
            {
                // 右侧停靠
                appBarData.uEdge = VisibilityWin32Api.ABE_RIGHT;
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Right = _state.WorkArea.Right;
                appBarData.rc.Left = appBarData.rc.Right - desiredWidth;
            }

            // ABM_QUERYPOS：向系统询问可用位置（不占用屏幕空间，不触发推开动画）
            _ = VisibilityWin32Api.SHAppBarMessage(VisibilityWin32Api.ABM_QUERYPOS, ref appBarData);

            // 强制使用期望值（系统查询可能修改 rc，此处覆盖回来）
            if (dockSide == WindowDockSide.Left)
            {
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Left = _state.WorkArea.Left;
                appBarData.rc.Right = appBarData.rc.Left + desiredWidth;
            }
            else
            {
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Right = _state.WorkArea.Right;
                appBarData.rc.Left = appBarData.rc.Right - desiredWidth;
            }

            // 将查询结果写入布局状态，供滑入动画使用
            _state.WindowWidth  = Math.Max(_state.MinWindowWidth, appBarData.rc.Right - appBarData.rc.Left);
            _state.WindowHeight = Math.Max(1, appBarData.rc.Bottom - appBarData.rc.Top);
            _state.TargetX      = appBarData.rc.Left;
            _state.TargetY      = appBarData.rc.Top;
            // CurrentX/Y 保持屏幕外，由滑入动画负责更新
        }

        /// <summary>
        /// 提交固定模式边界：正式向系统注册 AppBar 位置，触发系统推开其他窗口的动画
        ///
        /// 【前置条件】
        /// 必须在 QueryPinnedBounds() 和滑入动画完成之后调用
        /// 此时窗口已在目标位置，系统推开动画与窗口位置一致
        ///
        /// 【副作用】
        /// - 提交 AppBar 位置（ABM_SETPOS，触发系统推开动画）
        /// - 更新 _state 中的最终位置和尺寸
        /// - 不移动窗口（滑入动画已将窗口放到正确位置）
        /// - 根据用户设置的停靠位置（左侧/右侧）提交 AppBar
        /// </summary>
        public void CommitPinnedBounds()
        {
            VisibilityWin32Api.APPBARDATA appBarData = CreateAppBarData();
            int desiredWidth = _state.WindowWidth;

            // 根据停靠位置设置 AppBar 边缘和矩形
            var dockSide = ExperimentalSettings.DockSide;
            if (dockSide == WindowDockSide.Left)
            {
                // 左侧停靠
                appBarData.uEdge = VisibilityWin32Api.ABE_LEFT;
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Left = _state.WorkArea.Left;
                appBarData.rc.Right = appBarData.rc.Left + desiredWidth;
            }
            else
            {
                // 右侧停靠
                appBarData.uEdge = VisibilityWin32Api.ABE_RIGHT;
                appBarData.rc.Top = _state.WorkArea.Top;
                appBarData.rc.Bottom = _state.WorkArea.Bottom;
                appBarData.rc.Right = _state.WorkArea.Right;
                appBarData.rc.Left = appBarData.rc.Right - desiredWidth;
            }

            // ABM_SETPOS：正式占用屏幕边缘空间，系统会推开其他窗口（自带动画）
            // 不再调用 ApplyPinnedWindowFrame，避免在滑入动画结束后再次移动窗口造成闪烁
            _ = VisibilityWin32Api.SHAppBarMessage(VisibilityWin32Api.ABM_SETPOS, ref appBarData);

            // 更新最终尺寸（ABM_SETPOS 后系统可能微调 rc）
            _state.WindowWidth  = Math.Max(_state.MinWindowWidth, appBarData.rc.Right - appBarData.rc.Left);
            _state.WindowHeight = Math.Max(1, appBarData.rc.Bottom - appBarData.rc.Top);
            _state.TargetX      = appBarData.rc.Left;
            _state.TargetY      = appBarData.rc.Top;
        }

        /// <summary>
        /// 应用固定模式边界（兼容旧调用，如 OnAppWindowChanged 中的重新对齐）
        /// 直接执行 QueryPinnedBounds + CommitPinnedBounds，不经过动画
        /// </summary>
        public void ApplyPinnedBounds()
        {
            QueryPinnedBounds();
            CommitPinnedBounds();
        }

        // ==================== 退出固定模式 ====================

        /// <summary>
        /// 退出固定模式：注销 AppBar 并还原标准窗口样式
        ///
        /// 【调用顺序】
        /// 必须先调用 RemoveAppBar，再调用 RestoreStandardWindowStyle
        /// 确保屏幕空间在样式还原前已释放
        /// </summary>
        public void ExitPinnedMode()
        {
            RemoveAppBar();                 // 第一步：注销 AppBar，释放屏幕右侧保留区域
            RestoreStandardWindowStyle();   // 第二步：还原窗口边框和标题栏样式
        }

        /// <summary>
        /// 注销 AppBar，释放屏幕右侧占用的空间
        /// 幂等操作：未注册时调用无副作用
        /// </summary>
        public void RemoveAppBar()
        {
            if (!_isAppBarRegistered) return; // 未注册则直接返回，避免重复注销

            VisibilityWin32Api.APPBARDATA appBarData = CreateAppBarData();
            _ = VisibilityWin32Api.SHAppBarMessage(VisibilityWin32Api.ABM_REMOVE, ref appBarData); // 向系统注销 AppBar
            _isAppBarRegistered = false; // 更新注册状态标志
        }

        /// <summary>
        /// 还原标准窗口样式（进入固定模式前捕获的基础样式）
        /// 幂等操作：未捕获基础样式时调用无副作用
        /// </summary>
        public void RestoreStandardWindowStyle()
        {
            if (_hwnd == IntPtr.Zero || !_hasCapturedBaseWindowStyle) return; // 句柄无效或未捕获基础样式时跳过

            // 还原 GWL_STYLE 为进入固定模式前的原始值
            _ = AppearanceWin32Api.SetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_STYLE, _baseWindowStyle);

            // 还原 GWL_EXSTYLE（仅在已捕获的情况下还原）
            if (_hasCapturedBaseExtendedWindowStyle)
            {
                _ = AppearanceWin32Api.SetWindowLongPtr(_hwnd, AppearanceWin32Api.GWL_EXSTYLE, _baseExtendedWindowStyle);
            }

            // 刷新窗口框架，使样式还原立即生效
            RefreshWindowFrame();
        }

        // ==================== 内部辅助方法 ====================

        /// <summary>
        /// 注册 AppBar（幂等：已注册时直接返回）
        /// </summary>
        private void RegisterAppBarIfNeeded()
        {
            if (_isAppBarRegistered) return; // 已注册则跳过，避免重复注册

            VisibilityWin32Api.APPBARDATA appBarData = CreateAppBarData();
            _ = VisibilityWin32Api.SHAppBarMessage(VisibilityWin32Api.ABM_NEW, ref appBarData); // 向系统注册新 AppBar
            _isAppBarRegistered = true; // 标记为已注册
        }

        /// <summary>
        /// 创建并初始化 APPBARDATA 结构体（填充必要字段）
        /// </summary>
        private VisibilityWin32Api.APPBARDATA CreateAppBarData()
        {
            return new VisibilityWin32Api.APPBARDATA
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VisibilityWin32Api.APPBARDATA>(), // 结构体大小（Win32 要求）
                hWnd = _hwnd,                       // 关联的窗口句柄
                uCallbackMessage = _appBarMessageId // AppBar 状态变化时系统发送的回调消息 ID
            };
        }

        /// <summary>
        /// 精确对齐固定模式窗口位置，补偿 DWM 扩展边框偏移
        /// </summary>
        private void ApplyPinnedWindowFrame(PlacementWin32Api.RECT approvedRect)
        {
            if (_hwnd == IntPtr.Zero) return; // 句柄无效时跳过

            // 第一次移动：直接应用 AppBar 批准的矩形
            ApplyWindowRect(approvedRect);

            // 读取 DWM 实际渲染的扩展边框矩形（可能因 DWM 阴影/边框偏移与期望值不同）
            if (TryGetExtendedFrameBounds(out PlacementWin32Api.RECT actualBounds))
            {
                // 计算偏差并修正：期望位置 + (期望 - 实际) = 补偿后的位置
                PlacementWin32Api.RECT correctedRect = new()
                {
                    Left   = approvedRect.Left   + (approvedRect.Left   - actualBounds.Left),   // 补偿左侧偏移
                    Top    = approvedRect.Top    + (approvedRect.Top    - actualBounds.Top),    // 补偿顶部偏移
                    Right  = approvedRect.Right  + (approvedRect.Right  - actualBounds.Right),  // 补偿右侧偏移
                    Bottom = approvedRect.Bottom + (approvedRect.Bottom - actualBounds.Bottom)  // 补偿底部偏移
                };

                // 第二次移动：应用补偿后的矩形，使 DWM 实际渲染区域精确对齐 AppBar 位置
                ApplyWindowRect(correctedRect);
                approvedRect = correctedRect; // 更新为最终实际使用的矩形
            }

            // 将最终位置和尺寸写回布局状态
            _state.TargetX = approvedRect.Left;
            _state.TargetY = approvedRect.Top;
            _state.CurrentX = approvedRect.Left;
            _state.CurrentY = approvedRect.Top;
            _state.WindowWidth  = Math.Max(_state.MinWindowWidth, approvedRect.Right  - approvedRect.Left);
            _state.WindowHeight = Math.Max(1,                     approvedRect.Bottom - approvedRect.Top);
        }

        /// <summary>
        /// 调用 SetWindowPos 将窗口移动到指定矩形，并置顶显示
        /// </summary>
        private void ApplyWindowRect(PlacementWin32Api.RECT rect)
        {
            // 确保宽高不低于最小值
            int width  = Math.Max(_state.MinWindowWidth, rect.Right  - rect.Left);
            int height = Math.Max(1,                     rect.Bottom - rect.Top);

            _ = VisibilityWin32Api.SetWindowPos(
                _hwnd,
                VisibilityWin32Api.HWND_TOPMOST, // 插入到所有非置顶窗口之上（TOPMOST）
                rect.Left,                        // 窗口左上角 X 坐标
                rect.Top,                         // 窗口左上角 Y 坐标
                width,                            // 窗口宽度
                height,                           // 窗口高度
                VisibilityWin32Api.SWP_SHOWWINDOW); // 同时显示窗口
        }

        /// <summary>
        /// 尝试获取 DWM 扩展边框矩形（DWMWA_EXTENDED_FRAME_BOUNDS）
        /// 成功返回 true 并输出实际渲染区域，失败返回 false
        /// </summary>
        private bool TryGetExtendedFrameBounds(out PlacementWin32Api.RECT bounds)
        {
            if (_hwnd == IntPtr.Zero)
            {
                bounds = default; // 句柄无效，输出默认值
                return false;
            }

            // 调用 DwmGetWindowAttribute 获取 DWM 实际渲染的窗口边框矩形
            int hr = AppearanceWin32Api.DwmGetWindowAttribute(
                _hwnd,
                AppearanceWin32Api.DWMWA_EXTENDED_FRAME_BOUNDS, // 属性：扩展边框矩形
                out bounds,
                System.Runtime.InteropServices.Marshal.SizeOf<PlacementWin32Api.RECT>()); // 输出缓冲区大小

            return hr >= 0; // HRESULT >= 0 表示成功（S_OK 或其他成功码）
        }

        /// <summary>
        /// 刷新窗口框架：通知系统重新计算并绘制窗口非客户区
        /// 使用 SWP_FRAMECHANGED 标志，不移动、不调整大小、不改变 Z 序
        /// </summary>
        private void RefreshWindowFrame()
        {
            if (_hwnd == IntPtr.Zero) return; // 句柄无效时跳过

            _ = VisibilityWin32Api.SetWindowPos(
                _hwnd,
                IntPtr.Zero,  // hWndInsertAfter：忽略（SWP_NOZORDER 已设置）
                0, 0, 0, 0,   // 位置和尺寸：忽略（SWP_NOSIZE + SWP_NOMOVE 已设置）
                VisibilityWin32Api.SWP_NOSIZE     |  // 不改变窗口大小
                VisibilityWin32Api.SWP_NOMOVE     |  // 不改变窗口位置
                VisibilityWin32Api.SWP_NOZORDER   |  // 不改变 Z 序
                VisibilityWin32Api.SWP_NOACTIVATE |  // 不激活窗口（避免焦点切换）
                VisibilityWin32Api.SWP_FRAMECHANGED); // 触发 WM_NCCALCSIZE，强制重绘非客户区
        }
    }
}
