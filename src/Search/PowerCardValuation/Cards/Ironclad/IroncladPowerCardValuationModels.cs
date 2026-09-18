namespace CombatSolver;

/// <summary>
/// 铁甲战士单人能力牌登记入口。19 张单人 CardType.Power；MultiplayerOnly 的 TANK 不登记。
/// 逐卡状态为 QuantifiedDraft，待玩家复核后升级为 Modeled。
/// </summary>
internal static class IroncladPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. IroncladStrengthPowerCardValuationModels.All,
        .. IroncladExhaustPowerCardValuationModels.All,
        .. IroncladDefensePowerCardValuationModels.All,
        .. IroncladTriggerPowerCardValuationModels.All,
        .. IroncladCardFlowPowerCardValuationModels.All,
    ];
}

