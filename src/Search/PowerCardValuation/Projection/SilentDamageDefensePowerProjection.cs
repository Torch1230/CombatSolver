using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int SerpentFormProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int damagePerCard = PowerAmountGain<SerpentFormPower>(parent, child);
        if (damagePerCard == 0)
            return 0;
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        int incomingDamage = CurrentIncomingDamage(child);
        return Math.Min(
            child.Snapshot.EnemyHp,
            MarginalFrontierValue(
                PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, options),
                PowerTurnFrontier.Build(
                    child.Snapshot.Energy,
                    incomingDamage,
                    options,
                    damagePerCard: damagePerCard)));
    }

    private int TrackingProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int damagePercent = PowerAmountGain<TrackingPower>(parent, child);
        if (damagePercent == 0)
            return 0;
        PowerTurnCardOption[] baseline = BuildCurrentHandOptions(child);
        PowerTurnCardOption[] powered = BuildCurrentHandOptions(
            child,
            weakAttackBonusPercent: damagePercent);
        int incomingDamage = CurrentIncomingDamage(child);
        return Math.Min(
            child.Snapshot.EnemyHp,
            MarginalFrontierValue(
                PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, baseline),
                PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, powered)));
    }

    private int WraithFormProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int prevented = Math.Max(
            0,
            child.Snapshot.ProjectedPlayerHp - parent.Snapshot.ProjectedPlayerHp);
        return (int)Math.Min(int.MaxValue, (long)prevented * 8);
    }
}
