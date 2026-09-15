using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLowLossPotionCacheAsync(CombatState combat, Player player)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Low-loss potion cache: " + message);
        }
        NGame host = NGame.Instance!;
        var settingsBefore = SolverSettings.Current;
        bool enabledBefore = SolverController.LowLossPotionEnabled;
        try
        {
            SolverSettings.ApplyForTesting(settingsBefore with
            {
                PotionPolicy = SolverPotionPolicy.Smart, PotionDirectives = [],
                GrowthBudgets = default, RelicStrategyEnabled = false,
                AutomaticCalculationEnabled = true, AutoEnableFullAuto = false,
                EnableNoGcRegion = false, EnableDetailedDiagnosticLogs = false,
                SearchMaxDegreeOfParallelism = 1, SearchMaxExpandedNodes = 512,
                SearchTimeLimitSeconds = 3, AcceptableBattleHpLoss = 1,
                StopAtAcceptableBattleHpLoss = true,
            });
            SolverController.SetLowLossPotionForTesting(false);
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            foreach (var potion in player.PotionSlots.Where(p => p != null).ToArray()) potion!.Discard();
            await ClearPlayerPilesAsync(player);
            await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
            ConfigureMonsterMove(combat.Enemies[0], new UnattendedMonsterMoveCheck { MoveId = "FIRST_ACID_GOOP" });
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
                { PowerId = "STRENGTH_POWER", Target = "Enemy", Amount = 50 });
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = "BLOODLETTING", Pile = "Hand",
                DynamicVars = new() { ["HpLoss"] = 1, ["Energy"] = 3 },
            });
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLUDGEON", Pile = "Hand" });
            SetEnergy(player, 0);
            InjectPotionForTest(player, "ENERGY_POTION").DynamicVars["Energy"].BaseValue = 3;
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

            var root = CombatRootSnapshot.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            foreach (bool enabled in new[] { false, true })
            {
                string path = SolvedRouteCache.Capture(combat, root,
                    policy with { LowLossPotionEnabled = enabled }, damage).Path;
                if (File.Exists(path)) File.Delete(path);
            }

            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            SolverResult off = await AwaitResultAsync();
            Check(!off.WasRestoredFromCache && off.ProjectedBattleHpLost == 1
                && off.ExplicitPotionCount == 0, "record one-HP potion-free baseline");
            SolverController.SetLowLossPotionEnabled(host, combat, true);
            SolverResult on = await AwaitResultAsync(off);
            Check(!on.WasRestoredFromCache && on.ProjectedBattleHpLost == 0
                && on.ExplicitPotionCount == 1 && on.LowLossPotionApplied
                && on.PotionHpRequired == 1, "cache miss searches and records zero-HP relaxed route");
            _completedChecks.Add("LowLossPotionCache:MissRecordsBothPolicies:1to0");

            // Rebuild the combat session exactly as loading does, then exercise the
            // production checkbox setter instead of only comparing cache key strings.
            SolverController.Reset("low_loss_potion_cache_reload_test");
            await SolverController.LastCombatReferenceReleaseForTesting;
            SolverController.BeginCombat(combat);
            Check(!SolverController.LowLossPotionEnabled, "new session defaults off");
            SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
            SolverResult restoredOff = await AwaitResultAsync();
            AssertRestored(restoredOff, off, "reload baseline");
            SolverController.SetLowLossPotionEnabled(host, combat, true);
            SolverResult restoredOn = await AwaitResultAsync(restoredOff);
            AssertRestored(restoredOn, on, "enable after reload");
            SolverController.SetLowLossPotionEnabled(host, combat, false);
            restoredOff = await AwaitResultAsync(restoredOn);
            AssertRestored(restoredOff, off, "disable restores baseline");
            SolverController.SetLowLossPotionEnabled(host, combat, true);
            restoredOn = await AwaitResultAsync(restoredOff);
            AssertRestored(restoredOn, on, "re-enable restores relaxed route");
            Check(SolverController.ReplanAuditForBugReport.StartsWith("searches=0 ", StringComparison.Ordinal)
                && SolverController.ReplanAuditForBugReport.Contains("restored=4", StringComparison.Ordinal),
                "all four restored results avoid new search work");
            _completedChecks.Add("LowLossPotionCache:SessionReset:ToggleRestoresBoth:PlanAndCostRoundTrip:Searches0:Restored4");

            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            SolverResult manual = await AwaitResultAsync(restoredOn);
            Check(!manual.WasRestoredFromCache && manual.LowLossPotionApplied
                && manual.ProjectedBattleHpLost == 0, "explicit recalculation still bypasses cache");
            SolverSettings.ApplyForTesting(SolverSettings.Current with { AutomaticCalculationEnabled = false });
            string audit = SolverController.ReplanAuditForBugReport;
            SolverController.SetLowLossPotionEnabled(host, combat, false);
            for (int i = 0; i < 3; i++) await NextFrameAsync();
            Check(!SolverController.IsSearching && !SolverController.LowLossPotionEnabled
                && SolverController.ReplanAuditForBugReport == audit,
                "automatic calculation disabled defers work");
            _completedChecks.Add("LowLossPotionCache:ExplicitRecalculateBypassesCache:AutomaticCalculationDisabled");
        }
        finally
        {
            SolverSettings.ApplyForTesting(settingsBefore);
            SolverController.SetLowLossPotionForTesting(enabledBefore);
        }

        void AssertRestored(SolverResult restored, SolverResult original, string context)
        {
            Check(restored.WasRestoredFromCache, context + " must restore instead of searching");
            Check(SerializePlan(restored) == SerializePlan(original), context + " preserves route and effective potion cost");
            Check(!ReferenceEquals(restored.Forecast, original.Forecast), context + " binds the fresh root forecast");
        }

        async Task<SolverResult> AwaitResultAsync(SolverResult? previous = null)
        {
            long deadline = Environment.TickCount64 + 15_000;
            while (SolverController.LastCompletedResultForTesting == null || SolverController.IsSearching
                || ReferenceEquals(SolverController.LastCompletedResultForTesting, previous))
            {
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("Low-loss potion cache search failed: " + failure);
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("Low-loss potion cache result exceeded 15 seconds.");
                await NextFrameAsync();
            }
            return SolverController.LastCompletedResultForTesting;
        }
    }
}
