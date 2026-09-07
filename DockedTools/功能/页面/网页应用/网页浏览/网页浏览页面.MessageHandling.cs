using DockedTools.Features.UnifiedCalls.AsyncSafety;
using Microsoft.Web.WebView2.Core;
using System;
using System.Text.Json;
using System.Threading.Tasks;

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

            // 消息处理逻辑已移除（取色功能已删除）
            await Task.CompletedTask;
        }

        // ⚠️ CoreWebView2_DocumentTitleChanged已移至 网页浏览页面.Events.cs
    }
}

