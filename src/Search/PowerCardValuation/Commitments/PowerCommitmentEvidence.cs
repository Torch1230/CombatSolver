using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct PowerEvidenceContribution(bool Handled, int Gain);

internal sealed partial class CombatBeamSolver
{
    private int PowerCommitmentProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => PowerCardValuationMath.SaturatingSum(
            SilentPowerProgressEvidence(commitment, parent, child),
            IroncladPowerProgressEvidence(commitment, parent, child),
            DefectPowerProgressEvidence(commitment, parent, child),
            RegentPowerProgressEvidence(commitment, parent, child),
            NecrobinderPowerProgressEvidence(commitment, parent, child),
            ColorlessPowerProgressEvidence(commitment, parent, child));

    /// <summary>
    /// 路线进展／解除保护信号，不是“收益由该能力造成”的因果归因。固定前缀专搜已负责把能力路线
    /// 算到底，承诺席位只负责避免刚开能力就被剪掉；一旦路线在攻击、防御或牌流上取得进展，就应及时
    /// 释放席位。专用证据只描述对应能力可直接观察到的进展，未命中时统一回退到通用战术进展。
    /// </summary>
    private int PowerCommitmentRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
    {
        long gain = 0;
        bool hasSpecializedEvidence = false;
        AddEvidence(SilentPowerRealizedEvidence(commitment, parent, child));
        AddEvidence(IroncladPowerRealizedEvidence(commitment, parent, child));
        AddEvidence(DefectPowerRealizedEvidence(commitment, parent, child));
        AddEvidence(RegentPowerRealizedEvidence(commitment, parent, child));
        AddEvidence(NecrobinderPowerRealizedEvidence(commitment, parent, child));
        AddEvidence(ColorlessPowerRealizedEvidence(commitment, parent, child));
        if (!hasSpecializedEvidence)
            gain += GenericPowerCommitmentEvidence(parent.Snapshot, child.Snapshot);
        return (int)Math.Min(int.MaxValue, gain);

        void AddEvidence(PowerEvidenceContribution contribution)
        {
            hasSpecializedEvidence |= contribution.Handled;
            gain += contribution.Gain;
        }
    }

    /// <summary>
    /// 通用战术进展：普通攻击、可达手牌、零费可打与预计生命的改善都视为路线已经前进，
    /// 用来释放仍在租约中的承诺席位。这里衡量的是路线进展，不宣称收益来自能力本身。
    /// </summary>
    private static int GenericPowerCommitmentEvidence(
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        long gain = Math.Max(0, after.OffensiveProgressValue - before.OffensiveProgressValue);
        gain += Math.Max(0, after.ReachableHandValue - before.ReachableHandValue);
        gain += Math.Max(0, after.ZeroCostPlayableCount - before.ZeroCostPlayableCount) * 4L;
        gain += Math.Max(0, after.ProjectedPlayerHp - before.ProjectedPlayerHp);
        return (int)Math.Min(int.MaxValue, gain);
    }
}
