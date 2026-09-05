namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // This transient, synchronous callback payload never crosses the Search boundary.
    // Its node lists are borrowed only until the callback returns; the sink receives copies.
    private readonly record struct GlobalRetentionDecision(
        IReadOnlyList<SearchNode> OrderedPool,
        IReadOnlyList<SearchNode> Required,
        IReadOnlyList<SearchNode> Routing,
        IReadOnlyList<SearchNode> Selected,
        int Limit,
        int EffectiveLimit,
        int? RoutingQuota,
        int RoutingLimit,
        IReadOnlyDictionary<SearchNode, RoutingChoiceSignature>? RoutingSignatures,
        IReadOnlySet<SearchNode>? OptionLeaders,
        Func<SearchNode, double> BeamRankScore);

    private void ObserveSearchPath(
        SearchNode node,
        SearchPathObservationStage stage,
        string reason,
        int boundaryId = 0)
    {
        SearchPathObserver? observer = policy.Diagnostics.PathObserver;
        if (observer == null || !observer.WantsState(node.StateKey))
            return;
        observer.Observe(CaptureSearchPathObservation(node, stage, reason, boundaryId));
    }

    private SearchPathObservation CaptureSearchPathObservation(
        SearchNode node,
        SearchPathObservationStage stage,
        string reason,
        int boundaryId)
    {
        // Do not use the cached Actions property: even a read would populate that cache.
        // Only an explicitly watched state pays for this detached path walk and deep copy.
        PlanAction[] actions = new PlanAction[node.ActionCount];
        IReadOnlyList<PlanCardChoice> rootChoices = [];
        bool foundRootChoices = false;
        int index = actions.Length;
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (cursor.Action is { } action)
            {
                if (index == 0)
                    throw new InvalidOperationException("诊断动作链长于节点动作计数。");
                actions[--index] = CopyObservedAction(action);
            }
            if (!foundRootChoices && cursor.TurnSetupChoices is { } choices)
            {
                rootChoices = choices;
                foundRootChoices = true;
            }
        }
        if (index != 0)
            throw new InvalidOperationException("诊断动作链短于节点动作计数。");

        return new SearchPathObservation(
            _run.PathDiagnosticsSolverId,
            _profile.Phase,
            _profile.BeamWidth,
            stage,
            reason,
            boundaryId,
            node.StateKey,
            node.Parent?.StateKey,
            node.Turn,
            node.ActionCount,
            ObservedPolicyLabel(node),
            node.Parent is { } parent ? ObservedPolicyLabel(parent) : null,
            node.Traits,
            node.Outcome is { } outcome ? outcome with { } : null,
            node.BoundaryReason,
            node.IsTerminal,
            node.HasPredictionRisk,
            node.Snapshot.PlayerHp,
            node.Snapshot.PlayerMaxHp,
            node.Snapshot.EnemyHp,
            node.Snapshot.ShufflesCrossed,
            node.CumulativeEnemyHpLost,
            Array.AsReadOnly(actions),
            CopyObservedChoices(rootChoices));
    }

    private static SearchPathPolicyLabel ObservedPolicyLabel(SearchNode node) => new(
        node.PotionCount,
        node.PotionStrategicCost,
        node.FutureSoldHp,
        node.Snapshot.CumulativePlayerHpLost,
        node.ActionCount,
        node.Score);

    private static PlanAction CopyObservedAction(PlanAction action) => action with
    {
        Choice = action.Choice is { } choice ? CopyObservedChoice(choice) : null,
        NestedChoices = action.NestedChoices is { } nested ? CopyObservedChoices(nested) : null,
        TurnStartChoices = action.TurnStartChoices is { } setup ? CopyObservedChoices(setup) : null,
        RelicEffects = action.RelicEffects is { } relics
            ? Array.AsReadOnly(relics.Select(effect => effect with { }).ToArray())
            : null,
    };

    private static PlanCardChoice CopyObservedChoice(PlanCardChoice choice) => choice with
    {
        Cards = Array.AsReadOnly(choice.Cards.Select(token => token with { }).ToArray()),
    };

    private static IReadOnlyList<PlanCardChoice> CopyObservedChoices(
        IReadOnlyList<PlanCardChoice> choices)
        => Array.AsReadOnly(choices.Select(CopyObservedChoice).ToArray());

    private Action<GlobalRetentionDecision>? CreateGlobalRetentionObserver(
        IReadOnlyList<SearchNode> pool,
        int boundaryId)
    {
        SearchPathObserver? observer = policy.Diagnostics.PathObserver;
        if (observer == null || !observer.ObservesRetentionPools)
            return null;
        bool matched = false;
        foreach (SearchNode node in pool)
        {
            if (!observer.WantsRetentionPool(node.StateKey))
                continue;
            matched = true;
            break;
        }
        if (!matched)
            return null;

        ObserveSearchPathRetentionPool(
            pool, SearchPathObservationStage.RetentionPoolInput, "outer_prune_pool", boundaryId);
        return CreateMatchedGlobalRetentionCallback(boundaryId);
    }

    // Keep the capturing lambda out of the null/miss path, so disabled observation does
    // not allocate a closure merely by entering the outer Prune boundary.
    private Action<GlobalRetentionDecision> CreateMatchedGlobalRetentionCallback(int boundaryId)
        => decision => ObserveGlobalRetentionDecision(decision, boundaryId);

    private void ObserveSearchPathRetentionPool(
        IReadOnlyList<SearchNode> nodes,
        SearchPathObservationStage stage,
        string reason,
        int boundaryId)
    {
        SearchPathObserver observer = policy.Diagnostics.PathObserver
            ?? throw new InvalidOperationException("整池路径诊断缺少显式观察器。");
        for (int index = 0; index < nodes.Count; index++)
        {
            SearchNode node = nodes[index];
            observer.Observe(CaptureSearchPathObservation(node, stage, reason, boundaryId) with
            {
                Retention = new SearchPathRetentionDetails(
                    PoolIndex: index,
                    ParentRetentionRank: node.Parent?.RetentionRank),
            });
        }
    }

    private void ObserveGlobalRetentionDecision(GlobalRetentionDecision decision, int boundaryId)
    {
        SearchPathObserver observer = policy.Diagnostics.PathObserver
            ?? throw new InvalidOperationException("全局保路诊断缺少显式观察器。");
        for (int index = 0; index < decision.OrderedPool.Count; index++)
        {
            SearchNode node = decision.OrderedPool[index];
            SimulationSnapshot snapshot = node.Snapshot;
            SearchPathRoutingChoiceSignature? routingSignature = null;
            if (decision.RoutingSignatures != null
                && decision.RoutingSignatures.TryGetValue(node, out RoutingChoiceSignature signature))
            {
                routingSignature = new SearchPathRoutingChoiceSignature(
                    signature.Turn, signature.SourceId, signature.Effect, signature.Pile.ToString(),
                    signature.CardId, signature.Upgrade, signature.CardStateKey, signature.Occurrence,
                    signature.ContextId, signature.StateContext, signature.EnemyCombatDistributionKey,
                    signature.EnemyControlDistributionKey, signature.UnorderedPileKey);
            }
            observer.Observe(CaptureSearchPathObservation(
                node, SearchPathObservationStage.GlobalRetention, "outer_rank_best", boundaryId) with
            {
                Retention = new SearchPathRetentionDetails(
                    RawRank: index,
                    RequiredIndex: ObservedReferenceIndex(decision.Required, node),
                    RoutingIndex: ObservedReferenceIndex(decision.Routing, node),
                    SelectedIndex: ObservedReferenceIndex(decision.Selected, node),
                    BeamRankScore: decision.BeamRankScore(node),
                    OffensiveProgressValue: node.Snapshot.OffensiveProgressValue,
                    ParentRetentionRank: node.Parent?.RetentionRank,
                    Limit: decision.Limit,
                    EffectiveLimit: decision.EffectiveLimit,
                    RoutingQuota: decision.RoutingQuota,
                    RoutingLimit: decision.RoutingLimit,
                    RawCount: decision.OrderedPool.Count,
                    RequiredCount: decision.Required.Count,
                    RoutingCount: decision.Routing.Count,
                    SelectedCount: decision.Selected.Count,
                    RoutingChoiceSignature: routingSignature,
                    IsRoutingOptionLeader: decision.OptionLeaders?.Contains(node),
                    Evaluation: new SearchPathEvaluationValues(
                        snapshot.Energy, snapshot.Stars, snapshot.PlayerBlock,
                        snapshot.ProjectedPlayerHp, snapshot.HandCount, snapshot.ReachableHandValue,
                        snapshot.ZeroCostPlayableCount, snapshot.LiveDeckSize, snapshot.LiveDeckClutter,
                        snapshot.PersistentBuffValue, snapshot.StrategicEffects.RetentionValue,
                        snapshot.LatentSetupValue, snapshot.RetainedAttackValue, snapshot.ReplayPotentialValue,
                        snapshot.FutureResourceValue, snapshot.DelayedDamageValue, snapshot.ReactiveDamageValue,
                        snapshot.EnemyStrengthSuppression, snapshot.EnemyWeakTurns, snapshot.EnemyVulnerableTurns,
                        snapshot.SandpitRemaining, snapshot.FocusTargetPressure,
                        snapshot.ProjectedShuffleOrderValue, snapshot.LongTermResourceValue)),
            });
        }
    }

    private static int? ObservedReferenceIndex(IReadOnlyList<SearchNode> nodes, SearchNode candidate)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            if (ReferenceEquals(nodes[index], candidate))
                return index;
        }
        return null;
    }

    private int ObserveSearchPathBoundaryInput(
        IReadOnlyList<SearchNode> nodes,
        SearchPathObservationStage stage,
        string reason)
    {
        if (policy.Diagnostics.PathObserver == null)
            return 0;
        int boundaryId = ++_run.PathDiagnosticsBoundaryId;
        ObserveSearchPathBoundary(nodes, stage, reason, boundaryId);
        return boundaryId;
    }

    private void ObserveSearchPathBoundary(
        IReadOnlyList<SearchNode> nodes,
        SearchPathObservationStage stage,
        string reason,
        int boundaryId)
    {
        if (policy.Diagnostics.PathObserver == null)
            return;
        foreach (SearchNode node in nodes)
            ObserveSearchPath(node, stage, reason, boundaryId);
    }

    private void ObserveSearchPathTurnSelection(
        IReadOnlyList<SearchNode> before,
        IReadOnlyList<SearchNode> after,
        int boundaryId)
    {
        SearchPathObserver? observer = policy.Diagnostics.PathObserver;
        if (observer == null)
            return;
        List<SearchPathObservation>? retained = null;
        foreach (SearchNode node in after)
        {
            if (!observer.WantsState(node.StateKey))
                continue;
            SearchPathObservation observation = CaptureSearchPathObservation(
                node, SearchPathObservationStage.TurnAnnotated, "turn_outcome_annotation", boundaryId);
            retained ??= [];
            retained.Add(observation);
            observer.Observe(observation);
        }
        foreach (SearchNode node in before)
        {
            if (!observer.WantsState(node.StateKey))
                continue;
            SearchPathObservation observation = CaptureSearchPathObservation(
                node, SearchPathObservationStage.TurnDropped, "turn_outcome_filter", boundaryId);
            // Annotation produces `with` clones and deliberately changes FutureSoldHp,
            // Score, Traits and Outcome. Match their original policy history and exact
            // action/choice route, not reference identity or the post-annotation label.
            if (retained == null || !retained.Any(item => SameObservedTurnRoute(observation, item)))
                observer.Observe(observation);
        }
    }

    private static bool SameObservedTurnRoute(SearchPathObservation before, SearchPathObservation after)
    {
        if (before.StateKey != after.StateKey || before.ParentStateKey != after.ParentStateKey
            || before.Turn != after.Turn || before.ActionCount != after.ActionCount
            || before.ParentPolicyLabel != after.ParentPolicyLabel
            || before.PotionCount != after.PotionCount
            || before.PotionStrategicCost != after.PotionStrategicCost
            || before.CumulativePlayerHpLost != after.CumulativePlayerHpLost
            || before.Actions.Count != after.Actions.Count
            || !SameObservedChoices(before.RootTurnSetupChoices, after.RootTurnSetupChoices))
            return false;
        for (int index = 0; index < before.Actions.Count; index++)
        {
            PlanAction left = before.Actions[index];
            PlanAction right = after.Actions[index];
            if (left.Kind != right.Kind || left.Turn != right.Turn
                || left.CardId != right.CardId || left.CardOccurrence != right.CardOccurrence
                || left.CardStateKey != right.CardStateKey || left.CardStateOccurrence != right.CardStateOccurrence
                || left.TargetIndex != right.TargetIndex || left.TargetCombatId != right.TargetCombatId
                || left.PotionId != right.PotionId || left.PotionSlot != right.PotionSlot
                || left.ReplayCount != right.ReplayCount || left.EndsPlayerTurn != right.EndsPlayerTurn
                || left.NestedChoicesBeforePrimary != right.NestedChoicesBeforePrimary
                || !SameObservedChoice(left.Choice, right.Choice)
                || !SameObservedChoices(left.NestedChoices, right.NestedChoices)
                || !SameObservedChoices(left.TurnStartChoices, right.TurnStartChoices))
                return false;
        }
        return true;
    }

    private static bool SameObservedChoices(
        IReadOnlyList<PlanCardChoice>? left,
        IReadOnlyList<PlanCardChoice>? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!SameObservedChoice(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static bool SameObservedChoice(PlanCardChoice? left, PlanCardChoice? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        if (left.Effect != right.Effect || left.SourcePile != right.SourcePile
            || left.SourceId != right.SourceId || left.ContextId != right.ContextId
            || left.Timing != right.Timing || left.Cards.Count != right.Cards.Count)
            return false;
        for (int index = 0; index < left.Cards.Count; index++)
        {
            PlanCardToken a = left.Cards[index];
            PlanCardToken b = right.Cards[index];
            if (a.CardId != b.CardId || a.UpgradeLevel != b.UpgradeLevel
                || a.StateKey != b.StateKey || a.SourceOccurrence != b.SourceOccurrence
                || a.OptionOccurrence != b.OptionOccurrence)
                return false;
        }
        return true;
    }
}
