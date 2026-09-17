namespace CombatSolver;

/// <summary>
/// 閾佺敳鎴樺＋鍗曚汉鑳藉姏鐗岀櫥璁板叆鍙ｃ€?9 寮犲崟浜?CardType.Power锛汳ultiplayerOnly 鐨?TANK 涓嶇櫥璁般€?/// 閫愬崱鐘舵€佷负 QuantifiedDraft锛屽緟鐜╁澶嶆牳鍚庡崌绾т负 Modeled銆?/// </summary>
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

