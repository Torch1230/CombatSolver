using System.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public int Ascension { get; init; }
    public int ActIndexForTest { get; init; }
    public bool MarkEncounterAsSecondBossForTest { get; init; }
    public int EnemyCurrentHp { get; init; } = 1;
    public int[] InitialEnemyCurrentHps { get; init; } = [];
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
    public bool VerifyPredictionFailureBoundaries { get; init; }
    public bool VerifySearchPolicySnapshot { get; init; }
    public bool VerifyControllerSessionLifecycle { get; init; }
    public bool VerifyForkBoundaries { get; init; }
    public bool VerifyCombatRootSnapshot { get; init; }
    public bool VerifyPreCombatForecastApi { get; init; }
    public bool VerifyBaseLibCardModifierBoundary { get; init; }
    public bool StopAfterCombatRootSnapshotAssertion { get; init; }
    public bool VerifyIncrementalSearch { get; init; }
    public bool ForceShortSearchOnly { get; init; }
    public bool MeasureSearchPhases { get; init; }
    public bool HoldAfterInitialSearch { get; init; }
    public int? ShortSearchBudgetOverrideMilliseconds { get; init; }
    public int? DeepSearchBudgetOverrideMilliseconds { get; init; }
    public int? SearchMaxDegreeOfParallelismForTest { get; init; }
    public SolverSearchPhase? ExpectedInitialSearchPhase { get; init; }
    public bool? ExpectedInitialDeepSearchTriggered { get; init; }
    public bool? ExpectedInitialDeepSearchImprovedResult { get; init; }
    public double? ExpectedInitialTotalElapsedMillisecondsAtMost { get; init; }
    public long? ExpectedInitialTotalAllocatedBytesAtMost { get; init; }
    public int? ExpectedInitialGen2CollectionsAtMost { get; init; }
    public double? ExpectedInitialTotalGcPauseMillisecondsAtMost { get; init; }
    public double? ExpectedInitialMaxGcPauseMillisecondsAtMost { get; init; }
    public double? ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost { get; init; }
    public int? ExpectedInitialMainThreadFramesOver50MillisecondsAtMost { get; init; }
    public int? ExpectedInitialMainThreadFramesOver100MillisecondsAtMost { get; init; }
    public int? ExpectedInitialTransitionCacheHitsAtLeast { get; init; }
    public int? ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast { get; init; }
    public int? ExpectedInitialCycleShapesDetectedAtLeast { get; init; }
    public int? ExpectedInitialCycleProbeContinuationsExpandedAtLeast { get; init; }
    public int? ExpectedInitialCycleCandidatesProtectedAtLeast { get; init; }
    public int? ExpectedInitialCycleContinuationsStoppedAtLeast { get; init; }
    public int? ExpectedInitialCrossTurnCandidatesProtectedAtLeast { get; init; }
    public int? ExpectedInitialCrossTurnContinuationsStoppedAtLeast { get; init; }
    public int? ExpectedInitialNodeLimitSnapshotsReleasedAtLeast { get; init; }
    public int? ExpectedInitialChoiceBranchesEvaluatedAtLeast { get; init; }
    public int? ExpectedInitialExecutableActionCountAtLeast { get; init; }
    public int? ExpectedInitialSoldHp { get; init; }
    public int? ExpectedInitialSoldHpAtMost { get; init; }
    public int? ExpectedInitialSoldHpBranchesPrunedAtLeast { get; init; }
    public int? ExpectedInitialActionAdmissionRepresentativesProtectedAtLeast { get; init; }
    public int? ExpectedInitialHpInvestmentBranchesProtectedAtLeast { get; init; }
    public int? ExpectedInitialPotionCount { get; init; }
    public SolverTheftPolicy? ExpectedInitialTheftPolicy { get; init; }
    public int? ExpectedInitialOutstandingStolenResource { get; init; }
    public int? ExpectedInitialPotionHpSavedAtLeast { get; init; }
    public int? ExpectedInitialPotionBranchesRejectedAtLeast { get; init; }
    public int? ExpectedInitialSearchedTurnsAtLeast { get; init; }
    public int? ExpectedInitialShufflesCrossedAtLeast { get; init; }
    public int? ExpectedInitialUnmirroredCount { get; init; }
    public int? ExpectedInitialHpLostAtMost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLostAtMost { get; init; }
    public int? ExpectedInitialLongTermResourceValueAtLeast { get; init; }
    public int? ExpectedInitialFinalMaxHp { get; init; }
    public int? ExpectedInitialMaxBlockAtLeast { get; init; }
    public int? ExpectedInitialActualBlockAtLeast { get; init; }
    public string? ExpectedInitialActionCardId { get; init; }
    public string? ExpectedInitialAbsentActionCardId { get; init; }
    public string? ExpectedInitialFirstActionCardId { get; init; }
    public string? ExpectedInitialFirstActionPotionId { get; init; }
    public string? ExpectedInitialActionTitle { get; init; }
    public int? ExpectedInitialActionReplayCount { get; init; }
    public bool? ExpectedInitialOnlyDeathRoutesFound { get; init; }
    public int? ExpectedInitialCombatEndedTurn { get; init; }
    public int? ExpectedInitialDeathTurn { get; init; }
    public int? ExpectedInitialDeathTurnAtLeast { get; init; }
    public int? ExpectedInitialFinalEnemyHpAtMost { get; init; }
    public bool? ExpectedInitialActEndingBoss { get; init; }
    public BossHpRelief? ExpectedInitialBossHpRelief { get; init; }
    public string? ExpectedInitialPlannedChoiceCardId { get; init; }
    public int? ExpectedInitialTurnStartChoiceTurn { get; init; }
    public string? ExpectedInitialTurnStartChoiceSourceId { get; init; }
    public string? ExpectedInitialTurnStartChoiceCardId { get; init; }
    public string? ExpectedInitialTurnStartChoiceStateContains { get; init; }
    public string? ExpectedInitialTurnStartChoiceStateExcludes { get; init; }
    public int? ExpectedInitialSetupChoiceCountAtLeast { get; init; }
    public string? ExpectedInitialSetupChoiceSourceId { get; init; }
    public string? ExpectedInitialSetupChoiceTextStartsWith { get; init; }
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
    public string? ExpectedInitialRelicEffectId { get; init; }
    public string? ExpectedInitialRelicEffectSummary { get; init; }
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
    public int? ShortMaxCardBranchesPerNodeForTest { get; init; }
    public int? DeepMaxCardBranchesPerNodeForTest { get; init; }
    public SolverPotionPolicy? PotionPolicyForTest { get; init; }
    public SolverTheftPolicy? TheftPolicyForTest { get; init; }
    public bool? EnableNoGcRegionForTest { get; init; }
    public double? NoGcRegionBudgetGigabytesForTest { get; init; }
    public double? DeploymentInterActionDelaySecondsForTest { get; init; }
    public bool AssertDeploymentSpeedRestored { get; init; }
    public bool ExportBugReportAfterSetup { get; init; }
    public bool ExportBugReportAfterCombat { get; init; }
    public bool? EnableDetailedDiagnosticLogsForTest { get; init; }
    public bool ManualEndTurnAfterInitialSearch { get; init; }
    public bool SingleStepAfterInitialSearch { get; init; }
    public SingleStepResumeMode? SingleStepResumeModeForTest { get; init; }
    public int? ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast { get; init; }
    public bool EnableFullAutoAfterManualEndTurn { get; init; }
    public int? ExpectedManualDivergencesAtLeast { get; init; }
    public int? ExpectedUnexpectedReplansAtLeast { get; init; }
    public bool StopAfterExpectedUnexpectedReplan { get; init; }
    public bool ExpectedUnexpectedReplanWarning { get; init; }
    public bool ExportBugReportAfterUnexpectedReplan { get; init; }
    public string? ExpectedBugReportControlMode { get; init; }
    public int? ExpectedNoGcRegionRolloversAtLeast { get; init; }
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
    public bool ProcureThroughGame { get; init; }
    public int TargetIndex { get; init; }
    public int? PlayerHpBefore { get; init; }
    public int? PlayerBlockBefore { get; init; }
    public int? PlayerEnergyBefore { get; init; }
    public int? PlayerStarsBefore { get; init; }
    public int? EnemyHpBefore { get; init; }
    public bool TriggerPlayerSideTurnEndAfterUse { get; init; }
    public bool TriggerEnemySideTurnEndAfterUse { get; init; }
    public bool TriggerAutomaticDeath { get; init; }
    public string[] ChoiceCardIds { get; init; } = [];
    public string[] NestedChoiceCardIds { get; init; } = [];
    public bool ClearPlayerHandBeforeUse { get; init; }
    public UnattendedCardInjection[] Cards { get; init; } = [];
    public int? ExpectedPlayerHp { get; init; }
    public int? ExpectedPlayerBlock { get; init; }
    public int? ExpectedPlayerEnergy { get; init; }
    public int? ExpectedPlayerStars { get; init; }
    public int? ExpectedPlayerOrbCapacity { get; init; }
    public int? ExpectedEnemyHp { get; init; }
    public Dictionary<string, int> ExpectedPlayerPowers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentPlayerPowers { get; init; } = [];
    public string[] ExpectedAbsentEnemyPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedPlayerCardUpgrades { get; init; } = new(StringComparer.Ordinal);
    public string? ExpectedSurroundedFacing { get; init; }
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
    public bool UseCurrentMove { get; init; }
    public SearchBoundaryReason? ExpectedSearchBoundary { get; init; }
    public bool? ExpectedSimulatedDynamicResolution { get; init; }
    public int? PlayerHpBefore { get; init; }
    public int? PlayerBlockBefore { get; init; }
    public int? PlayerEnergyBefore { get; init; }
    public int? PlayerStarsBefore { get; init; }
    public int? PlayerGoldBefore { get; init; }
    public int? EnemyHpBefore { get; init; }
    public int? EnemyBlockBefore { get; init; }
    public int? OstyHpBefore { get; init; }
    public int? RoundNumberBefore { get; init; }
    public int? PlayerTurnNumberBefore { get; init; }
    public bool ClearAllRelicsBeforeMove { get; init; }
    public UnattendedRelicInjection[] RelicsBeforeMove { get; init; } = [];
    public bool ClearPlayerOrbsBeforeMove { get; init; }
    public UnattendedOrbInjection[] OrbsBeforeMove { get; init; } = [];
    public bool ClearAllPowersBeforeMove { get; init; }
    public bool ClearPlayerPilesBeforeMove { get; init; }
    public bool ClearPlayerHandBeforeMove { get; init; }
    public string DerivedHookTarget { get; init; } = "Player";
    public int? ExpectedModifiedHandDraw { get; init; }
    public int? ExpectedModifiedMaxEnergy { get; init; }
    public string DerivedHookCardId { get; init; } = string.Empty;
    public int DerivedHookBaseValue { get; init; }
    public int? ExpectedModifiedXValue { get; init; }
    public string DerivedHookOrbId { get; init; } = string.Empty;
    public int? ExpectedModifiedOrbValue { get; init; }
    public int? ExpectedModifiedHandDrawAfterPlayerSetup { get; init; }
    public int? ExpectedModifiedMaxEnergyAfterPlayerSetup { get; init; }
    public string? ExpectedStatefulRelicStateAfterPlayerSetup { get; init; }
    public string? ExpectedMirrorRelicState { get; init; }
    public bool? ExpectedShouldClearBlock { get; init; }
    public bool? ExpectedShouldFlush { get; init; }
    public bool? ExpectedShouldFlushAfterPlayerSetup { get; init; }
    public bool? ExpectedShouldPlayerResetEnergy { get; init; }
    public bool? ExpectedShouldPlayerResetEnergyAfterPlayerSetup { get; init; }
    public bool? ExpectedSimulatedSkipNextMove { get; init; }
    public bool RollNextMoveAfterActual { get; init; }
    public string[] MonsterStateLogBefore { get; init; } = [];
    public bool TriggerPlayerSideTurnEndBeforeMove { get; init; }
    public bool TriggerEnemySideTurnEndBeforeMove { get; init; }
    public bool TriggerPlayerSideTurnEndAfterMove { get; init; }
    public bool TriggerEnemySideTurnEndAfterMove { get; init; }
    public int EnemySideTurnEndTriggerCount { get; init; }
    public bool TriggerPlayerSideTurnStartAfterMove { get; init; }
    public bool TriggerEnemySideTurnStartAfterMove { get; init; }
    public bool TriggerPlayerTurnEndAfterMove { get; init; }
    public bool TriggerPlayerSetupAfterMove { get; init; }
    public string[] PlayerSetupChoiceCardIds { get; init; } = [];
    public bool TriggerAutoPrePlayAfterPlayerSetup { get; init; }
    public string[] AutoPrePlayChoiceCardIds { get; init; } = [];
    public bool KillMonsterAfterMove { get; init; }
    public int? KillEnemyIndexAfterMove { get; init; }
    public UnattendedCardInjection? CardBeforeMove { get; init; }
    public UnattendedCardInjection[] CardsBeforeMove { get; init; } = [];
    public UnattendedCardInjection? CardAfterMove { get; init; }
    public UnattendedCardInjection[] CardsAfterMove { get; init; } = [];
    public UnattendedCardTransformCheck[] CardTransformsAfterMove { get; init; } = [];
    public UnattendedCardInjection? PlayCardAfterMove { get; init; }
    public UnattendedCardPlayCheck[] CardPlayChecksBeforeMove { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterMove { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterPlayerSideTurnEnd { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterPlayerSetup { get; init; } = [];
    public string? LiveEndTurnRiskCardId { get; init; }
    public string LiveEndTurnRiskChoiceSourceId { get; init; } = string.Empty;
    public string[] LiveEndTurnRiskChoiceCardIds { get; init; } = [];
    public string? LiveEndTurnRiskKnowledgeChoiceCardId { get; init; }
    public UnattendedPowerInjection? PowerBeforeMove { get; init; }
    public UnattendedPowerInjection[] PowersBeforeMove { get; init; } = [];
    public UnattendedPowerInjection[] PowersAfterMove { get; init; } = [];
    public string? ExpectedNextMoveId { get; init; }
    public int? ExpectedPlayerHp { get; init; }
    public int? ExpectedPlayerHpLoss { get; init; }
    public int? ExpectedOstyHp { get; init; }
    public int? ExpectedOstyMaxHp { get; init; }
    public Dictionary<string, int> ExpectedOstyPowers { get; init; } = new(StringComparer.Ordinal);
    public int? ExpectedPlayerBlock { get; init; }
    public int? ExpectedPlayerBlockAfterMoveActions { get; init; }
    public int? ExpectedPlayerBlockGain { get; init; }
    public int? ExpectedPlayerEnergy { get; init; }
    public int? ExpectedPlayerStars { get; init; }
    public int? ExpectedPlayerGold { get; init; }
    public int? ExpectedPlayerOrbCapacity { get; init; }
    public int? ExpectedPlayerHandCount { get; init; }
    public int? ExpectedEnemyBlockGain { get; init; }
    public int? ExpectedEnemyHpGain { get; init; }
    public Dictionary<string, int> ExpectedPlayerPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentPlayerPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedEnemyPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentEnemyPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedPlayerPowerStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyPowerStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerPileCards { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerPileCardDamageTotals { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardCosts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardEnchantments { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardUpgrades { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyHpsByModel { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyBlocksByModel { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerOrbs { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedCardPlayCheck
{
    public string CardId { get; init; } = string.Empty;
    public int Occurrence { get; init; }
    public string Target { get; init; } = "Enemy";
    public bool ExpectedPlayable { get; init; } = true;
    public bool UseChoice { get; init; }
    public string[] ChoiceCardIds { get; init; } = [];
    public string[] ExpectedExcludedChoiceCardIds { get; init; } = [];
    public string? ExpectedCardIdAfterPlay { get; init; }
    public string? ExpectedCardPileAfterPlay { get; init; }
    public bool AssertForkableAfterPlay { get; init; }
}

internal sealed class UnattendedCardTransformCheck
{
    public string OriginalCardId { get; init; } = string.Empty;
    public string ReplacementCardId { get; init; } = string.Empty;
    public int Occurrence { get; init; }
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
    public UnattendedStageTiming[] StageTimings { get; init; } = [];
    public string[] CompletedChecks { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class UnattendedSolverMetrics
{
    public SolverSearchPhase Phase { get; init; }
    public SearchBoundaryReason Boundary { get; init; }
    public int SelectedExpanded { get; init; }
    public int SelectedTransitions { get; init; }
    public int SelectedChoiceBranches { get; init; }
    public int ChoiceReplayAttempts { get; init; }
    public int ChoiceReplayBudgetExhaustions { get; init; }
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
    public int ParallelActionReplayWaves { get; init; }
    public int ParallelActionReplayWorkItems { get; init; }
    public int MaxParallelActionReplayConcurrency { get; init; }
    public int DeferredRoundChoiceActions { get; init; }
    public int DeferredRoundChoiceLayerWidthTotal { get; init; }
    public int MaxDeferredRoundChoiceLayerWidth { get; init; }
    public int DeferredRoundChoiceFiniteQuotaFallbacks { get; init; }
    public int DeferredRoundChoiceFinitePrimaryLayers { get; init; }
    public int DeferredRoundChoiceFinitePendingFallbacks { get; init; }
    public int ParallelRoundChoiceReplayWaves { get; init; }
    public int ParallelRoundChoiceReplayWorkItems { get; init; }
    public int MaxParallelRoundChoiceReplayConcurrency { get; init; }
    public int SearchedTurns { get; init; }
    public int ShufflesCrossed { get; init; }
    public double Score { get; init; }
    public int ProjectedBattleHpLost { get; init; }
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
    public bool ConfiguredNoGcRegionEnabled { get; init; }
    public long ConfiguredNoGcRegionBudgetBytes { get; init; }
    public GCLatencyMode GcLatencyMode { get; init; }
    public bool NoGcRegionActive { get; init; }
    public long NoGcRegionBudgetBytes { get; init; }
    public int NoGcRegionRolloverCount { get; init; }
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
