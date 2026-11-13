namespace CombatSolver;

/// <summary>Runs synchronous native monster initialization against its root's frozen rules.</summary>
internal readonly struct NativeCombatRulesScope : IDisposable
{
    [ThreadStatic] private static CombatRulesSnapshot? _current;
    internal static CombatRulesSnapshot? Current => _current;
    private readonly CombatRulesSnapshot? _previous;
    internal NativeCombatRulesScope(CombatRulesSnapshot rules)
    {
        _previous = _current;
        _current = rules;
    }
    public void Dispose() => _current = _previous;

}
