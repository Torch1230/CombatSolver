using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int AccelerantProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int extraTriggers = PowerAmountGain<AccelerantPower>(parent, child);
        if (extraTriggers == 0)
            return 0;
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        long damage = 0;
        foreach (var enemy in combat.KnownEnemies)
        {
            var enemyState = simulator.State.GetCreature(enemy);
            if (!combat.ContainsCreature(enemy) || !enemyState.IsAlive)
                continue;
            int poison = Math.Max(0, combat.GetAmount<PoisonPower>(enemy));
            damage += PoisonStackProjection.ExtraTriggerDamage(
                poison,
                extraTriggers,
                Math.Max(0, enemyState.CurrentHp));
        }
        return (int)Math.Min(int.MaxValue, damage);
    }

    private int EnvenomProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int poisonPerHit = PowerAmountGain<EnvenomPower>(parent, child);
        if (poisonPerHit == 0)
            return 0;
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        int incomingDamage = CurrentIncomingDamage(child);
        return MarginalFrontierValue(
            PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, options),
            PowerTurnFrontier.Build(
                child.Snapshot.Energy,
                incomingDamage,
                options,
                damagePerUnblockedAttackHit: poisonPerHit));
    }

    private int NoxiousFumesProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int poisonPerTurn = PowerAmountGain<NoxiousFumesPower>(parent, child);
        if (poisonPerTurn == 0)
            return 0;
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int drawPerTurn = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int futureTurns = Math.Max(
            0,
            EstimateRemainingTurns(child.Snapshot, Math.Max(1, drawPerTurn)) - 1);
        long total = 0;
        foreach (var enemy in combat.KnownEnemies)
        {
            var enemyState = simulator.State.GetCreature(enemy);
            if (!combat.ContainsCreature(enemy) || !enemyState.IsAlive)
                continue;
            total += PoisonStackProjection.RecurringApplicationDamage(
                poisonPerTurn,
                futureTurns,
                Math.Max(0, enemyState.CurrentHp));
        }
        return (int)Math.Min(int.MaxValue, total);
    }
}
