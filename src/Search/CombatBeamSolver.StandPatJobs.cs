using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)
    {
        ParallelExpansionExecutor? executor = _run.ActiveParallelExpansion;
        if (executor == null)
            return;
        List<SearchNode> pending = [];
        HashSet<StateFingerprint> seen = [];
        foreach (SearchNode node in nodes)
        {
            // Preserve the first original cache representative. The caller will consume these
            // exact nodes in the same order and perform the same selection after preparation.
            if (!_run.StandPatCache.ContainsKey(node.StateKey) && seen.Add(node.StateKey))
                pending.Add(node);
        }
        if (pending.Count < 2)
            return;
        // Metadata before the probe group has completed with no lane in flight.
        _run.CheckpointPruneMetadata?.Invoke("stand_pat");
        for (int start = 0; start < pending.Count;)
        {
            long probeReserve = StandPatProbeAllocationReserve();
            _run.EnsurePruneMemory?.Invoke(probeReserve);
            int count = ResolveStandPatBatchSize(
                pending.Count - start,
                policy.MemoryPressureSignal.RemainingBytes,
                probeReserve);
            // The full-list fast path retains the original wave when it fits. Splitting only
            // changes scheduling: first representatives and their original order stay frozen.
            IReadOnlyList<SearchNode> batch = start == 0 && count == pending.Count
                ? pending : pending.GetRange(start, count);
            long allocatedBefore = OwnedSearchAllocatedBytes();
            StandPatEvaluation[] evaluations = executor.EvaluateStandPatProbes(
                batch, out long maximumProbeAllocatedBytes);
            _run.StandPatBatchAllocatedBytes += Math.Max(
                0, OwnedSearchAllocatedBytes() - allocatedBefore);
            _run.StandPatProbeAllocatedHighWater = Math.Max(
                _run.StandPatProbeAllocatedHighWater, maximumProbeAllocatedBytes);
            for (int index = 0; index < batch.Count; index++)
            {
                _run.StandPatCache.Add(batch[index].StateKey, evaluations[index]);
                _run.StandPatProbes++;
            }
            // EvaluateStandPatProbes waits for every lane's completion signal before returning.
            // The next iteration may therefore reclaim without racing a worker or losing roots.
            start += count;
        }
        // All probe workers have drained. Reserve the metadata tail only when it resumes;
        // carrying its full reservation through every batch strands most of a small region.
        _run.CheckpointPruneMetadata?.Invoke("rank_after_stand_pat");
    }

    // Called only on this solver's coordinator between drained worker waves. The worker
    // counter includes every merged lane; foreign/render allocation cannot inflate a tiny
    // candidate pool's bytes-per-input estimate by an entire process allocation quantum.
    private long OwnedSearchAllocatedBytes()
        => GC.GetAllocatedBytesForCurrentThread() + _run.OffThreadAllocatedBytes;

    private long StandPatProbeAllocationReserve()
        => BufferedAllocationReserve(_run.StandPatProbeAllocatedHighWater == 0
            ? 64L * 1024 * 1024 : _run.StandPatProbeAllocatedHighWater);

    internal static int ResolveStandPatBatchSize(
        int pendingCount, long remainingBytes, long probeReserveBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pendingCount);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(probeReserveBytes);
        if (remainingBytes == long.MaxValue)
            return pendingCount;
        return (int)Math.Clamp(remainingBytes / probeReserveBytes, 1, pendingCount);
    }

    private sealed partial class ParallelExpansionExecutor
    {
        private int _activeStandPatWorkers;

        public StandPatEvaluation[] EvaluateStandPatProbes(
            IReadOnlyList<SearchNode> nodes, out long maximumProbeAllocatedBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            maximumProbeAllocatedBytes = 0;
            ExpansionLane[] lanes = EnsureBackgroundLanes();
            using StandPatJobWave wave = new(DegreeOfParallelism);
            StandPatEvaluation[] evaluations = new StandPatEvaluation[nodes.Count];
            long startedAt = Stopwatch.GetTimestamp();
            int next = 0;
            int active = 0;
            ExceptionDispatchInfo? firstError = null;

            void Dispatch(int lane)
            {
                _coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                StandPatJob job = new(nodes[next], next, lane, wave);
                wave.Completed.AddCount();
                try { lanes[lane].Dispatch(job); }
                catch
                {
                    wave.Completed.Signal();
                    throw;
                }
                next++;
                active++;
            }

            try
            {
                for (int lane = 0; lane < DegreeOfParallelism && next < nodes.Count; lane++)
                    Dispatch(lane);
                while (active > 0)
                {
                    StandPatJobOutcome outcome = wave.Take();
                    active--;
                    _coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
                    maximumProbeAllocatedBytes = Math.Max(maximumProbeAllocatedBytes, outcome.AllocatedBytes);
                    _workProfile.Record(ParallelExpansionWorkProfile.Kind.StandPat,
                        outcome.ElapsedTicks, outcome.Concurrency);
                    firstError ??= outcome.Error;
                    if (firstError != null)
                        continue;
                    evaluations[outcome.Job.Index] = outcome.Evaluation;
                    if (next < nodes.Count)
                        Dispatch(outcome.Job.Lane);
                }
                firstError?.Throw();
                return evaluations;
            }
            finally
            {
                // Prune already owns these live candidate roots. No new expansion is admitted,
                // and no checkpoint may release them until all probe lanes have completed.
                wave.Completed.Signal();
                wave.Completed.Wait();
                while (wave.TryTake(out StandPatJobOutcome? pending))
                    _coordinator.MergeExpansionWorker(pending!.Worker, pending.AllocatedBytes);
                _workProfile.Record(ParallelExpansionWorkProfile.Kind.StandPatWave,
                    Stopwatch.GetTimestamp() - startedAt);
            }
        }

        private sealed class StandPatJobWave(int capacity) : IDisposable
        {
            private readonly object _gate = new();
            private readonly Queue<StandPatJobOutcome> _outcomes = new(capacity);
            public CountdownEvent Completed { get; } = new(1);

            public void Publish(StandPatJobOutcome outcome)
            {
                lock (_gate)
                {
                    _outcomes.Enqueue(outcome);
                    Monitor.Pulse(_gate);
                }
            }

            public StandPatJobOutcome Take()
            {
                lock (_gate)
                {
                    while (_outcomes.Count == 0)
                        Monitor.Wait(_gate);
                    return _outcomes.Dequeue();
                }
            }

            public bool TryTake(out StandPatJobOutcome? outcome)
            {
                lock (_gate)
                    return _outcomes.TryDequeue(out outcome);
            }

            public void Dispose() => Completed.Dispose();
        }

        private sealed class StandPatJobOutcome(StandPatJob job, CombatBeamSolver worker)
        {
            public StandPatJob Job { get; } = job;
            public CombatBeamSolver Worker { get; } = worker;
            public StandPatEvaluation Evaluation;
            public ExceptionDispatchInfo? Error;
            public long AllocatedBytes;
            public long ElapsedTicks;
            public int Concurrency;
        }

        private sealed record StandPatJob(SearchNode Node, int Index, int Lane, StandPatJobWave Wave)
            : IExpansionLaneWorkItem
        {
            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker)
            {
                StandPatJobOutcome outcome = new(this, worker);
                long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                long startedAt = Stopwatch.GetTimestamp();
                outcome.Concurrency = Interlocked.Increment(ref owner._activeStandPatWorkers);
                try
                {
                    worker.SearchCancellationToken.ThrowIfCancellationRequested();
                    // First representatives have distinct immutable state keys and snapshots.
                    // No other expansion/probe job can fork this same candidate simultaneously.
                    outcome.Evaluation = worker.ComputeStandPat(Node);
                }
                catch (System.Exception error)
                {
                    outcome.Error = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    Interlocked.Decrement(ref owner._activeStandPatWorkers);
                    outcome.AllocatedBytes = Math.Max(
                        0, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                    outcome.ElapsedTicks = Stopwatch.GetTimestamp() - startedAt;
                    Wave.Publish(outcome);
                }
            }

            public void Signal() => Wave.Completed.Signal();
        }
    }
}
