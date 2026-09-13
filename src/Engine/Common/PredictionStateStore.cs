using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed class PredictionStateStore
{
    // State values are already owned and eagerly forked. A separate heap-allocated entry
    // around every value adds no isolation; keep the value directly in the same ordered table.
    private Dictionary<(AbstractModel Model, Type StateType), object>? _states;
    private Dictionary<AbstractModel, AbstractModel>? _modelAliases;
    // 每种状态类型当前的条目数。ReadEntries<TState> 是热路径（每次动作后都要同步 Power 数量），
    // 绝大多数调用时该类型根本没有条目，用计数直接短路，避免整表扫描。
    private TypeEntryCounts? _countByType;

    public PredictionStateStore()
        : this(0, 0)
    {
    }

    private PredictionStateStore(int stateCapacity, int aliasCapacity)
    {
        if (stateCapacity > 0)
            _states = new(stateCapacity);
        if (aliasCapacity > 0)
            _modelAliases = new(aliasCapacity);
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
        if (_states is null || !_states.TryGetValue(key, out object? state))
        {
            state = create(argument)
                ?? throw new InvalidOperationException("Prediction state factory returned null.");
            (_states ??= [])[key] = state;
            IncrementCount(typeof(TState));
        }

        return (TState)state;
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
        if (_states is null || !_states.TryGetValue(key, out object? state))
        {
            state = create(argument)
                ?? throw new InvalidOperationException("Prediction state factory returned null.");
            (_states ??= [])[key] = state;
            IncrementCount(typeof(TState));
        }
        return (TState)state;
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
        var key = (ResolveModel(model), typeof(TState));
        if (_states is not null && _states.TryGetValue(key, out object? state))
            return (TState)state;
        return create(argument)
            ?? throw new InvalidOperationException("Prediction state factory returned null.");
    }

    public bool TryGetReadOnly<TState>(AbstractModel model, out TState? state)
        where TState : class, IPredictionStateForkable
    {
        var key = (ResolveModel(model), typeof(TState));
        if (_states is not null && _states.TryGetValue(key, out object? value))
        {
            state = (TState)value;
            return true;
        }
        state = null;
        return false;
    }

    public bool HasEntries<TState>()
        where TState : class, IPredictionStateForkable
        => _countByType is not null
            && _countByType.Get(typeof(TState)) != 0;

    public IEnumerable<(AbstractModel Model, TState State)> ReadEntries<TState>()
        where TState : class, IPredictionStateForkable
    {
        if (!HasEntries<TState>())
            yield break;
        foreach (((AbstractModel model, Type stateType), object state) in _states!)
        {
            if (stateType == typeof(TState))
                yield return (model, (TState)state);
        }
    }

    public bool Remove<TState>(AbstractModel model)
        where TState : class, IPredictionStateForkable
    {
        var key = (ResolveModel(model), typeof(TState));
        if (_states is null || !_states.Remove(key))
            return false;
        _countByType!.Decrement(typeof(TState));
        return true;
    }

    private void IncrementCount(Type stateType)
    {
        (_countByType ??= new()).Increment(stateType);
    }

    public void RemapModel(AbstractModel source, AbstractModel replacement)
    {
        AbstractModel resolvedSource = ResolveModel(source);
        AbstractModel resolvedReplacement = ResolveModel(replacement);
        if (ReferenceEquals(resolvedSource, resolvedReplacement))
            return;

        if (_states is not null)
        {
            (AbstractModel Model, Type StateType)[] keys = _states.Keys
                .Where(key => ReferenceEquals(key.Model, resolvedSource))
                .ToArray();
            foreach ((AbstractModel _, Type stateType) in keys)
            {
                object state = _states[(resolvedSource, stateType)];
                _states.Remove((resolvedSource, stateType));
                if (!_states.TryAdd((resolvedReplacement, stateType), state))
                    throw new InvalidOperationException($"Prediction state remap collided for {stateType.FullName}.");
            }
        }

        _modelAliases ??= [];
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
        PredictionStateStore fork = new(_states?.Count ?? 0, _modelAliases?.Count ?? 0);
        if (_states is { Count: > 0 })
        {
            foreach (((AbstractModel model, Type stateType), object state) in _states)
            {
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
                fork._states!.Add((forkedModel, stateType), forkedState);
            }
            fork._countByType = _countByType!.Fork();
        }
        if (_modelAliases is not null)
        {
            foreach ((AbstractModel source, AbstractModel replacement) in _modelAliases)
            {
                AbstractModel forkedSource = context.RemapOrSelf(source);
                AbstractModel forkedReplacement = context.RemapOrSelf(replacement);
                if (!ReferenceEquals(forkedSource, forkedReplacement))
                    fork._modelAliases![forkedSource] = forkedReplacement;
            }
        }
        return fork;
    }

    internal void AssertForkable()
    {
        if (_states is null)
            return;
        foreach (object state in _states.Values)
        {
            if (state is IPredictionForkBoundary boundary)
                boundary.AssertForkable();
        }
    }

    private AbstractModel ResolveModel(AbstractModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        AbstractModel current = model;
        if (_modelAliases is null)
            return current;
        for (int guard = 0; guard < 16 && _modelAliases.TryGetValue(current, out AbstractModel? replacement); guard++)
            current = replacement;
        return current;
    }

    // Three common type counts stay in one object; unused stores still allocate none.
    // Zero entries remain until Fork, just as they did in the dictionary representation.
    private sealed class TypeEntryCounts
    {
        private Type? _first, _second, _third;
        private int _firstCount, _secondCount, _thirdCount;
        private Dictionary<Type, int>? _overflow;

        public int Get(Type type)
        {
            if (_overflow is { } map)
                return map.GetValueOrDefault(type);
            if (ReferenceEquals(type, _first))
                return _firstCount;
            if (ReferenceEquals(type, _second))
                return _secondCount;
            return ReferenceEquals(type, _third) ? _thirdCount : 0;
        }

        public void Increment(Type type)
        {
            if (_overflow is { } map)
            {
                // Consume the ref immediately. State factories run before this method.
                CollectionsMarshal.GetValueRefOrAddDefault(map, type, out _)++;
                return;
            }
            Set(type, Get(type) + 1);
        }

        public void Decrement(Type type)
        {
            if (_overflow is { } map)
            {
                map[type]--;
                return;
            }
            if (ReferenceEquals(type, _first))
            {
                _firstCount--;
                return;
            }
            if (ReferenceEquals(type, _second))
            {
                _secondCount--;
                return;
            }
            if (ReferenceEquals(type, _third))
            {
                _thirdCount--;
                return;
            }
            throw new KeyNotFoundException();
        }

        private void Set(Type type, int count)
        {
            if (_overflow is { } map)
            {
                map[type] = count;
                return;
            }
            if (ReferenceEquals(type, _first) || _first == null)
            {
                _first = type;
                _firstCount = count;
                return;
            }
            if (ReferenceEquals(type, _second) || _second == null)
            {
                _second = type;
                _secondCount = count;
                return;
            }
            if (ReferenceEquals(type, _third) || _third == null)
            {
                _third = type;
                _thirdCount = count;
                return;
            }
            _overflow = new(4)
            {
                [_first] = _firstCount,
                [_second] = _secondCount,
                [_third] = _thirdCount,
                [type] = count,
            };
            _first = _second = _third = null;
        }

        public TypeEntryCounts? Fork()
        {
            TypeEntryCounts? copy = null;
            if (_overflow is { } map)
            {
                foreach (var (type, count) in map)
                    if (count != 0)
                        (copy ??= new()).Set(type, count);
            }
            else
            {
                if (_firstCount != 0)
                    (copy ??= new()).Set(_first!, _firstCount);
                if (_secondCount != 0)
                    (copy ??= new()).Set(_second!, _secondCount);
                if (_thirdCount != 0)
                    (copy ??= new()).Set(_third!, _thirdCount);
            }
            return copy;
        }
    }
}
