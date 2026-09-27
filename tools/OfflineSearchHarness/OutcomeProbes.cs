using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>
/// Research-only root rollout: reserve half the request for ordinary search, then
/// replay distinct observed first actions and finish with narrow beam searches.
/// Outcomes are feasible witnesses, never optimal values or admissible bounds.
/// </summary>
internal static class OutcomeProbes
{
    internal static SolverResult Run(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, HarnessOptions options,
        MainLoopContext loop, ref bool timeBoundary)
    {
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Outcome probes require shared accounting.");
        if (policy.Profile.MaxExpandedNodes < 2)
            throw new ArgumentException("Outcome probes require at least two expanded nodes.");
        Stopwatch clock = Stopwatch.StartNew();
        OpeningCollector collector = new();
        bool observedTime = false;
        List<object> trials = [];
        SolverResult RunOne(string label, int nodes, int milliseconds,
            IReadOnlyList<PlanAction> prefix, SearchPathObserver? observer, int beam)
        {
            SearchRequestWorkSnapshot before = totals.Snapshot();
            SearchDiagnosticsSink diagnostics = new(message =>
            {
                if (message.Contains("reason=time", StringComparison.Ordinal)) observedTime = true;
                policy.Diagnostics.Info(message);
            }, policy.Diagnostics.Debug, observer);
            SolverSearchProfile profile = policy.Profile with
            {
                BeamWidth = beam, MaxExpandedNodes = nodes,
                SoftTimeBudgetMilliseconds = milliseconds,
            };
            CombatBeamSolver solver = new(root, names, damage, policy with
            { Profile = profile, Diagnostics = diagnostics }, searchProfile: profile,
                fixedPrefixActions: prefix);
            Task<SolverResult> task = Task.Run(solver.Solve);
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(120), "outcome probe " + label);
            SolverResult result = task.GetAwaiter().GetResult();
            observedTime |= result.BoundaryReason == SearchBoundaryReason.TimeLimit;
            SearchRequestWorkSnapshot after = totals.Snapshot();
            SolverInterimResult quality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, result);
            trials.Add(new
            {
                label, prefix, nodeAllowance = nodes, timeAllowanceMs = milliseconds, beam,
                expanded = after.ExpandedNodes - before.ExpandedNodes,
                transitions = after.TransitionCount - before.TransitionCount,
                seconds = (after.Elapsed - before.Elapsed).TotalSeconds,
                quality, result.BoundaryReason,
                usableWitness = Usable(result, quality),
            });
            return result;
        }

        SolverResult selected = RunOne("baseline-half", policy.Profile.MaxExpandedNodes / 2,
            Math.Max(1, options.BudgetMilliseconds / 2), [], collector.Observer, policy.Profile.BeamWidth);
        SolverInterimResult selectedQuality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, selected);
        string selectedLabel = "baseline-half";
        // Uniform deterministic coverage of observed action identities, independent of their scores.
        PlanAction[] candidates = collector.Actions.Values.ToArray();
        int count = Math.Min(options.OutcomeProbes, candidates.Length);
        for (int i = 0; i < count; i++)
        {
            long remainingNodes = policy.Profile.MaxExpandedNodes - totals.Snapshot().ExpandedNodes;
            int remainingMs = options.BudgetMilliseconds - (int)clock.ElapsedMilliseconds;
            if (remainingNodes <= 0 || remainingMs <= 0)
            {
                observedTime |= remainingMs <= 0;
                break;
            }
            PlanAction action = candidates[i * candidates.Length / count];
            int nodes = (int)Math.Max(1, remainingNodes / (count - i));
            int ms = Math.Max(1, remainingMs / (count - i));
            string label = "prefix-" + i;
            SolverResult candidate = RunOne(label, nodes, ms, [action], null,
                Math.Min(8, policy.Profile.BeamWidth));
            SolverInterimResult quality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, candidate);
            // Unknown/truncated/losing suffixes are not negative training examples.
            // Only risk-free completed victories can replace the retained baseline.
            if (Usable(candidate, quality) && CombatSearchCoordinator.IsBetterPotionPolicyResult(
                policy.TheftPolicy, quality with { Score = 0 }, selectedQuality with { Score = 0 }))
            {
                selected = candidate;
                selectedQuality = quality;
                selectedLabel = label;
            }
        }
        SearchRequestWorkSnapshot work = totals.Snapshot();
        selected.TotalExpandedNodes = work.ExpandedNodes;
        selected.TotalTransitionCount = work.TransitionCount;
        selected.TotalChoiceBranchesEvaluated = work.ChoiceBranchesEvaluated;
        selected.TotalCycleReplayActions = work.CycleReplayActions;
        selected.TotalSearchElapsed = work.Elapsed;
        selected.TotalWorkerAllocatedBytes = work.WorkerAllocatedBytes;
        selected.TotalGen0Collections = checked((int)work.Gen0Collections);
        selected.TotalGen1Collections = checked((int)work.Gen1Collections);
        selected.TotalGen2Collections = checked((int)work.Gen2Collections);
        selected.TotalGcPauseDuration = work.GcPauseDuration;
        selected.TotalMaxObservedGcPause = work.MaxObservedGcPause;
        timeBoundary |= observedTime;
        File.WriteAllText(Path.Combine(options.OutputDirectory, "outcome-probes.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1, trainingSeconds = 0, selectedLabel,
                options.OutcomeProbes, options.BudgetMilliseconds, policy.Profile.MaxExpandedNodes,
                observedFirstActions = candidates.Length, collector.Truncated,
                collector.UnsupportedRootChoices, collector.SkippedTurnEndingActions,
                elapsedSeconds = clock.Elapsed.TotalSeconds, timeBoundary = observedTime,
                nodeAllowanceExceeded = work.ExpandedNodes > policy.Profile.MaxExpandedNodes,
                work, trials,
            }, UnattendedTestFiles.JsonOptions));
        return selected;
    }

    private static bool Usable(SolverResult result, SolverInterimResult quality)
        => quality.Won && quality.Survives && !result.Snapshot.HasRisk
            && result.BoundaryReason == SearchBoundaryReason.None;

    private sealed class OpeningCollector
    {
        // Only detached action values survive a solver run. No simulator/node references.
        internal SortedDictionary<string, PlanAction> Actions { get; } = new(StringComparer.Ordinal);
        internal bool Truncated { get; private set; }
        internal bool UnsupportedRootChoices { get; private set; }
        internal int SkippedTurnEndingActions { get; private set; }
        private bool _closed;
        private bool _sawFirstAction;
        internal SearchPathObserver Observer { get; }

        internal OpeningCollector()
            => Observer = new SearchPathObserver(_ => false, Observe, _ => !_closed);

        private void Observe(SearchPathObservation observation)
        {
            if (observation.Stage == SearchPathObservationStage.GlobalRetention && _sawFirstAction)
                _closed = true;
            if (observation.Stage != SearchPathObservationStage.RetentionPoolInput || _closed)
                return;
            if (observation.RootTurnSetupChoices.Count != 0)
            {
                UnsupportedRootChoices = true;
                _closed = true;
                return;
            }
            if (observation.Actions.Count != 1)
            {
                // A root with only turn-ending moves must not keep copying every
                // later retention pool merely because no supported probe was found.
                if (observation.Actions.Count > 1) _closed = true;
                return;
            }
            _sawFirstAction = true;
            PlanAction action = observation.Actions[0];
            if (action.Kind != PlanActionKind.PlayCard || action.EndsPlayerTurn)
            {
                SkippedTurnEndingActions++;
                return;
            }
            string key = JsonSerializer.Serialize(action with
            { RelicEffects = null, CardTitle = "", TargetName = "", PotionTitle = "" },
                UnattendedTestFiles.JsonOptions);
            if (Actions.ContainsKey(key)) return;
            if (Actions.Count >= 128) { Truncated = true; return; }
            Actions.Add(key, action);
        }
    }
}
