using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLoopDefensiveValueAsync(CombatState combat, Player player)
    {
        await ClearPlayerPilesAsync(player);
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        var settings = SolverSettings.Capture();
        var policy = SolverController.CaptureSearchPolicy(settings, combat, false, null);
        SimulationSnapshot Capture()
        {
            var root = CombatRootSnapshot.Capture(combat);
            var solver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat), policy);
            return solver.ReplayDiagnosticPrefix([]);
        }
        await SetBlockAsync(player.Creature, 100);
        var first = Capture();
        await SetBlockAsync(player.Creature, 200);
        var excess = Capture();
        try
        {
            if (first.ProjectedPlayerHp != player.Creature.CurrentHp
                || first.DefensiveBlockValue >= 100
                || excess.DefensiveBlockValue != first.DefensiveBlockValue)
                throw new InvalidOperationException("Excess expiring block still earns defensive progress.");
            await InjectPowerAsync(combat, player,
                new UnattendedPowerInjection { PowerId = "BARRICADE_POWER", Amount = 1, Target = "Player" });
            var retained = Capture();
            try
            {
                if (Hook.ShouldClearBlock(combat, player.Creature, out _)
                    || retained.DefensiveBlockValue <= excess.DefensiveBlockValue)
                    throw new InvalidOperationException("Native retained block lost its defensive reserve.");
            }
            finally { retained.ReleaseSimulator(); }
            foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
            await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "STURDY_CLAMP" });
            var capped = Capture();
            try
            {
                int cap = player.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.SturdyClamp>().Single().DynamicVars.Block.IntValue;
                if (capped.DefensiveBlockValue != first.DefensiveBlockValue + cap)
                    throw new InvalidOperationException("Capped retention valued block which will be cleared.");
            }
            finally { capped.ReleaseSimulator(); }
            if (!CombatHistoryCounterKey.AppliesTo(new HashSet<string> { "BANSHEES_CRY" })
                || CombatHistoryCounterKey.AppliesTo(new HashSet<string> { "STRIKE_IRONCLAD" }))
                throw new InvalidOperationException("History reader gating failed.");
            _completedChecks.Add("LoopDefense:100To200Saturated:NativeBarricadeRetained:CappedRetention:HistoryReaderGate");
        }
        finally { first.ReleaseSimulator(); excess.ReleaseSimulator(); }
    }
}
