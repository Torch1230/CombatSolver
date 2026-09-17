namespace CombatSolver;

internal readonly record struct SilentPowerRouteAdmissionInput(
    SilentPowerCardIdentity Card,
    bool IsAutoPlay,
    int SpentEnergy,
    int RemainingEnergy,
    bool HasTriggerEvidence,
    int ImmediateDefenseGain,
    int SetupGain,
    int ProjectedPotential,
    int TriggerProjectionFloor,
    int Investment);

internal readonly record struct SilentPowerRouteAdmissionResult(
    bool Admitted,
    int Potential);

internal static class SilentPowerRouteAdmission
{
    internal static SilentPowerRouteAdmissionResult Evaluate(
        in SilentPowerRouteAdmissionInput input)
    {
        Validate(in input);
        SilentPowerRoutePolicy policy = SilentPowerRoutePolicy.For(input.Card);
        if (!input.HasTriggerEvidence
            || input.Card == SilentPowerCardIdentity.MasterPlanner
                && input.ProjectedPotential == 0
            || policy.RequireImmediateDefenseGain
                && input.ImmediateDefenseGain == 0
            || policy.RequireFreeOrSpareActivation
                && input.SpentEnergy > 0
                && input.RemainingEnergy == 0)
        {
            return default;
        }

        int potential = SaturatingAdd(input.SetupGain, input.ProjectedPotential);
        if (policy.AllowTriggerBackedProjectionFloor)
            potential = Math.Max(potential, input.TriggerProjectionFloor);
        if (policy.PreferSlyActivation
            && !input.IsAutoPlay
            && input.SpentEnergy >= 3
            && potential < input.Investment)
        {
            return default;
        }
        return potential >= policy.MinimumProjection
            ? new SilentPowerRouteAdmissionResult(true, potential)
            : default;
    }

    private static void Validate(in SilentPowerRouteAdmissionInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input.SpentEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(input.RemainingEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ImmediateDefenseGain);
        ArgumentOutOfRangeException.ThrowIfNegative(input.SetupGain);
        ArgumentOutOfRangeException.ThrowIfNegative(input.ProjectedPotential);
        ArgumentOutOfRangeException.ThrowIfNegative(input.TriggerProjectionFloor);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Investment);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left + right);
}
