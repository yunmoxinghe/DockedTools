using DockedTools.Features.Pages.WebApp.Browser.Services;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.Settings.WebSettings
{
    /// <summary>
    /// 网页应用详情页 - Cookie 管理模块
    ///
    /// <para>UI 只做三件事：列出、删单条、清空（本站点 / 全部）。
    /// 读写逻辑全在 <see cref="WebAppCookieService"/> —— 那里写清了为什么必须借一个
    /// 活着的 CoreWebView2，以及为什么「清空全部」会波及所有网页应用。</para>
    ///
    /// <para>为什么走 WebViewManager 借内核，而不是在详情页里放一个隐藏的 WebView2：
    /// CoreWebView2CookieManager 没有独立构造入口（Environment 上不存在这类工厂方法），
    /// 为了看一眼 Cookie 就平地拉起一个浏览器进程，代价完全不成比例。
    /// 借不到的时候宁可显示「先打开一次该网页应用」，也不要偷偷建进程。</para>
    /// </summary>
    public sealed partial class WebAppDetailPage
    {
        /// <summary>
        /// 列表已加载过一次。 Expanded 只自动加载一次，之后靠「刷新」按钮 ——
        /// 用户在页面里来回开合的时候不该每次都跨进程问一遍。
        /// </summary>
        private bool _cookieLoadedOnce;

        private IReadOnlyList<WebAppCookieItem> _cookieItems = Array.Empty<WebAppCookieItem>();

        private async void OnDeleteCookieClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: WebAppCookieItem item })
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = await WebAppCookieService.DeleteAsync(
                    _originalUrl ?? string.Empty, _appId, item);

                ShowCookieHint(result.Ok
                    ? string.Format(
                        Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieCleared"),
                        result.Affected)
                    : result.Error ?? string.Empty);

                await LoadCookiesAsync();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private async void OnCookieExpanderExpanded(object sender, object e)
        {
            if (_cookieLoadedOnce)
            {
                return;
            }

            await LoadCookiesAsync();
        }

        private async void OnRefreshCookieClicked(object sender, RoutedEventArgs e)
            => await LoadCookiesAsync();

        private async void OnClearSiteCookieClicked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_originalUrl))
            {
                ShowCookieHint(Localization.LocalizationHelper.GetString("WebAppDetail_CookieBadUrl"));
                return;
            }

            bool confirmed = await ConfirmAsync(
                Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieClearSiteTitle"),
                Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieClearSiteContent"));

            if (!confirmed)
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = await WebAppCookieService.DeleteForUrlAsync(_originalUrl, _appId);
                if (result.Ok)
                {
                    ShowCookieHint(string.Format(
                        Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieCleared"),
                        result.Affected));
                }
                else
                {
                    ShowCookieHint(result.Error ?? string.Empty);
                }

                await LoadCookiesAsync();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private async void OnClearAllCookieClicked(object sender, RoutedEventArgs e)
        {
            bool confirmed = await ConfirmAsync(
                Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieClearAllTitle"),
                Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieClearAllContent"));

            if (!confirmed)
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = WebAppCookieService.DeleteAll(_appId);
                ShowCookieHint(result.Ok
                    ? Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieClearedAll")
                    : result.Error ?? string.Empty);

                await LoadCookiesAsync();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private async Task LoadCookiesAsync()
        {
            if (CookieListView is null)
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = await WebAppCookieService.ListAsync(
                    _originalUrl ?? string.Empty, _appId);

                _cookieLoadedOnce = result.Ok;

                if (!result.Ok)
                {
                    _cookieItems = Array.Empty<WebAppCookieItem>();
                    CookieListView.ItemsSource = _cookieItems;
                    UpdateCookieCount(0);
                    ShowCookieHint(result.Error ?? string.Empty);
                    return;
                }

                _cookieItems = result.Items;
                CookieListView.ItemsSource = _cookieItems;
                UpdateCookieCount(result.Items.Count);

                ClearCookieHint();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private void UpdateCookieCount(int count)
        {
            CookieCountText.Text = string.Format(
                Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieCount"),
                count);
        }

        private void SetCookieBusy(bool busy)
        {
            CookieLoadingRing.IsActive = busy;
            CookieListView.Opacity = busy ? 0.5 : 1.0;
        }

        private void ShowCookieHint(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ClearCookieHint();
                return;
            }

            CookieHintText.Text = message;
            CookieHintText.Visibility = Visibility.Visible;
        }

        private void ClearCookieHint()
        {
            CookieHintText.Visibility = Visibility.Collapsed;
            CookieHintText.Text = string.Empty;
        }

        /// <summary>
        /// 统一的二次确认弹窗。
        /// 删除 Cookie 等于踢掉登录态，属于「用户点错了会肉疼」的操作 —— 必须拦一道。
        /// 「清除全部」尤其要拦：所有网页应用共用一个 profile，这一下会连带清掉别人。
        /// </summary>
        private async Task<bool> ConfirmAsync(string title, string content)
        {
            var dialog = new UnifiedInAppDialog();
            dialog.Configure(
                title,
                content,
                primaryButtonText: Localization.LocalizationHelper.GetString("WebAppDetailPage_CookieConfirmPrimary"),
                closeButtonText: Localization.LocalizationHelper.GetString("Common_Cancel"));

            ContentDialogResult? result = await InAppDialogService.ShowAsync(dialog, this);

            return result == ContentDialogResult.Primary;
        }
    }
}
