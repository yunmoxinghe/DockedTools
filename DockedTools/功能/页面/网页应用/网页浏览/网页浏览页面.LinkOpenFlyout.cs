using DockedTools.Features.Localization;
using DockedTools.Features.Pages.Lab;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 链接打开方式询问菜单
    /// 用贴着光标的 MenuFlyout 取代原来的模态询问对话框（原生亚克力 / 圆角 / 图标 / 阴影）
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// 菜单里的一次选择
        /// </summary>
        /// <remarks>
        /// 「修改默认」不是打开方式，而是把用户送去设置页 —— 那座页面里有
        /// <c>LinkOpenBehaviorComboBox</c>（设置页面.xaml 的四个档），改默认值该在那里改，
        /// 菜单本身保持五个动作、不做二级选择。
        /// </remarks>
        private enum LinkOpenMenuAction
        {
            NewWindow,
            SystemBrowser,
            SamePage,
            CopyLink,
            EditDefault
        }

        /// <summary>
        /// 光标到菜单左上角的偏移（DIP）。
        /// MenuFlyout 的 <c>Position</c> 就是菜单左上角，贴太紧指针会正好压在第一个菜单项的边线上。
        /// </summary>
        private const double CursorOffsetInDips = 2.0;

        /// <summary>
        /// 等用户从菜单里做选择的硬上限（毫秒）。
        ///
        /// 【为什么必须有上限】本方法的唯一出口是 <c>flyout.Closed</c>。
        /// 官方口径里 <c>NewWindowRequested</c> 的 deferral 没 Complete 之前，
        /// 发起方的脚本会被一直挡着 —— 而 <see cref="HandleAskBehaviorAsync"/> 正是
        /// 在等这个方法返回才去结那个 deferral。Closed 不到的路子确实存在：
        /// Transient 弹层被 WebView2 的 HWND 抢走输入、宿主页面被拆掉、
        /// <c>item.Click</c> 里 <c>Hide()</c> 抛异常。任何一种一旦发生，
        /// 就是「这个标签页从此点了没反应」，而且没有超时替我们兜。
        ///
        /// 【为什么是 60 秒】一个「这个链接怎么打开」摆了一分钟还没人理，
        /// 用户实际上已经放弃它了；再等下去只会把页面扣得更久。
        /// 代价说清楚：超过 60 秒才点下去的话，这一按不会有任何效果
        /// （菜单会被主动关掉，本次请求按「取消」落地）。要调就改这一个常量。
        /// </summary>
        private const int AskMenuTimeoutMs = 60000;

        /// <summary>
        /// 在鼠标位置弹出「选择打开方式」菜单
        /// </summary>
        /// <returns>用户选了某一项时返回该项；轻按关闭（点别处 / 关掉页面）返回 <see langword="null"/>。</returns>
        private async Task<LinkOpenMenuAction?> ShowAskFlyoutAsync(string uri)
        {
            if (WebView is not { } anchor || anchor.XamlRoot is null)
            {
                return null;
            }

            // 【结论：只用原生 MenuFlyout，不再自己拼壳】
            // 试过的三条自绘路线都栽在同一件事上 —— 窗口化弹层的圆角 + 亚克力 + 阴影是
            // 「系统 Compositor 出的系统 Visual」那一套，靠 XAML 拼不出来：
            // docs/design-notes/popup-system-backdrop.md 开头就写明，窗口化 Popup 的
            // rounded corners / shadows / system backdrop 三者必须由同一套 Visual 树出，
            // 而且 backdrop 的 Visual 必须被「圆角 clip」裁一刀，否则方角会从圆角边框后面露出来
            // （L17-L31、L25-L26 原话）。换句话说：这三个东西只能整包由框架给，自己拼一定缺一块。
            //
            // 框架里唯一「整包给」的菜单就是 MenuFlyout：
            //   · 材质：DefaultMenuFlyoutPresenterStyle 的 SystemBackdrop = MenuFlyoutSystemBackdrop
            //     （MenuFlyout_themeresources.xaml L264-L271），由 FlyoutBase::ForwardSystemBackdropToPopup
            //     搬到弹层 Popup（FlyoutBase_partial.cpp L1340-L1346）；
            //   · 圆角/描边：CornerRadius = OverlayCornerRadius（L285）；
            //   · 图标：MenuFlyoutItem 只有在 MenuFlyoutPresenter 祖先下才会把
            //     IconRoot 从 Collapsed 切到 Visible（MenuFlyoutItem_Partial.cpp L690-L760）；
            //   · 焦点：菜单项由 presenter 统一管键盘焦点，不会出现「一开就落在最后一项」。
            // 代价只有一个，源码写死的：MenuFlyout 忽略 Placement（FlyoutBase_partial.cpp L3264），
            // Position 就是菜单左上角。所以「居中向上」拿不到，只能贴光标展开。
            LinkOpenMenuAction? picked = null;

            var flyout = new MenuFlyout();

            // TaskCompletionSource.RunContinuationsAsynchronously：续体不能占用 Closed 事件的调用线程，
            // 否则回到 HandleAskBehaviorAsync 后接着写的 UI 代码会撞在菜单自己的关闭过程里。
            var tcs = new TaskCompletionSource<LinkOpenMenuAction?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void AddAction(string resourceKey, LinkOpenMenuAction action, string glyph)
            {
                var item = new MenuFlyoutItem
                {
                    Text = LocalizationHelper.GetString(resourceKey),
                    Icon = new FontIcon { Glyph = glyph }
                };
                item.Click += (_, _) =>
                {
                    picked = action;
                    flyout.Hide();
                };
                flyout.Items.Add(item);
            }

            // 码位取的是 Segoe Fluent / MDL2 官方字形表里同名的四个图标，
            // 且与本文件所在页的右键菜单保持一致：右键「复制链接地址」用的就是 E71B（Link）。
            AddAction("LinkOpen_NewWindow", LinkOpenMenuAction.NewWindow, "\uE8A7");        // OpenInNewWindow
            AddAction("LinkOpen_SystemBrowser", LinkOpenMenuAction.SystemBrowser, "\uE774"); // Globe
            AddAction("LinkOpen_SamePage", LinkOpenMenuAction.SamePage, "\uE7C3");          // Page
            AddAction("LinkOpen_CopyLink", LinkOpenMenuAction.CopyLink, "\uE71B");           // Link
            AddAction("LinkOpen_EditDefault", LinkOpenMenuAction.EditDefault, "\uE713");     // Setting

            // Click 先于 Closed，所以这里读到的一定是最后一次点中的项；没点过就是 null（等于取消）。
            flyout.Closed += (_, _) => tcs.TrySetResult(picked);

            var options = new FlyoutShowOptions
            {
                // 官方「贴在指针旁的 flyout」样本用的就是这一对：Position（相对 placement target）+ Transient。
                // FlyoutShowMode 的官方定义里，Transient = 「显示中的 flyout 不获取焦点」；
                // 不设就是 Auto，实机走的是抢焦点那一路 —— 而这座窗口一失焦就自己藏起来
                // （主窗口.xaml.cs 的 OnWindowActivated → ToggleWindow），
                // 菜单刚弹、主窗口就从菜单底下溜了，看着就是「菜单没跟着鼠标、跑出去了」。
                // 代价：Transient 拿不到键盘焦点，ESC 关不掉，只能点别处轻按关闭（等价于取消）。
                ShowMode = FlyoutShowMode.Transient
            };
            if (TryGetCursorPositionIn(anchor, out Windows.Foundation.Point cursorPosition))
            {
                // MenuFlyout 的 Position 是菜单左上角（Placement 对菜单无效，见上面的说明），
                // 补一个很小的偏移，别让指针正好压在第一个菜单项的边线上。
                options.Position = new Windows.Foundation.Point(
                    cursorPosition.X + CursorOffsetInDips,
                    cursorPosition.Y + CursorOffsetInDips);
            }

            try
            {
                flyout.ShowAt(anchor, options);
            }
            catch (Exception ex)
            {
                // ShowAt 抛异常时 Closed 不会来，必须当场把 Task 结掉 ——
                // 否则调用方一路 await 下去，NewWindowRequested 的延迟永远不完成，页面卡在等这个事件。
                tcs.TrySetResult(null);
                LogService.Error("WebView2.NewWindow", $"打开方式菜单弹出失败: {uri}", ex);
            }

            // 超时与「用户真的答了」赛跑。绝不能用 Task.Wait/Result 那类同步等法 ——
            // WebView2 官方口径写得很清楚：把消息泵按住，事件处理程序和完成回调就都不跑了，
            // 等于自己把这个 deferral 锁死。
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(AskMenuTimeoutMs));

            if (!ReferenceEquals(completed, tcs.Task))
            {
                // 超时这一路 Closed 之后还可能再来，TrySetResult 是幂等的，接住即可
                tcs.TrySetResult(null);

                // 顺手把菜单关掉：留一张「按下去没反应」的活菜单在屏幕上，比关掉它更让人困惑。
                // Hide 失败也不能外溢 —— 调用方还在等一个结论，异常只会把结论换成卡死。
                try
                {
                    flyout.Hide();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LinkOpenFlyout] 超时后关闭菜单失败: {ex.Message}");
                }

                LogService.Warning(
                    "WebView2.NewWindow",
                    $"打开方式菜单超过 {AskMenuTimeoutMs / 1000} 秒没有回应，按「取消」处理并放行 deferral: {uri}");

                return null;
            }

            return await tcs.Task;
        }

        /// <summary>
        /// 取当前鼠标位置，换算成指定元素坐标系里的点
        /// </summary>
        /// <remarks>
        /// Windows App SDK 把 <c>PointerPoint</c> 的全部静态工厂（GetCurrentPoint / GetIntermediatePoints
        /// 等）都删掉了，XAML 侧拿不到「当前指针在哪」；而 <c>NewWindowRequested</c> 的事件参数里也没有
        /// 坐标（只有 Uri / IsUserInitiated / Handled / NewWindow / Name / WindowFeatures /
        /// OriginalSourceFrameInfo），所以只能走 Win32 取光标，再用 <see cref="WindowHandleHelper"/>
        /// 拿本页所在窗口的句柄换算成客户区坐标。
        /// </remarks>
        private bool TryGetCursorPositionIn(
            FrameworkElement target,
            out Windows.Foundation.Point position)
        {
            position = default;

            XamlRoot? root = target.XamlRoot;
            if (root?.Content is not UIElement rootElement)
            {
                LogService.Info("WebView2.NewWindow", "菜单定位：XamlRoot.Content 取不到，改用菜单默认摆放");
                return false;
            }

            // 取「这个页面所在窗口」的句柄，不是 App.MainWindow。后者在本项目只是兜底：
            // 托盘重建窗口那条路上 App.MainWindow 与页面宿主可能不是同一座窗口
            // （见 WindowHostController.cs 里那句「从 App.MainWindow 获取失败，回退到当前活动窗口」），
            // 拿错窗口 = ScreenToClient 的基准整体错位 = 菜单飞到主窗口外面。
            IntPtr hwnd = WindowHandleHelper.GetWindowHandleFromPage(this);
            if (hwnd == IntPtr.Zero)
            {
                LogService.Info("WebView2.NewWindow", "菜单定位：取不到页面所在窗口句柄，改用菜单默认摆放");
                return false;
            }

            if (!GetCursorPos(out NativePoint cursor))
            {
                LogService.Info("WebView2.NewWindow", "菜单定位：GetCursorPos 失败，改用菜单默认摆放");
                return false;
            }

            if (!ScreenToClient(hwnd, ref cursor))
            {
                LogService.Info("WebView2.NewWindow", "菜单定位：ScreenToClient 失败，改用菜单默认摆放");
                return false;
            }

            // RasterizationScale 是相对 96dpi 的倍率，物理像素除它以得到 DIP。
            double scale = root.RasterizationScale;
            if (scale <= 0.0)
            {
                scale = 1.0;
            }

            // 前提：主窗口是 ExtendsContentIntoTitleBar = true（见 功能\主窗口\外观\标题栏服务.cs），
            // XAML 内容铺满客户区，所以客户区原点与 XamlRoot 原点重合。
            var cursorInRoot = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale);
            Windows.Foundation.Point targetInRoot =
                target.TransformToVisual(rootElement).TransformPoint(new Windows.Foundation.Point(0, 0));
            var cursorInTarget = new Windows.Foundation.Point(
                cursorInRoot.X - targetInRoot.X,
                cursorInRoot.Y - targetInRoot.Y);

            position = cursorInTarget;
            return true;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out NativePoint lpPoint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }
    }
}
