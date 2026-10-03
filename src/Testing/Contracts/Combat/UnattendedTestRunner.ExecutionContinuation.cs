using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunDrawExecutionContinuationContractAsync(CombatState live, Player player, bool nested = false)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        foreach (string id in (nested ? new[] { "DAZED" } : ["STRIKE_SILENT", "DEFEND_SILENT"]))
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Draw" });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_DEFECT", "DEFEND_DEFECT", "BACKFLIP", "DEADLY_POISON" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Discard" });
        await InjectPowerAsync(live, player, new() { PowerId = "STRATAGEM_POWER", Amount = 1, Target = "Player" });
        if (nested) await InjectPowerAsync(live, player, new() { PowerId = "PAGESTORM_POWER", Amount = 1, Target = "Player" });
        await InjectRelicAsync(player, new() { RelicId = "THE_ABACUS" });
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        var names = SolverDisplayNames.Capture(live);
        string Stamp(CombatPredictionSimulator sim) => DescribeContinuationContractState(sim, root, player);
        void Equal(string expected, string actual, string stage)
        {
            if (expected != actual) throw new InvalidOperationException($"Draw execution {stage}:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
        }
        CombatPredictionSimulator Legacy(IReadOnlyList<PlanCardChoice>? choices)
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var sim = parent.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
            combat.BeginActionChoices(choices);
            try
            {
                sim.Draw(player, 5);
            }
            finally { combat.EndActionChoices(); }
            return sim;
        }
        string before = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var probe = Legacy(null);
        TurnStartChoiceRequest request = ((SimulatedCombatState)probe.State.CombatState).PendingTurnStartChoice
            ?? throw new InvalidOperationException("Draw fixture did not reach its player Stratagem selector.");
        PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(request.Spec!, names, 32, 32)
            .Select(choice => choice with { SourceId = request.SourceId, Timing = request.Timing, ContextId = request.ContextId }).ToArray();
        var seed = parent.Fork(); var seedCombat = (SimulatedCombatState)seed.State.CombatState;
        using (SimulationNotificationIsolation.Enter())
        {
            if (!seed.BeginExecutionContinuationCapture()) throw new InvalidOperationException("Draw capture did not start.");
            seedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            try { seed.Draw(player, 5); }
            finally { seedCombat.EndActionChoices(); seed.EndExecutionContinuationCapture(); }
        }
        var continuation = seed.TakeExecutionContinuation()
            ?? throw new InvalidOperationException("Draw capture did not retain its shuffle and draw positions.");
        if (continuation.Steps.Count < (nested ? 5 : 3) || seed.State.GetPlayerCombatState(player).Hand.Cards.Count != (nested ? 1 : 2))
            throw new InvalidOperationException("Draw capture did not preserve the completed draw prefix.");
        bool ordinaryRejected = false;
        try { _ = seed.Fork(); }
        catch (InvalidOperationException error) when (error.Message.Contains("owning continuation fork", StringComparison.Ordinal))
            { ordinaryRejected = true; }
        if (!ordinaryRejected) throw new InvalidOperationException("Ordinary Fork accepted a suspended execution.");
        CombatPredictionSimulator Resume(int index)
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator sim;
            PredictionExecutionContinuation frame;
            lock (seed) sim = seed.ForkExecutionContinuation(continuation, out frame);
            var combat = (SimulatedCombatState)sim.State.CombatState;
            combat.BeginActionChoices([choices[index]]);
            try
            {
                if (!sim.ResumeExecutionContinuation(frame)) throw new InvalidOperationException("Draw continuation suspended again unexpectedly.");
            }
            finally { combat.EndActionChoices(); }
            return sim;
        }
        string[] reference = choices.Select(c => Stamp(Legacy([c]))).ToArray();
        for (int index = choices.Length - 1; index >= 0; index--)
        {
            var resumed = Resume(index);
            Equal(reference[index], Stamp(resumed), "all choices " + index);
            Equal(Stamp(resumed), Stamp(resumed.Fork()), "completed Fork");
        }
        var first = Resume(0); string firstBefore = Stamp(first);
        _ = Resume(choices.Length - 1);
        Equal(firstBefore, Stamp(first), "first sibling preserved");
        using (SimulationNotificationIsolation.Enter()) first.State.GetPlayerCombatState(player).Hand.Cards[0].Upgrade();
        Equal(reference[0], Stamp(Resume(0)), "revisit after sibling mutation");
        string[] parallel = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() => Stamp(Resume(i)))));
        for (int i = 0; i < parallel.Length; i++) Equal(reference[i], parallel[i], "DOP2");
        Equal(before, Stamp(parent), "parent preserved");
        Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live preserved");
        string expectedNative = ContinuationStamp.CapturePredicted(player, Legacy([choices[0]]),
            root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        using var session = NativeChoiceRuntime.Begin(live, player, "test:draw-execution-continuation");
        session.SetPlanAndStartDriving(NGame.Instance!, [choices[0]], deadline.Token);
        Task native = CardPileCmd.Draw(new BlockingPlayerChoiceContext(), 5, player, fromHandDraw: false);
        await session.AwaitProducerAndCompleteAsync(native).WaitAsync(deadline.Token);
        Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
        _completedChecks.Add($"DrawExecutionContinuation:nested={nested}:choices={choices.Length}:partial-draw:shuffle-loop:state:history:RNG:siblings:DOP2:native");
    }
}
