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

    /// <summary>底栏 XAML 上原本挂的 ThemeResource 画刷资源键，复位时按它取回默认值</summary>
    private static string _defaultBackgroundResourceKey = "ApplicationPageBackgroundThemeBrush";

    /// <summary>
    /// 当前注册的宿主。BottomBarThemeService 是静态单例（一次只认一个底部栏），
    /// 多个网页页面实例并存时用它确认"我到底是不是宿主"，避免把别人的底部栏改成本页颜色。
    /// </summary>
    public static Border? RegisteredHost => _bottomBarHost;

    /// <summary>
    /// 注册底部栏容器实例（由网页浏览页面在初始化时调用）
    /// </summary>
    /// <param name="bottomBarHost">底栏容器</param>
    /// <param name="defaultBackgroundResourceKey">
    /// XAML 上 Background 用的 ThemeResource 资源键。复位时必须按它把默认画刷取回来，
    /// 不能靠 ClearValue —— ThemeResource 同样是本地值，清掉就直接变透明回不来了。
    /// </param>
    public static void Register(
        Border bottomBarHost,
        string defaultBackgroundResourceKey = "ApplicationPageBackgroundThemeBrush")
    {
        _bottomBarHost = bottomBarHost;
        _defaultBackgroundResourceKey = defaultBackgroundResourceKey;
        System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] 底部栏容器已注册");
    }

    /// <summary>
    /// 注销底部栏容器（页面卸载时清理）
    /// </summary>
    /// <param name="bottomBarHost">
    /// 传入自己的宿主时，只有当前宿主是自己才会注销。
    /// 多页面实例并存时，若非宿主的旧页面 Unload 无差别清空，真正的宿主之后
    /// 写底栏就会被 IsBottomBarHostOwner() 挡在门外，底栏从此不再更新。
    /// </param>
    public static void Unregister(Border? bottomBarHost = null)
    {
        if (bottomBarHost is not null && !ReferenceEquals(_bottomBarHost, bottomBarHost))
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 非宿主页请求注销，已忽略");
            return;
        }

        // ⭐ 顺序要点：**先复位，再断引用**。
        // 服务内所有写入都以 _bottomBarHost 非空为前提，一旦先置空，之后的复位全部落到
        // 「未注册」分支静默 return —— 复位沦为彻头彻尾的空操作，日志却照样报「已复位」。
        // 若这个 Border 被下一张网页复用（页面缓存 / 宿主原地重注册），
        // 上一张网页的 RequestedTheme 与自适应背景色就会原封不动地残留下来。
        bool restored = _bottomBarHost is not null && RestoreToDefault();

        _bottomBarHost = null;
        _customBackgroundBrush = null;

        System.Diagnostics.Debug.WriteLine(
            restored
                ? "[BottomBarThemeService] 底部栏容器已注销（已复位为系统默认）"
                : "[BottomBarThemeService] 底部栏容器已注销");
    }

    #region 一行调用 API

    /// <summary>
    /// 【一行调用】设置底部栏主题和背景颜色
    /// </summary>
    /// <param name="theme">主题模式：Light（亮）、Dark（暗）、Default（跟随系统）</param>
    /// <param name="backgroundColor">背景颜色：传入颜色值，或 null 表示跟随系统</param>
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
    public static void SetBottomBar(ElementTheme theme, Color? backgroundColor = null)
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return;
        }

        // 设置主题模式
        _bottomBarHost.RequestedTheme = theme;

        // 设置背景颜色
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
            // 跟随系统（取回 XAML 原本挂的 ThemeResource 画刷，见 ApplyDefaultBackground 说明）
            _customBackgroundBrush = null;
            ApplyDefaultBackground();
            System.Diagnostics.Debug.WriteLine($"[BottomBarThemeService] ✅ 底部栏已设置: 主题={theme}, 背景=跟随系统");
        }
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
    /// </summary>
    /// <param name="color">背景颜色，null 表示恢复跟随系统</param>
    public static void SetBackgroundColor(Color? color)
    {
        if (_bottomBarHost is null)
        {
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ⚠️ 底部栏容器未注册");
            return;
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
            ApplyDefaultBackground();
            System.Diagnostics.Debug.WriteLine("[BottomBarThemeService] ✅ 底部栏背景已恢复跟随系统");
        }
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

    #region 内部实现

    /// <summary>
    /// 把底栏还原到「跟随系统」默认态：Default 主题 + XAML 上挂的 ThemeResource 背景画刷。
    /// 供 <see cref="Unregister"/> 在断开引用之前调用，避免复位变成空操作。
    /// </summary>
    /// <returns>是否真的写过东西。false 表示本来就已经是默认态，一次都不必写</returns>
    private static bool RestoreToDefault()
    {
        if (_bottomBarHost is null)
        {
            return false;
        }

        bool changed = false;

        // RequestedTheme 是本服务里最贵的一步：改它会让整棵子树的 ThemeResource 重新求值，
        // 按钮样式与 VisualState 全都跟着重刷。多比一次远比无脑重写便宜。
        if (_bottomBarHost.RequestedTheme != ElementTheme.Default)
        {
            _bottomBarHost.RequestedTheme = ElementTheme.Default;
            changed = true;
        }

        // 只有铺过自定义画刷才需要把背景换回 ThemeResource；本来就在默认态就别动它。
        if (_customBackgroundBrush is not null)
        {
            _customBackgroundBrush = null;
            ApplyDefaultBackground();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 把底栏背景还原成 XAML 上挂的 ThemeResource 画刷。
    ///
    /// ⚠️ 这里**不能**用 ClearValue 代替：
    /// <c>Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"</c> 在 XAML 里
    /// 同样是以本地值形式落到 Background 上的，ClearValue 会把它连同 theme-resource 引用
    /// 一起擦掉，结果是「恢复跟随系统」实际变成了「全透明」，且之后切主题再也不会跟着变。
    /// 必须显式按资源键取一次当前主题下的画刷重新赋值，才等价于复位。
    /// 顶栏那套逻辑（ResetAdaptiveBarColour）早就这么处理了，这里补齐同一条规则。
    /// </summary>
    private static void ApplyDefaultBackground()
    {
        if (_bottomBarHost is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(_defaultBackgroundResourceKey)
            && Application.Current.Resources.TryGetValue(_defaultBackgroundResourceKey, out object? resource)
            && resource is Brush defaultBrush)
        {
            _bottomBarHost.Background = defaultBrush;
            return;
        }

        // 资源取不到才退化到 ClearValue，总好过留着上一张网页的自适应色
        _bottomBarHost.ClearValue(Border.BackgroundProperty);
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
        // 走 RestoreToDefault（带比脏）而不是无条件 SetBottomBar，日志也如实反映有没有真改动，
        // 不再自欺欺人地无条件打印「已重置」
        bool changed = RestoreToDefault();
        System.Diagnostics.Debug.WriteLine(
            changed
                ? "[BottomBarThemeService] 🔄 底部栏已重置为完全跟随系统"
                : "[BottomBarThemeService] 底部栏本就处于跟随系统状态，无需重置");
    }

    /// <summary>
    /// 检查底部栏是否已注册
    /// </summary>
    public static bool IsRegistered => _bottomBarHost is not null;

    #endregion
}
