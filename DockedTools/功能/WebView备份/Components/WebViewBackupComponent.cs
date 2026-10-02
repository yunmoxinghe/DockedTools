using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DockedTools.功能.WebView备份.Services;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace DockedTools.功能.WebView备份.Components;

/// <summary>
/// WebView2 配置备份管理组件（Reactor 实现）
/// 使用 Microsoft.UI.Reactor 声明式 UI 框架
/// 
/// 功能：
/// - 备份整个 UserDataFolder 为 ZIP（可自定义保存位置）
/// - 恢复配置（从备份列表选择）
/// - 查看备份历史（时间、大小、快速操作）
/// - 智能错误处理和用户引导
/// 
/// 用户体验优化：
/// - 清晰的状态提示和进度反馈
/// - 详细的错误说明和解决方案
/// - 便捷的文件管理操作
/// 
/// 最佳实践来源：
/// https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder
/// </summary>
public class WebViewBackupComponent : Component
{
    // ── 官方主题资源键（走 Theme.Ref 交给框架跟随亮暗，不写死颜色）──
    // 键名取自 WASDK 2.5.1 依赖的 Microsoft.WindowsAppSDK.WinUI 2.3.9 的 generic.xaml
    // （本项目 Microsoft.WindowsAppSDK 2.5.1 ⇒ WinUI 2.3.9）。
    // 卡片描边：官方卡片/内容层描边色，亮暗两套值。
    private const string TokenCardStroke = "CardStrokeColorDefaultBrush";
    // 状态文字色：成功 / 失败 / 进行中，对应原硬编码的 #4CAF50 / #F44336 / #FF9800。
    private const string TokenStatusSuccess = "SystemFillColorSuccessBrush";
    private const string TokenStatusCritical = "SystemFillColorCriticalBrush";
    private const string TokenStatusCaution = "SystemFillColorCautionBrush";

    public override Element Render()
    {
        var (status, setStatus) = UseState("就绪");
        var (backups, setBackups) = UseState<string[]>(Array.Empty<string>());
        var (isLoading, setIsLoading) = UseState(false);

        var backupService = new WebViewBackupServiceV2();

        // 组件加载时刷新备份列表
        UseEffect(() =>
        {
            _ = Task.Run(async () => await RefreshBackupsAsync());
        }, Array.Empty<object>());

        async Task RefreshBackupsAsync()
        {
            try
            {
                var list = await backupService.ListBackupsAsync();
                setBackups(list);
                setStatus($"找到 {list.Length} 个备份");
            }
            catch (Exception ex)
            {
                setStatus($"刷新失败：{ex.Message}");
            }
        }

        async Task BackupAsync()
        {
            if (!GlobalWebViewService.HasActiveWebView)
            {
                setStatus("❌ 未检测到活跃的 WebView2 - 请先打开网页应用");
                return;
            }

            var userDataFolder = GlobalWebViewService.CurrentWebView2?.Environment.UserDataFolder;
            if (string.IsNullOrEmpty(userDataFolder))
            {
                setStatus("❌ 无法获取数据目录");
                return;
            }

            if (!Directory.Exists(userDataFolder))
            {
                setStatus("❌ 数据目录不存在，请先使用网页应用功能");
                return;
            }

            setIsLoading(true);
            setStatus("🔄 正在备份配置，请稍候...");

            try
            {
                var backupName = "WebView配置";
                var zipPath = await backupService.BackupUserDataFolderAsync(userDataFolder, backupName);
                var fileInfo = new FileInfo(zipPath);

                setStatus($"✅ 备份成功！大小：{fileInfo.Length / 1024 / 1024:F2} MB | 位置：{zipPath}");
                await RefreshBackupsAsync();
            }
            catch (IOException ex) when (ex.Message.Contains("被使用") || ex.Message.Contains("being used"))
            {
                setStatus($"❌ 备份失败：文件被占用。请关闭所有网页应用窗口后重试。");
            }
            catch (IOException ex) when (ex.Message.Contains("空间不足") || ex.Message.Contains("not enough space"))
            {
                setStatus($"❌ 备份失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                setStatus($"❌ 备份失败：{ex.Message}");
            }
            finally
            {
                setIsLoading(false);
            }
        }

        async Task RestoreAsync(string zipPath)
        {
            if (!GlobalWebViewService.HasActiveWebView)
            {
                setStatus("❌ 未检测到活跃的 WebView2 - 请先打开网页应用");
                return;
            }

            var userDataFolder = GlobalWebViewService.CurrentWebView2?.Environment.UserDataFolder;
            if (string.IsNullOrEmpty(userDataFolder))
            {
                setStatus("❌ 无法获取数据目录");
                return;
            }

            setIsLoading(true);
            setStatus("🔄 正在恢复配置，请稍候...");

            try
            {
                await backupService.RestoreUserDataFolderAsync(zipPath, userDataFolder);
                setStatus("✅ 恢复成功！请重启应用以使更改生效");
            }
            catch (IOException ex) when (ex.Message.Contains("被使用") || ex.Message.Contains("being used"))
            {
                setStatus($"❌ 恢复失败：文件被占用。请关闭所有网页应用窗口后重试。");
            }
            catch (IOException ex) when (ex.Message.Contains("损坏") || ex.Message.Contains("invalid"))
            {
                setStatus($"❌ 恢复失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                setStatus($"❌ 恢复失败：{ex.Message}");
            }
            finally
            {
                setIsLoading(false);
            }
        }

        async Task DeleteAsync(string zipPath)
        {
            try
            {
                await backupService.DeleteBackupAsync(zipPath);
                setStatus($"✅ 已删除备份");
                await RefreshBackupsAsync();
            }
            catch (Exception ex)
            {
                setStatus($"❌ 删除失败：{ex.Message}");
            }
        }

        return VStack(
            // 标题区
            TextBlock("WebView2 配置备份与恢复")
                .FontSize(28)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),

            TextBlock("💡 备份所有 WebView2 数据（登录状态、缓存、Cookie、LocalStorage 等），支持快速迁移和数据保护")
                .Foreground(Theme.SecondaryText)
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap),

            // 信息卡片
            Border(
                VStack(
                    TextBlock($"数据目录：{GlobalWebViewService.CurrentWebView2?.Environment.UserDataFolder ?? "未检测到 WebView2"}")
                        .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap),
                    TextBlock($"备份位置：{backupService.GetBackupDirectory()}")
                        .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap),
                    TextBlock(status)
                        .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap)
                        .Foreground(
                            status.StartsWith("✅") ? Theme.Ref(TokenStatusSuccess) :
                            status.StartsWith("❌") ? Theme.Ref(TokenStatusCritical) :
                            Theme.Ref(TokenStatusCaution))
                ).Padding(16).Spacing(8)
            )
            .Background(Theme.SubtleFill)
            // ⚠️ 不能写成 .Set(b => b.BorderBrush = ...) / .Set(b => b.BorderThickness = ...)：
            // Reactor 的 Border 走对象池，归池时这两个属性会被重置，.Set() 里写的值在
            // 下一次重渲染就丢了（表现：卡片灰边框莫名消失）＝编译警告 REACTOR_POOL_001。
            // WithBorder 是 render 期修饰器，每次渲染都按同一份声明写回，天然自愈。
            // 画刷走 ThemeRef 而非写死 Colors.Gray：官方卡片描边色随亮暗主题变化。
            .WithBorder(Theme.Ref(TokenCardStroke), 1)
            .CornerRadius(8),

            // 操作按钮
            HStack(
                Button("💾 创建备份", async () => await BackupAsync())
                    .IsEnabled(!isLoading && GlobalWebViewService.HasActiveWebView),

                Button("🔄 刷新列表", async () => await RefreshBackupsAsync())
                    .IsEnabled(!isLoading),

                Button("📁 打开备份文件夹", () =>
                {
                    var backupDir = backupService.GetBackupDirectory();
                    if (Directory.Exists(backupDir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", backupDir);
                        setStatus($"已打开：{backupDir}");
                    }
                })
                    .IsEnabled(!isLoading),

                When(isLoading, () =>
                    ProgressRing()
                        .IsActive(true)
                        .Width(24)
                        .Height(24)
                )
            ).Spacing(12),

            // 备份列表
            TextBlock("备份历史")
                .FontSize(20)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),

            When(backups.Length == 0, () =>
                TextBlock("暂无备份")
                    .Foreground(Theme.SecondaryText)
            ),

            When(backups.Length > 0, () =>
                ScrollView(
                    VStack(
                        backups.Select(backup =>
                        {
                            var fileName = Path.GetFileName(backup);
                            var fileInfo = new FileInfo(backup);

                            return Border(
                                VStack(
                                    TextBlock(fileName)
                                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                                    
                                    HStack(
                                        TextBlock($"创建时间：{fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss}")
                                            .FontSize(12)
                                            .Foreground(Theme.SecondaryText),
                                        
                                        TextBlock($"大小：{fileInfo.Length / 1024 / 1024:F2} MB")
                                            .FontSize(12)
                                            .Foreground(Theme.SecondaryText)
                                    ).Spacing(16),

                                    HStack(
                                        Button("恢复", async () => await RestoreAsync(backup))
                                            .IsEnabled(!isLoading),
                                        
                                        Button("删除", async () => await DeleteAsync(backup))
                                            .IsEnabled(!isLoading)
                                    ).Spacing(12)
                                ).Padding(16).Spacing(12)
                            )
                            .Background(Theme.SubtleFill)
                            // 同上方信息卡片：走 WithBorder 而非 .Set(...)，边框才不会在池回收后丢失
                            .WithBorder(Theme.Ref(TokenCardStroke), 1)
                            .CornerRadius(8)
                            .WithKey(backup);
                        }).ToArray()
                    ).Spacing(8)
                ).MaxHeight(400)
            )

        ).Padding(24).Spacing(24);
    }
}
