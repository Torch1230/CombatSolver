using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;

namespace CombatSolver;

/// <summary>Lane-owned preview values and generated model pool for completed reads.</summary>
internal sealed class CompactCardMetadataReadBinding : ICompletedEnergyCostReadSource
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
    private readonly bool _keywordsCanChange;
    private readonly List<Binding> _active;
    private readonly Dictionary<(int Identity, int Definition), Binding> _generated = [];
    private readonly Dictionary<PredictedCard, int> _costIdentities = [];
    private ResumableDiscardProgram? _costProgram;
    internal PredictedCard this[int index] => _active[index].Card;

    internal CompactCardMetadataReadBinding(CompactDiscardProjection adapter, PredictedCard[] cards)
    {
        _adapter = adapter;
        _keywordsCanChange = adapter.Program.KeywordsCanChange;
        // Materialize only potentially changing previews, once per private reader. Generated
        // instances are pooled by identity and definition; sibling restores can reuse them.
        _active = cards.Select(card => Binding.Create(card, adapter.CardValuesInvariant)).ToList();
        if (adapter.Program.HasGlobalEnergyCosts)
            for (int card = 0; card < cards.Length; card++) _costIdentities.Add(cards[card], card);
    }

    internal void Read(ResumableDiscardProgram program)
    {
        if (program.HasGlobalEnergyCosts) _costProgram = program;
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
        if (_costProgram != null)
        {
            _costIdentities.Clear();
            for (int card = 0; card < program.CardCount; card++) _costIdentities.Add(_active[card].Card, card);
        }
        for (int card = 0; card < program.CardCount; card++)
        {
            var binding = _active[card];
            CardModel model = binding.Model;
            int captured = program.CapturedX(card);
            bool removed = program.CardRemoved(card);
            bool costChanged = ImportCosts(binding, program, card);
            bool keywordsChanged = _keywordsCanChange && ImportKeywords(model, program, card);
            bool enchantmentChanged = false;
            if (model.Enchantment is Swift swift)
            {
                var status = program.EnchantmentDisabled(card) ? EnchantmentStatus.Disabled : EnchantmentStatus.Normal;
                enchantmentChanged = swift.Status != status;
                swift._status = status;
            }
            bool singleSly = program.SingleTurnSly(card);
            if (!costChanged && !keywordsChanged && !enchantmentChanged && model.HasSingleTurnSly == singleSly && (!model.EnergyCost.CostsX || model.EnergyCost.CapturedXValue == captured) && model.HasBeenRemovedFromState == removed) continue;
            bool structureChanged = model.HasBeenRemovedFromState != removed;
            if (model.EnergyCost.CostsX) model.EnergyCost.CapturedXValue = captured;
            model.HasBeenRemovedFromState = removed;
            model.HasSingleTurnSly = singleSly;
            binding.Card.InvalidateCaches();
            if (structureChanged) binding.Card.NotifyHookListenerStructureChanged();
        }
    }

    public int ReadEnergyCost(PredictedCard card)
        => _costProgram != null && _costIdentities.TryGetValue(card, out int identity)
            ? _costProgram.EnergyCost(identity)
            : throw new InvalidOperationException("Completed energy cost query has no current card binding.");

    private static bool ImportKeywords(CardModel model, ResumableDiscardProgram program, int card)
    {
        bool ethereal = program.IsEthereal(card), retain = program.IsRetained(card);
        var keywords = model.LocalKeywords;
        bool changed = keywords.Contains(CardKeyword.Ethereal) != ethereal || keywords.Contains(CardKeyword.Retain) != retain;
        if (ethereal) keywords.Add(CardKeyword.Ethereal); else keywords.Remove(CardKeyword.Ethereal);
        if (retain) keywords.Add(CardKeyword.Retain); else keywords.Remove(CardKeyword.Retain);
        return changed;
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
