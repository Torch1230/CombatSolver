namespace CombatSolver;

/// <summary>
/// 原版能力牌模型入口。每个角色只在自己的目录登记；未登记卡牌继续使用
/// 现有估值，不在这里生成默认模型。模型的评审状态记录在对应卡池文档中。
/// </summary>
internal static class PowerCardValuationModels
{
    internal static PowerCardValuationRegistry Registry { get; } = new(
        IroncladPowerCardValuationModels.All
            .Concat(SilentPowerCardValuationModels.All)
            .Concat(DefectPowerCardValuationModels.All)
            .Concat(RegentPowerCardValuationModels.All)
            .Concat(NecrobinderPowerCardValuationModels.All)
            .Concat(ColorlessPowerCardValuationModels.All));
}
