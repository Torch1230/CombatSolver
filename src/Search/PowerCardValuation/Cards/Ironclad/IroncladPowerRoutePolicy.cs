namespace CombatSolver;

/// <summary>
/// 铁甲战士逐卡路线政策（Draft，待玩家复核）。只登记机制族与准入数据。
/// </summary>
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
            "AGGRESSION" => new(
                PowerRoutePriority.Normal,
                PreferDedicatedSearch: true),
            "BARRICADE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "CORRUPTION" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "CRIMSON_MANTLE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "CRUELTY" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "DARK_EMBRACE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "DEMON_FORM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "FEEL_NO_PAIN" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "HELLRAISER" => new(PowerRoutePriority.Low),
            "INFERNO" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "INFLAME" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "JUGGERNAUT" => new(
                PowerRoutePriority.Normal,
                AllowTriggerBackedProjectionFloor: true),
            "JUGGLING" => new(PowerRoutePriority.Low),
            "PYRE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "RUPTURE" => new(
                PowerRoutePriority.Normal,
                RequireFreeOrSpareActivation: true,
                PreferDedicatedSearch: true),
            "STAMPEDE" => new(PowerRoutePriority.Normal),
            "STONE_ARMOR" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "UNMOVABLE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "VICIOUS" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true),
            _ => default,
        };
}

