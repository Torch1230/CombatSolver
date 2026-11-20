namespace CombatSolver;

/// <summary>
/// 愚者逐卡路线政策（Specified）。机制族、优先级与准入条件来自玩家逐卡说明，见
/// docs/strategy/power-card-valuation/fool.md。MultiplayerOnly 的 COMPOST、COVERING_FIRE 不登记。
/// </summary>
internal static class FoolPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "ALL_IS_WELL" => PowerCommitmentFamily.BlockTriggerEngine,
            "ARCHERY" => PowerCommitmentFamily.DamageEngine,
            "ECSTASY" => PowerCommitmentFamily.CardGenerationEngine,
            "ENNUI" => PowerCommitmentFamily.HandEngine,
            "EXPLOSIVE_ARROWS" => PowerCommitmentFamily.DamageEngine,
            "HIDDEN_ROOTS" => PowerCommitmentFamily.DefenseEfficiency,
            "HONING" => PowerCommitmentFamily.DamageEngine,
            "LOST_LUTE" => PowerCommitmentFamily.AutoPlayEngine,
            "LULLABY" => PowerCommitmentFamily.DefenseEfficiency,
            "MOTIF" => PowerCommitmentFamily.HandEngine,
            "MYTHIC_FORM" => PowerCommitmentFamily.CardGenerationEngine
                | PowerCommitmentFamily.RandomGeneration,
            "QUIVER" => PowerCommitmentFamily.DamageEngine,
            "SERRATED_ARROWS" => PowerCommitmentFamily.StatusAmplifier,
            "SIMPLETON" => PowerCommitmentFamily.AutoPlayEngine,
            "SONG_PATH" => PowerCommitmentFamily.DefenseEfficiency
                | PowerCommitmentFamily.BlockTriggerEngine,
            "TUNE" => PowerCommitmentFamily.CostReductionEngine,
            "VIOLENT_CHORD" => PowerCommitmentFamily.DamageEngine,
            "YAH" => PowerCommitmentFamily.EnergyEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            // 整体表现不佳，只在牌区有悲伤来源时保护。
            "ALL_IS_WELL" => new(
                PowerRoutePriority.Low,
                RequirePositiveProjection: true),
            // 长线战斗才值得；剩余回合少时投影自然变小。
            "ARCHERY" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            // 手里没有悲伤时不开。
            "ECSTASY" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            // 悲伤保留占手牌，长线会把手牌塞死：只在短线且多抽净收益为正时保护，不给触发保底。
            "ENNUI" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            // 多敌人时优先开。
            "EXPLOSIVE_ARROWS" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            // 防御牌少、长线、爆发型敌人时优先开。
            "HIDDEN_ROOTS" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            // 有大额箭类攻击时才值得开。
            "HONING" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 多张旋律牌时优先，并应先于手中旋律牌打出。
            "LOST_LUTE" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 0 费，几乎任何时候都先开，且应先于手中旋律牌。
            "LULLABY" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            // 旋律牌越多越值，参考亡灵契约师书页风暴。
            "MOTIF" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 长线战斗才值得，费用充足时优先。
            "MYTHIC_FORM" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 多张射箭牌、长线战斗优先。
            "QUIVER" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 非射箭攻击多时优先，价值约等于易伤。
            "SERRATED_ARROWS" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true),
            // 只在手牌不多于3张时考虑。
            "SIMPLETON" => new(
                PowerRoutePriority.Low,
                RequirePositiveProjection: true),
            // 多数情况优先开。
            "SONG_PATH" => new(
                PowerRoutePriority.Core,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 有叽旋律牌、高费牌或保留时优先。
            "TUNE" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            // 多张哇旋律牌或多个敌人时优先。
            "VIOLENT_CHORD" => new(
                PowerRoutePriority.Strong,
                RequirePositiveProjection: true,
                PreferDedicatedSearch: true),
            // 默认倾向不开，只在有拔箭来源时保护。
            "YAH" => new(
                PowerRoutePriority.Low,
                RequirePositiveProjection: true),
            _ => default,
        };
}
