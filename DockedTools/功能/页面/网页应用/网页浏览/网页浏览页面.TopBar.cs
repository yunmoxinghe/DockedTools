using DockedTools.Features.Pages.Settings;
using DockedTools.Features.UnifiedCalls.TopAppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - 顶部栏管理模块
    /// 包含顶部栏UI初始化、内容更新、按钮设置等
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private void InitializeTopBar()
        {
            // 创建居中的标签页内容
            _topBarContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8
            };

            // 创建图标容器
            var iconViewbox = new Viewbox
            {
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconGrid = new Grid
            {
                Width = 16,
                Height = 16
            };

            _topBarIcon = new Image
            {
                Stretch = Stretch.UniformToFill,
                Visibility = Visibility.Collapsed
            };

            _topBarIconFallback = new FontIcon
            {
                Glyph = "\uE774",
                Width = 16,
                Height = 16,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconGrid.Children.Add(_topBarIcon);
            iconGrid.Children.Add(_topBarIconFallback);
            iconViewbox.Child = iconGrid;

            // 创建标题文本
            _topBarTitle = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 300
            };

            _topBarContent.Children.Add(iconViewbox);
            _topBarContent.Children.Add(_topBarTitle);

            // 创建取消固定按钮
            // 右侧按钮由独立顶部栏统一创建，避免页面自管导致尺寸/裁切不一致。
        }

        private void SetupTopBar()
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] SetupTopBar 被调用");
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] _topBarContent.Children.Count = {_topBarContent?.Children.Count}");
            
            // 在页面加载后设置顶部栏
            TopAppBarService.SetCenterContent(_topBarContent);
            
            // 设置左侧键盘映射按钮
            SetupLeftMappingButton();
            
            // 设置右侧内容（映射按钮 + 关闭按钮）
            SetupRightContent();
            
            TopAppBarService.SetForeground(_topBarForegroundBrush);
            TopAppBarService.IsVisible = true;
            
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 顶部栏内容已设置，IsVisible = true");
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] TopAppBarService.IsVisible = {TopAppBarService.IsVisible}");
            
            TopAppBarService.SetChromeVisible(false);
            
            // 恢复标题和图标（如果已有数据）
            UpdateTopBarContent();
        }
        
        private void UpdateTopBarContent()
        {
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] UpdateTopBarContent 被调用");
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] _topBarTitle = {(_topBarTitle != null ? "not null" : "null")}");
            System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] _currentShortcut = {(_currentShortcut != null ? "not null" : "null")}");
            
            // 更新标题
            if (_topBarTitle != null && _currentShortcut != null)
            {
                if (WebView?.CoreWebView2 != null && !string.IsNullOrWhiteSpace(WebView.CoreWebView2.DocumentTitle))
                {
                    // 如果有网页标题，使用网页标题
                    _topBarTitle.Text = WebView.CoreWebView2.DocumentTitle;
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 标题设置为网页标题: {_topBarTitle.Text}");
                }
                else
                {
                    // 否则使用快捷方式名称或 URL
                    _topBarTitle.Text = string.IsNullOrWhiteSpace(_currentShortcut.Name) 
                        ? (_pendingNavigationUri?.Host ?? _currentShortcut.Url) 
                        : _currentShortcut.Name;
                    System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 标题设置为快捷方式名称: {_topBarTitle.Text}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 无法更新标题：_topBarTitle 或 _currentShortcut 为 null");
            }
            
            // 更新图标（如果已有数据）
            if (_currentShortcut != null && _currentShortcut.IconBytes != null && _currentShortcut.IconBytes.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 显示快捷方式图标");
                _ = ShowShortcutIconAsync(_currentShortcut.IconBytes);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[WebBrowserPage] 无图标数据");
            }
        }

        private void SetupRightContent()
        {
            var container = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };

            // 1. 添加右侧映射按钮（如果启用）
            if (_currentShortcut?.RightButton.IsEnabled == true)
            {
                SetupRightMappingButton();
                if (_rightMappingButton != null)
                {
                    container.Children.Add(_rightMappingButton);
                }
            }

            // 2. 添加关闭按钮（如果未隐藏）
            if (!ExperimentalSettings.HideWebViewCloseButton)
            {
                _unpinButton = new Button
                {
                    Width = 40,
                    Height = 40,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Content = new FontIcon
                    {
                        Glyph = "\uE733",
                        FontSize = 16,
                        Foreground = _topBarForegroundBrush
                    }
                };

                // 设置按钮背景样式（与返回按钮一致）
                var transparentColor = (Windows.UI.Color)Application.Current.Resources["SubtleFillColorTransparent"];
                _unpinButton.Background = new SolidColorBrush(transparentColor);
                _unpinButton.BackgroundSizing = BackgroundSizing.InnerBorderEdge;

                // 设置悬停和按下状态的背景色
                var resources = new ResourceDictionary();
                var secondaryColor = (Windows.UI.Color)Application.Current.Resources["SubtleFillColorSecondary"];
                var tertiaryColor = (Windows.UI.Color)Application.Current.Resources["SubtleFillColorTertiary"];
                resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(secondaryColor);
                resources["ButtonBackgroundPressed"] = new SolidColorBrush(tertiaryColor);
                _unpinButton.Resources = resources;

                ToolTipService.SetToolTip(_unpinButton, "关闭");
                _unpinButton.Click += CloseButton_Click;
                container.Children.Add(_unpinButton);
            }

            // 设置到右侧面板
            TopAppBarService.SetRightContent(container.Children.Count > 0 ? container : null);
        }
    }
}
