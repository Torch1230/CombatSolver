namespace CombatSolver;

internal static class SimulationNotificationIsolation
{
    [ThreadStatic]
    private static int _depth;

    [ThreadStatic]
    private static bool _loggedSuppression;

    [ThreadStatic]
    private static object? _scopeIdentity;

    internal static object? ScopeIdentity => _scopeIdentity;

    public static bool IsActive => _depth > 0;

    public static IDisposable Enter()
    {
        if (_depth++ == 0)
        {
            _loggedSuppression = false;
            _scopeIdentity = new object();
        }
        return new Scope();
    }

    public static void LogSuppression(string caller)
    {
        if (_loggedSuppression)
            return;
        _loggedSuppression = true;
        Entry.Logger.Info(
            $"[CombatSolver/Test] SIMULATION_NOTIFICATION_SUPPRESSED caller={caller} thread={Environment.CurrentManagedThreadId}");
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _depth--;
            if (_depth == 0)
                _scopeIdentity = null;
        }
    }
}
