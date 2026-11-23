using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver.Replay;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal enum SingleStepResumeMode
{
    ExecuteCurrentTurn,
    FullAuto,
}

internal sealed class UnattendedTestRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = "SMOKE-001";
    public string CharacterId { get; init; } = "IRONCLAD";
    public string EncounterId { get; init; } = "FUZZY_WURM_CRAWLER_WEAK";
    public string[] ModifierIds { get; init; } = [];
    public string Seed { get; init; } = "COMBATSOLVER";
    public string? GeneratedScenarioPath { get; init; }
    public string? RunSnapshotPath { get; init; }
    public bool LoadRunSnapshotDirectly { get; init; }
    public int? TargetActFloor { get; init; }
    public int? TargetMapColumn { get; init; }
    public RoomType TargetRoomType { get; init; } = RoomType.Monster;
    public MapPointType TargetMapPointType { get; init; } = MapPointType.Unassigned;
    public int? PreCombatPlayerCurrentHpOverride { get; init; }
    public ulong? PreCombatSimulationSeed { get; init; }
    public UnattendedPreCombatMapStep[] PreCombatInterveningMapPoints { get; init; } = [];
    public string[] ExpectedLoadedMods { get; init; } = [];
    public string? ReplayStatePath { get; init; }
    public string? ShowcaseRoutePath { get; init; }
    public string? ShowcaseBundlePath { get; init; }
    public string? NativeStatePath { get; init; }
    public string? CheckpointArchivePath { get; init; }
    public string CheckpointSelector { get; init; } = CheckpointArchive.DefaultFixtureSelector;
    public string ReplayMode { get; init; } = "RestoreOnly";
    public string? ReplayPolicyOverridePath { get; init; }
    public string? DevelopmentStrategyScriptHash { get; init; }
    public string? DevelopmentStrategyParametersHash { get; init; }
    public string? DevelopmentMonitorStatePath { get; init; }
    public string? EvidenceDirectory { get; init; }
    public bool PreserveNativeCombatStateForTest { get; init; }
    public int Ascension { get; init; }
    public MegaCrit.Sts2.Core.Runs.GameMode GameMode { get; init; } = MegaCrit.Sts2.Core.Runs.GameMode.Standard;
    public string? DreadBossEncounterId { get; init; }
    public string[] DominionIdsForTest { get; init; } = [];
    public int ActIndexForTest { get; init; }
    public bool MarkEncounterAsSecondBossForTest { get; init; }
    public int EnemyCurrentHp { get; init; } = 1;
    public int[] InitialEnemyMaxHps { get; init; } = [];
    public int[] InitialEnemyCurrentHps { get; init; } = [];
    public int[] InitialEnemyBlocks { get; init; } = [];
    public int? InitialPlayerHp { get; init; }
    public int? InitialPlayerMaxHp { get; init; }
    public int? InitialPlayerBlock { get; init; }
    public int? InitialPlayerEnergy { get; init; }
    public int? InitialPlayerStars { get; init; }
    public int? InitialRoundNumber { get; init; }
    public int? InitialPlayerTurnNumber { get; init; }
    public string[][] InitialEnemyStateLogs { get; init; } = [];
    public bool ReloadRunRngAfterStateInjection { get; init; }
    public UnattendedCardInjection[] Cards { get; init; } =
    [
        new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 1 },
    ];
    public UnattendedCardInjection[] RunCards { get; init; } = [];
    public UnattendedPowerInjection[] Powers { get; init; } = [];
    public UnattendedOrbInjection[] Orbs { get; init; } = [];
    public UnattendedOrbCheck[] OrbChecks { get; init; } = [];
    public UnattendedPotionInjection[] Potions { get; init; } = [];
    public UnattendedRelicInjection[] Relics { get; init; } = [];
    public UnattendedRelicInjection[] CombatRelics { get; init; } = [];
    public UnattendedPotionCheck? PotionCheck { get; init; }
    public UnattendedPotionCheck[] PotionChecks { get; init; } = [];
    public UnattendedMonsterMoveCheck? MonsterMoveCheck { get; init; }
    public UnattendedMonsterMoveCheck[] MonsterMoveChecks { get; init; } = [];
    public string[] AdditionalMonsterIds { get; init; } = [];
    public string[] InitialEnemyMoveIds { get; init; } = [];
    public double TimeoutSeconds { get; init; } = 120;
    public int? ExpectedFinishedTurn { get; init; }
    public int? ExpectedFinishedTurnAtMost { get; init; }
    public int? ExpectedFinishedPlayerHpAtLeast { get; init; }
    public bool ClearPlayerHand { get; init; }
    public bool ClearPlayerPiles { get; init; }
    public bool ClearRunDeck { get; init; }
    public bool ClearAllPowers { get; init; }
    public bool VerifyCombatRootSnapshot { get; init; }
    public bool VerifyPreCombatForecastApi { get; init; }
    public bool VerifyInCombatRustKernelContract { get; init; }
    public bool StopAfterCombatRootSnapshotAssertion { get; init; }
    public bool VerifyIncrementalSearch { get; init; }
    private bool _fixedSearchBudget;
    private bool _legacyFixedSearchBudget;
    public bool FixedSearchBudget { get => _fixedSearchBudget || _legacyFixedSearchBudget; init => _fixedSearchBudget = value; }
    [JsonPropertyName("forceShortSearchOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyForceShortSearchOnly { get => false; init => _legacyFixedSearchBudget = value; }
    public bool MeasureSearchPhases { get; init; }
    public bool HoldAfterInitialSearch { get; init; }
    public int? SearchBudgetOverrideMilliseconds { get; init; }
    [JsonPropertyName("shortSearchBudgetOverrideMilliseconds")]
    public int? LegacyShortSearchBudgetMilliseconds { get; init; }
    [JsonPropertyName("deepSearchBudgetOverrideMilliseconds")]
    public int? LegacyDeepSearchBudgetMilliseconds { get; init; }
    public int? SearchMaxDegreeOfParallelismForTest { get; init; }
    public int? TranspositionPruningDisabledMaskForTest { get; init; }
    public int? TranspositionEntryLimitForTest { get; init; }
    public int? ExpectedInitialExpandedNodesAtMost { get; init; }
    public int? ExpectedInitialTransitionsAtMost { get; init; }
    public long? ExpectedInitialTotalExpandedNodesAtMost { get; init; }
    public long? ExpectedInitialTotalTransitionsAtMost { get; init; }
    public SearchBoundaryReason? ExpectedInitialBoundaryReason { get; init; }
    public double? ExpectedInitialTotalElapsedMillisecondsAtMost { get; init; }
    public int? ExpectedInitialExecutableActionCountAtLeast { get; init; }
    public int? ExpectedInitialUnavoidableHpLost { get; init; }
    public int? ExpectedInitialPotionCount { get; init; }
    public int? ExpectedInitialSearchedTurnsAtLeast { get; init; }
    public int? ExpectedInitialHpLostAtMost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLostAtMost { get; init; }
    public string? ExpectedInitialActionCardId { get; init; }
    public string? ExpectedInitialFirstActionCardId { get; init; }
    public bool? ExpectedInitialOnlyDeathRoutesFound { get; init; }
    public int? ExpectedInitialCombatEndedTurn { get; init; }
    public int? ExpectedInitialFinalEnemyHpAtMost { get; init; }
    public int? ExpectedInitialSetupChoiceCountAtLeast { get; init; }
    public bool VerifyInitialSetupWaitsForUserStart { get; init; }
    public bool VerifyTurnSetupManualRecalculate { get; init; }
    public bool VerifyTurnSetupManualRefresh { get; init; }
    public bool VerifyTurnSetupControlsDuringInitialSearch { get; init; }
    public bool VerifyTurnSetupSceneExitCancellation { get; init; }
    public bool StopAfterInitialSetupAssertion { get; init; }
    public bool StopAfterInitialSolverResultAssertion { get; init; }
    public bool ExpectedFullAutoPausedAtDeathTurn { get; init; }
    public bool ExpectedFullAutoPausedAfterWorseRecalculation { get; init; }
    public bool ExpectedFullAutoPausedAtLiveRisk { get; init; }
    public bool EnableStopOnWorseRecalculationForTest { get; init; }
    public int? ExpectedReusedTurn { get; init; }
    public int? ExpectedReusedProjectedBattleHpLost { get; init; }
    public int? ExpectedUnexpectedReplansAtMost { get; init; }
    public bool StopAfterExpectedReuse { get; init; }
    public string? ExpectedPlayedCardId { get; init; }
    public string? ExpectedUsedPotionId { get; init; }
    public string? ExpectedObservedPlayerPowerId { get; init; }
    public string? ExpectedNativeChoiceOwnerPrefix { get; init; }
    public NativeChoiceSurfaceKind? ExpectedNativeChoiceSurface { get; init; }
    public int? ExpectedNativeChoiceVisibleAtLeast { get; init; }
    public int? ExpectedNativeChoiceSearchStartedAtMost { get; init; }
    public bool StopAfterExpectedPlayerPower { get; init; }
    public bool ExpectedPlayerDeath { get; init; }
    public SolverDeploymentFastMode? HeadlessFastModeForTest { get; init; }
    public SolverDeploymentFastMode? DeploymentFastModeForTest { get; init; }
    public SolverPerformancePreset? PerformancePresetForTest { get; init; }
    public int? SearchBeamWidthForTest { get; init; }
    public int? SearchMaxExpandedNodesForTest { get; init; }
    public int? ShortMaxCardBranchesPerNodeForTest { get; init; }
    public int? DeepMaxCardBranchesPerNodeForTest { get; init; }
    public SolverPotionPolicy? PotionPolicyForTest { get; init; }
    public SolverTheftPolicy? TheftPolicyForTest { get; init; }
    // Wire-compat nullable field kept for PreCombatForecastWorker and launcher scripts (ignored by HotSearch).
    public bool? EnableNoGcRegionForTest { get; init; }
    public double? DeploymentInterActionDelaySecondsForTest { get; init; }
    public bool AssertDeploymentSpeedRestored { get; init; }
    public bool ExportBugReportAfterSetup { get; init; }
    public bool ExportBugReportAfterCombat { get; init; }
    public bool? EnableDetailedDiagnosticLogsForTest { get; init; }
    public bool ManualEndTurnAfterInitialSearch { get; init; }
    public bool EnableFullAutoAfterManualEndTurn { get; init; }
    public int? ExpectedUnexpectedReplansAtLeast { get; init; }
    public bool StopAfterExpectedUnexpectedReplan { get; init; }
    public string? ExpectedBugReportControlMode { get; init; }
    public int? InjectPlayerHpLossBeforeAutoSearchTurn { get; init; }
    public int InjectPlayerHpLossAmount { get; init; }
    public int? ClearPlayerBlockBeforeEndTurnForTest { get; init; }
    public bool ExitOnComplete { get; init; } = true;
}

internal sealed record UnattendedPreCombatMapStep
{
    public MapCoord Coordinate { get; init; }
    public RoomType RoomType { get; init; }
    public MapPointType MapPointType { get; init; }
}

internal sealed class UnattendedPotionCheck
{
    public string PotionId { get; init; } = string.Empty;
    public string Target { get; init; } = "Player";
    public int TargetIndex { get; init; }
}

internal sealed class UnattendedOrbCheck
{
    public string OrbId { get; init; } = string.Empty;
    public int TargetIndex { get; init; }
}

internal sealed class UnattendedMonsterMoveCheck
{
    public int EnemyIndex { get; init; }
    public string MonsterId { get; init; } = string.Empty;
    public int MonsterOccurrence { get; init; }
    public string? SpawnInitialMoveId { get; init; }
    public string MoveId { get; init; } = string.Empty;
    public int? PlayerHpBefore { get; init; }
    public int? PlayerBlockBefore { get; init; }
    public int? PlayerEnergyBefore { get; init; }
    public int? PlayerStarsBefore { get; init; }
    public int? EnemyHpBefore { get; init; }
    public int? EnemyBlockBefore { get; init; }
    public bool ClearPlayerHandBeforeMove { get; init; }
    public UnattendedPowerInjection? PowerBeforeMove { get; init; }
    public UnattendedPowerInjection[] PowersBeforeMove { get; init; } = [];
    public UnattendedCardInjection? CardBeforeMove { get; init; }
    public UnattendedCardInjection[] CardsBeforeMove { get; init; } = [];
}

internal sealed class UnattendedPowerInjection
{
    public string PowerId { get; init; } = string.Empty;
    public string Target { get; init; } = "Enemy";
    public int TargetIndex { get; init; }
    public string? PowerTarget { get; init; }
    public int PowerTargetIndex { get; init; }
    public string? Applier { get; init; }
    public int ApplierIndex { get; init; }
    public int Amount { get; init; } = 1;
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> InternalIntegerMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedCardInjection
{
    public string CardId { get; init; } = string.Empty;
    public string Pile { get; init; } = "Hand";
    public int Count { get; init; } = 1;
    public int UpgradeLevels { get; init; }
    public string? EnchantmentId { get; init; }
    public int EnchantmentAmount { get; init; } = 1;
    public string? AfflictionId { get; init; }
    public int AfflictionAmount { get; init; } = 1;
    public bool TreatAsDeckCard { get; init; }
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EnumMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedPotionInjection
{
    public string PotionId { get; init; } = string.Empty;
}

internal sealed class UnattendedRelicInjection
{
    public string RelicId { get; init; } = string.Empty;
    public bool AddWithoutObtainedEffects { get; init; }
    public Dictionary<string, int> IntegerMembers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> BooleanMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedOrbInjection
{
    public string OrbId { get; init; } = string.Empty;
    public int Count { get; init; } = 1;
    public Dictionary<string, decimal> DecimalMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedTestResult
{
    public bool ProcessReusable { get; init; }
    public int ProcessId { get; init; } = System.Environment.ProcessId;
    public bool ReusedProcess { get; init; }
    public string? DevelopmentStrategyScriptHash { get; init; }
    public string? DevelopmentStrategyParametersHash { get; init; }
    public string MainAssemblyHash { get; init; } = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(UnattendedTestResult).Assembly.Location)));
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string ScenarioId { get; init; }
    public required string Status { get; init; }
    public required string Stage { get; init; }
    public required string CharacterId { get; init; }
    public required string EncounterId { get; init; }
    public required string Seed { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public bool MainThread { get; init; }
    public bool CombatEnded { get; init; }
    public int StartedTurn { get; init; }
    public int FinishedTurn { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long ManagedFragmentedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public UnattendedSolverMetrics? SolverMetrics { get; init; }
    public System.Text.Json.Nodes.JsonObject? ReplayVerification { get; init; }
    public System.Text.Json.Nodes.JsonObject? GeneratedScenario { get; init; }
    public UnattendedStageTiming[] StageTimings { get; init; } = [];
    public string[] CompletedChecks { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class UnattendedSolverMetrics
{
    public SearchBoundaryReason Boundary { get; init; }
    public int SelectedExpanded { get; init; }
    public int SelectedTransitions { get; init; }
    public int SelectedChoiceBranches { get; init; }
    public long TotalExpanded { get; init; }
    public long TotalTransitions { get; init; }
    public long TotalChoiceBranches { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public double TotalElapsedMilliseconds { get; init; }
    public long WorkerAllocatedBytes { get; init; }
    public long TotalWorkerAllocatedBytes { get; init; }
    public int TotalGen0Collections { get; init; }
    public int TotalGen1Collections { get; init; }
    public int TotalGen2Collections { get; init; }
    public double TotalGcPauseMilliseconds { get; init; }
    public double MaxGcPauseMilliseconds { get; init; }
    public int MaxParallelConcurrency { get; init; }
    public int SearchedTurns { get; init; }
    public int ShufflesCrossed { get; init; }
    public double Score { get; init; }
    public int ProjectedBattleHpLost { get; init; }
    public int UnavoidableHpLost { get; init; }
    public int PotionCount { get; init; }
    public UnattendedPotionUse[] PotionUses { get; init; } = [];
    public bool OnlyDeathRoutes { get; init; }
    public int FinalHp { get; init; }
    public int FinalEnemyHp { get; init; }
    public int? CombatEndedTurn { get; init; }
    public double CapturedAtElapsedMilliseconds { get; init; }
    public long ManagedLiveBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long ManagedFragmentedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
}

internal sealed class UnattendedPotionUse
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Turn { get; init; }
    public int Slot { get; init; }
}

internal sealed class UnattendedStageTiming
{
    public required string Stage { get; init; }
    public double StartedMilliseconds { get; init; }
    public double DurationMilliseconds { get; init; }
}

internal static class UnattendedTestFiles
{
    public const string RequestUri = "user://combat_solver_test_request.json";
    public const string RunningUri = "user://combat_solver_test_running.json";
    public const string ResultUri = "user://combat_solver_test_result.json";
    public const string ReadyUri = "user://combat_solver_test_ready.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string GlobalPath(string uri) => ProjectSettings.GlobalizePath(uri);
}
