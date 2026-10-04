using DockedTools.Features.MainWindow.KeyboardManagement;
using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.Web.WebView2.Core;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.System;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 消息处理模块
    /// 包含WebView消息接收、解析、处理逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        /// <summary>
        /// ⭐ 任务 6.3：CoreWebView2_WebMessageReceived 事件入口（委托到异步实现）
        /// </summary>
        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            AsyncSafety.Run(
                async () => await CoreWebView2WebMessageReceivedAsync(sender, e),
                "WebBrowserPage",
                "WebMessageReceived");
        }

        /// <summary>
        /// ⭐ 任务 6.3：CoreWebView2_WebMessageReceived 异步实现
        /// </summary>
        private async Task CoreWebView2WebMessageReceivedAsync(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            // 快捷键上报先拦一道：这条路径要抢在别的消息处理前面，越早交回主窗口越好。
            // 焦点在网页里时 XAML 的 KeyboardAccelerator / PreviewKeyDown 收不到按键，
            // 页面里注入的常驻脚本是唯一还能把按键交回宿主的通道。
            if (TryHandleShortcutMessage(json))
            {
                return;
            }

            // 消息处理逻辑已移除（取色功能已删除）
            await Task.CompletedTask;
        }

        // ⚠️ CoreWebView2_DocumentTitleChanged已移至 网页浏览页面.Events.cs

        // ==================== 焦点在网页里时的快捷键回传 ====================

        /// <summary>
        /// 快捷键上报脚本的消息类型标记（与脚本里的 type 字段对应）
        /// </summary>
        private const string ShortcutMessageType = "dtShortcut";

        /// <summary>
        /// 把快捷键上报脚本装到内核上。
        ///
        /// 【为什么必须有这一段】
        /// 焦点落在 WebView2 里时，按键被 Chromium 吃掉后不会回传给 XAML 输入路由，
        /// XAML 层的 KeyboardAccelerator / PreviewKeyDown 根本收不到（WinUI WebView2 的已知行为，
        /// microsoft-ui-xaml#6231）。所以这一层加多少订阅都修不好，只能从页面内部上报。
        ///
        /// 【不是浏览器加速键表的问题 —— 实测结论】
        /// 曾经以为 Ctrl+1~9 / Ctrl+Tab / Ctrl+D 是被 Chromium 的浏览器加速键表
        /// （SELECT_TAB_0..7 / SELECT_NEXT_TAB / BOOKMARK_PAGE）吃掉的，于是把
        /// AreBrowserAcceleratorKeysEnabled 关掉了。实测（WebView2 Runtime 154，OS 级真实按键）：
        ///   · 加速键表开启时，Ctrl+1 / Ctrl+9 / Ctrl+D / Ctrl+Tab 照样到达页面的 capture 阶段；
        ///   · 同一轮里 F5 在开启时被浏览器吃掉并触发刷新、关闭后到达页面（阳性对照成立）。
        /// 说明这几个快捷键根本不在 WebView2 的加速键表里，关掉那张表对它们零帮助，
        /// 只会白白废掉网页自己的 Ctrl+F / Ctrl+P / F5。所以那一刀已经撤回。
        ///
        /// 【时序约束】
        /// AddScriptToExecuteOnDocumentCreated 只对注入之后才创建的文档生效，
        /// 所以必须赶在首次导航之前注入（调用点在 WebView.cs 的 EnsureWebViewInitializedAsync）。
        /// </summary>
        private async Task EnsureShortcutScriptInstalledAsync(CoreWebView2 core)
        {
            // 脚本随内核走，同一个内核重复注入没有意义（文档级脚本会越堆越多）
            if (ReferenceEquals(_shortcutScriptCore, core))
            {
                return;
            }

            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
                _shortcutScriptCore = core;
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 快捷键上报脚本已注入");
            }
            catch (Exception ex)
            {
                // 注入失败不该拖垮页面初始化，最坏结果是退回现状：焦点在网页里时快捷键无效
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 快捷键上报脚本注入失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 常驻快捷键上报脚本。
        ///
        /// 【刻意不 preventDefault】
        /// 只上报、不拦截：网页自己的按键逻辑（比如在线编辑器接 Ctrl+D）不受影响，
        /// 宿主这边的动作由主窗口的快捷键管理器决定要不要执行。
        ///
        /// 【为什么用 capture 阶段】
        /// 页面很可能在冒泡阶段 stopPropagation，挂在 window 的 capture 阶段是最早能拿到的位置。
        /// </summary>
        private const string ShortcutScript =
@"(function () {
  if (window.__dtShortcutInstalled) { return; }
  window.__dtShortcutInstalled = true;
  function post(payload) {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify(payload));
      }
    } catch (e) { }
  }
  window.addEventListener('keydown', function (ev) {
    if (ev.repeat) { return; }
    if (!ev.ctrlKey && !ev.altKey) { return; }
    post({
      type: 'dtShortcut',
      code: ev.code || '',
      key: ev.key || '',
      ctrl: !!ev.ctrlKey,
      alt: !!ev.altKey,
      shift: !!ev.shiftKey
    });
  }, true);
})();";

        /// <summary>
        /// 解析页面上报的快捷键消息并转交主窗口的快捷键管理器。
        /// </summary>
        /// <param name="json">WebMessage 原始字符串</param>
        /// <returns>命中快捷键消息返回 true（无论最终有没有执行动作）</returns>
        private bool TryHandleShortcutMessage(string json)
        {
            // 别的消息（取色等）不做 JSON 解析，先用廉价的字符串判断挡掉
            if (!json.Contains(ShortcutMessageType, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                    !string.Equals(typeElement.GetString(), ShortcutMessageType, StringComparison.Ordinal))
                {
                    return false;
                }

                string code = root.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.GetString() ?? string.Empty
                    : string.Empty;
                string keyName = root.TryGetProperty("key", out JsonElement keyElement)
                    ? keyElement.GetString() ?? string.Empty
                    : string.Empty;
                bool ctrl = root.TryGetProperty("ctrl", out JsonElement ctrlElement) && ctrlElement.GetBoolean();
                bool alt = root.TryGetProperty("alt", out JsonElement altElement) && altElement.GetBoolean();
                bool shift = root.TryGetProperty("shift", out JsonElement shiftElement) && shiftElement.GetBoolean();

                if (!TryMapVirtualKey(code, keyName, out VirtualKey key))
                {
                    return false;
                }

                // WebMessage 回调不保证在 UI 线程，切标签 / 改侧边栏都是 UI 操作，必须回 UI 线程
                Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
                if (dispatcher is null)
                {
                    return false;
                }

                dispatcher.TryEnqueue(() =>
                {
                    KeyboardShortcutManager.Current?.TryHandleShortcut(key, ctrl, alt, shift);
                });

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 快捷键消息解析失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 把页面上报的按键映射成 WinUI 的 VirtualKey。
        /// </summary>
        /// <param name="code">KeyboardEvent.code（物理键位，首选）</param>
        /// <param name="keyName">KeyboardEvent.key（兜底）</param>
        /// <param name="key">映射结果</param>
        /// <returns>能映射到宿主快捷键返回 true</returns>
        /// <remarks>
        /// 【为什么 key 也要参与】
        /// code 是物理键位、不受布局与输入法影响，本来最理想；但它不是每次都有值 ——
        /// 实测里合成事件、部分虚拟键盘 / 远程桌面下 code 会是空串，
        /// 光靠 code 就整个失效。所以 code 优先、key 兜底，两条路都留着。
        ///
        /// 【为什么要先挡掉纯修饰键】
        /// 单独按下 Ctrl / Alt / Shift 时也会触发 keydown（ev.ctrlKey 为真，
        /// 脚本的准入条件拦不住），实测里页面确实上报了 key="Control" 的事件。
        /// 不挡掉的话单按一下 Ctrl 就会命中一次快捷键。
        /// </remarks>
        private static bool TryMapVirtualKey(string code, string keyName, out VirtualKey key)
        {
            key = VirtualKey.None;

            switch (keyName)
            {
                case "Control":
                case "Shift":
                case "Alt":
                case "Meta":
                case "AltGraph":
                case "CapsLock":
                    return false;
            }

            switch (code)
            {
                case "Digit1": key = VirtualKey.Number1; return true;
                case "Digit2": key = VirtualKey.Number2; return true;
                case "Digit3": key = VirtualKey.Number3; return true;
                case "Digit4": key = VirtualKey.Number4; return true;
                case "Digit5": key = VirtualKey.Number5; return true;
                case "Digit6": key = VirtualKey.Number6; return true;
                case "Digit7": key = VirtualKey.Number7; return true;
                case "Digit8": key = VirtualKey.Number8; return true;
                case "Digit9": key = VirtualKey.Number9; return true;
                case "Tab": key = VirtualKey.Tab; return true;
                case "KeyD": key = VirtualKey.D; return true;
            }

            // code 为空 / 未命中时用 key 兜底。数字键不受 Shift 与布局影响，最稳；
            // 字母键在开了 Shift 时会变成大写，两个大小写都认。
            switch (keyName)
            {
                case "1": key = VirtualKey.Number1; return true;
                case "2": key = VirtualKey.Number2; return true;
                case "3": key = VirtualKey.Number3; return true;
                case "4": key = VirtualKey.Number4; return true;
                case "5": key = VirtualKey.Number5; return true;
                case "6": key = VirtualKey.Number6; return true;
                case "7": key = VirtualKey.Number7; return true;
                case "8": key = VirtualKey.Number8; return true;
                case "9": key = VirtualKey.Number9; return true;
                case "Tab": key = VirtualKey.Tab; return true;
                case "d":
                case "D": key = VirtualKey.D; return true;
            }

            return false;
        }
    }
}
