using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using HarmonyLib;

namespace OfflineSearchHarness;

// Observation only. State-key matches and sampled action permutations are not proofs
// of independence. Do not use probe runs for timing or allocation comparisons.
internal static class EquivalenceProbe
{
    private const int MaximumPairsPerSolver = 20_000;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<CombatBeamSolver, Observations> BySolver = new();
    private static readonly List<Observations> Results = [];

    private readonly record struct ActionKey(string Card, string State, uint? Target, int Replay);
    private readonly record struct PairKey(StateFingerprint Root, int Turn, ActionKey First, ActionKey Second);
    private readonly record struct Label(int Potions, int PotionCost, int SoldHp, int HpLost, int Actions, double Score);
    private sealed record Pair(bool Forward, StateFingerprint State, Label Cost)
    {
        public bool Compared;
    }
    private sealed class Observations
    {
        public long CandidateBuilds;
        public long AdmissionChecks;
        public long AdmissionRejected;
        public long CardAdmissionRejected;
        public long PairLimitBypasses;
        public int SwappedPairs;
        public int EqualStatePairs;
        public int EqualStateAndLabelPairs;
        public readonly Dictionary<PairKey, Pair> Pairs = [];
        public readonly List<object> Examples = [];
    }

    internal static void Install(string output)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_EQUIVALENCE_PROBE") != "1")
            return;
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "BuildCandidate"),
            prefix: new HarmonyMethod(typeof(EquivalenceProbe), nameof(BeforeBuild)));
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "TryAcceptTransposition"),
            postfix: new HarmonyMethod(typeof(EquivalenceProbe), nameof(AfterAdmission)));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate)
                File.WriteAllText(Path.Combine(output, "equivalence-probe.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    observationOnly = true,
                    maximumPairsPerSolver = MaximumPairsPerSolver,
                    solvers = Results.Select(s => new
                    {
                        s.CandidateBuilds, s.AdmissionChecks, s.AdmissionRejected, s.CardAdmissionRejected,
                        retainedPairKeys = s.Pairs.Count, s.PairLimitBypasses, s.SwappedPairs,
                        s.EqualStatePairs, s.EqualStateAndLabelPairs, s.Examples,
                    }).ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true }));
        };
    }

    private static Observations Get(CombatBeamSolver solver) => BySolver.GetValue(solver, _ =>
    {
        Observations result = new();
        Results.Add(result); // Only detached scalars/keys, never nodes, snapshots or models.
        return result;
    });

    private static void BeforeBuild(CombatBeamSolver __instance)
    {
        lock (Gate) Get(__instance).CandidateBuilds++;
    }

    private static void AfterAdmission(CombatBeamSolver __instance, SearchNode candidate, bool __result)
    {
        lock (Gate)
        {
            Observations s = Get(__instance);
            s.AdmissionChecks++;
            if (!__result)
            {
                s.AdmissionRejected++;
                if (candidate.Action?.Kind == PlanActionKind.PlayCard) s.CardAdmissionRejected++;
            }
            if (candidate.Parent is not { Parent: { } root } parent
                || candidate.Turn != parent.Turn || parent.Turn != root.Turn
                || !TryAction(parent.Action, out ActionKey a)
                || !TryAction(candidate.Action, out ActionKey b) || a == b)
                return;
            // Ordinal comparison provides a stable pair index. Occurrence positions are
            // deliberately omitted for this opportunity census, never for actual pruning.
            bool forward = string.CompareOrdinal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b)) < 0;
            PairKey key = new(root.StateKey, root.Turn, forward ? a : b, forward ? b : a);
            Label label = new(candidate.PotionCount, candidate.PotionStrategicCost, candidate.FutureSoldHp,
                candidate.Snapshot.CumulativePlayerHpLost, candidate.ActionCount, candidate.Score);
            if (!s.Pairs.TryGetValue(key, out Pair? prior))
            {
                if (s.Pairs.Count >= MaximumPairsPerSolver) { s.PairLimitBypasses++; return; }
                s.Pairs.Add(key, new Pair(forward, candidate.StateKey, label));
                return;
            }
            if (prior.Forward == forward || prior.Compared) return;
            prior.Compared = true;
            s.SwappedPairs++;
            bool sameState = prior.State == candidate.StateKey;
            bool sameLabel = prior.Cost == label;
            if (sameState) s.EqualStatePairs++;
            if (sameState && sameLabel) s.EqualStateAndLabelPairs++;
            if (s.Examples.Count < 24)
                s.Examples.Add(new { first = a.Card, second = b.Card, a.Target, otherTarget = b.Target,
                    sameState, sameLabel });
        }
    }

    private static bool TryAction(PlanAction? action, out ActionKey key)
    {
        key = default;
        if (action is not { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false }
            || action.Choice != null || action.NestedChoices is { Count: > 0 }
            || action.TurnStartChoices is { Count: > 0 } || action.RelicEffects is { Count: > 0 })
            return false;
        key = new(action.CardId, action.CardStateKey, action.TargetCombatId, action.ReplayCount);
        return true;
    }
}
