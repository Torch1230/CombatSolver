using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTemporaryStrengthAsync(CombatState combat, Player player, bool capped)
    {
        List<object> evidence = [];
        foreach (int mode in capped ? new[] { 2, 3 } : new[] { 0, 1 })
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            ClearRunDeck((RunState)combat.RunState, player);
            await ClearPlayerPilesAsync(player);
            Creature[] enemies = combat.Enemies.ToArray();
            if (enemies.Length != 3) throw new InvalidOperationException("Temporary strength fixture requires three enemies.");
            if (mode == 0)
            {
                foreach (int upgrade in new[] { 0, 1 })
                    await InjectCardAsync(combat, player, new UnattendedCardInjection
                        { CardId = "PIERCING_WAIL", UpgradeLevels = upgrade, Pile = "Hand" });
                await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), enemies[0], 6, enemies[0], null);
                enemies[0].GetPower<StrengthPower>()!.AmountOnTurnStart = 9;
                await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), enemies[2], 1, enemies[2], null);
            }
            else if (mode == 2)
            {
                await PowerCmd.Apply<FlexPotionPower>(new BlockingPlayerChoiceContext(), player.Creature, 999_999_999, player.Creature, null);
                await PowerCmd.Remove(player.Creature.GetPower<StrengthPower>()!);
            }
            else if (mode == 3)
                await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, -999_999_999, player.Creature, null);
            SetEnergy(player, 5); SetStars(player, 0);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CardModel[] cards = player.PlayerCombatState!.Hand.Cards.ToArray();
            Creature[] participants = mode == 0 ? enemies : [player.Creature];
            var root = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var prediction = root.Fork();
            var shadow = (SimulatedCombatState)prediction.State.CombatState;
            List<MoveStateSnapshot[]> expected = [];
            List<string[]> expectedPowers = [];
            int actions = mode == 0 ? cards.Length : mode == 1 ? 3 : 1;
            int Offset(int index) => mode switch { 2 => 6, 3 => 1_500_000_000, _ => new[] { 5, -2, -3 }[index] };
            using (SimulationNotificationIsolation.Enter())
            {
                for (int index = 0; index < actions; index++)
                {
                    if (mode == 0)
                    {
                        if (!prediction.ManualPlay(prediction.State.FindCard(cards[index])!, null, out _))
                            throw new InvalidOperationException("Temporary strength card suspended.");
                    }
                    else
                    {
                        shadow.ApplyTemporaryStrengthGain<FlexPotionPower>(player.Creature, Offset(index), player.Creature);
                        PowerLifecycleSupport.ResolvePowerAmountChanges(prediction, shadow);
                    }
                    CaptureExpected(prediction, shadow);
                }
                var child = prediction.Fork();
                var childCombat = (SimulatedCombatState)child.State.CombatState;
                childCombat.RestoreTemporaryStrength(participants);
                PowerLifecycleSupport.ResolvePowerAmountChanges(child, childCombat);
                CaptureExpected(child, childCombat);
                foreach (var enemy in enemies)
                    AssertSnapshotEqual(CaptureActual(combat, player, enemy),
                        CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "TemporaryStrength", "RootUnchanged");
                AssertSnapshotEqual(expected[^2][0], CaptureSimulated(prediction, shadow, player, enemies[0]), "TemporaryStrength", "ParentAfterChildRestore");
            }
            for (int index = 0; index < actions; index++)
            {
                if (mode == 0)
                {
                    if (!cards[index].TryManualPlay(null)) throw new InvalidOperationException("Native temporary strength card rejected.");
                }
                else await PowerCmd.Apply<FlexPotionPower>(new BlockingPlayerChoiceContext(), player.Creature, Offset(index), player.Creature, null);
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (mode == 2 && player.Creature.GetPowerAmount<StrengthPower>() != 6)
                    throw new InvalidOperationException("Native counter cap did not preserve the strength offset.");
                if (mode == 3 && player.Creature.GetPowerAmount<StrengthPower>() != 999_999_999)
                    throw new InvalidOperationException("Native first-application cap did not exercise both callbacks.");
                CompareNative(index, $"Mode{mode}-Action{index}");
            }
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).OfType<TemporaryStrengthPower>().ToArray())
                await power.AfterSideTurnEnd(new BlockingPlayerChoiceContext(), participants[0].Side, participants);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CompareNative(actions, $"Mode{mode}-AfterSideTurnEnd");
            evidence.Add(new { mode, actions, expectedPowers, finalStrength = participants.Select(creature => creature.GetPowerAmount<StrengthPower>()).ToArray() });

            void CaptureExpected(CombatPredictionSimulator simulator, SimulatedCombatState state)
            {
                expected.Add(enemies.Select(enemy => CaptureSimulated(simulator, state, player, enemy)).ToArray());
                expectedPowers.Add(SurgicalPowerValues(state.EffectivePowers()));
            }
            void CompareNative(int index, string stage)
            {
                foreach (var target in Enumerable.Range(0, enemies.Length))
                    AssertSnapshotEqual(expected[index][target], CaptureActual(combat, player, enemies[target]), "TemporaryStrength", stage);
                string[] actual = SurgicalPowerValues(combat.Creatures.SelectMany(creature => creature.Powers));
                if (!expectedPowers[index].SequenceEqual(actual))
                    throw new InvalidOperationException($"Temporary strength Power order/lifetime differs at {stage}: expected="
                        + string.Join('|', expectedPowers[index]) + "; actual=" + string.Join('|', actual));
            }
        }
        _completedChecks.Add(capped ? "TemporaryStrength:NativeStackAndInitialCounterCap:RequestedOffset:BeforeAppliedAndAmountChanged:AfterSideTurnEnd:FullState:ForkIsolation"
            : "TemporaryStrength:NativeLossAndGain:FirstApplicationOrder:StackingAndNegativeOffsets:Artifact:StrengthRetirementReacquisition:AfterSideTurnEnd:FullState:ForkIsolation");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "temporary-strength.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
