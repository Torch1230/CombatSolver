using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace CombatSolver;

/// <summary>
/// Root-scoped cache of the unfiltered <see cref="CardPoolModel.GetUnlockedCards"/> result that
/// <c>CardFactory.GetDefaultTransformationOptions</c> would recompute for every transform.
/// The cached array is exactly the upstream unlock-filtered sequence, in upstream order: the
/// per-branch rarity/combat/id/player-count filtering still runs in <c>CardFactory</c>, so
/// <c>Rng.NextItem</c> observes the same ordered candidates as the uncached path.
/// Random selection and card creation deliberately remain branch-local.
/// </summary>
internal sealed class RootCombatTransformationPoolSnapshot
{
    private sealed record NativeTransformationPoolEntry(
        CardPoolModel Pool,
        object AllCardsIdentity,
        CardModel[] UnlockedCards);

    private static readonly System.Reflection.Assembly NativeModelAssembly =
        typeof(CardModel).Assembly;

    private readonly CardMultiplayerConstraint _multiplayerConstraint;
    private readonly CardPoolModel? _canonicalColorlessPool;
    private readonly object? _canonicalColorlessCardsIdentity;
    private readonly CardModel[]? _colorlessUnlockedCards;
    private readonly IReadOnlyDictionary<Player, NativeTransformationPoolEntry>
        _characterPoolsByPlayer;

    private RootCombatTransformationPoolSnapshot(
        CardMultiplayerConstraint multiplayerConstraint,
        CardPoolModel? canonicalColorlessPool,
        object? canonicalColorlessCardsIdentity,
        CardModel[]? colorlessUnlockedCards,
        IReadOnlyDictionary<Player, NativeTransformationPoolEntry> characterPoolsByPlayer)
    {
        _multiplayerConstraint = multiplayerConstraint;
        _canonicalColorlessPool = canonicalColorlessPool;
        _canonicalColorlessCardsIdentity = canonicalColorlessCardsIdentity;
        _colorlessUnlockedCards = colorlessUnlockedCards;
        _characterPoolsByPlayer = characterPoolsByPlayer;
    }

    public static RootCombatTransformationPoolSnapshot Capture(
        IReadOnlyList<Player> players,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        Dictionary<Player, NativeTransformationPoolEntry> characterPoolsByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        foreach (Player player in players)
        {
            if (TryCaptureCharacterTransformationPool(
                    player,
                    multiplayerConstraint,
                    out NativeTransformationPoolEntry entry))
            {
                characterPoolsByPlayer.Add(player, entry);
            }
        }

        // The colorless fallback is the same canonical pool for every player, so one entry
        // covers every player's PostTransform/transform-from-token path.
        CardPoolModel colorlessPool = ModelDb.CardPool<ColorlessCardPool>();
        if (players.Count == 0
            || !TryCaptureCanonicalPool(colorlessPool, out CardModel[] colorlessCards))
        {
            return new RootCombatTransformationPoolSnapshot(
                multiplayerConstraint,
                canonicalColorlessPool: null,
                canonicalColorlessCardsIdentity: null,
                colorlessUnlockedCards: null,
                characterPoolsByPlayer);
        }

        Player firstPlayer = players[0];
        CardModel[] colorlessUnlocked =
            colorlessPool.GetUnlockedCards(firstPlayer.UnlockState, multiplayerConstraint).ToArray();
        return new RootCombatTransformationPoolSnapshot(
            multiplayerConstraint,
            colorlessPool,
            colorlessCards,
            AllCardsAreNativeCanonical(colorlessUnlocked) ? colorlessUnlocked : null,
            characterPoolsByPlayer);
    }

    /// <summary>
    /// Returns the root's unlock-filtered, order-preserving candidate sequence for
    /// <paramref name="cardPool"/>, or <c>false</c> when the caller must take the uncached
    /// upstream path. Every divergence (mutable or third-party pool, foreign player or
    /// constraint, changed <c>AllCards</c> identity) deliberately falls back rather than guessing.
    /// </summary>
    public bool TryGetUnlockedTransformationCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (multiplayerConstraint == _multiplayerConstraint && !cardPool.IsMutable)
        {
            if (_colorlessUnlockedCards != null
                && ReferenceEquals(cardPool, _canonicalColorlessPool)
                && ReferenceEquals(cardPool.AllCards, _canonicalColorlessCardsIdentity))
            {
                cards = _colorlessUnlockedCards;
                return true;
            }

            if (_characterPoolsByPlayer.TryGetValue(player, out NativeTransformationPoolEntry? entry)
                && ReferenceEquals(cardPool, entry.Pool)
                && ReferenceEquals(cardPool.AllCards, entry.AllCardsIdentity))
            {
                cards = entry.UnlockedCards;
                return true;
            }
        }

        cards = [];
        return false;
    }

    private static bool TryCaptureCharacterTransformationPool(
        Player player,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeTransformationPoolEntry entry)
    {
        entry = null!;
        CardPoolModel pool = player.Character.CardPool;
        if (!TryCaptureCanonicalPool(pool, out CardModel[] allCards))
        {
            return false;
        }

        CardModel[] unlocked = pool.GetUnlockedCards(player.UnlockState, multiplayerConstraint)
            .ToArray();
        if (!AllCardsAreNativeCanonical(unlocked))
        {
            return false;
        }

        entry = new NativeTransformationPoolEntry(pool, allCards, unlocked);
        return true;
    }

    private static bool TryCaptureCanonicalPool(CardPoolModel pool, out CardModel[] allCards)
    {
        if (pool.GetType().Assembly == NativeModelAssembly
            && !pool.IsMutable
            && !pool.IsMock
            && ReferenceEquals(pool, ModelDb.GetById<CardPoolModel>(pool.Id))
            && pool.AllCards is CardModel[] cards
            && AllCardsAreNativeCanonical(cards))
        {
            allCards = cards;
            return true;
        }

        allCards = [];
        return false;
    }

    private static bool AllCardsAreNativeCanonical(IEnumerable<CardModel> cards)
    {
        foreach (CardModel card in cards)
        {
            if (card is null
                || card.GetType().Assembly != NativeModelAssembly
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance))
            {
                return false;
            }
        }

        return true;
    }
}
