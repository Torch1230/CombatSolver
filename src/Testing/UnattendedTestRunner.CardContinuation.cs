using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunCardContinuationContractAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        string[] ids = ["DAGGER_THROW", "DEFEND_SILENT", "STRIKE_SILENT", "ACROBATICS", "BACKFLIP",
            "DEADLY_POISON", "PIERCING_WAIL", "PREPARED", "SURVIVOR"];
        foreach (string id in ids)
            await InjectCardAsync(live, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "WOUND", "DAZED", "STRIKE_SILENT" })
            await InjectCardAsync(live, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        SetEnergy(player, 5);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        Creature target = root.Enemies[0];
        string Stamp(CombatPredictionSimulator sim)
        {
            SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
            StateFingerprintBuilder key = new();
            combat.AppendFingerprint(ref key, sim);
            return ContinuationStamp.CapturePredicted(player, sim, root.StartTurnNumber, root.Forecast,
                root.StartTurnNumber).StateText + "\nFINGERPRINT=" + key.Finish()
                + "\nSHUFFLES=" + sim.ShuffleEventCount + ";TERMINAL=" + sim.TerminalStamp
                + ";IN_PROGRESS=" + sim.IsInProgress + ";LOSING=" + sim.IsAboutToLose
                + "\nHISTORY=" + CardContinuationContractHistoryText(sim);
        }
        PredictedCard Dagger(CombatPredictionSimulator sim) => sim.State.GetPlayerCombatState(player)
            .Hand.Cards.Single(card => card.Preview.Id.Entry == "DAGGER_THROW");
        CombatPredictionSimulator Replay(IReadOnlyList<PlanCardChoice>? choices)
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator sim = parent.Fork();
            SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
            ForkableSet<uint> deaths = [];
            combat.BeginActionChoices(choices);
            try
            {
                using (combat.BeginCardExecutionScope(deaths))
                    if (sim.ManualPlay(Dagger(sim), target, out _)) Finish(sim, deaths);
            }
            finally { combat.EndActionChoices(); }
            return sim;
        }
        void Finish(CombatPredictionSimulator sim, ForkableSet<uint> deaths)
        {
            SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, deaths)
                || !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                throw new InvalidOperationException("Continuation fixture unexpectedly opens a post-action choice.");
            sim.CheckWinCondition(root.StartTurnNumber);
        }
        CardContinuationContractCheckpoint Pause()
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator seed = parent.Fork();
            return CaptureCardContinuationContract(seed, Dagger(seed), target, new ForkableSet<uint>())
                ?? throw new InvalidOperationException("Plain Dagger Throw was rejected by production continuation eligibility.");
        }
        CombatPredictionSimulator Resume(CardContinuationContractCheckpoint checkpoint, PlanCardChoice choice)
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            var result = checkpoint.Resume([choice], CancellationToken.None);
            if (!result.Completed) throw new InvalidOperationException("Prototype resume requires full replay fallback.");
            Finish(result.Simulator, result.Deaths);
            return result.Simulator;
        }
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        string parentBefore = Stamp(parent);
        CombatPredictionSimulator pending = Replay(null);
        if (CardChoiceContinuation.Take(pending, new()) != null)
            throw new InvalidOperationException("Ordinary replay unexpectedly captured a continuation.");
        var request = ((SimulatedCombatState)pending.State.CombatState).PendingTurnStartChoice
            ?? throw new InvalidOperationException("Legacy reference did not stop at a choice.");
        PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(request.Spec!, names, 32, 32).ToArray();
        if (choices.Length < 8) throw new InvalidOperationException("Insufficient distinct benchmark choices: " + choices.Length);
        string[] references = choices.Select(choice => Stamp(Replay([choice]))).ToArray();
        using (CardContinuationContractCheckpoint checkpoint = Pause())
        {
            CombatPredictionSimulator first = Resume(checkpoint, choices[0]);
            string firstBefore = Stamp(first);
            for (int i = choices.Length - 1; i >= 0; i--)
            {
                CombatPredictionSimulator resumed = Resume(checkpoint, choices[i]);
                Equal(references[i], Stamp(resumed), "full state/history option " + i);
                AssertCardContinuationContractIdentity(resumed, player);
                Equal(Stamp(resumed), Stamp(resumed.Fork()), "completed child fork");
            }
            Equal(firstBefore, Stamp(first), "earliest sibling after later resumes");
            first.State.GetPlayerCombatState(player).DiscardPile.Cards[0].Upgrade();
            first.History.OfType<CombatPredictionDamageReceivedEntry>().Single().Result.BlockedDamage += 1000;
            Equal(references[0], Stamp(Resume(checkpoint, choices[0])), "revisit after sibling card mutation");
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            try { checkpoint.Resume([choices[0]], cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException e) when (e.CancellationToken == cancelled.Token) { }
            PlanCardChoice invalid = choices[0] with { ContextId = "invalid-prototype-context" };
            try { checkpoint.Resume([invalid], CancellationToken.None); throw new InvalidOperationException("Invalid choice ignored."); }
            catch (InvalidPlannedChoiceBranchException) { }
            Equal(references[0], Stamp(Resume(checkpoint, choices[0])), "revisit after cancel/error");
            using CancellationTokenSource inFlight = new();
            try { checkpoint.Resume([choices[0]], inFlight.Token, () => inFlight.Cancel()); throw new InvalidOperationException("In-flight cancellation ignored."); }
            catch (OperationCanceledException e) when (e.CancellationToken == inFlight.Token) { }
            InvalidOperationException injected = new("prototype error identity");
            try { checkpoint.Resume([choices[0]], CancellationToken.None, () => throw injected); throw new InvalidOperationException("Injected error ignored."); }
            catch (InvalidOperationException e) when (ReferenceEquals(e, injected)) { }
            Equal(references[0], Stamp(Resume(checkpoint, choices[0])), "revisit after in-flight cancel/error");
            using Barrier barrier = new(2);
            string[] parallel = new string[2];
            Task[] workers = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var result = checkpoint.Resume([choices[i]], CancellationToken.None, () =>
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("No DOP2 execution overlap.");
                });
                if (!result.Completed) throw new InvalidOperationException("Parallel resume was incomplete.");
                Finish(result.Simulator, result.Deaths);
                parallel[i] = Stamp(result.Simulator);
            })).ToArray();
            await Task.WhenAll(workers);
            for (int i = 0; i < 2; i++) Equal(references[i], parallel[i], "DOP2 option " + i);
        }
        Equal(parentBefore, Stamp(parent), "parent isolation");
        Equal(liveBefore, ContinuationStamp.CaptureLive(live).StateText, "live isolation");
        _completedChecks.Add("CardContinuationContract:AllOptions:FullStateRngHistory:Identity:SiblingMutation:Revisit:DOP2:Cancel:InvalidChoice");

        VerifyCardContinuationContractBoundaries(parent, player, target, names, Stamp);
        _completedChecks.Add("CardContinuationContract:PriorHistory:Shuffle:Upgrade:FallbackReplayAttachmentPairSly:OwnedDamage");

        CombatPredictionSimulator expected = Replay([choices[0]]);
        string expectedNative = ContinuationStamp.CapturePredicted(player, expected, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        CardModel liveCard = player.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == "DAGGER_THROW");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        using NativeChoiceSession session = NativeChoiceRuntime.Begin(live, player, "test:dagger-prototype");
        session.SetPlanAndStartDriving(NGame.Instance!, [choices[0] with { SourceId = "DAGGER_THROW" }], deadline.Token);
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), liveCard),
            () => { if (!liveCard.TryManualPlay(target)) throw new InvalidOperationException("Native dagger not playable."); }, deadline.Token);
        await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
        Equal(expectedNative, ContinuationStamp.CaptureLive(live).StateText, "native full continuation");
        _completedChecks.Add("CardContinuationContract:NativeDaggerThrow:FullContinuation");
        await RunPriorityCardContinuationContractAsync(live, player, "ACROBATICS");
        await RunPriorityCardContinuationContractAsync(live, player, "PREPARED");

        static void Equal(string expected, string actual, string what)
        {
            if (expected != actual) throw new InvalidOperationException(what + " differs:\nEXPECTED\n" + expected + "\nACTUAL\n" + actual);
        }
    }

    private static void AssertCardContinuationContractIdentity(CombatPredictionSimulator sim, Player player, string cardId = "DAGGER_THROW")
    {
        var starts = sim.History.OfType<CombatPredictionCardPlayStartedEntry>().ToArray();
        var ends = sim.History.OfType<CombatPredictionCardPlayFinishedEntry>().ToArray();
        if (starts.Length != 1 || ends.Length != 1 || !ReferenceEquals(starts[0].CardPlay, ends[0].CardPlay)
            || !ReferenceEquals(starts[0].Trace, ends[0].Trace)
            || !sim.History.HasCardPlayStartedSince(0, ends[0].Trace!)
            || !ReferenceEquals(starts[0].CardPlay.Card, sim.State.GetPlayerCombatState(player).DiscardPile.Cards
                .Single(c => c.Preview.Id.Entry == cardId).Preview))
            throw new InvalidOperationException("Active play/trace/card remapping failed.");
        foreach (var draw in sim.History.OfType<CombatPredictionCardDrawnEntry>())
            if (!ReferenceEquals(sim.History.GetResolvedEntry<CombatPredictionCardDrawResolvedEntry>(draw).OriginalEntry, draw))
                throw new InvalidOperationException("Deferred draw history pairing failed.");
    }

    private static string CardContinuationContractHistoryText(CombatPredictionSimulator simulator)
    {
        Dictionary<PredictionTraceFrame, int> traces = new(ReferenceEqualityComparer.Instance);
        int Trace(PredictionTraceFrame? frame)
        {
            if (frame == null) return -1;
            if (traces.TryGetValue(frame, out int id)) return id;
            _ = Trace(frame.Parent);
            traces.Add(frame, id = traces.Count);
            return id;
        }
        object? Value(object? value) => value switch
        {
            null => null,
            string or bool or int or uint or long or ulong or float or double or decimal => value,
            Enum e => e.ToString(),
            Creature c => c.CombatId,
            Player p => p.Creature.CombatId,
            PredictedCard c => Value(c.Preview),
            CardModel c => new { key = CardChoiceSupport.ChoiceCardKey(c), target = c.CurrentTarget?.CombatId, index = c.CurrentPlayIndex },
            AbstractModel m => new { model = m.Id.Entry, type = m.GetType().FullName },
            CombatPredictionHistoryEntry e => e.Index,
            System.Collections.IEnumerable list => list.Cast<object?>().Select(Value).ToArray(),
            _ when value.GetType().IsValueType || value is CardPlay || value is DamageResult =>
                value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name)
                    .ToDictionary(p => p.Name, p => Value(p.GetValue(value))),
            _ => throw new InvalidOperationException("Unserialized history payload: " + value.GetType()),
        };
        var entries = simulator.History.Select(entry => new
        {
            type = entry.GetType().Name, entry.Index, trace = Trace(entry.Trace),
            payload = entry.GetType().GetProperties().Where(p => p.Name is not ("Index" or "Trace"))
                .OrderBy(p => p.Name).ToDictionary(p => p.Name, p => Value(p.GetValue(entry))),
        }).ToArray();
        return JsonSerializer.Serialize(new { entries, traces = traces.OrderBy(p => p.Value).Select(p => new
        { id = p.Value, parent = Trace(p.Key.Parent), source = p.Key.Source.Id.Entry,
            invocation = p.Key.Invocation.Action?.ToString() ?? p.Key.Invocation.Method!.ToString() }) });
    }
}
