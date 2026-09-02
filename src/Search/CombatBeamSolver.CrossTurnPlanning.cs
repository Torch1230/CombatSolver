namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private SearchNode AttachCrossTurnSchedulingEvidence(SearchNode child)
    {
        if (child.Parent?.CrossTurnProbe is not { } probe
            || child.IsTerminal
            || child.BoundaryReason != SearchBoundaryReason.None)
        {
            child.CrossTurnProbe = null;
            return child;
        }

        if (child.Turn <= child.Parent.Turn)
        {
            child.CrossTurnProbe = probe;
            return child;
        }

        CycleExitQuality quality = MeasureCycleExitQuality(
            probe.Tracker.OriginNode,
            child);
        long progressMagnitude = quality.ProgressMagnitude;
        child.CrossTurnProbe = probe with
        {
            CompletedTurnTransitions = checked(
                probe.CompletedTurnTransitions + child.Turn - child.Parent.Turn),
            BestKnownProgressMagnitude = Math.Max(
                probe.BestKnownProgressMagnitude,
                progressMagnitude),
            LastTurnImproved = progressMagnitude > probe.BestKnownProgressMagnitude,
        };
        return child;
    }

    private void StartCrossTurnProbe(SearchNode node)
    {
        if (node.CrossTurnProbe != null)
            return;
        node.CrossTurnProbe = new CrossTurnProbeState(
            new CrossTurnProbeTracker(node, node.Snapshot.CycleShapeKey),
            0,
            0,
            false);
        _run.CrossTurnCandidatesProtected++;
    }

    private bool RequiresCrossTurnPlanning(SearchNode node)
    {
        if (node.IsTerminal
            || node.BoundaryReason != SearchBoundaryReason.None
            || node.CycleExitProbe != null
            || node.Outcome == null)
        {
            return false;
        }
        int availableFutureSoldHp = Math.Max(
            0,
            SoldHpThreshold() - battleDamage.SoldHpCommitted);
        return node.CombatProgress.TurnsWithoutProgress > 0
            || node.FutureSoldHp > availableFutureSoldHp;
    }
}
