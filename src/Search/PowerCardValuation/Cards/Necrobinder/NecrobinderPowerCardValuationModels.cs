namespace CombatSolver;

/// <summary>
/// 亡灵契约师单人能力牌登记入口。18 张单人 CardType.Power；MultiplayerOnly 的
/// CACOPHONY 与 SOULBOUND 不登记。FORBIDDEN_GRIMOIRE 只登记资料与估值，战后收益不创建战斗内承诺。
/// </summary>
internal static class NecrobinderPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. NecrobinderDoomPowerCardValuationModels.All,
        .. NecrobinderCardFlowPowerCardValuationModels.All,
    ];
}

