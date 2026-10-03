using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static readonly string[] ContinuationPotions =
    ["ATTACK_POTION", "SKILL_POTION", "POWER_POTION", "COLORLESS_POTION", "ASHWATER",
        "DROPLET_OF_PRECOGNITION", "GAMBLERS_BREW", "LIQUID_MEMORIES", "TOUCH_OF_INSANITY"];

    private static async Task PreparePotionContinuationCardsAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "STRIKE_SILENT", "DEFEND_SILENT", "BACKFLIP" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "FLASH_OF_STEEL" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Draw" });
        foreach (string id in new[] { "STRIKE_DEFECT", "DEFEND_DEFECT", "DEADLY_POISON" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Discard" });
        SetEnergy(player, 3);
    }

    private async Task RunPotionContinuationContractAsync(CombatState live, Player player)
    {
        foreach (string id in ContinuationPotions)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var item in player.PotionSlots.ToArray()) item?.Discard();
            await PreparePotionContinuationCardsAsync(live, player);
            PotionModel potion = InjectPotionForTest(player, id);
            int slot = player.GetPotionSlotIndex(potion);
            await InjectRelicAsync(player, new() { RelicId = "BELT_BUCKLE" });
            await InjectRelicAsync(player, new() { RelicId = "REPTILE_TRINKET" });
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            SolverDisplayNames names = SolverDisplayNames.Capture(live);
            CombatPredictionSimulator parent = root.ForkSimulator();
            string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
            void Equal(string expected, string actual, string stage)
            {
                if (expected != actual) throw new InvalidOperationException($"{id} {stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            }
            void Finish(CombatPredictionSimulator sim)
            {
                if (!CombatBeamSolver.SettleReplayActionBoundary(sim, (SimulatedCombatState)sim.State.CombatState))
                    throw new InvalidOperationException(id + " unexpectedly suspended at action completion.");
                sim.CheckWinCondition(root.StartTurnNumber);
            }
            CombatPredictionSimulator Legacy(PlanCardChoice? choice)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
                combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                try
                {
                    // Independent copy of the original manual potion sequence.
                    combat.ConsumePotion(player, slot);
                    combat.BeforePotionUsed(sim, potion, null);
                    if (!PotionOnUseSupport.Use(sim, combat, potion, null)
                        || choice != null && !PotionChoiceSupport.Apply(sim, potion, choice))
                        throw new InvalidOperationException(id + " legacy produced a nested choice.");
                    if (sim.State.GetCreature(player.Creature).IsAlive) combat.AfterPotionUsed(sim, potion, null);
                    sim.SynchronizePowerAmountPredictionStates();
                    PowerLifecycleSupport.ResolvePowerAmountChanges(sim, combat);
                    TriggeredPowerSupport.CompensateHistorySince(sim, combat, parent.History.Entries.Count);
                    if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, new ForkableSet<uint>()))
                        throw new InvalidOperationException(id + " legacy death selector.");
                    Finish(sim);
                }
                finally { combat.EndActionChoices(); }
                return sim;
            }
            string before = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            var discovery = PotionChoiceSupport.GeneratesCardChoice(potion) ? Legacy(null) : parent;
            PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(PotionChoiceSupport.GetSpec(discovery, potion), names, 256, 256)
                .Select(c => c with { SourceId = id }).ToArray();
            if (choices.Length < 3) throw new InvalidOperationException(id + " has insufficient contract choices.");
            var seed = parent.Fork(); var seedCombat = (SimulatedCombatState)seed.State.CombatState;
            var frame = new PotionChoiceFrame(potion, seed.History.Entries.Count, seed.ShuffleEventCount);
            using (SimulationNotificationIsolation.Enter())
            {
                seedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                try
                {
                    if (!PotionExecutionSupport.Prepare(seed, seedCombat, potion, slot, null))
                        throw new InvalidOperationException(id + " failed to prepare continuation.");
                }
                finally { seedCombat.EndActionChoices(); }
            }
            using var checkpoint = new PotionChoiceContinuation(seed, [], frame);
            CombatPredictionSimulator Resume(int index)
            {
                using var isolation = SimulationNotificationIsolation.Enter();
                var child = checkpoint.Fork(CancellationToken.None);
                var combat = (SimulatedCombatState)child.Simulator.State.CombatState;
                combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                try
                {
                    if (!PotionExecutionSupport.Complete(child.Simulator, combat, child.Frame.Potion,
                        null, choices[index], child.Frame.HistoryStart, child.Deaths))
                        throw new InvalidOperationException(id + " resumed with a nested choice.");
                    Finish(child.Simulator);
                }
                finally { combat.EndActionChoices(); }
                if (combat.GetPotionAtSlot(player, slot) != null) throw new InvalidOperationException(id + " potion was not consumed.");
                return child.Simulator;
            }
            var references = choices.Select(c => Stamp(Legacy(c))).ToArray();
            for (int i = choices.Length - 1; i >= 0; i--)
            {
                var resumed = Resume(i);
                Equal(references[i], Stamp(resumed), "all choices " + i);
                Equal(Stamp(resumed), Stamp(resumed.Fork()), "completed Fork");
            }
            int selected = Array.FindIndex(choices, c => c.Cards.Count > 0);
            var first = Resume(selected); string firstBefore = Stamp(first);
            _ = Resume(choices.Length - 1);
            Equal(firstBefore, Stamp(first), "first sibling preserved");
            using (SimulationNotificationIsolation.Enter())
                first.State.GetPlayerCombatState(player).Hand.Cards[0].Upgrade();
            Equal(references[selected], Stamp(Resume(selected)), "revisit after sibling mutation");
            string[] parallel = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() => Stamp(Resume(i)))));
            for (int i = 0; i < 2; i++) Equal(references[i], parallel[i], "DOP2");
            Equal(before, Stamp(parent), "parent preserved");
            Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live,
                includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(live));
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
            int searchChoices = await Task.Run(() => new CombatBeamSolver(root, names, damage,
                policy, CancellationToken.None, potionPolicyOverride: SolverPotionPolicy.Smart)
                .VerifyPotionChoiceContinuationForTesting());
            _completedChecks.Add($"PotionContinuation:{id}:choices={choices.Length}:search-choices={searchChoices}:state:history:RNG:post-hooks:siblings:DOP2");

            string expectedNative = ContinuationStamp.CapturePredicted(player, Legacy(choices[selected]),
                root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using var session = NativeChoiceRuntime.Begin(live, player, "test:potion-continuation");
            session.SetPlanAndStartDriving(NGame.Instance!, [choices[selected]], deadline.Token);
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is UsePotionAction use && use.PotionIndex == (uint)slot && ReferenceEquals(use.Player, player),
                () => potion.EnqueueManualUse(null), deadline.Token);
            await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
            Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
            _completedChecks.Add($"PotionContinuation:{id}:native-full-continuation");
        }
    }
}
