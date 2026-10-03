namespace CombatSolver;

internal static class GcDiagnosticFailureChecks
{
    public static void Run()
    {
        PolicyCheck.Run("checkpoint diagnostics cannot strand post-search reclamation", Checkpoint);
        PolicyCheck.Run("background-start diagnostics settle their operation", () => Background("stage=started"));
        PolicyCheck.Run("background-finish diagnostics settle their operation", () => Background("stage=finished"));
        PolicyCheck.Run("region-exit diagnostics settle the exit gate", RegionExit);
        PolicyCheck.Run("request diagnostics cannot leave an undispatched operation", Request);
        PolicyCheck.Run("deferred diagnostics cannot strand search exit", () => Deferred(false));
        PolicyCheck.Run("promotion diagnostics settle an existing deferred waiter", () => Deferred(true));
        PolicyCheck.Run("operation and finalization failures are both preserved", CombinedFailures);
    }

    private static void Checkpoint()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        SearchMemoryPressureSignal signal = new();
        ISearchGcScope? scope = null;
        Task? deferred = null;
        Exception injected = new InvalidOperationException("Injected checkpoint diagnostic failure.");
        try
        {
            scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, deadline.Token);
            PolicyCheck.Require(signal.IsEnabled, "Exercise an actual region and collection.");
            Entry.Logger.InfoSink = message =>
            {
                if (!message.Contains("HEAP_RECLAIM reason=in_search_memory_checkpoint", StringComparison.Ordinal))
                    return;
                Entry.Logger.InfoSink = null;
                deferred = SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_deferred", forceCollection: true);
                throw injected;
            };
            ExpectFailure(() => signal.ReclaimAndContinue(deadline.Token), injected);
            scope.Dispose();
            PolicyCheck.Require(scope.IsLifecycleCompleted && deferred != null,
                "A failed observation must still release the search and retain its independent cleanup.");
            deferred!.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void Background(string stage)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        Exception injected = new InvalidOperationException($"Injected background diagnostic failure: {stage}.");
        try
        {
            InjectOnce("MEMORY_RECLAIM " + stage, injected);
            Task operation = SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_background", true);
            ExpectFailure(() => operation.WaitAsync(deadline.Token).GetAwaiter().GetResult(), injected);
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_background_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void RegionExit()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        ISearchGcScope? scope = null;
        Exception injected = new InvalidOperationException("Injected region-exit diagnostic failure.");
        try
        {
            SearchMemoryPressureSignal signal = new();
            scope = SearchGcPolicy.EnterSearchScope(true, 1_000_000_000, signal, deadline.Token);
            PolicyCheck.Require(signal.IsEnabled, "Exercise an owned region before requesting its exit.");
            Task exit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled");
            InjectOnce("HEAP_REGION_EXIT", injected);
            scope.Dispose();
            ExpectFailure(() => exit.WaitAsync(deadline.Token).GetAwaiter().GetResult(), injected);
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_exit_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void Request()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        Exception injected = new InvalidOperationException("Injected request diagnostic failure.");
        try
        {
            InjectOnce("MEMORY_RECLAIM stage=requested", injected);
            ExpectFailure(() => SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_request", true), injected);
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_request_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void Deferred(bool failPromotion)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        Exception injected = new InvalidOperationException("Injected deferred diagnostic failure.");
        ISearchGcScope? scope = null;
        try
        {
            scope = SearchGcPolicy.EnterSearchScope(false, 1, new SearchMemoryPressureSignal(), deadline.Token);
            if (failPromotion)
            {
                Task manual = SearchGcPolicy.ForceManualGc();
                Task pending = SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_promote", true);
                InjectOnce("source=deferred", injected);
                ExpectFailure(scope.Dispose, injected);
                ExpectFailure(() => pending.WaitAsync(deadline.Token).GetAwaiter().GetResult(), injected);
                ExpectFailure(() => manual.WaitAsync(deadline.Token).GetAwaiter().GetResult(), injected);
            }
            else
            {
                InjectOnce("MEMORY_RECLAIM stage=deferred", injected);
                ExpectFailure(() => SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_defer", true), injected);
                scope.Dispose();
            }
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            scope?.Dispose();
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_deferred_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void CombinedFailures()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        Exception first = new InvalidOperationException("Injected operation failure.");
        Exception second = new InvalidOperationException("Injected finalization failure.");
        try
        {
            Entry.Logger.InfoSink = message =>
            {
                if (message.Contains("MEMORY_RECLAIM stage=started", StringComparison.Ordinal)) throw first;
                if (message.Contains("MEMORY_RECLAIM stage=finished", StringComparison.Ordinal)) throw second;
            };
            Task pending = SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_combined", true);
            AggregateException? observed = null;
            try { pending.WaitAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (AggregateException error) { observed = error; }
            PolicyCheck.Require(observed?.InnerExceptions.Count == 2
                && ReferenceEquals(observed.InnerExceptions[0], first)
                && ReferenceEquals(observed.InnerExceptions[1], second),
                "Finalization must preserve both original exception instances.");
            Entry.Logger.InfoSink = null;
            AssertNextSearchCanEnter(deadline.Token);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
            SearchGcPolicy.ReclaimIfPendingAsync("diagnostic_combined_cleanup", true)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
    }

    private static void InjectOnce(string marker, Exception injected)
        => Entry.Logger.InfoSink = message =>
        {
            if (!message.Contains(marker, StringComparison.Ordinal)) return;
            Entry.Logger.InfoSink = null;
            throw injected;
        };

    private static void ExpectFailure(Action operation, Exception expected)
    {
        try { operation(); }
        catch (Exception error) when (ReferenceEquals(error, expected)) { return; }
        throw new InvalidOperationException("The original diagnostic failure was not propagated.");
    }

    private static void AssertNextSearchCanEnter(CancellationToken cancellationToken)
    {
        using ISearchGcScope next = SearchGcPolicy.EnterSearchScope(
            false, 1, new SearchMemoryPressureSignal(), cancellationToken);
        next.Dispose();
        PolicyCheck.Require(next.IsLifecycleCompleted, "A diagnostic failure must not permanently block admission.");
    }
}
