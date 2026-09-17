namespace CombatSolver;

/// <summary>
/// 鍌ㄥ悰鍗曚汉鑳藉姏鐗岀櫥璁板叆鍙ｃ€?8 寮犲崟浜?CardType.Power锛汳ultiplayerOnly 鐨?HAMMER_TIME 涓嶇櫥璁般€?/// ROYALTIES 鍙櫥璁拌祫鏂欎笌浼板€硷紝鎴樺悗鏀剁泭涓嶅垱寤烘垬鏂楀唴鎵胯銆?/// </summary>
internal static class RegentPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. RegentStarPowerCardValuationModels.All,
        .. RegentControlPowerCardValuationModels.All,
    ];
}

