namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private SearchNode AttachCrossTurnSchedulingEvidence(SearchNode child)
    {
        if (child.Parent is not { } parent)
            return child;

        bool canPropagateProbe = parent.CrossTurnProbe is { }
            && !child.IsTerminal
            && child.BoundaryReason == SearchBoundaryReason.None;
        if (!canPropagateProbe)
        {
            child.CrossTurnProbe = null;
        }
        else if (child.Turn <= parent.Turn)
        {
            child.CrossTurnProbe = parent.CrossTurnProbe;
        }
        else
        {
            CrossTurnProbeState probe = parent.CrossTurnProbe!;
            CycleExitQuality quality = MeasureCycleExitQuality(
                probe.Tracker.OriginNode,
                child);
            long progressMagnitude = quality.ProgressMagnitude;
            child.CrossTurnProbe = probe with
            {
                CompletedTurnTransitions = checked(
                    probe.CompletedTurnTransitions + child.Turn - parent.Turn),
                BestKnownProgressMagnitude = Math.Max(
                    probe.BestKnownProgressMagnitude,
                    progressMagnitude),
                LastTurnImproved = progressMagnitude > probe.BestKnownProgressMagnitude,
            };
        }
        return child;
    }

    private static bool IsComparableCrossTurnOutcome(SearchBoundaryReason boundaryReason)
        => boundaryReason is not (
            SearchBoundaryReason.UnsupportedEffect or SearchBoundaryReason.PendingChoice);

    private static void PublishCrossTurnStandPatBaselines(
        SearchNode turnStart,
        IReadOnlyList<CrossTurnStandPatBaseline> baselines)
    {
        if (!ReferenceEquals(FindTurnStart(turnStart), turnStart))
            throw new InvalidOperationException("跨回合 stand-pat 基线只能属于回合起点节点。");

        if (baselines.Count == 0)
        {
            turnStart.CrossTurnStandPatBaselines = [];
            return;
        }

        List<CrossTurnStandPatBaseline> distinct = new(baselines.Count);
        foreach (CrossTurnStandPatBaseline baseline in baselines)
        {
            if (!distinct.Contains(baseline))
                distinct.Add(baseline);
        }
        turnStart.CrossTurnStandPatBaselines = distinct.ToArray();
    }

    private static void AttachCrossTurnSemanticStateEvidence(
        SearchNode node,
        SearchNode turnStart,
        IReadOnlyList<StateFingerprint> standPatKeys,
        IReadOnlyList<CrossTurnStandPatBaseline> standPatBaselines)
    {
        if (node.CrossTurnSemanticEvidenceAttached || standPatKeys.Count == 0)
            return;

        // Every key is a comparable direct EndTurn branch from the same turn start. Matching
        // any branch means that the observed difference can be explained by stand-pat branch
        // selection; only differing from all branches proves choice-dependent semantic change.
        bool changed = true;
        foreach (StateFingerprint baseline in standPatKeys)
        {
            if (node.StateKey != baseline)
                continue;
            changed = false;
            break;
        }
        node.CrossTurnSemanticEvidenceAttached = true;
        node.CrossTurnSemanticStateChanged = changed;
        CycleExitQuality candidateQuality = MeasureCycleExitQuality(turnStart, node);
        bool modeledQualityDominatedByStandPat = false;
        foreach (CrossTurnStandPatBaseline standPat in standPatBaselines)
        {
            if (!standPat.Quality.DominatesOrEquals(candidateQuality))
                continue;
            modeledQualityDominatedByStandPat = true;
            break;
        }
        // Exact semantic divergence only needs exceptional retention when the choice added no
        // gain visible to the modeled quality vector beyond an equivalent direct stand-pat.
        node.CrossTurnSemanticInvisibleToModeledQuality =
            changed && modeledQualityDominatedByStandPat;
        if (node.CrossTurnProbe is not { } probe)
            return;
        if (!modeledQualityDominatedByStandPat)
        {
            // A non-dominated modeled gain can now compete in the ordinary beam. Do not keep
            // paying for the earlier invisible-payoff lease after the gain has materialized.
            node.CrossTurnProbe = null;
            return;
        }
        node.CrossTurnProbe = probe with
        {
            SemanticStateChangeTransitions = checked(
                probe.SemanticStateChangeTransitions + (changed ? 1 : 0)),
            ConsecutiveSemanticStateChangeTransitions = changed
                ? checked(probe.ConsecutiveSemanticStateChangeTransitions + 1)
                : 0,
            LastTurnChangedSemanticState = changed,
        };
    }

    private void StartCrossTurnProbe(SearchNode node)
    {
        if (node.CrossTurnProbe != null)
            return;
        node.CrossTurnProbe = new CrossTurnProbeState(
            new CrossTurnProbeTracker(node, node.Snapshot.CycleShapeKey),
            0,
            node.CrossTurnSemanticStateChanged ? 1 : 0,
            node.CrossTurnSemanticStateChanged ? 1 : 0,
            0,
            false,
            node.CrossTurnSemanticStateChanged);
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
            || node.CrossTurnSemanticInvisibleToModeledQuality
            || node.FutureSoldHp > availableFutureSoldHp;
    }
}
