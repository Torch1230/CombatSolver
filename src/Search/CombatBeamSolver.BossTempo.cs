using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed class BossTempoRun
    {
        internal int RemainingDiscrepancies;
        internal string Stop = "not_started";
        internal int Deferred;
        internal int PeakPending;
        internal readonly Dictionary<(StateFingerprint State, int Remaining), TranspositionFrontier> Expanded = [];
    }

    private BossTempoRun Tempo => _run.BossTempo ??= new();

    private bool TryMarkTempoExpanded(SearchNode node)
    {
        var key = (node.StateKey, Tempo.RemainingDiscrepancies);
        TranspositionLabel label = new(node.PotionCount, node.PotionStrategicCost, node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost, node.ActionCount, node.Score);
        bool accepted;
        if (Tempo.Expanded.TryGetValue(key, out TranspositionFrontier? existing))
            accepted = existing.TryAccept(label);
        else if (AtTranspositionEntryLimit())
        {
            _run.TranspositionLimitBypasses++;
            accepted = true;
        }
        else
        {
            Tempo.Expanded.Add(key, new(label));
            ObserveTranspositionEntries();
            accepted = true;
        }
        if (!accepted) _run.TranspositionBranchesPruned++;
        ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition,
            accepted ? "tempo_accepted" : "tempo_dominated");
        return accepted;
    }

    private bool RunBossTempoDepthFirst(List<SearchNode> initial, List<SearchNode> completed,
        Stopwatch clock, ref SearchNode fallback, Func<SearchNode, bool> hpTarget,
        Func<SearchNode, int, int, bool> beforeParent,
        Action<SearchNode, int, int> afterParent, Action<SearchNode> observeBoundary,
        out bool targetReached, out int turnLayers)
    {
        BossTempoSearchOptions options = policy.BossTempoSearch!;
        ArgumentOutOfRangeException.ThrowIfNegative(options.DiscrepancyAllowance);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ScoutTurns);
        if (options.ScoutTurns > 0 && options.PrefixObserver == null)
            throw new InvalidOperationException("Boss tempo scouting requires a prefix observer.");
        Stack<(SearchNode Node, int Remaining)> pending = new();
        foreach (SearchNode seed in initial)
            pending.Push((seed, options.DiscrepancyAllowance));
        initial.Clear();
        targetReached = false;
        int maximumTurn = _startTurnNumber;
        int peakPending = pending.Count;
        int discrepancyLeaves = 0;
        string stop = "iteration_exhausted";
        bool interrupted = false;
        try
        {
            while (pending.TryPop(out var item))
            {
                SearchNode parent = item.Node;
                List<SearchNode> children = [];
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_run.Expanded >= _profile.MaxExpandedNodes)
                    { stop = "node_limit"; break; }
                    if (clock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                    { stop = "time_limit"; interrupted = true; break; }
                    if (parent.IsTerminal)
                    {
                        observeBoundary(parent);
                        completed.Add(parent);
                        targetReached |= hpTarget(parent);
                        if (targetReached) { stop = "hp_target"; break; }
                        continue;
                    }
                    if (!beforeParent(parent, pending.Count, completed.Count))
                    { stop = "boundary"; interrupted = true; break; }
                    Tempo.RemainingDiscrepancies = item.Remaining;
                    BeginCyclePlanningLayer();
                    foreach (SearchNode child in Expand(parent)) children.Add(child);
                    List<SearchNode> boundaries = children.Where(n => n.IsTerminal || n.Turn > parent.Turn).ToList();
                    List<SearchNode> annotated = AnnotateTurnOutcomes(boundaries);
                    ReleaseDroppedSnapshots(boundaries, annotated);
                    children = children.Where(n => !n.IsTerminal && n.Turn == parent.Turn).Concat(annotated).ToList();
                    TightenPrimarySearchIncumbentAtTurnLayer(children, maximumTurn - _startTurnNumber);
                    List<SearchNode> bounded = ApplyPrimaryIncumbentBound(children);
                    ReleaseDroppedSnapshots(children, bounded);
                    children = bounded;
                    foreach (SearchNode child in children)
                    {
                        maximumTurn = Math.Max(maximumTurn, child.Turn);
                        if (child.Score > fallback.Score) fallback = child;
                        if (child.IsTerminal || child.Turn > parent.Turn) observeBoundary(child);
                        if (child.IsTerminal)
                        {
                            completed.Add(child);
                            targetReached |= hpTarget(child);
                        }
                        else if (options.ScoutTurns > 0
                            && child.Turn >= _startTurnNumber + options.ScoutTurns
                            && !child.Snapshot.PlayerDead && !child.Snapshot.HasRisk
                            && child.BoundaryReason == SearchBoundaryReason.None)
                        {
                            options.PrefixObserver!(new(child.Actions.ToArray(), child.StateKey,
                                _initialEnemyCount - System.Numerics.BitOperations.PopCount(child.Snapshot.AliveEnemyMask),
                                child.PotionCount, Retention.IntermediateScore(child)));
                        }
                    }
                    SearchNode[] ordered = children.Where(n => !n.IsTerminal && !n.Snapshot.PlayerDead)
                        .Where(n => options.ScoutTurns == 0 || n.Turn < _startTurnNumber + options.ScoutTurns)
                        // A next-turn state includes refreshed energy and draws. Compare playable
                        // continuations before that refreshed state so waiting cannot buy free tempo.
                        .OrderBy(n => n.Turn > parent.Turn)
                        .ThenByDescending(n => n.PowerCommitment is
                            { Priority: PowerRoutePriority.Strong, NetUnrealizedValue: > 0 })
                        .ThenByDescending(Retention.IntermediateScore).ToArray();
                    List<SearchNode> retained = [];
                    // Explore early deviations before spending the pass on later deviations.
                    if (ordered.Length > 0)
                    {
                        pending.Push((ordered[0], item.Remaining));
                        retained.Add(ordered[0]);
                    }
                    for (int index = ordered.Length - 1; index >= 1; index--)
                    {
                        int remaining = item.Remaining - 1;
                        if (remaining < 0) { discrepancyLeaves++; continue; }
                        pending.Push((ordered[index], remaining));
                        retained.Add(ordered[index]);
                    }
                    ReleaseDroppedSnapshots(children, retained.Concat(completed).ToList());
                    children.Clear();
                    if (completed.Count > 64)
                    {
                        List<SearchNode> ranked = Retention.RankFinal(completed);
                        ReleaseDroppedSnapshots(completed, ranked.Concat(pending.Select(p => p.Node)).Append(parent).ToList());
                        completed.Clear();
                        completed.AddRange(ranked);
                    }
                    peakPending = Math.Max(peakPending, pending.Count);
                    afterParent(parent, pending.Count, completed.Count);
                    if (targetReached) { stop = "hp_target"; break; }
                }
                finally
                {
                    foreach (SearchNode child in children) child.Snapshot.ReleaseSimulator();
                    parent.Snapshot.ReleaseSimulator();
                }
            }
            turnLayers = Math.Max(0, maximumTurn - _startTurnNumber);
            policy.Diagnostics.Info($"[CombatSolver/Test] BOSS_TEMPO_ITERATION "
                + $"discrepancies={options.DiscrepancyAllowance} stop={stop} expanded={_run.Expanded} "
                + $"transitions={_run.TransitionCount} peak_pending={peakPending} deferred={discrepancyLeaves} "
                + $"incumbent_pruned={_run.PrimaryIncumbentBranchesPruned} elapsed_ms={clock.ElapsedMilliseconds}");
            Tempo.Stop = stop;
            Tempo.Deferred = discrepancyLeaves;
            Tempo.PeakPending = peakPending;
            return interrupted;
        }
        finally
        {
            foreach (var item in pending) item.Node.Snapshot.ReleaseSimulator();
            pending.Clear();
        }
    }
}
