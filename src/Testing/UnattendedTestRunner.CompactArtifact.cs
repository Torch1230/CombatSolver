using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactArtifactAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            var enemies = combat.Enemies.ToArray();
            if (enemies.Length != 3) throw new InvalidOperationException("Artifact fixture requires three enemies.");
            for (int enemy = 0; enemy < enemies.Length; enemy++)
            {
                await CreatureCmd.SetMaxHp(enemies[enemy], 200);
                await CreatureCmd.SetCurrentHp(enemies[enemy], mode == 1 && enemy == 2 ? 1 : 200);
            }
            string[] hand = mode == 0 ? ["PIERCING_WAIL", "PIERCING_WAIL", "NEUTRALIZE", "DEADLY_POISON", "MALAISE", "MALAISE", "FOOTWORK"]
                : ["MALAISE", "NEUTRALIZE"];
            for (int card = 0; card < hand.Length; card++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection
                    { CardId = hand[card], Pile = "Hand", UpgradeLevels = mode == 0 && card is 1 or 4 ? 1 : 0 });
            await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), enemies[0], 6, enemies[0], null);
            await PowerCmd.Apply<PiercingWailPower>(new BlockingPlayerChoiceContext(), enemies[1], 2, enemies[1], null);
            enemies[1].GetPower<PiercingWailPower>()!.AmountOnTurnStart = 7;
            int[] artifacts = [2, 1, 3];
            for (int enemy = 0; enemy < enemies.Length; enemy++)
            {
                await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemies[enemy], artifacts[enemy], enemies[enemy], null);
                enemies[enemy].GetPower<ArtifactPower>()!.AmountOnTurnStart = 9;
            }
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            SetEnergy(player, mode == 0 ? 30 : 0); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            CompactCardAction R(int card, int target = -1) => new(cards[card], Target: target);
            CompactCardAction[][] paths = mode == 0
                ? [[R(0)], [R(1)], [R(2, 1)], [R(3, 2)], [R(4, 3)], [R(5, 3)], [R(6)], [R(0), R(1)],
                    [R(0), R(1), R(3, 2)], [R(0), R(1), R(4, 3)], [R(6), R(0), R(1), R(2, 1), R(3, 2), R(4, 3), R(5, 1)]]
                : [[R(0, 1)], [R(1, 3)], [R(0, 1), R(1, 3)], [R(1, 3), R(0, 2)]];
            CompactCardAction[] native = mode == 0 ? paths[^1] : paths[2];
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            await AssertCompactCardSequencesAsync(captured, root, combat, player, cards, $"CompactArtifact:Mode{mode}", paths);
            List<MoveStateSnapshot[]> expected = [];
            ResumableDiscardProgram.Candidate completed;
            using (SimulationNotificationIsolation.Enter())
            {
                var adapter = new CompactDiscardProjection(root, player, includeAttacks: true);
                var lane = adapter.Program;
                foreach (var action in native)
                {
                    lane.Begin(adapter.IndexOf(action.RootCard!), action.Target); lane.Run();
                    if (!lane.Complete) throw new InvalidOperationException("Artifact route suspended.");
                    var projection = adapter.Materialize(lane);
                    expected.Add(enemies.Select(enemy => CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy)).ToArray());
                }
                completed = lane.Freeze();
                foreach (var enemy in enemies)
                    AssertSnapshotEqual(CaptureActual(combat, player, enemy), CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                        "CompactArtifact", "RootUnchanged");
            }
            for (int step = 0; step < native.Length; step++)
            {
                var action = native[step];
                if (!action.RootCard!.TryManualPlay(action.Target < 0 ? null : enemies[action.Target - 1]))
                    throw new InvalidOperationException("Native Artifact route rejected a card.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                for (int enemy = 0; enemy < enemies.Length; enemy++)
                    AssertSnapshotEqual(expected[step][enemy], CaptureActual(combat, player, enemies[enemy]), "CompactArtifact", $"Mode{mode}-Native{step}");
            }
            var final = completed.Open();
            int Amount(int owner, BasicPowerKind kind) => Enumerable.Range(0, final.PowerCount).Where(index => final.PowerDefinition(index).Owner == owner
                && final.PowerDefinition(index).Kind == kind).Select(index => final.Power(index).Amount).Single();
            if (Amount(0, BasicPowerKind.Artifact) != 1 || mode == 0 && (Amount(1, BasicPowerKind.Strength) != 6
                || Amount(1, BasicPowerKind.PiercingWail) != 0 || Amount(2, BasicPowerKind.Strength) != -10
                || Amount(2, BasicPowerKind.PiercingWail) != 10 || Amount(3, BasicPowerKind.Artifact) != 0)
                || mode == 1 && (Amount(1, BasicPowerKind.Artifact) != 2 || final.CreaturePresent(3) || Amount(3, BasicPowerKind.Artifact) != 0))
                throw new InvalidOperationException("Artifact fixture missed a modifier, zero-amount or death boundary.");
            evidence.Add(new { mode, branches = paths.Length, nativeActions = native.Length, finalEnergy = final.Energy,
                powers = Enumerable.Range(0, final.PowerCount).Select(index => new { definition = final.PowerDefinition(index), value = final.Power(index) }).ToArray() });
        }
        _completedChecks.Add("CompactArtifact:Native2Roots15Branches9Actions:FirstAndStackedTemporary:SignedStat:ZeroAmount:BuffUnaffected:Depletion:RootMetadata:Death:AllSnapshotProperties:AllRng:Frozen8Workers");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-artifact.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
