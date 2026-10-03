using System.Collections.Concurrent;

namespace OfflineSearchHarness;

/// <summary>
/// 单线程消息泵。游戏的回合循环全是 async/await：没有 SynchronizationContext 时
/// 每个 await 之后的续体会落到线程池，而模组的 CombatRootSnapshot.Capture 和
/// SimulatedCombatState 构造都断言必须在主线程上。装上这个 context 以后所有续体
/// 都回到宿主主线程，等价于 Godot 自己的 GodotSynchronizationContext + _Process 泵。
/// </summary>
internal sealed class MainLoopContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public long PumpedCallbacks { get; private set; }

    public bool OnMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (OnMainThread)
        {
            callback(state);
            return;
        }
        using ManualResetEventSlim done = new(false);
        Exception? failure = null;
        _queue.Add((_ =>
        {
            try { callback(state); }
            catch (Exception error) { failure = error; }
            finally { done.Set(); }
        }, null));
        done.Wait();
        if (failure != null)
            throw failure;
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>把队列里的续体一条条在主线程上跑掉，直到任务完成或超时。</summary>
    public void RunUntilCompleted(Task task, TimeSpan timeout, string label)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{label} 在 {timeout.TotalSeconds:0.#}s 内没有完成（已泵 {PumpedCallbacks} 个续体）。");
            PumpOnce(TimeSpan.FromMilliseconds(20));
        }
        Drain();
        task.GetAwaiter().GetResult();
    }

    /// <summary>把任务跑到「完成」或者「挂起且队列空了」——回合循环停在等玩家结束回合时就是后者。</summary>
    public bool RunUntilQuiescent(Task task, TimeSpan timeout, string label, int quietPumps = 40)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int quiet = 0;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{label} 在 {timeout.TotalSeconds:0.#}s 内既没完成也没静默（已泵 {PumpedCallbacks} 个续体）。");
            if (PumpOnce(TimeSpan.FromMilliseconds(10)))
                quiet = 0;
            else if (++quiet >= quietPumps)
                return false;
        }
        task.GetAwaiter().GetResult();
        return true;
    }

    /// <summary>纯粹把当前积压的续体跑完，用于「再推几步」的场合。</summary>
    public void Pump(TimeSpan window)
    {
        DateTime deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
            PumpOnce(TimeSpan.FromMilliseconds(5));
    }

    private bool PumpOnce(TimeSpan wait)
    {
        if (!_queue.TryTake(out var item, (int)wait.TotalMilliseconds))
            return false;
        PumpedCallbacks++;
        item.Callback(item.State);
        return true;
    }

    private void Drain()
    {
        while (_queue.TryTake(out var item, 0))
        {
            PumpedCallbacks++;
            item.Callback(item.State);
        }
    }
}
