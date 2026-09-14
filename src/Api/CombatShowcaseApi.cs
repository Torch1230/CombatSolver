namespace CombatSolver.Api;

public sealed record CombatShowcaseCompatibility(
    int RouteProtocolVersion,
    string GameVersion,
    string SolverVersion);

public sealed record CombatShowcaseEnterResult(
    string BundleId,
    string CharacterId,
    string EncounterId,
    int CombatEndedTurn,
    int TurnCount,
    int LocalSearchStarts);

public static class CombatShowcaseApi
{
    public static CombatShowcaseCompatibility GetCompatibility()
        => CombatShowcaseRuntime.GetCompatibility();

    public static Task<CombatShowcaseEnterResult> EnterAsync(string bundlePath)
        => CombatShowcaseRuntime.EnterAsync(bundlePath);
}
