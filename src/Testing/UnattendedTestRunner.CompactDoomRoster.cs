using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // A single-enemy root cannot separate AllEnemies from ChosenEnemy. This scenario drives
    // both native OnPlay shapes on a three-enemy roster and compares every compact
    // materialization with the real combat after the same native action.
    private async Task AssertCompactDoomRosterAsync(CombatState combat, Player player)
    {
        if (combat.Enemies.Count != 3)
            throw new InvalidOperationException("Compact doom roster fixture requires three primary enemies.");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        // The captured Osty keeps its persistent protection Power.
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers)
            .Where(power => power is not DieForYouPower).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        string[] ids = ["DEATHBRINGER", "NEGATIVE_PULSE", "SCOURGE", "PUTREFY", "FEAR"];
        foreach (string id in ids)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        for (int index = 0; index < 3; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        Creature[] enemies = combat.Enemies.ToArray();
        for (int index = 0; index < enemies.Length; index++)
        {
            await CreatureCmd.SetMaxHp(enemies[index], 300);
            await CreatureCmd.SetCurrentHp(enemies[index], 300);
            await SetBlockAsync(enemies[index], 0);
        }
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CardModel[] cards = ids.Select(id => player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == id)).ToArray();
        var captured = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        (int Card, int Target)[] route = [(0, -1), (1, -1), (2, 2), (3, 1), (4, 3)];
        List<MoveStateSnapshot> expected = [];
        List<object> evidence = [];
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate final;
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new CompactDiscardProjection(captured.ForkSimulator(), player, includeAttacks: true);
            var lane = adapter.Program;
            var initial = lane.Freeze();
            var mark = lane.State.Mark();
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            var reader = adapter.CreateReadView();
            for (int step = 0; step < route.Length; step++)
            {
                int before = lane.EventCount;
                lane.Begin(adapter.IndexOf(cards[route[step].Card]), route[step].Target);
                lane.Run();
                var changes = Enumerable.Range(before, lane.EventCount - before).Select(lane.EventAt)
                    .Where(item => item.Kind == ResumableDiscardProgram.EventKind.PowerChange)
                    .Select(item => (item.Target, (BasicPowerKind)item.Flags, item.Value)).ToArray();
                if (!changes.SequenceEqual(DoomRosterChanges(step)))
                    throw new InvalidOperationException($"Compact doom roster step {step} applied "
                        + $"[{string.Join(',', changes)}] instead of [{string.Join(',', DoomRosterChanges(step))}].");
                var projected = adapter.Materialize(lane);
                adapter.AssertValues(lane, projected);
                reader.Read(lane);
                SimulationSnapshot evaluation = evaluator.Evaluate(reader);
                AssertCompactEvaluation(Release(evaluator.Evaluate(projected)), evaluation, $"RosterStep{step}/Projection");
                expected.Add(CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemies[0]));
                evidence.Add(new { step, card = ids[route[step].Card], target = route[step].Target,
                    powerChanges = changes.Select(change => $"{change.Target}:{change.Item2}={change.Item3}").ToArray() });
            }
            final = lane.Freeze();
            lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                throw new InvalidOperationException("Compact doom roster rollback leaked applied Power or card state.");
        }
        for (int step = 0; step < route.Length; step++)
        {
            if (!cards[route[step].Card].TryManualPlay(route[step].Target < 0 ? null : enemies[route[step].Target - 1]))
                throw new InvalidOperationException($"Native compact doom roster step {step} was rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected[step], CaptureActual(combat, player, enemies[0]), "CompactDoomRoster", $"Native{step}");
            AssertDoomRosterNative(step, enemies, player);
        }
        using (SimulationNotificationIsolation.Enter())
        {
            var projected = adapter.Materialize(final.Open());
            AssertSnapshotEqual(CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemies[0]),
                CaptureActual(combat, player, enemies[0]), "CompactDoomRoster", "FrozenAfterNative");
        }
        _completedChecks.Add("CompactDoomRoster:ThreeEnemies:DeathbringerNegativePulseRosterPass:ScourgePutrefyFearChosenEnemy:"
            + "ExactPowerOrderAndScope:MaterializationAndReaderKeys:NativePerStep:Rollback:FrozenAfterNative");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-doom-roster.json"),
                JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = combat.Encounter?.Id.Entry,
                    seed = _request.Seed, enemies = enemies.Length, steps = evidence },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // One entry per native command of the five admitted OnPlay bodies: a bulk Power
    // command finishes its whole roster pass before the next command runs.
    private static (int Target, BasicPowerKind Kind, int Amount)[] DoomRosterChanges(int step) => step switch
    {
        0 => [(1, BasicPowerKind.Doom, 21), (2, BasicPowerKind.Doom, 21), (3, BasicPowerKind.Doom, 21),
            (1, BasicPowerKind.Weak, 1), (2, BasicPowerKind.Weak, 1), (3, BasicPowerKind.Weak, 1)],
        1 => [(1, BasicPowerKind.Doom, 7), (2, BasicPowerKind.Doom, 7), (3, BasicPowerKind.Doom, 7)],
        2 => [(2, BasicPowerKind.Doom, 13)],
        3 => [(1, BasicPowerKind.Weak, 2), (1, BasicPowerKind.Vulnerable, 2)],
        _ => [(3, BasicPowerKind.Vulnerable, 1)]
    };

    private static void AssertDoomRosterNative(int step, Creature[] enemies, Player player)
    {
        int Doom(int index) => enemies[index].GetPowerAmount<DoomPower>();
        int Weak(int index) => enemies[index].GetPowerAmount<WeakPower>();
        int Vulnerable(int index) => enemies[index].GetPowerAmount<VulnerablePower>();
        switch (step)
        {
            case 0:
                if (Doom(0) != 21 || Doom(1) != 21 || Doom(2) != 21 || Weak(0) != 1 || Weak(1) != 1 || Weak(2) != 1
                    || player.Creature.GetPowerAmount<DoomPower>() != 0 || player.Creature.GetPowerAmount<WeakPower>() != 0)
                    throw new InvalidOperationException("Native Deathbringer missed an enemy or reached the player.");
                break;
            case 1:
                if (Doom(0) != 28 || Doom(1) != 28 || Doom(2) != 28 || player.Creature.Block != 5)
                    throw new InvalidOperationException("Native NegativePulse changed its block or all-enemy Doom pass.");
                break;
            case 2:
                if (Doom(0) != 28 || Doom(1) != 41 || Doom(2) != 28
                    || player.PlayerCombatState!.Hand.Cards.Count(card => card.Id.Entry == "DEFEND_NECROBINDER") != 1)
                    throw new InvalidOperationException("Native Scourge reached the wrong enemy or lost its draw.");
                break;
            case 3:
                if (Weak(0) != 3 || Vulnerable(0) != 2 || Weak(1) != 1 || Vulnerable(1) != 0 || Weak(2) != 1 || Vulnerable(2) != 0)
                    throw new InvalidOperationException("Native Putrefy reached more than its chosen enemy.");
                break;
            default:
                if (Vulnerable(2) != 1 || Vulnerable(0) != 2 || Vulnerable(1) != 0
                    || enemies[2].CurrentHp != 293 || enemies[0].CurrentHp != 300 || enemies[1].CurrentHp != 300)
                    throw new InvalidOperationException("Native Fear attacked and debuffed a different enemy.");
                break;
        }
    }
}
