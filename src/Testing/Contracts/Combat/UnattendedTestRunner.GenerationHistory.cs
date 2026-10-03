using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertGenerationHistoryContract(Player player)
    {
        PredictedCard[] cards = Enumerable.Range(0, 8)
            .Select(_ => PredictedCard.Create(CanonicalModels.Card<DefendIronclad>(), player)).ToArray();
        foreach (PredictedCard card in cards) _ = card.MutablePreview;
        var trace = new PredictionTrace();
        var history = new CombatPredictionHistory(trace);
        var retained = new List<(CombatPredictionHistory History, PredictionTrace Trace)> { (history, trace) };
        var random = new Random(20260912);
        void Check(CombatPredictionHistory current, params PredictedCard[] extra)
        {
            CombatPredictionHistoryEntry[] before = current.Entries.ToArray();
            if (!ReferenceEquals(current.OfType<CombatPredictionCardGenerationOptionsEntry>().LastOrDefault(),
                    current.FindLatestCardGenerationOptions()))
                throw new InvalidOperationException("Unfiltered latest generation lookup changed publication identity.");
            foreach (PredictedCard card in cards.Concat(extra))
            {
                CombatPredictionCardGenerationOptionsEntry? expected = current
                    .OfType<CombatPredictionCardGenerationOptionsEntry>()
                    .LastOrDefault(entry => card.References(entry.Trace?.Source));
                if (!ReferenceEquals(expected, current.FindLatestCardGenerationOptions(card)))
                    throw new InvalidOperationException("Latest generation lookup changed publication identity.");
            }
            if (!before.SequenceEqual(current.Entries, ReferenceEqualityComparer.Instance))
                throw new InvalidOperationException("Latest generation lookup mutated history.");
        }
        Check(history);
        history.CardGenerationOptions([]); // Unfiltered query matches; card-filtered queries must miss.
        Check(history);
        for (int step = 0; step < 512; step++)
        {
            if (step % 4 == 0)
            {
                var childTrace = new PredictionTrace();
                history = history.Fork(childTrace);
                trace = childTrace;
                retained.Add((history, trace));
            }
            // Repeated and nested publications, both original and mutable preview sources.
            PredictedCard source = cards[random.Next(cards.Length - 1)]; // Last card always misses.
            using (trace.Push(step % 2 == 0 ? source.Original : source.Preview,
                PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
            {
                history.CardGenerationOptions(step % 3 == 0 ? [] : [cards[step % cards.Length]]);
                using (trace.Push(cards[0].Original, PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
                    history.CardGenerationOptions([cards[1]]);
            }
            for (int noise = 0; noise < step % 17; noise++) history.CardCostsRandomized([]);
            Check(history);
            var parent = retained[random.Next(retained.Count)];
            // Appending to an ancestor after fork must not change its descendants.
            using (parent.Trace.Push(cards[2].Original, PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
                parent.History.CardGenerationOptions([cards[3]]);
            Check(parent.History);
            Check(history);
        }
        foreach (var branch in retained) Check(branch.History);

        // Continuations copy their mutable suffix instead of sealing it. A query must
        // return the copied event with its copied trace/options, never a parent-tail event.
        var continuationTrace = new PredictionTrace();
        var continuation = new CombatPredictionHistory(continuationTrace);
        using (continuationTrace.Push(cards[0].Original,
            PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
            continuation.CardGenerationOptions([cards[2]]);
        int start = continuation.PrepareManualCardChoice();
        using (continuationTrace.Push(cards[1].Original,
            PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
        {
            continuation.CardGenerationOptions([cards[3]]);
            continuation.CardCostsRandomized([]);
            using (continuationTrace.Push(cards[1].Preview,
                PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
                continuation.CardGenerationOptions([cards[4]]);
        }
        var manualTrace = new PredictionTrace();
        using var manualContext = new PredictionForkContext();
        var manual = continuation.ForkManualCardChoice(manualTrace, manualContext, start);
        Check(manual);
        if (ReferenceEquals(manual.FindLatestCardGenerationOptions(), continuation.FindLatestCardGenerationOptions()))
            throw new InvalidOperationException("Manual continuation query returned the parent's copied suffix.");

        var executionTrace = new PredictionTrace();
        using var executionContext = new PredictionForkContext();
        var execution = continuation.ForkExecutionContinuation(executionTrace, executionContext, start);
        Check(execution);
        var copiedGeneration = execution.FindLatestCardGenerationOptions(cards[1])!;
        var parentGeneration = continuation.FindLatestCardGenerationOptions(cards[1])!;
        if (ReferenceEquals(copiedGeneration, parentGeneration)
            || ReferenceEquals(copiedGeneration.Trace, parentGeneration.Trace)
            || !ReferenceEquals(copiedGeneration.Trace!.Source, parentGeneration.Trace!.Source)
            || !ReferenceEquals(copiedGeneration.Options[0], executionContext.RequireRemap(cards[4])))
            throw new InvalidOperationException("Execution continuation query lost copied trace/options or stable source identity.");
        var descendant = execution.Fork(new PredictionTrace());
        using (continuationTrace.Push(cards[1].Original,
            PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
            continuation.CardGenerationOptions([]);
        using (executionTrace.Push(cards[1].Preview,
            PredictionInvocation.ForAction(PredictionActionKind.CardPlay)))
            execution.CardGenerationOptions([]);
        Check(continuation);
        Check(manual);
        Check(execution);
        Check(descendant);
    }
}
