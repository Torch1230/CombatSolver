namespace CombatSolver.Beam;

public enum ForecastSearchBoundary
{
    None,
    NodeLimit,
    TimeLimit,
    TurnLimit,
    PendingChoice,
    EventDefeat,
    UnsupportedEffect,
}

public enum ForecastConfidence
{
    Complete,
    Bounded,
    DeathOnly,
}

public sealed record ForecastPotionUse(string Id, int Turn, int Slot);

public sealed record BeamForecastResult(
    IReadOnlyList<PlannedAction> Actions,
    bool Won,
    int ProjectedHpLoss,
    int FinalHp,
    int? CombatEndedTurn,
    bool OnlyDeathRoutes,
    ForecastConfidence Confidence,
    ForecastSearchBoundary SearchBoundary,
    IReadOnlyList<ForecastPotionUse> PotionUses,
    long ExpandedNodes,
    double SearchElapsedMilliseconds);

public enum PortfolioSchedulingMode
{
    Parallel,
    Sequential,
}

public sealed record PreCombatPortfolioOptions(
    bool UseBeamWidthPortfolio,
    int PortfolioOptionsVersion = 1,
    IReadOnlyList<int>? BeamWidthPortfolioWidths = null,
    bool BeamWidthPortfolioPlainBaselineMember = true,
    bool ReallocatedRefinementPortfolio = true,
    bool OffensiveRefinementPortfolio = false,
    bool BoundedOffensiveRefinementPortfolio = false,
    bool StopPortfolioAtHpTarget = true,
    bool UseNoveltyPortfolio = false,
    bool AdaptiveNoveltyRefinement = false,
    int NoveltyWidth = 2,
    int NoveltyFamiliarAllowance = 0,
    int NoveltyMaxOpen = 2048,
    int NoveltyMaxEntries = 1_000_000,
    int NoveltyMaxActions = 128,
    int NoveltyMaxTurns = 32,
    int NoveltyBudgetTimeDivisor = 2,
    int NoveltyBudgetMaximumMilliseconds = 5_000,
    int NoveltyBudgetMaximumExpandedNodes = 2_500,
    bool UseEarlyTurnExploration = false,
    int EarlyTurnExplorationDepth = 0,
    int EarlyTurnExplorationBudgetMilliseconds = 0,
    PortfolioSchedulingMode SchedulingMode = PortfolioSchedulingMode.Parallel)
{
    public bool UsesParentNodeBudget => PortfolioOptionsVersion >= 2;
}

public sealed class OpeningChoiceUnsupportedException(string message, string? fieldPath = null)
    : InvalidOperationException(message)
{
    public string? FieldPath { get; } = fieldPath;
}

public sealed class ProjectionUnsupportedException(string message) : InvalidOperationException(message);
