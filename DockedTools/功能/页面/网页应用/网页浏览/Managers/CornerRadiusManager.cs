using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace DockedTools.Features.Pages.WebApp.Browser.Managers
{
    /// <summary>
    /// 圆角管理器
    /// 负责同步父容器的圆角到子元素
    /// </summary>
    public class CornerRadiusManager
    {
        private const double MinCornerRadius = 4.0;
        private const double DefaultCornerRadius = 12.0;

        /// <summary>
        /// 从父级容器同步圆角到指定元素
        /// </summary>
        public static void SyncCornerRadiusFromParent(FrameworkElement element, Border targetContainer)
        {
            var cornerRadius = FindParentCornerRadius(element);
            targetContainer.CornerRadius = cornerRadius;
        }

        /// <summary>
        /// 同步动态圆角到顶部栏和底部栏
        /// </summary>
        public static void SyncDynamicCorners(FrameworkElement element, Border topBarHost, Border bottomBarHost)
        {
            // ✅ 直接从 ContentAreaService 获取当前圆角
            var cornerRadius = DockedTools.Features.UnifiedCalls.ContentArea.ContentAreaService.CurrentCornerRadius;
            
            // 如果获取失败，fallback 到查找父容器
            if (cornerRadius == new CornerRadius(0))
            {
                cornerRadius = FindParentCornerRadius(element);
            }
            
            // 确保最小圆角
            double topLeft = Math.Max(MinCornerRadius, cornerRadius.TopLeft);
            double topRight = Math.Max(MinCornerRadius, cornerRadius.TopRight);
            double bottomLeft = Math.Max(MinCornerRadius, cornerRadius.BottomLeft);
            double bottomRight = Math.Max(MinCornerRadius, cornerRadius.BottomRight);
            
            // 顶部栏：只有顶部圆角
            topBarHost.CornerRadius = new CornerRadius(topLeft, topRight, 0, 0);
            
            // 底部栏：只有底部圆角
            bottomBarHost.CornerRadius = new CornerRadius(0, 0, bottomRight, bottomLeft);
            
            System.Diagnostics.Debug.WriteLine(
                $"[CornerRadiusManager] 应用圆角 - 顶部: {topBarHost.CornerRadius}, 底部: {bottomBarHost.CornerRadius}");
        }

        /// <summary>
        /// 从父级容器查找圆角
        /// </summary>
        public static CornerRadius FindParentCornerRadius(FrameworkElement element)
        {
            DependencyObject? parent = element.Parent;
            CornerRadius cornerRadius = new CornerRadius(0);
            
            while (parent != null)
            {
                if (parent is Frame frame && frame.CornerRadius != new CornerRadius(0))
                {
                    cornerRadius = frame.CornerRadius;
                    break;
                }
                if (parent is Border border && border.CornerRadius != new CornerRadius(0))
                {
                    cornerRadius = border.CornerRadius;
                    break;
                }
                if (parent is Grid grid && grid.CornerRadius != new CornerRadius(0))
                {
                    cornerRadius = grid.CornerRadius;
                    break;
                }
                
                parent = VisualTreeHelper.GetParent(parent);
            }
            
            // 如果没有找到圆角，使用默认值
            if (cornerRadius == new CornerRadius(0))
            {
                cornerRadius = new CornerRadius(DefaultCornerRadius);
            }
            
            return cornerRadius;
        }
    }
}
