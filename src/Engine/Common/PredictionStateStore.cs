using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed class PredictionStateStore
{
    private readonly Dictionary<(AbstractModel Model, Type StateType), StateEntry> _states;
    private readonly Dictionary<AbstractModel, AbstractModel> _modelAliases;
    // 每种状态类型当前的条目数。ReadEntries<TState> 是热路径（每次动作后都要同步 Power 数量），
    // 绝大多数调用时该类型根本没有条目，用计数直接短路，避免整表扫描。
    private readonly Dictionary<Type, int> _countByType = new();

    public PredictionStateStore()
        : this(0, 0)
    {
    }

    private PredictionStateStore(int stateCapacity, int aliasCapacity)
    {
        _states = new Dictionary<(AbstractModel Model, Type StateType), StateEntry>(stateCapacity);
        _modelAliases = new Dictionary<AbstractModel, AbstractModel>(aliasCapacity);
    }

    private abstract class StateEntry
    {
        public abstract object Read();
        public abstract object Materialize();
    }

    private sealed class OwnedStateEntry(object value) : StateEntry
    {
        private readonly object _value = value;

        public override object Read() => _value;

        public override object Materialize() => _value;
    }

    public TState Get<TState>(AbstractModel model)
        where TState : IPredictionStateForkable, new()
    {
        return Get(model, static () => new TState());
    }

    public TState Get<TState>(AbstractModel model, Func<TState> create)
        where TState : IPredictionStateForkable
        => Get(model, create, static factory => factory());

    public TState Get<TModel, TState>(TModel model, Func<TModel, TState> create)
        where TModel : AbstractModel
        where TState : IPredictionStateForkable
        => Get(model, model, create);

    public TState Get<TArgument, TState>(
        AbstractModel model,
        TArgument argument,
        Func<TArgument, TState> create)
        where TState : IPredictionStateForkable
    {
        var key = (ResolveModel(model), typeof(TState));
        if (!_states.TryGetValue(key, out StateEntry? entry))
        {
            object state = create(argument)
                ?? throw new InvalidOperationException("Prediction state factory returned null.");
            entry = new OwnedStateEntry(state);
            _states[key] = entry;
            IncrementCount(typeof(TState));
        }

        return (TState)entry.Materialize();
    }

    public TState GetReadOnly<TState>(AbstractModel model, Func<TState> create)
        where TState : IPredictionStateForkable
        => GetReadOnly(model, create, static factory => factory());

    public TState GetReadOnly<TModel, TState>(TModel model, Func<TModel, TState> create)
        where TModel : AbstractModel
        where TState : IPredictionStateForkable
        => GetReadOnly(model, model, create);

    public TState GetReadOnly<TArgument, TState>(
        AbstractModel model,
        TArgument argument,
        Func<TArgument, TState> create)
        where TState : IPredictionStateForkable
    {
        var key = (ResolveModel(model), typeof(TState));
        if (!_states.TryGetValue(key, out StateEntry? entry))
        {
            object state = create(argument)
                ?? throw new InvalidOperationException("Prediction state factory returned null.");
            entry = new OwnedStateEntry(state);
            _states[key] = entry;
            IncrementCount(typeof(TState));
        }
        return (TState)entry.Read();
    }

    /// <summary>
    /// Reads prediction state without inserting an untouched live-state projection into the store.
    /// Fingerprinting uses this path so observing a state does not make every later fork copy it.
    /// </summary>
    public TState Peek<TState>(AbstractModel model, Func<TState> create)
        where TState : IPredictionStateForkable
        => Peek(model, create, static factory => factory());

    public TState Peek<TModel, TState>(TModel model, Func<TModel, TState> create)
        where TModel : AbstractModel
        where TState : IPredictionStateForkable
        => Peek(model, model, create);

    public TState Peek<TArgument, TState>(
        AbstractModel model,
        TArgument argument,
        Func<TArgument, TState> create)
        where TState : IPredictionStateForkable
    {
        if (_states.TryGetValue((ResolveModel(model), typeof(TState)), out StateEntry? entry))
            return (TState)entry.Read();
        return create(argument)
            ?? throw new InvalidOperationException("Prediction state factory returned null.");
    }

    public bool TryGetReadOnly<TState>(AbstractModel model, out TState? state)
        where TState : class, IPredictionStateForkable
    {
        if (_states.TryGetValue((ResolveModel(model), typeof(TState)), out StateEntry? entry))
        {
            state = (TState)entry.Read();
            return true;
        }
        state = null;
        return false;
    }

    public IEnumerable<(AbstractModel Model, TState State)> ReadEntries<TState>()
        where TState : class, IPredictionStateForkable
    {
        if (!_countByType.TryGetValue(typeof(TState), out int count) || count == 0)
            yield break;
        foreach (((AbstractModel model, Type stateType), StateEntry entry) in _states)
        {
            if (stateType == typeof(TState))
                yield return (model, (TState)entry.Read());
        }
    }

    public bool Remove<TState>(AbstractModel model)
        where TState : class, IPredictionStateForkable
    {
        if (!_states.Remove((ResolveModel(model), typeof(TState))))
            return false;
        _countByType[typeof(TState)]--;
        return true;
    }

    private void IncrementCount(Type stateType)
        => _countByType[stateType] = _countByType.GetValueOrDefault(stateType) + 1;

    public void RemapModel(AbstractModel source, AbstractModel replacement)
    {
        AbstractModel resolvedSource = ResolveModel(source);
        AbstractModel resolvedReplacement = ResolveModel(replacement);
        if (ReferenceEquals(resolvedSource, resolvedReplacement))
            return;

        (AbstractModel Model, Type StateType)[] keys = _states.Keys
            .Where(key => ReferenceEquals(key.Model, resolvedSource))
            .ToArray();
        foreach ((AbstractModel _, Type stateType) in keys)
        {
            StateEntry entry = _states[(resolvedSource, stateType)];
            _states.Remove((resolvedSource, stateType));
            if (!_states.TryAdd((resolvedReplacement, stateType), entry))
                throw new InvalidOperationException($"Prediction state remap collided for {stateType.FullName}.");
        }

        foreach (AbstractModel alias in _modelAliases
                     .Where(pair => ReferenceEquals(pair.Value, resolvedSource))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _modelAliases[alias] = resolvedReplacement;
        }
        _modelAliases[source] = resolvedReplacement;
        _modelAliases[resolvedSource] = resolvedReplacement;
    }

    internal PredictionStateStore Fork(PredictionForkContext context)
    {
        AssertForkable();
        PredictionStateStore fork = new(_states.Count, _modelAliases.Count);
        foreach (((AbstractModel model, Type stateType), StateEntry entry) in _states)
        {
            object state = entry.Read();
            AbstractModel forkedModel = context.RemapOrSelf(model);
            object forkedState;
            if (context.TryRemap(state, out object? existing))
            {
                forkedState = existing!;
            }
            else
            {
                if (state is not IPredictionStateForkable forkable)
                {
                    throw new InvalidOperationException(
                        $"Prediction state {state.GetType().FullName} does not implement {nameof(IPredictionStateForkable)}.");
                }
                forkedState = forkable.Fork(context);
                context.Register(state, forkedState);
            }
            fork._states.Add((forkedModel, stateType), new OwnedStateEntry(forkedState));
        }
        foreach ((Type stateType, int count) in _countByType)
        {
            if (count != 0)
                fork._countByType[stateType] = count;
        }
        foreach ((AbstractModel source, AbstractModel replacement) in _modelAliases)
        {
            AbstractModel forkedSource = context.RemapOrSelf(source);
            AbstractModel forkedReplacement = context.RemapOrSelf(replacement);
            if (!ReferenceEquals(forkedSource, forkedReplacement))
                fork._modelAliases[forkedSource] = forkedReplacement;
        }
        return fork;
    }

    internal void AssertForkable()
    {
        foreach (StateEntry entry in _states.Values)
        {
            if (entry.Read() is IPredictionForkBoundary boundary)
                boundary.AssertForkable();
        }
    }

    private AbstractModel ResolveModel(AbstractModel model)
    {
        AbstractModel current = model;
        for (int guard = 0; guard < 16 && _modelAliases.TryGetValue(current, out AbstractModel? replacement); guard++)
            current = replacement;
        return current;
    }
}
