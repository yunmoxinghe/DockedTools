using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.Logging;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// 网页应用相关的桥接命令
    ///
    /// 【为什么走 Store 而不是事件总线】
    /// 页面上的「新建」按钮走的是 WebAppEventBus.PublishShortcutCreated，
    /// 但那个方法只发事件、不落盘，真正 SaveAsync 的是导航栏。
    /// 也就是说主窗口没起来时会静默丢数据。桥接是后台通道，
    /// 必须直接读写 WebAppShortcutStore，不能依赖任何 UI。
    ///
    /// 【去重策略】
    /// 沿用 EdgeBookmarkSync 的做法：按 URL 做 OrdinalIgnoreCase 去重，命中就返回已有条目。
    ///
    /// 【UI 刷新】
    /// 落盘后通过 BridgeConfig.RequestUiRefresh 通知界面，该委托由应用入口在 UI 线程注入，
    /// 内部会 TryEnqueue 回 UI 线程，避免后台线程直接碰 UI 集合。
    ///
    /// 【并发】
    /// 串行化的活已经下沉到 WebAppShortcutStore 里了（进程级 Gate + UpdateAsync 原子读改写），
    /// 这里不再自己持锁——否则 UI 侧（导航栏 / Edge 同步 / 删除服务）走 Store 时
    /// 和桥接侧的锁是两把，照样能打架。现在谁都走同一把。
    /// </summary>
    public static class WebAppBridgeCommands
    {
        /// <summary>列出所有网页应用</summary>
        public static async Task<JsonElement?> ListAsync()
        {
            var shortcuts = await WebAppShortcutStore.LoadAsync();

            var items = shortcuts
                .Select(s => new WebAppItemPayload
                {
                    Id = s.Id,
                    Name = s.Name,
                    Url = s.Url,
                    HasIcon = s.IconBytes is { Length: > 0 }
                })
                .ToList();

            return JsonSerializer.SerializeToElement(
                items, BridgeJsonContext.Default.ListWebAppItemPayload);
        }

        /// <summary>新增网页应用，URL 重复时返回已有条目</summary>
        public static async Task<JsonElement?> AddAsync(AddWebAppRequest? request)
        {
            // 参数校验不碰 Store，别让非法请求去排队抢锁
            if (request is null || string.IsNullOrWhiteSpace(request.Url))
            {
                throw new BridgeCommandException("INVALID_ARGUMENT", "缺少 url");
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new BridgeCommandException("INVALID_URL", $"不是合法的 http(s) 地址: {request.Url}");
            }

            var canonicalUrl = uri.AbsoluteUri;

            // 一次持锁完成「读 → 去重判断 → 写」，中间不会被 UI 侧或另一个桥接请求插队。
            // 返回 null 列表 = 不改盘（命中去重时）。
            var result = await WebAppShortcutStore.UpdateAsync<AddWebAppResult>(all =>
            {
                var list = all.ToList();

                var existing = list.FirstOrDefault(
                    s => string.Equals(s.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    LogService.Info(BridgeConfig.ModuleName, $"URL 已存在，跳过新增: {canonicalUrl}");

                    return (null, new AddWebAppResult
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        Duplicate = true
                    });
                }

                var name = string.IsNullOrWhiteSpace(request.Name) ? uri.Host : request.Name;
                byte[]? iconBytes = null;

                if (!string.IsNullOrWhiteSpace(request.IconBase64))
                {
                    try
                    {
                        iconBytes = Convert.FromBase64String(request.IconBase64);
                    }
                    catch (FormatException)
                    {
                        throw new BridgeCommandException("INVALID_ICON", "iconBase64 不是合法的 base64");
                    }
                }

                var shortcut = new WebAppShortcut(
                    Guid.NewGuid().ToString("N"),
                    name,
                    canonicalUrl,
                    iconBytes);

                list.Add(shortcut);

                LogService.Info(BridgeConfig.ModuleName, $"已新增网页应用: {name} ({canonicalUrl})");

                return (list, new AddWebAppResult
                {
                    Id = shortcut.Id,
                    Name = shortcut.Name,
                    Duplicate = false
                });
            });

            // 刷新界面放在锁外：它要 TryEnqueue 回 UI 线程，持着 Store 的锁等 UI 是找死锁
            if (!result.Duplicate)
            {
                NotifyUiRefresh();
            }

            return JsonSerializer.SerializeToElement(result, BridgeJsonContext.Default.AddWebAppResult);
        }

        private static void NotifyUiRefresh()
        {
            var refresh = BridgeConfig.RequestUiRefresh;
            if (refresh is null)
            {
                return;
            }

            try
            {
                refresh();
            }
            catch (Exception ex)
            {
                // 刷新失败不该让已经落盘的数据丢掉，只记日志
                LogService.Error(BridgeConfig.ModuleName, "通知界面刷新失败", ex);
            }
        }
    }
}
