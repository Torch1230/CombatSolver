using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertChoiceCombinationContract(Player player, SolverDisplayNames names)
    {
        PredictedCard[] cards = Enumerable.Range(0, 20).Select(index =>
            PredictedCard.Create(index % 2 == 0 ? CanonicalModels.Card<StrikeIronclad>()
                : CanonicalModels.Card<DefendIronclad>(), player)).ToArray();
        for (int index = 0; index < cards.Length; index += 3) cards[index].Upgrade();
        for (int index = 1; index < cards.Length; index += 5) cards[index].SetToFreeThisTurn();
        cards[2].MutablePreview.AddKeyword(CardKeyword.Ethereal);
        cards[3].MutablePreview.AddKeyword(CardKeyword.Retain);
        cards[4].MutablePreview.AddKeyword(CardKeyword.Unplayable);
        cards[5].MutablePreview.GiveSingleTurnSly();
        PlanChoiceEffect[] effects = Enum.GetValues<PlanChoiceEffect>();
        Random random = new(20260913);
        int comparisons = 0, choiceCount = 0;
        for (int trial = 0; trial < 1200; trial++)
        {
            // Reuse model identities across calls with changing scalar/keyword inputs;
            // a score must never survive in a model, spec, or cross-call dictionary.
            if (trial % 53 == 0) cards[trial % cards.Length].MutablePreview.GiveSingleTurnSly();
            if (trial % 47 == 0) cards[(trial + 1) % cards.Length].SetToFreeThisTurn();
            int count = trial % 100 == 0 ? 129 : random.Next(13);
            PredictedCard[] options = Enumerable.Range(0, count)
                .Select(_ => cards[random.Next(cards.Length)]).ToArray();
            IReadOnlyList<PredictedCard> source = trial % 3 == 0 ? options
                : trial % 3 == 1 ? options.Reverse().ToArray() : cards;
            int maximum = count > 128 ? 1 : random.Next(Math.Min(count + 2, 6));
            int minimum = trial % 11 == 0 ? maximum + 1 : random.Next(maximum + 1);
            CardChoiceSpec spec = new(effects[trial % effects.Length],
                trial % 3 == 0 ? PileType.Hand : trial % 3 == 1 ? PileType.Draw : PileType.Discard,
                minimum, maximum, options, source, trial % 5 - 2,
                ContextId: "combination-contract", MaxBranches: trial % 7 == 0 ? trial % 5 : null,
                IsImplicitAllSelection: trial % 19 == 0);
            int limit = 1 + trial % 17;
            IReadOnlyList<PlanCardChoice> expected = CardChoiceSupport.BuildChoicesBaselineForTesting(spec, names, limit, limit);
            IReadOnlyList<PlanCardChoice> actual = CardChoiceSupport.BuildChoices(spec, names, limit, limit);
            if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                throw new InvalidOperationException($"Choice combinations changed at trial {trial}, effect {spec.Effect}.");
            comparisons++;
            choiceCount += actual.Count;
        }
        // Native model microprobe: repeated single/multiple-card and any-card selections.
        // This isolates enumeration allocation; search timing is measured separately.
        List<object> probes = [];
        foreach ((int minimum, int maximum) in new[] { (1, 1), (2, 2), (0, 5) })
        {
            CardChoiceSpec spec = new(PlanChoiceEffect.Transform, PileType.Hand,
                minimum, maximum, cards[..10], cards, 7);
            for (int warm = 0; warm < 100; warm++)
            {
                CardChoiceSupport.BuildChoicesBaselineForTesting(spec, names, 42, 54);
                CardChoiceSupport.BuildChoices(spec, names, 42, 54);
            }
            foreach (bool candidate in new[] { false, true, true, false })
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                int generated = 0;
                for (int iteration = 0; iteration < 300; iteration++)
                    generated += (candidate ? CardChoiceSupport.BuildChoices(spec, names, 42, 54)
                        : CardChoiceSupport.BuildChoicesBaselineForTesting(spec, names, 42, 54)).Count;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                probes.Add(new { minimum, maximum, candidate, iterations = 300, generated,
                    allocatedBytes = bytes, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
        }
        return "ChoiceCombinations:" + JsonSerializer.Serialize(new { comparisons, choiceCount, probes });
    }
}
