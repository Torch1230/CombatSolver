using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed class SimCardPile
{
    private readonly List<PredictedCard> _cards;
    private bool _hasCachedFingerprint;
    private ulong _cachedFingerprintFirst;
    private ulong _cachedFingerprintSecond;
    private bool _hasCachedCycleShapeFingerprint;
    private ulong _cachedCycleShapeFingerprintFirst;
    private ulong _cachedCycleShapeFingerprintSecond;
    private bool _fingerprintCacheDisabled;

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
        InvalidateFingerprint();
        _cards.Add(card);
        card.SetOwnerPile(this);
    }

    public void Insert(int index, PredictedCard card)
    {
        InvalidateFingerprint();
        _cards.Insert(index, card);
        card.SetOwnerPile(this);
    }

    public bool Remove(PredictedCard card)
    {
        if (!_cards.Remove(card))
            return false;
        InvalidateFingerprint();
        card.SetOwnerPile(null);
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
        fork._hasCachedCycleShapeFingerprint = _hasCachedCycleShapeFingerprint;
        fork._cachedCycleShapeFingerprintFirst = _cachedCycleShapeFingerprintFirst;
        fork._cachedCycleShapeFingerprintSecond = _cachedCycleShapeFingerprintSecond;
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
        _hasCachedFingerprint = false;
        _hasCachedCycleShapeFingerprint = false;
    }

    internal void DisableFingerprintCache()
    {
        _fingerprintCacheDisabled = true;
        _hasCachedFingerprint = false;
        _hasCachedCycleShapeFingerprint = false;
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
