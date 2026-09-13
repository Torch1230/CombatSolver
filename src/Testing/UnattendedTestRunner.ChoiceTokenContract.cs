using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private delegate IReadOnlyList<PlanCardToken> TokenBuilder(IReadOnlyList<PredictedCard> selected,
        IReadOnlyList<PredictedCard> options, IReadOnlyList<PredictedCard> source, Func<CardModel, string> displayName);

    private static string AssertChoiceTokenContract(Player player)
    {
        TokenBuilder actual = typeof(CardChoiceSupport).GetMethod("ToTokens", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<TokenBuilder>();
        TokenBuilder original = OriginalChoiceTokens;
        Func<CardModel, string> display = card => card.Id.Entry;
        PredictedCard[] cards = Enumerable.Range(0, 16)
            .Select(i => PredictedCard.Create(i % 2 == 0 ? CanonicalModels.Card<StrikeIronclad>()
                : CanonicalModels.Card<DefendIronclad>(), player)).ToArray();
        for (int i = 0; i < cards.Length; i += 3) cards[i].Upgrade();
        var random = new Random(20260912);
        int comparisons = 0;
        for (int trial = 0; trial < 1024; trial++)
        {
            IReadOnlyList<PredictedCard> source = Enumerable.Range(0, random.Next(25))
                .Select(_ => cards[random.Next(cards.Length)]).ToList();
            IReadOnlyList<PredictedCard> options = trial % 3 == 0 ? source : trial % 3 == 1
                ? source.ToArray() : Enumerable.Range(0, random.Next(25))
                    .Select(_ => cards[random.Next(cards.Length)]).ToArray();
            PredictedCard[] selected = Enumerable.Range(0, random.Next(14))
                .Select(_ => cards[random.Next(cards.Length)]).ToArray();
            if (trial % 16 == 0) cards[trial / 16 % cards.Length].SetToFreeThisTurn();
            if (!original(selected, options, source, display).SequenceEqual(actual(selected, options, source, display)))
                throw new InvalidOperationException("Choice token state, occurrence, order or display changed.");
            comparisons++;
        }
        // Exercise aliased and copied options on the same native models. Numbers describe token building,
        // not a whole combat search; warm both delegates and record an interleaved sequence.
        var probes = new List<object>();
        foreach (bool shared in new[] { false, true })
        {
            IReadOnlyList<PredictedCard> options = shared ? cards : cards.ToArray();
            PredictedCard[] selected = cards.Where((_, i) => i % 2 == 0).ToArray();
            for (int warm = 0; warm < 1000; warm++)
            {
                original(selected, options, cards, display);
                actual(selected, options, cards, display);
            }
            foreach (bool candidate in new[] { false, true, true, false })
            {
                TokenBuilder build = candidate ? actual : original;
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                int count = 0;
                for (int iteration = 0; iteration < 10000; iteration++)
                    count += build(selected, options, cards, display).Count;
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                probes.Add(new { shared, candidate, iterations = 10000, count,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, elapsedMs = elapsed });
            }
        }
        return "ChoiceTokens:" + JsonSerializer.Serialize(new { comparisons, probes });
    }

    private static IReadOnlyList<PlanCardToken> OriginalChoiceTokens(
        IReadOnlyList<PredictedCard> selected,
        IReadOnlyList<PredictedCard> options,
        IReadOnlyList<PredictedCard> source,
        Func<CardModel, string> displayName)
    {
        List<PlanCardToken> tokens = [];
        foreach (PredictedCard card in selected)
        {
            string stateKey = CardChoiceSupport.ChoiceCardKey(card);
            int sourceOccurrence = source.TakeWhile(item => !ReferenceEquals(item, card))
                .Count(item => item.Preview.Id.Entry == card.Preview.Id.Entry && item.Preview.CurrentUpgradeLevel == card.Preview.CurrentUpgradeLevel);
            int optionOccurrence = options.TakeWhile(item => !ReferenceEquals(item, card))
                .Count(item => item.Preview.Id.Entry == card.Preview.Id.Entry && item.Preview.CurrentUpgradeLevel == card.Preview.CurrentUpgradeLevel);
            tokens.Add(new PlanCardToken(
                card.Preview.Id.Entry,
                card.Preview.CurrentUpgradeLevel,
                stateKey,
                sourceOccurrence,
                optionOccurrence,
                displayName(card.Preview)));
        }
        return tokens;
    }

}
