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
            if (options.Scheduler is not ("beam" or "bfws" or "portfolio" or "tempo" or "tempo-portfolio" or "request" or "replay"))
                throw new InvalidDataException("Expected beam, bfws, portfolio, tempo, tempo-portfolio, request or replay.");
            if (options.BossTempoSearch.HasValue && options.Scheduler != "request")
                throw new InvalidDataException("BossTempoSearch requires the request scheduler.");
            var policy = captured with { UseNoveltyPortfolio = options.Scheduler == "request"
                    ? captured.UseNoveltyPortfolio : options.Scheduler == "portfolio",
                UseBossTempoSearch = options.Scheduler == "request"
                    ? options.BossTempoSearch ?? captured.UseBossTempoSearch : options.Scheduler == "tempo-portfolio",
                BossTempoSearch = options.Scheduler == "tempo" ? new(2) : null,
                NoveltySearch = options.Scheduler == "bfws" ? new() : null, FixedBudget = true,
                Profile = captured.Profile with { SoftTimeBudgetMilliseconds = captured.BudgetOverrideMilliseconds ?? captured.Profile.SoftTimeBudgetMilliseconds,
                    BossTempoHpPricing = options.Scheduler == "tempo" } };
            if (options.Scheduler is "portfolio" or "tempo-portfolio" or "request" && !smart)
                throw new InvalidDataException("Portfolio benchmarks require the request coordinator (smart.flag).");
            if (options.Scheduler == "bfws" && smart)
                throw new InvalidDataException("Standalone novelty benchmarks require a direct solver.");
            if (options.Scheduler == "tempo" && smart)
                throw new InvalidDataException("Standalone tempo benchmarks require a direct solver.");
            CombatBugReportExporter.RecordSearchPolicy(combat, policy);
            SetStage($"novelty_benchmark_{options.Scheduler}");
            using JsonDocument? frozen = options.Scheduler == "replay"
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(area, "frozen-plan.json"))) : null;
            PlanAction[]? frozenActions = frozen?.RootElement.GetProperty("actions")
                .Deserialize<PlanAction[]>(UnattendedTestFiles.JsonOptions);
            if (frozen != null && (smart || frozenActions is not { Length: > 0 }))
                throw new InvalidDataException("Frozen plan replay requires a nonempty route and a direct solver.");
            // A direct single solver with explicit potions disabled isolates scheduling.
            SolverResult SolveVariant() => frozenActions != null
                ? new CombatBeamSolver(root, names, damage, policy, cancellation.Token,
                    searchProfile: policy.Profile, fixedPrefixActions: frozenActions).Solve()
                : smart
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
            if (frozen != null)
            {
                JsonElement quality = frozen.RootElement.GetProperty("quality");
                if (result.ExpandedNodes != 0 || !result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead || result.Snapshot.HasRisk
                    || result.ProjectedBattleHpLost != quality.GetProperty("projectedBattleHpLost").GetInt32()
                    || result.ProjectedBattlePotionCount != quality.GetProperty("projectedBattlePotionCount").GetInt32()
                    || result.CombatEndedTurn != quality.GetProperty("combatEndedTurn").GetInt32()
                    || result.BestNode.Actions.Count != frozenActions!.Length)
                    throw new InvalidOperationException("Frozen plan replay differed from its saved outcome.");
                _completedChecks.Add("NoveltySearch:FrozenPlan:ExactOutcome:Expanded=0");
            }
            _writer.CaptureSolverResult(result);
            var report = new { options, policy.Profile, ordinal = reports.Count,
                result = SolverDiagnostics.DescribeResult(result), research = (object?)result.BossTempoSearch
                    ?? result.BossTempoIteration ?? (object?)result.NoveltyPortfolio ?? result.NoveltySearch ?? (object)new { stop = "beam" },
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
                (long ActionStarted, int Turn, int Hp, bool Deploying, bool FullAuto, bool Paused)? lastProgress = null;
                try
                {
                    SetStage("novelty_native_deployment");
                    SolverController.StartPredictedRouteForTesting(_host, combat, result, state =>
                    {
                        observedEnd = true;
                        ledger.Complete(state);
                    });
                    while (CombatManager.Instance.IsInProgress)
                    {
                        EnsureWithinDeadline();
                        var progress = (SolverController.LastDeployedActionStartedAtMillisecondsForTesting,
                            player.PlayerCombatState!.TurnNumber, player.Creature.CurrentHp,
                            SolverController.IsDeploying, SolverController.FullAutoEnabled,
                            SolverController.AutomaticSearchPaused);
                        if (lastProgress != progress)
                        {
                            lastProgress = progress;
                            _writer.WriteGeneratedArtifact("native-progress.json", new
                            {
                                actionStarted = progress.Item1, turn = progress.Item2, hp = progress.Item3,
                                deploying = progress.Item4, fullAuto = progress.Item5, paused = progress.Item6,
                                audit = SolverController.ReplanAuditForBugReport,
                            });
                        }
                        if (!observedEnd && (SolverController.AutomaticSearchPaused || !SolverController.FullAutoEnabled))
                            throw new InvalidOperationException("Native benchmark deployment paused: " + SolverController.ReplanAuditForBugReport);
                        if (SolverController.UnexpectedReplanCount != 0 || SolverController.LastSearchFailureForTesting != null)
                            throw new InvalidOperationException("Native benchmark deployment replanned or failed: " + SolverController.ReplanAuditForBugReport);
                        await NextFrameAsync();
                    }
                    var outcome = ledger.Capture(combat, ended: true);
                    var forensicOutcome = CombatBugReportExporter.CaptureOutcome(combat);
                    int expectedRemainingLoss = result.ProjectedBattleHpLost - result.BattleHpLostSoFar;
                    int expectedRemainingPotions = result.ProjectedBattlePotionCount - result.BattlePotionsUsedSoFar;
                    _writer.WriteGeneratedArtifact("native-outcome.json", new { outcome, expectedLoss = result.ProjectedBattleHpLost,
                        expectedRemainingLoss, expectedRemainingPotions,
                        expectedPotions = result.ProjectedBattlePotionCount, unexpectedReplans = SolverController.UnexpectedReplanCount,
                        observedEnd, finalPlayerMaxHp = player.Creature.MaxHp, forensicOutcome });
                    if (!observedEnd || !outcome.Survived || outcome.FinalEnemyHp != 0 || outcome.HpLost != expectedRemainingLoss
                        || outcome.Potions.Length != expectedRemainingPotions || outcome.UnattributedHpLoss != 0
                        || forensicOutcome.HpLost != expectedRemainingLoss
                        || forensicOutcome.Potions.Length != expectedRemainingPotions
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
    public bool? BossTempoSearch { get; init; }
}

internal static partial class CombatSearchCoordinator
{
    internal static SolverInterimResult DescribeNoveltyQualityForTesting(
        CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
        => BuildInterimResult(root, policy, result);
}
