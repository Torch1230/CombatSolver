using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

// Offline data collection, after the learner's request has finished. Corrective
// completions are feasible witnesses, not an optimal cost-to-go oracle.
internal static class OutcomeCorrections
{
    internal static void Run(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, HarnessOptions options,
        MainLoopContext loop, SearchOutcomeValueModel collector)
    {
        var queries = collector.BeginCorrections();
        if (options.VerifyOutcomePrefix)
            WitnessPrefixChecks.Run(root, names, damage, policy, queries, options.OutputDirectory);
        Stopwatch clock = Stopwatch.StartNew();
        List<object> trials = [];
        for (int index = 0; index < queries.Length; index++)
        {
            var query = queries[index];
            int remaining = 6000 - (int)clock.ElapsedMilliseconds;
            if (remaining < 100) break;
            int milliseconds = Math.Min(1500, remaining / (queries.Length - index));
            if (milliseconds < 100) break;
            SearchRequestWorkTotals totals = new();
            SolverSearchProfile profile = policy.Profile with
            {
                BeamWidth = 8, MaxExpandedNodes = 1000, SoftTimeBudgetMilliseconds = milliseconds,
            };
            var teacher = policy with
            {
                UseObjectiveSearch = false, ObjectiveValueModel = null, UseAutomaticSearch = false,
                SharedEvidence = null, RequestWorkTotals = totals, Profile = profile,
                BudgetOverrideMilliseconds = milliseconds, MeasurePhasePerformance = false,
            };
            var before = collector.CorrectionWitness(query);
            var task = Task.Run(() => new CombatBeamSolver(root, names, damage, teacher,
                searchProfile: profile, witnessPrefix: query.Replay).Solve());
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(70), "outcome correction");
            var result = task.GetAwaiter().GetResult();
            var after = collector.CorrectionWitness(query);
            trials.Add(new
            {
                query.Prefix, turn = query.Replay.Steps[^1].Turn,
                nodeAllowance = 1000, timeAllowanceMs = milliseconds,
                work = totals.Snapshot(), before, after,
                queryWitnessAdded = before == null && after != null,
                queryWitnessImproved = before != null && after is { Won: true }
                    && SolverInterimResultOrdering.IsBetter(after, before),
                result.BoundaryReason, snapshotRisk = result.Snapshot.HasRisk,
                quality = CombatSearchCoordinator.CapturePortfolioQuality(root, teacher, result),
            });
        }
        File.WriteAllText(Path.Combine(options.OutputDirectory, "outcome-corrections.json"),
            JsonSerializer.Serialize(new
            {
                requested = queries.Length, completed = trials.Count, allowanceMilliseconds = 6000,
                elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, trials,
            }, UnattendedTestFiles.JsonOptions));
        File.WriteAllText(Path.Combine(options.OutputDirectory, "outcome-correction-rows.json"),
            JsonSerializer.Serialize(collector.ExportCorrectionRows()));
    }
}
