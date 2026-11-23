using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// 模组侧的离线初始化：日志、固定预算设置、无人测试口径开关，并直连 HotSearchCoordinator + HotRootBuilder 求解。
/// </summary>
internal static class ModRuntime
{
    public static List<string> PatchLog { get; } = [];

    /// <summary>离线会话作用域；释放即把无人测试口径还原（进程退出前 Program 负责释放）。</summary>
    public static IDisposable? Session { get; private set; }

    /// <summary>CLI <c>--no-rollout</c> 单构建 A/B 开关（关闭 Stage 8C 回合边界 Rollout）。</summary>
    public static bool DisableTurnBoundaryRolloutsOverride { get; set; }

    public static string Initialize(HarnessOptions options)
    {
        string logDirectory = options.LogDirectory;
        Directory.CreateDirectory(logDirectory);
        SetStatic(typeof(Entry), "Logger", Activator.CreateInstance(
            typeof(CombatSolverLog),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [logDirectory],
            null)!);

        ApplyFixedBudgetSettings(options);
        ApplyUnattendedOverrides(options);

        SolverSettingsSnapshot snapshot = SolverSettings.Capture();
        return $"backend=hot "
            + $"profile={options.Profile} "
            + $"beam={snapshot.Profile.BeamWidth} nodes={snapshot.Profile.MaxExpandedNodes} "
            + $"branches={snapshot.Profile.MaxCardBranchesPerNode}/"
            + $"{snapshot.Profile.MaxPileChoiceBranchesPerAction}/"
            + $"{snapshot.Profile.MaxHandChoiceBranchesPerAction} "
            + $"dop={snapshot.SearchMaxDegreeOfParallelism} "
            + $"potion_policy={snapshot.PotionPolicy}";
    }

    public static SolverSearchProfile ResolveProfile(HarnessOptions options)
    {
        SolverSearchProfile baseline = options.Profile switch
        {
            "Low" => Preset(SolverPerformancePreset.Low),
            "Medium" => Preset(SolverPerformancePreset.Medium),
            "High" => Preset(SolverPerformancePreset.High),
            "VeryHigh" => Preset(SolverPerformancePreset.VeryHigh),
            "Custom" => new SolverSearchProfile(24, 2000, 32, 18, 24, 600_000),
            _ => throw new ArgumentException($"未知预设 {options.Profile}。"),
        };
        return baseline with
        {
            BeamWidth = options.Beam ?? baseline.BeamWidth,
            MaxExpandedNodes = options.Nodes ?? baseline.MaxExpandedNodes,
            MaxCardBranchesPerNode = options.MaxCardBranchesPerNode ?? baseline.MaxCardBranchesPerNode,
            MaxPileChoiceBranchesPerAction =
                options.MaxPileChoiceBranchesPerAction ?? baseline.MaxPileChoiceBranchesPerAction,
            MaxHandChoiceBranchesPerAction =
                options.MaxHandChoiceBranchesPerAction ?? baseline.MaxHandChoiceBranchesPerAction,
        };
    }

    private static SolverSearchProfile Preset(SolverPerformancePreset preset)
        => SolverSettings.ResolvePerformanceValues(
            new SolverSettingsData { PerformancePreset = preset }).Profile;

    private static void ApplyFixedBudgetSettings(HarnessOptions options)
    {
        SolverSearchProfile profile = ResolveProfile(options);
        SolverSettingsData? resourceSettings = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RESOURCE_SETTINGS")
            is { Length: > 0 } resourcePath
                ? JsonSerializer.Deserialize<SolverSettingsData>(File.ReadAllText(resourcePath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    })
                    ?? throw new InvalidDataException("Empty resource policy settings.")
                : null;
        SolverSettings.ApplyForTesting(new SolverSettingsData
        {
            PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion,
            PerformancePreset = SolverPerformancePreset.Custom,
            SearchMaxExpandedNodes = profile.MaxExpandedNodes,
            SearchBeamWidth = profile.BeamWidth,
            SearchTimeLimitSeconds = 600,
            SearchMaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            SearchMaxCardBranchesPerNode = profile.MaxCardBranchesPerNode,
            SearchMaxPileChoiceBranchesPerAction = profile.MaxPileChoiceBranchesPerAction,
            SearchMaxHandChoiceBranchesPerAction = profile.MaxHandChoiceBranchesPerAction,
            StopAtAcceptableBattleHpLoss = options.StopAtZeroLoss,
            OnlineStatisticsEnabled = false,
            SearchCompletionNotificationsEnabled = false,
            PotionPolicy = Enum.Parse<SolverPotionPolicy>(options.PotionPolicy, ignoreCase: true),
            GrowthBudgets = resourceSettings?.GrowthBudgets ?? default,
            RelicStrategyEnabled = resourceSettings?.RelicStrategyEnabled ?? true,
            RelicCounterRules = resourceSettings?.RelicCounterRules ?? [],
        });
    }

    private static void ApplyUnattendedOverrides(HarnessOptions options)
        => Session = UnattendedTestRunner.BeginOfflineSession(new UnattendedTestRunner.OfflineSessionOptions
        {
            FixedSearchBudget = !options.ProductionBudget,
            MeasureSearchPhases = options.MeasureSearchPhases,
            VerifyIncrementalSearch = options.VerifyIncremental,
            SearchBudgetOverrideMilliseconds = options.BudgetMilliseconds,
            SearchMaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            TranspositionPruningDisabledMask = options.TranspositionPruningDisabledMask,
            TranspositionEntryLimit = options.TranspositionEntryLimit,
        });

    public static string DescribeStart(CombatState state)
        => SolverDiagnostics.DescribeStart(state, SolverSettings.Capture().Profile);

    public static object DescribePolicy(SearchPolicySnapshot policy) => new
    {
        policy.Profile,
        policy.PotionPolicy,
        policy.PotionStrategy,
        policy.FixedBudget,
        policy.MaxDegreeOfParallelism,
        policy.IncludeTurnSetup,
        policy.TheftPolicy,
        policy.ActTransitionBossHpStrategy,
        policy.FinalBossHpStrategy,
        policy.AcceptableBattleHpLoss,
        policy.StopAtAcceptableBattleHpLoss,
        policy.Act3BossStrategy,
        policy.GrowthBudgets,
        policy.RelicTargets,
        policy.BrightestFlameMaxHpLossLimit,
        policy.HasGrowthTargets,
        policy.IgnoreLongTermRewards,
        policy.VerifyIncrementalSearch,
        policy.DetailedDiagnostics,
        policy.MeasurePhasePerformance,
        policy.TranspositionEntryLimit,
        policy.EnableTurnBoundaryRollouts,
    };

    internal sealed record SearchOutcome(
        SolverResult Result,
        object? SolverMetrics,
        object Policy,
        Dictionary<string, object?> LegacyMetrics,
        Dictionary<string, object?> RootCapture,
        string RootContinuationStamp,
        string RootLiveStamp,
        object[] RouteActions,
        string[] PlanActions,
        object[] Continuations,
        bool TimeBoundaryObserved,
        double WallSeconds);

    private static SolverResult SolveEvaluate(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot basePolicy,
        int budgetMilliseconds,
        MainLoopContext loop,
        out object describedPolicy,
        ref bool timeBoundary)
    {
        SearchPolicySnapshot policy = basePolicy with
        {
            Profile = basePolicy.Profile with { SoftTimeBudgetMilliseconds = budgetMilliseconds },
        };
        describedPolicy = DescribePolicy(policy);
        bool observedTimeBoundary = false;
        SearchDiagnosticsSink diagnostics = new(
            message =>
            {
                if (message.Contains("TURN_LAYER_BUDGET reason=time", StringComparison.Ordinal))
                    observedTimeBoundary = true;
                policy.Diagnostics.Info(message);
            },
            policy.Diagnostics.Debug);
        SearchPolicySnapshot evaluatePolicy = policy with { Diagnostics = diagnostics };
        Task<SolverResult> solve = Task.Run(() => HotSearchCoordinator.SolveSingleBeam(
            root, names, damage, evaluatePolicy, CancellationToken.None));
        loop.RunUntilCompleted(solve, TimeSpan.FromSeconds(660), "HotSearchCoordinator.SolveSingleBeam");
        SolverResult result = solve.GetAwaiter().GetResult();
        timeBoundary = observedTimeBoundary || result.BoundaryReason == SearchBoundaryReason.TimeLimit;
        return result;
    }

    public static SearchOutcome RunSearchDetailed(
        CombatState state,
        HarnessOptions options,
        MainLoopContext loop,
        UnattendedTestRunner.OfflineScenarioSession? session,
        SolverTheftPolicy? theftPolicyOverride = null)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
        HarnessLog.Trace("root_captured");
        SolverDisplayNames names = SolverDisplayNames.Capture(state);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(state);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, state, includeTurnSetup: false,
            theftPolicy: theftPolicyOverride ?? SolverController.ResolveTheftPolicy(state));
        if (options.DisableTurnBoundaryRollouts || DisableTurnBoundaryRolloutsOverride)
            policy = policy with { EnableTurnBoundaryRollouts = false };
        HarnessLog.Trace("search_policy");

        Action<SolverProgress>? diagnosticProgress = null;
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_STREAM_DIAGNOSTICS") == "1")
        {
            SearchDiagnosticsSink original = policy.Diagnostics;
            policy = policy with
            {
                Diagnostics = new SearchDiagnosticsSink(
                    message =>
                    {
                        Console.WriteLine($"[search-diagnostic] {message}");
                        original.Info(message);
                    },
                    original.Debug),
            };
            long lastProgressMilliseconds = 0;
            diagnosticProgress = progress =>
            {
                long now = watch.ElapsedMilliseconds;
                long previous = Volatile.Read(ref lastProgressMilliseconds);
                if (now - previous < 1000
                    || Interlocked.CompareExchange(ref lastProgressMilliseconds, now, previous) != previous)
                    return;
                Console.WriteLine($"[search-progress] wall_ms={now} phase={progress.Phase} "
                    + $"expanded={progress.ExpandedNodes} reviewed_worldlines={progress.ReviewedWorldlines} "
                    + $"max_nodes={progress.MaxNodes} turn_layers={progress.CompletedTurnLayers} "
                    + $"play_depth={progress.PlayDepth} frontier={progress.FrontierNodes}");
            };
        }

        bool timeBoundary = false;
        int observedCoordinatorTimeBoundary = 0;
        SearchDiagnosticsSink timeBoundaryDiagnostics = policy.Diagnostics;
        policy = policy with
        {
            Diagnostics = new SearchDiagnosticsSink(
                message =>
                {
                    if (message.Contains("TURN_LAYER_BUDGET reason=time", StringComparison.Ordinal)
                        || message.Contains("SEARCH_TIME_BUDGET", StringComparison.Ordinal))
                        Interlocked.Exchange(ref observedCoordinatorTimeBoundary, 1);
                    timeBoundaryDiagnostics.Info(message);
                },
                timeBoundaryDiagnostics.Debug),
        };
        object describedPolicy = DescribePolicy(policy);

        SolverResult result = options.SearchMode == "Coordinator"
            ? HotSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, diagnosticProgress)
            : SolveEvaluate(root, names, damage, policy, options.BudgetMilliseconds, loop, out describedPolicy, ref timeBoundary);

        timeBoundary |= Volatile.Read(ref observedCoordinatorTimeBoundary) != 0;
        HarnessLog.Trace("solved");
        watch.Stop();

        File.WriteAllText(Path.Combine(options.OutputDirectory, "quality.json"), JsonSerializer.Serialize(new
        {
            quality = HotSearchCoordinator.CaptureQuality(root, policy, result),
            snapshot = result.Snapshot,
            result.ResultScope,
            result.BoundaryReason,
        }, UnattendedTestFiles.JsonOptions));

        Dictionary<string, object?> rootCapture = new()
        {
            ["cards"] = (int)root.HotRoot.RootState.TotalCardCount,
            ["powers"] = (int)root.HotRoot.RootState.PowerCount,
            ["enemies"] = (int)root.HotRoot.RootState.EnemyCount,
            ["coldSidecar"] = root.HotRoot.RootCold != null,
        };
        return new SearchOutcome(
            result,
            session?.CaptureSolverMetrics(result),
            describedPolicy,
            BuildMetrics(result),
            rootCapture,
            root.ContinuationStamp.StateText,
            root.LiveStamp.StateText,
            result.BestNode.Actions.Cast<object>().ToArray(),
            result.BestNode.Actions
                .Select(action => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
                    + $":target={action.TargetCombatId?.ToString() ?? "-"}:key={action.CardStateKey}")
                .ToArray(),
            result.Continuations.Select(continuation => (object)new
            {
                continuation.StartTurnNumber,
                continuation.ForecastOffset,
                continuation.ExpectedState.StateText,
            }).ToArray(),
            timeBoundary,
            watch.Elapsed.TotalSeconds);
    }

    private static Dictionary<string, object?> BuildMetrics(SolverResult result)
        => new()
        {
            ["boundary"] = result.BoundaryReason.ToString(),
            ["selectedExpanded"] = result.ExpandedNodes,
            ["selectedTransitions"] = result.TransitionCount,
            ["selectedChoiceBranches"] = result.ChoiceBranchesEvaluated,
            ["totalExpanded"] = result.TotalExpandedNodes,
            ["totalTransitions"] = result.TotalTransitionCount,
            ["totalChoiceBranches"] = result.TotalChoiceBranchesEvaluated,
            ["dominatedActionsPruned"] = result.DominatedActionsPruned,
            ["topQueueActionsDropped"] = result.TopQueueActionsDropped,
            ["actionAdmissionRepresentativesProtected"] = result.ActionAdmissionRepresentativesProtected,
            ["duplicateCardBranchesPruned"] = result.DuplicateCardBranchesPruned,
            ["shuffleBranchesPruned"] = result.ShuffleBranchesPruned,
            ["soldHpBranchesPruned"] = result.SoldHpBranchesPruned,
            ["primaryIncumbentBranchesPruned"] = result.PrimaryIncumbentBranchesPruned,
            ["primaryIncumbentUpdates"] = result.PrimaryIncumbentUpdates,
            ["replayCount"] = result.ReplayCount,
            ["forkCount"] = result.ForkCount,
            ["reusedNodeSnapshots"] = result.ReusedNodeSnapshots,
            ["transpositionBranchesPruned"] = result.TranspositionBranchesPruned,
            ["repeatableNoProgressBranchesPruned"] = result.RepeatableNoProgressBranchesPruned,
            ["standPatProbes"] = result.StandPatProbes,
            ["transitionCacheHits"] = result.TransitionCacheHits,
            ["maxParallelConcurrency"] = result.MaxParallelExpansionConcurrency,
            ["searchedTurns"] = result.SearchedTurns,
            ["shufflesCrossed"] = result.Snapshot.ShufflesCrossed,
            ["score"] = result.BestNode.Score,
            ["projectedBattleHpLost"] = result.ProjectedBattleHpLost,
            ["potionCount"] = result.PotionCount,
            ["onlyDeathRoutes"] = result.OnlyDeathRoutesFound,
            ["finalHp"] = result.Snapshot.PlayerHp,
            ["finalEnemyHp"] = result.Snapshot.EnemyHp,
            ["combatEndedTurn"] = result.CombatEndedTurn,
            ["transpositionCount"] = result.TranspositionCount,
            ["transpositionLimitBypasses"] = result.TranspositionLimitBypasses,
            ["standPatCacheCount"] = result.StandPatCacheCount,
        };

    public static void FlushDiagnostics()
    {
        object? logger = typeof(Entry)
            .GetProperty("Logger", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        object? journal = logger?.GetType()
            .GetProperty("Journal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(logger);
        if (journal == null)
            return;
        object? capture = journal.GetType()
            .GetMethod("CaptureAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(journal, null);
        if (capture is Task task)
            task.GetAwaiter().GetResult();
    }

    private static void SetStatic(Type type, string name, object value)
    {
        PropertyInfo? property = type.GetProperty(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(null, [value]);
            return;
        }
        FieldInfo field = type.GetField($"<{name}>k__BackingField",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
        field.SetValue(null, value);
    }
}
