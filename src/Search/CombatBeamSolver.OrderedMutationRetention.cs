namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const int MaximumOrderedMutationRunAdmissions = 2048;
    private const int MaximumOrderedMutationLayerAdmissions = 48;
    private const int MaximumOrderedMutationPaidCohortServiceAdmissions = 16;
    private const int MaximumOrderedMutationGenericClaimServiceAdmissions = 32;
    private const int MaximumOrderedMutationBoundaryHandoffAdmissions = 32;
    private const int MaximumOrderedMutationAlternativeAdmissions = 16;
    private const int MaximumOrderedMutationObservationAdmissions =
        MaximumOrderedMutationLayerAdmissions;
    private const int MaximumOrderedMutationObservationSteps = 3;
    private const int MaximumOrderedMutationRootAdmissions = 128;
    private const int MaximumOrderedMutationInitialAdmissions = 64;
    private const int MaximumOrderedMutationProgressTailRootAdmissions = 64;
    private const int MaximumOrderedMutationProgressTailInitialAdmissions = 32;
    private const int MaximumOrderedMutationContinuationsPerLineagePerPrune = 2;
    private const int MaximumOrderedMutationOptionCohortOutcomes = 3;
    private const int MaximumOrderedMutationAdmissionsPerInitialPerPrune =
        MaximumOrderedMutationContinuationsPerLineagePerPrune;
    private const int MaximumOrderedMutationCounterfactualSiblingAdmissions =
        2 * MaximumOrderedMutationContinuationsPerLineagePerPrune;

    /// <summary>
    /// Carries order-sensitive mutation identity with O(choices on this edge) work. No ancestor
    /// walk is needed on the expansion or retention hot paths.
    /// </summary>
    private SearchNode AttachOrderedMutationLineage(SearchNode child)
    {
        if (child.Parent is not { } parent || child.Action is not { } action)
            return child;

        // Activation is a one-prune scheduling transaction, not semantic lineage. In
        // particular, record `with` copies used by test/search helpers must not extend it.
        child.OrderedMutationActivationTicket = null;
        child.OrderedMutationLeaseTransitionPending = false;
        child.OrderedMutationContinuationHandoff = false;
        child.OrderedMutationContinuationBridge = false;
        child.OrderedMutationObservationRequested = false;
        child.OrderedMutationObservationDebtSettlementPending = false;
        // Boundary evidence belongs only to the edge which created its node. Never copy an old
        // completed segment into a descendant's live lineage.
        child.OrderedMutationBoundaryLineage = null;
        child.OrderedMutationObservationStepsRemaining = Math.Max(
            0,
            parent.OrderedMutationObservationStepsRemaining - 1);

        bool crossedTurn = child.Turn != parent.Turn;
        bool crossedShuffle = child.Snapshot.ShufflesCrossed != parent.Snapshot.ShufflesCrossed;
        if (crossedTurn || crossedShuffle)
            child.OrderedMutationObservationStepsRemaining = 0;
        (OrderedMutationLineage? completedBoundaryLineage,
            OrderedMutationLineage? liveLineage) = BuildOrderedMutationLineageSegments(
            parent.OrderedMutationLineage,
            action,
            parent.Turn,
            child.Turn,
            crossedTurn,
            crossedShuffle);
        if (completedBoundaryLineage != null)
        {
            child.OrderedMutationBoundaryLineage = new OrderedMutationBoundaryLineage(
                completedBoundaryLineage,
                parent.Turn,
                parent.Snapshot.ShufflesCrossed,
                child.Turn,
                child.Snapshot.ShufflesCrossed);
        }
        child.OrderedMutationLineage = liveLineage;
        if (parent.OrderedMutationRetentionLease is { } parentLease)
        {
            bool boundaryReached = parentLease.BoundaryReached
                || child.Turn != parentLease.OriginTurn
                || child.Snapshot.ShufflesCrossed != parentLease.OriginShufflesCrossed;
            child.OrderedMutationRetentionLease = boundaryReached == parentLease.BoundaryReached
                ? parentLease
                : parentLease with { BoundaryReached = boundaryReached };
            child.OrderedMutationLeaseTransitionPending = true;
            child.OrderedMutationAdmissionCharged = false;
        }
        return child;
    }

    private static (OrderedMutationLineage? CompletedBoundaryLineage,
        OrderedMutationLineage? LiveLineage) BuildOrderedMutationLineageSegments(
        OrderedMutationLineage? inheritedLineage,
        PlanAction action,
        int parentTurn,
        int childTurn,
        bool crossedTurn,
        bool crossedShuffle)
    {
        OrderedMutationLineageBuilder beforeBoundary = new(inheritedLineage);
        AppendOrdinaryOrderedMutationChoices(ref beforeBoundary, parentTurn, action);
        OrderedMutationLineage? completed = beforeBoundary.Build();
        if (!crossedTurn && !crossedShuffle)
            return (null, completed);

        // Ordinary choices execute in the source segment. Only choices explicitly recorded as
        // turn-start work are known to execute after a crossed turn boundary.
        OrderedMutationLineageBuilder afterBoundary = new(null);
        if (crossedTurn && action.TurnStartChoices is { Count: > 0 } turnStartChoices)
        {
            foreach (PlanCardChoice choice in turnStartChoices)
                afterBoundary.Append(childTurn, action, choice);
        }
        return (completed, afterBoundary.Build());
    }

    private static void AppendOrdinaryOrderedMutationChoices(
        ref OrderedMutationLineageBuilder lineage,
        int turn,
        PlanAction action)
    {
        IReadOnlyList<PlanCardChoice> nestedChoices = action.NestedChoices ?? [];
        int primaryIndex = action.NestedChoicesBeforePrimary;
        for (int index = 0; index < primaryIndex; index++)
            lineage.Append(turn, action, nestedChoices[index]);
        if (action.Choice != null)
            lineage.Append(turn, action, action.Choice);
        for (int index = primaryIndex; index < nestedChoices.Count; index++)
            lineage.Append(turn, action, nestedChoices[index]);
    }

    private struct OrderedMutationLineageBuilder(OrderedMutationLineage? inherited)
    {
        private readonly OrderedMutationLineage? _inherited = inherited;
        private int _turn = inherited?.Turn ?? 0;
        private int _choiceCount = inherited?.ChoiceCount ?? 0;
        private StateFingerprint _sequenceKey = inherited?.SequenceKey ?? default;
        private StateFingerprint _effectMultisetKey =
            inherited?.EffectMultisetKey ?? default;
        private bool _changed;

        public void Append(
            int turn,
            PlanAction action,
            PlanCardChoice choice)
        {
            if (!IsOrderedPersistentMutationEffect(choice.Effect) || choice.Cards.Count == 0)
            {
                return;
            }

            StateFingerprintBuilder sequence = new();
            sequence.Add('M');
            sequence.Add(_choiceCount);
            sequence.Add(_sequenceKey.First);
            sequence.Add(_sequenceKey.Second);
            sequence.Add((int)action.Kind);
            sequence.Add(action.CardId);
            sequence.Add(action.CardStateKey);
            sequence.Add(action.PotionId);
            sequence.Add((int)choice.Effect);
            sequence.Add((int)choice.SourcePile);
            sequence.Add(choice.SourceId);
            sequence.Add(choice.ContextId);
            sequence.Add((int)choice.Timing);
            sequence.Add(choice.Cards.Count);
            foreach (PlanCardToken card in choice.Cards)
            {
                sequence.Add(card.CardId);
                sequence.Add(card.UpgradeLevel);
                sequence.Add(card.StateKey);
                sequence.Add(card.SourceOccurrence);
                sequence.Add(card.OptionOccurrence);
            }

            StateFingerprintBuilder effect = new();
            effect.Add('E');
            effect.Add((int)choice.Effect);
            StateFingerprint effectKey = effect.Finish();
            _turn = turn;
            _choiceCount = checked(_choiceCount + 1);
            _sequenceKey = sequence.Finish();
            _effectMultisetKey = new StateFingerprint(
                unchecked(_effectMultisetKey.First
                    + StateFingerprintBuilder.MixFirst(effectKey.First)),
                unchecked(_effectMultisetKey.Second
                    + StateFingerprintBuilder.MixSecond(effectKey.Second)));
            _changed = true;
        }

        public readonly OrderedMutationLineage? Build()
            => _changed
                ? new OrderedMutationLineage(
                    _turn,
                    _choiceCount,
                    _sequenceKey,
                    _effectMultisetKey)
                : _inherited;
    }

    private static void VerifyOrderedMutationLineageBuilderForTesting()
    {
        OrderedMutationLineage inherited = new(
            Turn: 6,
            ChoiceCount: 3,
            SequenceKey: new StateFingerprint(0x110UL, 0x111UL),
            EffectMultisetKey: new StateFingerprint(0x112UL, 0x113UL));
        PlanAction action = new(
            PlanActionKind.PlayCard,
            Turn: 7);
        PlanCardToken anonymousToken = new(
            CardId: "",
            UpgradeLevel: 0,
            StateKey: "",
            SourceOccurrence: 0,
            OptionOccurrence: 0,
            Title: "");

        // Ignored choices must not manufacture a replacement object. Keeping this as an
        // identity assertion guards the hot-path sharing contract, not merely value equality.
        OrderedMutationLineageBuilder noOp = new(inherited);
        noOp.Append(
            turn: 7,
            action,
            new PlanCardChoice(
                PlanChoiceEffect.MoveToHand,
                MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
                [anonymousToken]));
        if (!ReferenceEquals(noOp.Build(), inherited))
        {
            throw new InvalidOperationException(
                "未发生持久有序变异时，lineage builder 没有复用继承对象。");
        }

        PlanCardChoice firstChoice = new(
            PlanChoiceEffect.Upgrade,
            MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
            [anonymousToken]);
        PlanCardChoice secondChoice = new(
            PlanChoiceEffect.Modify,
            MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand,
            [anonymousToken with
            {
                SourceOccurrence = 1,
                OptionOccurrence = 1,
            }]);

        // Production batches every persistent choice on the action and invokes Build once.
        // Compare that single-materialization path with the former step-by-step semantics.
        OrderedMutationLineageBuilder batched = new(inherited);
        batched.Append(turn: 7, action, firstChoice);
        batched.Append(turn: 7, action, secondChoice);
        OrderedMutationLineage? batchedResult = batched.Build();

        OrderedMutationLineageBuilder firstStep = new(inherited);
        firstStep.Append(turn: 7, action, firstChoice);
        OrderedMutationLineage? firstStepResult = firstStep.Build();
        OrderedMutationLineageBuilder secondStep = new(firstStepResult);
        secondStep.Append(turn: 7, action, secondChoice);
        OrderedMutationLineage? stepwiseResult = secondStep.Build();

        if (batchedResult == null
            || ReferenceEquals(batchedResult, inherited)
            || batchedResult.ChoiceCount != inherited.ChoiceCount + 2
            || batchedResult.Turn != 7
            || batchedResult != stepwiseResult)
        {
            throw new InvalidOperationException(
                "同一动作的多个有序变异没有以一次 Build 保持逐步 lineage 语义。");
        }

        PlanAction boundaryAction = action with
        {
            Choice = firstChoice,
            TurnStartChoices = [secondChoice],
        };
        (OrderedMutationLineage? completedBoundary,
            OrderedMutationLineage? newTurn) = BuildOrderedMutationLineageSegments(
            inherited,
            boundaryAction,
            parentTurn: 6,
            childTurn: 7,
            crossedTurn: true,
            crossedShuffle: false);
        PlanAction noMutation = action with { Choice = null, TurnStartChoices = null };
        (OrderedMutationLineage? repeatedBoundary,
            OrderedMutationLineage? nextLive) = BuildOrderedMutationLineageSegments(
            newTurn,
            noMutation,
            parentTurn: 7,
            childTurn: 7,
            crossedTurn: false,
            crossedShuffle: false);
        if (completedBoundary is not { Turn: 6, ChoiceCount: 4 }
            || newTurn is not { Turn: 7, ChoiceCount: 1 }
            || completedBoundary.SequenceKey == newTurn.SequenceKey
            || repeatedBoundary != null
            || !ReferenceEquals(nextLive, newTurn))
        {
            throw new InvalidOperationException(
                "有序变异没有在 turn boundary 拆成一次性旧段与可继承的新 turn 段。");
        }

        (OrderedMutationLineage? shuffleBoundary,
            OrderedMutationLineage? afterShuffle) = BuildOrderedMutationLineageSegments(
            inherited,
            boundaryAction with { TurnStartChoices = null },
            parentTurn: 6,
            childTurn: 6,
            crossedTurn: false,
            crossedShuffle: true);
        if (shuffleBoundary is not { Turn: 6, ChoiceCount: 4 }
            || afterShuffle != null)
        {
            throw new InvalidOperationException(
                "有序变异没有在 shuffle boundary 交付旧段后清空 live lineage。");
        }

        PlanAction firstThenSecond = action with
        {
            Choice = firstChoice,
            NestedChoices = [secondChoice],
            NestedChoicesBeforePrimary = 0,
        };
        PlanAction secondThenFirst = action with
        {
            Choice = secondChoice,
            NestedChoices = [firstChoice],
            NestedChoicesBeforePrimary = 0,
        };
        (OrderedMutationLineage? firstCompleted, _) =
            BuildOrderedMutationLineageSegments(
                inheritedLineage: null,
                action: firstThenSecond,
                parentTurn: 6,
                childTurn: 7,
                crossedTurn: true,
                crossedShuffle: false);
        (OrderedMutationLineage? secondCompleted, _) =
            BuildOrderedMutationLineageSegments(
                inheritedLineage: null,
                action: secondThenFirst,
                parentTurn: 6,
                childTurn: 7,
                crossedTurn: true,
                crossedShuffle: false);
        OrderedMutationBoundaryLineage firstBoundary = new(
            firstCompleted!, 6, 0, 7, 0);
        OrderedMutationBoundaryLineage secondBoundary = new(
            secondCompleted!, 6, 0, 7, 0);
        if (firstCompleted == null
            || secondCompleted == null
            || firstCompleted.SequenceKey == secondCompleted.SequenceKey
            || firstCompleted.EffectMultisetKey != secondCompleted.EffectMultisetKey
            || OrderedMutationCollisionLineage(firstBoundary, live: null)?.SequenceKey
                != firstCompleted.SequenceKey
            || OrderedMutationCollisionLineage(secondBoundary, live: null)?.SequenceKey
                != secondCompleted.SequenceKey
            || OrderedMutationCollisionLineage(boundary: null, live: nextLive) != nextLive)
        {
            throw new InvalidOperationException(
                "边界 prune 没有保留同 effect multiset 的两种旧段顺序，或旧段泄漏到下一边。");
        }

    }

    private static StateFingerprint BuildOrderedMutationDerivedKey(
        StateFingerprint currentKey,
        StateFingerprint childSequenceKey)
    {
        StateFingerprintBuilder key = new();
        key.Add('D');
        key.Add(currentKey.First);
        key.Add(currentKey.Second);
        key.Add(childSequenceKey.First);
        key.Add(childSequenceKey.Second);
        return key.Finish();
    }

    private static OrderedMutationLineage? OrderedMutationCollisionLineage(SearchNode node)
        => OrderedMutationCollisionLineage(
            node.OrderedMutationBoundaryLineage,
            node.OrderedMutationLineage);

    private static OrderedMutationLineage? OrderedMutationCollisionLineage(
        OrderedMutationBoundaryLineage? boundary,
        OrderedMutationLineage? live)
        => boundary?.CompletedLineage ?? live;

    private static StateFingerprint BuildOrderedMutationCheckpointKey(
        StateFingerprint currentKey,
        StateFingerprint checkpointStateKey)
    {
        StateFingerprintBuilder key = new();
        key.Add('C');
        key.Add(currentKey.First);
        key.Add(currentKey.Second);
        key.Add(checkpointStateKey.First);
        key.Add(checkpointStateKey.Second);
        return key.Finish();
    }

    private static StateFingerprint BuildOrderedMutationCheckpointStateKey(SearchNode node)
    {
        StateFingerprint tacticalKey = BuildOrderedPileTacticalKey(node);
        StateFingerprintBuilder key = new();
        key.Add(node.Turn);
        key.Add(node.Snapshot.ShufflesCrossed);
        key.Add(tacticalKey.First);
        key.Add(tacticalKey.Second);
        key.Add(node.Snapshot.ProjectedShuffleOrderKey.First);
        key.Add(node.Snapshot.ProjectedShuffleOrderKey.Second);
        return key.Finish();
    }

    private static StateFingerprint BuildOrderedMutationBoundaryTransitionKey(
        StateFingerprint inheritedKey,
        StateFingerprint? preBoundaryMutationSequence,
        StateFingerprint checkpointStateKey,
        StateFingerprint? postBoundaryMutationSequence)
    {
        StateFingerprint key = inheritedKey;
        if (preBoundaryMutationSequence is { } preBoundary)
            key = BuildOrderedMutationDerivedKey(key, preBoundary);
        key = BuildOrderedMutationCheckpointKey(key, checkpointStateKey);
        if (postBoundaryMutationSequence is { } postBoundary)
            key = BuildOrderedMutationDerivedKey(key, postBoundary);
        return key;
    }

    private static StateFingerprint BuildOrderedPileTacticalKey(SearchNode node)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        StateFingerprintBuilder key = new();
        key.Add(node.Turn);
        key.Add(node.PotionCount);
        key.Add(node.PotionStrategicCost);
        key.Add(node.FutureSoldHp);
        key.Add(snapshot.PlayerHp);
        key.Add(snapshot.ProjectedPlayerHp);
        key.Add(snapshot.PlayerBlock);
        key.Add(snapshot.EnemyHp);
        key.Add(snapshot.RawEnemyHp);
        key.Add(snapshot.MaxCurrentEnemyHp);
        key.Add(snapshot.EnemyCombatDistributionKey.First);
        key.Add(snapshot.EnemyCombatDistributionKey.Second);
        key.Add(snapshot.AliveEnemyMask);
        key.Add(snapshot.RevivingEnemyCount);
        key.Add(snapshot.FocusTargetCombatId ?? uint.MaxValue);
        key.Add(snapshot.PersistentBuffValue);
        key.Add((int)snapshot.StrategicSetupTraits);
        key.Add(snapshot.FutureResourceValue);
        key.Add(snapshot.DelayedDamageValue);
        key.Add(snapshot.EnemyStrengthSuppression);
        key.Add(snapshot.EnemyWeakTurns);
        key.Add(snapshot.EnemyVulnerableTurns);
        key.Add(snapshot.FocusTargetVulnerableTurns);
        key.Add(snapshot.EnemyControlDistributionKey.First);
        key.Add(snapshot.EnemyControlDistributionKey.Second);
        key.Add(snapshot.SandpitRemaining);
        key.Add(snapshot.LiveDeckClutter);
        key.Add(snapshot.OutstandingStolenResource);
        key.Add(snapshot.Energy);
        key.Add(snapshot.Stars);
        key.Add(snapshot.HandCount);
        key.Add(snapshot.PocketwatchCardsPlayedThisTurn);
        key.Add(snapshot.PocketwatchCardsPlayedLastTurn);
        key.Add(snapshot.PocketwatchCardThreshold);
        key.Add(snapshot.ShufflesCrossed);
        key.Add((int)snapshot.BoundaryReason);
        key.Add(snapshot.UnorderedPileKey.First);
        key.Add(snapshot.UnorderedPileKey.Second);
        return key.Finish();
    }

    private static bool IsOrderedPersistentMutationEffect(PlanChoiceEffect effect)
        => CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(effect);

    private void PromoteOrderedMutationProgressTail(SearchNode node)
    {
        bool observationBridge = node.Parent is
            { OrderedMutationContinuationBridge: true };
        bool recentObservationProgress = observationBridge
            && HasRecentOrderedMutationObservationProgress(node);
        if (recentObservationProgress)
        {
            PromoteOrderedMutationProgressTail(node, hasProgressEvidence: true);
            return;
        }
        if (!TryBuildOrderedMutationProgressRegionKey(
                node,
                out CycleRegionKey region)
            || !_run.CycleRegionLedger.TryGetValue(
                region,
                out CycleRegionLedgerEntry? ledger))
        {
            return;
        }
        PromoteOrderedMutationProgressTail(node, ledger);
    }

    private bool HasRecentOrderedMutationObservationProgress(SearchNode node)
    {
        SimulationSnapshot current = node.Snapshot;
        SearchNode? cursor = node.Parent;
        for (int steps = 0;
             steps < MaximumOrderedMutationObservationSteps
                 && cursor != null
                 && cursor.Turn == node.Turn;
             steps++, cursor = cursor.Parent)
        {
            SimulationSnapshot prior = cursor.Snapshot;
            if (TotalEnemyDurability(current) < TotalEnemyDurability(prior)
                || current.AliveEnemyCount < prior.AliveEnemyCount
                || CycleRegionSetupValue(current) > CycleRegionSetupValue(prior)
                || current.ProjectedPlayerHp > prior.ProjectedPlayerHp
                || UsefulDefensiveBlockReserve(current)
                    > UsefulDefensiveBlockReserve(prior))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryBuildOrderedMutationProgressRegionKey(
        SearchNode node,
        out CycleRegionKey region)
    {
        // This coordinator lookup intentionally ignores a newly propagated exit probe. The
        // evidence attachment path used to perform promotion before propagation, and worker
        // scheduling must not change which semantic region is eligible.
        if (node.CycleProbeLease is { } probeLease)
        {
            region = new CycleRegionKey(node.Turn, probeLease.Tracker.ShapeKey);
            return true;
        }
        if (node.Cycle is { } cycle)
        {
            region = new CycleRegionKey(node.Turn, cycle.ShapeKey);
            return true;
        }
        region = default;
        return false;
    }

    private void PromoteOrderedMutationProgressTail(
        SearchNode node,
        CycleRegionLedgerEntry ledger)
        => PromoteOrderedMutationProgressTail(
            node,
            hasProgressEvidence: ledger.ProgressEpochs > 0);

    private void PromoteOrderedMutationProgressTail(
        SearchNode node,
        bool hasProgressEvidence)
    {
        if (!hasProgressEvidence
            || node.OrderedMutationRetentionLease is not { } lease
            || lease.ProgressTailEligible
            || node.OrderedMutationActivationTicket != null
            || _run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(lease.RootKey) <= 0
            || _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(lease.InitialKey)
                <= 0)
        {
            return;
        }
        node.OrderedMutationRetentionLease = lease with
        {
            ProgressTailEligible = true,
        };
    }

    private static int OrderedMutationRootAdmissionLimit(
        OrderedMutationRetentionLease lease)
        => MaximumOrderedMutationRootAdmissions
            + (lease.ProgressTailEligible
                ? MaximumOrderedMutationProgressTailRootAdmissions
                : 0);

    private static int OrderedMutationInitialAdmissionLimit(
        OrderedMutationRetentionLease lease)
        => MaximumOrderedMutationInitialAdmissions
            + (lease.ProgressTailEligible
                ? MaximumOrderedMutationProgressTailInitialAdmissions
                : 0);

    private static bool CanRetainOrderedMutationLease(
        SearchRunContext run,
        SearchNode node)
        => node.OrderedMutationRetentionLease is { } lease
            && (node.OrderedMutationAdmissionCharged
                || HasRemainingOrderedMutationLeaseBudget(run, lease));

    private static bool HasPaidOrderedMutationAdmission(SearchNode node)
        => node.OrderedMutationRetentionLease != null
            && (node.OrderedMutationAdmissionCharged || node.OrderedMutationAdmissionPending);

    private static int OrderedMutationAdmissionServiceCost(SearchNode node)
        => HasPaidOrderedMutationAdmission(node) ? 0 : 1;

    private static bool CanMintOrderedMutationLease(
        SearchRunContext run,
        OrderedMutationRetentionLease lease)
        => HasRemainingOrderedMutationLeaseBudget(run, lease);

    private static bool HasRemainingOrderedMutationLeaseBudget(
        SearchRunContext run,
        OrderedMutationRetentionLease lease)
        => run.OrderedMutationPortfolioNodesConsumed < MaximumOrderedMutationRunAdmissions
            && run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(lease.RootKey)
                < OrderedMutationRootAdmissionLimit(lease)
            && run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(lease.InitialKey)
                < OrderedMutationInitialAdmissionLimit(lease)
            && run.OrderedMutationAdmissionsByLease.GetValueOrDefault(lease.Key)
                < OrderedMutationRetentionLease.MaximumProtectedAdmissions;

    private static bool CanReserveOrderedMutationAdmissions(
        SearchRunContext run,
        IEnumerable<OrderedMutationRetentionLease> leases)
    {
        OrderedMutationRetentionLease[] leaseArray = leases.ToArray();
        if (leaseArray.Length == 0)
            return true;
        if (run.OrderedMutationPortfolioNodesConsumed
                > MaximumOrderedMutationRunAdmissions - leaseArray.Length)
        {
            return false;
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in leaseArray
                     .GroupBy(lease => lease.RootKey))
        {
            int admissionLimit = group.Min(OrderedMutationRootAdmissionLimit);
            if (run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(group.Key)
                    > admissionLimit - group.Count())
            {
                return false;
            }
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in leaseArray
                     .GroupBy(lease => lease.InitialKey))
        {
            int admissionLimit = group.Min(OrderedMutationInitialAdmissionLimit);
            if (run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(group.Key)
                    > admissionLimit - group.Count())
            {
                return false;
            }
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in leaseArray
                     .GroupBy(lease => lease.Key))
        {
            if (run.OrderedMutationAdmissionsByLease.GetValueOrDefault(group.Key)
                    > OrderedMutationRetentionLease.MaximumProtectedAdmissions - group.Count())
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReserveOrderedMutationAdmissions(
        SearchRunContext run,
        IDictionary<StateFingerprint, int> reservedByRootLease,
        IDictionary<StateFingerprint, int> reservedByInitialLease,
        IDictionary<StateFingerprint, int> reservedByLease,
        ref int reservedRunAdmissions,
        IEnumerable<OrderedMutationRetentionLease> leases)
    {
        OrderedMutationRetentionLease[] leaseArray = leases.ToArray();
        if (leaseArray.Length == 0)
            return true;
        if (run.OrderedMutationPortfolioNodesConsumed
                > MaximumOrderedMutationRunAdmissions
                    - reservedRunAdmissions
                    - leaseArray.Length)
        {
            return false;
        }
        IGrouping<StateFingerprint, OrderedMutationRetentionLease>[] groupedByRoot = leaseArray
            .GroupBy(lease => lease.RootKey)
            .ToArray();
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in groupedByRoot)
        {
            _ = reservedByRootLease.TryGetValue(group.Key, out int alreadyReserved);
            int admissionLimit = group.Min(OrderedMutationRootAdmissionLimit);
            if (run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(group.Key)
                    > admissionLimit
                        - alreadyReserved
                        - group.Count())
            {
                return false;
            }
        }
        IGrouping<StateFingerprint, OrderedMutationRetentionLease>[] groupedByInitial = leaseArray
            .GroupBy(lease => lease.InitialKey)
            .ToArray();
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in
                 groupedByInitial)
        {
            _ = reservedByInitialLease.TryGetValue(group.Key, out int alreadyReserved);
            int consumedInitialAdmissions =
                run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(group.Key);
            int admissionLimit = group.Min(OrderedMutationInitialAdmissionLimit);
            if (consumedInitialAdmissions
                    >= admissionLimit
                        - OrderedMutationRetentionLease.MaximumProtectedAdmissions
                && alreadyReserved
                    > MaximumOrderedMutationAdmissionsPerInitialPerPrune - group.Count())
            {
                return false;
            }
            if (consumedInitialAdmissions
                    > admissionLimit
                        - alreadyReserved
                        - group.Count())
            {
                return false;
            }
        }
        IGrouping<StateFingerprint, OrderedMutationRetentionLease>[] groupedByLease = leaseArray
            .GroupBy(lease => lease.Key)
            .ToArray();
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in groupedByLease)
        {
            _ = reservedByLease.TryGetValue(group.Key, out int alreadyReserved);
            if (run.OrderedMutationAdmissionsByLease.GetValueOrDefault(group.Key)
                    > OrderedMutationRetentionLease.MaximumProtectedAdmissions
                        - alreadyReserved
                        - group.Count())
            {
                return false;
            }
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in groupedByRoot)
        {
            _ = reservedByRootLease.TryGetValue(group.Key, out int alreadyReserved);
            reservedByRootLease[group.Key] = checked(alreadyReserved + group.Count());
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in
                 groupedByInitial)
        {
            _ = reservedByInitialLease.TryGetValue(group.Key, out int alreadyReserved);
            reservedByInitialLease[group.Key] = checked(alreadyReserved + group.Count());
        }
        foreach (IGrouping<StateFingerprint, OrderedMutationRetentionLease> group in groupedByLease)
        {
            _ = reservedByLease.TryGetValue(group.Key, out int alreadyReserved);
            reservedByLease[group.Key] = checked(
                alreadyReserved + group.Count());
        }
        reservedRunAdmissions = checked(reservedRunAdmissions + leaseArray.Length);
        return true;
    }

    private static bool TryChargeOrderedMutationAdmission(
        SearchRunContext run,
        SearchNode node)
    {
        if (node.OrderedMutationAdmissionCharged)
            return true;
        if (node.OrderedMutationRetentionLease is not { } lease
            || !TryConsumeOrderedMutationAdmission(
                run.OrderedMutationAdmissionsByRootLease,
                run.OrderedMutationAdmissionsByInitialLease,
                run.OrderedMutationAdmissionsByLease,
                ref run.OrderedMutationPortfolioNodesConsumed,
                lease,
                out _))
        {
            return false;
        }
        node.OrderedMutationAdmissionCharged = true;
        return true;
    }

    private static void ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(
        SearchNode node)
    {
        node.OrderedMutationRetentionLease = null;
        node.OrderedMutationActivationTicket = null;
        node.OrderedMutationLeaseTransitionPending = false;
        node.OrderedMutationAdmissionPending = false;
        node.OrderedMutationAdmissionCharged = false;
        node.OrderedMutationAdmissionSequence = int.MaxValue;
        node.OrderedMutationContinuationHandoff = false;
        node.OrderedMutationContinuationBridge = false;
        node.OrderedMutationObservationRequested = false;
        node.OrderedMutationObservationDebtSettlementPending = false;
        node.OrderedMutationObservationStepsRemaining = 0;
    }

    private static bool HasFullyPendingAtomicOrderedMutationPair(
        int memberCount,
        int pendingCount,
        int distinctMemberSequences,
        int distinctPendingSequences)
        => memberCount == 2
            && pendingCount == 2
            && distinctMemberSequences == 2
            && distinctPendingSequences == 2;

    private static bool IsOrderedMutationPortfolioOnlyPendingMember(
        bool admissionPending,
        bool independentlySelected)
        => admissionPending && !independentlySelected;

    private static bool TryCommitOrderedMutationHandoffSourceAdmission<T>(
        IDictionary<T, OrderedMutationHandoffSourceLedgerKey> pending,
        IDictionary<OrderedMutationHandoffSourceLedgerKey, int> committed,
        T admitted)
        where T : notnull
    {
        if (!pending.Remove(
                admitted,
                out OrderedMutationHandoffSourceLedgerKey sourceLedgerKey))
        {
            return false;
        }
        _ = committed.TryGetValue(sourceLedgerKey, out int priorAdmissions);
        committed[sourceLedgerKey] = checked(priorAdmissions + 1);
        return true;
    }

    private static bool TryStageOrderedMutationHandoffSourceAdmission<T>(
        IDictionary<T, OrderedMutationHandoffSourceLedgerKey> pending,
        IDictionary<OrderedMutationHandoffSourceLedgerKey, int> provisional,
        T admitted,
        OrderedMutationHandoffSourceLedgerKey sourceLedgerKey,
        bool admissionPending,
        bool admissionCharged)
        where T : notnull
    {
        if (!admissionPending || admissionCharged)
            return false;
        if (pending.TryGetValue(
                admitted,
                out OrderedMutationHandoffSourceLedgerKey existingSourceLedgerKey))
        {
            if (existingSourceLedgerKey != sourceLedgerKey)
            {
                throw new InvalidOperationException(
                    "同一 ordered-mutation admission 被登记到不同 handoff source。");
            }
            return false;
        }

        _ = provisional.TryGetValue(sourceLedgerKey, out int priorAdmissions);
        int nextAdmissions = checked(priorAdmissions + 1);
        pending.Add(admitted, sourceLedgerKey);
        provisional[sourceLedgerKey] = nextAdmissions;
        return true;
    }

    private void FinalizeOrderedMutationPortfolio(List<SearchNode> selected)
    {
        foreach (IGrouping<StateFingerprint, SearchNode> activation in selected
                     .Where(node => node.OrderedMutationActivationTicket is { })
                     .GroupBy(node => node.OrderedMutationActivationTicket!.Key)
                     .ToArray())
        {
            SearchNode[] members = activation.ToArray();
            int distinctMemberSequences = members
                    .Select(node => OrderedMutationCollisionLineage(node)?.SequenceKey ?? default)
                    .Distinct()
                    .Take(2)
                    .Count();
            SearchNode[] pending = members
                .Where(node => node.OrderedMutationAdmissionPending
                    && !node.OrderedMutationAdmissionCharged)
                .ToArray();
            int distinctPendingSequences = pending
                    .Select(node => OrderedMutationCollisionLineage(node)?.SequenceKey ?? default)
                    .Distinct()
                    .Take(2)
                    .Count();
            bool canCommit = HasFullyPendingAtomicOrderedMutationPair(
                    members.Length,
                    pending.Length,
                    distinctMemberSequences,
                    distinctPendingSequences)
                && CanReserveOrderedMutationAdmissions(
                    _run,
                    pending.Select(node =>
                        node.OrderedMutationRetentionLease
                        ?? throw new InvalidOperationException(
                            "有序变异待提交节点缺少 lease。")));
            if (canCommit)
            {
                foreach (SearchNode node in pending)
                {
                    if (!TryChargeOrderedMutationAdmission(_run, node))
                    {
                        throw new InvalidOperationException(
                            "有序变异原子激活预检成功后仍发生了部分扣账。");
                    }
                }
                _run.OrderedMutationColdAtomicCommitted = checked(
                    _run.OrderedMutationColdAtomicCommitted + 1);
            }
            else
            {
                _run.OrderedMutationColdAtomicRejected = checked(
                    _run.OrderedMutationColdAtomicRejected + 1);
                _run.OrderedMutationLeaseExpiredBudget = checked(
                    _run.OrderedMutationLeaseExpiredBudget + members.Length);
            }

            foreach (SearchNode node in members)
            {
                node.OrderedMutationActivationTicket = null;
                if (canCommit)
                {
                    node.OrderedMutationAdmissionPending = false;
                    continue;
                }

                // A route that survived an independent ordinary lane remains valid, but a
                // singleton or unpaid pair member no longer proves an order collision and must
                // not carry the newly minted lease. Portfolio-only members are removed below.
                bool portfolioOnly = IsOrderedMutationPortfolioOnlyPendingMember(
                    node.OrderedMutationAdmissionPending,
                    _run.PendingOrderedMutationOrdinaryFallbackNodes.Contains(node));
                if (!portfolioOnly)
                {
                    _run.OrderedMutationOrdinaryFallbacks = checked(
                        _run.OrderedMutationOrdinaryFallbacks + 1);
                }
                ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(node);
                if (portfolioOnly)
                    selected.Remove(node);
            }
        }

        HashSet<SearchNode> rejectedPending = new(ReferenceEqualityComparer.Instance);
        foreach (SearchNode node in selected
                     .Where(candidate => candidate.OrderedMutationAdmissionPending)
                     .OrderBy(candidate => candidate.OrderedMutationAdmissionSequence)
                     .ThenBy(candidate => candidate.StateKey.First)
                     .ThenBy(candidate => candidate.StateKey.Second))
        {
            if (node.OrderedMutationAdmissionCharged
                || TryChargeOrderedMutationAdmission(_run, node))
            {
                node.OrderedMutationAdmissionPending = false;
                node.OrderedMutationAdmissionSequence = int.MaxValue;
                continue;
            }
            // A naturally ranked route is not owned by this portfolio. On an unexpected commit
            // failure it loses only the scheduling lease and remains an ordinary candidate;
            // portfolio-only work is removed.
            if (_run.PendingOrderedMutationOrdinaryFallbackNodes.Contains(node))
            {
                _run.OrderedMutationLeaseExpiredBudget = checked(
                    _run.OrderedMutationLeaseExpiredBudget + 1);
                _run.OrderedMutationOrdinaryFallbacks = checked(
                    _run.OrderedMutationOrdinaryFallbacks + 1);
                ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(node);
            }
            else
            {
                _run.OrderedMutationLeaseExpiredBudget = checked(
                    _run.OrderedMutationLeaseExpiredBudget + 1);
                node.OrderedMutationAdmissionPending = false;
                node.OrderedMutationAdmissionSequence = int.MaxValue;
                rejectedPending.Add(node);
            }
        }
        selected.RemoveAll(rejectedPending.Contains);
        // Commit source coverage only for the final ordered frontier; rejected atomic work must
        // not bias later handoffs.
        int pendingSourceAdmissions =
            _run.PendingOrderedMutationHandoffSourceByNode.Count;
        if (pendingSourceAdmissions > MaximumOrderedMutationLayerAdmissions)
        {
            throw new InvalidOperationException(
                "handoff source pending ledger 超出单层 ordered-mutation 硬上限。");
        }
        foreach (SearchNode survivor in selected)
        {
            if (!survivor.OrderedMutationAdmissionCharged)
                continue;
            _ = TryCommitOrderedMutationHandoffSourceAdmission(
                _run.PendingOrderedMutationHandoffSourceByNode,
                _run.OrderedMutationHandoffAdmissionsByInitialAndSource,
                survivor);
        }
        // Pending entries are scoped to one coordinator prune. Every committed survivor has
        // removed its own entry; everything left was rejected or removed by arbitration.
        _run.PendingOrderedMutationHandoffSourceByNode.Clear();
        if (pendingSourceAdmissions > 0)
        {
            int committedSourceAdmissions = checked(
                _run.OrderedMutationHandoffAdmissionsByInitialAndSource.Values.Sum());
            if (_run.PendingOrderedMutationHandoffSourceByNode.Count != 0
                || _run.OrderedMutationHandoffAdmissionsByInitialAndSource.Count
                    > committedSourceAdmissions
                || committedSourceAdmissions > _run.OrderedMutationPortfolioNodesConsumed
                || committedSourceAdmissions > MaximumOrderedMutationRunAdmissions)
            {
                throw new InvalidOperationException(
                    "handoff source coverage ledger 超出已成功 ordered admission 的硬界。");
            }
        }
        _run.PendingOrderedMutationOrdinaryFallbackNodes.Clear();
    }

    private static void ValidateOrderedMutationAdmissionLedger(SearchRunContext run)
    {
        int admitted = run.OrderedMutationPortfolioNodesConsumed;
        if (admitted < 0
            || admitted > MaximumOrderedMutationRunAdmissions
            || run.OrderedMutationAdmissionsByRootLease.Values.Sum() != admitted
            || run.OrderedMutationAdmissionsByInitialLease.Values.Sum() != admitted
            || run.OrderedMutationAdmissionsByLease.Values.Sum() != admitted)
        {
            throw new InvalidOperationException(
                "ordered-mutation admission 超出 2048/run 或 root/initial/lease 账本不同步。");
        }
    }

    private static bool TrySelectAtomicOrderedMutationPair(
        IReadOnlyList<StateFingerprint> orderedSequenceKeys,
        int availableAdmissions,
        int preferredAnchorIndex,
        out int firstIndex,
        out int secondIndex)
    {
        firstIndex = -1;
        secondIndex = -1;
        if (availableAdmissions < 2 || orderedSequenceKeys.Count < 2)
            return false;
        firstIndex = (uint)preferredAnchorIndex < (uint)orderedSequenceKeys.Count
            ? preferredAnchorIndex
            : 0;
        for (int index = 0; index < orderedSequenceKeys.Count; index++)
        {
            if (index == firstIndex)
                continue;
            if (orderedSequenceKeys[index] == orderedSequenceKeys[firstIndex])
                continue;
            secondIndex = index;
            return true;
        }
        firstIndex = -1;
        return false;
    }

    private static List<T> RoundRobinOrderedMutationQueues<T>(
        IReadOnlyList<List<T>> queues)
    {
        if (queues.Count == 0)
            return [];
        int rounds = queues.Max(queue => queue.Count);
        List<T> ordered = new(queues.Sum(queue => queue.Count));
        for (int round = 0; round < rounds; round++)
        {
            foreach (List<T> queue in queues)
            {
                if (round < queue.Count)
                    ordered.Add(queue[round]);
            }
        }
        return ordered;
    }

    private static List<T> OrderOrderedMutationHierarchy<T>(
        IEnumerable<T> items,
        Func<T, StateFingerprint> rootSelector,
        Func<T, StateFingerprint> initialSelector,
        Func<T, StateFingerprint> currentSelector,
        Func<StateFingerprint, int> rootAdmissions,
        Func<StateFingerprint, int> initialAdmissions,
        Func<StateFingerprint, int> currentAdmissions,
        Func<T, int> prioritySelector,
        Func<IEnumerable<T>, List<T>> orderCurrentQueue)
    {
        List<List<T>> rootQueues = items
            .GroupBy(rootSelector)
            .OrderBy(group => rootAdmissions(group.Key))
            .ThenBy(group => group.Min(prioritySelector))
            .ThenBy(group => group.Key.First)
            .ThenBy(group => group.Key.Second)
            .Select(root =>
            {
                List<List<T>> initialQueues = root
                    .GroupBy(initialSelector)
                    .OrderBy(group => initialAdmissions(group.Key))
                    .ThenBy(group => group.Min(prioritySelector))
                    .ThenBy(group => group.Key.First)
                    .ThenBy(group => group.Key.Second)
                    .Select(initial =>
                    {
                        List<List<T>> currentQueues = initial
                            .GroupBy(currentSelector)
                            .OrderBy(group => currentAdmissions(group.Key))
                            .ThenBy(group => group.Min(prioritySelector))
                            .ThenBy(group => group.Key.First)
                            .ThenBy(group => group.Key.Second)
                            .Select(group => orderCurrentQueue(group))
                            .ToList();
                        return RoundRobinOrderedMutationQueues(currentQueues);
                    })
                    .ToList();
                return RoundRobinOrderedMutationQueues(initialQueues);
            })
            .ToList();
        return RoundRobinOrderedMutationQueues(rootQueues);
    }

    private static bool TryConsumeOrderedMutationAdmission(
        IDictionary<StateFingerprint, int> admissionsByRootLease,
        IDictionary<StateFingerprint, int> admissionsByInitialLease,
        IDictionary<StateFingerprint, int> admissionsByLease,
        ref int runAdmissions,
        OrderedMutationRetentionLease lease,
        out int rootAdmissions)
    {
        _ = admissionsByRootLease.TryGetValue(lease.RootKey, out rootAdmissions);
        _ = admissionsByInitialLease.TryGetValue(
            lease.InitialKey,
            out int initialAdmissions);
        _ = admissionsByLease.TryGetValue(lease.Key, out int leaseAdmissions);
        if (rootAdmissions >= OrderedMutationRootAdmissionLimit(lease)
            || initialAdmissions >= OrderedMutationInitialAdmissionLimit(lease)
            || leaseAdmissions >= OrderedMutationRetentionLease.MaximumProtectedAdmissions
            || runAdmissions >= MaximumOrderedMutationRunAdmissions)
        {
            return false;
        }
        rootAdmissions = checked(rootAdmissions + 1);
        initialAdmissions = checked(initialAdmissions + 1);
        leaseAdmissions = checked(leaseAdmissions + 1);
        admissionsByRootLease[lease.RootKey] = rootAdmissions;
        admissionsByInitialLease[lease.InitialKey] = initialAdmissions;
        admissionsByLease[lease.Key] = leaseAdmissions;
        runAdmissions = checked(runAdmissions + 1);
        return true;
    }

    private static void VerifyNaturalOrderedMutationFairnessForTesting()
    {
        static List<T> OrderForTest<T>(
            IEnumerable<T> candidates,
            Func<T, OrderedMutationRetentionLease> leaseSelector,
            SearchRunContext run,
            IComparer<T> withinLeafComparer)
            => OrderOrderedMutationHierarchy(
                candidates,
                candidate => leaseSelector(candidate).RootKey,
                candidate => leaseSelector(candidate).InitialKey,
                candidate => leaseSelector(candidate).Key,
                key => run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(key),
                key => run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(key),
                key => run.OrderedMutationAdmissionsByLease.GetValueOrDefault(key),
                candidate => leaseSelector(candidate).PortfolioPriority,
                current => current.OrderBy(candidate => candidate, withinLeafComparer).ToList());

        int[] fanouts = [6, 9, 5, 1, 28, 13, 5, 2];
        int[] priorRootAdmissions = [15, 30, 20, 4, 72, 22, 73, 10];
        SearchRunContext schedulingRun = new(false, new SearchFramePressureSignal());
        List<(int Root, int Quality, OrderedMutationRetentionLease Lease)> selected = [];
        for (int rootIndex = 0; rootIndex < fanouts.Length; rootIndex++)
        {
            StateFingerprint root = new((ulong)rootIndex + 1, 1);
            schedulingRun.OrderedMutationAdmissionsByRootLease[root] =
                priorRootAdmissions[rootIndex];
            for (int index = 0; index < fanouts[rootIndex]; index++)
            {
                OrderedMutationRetentionLease lease = new(
                    root,
                    new StateFingerprint((ulong)rootIndex + 1, 2),
                    new StateFingerprint((ulong)rootIndex + 1, (ulong)(index / 4) + 3),
                    OriginTurn: 1,
                    OriginShufflesCrossed: 0,
                    PortfolioPriority: 0,
                    BoundaryReached: false);
                selected.Add((rootIndex,
                    rootIndex == 4 ? index : 100 + rootIndex * 32 + index,
                    lease));
            }
        }
        selected.Sort((left, right) => left.Quality.CompareTo(right.Quality));
        var original = selected.ToArray();
        var comparer = Comparer<(int Root, int Quality, OrderedMutationRetentionLease Lease)>
            .Create((left, right) => left.Quality.CompareTo(right.Quality));
        var ordered = OrderForTest(
            selected,
            candidate => candidate.Lease,
            schedulingRun,
            comparer);
        if (!selected.SequenceEqual(original)
            || !ordered.SequenceEqual(OrderForTest(
                selected.AsEnumerable().Reverse(),
                candidate => candidate.Lease,
                schedulingRun,
                comparer))
            || ordered.GroupBy(candidate => candidate.Lease.Key)
                .Any(group => !group.Select(candidate => candidate.Quality)
                    .SequenceEqual(group.Select(candidate => candidate.Quality).Order())))
        {
            throw new InvalidOperationException(
                "自然入选 ordered 调度修改了原选择顺序、叶内比较或确定性。");
        }
        var paid = ordered.Take(MaximumOrderedMutationLayerAdmissions).ToArray();
        int[] expectedCounts = [6, 9, 5, 1, 10, 10, 5, 2];
        if (paid.Length != 48
            || !Enumerable.Range(0, fanouts.Length)
                .Select(root => paid.Count(candidate => candidate.Root == root))
                .SequenceEqual(expectedCounts))
        {
            throw new InvalidOperationException(
                "自然入选高扇出族垄断了固定 48 个 ordered admission，挤掉其它活跃族。");
        }
        Dictionary<StateFingerprint, int> roots = [];
        Dictionary<StateFingerprint, int> initials = [];
        Dictionary<StateFingerprint, int> leaves = [];
        int admissions = 0;
        foreach (var candidate in paid)
        {
            if (!TryConsumeOrderedMutationAdmission(
                    roots, initials, leaves, ref admissions, candidate.Lease, out _))
            {
                throw new InvalidOperationException("自然入选公平调度意外耗尽实际账本。");
            }
        }
        if (admissions != 48
            || roots.Values.Sum() != admissions
            || initials.Values.Sum() != admissions
            || leaves.Values.Sum() != admissions
            || roots.Values.Any(count => count > MaximumOrderedMutationRootAdmissions)
            || initials.Values.Any(count => count > MaximumOrderedMutationInitialAdmissions)
            || leaves.Values.Any(count => count > OrderedMutationRetentionLease.MaximumProtectedAdmissions)
            || admissions > MaximumOrderedMutationRunAdmissions)
        {
            throw new InvalidOperationException("自然入选公平调度未精确扣除固定实际预算。");
        }
    }

    internal static void VerifyOrderedMutationRetentionPolicyForTesting()
    {
        VerifyOrderedMutationLineageBuilderForTesting();
        VerifyNaturalOrderedMutationFairnessForTesting();

        if (typeof(OrderedMutationRetentionLease).IsValueType)
        {
            throw new InvalidOperationException(
                "ordered-mutation lease 必须保持引用类型，避免扩大每个普通 SearchNode。");
        }
        if (!HasFullyPendingAtomicOrderedMutationPair(2, 2, 2, 2)
            || HasFullyPendingAtomicOrderedMutationPair(2, 1, 2, 1)
            || HasFullyPendingAtomicOrderedMutationPair(1, 1, 1, 1)
            || HasFullyPendingAtomicOrderedMutationPair(2, 2, 1, 1)
            || IsOrderedMutationPortfolioOnlyPendingMember(
                admissionPending: true,
                independentlySelected: true)
            || !IsOrderedMutationPortfolioOnlyPendingMember(
                admissionPending: true,
                independentlySelected: false))
        {
            throw new InvalidOperationException(
                "有序变异冷启动允许半提交，或错误删除了普通榜 anchor。");
        }

        if (MaximumOrderedMutationRunAdmissions != 2048
            || MaximumOrderedMutationLayerAdmissions != 48
            || MaximumOrderedMutationPaidCohortServiceAdmissions != 16
            || MaximumOrderedMutationGenericClaimServiceAdmissions != 32
            || MaximumOrderedMutationPaidCohortServiceAdmissions
                    + MaximumOrderedMutationGenericClaimServiceAdmissions
                != MaximumOrderedMutationLayerAdmissions
            || MaximumOrderedMutationBoundaryHandoffAdmissions != 32
            || MaximumOrderedMutationAlternativeAdmissions != 16
            || MaximumOrderedMutationObservationAdmissions
                != MaximumOrderedMutationLayerAdmissions
            || MaximumOrderedMutationObservationSteps != 3
            || MaximumOrderedMutationRootAdmissions != 128
            || MaximumOrderedMutationInitialAdmissions != 64
            || MaximumOrderedMutationContinuationsPerLineagePerPrune != 2
            || MaximumOrderedMutationOptionCohortOutcomes != 3
            || MaximumOrderedMutationAdmissionsPerInitialPerPrune != 2)
        {
            throw new InvalidOperationException("有序变异 portfolio 硬上限发生了意外漂移。");
        }

        static int[] SampleOptions(IEnumerable<int> values)
            => BeamRetentionPolicy.SelectOrderedMutationOptionCohort(
                    values.ToArray(),
                    (left, right) => left.CompareTo(right),
                    (left, right) => Math.Abs((long)left - right),
                    (left, right) => left == right,
                    MaximumOrderedMutationOptionCohortOutcomes)
                .ToArray();

        int[] expectedOptionCohort = [0, 4, 2];
        if (!SampleOptions([0, 1, 2, 3, 4]).SequenceEqual(expectedOptionCohort)
            || !SampleOptions([4, 3, 2, 1, 0]).SequenceEqual(expectedOptionCohort)
            || !SampleOptions([2, 4, 0, 3, 1]).SequenceEqual(expectedOptionCohort))
        {
            throw new InvalidOperationException(
                "有序变异类别采样不再稳定保留质量首选、语义极值和质量中位数。");
        }
        if (!BeamRetentionPolicy.SelectActiveOrderedMutationOptionPair(
                expectedOptionCohort,
                priorAdmissions: 0).SequenceEqual([0, 4])
            || !BeamRetentionPolicy.SelectActiveOrderedMutationOptionPair(
                expectedOptionCohort,
                priorAdmissions: 1).SequenceEqual([0, 4])
            || !BeamRetentionPolicy.SelectActiveOrderedMutationOptionPair(
                expectedOptionCohort,
                priorAdmissions: 2).SequenceEqual([0, 2])
            || !BeamRetentionPolicy.SelectActiveOrderedMutationOptionPair(
                expectedOptionCohort,
                priorAdmissions: 3).SequenceEqual([0, 2])
            || !BeamRetentionPolicy.SelectActiveOrderedMutationOptionPair(
                expectedOptionCohort,
                priorAdmissions: 4).SequenceEqual([0, 4]))
        {
            throw new InvalidOperationException(
                "有序变异时间分层采样没有在相同两节点预算内轮换语义极值和质量中位数。");
        }
        for (int count = 1; count <= 4; count++)
        {
            int[] sampled = SampleOptions(Enumerable.Range(0, count));
            if (sampled.Length != Math.Min(
                    count,
                    MaximumOrderedMutationOptionCohortOutcomes)
                || sampled.Distinct().Count() != sampled.Length)
            {
                throw new InvalidOperationException(
                    "有序变异类别采样超出硬上限或产生重复 outcome。");
            }
        }

        static OrderedMutationRetentionLease Lease(
            StateFingerprint root,
            StateFingerprint initial,
            StateFingerprint current)
            => new(
                root,
                initial,
                current,
                OriginTurn: 1,
                OriginShufflesCrossed: 0,
                PortfolioPriority: 0,
                BoundaryReached: false);

        static bool LedgerEquals(
            IReadOnlyDictionary<StateFingerprint, int> left,
            IReadOnlyDictionary<StateFingerprint, int> right)
            => left.Count == right.Count
                && left.All(pair => right.TryGetValue(pair.Key, out int value)
                    && value == pair.Value);

        static bool LedgerSumsAgree(
            int runAdmissions,
            IReadOnlyDictionary<StateFingerprint, int> roots,
            IReadOnlyDictionary<StateFingerprint, int> initials,
            IReadOnlyDictionary<StateFingerprint, int> leaves)
            => runAdmissions == roots.Values.Sum()
                && runAdmissions == initials.Values.Sum()
                && runAdmissions == leaves.Values.Sum();

        Dictionary<StateFingerprint, int> rootLedger = [];
        Dictionary<StateFingerprint, int> initialLedger = [];
        Dictionary<StateFingerprint, int> leaseLedger = [];
        int runAdmissions = 0;
        StateFingerprint sharedRoot = new(0x1000UL, 0x1001UL);
        StateFingerprint sharedInitial = new(0x2000UL, 0x2001UL);
        StateFingerprint firstLeaf = new(0x3000UL, 0x3001UL);
        OrderedMutationRetentionLease firstLease = Lease(
            sharedRoot,
            sharedInitial,
            firstLeaf);
        SearchRunContext chargeRun = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal());
        SearchNode chargeNode = new(
            Action: null,
            ActionCount: 0,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: default,
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: null!,
            CombatProgress: null!)
        {
            OrderedMutationRetentionLease = firstLease,
        };
        HashSet<SearchNode> ordinaryOwnership = new(ReferenceEqualityComparer.Instance)
        {
            chargeNode,
        };
        SearchNode pendingAlias = chargeNode with
        {
            OrderedMutationAdmissionPending = true,
        };
        SearchNode extraUnpaid = chargeNode with { };
        int unpaidPairCost = OrderedMutationAdmissionServiceCost(chargeNode)
            + OrderedMutationAdmissionServiceCost(extraUnpaid);
        if (!ordinaryOwnership.Contains(chargeNode)
            || HasPaidOrderedMutationAdmission(chargeNode)
            || OrderedMutationAdmissionServiceCost(chargeNode) != 1
            || OrderedMutationAdmissionServiceCost(pendingAlias) != 0
            || unpaidPairCost != 2
            || BeamRetentionPolicy.HasOrderedMutationLayerCapacity(47, 48, unpaidPairCost)
            || !BeamRetentionPolicy.HasOrderedMutationLayerCapacity(47, 48,
                OrderedMutationAdmissionServiceCost(pendingAlias)
                    + OrderedMutationAdmissionServiceCost(extraUnpaid)))
        {
            throw new InvalidOperationException(
                "普通路线归属被误认为已支付服务，或 pending alias 重复计费。");
        }
        if (!TryChargeOrderedMutationAdmission(chargeRun, chargeNode)
            || !chargeNode.OrderedMutationAdmissionCharged
            || chargeNode.OrderedMutationRetentionLease != firstLease
            || chargeRun.OrderedMutationPortfolioNodesConsumed != 1
            || !TryChargeOrderedMutationAdmission(chargeRun, chargeNode)
            || chargeNode.OrderedMutationRetentionLease != firstLease
            || chargeRun.OrderedMutationPortfolioNodesConsumed != 1
            || OrderedMutationAdmissionServiceCost(chargeNode) != 0
            || ordinaryOwnership.Count != 1)
        {
            throw new InvalidOperationException(
                "有序变异 admission charge 改写了 lease 值，或重复扣除了真实账本。");
        }
        for (int index = 0;
             index < OrderedMutationRetentionLease.MaximumProtectedAdmissions;
             index++)
        {
            if (!TryConsumeOrderedMutationAdmission(
                    rootLedger,
                    initialLedger,
                    leaseLedger,
                    ref runAdmissions,
                    firstLease,
                    out int rootAdmissions)
                || rootAdmissions != index + 1
                || leaseLedger.GetValueOrDefault(firstLeaf) != index + 1)
            {
                throw new InvalidOperationException(
                    "有序变异 current lane 没有共享同一实际入选预算。");
            }
        }
        if (TryConsumeOrderedMutationAdmission(
                rootLedger,
                initialLedger,
                leaseLedger,
                ref runAdmissions,
                firstLease,
                out _))
        {
            throw new InvalidOperationException("耗尽的 current lane 被重新签发了预算。");
        }
        StateFingerprint secondLeaf = new(0x3002UL, 0x3003UL);
        if (!TryConsumeOrderedMutationAdmission(
                rootLedger,
                initialLedger,
                leaseLedger,
                ref runAdmissions,
                Lease(sharedRoot, sharedInitial, secondLeaf),
                out int continuedRootAdmissions)
            || continuedRootAdmissions
                != OrderedMutationRetentionLease.MaximumProtectedAdmissions + 1
            || leaseLedger.GetValueOrDefault(secondLeaf) != 1)
        {
            throw new InvalidOperationException(
                "有序变异 fresh current lane 没有在 root 余额内继续。");
        }

        rootLedger.Clear();
        initialLedger.Clear();
        leaseLedger.Clear();
        runAdmissions = 0;
        StateFingerprint firstInitial = new(0x4000UL, 0x4001UL);
        StateFingerprint secondInitial = new(0x4002UL, 0x4003UL);
        for (int index = 0; index < MaximumOrderedMutationRootAdmissions; index++)
        {
            OrderedMutationRetentionLease lease = Lease(
                sharedRoot,
                index % 2 == 0 ? firstInitial : secondInitial,
                new StateFingerprint((ulong)index + 0x5000UL, (ulong)index + 0x6000UL));
            if (!TryConsumeOrderedMutationAdmission(
                    rootLedger,
                    initialLedger,
                    leaseLedger,
                    ref runAdmissions,
                    lease,
                    out int rootAdmissions)
                || rootAdmissions != index + 1)
            {
                throw new InvalidOperationException("有序变异 root 预算提前耗尽。");
            }
        }
        StateFingerprint rejectedInitial = new(0x7000UL, 0x7001UL);
        StateFingerprint rejectedLeaf = new(0x7002UL, 0x7003UL);
        Dictionary<StateFingerprint, int> rootBeforeRejection = new(rootLedger);
        Dictionary<StateFingerprint, int> initialBeforeRejection = new(initialLedger);
        Dictionary<StateFingerprint, int> leafBeforeRejection = new(leaseLedger);
        int runBeforeRejection = runAdmissions;
        if (TryConsumeOrderedMutationAdmission(
                rootLedger,
                initialLedger,
                leaseLedger,
                ref runAdmissions,
                Lease(sharedRoot, rejectedInitial, rejectedLeaf),
                out _)
            || rootLedger.GetValueOrDefault(sharedRoot)
                != MaximumOrderedMutationRootAdmissions
            || !LedgerEquals(rootLedger, rootBeforeRejection)
            || !LedgerEquals(initialLedger, initialBeforeRejection)
            || !LedgerEquals(leaseLedger, leafBeforeRejection)
            || runAdmissions != runBeforeRejection)
        {
            throw new InvalidOperationException(
                "派生 lease key 刷新了 root 共享硬预算或发生了部分扣账。");
        }

        SearchRunContext manyRootRun = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal());
        StateFingerprint[] admittedRoots = Enumerable.Range(
                0,
                32)
            .Select(index => new StateFingerprint(
                (ulong)index + 0x7100UL,
                (ulong)index + 0x7200UL))
            .ToArray();
        OrderedMutationRetentionLease[] admittedRootLeases = admittedRoots
            .Select((root, index) => Lease(
                root,
                new StateFingerprint((ulong)index + 0x7300UL, (ulong)index + 0x7400UL),
                new StateFingerprint((ulong)index + 0x7500UL, (ulong)index + 0x7600UL)))
            .ToArray();
        foreach (OrderedMutationRetentionLease lease in admittedRootLeases)
        {
            if (!CanMintOrderedMutationLease(manyRootRun, lease)
                || !CanReserveOrderedMutationAdmissions(manyRootRun, [lease])
                || !TryConsumeOrderedMutationAdmission(
                    manyRootRun.OrderedMutationAdmissionsByRootLease,
                    manyRootRun.OrderedMutationAdmissionsByInitialLease,
                    manyRootRun.OrderedMutationAdmissionsByLease,
                    ref manyRootRun.OrderedMutationPortfolioNodesConsumed,
                    lease,
                    out _))
            {
                throw new InvalidOperationException(
                    "有序变异 distinct root 被历史根数量而非实际 admission 预算提前阻断。");
            }
        }
        if (manyRootRun.OrderedMutationPortfolioNodesConsumed != admittedRootLeases.Length
            || manyRootRun.OrderedMutationAdmissionsByRootLease.Count
                != admittedRootLeases.Length
            || manyRootRun.OrderedMutationAdmissionsByInitialLease.Count
                != admittedRootLeases.Length
            || manyRootRun.OrderedMutationAdmissionsByLease.Count
                != admittedRootLeases.Length
            || manyRootRun.OrderedMutationAdmissionsByRootLease.Values.Any(count => count != 1)
            || !manyRootRun.OrderedMutationAdmissionsByRootLease.ContainsKey(admittedRoots[16])
            || !manyRootRun.OrderedMutationAdmissionsByRootLease.ContainsKey(admittedRoots[31])
            || !LedgerSumsAgree(
                manyRootRun.OrderedMutationPortfolioNodesConsumed,
                manyRootRun.OrderedMutationAdmissionsByRootLease,
                manyRootRun.OrderedMutationAdmissionsByInitialLease,
                manyRootRun.OrderedMutationAdmissionsByLease))
        {
            throw new InvalidOperationException(
                "有序变异 32 个 distinct roots 没有逐项扣入同一有限 run 账本。");
        }
        OrderedMutationRetentionLease laterRootLease = Lease(
            new StateFingerprint(0x7700UL, 0x7701UL),
            new StateFingerprint(0x7702UL, 0x7703UL),
            new StateFingerprint(0x7704UL, 0x7705UL));
        if (!CanMintOrderedMutationLease(manyRootRun, laterRootLease)
            || !CanReserveOrderedMutationAdmissions(manyRootRun, [laterRootLease])
            || !TryConsumeOrderedMutationAdmission(
                manyRootRun.OrderedMutationAdmissionsByRootLease,
                manyRootRun.OrderedMutationAdmissionsByInitialLease,
                manyRootRun.OrderedMutationAdmissionsByLease,
                ref manyRootRun.OrderedMutationPortfolioNodesConsumed,
                laterRootLease,
                out int laterRootAdmissions)
            || laterRootAdmissions != 1
            || manyRootRun.OrderedMutationAdmissionsByRootLease.Count != 33)
        {
            throw new InvalidOperationException(
                "有序变异第 33 个有效 root 没有在真实 run 预算内获得 admission。");
        }
        OrderedMutationRetentionLease existingRootContinuation = Lease(
            admittedRoots[0],
            new StateFingerprint(0x7800UL, 0x7801UL),
            new StateFingerprint(0x7802UL, 0x7803UL));
        if (!CanMintOrderedMutationLease(manyRootRun, existingRootContinuation)
            || !CanReserveOrderedMutationAdmissions(manyRootRun, [existingRootContinuation])
            || !TryConsumeOrderedMutationAdmission(
                manyRootRun.OrderedMutationAdmissionsByRootLease,
                manyRootRun.OrderedMutationAdmissionsByInitialLease,
                manyRootRun.OrderedMutationAdmissionsByLease,
                ref manyRootRun.OrderedMutationPortfolioNodesConsumed,
                existingRootContinuation,
                out int existingRootAdmissions)
            || existingRootAdmissions != 2
            || !LedgerSumsAgree(
                manyRootRun.OrderedMutationPortfolioNodesConsumed,
                manyRootRun.OrderedMutationAdmissionsByRootLease,
                manyRootRun.OrderedMutationAdmissionsByInitialLease,
                manyRootRun.OrderedMutationAdmissionsByLease))
        {
            throw new InvalidOperationException(
                "有序变异多 root 账本破坏了已有 root 的派生 current lane 或账本一致性。");
        }

        rootLedger.Clear();
        initialLedger.Clear();
        leaseLedger.Clear();
        runAdmissions = 0;
        for (int index = 0; index < MaximumOrderedMutationRunAdmissions; index++)
        {
            int rootIndex = index / MaximumOrderedMutationRootAdmissions;
            int withinRoot = index % MaximumOrderedMutationRootAdmissions;
            StateFingerprint root = new(
                (ulong)rootIndex + 0x8000UL,
                (ulong)rootIndex + 0x8100UL);
            StateFingerprint initial = new(
                (ulong)(rootIndex * 2 + withinRoot % 2) + 0x8200UL,
                (ulong)(rootIndex * 2 + withinRoot % 2) + 0x8300UL);
            StateFingerprint current = new(
                (ulong)(rootIndex * 8 + withinRoot / 16) + 0x8400UL,
                (ulong)(rootIndex * 8 + withinRoot / 16) + 0x8500UL);
            if (index == 250
                && (runAdmissions != 250
                    || rootLedger.Count != 2
                    || rootLedger.GetValueOrDefault(root) != 122))
            {
                throw new InvalidOperationException(
                    "有序变异 250 次消费形状不再保留已有成熟 root 的剩余额度。");
            }
            if (!TryConsumeOrderedMutationAdmission(
                    rootLedger,
                    initialLedger,
                    leaseLedger,
                    ref runAdmissions,
                    Lease(root, initial, current),
                    out _))
            {
                throw new InvalidOperationException("有序变异 run-global 预算提前耗尽。");
            }
        }
        Dictionary<StateFingerprint, int> rootBeforeRunRejection = new(rootLedger);
        Dictionary<StateFingerprint, int> initialBeforeRunRejection = new(initialLedger);
        Dictionary<StateFingerprint, int> leafBeforeRunRejection = new(leaseLedger);
        int runBeforeRunRejection = runAdmissions;
        if (TryConsumeOrderedMutationAdmission(
                rootLedger,
                initialLedger,
                leaseLedger,
                ref runAdmissions,
                Lease(
                    new StateFingerprint(0xf000UL, 0xf001UL),
                    new StateFingerprint(0xf002UL, 0xf003UL),
                    new StateFingerprint(0xf004UL, 0xf005UL)),
                out _)
            || !LedgerEquals(rootLedger, rootBeforeRunRejection)
            || !LedgerEquals(initialLedger, initialBeforeRunRejection)
            || !LedgerEquals(leaseLedger, leafBeforeRunRejection)
            || runAdmissions != runBeforeRunRejection)
        {
            throw new InvalidOperationException(
                "有序变异 portfolio 超出 run-global 硬上限或失败后部分扣账。");
        }
        if (!LedgerSumsAgree(
                runAdmissions,
                rootLedger,
                initialLedger,
                leaseLedger))
        {
            throw new InvalidOperationException("有序变异四级消费账本总和不一致。");
        }

        StateFingerprint rootKey = new(0x9000UL, 0x9001UL);
        StateFingerprint initialKey = new(0x9002UL, 0x9003UL);
        StateFingerprint leaseKey = new(0x9abcUL, 0xdef0UL);
        StateFingerprint firstParentLineage = new(1, 2);
        StateFingerprint secondParentLineage = new(3, 4);
        StateFingerprint firstParent = new(5, 6);
        StateFingerprint secondParent = new(7, 8);
        var firstContinuation = new OrderedMutationContinuationLineageSignature(
            rootKey,
            initialKey,
            leaseKey,
            firstParentLineage,
            firstParent);
        var secondContinuation = new OrderedMutationContinuationLineageSignature(
            rootKey,
            initialKey,
            leaseKey,
            secondParentLineage,
            firstParent);
        if (firstContinuation == secondContinuation)
        {
            throw new InvalidOperationException(
                "有序变异 continuation key 丢失了 parent lineage sequence。");
        }
        var otherParentContinuation = new OrderedMutationContinuationLineageSignature(
            rootKey,
            initialKey,
            leaseKey,
            firstParentLineage,
            secondParent);
        if (firstContinuation == otherParentContinuation)
        {
            throw new InvalidOperationException(
                "有序变异 continuation key 丢失了精确 parent state。");
        }
        if (firstContinuation == firstContinuation with
            {
                RootKey = new StateFingerprint(10, 11),
            }
            || firstContinuation == firstContinuation with
            {
                InitialLeaseKey = new StateFingerprint(12, 13),
            }
            || firstContinuation == firstContinuation with
            {
                LeaseKey = new StateFingerprint(14, 15),
            })
        {
            throw new InvalidOperationException(
                "有序变异 continuation key 丢失了 root/initial/current lane 身份。");
        }

        StateFingerprint[] coldSequences =
        [
            new StateFingerprint(10, 100),
            new StateFingerprint(10, 100),
            new StateFingerprint(20, 200),
        ];
        if (!TrySelectAtomicOrderedMutationPair(
                coldSequences,
                availableAdmissions: 2,
                preferredAnchorIndex: -1,
                out int firstColdIndex,
                out int secondColdIndex)
            || coldSequences[firstColdIndex] == coldSequences[secondColdIndex]
            || !TrySelectAtomicOrderedMutationPair(
                coldSequences,
                availableAdmissions: 2,
                preferredAnchorIndex: 2,
                out int anchoredColdIndex,
                out int anchoredCompanionIndex)
            || anchoredColdIndex != 2
            || anchoredCompanionIndex == anchoredColdIndex
            || coldSequences[anchoredColdIndex]
                == coldSequences[anchoredCompanionIndex]
            || TrySelectAtomicOrderedMutationPair(
                coldSequences,
                availableAdmissions: 1,
                preferredAnchorIndex: -1,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "有序变异 cold cohort 没有在无外部循环证据时原子选择两个不同 sequence，" +
                "或没有优先保留已入普通榜的 anchor。");
        }

        SearchRunContext reservationRun = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal());
        reservationRun.OrderedMutationPortfolioNodesConsumed =
            MaximumOrderedMutationRootAdmissions - 1;
        reservationRun.OrderedMutationAdmissionsByRootLease[sharedRoot] =
            MaximumOrderedMutationRootAdmissions - 1;
        Dictionary<StateFingerprint, int> reservedByRootLease = [];
        Dictionary<StateFingerprint, int> reservedByInitialLease = [];
        Dictionary<StateFingerprint, int> reservedByLease = [];
        int reservedRunAdmissions = 0;
        OrderedMutationRetentionLease[] pairLeases =
        [
            Lease(sharedRoot, firstInitial, new StateFingerprint(30, 300)),
            Lease(sharedRoot, secondInitial, new StateFingerprint(40, 400)),
        ];
        if (TryReserveOrderedMutationAdmissions(
                reservationRun,
                reservedByRootLease,
                reservedByInitialLease,
                reservedByLease,
                ref reservedRunAdmissions,
                pairLeases)
            || reservedRunAdmissions != 0
            || reservedByRootLease.Count != 0
            || reservedByInitialLease.Count != 0
            || reservedByLease.Count != 0)
        {
            throw new InvalidOperationException(
                "有序变异原子预留在 root 只剩一个名额时发生了部分写入。");
        }
        reservationRun.OrderedMutationPortfolioNodesConsumed =
            MaximumOrderedMutationRootAdmissions - 2;
        reservationRun.OrderedMutationAdmissionsByRootLease[sharedRoot] =
            MaximumOrderedMutationRootAdmissions - 2;
        if (!TryReserveOrderedMutationAdmissions(
                reservationRun,
                reservedByRootLease,
                reservedByInitialLease,
                reservedByLease,
                ref reservedRunAdmissions,
                pairLeases)
            || reservedRunAdmissions != 2
            || reservedByRootLease.GetValueOrDefault(sharedRoot) != 2
            || reservedByInitialLease.GetValueOrDefault(pairLeases[0].InitialKey) != 1
            || reservedByInitialLease.GetValueOrDefault(pairLeases[1].InitialKey) != 1
            || reservedByLease.GetValueOrDefault(pairLeases[0].Key) != 1
            || reservedByLease.GetValueOrDefault(pairLeases[1].Key) != 1)
        {
            throw new InvalidOperationException(
                "有序变异原子预留没有在恰好两个名额时完整保留 pair。");
        }

        SearchRunContext leafReservationRun = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal());
        leafReservationRun.OrderedMutationPortfolioNodesConsumed =
            OrderedMutationRetentionLease.MaximumProtectedAdmissions - 1;
        leafReservationRun.OrderedMutationAdmissionsByRootLease[sharedRoot] =
            OrderedMutationRetentionLease.MaximumProtectedAdmissions - 1;
        leafReservationRun.OrderedMutationAdmissionsByInitialLease[sharedInitial] =
            OrderedMutationRetentionLease.MaximumProtectedAdmissions - 1;
        leafReservationRun.OrderedMutationAdmissionsByLease[firstLeaf] =
            OrderedMutationRetentionLease.MaximumProtectedAdmissions - 1;
        Dictionary<StateFingerprint, int> leafReservedByRoot = [];
        Dictionary<StateFingerprint, int> leafReservedByInitial = [];
        Dictionary<StateFingerprint, int> leafReservedByLease = [];
        int leafReservedRun = 0;
        if (TryReserveOrderedMutationAdmissions(
                leafReservationRun,
                leafReservedByRoot,
                leafReservedByInitial,
                leafReservedByLease,
                ref leafReservedRun,
                [firstLease, firstLease])
            || leafReservedRun != 0
            || leafReservedByRoot.Count != 0
            || leafReservedByInitial.Count != 0
            || leafReservedByLease.Count != 0)
        {
            throw new InvalidOperationException(
                "有序变异 atomic pair 绕过了 current leaf 上限或留下部分 shadow reservation。");
        }

        static bool TryReserveAtInitialThreshold(
            int consumed,
            int alreadyReserved,
            IReadOnlyList<OrderedMutationRetentionLease> requested)
        {
            SearchRunContext run = new(
                measurePhasePerformance: false,
                new SearchFramePressureSignal());
            OrderedMutationRetentionLease first = requested[0];
            run.OrderedMutationPortfolioNodesConsumed = consumed;
            run.OrderedMutationAdmissionsByRootLease[first.RootKey] = consumed;
            run.OrderedMutationAdmissionsByInitialLease[first.InitialKey] = consumed;
            Dictionary<StateFingerprint, int> reservedRoots = new()
            {
                [first.RootKey] = alreadyReserved,
            };
            Dictionary<StateFingerprint, int> reservedInitials = new()
            {
                [first.InitialKey] = alreadyReserved,
            };
            Dictionary<StateFingerprint, int> reservedLeaves = [];
            int reservedRun = alreadyReserved;
            return TryReserveOrderedMutationAdmissions(
                run,
                reservedRoots,
                reservedInitials,
                reservedLeaves,
                ref reservedRun,
                requested);
        }

        OrderedMutationRetentionLease progressTailLease = firstLease with
        {
            ProgressTailEligible = true,
        };
        if (!TryReserveAtInitialThreshold(
                consumed: MaximumOrderedMutationInitialAdmissions,
                alreadyReserved: MaximumOrderedMutationAdmissionsPerInitialPerPrune,
                [progressTailLease])
            || TryReserveAtInitialThreshold(
                consumed: OrderedMutationInitialAdmissionLimit(progressTailLease)
                    - OrderedMutationRetentionLease.MaximumProtectedAdmissions,
                alreadyReserved: MaximumOrderedMutationAdmissionsPerInitialPerPrune,
                [progressTailLease])
            || TryReserveAtInitialThreshold(
                consumed: MaximumOrderedMutationInitialAdmissions
                    - OrderedMutationRetentionLease.MaximumProtectedAdmissions,
                alreadyReserved: MaximumOrderedMutationAdmissionsPerInitialPerPrune,
                [firstLease])
            || TryReserveAtInitialThreshold(
                consumed: MaximumOrderedMutationInitialAdmissions
                    - OrderedMutationRetentionLease.MaximumProtectedAdmissions,
                alreadyReserved: 1,
                [progressTailLease, firstLease]))
        {
            throw new InvalidOperationException(
                "有序变异 late Initial 阈值没有遵循 progress-tail 的有效累计上限或混合组保守上限。");
        }

        SearchRunContext manyRootReservationRun = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal());
        foreach (OrderedMutationRetentionLease lease in admittedRootLeases)
        {
            if (!TryConsumeOrderedMutationAdmission(
                    manyRootReservationRun.OrderedMutationAdmissionsByRootLease,
                    manyRootReservationRun.OrderedMutationAdmissionsByInitialLease,
                    manyRootReservationRun.OrderedMutationAdmissionsByLease,
                    ref manyRootReservationRun.OrderedMutationPortfolioNodesConsumed,
                    lease,
                    out _))
            {
                throw new InvalidOperationException(
                    "有序变异多 root 原子预留夹具初始化失败。");
            }
        }
        StateFingerprint freshPairRoot = new(0xa000UL, 0xa001UL);
        OrderedMutationRetentionLease[] freshRootPair =
        [
            Lease(
                freshPairRoot,
                new StateFingerprint(0xa100UL, 0xa101UL),
                new StateFingerprint(0xa102UL, 0xa103UL)),
            Lease(
                freshPairRoot,
                new StateFingerprint(0xa104UL, 0xa105UL),
                new StateFingerprint(0xa106UL, 0xa107UL)),
        ];
        Dictionary<StateFingerprint, int> manyReservedByRoot = [];
        Dictionary<StateFingerprint, int> manyReservedByInitial = [];
        Dictionary<StateFingerprint, int> manyReservedByLease = [];
        int manyReservedRun = 0;
        if (!TryReserveOrderedMutationAdmissions(
                manyRootReservationRun,
                manyReservedByRoot,
                manyReservedByInitial,
                manyReservedByLease,
                ref manyReservedRun,
                freshRootPair)
            || manyReservedRun != 2
            || manyReservedByRoot.Count != 1
            || manyReservedByRoot.GetValueOrDefault(freshPairRoot) != 2)
        {
            throw new InvalidOperationException(
                "已有 32 个历史 roots 时，同一新 root 的 atomic pair 未完整预留。");
        }
        OrderedMutationRetentionLease[] laterRootPair =
        [
            laterRootLease,
            laterRootLease with
            {
                InitialKey = new StateFingerprint(0xa108UL, 0xa109UL),
                Key = new StateFingerprint(0xa10aUL, 0xa10bUL),
            },
        ];
        if (!TryReserveOrderedMutationAdmissions(
                manyRootReservationRun,
                manyReservedByRoot,
                manyReservedByInitial,
                manyReservedByLease,
                ref manyReservedRun,
                laterRootPair)
            || manyReservedRun != 4
            || manyReservedByRoot.Count != 2
            || manyReservedByRoot.GetValueOrDefault(laterRootLease.RootKey) != 2)
        {
            throw new InvalidOperationException(
                "历史 roots 与 shadow reservations 错误阻断了后续新 root pair。");
        }
        Dictionary<StateFingerprint, int> twoRootReservedByRoot = [];
        Dictionary<StateFingerprint, int> twoRootReservedByInitial = [];
        Dictionary<StateFingerprint, int> twoRootReservedByLease = [];
        int twoRootReservedRun = 0;
        OrderedMutationRetentionLease otherFreshRootLease = Lease(
            new StateFingerprint(0xa200UL, 0xa201UL),
            new StateFingerprint(0xa202UL, 0xa203UL),
            new StateFingerprint(0xa204UL, 0xa205UL));
        OrderedMutationRetentionLease[] twoFreshRoots =
        [
            Lease(
                new StateFingerprint(0xa300UL, 0xa301UL),
                new StateFingerprint(0xa302UL, 0xa303UL),
                new StateFingerprint(0xa304UL, 0xa305UL)),
            otherFreshRootLease,
        ];
        if (!CanReserveOrderedMutationAdmissions(manyRootReservationRun, twoFreshRoots)
            || !TryReserveOrderedMutationAdmissions(
                manyRootReservationRun,
                twoRootReservedByRoot,
                twoRootReservedByInitial,
                twoRootReservedByLease,
                ref twoRootReservedRun,
                twoFreshRoots)
            || twoRootReservedRun != 2
            || twoRootReservedByRoot.Count != 2
            || twoRootReservedByInitial.Count != 2
            || twoRootReservedByLease.Count != 2)
        {
            throw new InvalidOperationException(
                "两个不同的新 roots 没有在剩余 run 预算内被原子预留。");
        }

        SearchRunContext runLimitedReservation = new(
            measurePhasePerformance: false,
            new SearchFramePressureSignal())
        {
            OrderedMutationPortfolioNodesConsumed =
                MaximumOrderedMutationRunAdmissions - 1,
        };
        Dictionary<StateFingerprint, int> runLimitedReservedByRoot = [];
        Dictionary<StateFingerprint, int> runLimitedReservedByInitial = [];
        Dictionary<StateFingerprint, int> runLimitedReservedByLease = [];
        int runLimitedReserved = 0;
        if (CanReserveOrderedMutationAdmissions(runLimitedReservation, twoFreshRoots)
            || TryReserveOrderedMutationAdmissions(
                runLimitedReservation,
                runLimitedReservedByRoot,
                runLimitedReservedByInitial,
                runLimitedReservedByLease,
                ref runLimitedReserved,
                twoFreshRoots)
            || runLimitedReserved != 0
            || runLimitedReservedByRoot.Count != 0
            || runLimitedReservedByInitial.Count != 0
            || runLimitedReservedByLease.Count != 0)
        {
            throw new InvalidOperationException(
                "有序变异 run 只剩一次 admission 时，fresh-root pair 未被原子拒绝。");
        }

        StateFingerprint fairnessRootA = new(1, 1);
        StateFingerprint fairnessRootB = new(2, 2);
        StateFingerprint fairnessInitialA1 = new(11, 11);
        StateFingerprint fairnessInitialA2 = new(12, 12);
        StateFingerprint fairnessInitialB1 = new(21, 21);
        StateFingerprint fairnessCurrentA11 = new(111, 111);
        StateFingerprint fairnessCurrentA12 = new(112, 112);
        StateFingerprint fairnessCurrentA21 = new(121, 121);
        StateFingerprint fairnessCurrentB11 = new(211, 211);
        (StateFingerprint Root, StateFingerprint Initial, StateFingerprint Current,
            int Priority, string Id)[] fairnessTokens =
        [
            (fairnessRootA, fairnessInitialA1, fairnessCurrentA11, 0, "A11a"),
            (fairnessRootA, fairnessInitialA1, fairnessCurrentA11, 0, "A11b"),
            (fairnessRootA, fairnessInitialA1, fairnessCurrentA12, 0, "A12a"),
            (fairnessRootA, fairnessInitialA2, fairnessCurrentA21, 0, "A21a"),
            (fairnessRootB, fairnessInitialB1, fairnessCurrentB11, 0, "B11a"),
            (fairnessRootB, fairnessInitialB1, fairnessCurrentB11, 0, "B11b"),
        ];
        static string[] FairOrder(
            IEnumerable<(StateFingerprint Root, StateFingerprint Initial,
                StateFingerprint Current, int Priority, string Id)> tokens,
            IReadOnlyDictionary<StateFingerprint, int>? rootCounts = null,
            IReadOnlyDictionary<StateFingerprint, int>? initialCounts = null,
            IReadOnlyDictionary<StateFingerprint, int>? currentCounts = null)
            => OrderOrderedMutationHierarchy(
                    tokens,
                    token => token.Root,
                    token => token.Initial,
                    token => token.Current,
                    key => rootCounts?.GetValueOrDefault(key) ?? 0,
                    key => initialCounts?.GetValueOrDefault(key) ?? 0,
                    key => currentCounts?.GetValueOrDefault(key) ?? 0,
                    token => token.Priority,
                    current => current.OrderBy(token => token.Id, StringComparer.Ordinal).ToList())
                .Select(token => token.Id)
                .ToArray();

        string[] expectedFairOrder = ["A11a", "B11a", "A21a", "B11b", "A12a", "A11b"];
        if (!FairOrder(fairnessTokens).SequenceEqual(expectedFairOrder)
            || !FairOrder(fairnessTokens.Reverse()).SequenceEqual(expectedFairOrder))
        {
            throw new InvalidOperationException(
                "有序变异 Root→Initial→Current round-robin 不稳定或层级失效。");
        }
        Dictionary<StateFingerprint, int> fairnessRootCounts = new()
        {
            [fairnessRootA] = 1,
            [fairnessRootB] = 0,
        };
        Dictionary<StateFingerprint, int> fairnessInitialCounts = new()
        {
            [fairnessInitialA1] = 1,
            [fairnessInitialA2] = 0,
        };
        Dictionary<StateFingerprint, int> fairnessCurrentCounts = new()
        {
            [fairnessCurrentA11] = 1,
            [fairnessCurrentA12] = 0,
        };
        string[] countAwareOrder = FairOrder(
            fairnessTokens,
            fairnessRootCounts,
            fairnessInitialCounts,
            fairnessCurrentCounts);
        if (countAwareOrder[0] != "B11a"
            || Array.IndexOf(countAwareOrder, "A21a")
                > Array.IndexOf(countAwareOrder, "A12a")
            || Array.IndexOf(countAwareOrder, "A12a")
                > Array.IndexOf(countAwareOrder, "A11a")
            || !FairOrder(
                    fairnessTokens.Reverse(),
                    fairnessRootCounts,
                    fairnessInitialCounts,
                    fairnessCurrentCounts)
                .SequenceEqual(countAwareOrder))
        {
            throw new InvalidOperationException(
                "有序变异公平调度没有优先续搜较少消费的 root/initial/current lane。");
        }

        (int Quality, long Semantic, string Id)[] pacingTokens =
        [
            (0, 10, "quality"),
            (1, 25, "near"),
            (2, 100, "far"),
            (3, -81, "far-negative"),
        ];
        static string[] Pace(
            IEnumerable<(int Quality, long Semantic, string Id)> tokens,
            string? incompatibleExplorer = null)
            => BeamRetentionPolicy.SelectDeterministicQualityAndExplorer(
                    tokens.ToList(),
                    (left, right) =>
                    {
                        int comparison = left.Quality.CompareTo(right.Quality);
                        return comparison != 0
                            ? comparison
                            : string.CompareOrdinal(left.Id, right.Id);
                    },
                    (left, right) => Math.Abs(left.Semantic - right.Semantic),
                    selection => selection.Count < 2
                        || selection[1].Id != incompatibleExplorer,
                    (left, right) => left.Id == right.Id)
                .Select(token => token.Id)
                .ToArray();

        string[] expectedPacing = ["quality", "far-negative"];
        if (!Pace(pacingTokens).SequenceEqual(expectedPacing)
            || !Pace(pacingTokens.Reverse()).SequenceEqual(expectedPacing))
        {
            throw new InvalidOperationException(
                "有序变异 late Initial 没有稳定选择质量首席和最大语义距离 explorer。");
        }
        string[] expectedFallbackPacing = ["quality", "far"];
        if (!Pace(pacingTokens, "far-negative").SequenceEqual(expectedFallbackPacing)
            || !Pace(pacingTokens.Reverse(), "far-negative")
                .SequenceEqual(expectedFallbackPacing))
        {
            throw new InvalidOperationException(
                "有序变异 late Initial explorer 没有跳过无法共同预留的候选。");
        }

        (int Parent, int Outcome, int Quality, long Semantic, string Id)[]
            rawHandoffCandidates = Enumerable.Range(1, 25)
                .Select(index => (
                    Parent: 1,
                    Outcome: index,
                    Quality: index,
                    Semantic: (long)index,
                    Id: $"p1-{index}"))
                .Append((
                    Parent: 1,
                    Outcome: 25,
                    Quality: 100,
                    Semantic: 25L,
                    Id: "p1-25-duplicate"))
                .Append((
                    Parent: 2,
                    Outcome: 1,
                    Quality: 0,
                    Semantic: 10L,
                    Id: "p2-near"))
                .Append((
                    Parent: 2,
                    Outcome: 2,
                    Quality: 1,
                    Semantic: 50L,
                    Id: "p2-far"))
                .ToArray();
        static string[] SelectHandoffCompanions(
            IEnumerable<(int Parent, int Outcome, int Quality,
                long Semantic, string Id)> candidates)
        {
            var anchors = new[]
            {
                (Parent: 1, Outcome: 0, Quality: 0, Semantic: 0L, Id: "a1"),
                (Parent: 2, Outcome: 0, Quality: 0, Semantic: 0L, Id: "a2"),
            };
            List<string> selected = [];
            foreach (var anchor in anchors)
            {
                if (BeamRetentionPolicy.TrySelectOrderedMutationSemanticCompanion(
                        anchor,
                        candidates.Where(candidate => candidate.Parent == anchor.Parent),
                        candidate => candidate.Outcome,
                        (left, right) => Math.Abs(left.Semantic - right.Semantic),
                        (left, right) =>
                        {
                            int comparison = left.Quality.CompareTo(right.Quality);
                            return comparison != 0
                                ? comparison
                                : string.CompareOrdinal(left.Id, right.Id);
                        },
                        out var companion))
                {
                    selected.Add(companion.Id);
                }
            }
            return selected.ToArray();
        }

        string[] expectedHandoffCompanions = ["p1-25", "p2-far"];
        string[] pacedHandoffCompanions = SelectHandoffCompanions(
            rawHandoffCandidates.Where(candidate =>
                candidate.Parent != 1 || candidate.Outcome <= 2));
        if (!SelectHandoffCompanions(rawHandoffCandidates)
                .SequenceEqual(expectedHandoffCompanions)
            || !SelectHandoffCompanions(rawHandoffCandidates.Reverse())
                .SequenceEqual(expectedHandoffCompanions)
            || SelectHandoffCompanions(rawHandoffCandidates).Length != 2
            || pacedHandoffCompanions[0] != "p1-2"
            || SelectHandoffCompanions(rawHandoffCandidates
                    .Where(candidate => candidate.Id != "p1-25"))
                .First() != "p1-25-duplicate")
        {
            throw new InvalidOperationException(
                "exact-parent handoff 没有稳定选择单个 companion packet representative，或提前采用了 paced 子集。");
        }

        var temporalAnchor = (
            Family: 1,
            Outcome: 0,
            Quality: 0,
            Semantic: 0L,
            Id: "anchor");
        var repeatedAnchor = (
            Family: 2,
            Outcome: 0,
            Quality: 0,
            Semantic: 0L,
            Id: "repeated-anchor");
        var temporalCandidates = new[]
        {
            (Family: 1, Outcome: 1, Quality: 2, Semantic: 1L, Id: "family-1"),
            (Family: 2, Outcome: 2, Quality: 1, Semantic: 100L, Id: "family-2"),
        };
        Dictionary<int, int> sourceAdmissions = [];
        string SelectCoverageCompanion(
            (int Family, int Outcome, int Quality, long Semantic, string Id) anchor,
            IEnumerable<(int Family, int Outcome, int Quality, long Semantic, string Id)>
                candidates) =>
            BeamRetentionPolicy.TrySelectOrderedMutationCoverageBalancedCompanion(
                anchor,
                candidates,
                candidate => candidate.Family,
                family => sourceAdmissions.GetValueOrDefault(family),
                candidate => candidate.Outcome,
                (left, right) => Math.Abs(left.Semantic - right.Semantic),
                (left, right) => left.Quality.CompareTo(right.Quality),
                out var selected)
                ? selected.Id
                : "missing";
        string firstCoverage = SelectCoverageCompanion(
            temporalAnchor,
            temporalCandidates);
        sourceAdmissions[2] = 2;
        string leastServedCoverage = SelectCoverageCompanion(
            repeatedAnchor,
            temporalCandidates);
        sourceAdmissions[1] = 2;
        string positiveTieCompletion = SelectCoverageCompanion(
            repeatedAnchor,
            temporalCandidates.Reverse());
        sourceAdmissions[1] = 0;
        sourceAdmissions[2] = 2;
        string[] admissionFallbackOrder =
            BeamRetentionPolicy.OrderOrderedMutationCoverageBalancedCompanions(
                    repeatedAnchor,
                    temporalCandidates,
                    candidate => candidate.Family,
                    family => sourceAdmissions.GetValueOrDefault(family),
                    candidate => candidate.Outcome,
                    (left, right) => Math.Abs(left.Semantic - right.Semantic),
                    (left, right) => left.Quality.CompareTo(right.Quality))
                .Select(candidate => candidate.Id)
                .ToArray();
        if (firstCoverage != "family-2"
            || leastServedCoverage != "family-1"
            || positiveTieCompletion != "family-2"
            || !admissionFallbackOrder.SequenceEqual(
                ["family-1", "family-2"]))
        {
            throw new InvalidOperationException(
                "exact-parent handoff 没有先覆盖 least-served source、在正向平局时补全 anchor family，或为无法 admission 的首选保留公平 fallback。");
        }


        if (BeamRetentionPolicy.OrderedMutationHandoffCohortAdmissionWidth(
                anchorAlreadySelected: true,
                companionCount: 0) != 0
            || BeamRetentionPolicy.OrderedMutationHandoffCohortAdmissionWidth(
                anchorAlreadySelected: true,
                companionCount: 2) != 2
            || BeamRetentionPolicy.OrderedMutationHandoffCohortAdmissionWidth(
                anchorAlreadySelected: false,
                companionCount: 2) != 3)
        {
            throw new InvalidOperationException(
                "exact-parent handoff cohort 没有按完整 packet 节点数计算零宽 anchor 或原子宽度。");
        }
        if (!BeamRetentionPolicy.CanAdmitOrderedMutationAlternatives(14, 2)
            || BeamRetentionPolicy.CanAdmitOrderedMutationAlternatives(15, 2)
            || !BeamRetentionPolicy.CanAdmitOrderedMutationAlternatives(15, 1)
            || BeamRetentionPolicy.CanAdmitOrderedMutationAlternatives(16, 1))
        {
            throw new InvalidOperationException(
                "exact-parent companion packet 没有按实际新增节点执行 Alternative=16 硬上限。");
        }
        if (!BeamRetentionPolicy.CanAdmitOrderedMutationHandoffs(31, 1)
            || BeamRetentionPolicy.CanAdmitOrderedMutationHandoffs(32, 1)
            || !BeamRetentionPolicy.CanAdmitOrderedMutationHandoffs(32, 0)
            || BeamRetentionPolicy.CanAdmitOrderedMutationHandoffs(-1, 1)
            || BeamRetentionPolicy.CanAdmitOrderedMutationHandoffs(0, -1))
        {
            throw new InvalidOperationException(
                "anchor-only fallback 绕过了 handoff reason 硬上限。");
        }
        if (!BeamRetentionPolicy.HasOrderedMutationLayerCapacity(46, 48, 2)
            || BeamRetentionPolicy.HasOrderedMutationLayerCapacity(47, 48, 2)
            || !BeamRetentionPolicy.HasOrderedMutationLayerCapacity(48, 48, 0)
            || BeamRetentionPolicy.HasOrderedMutationLayerCapacity(48, 48, 1)
            || !BeamRetentionPolicy.HasOrderedMutationLayerCapacity(
                31,
                MaximumOrderedMutationGenericClaimServiceAdmissions,
                1)
            || BeamRetentionPolicy.HasOrderedMutationLayerCapacity(
                32,
                MaximumOrderedMutationGenericClaimServiceAdmissions,
                1)
            || !BeamRetentionPolicy.HasOrderedMutationLayerCapacity(
                15,
                MaximumOrderedMutationPaidCohortServiceAdmissions,
                1)
            || BeamRetentionPolicy.HasOrderedMutationLayerCapacity(
                16,
                MaximumOrderedMutationPaidCohortServiceAdmissions,
                1))
        {
            throw new InvalidOperationException(
                "ordered-mutation 原子 packet 在剩余宽度不足时发生半包 admission。");
        }
        if (!BeamRetentionPolicy.CanAttemptOrderedMutationAdmissionWithinService(
                requestedWidth: 0,
                maximumAdmissionWidth: 0)
            || !BeamRetentionPolicy.CanAttemptOrderedMutationAdmissionWithinService(
                requestedWidth: 1,
                maximumAdmissionWidth: 1)
            || BeamRetentionPolicy.CanAttemptOrderedMutationAdmissionWithinService(
                requestedWidth: 2,
                maximumAdmissionWidth: 1)
            || BeamRetentionPolicy.CanAttemptOrderedMutationAdmissionWithinService(
                requestedWidth: -1,
                maximumAdmissionWidth: 1)
            || BeamRetentionPolicy.CanAttemptOrderedMutationAdmissionWithinService(
                requestedWidth: 0,
                maximumAdmissionWidth: -1))
        {
            throw new InvalidOperationException(
                "ordered-mutation fallback packet 绕过了当前 service 的实际剩余宽度。");
        }
        if (!BeamRetentionPolicy.CanAttemptOrderedMutationHandoffAnchor(
                admitted: 48,
                admissionLimit: 48,
                admittedHandoffs: MaximumOrderedMutationBoundaryHandoffAdmissions,
                requestedAnchorWidth: 0,
                maximumAdmissionWidth: 0)
            || BeamRetentionPolicy.CanAttemptOrderedMutationHandoffAnchor(
                admitted: 47,
                admissionLimit: 48,
                admittedHandoffs: MaximumOrderedMutationBoundaryHandoffAdmissions - 1,
                requestedAnchorWidth: 1,
                maximumAdmissionWidth: 0)
            || BeamRetentionPolicy.CanAttemptOrderedMutationHandoffAnchor(
                admitted: 48,
                admissionLimit: 48,
                admittedHandoffs: MaximumOrderedMutationBoundaryHandoffAdmissions - 1,
                requestedAnchorWidth: 1,
                maximumAdmissionWidth: 1)
            || BeamRetentionPolicy.CanAttemptOrderedMutationHandoffAnchor(
                admitted: 47,
                admissionLimit: 48,
                admittedHandoffs: MaximumOrderedMutationBoundaryHandoffAdmissions,
                requestedAnchorWidth: 1,
                maximumAdmissionWidth: 1))
        {
            throw new InvalidOperationException(
                "ordered-mutation handoff 没有在构造 companion 顺序前执行零宽安全的 anchor 硬上限预检。");
        }
        static (string[] Materialized, string[] Attempts) RunCompanionAttemptFixture(
            IReadOnlyList<string> companionPackets,
            bool allowAnchorOnlyHandoffFallback,
            string? successfulPacket)
        {
            List<string> materialized = [];
            List<string> attempts = [];
            int companionPacketCount = 0;
            foreach (string packet in companionPackets)
            {
                companionPacketCount++;
                materialized.Add(packet);
                attempts.Add(packet);
                if (string.Equals(packet, successfulPacket, StringComparison.Ordinal))
                    return (materialized.ToArray(), attempts.ToArray());
            }
            if (BeamRetentionPolicy.ShouldAppendOrderedMutationAnchorOnlyAttempt(
                    companionPacketCount,
                    allowAnchorOnlyHandoffFallback))
            {
                attempts.Add("anchor-only");
            }
            return (materialized.ToArray(), attempts.ToArray());
        }
        var firstPacketSucceeded = RunCompanionAttemptFixture(
            ["family-1", "family-2", "family-3"],
            allowAnchorOnlyHandoffFallback: true,
            successfulPacket: "family-1");
        var secondPacketSucceeded = RunCompanionAttemptFixture(
            ["family-1", "family-2", "family-3"],
            allowAnchorOnlyHandoffFallback: true,
            successfulPacket: "family-2");
        var protectedAllFailed = RunCompanionAttemptFixture(
            ["family-1", "family-2"],
            allowAnchorOnlyHandoffFallback: false,
            successfulPacket: null);
        var deferredAllFailed = RunCompanionAttemptFixture(
            ["family-1", "family-2"],
            allowAnchorOnlyHandoffFallback: true,
            successfulPacket: null);
        var protectedEmptyPacket = RunCompanionAttemptFixture(
            [],
            allowAnchorOnlyHandoffFallback: false,
            successfulPacket: null);
        if (!firstPacketSucceeded.Materialized.SequenceEqual(["family-1"])
            || !firstPacketSucceeded.Attempts.SequenceEqual(["family-1"])
            || !secondPacketSucceeded.Materialized.SequenceEqual(
                ["family-1", "family-2"])
            || !secondPacketSucceeded.Attempts.SequenceEqual(
                ["family-1", "family-2"])
            || !protectedAllFailed.Attempts.SequenceEqual(
                ["family-1", "family-2"])
            || !deferredAllFailed.Attempts.SequenceEqual(
                ["family-1", "family-2", "anchor-only"])
            || deferredAllFailed.Attempts.Count(attempt =>
                    attempt == "anchor-only") != 1
            || !protectedEmptyPacket.Attempts.SequenceEqual(["anchor-only"])
            || BeamRetentionPolicy.ShouldAppendOrderedMutationAnchorOnlyAttempt(
                companionPacketCount: -1,
                allowAnchorOnlyHandoffFallback: true))
        {
            throw new InvalidOperationException(
                "handoff companion 没有按公平顺序惰性构造、成功即停，或全局借额阶段没有只追加一次 anchor-only 尝试。");
        }
        int handoffAnchor = 1;
        int selectedCompanion = 2;
        int unselectedFallback = 3;
        HashSet<int> exclusiveHandoffClaimOwners = [handoffAnchor];
        int[] independentlyScheduledHandoffClaims =
            BeamRetentionPolicy.SelectOrderedMutationClaimsForSharedScheduling(
                    new[] { handoffAnchor, selectedCompanion, unselectedFallback },
                    candidate => candidate,
                    paidCandidates: new HashSet<int>(),
                    handoffAnchors: exclusiveHandoffClaimOwners)
                .ToArray();
        if (!independentlyScheduledHandoffClaims.SequenceEqual(
                [selectedCompanion, unselectedFallback]))
        {
            throw new InvalidOperationException(
                "未获选的 handoff fallback outcome 被错误吞并，无法复用其独立 claim。");
        }
        HashSet<int> selectedBySuccessfulCohort =
            [handoffAnchor, selectedCompanion];
        if (!BeamRetentionPolicy.SelectOrderedMutationClaimsForSharedScheduling(
                new[] { handoffAnchor, selectedCompanion, unselectedFallback },
                candidate => candidate,
                selectedBySuccessfulCohort,
                exclusiveHandoffClaimOwners)
                .SequenceEqual([unselectedFallback]))
        {
            throw new InvalidOperationException(
                "handoff cohort 没有只折叠实际获选 outcome，未选 fallback claim 被错误移除。");
        }
        HashSet<int> selectedAfterFirstFamilyRejected =
            [handoffAnchor, unselectedFallback];
        if (!BeamRetentionPolicy.SelectOrderedMutationClaimsForSharedScheduling(
                new[] { handoffAnchor, selectedCompanion, unselectedFallback },
                candidate => candidate,
                selectedAfterFirstFamilyRejected,
                exclusiveHandoffClaimOwners)
                .SequenceEqual([selectedCompanion]))
        {
            throw new InvalidOperationException(
                "首个 handoff family 未获选后，其独立 claim 被 fallback cohort 错误吞并。");
        }
        OrderedMutationHandoffSourceLedgerKey stagedSourceLedgerKey = new(
            new StateFingerprint(0xb080UL, 0xb081UL),
            new StateFingerprint(0xb082UL, 0xb083UL));
        Dictionary<int, OrderedMutationHandoffSourceLedgerKey> stagedSourceAdmissions = [];
        Dictionary<OrderedMutationHandoffSourceLedgerKey, int>
            provisionalSourceAdmissions = [];
        if (TryStageOrderedMutationHandoffSourceAdmission(
                stagedSourceAdmissions,
                provisionalSourceAdmissions,
                admitted: 1,
                sourceLedgerKey: stagedSourceLedgerKey,
                admissionPending: false,
                admissionCharged: false)
            || !TryStageOrderedMutationHandoffSourceAdmission(
                stagedSourceAdmissions,
                provisionalSourceAdmissions,
                admitted: 2,
                sourceLedgerKey: stagedSourceLedgerKey,
                admissionPending: true,
                admissionCharged: false)
            || TryStageOrderedMutationHandoffSourceAdmission(
                stagedSourceAdmissions,
                provisionalSourceAdmissions,
                admitted: 2,
                sourceLedgerKey: stagedSourceLedgerKey,
                admissionPending: true,
                admissionCharged: false)
            || TryStageOrderedMutationHandoffSourceAdmission(
                stagedSourceAdmissions,
                provisionalSourceAdmissions,
                admitted: 3,
                sourceLedgerKey: stagedSourceLedgerKey,
                admissionPending: true,
                admissionCharged: true)
            || stagedSourceAdmissions.Count != 1
            || provisionalSourceAdmissions.GetValueOrDefault(stagedSourceLedgerKey) != 1)
        {
            throw new InvalidOperationException(
                "同 prune 已选 companion 的 source provisional 重复推进，或 base-rank 零宽节点错误推进。");
        }
        (int Limit, int GenericClaims, int PaidCohorts)[] expectedServiceLimits =
        [
            (1, 0, 1),
            (2, 1, 1),
            (3, 2, 1),
            (47, 31, 16),
            (48, 32, 16),
        ];
        if (expectedServiceLimits.Any(expected =>
            BeamRetentionPolicy.OrderedMutationServiceLimits(expected.Limit)
                != (expected.GenericClaims, expected.PaidCohorts)))
        {
            throw new InvalidOperationException(
                "ordered-mutation 32/16 服务份额没有随实际 layer admission limit 缩放。");
        }
        OrderedMutationHandoffSourceLedgerKey sourceLedgerKey = new(
            new StateFingerprint(0xb100UL, 0xb101UL),
            new StateFingerprint(0xb102UL, 0xb103UL));
        Dictionary<int, OrderedMutationHandoffSourceLedgerKey> pendingSourceAdmissions =
            new()
            {
                [1] = sourceLedgerKey,
                [2] = sourceLedgerKey,
                [3] = sourceLedgerKey with
                {
                    RecurrenceSourceFamilyKey =
                        new StateFingerprint(0xb104UL, 0xb105UL),
                },
            };
        Dictionary<OrderedMutationHandoffSourceLedgerKey, int>
            committedSourceAdmissions = [];
        if (!TryCommitOrderedMutationHandoffSourceAdmission(
                pendingSourceAdmissions,
                committedSourceAdmissions,
                1)
            || TryCommitOrderedMutationHandoffSourceAdmission(
                pendingSourceAdmissions,
                committedSourceAdmissions,
                1)
            || !TryCommitOrderedMutationHandoffSourceAdmission(
                pendingSourceAdmissions,
                committedSourceAdmissions,
                2)
            || committedSourceAdmissions.GetValueOrDefault(sourceLedgerKey) != 2)
        {
            throw new InvalidOperationException(
                "handoff source coverage ledger 没有只在成功 admission 时幂等提交。");
        }
        pendingSourceAdmissions.Clear();
        if (committedSourceAdmissions.Count != 1)
        {
            throw new InvalidOperationException(
                "被拒绝的 handoff source 错误推进了 coverage ledger。");
        }
        var packetAnchor = (
            Outcome: 0,
            Quality: 0,
            Semantic: 0L,
            Id: "anchor");
        var boundedCompanionPacket = new[]
        {
            (Outcome: 25, Quality: 25, Semantic: 25L, Id: "semantic-winner"),
            (Outcome: 24, Quality: 24, Semantic: 24L, Id: "packet-sibling"),
            (Outcome: 24, Quality: 99, Semantic: 24L, Id: "duplicate-sibling"),
            (Outcome: 0, Quality: 1, Semantic: 1L, Id: "anchor-alias"),
        };
        static string[] KeepCompanionPacket(
            (int Outcome, int Quality, long Semantic, string Id) anchor,
            IEnumerable<(int Outcome, int Quality, long Semantic, string Id)> candidates)
            => BeamRetentionPolicy
                .SelectDistinctOrderedMutationCompanionPacketCandidates(
                    anchor,
                    candidates,
                    candidate => candidate.Outcome,
                    (left, right) =>
                    {
                        int comparison = left.Quality.CompareTo(right.Quality);
                        return comparison != 0
                            ? comparison
                            : string.CompareOrdinal(left.Id, right.Id);
                    })
                .Select(candidate => candidate.Id)
                .ToArray();
        string[] expectedPacketCandidates = ["packet-sibling", "semantic-winner"];
        if (!KeepCompanionPacket(packetAnchor, boundedCompanionPacket)
                .SequenceEqual(expectedPacketCandidates)
            || !KeepCompanionPacket(packetAnchor, boundedCompanionPacket.Reverse())
                .SequenceEqual(expectedPacketCandidates))
        {
            throw new InvalidOperationException(
                "exact-parent handoff 在选中 companion packet 后又丢弃其 bounded sibling，或保留了 selected outcome alias。");
        }

        int ordinaryLateThreshold = OrderedMutationInitialAdmissionLimit(firstLease)
            - OrderedMutationRetentionLease.MaximumProtectedAdmissions;
        int progressTailLateThreshold = OrderedMutationInitialAdmissionLimit(
                progressTailLease)
            - OrderedMutationRetentionLease.MaximumProtectedAdmissions;
        if (BeamRetentionPolicy.IsLateOrderedMutationInitial(
                ordinaryLateThreshold - 1,
                firstLease)
            || !BeamRetentionPolicy.IsLateOrderedMutationInitial(
                ordinaryLateThreshold,
                firstLease)
            || BeamRetentionPolicy.IsLateOrderedMutationInitial(
                progressTailLateThreshold - 1,
                progressTailLease)
            || !BeamRetentionPolicy.IsLateOrderedMutationInitial(
                progressTailLateThreshold,
                progressTailLease)
            || !BeamRetentionPolicy
                .ReusesOrderedMutationPacketListsWithoutLateOutcomesForTesting())
        {
            throw new InvalidOperationException(
                "ordered-mutation pacing 没有只在 Initial 真实尾部阈值启动，或无 late outcome 时没有复用原 packet lists。");
        }

        BeamRetentionPolicy.VerifyOrderedMutationKeyPolicyForTesting();
    }
}
