using System.Buffers;
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

    [ThreadStatic]
    private static Dictionary<string, int>? _seenScratch;

    public static IReadOnlyList<PlannedAction> Generate(CombatState state, CombatPolicy? policy = null)
    {
        var actions = new List<PlannedAction>(16);
        GenerateInto(state, policy ?? CombatPolicy.Default, actions);
        return actions;
    }

    public static void GenerateInto(CombatState state, CombatPolicy policy, List<PlannedAction> destination)
    {
        destination.Clear();
        Player player = state.Players[0];
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        if (hand.Count <= 32)
        {
            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                CardModel card = hand[handIndex];
                if (!card.CanPlay(out _)) continue;
                string id = card.Id.Entry;
                int occurrence = CountPrecedingMatchingId(hand, handIndex, id);
                EmitCardActions(state, player, turn, id, occurrence, card.TargetType, destination);
            }
        }
        else
        {
            Dictionary<string, int> seen = AcquireSeenScratch();
            try
            {
                for (int handIndex = 0; handIndex < hand.Count; handIndex++)
                {
                    CardModel card = hand[handIndex];
                    string id = card.Id.Entry;
                    int occurrence = seen.GetValueOrDefault(id);
                    seen[id] = occurrence + 1;
                    if (!card.CanPlay(out _)) continue;
                    EmitCardActions(state, player, turn, id, occurrence, card.TargetType, destination);
                }
            }
            finally
            {
                ReleaseSeenScratch(seen);
            }
        }

        AppendPotionAndEndTurnActions(state, player, turn, policy, destination);
    }

    public static IReadOnlyList<PlannedAction> GenerateForForecast(CombatState state, CombatPolicy? policy = null)
    {
        var actions = new List<PlannedAction>(16);
        GenerateForForecastInto(state, policy ?? CombatPolicy.Default, actions);
        return actions;
    }

    public static void GenerateForForecastInto(
        CombatState state,
        CombatPolicy policy,
        List<PlannedAction> destination)
    {
        destination.Clear();
        Player player = state.Players[0];
        int turn = player.PlayerCombatState?.TurnNumber ?? 0;
        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        int handCount = hand.Count;
        int previousPlayableIndex = -1;
        CombatStateDescriptionDigest previousPlayableKey = default;
        bool hasStateRequiresDeckVersion = false;
        bool stateRequiresDeckVersion = false;

        if (handCount <= 32)
        {
            Span<CombatStateDescriptionDigest> cachedKeys = handCount > 0
                ? stackalloc CombatStateDescriptionDigest[handCount]
                : default;
            Span<bool> hasCachedKey = handCount > 0
                ? stackalloc bool[handCount]
                : default;
            if (handCount > 0)
            {
                cachedKeys.Clear();
                hasCachedKey.Clear();
            }

            for (int handIndex = 0; handIndex < handCount; handIndex++)
            {
                CardModel card = hand[handIndex];
                if (!card.CanPlay(out _)) continue;
                string id = card.Id.Entry;
                int occurrence = CountPrecedingMatchingId(hand, handIndex, id);

                CombatStateDescriptionDigest playableKey = GetOrComputePlayableKey(
                    state,
                    card,
                    handIndex,
                    cachedKeys,
                    hasCachedKey,
                    ref hasStateRequiresDeckVersion,
                    ref stateRequiresDeckVersion);

                if (previousPlayableIndex >= 0
                    && playableKey == previousPlayableKey
                    && HaveEquivalentHandRemovalOrder(
                        state,
                        hand,
                        previousPlayableIndex,
                        handIndex,
                        playableKey,
                        cachedKeys,
                        hasCachedKey,
                        ref hasStateRequiresDeckVersion,
                        ref stateRequiresDeckVersion))
                {
                    continue;
                }

                previousPlayableIndex = handIndex;
                previousPlayableKey = playableKey;
                EmitCardActions(state, player, turn, id, occurrence, card.TargetType, destination);
            }
        }
        else
        {
            Dictionary<string, int> seen = AcquireSeenScratch();
            CombatStateDescriptionDigest[] cachedKeys = ArrayPool<CombatStateDescriptionDigest>.Shared.Rent(handCount);
            bool[] hasCachedKey = ArrayPool<bool>.Shared.Rent(handCount);
            Array.Clear(hasCachedKey, 0, handCount);
            try
            {
                for (int handIndex = 0; handIndex < handCount; handIndex++)
                {
                    CardModel card = hand[handIndex];
                    string id = card.Id.Entry;
                    int occurrence = seen.GetValueOrDefault(id);
                    seen[id] = occurrence + 1;
                    if (!card.CanPlay(out _)) continue;

                    CombatStateDescriptionDigest playableKey = GetOrComputePlayableKey(
                        state,
                        card,
                        handIndex,
                        cachedKeys,
                        hasCachedKey,
                        ref hasStateRequiresDeckVersion,
                        ref stateRequiresDeckVersion);

                    if (previousPlayableIndex >= 0
                        && playableKey == previousPlayableKey
                        && HaveEquivalentHandRemovalOrder(
                            state,
                            hand,
                            previousPlayableIndex,
                            handIndex,
                            playableKey,
                            cachedKeys,
                            hasCachedKey,
                            ref hasStateRequiresDeckVersion,
                            ref stateRequiresDeckVersion))
                    {
                        continue;
                    }

                    previousPlayableIndex = handIndex;
                    previousPlayableKey = playableKey;
                    EmitCardActions(state, player, turn, id, occurrence, card.TargetType, destination);
                }
            }
            finally
            {
                ArrayPool<CombatStateDescriptionDigest>.Shared.Return(cachedKeys);
                ArrayPool<bool>.Shared.Return(hasCachedKey);
                ReleaseSeenScratch(seen);
            }
        }

        AppendPotionAndEndTurnActions(state, player, turn, policy, destination);
    }

    private static int CountPrecedingMatchingId(IReadOnlyList<CardModel> hand, int handIndex, string id)
    {
        int occurrence = 0;
        for (int j = 0; j < handIndex; j++)
        {
            if (string.Equals(hand[j].Id.Entry, id, StringComparison.Ordinal))
                occurrence++;
        }
        return occurrence;
    }

    private static CombatStateDescriptionDigest GetOrComputePlayableKey(
        CombatState state,
        CardModel card,
        int handIndex,
        Span<CombatStateDescriptionDigest> cachedKeys,
        Span<bool> hasCachedKey,
        ref bool hasStateRequiresDeckVersion,
        ref bool stateRequiresDeckVersion)
    {
        if (hasCachedKey[handIndex])
            return cachedKeys[handIndex];

        bool requiresDeck = false;
        if (card.DeckVersion is not null)
        {
            if (DeckVersionAccessAnalyzer.CardOrEnchantmentReferencesDeckVersion(card))
            {
                requiresDeck = true;
            }
            else
            {
                if (!hasStateRequiresDeckVersion)
                {
                    stateRequiresDeckVersion = DeckVersionAccessAnalyzer.StateRequiresDeckVersionIdentity(state);
                    hasStateRequiresDeckVersion = true;
                }
                requiresDeck = stateRequiresDeckVersion;
            }
        }

        CombatStateDescriptionDigest key = ForecastRetentionKey.ComputePlayableCardKey(state, card, requiresDeck);
        cachedKeys[handIndex] = key;
        hasCachedKey[handIndex] = true;
        return key;
    }

    private static bool HaveEquivalentHandRemovalOrder(
        CombatState state,
        IReadOnlyList<CardModel> hand,
        int firstIndex,
        int secondIndex,
        CombatStateDescriptionDigest playableKey,
        Span<CombatStateDescriptionDigest> cachedKeys,
        Span<bool> hasCachedKey,
        ref bool hasStateRequiresDeckVersion,
        ref bool stateRequiresDeckVersion)
    {
        for (int index = firstIndex + 1; index < secondIndex; index++)
        {
            CombatStateDescriptionDigest midKey = GetOrComputePlayableKey(
                state,
                hand[index],
                index,
                cachedKeys,
                hasCachedKey,
                ref hasStateRequiresDeckVersion,
                ref stateRequiresDeckVersion);
            if (midKey != playableKey)
                return false;
        }

        return true;
    }

    private static void EmitCardActions(
        CombatState state,
        Player player,
        int turn,
        string cardId,
        int occurrence,
        TargetType targetType,
        List<PlannedAction> destination)
    {
        switch (targetType)
        {
            case TargetType.AnyEnemy:
            {
                IReadOnlyList<Creature> enemies = state.Enemies;
                for (int index = 0; index < enemies.Count; index++)
                {
                    if (enemies[index].IsHittable)
                        destination.Add(new PlannedAction(turn, "PlayCard", cardId, occurrence, index,
                            null, -1, NoChoices, NoChoiceCards));
                }
                break;
            }
            case TargetType.AnyAlly:
            {
                IReadOnlyList<Creature> allies = state.Allies;
                for (int index = 0; index < allies.Count; index++)
                {
                    if (allies[index].IsAlive && !ReferenceEquals(allies[index], player.Creature))
                        destination.Add(new PlannedAction(turn, "PlayCard", cardId, occurrence, -2 - index,
                            null, -1, NoChoices, NoChoiceCards));
                }
                break;
            }
            case TargetType.Osty:
            {
                IReadOnlyList<Creature> enemies = state.Enemies;
                for (int index = 0; index < enemies.Count; index++)
                {
                    if (enemies[index].IsAlive)
                        destination.Add(new PlannedAction(turn, "PlayCard", cardId, occurrence, index,
                            null, -1, NoChoices, NoChoiceCards));
                }
                destination.Add(new PlannedAction(turn, "PlayCard", cardId, occurrence, -1,
                    null, -1, NoChoices, NoChoiceCards));
                break;
            }
            default:
                destination.Add(new PlannedAction(turn, "PlayCard", cardId, occurrence, -1,
                    null, -1, NoChoices, NoChoiceCards));
                break;
        }
    }

    private static void AppendPotionAndEndTurnActions(
        CombatState state,
        Player player,
        int turn,
        CombatPolicy policy,
        List<PlannedAction> actions)
    {
        IReadOnlyList<PotionModel?> potionSlots = player.PotionSlots;
        for (int slot = 0; slot < potionSlots.Count; slot++)
        {
            PotionModel? potion = potionSlots[slot];
            if (potion is null || !policy.EffectivePotionStrategy.AllowsExplicitUse(
                    slot, potion.Id.Entry, policy.PotionPolicy)) continue;

            string potionId = potion.Id.Entry;
            switch (potion.TargetType)
            {
                case TargetType.AnyEnemy:
                {
                    IReadOnlyList<Creature> enemies = state.Enemies;
                    for (int index = 0; index < enemies.Count; index++)
                    {
                        if (!enemies[index].IsHittable) continue;
                        Creature? target = ActionExecutor.ResolveTarget(state, player, potion.TargetType, index);
                        if (PotionCmd.CanUseManually(state, player, potion, target))
                            actions.Add(new PlannedAction(turn, "UsePotion", null, 0, index,
                                potionId, slot, NoChoices, NoChoiceCards));
                    }
                    break;
                }
                case TargetType.AnyAlly:
                {
                    IReadOnlyList<Creature> allies = state.Allies;
                    for (int index = 0; index < allies.Count; index++)
                    {
                        if (!allies[index].IsAlive || ReferenceEquals(allies[index], player.Creature)) continue;
                        int targetIdx = -2 - index;
                        Creature? target = ActionExecutor.ResolveTarget(state, player, potion.TargetType, targetIdx);
                        if (PotionCmd.CanUseManually(state, player, potion, target))
                            actions.Add(new PlannedAction(turn, "UsePotion", null, 0, targetIdx,
                                potionId, slot, NoChoices, NoChoiceCards));
                    }
                    break;
                }
                case TargetType.Osty:
                {
                    IReadOnlyList<Creature> enemies = state.Enemies;
                    for (int index = 0; index < enemies.Count; index++)
                    {
                        if (!enemies[index].IsAlive) continue;
                        Creature? target = ActionExecutor.ResolveTarget(state, player, potion.TargetType, index);
                        if (PotionCmd.CanUseManually(state, player, potion, target))
                            actions.Add(new PlannedAction(turn, "UsePotion", null, 0, index,
                                potionId, slot, NoChoices, NoChoiceCards));
                    }
                    Creature? fallbackTarget = ActionExecutor.ResolveTarget(state, player, potion.TargetType, -1);
                    if (PotionCmd.CanUseManually(state, player, potion, fallbackTarget))
                        actions.Add(new PlannedAction(turn, "UsePotion", null, 0, -1,
                            potionId, slot, NoChoices, NoChoiceCards));
                    break;
                }
                default:
                {
                    Creature? target = ActionExecutor.ResolveTarget(state, player, potion.TargetType, -1);
                    if (PotionCmd.CanUseManually(state, player, potion, target))
                        actions.Add(new PlannedAction(turn, "UsePotion", null, 0, -1,
                            potionId, slot, NoChoices, NoChoiceCards));
                    break;
                }
            }
        }

        actions.Add(new PlannedAction(turn, "EndTurn", null, 0, -1, null, -1,
            NoChoices, NoChoiceCards));
    }

    internal static bool IsLegalTarget(CombatState state, Player player, TargetType type, int index) => type switch
    {
        TargetType.AnyEnemy => index >= 0 && index < state.Enemies.Count && state.Enemies[index].IsHittable,
        TargetType.AnyAlly => index <= -2 && -2 - index < state.Allies.Count
            && state.Allies[-2 - index].IsAlive && !ReferenceEquals(state.Allies[-2 - index], player.Creature),
        TargetType.AnyPlayer => index == -1,
        TargetType.Osty => index == -1 || (index >= 0 && index < state.Enemies.Count && state.Enemies[index].IsAlive),
        _ => index == -1,
    };

    private static Dictionary<string, int> AcquireSeenScratch()
    {
        Dictionary<string, int>? seen = _seenScratch;
        _seenScratch = null;
        return seen ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private static void ReleaseSeenScratch(Dictionary<string, int> seen)
    {
        seen.Clear();
        _seenScratch = seen;
    }
}
