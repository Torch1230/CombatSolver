using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;

namespace CombatSolver;

/// <summary>
/// Root-scoped, read-only projections of canonical generation pools. Random selection and
/// prediction-owned card creation deliberately remain branch-local.
/// </summary>
internal sealed class RootCombatCardGenerationPoolSnapshot
{
    private sealed record NativeCharacterGenerationPoolEntry(
        object CharacterIdentity,
        CardPoolModel Pool,
        object AllCardsIdentity,
        CardModel[] EligibleAttacks,
        CardModel[] NonBasicAndAncient,
        CardModel[] Powers,
        CardModel[] Common);

    private static readonly System.Reflection.Assembly NativeModelAssembly =
        typeof(CardModel).Assembly;
    private readonly CardPoolModel? _canonicalColorlessPool;
    private readonly object? _canonicalColorlessCardsIdentity;
    private readonly CardMultiplayerConstraint _multiplayerConstraint;
    private readonly IReadOnlyDictionary<Player, CardModel[]> _eligibleColorlessByPlayer;
    private readonly IReadOnlyDictionary<Player, NativeCharacterGenerationPoolEntry>
        _characterPoolsByPlayer;

    private RootCombatCardGenerationPoolSnapshot(
        CardPoolModel? canonicalColorlessPool,
        object? canonicalColorlessCardsIdentity,
        CardMultiplayerConstraint multiplayerConstraint,
        IReadOnlyDictionary<Player, CardModel[]> eligibleColorlessByPlayer,
        IReadOnlyDictionary<Player, NativeCharacterGenerationPoolEntry>
            characterPoolsByPlayer)
    {
        _canonicalColorlessPool = canonicalColorlessPool;
        _canonicalColorlessCardsIdentity = canonicalColorlessCardsIdentity;
        _multiplayerConstraint = multiplayerConstraint;
        _eligibleColorlessByPlayer = eligibleColorlessByPlayer;
        _characterPoolsByPlayer = characterPoolsByPlayer;
    }

    public static RootCombatCardGenerationPoolSnapshot Capture(
        IReadOnlyList<Player> players,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        CardPoolModel colorlessPool = ModelDb.CardPool<ColorlessCardPool>();
        IEnumerable<CardModel> allCards = colorlessPool.AllCards;
        if (colorlessPool.GetType() != typeof(ColorlessCardPool)
            || allCards is not CardModel[] canonicalCards
            || canonicalCards.Any(card =>
                card.IsMutable || card.GetType().Assembly != typeof(CardModel).Assembly))
        {
            return new RootCombatCardGenerationPoolSnapshot(
                canonicalColorlessPool: null,
                canonicalColorlessCardsIdentity: null,
                multiplayerConstraint,
                new Dictionary<Player, CardModel[]>(ReferenceEqualityComparer.Instance),
                new Dictionary<Player, NativeCharacterGenerationPoolEntry>(
                    ReferenceEqualityComparer.Instance));
        }

        Dictionary<Player, CardModel[]> eligibleByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        Dictionary<Player, NativeCharacterGenerationPoolEntry> characterPoolsByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        foreach (Player player in players)
        {
            eligibleByPlayer.Add(
                player,
                player.GetUnlockedCards(colorlessPool, multiplayerConstraint)
                    .FilterForCombatAndPlayerCount(multiplayerConstraint)
                    .ToArray());
            if (TryCaptureNativeCharacterGenerationPool(
                    player,
                    multiplayerConstraint,
                    out NativeCharacterGenerationPoolEntry characterAttacks))
            {
                characterPoolsByPlayer.Add(player, characterAttacks);
            }
        }

        return new RootCombatCardGenerationPoolSnapshot(
            colorlessPool,
            allCards,
            multiplayerConstraint,
            eligibleByPlayer,
            characterPoolsByPlayer);
    }

    public bool TryGetEligibleCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (_canonicalColorlessPool != null
            && ReferenceEquals(cardPool, _canonicalColorlessPool)
            && cardPool.GetType() == typeof(ColorlessCardPool)
            && ReferenceEquals(cardPool.AllCards, _canonicalColorlessCardsIdentity)
            && multiplayerConstraint == _multiplayerConstraint
            && _eligibleColorlessByPlayer.TryGetValue(player, out CardModel[]? eligible))
        {
            cards = eligible;
            return true;
        }

        cards = [];
        return false;
    }

    public bool TryGetEligibleCharacterAttackCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (TryGetNativeCharacterEntry(player, cardPool, multiplayerConstraint, out var entry))
        {
            cards = entry!.EligibleAttacks;
            return true;
        }
        cards = [];
        return false;
    }

    public bool TryGetEligibleCharacterCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        CharacterCombatGenerationPool selection,
        out IReadOnlyList<CardModel> cards)
    {
        if (TryGetNativeCharacterEntry(player, cardPool, multiplayerConstraint, out var entry))
        {
            cards = selection switch
            {
                CharacterCombatGenerationPool.NonBasicAndAncient => entry!.NonBasicAndAncient,
                CharacterCombatGenerationPool.Powers => entry!.Powers,
                CharacterCombatGenerationPool.Common => entry!.Common,
                _ => throw new ArgumentOutOfRangeException(nameof(selection)),
            };
            return true;
        }
        cards = [];
        return false;
    }

    private bool TryGetNativeCharacterEntry(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeCharacterGenerationPoolEntry? entry)
    {
        entry = null;
        if (multiplayerConstraint == _multiplayerConstraint
            && _characterPoolsByPlayer.TryGetValue(
                player,
                out entry)
            && ReferenceEquals(player.Character, entry.CharacterIdentity)
            && ReferenceEquals(cardPool, entry.Pool)
            && ReferenceEquals(player.Character.CardPool, entry.Pool)
            && !player.Character.IsMutable
            && player.Character.GetType().Assembly == NativeModelAssembly
            && !cardPool.IsMutable
            && !cardPool.IsMock
            && cardPool.GetType().Assembly == NativeModelAssembly
            && ReferenceEquals(cardPool, ModelDb.GetById<CardPoolModel>(cardPool.Id))
            && ReferenceEquals(cardPool.AllCards, entry.AllCardsIdentity))
        {
            return true;
        }

        entry = null;
        return false;
    }

    private static bool TryCaptureNativeCharacterGenerationPool(
        Player player,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeCharacterGenerationPoolEntry entry)
    {
        entry = null!;
        CardPoolModel cardPool = player.Character.CardPool;
        if (player.Character.GetType().Assembly != NativeModelAssembly
            || player.Character.IsMutable
            || !TryGetNativeCanonicalCharacterPoolCards(cardPool, out CardModel[] allCards))
        {
            return false;
        }

        // Preserve Metamorphosis' source order exactly: unlock filtering, then Attack,
        // then the upstream in-combat and player-count predicates.
        CardModel[] eligibleCards = player
            .GetUnlockedCards(cardPool, multiplayerConstraint)
            .Where(static card => card.Type == CardType.Attack)
            .FilterForCombatAndPlayerCount(multiplayerConstraint)
            .ToArray();
        // These callers use CardPoolModel.GetUnlockedCards directly. Preserve that source
        // and each caller's predicate before the original combat/player-count filtering.
        CardModel[] nonBasicAndAncient = cardPool.GetUnlockedCards(player.UnlockState, multiplayerConstraint)
            .Where(static card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient))
            .FilterForCombatAndPlayerCount(multiplayerConstraint).ToArray();
        CardModel[] powers = cardPool.GetUnlockedCards(player.UnlockState, multiplayerConstraint)
            .Where(static card => card.Type == CardType.Power)
            .FilterForCombatAndPlayerCount(multiplayerConstraint).ToArray();
        CardModel[] common = cardPool.GetUnlockedCards(player.UnlockState, multiplayerConstraint)
            .Where(static card => card.Rarity == CardRarity.Common)
            .FilterForCombatAndPlayerCount(multiplayerConstraint).ToArray();
        HashSet<CardModel> canonicalPoolCards = new(
            allCards,
            ReferenceEqualityComparer.Instance);
        if (eligibleCards.Concat(nonBasicAndAncient).Concat(powers).Concat(common).Any(card =>
                !canonicalPoolCards.Contains(card)
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance)))
        {
            return false;
        }

        entry = new NativeCharacterGenerationPoolEntry(
            player.Character,
            cardPool,
            allCards,
            eligibleCards,
            nonBasicAndAncient,
            powers,
            common);
        return true;
    }

    internal static bool CanCacheNativeCharacterPoolForTesting(CardPoolModel cardPool)
        => TryGetNativeCanonicalCharacterPoolCards(cardPool, out _);

    private static bool TryGetNativeCanonicalCharacterPoolCards(
        CardPoolModel cardPool,
        out CardModel[] cards)
    {
        if (cardPool.GetType().Assembly == NativeModelAssembly
            && !cardPool.IsMutable
            && !cardPool.IsMock
            && !cardPool.IsColorless
            && ReferenceEquals(cardPool, ModelDb.GetById<CardPoolModel>(cardPool.Id))
            && cardPool.AllCards is CardModel[] allCards
            && AllCardsAreNativeCanonical(allCards))
        {
            cards = allCards;
            return true;
        }

        cards = [];
        return false;
    }

    private static bool AllCardsAreNativeCanonical(IEnumerable<CardModel> cards)
    {
        foreach (CardModel card in cards)
        {
            if (card is null
                || card.GetType().Assembly != NativeModelAssembly
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance)
                || !ReferenceEquals(card, ModelDb.GetById<CardModel>(card.Id)))
            {
                return false;
            }
        }
        return true;
    }
}
