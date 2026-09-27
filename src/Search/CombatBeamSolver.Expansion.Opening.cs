using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    internal IReadOnlyList<PlanAction> BuildOpeningPowerActions()
        => BuildPowerActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction[]> BuildOpeningPowerPotionSynergyPrefixes()
    {
        List<(PlanAction[] Prefix, int ExtraCards)> candidates = [];
        foreach (PlanAction power in BuildOpeningPowerActions()
                     .Where(action => PowerCardValuationModels.Registry.ContainsCardId(action.CardId!))
                     .Take(3))
        {
            SimulationSnapshot powered = Replay([power]);
            try
            {
                foreach (PlanAction potion in BuildPotionActionsAfterPrefix([power])
                             .Where(action => action.Choice == null)
                             .GroupBy(action => action.PotionSlot)
                             .Select(group => group.First()))
                {
                    SimulationSnapshot after = Replay([power, potion]);
                    try
                    {
                        int extraCards = after.HandCount - powered.HandCount;
                        if (extraCards > 0)
                            candidates.Add(([power, potion], extraCards));
                    }
                    finally
                    {
                        after.ReleaseSimulator();
                    }
                }
            }
            finally
            {
                powered.ReleaseSimulator();
            }
        }
        return candidates.OrderByDescending(candidate => candidate.ExtraCards)
            .Take(3)
            .Select(candidate => candidate.Prefix)
            .ToArray();
    }

    internal IReadOnlyList<PlanAction> BuildOpeningFetchedPowerActions()
    {
        SearchNode seed = CreateOpeningFollowUpSeed([], SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Snapshot.Turn == seed.Snapshot.Turn
                    && HasPlayableFetchedPower(node)
                    && node.Action!.Choice!.Cards.Any(card =>
                        PowerCardValuationModels.Registry.ContainsCardId(card.CardId)))
                .OrderByDescending(node => node.Score)
                .DistinctBy(node => (node.Action!.CardStateKey,
                    Choice: string.Join('|', node.Action.Choice!.Cards.Select(card => card.StateKey))))
                .Take(3)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningPowerUpgradeActions()
    {
        SearchNode seed = CreateOpeningFollowUpSeed([], SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)seed.Snapshot.Simulator;
            PredictedCard[] powers = simulator.State.GetPlayerCombatState(_player).Hand.Cards
                .Where(card => card.Preview.Type == CardType.Power
                    && PowerCardValuationModels.Registry.ContainsCardId(card.Preview.Id.Entry))
                .ToArray();
            if (powers.Length == 0)
                return [];

            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is { Kind: PlanActionKind.PlayCard }
                    && node.Snapshot.Turn == seed.Snapshot.Turn)
                .Select(node => (Node: node, UpgradeGain: ((CombatPredictionSimulator)node.Snapshot.Simulator)
                    .State.GetPlayerCombatState(_player).Hand.Cards
                    .Where(card => card.Preview.Type == CardType.Power)
                    .Sum(card => powers
                        .Where(original => ReferenceEquals(original.Original, card.Original))
                        .Sum(original => Math.Max(0,
                            card.Preview.CurrentUpgradeLevel - original.Preview.CurrentUpgradeLevel)))))
                .Where(item => item.UpgradeGain > 0)
                .OrderByDescending(item => item.UpgradeGain)
                .ThenByDescending(item => item.Node.Score)
                .DistinctBy(item => (item.Node.Action!.CardId,
                    item.Node.Action.CardStateKey,
                    item.Node.Action.CardStateOccurrence,
                    item.Node.Action.TargetCombatId,
                    Choice: string.Join('|', item.Node.Action.Choice?.Cards.Select(card => card.StateKey)
                        ?? [])))
                .Take(3)
                .Select(item => item.Node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildPowerActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix, bool includeWhiteNoise = false)
    {
        SimulationSnapshot prefixSnapshot = Replay(prefix);
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)prefixSnapshot.Simulator;
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
            IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
            List<PlanAction> actions = [];
            HashSet<string> seenCardStates = [];
            SearchNode seed = new(
                null,
                0,
                prefixSnapshot.PotionUseCount,
                prefixSnapshot.PotionStrategicCost,
                prefixSnapshot.Turn,
                SearchRouteTraits.None,
                0,
                prefixSnapshot.Score,
                prefixSnapshot.StateKey,
                prefixSnapshot.HasRisk,
                prefixSnapshot.BoundaryReason,
                false,
                null,
                prefixSnapshot,
                CombatProgressState.Capture(prefixSnapshot));

            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                PredictedCard card = hand[handIndex];
                if ((card.Preview.Type != CardType.Power
                     && !(includeWhiteNoise && card.Preview.Id.Entry == "WHITE_NOISE"))
                    || !combat.CanPlayCard(simulator, card))
                    continue;

                string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
                if (!seenCardStates.Add(cardStateKey))
                    continue;
                int occurrence = hand.Take(handIndex).Count(candidate =>
                    string.Equals(candidate.Preview.Id.Entry, card.Preview.Id.Entry, StringComparison.Ordinal));
                int cardStateOccurrence = hand.Take(handIndex).Count(candidate =>
                    string.Equals(
                        CardChoiceSupport.ChoiceCardKey(candidate),
                        cardStateKey,
                        StringComparison.Ordinal));
                foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
                {
                    if (!card.Original.CanPlayTargeting(target))
                        continue;
                    PlanAction action = new(
                        PlanActionKind.PlayCard,
                        prefixSnapshot.Turn,
                        card.Preview.Id.Entry,
                        occurrence,
                        targetIndex,
                        target?.CombatId,
                        displayNames.Card(card.Preview),
                        displayNames.Creature(target, ((SimulatedCombatState)simulator.State.CombatState).KnownEnemies),
                        ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                        CardStateKey: cardStateKey,
                        CardStateOccurrence: cardStateOccurrence,
                        CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "", CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                    SimulationSnapshot probe = ReplayAction(seed, action);
                    try
                    {
                        if (probe.BoundaryReason == SearchBoundaryReason.None
                            && probe.Turn == prefixSnapshot.Turn
                            && CardChoiceSupport.GetSpec(
                                (CombatPredictionSimulator)probe.Simulator,
                                card) == null)
                        {
                            actions.Add(action);
                        }
                    }
                    finally
                    {
                        probe.ReleaseSimulator();
                    }
                }
            }
            return actions;
        }
        finally
        {
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningPotionActions()
        => BuildPotionActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction> BuildOpeningNightmareActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)seed.Snapshot.Simulator;
            SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
            int nextTurnEnergy = Math.Max(0,
                PersistentPowerSupport.GetModifiedMaxEnergy(
                    (SimulatedCombatState)simulator.State.CombatState, _player));
            var candidates = children
                .Where(node => node.Action is
                {
                    Kind: PlanActionKind.PlayCard,
                    CardId: "NIGHTMARE",
                    Choice: { Cards.Count: 1 },
                })
                .Select(node => (Node: node, Token: node.Action!.Choice!.Cards[0]))
                .Select(item => (item.Node, item.Token,
                    Card: player.Hand.Cards.First(card => CardChoiceSupport.MatchesToken(card, item.Token))))
                .Where(item => item.Card.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power
                    && !item.Card.HasKeyword(simulator.State, CardKeyword.Unplayable))
                .Select(item => (item.Node, item.Token,
                    Type: item.Card.Preview.Type,
                    Value: NightmareCopyTargetValue(item.Card, nextTurnEnergy)))
                .Where(item => item.Value > 0)
                .DistinctBy(item => item.Token.StateKey)
                .OrderByDescending(item => item.Value)
                .ThenByDescending(item => item.Node.Score)
                .ToArray();
            return candidates.Take(2)
                .Concat(candidates.GroupBy(item => item.Type).Select(group => group.First()))
                .DistinctBy(item => item.Token.StateKey)
                .Take(4)
                .Select(item => item.Node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningHandSetupActions(
        IReadOnlyList<PlanAction>? prefix = null)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix ?? [], SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    && (node.Action.Choice?.Effect is PlanChoiceEffect.Discard
                        or PlanChoiceEffect.DiscardAndDraw
                        || node.Snapshot.HandCount > seed.Snapshot.HandCount
                        || node.Snapshot.ReachableHandValue > seed.Snapshot.ReachableHandValue))
                .OrderByDescending(node => node.Snapshot.Energy)
                .ThenBy(node => node.Action!.Choice is
                    { Effect: PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw, Cards.Count: 1 } choice
                    ? OpeningDiscardChoiceCardValue(node, choice)
                    : double.MaxValue)
                .ThenByDescending(node => node.Snapshot.ReachableHandValue)
                .ThenByDescending(node => node.Score)
                .DistinctBy(node => (node.Action!.CardId,
                    node.Action.Choice?.Cards.FirstOrDefault()?.StateKey))
                .Take(3)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningHandCycleActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            SimPlayerCombatState beforePlayer = ((CombatPredictionSimulator)seed.Snapshot.Simulator)
                .State.GetPlayerCombatState(_player);
            Dictionary<string, int> beforeCounts = beforePlayer.Hand.Cards
                .GroupBy(CardChoiceSupport.ChoiceCardKey)
                .ToDictionary(group => group.Key, group => group.Count());
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is
                    { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    && node.Snapshot.Turn == seed.Turn)
                .Select(node =>
                {
                    SimPlayerCombatState afterPlayer = ((CombatPredictionSimulator)node.Snapshot.Simulator)
                        .State.GetPlayerCombatState(_player);
                    int newCards = afterPlayer.Hand.Cards
                        .GroupBy(CardChoiceSupport.ChoiceCardKey)
                        .Sum(group => Math.Max(0,
                            group.Count() - beforeCounts.GetValueOrDefault(group.Key)));
                    return (Node: node, NewCards: newCards);
                })
                .Where(item => item.NewCards >= 2)
                .OrderByDescending(item => item.NewCards)
                .ThenByDescending(item => item.Node.Score)
                .DistinctBy(item => item.Node.Action!.CardStateKey)
                .Take(3)
                .Select(item => item.Node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    private double OpeningDiscardChoiceCardValue(SearchNode node, PlanCardChoice choice)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
        PredictedCard card = player.DiscardPile.Cards
            .Concat(player.Hand.Cards)
            .Concat(player.DrawPile.Cards)
            .Concat(player.ExhaustPile.Cards)
            .FirstOrDefault(candidate => string.Equals(
                CardChoiceSupport.ChoiceCardKey(candidate), choice.Cards[0].StateKey,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"开局弃牌选择执行后找不到 {choice.Cards[0].CardId}+{choice.Cards[0].UpgradeLevel} " +
                $"source={choice.SourcePile} action={node.Action?.CardId}。");
        return CardChoiceSupport.CardValue(card.Preview);
    }

    private static int NightmareCopyTargetValue(PredictedCard card, int nextTurnEnergy)
    {
        int cost = Math.Max(0, card.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local));
        int playableCopies = cost == 0
            ? CardChoiceSupport.NightmareCopyCount
            : Math.Min(CardChoiceSupport.NightmareCopyCount, nextTurnEnergy / cost);
        if (playableCopies == 0)
            return 0;
        int value = Math.Clamp((int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview)), 1, 24);
        if (card.Preview.Type == CardType.Power
            && PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                card.Preview.Id.Entry, out PowerCommitmentDescriptor descriptor))
        {
            value += descriptor.Priority switch
            {
                PowerRoutePriority.Dedicated => 16,
                PowerRoutePriority.Core => 12,
                PowerRoutePriority.Strong => 8,
                PowerRoutePriority.Normal => 4,
                _ => 0,
            };
        }
        return value * playableCopies;
    }

    internal IReadOnlyList<PlanAction> BuildPotionActionsAfterPrefix(IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot rootSnapshot = Replay(prefix);
        List<SearchNode> children = [];
        try
        {
            SearchNode seed = CreateOpeningSearchSeed(rootSnapshot);
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action?.Kind == PlanActionKind.UsePotion)
                .OrderByDescending(node => node.Score)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction[]> BuildEarlierCopyPotionDelayedDamagePrefixes(
        IReadOnlyList<PlanAction> route)
    {
        int copyIndex = -1;
        for (int index = 0; index < route.Count; index++)
        {
            if (route[index] is { Kind: PlanActionKind.UsePotion, PotionId: "DUPLICATOR" })
            {
                copyIndex = index;
                break;
            }
        }
        if (copyIndex < 0)
            return [];

        List<(PlanAction[] Prefix, int DelayedGain)> candidates = [];
        for (int index = 0; index < copyIndex; index++)
        {
            PlanAction action = route[index];
            if (action.Kind != PlanActionKind.PlayCard
                || action.Turn > _startTurnNumber + 2)
                continue;
            PlanAction[] before = route.Take(index).ToArray();
            SimulationSnapshot beforeState = Replay(before);
            SimulationSnapshot afterState = Replay([.. before, action]);
            int delayedGain = afterState.DelayedDamageValue - beforeState.DelayedDamageValue;
            beforeState.ReleaseSimulator();
            afterState.ReleaseSimulator();
            if (delayedGain <= 0)
                continue;

            PlanAction? copyPotion = BuildPotionActionsAfterPrefix(before)
                .FirstOrDefault(candidate => candidate.PotionId == "DUPLICATOR");
            if (copyPotion == null)
                continue;
            PlanAction[] prefix = [.. before, copyPotion, action];
            if (CanReplayOpeningPrefix(prefix))
                candidates.Add((prefix, delayedGain));
        }
        return candidates.OrderByDescending(candidate => candidate.DelayedGain)
            .Take(3).Select(candidate => candidate.Prefix).ToArray();
    }

    internal IReadOnlyList<PlanAction> BuildPreferredOpeningPotionActions()
        => BuildPreferredPotionActionsAfterPrefix([]);

    private static SearchNode CreateOpeningSearchSeed(SimulationSnapshot rootSnapshot)
    {
        return new(
            null,
            0,
            rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost,
            rootSnapshot.Turn,
            SearchRouteTraits.None,
            0,
            rootSnapshot.Score,
            rootSnapshot.StateKey,
            rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason,
            false,
            null,
            rootSnapshot,
            CombatProgressState.Capture(rootSnapshot));
    }

    internal IReadOnlyList<PlanAction> BuildOpeningResourceActions()
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        List<SearchNode> children = [];
        try
        {
            IReadOnlyList<PredictedCard> openingHand = ((CombatPredictionSimulator)rootSnapshot.Simulator)
                .State.GetPlayerCombatState(_player).Hand.Cards;
            IReadOnlyDictionary<string, CardType> cardTypes = openingHand
                .GroupBy(card => card.Preview.Id.Entry)
                .ToDictionary(group => group.Key, group => group.First().Preview.Type);
            SearchNode seed = CreateOpeningSearchSeed(rootSnapshot);
            children.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == rootSnapshot.Turn));
            return children
                .Where(node => node.Snapshot.Energy > rootSnapshot.Energy
                    || node.Snapshot.Stars > rootSnapshot.Stars
                    || node.Snapshot.HandCount > rootSnapshot.HandCount
                    || node.Snapshot.ReachableHandValue > rootSnapshot.ReachableHandValue
                    || node.Snapshot.ZeroCostPlayableCount > rootSnapshot.ZeroCostPlayableCount
                    || (node.Traits & SearchRouteTraits.Resource) != 0)
                .Select(node => (
                    Node: node,
                    Value: (node.Snapshot.Energy - rootSnapshot.Energy) * 64
                        + (node.Snapshot.Stars - rootSnapshot.Stars) * 48
                        + (node.Snapshot.HandCount - rootSnapshot.HandCount) * 16
                        + node.Snapshot.ReachableHandValue - rootSnapshot.ReachableHandValue
                        + (node.Snapshot.ZeroCostPlayableCount - rootSnapshot.ZeroCostPlayableCount) * 8))
                .GroupBy(candidate => cardTypes[candidate.Node.Action!.CardId])
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Node.Score)
                    .First())
                .OrderByDescending(candidate => candidate.Value)
                .ThenByDescending(candidate => candidate.Node.Score)
                .Take(3)
                .Select(candidate => candidate.Node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildPreferredPotionActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        HashSet<uint> setupTargetIds = (_forecast.Rounds.FirstOrDefault() ?? [])
            .Where(move => move.AttackHits.Count == 0 && move.Owner.CombatId.HasValue)
            .Select(move => move.Owner.CombatId!.Value)
            .ToHashSet();
        List<PlanAction> selected = [];
        foreach (IGrouping<int, PlanAction> slotActions in BuildPotionActionsAfterPrefix(prefix)
                     .GroupBy(action => action.PotionSlot))
        {
            selected.Add(slotActions.First());
            PlanAction? setupTargetAction = slotActions.FirstOrDefault(action =>
                action.TargetCombatId is uint targetId && setupTargetIds.Contains(targetId));
            if (setupTargetAction != null && !selected.Contains(setupTargetAction))
                selected.Add(setupTargetAction);
        }
        return selected;
    }

    internal IReadOnlyList<PlanAction> SelectGeneratedCardPotionActions(
        IReadOnlyList<PlanAction> actions)
        => actions
            .Where(action => action.Choice is
            {
                Effect: PlanChoiceEffect.GenerateToHand,
                Cards.Count: 1,
            })
            .Select(action => (Action: action, Value: GeneratedCardResourceValue(action)))
            .GroupBy(candidate => candidate.Action.PotionSlot)
            .SelectMany(group => group
                .OrderByDescending(candidate => candidate.Value)
                .ThenBy(candidate => candidate.Action.Choice!.Cards[0].CardId, StringComparer.Ordinal)
                .Take(3)
                .Select(candidate => candidate.Action))
            .ToArray();

    private int GeneratedCardResourceValue(PlanAction action)
    {
        SimulationSnapshot snapshot = Replay([action]);
        try
        {
            PlanCardToken token = action.Choice!.Cards[0];
            PredictedCard? card = ((CombatPredictionSimulator)snapshot.Simulator).State
                .GetPlayerCombatState(_player)
                .Hand.Cards
                .LastOrDefault(candidate => CardChoiceSupport.MatchesToken(candidate, token));
            if (card == null)
                return 0;

            int draw = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Cards"));
            int energy = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Energy"));
            int stars = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Stars"));
            return draw * 16 + energy * 16 + stars * 8;
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private SearchNode CreateOpeningFollowUpSeed(IReadOnlyList<PlanAction> prefix, SearchRouteTraits traits = SearchRouteTraits.Scaling)
    {
        SimulationSnapshot snapshot = Replay([]);
        SearchNode seed = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, traits, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot, CombatProgressState.Capture(snapshot));
        // Build the actual parent chain, so replay verification and descendant actions
        // include the resource/potion/setup cards that produced this state.
        return ApplyFixedPrefix(seed, prefix)
            ?? throw new InvalidOperationException("Opening follow-up prefix is no longer applicable.");
    }

    internal bool CanReplayOpeningPrefix(IReadOnlyList<PlanAction> prefix)
    {
        IReadOnlyList<SimulationSnapshot> roots = _includeTurnSetup
            ? BuildTurnSetupRoots().Select(candidate => candidate.Snapshot).ToArray()
            : [Replay([])];
        bool applicable = false;
        foreach (SimulationSnapshot snapshot in roots)
        {
            SearchNode? applied = ApplyFixedPrefix(CreateOpeningSearchSeed(snapshot), prefix);
            if (applied == null)
                continue;
            applicable = true;
            applied.Snapshot.ReleaseSimulator();
        }
        return applicable;
    }

    internal IReadOnlyList<PlanAction> BuildOpeningOffensiveFollowUps(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        SimulationSnapshot prefixSnapshot = seed.Snapshot;
        List<SearchNode> followUps = [];
        try
        {
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is
                {
                    Kind: PlanActionKind.PlayCard,
                    Turn: var turn,
                    TargetCombatId: not null,
                }
                && turn == prefixSnapshot.Turn
                && node.Snapshot.EnemyHp < prefixSnapshot.EnemyHp));
            return followUps
                .GroupBy(node => node.Action!.TargetCombatId!.Value)
                .Select(group => group
                    .OrderBy(node => node.Snapshot.AliveEnemyCount)
                    .ThenBy(node => node.Snapshot.EnemyHp)
                    .ThenByDescending(node => node.Snapshot.FocusTargetPressure)
                    .ThenByDescending(node => node.Score)
                    .First().Action!)
                .OrderBy(action => action.TargetCombatId)
                .Take(3)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction[]> BuildOpeningFocusedTargetPrefixes(
        IReadOnlyList<PlanAction> opening)
    {
        if (!opening.Any(action => action is
            { Kind: PlanActionKind.PlayCard, TargetCombatId: not null }))
            return [];

        List<PlanAction[]> focused = [];
        for (int targetIndex = 0; targetIndex < Math.Min(3, root.Enemies.Count); targetIndex++)
        {
            Creature enemy = root.Enemies[targetIndex];
            PlanAction[] prefix = opening.Select(action => action is
                { Kind: PlanActionKind.PlayCard, TargetCombatId: not null }
                    ? action with
                    {
                        TargetIndex = targetIndex,
                        TargetCombatId = enemy.CombatId,
                        TargetName = displayNames.Creature(enemy),
                    }
                    : action).ToArray();
            if (CanReplayOpeningPrefix(prefix))
                focused.Add(prefix);
        }
        return focused;
    }

    internal IReadOnlyList<PlanAction[]> BuildOpeningLeadingTargetPrefixes(
        IReadOnlyList<PlanAction> opening)
    {
        int[] targetedIndices = opening.Select((action, index) => (action, index))
            .Where(item => item.action is { Kind: PlanActionKind.PlayCard, TargetCombatId: not null })
            .Take(3).Select(item => item.index).ToArray();
        if (targetedIndices.Length < 2)
            return [];

        List<PlanAction[]> variants = [];
        for (int targetIndex = 0; targetIndex < Math.Min(3, root.Enemies.Count); targetIndex++)
        {
            Creature enemy = root.Enemies[targetIndex];
            for (int count = 1; count <= Math.Min(2, targetedIndices.Length); count++)
            {
                PlanAction[] prefix = opening.ToArray();
                for (int i = 0; i < count; i++)
                {
                    int actionIndex = targetedIndices[i];
                    prefix[actionIndex] = prefix[actionIndex] with
                    {
                        TargetIndex = targetIndex,
                        TargetCombatId = enemy.CombatId,
                        TargetName = displayNames.Creature(enemy),
                    };
                }
                if (targetedIndices.Take(count).All(index =>
                        prefix[index].TargetCombatId == opening[index].TargetCombatId))
                    continue;
                if (CanReplayOpeningPrefix(prefix))
                    variants.Add(prefix);
                if (count == 2 && targetedIndices[1] > targetedIndices[0] + 1)
                {
                    List<PlanAction> reordered = prefix.ToList();
                    PlanAction secondTargeted = reordered[targetedIndices[1]];
                    reordered.RemoveAt(targetedIndices[1]);
                    reordered.Insert(targetedIndices[0] + 1, secondTargeted);
                    if (CanReplayOpeningPrefix(reordered))
                        variants.Add(reordered.ToArray());
                }
            }
        }
        return variants;
    }

    internal IReadOnlyList<PlanAction> BuildOpeningOffensiveCardVariantsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    && node.Snapshot.Turn == seed.Snapshot.Turn
                    && node.Snapshot.EnemyHp < seed.Snapshot.EnemyHp)
                .GroupBy(node => node.Action!.CardStateKey)
                .Select(group => group.OrderByDescending(node => node.Score).First())
                .OrderByDescending(node => node.Score)
                .Take(5)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningFreeOffensiveActions()
        => BuildFreeOffensiveActionsAfterPrefix([], includeTerminal: true);

    internal IReadOnlyList<PlanAction> BuildFreeOffensiveActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix, bool includeTerminal = false)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is
                {
                    Kind: PlanActionKind.PlayCard,
                    TargetCombatId: not null,
                } && node.Snapshot.Turn == seed.Snapshot.Turn
                    && node.Snapshot.Energy == seed.Snapshot.Energy
                    && node.Snapshot.EnemyHp < seed.Snapshot.EnemyHp
                    && (includeTerminal || !node.Snapshot.AllEnemiesDead))
                .GroupBy(node => node.Action!.CardStateKey)
                .Select(group => group.OrderByDescending(node => node.Score).First())
                .OrderByDescending(node => node.Score)
                .Take(3)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildTurnEndChoiceActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action is
                {
                    Kind: PlanActionKind.EndTurn,
                    TurnStartChoices: { Count: > 0 },
                })
                .OrderByDescending(node => node.Score)
                .Select(node => node.Action!)
                .DistinctBy(TurnEndChoiceKey)
                .Take(4)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            seed.Snapshot.ReleaseSimulator();
        }
    }

    internal static string TurnEndChoiceKey(PlanAction action)
        => JsonSerializer.Serialize(action.TurnStartChoices);

    internal IReadOnlyList<PlanAction[]> BuildOpeningNoCostPrefixes()
    {
        SearchNode seed = CreateOpeningFollowUpSeed([], SearchRouteTraits.None);
        List<SearchNode> allNodes = [seed];
        List<SearchNode> frontier = [seed];
        try
        {
            List<SearchNode> candidates = [];
            for (int depth = 0; depth < 4 && frontier.Count > 0; depth++)
            {
                List<SearchNode> next = [];
                foreach (SearchNode parent in frontier)
                {
                    List<SearchNode> children = Expand(parent).ToList();
                    allNodes.AddRange(children);
                    next.AddRange(children.Where(node => node.Action is
                        { Kind: PlanActionKind.PlayCard }
                        && node.Snapshot.Turn == parent.Snapshot.Turn
                        && node.Snapshot.Energy == parent.Snapshot.Energy));
                }
                frontier = next
                    .DistinctBy(node => node.Snapshot.StateKey)
                    .OrderByDescending(node => node.Score)
                    .Take(24)
                    .ToList();
                candidates.AddRange(frontier);
            }
            return candidates
                .OrderByDescending(node => node.ActionCount)
                .ThenByDescending(node => node.Score)
                .Take(12)
                .Select(node => node.Actions.ToArray())
                .ToArray();
        }
        finally
        {
            foreach (SearchNode node in allNodes)
                node.Snapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningDefensiveFollowUp(IReadOnlyList<PlanAction> prefix)
        => BuildOpeningDefensiveFollowUps(prefix).FirstOrDefault();

    internal IReadOnlyList<PlanAction> BuildOpeningDefensiveFollowUps(IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix);
        SimulationSnapshot prefixSnapshot = seed.Snapshot;
        List<SearchNode> followUps = [];
        try
        {
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == prefixSnapshot.Turn));
            return followUps
                .Where(node => node.Snapshot.PlayerBlock > prefixSnapshot.PlayerBlock)
                .OrderByDescending(node => node.Snapshot.PlayerBlock)
                .ThenByDescending(node => node.Score)
                .DistinctBy(node => node.Action!.CardId)
                .Take(3)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningSetupFollowUp(IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix);
        SimulationSnapshot prefixSnapshot = seed.Snapshot;
        List<SearchNode> followUps = [];
        try
        {
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == prefixSnapshot.Turn));
            IReadOnlyList<PredictedCard> hand = ((CombatPredictionSimulator)prefixSnapshot.Simulator)
                .State.GetPlayerCombatState(_player).Hand.Cards;
            SearchNode? best = followUps
                .Where(node => node.Snapshot.PersistentBuffValue > prefixSnapshot.PersistentBuffValue
                    || node.Snapshot.DelayedDamageValue > prefixSnapshot.DelayedDamageValue
                    || node.Snapshot.ReplayPotentialValue > prefixSnapshot.ReplayPotentialValue
                    || node.Snapshot.ReactiveDamageValue > prefixSnapshot.ReactiveDamageValue
                    || node.Snapshot.StrategicEffects.RetentionValue
                        > prefixSnapshot.StrategicEffects.RetentionValue
                    || node.Snapshot.LongTermResourceValue > prefixSnapshot.LongTermResourceValue)
                .GroupBy(node => (node.Action!.CardStateKey, node.Action.TargetCombatId))
                .Select(group => group
                    .OrderByDescending(node => node.Snapshot.ZeroCostPlayableCount)
                    .ThenByDescending(node => node.Action?.Choice is
                        { Effect: PlanChoiceEffect.Exhaust, Cards.Count: 1 } choice
                        && hand.Count(card => card.Preview.Id.Entry == choice.Cards[0].CardId) > 1)
                    .ThenByDescending(node => node.Score)
                    .First())
                .OrderByDescending(node => node.Snapshot.PersistentBuffValue
                    - prefixSnapshot.PersistentBuffValue)
                .ThenByDescending(node => node.Score)
                .FirstOrDefault();
            return best?.Action;
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningFullRedrawPotionAction(PlanAction selectedPotionAction)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SimulationSnapshot? probeSnapshot = null;
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)rootSnapshot.Simulator;
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            PotionModel? potion = combat.GetPotionAtSlot(_player, selectedPotionAction.PotionSlot);
            if (potion is not GamblersBrew
                || !string.Equals(
                    potion.Id.Entry,
                    selectedPotionAction.PotionId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            SearchNode seed = new(
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
                false,
                null,
                rootSnapshot,
                CombatProgressState.Capture(rootSnapshot));
            PlanAction baseAction = selectedPotionAction with
            {
                Turn = _startTurnNumber,
                Choice = null,
                NestedChoices = null,
                NestedChoicesBeforePrimary = 0,
                TurnStartChoices = null,
                RelicEffects = null,
                EndsPlayerTurn = false,
            };
            probeSnapshot = ReplayAction(seed, baseAction);
            CardChoiceSpec spec = PotionChoiceSupport.GetSpec(
                (CombatPredictionSimulator)probeSnapshot.Simulator,
                potion);
            PlanCardChoice? fullRedraw = CardChoiceSupport.BuildChoices(
                    spec,
                    displayNames,
                    _profile.MaxPileChoiceBranchesPerAction,
                    _profile.MaxHandChoiceBranchesPerAction)
                .OrderByDescending(choice => choice.Cards.Count)
                .FirstOrDefault();
            return fullRedraw == null
                ? null
                : baseAction with
                {
                    Choice = fullRedraw with { SourceId = potion.Id.Entry },
                };
        }
        finally
        {
            probeSnapshot?.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    private bool HasPlayableFetchedPower(SearchNode node)
    {
        if (node.Action?.Choice is not { Effect: PlanChoiceEffect.MoveToHand } choice)
            return false;
        var simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        return simulator.State.GetPlayerCombatState(_player).Hand.Cards.Any(card =>
            card.Preview.Type == CardType.Power && combat.CanPlayCard(simulator, card)
            && choice.Cards.Any(token => CardChoiceSupport.MatchesToken(card, token)));
    }

}
