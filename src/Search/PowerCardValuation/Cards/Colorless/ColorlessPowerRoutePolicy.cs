namespace CombatSolver;

/// <summary>
/// 无色能力牌逐卡路线政策（Draft，待玩家复核）。无色能力可在任意角色持有，按实际 CardId 识别。
/// </summary>
internal static class ColorlessPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "AUTOMATION" => PowerCommitmentFamily.EnergyEngine,
            "CALAMITY" => PowerCommitmentFamily.CardGenerationEngine,
            "ENTROPY" => PowerCommitmentFamily.CardGenerationEngine,
            "ETERNAL_ARMOR" => PowerCommitmentFamily.DefenseEfficiency,
            "FASTEN" => PowerCommitmentFamily.DexterityGrowth
                | PowerCommitmentFamily.DefenseEfficiency,
            "MAYHEM" => PowerCommitmentFamily.AutoPlayEngine,
            "NOSTALGIA" => PowerCommitmentFamily.RetainEngine
                | PowerCommitmentFamily.CardGenerationEngine,
            "PANACHE" => PowerCommitmentFamily.DamageEngine,
            "PREP_TIME" => PowerCommitmentFamily.StrengthGrowth
                | PowerCommitmentFamily.DexterityGrowth,
            "PROWESS" => PowerCommitmentFamily.StrengthGrowth
                | PowerCommitmentFamily.DexterityGrowth,
            "ROLLING_BOULDER" => PowerCommitmentFamily.DamageEngine,
            "STRATAGEM" => PowerCommitmentFamily.HandEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "AUTOMATION" => new(
                PowerRoutePriority.Core,
                PreferDedicatedSearch: true),
            "CALAMITY" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "ENTROPY" => new(PowerRoutePriority.Strong),
            "ETERNAL_ARMOR" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "FASTEN" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true),
            "MAYHEM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "NOSTALGIA" => new(
                PowerRoutePriority.Strong,
                PreferDedicatedSearch: true),
            "PANACHE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "PREP_TIME" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "PROWESS" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "ROLLING_BOULDER" => new(
                PowerRoutePriority.Normal,
                AllowTriggerBackedProjectionFloor: true),
            "STRATAGEM" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            _ => default,
        };
}

