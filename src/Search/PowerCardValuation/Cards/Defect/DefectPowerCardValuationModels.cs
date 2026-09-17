namespace CombatSolver;

/// <summary>
/// 鏁呴殰鏈哄櫒浜哄崟浜鸿兘鍔涚墝鐧昏鍏ュ彛銆?0 寮犲崟浜?CardType.Power锛汳ultiplayerOnly 鐨?ONE_FOR_ALL 涓嶇櫥璁般€?/// 娉ㄦ剰 WhiteNoise 鏄?CardType.Skill锛屼笉灞炰簬鑳藉姏鐗屾ā鍨嬭寖鍥淬€?/// </summary>
internal static class DefectPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        .. DefectOrbPowerCardValuationModels.All,
        .. DefectGrowthPowerCardValuationModels.All,
        .. DefectCardFlowPowerCardValuationModels.All,
    ];
}

