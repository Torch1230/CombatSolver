namespace CombatSolver;

// Tracks only work spawned while the unattended protocol owns the process. It observes task
// completion without changing cancellation, exception propagation, or production control flow.
internal static class UnattendedAsyncActivityTracker
{
    private static readonly Lock Gate = new();
    private static volatile bool _accepting;
    private static int _activeCount;
    private static TaskCompletionSource _idle = CompletedSource();

    public static bool IsRequestActive => _accepting;

    public static bool IsIdle
    {
        get
        {
            lock (Gate)
                return _activeCount == 0;
        }
    }

    public static void BeginRequest()
    {
        lock (Gate)
        {
            if (_accepting || _activeCount != 0)
                throw new InvalidOperationException("上一项无人测试的异步活动尚未结束。");
            _accepting = true;
        }
    }

    public static Task Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        IDisposable? activity = BeginActivity();
        if (activity == null)
            return task;

        _ = task.ContinueWith(
            static (_, state) => ((IDisposable)state!).Dispose(),
            activity,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public static IDisposable? BeginActivity()
    {
        if (!_accepting)
            return null;

        TaskCompletionSource idle;
        lock (Gate)
        {
            if (!_accepting)
                return null;
            if (_activeCount++ == 0)
            {
                _idle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            idle = _idle;
        }
        return new Activity(idle);
    }

    public static Task WaitForIdleAsync()
    {
        lock (Gate)
            return _activeCount == 0 ? Task.CompletedTask : _idle.Task;
    }

    public static bool TryEndRequest()
    {
        lock (Gate)
        {
            if (!_accepting || _activeCount != 0)
                return false;
            _accepting = false;
            return true;
        }
    }

    public static void AbortRequest()
    {
        lock (Gate)
            _accepting = false;
    }

    private static void Complete(TaskCompletionSource idle)
    {
        lock (Gate)
        {
            if (_activeCount <= 0)
                throw new InvalidOperationException("无人测试异步活动计数下溢。");
            if (--_activeCount == 0)
                idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Activity(TaskCompletionSource idle) : IDisposable
    {
        private TaskCompletionSource? _idle = idle;

        public void Dispose()
        {
            TaskCompletionSource? completion = Interlocked.Exchange(ref _idle, null);
            if (completion != null)
                Complete(completion);
        }
    }
}
