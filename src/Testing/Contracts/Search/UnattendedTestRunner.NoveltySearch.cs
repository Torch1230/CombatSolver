using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunNoveltySearchBenchmarkAsync(CombatState combat, Player player)
    {
        string area = _request.EvidenceDirectory ?? throw new InvalidOperationException("Novelty benchmark requires an output directory.");
        string input = File.ReadAllText(Path.Combine(area, "research-options.json"));
        NoveltyBenchmarkVariant[] variants = input.TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize<NoveltyBenchmarkVariant[]>(input, UnattendedTestFiles.JsonOptions)!
            : [JsonSerializer.Deserialize<NoveltyBenchmarkVariant>(input, UnattendedTestFiles.JsonOptions)!];
        if (variants.Length is < 1 or > 5 || variants.Any(v => v == null)) throw new InvalidDataException("Novelty benchmark requires 1..5 variants.");
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        var enemies = combat.Enemies.ToArray();
        var liveBefore = enemies.Select(enemy => CaptureActual(combat, player, enemy)).ToArray();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var rootBefore = CaptureKnownRouteRootStates(root, player, enemies);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var settings = SolverSettings.Capture();
        var captured = SolverController.CaptureSearchPolicy(settings, combat, false, null);
        if (File.Exists(Path.Combine(area, "control-checks.flag")))
        {
            await CheckNoveltyControlsAsync(combat, player, root, names, damage, captured);
            return;
        }
        bool smart = File.Exists(Path.Combine(area, "smart.flag"));
        bool native = File.Exists(Path.Combine(area, "native.flag"));
        bool useGcScope = File.Exists(Path.Combine(area, "gc-scope.flag"));
        bool expectReclaim = File.Exists(Path.Combine(area, "expect-reclaim.flag"));
        if (native && variants.Length != 1) throw new InvalidOperationException("Native deployment requires one selected variant.");
        List<object> reports = [];
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(Math.Max(1, _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds)));
        foreach (NoveltyBenchmarkVariant options in variants)
        {
            if (options.Scheduler is not ("beam" or "bfws" or "portfolio"))
                throw new InvalidDataException("Expected beam, bfws or portfolio.");
            var policy = captured with { UseNoveltyPortfolio = options.Scheduler == "portfolio",
                NoveltySearch = options.Scheduler == "bfws" ? new() : null, FixedBudget = true,
                Profile = captured.Profile with { SoftTimeBudgetMilliseconds = captured.BudgetOverrideMilliseconds ?? captured.Profile.SoftTimeBudgetMilliseconds } };
            if (options.Scheduler == "portfolio" && !smart)
                throw new InvalidDataException("Portfolio benchmarks require the request coordinator (smart.flag).");
            if (options.Scheduler == "bfws" && smart)
                throw new InvalidDataException("Standalone novelty benchmarks require a direct solver.");
            CombatBugReportExporter.RecordSearchPolicy(combat, policy);
            SetStage($"novelty_benchmark_{options.Scheduler}");
            // A direct single solver with explicit potions disabled isolates scheduling.
            SolverResult SolveVariant() => smart
                ? CombatSearchCoordinator.Solve(root, names, damage, policy, cancellation.Token, null)
                : new CombatBeamSolver(root, names, damage, policy, cancellation.Token,
                    searchProfile: policy.Profile, potionPolicyOverride: SolverPotionPolicy.Disabled,
                    maximumPotionUses: 0).Solve();
            SolverResult result = await Task.Run(() =>
            {
                if (!useGcScope) return SolveVariant();
                ISearchGcScope scope;
                SolverResult scoped;
                using (scope = SearchGcPolicy.EnterSearchScope(settings.EnableNoGcRegion,
                           settings.NoGcRegionBudgetBytes, policy.MemoryPressureSignal, cancellation.Token))
                    scoped = SolveVariant();
                if (expectReclaim && policy.MemoryPressureSignal.ReclaimCount == 0)
                    throw new InvalidOperationException("Expected a real memory checkpoint in the novelty request.");
                if (scope.IsLifecycleCompleted)
                {
                    scoped.GcLifecycle = scope.Lifecycle;
                    scoped.GcLifecycleAttribution = scope.LifecycleAttribution;
                }
                return scoped;
            }, cancellation.Token);
            _writer.CaptureSolverResult(result);
            var report = new { options, policy.Profile, ordinal = reports.Count,
                result = SolverDiagnostics.DescribeResult(result), research = (object?)result.NoveltyPortfolio ?? result.NoveltySearch ?? (object)new { stop = "beam" },
                actions = result.BestNode.Actions, effectivePolicy = CombatBugReportExporter.LatestEffectivePolicy,
                quality = CombatSearchCoordinator.DescribeNoveltyQualityForTesting(root, policy, result),
                growthRewards = result.Snapshot.GrowthRewards, relicCounters = result.Snapshot.RelicCounters,
                finalMaxHp = result.Snapshot.PlayerMaxHp,
                useGcScope, reclaimCount = policy.MemoryPressureSignal.ReclaimCount };
            reports.Add(report);
            _writer.WriteGeneratedArtifact($"research-{reports.Count - 1}.json", report);
            _writer.WriteGeneratedArtifact("research-batch.json", reports);
            _writer.WriteGeneratedArtifact("research.json", report);
            if (ContinuationStamp.CaptureLive(combat) != before) throw new InvalidOperationException("Novelty benchmark changed the live root.");
            var rootAfter = CaptureKnownRouteRootStates(root, player, enemies);
            for (int i = 0; i < enemies.Length; i++)
            {
                AssertSnapshotEqual(liveBefore[i], CaptureActual(combat, player, enemies[i]), "NoveltySearch", "live_root");
                AssertSnapshotEqual(rootBefore[i], rootAfter[i], "NoveltySearch", "shadow_root");
            }
            _completedChecks.Add($"NoveltySearch:{reports.Count}:LiveAndShadowRootUnchanged");
            if (native)
            {
                if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead || result.Snapshot.HasRisk)
                    throw new InvalidOperationException("Native benchmark deployment requires a predicted safe victory.");
                using var ledger = new CombatReplayOutcome(combat);
                SolverController.SetStopFullAutoOnCombatEnd(false, persist: false);
                SolverController.SetStopFullAutoOnDeathTurn(false, persist: false);
                SolverController.SetStopFullAutoOnWorseRecalculation(false, persist: false);
                _protocolHost.EnableAutomaticTurnSearch();
                bool observedEnd = false;
                try
                {
                    SolverController.StartPredictedRouteForTesting(_host, combat, result, state =>
                    {
                        observedEnd = true;
                        ledger.Complete(state);
                    });
                    while (CombatManager.Instance.IsInProgress)
                    {
                        EnsureWithinDeadline();
                        if (SolverController.UnexpectedReplanCount != 0 || SolverController.LastSearchFailureForTesting != null)
                            throw new InvalidOperationException("Native benchmark deployment replanned or failed: " + SolverController.ReplanAuditForBugReport);
                        await NextFrameAsync();
                    }
                    var outcome = ledger.Capture(combat, ended: true);
                    _writer.WriteGeneratedArtifact("native-outcome.json", new { outcome, expectedLoss = result.ProjectedBattleHpLost,
                        expectedPotions = result.ProjectedBattlePotionCount, unexpectedReplans = SolverController.UnexpectedReplanCount,
                        observedEnd, finalPlayerMaxHp = player.Creature.MaxHp, forensicOutcome = CombatBugReportExporter.CaptureOutcome(combat) });
                    if (!observedEnd || !outcome.Survived || outcome.FinalEnemyHp != 0 || outcome.HpLost != result.ProjectedBattleHpLost
                        || outcome.Potions.Length != result.ProjectedBattlePotionCount || outcome.UnattributedHpLoss != 0
                        || SolverController.UnexpectedReplanCount != 0)
                        throw new InvalidOperationException("Native battle outcome differed from the frozen initial result.");
                    _completedChecks.Add("NoveltySearch:NativeVictory:ExactLossPotions:NoUnexpectedReplans");
                }
                finally { CombatReplayRecording.TestCombatEndObserver = null; }
            }
        }
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
internal sealed record NoveltyBenchmarkVariant
{
    public string Scheduler { get; init; } = "beam";
}

internal static partial class CombatSearchCoordinator
{
    internal static SolverInterimResult DescribeNoveltyQualityForTesting(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => BuildInterimResult(root, policy, result);
}
