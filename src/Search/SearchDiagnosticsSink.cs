namespace CombatSolver;

internal sealed class SearchDiagnosticsSink(
    Action<string> info,
    Action<string> debug,
    SearchPathObserver? pathObserver = null)
{
    public SearchPathObserver? PathObserver { get; } = pathObserver;

    public void Info(string message) => info(message);

    public void Debug(string message) => debug(message);
}

// Observation is opt-in and never participates in candidate acceptance. Both delegates may
// run on expansion workers: an injected collector owns its synchronization and output limits.
internal sealed class SearchPathObserver(
    Func<StateFingerprint, bool> wantsState,
    Action<SearchPathObservation> observe,
    Func<StateFingerprint, bool>? wantsRetentionPool = null)
{
    public bool WantsState(StateFingerprint stateKey) => wantsState(stateKey);

    public bool ObservesRetentionPools => wantsRetentionPool != null;

    public bool WantsRetentionPool(StateFingerprint stateKey)
        => wantsRetentionPool?.Invoke(stateKey) == true;

    public void Observe(SearchPathObservation observation) => observe(observation);
}

internal enum SearchPathObservationStage
{
    Root,
    Generated,
    AdmissionTransposition,
    ExpansionTransposition,
    Expanded,
    ExpansionBlocked,
    ActionAdmitted,
    PruneInput,
    PruneFinal,
    TurnInput,
    TurnAnnotated,
    TurnDropped,
    RetentionPoolInput,
    GlobalRetention,
    RetentionPoolFinal,
}

internal readonly record struct SearchPathPolicyLabel(
    int PotionCount,
    int PotionStrategicCost,
    int FutureSoldHp,
    int CumulativePlayerHpLost,
    int ActionCount,
    double Score);

internal readonly record struct SearchPathRoutingChoiceSignature(
    int Turn,
    string SourceId,
    PlanChoiceEffect Effect,
    string Pile,
    string CardId,
    int Upgrade,
    string CardStateKey,
    int Occurrence,
    string ContextId,
    int StateContext,
    StateFingerprint EnemyCombatDistributionKey,
    StateFingerprint EnemyControlDistributionKey,
    StateFingerprint UnorderedPileKey);

internal readonly record struct SearchPathEvaluationValues(
    int Energy,
    int Stars,
    int PlayerBlock,
    int ProjectedPlayerHp,
    int HandCount,
    int ReachableHandValue,
    int ZeroCostPlayableCount,
    int LiveDeckSize,
    int LiveDeckClutter,
    int PersistentBuffValue,
    int StrategicRetentionValue,
    int LatentSetupValue,
    int RetainedAttackValue,
    int ReplayPotentialValue,
    int FutureResourceValue,
    int DelayedDamageValue,
    int ReactiveDamageValue,
    int EnemyStrengthSuppression,
    int EnemyWeakTurns,
    int EnemyVulnerableTurns,
    int SandpitRemaining,
    int FocusTargetPressure,
    int ProjectedShuffleOrderValue,
    int LongTermResourceValue);

// All indexes, including RawRank, are zero-based; null means absent or not evaluated.
// ParentRetentionRank is the immediate parent's value, not a routing-family minimum.
// An option leader may still be excluded from routing or required by their original caps.
internal sealed record SearchPathRetentionDetails(
    int? PoolIndex = null,
    int? RawRank = null,
    int? RequiredIndex = null,
    int? RoutingIndex = null,
    int? SelectedIndex = null,
    double? BeamRankScore = null,
    int? OffensiveProgressValue = null,
    int? ParentRetentionRank = null,
    int? Limit = null,
    int? EffectiveLimit = null,
    int? RoutingQuota = null,
    int? RoutingLimit = null,
    int? RawCount = null,
    int? RequiredCount = null,
    int? RoutingCount = null,
    int? SelectedCount = null,
    SearchPathRoutingChoiceSignature? RoutingChoiceSignature = null,
    bool? IsRoutingOptionLeader = null,
    SearchPathEvaluationValues? Evaluation = null);

// Arrays/choices are detached value copies. This must not acquire a SearchNode, snapshot,
// simulator, model, ledger, lazy enumerable, or callback that retains one of those objects.
internal sealed record SearchPathObservation(
    Guid SolverId,
    SolverSearchPhase Phase,
    int BeamWidth,
    SearchPathObservationStage Stage,
    string Reason,
    int BoundaryId,
    StateFingerprint StateKey,
    StateFingerprint? ParentStateKey,
    int Turn,
    int ActionCount,
    SearchPathPolicyLabel PolicyLabel,
    SearchPathPolicyLabel? ParentPolicyLabel,
    SearchRouteTraits Traits,
    TurnOutcome? Outcome,
    SearchBoundaryReason BoundaryReason,
    bool IsTerminal,
    bool HasPredictionRisk,
    int PlayerHp,
    int PlayerMaxHp,
    int EnemyHp,
    int ShufflesCrossed,
    int CumulativeEnemyHpLost,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanCardChoice> RootTurnSetupChoices)
{
    public SearchPathRetentionDetails? Retention { get; init; }

    public int PotionCount => PolicyLabel.PotionCount;
    public int PotionStrategicCost => PolicyLabel.PotionStrategicCost;
    public int FutureSoldHp => PolicyLabel.FutureSoldHp;
    public int CumulativePlayerHpLost => PolicyLabel.CumulativePlayerHpLost;
    public double Score => PolicyLabel.Score;
}
