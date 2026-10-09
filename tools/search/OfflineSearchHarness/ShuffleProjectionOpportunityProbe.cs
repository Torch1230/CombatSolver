using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace OfflineSearchHarness;

// Passive dependency-repeat counts. The original projection always executes;
// diagnostic wall time and memory must never enter performance acceptance.
internal static class ShuffleProjectionOpportunityProbe
{
    private const int MaximumInputsPerSolver = 4096;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<CombatBeamSolver, Counts> Solvers = new();
    private static readonly List<Counts> Results = [];
    private sealed class Counts
    {
        public int Calls, Repeats, RejectedSources, LimitBypasses, RetainedInputs, OutputMismatches;
        public long Cards, RepeatedCards;
        public Dictionary<StateFingerprint, List<Observation>> Inputs = [];
        public int SortingInputRepeats, SortingRetainedInputs, SortingLimitBypasses;
        public long SortingRepeatedCards;
        public Dictionary<StateFingerprint, List<Input>> SortingInputs = [];
    }

    private readonly record struct CardInput(Type Type, string Category, string Entry, int Upgrade,
        StateFingerprint Fingerprint, long ValueBits);
    private sealed record Input(StateFingerprint Hash, StateFingerprint SortingHash,
        PredictionRngState Rng, CardInput[] Cards);
    private sealed record Observation(Input Input, (StateFingerprint Key, int Value) Output);
    private static readonly ConditionalWeakTable<Type, NativeSort> NativeSortTypes = new();
    private sealed record NativeSort(bool AuditedShape);

    internal static void Install(string output)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_SHUFFLE_PROBE") != "1") return;
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "BuildProjectedShuffleOrder"),
            prefix: new HarmonyMethod(typeof(ShuffleProjectionOpportunityProbe), nameof(Capture)),
            postfix: new HarmonyMethod(typeof(ShuffleProjectionOpportunityProbe), nameof(Observe)));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate) File.WriteAllText(Path.Combine(output, "shuffle-projection-opportunities.json"),
                JsonSerializer.Serialize(Results.Select(c => new
                {
                    c.Calls, c.Repeats, c.Cards, c.RepeatedCards, c.RejectedSources,
                    c.LimitBypasses, c.RetainedInputs, c.OutputMismatches,
                    c.SortingInputRepeats, c.SortingRepeatedCards, c.SortingRetainedInputs,
                    c.SortingLimitBypasses,
                    maximumInputsPerSolver = MaximumInputsPerSolver,
                    scope = "Per-solver exact equality of recorded ordered inputs; card fingerprints are existing 128-bit values. Not a cache-safety proof or performance measurement.",
                    sortingScope = "Only ordered runtime type, ordinal ModelId and upgrade inputs; excludes card state, value and RNG. Counts sorting-permutation opportunity, not full projection reuse.",
                }), new JsonSerializerOptions { WriteIndented = true }));
        };
    }

    private static void Capture(CombatPredictionSimulator simulator, List<PredictedCard> cards,
        out Input? __state)
    {
        __state = null;
        var values = new CardInput[cards.Count];
        PredictionRngState rng = simulator.Rng.ShuffleState;
        StateFingerprintBuilder hash = new();
        StateFingerprintBuilder sortingHash = new();
        sortingHash.Add(cards.Count);
        hash.Add(rng.Counter); hash.Add(rng.State0); hash.Add(rng.State1);
        hash.Add(rng.State2); hash.Add(rng.State3); hash.Add(cards.Count);
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            CardModel preview = card.Preview;
            Type type = preview.GetType();
            if (!NativeSortTypes.GetValue(type, static value => new NativeSort(
                    value.Assembly == typeof(CardModel).Assembly
                    && value.GetMethod(nameof(CardModel.CompareTo), [typeof(AbstractModel)])
                        ?.DeclaringType == typeof(CardModel))).AuditedShape)
                return;
            StateFingerprint fingerprint = card.TryGetCachedFingerprint(out ulong first, out ulong second)
                ? new(first, second) : CombatBeamSolver.CaptureCardStateFingerprintForTesting(card);
            long valueBits = BitConverter.DoubleToInt64Bits(CardChoiceSupport.CardValue(preview));
            values[index] = new(type, preview.Id.Category, preview.Id.Entry,
                preview.CurrentUpgradeLevel, fingerprint, valueBits);
            hash.Add(RuntimeHelpers.GetHashCode(type)); hash.Add(preview.Id.Category);
            hash.Add(preview.Id.Entry); hash.Add(preview.CurrentUpgradeLevel);
            hash.Add(fingerprint.First); hash.Add(fingerprint.Second); hash.Add(valueBits);
            sortingHash.Add(RuntimeHelpers.GetHashCode(type)); sortingHash.Add(preview.Id.Category);
            sortingHash.Add(preview.Id.Entry); sortingHash.Add(preview.CurrentUpgradeLevel);
        }
        __state = new(hash.Finish(), sortingHash.Finish(), rng, values);
    }

    private static void Observe(CombatBeamSolver __instance, List<PredictedCard> cards,
        Input? __state, (StateFingerprint Key, int Value) __result)
    {
        lock (Gate)
        {
            Counts c = Solvers.GetValue(__instance, _ =>
                { Counts result = new(); Results.Add(result); return result; });
            c.Calls++; c.Cards += cards.Count;
            if (__state is null) { c.RejectedSources++; return; }
            ObserveSortingInputs(c, __state);
            if (c.Inputs.TryGetValue(__state.Hash, out List<Observation>? bucket))
            {
                foreach (Observation prior in bucket)
                {
                    if (prior.Input.Rng != __state.Rng
                        || !prior.Input.Cards.AsSpan().SequenceEqual(__state.Cards)) continue;
                    c.Repeats++; c.RepeatedCards += cards.Count;
                    if (prior.Output != __result) c.OutputMismatches++;
                    return;
                }
            }
            if (c.RetainedInputs == MaximumInputsPerSolver) { c.LimitBypasses++; return; }
            if (bucket is null) c.Inputs.Add(__state.Hash, bucket = []);
            bucket.Add(new(__state, __result)); c.RetainedInputs++;
        }
    }

    private static void ObserveSortingInputs(Counts c, Input current)
    {
        if (c.SortingInputs.TryGetValue(current.SortingHash, out List<Input>? bucket))
        {
            foreach (Input prior in bucket)
            {
                if (prior.Cards.Length != current.Cards.Length) continue;
                bool same = true;
                for (int index = 0; index < current.Cards.Length; index++)
                {
                    CardInput left = prior.Cards[index], right = current.Cards[index];
                    if (left.Type == right.Type && left.Category == right.Category
                        && left.Entry == right.Entry && left.Upgrade == right.Upgrade) continue;
                    same = false; break;
                }
                if (!same) continue;
                c.SortingInputRepeats++; c.SortingRepeatedCards += current.Cards.Length;
                return;
            }
        }
        if (c.SortingRetainedInputs == MaximumInputsPerSolver)
            { c.SortingLimitBypasses++; return; }
        if (bucket is null) c.SortingInputs.Add(current.SortingHash, bucket = []);
        bucket.Add(current); c.SortingRetainedInputs++;
    }
}
