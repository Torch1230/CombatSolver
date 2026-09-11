using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // A solver instance is one fixed worker lane. No candidate retains these mutable readers.
    private CompactReplayLane? _compactReplayLane;

    private sealed class CompactReplayLane(CompactCombatRoot root)
    {
        internal readonly ResumableDiscardProgram Program = root.Initial.Open();
        internal readonly CompactPlanReplay Replay = new(root.Adapter);
        internal readonly CompactDiscardReadView Reader = root.Adapter.CreateReadView();
    }

    private bool TryCompactReplay(IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent,
        int startingTurn, int priorActionCount, ReplayForkSeed? seed, out SimulationSnapshot? snapshot)
    {
        snapshot = null;
        if (policy.CompactRoot is not { } compactRoot) return false;
        CompactCombatCandidate? candidate = parent?.CompactCandidate;
        if (parent != null && candidate == null) return false;
        if (candidate != null && !ReferenceEquals(candidate.Root, compactRoot))
            throw new InvalidOperationException("Compact replay parent belongs to another captured root.");
        if (parent?.BoundaryReason is not (null or SearchBoundaryReason.None))
            throw new InvalidOperationException("Compact replay cannot extend a search boundary.");
        if (parent == null && seed != null)
            throw new InvalidOperationException("Compact root replay cannot consume a parent seed.");
        cancellationToken.ThrowIfCancellationRequested();
        var lane = _compactReplayLane ??= new(compactRoot);
        var program = lane.Program;
        (candidate?.Values ?? compactRoot.Initial).RestoreInto(program);
        if (program.PlayerTurn != (parent == null ? _startTurnNumber : startingTurn))
            throw new InvalidOperationException("Compact replay clock differs from the parent.");
        int shuffles = parent?.ShufflesCrossed ?? 0;
        foreach (PlanAction action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (program.Ending)
                throw new InvalidOperationException("Compact replay contains actions after the terminal boundary.");
            int eventStart = program.EventCount;
            if (!lane.Replay.TryExecute(program, action, cancellationToken))
            {
                // The exact old pending-choice representation still owns this boundary.
                // No logical counters or seed ownership have been consumed by this attempt.
                compactRoot.RecordReplay(false);
                return false;
            }
            for (int index = eventStart; index < program.EventCount; index++)
            {
                var item = program.EventAt(index);
                if (item.Kind != ResumableDiscardProgram.EventKind.Shuffle
                    || action.Kind == PlanActionKind.EndTurn && item.Card != -1) continue;
                // Preserve the existing search metric: once per card, or the normal
                // round draw. A Sly child shuffle after round draw is a separate event.
                shuffles++;
                break;
            }
        }
        ForkableSet<uint> deaths = seed != null ? seed.TakeCompact(candidate!)
            : parent == null ? [] : ((ForkableSet<uint>)parent.ProcessedEnemyDeaths).Fork();
        for (int index = 1; index < program.CreatureCount; index++)
            if (program.Creature(index).CurrentHp <= 0 && compactRoot.Adapter.Creature(index).CombatId is uint combatId)
                deaths.Add(combatId);
        lane.Reader.Read(program);
        snapshot = SnapshotFromReadView(lane.Reader, program.PlayerTurn, priorActionCount + actions.Count,
            shuffles, SearchBoundaryReason.None, deaths);
        snapshot.AttachCompact(new(compactRoot, program.Freeze(), lane.Reader.LastImpureHistoryIndex));
        if (parent == null) _run.ReplayCount++;
        else
        {
            _run.TransitionCount += actions.Count;
            if (seed == null) _run.ForkCount++;
        }
        compactRoot.RecordReplay(true);
        return true;
    }
}
