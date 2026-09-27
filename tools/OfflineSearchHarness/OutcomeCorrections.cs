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
        Stopwatch clock = Stopwatch.StartNew();
        List<object> trials = [];
        foreach (var query in queries)
        {
            int remaining = 6000 - (int)clock.ElapsedMilliseconds;
            if (remaining < 100) break;
            int milliseconds = Math.Min(1500, remaining);
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
                searchProfile: profile, fixedPrefixActions: query.Prefix).Solve());
            loop.RunUntilCompleted(task, TimeSpan.FromSeconds(70), "outcome correction");
            var result = task.GetAwaiter().GetResult();
            var after = collector.CorrectionWitness(query);
            trials.Add(new
            {
                query.Prefix, nodeAllowance = 1000, timeAllowanceMs = milliseconds,
                work = totals.Snapshot(), before, after,
                queryWitnessImproved = after != null && (before == null
                    || after.Won && SolverInterimResultOrdering.IsBetter(after, before)),
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
    }
}
