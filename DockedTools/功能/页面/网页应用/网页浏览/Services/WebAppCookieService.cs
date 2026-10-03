using DockedTools.Features.Pages.WebApp.Browser.Managers;
using Microsoft.UI.Xaml.Data;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 网页应用 Cookie 管理（在「网页管理」页里查看 / 删除某个网页应用留下的 Cookie）。
///
/// <para><b>头号前提：本项目里所有网页应用共享同一份 Cookie。</b>
/// 页面建内核走的是 <c>CoreWebView2Environment.CreateWithOptionsAsync(userDataFolder: null)</c>
/// （见 网页浏览页面.WebView.cs），不传 userDataFolder 意味着用进程默认的用户数据目录，
/// 于是所有 CoreWebView2 落在同一个 profile 上，<b>Cookie 是全局一罐</b>。
/// 后果有两条，UI 文案必须照这个说：
/// ① A 站点的 SSO Cookie 会出现在同源（或同一父域）的 B 站点里，这不是 bug；
/// ② 「清空全部」会把<b>所有</b>网页应用的登录态全部清掉 —— 不只是当前这一个。</para>
///
/// <para><b>为什么必须借一个活着的 CoreWebView2。</b>
/// CoreWebView2CookieManager 只能从 <c>CoreWebView2.CookieManager</c> 或
/// <c>CoreWebView2Profile.CookieManager</c> 拿到；<b>CoreWebView2Environment 上没有任何
/// 返回 CookieManager 的成员</b>（官方 API 面里查过，不存在 CreateCoreWebView2CookieManagerAsync
/// 这类东西），所以不存在「不建 WebView 直接读 Cookie」的正规途径。
/// Cookie 躺在 profile 目录里的 SQLite 里，但那是实现细节，官方不支持直连读取。
/// 于是：优先借用目标应用自己的内核，借用不到就退而借用任意一个活着的内核 ——
/// 反正它们看的是同一罐 Cookie。</para>
///
/// <para>参考：https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2cookiemanager</para>
/// </summary>
/// <summary>
/// 列表展示用的一条 Cookie（不保留 Value —— 展示 Cookie 明文既没必要也不安全）。
///
/// <para>刻意不做成 WebAppCookieService 的嵌套类：WinUI 的 <c>x:DataType</c>
/// 认不了嵌套类型，而 DataTemplate 里不标 x:DataType 就会刷一屏 WMC1510 裁剪警告。
/// 放到命名空间顶层，详情页那边才能按习惯写法把 <c>{Binding}</c> 也纳入静态解析。</para>
/// </summary>
[Bindable]
public sealed class WebAppCookieItem
{
    // ⚠️ 这里必须是 set 而不是 init：[Bindable] + DataTemplate 的 x:DataType 会让 XAML 编译器
    //    给每个属性生成一份 set_ 访问器，而 init-only 属性在生成代码里赋值会直接炸成 CS8852
    //    （报错落在 obj 下的 XamlTypeInfo.g.cs，很难一眼看出是这里的属性写法引起的）。
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public bool IsSession { get; set; }
    public bool IsHttpOnly { get; set; }
    public bool IsSecure { get; set; }
    public DateTimeOffset? Expires { get; set; }

    /// <summary>列表里显示的过期时间；会话 Cookie 显示为「本次会话结束」</summary>
    public string ExpiresText => IsSession
        ? Localization.LocalizationHelper.GetString("WebAppDetail_CookieExpiresSession")
        : Expires?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            ?? Localization.LocalizationHelper.GetString("WebAppDetail_CookieExpiresUnknown");
}

/// <summary>一次 Cookie 操作的统一结果</summary>
public sealed class WebAppCookieResult
{
    public bool Ok { get; init; }
    public IReadOnlyList<WebAppCookieItem> Items { get; init; } = Array.Empty<WebAppCookieItem>();
    public int Affected { get; init; }
    public string? Error { get; init; }

    public static WebAppCookieResult Fail(string error) => new() { Ok = false, Error = error };

    public static WebAppCookieResult Success(IReadOnlyList<WebAppCookieItem> items)
        => new() { Ok = true, Items = items, Affected = items.Count };

    public static WebAppCookieResult Done(int affected)
        => new() { Ok = true, Affected = affected };
}

/// <summary>
/// 网页应用 Cookie 管理（在「网页管理」页里查看 / 删除某个网页应用留下的 Cookie）。
///
/// <para><b>头号前提：本项目里所有网页应用共享同一份 Cookie。</b>
/// 页面建内核走的是 <c>CoreWebView2Environment.CreateWithOptionsAsync(userDataFolder: null)</c>
/// （见 网页浏览页面.WebView.cs），不传 userDataFolder 意味着用进程默认的用户数据目录，
/// 于是所有 CoreWebView2 落在同一个 profile 上，<b>Cookie 是全局一罐</b>。
/// 后果有两条，UI 文案必须照这个说：
/// ① A 站点的 SSO Cookie 会出现在同源（或同一父域）的 B 站点里，这不是 bug；
/// ② 「清空全部」会把<b>所有</b>网页应用的登录态全部清掉 —— 不只是当前这一个。</para>
///
/// <para><b>为什么必须借一个活着的 CoreWebView2。</b>
/// CoreWebView2CookieManager 只能从 <c>CoreWebView2.CookieManager</c> 或
/// <c>CoreWebView2Profile.CookieManager</c> 拿到；<b>CoreWebView2Environment 上没有任何
/// 返回 CookieManager 的成员</b>（官方 API 面里查过，不存在 CreateCoreWebView2CookieManagerAsync
/// 这类东西），所以不存在「不建 WebView 直接读 Cookie」的正规途径。
/// Cookie 躺在 profile 目录里的 SQLite 里，但那是实现细节，官方不支持直连读取。
/// 于是：优先借用目标应用自己的内核，借用不到就退而借用任意一个活着的内核 ——
/// 反正它们看的是同一罐 Cookie。</para>
///
/// <para>参考：https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2cookiemanager</para>
/// </summary>
public static class WebAppCookieService
{
    /// <summary>
    /// 用户主动拉起来的临时内核（见 <see cref="RegisterTemporaryCore"/>）。
    /// 一个网页应用都没开时，本来是无内核可借、只能提示用户先去打开；
    /// 有了它就能就地读 Cookie，代价是后台常驻一个浏览器进程 —— 所以只由用户
    /// 显式点击才创建，绝不自动拉起。
    /// </summary>
    private static CoreWebView2? _temporaryCore;

    /// <summary>
    /// 登记一个临时内核，供后续 Cookie 操作借用。
    /// </summary>
    public static void RegisterTemporaryCore(CoreWebView2 core)
    {
        _temporaryCore = core;
    }

    /// <summary>
    /// 临时内核是否已就位且还能用。
    /// 内核对象非 null 不代表底层 COM 还活着（浏览器进程可能已经崩了），
    /// 所以探的时候顺手摸一下 CookieManager —— 拿不到就当没有。
    /// </summary>
    public static bool HasTemporaryCore()
    {
        if (_temporaryCore is null)
        {
            return false;
        }

        try
        {
            return _temporaryCore.CookieManager is not null;
        }
        catch
        {
            _temporaryCore = null;
            return false;
        }
    }

    /// <summary>
    /// 枚举「访问某个 URL 时会带上」的 Cookie。
    /// </summary>
    /// <param name="url">网页应用的 URL</param>
    /// <param name="instanceId">网页应用实例 Id（优先借用它自己的内核）</param>
    public static async Task<WebAppCookieResult> ListAsync(string url, string? instanceId)
    {
        if (!TryResolveManager(instanceId, out CoreWebView2CookieManager? manager, out string? error))
        {
            return WebAppCookieResult.Fail(error!);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return WebAppCookieResult.Fail(
                Localization.LocalizationHelper.GetString("WebAppDetail_CookieBadUrl"));
        }

        try
        {
            // GetCookiesAsync 本身就按 URL 做匹配（含 Domain 规则，.foo.com / foo.com 是不同域），
            // 拿回来的就是「这个站点真正会用上的那些」，不需要本地再按域名过滤一遍。
            //
            // ⚠️ 这个 await 期间借来的内核有可能正好被 LRU 淘汰（淘汰是异步低优先级执行的）。
            // 恢复时 COM 对象已废，抛出来的会是看不懂的 HRESULT —— 由 <see cref="TranslateFailure"/>
            // 统一翻译成「没有可用的内核」。
            IReadOnlyList<CoreWebView2Cookie> cookies = await manager!.GetCookiesAsync(url);

            var items = cookies
                .Select(ToItem)
                .OrderBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return WebAppCookieResult.Success(items);
        }
        catch (Exception ex)
        {
            return TranslateFailure(ex, instanceId, "枚举 Cookie");
        }
    }

    /// <summary>
    /// 删除单条 Cookie。
    /// 用 <c>DeleteCookiesWithDomainAndPath</c> 而不是 <c>DeleteCookies(name, uri)</c>：
    /// 后者靠 uri 反推域与路径，遇到跨域共享的父域 Cookie（.example.com）会漏删。
    ///
    /// <para>⚠️ 这里返回 <c>Task</c> 但<b>刻意不 Task.Run</b>，别「顺手优化」成后台线程：
    /// <c>CoreWebView2CookieManager</c> 是 CoreWebView2 的子对象，WebView2 并没有承诺它是
    /// agile object，跨线程调用没有官方保证（典型失败是 <c>RPC_E_WRONG_THREAD</c>）。
    /// 而删除是毫秒级的同步 COM 调用，留在调用方线程上做完既安全又简单。
    /// 方法因此不带 <c>async</c> —— 带上是假签名（CS1998），会误导调用方以为不会占用当前线程。</para>
    /// </summary>
    public static Task<WebAppCookieResult> DeleteAsync(
        string? instanceId, WebAppCookieItem item)
    {
        return Task.FromResult(DeleteCore(instanceId, item));
    }

    private static WebAppCookieResult DeleteCore(string? instanceId, WebAppCookieItem item)
    {
        if (!TryResolveManager(instanceId, out CoreWebView2CookieManager? manager, out string? error))
        {
            return WebAppCookieResult.Fail(error!);
        }

        try
        {
            manager!.DeleteCookiesWithDomainAndPath(item.Name, item.Domain, item.Path);
            return WebAppCookieResult.Done(1);
        }
        catch (Exception ex)
        {
            return TranslateFailure(ex, instanceId, "删除 Cookie");
        }
    }

    /// <summary>
    /// 删除访问某个 URL 时会带上的全部 Cookie。逐条删，好给出准确的条数 ——
    /// 一次性 API 没有返回值，「删了没有」只能猜。
    /// </summary>
    public static async Task<WebAppCookieResult> DeleteForUrlAsync(string url, string? instanceId)
    {
        WebAppCookieResult listed = await ListAsync(url, instanceId);
        if (!listed.Ok)
        {
            return listed;
        }

        if (!TryResolveManager(instanceId, out CoreWebView2CookieManager? manager, out string? error))
        {
            return WebAppCookieResult.Fail(error!);
        }

        try
        {
            int count = 0;
            foreach (WebAppCookieItem item in listed.Items)
            {
                manager!.DeleteCookiesWithDomainAndPath(item.Name, item.Domain, item.Path);
                count++;
            }

            return WebAppCookieResult.Done(count);
        }
        catch (Exception ex)
        {
            return TranslateFailure(ex, instanceId, "清空站点 Cookie");
        }
    }

    /// <summary>
    /// 清空整个 profile 的 Cookie。
    /// <b>⚠️ 这会把所有网页应用的登录态一起清掉</b> —— 见类注释第 ① 条：
    /// 它们共用一个 user data folder。UI 上必须二次确认。
    /// </summary>
    public static WebAppCookieResult DeleteAll(string? instanceId)
    {
        if (!TryResolveManager(instanceId, out CoreWebView2CookieManager? manager, out string? error))
        {
            return WebAppCookieResult.Fail(error!);
        }

        try
        {
            manager!.DeleteAllCookies();
            return WebAppCookieResult.Done(0);
        }
        catch (Exception ex)
        {
            return TranslateFailure(ex, instanceId, "清空全部 Cookie");
        }
    }

    /// <summary>
    /// 把一次 Cookie 操作失败翻译成能给用户看的话。
    ///
    /// <para>关键在于区分两种失败：
    /// ① <b>借来的内核在操作期间被淘汰了</b> —— 抛出来的是 HRESULT / COMException，
    ///    直接显示成「未指定的错误 (0x8000FFFF)」对用户毫无意义；
    /// ② <b>真的失败了</b>（比如 URL 不合法、磁盘写不进去）。
    ///
    /// 判据很朴素：失败后再探一次有没有活着的内核。探不到就是 ①，
    /// 回报「先打开一次网页应用」那条文案；还探得到就是 ②，如实显示原始消息。</para>
    ///
    /// <para>为什么不在操作前把内核「顶到 LRU 最近使用端」来防这个：
    /// 借用走的是 <c>TryPeekCore</c> 而不是 <c>TryGet</c>，刻意不去动 LRU 顺序 ——
    /// 一次 Cookie 查询不配把某个实例顶成「最近使用」，那可能把真正该保住的页面挤掉。</para>
    /// </summary>
    private static WebAppCookieResult TranslateFailure(Exception ex, string? instanceId, string what)
    {
        System.Diagnostics.Debug.WriteLine($"[WebAppCookieService] {what}失败: {ex.Message}");

        bool stillAlive =
            (!string.IsNullOrEmpty(instanceId) && WebViewManager.TryPeekCore(instanceId!, out _)) ||
            WebViewManager.TryPeekAnyCore() is not null;

        return WebAppCookieResult.Fail(stillAlive
            ? ex.Message
            : Localization.LocalizationHelper.GetString("WebAppDetail_CookieNoWebView"));
    }

    /// <summary>
    /// 找到一个能用的 CookieManager。
    ///
    /// 找不到时返回的错误会对用户说「先打开一次该网页应用」 —— 这是当前唯一诚实的说法：
    /// 没有活着的 CoreWebView2 就读不到 Cookie（见类注释），
    /// 也没有轻量办法临时造一个（那要平地起一个浏览器进程）。
    /// </summary>
    private static bool TryResolveManager(
        string? instanceId,
        out CoreWebView2CookieManager? manager,
        out string? error)
    {
        manager = null;
        error = null;

        CoreWebView2? core = null;

        if (!string.IsNullOrEmpty(instanceId)
            && WebViewManager.TryPeekCore(instanceId!, out CoreWebView2? own))
        {
            core = own;
        }
        else
        {
            // 借用任意一个活着的内核：同一个 user data folder 下 Cookie 是共享的（见类注释），
            // 用谁的内核对结果没有任何影响。
            core = WebViewManager.TryPeekAnyCore();
        }

        // 一个网页应用都没开（也就没有内核可借）时，退到用户主动拉起的临时内核。
        // 排在最后：现成内核零成本，临时内核是要养一个浏览器进程的。
        core ??= _temporaryCore;

        if (core is null)
        {
            error = Localization.LocalizationHelper.GetString("WebAppDetail_CookieNoWebView");
            return false;
        }

        // 内核对象还活着不代表底层 COM 还能用 —— 页面被 Dispose / 浏览器进程重启之后，
        // 拿 CookieManager 会直接抛。这里先探一次，把「拿不到」在进面的地方变成一条可显示的错误。
        try
        {
            manager = core.CookieManager;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebAppCookieService] 取 CookieManager 失败: {ex.Message}");
            error = Localization.LocalizationHelper.GetString("WebAppDetail_CookieNoWebView");
            return false;
        }

        if (manager is null)
        {
            error = Localization.LocalizationHelper.GetString("WebAppDetail_CookieNoWebView");
            return false;
        }

        return true;
    }

    private static WebAppCookieItem ToItem(CoreWebView2Cookie cookie) => new()
    {
        Name = cookie.Name,
        Domain = cookie.Domain,
        Path = cookie.Path,
        IsSession = cookie.IsSession,
        IsHttpOnly = cookie.IsHttpOnly,
        IsSecure = cookie.IsSecure,
        Expires = cookie.IsSession || cookie.Expires <= 0
            ? null
            : DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires)
    };
}
