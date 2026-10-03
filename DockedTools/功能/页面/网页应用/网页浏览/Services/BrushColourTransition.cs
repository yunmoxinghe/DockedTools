using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.CompilerServices;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 给 SolidColorBrush 做颜色淡入的统一入口（顶栏/底栏自适应取色专用）。
///
/// <para>为什么不用 XAML 的 <c>BackgroundTransition</c>：
/// WinUI 的隐式 BrushTransition 只在 <b>Grid / StackPanel / ContentPresenter</b> 上受支持，
/// 而本项目的两块 — 顶栏色块 <c>WebPageTopAppBarBackground</c>（Border）与底栏 <c>BottomBarHost</c>
/// （Border）— 恰好都不在支持列表里。写上去不会报错，但也不会有任何过渡效果。
/// 所以只能自己驱动：拿一支常驻画刷，用 Storyboard 动它的 <see cref="SolidColorBrush.Color"/>。</para>
///
/// <para>为什么不直接赋值：自适应取色是 250ms trailing 节流驱动的，
/// 一次滚动里可能连着来十几个不同色值，直接赋值就是一串硬跳；
/// 从一个亮站点切到暗站点尤其刺眼，颜色必须自己平滑地滚过去。</para>
///
/// <para>谁来管 Storyboard：每次 <see cref="AnimateTo"/> 都会先停掉同一支画刷上上一次没跑完的动画。
/// 不停的话两个 Storyboard 同时持有同一依赖属性，后写的目标会被前一个持续覆盖，
/// 表现为「color 换了一半又跳回中间色」。</para>
/// </summary>
public static class BrushColourTransition
{
    /// <summary>默认过渡时长（毫秒）。与 ATBC 的思路无关，是本项目自己的手感参数。</summary>
    public const int DefaultDurationMs = 300;

    private sealed class Running
    {
        public Storyboard? Storyboard;
    }

    /// <summary>
    /// 画刷 → 正在跑的 Storyboard。用 ConditionalWeakTable 而不是 Dictionary：
    /// 画刷私钥跟着页面走，页面被 LRU 淘汰后不必记得回来注销，GC 时自动摘掉。
    /// </summary>
    private static readonly ConditionalWeakTable<SolidColorBrush, Running> _running = new();

    private static readonly ConditionalWeakTable<SolidColorBrush, Running>.CreateValueCallback _factory =
        _ => new Running();

    /// <summary>
    /// 把画刷平滑地变到目标色。已经在这个色上就什么都不做（连 Storyboard 都不建）。
    /// </summary>
    /// <param name="host">承载画刷的视觉元素（Border），动画要挂在它身上才跑得起来。</param>
    /// <param name="brush">被驱动的常驻画刷，必须已经挂在 <paramref name="host"/> 的 Background 上。</param>
    /// <param name="target">目标颜色</param>
    /// <param name="durationMs">时长；&lt;= 0 时退化为立即赋值</param>
    public static void AnimateTo(Border host, SolidColorBrush brush, Windows.UI.Color target, int durationMs = DefaultDurationMs)
    {
        if (host is null || brush is null)
        {
            return;
        }

        Stop(brush);

        if (durationMs <= 0 || brush.Color == target)
        {
            brush.Color = target;
            return;
        }

        Windows.UI.Color from = brush.Color;

        // 首帧特判：画刷还是全透明（刚 new 出来、还没上过色）时，
        // 从透明直接渐变到目标色会先穿过一层半透明的黑：ColorAnimation 是四个通道
        // 分别线性插值的，起始处 RGB 还是 0（黑），配上一层逐渐变厚的 alpha，
        // 肉眼就是「先闪一下黑」。
        // 所以先把 RGB 直接打到目标值、alpha 保持 0，再只滚 alpha —— 视觉上是干净的一次淡入。
        bool firstPaint = from.A == 0 && target.A != 0;
        if (firstPaint)
        {
            from = Windows.UI.Color.FromArgb(0, target.R, target.G, target.B);
            brush.Color = from;
        }

        var animation = new ColorAnimation
        {
            From = from,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase
            {
                EasingMode = firstPaint ? EasingMode.EaseOut : EasingMode.EaseInOut
            },
            // Color 属于「依赖型动画」（dependent animation）：历史上某些目标需要显式开启，
            // 置 true 是幂等的遗留开关，不置在个别平台上会被直接忽略导致毫无过渡。
            EnableDependentAnimation = true
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        // ⭐ 关键：动画必须挂在【视觉元素】上、用【间接属性路径】寻址到画刷的 Color 子属性。
        // 之前的写法 Storyboard.SetTarget(animation, brush) + "Color" 直接把游离的
        // SolidColorBrush 当 target —— 画刷不在视觉树里，动画系统找不到对应的合成视觉节点，
        // 动画被静默丢弃（不报错、不过渡），颜色变成瞬间跳变，这正是「切色没淡入」的根因。
        // 正确姿势是 target 指向 Border，路径写成 (Background).(SolidColorBrush.Color)，
        // 让动画沿着视觉元素的 Background 属性找到那支画刷再去动它的 Color。
        Storyboard.SetTarget(animation, host);
        Storyboard.SetTargetProperty(animation, "(Background).(SolidColorBrush.Color)");

        Running running = _running.GetValue(brush, _factory);
        running.Storyboard = storyboard;

        storyboard.Completed += (_, _) =>
        {
            // 收尾时把终值写死一次：动画插值算出的末帧可能因为量化（逐通道 byte 取整）
            // 与 target 差一两个单位，直接落终值能让「下一次同色去重」判定得准确命中。
            brush.Color = target;
            if (running.Storyboard == storyboard)
            {
                running.Storyboard = null;
            }
        };

        // 探针：颜色过渡是肉眼才能确认的效果，日志里留一条「动画真的起了」的凭据，
        // 排查「切色没淡入」时一眼能分清是没走到这里、还是动画起了但被别处覆盖了。
        System.Diagnostics.Debug.WriteLine(
            $"[BrushColourTransition] 🎨 淡入开始: {from} → {target}, {durationMs}ms, " +
            $"host={host.GetType().Name}");

        storyboard.Begin();
    }

    /// <summary>
    /// 立即赋值并停掉正在跑的动画。
    /// 用于「不该有过渡」的场景：页面复位 / 跟随系统切换 —— 那些是粗粒度状态变更，
    /// 带着 300ms 的尾巴反而会迟到地盖住新页面的第一帧。
    /// </summary>
    public static void SnapTo(SolidColorBrush brush, Windows.UI.Color target)
    {
        if (brush is null)
        {
            return;
        }

        Stop(brush);
        brush.Color = target;
    }

    /// <summary>停掉画刷上正在跑的颜色动画（不改动当前色值）</summary>
    public static void Stop(SolidColorBrush brush)
    {
        if (brush is null)
        {
            return;
        }

        if (!_running.TryGetValue(brush, out Running? running) || running.Storyboard is not { } storyboard)
        {
            return;
        }

        running.Storyboard = null;

        try
        {
            // 先把当前中途值钉下来再停，否则 Stop() 会让属性值直接回落到动画起点，
            // 肉眼就是「闪回上一个色」。
            Windows.UI.Color current = brush.Color;
            storyboard.Stop();
            brush.Color = current;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrushColourTransition] 停止颜色动画失败: {ex.Message}");
        }
    }
}
