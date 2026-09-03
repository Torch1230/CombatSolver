namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const int MaximumDetectedCyclePeriodActions = 32;
    private const int MaximumCycleExitProbeActions = 8;
    private const int MaximumCycleExitProbeTurnTransitions = 2;

    private SearchNode AttachCycleSchedulingEvidence(SearchNode child)
    {
        child = AttachCycleEvidence(child);
        AttachPropagatedCycleExitProbe(child);
        ObserveCycleExitProgress(child);
        return AttachCrossTurnSchedulingEvidence(child);
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
            actionKeys[actionKeyCount++] = BuildCycleActionKey(action);
            actionCursor = actionParent;
        }

        StateFingerprint fallbackSequenceKey = default;
        CycleTransitionDelta fallbackDelta = default;
        SearchNode? fallbackAncestor = null;
        int fallbackActionCount = 0;
        int fallbackTotalStructuralRepetitions = 1;
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
                    HasExactStateChange = ancestor.StateKey != child.StateKey,
                    TotalStructuralRepetitions = prior.TotalStructuralRepetitions + 1,
                };
                return AttachCycleProbeLease(AttachSelectedCycle(child, candidate));
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
                && priorAncestor.Snapshot.CycleShapeKey == child.Snapshot.CycleShapeKey
                && CycleTransitionDelta.Between(
                    priorAncestor.Snapshot,
                    ancestor.Snapshot) == delta)
            {
                EnemyDurabilityVector priorEnemyFloor = EnemyDurabilityProgress.MergeMinimum(
                    priorAncestor.Snapshot.EnemyDurabilityByCombatId,
                    ancestor.Snapshot.EnemyDurabilityByCombatId,
                    out _);
                EnemyDurabilityVector enemyFloor = EnemyDurabilityProgress.MergeMinimum(
                    priorEnemyFloor,
                    child.Snapshot.EnemyDurabilityByCombatId,
                    out bool hasNewWindowEnemyProgress);
                CycleSearchState candidate = new(
                    child.Snapshot.CycleShapeKey,
                    sequenceKey,
                    actionCount,
                    continuesPrior ? prior!.Repetitions + 1 : 2,
                    delta,
                    true)
                {
                    PriorCycleEndpoint = ancestor,
                    PriorProjectedPlayerHp = ancestor.Snapshot.ProjectedPlayerHp,
                    EnemyDurabilityFloor = enemyFloor,
                    HasNewEnemyDurabilityProgress = hasNewWindowEnemyProgress,
                    HasExactStateChange = ancestor.StateKey != child.StateKey,
                    TotalStructuralRepetitions = continuesPrior
                        ? prior!.TotalStructuralRepetitions + 1
                        : 2,
                };
                return AttachCycleProbeLease(AttachSelectedCycle(child, candidate));
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
        return child with { Cycle = cycle };
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
            CycleProbeLease childLease = child.CycleProbeLease!.Value;
            CycleProbeTracker tracker = trackers[index];
            bool improvedSinceWrap = lease.ImprovedSinceWrap;
            bool completedRepetition = childLease.NextActionIndex == 0;
            if (completedRepetition
                && child.Cycle is { } cycle
                && ShouldReprobeCycleExits(cycle))
            {
                tracker.RearmExitProbes();
                improvedSinceWrap = true;
            }
            child.CycleProbeLease = childLease with
            {
                Tracker = tracker,
                ImprovedSinceWrap = completedRepetition ? false : improvedSinceWrap,
                LastCompletedRepetitionImproved = completedRepetition
                    ? improvedSinceWrap
                    : lease.LastCompletedRepetitionImproved,
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
            || child.Parent is not { CycleExitProbe: { RemainingActions: > 0 } probe }
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
        bool completesProbe = child.IsTerminal
            || turnTransitions < 0
            || remainingActions <= 0;
        child.CycleExitObservation = new CycleExitObservation(
            probe.OriginTracker,
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
            RemainingTurnTransitions = turnTransitions,
        };
    }

    private static void CommitCycleExitObservation(SearchNode child)
    {
        if (child.PendingCycleExitObservation is { } pending)
        {
            if (pending.OriginNode.CycleProbeLease is { } currentLease
                && ReferenceEquals(currentLease.Tracker, pending.OriginTracker)
                && currentLease.NextActionIndex == pending.OriginPhaseIndex)
            {
                long exitGeneration = pending.OriginTracker.ObserveExit(
                    pending.OriginPhaseIndex,
                    pending.ExitActionKey,
                    pending.Quality);
                if (exitGeneration > 0)
                {
                    pending.OriginNode.CycleProbeLease = currentLease with
                    {
                        ImprovedSinceWrap = true,
                    };
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
                        MaximumCycleExitProbeTurnTransitions);
                }
            }
            child.PendingCycleExitObservation = null;
        }

        if (child.CycleExitObservation is { } observation)
        {
            _ = observation.OriginTracker.ObserveExit(
                observation.OriginPhaseIndex,
                observation.ExitActionKey,
                observation.Quality);
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
            Math.Max(0L, (long)after.PlayerBlock - before.PlayerBlock),
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

    private int CycleRepetitionBudget(int periodActions)
    {
        int proportional = Math.Clamp(_profile.MaxExpandedNodes / 16, 32, 256);
        int power = 1;
        while (power <= proportional / 2)
            power <<= 1;
        return Math.Max(2, power / Math.Max(1, periodActions));
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
            || cycle.LastDelta.PlayerBlock != 0
            || HasDurableCycleProgress(cycle.LastDelta);

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
        int repetitionBudget = CycleRepetitionBudget(cycle.PeriodActions);
        if (matchingLease is { LastCompletedRepetitionImproved: true })
            repetitionBudget = checked(repetitionBudget * 2);
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
        if (stoppedAsUnproductive || stoppedAtBudget)
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
            new CycleProbeTracker(cycle.ShapeKey, cycle.SequenceKey, actionKeys),
            0,
            0,
            false,
            false);
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
}
