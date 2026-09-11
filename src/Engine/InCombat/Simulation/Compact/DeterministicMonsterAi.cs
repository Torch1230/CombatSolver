namespace CombatSolver.Engine.InCombat.Simulation.Compact;

// Closed deterministic graphs only. All model identities, graph validation and native
// move metadata stay at the admission boundary; the executor receives copied integers.
internal sealed class DeterministicMonsterAi
{
    private readonly int[] _next, _rootLog;
    internal int Owner { get; }
    internal int Current { get; }
    internal int MoveCount => _next.Length;
    internal int Next(int move) => _next[move];
    internal ReadOnlySpan<int> RootLog => _rootLog;

    internal DeterministicMonsterAi(int owner, int current, ReadOnlySpan<int> next, ReadOnlySpan<int> log)
    {
        _next = next.ToArray(); _rootLog = log.ToArray();
        if (owner <= 0 || (uint)current >= (uint)_next.Length
            || _next.Any(move => (uint)move >= (uint)_next.Length)
            || _rootLog.Any(move => (uint)move >= (uint)_next.Length))
            throw new ArgumentException("Invalid deterministic monster graph or root state.");
        Owner = owner; Current = current;
    }
}

internal sealed class DeterministicMonsterAiLayout
{
    private readonly DeterministicMonsterAi _definition;
    private readonly int _current;
    private readonly ReversibleValueBuffer _log;
    internal int Owner => _definition.Owner;
    internal int Current(ReversibleValueState state) => checked((int)state[_current]);
    internal int LogCount(ReversibleValueState state) => _log.Count(state);
    internal int LogAt(ReversibleValueState state, int index) => checked((int)_log.Read(state, index));

    internal DeterministicMonsterAiLayout(ReversibleValueState state, DeterministicMonsterAi definition)
    {
        _definition = definition;
        _current = state.Allocate(1); _log = new(state);
        state.Write(_current, definition.Current);
        foreach (int move in definition.RootLog) _log.Append(state, [move]);
    }

    internal void Advance(ReversibleValueState state)
    {
        int next = _definition.Next(Current(state));
        state.Write(_current, next);
        _log.Append(state, [next]);
    }
}
