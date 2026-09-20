namespace CombatSolver;

// Boundary for optional features (route cache, showcase capture). A failure here costs
// only that feature. Search, replay and simulator errors must not be routed through it,
// and cancellation always propagates.
internal static class AncillaryWork
{
    internal static T? Try<T>(string operation, Func<T> work, Action<string> log)
    {
        try
        {
            return work();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log($"{operation} error={error}");
            return default;
        }
    }

    internal static void Run(string operation, Action work, Action<string> log)
        => Try(operation, () =>
        {
            work();
            return true;
        }, log);
}
