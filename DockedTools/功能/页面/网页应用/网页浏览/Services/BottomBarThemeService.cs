using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 底部操作栏主题服务
/// 一行调用实现：主题模式（亮/暗/跟随系统）+ 背景颜色（自定义/跟随系统）
/// </summary>
public static class BottomBarThemeService
{
    private static Border? _bottomBarHost;
    private static SolidColorBrush? _customBackgroundBrush;

    /// <summary>
    /// 注册底部栏容器实例（由网页浏览页面在初始化时调用）
    /// </summary>
    public static void Register(Border bottomBarHost)
    {
        _bottomBarHost = bottomBarHost;
        System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 底部栏容器已注册");
    }

    /// <summary>
    /// 注销底部栏容器（页面卸载时清理）
    /// </summary>
    public static void Unregister()
    {
        _bottomBarHost = null;
        _customBackgroundBrush = null;
        System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 底部栏容器已注销");
    }

    #region 一行调用 API

    /// <summary>
    /// 【一行调用】设置底部栏主题和背景颜色
    ///
    /// ⭐ 脏值拦截：三个值（RequestedTheme / 是否自定义背景 / 背景色）全都没变时整体短路，
    /// 一次都不写。**RequestedTheme 是本 API 里最贵的一步** —— 改动它会让整棵子树的
    /// ThemeResource 重新求值，按钮样式与 VisualState 全都跟着重刷。写背景色反而是便宜的。
    /// 所以这里宁可多比一次，也不要无脑重写。
    /// </summary>
    /// <param name="theme">主题模式：Light（亮）、Dark（暗）、Default（跟随系统）</param>
    /// <param name="backgroundColor">背景颜色：传入颜色值，或 null 表示跟随系统</param>
    /// <returns>是否真的写过值。false 表示与现状完全一致、未做任何改动</returns>
    /// <example>
    /// // 亮色主题 + 跟随系统背景
    /// BottomBarThemeService.SetBottomBar(ElementTheme.Light, null);
    ///
    /// // 暗色主题 + 自定义红色背景
    /// BottomBarThemeService.SetBottomBar(ElementTheme.Dark, Colors.Red);
    ///
    /// // 跟随系统主题 + 跟随系统背景
    /// BottomBarThemeService.SetBottomBar(ElementTheme.Default, null);
    /// </example>
    public static bool SetBottomBar(ElementTheme theme, Color? backgroundColor = null)
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return false;
        }

        // ⭐ 比脏：主题没变且（或不是）背景没变 → 完全不动
        bool themeChanged = _bottomBarHost.RequestedTheme != theme;
        bool backgroundChanged = backgroundColor != _customBackgroundBrush?.Color;

        if (!themeChanged && !backgroundChanged)
        {
            return false;
        }

        if (themeChanged)
        {
            _bottomBarHost.RequestedTheme = theme;
        }

        if (!backgroundChanged)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BottomBarThemeService] 底部栏已设置: 主题={theme}, 背景={(backgroundColor.HasValue ? "自定义" : "跟随系统")}");
            return true;
        }

        if (backgroundColor.HasValue)
        {
            // 自定义颜色
            if (_customBackgroundBrush == null)
            {
                _customBackgroundBrush = new SolidColorBrush(backgroundColor.Value);
                _bottomBarHost.Background = _customBackgroundBrush;
            }
            else
            {
                _customBackgroundBrush.Color = backgroundColor.Value;
            }
            System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] ✅ 底部栏已设置: 主题={theme}, 背景=自定义({backgroundColor.Value})");
        }
        else
        {
            // 跟随系统（恢复 ThemeResource 绑定）
            _customBackgroundBrush = null;
            _bottomBarHost.ClearValue(Border.BackgroundProperty);
            System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] ✅ 底部栏已设置: 主题={theme}, 背景=跟随系统");
        }

        return true;
    }

    #endregion

    #region 单独控制 API（兼容旧代码）

    /// <summary>
    /// 设置底部操作栏的局部主题（仅控制主题，不改变背景颜色）
    /// </summary>
    /// <param name="theme">Light, Dark 或 Default（跟随系统）</param>
    public static void SetTheme(ElementTheme theme)
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册，无法设置主题");
            return;
        }

        _bottomBarHost.RequestedTheme = theme;
        System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] ✅ 底部栏主题已设置为: {theme}");
    }

    /// <summary>
    /// 设置底部栏背景颜色（仅控制背景，不改变主题）
    /// 同 <see cref="SetBottomBar"/> 一样做脏值拦截，色值没变就不写。
    /// </summary>
    /// <param name="color">背景颜色，null 表示恢复跟随系统</param>
    /// <returns>是否真的写过值。false 表示与现状完全一致、未做任何改动</returns>
    public static bool SetBackgroundColor(Color? color)
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return false;
        }

        if (color == _customBackgroundBrush?.Color)
        {
            return false;
        }

        if (color.HasValue)
        {
            if (_customBackgroundBrush == null)
            {
                _customBackgroundBrush = new SolidColorBrush(color.Value);
                _bottomBarHost.Background = _customBackgroundBrush;
            }
            else
            {
                _customBackgroundBrush.Color = color.Value;
            }
            System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] ✅ 底部栏背景已设置为: {color.Value}");
        }
        else
        {
            _customBackgroundBrush = null;
            _bottomBarHost.ClearValue(Border.BackgroundProperty);
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ✅ 底部栏背景已恢复跟随系统");
        }

        return true;
    }

    /// <summary>
    /// 切换底部操作栏的局部主题（Light ↔ Dark）
    /// </summary>
    public static void ToggleTheme()
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册，无法切换主题");
            return;
        }

        var currentTheme = _bottomBarHost.ActualTheme;
        var newTheme = currentTheme == ElementTheme.Dark 
            ? ElementTheme.Light 
            : ElementTheme.Dark;
        
        SetTheme(newTheme);
        System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] 🔄 主题已切换: {currentTheme} → {newTheme}");
    }

    #endregion

    #region 查询 API

    /// <summary>
    /// 获取底部操作栏当前实际生效的主题
    /// </summary>
    public static ElementTheme GetActualTheme()
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return ElementTheme.Default;
        }

        return _bottomBarHost.ActualTheme;
    }

    /// <summary>
    /// 获取底部操作栏请求的主题
    /// </summary>
    public static ElementTheme GetRequestedTheme()
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return ElementTheme.Default;
        }

        return _bottomBarHost.RequestedTheme;
    }

    /// <summary>
    /// 获取当前背景颜色（如果是自定义颜色）
    /// </summary>
    public static Color? GetBackgroundColor()
    {
        return _customBackgroundBrush?.Color;
    }

    /// <summary>
    /// 重置底部操作栏为默认状态（跟随系统主题 + 跟随系统背景）
    /// </summary>
    public static void Reset()
    {
        SetBottomBar(ElementTheme.Default, null);
        System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 🔄 底部栏已重置为完全跟随系统");
    }

    /// <summary>
    /// 检查底部栏是否已注册
    /// </summary>
    public static bool IsRegistered => _bottomBarHost is not null;

    #endregion
}
