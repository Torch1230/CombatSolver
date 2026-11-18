using System.Collections.Concurrent;

namespace CombatSolver.Api;

// Only the process owner and session data are replaced. Timers and CTS ownership
// compile from PreCombatForecastWorker.Idle.cs, including its production finally.
internal static partial class PreCombatForecastWorker
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WorkerSession? _session;
    private sealed record WorkerProcess(int Id);
    private sealed record WorkerSession(WorkerProcess Process);
    private static readonly ConcurrentQueue<int> StoppedSessions = new();

    private static void StopCurrentSession()
    {
        if (Interlocked.Exchange(ref _session, null) is { } session)
            StoppedSessions.Enqueue(session.Process.Id);
    }

    internal static async Task CheckIdleExpiryAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource source = new();
        _idleShutdown = source;
        _session = new(new(10));
        await RunIdleShutdownAsync(_session, source, 0, source.Token);
        Require(StoppedSessions.ToArray().SequenceEqual([10]), "Idle expiry did not stop its session exactly once.");
        Require(_idleShutdown is null, "A completed timer retained its source.");
        RequireDisposed(source);
    }

    internal static async Task CheckIdleCancellationAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource source = new();
        _idleShutdown = source;
        _session = new(new(20));
        Task timer = RunIdleShutdownAsync(_session, source, Timeout.Infinite, source.Token);
        CancelIdleShutdown();
        await timer;
        Require(_session?.Process.Id == 20 && StoppedSessions.IsEmpty, "Cancellation stopped the reusable session.");
        Require(_idleShutdown is null, "Cancellation left a pending idle timer.");
        RequireDisposed(source);
        CancelIdleShutdown();
    }

    internal static async Task CheckIdleGateCancellationAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource source = new();
        _idleShutdown = source;
        _session = new(new(30));
        await Gate.WaitAsync();
        try
        {
            Task timer = RunIdleShutdownAsync(_session, source, 0, source.Token);
            Require(!timer.IsCompleted, "Timer did not reach the held request gate.");
            CancelIdleShutdown();
            await timer.WaitAsync(TimeSpan.FromSeconds(2));
            Require(_session?.Process.Id == 30 && StoppedSessions.IsEmpty, "A cancelled gate waiter stopped the session.");
            RequireDisposed(source);
        }
        finally { Gate.Release(); }
    }

    internal static async Task CheckStaleIdleCompletionAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource previous = new(), current = new();
        WorkerSession oldSession = new(new(40));
        CancellationToken previousToken = previous.Token;
        _idleShutdown = previous;
        CancelIdleShutdown();
        _session = new(new(41));
        _idleShutdown = current;
        await RunIdleShutdownAsync(oldSession, previous, 0, previousToken);
        Require(ReferenceEquals(_idleShutdown, current), "Old timer cleanup removed a replacement source.");
        Require(_session?.Process.Id == 41 && StoppedSessions.IsEmpty, "Old timer stopped the replacement session.");
        RequireDisposed(previous);
        Require(!current.IsCancellationRequested, "Old timer cancelled its replacement.");
        CancelIdleShutdown();
        CompleteIdleShutdown(current);
    }

    internal static async Task CheckIdleCancelDisposeOverlapAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource source = new();
        _idleShutdown = source;
        using ManualResetEventSlim entered = new(), release = new();
        using CancellationTokenRegistration registration = source.Token.Register(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Cancellation callback was not released.");
        });
        Task cancel = Task.Factory.StartNew(CancelIdleShutdown, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? cleanup = null;
        bool disposedDuringCancellation = false;
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "Cancellation did not enter its callback.");
            cleanup = Task.Factory.StartNew(() => CompleteIdleShutdown(source),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            try { _ = source.Token; }
            catch (ObjectDisposedException) { disposedDuringCancellation = true; }
        }
        finally
        {
            release.Set();
            await Task.WhenAll(new[] { cancel, cleanup ?? Task.CompletedTask });
        }
        Require(!disposedDuringCancellation, "Idle cleanup disposed the source while Cancel was still executing.");
        RequireDisposed(source);
        Require(_idleShutdown is null, "Concurrent cleanup restored an old timer.");
    }

    internal static async Task CheckIdleCancellationBeforeStartAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource source = new();
        CancellationToken token = source.Token;
        _idleShutdown = source;
        _session = new(new(45));
        CancelIdleShutdown();
        RequireDisposed(source);
        await RunIdleShutdownAsync(_session, source, Timeout.Infinite, token);
        Require(_session?.Process.Id == 45 && StoppedSessions.IsEmpty,
            "A timer cancelled before startup stopped the session.");
        Require(_idleShutdown is null, "A cancelled startup restored its source.");
    }

    internal static Task CheckIndefiniteIdleLifetimeAsync()
    {
        ResetIdleChecks();
        using CancellationTokenSource previous = new();
        _idleShutdown = previous;
        _session = new(new(50));
        SetIdleWorkerLifetime(null);
        try
        {
            ScheduleIdleShutdown(_session);
            Require(_idleShutdown is null && previous.IsCancellationRequested,
                "Indefinite lifetime left the old timer active.");
            Require(GetIdleTimeoutMilliseconds() is null, "Indefinite lifetime was reported as a timeout.");
            Require(_session?.Process.Id == 50 && StoppedSessions.IsEmpty, "Indefinite lifetime stopped the session.");
        }
        finally
        {
            CompleteIdleShutdown(previous);
            SetIdleWorkerLifetime(PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds);
        }
        return Task.CompletedTask;
    }

    private static void ResetIdleChecks()
    {
        Require(_idleShutdown is null && Gate.CurrentCount == 1, "A prior idle check leaked an owner or gate.");
        _session = null;
        StoppedSessions.Clear();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A completed idle task did not dispose its cancellation source.");
    }
}
