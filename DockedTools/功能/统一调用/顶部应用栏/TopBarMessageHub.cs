using System;
using Microsoft.UI.Dispatching;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 顶栏事件的【全局广播总线】：给 UI 树【之外】的订阅者用。
///
/// 与通道上另一个收口的分工：
///   · <see cref="TopBarScope.OnEvent"/> —— 只送达【当前所有者页面】（转接层干活的地方）；
///   · 本 hub —— 进程内全局单例，后台服务、定时器、设备/网络回调、材质实现方
///     这些【不在元素树里】的代码也能收到，且跨线程发布会自动封送到 UI 线程。
///
/// 广播只有这一个出口（早先还有一份挂在通道实例上的 EventRaised，内容与时机完全
/// 重复且零订阅者，已删）。<see cref="Publish"/> 是 internal：只有
/// <see cref="TopBarChannel.Raise"/> 能发，外部无法伪造顶栏事件。
///
/// 典型订阅方是【材质实现方】：顶栏只上抛"要不要底衬、想要什么材质"的意图
/// （<see cref="TopBarEvent.Material"/>），自己不画任何材质，
/// 真正上材质的是订阅到这条消息的那一段代码（宿主窗口 SystemBackdrop、
/// 元素级 SystemBackdropElement、或一层原生 XAML 材质层均可）。
/// </summary>
public sealed class TopBarMessageHub
{
    public static TopBarMessageHub Instance { get; } = new();

    // 静态构造时抓取的队列只能当"兜底参考"：谁第一个碰到 Instance，_uiQueue 就属于谁。
    // 若那发生在后台线程，GetForCurrentThread() 返回 null，之后所有投递退化成同步执行；
    // 多 UI 线程时更糟，队列会被钉死在第一个窗口上。因此 Publish 每次都按调用线程重解析。
    private readonly DispatcherQueue? _uiQueue = DispatcherQueue.GetForCurrentThread();

    /// <summary>事件到达（始终在 UI 线程回调）。</summary>
    public event Action<TopBarEvent>? EventReceived;

    /// <summary>广播一条顶栏事件；后台线程调用时自动切换到 UI 线程再投递给订阅者。</summary>
    internal void Publish(TopBarEvent topBarEvent)
    {
        ArgumentNullException.ThrowIfNull(topBarEvent);

        // 按【调用线程】重新解析队列，而不是直接用构造期捕获的那个：
        // 跨窗口/跨线程时后者是错的宿主，会让材质层那条 Material 消息不发即丢
        // （表现就是"滚动后亚克力出不来"）。
        var queue = DispatcherQueue.GetForCurrentThread() ?? _uiQueue;

        if (queue is null || queue.HasThreadAccess)
        {
            EventReceived?.Invoke(topBarEvent);
        }
        else
        {
            queue.TryEnqueue(() => EventReceived?.Invoke(topBarEvent));
        }
    }
}
