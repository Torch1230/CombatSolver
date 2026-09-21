using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLoopHistoryDependenciesAsync(CombatState combat, Player player)
    {
        async Task<CombatRootSnapshot> Root(string card, string pile = "Hand")
        {
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
            await InjectCardAsync(combat, player, new() { CardId = card, Pile = pile });
            return CombatRootSnapshot.Capture(combat);
        }
        void Check(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
            _completedChecks.Add("LoopHistory:" + message);
        }
        StateFingerprint Key(CombatRootSnapshot root, CombatPredictionSimulator sim)
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            var evaluator = new SurgicalEvaluationDriver(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            return ReleaseSurgicalSnapshot(evaluator.Evaluate(sim.Fork())).StateKey;
        }
        void PlayHistory(CombatPredictionSimulator sim, bool ethereal)
        {
            PredictedCard defend = sim.State.GetPlayerCombatState(player).Hand.Cards
                .Single(card => card.Preview.Id.Entry == "DEFEND_IRONCLAD");
            using (sim.PushActionSource(defend.Original, PredictionActionKind.CardPlay))
            {
                var play = CreateHistoryProbePlay(defend, player);
                sim.History.CardPlayStarted(defend, play);
                sim.History.CardPlayFinished(defend, play, ethereal);
            }
        }
        foreach (string pile in new[] { "Hand", "Draw", "Exhaust" })
        {
            var root = await Root("BANSHEES_CRY", pile);
            Check(root.HistoryDependencies == CombatHistoryDependencies.EtherealPlays, "BansheeMask:" + pile);
            var parent = root.ForkSimulator();
            var child = parent.Fork();
            PlayHistory(child, false);
            Check(Key(root, parent) == Key(root, child), "IrrelevantFinishedPlaysMerge:" + pile);
            PlayHistory(child, true);
            Check(Key(root, parent) != Key(root, child), "EtherealPlaysDistinct:" + pile);
            Check(Key(root, child) == Key(root, child.Fork()), "ForkStable:" + pile);
        }
        var generatedRoot = await Root("INFERNAL_BLADE");
        Check(generatedRoot.HistoryDependencies == CombatHistoryDependencies.All, "FutureReaderMask");
        var before = generatedRoot.ForkSimulator();
        var after = before.Fork();
        PlayHistory(after, false);
        Check(Key(generatedRoot, before) != Key(generatedRoot, after), "DistinctBeforeReaderExists");
        decimal AddAxe(CombatPredictionSimulator sim)
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            sim.CreateAndAddGeneratedCardsToCombat<GoldAxe>(player, PileType.Hand, 1, player);
            return GoldAxeValue(sim, sim.State.GetPlayerCombatState(player).Hand.Cards
                .Single(card => card.Preview.Id.Entry == "GOLD_AXE"));
        }
        Check(AddAxe(after) == AddAxe(before) + 1, "GeneratedAxeObservesPriorHistory");
        Check(CombatHistoryCounterKey.Capture([ModelDb.Card<DefendIronclad>()], true)
            == CombatHistoryDependencies.All, "ExternalHooksConservative");
        foreach (var method in typeof(Engine.InCombat.Mirrors.Cards.OnPlay.CardGenerationCardMirrors)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Type? sourceType = method.GetParameters().FirstOrDefault()?.ParameterType;
            if (sourceType == null || !typeof(CardModel).IsAssignableFrom(sourceType)) continue;
            var source = ModelDb.AllCards.Single(card => card.GetType() == sourceType);
            Check(CombatHistoryCounterKey.Capture([source], false) == CombatHistoryDependencies.All,
                "GeneratorCatalog:" + source.Id.Entry);
        }
        Check(CombatHistoryCounterKey.Capture([ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.NightmarePower>()], false)
            == CombatHistoryDependencies.All, "StoredCopyCanOutliveOriginal");
        // Concurrent consumers cannot each receive an independent 4096-action allowance.
        SearchRequestWorkTotals totals = new();
        int consumed = 0;
        Parallel.For(0, 8, _ =>
        {
            while (totals.TryConsumeCycleReplayAction()) Interlocked.Increment(ref consumed);
        });
        Check(consumed == 4096 && totals.RemainingCycleReplayActions == 0
            && totals.Snapshot().CycleReplayActions == 4096, "ConcurrentRequestCap");
    }
}
