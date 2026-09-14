namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const string BlockPotionId = "BLOCK_POTION";

    private sealed record BlockPotionInsertion(
        SearchNode Node,
        RouteAnnotations Annotations,
        int HpSaved);

    /// <summary>
    /// Smart search first produces a complete potion-free route. When that exact route contains a
    /// turn which loses at least one ordinary potion's HP value, try the block potion immediately
    /// before the action which hands control to the enemies. This is one deterministic route replay,
    /// not another potion search layer.
    /// </summary>
    private BlockPotionInsertion? TryInsertBlockPotion(
        SearchNode original,
        RouteAnnotations originalAnnotations,
        SolverResultScope resultScope)
    {
        if (resultScope != SolverResultScope.SearchCompletion
            || !_forceAllPotionsDisabled
            || policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || !SolverInterimResultOrdering.IsCompleteVictory(
                original.ActionCount,
                original.Snapshot.AllEnemiesDead,
                original.Snapshot.PlayerDead,
                original.Snapshot.ProjectedPlayerHp)
            || original.Actions.Any(action => action.Kind == PlanActionKind.UsePotion))
        {
            return null;
        }

        SearchablePotionSlotSnapshot? blockPotion = root.SearchablePotions
            .Where(potion => string.Equals(potion.PotionId, BlockPotionId, StringComparison.Ordinal)
                && policy.PotionStrategy.AllowsExplicitUse(
                    potion.Slot,
                    potion.PotionId,
                    SolverPotionPolicy.Smart,
                    forceAllDisabled: false))
            .OrderBy(potion => potion.Slot)
            .Cast<SearchablePotionSlotSnapshot?>()
            .FirstOrDefault();
        if (blockPotion is not { } selectedPotion)
            return null;

        (int Turn, int HpLost)? target = originalAnnotations.HpLostByTurn
            .Where(item => item.Value >= SolverWeights.PotionMinimumHpSaved)
            .OrderBy(item => item.Key)
            .Select(item => ((int Turn, int HpLost)?)(item.Key, item.Value))
            .FirstOrDefault();
        if (target is not { } targetTurn)
            return null;

        PlanAction[] originalActions = original.Actions.ToArray();
        int insertionIndex = Array.FindIndex(originalActions, action =>
            action.Turn == targetTurn.Turn
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        if (insertionIndex < 0)
            return null;

        PlanAction potionAction = new(
            PlanActionKind.UsePotion,
            targetTurn.Turn,
            TargetIndex: -1,
            TargetCombatId: null,
            TargetName: string.Empty,
            PotionSlot: selectedPotion.Slot,
            PotionId: selectedPotion.PotionId,
            PotionTitle: displayNames.Potion(selectedPotion.PotionId));
        PlanAction[] insertedActions = new PlanAction[originalActions.Length + 1];
        Array.Copy(originalActions, 0, insertedActions, 0, insertionIndex);
        insertedActions[insertionIndex] = potionAction;
        Array.Copy(
            originalActions,
            insertionIndex,
            insertedActions,
            insertionIndex + 1,
            originalActions.Length - insertionIndex);

        SearchNode inserted = ReplayInsertedRoute(
            insertedActions,
            original.GetTurnSetupChoices(),
            original.GetTurnSetupPlayState(),
            originalAnnotations);
        RouteAnnotations insertedAnnotations = BuildRouteAnnotations(inserted);
        int hpSaved = original.Snapshot.CumulativePlayerHpLost
            - inserted.Snapshot.CumulativePlayerHpLost;
        bool accepted = SolverInterimResultOrdering.IsCompleteVictory(
                inserted.ActionCount,
                inserted.Snapshot.AllEnemiesDead,
                inserted.Snapshot.PlayerDead,
                inserted.Snapshot.ProjectedPlayerHp)
            && inserted.Snapshot.BoundaryReason != SearchBoundaryReason.UnsupportedEffect
            && inserted.PotionCount == original.PotionCount + 1
            && inserted.Snapshot.ProjectedDeathSaveUseCount
                <= original.Snapshot.ProjectedDeathSaveUseCount
            && hpSaved >= SolverWeights.PotionMinimumHpSaved;
        if (!accepted)
        {
            inserted.Snapshot.ReleaseSimulator();
            return null;
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] BLOCK_POTION_ROUTE_INSERTED " +
            $"turn={targetTurn.Turn} slot={selectedPotion.Slot} " +
            $"turn_hp_lost={targetTurn.HpLost} hp_saved={hpSaved} " +
            $"expanded_nodes_added=0");
        return new BlockPotionInsertion(
            inserted,
            insertedAnnotations,
            hpSaved);
    }

    private SearchNode ReplayInsertedRoute(
        IReadOnlyList<PlanAction> actions,
        IReadOnlyList<PlanCardChoice> turnSetupChoices,
        ContinuationStamp? turnSetupPlayState,
        RouteAnnotations originalAnnotations)
    {
        SimulationSnapshot rootSnapshot = _includeTurnSetup
            ? ReplayTurnSetup(turnSetupChoices)
            : Replay([]);
        SearchNode current = new(
            null,
            0,
            rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost,
            _startTurnNumber,
            SearchRouteTraits.None,
            0,
            rootSnapshot.Score,
            rootSnapshot.StateKey,
            rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason,
            rootSnapshot.PlayerDead
                || rootSnapshot.AllEnemiesDead
                || rootSnapshot.BoundaryReason != SearchBoundaryReason.None,
            null,
            rootSnapshot,
            CombatProgressState.Capture(rootSnapshot),
            TurnSetupChoices: turnSetupChoices,
            TurnSetupPlayState: turnSetupPlayState);

        try
        {
            foreach (PlanAction action in actions)
            {
                if (action.Turn != current.Turn)
                {
                    throw new InvalidOperationException(
                        $"格挡药插入路线的动作回合不连续：action_turn={action.Turn} " +
                        $"state_turn={current.Turn}。");
                }

                SearchNode parent = current;
                SimulationSnapshot snapshot = ReplayAction(parent, action);
                bool terminal = snapshot.PlayerDead
                    || snapshot.AllEnemiesDead
                    || snapshot.BoundaryReason != SearchBoundaryReason.None;
                bool turnBoundary = terminal || snapshot.Turn > parent.Turn;
                SearchRouteTraits traits = action.Kind == PlanActionKind.UsePotion
                    ? ClassifyPotionTraits(parent.Traits, parent.Snapshot, snapshot)
                    : turnBoundary
                        ? ClassifyRoundTransitionTraits(parent.Traits, parent.Snapshot, snapshot)
                        : parent.Traits;
                int futureSold = parent.FutureSoldHp;
                TurnOutcome? outcome = null;
                if (turnBoundary)
                {
                    SearchNode turnStart = FindTurnStart(parent);
                    int hpLost = Math.Max(
                        0,
                        snapshot.CumulativePlayerHpLost
                            - turnStart.Snapshot.CumulativePlayerHpLost);
                    int soldThisTurn = Math.Min(
                        hpLost,
                        originalAnnotations.SoldHpByTurn.GetValueOrDefault(action.Turn));
                    futureSold += soldThisTurn;
                    bool endedByTurn = action.Kind == PlanActionKind.EndTurn
                        || snapshot.Turn > parent.Turn;
                    int actualBlock = endedByTurn
                        ? parent.Snapshot.PlayerBlock
                        : snapshot.PlayerBlock;
                    int energyLeft = endedByTurn
                        ? parent.Snapshot.Energy
                        : snapshot.Energy;
                    outcome = new TurnOutcome(
                        action.Turn,
                        hpLost,
                        Math.Max(
                            0,
                            snapshot.RecoveredPlayerHp
                                - turnStart.Snapshot.RecoveredPlayerHp),
                        checked(AccumulateEnemyHpLost(parent, snapshot)
                            - turnStart.CumulativeEnemyHpLost),
                        soldThisTurn,
                        actualBlock,
                        actualBlock,
                        energyLeft);
                }

                current = new SearchNode(
                    action,
                    parent.ActionCount + 1,
                    snapshot.PotionUseCount,
                    snapshot.PotionStrategicCost,
                    snapshot.Turn,
                    traits,
                    futureSold,
                    ApplySoldHpPenalty(snapshot.Score, futureSold),
                    snapshot.StateKey,
                    snapshot.HasRisk,
                    snapshot.BoundaryReason,
                    terminal,
                    parent,
                    snapshot,
                    turnBoundary
                        ? parent.CombatProgress.Advance(snapshot)
                        : parent.CombatProgress,
                    Outcome: outcome)
                {
                    CumulativeEnemyHpLost = AccumulateEnemyHpLost(parent, snapshot),
                };
                parent.Snapshot.ReleaseSimulator();
            }
            return current;
        }
        catch
        {
            current.Snapshot.ReleaseSimulator();
            throw;
        }
    }
}
