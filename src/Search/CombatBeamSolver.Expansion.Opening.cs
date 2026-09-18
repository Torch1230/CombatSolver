using System.Runtime.CompilerServices;
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

    internal IReadOnlyList<PlanAction> BuildPowerActionsAfterPrefix(IReadOnlyList<PlanAction> prefix)
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
                if (card.Preview.Type != CardType.Power || !combat.CanPlayCard(simulator, card))
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
                        displayNames.Creature(target),
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

    internal IReadOnlyList<PlanAction> SelectGeneratedResourcePotionActions(
        IReadOnlyList<PlanAction> actions)
        => actions
            .Where(action => action.Choice is
            {
                Effect: PlanChoiceEffect.GenerateToHand,
                Cards.Count: 1,
            })
            .Select(action => (Action: action, Value: GeneratedCardResourceValue(action)))
            .Where(candidate => candidate.Value > 0)
            .GroupBy(candidate => candidate.Action.PotionSlot)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Value)
                .ThenBy(candidate => candidate.Action.Choice!.Cards[0].CardId, StringComparer.Ordinal)
                .First().Action)
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

    internal PlanAction? BuildOpeningDefensiveFollowUp(IReadOnlyList<PlanAction> prefix)
    {
        SearchNode seed = CreateOpeningFollowUpSeed(prefix);
        SimulationSnapshot prefixSnapshot = seed.Snapshot;
        List<SearchNode> followUps = [];
        try
        {
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == prefixSnapshot.Turn));
            SearchNode? best = followUps
                .Where(node => node.Snapshot.PlayerBlock > prefixSnapshot.PlayerBlock)
                .OrderByDescending(node => node.Snapshot.PlayerBlock)
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
            SearchNode? best = followUps
                .Where(node => node.Snapshot.PersistentBuffValue > prefixSnapshot.PersistentBuffValue
                    || node.Snapshot.DelayedDamageValue > prefixSnapshot.DelayedDamageValue
                    || node.Snapshot.ReplayPotentialValue > prefixSnapshot.ReplayPotentialValue
                    || node.Snapshot.ReactiveDamageValue > prefixSnapshot.ReactiveDamageValue
                    || node.Snapshot.StrategicEffects.RetentionValue
                        > prefixSnapshot.StrategicEffects.RetentionValue
                    || node.Snapshot.LongTermResourceValue > prefixSnapshot.LongTermResourceValue)
                .OrderByDescending(node => node.Score)
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
