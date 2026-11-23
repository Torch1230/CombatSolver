using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rooms;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal static class InCombatRouteTracer
{
    public static InCombatBeamResult BuildResult(
        CombatState root,
        ForecastPassOutcome selectedPass,
        IReadOnlyList<EvaluatedForecastCandidate> allEvaluatedCandidates,
        long totalExpandedNodes,
        int priorBattleHpLost,
        double searchElapsedMilliseconds,
        CancellationToken traceCancellation = default)
    {
        if (allEvaluatedCandidates.Count == 0)
            throw new InvalidOperationException("In-combat beam search produced no evaluated candidates.");

        SearchNode selected = selectedPass.Selected;
        IReadOnlyList<PlannedAction> route = selected.Route();
        bool onlyDeathRoutes = allEvaluatedCandidates.All(candidate =>
            !candidate.Node.State.Players[0].Creature.IsAlive ||
            candidate.Node.State.Players[0].Creature.CurrentHp <= 0 ||
            candidate.ProjectedPlayerHp <= 0);

        EvaluatedForecastCandidate selectedCandidate = allEvaluatedCandidates
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Node, selected));
        ForecastSearchBoundary boundary;
        if (selected.Won)
        {
            boundary = ForecastSearchBoundary.None;
        }
        else if (selectedCandidate.Node is not null && selectedCandidate.NodeBoundary != ForecastSearchBoundary.None)
        {
            boundary = selectedCandidate.NodeBoundary;
        }
        else if (selectedPass.PassBoundary != ForecastSearchBoundary.None)
        {
            boundary = selectedPass.PassBoundary;
        }
        else if (!selected.Ended)
        {
            boundary = ForecastSearchBoundary.NodeLimit;
        }
        else
        {
            boundary = ForecastSearchBoundary.None;
        }

        return TraceRoute(
            root,
            route,
            selected,
            selectedCandidate.Node is not null ? selectedCandidate.ProjectedPlayerHp : selected.State.Players[0].Creature.CurrentHp,
            onlyDeathRoutes,
            boundary,
            totalExpandedNodes,
            priorBattleHpLost,
            searchElapsedMilliseconds,
            selectedPass.Evaluator,
            traceCancellation);
    }

    public static InCombatBeamResult TraceRoute(
        CombatState root,
        IReadOnlyList<PlannedAction> route,
        SearchNode selectedNode,
        int projectedPlayerHp,
        bool onlyDeathRoutes,
        ForecastSearchBoundary boundary,
        long totalExpandedNodes,
        int priorBattleHpLost,
        double searchElapsedMilliseconds,
        StateEvaluator evaluator,
        CancellationToken traceCancellation = default)
    {
        CombatState sim = root.Clone();
        Player player = sim.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        int rootInitialCumulativeHpLost = player.Creature.CumulativeHpLost;
        int rootTurn = pcs.TurnNumber;

        var ledger = new PredictionLedgerSink();
        sim.PredictionSink = ledger;

        var flashedThisAction = new List<string>();
        void OnRelicFlashed(RelicModel relic, IEnumerable<Creature> _)
        {
            if (!flashedThisAction.Contains(relic.Id.Entry, StringComparer.Ordinal))
                flashedThisAction.Add(relic.Id.Entry);
        }
        foreach (RelicModel relic in player.Relics)
            relic.Flashed += OnRelicFlashed;

        var tracedActions = new List<InCombatActionTrace>(route.Count);
        var potionUses = new List<ForecastPotionUse>();
        var turnAccumulators = new Dictionary<int, TurnAccumulator>();

        int currentTurn = pcs.TurnNumber;
        EnsureTurnAccumulator(currentTurn);

        try
        {
            for (int actionIndex = 0; actionIndex < route.Count; actionIndex++)
            {
                traceCancellation.ThrowIfCancellationRequested();
                PlannedAction action = route[actionIndex];
                if (sim.Engine is not { IsInProgress: true })
                    throw new InvalidOperationException("In-combat route trace encountered action after combat ended.");

                flashedThisAction.Clear();
                int roundBeforeAction = sim.RoundNumber;
                int hpLostBeforeAction = player.Creature.CumulativeHpLost;
                int hpBeforeAction = player.Creature.CurrentHp;
                int rawEnemyHpBeforeAction = SumEnemyHp(sim);
                int blockBeforeAction = player.Creature.Block;
                int energyBeforeAction = pcs.Energy;
                Dictionary<uint, bool> aliveBefore = CaptureAliveEnemies(sim);

                int cardUpgradeLevel = 0;
                string cardEnchantmentId = string.Empty;
                uint? targetCombatId = null;

                if (action.Kind == "PlayCard")
                {
                    CardModel card = pcs.Hand.Cards
                        .Where(candidate => candidate.Id.Entry == action.CardId)
                        .Skip(action.CardOccurrence)
                        .FirstOrDefault()
                        ?? throw new InvalidOperationException($"Trace could not find hand card '{action.CardId}'#{action.CardOccurrence}.");
                    cardUpgradeLevel = card.CurrentUpgradeLevel;
                    cardEnchantmentId = card.Enchantments.FirstOrDefault()?.Id.Entry ?? string.Empty;
                    targetCombatId = ResolveTargetCombatId(sim, card.TargetType, action.TargetIndex);
                }
                else if (action.Kind == "UsePotion")
                {
                    PotionModel potion = player.PotionSlots[action.PotionSlot]
                        ?? throw new InvalidOperationException($"Trace could not find potion at slot {action.PotionSlot}.");
                    targetCombatId = ResolveTargetCombatId(sim, potion.TargetType, action.TargetIndex);
                    potionUses.Add(new ForecastPotionUse(potion.Id.Entry, Math.Max(1, action.Turn), action.PotionSlot));

                    TurnAccumulator useAcc = EnsureTurnAccumulator(action.Turn);
                    useAcc.PotionCount++;
                    useAcc.PotionStrategicCost += CombatSolver.Beam.Policy.PotionRewardOutlook.StrategicHpCost(potion);
                }

                var actionChoices = new List<InCombatChoiceTrace>();
                var tracingSource = new TracingSelectionSource(
                    new SolverSelectionSource(action.ChoiceIndices),
                    sim,
                    pcs,
                    action,
                    roundBeforeAction,
                    actionChoices);
                sim.CardSelectionSource = tracingSource;

                IReadOnlyList<string> observed = ExecuteSynchronously(sim, action, tracingSource);
                if (!observed.SequenceEqual(action.ChoiceCardIds, StringComparer.Ordinal))
                    throw new InvalidOperationException("In-combat route trace card choices differ from search prediction.");

                var killedIds = new List<uint>();
                foreach ((uint enemyId, bool wasAlive) in aliveBefore)
                {
                    if (!wasAlive)
                        continue;
                    Creature? afterCreature = sim.Enemies.FirstOrDefault(e => e.CombatId == enemyId);
                    if (afterCreature is null || afterCreature.IsDead)
                        killedIds.Add(enemyId);
                }

                tracedActions.Add(new InCombatActionTrace(
                    Turn: action.Turn,
                    Kind: action.Kind,
                    CardId: action.CardId ?? string.Empty,
                    CardOccurrence: action.CardOccurrence,
                    CardUpgradeLevel: cardUpgradeLevel,
                    CardEnchantmentId: cardEnchantmentId,
                    TargetIndex: action.TargetIndex,
                    TargetCombatId: targetCombatId,
                    PotionId: action.PotionId ?? string.Empty,
                    PotionSlot: action.PotionSlot,
                    Choices: actionChoices,
                    FlashedRelicIds: flashedThisAction.ToArray(),
                    KilledEnemyCombatIds: killedIds));

                TurnAccumulator acc = EnsureTurnAccumulator(action.Turn);
                int hpLossDelta = Math.Max(0, player.Creature.CumulativeHpLost - hpLostBeforeAction);
                int hpRecoveredDelta = Math.Max(0, player.Creature.CurrentHp - hpBeforeAction + hpLossDelta);
                int enemyHpLossDelta = Math.Max(0, rawEnemyHpBeforeAction - SumEnemyHp(sim));
                acc.HpLost += hpLossDelta;
                acc.HpRecovered += hpRecoveredDelta;
                acc.EnemyHpLost += enemyHpLossDelta;

                bool turnAdvanced = action.Kind == "EndTurn" || pcs.TurnNumber > action.Turn;
                if (turnAdvanced)
                {
                    acc.ActualBlock = blockBeforeAction;
                    acc.EnergyLeft = energyBeforeAction;
                    acc.MaxBlock = Math.Max(acc.MaxBlock, blockBeforeAction);
                }
                else
                {
                    acc.ActualBlock = player.Creature.Block;
                    acc.EnergyLeft = pcs.Energy;
                    acc.MaxBlock = Math.Max(acc.MaxBlock, player.Creature.Block);
                }

                if (sim.Engine is { IsInProgress: false })
                {
                    acc.CombatEnded = sim.Engine.Won;
                }
                else if (turnAdvanced)
                {
                    currentTurn = pcs.TurnNumber;
                    EnsureTurnAccumulator(currentTurn);
                }
            }
        }
        finally
        {
            foreach (RelicModel relic in player.Relics)
                relic.Flashed -= OnRelicFlashed;
        }

        // Remove empty trailing turn accumulator created after the last EndTurn if no actions occurred in it and combat isn't in that turn with actions
        int lastTurnWithActions = tracedActions.Count > 0 ? tracedActions[^1].Turn : rootTurn;
        var turnOutcomes = turnAccumulators
            .Where(pair => pair.Key <= lastTurnWithActions)
            .OrderBy(pair => pair.Key)
            .Select(pair => new InCombatTurnOutcomeTrace(
                Turn: pair.Key,
                HpLost: pair.Value.HpLost,
                HpRecovered: pair.Value.HpRecovered,
                EnemyHpLost: pair.Value.EnemyHpLost,
                SoldHp: 0,
                MaxBlock: pair.Value.MaxBlock,
                ActualBlock: pair.Value.ActualBlock,
                EnergyLeft: pair.Value.EnergyLeft,
                PotionCount: pair.Value.PotionCount,
                PotionStrategicCost: pair.Value.PotionStrategicCost,
                CombatEnded: pair.Value.CombatEnded))
            .ToArray();

        CombatState finalState = selectedNode.State;
        Player finalPlayer = finalState.Players[0];
        PlayerCombatState finalPcs = finalPlayer.PlayerCombatState!;
        int postRootHpLoss = Math.Max(0, finalPlayer.Creature.CumulativeHpLost - rootInitialCumulativeHpLost);
        int postRootRecovered = Math.Max(0, finalPlayer.Creature.CurrentHp - root.Players[0].Creature.CurrentHp + postRootHpLoss);
        int projectedBattleHpLost = checked(priorBattleHpLost + postRootHpLoss);
        int? combatEndedTurn = selectedNode.Won ? Math.Max(1, selectedNode.Turn) : null;
        int? deathTurn = finalPlayer.Creature.IsDead ? Math.Max(1, selectedNode.Turn) : null;
        int postCombatRelicHeal = selectedNode.Won ? ComputePostCombatRelicHeal(finalPlayer) : 0;

        ForecastConfidence confidence = onlyDeathRoutes
            ? ForecastConfidence.DeathOnly
            : combatEndedTurn.HasValue
                ? ForecastConfidence.Complete
                : ForecastConfidence.Bounded;

        var terminal = new InCombatTerminalSnapshot(
            PlayerDead: finalPlayer.Creature.IsDead,
            AllEnemiesDead: !finalState.Enemies.Any(e => e.IsAlive && e.IsPrimaryEnemy) && selectedNode.Won,
            PlayerHp: finalPlayer.Creature.CurrentHp,
            PlayerMaxHp: finalPlayer.Creature.MaxHp,
            CumulativePlayerHpLost: finalPlayer.Creature.CumulativeHpLost,
            RecoveredPlayerHp: postRootRecovered,
            ProjectedPlayerHp: projectedPlayerHp,
            PlayerBlock: finalPlayer.Creature.Block,
            EnemyHp: SumEnemyHp(finalState),
            AliveEnemyCount: finalState.Enemies.Count(e => e.IsAlive),
            Energy: finalPcs.Energy,
            Stars: finalPcs.Stars,
            HandCount: finalPcs.Hand.Cards.Count,
            OutstandingStolenResource: evaluator.OutstandingTheft(selectedNode),
            Turn: selectedNode.Turn,
            ShufflesCrossed: Math.Max(0, sim.RunState.Rng.Shuffle.Counter - root.RunState.Rng.Shuffle.Counter),
            PostCombatRelicHeal: postCombatRelicHeal,
            ProjectedBattleHpLost: projectedBattleHpLost,
            CombatEndedTurn: combatEndedTurn,
            DeathTurn: deathTurn,
            OnlyDeathRoutes: onlyDeathRoutes,
            Confidence: confidence,
            SearchBoundary: boundary,
            HasRisk: !selectedNode.Ended && boundary != ForecastSearchBoundary.None);

        return new InCombatBeamResult(
            Actions: tracedActions,
            TurnOutcomes: turnOutcomes,
            Terminal: terminal,
            PotionUses: potionUses,
            ExpandedNodes: totalExpandedNodes,
            SearchElapsedMilliseconds: searchElapsedMilliseconds);

        TurnAccumulator EnsureTurnAccumulator(int turn)
        {
            if (!turnAccumulators.TryGetValue(turn, out TurnAccumulator? acc))
            {
                acc = new TurnAccumulator
                {
                    MaxBlock = player.Creature.Block,
                    ActualBlock = player.Creature.Block,
                    EnergyLeft = pcs.Energy,
                };
                turnAccumulators.Add(turn, acc);
            }
            return acc;
        }
    }

    private static IReadOnlyList<string> ExecuteSynchronously(
        CombatState sim,
        PlannedAction action,
        TracingSelectionSource tracingSource)
    {
        Player player = sim.Players[0];
        if ((player.PlayerCombatState?.TurnNumber ?? 0) != action.Turn)
            throw new InvalidOperationException("Trace route turn does not match Core state.");

        switch (action.Kind)
        {
            case "PlayCard":
            {
                CardModel card = player.PlayerCombatState!.Hand.Cards
                    .Where(candidate => candidate.Id.Entry == action.CardId)
                    .Skip(action.CardOccurrence)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException($"Trace card {action.CardId}#{action.CardOccurrence} is unavailable.");
                Creature? target = card.TargetType == TargetType.Self
                    ? null
                    : ActionExecutor.ResolveTarget(sim, player, card.TargetType, action.TargetIndex);
                if (sim.Engine!.PlayCardWithResultAsync(player, card, target).GetAwaiter().GetResult() is null)
                    throw new InvalidOperationException($"Trace card play rejected for {action.CardId}.");
                break;
            }
            case "UsePotion":
            {
                PotionModel potion = player.PotionSlots[action.PotionSlot]
                    ?? throw new InvalidOperationException($"Trace potion slot {action.PotionSlot} is empty.");
                Creature? target = ActionExecutor.ResolveTarget(sim, player, potion.TargetType, action.TargetIndex);
                PotionCmd.Use(potion, player, target).GetAwaiter().GetResult();
                break;
            }
            case "EndTurn":
                sim.Engine!.EndPlayerTurnAsync().GetAwaiter().GetResult();
                break;
            default:
                throw new InvalidOperationException($"Unknown trace action {action.Kind}.");
        }
        sim.Engine!.CheckWinCondition();
        if (tracingSource.Inner.ChoicesConsumed != action.ChoiceIndices.Count)
            throw new InvalidOperationException("Trace route has unconsumed card selections.");
        return tracingSource.ChosenIds;
    }

    private static uint? ResolveTargetCombatId(CombatState state, TargetType targetType, int targetIndex)
    {
        if (targetType is TargetType.AnyEnemy or TargetType.Osty && targetIndex >= 0 && targetIndex < state.Enemies.Count)
            return state.Enemies[targetIndex].CombatId;
        if (targetType == TargetType.AnyAlly && targetIndex <= -2 && -2 - targetIndex < state.Allies.Count)
            return state.Allies[-2 - targetIndex].CombatId;
        return null;
    }

    private static int SumEnemyHp(CombatState state) =>
        state.Enemies.Where(enemy => enemy.IsAlive).Sum(enemy => enemy.CurrentHp);

    private static Dictionary<uint, bool> CaptureAliveEnemies(CombatState state)
    {
        var map = new Dictionary<uint, bool>(state.Enemies.Count);
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.CombatId is uint id)
                map[id] = enemy.IsAlive;
        }
        return map;
    }

    private static int ComputePostCombatRelicHeal(Player player)
    {
        if (player.Creature.IsDead)
            return 0;
        int heal = 0;
        bool hasMeat = false;
        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted) continue;
            if (relic is BurningBlood) heal += 6;
            else if (relic is BlackBlood) heal += 12;
            else if (relic is MeatOnTheBone) hasMeat = true;
        }
        if (hasMeat && player.Creature.CurrentHp * 2 <= player.Creature.MaxHp)
            heal += 12;
        return Math.Min(heal, Math.Max(0, player.Creature.MaxHp - player.Creature.CurrentHp));
    }

    private sealed class TurnAccumulator
    {
        public int HpLost { get; set; }
        public int HpRecovered { get; set; }
        public int EnemyHpLost { get; set; }
        public int MaxBlock { get; set; }
        public int ActualBlock { get; set; }
        public int EnergyLeft { get; set; }
        public int PotionCount { get; set; }
        public int PotionStrategicCost { get; set; }
        public bool CombatEnded { get; set; }
    }

    private sealed class TracingSelectionSource(
        SolverSelectionSource inner,
        CombatState sim,
        PlayerCombatState pcs,
        PlannedAction action,
        int roundBeforeAction,
        List<InCombatChoiceTrace> destination) : ICardSelectionDecisionSource
    {
        public SolverSelectionSource Inner { get; } = inner;
        public List<string> ChosenIds { get; } = [];

        public async Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            string timing = sim.CurrentSide == CombatSide.Enemy
                ? "EnemyTurn"
                : (pcs.TurnNumber > action.Turn || sim.RoundNumber > roundBeforeAction)
                    ? "PlayerTurnStart"
                    : action.Kind == "EndTurn"
                        ? "PlayerTurnEnd"
                        : "Action";

            (string sourcePile, CardPile? matchingPile) = IdentifySourcePile(request.Candidates, pcs);
            IReadOnlyList<CardModel> selected = await Inner.ChooseCardsAsync(request).ConfigureAwait(false);
            var tokens = new List<InCombatChoiceCardToken>(selected.Count);
            foreach (CardModel card in selected)
            {
                ChosenIds.Add(card.Id.Entry);
                int optOcc = CountPrecedingMatching(request.Candidates, card);
                int srcOcc = matchingPile is not null
                    ? CountPrecedingMatching(matchingPile.Cards, card)
                    : -1;
                tokens.Add(new InCombatChoiceCardToken(
                    card.Id.Entry,
                    card.CurrentUpgradeLevel,
                    optOcc,
                    srcOcc));
            }

            destination.Add(new InCombatChoiceTrace(
                Timing: timing,
                SourceId: request.Source?.Id.Entry ?? string.Empty,
                SourcePile: sourcePile,
                Cards: tokens));
            return selected;
        }

        private static (string Name, CardPile? Pile) IdentifySourcePile(
            IReadOnlyList<CardModel> candidates,
            PlayerCombatState pcs)
        {
            if (candidates.Count == 0)
                return ("None", null);
            if (AllInPile(candidates, pcs.Hand))
                return ("Hand", pcs.Hand);
            if (AllInPile(candidates, pcs.DrawPile))
                return ("Draw", pcs.DrawPile);
            if (AllInPile(candidates, pcs.DiscardPile))
                return ("Discard", pcs.DiscardPile);
            if (AllInPile(candidates, pcs.ExhaustPile))
                return ("Exhaust", pcs.ExhaustPile);
            return ("None", null);
        }

        private static bool AllInPile(IReadOnlyList<CardModel> candidates, CardPile pile)
        {
            foreach (CardModel c in candidates)
            {
                if (!pile.Cards.Any(p => ReferenceEquals(p, c)))
                    return false;
            }
            return true;
        }

        private static int CountPrecedingMatching(IReadOnlyList<CardModel> list, CardModel target)
        {
            int count = 0;
            foreach (CardModel c in list)
            {
                if (ReferenceEquals(c, target))
                    return count;
                if (string.Equals(c.Id.Entry, target.Id.Entry, StringComparison.Ordinal)
                    && c.CurrentUpgradeLevel == target.CurrentUpgradeLevel)
                {
                    count++;
                }
            }
            return -1;
        }
    }
}
