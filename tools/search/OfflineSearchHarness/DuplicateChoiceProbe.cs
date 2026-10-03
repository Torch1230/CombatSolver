using System.Text.Json;
using CombatSolver;
using HarmonyLib;
using System.Diagnostics;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;

namespace OfflineSearchHarness;

// Exploratory upper bound only: skip mode is not a proof of semantic equivalence.
internal static class DuplicateChoiceProbe
{
    private static string? _mode;
    private static int _duplicateCalls;
    private static int _nightmareCalls;
    private static int _repeatedOptions;

    internal static void Install(string output)
    {
        _mode = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_DUPLICATE_CHOICES");
        if (_mode is not ("measure" or "skip")) return;
        GameBootstrap.Harmony.Patch(
            AccessTools.Method(typeof(CardChoiceSupport), "ReserveIdentityOccurrenceRepresentatives"),
            prefix: new HarmonyMethod(typeof(DuplicateChoiceProbe), nameof(BeforeReserve)));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.WriteAllText(
            Path.Combine(output, "duplicate-choice-probe.json"), JsonSerializer.Serialize(new
            {
                mode = _mode, duplicateCalls = _duplicateCalls,
                nightmareCalls = _nightmareCalls, repeatedOptions = _repeatedOptions,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool BeforeReserve(CardChoiceSpec spec)
    {
        if (spec.Effect is not (PlanChoiceEffect.Duplicate or PlanChoiceEffect.Nightmare)) return true;
        if (spec.Effect == PlanChoiceEffect.Duplicate) Interlocked.Increment(ref _duplicateCalls);
        else Interlocked.Increment(ref _nightmareCalls);
        int repeats = spec.Options.Count - spec.Options.Select(CardChoiceSupport.ChoiceCardKey).Distinct().Count();
        Interlocked.Add(ref _repeatedOptions, repeats);
        return _mode != "skip";
    }

    internal static void RunBuilders(CombatState combat, string output)
    {
        if (_mode != "builders") return;
        var player = combat.Players.Single();
        var names = SolverDisplayNames.Capture(combat);
        List<object> results = [];
        foreach (bool unique in new[] { true, false })
        {
            var cards = Enumerable.Range(0, 10).Select(index =>
            {
                var card = PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), player);
                card.MutablePreview.BaseReplayCount = unique ? index : index % 3;
                return card;
            }).ToArray();
            CardChoiceSpec spec = new(PlanChoiceEffect.Transform, PileType.Hand, 0, 5, cards, cards, 0);
            for (int index = 0; index < 100; index++) CardChoiceSupport.BuildChoices(spec, names, 42, 54);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int count = 0;
            for (int index = 0; index < 2000; index++)
                count += CardChoiceSupport.BuildChoices(spec, names, 42, 54).Count;
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            results.Add(new { unique, iterations = 2000, choices = count, allocatedBytes = bytes, elapsedMs = ms });
        }
        File.WriteAllText(Path.Combine(output, "duplicate-builder-probe.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
