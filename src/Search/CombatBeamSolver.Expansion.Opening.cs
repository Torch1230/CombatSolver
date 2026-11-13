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
    private IEnumerable<SearchNode> ExpandOpeningSeed(SearchNode seed)
        => seed.IsTerminal || seed.Snapshot.PlayerDead || seed.Snapshot.AllEnemiesDead
            || seed.Snapshot.BoundaryReason != SearchBoundaryReason.None
                ? []
                : Expand(seed);

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
            children.AddRange(ExpandOpeningSeed(seed));
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

            children.AddRange(ExpandOpeningSeed(seed));
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
            // A prefix may stop at a choice or another replay boundary. Only
            // settled, live states can be parents of an incremental power probe.
            if (prefixSnapshot.BoundaryReason != SearchBoundaryReason.None
                || prefixSnapshot.PlayerDead || prefixSnapshot.AllEnemiesDead)
                return [];
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)prefixSnapshot.Simulator;
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
            IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
            List<PlanAction> actions = [];
            HashSet<string> seenCardStates = [];

            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                PredictedCard card = hand[handIndex];
                if ((card.Preview.Type != CardType.Power
                     && !(includeWhiteNoise && OpeningActionRegistry.Default.MatchesId(
                         OpeningCandidatePurpose.GeneratedPowerCard, card.Preview.Id.Entry)))
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
                    SimulationSnapshot probe = ReplayOpeningPowerProbe(prefixSnapshot, prefix, action);
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

    private SimulationSnapshot ReplayOpeningPowerProbe(
        SimulationSnapshot prefixSnapshot, IReadOnlyList<PlanAction> prefix, PlanAction action)
        => SearchTransitionGuard.Execute(action, prefixSnapshot.StateKey, prefix.Count, () =>
        {
            SimulationSnapshot incremental = Replay(
                [action], prefixSnapshot, prefixSnapshot.Turn, prefix.Count);
            try
            {
                if (policy.VerifyIncrementalSearch)
                {
                    PlanAction[] fullActions = [.. prefix, action];
                    SimulationSnapshot replayed = Replay(fullActions, allowExecutionCapture: false);
                    try
                    {
                        AssertIncrementalEquivalent(action, fullActions, incremental, replayed);
                    }
                    finally
                    {
                        replayed.ReleaseSimulator();
                    }
                }
                return incremental;
            }
            catch
            {
                incremental.ReleaseSimulator();
                throw;
            }
        });

    internal IReadOnlyList<PlanAction> BuildOpeningPotionActions()
        => BuildPotionActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction> BuildOpeningPlanPotionActions(
        PlanChoiceEffect desiredEffect,
        int maximumActions)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        List<SearchNode> children = [];
        try
        {
            SearchNode seed = CreateOpeningSearchSeed(rootSnapshot);
            children.AddRange(ExpandOpeningSeed(seed));
            var candidates = children
                .Where(node => node.Action?.Kind == PlanActionKind.UsePotion)
                .Select(node => (Node: node,
                    Reach: OpeningPlanPotionReachability(node.Snapshot, desiredEffect)))
                .ToArray();
            var selected = candidates
                .OrderByDescending(candidate => candidate.Reach.Rank)
                .ThenByDescending(candidate => candidate.Reach.RetainedPowers)
                .ThenBy(candidate => candidate.Reach.DrawDistance)
                .ThenByDescending(candidate => candidate.Node.Score)
                .GroupBy(candidate => candidate.Node.Action!.PotionSlot)
                .SelectMany(group => group.Take(maximumActions))
                .Take(maximumActions)
                .Select(candidate => candidate.Node.Action!)
                .ToArray();
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] PLAN_POTION_REACHABILITY candidates={candidates.Length} " +
                $"direct={candidates.Count(candidate => candidate.Reach.Rank == 3)} " +
                $"one_draw={candidates.Count(candidate => candidate.Reach.Rank == 2)} " +
                $"chain_draw={candidates.Count(candidate => candidate.Reach.Rank == 1)} " +
                $"selected={selected.Length}");
            return selected;
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    private (int Rank, int RetainedPowers, int DrawDistance) OpeningPlanPotionReachability(
        SimulationSnapshot snapshot,
        PlanChoiceEffect desiredEffect)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
        int retainedPowers = player.Hand.Cards.Count(card =>
            PowerCardValuationModels.Registry.ContainsCardId(card.Preview.Id.Entry));
        if (HasPlayableChoiceEffect(snapshot, desiredEffect))
            return (3, retainedPowers, 0);

        int drawDistance = int.MaxValue;
        for (int index = 0; index < player.DrawPile.Cards.Count; index++)
        {
            if (CardChoiceSupport.GetPotentialEffect(simulator, player.DrawPile.Cards[index])
                != desiredEffect)
                continue;
            drawDistance = index;
            break;
        }
        int maximumSingleDraw = 0;
        int totalDraw = 0;
        foreach (PredictedCard card in player.Hand.Cards)
        {
            if (!combat.CanPlayCard(simulator, card))
                continue;
            int draw = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(
                card.Preview.DynamicVars, "Cards"));
            maximumSingleDraw = Math.Max(maximumSingleDraw, draw);
            totalDraw += draw;
        }
        int rank = drawDistance < maximumSingleDraw ? 2
            : drawDistance < totalDraw ? 1 : 0;
        return (rank, retainedPowers, drawDistance);
    }

    internal IReadOnlyList<PlanAction> BuildOpeningCopyActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix,
        int maximumPowerTargets = 1,
        int maximumActions = 4,
        PlanChoiceEffect desiredEffect = PlanChoiceEffect.Nightmare)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)seed.Snapshot.Simulator;
            SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
            if (!HasPlayableChoiceEffect(seed.Snapshot, desiredEffect))
                return [];
            children.AddRange(ExpandOpeningSeed(seed));
            int nextTurnEnergy = Math.Max(0,
                PersistentPowerSupport.GetModifiedMaxEnergy(
                    (SimulatedCombatState)simulator.State.CombatState, _player));
            var candidates = children
                .Where(node => node.Action is
                    { Kind: PlanActionKind.PlayCard, Choice: { Cards.Count: 1 } }
                    && node.Action.Choice.Effect == desiredEffect)
                .Select(node => (Node: node, Token: node.Action!.Choice!.Cards[0]))
                .Select(item => (item.Node, item.Token,
                    Card: player.Hand.Cards.First(card => CardChoiceSupport.MatchesToken(card, item.Token))))
                .Where(item => item.Card.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power
                    && !item.Card.HasKeyword(simulator.State, CardKeyword.Unplayable))
                .Select(item => (item.Node, item.Token,
                    Type: item.Card.Preview.Type,
                    Value: DeferredCopyTargetValue(item.Card, nextTurnEnergy)))
                .Where(item => item.Value > 0)
                .DistinctBy(item => item.Token.StateKey)
                .OrderByDescending(item => item.Value)
                .ThenByDescending(item => item.Node.Score)
                .ToArray();
            IEnumerable<(SearchNode Node, PlanCardToken Token, CardType Type, int Value)> selected =
                candidates.Take(2)
                    .Concat(candidates.GroupBy(item => item.Type).Select(group => group.First()));
            if (maximumPowerTargets > 1)
                selected = selected.Concat(candidates
                    .Where(item => item.Type == CardType.Power)
                    .DistinctBy(item => item.Token.CardId)
                    .Take(maximumPowerTargets));
            return selected
                .DistinctBy(item => item.Token.StateKey)
                .Take(maximumActions)
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
        IReadOnlyList<PlanAction>? prefix = null,
        int maximumActions = 3,
        PlanChoiceEffect? desiredFollowUpEffect = null)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix ?? [], SearchRouteTraits.None);
        List<SearchNode> children = [];
        Dictionary<string, OpeningDiscardChoiceValues> discardValues = [];
        try
        {
            children.AddRange(ExpandOpeningSeed(seed));
            IEnumerable<SearchNode> ordered = children
                .Where(node => node.Action is { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
                    && (node.Action.Choice?.Effect is PlanChoiceEffect.Discard
                        or PlanChoiceEffect.DiscardAndDraw
                        || node.Snapshot.HandCount > seed.Snapshot.HandCount
                        || node.Snapshot.ReachableHandValue > seed.Snapshot.ReachableHandValue))
                .OrderByDescending(node => node.Snapshot.Energy)
                .ThenBy(node => node.Action!.Choice is
                    { Effect: PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw, Cards.Count: 1 } choice
                    ? OpeningDiscardChoiceCardValue(seed, node, choice, discardValues)
                    : double.MaxValue)
                .ThenByDescending(node => node.Snapshot.ReachableHandValue)
                .ThenByDescending(node => node.Score)
                .DistinctBy(node => (node.Action!.CardId,
                    node.Action.Choice?.Cards.FirstOrDefault()?.StateKey));
            if (desiredFollowUpEffect is { } effect)
                ordered = ordered.OrderByDescending(node => HasPlayableChoiceEffect(
                    node.Snapshot, effect));
            return ordered
                .Take(maximumActions)
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

    internal bool ContainsChoiceEffectInRoot(PlanChoiceEffect effect)
    {
        SimulationSnapshot snapshot = Replay([]);
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
            return simulator.State.GetPlayerCombatState(_player).AllCards.Any(card =>
                CardChoiceSupport.GetPotentialEffect(simulator, card) == effect);
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private bool HasPlayableChoiceEffect(
        SimulationSnapshot snapshot, PlanChoiceEffect effect)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return simulator.State.GetPlayerCombatState(_player).Hand.Cards.Any(card =>
            combat.CanPlayCard(simulator, card)
            && CardChoiceSupport.GetPotentialEffect(simulator, card) == effect);
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
            children.AddRange(ExpandOpeningSeed(seed));
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

    private sealed record OpeningDiscardChoiceValues(
        PlanChoiceEffect Effect,
        PileType SourcePile,
        string SourceId,
        string ContextId,
        PlanChoiceTiming Timing,
        IReadOnlyList<(PlanCardToken Token, double Value)> Options);

    private double OpeningDiscardChoiceCardValue(SearchNode seed, SearchNode node, PlanCardChoice choice,
        Dictionary<string, OpeningDiscardChoiceValues> cache)
    {
        PlanAction action = node.Action
            ?? throw new InvalidOperationException("开局弃牌估值缺少动作。");
        _ = action.GetActionChoicesInExecutionOrder();
        PlanAction probeAction = action with
        {
            Choice = null,
            NestedChoices = action.NestedChoices?.Take(action.NestedChoicesBeforePrimary).ToArray(),
            TurnStartChoices = null,
            RelicEffects = null,
            EndsPlayerTurn = false,
        };
        string key = JsonSerializer.Serialize(probeAction);
        if (!cache.TryGetValue(key, out OpeningDiscardChoiceValues? values))
        {
            SimulationSnapshot probe = ReplayAction(seed, probeAction);
            try
            {
                SimulatedCombatState combat = (SimulatedCombatState)probe.Simulator.State.CombatState;
                TurnStartChoiceRequest request = probe.BoundaryReason == SearchBoundaryReason.PendingChoice
                    && combat.PendingTurnStartChoice is { } pending ? pending
                    : throw new InvalidOperationException($"开局弃牌估值未到达选择边界：action={action.CardId}。");
                CardChoiceSpec spec = BuildPrimaryCardChoiceSpec(probe)
                    ?? throw new InvalidOperationException($"开局弃牌估值没有主选择：action={action.CardId}。");
                List<(PlanCardToken Token, double Value)> options = [];
                Dictionary<(string Id, int Upgrade), int> occurrences = [];
                foreach (PredictedCard card in spec.Options)
                {
                    var identity = (card.Preview.Id.Entry, card.Preview.CurrentUpgradeLevel);
                    int optionOccurrence = occurrences.GetValueOrDefault(identity);
                    occurrences[identity] = optionOccurrence + 1;
                    int sourceOccurrence = 0;
                    bool found = false;
                    foreach (PredictedCard sourceCard in spec.SourceCards)
                    {
                        if (ReferenceEquals(sourceCard, card)) { found = true; break; }
                        if (sourceCard.Preview.Id.Entry == identity.Entry
                            && sourceCard.Preview.CurrentUpgradeLevel == identity.CurrentUpgradeLevel)
                            sourceOccurrence++;
                    }
                    if (!found)
                        throw new InvalidOperationException($"开局弃牌候选不在来源牌堆：action={action.CardId}。");
                    // The selection's cost and keywords may change when discard triggers an automatic play.
                    options.Add((new PlanCardToken(identity.Entry, identity.CurrentUpgradeLevel,
                        CardChoiceSupport.ChoiceCardKey(card), sourceOccurrence, optionOccurrence, string.Empty),
                        CardChoiceSupport.CardValue(card.Preview)));
                }
                values = new(spec.Effect, spec.SourcePile, request.SourceId, request.ContextId, request.Timing, options);
                cache.Add(key, values);
            }
            finally { probe.ReleaseSimulator(); }
        }
        PlanCardToken token = choice.Cards.Single();
        if (values.Effect != choice.Effect || values.SourcePile != choice.SourcePile
            || values.SourceId != choice.SourceId || values.ContextId != choice.ContextId || values.Timing != choice.Timing)
            throw new InvalidOperationException($"开局弃牌估值的选择上下文不一致：action={action.CardId}。");
        foreach (var option in values.Options)
        {
            if (option.Token.CardId == token.CardId && option.Token.UpgradeLevel == token.UpgradeLevel
                && option.Token.SourceOccurrence == token.SourceOccurrence
                && option.Token.OptionOccurrence == token.OptionOccurrence
                && string.Equals(option.Token.StateKey, token.StateKey, StringComparison.Ordinal))
                return option.Value;
        }
        throw new InvalidOperationException(
            $"开局弃牌估值找不到选择时的卡牌 {token.CardId}+{token.UpgradeLevel} " +
            $"source={choice.SourcePile} action={action.CardId}。");
    }

    private static int DeferredCopyTargetValue(PredictedCard card, int nextTurnEnergy)
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
            children.AddRange(ExpandOpeningSeed(seed));
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

    internal IReadOnlyList<PlanAction> BuildFreeEntropicPotionActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot snapshot = Replay(prefix);
        List<SearchNode> children = [];
        try
        {
            SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
            HashSet<int> freeSlots = Enumerable.Range(0, root.PotionSlotCount)
                .Where(slot => combat.IsFreeEntropicPotionAtSlot(_player, slot))
                .ToHashSet();
            if (freeSlots.Count == 0)
                return [];
            children.AddRange(ExpandOpeningSeed(CreateOpeningSearchSeed(snapshot)));
            return children
                .Where(node => node.Action is { Kind: PlanActionKind.UsePotion } action
                    && freeSlots.Contains(action.PotionSlot))
                .OrderByDescending(node => node.Score)
                .GroupBy(node => node.Action!.PotionSlot)
                .Select(group => group.First().Action!)
                .Take(2)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            snapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction[]> BuildEarlierCopyPotionDelayedDamagePrefixes(
        IReadOnlyList<PlanAction> route)
    {
        int copyIndex = -1;
        for (int index = 0; index < route.Count; index++)
        {
            if (OpeningActionRegistry.Default.Matches(
                    OpeningCandidatePurpose.DelayedDamageCopyPotion, route[index]))
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
                .FirstOrDefault(candidate => OpeningActionRegistry.Default.Matches(
                    OpeningCandidatePurpose.DelayedDamageCopyPotion, candidate));
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
            children.AddRange(ExpandOpeningSeed(seed).Where(node =>
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
        => TryCreateOpeningFollowUpSeed(prefix, traits)
            ?? throw new InvalidOperationException("Opening follow-up prefix is no longer applicable.");

    private SearchNode? TryCreateOpeningFollowUpSeed(IReadOnlyList<PlanAction> prefix, SearchRouteTraits traits = SearchRouteTraits.Scaling)
    {
        SimulationSnapshot snapshot = Replay([]);
        SearchNode seed = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, traits, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot, CombatProgressState.Capture(snapshot));
        // Build the actual parent chain, so replay verification and descendant actions
        // include the resource/potion/setup cards that produced this state.
        return ApplyFixedPrefix(seed, prefix);
    }

    internal bool CanContinueAtPrefix(IReadOnlyList<PlanAction> prefix)
    {
        SearchNode? applied = TryCreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        if (applied == null)
            return false;
        try
        {
            return applied.ActionCount == prefix.Count
                && !applied.Snapshot.PlayerDead && !applied.Snapshot.AllEnemiesDead
                && !applied.Snapshot.HasRisk
                && applied.Snapshot.BoundaryReason == SearchBoundaryReason.None;
        }
        finally
        {
            applied.Snapshot.ReleaseSimulator();
        }
    }

    internal bool CanReplayOpeningPrefix(IReadOnlyList<PlanAction> prefix)
    {
        if (prefix.Any(action => action.EndsPlayerTurn))
            return false;
        IReadOnlyList<SimulationSnapshot> roots = _includeTurnSetup
            ? BuildTurnSetupRoots().Select(candidate => candidate.Snapshot).ToArray()
            : [Replay([])];
        bool applicable = false;
        foreach (SimulationSnapshot snapshot in roots)
        {
            SearchNode? applied;
            try
            {
                applied = ApplyFixedPrefix(CreateOpeningSearchSeed(snapshot), prefix);
            }
            catch (InvalidPlannedChoiceBranchException)
            {
                continue;
            }
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
            followUps.AddRange(ExpandOpeningSeed(seed).Where(node =>
                node.Action is
                {
                    Kind: PlanActionKind.PlayCard,
                    Turn: var turn,
                    TargetCombatId: not null,
                }
                && turn == prefixSnapshot.Turn
                && node.Snapshot.EnemyHp < prefixSnapshot.EnemyHp));
            return TargetPlanRegistry.Default.SelectOffensiveFollowUps(followUps);
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
        => TargetPlanRegistry.Default.BuildFocusedPrefixes(opening, root.Enemies,
            enemy => displayNames.Creature(enemy), CanReplayOpeningPrefix);

    internal IReadOnlyList<PlanAction[]> BuildOpeningLeadingTargetPrefixes(
        IReadOnlyList<PlanAction> opening)
        => TargetPlanRegistry.Default.BuildLeadingPrefixes(opening, root.Enemies,
            enemy => displayNames.Creature(enemy), CanReplayOpeningPrefix);

    internal IReadOnlyList<PlanAction> BuildOpeningOffensiveCardVariantsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        List<SearchNode> children = [];
        try
        {
            children.AddRange(ExpandOpeningSeed(seed));
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
            children.AddRange(ExpandOpeningSeed(seed));
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
        SearchNode? seed = TryCreateOpeningFollowUpSeed(prefix, SearchRouteTraits.None);
        if (seed == null)
            return [];
        List<SearchNode> children = [];
        try
        {
            children.AddRange(ExpandOpeningSeed(seed));
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
                    List<SearchNode> children = ExpandOpeningSeed(parent).ToList();
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
            followUps.AddRange(ExpandOpeningSeed(seed).Where(node =>
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
            followUps.AddRange(ExpandOpeningSeed(seed).Where(node =>
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
