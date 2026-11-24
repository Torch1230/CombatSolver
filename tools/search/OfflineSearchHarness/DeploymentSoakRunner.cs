using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

/// <summary>
/// 镜像 <c>NativeChoiceRuntime.ResolvePlannedCards</c> 的纯 .NET 计划驱动选牌器：
/// 按 <c>OptionOccurrence</c>、<c>CardId</c>、<c>UpgradeLevel</c> 及（若提供完整键时的）<c>StateKey</c>
/// 精确选择计划中的卡牌，失配时抛出 <see cref="NativeChoicePlanMismatchException"/>。
/// </summary>
internal sealed class PlanDrivenCardSelector(IReadOnlyList<PlanCardChoice> plans) : ICardSelector
{
    private readonly IReadOnlyList<PlanCardChoice> _plans = plans;
    private int _planIndex;

    public Exception? LastError { get; private set; }

    private NativeChoicePlanMismatchException Fail(string message)
    {
        NativeChoicePlanMismatchException ex = new(message);
        LastError ??= ex;
        return ex;
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        List<CardModel> optList = options.ToList();

        if (_planIndex >= _plans.Count)
        {
            throw Fail(
                $"计划驱动选牌已耗尽（共 {_plans.Count} 个计划），实机仍请求从 {optList.Count} 张候选中选择 {minSelect}..{maxSelect} 张。");
        }

        PlanCardChoice plan = _plans[_planIndex];
        if (TryResolvePlan(plan, optList, minSelect, maxSelect, out List<CardModel> selected, out string? error))
        {
            _planIndex++;
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        // 原版 CardSelectCmd 在 options.Count <= minSelect 且无需手动确认时会隐式自动选择而不调用 ICardSelector。
        // 若上一条单卡计划因隐式自动选择被跳过，且紧邻下一条计划精确命中当前候选窗口，则对齐到下一条计划。
        if (plan.Cards.Count == 1 && _planIndex + 1 < _plans.Count)
        {
            PlanCardChoice nextPlan = _plans[_planIndex + 1];
            if (TryResolvePlan(nextPlan, optList, minSelect, maxSelect, out List<CardModel> nextSelected, out _))
            {
                _planIndex += 2;
                return Task.FromResult<IEnumerable<CardModel>>(nextSelected);
            }
        }

        throw Fail(error ?? "计划选牌解析失败。");
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => new() { card = options.Count > 0 ? options[0].Card : null };

    private static bool TryResolvePlan(
        PlanCardChoice plan,
        List<CardModel> optList,
        int minSelect,
        int maxSelect,
        out List<CardModel> selected,
        out string? error)
    {
        selected = new List<CardModel>(plan.Cards.Count);
        error = null;

        if (plan.Cards.Count == 0)
        {
            if (minSelect == 0)
                return true;
            error = $"计划选择 0 张牌，但实机选牌要求至少选择 {minSelect} 张（source={plan.SourceId}/{plan.Effect}）。";
            return false;
        }

        foreach (PlanCardToken token in plan.Cards)
        {
            CardModel? card = optList
                .Where(option => string.Equals(option.Id.Entry, token.CardId, StringComparison.Ordinal)
                    && option.CurrentUpgradeLevel == token.UpgradeLevel)
                .Skip(token.OptionOccurrence)
                .FirstOrDefault();

            if (card == null)
            {
                string currentOpts = string.Join(", ", optList.Select(static o => HotImportSupport.FormatLiveChoiceCardKey(o)));
                error = $"计划选牌找不到 {token.CardId}+{token.UpgradeLevel}#opt{token.OptionOccurrence} " +
                    $"(source={plan.SourceId}/{plan.Effect})；当前候选=[{currentOpts}]。";
                return false;
            }

            if (!string.IsNullOrEmpty(token.StateKey) && token.StateKey.Contains('|'))
            {
                string actualKey = HotImportSupport.FormatLiveChoiceCardKey(card);
                if (!string.Equals(actualKey, token.StateKey, StringComparison.Ordinal))
                {
                    error = $"计划选牌 StateKey 失配 {token.CardId}+{token.UpgradeLevel}#opt{token.OptionOccurrence}：" +
                        $"expected={token.StateKey} actual={actualKey}。";
                    return false;
                }
            }

            if (selected.Contains(card))
            {
                error = $"计划选牌重复选中同一实例 {token.CardId}+{token.UpgradeLevel}#opt{token.OptionOccurrence}。";
                return false;
            }

            selected.Add(card);
        }

        if (selected.Count < minSelect || selected.Count > maxSelect)
        {
            error = $"计划选牌数量 {selected.Count} 超出实机要求范围 {minSelect}..{maxSelect}（source={plan.SourceId}/{plan.Effect}）。";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Layer 3 整场求解→部署反重算压测引擎（<c>--soak-deploy</c>）：
/// 在纯 .NET 实机战斗上跑 N 场（默认 60 场，覆盖全 6 角色 × Standard/Dread/Blitz + 多样遗物/药水/遭遇），
/// 第 1 回合（及缓存未命中时）调用 HotSearchCoordinator.Solve（Smart 药水策略 + DOP=8），
/// 按生产部署逻辑逐动作部署整回合，并在下一回合断言 ContinuationStamp 精确命中缓存（目标 0 计划外重算、0 硬错）。
/// </summary>
internal static class DeploymentSoakRunner
{
    private static readonly string[] AllCharacters =
        ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "FOOL"];

    private sealed record SoakReplanSample(
        int FightIndex,
        string Tag,
        int Turn,
        string Cause,
        string FirstDiff);

    private sealed record SoakErrorSample(
        int FightIndex,
        string Tag,
        int Turn,
        string ExceptionType,
        string Message);

    public static int Run(string[] args)
    {
        int fights = 60;
        int seed = 2026;
        string profile = "Medium";
        int dop = 8;
        int budgetMs = 15_000;
        string repoRoot = ResolveRepoRoot();
        string outPath = Path.Combine(repoRoot, ".local", "offline-harness", "soak-deploy.json");
        string? dumpOutcomesPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--soak-deploy 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--fights": fights = Math.Max(1, int.Parse(NextVal())); break;
                case "--seed": seed = int.Parse(NextVal()); break;
                case "--profile": profile = NextVal(); break;
                case "--dop": dop = Math.Max(1, int.Parse(NextVal())); break;
                case "--budget-ms": budgetMs = Math.Max(500, int.Parse(NextVal())); break;
                case "--out": outPath = Path.GetFullPath(NextVal()); break;
                case "--dump-outcomes": dumpOutcomesPath = Path.GetFullPath(NextVal()); break;
                default: throw new ArgumentException($"--soak-deploy 未知选项：{key}");
            }
        }
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh"))
            throw new ArgumentException("--profile 只支持 Low|Medium|High|VeryHigh。");

        string outDir = Path.GetDirectoryName(outPath)!;
        Directory.CreateDirectory(outDir);

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine($"=== --soak-deploy (fights={fights}, seed={seed}, profile={profile}, dop={dop}, budgetMs={budgetMs}, out={outPath}) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = new()
        {
            Profile = profile,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = "Smart",
            SearchMode = "Coordinator",
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", "soak-deploy-logs"),
        };
        GameBootstrap.EnsureBootstrapped(loop, modOptions);

        List<(string Tag, CombatSetupSpec Setup)> scenarios = BuildSoakScenarios(fights, seed);

        int gen2Before = GC.CollectionCount(2);
        using MemorySampler memory = new(TimeSpan.FromMilliseconds(150));

        int wins = 0;
        int defeats = 0;
        int hardErrorCount = 0;
        int unplannedReplanCount = 0;
        int plannedBoundaryReplanCount = 0;
        int choiceMismatchCount = 0;
        int continuationsReused = 0;
        int totalTurnsDeployed = 0;
        int totalSolves = 0;
        long totalHpLoss = 0;
        int totalPotionsUsed = 0;
        int completedFightsForStats = 0;
        List<double> solveTimesMs = new(fights * 2);
        List<SoakReplanSample> replanSamples = [];
        List<SoakErrorSample> errorSamples = [];
        List<FightOutcomeRecord> fightOutcomes = new(fights);

        bool customBeam135Stage2Ok = false;
        try
        {
            for (int f = 0; f < scenarios.Count; f++)
            {
                (string tag, CombatSetupSpec setup) = scenarios[f];
                FightOutcomeRecord outcomeRecord = RunSingleSoakFight(
                    loop, f, tag, setup,
                    ref wins, ref defeats, ref hardErrorCount,
                    ref unplannedReplanCount, ref plannedBoundaryReplanCount,
                    ref choiceMismatchCount, ref continuationsReused,
                    ref totalTurnsDeployed, ref totalSolves,
                    ref totalHpLoss, ref totalPotionsUsed, ref completedFightsForStats,
                    solveTimesMs, replanSamples, errorSamples);
                fightOutcomes.Add(outcomeRecord);

                if ((f + 1) % 10 == 0 || f + 1 == scenarios.Count)
                {
                    Console.WriteLine(
                        $"[soak] {f + 1}/{scenarios.Count} | wins={wins} defeats={defeats} errors={hardErrorCount} | " +
                        $"unplanned_replans={unplannedReplanCount} choice_mismatches={choiceMismatchCount} " +
                        $"reused={continuationsReused} | {totalSw.Elapsed.TotalSeconds:F1}s");
                }
            }

            // 专项回归（#136）：Custom baseBeamWidth=135 & 180 (>100 / >160) 下 Stage 2 药水搜索不得抛 Math.Clamp 异常
            customBeam135Stage2Ok = RunCustomBeamStage2Regression(loop, dop, ref hardErrorCount, errorSamples);
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        memory.Dispose();
        int gcGen2 = GC.CollectionCount(2) - gen2Before;
        long peakRssBytes = Math.Max(memory.PeakWorkingSetBytes, memory.PeakProcessWorkingSetBytes);
        double peakRssMb = Math.Round(peakRssBytes / (1024.0 * 1024.0), 2);

        double hardErrorRate = fights > 0 ? (double)hardErrorCount / fights : 0.0;
        double unplannedReplanRate = totalTurnsDeployed > 0 ? (double)unplannedReplanCount / totalTurnsDeployed : 0.0;
        double winRate = fights > 0 ? (double)wins / fights : 0.0;
        double avgHpLoss = completedFightsForStats > 0 ? (double)totalHpLoss / completedFightsForStats : 0.0;
        double avgPotionsUsed = completedFightsForStats > 0 ? (double)totalPotionsUsed / completedFightsForStats : 0.0;
        double p50SolveMs = Percentile(solveTimesMs, 0.50);
        double p95SolveMs = Percentile(solveTimesMs, 0.95);
        BenchmarkSummaryRecord outcomeSummary = BenchmarkOutcomeHelper.BuildSummary(
            fightOutcomes, totalSw.Elapsed.TotalMilliseconds, peakRssMb, gcGen2, solveTimesMs);

        if (dumpOutcomesPath != null)
        {
            BenchmarkOutcomeHelper.WriteOutcomeFile(dumpOutcomesPath, new BenchmarkOutcomeFile
            {
                Suite = "soak-deploy",
                Profile = profile,
                SearchMode = "Coordinator",
                PotionPolicy = "Smart",
                Dop = dop,
                Seed = seed,
                BudgetMs = budgetMs,
                Summary = outcomeSummary,
                Outcomes = fightOutcomes.ToArray(),
            });
        }

        object report = new
        {
            fights,
            seed,
            profile,
            dop,
            budget_ms = budgetMs,
            elapsed_seconds = Math.Round(totalSw.Elapsed.TotalSeconds, 2),
            wins,
            defeats,
            hard_error_count = hardErrorCount,
            hard_error_rate = Math.Round(hardErrorRate, 4),
            unplanned_replan_count = unplannedReplanCount,
            unplanned_replan_rate = Math.Round(unplannedReplanRate, 4),
            planned_boundary_replans = plannedBoundaryReplanCount,
            continuations_reused = continuationsReused,
            total_turns_deployed = totalTurnsDeployed,
            total_solves = totalSolves,
            choice_mismatch_count = choiceMismatchCount,
            win_rate = Math.Round(winRate, 4),
            avg_hp_loss = Math.Round(avgHpLoss, 2),
            avg_potions_used = Math.Round(avgPotionsUsed, 2),
            avg_economic_value_v = outcomeSummary.AvgEconomicValueV,
            p50_solve_ms = Math.Round(p50SolveMs, 1),
            p95_solve_ms = Math.Round(p95SolveMs, 1),
            peak_rss_mb = peakRssMb,
            gc_gen2 = gcGen2,
            custom_beam135_stage2_ok = customBeam135Stage2Ok,
            outcomes_path = dumpOutcomesPath,
            replan_samples = replanSamples.Take(100).ToArray(),
            error_samples = errorSamples.Take(25).ToArray(),
        };

        File.WriteAllText(outPath, JsonSerializer.Serialize(report, FuzzScenarioGenerator.JsonOptions));

        Console.WriteLine();
        Console.WriteLine("=== --soak-deploy BASELINE SUMMARY ===");
        Console.WriteLine($"fights={fights} wins={wins} defeats={defeats} win_rate={winRate:P1}");
        Console.WriteLine($"hard_error_count={hardErrorCount} hard_error_rate={hardErrorRate:P2} choice_mismatch_count={choiceMismatchCount} custom_beam135_stage2_ok={customBeam135Stage2Ok}");
        Console.WriteLine($"unplanned_replan_count={unplannedReplanCount} unplanned_replan_rate={unplannedReplanRate:P2} (reused={continuationsReused}, planned_boundary={plannedBoundaryReplanCount}, turns={totalTurnsDeployed})");
        Console.WriteLine($"avg_hp_loss={avgHpLoss:F2} avg_potions_used={avgPotionsUsed:F2} avg_economic_value_v={outcomeSummary.AvgEconomicValueV:F2} p50_solve_ms={p50SolveMs:F1} p95_solve_ms={p95SolveMs:F1}");
        Console.WriteLine($"peak_rss_mb={peakRssMb:F1} gc_gen2={gcGen2} elapsed={totalSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"baseline_json={outPath}");
        if (dumpOutcomesPath != null)
            Console.WriteLine($"outcomes_json={dumpOutcomesPath}");

        if (replanSamples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Top unplanned replans ({replanSamples.Count}):");
            foreach (SoakReplanSample s in replanSamples.Take(10))
                Console.WriteLine($"  fight#{s.FightIndex} ({s.Tag}) T{s.Turn} [{s.Cause}]: {s.FirstDiff}");
        }
        if (errorSamples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Top hard errors ({errorSamples.Count}):");
            foreach (SoakErrorSample e in errorSamples.Take(10))
                Console.WriteLine($"  fight#{e.FightIndex} ({e.Tag}) T{e.Turn} [{e.ExceptionType}]: {e.Message}");
        }

        return (hardErrorCount == 0 && unplannedReplanCount == 0 && choiceMismatchCount == 0 && customBeam135Stage2Ok) ? 0 : 1;
    }

    private static FightOutcomeRecord RunSingleSoakFight(
        MainLoopContext loop,
        int fightIndex,
        string tag,
        CombatSetupSpec setup,
        ref int wins,
        ref int defeats,
        ref int hardErrorCount,
        ref int unplannedReplanCount,
        ref int plannedBoundaryReplanCount,
        ref int choiceMismatchCount,
        ref int continuationsReused,
        ref int totalTurnsDeployed,
        ref int totalSolves,
        ref long totalHpLoss,
        ref int totalPotionsUsed,
        ref int completedFightsForStats,
        List<double> solveTimesMs,
        List<SoakReplanSample> replanSamples,
        List<SoakErrorSample> errorSamples)
    {
        int currentTurn = 0;
        int replansBeforeFight = unplannedReplanCount;
        int choiceMismatchesBeforeFight = choiceMismatchCount;
        string group = $"{setup.CharacterId.ToLowerInvariant()}_{setup.GameMode.ToString().ToLowerInvariant()}";
        int startHp = setup.PlayerHp ?? 80;
        int startMaxHp = setup.PlayerMaxHp ?? 80;
        double fightSolveMs = 0.0;
        int fightSolveCount = 0;
        long fightExpandedNodes = 0;
        long fightTransitions = 0;
        string fightLastBoundary = "None";
        int fightPotionsUsed = 0;
        int alchemizeCount = 0;
        int rootOptionalPaidRawHpCost = 0;
        int rootOptionalPaidCount = 0;
        ushort rootOccupiedMask = 0;
        ushort rootConsumedMask = 0;
        short[] rootSlotCosts = [];
        HotSearchPolicy rootHotPolicy = default;
        bool hasRootHotPolicy = false;
        SolverTheftPolicy? fightTheftPolicy = null;

        try
        {
            BattleDamageTracker.Reset();
            CombatState combat = RealCombatDriver.StartCombat(loop, setup);
            Player player = LocalContext.GetMe(combat)!;
            startHp = player.Creature.CurrentHp;
            startMaxHp = player.Creature.MaxHp;
            int initialUnusedLizardTails = player.Relics
                .OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>()
                .Count(static t => !t.WasUsed);
            int initialFairyCount = player.PotionSlots
                .Count(static p => p != null && string.Equals(p.Id.Entry, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));

            SolverResult? activePlan = null;
            const int maxTurns = 35;

            for (int turnLoop = 0; turnLoop < maxTurns; turnLoop++)
            {
                HotCombatOutcome outcome = RealCombatDriver.GetLiveOutcome(combat);
                if (outcome != HotCombatOutcome.Ongoing)
                    break;

                currentTurn = player.PlayerCombatState!.TurnNumber;
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                ContinuationStamp liveStamp = ContinuationStamp.CaptureLive(combat);

                if (activePlan != null)
                {
                    if (activePlan.TryCreateContinuation(liveStamp, player.Creature.CurrentHp, damage, out SolverResult? reused)
                        && reused != null)
                    {
                        activePlan = reused;
                        continuationsReused++;
                        BattleDamageTracker.RegisterPlan(combat, activePlan);
                    }
                    else
                    {
                        CachedContinuation? expected = activePlan.Continuations
                            .FirstOrDefault(c => c.StartTurnNumber == currentTurn);
                        if (expected != null)
                        {
                            string firstDiff = expected.ExpectedState.DescribeFirstDifference(liveStamp);
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(fightIndex, tag, currentTurn, "StateMismatch", firstDiff));
                        }
                        else if (activePlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(
                                fightIndex, tag, currentTurn, "ContinuationMissing",
                                $"cached_turns={activePlan.Continuations.Count}, prev_ended_turn={activePlan.CombatEndedTurn?.ToString() ?? "-"}"));
                        }
                        else
                        {
                            plannedBoundaryReplanCount++;
                        }
                        activePlan = null;
                    }
                }

                if (activePlan == null)
                {
                    CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                    SolverDisplayNames names = SolverDisplayNames.Capture(combat);
                    SolverSettingsSnapshot settings = SolverSettings.Capture();
                    SolverTheftPolicy? theftPolicy = SolverController.ResolveTheftPolicy(combat);
                    SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                        settings, combat, includeTurnSetup: false,
                        theftPolicy: theftPolicy);

                    if (!hasRootHotPolicy)
                    {
                        CombatHotState rootHotState = root.HotRoot.RootState;
                        rootSlotCosts = HotPotionPolicy.BuildRootSlotCosts(
                            root, in rootHotState, root.HotRoot.Rules, policy.PotionStrategy.Thresholds);
                        rootHotPolicy = new HotSearchPolicy(
                            root, damage, policy, root.HotRoot.Rules, in rootHotState, rootSlotCosts);
                        int slotCount = Math.Min((int)rootHotState.PotionSlotCount, HotLimits.MaxPotions);
                        for (int s = 0; s < slotCount; s++)
                        {
                            if (rootHotState.Potions[s] != 0)
                                rootOccupiedMask |= (ushort)(1 << s);
                        }
                        fightTheftPolicy = theftPolicy;
                        hasRootHotPolicy = true;
                    }

                    Stopwatch solveSw = Stopwatch.StartNew();
                    SolverResult solved = HotSearchCoordinator.Solve(
                        root, names, damage, policy, CancellationToken.None, null);
                    solveSw.Stop();

                    double ms = solveSw.Elapsed.TotalMilliseconds;
                    solveTimesMs.Add(ms);
                    fightSolveMs += ms;
                    fightSolveCount++;
                    fightExpandedNodes += solved.TotalExpandedNodes > 0 ? solved.TotalExpandedNodes : solved.ExpandedNodes;
                    fightTransitions += solved.TotalTransitionCount > 0 ? solved.TotalTransitionCount : solved.TransitionCount;
                    fightLastBoundary = solved.BoundaryReason.ToString();
                    totalSolves++;
                    activePlan = solved;
                    BattleDamageTracker.RegisterPlan(combat, activePlan);
                }

                // 部署当前回合整套计划动作
                SolverResult currentPlan = activePlan;
                int planTurn = currentPlan.StartTurnNumber;
                List<PlanAction> actions = currentPlan.BestNode.Actions
                    .Where(a => a.Turn == planTurn && a.IsExecutable)
                    .ToList();
                PlanAction? plannedEndTurn = currentPlan.BestNode.Actions
                    .FirstOrDefault(a => a.Turn == planTurn && a.Kind == PlanActionKind.EndTurn);

                bool deploymentDrifted = false;
                totalTurnsDeployed++;

                for (int actionIdx = 0; actionIdx < actions.Count; actionIdx++)
                {
                    if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                        break;
                    if (player.PlayerCombatState!.TurnNumber != planTurn)
                    {
                        unplannedReplanCount++;
                        replanSamples.Add(new SoakReplanSample(
                            fightIndex, tag, planTurn, "TurnDriftMidDeploy",
                            $"expected_turn={planTurn} actual_turn={player.PlayerCombatState.TurnNumber} at action#{actionIdx}"));
                        activePlan = null;
                        deploymentDrifted = true;
                        break;
                    }

                    PlanAction action = actions[actionIdx];
                    Creature? target = combat.GetCreature(action.TargetCombatId);

                    List<PlanCardChoice> choicePlans = [.. action.GetActionChoicesInExecutionOrder()];
                    if (action.TurnStartChoices is { Count: > 0 } phaseChoices)
                        choicePlans.AddRange(phaseChoices);

                    RealCombatAction liveAction;
                    if (action.Kind == PlanActionKind.UsePotion)
                    {
                        PotionModel? potion = player.GetPotionAtSlotIndex(action.PotionSlot);
                        if (potion == null || !string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                        {
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(
                                fightIndex, tag, planTurn, "PotionDrift",
                                $"slot={action.PotionSlot} expected={action.PotionId} actual={potion?.Id.Entry ?? "empty"}"));
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }
                        if ((uint)action.PotionSlot < (uint)rootSlotCosts.Length)
                        {
                            ushort slotBit = (ushort)(1 << action.PotionSlot);
                            if ((rootOccupiedMask & ~rootConsumedMask & slotBit) != 0)
                            {
                                rootConsumedMask |= slotBit;
                                if (rootSlotCosts[action.PotionSlot] > 0)
                                {
                                    rootOptionalPaidRawHpCost += rootSlotCosts[action.PotionSlot];
                                    rootOptionalPaidCount++;
                                }
                            }
                        }
                        liveAction = RealCombatAction.UsePotion(potion, target);
                        fightPotionsUsed++;
                    }
                    else
                    {
                        List<CardModel> hand = player.PlayerCombatState.Hand.Cards.ToList();
                        CardModel card;
                        try
                        {
                            card = SolverController.FindCardForDeployment(hand, action);
                        }
                        catch (InvalidOperationException ex)
                        {
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(
                                fightIndex, tag, planTurn, "CardMissing", ex.Message));
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }

                        if (!card.CanPlayTargeting(target))
                        {
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(
                                fightIndex, tag, planTurn, "CardUnplayable",
                                $"card={action.CardId} target={action.TargetCombatId?.ToString() ?? "-"} energy={player.PlayerCombatState.Energy}"));
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }
                        if (string.Equals(action.CardId, "ALCHEMIZE", StringComparison.Ordinal))
                            alchemizeCount++;
                        liveAction = RealCombatAction.PlayCard(card, target);
                    }

                    PlanDrivenCardSelector actionSelector = new(choicePlans);
                    using (RunManager.Instance.CardSelectorStack.PushSelector(player, actionSelector))
                    {
                        _ = RealCombatDriver.RunRealCombatAction(
                            loop, combat, liveAction, externalErrorProbe: () => actionSelector.LastError);
                    }
                }

                if (deploymentDrifted)
                    continue;

                if (RealCombatDriver.GetLiveOutcome(combat) == HotCombatOutcome.Ongoing
                    && player.PlayerCombatState!.TurnNumber == planTurn)
                {
                    if (plannedEndTurn == null)
                    {
                        // 搜索在回合中途因预算截断导致没有 EndTurn 节点
                        if (currentPlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplanCount++;
                            replanSamples.Add(new SoakReplanSample(
                                fightIndex, tag, planTurn, "PlanExhaustedWithoutEndTurn",
                                $"executed={actions.Count}"));
                        }
                        else
                        {
                            plannedBoundaryReplanCount++;
                        }
                        activePlan = null;
                        continue;
                    }

                    // 离线 EndTurn 会连续推进 PlayerTurnEnd -> EnemyTurn -> PlayerTurnStart -> 下一回合 Play，
                    // 必须把 TurnStartChoices（含 PlayerTurnEnd / EnemyTurn / PlayerTurnStart 如 TyrannyPower）全部喂给选牌器！
                    IReadOnlyList<PlanCardChoice> endTurnChoices = plannedEndTurn.TurnStartChoices ?? [];
                    PlanDrivenCardSelector endTurnSelector = new(endTurnChoices);
                    using (RunManager.Instance.CardSelectorStack.PushSelector(player, endTurnSelector))
                    {
                        _ = RealCombatDriver.RunRealCombatAction(
                            loop, combat, RealCombatAction.EndTurn(), externalErrorProbe: () => endTurnSelector.LastError);
                    }
                }
            }

            HotCombatOutcome finalOutcome = RealCombatDriver.GetLiveOutcome(combat);
            int endHp = player.Creature.CurrentHp;
            int endMaxHp = player.Creature.MaxHp;
            int hpLoss = Math.Max(0, startHp - endHp);
            bool won = finalOutcome == HotCombatOutcome.Victory;
            bool survives = !player.Creature.IsDead && endHp > 0;
            completedFightsForStats++;
            totalHpLoss += hpLoss;
            totalPotionsUsed += fightPotionsUsed;

            if (won)
                wins++;
            else
                defeats++;

            int remainingUnusedLizardTails = player.Relics
                .OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>()
                .Count(static t => !t.WasUsed);
            int remainingFairyCount = player.PotionSlots
                .Count(static p => p != null && string.Equals(p.Id.Entry, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));
            int lizardTailConsumed = Math.Max(0, initialUnusedLizardTails - remainingUnusedLizardTails);
            int fairyConsumed = Math.Max(0, initialFairyCount - remainingFairyCount);
            int deathSaveUses = lizardTailConsumed + fairyConsumed;
            int deathSaveHpRestored = lizardTailConsumed * (endMaxHp / 2) + fairyConsumed * ((endMaxHp * 30) / 100);

            int postCombatHeal = hasRootHotPolicy
                ? HotRouteQuality.RankedPostCombatRelicHeal(rootHotPolicy.PostCombatRelicHeal, won, endHp, endMaxHp)
                : 0;
            int netHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
                endPlayerHp: endHp,
                postCombatMonotoneHeal: postCombatHeal,
                initialPlayerHp: startHp,
                endPlayerMaxHp: endMaxHp,
                initialPlayerMaxHp: startMaxHp,
                deathSaveHpRestored: deathSaveHpRestored,
                growthHpCredit: 0,
                bossHpRelief: hasRootHotPolicy ? rootHotPolicy.StrategicBossHpRelief : BossHpRelief.None,
                ignoreLongTermRewards: false);
            int endEmptySlots = player.PotionSlots.Count(static p => p == null);
            int netPotionCostCenti = hasRootHotPolicy
                ? HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
                    rootOptionalPaidRawHpCost, rootOptionalPaidCount, alchemizeCount, endEmptySlots, in rootHotPolicy)
                : rootOptionalPaidRawHpCost * HotSearchConstants.HpFixedScale;
            int strategicDeficit = HotRouteQuality.RoundCentiToHp(-netHpDeltaCenti);
            int potionStrategicCost = netPotionCostCenti > 0
                ? Math.Max(1, HotRouteQuality.RoundCentiToHp(netPotionCostCenti))
                : 0;
            int enemyHpRemaining = combat.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp);

            return new FightOutcomeRecord
            {
                FightId = tag,
                Tier = "soak",
                Group = group,
                CharacterId = setup.CharacterId,
                EncounterId = setup.EncounterId,
                Won = won,
                Survives = survives,
                DeathSaveUseCount = deathSaveUses,
                StartHp = startHp,
                EndHp = endHp,
                StartMaxHp = startMaxHp,
                EndMaxHp = endMaxHp,
                HpLoss = hpLoss,
                StrategicHpDeficit = strategicDeficit,
                PotionsUsed = fightPotionsUsed,
                PotionCostHp = potionStrategicCost,
                NetPersistentHpDeltaCenti = netHpDeltaCenti,
                NetPotionOpportunityCostCenti = netPotionCostCenti,
                EconomicValueCenti = (long)netHpDeltaCenti - netPotionCostCenti,
                Turns = currentTurn,
                EnemyHpRemaining = enemyHpRemaining,
                ElapsedMs = Math.Round(fightSolveMs, 2),
                SolveCount = fightSolveCount,
                Boundary = fightLastBoundary,
                ExpandedNodes = fightExpandedNodes,
                Transitions = fightTransitions,
                UnplannedReplans = unplannedReplanCount - replansBeforeFight,
                ChoiceMismatches = choiceMismatchCount - choiceMismatchesBeforeFight,
                HardErrors = 0,
                TheftPolicy = fightTheftPolicy?.ToString(),
                HardError = null,
            };
        }
        catch (Exception ex)
        {
            string errDesc;
            if (FindInner<NativeChoicePlanMismatchException>(ex) is { } choiceMismatch)
            {
                choiceMismatchCount++;
                hardErrorCount++;
                errDesc = $"{choiceMismatch.GetType().Name}: {choiceMismatch.Message}";
                errorSamples.Add(new SoakErrorSample(fightIndex, tag, currentTurn, choiceMismatch.GetType().Name, choiceMismatch.Message));
            }
            else
            {
                Exception root = Unwrap(ex);
                hardErrorCount++;
                errDesc = $"{root.GetType().Name}: {root.Message}";
                errorSamples.Add(new SoakErrorSample(fightIndex, tag, currentTurn, root.GetType().Name, root.Message));
            }

            return new FightOutcomeRecord
            {
                FightId = tag,
                Tier = "soak",
                Group = group,
                CharacterId = setup.CharacterId,
                EncounterId = setup.EncounterId,
                Won = false,
                Survives = false,
                StartHp = startHp,
                StartMaxHp = startMaxHp,
                Turns = currentTurn,
                ElapsedMs = Math.Round(fightSolveMs, 2),
                SolveCount = fightSolveCount,
                Boundary = "Error",
                ExpandedNodes = fightExpandedNodes,
                Transitions = fightTransitions,
                UnplannedReplans = unplannedReplanCount - replansBeforeFight,
                ChoiceMismatches = choiceMismatchCount - choiceMismatchesBeforeFight,
                HardErrors = 1,
                HardError = errDesc,
            };
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    private static List<(string Tag, CombatSetupSpec Setup)> BuildSoakScenarios(int fights, int seed)
    {
        Random rng = new(seed);

        Dictionary<string, CharacterModel> charMap = ModelDb.AllCharacters
            .ToDictionary(static c => c.Id.Entry, StringComparer.Ordinal);

        CardModel[] colorlessPool = ModelDb.CardPool<ColorlessCardPool>().AllCards
            .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                && GeneratedCombatScenario.IsSingleplayerCard(c)
                && c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
            .DistinctBy(static c => c.Id)
            .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        RelicModel[] sharedRelics = ModelDb.RelicPool<SharedRelicPool>().AllRelics
            .Where(r => !HotCoverageAuditor.IsMockOrDeprecated(r)
                && GeneratedCombatScenario.IsSingleplayerRelic(r)
                && r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop)
            .DistinctBy(static r => r.Id)
            .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        PotionModel[] sharedPotions = ModelDb.PotionPool<SharedPotionPool>().AllPotions
            .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
            .DistinctBy(static p => p.Id)
            .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        // 选取 Act 1/2/3 普通与精英遭遇（覆盖常规战斗与高压多敌/机制战）
        List<(int ActIndex, EncounterModel Encounter)> encounterPool = [];
        ActModel[] acts = ModelDb.Acts.ToArray();
        for (int a = 0; a < acts.Length; a++)
        {
            foreach (EncounterModel e in acts[a].AllEncounters)
            {
                if (HotCoverageAuditor.IsMockOrDeprecated(e))
                    continue;
                if (e.RoomType is RoomType.Monster or RoomType.Elite)
                    encounterPool.Add((Math.Min(a, 2), e));
            }
        }
        encounterPool = encounterPool
            .DistinctBy(static x => x.Encounter.Id)
            .OrderBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();

        DominionModel[] dominions = ModelDb.All.OfType<DominionModel>()
            .Where(d => !HotCoverageAuditor.IsMockOrDeprecated(d))
            .OrderBy(static d => d.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        EnchantmentModel[] soakEnchantments = ModelDb.DebugEnchantments
            .Where(e => !HotCoverageAuditor.IsMockOrDeprecated(e)
                && e.Id.Entry is "ADROIT" or "SHARP" or "NIMBLE" or "SWIFT" or "VIGOROUS" or "STEADY" or "SPIRAL" or "INSTINCT" or "TEZCATARAS_EMBER" or "PERFECT_FIT")
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        List<(string Tag, CombatSetupSpec Setup)> result = new(fights);
        for (int i = 0; i < fights; i++)
        {
            string charId = AllCharacters[i % AllCharacters.Length];
            CharacterModel ch = charMap[charId];

            GameMode mode = (i % 6) switch
            {
                0 or 1 or 2 => GameMode.Standard,
                3 or 4 => GameMode.Dread,
                _ => GameMode.Blitz,
            };
            string? dominionId = mode == GameMode.Dread && dominions.Length > 0
                ? dominions[(i / 2) % dominions.Length].Id.Entry
                : null;

            (int actIdx, EncounterModel enc) = encounterPool[rng.Next(encounterPool.Count)];

            CardModel[] charCards = ch.CardPool.AllCards
                .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                    && GeneratedCombatScenario.IsSingleplayerCard(c)
                    && c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare
                    && (mode != GameMode.Blitz || c.IsAllowedInBlitz))
                .DistinctBy(static c => c.Id)
                .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int addCardCount = 6 + rng.Next(4);
            List<CardInjectionSpec> addedCards = new(addCardCount + 1);
            for (int c = 0; c < addCardCount && charCards.Length > 0; c++)
            {
                CardModel pick = charCards[rng.Next(charCards.Length)];
                int upg = rng.Next(3) == 0 && pick.MaxUpgradeLevel > 0 ? 1 : 0;
                string? enchId = null;
                if (c == 0 && soakEnchantments.Length > 0 && rng.Next(2) == 0)
                {
                    EnchantmentModel cand = soakEnchantments[rng.Next(soakEnchantments.Length)];
                    if (RealCombatDriver.CanSafelyEnchant(cand, pick))
                        enchId = cand.Id.Entry;
                }
                addedCards.Add(new CardInjectionSpec(pick.Id.Entry, upg, enchId, 2));
            }
            if (colorlessPool.Length > 0 && rng.Next(2) == 0)
            {
                CardModel cl = colorlessPool[rng.Next(colorlessPool.Length)];
                if (mode != GameMode.Blitz || cl.IsAllowedInBlitz)
                    addedCards.Add(new CardInjectionSpec(cl.Id.Entry, rng.Next(2)));
            }

            RelicModel[] relicPool = ch.RelicPool.AllRelics
                .Concat(sharedRelics)
                .Where(r => !HotCoverageAuditor.IsMockOrDeprecated(r)
                    && GeneratedCombatScenario.IsSingleplayerRelic(r)
                    && (mode != GameMode.Blitz || !RelicLists.notAllowedInBlitz.Contains(r.Id)))
                .DistinctBy(static r => r.Id)
                .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int relicCount = 2 + rng.Next(4);
            HashSet<string> chosenRelics = new(StringComparer.Ordinal);
            for (int r = 0; r < relicCount * 2 && chosenRelics.Count < relicCount && relicPool.Length > 0; r++)
                chosenRelics.Add(relicPool[rng.Next(relicPool.Length)].Id.Entry);

            PotionModel[] potionPool = ch.PotionPool.AllPotions
                .Concat(sharedPotions)
                .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
                .DistinctBy(static p => p.Id)
                .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int potionCount = 1 + rng.Next(3);
            List<string> chosenPotions = new(potionCount);
            for (int p = 0; p < potionCount && potionPool.Length > 0; p++)
                chosenPotions.Add(potionPool[rng.Next(potionPool.Length)].Id.Entry);

            int ascension = mode == GameMode.Blitz ? (i % 6) : ((i % 5 == 0) ? 10 : 0);
            int maxHp = 85 + actIdx * 10;
            int curHp = maxHp - rng.Next(15);

            string tag = $"soak_{i:D2}_{charId.ToLowerInvariant()}_{mode.ToString().ToLowerInvariant()}_{enc.Id.Entry.ToLowerInvariant()}";
            result.Add((tag, new CombatSetupSpec
            {
                CharacterId = charId,
                EncounterId = enc.Id.Entry,
                Seed = $"SOAK{seed:X4}_{i:D3}",
                GameMode = mode,
                DominionId = dominionId,
                Ascension = ascension,
                ActIndex = actIdx,
                PlayerMaxHp = maxHp,
                PlayerHp = curHp,
                Cards = addedCards.ToArray(),
                Relics = chosenRelics.ToArray(),
                PotionSlotCount = 3,
                Potions = chosenPotions.ToArray(),
            }));
        }

        return result;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0.0;
        double[] sorted = values.OrderBy(static x => x).ToArray();
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi)
            return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    private static bool RunCustomBeamStage2Regression(
        MainLoopContext loop,
        int dop,
        ref int hardErrorCount,
        List<SoakErrorSample> errorSamples)
    {
        const string Tag = "regression_custom_beam135_180_stage2";
        try
        {
            BattleDamageTracker.Reset();
            CombatSetupSpec setup = new()
            {
                CharacterId = "IRONCLAD",
                EncounterId = "PHROG_PARASITE_ELITE",
                Seed = "BEAM135_REG",
                GameMode = GameMode.Standard,
                Ascension = 10,
                ActIndex = 0,
                PlayerMaxHp = 80,
                PlayerHp = 28,
                Cards = [new CardInjectionSpec("WHIRLWIND", 0), new CardInjectionSpec("POMMEL_STRIKE", 0)],
                Relics = ["BURNING_BLOOD"],
                PotionSlotCount = 3,
                Potions = ["BLOCK_POTION", "FIRE_POTION", "STRENGTH_POTION"],
            };

            CombatState combat = RealCombatDriver.StartCombat(loop, setup);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SearchPolicySnapshot basePolicy = SolverController.CaptureSearchPolicy(
                settings, combat, includeTurnSetup: false,
                theftPolicy: SolverController.ResolveTheftPolicy(combat));

            int[] customBeams = [135, 180];
            List<string> results = [];
            foreach (int beam in customBeams)
            {
                SearchPolicySnapshot customPolicy = basePolicy with
                {
                    Profile = basePolicy.Profile with { BeamWidth = beam },
                    PotionPolicy = SolverPotionPolicy.Smart,
                    MaxDegreeOfParallelism = dop,
                    BudgetOverrideMilliseconds = 5_000,
                };
                Stopwatch sw = Stopwatch.StartNew();
                SolverResult solved = HotSearchCoordinator.Solve(
                    root, names, damage, customPolicy, CancellationToken.None, null);
                sw.Stop();
                results.Add($"beam={beam}:{sw.ElapsedMilliseconds}ms/actions={solved.BestNode.Actions.Count}");
            }

            Console.WriteLine($"[soak] custom_beam_stage2_regression: PASS ({string.Join(", ", results)})");
            return true;
        }
        catch (Exception ex)
        {
            Exception root = Unwrap(ex);
            hardErrorCount++;
            errorSamples.Add(new SoakErrorSample(-1, Tag, 1, root.GetType().Name, root.Message));
            Console.WriteLine($"[soak] custom_beam_stage2_regression: FAIL ({root.GetType().Name}: {root.Message})");
            return false;
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        if (ex is T direct)
            return direct;
        if (ex is AggregateException agg)
        {
            foreach (Exception inner in agg.InnerExceptions)
            {
                if (FindInner<T>(inner) is { } found)
                    return found;
            }
        }
        return ex.InnerException != null ? FindInner<T>(ex.InnerException) : null;
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException or AggregateException && ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
}
