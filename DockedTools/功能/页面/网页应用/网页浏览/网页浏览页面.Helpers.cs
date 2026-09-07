using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 辅助方法模块
    /// 包含图标显示、窗口操作、对话框创建、WebView重载等辅助功能
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private async void TryReloadWebView()
        {
            try
            {
                // 防抖检查：如果正在重载或距离上次重载时间太短，则忽略
                var now = DateTime.Now;
                var timeSinceLastReload = (now - _lastReloadTime).TotalMilliseconds;
                
                if (_isReloading)
                {
                    System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 正在重载中，忽略本次请求");
                    return;
                }
                
                if (timeSinceLastReload < ReloadDebounceMs)
                {
                    System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 距离上次重载时间太短 ({timeSinceLastReload:F0}ms < {ReloadDebounceMs}ms)，忽略本次请求");
                    return;
                }
                
                _isReloading = true;
                _lastReloadTime = now;
                
                System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 开始重载流程");
                System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] WebView 是否为 null: {WebView == null}");
                System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] _isWebViewReady: {_isWebViewReady}");
                
                // 检查 WebView 是否存在
                if (WebView == null)
                {
                    System.Diagnostics.Debug.WriteLine("[TryReloadWebView] WebView 为 null");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] CoreWebView2 是否为 null: {WebView.CoreWebView2 == null}");
                
                // 检查 CoreWebView2 是否已初始化
                if (WebView.CoreWebView2 == null)
                {
                    System.Diagnostics.Debug.WriteLine("[TryReloadWebView] CoreWebView2 未初始化，尝试重新初始化");
                    
                    // 尝试重新初始化 WebView
                    _isWebViewReady = false;
                    await EnsureWebViewInitializedAsync();
                    
                    System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 初始化完成，_isWebViewReady: {_isWebViewReady}");
                    System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] CoreWebView2 是否为 null: {WebView?.CoreWebView2 == null}");
                    
                    // 如果初始化成功且有待导航的 URI，则导航
                    if (_isWebViewReady && WebView?.Source != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 重新导航到: {WebView.Source}");
                        var currentSource = WebView.Source;
                        WebView.Source = null;
                        await Task.Delay(50);
                        WebView.Source = currentSource;
                    }
                    else if (_pendingNavigationUri != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 导航到待处理的 URI: {_pendingNavigationUri}");
                        TryNavigatePendingUri();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 初始化后无可用的 URI");
                    }
                    return;
                }

                // 正常重载
                System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 执行正常重载");
                WebView.Reload();
                System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 重载命令已发送");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 重载失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 堆栈: {ex.StackTrace}");
                
                // 如果重载失败，尝试重新导航到当前 URL
                try
                {
                    if (WebView?.Source != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 尝试重新导航到: {WebView.Source}");
                        var currentSource = WebView.Source;
                        
                        // 短暂延迟后重新初始化
                        await Task.Delay(100);
                        
                        // 重新初始化 WebView
                        _isWebViewReady = false;
                        await EnsureWebViewInitializedAsync();
                        
                        // 导航到之前的 URL
                        if (_isWebViewReady && WebView != null)
                        {
                            WebView.Source = currentSource;
                        }
                    }
                }
                catch (Exception innerEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[TryReloadWebView] 重新导航也失败: {innerEx.Message}");
                }
            }
            finally
            {
                // 重载完成，重置标志
                _isReloading = false;
                System.Diagnostics.Debug.WriteLine("[TryReloadWebView] 重载流程结束");
            }
        }

        private async Task ShowShortcutIconAsync(byte[]? iconBytes)
        {
            if (iconBytes is not { Length: > 0 })
            {
                ShowFallbackIcon();
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(iconBytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);

                if (_topBarIcon != null)
                {
                    _topBarIcon.Source = bitmap;
                    _topBarIcon.Visibility = Visibility.Visible;
                }
                if (_topBarIconFallback != null)
                {
                    _topBarIconFallback.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                ShowFallbackIcon();
            }
        }

        private void ShowFallbackIcon()
        {
            if (_topBarIcon != null)
            {
                _topBarIcon.Source = null;
                _topBarIcon.Visibility = Visibility.Collapsed;
            }
            if (_topBarIconFallback != null)
            {
                _topBarIconFallback.Visibility = Visibility.Visible;
            }
        }


        private void HandleDoubleClick()
        {
            try
            {
                var window = GetMainWindowInstance();
                if (window is DockedTools.MainWindow mainWindow)
                {
                    mainWindow.ToggleWindowState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleDoubleClick] 异常: {ex.Message}");
            }
        }

        private Window? GetMainWindowInstance()
        {
            try
            {
                if (Application.Current is App app)
                {
                    var window = app.MainWindow;
                    System.Diagnostics.Debug.WriteLine($"[GetMainWindowInstance] 从 App.MainWindow 获取: {window?.GetType().Name ?? "null"}");
                    if (window != null)
                    {
                        return window;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetMainWindowInstance] 异常: {ex.Message}");
            }
            
            System.Diagnostics.Debug.WriteLine("[GetMainWindowInstance] 所有方法都失败了");
            return null;
        }

        // ⚠️ CreateExternalOpenDialog已移至 网页浏览页面.ContextMenu.cs

        /// <summary>
        /// 给 WebView2 控件设置焦点
        /// 
        /// 【解决方案】
        /// WinUI3 WebView2 的 Focus() 方法只能触发焦点事件，但不会真正将焦点传递给页面内容
        /// 需要组合使用：
        /// 1. Focus(FocusState.Programmatic) - 给 WebView 控件设置焦点
        /// 2. ExecuteScriptAsync("window.focus()") - 用 JavaScript 激活页面焦点
        /// 3. ExecuteScriptAsync("document.body.focus()") - 聚焦 body 元素
        /// 
        /// 参考：https://github.com/MicrosoftEdge/WebView2Feedback/issues/4465
        /// </summary>
        public async void SetWebViewFocus()
        {
            try
            {
                if (WebView == null || !_isWebViewReady || WebView.CoreWebView2 == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] Cannot set focus - WebView: {WebView != null}, Ready: {_isWebViewReady}, CoreWebView2: {WebView?.CoreWebView2 != null}");
                    return;
                }

                // 步骤1: 给 WebView 控件本身设置焦点
                WebView.Focus(FocusState.Programmatic);
                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] ✅ WebView2 控件焦点已设置");

                // 短暂延迟确保控件焦点生效
                await Task.Delay(10);

                // 步骤2: 用 JavaScript 激活页面窗口焦点
                try
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync("window.focus();");
                    System.Diagnostics.Debug.WriteLine("[WebBrowserPage] ✅ window.focus() 已执行");
                }
                catch (Exception jsEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] ⚠️ window.focus() 失败: {jsEx.Message}");
                }

                // 步骤3: 聚焦 body 元素（备选方案）
                try
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync("document.body.focus();");
                    System.Diagnostics.Debug.WriteLine("[WebBrowserPage] ✅ document.body.focus() 已执行");
                }
                catch (Exception bodyEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] ⚠️ document.body.focus() 失败: {bodyEx.Message}");
                }

                System.Diagnostics.Debug.WriteLine("[WebBrowserPage] 🎯 焦点设置流程完成");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] ❌ 设置 WebView 焦点失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 主窗口状态变化完成事件处理器
        /// 当窗口从隐藏状态恢复显示动画完成后，给 WebView 设置焦点
        /// 
        /// 【注意】
        /// StateCompleted 事件在动画播放完成后触发，无需延迟等待
        /// </summary>
        private void OnMainWindowStateCompleted(object? sender, DockedTools.Features.MainWindow.State.StateCompletedEventArgs args)
        {
            // 只在窗口从隐藏状态恢复显示时处理
            if (args.PreviousState == DockedTools.Features.MainWindow.State.WindowState.Hidden &&
                args.CurrentState != DockedTools.Features.MainWindow.State.WindowState.Hidden &&
                args.CurrentState != DockedTools.Features.MainWindow.State.WindowState.NotCreated)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 窗口显示动画完成：{args.PreviousState} -> {args.CurrentState}");
                
                // ⭐ 动画已完成，直接设置焦点，无需延迟
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    SetWebViewFocus();
                });
            }
        }

        /// <summary>
        /// 恢复共享顶部栏背景（页面离开时调用）
        /// </summary>
        private static void RestoreSharedTopAppBarBackground()
        {
            TopAppBarService.ResetBackground();
            TopAppBarService.ResetForeground();
            TopAppBarService.ResetChromeVisibility();
        }
    }
}
