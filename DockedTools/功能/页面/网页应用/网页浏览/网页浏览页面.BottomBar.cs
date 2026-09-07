using Microsoft.UI.Xaml;
using System;
using DockedTools.Features.Pages.WebApp.Browser.Services;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// 网页浏览页面 - Reactor底部按钮栏模块
    /// 包含底部按钮栏的初始化、布局更新、状态管理逻辑
    /// </summary>
    public sealed partial class WebBrowserPage
    {
        private void InitializeBottomBarReactor()
        {
            // ✅ 注册底部栏主题服务
            BottomBarThemeService.Register(BottomBarHost);

            // 创建 ReactorHostControl（WinUI ContentControl）
            _reactorHostControl = new Microsoft.UI.Reactor.Hosting.ReactorHostControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,  // 拉伸填充
                VerticalAlignment = VerticalAlignment.Stretch       // 拉伸填充
            };

            // 创建 Reactor 组件实例
            _bottomButtonBarComponent = new Components.BottomButtonBar
            {
                ButtonWidth = 48.0,  // 初始按钮宽度（会根据窗口自适应）
                CanGoBack = false,
                CanGoForward = false,
                OnBackClick = () => BackButton_Click(null!, null!),
                OnForwardClick = () => ForwardButton_Click(null!, null!),
                OnRefreshClick = () => RefreshButton_Click(null!, null!),
                OnCopyUrlClick = () => CopyUrlButton_Click(null!, null!),
                OnOpenExternalClick = () => OpenExternalButton_Click(null!, null!)
            };

            // 挂载组件到 ReactorHostControl
            _reactorHostControl.Mount(_bottomButtonBarComponent);

            // 将 ReactorHostControl 添加到容器
            BottomButtonsContainer.Children.Add(_reactorHostControl);
        }

        private void UpdateBottomBarLayout()
        {
            if (BottomBarHost.ActualWidth <= 0 || _bottomButtonBarComponent == null)
            {
                return;
            }

            const int buttonCount = 5;  // ✅ 恢复原来的按钮数量
            const double minButtonWidth = 40.0;
            const double maxButtonWidth = 68.0;
            const double fixedHorizontalSpacing = 4.0;  // 固定左右和按钮间距

            double availableWidth = BottomBarHost.ActualWidth;
            
            // 计算可用于按钮的宽度（减去固定间距）
            // 总间距 = 左边距 + (按钮数-1)*按钮间距 + 右边距 = fixedHorizontalSpacing * (buttonCount + 1)
            double totalSpacing = fixedHorizontalSpacing * (buttonCount + 1);
            double widthForButtons = availableWidth - totalSpacing;
            double buttonWidth = widthForButtons / buttonCount;
            
            // 限制按钮宽度在最小和最大值之间
            buttonWidth = Math.Max(minButtonWidth, Math.Min(maxButtonWidth, buttonWidth));

            // 更新按钮宽度（间距已经在组件内部固定）
            _bottomButtonBarComponent.ButtonWidth = buttonWidth;
            
            // 触发重新渲染
            _reactorHostControl?.Mount(_bottomButtonBarComponent);

            System.Diagnostics.Debug.WriteLine($"[UpdateBottomBarLayout] buttonWidth={buttonWidth:F2} (间距固定4px)");
        }

        /// <summary>
        /// 更新底部导航按钮的启用/禁用状态
        /// </summary>
        private void UpdateNavigationButtonStates()
        {
            if (_bottomButtonBarComponent == null || _reactorHostControl == null)
            {
                return;
            }

            bool canGoBack = WebView?.CanGoBack ?? false;
            bool canGoForward = WebView?.CanGoForward ?? false;

            // 更新组件 Props
            _bottomButtonBarComponent.CanGoBack = canGoBack;
            _bottomButtonBarComponent.CanGoForward = canGoForward;

            // 触发重新渲染
            _reactorHostControl.Mount(_bottomButtonBarComponent);

            System.Diagnostics.Debug.WriteLine($"[UpdateNavigationButtonStates] CanGoBack={canGoBack}, CanGoForward={canGoForward}");
        }
    }
}
