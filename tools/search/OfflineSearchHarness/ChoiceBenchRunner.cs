using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

/// <summary>
/// Stage 8D 专项高分支选牌与回合准备基准（<c>--bench-choices</c>）：
/// 在纯 .NET 实机 <c>sts2.dll</c> 上验证 17 个覆盖牌堆搜索（Wish / Graveblast）、置顶抽牌堆（Headbutt / CosmicIndifference）、
/// 多选组合（Charge!! / Dredge / Purity+）、回合初变换/消耗/弃牌及复合触发（Entropy / Tyranny / ToastyMittens / ToolsOfTheTrade）、
/// 以及 Depth-0 首回合 <c>PendingTurnSetup</c>（Toolbox × ToastyMittens / GamblingChip）的精确最优性与零失配部署一致性。
/// 输出同时兼容 <c>--compare-outcomes</c>（<see cref="BenchmarkOutcomeFile"/>），可直接用于 Stage 8D 前后 A/B 对比。
/// </summary>
internal static class ChoiceBenchRunner
{
    private const string TurnSetupHarmonyId = "OfflineSearchHarness.ChoiceBench.TurnSetup";
    private const string DeterministicEncounterId = "DEVOTED_SCULPTOR_WEAK";

    private static readonly Type CombatTurnStateType = typeof(CombatManager).Assembly.GetType(
        "MegaCrit.Sts2.Core.Combat.CombatTurnState",
        throwOnError: true)!;

    private static readonly MethodInfo SetupPlayerTurnMethod = typeof(CombatManager).GetMethod(
        "SetupPlayerTurn",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [CombatTurnStateType, typeof(Player), typeof(HookPlayerChoiceContext)],
        modifiers: null)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "SetupPlayerTurn");

    private static readonly MethodInfo SetupPlayerTurnPrefixMethod = typeof(ChoiceBenchRunner).GetMethod(
        nameof(SetupPlayerTurnPrefix),
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(ChoiceBenchRunner).FullName, nameof(SetupPlayerTurnPrefix));

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ThreadStatic]
    private static bool _invokingOriginalSetup;

    private static TurnSetupHookContext? _activeTurnSetupHook;

    private sealed record CardSpec(string CardId, int UpgradeLevel = 0);

    private sealed record ChoiceCaseSpec
    {
        public required string CaseId { get; init; }
        public required string Group { get; init; }
        public required string Description { get; init; }
        public required string CharacterId { get; init; }
        public string EncounterId { get; init; } = DeterministicEncounterId;
        public string Seed { get; init; } = "CHOICE01";
        public bool IncludeTurnSetup { get; init; }
        public int PlayerMaxHp { get; init; } = 80;
        public int PlayerHp { get; init; } = 10;
        public int EnemyHp { get; init; } = 42;
        public int Energy { get; init; } = 3;
        public string[] PreCombatRelics { get; init; } = [];
        public string[] PostStartRelics { get; init; } = [];
        public PowerInjectionSpec[] InitialPowers { get; init; } = [];
        public CardSpec[] Hand { get; init; } = [];
        public CardSpec[] DrawPile { get; init; } = [];
        public CardSpec[] DiscardPile { get; init; } = [];
        public CardSpec[] ExhaustPile { get; init; } = [];
        public bool ExpectWon { get; init; } = true;
        public int MaxExpectedHpLoss { get; init; }
        public int? MaxExpectedTurns { get; init; }
        public int? MinTurnSetupChoices { get; init; }
        public string[] RequiredChosenCardIds { get; init; } = [];
        public string[] ForbiddenChosenCardIds { get; init; } = [];
    }

    private sealed class TurnSetupHookContext(ChoiceCaseSpec caseSpec)
    {
        public ChoiceCaseSpec Case { get; } = caseSpec;
        public bool Intercepted { get; set; }
        public SolverResult? SolvedResult { get; set; }
        public CombatRootSnapshot? RootSnapshot { get; set; }
        public SearchPolicySnapshot? SearchPolicy { get; set; }
        public BattleDamageSnapshot DamageSnapshot { get; set; }
        public double SolveMs { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed record ChoiceCaseReport
    {
        [JsonPropertyName("case_id")]
        public required string CaseId { get; init; }

        [JsonPropertyName("group")]
        public required string Group { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("character_id")]
        public required string CharacterId { get; init; }

        [JsonPropertyName("encounter_id")]
        public required string EncounterId { get; init; }

        [JsonPropertyName("include_turn_setup")]
        public bool IncludeTurnSetup { get; init; }

        [JsonPropertyName("won")]
        public bool Won { get; init; }

        [JsonPropertyName("hp_loss")]
        public int HpLoss { get; init; }

        [JsonPropertyName("turns")]
        public int Turns { get; init; }

        [JsonPropertyName("enemy_hp_remaining")]
        public int EnemyHpRemaining { get; init; }

        [JsonPropertyName("economic_value_v")]
        public double EconomicValueV { get; init; }

        [JsonPropertyName("elapsed_ms")]
        public double ElapsedMs { get; init; }

        [JsonPropertyName("solve_count")]
        public int SolveCount { get; init; }

        [JsonPropertyName("expanded_nodes")]
        public long ExpandedNodes { get; init; }

        [JsonPropertyName("transitions")]
        public long Transitions { get; init; }

        [JsonPropertyName("unplanned_replans")]
        public int UnplannedReplans { get; init; }

        [JsonPropertyName("choice_mismatches")]
        public int ChoiceMismatches { get; init; }

        [JsonPropertyName("hard_errors")]
        public int HardErrors { get; init; }

        [JsonPropertyName("turn_setup_choices_count")]
        public int TurnSetupChoicesCount { get; init; }

        [JsonPropertyName("optimality_passed")]
        public bool OptimalityPassed { get; init; }

        [JsonPropertyName("optimality_note")]
        public required string OptimalityNote { get; init; }

        [JsonPropertyName("observed_choices")]
        public required string[] ObservedChoices { get; init; }

        [JsonPropertyName("replan_diffs")]
        public string[]? ReplanDiffs { get; init; }

        [JsonPropertyName("hard_error")]
        public string? HardError { get; init; }
    }

    internal static int Run(string[] args)
    {
        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        int dop = 8;
        int budgetMs = 15_000;
        bool strict = false;
        List<string> filters = [];
        string repoRoot = ResolveRepoRoot();
        string outPath = Path.Combine(repoRoot, ".local", "offline-harness", "choice-bench.json");
        string? dumpOutcomesPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--bench-choices 选项 {key} 缺少参数。");

            switch (key)
            {
                case "--profile":
                    profile = NextVal();
                    break;
                case "--beam":
                    beamOverride = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--nodes":
                    nodesOverride = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--dop":
                    dop = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--budget-ms":
                    budgetMs = Math.Max(500, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--filter":
                    foreach (string part in NextVal().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        filters.Add(part);
                    break;
                case "--out":
                    outPath = Path.GetFullPath(NextVal());
                    break;
                case "--dump-outcomes":
                    dumpOutcomesPath = Path.GetFullPath(NextVal());
                    break;
                case "--strict":
                    strict = true;
                    break;
                default:
                    throw new ArgumentException($"--bench-choices 未知选项：{key}");
            }
        }

        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException("--profile 只支持 Low|Medium|High|VeryHigh|Custom。");

        List<ChoiceCaseSpec> allCases = BuildChoiceCases();
        List<ChoiceCaseSpec> selectedCases = filters.Count == 0
            ? allCases
            : allCases.Where(c => filters.Any(f =>
                c.CaseId.Contains(f, StringComparison.OrdinalIgnoreCase)
                || c.Group.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();

        if (selectedCases.Count == 0)
            throw new ArgumentException($"--filter 未匹配到任何场景（filters={string.Join(",", filters)}）。");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine(
            $"=== --bench-choices (cases={selectedCases.Count}/{allCases.Count}, profile={profile}, " +
            $"beam={beamOverride?.ToString(CultureInfo.InvariantCulture) ?? "preset"}, " +
            $"nodes={nodesOverride?.ToString(CultureInfo.InvariantCulture) ?? "preset"}, " +
            $"dop={dop}, budgetMs={budgetMs}, strict={strict}) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = new()
        {
            Profile = profile,
            Beam = beamOverride,
            Nodes = nodesOverride,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = "Smart",
            SearchMode = "Coordinator",
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", "choice-bench-logs"),
        };
        GameBootstrap.EnsureBootstrapped(loop, modOptions);

        int gen2Before = GC.CollectionCount(2);
        using MemorySampler memory = new(TimeSpan.FromMilliseconds(150));

        List<FightOutcomeRecord> fightOutcomes = new(selectedCases.Count);
        List<ChoiceCaseReport> caseReports = new(selectedCases.Count);
        List<double> solveTimesMs = new(selectedCases.Count * 2);

        try
        {
            for (int i = 0; i < selectedCases.Count; i++)
            {
                ChoiceCaseSpec caseSpec = selectedCases[i];
                (FightOutcomeRecord outcome, ChoiceCaseReport report) = RunSingleCase(loop, caseSpec, solveTimesMs);
                fightOutcomes.Add(outcome);
                caseReports.Add(report);

                string status = report.HardErrors > 0
                    ? "ERROR"
                    : report.ChoiceMismatches > 0 || report.UnplannedReplans > 0
                        ? "PARITY_FAIL"
                        : report.OptimalityPassed
                            ? "OPT_PASS"
                            : "OPT_MISS";
                Console.WriteLine(
                    $"[{i + 1,2}/{selectedCases.Count,2}] {caseSpec.CaseId,-40} | {status,-11} | " +
                    $"won={(report.Won ? "Y" : "N")} hpLoss={report.HpLoss,2} T={report.Turns} " +
                    $"V={report.EconomicValueV,6:F2} ms={report.ElapsedMs,7:F1} " +
                    $"replans={report.UnplannedReplans} chErr={report.ChoiceMismatches} | {report.OptimalityNote}");
            }
        }
        finally
        {
            _activeTurnSetupHook = null;
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        memory.Dispose();
        int gcGen2 = GC.CollectionCount(2) - gen2Before;
        long peakRssBytes = Math.Max(memory.PeakWorkingSetBytes, memory.PeakProcessWorkingSetBytes);
        double peakRssMb = Math.Round(peakRssBytes / (1024.0 * 1024.0), 2);
        BenchmarkSummaryRecord summary = BenchmarkOutcomeHelper.BuildSummary(
            fightOutcomes, totalSw.Elapsed.TotalMilliseconds, peakRssMb, gcGen2, solveTimesMs);

        if (dumpOutcomesPath != null)
        {
            BenchmarkOutcomeHelper.WriteOutcomeFile(dumpOutcomesPath, new BenchmarkOutcomeFile
            {
                Suite = "choice-bench",
                Profile = profile,
                SearchMode = "Coordinator",
                PotionPolicy = "Smart",
                Dop = dop,
                Seed = null,
                BudgetMs = budgetMs,
                Summary = summary,
                Outcomes = fightOutcomes.ToArray(),
            });
        }

        int optPassed = caseReports.Count(static r => r.OptimalityPassed && r.HardErrors == 0 && r.ChoiceMismatches == 0 && r.UnplannedReplans == 0);
        int hardErrors = summary.HardErrors;
        int choiceMismatches = summary.TotalChoiceMismatches;
        int unplannedReplans = summary.TotalUnplannedReplans;

        object fullReport = new
        {
            suite = "choice-bench",
            profile,
            beam = beamOverride,
            nodes = nodesOverride,
            dop,
            budget_ms = budgetMs,
            strict,
            cases_run = caseReports.Count,
            optimality_passed = optPassed,
            optimality_rate = Math.Round(caseReports.Count > 0 ? (double)optPassed / caseReports.Count : 0.0, 4),
            summary,
            outcomes_path = dumpOutcomesPath,
            cases = caseReports.ToArray(),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(fullReport, ReportJsonOptions));

        Console.WriteLine();
        Console.WriteLine("=== --bench-choices SUMMARY ===");
        Console.WriteLine(
            $"cases={caseReports.Count} opt_passed={optPassed}/{caseReports.Count} ({(caseReports.Count > 0 ? (double)optPassed / caseReports.Count : 0.0):P1}) | " +
            $"wins={summary.Wins}/{summary.TotalFights} avg_hp_loss={summary.AvgHpLoss:F2} avg_v={summary.AvgEconomicValueV:F2}");
        Console.WriteLine(
            $"hard_errors={hardErrors} choice_mismatches={choiceMismatches} unplanned_replans={unplannedReplans} | " +
            $"p50_ms={summary.P50Ms:F1} p95_ms={summary.P95Ms:F1} peak_rss_mb={peakRssMb:F1} gc_gen2={gcGen2} elapsed={totalSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"report_json={outPath}");
        if (dumpOutcomesPath != null)
            Console.WriteLine($"outcomes_json={dumpOutcomesPath}");

        bool parityClean = hardErrors == 0 && choiceMismatches == 0 && unplannedReplans == 0;
        if (!parityClean)
            return 1;
        return strict && optPassed < caseReports.Count ? 1 : 0;
    }

    private static (FightOutcomeRecord Outcome, ChoiceCaseReport Report) RunSingleCase(
        MainLoopContext loop,
        ChoiceCaseSpec caseSpec,
        List<double> solveTimesMs)
    {
        int currentTurn = 0;
        int startHp = caseSpec.PlayerHp;
        int startMaxHp = caseSpec.PlayerMaxHp;
        double fightSolveMs = 0.0;
        int fightSolveCount = 0;
        long fightExpandedNodes = 0;
        long fightTransitions = 0;
        string fightLastBoundary = "None";
        int unplannedReplans = 0;
        int choiceMismatches = 0;
        int turnSetupChoicesCount = 0;
        List<string> observedChoices = [];
        List<string> observedChosenCardIds = [];
        List<string> replanDiffs = [];
        List<string> routeSegments = [];

        HotSearchPolicy rootHotPolicy = default;
        bool hasRootHotPolicy = false;
        SolverTheftPolicy? fightTheftPolicy = null;

        Harmony? turnSetupHarmony = null;
        TurnSetupHookContext? turnSetupHook = null;

        try
        {
            BattleDamageTracker.Reset();

            CombatSetupSpec baseSpec = new()
            {
                CharacterId = caseSpec.CharacterId,
                EncounterId = caseSpec.EncounterId,
                Seed = caseSpec.Seed,
                GameMode = GameMode.Standard,
                Ascension = 0,
                ActIndex = 0,
                PlayerMaxHp = caseSpec.PlayerMaxHp,
                PlayerHp = caseSpec.PlayerHp,
                ClearStarterDeck = true,
                ClearStarterRelics = true,
                Relics = caseSpec.PreCombatRelics,
                Cards =
                [
                    new CardInjectionSpec("WOUND", 0),
                    new CardInjectionSpec("WOUND", 0),
                    new CardInjectionSpec("WOUND", 0),
                    new CardInjectionSpec("WOUND", 0),
                    new CardInjectionSpec("WOUND", 0),
                ],
                InitialPowers = caseSpec.IncludeTurnSetup ? [] : caseSpec.InitialPowers,
            };

            if (caseSpec.IncludeTurnSetup)
            {
                turnSetupHook = new TurnSetupHookContext(caseSpec);
                _activeTurnSetupHook = turnSetupHook;
                turnSetupHarmony = new Harmony(TurnSetupHarmonyId);
                turnSetupHarmony.Patch(
                    SetupPlayerTurnMethod,
                    prefix: new HarmonyMethod(SetupPlayerTurnPrefixMethod) { priority = Priority.First });
            }

            CombatState combat;
            try
            {
                combat = RealCombatDriver.StartCombat(loop, baseSpec);
            }
            finally
            {
                if (turnSetupHarmony != null)
                {
                    turnSetupHarmony.UnpatchAll(TurnSetupHarmonyId);
                    turnSetupHarmony = null;
                }
                _activeTurnSetupHook = null;
            }

            if (turnSetupHook?.Error != null)
                throw turnSetupHook.Error;

            Player player = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException("实机战斗缺少本地玩家。");

            SolverResult? activePlan = null;

            if (caseSpec.IncludeTurnSetup)
            {
                if (turnSetupHook == null || !turnSetupHook.Intercepted || turnSetupHook.SolvedResult == null)
                    throw new InvalidOperationException($"场景 {caseSpec.CaseId} 未触发 Turn 1 PendingTurnSetup 拦截。");

                SolverResult setupSolved = turnSetupHook.SolvedResult;
                fightSolveMs += turnSetupHook.SolveMs;
                solveTimesMs.Add(turnSetupHook.SolveMs);
                fightSolveCount++;
                fightExpandedNodes += setupSolved.TotalExpandedNodes > 0 ? setupSolved.TotalExpandedNodes : setupSolved.ExpandedNodes;
                fightTransitions += setupSolved.TotalTransitionCount > 0 ? setupSolved.TotalTransitionCount : setupSolved.TransitionCount;
                fightLastBoundary = setupSolved.BoundaryReason.ToString();
                turnSetupChoicesCount = setupSolved.TurnSetupChoices.Count;
                RecordPlanChoices("SetupT1", setupSolved.TurnSetupChoices, observedChoices, observedChosenCardIds);

                if (turnSetupHook.RootSnapshot != null && turnSetupHook.SearchPolicy != null)
                {
                    CombatHotState rootHotState = turnSetupHook.RootSnapshot.HotRoot.RootState;
                    short[] slotCosts = HotPotionPolicy.BuildRootSlotCosts(
                        turnSetupHook.RootSnapshot, in rootHotState, turnSetupHook.RootSnapshot.HotRoot.Rules,
                        turnSetupHook.SearchPolicy.PotionStrategy.Thresholds);
                    rootHotPolicy = new HotSearchPolicy(
                        turnSetupHook.RootSnapshot, turnSetupHook.DamageSnapshot, turnSetupHook.SearchPolicy,
                        turnSetupHook.RootSnapshot.HotRoot.Rules, in rootHotState, slotCosts);
                    fightTheftPolicy = turnSetupHook.SearchPolicy.TheftPolicy;
                    hasRootHotPolicy = true;
                }

                // 验证 Depth-0 TurnSetup 执行完毕进入 Play 阶段后的精确状态戳一致性
                if (RealCombatDriver.GetLiveOutcome(combat) == HotCombatOutcome.Ongoing)
                {
                    ContinuationStamp livePlayStamp = ContinuationStamp.CaptureLive(combat);
                    if (setupSolved.TurnSetupPlayState is { } expectedPlayState)
                    {
                        if (expectedPlayState != livePlayStamp)
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T1[TurnSetupPlayStateMismatch]: {expectedPlayState.DescribeFirstDifference(livePlayStamp)}");
                            activePlan = null;
                        }
                        else
                        {
                            activePlan = setupSolved;
                            BattleDamageTracker.RegisterPlan(combat, activePlan);
                        }
                    }
                    else
                    {
                        unplannedReplans++;
                        replanDiffs.Add("T1[TurnSetupPlayStateMissing]: TurnSetupPlayState == null");
                        activePlan = null;
                    }
                }
                else
                {
                    activePlan = setupSolved;
                }
            }
            else
            {
                // Play 阶段精确注入确定性牌堆、血量、能量与 Turn-2 遗物
                Task fixtureTask = ApplyPlayPhaseFixtureAsync(combat, player, caseSpec);
                loop.RunUntilCompleted(fixtureTask, TimeSpan.FromSeconds(20), $"ApplyPlayPhaseFixture({caseSpec.CaseId})");
                loop.DrainPending();
            }

            startHp = player.Creature.CurrentHp;
            startMaxHp = player.Creature.MaxHp;

            const int maxTurns = 16;
            for (int turnLoop = 0; turnLoop < maxTurns; turnLoop++)
            {
                if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                    break;

                currentTurn = player.PlayerCombatState!.TurnNumber;
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                ContinuationStamp liveStamp = ContinuationStamp.CaptureLive(combat);

                if (activePlan != null && !(caseSpec.IncludeTurnSetup && turnLoop == 0 && activePlan.StartTurnNumber == currentTurn))
                {
                    if (activePlan.TryCreateContinuation(liveStamp, player.Creature.CurrentHp, damage, out SolverResult? reused)
                        && reused != null)
                    {
                        activePlan = reused;
                        BattleDamageTracker.RegisterPlan(combat, activePlan);
                    }
                    else
                    {
                        CachedContinuation? expected = activePlan.Continuations
                            .FirstOrDefault(c => c.StartTurnNumber == currentTurn);
                        if (expected != null)
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T{currentTurn}[StateMismatch]: {expected.ExpectedState.DescribeFirstDifference(liveStamp)}");
                        }
                        else if (activePlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T{currentTurn}[ContinuationMissing]: cached={activePlan.Continuations.Count}");
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
                        settings, combat, includeTurnSetup: false, theftPolicy: theftPolicy);

                    if (!hasRootHotPolicy)
                    {
                        CombatHotState rootHotState = root.HotRoot.RootState;
                        short[] slotCosts = HotPotionPolicy.BuildRootSlotCosts(
                            root, in rootHotState, root.HotRoot.Rules, policy.PotionStrategy.Thresholds);
                        rootHotPolicy = new HotSearchPolicy(
                            root, damage, policy, root.HotRoot.Rules, in rootHotState, slotCosts);
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
                    activePlan = solved;
                    BattleDamageTracker.RegisterPlan(combat, activePlan);
                }

                SolverResult currentPlan = activePlan;
                int planTurn = currentPlan.StartTurnNumber;
                List<PlanAction> actions = currentPlan.BestNode.Actions
                    .Where(a => a.Turn == planTurn && a.IsExecutable)
                    .ToList();
                PlanAction? plannedEndTurn = currentPlan.BestNode.Actions
                    .FirstOrDefault(a => a.Turn == planTurn && a.Kind == PlanActionKind.EndTurn);

                bool deploymentDrifted = false;

                for (int actionIdx = 0; actionIdx < actions.Count; actionIdx++)
                {
                    if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                        break;

                    if (player.PlayerCombatState!.TurnNumber != planTurn)
                    {
                        unplannedReplans++;
                        replanDiffs.Add($"T{planTurn}[TurnDriftMidDeploy]: actual={player.PlayerCombatState.TurnNumber}");
                        activePlan = null;
                        deploymentDrifted = true;
                        break;
                    }

                    PlanAction action = actions[actionIdx];
                    Creature? target = combat.GetCreature(action.TargetCombatId);

                    List<PlanCardChoice> choicePlans = [.. action.GetActionChoicesInExecutionOrder()];
                    if (action.TurnStartChoices is { Count: > 0 } phaseChoices)
                        choicePlans.AddRange(phaseChoices);

                    RecordPlanChoices($"T{planTurn}:{action.CardId}", choicePlans, observedChoices, observedChosenCardIds);
                    routeSegments.Add($"{planTurn}:{action.Kind}:{action.CardId}:{action.CardOccurrence}:{choicePlans.Count}");

                    RealCombatAction liveAction;
                    if (action.Kind == PlanActionKind.UsePotion)
                    {
                        PotionModel? potion = player.GetPotionAtSlotIndex(action.PotionSlot);
                        if (potion == null || !string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T{planTurn}[PotionDrift]: slot={action.PotionSlot} expected={action.PotionId}");
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }
                        liveAction = RealCombatAction.UsePotion(potion, target);
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
                            unplannedReplans++;
                            replanDiffs.Add($"T{planTurn}[CardMissing]: {ex.Message}");
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }

                        if (!card.CanPlayTargeting(target))
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T{planTurn}[CardUnplayable]: card={action.CardId} energy={player.PlayerCombatState.Energy}");
                            activePlan = null;
                            deploymentDrifted = true;
                            break;
                        }

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
                        if (currentPlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplans++;
                            replanDiffs.Add($"T{planTurn}[PlanExhaustedWithoutEndTurn]: executed={actions.Count}");
                        }
                        activePlan = null;
                        continue;
                    }

                    IReadOnlyList<PlanCardChoice> endTurnChoices = plannedEndTurn.TurnStartChoices ?? [];
                    RecordPlanChoices($"T{planTurn}->T{planTurn + 1}:Phase", endTurnChoices, observedChoices, observedChosenCardIds);
                    routeSegments.Add($"{planTurn}:EndTurn:choices={endTurnChoices.Count}");

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
            int enemyHpRemaining = combat.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp);

            int postCombatHeal = hasRootHotPolicy
                ? HotRouteQuality.RankedPostCombatRelicHeal(rootHotPolicy.PostCombatRelicHeal, won, endHp, endMaxHp)
                : 0;
            int netHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
                endPlayerHp: endHp,
                postCombatMonotoneHeal: postCombatHeal,
                initialPlayerHp: startHp,
                endPlayerMaxHp: endMaxHp,
                initialPlayerMaxHp: startMaxHp,
                deathSaveHpRestored: 0,
                growthHpCredit: 0,
                bossHpRelief: hasRootHotPolicy ? rootHotPolicy.StrategicBossHpRelief : BossHpRelief.None,
                ignoreLongTermRewards: false);
            int strategicDeficit = HotRouteQuality.RoundCentiToHp(-netHpDeltaCenti);

            (bool optPassed, string optNote) = EvaluateOptimality(
                caseSpec, won, hpLoss, currentTurn, turnSetupChoicesCount, observedChosenCardIds);

            FightOutcomeRecord outcomeRecord = new()
            {
                FightId = caseSpec.CaseId,
                Tier = "choice-l1",
                Group = caseSpec.Group,
                CharacterId = caseSpec.CharacterId,
                EncounterId = caseSpec.EncounterId,
                Won = won,
                Survives = survives,
                DeathSaveUseCount = 0,
                StartHp = startHp,
                EndHp = endHp,
                StartMaxHp = startMaxHp,
                EndMaxHp = endMaxHp,
                HpLoss = hpLoss,
                StrategicHpDeficit = strategicDeficit,
                PotionsUsed = 0,
                PotionCostHp = 0,
                NetPersistentHpDeltaCenti = netHpDeltaCenti,
                NetPotionOpportunityCostCenti = 0,
                EconomicValueCenti = netHpDeltaCenti,
                Turns = currentTurn,
                EnemyHpRemaining = enemyHpRemaining,
                ElapsedMs = Math.Round(fightSolveMs, 2),
                SolveCount = fightSolveCount,
                Boundary = fightLastBoundary,
                ExpandedNodes = fightExpandedNodes,
                Transitions = fightTransitions,
                UnplannedReplans = unplannedReplans,
                ChoiceMismatches = choiceMismatches,
                HardErrors = 0,
                TheftPolicy = fightTheftPolicy?.ToString(),
                HardError = null,
                RouteSignature = string.Join(";", routeSegments),
            };

            ChoiceCaseReport report = new()
            {
                CaseId = caseSpec.CaseId,
                Group = caseSpec.Group,
                Description = caseSpec.Description,
                CharacterId = caseSpec.CharacterId,
                EncounterId = caseSpec.EncounterId,
                IncludeTurnSetup = caseSpec.IncludeTurnSetup,
                Won = won,
                HpLoss = hpLoss,
                Turns = currentTurn,
                EnemyHpRemaining = enemyHpRemaining,
                EconomicValueV = outcomeRecord.EconomicValueV,
                ElapsedMs = Math.Round(fightSolveMs, 2),
                SolveCount = fightSolveCount,
                ExpandedNodes = fightExpandedNodes,
                Transitions = fightTransitions,
                UnplannedReplans = unplannedReplans,
                ChoiceMismatches = choiceMismatches,
                HardErrors = 0,
                TurnSetupChoicesCount = turnSetupChoicesCount,
                OptimalityPassed = optPassed,
                OptimalityNote = optNote,
                ObservedChoices = observedChoices.ToArray(),
                ReplanDiffs = replanDiffs.Count > 0 ? replanDiffs.ToArray() : null,
                HardError = null,
            };

            return (outcomeRecord, report);
        }
        catch (Exception ex)
        {
            string errDesc;
            if (FindInner<NativeChoicePlanMismatchException>(ex) is { } mismatch)
            {
                choiceMismatches++;
                errDesc = $"{mismatch.GetType().Name}: {mismatch.Message}";
            }
            else
            {
                Exception rootEx = Unwrap(ex);
                errDesc = $"{rootEx.GetType().Name}: {rootEx.Message}";
            }

            FightOutcomeRecord errOutcome = new()
            {
                FightId = caseSpec.CaseId,
                Tier = "choice-l1",
                Group = caseSpec.Group,
                CharacterId = caseSpec.CharacterId,
                EncounterId = caseSpec.EncounterId,
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
                UnplannedReplans = unplannedReplans,
                ChoiceMismatches = choiceMismatches,
                HardErrors = 1,
                HardError = errDesc,
            };

            ChoiceCaseReport errReport = new()
            {
                CaseId = caseSpec.CaseId,
                Group = caseSpec.Group,
                Description = caseSpec.Description,
                CharacterId = caseSpec.CharacterId,
                EncounterId = caseSpec.EncounterId,
                IncludeTurnSetup = caseSpec.IncludeTurnSetup,
                Won = false,
                HpLoss = startHp,
                Turns = currentTurn,
                EnemyHpRemaining = caseSpec.EnemyHp,
                EconomicValueV = -startHp,
                ElapsedMs = Math.Round(fightSolveMs, 2),
                SolveCount = fightSolveCount,
                ExpandedNodes = fightExpandedNodes,
                Transitions = fightTransitions,
                UnplannedReplans = unplannedReplans,
                ChoiceMismatches = choiceMismatches,
                HardErrors = 1,
                TurnSetupChoicesCount = turnSetupChoicesCount,
                OptimalityPassed = false,
                OptimalityNote = $"ERROR: {errDesc}",
                ObservedChoices = observedChoices.ToArray(),
                ReplanDiffs = replanDiffs.Count > 0 ? replanDiffs.ToArray() : null,
                HardError = errDesc,
            };

            return (errOutcome, errReport);
        }
        finally
        {
            _activeTurnSetupHook = null;
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    [HarmonyPriority(Priority.First)]
    private static bool SetupPlayerTurnPrefix(
        CombatManager __instance,
        object __0,
        Player __1,
        HookPlayerChoiceContext __2,
        ref Task __result)
    {
        TurnSetupHookContext? hook = _activeTurnSetupHook;
        if (_invokingOriginalSetup
            || hook == null
            || hook.Intercepted
            || __1.PlayerCombatState is not { TurnNumber: 1, Phase: PlayerTurnPhase.Start })
        {
            return true;
        }

        CombatState? combat = __instance.DebugOnlyGetState();
        if (combat == null || !ReferenceEquals(LocalContext.GetMe(combat), __1))
            return true;

        hook.Intercepted = true;
        __result = RunInterceptedTurnSetupAsync(__instance, __0, __1, __2, combat, hook);
        return false;
    }

    private static async Task RunInterceptedTurnSetupAsync(
        CombatManager manager,
        object turnState,
        Player player,
        HookPlayerChoiceContext choiceContext,
        CombatState combat,
        TurnSetupHookContext hook)
    {
        try
        {
            await ApplyTurnSetupPreDrawFixtureAsync(combat, player, hook.Case);

            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                settings, combat, includeTurnSetup: true,
                theftPolicy: SolverController.ResolveTheftPolicy(combat));

            Stopwatch sw = Stopwatch.StartNew();
            SolverResult solved = HotSearchCoordinator.Solve(
                root, names, damage, policy, CancellationToken.None, null);
            sw.Stop();

            hook.SolvedResult = solved;
            hook.RootSnapshot = root;
            hook.SearchPolicy = policy;
            hook.DamageSnapshot = damage;
            hook.SolveMs = sw.Elapsed.TotalMilliseconds;

            PlanDrivenCardSelector turnSetupSelector = new(solved.TurnSetupChoices);
            using (RunManager.Instance.CardSelectorStack.PushSelector(player, turnSetupSelector))
            {
                Task originalTask;
                _invokingOriginalSetup = true;
                try
                {
                    originalTask = (Task)SetupPlayerTurnMethod.Invoke(manager, [turnState, player, choiceContext])!;
                }
                finally
                {
                    _invokingOriginalSetup = false;
                }

                await originalTask;
            }

            if (turnSetupSelector.LastError != null)
                throw turnSetupSelector.LastError;
        }
        catch (Exception ex)
        {
            hook.Error = ex;
            throw;
        }
    }

    private static async Task ApplyTurnSetupPreDrawFixtureAsync(
        CombatState combat,
        Player player,
        ChoiceCaseSpec caseSpec)
    {
        player.Creature.SetMaxHpInternal(caseSpec.PlayerMaxHp);
        player.Creature.SetCurrentHpInternal(Math.Min(caseSpec.PlayerHp, caseSpec.PlayerMaxHp));
        SetPrimaryEnemyHp(combat, caseSpec.EnemyHp);

        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("TurnSetup 注入时缺少 PlayerCombatState。");
        CardModel[] existing =
        [
            .. pcs.Hand.Cards,
            .. pcs.DrawPile.Cards,
            .. pcs.DiscardPile.Cards,
            .. pcs.ExhaustPile.Cards,
        ];
        if (existing.Length > 0)
            await CardPileCmd.RemoveFromCombat(existing, skipVisuals: true);

        InjectPileCards(combat, player, PileType.Draw, caseSpec.DrawPile);
        InjectPileCards(combat, player, PileType.Discard, caseSpec.DiscardPile);
        InjectPileCards(combat, player, PileType.Exhaust, caseSpec.ExhaustPile);
    }

    private static async Task ApplyPlayPhaseFixtureAsync(
        CombatState combat,
        Player player,
        ChoiceCaseSpec caseSpec)
    {
        player.Creature.SetMaxHpInternal(caseSpec.PlayerMaxHp);
        player.Creature.SetCurrentHpInternal(Math.Min(caseSpec.PlayerHp, caseSpec.PlayerMaxHp));
        SetPrimaryEnemyHp(combat, caseSpec.EnemyHp);

        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("Play 阶段注入时缺少 PlayerCombatState。");

        CardModel[] existing =
        [
            .. pcs.Hand.Cards,
            .. pcs.DrawPile.Cards,
            .. pcs.DiscardPile.Cards,
            .. pcs.ExhaustPile.Cards,
        ];
        if (existing.Length > 0)
            await CardPileCmd.RemoveFromCombat(existing, skipVisuals: true);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        InjectPileCards(combat, player, PileType.Hand, caseSpec.Hand);
        InjectPileCards(combat, player, PileType.Draw, caseSpec.DrawPile);
        InjectPileCards(combat, player, PileType.Discard, caseSpec.DiscardPile);
        InjectPileCards(combat, player, PileType.Exhaust, caseSpec.ExhaustPile);

        if (pcs.Energy > caseSpec.Energy)
            pcs.LoseEnergy(pcs.Energy - caseSpec.Energy);
        else if (pcs.Energy < caseSpec.Energy)
            pcs.GainEnergy(caseSpec.Energy - pcs.Energy);

        // Turn 2+ 遗物在 Turn 1 Setup 完成之后静默注入，避免在 StartCombat 阶段提前消耗
        HashSet<ModelId> ownedRelics = player.Relics.Select(static r => r.Id).ToHashSet();
        foreach (string relicId in caseSpec.PostStartRelics)
        {
            RelicModel canonical = RealCombatDriver.ResolveModel(ModelDb.AllRelics, relicId, "遗物");
            if (!ownedRelics.Add(canonical.Id))
                continue;
            player.AddRelicInternal(canonical.ToMutable(), -1, silent: true);
        }

        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void SetPrimaryEnemyHp(CombatState combat, int targetHp)
    {
        Creature enemy = combat.Enemies.FirstOrDefault(static e => e.IsAlive)
            ?? throw new InvalidOperationException("场景缺少存活敌人。");
        enemy.SetMaxHpInternal(targetHp);
        enemy.SetCurrentHpInternal(targetHp);
    }

    private static void InjectPileCards(
        CombatState combat,
        Player player,
        PileType pileType,
        CardSpec[] specs)
    {
        if (specs.Length == 0)
            return;
        CardPile pile = CardPile.Get(pileType, player)
            ?? throw new InvalidOperationException($"无法获取玩家牌堆 {pileType}。");

        foreach (CardSpec spec in specs)
        {
            CardModel canonical = RealCombatDriver.ResolveModel(ModelDb.AllCards, spec.CardId, "卡牌");
            CardModel card = combat.CreateCard(canonical, player);
            for (int u = 0; u < spec.UpgradeLevel && card.IsUpgradable; u++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            pile.AddInternal(card, -1);
        }
    }

    private static void RecordPlanChoices(
        string prefix,
        IReadOnlyList<PlanCardChoice> choices,
        List<string> observedChoices,
        List<string> observedChosenCardIds)
    {
        foreach (PlanCardChoice choice in choices)
        {
            string source = string.IsNullOrEmpty(choice.SourceId) ? choice.Effect.ToString() : choice.SourceId;
            string cardsText = choice.Cards.Count == 0
                ? "(none)"
                : string.Join(",", choice.Cards.Select(static c => c.UpgradeLevel > 0 ? $"{c.CardId}+{c.UpgradeLevel}" : c.CardId));
            observedChoices.Add($"{prefix}[{source}/{choice.Effect}]->[{cardsText}]");
            foreach (PlanCardToken token in choice.Cards)
                observedChosenCardIds.Add(token.CardId);
        }
    }

    private static (bool Passed, string Note) EvaluateOptimality(
        ChoiceCaseSpec caseSpec,
        bool won,
        int hpLoss,
        int turns,
        int turnSetupChoicesCount,
        List<string> observedChosenCardIds)
    {
        List<string> failures = [];

        if (caseSpec.ExpectWon && !won)
            failures.Add("expected_win=true");
        if (hpLoss > caseSpec.MaxExpectedHpLoss)
            failures.Add($"hp_loss={hpLoss}>{caseSpec.MaxExpectedHpLoss}");
        if (caseSpec.MaxExpectedTurns is { } maxTurns && turns > maxTurns)
            failures.Add($"turns={turns}>{maxTurns}");
        if (caseSpec.MinTurnSetupChoices is { } minSetup && turnSetupChoicesCount < minSetup)
            failures.Add($"setup_choices={turnSetupChoicesCount}<{minSetup}");

        foreach (string requiredId in caseSpec.RequiredChosenCardIds)
        {
            if (!observedChosenCardIds.Contains(requiredId, StringComparer.Ordinal))
                failures.Add($"missing_pick={requiredId}");
        }

        foreach (string forbiddenId in caseSpec.ForbiddenChosenCardIds)
        {
            if (observedChosenCardIds.Contains(forbiddenId, StringComparer.Ordinal))
                failures.Add($"burned_key_card={forbiddenId}");
        }

        if (failures.Count == 0)
            return (true, $"ok(T{turns},hpLoss={hpLoss})");
        return (false, string.Join("; ", failures));
    }

    private static List<ChoiceCaseSpec> BuildChoiceCases() =>
    [
        // 1. Wish（许愿）：25 张不同抽牌堆，唯一致胜组合牌 BLUDGEON+1 位于第 25 张（越过旧 MaxChoicesPerAction=16 / MaxPile=18）
        new()
        {
            CaseId = "choice-01-wish-25card-draw",
            Group = "pile_search",
            Description = "Wish 25-card distinct DrawPile: Bash(2E)+Wish(0E->Bludgeon+@idx24)+Bludgeon+(3E)=71>=55 T1 lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH01WISH",
            PlayerHp = 10,
            EnemyHp = 55,
            Energy = 5,
            Hand = [new("WISH"), new("BASH"), new("DEFEND_IRONCLAD")],
            DrawPile =
            [
                new("DEMON_FORM"), new("BARRICADE"), new("CORRUPTION"), new("DARK_EMBRACE"), new("FEEL_NO_PAIN"),
                new("INFLAME"), new("JUGGERNAUT"), new("APOTHEOSIS"), new("MAYHEM"), new("PANACHE"),
                new("FLAME_BARRIER"), new("SHRUG_IT_OFF"), new("ARMAMENTS"), new("TRUE_GRIT"), new("BURNING_PACT"),
                new("BATTLE_TRANCE"), new("BLOODLETTING"), new("SHOCKWAVE"), new("UPPERCUT"), new("SWORD_BOOMERANG"),
                new("THUNDERCLAP"), new("IRON_WAVE"), new("POMMEL_STRIKE"), new("TWIN_STRIKE"),
                new("BLUDGEON", 1),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 1,
            RequiredChosenCardIds = ["BLUDGEON"],
        },

        // 2. Headbutt（头槌）：12 张不同弃牌堆，回合中段置顶 BLUDGEON+1 到 Draw[0] 实现 T2 零伤斩杀
        new()
        {
            CaseId = "choice-02-headbutt-t2-survival",
            Group = "draw_top",
            Description = "Headbutt 12-card DiscardPile: put Bludgeon+@idx11 on Draw[0] + Strike mid-turn -> T2 lethal 0 HP loss",
            CharacterId = "IRONCLAD",
            Seed = "CH02HEAD",
            PlayerHp = 15,
            EnemyHp = 50,
            Energy = 3,
            Hand = [new("HEADBUTT"), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"), new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("DEFEND_IRONCLAD"), new("DEFEND_IRONCLAD"), new("DEFEND_IRONCLAD"),
                new("WOUND"), new("WOUND"), new("SLIMED"),
            ],
            DiscardPile =
            [
                new("ANGER"), new("IRON_WAVE"), new("TWIN_STRIKE"), new("POMMEL_STRIKE"), new("SHRUG_IT_OFF"),
                new("ARMAMENTS"), new("TRUE_GRIT"), new("BURNING_PACT"), new("BLOODLETTING"), new("THUNDERCLAP"),
                new("SWORD_BOOMERANG"), new("BLUDGEON", 1),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            RequiredChosenCardIds = ["BLUDGEON"],
        },

        // 3. Cosmic Indifference（宇宙冷漠）：12 张不同弃牌堆，必须把末尾 IMPERVIOUS 置顶活过 T2 21 伤，T3 用 BLUDGEON+1 斩杀
        new()
        {
            CaseId = "choice-03-cosmic-indifference-t2-survival",
            Group = "draw_top",
            Description = "CosmicIndifference 12-card DiscardPile: put Impervious@idx11 on Draw[0] to block T2 21dmg -> T3 Bludgeon+ win",
            CharacterId = "REGENT",
            Seed = "CH03COSM",
            PlayerHp = 5,
            EnemyHp = 42,
            Energy = 3,
            Hand = [new("COSMIC_INDIFFERENCE"), new("STRIKE_REGENT"), new("DEFEND_REGENT"), new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("WOUND"), new("SLIMED"), new("DEFEND_REGENT"), new("DEFEND_REGENT"), new("WOUND"),
                new("BLUDGEON", 1), new("STRIKE_REGENT"), new("STRIKE_REGENT"), new("DEFEND_REGENT"),
            ],
            DiscardPile =
            [
                new("STRIKE_REGENT"), new("SOLAR_STRIKE"), new("CRESCENT_SPEAR"), new("FALLING_STAR"),
                new("GLOW"), new("ALIGNMENT"), new("SLICE"), new("DEFLECT"), new("ANGER"),
                new("IRON_WAVE"), new("TWIN_STRIKE"), new("IMPERVIOUS"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 3,
            RequiredChosenCardIds = ["IMPERVIOUS"],
        },

        // 4. Graveblast（坟冢爆射）：20 张不同弃牌堆，唯一致胜牌 BLUDGEON+1 在第 20 张（越过旧 16/18 截断）
        new()
        {
            CaseId = "choice-04-graveblast-20card-discard",
            Group = "pile_search",
            Description = "Graveblast+ 20-card DiscardPile: 6dmg + return Bludgeon+@idx19 -> 48>=48 T1 lethal",
            CharacterId = "NECROBINDER",
            Seed = "CH04GRVB",
            PlayerHp = 10,
            EnemyHp = 48,
            Energy = 4,
            Hand = [new("GRAVEBLAST", 1), new("DEFEND_NECROBINDER"), new("WOUND"), new("DAZED")],
            DrawPile = [new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"), new("DEFEND_NECROBINDER"), new("DEFEND_NECROBINDER")],
            DiscardPile =
            [
                new("SLICE"), new("DEFLECT"), new("ANGER"), new("DEFILE"), new("DEFY"),
                new("SCULPTING_STRIKE"), new("BLIGHT_STRIKE"), new("CAPTURE_SPIRIT"), new("DEATH_MARCH"), new("IRON_WAVE"),
                new("TWIN_STRIKE"), new("POMMEL_STRIKE"), new("SHRUG_IT_OFF"), new("ARMAMENTS"), new("TRUE_GRIT"),
                new("BURNING_PACT"), new("SWORD_BOOMERANG"), new("THUNDERCLAP"), new("UPPERCUT"),
                new("BLUDGEON", 1),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 1,
            RequiredChosenCardIds = ["BLUDGEON"],
        },

        // 5. Charge!!（冲锋！）：15 张抽牌堆选 2 变换 MinionDiveBomb+；IMPERVIOUS 在 idx0（防 c_0 钉死），须变顶部砖牌保住 IMPERVIOUS 与 BLUDGEON+1
        new()
        {
            CaseId = "choice-05-charge-15card-draw",
            Group = "multi_pick_pile",
            Description = "Charge+ 15-card DrawPile (k=2): transform top-5 bricks while preserving Impervious@idx0 & Bludgeon+@idx5",
            CharacterId = "REGENT",
            Seed = "CH05CHRG",
            PlayerHp = 12,
            EnemyHp = 55,
            Energy = 3,
            Hand = [new("CHARGE", 1), new("DEFEND_REGENT"), new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("IMPERVIOUS"), new("WOUND"), new("SLIMED"), new("WOUND"), new("DAZED"),
                new("BLUDGEON", 1), new("DEMON_FORM"), new("ECHO_FORM"), new("WRAITH_FORM"), new("BARRICADE"),
                new("INFLAME"), new("FOOTWORK"), new("AFTERIMAGE"), new("ACCURACY"), new("NOXIOUS_FUMES"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 3,
            ForbiddenChosenCardIds = ["IMPERVIOUS", "BLUDGEON"],
        },

        // 6. Entropy(1)（熵 1 层）：回合初确定性随机变换 1 张手牌，禁止误烧 idx0 的 BLUDGEON+1
        new()
        {
            CaseId = "choice-06-entropy-1-turnstart",
            Group = "turn_start",
            Description = "Entropy(1) Turn-2 start transform 1: preserve Bludgeon+@idx0 for T2 42dmg lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH06ENT1",
            PlayerHp = 5,
            EnemyHp = 42,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("ENTROPY_POWER", 1, OnPlayer: true)],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 7. Entropy(2)（熵 2 层）：回合初多选同时变换 2 张手牌，必须同时保留 BLUDGEON+1 与 SLICE
        new()
        {
            CaseId = "choice-07-entropy-2-turnstart",
            Group = "turn_start",
            Description = "Entropy(2) Turn-2 start simultaneous transform 2: preserve Bludgeon+ & Slice for T2 48dmg lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH07ENT2",
            PlayerHp = 5,
            EnemyHp = 48,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("ENTROPY_POWER", 2, OnPlayer: true)],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 8. Tyranny(1)（暴政 1 层）：测试旧 zeroCostPlayable 陷阱，禁止消耗 3 费 BLUDGEON+1
        new()
        {
            CaseId = "choice-08-tyranny-1-turnstart",
            Group = "turn_start",
            Description = "Tyranny(1) Turn-2 start exhaust 1: avoid zeroCostPlayable trap, preserve Bludgeon+@idx0",
            CharacterId = "REGENT",
            Seed = "CH08TYR1",
            PlayerHp = 5,
            EnemyHp = 42,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("TYRANNY_POWER", 1, OnPlayer: true)],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_REGENT"), new("DEFEND_REGENT"), new("WOUND"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 9. Tyranny(2)（暴政 2 层）：回合初消耗 2 张，测多选排重与保住 BLUDGEON+1 + SLICE
        new()
        {
            CaseId = "choice-09-tyranny-2-turnstart",
            Group = "turn_start",
            Description = "Tyranny(2) Turn-2 start exhaust 2: avoid c_0 fixation, preserve Bludgeon+ & Slice for T2 48dmg lethal",
            CharacterId = "REGENT",
            Seed = "CH09TYR2",
            PlayerHp = 5,
            EnemyHp = 48,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("TYRANNY_POWER", 2, OnPlayer: true)],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_REGENT"), new("DEFEND_REGENT"),
                new("WOUND"), new("DAZED"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON", "SLICE"],
        },

        // 10. ToastyMittens（烘焙手套）：Turn-2 回合初消耗 1 张手牌并 +1 力量，靠 BLUDGEON+1 (42+1=43) 精确斩杀
        new()
        {
            CaseId = "choice-10-toasty-mittens-turnstart",
            Group = "turn_start",
            Description = "ToastyMittens Turn-2 start exhaust 1 (+1 Str): preserve Bludgeon+@idx0 for exact 43dmg lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH10MITT",
            PlayerHp = 5,
            EnemyHp = 43,
            Energy = 3,
            PostStartRelics = ["TOASTY_MITTENS"],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 11. ToolsOfTheTrade（必备工具）：Turn-2 回合初弃 1 张手牌，保住 BLUDGEON+1 + SLICE 48 伤斩杀
        new()
        {
            CaseId = "choice-11-tools-of-the-trade-turnstart",
            Group = "turn_start",
            Description = "ToolsOfTheTrade(1) Turn-2 start discard 1: preserve Bludgeon+ & Slice for T2 48dmg lethal",
            CharacterId = "SILENT",
            Seed = "CH11TOOL",
            PlayerHp = 5,
            EnemyHp = 48,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("TOOLS_OF_THE_TRADE_POWER", 1, OnPlayer: true)],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_SILENT"), new("DEFEND_SILENT"), new("WOUND"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON", "SLICE"],
        },

        // 12. 复合回合初：ToolsOfTheTrade + ToastyMittens（直击 #294 Bug #5 跨组 sameBatch 合并缺陷）
        new()
        {
            CaseId = "choice-12-compound-tools-plus-mittens",
            Group = "turn_start_compound",
            Description = "Compound ToolsOfTheTrade(Discard) + ToastyMittens(Exhaust +1 Str): verifies distinct choice groups & 0-mismatch parity",
            CharacterId = "SILENT",
            Seed = "CH12CMP1",
            PlayerHp = 5,
            EnemyHp = 49,
            Energy = 3,
            InitialPowers = [new PowerInjectionSpec("TOOLS_OF_THE_TRADE_POWER", 1, OnPlayer: true)],
            PostStartRelics = ["TOASTY_MITTENS"],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_SILENT"), new("DEFEND_SILENT"), new("WOUND"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON", "SLICE"],
        },

        // 13. 复合回合初：Entropy(1) + Tyranny(1)（直击 #294 Bug #5 跨组 sameBatch 合并缺陷）
        new()
        {
            CaseId = "choice-13-compound-entropy-plus-tyranny",
            Group = "turn_start_compound",
            Description = "Compound Entropy(Transform) + Tyranny(Exhaust): verifies distinct choice groups & 0-mismatch parity",
            CharacterId = "REGENT",
            Seed = "CH13CMP2",
            PlayerHp = 5,
            EnemyHp = 48,
            Energy = 3,
            InitialPowers =
            [
                new PowerInjectionSpec("ENTROPY_POWER", 1, OnPlayer: true),
                new PowerInjectionSpec("TYRANNY_POWER", 1, OnPlayer: true),
            ],
            Hand = [new("WOUND"), new("DAZED")],
            DrawPile =
            [
                new("BLUDGEON", 1), new("SLICE"), new("DEFLECT"), new("STRIKE_REGENT"), new("DEFEND_REGENT"), new("WOUND"),
                new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 14. Depth-0 首回合 PendingTurnSetup：Toolbox × ToastyMittens（includeTurnSetup: true）
        new()
        {
            CaseId = "choice-14-turnsetup-toolbox-plus-mittens",
            Group = "turn_setup",
            Description = "Depth-0 Turn-1 PendingTurnSetup Toolbox + ToastyMittens: full pre-T1 expansion + TurnSetupPlayState parity + T1 43dmg lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH14TBXM",
            IncludeTurnSetup = true,
            PlayerHp = 10,
            EnemyHp = 43,
            PreCombatRelics = ["TOOLBOX", "TOASTY_MITTENS"],
            DrawPile =
            [
                new("BLUDGEON", 1), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"), new("WOUND"), new("DAZED"),
                new("DEFEND_IRONCLAD"), new("STRIKE_IRONCLAD"), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 1,
            MinTurnSetupChoices = 2,
            ForbiddenChosenCardIds = ["BLUDGEON"],
        },

        // 15. Depth-0 首回合 PendingTurnSetup：GamblingChip（0..5 子集重抽，必须留 idx0 SLICE、弃 4 张状态牌摸到 idx8 BLUDGEON+1）
        new()
        {
            CaseId = "choice-15-turnsetup-gambling-chip",
            Group = "turn_setup",
            Description = "Depth-0 Turn-1 PendingTurnSetup GamblingChip: keep Slice@idx0 & mulligan 4 statuses to draw Bludgeon+@idx8 -> T1 48dmg lethal",
            CharacterId = "IRONCLAD",
            Seed = "CH15GCHP",
            IncludeTurnSetup = true,
            PlayerHp = 5,
            EnemyHp = 48,
            PreCombatRelics = ["GAMBLING_CHIP"],
            DrawPile =
            [
                new("SLICE"), new("WOUND"), new("BURN"), new("SLIMED"), new("DECAY"),
                new("DEFEND_IRONCLAD"), new("STRIKE_IRONCLAD"), new("DEFEND_IRONCLAD"), new("BLUDGEON", 1), new("WOUND"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 1,
            MinTurnSetupChoices = 1,
            ForbiddenChosenCardIds = ["SLICE"],
        },

        // 16. Dredge（k=3 多选弃牌堆）：15 张不同弃牌堆，idx0 为 3 费陷阱 DEMON_FORM，最优三元组位于 idx12..14
        new()
        {
            CaseId = "choice-16-dredge-k3-discard",
            Group = "multi_pick_pile",
            Description = "Dredge 15-card DiscardPile (k=3): avoid DemonForm@idx0 c_0 trap, pick {Bludgeon+,Slice,Anger}@idx12..14 -> T1 54>=52 lethal",
            CharacterId = "NECROBINDER",
            Seed = "CH16DRDG",
            PlayerHp = 10,
            EnemyHp = 52,
            Energy = 4,
            Hand = [new("DREDGE"), new("WOUND"), new("DAZED")],
            DrawPile = [new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND"), new("WOUND")],
            DiscardPile =
            [
                new("DEMON_FORM"), new("BARRICADE"), new("CORRUPTION"), new("DARK_EMBRACE"), new("FEEL_NO_PAIN"),
                new("INFLAME"), new("JUGGERNAUT"), new("FLAME_BARRIER"), new("SHRUG_IT_OFF"), new("ARMAMENTS"),
                new("TRUE_GRIT"), new("BURNING_PACT"),
                new("BLUDGEON", 1), new("SLICE"), new("ANGER"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 1,
            RequiredChosenCardIds = ["BLUDGEON", "SLICE", "ANGER"],
            ForbiddenChosenCardIds = ["DEMON_FORM"],
        },

        // 17. Purity+（0..5 可选子集消耗）：必须精准烧掉全部 4 张 Burn/Decay（防回合末 8 伤致死）同时保留 idx1 BLUDGEON+1 与 SLICE
        new()
        {
            CaseId = "choice-17-purity-plus-subset",
            Group = "optional_subset",
            Description = "Purity+ (0..5 Hand exhaust): exhaust all 4 Burn/Decay while keeping Bludgeon+@idx1 & Slice -> T2 0 HP loss win",
            CharacterId = "IRONCLAD",
            Seed = "CH17PURT",
            PlayerHp = 6,
            EnemyHp = 56,
            Energy = 3,
            Hand =
            [
                new("PURITY", 1), new("BLUDGEON", 1), new("BURN"), new("BURN"), new("BURN"), new("DECAY"), new("SLICE"),
            ],
            DrawPile =
            [
                new("IMPERVIOUS"), new("STRIKE_IRONCLAD"), new("STRIKE_IRONCLAD"),
                new("DEFEND_IRONCLAD"), new("DEFEND_IRONCLAD"),
            ],
            MaxExpectedHpLoss = 0,
            MaxExpectedTurns = 2,
            ForbiddenChosenCardIds = ["BLUDGEON", "SLICE"],
        },
    ];

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
        while (ex is TargetInvocationException or AggregateException && ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
}
