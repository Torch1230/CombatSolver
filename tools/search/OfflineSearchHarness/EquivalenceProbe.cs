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
        ReplayRequests.Install(output);
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

    // Repeated input/output observations remain separate from swap observations.
    // Selected feature equality never licenses transition or snapshot reuse.
    private static class ReplayRequests
    {
        private const int MaximumInputs = 250_000;
        private static readonly object ReplayGate = new();
        private static readonly ConditionalWeakTable<CombatBeamSolver, Member> Members = new();
        private static readonly ConditionalWeakTable<CombatRootSnapshot, RootIdentity> Roots = new();
        private static readonly List<Member> MemberResults = [];
        private static readonly Dictionary<Input, Entry> Inputs = [];
        private static readonly List<object> Examples = [];
        private static readonly Dictionary<int, long> Modes = [];
        private static readonly Dictionary<PrefixInput, Entry> PrefixInputs = [];
        private static readonly ConditionalWeakTable<object, RootIdentity> HistoryIdentities = new();
        private static bool ObservePrefix;
        private static bool ObserveRoute;
        private static readonly ConditionalWeakTable<SearchNode, RouteIdentity> NodeRoutes = new();
        private static readonly Dictionary<RouteEdge, RouteIdentity> RouteEdges = [];
        private static readonly Dictionary<RouteInput, Entry> RouteInputs = [];
        private static readonly List<object> RouteExamples = [];
        private static int NextRoute;
        private static long RouteRepeats, RouteLimitBypasses, RouteOpaqueOrigins,
            RouteParentStateMismatches, RouteResultStateMismatches, RouteResultFeatureMismatches,
            RouteLabelMismatches, RouteDifferentProfileMatches;
        private static int NextHistoryIdentity;
        private static long PrefixRepeats, PrefixLimitBypasses, PrefixSelectedMatches,
            PrefixBeforeMismatches, PrefixAfterMismatches, PrefixDifferentProfileMatches;
        private static int NextMember, NextRoot;
        private static long Started, Completed, Repeats, SameMember, CrossMember, LimitBypasses,
            BeforeMismatches, AfterStateMismatches, AfterFeatureMismatches, HistoryOnlyMismatches,
            SelectedMatches, SameProfileSelectedMatches, DifferentProfileSelectedMatches;
        private sealed record RootIdentity(int Id);
        private sealed class Member
        {
            internal int Id, Root, Workers;
            internal string Profile = "", ReplayPolicy = "";
        }
        private static class Metadata
        {
            internal static readonly System.Reflection.FieldInfo Root = typeof(CombatBeamSolver)
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(CombatRootSnapshot));
            internal static readonly System.Reflection.FieldInfo Profile =
                AccessTools.Field(typeof(CombatBeamSolver), "_profile");
            internal static readonly System.Reflection.FieldInfo ForkGate =
                AccessTools.Field(typeof(CombatBeamSolver), "_parallelActionReplayForkGate");
        }

        private readonly record struct Input(int Root, StateFingerprint State, int Actions,
            SearchBoundaryReason Boundary, string Action, int CaptureMode);
        private readonly record struct Features(long ScoreBits, int HpLost, int RecoveredHp,
            int ProjectedHp, int PotionUses, int AutomaticPotions, int PotionCost,
            int GrowthCredit, int RelicCredit, int FutureHeal, int LongTermValue,
            int Shuffles, bool Risk, bool Dead, bool Won, SearchBoundaryReason Boundary)
        {
            internal static Features Capture(SimulationSnapshot s) => new(
                BitConverter.DoubleToInt64Bits(s.Score), s.CumulativePlayerHpLost,
                s.RecoveredPlayerHp, s.ProjectedPlayerHp, s.PotionUseCount,
                s.AutomaticPotionUseCount, s.PotionStrategicCost, s.GrowthHpCredit,
                s.RelicCounters.HpCredit, s.FutureHealPotential,
                s.LongTermResourceValue, s.ShufflesCrossed, s.HasRisk, s.PlayerDead,
                s.AllEnemiesDead, s.BoundaryReason);
        }
        private readonly record struct PathLabel(int Potions, int Cost, int SoldHp, long ScoreBits,
            SearchRouteTraits Traits, bool HasNonPotionAction);
        // Head identity preserves the entire sealed segment chain and its completion maps.
        // Tail entry order and the mutable completion-map identity are read without sealing
        // or modifying history. Different segment layouts deliberately remain different.
        private readonly record struct PrefixStamp(int Head, string Tail, int CompletionMap, int Pending);
        private readonly record struct PrefixInput(Input Input, PrefixStamp Prefix);
        private sealed record Observation(Input Input, int Member, string Profile,
            Features Before, PathLabel Label, int BeforeHistory, PrefixStamp Prefix);
        private sealed record Entry(Observation First, StateFingerprint AfterState,
            Features After, int AfterHistory);
        private sealed record RouteIdentity(int Id, int Actions);
        private readonly record struct RouteEdge(int Parent, string Action);
        private readonly record struct RouteInput(int Root, int Route, string Policy, string Action, int Mode);

        private static class RouteMetadata
        {
            internal static readonly string[] Names = ["_potionPolicy", "_potionStrategy",
                "_forceAllPotionsDisabled", "_enforcePotionDirectives", "_maximumPotionUses",
                "_minimumPotionUses", "_earliestPotionTurn", "_includeTurnSetup", "_theftPolicy",
                "_growthBudgets", "_relicTargets", "_ignoreLongTermRewards", "_strategicBossHpRelief"];
            internal static readonly System.Reflection.FieldInfo[] Fields = Names.Select(name =>
                AccessTools.Field(typeof(CombatBeamSolver), name)
                ?? throw new InvalidOperationException("Route probe field missing: " + name)).ToArray();
        }

        // A detached prefix trie compares complete ordered PlanActions, without forcing
        // SearchNode.Actions materialization or retaining nodes/simulators. Origin state
        // and setup choices partition the census; they are not a complete origin proof.
        // Called under ReplayGate. Opaque/truncated origins and capacity refuse counting.
        private static RouteIdentity RouteFor(SearchNode parent)
        {
            if (NodeRoutes.TryGetValue(parent, out RouteIdentity? cached)) return cached;
            Stack<SearchNode> pending = new();
            SearchNode cursor = parent;
            while (!NodeRoutes.TryGetValue(cursor, out cached))
            {
                pending.Push(cursor);
                if (cursor.Parent == null) break;
                cursor = cursor.Parent;
            }
            RouteIdentity route = cached ?? new(0, 0);
            while (pending.TryPop(out SearchNode? node))
            {
                if (node.Parent == null && (node.Action != null || node.ActionCount != 0))
                { RouteOpaqueOrigins++; route = new(-1, 0); }
                else if (route.Id >= 0)
                {
                    string descriptor = node.Parent == null ? JsonSerializer.Serialize(new {
                        node.StateKey, node.Turn, node.ActionCount, node.Snapshot.HistoryEntryCount,
                        node.TurnSetupChoices
                    }) : JsonSerializer.Serialize(node.Action);
                    RouteEdge edge = new(route.Id, descriptor);
                    if (!RouteEdges.TryGetValue(edge, out RouteIdentity? next))
                    {
                        if (RouteEdges.Count >= MaximumInputs) route = new(-1, 0);
                        else { next = new(++NextRoute, route.Actions + (node.Action == null ? 0 : 1));
                            RouteEdges.Add(edge, next); route = next; }
                    }
                    else route = next;
                    if (route.Id >= 0 && route.Actions != node.ActionCount)
                    { RouteOpaqueOrigins++; route = new(-1, 0); }
                }
                NodeRoutes.Add(node, route);
            }
            return route;
        }

        private static class HistoryMetadata
        {
            private static readonly Type HistoryType = typeof(CombatSolver.Engine.InCombat.Simulation.CombatPredictionHistory);
            internal static readonly System.Reflection.FieldInfo Head = AccessTools.Field(HistoryType, "_prefix");
            internal static readonly System.Reflection.FieldInfo Tail = AccessTools.Field(HistoryType, "_tail");
            internal static readonly System.Reflection.FieldInfo Completions = AccessTools.Field(HistoryType, "_tailCompletions");
            internal static readonly System.Reflection.FieldInfo Pending = AccessTools.Field(HistoryType, "_pendingDeferredEntries");
        }

        // Called under ReplayGate. Weak identities and detached integer/string stamps
        // never retain a simulator, history entry, segment, CardPlay, trace or model graph.
        private static PrefixStamp CapturePrefix(CombatBeamSolver solver, SearchNode parent)
        {
            // Ordinary Fork seals the parent's tail. Use the same owner gate as
            // parallel seed preparation so the observation cannot mix before/after fields.
            object? forkGate = Metadata.ForkGate.GetValue(solver);
            if (forkGate == null) return CapturePrefixOwned(parent);
            lock (forkGate) return CapturePrefixOwned(parent);
        }

        private static PrefixStamp CapturePrefixOwned(SearchNode parent)
        {
            object history = parent.Snapshot.Simulator.History;
            int Id(object? item) => item == null ? 0
                : HistoryIdentities.GetValue(item, _ => new(++NextHistoryIdentity)).Id;
            System.Collections.IEnumerable? tail =
                (System.Collections.IEnumerable?)HistoryMetadata.Tail.GetValue(history);
            string entries = tail == null ? "" : string.Join(",", tail.Cast<object>().Select(Id));
            return new(Id(HistoryMetadata.Head.GetValue(history)), entries,
                Id(HistoryMetadata.Completions.GetValue(history)),
                (int)(HistoryMetadata.Pending.GetValue(history)
                    ?? throw new InvalidOperationException("Prefix probe requires deferred-entry count.")));
        }

        internal static void Install(string output)
        {
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_TRANSITION_PROBE") != "1") return;
            ObservePrefix = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_TRANSITION_PREFIX_PROBE") == "1";
            ObserveRoute = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_TRANSITION_ROUTE_PROBE") == "1";
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "CreateExpansionWorker"),
                postfix: new HarmonyMethod(typeof(ReplayRequests), nameof(ObserveWorker)));
            GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(CombatBeamSolver), "ReplayAction"),
                prefix: new HarmonyMethod(typeof(ReplayRequests), nameof(BeforeReplay)),
                postfix: new HarmonyMethod(typeof(ReplayRequests), nameof(AfterReplay)));
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (ReplayGate)
                    File.WriteAllText(Path.Combine(output, "action-transition-probe.json"),
                        JsonSerializer.Serialize(new {
                            schemaVersion = 2, observationOnly = true, maximumInputs = MaximumInputs,
                            scope = "Exact frozen root identity, existing state key, action count, boundary, full serialized PlanAction and capture mode. Selected path/snapshot features and profile equality are diagnostic checks, not full state/history/callback/policy proof. No simulator or snapshot graph retained.",
                            Started, Completed, Repeats, SameMember, CrossMember, LimitBypasses,
                            BeforeMismatches, AfterStateMismatches, AfterFeatureMismatches,
                            HistoryOnlyMismatches, SelectedMatches, SameProfileSelectedMatches,
                            DifferentProfileSelectedMatches, retainedInputs = Inputs.Count,
                            prefixObservationEnabled = ObservePrefix,
                            prefixScope = "Existing input plus exact sealed history head identity, ordered tail entry identities, tail completion-map identity and pending count. Reads use the existing parallel fork owner gate; different segment layouts are not normalized. This is a conservative identity census, not a complete policy/state/checkpoint or cache proof.",
                            PrefixRepeats, PrefixLimitBypasses, PrefixSelectedMatches,
                            PrefixBeforeMismatches, PrefixAfterMismatches, PrefixDifferentProfileMatches,
                            retainedPrefixInputs = PrefixInputs.Count,
                            routeObservationEnabled = ObserveRoute,
                            routeScope = "Exact ordered full PlanAction prefix from a zero-action SearchNode origin, origin state key/turn/history count/setup choices, and selected immutable replay-policy fields. Parent/result state checks remain diagnostic; full origin history and all policy/checkpoint/callback semantics are not certified. No route deletion or cache.",
                            RouteRepeats, RouteLimitBypasses, RouteOpaqueOrigins,
                            RouteParentStateMismatches, RouteResultStateMismatches, RouteResultFeatureMismatches,
                            RouteLabelMismatches, RouteDifferentProfileMatches,
                            retainedRouteInputs = RouteInputs.Count, retainedRouteEdges = RouteEdges.Count,
                            routeExamples = RouteExamples,
                            modes = Modes, members = MemberResults.Select(m => new {
                                m.Id, m.Root, m.Workers, m.Profile, m.ReplayPolicy
                            }).ToArray(), Examples
                        }, new JsonSerializerOptions { WriteIndented = true }));
            };
        }

        private static Member For(CombatBeamSolver solver) => Members.GetValue(solver, _ =>
        {
            CombatRootSnapshot root = (CombatRootSnapshot)(Metadata.Root.GetValue(solver)
                ?? throw new InvalidOperationException("Transition probe requires captured root."));
            Member member = new() {
                Id = ++NextMember,
                Root = Roots.GetValue(root, _ => new(++NextRoot)).Id,
                Profile = JsonSerializer.Serialize(Metadata.Profile.GetValue(solver)),
                ReplayPolicy = ObserveRoute ? JsonSerializer.Serialize(RouteMetadata.Fields
                    .Select((field, index) => new { name = RouteMetadata.Names[index], value = field.GetValue(solver) })) : ""
            };
            MemberResults.Add(member);
            return member;
        });

        private static void ObserveWorker(CombatBeamSolver __instance, CombatBeamSolver __result)
        {
            lock (ReplayGate)
            {
                Member member = For(__instance);
                Members.Add(__result, member);
                member.Workers++;
            }
        }

        private static readonly ConditionalWeakTable<Observation, RouteIdentity> ObservationRoutes = new();

        private static void BeforeReplay(CombatBeamSolver __instance, SearchNode parent,
            PlanAction action, object? roundCheckpointCapture, object? cardChoiceCapture,
            out Observation __state)
        {
            string actionText = JsonSerializer.Serialize(action);
            int mode = (roundCheckpointCapture == null ? 0 : 1) | (cardChoiceCapture == null ? 0 : 2);
            Features features = Features.Capture(parent.Snapshot);
            PathLabel label = new(parent.PotionCount, parent.PotionStrategicCost, parent.FutureSoldHp,
                BitConverter.DoubleToInt64Bits(parent.Score), parent.Traits, parent.HasNonPotionAction);
            lock (ReplayGate)
            {
                Member member = For(__instance);
                Started++;
                __state = new(new(member.Root, parent.StateKey, parent.ActionCount,
                    parent.BoundaryReason, actionText, mode), member.Id, member.Profile,
                    features, label, parent.Snapshot.HistoryEntryCount,
                    ObservePrefix ? CapturePrefix(__instance, parent) : default);
                if (ObserveRoute) ObservationRoutes.Add(__state, RouteFor(parent));
            }
        }

        private static void AfterReplay(SimulationSnapshot __result, Observation __state)
        {
            Features after = Features.Capture(__result);
            lock (ReplayGate)
            {
                Completed++;
                Modes.TryGetValue(__state.Input.CaptureMode, out long modeCount);
                Modes[__state.Input.CaptureMode] = modeCount + 1;
                if (ObserveRoute)
                {
                    RouteIdentity route = ObservationRoutes.GetValue(__state,
                        _ => throw new InvalidOperationException("Route observation prefix missing."));
                    if (route.Id < 0) RouteLimitBypasses++;
                    else
                    {
                        string replayPolicy = MemberResults.Single(m => m.Id == __state.Member).ReplayPolicy;
                        RouteInput input = new(__state.Input.Root, route.Id, replayPolicy,
                            __state.Input.Action, __state.Input.CaptureMode);
                        if (RouteInputs.TryGetValue(input, out Entry? old))
                        {
                            RouteRepeats++;
                            bool parentEqual = old.First.Input.State == __state.Input.State
                                && old.First.BeforeHistory == __state.BeforeHistory;
                            bool resultEqual = old.AfterState == __result.StateKey
                                && old.AfterHistory == __result.HistoryEntryCount;
                            bool featuresEqual = old.After == after;
                            if (!parentEqual) RouteParentStateMismatches++;
                            if (!resultEqual) RouteResultStateMismatches++;
                            if (!featuresEqual) RouteResultFeatureMismatches++;
                            if (old.First.Label != __state.Label) RouteLabelMismatches++;
                            if (parentEqual && resultEqual && featuresEqual
                                && old.First.Profile != __state.Profile) RouteDifferentProfileMatches++;
                            if ((!parentEqual || !resultEqual || !featuresEqual) && RouteExamples.Count < 12)
                                RouteExamples.Add(new { input, parentEqual, resultEqual, featuresEqual,
                                    oldParent = old.First.Input.State, parent = __state.Input.State,
                                    oldResult = old.AfterState, result = __result.StateKey,
                                    oldFeatures = old.After, features = after });
                        }
                        else if (RouteInputs.Count >= MaximumInputs) RouteLimitBypasses++;
                        else RouteInputs.Add(input, new(__state, __result.StateKey, after, __result.HistoryEntryCount));
                    }
                }
                if (ObservePrefix)
                {
                    PrefixInput prefixInput = new(__state.Input, __state.Prefix);
                    if (PrefixInputs.TryGetValue(prefixInput, out Entry? prefixPrior))
                    {
                        PrefixRepeats++;
                        bool prefixBefore = prefixPrior.First.Before == __state.Before
                            && prefixPrior.First.Label == __state.Label
                            && prefixPrior.First.BeforeHistory == __state.BeforeHistory;
                        bool prefixAfter = prefixPrior.AfterState == __result.StateKey
                            && prefixPrior.After == after && prefixPrior.AfterHistory == __result.HistoryEntryCount;
                        if (!prefixBefore) PrefixBeforeMismatches++;
                        if (!prefixAfter) PrefixAfterMismatches++;
                        if (prefixBefore && prefixAfter)
                        {
                            PrefixSelectedMatches++;
                            if (prefixPrior.First.Profile != __state.Profile) PrefixDifferentProfileMatches++;
                        }
                    }
                    else if (PrefixInputs.Count >= MaximumInputs) PrefixLimitBypasses++;
                    else PrefixInputs.Add(prefixInput, new(__state, __result.StateKey, after, __result.HistoryEntryCount));
                }
                if (!Inputs.TryGetValue(__state.Input, out Entry? prior))
                {
                    if (Inputs.Count >= MaximumInputs) { LimitBypasses++; return; }
                    Inputs.Add(__state.Input, new(__state, __result.StateKey, after, __result.HistoryEntryCount));
                    return;
                }
                Repeats++;
                if (prior.First.Member == __state.Member) SameMember++; else CrossMember++;
                bool beforeMatches = prior.First.Before == __state.Before && prior.First.Label == __state.Label;
                bool stateMatches = prior.AfterState == __result.StateKey;
                bool afterMatches = prior.After == after;
                bool historyMatches = prior.First.BeforeHistory == __state.BeforeHistory
                    && prior.AfterHistory == __result.HistoryEntryCount;
                if (!beforeMatches) BeforeMismatches++;
                if (!stateMatches) AfterStateMismatches++;
                if (!afterMatches) AfterFeatureMismatches++;
                if (beforeMatches && stateMatches && afterMatches)
                {
                    SelectedMatches++;
                    if (prior.First.Profile == __state.Profile) SameProfileSelectedMatches++;
                    else DifferentProfileSelectedMatches++;
                    if (!historyMatches) HistoryOnlyMismatches++;
                }
                else if (Examples.Count < 12)
                    Examples.Add(new {
                        action = __state.Input.Action, priorMember = prior.First.Member,
                        member = __state.Member, beforeMatches, stateMatches, afterMatches,
                        historyMatches, priorBefore = prior.First.Before, before = __state.Before,
                        priorAfter = prior.After, currentAfter = after,
                        priorLabel = prior.First.Label, currentLabel = __state.Label
                    });
            }
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
