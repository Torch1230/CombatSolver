namespace CombatSolver;

/// <summary>
/// 鏁呴殰鏈哄櫒浜洪€愬崱璺嚎鏀跨瓥锛圖raft锛屽緟鐜╁澶嶆牳锛夈€傚彧鐧昏鏈哄埗鏃忎笌鍑嗗叆鏁版嵁銆?/// </summary>
internal static class DefectPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "BIASED_COGNITION" => PowerCommitmentFamily.FocusEngine,
            "BUFFER" => PowerCommitmentFamily.DefenseEfficiency,
            "BULK_UP" => PowerCommitmentFamily.StrengthGrowth
                | PowerCommitmentFamily.DexterityGrowth,
            "CAPACITOR" => PowerCommitmentFamily.OrbEngine,
            "CONSUMING_SHADOW" => PowerCommitmentFamily.OrbEngine,
            "COOLANT" => PowerCommitmentFamily.OrbEngine
                | PowerCommitmentFamily.DefenseEfficiency,
            "CREATIVE_AI" => PowerCommitmentFamily.CardGenerationEngine,
            "DEFRAGMENT" => PowerCommitmentFamily.FocusEngine,
            "ECHO_FORM" => PowerCommitmentFamily.AutoPlayEngine
                | PowerCommitmentFamily.CostReductionEngine,
            "FERAL" => PowerCommitmentFamily.CostReductionEngine,
            "HAILSTORM" => PowerCommitmentFamily.OrbEngine
                | PowerCommitmentFamily.DamageEngine,
            "ITERATION" => PowerCommitmentFamily.HandEngine,
            "LOOP" => PowerCommitmentFamily.OrbEngine,
            "MACHINE_LEARNING" => PowerCommitmentFamily.HandEngine,
            "SMOKESTACK" => PowerCommitmentFamily.CardGenerationEngine
                | PowerCommitmentFamily.DamageEngine,
            "SPINNER" => PowerCommitmentFamily.OrbEngine,
            "STORM" => PowerCommitmentFamily.OrbEngine,
            "SUBROUTINE" => PowerCommitmentFamily.EnergyEngine,
            "THUNDER" => PowerCommitmentFamily.OrbEngine
                | PowerCommitmentFamily.DamageEngine,
            "TRASH_TO_TREASURE" => PowerCommitmentFamily.OrbEngine
                | PowerCommitmentFamily.CardGenerationEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "BIASED_COGNITION" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "BUFFER" => new(
                PowerRoutePriority.Strong,
                RequireImmediateDefenseGain: true),
            "BULK_UP" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "CAPACITOR" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "CONSUMING_SHADOW" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "COOLANT" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "CREATIVE_AI" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "DEFRAGMENT" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "ECHO_FORM" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "FERAL" => new(PowerRoutePriority.Normal),
            "HAILSTORM" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "ITERATION" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "LOOP" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "MACHINE_LEARNING" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "SMOKESTACK" => new(PowerRoutePriority.Normal),
            "SPINNER" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "STORM" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true),
            "SUBROUTINE" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true),
            "THUNDER" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "TRASH_TO_TREASURE" => new(PowerRoutePriority.Normal),
            _ => default,
        };
}

