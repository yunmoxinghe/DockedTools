using DockedTools.Features.Localization;
using DockedTools.Features.Pages.WebApp.Browser.Services;
using DockedTools.Features.Pages.WebApp.Shared;
using DockedTools.Features.UnifiedCalls.InAppDialog;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.Settings.WebSettings
{
    /// <summary>
    /// 网页应用 Cookie 管理子页面。
    ///
    /// <para>为什么独立成页而不是塞在详情页的展开器里：SettingsExpander 的动态列表
    /// 在本项目（PublishAot + CsWinRT）上跑不通 —— 内部 ItemsRepeater 只认 WinRT
    /// 容器，而 Cookie 项是纯 CLR 类型，拿不到对应的泛型实例化。独立成页之后，
    /// 列表就是一个普通 ItemsControl（直接摊在页面滚动区里，不套第二层滚动），
    /// 那套限制全部消失。</para>
    ///
    /// <para>读写逻辑全在 <see cref="WebAppCookieService"/> —— 那里写清了为什么必须借
    /// 一个活着的 CoreWebView2，以及为什么「清空全部」会波及所有网页应用。</para>
    /// </summary>
    public sealed partial class WebAppCookiePage : Page
    {
        private readonly 智能标题 _智能标题 = new();

        /// <summary>
        /// Cookie 列表的数据源 —— 挂在 CookieItemsControl.ItemsSource 上。
        /// 用 ObservableCollection 是因为删单条 / 刷新后要做增量更新，而不是整表替换。
        /// </summary>
        private readonly ObservableCollection<WebAppCookieItem> _cookieItemSource = new();

        private string? _appId;

        /// <summary>
        /// 网页应用的 URL。清「本站点」Cookie 和按 URL 过滤列表都要用它，
        /// 所以进页面时先按 appId 反查出来存着。
        /// </summary>
        private string _originalUrl = string.Empty;

        /// <summary>
        /// 当前提示文案。独立存一份而不是读控件属性，是因为加载中要临时换成
        /// 「正在读取」，读完还得把原来的提示放回去。
        /// </summary>
        private string _cookieHint = string.Empty;
        private InfoBarSeverity _cookieHintSeverity = InfoBarSeverity.Informational;

        public WebAppCookiePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _智能标题.Setup(this, PageScrollViewer, PageTitleBlock);

            if (e.Parameter is string appId)
            {
                _appId = appId;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _智能标题.Cleanup();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await ResolveAppUrlAsync();
            await LoadCookiesAsync();
        }

        /// <summary>
        /// 按 appId 反查网页应用的 URL。
        /// Cookie 是按站点存的，「清除本站点」和列表过滤都得有准确 URL，
        /// 而导航参数只传了 appId（跟详情页保持一致），所以这里补查一次。
        /// </summary>
        private async Task ResolveAppUrlAsync()
        {
            if (string.IsNullOrEmpty(_appId))
            {
                return;
            }

            IReadOnlyList<WebAppShortcut> shortcuts = await WebAppShortcutStore.LoadAsync();
            WebAppShortcut? app = shortcuts.FirstOrDefault(s => s.Id == _appId);

            _originalUrl = app?.Url ?? string.Empty;
        }

        private async void OnDeleteCookieClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: WebAppCookieItem item })
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = await WebAppCookieService.DeleteAsync(_appId, item);

                ShowCookieHint(
                    result.Ok
                        ? string.Format(
                            LocalizationHelper.GetString("WebAppDetailPage_CookieCleared"),
                            result.Affected)
                        : result.Error ?? string.Empty,
                    result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);

                await LoadCookiesAsync();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        /// <summary>
        /// 手动拉一个「空」内核，专门用来读 Cookie。
        ///
        /// <para>为什么需要这个按钮：Cookie 只能挂在活着的 CoreWebView2 上，而本进程
        /// 一个网页应用都没开时就没有内核可借，只能提示用户先去打开。多数时候这够用，
        /// 但「就想看一眼 Cookie」时不该被逼着先开个网页 —— 于是给用户一条明确的
        /// 兜底路：点一下就地拉内核。</para>
        ///
        /// <para>为什么必须用户点才做：这会起一个浏览器进程并常驻后台。自动拉起等于
        /// 每次进页面都白养一个进程，代价跟一次查询完全不成比例。</para>
        /// </summary>
        private async void OnCreateTempCoreClicked(object sender, RoutedEventArgs e)
        {
            // 已经有一个能用的了就别重复拉 —— 再点是浪费一个浏览器进程。
            if (WebAppCookieService.HasTemporaryCore())
            {
                ShowCookieHint(
                    LocalizationHelper.GetString("WebAppDetail_CookieTempCoreReady"),
                    InfoBarSeverity.Informational);
                await LoadCookiesAsync();
                return;
            }

            SetCookieBusy(true);

            try
            {
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateWithOptionsAsync(
                        browserExecutableFolder: null,
                        userDataFolder: null,
                        options: null);

                await CookieTempWebView.EnsureCoreWebView2Async(environment);

                CoreWebView2? core = CookieTempWebView.CoreWebView2;

                if (core is null)
                {
                    ShowCookieHint(
                        LocalizationHelper.GetString("WebAppDetail_CookieTempCoreFailed"),
                        InfoBarSeverity.Error);
                    return;
                }

                WebAppCookieService.RegisterTemporaryCore(core);
                ShowCookieHint(
                    LocalizationHelper.GetString("WebAppDetail_CookieTempCoreReady"),
                    InfoBarSeverity.Success);

                await LoadCookiesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppCookiePage] 创建临时内核失败: {ex.Message}");
                ShowCookieHint(
                    LocalizationHelper.GetString("WebAppDetail_CookieTempCoreFailed"),
                    InfoBarSeverity.Error);
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private async void OnRefreshCookieClicked(object sender, RoutedEventArgs e)
            => await LoadCookiesAsync();

        private async void OnClearSiteCookieClicked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_originalUrl))
            {
                ShowCookieHint(
                    LocalizationHelper.GetString("WebAppDetail_CookieBadUrl"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool confirmed = await ConfirmAsync(
                LocalizationHelper.GetString("WebAppDetailPage_CookieClearSiteTitle"),
                LocalizationHelper.GetString("WebAppDetailPage_CookieClearSiteContent"));

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
                    ShowCookieHint(
                        string.Format(
                            LocalizationHelper.GetString("WebAppDetailPage_CookieCleared"),
                            result.Affected),
                        InfoBarSeverity.Success);
                }
                else
                {
                    ShowCookieHint(result.Error ?? string.Empty, InfoBarSeverity.Error);
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
                LocalizationHelper.GetString("WebAppDetailPage_CookieClearAllTitle"),
                LocalizationHelper.GetString("WebAppDetailPage_CookieClearAllContent"));

            if (!confirmed)
            {
                return;
            }

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = WebAppCookieService.DeleteAll(_appId);

                ShowCookieHint(
                    result.Ok
                        ? LocalizationHelper.GetString("WebAppDetailPage_CookieClearedAll")
                        : result.Error ?? string.Empty,
                    result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);

                await LoadCookiesAsync();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        private async Task LoadCookiesAsync()
        {
            if (CookieItemsControl is null)
            {
                return;
            }

            // ItemsSource 只挂一次，之后刷新靠集合增删做增量更新。
            CookieItemsControl.ItemsSource ??= _cookieItemSource;

            SetCookieBusy(true);

            try
            {
                WebAppCookieResult result = await WebAppCookieService.ListAsync(
                    _originalUrl ?? string.Empty, _appId);

                SyncCookieItems(result.Ok ? result.Items : Array.Empty<WebAppCookieItem>());

                if (!result.Ok)
                {
                    ShowCookieHint(result.Error ?? string.Empty, InfoBarSeverity.Warning);
                    return;
                }

                ClearCookieHint();
            }
            finally
            {
                SetCookieBusy(false);
            }
        }

        /// <summary>
        /// 全量替换 Cookie 列表。走 ObservableCollection 的增删，UI 做增量更新。
        ///
        /// <para>必须在 UI 线程调用：ObservableCollection 的变更通知会直接驱动 XAML，
        /// 跨线程改会抛。本方法只从 UI 线程发起的 async 链里调用，不做额外调度。</para>
        /// </summary>
        private void SyncCookieItems(IReadOnlyList<WebAppCookieItem> items)
        {
            _cookieItemSource.Clear();

            foreach (WebAppCookieItem item in items)
            {
                _cookieItemSource.Add(item);
            }
        }

        private void SetCookieBusy(bool busy)
        {
            CookieLoadingRing.IsActive = busy;
            CookieLoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ApplyCookieStatusText();
        }

        private void ShowCookieHint(string message, InfoBarSeverity severity)
        {
            _cookieHint = message ?? string.Empty;
            _cookieHintSeverity = severity;
            ApplyCookieStatusText();
        }

        private void ClearCookieHint()
        {
            _cookieHint = string.Empty;
            _cookieHintSeverity = InfoBarSeverity.Informational;
            ApplyCookieStatusText();
        }

        /// <summary>
        /// 把文案写进 InfoBar。加载中优先显示「正在读取」，读完了回落到提示文案；
        /// 提示也为空时整条 InfoBar 收起 —— 否则页面顶部会一直挂一条空条。
        /// </summary>
        private void ApplyCookieStatusText()
        {
            string text = CookieLoadingRing.IsActive
                ? LocalizationHelper.GetString("WebAppDetail_CookieLoading")
                : _cookieHint;

            if (string.IsNullOrEmpty(text))
            {
                StatusInfoBar.IsOpen = false;
                StatusInfoBar.Message = string.Empty;
                return;
            }

            StatusInfoBar.Severity = CookieLoadingRing.IsActive
                ? InfoBarSeverity.Informational
                : _cookieHintSeverity;
            StatusInfoBar.Message = text;
            StatusInfoBar.IsOpen = true;
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
                primaryButtonText: LocalizationHelper.GetString("WebAppDetailPage_CookieConfirmPrimary"),
                closeButtonText: LocalizationHelper.GetString("Common_Cancel"));

            ContentDialogResult? result = await InAppDialogService.ShowAsync(dialog, this);

            return result == ContentDialogResult.Primary;
        }
    }
}
