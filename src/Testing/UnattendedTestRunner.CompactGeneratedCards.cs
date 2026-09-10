using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactGeneratedCardsAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            var enemies = combat.Enemies.ToArray();
            if (enemies.Length != 3) throw new InvalidOperationException("Generated-card fixture requires three enemies.");
            for (int enemy = 0; enemy < enemies.Length; enemy++)
            {
                await CreatureCmd.SetMaxHp(enemies[enemy], 100);
                await CreatureCmd.SetCurrentHp(enemies[enemy], mode == 1 && enemy == 2 ? 4 : 100);
            }
            string[] hand = mode == 0 ? ["CLOAK_AND_DAGGER", "CLOAK_AND_DAGGER", "BACKFLIP", "STRIKE_SILENT", "DEFEND_SILENT"]
                : ["CLOAK_AND_DAGGER", "BACKFLIP", "STRIKE_SILENT", .. Enumerable.Repeat("DEFEND_SILENT", 7)];
            for (int card = 0; card < hand.Length; card++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection
                    { CardId = hand[card], Pile = "Hand", UpgradeLevels = hand[card] == "CLOAK_AND_DAGGER" && (mode == 1 || card == 1) ? 1 : 0 });
            if (mode == 0)
            {
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Discard" });
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", UpgradeLevels = 1, Pile = "Discard" });
            }
            await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            await PowerCmd.Apply<VulnerablePower>(new BlockingPlayerChoiceContext(), enemies[0], 2, player.Creature, null);
            SetEnergy(player, 20); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            int generatedBefore = CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count();
            CompactCardAction R(int index, int target = -1) => new(cards[index], Target: target);
            static CompactCardAction G(int ordinal, int target = 1) => new(null, ordinal, target);
            CompactCardAction[][] paths = mode == 0
                ? [[R(0)], [R(1)], [R(0), G(0)], [R(1), G(1)], [R(0), R(1), G(2)],
                    [R(1), R(0), G(0), G(2)], [R(1), G(1), R(0), R(2)], [R(1), G(0), R(0), G(2, 2), R(2)]]
                : [[R(0)], [R(0), G(0)], [R(0), G(0), R(1), G(1, 3)], [R(0), R(2, 2), G(0), R(1), G(1, 3)]];
            CompactCardAction[] native = mode == 0 ? paths[^1] : paths[2];
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            await AssertCompactCardSequencesAsync(captured, root, combat, player, cards, $"CompactGeneratedCards:Mode{mode}", paths);
            CompactDiscardProjection adapter;
            List<MoveStateSnapshot[]> expected = [];
            List<StateFingerprint[]> expectedGenerated = [];
            ResumableDiscardProgram.Candidate final;
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true);
                var lane = adapter.Program;
                foreach (var action in native)
                {
                    int identity = action.RootCard != null ? adapter.IndexOf(action.RootCard) : adapter.CardCount + action.GeneratedOrdinal;
                    lane.Begin(identity, action.Target); lane.Run();
                    if (!lane.Complete) throw new InvalidOperationException("Generated-card route suspended.");
                    var projection = adapter.Materialize(lane);
                    var shadow = (SimulatedCombatState)projection.State.CombatState;
                    expected.Add(enemies.Select(enemy => CaptureSimulated(projection, shadow, player, enemy)).ToArray());
                    var identities = adapter.CaptureCardIdentities(projection);
                    expectedGenerated.Add(identities.Where(pair => pair.Value >= adapter.CardCount).OrderBy(pair => pair.Value)
                        .Select(pair => CombatBeamSolver.CaptureCardStateFingerprintForTesting(projection.State.FindCard(pair.Key)!)).ToArray());
                }
                final = lane.Freeze();
                foreach (var enemy in enemies)
                    AssertSnapshotEqual(CaptureActual(combat, player, enemy), CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy),
                        "CompactGeneratedCards", "RootUnchanged");
            }
            for (int actionIndex = 0; actionIndex < native.Length; actionIndex++)
            {
                var action = native[actionIndex];
                CardModel original = action.RootCard ?? Generated()[action.GeneratedOrdinal];
                if (!original.TryManualPlay(action.Target < 0 ? null : enemies[action.Target - 1]))
                    throw new InvalidOperationException("Native generated-card route rejected a card.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                for (int enemy = 0; enemy < enemies.Length; enemy++)
                    AssertSnapshotEqual(expected[actionIndex][enemy], CaptureActual(combat, player, enemies[enemy]),
                        "CompactGeneratedCards", $"Mode{mode}-Native{actionIndex}");
                var actual = Generated().Select(card => CombatBeamSolver.CaptureCardStateFingerprintForTesting(PredictedCard.FromGenerated(card))).ToArray();
                if (!expectedGenerated[actionIndex].SequenceEqual(actual))
                    throw new InvalidOperationException("Native generated instance order or metadata differs.");
            }
            var completed = final.Open();
            if (mode == 1 && (completed.Count(ResumableDiscardProgram.Pile.Exhaust) != 2 || completed.CreaturePresent(3)))
                throw new InvalidOperationException("Full-hand generation missed shuffle, exhaustion or target death.");
            evidence.Add(new { mode, branches = paths.Length, nativeActions = native.Length, generated = Generated().Length,
                shuffles = completed.ShuffleCount, exhausted = completed.Count(ResumableDiscardProgram.Pile.Exhaust),
                finalCardCount = completed.CardCount, block = completed.Block });
            CardModel[] Generated() => CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Skip(generatedBefore).Select(entry => entry.Card).ToArray();
        }
        _completedChecks.Add("CompactGeneratedCards:Native2Roots12Branches9Actions:CreationOrder:FullHandSpill:ShivAttackExhaustion:Shuffle:Death:ShivHistory:AllSnapshotProperties:AllRng:Frozen8Workers");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-generated-cards.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
