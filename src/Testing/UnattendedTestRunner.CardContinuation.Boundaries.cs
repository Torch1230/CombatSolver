using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void VerifyCardContinuationContractBoundaries(CombatPredictionSimulator parent, Player player,
        Creature target, SolverDisplayNames names, Func<CombatPredictionSimulator, string> stamp)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        PredictedCard Card(CombatPredictionSimulator sim, string id = "DAGGER_THROW")
            => sim.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == id);
        void Finish(CombatPredictionSimulator sim, ForkableSet<uint> deaths)
        {
            SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(sim, combat, combat.KnownEnemies, deaths)
                || !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                throw new InvalidOperationException("Boundary fixture produced a post-action choice.");
            sim.CheckWinCondition(combat.GetPlayerTurnNumber(player));
        }
        CombatPredictionSimulator Legacy(CombatPredictionSimulator basis, TurnStartChoiceCursor cursor,
            string cardId = "DAGGER_THROW")
        {
            CombatPredictionSimulator sim = basis.Fork();
            SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
            ForkableSet<uint> deaths = [];
            combat.BeginActionChoices(cursor);
            try
            {
                using (combat.BeginCardExecutionScope(deaths))
                    if (sim.ManualPlay(Card(sim, cardId), cardId == "DAGGER_THROW" ? target : null, out _))
                        Finish(sim, deaths);
            }
            finally { combat.EndActionChoices(); }
            return sim;
        }
        PlanCardChoice Select(TurnStartChoiceRequest request) => CardChoiceSupport.BuildChoices(
            request.Spec ?? throw new InvalidOperationException("Expected explicit choice spec."), names, 32, 32)[0]
                with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
        (CombatPredictionSimulator Sim, List<PlanCardChoice> Plans) ResolveAll(
            CombatPredictionSimulator basis, PlanCardChoice? first = null)
        {
            List<PlanCardChoice> plans = [];
            var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                if (plans.Count >= 16) throw new InvalidOperationException("Unbounded fixture choice chain.");
                PlanCardChoice choice = plans.Count == 0 && first != null ? first : Select(request);
                plans.Add(choice);
                return choice;
            });
            var sim = Legacy(basis, cursor);
            if (sim.HasPendingChoice) throw new InvalidOperationException("Automatic oracle did not finish.");
            return (sim, plans);
        }
        CardContinuationContractCheckpoint? Capture(CombatPredictionSimulator basis)
        {
            var seed = basis.Fork();
            return CaptureCardContinuationContract(seed, Card(seed), target, new ForkableSet<uint>());
        }
        (CombatPredictionSimulator Sim, string Path) Driver(CombatPredictionSimulator basis, IReadOnlyList<PlanCardChoice> plans)
        {
            // The prototype supports one own selector. The complete original action is retained
            // for every fallback; no option is dropped, reordered, or treated as a completed state.
            if (plans.Count == 1)
            {
                using CardContinuationContractCheckpoint? checkpoint = Capture(basis);
                if (checkpoint != null)
                {
                    var resumed = checkpoint.Resume(plans, CancellationToken.None);
                    if (resumed.Completed)
                    {
                        Finish(resumed.Simulator, resumed.Deaths);
                        return (resumed.Simulator, "resume");
                    }
                    return (Legacy(basis, new(plans)), "later-choice-full-replay");
                }
            }
            return (Legacy(basis, new(plans)), "ineligible-full-replay");
        }
        void Equal(CombatPredictionSimulator expected, CombatPredictionSimulator actual, string label)
        {
            if (stamp(expected) != stamp(actual))
                throw new InvalidOperationException(label + " differs:\n" + stamp(expected) + "\n" + stamp(actual));
        }
        string parentBefore = stamp(parent);
        var historyRoot = Legacy(parent, new((IReadOnlyList<PlanCardChoice>?)null), "DEFEND_SILENT");
        historyRoot.AddToPile(historyRoot.State.GetPlayerCombatState(player).DrawPile.Cards.ToArray(), PileType.Discard);
        Card(historyRoot).Upgrade();
        string historyBefore = stamp(historyRoot);
        var pending = Legacy(historyRoot, new((IReadOnlyList<PlanCardChoice>?)null));
        var request = ((SimulatedCombatState)pending.State.CombatState).PendingTurnStartChoice!;
        var options = CardChoiceSupport.BuildChoices(request.Spec!, names, 32, 32);
        foreach (var option in options)
        {
            var expected = Legacy(historyRoot, new([option]));
            var actual = Driver(historyRoot, [option]);
            if (actual.Path != "resume" || actual.Sim.ShuffleEventCount != historyRoot.ShuffleEventCount + 1)
                throw new InvalidOperationException("Upgraded dagger did not resume after a real shuffle.");
            if (!ReferenceEquals(historyRoot.History[0], actual.Sim.History[0]))
                throw new InvalidOperationException("Completed history prefix was not shared.");
            Equal(expected, actual.Sim, "prior history + shuffled RNG");
        }
        if (historyBefore != stamp(historyRoot)) throw new InvalidOperationException("Suspension mutated history parent.");

        foreach (string shape in new[] { "replay", "enchantment", "paired-hook" })
        {
            var basis = parent.Fork();
            if (shape == "replay") Card(basis).MutablePreview.BaseReplayCount++;
            if (shape == "enchantment") Card(basis).Enchant(ModelDb.Enchantment<Swift>().ToMutable(), 1m);
            if (shape == "paired-hook")
            {
                var combat = (SimulatedCombatState)basis.State.CombatState;
                combat.Apply<VigorPower>(player.Creature, 3, player.Creature);
                _ = combat.DrainPowerAmountChanges();
            }
            using var rejected = Capture(basis);
            if (rejected != null) throw new InvalidOperationException("Ineligible shape captured: " + shape);
            var reference = ResolveAll(basis);
            var actual = Driver(basis, reference.Plans);
            if (actual.Path != "ineligible-full-replay") throw new InvalidOperationException("Missing fallback: " + shape);
            Equal(reference.Sim, actual.Sim, "fallback " + shape);
        }

        var slyRoot = parent.Fork();
        Card(slyRoot, "PREPARED").MutablePreview.GiveSingleTurnSly();
        var slyPending = Legacy(slyRoot, new((IReadOnlyList<PlanCardChoice>?)null));
        var slyRequest = ((SimulatedCombatState)slyPending.State.CombatState).PendingTurnStartChoice!;
        PlanCardChoice outer = CardChoiceSupport.BuildRequestedChoice(slyRequest.Spec!, ["PREPARED"]);
        var incomplete = Driver(slyRoot, [outer]);
        if (incomplete.Path != "later-choice-full-replay" || !incomplete.Sim.HasPendingChoice)
            throw new InvalidOperationException("Nested Sly choice failed to fall back at the pending boundary.");
        Equal(Legacy(slyRoot, new([outer])), incomplete.Sim, "nested Sly pending fallback");
        var slyComplete = ResolveAll(slyRoot, outer);
        if (slyComplete.Plans.Count != 2) throw new InvalidOperationException("Expected two nested choice steps.");
        var completed = Driver(slyRoot, slyComplete.Plans);
        if (completed.Path != "ineligible-full-replay") throw new InvalidOperationException("Full nested chain did not use fallback.");
        Equal(slyComplete.Sim, completed.Sim, "nested Sly completed fallback");

        (CardContinuationContractCheckpoint disposed, WeakReference<CombatPredictionSimulator> seed) =
            MakeDisposedCardContinuationContract(parent, player, target);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        if (seed.TryGetTarget(out _)) throw new InvalidOperationException("Disposed checkpoint retained its seed.");
        try { disposed.Resume([options[0]], CancellationToken.None); throw new InvalidOperationException("Disposed checkpoint resumed."); }
        catch (ObjectDisposedException) { }
        GC.KeepAlive(disposed);
        if (parentBefore != stamp(parent)) throw new InvalidOperationException("Boundary tests mutated parent.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CardContinuationContractCheckpoint, WeakReference<CombatPredictionSimulator>) MakeDisposedCardContinuationContract(
        CombatPredictionSimulator parent, Player player, Creature target)
    {
        var seed = parent.Fork();
        var dagger = seed.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "DAGGER_THROW");
        var checkpoint = CaptureCardContinuationContract(seed, dagger, target, new ForkableSet<uint>())
            ?? throw new InvalidOperationException("Retention fixture was not eligible.");
        try { seed.Fork(); throw new InvalidOperationException("Ordinary fork accepted a suspended seed."); }
        catch (InvalidOperationException e) when (e.Message == "Suspended card requires its owning continuation fork.") { }
        checkpoint.Dispose();
        return (checkpoint, new(seed));
    }
}
