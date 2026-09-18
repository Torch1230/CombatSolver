namespace CombatSolver;

/// <summary>
/// 故障机器人单人能力牌登记入口。20 张单人 CardType.Power；MultiplayerOnly 的 ONE_FOR_ALL 不登记。
/// 注意 WhiteNoise 是 CardType.Skill，不属于能力牌模型范围。
/// </summary>
internal static class DefectPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. DefectOrbPowerCardValuationModels.All,
        .. DefectGrowthPowerCardValuationModels.All,
        .. DefectCardFlowPowerCardValuationModels.All,
    ];
}

