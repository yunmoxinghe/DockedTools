using System;
using System.Threading.Tasks;
using Windows.System;

namespace DockedTools.功能.统一调用.快速反馈;

/// <summary>
/// 快速反馈服务
///
/// 微软表单做的「快速反馈」渠道，托盘菜单和设置页共用这一个入口，
/// 避免同一个 URL 在仓库里散落多处、将来改表单时漏改。
/// </summary>
public static class QuickFeedbackService
{
    /// <summary>
    /// 快速反馈表单短链。
    /// 短链会 302 到 forms.cloud.microsoft/pages/responsepage.aspx?id=... ，
    /// Launcher 跟随重定向由浏览器负责，这里只需要保证短链本身有效。
    /// </summary>
    public const string FormUrl = "https://forms.cloud.microsoft/r/qxmkXUZXmR";

    /// <summary>
    /// 表单地址（预先构造好，避免每次点击都重新解析字符串）
    /// </summary>
    public static Uri FormUri { get; } = new Uri(FormUrl);

    /// <summary>
    /// 打开快速反馈表单
    /// </summary>
    /// <returns>是否成功唤起浏览器</returns>
    public static async Task<bool> LaunchAsync()
    {
        try
        {
            var success = await Launcher.LaunchUriAsync(FormUri);

            System.Diagnostics.Debug.WriteLine(
                $"[QuickFeedback] 打开反馈表单{(success ? "成功" : "失败")}: {FormUrl}");

            return success;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QuickFeedback] 打开反馈表单异常: {ex.Message}");
            return false;
        }
    }
}
