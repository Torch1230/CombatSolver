using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Simulation;
using System.Runtime.CompilerServices;

namespace CombatSolver;

internal enum PlanActionKind
{
    PlayCard,
    UsePotion,
    EndTurn,
}

internal enum PlanChoiceEffect
{
    MoveToHand,
    MoveToDrawTop,
    Discard,
    Exhaust,
    Upgrade,
    Transform,
    Duplicate,
    Modify,
    Nightmare,
    DiscardAndDraw,
    MoveToHandFreeThisTurn,
    SetFreeThisCombat,
    ApplySly,
    ApplyEthereal,
    ApplyRetain,
    AutoPlayRepeated,
    GenerateToHand,
    ApplyKnowledgeCurse,
}

internal enum PlanChoiceTiming
{
    Action,
    PlayerTurnEnd,
    EnemyTurn,
    PlayerTurnStart,
}

internal enum SearchBoundaryReason
{
    None,
    Shuffle,
    NoCards,
    UnsupportedEffect,
    DynamicResolution,
    PendingChoice,
    EventDefeat,
    TurnLimit,
    NodeLimit,
    TimeLimit,
}

[Flags]
internal enum SearchRouteTraits
{
    None = 0,
    Scaling = 1 << 0,
    Resource = 1 << 1,
    Control = 1 << 2,
    RevivalWindow = 1 << 3,
    DeclinedExtraTurn = 1 << 4,
    ReactiveDamage = 1 << 5,
    EndTurnDeckCompression = 1 << 6,
    LongTermResource = 1 << 7,
}

[Flags]
internal enum PersistentSetupTraits
{
    None = 0,
    Curious = 1 << 0,
    EchoForm = 1 << 1,
    Buffer = 1 << 2,
    Focus = 1 << 3,
    Thunder = 1 << 4,
    RecurringScaling = 1 << 5,
    OrbEngine = 1 << 6,
}

internal sealed record PlanCardToken(
    string CardId,
    int UpgradeLevel,
    string StateKey,
    int SourceOccurrence,
    int OptionOccurrence,
    string Title);

internal sealed record PlanCardChoice(
    PlanChoiceEffect Effect,
    PileType SourcePile,
    IReadOnlyList<PlanCardToken> Cards,
    string SourceId = "",
    string ContextId = "",
    PlanChoiceTiming Timing = PlanChoiceTiming.Action);

internal sealed record PredictionGap(
    string SourceId,
    string Method,
    string Reason,
    bool Compensated)
{
    public string Key => $"{SourceId}.{Method}";
}

internal sealed record PlanRelicEffect(
    string RelicId,
    string RelicTitle,
    string Summary);

internal sealed record PlanAction(
    PlanActionKind Kind,
    int Turn,
    string CardId = "",
    int CardOccurrence = 0,
    int TargetIndex = -1,
    uint? TargetCombatId = null,
    string CardTitle = "",
    string TargetName = "",
    PlanCardChoice? Choice = null,
    IReadOnlyList<PlanCardChoice>? NestedChoices = null,
    int NestedChoicesBeforePrimary = 0,
    int PotionSlot = -1,
    string PotionId = "",
    string PotionTitle = "",
    IReadOnlyList<PlanCardChoice>? TurnStartChoices = null,
    IReadOnlyList<PlanRelicEffect>? RelicEffects = null,
    int ReplayCount = 0,
    string CardStateKey = "",
    int CardStateOccurrence = 0,
    bool EndsPlayerTurn = false)
{
    public bool IsExecutable => Kind is PlanActionKind.PlayCard or PlanActionKind.UsePotion;
    public string ActionTitle => Kind == PlanActionKind.UsePotion ? PotionTitle : CardTitle;

    public IReadOnlyList<PlanCardChoice> GetActionChoicesInExecutionOrder()
    {
        IReadOnlyList<PlanCardChoice> nested = NestedChoices ?? [];
        if (NestedChoicesBeforePrimary < 0 || NestedChoicesBeforePrimary > nested.Count)
        {
            throw new InvalidOperationException(
                $"动作内前置选择数量越界：{NestedChoicesBeforePrimary}/{nested.Count}。");
        }

        List<PlanCardChoice> ordered = new(nested.Count + (Choice == null ? 0 : 1));
        ordered.AddRange(nested.Take(NestedChoicesBeforePrimary));
        if (Choice != null)
            ordered.Add(Choice);
        ordered.AddRange(nested.Skip(NestedChoicesBeforePrimary));
        return ordered;
    }
}

internal sealed record TurnOutcome(
    int Turn,
    int HpLost,
    int SoldHp,
    int MaxBlock,
    int ActualBlock,
    int EnergyLeft);

internal readonly record struct CombatProgressState(
    int BestEnemyHp,
    int BestAliveEnemyCount,
    int BestOffensiveProgressValue,
    int BestLiveDeckClutter,
    int BestLiveDeckSize,
    int BestOutstandingStolenResource,
    int BestSandpitRemaining,
    int MostProcessedEnemyDeaths,
    int TurnsWithoutProgress)
{
    public static CombatProgressState Capture(SimulationSnapshot snapshot)
        => new(
            snapshot.EnemyHp,
            snapshot.AliveEnemyCount,
            snapshot.OffensiveProgressValue,
            snapshot.LiveDeckClutter,
            snapshot.LiveDeckSize,
            snapshot.OutstandingStolenResource,
            snapshot.SandpitRemaining,
            snapshot.ProcessedEnemyDeaths.Count,
            0);

    public CombatProgressState Advance(SimulationSnapshot snapshot)
    {
        bool progressed = snapshot.EnemyHp < BestEnemyHp
            || snapshot.AliveEnemyCount < BestAliveEnemyCount
            || snapshot.OffensiveProgressValue > BestOffensiveProgressValue
            || snapshot.LiveDeckClutter < BestLiveDeckClutter
            || snapshot.LiveDeckSize < BestLiveDeckSize
            || snapshot.OutstandingStolenResource < BestOutstandingStolenResource
            || snapshot.SandpitRemaining < BestSandpitRemaining
            || snapshot.ProcessedEnemyDeaths.Count > MostProcessedEnemyDeaths;
        return new CombatProgressState(
            Math.Min(BestEnemyHp, snapshot.EnemyHp),
            Math.Min(BestAliveEnemyCount, snapshot.AliveEnemyCount),
            Math.Max(BestOffensiveProgressValue, snapshot.OffensiveProgressValue),
            Math.Min(BestLiveDeckClutter, snapshot.LiveDeckClutter),
            Math.Min(BestLiveDeckSize, snapshot.LiveDeckSize),
            Math.Min(BestOutstandingStolenResource, snapshot.OutstandingStolenResource),
            Math.Min(BestSandpitRemaining, snapshot.SandpitRemaining),
            Math.Max(MostProcessedEnemyDeaths, snapshot.ProcessedEnemyDeaths.Count),
            progressed ? 0 : TurnsWithoutProgress + 1);
    }
}

internal sealed record SearchNode(
    PlanAction? Action,
    int ActionCount,
    int PotionCount,
    int PotionStrategicCost,
    int Turn,
    SearchRouteTraits Traits,
    int FutureSoldHp,
    double Score,
    StateFingerprint StateKey,
    bool HasPredictionRisk,
    SearchBoundaryReason BoundaryReason,
    bool IsTerminal,
    SearchNode? Parent,
    SimulationSnapshot Snapshot,
    CombatProgressState CombatProgress,
    TurnOutcome? Outcome = null,
    string? RepeatableNoProgressCardId = null,
    int RepeatableNoProgressCount = 0,
    IReadOnlyList<PlanCardChoice>? TurnSetupChoices = null,
    ContinuationStamp? TurnSetupPlayState = null)
{
    private IReadOnlyList<PlanAction>? _actions;

    public int RetentionRank { get; set; } = int.MaxValue;
    public IReadOnlyList<PlanAction> Actions => _actions ??= MaterializeActions();

    public IReadOnlyList<PlanCardChoice> GetTurnSetupChoices()
    {
        for (SearchNode? node = this; node != null; node = node.Parent)
        {
            if (node.TurnSetupChoices is { } choices)
                return choices;
        }
        return [];
    }

    public ContinuationStamp? GetTurnSetupPlayState()
    {
        for (SearchNode? node = this; node != null; node = node.Parent)
        {
            if (node.TurnSetupPlayState is { } stamp)
                return stamp;
        }
        return null;
    }

    private IReadOnlyList<PlanAction> MaterializeActions()
    {
        PlanAction[] actions = new PlanAction[ActionCount];
        int index = actions.Length;
        for (SearchNode? node = this; node?.Action is { } action; node = node.Parent)
            actions[--index] = action;
        if (index != 0)
            throw new InvalidOperationException("搜索节点动作链长度不一致。");
        return actions;
    }
}

internal sealed class SimulationSnapshot(
    double score,
    StateFingerprint stateKey,
    StateFingerprint unorderedPileKey,
    StateFingerprint projectedShuffleOrderKey,
    int projectedShuffleOrderValue,
    bool hasRisk,
    bool playerDead,
    bool allEnemiesDead,
    int playerHp,
    int playerMaxHp,
    int cumulativePlayerHpLost,
    int longTermResourceValue,
    LongTermGoals longTermGoals,
    LongTermGoals longTermGoalCardsPlayed,
    int angerCopiesGenerated,
    int projectedPlayerHp,
    int playerBlock,
    int enemyHp,
    int aliveEnemyCount,
    ulong aliveEnemyMask,
    int rawEnemyHp,
    int maxCurrentEnemyHp,
    StateFingerprint enemyCombatDistributionKey,
    int revivingEnemyCount,
    int persistentBuffValue,
    PersistentSetupTraits persistentSetupTraits,
    int latentSetupValue,
    PersistentSetupTraits latentSetupTraits,
    uint? focusTargetCombatId,
    int focusTargetPressure,
    int focusTargetRemainingHp,
    int focusTargetCurrentThreat,
    int focusTargetVulnerableTurns,
    uint? mostVulnerableTargetCombatId,
    int retainedAttackValue,
    int replayPotentialValue,
    int futureResourceValue,
    int delayedDamageValue,
    int reactiveDamageValue,
    int enemyStrengthSuppression,
    int enemyWeakTurns,
    int enemyVulnerableTurns,
    StateFingerprint enemyControlDistributionKey,
    int sandpitRemaining,
    int liveDeckClutter,
    int liveDeckSize,
    int outstandingStolenResource,
    int offensiveProgressValue,
    int energy,
    int stars,
    int historyEntryCount,
    int handCount,
    bool canTriggerArtOfWarNextTurn,
    int pocketwatchCardsPlayedThisTurn,
    int pocketwatchCardsPlayedLastTurn,
    int pocketwatchCardThreshold,
    int potionUseCount,
    int potionStrategicCost,
    int automaticPotionUseCount,
    int turn,
    int shufflesCrossed,
    IReadOnlySet<uint> processedEnemyDeaths,
    SearchBoundaryReason boundaryReason,
    IReadOnlyList<PredictionGap> predictionGaps,
    CombatPredictionSimulator simulator)
{
    private CombatPredictionSimulator? _simulator = simulator;
    private string? _releasedBy;
    private int _releasedAtLine;

    public double Score { get; } = score;
    public StateFingerprint StateKey { get; } = stateKey;
    public StateFingerprint UnorderedPileKey { get; } = unorderedPileKey;
    public StateFingerprint ProjectedShuffleOrderKey { get; } = projectedShuffleOrderKey;
    public int ProjectedShuffleOrderValue { get; } = projectedShuffleOrderValue;
    public bool HasRisk { get; } = hasRisk;
    public bool PlayerDead { get; } = playerDead;
    public bool AllEnemiesDead { get; } = allEnemiesDead;
    public int PlayerHp { get; } = playerHp;
    public int PlayerMaxHp { get; } = playerMaxHp;
    public int CumulativePlayerHpLost { get; } = cumulativePlayerHpLost;
    public int LongTermResourceValue { get; } = longTermResourceValue;
    public LongTermGoals LongTermGoals { get; } = longTermGoals;
    public LongTermGoals LongTermGoalCardsPlayed { get; } = longTermGoalCardsPlayed;
    public int AngerCopiesGenerated { get; } = angerCopiesGenerated;
    public int ProjectedPlayerHp { get; } = projectedPlayerHp;
    public int PlayerBlock { get; } = playerBlock;
    public int EnemyHp { get; } = enemyHp;
    public int AliveEnemyCount { get; } = aliveEnemyCount;
    public ulong AliveEnemyMask { get; } = aliveEnemyMask;
    public int RawEnemyHp { get; } = rawEnemyHp;
    public int MaxCurrentEnemyHp { get; } = maxCurrentEnemyHp;
    public StateFingerprint EnemyCombatDistributionKey { get; } = enemyCombatDistributionKey;
    public int RevivingEnemyCount { get; } = revivingEnemyCount;
    public int PersistentBuffValue { get; } = persistentBuffValue;
    public PersistentSetupTraits PersistentSetupTraits { get; } = persistentSetupTraits;
    public int LatentSetupValue { get; } = latentSetupValue;
    public PersistentSetupTraits LatentSetupTraits { get; } = latentSetupTraits;
    public PersistentSetupTraits StrategicSetupTraits { get; } =
        persistentSetupTraits | latentSetupTraits;
    public uint? FocusTargetCombatId { get; } = focusTargetCombatId;
    public int FocusTargetPressure { get; } = focusTargetPressure;
    public int FocusTargetRemainingHp { get; } = focusTargetRemainingHp;
    public int FocusTargetCurrentThreat { get; } = focusTargetCurrentThreat;
    public int FocusTargetVulnerableTurns { get; } = focusTargetVulnerableTurns;
    public uint? MostVulnerableTargetCombatId { get; } = mostVulnerableTargetCombatId;
    public int RetainedAttackValue { get; } = retainedAttackValue;
    public int ReplayPotentialValue { get; } = replayPotentialValue;
    public int FutureResourceValue { get; } = futureResourceValue;
    public int DelayedDamageValue { get; } = delayedDamageValue;
    public int ReactiveDamageValue { get; } = reactiveDamageValue;
    public int EnemyStrengthSuppression { get; } = enemyStrengthSuppression;
    public int EnemyWeakTurns { get; } = enemyWeakTurns;
    public int EnemyVulnerableTurns { get; } = enemyVulnerableTurns;
    public StateFingerprint EnemyControlDistributionKey { get; } = enemyControlDistributionKey;
    public int SandpitRemaining { get; } = sandpitRemaining;
    public int LiveDeckClutter { get; } = liveDeckClutter;
    public int LiveDeckSize { get; } = liveDeckSize;
    public int OutstandingStolenResource { get; } = outstandingStolenResource;
    public int OffensiveProgressValue { get; } = offensiveProgressValue;
    public int Energy { get; } = energy;
    public int Stars { get; } = stars;
    public int HistoryEntryCount { get; } = historyEntryCount;
    public int HandCount { get; } = handCount;
    public bool CanTriggerArtOfWarNextTurn { get; } = canTriggerArtOfWarNextTurn;
    public int PocketwatchCardsPlayedThisTurn { get; } = pocketwatchCardsPlayedThisTurn;
    public int PocketwatchCardsPlayedLastTurn { get; } = pocketwatchCardsPlayedLastTurn;
    public int PocketwatchCardThreshold { get; } = pocketwatchCardThreshold;
    public int PotionUseCount { get; } = potionUseCount;
    public int PotionStrategicCost { get; } = potionStrategicCost;
    public int AutomaticPotionUseCount { get; } = automaticPotionUseCount;
    public bool CanStillTriggerPocketwatch => PocketwatchCardThreshold >= 0
        && PocketwatchCardsPlayedThisTurn <= PocketwatchCardThreshold;
    public int Turn { get; } = turn;
    public int ShufflesCrossed { get; } = shufflesCrossed;
    public IReadOnlySet<uint> ProcessedEnemyDeaths { get; } = processedEnemyDeaths;
    public SearchBoundaryReason BoundaryReason { get; } = boundaryReason;
    public IReadOnlyList<PredictionGap> PredictionGaps { get; } = predictionGaps;
    public ContinuationStamp? Continuation { get; private set; }

    public CombatPredictionSimulator Simulator => _simulator
        ?? throw new InvalidOperationException(
            $"搜索快照的模拟器已经释放：{_releasedBy ?? "unknown"}:{_releasedAtLine}。");

    public bool HasSimulator => _simulator != null;

    public void SetContinuation(ContinuationStamp continuation)
        => Continuation = continuation;

    public void ReleaseSimulator(
        [CallerMemberName] string caller = "",
        [CallerLineNumber] int line = 0)
    {
        _simulator = null;
        _releasedBy = caller;
        _releasedAtLine = line;
    }
}

/// <summary>
/// 搜索完成后交给运行时的轻量计划。它不能引用 SearchNode 或模拟器快照，
/// 否则短搜结果会在整个深化阶段持续保留完整分支对象图。
/// </summary>
internal sealed record SelectedSearchPlan(
    IReadOnlyList<PlanAction> Actions,
    int ActionCount,
    double Score);

/// <summary>最终路线的只读标量摘要；不持有 CombatPredictionSimulator。</summary>
/// <summary>
/// Whether the pursued cross-combat goals were honoured, and if not, why not.
/// </summary>
internal enum LongTermGoalOutcome
{
    /// <summary>No goal was pursued.</summary>
    Off,

    /// <summary>Honoured at no extra cost.</summary>
    Free,

    /// <summary>Honoured; the price is reported.</summary>
    Paid,

    /// <summary>No route both banked the goals and refrained from wasting their cards.</summary>
    NoCompliantRoute,

    /// <summary>Compliant routes existed but every one of them killed the player.</summary>
    CompliantRouteWouldDie,
}

/// <summary>
/// One alternative the search already found: the best route among those using exactly this set of potions.
/// </summary>
/// <remarks>
/// These come from the final candidate set of the search that just ran, so each line is the best route the beam
/// <em>kept</em> for that potion set, not the best route that exists for it. Treat the numbers as a floor on how
/// well that world line can go, not as an authoritative comparison.
/// </remarks>
internal sealed record RouteWorldLine(
    IReadOnlyList<string> PotionTitles,
    int HpLost,
    int PotionCount,
    bool Won,
    bool IsSelected);

internal sealed record SolverSnapshot(
    bool HasRisk,
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int LongTermResourceValue,
    LongTermGoals LongTermGoals,
    int AngerCopiesGenerated,
    int ProjectedPlayerHp,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int OutstandingStolenResource,
    int Turn,
    int ShufflesCrossed,
    SearchBoundaryReason BoundaryReason,
    IReadOnlyList<PredictionGap> PredictionGaps);

internal sealed record CachedContinuation(
    ContinuationStamp ExpectedState,
    int StartTurnNumber,
    int ForecastOffset);

internal sealed class SolverResult
{
    public SolverSearchPhase SearchPhase { get; internal set; } = SolverSearchPhase.Short;
    public bool DeepSearchTriggered { get; internal set; }
    public bool DeepSearchImprovedResult { get; internal set; }
    public bool SingleSessionSearch { get; internal set; }
    public TimeSpan ShortSearchElapsed { get; internal set; }
    public TimeSpan DeepSearchElapsed { get; internal set; }
    public TimeSpan TotalSearchElapsed { get; internal set; }
    public long TotalWorkerAllocatedBytes { get; internal set; }
    public int ShortExpandedNodes { get; internal set; }
    public int DeepExpandedNodes { get; internal set; }
    public int ShortTransitionCount { get; internal set; }
    public int DeepTransitionCount { get; internal set; }
    public int TotalGen0Collections { get; internal set; }
    public int TotalGen1Collections { get; internal set; }
    public int TotalGen2Collections { get; internal set; }
    public TimeSpan TotalGcPauseDuration { get; internal set; }
    public TimeSpan TotalMaxObservedGcPause { get; internal set; }
    public int MainThreadFrameCount { get; internal set; }
    public int MainThreadFramesOver33Milliseconds { get; internal set; }
    public double MaxMainThreadFrameGapMilliseconds { get; internal set; }
    public double P95MainThreadFrameGapMilliseconds { get; internal set; }
    public double P99MainThreadFrameGapMilliseconds { get; internal set; }
    public int MainThreadFramesOver50Milliseconds { get; internal set; }
    public int MainThreadFramesOver100Milliseconds { get; internal set; }
    public SearchPhaseMetric ForkMetric { get; internal set; }
    public SearchPhaseMetric ActionMetric { get; internal set; }
    public SearchPhaseMetric CardExecutionMetric { get; internal set; }
    public SearchPhaseMetric CardPostProcessingMetric { get; internal set; }
    public SearchPhaseMetric PotionExecutionMetric { get; internal set; }
    public SearchPhaseMetric RoundAdvanceMetric { get; internal set; }
    public SearchPhaseMetric RoundPlayerEndMetric { get; internal set; }
    public SearchPhaseMetric RoundEndSimulationMetric { get; internal set; }
    public SearchPhaseMetric RoundFlushMetric { get; internal set; }
    public SearchPhaseMetric RoundPlayerEndPowersMetric { get; internal set; }
    public SearchPhaseMetric RoundEnemyTurnMetric { get; internal set; }
    public SearchPhaseMetric RoundEnemyStartMetric { get; internal set; }
    public SearchPhaseMetric RoundEnemyMovesMetric { get; internal set; }
    public SearchPhaseMetric RoundEnemyEndPowersMetric { get; internal set; }
    public SearchPhaseMetric RoundPlayerStartMetric { get; internal set; }
    public SearchPhaseMetric RoundDrawMetric { get; internal set; }
    public SearchPhaseMetric SnapshotMetric { get; internal set; }
    public SearchPhaseMetric ThreatProjectionMetric { get; internal set; }
    public SearchPhaseMetric FingerprintMetric { get; internal set; }
    public SearchPhaseMetric ProjectedShuffleMetric { get; internal set; }
    public SearchPhaseMetric PileFingerprintMetric { get; internal set; }
    public SearchPhaseMetric PileFingerprintMissMetric { get; internal set; }
    public SearchPhaseMetric CardFingerprintMissMetric { get; internal set; }
    public SearchPhaseMetric CombatFingerprintMetric { get; internal set; }
    public SearchPhaseMetric PruneMetric { get; internal set; }
    public SearchPhaseMetric FinalSelectionMetric { get; internal set; }
    public required int StartTurnNumber { get; init; }
    public required IReadOnlyList<PlanCardChoice> TurnSetupChoices { get; init; }
    public ContinuationStamp? TurnSetupPlayState { get; init; }
    public required SelectedSearchPlan BestNode { get; init; }
    public required SolverSnapshot Snapshot { get; init; }
    public required IntentForecast Forecast { get; init; }
    public required int ExpandedNodes { get; init; }
    public required int DominatedActionsPruned { get; init; }
    public required int TopQueueActionsDropped { get; init; }
    public required int DuplicateCardBranchesPruned { get; init; }
    public required int ChoiceBranchesEvaluated { get; init; }
    public required int ShuffleBranchesPruned { get; init; }
    public required int SoldHpBranchesPruned { get; init; }
    public required int ReplayCount { get; init; }
    public required int ForkCount { get; init; }
    public required int TransitionCount { get; init; }
    public required int ReusedNodeSnapshots { get; init; }
    public required int TranspositionBranchesPruned { get; init; }
    public required int RepeatableNoProgressBranchesPruned { get; init; }
    public required int StandPatProbes { get; init; }
    public int ParallelExpansionWaves { get; init; }
    public int ParallelExpansionWorkItems { get; init; }
    public int MaxParallelExpansionConcurrency { get; init; }
    public int NodeLimitSnapshotsReleased { get; init; }
    public required int TransitionCacheHits { get; init; }
    public required long WorkerAllocatedBytes { get; init; }
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required TimeSpan GcPauseDuration { get; init; }
    public TimeSpan MaxObservedGcPause { get; init; }
    public required int WorkerYieldCount { get; init; }
    public int FrameRecoveryWaitCount { get; init; }
    public TimeSpan FrameRecoveryWaitDuration { get; init; }
    public required int SearchedTurns { get; init; }
    public required SearchBoundaryReason BoundaryReason { get; init; }
    public required int UnavoidableHpLost { get; init; }
    public required int SoldHp { get; init; }
    public required int FutureSoldHp { get; init; }
    public required int BattleHpLostSoFar { get; init; }
    public required int ProjectedBattleHpLost { get; init; }
    public required int BattlePotionsUsedSoFar { get; init; }
    public required int PotionCount { get; init; }
    public int ProjectedBattlePotionCount => BattlePotionsUsedSoFar + PotionCount;
    public required int PotionHpSaved { get; internal set; }
    public required LongTermGoals RequiredLongTermGoals { get; init; }
    public required LongTermGoals BankedLongTermGoals { get; init; }
    public required int LongTermGoalHpPrice { get; init; }
    public required int LongTermGoalPotionPrice { get; init; }
    public required IReadOnlyList<RouteWorldLine> WorldLines { get; init; }
    public required LongTermGoalOutcome LongTermGoalOutcome { get; init; }
    public required int CompliantRouteCount { get; init; }
    public required int PotionHpRequired { get; init; }
    public required int PotionBranchesRejected { get; init; }
    public required SolverTheftPolicy? TheftPolicy { get; init; }
    public required int OutstandingStolenResource { get; init; }
    public required int SoldHpThreshold { get; init; }
    public required IReadOnlyDictionary<int, int> SoldHpByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> HpLostByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> MaxBlockByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> ActualBlockByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> EnergyLeftByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> PotionCountByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> PotionStrategicCostByTurn { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction { get; init; }
    public required int? CombatEndedTurn { get; init; }
    public required int? DeathTurn { get; init; }
    public required bool OnlyDeathRoutesFound { get; init; }
    public required bool IsActEndingBoss { get; init; }
    public required BossHpRelief BossHpRelief { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<CachedContinuation> Continuations { get; init; }
    public bool WasReused { get; init; }
    public int? ReusedFromTurn { get; init; }
    public bool RecalculatedAfterCompleteProjection { get; internal set; }
    public int? PreviousProjectedBattleHpLost { get; internal set; }
    public int ProjectedBattleHpLossIncrease => PreviousProjectedBattleHpLost is { } previous
        ? Math.Max(0, ProjectedBattleHpLost - previous)
        : 0;
    public string? RecalculationStateDifference { get; internal set; }

    public bool TryCreateContinuation(
        ContinuationStamp actual,
        int currentHp,
        BattleDamageSnapshot battleDamage,
        out SolverResult? continuation)
    {
        CachedContinuation? cached = Continuations.FirstOrDefault(item => item.ExpectedState == actual);
        if (cached == null)
        {
            continuation = null;
            return false;
        }
        if (!BestNode.Actions.Any(action => action.Turn == cached.StartTurnNumber))
        {
            continuation = null;
            return false;
        }

        int searchedTurns = Math.Max(1, BestNode.Actions
            .Select(action => action.Turn)
            .Where(turn => turn >= cached.StartTurnNumber)
            .DefaultIfEmpty(cached.StartTurnNumber)
            .Max() - cached.StartTurnNumber + 1);
        int totalRemainingLoss = Math.Max(0, currentHp - Snapshot.PlayerHp);
        int remainingPotionCount = PotionCountByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value);
        int remainingPotionCost = PotionStrategicCostByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value);
        Dictionary<int, int> soldByTurn = SoldHpByTurn.ToDictionary(item => item.Key, item => item.Value);
        int remainingSold = Math.Min(totalRemainingLoss, SoldHpByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value));
        int unavoidable = Math.Max(0, battleDamage.HpLostSoFar - battleDamage.SoldHpCommitted)
            + Math.Max(0, totalRemainingLoss - remainingSold);
        IntentForecast slicedForecast = new()
        {
            Rounds = Forecast.Rounds.Skip(cached.ForecastOffset).ToList(),
            HasUnsupportedIntent = Forecast.HasUnsupportedIntent,
            IsExactForModeledDamage = Forecast.IsExactForModeledDamage,
            UnsupportedDetails = Forecast.UnsupportedDetails,
            ApproximationDetails = Forecast.ApproximationDetails,
            MonsterAiCountersByRound = Forecast.MonsterAiCountersByRound.Skip(cached.ForecastOffset).ToList(),
        };
        continuation = new SolverResult
        {
            StartTurnNumber = cached.StartTurnNumber,
            TurnSetupChoices = TurnSetupChoices,
            TurnSetupPlayState = TurnSetupPlayState,
            BestNode = BestNode,
            Snapshot = Snapshot,
            Forecast = slicedForecast,
            ExpandedNodes = 0,
            DominatedActionsPruned = 0,
            TopQueueActionsDropped = 0,
            DuplicateCardBranchesPruned = 0,
            ChoiceBranchesEvaluated = 0,
            ShuffleBranchesPruned = 0,
            SoldHpBranchesPruned = 0,
            ReplayCount = 0,
            ForkCount = 0,
            TransitionCount = 0,
            ReusedNodeSnapshots = 1,
            TranspositionBranchesPruned = 0,
            RepeatableNoProgressBranchesPruned = 0,
            StandPatProbes = 0,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = 0,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            GcPauseDuration = TimeSpan.Zero,
            WorkerYieldCount = 0,
            SearchedTurns = searchedTurns,
            BoundaryReason = BoundaryReason,
            UnavoidableHpLost = unavoidable,
            SoldHp = battleDamage.SoldHpCommitted + remainingSold,
            FutureSoldHp = remainingSold,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            ProjectedBattleHpLost = battleDamage.HpLostSoFar + totalRemainingLoss,
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            PotionCount = remainingPotionCount,
            PotionHpSaved = remainingPotionCount == 0 ? 0 : PotionHpSaved,
            RequiredLongTermGoals = RequiredLongTermGoals,
            BankedLongTermGoals = BankedLongTermGoals,
            LongTermGoalHpPrice = LongTermGoalHpPrice,
            LongTermGoalPotionPrice = LongTermGoalPotionPrice,
            WorldLines = WorldLines,
            LongTermGoalOutcome = LongTermGoalOutcome,
            CompliantRouteCount = CompliantRouteCount,
            PotionHpRequired = remainingPotionCost,
            PotionBranchesRejected = 0,
            TheftPolicy = TheftPolicy,
            OutstandingStolenResource = Snapshot.OutstandingStolenResource,
            SoldHpThreshold = SoldHpThreshold,
            SoldHpByTurn = soldByTurn,
            HpLostByTurn = HpLostByTurn,
            MaxBlockByTurn = MaxBlockByTurn,
            ActualBlockByTurn = ActualBlockByTurn,
            EnergyLeftByTurn = EnergyLeftByTurn,
            PotionCountByTurn = PotionCountByTurn,
            PotionStrategicCostByTurn = PotionStrategicCostByTurn,
            KillsAfterAction = KillsAfterAction,
            CombatEndedTurn = CombatEndedTurn,
            DeathTurn = DeathTurn,
            OnlyDeathRoutesFound = OnlyDeathRoutesFound,
            IsActEndingBoss = IsActEndingBoss,
            BossHpRelief = BossHpRelief,
            Elapsed = TimeSpan.Zero,
            Continuations = Continuations.Where(item => item.StartTurnNumber > cached.StartTurnNumber).ToList(),
            WasReused = true,
            ReusedFromTurn = StartTurnNumber,
            RecalculatedAfterCompleteProjection = RecalculatedAfterCompleteProjection,
            PreviousProjectedBattleHpLost = PreviousProjectedBattleHpLost,
            RecalculationStateDifference = RecalculationStateDifference,
        };
        return true;
    }

    public string Format()
    {
        List<string> lines =
        [
            "[b]战斗路线求解器[/b]",
            $"洗牌边界前预计：玩家 {Snapshot.PlayerHp} HP / {Snapshot.PlayerBlock} 格挡；敌方合计 {Snapshot.EnemyHp} HP",
            $"置信度：{ConfidenceText()}　展开 {ExpandedNodes} 节点　{Elapsed.TotalMilliseconds:F0} ms",
            $"动态范围：{SearchedTurns} 回合，边界 {BoundaryReason}；洗牌分支停止 {ShuffleBranchesPruned}",
            $"本局战损：已发生 {BattleHpLostSoFar}，路线预计累计 {ProjectedBattleHpLost}；主动卖血 {SoldHp}/{SoldHpThreshold}",
            BattlePotionsUsedSoFar > 0
                ? $"本局已喝药：{BattlePotionsUsedSoFar} 瓶；路线还需使用 {PotionCount} 瓶"
                : $"路线预计用药：{PotionCount} 瓶",
            "",
        ];
        if (TheftPolicy is { } theftPolicy)
        {
            lines.Insert(
                lines.Count - 1,
                $"偷窃策略：{(theftPolicy == SolverTheftPolicy.PreserveResources ? "保牌/保钱" : "放走")}；未追回资源 {OutstandingStolenResource}");
        }

        for (int turn = StartTurnNumber; turn < StartTurnNumber + SearchedTurns; turn++)
        {
            List<(PlanAction Action, int Index)> indexedActions = BestNode.Actions
                .Select((action, index) => (Action: action, Index: index))
                .Where(item => item.Action.Turn == turn && item.Action.IsExecutable)
                .ToList();
            string playText = indexedActions.Count == 0
                ? "直接结束"
                : string.Join(" | ", indexedActions.Select(item => DescribeWithKills(item.Action, item.Index)));
            string hpLoss = HpLostByTurn.GetValueOrDefault(turn) > 0
                ? $"　[color=#ef6b6b]预计掉血 {HpLostByTurn[turn]}[/color]"
                : string.Empty;
            string combatEnd = CombatEndedTurn == turn
                ? "　[color=#73c991][b]战斗结束[/b][/color]"
                : string.Empty;
            lines.Add($"[b]第 {turn} 回合[/b]　{playText}{hpLoss}{combatEnd}");
        }

        lines.Add("");
        lines.Add("[color=#d5b46a]评分：不死优先；区分不可避免战损与主动卖血，并综合击杀、输出、易伤、能力牌和费用利用。[/color]");
        IReadOnlyList<string> unmirrored = UnmirroredDetails();
        if (unmirrored.Count > 0)
            lines.Add($"[color=#e86b6b]未镜像：{string.Join("、", unmirrored)}[/color]");
        IReadOnlyList<string> compensated = CompensatedDetails();
        if (compensated.Count > 0)
            lines.Add($"[color=#73c991]求解器已补偿：{string.Join("、", compensated)}[/color]");
        if (Forecast.ApproximationDetails.Count > 0)
            lines.Add($"[color=#e8a05b]近似预测：{string.Join("、", Forecast.ApproximationDetails)}[/color]");
        return string.Join('\n', lines);
    }

    public string ConfidenceText()
    {
        if (Forecast.HasUnsupportedIntent || Snapshot.PredictionGaps.Any(gap => !gap.Compensated))
            return "低（存在未镜像效果）";
        if (!Forecast.IsExactForModeledDamage)
            return "中（后续状态按当前数值估算）";
        return "中高（攻击、牌序与 RNG 已建模）";
    }

    public IReadOnlyList<string> UnmirroredDetails()
    {
        return Snapshot.PredictionGaps
            .Where(gap => !gap.Compensated)
            .Select(gap => $"{gap.Key} [{gap.Reason}]")
            .Concat(Forecast.UnsupportedDetails)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<string> CompensatedDetails()
    {
        return Snapshot.PredictionGaps
            .Where(gap => gap.Compensated)
            .Select(gap => gap.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string Describe(PlanAction action)
    {
        string title = action.Kind switch
        {
            PlanActionKind.UsePotion => $"{action.PotionTitle}（药水）",
            PlanActionKind.EndTurn => "结束回合",
            _ => action.CardTitle,
        };
        string card = string.IsNullOrEmpty(action.TargetName)
            ? title
            : $"{title}→{action.TargetName}";
        if (action.RelicEffects is { Count: > 0 })
            card += $" [{string.Join("、", action.RelicEffects.Select(DescribeRelicEffect))}]";
        if (action.Choice == null)
            return card;
        if (action.Choice.Cards.Count == 0)
            return $"{card}（不选）";
        string chosen = string.Join("、", action.Choice.Cards.Select(item => item.Title));
        return $"{card}（选 {chosen}）";
    }

    private static string DescribeRelicEffect(PlanRelicEffect effect)
        => string.IsNullOrEmpty(effect.Summary)
            ? effect.RelicTitle
            : $"{effect.RelicTitle}{effect.Summary}";

    public string DescribeWithKills(PlanAction action, int actionIndex)
    {
        string text = Describe(action);
        if (!KillsAfterAction.TryGetValue(actionIndex, out IReadOnlyList<string>? kills) || kills.Count == 0)
            return text;
        return $"{text} [color=#73c991][b]击杀 {string.Join("、", kills)}[/b][/color]";
    }
}
