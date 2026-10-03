using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace DockedTools.Features.Tray
{
    public partial class SystemTrayIcon : IDisposable
    {
        private const uint WM_APP = 0x8000;
        private const uint TRAY_CALLBACK = WM_APP + 100;
        private const uint WM_CONTEXTMENU = 0x007B;  // 🛠️ 添加 WM_CONTEXTMENU
        
        // 🔧 Subclass ID 常量，避免魔法数字
        private const nuint SUBCLASS_ID = 102;

        // 🎯 根据应用包标识动态生成托盘图标 GUID
        // 这样开发版和正式版使用不同的 GUID，不会相互冲突
        private static readonly Guid TRAY_ICON_GUID = GenerateTrayIconGuid();

        /// <summary>
        /// 重启交接中：置位后 <see cref="Dispose()"/> 不再下发 NIM_DELETE。
        ///
        /// 【为什么需要它】
        /// TRAY_ICON_GUID 是按包名哈希出来的，同一个包的**所有实例算出来完全相同**。
        /// NIF_GUID 模式下 explorer 用 GUID 而非 hWnd 标识图标 —— 换句话说它认图标不认进程。
        /// 于是重启时序撞车：
        ///   t=0      旧实例 Launch 唤起新实例
        ///   t≈400ms  新实例 AddToTray（NIM_ADD）→ 图标出现
        ///   t=500ms  旧实例退出 → Dispose → NIM_DELETE 同一个 GUID → 把接班人的图标删了 💥
        /// 旧实例卡得越久撞上的概率越高，表现为「有时候图标会丢」。
        ///
        /// 【为什么可以直接跳过删除】
        /// 进程退出后 explorer 会检测到 hWnd 失效并自行回收该进程的图标登记，
        /// 本来也不需要手动删。只有「本进程继续存活但要收起图标」的场景才必须 NIM_DELETE。
        ///
        /// 【注意】这是进程内标志，不需要跨进程同步 —— 要保护的正是本进程退出这一步。
        /// </summary>
        public static bool IsHandingOffToSuccessor { get; set; }

        private static Guid GenerateTrayIconGuid()
        {
            try
            {
                // 尝试获取打包应用的包标识
                var package = Windows.ApplicationModel.Package.Current;
                var packageName = package.Id.Name;
                
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Package name: {packageName}");
                
                // ✅ Native AOT 兼容：使用 SHA256.HashData 静态方法
                // .NET 5+ 引入的静态方法，AOT 友好且性能更好
                // 参考：https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1850
                var hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(packageName));
                
                // 取前 16 字节作为 GUID
                var guidBytes = new byte[16];
                Array.Copy(hash, guidBytes, 16);
                
                var guid = new Guid(guidBytes);
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Generated GUID: {guid}");
                
                return guid;
            }
            catch (Exception ex)
            {
                // 如果获取失败（例如未打包应用），使用固定 GUID
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Failed to get package identity, using fallback GUID: {ex}");
                return new Guid("A5B8C3D4-E6F7-4891-A2B3-C4D5E6F78901");
            }
        }

        private readonly Window _hiddenWindow;
        private readonly uint _iconId;
        private IntPtr _hWnd;
        private bool _isVisible;
        private string _tooltip = "";
        private bool _disposed;
        private IntPtr _hIcon;
        private GCHandle _gcHandle;
        private SUBCLASSPROC _subclassDelegate;

        public SystemTrayIcon(uint trayIconId, string iconPath, string tooltip)
        {
            _iconId = trayIconId;
            _tooltip = tooltip ?? "";

            _hiddenWindow = new Window();
            _hiddenWindow.Content = new Microsoft.UI.Xaml.Controls.Grid();
            _hiddenWindow.AppWindow.IsShownInSwitchers = false;
            // 🔧 不设置 IsAlwaysOnTop，让窗口保持普通 Z 轴层级
            _hWnd = WindowNative.GetWindowHandle(_hiddenWindow);

            // 🔧 设置窗口样式：WS_POPUP + WS_EX_TRANSPARENT + WS_EX_LAYERED
            // 这样窗口完全透明且不接收鼠标事件
            SetWindowLongPtr(_hWnd, GWL_STYLE, unchecked((IntPtr)WS_POPUP));
            SetWindowLongPtr(_hWnd, GWL_EXSTYLE, unchecked((IntPtr)(WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW)));
            
            // 🔧 设置窗口完全透明（alpha = 0）
            SetLayeredWindowAttributes(_hWnd, 0, 0, LWA_ALPHA);

            // 🔧 使用强引用确保 native callback 存活期间托管对象不被 GC 回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            _subclassDelegate = WndProc;
            var fnPtr = Marshal.GetFunctionPointerForDelegate(_subclassDelegate);
            SetWindowSubclass(_hWnd, fnPtr, SUBCLASS_ID, (nuint)GCHandle.ToIntPtr(_gcHandle));

            SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER);
            LoadIcon(iconPath);
        }

        private void LoadIcon(string iconPath)
        {
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] LoadIcon: path={iconPath}");
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] File exists: {System.IO.File.Exists(iconPath)}");
            
            var p = Marshal.StringToHGlobalUni(iconPath);
            try
            {
                var dpi = GetDpiForWindow(_hWnd);
                int size = (int)(dpi / 6d);
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] DPI={dpi}, icon size={size}");
                
                _hIcon = LoadImage(IntPtr.Zero, p, 1, size, size, 0x0010);
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] LoadImage returned: 0x{_hIcon.ToInt64():X}");
                
                if (_hIcon == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] LoadImage FAILED, Win32Error={error}");
                    throw new ArgumentException($"Failed to load icon from {iconPath}, Win32Error={error}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        ~SystemTrayIcon() { Dispose(false); }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            // 🔧 按正确顺序清理资源，每个步骤都有 try-catch 保护
            
            // 1️⃣ 从托盘删除图标
            //    交接闸门的判定在 RemoveFromTray() 内部统一处理（那里是所有删除路径的
            //    唯一汇聚点），这里只需照常调用。
            try
            {
                if (_isVisible)
                {
                    RemoveFromTray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Error removing tray icon: {ex}");
            }

            // 2️⃣ 移除 window subclass
            try
            {
                if (_hWnd != IntPtr.Zero && _subclassDelegate != null)
                {
                    var fnPtr = Marshal.GetFunctionPointerForDelegate(_subclassDelegate);
                    RemoveWindowSubclass(_hWnd, fnPtr, SUBCLASS_ID);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Error removing window subclass: {ex}");
            }

            // 3️⃣ 关闭隐藏窗口
            if (disposing)
            {
                try
                {
                    _hiddenWindow?.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Error closing hidden window: {ex}");
                }
            }

            // 4️⃣ 销毁 icon
            try
            {
                if (_hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Error destroying icon: {ex}");
            }

            // 5️⃣ 释放 GCHandle
            try
            {
                if (_gcHandle.IsAllocated)
                {
                    _gcHandle.Free();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Error freeing GCHandle: {ex}");
            }
        }

        public uint TrayIconId => _iconId;

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SystemTrayIcon));
                _isVisible = value;
                if (_isVisible) AddToTray();
                else RemoveFromTray();
            }
        }

        public string Tooltip
        {
            get => _tooltip;
            set
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SystemTrayIcon));
                _tooltip = value ?? "";
                if (_isVisible) UpdateTray(NIM_MODIFY);
            }
        }

        public event TypedEventHandler<SystemTrayIcon, SystemTrayIconEventArgs>? LeftClick;
        public event TypedEventHandler<SystemTrayIcon, SystemTrayIconEventArgs>? RightClick;

        private void AddToTray()
        {
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] AddToTray: iconId={_iconId}, hIcon=0x{_hIcon.ToInt64():X}");
            
            // 🔧 首先尝试删除可能存在的僵尸图标（开发模式下常见问题）
            var deleteData = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _iconId,
                uFlags = NIF_GUID,
                guidItem = TRAY_ICON_GUID
            };
            Shell_NotifyIconW(NIM_DELETE, ref deleteData);
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Pre-cleanup: attempted to delete any existing tray icon");
            
            var data = CreateNotifyIconData(NIM_ADD);
            global::DockedTools.Features.Shared.AotOptimization.AotDebugLogger.Log(
                $"[Tray] NIM_ADD 入参: uFlags=0x{data.uFlags:X} (NIF_TIP={(data.uFlags & NIF_TIP) != 0}, NIF_SHOWTIP={(data.uFlags & NIF_SHOWTIP) != 0}), " +
                $"cbSize={data.cbSize}, uID={data.uID}, guidItem={data.guidItem}, hWnd=0x{_hWnd.ToInt64():X}, " +
                $"tipLen={_tooltip.Length}, tip=「{_tooltip}」, szTip[0..3]={data.szTip[0]},{data.szTip[1]},{data.szTip[2]}");

            bool addResult = Shell_NotifyIconW(NIM_ADD, ref data);
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_ADD) returned: {addResult}");
            if (!addResult)
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_ADD) FAILED, Win32Error={error} (0x{error:X})");
                
                // 🔧 如果失败，尝试使用 MODIFY 而不是 ADD（图标可能已存在）
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Retrying with NIM_MODIFY...");
                bool modifyResult = Shell_NotifyIconW(NIM_MODIFY, ref data);
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_MODIFY) returned: {modifyResult}");
                
                if (!modifyResult)
                {
                    int modifyError = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_MODIFY) also FAILED, Win32Error={modifyError} (0x{modifyError:X})");
                    System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] WARNING: Tray icon may not be visible!");
                }
            }
            
            bool versionResult = Shell_NotifyIconW(NIM_SETVERSION, ref data);
            global::DockedTools.Features.Shared.AotOptimization.AotDebugLogger.Log(
                $"[Tray] NIM_ADD={addResult}, NIM_SETVERSION={versionResult}, lastError={Marshal.GetLastWin32Error()}");
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_SETVERSION) returned: {versionResult}");
            if (!versionResult)
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Shell_NotifyIconW(NIM_SETVERSION) FAILED, Win32Error={error} (0x{error:X})");
            }

            // 🔧 切到 NOTIFYICON_VERSION_4 之后再补一次 NIM_MODIFY 重设 tooltip。
            //    uVersion=4 会重写图标的通知通道，SETVERSION 携带的 NIF_TIP/NIF_SHOWTIP 不一定被保留，
            //    实测少了这一步 explorer 侧就不出悬停文本。MODIFY 不带 NIF_MESSAGE / uVersion，
            //    只下发图标与提示，不会把已设好的版本冲掉。
            var modifyData = CreateNotifyIconData(NIM_MODIFY);
            bool tipResult = Shell_NotifyIconW(NIM_MODIFY, ref modifyData);
            global::DockedTools.Features.Shared.AotOptimization.AotDebugLogger.Log(
                $"[Tray] NIM_MODIFY(重设tooltip)={tipResult}, uFlags=0x{modifyData.uFlags:X}, " +
                $"NIF_TIP={(modifyData.uFlags & NIF_TIP) != 0}, NIF_SHOWTIP={(modifyData.uFlags & NIF_SHOWTIP) != 0}, " +
                $"cbSize={modifyData.cbSize}, lastError={Marshal.GetLastWin32Error()}");
        }

        private void RemoveFromTray()
        {
            // ⚠️ 重启交接闸门。闸门必须落在这里 —— 本方法是所有 NIM_DELETE 的唯一汇聚点：
            //   · IsVisible = false  → RemoveFromTray()
            //   · Dispose(bool)      → RemoveFromTray()
            // 挡在调用侧会漏掉前者，而真实调用链走的就是前者：
            // 托盘图标管理器.Dispose() 先 icon.IsVisible = false（这一步已经把图标删了），
            // 之后 icon.Dispose() 里 _isVisible 已是 false，反而不会再删。
            if (IsHandingOffToSuccessor)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SystemTrayIcon] 处于重启交接流程，跳过 NIM_DELETE" +
                    "（图标已由新实例接管，本进程退出后 explorer 自行回收登记）");
                return;
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _iconId,
                uFlags = NIF_GUID,  // 🎯 使用 GUID 标识要删除的图标
                guidItem = TRAY_ICON_GUID  // 🎯 设置固定的 GUID
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }

        private void UpdateTray(uint message)
        {
            var data = CreateNotifyIconData(message);
            Shell_NotifyIconW(message, ref data);
        }

        private NOTIFYICONDATAW CreateNotifyIconData(uint message)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = _iconId,
                uFlags = NIF_ICON | NIF_GUID,  // 🎯 添加 NIF_GUID 标志
                hIcon = _hIcon,
                guidItem = TRAY_ICON_GUID,  // 🎯 设置固定的 GUID
                szTip = new ushort[128],
                szInfo = new ushort[256],
                szInfoTitle = new ushort[64],
            };

            var tip = _tooltip;
            if (!string.IsNullOrEmpty(tip))
            {
                data.uFlags |= NIF_TIP | NIF_SHOWTIP;
                // 限制最大长度为 127，留一个位置给 null 终止符
                int maxLength = Math.Min(tip.Length, 127);
                for (int i = 0; i < maxLength; i++)
                    data.szTip[i] = (ushort)tip[i];
                // 添加 null 终止符
                data.szTip[maxLength] = 0;
            }

            if (message == NIM_ADD || message == NIM_SETVERSION)
            {
                data.uFlags |= NIF_MESSAGE;
                data.uCallbackMessage = TRAY_CALLBACK;
            }

            if (message == NIM_ADD || message == NIM_SETVERSION)
                data.VersionOrTimeout = 4;

            return data;
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
        {
            // 🔧 P1: 检查对象是否已释放，避免 native callback 访问已释放对象
            if (_disposed)
            {
                System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] WndProc called after dispose, returning default handling");
                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }

            try
            {
                if (uMsg == WM_GETMINMAXINFO)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize = new POINT(0, 0);
                    Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }

                if (uMsg == TRAY_CALLBACK)
                {
                    var msg = (uint)(lParam.ToInt32() & 0xffff);
                    var args = new SystemTrayIconEventArgs();

                    // 🔍 检测输入设备类型
                    args.InputDevice = DetectInputDeviceType();

                    switch (msg)
                    {
                        case WM_LBUTTONUP:  // 🛠️ 改为 UP，在释放时触发
                            LeftClick?.Invoke(this, args);
                            break;
                        case WM_CONTEXTMENU:  // 🛠️ 使用 WM_CONTEXTMENU 而不是 WM_RBUTTONDOWN
                            RightClick?.Invoke(this, args);
                            break;
                        case NIN_POPUPOPEN:
                        case NIN_POPUPCLOSE:
                            // 🔍 仅观察，且**带 NIF_SHOWTIP 时这里永远收不到是正常现象**：
                            //    NIF_SHOWTIP 表示回退到 explorer 自绘的标准 tooltip，它自己画完就完事，不通知应用；
                            //    只有去掉 NIF_SHOWTIP、改由应用自绘弹出 UI 时，explorer 才会下发这两个通知。
                            //    （2026-10-03 实测：修好 cbSize 后 tooltip 正常显示，此处仍为 0 条。）
                            global::DockedTools.Features.Shared.AotOptimization.AotDebugLogger.Log(
                                $"[Tray] 收到 {(msg == NIN_POPUPOPEN ? "NIN_POPUPOPEN" : "NIN_POPUPCLOSE")}: " +
                                $"x={(short)(wParam.ToInt64() & 0xffff)}, y={(short)((wParam.ToInt64() >> 16) & 0xffff)}");
                            break;
                    }

                    if (args.Flyout != null)
                        ShowFlyout(args.Flyout);
                }

                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }
            catch (Exception ex)
            {
                // 🔧 P2: 记录完整异常信息但不重新抛出，避免从 native callback 抛出导致崩溃
                System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Exception in WndProc: {ex}");
                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }
        }

        /// <summary>
        /// 检测当前输入设备类型
        /// </summary>
        /// <returns>输入设备类型</returns>
        private InputDeviceType DetectInputDeviceType()
        {
            // 🔍 检查 GetMessageExtraInfo 的触摸标志
            var extraInfo = GetMessageExtraInfo();
            const long MOUSEEVENTF_FROMTOUCH = 0xFF515700;
            const long SIGNATURE_MASK = 0xFFFFFF00;
            var isFromTouch = (extraInfo.ToInt64() & SIGNATURE_MASK) == MOUSEEVENTF_FROMTOUCH;
            
            // 🔍 检查系统是否有鼠标
            var mousePresent = GetSystemMetrics(SM_MOUSEPRESENT);
            
            System.Diagnostics.Debug.WriteLine($"[SystemTrayIcon] Input detection: MousePresent={mousePresent}, FromTouch={isFromTouch}, ExtraInfo=0x{extraInfo.ToInt64():X}");
            
            // 🎯 简化决策逻辑：默认鼠标，只有明确检测到触摸才用触摸模式
            if (isFromTouch)
            {
                System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] ✅ Detected: Touch (from message extra info)");
                return InputDeviceType.Touch;
            }
            
            // 默认返回鼠标类型（即使是触摸设备，如果有鼠标也优先使用鼠标模式）
            System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] ✅ Detected: Mouse (default)");
            return InputDeviceType.Mouse;
        }

        private void ShowFlyout(FlyoutBase flyout)
        {
            flyout.ShouldConstrainToRootBounds = false;
            var grid = (Microsoft.UI.Xaml.Controls.Grid)_hiddenWindow.Content;
            grid.ContextFlyout = flyout;

            flyout.Closed += OnFlyoutClosed;
            
            // 🔧 关键修复：设置 MenuFlyout 的 Presenter 样式来调整阴影偏移
            // 使用 -8px 的负边距来微调菜单位置
            // 🔧 AOT 兼容：使用 as + null 检查代替 is 模式匹配
            var menuFlyout = flyout as MenuFlyout;
            if (menuFlyout != null)
            {
                var presenterStyle = new Microsoft.UI.Xaml.Style(typeof(Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter));
                
                // 设置 -8px 的负上边距来微调位置
                presenterStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                    Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter.MarginProperty,
                    new Microsoft.UI.Xaml.Thickness(0, -8, 0, 0)));
                
                menuFlyout.MenuFlyoutPresenterStyle = presenterStyle;
                
                System.Diagnostics.Debug.WriteLine("[ShowFlyout] Applied MenuFlyoutPresenter style with Margin(0, -8, 0, 0)");
            }
            
            // 🔧 关键修复：在显示菜单前模拟鼠标移动，强制 WinUI 切换到鼠标模式
            // 获取当前鼠标位置
            if (GetCursorPos(out var cursorPos))
            {
                // 模拟一个微小的鼠标移动（移动1像素再移回）
                // 这会触发 WinUI 的输入模式检测，切换到鼠标布局
                SetCursorPos(cursorPos.X + 1, cursorPos.Y);
                SetCursorPos(cursorPos.X, cursorPos.Y);
            }
            
            // 🛠️ 关键修复：调整顺序，先显示窗口再设置前台
            // 删除 _hiddenWindow.Activate()，避免触发 WinUI 的 touch 输入模式初始化
            ShowWindow(_hWnd, SW_SHOW);
            
            // 🔧 设置窗口 Z 轴层级：HWND_NOTOPMOST (-2)
            // 将窗口放置在所有非置顶窗口之上，但在置顶窗口（如任务栏）之下
            SetWindowPos(_hWnd, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            
            SetForegroundWindow(_hWnd);

            var iconId = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _hWnd,
                uID = _iconId,
                guidItem = TRAY_ICON_GUID  // 🎯 设置固定的 GUID
            };

            if (Shell_NotifyIconGetRect(ref iconId, out var iconRect) == 0)
            {
                int cx = (iconRect.left + iconRect.right) / 2;
                
                // 🔧 使用 SHAppBarMessage 获取任务栏的真实位置
                APPBARDATA appBarData = new APPBARDATA
                {
                    cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
                };
                
                SHAppBarMessage(ABM_GETTASKBARPOS, ref appBarData);
                
                // 🔍 获取 DPI 缩放比例
                uint dpi = GetDpiForWindow(_hWnd);
                double dpiScale = dpi / 96.0;
                
                // 🔍 获取显示器信息来验证任务栏位置
                var monitor = MonitorFromWindow(_hWnd, MONITOR_DEFAULTTONEAREST);
                MONITORINFO monitorInfo = new MONITORINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
                };
                GetMonitorInfo(monitor, ref monitorInfo);
                
                // 任务栏的顶部（屏幕坐标）
                int taskbarTop = appBarData.rc.top;
                
                // 🔍 验证：工作区底部应该等于任务栏顶部
                int workAreaBottom = monitorInfo.rcWork.bottom;
                int screenBottom = monitorInfo.rcMonitor.bottom;
                
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] DPI: {dpi}, Scale: {dpiScale:F2}");
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] Monitor Work Area: T={monitorInfo.rcWork.top}, B={monitorInfo.rcWork.bottom}, L={monitorInfo.rcWork.left}, R={monitorInfo.rcWork.right}");
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] Monitor Full Area: T={monitorInfo.rcMonitor.top}, B={monitorInfo.rcMonitor.bottom}, L={monitorInfo.rcMonitor.left}, R={monitorInfo.rcMonitor.right}");
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] Taskbar from AppBar: T={taskbarTop}, B={appBarData.rc.bottom}, Edge={appBarData.uEdge}");
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] IconRect: L={iconRect.left}, T={iconRect.top}, R={iconRect.right}, B={iconRect.bottom}");
                
                // 🔧 使用工作区底部作为任务栏顶部（这是最可靠的方法）
                int realTaskbarTop = workAreaBottom;
                
                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] Using WorkArea.Bottom as taskbar top: {realTaskbarTop}");
                
                // 🔧 将窗口定位在任务栏顶部，水平居中在图标上方
                _hiddenWindow.AppWindow.MoveAndResize(
                    new RectInt32(cx, realTaskbarTop, 1, 1),
                    DisplayArea.GetFromPoint(new PointInt32(cx, realTaskbarTop), DisplayAreaFallback.Primary));

                System.Diagnostics.Debug.WriteLine($"[ShowFlyout] Window positioned at: X={cx}, Y={realTaskbarTop}");

                // 🔧 使用 Top placement，菜单在窗口上方展开（任务栏外）
                // Position 设置为 null 让菜单自动居中
                var showOptions = new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.Top,
                    ShowMode = FlyoutShowMode.Standard
                };
                
                flyout.ShowAt(grid, showOptions);
            }
            else
            {
                flyout.ShowAt(grid, new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.Bottom,
                    ShowMode = FlyoutShowMode.Standard  // 🔧 强制使用标准模式
                });
            }
        }

        private void OnFlyoutClosed(object? sender, object e)
        {
            // 🔧 AOT 兼容：使用 as + null 检查代替 is 模式匹配
            var flyout = sender as FlyoutBase;
            if (flyout != null)
            {
                flyout.Closed -= OnFlyoutClosed;
                ShowWindow(_hWnd, 0);
                
                // 🛠️ 经典托盘菜单修复：菜单关闭后发送 WM_NULL 消息
                // 确保焦点正确释放，避免菜单不消失或焦点异常
                PostMessage(_hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint NIM_ADD = 0;
        private const uint NIM_MODIFY = 1;
        private const uint NIM_DELETE = 2;
        private const uint NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 0x0001;
        private const uint NIF_ICON = 0x0002;
        private const uint NIF_TIP = 0x0004;
        private const uint NIF_SHOWTIP = 0x0080;
        private const uint NIF_GUID = 0x0020;  // 🎯 使用 GUID 标识图标
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;  // 🛠️ 添加 WM_LBUTTONUP
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;  // 🛠️ 添加 WM_RBUTTONUP
        private const uint WM_NULL = 0x0000;  // 🛠️ 添加 WM_NULL
        private const int SW_SHOW = 5;
        private const uint WM_GETMINMAXINFO = 0x0024;
        // NOTIFYICON_VERSION_4 专用通知：explorer 悬停/移出图标时下发。
        // 版本 4 下标准 tooltip 默认被抑制、改由应用自绘弹窗；想保留标准 tooltip 必须带 NIF_SHOWTIP。
        private const uint NIN_POPUPOPEN = 0x0406;
        private const uint NIN_POPUPCLOSE = 0x0407;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public ushort[] szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] szInfo;
            public uint VersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public ushort[] szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            // ⚠️ 不能省。缺了它 Marshal.SizeOf 就是 968（= NOTIFYICONDATAW_V3_SIZE，Win7 结构），
            //    shell 会认为调用方只支持 V3，NOTIFYICON_VERSION_4 的部分行为走不到完整路径。
            //    补上后 sizeof = 976，才是完整 V4 结构。参考 shellapi.h。
            public IntPtr hBalloonIcon;
        }

        [LibraryImport("shell32.dll")]
        private static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [LibraryImport("shell32.dll", SetLastError = true)]
        private static partial IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [LibraryImport("user32.dll")]
        private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint ABM_GETTASKBARPOS = 5;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // 🔧 x64 平台使用 SetWindowLongPtrW（Unicode 版本）
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [LibraryImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [LibraryImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, nuint uIdSubclass);

        [LibraryImport("comctl32.dll")]
        private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(IntPtr hwnd);

        // 🔧 LoadImageW 是 Unicode 版本的 API
        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyIcon(IntPtr hIcon);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetCursorPos(int X, int Y);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetShellWindow();

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetMessageExtraInfo();

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private const int SM_DIGITIZER = 94;
        private const int SM_MAXIMUMTOUCHES = 95;
        private const int SM_MOUSEPRESENT = 19;
        
        // Digitizer 标志位
        private const int NID_INTEGRATED_TOUCH = 0x01;
        private const int NID_EXTERNAL_TOUCH = 0x02;
        private const int NID_INTEGRATED_PEN = 0x04;
        private const int NID_EXTERNAL_PEN = 0x08;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);
    }

    public delegate void TypedEventHandler<TSender, TArgs>(TSender sender, TArgs args);
}
