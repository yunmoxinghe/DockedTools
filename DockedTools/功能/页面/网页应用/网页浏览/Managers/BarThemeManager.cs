using DockedTools.Features.Pages.WebApp.Browser.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.UI;

namespace DockedTools.Features.Pages.WebApp.Browser.Managers
{
    /// <summary>
    /// 顶部栏和底部栏的主题管理器
    /// 负责根据网页颜色动态调整栏的背景色和前景色
    /// 支持底部栏独立主题控制（不受网页主题色影响）
    /// </summary>
    public class BarThemeManager
    {
        private readonly SolidColorBrush _topBarBackgroundBrush = new(Colors.Transparent);
        private readonly SolidColorBrush _bottomBarBackgroundBrush = new(Colors.Transparent);
        private readonly SolidColorBrush _topBarForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarForegroundBrush = new();
        private readonly SolidColorBrush _topBarSecondaryForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarDisabledForegroundBrush = new();
        private readonly SolidColorBrush _bottomBarHoverForegroundBrush = new();
        
        private FrameworkElement? _themeListenerElement; // 用于监听主题变化
        private bool _bottomBarIndependentTheme = false; // ✅ 底部栏是否使用独立主题（不受网页主题色影响）

        public SolidColorBrush TopBarBackgroundBrush => _topBarBackgroundBrush;
        public SolidColorBrush BottomBarBackgroundBrush => _bottomBarBackgroundBrush;
        public SolidColorBrush TopBarForegroundBrush => _topBarForegroundBrush;
        public SolidColorBrush BottomBarForegroundBrush => _bottomBarForegroundBrush;
        public SolidColorBrush TopBarSecondaryForegroundBrush => _topBarSecondaryForegroundBrush;
        public SolidColorBrush BottomBarDisabledForegroundBrush => _bottomBarDisabledForegroundBrush;
        public SolidColorBrush BottomBarHoverForegroundBrush => _bottomBarHoverForegroundBrush;

        /// <summary>
        /// 从当前主题资源更新前景色
        /// </summary>
        private void UpdateForegroundColorsFromTheme()
        {
            // 从主题资源获取默认文本颜色
            if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out object? resource) 
                && resource is SolidColorBrush themeBrush)
            {
                _topBarForegroundBrush.Color = themeBrush.Color;
                _bottomBarForegroundBrush.Color = themeBrush.Color;
            }
            else
            {
                // 回退：根据当前主题选择黑色或白色
                var theme = Application.Current.RequestedTheme;
                var defaultColor = theme == ApplicationTheme.Dark ? Colors.White : Colors.Black;
                _topBarForegroundBrush.Color = defaultColor;
                _bottomBarForegroundBrush.Color = defaultColor;
            }

            // 初始化次要前景色
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? secondaryResource) 
                && secondaryResource is SolidColorBrush secondaryBrush)
            {
                _topBarSecondaryForegroundBrush.Color = secondaryBrush.Color;
            }
            else
            {
                _topBarSecondaryForegroundBrush.Color = ColorService.CreateSecondaryColor(_topBarForegroundBrush.Color);
            }

            // 初始化禁用状态颜色
            if (Application.Current.Resources.TryGetValue("TextFillColorDisabledBrush", out object? disabledResource) 
                && disabledResource is SolidColorBrush disabledBrush)
            {
                _bottomBarDisabledForegroundBrush.Color = disabledBrush.Color;
            }
            else
            {
                _bottomBarDisabledForegroundBrush.Color = ColorService.CreateSecondaryColor(_bottomBarForegroundBrush.Color, 0.6);
            }
            
            // 初始化悬停状态颜色
            _bottomBarHoverForegroundBrush.Color = ColorService.AdjustColorBrightness(_bottomBarForegroundBrush.Color, 0.15);
        }

        /// <summary>
        /// 系统主题切换时的回调
        /// </summary>
        private void OnThemeChanged(FrameworkElement sender, object args)
        {
            System.Diagnostics.Debug.WriteLine("[BarThemeManager] 检测到系统主题切换，重新加载主题资源");
            
            // 重新从主题资源获取颜色
            UpdateForegroundColorsFromTheme();
            
            // 应用系统强调色
            ApplySystemAccentColor();
        }

        /// <summary>
        /// 释放资源（取消订阅主题变化事件）
        /// </summary>
        public void Dispose()
        {
            if (_themeListenerElement != null)
            {
                _themeListenerElement.ActualThemeChanged -= OnThemeChanged;
                _themeListenerElement = null;
            }
        }

        /// <summary>
        /// 应用栏的着色（根据网页主题色动态调整）
        /// </summary>
        public void ApplyBarTint(bool isTop, Color sampledColor)
        {
            // ✅ 如果底部栏使用独立主题，则跳过底部栏的着色
            if (!isTop && _bottomBarIndependentTheme)
            {
                System.Diagnostics.Debug.WriteLine("[BarThemeManager] 底部栏使用独立主题，跳过网页主题色着色");
                return;
            }

            var tinted = Color.FromArgb(byte.MaxValue, sampledColor.R, sampledColor.G, sampledColor.B);
            SolidColorBrush background = isTop ? _topBarBackgroundBrush : _bottomBarBackgroundBrush;
            SolidColorBrush foreground = isTop ? _topBarForegroundBrush : _bottomBarForegroundBrush;

            // 使用动画平滑过渡
            ColorService.AnimateColorChange(background, tinted);
            
            var contrastColor = ColorService.GetContrastingForeground(sampledColor);
            ColorService.AnimateColorChange(foreground, contrastColor);

            // 更新次要前景色
            if (isTop)
            {
                var secondaryColor = ColorService.CreateSecondaryColor(contrastColor);
                ColorService.AnimateColorChange(_topBarSecondaryForegroundBrush, secondaryColor);
            }
            else
            {
                // 更新底部栏的悬停和禁用状态颜色
                double luminance = ColorService.CalculateLuminance(sampledColor);
                double adjustFactor = luminance < 0.179 ? 0.2 : -0.2;
                var hoverColor = ColorService.AdjustColorBrightness(contrastColor, adjustFactor);
                
                ColorService.AnimateColorChange(_bottomBarHoverForegroundBrush, hoverColor);
                
                var disabledColor = ColorService.CreateSecondaryColor(contrastColor, 0.6);
                ColorService.AnimateColorChange(_bottomBarDisabledForegroundBrush, disabledColor);
            }
        }

        /// <summary>
        /// 启用底部栏独立主题模式（不受网页主题色影响，使用 BottomBarThemeResources.xaml 的主题）
        /// </summary>
        public void EnableBottomBarIndependentTheme(bool enable)
        {
            _bottomBarIndependentTheme = enable;
            System.Diagnostics.Debug.WriteLine($"[BarThemeManager] 底部栏独立主题模式: {(enable ? "已启用" : "已禁用")}");
        }

        /// <summary>
        /// 获取底部栏是否使用独立主题
        /// </summary>
        public bool IsBottomBarIndependentTheme => _bottomBarIndependentTheme;

        /// <summary>
        /// 应用系统强调色作为回退方案
        /// </summary>
        public void ApplySystemAccentColor()
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? accentResource) 
                    && accentResource is Color accentColor)
                {
                    ApplyBarTint(isTop: true, accentColor);
                    ApplyBarTint(isTop: false, accentColor);
                    System.Diagnostics.Debug.WriteLine("[BarThemeManager] 应用系统强调色");
                }
            }
            catch
            {
                // 保持透明
            }
        }
    }
}
