using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using HarmonyLib;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models.Cards;

namespace OfflineSearchHarness;

// Opportunity counts only: instrumented runs are not performance measurements.
internal static class SnapshotOpportunityProbe
{
    private const int MaximumKeysPerSolver = 100_000;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<CombatBeamSolver, Counts> Solvers = new();
    private static readonly List<Counts> Results = [];
    private sealed class Counts
    {
        public int Total;
        public int DuplicateStates;
        public int DuplicateEvaluationInputs;
        public int StateLimitBypasses;
        public int EvaluationLimitBypasses;
        public Dictionary<string, int> Boundaries = [];
        public HashSet<StateFingerprint> States = [];
        public HashSet<(StateFingerprint, int, SearchBoundaryReason)> Evaluations = [];
    }

    internal static void Install(string output)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_SNAPSHOT_PROBE") != "1") return;
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "Snapshot"),
            postfix: new HarmonyMethod(typeof(SnapshotOpportunityProbe), nameof(Observe)));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate) File.WriteAllText(Path.Combine(output, "snapshot-opportunities.json"),
                JsonSerializer.Serialize(Results.Select(c => new
                {
                    c.Total, c.DuplicateStates, c.DuplicateEvaluationInputs, c.Boundaries,
                    c.StateLimitBypasses, c.EvaluationLimitBypasses, maximumKeysPerSolver = MaximumKeysPerSolver,
                }), new JsonSerializerOptions { WriteIndented = true }));
        };
    }

    private static void Observe(CombatBeamSolver __instance, int actionCount, SimulationSnapshot __result)
    {
        lock (Gate)
        {
            Counts c = Solvers.GetValue(__instance, _ => { Counts result = new(); Results.Add(result); return result; });
            c.Total++;
            string boundary = __result.BoundaryReason.ToString();
            c.Boundaries[boundary] = c.Boundaries.GetValueOrDefault(boundary) + 1;
            if (c.States.Contains(__result.StateKey)) c.DuplicateStates++;
            else if (c.States.Count < MaximumKeysPerSolver) c.States.Add(__result.StateKey);
            else c.StateLimitBypasses++;
            var evaluation = (__result.StateKey, actionCount, __result.BoundaryReason);
            if (c.Evaluations.Contains(evaluation)) c.DuplicateEvaluationInputs++;
            else if (c.Evaluations.Count < MaximumKeysPerSolver) c.Evaluations.Add(evaluation);
            else c.EvaluationLimitBypasses++;
        }
    }

    internal static void RunShuffleWitness(CombatState combat, string output)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_SHUFFLE_WITNESS") != "1") return;
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        var player = combat.Players.Single();
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var first = PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), player);
        var second = PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), player);
        // Same native sorting identity, different persistent combat state.
        first.MutablePreview.BaseReplayCount = 1;
        second.MutablePreview.BaseReplayCount = 2;
        List<PredictedCard> forward = [first, second];
        List<PredictedCard> reversed = [second, first];
        var rng = simulator.Rng.ShuffleState;
        forward.StableShuffle(rng.ToRng());
        reversed.StableShuffle(rng.ToRng());
        string[] a = forward.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
        string[] b = reversed.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
        bool sameMultiset = a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal));
        bool sameSequence = a.SequenceEqual(b);
        if (first.CompareTo(second) != 0 || !sameMultiset || sameSequence)
            throw new InvalidOperationException("Expected equal-sort-key shuffle counterexample was not reproduced.");
        if (liveBefore != ContinuationStamp.CaptureLive(combat).StateText)
            throw new InvalidOperationException("Shuffle witness changed the live root.");
        File.WriteAllText(Path.Combine(output, "shuffle-order-witness.json"), JsonSerializer.Serialize(new
        {
            nativeCompare = first.CompareTo(second), sameMultiset, sameSequence,
            forward = a, reversed = b, rng.Counter, liveUnchanged = true,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
