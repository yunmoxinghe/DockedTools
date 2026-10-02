using DockedTools.Features.Pages.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 把网页里的 Web Notification 转投成 Windows 系统通知（toast）。
///
/// <para><b>为什么需要这一层。</b>WebView2 内置的那套默认通知 UI 是运行时的自绘窗口，
/// 不属于系统通知中心：不会进「通知和操作」历史、不跟随系统的免打扰 / 专注助手、
/// 应用退出后也一并消失。宿主接管之后改走 <c>Microsoft.Windows.AppNotifications</c>，
/// 才算是真正落到系统里。</para>
///
/// <para><b>两条必须同时做的事。</b>
/// ① 权限：以前 <c>PermissionRequested</c> 根本不会为 <c>PermissionKind.Notification</c> 触发，
///    现在会了，而且<b>留空等同于拒绝</b>（<c>State.Default</c> 在通知这条上算 deny）。
///    所以网页调 <c>Notification.requestPermission()</c> 拿到的永远是 denied ——
///    必须显式置 <c>Allow</c>，否则后面那条事件一次也收不到。
/// ② 接管：置 <c>args.Handled = true</c> 才取消 WebView2 的默认 UI。
///    置了之后宿主就得负责把「已展示」告诉网页 —— 不调 <see cref="CoreWebView2Notification.ReportShown"/>
///    的话，页面的 <c>show</c> 事件不会触发，那些等着 notification.show 再继续的逻辑会卡住。
///    反过来也一样：Handled 还是 false 时调 Report* 会直接抛
///    <c>HRESULT_FROM_WIN32(ERROR_INVALID_STATE)</c>。</para>
///
/// <para><b>关于持久通知（Service Worker 发出的那种）。</b>
/// 早期的设计方案里提到 CoreWebView2Profile 上会有一条 <c>NotificationReceived</c>，
/// 用来接持久通知；但截至本机依赖的 WebView2 1.0.3719.77，Profile 的成员里并没有这条事件
/// （只有 Deleted），<b>官方 API 文档也没有列出它</b>。
/// 所以这里只接 CoreWebView2.NotificationReceived —— 也就是非持久通知那一类。
/// 真要支持持久通知，得等到 API 落地再说，不要先写一份编译不过的代码占位置。</para>
///
/// <para>参考：
/// https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2notification
/// https://learn.microsoft.com/windows/apps/windows-app-sdk/notifications/app-notifications/app-notifications-quickstart
/// </para>
/// </summary>
public static class WebNotificationBridge
{
    /// <summary>通知归属分组的前缀 —— 用来把不同站点的 tag 隔开，见 <see cref="Build"/></summary>
    private const string GroupPrefix = "web:";

    private static readonly object _registerLock = new();

    private static bool _platformRegistered;

    /// <summary>
    /// 系统通知平台注册。<c>AppNotificationManager.Show</c> 之前必须先 Register，
    /// 否则 Show 会静默失败 —— 不抛异常、不弹通知、调试输出里也什么都不留。
    /// </summary>
    private static void EnsurePlatformRegistered()
    {
        if (_platformRegistered)
        {
            return;
        }

        lock (_registerLock)
        {
            if (_platformRegistered)
            {
                return;
            }

            try
            {
                AppNotificationManager manager = AppNotificationManager.Default;

                // NotificationInvoked 只在【应用已经在跑】的时候触发；
                // 没在跑的时候系统会按 OnLaunched 的激活路径重启进程。
                // 后一条目前还没接，先在这里把控制权交给未来的实现，不假装已经处理干净。
                manager.NotificationInvoked += OnNotificationInvoked;
                manager.Register();

                _platformRegistered = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebNotificationBridge] 通知平台注册失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 事件会因应用未运行而丢失的那部分内容在 arguments 里都带着：
    /// action / origin / title / body 原样回填，将来接了 OnLaunched 就能直接还原。
    /// </summary>
    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[WebNotificationBridge] 通知被点击: {string.Join(", ", args.Arguments)} " +
            "(⚠️ 尚未接 OnLaunched 激活路径，仅在应用已运行时生效)");
    }

    /// <summary>
    /// 给一个内核实例挂上通知桥接。重复调用安全（先减后加）。
    /// </summary>
    /// <param name="core">目标内核实例。页面重建 / 内核重投必须重新调用一次。</param>
    public static void Attach(CoreWebView2 core)
    {
        if (core is null)
        {
            return;
        }

        EnsurePlatformRegistered();

        core.PermissionRequested -= OnPermissionRequested;
        core.PermissionRequested += OnPermissionRequested;

        core.NotificationReceived -= OnNotificationReceived;
        core.NotificationReceived += OnNotificationReceived;
    }

    /// <summary>
    /// 摘掉桥接。内核实例被弃用时调用，避免把回调挂在已经关掉的对象上。
    /// </summary>
    public static void Detach(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        core.PermissionRequested -= OnPermissionRequested;
        core.NotificationReceived -= OnNotificationReceived;
    }

    /// <summary>
    /// 权限请求。<b>只碰通知这一项</b> —— 其余种类原样放行。
    /// 早先这里压根没有订阅者，那些种类一直走的是 WebView2 的默认处理；
    /// 顺手把它们也改写成 Allow/Deny 等于无声地改掉了相机、麦克风、剪贴板的行为，
    /// 属于典型的「顺手」，能惹祸就不好了。
    /// </summary>
    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        // ⚠️ 枚举值是 Notifications（复数），不是 Notification —— 写错版本的名称只会拿到
        // 一个编译错误还好，写成别的 kind 就变成静默放行别的权限了。
        if (args.PermissionKind != CoreWebView2PermissionKind.Notifications)
        {
            return;
        }

        args.State = ExperimentalSettings.WebNotificationsEnabled
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;

        System.Diagnostics.Debug.WriteLine(
            $"[WebNotificationBridge] 通知权限 {args.Uri} → {args.State}");
    }

    private static void OnNotificationReceived(CoreWebView2 sender, CoreWebView2NotificationReceivedEventArgs args)
    {
        if (!ExperimentalSettings.WebNotificationsEnabled)
        {
            // 不置 Handled：交还给 WebView2 的默认通知 UI，行为和没这层桥接时一致。
            // 这里按 GetDeferral 与否都不影响 —— 没有异步工作要做。
            return;
        }

        CoreWebView2Notification notification = args.Notification;

        try
        {
            // ⭐ 必须在调用任何 Report* 之前置 true，否则 Report 抛 ERROR_INVALID_STATE。
            // 而且这个值一经置 true 就撤不回来了。
            args.Handled = true;

            // 先把宿主这次处理的「tag 归属」和 Build 的组串在前，和 ApplyAdaptiveBarColour 那道
            // URL 闸门不是一个层面：这里是防止站点 A 的 tag="chat" 顶掉站点 B 的同名 tag。
            AppNotification appNotification = Build(notification, args.SenderOrigin);

            AppNotificationManager.Default.Show(appNotification);

            // 网页侧的 notification.show 事件靠这个才触发；漏了会让等 show 的页面逻辑卡住。
            notification.ReportShown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebNotificationBridge] 转发网页通知失败: {ex.Message}");
        }
    }

    private static AppNotification Build(CoreWebView2Notification notification, string origin)
    {
        // 网页完全不给标题时拿主机名顶上 —— 系统通知没有「无标题」这种形态，
        // 留空会让用户在通知中心里看到一条不知道来自哪里的空白。
        string title = string.IsNullOrWhiteSpace(notification.Title)
            ? HostOf(origin)
            : notification.Title;

        var builder = new AppNotificationBuilder()
            .AddArgument("action", "webNotification")
            .AddArgument("origin", origin ?? string.Empty)
            .AddText(title);

        if (!string.IsNullOrWhiteSpace(notification.Body))
        {
            builder.AddText(notification.Body);
        }

        // ⭐ SetGroup 不是为了分组显示好看：tag 在整个应用范围内生效，
        // 「站点 A 的 tag=chat」会直接替换掉「站点 B 的 tag=chat」。
        // 带上 origin 打成分组前缀，才能各扫门前雪。
        // 这个坑和自适应取色那条「不同 page 串色」同源 —— 一份宿主 UI 被多个来源共享时，
        // 任何共享命名的东西都要带上来源标识。
        builder.SetGroup(GroupPrefix + (string.IsNullOrEmpty(origin) ? "unknown" : origin));

        if (!string.IsNullOrEmpty(notification.Tag))
        {
            builder.SetTag(notification.Tag);
        }

        if (TryWebUri(notification.IconUri, out Uri? icon))
        {
            builder.SetAppLogoOverride(icon!, AppNotificationImageCrop.Circle);
        }
        else if (TryWebUri(notification.BadgeUri, out Uri? badge))
        {
            builder.SetAppLogoOverride(badge!, AppNotificationImageCrop.Circle);
        }

        if (TryWebUri(notification.BodyImageUri, out Uri? bodyImage))
        {
            builder.SetInlineImage(bodyImage!);
        }

        if (notification.IsSilent)
        {
            builder.MuteAudio();
        }

        if (notification.RequiresInteraction)
        {
            builder.SetDuration(AppNotificationDuration.Long);
        }

        return builder.BuildNotification();
    }

    /// <summary>
    /// 只接受 http/https 的图片地址。
    /// <c>ms-appx</c> 之类的本地 scheme 本就不该出现在网页给的地址里，而 <c>file://</c>
    /// 在打包应用的通知里加载不出来 —— 传过去的结果是整条通知渲染失败，比不放图更糟。
    /// </summary>
    private static bool TryWebUri(string? value, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        bool isWeb = parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps;
        if (!isWeb)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string HostOf(string? origin)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed) && !string.IsNullOrEmpty(parsed.Host))
        {
            return parsed.Host;
        }

        return "网页应用";
    }
}
