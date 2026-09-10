using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

/// <summary>
/// Completed, synchronous evaluation of a closed effect program. All omitted state must be
/// invariant in Root: creatures/roster, Powers, card metadata, the other eight RNGs, relics, potions, and lifecycle
/// state other than CardHistory. Piles borrow immutable root cards, never mutable previews.
/// A reader is owned by one lane and cannot be retained in a candidate. This is not an execution API.
/// </summary>
internal abstract class CompletedStateReadView
{
    internal abstract CombatPredictionSimulator Root { get; }
    internal abstract int Energy { get; }
    internal abstract int Block { get; }
    internal abstract int HistoryEntries { get; }
    internal abstract IReadOnlyList<PredictedCard> Hand { get; }
    internal abstract IReadOnlyList<PredictedCard> Draw { get; }
    internal abstract IReadOnlyList<PredictedCard> Discard { get; }
    internal abstract IReadOnlyList<PredictedCard> Exhaust { get; }
    internal abstract IReadOnlyList<PredictionGap> PredictionGaps { get; }
    internal abstract CardHistoryReadValues CardHistory { get; }
    internal abstract PredictionRngState ShuffleRng { get; }
    // Opt in only when enemy state/AI, Powers and the live-card multiset plus metadata cannot
    // change in this root's execution domain. A new stable root requires a new cache.
    internal CombatBeamSolver.ReadViewInvariantCache? Invariants { get; init; }
}

// Null means preserve the original map entry (including absence); zero is an explicit entry.
internal readonly record struct CardHistoryReadValues(Player Owner, int? BlockPlays, int? SkillPlays,
    int? Discards, int? EnergySpent, int? Draws, int? Series, int? Starts, int? Plays, int? ManualPlays);
