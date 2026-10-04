using DockedTools.Features.Pages.Settings;
// ⚠️ 这条 using 不能省：PwaModeService 在 ...Browser.Services 子命名空间里，
//    而本页在 ...Browser，跨了一层，不写全名就解析不到（同目录的 AdaptiveColour.cs 同理）。
using DockedTools.Features.Pages.WebApp.Browser.Services;
using System;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - PWA 模式模块
    ///
    /// <para>「让网页以为自己在手机 PWA 宿主里」分三步落地，全部在 <see cref="PwaModeService"/>：
    /// 移动端 UA + Client Hints（CDP）、<c>navigator.standalone</c>、<c>display-mode</c> 媒体查询（注入脚本）。</para>
    ///
    /// <para>注入时机和自适应取色是同一套约束：<c>AddScriptToExecuteOnDocumentCreated</c>
    /// 只对【注入之后才创建的文档】生效，所以必须在首次导航之前挂上，
    /// 否则用户看到的第一个页面永远没有伪装。</para>
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// 下发一次的兜底时限。
        ///
        /// <para>这次 await 卡在 WebView 初始化的必经之路上（见 WebView.cs），
        /// 而 <c>PwaModeService.ApplyToCoreAsync</c> 内部有跨进程 CDP 往返 ——
        /// 浏览器进程繁忙、快要崩、或者渲染进程起不来时它是可能挂住的。
        /// 没有上限的话，「PWA 下发卡住」会直接升级成「整个 WebView 起不来」，
        /// 而 UA / 触摸归根结底只是增强项，不配让页面打不开。</para>
        /// </summary>
        private const int PwaApplyTimeoutMs = 3000;

        /// <summary>
        /// 把 PWA 模式的当前设置下发到本页内核。
        ///
        /// <para><b>必须 await，而且必须在首次导航之前</b>（见 WebView.cs 的两条初始化路径）。
        /// 三条通道里没有一条追得上「已经发出去的请求」：<c>WebResourceRequested</c> 改的是
        /// 尚未发出的请求、CDP override 对已加载文档无效、注入脚本只对后续文档生效。
        /// 上一版这里是 fire-and-forget，于是它和 <c>Navigation.cs</c> 那条
        /// <c>ContinueWith → TryNavigatePendingUri</c> 抢跑，谁先到看运气 ——
        /// 「首次进页面显示已开启、网页却还是桌面版」就是这么来的。</para>
        /// </summary>
        private async Task EnsurePwaModeAsync()
        {
            var core = WebView?.CoreWebView2;
            if (core == null)
            {
                // ⭐ 内核还没就绪：既没有 UA 覆盖也没有脚本，此时网页是完完全全的桌面态。
                //    必须如实记为「未生效」—— 底栏图标读的就是这个值，
                //    否则新开一个 Page 会顶着上一次的设置直接显示成已开启。
                SetPwaModeEffective(false);
                return;
            }

            bool effective;

            try
            {
                Task<bool> work = PwaModeService.ApplyToCoreAsync(core);
                Task finished = await Task.WhenAny(work, Task.Delay(PwaApplyTimeoutMs));

                if (finished != work)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EnsurePwaModeAsync] ⏱️ 下发超时（{PwaApplyTimeoutMs}ms），按未生效处理");
                    effective = false;
                }
                else
                {
                    bool applied = await work;
                    // ⭐ 关模式下 applied 只代表「把默认 UA 写回去了」，谈不上「伪装生效」。
                    //    所以「生效」必须额外要求设置本身是开的 —— 关着的灯不该显示成绿灯。
                    effective = applied && ExperimentalSettings.EnablePwaMode;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsurePwaModeAsync] 下发失败: {ex.Message}");
                effective = false;
            }

            SetPwaModeEffective(effective);
        }

        /// <summary>
        /// 记录本页内核的 PWA 伪装是否真的到位，并把结果同步给底栏。
        ///
        /// <para>这是「设置」与「UI」之间唯一的校验点：底栏图标读的是
        /// <see cref="_pwaModeEffective"/> + <c>EnablePwaMode</c> 两个值，而不是设置本身，
        /// 于是「设置开了但内核没跟上」会显示成第三档警示态，而不会假装成功。</para>
        /// </summary>
        private void SetPwaModeEffective(bool effective)
        {
            _pwaModeEffective = effective;
            UpdateBottomBarPwaMode();
        }

        /// <summary>
        /// 底栏「PWA 模式」按钮：开 ⇄ 关。
        /// 设置是全局的，改完广播出去，其它已经打开的网页浏览页会跟着同步。
        /// </summary>
        private void OnPwaModeButtonClick()
        {
            bool next = !ExperimentalSettings.EnablePwaMode;
            ExperimentalSettings.EnablePwaMode = next;
            System.Diagnostics.Debug.WriteLine($"[OnPwaModeButtonClick] PWA 模式 → {next}");

            // ⭐ 立刻给出反馈，不等异步链。
            //    新状态还没经过内核验证，先渲染成「待生效」那一档；
            //    异步跑完会按真实结果校正一次。这样点下去有没有反应，第一眼就能看出来 ——
            //    早先要等 CDP 往返回来才动图标，看着像按钮没接上。
            SetPwaModeEffective(false);

            PwaModeService.NotifyChanged();
        }

        /// <summary>
        /// PWA 模式设置变化（可能由本页按钮触发，也可能来自别的页面实例）。
        /// </summary>
        private void OnPwaModeSettingsChanged() => _ = ApplyPwaModeChangeAsync();

        private async Task ApplyPwaModeChangeAsync()
        {
            await EnsurePwaModeAsync();
            UpdateBottomBarPwaMode();

            // 已经加载完的文档不会再跑一次常驻脚本 —— 不重新导航的话，
            // 用户点完按钮现场什么都不会变，会以为按钮坏了。
            ReloadForPwaModeSwitch();
        }

        private void ReloadForPwaModeSwitch()
        {
            var core = WebView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(core.Source) ||
                core.Source.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                core.Reload();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReloadForPwaModeSwitch] 重新加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 把 PWA 模式的最新状态同步到底栏按钮（脏值比对，避免无谓的 reconcile）。
        ///
        /// 两个值一起比：<c>PwaMode</c> 是设置、<c>PwaModeEffective</c> 是内核实际结果，
        /// 三档外观由它俩共同决定，漏比任何一个都会让图标停在别人的状态上。
        /// </summary>
        private void UpdateBottomBarPwaMode()
        {
            if (_bottomButtonBarComponent == null)
            {
                return;
            }

            bool enabled = ExperimentalSettings.EnablePwaMode;
            // 设置关着时不谈「生效」：既无从生效，也不该让曾经验证过的残留值透出去。
            bool effective = enabled && _pwaModeEffective;

            if (_bottomButtonBarComponent.PwaMode == enabled &&
                _bottomButtonBarComponent.PwaModeEffective == effective)
            {
                return;
            }

            _bottomButtonBarComponent.PwaMode = enabled;
            _bottomButtonBarComponent.PwaModeEffective = effective;
            _reactorHostControl?.Mount(_bottomButtonBarComponent);
        }
    }
}
