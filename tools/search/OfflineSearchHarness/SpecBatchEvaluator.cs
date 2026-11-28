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
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace OfflineSearchHarness;

/// <summary>
/// Single-process JSONL batch evaluator for DeckProfiler research:
/// boots the headless STS2 host once, accepts CombatSetupSpec / bundle_path / request_path,
/// runs closed-loop deployment, and streams full fight + per-turn + CPU/wall/memory telemetry as JSONL.
/// </summary>
internal static class SpecBatchEvaluator
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteJsonLineOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool _damageObserverPatched;
    private static Action<ICombatState, Creature?, DamageResult, CardModel?>? _activeDamageObserver;

    private static void EnsureLethalDamageObserverPatched()
    {
        if (_damageObserverPatched)
            return;
        MethodInfo target = typeof(Hook).GetMethod(
            nameof(Hook.AfterDamageGiven),
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(Hook), nameof(Hook.AfterDamageGiven));
        MethodInfo prefix = typeof(SpecBatchEvaluator).GetMethod(
            nameof(ObserveAfterDamageGivenPrefix),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        new Harmony("combatsolver.offline.specbatch.telemetry").Patch(target, prefix: new HarmonyMethod(prefix));
        _damageObserverPatched = true;
    }

    private static void ObserveAfterDamageGivenPrefix(
        ICombatState combatState,
        Creature? dealer,
        DamageResult results,
        CardModel? cardSource)
    {
        _activeDamageObserver?.Invoke(combatState, dealer, results, cardSource);
    }

    private sealed record EvalSpecInput
    {
        [JsonPropertyName("op")]
        public string? Op { get; init; }

        [JsonPropertyName("spec_id")]
        public string SpecId { get; init; } = string.Empty;

        [JsonPropertyName("group")]
        public string? Group { get; init; }

        [JsonPropertyName("tier")]
        public string? Tier { get; init; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; init; }

        [JsonPropertyName("setup")]
        public CombatSetupSpec? Setup { get; init; }

        [JsonPropertyName("bundle_path")]
        public string? BundlePath { get; init; }

        [JsonPropertyName("request_path")]
        public string? RequestPath { get; init; }

        [JsonPropertyName("profile")]
        public string? Profile { get; init; }

        [JsonPropertyName("beam")]
        public int? Beam { get; init; }

        [JsonPropertyName("nodes")]
        public int? Nodes { get; init; }

        [JsonPropertyName("potion_policy")]
        public string? PotionPolicy { get; init; }

        [JsonPropertyName("budget_ms")]
        public int? BudgetMs { get; init; }

        [JsonPropertyName("dop")]
        public int? Dop { get; init; }

        [JsonPropertyName("emit_post_state")]
        public bool EmitPostState { get; init; }

        [JsonPropertyName("include_per_turn")]
        public bool? IncludePerTurn { get; init; }
    }

    private sealed record EvalResolvedCard(
        [property: JsonPropertyName("card_id")] string CardId,
        [property: JsonPropertyName("upgrade_level")] int UpgradeLevel,
        [property: JsonPropertyName("enchantment_id")] string? EnchantmentId);

    private sealed record EvalEnemySnapshot(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("hp")] int Hp,
        [property: JsonPropertyName("max_hp")] int MaxHp,
        [property: JsonPropertyName("block")] int Block,
        [property: JsonPropertyName("alive")] bool Alive,
        [property: JsonPropertyName("powers")] Dictionary<string, int> Powers);

    private sealed record EvalTurnEventTelemetry(
        [property: JsonPropertyName("enemy_dmg_card_attack_hp")] int EnemyDmgCardAttackHp,
        [property: JsonPropertyName("enemy_dmg_card_attack_blocked")] int EnemyDmgCardAttackBlocked,
        [property: JsonPropertyName("enemy_dmg_card_attack_hits")] int EnemyDmgCardAttackHits,
        [property: JsonPropertyName("enemy_dmg_card_other_hp")] int EnemyDmgCardOtherHp,
        [property: JsonPropertyName("enemy_dmg_card_other_blocked")] int EnemyDmgCardOtherBlocked,
        [property: JsonPropertyName("enemy_dmg_player_side_power_hp")] int EnemyDmgPlayerSidePowerHp,
        [property: JsonPropertyName("enemy_dmg_enemy_side_unblockable_hp")] int EnemyDmgEnemySideUnblockableHp,
        [property: JsonPropertyName("enemy_dmg_enemy_side_blockable_hp")] int EnemyDmgEnemySideBlockableHp,
        [property: JsonPropertyName("enemy_dmg_overkill")] int EnemyDmgOverkill,
        [property: JsonPropertyName("distinct_enemies_damaged")] int DistinctEnemiesDamaged,
        [property: JsonPropertyName("enemy_dmg_max_single_target")] int EnemyDmgMaxSingleTarget,
        [property: JsonPropertyName("enemy_dmg_max_single_hit")] int EnemyDmgMaxSingleHit,
        [property: JsonPropertyName("player_incoming_raw_dmg")] int PlayerIncomingRawDmg,
        [property: JsonPropertyName("player_dmg_blocked")] int PlayerDmgBlocked,
        [property: JsonPropertyName("player_hp_dmg_taken")] int PlayerHpDmgTaken,
        [property: JsonPropertyName("player_unblockable_hp_dmg")] int PlayerUnblockableHpDmg,
        [property: JsonPropertyName("player_attack_hits_taken")] int PlayerAttackHitsTaken,
        [property: JsonPropertyName("player_block_gained")] int PlayerBlockGained,
        [property: JsonPropertyName("player_block_from_cards")] int PlayerBlockFromCards,
        [property: JsonPropertyName("player_block_from_non_card")] int PlayerBlockFromNonCard,
        [property: JsonPropertyName("enemy_block_gained")] int EnemyBlockGained,
        [property: JsonPropertyName("poison_applied")] int PoisonApplied,
        [property: JsonPropertyName("weak_applied")] int WeakApplied,
        [property: JsonPropertyName("vuln_applied")] int VulnApplied,
        [property: JsonPropertyName("str_down_applied")] int StrDownApplied,
        [property: JsonPropertyName("cards_drawn_initial")] int CardsDrawnInitial,
        [property: JsonPropertyName("cards_drawn_extra")] int CardsDrawnExtra,
        [property: JsonPropertyName("cards_discarded")] int CardsDiscarded,
        [property: JsonPropertyName("cards_exhausted")] int CardsExhausted,
        [property: JsonPropertyName("cards_generated")] int CardsGenerated,
        [property: JsonPropertyName("sly_auto_plays")] int SlyAutoPlays,
        [property: JsonPropertyName("zero_cost_cards_played")] int ZeroCostCardsPlayed,
        [property: JsonPropertyName("shivs_played")] int ShivsPlayed,
        [property: JsonPropertyName("energy_spent_total")] int EnergySpentTotal);

    private readonly record struct BufferedHistoryEvent(
        CombatHistoryEntry Entry,
        int TurnNumber,
        bool IsSlyAutoPlay);

    private sealed record EvalTurnTelemetry(
        [property: JsonPropertyName("turn")] int Turn,
        [property: JsonPropertyName("replanned_on_turn")] bool ReplannedOnTurn,
        [property: JsonPropertyName("player_hp_start")] int PlayerHpStart,
        [property: JsonPropertyName("player_hp_end")] int PlayerHpEnd,
        [property: JsonPropertyName("energy_start")] int EnergyStart,
        [property: JsonPropertyName("energy_left_live")] int EnergyLeftLive,
        [property: JsonPropertyName("hand_size_start")] int HandSizeStart,
        [property: JsonPropertyName("hand_size_end")] int HandSizeEnd,
        [property: JsonPropertyName("draw_pile_start")] int DrawPileStart,
        [property: JsonPropertyName("draw_pile_end")] int DrawPileEnd,
        [property: JsonPropertyName("discard_pile_end")] int DiscardPileEnd,
        [property: JsonPropertyName("exhaust_pile_start")] int ExhaustPileStart,
        [property: JsonPropertyName("exhaust_pile_end")] int ExhaustPileEnd,
        [property: JsonPropertyName("hand_end_status_count")] int HandEndStatusCount,
        [property: JsonPropertyName("hand_end_curse_count")] int HandEndCurseCount,
        [property: JsonPropertyName("hand_end_afflicted_count")] int HandEndAfflictedCount,
        [property: JsonPropertyName("hand_end_playable_left")] int HandEndPlayableLeft,
        [property: JsonPropertyName("deck_status_curse_end")] int DeckStatusCurseEnd,
        [property: JsonPropertyName("cards_played")] int CardsPlayed,
        [property: JsonPropertyName("attack_cards_played")] int AttackCardsPlayed,
        [property: JsonPropertyName("skill_cards_played")] int SkillCardsPlayed,
        [property: JsonPropertyName("power_cards_played")] int PowerCardsPlayed,
        [property: JsonPropertyName("status_cards_played")] int StatusCardsPlayed,
        [property: JsonPropertyName("curse_cards_played")] int CurseCardsPlayed,
        [property: JsonPropertyName("afflicted_cards_played")] int AfflictedCardsPlayed,
        [property: JsonPropertyName("afflictions_played")] Dictionary<string, int> AfflictionsPlayed,
        [property: JsonPropertyName("cards_played_ids")] string[] CardsPlayedIds,
        [property: JsonPropertyName("potions_used_live")] int PotionsUsedLive,
        [property: JsonPropertyName("potion_ids_used")] string[] PotionIdsUsed,
        [property: JsonPropertyName("player_block_end")] int PlayerBlockEnd,
        [property: JsonPropertyName("enemy_total_hp_start")] int EnemyTotalHpStart,
        [property: JsonPropertyName("enemy_total_hp_player_turn_end")] int EnemyTotalHpPlayerTurnEnd,
        [property: JsonPropertyName("enemy_total_block_player_turn_end")] int EnemyTotalBlockPlayerTurnEnd,
        [property: JsonPropertyName("enemy_total_hp_end")] int EnemyTotalHpEnd,
        [property: JsonPropertyName("enemy_alive_start")] int EnemyAliveStart,
        [property: JsonPropertyName("enemy_alive_end")] int EnemyAliveEnd,
        [property: JsonPropertyName("hp_lost")] int? HpLost,
        [property: JsonPropertyName("hp_recovered")] int? HpRecovered,
        [property: JsonPropertyName("sold_hp")] int? SoldHp,
        [property: JsonPropertyName("enemy_hp_lost")] int? EnemyHpLost,
        [property: JsonPropertyName("max_block")] int? MaxBlock,
        [property: JsonPropertyName("actual_block")] int? ActualBlock,
        [property: JsonPropertyName("energy_left")] int? EnergyLeft,
        [property: JsonPropertyName("potion_count")] int? PotionCount,
        [property: JsonPropertyName("potion_strategic_cost")] int? PotionStrategicCost,
        [property: JsonPropertyName("kills")] string[] Kills,
        [property: JsonPropertyName("player_powers_end")] Dictionary<string, int> PlayerPowersEnd,
        [property: JsonPropertyName("enemies_player_turn_end")] EvalEnemySnapshot[] EnemiesPlayerTurnEnd,
        [property: JsonPropertyName("enemies_end")] EvalEnemySnapshot[] EnemiesEnd,
        [property: JsonPropertyName("events")] EvalTurnEventTelemetry Events);

    private sealed record EvalTimingTelemetry(
        [property: JsonPropertyName("setup_ms")] double SetupMs,
        [property: JsonPropertyName("solve_ms")] double SolveMs,
        [property: JsonPropertyName("deploy_overhead_ms")] double DeployOverheadMs,
        [property: JsonPropertyName("total_wall_ms")] double TotalWallMs,
        [property: JsonPropertyName("cpu_time_ms")] double CpuTimeMs,
        [property: JsonPropertyName("working_set_mb")] double WorkingSetMb,
        [property: JsonPropertyName("gc_gen2_delta")] int GcGen2Delta);

    private sealed record EvalPostPotionConsumed(
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("potion_id")] string PotionId,
        [property: JsonPropertyName("passive")] bool Passive);

    private sealed record EvalPostRelicState(
        [property: JsonPropertyName("relic_id")] string RelicId,
        [property: JsonPropertyName("ints")] Dictionary<string, int>? Ints = null,
        [property: JsonPropertyName("bools")] Dictionary<string, bool>? Bools = null);

    private sealed record EvalDeckMutation(
        [property: JsonPropertyName("deck_index")] int DeckIndex,
        [property: JsonPropertyName("card_id")] string CardId,
        [property: JsonPropertyName("ints")] Dictionary<string, int> Ints);

    private sealed record EvalPostCombatState(
        [property: JsonPropertyName("combat_outcome")] string CombatOutcome,
        [property: JsonPropertyName("hp_after")] int HpAfter,
        [property: JsonPropertyName("max_hp_after")] int MaxHpAfter,
        [property: JsonPropertyName("gold_delta")] int GoldDelta,
        [property: JsonPropertyName("potions_after")] string?[] PotionsAfter,
        [property: JsonPropertyName("potions_consumed")] EvalPostPotionConsumed[] PotionsConsumed,
        [property: JsonPropertyName("potions_gained")] string[] PotionsGained,
        [property: JsonPropertyName("relic_states_after")] EvalPostRelicState[] RelicStatesAfter,
        [property: JsonPropertyName("relic_counter_deltas")] Dictionary<string, int> RelicCounterDeltas,
        [property: JsonPropertyName("deck_mutations")] EvalDeckMutation[] DeckMutations,
        [property: JsonPropertyName("extra_card_rewards")] int ExtraCardRewards);

    private sealed record EvalSpecResultRecord
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 4;

        [JsonPropertyName("spec_id")]
        public required string SpecId { get; init; }

        [JsonPropertyName("profile")]
        public required string Profile { get; init; }

        [JsonPropertyName("potion_policy")]
        public required string PotionPolicy { get; init; }

        [JsonPropertyName("dop")]
        public int Dop { get; init; }

        [JsonPropertyName("budget_ms")]
        public int BudgetMs { get; init; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; init; }

        [JsonPropertyName("resolved_deck")]
        public EvalResolvedCard[] ResolvedDeck { get; init; } = [];

        [JsonPropertyName("resolved_relics")]
        public string[] ResolvedRelics { get; init; } = [];

        [JsonPropertyName("resolved_potions")]
        public string[] ResolvedPotions { get; init; } = [];

        [JsonPropertyName("outcome")]
        public required FightOutcomeRecord Outcome { get; init; }

        [JsonPropertyName("turn1_was_losslessly_exhausted")]
        public bool Turn1WasLosslesslyExhausted { get; init; }

        [JsonPropertyName("was_losslessly_exhausted")]
        public bool WasLosslesslyExhausted { get; init; }

        [JsonPropertyName("only_death_routes_found")]
        public bool OnlyDeathRoutesFound { get; init; }

        [JsonPropertyName("death_turn")]
        public int? DeathTurn { get; init; }

        [JsonPropertyName("potions_used_ids")]
        public string[] PotionsUsedIds { get; init; } = [];

        [JsonPropertyName("deploy_replans")]
        public PlayerBeatReplayRunner.DeployReplanDiff[] DeployReplans { get; init; } = [];

        [JsonPropertyName("per_turn")]
        public EvalTurnTelemetry[] PerTurn { get; init; } = [];

        [JsonPropertyName("post_combat_state")]
        public EvalPostCombatState? PostCombatState { get; init; }

        [JsonPropertyName("timing")]
        public required EvalTimingTelemetry Timing { get; init; }
    }

    public static int Run(string[] args)
    {
        string? specsPath = null;
        string? outPath = null;
        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        string potionPolicy = "Smart";
        int budgetMs = 600_000;
        int dop = 1;
        int maxTurns = 40;
        bool resume = false;
        string? logDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--eval-specs 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--specs": specsPath = Path.GetFullPath(NextVal()); break;
                case "--out": outPath = Path.GetFullPath(NextVal()); break;
                case "--profile": profile = NextVal(); break;
                case "--beam": beamOverride = Math.Max(4, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--nodes": nodesOverride = Math.Max(100, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--potion-policy": potionPolicy = NextVal(); break;
                case "--budget-ms": budgetMs = Math.Max(500, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--dop": dop = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--max-turns": maxTurns = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--resume": resume = true; break;
                case "--log-dir": logDir = Path.GetFullPath(NextVal()); break;
                default:
                    if (!key.StartsWith("--", StringComparison.Ordinal) && specsPath == null)
                        specsPath = Path.GetFullPath(key);
                    else
                        throw new ArgumentException($"--eval-specs 未知选项：{key}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(specsPath) || !File.Exists(specsPath))
            throw new ArgumentException($"找不到 --specs 文件：{specsPath ?? "(null)"}");
        if (string.IsNullOrWhiteSpace(outPath))
            throw new ArgumentException("--eval-specs 需要指定 --out <results.jsonl>。");

        SolverPotionPolicy defaultPotionPolicy = Enum.Parse<SolverPotionPolicy>(potionPolicy, ignoreCase: true);
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException($"未知 --profile：{profile}");

        string outDir = Path.GetDirectoryName(outPath)!;
        Directory.CreateDirectory(outDir);
        string resolvedLogDir = logDir ?? Path.Combine(outDir, "_eval_specs_logs");
        Directory.CreateDirectory(resolvedLogDir);

        HashSet<string> completedIds = new(StringComparer.Ordinal);
        if (resume && File.Exists(outPath))
        {
            foreach (string existingLine in File.ReadLines(outPath))
            {
                if (string.IsNullOrWhiteSpace(existingLine))
                    continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(existingLine);
                    if (doc.RootElement.TryGetProperty("spec_id", out JsonElement idEl)
                        && idEl.GetString() is { Length: > 0 } existingId)
                    {
                        completedIds.Add(existingId);
                    }
                }
                catch (JsonException)
                {
                    // Ignore partial trailing line on resume.
                }
            }
        }

        List<EvalSpecInput> inputs = LoadSpecs(specsPath);

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions bootOptions = new()
        {
            Profile = profile,
            Beam = beamOverride,
            Nodes = nodesOverride,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = defaultPotionPolicy.ToString(),
            SearchMode = "Coordinator",
            WorkspaceDirectory = outDir,
            OutputDirectory = resolvedLogDir,
        };
        GameBootstrap.EnsureBootstrapped(loop, bootOptions);

        int processed = 0;
        int skipped = 0;
        int wins = 0;
        int defeats = 0;
        int hardErrors = 0;
        Stopwatch batchSw = Stopwatch.StartNew();

        try
        {
            using FileStream fs = new(
                outPath,
                resume ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            using StreamWriter writer = new(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (EvalSpecInput spec in inputs)
            {
                if (completedIds.Contains(spec.SpecId))
                {
                    skipped++;
                    continue;
                }

                EvalSpecResultRecord result = EvaluateSingleSpec(
                    loop, spec, outDir, resolvedLogDir,
                    profile, beamOverride, nodesOverride, defaultPotionPolicy, budgetMs, dop, maxTurns);

                writer.WriteLine(JsonSerializer.Serialize(result, WriteJsonLineOptions));
                writer.Flush();

                processed++;
                if (result.Outcome.HardErrors > 0)
                    hardErrors++;
                else if (result.Outcome.Won)
                    wins++;
                else
                    defeats++;
            }
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        batchSw.Stop();
        double avgMs = processed > 0 ? batchSw.Elapsed.TotalMilliseconds / processed : 0.0;
        Console.WriteLine(
            $"[eval-specs] processed={processed} skipped={skipped} wins={wins} defeats={defeats} " +
            $"hard_errors={hardErrors} total_wall_s={batchSw.Elapsed.TotalSeconds:F3} avg_ms={avgMs:F1} out={outPath}");
        return hardErrors == 0 ? 0 : 1;
    }

    public static int RunStdio(string[] args)
    {
        // 必须在任何游戏/模组初始化前隔离 stdout：所有杂散 Console.WriteLine 自动归入 stderr，
        // 仅 protocolOut 以显式无 BOM UTF-8 直接写向 stdout 管道（避免 Windows CP936 代码页损坏全角字符）。
        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using StreamReader protocolIn = new(Console.OpenStandardInput(), utf8NoBom);
        using StreamWriter protocolOut = new(Console.OpenStandardOutput(), utf8NoBom);
        Console.SetOut(Console.Error);

        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        string potionPolicy = "Smart";
        int budgetMs = 600_000;
        int dop = 1;
        int maxTurns = 40;
        string? logDir = null;
        string? workspaceDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--eval-stdio 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--profile": profile = NextVal(); break;
                case "--beam": beamOverride = Math.Max(4, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--nodes": nodesOverride = Math.Max(100, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--potion-policy": potionPolicy = NextVal(); break;
                case "--budget-ms": budgetMs = Math.Max(500, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--dop": dop = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--max-turns": maxTurns = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture)); break;
                case "--log-dir": logDir = Path.GetFullPath(NextVal()); break;
                case "--workspace": workspaceDir = Path.GetFullPath(NextVal()); break;
                default:
                    throw new ArgumentException($"--eval-stdio 未知选项：{key}");
            }
        }

        SolverPotionPolicy defaultPotionPolicy = Enum.Parse<SolverPotionPolicy>(potionPolicy, ignoreCase: true);
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException($"未知 --profile：{profile}");

        string resolvedLogDir = logDir ?? Path.Combine(Path.GetTempPath(), $"offline_harness_stdio_{Environment.ProcessId}");
        string resolvedWorkspace = workspaceDir ?? resolvedLogDir;
        Directory.CreateDirectory(resolvedWorkspace);
        Directory.CreateDirectory(resolvedLogDir);

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions bootOptions = new()
        {
            Profile = profile,
            Beam = beamOverride,
            Nodes = nodesOverride,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = defaultPotionPolicy.ToString(),
            SearchMode = "Coordinator",
            WorkspaceDirectory = resolvedWorkspace,
            OutputDirectory = resolvedLogDir,
        };
        GameBootstrap.EnsureBootstrapped(loop, bootOptions);

        void WriteFrame(object frame)
        {
            protocolOut.WriteLine(JsonSerializer.Serialize(frame, WriteJsonLineOptions));
            protocolOut.Flush();
        }

        WriteFrame(new
        {
            type = "ready",
            protocol_version = 1,
            schema_version = 4,
            game_version = "0.114.0",
            pid = Environment.ProcessId,
            default_profile = profile,
            default_dop = dop,
        });

        int fightsServed = 0;
        bool byeSent = false;
        try
        {
            while (protocolIn.ReadLine() is { } rawLine)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0)
                    continue;

                string op = "eval";
                string fallbackSpecId = $"stdio_{fightsServed + 1}";
                try
                {
                    using JsonDocument peek = JsonDocument.Parse(trimmed);
                    if (peek.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("NDJSON 请求帧必须为 JSON 对象。");
                    if (peek.RootElement.TryGetProperty("op", out JsonElement opEl)
                        && opEl.ValueKind == JsonValueKind.String
                        && opEl.GetString() is { Length: > 0 } parsedOp)
                    {
                        op = parsedOp.Trim().ToLowerInvariant();
                    }
                    if (peek.RootElement.TryGetProperty("spec_id", out JsonElement sidEl)
                        && sidEl.ValueKind == JsonValueKind.String
                        && sidEl.GetString() is { Length: > 0 } parsedSid)
                    {
                        fallbackSpecId = parsedSid;
                    }
                }
                catch (Exception parseEx)
                {
                    WriteFrame(CreateStdioErrorRecord(
                        fallbackSpecId, profile, defaultPotionPolicy.ToString(), dop, budgetMs,
                        $"{parseEx.GetType().Name}: {parseEx.Message}"));
                    continue;
                }

                if (op == "ping")
                {
                    WriteFrame(new { type = "pong" });
                    continue;
                }

                if (op == "shutdown")
                {
                    WriteFrame(new { type = "bye", fights_served = fightsServed });
                    byeSent = true;
                    break;
                }

                if (op != "eval")
                {
                    WriteFrame(CreateStdioErrorRecord(
                        fallbackSpecId, profile, defaultPotionPolicy.ToString(), dop, budgetMs,
                        $"未知 op：{op}"));
                    continue;
                }

                EvalSpecInput? spec;
                try
                {
                    spec = JsonSerializer.Deserialize<EvalSpecInput>(trimmed, ReadJsonOptions)
                        ?? throw new InvalidDataException("eval 请求反序列化为空。");
                    if (string.IsNullOrWhiteSpace(spec.SpecId))
                        spec = spec with { SpecId = fallbackSpecId };
                    int sourceCount = (spec.Setup != null ? 1 : 0)
                        + (!string.IsNullOrWhiteSpace(spec.BundlePath) ? 1 : 0)
                        + (!string.IsNullOrWhiteSpace(spec.RequestPath) ? 1 : 0);
                    if (sourceCount != 1)
                        throw new InvalidDataException($"spec '{spec.SpecId}' 必须且只能指定 setup / bundle_path / request_path 之一。");
                }
                catch (Exception ex)
                {
                    WriteFrame(CreateStdioErrorRecord(
                        fallbackSpecId, profile, defaultPotionPolicy.ToString(), dop, budgetMs,
                        $"{ex.GetType().Name}: {ex.Message}"));
                    continue;
                }

                EvalSpecResultRecord result = EvaluateSingleSpec(
                    loop, spec, resolvedWorkspace, resolvedLogDir,
                    profile, beamOverride, nodesOverride, defaultPotionPolicy, budgetMs, dop, maxTurns,
                    emitPostState: spec.EmitPostState,
                    includePerTurn: spec.IncludePerTurn ?? true,
                    resultType: "eval_result");
                fightsServed++;
                WriteFrame(result);
            }

            if (!byeSent)
            {
                try
                {
                    WriteFrame(new { type = "bye", fights_served = fightsServed });
                }
                catch (IOException)
                {
                    // Client closed stdout pipe before EOF bye.
                }
            }
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        return 0;
    }

    private static EvalSpecResultRecord CreateStdioErrorRecord(
        string specId,
        string profile,
        string potionPolicy,
        int dop,
        int budgetMs,
        string errorMessage)
    {
        double workingSetMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0), 2);
        return new EvalSpecResultRecord
        {
            Type = "eval_result",
            SpecId = specId,
            Profile = profile,
            PotionPolicy = potionPolicy,
            Dop = dop,
            BudgetMs = budgetMs,
            Outcome = new FightOutcomeRecord
            {
                FightId = specId,
                Tier = "spec",
                Group = "eval_stdio",
                CharacterId = "UNKNOWN",
                EncounterId = "UNKNOWN",
                Won = false,
                Survives = false,
                Boundary = "Error",
                HardErrors = 1,
                HardError = errorMessage,
            },
            Timing = new EvalTimingTelemetry(
                SetupMs: 0.0,
                SolveMs: 0.0,
                DeployOverheadMs: 0.0,
                TotalWallMs: 0.0,
                CpuTimeMs: 0.0,
                WorkingSetMb: workingSetMb,
                GcGen2Delta: 0),
        };
    }

    private static List<EvalSpecInput> LoadSpecs(string specsPath)
    {
        List<EvalSpecInput> inputs = [];
        int lineNo = 0;
        foreach (string rawLine in File.ReadLines(specsPath))
        {
            lineNo++;
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            EvalSpecInput item = JsonSerializer.Deserialize<EvalSpecInput>(trimmed, ReadJsonOptions)
                ?? throw new InvalidDataException($"specs 第 {lineNo} 行反序列化为空。");
            if (string.IsNullOrWhiteSpace(item.SpecId))
                throw new InvalidDataException($"specs 第 {lineNo} 行缺少 spec_id。");
            int sourceCount = (item.Setup != null ? 1 : 0)
                + (!string.IsNullOrWhiteSpace(item.BundlePath) ? 1 : 0)
                + (!string.IsNullOrWhiteSpace(item.RequestPath) ? 1 : 0);
            if (sourceCount != 1)
                throw new InvalidDataException($"spec '{item.SpecId}' 必须且只能指定 setup / bundle_path / request_path 之一。");
            inputs.Add(item);
        }
        return inputs;
    }

    private static EvalSpecResultRecord EvaluateSingleSpec(
        MainLoopContext loop,
        EvalSpecInput input,
        string workspaceDir,
        string logDir,
        string defaultProfile,
        int? defaultBeam,
        int? defaultNodes,
        SolverPotionPolicy defaultPotionPolicy,
        int defaultBudgetMs,
        int defaultDop,
        int maxTurns,
        bool emitPostState = false,
        bool includePerTurn = true,
        string? resultType = null)
    {
        string effectiveProfile = input.Profile ?? defaultProfile;
        int? effectiveBeam = input.Beam ?? defaultBeam;
        int? effectiveNodes = input.Nodes ?? defaultNodes;
        SolverPotionPolicy effectivePotionPolicy = string.IsNullOrWhiteSpace(input.PotionPolicy)
            ? defaultPotionPolicy
            : Enum.Parse<SolverPotionPolicy>(input.PotionPolicy, ignoreCase: true);
        int effectiveBudgetMs = Math.Max(500, input.BudgetMs ?? defaultBudgetMs);
        int effectiveDop = Math.Max(1, input.Dop ?? defaultDop);

        HarnessOptions specOptions = new()
        {
            Profile = effectiveProfile,
            Beam = effectiveBeam,
            Nodes = effectiveNodes,
            MaxDegreeOfParallelism = effectiveDop,
            BudgetMilliseconds = effectiveBudgetMs,
            PotionPolicy = effectivePotionPolicy.ToString(),
            SearchMode = "Coordinator",
            WorkspaceDirectory = workspaceDir,
            OutputDirectory = logDir,
        };
        SolverSearchProfile resolvedProfile = ModRuntime.ResolveProfile(specOptions) with
        {
            SoftTimeBudgetMilliseconds = effectiveBudgetMs,
        };

        string tier = input.Tier ?? "spec";
        string group = input.Group ?? "eval_specs";
        string characterId = input.Setup?.CharacterId ?? "UNKNOWN";
        string encounterId = input.Setup?.EncounterId ?? "UNKNOWN";

        string? bossHpRelief = null;
        IDisposable? choiceScope = null;
        IDisposable? bundleHandle = null;
        Process proc = Process.GetCurrentProcess();
        TimeSpan cpuBefore = proc.TotalProcessorTime;
        int gen2Before = GC.CollectionCount(2);
        Stopwatch totalSw = Stopwatch.StartNew();

        try
        {
            CombatState combat;
            if (input.Setup != null)
            {
                combat = RealCombatDriver.StartCombat(loop, input.Setup);
            }
            else if (!string.IsNullOrWhiteSpace(input.BundlePath))
            {
                combat = PlayerBeatReplayRunner.RestoreBundleForBatch(
                    loop, input.BundlePath, out characterId, out encounterId, out bundleHandle);
            }
            else
            {
                UnattendedTestRequest request = GeneratedScenarioSetup.ReadRequest(input.RequestPath!);
                string evidenceDir = Path.Combine(logDir, "evidence");
                Directory.CreateDirectory(evidenceDir);
                GeneratedScenarioSetup setup = GeneratedScenarioSetup.Prepare(request, evidenceDir);
                characterId = setup.Request.CharacterId;
                encounterId = setup.Request.EncounterId;
                Task<IDisposable?> enter = setup.EnterCombatRoomAsync();
                loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), $"EvalSpecs Enter ({input.SpecId})");
                choiceScope = enter.GetAwaiter().GetResult();

                combat = OfflineCombat.WaitForPlayableCombat(loop);
                setup.CaptureOpening(combat);
                if (!setup.Request.PreserveNativeCombatStateForTest)
                {
                    loop.RunUntilCompleted(
                        setup.Session.InjectInitialStateAsync(combat, LocalContext.GetMe(combat)!),
                        TimeSpan.FromSeconds(30),
                        $"EvalSpecs Inject ({input.SpecId})");
                }
            }

            bossHpRelief = ActEndingBossPolicy.ResolveHpRelief(combat).ToString();
            double setupMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2);

            Player player = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException($"Spec '{input.SpecId}' 缺少本地玩家。");
            int startGold = input.Setup?.PlayerGold ?? player.Gold;
            string?[] initialPotionSlots = emitPostState
                ? player.PotionSlots.Select(static p => p?.Id.Entry).ToArray()
                : [];
            Dictionary<string, int> initialRelicCounters = emitPostState
                ? CaptureInitialRelicCounters(input.Setup, player)
                : [];
            Dictionary<int, Dictionary<string, int>> initialDeckScalars = emitPostState
                ? CaptureDeckPersistentScalars(player)
                : [];

            EvalResolvedCard[] resolvedDeck = player.Deck.Cards
                .Select(static c => new EvalResolvedCard(
                    c.Id.Entry,
                    c.CurrentUpgradeLevel,
                    c.Enchantment?.Id.Entry))
                .ToArray();
            string[] resolvedRelics = player.Relics
                .Select(static r => r.Id.Entry)
                .ToArray();
            string[] resolvedPotions = player.PotionSlots
                .Where(static p => p != null)
                .Select(static p => p!.Id.Entry)
                .ToArray();

            List<PlayerBeatReplayRunner.DeployReplanDiff> replanDiffs = [];
            List<EvalTurnTelemetry> perTurn = [];
            List<string> potionsUsedIds = [];
            List<(int Slot, string PotionId)> activePotionsUsed = [];

            FightOutcomeRecord outcome = SolveAndDeployWithTelemetry(
                loop, combat, input.SpecId, tier, group, characterId, encounterId,
                resolvedProfile, effectivePotionPolicy, effectiveDop, effectiveBudgetMs, maxTurns,
                replanDiffs, perTurn, potionsUsedIds,
                out bool turn1Exhausted, out bool allSolvesExhausted,
                out bool onlyDeathRoutes, out int? deathTurn,
                activePotionsUsed);

            EvalPostCombatState? postCombatState = emitPostState
                ? CapturePostCombatState(
                    combat, player, startGold, initialPotionSlots, activePotionsUsed,
                    initialRelicCounters, initialDeckScalars)
                : null;

            totalSw.Stop();
            double totalWallMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2);
            double cpuTimeMs = Math.Round((proc.TotalProcessorTime - cpuBefore).TotalMilliseconds, 2);
            double workingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 2);
            int gcGen2Delta = Math.Max(0, GC.CollectionCount(2) - gen2Before);
            double deployOverheadMs = Math.Round(Math.Max(0.0, totalWallMs - setupMs - outcome.ElapsedMs), 2);

            return new EvalSpecResultRecord
            {
                Type = resultType,
                SpecId = input.SpecId,
                Profile = effectiveProfile,
                PotionPolicy = effectivePotionPolicy.ToString(),
                Dop = effectiveDop,
                BudgetMs = effectiveBudgetMs,
                Metadata = input.Metadata,
                ResolvedDeck = resolvedDeck,
                ResolvedRelics = resolvedRelics,
                ResolvedPotions = resolvedPotions,
                Outcome = outcome with { BossHpRelief = bossHpRelief },
                Turn1WasLosslesslyExhausted = turn1Exhausted,
                WasLosslesslyExhausted = allSolvesExhausted,
                OnlyDeathRoutesFound = onlyDeathRoutes,
                DeathTurn = deathTurn,
                PotionsUsedIds = potionsUsedIds.ToArray(),
                DeployReplans = replanDiffs.ToArray(),
                PerTurn = includePerTurn ? perTurn.ToArray() : [],
                PostCombatState = postCombatState,
                Timing = new EvalTimingTelemetry(
                    SetupMs: setupMs,
                    SolveMs: outcome.ElapsedMs,
                    DeployOverheadMs: deployOverheadMs,
                    TotalWallMs: totalWallMs,
                    CpuTimeMs: cpuTimeMs,
                    WorkingSetMb: workingSetMb,
                    GcGen2Delta: gcGen2Delta),
            };
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            double totalWallMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2);
            double cpuTimeMs = Math.Round((proc.TotalProcessorTime - cpuBefore).TotalMilliseconds, 2);
            double workingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 2);
            int gcGen2Delta = Math.Max(0, GC.CollectionCount(2) - gen2Before);

            return new EvalSpecResultRecord
            {
                Type = resultType,
                SpecId = input.SpecId,
                Profile = effectiveProfile,
                PotionPolicy = effectivePotionPolicy.ToString(),
                Dop = effectiveDop,
                BudgetMs = effectiveBudgetMs,
                Metadata = input.Metadata,
                Outcome = new FightOutcomeRecord
                {
                    BossHpRelief = bossHpRelief,
                    FightId = input.SpecId,
                    Tier = tier,
                    Group = group,
                    CharacterId = characterId,
                    EncounterId = encounterId,
                    Won = false,
                    Survives = false,
                    Boundary = "Error",
                    HardErrors = 1,
                    HardError = $"{ex.GetType().Name}: {ex.Message}",
                },
                Timing = new EvalTimingTelemetry(
                    SetupMs: totalWallMs,
                    SolveMs: 0.0,
                    DeployOverheadMs: 0.0,
                    TotalWallMs: totalWallMs,
                    CpuTimeMs: cpuTimeMs,
                    WorkingSetMb: workingSetMb,
                    GcGen2Delta: gcGen2Delta),
            };
        }
        finally
        {
            choiceScope?.Dispose();
            bundleHandle?.Dispose();
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    private static int NormalizePeriodicCounter(int value, int period)
    {
        int rem = value % period;
        return rem < 0 ? rem + period : rem;
    }

    private static Dictionary<string, int> CaptureInitialRelicCounters(CombatSetupSpec? setup, Player player)
    {
        Dictionary<string, int> baseline = new(StringComparer.Ordinal);
        if (setup?.RelicSpecs != null)
        {
            foreach (RelicInjectionSpec spec in setup.RelicSpecs)
            {
                if (spec.IntegerMembers == null)
                    continue;
                string rid = spec.RelicId.Trim().ToUpperInvariant();
                foreach ((string member, int value) in spec.IntegerMembers)
                    baseline[$"{rid}.{member}"] = value;
            }
        }
        if (setup?.WuppyCurrentHp is { } wuppyHp)
            baseline["WUPPY.CurrentHp"] = wuppyHp;
        if (setup?.WuppyMaxHp is { } wuppyMaxHp)
            baseline["WUPPY.MaxHp"] = wuppyMaxHp;

        foreach (RelicModel relic in player.Relics)
        {
            if (relic is MegaCrit.Sts2.Core.Models.Relics.Wuppy wuppy)
            {
                baseline.TryAdd("WUPPY.CurrentHp", wuppy.CurrentHp);
                baseline.TryAdd("WUPPY.MaxHp", wuppy.MaxHp);
            }
            else if (relic is MegaCrit.Sts2.Core.Models.Relics.Byrdpip byrdpip)
            {
                baseline.TryAdd("BYRDPIP.SwoopsPlayed", byrdpip.SwoopsPlayed);
            }
        }
        return baseline;
    }

    private static Dictionary<int, Dictionary<string, int>> CaptureDeckPersistentScalars(Player player)
    {
        Dictionary<int, Dictionary<string, int>> result = [];
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int i = 0; i < cards.Count; i++)
        {
            switch (cards[i])
            {
                case MegaCrit.Sts2.Core.Models.Cards.GeneticAlgorithm ga:
                    result[i] = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["CurrentBlock"] = ga.CurrentBlock,
                        ["IncreasedBlock"] = ga.IncreasedBlock,
                    };
                    break;
                case MegaCrit.Sts2.Core.Models.Cards.TheScythe scythe:
                    result[i] = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["CurrentDamage"] = scythe.CurrentDamage,
                        ["IncreasedDamage"] = scythe.IncreasedDamage,
                    };
                    break;
                case MegaCrit.Sts2.Core.Models.Cards.ShowOff showOff:
                    result[i] = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["CurrentAim"] = showOff.CurrentAim,
                        ["IncreasedAim"] = showOff.IncreasedAim,
                    };
                    break;
            }
        }
        return result;
    }

    private static EvalPostCombatState CapturePostCombatState(
        CombatState combat,
        Player player,
        int startGold,
        string?[] initialPotionSlots,
        List<(int Slot, string PotionId)> activePotionsUsed,
        Dictionary<string, int> initialRelicCounters,
        Dictionary<int, Dictionary<string, int>> initialDeckScalars)
    {
        HotCombatOutcome liveOutcome = RealCombatDriver.GetLiveOutcome(combat);
        string outcomeLabel = liveOutcome switch
        {
            HotCombatOutcome.Victory => "Victory",
            HotCombatOutcome.Defeat => "Defeat",
            HotCombatOutcome.EventDefeat => "EventDefeat",
            _ => "Timeout",
        };

        string?[] potionsAfter = player.PotionSlots
            .Select(static p => p?.Id.Entry)
            .ToArray();

        List<EvalPostPotionConsumed> consumed = [];
        HashSet<int> activelyConsumedSlots = [];
        foreach ((int slot, string potionId) in activePotionsUsed)
        {
            consumed.Add(new EvalPostPotionConsumed(slot, potionId, Passive: false));
            activelyConsumedSlots.Add(slot);
        }

        List<string> gained = [];
        int maxSlots = Math.Max(initialPotionSlots.Length, potionsAfter.Length);
        for (int i = 0; i < maxSlots; i++)
        {
            string? before = i < initialPotionSlots.Length ? initialPotionSlots[i] : null;
            string? after = i < potionsAfter.Length ? potionsAfter[i] : null;
            bool wasActive = activelyConsumedSlots.Contains(i);
            bool wasPassive = false;

            if (before != null && !wasActive && !string.Equals(before, after, StringComparison.Ordinal))
            {
                consumed.Add(new EvalPostPotionConsumed(i, before, Passive: true));
                wasPassive = true;
            }

            if (after != null && (before == null || wasActive || wasPassive || !string.Equals(before, after, StringComparison.Ordinal)))
            {
                gained.Add(after);
            }
        }

        List<EvalPostRelicState> relicStates = [];
        Dictionary<string, int> relicDeltas = new(StringComparer.Ordinal);
        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;

            Dictionary<string, int>? ints = null;
            Dictionary<string, bool>? bools = null;
            switch (relic)
            {
                case MegaCrit.Sts2.Core.Models.Relics.Nunchaku r:
                    ints = new(StringComparer.Ordinal) { ["AttacksPlayed"] = NormalizePeriodicCounter(r.AttacksPlayed, 10) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.PenNib r:
                    ints = new(StringComparer.Ordinal) { ["AttacksPlayed"] = NormalizePeriodicCounter(r.AttacksPlayed, 10) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.HappyFlower r:
                    ints = new(StringComparer.Ordinal) { ["TurnsSeen"] = NormalizePeriodicCounter(r.TurnsSeen, 3) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.FakeHappyFlower r:
                    ints = new(StringComparer.Ordinal) { ["TurnsSeen"] = NormalizePeriodicCounter(r.TurnsSeen, 5) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.Pendulum r:
                    ints = new(StringComparer.Ordinal) { ["TurnsSeen"] = NormalizePeriodicCounter(r.TurnsSeen, 3) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.PollinousCore r:
                    ints = new(StringComparer.Ordinal) { ["TurnsSeen"] = NormalizePeriodicCounter(r.TurnsSeen, 4) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.TuningFork r:
                    ints = new(StringComparer.Ordinal) { ["SkillsPlayed"] = NormalizePeriodicCounter(r.SkillsPlayed, 10) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.JossPaper r:
                    ints = new(StringComparer.Ordinal) { ["CardsExhausted"] = NormalizePeriodicCounter(r.CardsExhausted, 5) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.IronClub r:
                    ints = new(StringComparer.Ordinal) { ["CardsPlayed"] = NormalizePeriodicCounter(r.CardsPlayed, 4) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.GalacticDust r:
                    ints = new(StringComparer.Ordinal) { ["StarsSpent"] = NormalizePeriodicCounter(r.StarsSpent, 10) };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.LizardTail r:
                    bools = new(StringComparer.Ordinal) { ["WasUsed"] = r.WasUsed };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.VenerableTeaSet r:
                    bools = new(StringComparer.Ordinal) { ["GainEnergyInNextCombat"] = r.GainEnergyInNextCombat };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.FakeVenerableTeaSet r:
                    bools = new(StringComparer.Ordinal) { ["GainEnergyInNextCombat"] = r.GainEnergyInNextCombat };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.Wuppy r:
                    ints = new(StringComparer.Ordinal)
                    {
                        ["CurrentHp"] = r.CurrentHp,
                        ["MaxHp"] = r.MaxHp,
                    };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.Byrdpip r:
                    ints = new(StringComparer.Ordinal) { ["SwoopsPlayed"] = r.SwoopsPlayed };
                    break;
                case MegaCrit.Sts2.Core.Models.Relics.LavaLamp r:
                    bools = new(StringComparer.Ordinal) { ["TookDamageThisCombat"] = r.TookDamageThisCombat };
                    break;
            }

            if (ints != null || bools != null)
            {
                string rid = relic.Id.Entry;
                relicStates.Add(new EvalPostRelicState(rid, ints, bools));
                if (ints != null)
                {
                    foreach ((string member, int afterVal) in ints)
                    {
                        string key = $"{rid}.{member}";
                        int beforeVal = initialRelicCounters.GetValueOrDefault(key, 0);
                        int delta = afterVal - beforeVal;
                        if (delta != 0)
                            relicDeltas[key] = delta;
                    }
                }
            }
        }

        List<EvalDeckMutation> deckMutations = [];
        Dictionary<int, Dictionary<string, int>> finalDeckScalars = CaptureDeckPersistentScalars(player);
        foreach ((int idx, Dictionary<string, int> afterMap) in finalDeckScalars)
        {
            if (idx >= player.Deck.Cards.Count)
                continue;
            initialDeckScalars.TryGetValue(idx, out Dictionary<string, int>? beforeMap);
            bool changed = beforeMap == null
                || afterMap.Any(kv => !beforeMap.TryGetValue(kv.Key, out int prev) || prev != kv.Value);
            if (changed)
            {
                deckMutations.Add(new EvalDeckMutation(
                    idx,
                    player.Deck.Cards[idx].Id.Entry,
                    afterMap));
            }
        }

        int extraCardRewards = 0;
        if (combat.RunState.CurrentRoom is MegaCrit.Sts2.Core.Rooms.CombatRoom activeRoom
            && activeRoom.ExtraRewards.TryGetValue(player, out List<MegaCrit.Sts2.Core.Rewards.Reward>? extraRewards))
        {
            extraCardRewards = extraRewards.Count(static r => r is MegaCrit.Sts2.Core.Rewards.CardReward);
        }

        return new EvalPostCombatState(
            CombatOutcome: outcomeLabel,
            HpAfter: player.Creature.CurrentHp,
            MaxHpAfter: player.Creature.MaxHp,
            GoldDelta: player.Gold - startGold,
            PotionsAfter: potionsAfter,
            PotionsConsumed: consumed.ToArray(),
            PotionsGained: gained.ToArray(),
            RelicStatesAfter: relicStates.ToArray(),
            RelicCounterDeltas: relicDeltas,
            DeckMutations: deckMutations.ToArray(),
            ExtraCardRewards: extraCardRewards);
    }

    private sealed class TurnAccumulator
    {
        public int Turn;
        public bool ReplannedOnTurn;
        public int PlayerHpStart;
        public int EnergyStart;
        public int HandSizeStart;
        public int HandSizeEnd;
        public int DrawPileStart;
        public int DrawPileEnd;
        public int DiscardPileEnd;
        public int ExhaustPileStart;
        public int ExhaustPileEnd;
        public int HandEndStatusCount;
        public int HandEndCurseCount;
        public int HandEndAfflictedCount;
        public int HandEndPlayableLeft;
        public int DeckStatusCurseEnd;
        public int EnemyTotalHpStart;
        public int EnemyTotalHpPlayerTurnEnd;
        public int EnemyTotalBlockPlayerTurnEnd;
        public int EnemyAliveStart;
        public int CardsPlayed;
        public int AttackCardsPlayed;
        public int SkillCardsPlayed;
        public int PowerCardsPlayed;
        public int StatusCardsPlayed;
        public int CurseCardsPlayed;
        public int AfflictedCardsPlayed;
        public Dictionary<string, int> AfflictionsPlayed = new(StringComparer.Ordinal);
        public List<string> CardsPlayedIds = [];
        public int PotionsUsedLive;
        public List<string> PotionIdsUsed = [];
        public int PlayerBlockEnd;
        public int EnergyLeftLive;
        public Dictionary<string, int> PlayerPowersEnd = [];
        public EvalEnemySnapshot[] EnemiesPlayerTurnEnd = [];
        public List<string> Kills = [];
        public int? HpLost;
        public int? HpRecovered;
        public int? SoldHp;
        public int? EnemyHpLost;
        public int? MaxBlock;
        public int? ActualBlock;
        public int? EnergyLeft;
        public int? PotionCount;
        public int? PotionStrategicCost;
    }

    private static FightOutcomeRecord SolveAndDeployWithTelemetry(
        MainLoopContext loop,
        CombatState combat,
        string fightId,
        string tier,
        string group,
        string characterId,
        string encounterId,
        SolverSearchProfile resolvedProfile,
        SolverPotionPolicy potionPolicy,
        int dop,
        int budgetMs,
        int maxTurns,
        List<PlayerBeatReplayRunner.DeployReplanDiff> replanDiffs,
        List<EvalTurnTelemetry> perTurn,
        List<string> potionsUsedIds,
        out bool turn1WasLosslesslyExhausted,
        out bool allSolvesLosslesslyExhausted,
        out bool lastOnlyDeathRoutesFound,
        out int? lastDeathTurn,
        List<(int Slot, string PotionId)>? activePotionsUsed = null)
    {
        Player player = LocalContext.GetMe(combat)!;
        int startHp = player.Creature.CurrentHp;
        int startMaxHp = player.Creature.MaxHp;
        int initialUnusedLizardTails = player.Relics
            .OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>()
            .Count(static t => !t.WasUsed);
        int initialFairyCount = player.PotionSlots
            .Count(static p => p != null && string.Equals(p.Id.Entry, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));

        PlayerBeatReplayRunner.RootCanonicalContext rootContext = PlayerBeatReplayRunner.RootCanonicalContext.Empty;
        short[] rootSlotCosts = [];
        HotSearchPolicy rootHotPolicy = default;
        bool hasRootHotPolicy = false;
        ushort rootOccupiedMask = 0;
        ushort rootConsumedMask = 0;
        int rootOptionalPaidRawHpCost = 0;
        int rootOptionalPaidCount = 0;
        int alchemizeCount = 0;
        int fightPotionsUsed = 0;
        double fightSolveMs = 0.0;
        int fightSolveCount = 0;
        long fightExpandedNodes = 0;
        long fightTransitions = 0;
        string fightLastBoundary = "None";
        int unplannedReplans = 0;
        int choiceMismatches = 0;
        SolverTheftPolicy? fightTheftPolicy = null;
        string? firstRouteSignature = null;
        int currentTurn = player.PlayerCombatState!.TurnNumber;

        turn1WasLosslesslyExhausted = false;
        allSolvesLosslesslyExhausted = true;
        lastOnlyDeathRoutesFound = false;
        lastDeathTurn = null;

        SolverResult? activePlan = null;
        int grossHpLoss = 0;
        int observedDeathSaveUses = 0;
        int observedDeathSaveHpRestored = 0;
        void OnPlayerHpChanged(int oldHp, int newHp)
        {
            if (newHp < oldHp)
            {
                grossHpLoss += oldHp - newHp;
            }
            else if (oldHp == 0 && newHp > 0 && CombatManager.Instance.IsInProgress)
            {
                observedDeathSaveUses++;
                observedDeathSaveHpRestored += newHp;
            }
        }

        EnsureLethalDamageObserverPatched();

        TurnAccumulator? activeTurnAcc = null;
        CombatHistory combatHistory = CombatManager.Instance.History;
        List<BufferedHistoryEvent> bufferedHistory = [];
        HashSet<DamageResult> observedDamageResults = new(ReferenceEqualityComparer.Instance);
        int liveHistoryCursor = 0;
        int commitHistoryCursor = 0;

        void DrainLiveHistoryToBuffer()
        {
            IReadOnlyList<CombatHistoryEntry> entries = combatHistory.Entries as IReadOnlyList<CombatHistoryEntry>
                ?? combatHistory.Entries.ToList();
            int count = entries.Count;
            if (count < liveHistoryCursor)
            {
                // CombatManager.EndCombatInternal 在战斗结束时会调 History.Clear()；
                // 已缓冲的条目保留在 bufferedHistory 中，不会丢失致死回合事件。
                liveHistoryCursor = 0;
            }
            for (int i = liveHistoryCursor; i < count; i++)
            {
                CombatHistoryEntry entry = entries[i];
                if (entry is DamageReceivedEntry dmgEntry)
                    observedDamageResults.Add(dmgEntry.Result);
                int entryTurn = entry._playerTurnNumbers.TryGetValue(player.NetId, out int recordedTurn)
                    ? recordedTurn
                    : entry.RoundNumber;
                bool isSlyAutoPlay = entry is CardPlayStartedEntry cps
                    && cps.CardPlay.Player == player
                    && cps.CardPlay.IsAutoPlay
                    && cps.CardPlay.IsFirstInSeries
                    && cps.CardPlay.Card.IsSlyThisTurn;
                bufferedHistory.Add(new BufferedHistoryEvent(entry, entryTurn, isSlyAutoPlay));
            }
            liveHistoryCursor = count;
        }

        // 原生 CreatureCmd.cs:341 在最后一名敌方被 ApplyDamageResults 击杀后因 IsEnding==true 跳过写入 History.DamageReceived，
        // 而第 437 行 Hook.AfterDamageGiven 仍会执行。此处以 DamageResult 引用去重，纯只读补录被引擎跳过的致死一击。
        void ObserveMissedDamage(ICombatState cs, Creature? dealer, DamageResult result, CardModel? cardSource)
        {
            if (!cs.IsLiveCombat())
                return;
            DrainLiveHistoryToBuffer();
            if (!observedDamageResults.Add(result))
                return;
            int entryTurn = player.PlayerCombatState?.TurnNumber ?? cs.RoundNumber;
            DamageReceivedEntry synthetic = new(
                result, result.Receiver, dealer, cardSource,
                cs.RoundNumber, cs.CurrentSide, combatHistory, cs.Players);
            bufferedHistory.Add(new BufferedHistoryEvent(synthetic, entryTurn, IsSlyAutoPlay: false));
        }

        EvalTurnEventTelemetry DrainTurnEvents(int turnNumber)
        {
            DrainLiveHistoryToBuffer();

            int enemyDmgCardAttackHp = 0;
            int enemyDmgCardAttackBlocked = 0;
            int enemyDmgCardAttackHits = 0;
            int enemyDmgCardOtherHp = 0;
            int enemyDmgCardOtherBlocked = 0;
            int enemyDmgPlayerSidePowerHp = 0;
            int enemyDmgEnemySideUnblockableHp = 0;
            int enemyDmgEnemySideBlockableHp = 0;
            int enemyDmgOverkill = 0;
            int enemyDmgMaxSingleHit = 0;
            Dictionary<uint, int>? enemyDmgByCombatId = null;
            Dictionary<Creature, int>? enemyDmgFallback = null;
            int playerIncomingRawDmg = 0;
            int playerDmgBlocked = 0;
            int playerHpDmgTaken = 0;
            int playerUnblockableHpDmg = 0;
            int playerAttackHitsTaken = 0;
            int playerBlockGained = 0;
            int playerBlockFromCards = 0;
            int playerBlockFromNonCard = 0;
            int enemyBlockGained = 0;
            int poisonApplied = 0;
            int weakApplied = 0;
            int vulnApplied = 0;
            int strDownApplied = 0;
            int cardsDrawnInitial = 0;
            int cardsDrawnExtra = 0;
            int cardsDiscarded = 0;
            int cardsExhausted = 0;
            int cardsGenerated = 0;
            int slyAutoPlays = 0;
            int zeroCostCardsPlayed = 0;
            int shivsPlayed = 0;
            int energySpentTotal = 0;

            while (commitHistoryCursor < bufferedHistory.Count
                && bufferedHistory[commitHistoryCursor].TurnNumber <= turnNumber)
            {
                BufferedHistoryEvent ev = bufferedHistory[commitHistoryCursor++];
                switch (ev.Entry)
                {
                    case DamageReceivedEntry dmg:
                    {
                        DamageResult r = dmg.Result;
                        if (dmg.Receiver.Side == CombatSide.Enemy)
                        {
                            enemyDmgOverkill += r.OverkillDamage;
                            int hitTotalDmg = r.TotalDamage;
                            if (hitTotalDmg > 0)
                            {
                                if (hitTotalDmg > enemyDmgMaxSingleHit)
                                    enemyDmgMaxSingleHit = hitTotalDmg;
                                if (dmg.Receiver.CombatId is uint enemyCombatId)
                                {
                                    enemyDmgByCombatId ??= [];
                                    enemyDmgByCombatId[enemyCombatId] =
                                        enemyDmgByCombatId.GetValueOrDefault(enemyCombatId) + hitTotalDmg;
                                }
                                else
                                {
                                    enemyDmgFallback ??= new Dictionary<Creature, int>(ReferenceEqualityComparer.Instance);
                                    enemyDmgFallback[dmg.Receiver] =
                                        enemyDmgFallback.GetValueOrDefault(dmg.Receiver) + hitTotalDmg;
                                }
                            }
                            if (dmg.CardSource != null)
                            {
                                if (r.Props.IsPoweredAttack())
                                {
                                    enemyDmgCardAttackHp += r.UnblockedDamage;
                                    enemyDmgCardAttackBlocked += r.BlockedDamage;
                                    enemyDmgCardAttackHits++;
                                }
                                else
                                {
                                    enemyDmgCardOtherHp += r.UnblockedDamage;
                                    enemyDmgCardOtherBlocked += r.BlockedDamage;
                                }
                            }
                            else if (dmg.CurrentSide == CombatSide.Player)
                            {
                                enemyDmgPlayerSidePowerHp += r.UnblockedDamage;
                            }
                            else if (r.Props.HasFlag(ValueProp.Unblockable))
                            {
                                enemyDmgEnemySideUnblockableHp += r.UnblockedDamage;
                            }
                            else
                            {
                                enemyDmgEnemySideBlockableHp += r.UnblockedDamage;
                            }
                        }
                        else if (dmg.Receiver == player.Creature)
                        {
                            playerIncomingRawDmg += r.BlockedDamage + r.UnblockedDamage + r.OverkillDamage;
                            playerDmgBlocked += r.BlockedDamage;
                            playerHpDmgTaken += r.UnblockedDamage;
                            if (r.Props.HasFlag(ValueProp.Unblockable))
                                playerUnblockableHpDmg += r.UnblockedDamage;
                            if (r.Props.IsPoweredAttack())
                                playerAttackHitsTaken++;
                        }
                        break;
                    }
                    case BlockGainedEntry blk:
                        if (blk.Receiver == player.Creature)
                        {
                            playerBlockGained += blk.Amount;
                            if (blk.CardPlay != null)
                                playerBlockFromCards += blk.Amount;
                            else
                                playerBlockFromNonCard += blk.Amount;
                        }
                        else if (blk.Receiver.Side == CombatSide.Enemy)
                        {
                            enemyBlockGained += blk.Amount;
                        }
                        break;
                    // PowerCmd.Apply / ModifyAmount 均在 Hook.ModifyPowerAmountReceived（含 ArtifactPower 拦截归零）后
                    // 才记录 History.PowerReceived，因此此处严格统计实际落地层数；
                    // TemporaryStrengthPower（如 PiercingWailPower/DarkShacklesPower/ShacklingPotionPower）在 BeforeApplied /
                    // AfterPowerAmountChanged 中会自动调用 PowerCmd.Apply<StrengthPower>(-amount, applier)，
                    // 故仅统计敌方来自玩家侧的负 StrengthPower 即可无重无漏同时覆盖永久（Malaise）与临时降攻。
                    case PowerReceivedEntry pwr when pwr.Power.Owner.Side == CombatSide.Enemy:
                        if (pwr.Power is PoisonPower && pwr.Amount > 0m)
                            poisonApplied += (int)pwr.Amount;
                        else if (pwr.Power is WeakPower && pwr.Amount > 0m)
                            weakApplied += (int)pwr.Amount;
                        else if (pwr.Power is VulnerablePower && pwr.Amount > 0m)
                            vulnApplied += (int)pwr.Amount;
                        else if (pwr.Power is StrengthPower
                            && pwr.Amount < 0m
                            && (pwr.Applier == null || pwr.Applier.Side == CombatSide.Player))
                            strDownApplied += (int)(-pwr.Amount);
                        break;
                    case CardDrawnEntry drawn when drawn.Actor == player.Creature:
                        if (drawn.FromHandDraw)
                            cardsDrawnInitial++;
                        else
                            cardsDrawnExtra++;
                        break;
                    case CardDiscardedEntry disc when disc.Actor == player.Creature:
                        cardsDiscarded++;
                        break;
                    case CardExhaustedEntry exh when exh.Actor == player.Creature:
                        cardsExhausted++;
                        break;
                    case CardGeneratedEntry gen when gen.Actor == player.Creature:
                        cardsGenerated++;
                        break;
                    case CardPlayStartedEntry cps when cps.CardPlay.Player == player:
                        if (ev.IsSlyAutoPlay)
                            slyAutoPlays++;
                        if (cps.CardPlay.IsFirstInSeries)
                        {
                            if (cps.CardPlay.Resources.EnergySpent == 0)
                                zeroCostCardsPlayed++;
                            if (cps.CardPlay.Card.Tags.Contains(CardTag.Shiv))
                                shivsPlayed++;
                        }
                        break;
                    case EnergySpentEntry energy when energy.Actor == player.Creature:
                        energySpentTotal += energy.Amount;
                        break;
                }
            }

            int distinctEnemiesDamaged = (enemyDmgByCombatId?.Count ?? 0) + (enemyDmgFallback?.Count ?? 0);
            int enemyDmgMaxSingleTarget = 0;
            if (enemyDmgByCombatId != null)
            {
                foreach (int total in enemyDmgByCombatId.Values)
                {
                    if (total > enemyDmgMaxSingleTarget)
                        enemyDmgMaxSingleTarget = total;
                }
            }
            if (enemyDmgFallback != null)
            {
                foreach (int total in enemyDmgFallback.Values)
                {
                    if (total > enemyDmgMaxSingleTarget)
                        enemyDmgMaxSingleTarget = total;
                }
            }

            return new EvalTurnEventTelemetry(
                EnemyDmgCardAttackHp: enemyDmgCardAttackHp,
                EnemyDmgCardAttackBlocked: enemyDmgCardAttackBlocked,
                EnemyDmgCardAttackHits: enemyDmgCardAttackHits,
                EnemyDmgCardOtherHp: enemyDmgCardOtherHp,
                EnemyDmgCardOtherBlocked: enemyDmgCardOtherBlocked,
                EnemyDmgPlayerSidePowerHp: enemyDmgPlayerSidePowerHp,
                EnemyDmgEnemySideUnblockableHp: enemyDmgEnemySideUnblockableHp,
                EnemyDmgEnemySideBlockableHp: enemyDmgEnemySideBlockableHp,
                EnemyDmgOverkill: enemyDmgOverkill,
                DistinctEnemiesDamaged: distinctEnemiesDamaged,
                EnemyDmgMaxSingleTarget: enemyDmgMaxSingleTarget,
                EnemyDmgMaxSingleHit: enemyDmgMaxSingleHit,
                PlayerIncomingRawDmg: playerIncomingRawDmg,
                PlayerDmgBlocked: playerDmgBlocked,
                PlayerHpDmgTaken: playerHpDmgTaken,
                PlayerUnblockableHpDmg: playerUnblockableHpDmg,
                PlayerAttackHitsTaken: playerAttackHitsTaken,
                PlayerBlockGained: playerBlockGained,
                PlayerBlockFromCards: playerBlockFromCards,
                PlayerBlockFromNonCard: playerBlockFromNonCard,
                EnemyBlockGained: enemyBlockGained,
                PoisonApplied: poisonApplied,
                WeakApplied: weakApplied,
                VulnApplied: vulnApplied,
                StrDownApplied: strDownApplied,
                CardsDrawnInitial: cardsDrawnInitial,
                CardsDrawnExtra: cardsDrawnExtra,
                CardsDiscarded: cardsDiscarded,
                CardsExhausted: cardsExhausted,
                CardsGenerated: cardsGenerated,
                SlyAutoPlays: slyAutoPlays,
                ZeroCostCardsPlayed: zeroCostCardsPlayed,
                ShivsPlayed: shivsPlayed,
                EnergySpentTotal: energySpentTotal);
        }

        static int? TryLookup(IReadOnlyDictionary<int, int> dict, int turn)
            => dict.TryGetValue(turn, out int val) ? val : null;

        static Dictionary<string, int> SnapshotCreaturePowers(Creature c)
            => c.Powers
                .GroupBy(static pw => pw.Id.Entry, StringComparer.Ordinal)
                .ToDictionary(static g => g.Key, static g => g.Sum(static pw => pw.Amount), StringComparer.Ordinal);

        static EvalEnemySnapshot[] SnapshotEnemies(CombatState cs)
            => cs.Enemies.Select(static e => new EvalEnemySnapshot(
                e.Monster?.Id.Entry ?? "UNKNOWN",
                e.CurrentHp,
                e.MaxHp,
                e.Block,
                e.IsAlive,
                SnapshotCreaturePowers(e))).ToArray();

        static int CountStatusOrCurse(IReadOnlyList<CardModel> cards)
        {
            int count = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Type is CardType.Status or CardType.Curse)
                    count++;
            }
            return count;
        }

        // CardModel.CanPlay() 仅校验 Unplayable、能量/星能、AnyAlly、Hook.ShouldPlay 及 IsPlayable，
        // 全链路纯只读无状态副作用；对 AnyEnemy 补充校验存在可合法指定的在场敌方目标。
        static bool IsHandCardPlayableReadOnly(CardModel card, CombatState cs)
        {
            if (!card.CanPlay())
                return false;
            if (card.TargetType == TargetType.AnyEnemy)
                return cs.HittableEnemies.Any(e => card.IsValidTarget(e));
            return true;
        }

        static void CapturePlayerTurnEndSnapshot(TurnAccumulator acc, CombatState cs, Player p)
        {
            acc.EnemiesPlayerTurnEnd = SnapshotEnemies(cs);
            acc.EnemyTotalHpPlayerTurnEnd = cs.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp);
            acc.EnemyTotalBlockPlayerTurnEnd = cs.Enemies.Where(static e => e.IsAlive).Sum(static e => e.Block);
            if (p.PlayerCombatState is { } pcs)
            {
                acc.EnergyLeftLive = pcs.Energy;
                if (CombatManager.Instance.IsInProgress)
                {
                    acc.PlayerBlockEnd = p.Creature.Block;
                    IReadOnlyList<CardModel> handCards = pcs.Hand.Cards;
                    acc.HandSizeEnd = handCards.Count;
                    int handStatus = 0;
                    int handCurse = 0;
                    int handAfflicted = 0;
                    int handPlayable = 0;
                    for (int i = 0; i < handCards.Count; i++)
                    {
                        CardModel c = handCards[i];
                        if (c.Type == CardType.Status)
                            handStatus++;
                        else if (c.Type == CardType.Curse)
                            handCurse++;
                        if (c.Affliction != null)
                            handAfflicted++;
                        if (IsHandCardPlayableReadOnly(c, cs))
                            handPlayable++;
                    }
                    acc.HandEndStatusCount = handStatus;
                    acc.HandEndCurseCount = handCurse;
                    acc.HandEndAfflictedCount = handAfflicted;
                    acc.HandEndPlayableLeft = handPlayable;
                    acc.DrawPileEnd = pcs.DrawPile.Cards.Count;
                    acc.DiscardPileEnd = pcs.DiscardPile.Cards.Count;
                    acc.ExhaustPileEnd = pcs.ExhaustPile.Cards.Count;
                    acc.DeckStatusCurseEnd = handStatus + handCurse
                        + CountStatusOrCurse(pcs.DrawPile.Cards)
                        + CountStatusOrCurse(pcs.DiscardPile.Cards);
                    acc.PlayerPowersEnd = SnapshotCreaturePowers(p.Creature);
                }
            }
        }

        void CommitTurnAccumulator(TurnAccumulator acc, CombatState cs, Player p, List<EvalTurnTelemetry> dest)
        {
            EvalTurnEventTelemetry events = DrainTurnEvents(acc.Turn);
            dest.Add(new EvalTurnTelemetry(
                Turn: acc.Turn,
                ReplannedOnTurn: acc.ReplannedOnTurn,
                PlayerHpStart: acc.PlayerHpStart,
                PlayerHpEnd: p.Creature.CurrentHp,
                EnergyStart: acc.EnergyStart,
                EnergyLeftLive: acc.EnergyLeftLive,
                HandSizeStart: acc.HandSizeStart,
                HandSizeEnd: acc.HandSizeEnd,
                DrawPileStart: acc.DrawPileStart,
                DrawPileEnd: acc.DrawPileEnd,
                DiscardPileEnd: acc.DiscardPileEnd,
                ExhaustPileStart: acc.ExhaustPileStart,
                ExhaustPileEnd: acc.ExhaustPileEnd,
                HandEndStatusCount: acc.HandEndStatusCount,
                HandEndCurseCount: acc.HandEndCurseCount,
                HandEndAfflictedCount: acc.HandEndAfflictedCount,
                HandEndPlayableLeft: acc.HandEndPlayableLeft,
                DeckStatusCurseEnd: acc.DeckStatusCurseEnd,
                CardsPlayed: acc.CardsPlayed,
                AttackCardsPlayed: acc.AttackCardsPlayed,
                SkillCardsPlayed: acc.SkillCardsPlayed,
                PowerCardsPlayed: acc.PowerCardsPlayed,
                StatusCardsPlayed: acc.StatusCardsPlayed,
                CurseCardsPlayed: acc.CurseCardsPlayed,
                AfflictedCardsPlayed: acc.AfflictedCardsPlayed,
                AfflictionsPlayed: new Dictionary<string, int>(acc.AfflictionsPlayed, StringComparer.Ordinal),
                CardsPlayedIds: acc.CardsPlayedIds.ToArray(),
                PotionsUsedLive: acc.PotionsUsedLive,
                PotionIdsUsed: acc.PotionIdsUsed.ToArray(),
                PlayerBlockEnd: acc.PlayerBlockEnd,
                EnemyTotalHpStart: acc.EnemyTotalHpStart,
                EnemyTotalHpPlayerTurnEnd: acc.EnemyTotalHpPlayerTurnEnd,
                EnemyTotalBlockPlayerTurnEnd: acc.EnemyTotalBlockPlayerTurnEnd,
                EnemyTotalHpEnd: cs.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp),
                EnemyAliveStart: acc.EnemyAliveStart,
                EnemyAliveEnd: cs.Enemies.Count(static e => e.IsAlive),
                HpLost: acc.HpLost,
                HpRecovered: acc.HpRecovered,
                SoldHp: acc.SoldHp,
                EnemyHpLost: acc.EnemyHpLost,
                MaxBlock: acc.MaxBlock,
                ActualBlock: acc.ActualBlock,
                EnergyLeft: acc.EnergyLeft,
                PotionCount: acc.PotionCount,
                PotionStrategicCost: acc.PotionStrategicCost,
                Kills: acc.Kills.ToArray(),
                PlayerPowersEnd: acc.PlayerPowersEnd,
                EnemiesPlayerTurnEnd: acc.EnemiesPlayerTurnEnd,
                EnemiesEnd: SnapshotEnemies(cs),
                Events: events));
        }

        DrainLiveHistoryToBuffer();
        _activeDamageObserver = ObserveMissedDamage;
        combatHistory.Changed += DrainLiveHistoryToBuffer;
        player.Creature.CurrentHpChanged += OnPlayerHpChanged;
        try
        {
            for (int turnLoop = 0; turnLoop < maxTurns; turnLoop++)
            {
                if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                    break;

                currentTurn = player.PlayerCombatState!.TurnNumber;
                if (activeTurnAcc == null || activeTurnAcc.Turn != currentTurn)
                {
                    if (activeTurnAcc != null)
                        CommitTurnAccumulator(activeTurnAcc, combat, player, perTurn);
                    activeTurnAcc = new TurnAccumulator
                    {
                        Turn = currentTurn,
                        PlayerHpStart = player.Creature.CurrentHp,
                        EnergyStart = player.PlayerCombatState.Energy,
                        HandSizeStart = player.PlayerCombatState.Hand.Cards.Count,
                        DrawPileStart = player.PlayerCombatState.DrawPile.Cards.Count,
                        ExhaustPileStart = player.PlayerCombatState.ExhaustPile.Cards.Count,
                        EnemyTotalHpStart = combat.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp),
                        EnemyAliveStart = combat.Enemies.Count(static e => e.IsAlive),
                    };
                    CapturePlayerTurnEndSnapshot(activeTurnAcc, combat, player);
                }

                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                ContinuationStamp liveStamp = ContinuationStamp.CaptureLive(combat);

                if (activePlan != null)
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
                            string[] diffs = expected.ExpectedState.DescribeDifferences(liveStamp, maximumDifferences: 8).ToArray();
                            unplannedReplans++;
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                                currentTurn, "StateMismatch", diffs.FirstOrDefault() ?? "mismatch", diffs));
                        }
                        else if (activePlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplans++;
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                                currentTurn, "ContinuationMissing",
                                $"cached_turns={activePlan.Continuations.Count}", []));
                        }
                        activePlan = null;
                    }
                }

                if (activePlan == null)
                {
                    activeTurnAcc.ReplannedOnTurn = true;
                    CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                    SolverDisplayNames names = SolverDisplayNames.Capture(combat);
                    SolverSettingsSnapshot settings = SolverSettings.Capture() with { PotionPolicy = potionPolicy };
                    SolverTheftPolicy? theftPolicy = SolverController.ResolveTheftPolicy(combat);
                    SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                        settings, combat, includeTurnSetup: false, theftPolicy: theftPolicy) with
                    {
                        Profile = resolvedProfile,
                        FixedBudget = true,
                        BudgetOverrideMilliseconds = budgetMs,
                        MaxDegreeOfParallelism = dop,
                        PotionPolicy = potionPolicy,
                        EnableTurnBoundaryRollouts = !ModRuntime.DisableTurnBoundaryRolloutsOverride,
                    };

                    if (!hasRootHotPolicy)
                    {
                        CombatHotState rootHotState = root.HotRoot.RootState;
                        rootContext = PlayerBeatReplayRunner.CaptureRootCanonicalContext(root, damage, policy);
                        rootSlotCosts = rootContext.RootSlotCosts;
                        rootHotPolicy = rootContext.Policy;
                        int slotCount = Math.Min((int)rootHotState.PotionSlotCount, HotLimits.MaxPotions);
                        for (int s = 0; s < slotCount; s++)
                        {
                            if (rootHotState.Potions[s] != 0)
                                rootOccupiedMask |= (ushort)(1 << s);
                        }
                        fightTheftPolicy = theftPolicy;
                        hasRootHotPolicy = true;
                    }

                    Stopwatch sw = Stopwatch.StartNew();
                    SolverResult solved = HotSearchCoordinator.Solve(
                        root, names, damage, policy, CancellationToken.None, null);
                    sw.Stop();

                    fightSolveMs += sw.Elapsed.TotalMilliseconds;
                    fightSolveCount++;
                    if (fightSolveCount == 1)
                        turn1WasLosslesslyExhausted = solved.WasLosslesslyExhausted;
                    if (!solved.WasLosslesslyExhausted)
                        allSolvesLosslesslyExhausted = false;
                    lastOnlyDeathRoutesFound = solved.OnlyDeathRoutesFound;
                    lastDeathTurn = solved.DeathTurn;

                    fightExpandedNodes += solved.TotalExpandedNodes > 0 ? solved.TotalExpandedNodes : solved.ExpandedNodes;
                    fightTransitions += solved.TotalTransitionCount > 0 ? solved.TotalTransitionCount : solved.TransitionCount;
                    fightLastBoundary = solved.BoundaryReason.ToString();
                    firstRouteSignature ??= BenchmarkOutcomeHelper.BuildRouteSignature(solved);
                    activePlan = solved;
                    BattleDamageTracker.RegisterPlan(combat, activePlan);
                }

                SolverResult currentPlan = activePlan;
                int planTurn = currentPlan.StartTurnNumber;

                activeTurnAcc.HpLost = TryLookup(currentPlan.HpLostByTurn, planTurn);
                activeTurnAcc.HpRecovered = TryLookup(currentPlan.HpRecoveredByTurn, planTurn);
                activeTurnAcc.SoldHp = TryLookup(currentPlan.SoldHpByTurn, planTurn);
                activeTurnAcc.EnemyHpLost = TryLookup(currentPlan.EnemyHpLostByTurn, planTurn);
                activeTurnAcc.MaxBlock = TryLookup(currentPlan.MaxBlockByTurn, planTurn);
                activeTurnAcc.ActualBlock = TryLookup(currentPlan.ActualBlockByTurn, planTurn);
                activeTurnAcc.EnergyLeft = TryLookup(currentPlan.EnergyLeftByTurn, planTurn);
                activeTurnAcc.PotionCount = TryLookup(currentPlan.PotionCountByTurn, planTurn);
                activeTurnAcc.PotionStrategicCost = TryLookup(currentPlan.PotionStrategicCostByTurn, planTurn);

                List<int> turnActionIndices = [];
                List<PlanAction> actions = [];
                PlanAction? plannedEndTurn = null;
                for (int idx = 0; idx < currentPlan.BestNode.Actions.Count; idx++)
                {
                    PlanAction a = currentPlan.BestNode.Actions[idx];
                    if (a.Turn != planTurn)
                        continue;
                    if (a.IsExecutable)
                    {
                        actions.Add(a);
                        turnActionIndices.Add(idx);
                    }
                    else if (a.Kind == PlanActionKind.EndTurn && plannedEndTurn == null)
                    {
                        plannedEndTurn = a;
                        turnActionIndices.Add(idx);
                    }
                }
                foreach (int aIdx in turnActionIndices)
                {
                    if (currentPlan.KillsAfterAction.TryGetValue(aIdx, out IReadOnlyList<string>? killed) && killed.Count > 0)
                        activeTurnAcc.Kills.AddRange(killed);
                }

                bool drifted = false;
                for (int actionIdx = 0; actionIdx < actions.Count; actionIdx++)
                {
                    if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                        break;
                    if (player.PlayerCombatState!.TurnNumber != planTurn)
                    {
                        unplannedReplans++;
                        replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                            planTurn, "TurnDriftMidDeploy",
                            $"expected_turn={planTurn} actual_turn={player.PlayerCombatState.TurnNumber} at action#{actionIdx}", []));
                        activePlan = null;
                        drifted = true;
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
                            unplannedReplans++;
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                                planTurn, "PotionDrift",
                                $"slot={action.PotionSlot} expected={action.PotionId} actual={potion?.Id.Entry ?? "empty"}", []));
                            activePlan = null;
                            drifted = true;
                            break;
                        }
                        if ((uint)action.PotionSlot < (uint)rootSlotCosts.Length)
                        {
                            ushort bit = (ushort)(1 << action.PotionSlot);
                            if ((rootOccupiedMask & ~rootConsumedMask & bit) != 0)
                            {
                                rootConsumedMask |= bit;
                                if (rootSlotCosts[action.PotionSlot] > 0)
                                {
                                    rootOptionalPaidRawHpCost += rootSlotCosts[action.PotionSlot];
                                    rootOptionalPaidCount++;
                                }
                            }
                        }
                        liveAction = RealCombatAction.UsePotion(potion, target);
                        fightPotionsUsed++;
                        potionsUsedIds.Add(potion.Id.Entry);
                        activePotionsUsed?.Add((action.PotionSlot, potion.Id.Entry));
                        activeTurnAcc.PotionsUsedLive++;
                        activeTurnAcc.PotionIdsUsed.Add(potion.Id.Entry);
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
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(planTurn, "CardMissing", ex.Message, []));
                            activePlan = null;
                            drifted = true;
                            break;
                        }
                        if (!card.CanPlayTargeting(target))
                        {
                            unplannedReplans++;
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                                planTurn, "CardUnplayable",
                                $"card={action.CardId} target={action.TargetCombatId?.ToString() ?? "-"} energy={player.PlayerCombatState.Energy}", []));
                            activePlan = null;
                            drifted = true;
                            break;
                        }
                        if (string.Equals(action.CardId, "ALCHEMIZE", StringComparison.Ordinal))
                            alchemizeCount++;
                        liveAction = RealCombatAction.PlayCard(card, target);
                        activeTurnAcc.CardsPlayed++;
                        activeTurnAcc.CardsPlayedIds.Add(card.Id.Entry);
                        switch (card.Type)
                        {
                            case CardType.Attack:
                                activeTurnAcc.AttackCardsPlayed++;
                                break;
                            case CardType.Skill:
                                activeTurnAcc.SkillCardsPlayed++;
                                break;
                            case CardType.Power:
                                activeTurnAcc.PowerCardsPlayed++;
                                break;
                            case CardType.Status:
                                activeTurnAcc.StatusCardsPlayed++;
                                break;
                            case CardType.Curse:
                                activeTurnAcc.CurseCardsPlayed++;
                                break;
                        }
                        if (card.Affliction is { } affliction)
                        {
                            activeTurnAcc.AfflictedCardsPlayed++;
                            string afflictionId = affliction.Id.Entry;
                            activeTurnAcc.AfflictionsPlayed[afflictionId] =
                                activeTurnAcc.AfflictionsPlayed.GetValueOrDefault(afflictionId) + 1;
                        }
                    }

                    PlanDrivenCardSelector actionSelector = new(choicePlans);
                    try
                    {
                        using (RunManager.Instance.CardSelectorStack.PushSelector(player, actionSelector))
                        {
                            _ = RealCombatDriver.RunRealCombatAction(
                                loop, combat, liveAction, externalErrorProbe: () => actionSelector.LastError);
                        }
                    }
                    catch (Exception ex) when (PlayerBeatReplayRunner.FindInner<NativeChoicePlanMismatchException>(ex) != null)
                    {
                        choiceMismatches++;
                        throw;
                    }

                    CapturePlayerTurnEndSnapshot(activeTurnAcc, combat, player);
                }

                if (drifted)
                    continue;

                if (RealCombatDriver.GetLiveOutcome(combat) == HotCombatOutcome.Ongoing
                    && player.PlayerCombatState!.TurnNumber == planTurn)
                {
                    if (plannedEndTurn == null)
                    {
                        if (currentPlan.BoundaryReason == SearchBoundaryReason.None)
                        {
                            unplannedReplans++;
                            replanDiffs.Add(new PlayerBeatReplayRunner.DeployReplanDiff(
                                planTurn, "PlanExhaustedWithoutEndTurn", $"executed={actions.Count}", []));
                        }
                        activePlan = null;
                        continue;
                    }

                    CapturePlayerTurnEndSnapshot(activeTurnAcc, combat, player);

                    IReadOnlyList<PlanCardChoice> endTurnChoices = plannedEndTurn.TurnStartChoices ?? [];
                    PlanDrivenCardSelector endTurnSelector = new(endTurnChoices);
                    try
                    {
                        using (RunManager.Instance.CardSelectorStack.PushSelector(player, endTurnSelector))
                        {
                            _ = RealCombatDriver.RunRealCombatAction(
                                loop, combat, RealCombatAction.EndTurn(), externalErrorProbe: () => endTurnSelector.LastError);
                        }
                    }
                    catch (Exception ex) when (PlayerBeatReplayRunner.FindInner<NativeChoicePlanMismatchException>(ex) != null)
                    {
                        choiceMismatches++;
                        throw;
                    }

                    CommitTurnAccumulator(activeTurnAcc, combat, player, perTurn);
                    activeTurnAcc = null;
                }
            }

            if (activeTurnAcc != null)
            {
                CommitTurnAccumulator(activeTurnAcc, combat, player, perTurn);
                activeTurnAcc = null;
            }
        }
        finally
        {
            _activeDamageObserver = null;
            combatHistory.Changed -= DrainLiveHistoryToBuffer;
            player.Creature.CurrentHpChanged -= OnPlayerHpChanged;
        }

        if (fightSolveCount == 0)
            allSolvesLosslesslyExhausted = false;

        HotCombatOutcome finalOutcome = RealCombatDriver.GetLiveOutcome(combat);
        int endHp = player.Creature.CurrentHp;
        int endMaxHp = player.Creature.MaxHp;
        bool won = finalOutcome == HotCombatOutcome.Victory;
        bool survives = !player.Creature.IsDead && endHp > 0;

        int remainingLizardTails = player.Relics
            .OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>()
            .Count(static t => !t.WasUsed);
        int remainingFairyCount = player.PotionSlots
            .Count(static p => p != null && string.Equals(p.Id.Entry, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));
        int lizardTailConsumed = Math.Max(0, initialUnusedLizardTails - remainingLizardTails);
        int fairyConsumed = Math.Max(0, initialFairyCount - remainingFairyCount);
        int deathSaveUses = Math.Max(lizardTailConsumed + fairyConsumed, observedDeathSaveUses);
        int deathSaveHpRestored = observedDeathSaveHpRestored;
        int hpLoss = Math.Max(0, startHp - (endHp - deathSaveHpRestored));
        int finalGrossHpLoss = Math.Max(hpLoss, grossHpLoss);

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
            FightId = fightId,
            Tier = tier,
            Group = group,
            CharacterId = characterId,
            EncounterId = encounterId,
            Won = won,
            Survives = survives,
            DeathSaveUseCount = deathSaveUses,
            StartHp = startHp,
            EndHp = endHp,
            StartMaxHp = startMaxHp,
            EndMaxHp = endMaxHp,
            HpLoss = hpLoss,
            GrossHpLoss = finalGrossHpLoss,
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
            UnplannedReplans = unplannedReplans,
            ChoiceMismatches = choiceMismatches,
            HardErrors = 0,
            TheftPolicy = fightTheftPolicy?.ToString(),
            HardError = null,
            RouteSignature = firstRouteSignature,
        };
    }
}
