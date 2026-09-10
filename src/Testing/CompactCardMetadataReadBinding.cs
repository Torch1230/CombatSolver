using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>Lane-owned preview values for completed reads. Never executes a card or owns a candidate.</summary>
internal sealed class CompactCardMetadataReadBinding
{
    private readonly PredictedCard[] _cards;
    private readonly CardModel[] _models;

    internal CompactCardMetadataReadBinding(PredictedCard[] cards)
    {
        _cards = (PredictedCard[])cards.Clone();
        // Materialize COW previews once, while the evaluation context is still private.
        _models = _cards.Select(card => card.MutablePreview).ToArray();
    }

    internal void Read(ResumableDiscardProgram program)
    {
        for (int card = 0; card < _cards.Length; card++)
        {
            CardModel model = _models[card];
            int captured = program.CapturedX(card);
            bool removed = program.CardRemoved(card);
            if ((!model.EnergyCost.CostsX || model.EnergyCost.CapturedXValue == captured) && model.HasBeenRemovedFromState == removed) continue;
            bool structureChanged = model.HasBeenRemovedFromState != removed;
            if (model.EnergyCost.CostsX) model.EnergyCost.CapturedXValue = captured;
            model.HasBeenRemovedFromState = removed;
            _cards[card].InvalidateCaches();
            if (structureChanged) _cards[card].NotifyHookListenerStructureChanged();
        }
    }
}
