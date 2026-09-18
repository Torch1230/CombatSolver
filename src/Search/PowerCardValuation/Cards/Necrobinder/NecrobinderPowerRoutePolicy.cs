namespace CombatSolver;

/// <summary>
/// 亡灵契约师逐卡路线政策（Draft，待玩家复核）。只登记机制族与准入数据。
/// </summary>
internal static class NecrobinderPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "CALCIFY" => PowerCommitmentFamily.SummonEngine,
            "CALL_OF_THE_VOID" => PowerCommitmentFamily.CardGenerationEngine,
            "COUNTDOWN" => PowerCommitmentFamily.DoomEngine,
            "DANSE_MACABRE" => PowerCommitmentFamily.BlockTriggerEngine,
            "DEMESNE" => PowerCommitmentFamily.EnergyEngine
                | PowerCommitmentFamily.HandEngine,
            "DEVOUR_LIFE" => PowerCommitmentFamily.SummonEngine,
            "FORBIDDEN_GRIMOIRE" => PowerCommitmentFamily.CrossCombatGrowth,
            "FRIENDSHIP" => PowerCommitmentFamily.EnergyEngine,
            "HAUNT" => PowerCommitmentFamily.DoomEngine
                | PowerCommitmentFamily.DamageEngine,
            "LETHALITY" => PowerCommitmentFamily.DamageEngine,
            "NECRO_MASTERY" => PowerCommitmentFamily.SummonEngine
                | PowerCommitmentFamily.DamageEngine,
            "NEUROSURGE" => PowerCommitmentFamily.EnergyEngine
                | PowerCommitmentFamily.LifeInvestment,
            "PAGESTORM" => PowerCommitmentFamily.HandEngine,
            "REAPER_FORM" => PowerCommitmentFamily.DoomEngine
                | PowerCommitmentFamily.DamageEngine,
            "SENTRY_MODE" => PowerCommitmentFamily.CardGenerationEngine,
            "SHROUD" => PowerCommitmentFamily.BlockTriggerEngine
                | PowerCommitmentFamily.DoomEngine,
            "SLEIGHT_OF_FLESH" => PowerCommitmentFamily.DamageEngine
                | PowerCommitmentFamily.StatusAmplifier,
            "SPIRIT_OF_ASH" => PowerCommitmentFamily.BlockTriggerEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "CALCIFY" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "CALL_OF_THE_VOID" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "COUNTDOWN" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "DANSE_MACABRE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "DEMESNE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "DEVOUR_LIFE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "FORBIDDEN_GRIMOIRE" => new(
                PowerRoutePriority.Low,
                NoInCombatCommitment: true),
            "FRIENDSHIP" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "HAUNT" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "LETHALITY" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "NECRO_MASTERY" => new(
                PowerRoutePriority.Normal,
                AllowTriggerBackedProjectionFloor: true),
            "NEUROSURGE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "PAGESTORM" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "REAPER_FORM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "SENTRY_MODE" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "SHROUD" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "SLEIGHT_OF_FLESH" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "SPIRIT_OF_ASH" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            _ => default,
        };
}

