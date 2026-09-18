namespace CombatSolver;

/// <summary>
/// 无色单人能力牌登记入口。12 张单人 CardType.Power；MultiplayerOnly 的 BEACON_OF_HOPE 不登记。
/// 无色能力按实际 CardId 识别，可在任意角色持有。
/// </summary>
internal static class ColorlessPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. ColorlessGrowthPowerCardValuationModels.All,
        .. ColorlessFlowPowerCardValuationModels.All,
    ];
}

