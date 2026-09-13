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
        void Check(CombatPredictionHistory current)
        {
            CombatPredictionHistoryEntry[] before = current.Entries.ToArray();
            if (!ReferenceEquals(current.OfType<CombatPredictionCardGenerationOptionsEntry>().LastOrDefault(),
                    current.FindLatestCardGenerationOptions()))
                throw new InvalidOperationException("Unfiltered latest generation lookup changed publication identity.");
            foreach (PredictedCard card in cards)
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
    }
}
