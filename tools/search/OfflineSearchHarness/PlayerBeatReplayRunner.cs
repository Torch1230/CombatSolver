using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using CombatSolver.Replay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

/// <summary>
/// Plain-.NET Targeted Replay &amp; Player-Beat-Solver Benchmark Runner:
/// <list type="bullet">
///   <item><see cref="RunReplayBundle"/> (<c>--replay-bundle &lt;zip-or-dir-or-json&gt; [--lockstep-actions] [--deploy] [--dump-diff &lt;path&gt;]</c>):
///   restores a single real CombatSolver report bundle / checkpoint archive directly against real <c>sts2.dll</c>
///   without Godot (~0.2s/fight), solves with current <see cref="HotSearchCoordinator"/> comparing vs player &amp; old solver,
///   and optionally lockstep-replays recorded player actions against <c>sts2.dll</c> + <see cref="HotCombatEngine"/>
///   (or deploys current solver's plan) to emit exact turn-by-turn <see cref="ContinuationStamp"/> diffs.</item>
///   <item><see cref="RunPlayerCorpus"/> (<c>--bench-player-corpus &lt;curated_manifest.json&gt; [--split all|dev|holdout] [--profile ...] [--dop 8] [--dump-outcomes &lt;path&gt;]</c>):
///   runs the curated vanilla single-player "player beat solver" corpus, prints a Solver-vs-Player scoreboard
///   (Beat / Tied / Behind Player + breakdown by character &amp; encounter type + Unplanned Replans + p50/p95 ms),
///   and writes a standard <see cref="BenchmarkOutcomeFile"/> directly consumable by <c>--compare-outcomes</c>.</item>
/// </list>
/// </summary>
internal static class PlayerBeatReplayRunner
{
    private const uint VanillaV01131ModelHash = 1748280997u;

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private enum PlayerVerdict
    {
        BeatPlayer,
        TiedPlayer,
        BehindPlayer,
        Error,
    }

    private sealed record CuratedCorpusManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("corpus_name")]
        public string CorpusName { get; init; } = "player-beat-solver";

        [JsonPropertyName("cases")]
        public CuratedCorpusCase[] Cases { get; init; } = [];
    }

    private sealed record CuratedCorpusCase
    {
        [JsonPropertyName("case_id")]
        public string CaseId { get; init; } = string.Empty;

        [JsonPropertyName("report_id")]
        public string ReportId { get; init; } = string.Empty;

        [JsonPropertyName("bundle_path")]
        public string BundlePath { get; init; } = string.Empty;

        [JsonPropertyName("bundle_rel_path")]
        public string? BundleRelPath { get; init; }

        [JsonPropertyName("format")]
        public string Format { get; init; } = "checkpoint_v2_native_events_v1";

        [JsonPropertyName("replay_mode")]
        public string ReplayMode { get; init; } = "native_events_combat_start";

        [JsonPropertyName("split")]
        public string Split { get; init; } = "dev";

        [JsonPropertyName("character")]
        public string Character { get; init; } = string.Empty;

        [JsonPropertyName("encounter")]
        public string Encounter { get; init; } = string.Empty;

        [JsonPropertyName("encounter_type")]
        public string EncounterType { get; init; } = "Monster";

        [JsonPropertyName("game_mode")]
        public string GameMode { get; init; } = "Standard";

        [JsonPropertyName("ascension")]
        public int Ascension { get; init; }

        [JsonPropertyName("act_index")]
        public int ActIndex { get; init; }

        [JsonPropertyName("floor")]
        public int Floor { get; init; }

        [JsonPropertyName("game_version")]
        public string GameVersion { get; init; } = string.Empty;

        [JsonPropertyName("old_solver_version")]
        public string OldSolverVersion { get; init; } = string.Empty;

        [JsonPropertyName("player_won")]
        public bool PlayerWon { get; init; } = true;

        [JsonPropertyName("player_hp_loss")]
        public int PlayerHpLoss { get; init; }

        [JsonPropertyName("player_potions_used")]
        public int PlayerPotionsUsed { get; init; }

        [JsonPropertyName("player_turns")]
        public int PlayerTurns { get; init; }

        [JsonPropertyName("old_solver_won")]
        public bool OldSolverWon { get; init; } = true;

        [JsonPropertyName("old_solver_hp_loss")]
        public int OldSolverHpLoss { get; init; }

        [JsonPropertyName("old_solver_potions_used")]
        public int OldSolverPotionsUsed { get; init; }

        [JsonPropertyName("old_solver_turns")]
        public int OldSolverTurns { get; init; }

        [JsonPropertyName("old_solver_baseline_turn")]
        public int OldSolverBaselineTurn { get; init; } = 1;

        [JsonPropertyName("player_checkpoint_id")]
        public string? PlayerCheckpointId { get; init; }

        [JsonPropertyName("player_event_cursor")]
        public int? PlayerEventCursor { get; init; }
    }

    private sealed record RecordedOriginJson
    {
        [JsonPropertyName("nextActionId")]
        public uint NextActionId { get; init; }

        [JsonPropertyName("nextHookId")]
        public uint NextHookId { get; init; }

        [JsonPropertyName("choiceIds")]
        public uint[] ChoiceIds { get; init; } = [];

        [JsonPropertyName("rewardIds")]
        public int[] RewardIds { get; init; } = [];

        [JsonPropertyName("gameVersion")]
        public string GameVersion { get; init; } = string.Empty;

        [JsonPropertyName("gameCommit")]
        public string GameCommit { get; init; } = string.Empty;

        [JsonPropertyName("modelIdHash")]
        public uint ModelIdHash { get; init; }
    }

    private sealed record BundleCheckpointSummary(
        string CheckpointId,
        string Label,
        int EventCursor,
        bool CanSearch,
        string? MetadataPath,
        string? ReplayStatePath,
        string? NativeStatePath,
        string? RunStatePath,
        string? ExpectedContinuationStamp);

    private sealed record BundleGroundTruth(
        string CaseId,
        string CharacterId,
        string EncounterId,
        string EncounterType,
        string GameMode,
        int Ascension,
        string GameVersion,
        string OldSolverVersion,
        bool HasPlayerOutcome,
        bool PlayerWon,
        int PlayerHpLoss,
        int PlayerPotionsUsed,
        int PlayerTurns,
        bool HasOldSolverOutcome,
        bool OldSolverWon,
        int OldSolverHpLoss,
        int OldSolverPotionsUsed,
        int OldSolverTurns,
        string? PlayerCheckpointId = null,
        int? PlayerEventCursor = null,
        int PlayerDeathSaveUseCount = 0);

    internal readonly record struct RootPotionSlotInfo(
        int Slot,
        string Entry,
        short StrategicCostHp,
        bool IsCombatUsable);

    internal readonly record struct RootCanonicalContext(
        bool HasPolicy,
        HotSearchPolicy Policy,
        short[] RootSlotCosts,
        RootPotionSlotInfo[] RootPotions)
    {
        public static RootCanonicalContext Empty => new(false, default, [], []);
    }

    private sealed record PlayerCheckpointSnapshot(
        int EventCursor,
        string CheckpointId,
        int CurrentHp,
        int MaxHp,
        int UnusedLizardTails,
        Dictionary<int, string> PotionsBySlot);

    private sealed record MatchedRouteTelemetry(
        bool Won,
        int ProjectedBattleHpLost,
        int TotalPotionsUsed,
        int Turns,
        int? FinalHp,
        int DeathSaveUseCount,
        int DeathSaveRelicHpRestored,
        int DeathSavePotionHpRestored);

    private sealed record PlayerBundleTelemetry(
        int InitialUnusedLizardTails,
        int InitialFairyCount,
        int DeathSaveUseCount,
        int LizardTailUseCount,
        int FairyUseCount,
        int DeathSaveHpRestored,
        int? TelemetryFinalHp,
        int LatestObservedMaxHp,
        int[] ConsumedRootPotionSlots,
        int AlchemizePlayCount)
    {
        public static PlayerBundleTelemetry Empty => new(0, 0, 0, 0, 0, 0, null, 0, [], 0);
    }

    private sealed class PreparedBundle : IDisposable
    {
        private readonly string? _tempDir;

        public required string RootDirectory { get; init; }
        public required string SourcePath { get; init; }
        public required string Format { get; init; }
        public required JsonObject CheckpointIndex { get; init; }
        public JsonObject? ReportJson { get; init; }
        public required RecordedOriginJson Origin { get; init; }
        public required string OriginSavePath { get; init; }
        public required RecordedCombatEvent[] Events { get; init; }
        public required BundleCheckpointSummary StartCheckpoint { get; init; }
        public BundleCheckpointSummary? ReadyCheckpoint { get; init; }
        public required IReadOnlyList<BundleCheckpointSummary> AllCheckpoints { get; init; }
        public required BundleGroundTruth GroundTruth { get; init; }
        public required PlayerBundleTelemetry PlayerTelemetry { get; init; }
        public bool ModelHashMatches => Origin.ModelIdHash == ModelIdSerializationCache.Hash;

        public PreparedBundle(string? tempDir) => _tempDir = tempDir;

        public void Dispose()
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); }
                catch { /* best-effort temp cleanup */ }
            }
        }
    }

    private sealed record RestoreVerification(
        bool CombatStartStampMatched,
        bool CombatStartNativeMatched,
        bool ReadyStampMatched,
        bool ReadyNativeMatched,
        int OpeningEventsReplayed,
        string? FirstRestoreDiff,
        double RestoreMs);

    private sealed record LockstepStepDiff(
        int StepIndex,
        int EventSequence,
        int TurnNumber,
        string ActionKind,
        string ActionDescription,
        bool HotMatchedLive,
        string ExpectedOutcome,
        string ActualOutcome,
        string[] Differences,
        string? CheckpointIdVerified,
        bool? CheckpointStampMatched,
        string? CheckpointFirstDiff);

    private sealed record LockstepAuditReport(
        string BundlePath,
        string CharacterId,
        string EncounterId,
        bool ModelHashMatches,
        int TotalRecordedEvents,
        int ActionsReplayed,
        int HotDiffCount,
        int CheckpointMismatchCount,
        string? Error,
        LockstepStepDiff[] Steps);

    internal sealed record DeployReplanDiff(
        int Turn,
        string Cause,
        string FirstDiff,
        string[] Differences);

    private sealed record PlayerCorpusCaseResult(
        CuratedCorpusCase Case,
        FightOutcomeRecord Outcome,
        PlayerVerdict VerdictVsPlayer,
        PlayerVerdict VerdictVsOldSolver,
        int PlayerPotionCostEstimateHp,
        double SolverEffectiveCostHp,
        double PlayerEffectiveCostHp,
        bool CombatStartStampMatched,
        bool ReadyStampMatched,
        double RestoreMs,
        int PlayerDeathSaveUseCount = 0,
        BossHpRelief BossHpRelief = BossHpRelief.None,
        double? SolverCanonicalV = null,
        double? PlayerCanonicalV = null,
        PlayerVerdict? VsPlayerCanonicalV = null);

    private sealed record CapturedChoiceRecord(
        string[] OptionEntries,
        int[] OptionUpgrades,
        string[] OptionStateKeys,
        int[] SelectedIndices,
        string[] SelectedEntries,
        int[] SelectedUpgrades,
        string[] SelectedStateKeys);

    /// <summary>
    /// Feeds recorded <see cref="CombatReplayEventType.PlayerChoice"/> events from <c>events.jsonl</c>
    /// into live <c>sts2.dll</c> <c>CardSelectCmd</c>, recording the exact live options and picked cards
    /// so <see cref="HotCombatEngine"/> can execute the exact same choice in lockstep.
    /// </summary>
    private sealed class RecordedEventChoiceSelector(
        Player player,
        RecordedCombatEvent[] events,
        Func<int> getCursor,
        Action<int> setCursor,
        bool syncLocalChoice) : ICardSelector
    {
        private readonly Player _player = player;
        private readonly RecordedCombatEvent[] _events = events;
        private readonly Func<int> _getCursor = getCursor;
        private readonly Action<int> _setCursor = setCursor;
        private readonly bool _syncLocalChoice = syncLocalChoice;

        public List<CapturedChoiceRecord> CapturedChoices { get; } = [];
        public Exception? LastError { get; private set; }

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            try
            {
                int cursor = _getCursor();
                while (cursor < _events.Length)
                {
                    CombatReplayEvent peek = DecodeReplayEvent(_events[cursor]);
                    if (peek.eventType == CombatReplayEventType.PlayerChoice)
                        break;
                    if (peek.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.ResumeAction)
                    {
                        cursor++;
                        continue;
                    }
                    // Enemy-turn and next-turn-start choices follow the system ReadyToBeginEnemyTurnAction gate.
                    if (peek.eventType == CombatReplayEventType.GameAction
                        && string.Equals(_events[cursor].Origin, "system", StringComparison.Ordinal)
                        && peek.action is NetReadyToBeginEnemyTurnAction)
                    {
                        cursor++;
                        continue;
                    }
                    break;
                }

                if (cursor >= _events.Length)
                    throw new InvalidDataException($"recorded_choice_missing:cursor={cursor}/{_events.Length}");

                CombatReplayEvent ev = DecodeReplayEvent(_events[cursor]);
                if (ev.eventType != CombatReplayEventType.PlayerChoice || ev.playerChoiceResult is not { } net)
                {
                    throw new InvalidDataException(
                        $"recorded_choice_order_mismatch:cursor={cursor} actual_event={ev.eventType}");
                }

                CardModel[] candidates = options.ToArray();
                PlayerChoiceResult choice = PlayerChoiceResult.FromNetData(
                    _player, _player.Creature.CombatState!.RunState, net);

                CardModel[] selected;
                if (net.type == PlayerChoiceType.Index)
                {
                    selected = choice.AsIndexes()
                        .Where(static idx => idx >= 0)
                        .Select(idx => (uint)idx < (uint)candidates.Length
                            ? candidates[idx]
                            : throw new InvalidDataException(
                                $"recorded_choice_index_out_of_range:{idx}/{candidates.Length}"))
                        .ToArray();
                }
                else
                {
                    selected = choice.AsCards(net.type)
                        .Select(card => MatchCandidateCard(candidates, card, net.type))
                        .ToArray();
                }

                if (selected.Length < minSelect || selected.Length > maxSelect)
                {
                    throw new InvalidDataException(
                        $"recorded_choice_count_mismatch:selected={selected.Length} range={minSelect}..{maxSelect}");
                }

                int[] selectedIndices = selected
                    .Select(sel => Array.FindIndex(candidates, c => ReferenceEquals(c, sel)))
                    .ToArray();

                CapturedChoices.Add(new CapturedChoiceRecord(
                    OptionEntries: candidates.Select(static c => c.Id.Entry).ToArray(),
                    OptionUpgrades: candidates.Select(static c => c.CurrentUpgradeLevel).ToArray(),
                    OptionStateKeys: candidates.Select(static c => HotImportSupport.FormatLiveChoiceCardKey(c)).ToArray(),
                    SelectedIndices: selectedIndices,
                    SelectedEntries: selected.Select(static c => c.Id.Entry).ToArray(),
                    SelectedUpgrades: selected.Select(static c => c.CurrentUpgradeLevel).ToArray(),
                    SelectedStateKeys: selected.Select(static c => HotImportSupport.FormatLiveChoiceCardKey(c)).ToArray()));

                if (_syncLocalChoice && ev.choiceId is uint cid)
                    RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(_player, cid, choice);

                _setCursor(cursor + 1);
                return Task.FromResult<IEnumerable<CardModel>>(selected);
            }
            catch (Exception ex)
            {
                LastError ??= ex;
                throw;
            }
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => new() { card = options.Count > 0 ? options[0].Card : null };

        private static CardModel MatchCandidateCard(
            CardModel[] candidates,
            CardModel target,
            PlayerChoiceType choiceType)
        {
            foreach (CardModel c in candidates)
            {
                if (ReferenceEquals(c, target))
                    return c;
            }
            if (choiceType == PlayerChoiceType.CanonicalCard)
            {
                foreach (CardModel c in candidates)
                {
                    if (c.Id == target.Id)
                        return c;
                }
            }
            foreach (CardModel c in candidates)
            {
                if (c.Id == target.Id && c.CurrentUpgradeLevel == target.CurrentUpgradeLevel)
                    return c;
            }
            throw new InvalidDataException(
                $"recorded_choice_card_not_found:{target.Id.Entry}+{target.CurrentUpgradeLevel}");
        }
    }

    /// <summary>
    /// Entry point for <c>--replay-bundle &lt;zip-or-dir-or-json&gt; [options]</c>.
    /// </summary>
    public static int RunReplayBundle(string[] args)
    {
        string? bundleInput = null;
        bool lockstepActions = false;
        bool deploySolver = false;
        string? dumpDiffPath = null;
        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        int dop = 8;
        int budgetMs = 15_000;
        string searchMode = "Coordinator";
        string potionPolicy = "Smart";
        bool stopAtZeroLoss = false;
        bool disableRollout = false;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--replay-bundle 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--lockstep-actions":
                    lockstepActions = true;
                    break;
                case "--deploy":
                    deploySolver = true;
                    break;
                case "--dump-diff":
                    dumpDiffPath = Path.GetFullPath(NextVal());
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
                case "--no-rollout":
                    disableRollout = true;
                    break;
                default:
                    if (key.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"--replay-bundle 未知选项：{key}");
                    if (bundleInput != null)
                        throw new ArgumentException($"--replay-bundle 仅接受一个目标路径，多余参数：{key}");
                    bundleInput = Path.GetFullPath(key);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(bundleInput))
        {
            Console.Error.WriteLine(
                "用法：--replay-bundle <zip-or-dir-or-json> [--lockstep-actions] [--deploy] " +
                "[--dump-diff <path>] [--profile Medium|High|...] [--dop 8] [--budget-ms 15000]");
            return 2;
        }
        if (!File.Exists(bundleInput) && !Directory.Exists(bundleInput))
        {
            Console.Error.WriteLine($"找不到回放目标：{bundleInput}");
            return 2;
        }

        ValidateSearchArgs(profile, searchMode, potionPolicy, out SolverPotionPolicy parsedPotionPolicy);
        if (disableRollout)
            ModRuntime.DisableTurnBoundaryRolloutsOverride = true;

        // Route JSON / Fuzz JSON / Manifest case JSON support
        if (File.Exists(bundleInput) && bundleInput.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return RunStandaloneJsonReplay(
                bundleInput, lockstepActions, deploySolver, dumpDiffPath,
                profile, beamOverride, nodesOverride, dop, budgetMs, searchMode, parsedPotionPolicy, stopAtZeroLoss);
        }

        string repoRoot = ResolveRepoRoot();
        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = BuildModOptions(
            repoRoot, profile, beamOverride, nodesOverride, dop, budgetMs,
            parsedPotionPolicy, searchMode, stopAtZeroLoss, "replay-bundle-logs");
        GameBootstrap.EnsureBootstrapped(loop, modOptions);
        SolverSearchProfile resolvedProfile = ModRuntime.ResolveProfile(modOptions) with
        {
            SoftTimeBudgetMilliseconds = budgetMs,
        };

        using PreparedBundle bundle = LoadBundle(bundleInput);
        BundleGroundTruth gt = bundle.GroundTruth;
        Console.WriteLine($"=== --replay-bundle {Path.GetFileName(bundleInput)} ===");
        Console.WriteLine(
            $"Case={gt.CaseId} | {gt.CharacterId} vs {gt.EncounterId} ({gt.EncounterType}) | " +
            $"mode={gt.GameMode} A{gt.Ascension} | game={gt.GameVersion} old_solver={gt.OldSolverVersion} | " +
            $"events={bundle.Events.Length} | modelHash={(bundle.ModelHashMatches ? "MATCH" : $"MISMATCH({bundle.Origin.ModelIdHash}!={ModelIdSerializationCache.Hash}, vanilla={VanillaV01131ModelHash})")}");

        int exitCode = 0;
        try
        {
            if (lockstepActions)
            {
                LockstepAuditReport audit = RunLockstepAudit(loop, bundle);
                PrintLockstepSummary(audit);
                if (dumpDiffPath != null)
                {
                    WriteJsonFile(dumpDiffPath, audit);
                    Console.WriteLine($"diff_json={dumpDiffPath}");
                }
                if (audit.HotDiffCount > 0 || audit.CheckpointMismatchCount > 0 || !string.IsNullOrEmpty(audit.Error))
                    exitCode = 1;
            }

            // Always also restore Turn-1 root and solve (or deploy) so user sees Solver vs Player & Old Solver
            CombatState combat = RestoreBundleToTurn1Playable(loop, bundle, out RestoreVerification verify);
            Console.WriteLine(
                $"[restore] {verify.RestoreMs:F1}ms | start_stamp={(verify.CombatStartStampMatched ? "OK" : "DIFF")} " +
                $"start_native={(verify.CombatStartNativeMatched ? "OK" : "DIFF/NA")} | " +
                $"ready_stamp={(verify.ReadyStampMatched ? "OK" : "DIFF/NA")} | opening_events={verify.OpeningEventsReplayed}");
            if (!string.IsNullOrEmpty(verify.FirstRestoreDiff))
                Console.WriteLine($"[restore:diff] {verify.FirstRestoreDiff}");

            List<DeployReplanDiff> deployDiffs = [];
            FightOutcomeRecord solverOutcome;
            RootCanonicalContext rootContext;
            if (deploySolver)
            {
                solverOutcome = SolveAndDeployBundleCombat(
                    loop, combat, gt.CaseId, "single", $"{gt.CharacterId.ToLowerInvariant()}_{gt.EncounterType.ToLowerInvariant()}",
                    gt.CharacterId, gt.EncounterId, resolvedProfile, parsedPotionPolicy, dop, budgetMs,
                    out rootContext, deployDiffs);
                PrintSingleBundleComparison(gt, solverOutcome, in rootContext, bundle.PlayerTelemetry, deployDiffs);
                if (dumpDiffPath != null && !lockstepActions)
                {
                    double? solverCanonicalV = null;
                    double? playerCanonicalV = null;
                    PlayerVerdict? canonicalVerdict = gt.HasPlayerOutcome
                        ? EvaluateCanonicalVAgainstPlayer(
                            solverOutcome, gt.PlayerWon, gt.PlayerHpLoss, gt.PlayerPotionsUsed,
                            in rootContext, bundle.PlayerTelemetry,
                            out solverCanonicalV, out playerCanonicalV)
                        : null;
                    WriteJsonFile(dumpDiffPath, new
                    {
                        bundle_path = bundle.SourcePath,
                        case_id = gt.CaseId,
                        restore = verify,
                        solver_outcome = solverOutcome,
                        ground_truth = gt,
                        player_death_save_use_count = gt.PlayerDeathSaveUseCount,
                        boss_hp_relief = rootContext.HasPolicy ? rootContext.Policy.StrategicBossHpRelief.ToString() : null,
                        solver_canonical_v = solverCanonicalV,
                        player_canonical_v = playerCanonicalV,
                        vs_player_canonical_v = canonicalVerdict?.ToString(),
                        deploy_replans = deployDiffs,
                    });
                    Console.WriteLine($"diff_json={dumpDiffPath}");
                }
                if (solverOutcome.HardErrors > 0 || solverOutcome.UnplannedReplans > 0 || solverOutcome.ChoiceMismatches > 0)
                    exitCode = 1;
            }
            else
            {
                solverOutcome = SolveRestoredRoot(
                    combat, gt.CaseId, "single", $"{gt.CharacterId.ToLowerInvariant()}_{gt.EncounterType.ToLowerInvariant()}",
                    gt.CharacterId, gt.EncounterId, resolvedProfile, searchMode, parsedPotionPolicy, dop, budgetMs,
                    out rootContext);
                PrintSingleBundleComparison(gt, solverOutcome, in rootContext, bundle.PlayerTelemetry, deployDiffs);
                if (dumpDiffPath != null && !lockstepActions)
                {
                    double? solverCanonicalV = null;
                    double? playerCanonicalV = null;
                    PlayerVerdict? canonicalVerdict = gt.HasPlayerOutcome
                        ? EvaluateCanonicalVAgainstPlayer(
                            solverOutcome, gt.PlayerWon, gt.PlayerHpLoss, gt.PlayerPotionsUsed,
                            in rootContext, bundle.PlayerTelemetry,
                            out solverCanonicalV, out playerCanonicalV)
                        : null;
                    WriteJsonFile(dumpDiffPath, new
                    {
                        bundle_path = bundle.SourcePath,
                        case_id = gt.CaseId,
                        restore = verify,
                        solver_outcome = solverOutcome,
                        ground_truth = gt,
                        player_death_save_use_count = gt.PlayerDeathSaveUseCount,
                        boss_hp_relief = rootContext.HasPolicy ? rootContext.Policy.StrategicBossHpRelief.ToString() : null,
                        solver_canonical_v = solverCanonicalV,
                        player_canonical_v = playerCanonicalV,
                        vs_player_canonical_v = canonicalVerdict?.ToString(),
                    });
                    Console.WriteLine($"diff_json={dumpDiffPath}");
                }
                if (solverOutcome.HardErrors > 0)
                    exitCode = 1;
            }
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        return exitCode;
    }

    /// <summary>
    /// Entry point for <c>--bench-player-corpus &lt;curated_manifest.json&gt; [options]</c>.
    /// </summary>
    public static int RunPlayerCorpus(string[] args)
    {
        string? manifestPath = null;
        string split = "all";
        string profile = "Medium";
        int? beamOverride = null;
        int? nodesOverride = null;
        int dop = 8;
        int budgetMs = 15_000;
        string searchMode = "Coordinator";
        string potionPolicy = "Smart";
        bool stopAtZeroLoss = false;
        bool disableRollout = false;
        bool deploySolver = false;
        string? filter = null;
        int? limit = null;
        string? dumpOutcomesPath = null;
        string? scoreboardOutPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--bench-player-corpus 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--split":
                case "--tier":
                case "--slice":
                    split = NextVal().ToLowerInvariant();
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
                case "--no-rollout":
                    disableRollout = true;
                    break;
                case "--deploy":
                    deploySolver = true;
                    break;
                case "--filter":
                    filter = NextVal();
                    break;
                case "--limit":
                    limit = Math.Max(1, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--dump-outcomes":
                    dumpOutcomesPath = Path.GetFullPath(NextVal());
                    break;
                case "--out":
                    scoreboardOutPath = Path.GetFullPath(NextVal());
                    break;
                default:
                    if (key.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"--bench-player-corpus 未知选项：{key}");
                    if (manifestPath != null)
                        throw new ArgumentException($"--bench-player-corpus 仅接受一个清单路径，多余参数：{key}");
                    manifestPath = Path.GetFullPath(key);
                    break;
            }
        }

        string repoRoot = ResolveRepoRoot();
        manifestPath ??= Path.Combine(repoRoot, ".local", "player-beat-solver", "curated_manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"找不到策划语料清单：{manifestPath}");
            return 2;
        }

        if (split is not ("all" or "dev" or "holdout"))
            throw new ArgumentException($"--split 只支持 all|dev|holdout，收到：{split}");

        ValidateSearchArgs(profile, searchMode, potionPolicy, out SolverPotionPolicy parsedPotionPolicy);
        if (disableRollout)
            ModRuntime.DisableTurnBoundaryRolloutsOverride = true;

        CuratedCorpusManifest manifest = JsonSerializer.Deserialize<CuratedCorpusManifest>(
            File.ReadAllText(manifestPath), ReadJsonOptions)
            ?? throw new InvalidDataException($"无法解析语料清单：{manifestPath}");
        string manifestDir = Path.GetDirectoryName(manifestPath) ?? repoRoot;

        List<CuratedCorpusCase> cases = manifest.Cases
            .Where(c => split == "all" || string.Equals(c.Split, split, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            cases = cases
                .Where(c => c.CaseId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.Character.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.Encounter.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.EncounterType.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.GameMode.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (limit.HasValue && cases.Count > limit.Value)
            cases = cases.Take(limit.Value).ToList();

        if (cases.Count == 0)
        {
            Console.Error.WriteLine("筛选后没有可执行的玩家对照语料案例。");
            return 2;
        }

        string suiteName = $"player-beat-solver-{split}";
        dumpOutcomesPath ??= Path.Combine(
            repoRoot, ".local", "offline-harness",
            $"{suiteName}-{profile.ToLowerInvariant()}-dop{dop}.json");

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine(
            $"=== --bench-player-corpus (cases={cases.Count}, split={split}, profile={profile}, " +
            $"mode={searchMode}, potion={parsedPotionPolicy}, dop={dop}, budgetMs={budgetMs}, deploy={deploySolver}) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = BuildModOptions(
            repoRoot, profile, beamOverride, nodesOverride, dop, budgetMs,
            parsedPotionPolicy, searchMode, stopAtZeroLoss, "player-corpus-logs");
        GameBootstrap.EnsureBootstrapped(loop, modOptions);
        SolverSearchProfile resolvedProfile = ModRuntime.ResolveProfile(modOptions) with
        {
            SoftTimeBudgetMilliseconds = budgetMs,
        };

        int gen2Before = GC.CollectionCount(2);
        using MemorySampler memory = new(TimeSpan.FromMilliseconds(150));

        List<PlayerCorpusCaseResult> results = new(cases.Count);
        try
        {
            for (int idx = 0; idx < cases.Count; idx++)
            {
                CuratedCorpusCase entry = cases[idx];
                PlayerCorpusCaseResult res = RunSingleCorpusCase(
                    loop, entry, manifestDir, resolvedProfile, searchMode, parsedPotionPolicy,
                    dop, budgetMs, deploySolver);
                results.Add(res);

                string verdictTag = res.VerdictVsPlayer switch
                {
                    PlayerVerdict.BeatPlayer => "BEAT ",
                    PlayerVerdict.TiedPlayer => "TIED ",
                    PlayerVerdict.BehindPlayer => "BHND ",
                    _ => "ERR  ",
                };
                string winTag = res.Outcome.HardErrors > 0 ? "ERR" : (res.Outcome.Won ? "W" : "L");
                Console.WriteLine(
                    $"[{idx + 1,3}/{cases.Count,-3}] [{entry.Split,-7}] {verdictTag} | {entry.Character,-11} {entry.Encounter,-26} | " +
                    $"S:{winTag} hp={res.Outcome.HpLoss,3} pot={res.Outcome.PotionsUsed} vs " +
                    $"P:W hp={entry.PlayerHpLoss,3} pot={entry.PlayerPotionsUsed} (old:{(entry.OldSolverWon ? "W" : "L")}/{entry.OldSolverHpLoss}/{entry.OldSolverPotionsUsed}) | " +
                    $"{res.Outcome.ElapsedMs,6:F0}ms | replans={res.Outcome.UnplannedReplans}");
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

        FightOutcomeRecord[] outcomeRecords = results.Select(static r => r.Outcome).ToArray();
        BenchmarkSummaryRecord summary = BenchmarkOutcomeHelper.BuildSummary(
            outcomeRecords, totalSw.Elapsed.TotalMilliseconds, peakRssMb, gcGen2);

        BenchmarkOutcomeFile outcomeFile = new()
        {
            Suite = suiteName,
            Profile = profile,
            SearchMode = searchMode,
            PotionPolicy = parsedPotionPolicy.ToString(),
            Dop = dop,
            Seed = null,
            BudgetMs = budgetMs,
            Summary = summary,
            Outcomes = outcomeRecords,
        };
        BenchmarkOutcomeHelper.WriteOutcomeFile(dumpOutcomesPath, outcomeFile);

        Console.WriteLine();
        PrintPlayerCorpusScoreboard(results, summary, dumpOutcomesPath);

        if (scoreboardOutPath != null)
        {
            WriteJsonFile(scoreboardOutPath, BuildScoreboardJson(manifestPath, split, summary, results));
            Console.WriteLine($"scoreboard_json={scoreboardOutPath}");
        }

        return summary.HardErrors == 0 && summary.TotalUnplannedReplans == 0 ? 0 : 1;
    }

    private static int RunStandaloneJsonReplay(
        string jsonPath,
        bool lockstepActions,
        bool deploySolver,
        string? dumpDiffPath,
        string profile,
        int? beamOverride,
        int? nodesOverride,
        int dop,
        int budgetMs,
        string searchMode,
        SolverPotionPolicy parsedPotionPolicy,
        bool stopAtZeroLoss)
    {
        JsonObject rootObj = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject
            ?? throw new InvalidDataException($"JSON 根节点不是对象：{jsonPath}");

        // 1. FuzzFailureArtifact JSON -> delegate to CombatDiffFuzzer.RunReplay
        if (rootObj["scenario"] != null && rootObj["steps"] != null)
            return CombatDiffFuzzer.RunReplay(jsonPath);

        // 2. Single curated case JSON referencing bundle_path
        if (rootObj["bundle_path"]?.GetValue<string>() is { Length: > 0 } linkedBundle)
        {
            List<string> forwarded = [linkedBundle, "--profile", profile, "--dop", dop.ToString(CultureInfo.InvariantCulture),
                "--budget-ms", budgetMs.ToString(CultureInfo.InvariantCulture), "--search-mode", searchMode,
                "--potion-policy", parsedPotionPolicy.ToString()];
            if (beamOverride.HasValue) { forwarded.Add("--beam"); forwarded.Add(beamOverride.Value.ToString(CultureInfo.InvariantCulture)); }
            if (nodesOverride.HasValue) { forwarded.Add("--nodes"); forwarded.Add(nodesOverride.Value.ToString(CultureInfo.InvariantCulture)); }
            if (lockstepActions) forwarded.Add("--lockstep-actions");
            if (deploySolver) forwarded.Add("--deploy");
            if (stopAtZeroLoss) forwarded.Add("--stop-at-zero-loss");
            if (dumpDiffPath != null) { forwarded.Add("--dump-diff"); forwarded.Add(dumpDiffPath); }
            return RunReplayBundle(forwarded.ToArray());
        }

        // 3. UnattendedTestRequest / GeneratedScenario JSON
        string repoRoot = ResolveRepoRoot();
        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = BuildModOptions(
            repoRoot, profile, beamOverride, nodesOverride, dop, budgetMs,
            parsedPotionPolicy, searchMode, stopAtZeroLoss, "replay-json-logs");
        GameBootstrap.EnsureBootstrapped(loop, modOptions);
        SolverSearchProfile resolvedProfile = ModRuntime.ResolveProfile(modOptions) with
        {
            SoftTimeBudgetMilliseconds = budgetMs,
        };

        IDisposable? choiceScope = null;
        try
        {
            UnattendedTestRequest request = GeneratedScenarioSetup.ReadRequest(jsonPath);
            string evidenceDir = Path.Combine(repoRoot, ".local", "offline-harness", "replay-json-evidence");
            Directory.CreateDirectory(evidenceDir);
            GeneratedScenarioSetup setup = GeneratedScenarioSetup.Prepare(request, evidenceDir);
            Task<IDisposable?> enter = setup.EnterCombatRoomAsync();
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), "ReplayJson Enter");
            choiceScope = enter.GetAwaiter().GetResult();

            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            setup.CaptureOpening(combat);
            if (!setup.Request.PreserveNativeCombatStateForTest)
            {
                loop.RunUntilCompleted(
                    setup.Session.InjectInitialStateAsync(combat, LocalContext.GetMe(combat)!),
                    TimeSpan.FromSeconds(30), "ReplayJson Inject");
            }

            List<DeployReplanDiff> deployDiffs = [];
            string fightId = Path.GetFileNameWithoutExtension(jsonPath);
            FightOutcomeRecord outcome = deploySolver
                ? SolveAndDeployBundleCombat(
                    loop, combat, fightId, "json", "standalone_json",
                    setup.Request.CharacterId, setup.Request.EncounterId,
                    resolvedProfile, parsedPotionPolicy, dop, budgetMs, out _, deployDiffs)
                : SolveRestoredRoot(
                    combat, fightId, "json", "standalone_json",
                    setup.Request.CharacterId, setup.Request.EncounterId,
                    resolvedProfile, searchMode, parsedPotionPolicy, dop, budgetMs, out _);

            Console.WriteLine(
                $"[json-replay] {fightId}: {(outcome.Won ? "WIN" : "LOSS")} hp_loss={outcome.HpLoss} " +
                $"pot={outcome.PotionsUsed} V={outcome.EconomicValueV:F2} T={outcome.Turns} " +
                $"ms={outcome.ElapsedMs:F1} replans={outcome.UnplannedReplans} boundary={outcome.Boundary}");
            if (dumpDiffPath != null)
                WriteJsonFile(dumpDiffPath, new { request_path = jsonPath, outcome, deploy_replans = deployDiffs });
            return outcome.HardErrors == 0 && outcome.UnplannedReplans == 0 ? 0 : 1;
        }
        finally
        {
            choiceScope?.Dispose();
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }
    }

    private static PlayerCorpusCaseResult RunSingleCorpusCase(
        MainLoopContext loop,
        CuratedCorpusCase entry,
        string manifestDir,
        SolverSearchProfile resolvedProfile,
        string searchMode,
        SolverPotionPolicy potionPolicy,
        int dop,
        int budgetMs,
        bool deploySolver)
    {
        string bundlePath = ResolveCaseBundlePath(entry, manifestDir);
        string group = $"{entry.Character.ToLowerInvariant()}_{entry.EncounterType.ToLowerInvariant()}";
        bool startMatched = false;
        bool readyMatched = false;
        double restoreMs = 0.0;

        try
        {
            using PreparedBundle bundle = LoadBundle(bundlePath, entry);
            CombatState combat = RestoreBundleToTurn1Playable(loop, bundle, out RestoreVerification verify);
            startMatched = verify.CombatStartStampMatched;
            readyMatched = verify.ReadyStampMatched;
            restoreMs = verify.RestoreMs;

            RootCanonicalContext rootContext;
            FightOutcomeRecord outcome;
            if (deploySolver)
            {
                List<DeployReplanDiff> deployDiffs = [];
                outcome = SolveAndDeployBundleCombat(
                    loop, combat, entry.CaseId, entry.Split, group,
                    entry.Character, entry.Encounter,
                    resolvedProfile, potionPolicy, dop, budgetMs,
                    out rootContext, deployDiffs);
            }
            else
            {
                outcome = SolveRestoredRoot(
                    combat, entry.CaseId, entry.Split, group,
                    entry.Character, entry.Encounter,
                    resolvedProfile, searchMode, potionPolicy, dop, budgetMs,
                    out rootContext);
            }

            short[] rootSlotCosts = rootContext.RootSlotCosts;
            int playerDeathSaveUseCount = bundle.PlayerTelemetry.DeathSaveUseCount;
            PlayerVerdict vsPlayer = EvaluateAgainstTarget(
                outcome, entry.PlayerWon, entry.PlayerHpLoss, entry.PlayerPotionsUsed,
                rootSlotCosts, out int playerPotEst, out double solverEffCost, out double playerEffCost,
                targetDeathSaveUseCount: playerDeathSaveUseCount);
            // Old-solver manifest rows do not record death saves, so keep targetDeathSaveUseCount = 0 for vsOld.
            PlayerVerdict vsOld = EvaluateAgainstTarget(
                outcome, entry.OldSolverWon, entry.OldSolverHpLoss, entry.OldSolverPotionsUsed,
                rootSlotCosts, out _, out _, out _,
                targetDeathSaveUseCount: 0);

            PlayerVerdict? vsPlayerCanonicalV = EvaluateCanonicalVAgainstPlayer(
                outcome, entry.PlayerWon, entry.PlayerHpLoss, entry.PlayerPotionsUsed,
                in rootContext, bundle.PlayerTelemetry,
                out double? solverCanonicalV, out double? playerCanonicalV);
            BossHpRelief bossHpRelief = rootContext.HasPolicy
                ? rootContext.Policy.StrategicBossHpRelief
                : BossHpRelief.None;

            return new PlayerCorpusCaseResult(
                entry, outcome, vsPlayer, vsOld, playerPotEst, solverEffCost, playerEffCost,
                startMatched, readyMatched, restoreMs,
                PlayerDeathSaveUseCount: playerDeathSaveUseCount,
                BossHpRelief: bossHpRelief,
                SolverCanonicalV: solverCanonicalV,
                PlayerCanonicalV: playerCanonicalV,
                VsPlayerCanonicalV: vsPlayerCanonicalV);
        }
        catch (Exception ex)
        {
            Exception rootEx = Unwrap(ex);
            FightOutcomeRecord errOutcome = new()
            {
                FightId = entry.CaseId,
                Tier = entry.Split,
                Group = group,
                CharacterId = entry.Character,
                EncounterId = entry.Encounter,
                Won = false,
                Survives = false,
                Boundary = "Error",
                HardErrors = 1,
                HardError = $"{rootEx.GetType().Name}: {rootEx.Message}",
            };
            return new PlayerCorpusCaseResult(
                entry, errOutcome, PlayerVerdict.Error, PlayerVerdict.Error,
                0, 0.0, 0.0, startMatched, readyMatched, restoreMs,
                PlayerDeathSaveUseCount: 0,
                BossHpRelief: BossHpRelief.None,
                SolverCanonicalV: null,
                PlayerCanonicalV: null,
                VsPlayerCanonicalV: PlayerVerdict.Error);
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }
    }

    private static PreparedBundle LoadBundle(string inputPath, CuratedCorpusCase? manifestOverride = null)
    {
        string rootDir;
        string? tempDir = null;

        if (Directory.Exists(inputPath))
        {
            rootDir = Path.GetFullPath(inputPath);
        }
        else if (File.Exists(inputPath) && inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            tempDir = Path.Combine(Path.GetTempPath(), "combatsolver-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            using ZipArchive archive = CheckpointArchive.OpenValidated(inputPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                string rel = entry.FullName;
                CheckpointArchive.ValidateEntryPath(rel);
                string dst = Path.GetFullPath(Path.Combine(tempDir, rel));
                if (!dst.StartsWith(tempDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"unsafe_destination:{rel}");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using Stream src = entry.Open();
                using FileStream outFs = new(dst, FileMode.CreateNew, FileAccess.Write);
                src.CopyTo(outFs);
            }
            rootDir = tempDir;
        }
        else
        {
            throw new InvalidDataException($"不支持的回放包格式：{inputPath}");
        }

        try
        {
            string indexPath = File.Exists(Path.Combine(rootDir, CheckpointArchive.IndexPath))
                ? Path.Combine(rootDir, CheckpointArchive.IndexPath)
                : File.Exists(Path.Combine(rootDir, CheckpointArchive.LegacyIndexPath))
                    ? Path.Combine(rootDir, CheckpointArchive.LegacyIndexPath)
                    : throw new FileNotFoundException("回放包缺少 replay/checkpoint.json。", rootDir);

            JsonObject index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject
                ?? throw new InvalidDataException("checkpoint.json 不是有效 JSON 对象。");
            int schemaVer = index["schemaVersion"]?.GetValue<int>() ?? 0;
            if (schemaVer != 2)
                throw new InvalidDataException($"仅支持 schemaVersion=2 检查点包，收到：{schemaVer}");

            JsonObject recording = index["recording"] as JsonObject
                ?? throw new InvalidDataException("回放包缺少 recording 段（需要 native_events_v1 原生录制）。");
            if (recording["complete"]?.GetValue<bool>() != true)
                throw new InvalidDataException($"原生录制不完整：{recording["incompleteReason"]?.GetValue<string>() ?? "incomplete"}");

            string originPath = ResolveBundleRelative(rootDir, recording["originPath"]?.GetValue<string>());
            string originSavePath = ResolveBundleRelative(rootDir, recording["runSavePath"]?.GetValue<string>());
            string eventsPath = ResolveBundleRelative(rootDir, recording["eventsPath"]?.GetValue<string>());

            RecordedOriginJson origin = JsonSerializer.Deserialize<RecordedOriginJson>(
                File.ReadAllText(originPath), ReadJsonOptions)
                ?? throw new InvalidDataException("无法解析 recording/origin.json。");

            RecordedCombatEvent[] events = File.ReadLines(eventsPath)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => JsonSerializer.Deserialize<RecordedCombatEvent>(line, ReadJsonOptions)
                    ?? throw new InvalidDataException("events.jsonl 包含无效事件行。"))
                .ToArray();
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i].Sequence != i)
                    throw new InvalidDataException($"recording_sequence_gap:{i}!=actual({events[i].Sequence})");
            }

            JsonArray rawCheckpoints = index["checkpoints"] as JsonArray
                ?? throw new InvalidDataException("checkpoint.json 缺少 checkpoints 数组。");
            List<BundleCheckpointSummary> summaries = new(rawCheckpoints.Count);
            foreach (JsonObject cpNode in rawCheckpoints.OfType<JsonObject>())
            {
                string cid = cpNode["checkpointId"]?.GetValue<string>() ?? string.Empty;
                string label = cpNode["label"]?.GetValue<string>() ?? string.Empty;
                int evCursor = (int)(cpNode["eventCursor"]?.GetValue<long>() ?? 0L);
                bool canSearch = cpNode["canSearch"]?.GetValue<bool>() == true;
                string? metaPath = TryResolveBundleRelative(rootDir, cpNode["metadataPath"]?.GetValue<string>());
                string? rsPath = TryResolveBundleRelative(rootDir, cpNode["replayStatePath"]?.GetValue<string>());
                string? nativePath = TryResolveBundleRelative(rootDir, cpNode["nativeStatePath"]?.GetValue<string>());
                string? runPath = TryResolveBundleRelative(rootDir, cpNode["runStatePath"]?.GetValue<string>());
                string? expectedStamp = null;
                if (metaPath != null && File.Exists(metaPath))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    if (doc.RootElement.TryGetProperty("exactContinuationState", out JsonElement stEl))
                        expectedStamp = stEl.GetString();
                }
                summaries.Add(new BundleCheckpointSummary(
                    cid, label, evCursor, canSearch, metaPath, rsPath, nativePath, runPath, expectedStamp));
            }

            string? startId = index["combatStartCheckpointId"]?.GetValue<string>();
            BundleCheckpointSummary startCp = summaries.FirstOrDefault(c => c.CheckpointId == startId)
                ?? summaries.FirstOrDefault(static c => c.Label == "combat_start")
                ?? throw new InvalidDataException("回放包缺少 combat_start 检查点。");
            BundleCheckpointSummary? readyCp = summaries.FirstOrDefault(static c => c.CanSearch);

            string? reportPath = File.Exists(Path.Combine(rootDir, "report.json"))
                ? Path.Combine(rootDir, "report.json")
                : File.Exists(Path.Combine(rootDir, "combat-solver", "report.json"))
                    ? Path.Combine(rootDir, "combat-solver", "report.json")
                    : null;
            JsonObject? reportJson = reportPath != null
                ? JsonNode.Parse(File.ReadAllText(reportPath)) as JsonObject
                : null;

            BundleGroundTruth groundTruth = ExtractGroundTruth(
                inputPath, index, reportJson, originSavePath, startCp, manifestOverride);
            bool modelHashMatches = origin.ModelIdHash == ModelIdSerializationCache.Hash;
            PlayerBundleTelemetry playerTelemetry = ExtractPlayerBundleTelemetry(
                rootDir, index, originSavePath, startCp, summaries, events, modelHashMatches, groundTruth);
            groundTruth = groundTruth with { PlayerDeathSaveUseCount = playerTelemetry.DeathSaveUseCount };

            return new PreparedBundle(tempDir)
            {
                RootDirectory = rootDir,
                SourcePath = inputPath,
                Format = "checkpoint_v2_native_events_v1",
                CheckpointIndex = index,
                ReportJson = reportJson,
                Origin = origin,
                OriginSavePath = originSavePath,
                Events = events,
                StartCheckpoint = startCp,
                ReadyCheckpoint = readyCp,
                AllCheckpoints = summaries,
                GroundTruth = groundTruth,
                PlayerTelemetry = playerTelemetry,
            };
        }
        catch
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* ignore cleanup failure */ }
            }
            throw;
        }
    }

    private static CombatState RestoreBundleToTurn1Playable(
        MainLoopContext loop,
        PreparedBundle bundle,
        out RestoreVerification verification)
    {
        Stopwatch sw = Stopwatch.StartNew();
        RealCombatDriver.CleanupActiveRun();
        BattleDamageTracker.Reset();

        SerializableRun save = JsonSerializer.Deserialize(
            File.ReadAllText(bundle.OriginSavePath),
            JsonSerializationUtility.GetTypeInfo<SerializableRun>())
            ?? throw new InvalidDataException("origin.save 反序列化为空。");
        if (save.Players.Count != 1)
            throw new InvalidDataException($"仅支持单人存档，收到玩家数={save.Players.Count}");

        RunState runState = RunState.FromSerializable(save);
        loop.RunUntilCompleted(
            RunManager.Instance.SetUpSavedSingleplayer(runState, save),
            TimeSpan.FromSeconds(30), "SetUpSavedSingleplayer");
        RunManager.Instance.ShouldSave = false;
        RunManager.Instance.Launch();
        loop.RunUntilCompleted(
            RunManager.Instance.GenerateMap(),
            TimeSpan.FromSeconds(30), "GenerateMap");

        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(bundle.Origin.NextActionId);
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(bundle.Origin.NextHookId);
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(bundle.Origin.ChoiceIds.ToList());
        RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(bundle.Origin.RewardIds.ToList());

        Player player = runState.Players.Single();
        EncounterModel encounter = ResolveEncounter(bundle.GroundTruth.EncounterId);

        bool startStampMatched = false;
        bool startNativeMatched = false;
        string? firstRestoreDiff = null;
        bool observedStart = false;

        void OnCombatBegan(CombatState startedCombat)
        {
            if (observedStart)
                return;
            observedStart = true;

            if (bundle.StartCheckpoint.RunStatePath != null && File.Exists(bundle.StartCheckpoint.RunStatePath))
                RestoreOutOfCombatRng(runState, bundle.StartCheckpoint.RunStatePath);

            if (!string.IsNullOrEmpty(bundle.StartCheckpoint.ExpectedContinuationStamp))
            {
                string actualStart = ContinuationStamp.CaptureLive(startedCombat).StateText;
                startStampMatched = StampsMatch(bundle.StartCheckpoint.ExpectedContinuationStamp, actualStart, out string? diff);
                if (!startStampMatched)
                    firstRestoreDiff ??= $"combat_start: {diff}";
            }

            if (bundle.ModelHashMatches
                && bundle.StartCheckpoint.NativeStatePath != null
                && File.Exists(bundle.StartCheckpoint.NativeStatePath))
            {
                startNativeMatched = NativeStateMatches(startedCombat, bundle.StartCheckpoint.NativeStatePath);
            }
        }

        int openingCursor = 0;
        RecordedEventChoiceSelector openingSelector = new(
            player, bundle.Events, () => openingCursor, next => openingCursor = next, syncLocalChoice: true);

        CombatManager.Instance.CombatBegan += OnCombatBegan;
        try
        {
            using (RunManager.Instance.CardSelectorStack.UseLocalSelector(openingSelector))
            {
                Task<AbstractRoom> enterTask = RunManager.Instance.EnterRoomDebug(
                    encounter.RoomType, MapPointType.Unassigned, encounter.ToMutable(), showTransition: false);
                loop.RunUntilCompleted(enterTask, TimeSpan.FromSeconds(60), "Replay EnterRoomDebug");

                CombatState combat = WaitForPlayableOrEnded(loop, TimeSpan.FromSeconds(30), () => openingSelector.LastError);

                bool readyStampMatched = false;
                bool readyNativeMatched = false;
                if (bundle.ReadyCheckpoint is { } ready && ready.EventCursor == openingCursor)
                {
                    if (!string.IsNullOrEmpty(ready.ExpectedContinuationStamp))
                    {
                        string actualReady = ContinuationStamp.CaptureLive(combat).StateText;
                        readyStampMatched = StampsMatch(ready.ExpectedContinuationStamp, actualReady, out string? diff);
                        if (!readyStampMatched)
                            firstRestoreDiff ??= $"ready({ready.Label}): {diff}";
                    }
                    if (bundle.ModelHashMatches && ready.NativeStatePath != null && File.Exists(ready.NativeStatePath))
                        readyNativeMatched = NativeStateMatches(combat, ready.NativeStatePath);
                }

                sw.Stop();
                verification = new RestoreVerification(
                    CombatStartStampMatched: startStampMatched,
                    CombatStartNativeMatched: startNativeMatched,
                    ReadyStampMatched: readyStampMatched,
                    ReadyNativeMatched: readyNativeMatched,
                    OpeningEventsReplayed: openingCursor,
                    FirstRestoreDiff: firstRestoreDiff,
                    RestoreMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2));
                return combat;
            }
        }
        finally
        {
            CombatManager.Instance.CombatBegan -= OnCombatBegan;
        }
    }

    private static LockstepAuditReport RunLockstepAudit(MainLoopContext loop, PreparedBundle bundle)
    {
        List<LockstepStepDiff> stepDiffs = [];
        int hotDiffCount = 0;
        int checkpointMismatchCount = 0;
        int actionsReplayed = 0;
        string? auditError = null;

        try
        {
            if (!bundle.ModelHashMatches)
            {
                throw new InvalidDataException(
                    $"modelIdHash 不一致（bundle={bundle.Origin.ModelIdHash}, current={ModelIdSerializationCache.Hash}），" +
                    "无法用原版 sts2.dll 解码 events.jsonl 原生事件。");
            }

            CombatState combat = RestoreBundleToTurn1Playable(loop, bundle, out RestoreVerification verify);
            Player player = LocalContext.GetMe(combat)!;

            HotRootBundle hotRoot = HotRootBuilder.BuildFromLive(combat);
            CombatHotState hot = hotRoot.RootState;
            HotColdSidecar? hotCold = hotRoot.RootCold;
            HotRules rules = hotRoot.Rules;
            HotExecScratch scratch = new();
            HotChoiceSelection[] choiceOptBuf = new HotChoiceSelection[64];

            Dictionary<int, BundleCheckpointSummary> cpByCursor = bundle.AllCheckpoints
                .Where(static c => c.CanSearch && !string.IsNullOrEmpty(c.ExpectedContinuationStamp))
                .GroupBy(static c => c.EventCursor)
                .ToDictionary(static g => g.Key, static g => g.Last());

            int cursor = verify.OpeningEventsReplayed;
            int stepIndex = 0;

            while (cursor < bundle.Events.Length && RealCombatDriver.GetLiveOutcome(combat) == HotCombatOutcome.Ongoing)
            {
                RecordedCombatEvent rec = bundle.Events[cursor];
                CombatReplayEvent decoded = DecodeReplayEvent(rec);

                if (decoded.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.ResumeAction)
                {
                    cursor++;
                    continue;
                }
                if (decoded.eventType == CombatReplayEventType.PlayerChoice)
                {
                    // Orphaned choice not consumed by a preceding action
                    cursor++;
                    continue;
                }
                if (decoded.eventType != CombatReplayEventType.GameAction || decoded.action == null)
                {
                    cursor++;
                    continue;
                }
                if (string.Equals(rec.Origin, "system", StringComparison.Ordinal))
                {
                    cursor++;
                    continue;
                }

                int turnBefore = player.PlayerCombatState!.TurnNumber;
                if (!TryBindRecordedAction(combat, player, in hot, decoded.action,
                        out RealCombatAction liveAction, out HotAction hotAction, out string actionDesc))
                {
                    // Skip non-combat / unsupported meta actions (e.g. UndoEndTurn / DiscardPotion) cleanly with note
                    stepDiffs.Add(new LockstepStepDiff(
                        StepIndex: stepIndex++,
                        EventSequence: (int)rec.Sequence,
                        TurnNumber: turnBefore,
                        ActionKind: decoded.action.GetType().Name,
                        ActionDescription: actionDesc,
                        HotMatchedLive: true,
                        ExpectedOutcome: "SkippedNonHotAction",
                        ActualOutcome: "Skipped",
                        Differences: [],
                        CheckpointIdVerified: null,
                        CheckpointStampMatched: null,
                        CheckpointFirstDiff: null));
                    cursor++;
                    continue;
                }

                int eventSeq = (int)rec.Sequence;
                cursor++;

                // 1. Execute recorded action on live sts2.dll using recorded PlayerChoice events
                RecordedEventChoiceSelector stepSelector = new(
                    player, bundle.Events, () => cursor, next => cursor = next, syncLocalChoice: false);
                HotCombatOutcome liveOutcome;
                using (RunManager.Instance.CardSelectorStack.PushSelector(player, stepSelector))
                {
                    liveOutcome = RealCombatDriver.RunRealCombatAction(
                        loop, combat, liveAction, externalErrorProbe: () => stepSelector.LastError);
                }

                // 2. Step HotCombatEngine in lockstep using the exact same choices captured from live sts2.dll
                HotParallelExpander.AssignDeterministicCardUids(ref hot);
                StepHotWithCapturedChoices(
                    ref hot, ref hotCold, rules, scratch, hotAction,
                    stepSelector.CapturedChoices, choiceOptBuf, out string? hotChoiceErr);

                actionsReplayed++;

                bool matched = hotChoiceErr == null && hot.TerminalOutcome == liveOutcome;
                string[] diffs = [];
                if (hotChoiceErr != null)
                {
                    diffs = [hotChoiceErr];
                }
                else if (hot.TerminalOutcome != liveOutcome)
                {
                    diffs = [$"terminal_outcome expected={hot.TerminalOutcome} actual={liveOutcome}"];
                }
                else if (liveOutcome == HotCombatOutcome.Ongoing)
                {
                    string hotStamp = HotContinuationStamp.CaptureFromHot(in hot, hotCold, rules);
                    string liveStamp = HotContinuationStamp.StripModStampSections(
                        ContinuationStamp.CaptureLive(combat).StateText);
                    if (!string.Equals(hotStamp, liveStamp, StringComparison.Ordinal))
                    {
                        matched = false;
                        diffs = new ContinuationStamp(hotStamp)
                            .DescribeDifferences(new ContinuationStamp(liveStamp), maximumDifferences: 8)
                            .ToArray();
                    }
                }

                if (!matched)
                {
                    hotDiffCount++;
                    // Resync hot from live so subsequent turns in the fight can also be audited in one pass
                    if (liveOutcome == HotCombatOutcome.Ongoing)
                    {
                        HotRootBundle resynced = HotRootBuilder.BuildFromLive(combat);
                        hot = resynced.RootState;
                        hotCold = resynced.RootCold;
                        rules = resynced.Rules;
                    }
                }

                // 3. Cross-check against recorded bundle checkpoint if one exists at this cursor
                string? cpVerifiedId = null;
                bool? cpMatched = null;
                string? cpDiff = null;
                if (liveOutcome == HotCombatOutcome.Ongoing && cpByCursor.TryGetValue(cursor, out BundleCheckpointSummary? cpAtCursor))
                {
                    cpVerifiedId = cpAtCursor.CheckpointId;
                    string liveFull = ContinuationStamp.CaptureLive(combat).StateText;
                    bool ok = StampsMatch(cpAtCursor.ExpectedContinuationStamp!, liveFull, out string? firstCpDiff);
                    cpMatched = ok;
                    if (!ok)
                    {
                        checkpointMismatchCount++;
                        cpDiff = firstCpDiff;
                    }
                }

                stepDiffs.Add(new LockstepStepDiff(
                    StepIndex: stepIndex++,
                    EventSequence: eventSeq,
                    TurnNumber: turnBefore,
                    ActionKind: hotAction.Kind.ToString(),
                    ActionDescription: actionDesc,
                    HotMatchedLive: matched,
                    ExpectedOutcome: hot.TerminalOutcome.ToString(),
                    ActualOutcome: liveOutcome.ToString(),
                    Differences: diffs,
                    CheckpointIdVerified: cpVerifiedId,
                    CheckpointStampMatched: cpMatched,
                    CheckpointFirstDiff: cpDiff));
            }
        }
        catch (Exception ex)
        {
            Exception root = Unwrap(ex);
            auditError = $"{root.GetType().Name}: {root.Message}";
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
        }

        return new LockstepAuditReport(
            BundlePath: bundle.SourcePath,
            CharacterId: bundle.GroundTruth.CharacterId,
            EncounterId: bundle.GroundTruth.EncounterId,
            ModelHashMatches: bundle.ModelHashMatches,
            TotalRecordedEvents: bundle.Events.Length,
            ActionsReplayed: actionsReplayed,
            HotDiffCount: hotDiffCount,
            CheckpointMismatchCount: checkpointMismatchCount,
            Error: auditError,
            Steps: stepDiffs.ToArray());
    }

    internal static CombatState RestoreBundleForBatch(
        MainLoopContext loop,
        string bundlePath,
        out string characterId,
        out string encounterId,
        out IDisposable bundleHandle)
    {
        PreparedBundle bundle = LoadBundle(bundlePath);
        try
        {
            CombatState combat = RestoreBundleToTurn1Playable(loop, bundle, out _);
            characterId = bundle.GroundTruth.CharacterId;
            encounterId = bundle.GroundTruth.EncounterId;
            bundleHandle = bundle;
            return combat;
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    internal static RootCanonicalContext CaptureRootCanonicalContext(
        CombatRootSnapshot root,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy)
    {
        HotRootBundle rootBundle = root.HotRoot;
        CombatHotState rootHotState = rootBundle.RootState;
        HotRules rules = rootBundle.Rules;
        short[] slotCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in rootHotState, rules, policy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, damage, policy, rules, in rootHotState, slotCosts);

        int slotCount = Math.Min((int)rootHotState.PotionSlotCount, HotLimits.MaxPotions);
        List<RootPotionSlotInfo> rootPotions = new(slotCount);
        for (int s = 0; s < slotCount; s++)
        {
            ushort defId = rootHotState.Potions[s];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;
            HotPotionDef pDef = rules.Potions[defId];
            short cost = (uint)s < (uint)slotCosts.Length && slotCosts[s] >= 0
                ? slotCosts[s]
                : HotPotionPolicy.ComputePotionDefCost(
                    pDef, hotPolicy.HasRenewablePotionShapedRock, hotPolicy.InitialPlayerMaxHp, hotPolicy.PotionThresholds);
            rootPotions.Add(new RootPotionSlotInfo(
                Slot: s,
                Entry: pDef.Entry,
                StrategicCostHp: cost,
                IsCombatUsable: HotPotionPolicy.IsDefCombatUsable(pDef)));
        }

        return new RootCanonicalContext(true, hotPolicy, slotCosts, rootPotions.ToArray());
    }

    private static FightOutcomeRecord SolveRestoredRoot(
        CombatState combat,
        string fightId,
        string tier,
        string group,
        string characterId,
        string encounterId,
        SolverSearchProfile resolvedProfile,
        string searchMode,
        SolverPotionPolicy potionPolicy,
        int dop,
        int budgetMs,
        out RootCanonicalContext rootContext)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            Profile = resolvedProfile,
            FixedBudget = true,
            BudgetOverrideMilliseconds = budgetMs,
            MaxDegreeOfParallelism = dop,
            PotionPolicy = potionPolicy,
            EnableTurnBoundaryRollouts = !ModRuntime.DisableTurnBoundaryRolloutsOverride,
        };

        rootContext = CaptureRootCanonicalContext(root, damage, policy);

        Stopwatch sw = Stopwatch.StartNew();
        SolverResult solved = searchMode == "Coordinator"
            ? HotSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null)
            : HotSearchCoordinator.SolveSingleBeam(root, names, damage, policy, CancellationToken.None);
        sw.Stop();

        return BenchmarkOutcomeHelper.FromSolverResult(
            fightId, tier, group, characterId, encounterId,
            root, policy, solved, sw.Elapsed.TotalMilliseconds);
    }

    private static FightOutcomeRecord SolveAndDeployBundleCombat(
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
        out RootCanonicalContext rootContext,
        List<DeployReplanDiff> replanDiffs)
    {
        Player player = LocalContext.GetMe(combat)!;
        int startHp = player.Creature.CurrentHp;
        int startMaxHp = player.Creature.MaxHp;
        int initialUnusedLizardTails = player.Relics
            .OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>()
            .Count(static t => !t.WasUsed);
        int initialFairyCount = player.PotionSlots
            .Count(static p => p != null && string.Equals(p.Id.Entry, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));

        rootContext = RootCanonicalContext.Empty;
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

        SolverResult? activePlan = null;
        const int maxTurns = 40;
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

        player.Creature.CurrentHpChanged += OnPlayerHpChanged;
        try
        {
            for (int turnLoop = 0; turnLoop < maxTurns; turnLoop++)
        {
            if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
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
                        replanDiffs.Add(new DeployReplanDiff(
                            currentTurn, "StateMismatch", diffs.FirstOrDefault() ?? "mismatch", diffs));
                    }
                    else if (activePlan.BoundaryReason == SearchBoundaryReason.None)
                    {
                        unplannedReplans++;
                        replanDiffs.Add(new DeployReplanDiff(
                            currentTurn, "ContinuationMissing",
                            $"cached_turns={activePlan.Continuations.Count}", []));
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
                    rootContext = CaptureRootCanonicalContext(root, damage, policy);
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
                fightExpandedNodes += solved.TotalExpandedNodes > 0 ? solved.TotalExpandedNodes : solved.ExpandedNodes;
                fightTransitions += solved.TotalTransitionCount > 0 ? solved.TotalTransitionCount : solved.TransitionCount;
                fightLastBoundary = solved.BoundaryReason.ToString();
                firstRouteSignature ??= BenchmarkOutcomeHelper.BuildRouteSignature(solved);
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

            bool drifted = false;
            for (int actionIdx = 0; actionIdx < actions.Count; actionIdx++)
            {
                if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                    break;
                if (player.PlayerCombatState!.TurnNumber != planTurn)
                {
                    unplannedReplans++;
                    replanDiffs.Add(new DeployReplanDiff(
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
                        replanDiffs.Add(new DeployReplanDiff(
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
                        replanDiffs.Add(new DeployReplanDiff(planTurn, "CardMissing", ex.Message, []));
                        activePlan = null;
                        drifted = true;
                        break;
                    }
                    if (!card.CanPlayTargeting(target))
                    {
                        unplannedReplans++;
                        replanDiffs.Add(new DeployReplanDiff(
                            planTurn, "CardUnplayable",
                            $"card={action.CardId} target={action.TargetCombatId?.ToString() ?? "-"} energy={player.PlayerCombatState.Energy}", []));
                        activePlan = null;
                        drifted = true;
                        break;
                    }
                    if (string.Equals(action.CardId, "ALCHEMIZE", StringComparison.Ordinal))
                        alchemizeCount++;
                    liveAction = RealCombatAction.PlayCard(card, target);
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
                catch (Exception ex) when (FindInner<NativeChoicePlanMismatchException>(ex) != null)
                {
                    choiceMismatches++;
                    throw;
                }
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
                        replanDiffs.Add(new DeployReplanDiff(
                            planTurn, "PlanExhaustedWithoutEndTurn", $"executed={actions.Count}", []));
                    }
                    activePlan = null;
                    continue;
                }

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
                catch (Exception ex) when (FindInner<NativeChoicePlanMismatchException>(ex) != null)
                {
                    choiceMismatches++;
                    throw;
                }
            }
        }
        }
        finally
        {
            player.Creature.CurrentHpChanged -= OnPlayerHpChanged;
        }

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

    private static bool TryBindRecordedAction(
        CombatState combat,
        Player player,
        in CombatHotState hot,
        INetAction netAction,
        out RealCombatAction liveAction,
        out HotAction hotAction,
        out string description)
    {
        if (netAction is NetPlayCardAction play)
        {
            CardModel card = play.card.ToCardModelOrNull()
                ?? throw new InvalidDataException($"lockstep_card_not_found:netId={play.card.CombatCardIndex} model={play.modelId}");
            IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
            int handIdx = -1;
            for (int i = 0; i < hand.Count; i++)
            {
                if (ReferenceEquals(hand[i], card))
                {
                    handIdx = i;
                    break;
                }
            }
            if (handIdx < 0)
                throw new InvalidDataException($"lockstep_card_not_in_hand:{card.Id.Entry} (netId={play.card.CombatCardIndex})");

            Creature? target = play.targetId is uint tid ? combat.GetCreature(tid) : null;
            sbyte enemySlot = FindHotEnemySlot(in hot, play.targetId);
            liveAction = RealCombatAction.PlayCard(card, target);
            hotAction = HotAction.PlayCard(handIdx, enemySlot);
            description = $"PlayCard({card.Id.Entry}+{card.CurrentUpgradeLevel}@{handIdx}, target={target?.Monster?.Id.Entry ?? "-"}#{play.targetId?.ToString() ?? "-"})";
            return true;
        }

        if (netAction is NetUsePotionAction usePot)
        {
            int slot = checked((int)usePot.potionIndex);
            PotionModel potion = player.GetPotionAtSlotIndex(slot)
                ?? throw new InvalidDataException($"lockstep_potion_slot_empty:{slot}");
            Creature? target = usePot.targetId is uint tid ? combat.GetCreature(tid) : null;
            sbyte enemySlot = FindHotEnemySlot(in hot, usePot.targetId);
            liveAction = RealCombatAction.UsePotion(potion, target);
            hotAction = HotAction.UsePotion(slot, enemySlot);
            description = $"UsePotion({potion.Id.Entry}@slot{slot}, target={target?.Monster?.Id.Entry ?? "-"}#{usePot.targetId?.ToString() ?? "-"})";
            return true;
        }

        if (netAction is NetEndPlayerTurnAction endTurn)
        {
            liveAction = RealCombatAction.EndTurn();
            hotAction = HotAction.EndTurn();
            description = $"EndTurn(T{endTurn.turnNumber})";
            return true;
        }

        liveAction = default;
        hotAction = default;
        description = netAction.ToString() ?? netAction.GetType().Name;
        return false;
    }

    private static sbyte FindHotEnemySlot(in CombatHotState hot, uint? targetCombatId)
    {
        if (targetCombatId is not uint cid)
            return -1;
        for (int i = 0; i < hot.EnemyCount; i++)
        {
            if (hot.Enemies[i].Vital.CombatId == cid)
                return (sbyte)i;
        }
        return -1;
    }

    private static void StepHotWithCapturedChoices(
        ref CombatHotState hot,
        ref HotColdSidecar? hotCold,
        HotRules rules,
        HotExecScratch scratch,
        HotAction action,
        List<CapturedChoiceRecord> capturedChoices,
        HotChoiceSelection[] choiceOptBuf,
        out string? error)
    {
        error = null;
        List<HotChoiceSelection> selections = [];
        int choiceCursor = 0;
        int pickedInChoiceCursor = 0;
        int recordSelectionStart = 0;
        bool usedForcedChoiceBypass = false;
        Span<byte> classMultiplicities = choiceOptBuf.Length <= 128
            ? stackalloc byte[choiceOptBuf.Length]
            : new byte[choiceOptBuf.Length];

        for (int guard = 0; guard < 32; guard++)
        {
            CombatHotState probe = hot;
            HotColdSidecar? probeCold = hotCold;
            HotStepResult res = HotCombatEngine.Step(
                ref probe, ref probeCold, rules, scratch, action,
                CollectionsMarshal.AsSpan(selections));

            if (!res.NeedsChoice)
            {
                if (!res.Executed)
                {
                    error = $"HotCombatEngine.Step({action.Kind}) returned Executed=false";
                    return;
                }
                if (usedForcedChoiceBypass && choiceCursor < capturedChoices.Count)
                {
                    error = $"HotCombatEngine bypassed a forced pile choice, leaving {capturedChoices.Count - choiceCursor} unconsumed recorded live choice(s).";
                    return;
                }
                hot = probe;
                hotCold = probeCold;
                return;
            }

            classMultiplicities.Clear();
            int optCount = HotCombatEngine.EnumerateChoiceOptionsWithClasses(
                in probe, probeCold, rules, scratch, choiceOptBuf, classMultiplicities);
            int rawEligibleCount = 0;
            for (int i = 0; i < optCount; i++)
            {
                if (choiceOptBuf[i].Kind != HotChoiceKind.None)
                    rawEligibleCount += classMultiplicities[i];
            }

            bool isForcedSinglePileChoice = scratch.PendingChoiceKind != HotChoiceKind.GeneratedCard
                && scratch.PendingChoiceMin > 0
                && optCount == 1
                && rawEligibleCount <= scratch.PendingChoiceMin
                && choiceOptBuf[0].Kind != HotChoiceKind.None;

            if (choiceCursor >= capturedChoices.Count)
            {
                // Vanilla CardSelectCmd auto-resolves mandatory pile choices when !RequireManualConfirmation && num <= MinSelect
                // without invoking ICardSelector, so 0 PlayerChoice events are recorded (while GeneratedCard always records).
                if (isForcedSinglePileChoice)
                {
                    selections.Add(choiceOptBuf[0]);
                    continue;
                }
                if (scratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    continue;
                }
                error = $"HotCombatEngine requested choice #{choiceCursor + 1} (opts={optCount}), but live only recorded {capturedChoices.Count} choice(s).";
                return;
            }

            CapturedChoiceRecord rec = capturedChoices[choiceCursor];
            if (isForcedSinglePileChoice && pickedInChoiceCursor == 0)
            {
                // Only bypass ahead of an unconsumed recorded prompt when that recorded prompt cannot belong to this
                // forced choice (e.g., an earlier mandatory 1-card pile choice auto-resolved before a later recorded prompt).
                // Never bypass a RequireManualConfirmation=true prompt or a filter bug collapsing live options to <= MinSelect.
                if (!RecordedChoiceMatchesHotOption(in probe, probeCold, rules, choiceOptBuf[0], rec))
                {
                    usedForcedChoiceBypass = true;
                    selections.Add(choiceOptBuf[0]);
                    continue;
                }
                if (rec.OptionEntries.Length > rawEligibleCount)
                {
                    error = $"HotCombatEngine choice filter collapsed options to {rawEligibleCount} (<= min={scratch.PendingChoiceMin}), but live prompt offered {rec.OptionEntries.Length} option(s).";
                    return;
                }
            }

            if (rec.SelectedEntries.Length == 0)
            {
                selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                choiceCursor++;
                pickedInChoiceCursor = 0;
                continue;
            }

            if (pickedInChoiceCursor >= rec.SelectedEntries.Length)
            {
                if (scratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    choiceCursor++;
                    pickedInChoiceCursor = 0;
                    continue;
                }
                choiceCursor++;
                pickedInChoiceCursor = 0;
                continue;
            }

            if (pickedInChoiceCursor == 0)
                recordSelectionStart = selections.Count;
            ReadOnlySpan<HotChoiceSelection> priorPicksInRecord = CollectionsMarshal.AsSpan(selections).Slice(recordSelectionStart);

            string wantEntry = rec.SelectedEntries[pickedInChoiceCursor];
            int wantUpgrade = rec.SelectedUpgrades[pickedInChoiceCursor];
            string wantStateKey = rec.SelectedStateKeys[pickedInChoiceCursor];
            int wantOptionIdx = rec.SelectedIndices[pickedInChoiceCursor];
            pickedInChoiceCursor++;
            if (pickedInChoiceCursor >= rec.SelectedEntries.Length)
            {
                choiceCursor++;
                pickedInChoiceCursor = 0;
            }

            if (!TryMatchHotChoiceOption(
                    in probe, probeCold, rules, choiceOptBuf.AsSpan(0, optCount),
                    rec, wantEntry, wantUpgrade, wantStateKey, wantOptionIdx,
                    priorPicksInRecord, out HotChoiceSelection matched))
            {
                error = $"HotCombatEngine choice options ({optCount}) did not contain live-selected {wantEntry}+{wantUpgrade} (optIdx={wantOptionIdx}).";
                return;
            }
            selections.Add(matched);
        }

        error = "HotCombatEngine choice resolution exceeded 32 iterations.";
    }

    private static bool TryMatchHotChoiceOption(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        ReadOnlySpan<HotChoiceSelection> options,
        CapturedChoiceRecord rec,
        string wantEntry,
        int wantUpgrade,
        string wantStateKey,
        int wantOptionIdx,
        ReadOnlySpan<HotChoiceSelection> priorPicksInRecord,
        out HotChoiceSelection matched)
    {
        // 1. Exact GeneratedCard index + entry match
        for (int i = 0; i < options.Length; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind == HotChoiceKind.GeneratedCard
                && opt.CardUidOrIndex == wantOptionIdx
                && opt.CardDefId < rules.Cards.Length
                && string.Equals(rules.Cards[opt.CardDefId].Entry, wantEntry, StringComparison.Ordinal))
            {
                matched = opt;
                return true;
            }
        }

        // 2. Exact StateKey match for pile cards (re-resolving canonical deduplication to the exact live copy)
        if (!string.IsNullOrEmpty(wantStateKey))
        {
            for (int i = 0; i < options.Length; i++)
            {
                HotChoiceSelection opt = options[i];
                if (opt.Kind is HotChoiceKind.None or HotChoiceKind.GeneratedCard)
                    continue;
                int cardIdx = ResolveHotChoiceCardIndex(in probe, opt);
                if (cardIdx >= 0)
                {
                    ref readonly HotCard hc = ref probe.Cards[cardIdx];
                    string key = HotContinuationStamp.FormatChoiceCardKey(in hc, probeCold, rules);
                    if (string.Equals(key, wantStateKey, StringComparison.Ordinal))
                    {
                        matched = ResolveExactPileCopySelection(
                            in probe, probeCold, rules, opt, rec, wantOptionIdx,
                            wantStateKey, priorPicksInRecord);
                        return true;
                    }
                }
            }
        }

        // 3. Entry + UpgradeLevel match (stay on the already-filtered canonical option to avoid picking ineligible/excluded copies)
        for (int i = 0; i < options.Length; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind == HotChoiceKind.None || opt.CardDefId >= rules.Cards.Length)
                continue;
            if (!string.Equals(rules.Cards[opt.CardDefId].Entry, wantEntry, StringComparison.Ordinal))
                continue;
            if (opt.Kind == HotChoiceKind.GeneratedCard)
            {
                matched = opt;
                return true;
            }
            int cardIdx = ResolveHotChoiceCardIndex(in probe, opt);
            if (cardIdx >= 0 && probe.Cards[cardIdx].UpgradeLevel == wantUpgrade)
            {
                matched = opt;
                return true;
            }
        }

        // 4. Entry-only fallback
        for (int i = 0; i < options.Length; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind != HotChoiceKind.None
                && opt.CardDefId < rules.Cards.Length
                && string.Equals(rules.Cards[opt.CardDefId].Entry, wantEntry, StringComparison.Ordinal))
            {
                matched = opt;
                return true;
            }
        }

        matched = default;
        return false;
    }

    private static HotChoiceSelection ResolveExactPileCopySelection(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotChoiceSelection canonicalOpt,
        CapturedChoiceRecord rec,
        int wantOptionIdx,
        string wantStateKey,
        ReadOnlySpan<HotChoiceSelection> priorPicksInRecord)
    {
        // EnumerateChoiceOptions intentionally deduplicates identical pile cards to the lowest-CardUid copy.
        // For Hand/Discard/Exhaust (whose CardSelectCmd order matches Hot pile order), map the live option's
        // occurrence ordinal to the matching eligible, non-excluded CardUid so multiset-identical lockstep slot orders align.
        HotPileKind? pile = canonicalOpt.Kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => null,
        };
        if (pile is not HotPileKind targetPile)
            return canonicalOpt;

        if ((uint)wantOptionIdx >= (uint)rec.OptionStateKeys.Length)
            return canonicalOpt;

        int ordinal = 0;
        for (int j = 0; j < wantOptionIdx; j++)
        {
            if (string.Equals(rec.OptionStateKeys[j], wantStateKey, StringComparison.Ordinal))
                ordinal++;
        }

        ReadOnlySpan<ushort> allowedUids = HotCombatEngine.GetPendingFilteredChoiceUids();
        ReadOnlySpan<ushort> excludedUids = HotCombatEngine.GetPendingExcludedChoiceUids();

        int zoneStart = HotPiles.ZoneStart(in probe, targetPile);
        int zoneCount = HotPiles.ZoneCount(in probe, targetPile);
        int seen = 0;
        for (int offset = 0; offset < zoneCount; offset++)
        {
            ref readonly HotCard hc = ref probe.Cards[zoneStart + offset];
            if (hc.CardDefId == 0 || hc.CardDefId >= rules.Cards.Length)
                continue;
            if (hc.CardDefId != canonicalOpt.CardDefId)
                continue;
            if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, hc.CardUid))
                continue;
            if (!string.Equals(HotContinuationStamp.FormatChoiceCardKey(in hc, probeCold, rules), wantStateKey, StringComparison.Ordinal))
                continue;

            if (seen == ordinal)
            {
                if (hc.CardUid != 0
                    && (excludedUids.IsEmpty || !ContainsUid(excludedUids, hc.CardUid))
                    && !ContainsSelectionUid(priorPicksInRecord, canonicalOpt.Kind, hc.CardUid))
                {
                    return new HotChoiceSelection(canonicalOpt.Kind, hc.CardUid, hc.CardDefId);
                }
                return canonicalOpt;
            }
            seen++;
        }

        return canonicalOpt;
    }

    private static bool RecordedChoiceMatchesHotOption(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotChoiceSelection opt,
        CapturedChoiceRecord rec)
    {
        if (rec.OptionEntries.Length == 0)
            return false;
        if (opt.CardDefId == 0 || opt.CardDefId >= rules.Cards.Length)
            return true;

        int cardIdx = ResolveHotChoiceCardIndex(in probe, opt);
        if (cardIdx < 0)
            return true;

        ref readonly HotCard hc = ref probe.Cards[cardIdx];
        string hotKey = HotContinuationStamp.FormatChoiceCardKey(in hc, probeCold, rules);
        for (int i = 0; i < rec.OptionStateKeys.Length; i++)
        {
            if (string.Equals(rec.OptionStateKeys[i], hotKey, StringComparison.Ordinal))
                return true;
        }

        string hotEntry = rules.Cards[opt.CardDefId].Entry;
        bool checkUpgrades = rec.OptionUpgrades.Length == rec.OptionEntries.Length;
        for (int i = 0; i < rec.OptionEntries.Length; i++)
        {
            if (string.Equals(rec.OptionEntries[i], hotEntry, StringComparison.Ordinal)
                && (!checkUpgrades || rec.OptionUpgrades[i] == hc.UpgradeLevel))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUid(ReadOnlySpan<ushort> uids, ushort uid)
    {
        for (int i = 0; i < uids.Length; i++)
        {
            if (uids[i] == uid)
                return true;
        }
        return false;
    }

    private static bool ContainsSelectionUid(
        ReadOnlySpan<HotChoiceSelection> selections,
        HotChoiceKind kind,
        ushort uid)
    {
        for (int i = 0; i < selections.Length; i++)
        {
            if (selections[i].Kind == kind && selections[i].CardUidOrIndex == uid)
                return true;
        }
        return false;
    }

    private static int ResolveHotChoiceCardIndex(in CombatHotState probe, HotChoiceSelection opt)
    {
        HotPileKind pile = opt.Kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };
        int idx = HotCombatEngine.FindCardIndexByUid(in probe, pile, opt.CardUidOrIndex);
        return idx >= 0 ? idx : HotCombatEngine.FindCardIndexByUidAnyPile(in probe, opt.CardUidOrIndex);
    }

    /// <summary>
    /// Strict Win-first + DeathSave-preservation + HP loss + Potion cost comparison against a target outcome (Player or Old Solver).
    /// Matches <see cref="HotRouteQuality.Compare"/> Step 1 (Win) and Step 3 (preserve one-shot death saves),
    /// then uses exact Pareto dominance and unified economic cost (<c>HpLoss + PotionCostHp</c>) on tradeoffs.
    /// </summary>
    private static PlayerVerdict EvaluateAgainstTarget(
        FightOutcomeRecord solver,
        bool targetWon,
        int targetHpLoss,
        int targetPotionsUsed,
        short[] rootSlotCosts,
        out int targetPotionCostEstimateHp,
        out double solverEffectiveCostHp,
        out double targetEffectiveCostHp,
        int targetDeathSaveUseCount = 0)
    {
        // Target corpus ground truth (Player / Old Solver) records gross projectedBattleHpLost,
        // so align Raw comparison on GrossHpLoss ?? HpLoss while canonical V handles net HP/healing/death saves.
        int solverHpLoss = solver.GrossHpLoss ?? solver.HpLoss;
        targetPotionCostEstimateHp = EstimatePotionCostHp(targetPotionsUsed, rootSlotCosts);
        solverEffectiveCostHp = solverHpLoss + solver.PotionCostHp;
        targetEffectiveCostHp = targetHpLoss + targetPotionCostEstimateHp;

        if (solver.HardErrors > 0 || !string.IsNullOrEmpty(solver.HardError))
            return PlayerVerdict.Error;

        if (solver.Won != targetWon)
            return solver.Won ? PlayerVerdict.BeatPlayer : PlayerVerdict.BehindPlayer;

        if (!solver.Won)
        {
            return solverHpLoss < targetHpLoss ? PlayerVerdict.BeatPlayer
                : solverHpLoss > targetHpLoss ? PlayerVerdict.BehindPlayer
                : PlayerVerdict.TiedPlayer;
        }

        // Preserve one-shot death saves (Lizard Tail / Fairy in a Bottle), matching HotRouteQuality.Compare Step 3:
        // a solver victory that burns more death saves than the target never beats or ties a clean target victory.
        if (solver.DeathSaveUseCount != targetDeathSaveUseCount)
            return solver.DeathSaveUseCount < targetDeathSaveUseCount
                ? PlayerVerdict.BeatPlayer
                : PlayerVerdict.BehindPlayer;

        // Exact tie on both HP loss and potions used
        if (solverHpLoss == targetHpLoss && solver.PotionsUsed == targetPotionsUsed)
            return PlayerVerdict.TiedPlayer;

        // Strict Pareto dominance (fewer/equal HP loss AND fewer/equal potions)
        if (solverHpLoss <= targetHpLoss && solver.PotionsUsed <= targetPotionsUsed)
            return PlayerVerdict.BeatPlayer;
        if (solverHpLoss >= targetHpLoss && solver.PotionsUsed >= targetPotionsUsed)
            return PlayerVerdict.BehindPlayer;

        // HP-vs-Potion tradeoff: compare unified economic cost (HP loss + strategic potion cost)
        if (solverEffectiveCostHp < targetEffectiveCostHp)
            return PlayerVerdict.BeatPlayer;
        if (solverEffectiveCostHp > targetEffectiveCostHp)
            return PlayerVerdict.BehindPlayer;
        return PlayerVerdict.TiedPlayer;
    }

    private static int EstimatePotionCostHp(int potionsUsed, short[] rootSlotCosts)
    {
        if (potionsUsed <= 0)
            return 0;
        int[] positiveCosts = rootSlotCosts.Where(static c => c > 0).OrderBy(static c => c).Select(static c => (int)c).ToArray();
        if (positiveCosts.Length == 0)
            return potionsUsed * HotSearchConstants.PotionMinimumHpSaved;

        // Use the cheapest available root potion costs (conservative lower bound on player potion cost)
        int total = 0;
        for (int i = 0; i < potionsUsed; i++)
            total += i < positiveCosts.Length ? positiveCosts[i] : HotSearchConstants.PotionMinimumHpSaved;
        return total;
    }

    /// <summary>
    /// Objective-aligned auxiliary canonical <c>V</c> comparison matching <see cref="HotRouteQuality"/>
    /// (<see cref="HotRouteQuality.PersistentHpScale"/> for <see cref="BossHpRelief"/>, monotone post-combat
    /// relic healing, <see cref="HotRouteQuality.DeathSavePremium"/>, renewable <c>PotionShapedRock</c> = 0,
    /// passive <c>FairyInABottle</c> excluded from active potion cost, and
    /// <see cref="HotPotionPolicy.ComputeNetPotionOpportunityCostCenti"/>).
    /// </summary>
    private static PlayerVerdict? EvaluateCanonicalVAgainstPlayer(
        FightOutcomeRecord solver,
        bool playerWon,
        int playerHpLoss,
        int playerPotionsUsed,
        in RootCanonicalContext rootContext,
        PlayerBundleTelemetry playerTelemetry,
        out double? solverCanonicalV,
        out double? playerCanonicalV)
    {
        if (solver.HardErrors > 0 || !string.IsNullOrEmpty(solver.HardError))
        {
            solverCanonicalV = null;
            playerCanonicalV = null;
            return PlayerVerdict.Error;
        }

        if (!rootContext.HasPolicy)
        {
            solverCanonicalV = solver.EconomicValueV;
            playerCanonicalV = null;
            return null;
        }

        HotSearchPolicy hotPolicy = rootContext.Policy;
        long solverVCenti = solver.EconomicValueCenti;
        solverCanonicalV = Math.Round(solverVCenti / (double)HotSearchConstants.HpFixedScale, 2);

        int initialHp = hotPolicy.InitialPlayerHp;
        int initialMaxHp = Math.Max(1, hotPolicy.InitialPlayerMaxHp);
        int deathSaveHp = Math.Max(0, playerTelemetry.DeathSaveHpRestored);
        int endMaxHp = playerTelemetry.LatestObservedMaxHp > 0
            ? playerTelemetry.LatestObservedMaxHp
            : initialMaxHp;

        int endHp;
        if (!playerWon)
        {
            endHp = 0;
        }
        else if (playerTelemetry.TelemetryFinalHp.HasValue)
        {
            endHp = Math.Max(1, playerTelemetry.TelemetryFinalHp.Value);
        }
        else
        {
            endHp = Math.Clamp(initialHp - playerHpLoss + deathSaveHp, 1, Math.Max(endMaxHp, initialMaxHp));
        }

        if (endHp > endMaxHp)
            endMaxHp = endHp;

        int postCombatHeal = HotRouteQuality.RankedPostCombatRelicHeal(
            hotPolicy.PostCombatRelicHeal, playerWon, endHp, endMaxHp);
        int playerNetHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: endHp,
            postCombatMonotoneHeal: postCombatHeal,
            initialPlayerHp: initialHp,
            endPlayerMaxHp: endMaxHp,
            initialPlayerMaxHp: initialMaxHp,
            deathSaveHpRestored: deathSaveHp,
            growthHpCredit: 0,
            bossHpRelief: hotPolicy.StrategicBossHpRelief,
            ignoreLongTermRewards: hotPolicy.IgnoreLongTermRewards);

        int playerNetPotionCostCenti = ComputePlayerCanonicalPotionCostCenti(
            playerPotionsUsed, in rootContext, playerTelemetry);
        long playerVCenti = (long)playerNetHpDeltaCenti - playerNetPotionCostCenti;
        playerCanonicalV = Math.Round(playerVCenti / (double)HotSearchConstants.HpFixedScale, 2);

        if (solver.Won != playerWon)
            return solver.Won ? PlayerVerdict.BeatPlayer : PlayerVerdict.BehindPlayer;

        if (!solver.Won)
        {
            int solverHpLoss = solver.GrossHpLoss ?? solver.HpLoss;
            return solverHpLoss < playerHpLoss ? PlayerVerdict.BeatPlayer
                : solverHpLoss > playerHpLoss ? PlayerVerdict.BehindPlayer
                : PlayerVerdict.TiedPlayer;
        }

        if (solver.DeathSaveUseCount != playerTelemetry.DeathSaveUseCount)
            return solver.DeathSaveUseCount < playerTelemetry.DeathSaveUseCount
                ? PlayerVerdict.BeatPlayer
                : PlayerVerdict.BehindPlayer;

        if (solverVCenti != playerVCenti)
            return solverVCenti > playerVCenti
                ? PlayerVerdict.BeatPlayer
                : PlayerVerdict.BehindPlayer;

        if (solver.NetPersistentHpDeltaCenti != playerNetHpDeltaCenti)
            return solver.NetPersistentHpDeltaCenti > playerNetHpDeltaCenti
                ? PlayerVerdict.BeatPlayer
                : PlayerVerdict.BehindPlayer;

        if (solver.PotionsUsed != playerPotionsUsed)
            return solver.PotionsUsed < playerPotionsUsed
                ? PlayerVerdict.BeatPlayer
                : PlayerVerdict.BehindPlayer;

        return PlayerVerdict.TiedPlayer;
    }

    private static int ComputePlayerCanonicalPotionCostCenti(
        int playerPotionsUsed,
        in RootCanonicalContext rootContext,
        PlayerBundleTelemetry playerTelemetry)
    {
        // Passive Fairy in a Bottle triggers are priced via DeathSaveUseCount/DeathSavePremium, not active potion cost.
        int explicitPotionsUsed = Math.Max(0, playerPotionsUsed - playerTelemetry.FairyUseCount);
        if (explicitPotionsUsed <= 0)
            return 0;

        Dictionary<int, RootPotionSlotInfo> usableBySlot = new();
        for (int i = 0; i < rootContext.RootPotions.Length; i++)
        {
            RootPotionSlotInfo info = rootContext.RootPotions[i];
            if (info.IsCombatUsable)
                usableBySlot[info.Slot] = info;
        }

        HashSet<int> usedSlots = [];
        int paidRawHpCost = 0;
        int paidCount = 0;
        int consumedTotal = 0;

        // 1. Prefer exact root potion slots observed consumed in events / checkpoints / plannedActions.
        for (int i = 0; i < playerTelemetry.ConsumedRootPotionSlots.Length && consumedTotal < explicitPotionsUsed; i++)
        {
            int slot = playerTelemetry.ConsumedRootPotionSlots[i];
            if (!usableBySlot.TryGetValue(slot, out RootPotionSlotInfo info) || !usedSlots.Add(slot))
                continue;
            consumedTotal++;
            if (info.StrategicCostHp > 0)
            {
                paidRawHpCost += info.StrategicCostHp;
                paidCount++;
            }
        }

        // 2. If more explicit potions were used than observed root slots, assign cheapest remaining root slots
        //    (including 0-cost Token / renewable PotionShapedRock). Uses beyond root slots are combat-generated (0 cost).
        if (consumedTotal < explicitPotionsUsed)
        {
            foreach (RootPotionSlotInfo info in usableBySlot.Values
                .Where(s => !usedSlots.Contains(s.Slot))
                .OrderBy(static s => s.StrategicCostHp)
                .ThenBy(static s => s.Slot))
            {
                if (consumedTotal >= explicitPotionsUsed)
                    break;
                usedSlots.Add(info.Slot);
                consumedTotal++;
                if (info.StrategicCostHp > 0)
                {
                    paidRawHpCost += info.StrategicCostHp;
                    paidCount++;
                }
            }
        }

        HotSearchPolicy hotPolicy = rootContext.Policy;
        int endEmptySlots = Math.Min(
            (int)hotPolicy.RootPotionSlotCount,
            hotPolicy.EmptyRootPotionSlots + consumedTotal);
        return HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            paidRawHpCost,
            paidCount,
            playerTelemetry.AlchemizePlayCount,
            endEmptySlots,
            in hotPolicy);
    }

    private static void PrintLockstepSummary(LockstepAuditReport audit)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"=== Lockstep Recorded-Actions Audit ({audit.CharacterId} vs {audit.EncounterId}) ===");
        Console.WriteLine(
            $"events={audit.TotalRecordedEvents} actions_replayed={audit.ActionsReplayed} " +
            $"hot_diffs={audit.HotDiffCount} checkpoint_mismatches={audit.CheckpointMismatchCount} " +
            $"error={audit.Error ?? "none"}");

        foreach (LockstepStepDiff step in audit.Steps)
        {
            string status = step.HotMatchedLive ? "OK  " : "DIFF";
            string cpTag = step.CheckpointIdVerified == null
                ? string.Empty
                : $" [cp={(step.CheckpointStampMatched == true ? "OK" : "DIFF")}]";
            if (!step.HotMatchedLive || step.CheckpointStampMatched == false)
            {
                Console.WriteLine($"  [{status}] step#{step.StepIndex} ev#{step.EventSequence} T{step.TurnNumber} {step.ActionDescription}{cpTag}");
                foreach (string d in step.Differences)
                    Console.WriteLine($"         hot_vs_live: {d}");
                if (!string.IsNullOrEmpty(step.CheckpointFirstDiff))
                    Console.WriteLine($"         live_vs_cp:  {step.CheckpointFirstDiff}");
            }
        }
    }

    private static void PrintSingleBundleComparison(
        BundleGroundTruth gt,
        FightOutcomeRecord solver,
        in RootCanonicalContext rootContext,
        PlayerBundleTelemetry playerTelemetry,
        List<DeployReplanDiff> deployDiffs)
    {
        short[] rootSlotCosts = rootContext.RootSlotCosts;
        double? solverCanonicalV = null;
        double? playerCanonicalV = null;
        PlayerVerdict? vsPlayerCanonicalV = gt.HasPlayerOutcome
            ? EvaluateCanonicalVAgainstPlayer(
                solver, gt.PlayerWon, gt.PlayerHpLoss, gt.PlayerPotionsUsed,
                in rootContext, playerTelemetry,
                out solverCanonicalV, out playerCanonicalV)
            : null;

        Console.WriteLine();
        Console.WriteLine("### Single-Bundle Comparison");
        Console.WriteLine("| Competitor | Won | HP Loss | Potions | Pot Cost | Effective Cost | V | Turns | ms | Replans | Boundary |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        if (gt.HasOldSolverOutcome)
        {
            int oldPotCost = EstimatePotionCostHp(gt.OldSolverPotionsUsed, rootSlotCosts);
            Console.WriteLine(
                $"| Old Solver ({gt.OldSolverVersion}) | {(gt.OldSolverWon ? "W" : "L")} | {gt.OldSolverHpLoss} | " +
                $"{gt.OldSolverPotionsUsed} | ~{oldPotCost} | ~{gt.OldSolverHpLoss + oldPotCost} | - | {gt.OldSolverTurns} | - | - | - |");
        }
        if (gt.HasPlayerOutcome)
        {
            int playerPotCost = EstimatePotionCostHp(gt.PlayerPotionsUsed, rootSlotCosts);
            string playerVStr = playerCanonicalV.HasValue
                ? playerCanonicalV.Value.ToString("F2", CultureInfo.InvariantCulture)
                : "-";
            Console.WriteLine(
                $"| Player Recorded | {(gt.PlayerWon ? "W" : "L")} | {gt.PlayerHpLoss} | " +
                $"{gt.PlayerPotionsUsed} | ~{playerPotCost} | ~{gt.PlayerHpLoss + playerPotCost} | {playerVStr} | {gt.PlayerTurns} | - | - | - |");
        }
        Console.WriteLine(
            $"| **Current Solver** | **{(solver.HardErrors > 0 ? "ERR" : solver.Won ? "W" : "L")}** | **{solver.HpLoss}** | " +
            $"**{solver.PotionsUsed}** | **{solver.PotionCostHp}** | **{solver.HpLoss + solver.PotionCostHp}** | " +
            $"**{solver.EconomicValueV:F2}** | **{solver.Turns}** | **{solver.ElapsedMs:F1}** | **{solver.UnplannedReplans}** | **{solver.Boundary}** |");

        if (gt.HasPlayerOutcome)
        {
            PlayerVerdict vsPlayer = EvaluateAgainstTarget(
                solver, gt.PlayerWon, gt.PlayerHpLoss, gt.PlayerPotionsUsed,
                rootSlotCosts, out _, out _, out _,
                targetDeathSaveUseCount: gt.PlayerDeathSaveUseCount);
            Console.WriteLine(
                $"Verdict vs Player (Raw): {vsPlayer} (DeathSave S={solver.DeathSaveUseCount} vs P={gt.PlayerDeathSaveUseCount})");
            if (vsPlayerCanonicalV.HasValue && solverCanonicalV.HasValue && playerCanonicalV.HasValue)
            {
                BossHpRelief relief = rootContext.HasPolicy ? rootContext.Policy.StrategicBossHpRelief : BossHpRelief.None;
                Console.WriteLine(
                    $"Verdict vs Player (Canonical V, {relief}): {vsPlayerCanonicalV.Value} " +
                    $"(S={solverCanonicalV.Value:F2} vs P={playerCanonicalV.Value:F2}, " +
                    $"delta={(solverCanonicalV.Value - playerCanonicalV.Value):+0.00;-0.00;0.00})");
            }
        }
        if (deployDiffs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"### Deploy Replans ({deployDiffs.Count})");
            foreach (DeployReplanDiff rd in deployDiffs)
            {
                Console.WriteLine($"  T{rd.Turn} [{rd.Cause}]: {rd.FirstDiff}");
                foreach (string d in rd.Differences.Skip(1))
                    Console.WriteLine($"    -> {d}");
            }
        }
    }

    private static void PrintPlayerCorpusScoreboard(
        IReadOnlyList<PlayerCorpusCaseResult> results,
        BenchmarkSummaryRecord overall,
        string dumpOutcomesPath)
    {
        static string FormatScoreRow(string label, IReadOnlyList<PlayerCorpusCaseResult> subset)
        {
            int n = subset.Count;
            int beat = subset.Count(static r => r.VerdictVsPlayer == PlayerVerdict.BeatPlayer);
            int tied = subset.Count(static r => r.VerdictVsPlayer == PlayerVerdict.TiedPlayer);
            int behind = subset.Count(static r => r.VerdictVsPlayer == PlayerVerdict.BehindPlayer);
            int errs = subset.Count(static r => r.VerdictVsPlayer == PlayerVerdict.Error);
            int beatOrTie = beat + tied;
            double beatOrTiePct = n > 0 ? (double)beatOrTie / n : 0.0;
            int beatOld = subset.Count(static r => r.VerdictVsOldSolver == PlayerVerdict.BeatPlayer);
            int tiedOld = subset.Count(static r => r.VerdictVsOldSolver == PlayerVerdict.TiedPlayer);
            var valid = subset.Where(static r => r.VerdictVsPlayer != PlayerVerdict.Error).ToList();
            double avgSolverHp = valid.Count > 0 ? valid.Average(static r => (double)r.Outcome.HpLoss) : 0.0;
            double avgPlayerHp = valid.Count > 0 ? valid.Average(static r => (double)r.Case.PlayerHpLoss) : 0.0;
            double avgV = valid.Count > 0 ? valid.Average(static r => r.Outcome.EconomicValueV) : 0.0;
            int replans = subset.Sum(static r => r.Outcome.UnplannedReplans);
            double[] ms = valid.Select(static r => r.Outcome.ElapsedMs).ToArray();
            double p50 = BenchmarkOutcomeHelper.Percentile(ms, 0.50);
            double p95 = BenchmarkOutcomeHelper.Percentile(ms, 0.95);
            return $"| {label} | {n} | {beat} / {tied} / {behind} ({beatOrTiePct:P1} >=P) | " +
                   $"{beatOld}B/{tiedOld}T | {avgSolverHp:F1} vs {avgPlayerHp:F1} | {avgV:F2} | " +
                   $"{p50:F0} / {p95:F0} | {replans} | {errs} |";
        }

        static string FormatCanonicalVLine(string label, IReadOnlyList<PlayerCorpusCaseResult> subset)
        {
            int n = subset.Count;
            int rawBeatOrTie = subset.Count(static r => r.VerdictVsPlayer is PlayerVerdict.BeatPlayer or PlayerVerdict.TiedPlayer);
            double rawPct = n > 0 ? (double)rawBeatOrTie / n : 0.0;
            int beatV = subset.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.BeatPlayer);
            int tiedV = subset.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.TiedPlayer);
            int behindV = subset.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.BehindPlayer);
            int errV = subset.Count(static r => r.VsPlayerCanonicalV is null or PlayerVerdict.Error);
            double vPct = n > 0 ? (double)(beatV + tiedV) / n : 0.0;
            var validV = subset.Where(static r => r.SolverCanonicalV.HasValue && r.PlayerCanonicalV.HasValue).ToList();
            double avgSolverV = validV.Count > 0 ? validV.Average(static r => r.SolverCanonicalV!.Value) : 0.0;
            double avgPlayerV = validV.Count > 0 ? validV.Average(static r => r.PlayerCanonicalV!.Value) : 0.0;
            double deltaV = avgSolverV - avgPlayerV;
            int playerDeathSaveCases = subset.Count(static r => r.PlayerDeathSaveUseCount > 0);
            int solverDeathSaveCases = subset.Count(static r => r.Outcome.DeathSaveUseCount > 0);
            return $"Canonical V vs Player ({label}): {beatV} / {tiedV} / {behindV} ({vPct:P1} >=P, err={errV}) | " +
                   $"Avg V (S vs P): {avgSolverV:F2} vs {avgPlayerV:F2} ({deltaV:+0.00;-0.00;0.00}) | " +
                   $"Raw >=P: {rawPct:P1} | DeathSave fights S={solverDeathSaveCases} P={playerDeathSaveCases}";
        }

        Console.WriteLine("### Solver vs Player Scoreboard (Overall & Split)");
        Console.WriteLine("| Slice | N | Beat / Tied / Behind Player | vs Old Solver | Avg HP (S vs P) | Avg V | p50 / p95 ms | Replans | Errors |");
        Console.WriteLine("|---|---:|---|---|---:|---:|---:|---:|---:|");
        List<IGrouping<string, PlayerCorpusCaseResult>> splitGroups = results
            .GroupBy(static r => r.Case.Split, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .ToList();
        foreach (IGrouping<string, PlayerCorpusCaseResult> grp in splitGroups)
        {
            Console.WriteLine(FormatScoreRow(grp.Key, grp.ToList()));
        }
        Console.WriteLine(FormatScoreRow("**ALL**", results));
        Console.WriteLine();
        if (splitGroups.Count > 1)
        {
            foreach (IGrouping<string, PlayerCorpusCaseResult> grp in splitGroups)
                Console.WriteLine(FormatCanonicalVLine(grp.Key, grp.ToList()));
        }
        Console.WriteLine(FormatCanonicalVLine("ALL", results));

        Console.WriteLine();
        Console.WriteLine("### Breakdown by Character");
        Console.WriteLine("| Character | N | Beat / Tied / Behind Player | vs Old Solver | Avg HP (S vs P) | Avg V | p50 / p95 ms | Replans | Errors |");
        Console.WriteLine("|---|---:|---|---|---:|---:|---:|---:|---:|");
        foreach (IGrouping<string, PlayerCorpusCaseResult> grp in results
            .GroupBy(static r => r.Case.Character, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(FormatScoreRow(grp.Key, grp.ToList()));
        }

        Console.WriteLine();
        Console.WriteLine("### Breakdown by Encounter Type");
        Console.WriteLine("| Encounter Type | N | Beat / Tied / Behind Player | vs Old Solver | Avg HP (S vs P) | Avg V | p50 / p95 ms | Replans | Errors |");
        Console.WriteLine("|---|---:|---|---|---:|---:|---:|---:|---:|");
        foreach (IGrouping<string, PlayerCorpusCaseResult> grp in results
            .GroupBy(static r => r.Case.EncounterType, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(FormatScoreRow(grp.Key, grp.ToList()));
        }

        List<PlayerCorpusCaseResult> behindTop = results
            .Where(static r => r.VerdictVsPlayer == PlayerVerdict.BehindPlayer)
            .OrderByDescending(static r => (r.Case.PlayerWon ? 1 : 0) - (r.Outcome.Won ? 1 : 0))
            .ThenByDescending(static r => r.SolverEffectiveCostHp - r.PlayerEffectiveCostHp)
            .Take(20)
            .ToList();
        if (behindTop.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"### Top Behind-Player Gaps ({behindTop.Count} shown)");
            Console.WriteLine("| CaseId | Split | Char | Encounter | Solver (W/HP/Pot) | Player (W/HP/Pot) | EffCost Gap | Canonical V (S vs P / Verdict) | Boundary | ms |");
            Console.WriteLine("|---|---|---|---|---|---|---:|---|---|---:|");
            foreach (PlayerCorpusCaseResult r in behindTop)
            {
                double gap = r.SolverEffectiveCostHp - r.PlayerEffectiveCostHp;
                string canonicalCol = r.SolverCanonicalV.HasValue && r.PlayerCanonicalV.HasValue && r.VsPlayerCanonicalV.HasValue
                    ? $"{r.SolverCanonicalV.Value:F2} vs {r.PlayerCanonicalV.Value:F2} ({r.VsPlayerCanonicalV.Value switch { PlayerVerdict.BeatPlayer => "BEAT", PlayerVerdict.TiedPlayer => "TIED", PlayerVerdict.BehindPlayer => "BHND", _ => "ERR" }})"
                    : "-";
                Console.WriteLine(
                    $"| {r.Case.CaseId} | {r.Case.Split} | {r.Case.Character} | {r.Case.Encounter} | " +
                    $"{(r.Outcome.Won ? "W" : "L")}/{r.Outcome.HpLoss}/{r.Outcome.PotionsUsed} | " +
                    $"{(r.Case.PlayerWon ? "W" : "L")}/{r.Case.PlayerHpLoss}/{r.Case.PlayerPotionsUsed} | " +
                    $"+{gap:F1} | {canonicalCol} | {r.Outcome.Boundary} | {r.Outcome.ElapsedMs:F0} |");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"wins={overall.Wins}/{overall.TotalFights} ({overall.WinRate:P1}) | " +
            $"avg_v={overall.AvgEconomicValueV:F2} | p50={overall.P50Ms:F1}ms p95={overall.P95Ms:F1}ms | " +
            $"unplanned_replans={overall.TotalUnplannedReplans} hard_errors={overall.HardErrors} | " +
            $"peak_rss_mb={overall.PeakRssMb:F1} gc_gen2={overall.GcGen2} total_s={overall.TotalElapsedMs / 1000.0:F2}s");
        Console.WriteLine($"outcomes_json={dumpOutcomesPath}");
    }

    private static object BuildScoreboardJson(
        string manifestPath,
        string split,
        BenchmarkSummaryRecord overall,
        IReadOnlyList<PlayerCorpusCaseResult> results)
    {
        List<PlayerCorpusCaseResult> validCanonical = results
            .Where(static r => r.SolverCanonicalV.HasValue && r.PlayerCanonicalV.HasValue)
            .ToList();

        return new
        {
            manifest_path = manifestPath,
            split,
            summary = overall,
            vs_player = new
            {
                total = results.Count,
                beat_player = results.Count(static r => r.VerdictVsPlayer == PlayerVerdict.BeatPlayer),
                tied_player = results.Count(static r => r.VerdictVsPlayer == PlayerVerdict.TiedPlayer),
                behind_player = results.Count(static r => r.VerdictVsPlayer == PlayerVerdict.BehindPlayer),
                errors = results.Count(static r => r.VerdictVsPlayer == PlayerVerdict.Error),
            },
            vs_player_canonical_v = new
            {
                total = results.Count,
                beat_player = results.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.BeatPlayer),
                tied_player = results.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.TiedPlayer),
                behind_player = results.Count(static r => r.VsPlayerCanonicalV == PlayerVerdict.BehindPlayer),
                errors = results.Count(static r => r.VsPlayerCanonicalV is null or PlayerVerdict.Error),
                avg_solver_canonical_v = validCanonical.Count > 0
                    ? Math.Round(validCanonical.Average(static r => r.SolverCanonicalV!.Value), 2)
                    : 0.0,
                avg_player_canonical_v = validCanonical.Count > 0
                    ? Math.Round(validCanonical.Average(static r => r.PlayerCanonicalV!.Value), 2)
                    : 0.0,
            },
            vs_old_solver = new
            {
                beat_old = results.Count(static r => r.VerdictVsOldSolver == PlayerVerdict.BeatPlayer),
                tied_old = results.Count(static r => r.VerdictVsOldSolver == PlayerVerdict.TiedPlayer),
                behind_old = results.Count(static r => r.VerdictVsOldSolver == PlayerVerdict.BehindPlayer),
            },
            cases = results.Select(static r => new
            {
                case_id = r.Case.CaseId,
                split = r.Case.Split,
                character = r.Case.Character,
                encounter = r.Case.Encounter,
                encounter_type = r.Case.EncounterType,
                game_mode = r.Case.GameMode,
                verdict_vs_player = r.VerdictVsPlayer.ToString(),
                verdict_vs_old_solver = r.VerdictVsOldSolver.ToString(),
                solver_won = r.Outcome.Won,
                solver_hp_loss = r.Outcome.HpLoss,
                solver_potions_used = r.Outcome.PotionsUsed,
                solver_potion_cost_hp = r.Outcome.PotionCostHp,
                solver_death_save_use_count = r.Outcome.DeathSaveUseCount,
                solver_v = r.Outcome.EconomicValueV,
                solver_turns = r.Outcome.Turns,
                player_won = r.Case.PlayerWon,
                player_hp_loss = r.Case.PlayerHpLoss,
                player_potions_used = r.Case.PlayerPotionsUsed,
                player_potion_cost_est_hp = r.PlayerPotionCostEstimateHp,
                player_death_save_use_count = r.PlayerDeathSaveUseCount,
                player_turns = r.Case.PlayerTurns,
                boss_hp_relief = r.BossHpRelief.ToString(),
                solver_canonical_v = r.SolverCanonicalV,
                player_canonical_v = r.PlayerCanonicalV,
                vs_player_canonical_v = r.VsPlayerCanonicalV?.ToString(),
                old_solver_won = r.Case.OldSolverWon,
                old_solver_hp_loss = r.Case.OldSolverHpLoss,
                old_solver_potions_used = r.Case.OldSolverPotionsUsed,
                restore_ms = r.RestoreMs,
                combat_start_stamp_matched = r.CombatStartStampMatched,
                ready_stamp_matched = r.ReadyStampMatched,
                elapsed_ms = r.Outcome.ElapsedMs,
                unplanned_replans = r.Outcome.UnplannedReplans,
                hard_error = r.Outcome.HardError,
            }).ToArray(),
        };
    }

    private static BundleGroundTruth ExtractGroundTruth(
        string sourcePath,
        JsonObject index,
        JsonObject? reportJson,
        string originSavePath,
        BundleCheckpointSummary startCp,
        CuratedCorpusCase? manifestOverride)
    {
        if (manifestOverride != null)
        {
            return new BundleGroundTruth(
                CaseId: manifestOverride.CaseId,
                CharacterId: manifestOverride.Character,
                EncounterId: manifestOverride.Encounter,
                EncounterType: manifestOverride.EncounterType,
                GameMode: manifestOverride.GameMode,
                Ascension: manifestOverride.Ascension,
                GameVersion: manifestOverride.GameVersion,
                OldSolverVersion: manifestOverride.OldSolverVersion,
                HasPlayerOutcome: true,
                PlayerWon: manifestOverride.PlayerWon,
                PlayerHpLoss: manifestOverride.PlayerHpLoss,
                PlayerPotionsUsed: manifestOverride.PlayerPotionsUsed,
                PlayerTurns: manifestOverride.PlayerTurns,
                HasOldSolverOutcome: true,
                OldSolverWon: manifestOverride.OldSolverWon,
                OldSolverHpLoss: manifestOverride.OldSolverHpLoss,
                OldSolverPotionsUsed: manifestOverride.OldSolverPotionsUsed,
                OldSolverTurns: manifestOverride.OldSolverTurns,
                PlayerCheckpointId: manifestOverride.PlayerCheckpointId,
                PlayerEventCursor: manifestOverride.PlayerEventCursor);
        }

        string charId = string.Empty;
        string encId = string.Empty;
        string encType = "Monster";
        string gameMode = "Standard";
        int ascension = 0;
        string gameVersion = reportJson?["gameVersion"]?.GetValue<string>() ?? string.Empty;
        string oldSolverVersion = reportJson?["modVersion"]?.GetValue<string>() ?? string.Empty;
        string caseId = reportJson?["reportId"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(sourcePath);

        if (reportJson?["combat"] is JsonObject combatObj)
        {
            charId = combatObj["characterId"]?.GetValue<string>() ?? charId;
            encId = combatObj["encounterId"]?.GetValue<string>() ?? encId;
            encType = combatObj["encounterType"]?.GetValue<string>() ?? encType;
            ascension = combatObj["ascension"]?.GetValue<int>() ?? ascension;
        }

        if (startCp.ReplayStatePath != null && File.Exists(startCp.ReplayStatePath))
        {
            using JsonDocument rsDoc = JsonDocument.Parse(File.ReadAllText(startCp.ReplayStatePath));
            JsonElement root = rsDoc.RootElement;
            if (string.IsNullOrEmpty(encId) && root.TryGetProperty("encounterId", out JsonElement eEl))
                encId = eEl.GetString() ?? encId;
            if (string.IsNullOrEmpty(charId) && root.TryGetProperty("players", out JsonElement plArr) && plArr.GetArrayLength() > 0)
                charId = plArr[0].GetProperty("characterId").GetString() ?? charId;
            if (root.TryGetProperty("ascensionLevel", out JsonElement ascEl))
                ascension = ascEl.GetInt32();
            if (root.TryGetProperty("combatRules", out JsonElement rulesEl) && rulesEl.TryGetProperty("gameMode", out JsonElement gmEl))
                gameMode = gmEl.GetString() ?? gameMode;
        }

        if (string.IsNullOrEmpty(gameMode) && File.Exists(originSavePath))
        {
            using JsonDocument saveDoc = JsonDocument.Parse(File.ReadAllText(originSavePath));
            if (saveDoc.RootElement.TryGetProperty("game_mode", out JsonElement gm))
                gameMode = gm.GetString() ?? "Standard";
        }

        // Extract deduplicated searchResults timeline for player & old-solver ground truth
        List<JsonObject> timeline = [];
        Dictionary<string, int> posByCp = new(StringComparer.Ordinal);
        if (index["searchResults"] is JsonArray srs)
        {
            int idxSr = 0;
            foreach (JsonObject sr in srs.OfType<JsonObject>())
            {
                string cid = sr["checkpointId"]?.GetValue<string>() ?? $"idx:{idxSr}";
                if (posByCp.TryGetValue(cid, out int existingIdx))
                    timeline[existingIdx] = sr;
                else
                {
                    posByCp[cid] = timeline.Count;
                    timeline.Add(sr);
                }
                idxSr++;
            }
        }

        static bool IsWinningSr(JsonObject sr) => sr["combatEndedTurn"] != null && sr["deathTurn"] == null;
        static int SrHp(JsonObject sr) => sr["projectedBattleHpLost"]?.GetValue<int>() ?? 0;
        static int SrPots(JsonObject sr) => (sr["potionCount"]?.GetValue<int>() ?? 0) + (sr["battlePotionsUsedSoFar"]?.GetValue<int>() ?? 0);
        static int SrTurns(JsonObject sr) => sr["combatEndedTurn"]?.GetValue<int>() ?? sr["deathTurn"]?.GetValue<int>() ?? 0;

        JsonObject? t1Base = timeline.LastOrDefault(static s =>
            (s["startTurnNumber"]?.GetValue<int>() ?? 0) == 1 && (s["eventCursor"]?.GetValue<long>() ?? 0L) == 0L)
            ?? timeline.FirstOrDefault(static s => (s["startTurnNumber"]?.GetValue<int>() ?? 0) == 1);

        JsonObject? bestPlayer = timeline
            .Where(static s => (s["eventCursor"]?.GetValue<long>() ?? 0L) > 0L && IsWinningSr(s))
            .OrderBy(static s => SrHp(s) + HotSearchConstants.PotionMinimumHpSaved * SrPots(s))
            .ThenBy(SrHp)
            .ThenBy(SrPots)
            .ThenBy(SrTurns)
            .FirstOrDefault();

        JsonObject? mpc = reportJson?["manualProjectionComparison"] as JsonObject;
        bool hasOld = t1Base != null || mpc != null;
        bool oldWon = t1Base != null ? IsWinningSr(t1Base) : true;
        int oldHp = t1Base != null ? SrHp(t1Base) : (mpc?["previousProjectedBattleHpLost"]?.GetValue<int>() ?? 0);
        int oldPots = t1Base != null ? SrPots(t1Base) : (mpc?["previousProjectedBattlePotionCount"]?.GetValue<int>() ?? 0);
        int oldTurns = t1Base != null ? SrTurns(t1Base) : 0;

        bool hasPlayer = bestPlayer != null || mpc != null;
        bool playerWon = bestPlayer != null ? IsWinningSr(bestPlayer) : true;
        int playerHp = bestPlayer != null ? SrHp(bestPlayer) : (mpc?["currentProjectedBattleHpLost"]?.GetValue<int>() ?? 0);
        int playerPots = bestPlayer != null ? SrPots(bestPlayer) : (mpc?["currentProjectedBattlePotionCount"]?.GetValue<int>() ?? 0);
        int playerTurns = bestPlayer != null ? SrTurns(bestPlayer) : 0;
        string? bestPlayerCpId = bestPlayer?["checkpointId"]?.GetValue<string>()
            ?? mpc?["currentCheckpointId"]?.GetValue<string>();
        int? bestPlayerCursor = bestPlayer?["eventCursor"] is JsonNode evCursorNode
            ? (int)evCursorNode.GetValue<long>()
            : null;

        return new BundleGroundTruth(
            CaseId: caseId,
            CharacterId: charId,
            EncounterId: encId,
            EncounterType: encType,
            GameMode: gameMode,
            Ascension: ascension,
            GameVersion: gameVersion,
            OldSolverVersion: oldSolverVersion,
            HasPlayerOutcome: hasPlayer,
            PlayerWon: playerWon,
            PlayerHpLoss: playerHp,
            PlayerPotionsUsed: playerPots,
            PlayerTurns: playerTurns,
            HasOldSolverOutcome: hasOld,
            OldSolverWon: oldWon,
            OldSolverHpLoss: oldHp,
            OldSolverPotionsUsed: oldPots,
            OldSolverTurns: oldTurns,
            PlayerCheckpointId: bestPlayerCpId,
            PlayerEventCursor: bestPlayerCursor);
    }

    /// <summary>
    /// Deterministically extracts symmetric player one-shot death-save uses (<c>LIZARD_TAIL</c> and
    /// <c>FAIRY_IN_A_BOTTLE</c>), restored revival HP, observed ending HP/MaxHP, and consumed root potion slots
    /// from general bundle artifacts (origin/checkpoint saves, replay-state JSONs, recorded events,
    /// <c>searchResults</c>, and combat route telemetry).
    /// </summary>
    private static PlayerBundleTelemetry ExtractPlayerBundleTelemetry(
        string rootDir,
        JsonObject index,
        string originSavePath,
        BundleCheckpointSummary startCp,
        IReadOnlyList<BundleCheckpointSummary> allCheckpoints,
        RecordedCombatEvent[] events,
        bool modelHashMatches,
        BundleGroundTruth gt)
    {
        PlayerCheckpointSnapshot? startSnap =
            (File.Exists(originSavePath) ? TryReadSavePlayerSnapshot(originSavePath, 0, "origin") : null)
            ?? (startCp.RunStatePath != null && File.Exists(startCp.RunStatePath)
                ? TryReadSavePlayerSnapshot(startCp.RunStatePath, startCp.EventCursor, startCp.CheckpointId)
                : null)
            ?? (startCp.ReplayStatePath != null && File.Exists(startCp.ReplayStatePath)
                ? TryReadReplayStatePlayerSnapshot(startCp.ReplayStatePath, startCp.EventCursor, startCp.CheckpointId)
                : null);

        if (startSnap == null)
            return PlayerBundleTelemetry.Empty;

        List<PlayerCheckpointSnapshot> cpSnaps = new(allCheckpoints.Count);
        foreach (BundleCheckpointSummary cp in allCheckpoints.OrderBy(static c => c.EventCursor))
        {
            PlayerCheckpointSnapshot? snap = null;
            if (cp.RunStatePath != null && File.Exists(cp.RunStatePath))
                snap = TryReadSavePlayerSnapshot(cp.RunStatePath, cp.EventCursor, cp.CheckpointId);
            if (snap == null && cp.ReplayStatePath != null && File.Exists(cp.ReplayStatePath))
                snap = TryReadReplayStatePlayerSnapshot(cp.ReplayStatePath, cp.EventCursor, cp.CheckpointId);
            if (snap != null)
                cpSnaps.Add(snap);
        }

        int initialUnusedLizardTails = startSnap.UnusedLizardTails;
        HashSet<int> initialFairySlots = startSnap.PotionsBySlot
            .Where(static kv => string.Equals(kv.Value, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal))
            .Select(static kv => kv.Key)
            .ToHashSet();
        int initialFairyCount = initialFairySlots.Count;
        int initialTotalDeathSaves = initialUnusedLizardTails + initialFairyCount;

        JsonObject? playerSr = gt.HasPlayerOutcome ? FindMatchingPlayerSearchResult(index, gt) : null;
        int targetCursor = gt.PlayerEventCursor
            ?? (playerSr?["eventCursor"] is JsonNode srCursor ? (int)srCursor.GetValue<long>() : events.Length);
        targetCursor = Math.Clamp(targetCursor, 0, events.Length);

        PlayerCheckpointSnapshot snapAtTarget =
            cpSnaps.LastOrDefault(s => !string.IsNullOrEmpty(gt.PlayerCheckpointId)
                && string.Equals(s.CheckpointId, gt.PlayerCheckpointId, StringComparison.Ordinal))
            ?? cpSnaps.LastOrDefault(s => s.EventCursor <= targetCursor)
            ?? cpSnaps.LastOrDefault()
            ?? startSnap;

        int latestObservedMaxHp = snapAtTarget.MaxHp > 0 ? snapAtTarget.MaxHp : startSnap.MaxHp;

        HashSet<int> discardedFairySlots = [];
        HashSet<int> rootSlotsStillOriginal = new(startSnap.PotionsBySlot.Keys);
        List<int> consumedRootPotionSlots = [];
        int alchemizePlayCount = 0;

        if (modelHashMatches)
        {
            for (int i = 0; i < events.Length; i++)
            {
                RecordedCombatEvent rec = events[i];
                if (string.Equals(rec.Origin, "system", StringComparison.Ordinal))
                    continue;
                CombatReplayEvent decoded = DecodeReplayEvent(rec);
                if (decoded.eventType != CombatReplayEventType.GameAction || decoded.action == null)
                    continue;

                if (decoded.action is NetDiscardPotionGameAction discard)
                {
                    int slot = checked((int)discard.potionSlotIndex);
                    if (initialFairySlots.Contains(slot))
                    {
                        PlayerCheckpointSnapshot? priorCp = cpSnaps.LastOrDefault(s => s.EventCursor <= rec.Sequence);
                        bool hadFairyBeforeDiscard = priorCp == null
                            || (priorCp.PotionsBySlot.TryGetValue(slot, out string? pid)
                                && string.Equals(pid, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal));
                        if (hadFairyBeforeDiscard)
                            discardedFairySlots.Add(slot);
                    }
                    if (i < targetCursor)
                        rootSlotsStillOriginal.Remove(slot);
                }
                else if (i < targetCursor && decoded.action is NetUsePotionAction usePot)
                {
                    int slot = checked((int)usePot.potionIndex);
                    if (rootSlotsStillOriginal.Remove(slot))
                        consumedRootPotionSlots.Add(slot);
                }
                else if (i < targetCursor && decoded.action is NetPlayCardAction play)
                {
                    if (string.Equals(play.modelId?.Entry, "ALCHEMIZE", StringComparison.OrdinalIgnoreCase))
                        alchemizePlayCount++;
                }
            }
        }
        else
        {
            foreach (KeyValuePair<int, string> kv in startSnap.PotionsBySlot)
            {
                if (string.Equals(kv.Value, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal))
                    continue;
                if (!snapAtTarget.PotionsBySlot.TryGetValue(kv.Key, out string? nowEntry)
                    || !string.Equals(nowEntry, kv.Value, StringComparison.Ordinal))
                {
                    if (rootSlotsStillOriginal.Remove(kv.Key))
                        consumedRootPotionSlots.Add(kv.Key);
                }
            }
        }

        if (playerSr?["plannedActions"] is JsonArray srActions)
        {
            foreach (JsonObject act in srActions.OfType<JsonObject>())
            {
                string? kind = act["kind"]?.GetValue<string>();
                if (string.Equals(kind, "UsePotion", StringComparison.Ordinal))
                {
                    int slot = act["potionSlot"]?.GetValue<int>() ?? -1;
                    if (slot >= 0 && startSnap.PotionsBySlot.TryGetValue(slot, out string? rootEntry))
                    {
                        string actPotId = NormalizeModelEntry(act["potionId"]?.GetValue<string>(), "POTION.");
                        if ((string.IsNullOrEmpty(actPotId) || string.Equals(actPotId, rootEntry, StringComparison.Ordinal))
                            && rootSlotsStillOriginal.Remove(slot))
                        {
                            consumedRootPotionSlots.Add(slot);
                        }
                    }
                }
                else if (string.Equals(kind, "PlayCard", StringComparison.Ordinal))
                {
                    string? cardId = act["cardId"]?.GetValue<string>();
                    if (string.Equals(cardId, "ALCHEMIZE", StringComparison.OrdinalIgnoreCase))
                        alchemizePlayCount++;
                }
            }
        }

        MatchedRouteTelemetry? matchedTelemetry = gt.HasPlayerOutcome
            ? FindMatchingPlayerTelemetry(rootDir, index, gt)
            : null;

        int deathSaveUseCount = 0;
        int lizardTailUseCount = 0;
        int fairyUseCount = 0;
        int deathSaveHpRestored = 0;

        if (initialTotalDeathSaves > 0)
        {
            int minUnusedLizardTails = initialUnusedLizardTails;
            foreach (PlayerCheckpointSnapshot snap in cpSnaps)
            {
                if (snap.UnusedLizardTails < minUnusedLizardTails)
                    minUnusedLizardTails = snap.UnusedLizardTails;
            }
            int liveLizardTailUses = Math.Max(0, initialUnusedLizardTails - minUnusedLizardTails);

            int liveFairyUses = 0;
            int liveFairyUsesAtSr = 0;
            PlayerCheckpointSnapshot? snapUpToSr = cpSnaps.LastOrDefault(s => s.EventCursor <= targetCursor);
            foreach (int slot in initialFairySlots)
            {
                if (discardedFairySlots.Contains(slot))
                    continue;
                if (cpSnaps.Any(s => !s.PotionsBySlot.TryGetValue(slot, out string? pid)
                    || !string.Equals(pid, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal)))
                {
                    liveFairyUses++;
                }
                if (snapUpToSr != null
                    && (!snapUpToSr.PotionsBySlot.TryGetValue(slot, out string? pidAtSr)
                        || !string.Equals(pidAtSr, "FAIRY_IN_A_BOTTLE", StringComparison.Ordinal)))
                {
                    liveFairyUsesAtSr++;
                }
            }

            int usableFairyBudget = Math.Max(0, initialFairyCount - discardedFairySlots.Count);
            int projectedImplicitFairyUses = 0;
            if (playerSr != null && playerSr["plannedActions"] is JsonArray plannedArr && usableFairyBudget > 0)
            {
                int srPotionCount = playerSr["potionCount"]?.GetValue<int>() ?? 0;
                int explicitUsePotions = plannedArr
                    .OfType<JsonObject>()
                    .Count(static a => string.Equals(a["kind"]?.GetValue<string>(), "UsePotion", StringComparison.Ordinal));
                int remainingFairyAtSr = Math.Max(0, usableFairyBudget - liveFairyUsesAtSr);
                projectedImplicitFairyUses = Math.Clamp(srPotionCount - explicitUsePotions, 0, remainingFairyAtSr);
            }
            int totalFairyUses = Math.Clamp(
                Math.Max(liveFairyUses, liveFairyUsesAtSr + projectedImplicitFairyUses),
                0,
                usableFairyBudget);

            int telemetryDeathSaves = matchedTelemetry?.DeathSaveUseCount ?? 0;
            int telemetryRelicHp = Math.Max(0, matchedTelemetry?.DeathSaveRelicHpRestored ?? 0);
            int telemetryPotionHp = Math.Max(0, matchedTelemetry?.DeathSavePotionHpRestored ?? 0);

            int inferredRelicFromTelemetry = telemetryRelicHp > 0 ? Math.Min(initialUnusedLizardTails, 1) : 0;
            int inferredFairyFromTelemetry = telemetryPotionHp > 0 ? Math.Min(usableFairyBudget, 1) : 0;

            lizardTailUseCount = Math.Clamp(Math.Max(liveLizardTailUses, inferredRelicFromTelemetry), 0, initialUnusedLizardTails);
            fairyUseCount = Math.Clamp(Math.Max(totalFairyUses, inferredFairyFromTelemetry), 0, usableFairyBudget);
            deathSaveUseCount = Math.Clamp(
                Math.Max(lizardTailUseCount + fairyUseCount, telemetryDeathSaves),
                0,
                initialTotalDeathSaves);

            int remainingUnassigned = Math.Max(0, deathSaveUseCount - (lizardTailUseCount + fairyUseCount));
            if (remainingUnassigned > 0)
            {
                int availFairy = Math.Max(0, usableFairyBudget - fairyUseCount);
                int availLizard = Math.Max(0, initialUnusedLizardTails - lizardTailUseCount);
                if (telemetryPotionHp >= telemetryRelicHp)
                {
                    int addFairy = Math.Min(remainingUnassigned, availFairy);
                    fairyUseCount += addFairy;
                    remainingUnassigned -= addFairy;
                    lizardTailUseCount += Math.Min(remainingUnassigned, availLizard);
                }
                else
                {
                    int addLizard = Math.Min(remainingUnassigned, availLizard);
                    lizardTailUseCount += addLizard;
                    remainingUnassigned -= addLizard;
                    fairyUseCount += Math.Min(remainingUnassigned, availFairy);
                }
            }

            int telemetryDeathSaveHp = telemetryRelicHp + telemetryPotionHp;
            int baseMaxHp = Math.Max(1, latestObservedMaxHp > 0 ? latestObservedMaxHp : startSnap.MaxHp);
            int estimatedDeathSaveHp = lizardTailUseCount * Math.Max(1, baseMaxHp / 2)
                + fairyUseCount * Math.Max(1, (baseMaxHp * 30) / 100);
            deathSaveHpRestored = telemetryDeathSaveHp > 0 && telemetryDeathSaves >= deathSaveUseCount
                ? telemetryDeathSaveHp
                : Math.Max(telemetryDeathSaveHp, estimatedDeathSaveHp);
        }

        return new PlayerBundleTelemetry(
            InitialUnusedLizardTails: initialUnusedLizardTails,
            InitialFairyCount: initialFairyCount,
            DeathSaveUseCount: deathSaveUseCount,
            LizardTailUseCount: lizardTailUseCount,
            FairyUseCount: fairyUseCount,
            DeathSaveHpRestored: deathSaveHpRestored,
            TelemetryFinalHp: matchedTelemetry?.FinalHp,
            LatestObservedMaxHp: latestObservedMaxHp,
            ConsumedRootPotionSlots: consumedRootPotionSlots.ToArray(),
            AlchemizePlayCount: alchemizePlayCount);
    }

    private static JsonObject? FindMatchingPlayerSearchResult(JsonObject index, BundleGroundTruth gt)
    {
        if (index["searchResults"] is not JsonArray srs || srs.Count == 0)
            return null;

        bool MatchesOutcome(JsonObject sr)
        {
            bool srWon = sr["combatEndedTurn"] != null && sr["deathTurn"] == null;
            int srHp = sr["projectedBattleHpLost"]?.GetValue<int>() ?? 0;
            int srPots = (sr["potionCount"]?.GetValue<int>() ?? 0) + (sr["battlePotionsUsedSoFar"]?.GetValue<int>() ?? 0);
            int srTurns = sr["combatEndedTurn"]?.GetValue<int>() ?? sr["deathTurn"]?.GetValue<int>() ?? 0;
            return srWon == gt.PlayerWon
                && srHp == gt.PlayerHpLoss
                && srPots == gt.PlayerPotionsUsed
                && (gt.PlayerTurns <= 0 || srTurns <= 0 || srTurns == gt.PlayerTurns);
        }

        List<JsonObject> all = srs.OfType<JsonObject>().ToList();
        if (!string.IsNullOrEmpty(gt.PlayerCheckpointId))
        {
            JsonObject? exact = all.LastOrDefault(sr =>
                string.Equals(sr["checkpointId"]?.GetValue<string>(), gt.PlayerCheckpointId, StringComparison.Ordinal)
                && MatchesOutcome(sr));
            if (exact != null)
                return exact;
        }

        JsonObject? byOutcome = all.LastOrDefault(sr =>
            (sr["eventCursor"]?.GetValue<long>() ?? 0L) > 0L && MatchesOutcome(sr));
        if (byOutcome != null)
            return byOutcome;

        if (!string.IsNullOrEmpty(gt.PlayerCheckpointId))
        {
            return all.LastOrDefault(sr =>
                string.Equals(sr["checkpointId"]?.GetValue<string>(), gt.PlayerCheckpointId, StringComparison.Ordinal));
        }

        return null;
    }

    private static PlayerCheckpointSnapshot? TryReadSavePlayerSnapshot(
        string savePath,
        int eventCursor,
        string checkpointId)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(savePath));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("players", out JsonElement players)
            || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement p0 = players[0];
        if (p0.ValueKind != JsonValueKind.Object)
            return null;

        int currentHp = TryGetInt(p0, "current_hp") ?? 0;
        int maxHp = TryGetInt(p0, "max_hp") ?? 0;
        int unusedLizardTails = 0;

        if (p0.TryGetProperty("relics", out JsonElement relics) && relics.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement relic in relics.EnumerateArray())
            {
                if (relic.ValueKind != JsonValueKind.Object)
                    continue;
                string entry = NormalizeModelEntry(TryGetString(relic, "id"), "RELIC.");
                if (!string.Equals(entry, "LIZARD_TAIL", StringComparison.Ordinal))
                    continue;

                bool wasUsed = false;
                if (relic.TryGetProperty("props", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
                {
                    if (props.TryGetProperty("bools", out JsonElement bools) && bools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement prop in bools.EnumerateArray())
                        {
                            if (prop.ValueKind != JsonValueKind.Object)
                                continue;
                            if (string.Equals(TryGetString(prop, "name"), "WasUsed", StringComparison.OrdinalIgnoreCase)
                                && prop.TryGetProperty("value", out JsonElement valEl)
                                && valEl.ValueKind == JsonValueKind.True)
                            {
                                wasUsed = true;
                                break;
                            }
                        }
                    }
                    else if (props.TryGetProperty("WasUsed", out JsonElement directVal)
                        && directVal.ValueKind == JsonValueKind.True)
                    {
                        wasUsed = true;
                    }
                }

                if (!wasUsed)
                    unusedLizardTails++;
            }
        }

        Dictionary<int, string> potionsBySlot = new();
        if (p0.TryGetProperty("potions", out JsonElement potions) && potions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement pot in potions.EnumerateArray())
            {
                if (pot.ValueKind != JsonValueKind.Object)
                    continue;
                int? slot = TryGetInt(pot, "slot_index") ?? TryGetInt(pot, "slot");
                string entry = NormalizeModelEntry(TryGetString(pot, "id"), "POTION.");
                if (slot.HasValue && !string.IsNullOrEmpty(entry))
                    potionsBySlot[slot.Value] = entry;
            }
        }

        return new PlayerCheckpointSnapshot(eventCursor, checkpointId, currentHp, maxHp, unusedLizardTails, potionsBySlot);
    }

    private static PlayerCheckpointSnapshot? TryReadReplayStatePlayerSnapshot(
        string replayStatePath,
        int eventCursor,
        string checkpointId)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(replayStatePath));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("players", out JsonElement players)
            || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement p0 = players[0];
        if (p0.ValueKind != JsonValueKind.Object)
            return null;

        int currentHp = TryGetInt(p0, "currentHp") ?? 0;
        int maxHp = TryGetInt(p0, "maxHp") ?? 0;
        int unusedLizardTails = 0;

        if (p0.TryGetProperty("relics", out JsonElement relics) && relics.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement relic in relics.EnumerateArray())
            {
                if (relic.ValueKind != JsonValueKind.Object)
                    continue;
                string entry = NormalizeModelEntry(TryGetString(relic, "id"), "RELIC.");
                if (!string.Equals(entry, "LIZARD_TAIL", StringComparison.Ordinal))
                    continue;

                bool wasUsed = false;
                if (relic.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    if (fields.TryGetProperty("LizardTail._wasUsed", out JsonElement wuEl)
                        && wuEl.ValueKind == JsonValueKind.True)
                    {
                        wasUsed = true;
                    }
                    else if (string.Equals(TryGetString(fields, "RelicModel._status"), "Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        wasUsed = true;
                    }
                }

                if (!wasUsed)
                    unusedLizardTails++;
            }
        }

        Dictionary<int, string> potionsBySlot = new();
        if (p0.TryGetProperty("potions", out JsonElement potions) && potions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement pot in potions.EnumerateArray())
            {
                if (pot.ValueKind != JsonValueKind.Object)
                    continue;
                int? slot = TryGetInt(pot, "slot") ?? TryGetInt(pot, "slot_index");
                string entry = NormalizeModelEntry(TryGetString(pot, "id"), "POTION.");
                if (slot.HasValue && !string.IsNullOrEmpty(entry))
                    potionsBySlot[slot.Value] = entry;
            }
        }

        return new PlayerCheckpointSnapshot(eventCursor, checkpointId, currentHp, maxHp, unusedLizardTails, potionsBySlot);
    }

    private static MatchedRouteTelemetry? FindMatchingPlayerTelemetry(
        string rootDir,
        JsonObject index,
        BundleGroundTruth gt)
    {
        bool MatchesPlayer(MatchedRouteTelemetry t)
            => t.Won == gt.PlayerWon
               && t.ProjectedBattleHpLost == gt.PlayerHpLoss
               && t.TotalPotionsUsed == gt.PlayerPotionsUsed
               && (gt.PlayerTurns <= 0 || t.Turns <= 0 || t.Turns == gt.PlayerTurns);

        string rawSlot = index["slot"]?.GetValue<string>() ?? "current";
        string slot = rawSlot is "current" or "recent" ? rawSlot : "current";
        string[] routeFiles =
        [
            Path.Combine(rootDir, "replay", slot, "last-route.txt"),
            Path.Combine(rootDir, "diagnostics", "current-route.txt"),
        ];

        foreach (string path in routeFiles)
        {
            if (!File.Exists(path))
                continue;
            MatchedRouteTelemetry? match = null;
            foreach (string line in File.ReadLines(path))
            {
                if (TryParseResultTelemetryLine(line, out MatchedRouteTelemetry? parsed)
                    && parsed != null
                    && MatchesPlayer(parsed))
                {
                    match = parsed;
                }
            }
            if (match != null)
                return match;
        }

        string combatLogsDir = Path.Combine(rootDir, "diagnostics", "logs", "combat");
        if (!Directory.Exists(combatLogsDir))
            return null;

        string? expectedSessionId = index["sessionId"]?.GetValue<string>();
        string logsIndexPath = Path.Combine(rootDir, "diagnostics", "logs", "index.json");
        if (!string.IsNullOrEmpty(expectedSessionId) && File.Exists(logsIndexPath))
        {
            using JsonDocument logIdxDoc = JsonDocument.Parse(File.ReadAllText(logsIndexPath));
            if (logIdxDoc.RootElement.ValueKind == JsonValueKind.Object
                && logIdxDoc.RootElement.TryGetProperty("combat", out JsonElement combatEl)
                && combatEl.ValueKind == JsonValueKind.Object)
            {
                string? logSessionId = TryGetString(combatEl, "sessionId");
                if (!string.IsNullOrEmpty(logSessionId)
                    && !string.Equals(logSessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        foreach (string logPath in Directory.EnumerateFiles(combatLogsDir, "*.jsonl")
            .OrderByDescending(static p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            MatchedRouteTelemetry? fileLastMatch = null;
            foreach (string rawLine in File.ReadLines(logPath))
            {
                if (!rawLine.Contains("] RESULT ", StringComparison.Ordinal))
                    continue;
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;
                using JsonDocument lineDoc = JsonDocument.Parse(trimmed);
                if (lineDoc.RootElement.ValueKind != JsonValueKind.Object
                    || !lineDoc.RootElement.TryGetProperty("Message", out JsonElement msgEl)
                    || msgEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string? msg = msgEl.GetString();
                if (string.IsNullOrEmpty(msg))
                    continue;
                int nl = msg.IndexOfAny(['\r', '\n']);
                string firstLine = nl >= 0 ? msg[..nl] : msg;
                if (TryParseResultTelemetryLine(firstLine, out MatchedRouteTelemetry? parsed)
                    && parsed != null
                    && MatchesPlayer(parsed))
                {
                    fileLastMatch = parsed;
                }
            }
            if (fileLastMatch != null)
                return fileLastMatch;
        }

        return null;
    }

    private static bool TryParseResultTelemetryLine(string line, out MatchedRouteTelemetry? telemetry)
    {
        telemetry = null;
        int marker = line.IndexOf("] RESULT ", StringComparison.Ordinal);
        if (marker < 0)
            return false;

        int? projectedHpLost = null;
        int? battlePotsSoFar = null;
        int? potionCount = null;
        int? combatEndedTurn = null;
        int? deathTurn = null;
        int? finalHp = null;
        int? deathSaveRelicHp = null;
        int? deathSavePotionHp = null;
        int? deathSaveUses = null;
        int? projectedDeathSaveUses = null;

        string payload = line[(marker + "] RESULT ".Length)..];
        foreach (string token in payload.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0 || eq >= token.Length - 1)
                continue;
            string key = token[..eq];
            string val = token[(eq + 1)..];
            if (val == "-")
                continue;

            switch (key)
            {
                case "projected_battle_hp_lost":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v1))
                        projectedHpLost = v1;
                    break;
                case "battle_potions_used_so_far":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v2))
                        battlePotsSoFar = v2;
                    break;
                case "potion_count":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v3))
                        potionCount = v3;
                    break;
                case "combat_ended_turn":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v4))
                        combatEndedTurn = v4;
                    break;
                case "death_turn":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v5))
                        deathTurn = v5;
                    break;
                case "final_hp":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v6))
                        finalHp = v6;
                    break;
                case "death_save_relic_hp":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v7))
                        deathSaveRelicHp = v7;
                    break;
                case "death_save_potion_hp":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v8))
                        deathSavePotionHp = v8;
                    break;
                case "death_save_uses":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v9))
                        deathSaveUses = v9;
                    break;
                case "projected_death_save_uses":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v10))
                        projectedDeathSaveUses = v10;
                    break;
            }
        }

        if (!projectedHpLost.HasValue)
            return false;

        bool won = combatEndedTurn.HasValue && !deathTurn.HasValue;
        int totalPotions = (battlePotsSoFar ?? 0) + (potionCount ?? 0);
        int turns = combatEndedTurn ?? deathTurn ?? 0;
        int dsUses = Math.Max(deathSaveUses ?? 0, projectedDeathSaveUses ?? 0);

        telemetry = new MatchedRouteTelemetry(
            Won: won,
            ProjectedBattleHpLost: projectedHpLost.Value,
            TotalPotionsUsed: totalPotions,
            Turns: turns,
            FinalHp: finalHp,
            DeathSaveUseCount: dsUses,
            DeathSaveRelicHpRestored: deathSaveRelicHp ?? 0,
            DeathSavePotionHpRestored: deathSavePotionHp ?? 0);
        return true;
    }

    private static string NormalizeModelEntry(string? rawId, string prefix)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return string.Empty;
        string trimmed = rawId.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[prefix.Length..];
        return trimmed.ToUpperInvariant();
    }

    private static int? TryGetInt(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int value)
            ? value
            : null;

    private static string? TryGetString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out JsonElement el)
            && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static CombatState WaitForPlayableOrEnded(
        MainLoopContext loop,
        TimeSpan timeout,
        Func<Exception?>? externalErrorProbe)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            loop.DrainPending();
            if (externalErrorProbe?.Invoke() is { } err)
                throw err;

            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (state != null)
            {
                if (!CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting && !CombatManager.Instance.IsEnding)
                    return state;
                if (CombatManager.Instance.IsInProgress
                    && state.CurrentSide == CombatSide.Player
                    && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                    && !CombatManager.Instance.PlayerActionsDisabled
                    && !RunManager.Instance.ActionExecutor.IsRunning)
                {
                    return state;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"回放等待开战可操作阶段超时：in_progress={CombatManager.Instance.IsInProgress} " +
                    $"starting={CombatManager.Instance.IsStarting} side={state?.CurrentSide.ToString() ?? "-"} " +
                    $"phase={player?.PlayerCombatState?.Phase.ToString() ?? "-"}");
            }
            loop.PumpOnce(10);
        }
    }

    private static void RestoreOutOfCombatRng(RunState runState, string runSnapshotPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runSnapshotPath));
        if (!document.RootElement.TryGetProperty("rng", out JsonElement rngElement)
            || !rngElement.TryGetProperty("rngs", out JsonElement rngsElement))
        {
            return;
        }

        SerializableRunRngSet parsed = new()
        {
            Seed = rngElement.GetProperty("seed").GetString() ?? runState.Rng.StringSeed,
        };
        foreach (JsonProperty prop in rngsElement.EnumerateObject())
        {
            RunRngType? type = prop.Name switch
            {
                "up_front" => RunRngType.UpFront,
                "unknown_map_point" => RunRngType.UnknownMapPoint,
                "treasure_room_relics" => RunRngType.TreasureRoomRelics,
                _ => null,
            };
            if (type is not RunRngType rngType)
                continue;
            JsonElement val = prop.Value;
            parsed.Rngs[rngType] = new SerializableRng
            {
                counter = val.GetProperty("counter").GetInt32(),
                state0 = val.GetProperty("s0").GetUInt64(),
                state1 = val.GetProperty("s1").GetUInt64(),
                state2 = val.GetProperty("s2").GetUInt64(),
                state3 = val.GetProperty("s3").GetUInt64(),
            };
        }

        foreach (RunRngType t in new[] { RunRngType.UpFront, RunRngType.UnknownMapPoint, RunRngType.TreasureRoomRelics })
        {
            if (parsed.Rngs.TryGetValue(t, out SerializableRng? sRng) && sRng != null)
                runState.Rng.GetRng(t).LoadFromSerializable(sRng);
        }
    }

    private static bool StampsMatch(string expected, string actual, out string? firstDiff)
    {
        string normExpected = HotContinuationStamp.StripModStampSections(expected);
        string normActual = HotContinuationStamp.StripModStampSections(actual);
        if (string.Equals(normExpected, normActual, StringComparison.Ordinal))
        {
            firstDiff = null;
            return true;
        }
        firstDiff = new ContinuationStamp(normExpected).DescribeFirstDifference(new ContinuationStamp(normActual));
        return false;
    }

    private static bool NativeStateMatches(CombatState combat, string nativePath)
    {
        byte[] expected = File.ReadAllBytes(nativePath);
        NetFullCombatState actual = NetFullCombatState.FromRun(combat.RunState, justFinishedAction: null);
        PacketWriter writer = new() { WarnOnGrow = false };
        actual.Serialize(writer);
        writer.ZeroByteRemainder();
        return writer.Buffer.AsSpan(0, writer.BytePosition).SequenceEqual(expected);
    }

    private static CombatReplayEvent DecodeReplayEvent(RecordedCombatEvent record)
    {
        PacketReader reader = new();
        reader.Reset(record.Payload);
        return reader.Read<CombatReplayEvent>();
    }

    private static EncounterModel ResolveEncounter(string encounterId)
    {
        EncounterModel[] matches = ModelDb.All.OfType<EncounterModel>()
            .Where(e => e.Id.Entry.Equals(encounterId, StringComparison.OrdinalIgnoreCase)
                || e.Id.ToString().Equals(encounterId, StringComparison.OrdinalIgnoreCase)
                || e.GetType().Name.Equals(encounterId, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static e => e.Id)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"无法唯一解析遭遇：{encounterId}（匹配数={matches.Length}）。");
    }

    private static string ResolveCaseBundlePath(CuratedCorpusCase entry, string manifestDir)
    {
        if (!string.IsNullOrWhiteSpace(entry.BundlePath) && File.Exists(entry.BundlePath))
            return entry.BundlePath;
        if (!string.IsNullOrWhiteSpace(entry.BundleRelPath))
        {
            string relFull = Path.GetFullPath(Path.Combine(manifestDir, entry.BundleRelPath));
            if (File.Exists(relFull))
                return relFull;
        }
        throw new FileNotFoundException($"找不到语料案例回放包：{entry.CaseId}", entry.BundlePath);
    }

    private static string ResolveBundleRelative(string rootDir, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidDataException("回放包内路径字段为空。");
        CheckpointArchive.ValidateEntryPath(rel);
        string full = Path.GetFullPath(Path.Combine(rootDir, rel));
        if (!full.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"unsafe_bundle_path:{rel}");
        if (!File.Exists(full))
            throw new FileNotFoundException($"回放包缺少材料：{rel}", full);
        return full;
    }

    private static string? TryResolveBundleRelative(string rootDir, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
            return null;
        CheckpointArchive.ValidateEntryPath(rel);
        string full = Path.GetFullPath(Path.Combine(rootDir, rel));
        return full.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full)
            ? full
            : null;
    }

    private static void ValidateSearchArgs(
        string profile,
        string searchMode,
        string potionPolicy,
        out SolverPotionPolicy parsedPotionPolicy)
    {
        if (profile is not ("Low" or "Medium" or "High" or "VeryHigh" or "Custom"))
            throw new ArgumentException("--profile 只支持 Low|Medium|High|VeryHigh|Custom。");
        if (searchMode is not ("Coordinator" or "Evaluate"))
            throw new ArgumentException("--search-mode 只支持 Coordinator|Evaluate。");
        parsedPotionPolicy = Enum.Parse<SolverPotionPolicy>(potionPolicy, ignoreCase: true);
    }

    private static HarnessOptions BuildModOptions(
        string repoRoot,
        string profile,
        int? beamOverride,
        int? nodesOverride,
        int dop,
        int budgetMs,
        SolverPotionPolicy potionPolicy,
        string searchMode,
        bool stopAtZeroLoss,
        string logSubdir)
        => new()
        {
            Profile = profile,
            Beam = beamOverride,
            Nodes = nodesOverride,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = potionPolicy.ToString(),
            SearchMode = searchMode,
            StopAtZeroLoss = stopAtZeroLoss,
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", logSubdir),
        };

    private static void WriteJsonFile(string path, object payload)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, BenchmarkOutcomeHelper.JsonOptions));
    }

    internal static TException? FindInner<TException>(Exception ex) where TException : Exception
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is TException match)
                return match;
        }
        return null;
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
