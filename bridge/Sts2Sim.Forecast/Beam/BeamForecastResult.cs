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

public sealed record PreCombatPortfolioOptions(bool UseBeamWidthPortfolio);

public sealed class OpeningChoiceUnsupportedException(string message, string? fieldPath = null)
    : InvalidOperationException(message)
{
    public string? FieldPath { get; } = fieldPath;
}

public sealed class ProjectionUnsupportedException(string message) : InvalidOperationException(message);
