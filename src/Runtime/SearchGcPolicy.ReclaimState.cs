namespace CombatSolver;

internal static partial class SearchGcPolicy
{
    private static Exception CombineGcFailures(Exception? failure, Exception finalizationFailure)
        => failure == null ? finalizationFailure
            : new AggregateException("GC operation and finalization both failed.", failure, finalizationFailure);

    // Mutated only under Gate. A pending request and a running collection are mutually
    // exclusive, and the completion belongs to that exact operation until Finish.
    // This value owner adds no heap allocation beyond the existing completion source.
    private struct ReclaimState
    {
        private enum Phase { Idle, Pending, Running }
        private Phase _phase;
        private TaskCompletionSource? _completion;
        private Task? _task;
        private Task? _inSearchManualTask;

        public bool CollectsGeneration2 { get; private set; }
        public bool TrimsWorkingSet { get; private set; }
        public bool CollectionStarted { get; private set; }
        public long CoverageEpoch { get; private set; }

        public readonly bool IsPending => _phase == Phase.Pending;
        public readonly bool IsRunning => _phase == Phase.Running;
        public readonly Task Task => _task ?? System.Threading.Tasks.Task.CompletedTask;
        public readonly Task InSearchManualTask
            => _inSearchManualTask ?? System.Threading.Tasks.Task.CompletedTask;
        public readonly TaskCompletionSource Completion => _completion
            ?? throw new InvalidOperationException("GC reclaim has no completion owner.");

        public void Request(TaskCompletionSource? completion = null)
        {
            RequireIdle();
            _completion = completion ?? new(TaskCreationOptions.RunContinuationsAsynchronously);
            _task = _completion.Task;
            _phase = Phase.Pending;
        }

        public void Start(TaskCompletionSource completion, bool collectsGeneration2, bool trimsWorkingSet)
        {
            if (!IsPending || !ReferenceEquals(_completion, completion))
                throw new InvalidOperationException("Only the pending GC owner may start reclamation.");
            _phase = Phase.Running;
            CollectsGeneration2 = collectsGeneration2;
            TrimsWorkingSet = trimsWorkingSet;
        }

        public void StartCheckpoint(TaskCompletionSource completion, Task manualTask)
        {
            RequireIdle();
            _completion = completion;
            _task = completion.Task;
            _inSearchManualTask = manualTask;
            _phase = Phase.Running;
        }

        public void BeginCollection(long coverageEpoch = 0)
        {
            if (!IsRunning || CollectionStarted)
                throw new InvalidOperationException("Collection may start once for the running GC owner.");
            CollectionStarted = true;
            CoverageEpoch = coverageEpoch;
        }

        public void Finish(TaskCompletionSource completion)
        {
            if (!IsRunning || !ReferenceEquals(_completion, completion))
                throw new InvalidOperationException("A stale GC completion cannot release another operation.");
            _phase = Phase.Idle;
            _completion = null;
            _inSearchManualTask = null;
            CollectsGeneration2 = false;
            TrimsWorkingSet = false;
            CollectionStarted = false;
            CoverageEpoch = 0;
            // Existing callers may still join the completed operation's outcome.
        }

        public TaskCompletionSource CancelPending()
        {
            if (!IsPending)
                throw new InvalidOperationException("Only a pending GC request may be cancelled before starting.");
            TaskCompletionSource completion = Completion;
            this = default;
            return completion;
        }

        private readonly void RequireIdle()
        {
            if (_phase != Phase.Idle)
                throw new InvalidOperationException("An unfinished GC operation already owns reclamation.");
        }
    }
}
