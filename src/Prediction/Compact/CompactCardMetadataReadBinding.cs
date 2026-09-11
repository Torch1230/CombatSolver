using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

/// <summary>Lane-owned preview values and generated model pool for completed reads.</summary>
internal sealed class CompactCardMetadataReadBinding
{
    private readonly record struct Binding(PredictedCard Card, CardModel Model, List<LocalCostModifier>? CostPool)
    {
        internal static Binding Create(PredictedCard card, bool invariant)
        {
            CardModel model = invariant ? card.Preview : card.MutablePreview;
            return new(card, model, model.Enchantment is Slither ? model.EnergyCost._localModifiers.ToList() : null);
        }
    }
    private readonly CompactDiscardProjection _adapter;
    private readonly List<Binding> _active;
    private readonly Dictionary<(int Identity, int Definition), Binding> _generated = [];
    internal PredictedCard this[int index] => _active[index].Card;

    internal CompactCardMetadataReadBinding(CompactDiscardProjection adapter, PredictedCard[] cards)
    {
        _adapter = adapter;
        // Materialize only potentially changing previews, once per private reader. Generated
        // instances are pooled by identity and definition; sibling restores can reuse them.
        _active = cards.Select(card => Binding.Create(card, adapter.CardValuesInvariant)).ToList();
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
                binding = Binding.Create(generated, false);
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
            bool costChanged = ImportCosts(binding, program, card);
            bool singleSly = program.SingleTurnSly(card);
            if (!costChanged && model.HasSingleTurnSly == singleSly && (!model.EnergyCost.CostsX || model.EnergyCost.CapturedXValue == captured) && model.HasBeenRemovedFromState == removed) continue;
            bool structureChanged = model.HasBeenRemovedFromState != removed;
            if (model.EnergyCost.CostsX) model.EnergyCost.CapturedXValue = captured;
            model.HasBeenRemovedFromState = removed;
            model.HasSingleTurnSly = singleSly;
            binding.Card.InvalidateCaches();
            if (structureChanged) binding.Card.NotifyHookListenerStructureChanged();
        }
    }

    private static bool ImportCosts(Binding binding, ResumableDiscardProgram program, int card)
    {
        if (binding.CostPool is not { } pool) return false;
        int count = program.CostModifierCount(card);
        List<LocalCostModifier> active = binding.Model.EnergyCost._localModifiers;
        bool changed = active.Count != count;
        while (pool.Count < count) pool.Add(new(0, LocalCostType.Absolute, LocalCostModifierExpiration.EndOfCombat, false));
        if (active.Count > count) active.RemoveRange(count, active.Count - count);
        for (int index = 0; index < count; index++)
        {
            int amount = program.CostModifierAt(card, index);
            LocalCostModifier modifier = pool[index];
            changed |= modifier.Amount != amount;
            modifier.Amount = amount;
            if (index == active.Count) active.Add(modifier);
        }
        return changed;
    }
}
