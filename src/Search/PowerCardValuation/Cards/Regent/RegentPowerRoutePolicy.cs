namespace CombatSolver;

/// <summary>
/// 储君逐卡路线政策（Draft，待玩家复核）。只登记机制族与准入数据。
/// </summary>
internal static class RegentPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "ARSENAL" => PowerCommitmentFamily.StrengthGrowth
                | PowerCommitmentFamily.CardGenerationEngine,
            "BLACK_HOLE" => PowerCommitmentFamily.StarEngine
                | PowerCommitmentFamily.DamageEngine,
            "CHILD_OF_THE_STARS" => PowerCommitmentFamily.StarEngine
                | PowerCommitmentFamily.DefenseEfficiency,
            "FURNACE" => PowerCommitmentFamily.StarEngine,
            "GENESIS" => PowerCommitmentFamily.StarEngine
                | PowerCommitmentFamily.EnergyEngine,
            "MONARCHS_GAZE" => PowerCommitmentFamily.StatusAmplifier,
            "NEUTRON_AEGIS" => PowerCommitmentFamily.DefenseEfficiency
                | PowerCommitmentFamily.StarEngine,
            "ORBIT" => PowerCommitmentFamily.EnergyEngine,
            "PALE_BLUE_DOT" => PowerCommitmentFamily.HandEngine,
            "PARRY" => PowerCommitmentFamily.DefenseEfficiency,
            "PILLAR_OF_CREATION" => PowerCommitmentFamily.BlockTriggerEngine
                | PowerCommitmentFamily.CardGenerationEngine,
            "ROYALTIES" => PowerCommitmentFamily.CrossCombatGrowth,
            "SEEKING_EDGE" => PowerCommitmentFamily.DamageEngine
                | PowerCommitmentFamily.StarEngine,
            "SPECTRUM_SHIFT" => PowerCommitmentFamily.CardGenerationEngine,
            "SWORD_SAGE" => PowerCommitmentFamily.DamageEngine,
            "THE_SEALED_THRONE" => PowerCommitmentFamily.StarEngine,
            "TYRANNY" => PowerCommitmentFamily.HandEngine,
            "VOID_FORM" => PowerCommitmentFamily.CostReductionEngine
                | PowerCommitmentFamily.AutoPlayEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "ARSENAL" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "BLACK_HOLE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "CHILD_OF_THE_STARS" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "FURNACE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "GENESIS" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "MONARCHS_GAZE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "NEUTRON_AEGIS" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "ORBIT" => new(
                PowerRoutePriority.Core,
                PreferDedicatedSearch: true),
            "PALE_BLUE_DOT" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "PARRY" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "PILLAR_OF_CREATION" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true),
            "ROYALTIES" => new(
                PowerRoutePriority.Low,
                NoInCombatCommitment: true),
            "SEEKING_EDGE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "SPECTRUM_SHIFT" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "SWORD_SAGE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "THE_SEALED_THRONE" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "TYRANNY" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "VOID_FORM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            _ => default,
        };
}

