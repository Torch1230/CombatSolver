namespace CombatSolver;

/// <summary>
/// 按卡池把通用能力承诺请求路由到各角色目录的逐卡实现。公共层只认识
/// <see cref="PowerCommitmentDescriptor" />，不认识任何角色的枚举。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool PowerHasTriggerEvidence(
        in PowerCommitmentDescriptor descriptor,
        SearchNode parent,
        SearchNode child)
        => descriptor.Pool switch
        {
            PowerCardPool.Ironclad => IroncladPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            PowerCardPool.Silent => SilentPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            PowerCardPool.Defect => DefectPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            PowerCardPool.Regent => RegentPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            PowerCardPool.Necrobinder => NecrobinderPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            PowerCardPool.Colorless => ColorlessPowerHasTriggerEvidence(descriptor.CardId, parent, child),
            _ => false,
        };

    private int PowerTriggerProjectionFloor(
        in PowerCommitmentDescriptor descriptor,
        SearchNode child)
        => descriptor.Pool switch
        {
            PowerCardPool.Ironclad => IroncladPowerTriggerProjectionFloor(descriptor.CardId, child),
            PowerCardPool.Silent => SilentPowerTriggerProjectionFloor(descriptor.CardId, child),
            PowerCardPool.Defect => DefectPowerTriggerProjectionFloor(descriptor.CardId, child),
            PowerCardPool.Regent => RegentPowerTriggerProjectionFloor(descriptor.CardId, child),
            PowerCardPool.Necrobinder => NecrobinderPowerTriggerProjectionFloor(descriptor.CardId, child),
            PowerCardPool.Colorless => ColorlessPowerTriggerProjectionFloor(descriptor.CardId, child),
            _ => 0,
        };

    private int PowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        if (!PowerCardValuationModels.Registry.TryGetPool(cardId, out PowerCardPool pool))
            return 0;
        return pool switch
        {
            PowerCardPool.Ironclad => IroncladPowerOpeningProjectionPotential(cardId, parent, child),
            PowerCardPool.Silent => SilentPowerOpeningProjectionPotential(cardId, parent, child),
            PowerCardPool.Defect => DefectPowerOpeningProjectionPotential(cardId, parent, child),
            PowerCardPool.Regent => RegentPowerOpeningProjectionPotential(cardId, parent, child),
            PowerCardPool.Necrobinder => NecrobinderPowerOpeningProjectionPotential(cardId, parent, child),
            PowerCardPool.Colorless => ColorlessPowerOpeningProjectionPotential(cardId, parent, child),
            _ => 0,
        };
    }
}
