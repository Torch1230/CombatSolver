namespace CombatSolver;

internal readonly record struct PowerRouteAdmissionInput(
    string CardId,
    bool IsAutoPlay,
    int SpentEnergy,
    int RemainingEnergy,
    bool HasTriggerEvidence,
    int ImmediateDefenseGain,
    int SetupGain,
    int ProjectedPotential,
    int TriggerProjectionFloor,
    int Investment);

internal readonly record struct PowerRouteAdmissionResult(
    bool Admitted,
    int Potential);

/// <summary>
/// 通用能力路线准入判定。所有角色共用同一套顺序；逐卡差异只由 <see cref="PowerRouteAdmissionPolicy" />
/// 表达。固定前缀后验与终局比较都不读取这里的结论。
/// </summary>
internal static class PowerRouteAdmission
{
    internal static PowerRouteAdmissionResult Evaluate(
        in PowerRouteAdmissionInput input,
        in PowerRouteAdmissionPolicy policy)
    {
        Validate(in input);
        if (policy.NoInCombatCommitment
            || !input.HasTriggerEvidence
            || policy.RequirePositiveProjection && input.ProjectedPotential == 0
            || policy.RequireImmediateDefenseGain && input.ImmediateDefenseGain == 0
            || policy.RequireFreeOrSpareActivation
                && input.SpentEnergy > 0
                && input.RemainingEnergy == 0)
        {
            return default;
        }

        int potential = PowerCardValuationMath.SaturatingAddNonNegative(
            input.SetupGain,
            input.ProjectedPotential);
        if (policy.AllowTriggerBackedProjectionFloor)
            potential = Math.Max(potential, input.TriggerProjectionFloor);
        if (policy.PreferFreeActivation
            && !input.IsAutoPlay
            && input.SpentEnergy >= 3
            && potential < input.Investment)
        {
            return default;
        }
        return potential >= policy.MinimumProjection
            ? new PowerRouteAdmissionResult(true, potential)
            : default;
    }

    internal static PowerRoutePriority HighestPriority(IEnumerable<PowerRoutePriority> priorities)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        PowerRoutePriority highest = PowerRoutePriority.Low;
        foreach (PowerRoutePriority priority in priorities)
            highest = priority > highest ? priority : highest;
        return highest;
    }

    private static void Validate(in PowerRouteAdmissionInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.SpentEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(input.RemainingEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ImmediateDefenseGain);
        ArgumentOutOfRangeException.ThrowIfNegative(input.SetupGain);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ProjectedPotential);
        ArgumentOutOfRangeException.ThrowIfNegative(input.TriggerProjectionFloor);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Investment);
    }
}
