using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Diagnostics.CodeAnalysis;

namespace DockedTools.Features.UnifiedCalls.ContentArea
{
    /// <summary>
    /// ContentArea 全局服务，提供统一的导航入口和状态管理
    /// </summary>
    public static class ContentAreaService
    {
        private static MainWindowContent.ContentArea.ContentArea? _instance;

        /// <summary>
        /// 圆角变化事件
        /// </summary>
        public static event EventHandler<CornerRadius>? CornerRadiusChanged;

        /// <summary>
        /// 注册 ContentArea 实例（由 Linker 调用）
        /// </summary>
        public static void Register(MainWindowContent.ContentArea.ContentArea contentArea)
        {
            _instance = contentArea ?? throw new ArgumentNullException(nameof(contentArea));
            System.Diagnostics.Debug.WriteLine("[ContentAreaService] ContentArea 已注册");
        }

        /// <summary>
        /// 取消注册 ContentArea 实例
        /// </summary>
        public static void Unregister()
        {
            _instance = null;
            CornerRadiusChanged = null;
            System.Diagnostics.Debug.WriteLine("[ContentAreaService] ContentArea 已取消注册");
        }
        
        /// <summary>
        /// 通知圆角已变化（由 ContentArea 内部调用）
        /// </summary>
        internal static void NotifyCornerRadiusChanged(CornerRadius cornerRadius)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentAreaService] 圆角变化通知: {cornerRadius}");
            CornerRadiusChanged?.Invoke(null, cornerRadius);
        }

        /// <summary>
        /// 导航到指定页面
        /// </summary>
        public static void Navigate(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pageType,
            object? parameter = null,
            NavigationTransitionInfo? transitionInfo = null)
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("ContentArea 未注册。请确保在 Linker 中调用 ContentAreaService.Register()");
            }

            _instance.Navigate(pageType, parameter, transitionInfo);
        }

        /// <summary>
        /// 返回上一页
        /// </summary>
        public static void GoBack()
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("ContentArea 未注册");
            }

            _instance.GoBack();
        }

        /// <summary>
        /// 是否可以返回
        /// </summary>
        public static bool CanGoBack
        {
            get
            {
                if (_instance == null)
                {
                    return false;
                }

                return _instance.CanGoBack;
            }
        }

        /// <summary>
        /// 当前显示的页面类型
        /// </summary>
        public static Type? CurrentPageType => _instance?.CurrentPageType;

        /// <summary>
        /// 当前显示的页面参数
        /// </summary>
        public static object? CurrentPageParameter => _instance?.CurrentPageParameter;
        
        /// <summary>
        /// 获取当前内容区的圆角
        /// </summary>
        public static CornerRadius CurrentCornerRadius
        {
            get
            {
                if (_instance == null)
                {
                    return new CornerRadius(0);
                }
                
                // 从 ContentBorder 读取当前圆角
                return _instance.GetCurrentCornerRadius();
            }
        }
    }
}
