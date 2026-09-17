namespace CombatSolver;

/// <summary>
/// 鏃犺壊鑳藉姏鐗岄€愬崱璺嚎鏀跨瓥锛圖raft锛屽緟鐜╁澶嶆牳锛夈€傛棤鑹茶兘鍔涘彲鍦ㄤ换鎰忚鑹叉寔鏈夛紝鎸夊疄闄?CardId 璇嗗埆銆?/// </summary>
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
            "AUTOMATION" => new(PowerRoutePriority.Normal),
            "CALAMITY" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "ENTROPY" => new(PowerRoutePriority.Normal),
            "ETERNAL_ARMOR" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "FASTEN" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            "MAYHEM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "NOSTALGIA" => new(PowerRoutePriority.Normal),
            "PANACHE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            "PREP_TIME" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "PROWESS" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "ROLLING_BOULDER" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "STRATAGEM" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            _ => default,
        };
}

