using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal partial struct ModelPredictionStateWriter
{
    private readonly record struct CardPosition(ulong Owner, int Pile, int Index) : IComparable<CardPosition>
    {
        public int CompareTo(CardPosition other)
        {
            int comparison = Owner.CompareTo(other.Owner);
            if (comparison == 0) comparison = Pile.CompareTo(other.Pile);
            return comparison != 0 ? comparison : Index.CompareTo(other.Index);
        }
    }

    internal void BindCardReferences(ICombatState combat, CombatPredictionSimulator? simulator)
    {
        _referenceCombat = combat;
        _referenceSimulator = simulator;
        _cardPositions = null;
    }

    public void AddCard(string name, CardModel? card)
    {
        if (_referenceSimulator is not null)
            throw new InvalidOperationException("Predicted state must describe PredictedCard references, not live models.");
        WritePosition(name, ResolvePosition(card));
    }

    public void AddCard(string name, PredictedCard? card)
    {
        if (_referenceSimulator is null)
            throw new InvalidOperationException("Live state must describe CardModel references.");
        WritePosition(name, ResolvePosition(card));
    }

    public void AddCards(string name, IReadOnlyList<CardModel?>? cards, bool unordered = false)
    {
        if (_referenceSimulator is not null)
            throw new InvalidOperationException("Predicted state must describe PredictedCard references.");
        WritePositions(name, cards, unordered);
    }

    public void AddCards(string name, IReadOnlyList<PredictedCard?>? cards, bool unordered = false)
    {
        if (_referenceSimulator is null)
            throw new InvalidOperationException("Live state must describe CardModel references.");
        WritePositions(name, cards, unordered);
    }

    private void WritePositions<T>(string name, IReadOnlyList<T?>? cards, bool unordered) where T : class
    {
        BeginField(name, unordered ? 'U' : 'O');
        Add("count", (long)(cards?.Count ?? -1));
        if (cards is null || cards.Count == 0) return;
        if (!unordered)
        {
            foreach (T? card in cards) WritePosition("item", ResolvePosition(card));
            return;
        }
        // Sorting is opt-in: an unordered multiset still preserves nulls and multiplicity.
        CardPosition?[] positions = new CardPosition?[cards.Count];
        for (int index = 0; index < cards.Count; index++) positions[index] = ResolvePosition(cards[index]);
        Array.Sort(positions);
        foreach (CardPosition? position in positions) WritePosition("item", position);
    }

    private void WritePosition(string name, CardPosition? position)
    {
        BeginField(name, position.HasValue ? 'R' : 'N');
        if (position is not { } value) return;
        Add("owner", value.Owner);
        Add("pile", (long)value.Pile);
        Add("index", (long)value.Index);
    }

    private CardPosition? ResolvePosition(object? card)
    {
        if (_referenceCombat is null)
            throw new InvalidOperationException("Card references require a bound combat observation.");
        if (card is null) return null;
        if (_cardPositions is null) BuildCardPositions();
        return _cardPositions!.TryGetValue(card, out CardPosition position)
            ? position : throw new InvalidOperationException("Card reference is outside this observation's combat piles.");
    }

    private void BuildCardPositions()
    {
        // One lazy index per complete observation, shared by all registered model writers.
        // No live card fields are read in predicted observation, including after preview COW.
        Dictionary<object, CardPosition> positions = new(ReferenceEqualityComparer.Instance);
        foreach (Player player in _referenceCombat!.Players)
        {
            if (_referenceSimulator is { } simulator)
            {
                var piles = simulator.State.GetPlayerCombatState(player).AllPiles;
                for (int pile = 0; pile < piles.Count; pile++)
                    for (int index = 0; index < piles[pile].Cards.Count; index++)
                        positions.Add(piles[pile].Cards[index], new(player.NetId, pile, index));
            }
            else
            {
                PlayerCombatState state = player.PlayerCombatState
                    ?? throw new InvalidOperationException("Live player has no combat state.");
                AddLivePile(state.Hand.Cards, player.NetId, 0);
                AddLivePile(state.DrawPile.Cards, player.NetId, 1);
                AddLivePile(state.DiscardPile.Cards, player.NetId, 2);
                AddLivePile(state.ExhaustPile.Cards, player.NetId, 3);
                AddLivePile(state.PlayPile.Cards, player.NetId, 4);
            }
        }
        _cardPositions = positions;

        void AddLivePile(IReadOnlyList<CardModel> cards, ulong owner, int pile)
        {
            for (int index = 0; index < cards.Count; index++)
                positions.Add(cards[index], new(owner, pile, index));
        }
    }
}
