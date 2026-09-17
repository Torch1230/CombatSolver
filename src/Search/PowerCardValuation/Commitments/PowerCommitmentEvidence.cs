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
