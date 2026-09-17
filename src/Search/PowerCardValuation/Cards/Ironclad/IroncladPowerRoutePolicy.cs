namespace CombatSolver;

/// <summary>
/// 閾佺敳鎴樺＋閫愬崱璺嚎鏀跨瓥锛圖raft锛屽緟鐜╁澶嶆牳锛夈€傚彧鐧昏鏈哄埗鏃忎笌鍑嗗叆鏁版嵁銆?/// </summary>
internal static class IroncladPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "AGGRESSION" => PowerCommitmentFamily.CardGenerationEngine,
            "BARRICADE" => PowerCommitmentFamily.DefenseEfficiency,
            "CORRUPTION" => PowerCommitmentFamily.CostReductionEngine,
            "CRIMSON_MANTLE" => PowerCommitmentFamily.BlockTriggerEngine
                | PowerCommitmentFamily.LifeInvestment,
            "CRUELTY" => PowerCommitmentFamily.StatusAmplifier,
            "DARK_EMBRACE" => PowerCommitmentFamily.ExhaustEngine,
            "DEMON_FORM" => PowerCommitmentFamily.StrengthGrowth,
            "FEEL_NO_PAIN" => PowerCommitmentFamily.ExhaustEngine,
            "HELLRAISER" => PowerCommitmentFamily.AutoPlayEngine,
            "INFERNO" => PowerCommitmentFamily.LifeInvestment
                | PowerCommitmentFamily.DamageEngine,
            "INFLAME" => PowerCommitmentFamily.StrengthGrowth,
            "JUGGERNAUT" => PowerCommitmentFamily.BlockTriggerEngine,
            "JUGGLING" => PowerCommitmentFamily.AutoPlayEngine
                | PowerCommitmentFamily.CardGenerationEngine,
            "PYRE" => PowerCommitmentFamily.EnergyEngine,
            "RUPTURE" => PowerCommitmentFamily.StrengthGrowth
                | PowerCommitmentFamily.LifeInvestment,
            "STAMPEDE" => PowerCommitmentFamily.AutoPlayEngine,
            "STONE_ARMOR" => PowerCommitmentFamily.DefenseEfficiency,
            "UNMOVABLE" => PowerCommitmentFamily.DefenseEfficiency,
            "VICIOUS" => PowerCommitmentFamily.CardGenerationEngine
                | PowerCommitmentFamily.StatusAmplifier,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "AGGRESSION" => new(PowerRoutePriority.Normal),
            "BARRICADE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "CORRUPTION" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "CRIMSON_MANTLE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "CRUELTY" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "DARK_EMBRACE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "DEMON_FORM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "FEEL_NO_PAIN" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "HELLRAISER" => new(PowerRoutePriority.Normal),
            "INFERNO" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "INFLAME" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "JUGGERNAUT" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "JUGGLING" => new(PowerRoutePriority.Normal),
            "PYRE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "RUPTURE" => new(
                PowerRoutePriority.Normal,
                RequireFreeOrSpareActivation: true),
            "STAMPEDE" => new(PowerRoutePriority.Normal),
            "STONE_ARMOR" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "UNMOVABLE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "VICIOUS" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            _ => default,
        };
}

