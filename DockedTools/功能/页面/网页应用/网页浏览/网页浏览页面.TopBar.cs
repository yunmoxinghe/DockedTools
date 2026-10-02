using System;
using System.Collections.Generic;
using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 顶部栏管理模块
    /// 2026-10-02：顶栏换成 Reactor 版 AppTopBar，本模块从"自己拼 UI"改为"下发顶栏状态"：
    /// 只提供标题文本/图标路径与一组按钮数据，外观与动画全部交给 AppTopBar。
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        // 左/右两侧映射按钮的固定 Id（用来让服务把 Clicked 事件分派回来）
        private const string LeftMappingButtonId = "__browser_left_mapping";
        private const string RightMappingButtonId = "__browser_right_mapping";
        private const string UnpinButtonId = "__browser_unpin";

        /// <summary>
        /// 更新居中位。新契约下页面给出的是【标题数据】而不是 UIElement：
        /// 只有文本 + 可选的位图/字形图标，其余（省略号、图标压到 16px、换字动画）
        /// 全部由 <c>AppTopBar</c> 保证。
        /// </summary>
        private void PublishTopBarCenter()
        {
            // 本方法会被异步回调（网页标题变化、图标落盘完成）触发，那时本页可能【已经切走】，
            // 顶栏的写入目标是新前台页 —— 照发下去会永久覆盖人家那份 state 里的标题。
            // 文本照常记在 _topBarTitleText 上：回到本页时作用域重新上台会带着它，不会丢。
            if (!TopAppBarService.IsWritingTarget(this))
            {
                return;
            }

            var icon = string.IsNullOrEmpty(_topBarIconPath)
                ? null
                : TopBarCenterIcon.OfBitmap(_topBarIconPath);
            TopAppBarService.SetTitle(_topBarTitleText, icon);
        }

        private void SetupTopBar()
        {
            // 标题 / 图标已由各自的 setter 写进 _topBarTitleText / _topBarIconPath，
            // 这里统一汇总成一份快照下发
            PublishTopBarCenter();

            SetupLeftMappingButton();
            SetupRightContent();

            TopAppBarService.IsVisible = true;
            // 沉浸式：不要顶栏自己的底衬，让网页内容直接顶到顶栏下沿
            TopAppBarService.SetChromeVisible(false);
        }

        private void UpdateTopBarContent()
        {
            // 优先用网页标题，回落为快捷方式名 / URL 的 Host
            if (_currentShortcut != null)
            {
                var documentTitle = WebView?.CoreWebView2?.DocumentTitle;
                _topBarTitleText = !string.IsNullOrWhiteSpace(documentTitle)
                    ? documentTitle
                    : string.IsNullOrWhiteSpace(_currentShortcut.Name)
                        ? (_pendingNavigationUri?.Host ?? _currentShortcut.Url)
                        : _currentShortcut.Name;

                PublishTopBarCenter();
            }

            if (_currentShortcut?.IconBytes is { Length: > 0 } iconBytes)
            {
                _ = PublishShortcutIconAsync(iconBytes);
            }
        }

        private void SetupRightContent()
        {
            // 右侧 = 映射按钮（可选）+ 关闭按钮（可选）。
            // 以前这里要手搓两个 Button 的背景/圆角/悬浮色，现在只发数据。
            //
            // 先重建右侧映射按钮的数据（它只填 _rightMappingButton 与回调，
            // 真正的下发在本方法末尾统一做一次），否则 _rightMappingButton 会一直是旧值/null。
            SetupRightMappingButton();

            var buttons = new List<TopBarButton>();

            if (_rightMappingButton is { } right)
            {
                buttons.Add(right);
            }

            if (!ExperimentalSettings.HideWebViewCloseButton)
            {
                // 与旧版同一个字形（取消固定/关闭），避免换图标造成观感变化
                buttons.Add(TopBarButton.Of(UnpinButtonId, "\uE733", "关闭"));
                TopAppBarService.RegisterAction(UnpinButtonId, () => CloseButton_Click(null!, null!));
            }

            TopAppBarService.SetRightButtons(buttons.Count > 0 ? buttons : null);
        }
    }
}
