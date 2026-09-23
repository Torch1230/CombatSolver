using System.Collections;
using System.Reflection;
using System.Text.Json;
using CombatSolver;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>
/// 模组侧的离线初始化：日志、固定预算设置、无人测试口径开关，以及把「搜索正确性依赖」的
/// Harmony 补丁用宿主自己的 Harmony 实例装上（游戏内是 RitsuLib 的 ModPatcher 干这件事）。
/// </summary>
internal static class ModRuntime
{
    /// <summary>搜索正确性/语义相关的补丁；UI、覆盖层、通知、观察类补丁一律不装。</summary>
    private static readonly string[] SearchPatchTypes =
    [
        "CombatSolver.CombatStateTrackerIsolationPatch",
        "CombatSolver.PowerDynamicVarMaterializationGuardPatch",
        "CombatSolver.ModelDbGetIdCachePatch",
        "CombatSolver.BaseLibCloneConcurrencyPatch",
        "CombatSolver.BaseLibDynamicVarCloneMetadataPatch",
        "CombatSolver.RitsuDynamicVarCloneMetadataPatch",
        "CombatSolver.SimulationCardPileLookupPatch",
        "CombatSolver.RitsuFreePlayVoidIsolationPatch",
        "CombatSolver.RitsuFreePlayBoolIsolationPatch",
        "CombatSolver.RitsuFreePlayResolveIsolationPatch",
        "CombatSolver.RitsuBaseLibTargetTypeLookupPatch",
        "CombatSolver.RitsuBaseLibTargetTypeResolutionPatch",
        "CombatSolver.RitsuBaseLibTargetTypeEvidencePatch",
        "CombatSolver.PowerAmountComparisonPatch",
    ];

    public static List<string> PatchLog { get; } = [];

    /// <summary>阶段表由求解器在搜索结束时发布；离线宿主把它并进结果 JSON，避免只留在异步日志里。</summary>
    public static string? LastPhasePerformance { get; private set; }

    /// <summary>离线会话作用域；释放即把无人测试口径还原（进程退出前 Program 负责释放）。</summary>
    public static IDisposable? Session { get; private set; }

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
        string cardPileFreeze = FreezeModCardPiles();
        int applied = ApplySearchPatches();

        SolverSettingsSnapshot snapshot = SolverSettings.Capture();
        return $"patches_applied={applied}/{SearchPatchTypes.Length} "
            + $"mod_card_piles={DescribeModCardPiles()} freeze={cardPileFreeze} "
            + $"profile={options.Profile} "
            + $"beam={snapshot.Profile.BeamWidth} nodes={snapshot.Profile.MaxExpandedNodes} "
            + $"branches={snapshot.Profile.MaxCardBranchesPerNode}/"
            + $"{snapshot.Profile.MaxPileChoiceBranchesPerAction}/"
            + $"{snapshot.Profile.MaxHandChoiceBranchesPerAction} "
            + $"dop={snapshot.SearchMaxDegreeOfParallelism} "
            + $"portfolio={UnattendedTestRunner.UseBeamWidthPortfolioOverride} "
            + $"no_gc={snapshot.EnableNoGcRegion} potion_policy={snapshot.PotionPolicy}";
    }

    /// <summary>
    /// 预设值来自模组自己的 <c>SolverSettings</c>（不抄常量），Custom 沿用
    /// <c>tools/fit-beam-weights/run_mac.py</c> 写进 combat_solver_settings.json 的默认分支上限 32/18/24。
    /// plan 里显式给的 beam / nodes / 分支上限再覆盖预设。
    /// </summary>
    public static SolverSearchProfile ResolveProfile(HarnessOptions options)
    {
        SolverSearchProfile baseline = options.Profile switch
        {
            "Low" => Preset(SolverPerformancePreset.Low),
            "Medium" => Preset(SolverPerformancePreset.Medium),
            "High" => Preset(SolverPerformancePreset.High),
            "VeryHigh" => Preset(SolverPerformancePreset.VeryHigh),
            // Custom 沿用模组自己写进 combat_solver_settings.json 的默认分支上限 32/18/24。
            "Custom" => new SolverSearchProfile(24, 2000, 32, 18, 24, 600_000),
            _ => throw new ArgumentException($"未知预设 {options.Profile}。"),
        };
        SolverSearchProfile profile = baseline with
        {
            BeamWidth = options.Beam ?? baseline.BeamWidth,
            MaxExpandedNodes = options.Nodes ?? baseline.MaxExpandedNodes,
            MaxCardBranchesPerNode = options.MaxCardBranchesPerNode ?? baseline.MaxCardBranchesPerNode,
            MaxPileChoiceBranchesPerAction =
                options.MaxPileChoiceBranchesPerAction ?? baseline.MaxPileChoiceBranchesPerAction,
            MaxHandChoiceBranchesPerAction =
                options.MaxHandChoiceBranchesPerAction ?? baseline.MaxHandChoiceBranchesPerAction,
        };
        return profile;
    }

    /// <summary>预设值一律问模组自己要，不在宿主里抄常量。</summary>
    private static SolverSearchProfile Preset(SolverPerformancePreset preset)
        => SolverSettings.ResolvePerformanceValues(
            new SolverSettingsData { PerformancePreset = preset }).Profile;

    /// <summary>
    /// 与 <c>run_mac.py</c> 写进 combat_solver_settings.json 的同一套口径（预设换成解析后的 profile）。
    /// </summary>
    private static void ApplyFixedBudgetSettings(HarnessOptions options)
    {
        SolverSearchProfile profile = ResolveProfile(options);
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
            EnableNoGcRegion = options.EnableNoGcRegion,
            NoGcRegionBudgetGigabytes = options.NoGcRegionBudgetGigabytes,
            UseBeamWidthPortfolio = options.UsePortfolio,
            UseNoveltyPortfolio = options.AdaptiveNoveltyRefinement,
            StopAtAcceptableBattleHpLoss = options.StopAtZeroLoss,
            OnlineStatisticsEnabled = false,
            SearchCompletionNotificationsEnabled = false,
            PotionPolicy = Enum.Parse<SolverPotionPolicy>(options.PotionPolicy, ignoreCase: true),
        });
    }

    /// <summary>
    /// 无人测试请求里 <c>fixedSearchBudget</c> / <c>searchBudgetOverrideMilliseconds</c> /
    /// <c>searchMaxDegreeOfParallelismForTest</c> / <c>useBeamWidthPortfolioForTest</c>
    /// 落在协议主机上，<c>SolverController.CaptureSearchPolicy</c> 从那里读。离线没有协议循环，
    /// 走模组自己的 <c>UnattendedTestRunner.BeginOfflineSession</c> 写同一批状态。
    /// </summary>
    private static void ApplyUnattendedOverrides(HarnessOptions options)
        => Session = UnattendedTestRunner.BeginOfflineSession(new UnattendedTestRunner.OfflineSessionOptions
        {
            FixedSearchBudget = !options.ProductionBudget,
            MeasureSearchPhases = options.MeasureSearchPhases,
            VerifyIncrementalSearch = options.VerifyIncremental,
            SearchBudgetOverrideMilliseconds = options.BudgetMilliseconds,
            SearchMaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            UseBeamWidthPortfolio = options.UsePortfolio,
            BeamWidthPortfolioPlainBaselineMember = options.NoPlainBaselineMember ? false : null,
            PileOrderInvariantMask = options.UnorderedPileMask,
            StateKeySalt = options.StateKeySalt,
            TranspositionPruningDisabledMask = options.TranspositionPruningDisabledMask,
            MemoryNoProgressRecoveryLimit = options.MemoryNoProgressRecoveryLimit,
            TranspositionEntryLimit = options.TranspositionEntryLimit,
        });

    /// <summary>
    /// 游戏里 RitsuLib 在模组注册结束后冻结牌堆注册表，<c>SimulationCardPileLookupPatch</c> 的无分配
    /// 快速路径才会生效；离线宿主没有那一步，于是每次搜索都退回原版 <c>Player.Piles</c> 的
    /// Concat + 谓词 + 枚举器分配。这里按同一时点（无任何注册牌堆）冻结，让离线指标对应实机路径。
    /// </summary>
    private static string FreezeModCardPiles()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            Type registry = typeof(STS2RitsuLib.CardPiles.ModCardPileRegistry);
            if (registry.GetProperty("IsFrozen", flags)?.GetValue(null) is true)
                return "already";
            // RitsuLib 0.111.0 的入口是 FreezeRegistrations(string)，参数是冻结发起方。
            MethodInfo? freeze = registry.GetMethod("FreezeRegistrations", flags, null, [typeof(string)], null);
            if (freeze == null)
                return "no-FreezeRegistrations";
            freeze.Invoke(null, ["OfflineSearchHarness"]);
            return "frozen=" + (registry.GetProperty("IsFrozen", flags)?.GetValue(null) ?? "?");
        }
        catch (Exception error)
        {
            Exception root = error;
            while (root.InnerException != null) root = root.InnerException;
            return $"failed:{root.GetType().Name}:{root.Message}";
        }
    }

    private static int ApplySearchPatches()
    {
        int applied = 0;
        foreach (string typeName in SearchPatchTypes)
        {
            Type? patchType = AccessTools.TypeByName(typeName);
            if (patchType == null)
            {
                PatchLog.Add($"{typeName}: 缺少类型，跳过");
                continue;
            }
            try
            {
                object? targets = AccessTools.Method(patchType, "GetTargets")?.Invoke(null, null);
                if (targets is not IEnumerable list)
                {
                    PatchLog.Add($"{typeName}: GetTargets 无结果，跳过");
                    continue;
                }
                HarmonyMethod? prefix = Wrap(patchType, "Prefix");
                HarmonyMethod? postfix = Wrap(patchType, "Postfix");
                HarmonyMethod? transpiler = Wrap(patchType, "Transpiler");
                HarmonyMethod? finalizer = Wrap(patchType, "Finalizer");
                int patched = 0;
                foreach (object target in list)
                {
                    MethodBase? method = Resolve(target);
                    if (method == null)
                        continue;
                    GameBootstrap.Harmony.Patch(method, prefix, postfix, transpiler, finalizer);
                    patched++;
                }
                PatchLog.Add($"{typeName}: 已装 {patched} 个目标");
                if (patched > 0)
                    applied++;
            }
            catch (Exception error)
            {
                Exception root = error;
                while (root.InnerException != null) root = root.InnerException;
                PatchLog.Add($"{typeName}: 失败 {root.GetType().Name}: {root.Message}");
            }
        }
        return applied;
    }

    private static HarmonyMethod? Wrap(Type patchType, string name)
    {
        MethodInfo? method = patchType.GetMethod(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return method == null ? null : new HarmonyMethod(method);
    }

    private static MethodBase? Resolve(object target)
    {
        Type type = target.GetType();
        Type targetType = (Type)type.GetProperty("TargetType")!.GetValue(target)!;
        string methodName = (string)type.GetProperty("MethodName")!.GetValue(target)!;
        Type[] parameterTypes = (Type[]?)type.GetProperty("ParameterTypes")?.GetValue(target) ?? [];
        object? methodType = type.GetProperty("HarmonyMethodType")?.GetValue(target);
        if (methodType is MethodType.Getter)
            return AccessTools.PropertyGetter(targetType, methodName);
        if (methodType is MethodType.Setter)
            return AccessTools.PropertySetter(targetType, methodName);
        if (methodType is MethodType.Constructor)
            return AccessTools.Constructor(targetType, parameterTypes);
        return AccessTools.Method(targetType, methodName, parameterTypes.Length == 0 ? null : parameterTypes)
            ?? AccessTools.Method(targetType, methodName);
    }

    public static string DescribeStart(CombatState state)
        => SolverDiagnostics.DescribeStart(state, SolverSettings.Capture().Profile);

    /// <summary>把 CombatRootSnapshot.Capture 的各步单独跑一遍，用来定位段错误落在哪一步。</summary>
    private static void ProbeRootCapture(CombatState state)
    {
        HarnessLog.Trace("probe.power_dynamic_vars");
        PowerDynamicVarWarmup.EnsureMaterialized(state);
        HarnessLog.Trace("probe.card_dynamic_vars");
        CardDynamicVarWarmup.EnsureMaterialized(state);
        HarnessLog.Trace("probe.continuation_stamp");
        _ = ContinuationStamp.CaptureLive(state);
        HarnessLog.Trace("probe.hook_listeners");
        var listeners = state.IterateHookListeners().ToArray();
        HarnessLog.Trace($"probe.intent_forecast listeners={listeners.Length}");
        _ = CombatSolver.IntentForecaster.Build(state, SolverWeights.SetupValueHorizonTurns);
        HarnessLog.Trace("probe.simulated_combat_state");
        _ = new SimulatedCombatState(state, listeners);
        HarnessLog.Trace("probe.done");
    }

    /// <summary>宿主一次搜索的全部产物。</summary>
    /// <summary>与游戏内 <c>replayVerification.beamWeightFit.policy</c> 同一组字段，方便逐项对账。</summary>
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
        policy.UseBeamWidthPortfolio,
        policy.BeamWidthPortfolioPlainBaselineMember,
        policy.UseNoveltyPortfolio,
        policy.BeamWidthPortfolioWidths,
        portfolioSelector = policy.PortfolioExperiment?.Model?.ModelId,
        observePortfolio = policy.PortfolioExperiment?.Observe != null,
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

    /// <summary>
    /// Evaluate 口径：单次求解，不经协调器——直接建一个 <c>CombatBeamSolver</c>，用
    /// <c>SearchRequestWorkTotals</c> 回填 Total* 字段，profile 的软时间预算换成 <c>--budget-ms</c>。
    /// 组合成员、协调器的审计通道都不参与，所以它量的是「一棵树自己」的搜索量。
    /// </summary>
    private static SolverResult SolveEvaluate(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot basePolicy,
        SolverSettingsSnapshot settings,
        int budgetMilliseconds,
        MainLoopContext loop,
        out object describedPolicy)
    {
        SearchRequestWorkTotals totals = new();
        SearchPolicySnapshot policy = basePolicy with
        {
            RequestWorkTotals = totals,
            Profile = basePolicy.Profile with { SoftTimeBudgetMilliseconds = budgetMilliseconds },
        };
        describedPolicy = DescribePolicy(policy);
        CombatBeamSolver solver = new(
            root, names, damage, policy, searchProfile: policy.Profile);
        // 与参考跑法一致：求解在工作线程上跑，主线程只泵消息循环。
        Task<SolverResult> solve = Task.Run(solver.Solve);
        loop.RunUntilCompleted(solve, TimeSpan.FromSeconds(660), "CombatBeamSolver.Solve");
        SolverResult result = solve.GetAwaiter().GetResult();
        if (policy.MeasurePhasePerformance)
        {
            string phasePerformance = SolverDiagnostics.DescribeSearchPhasePerformance(result);
            LastPhasePerformance = phasePerformance;
            policy.Diagnostics.Info(phasePerformance);
        }
        SearchRequestWorkSnapshot work = totals.Snapshot();
        result.TotalExpandedNodes = work.ExpandedNodes;
        result.TotalTransitionCount = work.TransitionCount;
        result.TotalChoiceBranchesEvaluated = work.ChoiceBranchesEvaluated;
        result.TotalSearchElapsed = work.Elapsed;
        return result;
    }

    public static SearchOutcome RunSearchDetailed(
        CombatState state,
        HarnessOptions options,
        MainLoopContext loop,
        UnattendedTestRunner.OfflineScenarioSession? session)
    {
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_PROBE_ROOT") == "1")
            ProbeRootCapture(state);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
        HarnessLog.Trace("root_captured");
        SolverDisplayNames names = SolverDisplayNames.Capture(state);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(state);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, state, includeTurnSetup: false, theftPolicy: null);
        policy = policy with { Profile = policy.Profile with
        {
            BaseScoreOnly = options.Ordering == "base",
            SecondRankBand = options.Ordering == "band",
            ContextualRanking = options.RankingModelPath == null ? null
                : ContextualRankingModel.Parse(File.ReadAllText(options.RankingModelPath)),
            ContinuousThreatRanking = options.ContinuousThreatRanking,
            BaseScoreTacticalTies = options.BaseScoreTacticalTies,
            AdaptiveNoveltyRefinement = options.AdaptiveNoveltyRefinement,
            ReallocatedRefinementPortfolio = options.ReallocatedRefinementPortfolio
                ?? policy.Profile.ReallocatedRefinementPortfolio,
            BeamWeightPerturbation = options.BeamWeightPerturbation,
            OffensiveRefinementPortfolio = options.OffensiveRefinementPortfolio,
            BoundedOffensiveRefinementPortfolio = options.BoundedOffensiveRefinementPortfolio
                ?? policy.Profile.BoundedOffensiveRefinementPortfolio,
            StopPortfolioAtHpTarget = options.StopPortfolioAtHpTarget ?? policy.Profile.StopPortfolioAtHpTarget,
        } };
        using OrderingObservations? orderingObservations = options.OrderingObservationLimit > 0
            ? new OrderingObservations(options.OutputDirectory, options.OrderingObservationLimit,
                options.OrderingWatchedStatesPath) : null;
        if (orderingObservations != null)
            policy = policy with { Diagnostics = new SearchDiagnosticsSink(
                policy.Diagnostics.Info, policy.Diagnostics.Debug, orderingObservations.Observer) };
        List<BeamPortfolioObservation> observations = [];
        if (options.ObservePortfolio || options.PortfolioModelPath != null)
        {
            BeamPortfolioSelector? model = options.PortfolioModelPath == null ? null
                : BeamPortfolioSelector.Parse(File.ReadAllText(options.PortfolioModelPath));
            policy = policy with
            {
                PortfolioExperiment = new BeamPortfolioExperiment(model, observation =>
                {
                    if (observations.Count >= 4096)
                        throw new InvalidOperationException("Portfolio observation limit exceeded.");
                    observations.Add(observation);
                }),
            };
        }
        HarnessLog.Trace("search_policy");
        Action<SolverProgress>? diagnosticProgress = null;
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_STREAM_DIAGNOSTICS") == "1")
        {
            SearchDiagnosticsSink original = policy.Diagnostics;
            policy = policy with { Diagnostics = new SearchDiagnosticsSink(message =>
            {
                Console.WriteLine($"[search-diagnostic] {message}");
                original.Info(message);
            }, original.Debug, original.PathObserver) };
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
        ProgressRetirementProbe? retirementProbe = null;
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_RETIRE_PROGRESS_AFTER_NODES") is { } stopNodes)
        {
            if (options.SearchMode != "Coordinator" || options.EnableNoGcRegion)
                throw new ArgumentException("Progress retirement requires Coordinator with normal GC.");
            retirementProbe = new ProgressRetirementProbe(int.Parse(stopNodes));
            policy = policy with { Interaction = retirementProbe.Interaction };
            diagnosticProgress += retirementProbe.Observe;
        }
        SearchTimeBoundaryObserver timeBoundaryObserver = new();
        policy = policy with { Diagnostics = timeBoundaryObserver.Wrap(policy.Diagnostics) };
        bool timeBoundary = false;
        object describedPolicy = DescribePolicy(policy);
        SolverResult result;
        if (options.EnableNoGcRegion)
        {
            if (options.SignalBallastMegabytes > 0)
            {
                // 先收掉建局阶段的可回收对象，让球压测试只面对 scope 开头之后分配的对象。
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            }
            using ISearchGcScope gcScope = SearchGcPolicy.EnterSearchScope(
                enableNoGcRegion: true,
                settings.NoGcRegionBudgetBytes,
                policy.MemoryPressureSignal,
                CancellationToken.None);
            byte[]? ballast = options.SignalBallastMegabytes > 0
                ? new byte[options.SignalBallastMegabytes * 1024L * 1024L]
                : null;
            try
            {
                result = options.SearchMode == "Coordinator"
                    ? CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, diagnosticProgress)
                    : SolveEvaluate(root, names, damage, policy, settings,
                        options.BudgetMilliseconds, loop, out describedPolicy);
            }
            finally
            {
                GC.KeepAlive(ballast);
            }
        }
        else
        {
            result = options.SearchMode == "Coordinator"
                ? CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, diagnosticProgress)
                : SolveEvaluate(root, names, damage, policy, settings,
                    options.BudgetMilliseconds, loop, out describedPolicy);
        }
        if (retirementProbe != null)
            result = retirementProbe.Finish(result, options.OutputDirectory);
        timeBoundaryObserver.ObserveSelectedResult(result.BoundaryReason);
        timeBoundary = timeBoundaryObserver.TimeBoundaryObserved;
        if (options.SearchMode == "Coordinator" && policy.MeasurePhasePerformance)
            LastPhasePerformance = SolverDiagnostics.DescribeSearchPhasePerformance(result);
        orderingObservations?.WriteSelectedPath(options.OutputDirectory, result);
        HarnessLog.Trace("solved");
        watch.Stop();
        File.WriteAllText(Path.Combine(options.OutputDirectory, "quality.json"), JsonSerializer.Serialize(new
        {
            quality = CombatSearchCoordinator.CapturePortfolioQuality(root, policy, result),
            snapshot = result.Snapshot,
            result.ResultScope,
            result.BoundaryReason,
        }, UnattendedTestFiles.JsonOptions));
        if (policy.PortfolioExperiment != null)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "portfolio-observations.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = BeamPortfolioSelector.SchemaVersion,
                    solverAssemblyId = typeof(CombatSearchCoordinator).Module.ModuleVersionId.ToString(),
                    gameAssemblyId = typeof(CombatState).Module.ModuleVersionId.ToString(),
                    featureNames = BeamPortfolioSelector.FeatureNames,
                    observations,
                }, UnattendedTestFiles.JsonOptions));
        }

        Dictionary<string, object?> rootCapture = new()
        {
            ["cards"] = root.CapturedCardCount,
            ["powers"] = root.CapturedPowerCount,
            ["listeners"] = root.CapturedHookListenerCount,
            ["run_mod_subscribers"] = root.CapturedRunModSubscriberCount,
            ["combat_mod_subscribers"] = root.CapturedCombatModSubscriberCount,
            ["base_lib_card_modifiers"] = root.CapturedBaseLibCardModifiers,
        };
        return new SearchOutcome(
            result,
            session?.CaptureSolverMetrics(result),
            describedPolicy,
            BuildLegacyMetrics(result),
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

    private static Dictionary<string, object?> BuildLegacyMetrics(SolverResult result)
    {

        // 键名与游戏内 result.json 的 solverMetrics 保持一致，方便逐字段比。耗时/内存/GC 这类
        // 与环境相关的字段不放进来（固定节点预算下结果与时间无关）。
        Dictionary<string, object?> metrics = new()
        {
            ["boundary"] = result.BoundaryReason.ToString(),
            ["selectedExpanded"] = result.ExpandedNodes,
            ["selectedTransitions"] = result.TransitionCount,
            ["selectedChoiceBranches"] = result.ChoiceBranchesEvaluated,
            ["totalExpanded"] = result.TotalExpandedNodes,
            ["totalTransitions"] = result.TotalTransitionCount,
            ["totalChoiceBranches"] = result.TotalChoiceBranchesEvaluated,
            ["choiceReplayAttempts"] = result.ChoiceReplayAttempts,
            ["choiceReplayBudgetExhaustions"] = result.ChoiceReplayBudgetExhaustions,
            ["choiceBranchesDroppedByBudget"] = result.ChoiceBranchesDroppedByBudget,
            ["roundReplayPrefixCaptures"] = result.RoundReplayPrefixCaptures,
            // 剪枝/复用计数：量"重复模拟"与各类剪枝占转移数的比例。
            ["dominatedActionsPruned"] = result.DominatedActionsPruned,
            ["topQueueActionsDropped"] = result.TopQueueActionsDropped,
            ["actionAdmissionRepresentativesProtected"] = result.ActionAdmissionRepresentativesProtected,
            ["duplicateCardBranchesPruned"] = result.DuplicateCardBranchesPruned,
            ["shuffleBranchesPruned"] = result.ShuffleBranchesPruned,
            ["soldHpBranchesPruned"] = result.SoldHpBranchesPruned,
            ["replayCount"] = result.ReplayCount,
            ["forkCount"] = result.ForkCount,
            ["reusedNodeSnapshots"] = result.ReusedNodeSnapshots,
            ["transpositionBranchesPruned"] = result.TranspositionBranchesPruned,
            ["repeatableNoProgressBranchesPruned"] = result.RepeatableNoProgressBranchesPruned,
            ["standPatProbes"] = result.StandPatProbes,
            ["transitionCacheHits"] = result.TransitionCacheHits,
            ["roundReplayPrefixReuses"] = result.RoundReplayPrefixReuses,
            ["cycleRegionsDetected"] = result.CycleRegionsDetected,
            ["cycleRegionCandidatesConsidered"] = result.CycleRegionCandidatesConsidered,
            ["cycleRegionCandidatesAdmitted"] = result.CycleRegionCandidatesAdmitted,
            ["cycleRegionCandidatesDropped"] = result.CycleRegionCandidatesDropped,
            ["cycleRegionProgressEpochs"] = result.CycleRegionProgressEpochs,
            ["cycleRegionProbeCandidatesAdmitted"] = result.CycleRegionProbeCandidatesAdmitted,
            ["cycleRegionProgressCandidatesAdmitted"] = result.CycleRegionProgressCandidatesAdmitted,
            ["cycleRegionMaxActionFamilies"] = result.CycleRegionMaxActionFamilies,
            ["orderedMutationCandidatesAdmitted"] = result.OrderedMutationCandidatesAdmitted,
            ["orderedMutationLeaseExpiredBudget"] = result.OrderedMutationLeaseExpiredBudget,
            ["orderedMutationOrdinaryFallbacks"] = result.OrderedMutationOrdinaryFallbacks,
            ["orderedMutationColdAtomicCommitted"] = result.OrderedMutationColdAtomicCommitted,
            ["orderedMutationColdAtomicRejected"] = result.OrderedMutationColdAtomicRejected,
            ["deferredRoundChoiceActions"] = result.DeferredRoundChoiceActions,
            ["deferredRoundChoiceLayerWidthTotal"] = result.DeferredRoundChoiceLayerWidthTotal,
            ["maxDeferredRoundChoiceLayerWidth"] = result.MaxDeferredRoundChoiceLayerWidth,
            ["deferredRoundChoiceFiniteQuotaFallbacks"] = result.DeferredRoundChoiceFiniteQuotaFallbacks,
            ["deferredRoundChoiceFinitePrimaryLayers"] = result.DeferredRoundChoiceFinitePrimaryLayers,
            ["deferredRoundChoiceFinitePendingFallbacks"] = result.DeferredRoundChoiceFinitePendingFallbacks,
            ["parallelActionReplayWaves"] = result.ParallelActionReplayWaves,
            ["parallelActionReplayWorkItems"] = result.ParallelActionReplayWorkItems,
            ["maxParallelActionReplayConcurrency"] = result.MaxParallelActionReplayConcurrency,
            ["parallelRoundChoiceReplayWaves"] = result.ParallelRoundChoiceReplayWaves,
            ["parallelRoundChoiceReplayWorkItems"] = result.ParallelRoundChoiceReplayWorkItems,
            ["maxParallelRoundChoiceReplayConcurrency"] = result.MaxParallelRoundChoiceReplayConcurrency,
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
            ["expandedTranspositionCount"] = result.ExpandedTranspositionCount,
            ["transpositionLimitBypasses"] = result.TranspositionLimitBypasses,
            ["standPatCacheCount"] = result.StandPatCacheCount,
            ["threatProjectionCacheCount"] = result.ThreatProjectionCacheCount,
            ["coverageCacheCount"] = result.CoverageCacheCount,
        };
        return metrics;
    }

    /// <summary>
    /// 模组日志由后台写线程持有；进程直接退出会丢掉尚未落盘的最后一段（阶段表恰好在末尾）。
    /// 用该日志自己的 FIFO 快照屏障等所有已入队条目写完，再结束进程。
    /// </summary>
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

    /// <summary>
    /// <c>SimulationCardPileLookupPatch</c> 的快速路径只在 RitsuLib 的模组牌堆注册表冻结、且没有任何
    /// 注册牌堆时生效。游戏启动流程会冻结它，离线宿主没有这一步；这里把真实取值报出来，避免把
    /// 「宿主没冻结」误读成搜索分配。
    /// </summary>
    private static string DescribeModCardPiles()
    {
        try
        {
            Type registry = typeof(STS2RitsuLib.CardPiles.ModCardPileRegistry);
            object? frozen = registry
                .GetProperty("IsFrozen", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            object? definitions = registry
                .GetMethod("GetDefinitionsSnapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(null, null);
            int count = definitions is System.Collections.ICollection collection ? collection.Count : -1;
            return $"frozen={frozen ?? "?"} definitions={count}";
        }
        catch (Exception error)
        {
            return $"probe_failed:{error.GetType().Name}";
        }
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
