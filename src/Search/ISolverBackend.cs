using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal enum SolverBackendKind
{
    Legacy,
    Hot,
}

internal interface ISolverBackend
{
    SolverResult SolveCoordinator(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback);

    SolverResult SolveSingleBeam(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken);

    LiveEndTurnRiskProjection EvaluateEndTurnRisk(
        CombatState liveState,
        IReadOnlyList<PlanCardChoice>? turnStartChoices);
}

internal sealed class LegacySolverBackend : ISolverBackend
{
    public static LegacySolverBackend Instance { get; } = new();

    public SolverResult SolveCoordinator(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
        => CombatSearchCoordinator.Solve(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback);

    public SolverResult SolveSingleBeam(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken)
        => new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            searchProfile: policy.Profile).Solve();

    public LiveEndTurnRiskProjection EvaluateEndTurnRisk(
        CombatState liveState,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
        => LiveEndTurnRiskEvaluator.Evaluate(liveState, turnStartChoices);
}

internal static class SolverBackendSelector
{
    public static SolverBackendKind Current { get; set; } = SolverBackendKind.Legacy;

    public static ISolverBackend Resolve(SolverBackendKind? kind = null)
        => (kind ?? Current) switch
        {
            SolverBackendKind.Legacy => LegacySolverBackend.Instance,
            SolverBackendKind.Hot => HotSearch.HotSolverBackend.Instance,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
