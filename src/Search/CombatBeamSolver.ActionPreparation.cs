using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct PreparedCardAction(
        PlanAction Action,
        CardType CardType,
        uint? TargetCombatId,
        bool RequiresUnsupportedExistingChoice,
        PlanCardChoice? RequiredEmptyChoice);

    private readonly record struct PreparedPotionAction(PlanAction Action, PotionModel Potion);

    private List<PreparedCardAction> PrepareCardActions(SearchNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulationSnapshot snapshot = node.Snapshot;
        CompletedStateReadView? view = ReadCompactPolicyState(snapshot);
        CombatPredictionSimulator simulator = view?.EvaluationContext ?? snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            return [];

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = view?.Hand ?? playerState.Hand.Cards;
        List<PreparedCardAction> actions = new(hand.Count);
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PredictedCard card = hand[handIndex];
            string cardId = card.Preview.Id.Entry;
            int occurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(hand[priorIndex].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                    occurrence++;
            }
            if (!simulatedCombat.CanPlayCardAtResources(simulator, card, view?.Energy, snapshot.Stars, out _, out _))
                continue;
            StateFingerprint playableKey = BuildPlayableCardKey(card);
            bool duplicate = false;
            for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
            {
                if (seenCards[seenIndex] == playableKey)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                _run.DuplicateCardBranchesPruned++;
                continue;
            }
            seenCards[seenCardCount++] = playableKey;
            string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
            bool requiresUnsupportedExistingChoice =
                CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview);
            PlanCardChoice? requiredEmptyChoice =
                CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
            int cardStateOccurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(
                        CardChoiceSupport.ChoiceCardKey(hand[priorIndex]),
                        cardStateKey,
                        StringComparison.Ordinal))
                {
                    cardStateOccurrence++;
                }
            }
            foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator, view))
            {
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                PlanAction planAction = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
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
                actions.Add(new PreparedCardAction(
                    planAction,
                    card.Preview.Type,
                    target?.CombatId,
                    requiresUnsupportedExistingChoice,
                    requiredEmptyChoice));
            }
        }
        return actions;
    }

    private List<PreparedPotionAction> PreparePotionActions(SearchNode node)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || _maximumPotionUses != null
                && ExplicitPotionUseCount(node) >= _maximumPotionUses.Value)
        {
            return [];
        }

        CompletedStateReadView? view = ReadCompactPolicyState(snapshot);
        CombatPredictionSimulator simulator = view?.EvaluationContext ?? snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        List<PreparedPotionAction> actions = [];
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !AllowsPotionUse(potionSlot, potion.Id.Entry)
                || PotionUsePolicy.RequiresOpeningUse(potion)
                    && node.HasNonPotionAction)
            {
                continue;
            }

            foreach ((int targetIndex, Creature? target) in TargetsForPotion(potion, simulator, view))
            {
                PlanAction baseAction = new(
                    PlanActionKind.UsePotion,
                    node.Turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: target?.CombatId,
                    TargetName: displayNames.Creature(target),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: displayNames.Potion(potion));
                actions.Add(new PreparedPotionAction(baseAction, potion));
            }
        }
        return actions;
    }
}
