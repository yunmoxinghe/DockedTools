using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using DockedTools.Features.Localization;
using DockedTools.Features.Pages.Settings;

namespace DockedTools.Features.Pages.WebApp.Browser.Components;

/// <summary>
/// 底部按钮栏 Reactor 组件
/// 使用 HStack + 均匀间距实现完美对称的按钮布局
/// 支持双主题系统（Light/Dark），按钮前景色跟随主题自动切换
/// </summary>
public class BottomButtonBar : Component
{
    // 固定常量
    private const double FixedButtonHeight = 32.0;      // 固定按钮高度
    private const double UniformSpacing = 4.0;          // 统一间距（上下左右和按钮之间都是 4px）
    private const double FixedBarHeight = 48.0;         // 固定操作栏总高度（32px按钮 + 2×8px上下间距）
    
    // Props（按钮宽度是自适应的，通过外部传入）
    public double ButtonWidth { get; set; } = 48.0;
    public bool CanGoBack { get; set; }
    public bool CanGoForward { get; set; }
    public Action? OnBackClick { get; set; }
    public Action? OnForwardClick { get; set; }
    public Action? OnRefreshClick { get; set; }
    public Action? OnCopyUrlClick { get; set; }
    public Action? OnOpenExternalClick { get; set; }

    /// <summary>
    /// 当前「窗口收起时」省电模式（按钮图标随模式变化）
    /// </summary>
    public WebViewIdlePowerMode IdleMode { get; set; } = WebViewIdlePowerMode.Normal;

    /// <summary>
    /// 点击省电模式按钮（由宿主负责循环切换并持久化）
    /// </summary>
    public Action? OnCycleIdleModeClick { get; set; }

    public override Element Render()
    {
        // 直接返回 HStack，用 Padding 控制精确间距，不使用任何居中对齐
        return HStack(UniformSpacing,  // 按钮之间固定间距 4px
            CreateIconButton("\uE72B", CanGoBack, OnBackClick, "后退"),
            CreateIconButton("\uE72A", CanGoForward, OnForwardClick, "前进"),
            CreateIconButton("\uE72C", true, OnRefreshClick, "刷新"),
            CreateIconButton("\uE8C8", true, OnCopyUrlClick, "复制URL"),
            CreateIconButton("\uE774", true, OnOpenExternalClick, "外部打开"),
            CreateIdleModeButton()
        )
        .Padding(UniformSpacing)  // 上下左右统一 4px 内边距
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Top);  // 🔥 改为顶部对齐，不居中
    }

    /// <summary>
    /// 创建带图标的按钮（使用 Fluent 图标字体）
    /// 按钮前景色通过 Button 的 Lightweight Styling 控制，TextBlock 会自动继承
    /// </summary>
    private Element CreateIconButton(string glyph, bool isEnabled, Action? onClick, string fallbackName)
    {
        // 为每个图标按钮提供无障碍名称
        string automationName = glyph switch
        {
            "\uE72B" => LocalizationHelper.GetString("BottomButtonBar_BackButton"),
            "\uE72A" => LocalizationHelper.GetString("BottomButtonBar_ForwardButton"),
            "\uE72C" => LocalizationHelper.GetString("BottomButtonBar_RefreshButton"),
            "\uE8C8" => LocalizationHelper.GetString("BottomButtonBar_CopyUrlButton"),
            "\uE774" => LocalizationHelper.GetString("BottomButtonBar_OpenExternalButton"),
            _ => fallbackName
        };

        // ✅ 不需要手动设置 Resources，因为 BottomBarHost 的 RequestedTheme 会自动应用
        // ThemeDictionaries 中定义的 ButtonForeground 等资源
        return Button(
            content: TextBlock(glyph)
                .FontFamily(new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"))
                .FontSize(16),
            // ⚠️ 不在 TextBlock 上设置 Foreground，让它继承 Button 的前景色
            onClick: () => onClick?.Invoke()
        )
        .SubtleButton()
        .Width(ButtonWidth)
        .Height(FixedButtonHeight)
        .MaxHeight(FixedButtonHeight)
        .MinWidth(32)
        .IsEnabled(isEnabled)
        .AutomationName(automationName)
        // ⚠️ WinUI 限制：IsEnabled=false 的 Button 不再接收指针事件，因此后退/前进
        //    在不可用时（IsEnabled=false）悬停不会弹出提示，这是框架行为而非遗漏。
        .ToolTip(automationName);
    }

    /// <summary>
    /// 创建「窗口收起时模式」按钮
    /// 图标随模式变化：正常运行=\uEC4A / 效率模式=\uEC0A（绿） / 已挂起=\uF2D9（黄）
    /// 点击即在三档之间循环，具体切换与持久化由宿主的 OnCycleIdleModeClick 负责
    /// </summary>
    private Element CreateIdleModeButton()
    {
        string glyph = IdleMode switch
        {
            WebViewIdlePowerMode.Efficient => "\uEC0A",   // 效率模式
            WebViewIdlePowerMode.Suspend => "\uF2D9",     // 已挂起
            _ => "\uEC4A"                                 // 正常运行
        };

        string modeNameKey = IdleMode switch
        {
            WebViewIdlePowerMode.Efficient => "WebViewIdleMode_Efficient",
            WebViewIdlePowerMode.Suspend => "WebViewIdleMode_Suspend",
            _ => "WebViewIdleMode_Normal"
        };

        // ⭐ 只有两个「降级」档位用语义色点出状态，普通档保持按钮默认前景色，
        //    免得把「一切正常」也染得看起来像个警告。
        //    走 Theme token（= SystemFillColorSuccessBrush / SystemFillColorCautionBrush），
        //    深浅色主题自动取对应变体，不写死 hex。
        ThemeRef? iconForeground = IdleMode switch
        {
            WebViewIdlePowerMode.Efficient => Theme.SystemSuccess,   // 绿
            WebViewIdlePowerMode.Suspend => Theme.SystemCaution,     // 黄
            _ => null
        };

        string automationName =
            $"{LocalizationHelper.GetString("BottomButtonBar_IdleModeButton")}：" +
            LocalizationHelper.GetString(modeNameKey);

        // 悬停提示比无障碍名称多一段「可点击循环切换」的说明
        string toolTip =
            $"{automationName}（{LocalizationHelper.GetString("BottomButtonBar_IdleModeCycleHint")}）";

        var icon = TextBlock(glyph)
            .FontFamily(new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"))
            .FontSize(16);

        if (iconForeground.HasValue)
        {
            icon = icon.Foreground(iconForeground.Value);
        }

        return Button(
            content: icon,
            onClick: () => OnCycleIdleModeClick?.Invoke()
        )
        .SubtleButton()
        .Width(ButtonWidth)
        .Height(FixedButtonHeight)
        .MaxHeight(FixedButtonHeight)
        .MinWidth(32)
        .AutomationName(automationName)
        .ToolTip(toolTip);
    }
}
