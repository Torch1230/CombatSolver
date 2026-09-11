using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactRandomCostsAsync(CombatState combat, Player player)
    {
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            var enemies = combat.Enemies.ToArray();
            if (enemies.Length != 3) throw new InvalidOperationException("Random-cost fixture requires three enemies.");
            foreach (var enemy in enemies)
            {
                await CreatureCmd.SetMaxHp(enemy, 200);
                await CreatureCmd.SetCurrentHp(enemy, 200);
            }
            string[] hand = mode == 0 ? ["FINESSE", "BACKFLIP", "BACKFLIP"]
                : ["BACKFLIP", "BACKFLIP", .. Enumerable.Repeat("DEFEND_SILENT", 8)];
            foreach (string id in hand) await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            string[] draw = mode == 0 ? ["OUTBREAK"] : ["STRIKE_SILENT", "OUTBREAK"];
            foreach (string id in draw) await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = id, Pile = "Draw", UpgradeLevels = id == "OUTBREAK" ? 1 : 0, EnchantmentId = "SLITHER" });
            CardModel[] cards = [.. player.PlayerCombatState!.Hand.Cards, .. player.PlayerCombatState.DrawPile.Cards];
            if (mode == 0)
            {
                cards[3].EnergyCost.SetThisCombat(3);
                cards[3].EnergyCost.SetThisCombat(1);
            }
            SetEnergy(player, 30); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CompactCardAction R(int card, int target = -1) => new(cards[card], Target: target);
            CompactCardAction[][] paths = mode == 0
                ? [[R(0)], [R(0), R(3)], [R(0), R(1)], [R(0), R(3), R(1)], [R(0), R(3), R(1), R(3)],
                    [R(0), R(3), R(1), R(3), R(2)], [R(0), R(3), R(1), R(3), R(2), R(3)], [R(1), R(3), R(2), R(3), R(0)]]
                : [[R(0)], [R(1)], [R(0), R(10, 1)], [R(0), R(10, 1), R(1)], [R(1), R(10, 2), R(0)]];
            CompactCardAction[] native = mode == 0 ? paths[6] : paths[3];
            var captured = CombatRootSnapshot.Capture(combat);
            var root = captured.ForkSimulator();
            await AssertCompactCardSequencesAsync(captured, root, combat, player, cards, $"CompactRandomCosts:Mode{mode}", paths);
            CompactDiscardProjection adapter;
            List<MoveStateSnapshot[]> expected = [];
            List<string[]> expectedCosts = [];
            List<int[]> expectedAmounts = [];
            ResumableDiscardProgram.Candidate completed;
            MoveStateSnapshot[] rootSnapshots;
            string[] rootCosts;
            using (SimulationNotificationIsolation.Enter())
            {
                adapter = new(root, player, includeAttacks: true);
                var lane = adapter.Program;
                rootSnapshots = enemies.Select(enemy => CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy)).ToArray();
                rootCosts = cards.Select(card => Costs(root.State.FindCard(card)!.Preview)).ToArray();
                foreach (var action in native)
                {
                    lane.Begin(adapter.IndexOf(action.RootCard!), action.Target); lane.Run();
                    if (!lane.Complete) throw new InvalidOperationException("Random-cost route suspended.");
                    var projection = adapter.Materialize(lane);
                    expected.Add(enemies.Select(enemy => CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy)).ToArray());
                    expectedCosts.Add(cards.Select(card => Costs(projection.State.FindCard(card)!.Preview)).ToArray());
                    expectedAmounts.Add(cards.Select(card => projection.State.FindCard(card)!.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local)).ToArray());
                    if (expected.Count == 1 && mode == 1 && (lane.CostModifierCount(adapter.IndexOf(cards[10])) != 1
                        || lane.CostModifierCount(adapter.IndexOf(cards[11])) != 0))
                        throw new InvalidOperationException("Full-hand gate randomized an undrawn card.");
                }
                completed = lane.Freeze();
            }
            for (int step = 0; step < native.Length; step++)
            {
                var action = native[step];
                if (!action.RootCard!.TryManualPlay(action.Target < 0 ? null : enemies[action.Target - 1]))
                    throw new InvalidOperationException("Native random-cost route rejected a card.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                for (int enemy = 0; enemy < enemies.Length; enemy++)
                    AssertSnapshotEqual(expected[step][enemy], CaptureActual(combat, player, enemies[enemy]), "CompactRandomCosts", $"Mode{mode}-Native{step}");
                if (!expectedCosts[step].SequenceEqual(cards.Select(Costs)))
                    throw new InvalidOperationException("Native full cost-modifier list or card metadata differs.");
            }
            using (SimulationNotificationIsolation.Enter())
            {
                for (int enemy = 0; enemy < enemies.Length; enemy++)
                    AssertSnapshotEqual(rootSnapshots[enemy], CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemies[enemy]),
                        "CompactRandomCosts", "RootAfterNativeDraws");
                if (!rootCosts.SequenceEqual(cards.Select(card => Costs(root.State.FindCard(card)!.Preview))))
                    throw new InvalidOperationException("Native random draws mutated captured root costs.");
            }
            var final = completed.Open();
            evidence.Add(new { mode, branches = paths.Length, nativeActions = native.Length, costsAfterActions = expectedAmounts,
                finalEnergy = final.Energy, shuffleRng = final.ShuffleRng, energyCostRng = final.EnergyCostRng,
                modifiers = cards.Select(Costs).ToArray() });
        }
        _completedChecks.Add("CompactRandomCosts:Native2Roots13Branches9Actions:RepeatedDrawShuffle:Payment:FullModifierLists:FullHandGate:TwoRngStreams:RootAfterNative:AllSnapshotProperties:AllRng:Frozen8Workers");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-random-costs.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        static string Costs(CardModel card) => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}:{card.Enchantment?.Id.Entry}:{card.Enchantment?.Amount}:"
            + $"{card.EnergyCost._base}:{card.EnergyCost.GetWithModifiers(CostModifiers.Local)}:"
            + string.Join(';', card.EnergyCost._localModifiers.Select(modifier => $"{modifier.Amount}/{modifier.Type}/{modifier.Expiration}/{modifier.IsReduceOnly}"));
    }
}
