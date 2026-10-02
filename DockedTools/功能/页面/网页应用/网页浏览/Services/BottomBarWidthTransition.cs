using Microsoft.UI.Dispatching;
using System;

namespace DockedTools.Features.Pages.WebApp.Browser.Services;

/// <summary>
/// 底部栏按钮宽度的平滑驱动（图标「位移」动画的实现）。
///
/// <para>为什么动的是<b>宽度</b>而不是位置：
/// 六个按钮等宽、整排居中，所以「图标之间的距离」唯一自变量就是 <c>ButtonWidth</c>——
/// 位置是宽度的衍生物，不是独立可控的量。</para>
///
/// <para>为什么不用 <c>RepositionThemeTransition</c>：
/// WinUI 里它是「位置变了就补一段位移」，但<b>元素的尺寸是当帧就落到终值的</b>。
/// 放到本场景里，一次宽度跳变会先让六个按钮瞬间变宽（可能 20px 以上），
/// 再用约 300ms 把它们的偏移缓过去 —— 中间那段时间相邻按钮是<b>互相重叠</b>的，
/// 比直接硬跳还难看。让宽度自己平滑地长过去，位置就自动单调跟随，全程不会有重叠或空隙。</para>
///
/// <para>为什么不是每一帧都滑 —— 见 <see cref="SnapThreshold"/>：
/// 拖窗口时 <c>SizeChanged</c> 会以 ~60Hz 连续到达，每帧宽度只变不到 1px。
/// 这种量级既看不出「跳」，而每帧一次 <c>Mount()</c> 等于每帧一次完整 reconcile，
/// 纯粹是白烧 CPU。所以只有**离散跳变**（最大化/还原、停靠区变化、缓存页换回来）
/// 才值得走动画；连续拖拽时直接落值，让底栏 1:1 跟住窗口边缘，反而最跟手。</para>
/// </summary>
public sealed class BottomBarWidthTransition
{
    /// <summary>默认过渡时长（毫秒）。比颜色过渡短：宽度是几何量，拖长了会显得黏手。</summary>
    public const int DefaultDurationMs = 180;

    /// <summary>
    /// 小于这个差值（px）就认为「看不出跳变」，直接落值不走动画。
    /// 6 个按钮的宽度量程总共只有 28px，3px 大约是量程的一成，
    /// 恰好是「肉眼能察觉为一次跳变」的下限，也恰好大于任何单帧拖拽的增量。
    /// </summary>
    private const double SnapThreshold = 3.0;

    /// <summary>逐帧步进间隔。16ms ≈ 60fps，再密也只是让 CPU 白做功。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// ⚠️ 必须强引用：<c>DispatcherQueueTimer</c> 是 WinRT 投影对象，
    /// 只放局部变量的话会在 GC 时被提前回收，动画跑到一半就静默消失（项目里已有过这个坑）。
    /// </summary>
    private readonly DispatcherQueueTimer _timer;

    private readonly Action<double> _apply;

    private double _current;
    private double _from;
    private double _target;
    private int _durationMs;
    private long _startTicks;
    private bool _animating;

    /// <summary>当前实际下发的宽度。中途被打断时它是插值到的那个中间值，而不是目标值。</summary>
    public double Current => _current;

    /// <param name="queue">UI 线程队列（动画必须在 UI 线程上改宽度）</param>
    /// <param name="apply">把宽度下发到底栏组件并触发重渲染的回调（内部会调 Mount）</param>
    /// <param name="initial">初始宽度，应与组件构造时的宽度一致</param>
    public BottomBarWidthTransition(DispatcherQueue queue, Action<double> apply, double initial)
    {
        _apply = apply;
        _current = initial;

        _timer = queue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// 平滑地变到目标宽度。差值小于 <see cref="SnapThreshold"/> 时退化为立即落值。
    /// 已经在往同一个目标走时不会重启动画（否则每次去抖漏过来的一帧都会把进度打回原点）。
    /// </summary>
    public void AnimateTo(double target, int durationMs = DefaultDurationMs)
    {
        if (Math.Abs(target - _current) < SnapThreshold || durationMs <= 0)
        {
            Set(target);
            return;
        }

        // 已经在往同一个目标走：重启会把进度打回原点，表现为「走一半又退回去重走一遍」。
        if (_animating && Math.Abs(target - _target) < 0.5)
        {
            return;
        }

        // 从**当前插值到的位置**起步，而不是从上一轮的老起点 —— 这样连续改目标也是平滑的。
        _from = _current;
        _target = target;
        _durationMs = durationMs;
        _startTicks = Environment.TickCount64;
        _animating = true;
        _timer.Start();
    }

    /// <summary>立即落值并停掉在跑的动画。首帧挂载、复位这类不该有过渡的场景用这个。</summary>
    public void Set(double value)
    {
        Stop();
        _current = value;
        _apply(value);
    }

    /// <summary>停掉在跑的动画，保持当前宽度不动。</summary>
    public void Stop()
    {
        if (!_animating)
        {
            return;
        }

        _animating = false;
        _timer.Stop();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!_animating)
        {
            sender.Stop();
            return;
        }

        double t = (double)(Environment.TickCount64 - _startTicks) / _durationMs;

        if (t >= 1.0)
        {
            // 收尾直接落终值：插值末帧的浮点残差会让下一次「同值去重」判定差之毫厘，
            // 进而白跑一次动画。
            _animating = false;
            sender.Stop();
            _current = _target;
            _apply(_target);
            return;
        }

        _current = _from + (_target - _from) * EaseOutCubic(t);
        _apply(_current);
    }

    /// <summary>
    /// 三次缓出：起步快、收尾慢。宽度这种几何量用缓入会很别扭 ——
    /// 开头磨蹭的一瞬间看起来像卡顿。
    /// </summary>
    private static double EaseOutCubic(double t)
    {
        double inv = 1.0 - t;
        return 1.0 - inv * inv * inv;
    }
}
