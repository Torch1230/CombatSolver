using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertHangCapAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        foreach (int amount in new[] { 999_999_999, 999_999_998 })
        foreach (int artifact in new[] { 0, 1 })
        {
            await PrepareCompactOstyAsync(combat, player, 1);
            await ClearPlayerPilesAsync(player);
            foreach (var power in player.Creature.Powers.OfType<StrengthPower>().ToArray()) await PowerCmd.Remove(power);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "HANG", Pile = "Hand" });
            var card = player.PlayerCombatState!.Hand.Cards.Single();
            card.DynamicVars.Damage.BaseValue = 0;
            var enemy = combat.Enemies.Single();
            await PowerCmd.Apply<HangPower>(new BlockingPlayerChoiceContext(), enemy, amount, player.Creature, null);
            if (artifact != 0)
                await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemy, 1, enemy, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var captured = CombatRootSnapshot.Capture(combat);
            var simulator = captured.ForkSimulator();
            MoveStateSnapshot expected, values;
            string[] expectedPowers;
            using (SimulationNotificationIsolation.Enter())
            {
                if (!simulator.ManualPlay(simulator.State.FindCard(card)!, enemy, out _))
                    throw new InvalidOperationException("Hang cap oracle unexpectedly suspended.");
                expected = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy);
                var compact = new CompactCombatRoot(captured.ForkSimulator(), player);
                var adapter = compact.Adapter; var lane = adapter.Program;
                var before = lane.State.Mark();
                lane.Begin(adapter.IndexOf(card), 1); lane.Run();
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, simulator);
                values = CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy);
                AssertSnapshotEqual(expected, values, "HangCap", "Compact");
                expectedPowers = CompactPowerValues(((SimulatedCombatState)simulator.State.CombatState).EffectivePowers()).ToArray();
                if (!expectedPowers.SequenceEqual(CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers()))
                    || !CompactHistory(simulator, adapter).SequenceEqual(CompactHistory(projected, adapter)))
                    throw new InvalidOperationException("Hang cap Power metadata or represented history differs.");
                AssertCompactRngSet(simulator.Rng, projected.Rng);
                var display = SolverDisplayNames.Capture(combat);
                var damage = BattleDamageTracker.Observe(combat);
                var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
                var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
                var reader = adapter.CreateReadView(); reader.Read(lane);
                AssertCompactEvaluation(Release(evaluator.Evaluate(simulator)), evaluator.Evaluate(reader));
                lane.State.Rollback(before);
                if (!lane.State.Freeze().ContentEquals(compact.Initial.Open().State.Freeze()))
                    throw new InvalidOperationException("Hang cap rollback leaked Artifact or Power state.");
            }
            if (!card.TryManualPlay(enemy)) throw new InvalidOperationException("Native Hang cap play was rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var actual = CaptureActual(combat, player, enemy);
            AssertSnapshotEqual(expected, actual, "HangCap", $"Native{amount}");
            var actualPowers = CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers)).ToArray();
            if (!expectedPowers.SequenceEqual(actualPowers))
                throw new InvalidOperationException("Native Hang cap Power metadata differs.");
            evidence.Add(new { amount, artifact, expected, values, actual, expectedPowers, actualPowers });
            if (enemy.GetPowerAmount<HangPower>() != (artifact == 0 ? 999_999_999 : amount)
                || enemy.GetPowerAmount<ArtifactPower>() != 0)
                throw new InvalidOperationException("Hang cap changed the native growth limit or failed to process the existing zero request.");
            _completedChecks.Add($"HangCap:Root{amount}:Artifact{artifact}:ZeroDamage:NativeFullState:ExistingZeroRequestConsumesArtifact:Rollback");
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-hang-cap.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task PrepareCompactHangAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "HANG", Pile = "Hand", UpgradeLevels = 1 });
        foreach (string id in new[] { "HANG", "HANG", "STRIKE_NECROBINDER", "UNLEASH", "SOUL" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 10_000); await CreatureCmd.SetCurrentHp(enemy, 10_000);
        await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, enemy, null);
        await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), enemy, 2, player.Creature, null);
        if (mode == 1)
        {
            await PowerCmd.Apply<HangPower>(new BlockingPlayerChoiceContext(), enemy, 3, player.Creature, null);
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemy, 1, enemy, null);
        }
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers)) power.AmountOnTurnStart = 37;
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactHangAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 2; mode++)
        {
            await PrepareCompactHangAsync(combat, player, mode);
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactHang", "compact-hang",
                [("HANG", 1), ("STRIKE_NECROBINDER", 0), ("HANG", 0), ("UNLEASH", 0), ("HANG", 0), ("SOUL", 0)],
                (lane, before, step) =>
                {
                    if (step >= 5) return;
                    int hang = Enumerable.Range(0, before.PowerCount).Where(index => before.PowerDefinition(index).Kind == BasicPowerKind.Hang)
                        .Select(index => before.Power(index).Amount).Single();
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    var attack = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Damage);
                    int expectedDamage = step == 3 ? (int)((6 + before.Creature(before.PetIndex).CurrentHp + 2) * 1.5m)
                        : (int)(((step == 0 ? 13 : step == 1 ? 6 : 10) + 11) * 0.75m * 1.5m
                            * (step == 1 || hang == 0 ? 1 : hang));
                    // Damage events store HP loss; block is recorded separately.
                    if (attack.Value + events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.DamageBlocked).Sum(item => item.Value) != expectedDamage
                        || attack.Dealer != (step == 3 ? before.PetIndex : 0))
                        throw new InvalidOperationException("Hang multiplier changed the additive order or affected a different attack source.");
                    int after = Enumerable.Range(0, lane.PowerCount).Where(index => lane.PowerDefinition(index).Kind == BasicPowerKind.Hang)
                        .Select(index => lane.Power(index).Amount).Single();
                    int expectedHang = step is 1 or 3 || mode == 1 && step == 0 ? hang : hang + Math.Max(2, hang);
                    if (after != expectedHang) throw new InvalidOperationException("Hang growth or Artifact order differs.");
                });
            _completedChecks.Add($"CompactHang:Mode{mode}:CardSpecificMultiplier:StrengthThenWeakVulnerable:AttackBeforeGrowth:Artifact:TwoRounds");
        }
    }

    private async Task AssertCompactHangTerminalAsync(CombatState combat, Player player)
    {
        await PrepareCompactHangAsync(combat, player, 1);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await AssertCompactPetCardRouteAsync(combat, player, 2, "CompactHangTerminal", "compact-hang-terminal",
            [("HANG", 1)], (lane, _, _) =>
            {
                if (!lane.Terminal || Enumerable.Range(0, lane.PowerCount)
                    .Any(index => lane.PowerDefinition(index).Owner == 1 && lane.Power(index).Amount != 0))
                    throw new InvalidOperationException("Hang must clear target Powers on death and reject later growth.");
            }, rounds: 0, requirePending: false);
    }
}
