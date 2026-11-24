using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;

namespace OfflineSearchHarness;

internal sealed record FightOutcomeRecord
{
    [JsonPropertyName("fight_id")]
    public required string FightId { get; init; }

    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("character_id")]
    public required string CharacterId { get; init; }

    [JsonPropertyName("encounter_id")]
    public required string EncounterId { get; init; }

    [JsonPropertyName("won")]
    public bool Won { get; init; }

    [JsonPropertyName("survives")]
    public bool Survives { get; init; }

    [JsonPropertyName("death_save_use_count")]
    public int DeathSaveUseCount { get; init; }

    [JsonPropertyName("outstanding_stolen")]
    public int OutstandingStolen { get; init; }

    [JsonPropertyName("forfeits_gold")]
    public bool ForfeitsGold { get; init; }

    [JsonPropertyName("start_hp")]
    public int StartHp { get; init; }

    [JsonPropertyName("end_hp")]
    public int EndHp { get; init; }

    [JsonPropertyName("start_max_hp")]
    public int StartMaxHp { get; init; }

    [JsonPropertyName("end_max_hp")]
    public int EndMaxHp { get; init; }

    [JsonPropertyName("hp_loss")]
    public int HpLoss { get; init; }

    [JsonPropertyName("gross_hp_loss")]
    public int? GrossHpLoss { get; init; }

    [JsonPropertyName("strategic_hp_deficit")]
    public int StrategicHpDeficit { get; init; }

    [JsonPropertyName("potions_used")]
    public int PotionsUsed { get; init; }

    [JsonPropertyName("potion_cost_hp")]
    public int PotionCostHp { get; init; }

    [JsonPropertyName("net_persistent_hp_delta_centi")]
    public int NetPersistentHpDeltaCenti { get; init; }

    [JsonPropertyName("net_potion_opportunity_cost_centi")]
    public int NetPotionOpportunityCostCenti { get; init; }

    [JsonPropertyName("economic_value_centi")]
    public long EconomicValueCenti { get; init; }

    [JsonPropertyName("economic_value_v")]
    public double EconomicValueV => Math.Round(EconomicValueCenti / (double)HotSearchConstants.HpFixedScale, 2);

    [JsonPropertyName("growth_hp_credit")]
    public int GrowthHpCredit { get; init; }

    [JsonPropertyName("growth_reward_count")]
    public int GrowthRewardCount { get; init; }

    [JsonPropertyName("turns")]
    public int Turns { get; init; }

    [JsonPropertyName("enemy_hp_remaining")]
    public int EnemyHpRemaining { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; init; }

    [JsonPropertyName("solve_count")]
    public int SolveCount { get; init; } = 1;

    [JsonPropertyName("boundary")]
    public string Boundary { get; init; } = "None";

    [JsonPropertyName("expanded_nodes")]
    public long ExpandedNodes { get; init; }

    [JsonPropertyName("transitions")]
    public long Transitions { get; init; }

    [JsonPropertyName("score")]
    public long Score { get; init; }

    [JsonPropertyName("unplanned_replans")]
    public int UnplannedReplans { get; init; }

    [JsonPropertyName("choice_mismatches")]
    public int ChoiceMismatches { get; init; }

    [JsonPropertyName("hard_errors")]
    public int HardErrors { get; init; }

    [JsonPropertyName("theft_policy")]
    public string? TheftPolicy { get; init; }

    [JsonPropertyName("hard_error")]
    public string? HardError { get; init; }

    [JsonPropertyName("route_signature")]
    public string? RouteSignature { get; init; }
}

internal sealed record BenchmarkSummaryRecord
{
    [JsonPropertyName("total_fights")]
    public int TotalFights { get; init; }

    [JsonPropertyName("wins")]
    public int Wins { get; init; }

    [JsonPropertyName("defeats")]
    public int Defeats { get; init; }

    [JsonPropertyName("hard_errors")]
    public int HardErrors { get; init; }

    [JsonPropertyName("win_rate")]
    public double WinRate { get; init; }

    [JsonPropertyName("avg_hp_loss")]
    public double AvgHpLoss { get; init; }

    [JsonPropertyName("avg_hp_loss_wins")]
    public double AvgHpLossWins { get; init; }

    [JsonPropertyName("avg_potions_used")]
    public double AvgPotionsUsed { get; init; }

    [JsonPropertyName("avg_economic_value_v")]
    public double AvgEconomicValueV { get; init; }

    [JsonPropertyName("total_unplanned_replans")]
    public int TotalUnplannedReplans { get; init; }

    [JsonPropertyName("total_choice_mismatches")]
    public int TotalChoiceMismatches { get; init; }

    [JsonPropertyName("p50_ms")]
    public double P50Ms { get; init; }

    [JsonPropertyName("p95_ms")]
    public double P95Ms { get; init; }

    [JsonPropertyName("total_elapsed_ms")]
    public double TotalElapsedMs { get; init; }

    [JsonPropertyName("peak_rss_mb")]
    public double PeakRssMb { get; init; }

    [JsonPropertyName("gc_gen2")]
    public int GcGen2 { get; init; }
}

internal sealed record BenchmarkOutcomeFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("suite")]
    public required string Suite { get; init; }

    [JsonPropertyName("profile")]
    public required string Profile { get; init; }

    [JsonPropertyName("search_mode")]
    public required string SearchMode { get; init; }

    [JsonPropertyName("potion_policy")]
    public required string PotionPolicy { get; init; }

    [JsonPropertyName("dop")]
    public int Dop { get; init; }

    [JsonPropertyName("seed")]
    public int? Seed { get; init; }

    [JsonPropertyName("budget_ms")]
    public int BudgetMs { get; init; }

    [JsonPropertyName("summary")]
    public required BenchmarkSummaryRecord Summary { get; init; }

    [JsonPropertyName("outcomes")]
    public required FightOutcomeRecord[] Outcomes { get; init; }
}

internal static class BenchmarkOutcomeHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static FightOutcomeRecord FromSolverResult(
        string fightId,
        string tier,
        string? group,
        string characterId,
        string encounterId,
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result,
        double elapsedMs)
    {
        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootState = rootBundle.RootState;
        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootState, rootBundle.Rules, policy.PotionStrategy.Thresholds);
        BattleDamageSnapshot battleDamage = new(
            HpLostSoFar: result.BattleHpLostSoFar,
            SoldHpCommitted: result.SoldHp,
            PotionsUsedSoFar: result.BattlePotionsUsedSoFar,
            PotionIdsUsedSoFar: result.BattlePotionIdsUsedSoFar,
            HpRecoveredOrGainedSoFar: result.BattleHpRecoveredOrGainedSoFar);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rootBundle.Rules, in rootState, rootPotionCosts);

        SolverInterimResult quality = HotSearchCoordinator.CaptureQuality(root, policy, result);
        SolverSnapshot snap = result.Snapshot;

        int postCombatHeal = HotRouteQuality.RankedPostCombatRelicHeal(
            hotPolicy.PostCombatRelicHeal,
            quality.Won,
            snap.PlayerHp,
            snap.PlayerMaxHp);
        int computedNetHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: snap.PlayerHp,
            postCombatMonotoneHeal: postCombatHeal,
            initialPlayerHp: hotPolicy.InitialPlayerHp,
            endPlayerMaxHp: snap.PlayerMaxHp,
            initialPlayerMaxHp: hotPolicy.InitialPlayerMaxHp,
            deathSaveHpRestored: snap.DeathSaveHpRestored,
            growthHpCredit: quality.GrowthHpCredit,
            bossHpRelief: hotPolicy.StrategicBossHpRelief,
            ignoreLongTermRewards: hotPolicy.IgnoreLongTermRewards);

        int netHpDeltaCenti = HotRouteQuality.RoundCentiToHp(-computedNetHpDeltaCenti) == quality.StrategicHpDeficit
            ? computedNetHpDeltaCenti
            : -quality.StrategicHpDeficit * HotSearchConstants.HpFixedScale;

        int netPotionCostCenti = hotPolicy.EncounterBossHpRelief == BossHpRelief.RunEnding && result.PotionCount > 0
            ? result.PotionCount * HotSearchConstants.RunEndingPotionTieBreakCenti
            : quality.PotionStrategicCost * HotSearchConstants.HpFixedScale;

        long economicValueCenti = (long)netHpDeltaCenti - netPotionCostCenti;
        int turns = quality.Won
            ? (quality.CombatEndedTurn ?? result.CombatEndedTurn ?? snap.Turn)
            : Math.Max(result.SearchedTurns, snap.Turn);

        long expandedNodes = result.TotalExpandedNodes > 0 ? result.TotalExpandedNodes : result.ExpandedNodes;
        long transitions = result.TotalTransitionCount > 0 ? result.TotalTransitionCount : result.TransitionCount;
        long score = double.IsFinite(quality.Score)
            ? (long)Math.Clamp(quality.Score, -1_000_000_000_000_000d, 1_000_000_000_000_000d)
            : 0L;

        int deathSaveHpRestored = Math.Max(0, snap.DeathSaveHpRestored);

        return new FightOutcomeRecord
        {
            FightId = fightId,
            Tier = tier,
            Group = group,
            CharacterId = characterId,
            EncounterId = encounterId,
            Won = quality.Won,
            Survives = quality.Survives,
            DeathSaveUseCount = quality.DeathSaveUseCount,
            OutstandingStolen = quality.OutstandingStolenResource,
            ForfeitsGold = quality.ForfeitsEncounterGold,
            StartHp = hotPolicy.InitialPlayerHp,
            EndHp = snap.PlayerHp,
            StartMaxHp = hotPolicy.InitialPlayerMaxHp,
            EndMaxHp = snap.PlayerMaxHp,
            HpLoss = Math.Max(0, hotPolicy.InitialPlayerHp - (snap.PlayerHp - deathSaveHpRestored)),
            GrossHpLoss = quality.ProjectedBattleHpLost,
            StrategicHpDeficit = quality.StrategicHpDeficit,
            PotionsUsed = quality.ProjectedBattlePotionCount,
            PotionCostHp = quality.PotionStrategicCost,
            NetPersistentHpDeltaCenti = netHpDeltaCenti,
            NetPotionOpportunityCostCenti = netPotionCostCenti,
            EconomicValueCenti = economicValueCenti,
            GrowthHpCredit = quality.GrowthHpCredit,
            GrowthRewardCount = quality.GrowthRewardCount,
            Turns = turns,
            EnemyHpRemaining = quality.EnemyHp,
            ElapsedMs = Math.Round(elapsedMs, 2),
            SolveCount = 1,
            Boundary = result.BoundaryReason.ToString(),
            ExpandedNodes = expandedNodes,
            Transitions = transitions,
            Score = score,
            UnplannedReplans = 0,
            ChoiceMismatches = 0,
            HardErrors = 0,
            TheftPolicy = quality.TheftPolicy?.ToString(),
            HardError = null,
            RouteSignature = BuildRouteSignature(result),
        };
    }

    public static string BuildRouteSignature(SolverResult result)
        => string.Join(";", result.BestNode.Actions.Select(static a =>
            $"{a.Turn}:{a.Kind}:{a.CardId}:{a.PotionId}:{a.TargetCombatId}:{a.CardOccurrence}:{a.Choice?.Cards.Count ?? 0}"));

    public static BenchmarkSummaryRecord BuildSummary(
        IReadOnlyList<FightOutcomeRecord> outcomes,
        double totalElapsedMs,
        double peakRssMb,
        int gcGen2,
        IReadOnlyList<double>? solveTimesMs = null)
    {
        int total = outcomes.Count;
        int hardErrors = outcomes.Sum(static o => o.HardErrors > 0 ? o.HardErrors : (!string.IsNullOrEmpty(o.HardError) ? 1 : 0));
        List<FightOutcomeRecord> valid = outcomes.Where(static o => o.HardErrors == 0 && string.IsNullOrEmpty(o.HardError)).ToList();
        int wins = valid.Count(static o => o.Won);
        int defeats = valid.Count - wins;
        List<FightOutcomeRecord> wonFights = valid.Where(static o => o.Won).ToList();

        double winRate = total > 0 ? (double)wins / total : 0.0;
        double avgHpLoss = valid.Count > 0 ? valid.Average(static o => (double)o.HpLoss) : 0.0;
        double avgHpLossWins = wonFights.Count > 0 ? wonFights.Average(static o => (double)o.HpLoss) : 0.0;
        double avgPotions = valid.Count > 0 ? valid.Average(static o => (double)o.PotionsUsed) : 0.0;
        double avgEconomicValueV = valid.Count > 0
            ? valid.Average(static o => o.EconomicValueCenti / (double)HotSearchConstants.HpFixedScale)
            : 0.0;
        int totalUnplannedReplans = outcomes.Sum(static o => o.UnplannedReplans);
        int totalChoiceMismatches = outcomes.Sum(static o => o.ChoiceMismatches);

        IReadOnlyList<double> timings = solveTimesMs is { Count: > 0 }
            ? solveTimesMs
            : valid.Select(static o => o.ElapsedMs).ToList();

        return new BenchmarkSummaryRecord
        {
            TotalFights = total,
            Wins = wins,
            Defeats = defeats,
            HardErrors = hardErrors,
            WinRate = Math.Round(winRate, 4),
            AvgHpLoss = Math.Round(avgHpLoss, 2),
            AvgHpLossWins = Math.Round(avgHpLossWins, 2),
            AvgPotionsUsed = Math.Round(avgPotions, 2),
            AvgEconomicValueV = Math.Round(avgEconomicValueV, 2),
            TotalUnplannedReplans = totalUnplannedReplans,
            TotalChoiceMismatches = totalChoiceMismatches,
            P50Ms = Math.Round(Percentile(timings, 0.50), 1),
            P95Ms = Math.Round(Percentile(timings, 0.95), 1),
            TotalElapsedMs = Math.Round(totalElapsedMs, 1),
            PeakRssMb = Math.Round(peakRssMb, 2),
            GcGen2 = gcGen2,
        };
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
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

    public static HotRouteKey ToMaterialRouteKey(FightOutcomeRecord r) => new(
        Won: r.Won,
        Survives: r.Survives,
        DeathSaveUseCount: (byte)Math.Clamp(r.DeathSaveUseCount, 0, byte.MaxValue),
        OutstandingStolen: r.OutstandingStolen,
        ForfeitsGold: r.ForfeitsGold,
        StrategicHpDeficit: r.StrategicHpDeficit,
        OptionalPotionStrategicCost: r.PotionCostHp,
        RawPotionStrategicCost: r.PotionCostHp,
        GrowthHpCredit: r.GrowthHpCredit,
        GrowthRewardCount: r.GrowthRewardCount,
        CombatEndedTurn: r.Won ? r.Turns : int.MaxValue,
        ProjectedBattleHpLost: r.GrossHpLoss ?? r.HpLoss,
        ProjectedBattlePotionCount: r.PotionsUsed,
        ExplicitPotionCount: r.PotionsUsed,
        PlayerHp: r.EndHp,
        EnemyHp: r.EnemyHpRemaining,
        Score: 0L,
        PotionMask: 0,
        AmbergrisCount: 0)
    {
        HasCentiPrecision = true,
        NetPersistentHpDeltaCenti = r.NetPersistentHpDeltaCenti,
        NetPotionOpportunityCostCenti = r.NetPotionOpportunityCostCenti,
        EndPlayerMaxHp = r.EndMaxHp,
        RouteTurn = (byte)Math.Clamp(r.Turns, 0, byte.MaxValue),
    };

    /// <summary>
    /// Material win-first + unified economic value <c>V</c> comparison returning negative when
    /// <paramref name="candidate"/> is strictly better than <paramref name="baseline"/>, positive when worse,
    /// and 0 when materially equal (ignoring heuristic Score scale differences).
    /// </summary>
    public static int CompareMaterial(FightOutcomeRecord candidate, FightOutcomeRecord baseline)
    {
        bool candErr = candidate.HardErrors > 0 || !string.IsNullOrEmpty(candidate.HardError);
        bool baseErr = baseline.HardErrors > 0 || !string.IsNullOrEmpty(baseline.HardError);
        if (candErr != baseErr)
            return candErr ? 1 : -1;
        if (candErr && baseErr)
            return 0;

        HotRouteKey candKey = ToMaterialRouteKey(candidate);
        HotRouteKey baseKey = ToMaterialRouteKey(baseline);

        bool preserveResources = string.Equals(candidate.TheftPolicy, nameof(SolverTheftPolicy.PreserveResources), StringComparison.Ordinal)
            || string.Equals(baseline.TheftPolicy, nameof(SolverTheftPolicy.PreserveResources), StringComparison.Ordinal);

        if (preserveResources)
        {
            int cmp = baseKey.Won.CompareTo(candKey.Won);
            if (cmp != 0) return cmp;
            cmp = baseKey.Survives.CompareTo(candKey.Survives);
            if (cmp != 0) return cmp;
            cmp = candKey.DeathSaveUseCount.CompareTo(baseKey.DeathSaveUseCount);
            if (cmp != 0) return cmp;
            cmp = candKey.OutstandingStolen.CompareTo(baseKey.OutstandingStolen);
            if (cmp != 0) return cmp;
            cmp = candKey.ForfeitsGold.CompareTo(baseKey.ForfeitsGold);
            if (cmp != 0) return cmp;
        }

        HotSearchPolicy defaultPolicy = default;
        return HotRouteQuality.Compare(in candKey, in baseKey, in defaultPolicy);
    }

    public static void WriteOutcomeFile(string path, BenchmarkOutcomeFile file)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    public static BenchmarkOutcomeFile ReadOutcomeFile(string path)
        => JsonSerializer.Deserialize<BenchmarkOutcomeFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"无法解析评测结果文件：{path}");
}

internal static class OutcomeComparator
{
    private sealed record PairwiseFightDiff
    {
        [JsonPropertyName("fight_id")]
        public required string FightId { get; init; }

        [JsonPropertyName("tier")]
        public required string Tier { get; init; }

        [JsonPropertyName("group")]
        public string? Group { get; init; }

        [JsonPropertyName("verdict")]
        public required string Verdict { get; init; }

        [JsonPropertyName("base_won")]
        public bool BaseWon { get; init; }

        [JsonPropertyName("cand_won")]
        public bool CandWon { get; init; }

        [JsonPropertyName("base_hp_loss")]
        public int BaseHpLoss { get; init; }

        [JsonPropertyName("cand_hp_loss")]
        public int CandHpLoss { get; init; }

        [JsonPropertyName("hp_loss_delta")]
        public int HpLossDelta { get; init; }

        [JsonPropertyName("base_potions")]
        public int BasePotions { get; init; }

        [JsonPropertyName("cand_potions")]
        public int CandPotions { get; init; }

        [JsonPropertyName("potion_delta")]
        public int PotionDelta { get; init; }

        [JsonPropertyName("base_v")]
        public double BaseV { get; init; }

        [JsonPropertyName("cand_v")]
        public double CandV { get; init; }

        [JsonPropertyName("v_delta")]
        public double VDelta { get; init; }

        [JsonPropertyName("base_turns")]
        public int BaseTurns { get; init; }

        [JsonPropertyName("cand_turns")]
        public int CandTurns { get; init; }

        [JsonPropertyName("base_ms")]
        public double BaseMs { get; init; }

        [JsonPropertyName("cand_ms")]
        public double CandMs { get; init; }

        [JsonPropertyName("base_unplanned_replans")]
        public int BaseUnplannedReplans { get; init; }

        [JsonPropertyName("cand_unplanned_replans")]
        public int CandUnplannedReplans { get; init; }

        [JsonPropertyName("base_choice_mismatches")]
        public int BaseChoiceMismatches { get; init; }

        [JsonPropertyName("cand_choice_mismatches")]
        public int CandChoiceMismatches { get; init; }

        [JsonPropertyName("base_hard_errors")]
        public int BaseHardErrors { get; init; }

        [JsonPropertyName("cand_hard_errors")]
        public int CandHardErrors { get; init; }

        [JsonPropertyName("exact_route_match")]
        public bool ExactRouteMatch { get; init; }
    }

    private sealed record SliceAggregate
    {
        [JsonPropertyName("slice")]
        public required string Slice { get; init; }

        [JsonPropertyName("fights")]
        public int Fights { get; init; }

        [JsonPropertyName("better")]
        public int Better { get; init; }

        [JsonPropertyName("equal")]
        public int Equal { get; init; }

        [JsonPropertyName("worse")]
        public int Worse { get; init; }

        [JsonPropertyName("better_to_worse_ratio")]
        public double? BetterToWorseRatio { get; init; }

        [JsonPropertyName("base_wins")]
        public int BaseWins { get; init; }

        [JsonPropertyName("cand_wins")]
        public int CandWins { get; init; }

        [JsonPropertyName("win_delta")]
        public int WinDelta { get; init; }

        [JsonPropertyName("base_win_rate")]
        public double BaseWinRate { get; init; }

        [JsonPropertyName("cand_win_rate")]
        public double CandWinRate { get; init; }

        [JsonPropertyName("base_avg_v")]
        public double BaseAvgV { get; init; }

        [JsonPropertyName("cand_avg_v")]
        public double CandAvgV { get; init; }

        [JsonPropertyName("v_delta")]
        public double VDelta { get; init; }

        [JsonPropertyName("base_avg_hp_loss")]
        public double BaseAvgHpLoss { get; init; }

        [JsonPropertyName("cand_avg_hp_loss")]
        public double CandAvgHpLoss { get; init; }

        [JsonPropertyName("hp_loss_delta")]
        public double HpLossDelta { get; init; }

        [JsonPropertyName("shared_wins")]
        public int SharedWins { get; init; }

        [JsonPropertyName("shared_wins_hp_loss_delta")]
        public double SharedWinsHpLossDelta { get; init; }

        [JsonPropertyName("base_avg_potions")]
        public double BaseAvgPotions { get; init; }

        [JsonPropertyName("cand_avg_potions")]
        public double CandAvgPotions { get; init; }

        [JsonPropertyName("potion_delta")]
        public double PotionDelta { get; init; }

        [JsonPropertyName("base_p50_ms")]
        public double BaseP50Ms { get; init; }

        [JsonPropertyName("cand_p50_ms")]
        public double CandP50Ms { get; init; }

        [JsonPropertyName("p50_ms_delta")]
        public double P50MsDelta { get; init; }

        [JsonPropertyName("base_p95_ms")]
        public double BaseP95Ms { get; init; }

        [JsonPropertyName("cand_p95_ms")]
        public double CandP95Ms { get; init; }

        [JsonPropertyName("p95_ms_delta")]
        public double P95MsDelta { get; init; }

        [JsonPropertyName("base_unplanned_replans")]
        public int BaseUnplannedReplans { get; init; }

        [JsonPropertyName("cand_unplanned_replans")]
        public int CandUnplannedReplans { get; init; }

        [JsonPropertyName("base_choice_mismatches")]
        public int BaseChoiceMismatches { get; init; }

        [JsonPropertyName("cand_choice_mismatches")]
        public int CandChoiceMismatches { get; init; }

        [JsonPropertyName("base_hard_errors")]
        public int BaseHardErrors { get; init; }

        [JsonPropertyName("cand_hard_errors")]
        public int CandHardErrors { get; init; }

        [JsonPropertyName("exact_route_diffs")]
        public int ExactRouteDiffs { get; init; }
    }

    public static int Run(string[] args)
    {
        List<string> positional = [];
        string? outPath = null;
        bool checkExact = false;
        bool gate = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outPath = i + 1 < args.Length
                        ? Path.GetFullPath(args[++i])
                        : throw new ArgumentException("--compare-outcomes --out 缺少路径。");
                    break;
                case "--check-exact":
                    checkExact = true;
                    break;
                case "--gate":
                    gate = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"--compare-outcomes 未知选项：{args[i]}");
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 2)
        {
            Console.Error.WriteLine("用法：--compare-outcomes <baseline.json> <candidate.json> [--out <report.json>] [--check-exact] [--gate]");
            return 2;
        }

        string basePath = Path.GetFullPath(positional[0]);
        string candPath = Path.GetFullPath(positional[1]);

        BenchmarkOutcomeFile baseFile = BenchmarkOutcomeHelper.ReadOutcomeFile(basePath);
        BenchmarkOutcomeFile candFile = BenchmarkOutcomeHelper.ReadOutcomeFile(candPath);

        Dictionary<string, FightOutcomeRecord> baseMap = baseFile.Outcomes.ToDictionary(static o => o.FightId, StringComparer.Ordinal);
        Dictionary<string, FightOutcomeRecord> candMap = candFile.Outcomes.ToDictionary(static o => o.FightId, StringComparer.Ordinal);

        string[] sharedIds = baseFile.Outcomes
            .Select(static o => o.FightId)
            .Where(candMap.ContainsKey)
            .ToArray();
        string[] baseOnly = baseMap.Keys.Except(candMap.Keys, StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        string[] candOnly = candMap.Keys.Except(baseMap.Keys, StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();

        if (sharedIds.Length == 0)
        {
            Console.Error.WriteLine("两侧没有共同的 fight_id，无法成对比较。");
            return 2;
        }

        List<(FightOutcomeRecord Base, FightOutcomeRecord Cand, PairwiseFightDiff Diff)> pairs = new(sharedIds.Length);
        foreach (string id in sharedIds)
        {
            FightOutcomeRecord b = baseMap[id];
            FightOutcomeRecord c = candMap[id];
            int cmp = BenchmarkOutcomeHelper.CompareMaterial(c, b);
            int rev = BenchmarkOutcomeHelper.CompareMaterial(b, c);
            if ((cmp < 0 && rev < 0) || (cmp > 0 && rev > 0))
                throw new InvalidOperationException($"非反对称路线比较：{id}");

            string verdict = cmp < 0 ? "Better" : cmp > 0 ? "Worse" : "Equal";
            double baseV = b.EconomicValueV;
            double candV = c.EconomicValueV;
            // Harmonize HpLoss unit on gross (GrossHpLoss ?? HpLoss) when diffing mixed-schema baselines
            // (pre-Wave-3 files store gross loss in HpLoss with GrossHpLoss == null; Wave-3 files store net in HpLoss).
            bool harmonizeGrossHpLoss = b.GrossHpLoss.HasValue != c.GrossHpLoss.HasValue;
            int baseHpLoss = harmonizeGrossHpLoss ? (b.GrossHpLoss ?? b.HpLoss) : b.HpLoss;
            int candHpLoss = harmonizeGrossHpLoss ? (c.GrossHpLoss ?? c.HpLoss) : c.HpLoss;
            bool exactRoute = string.Equals(b.RouteSignature ?? string.Empty, c.RouteSignature ?? string.Empty, StringComparison.Ordinal)
                && b.Won == c.Won
                && baseHpLoss == candHpLoss
                && b.PotionsUsed == c.PotionsUsed
                && b.Turns == c.Turns
                && b.Score == c.Score;

            int baseErrCount = b.HardErrors > 0 ? b.HardErrors : (!string.IsNullOrEmpty(b.HardError) ? 1 : 0);
            int candErrCount = c.HardErrors > 0 ? c.HardErrors : (!string.IsNullOrEmpty(c.HardError) ? 1 : 0);

            pairs.Add((b, c, new PairwiseFightDiff
            {
                FightId = id,
                Tier = c.Tier,
                Group = c.Group ?? b.Group,
                Verdict = verdict,
                BaseWon = b.Won,
                CandWon = c.Won,
                BaseHpLoss = baseHpLoss,
                CandHpLoss = candHpLoss,
                HpLossDelta = candHpLoss - baseHpLoss,
                BasePotions = b.PotionsUsed,
                CandPotions = c.PotionsUsed,
                PotionDelta = c.PotionsUsed - b.PotionsUsed,
                BaseV = baseV,
                CandV = candV,
                VDelta = Math.Round(candV - baseV, 2),
                BaseTurns = b.Turns,
                CandTurns = c.Turns,
                BaseMs = b.ElapsedMs,
                CandMs = c.ElapsedMs,
                BaseUnplannedReplans = b.UnplannedReplans,
                CandUnplannedReplans = c.UnplannedReplans,
                BaseChoiceMismatches = b.ChoiceMismatches,
                CandChoiceMismatches = c.ChoiceMismatches,
                BaseHardErrors = baseErrCount,
                CandHardErrors = candErrCount,
                ExactRouteMatch = exactRoute,
            }));
        }

        SliceAggregate overall = Aggregate("ALL", pairs);
        SliceAggregate[] byTier = pairs
            .GroupBy(static p => p.Cand.Tier, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => Aggregate(g.Key, g.ToList()))
            .ToArray();

        Console.WriteLine("=== Pairwise A/B Outcome Comparison ===");
        Console.WriteLine($"Base: {basePath} (suite={baseFile.Suite}, profile={baseFile.Profile}, mode={baseFile.SearchMode}, dop={baseFile.Dop})");
        Console.WriteLine($"Cand: {candPath} (suite={candFile.Suite}, profile={candFile.Profile}, mode={candFile.SearchMode}, dop={candFile.Dop})");
        if (baseOnly.Length > 0 || candOnly.Length > 0)
            Console.WriteLine($"Unmatched IDs: base_only={baseOnly.Length}, cand_only={candOnly.Length}");
        Console.WriteLine();

        SliceAggregate[] tableSlices = byTier.Length > 1 ? [overall, .. byTier] : [overall];
        PrintSliceTable(tableSlices);

        PairwiseFightDiff[] reliabilityRegressions = pairs
            .Select(static p => p.Diff)
            .Where(static d => d.CandUnplannedReplans > d.BaseUnplannedReplans
                || d.CandChoiceMismatches > d.BaseChoiceMismatches
                || d.CandHardErrors > d.BaseHardErrors)
            .ToArray();

        if (reliabilityRegressions.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"!!! RELIABILITY REGRESSIONS DETECTED ({reliabilityRegressions.Length} fight(s)) !!!");
            foreach (PairwiseFightDiff r in reliabilityRegressions)
            {
                Console.WriteLine(
                    $"  - {r.FightId} [{r.Tier}]: replans {r.BaseUnplannedReplans}->{r.CandUnplannedReplans}, " +
                    $"choice_mismatches {r.BaseChoiceMismatches}->{r.CandChoiceMismatches}, " +
                    $"hard_errors {r.BaseHardErrors}->{r.CandHardErrors}");
            }
        }

        PairwiseFightDiff[] betterList = pairs
            .Select(static p => p.Diff)
            .Where(static d => d.Verdict == "Better")
            .OrderByDescending(static d => (d.CandWon ? 1 : 0) - (d.BaseWon ? 1 : 0))
            .ThenByDescending(static d => d.VDelta)
            .ThenBy(static d => d.HpLossDelta)
            .ToArray();
        PairwiseFightDiff[] worseList = pairs
            .Select(static p => p.Diff)
            .Where(static d => d.Verdict == "Worse")
            .OrderBy(static d => (d.CandWon ? 1 : 0) - (d.BaseWon ? 1 : 0))
            .ThenBy(static d => d.VDelta)
            .ThenByDescending(static d => d.HpLossDelta)
            .ToArray();

        if (betterList.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"### Better Fights ({betterList.Length})");
            PrintDiffRows(betterList.Take(25));
        }

        if (worseList.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"### Worse Fights ({worseList.Length})");
            PrintDiffRows(worseList);
        }

        if (checkExact)
        {
            PairwiseFightDiff[] routeDiffs = pairs.Select(static p => p.Diff).Where(static d => !d.ExactRouteMatch).ToArray();
            Console.WriteLine();
            Console.WriteLine($"Exact Route Check: {pairs.Count - routeDiffs.Length}/{pairs.Count} bit-exact ({routeDiffs.Length} diff(s))");
            if (routeDiffs.Length > 0)
                PrintDiffRows(routeDiffs.Take(20));
        }

        bool holdoutRegressed = byTier.Any(static t =>
            (string.Equals(t.Slice, "holdout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Slice, "B", StringComparison.OrdinalIgnoreCase))
            && t.Worse > 0);
        bool gatePass = overall.CandHardErrors == 0
            && overall.CandUnplannedReplans == 0
            && overall.CandChoiceMismatches == 0
            && overall.CandWins >= overall.BaseWins
            && !holdoutRegressed;
        if (checkExact)
            gatePass &= overall.ExactRouteDiffs == 0;

        if (outPath != null)
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            object report = new
            {
                base_path = basePath,
                cand_path = candPath,
                shared_fights = sharedIds.Length,
                base_only = baseOnly,
                cand_only = candOnly,
                overall,
                by_tier = byTier,
                reliability_regressions = reliabilityRegressions,
                gate_pass = gatePass,
                better_fights = betterList,
                worse_fights = worseList,
                all_diffs = pairs.Select(static p => p.Diff).ToArray(),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, BenchmarkOutcomeHelper.JsonOptions));
            Console.WriteLine();
            Console.WriteLine($"comparison_json={outPath}");
        }

        if (gate)
        {
            Console.WriteLine($"AB_GATE={(gatePass ? "PASS" : "FAIL")}");
            return gatePass ? 0 : 1;
        }
        if (checkExact && overall.ExactRouteDiffs > 0)
            return 1;
        return 0;
    }

    private static SliceAggregate Aggregate(
        string sliceName,
        List<(FightOutcomeRecord Base, FightOutcomeRecord Cand, PairwiseFightDiff Diff)> rows)
    {
        int n = rows.Count;
        int better = rows.Count(static r => r.Diff.Verdict == "Better");
        int equal = rows.Count(static r => r.Diff.Verdict == "Equal");
        int worse = rows.Count(static r => r.Diff.Verdict == "Worse");
        double? ratio = worse > 0
            ? Math.Round((double)better / worse, 2)
            : (better > 0 ? null : 1.0);

        int baseWins = rows.Count(static r => r.Base.Won);
        int candWins = rows.Count(static r => r.Cand.Won);
        double baseWinRate = n > 0 ? (double)baseWins / n : 0.0;
        double candWinRate = n > 0 ? (double)candWins / n : 0.0;

        var validBoth = rows.Where(static r => r.Diff.BaseHardErrors == 0 && r.Diff.CandHardErrors == 0).ToList();
        double baseAvgV = validBoth.Count > 0 ? validBoth.Average(static r => r.Diff.BaseV) : 0.0;
        double candAvgV = validBoth.Count > 0 ? validBoth.Average(static r => r.Diff.CandV) : 0.0;
        double baseAvgHp = validBoth.Count > 0 ? validBoth.Average(static r => (double)r.Diff.BaseHpLoss) : 0.0;
        double candAvgHp = validBoth.Count > 0 ? validBoth.Average(static r => (double)r.Diff.CandHpLoss) : 0.0;
        double baseAvgPot = validBoth.Count > 0 ? validBoth.Average(static r => (double)r.Base.PotionsUsed) : 0.0;
        double candAvgPot = validBoth.Count > 0 ? validBoth.Average(static r => (double)r.Cand.PotionsUsed) : 0.0;

        var sharedWins = validBoth.Where(static r => r.Base.Won && r.Cand.Won).ToList();
        double sharedWinsHpDelta = sharedWins.Count > 0
            ? sharedWins.Average(static r => (double)r.Diff.HpLossDelta)
            : 0.0;

        double[] baseTimes = validBoth.Select(static r => r.Base.ElapsedMs).ToArray();
        double[] candTimes = validBoth.Select(static r => r.Cand.ElapsedMs).ToArray();
        double baseP50 = BenchmarkOutcomeHelper.Percentile(baseTimes, 0.50);
        double candP50 = BenchmarkOutcomeHelper.Percentile(candTimes, 0.50);
        double baseP95 = BenchmarkOutcomeHelper.Percentile(baseTimes, 0.95);
        double candP95 = BenchmarkOutcomeHelper.Percentile(candTimes, 0.95);

        return new SliceAggregate
        {
            Slice = sliceName,
            Fights = n,
            Better = better,
            Equal = equal,
            Worse = worse,
            BetterToWorseRatio = ratio,
            BaseWins = baseWins,
            CandWins = candWins,
            WinDelta = candWins - baseWins,
            BaseWinRate = Math.Round(baseWinRate, 4),
            CandWinRate = Math.Round(candWinRate, 4),
            BaseAvgV = Math.Round(baseAvgV, 2),
            CandAvgV = Math.Round(candAvgV, 2),
            VDelta = Math.Round(candAvgV - baseAvgV, 2),
            BaseAvgHpLoss = Math.Round(baseAvgHp, 2),
            CandAvgHpLoss = Math.Round(candAvgHp, 2),
            HpLossDelta = Math.Round(candAvgHp - baseAvgHp, 2),
            SharedWins = sharedWins.Count,
            SharedWinsHpLossDelta = Math.Round(sharedWinsHpDelta, 2),
            BaseAvgPotions = Math.Round(baseAvgPot, 2),
            CandAvgPotions = Math.Round(candAvgPot, 2),
            PotionDelta = Math.Round(candAvgPot - baseAvgPot, 2),
            BaseP50Ms = Math.Round(baseP50, 1),
            CandP50Ms = Math.Round(candP50, 1),
            P50MsDelta = Math.Round(candP50 - baseP50, 1),
            BaseP95Ms = Math.Round(baseP95, 1),
            CandP95Ms = Math.Round(candP95, 1),
            P95MsDelta = Math.Round(candP95 - baseP95, 1),
            BaseUnplannedReplans = rows.Sum(static r => r.Diff.BaseUnplannedReplans),
            CandUnplannedReplans = rows.Sum(static r => r.Diff.CandUnplannedReplans),
            BaseChoiceMismatches = rows.Sum(static r => r.Diff.BaseChoiceMismatches),
            CandChoiceMismatches = rows.Sum(static r => r.Diff.CandChoiceMismatches),
            BaseHardErrors = rows.Sum(static r => r.Diff.BaseHardErrors),
            CandHardErrors = rows.Sum(static r => r.Diff.CandHardErrors),
            ExactRouteDiffs = rows.Count(static r => !r.Diff.ExactRouteMatch),
        };
    }

    private static void PrintSliceTable(IReadOnlyList<SliceAggregate> slices)
    {
        Console.WriteLine("| Slice | N | Better / Eq / Worse (B:W) | Win Rate (Δ) | Avg V (Δ) | Avg HP Loss (Δ / SharedWin Δ) | Avg Pot (Δ) | p50 ms (Δ) | p95 ms (Δ) | Replans | ChoiceErr | HardErr |");
        Console.WriteLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (SliceAggregate s in slices)
        {
            string ratioText = s.BetterToWorseRatio.HasValue
                ? s.BetterToWorseRatio.Value.ToString("F2", CultureInfo.InvariantCulture) + "x"
                : "inf";
            Console.WriteLine(
                $"| {s.Slice} | {s.Fights} | {s.Better} / {s.Equal} / {s.Worse} ({ratioText}) | " +
                $"{s.BaseWinRate:P1}->{s.CandWinRate:P1} ({SignedInt(s.WinDelta)}) | " +
                $"{s.BaseAvgV:F2}->{s.CandAvgV:F2} ({SignedDouble(s.VDelta)}) | " +
                $"{s.BaseAvgHpLoss:F2}->{s.CandAvgHpLoss:F2} ({SignedDouble(s.HpLossDelta)} / {SignedDouble(s.SharedWinsHpLossDelta)}) | " +
                $"{s.BaseAvgPotions:F2}->{s.CandAvgPotions:F2} ({SignedDouble(s.PotionDelta)}) | " +
                $"{s.BaseP50Ms:F1}->{s.CandP50Ms:F1} ({SignedDouble(s.P50MsDelta, "F1")}) | " +
                $"{s.BaseP95Ms:F1}->{s.CandP95Ms:F1} ({SignedDouble(s.P95MsDelta, "F1")}) | " +
                $"{s.BaseUnplannedReplans}->{s.CandUnplannedReplans} | " +
                $"{s.BaseChoiceMismatches}->{s.CandChoiceMismatches} | " +
                $"{s.BaseHardErrors}->{s.CandHardErrors} |");
        }
    }

    private static void PrintDiffRows(IEnumerable<PairwiseFightDiff> rows)
    {
        Console.WriteLine("| FightId | Tier | Verdict | Won | HP Loss (Δ) | Pot (Δ) | V (Δ) | Turns | ms |");
        Console.WriteLine("|---|---|---|---|---:|---:|---:|---:|---:|");
        foreach (PairwiseFightDiff d in rows)
        {
            Console.WriteLine(
                $"| {d.FightId} | {d.Tier} | {d.Verdict} | {(d.BaseWon ? "W" : "L")}->{(d.CandWon ? "W" : "L")} | " +
                $"{d.BaseHpLoss}->{d.CandHpLoss} ({SignedInt(d.HpLossDelta)}) | " +
                $"{d.BasePotions}->{d.CandPotions} ({SignedInt(d.PotionDelta)}) | " +
                $"{d.BaseV:F2}->{d.CandV:F2} ({SignedDouble(d.VDelta)}) | " +
                $"{d.BaseTurns}->{d.CandTurns} | {d.BaseMs:F0}->{d.CandMs:F0} |");
        }
    }

    private static string SignedInt(int v) => v > 0 ? $"+{v}" : v.ToString(CultureInfo.InvariantCulture);

    private static string SignedDouble(double v, string fmt = "F2")
        => v > 0 ? $"+{v.ToString(fmt, CultureInfo.InvariantCulture)}" : v.ToString(fmt, CultureInfo.InvariantCulture);
}
