using System;
using System.Collections.Generic;

namespace DockedTools.Features.UnifiedCalls.TopAppBar;

/// <summary>
/// <see cref="TopBarChannel.Apply"/> 的返回值，代表【本次快照的所有权】。
///
/// 为什么 Dispose 不等于 Reset：
/// 页面嵌套、导航动画、旧页延迟卸载都会让「旧页 Dispose」发生在「新页 Apply」之后。
/// 若 Dispose 一律把顶栏复位，就会出现 A→B 切换后 A 的迟到 Dispose 把 B 的顶栏清掉
/// （表现为切换后顶栏闪烁成透明）。
/// 这里让 channel 维护一个按 Apply 排序的作用域栈，Dispose 只【摘掉自己那一项】：
/// 若自己已经不是栈顶所有者，摘除不影响当前顶栏；若仍是所有者，则回落到下一项
/// （没有则回落到 <see cref="TopBarSnapshot.Empty"/>）。Dispose 幂等。
/// </summary>
public sealed class TopBarScope : IDisposable
{
    private readonly TopBarChannel _owner;
    private TopBarSnapshot _snapshot;

    internal TopBarScope(TopBarChannel owner, TopBarSnapshot snapshot)
    {
        _owner = owner;
        _snapshot = snapshot;
    }

    /// <summary>本作用域当前登记的快照（值语义）。</summary>
    internal TopBarSnapshot Snapshot => _snapshot;

    /// <summary>
    /// 本页的事件回调：顶栏上抛的任何 <see cref="TopBarEvent"/> 都会送到这里，
    /// 但【只有本页是当前所有者时才送】。回调里做什么由本页（转接层）决定。
    /// </summary>
    public Action<TopBarEvent>? OnEvent { get; set; }

    /// <summary>页面在存续期间更新自己的顶栏（重发一整份新快照）。</summary>
    public void Update(TopBarSnapshot snapshot) => _owner.Update(this, snapshot);

    /// <summary>当前生效快照（读用）。</summary>
    public TopBarSnapshot Current => _snapshot;

    /// <summary>本页是否仍是当前所有者（决定能不能收到事件）。</summary>
    public bool IsOwner => _owner.IsOwner(this);

    internal void SetSnapshot(TopBarSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>交还所有权（幂等）：不是当前所有者时不做任何事。</summary>
    public void Dispose() => _owner.Remove(this);
}

/// <summary>
/// 顶栏通道：唯一的一条双向管道。
///   下行 —— 转接层 <see cref="Apply"/>/<see cref="TopBarScope.Update"/> 一份
///           <see cref="TopBarSnapshot"/>，channel 归约出 <see cref="Current"/>；
///   上行 —— 顶栏 <see cref="Raise"/> 一个 <see cref="TopBarEvent"/>，
///           channel 广播给外部订阅者，并送达【当前所有者】的回调。
///
/// 通道本身不含任何 WinUI / Reactor 类型，宿主侧只订阅 <see cref="Changed"/>。
/// 它也不含任何"行为"：不做返回、不认识刷新、不知道搜索词该怎么用 ——
/// 那些都由转接层在 <see cref="TopBarScope.OnEvent"/> 里决定。
/// </summary>
public sealed class TopBarChannel
{
    // 作用域栈：按 Apply 的先后登记，【栈顶】即当前所有者
    private readonly List<TopBarScope> _stack = new();

    /// <summary>当前顶栏应呈现的快照（作用域栈为空时即"未配置"的空态）。</summary>
    public TopBarSnapshot Current { get; private set; } = TopBarSnapshot.Empty;

    /// <summary>当前生效快照发生变化（含 Apply / Update / Remove）。宿主据此重渲染。</summary>
    public event EventHandler? Changed;

    /// <summary>页面挂载：登记自己的快照并返回所有权凭证。</summary>
    public TopBarScope Apply(TopBarSnapshot snapshot)
    {
        var scope = new TopBarScope(this, snapshot);

        // 清掉没人认领的残留。它们来自"页面已经没了、凭证还留着"：
        // 页面被 GC 或被缓存淘汰后，它那份 TopBarPageScope 随之消失，而能摘除栈里
        // 这一项的唯一入口（TopBarPageScope.Detach）也一起没了 —— 从此无人 Remove。
        // 正常路径下前一项会由 SetForegroundPage 紧接着摘掉（栈恒为 1 项），
        // 所以这里只在【已经堆积起来】时才动手，不改变正常的交接语义。
        while (_stack.Count > 1)
        {
            _stack.RemoveAt(0);
        }

        _stack.Add(scope);
        RaiseChanged();
        return scope;
    }

    /// <summary>页面存续期间重发快照。</summary>
    internal void Update(TopBarScope scope, TopBarSnapshot snapshot)
    {
        if (!_stack.Contains(scope))
        {
            return;
        }
        if (scope.Snapshot.Equals(snapshot))
        {
            return;
        }
        scope.SetSnapshot(snapshot);
        RaiseChanged();
    }

    /// <summary>页面卸载：摘掉自己那一项。不是所有者的 Dispose 不会影响当前顶栏。</summary>
    internal void Remove(TopBarScope scope)
    {
        if (_stack.Remove(scope))
        {
            RaiseChanged();
        }
    }

    /// <summary>当前所有者（最后登记的、仍存活的页面作用域）。</summary>
    private TopBarScope? Owner => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>作用域是否是当前所有者。</summary>
    internal bool IsOwner(TopBarScope scope) => ReferenceEquals(Owner, scope);

    /// <summary>
    /// 顶栏上抛事件的唯一入口：先广播（经 TopBarMessageHub，树内外订阅者都收到），
    /// 再送达当前所有者页面。顶栏只调用这一个方法，不认识任何具体业务。
    /// </summary>
    public void Raise(TopBarEvent topBarEvent)
    {
        TopBarMessageHub.Instance.Publish(topBarEvent);
        Owner?.OnEvent?.Invoke(topBarEvent);
    }

    internal void RaiseChanged()
    {
        Current = Owner?.Snapshot ?? TopBarSnapshot.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
