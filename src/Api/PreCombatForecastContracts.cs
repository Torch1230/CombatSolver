using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;

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

/// <summary>
/// A known non-combat map point between the live position and the forecast target. The worker records these steps
/// without executing their choices, so floor-, coordinate-, and room-history-dependent combat setup matches the
/// requested route while player-state changes remain the caller's explicitly labelled responsibility.
/// </summary>
public sealed record PreCombatMapStep(
    MapCoord Coordinate,
    RoomType RoomType,
    MapPointType MapPointType);

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

    /// <summary>
    /// Optional player HP at combat entry for an explicitly labeled hypothetical branch, such as resting at a
    /// guaranteed campfire before a boss. The worker applies it before combat-start hooks run.
    /// </summary>
    public int? PlayerCurrentHpOverride { get; init; }

    /// <summary>
    /// Ordered known non-combat points between the live position and the target. Event steps are rejected because an
    /// unresolved event option can advance combat-relevant RNG; callers should wait until that option is resolved.
    /// </summary>
    public IReadOnlyList<PreCombatMapStep> InterveningMapPoints { get; init; } = [];

    /// <summary>
    /// When true, caller cancellation owns and stops the isolated worker instead of detaching from a shared
    /// request. Use this for an explicit, visible manual operation with a Stop control. The default preserves
    /// shared request deduplication for lightweight callers.
    /// </summary>
    public bool CancelWorkerWhenCallerCancels { get; init; }

    /// <summary>
    /// Ignore a successful cached result and launch a fresh isolated search. Intended for an explicit Recalculate
    /// action where the caller exposes the cost and progress to the player.
    /// </summary>
    public bool ForceRefresh { get; init; }

    /// <summary>
    /// Stop the isolated worker after this request has returned to its reusable barrier. This changes only worker
    /// lifetime; it does not participate in forecast caching or combat semantics.
    /// </summary>
    public bool CloseWorkerAfterRequest { get; init; }

    /// <summary>
    /// Idle lifetime for a retained worker. Null disables automatic idle shutdown. This changes only worker lifetime
    /// and is deliberately excluded from deterministic forecast cache keys.
    /// </summary>
    public int? WorkerIdleTimeoutMilliseconds { get; init; } =
        PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds;

    internal ulong? SimulationSeed { get; init; }
}

/// <summary>Request-level limits for one explicitly hypothetical combat sample.</summary>
public sealed record PreCombatSimulationOptions
{
    public static PreCombatSimulationOptions Default { get; } = new();

    /// <summary>Search time inside the isolated worker.</summary>
    public int SearchBudgetMilliseconds { get; init; } = 8_000;

    /// <summary>Wall-clock deadline including worker startup.</summary>
    public int OverallTimeoutMilliseconds { get; init; } = 60_000;

    /// <summary>Optional worker search parallelism. Null follows the user's Combat Solver setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// Caller-owned sample seed used only inside the isolated worker for encounter composition, monster HP,
    /// opening shuffle, monster AI, and the other combat RNG streams.
    /// </summary>
    public ulong SampleSeed { get; init; }

    /// <summary>Stop the isolated worker after this sample has returned to its reusable barrier.</summary>
    public bool CloseWorkerAfterRequest { get; init; }

    /// <summary>Idle lifetime for a retained worker. Null keeps it alive until explicitly stopped.</summary>
    public int? WorkerIdleTimeoutMilliseconds { get; init; } =
        PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds;
}

/// <summary>Read-only process and memory information for the reusable isolated worker.</summary>
public sealed record PreCombatWorkerStatus
{
    public required bool IsRunning { get; init; }
    public required bool IsBusy { get; init; }
    public int? ProcessId { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateMemoryBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public bool AudioMuted { get; init; }
    /// <summary>Configured idle lifetime. Null means automatic idle shutdown is disabled.</summary>
    public int? IdleTimeoutMilliseconds { get; init; }
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
