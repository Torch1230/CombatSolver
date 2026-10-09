using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using HarmonyLib;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace OfflineSearchHarness;

// Opportunity counts only: instrumented runs are not performance measurements.
internal static class SnapshotOpportunityProbe
{
    private const int MaximumKeysPerSolver = 100_000;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<CombatBeamSolver, Counts> Solvers = new();
    private static readonly List<Counts> Results = [];
    private static readonly ConditionalWeakTable<CombatBeamSolver, Family> Families = new();
    private static readonly List<Family> FamilyResults = [];
    private static readonly ConditionalWeakTable<CombatBeamSolver, SolverIdentity> Identities = new();
    private static int _nextSolver;
    private sealed record SolverIdentity(int Id);
    private const int MaximumRequestInputs = 250_000;
    private static readonly ConditionalWeakTable<CombatRootSnapshot, SolverIdentity> RootIdentities = new();
    // Resolve the private field only when a family probe is enabled. A probe ABI
    // mismatch must not prevent ordinary, uninstrumented harness requests.
    private static class RootMetadata
    {
        internal static readonly System.Reflection.FieldInfo Field = typeof(CombatBeamSolver)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(CombatRootSnapshot));
    }
    private static int _nextRoot;
    private static bool _observeRequest;
    private static readonly RequestCounts Request = new();
    private sealed class RequestCounts
    {
        public long Total, SameMemberRepeats, CrossMemberRepeats, LimitBypasses,
            FeatureMismatches, HistoryOnlyMismatches;
        public readonly Dictionary<(int Root, StateFingerprint State, int Actions, SearchBoundaryReason Boundary),
            (int Member, FeatureSignature Features)> Inputs = [];
        public readonly List<object> Mismatches = [];
        public readonly List<object> NonHistoryMismatches = [];
    }
    private sealed class Family
    {
        public int Id, Root;
        public long Total, SameWorkerRepeats, CrossWorkerRepeats, LimitBypasses, FeatureMismatches;
        public int Workers;
        public readonly Dictionary<(StateFingerprint, int, SearchBoundaryReason),
            (int Solver, FeatureSignature Features)> Inputs = [];
        public readonly List<object> Mismatches = [];
    }
    // Selected detached values are a counterexample check, not a complete snapshot oracle.
    private readonly record struct FeatureSignature(
        long ScoreBits, int ProjectedHp, StateFingerprint ShuffleKey, int ShuffleValue,
        int PersistentBuff, int LatentSetup, int RetainedAttack, int ReachableHand,
        int FutureHeal, int GrowthCredit, int RelicCredit, int PotionCost, int HistoryCount,
        int Shuffles, bool Risk, int CumulativeHpLost, int RecoveredHp)
    {
        public static FeatureSignature Capture(SimulationSnapshot s) => new(
            BitConverter.DoubleToInt64Bits(s.Score), s.ProjectedPlayerHp,
            s.ProjectedShuffleOrderKey, s.ProjectedShuffleOrderValue,
            s.PersistentBuffValue, s.LatentSetupValue, s.RetainedAttackValue,
            s.ReachableHandValue, s.FutureHealPotential, s.GrowthHpCredit,
            s.RelicCounters.HpCredit, s.PotionStrategicCost, s.HistoryEntryCount,
            s.ShufflesCrossed, s.HasRisk, s.CumulativePlayerHpLost, s.RecoveredPlayerHp);
    }
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
        string? mode = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_SNAPSHOT_PROBE");
        if (mode == "pile-lookups")
        {
            PileLookupProbe.Install(output);
            return;
        }
        if (mode is "worker-families" or "request-families")
        {
            _observeRequest = mode == "request-families";
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "CreateExpansionWorker"),
                postfix: new HarmonyMethod(typeof(SnapshotOpportunityProbe), nameof(ObserveWorker)));
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "Snapshot"),
                postfix: new HarmonyMethod(typeof(SnapshotOpportunityProbe), nameof(ObserveFamily)));
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (Gate) File.WriteAllText(Path.Combine(output, _observeRequest
                    ? "snapshot-request-families.json" : "snapshot-worker-families.json"),
                    JsonSerializer.Serialize(new
                    {
                        observationOnly = true, maximumKeysPerFamily = MaximumKeysPerSolver,
                        scope = "Worker families follow actual CreateExpansionWorker ownership. Request mode additionally partitions members by exact frozen root identity. Keys and selected feature equality are not a cache-safety proof. No snapshot/model graphs retained.",
                        request = !_observeRequest ? null : new
                        {
                            maximumInputs = MaximumRequestInputs, roots = _nextRoot,
                            Request.Total, retainedInputs = Request.Inputs.Count,
                            Request.SameMemberRepeats, Request.CrossMemberRepeats,
                            Request.LimitBypasses, Request.FeatureMismatches,
                            Request.HistoryOnlyMismatches, Request.Mismatches, Request.NonHistoryMismatches,
                        },
                        families = FamilyResults.Select(f => new
                        {
                            f.Id, f.Root,
                            f.Total, f.Workers, retainedInputs = f.Inputs.Count, f.SameWorkerRepeats,
                            f.CrossWorkerRepeats, f.LimitBypasses, f.FeatureMismatches, f.Mismatches,
                        }),
                    }, new JsonSerializerOptions { WriteIndented = true }));
            };
            return;
        }
        if (mode != "1") return;
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

    private static Family FamilyFor(CombatBeamSolver solver) => Families.GetValue(solver, _ =>
    {
        CombatRootSnapshot root = (CombatRootSnapshot)(RootMetadata.Field.GetValue(solver)
            ?? throw new InvalidOperationException("Snapshot probe requires the captured root."));
        Family family = new()
        {
            Id = FamilyResults.Count + 1,
            Root = RootIdentities.GetValue(root, _ => new(++_nextRoot)).Id,
        };
        FamilyResults.Add(family);
        return family;
    });

    private static void ObserveWorker(CombatBeamSolver __instance, CombatBeamSolver __result)
    {
        lock (Gate)
        {
            Family family = FamilyFor(__instance);
            Families.Add(__result, family);
            family.Workers++;
        }
    }

    private static void ObserveFamily(CombatBeamSolver __instance, int actionCount, SimulationSnapshot __result)
    {
        lock (Gate)
        {
            Family family = FamilyFor(__instance);
            int solver = Identities.GetValue(__instance, _ => new(++_nextSolver)).Id;
            family.Total++;
            var key = (__result.StateKey, actionCount, __result.BoundaryReason);
            FeatureSignature features = FeatureSignature.Capture(__result);
            if (_observeRequest)
                ObserveRequest(family, key, features);
            if (family.Inputs.TryGetValue(key, out var prior))
            {
                if (prior.Solver == solver) family.SameWorkerRepeats++;
                else family.CrossWorkerRepeats++;
                if (prior.Features != features)
                {
                    family.FeatureMismatches++;
                    if (family.Mismatches.Count < 12)
                        family.Mismatches.Add(new { key.Item2, key.Item3, first = prior.Features, current = features });
                }
            }
            else if (family.Inputs.Count < MaximumKeysPerSolver)
                family.Inputs.Add(key, (solver, features));
            else family.LimitBypasses++;
        }
    }

    private static void ObserveRequest(Family family,
        (StateFingerprint State, int Actions, SearchBoundaryReason Boundary) input,
        FeatureSignature features)
    {
        Request.Total++;
        var key = (family.Root, input.State, input.Actions, input.Boundary);
        if (Request.Inputs.TryGetValue(key, out var prior))
        {
            if (prior.Member == family.Id) Request.SameMemberRepeats++;
            else Request.CrossMemberRepeats++;
            if (prior.Features != features)
            {
                Request.FeatureMismatches++;
                bool historyOnly = prior.Features with { HistoryCount = 0 }
                    == features with { HistoryCount = 0 };
                if (historyOnly) Request.HistoryOnlyMismatches++;
                var mismatch = new { root = key.Root, state = key.State,
                    actions = key.Actions, boundary = key.Boundary,
                    firstMember = prior.Member, currentMember = family.Id,
                    first = prior.Features, current = features, historyOnly };
                if (Request.Mismatches.Count < 12)
                    Request.Mismatches.Add(mismatch);
                if (!historyOnly && Request.NonHistoryMismatches.Count < 12)
                    Request.NonHistoryMismatches.Add(mismatch);
            }
        }
        else if (Request.Inputs.Count < MaximumRequestInputs)
            Request.Inputs.Add(key, (family.Id, features));
        else Request.LimitBypasses++;
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
        PileLookupProbe.RunWitness(combat);
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

    // Tags identify the helper that returned a model, not a membership certificate.
    // Original lookup always executes. No Player, pile, simulator, or Model is
    // retained by the result rows; model tags have weak keys and scalar values.
    private static class PileLookupProbe
    {
        private enum Origin { Unmarked, CloneHelper, GeneratedHelper }
        private sealed record Tag(Origin Origin);
        private sealed class Counts
        {
            public long Queries, Present, Absent;
            public readonly List<string> FirstPaths = [];
        }
        private sealed class State
        {
            public readonly ConditionalWeakTable<CardModel, Tag> Tags = new();
            public readonly Dictionary<(string Type, Origin Origin, string Phase), Counts> Rows = [];
            public object? Witness;
        }
        private static State? _state;
        [ThreadStatic] private static int _snapshotDepth;
        [ThreadStatic] private static bool _witness;

        internal static void Install(string output)
        {
            _state = new();
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(PredictionUtils), nameof(PredictionUtils.CloneCardStateForSimulation)),
                postfix: new HarmonyMethod(typeof(PileLookupProbe), nameof(ObserveClone)));
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(PredictionUtils), nameof(PredictionUtils.CreateCard)),
                postfix: new HarmonyMethod(typeof(PileLookupProbe), nameof(ObserveGenerated)));
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(SimulationCardPileLookupFastPath), nameof(SimulationCardPileLookupFastPath.Find)),
                postfix: new HarmonyMethod(typeof(PileLookupProbe), nameof(ObserveLookup)));
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "Snapshot"),
                prefix: new HarmonyMethod(typeof(PileLookupProbe), nameof(EnterSnapshot)),
                finalizer: new HarmonyMethod(typeof(PileLookupProbe), nameof(LeaveSnapshot)));
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (Gate) File.WriteAllText(Path.Combine(output, "native-pile-lookup-opportunities.json"),
                    JsonSerializer.Serialize(new
                    {
                        observationOnly = true,
                        scope = "Helper tags do not prove absence. Snapshot phase is inclusive; remaining queries are outside Snapshot. Witness queries are separate. No cache or original lookup suppression.",
                        witness = _state.Witness,
                        rows = _state.Rows.OrderBy(r => r.Key.Phase).ThenBy(r => r.Key.Origin)
                            .ThenBy(r => r.Key.Type, StringComparer.Ordinal).Select(r => new
                            {
                                r.Key.Type, origin = r.Key.Origin.ToString(), r.Key.Phase,
                                r.Value.Queries, r.Value.Present, r.Value.Absent, r.Value.FirstPaths,
                            }),
                    }, new JsonSerializerOptions { WriteIndented = true }));
            };
        }

        private static void ObserveClone(CardModel __result)
            => _state!.Tags.GetValue(__result, static _ => new(Origin.CloneHelper));
        private static void ObserveGenerated(CardModel __result)
            => _state!.Tags.GetValue(__result, static _ => new(Origin.GeneratedHelper));
        private static void EnterSnapshot() => _snapshotDepth++;
        private static void LeaveSnapshot() => _snapshotDepth--;

        private static void ObserveLookup(CardModel card, CardPile? __result)
        {
            State state = _state!;
            Origin origin = state.Tags.TryGetValue(card, out Tag? tag) ? tag.Origin : Origin.Unmarked;
            string phase = _witness ? "Witness" : _snapshotDepth > 0 ? "SnapshotInclusive" : "OutsideSnapshot";
            lock (Gate)
            {
                var key = (card.GetType().FullName!, origin, phase);
                if (!state.Rows.TryGetValue(key, out Counts? counts)) state.Rows.Add(key, counts = new());
                counts.Queries++;
                if (__result is null) counts.Absent++; else counts.Present++;
                if (counts.FirstPaths.Count < 1)
                    counts.FirstPaths.Add(string.Join("\n", new System.Diagnostics.StackTrace().GetFrames()
                        .Select(f => f.GetMethod()).Where(m => m is not null)
                        .Select(m => m!.DeclaringType?.FullName + "." + m.Name)));
            }
        }

        internal static void RunWitness(CombatState combat)
        {
            if (_state is null) return;
            string before = ContinuationStamp.CaptureLive(combat).StateText;
            var player = combat.Players.Single();
            CardPile hand = player.PlayerCombatState!.Hand;
            CardModel original = player.PlayerCombatState.AllPiles.SelectMany(p => p.Cards).First();
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            _witness = true;
            try
            {
                CardModel clone = PredictionUtils.CloneCardStateForSimulation(original);
                bool absentBefore = clone.Pile is null;
                hand.AddInternal(clone, silent: true);
                bool presentAfterAdd;
                try { presentAfterAdd = ReferenceEquals(clone.Pile, hand); }
                finally { hand.RemoveInternal(clone, silent: true); }
                bool absentAfterRemove = clone.Pile is null;
                bool liveRestored = ContinuationStamp.CaptureLive(combat).StateText == before;
                if (!absentBefore || !presentAfterAdd || !absentAfterRemove || !liveRestored)
                    throw new InvalidOperationException("Native pile insertion/removal witness failed or did not restore live state.");
                _state.Witness = new { absentBefore, presentAfterAdd, absentAfterRemove, liveRestored,
                    scope = "Offline actual game AddInternal/RemoveInternal under notification isolation; not full native/simulated combat proof." };
            }
            finally { _witness = false; }
        }
    }
}
