namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const int MaximumDetectedCyclePeriodActions = 32;
    private const int BaseCycleExitProbeActions = 8;
    private const int MaximumCycleExitProbeActions = 32;
    private const int MaximumCycleExitProbeTurnTransitions = 2;
    private const byte MaximumCycleFamilyImprovementEpoch = 4;
    private const int MinimumCycleFamilyDepthBudget = 8;
    private const int MaximumBaseCycleFamilyDepthBudget = 32;
    private const int MaximumCycleFamilyDepthBudget = 128;
    private const int MinimumCycleFamilyProbeStartBudget = 4;
    private const int MaximumCycleFamilyProbeStartBudget = 16;
    private const int BaseCycleFamilyProbeExpansionBudget = 64;
    private const int MaximumCycleFamilyProbeExpansionBudget = 256;
    private const int MinimumCycleFamilyActionHorizon = 32;
    private const int MaximumBaseCycleFamilyActionHorizon = 64;
    private const int MaximumCycleFamilyActionHorizon = 512;

    private SearchNode AttachCycleSchedulingEvidence(SearchNode child)
    {
        child = AttachOrderedMutationLineage(child);
        child = AttachCycleEvidence(child);
        AttachPropagatedCycleExitProbe(child);
        ObserveCycleExitProgress(child);
        child = AttachCrossTurnSchedulingEvidence(child);
        ObserveSearchPath(child, SearchPathObservationStage.Generated, "resolved_transition");
        return child;
    }

    private SearchNode AttachCycleEvidence(SearchNode child)
    {
        if (child.Parent == null
            || child.Action == null
            || child.IsTerminal
            || child.Turn != child.Parent.Turn
            || child.BoundaryReason != SearchBoundaryReason.None)
        {
            return AttachCycleProbeLease(child);
        }

        // First locate same structural shapes with pointer-only work. Only routes that actually
        // recur pay for action hashing. The second pass hashes every edge at most once, so raising
        // the period window does not turn the hot path into O(period²) action/choice hashing.
        Span<bool> shapeRecursAt = stackalloc bool[MaximumDetectedCyclePeriodActions + 1];
        shapeRecursAt.Clear();
        int maximumRecurringPeriod = 0;
        SearchNode shapeCursor = child;
        for (int actionCount = 1;
             actionCount <= MaximumDetectedCyclePeriodActions
                 && shapeCursor.Parent is { } ancestor;
             actionCount++, shapeCursor = ancestor)
        {
            if (ancestor.Turn != child.Turn)
                break;
            if (ancestor.Snapshot.CycleShapeKey != child.Snapshot.CycleShapeKey)
                continue;
            shapeRecursAt[actionCount] = true;
            maximumRecurringPeriod = actionCount;
        }
        if (maximumRecurringPeriod == 0)
            return AttachCycleProbeLease(child);

        Span<StateFingerprint> actionKeys =
            stackalloc StateFingerprint[MaximumDetectedCyclePeriodActions * 2];
        Span<bool> damagePhases = stackalloc bool[MaximumDetectedCyclePeriodActions * 2];
        int actionKeyCount = 0;
        SearchNode actionCursor = child;
        int maximumActionKeys = maximumRecurringPeriod * 2;
        while (actionKeyCount < maximumActionKeys
               && actionCursor.Parent is { } actionParent
               && actionParent.Turn == child.Turn
               && actionCursor.Action is { } action
               && action.Kind is PlanActionKind.PlayCard or PlanActionKind.UsePotion
               && !action.EndsPlayerTurn)
        {
            damagePhases[actionKeyCount] = EnemyDurabilityProgress.PositiveReduction(
                actionParent.Snapshot.EnemyDurabilityByCombatId,
                actionCursor.Snapshot.EnemyDurabilityByCombatId) > 0;
            actionKeys[actionKeyCount++] = BuildCycleActionKey(action);
            actionCursor = actionParent;
        }

        StateFingerprint fallbackSequenceKey = default;
        CycleTransitionDelta fallbackDelta = default;
        SearchNode? fallbackAncestor = null;
        int fallbackActionCount = 0;
        int fallbackTotalStructuralRepetitions = 1;
        CycleSearchState? consistentCycle = null;
        StateFingerprintBuilder sequenceKeyBuilder = new();
        sequenceKeyBuilder.Add('S');
        SearchNode cursor = child;
        for (int actionCount = 1;
             actionCount <= maximumRecurringPeriod
                 && actionCount <= actionKeyCount
                 && cursor.Parent is { } ancestor;
             actionCount++, cursor = ancestor)
        {
            StateFingerprint actionKey = actionKeys[actionCount - 1];
            sequenceKeyBuilder.Add(actionKey.First);
            sequenceKeyBuilder.Add(actionKey.Second);
            if (!shapeRecursAt[actionCount])
                continue;

            StateFingerprintBuilder sequenceWithLength = sequenceKeyBuilder;
            sequenceWithLength.Add(actionCount);
            StateFingerprint sequenceKey = sequenceWithLength.Finish();
            CycleTransitionDelta delta = CycleTransitionDelta.Between(
                ancestor.Snapshot,
                child.Snapshot);
            CycleSearchState? prior = ancestor.Cycle;
            bool continuesPrior = prior != null
                && prior.ShapeKey == child.Snapshot.CycleShapeKey
                && prior.SequenceKey == sequenceKey
                && prior.PeriodActions == actionCount;
            if (continuesPrior
                && prior!.HasConsistentDelta
                && prior.LastDelta == delta)
            {
                EnemyDurabilityVector enemyFloor = EnemyDurabilityProgress.MergeMinimum(
                    prior.EnemyDurabilityFloor,
                    child.Snapshot.EnemyDurabilityByCombatId,
                    out bool hasNewEnemyProgress);
                CycleSearchState candidate = new(
                    child.Snapshot.CycleShapeKey,
                    sequenceKey,
                    actionCount,
                    prior!.Repetitions + 1,
                    delta,
                    true)
                {
                    PriorCycleEndpoint = ancestor,
                    PriorProjectedPlayerHp = ancestor.Snapshot.ProjectedPlayerHp,
                    EnemyDurabilityFloor = enemyFloor,
                    HasNewEnemyDurabilityProgress = hasNewEnemyProgress,
                    HasConsistentDamagePhases = HasMatchingCycleDamagePhases(
                        damagePhases[..actionKeyCount], actionCount),
                    HasExactStateChange = ancestor.StateKey != child.StateKey,
                    TotalStructuralRepetitions = prior.TotalStructuralRepetitions + 1,
                };
                consistentCycle = SelectPreferredConsistentCycleEvidence(consistentCycle, candidate);
                if (HasCertifiedCycleDamageProgress(consistentCycle))
                    return AttachCycleProbeLease(AttachSelectedCycle(child, consistentCycle));
                continue;
            }

            bool hasPriorWindow = actionKeyCount >= actionCount * 2;
            for (int actionIndex = 0; hasPriorWindow && actionIndex < actionCount; actionIndex++)
                hasPriorWindow = actionKeys[actionIndex] == actionKeys[actionCount + actionIndex];
            SearchNode priorAncestor = ancestor;
            for (int actionIndex = 0; hasPriorWindow && actionIndex < actionCount; actionIndex++)
            {
                if (priorAncestor.Parent is not { } priorParent)
                {
                    hasPriorWindow = false;
                    break;
                }
                priorAncestor = priorParent;
            }
            if (hasPriorWindow
                && priorAncestor.Snapshot.CycleShapeKey == child.Snapshot.CycleShapeKey)
            {
                EnemyDurabilityVector priorEnemyFloor = EnemyDurabilityProgress.MergeMinimum(
                    priorAncestor.Snapshot.EnemyDurabilityByCombatId,
                    ancestor.Snapshot.EnemyDurabilityByCombatId,
                    out _);
                EnemyDurabilityVector enemyFloor = EnemyDurabilityProgress.MergeMinimum(
                    priorEnemyFloor,
                    child.Snapshot.EnemyDurabilityByCombatId,
                    out bool hasNewWindowEnemyProgress);
                bool hasConsistentDelta = CycleTransitionDelta.Between(
                    priorAncestor.Snapshot,
                    ancestor.Snapshot) == delta;
                bool hasConsistentDamagePhases = HasMatchingCycleDamagePhases(
                    damagePhases[..actionKeyCount], actionCount);
                if (HasRepeatableCycleEvidence(
                        hasConsistentDelta,
                        hasNewWindowEnemyProgress,
                        hasConsistentDamagePhases))
                {
                    // Growing or shrinking damage can repeat the same action/shape phases
                    // without equal magnitudes. Preserve the real delta-consistency fact;
                    // the separate phase certificate and actual new low justify continuation.
                    CycleSearchState candidate = new(
                        child.Snapshot.CycleShapeKey,
                        sequenceKey,
                        actionCount,
                        continuesPrior ? prior!.Repetitions + 1 : 2,
                        delta,
                        hasConsistentDelta)
                    {
                        PriorCycleEndpoint = ancestor,
                        PriorProjectedPlayerHp = ancestor.Snapshot.ProjectedPlayerHp,
                        EnemyDurabilityFloor = enemyFloor,
                        HasNewEnemyDurabilityProgress = hasNewWindowEnemyProgress,
                        HasConsistentDamagePhases = hasConsistentDamagePhases,
                        HasExactStateChange = ancestor.StateKey != child.StateKey,
                        TotalStructuralRepetitions = continuesPrior
                            ? prior!.TotalStructuralRepetitions + 1
                            : 2,
                    };
                    consistentCycle = SelectPreferredConsistentCycleEvidence(consistentCycle, candidate);
                    if (HasCertifiedCycleDamageProgress(consistentCycle))
                        return AttachCycleProbeLease(AttachSelectedCycle(child, consistentCycle));
                    continue;
                }
            }

            // A shorter same-shape recurrence can be only one phase of an alternating
            // sequence (A/B, duplicate occurrences, or a longer control loop). Keep looking
            // for a two-window match; the first observed recurrence remains only a probe seed.
            if (fallbackAncestor == null)
            {
                fallbackSequenceKey = sequenceKey;
                fallbackDelta = delta;
                fallbackAncestor = ancestor;
                fallbackActionCount = actionCount;
                fallbackTotalStructuralRepetitions = continuesPrior
                    ? prior!.TotalStructuralRepetitions + 1
                    : 1;
            }
        }
        if (consistentCycle != null)
            return AttachCycleProbeLease(AttachSelectedCycle(child, consistentCycle));
        if (fallbackAncestor == null)
            return AttachCycleProbeLease(child);
        EnemyDurabilityVector fallbackEnemyFloor = EnemyDurabilityProgress.MergeMinimum(
            fallbackAncestor.Snapshot.EnemyDurabilityByCombatId,
            child.Snapshot.EnemyDurabilityByCombatId,
            out bool hasNewFallbackEnemyProgress);
        CycleSearchState fallback = new(
            child.Snapshot.CycleShapeKey,
            fallbackSequenceKey,
            fallbackActionCount,
            1,
            fallbackDelta,
            false)
        {
            PriorProjectedPlayerHp = fallbackAncestor.Snapshot.ProjectedPlayerHp,
            EnemyDurabilityFloor = fallbackEnemyFloor,
            HasNewEnemyDurabilityProgress = hasNewFallbackEnemyProgress,
            HasExactStateChange = fallbackAncestor.StateKey != child.StateKey,
            TotalStructuralRepetitions = fallbackTotalStructuralRepetitions,
        };
        return AttachCycleProbeLease(AttachSelectedCycle(child, fallback));
    }

    private static CycleSearchState SelectPreferredConsistentCycleEvidence(
        CycleSearchState? current,
        CycleSearchState candidate)
        // A short structural recurrence can be a quiet phase of a longer productive period.
        // Inspect the bounded period window before treating that quiet phase as the whole loop.
        => current == null
            || HasCertifiedCycleDamageProgress(candidate) && !HasCertifiedCycleDamageProgress(current)
            || HasCertifiedCycleDamageProgress(candidate) == HasCertifiedCycleDamageProgress(current)
                && candidate.PeriodActions < current.PeriodActions
            ? candidate
            : current;

    private static bool HasCertifiedCycleDamageProgress(CycleSearchState cycle)
        => cycle.HasNewEnemyDurabilityProgress && cycle.HasConsistentDamagePhases;

    private static bool HasRepeatableCycleEvidence(
        bool hasConsistentDelta,
        bool hasNewEnemyDurabilityProgress,
        bool hasConsistentDamagePhases)
        => hasConsistentDelta
            || hasNewEnemyDurabilityProgress && hasConsistentDamagePhases;

    private static bool HasMatchingCycleDamagePhases(ReadOnlySpan<bool> damagePhases, int period)
    {
        if (period <= 0 || damagePhases.Length < period * 2)
            return false;
        for (int index = 0; index < period; index++)
        {
            if (damagePhases[index] != damagePhases[index + period])
                return false;
        }
        return true;
    }

    private SearchNode AttachSelectedCycle(SearchNode child, CycleSearchState cycle)
    {
        _run.CycleShapesDetected++;
        if (_detailedDiagnostics
            && (cycle.Repetitions <= 3
                || (cycle.Repetitions & (cycle.Repetitions - 1)) == 0))
        {
            CycleTransitionDelta delta = cycle.LastDelta;
            policy.Diagnostics.Info(
                $"[CombatSolver/Debug] CYCLE_SHAPE actions={cycle.PeriodActions} " +
                $"repetitions={cycle.Repetitions} action_count={child.ActionCount} " +
                $"enemy_delta={delta.EnemyHp} enemy_block_delta={delta.EnemyBlock} " +
                $"hp_delta={delta.PlayerHp} " +
                $"block_delta={delta.PlayerBlock} energy_delta={delta.Energy} " +
                $"sequence={DescribeCycleActions(child, cycle.PeriodActions)}");
        }
        return AttachCycleState(child, cycle);
    }

    private static SearchNode AttachCycleState(
        SearchNode child,
        CycleSearchState cycle)
    {
        // The caller has just materialized this child and has not published it to a candidate
        // collection. Scheduling metadata may therefore be attached in place without changing
        // any simulator-state ownership or exposing a partial node to another worker.
        child.Cycle = cycle;
        return child;
    }

    private static void AnnotateCycleExitProgress(
        SearchNode parent,
        IEnumerable<SearchNode> directChildren)
    {
        if (parent.CycleProbeLease is not { } lease)
            return;
        SearchNode[] propagated = directChildren
            .Where(child => child.CycleProbeLease is { } childLease
                && ReferenceEquals(childLease.Tracker, lease.Tracker))
            .ToArray();
        if (propagated.Length == 0)
            return;
        CycleProbeTracker[] trackers = new CycleProbeTracker[propagated.Length];
        trackers[0] = lease.Tracker;
        // Clone every sibling from the unchanged common baseline before any branch-specific
        // exact-state rearm mutates one tracker. This keeps DOP and choice order irrelevant.
        for (int index = 1; index < trackers.Length; index++)
            trackers[index] = lease.Tracker.Clone();

        for (int index = 0; index < propagated.Length; index++)
        {
            SearchNode child = propagated[index];
            CycleProbeLease childLease = child.CycleProbeLease!;
            CycleProbeTracker tracker = trackers[index];
            bool improvedSinceWrap = lease.ImprovedSinceWrap
                || tracker.ExitQualityEpoch > lease.ObservedExitQualityEpoch;
            bool completedRepetition = childLease.NextActionIndex == 0;
            if (completedRepetition
                && child.Cycle is { } cycle
                && ShouldReprobeCycleExits(cycle))
            {
                tracker.RearmExitProbes();
            }
            child.CycleProbeLease = childLease with
            {
                Tracker = tracker,
                ImprovedSinceWrap = completedRepetition ? false : improvedSinceWrap,
                LastCompletedRepetitionImproved = completedRepetition
                    ? improvedSinceWrap
                    : lease.LastCompletedRepetitionImproved,
                ObservedExitQualityEpoch = completedRepetition
                    ? tracker.ExitQualityEpoch
                    : lease.ObservedExitQualityEpoch,
            };
        }
    }

    private static void ObserveCycleExitProgress(SearchNode child)
    {
        if (child.Parent is not { CycleProbeLease: { } lease } parent
            || child.Action is not { } action
            || child.CycleProbeLease != null
            || child.CycleExitProbe != null
            || child.IsTerminal)
        {
            return;
        }
        StateFingerprint actionKey = BuildCycleActionKey(action);
        // Expansion workers must not mutate a tracker shared by sibling lanes. The
        // coordinator commits this immutable observation in deterministic child order.
        child.PendingCycleExitObservation = new PendingCycleExitObservation(
            lease.Tracker,
            parent,
            lease.NextActionIndex,
            actionKey,
            MeasureCycleExitQuality(parent, child));
    }

    private static void AttachPropagatedCycleExitProbe(SearchNode child)
    {
        if (child.CycleExitProbe != null
            || child.Parent is not
                {
                    CycleExitProbe:
                    {
                        RemainingActions: > 0,
                        RemainingEpochActions: > 0,
                    } probe,
                }
                parent)
        {
            return;
        }
        // Feed the actual bounded lookahead outcome back into the same phase/action Pareto
        // frontier. This is measured from the loop exit origin, not edge-by-edge, so an
        // unchanged setup edge can be retried when its second-or-later action payoff improves.
        int turnTransitions = probe.RemainingTurnTransitions
            - (child.Turn > parent.Turn ? 1 : 0);
        int remainingActions = probe.RemainingActions - 1;
        int remainingEpochActions = probe.RemainingEpochActions - 1;
        bool completesProbe = child.IsTerminal
            || turnTransitions < 0
            || remainingActions <= 0
            || remainingEpochActions <= 0;
        child.CycleExitObservation = new CycleExitObservation(
            probe.OriginTracker,
            probe.OriginNode.ActionCount,
            probe.OriginPhaseIndex,
            probe.ExitActionKey,
            probe.OriginGeneration,
            MeasureCycleExitQuality(probe.OriginNode, child),
            completesProbe);
        if (completesProbe)
            return;
        child.CycleExitProbe = probe with
        {
            RemainingActions = remainingActions,
            RemainingEpochActions = remainingEpochActions,
            RemainingTurnTransitions = turnTransitions,
        };
    }

    private void CommitCycleExitObservation(SearchNode child)
        => CommitCycleExitObservationCore(child, _run.CycleFamilyLedger);

    private static void CommitCycleExitObservationCore(
        SearchNode child,
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers)
    {
        if (child.CycleExitObservation is { } observation)
        {
            _ = observation.OriginTracker.ObserveExit(
                observation.OriginPhaseIndex,
                observation.ExitActionKey,
                observation.Quality,
                out bool qualityImproved);
            if (qualityImproved)
            {
                TryMarkCycleFamilyImproved(
                    ledgers,
                    observation.OriginTracker,
                    observation.OriginActionCount,
                    observation.Quality);
            }
            if (observation.CompletesProbe)
            {
                observation.OriginTracker.CompleteExitProbe(
                    observation.OriginPhaseIndex,
                    observation.ExitActionKey,
                    observation.OriginGeneration);
            }
            child.CycleExitObservation = null;
        }
    }

    private static bool TryMaterializePendingCycleExitObservation(
        SearchNode child,
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers)
    {
        PendingCycleExitObservation? pending = child.PendingCycleExitObservation;
        child.PendingCycleExitObservation = null;
        if (pending == null || !IsValidPendingCycleExitObservation(child, pending))
            return false;

        CycleProbeLease currentLease = pending.OriginNode.CycleProbeLease!;
        long exitGeneration = pending.OriginTracker.ObserveExit(
            pending.OriginPhaseIndex,
            pending.ExitActionKey,
            pending.Quality,
            out bool qualityImproved);
        if (exitGeneration <= 0)
            return false;

        if (qualityImproved)
        {
            TryMarkCycleFamilyImproved(
                ledgers,
                pending.OriginTracker,
                pending.OriginNode.ActionCount,
                pending.Quality);
            pending.OriginNode.CycleProbeLease = currentLease with
            {
                ImprovedSinceWrap = true,
            };
        }
        byte activeEpoch = ActiveCycleFamilyImprovementEpoch(
            ledgers,
            pending.OriginTracker);
        child.CycleExitProbe = new CycleExitProbeState(
            pending.OriginTracker,
            pending.OriginNode,
            pending.OriginPhaseIndex,
            pending.OriginTracker.ShapeKey,
            pending.OriginTracker.SequenceKey,
            pending.OriginTracker.PeriodActions,
            pending.ExitActionKey,
            exitGeneration,
            MaximumCycleExitProbeActions,
            CycleExitProbeActionBudget(activeEpoch),
            MaximumCycleExitProbeTurnTransitions);
        return true;
    }

    private static bool HasValidPendingCycleExitObservation(SearchNode child)
        => child.PendingCycleExitObservation is { } pending
            && IsValidPendingCycleExitObservation(child, pending);

    private static bool IsValidPendingCycleExitObservation(
        SearchNode child,
        PendingCycleExitObservation pending)
        => child.CycleProbeLease == null
            && child.CycleExitProbe == null
            && !child.IsTerminal
            && child.BoundaryReason == SearchBoundaryReason.None
            && child.Action is { } action
            && ReferenceEquals(child.Parent, pending.OriginNode)
            && pending.OriginPhaseIndex >= 0
            && pending.OriginPhaseIndex < pending.OriginTracker.PeriodActions
            && BuildCycleActionKey(action) == pending.ExitActionKey
            && pending.OriginNode.CycleProbeLease is { } currentLease
            && ReferenceEquals(currentLease.Tracker, pending.OriginTracker)
            && currentLease.NextActionIndex == pending.OriginPhaseIndex;

    private static SearchNode? MaterializeAdmittedCycleExitObservation(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers)
    {
        List<SearchNode>? pendingCandidates = null;
        int bestMaxHp = 0;
        foreach (SearchNode candidate in candidates)
        {
            if (!HasValidPendingCycleExitObservation(candidate))
                continue;
            pendingCandidates ??= [];
            pendingCandidates.Add(candidate);
            bestMaxHp = Math.Max(bestMaxHp, candidate.Snapshot.PlayerMaxHp);
        }

        SearchNode? admitted = null;
        if (pendingCandidates != null)
        {
            pendingCandidates.Sort((left, right) =>
                ComparePendingCycleExitAdmissionCandidates(left, right, bestMaxHp));
            foreach (SearchNode candidate in pendingCandidates)
            {
                if (!TryMaterializePendingCycleExitObservation(candidate, ledgers))
                    continue;
                admitted = candidate;
                break;
            }
        }

        // Pending observations are coordinator-local claims. A candidate that did not win the
        // single bounded exit lane may remain an ordinary route, but it must never retain the
        // origin tracker or cross a frontier as an unissued scheduling obligation.
        foreach (SearchNode candidate in candidates)
            candidate.PendingCycleExitObservation = null;
        return admitted;
    }

    private static int ComparePendingCycleExitAdmissionCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        int comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static CycleExitQuality MeasureCycleExitQuality(
        SearchNode beforeNode,
        SearchNode afterNode)
    {
        SimulationSnapshot before = beforeNode.Snapshot;
        SimulationSnapshot after = afterNode.Snapshot;
        return new CycleExitQuality(
            EnemyDurabilityProgress.PositiveReduction(
                before.EnemyDurabilityByCombatId,
                after.EnemyDurabilityByCombatId),
            Math.Max(0L, (long)after.OffensiveProgressValue - before.OffensiveProgressValue),
            Math.Max(0L, (long)after.DelayedDamageValue - before.DelayedDamageValue),
            Math.Max(0L, (long)after.PersistentBuffValue - before.PersistentBuffValue),
            Math.Max(0L, (long)after.StrategicEffects.RetentionValue
                - before.StrategicEffects.RetentionValue),
            Math.Max(0L, (long)after.FutureResourceValue - before.FutureResourceValue),
            Math.Max(0L, (long)after.LongTermResourceValue - before.LongTermResourceValue),
            Math.Max(0L, (long)after.ReplayPotentialValue - before.ReplayPotentialValue),
            Math.Max(0L, (long)after.RetainedAttackValue - before.RetainedAttackValue),
            Math.Max(0L, (long)after.ProjectedPlayerHp - before.ProjectedPlayerHp),
            Math.Max(0L, UsefulDefensiveBlockReserve(after)
                - UsefulDefensiveBlockReserve(before)),
            Math.Max(0L, (long)after.PlayerHp - before.PlayerHp),
            Math.Max(0L, (long)after.Energy - before.Energy),
            Math.Max(0L, (long)after.Stars - before.Stars),
            Math.Max(0L, (long)after.EnemyStrengthSuppression
                - before.EnemyStrengthSuppression),
            Math.Max(0L, (long)after.EnemyWeakTurns - before.EnemyWeakTurns),
            Math.Max(0L, (long)after.EnemyVulnerableTurns - before.EnemyVulnerableTurns),
            Math.Max(0L, (long)before.OutstandingStolenResource
                - after.OutstandingStolenResource),
            Math.Max(0L, (long)before.SandpitRemaining - after.SandpitRemaining),
            Math.Max(0L, (long)after.OstyHp - before.OstyHp),
            Math.Max(0L, (long)after.OstyMaxHp - before.OstyMaxHp),
            Math.Max(0L, (long)before.LiveDeckClutter - after.LiveDeckClutter),
            Math.Max(0L, (long)before.LiveDeckSize - after.LiveDeckSize),
            (long)after.CumulativePlayerHpLost - before.CumulativePlayerHpLost
                + before.PlayerMaxHp - after.PlayerMaxHp,
            (long)before.PlayerHp - after.PlayerHp
                + before.PlayerMaxHp - after.PlayerMaxHp,
            (long)before.ProjectedPlayerHp - after.ProjectedPlayerHp,
            (long)afterNode.FutureSoldHp - beforeNode.FutureSoldHp,
            (long)afterNode.PotionStrategicCost - beforeNode.PotionStrategicCost,
            (long)afterNode.PotionCount - beforeNode.PotionCount);
    }

    private static int UsefulDefensiveBlockReserve(SimulationSnapshot snapshot)
    {
        // ProjectedPlayerHp already prices the known incoming attack, and block-to-damage
        // conversions are included in OffensiveProgressValue. Preserve a finite additional
        // reserve for retained block and later attacks without treating unbounded same-turn
        // block accumulation as endlessly improving search quality.
        return Math.Min(
            Math.Max(0, snapshot.PlayerBlock),
            Math.Max(0, snapshot.PlayerMaxHp));
    }

    private static int ScaleCycleFamilyBudget(
        int baseBudget,
        byte activeEpoch,
        int maximumBudget)
        => (int)Math.Min(
            maximumBudget,
            (long)baseBudget << Math.Min(
                activeEpoch,
                MaximumCycleFamilyImprovementEpoch));

    private static int CycleFamilyDepthBudget(
        int periodActions,
        byte activeEpoch)
    {
        int period = Math.Clamp(periodActions, 1, MaximumDetectedCyclePeriodActions);
        int baseBudget = Math.Clamp(
            period * 2,
            MinimumCycleFamilyDepthBudget,
            MaximumBaseCycleFamilyDepthBudget);
        return ScaleCycleFamilyBudget(
            baseBudget,
            activeEpoch,
            MaximumCycleFamilyDepthBudget);
    }

    private static int CycleFamilyProbeStartBudget(
        int periodActions,
        byte activeEpoch)
    {
        int period = Math.Clamp(periodActions, 1, MaximumDetectedCyclePeriodActions);
        int baseBudget = Math.Clamp(
            period,
            MinimumCycleFamilyProbeStartBudget,
            MaximumCycleFamilyProbeStartBudget);
        return ScaleCycleFamilyBudget(
            baseBudget,
            activeEpoch,
            MaximumCycleFamilyProbeStartBudget);
    }

    private static int CycleFamilyProbeExpansionBudget(byte activeEpoch)
        => ScaleCycleFamilyBudget(
            BaseCycleFamilyProbeExpansionBudget,
            activeEpoch,
            MaximumCycleFamilyProbeExpansionBudget);

    private static int CycleExitProbeActionBudget(byte activeEpoch)
        => ScaleCycleFamilyBudget(
            BaseCycleExitProbeActions,
            activeEpoch,
            MaximumCycleExitProbeActions);

    private static int CycleRepetitionBudget(
        int periodActions,
        byte activeEpoch)
    {
        int period = Math.Clamp(periodActions, 1, MaximumDetectedCyclePeriodActions);
        int baseActionHorizon = Math.Clamp(
            period * 4,
            MinimumCycleFamilyActionHorizon,
            MaximumBaseCycleFamilyActionHorizon);
        int actionHorizon = ScaleCycleFamilyBudget(
            baseActionHorizon,
            activeEpoch,
            MaximumCycleFamilyActionHorizon);
        return Math.Max(2, actionHorizon / period);
    }

    private static bool ShouldStopUnproductiveCycle(
        SearchNode continuingCycle)
    {
        CycleSearchState? cycle = continuingCycle.Cycle;
        if (cycle == null
            || !cycle.HasConsistentDelta
            || cycle.LastDelta.EnemyHp != 0
            || cycle.LastDelta.EnemyBlock < 0
            || cycle.HasNewEnemyDurabilityProgress
            || cycle.LastDelta.AliveEnemyCount != 0
            || cycle.LastDelta.PlayerHp > 0
            || cycle.LastDelta.CumulativePlayerHpLost < 0
            || HasDurableCycleProgress(cycle.LastDelta)
            || continuingCycle.Snapshot.ProjectedPlayerHp
                > cycle.PriorProjectedPlayerHp)
        {
            return false;
        }

        return !HasImprovingExitEvidence(continuingCycle);
    }

    private static bool HasDurableCycleProgress(CycleTransitionDelta delta)
        => delta.PlayerMaxHp > 0
            || delta.Energy > 0
            || delta.Stars > 0
            || delta.LongTermResourceValue > 0
            || delta.PersistentBuffValue > 0
            || delta.StrategicRetentionValue > 0
            || delta.FutureResourceValue > 0
            || delta.DelayedDamageValue > 0
            || delta.ReplayPotentialValue > 0
            || delta.RetainedAttackValue > 0
            || delta.EnemyStrengthSuppression > 0
            || delta.EnemyWeakTurns > 0
            || delta.EnemyVulnerableTurns > 0
            || delta.OutstandingStolenResource < 0
            || delta.SandpitRemaining < 0
            || delta.OstyHp > 0
            || delta.OstyMaxHp > 0
            || delta.OffensiveProgressValue > 0;

    private static bool ShouldReprobeCycleExits(CycleSearchState cycle)
        => cycle.HasExactStateChange
            || cycle.LastDelta.PlayerHp != 0
            || cycle.LastDelta.PlayerMaxHp != 0
            || cycle.LastDelta.CumulativePlayerHpLost != 0
            || HasDurableCycleProgress(cycle.LastDelta);

    private static bool TryGetOrCreateCycleFamilyLedger(
        IDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers,
        CanonicalCycleFamilyKey family,
        bool allowCreate,
        out CycleFamilyLedgerEntry ledger)
    {
        if (ledgers.TryGetValue(family, out ledger!))
            return true;
        if (!allowCreate)
            return false;
        ledger = new CycleFamilyLedgerEntry();
        ledgers.Add(family, ledger);
        return true;
    }

    private static int CyclePlanningBudgetConsumedForTurn(
        IReadOnlyDictionary<int, int> consumedByTurn,
        int turn)
        => consumedByTurn.TryGetValue(turn, out int consumed) ? consumed : 0;

    private static bool HasCyclePlanningBudgetForTurn(
        IReadOnlyDictionary<int, int> consumedByTurn,
        int turn,
        int budget)
        => CyclePlanningBudgetConsumedForTurn(consumedByTurn, turn) < budget;

    private static void ChargeCyclePlanningBudgetForTurn(
        IDictionary<int, int> consumedByTurn,
        int turn,
        int budget)
    {
        int consumed = consumedByTurn.TryGetValue(turn, out int current) ? current : 0;
        if (consumed >= budget)
        {
            throw new InvalidOperationException(
                $"循环规划第 {turn} 回合预算重复扣费：{consumed}/{budget}。");
        }
        consumedByTurn[turn] = checked(consumed + 1);
    }

    private void BeginCyclePlanningLayer()
    {
        // A serial parent can publish a better exit before the next parent is expanded, while
        // one parallel wave publishes every exit only after all parents are prepared. Advance at
        // most one requested epoch and freeze it at the deterministic play-depth boundary so both
        // modes use identical budgets; improvements observed here become eligible next depth.
        foreach (CycleFamilyLedgerEntry ledger in _run.CycleFamilyLedger.Values)
            AdvanceCycleFamilyImprovementEpochAtLayerStart(ledger);
    }

    private static void RequestCycleFamilyImprovementEpoch(
        CycleFamilyLedgerEntry ledger)
        => RequestCycleFamilyImprovementEpochAtLeast(
            ledger,
            ledger.EarnedImprovementEpoch + 1);

    private static bool TryRequestCycleFamilyImprovementForActionCount(
        CycleFamilyLedgerEntry ledger,
        int actionCount)
    {
        if (!ledger.AdmittedActionCounts.Contains(actionCount)
            || !(ledger.ImprovementEvidenceActionCounts ??= []).Add(actionCount))
        {
            return false;
        }
        RequestCycleFamilyImprovementEpoch(ledger);
        return true;
    }

    private static void RequestCycleFamilyImprovementEpochAtLeast(
        CycleFamilyLedgerEntry ledger,
        int requestedEpoch)
    {
        byte requested = (byte)Math.Clamp(
            requestedEpoch,
            0,
            MaximumCycleFamilyImprovementEpoch);
        if (requested > ledger.RequestedImprovementEpoch)
            ledger.RequestedImprovementEpoch = requested;
    }

    private static bool TryRequestCycleFamilyImprovementEpochAtLeast(
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers,
        CanonicalCycleFamilyKey family,
        int requestedEpoch)
    {
        if (requestedEpoch <= 0
            || !ledgers.TryGetValue(family, out CycleFamilyLedgerEntry? ledger))
        {
            return false;
        }
        RequestCycleFamilyImprovementEpochAtLeast(ledger, requestedEpoch);
        return true;
    }

    private void RequestRetainedCycleStartupImprovementEpoch(
        SearchNode candidate,
        int healthRiskBucket)
    {
        if (healthRiskBucket <= 0
            || candidate.CycleProbeLease is not { } lease)
        {
            return;
        }
        _ = TryRequestCycleFamilyImprovementEpochAtLeast(
            _run.CycleFamilyLedger,
            lease.Tracker.FamilyKey,
            healthRiskBucket);
    }

    private static void AdvanceCycleFamilyImprovementEpochAtLayerStart(
        CycleFamilyLedgerEntry ledger)
    {
        if (ledger.EarnedImprovementEpoch < ledger.RequestedImprovementEpoch)
            ledger.EarnedImprovementEpoch++;
        ledger.ActiveImprovementEpoch = ledger.EarnedImprovementEpoch;
    }

    internal static void VerifyCycleFamilyLayerBudgetPolicyForTesting()
    {
        VerifySparseProbeStoragePolicyForTesting();
        VerifyCycleExitQualityTrackerPolicyForTesting();
        VerifyCyclePlanningTurnBudgetPolicyForTesting();
        VerifyCycleCanonicalActionKeyPolicyForTesting();
        VerifyCycleFamilyImprovementBudgetsForTesting();
        VerifyProductiveCyclePeriodSelectionForTesting();

        VerifyCycleFamilyImprovementEpochPolicyForTesting();
        VerifyCycleStartupEpochRequestPolicyForTesting();

        Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> boundedLedgers = [];
        CanonicalCycleFamilyKey existingKey = new(
            3,
            1,
            new StateFingerprint(0x1234UL, 0x5678UL));
        if (!TryGetOrCreateCycleFamilyLedger(
                boundedLedgers,
                existingKey,
                allowCreate: true,
                out CycleFamilyLedgerEntry existingLedger))
        {
            throw new InvalidOperationException("循环 family 账本无法登记首个实际入选键。");
        }
        for (int index = 0; index < 1024; index++)
        {
            CanonicalCycleFamilyKey rejectedKey = new(
                3,
                1,
                new StateFingerprint((ulong)index + 1, (ulong)index + 2));
            if (rejectedKey == existingKey)
                continue;
            if (TryGetOrCreateCycleFamilyLedger(
                    boundedLedgers,
                    rejectedKey,
                    allowCreate: false,
                    out _))
            {
                throw new InvalidOperationException(
                    "循环 family 当前回合预算耗尽后仍为新键建立了持久账本。");
            }
        }
        if (boundedLedgers.Count != 1
            || !TryGetOrCreateCycleFamilyLedger(
                boundedLedgers,
                existingKey,
                allowCreate: false,
                out CycleFamilyLedgerEntry settledLedger)
            || !ReferenceEquals(existingLedger, settledLedger))
        {
            throw new InvalidOperationException(
                "循环 family 账本上界失效，或预算耗尽后无法结算已有键。");
        }
    }

    private static void VerifySparseProbeStoragePolicyForTesting()
    {
        if (typeof(CycleProbeLease).IsValueType
            || typeof(CrossTurnProbeState).IsValueType)
        {
            throw new InvalidOperationException(
                "稀疏循环探针状态必须由引用类型承载，避免每个搜索节点内嵌可空结构体。");
        }

        SearchNode unpublished = new(
            Action: null,
            ActionCount: 7,
            PotionCount: 1,
            PotionStrategicCost: 2,
            Turn: 3,
            Traits: SearchRouteTraits.Resource,
            FutureSoldHp: 4,
            Score: 5,
            StateKey: default,
            HasPredictionRisk: true,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: null!,
            CombatProgress: null!)
        {
            RetentionRank = 17,
            CrossTurnSemanticStateChanged = true,
        };
        CycleSearchState cycle = new(
            default,
            default,
            PeriodActions: 1,
            Repetitions: 2,
            LastDelta: default,
            HasConsistentDelta: true);
        SearchNode attached = AttachCycleState(unpublished, cycle);
        if (!ReferenceEquals(attached, unpublished)
            || !ReferenceEquals(attached.Cycle, cycle)
            || attached.ActionCount != 7
            || attached.RetentionRank != 17
            || !attached.CrossTurnSemanticStateChanged)
        {
            throw new InvalidOperationException(
                "循环证据应就地附着到未发布节点，且不得改写其他搜索字段。");
        }

        static SearchNode AppendTestAction(
            SearchNode parent,
            PlanAction action)
            => new(
                action,
                parent.ActionCount + 1,
                parent.PotionCount,
                parent.PotionStrategicCost,
                parent.Turn,
                parent.Traits,
                parent.FutureSoldHp,
                parent.Score,
                parent.StateKey,
                parent.HasPredictionRisk,
                parent.BoundaryReason,
                parent.IsTerminal,
                parent,
                parent.Snapshot,
                parent.CombatProgress);

        SearchNode firstPotion = AppendTestAction(
            unpublished,
            new PlanAction(PlanActionKind.UsePotion, unpublished.Turn));
        SearchNode secondPotion = AppendTestAction(
            firstPotion,
            new PlanAction(PlanActionKind.UsePotion, unpublished.Turn));
        SearchNode firstCard = AppendTestAction(
            secondPotion,
            new PlanAction(PlanActionKind.PlayCard, unpublished.Turn));
        SearchNode laterPotion = AppendTestAction(
            firstCard,
            new PlanAction(PlanActionKind.UsePotion, unpublished.Turn));
        if (unpublished.HasNonPotionAction
            || firstPotion.HasNonPotionAction
            || secondPotion.HasNonPotionAction
            || !firstCard.HasNonPotionAction
            || !laterPotion.HasNonPotionAction
            || unpublished.HasMaterializedActionsForTesting
            || firstPotion.HasMaterializedActionsForTesting
            || secondPotion.HasMaterializedActionsForTesting
            || firstCard.HasMaterializedActionsForTesting
            || laterPotion.HasMaterializedActionsForTesting)
        {
            throw new InvalidOperationException(
                "开场药水路线标量没有随首个非药水动作单调传播，或意外物化了完整动作链。");
        }
    }

    private static void VerifyCycleFamilyImprovementEpochPolicyForTesting()
    {
        CycleTransitionDelta positiveBlockDelta = default(CycleTransitionDelta) with
        {
            PlayerBlock = 1,
        };
        CycleExitQuality positiveBlockQuality = default(CycleExitQuality) with
        {
            PlayerBlockGain = 1,
        };
        if (!HasOnlyUnmodeledExactCycleStateChange(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta: default,
                quality: default)
            || HasOnlyUnmodeledExactCycleStateChange(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta: positiveBlockDelta,
                quality: positiveBlockQuality))
        {
            throw new InvalidOperationException(
                "循环 family 把已知格挡漂移误判成未建模 exact-state 改善。");
        }

        CycleTransitionDelta[] durableProgressDeltas =
        [
            default(CycleTransitionDelta) with { PersistentBuffValue = 1 },
            default(CycleTransitionDelta) with { FutureResourceValue = 1 },
            default(CycleTransitionDelta) with { DelayedDamageValue = 1 },
            default(CycleTransitionDelta) with
            {
                PlayerHp = -1,
                CumulativePlayerHpLost = 1,
                PersistentBuffValue = 1,
            },
            default(CycleTransitionDelta) with { PlayerHp = 1 },
        ];
        CycleExitQuality deckClutterReduction = default(CycleExitQuality) with
        {
            DeckClutterReduction = 1,
        };
        CycleExitQuality deckSizeReduction = default(CycleExitQuality) with
        {
            DeckSizeReduction = 1,
        };
        CycleExitQuality projectedOnly = default(CycleExitQuality) with
        {
            ProjectedPlayerHpGain = 1,
        };
        if (durableProgressDeltas.Any(delta => !HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta,
                quality: default))
            || !HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta: default,
                quality: deckClutterReduction)
            || !HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta: default,
                quality: deckSizeReduction)
            || HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                positiveBlockDelta,
                positiveBlockQuality)
            || HasCycleFamilyImprovementEvidence(
                hasExactStateChange: false,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                durableProgressDeltas[0],
                quality: default)
            || HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: false,
                hasNewEnemyDurabilityProgress: false,
                durableProgressDeltas[0],
                quality: default)
            || HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: true,
                durableProgressDeltas[0],
                quality: default)
            || HasCycleFamilyImprovementEvidence(
                hasExactStateChange: true,
                hasConsistentDelta: true,
                hasNewEnemyDurabilityProgress: false,
                delta: default,
                quality: projectedOnly))
        {
            throw new InvalidOperationException(
                "循环 family 没有奖励完整、稳定且有界的持久收益/治疗/烧牌周期，" +
                "或错误奖励了格挡、投影、非精确、不稳定及敌方耐久直降周期。");
        }

        CycleFamilyLedgerEntry unmodeledMutation = new();
        CycleFamilyLedgerEntry modeledDurableProgress = new();
        CycleFamilyLedgerEntry knownBlockMutation = new();
        for (int targetEpoch = 1;
             targetEpoch <= MaximumCycleFamilyImprovementEpoch;
             targetEpoch++)
        {
            if (HasOnlyUnmodeledExactCycleStateChange(
                    hasExactStateChange: true,
                    hasConsistentDelta: true,
                    hasNewEnemyDurabilityProgress: false,
                    delta: default,
                    quality: default))
            {
                RequestCycleFamilyImprovementEpoch(unmodeledMutation);
            }
            if (HasCycleFamilyImprovementEvidence(
                    hasExactStateChange: true,
                    hasConsistentDelta: true,
                    hasNewEnemyDurabilityProgress: false,
                    durableProgressDeltas[0],
                    quality: default))
            {
                // Repeated siblings in one play-depth must still request only the next epoch.
                for (int sibling = 0; sibling < 32; sibling++)
                    RequestCycleFamilyImprovementEpoch(modeledDurableProgress);
            }
            if (HasOnlyUnmodeledExactCycleStateChange(
                    hasExactStateChange: true,
                    hasConsistentDelta: true,
                    hasNewEnemyDurabilityProgress: false,
                    delta: positiveBlockDelta,
                    quality: positiveBlockQuality))
            {
                RequestCycleFamilyImprovementEpoch(knownBlockMutation);
            }
            AdvanceCycleFamilyImprovementEpochAtLayerStart(unmodeledMutation);
            AdvanceCycleFamilyImprovementEpochAtLayerStart(modeledDurableProgress);
            AdvanceCycleFamilyImprovementEpochAtLayerStart(knownBlockMutation);
            if (unmodeledMutation.ActiveImprovementEpoch != targetEpoch
                || modeledDurableProgress.ActiveImprovementEpoch != targetEpoch
                || knownBlockMutation.ActiveImprovementEpoch != 0)
            {
                throw new InvalidOperationException(
                    "未建模或持久收益周期没有逐层请求 epoch，或已知格挡错误扩容。");
            }
        }

        CycleFamilyLedgerEntry noProgressSiblingFirst = new();
        CycleFamilyLedgerEntry progressSiblingFirst = new();
        const int sharedActionCount = 17;
        noProgressSiblingFirst.AdmittedActionCounts.Add(sharedActionCount);
        progressSiblingFirst.AdmittedActionCounts.Add(sharedActionCount);
        // The first ledger models a no-progress sibling winning family admission before the
        // progress sibling arrives. The second models the reverse coordinator order.
        if (!TryRequestCycleFamilyImprovementForActionCount(
                noProgressSiblingFirst,
                sharedActionCount)
            || !TryRequestCycleFamilyImprovementForActionCount(
                progressSiblingFirst,
                sharedActionCount))
        {
            throw new InvalidOperationException(
                "循环 family 的 progress evidence 被同深度先入选的普通 sibling 吞掉。");
        }
        for (int sibling = 0; sibling < 32; sibling++)
        {
            if (TryRequestCycleFamilyImprovementForActionCount(
                    noProgressSiblingFirst,
                    sharedActionCount)
                || TryRequestCycleFamilyImprovementForActionCount(
                    progressSiblingFirst,
                    sharedActionCount))
            {
                throw new InvalidOperationException(
                    "同一 family/depth 的重复 progress sibling 请求了多个 epoch。");
            }
        }
        if (noProgressSiblingFirst.RequestedImprovementEpoch != 1
            || progressSiblingFirst.RequestedImprovementEpoch != 1)
        {
            throw new InvalidOperationException(
                "循环 family 的同深度 evidence 受 sibling 顺序影响。");
        }
        AdvanceCycleFamilyImprovementEpochAtLayerStart(noProgressSiblingFirst);
        if (TryRequestCycleFamilyImprovementForActionCount(
                noProgressSiblingFirst,
                sharedActionCount))
        {
            throw new InvalidOperationException(
                "已消费的 family/depth evidence 在下一层被重复续租。");
        }
        const int nextActionCount = sharedActionCount + 1;
        noProgressSiblingFirst.AdmittedActionCounts.Add(nextActionCount);
        if (!TryRequestCycleFamilyImprovementForActionCount(
                noProgressSiblingFirst,
                nextActionCount)
            || noProgressSiblingFirst.RequestedImprovementEpoch != 2)
        {
            throw new InvalidOperationException(
                "新深度的持久收益没有获得下一阶段的有界续租。");
        }

        CycleFamilyLedgerEntry forward = new();
        CycleFamilyLedgerEntry reverse = new();
        AdvanceCycleFamilyImprovementEpochAtLayerStart(forward);
        AdvanceCycleFamilyImprovementEpochAtLayerStart(reverse);

        // Coordinator commit order and the number of sibling observations cannot mint more than
        // one epoch inside a play-depth. All evidence becomes active only at the next boundary.
        for (int index = 0; index < 100; index++)
            RequestCycleFamilyImprovementEpoch(forward);
        for (int index = 99; index >= 0; index--)
            RequestCycleFamilyImprovementEpoch(reverse);
        if (forward.ActiveImprovementEpoch != 0
            || reverse.ActiveImprovementEpoch != 0
            || forward.RequestedImprovementEpoch != 1
            || reverse.RequestedImprovementEpoch != 1)
        {
            throw new InvalidOperationException(
                "循环 family 同层 evidence 提前生效、受提交顺序影响或一次请求了多级 epoch。");
        }

        AdvanceCycleFamilyImprovementEpochAtLayerStart(forward);
        AdvanceCycleFamilyImprovementEpochAtLayerStart(reverse);
        if (forward.EarnedImprovementEpoch != 1
            || forward.ActiveImprovementEpoch != 1
            || reverse.EarnedImprovementEpoch != 1
            || reverse.ActiveImprovementEpoch != 1)
        {
            throw new InvalidOperationException(
                "循环 family 的 staged epoch 没有在下一 play-depth 同步生效。");
        }

        for (int targetEpoch = 2;
             targetEpoch <= MaximumCycleFamilyImprovementEpoch;
             targetEpoch++)
        {
            for (int evidence = 0; evidence < targetEpoch * 7; evidence++)
            {
                RequestCycleFamilyImprovementEpoch(forward);
                RequestCycleFamilyImprovementEpoch(reverse);
            }
            AdvanceCycleFamilyImprovementEpochAtLayerStart(forward);
            AdvanceCycleFamilyImprovementEpochAtLayerStart(reverse);
            if (forward.ActiveImprovementEpoch != targetEpoch
                || reverse.ActiveImprovementEpoch != targetEpoch)
            {
                throw new InvalidOperationException(
                    "循环 family 的 staged epoch 没有保持每层最多推进一级。");
            }
        }

        for (int index = 0; index < 100; index++)
        {
            RequestCycleFamilyImprovementEpoch(forward);
            AdvanceCycleFamilyImprovementEpochAtLayerStart(forward);
        }
        if (forward.EarnedImprovementEpoch != MaximumCycleFamilyImprovementEpoch
            || forward.ActiveImprovementEpoch != MaximumCycleFamilyImprovementEpoch
            || forward.RequestedImprovementEpoch
                != MaximumCycleFamilyImprovementEpoch)
        {
            throw new InvalidOperationException("循环 family improvement epoch 突破了硬上限。");
        }

        CycleFamilyLedgerEntry createdInsideLayer = new();
        RequestCycleFamilyImprovementEpoch(createdInsideLayer);
        if (createdInsideLayer.ActiveImprovementEpoch != 0
            || createdInsideLayer.RequestedImprovementEpoch != 1)
        {
            throw new InvalidOperationException(
                "层内新建循环 family 意外立即获得了本层 epoch 预算。");
        }

        StateFingerprint crossTurnSequence = new(0xA1UL, 0xB2UL);
        Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> crossTurnLedgers =
            new()
            {
                [new CanonicalCycleFamilyKey(7, 1, crossTurnSequence)] = new(),
                [new CanonicalCycleFamilyKey(8, 1, crossTurnSequence)] = new(),
            };
        CycleFamilyLedgerEntry firstTurn = crossTurnLedgers[
            new CanonicalCycleFamilyKey(7, 1, crossTurnSequence)];
        CycleFamilyLedgerEntry secondTurn = crossTurnLedgers[
            new CanonicalCycleFamilyKey(8, 1, crossTurnSequence)];
        RequestCycleFamilyImprovementEpoch(firstTurn);
        foreach (CycleFamilyLedgerEntry ledger in crossTurnLedgers.Values)
            AdvanceCycleFamilyImprovementEpochAtLayerStart(ledger);
        if (firstTurn.ActiveImprovementEpoch != 1
            || secondTurn.ActiveImprovementEpoch != 0)
        {
            throw new InvalidOperationException(
                "循环 family 的 improvement epoch 在不同战斗回合之间发生了泄漏。");
        }
    }

    private static void VerifyProductiveCyclePeriodSelectionForTesting()
    {
        CycleSearchState quietPhase = new(default, default, 1, 2, default, true);
        CycleSearchState productivePeriod = new(default, default, 3, 2, default, true)
        {
            HasNewEnemyDurabilityProgress = true,
            HasConsistentDamagePhases = true,
        };
        CycleSearchState repeatedProductivePeriod = productivePeriod with { PeriodActions = 6 };
        CycleSearchState recoveredOldDurability = productivePeriod with
        {
            HasNewEnemyDurabilityProgress = false,
        };
        CycleSearchState growingDamagePeriod = productivePeriod with
        {
            HasConsistentDelta = false,
            LastDelta = default(CycleTransitionDelta) with { EnemyHp = -2 },
        };
        if (!HasRepeatableCycleEvidence(false, true, true)
            || HasRepeatableCycleEvidence(false, false, true)
            || HasRepeatableCycleEvidence(false, true, false)
            || !ReferenceEquals(
                SelectPreferredConsistentCycleEvidence(quietPhase, growingDamagePeriod),
                growingDamagePeriod)
            || growingDamagePeriod.HasConsistentDelta
            || HasMatchingCycleDamagePhases([true, false, false, true], period: 2)
            || !HasMatchingCycleDamagePhases([true, false, false, true, false, false], period: 3)
            || !HasMatchingCycleDamagePhases([true, true], period: 1)
            || HasMatchingCycleDamagePhases([true, false, false], period: 3)
            || !ReferenceEquals(
                SelectPreferredConsistentCycleEvidence(quietPhase, productivePeriod),
                productivePeriod)
            || !ReferenceEquals(
                SelectPreferredConsistentCycleEvidence(productivePeriod, quietPhase),
                productivePeriod)
            || !ReferenceEquals(
                SelectPreferredConsistentCycleEvidence(productivePeriod, repeatedProductivePeriod),
                productivePeriod)
            || !ReferenceEquals(
                SelectPreferredConsistentCycleEvidence(quietPhase, recoveredOldDurability),
                quietPhase))
        {
            throw new InvalidOperationException(
                "循环检测被短暂静默阶段截断，或把旧耐久恢复当成了新的周期进展。");
        }
    }

    private static void VerifyCycleFamilyImprovementBudgetsForTesting()
    {
        int[] expectedDepth = [8, 16, 32, 64, 128];
        int[] expectedProbeStarts = [4, 8, 16, 16, 16];
        int[] expectedProbeActions = [8, 16, 32, 32, 32];
        int[] expectedProbeNodes = [64, 128, 256, 256, 256];
        int[] expectedShortCycleRepetitions = [32, 64, 128, 256, 512];
        int[] expectedLongCycleRepetitions = [2, 4, 8, 16, 16];
        for (byte epoch = 0; epoch <= MaximumCycleFamilyImprovementEpoch; epoch++)
        {
            if (CycleFamilyDepthBudget(periodActions: 1, epoch) != expectedDepth[epoch]
                || CycleFamilyProbeStartBudget(periodActions: 1, epoch)
                    != expectedProbeStarts[epoch]
                || CycleExitProbeActionBudget(epoch) != expectedProbeActions[epoch]
                || CycleFamilyProbeExpansionBudget(epoch) != expectedProbeNodes[epoch]
                || CycleRepetitionBudget(periodActions: 1, epoch)
                    != expectedShortCycleRepetitions[epoch]
                || CycleRepetitionBudget(MaximumDetectedCyclePeriodActions, epoch)
                    != expectedLongCycleRepetitions[epoch])
            {
                throw new InvalidOperationException(
                    $"循环 family 第 {epoch} 级 staged 预算不符合有界几何序列。");
            }
        }

        if (CycleFamilyDepthBudget(
                MaximumDetectedCyclePeriodActions,
                MaximumCycleFamilyImprovementEpoch) != MaximumCycleFamilyDepthBudget
            || CycleFamilyProbeStartBudget(
                MaximumDetectedCyclePeriodActions,
                MaximumCycleFamilyImprovementEpoch)
                != MaximumCycleFamilyProbeStartBudget
            || CycleExitProbeActionBudget(MaximumCycleFamilyImprovementEpoch)
                != MaximumCycleExitProbeActions
            || CycleFamilyProbeExpansionBudget(MaximumCycleFamilyImprovementEpoch)
                != MaximumCycleFamilyProbeExpansionBudget)
        {
            throw new InvalidOperationException("循环 family staged 预算突破了声明的硬上限。");
        }
    }

    private static void VerifyCycleStartupEpochRequestPolicyForTesting()
    {
        CycleFamilyLedgerEntry bucketFour = new();
        RequestCycleFamilyImprovementEpochAtLeast(bucketFour, requestedEpoch: 4);
        if (bucketFour.ActiveImprovementEpoch != 0
            || bucketFour.RequestedImprovementEpoch != 4)
        {
            throw new InvalidOperationException(
                "循环 startup bucket 请求在 play-depth 边界前提前生效或没有保留目标 epoch。");
        }
        for (byte layer = 1; layer <= MaximumCycleFamilyImprovementEpoch; layer++)
        {
            AdvanceCycleFamilyImprovementEpochAtLayerStart(bucketFour);
            if (bucketFour.EarnedImprovementEpoch != layer
                || bucketFour.ActiveImprovementEpoch != layer
                || bucketFour.RequestedImprovementEpoch != 4)
            {
                throw new InvalidOperationException(
                    "一次 bucket4 请求没有严格经过四个 play-depth 才推进到 epoch4。");
            }
        }

        CycleFamilyLedgerEntry forward = new();
        CycleFamilyLedgerEntry reverse = new();
        int[] forwardRequests = [1, 4, 2, 4, 3];
        int[] reverseRequests = [3, 4, 2, 4, 1];
        foreach (int requestedEpoch in forwardRequests)
            RequestCycleFamilyImprovementEpochAtLeast(forward, requestedEpoch);
        foreach (int requestedEpoch in reverseRequests)
            RequestCycleFamilyImprovementEpochAtLeast(reverse, requestedEpoch);
        if (forward.RequestedImprovementEpoch != 4
            || reverse.RequestedImprovementEpoch != 4
            || forward.EarnedImprovementEpoch != 0
            || reverse.EarnedImprovementEpoch != 0)
        {
            throw new InvalidOperationException(
                "循环 startup bucket 请求受提交顺序/重复次数影响，或在层内提前赚取 epoch。");
        }

        CanonicalCycleFamilyKey admittedFamily = new(
            11,
            1,
            new StateFingerprint(0xC3UL, 0xD4UL));
        CanonicalCycleFamilyKey missingFamily = new(
            11,
            1,
            new StateFingerprint(0xE5UL, 0xF6UL));
        CycleFamilyLedgerEntry admittedLedger = new();
        Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> admittedLedgers = new()
        {
            [admittedFamily] = admittedLedger,
        };
        if (TryRequestCycleFamilyImprovementEpochAtLeast(
                admittedLedgers,
                admittedFamily,
                requestedEpoch: 0)
            || TryRequestCycleFamilyImprovementEpochAtLeast(
                admittedLedgers,
                missingFamily,
                requestedEpoch: 4)
            || admittedLedgers.Count != 1
            || admittedLedger.RequestedImprovementEpoch != 0)
        {
            throw new InvalidOperationException(
                "bucket0 或缺失/未 admission 的循环 family 错误获得了 startup epoch。");
        }
    }

    private static void VerifyCycleExitQualityTrackerPolicyForTesting()
    {
        StateFingerprint shape = new(0x101UL, 0x202UL);
        StateFingerprint sequence = new(0x303UL, 0x404UL);
        StateFingerprint cycleAction = new(0x505UL, 0x606UL);
        StateFingerprint firstExitAction = new(0x707UL, 0x808UL);
        CycleProbeTracker tracker = new(
            shape,
            sequence,
            [cycleAction],
            new CanonicalCycleFamilyKey(2, 1, sequence));

        CycleExitQuality baseline = default;
        long firstGeneration = tracker.ObserveExit(
            0,
            firstExitAction,
            baseline,
            out bool firstActionImproved);
        if (firstGeneration != 1
            || !firstActionImproved
            || tracker.ExitQualityEpoch != 1
            || !tracker.HasPendingExitProbe(0, firstExitAction, firstGeneration))
        {
            throw new InvalidOperationException(
                "循环出口首次出现的新动作没有发布质量 epoch 与待探测 ticket。");
        }

        CycleExitQuality dominated = baseline with { StrategicHpCost = 1 };
        long dominatedGeneration = tracker.ObserveExit(
            0,
            firstExitAction,
            dominated,
            out bool dominatedImproved);
        if (dominatedImproved
            || dominatedGeneration != firstGeneration
            || tracker.ExitQualityEpoch != 1
            || tracker.ActiveExitProbeTicketCountForTesting != 1)
        {
            throw new InvalidOperationException(
                "被支配的循环出口错误推进了质量 epoch 或重复签发 ticket。");
        }

        CycleExitQuality paretoImprovement = baseline with
        {
            EnemyDurabilityProgress = 1,
        };
        long improvedGeneration = tracker.ObserveExit(
            0,
            firstExitAction,
            paretoImprovement,
            out bool paretoImproved);
        if (!paretoImproved
            || improvedGeneration <= firstGeneration
            || tracker.ExitQualityEpoch != 2
            || tracker.HasPendingExitProbe(0, firstExitAction, firstGeneration)
            || !tracker.HasPendingExitProbe(0, firstExitAction, improvedGeneration)
            || tracker.ActiveExitProbeTicketCountForTesting != 1)
        {
            throw new InvalidOperationException(
                "真正的 Pareto 改善没有推进循环出口 epoch 并替换旧 ticket。");
        }

        CycleProbeTracker clone = tracker.Clone();
        if (clone.ExitQualityEpoch != tracker.ExitQualityEpoch
            || clone.ActiveExitProbeTicketCountForTesting
                != tracker.ActiveExitProbeTicketCountForTesting
            || !clone.HasPendingExitProbe(0, firstExitAction, improvedGeneration))
        {
            throw new InvalidOperationException(
                "循环出口 tracker 克隆没有保留质量 epoch、frontier ticket 状态。");
        }
        if (!clone.TryMarkExitProbeIssued(0, firstExitAction, improvedGeneration)
            || clone.HasPendingExitProbe(0, firstExitAction, improvedGeneration)
            || !tracker.HasPendingExitProbe(0, firstExitAction, improvedGeneration))
        {
            throw new InvalidOperationException(
                "循环出口 tracker 克隆与原 tracker 共享了可变 ticket 状态。");
        }

        VerifyCommittedCycleExitImprovementEpochPolicyForTesting();
        VerifyCycleExitAdmissionMaterializationPolicyForTesting();
    }

    private static void VerifyCommittedCycleExitImprovementEpochPolicyForTesting()
    {
        const int firstActionCount = 17;
        CanonicalCycleFamilyKey family = new(
            5,
            1,
            new StateFingerprint(0x901UL, 0x902UL));
        StateFingerprint cycleAction = new(0x903UL, 0x904UL);
        CycleProbeTracker tracker = new(
            new StateFingerprint(0x905UL, 0x906UL),
            family.SequenceKey,
            [cycleAction],
            family);
        CycleFamilyLedgerEntry ledger = new();
        ledger.AdmittedActionCounts.Add(firstActionCount);
        Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers = new()
        {
            [family] = ledger,
        };

        static SearchNode Node(
            int actionCount,
            PlanAction? action = null,
            SearchNode? parent = null)
            => new(
                Action: action,
                ActionCount: actionCount,
                PotionCount: 0,
                PotionStrategicCost: 0,
                Turn: 5,
                Traits: SearchRouteTraits.None,
                FutureSoldHp: 0,
                Score: 0,
                StateKey: default,
                HasPredictionRisk: false,
                BoundaryReason: SearchBoundaryReason.None,
                IsTerminal: false,
                Parent: parent,
                Snapshot: null!,
                CombatProgress: null!);

        SearchNode origin = Node(firstActionCount);
        origin.CycleProbeLease = new CycleProbeLease(
            tracker,
            NextActionIndex: 0,
            CompletedRepetitions: 1,
            ImprovedSinceWrap: false,
            LastCompletedRepetitionImproved: false,
            ObservedExitQualityEpoch: 0);

        void CommitPending(PlanAction action, CycleExitQuality quality)
        {
            SearchNode child = Node(firstActionCount + 1, action, origin);
            child.PendingCycleExitObservation = new PendingCycleExitObservation(
                tracker,
                origin,
                OriginPhaseIndex: 0,
                BuildCycleActionKey(action),
                quality);
            if (!TryMaterializePendingCycleExitObservation(child, ledgers)
                || child.PendingCycleExitObservation != null
                || child.CycleExitProbe == null)
            {
                throw new InvalidOperationException(
                    "循环出口真实提交路径没有结算 pending observation。");
            }
        }

        PlanAction sharedExitAction = new(
            PlanActionKind.PlayCard,
            Turn: 5,
            CardId: "TEST.EXIT.SHARED");
        CommitPending(sharedExitAction, default);
        CommitPending(
            sharedExitAction,
            default(CycleExitQuality) with { PlayerBlockGain = 1 });
        CommitPending(
            sharedExitAction,
            default(CycleExitQuality) with { ProjectedPlayerHpGain = 1 });
        if (tracker.ExitQualityEpoch != 3
            || ledger.RequestedImprovementEpoch != 0
            || ledger.ImprovementEvidenceActionCounts != null)
        {
            throw new InvalidOperationException(
                "真实提交没有观察到测试出口，或首次全零、仅格挡、仅投影、仅评分的出口错误续租了 family epoch。");
        }

        CycleExitQuality durable = default(CycleExitQuality) with
        {
            PersistentBuffGain = 1,
        };
        CommitPending(sharedExitAction, durable);
        if (ledger.RequestedImprovementEpoch != 1
            || ledger.ImprovementEvidenceActionCounts is not { Count: 1 } evidence
            || !evidence.Contains(firstActionCount))
        {
            throw new InvalidOperationException(
                "真实出口提交路径没有用已 admission 的 family/depth 证据请求首个 epoch。");
        }

        // A different exit action at the same family/depth is still the same renewal evidence.
        CommitPending(
            new PlanAction(
                PlanActionKind.PlayCard,
                Turn: 5,
                CardId: "TEST.EXIT.DISTINCT"),
            durable with { PersistentBuffGain = 2 });
        if (ledger.RequestedImprovementEpoch != 1
            || ledger.ImprovementEvidenceActionCounts.Count != 1)
        {
            throw new InvalidOperationException(
                "同一 family/depth 的多个出口动作重复请求了 improvement epoch。");
        }

        AdvanceCycleFamilyImprovementEpochAtLayerStart(ledger);
        const int nextActionCount = firstActionCount + 1;
        ledger.AdmittedActionCounts.Add(nextActionCount);
        SearchNode propagated = Node(nextActionCount + 1);
        propagated.CycleExitObservation = new CycleExitObservation(
            tracker,
            nextActionCount,
            OriginPhaseIndex: 0,
            new StateFingerprint(0x90BUL, 0x90CUL),
            OriginGeneration: 1,
            durable with { PersistentBuffGain = 3 },
            CompletesProbe: false);
        CommitCycleExitObservationCore(propagated, ledgers);
        if (propagated.CycleExitObservation != null
            || ledger.RequestedImprovementEpoch != 2
            || ledger.ImprovementEvidenceActionCounts.Count != 2
            || !ledger.ImprovementEvidenceActionCounts.IsSubsetOf(
                ledger.AdmittedActionCounts))
        {
            throw new InvalidOperationException(
                "下一已 admission 深度的 propagated 出口没有逐层续租，或证据账本突破 admission 硬界。");
        }
    }

    private static void VerifyCycleExitAdmissionMaterializationPolicyForTesting()
    {
        SimulationSnapshot snapshot = (SimulationSnapshot)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(SimulationSnapshot));

        (CycleProbeTracker Tracker,
            Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> Ledgers,
            SearchNode[] Candidates) BuildCandidates(IReadOnlyList<PlanAction> actions)
        {
            CanonicalCycleFamilyKey family = new(
                7,
                1,
                new StateFingerprint(0xA101UL, 0xA102UL));
            CycleProbeTracker tracker = new(
                new StateFingerprint(0xA103UL, 0xA104UL),
                family.SequenceKey,
                [new StateFingerprint(0xA105UL, 0xA106UL)],
                family);
            CycleFamilyLedgerEntry ledger = new();
            const int originActionCount = 9;
            ledger.AdmittedActionCounts.Add(originActionCount);
            Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers = new()
            {
                [family] = ledger,
            };
            SearchNode origin = new(
                Action: null,
                ActionCount: originActionCount,
                PotionCount: 0,
                PotionStrategicCost: 0,
                Turn: 7,
                Traits: SearchRouteTraits.None,
                FutureSoldHp: 0,
                Score: 0,
                StateKey: new StateFingerprint(0xA107UL, 0xA108UL),
                HasPredictionRisk: false,
                BoundaryReason: SearchBoundaryReason.None,
                IsTerminal: false,
                Parent: null,
                Snapshot: snapshot,
                CombatProgress: null!);
            origin.CycleProbeLease = new CycleProbeLease(
                tracker,
                NextActionIndex: 0,
                CompletedRepetitions: 1,
                ImprovedSinceWrap: false,
                LastCompletedRepetitionImproved: false,
                ObservedExitQualityEpoch: 0);

            SearchNode[] candidates = new SearchNode[actions.Count];
            for (int index = 0; index < actions.Count; index++)
            {
                PlanAction action = actions[index];
                SearchNode candidate = new(
                    action,
                    originActionCount + 1,
                    PotionCount: action.Kind == PlanActionKind.UsePotion ? 1 : 0,
                    PotionStrategicCost: 0,
                    Turn: 7,
                    Traits: SearchRouteTraits.None,
                    FutureSoldHp: 0,
                    Score: index + 1,
                    StateKey: new StateFingerprint(
                        (ulong)(0xA200 + index),
                        (ulong)(0xA300 + index)),
                    HasPredictionRisk: false,
                    BoundaryReason: SearchBoundaryReason.None,
                    IsTerminal: false,
                    Parent: origin,
                    Snapshot: snapshot,
                    CombatProgress: null!);
                candidate.PendingCycleExitObservation = new PendingCycleExitObservation(
                    tracker,
                    origin,
                    OriginPhaseIndex: 0,
                    BuildCycleActionKey(action),
                    default(CycleExitQuality) with { FutureResourceGain = index });
                candidates[index] = candidate;
            }
            return (tracker, ledgers, candidates);
        }

        PlanAction[] mixedActions =
        [
            new(PlanActionKind.PlayCard, 7, CardId: "TEST.EXIT.CARD"),
            new(PlanActionKind.UsePotion, 7, PotionId: "TEST.EXIT.POTION"),
            new(PlanActionKind.EndTurn, 7, EndsPlayerTurn: true),
        ];
        var forward = BuildCandidates(mixedActions);
        if (forward.Tracker.ExitEnvelopeActionCountForTesting != 0
            || forward.Tracker.ExitQualityEpoch != 0
            || !HasCycleAdmissionTranspositionLease(forward.Candidates[0])
            || HasCycleExpansionTranspositionLease(forward.Candidates[0]))
        {
            throw new InvalidOperationException(
                "临时循环出口在 admission 前写入 tracker，或错误获得 expansion 租约。");
        }
        SearchNode? forwardWinner = MaterializeAdmittedCycleExitObservation(
            forward.Candidates,
            forward.Ledgers);

        var reverse = BuildCandidates(mixedActions);
        Array.Reverse(reverse.Candidates);
        SearchNode? reverseWinner = MaterializeAdmittedCycleExitObservation(
            reverse.Candidates,
            reverse.Ledgers);
        if (forwardWinner?.Action?.Kind != PlanActionKind.EndTurn
            || reverseWinner?.Action?.Kind != PlanActionKind.EndTurn
            || forwardWinner.CycleExitProbe?.ExitActionKey
                != reverseWinner.CycleExitProbe?.ExitActionKey
            || forward.Tracker.ExitEnvelopeActionCountForTesting != 1
            || reverse.Tracker.ExitEnvelopeActionCountForTesting != 1
            || forward.Tracker.ExitQualityEpoch != 1
            || reverse.Tracker.ExitQualityEpoch != 1
            || forward.Candidates.Count(candidate => candidate.CycleExitProbe != null) != 1
            || reverse.Candidates.Count(candidate => candidate.CycleExitProbe != null) != 1
            || forward.Candidates.Any(candidate => candidate.PendingCycleExitObservation != null)
            || reverse.Candidates.Any(candidate => candidate.PendingCycleExitObservation != null)
            || HasCycleExpansionTranspositionLease(forwardWinner))
        {
            throw new InvalidOperationException(
                "卡牌/药水/结束回合的统一循环出口 admission 受枚举顺序影响、写入多个 action envelope，或让 pending 跨越 frontier。");
        }

        PlanAction[] wideActions = Enumerable.Range(0, 64)
            .Select(index => new PlanAction(
                PlanActionKind.PlayCard,
                7,
                CardId: $"TEST.EXIT.{index}",
                CardOccurrence: index))
            .ToArray();
        var wide = BuildCandidates(wideActions);
        SearchNode? wideWinner = MaterializeAdmittedCycleExitObservation(
            wide.Candidates,
            wide.Ledgers);
        if (wideWinner?.Action?.CardOccurrence != 63
            || wide.Tracker.ExitEnvelopeActionCountForTesting != 1
            || wide.Candidates.Count(candidate => candidate.CycleExitProbe != null) != 1
            || wide.Candidates.Any(candidate => candidate.PendingCycleExitObservation != null))
        {
            throw new InvalidOperationException(
                "宽循环出口波次没有在完整候选集 admission 后仅 materialize 一个确定性 action envelope。");
        }

        var stale = BuildCandidates(
        [
            new(PlanActionKind.PlayCard, 7, CardId: "TEST.EXIT.STALE"),
            new(PlanActionKind.PlayCard, 7, CardId: "TEST.EXIT.FALLBACK"),
        ]);
        SearchNode staleFirst = stale.Candidates[1];
        SearchNode fallback = stale.Candidates[0];
        PendingCycleExitObservation staleObservation = staleFirst.PendingCycleExitObservation!;
        long staleGeneration = stale.Tracker.ObserveExit(
            staleObservation.OriginPhaseIndex,
            staleObservation.ExitActionKey,
            staleObservation.Quality,
            out _);
        if (!stale.Tracker.TryMarkExitProbeIssued(
                staleObservation.OriginPhaseIndex,
                staleObservation.ExitActionKey,
                staleGeneration))
        {
            throw new InvalidOperationException("循环出口 stale admission 测试无法预签发票据。");
        }
        SearchNode? fallbackWinner = MaterializeAdmittedCycleExitObservation(
            stale.Candidates,
            stale.Ledgers);
        if (!ReferenceEquals(fallbackWinner, fallback)
            || staleFirst.CycleExitProbe != null
            || fallback.CycleExitProbe == null
            || stale.Tracker.ExitEnvelopeActionCountForTesting != 2
            || stale.Candidates.Any(candidate => candidate.PendingCycleExitObservation != null))
        {
            throw new InvalidOperationException(
                "循环出口 admission 没有跳过 stale 首选项并确定性尝试下一候选。");
        }
    }

    private static void VerifyCyclePlanningTurnBudgetPolicyForTesting()
    {
        if (CyclePlanningPerTurnBudget(maxExpandedNodes: 0) != 64
            || CyclePlanningPerTurnBudget(int.MaxValue) != 256)
        {
            throw new InvalidOperationException(
                "循环 family/probe 的单回合预算没有保持 64..256 硬界限。");
        }

        const int firstTurn = 3;
        const int secondTurn = 4;
        const int turnBudget = 4;
        Dictionary<int, int> depthConsumption = [];
        Dictionary<int, int> probeConsumption = [];

        for (int index = 0; index < turnBudget; index++)
        {
            ChargeCyclePlanningBudgetForTurn(
                depthConsumption,
                firstTurn,
                turnBudget);
            ChargeCyclePlanningBudgetForTurn(
                probeConsumption,
                firstTurn,
                turnBudget);
        }
        if (HasCyclePlanningBudgetForTurn(depthConsumption, firstTurn, turnBudget)
            || HasCyclePlanningBudgetForTurn(probeConsumption, firstTurn, turnBudget))
        {
            throw new InvalidOperationException("循环规划第一回合硬上限失效。");
        }
        if (!HasCyclePlanningBudgetForTurn(depthConsumption, secondTurn, turnBudget)
            || !HasCyclePlanningBudgetForTurn(probeConsumption, secondTurn, turnBudget))
        {
            throw new InvalidOperationException(
                "循环规划第一回合耗尽后错误挤占了第二回合预算。");
        }
        ChargeCyclePlanningBudgetForTurn(depthConsumption, secondTurn, turnBudget);
        ChargeCyclePlanningBudgetForTurn(probeConsumption, secondTurn, turnBudget);
        if (CyclePlanningBudgetConsumedForTurn(depthConsumption, secondTurn) != 1
            || CyclePlanningBudgetConsumedForTurn(probeConsumption, secondTurn) != 1)
        {
            throw new InvalidOperationException(
                "循环 family 深度或出口探测没有按回合独立扣费。");
        }

        const int searchedTurns = 3;
        Dictionary<int, int> ledgerConsumption = [];
        Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers = [];
        for (int turnOffset = 0; turnOffset < searchedTurns; turnOffset++)
        {
            int turn = firstTurn + turnOffset;
            for (int index = 0; index < turnBudget; index++)
            {
                CanonicalCycleFamilyKey family = new(
                    turn,
                    1,
                    new StateFingerprint(
                        unchecked((ulong)(turn * 100 + index + 1)),
                        unchecked((ulong)(turn * 100 + index + 1001))));
                if (!TryGetOrCreateCycleFamilyLedger(
                        ledgers,
                        family,
                        HasCyclePlanningBudgetForTurn(
                            ledgerConsumption,
                            turn,
                            turnBudget),
                        out _))
                {
                    throw new InvalidOperationException(
                        "循环 family 在本回合预算内无法建立账本。");
                }
                ChargeCyclePlanningBudgetForTurn(
                    ledgerConsumption,
                    turn,
                    turnBudget);
            }
            CanonicalCycleFamilyKey overflow = new(
                turn,
                1,
                new StateFingerprint(
                    unchecked((ulong)(turn * 10000 + 7)),
                    unchecked((ulong)(turn * 10000 + 11))));
            if (TryGetOrCreateCycleFamilyLedger(
                    ledgers,
                    overflow,
                    HasCyclePlanningBudgetForTurn(
                        ledgerConsumption,
                        turn,
                        turnBudget),
                    out _))
            {
                throw new InvalidOperationException(
                    "循环 family 超过单回合硬上限后仍建立了账本。");
            }
        }
        if (ledgers.Count > searchedTurns * turnBudget
            || ledgerConsumption.Values.Any(consumed => consumed > turnBudget)
            || ledgerConsumption.Values.Sum() > searchedTurns * turnBudget)
        {
            throw new InvalidOperationException(
                "循环 family 总账本不再受搜索回合数与单回合预算乘积约束。");
        }
    }

    private static void VerifyCycleCanonicalActionKeyPolicyForTesting()
    {
        PlanCardToken token = new(
            "cycle-option",
            2,
            "mutable-state-a",
            SourceOccurrence: 1,
            OptionOccurrence: 2,
            Title: "");
        PlanCardChoice choice = new(
            PlanChoiceEffect.Modify,
            MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
            [token],
            SourceId: "cycle-source",
            ContextId: "context-a");
        PlanAction baseline = new(
            PlanActionKind.PlayCard,
            Turn: 2,
            CardId: "cycle-card",
            CardOccurrence: 1,
            Choice: choice,
            ReplayCount: 1,
            CardStateKey: "mutable-card-state-a",
            CardStateOccurrence: 2);
        StateFingerprint baselineKey = BuildCycleFamilyActionKey(baseline);

        PlanAction mutableStateChanged = baseline with
        {
            ReplayCount = 99,
            CardStateKey = "mutable-card-state-b",
            Choice = choice with
            {
                Cards = [token with { StateKey = "mutable-state-b" }],
            },
        };
        if (BuildCycleFamilyActionKey(mutableStateChanged) != baselineKey)
        {
            throw new InvalidOperationException(
                "循环 family canonical key 错误包含了可变 CardStateKey 或 ReplayCount。");
        }
        if (BuildCycleDeterministicActionFingerprint(mutableStateChanged)
            == BuildCycleDeterministicActionFingerprint(baseline))
        {
            throw new InvalidOperationException(
                "循环候选最终稳定排序没有覆盖完整的可执行动作状态。");
        }

        PlanAction[] stableSemanticVariants =
        [
            baseline with { CardOccurrence = baseline.CardOccurrence + 1 },
            baseline with { Choice = choice with { ContextId = "context-b" } },
            baseline with
            {
                Choice = choice with
                {
                    Cards = [token with
                    {
                        SourceOccurrence = token.SourceOccurrence + 1,
                    }],
                },
            },
            baseline with
            {
                Choice = choice with
                {
                    Cards = [token with
                    {
                        OptionOccurrence = token.OptionOccurrence + 1,
                    }],
                },
            },
        ];
        if (stableSemanticVariants.Any(variant =>
                BuildCycleFamilyActionKey(variant) == baselineKey))
        {
            throw new InvalidOperationException(
                "循环 family canonical key 丢失了稳定的动作/选择 occurrence 或 context 语义。");
        }
    }

    private static bool HasCycleExitFamilyImprovementEvidence(CycleExitQuality quality)
        => quality.EnemyDurabilityProgress > 0
            || quality.OffensiveProgressGain > 0
            || quality.DelayedDamageGain > 0
            || quality.PersistentBuffGain > 0
            || quality.StrategicRetentionGain > 0
            || quality.FutureResourceGain > 0
            || quality.LongTermResourceGain > 0
            || quality.ReplayPotentialGain > 0
            || quality.RetainedAttackGain > 0
            || quality.PlayerHpGain > 0
            || quality.EnergyGain > 0
            || quality.StarsGain > 0
            || quality.EnemyStrengthSuppressionGain > 0
            || quality.EnemyWeakTurnsGain > 0
            || quality.EnemyVulnerableTurnsGain > 0
            || quality.OutstandingStolenResourceRecovery > 0
            || quality.SandpitProgress > 0
            || quality.OstyHpGain > 0
            || quality.OstyMaxHpGain > 0
            || quality.DeckClutterReduction > 0
            || quality.DeckSizeReduction > 0;

    private static bool TryMarkCycleFamilyImproved(
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers,
        CycleProbeTracker tracker,
        int originActionCount,
        CycleExitQuality quality)
    {
        // Ticket/frontier changes decide whether an exit deserves bounded lookahead. They are
        // deliberately broader than family-budget evidence: a newly discovered zero-quality,
        // block-only or projected-only exit must not make the enclosing loop progressively wider.
        if (!HasCycleExitFamilyImprovementEvidence(quality))
            return false;

        // A tracker is issued only after its family earned an admission. Do not materialize
        // metadata for an unadmitted/missing family merely because a finishing observation
        // arrived after that combat turn's capacity was exhausted.
        if (ledgers.TryGetValue(
                tracker.FamilyKey,
                out CycleFamilyLedgerEntry? ledger))
        {
            // Direct cycle progress and all exit actions at one family/depth share one evidence
            // ticket. This both removes sibling-order effects and keeps evidence storage a subset
            // of the already hard-bounded admitted depth set.
            return TryRequestCycleFamilyImprovementForActionCount(
                ledger,
                originActionCount);
        }
        return false;
    }

    private static byte ActiveCycleFamilyImprovementEpoch(
        IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledgers,
        CycleProbeTracker tracker)
        => ledgers.TryGetValue(
            tracker.FamilyKey,
            out CycleFamilyLedgerEntry? ledger)
                ? ledger.ActiveImprovementEpoch
                : (byte)0;

    private static bool HasOnlyUnmodeledExactCycleStateChange(
        bool hasExactStateChange,
        bool hasConsistentDelta,
        bool hasNewEnemyDurabilityProgress,
        CycleTransitionDelta delta,
        CycleExitQuality quality)
        => hasExactStateChange
            && hasConsistentDelta
            && !hasNewEnemyDurabilityProgress
            && delta == default
            && quality == default;

    private static bool HasCycleFamilyImprovementEvidence(
        bool hasExactStateChange,
        bool hasConsistentDelta,
        bool hasNewEnemyDurabilityProgress,
        CycleTransitionDelta delta,
        CycleExitQuality quality)
        => HasOnlyUnmodeledExactCycleStateChange(
                hasExactStateChange,
                hasConsistentDelta,
                hasNewEnemyDurabilityProgress,
                delta,
                quality)
            || hasExactStateChange
                && hasConsistentDelta
                && !hasNewEnemyDurabilityProgress
                && (HasDurableCycleProgress(delta)
                    // Healing and deck removal are monotonic toward natural bounds. They are
                    // safe delayed-payoff evidence; projected HP, raw score and block are not.
                    || delta.PlayerHp > 0
                    || quality.DeckClutterReduction > 0
                    || quality.DeckSizeReduction > 0);

    private static bool HasAdmittedCompletedCycleImprovement(
        SearchNode candidate,
        CanonicalCycleFamilyKey admittedFamily)
    {
        if (candidate.Cycle is not { } cycle
            || candidate.CycleProbeLease is not
                {
                    NextActionIndex: 0,
                    CompletedRepetitions: > 0,
                } lease
            || cycle.PriorCycleEndpoint is not { } priorEndpoint)
        {
            return false;
        }
        return cycle.ShapeKey == candidate.Snapshot.CycleShapeKey
            && lease.Tracker.ShapeKey == cycle.ShapeKey
            && lease.Tracker.SequenceKey == cycle.SequenceKey
            && lease.Tracker.PeriodActions == cycle.PeriodActions
            && lease.Tracker.FamilyKey == admittedFamily
            && HasCycleFamilyImprovementEvidence(
                cycle.HasExactStateChange,
                cycle.HasConsistentDelta,
                cycle.HasNewEnemyDurabilityProgress,
                cycle.LastDelta,
                MeasureCycleExitQuality(priorEndpoint, candidate));
    }

    private bool TryAdmitCycleFamilyDepth(SearchNode candidate)
    {
        CycleSearchState cycle = candidate.Cycle
            ?? throw new InvalidOperationException("循环族预算候选缺少循环证据。");
        CanonicalCycleFamilyKey family = BuildCanonicalCycleFamilyKey(candidate, cycle);
        int turnBudget = CyclePlanningPerTurnBudget();
        bool turnBudgetAvailable = HasCyclePlanningBudgetForTurn(
            _run.CycleFamilyDepthsConsumedByTurn,
            family.Turn,
            turnBudget);
        if (!TryGetOrCreateCycleFamilyLedger(
                _run.CycleFamilyLedger,
                family,
                allowCreate: turnBudgetAvailable,
                out CycleFamilyLedgerEntry ledger))
        {
            return false;
        }
        bool alreadyAdmitted = ledger.AdmittedActionCounts.Contains(candidate.ActionCount);
        if (!alreadyAdmitted
            && (ledger.AdmittedActionCounts.Count >= CycleFamilyDepthBudget(
                    family.PrimitivePeriodActions,
                    ledger.ActiveImprovementEpoch)
                || !turnBudgetAvailable))
        {
            return false;
        }

        if (!alreadyAdmitted)
        {
            ledger.AdmittedActionCounts.Add(candidate.ActionCount);
            ChargeCyclePlanningBudgetForTurn(
                _run.CycleFamilyDepthsConsumedByTurn,
                family.Turn,
                turnBudget);
        }
        // Family identity deliberately omits mutable state. Admission and evidence therefore use
        // separate per-depth tickets: a no-progress sibling cannot consume the sole opportunity
        // for a later progress sibling, while sibling order and duplicate observations cannot mint
        // extra epochs. Epoch, family and per-turn caps remain the hard bounds.
        if (HasAdmittedCompletedCycleImprovement(candidate, family))
        {
            _ = TryRequestCycleFamilyImprovementForActionCount(
                ledger,
                candidate.ActionCount);
        }
        return true;
    }

    private bool TryConsumeCycleExitProbeExpansionBudget(SearchNode candidate)
    {
        if (candidate.CycleExitProbe is not { RemainingActions: > 0 } probe)
            return true;

        int turnExpansionBudget = CyclePlanningPerTurnBudget();
        int budgetTurn = probe.OriginTracker.FamilyKey.Turn;
        if (!TryGetOrCreateCycleFamilyLedger(
                _run.CycleFamilyLedger,
                probe.OriginTracker.FamilyKey,
                // A tracker can only be issued by a family that already earned a depth
                // admission. Never let a stale probe create uncharged persistent metadata.
                allowCreate: false,
                out CycleFamilyLedgerEntry ledger))
        {
            // The probe was already issued, so settle its tracker even when no new persistent
            // family metadata may be created. This prevents a rejected ticket from remaining
            // active while keeping the ledger cardinality bounded by consumed work.
            probe.OriginTracker.CompleteExitProbe(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
            return false;
        }
        int familyStartBudget = CycleFamilyProbeStartBudget(
            probe.OriginTracker.FamilyKey.PrimitivePeriodActions,
            ledger.ActiveImprovementEpoch);
        int familyExpansionBudget = CycleFamilyProbeExpansionBudget(
            ledger.ActiveImprovementEpoch);
        CycleExitProbeTicketKey ticket = BuildCycleExitProbeTicketKey(candidate);
        bool firstTicketExpansion = !_run.ExpandedCycleProbeTickets.Contains(ticket);
        if (firstTicketExpansion && ledger.ProbeStarts >= familyStartBudget)
        {
            probe.OriginTracker.CompleteExitProbe(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
            return false;
        }
        if (ledger.ProbeExpandedNodes >= familyExpansionBudget
            || !HasCyclePlanningBudgetForTurn(
                _run.CycleProbeExpandedNodesConsumedByTurn,
                budgetTurn,
                turnExpansionBudget))
        {
            probe.OriginTracker.CompleteExitProbe(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
            return false;
        }

        if (firstTicketExpansion)
        {
            _run.ExpandedCycleProbeTickets.Add(ticket);
            ledger.ProbeStarts++;
        }
        ledger.ProbeExpandedNodes++;
        ChargeCyclePlanningBudgetForTurn(
            _run.CycleProbeExpandedNodesConsumedByTurn,
            budgetTurn,
            turnExpansionBudget);
        return true;
    }

    private int CyclePlanningPerTurnBudget()
        => CyclePlanningPerTurnBudget(_profile.MaxExpandedNodes);

    private static int CyclePlanningPerTurnBudget(int maxExpandedNodes)
        => Math.Clamp(maxExpandedNodes / 128, 64, 256);

    private static CanonicalCycleFamilyKey BuildCanonicalCycleFamilyKey(
        SearchNode endpoint,
        CycleSearchState cycle)
    {
        int period = cycle.PeriodActions;
        Span<StateFingerprint> phaseTokens =
            stackalloc StateFingerprint[MaximumDetectedCyclePeriodActions];
        SearchNode cursor = endpoint;
        for (int index = period - 1; index >= 0; index--)
        {
            SearchNode parent = cursor.Parent
                ?? throw new InvalidOperationException("循环族动作链提前抵达根节点。");
            PlanAction action = cursor.Action
                ?? throw new InvalidOperationException("循环族动作链缺少动作。");
            StateFingerprintBuilder phase = new();
            phase.Add(parent.Snapshot.CycleShapeKey.First);
            phase.Add(parent.Snapshot.CycleShapeKey.Second);
            StateFingerprint actionKey = BuildCycleFamilyActionKey(action);
            phase.Add(actionKey.First);
            phase.Add(actionKey.Second);
            phase.Add(cursor.Snapshot.CycleShapeKey.First);
            phase.Add(cursor.Snapshot.CycleShapeKey.Second);
            phaseTokens[index] = phase.Finish();
            cursor = parent;
        }

        int primitivePeriod = period;
        for (int candidatePeriod = 1; candidatePeriod < period; candidatePeriod++)
        {
            if (period % candidatePeriod != 0)
                continue;
            bool repeats = true;
            for (int index = candidatePeriod; index < period; index++)
            {
                if (phaseTokens[index] == phaseTokens[index % candidatePeriod])
                    continue;
                repeats = false;
                break;
            }
            if (!repeats)
                continue;
            primitivePeriod = candidatePeriod;
            break;
        }

        int canonicalRotation = 0;
        for (int rotation = 1; rotation < primitivePeriod; rotation++)
        {
            if (CompareCycleRotations(
                    phaseTokens,
                    primitivePeriod,
                    rotation,
                    canonicalRotation) < 0)
            {
                canonicalRotation = rotation;
            }
        }
        StateFingerprintBuilder sequence = new();
        sequence.Add('F');
        sequence.Add(primitivePeriod);
        for (int index = 0; index < primitivePeriod; index++)
        {
            StateFingerprint token =
                phaseTokens[(canonicalRotation + index) % primitivePeriod];
            sequence.Add(token.First);
            sequence.Add(token.Second);
        }
        return new CanonicalCycleFamilyKey(
            endpoint.Turn,
            primitivePeriod,
            sequence.Finish());
    }

    private static int CompareCycleRotations(
        ReadOnlySpan<StateFingerprint> tokens,
        int period,
        int leftRotation,
        int rightRotation)
    {
        for (int index = 0; index < period; index++)
        {
            StateFingerprint left = tokens[(leftRotation + index) % period];
            StateFingerprint right = tokens[(rightRotation + index) % period];
            int comparison = left.First.CompareTo(right.First);
            if (comparison != 0)
                return comparison;
            comparison = left.Second.CompareTo(right.Second);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static StateFingerprint BuildCycleFamilyActionKey(PlanAction action)
    {
        StateFingerprintBuilder key = new();
        key.Add((int)action.Kind);
        key.Add(action.CardId);
        // CardOccurrence is the stable physical-card address used when exact mutable state is
        // deliberately omitted. It keeps two same-id cards from collapsing into one family
        // while allowing an enchantment/upgrade/replay-state change on that card to recur.
        key.Add(action.CardOccurrence);
        key.Add(action.TargetCombatId ?? uint.MaxValue);
        key.Add(action.PotionId);
        // Inventory identity is already represented by the surrounding cycle shape. Slot is
        // an incidental address and must not split one semantic cycle family across slots.
        key.Add(action.NestedChoicesBeforePrimary);
        AppendCycleChoiceKey(ref key, action.Choice);
        AppendCycleChoiceListKey(ref key, action.NestedChoices);
        AppendCycleChoiceListKey(ref key, action.TurnStartChoices);
        return key.Finish();
    }

    private static bool RequiresBoundedCyclePlanning(SearchNode node)
        => node.Cycle is { } cycle
            && cycle.LastDelta.EnemyHp >= 0
            && cycle.LastDelta.EnemyBlock >= 0
            && !cycle.HasNewEnemyDurabilityProgress
            && cycle.LastDelta.AliveEnemyCount >= 0;

    private bool ShouldStopCycleAtBudget(SearchNode candidate)
    {
        if (candidate.CycleExitProbe is { RemainingActions: > 0 })
            return false;
        CycleSearchState? cycle = candidate.Cycle;
        if (cycle == null)
            return false;
        if (candidate.CycleProbeLease is { } lease
            && (lease.NextActionIndex != 0
                || lease.Tracker.ShapeKey != cycle.ShapeKey
                || lease.Tracker.SequenceKey != cycle.SequenceKey
                || lease.Tracker.PeriodActions != cycle.PeriodActions))
        {
            return false;
        }
        CycleProbeLease? matchingLease = candidate.CycleProbeLease is { } activeLease
            && activeLease.NextActionIndex == 0
            && activeLease.Tracker.ShapeKey == cycle.ShapeKey
            && activeLease.Tracker.SequenceKey == cycle.SequenceKey
            && activeLease.Tracker.PeriodActions == cycle.PeriodActions
                ? activeLease
                : null;
        int observedRepetitions = matchingLease is { } matched
            ? Math.Max(cycle.TotalStructuralRepetitions, matched.CompletedRepetitions)
            : cycle.TotalStructuralRepetitions;
        byte activeEpoch = 0;
        CanonicalCycleFamilyKey family = BuildCanonicalCycleFamilyKey(candidate, cycle);
        if (_run.CycleFamilyLedger.TryGetValue(family, out CycleFamilyLedgerEntry? ledger))
            activeEpoch = ledger.ActiveImprovementEpoch;
        int repetitionBudget = CycleRepetitionBudget(
            cycle.PeriodActions,
            activeEpoch);
        return observedRepetitions > repetitionBudget
            && RequiresBoundedCyclePlanning(candidate);
    }

    private static bool HasImprovingExitEvidence(SearchNode endpoint)
        => endpoint.CycleProbeLease is { NextActionIndex: 0 } lease
            && lease.LastCompletedRepetitionImproved;

    private static bool IsCycleContinuation(SearchNode candidate)
        => candidate.Cycle is { TotalStructuralRepetitions: > 1 };

    private bool ShouldRejectCycleCandidate(SearchNode candidate)
    {
        bool continuesCycle = IsCycleContinuation(candidate);
        bool unproductiveCycle = ShouldStopUnproductiveCycle(candidate);
        bool stoppedAsUnproductive = unproductiveCycle
            && continuesCycle
            && candidate.CycleProbeLease == null
            && candidate.CycleExitProbe == null;
        bool stoppedAtBudget = ShouldStopCycleAtBudget(candidate);
        bool stoppedAtFamilyBudget = !stoppedAsUnproductive
            && !stoppedAtBudget
            && candidate.CycleExitProbe == null
            && candidate.Cycle != null
            && RequiresBoundedCyclePlanning(candidate)
            && !TryAdmitCycleFamilyDepth(candidate);
        if (stoppedAsUnproductive || stoppedAtBudget || stoppedAtFamilyBudget)
        {
            _run.CycleContinuationsStopped++;
            return true;
        }
        if (unproductiveCycle
            && continuesCycle
            && candidate.CycleProbeLease != null)
        {
            _run.CycleProbeContinuationsExpanded++;
        }
        return false;
    }

    private static ActionCandidate? SelectPreferredCycleAdmissionCandidate(
        IEnumerable<ActionCandidate> candidates,
        int bestMaxHp)
        => candidates
            .OrderBy(candidate => CycleHealthRisk(candidate.Node, bestMaxHp))
            .ThenBy(candidate => candidate.Node.PotionStrategicCost)
            .ThenBy(candidate => candidate.Node.Turn)
            .ThenBy(candidate => candidate.Node.ActionCount)
            .ThenByDescending(candidate => candidate.Node.Snapshot.ProjectedPlayerHp)
            .ThenByDescending(candidate => candidate.Node.Score)
            .ThenBy(candidate => candidate.Node, CycleCandidateDeterministicComparer.Instance)
            .Select(candidate => (ActionCandidate?)candidate)
            .FirstOrDefault();

    private static bool AdmitExistingCycleProbeLease(
        IReadOnlyList<ActionCandidate> candidates,
        List<ActionCandidate> selected,
        int bestMaxHp)
    {
        if (selected.Any(candidate => HasValidCycleProbeLease(candidate.Node)))
            return true;
        ActionCandidate single = default;
        int count = 0;
        foreach (ActionCandidate candidate in candidates)
        {
            if (selected.Any(current => ReferenceEquals(current.Node, candidate.Node))
                || !HasValidCycleProbeLease(candidate.Node))
            {
                continue;
            }
            single = candidate;
            count++;
        }
        if (count == 0)
            return false;
        ActionCandidate leased = count == 1
            ? single
            : SelectPreferredCycleAdmissionCandidate(
                candidates.Where(candidate =>
                    !selected.Any(current => ReferenceEquals(current.Node, candidate.Node))
                    && HasValidCycleProbeLease(candidate.Node)),
                bestMaxHp)
                ?? throw new InvalidOperationException("循环 admission 无法选择现有租约。");
        selected.Add(leased);
        return true;
    }

    private void AdmitCycleProbeCandidate(
        IReadOnlyList<ActionCandidate> candidates,
        List<ActionCandidate> selected)
    {
        if (candidates.Count == 0)
            return;
        int bestMaxHp = candidates.Max(candidate => candidate.Node.Snapshot.PlayerMaxHp);

        // A lease issued before transposition owns the one bounded cycle lane for this
        // parent. Preserve that exact candidate instead of minting a second lease for a
        // different recurrence that happened to win an ordinary action slot.
        if (AdmitExistingCycleProbeLease(candidates, selected, bestMaxHp))
            return;

        // A recurrence that already won an ordinary action slot still needs its bounded
        // continuation lease. Small frontiers do not necessarily reach the later global
        // portfolio pass, so merely leaving the node in `selected` can make the next
        // structurally identical (but internally changed) phase look disposable.
        ActionCandidate? selectedEvidence = SelectPreferredCycleAdmissionCandidate(
            selected.Where(candidate => candidate.Node.CycleExitProbe == null
                && RequiresBoundedCyclePlanning(candidate.Node)),
            bestMaxHp);
        if (selectedEvidence is { } alreadyRetained)
        {
            EnsureBoundedCycleProbeLease(alreadyRetained.Node);
            return;
        }

        ActionCandidate? evidence = SelectPreferredCycleAdmissionCandidate(
            candidates.Where(candidate =>
                !selected.Any(current => ReferenceEquals(current.Node, candidate.Node))
                && candidate.Node.Cycle != null
                && candidate.Node.CycleExitProbe == null
                && RequiresBoundedCyclePlanning(candidate.Node)),
            bestMaxHp);
        if (evidence is not { } retained)
            return;

        // This is a bounded scheduling lease for one exact simulator state. It never replaces
        // a normal candidate and it does not claim the observed recurrence is an infinite loop.
        EnsureBoundedCycleProbeLease(retained.Node);
        selected.Add(retained);
    }

    private void EnsureBoundedCycleProbeLease(SearchNode candidate)
    {
        if (candidate.CycleProbeLease != null
            || candidate.CycleExitProbe != null
            || !RequiresBoundedCyclePlanning(candidate))
        {
            return;
        }
        StartCycleProbeLease(candidate);
        _run.CycleCandidatesProtected++;
    }

    private static void AdmitCycleExitProbeCandidate(
        IReadOnlyList<ActionCandidate> candidates,
        List<ActionCandidate> selected)
    {
        if (candidates.Count == 0)
            return;
        if (selected.Any(candidate => candidate.Node.CycleExitProbe != null))
            return;
        int bestMaxHp = candidates.Max(candidate => candidate.Node.Snapshot.PlayerMaxHp);
        ActionCandidate? retained = candidates
            .Where(candidate => candidate.Node.CycleExitProbe != null
                && !selected.Any(current => ReferenceEquals(current.Node, candidate.Node)))
            .OrderBy(candidate => CycleHealthRisk(candidate.Node, bestMaxHp))
            .ThenBy(candidate => candidate.Node.PotionStrategicCost)
            .ThenBy(candidate => candidate.Node.Turn)
            .ThenBy(candidate => candidate.Node.ActionCount)
            .ThenByDescending(candidate => candidate.Node.Snapshot.ProjectedPlayerHp)
            .ThenByDescending(candidate => candidate.Node.Score)
            .ThenBy(candidate => candidate.Node, CycleCandidateDeterministicComparer.Instance)
            .Select(candidate => (ActionCandidate?)candidate)
            .FirstOrDefault();
        if (retained is { } candidate)
            selected.Add(candidate);
    }

    private SearchNode AttachCycleProbeLease(SearchNode child)
    {
        if (child.Parent?.CycleProbeLease is not { } lease
            || child.Action is not { } action
            || child.IsTerminal
            || child.BoundaryReason != SearchBoundaryReason.None
            || child.Turn != child.Parent.Turn
            || lease.NextActionIndex < 0
            || lease.NextActionIndex >= lease.Tracker.ActionKeys.Count
            || BuildCycleActionKey(action)
                != lease.Tracker.ActionKeys[lease.NextActionIndex])
        {
            child.CycleProbeLease = null;
            return child;
        }

        int nextActionIndex = lease.NextActionIndex + 1;
        bool completedRepetition = nextActionIndex == lease.Tracker.PeriodActions;
        if (completedRepetition)
        {
            CycleSearchState? cycle = child.Cycle;
            if (cycle == null
                || cycle.ShapeKey != lease.Tracker.ShapeKey
                || cycle.SequenceKey != lease.Tracker.SequenceKey
                || cycle.PeriodActions != lease.Tracker.PeriodActions)
            {
                child.CycleProbeLease = null;
                return child;
            }
            nextActionIndex = 0;
        }
        child.CycleProbeLease = lease with
        {
            NextActionIndex = nextActionIndex,
            CompletedRepetitions = lease.CompletedRepetitions
                + (completedRepetition ? 1 : 0),
        };
        return child;
    }

    private static void StartCycleProbeLease(SearchNode node)
    {
        if (node.CycleProbeLease != null)
            return;
        CycleSearchState cycle = node.Cycle
            ?? throw new InvalidOperationException("循环探测租约缺少循环证据。");
        StateFingerprint[] actionKeys = new StateFingerprint[cycle.PeriodActions];
        SearchNode cursor = node;
        for (int index = actionKeys.Length - 1; index >= 0; index--)
        {
            actionKeys[index] = BuildCycleActionKey(cursor.Action
                ?? throw new InvalidOperationException("循环探测动作链提前抵达根节点。"));
            cursor = cursor.Parent
                ?? throw new InvalidOperationException("循环探测动作链长度与父链不一致。");
        }
        node.CycleProbeLease = new CycleProbeLease(
            new CycleProbeTracker(
                cycle.ShapeKey,
                cycle.SequenceKey,
                actionKeys,
                BuildCanonicalCycleFamilyKey(node, cycle)),
            0,
            0,
            false,
            false,
            0);
    }

    private static StateFingerprint BuildCycleActionKey(PlanAction action)
    {
        StateFingerprintBuilder key = new();
        AppendCycleActionKey(ref key, action);
        return key.Finish();
    }

    private static string DescribeCycleActions(SearchNode child, int actionCount)
    {
        string[] tokens = new string[actionCount];
        SearchNode cursor = child;
        for (int index = actionCount - 1; index >= 0; index--)
        {
            tokens[index] = PolicyActionToken(cursor.Action!);
            cursor = cursor.Parent!;
        }
        return string.Join('>', tokens);
    }

    private static void AppendCycleActionKey(
        ref StateFingerprintBuilder key,
        PlanAction action)
    {
        // The parent walk is newest-to-oldest. Reversing every candidate consistently keeps
        // the key order-sensitive without allocating a temporary action array on the hot path.
        key.Add((int)action.Kind);
        key.Add(action.Turn);
        key.Add(action.CardId);
        key.Add(action.CardOccurrence);
        // Mutable card state is intentionally not part of scheduling identity. The generated
        // PlanAction still carries the exact state key used by ReplayAction, so this coarser key
        // cannot skip simulation; it only recognizes an N-step setup pattern across mutations.
        key.Add(action.TargetIndex);
        key.Add(action.TargetCombatId ?? uint.MaxValue);
        key.Add(action.PotionId);
        key.Add(action.PotionSlot);
        // ReplayCount is mutable payoff state, not route structure. ReplayAction still consumes
        // the exact current count from PlanAction on every simulated edge.
        key.Add(action.NestedChoicesBeforePrimary);
        AppendCycleChoiceKey(ref key, action.Choice);
        AppendCycleChoiceListKey(ref key, action.NestedChoices);
        AppendCycleChoiceListKey(ref key, action.TurnStartChoices);
    }

    private static void AppendCycleChoiceListKey(
        ref StateFingerprintBuilder key,
        IReadOnlyList<PlanCardChoice>? choices)
    {
        key.Add(choices?.Count ?? -1);
        if (choices == null)
            return;
        foreach (PlanCardChoice choice in choices)
            AppendCycleChoiceKey(ref key, choice);
    }

    private static void AppendCycleChoiceKey(
        ref StateFingerprintBuilder key,
        PlanCardChoice? choice)
    {
        if (choice == null)
        {
            key.Add(-1);
            return;
        }
        key.Add((int)choice.Effect);
        key.Add((int)choice.SourcePile);
        key.Add(choice.SourceId);
        key.Add(choice.ContextId);
        key.Add((int)choice.Timing);
        key.Add(choice.Cards.Count);
        foreach (PlanCardToken card in choice.Cards)
        {
            key.Add(card.CardId);
            key.Add(card.UpgradeLevel);
            key.Add(card.SourceOccurrence);
            key.Add(card.OptionOccurrence);
        }
    }

    private static int CompareCycleCandidateDeterministicFingerprints(
        SearchNode left,
        SearchNode right)
    {
        int comparison = left.StateKey.First.CompareTo(right.StateKey.First);
        if (comparison != 0)
            return comparison;
        comparison = left.StateKey.Second.CompareTo(right.StateKey.Second);
        if (comparison != 0)
            return comparison;

        StateFingerprint leftAction = BuildCycleDeterministicActionFingerprint(left.Action);
        StateFingerprint rightAction = BuildCycleDeterministicActionFingerprint(right.Action);
        comparison = leftAction.First.CompareTo(rightAction.First);
        if (comparison != 0)
            return comparison;
        comparison = leftAction.Second.CompareTo(rightAction.Second);
        if (comparison != 0)
            return comparison;

        StateFingerprint leftParent = left.Parent?.StateKey ?? default;
        StateFingerprint rightParent = right.Parent?.StateKey ?? default;
        comparison = leftParent.First.CompareTo(rightParent.First);
        return comparison != 0
            ? comparison
            : leftParent.Second.CompareTo(rightParent.Second);
    }

    private sealed class CycleCandidateDeterministicComparer : IComparer<SearchNode>
    {
        public static CycleCandidateDeterministicComparer Instance { get; } = new();

        public int Compare(SearchNode? left, SearchNode? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left == null)
                return 1;
            if (right == null)
                return -1;
            return CompareCycleCandidateDeterministicFingerprints(left, right);
        }
    }

    private static StateFingerprint BuildCycleDeterministicActionFingerprint(
        PlanAction? action)
    {
        StateFingerprintBuilder key = new();
        if (action == null)
        {
            key.Add(-1);
            return key.Finish();
        }

        key.Add((int)action.Kind);
        key.Add(action.Turn);
        key.Add(action.CardId);
        key.Add(action.CardOccurrence);
        key.Add(action.TargetIndex);
        key.Add(action.TargetCombatId ?? uint.MaxValue);
        key.Add(action.PotionId);
        key.Add(action.PotionSlot);
        key.Add(action.ReplayCount);
        key.Add(action.CardStateKey);
        key.Add(action.CardStateOccurrence);
        key.Add(action.EndsPlayerTurn);
        key.Add(action.NestedChoicesBeforePrimary);
        AppendCycleDeterministicChoiceKey(ref key, action.Choice);
        AppendCycleDeterministicChoiceListKey(ref key, action.NestedChoices);
        AppendCycleDeterministicChoiceListKey(ref key, action.TurnStartChoices);

        IReadOnlyList<PlanRelicEffect>? relicEffects = action.RelicEffects;
        key.Add(relicEffects?.Count ?? -1);
        if (relicEffects != null)
        {
            foreach (PlanRelicEffect effect in relicEffects)
            {
                // Titles are locale-dependent. The stable id and semantic summary are enough
                // to make otherwise-identical action annotations deterministic.
                key.Add(effect.RelicId);
                key.Add(effect.Summary);
            }
        }
        return key.Finish();
    }

    private static void AppendCycleDeterministicChoiceListKey(
        ref StateFingerprintBuilder key,
        IReadOnlyList<PlanCardChoice>? choices)
    {
        key.Add(choices?.Count ?? -1);
        if (choices == null)
            return;
        foreach (PlanCardChoice choice in choices)
            AppendCycleDeterministicChoiceKey(ref key, choice);
    }

    private static void AppendCycleDeterministicChoiceKey(
        ref StateFingerprintBuilder key,
        PlanCardChoice? choice)
    {
        if (choice == null)
        {
            key.Add(-1);
            return;
        }
        key.Add((int)choice.Effect);
        key.Add((int)choice.SourcePile);
        key.Add(choice.SourceId);
        key.Add(choice.ContextId);
        key.Add((int)choice.Timing);
        key.Add(choice.Cards.Count);
        foreach (PlanCardToken card in choice.Cards)
        {
            key.Add(card.CardId);
            key.Add(card.UpgradeLevel);
            key.Add(card.StateKey);
            key.Add(card.SourceOccurrence);
            key.Add(card.OptionOccurrence);
        }
    }
}
