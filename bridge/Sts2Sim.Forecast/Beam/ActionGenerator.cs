using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace CombatSolver.Beam;

/// <summary>Enumerates Core-legal card, potion, and end-turn candidates. Method: CombatBeamSolver.Expansion.cs @ d231e9e5.</summary>
internal static class ActionGenerator
{
    private static readonly IReadOnlyList<IReadOnlyList<int>> NoChoices = [];
    private static readonly IReadOnlyList<string> NoChoiceCards = [];

    public static IReadOnlyList<PlannedAction> Generate(CombatState state, CombatPolicy? policy = null)
    {
        policy ??= CombatPolicy.Default;
        Player player = state.Players[0];
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        var actions = new List<PlannedAction>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CardModel card in player.PlayerCombatState!.Hand.Cards)
        {
            string id = card.Id.Entry;
            int occurrence = seen.GetValueOrDefault(id);
            seen[id] = occurrence + 1;
            if (!card.CanPlay(out _)) continue;
            foreach (int index in TargetIndices(state, player, card.TargetType))
                actions.Add(new PlannedAction(turn, "PlayCard", id, occurrence, index,
                    null, -1, NoChoices, NoChoiceCards));
        }

        AppendPotionAndEndTurnActions(state, player, turn, policy, actions);
        return actions;
    }

    public static IReadOnlyList<PlannedAction> GenerateForForecast(CombatState state, CombatPolicy? policy = null)
    {
        policy ??= CombatPolicy.Default;
        Player player = state.Players[0];
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        var actions = new List<PlannedAction>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        int previousPlayableIndex = -1;
        CombatStateDescriptionDigest previousPlayableKey = default;

        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            CardModel card = hand[handIndex];
            string id = card.Id.Entry;
            int occurrence = seen.GetValueOrDefault(id);
            seen[id] = occurrence + 1;
            if (!card.CanPlay(out _)) continue;

            CombatStateDescriptionDigest playableKey = ForecastRetentionKey.ComputePlayableCardKey(state, card);
            if (previousPlayableIndex >= 0
                && playableKey == previousPlayableKey
                && HaveEquivalentHandRemovalOrder(state, hand, previousPlayableIndex, handIndex, playableKey))
            {
                continue;
            }

            previousPlayableIndex = handIndex;
            previousPlayableKey = playableKey;
            foreach (int index in TargetIndices(state, player, card.TargetType))
                actions.Add(new PlannedAction(turn, "PlayCard", id, occurrence, index,
                    null, -1, NoChoices, NoChoiceCards));
        }

        AppendPotionAndEndTurnActions(state, player, turn, policy, actions);
        return actions;
    }

    private static bool HaveEquivalentHandRemovalOrder(
        CombatState state,
        IReadOnlyList<CardModel> hand,
        int firstIndex,
        int secondIndex,
        CombatStateDescriptionDigest playableKey)
    {
        for (int index = firstIndex + 1; index < secondIndex; index++)
        {
            if (ForecastRetentionKey.ComputePlayableCardKey(state, hand[index]) != playableKey)
                return false;
        }

        return true;
    }

    private static void AppendPotionAndEndTurnActions(
        CombatState state,
        Player player,
        int turn,
        CombatPolicy policy,
        List<PlannedAction> actions)
    {
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.PotionSlots[slot];
            if (potion is null || !policy.EffectivePotionStrategy.AllowsExplicitUse(
                    slot, potion.Id.Entry, policy.PotionPolicy)) continue;
            foreach (int index in TargetIndices(state, player, potion.TargetType))
            {
                Creature? target = ActionExecutor.ResolveTarget(state, player, potion.TargetType, index);
                if (PotionCmd.CanUseManually(state, player, potion, target))
                    actions.Add(new PlannedAction(turn, "UsePotion", null, 0, index,
                        potion.Id.Entry, slot, NoChoices, NoChoiceCards));
            }
        }

        actions.Add(new PlannedAction(turn, "EndTurn", null, 0, -1, null, -1,
            NoChoices, NoChoiceCards));
    }

    internal static bool IsLegalTarget(CombatState state, Player player, TargetType type, int index) =>
        TargetIndices(state, player, type).Contains(index);

    private static IEnumerable<int> TargetIndices(CombatState state, Player player, TargetType type)
    {
        if (type == TargetType.AnyEnemy)
        {
            for (int index = 0; index < state.Enemies.Count; index++)
                if (state.Enemies[index].IsHittable) yield return index;
        }
        else if (type == TargetType.AnyAlly)
        {
            // The current acceptance corpus is single-player; non-player allies use negative indices.
            for (int index = 0; index < state.Allies.Count; index++)
                if (state.Allies[index].IsAlive && !ReferenceEquals(state.Allies[index], player.Creature))
                    yield return -2 - index;
        }
        else if (type == TargetType.AnyPlayer)
        {
            yield return -1;
        }
        else if (type == TargetType.Osty)
        {
            for (int index = 0; index < state.Enemies.Count; index++)
                if (state.Enemies[index].IsAlive) yield return index;
            yield return -1;
        }
        else
        {
            yield return -1;
        }
    }
}
