using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 进程管理模块
    /// 包含浏览器进程退出和故障恢复逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// environment 级「浏览器进程退出」。
        ///
        /// <para>与其说是占位被填，不如说这里原本缺的是<b>区分层级</b>：
        /// CoreWebView2_ProcessFailed 是内核级事件，带完整的日志与恢复策略
        /// （渲染进程退出 → Reload；浏览器进程崩溃 → 关掉重建）；
        /// 这条则是 environment 级 —— 同一个用户数据目录下关联的<b>整个进程组</b>都没了，
        /// 此时内核对象还在但已经是个空壳，任何 CoreWebView2 调用都会失败。
        /// 所以这里不该再去踢 Reload，只做两件事：</para>
        ///
        /// <para>① 留一条同等详细度的日志（排查「为什么整组进程都没了」靠的就是它）；
        /// ② 把本页标记为「待重建」，由 INavigationAware.OnNavigatedTo 那边的重建路径兜 ——
        /// 不在事件里直接重建，是因为页面此刻很可能不在前台（被 LRU 缓存着），
        /// 为一个没给用户看的页面拉起浏览器进程组毫无意义。</para>
        ///
        /// <para>⚠️ 不要在这里Detach 事件：内核对象已归0，"-=" 本身就可能抛。
        /// 反正这份引用随独享的 environment 一起走，页面被 Dispose 时不会再有人订阅。</para>
        /// </summary>
        private void CoreWebView2Environment_BrowserProcessExited(
            object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
        {
            try
            {
                string kind = e.BrowserProcessExitKind.ToString();
                string shortcutId = _currentShortcut?.Id ?? "null";

                string message = $"WebView2 浏览器进程组已退出\n" +
                    $"  退出方式: {kind}\n" +
                    $"  Shortcut ID: {shortcutId}\n" +
                    $"  实例 ID: {_instanceId}\n" +
                    $"  IsDisposed: {_isDisposed}\n" +
                    $"  IsWebViewReady: {_isWebViewReady}";

                System.Diagnostics.Debug.WriteLine($"[BrowserProcessExited] {message}");

                // 正常退出（例如本会话最后一个 WebView 被关闭）也要留痕：
                // 「到底是崩了还是我关的」这两个最常见的疑问，光靠 Warning 级别区分不出来，
                // 所以这里按 kind 分别落到 Warning / Info。
                if (string.Equals(kind, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    Features.UnifiedCalls.Logging.LogService.Warning("WebView2.BrowserProcessExited", message);
                }
                else
                {
                    Features.UnifiedCalls.Logging.LogService.Info("WebView2.BrowserProcessExited", message);
                }

                if (_isDisposed)
                {
                    return;
                }

                // 整个进程组都没了 ⇒ 本页的内核必然已经失效。
                // 标记之后由 INavigationAware.OnNavigatedTo 的 _needsWebViewRecreation 分支重建，
                // 而不是在这里硬重建（见注释第 ② 条）。
                _needsWebViewRecreation = true;
                _isWebViewReady = false;
            }
            catch (Exception ex)
            {
                // 这里所有的 e.* 访问都可能因为底层 COM 已死而抛 —— 兜住，别带崩进程
                System.Diagnostics.Debug.WriteLine($"[BrowserProcessExited] 处理失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// ⭐ 任务 3.3：WebView2 进程失败事件处理器（已完成）
        /// 记录 ProcessFailedKind、Reason、当前 URL、Shortcut ID、是否正在恢复等诊断信息
        /// 捕获 handler 内部异常，避免二次崩溃
        /// </summary>
        private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try
            {
                // ✅ 记录 ProcessFailedKind
                var processFailedKind = e.ProcessFailedKind;
                
                // ✅ 记录 Reason
                var reason = e.Reason;
                
                // ✅ 记录当前 URL
                // ⚠️ 刻意读 WebView.Source（XAML 依赖属性，本地值）而不是
                //    WebView.CoreWebView2.Source（一次跨进程 COM 调用）——
                //    微软文档写明：这些失败 handler 执行期间底层浏览器进程是被 block 的，
                //    此时调 CoreWebView2 的 API 既可能抛、也可能干脆不回。
                //    日志里少一个精确 URL 无所谓，在处理器里等一个不回的对象是要命。
                string? currentUrl = null;
                try
                {
                    currentUrl = WebView?.Source?.AbsoluteUri ?? _pendingNavigationUri?.ToString() ?? "未知";
                }
                catch
                {
                    currentUrl = "无法获取";
                }
                
                // ✅ 记录 Shortcut ID
                var shortcutId = _currentShortcut?.Id ?? "null";
                var shortcutName = _currentShortcut?.Name ?? "未知";
                
                // ✅ 记录是否正在恢复（通过检查相关标志）
                var isRecovering = _needsWebViewRecreation ? "是" : "否";
                
                // 记录进程描述信息（如果可用）
                var processDescription = !string.IsNullOrEmpty(e.ProcessDescription) 
                    ? e.ProcessDescription 
                    : "无描述";
                
                // 记录 ExitCode（如果可用）
                int? exitCode = null;
                try
                {
                    exitCode = e.ExitCode;
                }
                catch
                {
                    // ExitCode 可能不可用（某些失败类型）
                }
                
                // ✅ 构建详细的日志消息（包含所有需求字段）
                var logMessage = $"WebView2 进程失败\n" +
                                $"  ProcessFailedKind: {processFailedKind}\n" +
                                $"  Reason: {reason}\n" +
                                $"  ProcessDescription: {processDescription}\n" +
                                $"  ExitCode: {(exitCode.HasValue ? exitCode.Value.ToString() : "N/A")}\n" +
                                $"  当前 URL: {currentUrl}\n" +
                                $"  Shortcut ID: {shortcutId}\n" +
                                $"  Shortcut 名称: {shortcutName}\n" +
                                $"  是否正在恢复: {isRecovering}\n" +
                                $"  IsDisposed: {_isDisposed}\n" +
                                $"  IsWebViewReady: {_isWebViewReady}\n" +
                                $"  实例 ID: {_instanceId}";
                
                // ✅ 输出到调试控制台
                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] {logMessage}");
                
                // ✅ 使用 LogService 记录到文件（需求：2.3.2）
                Features.UnifiedCalls.Logging.LogService.Error(
                    "WebView2.ProcessFailed",
                    logMessage);
                
                // ⭐ 任务 3.4：实现恢复策略（需求：2.3.3、3.2.1、3.2.2、3.2.3）
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    try
                    {
                        // ⭐ 任务 3.5：防重入检查 - 如果正在恢复中，直接返回（需求：2.3.3）
                        if (_isRecoveringWebView)
                        {
                            System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ⚠️ 正在恢复中，忽略本次事件以防止重入");
                            Features.UnifiedCalls.Logging.LogService.Warning(
                                "WebView2.ProcessFailed",
                                $"防重入保护：忽略 {processFailedKind} 事件（恢复正在进行中）");
                            return;
                        }
                        
                        switch (processFailedKind)
                        {
                            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                                // 渲染进程退出：内核还在，交给统一恢复入口（Reload，带上限与退避）。
                                // ⚠️ 这里绝不再直接 Reload —— 旧写法无次数上限、无冷却，
                                //    一个必崩的页面会 Reload→崩→Reload 无限转，每轮都在 UI 线程上重新起进程。
                                RequestWebViewRecovery("渲染进程退出", recreate: false);
                                break;

                            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                                // 主浏览器进程退出：内核已成空壳，必须重建控件。
                                // ⚠️ 旧写法是在这里当场 Close + RecreateWebView，属于「在进程失败的恢复路径里
                                //    同步往 XAML 树塞新 WebView2」；整组进程消失时 LRU 里每个页面都会各来一次，
                                //    现在统一走 RequestWebViewRecovery（前台页延后一步重建、后台页只标记）。
                                RequestWebViewRecovery("浏览器进程退出", recreate: true);
                                break;

                            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                                // 渲染进程无响应：累计次数，连续多次后走同一条恢复入口（需求：3.2.3）
                                _unresponsiveCount++;
                                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] 检测到 RenderProcessUnresponsive，计数: {_unresponsiveCount}/{MaxUnresponsiveCountBeforeReload}");

                                if (_unresponsiveCount >= MaxUnresponsiveCountBeforeReload)
                                {
                                    RequestWebViewRecovery($"渲染进程连续无响应 {MaxUnresponsiveCountBeforeReload} 次", recreate: false);
                                }
                                break;
                            
                            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                                // Frame 渲染进程退出：仅记录日志，通常不需要恢复
                                System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] 检测到 FrameRenderProcessExited，仅记录日志");
                                break;
                            
                            case CoreWebView2ProcessFailedKind.UtilityProcessExited:
                            case CoreWebView2ProcessFailedKind.SandboxHelperProcessExited:
                            case CoreWebView2ProcessFailedKind.GpuProcessExited:
                                // 辅助进程退出：通常无需恢复，仅记录诊断信息
                                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] 检测到 {processFailedKind}，仅记录诊断信息");
                                break;
                            
                            default:
                                // 未知类型：记录日志
                                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] 检测到未知的 ProcessFailedKind: {processFailedKind}");
                                break;
                        }
                    }
                    catch (Exception recoveryEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] ⚠️ 恢复策略执行失败: {recoveryEx.Message}");
                        Features.UnifiedCalls.Logging.LogService.Error(
                            "WebView2.ProcessFailed.Recovery",
                            "恢复策略执行过程中发生异常",
                            recoveryEx);
                        
                        // ⭐ 任务 3.5：异常情况下也要重置 guard
                        _isRecoveringWebView = false;
                        System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ 恢复标志已重置（异常恢复）");
                    }
                });
            }
            catch (Exception ex)
            {
                // ✅ 捕获处理器内部异常，避免二次崩溃（需求：2.3.2）
                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] ⚠️ 处理器内部异常: {ex.Message}");

                // 记录处理器自身的异常
                try
                {
                    Features.UnifiedCalls.Logging.LogService.Error(
                        "WebView2.ProcessFailed",
                        "ProcessFailed 处理器内部发生异常（已捕获，避免二次崩溃）",
                        ex);
                }
                catch
                {
                    // 如果日志服务本身失败，也不抛出异常
                }
            }
        }

        /// <summary>
        /// 统一的进程失败恢复入口 —— 把「要不要立刻自愈、能自愈几次、隔多久再来一次」集中管起来。
        ///
        /// <para><b>必须从 UI 线程调用</b>（<see cref="CoreWebView2_ProcessFailed"/> 已经用
        /// <c>DispatcherQueue.TryEnqueue</c> 把恢复动作整体挪到了 handler 栈之外，正是微软
        /// 对「事件回调串行、不要在回调里重入」的要求）。</para>
        ///
        /// <para><b>三道闸门，目的都是少占 UI 线程：</b>① 页面不在前台就不动作（整组进程
        /// 消失时，LRU 里每个缓存页各自重建会把主线程按住很久）；② 连续自愈有上限；
        /// ③ 每次自愈之间按指数退避冷却。三道之后才轮到真正的 Reload / 重建。</para>
        /// </summary>
        /// <param name="reason">写进日志的原因</param>
        /// <param name="recreate">true = 内核已成空壳必须重建控件；false = 内核还在，Reload 即可</param>
        private void RequestWebViewRecovery(string reason, bool recreate)
        {
            if (_isDisposed)
            {
                return;
            }

            // 重入闸门：一次恢复没跑完之前不再叠加第二套动作
            if (_isRecoveringWebView)
            {
                LogService.Warning("WebView2.Recovery", $"{reason}：上一次恢复仍在进行中，本次忽略");
                return;
            }

            // ── 闸门①：后台页不动作 ────────────────────────────────
            if (!_isPageActive)
            {
                _deferredRecoveryReason = reason;
                _deferredRecoveryNeedsRecreation |= recreate;

                if (recreate)
                {
                    // 内核已是空壳：ready 必须打掉，其它路径才不会拿它去调用；
                    // 同时置待重建，让 OnNavigatedTo 那条现成的重建路径也能接住它。
                    _needsWebViewRecreation = true;
                    _isWebViewReady = false;
                }

                LogService.Warning("WebView2.Recovery",
                    $"{reason}：页面不在前台，本次不做恢复，记为待办（回到前台补做，实例 {_instanceId}）");
                return;
            }

            // ── 闸门②：连续自愈上限 ────────────────────────────────
            if (_consecutiveRecoveryAttempts >= MaxConsecutiveRecoveryAttempts)
            {
                if (recreate)
                {
                    _needsWebViewRecreation = true;
                    _isWebViewReady = false;
                }

                LogService.Warning("WebView2.Recovery",
                    $"{reason}：连续自愈已达上限（{MaxConsecutiveRecoveryAttempts} 次），已停止自动恢复。" +
                    $"继续踢只会在 UI 线程上反复拉起进程树。导航成功后计数自动清零（实例 {_instanceId}）");
                return;
            }

            // ── 闸门③：指数退避冷却 ────────────────────────────────
            int backoffMs = ComputeRecoveryBackoffMs(_consecutiveRecoveryAttempts);
            double elapsedMs = (DateTime.UtcNow - _lastRecoveryUtc).TotalMilliseconds;
            double remainingMs = backoffMs - elapsedMs;

            if (remainingMs > 0)
            {
                if (_recoveryRetryScheduled)
                {
                    LogService.Info("WebView2.Recovery",
                        $"{reason}：退避冷却中（剩余 {remainingMs:F0}ms），重试已在排，本次不重复排队");
                    return;
                }

                // 先按 double 比大小，再限幅取整 —— 首次恢复时 _lastRecoveryUtc 是
                // DateTime.MinValue，elapsed 是个天文数字，直接 (int) 强转一个负的 double
                // 依赖的是溢出饱和语义，不该让恢复逻辑踩在这种细节上。
                ScheduleRecoveryRetry(reason, recreate, (int)Math.Clamp(remainingMs, 1, int.MaxValue));
                return;
            }

            ExecuteWebViewRecovery(reason, recreate);
        }

        /// <summary>
        /// 退避时长 = 基数 × 2^(已连续自愈次数)，封顶 <see cref="RecoveryBackoffMaxMs"/>。
        /// </summary>
        private static int ComputeRecoveryBackoffMs(int attempts)
        {
            int shifts = Math.Min(attempts, 10); // 卡住移位，避免 long 溢出
            long backoff = (long)RecoveryBackoffBaseMs << shifts;
            return (int)Math.Min(backoff, RecoveryBackoffMaxMs);
        }

        /// <summary>
        /// 冷却期到点后补做一次恢复。
        ///
        /// 用「延时 + 回投递 UI 线程」而不是 DispatcherQueueTimer：
        /// 不必新增一个需要随页面释放的定时器字段，而 <c>_isDisposed</c> 检查足够兜住
        /// 「延时期间页面被 LRU 淘汰」这段窗口。
        /// </summary>
        private void ScheduleRecoveryRetry(string reason, bool recreate, int delayMs)
        {
            _recoveryRetryScheduled = true;

            LogService.Info("WebView2.Recovery",
                $"{reason}：进入退避冷却，{delayMs}ms 后重试" +
                $"（已连续自愈 {_consecutiveRecoveryAttempts}/{MaxConsecutiveRecoveryAttempts} 次）");

            _ = Task.Delay(delayMs).ContinueWith(_ =>
            {
                bool queued = false;
                try
                {
                    queued = DispatcherQueue?.TryEnqueue(() =>
                    {
                        _recoveryRetryScheduled = false;

                        if (_isDisposed)
                        {
                            return;
                        }

                        RequestWebViewRecovery(reason + "（退避后重试）", recreate);
                    }) ?? false;
                }
                catch
                {
                    queued = false;
                }

                if (!queued)
                {
                    // 投递失败（队列已关闭等）时必须自己清标志，
                    // 否则页面永久认为「重试还在排着」，再也不会尝试恢复。
                    _recoveryRetryScheduled = false;
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 真正动手恢复。只在 UI 线程、且过了上面三道闸门后调用。
        /// </summary>
        private void ExecuteWebViewRecovery(string reason, bool recreate)
        {
            _isRecoveringWebView = true;
            _recoveryRetryScheduled = false;
            _consecutiveRecoveryAttempts++;
            _lastRecoveryUtc = DateTime.UtcNow;
            _deferredRecoveryReason = null;
            _deferredRecoveryNeedsRecreation = false;

            try
            {
                if (recreate)
                {
                    RecreateAfterBrowserProcessLoss(reason);
                }
                else if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Reload();
                    _unresponsiveCount = 0;

                    LogService.Info("WebView2.Recovery",
                        $"{reason}：已触发 Reload（第 {_consecutiveRecoveryAttempts}/{MaxConsecutiveRecoveryAttempts} 次自愈）");
                }
                else
                {
                    // 控件还在但内核为空 ⇒ 与重建同一条路：直接把初始化拉起来
                    _isWebViewReady = false;

                    LogService.Warning("WebView2.Recovery",
                        $"{reason}：控件存在但内核为空，直接重新初始化（第 {_consecutiveRecoveryAttempts}/{MaxConsecutiveRecoveryAttempts} 次自愈）");
                    KickWebViewReinitialization();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("WebView2.Recovery", $"{reason}：恢复动作失败", ex);
            }
            finally
            {
                _isRecoveringWebView = false;
            }
        }

        /// <summary>
        /// 浏览器进程整组没了之后的重建：摘掉死控件 → 换新实例 → 重新初始化 → 回原 URL。
        ///
        /// <para>旧代码在 ProcessFailed 的 case 里就地做完这一整套，且只置了
        /// <c>_needsWebViewRecreation = false</c> 而没有重新初始化新控件 —— 结果是
        /// 「换上去的 WebView2 其实从没被拉起」，用户看到的是一块空白。这里把初始化补上，
        /// 并把崩溃现场的 URL 先存进 <see cref="_pendingNavigationUri"/>，恢复完直接导航回去。</para>
        /// </summary>
        private void RecreateAfterBrowserProcessLoss(string reason)
        {
            // 崩溃现场 URL 先存下来：Close 之后 WebView.Source 可能已经读不到
            try
            {
                Uri? dyingUrl = WebView?.Source;
                if (dyingUrl != null && _pendingNavigationUri == null)
                {
                    _pendingNavigationUri = dyingUrl;
                }
            }
            catch
            {
                // 读不到就算了，后面按 _currentShortcut 的路子恢复
            }

            try
            {
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
                }

                WebView?.Close();
            }
            catch (Exception closeEx)
            {
                // 浏览器进程已经没了，摘控件抛异常属于正常（COM 对象是空壳），不能让它挡住重建
                LogService.Warning("WebView2.Recovery",
                    $"关闭失效 WebView 时抛异常（忽略并继续重建）: {closeEx.GetType().Name}: {closeEx.Message}");
            }

            RecreateWebView();
            _needsWebViewRecreation = false;
            _isWebViewReady = false;

            LogService.Warning("WebView2.Recovery",
                $"{reason}：已重建 WebView 控件，重新初始化内核（第 {_consecutiveRecoveryAttempts}/{MaxConsecutiveRecoveryAttempts} 次自愈）");

            KickWebViewReinitialization();
        }

        /// <summary>
        /// 把初始化拉起来，完成后回 UI 线程导航。
        ///
        /// ⚠️ <c>EnsureWebViewInitializedAsync</c> 必须在 UI 线程发起（碰 XAML 与 STA 的
        /// WebView2）；它的续体刻意调度回 UI 线程再导航 —— 官方线程模型写明跨线程
        /// 使用 WebView2 是不安全的。
        /// </summary>
        private void KickWebViewReinitialization()
        {
            _ = EnsureWebViewInitializedAsync().ContinueWith(t =>
            {
                try
                {
                    bool queued = DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_isDisposed)
                        {
                            return;
                        }

                        if (t.IsCompletedSuccessfully)
                        {
                            TryNavigatePendingUri();
                        }
                        else
                        {
                            Exception? root = t.Exception?.GetBaseException();
                            LogService.Error("WebView2.Recovery", "恢复后的内核重新初始化失败",
                                root ?? new InvalidOperationException("初始化任务未成功完成（无异常对象）"));
                        }
                    }) ?? false;

                    if (!queued)
                    {
                        LogService.Warning("WebView2.Recovery", "恢复初始化完成，但投递回 UI 线程失败（队列已关闭？），本次不导航");
                    }
                }
                catch
                {
                    // 恢复路径上的任何异常都到此为止，不许外溢
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 页面回到前台时，补做后台期间欠下的恢复（由 RestoreWebViewFromIdleState 调用）。
        ///
        /// <para>走 <c>INavigationAware.OnNavigatedTo</c> 的切页路径不需要它 —— 那条路径
        /// 自己会消费 <c>_needsWebViewRecreation</c> 完成重建，并在重建时清空待办标记。
        /// 这里专门兜住「窗口收起→再滑出来」那条不做导航的路径。</para>
        /// </summary>
        private void TryRunDeferredRecovery()
        {
            string? reason = _deferredRecoveryReason;
            bool recreate = _deferredRecoveryNeedsRecreation || _needsWebViewRecreation;

            if (reason == null && !recreate)
            {
                return;
            }

            _deferredRecoveryReason = null;
            _deferredRecoveryNeedsRecreation = false;

            // 回前台补做的这一次不计入「连续自愈」退避：后台期间本来就没动作，
            // 而用户现在正等着这块内容出来，直接执行。
            ExecuteWebViewRecovery($"{reason ?? "页面在后台期间内核失效"}（回到前台补做）", recreate);
        }
    }
}
