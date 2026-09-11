using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal readonly record struct CompletedPowerReadValues(int Amount, Creature? Applier, int Order, bool Retired,
    int AmountOnTurnStart = 0, bool SkipNextDurationTick = false);

internal sealed partial class SimulatedCombatState
{
    internal bool IsCapturedRootPowerSlot(PowerModel power) => _rootPowerAmounts.ContainsKey((power.Owner, power.GetType()));

    // A lane-local evaluation projection, never an execution state or a retained candidate.
    // All model instances are allocated at setup; subsequent reads only replace supplied values
    // and invalidate dependent listener lists. No Power command, hook, RNG or amount event runs.
    internal sealed class CompletedPowerReadBinding
    {
        private readonly SimulatedCombatState _state;
        private readonly PowerModel[] _models;
        private readonly PowerModel[] _replacementModels;
        private readonly HashSet<(Creature Owner, Type Type)> _initialRetired;
        private readonly List<PowerModel> _unrepresentedOrder;
        private readonly CompletedPowerReadValues[] _previous;
        private readonly int[] _order;
        private readonly Comparison<int> _compareOrder;
        private bool _hasPrevious;

        internal CompletedPowerReadBinding(SimulatedCombatState state, IReadOnlyList<PowerModel> templates,
            IReadOnlyList<PowerModel> canonicalTemplates)
        {
            state.AssertForkable();
            if (templates.Count != canonicalTemplates.Count) throw new ArgumentException("Incomplete canonical Power metadata.");
            _state = state;
            _models = templates.Select(source =>
            {
                PowerModel model = PredictionUtils.CloneModelForSimulation(source);
                model._owner = source.Owner;
                model._target = source.Target;
                model._applier = source.Applier;
                model._amount = source.Amount;
                return model;
            }).ToArray();
            _replacementModels = templates.Select((source, index) =>
            {
                PowerModel canonical = canonicalTemplates[index];
                if (canonical.IsMutable || canonical.GetType() != source.GetType())
                    throw new ArgumentException("Replacement Power metadata must be canonical and have the same type.");
                // A captured instance can retire and be reacquired with native defaults.
                // Keep both read models at setup so restoring either lifetime never clones
                // per leaf or mutates the original template's private fields/variables.
                if (source.Amount == 0) return _models[index];
                PowerModel model = PredictionUtils.CloneModelForSimulation(canonical);
                model._owner = source.Owner;
                model._target = null;
                model._applier = null;
                model._amount = 0;
                model.AmountOnTurnStart = 0;
                return model;
            }).ToArray();
            _previous = new CompletedPowerReadValues[templates.Count];
            _order = new int[templates.Count];
            _compareOrder = (left, right) => _previous[left].Order.CompareTo(_previous[right].Order);
            _initialRetired = state._retiredRootPowerSlots?.ToHashSet() ?? [];
            var keys = _models.Select(p => (p.Owner, p.GetType())).ToHashSet();
            if (keys.Count != templates.Count)
                throw new ArgumentException("Completed Power reads require unique owner/type slots.");
            _unrepresentedOrder = state._powerListenerOrder?.Where(p => !keys.Contains((p.Owner, p.GetType()))).ToList() ?? [];
            state._powers ??= [];
            foreach (PowerModel model in _models) state._powers[(model.Owner, model.GetType())] = model;
            state.InvalidateHookListeners();
        }

        internal void Read(ReadOnlySpan<CompletedPowerReadValues> values, IReadOnlyList<Creature> roster)
        {
            if (values.Length != _models.Length) throw new ArgumentException("Incomplete Power read input.");
            bool rosterChanged = !_state._enemies.SequenceEqual(roster);
            if (_hasPrevious && !rosterChanged && values.SequenceEqual(_previous)) return;
            values.CopyTo(_previous); _hasPrevious = true;
            _state._retiredRootPowerSlots ??= [];
            _state._retiredRootPowerSlots.Clear();
            _state._retiredRootPowerSlots.UnionWith(_initialRetired);
            _state._powerListenerOrder ??= [];
            _state._powerListenerOrder.Clear();
            _state._powerListenerOrder.AddRange(_unrepresentedOrder);
            for (int index = 0; index < values.Length; index++)
            {
                var value = values[index];
                PowerModel model = value.Retired ? _replacementModels[index] : _models[index];
                model._amount = value.Amount;
                model._applier = value.Applier;
                model.AmountOnTurnStart = value.AmountOnTurnStart;
                model.SkipNextDurationTick = value.SkipNextDurationTick;
                _state._powers![(model.Owner, model.GetType())] = model;
                if (value.Retired) _state._retiredRootPowerSlots.Add((model.Owner, model.GetType()));
                _order[index] = index;
            }
            // Acquisition order is distinct from effective listener order; the existing
            // owner-anchor insertion algorithm still computes that final sequence.
            Array.Sort(_order, _compareOrder);
            foreach (int index in _order)
                if (values[index].Amount != 0)
                    _state._powerListenerOrder.Add(values[index].Retired ? _replacementModels[index] : _models[index]);
            if (rosterChanged)
            {
                while (_state._enemies.Count > roster.Count) _state._enemies.RemoveAt(_state._enemies.Count - 1);
                for (int index = 0; index < roster.Count; index++)
                {
                    if (index == _state._enemies.Count) _state._enemies.Add(roster[index]);
                    else if (!ReferenceEquals(_state._enemies[index], roster[index])) _state._enemies[index] = roster[index];
                }
                _state._creatures = null;
                _state.InvalidateBaseHookListeners();
            }
            else _state.InvalidateHookListeners();
        }
    }
}
