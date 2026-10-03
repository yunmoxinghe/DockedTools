using System;
using System.Collections.Generic;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 站点栏色记忆：按站点（<c>scheme://host:port</c>）记住上次算出来的栏色。
///
/// <para><b>解决什么</b>：切换 page 时（无论从 LRU 缓存切回还是新建实例），
/// 顶栏/底栏都要等一次性探测——首屏 250ms、迟到采样再 750ms——才有色，
/// 而页面切换动画只有几百毫秒。动画期间露出来的是系统默认色，
/// 等真实取色回来再淡入一次，观感就是「先白一下、再跳成网页色」。</para>
///
/// <para><b>为什么是静态的</b>：颜色必须活过页面实例。页面被 LRU 淘汰后实例和它的
/// 私有字段一起没了，按实例存等于淘汰一次就失忆一次，而「切回一个很久没开的网页」恰恰
/// 是最需要预热的场景。静态字典陪进程活着，页面重建也能命中。</para>
///
/// <para><b>为什么按站点而不是按 URL</b>：同一站点不同路径的外观高度一致，按 origin 命中率
/// 高得多；而预热本来就是「先顶一个大概对的色」，真值 250ms 后到，不同会自己淡过去。
/// 按完整 URL 存则几乎每次都是 miss，白记。</para>
///
/// <para><b>不做老化</b>：记的只是预热值，不是答案。过期了顶多第一帧偏一点，
/// 真实取色会立刻纠正；加过期机制反而要凭空挑一个时长，收益不成比例。</para>
/// </summary>
public static class AdaptiveBarColourMemory
{
    /// <summary>容量上限。一个进程里常用的网页应用通常十几个，64 绰绰有余。</summary>
    private const int Capacity = 64;

    private static readonly object Gate = new();

    private static readonly Dictionary<string, AdaptiveBarColourResult> ByOrigin =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>访问顺序（尾最新），用于满了之后淘汰最久没用的那个。</summary>
    private static readonly LinkedList<string> Order = new();

    /// <summary>
    /// 取某个 URL 所属站点的记忆色。没有记录时返回 null。
    /// </summary>
    public static AdaptiveBarColourResult? TryGet(string? url)
    {
        string? origin = OriginOf(url);
        if (origin is null)
        {
            return null;
        }

        lock (Gate)
        {
            if (!ByOrigin.TryGetValue(origin, out AdaptiveBarColourResult? result))
            {
                return null;
            }

            // 命中也算一次访问：不然一个长期打开、从不重新导航的页面会被别的站点挤掉
            Order.Remove(origin);
            Order.AddLast(origin);
            return result;
        }
    }

    /// <summary>
    /// 记住某个 URL 所属站点的栏色。<paramref name="url"/> 取不到站点（本地页、data: 等）时静默忽略。
    /// </summary>
    public static void Remember(string? url, AdaptiveBarColourResult result)
    {
        string? origin = OriginOf(url);
        if (origin is null)
        {
            return;
        }

        lock (Gate)
        {
            if (ByOrigin.ContainsKey(origin))
            {
                Order.Remove(origin);
            }
            else if (Order.Count >= Capacity && Order.First?.Value is { } oldest)
            {
                Order.RemoveFirst();
                ByOrigin.Remove(oldest);
            }

            ByOrigin[origin] = result;
            Order.AddLast(origin);
        }
    }

    /// <summary>
    /// URL → 站点键。只认 http/https：本地页、<c>data:</c>、<c>about:</c> 这类
    /// 没有「站点」概念，按它们缓存只会让不同文档互相错配。
    /// </summary>
    private static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
