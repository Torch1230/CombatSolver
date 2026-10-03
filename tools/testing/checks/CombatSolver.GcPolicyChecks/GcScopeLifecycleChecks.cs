namespace CombatSolver;

internal static class GcScopeLifecycleChecks
{
    public static void Run()
    {
        PolicyCheck.Run("admission snapshot excludes previous request events", () =>
        {
            SearchGcLifecycleCounters counters = new();
            counters.RecordStartAttempt();
            counters.RecordStarted(false);
            counters.RecordForcedCollection();
            counters.RecordEndAttempt();
            counters.RecordEnded();
            using SyntheticScope scope = new(counters, SearchGcLifecycleAttribution.ExclusiveSearchScope);
            counters.RecordStartAttempt();
            counters.RecordStarted(false);
            counters.RecordForcedCollection();
            scope.Dispose();
            PolicyCheck.Require(scope.Lifecycle.NoGcStarts == 1 && scope.Lifecycle.ForcedCollections == 1
                && scope.Lifecycle.NoGcEnds == 0,
                "Events before admitted-scope construction must not leak into that scope's delta.");
        });
        PolicyCheck.Run("a closed scope is not changed by later request or cleanup events", () =>
        {
            SearchGcLifecycleCounters counters = new();
            SyntheticScope first = new(counters, SearchGcLifecycleAttribution.ExclusiveSearchScope);
            counters.RecordStartAttempt();
            counters.RecordStarted(false);
            first.Dispose();
            SearchGcLifecycleSnapshot frozen = first.Lifecycle;
            counters.RecordEndAttempt();
            counters.RecordEnded();
            counters.RecordForcedCollection(); // deferred cleanup after admission release
            using SyntheticScope second = new(counters, SearchGcLifecycleAttribution.ExclusiveSearchScope);
            counters.RecordStartAttempt();
            counters.RecordStarted(false);
            counters.RecordForcedCollection();
            first.Dispose();
            PolicyCheck.Require(first.Lifecycle == frozen && first.Lifecycle.ForcedCollections == 0,
                "Reading or disposing the first scope again must use its frozen exit boundary.");
        });
        PolicyCheck.Run("unfinished scope exposes no claimed final delta", () =>
        {
            SearchGcLifecycleCounters counters = new();
            using SyntheticScope scope = new(counters, SearchGcLifecycleAttribution.ExclusiveSearchScope);
            PolicyCheck.Require(!scope.IsLifecycleCompleted, "A live scope is not a finalized interval.");
            PolicyCheck.Throws<InvalidOperationException>(() => _ = scope.Lifecycle);
            scope.Dispose();
            PolicyCheck.Require(scope.IsLifecycleCompleted, "Disposal must publish the completed interval.");
        });
        PolicyCheck.Run("overlapping ordinary-GC scopes explicitly retain process-window attribution", () =>
        {
            SearchGcLifecycleCounters counters = new();
            using SyntheticScope first = new(counters, SearchGcLifecycleAttribution.SharedProcessWindow);
            using SyntheticScope second = new(counters, SearchGcLifecycleAttribution.SharedProcessWindow);
            counters.RecordForcedCollection();
            first.Dispose();
            second.Dispose();
            PolicyCheck.Require(first.Lifecycle.ForcedCollections == 1 && second.Lifecycle.ForcedCollections == 1
                && first.LifecycleAttribution == SearchGcLifecycleAttribution.SharedProcessWindow
                && second.LifecycleAttribution == SearchGcLifecycleAttribution.SharedProcessWindow,
                "Overlapping global-counter intervals must not claim exclusive event ownership.");
        });
        PolicyCheck.Run("actual ordinary-GC scopes close once even with concurrent disposal", () =>
        {
            using ISearchGcScope first = SearchGcPolicy.EnterSearchScope(
                false, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            using ISearchGcScope second = SearchGcPolicy.EnterSearchScope(
                false, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            Task.WaitAll(Task.Run(first.Dispose), Task.Run(first.Dispose));
            second.Dispose();
            PolicyCheck.Require(first.IsLifecycleCompleted && second.IsLifecycleCompleted
                && first.Lifecycle == default && second.Lifecycle == default
                && first.LifecycleAttribution == SearchGcLifecycleAttribution.SharedProcessWindow,
                "Ordinary-GC scopes must release admission only once and must label shared attribution.");
        });
        PolicyCheck.Run("exclusive runtime scope includes the admitted unsupported-budget fallback", () =>
        {
            // One byte is below the region minimum, so the real policy tests its serial admission
            // path without attempting to reserve a NoGC region or force any collection.
            using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(
                true, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            PolicyCheck.Require(!scope.IsLifecycleCompleted
                && scope.LifecycleAttribution == SearchGcLifecycleAttribution.ExclusiveSearchScope,
                "Requested NoGC retains exclusive admission even when a region cannot be established.");
            scope.Dispose();
            PolicyCheck.Require(scope.Lifecycle == default,
                "An unattempted region start must not be reported as a start or a forced collection.");
        });
        PolicyCheck.Run("waiting next search enters only after the first scope is frozen", () =>
        {
            using ISearchGcScope first = SearchGcPolicy.EnterSearchScope(
                true, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            using ManualResetEventSlim waiting = new();
            Entry.Logger.InfoSink = message =>
            {
                if (message.Contains("reason=active_search", StringComparison.Ordinal))
                    waiting.Set();
            };
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(3));
            Task<ISearchGcScope> next = Task.Run(() => SearchGcPolicy.EnterSearchScope(
                true, 1, new SearchMemoryPressureSignal(), deadline.Token));
            try
            {
                PolicyCheck.Require(waiting.Wait(TimeSpan.FromSeconds(2)), "Expected the next scope to wait for admission.");
                PolicyCheck.Require(!next.IsCompleted && !first.IsLifecycleCompleted,
                    "A waiting request cannot finalize or take over the current scope.");
                first.Dispose();
                using ISearchGcScope second = next.GetAwaiter().GetResult();
                PolicyCheck.Require(first.IsLifecycleCompleted && !second.IsLifecycleCompleted,
                    "The first exit snapshot must be frozen before the second scope is admitted.");
                SearchGcLifecycleSnapshot firstDelta = first.Lifecycle;
                second.Dispose();
                PolicyCheck.Require(first.Lifecycle == firstDelta,
                    "Finalizing another request cannot change an already closed scope.");
            }
            finally
            {
                deadline.Cancel();
                first.Dispose();
                if (next.IsCompletedSuccessfully)
                    next.Result.Dispose();
                else
                {
                    try { next.GetAwaiter().GetResult().Dispose(); }
                    catch (OperationCanceledException) { }
                }
                Entry.Logger.InfoSink = null;
            }
        });
        PolicyCheck.Run("canceling an admission wait returns no scope and preserves the current one", () =>
        {
            using ISearchGcScope current = SearchGcPolicy.EnterSearchScope(
                true, 1, new SearchMemoryPressureSignal(), CancellationToken.None);
            using ManualResetEventSlim waiting = new();
            using CancellationTokenSource canceled = new(TimeSpan.FromSeconds(3));
            Entry.Logger.InfoSink = message =>
            {
                if (message.Contains("reason=active_search", StringComparison.Ordinal))
                    waiting.Set();
            };
            Task<ISearchGcScope> pending = Task.Run(() => SearchGcPolicy.EnterSearchScope(
                true, 1, new SearchMemoryPressureSignal(), canceled.Token));
            try
            {
                PolicyCheck.Require(waiting.Wait(TimeSpan.FromSeconds(2)), "Expected an admission wait before cancellation.");
                canceled.Cancel();
                PolicyCheck.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
                PolicyCheck.Require(!current.IsLifecycleCompleted,
                    "Canceling another wait must not complete the admitted scope.");
                current.Dispose();
                PolicyCheck.Require(current.Lifecycle == default, "The canceled wait performed no GC lifecycle operation.");
            }
            finally
            {
                canceled.Cancel();
                try { pending.GetAwaiter().GetResult().Dispose(); }
                catch (OperationCanceledException) { }
                Entry.Logger.InfoSink = null;
            }
        });
    }

    private sealed class SyntheticScope(
        SearchGcLifecycleCounters counters,
        SearchGcLifecycleAttribution attribution) : SearchGcScope(counters.Capture(), attribution)
    {
        public override void Dispose() => CompleteLifecycle(counters.Capture());
    }
}
