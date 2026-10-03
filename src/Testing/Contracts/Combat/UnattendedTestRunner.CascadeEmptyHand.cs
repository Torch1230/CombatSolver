using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCascadeEmptyHandAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectRelicAsync(player, new() { RelicId = "UNCEASING_TOP" });
        await InjectCardAsync(live, player, new() { CardId = "CASCADE", Pile = "Hand", UpgradeLevels = 1 });
        await InjectCardAsync(live, player, new() { CardId = "THUNDERCLAP", Pile = "Draw" });
        // The ordered piles and shuffle state immediately before T6 Cascade in report d9c106.
        (string Id, int Upgrade)[] discard =
        [
            ("STRIKE_IRONCLAD", 0), ("BATTLE_TRANCE", 1), ("DEFEND_IRONCLAD", 0),
            ("STRIKE_IRONCLAD", 1), ("BULLY", 1), ("VICIOUS", 1), ("STRIKE_IRONCLAD", 0),
            ("POMMEL_STRIKE", 1), ("BASH", 1), ("TRUE_GRIT", 1), ("TAUNT", 1),
            ("BODY_SLAM", 1), ("DEFEND_IRONCLAD", 1), ("HOWL_FROM_BEYOND", 0),
            ("SHRUG_IT_OFF", 0), ("SECOND_WIND", 0), ("HEADBUTT", 1),
        ];
        foreach (var entry in discard)
            await InjectCardAsync(live, player, new() { CardId = entry.Id, Pile = "Discard", UpgradeLevels = entry.Upgrade });
        SetEnergy(player, 4);
        player.RunState.Rng.Shuffle.LoadFromSerializable(new SerializableRng
        {
            counter = 684, state0 = 15277516461837409286UL, state1 = 2205921759845499263UL,
            state2 = 16515738470700318798UL, state3 = 7435349252154548846UL,
        });
        var nativeCard = player.PlayerCombatState!.Hand.Cards.Single();
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var names = SolverDisplayNames.Capture(live);
        string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
        void Equal(string expected, string actual, string stage)
        {
            if (expected != actual) throw new InvalidOperationException($"Cascade empty hand/{stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
        }
        CombatPredictionSimulator Replay(IReadOnlyList<PlanCardChoice>? choices, bool capture = false)
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
            if (capture && !sim.BeginExecutionContinuationCapture()) throw new InvalidOperationException("Cascade capture did not start.");
            combat.BeginActionChoices(choices);
            try
            {
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    sim.ManualPlay(sim.State.FindCard(nativeCard)!, target: null, out _);
                if (!sim.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(sim, combat);
            }
            finally { combat.EndActionChoices(); if (capture) sim.EndExecutionContinuationCapture(); }
            return sim;
        }
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var seed = Replay(null, capture: true);
        List<PlanCardChoice> prefix = [];
        while (seed.HasPendingChoice)
        {
            if (prefix.Count >= 4) throw new InvalidOperationException("Cascade choices did not make bounded progress.");
            var request = ((SimulatedCombatState)seed.State.CombatState).PendingTurnStartChoice!;
            var choice = CardChoiceSupport.BuildChoices(request.Spec!, names, 12, 12)
                .OrderByDescending(candidate => candidate.Cards.Any(token => token.CardId == "BASH")).First()
                with { SourceId = request.SourceId, Timing = request.Timing, ContextId = request.ContextId };
            Entry.Logger.Info($"CASCADE_FIXTURE_CHOICE source={request.SourceId} cards={string.Join(',', choice.Cards.Select(token => token.CardId))} hand={string.Join(',', seed.State.GetPlayerCombatState(player).Hand.Cards.Select(card => card.Preview.Id.Entry))}");
            var continuation = seed.TakeExecutionContinuation()
                ?? throw new InvalidOperationException("Cascade lost its execution checkpoint.");
            using (SimulationNotificationIsolation.Enter())
            {
                var resumed = seed.ForkExecutionContinuation(continuation, out var copied);
                var combat = (SimulatedCombatState)resumed.State.CombatState;
                combat.BeginActionChoices([choice]);
                try { resumed.ResumeExecutionContinuation(copied); if (!resumed.HasPendingChoice) CombatBeamSolver.SettleReplayActionBoundary(resumed, combat); }
                finally { combat.EndActionChoices(); }
                prefix.Add(choice);
                Equal(Stamp(Replay(prefix)), Stamp(resumed), "checkpoint versus full replay");
                seed = resumed;
            }
        }
        Equal(Stamp(seed), Stamp(seed.Fork()), "completed Fork");
        if (!prefix.Select(choice => choice.SourceId).SequenceEqual(["TRUE_GRIT"]))
            throw new InvalidOperationException("Cascade fixture did not reach its nested True Grit selection.");
        Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
        string expectedNative = ContinuationStamp.CapturePredicted(player, seed, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        using var session = NativeChoiceRuntime.Begin(live, player, "test:cascade-empty-hand");
        session.SetPlanAndStartDriving(NGame.Instance!, prefix.ToArray(), deadline.Token);
        GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
            () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native Cascade could not play."); }, deadline.Token);
        await session.AwaitProducerAndCompleteAsync(native.CompletionTask).WaitAsync(deadline.Token);
        Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
        _completedChecks.Add("CascadeEmptyHand:UnceasingTop:Shuffle:TrueGrit:FullReplay:ExecutionContinuation:Fork:NativeState:RNG");
    }

    private async Task AssertEmptyHandEffectBoundaryAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        await InjectRelicAsync(player, new() { RelicId = "UNCEASING_TOP" });
        foreach (string fixture in new[] { "Havoc", "SingleCard", "Repeat" })
        {
            await ClearPlayerPilesAsync(player);
            string id = fixture == "Havoc" ? "HAVOC" : "DEFEND_IRONCLAD";
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
            if (fixture == "Havoc")
                await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Draw" });
            await InjectCardAsync(live, player, new() { CardId = "BASH", Pile = "Discard" });
            var nativeCard = player.PlayerCombatState!.Hand.Cards.Single();
            if (fixture == "Repeat") nativeCard.BaseReplayCount = 1;
            SetEnergy(player, 3);
            var root = CombatRootSnapshot.Capture(live);
            var sim = root.ForkSimulator();
            using (sim.BeginCardOrPotionEffect(player))
            {
                try { sim.Fork(); throw new InvalidOperationException("Expected an effect-scope fork refusal."); }
                catch (InvalidOperationException error) when (error.Message == "Combat prediction cannot be forked during a card or potion effect.") { }
            }
            using (SimulationNotificationIsolation.Enter())
            using (((SimulatedCombatState)sim.State.CombatState).BeginCardExecutionScope(new ForkableSet<uint>()))
                if (!sim.ManualPlay(sim.State.FindCard(nativeCard)!, null, out _))
                    throw new InvalidOperationException("Empty-hand boundary fixture suspended unexpectedly.");
            if (!CombatBeamSolver.SettleReplayActionBoundary(sim, (SimulatedCombatState)sim.State.CombatState))
                throw new InvalidOperationException("Empty-hand fixture action did not settle.");
            string expected = ContinuationStamp.CapturePredicted(player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            if (DescribeContinuationContractState(sim, root, player) != DescribeContinuationContractState(sim.Fork(), root, player))
                throw new InvalidOperationException("Completed effect scope changed across Fork.");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native empty-hand fixture could not play."); }, deadline.Token);
            await native.CompletionTask.WaitAsync(deadline.Token);
            string actual = ContinuationStamp.CaptureLive(live).StateText;
            if (expected != actual)
                throw new InvalidOperationException($"Empty-hand {fixture}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            _completedChecks.Add($"EmptyHandEffectBoundary:{fixture}:UnceasingTop:ResultPile:Shuffle:NativeState:RNG:Fork");
        }
    }
}
