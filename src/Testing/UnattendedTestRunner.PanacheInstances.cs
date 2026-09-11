using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPanacheInstancesAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single();
        await SetBlockAsync(enemy, 0);
        await CreatureCmd.SetMaxHp(enemy, 1000); await CreatureCmd.SetCurrentHp(enemy, 1000);
        for (int step = 0; step < 8; step++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = step < 2 ? "PANACHE" : "DEFEND_SILENT", Pile = "Hand", UpgradeLevels = step == 1 ? 1 : 0 });
        SetEnergy(player, 20);
        var cards = player.PlayerCombatState!.Hand.Cards.ToArray();
        var captured = CombatRootSnapshot.Capture(combat);
        (MoveStateSnapshot[] Snapshots, string[][] Powers) Predict()
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var simulator = captured.ForkSimulator();
            List<MoveStateSnapshot> snapshots = [];
            List<string[]> powers = [];
            foreach (var card in cards)
            {
                if (!simulator.ManualPlay(simulator.State.FindCard(card)!, null, out _))
                    throw new InvalidOperationException("Panache instance oracle unexpectedly suspended.");
                var shadow = (SimulatedCombatState)simulator.State.CombatState;
                snapshots.Add(CaptureSimulated(simulator, shadow, player, enemy));
                powers.Add(PanacheValues(shadow.EffectivePowers().OfType<PanachePower>(), simulator));
                simulator = simulator.Fork();
            }
            return (snapshots.ToArray(), powers.ToArray());
        }
        var expected = Predict();
        List<MoveStateSnapshot> actual = [];
        List<string[]> actualPowers = [];
        foreach (var card in cards)
        {
            if (!card.TryManualPlay(null)) throw new InvalidOperationException("Native Panache instance card was rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            actual.Add(CaptureActual(combat, player, enemy));
            actualPowers.Add(PanacheValues(player.Creature.Powers.OfType<PanachePower>()));
        }
        var frozenAfterNative = Predict();
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "panache-instances.json"), JsonSerializer.Serialize(
                new { expected = expected.Snapshots, expectedPowers = expected.Powers, actual, actualPowers,
                    frozenAfterNative = frozenAfterNative.Snapshots, frozenPowers = frozenAfterNative.Powers },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        for (int step = 0; step < cards.Length; step++)
        {
            AssertSnapshotEqual(expected.Snapshots[step], actual[step], "PanacheInstances", $"Native{step}");
            AssertSnapshotEqual(expected.Snapshots[step], frozenAfterNative.Snapshots[step], "PanacheInstances", $"FrozenAfterNative{step}");
            if (!expected.Powers[step].SequenceEqual(actualPowers[step]) || !expected.Powers[step].SequenceEqual(frozenAfterNative.Powers[step]))
                throw new InvalidOperationException($"Panache instance lifecycle differs at step {step}.");
        }
        if (enemy.CurrentHp != 976 || player.Creature.Powers.OfType<PanachePower>().Count() != 2)
            throw new InvalidOperationException("Panache must retain two independent instances and trigger each once.");
        _completedChecks.Add("PanacheInstances:TwoIndependentAmountsAndCounters:NativeEightCards:ForkAfterEach:FrozenAfterLiveAdvanced");
    }

    private static string[] PanacheValues(IEnumerable<PanachePower> powers, CombatPredictionSimulator? simulator = null)
        => powers.Select(power =>
        {
            bool applied = simulator == null ? power.GetInternalData<PanachePower.Data>().alreadyApplied
                : simulator.StateStore.Peek(power, static value => new PanachePredictionState(value)).AlreadyApplied;
            return $"{power.Amount}:{power.DynamicVars["CardsLeft"].IntValue}:{applied}:{power.AmountOnTurnStart}:{power.Applier?.CombatId}:{power.Target?.CombatId}";
        }).ToArray();
}
