using System.Reflection;

namespace CombatSolver;

internal static class LoadedModWarnings
{
    private static bool _speedXPresent;

    static LoadedModWarnings()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) => Observe(args.LoadedAssembly);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            Observe(assembly);
    }

    internal static bool SpeedXPresent => Volatile.Read(ref _speedXPresent);

    private static void Observe(Assembly assembly)
    {
        if (string.Equals(assembly.GetName().Name, "SpeedX", StringComparison.Ordinal))
            Volatile.Write(ref _speedXPresent, true);
    }
}
