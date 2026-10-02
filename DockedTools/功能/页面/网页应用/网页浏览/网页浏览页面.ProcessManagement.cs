using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.Web.WebView2.Core;
using System;

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
                string? currentUrl = null;
                try
                {
                    currentUrl = WebView?.CoreWebView2?.Source ?? _pendingNavigationUri?.ToString() ?? "未知";
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
                                // ⭐ 渲染进程退出：优先调用 Reload（需求：3.2.1）
                                System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] 检测到 RenderProcessExited，尝试 Reload");
                                
                                // ⭐ 任务 3.5：设置恢复标志
                                _isRecoveringWebView = true;
                                
                                if (WebView?.CoreWebView2 != null)
                                {
                                    try
                                    {
                                        WebView.CoreWebView2.Reload();
                                        System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ Reload 已调用");
                                        
                                        // 重置无响应计数器（Reload 后重置）
                                        _unresponsiveCount = 0;
                                    }
                                    catch (Exception reloadEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] ❌ Reload 失败: {reloadEx.Message}");
                                        Features.UnifiedCalls.Logging.LogService.Error(
                                            "WebView2.ProcessFailed.Reload",
                                            "渲染进程退出后尝试 Reload 失败",
                                            reloadEx);
                                    }
                                    finally
                                    {
                                        // ⭐ 任务 3.5：恢复结束后重置 guard
                                        _isRecoveringWebView = false;
                                        System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ 恢复标志已重置（RenderProcessExited）");
                                    }
                                }
                                else
                                {
                                    // WebView 不可用，重置标志
                                    _isRecoveringWebView = false;
                                }
                                break;
                            
                            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                                // ⭐ 主浏览器进程退出：标记需要重建，关闭旧 WebView（需求：3.2.2）
                                System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] 检测到 BrowserProcessExited，标记需要重建 WebView");
                                
                                // ⭐ 任务 3.5：设置恢复标志
                                _isRecoveringWebView = true;
                                
                                _needsWebViewRecreation = true;
                                
                                // 关闭旧 WebView（清理资源）
                                try
                                {
                                    if (WebView?.CoreWebView2 != null)
                                    {
                                        WebView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
                                        WebView.Close();
                                        System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ 旧 WebView 已关闭");
                                    }
                                    
                                    // 重新创建 WebView
                                    RecreateWebView();
                                    _needsWebViewRecreation = false;
                                    
                                    System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ WebView 已重建");
                                }
                                catch (Exception recreateEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] ❌ 重建 WebView 失败: {recreateEx.Message}");
                                    Features.UnifiedCalls.Logging.LogService.Error(
                                        "WebView2.ProcessFailed.Recreate",
                                        "主浏览器进程退出后尝试重建 WebView 失败",
                                        recreateEx);
                                }
                                finally
                                {
                                    // ⭐ 任务 3.5：恢复结束后重置 guard
                                    _isRecoveringWebView = false;
                                    System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ 恢复标志已重置（BrowserProcessExited）");
                                }
                                break;
                            
                            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                                // ⭐ 渲染进程无响应：记录次数，连续多次后 reload（需求：3.2.3）
                                _unresponsiveCount++;
                                System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] 检测到 RenderProcessUnresponsive，计数: {_unresponsiveCount}/{MaxUnresponsiveCountBeforeReload}");
                                
                                if (_unresponsiveCount >= MaxUnresponsiveCountBeforeReload)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] 连续无响应达到 {MaxUnresponsiveCountBeforeReload} 次，触发 Reload");
                                    
                                    // ⭐ 任务 3.5：设置恢复标志
                                    _isRecoveringWebView = true;
                                    
                                    if (WebView?.CoreWebView2 != null)
                                    {
                                        try
                                        {
                                            WebView.CoreWebView2.Reload();
                                            System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ Reload 已调用（无响应恢复）");
                                            
                                            // 重置计数器
                                            _unresponsiveCount = 0;
                                        }
                                        catch (Exception reloadEx)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[CoreWebView2_ProcessFailed] ❌ Reload 失败（无响应恢复）: {reloadEx.Message}");
                                            Features.UnifiedCalls.Logging.LogService.Error(
                                                "WebView2.ProcessFailed.UnresponsiveReload",
                                                $"连续无响应 {MaxUnresponsiveCountBeforeReload} 次后尝试 Reload 失败",
                                                reloadEx);
                                        }
                                        finally
                                        {
                                            // ⭐ 任务 3.5：恢复结束后重置 guard
                                            _isRecoveringWebView = false;
                                            System.Diagnostics.Debug.WriteLine("[CoreWebView2_ProcessFailed] ✅ 恢复标志已重置（RenderProcessUnresponsive）");
                                        }
                                    }
                                    else
                                    {
                                        // WebView 不可用，重置标志
                                        _isRecoveringWebView = false;
                                    }
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
    }
}
