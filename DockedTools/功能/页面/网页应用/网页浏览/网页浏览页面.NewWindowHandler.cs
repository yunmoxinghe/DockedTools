using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.ContentArea;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 新窗口请求处理模块
    /// 处理 target="_blank" 等新窗口打开请求
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// 处理新窗口请求事件
        /// 根据用户设置决定如何打开链接
        ///
        /// <para><b>⚠️ 本处理器刻意写成同步 <see langword="void"/>，并且把真正的动作
        /// 投递到下一轮消息循环。</b></para>
        ///
        /// <para>旧版是 <c>async void</c> 直接在回调栈里 <c>await</c> 一个等用户点的应用内
        /// 对话框。微软《WebView2 应用的线程模型》写明了：回调（事件处理程序与完成回调）
        /// 是串行运行的，在 WebView2 事件回调里同步起模态 UI 属于「尝试重入」，
        /// WebView2 不支持，会把该回调<b>无限期留在调用栈上</b> —— 落到实机上就是
        /// 整个软件无响应，进而被托盘看门狗判为卡死并静默重启。
        /// 官方给的替代写法就是「把安排好的工作放到事件处理程序完成之后执行」，
        /// 也就是下面这一段 <c>TryEnqueue</c>。</para>
        ///
        /// <para><c>e.Handled</c> 原本是必须留在栈内的 —— 它是对本次回调的答复，
        /// 出栈之后就晚了。但「每次询问」档的答复要等用户点完菜单才知道，
        /// 而且其中「新窗口打开」这一档在旧版对话框里根本给不出来：栈内写了 <c>Handled = true</c>
        /// 就等于当场否掉了内核弹窗，之后无论用户选什么都回不去。
        /// 解法是官方指定的另一条：<c>e.GetDeferral()</c>。
        /// 《CoreWebView2.NewWindowRequested》Remarks 原文 ——
        /// 「If either Handled or NewWindow properties are not set, the target content will be
        /// opened on a popup window. If a deferral is not taken on the event args, scripts that
        /// resulted in the new window that are requested are blocked until the event handler
        /// returns. If a deferral is taken, then scripts are blocked until the deferral is
        /// completed.」 —— 取了延迟之后事件参数会一直活着，属性到 <c>Complete()</c> 才被读，
        /// 所以「拦截 / 放行」这件事可以从栈内挪到菜单关闭之后。</para>
        /// </summary>
        private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // 所有权在成功投递后交给下一轮消息循环；没交出去就留在这里兜底回收，
            // 否则页面会一直卡在「等这个事件处理完」上（官方特别警告过异常导致
            // Complete 没被调用时，延迟要等到垃圾回收才释放，期间 WebView2 一直在等）。
            // 类型是 Windows.Foundation.Deferral 而不是 CoreWebView2Deferral ——
            // 本项目走的是 WebView2 的 WinRT 投影，和 WebPermissionBridge 里同一套。
            Windows.Foundation.Deferral? askDeferral = null;

            try
            {
                LinkOpenBehavior behavior = ExperimentalSettings.LinkOpenBehavior;

                // 「新窗口打开」= 什么都不做。官方口径是 Handled 与 NewWindow 都不设时
                // 内容开进 WebView2 自己的 popup 窗口，而 Handled 的默认值本就是 false，
                // 所以这一档连事件参数都不用读，直接出栈把请求还给内核。
                // （事件订阅保持常驻，判的是每次请求时的当前设置 —— 用户改档不需要重订阅。）
                if (behavior == LinkOpenBehavior.NewWindow)
                {
                    System.Diagnostics.Debug.WriteLine("[NewWindowRequested] 按「新窗口打开」放行，不拦截");
                    return;
                }

                // 先取成局部变量：菜单正文和日志用得上，不必再回读事件参数
                string uri = e.Uri;
                bool isUserInitiated = e.IsUserInitiated;

                System.Diagnostics.Debug.WriteLine($"[NewWindowRequested] 收到新窗口请求: {uri}, 行为: {behavior}, 用户触发: {isUserInitiated}");

                if (behavior == LinkOpenBehavior.Ask)
                {
                    // 答复要等菜单关掉，所以这里一个属性都不写，只把回调挂起
                    askDeferral = e.GetDeferral();
                }
                else
                {
                    // 其余档的答复当场就能定，照旧在栈内拦掉，不需要延迟
                    e.Handled = true;
                }

                Windows.Foundation.Deferral? deferred = askDeferral;
                bool queued = DispatcherQueue?.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    () => _ = HandleNewWindowRequestAsync(behavior, uri, isUserInitiated, e, deferred)) ?? false;

                if (queued)
                {
                    askDeferral = null;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[NewWindowRequested] 无法投递到 UI 线程，本次按「新窗口打开」放行");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NewWindowRequested] 处理失败: {ex.Message}");
            }
            finally
            {
                askDeferral?.Complete();
            }
        }

        /// <summary>
        /// 新窗口请求的实际处置 —— 在回调出栈之后才运行，因此可以放心弹菜单、
        /// 拉起外部进程，甚至再开一个 WebView2 而不触发重入。
        /// </summary>
        /// <param name="eventArgs">
        /// 仅「每次询问」档会传入（配合 <paramref name="deferral"/> 在菜单关闭之后再回写
        /// <c>Handled</c>）；其余档已在栈内答复完，传 <see langword="null"/>。
        /// </param>
        /// <param name="deferral">与 <paramref name="eventArgs"/> 配对的延迟，必须在最后完成。</param>
        private async Task HandleNewWindowRequestAsync(
            LinkOpenBehavior behavior,
            string uri,
            bool isUserInitiated,
            CoreWebView2NewWindowRequestedEventArgs? eventArgs,
            Windows.Foundation.Deferral? deferral)
        {
            try
            {
                switch (behavior)
                {
                    case LinkOpenBehavior.Ask:
                        // 每次询问
                        await HandleAskBehaviorAsync(uri, isUserInitiated, eventArgs);
                        break;

                    case LinkOpenBehavior.SystemBrowser:
                        // 在系统默认浏览器打开
                        await LaunchSystemBrowserAsync(uri);
                        break;

                    case LinkOpenBehavior.SamePage:
                        // 在本页打开
                        NavigateToUrl(uri);
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine($"[NewWindowRequested] 未知行为: {behavior}");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleNewWindowRequestAsync] 处理失败: {ex.Message}");
            }
            finally
            {
                // 出异常时 Handled 仍是默认的 false，效果等于放行让内核弹窗；
                // 无论如何都要完成延迟，不能把页面吊在半路。
                deferral?.Complete();
            }
        }
        
        /// <summary>
        /// 处理"每次询问"行为
        /// </summary>
        private async Task HandleAskBehaviorAsync(
            string uri, bool isUserInitiated, CoreWebView2NewWindowRequestedEventArgs? eventArgs)
        {
            try
            {
                // 非用户触发的弹窗(可能是广告)直接阻止
                if (!isUserInitiated)
                {
                    System.Diagnostics.Debug.WriteLine($"[HandleAskBehavior] 阻止非用户触发的弹窗: {uri}");
                    if (eventArgs != null)
                    {
                        eventArgs.Handled = true;
                    }
                    return;
                }
                
                // 确保在 UI 线程上执行。
                // 走的是本项目 DispatcherQueueExtensions 里的 EnqueueAsync —— 它返回非泛型 Task，
                // 只告知「跑完了」，不带回返回值，所以选中项必须由闭包写回局部变量。
                // （实际上从 CoreWebView2_NewWindowRequested 的 TryEnqueue 进来时已经在本线程，
                //   这个分支只是保留原有的兜底。）
                LinkOpenMenuAction? picked = null;
                if (!DispatcherQueue.HasThreadAccess)
                {
                    await DispatcherQueue.EnqueueAsync(async () =>
                    {
                        picked = await ShowAskFlyoutAsync(uri);
                    });
                }
                else
                {
                    picked = await ShowAskFlyoutAsync(uri);
                }

                if (picked is null)
                {
                    // 取消（点别处关掉菜单）—— 拦掉，什么都不开
                    if (eventArgs != null)
                    {
                        eventArgs.Handled = true;
                    }
                    return;
                }

                if (picked == LinkOpenMenuAction.NewWindow)
                {
                    // 用户要普通的新窗口：Handled 保持 false，上面的 deferral 一完成，
                    // 内核就按默认口径把内容开进它自己的 popup 窗口。
                    System.Diagnostics.Debug.WriteLine("[HandleAskBehavior] 用户选择新窗口打开，放行给内核");
                    return;
                }

                // 其余四档都不该让内核弹窗，先当场把答复钉死。
                if (eventArgs != null)
                {
                    eventArgs.Handled = true;
                }

                switch (picked.Value)
                {
                    case LinkOpenMenuAction.SystemBrowser:
                        await LaunchSystemBrowserAsync(uri);
                        break;

                    case LinkOpenMenuAction.SamePage:
                        NavigateToUrl(uri);
                        break;

                    case LinkOpenMenuAction.CopyLink:
                        CopyTextToClipboard(uri);
                        break;

                    case LinkOpenMenuAction.EditDefault:
                        OpenLinkOpenSettingsPage();
                        break;
                }
            }
            catch (Exception ex)
            {
                // 问不成就当没问：拦掉。异常都落在 Handled = true 之前（弹菜单、执行动作），
                // 不补这一句，延迟一完成内核就会按「Handled/NewWindow 都没设」的默认口径
                // 弹一个用户根本没选过的 popup —— 询问档反而成了最难收拾的那一档。
                // 用户显式选「新窗口打开」是正常 return，不会走到这里，所以无条件置 true 是安全的。
                if (eventArgs != null)
                {
                    eventArgs.Handled = true;
                }

                LogService.Error("WebView2.NewWindow", $"询问打开方式失败，已拦截: {uri}", ex);
            }
        }

        /// <summary>
        /// 把文本写进剪贴板（菜单里的「复制链接」）
        /// </summary>
        private static void CopyTextToClipboard(string text)
        {
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);
                // Flush：不显式刷一次，写入要等进程自己决定何时提交，包应用被挂起时可能丢。
                Clipboard.Flush();
                System.Diagnostics.Debug.WriteLine($"[HandleAskBehavior] 已复制链接: {text}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleAskBehavior] 复制链接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 跳到设置页（菜单里的「修改默认」）
        /// </summary>
        /// <remarks>
        /// 导航排在方法返回之后：这一刻 <c>Handled</c> 刚写完、延迟还没 Complete，
        /// 当场换页会把承载这座 WebView 的页面一起拆掉，后续路径就跑在正在释放的对象上。
        /// </remarks>
        private void OpenLinkOpenSettingsPage()
        {
            bool queued = DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () =>
                {
                    try
                    {
                        ContentAreaService.Navigate(typeof(SettingsPage));
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("WebView2.NewWindow", "打开链接设置页失败", ex);
                    }
                }) ?? false;

            if (!queued)
            {
                LogService.Info("WebView2.NewWindow", "菜单「修改默认」：投递导航失败，设置页未打开");
            }
        }
        
        /// <summary>
        /// 在系统默认浏览器中打开链接
        /// </summary>
        private async Task LaunchSystemBrowserAsync(string uri)
        {
            try
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? uriObj))
                {
                    bool success = await Launcher.LaunchUriAsync(uriObj);
                    System.Diagnostics.Debug.WriteLine($"[LaunchSystemBrowser] 启动系统浏览器: {uri}, 结果: {success}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[LaunchSystemBrowser] 无效的 URI: {uri}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchSystemBrowser] 启动失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 在当前 WebView 窗口中导航到 URL
        /// </summary>
        private void NavigateToUrl(string uri)
        {
            try
            {
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Navigate(uri);
                    System.Diagnostics.Debug.WriteLine($"[NavigateToUrl] 在 WebView 中打开: {uri}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigateToUrl] WebView 未初始化");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigateToUrl] 导航失败: {ex.Message}");
            }
        }
    }
}
