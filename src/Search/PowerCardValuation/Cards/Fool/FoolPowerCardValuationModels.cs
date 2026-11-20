namespace CombatSolver;

/// <summary>
/// 愚者单人能力牌登记入口。18 张单人 CardType.Power；MultiplayerOnly 的 COMPOST、COVERING_FIRE 不登记。
/// 逐卡理解来自玩家说明，见 docs/strategy/power-card-valuation/fool.md。
/// </summary>
internal static class FoolPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. FoolArrowPowerCardValuationModels.All,
        .. FoolMelodyPowerCardValuationModels.All,
        .. FoolFlowPowerCardValuationModels.All,
    ];
}
