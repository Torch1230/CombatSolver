namespace CombatSolver;

internal static class PolicyCheck
{
    public static int Completed { get; private set; }

    public static void Run(string name, Action check)
    {
        try
        {
            check();
            Completed++;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Policy check failed: {name}", error);
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

// Only game-host glue is substituted. The runtime policy itself is linked from production source;
// these checks never request a NoGC region, force GC or trim the process working set.
internal static class Entry
{
    public static CheckLogger Logger { get; } = new();
}

internal sealed class CheckLogger
{
    public Action<string>? InfoSink { get; set; }
    public void Info(string message) => InfoSink?.Invoke(message);
    public void Warn(string message) { }
    public void Error(string message) => throw new InvalidOperationException(message);
}

internal static class UnattendedTestRunner
{
    public static bool IsActive => false;
}
