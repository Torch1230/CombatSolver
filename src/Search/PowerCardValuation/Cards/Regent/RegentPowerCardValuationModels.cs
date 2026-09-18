namespace CombatSolver;

/// <summary>
/// 储君单人能力牌登记入口。18 张单人 CardType.Power；MultiplayerOnly 的 HAMMER_TIME 不登记。
/// ROYALTIES 只登记资料与估值，战后收益不创建战斗内承诺。
/// </summary>
internal static class RegentPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. RegentStarPowerCardValuationModels.All,
        .. RegentControlPowerCardValuationModels.All,
    ];
}

