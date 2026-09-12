using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal readonly record struct CompletedPowerReadValues(int Amount, Creature? Applier, int Order, bool Retired,
    int AmountOnTurnStart = 0, bool SkipNextDurationTick = false, int CardsLeft = 0, bool AlreadyApplied = false);

internal sealed partial class SimulatedCombatState
{
    internal bool IsCapturedRootPowerSlot(PowerModel power) => _rootPowerAmounts.ContainsKey((power.Owner, power.GetType()));

    // A lane-local evaluation projection, never an execution state or a retained candidate.
    // Ordinary and root instances are allocated at setup; independent-instance pools grow
    // only at a new lane high-water mark. Reads import values and listener order without
    // executing a Power command, hook, RNG operation or amount event.
    internal sealed class CompletedPowerReadBinding
    {
        private readonly SimulatedCombatState _state;
        private readonly CombatPredictionSimulator _context;
        private readonly List<PowerModel> _models;
        private readonly PowerModel[] _replacementModels;
        private readonly PanachePower? _panacheTemplate;
        private readonly Creature? _panacheOwner;
        private readonly int _ordinaryCount, _rootCount;
        private readonly HashSet<(Creature Owner, Type Type)> _initialRetired;
        private readonly List<PowerModel> _unrepresentedOrder;
        private CompletedPowerReadValues[] _previous;
        private int[] _order;
        private int _previousCount;
        private readonly IComparer<int> _compareOrder;
        private bool _hasPrevious;

        internal CompletedPowerReadBinding(CombatPredictionSimulator context, IReadOnlyList<PowerModel> templates,
            IReadOnlyList<PowerModel> canonicalTemplates, PanachePower? panacheTemplate)
        {
            var state = (SimulatedCombatState)context.State.CombatState;
            state.AssertForkable();
            if (templates.Count != canonicalTemplates.Count) throw new ArgumentException("Incomplete canonical Power metadata.");
            _state = state;
            _context = context;
            _ordinaryCount = templates.Count;
            _panacheTemplate = panacheTemplate;
            _panacheOwner = panacheTemplate == null ? null : state.Players.Single().Creature;
            _models = templates.Select(source =>
            {
                PowerModel model = PredictionUtils.CloneModelForSimulation(source);
                model._owner = source.Owner;
                model._target = source.Target;
                model._applier = source.Applier;
                model._amount = source.Amount;
                return model;
            }).ToList();
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
            // These root models already belong to this setup fork. Keep their original
            // single-slot or multi-instance remapping, including StateStore ownership.
            var panacheRoots = state.EffectivePowers().OfType<PanachePower>().ToArray();
            if (panacheRoots.Length > 0 && panacheTemplate == null)
                throw new ArgumentException("Independent Power read metadata is missing.");
            _models.AddRange(panacheRoots);
            _rootCount = _models.Count;
            _previous = new CompletedPowerReadValues[_rootCount];
            _order = new int[_rootCount];
            _compareOrder = Comparer<int>.Create((left, right) => _previous[left].Order.CompareTo(_previous[right].Order));
            _initialRetired = state._retiredRootPowerSlots?.ToHashSet() ?? [];
            var keys = templates.Select(p => (p.Owner, p.GetType())).ToHashSet();
            if (keys.Count != templates.Count)
                throw new ArgumentException("Completed Power reads require unique owner/type slots.");
            _unrepresentedOrder = state._powerListenerOrder?.Where(p => p is not PanachePower && !keys.Contains((p.Owner, p.GetType()))).ToList() ?? [];
            state._powers ??= [];
            for (int index = 0; index < _ordinaryCount; index++)
            {
                var model = _models[index];
                state._powers[(model.Owner, model.GetType())] = model;
            }
            state.InvalidateHookListeners();
        }

        internal void Read(ReadOnlySpan<CompletedPowerReadValues> values, IReadOnlyList<Creature> roster)
        {
            if (values.Length < _rootCount || values.Length > _rootCount && _panacheTemplate == null)
                throw new ArgumentException("Incomplete Power read input.");
            bool rosterChanged = !_state._enemies.SequenceEqual(roster);
            if (_hasPrevious && !rosterChanged && values.Length == _previousCount && values.SequenceEqual(_previous.AsSpan(0, _previousCount))) return;
            while (_models.Count < values.Length)
            {
                var model = PredictionUtils.CloneModelForSimulation(_panacheTemplate!);
                model._owner = _panacheOwner!;
                model._target = null;
                model._applier = null;
                model._amount = 0;
                (_state._addedPowerInstances ??= []).Add(model);
                _models.Add(model);
            }
            if (_previous.Length < values.Length)
            {
                Array.Resize(ref _previous, values.Length);
                Array.Resize(ref _order, values.Length);
            }
            // A lane can restore a sibling with fewer generated instances. Pooled models
            // outside this candidate remain inactive and never join its listener list.
            for (int index = values.Length; index < _models.Count; index++) _models[index]._amount = 0;
            values.CopyTo(_previous); _previousCount = values.Length; _hasPrevious = true;
            _state._retiredRootPowerSlots ??= [];
            _state._retiredRootPowerSlots.Clear();
            _state._retiredRootPowerSlots.UnionWith(_initialRetired);
            _state._powerListenerOrder ??= [];
            _state._powerListenerOrder.Clear();
            _state._powerListenerOrder.AddRange(_unrepresentedOrder);
            for (int index = 0; index < values.Length; index++)
            {
                var value = values[index];
                PowerModel model = index < _ordinaryCount && value.Retired ? _replacementModels[index] : _models[index];
                model._amount = value.Amount;
                model._applier = value.Applier;
                model.AmountOnTurnStart = value.AmountOnTurnStart;
                model.SkipNextDurationTick = value.SkipNextDurationTick;
                if (index < _ordinaryCount)
                {
                    _state._powers![(model.Owner, model.GetType())] = model;
                    if (value.Retired) _state._retiredRootPowerSlots.Add((model.Owner, model.GetType()));
                }
                else
                {
                    var panache = (PanachePower)model;
                    panache.DynamicVars["CardsLeft"].BaseValue = value.CardsLeft;
                    var hidden = _context.StateStore.Get(panache, static model => new PanachePredictionState(model));
                    hidden.CardsLeft = value.CardsLeft; hidden.AlreadyApplied = value.AlreadyApplied;
                    var key = (model.Owner, model.GetType());
                    if (value.Amount == 0 && _state._powers!.GetValueOrDefault(key) == model && _state._rootPowerAmounts.ContainsKey(key))
                        _state._retiredRootPowerSlots.Add(key);
                }
                _order[index] = index;
            }
            // Acquisition order is distinct from effective listener order; the existing
            // owner-anchor insertion algorithm still computes that final sequence.
            Array.Sort(_order, 0, values.Length, _compareOrder);
            for (int position = 0; position < values.Length; position++)
            {
                int index = _order[position];
                if (values[index].Amount != 0)
                    _state._powerListenerOrder.Add(index < _ordinaryCount && values[index].Retired ? _replacementModels[index] : _models[index]);
            }
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
