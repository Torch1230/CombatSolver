using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] rawArgs)
    {
        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(rawArgs);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return 2;
        }

        HarnessLog.VerboseGameLog = options.VerboseGameLog;
        HarnessLog.Language = options.Language;
        Directory.CreateDirectory(options.OutputDirectory);

        List<StepRecord> steps = [];
        Dictionary<string, object?> payload = new()
        {
            ["label"] = options.Label,
            ["scenario"] = options.Scenario,
            ["requestPath"] = options.RequestPath,
            ["milestone"] = options.Milestone,
        };

        string reached = "none";
        int exitCode = 0;
        IDisposable? choiceScope = null;
        try
        {
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);

            Step(steps, "M0.1 装 Godot 绕过补丁", () =>
            {
                GameBootstrap.ApplyGodotBypasses();
                int nodeCctors = GameBootstrap.SkipGodotNodeStaticConstructors();
                return $"{GameBootstrap.Bypasses.Count} 处绕过（含 {nodeCctors} 个节点静态构造）";
            });
            Step(steps, "M0.2 初始化游戏静态状态", GameBootstrap.InitializeStaticState);
            Step(steps, "M0.3 初始化模组运行期状态", () => ModRuntime.Initialize(options));
            if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_PROBE_STATICS") is { Length: > 0 } filter)
                Step(steps, "P 静态构造探针", () => $"types={GameBootstrap.ProbeStaticConstructors(filter)}");
            GeneratedScenarioSetup? generated = null;
            UnattendedTestRunner.OfflineScenarioSession? session = null;
            CombatState? combat = null;

            if (options.RequestPath != null)
            {
                Step(steps, "G1.1 解析生成场景请求", () =>
                {
                    UnattendedTestRequest request = GeneratedScenarioSetup.ReadRequest(options.RequestPath);
                    generated = GeneratedScenarioSetup.Prepare(
                        request, Path.Combine(options.OutputDirectory, "evidence"));
                    session = generated.Session;
                    var resolvedOptions = generated.Resolved.Options;
                    return $"character={resolvedOptions.CharacterId} encounter={resolvedOptions.EncounterId} "
                        + $"act={resolvedOptions.ActIndex} A{resolvedOptions.Ascension} "
                        + $"catalog={generated.Resolved.CatalogFingerprint[..12]}";
                });
                payload["resolvedScenario"] = generated!.Resolved.Options;
                payload["catalogFingerprint"] = generated.Resolved.CatalogFingerprint;

                Step(steps, "G1.2 建跑局、注入装备、进遭遇战房间", () =>
                {
                    Task<IDisposable?> enter = generated!.EnterCombatRoomAsync();
                    loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(300), "生成场景进房");
                    choiceScope = enter.GetAwaiter().GetResult();
                    return $"pumped={loop.PumpedCallbacks}";
                });
                Step(steps, "G1.3 推进到玩家第一回合", () =>
                {
                    combat = OfflineCombat.WaitForPlayableCombat(loop);
                    generated!.CaptureOpening(combat);
                    return OfflineCombat.DescribeRoot(combat);
                });
                payload["setupChoices"] = generated.SetupChoices;
            }
            else
            {
                session = UnattendedTestRunner.OfflineScenarioSession.Create(new UnattendedTestRequest());
                Step(steps, "M1.1 建跑局并进入遭遇战房间", () =>
                {
                    Task enter = OfflineCombat.EnterCombatRoomAsync(options.Scenario);
                    loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), "EnterRoomDebug");
                    return $"pumped={loop.PumpedCallbacks}";
                });
                Step(steps, "M1.2 推进到玩家第一回合", () =>
                {
                    combat = OfflineCombat.WaitForPlayableCombat(loop);
                    return OfflineCombat.DescribeRoot(combat);
                });
            }

            reached = "M1";
            payload["budget"] = DescribeBudget(options);
            payload["root"] = OfflineCombat.DescribeRoot(combat!);
            string diagnostics = ModRuntime.DescribeStart(combat!);
            payload["rootDiagnostics"] = diagnostics;
            File.WriteAllText(Path.Combine(options.OutputDirectory, "root-diagnostics.txt"), diagnostics);
            WriteProgress(options, "M1", "ok", "已到达玩家第一回合");

            if (options.Milestone != "M1")
            {
                ModRuntime.SearchOutcome? outcome = null;
                using MemorySampler memory = new(TimeSpan.FromMilliseconds(100));
                Step(steps, $"M2.1 跑一次固定预算搜索（{options.SearchMode}）", () =>
                {
                    outcome = ModRuntime.RunSearchDetailed(combat!, options, loop, session);
                    var solver = outcome.LegacyMetrics;
                    return $"boundary={solver["boundary"]} total_expanded={solver["totalExpanded"]} "
                        + $"total_transitions={solver["totalTransitions"]} score={solver["score"]} "
                        + $"projected_battle_hp_lost={solver["projectedBattleHpLost"]} "
                        + $"wall_s={outcome.WallSeconds:0.00}";
                });
                payload["search"] = new Dictionary<string, object?>
                {
                    ["rootContinuationStamp"] = outcome!.RootContinuationStamp,
                    ["rootLiveStamp"] = outcome.RootLiveStamp,
                    ["rootCapture"] = outcome.RootCapture,
                    ["solverMetrics"] = outcome.LegacyMetrics,
                    ["planActions"] = outcome.PlanActions,
                    ["cachedContinuations"] = outcome.Result.Continuations
                        .Select(item => new
                        {
                            item.StartTurnNumber,
                            item.ForecastOffset,
                            state = item.ExpectedState.StateText,
                        })
                        .ToArray(),
                    ["continuations"] = outcome.Continuations,
                    ["patchLog"] = ModRuntime.PatchLog.ToArray(),
                };
                // 与游戏内 result.json 同名同形的那一份（游戏自己的 Writer 造的）。
                payload["solverMetrics"] = outcome.SolverMetrics;
                // 宿主自己从 SolverResult 读的剪枝/复用计数（游戏内 result.json 没有这些字段）。
                payload["pruneCounters"] = outcome.LegacyMetrics;
                payload["searchPolicy"] = outcome.Policy;
                payload["phasePerformance"] = ModRuntime.LastPhasePerformance;
                File.WriteAllText(
                    Path.Combine(options.OutputDirectory, "search-policy.json"),
                    JsonSerializer.Serialize(outcome.Policy, UnattendedTestFiles.JsonOptions));
                payload["timeBoundaryObserved"] = outcome.TimeBoundaryObserved;
                payload["wallSeconds"] = outcome.WallSeconds;
                memory.Dispose();
                payload["peakManagedHeapBytes"] = memory.PeakManagedHeapBytes;
                payload["peakManagedLiveBytes"] = memory.PeakManagedLiveBytes;
                payload["peakWorkingSetBytes"] = memory.PeakWorkingSetBytes;
                payload["memorySamples"] = memory.Samples;
                payload["totalAllocatedBytes"] = GC.GetTotalAllocatedBytes(precise: false);

                File.WriteAllText(
                    Path.Combine(options.OutputDirectory, "route.json"),
                    JsonSerializer.Serialize(outcome.RouteActions, UnattendedTestFiles.JsonOptions));

                reached = "M2";
                WriteProgress(options, "M2", "ok", "搜索完成并产出指标");
            }
        }
        catch (Exception error)
        {
            Exception root = Unwrap(error);
            payload["error"] = new
            {
                type = root.GetType().FullName,
                message = root.Message,
                stack = root.StackTrace,
            };
            WriteProgress(options, NextMilestone(reached), "blocked", $"{root.GetType().Name}: {root.Message}");
            Console.Error.WriteLine($"[FAIL] {root.GetType().Name}: {root.Message}");
            // 包装异常（如 SearchTransitionException）的真正原因在 InnerException 上，必须打出来。
            for (Exception? inner = root.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine($"[FAIL:inner] {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(root.StackTrace);
            exitCode = 1;
        }
        finally
        {
            choiceScope?.Dispose();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        payload["reachedMilestone"] = reached;
        payload["bypasses"] = GameBootstrap.Bypasses;
        payload["steps"] = steps;
        string resultPath = Path.Combine(options.OutputDirectory, "harness-result.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(payload, Json));
        // run_plan.py 直接消费的一份：solverMetrics 与游戏内 result.json 同名同形。
        File.WriteAllText(
            Path.Combine(options.OutputDirectory, "result.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["label"] = options.Label,
                ["status"] = exitCode == 0 ? "Passed" : "Failed",
                ["reachedMilestone"] = reached,
                ["requestPath"] = options.RequestPath,
                ["profile"] = options.Profile,
                ["searchMode"] = options.SearchMode,
                ["budget"] = payload.GetValueOrDefault("budget"),
                ["solverMetrics"] = payload.GetValueOrDefault("solverMetrics"),
                ["pruneCounters"] = payload.GetValueOrDefault("pruneCounters"),
                ["timeBoundaryObserved"] = payload.GetValueOrDefault("timeBoundaryObserved"),
                ["wallSeconds"] = payload.GetValueOrDefault("wallSeconds"),
                ["peakManagedHeapBytes"] = payload.GetValueOrDefault("peakManagedHeapBytes"),
                ["peakManagedLiveBytes"] = payload.GetValueOrDefault("peakManagedLiveBytes"),
                ["peakWorkingSetBytes"] = payload.GetValueOrDefault("peakWorkingSetBytes"),
                ["totalAllocatedBytes"] = payload.GetValueOrDefault("totalAllocatedBytes"),
                ["rootContinuationStamp"] = payload.GetValueOrDefault("search") is Dictionary<string, object?> s
                    ? s.GetValueOrDefault("rootContinuationStamp")
                    : null,
                ["continuations"] = payload.GetValueOrDefault("search") is Dictionary<string, object?> search
                    ? search.GetValueOrDefault("continuations")
                    : null,
                ["catalogFingerprint"] = payload.GetValueOrDefault("catalogFingerprint"),

                ["error"] = payload.GetValueOrDefault("error"),
            }, UnattendedTestFiles.JsonOptions));

        Console.WriteLine();
        Console.WriteLine("| 步骤 | 结果 | 详情 | 耗时 ms |");
        Console.WriteLine("|---|---|---|---:|");
        foreach (StepRecord step in steps)
            Console.WriteLine($"| {step.Name} | {step.Status} | {step.Detail.Replace("|", "\\|")} | {step.Milliseconds} |");
        Console.WriteLine();
        Console.WriteLine($"reached={reached} result={resultPath}");
        return exitCode;
    }

    private static object DescribeBudget(HarnessOptions options)
    {
        SolverSearchProfile profile = ModRuntime.ResolveProfile(options);
        return new
        {
            options.Profile,
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            profile.MaxCardBranchesPerNode,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction,
            options.MaxDegreeOfParallelism,
            options.BudgetMilliseconds,
            options.PotionPolicy,
            options.SearchMode,
            options.UsePortfolio,
            fixedSearchBudget = true,
            enableNoGcRegion = options.EnableNoGcRegion,
            noGcRegionBudgetGigabytes = options.EnableNoGcRegion
                ? options.NoGcRegionBudgetGigabytes
                : (double?)null,
        };
    }

    private static string NextMilestone(string reached) => reached switch
    {
        "none" => "M1",
        "M1" => "M2",
        _ => "M3",
    };

    private static void Step(List<StepRecord> steps, string name, Func<string> body)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            string detail = body();
            steps.Add(new StepRecord(name, "ok", detail, watch.ElapsedMilliseconds));
            Console.WriteLine($"[ok]   {name}: {detail} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception error)
        {
            Exception root = Unwrap(error);
            steps.Add(new StepRecord(name, "FAIL", $"{root.GetType().Name}: {root.Message}", watch.ElapsedMilliseconds));
            Console.WriteLine($"[FAIL] {name}: {root.GetType().Name}: {root.Message} ({watch.ElapsedMilliseconds} ms)");
            throw;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException or AggregateException && error.InnerException != null)
            error = error.InnerException;
        return error;
    }

    private static void WriteProgress(HarnessOptions options, string milestone, string status, string note)
    {
        if (options.RequestPath != null)
            return;
        string path = Path.Combine(options.WorkspaceDirectory, "progress.json");
        Directory.CreateDirectory(options.WorkspaceDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(new { milestone, status, note }, Json));
    }
}

internal sealed record StepRecord(string Name, string Status, string Detail, long Milliseconds);

internal sealed record HarnessOptions
{
    public const string Usage = """
        用法：OfflineSearchHarness [选项]
          --request <path>       无人测试请求 JSON（含 generatedScenarioPath），走生成场景开局流程
          --label <name>         本根标签（写进 result.json，默认 offline）
          --character <id>       角色（无 --request 时用，默认 IRONCLAD）
          --encounter <id>       遭遇（无 --request 时用，默认 FUZZY_WURM_CRAWLER_WEAK）
          --seed <string>        跑局种子（无 --request 时用，默认 OFFLINEHARNESS1）
          --ascension <int>      飞升层数（默认 0）
          --act-index <int>      测试幕索引（默认 0）
          --profile <p>          Low|Medium|High|VeryHigh|Custom（默认 Custom）
          --beam <int>           Beam 宽度（覆盖预设）
          --nodes <int>          最大展开节点（覆盖预设）
          --card-branches <int>  每节点卡牌分支上限（覆盖预设）
          --pile-branches <int>  每动作牌堆选择分支上限（覆盖预设）
          --hand-branches <int>  每动作手牌选择分支上限（覆盖预设）
          --dop <int>            搜索并行度（默认 1）
          --budget-ms <int>      搜索预算毫秒（默认 600000）
          --potion-policy <p>    药水政策（默认 Smart）
          --search-mode <m>      Evaluate（单次求解，不经协调器，默认）| Coordinator（生产协调器）
          --use-portfolio        开宽度组合（只对 --search-mode Coordinator 有效）
          --no-plain-baseline    消融：丢掉普通基线成员（需 --use-portfolio）
          --unordered-pile-mask <0..15>  实验：状态键里顺序无关的牌堆（1手牌/2抽牌堆/4弃牌堆/8消耗堆）
          --state-key-salt <int> 实验：给状态指纹异或一个常量（双射，只改数值不改相等关系）
          --measure-phases       开按阶段的耗时/分配统计（SEARCH_PHASE 行进运行日志）
          --disable-transposition-prune <0..3>  实验：关掉转置支配剪枝（1=候选准入/2=展开准入）
          --memory-no-progress-limit <int>  实验：连续多少次无进展回收后提前收手（0=关闭）
          --transposition-entry-limit <int>  实验：转置支配表合并条目上限（0=不设上限；缺省=生产默认 1000000）
          --enable-no-gc-region   开 Runtime 的搜索内 No-GC 生命周期（默认关闭）
          --no-gc-region-budget-gigabytes <double>  No-GC 区域预算，单位十进制 GB（默认 1）
          --signal-ballast-mb <int>  进 No-GC scope 后先持有 N MiB 活对象，制造回收腾不出余量的压力
          --observe-portfolio    导出追加搜索的特征与实际政策标签
          --portfolio-model <p>  加载可选选择器 JSON；不匹配的版本回退原组合
          --milestone <M1|M2>    跑到哪个里程碑（默认 M2）
          --out <dir>            产物目录（默认 <workspace>/offline）
          --workspace <dir>      工作区目录（默认 .local/offline-harness）
          --language <code>      本地化语言码（默认 eng）
          --verbose-game-log     把游戏 info/debug 日志也打到标准输出
        环境变量 OFFLINE_HARNESS_COMBATSOLVER_DLL 可以换掉运行时加载的 CombatSolver.dll。
        """;

    public HarnessScenario Scenario { get; init; } = new("IRONCLAD", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS1", 0, 0);
    public string? RequestPath { get; init; }
    public string Label { get; init; } = "offline";
    public string Profile { get; init; } = "Custom";
    public int? Beam { get; init; }
    public int? Nodes { get; init; }
    public int? MaxCardBranchesPerNode { get; init; }
    public int? MaxPileChoiceBranchesPerAction { get; init; }
    public int? MaxHandChoiceBranchesPerAction { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 1;
    public int BudgetMilliseconds { get; init; } = 600_000;
    public string PotionPolicy { get; init; } = "Smart";
    public string SearchMode { get; init; } = "Evaluate";
    /// <summary>开宽度组合（协调器的组合成员通道）；Evaluate 模式下没有意义。</summary>
    public bool UsePortfolio { get; init; }
    /// <summary>消融：丢掉只带基线宽度、不带排序修饰的组合成员，少跑一次真实搜索。</summary>
    public bool NoPlainBaselineMember { get; init; }
    /// <summary>实验：状态键里哪些牌堆改成顺序无关哈希（手牌=1/抽牌堆=2/弃牌堆=4/消耗堆=8）；0 即生产口径。</summary>
    public int UnorderedPileMask { get; init; }
    /// <summary>实验：给状态指纹异或一个由该值导出的常量；双射，只改数值不改相等关系。0 即生产口径。</summary>
    public int StateKeySalt { get; init; }
    /// <summary>开按阶段统计：每个阶段的耗时与分配字节，落到运行日志的 SEARCH_PHASE 行。</summary>
    public bool MeasureSearchPhases { get; init; }
    /// <summary>实验：关掉转置支配剪枝的位（1=候选准入/2=展开准入）；0 即生产口径。</summary>
    public int TranspositionPruningDisabledMask { get; init; }
    /// <summary>实验：连续多少次无进展回收后提前收手；0 即关闭（生产口径）。</summary>
    public int MemoryNoProgressRecoveryLimit { get; init; }
    /// <summary>实验：转置支配表合并条目上限；0 = 不设上限，缺省 = 生产默认。</summary>
    public int? TranspositionEntryLimit { get; init; }
    /// <summary>实验：走 Runtime 的搜索内 No-GC 生命周期，供无头宿主复现内存回收与截断。</summary>
    public bool EnableNoGcRegion { get; init; }
    /// <summary>No-GC 区域预算；只在 <see cref="EnableNoGcRegion" /> 开启时生效。</summary>
    public double NoGcRegionBudgetGigabytes { get; init; } = 1d;
    /// <summary>进入 No-GC scope 后先持有的活对象 MiB，用于制造“回收腾不出余量”的受控压力。</summary>
    public int SignalBallastMegabytes { get; init; }
    public bool ObservePortfolio { get; init; }
    public string? PortfolioModelPath { get; init; }
    public string Milestone { get; init; } = "M2";
    public string WorkspaceDirectory { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public bool VerboseGameLog { get; init; }
    public string Language { get; init; } = "eng";

    public string LogDirectory => Path.Combine(OutputDirectory, "logs");

    public static HarnessOptions Parse(string[] args)
    {
        string character = "IRONCLAD", encounter = "FUZZY_WURM_CRAWLER_WEAK", seed = "OFFLINEHARNESS1";
        int ascension = 0, actIndex = 0, dop = 1, budget = 600_000, unorderedPileMask = 0, stateKeySalt = 0;
        int transpositionPruneOff = 0, memoryNoProgressLimit = 0;
        int? transpositionEntryLimit = null;
        bool measurePhases = false, enableNoGcRegion = false;
        double noGcRegionBudgetGigabytes = 1d;
        int signalBallastMegabytes = 0;
        int? beam = null, nodes = null, cardBranches = null, pileBranches = null, handBranches = null;
        bool usePortfolio = false, observePortfolio = false, noPlainBaseline = false;
        string? portfolioModelPath = null;
        string potionPolicy = "Smart", milestone = "M2", language = "eng";
        string profile = "Custom", searchMode = "Evaluate", label = "offline";
        string? output = null, requestPath = null;
        string workspace = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../.local/offline-harness"));
        bool verbose = false;

        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            string Value()
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"选项 {key} 缺少取值。");
                return args[++index];
            }
            switch (key)
            {
                case "--request": requestPath = Path.GetFullPath(Value()); break;
                case "--label": label = Value(); break;
                case "--character": character = Value(); break;
                case "--encounter": encounter = Value(); break;
                case "--seed": seed = Value(); break;
                case "--ascension": ascension = int.Parse(Value()); break;
                case "--act-index": actIndex = int.Parse(Value()); break;
                case "--profile": profile = Value(); break;
                case "--beam": beam = int.Parse(Value()); break;
                case "--nodes": nodes = int.Parse(Value()); break;
                case "--card-branches": cardBranches = int.Parse(Value()); break;
                case "--pile-branches": pileBranches = int.Parse(Value()); break;
                case "--hand-branches": handBranches = int.Parse(Value()); break;
                case "--dop": dop = int.Parse(Value()); break;
                case "--budget-ms": budget = int.Parse(Value()); break;
                case "--potion-policy": potionPolicy = Value(); break;
                case "--search-mode": searchMode = Value(); break;
                case "--use-portfolio": usePortfolio = true; break;
                case "--no-plain-baseline": noPlainBaseline = true; break;
                case "--unordered-pile-mask": unorderedPileMask = int.Parse(Value()); break;
                case "--state-key-salt": stateKeySalt = int.Parse(Value()); break;
                case "--measure-phases": measurePhases = true; break;
                case "--disable-transposition-prune": transpositionPruneOff = int.Parse(Value()); break;
                case "--memory-no-progress-limit": memoryNoProgressLimit = int.Parse(Value()); break;
                case "--transposition-entry-limit": transpositionEntryLimit = int.Parse(Value()); break;
                case "--enable-no-gc-region": enableNoGcRegion = true; break;
                case "--no-gc-region-budget-gigabytes": noGcRegionBudgetGigabytes = double.Parse(Value()); break;
                case "--signal-ballast-mb": signalBallastMegabytes = int.Parse(Value()); break;
                case "--observe-portfolio": observePortfolio = true; break;
                case "--portfolio-model": portfolioModelPath = Path.GetFullPath(Value()); break;
                case "--milestone": milestone = Value(); break;
                case "--out": output = Value(); break;
                case "--workspace": workspace = Value(); break;
                case "--language": language = Value(); break;
                case "--verbose-game-log": verbose = true; break;
                default: throw new ArgumentException($"未知选项 {key}。");
            }
        }
        if (milestone is not ("M1" or "M2"))
            throw new ArgumentException("--milestone 只接受 M1 或 M2。");
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException("--profile 只接受 Low|Medium|High|VeryHigh|Custom。");
        if (searchMode is not ("Evaluate" or "Coordinator"))
            throw new ArgumentException("--search-mode 只接受 Evaluate 或 Coordinator。");
        if (usePortfolio && searchMode != "Coordinator")
            throw new ArgumentException("--use-portfolio 只对 --search-mode Coordinator 有效。");
        if ((observePortfolio || portfolioModelPath != null) && (!usePortfolio || searchMode != "Coordinator"))
            throw new ArgumentException("选择器实验需要 --search-mode Coordinator --use-portfolio。");
        if (noPlainBaseline && (!usePortfolio || searchMode != "Coordinator"))
            throw new ArgumentException("--no-plain-baseline 需要 --search-mode Coordinator --use-portfolio。");
        if (unorderedPileMask is < 0 or > 15)
            throw new ArgumentException("--unordered-pile-mask 只接受 0..15（手牌=1/抽牌堆=2/弃牌堆=4/消耗堆=8）。");
        if (transpositionPruneOff is < 0 or > 3)
            throw new ArgumentException("--disable-transposition-prune 只接受 0..3（1=候选准入/2=展开准入）。");
        if (memoryNoProgressLimit < 0)
            throw new ArgumentException("--memory-no-progress-limit 只接受非负数（0=关闭）。");
        if (transpositionEntryLimit is < 0)
            throw new ArgumentException("--transposition-entry-limit 只接受非负数（0=不设上限）。");
        if (noGcRegionBudgetGigabytes < 1d || noGcRegionBudgetGigabytes > 256d)
            throw new ArgumentException("--no-gc-region-budget-gigabytes 只接受 1..256。");
        if (signalBallastMegabytes is < 0 or > 4096)
            throw new ArgumentException("--signal-ballast-mb 只接受 0..4096。");
        if (signalBallastMegabytes > 0 && !enableNoGcRegion)
            throw new ArgumentException("--signal-ballast-mb 需要 --enable-no-gc-region。");
        if (profile == "Custom" && requestPath == null)
        {
            beam ??= 24;
            nodes ??= 2000;
        }

        return new HarnessOptions
        {
            Scenario = new HarnessScenario(character, encounter, seed, ascension, actIndex),
            RequestPath = requestPath,
            Label = label,
            Profile = profile,
            Beam = beam,
            Nodes = nodes,
            MaxCardBranchesPerNode = cardBranches,
            MaxPileChoiceBranchesPerAction = pileBranches,
            MaxHandChoiceBranchesPerAction = handBranches,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budget,
            PotionPolicy = potionPolicy,
            SearchMode = searchMode,
            UsePortfolio = usePortfolio,
            NoPlainBaselineMember = noPlainBaseline,
            UnorderedPileMask = unorderedPileMask,
            StateKeySalt = stateKeySalt,
            MeasureSearchPhases = measurePhases,
            TranspositionPruningDisabledMask = transpositionPruneOff,
            TranspositionEntryLimit = transpositionEntryLimit,
            MemoryNoProgressRecoveryLimit = memoryNoProgressLimit,
            EnableNoGcRegion = enableNoGcRegion,
            NoGcRegionBudgetGigabytes = noGcRegionBudgetGigabytes,
            SignalBallastMegabytes = signalBallastMegabytes,
            ObservePortfolio = observePortfolio,
            PortfolioModelPath = portfolioModelPath,
            Milestone = milestone,
            WorkspaceDirectory = Path.GetFullPath(workspace),
            OutputDirectory = Path.GetFullPath(output ?? Path.Combine(workspace, "offline")),
            VerboseGameLog = verbose,
            Language = language,
        };
    }
}
