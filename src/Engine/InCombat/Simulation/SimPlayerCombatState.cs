using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class SimPlayerCombatState
{
    private readonly PlayerCombatState _liveState;
    private SimOrbQueue? _orbQueue;
    private SimCardPile? _hand;
    private SimCardPile? _drawPile;
    private SimCardPile? _discardPile;
    private SimCardPile? _exhaustPile;
    private SimCardPile? _playPile;
    private SimCardPile[]? _allPiles;

    public SimOrbQueue OrbQueue => _orbQueue ??= new SimOrbQueue(_liveState.OrbQueue);

    public SimCardPile Hand => _hand ??= new SimCardPile(_liveState.Hand);

    public SimCardPile DrawPile => _drawPile ??= new SimCardPile(_liveState.DrawPile);

    public SimCardPile DiscardPile => _discardPile ??= new SimCardPile(_liveState.DiscardPile);

    public SimCardPile ExhaustPile => _exhaustPile ??= new SimCardPile(_liveState.ExhaustPile);

    public SimCardPile PlayPile => _playPile ??= new SimCardPile(_liveState.PlayPile);

    public IReadOnlyList<SimCardPile> AllPiles
        => _allPiles ??= [Hand, DrawPile, DiscardPile, ExhaustPile, PlayPile];

    public AllCardsEnumerable AllCards => new(this);

    public readonly struct AllCardsEnumerable(SimPlayerCombatState state) : IEnumerable<PredictedCard>
    {
        private readonly SimPlayerCombatState _state = state;

        public Enumerator GetEnumerator() => new(_state);

        IEnumerator<PredictedCard> IEnumerable<PredictedCard>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct Enumerator : IEnumerator<PredictedCard>
    {
        private readonly SimPlayerCombatState _state;
        private int _pileIndex;
        private List<PredictedCard>.Enumerator _cards;

        internal Enumerator(SimPlayerCombatState state)
        {
            _state = state;
            _pileIndex = -1;
            _cards = default;
        }

        public readonly PredictedCard Current => _cards.Current;

        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_pileIndex >= 0 && _cards.MoveNext())
                return true;
            while (++_pileIndex < 5)
            {
                _cards = _state.GetPileByEnumerationIndex(_pileIndex).GetEnumerator();
                if (_cards.MoveNext())
                    return true;
            }
            return false;
        }

        public readonly void Dispose()
        {
        }

        void System.Collections.IEnumerator.Reset() => throw new NotSupportedException();
    }

    public int Energy { get; private set; }

    public int Stars { get; private set; }

    public SimPlayerCombatState(PlayerCombatState liveState)
    {
        _liveState = liveState;
        Energy = liveState.Energy;
        Stars = liveState.Stars;
    }

    private SimPlayerCombatState(PlayerCombatState liveState, int energy, int stars)
    {
        _liveState = liveState;
        Energy = energy;
        Stars = stars;
    }

    public PredictedCard? FindCard(CardModel card)
    {
        foreach (PredictedCard predicted in AllCards)
        {
            if (predicted.References(card))
                return predicted;
        }
        return null;
    }

    private SimCardPile GetPileByEnumerationIndex(int index)
        => index switch
        {
            0 => Hand,
            1 => DrawPile,
            2 => DiscardPile,
            3 => ExhaustPile,
            4 => PlayPile,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Unknown combat-pile index."),
        };

    public SimCardPile? GetCardPile(PileType type)
    {
        return type switch
        {
            PileType.None => null,
            PileType.Draw => DrawPile,
            PileType.Hand => Hand,
            PileType.Discard => DiscardPile,
            PileType.Exhaust => ExhaustPile,
            PileType.Play => PlayPile,
            PileType.Deck => throw new ArgumentOutOfRangeException(nameof(type), type, "Deck is not a combat pile."),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, $"Unknown pile type: {type}.")
        };
    }

    internal bool ContainsPile(SimCardPile pile)
        => ReferenceEquals(_hand, pile)
            || ReferenceEquals(_drawPile, pile)
            || ReferenceEquals(_discardPile, pile)
            || ReferenceEquals(_exhaustPile, pile)
            || ReferenceEquals(_playPile, pile);

    // Mirrors PlayerCombatState.GainEnergy.
    public void GainEnergy(decimal amount)
    {
        Energy = (int)Math.Clamp(Energy + amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.LoseEnergy.
    public void LoseEnergy(decimal amount)
    {
        Energy = (int)Math.Clamp(Energy - amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.GainStars.
    public void GainStars(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("Must not be negative.", nameof(amount));
        Stars = (int)Math.Clamp(Stars + amount, 0m, 999999999m);
    }

    // Mirrors PlayerCombatState.LoseStars.
    public void LoseStars(decimal amount)
    {
        Stars = (int)Math.Clamp(Stars - amount, 0m, 999999999m);
    }

    internal void MaterializeRoot()
    {
        _ = OrbQueue;
        foreach (PredictedCard card in AllCards)
            card.MaterializePreview();
    }

    internal SimPlayerCombatState Fork(PredictionForkContext context)
    {
        SimPlayerCombatState fork = new(_liveState, Energy, Stars)
        {
            _orbQueue = _orbQueue?.Fork(context),
            _hand = _hand?.Fork(context),
            _drawPile = _drawPile?.Fork(context),
            _discardPile = _discardPile?.Fork(context),
            _exhaustPile = _exhaustPile?.Fork(context),
            _playPile = _playPile?.Fork(context)
        };
        context.Register(this, fork);
        return fork;
    }
}
