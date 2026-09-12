using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertHistoryCourseWindowAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single();
        await SetBlockAsync(enemy, 0);
        await CreatureCmd.SetMaxHp(enemy, 1000); await CreatureCmd.SetCurrentHp(enemy, 1000);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_SILENT", Pile = "Hand" });
        SetEnergy(player, 20);
        if (!player.PlayerCombatState!.Hand.Cards.Single().TryManualPlay(enemy)) throw new InvalidOperationException("HistoryCourse seed attack rejected.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "HISTORY_COURSE", AddWithoutObtainedEffects = true });
        var captured = CombatRootSnapshot.Capture(combat);
        // Resolve both future windows before live advances. A duplicate auto-play must
        // not become the next turn's eligible attack, and an empty window stays empty.
        MoveStateSnapshot[] Predict()
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var simulator = captured.ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            List<MoveStateSnapshot> values = [];
            for (int step = 0; step < 2; step++)
            {
                shadow.CommitHistoryCourseTurn(player);
                shadow.RoundNumber++; shadow.AdvancePlayerTurn(player); shadow.BeginSideTurn(player.Creature);
                if (shadow.TriggerScheduledAutoPlays(simulator, player, shadow.GetPlayerTurnNumber(player), new TurnStartChoiceCursor(null), new HashSet<uint>()))
                    throw new InvalidOperationException("HistoryCourse window unexpectedly suspended.");
                shadow.AssertForkable();
                values.Add(CaptureSimulated(simulator, shadow, player, enemy));
                simulator = simulator.Fork(); shadow = (SimulatedCombatState)simulator.State.CombatState;
            }
            return values.ToArray();
        }
        var expected = Predict();
        List<MoveStateSnapshot> actual = [];
        for (int step = 0; step < 2; step++)
        {
            combat.RoundNumber++; player.PlayerCombatState.IncrementTurnNumber();
            await Hook.AfterAutoPrePlayPhaseEntered(new HookPlayerChoiceContext(player, player.NetId, GameActionType.Combat), combat, player);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            actual.Add(CaptureActual(combat, player, enemy));
        }
        var frozenAfterNative = Predict();
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "history-course-window.json"),
                JsonSerializer.Serialize(new { expected, actual, frozenAfterNative }, new JsonSerializerOptions { WriteIndented = true }));
        }
        for (int step = 0; step < 2; step++)
        {
            AssertSnapshotEqual(expected[step], actual[step], "HistoryCourseWindow", $"Native{step}");
            AssertSnapshotEqual(expected[step], frozenAfterNative[step], "HistoryCourseWindow", $"FrozenAfterNative{step}");
        }
        if (enemy.CurrentHp != 988) throw new InvalidOperationException("Native HistoryCourse did not replay exactly one six-damage attack.");
        _completedChecks.Add("HistoryCourseWindow:CurrentRootAttackCaptured:TwoNativeAutoPrePlayWindows:DuplicateExcluded:EmptyDoesNotRefill:ForkAfterEach:FrozenAfterLiveAdvanced");
    }
}
