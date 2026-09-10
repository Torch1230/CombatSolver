using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>Lane-owned preview values and generated model pool for completed reads.</summary>
internal sealed class CompactCardMetadataReadBinding
{
    private readonly record struct Binding(PredictedCard Card, CardModel Model);
    private readonly CompactDiscardProjection _adapter;
    private readonly List<Binding> _active;
    private readonly Dictionary<(int Identity, int Definition), Binding> _generated = [];
    internal PredictedCard this[int index] => _active[index].Card;

    internal CompactCardMetadataReadBinding(CompactDiscardProjection adapter, PredictedCard[] cards)
    {
        _adapter = adapter;
        // Materialize only potentially changing previews, once per private reader. Generated
        // instances are pooled by identity and definition; sibling restores can reuse them.
        _active = cards.Select(card => new Binding(card, adapter.CardValuesInvariant ? card.Preview : card.MutablePreview)).ToList();
    }

    internal void Read(ResumableDiscardProgram program)
    {
        if (_adapter.CardValuesInvariant) return;
        if (_active.Count > program.CardCount) _active.RemoveRange(program.CardCount, _active.Count - program.CardCount);
        for (int card = _adapter.CardCount; card < program.CardCount; card++)
        {
            var key = (card, program.DefinitionIndex(card));
            if (!_generated.TryGetValue(key, out var binding))
            {
                var generated = _adapter.CreateGeneratedCard(key.Item2);
                binding = new(generated, generated.MutablePreview);
                _generated.Add(key, binding);
            }
            if (card == _active.Count) _active.Add(binding);
            else _active[card] = binding;
        }
        for (int card = 0; card < program.CardCount; card++)
        {
            var binding = _active[card];
            CardModel model = binding.Model;
            int captured = program.CapturedX(card);
            bool removed = program.CardRemoved(card);
            if ((!model.EnergyCost.CostsX || model.EnergyCost.CapturedXValue == captured) && model.HasBeenRemovedFromState == removed) continue;
            bool structureChanged = model.HasBeenRemovedFromState != removed;
            if (model.EnergyCost.CostsX) model.EnergyCost.CapturedXValue = captured;
            model.HasBeenRemovedFromState = removed;
            binding.Card.InvalidateCaches();
            if (structureChanged) binding.Card.NotifyHookListenerStructureChanged();
        }
    }
}
