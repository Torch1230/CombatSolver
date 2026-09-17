namespace CombatSolver;

/// <summary>
/// 浜＄伒濂戠害甯堝崟浜鸿兘鍔涚墝鐧昏鍏ュ彛銆?8 寮犲崟浜?CardType.Power锛汳ultiplayerOnly 鐨?/// CACOPHONY 涓?SOULBOUND 涓嶇櫥璁般€侳ORBIDDEN_GRIMOIRE 鍙櫥璁拌祫鏂欎笌浼板€硷紝鎴樺悗鏀剁泭涓嶅垱寤烘垬鏂楀唴鎵胯銆?/// </summary>
internal static class NecrobinderPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. NecrobinderDoomPowerCardValuationModels.All,
        .. NecrobinderCardFlowPowerCardValuationModels.All,
    ];
}

