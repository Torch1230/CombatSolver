namespace CombatSolver;

// Route-cache and showcase file/protocol failures cost only those optional features.
// Programming errors, resource exhaustion and cancellation retain their original failure path.
internal static class AncillaryWork
{
    internal static T? Try<T>(string operation, Func<T> work, Action<string> log)
    {
        try
        {
            return work();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or System.Text.Json.JsonException
            or InvalidDataException or NotSupportedException)
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
