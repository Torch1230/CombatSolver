using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed class SimCardPile
{
    private readonly List<PredictedCard> _cards;
    private bool _hasCachedFingerprint;
    private ulong _cachedFingerprintFirst;
    private ulong _cachedFingerprintSecond;
    private bool _hasCachedUnorderedFingerprint;
    private ulong _cachedUnorderedFingerprintFirst;
    private ulong _cachedUnorderedFingerprintSecond;
    private bool _hasCachedCycleShapeFingerprint;
    private ulong _cachedCycleShapeFingerprintFirst;
    private ulong _cachedCycleShapeFingerprintSecond;
    private bool _fingerprintCacheDisabled;
    private HookCardProjection? _hookCardProjection;
    // A pending recheck belongs to this pile, never to the shared immutable indices.
    private PredictedCard? _hookProjectionDirtyCard;
    private PredictedCard? _hookProjectionSecondDirtyCard;

    // Indices refer only to this ordered pile. Forks share the immutable indices,
    // then resolve receivers through their own card wrappers and previews.
    private sealed record HookCardProjection(MirroredHookListenerFilter Filter, int[] Indices);

    public PileType Type { get; }

    public IReadOnlyList<PredictedCard> Cards => _cards;

    public List<PredictedCard>.Enumerator GetEnumerator() => _cards.GetEnumerator();

    public bool IsEmpty => _cards.Count == 0;

    public PredictedCard? TopCard => IsEmpty ? null : _cards[0];

    public PredictedCard? BottomCard => IsEmpty ? null : _cards[^1];

    public SimCardPile(PileType type, IEnumerable<PredictedCard> cards)
    {
        Type = type;
        _cards = [.. cards];
        AttachCards();
    }

    private SimCardPile(PileType type, List<PredictedCard> cards)
    {
        Type = type;
        _cards = cards;
        AttachCards();
    }

    public SimCardPile(CardPile pile)
        : this(pile.Type, pile.Cards.Select(card => new PredictedCard(card)))
    {
    }

    public void Add(PredictedCard card)
    {
        InvalidateMembershipFingerprint(inserted: card);
        _cards.Add(card);
        card.SetOwnerPile(this, membershipChangedPile: this);
    }

    public void Insert(int index, PredictedCard card)
    {
        InvalidateMembershipFingerprint(inserted: card);
        _cards.Insert(index, card);
        card.SetOwnerPile(this, membershipChangedPile: this);
    }

    public bool Remove(PredictedCard card)
    {
        if (!_cards.Remove(card))
            return false;
        InvalidateMembershipFingerprint(removed: card);
        card.SetOwnerPile(null, membershipChangedPile: this);
        return true;
    }

    public void Clear()
    {
        if (_cards.Count == 0)
            return;
        foreach (PredictedCard card in _cards)
            card.SetOwnerPile(null);
        InvalidateFingerprint();
        _cards.Clear();
    }

    public SimCardPile Clone()
    {
        return new SimCardPile(Type, _cards.Select(card => card.Clone()));
    }

    internal SimCardPile Fork(PredictionForkContext context)
    {
        List<PredictedCard> cards = new(_cards.Count);
        foreach (PredictedCard card in _cards)
            cards.Add(card.Fork(context));
        SimCardPile fork = new(Type, cards);
        fork._hasCachedFingerprint = _hasCachedFingerprint;
        fork._cachedFingerprintFirst = _cachedFingerprintFirst;
        fork._cachedFingerprintSecond = _cachedFingerprintSecond;
        fork._hasCachedUnorderedFingerprint = _hasCachedUnorderedFingerprint;
        fork._cachedUnorderedFingerprintFirst = _cachedUnorderedFingerprintFirst;
        fork._cachedUnorderedFingerprintSecond = _cachedUnorderedFingerprintSecond;
        fork._hasCachedCycleShapeFingerprint = _hasCachedCycleShapeFingerprint;
        fork._cachedCycleShapeFingerprintFirst = _cachedCycleShapeFingerprintFirst;
        fork._cachedCycleShapeFingerprintSecond = _cachedCycleShapeFingerprintSecond;
        if (!_fingerprintCacheDisabled && !fork._fingerprintCacheDisabled)
        {
            fork._hookCardProjection = _hookCardProjection;
            if (_hookProjectionDirtyCard is { } dirty)
            {
                if (context.TryRemap(dirty, out PredictedCard? mapped))
                    fork._hookProjectionDirtyCard = mapped;
                else
                    fork._hookCardProjection = null;
            }
            if (_hookProjectionSecondDirtyCard is { } secondDirty)
            {
                if (fork._hookCardProjection is not null
                    && context.TryRemap(secondDirty, out PredictedCard? mapped))
                    fork._hookProjectionSecondDirtyCard = mapped;
                else
                {
                    fork._hookCardProjection = null;
                    fork._hookProjectionDirtyCard = null;
                }
            }
        }
        context.Register(this, fork);
        return fork;
    }

    internal bool TryGetCachedFingerprint(out ulong first, out ulong second)
    {
        first = _cachedFingerprintFirst;
        second = _cachedFingerprintSecond;
        return !_fingerprintCacheDisabled && _hasCachedFingerprint;
    }

    internal void SetCachedFingerprint(ulong first, ulong second)
    {
        if (_fingerprintCacheDisabled)
            return;
        _cachedFingerprintFirst = first;
        _cachedFingerprintSecond = second;
        _hasCachedFingerprint = true;
    }

    internal bool TryGetCachedUnorderedFingerprint(out ulong first, out ulong second)
    {
        first = _cachedUnorderedFingerprintFirst;
        second = _cachedUnorderedFingerprintSecond;
        return !_fingerprintCacheDisabled && _hasCachedUnorderedFingerprint;
    }

    internal void SetCachedUnorderedFingerprint(ulong first, ulong second)
    {
        if (_fingerprintCacheDisabled)
            return;
        _cachedUnorderedFingerprintFirst = first;
        _cachedUnorderedFingerprintSecond = second;
        _hasCachedUnorderedFingerprint = true;
    }

    internal bool TryGetCachedCycleShapeFingerprint(out ulong first, out ulong second)
    {
        first = _cachedCycleShapeFingerprintFirst;
        second = _cachedCycleShapeFingerprintSecond;
        return !_fingerprintCacheDisabled && _hasCachedCycleShapeFingerprint;
    }

    internal void SetCachedCycleShapeFingerprint(ulong first, ulong second)
    {
        if (_fingerprintCacheDisabled)
            return;
        _cachedCycleShapeFingerprintFirst = first;
        _cachedCycleShapeFingerprintSecond = second;
        _hasCachedCycleShapeFingerprint = true;
    }

    internal void InvalidateFingerprint()
    {
        _hookCardProjection = null;
        _hookProjectionDirtyCard = null;
        _hookProjectionSecondDirtyCard = null;
        InvalidateCardStateFingerprints();
    }

    internal void InvalidateCardFingerprint(PredictedCard card)
    {
        InvalidateHookCardProjection(card);
        InvalidateCardStateFingerprints();
    }

    // The membership caller already updates the projection before notifying the
    // card's mutation observer. Keep all value fingerprints invalidated; an observer
    // can still reenter or invalidate this projection through a separate card write.
    internal void InvalidateMembershipStateFingerprints() => InvalidateCardStateFingerprints();

    private void InvalidateMembershipFingerprint(PredictedCard? inserted = null, PredictedCard? removed = null)
    {
        InvalidateCardStateFingerprints();
        if (_hookCardProjection is not { Indices.Length: 0 } cached
            || inserted is not null && HasPotentialHookReceiver(inserted, cached.Filter)
            // Removing one occurrence must not leave an unowned alias behind an
            // empty cached result: later writes could no longer notify this pile.
            || removed is not null && _cards.Contains(removed))
        {
            _hookCardProjection = null;
            _hookProjectionDirtyCard = null;
            _hookProjectionSecondDirtyCard = null;
            return;
        }
        if (removed is not null && (ReferenceEquals(removed, _hookProjectionDirtyCard)
            || ReferenceEquals(removed, _hookProjectionSecondDirtyCard)))
        {
            // A reentrant completion may have changed attached types since the
            // original proof. Keep the full fallback when that card gained a hook.
            if (HasPotentialHookReceiver(removed, cached.Filter))
            {
                _hookCardProjection = null;
                _hookProjectionDirtyCard = null;
                _hookProjectionSecondDirtyCard = null;
            }
            else
            {
                if (ReferenceEquals(removed, _hookProjectionDirtyCard))
                    _hookProjectionDirtyCard = _hookProjectionSecondDirtyCard;
                _hookProjectionSecondDirtyCard = null;
            }
        }
    }

    private void InvalidateCardStateFingerprints()
    {
        _hasCachedFingerprint = false;
        _hasCachedUnorderedFingerprint = false;
        _hasCachedCycleShapeFingerprint = false;
    }

    internal void InvalidateHookCardProjection(PredictedCard card)
    {
        // Empty indices prove that every previous member was irrelevant. Only the
        // tracked cards can have changed; recheck their current card/attachment types
        // on demand. A third distinct write and nonempty projections invalidate fully.
        if (_hookCardProjection is { Indices.Length: 0 })
        {
            if (_hookProjectionDirtyCard is null || ReferenceEquals(_hookProjectionDirtyCard, card))
            {
                _hookProjectionDirtyCard = card;
                return;
            }
            if (_hookProjectionSecondDirtyCard is null || ReferenceEquals(_hookProjectionSecondDirtyCard, card))
            {
                _hookProjectionSecondDirtyCard = card;
                return;
            }
        }
        _hookCardProjection = null;
        _hookProjectionDirtyCard = null;
        _hookProjectionSecondDirtyCard = null;
    }

    internal void DisableFingerprintCache()
    {
        _hookCardProjection = null;
        _hookProjectionDirtyCard = null;
        _hookProjectionSecondDirtyCard = null;
        _fingerprintCacheDisabled = true;
        _hasCachedFingerprint = false;
        _hasCachedUnorderedFingerprint = false;
        _hasCachedCycleShapeFingerprint = false;
    }

    internal bool TryGetHookCardProjection(
        MirroredHookListenerFilter filter, out ReadOnlySpan<int> indices)
    {
        if (_fingerprintCacheDisabled || !filter.CanProjectReceivers)
        {
            indices = default;
            return false;
        }
        if (_hookCardProjection is { } cached && ReferenceEquals(cached.Filter, filter))
        {
            if ((_hookProjectionDirtyCard is not { } dirty
                    || ReferenceEquals(dirty.OwnerPile, this) && !HasPotentialHookReceiver(dirty, filter))
                && (_hookProjectionSecondDirtyCard is not { } secondDirty
                    || ReferenceEquals(secondDirty.OwnerPile, this) && !HasPotentialHookReceiver(secondDirty, filter)))
            {
                _hookProjectionDirtyCard = null;
                _hookProjectionSecondDirtyCard = null;
                indices = cached.Indices;
                return true;
            }
            _hookCardProjection = null;
            _hookProjectionDirtyCard = null;
            _hookProjectionSecondDirtyCard = null;
        }

        _hookProjectionDirtyCard = null;
        _hookProjectionSecondDirtyCard = null;
        List<int>? selected = null;
        for (int index = 0; index < _cards.Count; index++)
        {
            PredictedCard card = _cards[index];
            // A wrapper shared by different piles cannot notify both owners about
            // future writes. Keep the original scan for that representation.
            if (!ReferenceEquals(card.OwnerPile, this))
            {
                _hookCardProjection = null;
                indices = default;
                return false;
            }
            CardModel preview = card.Preview;
            // Keep potential receivers even while removed. The producer checks the
            // current removal flag; participation depends on immutable runtime types.
            if (filter.HasMirroredCallbacks(preview)
                || preview.Affliction is { } affliction && filter.HasMirroredCallbacks(affliction)
                || preview.Enchantment is { } enchantment && filter.HasMirroredCallbacks(enchantment))
                (selected ??= []).Add(index);
        }
        _hookCardProjection = new HookCardProjection(filter, selected?.ToArray() ?? []);
        indices = _hookCardProjection.Indices;
        return true;
    }

    private static bool HasPotentialHookReceiver(PredictedCard card, MirroredHookListenerFilter filter)
    {
        CardModel preview = card.Preview;
        return filter.HasMirroredCallbacks(preview)
            || preview.Affliction is { } affliction && filter.HasMirroredCallbacks(affliction)
            || preview.Enchantment is { } enchantment && filter.HasMirroredCallbacks(enchantment);
    }

    private void AttachCards()
    {
        foreach (PredictedCard card in _cards)
            card.SetOwnerPile(this);
    }

    public PredictedCard? Find(CardModel card)
    {
        return _cards.Find(predicted => predicted.References(card));
    }
}
