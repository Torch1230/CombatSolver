using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertLongTermResourceBeamCap(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null)
            with { GrowthBudgets = default, IgnoreLongTermRewards = false };
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat), policy);
        SimulationSnapshot initial = InvokeForcedTerminalReplay(driver, [], null, 0, null);
        try
        {
            SimulationSnapshot Observe(int resource, bool loseHp)
            {
                CombatPredictionSimulator simulator = initial.Simulator.Fork();
                SimulatedCombatState state = (SimulatedCombatState)simulator.State.CombatState;
                state.RecordLongTermResource(resource);
                if (loseHp) simulator.Damage(player.Creature, 1, ValueProp.Unblockable | ValueProp.Unpowered, player.Creature);
                return (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot",
                    [simulator, initial.Turn, 0, initial.ShufflesCrossed, SearchBoundaryReason.None, initial.ProcessedEnemyDeaths])!;
            }
            foreach (int resource in new[] { 1, 25, 1000 })
            {
                SimulationSnapshot reward = Observe(resource, false);
                SimulationSnapshot paid = Observe(resource, true);
                try
                {
                    double bonus = reward.Score - initial.Score;
                    if (bonus <= 0 || bonus >= SolverWeights.Hp || paid.Score >= initial.Score)
                        throw new InvalidOperationException($"Long-term resource {resource}: bonus={bonus}, paid_delta={paid.Score-initial.Score}; reward must stay below one HP weight.");
                    if (reward.LongTermResourceValue != resource || reward.GrowthHpCredit != 0)
                        throw new InvalidOperationException("Beam cap changed the actual resource or fabricated growth credit.");
                }
                finally { reward.ReleaseSimulator(); paid.ReleaseSimulator(); }
            }
        }
        finally { initial.ReleaseSimulator(); }
    }
}
