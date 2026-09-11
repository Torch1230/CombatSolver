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
    private sealed record NativeCharacterPoolEntry(
        object CharacterIdentity,
        CardPoolModel Pool,
        object AllCardsIdentity,
        CardModel[] EligibleCards,
        CardModel[] EligibleAttacks);

    private static readonly System.Reflection.Assembly NativeModelAssembly =
        typeof(CardModel).Assembly;
    private readonly CardPoolModel? _canonicalColorlessPool;
    private readonly object? _canonicalColorlessCardsIdentity;
    private readonly CardMultiplayerConstraint _multiplayerConstraint;
    private readonly IReadOnlyDictionary<Player, CardModel[]> _eligibleColorlessByPlayer;
    private readonly IReadOnlyDictionary<Player, NativeCharacterPoolEntry>
        _eligibleCharacterPoolsByPlayer;

    private RootCombatCardGenerationPoolSnapshot(
        CardPoolModel? canonicalColorlessPool,
        object? canonicalColorlessCardsIdentity,
        CardMultiplayerConstraint multiplayerConstraint,
        IReadOnlyDictionary<Player, CardModel[]> eligibleColorlessByPlayer,
        IReadOnlyDictionary<Player, NativeCharacterPoolEntry>
            eligibleCharacterPoolsByPlayer)
    {
        _canonicalColorlessPool = canonicalColorlessPool;
        _canonicalColorlessCardsIdentity = canonicalColorlessCardsIdentity;
        _multiplayerConstraint = multiplayerConstraint;
        _eligibleColorlessByPlayer = eligibleColorlessByPlayer;
        _eligibleCharacterPoolsByPlayer = eligibleCharacterPoolsByPlayer;
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
                new Dictionary<Player, NativeCharacterPoolEntry>(
                    ReferenceEqualityComparer.Instance));
        }

        Dictionary<Player, CardModel[]> eligibleByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        Dictionary<Player, NativeCharacterPoolEntry> eligibleCharacterPoolsByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        foreach (Player player in players)
        {
            eligibleByPlayer.Add(
                player,
                player.GetUnlockedCards(colorlessPool, multiplayerConstraint)
                    .FilterForCombatAndPlayerCount(multiplayerConstraint)
                    .ToArray());
            if (TryCaptureNativeCharacterPool(
                    player,
                    multiplayerConstraint,
                    out NativeCharacterPoolEntry characterPool))
            {
                eligibleCharacterPoolsByPlayer.Add(player, characterPool);
            }
        }

        return new RootCombatCardGenerationPoolSnapshot(
            colorlessPool,
            allCards,
            multiplayerConstraint,
            eligibleByPlayer,
            eligibleCharacterPoolsByPlayer);
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

    public bool TryGetEligibleCharacterCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (TryGetNativeCharacterEntry(player, cardPool, multiplayerConstraint, out var entry))
        {
            cards = entry.EligibleCards;
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
            cards = entry.EligibleAttacks;
            return true;
        }
        cards = [];
        return false;
    }

    private bool TryGetNativeCharacterEntry(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeCharacterPoolEntry entry)
    {
        if (multiplayerConstraint == _multiplayerConstraint
            && _eligibleCharacterPoolsByPlayer.TryGetValue(player, out var captured)
            && ReferenceEquals(player.Character, captured.CharacterIdentity)
            && ReferenceEquals(cardPool, captured.Pool)
            && ReferenceEquals(player.Character.CardPool, captured.Pool)
            && !player.Character.IsMutable
            && player.Character.GetType().Assembly == NativeModelAssembly
            && !cardPool.IsMutable
            && !cardPool.IsMock
            && cardPool.GetType().Assembly == NativeModelAssembly
            && ReferenceEquals(cardPool, ModelDb.GetById<CardPoolModel>(cardPool.Id))
            && ReferenceEquals(cardPool.AllCards, captured.AllCardsIdentity))
        {
            entry = captured;
            return true;
        }
        entry = null!;
        return false;
    }

    private static bool TryCaptureNativeCharacterPool(
        Player player,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeCharacterPoolEntry entry)
    {
        entry = null!;
        CardPoolModel cardPool = player.Character.CardPool;
        if (player.Character.GetType().Assembly != NativeModelAssembly
            || player.Character.IsMutable
            || !TryGetNativeCanonicalCharacterPoolCards(cardPool, out CardModel[] allCards))
        {
            return false;
        }

        // Freeze the complete native combat pool once. Combat eligibility already
        // excludes Basic/Ancient/Event, including CallOfTheVoid's source exclusions.
        // Attack selection is a stable projection of these same canonical models.
        CardModel[] eligibleCards = player
            .GetUnlockedCards(cardPool, multiplayerConstraint)
            .FilterForCombatAndPlayerCount(multiplayerConstraint)
            .ToArray();
        HashSet<CardModel> canonicalPoolCards = new(
            allCards,
            ReferenceEqualityComparer.Instance);
        if (eligibleCards.Any(card =>
                !canonicalPoolCards.Contains(card)
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance)))
        {
            return false;
        }

        entry = new NativeCharacterPoolEntry(
            player.Character,
            cardPool,
            allCards,
            eligibleCards,
            eligibleCards.Where(static card => card.Type == CardType.Attack).ToArray());
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
