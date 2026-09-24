using System.Runtime;

namespace CombatSolver;

internal enum RuntimeGcProfileStatus
{
    Default,
    Active,
    ServerGcUnavailable,
    UnknownProfile,
}

internal readonly record struct RuntimeGcProfileSelection(
    string? RequestedProfile,
    bool IsServerGc,
    RuntimeGcProfileStatus Status)
{
    public bool IsActive => Status == RuntimeGcProfileStatus.Active;

    // Apply only to the effective search snapshot. The saved setting remains the
    // player's choice for an ordinary launch, including an explicit NoGC=true.
    public bool ResolveEnableNoGcRegion(bool configured) => configured && !IsActive;
}

internal static class RuntimeGcProfile
{
    public const string EnvironmentVariable = "COMBATSOLVER_RUNTIME_PROFILE";
    public const string ServerGenerational = "server-generational";

    // CLR GC mode is fixed at startup. Read the opt-in once as well, so changing
    // the process environment cannot switch policy midway through a search.
    public static RuntimeGcProfileSelection Current { get; } = Resolve(
        ResolveRequest(Environment.GetEnvironmentVariable(EnvironmentVariable),
            AppContext.GetData("CombatSolver.RuntimeProfile") as string), GCSettings.IsServerGC);

    internal static string? ResolveRequest(string? environment, string? startup)
        => string.IsNullOrWhiteSpace(environment) ? startup : environment;

    internal static RuntimeGcProfileSelection Resolve(string? requestedProfile, bool isServerGc)
    {
        string? requested = requestedProfile?.Trim();
        RuntimeGcProfileStatus status = string.IsNullOrEmpty(requested)
            ? RuntimeGcProfileStatus.Default
            : !string.Equals(requested, ServerGenerational, StringComparison.OrdinalIgnoreCase)
                ? RuntimeGcProfileStatus.UnknownProfile
                : isServerGc
                    ? RuntimeGcProfileStatus.Active
                    : RuntimeGcProfileStatus.ServerGcUnavailable;
        return new RuntimeGcProfileSelection(requested, isServerGc, status);
    }
}
