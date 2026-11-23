namespace CombatSolver.Beam;

public sealed record InCombatChoiceCardToken(
    string CardId,
    int UpgradeLevel,
    int OptionOccurrence,
    int SourceOccurrence);

public sealed record InCombatChoiceTrace(
    string Timing,
    string SourceId,
    string SourcePile,
    IReadOnlyList<InCombatChoiceCardToken> Cards);

public sealed record InCombatActionTrace(
    int Turn,
    string Kind,
    string CardId,
    int CardOccurrence,
    int CardUpgradeLevel,
    string CardEnchantmentId,
    int TargetIndex,
    uint? TargetCombatId,
    string PotionId,
    int PotionSlot,
    IReadOnlyList<InCombatChoiceTrace> Choices,
    IReadOnlyList<string> FlashedRelicIds,
    IReadOnlyList<uint> KilledEnemyCombatIds);

public sealed record InCombatTurnOutcomeTrace(
    int Turn,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int SoldHp,
    int MaxBlock,
    int ActualBlock,
    int EnergyLeft,
    int PotionCount,
    int PotionStrategicCost,
    bool CombatEnded);

public sealed record InCombatTerminalSnapshot(
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
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
    int PostCombatRelicHeal,
    int ProjectedBattleHpLost,
    int? CombatEndedTurn,
    int? DeathTurn,
    bool OnlyDeathRoutes,
    ForecastConfidence Confidence,
    ForecastSearchBoundary SearchBoundary,
    bool HasRisk);

public sealed record InCombatBeamResult(
    IReadOnlyList<InCombatActionTrace> Actions,
    IReadOnlyList<InCombatTurnOutcomeTrace> TurnOutcomes,
    InCombatTerminalSnapshot Terminal,
    IReadOnlyList<ForecastPotionUse> PotionUses,
    long ExpandedNodes,
    double SearchElapsedMilliseconds);

public sealed record InCombatProgressUpdate(
    long ExpandedNodes,
    int MaxTurnReached,
    double ElapsedMilliseconds,
    int CurrentBestProjectedHpLoss,
    bool CurrentBestWon,
    int? CurrentBestCombatEndedTurn,
    int CurrentBestPotionCount,
    InCombatBeamResult? CurrentBest);
