using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Xaml;

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

            // ⚠️ 必须走包唤起（MSIX）而不是直接 Process.Start(exe)：
            // 直接跑 exe 会让新实例缺了正确的 AppUserModelID 与完整 Launch 激活，
            // 跟开始菜单/搜索里的那个应用对不上号。非打包运行时会自动降级到跑 exe。
            var launch = DockedTools.Features.AppEntry.PackagedActivationService
                .Launch(string.Join(" ", argsList));

            if (!launch.Success)
            {
                throw new InvalidOperationException($"无法启动新的应用实例：{launch.Detail}");
            }
            
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
            Debug.WriteLine($"重启失败: {ex.Message}");
            throw;
        }
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
