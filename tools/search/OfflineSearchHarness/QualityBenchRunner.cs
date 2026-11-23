using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CombatSolver;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;

namespace OfflineSearchHarness;

/// <summary>
/// Unbiased Tier 1 Existing Real Roots Batch Runner (<c>--bench-corpora</c>):
/// - <c>dev</c>: <c>dev-00</c>..<c>dev-11</c> under <c>coverage/corpora/novelty/</c> + <c>GA-IRONCLAD-ELITE-00</c>
/// - <c>holdout</c>: <c>holdout-00</c>..<c>holdout-04</c> under <c>coverage/corpora/novelty/</c> (strict holdout — NEVER tune against!)
/// Outputs uniform <see cref="BenchmarkOutcomeFile"/> JSON consumable by <c>--compare-outcomes</c>.
/// Zero synthetic or hand-crafted decks.
/// </summary>
internal static class QualityBenchRunner
{
    private sealed record CorporaFightEntry(
        string FightId,
        string Tier,
        string Group,
        UnattendedTestRequest Request);

    public static int RunCorpora(string[] args)
    {
        string slice = "all";
        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        int dop = 8;
        int budgetMs = 15_000;
        string searchMode = "Coordinator";
        string potionPolicy = "Smart";
        bool stopAtZeroLoss = false;
        string? filter = null;
        string repoRoot = ResolveRepoRoot();
        string? dumpOutcomesPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--bench-corpora 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--tier":
                case "--tiers":
                case "--slice":
                    slice = NextVal().ToLowerInvariant();
                    break;
                case "--profile":
                    profile = NextVal();
                    break;
                case "--beam":
                    beamOverride = int.Parse(NextVal(), CultureInfo.InvariantCulture);
                    break;
                case "--nodes":
                    nodesOverride = int.Parse(NextVal(), CultureInfo.InvariantCulture);
                    break;
                case "--dop":
                    dop = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--budget-ms":
                    budgetMs = Math.Max(500, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--search-mode":
                    searchMode = NextVal();
                    break;
                case "--potion-policy":
                    potionPolicy = NextVal();
                    break;
                case "--stop-at-zero-loss":
                    stopAtZeroLoss = true;
                    break;
                case "--filter":
                    filter = NextVal();
                    break;
                case "--dump-outcomes":
                case "--out":
                    dumpOutcomesPath = Path.GetFullPath(NextVal());
                    break;
                default:
                    throw new ArgumentException($"--bench-corpora 未知选项：{key}");
            }
        }

        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException("--profile 只支持 Low|Medium|High|VeryHigh|Custom。");
        if (searchMode is not ("Coordinator" or "Evaluate"))
            throw new ArgumentException("--search-mode 只支持 Coordinator|Evaluate。");
        SolverPotionPolicy parsedPotionPolicy = Enum.Parse<SolverPotionPolicy>(potionPolicy, ignoreCase: true);

        bool includeDev = slice is "all" or "dev" or "a" or "ab";
        bool includeHoldout = slice is "all" or "holdout" or "b" or "ab";
        if (!includeDev && !includeHoldout)
            throw new ArgumentException($"--tier 只支持 all|dev|holdout（或兼容别名 A|B|AB），收到：{slice}");

        string suiteName = $"corpora-{slice}";
        dumpOutcomesPath ??= Path.Combine(
            repoRoot, ".local", "offline-harness", $"{suiteName}-{profile.ToLowerInvariant()}-dop{dop}.json");

        List<CorporaFightEntry> entries = [];
        if (includeDev)
            entries.AddRange(LoadDevRoots(repoRoot));
        if (includeHoldout)
            entries.AddRange(LoadHoldoutRoots(repoRoot));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            entries = entries
                .Where(e => e.FightId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || e.Group.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("筛选后没有可执行的语料战斗。");
            return 2;
        }

        string evidenceRoot = Path.Combine(repoRoot, ".local", "offline-harness", "bench-evidence");
        Directory.CreateDirectory(evidenceRoot);

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine(
            $"=== --bench-corpora (slice={slice}, fights={entries.Count}, profile={profile}, " +
            $"mode={searchMode}, potion={parsedPotionPolicy}, dop={dop}, budgetMs={budgetMs}) ===");

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
            PotionPolicy = parsedPotionPolicy.ToString(),
            SearchMode = searchMode,
            StopAtZeroLoss = stopAtZeroLoss,
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", "bench-logs"),
        };
        GameBootstrap.EnsureBootstrapped(loop, modOptions);
        SolverSearchProfile resolvedProfile = ModRuntime.ResolveProfile(modOptions) with
        {
            SoftTimeBudgetMilliseconds = budgetMs,
        };

        int gen2Before = GC.CollectionCount(2);
        using MemorySampler memory = new(TimeSpan.FromMilliseconds(150));

        List<FightOutcomeRecord> outcomes = new(entries.Count);
        try
        {
            foreach (CorporaFightEntry entry in entries)
            {
                FightOutcomeRecord record = RunSingleFight(
                    loop, entry, evidenceRoot, resolvedProfile, searchMode, parsedPotionPolicy, dop, budgetMs);
                outcomes.Add(record);

                string statusTag = record.HardErrors > 0
                    ? $"ERR({record.HardError})"
                    : (record.Won ? "WIN" : "LOSS");
                Console.WriteLine(
                    $"[{record.Tier,-7}] {record.FightId,-28} | {statusTag,-5} | hp={record.HpLoss,3} | pot={record.PotionsUsed} | " +
                    $"V={record.EconomicValueV,6:F2} | T={record.Turns,2} | {record.ElapsedMs,7:F1}ms | {record.Boundary,-10} | nodes={record.ExpandedNodes}");
            }
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        memory.Dispose();
        totalSw.Stop();
        int gcGen2 = GC.CollectionCount(2) - gen2Before;
        long peakRssBytes = Math.Max(memory.PeakWorkingSetBytes, memory.PeakProcessWorkingSetBytes);
        double peakRssMb = Math.Round(peakRssBytes / (1024.0 * 1024.0), 2);

        BenchmarkSummaryRecord summary = BenchmarkOutcomeHelper.BuildSummary(
            outcomes, totalSw.Elapsed.TotalMilliseconds, peakRssMb, gcGen2);

        BenchmarkOutcomeFile file = new()
        {
            Suite = suiteName,
            Profile = profile,
            SearchMode = searchMode,
            PotionPolicy = parsedPotionPolicy.ToString(),
            Dop = dop,
            Seed = null,
            BudgetMs = budgetMs,
            Summary = summary,
            Outcomes = outcomes.ToArray(),
        };
        BenchmarkOutcomeHelper.WriteOutcomeFile(dumpOutcomesPath, file);

        Console.WriteLine();
        PrintMarkdownTables(outcomes, summary, dumpOutcomesPath);

        return summary.HardErrors == 0 ? 0 : 1;
    }

    private static FightOutcomeRecord RunSingleFight(
        MainLoopContext loop,
        CorporaFightEntry entry,
        string evidenceRoot,
        SolverSearchProfile resolvedProfile,
        string searchMode,
        SolverPotionPolicy potionPolicy,
        int dop,
        int budgetMs)
    {
        IDisposable? choiceScope = null;
        string characterId = entry.Request.CharacterId;
        string encounterId = entry.Request.EncounterId;

        try
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();

            string evidenceDir = Path.Combine(evidenceRoot, entry.FightId);
            Directory.CreateDirectory(evidenceDir);
            GeneratedScenarioSetup setup = GeneratedScenarioSetup.Prepare(entry.Request, evidenceDir);
            characterId = setup.Request.CharacterId;
            encounterId = setup.Request.EncounterId;

            Task<IDisposable?> enter = setup.EnterCombatRoomAsync();
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), $"Bench Enter {entry.FightId}");
            choiceScope = enter.GetAwaiter().GetResult();

            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            setup.CaptureOpening(combat);
            if (!setup.Request.PreserveNativeCombatStateForTest)
            {
                loop.RunUntilCompleted(
                    setup.Session.InjectInitialStateAsync(combat, LocalContext.GetMe(combat)!),
                    TimeSpan.FromSeconds(30), $"Bench Inject {entry.FightId}");
            }

            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                settings, combat, includeTurnSetup: false,
                theftPolicy: setup.Request.TheftPolicyForTest ?? SolverController.ResolveTheftPolicy(combat)) with
            {
                Profile = resolvedProfile,
                FixedBudget = true,
                BudgetOverrideMilliseconds = budgetMs,
                MaxDegreeOfParallelism = dop,
                PotionPolicy = potionPolicy,
            };

            Stopwatch sw = Stopwatch.StartNew();
            SolverResult solved = searchMode == "Coordinator"
                ? HotSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null)
                : HotSearchCoordinator.SolveSingleBeam(root, names, damage, policy, CancellationToken.None);
            sw.Stop();

            return BenchmarkOutcomeHelper.FromSolverResult(
                entry.FightId, entry.Tier, entry.Group, characterId, encounterId,
                root, policy, solved, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Exception rootEx = Unwrap(ex);
            return new FightOutcomeRecord
            {
                FightId = entry.FightId,
                Tier = entry.Tier,
                Group = entry.Group,
                CharacterId = characterId,
                EncounterId = encounterId,
                Won = false,
                Survives = false,
                Boundary = "Error",
                HardErrors = 1,
                HardError = $"{rootEx.GetType().Name}: {rootEx.Message}",
            };
        }
        finally
        {
            choiceScope?.Dispose();
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    private static List<CorporaFightEntry> LoadDevRoots(string repoRoot)
    {
        List<CorporaFightEntry> list = [];
        string noveltyDir = Path.Combine(repoRoot, "coverage", "corpora", "novelty");
        foreach (string path in Directory.GetFiles(noveltyDir, "dev-*.json")
            .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            list.Add(new CorporaFightEntry(id, "dev", "novelty_dev", WrapGeneratedSpec(id, path)));
        }

        string gaReqPath = Path.Combine(repoRoot, "coverage", "corpora", "strategy", "roots", "GA-IRONCLAD-ELITE-00-request.json");
        if (File.Exists(gaReqPath))
        {
            UnattendedTestRequest req = ReadAndResolveRequest(gaReqPath, repoRoot);
            list.Add(new CorporaFightEntry(req.ScenarioId, "dev", "strategy_root", req));
        }
        return list;
    }

    private static List<CorporaFightEntry> LoadHoldoutRoots(string repoRoot)
    {
        List<CorporaFightEntry> list = [];
        string noveltyDir = Path.Combine(repoRoot, "coverage", "corpora", "novelty");
        foreach (string path in Directory.GetFiles(noveltyDir, "holdout-*.json")
            .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            list.Add(new CorporaFightEntry(id, "holdout", "novelty_holdout", WrapGeneratedSpec(id, path)));
        }
        return list;
    }

    private static UnattendedTestRequest WrapGeneratedSpec(string id, string absSpecPath)
        => JsonSerializer.Deserialize<UnattendedTestRequest>(
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                scenarioId = id,
                cards = Array.Empty<object>(),
                generatedScenarioPath = absSpecPath,
            }),
            UnattendedTestFiles.JsonOptions)!;

    private static UnattendedTestRequest ReadAndResolveRequest(string reqPath, string repoRoot)
    {
        UnattendedTestRequest req = GeneratedScenarioSetup.ReadRequest(reqPath);
        if (req.GeneratedScenarioPath != null && !Path.IsPathRooted(req.GeneratedScenarioPath))
        {
            string absSpec = Path.GetFullPath(Path.Combine(repoRoot, req.GeneratedScenarioPath));
            var json = JsonSerializer.SerializeToNode(req, UnattendedTestFiles.JsonOptions)!.AsObject();
            json["generatedScenarioPath"] = absSpec;
            req = json.Deserialize<UnattendedTestRequest>(UnattendedTestFiles.JsonOptions)!;
        }
        return req;
    }

    private static void PrintMarkdownTables(
        IReadOnlyList<FightOutcomeRecord> outcomes,
        BenchmarkSummaryRecord overall,
        string dumpOutcomesPath)
    {
        Console.WriteLine("### Per-Fight Results");
        Console.WriteLine("| FightId | Tier | Char | Won | HP Loss | Pot | Pot Cost | V | Turns | ms | Boundary | Nodes |");
        Console.WriteLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|");
        foreach (FightOutcomeRecord o in outcomes)
        {
            string wonStr = o.HardErrors > 0 ? "ERR" : (o.Won ? "W" : "L");
            Console.WriteLine(
                $"| {o.FightId} | {o.Tier} | {o.CharacterId} | {wonStr} | " +
                $"{o.HpLoss} | {o.PotionsUsed} | {o.PotionCostHp} | {o.EconomicValueV:F2} | {o.Turns} | " +
                $"{o.ElapsedMs:F1} | {o.Boundary} | {o.ExpandedNodes} |");
        }

        Console.WriteLine();
        Console.WriteLine("### Slice Summary");
        Console.WriteLine("| Slice | Wins/N | Win Rate | Avg HP Loss (Wins) | Avg Pot | Avg V | p50 ms | p95 ms | Errors |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (IGrouping<string, FightOutcomeRecord> grp in outcomes
            .GroupBy(static o => o.Tier, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            BenchmarkSummaryRecord s = BenchmarkOutcomeHelper.BuildSummary(grp.ToList(), 0, overall.PeakRssMb, 0);
            Console.WriteLine(
                $"| {grp.Key} | {s.Wins}/{s.TotalFights} | {s.WinRate:P1} | " +
                $"{s.AvgHpLoss:F2} ({s.AvgHpLossWins:F2}) | {s.AvgPotionsUsed:F2} | " +
                $"{s.AvgEconomicValueV:F2} | {s.P50Ms:F1} | {s.P95Ms:F1} | {s.HardErrors} |");
        }
        Console.WriteLine(
            $"| **ALL** | **{overall.Wins}/{overall.TotalFights}** | **{overall.WinRate:P1}** | " +
            $"**{overall.AvgHpLoss:F2} ({overall.AvgHpLossWins:F2})** | **{overall.AvgPotionsUsed:F2}** | " +
            $"**{overall.AvgEconomicValueV:F2}** | **{overall.P50Ms:F1}** | **{overall.P95Ms:F1}** | **{overall.HardErrors}** |");

        Console.WriteLine();
        Console.WriteLine($"peak_rss_mb={overall.PeakRssMb:F1} gc_gen2={overall.GcGen2} total_elapsed_s={overall.TotalElapsedMs / 1000.0:F2}s");
        Console.WriteLine($"outcomes_json={dumpOutcomesPath}");
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
