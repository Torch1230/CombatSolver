namespace CombatSolver.Api;

internal static partial class PreCombatForecastWorker
{
    private const int KeepAliveIndefinitely = -1;
    private static CancellationTokenSource? _idleShutdown;
    private static int _idleWorkerLifetimeMilliseconds = PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds;
    private static object? _inProcessSession;

    private static void ScheduleIdleShutdown(WorkerSession session)
    {
        CancelIdleShutdown();
        int idleTimeoutMilliseconds = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        if (idleTimeoutMilliseconds == KeepAliveIndefinitely)
            return;
        CancellationTokenSource cancellation = new();
        // A new request can take and dispose the source before this task starts.
        CancellationToken token = cancellation.Token;
        _idleShutdown = cancellation;
        _ = Task.Run(() => RunIdleShutdownAsync(session, cancellation, idleTimeoutMilliseconds, token));
    }

    private static void ScheduleInProcessIdleShutdown(object inProcessSession)
    {
        CancelIdleShutdown();
        int idleTimeoutMilliseconds = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        if (idleTimeoutMilliseconds == KeepAliveIndefinitely)
            return;
        CancellationTokenSource cancellation = new();
        CancellationToken token = cancellation.Token;
        _idleShutdown = cancellation;
        _ = Task.Run(() => RunInProcessIdleShutdownAsync(inProcessSession, cancellation, idleTimeoutMilliseconds, token));
    }

    private static async Task RunIdleShutdownAsync(
        WorkerSession session, CancellationTokenSource cancellation, int idleTimeoutMilliseconds,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(idleTimeoutMilliseconds, token).ConfigureAwait(false);
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_session, session))
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP pid={session.Process.Id} " +
                        $"idle_ms={idleTimeoutMilliseconds}");
                    StopCurrentSession();
                }
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A new request or explicit panel close owns the next session transition.
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP_FAILED exception={ex}");
        }
        finally
        {
            CompleteIdleShutdown(cancellation);
        }
    }

    private static async Task RunInProcessIdleShutdownAsync(
        object inProcessSession, CancellationTokenSource cancellation, int idleTimeoutMilliseconds,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(idleTimeoutMilliseconds, token).ConfigureAwait(false);
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_inProcessSession, inProcessSession))
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/PreCombatApi] INPROCESS_IDLE_STOP idle_ms={idleTimeoutMilliseconds}");
                    StopCurrentInProcessSession();
                }
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A new request or explicit panel close owns the next session transition.
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CombatSolver/PreCombatApi] INPROCESS_IDLE_STOP_FAILED exception={ex}");
        }
        finally
        {
            CompleteIdleShutdown(cancellation);
        }
    }

    private static void StopCurrentInProcessSession() =>
        Interlocked.Exchange(ref _inProcessSession, null);

    private static int? GetIdleTimeoutMilliseconds()
    {
        int value = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        return value == KeepAliveIndefinitely ? null : value;
    }

    private static void SetIdleWorkerLifetime(int? idleTimeoutMilliseconds) =>
        Interlocked.Exchange(
            ref _idleWorkerLifetimeMilliseconds,
            idleTimeoutMilliseconds ?? KeepAliveIndefinitely);

    private static void CompleteIdleShutdown(CancellationTokenSource cancellation)
    {
        // Removing the field transfers disposal ownership. A cancellation caller
        // that already took this source must keep it alive until Cancel returns.
        if (ReferenceEquals(Interlocked.CompareExchange(ref _idleShutdown, null, cancellation), cancellation))
            cancellation.Dispose();
    }

    private static void CancelIdleShutdown()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _idleShutdown, null);
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }
}
