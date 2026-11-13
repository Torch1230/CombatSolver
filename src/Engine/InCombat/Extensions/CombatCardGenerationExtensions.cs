using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Extensions;

internal static class CombatCardGenerationExtensions
{
    public static IEnumerable<CardModel> FilterForCombatAndPlayerCount(
        this IEnumerable<CardModel> cards,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        IEnumerable<CardModel> combatCards = CardFactory.FilterForCombat(cards);
        return multiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly
            ? combatCards.Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            : combatCards.Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly);
    }

    // Mirrors CardFactory.GetDistinctForCombat, but does not create cards for the player.
    public static IEnumerable<CardModel> TakeRandomDistinctForCombat(
        this IEnumerable<CardModel> cards,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint, CombatRulesSnapshot rules)
    {
        return cards.FilterForCombatAndPlayerCount(multiplayerConstraint).Where(rules.Allows).TakeRandom(count, rng);
    }

    // Mirrors CardFactory.GetForCombat, but does not create cards for the player.
    public static IEnumerable<CardModel> TakeRandomForCombat(
        this IEnumerable<CardModel> cards,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint, CombatRulesSnapshot rules)
    {
        var options = cards.FilterForCombatAndPlayerCount(multiplayerConstraint).Where(rules.Allows).ToList();
        if (options.Count == 0)
        {
            return [];
        }

        List<CardModel> results = [];
        for (var i = 0; i < count; i++)
        {
            results.Add(rng.NextItem(options)!);
        }

        return results;
    }

    // Mirrors CardFactory.GetDistinctForCombat, but returns PredictedCard instead of CardModel.
    public static IEnumerable<PredictedCard> GetDistinctForCombat(
        this IEnumerable<CardModel> cards,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint, CombatRulesSnapshot rules)
    {
        return cards
            .TakeRandomDistinctForCombat(player, count, rng, multiplayerConstraint, rules)
            .Select(card => PredictedCard.Create(card, player));
    }

    public static IEnumerable<PredictedCard> GetDistinctUnlockedColorlessForCombat(
        this CombatPredictionSimulator simulator,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint,
        Func<CardModel, bool>? filter = null)
    {
        CardPoolModel colorlessPool = ModelDb.CardPool<ColorlessCardPool>();
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleCards(
                player,
                colorlessPool,
                multiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            // Keep the upstream TakeRandom/UnstableShuffle RNG behavior and create a fresh
            // prediction-owned card for every branch. Only eligibility filtering is cached;
            // the caller-specific predicate stays before random selection, same as fallback.
            IEnumerable<CardModel> candidates = filter == null
                ? cached
                : cached.Where(filter);
            return candidates
                .TakeRandom(count, rng)
                .Select(card => PredictedCard.Create(card, player));
        }

        IEnumerable<CardModel> unlocked = player.GetUnlockedCards(colorlessPool, multiplayerConstraint);
        if (filter != null)
            unlocked = unlocked.Where(filter);
        return unlocked.GetDistinctForCombat(
            player,
            count,
            rng,
            multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    public static IEnumerable<PredictedCard> GetDistinctUnlockedCharacterCardsForCombat(
        this CombatPredictionSimulator simulator,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint,
        Func<CardModel, bool>? filter = null)
    {
        CardPoolModel characterPool = player.Character.CardPool;
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleAllCharacterCards(
                player,
                characterPool,
                multiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            // Root eligibility is already combat/player-count filtered; the caller predicate
            // stays before random selection so candidate order and RNG use match fallback.
            IEnumerable<CardModel> candidates = filter == null
                ? cached
                : cached.Where(filter);
            return candidates
                .TakeRandom(count, rng)
                .Select(card => PredictedCard.Create(card, player));
        }

        IEnumerable<CardModel> unlocked = player.GetUnlockedCharacterCards(multiplayerConstraint);
        if (filter != null)
            unlocked = unlocked.Where(filter);
        return unlocked.GetDistinctForCombat(
            player,
            count,
            rng,
            multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    public static IEnumerable<PredictedCard> GetUnlockedCharacterCardsForCombat(
        this CombatPredictionSimulator simulator,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint,
        Func<CardModel, bool>? filter = null)
    {
        CardPoolModel characterPool = player.Character.CardPool;
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleAllCharacterCards(
                player,
                characterPool,
                multiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            IReadOnlyList<CardModel> candidates = filter == null
                ? cached
                : cached.Where(filter).ToArray();
            if (candidates.Count == 0)
                return [];
            // With-replacement selection must retain upstream NextItem order and count.
            List<PredictedCard> selected = new(count);
            for (int index = 0; index < count; index++)
                selected.Add(PredictedCard.Create(rng.NextItem(candidates)!, player));
            return selected;
        }

        IEnumerable<CardModel> unlocked = player.GetUnlockedCharacterCards(multiplayerConstraint);
        if (filter != null)
            unlocked = unlocked.Where(filter);
        return unlocked.GetForCombat(player, count, rng, multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    public static IEnumerable<PredictedCard> GetUnlockedCharacterAttacksForCombat(
        this CombatPredictionSimulator simulator,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        CardPoolModel characterPool = player.Character.CardPool;
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                characterPool,
                multiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            if (cached.Count == 0)
                return [];

            // Selection and mutable card creation stay branch-local. Rng.NextItem sees the
            // same ordered candidates and advances all five RNG fields exactly as the fallback.
            List<CardModel> selected = [];
            for (int index = 0; index < count; index++)
                selected.Add(rng.NextItem(cached)!);
            return selected.Select(card => PredictedCard.Create(card, player));
        }

        return player.GetUnlockedCharacterCards(multiplayerConstraint)
            .Where(static card => card.Type == CardType.Attack)
            .GetForCombat(player, count, rng, multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    public static IEnumerable<PredictedCard> GetDistinctUnlockedCharacterAttacksForCombat(
        this CombatPredictionSimulator simulator,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        CardPoolModel characterPool = player.Character.CardPool;
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                characterPool,
                multiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            // Distinct selection must retain TakeRandom's shuffle and RNG consumption;
            // the with-replacement NextItem path is a different contract, even for one card.
            return cached.AsEnumerable()
                .TakeRandom(count, rng)
                .Select(card => PredictedCard.Create(card, player));
        }

        return player.GetUnlockedCharacterCards(multiplayerConstraint)
            .Where(static card => card.Type == CardType.Attack)
            .GetDistinctForCombat(player, count, rng, multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    // Mirrors CardFactory.GetDefaultTransformationOptions' pool choice, then reuses the root's
    // already-computed unlock filtering. Rng.NextItem still receives the same ordered array:
    // CardFactory.GetFilteredTransformationOptions materializes before selecting, and the cached
    // array is the identical upstream GetUnlockedCards sequence for the same pool and unlock state.
    public static CardModel CreateRandomCardForTransform(
        this CombatPredictionSimulator simulator,
        CardModel original,
        bool isInCombat,
        Rng rng)
    {
        CardPoolModel pool = TransformationOptionPool(original);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        IEnumerable<CardModel> options;
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootUnlockedTransformationCards(
                original.Owner,
                pool,
                combat.CardMultiplayerConstraint,
                out IReadOnlyList<CardModel>? cached))
        {
            options = cached;
        }
        else options = pool.GetUnlockedCards(original.Owner.UnlockState, combat.CardMultiplayerConstraint);
        if (original.Rarity is not (CardRarity.Status or CardRarity.Curse))
            options = options.Where(card => card.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare);
        if (isInCombat) options = options.Where(card => card.CanBeGeneratedInCombat);
        CardModel[] eligible = options.Where(card => card.Id != original.Id && combat.Rules.Allows(card)
            && (combat.CardMultiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly
                ? card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly
                : card.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly)).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException($"No transformation options for {original.Id}.");
        return PredictedCard.Create(rng.NextItem(eligible)!, original.Owner).Preview;
    }

    // Mirrors the pool choice in CardFactory.GetDefaultTransformationOptions verbatim.
    private static CardPoolModel TransformationOptionPool(CardModel original)
        => original.Type != CardType.Quest
            && original.Rarity is not (CardRarity.Event or CardRarity.Ancient or CardRarity.Token)
                ? original.Pool
                : ModelDb.CardPool<ColorlessCardPool>();

    // Prepare exactly once for one power trigger. The fallback freezes the original
    // GetUnlockedCards result once, while its predicates are still evaluated per draw.
    public static CharacterGenerationCandidates PrepareCharacterGenerationCandidates(
        this CombatPredictionSimulator simulator,
        Player player,
        CardPoolModel pool,
        CharacterCombatGenerationPool selection,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        if (simulator.State.CombatState is ICombatPredictionCardGenerationPoolSnapshot snapshot
            && snapshot.TryGetRootEligibleCharacterCards(
                player, pool, multiplayerConstraint, selection, out IReadOnlyList<CardModel>? cached))
        {
            return new(cached, alreadyEligible: true, multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
        }
        IEnumerable<CardModel> unlocked = pool.GetUnlockedCards(player.UnlockState, multiplayerConstraint);
        IEnumerable<CardModel> options = selection switch
        {
            CharacterCombatGenerationPool.NonBasicAndAncient => unlocked.Where(
                static card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)),
            CharacterCombatGenerationPool.Powers => unlocked.Where(static card => card.Type == CardType.Power),
            CharacterCombatGenerationPool.Common => unlocked.Where(static card => card.Rarity == CardRarity.Common),
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        return new(options, alreadyEligible: false, multiplayerConstraint, ((SimulatedCombatState)simulator.State.CombatState).Rules);
    }

    internal readonly struct CharacterGenerationCandidates(
        IEnumerable<CardModel> options,
        bool alreadyEligible,
        CardMultiplayerConstraint multiplayerConstraint, CombatRulesSnapshot rules)
    {
        public IEnumerable<PredictedCard> GetDistinctForCombat(Player player, int count, Rng rng)
            => (alreadyEligible ? options : options.FilterForCombatAndPlayerCount(multiplayerConstraint).Where(rules.Allows))
                .TakeRandom(count, rng)
                .Select(card => PredictedCard.Create(card, player));
    }

    // Mirrors CardFactory.GetForCombat, but returns PredictedCard instead of CardModel.
    public static IEnumerable<PredictedCard> GetForCombat(
        this IEnumerable<CardModel> cards,
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint, CombatRulesSnapshot rules)
    {
        return cards
            .TakeRandomForCombat(player, count, rng, multiplayerConstraint, rules)
            .Select(card => PredictedCard.Create(card, player));
    }
}
