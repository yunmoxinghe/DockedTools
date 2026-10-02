using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// 顶栏的 Reactor 宿主：把一条 <see cref="TopBarChannel"/> 接到纯函数组件
/// <see cref="AppTopBar"/> 上。
/// </summary>
/// <remarks>
/// <b>这一层存在的理由。</b><see cref="AppTopBar"/> 是纯函数 —— 它的唯一输入是
/// <see cref="TopBarSnapshot"/>，不会自己去订阅任何东西。订阅 - 下发这段胶水必须有人做：
/// <list type="bullet">
///   <item>下行：转接层（本项目的 <see cref="TopAppBarService"/>）把各种意图归约成一份快照，
///         经 channel 下发；这里订阅 <see cref="TopBarChannel.Changed"/> 拿到最新快照。</item>
///   <item>渲染：把快照翻译成 <see cref="AppTopBarProps"/>（<see cref="AppTopBarProps.FromSnapshot"/>），
///         交给子组件。props 是值语义的，内容不变时子组件不重渲染 —— 这正是那套
///         "手动 Equals" 想要换来的东西。</item>
///   <item>上行：<c>channel.Raise</c> 作为函数的事件出口传进去，顶栏因此不需要认识任何
///         具体业务。</item>
/// </list>
/// 宿主本身【不含任何业务逻辑】：不知道"返回"是什么意思，也不决定材质怎么画。
/// </remarks>
internal sealed class TopBarHost : Component
{
    /// <summary>
    /// 本条顶栏的通道。由宿主页感的地方（<see cref="TopAppBarService"/>）下发快照，
    /// 因此通道实例必须在宿主之外创建并长期持有。
    /// </summary>
    public TopBarChannel Channel { get; init; } = new TopBarChannel();

    public override Element Render()
    {
        var channel = Channel;

        // 当前下发的快照进本地状态：channel.Changed 只在【真的变了】时投递，
        // 这里接住即可，不用自己做去重。
        var (snapshot, setSnapshot) = UseState(channel.Current);

        UseEffect(() =>
        {
            void OnChanged(object? sender, EventArgs e) => setSnapshot(channel.Current);

            channel.Changed += OnChanged;
            // 兜住"宿主从没收到过 Changed"的情况：转接层可能在本组件挂载前就已经下发过了
            setSnapshot(channel.Current);
            return () => channel.Changed -= OnChanged;
        }, channel);

        var props = AppTopBarProps.FromSnapshot(snapshot, channel.Raise);

        // ⭐ 诊断用：把 AppTopBar 的 Render() 抛出的异常抓出来打到调试输出。
        //    依据 Reactor 官方文档（Core.ErrorFallback）：reconciler 对"组件 Render() 抛异常"
        //    的处理是【就地替换成红色占位面板】，且下一次渲染会重试子组件（error recovery）
        //    —— 表现就是"红一下就没了"，异常本身既不冒泡到 Application.UnhandledException、
        //    也不写日志，所以不装这一层根本看不到是什么错。
        //    ErrorBoundary 的 fallback 拿到的是完整 Exception（含 ToString 全栈）。
        //    定位完保留它：把"红屏闪一下"换成"日志里一条可查的记录 + 占位"，行为更可控。
        return ErrorBoundary(
            Component<AppTopBar, AppTopBarProps>(props),
            ex =>
            {
                System.Diagnostics.Debug.WriteLine($"[TopBar][render-throw] {ex}");
                // 占位保留顶栏高度，避免出错那一帧顶栏塌缩导致内容区跳动
                return Grid(Array.Empty<GridSize>(), Array.Empty<GridSize>()).Height(48);
            });
    }
}
