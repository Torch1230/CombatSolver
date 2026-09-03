using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

/// <summary>Classifies how a generated card result is determined for projection policy.</summary>
internal enum CardGenerationResultKind
{
    /// <summary>The generated card identity or visible state depends on prediction RNG.</summary>
    Random,

    /// <summary>The result is deterministic but depends on runtime context that is useful to surface.</summary>
    Contextual,

    /// <summary>The source always produces an already-described card result that does not need a prediction tip.</summary>
    Fixed
}

/// <summary>Identifies the gameplay source that caused a damage event.</summary>
internal enum CombatDamageSourceKind
{
    Unknown,
    Card,
    Potion,
    Power,
    Relic,
    Poison,
    Thorns,
    Orb,
    MonsterMove,
}

/// <summary>
/// Immutable provenance retained with damage history. The id is the model id for model-backed sources and is
/// intentionally scalar so history forks do not retain mutable simulator objects.
/// </summary>
internal readonly record struct CombatDamageSource(CombatDamageSourceKind Kind, string? Id)
{
    public static CombatDamageSource Unknown => new(CombatDamageSourceKind.Unknown, null);

    public static CombatDamageSource For(CombatDamageSourceKind kind, string? id = null)
        => new(kind, id);
}

/// <summary>
/// Immutable card data retained by prediction history. History consumers only need these
/// scalar fields; retaining a PredictedCard would keep its owner pile and simulator graph alive.
/// </summary>
internal readonly record struct CombatPredictionCardSnapshot(
    CardModel Original,
    Player Owner,
    string Id,
    int UpgradeLevel,
    CardType Type)
{
    public static CombatPredictionCardSnapshot Capture(PredictedCard card)
    {
        CardModel preview = card.Preview;
        return new CombatPredictionCardSnapshot(
            card.Original,
            preview.Owner,
            preview.Id.Entry,
            preview.CurrentUpgradeLevel,
            preview.Type);
    }
}

/// <summary>
/// Represents one identity-based occurrence in a combat prediction timeline.
/// </summary>
internal abstract class CombatPredictionHistoryEntry
{
    // The property setters are provided for the sake of object initializers; they should not be modified
    // after construction.
    public int Index { get; set; }
    public PredictionTraceFrame? Trace { get; set; }
}

internal sealed class CombatPredictionRiskEntry : CombatPredictionHistoryEntry
{
    public required PredictionRiskReason Reason { get; init; }
}

/// <summary>
/// Mirrors <see cref="CardPlayStartedEntry"/>.
/// </summary>
internal sealed class CombatPredictionCardPlayStartedEntry : CombatPredictionHistoryEntry
{
    public required PredictedCard Card { get; init; }
    public required CardPlay CardPlay { get; init; }
}

/// <summary>
/// Mirrors <see cref="CardPlayFinishedEntry"/>.
/// </summary>
internal sealed class CombatPredictionCardPlayFinishedEntry : CombatPredictionHistoryEntry
{
    public required PredictedCard Card { get; init; }
    public required CardPlay CardPlay { get; init; }
    public required bool WasEthereal { get; init; }
}

internal sealed class CombatPredictionDamageReceivedEntry : CombatPredictionHistoryEntry
{
    public required Creature Receiver { get; init; }
    public required DamageResult Result { get; init; }
    public required Creature? Dealer { get; init; }
    public required PredictedCard? CardSource { get; init; }
    public required CombatDamageSource Source { get; init; }
}

internal sealed class CombatPredictionCreatureAttackedEntry : CombatPredictionHistoryEntry
{
    public required Creature Attacker { get; init; }
    public required IReadOnlyList<DamageResult> HitResults { get; init; }
}

internal sealed class CombatPredictionOrbChanneledEntry : CombatPredictionHistoryEntry
{
    public required OrbModel Orb { get; init; }
}

internal sealed class CombatPredictionCardDrawnEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardSnapshot Card { get; init; }
    public required bool FromHandDraw { get; init; }
}

internal sealed class CombatPredictionCardDrawResolvedEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardDrawnEntry OriginalEntry { get; init; }
    public required CombatPredictionCardSnapshot Card { get; init; }
}

internal sealed class CombatPredictionCardCostsRandomizedEntry : CombatPredictionHistoryEntry
{
    public required IReadOnlyList<CombatPredictionCardSnapshot> Cards { get; init; }
}

internal sealed class CombatPredictionCardsSelectedEntry : CombatPredictionHistoryEntry
{
    public required IReadOnlyList<CombatPredictionCardSnapshot> Cards { get; init; }
}

internal sealed class CombatPredictionCardGeneratedEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardSnapshot Card { get; init; }
    public required Player? Creator { get; init; }
    public required CardGenerationResultKind ResultKind { get; init; }
}

internal sealed class CombatPredictionCardGenerationResolvedEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardGeneratedEntry OriginalEntry { get; init; }
    public required CombatPredictionCardSnapshot Card { get; init; }
}

internal sealed class CombatPredictionCardGenerationOptionsEntry : CombatPredictionHistoryEntry
{
    public required IReadOnlyList<CombatPredictionCardSnapshot> Cards { get; init; }
    public required IReadOnlyList<PredictedCard> Options { get; init; }
}

internal sealed class CombatPredictionCardAfflictedEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardSnapshot Card { get; init; }
    public required AfflictionModel Affliction { get; init; }
}

internal sealed class CombatPredictionAutoPlayFromDrawPileEntry : CombatPredictionHistoryEntry
{
    public required CombatPredictionCardSnapshot Card { get; init; }
}

internal sealed class CombatPredictionPotionGeneratedEntry : CombatPredictionHistoryEntry
{
    public required PotionModel Potion { get; init; }
}
