using DockedTools.Features.Pages.WebApp.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 网页能力的宿主接管层，做两件事：
/// ① 把网页里的 Web Notification 转投成 Windows 系统通知（toast）；
/// ② 按「网页应用」逐站决定 WebView2 的权限请求（通知 / 地理位置 / 相机 / 麦克风 / 剪贴板）。
///
/// <para><b>为什么改了名字。</b>这个类原本只管通知，叫 WebNotificationBridge。
/// 后来逐站权限从通知扩到五类，继续叫「通知桥接」会让后来人以为相机请求跟这里没关系 ——
/// 名字必须跟着职责走，不然注释写得再细也会被类名带偏。</para>
///
/// <para><b>逐站权限为什么不交给 WebView2 自己弹窗。</b>
/// WebView2 的默认行为是运行时弹自己的许可框。对浏览器合适，对这里不合适：
/// 用户是「安装」了一个网页应用，期望在应用设置里管它有哪些能力，而不是每次被拦一下。
/// 所以受管的五类一律由宿主静默裁决：开了就 Allow，没开就 Default（等同拒绝但不留痕）。</para>
///
/// <para><b>为什么需要这一层（通知部分）。</b>WebView2 内置的那套默认通知 UI 是运行时的自绘窗口，
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
/// <para><b>权限裁决的三条铁律。</b>
/// ① <b>只碰受管的五类</b>：不是受管种类的（传感器、MIDI、本地字体等）原样放行，
///    交给 WebView2 自己处理。顺手把它们也改写成 Allow/Deny 等于无声改掉了别的能力的行为，
///    属于典型的「顺手」，能惹祸就不好了。
/// ② <b>拒绝时不留痕</b>：<c>SavesInProfile</c> 默认 true，会把这次拒绝写进 profile 持久化，
///    而「状态持久化后浏览器可能不再触发 PermissionRequested」—— 一旦拒绝落盘，
///    用户之后在设置里打开开关也救不回来。所以拒绝分支必须显式置 false。
/// ③ <b>枚举名要写对</b>：是 <c>Notifications</c>（复数）、<c>ClipboardRead</c>，
///    写错名字要么编译不过，要么静默放行了别的能力。</para>
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
public static class WebPermissionBridge
{
    /// <summary>通知归属分组的前缀 —— 用来把不同站点的 tag 隔开，见 <see cref="Build"/></summary>
    private const string GroupPrefix = "web:";

    /// <summary>
    /// 本桥发的所有通知在 arguments 里打的标记。将来若接入别的通知来源，
    /// 这条就是认领边界 —— 见 <see cref="ReadOrigin"/>。
    /// </summary>
    private const string ActionValue = "webNotification";

    private const string ActionKey = "action";

    private const string OriginKey = "origin";

    /// <summary>
    /// arguments 里携带的「回执单号」。每一条通知一个，用来在点击时找回它对应的
    /// <see cref="CoreWebView2Notification"/> 与 deferral。
    ///
    /// <para>为什么不用 tag 反查：tag 是网页给的、可以为空、也可以重复
    /// （同一站点连发三条无 tag 通知，键就撞在一起了，第三条会把前两条的回执顶掉）。
    /// 回执单号是我们自己发的，天然唯一。</para>
    /// </summary>
    private const string ReceiptKey = "receipt";

    /// <summary>
    /// 回执的最长等待时间。超过就当作「用户没理会」上报 <c>ReportClosed</c> 并把 deferral 放掉。
    ///
    /// <para>为什么必须有这条兜底：<see cref="Windows.Foundation.Deferral"/> 不 Complete 的话
    /// WebView2 会一直把这起事件挂在未完成状态，属于**泄漏**，不是「晚一点再报」。
    /// 而系统通知被用户划掉 / 过期清除时，宿主这边收不到任何回调
    /// （WindowsAppSDK 没有提供 dismissed 事件），只靠点击回执是清不干净的。</para>
    /// </summary>
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromMinutes(10);

    private static readonly object _registerLock = new();

    private static bool _platformRegistered;

    /// <summary>
    /// 最近一次 <see cref="Attach"/> 时抓到的 WebView2 profile。全应用共用一个
    /// <c>CoreWebView2Environment</c>，所有内核共享同一份 profile，所以抓一份就够
    /// 给 <see cref="ResetSitePermissionsAsync"/> 当句柄用。
    /// </summary>
    private static CoreWebView2Profile? _profile;

    private static readonly object _pendingLock = new();

    /// <summary>已展示、还在等回执的通知。key 是回执单号。</summary>
    private static readonly Dictionary<string, PendingReceipt> _pending = new();

    /// <summary>
    /// 一条已展示通知的回执上下文。
    /// </summary>
    private sealed class PendingReceipt
    {
        /// <summary>发出这条通知的内核。内核被弃用（Detach）时要连带收尾。</summary>
        public CoreWebView2 Core { get; init; } = null!;

        public CoreWebView2Notification Notification { get; init; } = null!;

        public Windows.Foundation.Deferral Deferral { get; init; } = null!;

        public long ShownTicks { get; init; }
    }

    /// <summary>
    /// 通知被点击（应用已经在跑的那条路径）。
    /// 应用没在跑时系统走 COM 激活，由 <c>OnLaunched</c> 那边的
    /// <c>ExtendedActivationKind.AppNotification</c> 分支接 —— 两条最终都汇聚到同一个处理函数。
    /// </summary>
    public static event Action<AppNotificationActivatedEventArgs>? Activated;

    /// <summary>
    /// 读出一条通知的来源 origin；返回 null 表示「这不是本桥发出的通知」，调用方应直接忽略。
    ///
    /// <para>为什么要认领判断：将来若接入别处的通知（备份完成、下载结束之类），
    /// 所有通知都会走同一个 <see cref="Activated"/>。不校验 action 的话，
    /// 一条备份通知被点开也会被当成「跳转到某个网页应用」。</para>
    /// </summary>
    public static string? ReadOrigin(AppNotificationActivatedEventArgs? args)
    {
        if (args?.Arguments is null)
        {
            return null;
        }

        if (!args.Arguments.TryGetValue(ActionKey, out string? action) || action != ActionValue)
        {
            return null;
        }

        return args.Arguments.TryGetValue(OriginKey, out string? origin) ? origin : null;
    }

    /// <summary>
    /// 系统通知平台注册。<c>AppNotificationManager.Show</c> 之前必须先 Register，
    /// 否则 Show 会静默失败 —— 不抛异常、不弹通知、调试输出里也什么都不留。
    ///
    /// <para>⚠️ 调用时机是硬要求：必须排在 <c>AppInstance.GetActivatedEventArgs()</c> <b>之前</b>。
    /// 顺序反了的话通知激活参数会直接丢失 —— 用户点了通知，应用被叫起来了，
    /// 但拿不到任何 arguments，等于点了个寂寞。所以应用入口里这一行被刻意放得很靠前。</para>
    /// </summary>
    public static void EnsurePlatformRegistered()
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
                // 没在跑的时候系统走 COM 激活，落在 OnLaunched 的
                // ExtendedActivationKind.AppNotification 分支上。两条都得有，缺一条就有一段空窗。
                manager.NotificationInvoked += OnNotificationInvoked;
                manager.Register();

                _platformRegistered = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 通知平台注册失败: {ex.Message}");
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
            $"[WebPermissionBridge] 通知被点击: {string.Join(", ", args.Arguments)}");

        // ⭐ 先把回执做掉，再交给订阅方。
        // 顺序反了的话，订阅方里任何一个异常都会让网页永远收不到 click 事件 ——
        // 而这里是 COM 回调边界，异常跨出去有终止进程的风险，所以两边都各包一层。
        try
        {
            if (args.Arguments is not null &&
                args.Arguments.TryGetValue(ReceiptKey, out string? receipt) &&
                !string.IsNullOrEmpty(receipt))
            {
                CompleteReceipt(receipt, clicked: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 回执失败: {ex.Message}");
        }

        try
        {
            Activated?.Invoke(args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 通知订阅方抛异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 给一条已展示的通知下回执：点开了就 <c>ReportClicked</c>，否则 <c>ReportClosed</c>。
    /// 两条都会把 deferral 放掉（<see cref="Windows.Foundation.Deferral.Complete"/>）。
    ///
    /// <para>调用约束（来自 WebView2 IDL，不是猜的）：<c>ReportClicked</c> /
    /// <c>ReportClosed</c> 要求 <c>Handled</c> 为 TRUE <b>且</b> <c>ReportShown</c> 已经跑过。
    /// 本桥里这两个前置都在 <see cref="OnNotificationReceived"/> 里同步满足了。</para>
    ///
    /// <para>⚠️ 只有「应用本来就在跑」这条路径能回执点击：应用没在跑时系统靠 COM 激活
    /// 重启进程，新进程里 <see cref="_pending"/> 是空的（那是另一个进程），
    /// <see cref="CoreWebView2Notification"/> 对象也不存在了。这条是跨平台通知的固有边界，
    /// 不是实现偷懒 —— 真要覆盖就得靠 Service Worker 的 notificationclick，那是持久通知的事。</para>
    /// </summary>
    private static void CompleteReceipt(string receipt, bool clicked)
    {
        PendingReceipt? item;

        lock (_pendingLock)
        {
            if (!_pending.Remove(receipt, out item))
            {
                return;
            }
        }

        try
        {
            if (clicked)
            {
                item.Notification.ReportClicked();
            }
            else
            {
                item.Notification.ReportClosed();
            }
        }
        catch (Exception ex)
        {
            // 内核已经关掉（页面被淘汰 / 进程退出）时 Report* 会抛。
            // 这时网页侧本来也收不到事件了，记一笔就够，不能让它冒出去。
            System.Diagnostics.Debug.WriteLine(
                $"[WebPermissionBridge] Report{(clicked ? "Clicked" : "Closed")} 失败: {ex.Message}");
        }
        finally
        {
            // ⭐ 无论 Report 成功与否都必须 Complete：deferral 不释放就是泄漏，
            // WebView2 会一直把这起事件挂在那里。
            try
            {
                item.Deferral.Complete();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] deferral Complete 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 收掉已经超时的回执，顺带把对应 deferral 放掉。见 <see cref="ReceiptTimeout"/>。
    /// </summary>
    private static void SweepExpiredReceipts()
    {
        List<string>? expired = null;
        long now = Environment.TickCount64;

        lock (_pendingLock)
        {
            foreach (KeyValuePair<string, PendingReceipt> pair in _pending)
            {
                if (now - pair.Value.ShownTicks >= ReceiptTimeout.TotalMilliseconds)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string receipt in expired)
        {
            CompleteReceipt(receipt, clicked: false);
        }
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

        // ⭐ 预热网页应用快照 —— 这不是性能优化，是正确性前提。
        // 桥接的权限决策（IsPermissionAllowedForOrigin）跑在同步回调里、不能 await，
        // 只能查 Store 的内存快照；快照为 null 时会被保守判为「未登记 → 拒绝」。
        // 而快照只在有人调过 LoadAsync 之后才有值，启动时并没有一处无条件预热
        // （导航栏/主页那几处都是事件回调）。若不在这里补一次，冷启动后第一次
        // requestPermission 就会拿到 denied —— 正好复现「开了开关还不行」。
        //
        // 选 Attach 作为时机的理由：它在内核初始化时跑，必然早于页面加载完、
        // JS 调到 requestPermission（中间隔着导航 + 脚本执行，通常几百毫秒），
        // 一次读盘远在这之前就完成了。
        _ = WarmUpStoreAsync();

        // ⭐ 抓一份 profile 句柄给「重置站点权限」用（共享环境，多内核同一份）。
        try
        {
            _profile = core.Profile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 抓取 profile 失败: {ex.Message}");
        }

        core.PermissionRequested -= OnPermissionRequested;
        core.PermissionRequested += OnPermissionRequested;

        core.NotificationReceived -= OnNotificationReceived;
        core.NotificationReceived += OnNotificationReceived;
    }

    /// <summary>
    /// 预热网页应用快照，让同步权限回调能查到数据。见 <see cref="Attach"/> 里的说明。
    ///
    /// <para>刻意做成「发了不管」：预热早一步算赚到，晚一步也只是退回原行为，
    /// 没有任何调用方需要等它的结果。异常必须在这里吞掉 —— 一个没人 await 的
    /// Task 里抛出未观察异常，会在 GC  finalize 时炸进程。</para>
    /// </summary>
    private static async Task WarmUpStoreAsync()
    {
        try
        {
            await WebAppShortcutStore.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 预热网页应用快照失败: {ex.Message}");
        }
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

        // ⚠️ 顺序有讲究：先把这个内核还没回执的通知收掉，再摘事件。
        // 反过来的话，LRU 淘汰内核的那一小段窗口里进来的通知会挂在已弃用的对象上，
        // 而 Detach 之后它们连被清理的机会都没有 —— deferral 就此泄漏。
        List<string>? owned = null;

        lock (_pendingLock)
        {
            foreach (KeyValuePair<string, PendingReceipt> pair in _pending)
            {
                if (ReferenceEquals(pair.Value.Core, core))
                {
                    (owned ??= new List<string>()).Add(pair.Key);
                }
            }
        }

        if (owned is not null)
        {
            foreach (string receipt in owned)
            {
                CompleteReceipt(receipt, clicked: false);
            }
        }

        core.PermissionRequested -= OnPermissionRequested;
        core.NotificationReceived -= OnNotificationReceived;
    }

    /// <summary>
    /// 受本桥接管、由网页应用开关逐站控制的权限种类。
    /// 与 <see cref="WebAppShortcut"/> 上的 <c>*Enabled</c> 字段一一对应。
    /// </summary>
    private enum SitePermission
    {
        Notifications,
        Geolocation,
        Camera,
        Microphone,
        ClipboardRead
    }

    /// <summary>
    /// 受管权限对应的 WebView2 种类，给「重置站点权限」遍历用。
    /// 和 <see cref="ToSitePermission"/> 是同一张表的两个方向，改一处必须两处都改。
    /// </summary>
    private static readonly CoreWebView2PermissionKind[] ManagedKinds =
    {
        CoreWebView2PermissionKind.Notifications,
        CoreWebView2PermissionKind.Geolocation,
        CoreWebView2PermissionKind.Camera,
        CoreWebView2PermissionKind.Microphone,
        CoreWebView2PermissionKind.ClipboardRead
    };

    /// <summary>
    /// WebView2 权限种类 → 受管权限。返回 false 表示「这一项不归我们管」，
    /// 调用方应原样放行，让 WebView2 走它自己的默认处理。
    ///
    /// <para>⚠️ 枚举名必须照抄 WinRT 定义的拼写：<c>Notifications</c> 是复数、
    /// 剪贴板是 <c>ClipboardRead</c>。这里写错名字不会有警告，只会静默放行了别的能力。</para>
    /// </summary>
    private static bool ToSitePermission(CoreWebView2PermissionKind kind, out SitePermission permission)
    {
        switch (kind)
        {
            case CoreWebView2PermissionKind.Notifications:
                permission = SitePermission.Notifications;
                return true;
            case CoreWebView2PermissionKind.Geolocation:
                permission = SitePermission.Geolocation;
                return true;
            case CoreWebView2PermissionKind.Camera:
                permission = SitePermission.Camera;
                return true;
            case CoreWebView2PermissionKind.Microphone:
                permission = SitePermission.Microphone;
                return true;
            case CoreWebView2PermissionKind.ClipboardRead:
                permission = SitePermission.ClipboardRead;
                return true;
            default:
                permission = default;
                return false;
        }
    }

    /// <summary>
    /// 权限请求。<b>只碰受管的五类</b> —— 其余种类（传感器、MIDI、本地字体、自动播放等）
    /// 原样放行。早先这里压根没有订阅者，那些种类一直走的是 WebView2 的默认处理；
    /// 顺手把它们也改写成 Allow/Deny 等于无声地改掉了别的能力的行为，
    /// 属于典型的「顺手」，能惹祸就不好了。
    /// </summary>
    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (!ToSitePermission(args.PermissionKind, out SitePermission permission))
        {
            return;
        }

        // ⭐ 按站点（origin）决定是否放行：只有「登记过且开了这项」的网页应用才允许，
        // 未登记的 origin（第三方 iframe 等）保守拒绝。不再用全局开关。
        bool allowed = IsPermissionAllowedForOrigin(args.Uri, permission);

        // ⚠️ SavesInProfile 默认 true：事件里设的状态会写进 WebView2 profile、跨会话持久化，
        //    并变成该 origin 的默认行为；而且「状态持久化后，浏览器启发式可能不再触发
        //    PermissionRequested 事件」。这正是「开了开关还不行」的头号根因 —— 一旦某个
        //    拒绝被持久化，之后打开开关事件也不再触发，站点永久 denied。
        //    所以：**拒绝时务必不落盘（false），允许时才落盘（true，一次授权长期有效）。**
        args.State = allowed
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Default;
        args.SavesInProfile = allowed;

        System.Diagnostics.Debug.WriteLine(
            $"[WebPermissionBridge] {args.PermissionKind} 权限 {args.Uri} → {args.State} (SavesInProfile={args.SavesInProfile})");
    }

    /// <summary>
    /// 这个 origin 是否允许指定的权限：在已登记的网页应用里找匹配项，看它对应的
    /// <c>*Enabled</c> 字段。找不到（第三方 / 未登记）则拒绝。
    ///
    /// <para>同步查内存快照（<see cref="WebAppShortcutStore.TryGetCachedApps"/>），
    /// 因为权限事件是同步回调、不能 await。快照在应用启动加载快捷方式后就一直是最新的。</para>
    /// </summary>
    private static bool IsPermissionAllowedForOrigin(string? origin, SitePermission permission)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        string? target = AuthorityOf(origin);
        if (target is null)
        {
            return false;
        }

        IReadOnlyList<WebAppShortcut>? apps = WebAppShortcutStore.TryGetCachedApps();
        if (apps is null)
        {
            // 快照还没加载。Attach 里已经发过一次预热，走到这里说明这次请求来得更早
            // （内核初始化后极短时间内就发起了 requestPermission）。
            // 保守拒绝，但不落盘（见 OnPermissionRequested 的 SavesInProfile），
            // 所以下次请求仍会重新走这里，不会把拒绝钉死。
            // ⚠️ 真在日志里看到这行，说明预热还是输了，需要把预热再往前挪。
            System.Diagnostics.Debug.WriteLine(
                $"[WebPermissionBridge] 快照未就绪，{target} 本次保守拒绝（预热未完成）");
            return false;
        }

        foreach (WebAppShortcut app in apps)
        {
            string? appAuthority = AuthorityOf(app.Url);
            if (appAuthority is not null &&
                string.Equals(appAuthority, target, StringComparison.OrdinalIgnoreCase))
            {
                return IsEnabledOn(app, permission);
            }
        }

        return false;
    }

    /// <summary>读出某个网页应用上指定权限的开关值。</summary>
    private static bool IsEnabledOn(WebAppShortcut app, SitePermission permission) => permission switch
    {
        SitePermission.Notifications => app.NotificationsEnabled,
        SitePermission.Geolocation => app.GeolocationEnabled,
        SitePermission.Camera => app.CameraEnabled,
        SitePermission.Microphone => app.MicrophoneEnabled,
        SitePermission.ClipboardRead => app.ClipboardReadEnabled,
        _ => false
    };

    /// <summary>
    /// 把 URL 或 origin 统一规整成 authority（scheme://host[:port]），用于比对。
    /// <c>args.Uri</c> 已经是 origin 形式，<c>app.Url</c> 是完整地址，两者都取 authority 即可对齐。
    /// </summary>
    private static string? AuthorityOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// 清除某个 origin 在 WebView2 profile 里已持久化的<b>全部受管权限</b>决策（Allow 或 Deny），
    /// 恢复成默认行为。用于「开了开关还是不行」——多半是旧决策被持久化卡住了，清掉后
    /// 下一次站点再请求时会重新走桥接决策。
    ///
    /// <para>来源：<c>CoreWebView2Profile.SetPermissionStateAsync(kind, origin, Default)</c>，
    /// 文档明确「设为 Default 会擦掉 profile 里已存的该站点状态」。</para>
    ///
    /// <para>为什么<b>一次清全部</b>而不是按权限分别给按钮：用户的心智是「这个站点记着我了，
    /// 清掉重来」，不是「我只想清相机的那份」。而且 <see cref="ManagedKinds"/> 已经枚举了一切，
    /// 遍历一遍比让用户理解五种权限的持久化差异简单得多。</para>
    ///
    /// <para>返回是否全部清掉了。<b>没清掉时必须如实回报</b>：
    /// profile 只有在 <see cref="Attach"/> 跑过之后才有值，也就是用户至少打开过一次网页浏览页。
    /// 从没打开过时这个句柄是空的，此时说「已清除」纯属撒谎 —— 用户照着提示刷新页面只会再撞一次墙。</para>
    /// </summary>
    public static async Task<bool> ResetSitePermissionsAsync(string origin)
    {
        CoreWebView2Profile? profile = _profile;
        if (profile is null)
        {
            System.Diagnostics.Debug.WriteLine("[WebPermissionBridge] 重置站点权限失败：profile 未就绪（尚未打开过网页浏览页）");
            return false;
        }

        bool allCleared = true;

        // 逐项清，不用 Task.WhenAll：SetPermissionStateAsync 写的是同一个 profile，
        // 并发写同一个底层存储是自找麻烦；而且失败要能定位到具体哪一项。
        foreach (CoreWebView2PermissionKind kind in ManagedKinds)
        {
            try
            {
                await profile.SetPermissionStateAsync(kind, origin, CoreWebView2PermissionState.Default);
            }
            catch (Exception ex)
            {
                allCleared = false;
                System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 重置 {kind} 权限失败: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[WebPermissionBridge] 重置站点权限 {origin}: {(allCleared ? "全部清除" : "部分失败")}");
        return allCleared;
    }

    private static void OnNotificationReceived(CoreWebView2 sender, CoreWebView2NotificationReceivedEventArgs args)
    {
        // ⭐ 按站点决定是否接管：该应用没开通知就交还 WebView2 默认行为（等于拒绝，不弹系统通知）。
        // 不再依赖全局开关。
        if (!IsPermissionAllowedForOrigin(args.SenderOrigin, SitePermission.Notifications))
        {
            // 不置 Handled：交还给 WebView2 的默认通知 UI，行为和没这层桥接时一致。
            // 这里按 GetDeferral 与否都不影响 —— 没有异步工作要做。
            return;
        }

        CoreWebView2Notification notification = args.Notification;

        // ⭐ deferral 必须在这里就取：ReportClicked / ReportClosed 是**异步**发生的
        // （用户可能几分钟后才点），而 IDL 要求 Report* 只能在处理本次事件的过程中调用。
        // 不取 deferral 的话事件在 handler 返回时就结束了，之后补的 Report* 全部
        // 抛 ERROR_INVALID_STATE —— 网页的 onclick / onclose 永远收不到。
        Windows.Foundation.Deferral? deferral = null;

        try
        {
            deferral = args.GetDeferral();

            // ⭐ 必须在调用任何 Report* 之前置 true，否则 Report 抛 ERROR_INVALID_STATE。
            // 而且这个值一经置 true 就撤不回来了。
            args.Handled = true;

            string receipt = Guid.NewGuid().ToString("N");

            // 先把宿主这次处理的「tag 归属」和 Build 的组串在前，和 ApplyAdaptiveBarColour 那道
            // URL 闸门不是一个层面：这里是防止站点 A 的 tag="chat" 顶掉站点 B 的同名 tag。
            AppNotification appNotification = Build(notification, args.SenderOrigin, receipt);

            AppNotificationManager.Default.Show(appNotification);

            // 网页侧的 notification.show 事件靠这个才触发；漏了会让等 show 的页面逻辑卡住。
            notification.ReportShown();

            lock (_pendingLock)
            {
                _pending[receipt] = new PendingReceipt
                {
                    Core = sender,
                    Notification = notification,
                    Deferral = deferral,
                    ShownTicks = Environment.TickCount64
                };
            }

            // 走到这里 deferral 的 ownership 已经交给 _pending，别在 finally 里重复 Complete。
            deferral = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] 转发网页通知失败: {ex.Message}");

            // 失败时必须把 deferral 放掉，否则这起事件永远挂着。
            // 注意：Handled 已经是 true（置了就撤不回来），所以这条通知对网页来说
            // 既没弹出去也没回执 —— 属于最坏情况，宁可记清楚也不要假装成功。
            try
            {
                deferral?.Complete();
            }
            catch (Exception completeEx)
            {
                System.Diagnostics.Debug.WriteLine($"[WebPermissionBridge] deferral Complete 失败: {completeEx.Message}");
            }
        }

        // 顺手清理超时的回执。放在这里而不是起一个定时器：
        // 通知本来就是低频事件，每来一条扫一次够用了，不必为一个兜底动作常驻一个计时器。
        SweepExpiredReceipts();
    }

    private static AppNotification Build(CoreWebView2Notification notification, string origin, string receipt)
    {
        // 网页完全不给标题时拿主机名顶上 —— 系统通知没有「无标题」这种形态，
        // 留空会让用户在通知中心里看到一条不知道来自哪里的空白。
        string title = string.IsNullOrWhiteSpace(notification.Title)
            ? HostOf(origin)
            : notification.Title;

        var builder = new AppNotificationBuilder()
            .AddArgument(ActionKey, ActionValue)
            .AddArgument(OriginKey, origin ?? string.Empty)
            .AddArgument(ReceiptKey, receipt)
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
