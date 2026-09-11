using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactSearchBackendAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Deck.Cards.Count != 30 || player.PlayerCombatState!.AllCards.Count() != 30)
            throw new InvalidOperationException("Compact search differential requires the unchanged original 30-card combat and run deck.");
        var captured = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var original = CaptureActual(combat, player, combat.Enemies.Single());
        CompactCombatRoot compact;
        var preparation = Stopwatch.StartNew();
        using (SimulationNotificationIsolation.Enter()) compact = new(captured.ForkSimulator(), player);
        preparation.Stop();
        var (baseline, baselineResources) = await Measure(policy);
        Write("compact-search-baseline.json", Summary(baseline));
        int pendingChecks;
        using (SimulationNotificationIsolation.Enter())
            pendingChecks = AssertCompactReplayBoundaries(captured, display, damage, policy, player, baseline.BestNode.Actions);
        var (changed, candidateResources) = await Measure(policy with { CompactRoot = compact });
        Write("compact-search-candidate.json", Summary(changed));
        object Logical(SolverResult result) => new
        {
            result.BestNode.Actions, result.BestNode.Score, result.Snapshot, result.Continuations, result.CombatEndedTurn,
            result.ProjectedBattleHpLost, result.ExpandedNodes, result.TransitionCount, result.ForkCount,
            result.ReplayCount, result.ReusedNodeSnapshots, result.ChoiceBranchesEvaluated,
            result.ChoiceReplayAttempts, result.ChoiceReplayBudgetExhaustions, result.ChoiceBranchesDroppedByBudget,
            result.DominatedActionsPruned, result.TopQueueActionsDropped, result.ActionAdmissionRepresentativesProtected,
            result.DuplicateCardBranchesPruned, result.ShuffleBranchesPruned, result.SoldHpBranchesPruned,
            result.HpInvestmentBranchesProtected, result.TranspositionBranchesPruned,
            result.RepeatableNoProgressBranchesPruned, result.StandPatProbes,
            result.OrderedMutationCandidatesAdmitted, result.CrossTurnCandidatesProtected,
            result.CrossTurnContinuationsStopped, result.CycleShapesDetected, result.CycleRegionsDetected,
            result.PrimaryIncumbentBranchesPruned, result.PrimaryIncumbentUpdates
        };
        Write("compact-search-logical-baseline.json", Logical(baseline));
        Write("compact-search-logical-candidate.json", Logical(changed));
        if (JsonSerializer.Serialize(Logical(baseline)) != JsonSerializer.Serialize(Logical(changed)))
        {
            throw new InvalidOperationException("Compact backend changed the complete route, evaluation or logical search work; see logical evidence.");
        }
        var counts = compact.Counts;
        if (counts.CompletedReplays <= 0 || counts.Materializations >= counts.CompletedReplays)
            throw new InvalidOperationException("Compact search did not avoid completed-candidate materialization.");
        AssertSnapshotEqual(original, CaptureActual(combat, player, combat.Enemies.Single()), "CompactSearchBackend", "ActualUnchanged");
        Write("compact-search-backend.json", new
        {
            equivalent = true, pendingChecks, preparationMilliseconds = preparation.Elapsed.TotalMilliseconds,
            completedReplays = counts.CompletedReplays, pendingReplays = counts.PendingReplays,
            materializations = counts.Materializations, baseline = Summary(baseline), candidate = Summary(changed),
            baselineResources, candidateResources
        });
        _completedChecks.Add($"CompactSearchBackend:WholeSearchLogicalEquivalent:{counts.CompletedReplays}Completed:{counts.PendingReplays}Pending:{counts.Materializations}Materializations:ActualUnchanged");

        static object Summary(SolverResult result) => new
        {
            result.ExpandedNodes, result.TransitionCount, result.ChoiceBranchesEvaluated,
            result.CombatEndedTurn, result.ProjectedBattleHpLost, result.Elapsed,
            actions = result.BestNode.Actions.Count, result.MaxParallelExpansionConcurrency
        };
        async Task<(SolverResult Result, object Resources)> Measure(SearchPolicySnapshot selectedPolicy)
        {
            using var process = Process.GetCurrentProcess();
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            TimeSpan cpu = process.TotalProcessorTime;
            int[] collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var result = await Task.Run(() => CombatSearchCoordinator.Solve(captured, display, damage, selectedPolicy, default, null));
            process.Refresh();
            return (result, new
            {
                scope = "Process-wide deltas during headless coordinator solve; includes game/native background work",
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                gcCollections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collections[generation]).ToArray(),
                managedHeapAtEndBytes = GC.GetTotalMemory(forceFullCollection: false),
                workingSetAtEndBytes = process.WorkingSet64
            });
        }
        void Write(string file, object value)
        {
            if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory)) return;
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, file),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static int AssertCompactReplayBoundaries(CombatRootSnapshot root, SolverDisplayNames display,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, Player player, IReadOnlyList<PlanAction> route)
    {
        var captured = new CompactCombatRoot(root.ForkSimulator(), player);
        var legacy = new CombatBeamSolver(root, display, damage, policy, searchProfile: policy.ShortProfile);
        var compact = new CombatBeamSolver(root, display, damage, policy with { CompactRoot = captured }, searchProfile: policy.ShortProfile);
        var continuationReader = captured.Adapter.CreateReadView();
        var beforeLegacy = InvokeForcedTerminalReplay(legacy, [], null, root.StartTurnNumber, null);
        var beforeCompact = InvokeForcedTerminalReplay(compact, [], null, root.StartTurnNumber, null);
        List<(CompactCombatCandidate Values, TurnStartChoiceRequest Request, string Signature)> retained = [];
        try
        {
            for (int step = 0; step < route.Count; step++)
            {
                PlanAction action = route[step];
                int choiceCount = action.Kind == PlanActionKind.EndTurn ? action.TurnStartChoices?.Count ?? 0
                    : action.GetActionChoicesInExecutionOrder().Count;
                if (choiceCount > 0)
                {
                    PlanAction invalid = InvalidFirstChoice(action);
                    AssertInvalid(legacy, beforeLegacy, invalid);
                    AssertInvalid(compact, beforeCompact, invalid);
                }
                for (int cut = 0; cut < choiceCount; cut++)
                {
                    PlanAction partial = Prefix(action, cut);
                    var expected = InvokeForcedTerminalReplay(legacy, [partial], beforeLegacy, beforeLegacy.Turn, null);
                    SimulationSnapshot? actual = null;
                    try
                    {
                        actual = InvokeForcedTerminalReplay(compact, [partial], beforeCompact, beforeCompact.Turn, null);
                        if (expected.BoundaryReason != SearchBoundaryReason.PendingChoice || actual.CompactPendingChoice == null)
                            throw new InvalidOperationException($"Compact partial plan did not suspend at {step}/{cut}.");
                        AssertCompactEvaluation(expected, actual, $"SearchPending/{step}/{cut}/{action.CardId}");
                        var request = ((SimulatedCombatState)expected.Simulator.State.CombatState).PendingTurnStartChoice!;
                        string signature = Signature(request);
                        if (signature != Signature(actual.CompactPendingChoice))
                            throw new InvalidOperationException($"Compact pending request/spec differs at {step}/{cut}: {signature} / {Signature(actual.CompactPendingChoice)}.");
                        retained.Add((actual.CompactCandidate!, actual.CompactPendingChoice, signature));
                    }
                    finally { expected.ReleaseSimulator(); actual?.ReleaseSimulator(); }
                }
                var nextLegacy = InvokeForcedTerminalReplay(legacy, [action], beforeLegacy, beforeLegacy.Turn, null);
                var nextCompact = InvokeForcedTerminalReplay(compact, [action], beforeCompact, beforeCompact.Turn, null);
                beforeLegacy.ReleaseSimulator(); beforeCompact.ReleaseSimulator();
                beforeLegacy = nextLegacy; beforeCompact = nextCompact;
                AssertCompactEvaluation(beforeLegacy, beforeCompact, $"SearchCompleted/{step}/{action.CardId}");
                continuationReader.Read(beforeCompact.CompactCandidate!.Values.Open());
                var expectedStamp = ContinuationStamp.CapturePredicted(player, beforeLegacy.Simulator,
                    beforeLegacy.Turn, root.Forecast, root.StartTurnNumber);
                var actualStamp = ContinuationStamp.CapturePredicted(player, continuationReader.EvaluationContext,
                    beforeCompact.Turn, root.Forecast, root.StartTurnNumber, continuationReader);
                if (expectedStamp != actualStamp)
                    throw new InvalidOperationException($"Compact continuation differs at {step}: {expectedStamp.DescribeFirstDifference(actualStamp)}.");
            }
            var metadata = new CompactPlanReplay(captured.Adapter);
            foreach (var sample in retained.AsEnumerable().Reverse())
            {
                if (Signature(sample.Request) != sample.Signature)
                    throw new InvalidOperationException("Pending choice previews were overwritten by later lane restores.");
                var workspace = sample.Values.Values.Open();
                if (!workspace.NeedsChoice || Signature(metadata.CapturePendingChoice(workspace, sample.Request.Timing)) != sample.Signature)
                    throw new InvalidOperationException("Frozen selector did not preserve its request and source instances.");
            }
            return retained.Count;
        }
        finally { beforeLegacy.ReleaseSimulator(); beforeCompact.ReleaseSimulator(); }

        static PlanAction Prefix(PlanAction action, int count)
        {
            if (action.Kind == PlanActionKind.EndTurn) return action with { TurnStartChoices = action.TurnStartChoices!.Take(count).ToArray() };
            int before = Math.Min(count, action.NestedChoicesBeforePrimary);
            bool primary = action.Choice != null && count > action.NestedChoicesBeforePrimary;
            var nested = (action.NestedChoices ?? []).Take(before)
                .Concat((action.NestedChoices ?? []).Skip(action.NestedChoicesBeforePrimary).Take(count - before - (primary ? 1 : 0))).ToArray();
            return action with { Choice = primary ? action.Choice : null, NestedChoices = nested, NestedChoicesBeforePrimary = before };
        }
        static PlanAction InvalidFirstChoice(PlanAction action)
        {
            PlanCardChoice Invalid(PlanCardChoice choice) => choice with { SourceId = choice.SourceId + "_INVALID" };
            if (action.Kind == PlanActionKind.EndTurn)
                return action with { TurnStartChoices = action.TurnStartChoices!.Select((choice, index) => index == 0 ? Invalid(choice) : choice).ToArray() };
            if (action.NestedChoicesBeforePrimary == 0 && action.Choice != null)
                return action with { Choice = Invalid(action.Choice) };
            return action with { NestedChoices = action.NestedChoices!.Select((choice, index) => index == 0 ? Invalid(choice) : choice).ToArray() };
        }
        static void AssertInvalid(CombatBeamSolver driver, SimulationSnapshot parent, PlanAction invalid)
        {
            try
            {
                InvokeForcedTerminalReplay(driver, [invalid], parent, parent.Turn, null).ReleaseSimulator();
            }
            catch (InvalidPlannedChoiceBranchException) { return; }
            throw new InvalidOperationException("Replay accepted a choice from a different source.");
        }
        static string Signature(TurnStartChoiceRequest request)
        {
            var spec = request.Spec ?? throw new InvalidOperationException("Pending differential requires an explicit option spec.");
            return JsonSerializer.Serialize(new
            {
                request.SourceId, request.Effect, request.SourcePile, request.Count, request.ContextId, request.Timing,
                spec.MinCount, spec.MaxCount, spec.ReplacementValue, spec.MaxBranches, specContextId = spec.ContextId,
                options = spec.Options.Select(card => CardChoiceSupport.ChoiceCardKey(card)).ToArray(),
                source = spec.SourceCards.Select(card => CardChoiceSupport.ChoiceCardKey(card)).ToArray()
            });
        }
    }
}
