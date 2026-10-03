using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Xaml;
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.功能.统一调用;

/// <summary>
/// 应用重启服务
/// 打包（MSIX）与免打包两种形态下都能正确重开的实现
/// </summary>
public static class AppRestartService
{
    /// <summary>
    /// 重启应用（基础版）
    /// </summary>
    public static void Restart()
    {
        RestartWithArgs("--restart");
    }

    /// <summary>
    /// 带参数重启应用
    /// </summary>
    /// <param name="args">启动参数，例如 "--restart-from=update"</param>
    public static async void RestartWithArgs(params string[] args)
    {
        try
        {
            // 确保包含 --restart 标记（用于绕过单实例检测）
            var argsList = args.ToList();
            if (!argsList.Any(a => a.Contains("--restart")))
            {
                argsList.Insert(0, "--restart");
            }

            var argString = string.Join(" ", argsList);

            // ✅ 交接开始：接班的实例已经（或即将）唤起。从这一刻起，本进程退出时必须跳过
            // NIM_DELETE —— 托盘 GUID 是按包名哈希的，新旧实例算出来是同一个值，
            // 而 NIF_GUID 模式下 explorer 认 GUID 不认进程，删一个等于全删。
            // 详见 SystemTrayIcon.IsHandingOffToSuccessor 的注释。
            // 两条重启路径都必须先置位：
            //   官方路径下进程是被系统终止的，不走 Dispose，但 explorer 会按 hWnd 失效
            //   自行回收登记，不会连累新实例；置位是为了覆盖"置位后 API 却失败"的中间态。
            DockedTools.Features.Tray.SystemTrayIcon.IsHandingOffToSuccessor = true;

            // ── 路径一（首选）：官方 Restart API ──────────────────────
            // Microsoft.Windows.AppLifecycle.AppInstance.Restart —— Windows App SDK 官方
            // 推荐的重启方式（对应最佳实践里的「用 Windows App SDK Restart APIs 管理重启」）。
            // 它是同步的：成功时系统会终止本进程并重新唤起应用，这一行根本不会返回，
            // 也就不会出现「新旧两个进程同时活着」的中间态。
            //
            // ⚠️ 两个关键事实（决定了后面为什么还要留降级路径）：
            //   ① 返回类型是 AppRestartFailureReason，枚举里【没有"成功"值】——
            //      能拿到返回值就说明这次重启失败了。
            //   ② 有限制 NotInForeground：应用必须"可见且在前台"。
            //      我们常年只跑在托盘（可能一个窗口都没有），这种场景必然吃这条。
            try
            {
                var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(argString);
                LogService.Warning(
                    "重启服务",
                    $"官方 Restart API 未生效（{ReasonText(reason)}），降级到手工唤起路径");
            }
            catch (Exception ex)
            {
                LogService.Warning(
                    "重启服务",
                    $"官方 Restart API 调用异常，降级到手工唤起路径：{ex.GetType().Name} {ex.Message}");
            }

            // ── 路径二（降级）：手工唤起新实例 ────────────────────────
            // ⚠️ 必须走包唤起（MSIX）而不是直接 Process.Start(exe)：
            // 直接跑 exe 会让新实例缺了正确的 AppUserModelID 与完整 Launch 激活，
            // 跟开始菜单/搜索里的那个应用对不上号。非打包运行时会自动降级到跑 exe。
            var launch = DockedTools.Features.AppEntry.PackagedActivationService
                .Launch(argString);

            if (!launch.Success)
            {
                throw new InvalidOperationException($"无法启动新的应用实例：{launch.Detail}");
            }

            // IsHandingOffToSuccessor 已在方法开头置位（两条路径共用），这里不再重复设置。
            // 注意 Launch 失败时会走下面的 throw，本进程继续活着 —— 那种情况下标志已经置位了，
            // 但因为新实例压根没起来，本进程仍是托盘图标的唯一持有者；
            // 标志只影响"退出时是否下发 NIM_DELETE"，而退出时必然已有接班者，语义仍然成立。

            // 给新进程一点时间启动，然后再退出旧实例
            // 这样新进程有足够时间获取 Mutex 并初始化资源
            await System.Threading.Tasks.Task.Delay(500);
            
            // ⚠️ 重要：触发应用的正常退出流程，确保托盘图标等资源被正确清理
            // 不能直接调用 Application.Current.Exit()，因为这会跳过清理逻辑
            if (Application.Current is DockedTools.App app)
            {
                // 调用 App 的公开退出方法,确保托盘图标被正确清理
                app.ExitApplicationPublic();
            }
            else
            {
                // 降级处理：如果无法获取 App 实例，直接退出
                Application.Current.Exit();
            }
        }
        catch (Exception ex)
        {
            // ⚠️ 本方法是 async void：这里的 throw 不会有人接得住，会被 ExceptionPolicy
            // 兜底吞掉（策略里未分类异常默认 handled）。落日志是唯一能事后追溯的线索，
            // 否则「点了重启没反应」这类问题查不到任何痕迹。
            LogService.Error("重启服务", "重启失败", ex);
            Debug.WriteLine($"重启失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 把官方 Restart 的失败原因翻成人话，落日志时能直接看懂。
    ///
    /// ⚠️ 类型是 Windows.ApplicationModel.Core.AppRestartFailureReason（WinAppSDK 的
    /// AppInstance.Restart 复用了这个 WinRT 枚举，没有另起一套）。枚举里【没有"成功"值】：
    /// 能拿到返回值本身就说明这次重启没成，所以这里不需要处理成功分支。
    /// 成员经 winapp find-api 对照本项目实际引用的元数据确认：
    ///   RestartPending / NotInForeground / InvalidUser / Other
    /// </summary>
    private static string ReasonText(
        Windows.ApplicationModel.Core.AppRestartFailureReason reason)
    {
        return reason switch
        {
            Windows.ApplicationModel.Core.AppRestartFailureReason.RestartPending
                => "已有重启正在进行（RestartPending）",
            Windows.ApplicationModel.Core.AppRestartFailureReason.NotInForeground
                => "应用不在前台（NotInForeground）",
            Windows.ApplicationModel.Core.AppRestartFailureReason.InvalidUser
                => "当前用户不被允许重启（InvalidUser）",
            Windows.ApplicationModel.Core.AppRestartFailureReason.Other
                => "其他原因（Other）",
            _ => $"未知原因（{(int)reason}）"
        };
    }

    /// <summary>
    /// 延迟重启（给应用时间保存状态）
    /// </summary>
    /// <param name="delayMilliseconds">延迟毫秒数</param>
    /// <param name="onBeforeRestart">重启前的回调（用于保存状态）</param>
    public static async void RestartWithDelay(int delayMilliseconds = 500, Action? onBeforeRestart = null)
    {
        try
        {
            // 执行重启前的操作
            onBeforeRestart?.Invoke();

            // 延迟
            await System.Threading.Tasks.Task.Delay(delayMilliseconds);

            Restart();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"延迟重启失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 检查是否从重启启动
    ///
    /// 走 LaunchArguments 而不是只看命令行：Shell 唤起时参数可能只躺在激活载荷里。
    /// </summary>
    /// <returns>如果是重启启动返回 true</returns>
    public static bool IsRestartedLaunch()
    {
        return DockedTools.Features.AppEntry.LaunchArguments.Contains("--restart");
    }

    /// <summary>
    /// 获取重启来源
    /// </summary>
    /// <returns>重启来源标识，如 "update", "crash", "settings" 等</returns>
    public static string? GetRestartSource()
    {
        var restartArg = DockedTools.Features.AppEntry.LaunchArguments.All
            .FirstOrDefault(arg => arg.StartsWith("--restart-from="));
        
        if (restartArg != null)
        {
            return restartArg.Replace("--restart-from=", "");
        }

        return null;
    }
}
