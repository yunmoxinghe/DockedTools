using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DockedTools.Features.MainWindowContent.NavigationBar
{
    /// <summary>
    /// NavigationView 滚动扩展方法
    /// 
    /// 【文件职责】
    /// 提供 NavigationView 平滑滚动到指定项的功能，包含健壮的异常处理
    /// 
    /// 【核心功能】
    /// 1. 查找 NavigationView 内部的 ScrollViewer
    /// 2. 计算目标 NavigationViewItem 的位置
    /// 3. 使用平滑动画滚动到目标位置
    /// 4. 提供参数验证和异常保护
    /// 
    /// 【UX 最佳实践】
    /// - 使用平滑动画（符合 Nielsen Norman Group 指导）
    /// - 防止用户在快捷键跳转时迷失方向
    /// - 尊重系统的「减少动态效果」设置（见 <see cref="AnimationsAllowed"/>）
    /// 
    /// 【参考来源】
    /// - WinUI ScrollViewer.ChangeView API 文档
    /// - https://learn.microsoft.com/en-us/windows/apps/develop/composition/scroll-input-animations
    /// - https://www.nngroup.com/articles/animation-duration/
    /// </summary>
    public static class NavigationViewScrollExtensions
    {
        // ==================== 常量定义 ====================
        
        /// <summary>
        /// WebApp 标签的 Tag 前缀
        /// 用于区分不同类型的导航项
        /// </summary>
        private const string WebAppTagPrefix = "webapp:";
        
        /// <summary>
        /// 可视化树递归查找的最大深度（防止无限递归）
        /// 通常 NavigationView 的层级不会超过 20 层
        /// </summary>
        private const int MaxVisualTreeDepth = 20;
        /// <summary>
        /// 递归查找 NavigationView 内部的 ScrollViewer
        /// 
        /// 【异常处理】
        /// - 限制递归深度，防止栈溢出
        /// - 处理 VisualTreeHelper 可能抛出的异常（如控件未加载）
        /// </summary>
        /// <param name="element">起始元素</param>
        /// <param name="currentDepth">当前递归深度（默认 0）</param>
        /// <returns>找到的 ScrollViewer，如果没有找到则返回 null</returns>
        private static ScrollViewer? FindScrollViewer(DependencyObject element, int currentDepth = 0)
        {
            if (element == null)
            {
                return null;
            }

            // 防止无限递归
            if (currentDepth > MaxVisualTreeDepth)
            {
                LogDebug($"⚠️ 达到最大递归深度 {MaxVisualTreeDepth}，停止查找");
                return null;
            }

            if (element is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            try
            {
                int childCount = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < childCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(element, i);
                    var result = FindScrollViewer(child, currentDepth + 1);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("查找 ScrollViewer 时发生异常", ex);
            }

            return null;
        }

        /// <summary>
        /// 平滑滚动到指定的 NavigationViewItem
        /// 
        /// 【使用场景】
        /// - 快捷键跳转到不可见的标签时
        /// - 程序化选择项时确保用户能看到选中项
        /// 
        /// 【动画时长】
        /// - 由 WinUI 自动控制，通常为 200-300ms
        /// - 符合 UX 最佳实践（Nielsen Norman Group）
        /// 
        /// 【返回值说明】
        /// - true: 成功执行滚动（或项已在可见区域）
        /// - false: 滚动失败（参数无效、控件未加载或异常）
        /// 
        /// 【异常处理】
        /// - 参数验证：确保 navView 和 item 不为 null
        /// - 捕获 StartBringIntoView 可能抛出的异常
        /// - 异常情况下返回 false，不影响程序正常运行
        /// 
        /// 【注意】
        /// ChangeView 返回 false 通常表示目标已在可见区域，这是正常行为
        /// </summary>
        /// <param name="navView">NavigationView 实例（必需）</param>
        /// <param name="item">目标 NavigationViewItem（必需）</param>
        /// <param name="animated">是否使用动画（默认 true）</param>
        /// <returns>如果成功滚动返回 true，否则返回 false</returns>
        public static bool ScrollIntoView(
            this NavigationView navView,
            NavigationViewItem item,
            bool animated = true)
        {
            // 参数验证
            if (navView == null)
            {
                LogDebug("⚠️ ScrollIntoView 接收到 null navView");
                return false;
            }

            if (item == null)
            {
                LogDebug("⚠️ ScrollIntoView 接收到 null item");
                return false;
            }

            try
            {
                // 使用 WinUI 3 原生的 StartBringIntoView API
                // 这个方法会自动处理所有布局和滚动逻辑
                var options = new Microsoft.UI.Xaml.BringIntoViewOptions
                {
                    // ⭐ animated 是【调用方】的意愿，系统设置是【用户】的意愿，两者取交集 ——
                    //    早先这里直接写 AnimationDesired = animated，把「减少动态效果」给吃了。
                    AnimationDesired = animated && AnimationsAllowed()
                };
                
                item.StartBringIntoView(options);
                
                LogDebug($"✅ StartBringIntoView 已调用，动画={animated}");
                return true;
            }
            catch (Exception ex)
            {
                LogError("滚动到项失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 通过 Tag 查找 NavigationViewItem
        /// 
        /// 【AOT 优化】
        /// - 避免使用 LINQ，使用显式循环
        /// - 减少枚举器分配，提升性能
        /// - 使用常量替代硬编码字符串
        /// 
        /// 【异常处理】
        /// - 参数验证：确保 navView 和 tag 不为 null
        /// - 安全的类型转换和 null 检查
        /// - 异常情况下返回 null，不抛出异常
        /// </summary>
        /// <param name="navView">NavigationView 实例（必需）</param>
        /// <param name="tag">要查找的 Tag 值（纯 ID，不包含前缀）</param>
        /// <returns>找到的 NavigationViewItem，如果没有找到则返回 null</returns>
        public static NavigationViewItem? FindNavigationViewItem(
            this NavigationView navView,
            string tag)
        {
            // 参数验证
            if (navView == null)
            {
                LogDebug("⚠️ FindNavigationViewItem 接收到 null navView");
                return null;
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                LogDebug("⚠️ FindNavigationViewItem 接收到空的 tag");
                return null;
            }

            try
            {
                // ⭐ AOT 优化：使用常量而非字符串拼接
                string tagWithPrefix = WebAppTagPrefix + tag;
                
                // 在 MenuItems 中查找
                foreach (var item in navView.MenuItems)
                {
                    if (item is NavigationViewItem navItem && 
                        navItem.Tag is string itemTag)
                    {
                        if (itemTag == tag || itemTag == tagWithPrefix)
                        {
                            LogDebug($"✅ 在 MenuItems 中找到项: Tag={itemTag}");
                            return navItem;
                        }
                    }
                }

                // 在 FooterMenuItems 中查找
                foreach (var item in navView.FooterMenuItems)
                {
                    if (item is NavigationViewItem navItem && 
                        navItem.Tag is string itemTag)
                    {
                        if (itemTag == tag || itemTag == tagWithPrefix)
                        {
                            LogDebug($"✅ 在 FooterMenuItems 中找到项: Tag={itemTag}");
                            return navItem;
                        }
                    }
                }
                
                // 未找到
                LogDebug($"❌ 未找到 Tag={tag} 的项");
                return null;
            }
            catch (Exception ex)
            {
                LogError($"查找 Tag={tag} 的项时发生异常", ex);
                return null;
            }
        }

        /// <summary>
        /// 滚动到指定 Tag 的 NavigationViewItem
        /// 
        /// 【便捷方法】
        /// 结合 FindNavigationViewItem 和 ScrollIntoView
        /// 
        /// 【异常处理】
        /// 参数验证和异常捕获由 FindNavigationViewItem 和 ScrollIntoView 处理
        /// </summary>
        /// <param name="navView">NavigationView 实例（必需）</param>
        /// <param name="tag">目标项的 Tag 值</param>
        /// <param name="animated">是否使用动画（默认 true）</param>
        /// <returns>如果成功滚动返回 true，否则返回 false</returns>
        public static bool ScrollToItemByTag(
            this NavigationView navView,
            string tag,
            bool animated = true)
        {
            var item = navView.FindNavigationViewItem(tag);
            if (item == null)
            {
                LogDebug($"未找到 Tag={tag} 的项，无法滚动");
                return false;
            }

            return navView.ScrollIntoView(item, animated);
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 系统是否允许动画效果。
        ///
        /// <para>WinUI 桌面应用拿不到 Web 那套 <c>prefers-reduced-motion</c> 媒体查询，
        /// 最接近的等价物是「设置 → 辅助功能 → 视觉效果 → 动画效果」这个开关，
        /// 它在 <c>UISettings.AnimationsEnabled</c> 上暴露。早先这里把它写成 TODO，
        /// 于是用户明明在系统里关掉了动画，导航栏跳转照样滑得飞快。
        /// 补上之后，本文件「UX 最佳实践」里那条才算真的闭环。</para>
        ///
        /// <para>为什么每次 new 一个 UISettings：这个开关是用户随时能改的，
        /// 静态缓存一份的话改完必须重启应用才生效。构造它只是个薄薄的 WinRT 对象，
        /// 在跳转这种低频操作上完全不心疼。</para>
        /// </summary>
        private static bool AnimationsAllowed()
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                // 拿不到就按「允许」处理：宁可多一点动画，也不要把滚动变成一次硬跳
                return true;
            }
        }

        private static void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigationViewScrollExtensions] {message}");
        }

        /// <summary>
        /// 条件编译的错误日志方法
        /// 
        /// 【异常处理最佳实践】
        /// 记录完整异常信息，便于调试
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogError(string message, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigationViewScrollExtensions] ❌ {message}");
            System.Diagnostics.Debug.WriteLine($"[NavigationViewScrollExtensions]    异常类型: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"[NavigationViewScrollExtensions]    异常消息: {ex.Message}");
        }
    }
}
