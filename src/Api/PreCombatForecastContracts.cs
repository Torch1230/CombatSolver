namespace CombatSolver.Api;

/// <summary>Stable status values returned by the pre-combat forecast API.</summary>
public enum PreCombatForecastStatus
{
    Succeeded,
    Unsupported,
    LiveStateChanged,
    TimedOut,
    Cancelled,
    Failed,
}

/// <summary>How much of the combat the selected search route proved.</summary>
public enum PreCombatForecastConfidence
{
    Complete,
    Bounded,
    DeathOnly,
}

/// <summary>The kind of combat room that will host the encounter.</summary>
public enum PreCombatRoomKind
{
    Normal,
    Elite,
    Boss,
}

/// <summary>The map-point identity that led to the combat room.</summary>
public enum PreCombatMapPointKind
{
    Normal,
    Elite,
    Boss,
    Unknown,
}

/// <summary>Request-level limits for an isolated pre-combat search.</summary>
public sealed record PreCombatForecastOptions
{
    public static PreCombatForecastOptions Default { get; } = new();

    /// <summary>
    /// Search time inside the worker. The public API intentionally defaults to the short-search phase so a map
    /// preview cannot occupy the machine for the normal deep-search budget.
    /// </summary>
    public int SearchBudgetMilliseconds { get; init; } = 8_000;

    /// <summary>Wall-clock deadline including worker startup and shutdown.</summary>
    public int OverallTimeoutMilliseconds { get; init; } = 60_000;

    /// <summary>Optional worker search parallelism. Null follows the user's Combat Solver setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>A potion action present in the selected route.</summary>
public sealed record PreCombatPotionUse(
    string Id,
    string Title,
    int Turn,
    int Slot);

/// <summary>Immutable response from an isolated pre-combat search.</summary>
public sealed record PreCombatForecastResult
{
    public required PreCombatForecastStatus Status { get; init; }
    public required string RequestId { get; init; }
    public int? ProjectedHpLoss { get; init; }
    public IReadOnlyList<PreCombatPotionUse> PotionUses { get; init; } = [];
    public string? SearchBoundary { get; init; }
    public PreCombatForecastConfidence? Confidence { get; init; }
    public int? FinalHp { get; init; }
    public int? CombatEndedTurn { get; init; }
    public double? SearchElapsedMilliseconds { get; init; }
    public double? TotalElapsedMilliseconds { get; init; }
    public string? Error { get; init; }
    public string? DiagnosticLogPath { get; init; }

    public bool IsSuccess => Status == PreCombatForecastStatus.Succeeded;
}
