namespace CombatSolver;

/// <summary>
/// 鏃犺壊鍗曚汉鑳藉姏鐗岀櫥璁板叆鍙ｃ€?2 寮犲崟浜?CardType.Power锛汳ultiplayerOnly 鐨?/// BEACON_OF_HOPE 涓嶇櫥璁般€傛棤鑹茶兘鍔涙寜瀹為檯 CardId 璇嗗埆锛屽彲鍦ㄤ换鎰忚鑹叉寔鏈夈€?/// </summary>
internal static class ColorlessPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. ColorlessGrowthPowerCardValuationModels.All,
        .. ColorlessFlowPowerCardValuationModels.All,
    ];
}

