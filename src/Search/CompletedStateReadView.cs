using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using System.Runtime.CompilerServices;

namespace CombatSolver;

/// <summary>
/// Completed, synchronous evaluation of a closed effect program. All omitted state must be
/// invariant in Root: known creature identities, Powers, card metadata, the other eight RNGs, relics, potions, and lifecycle
/// state other than CardHistory. Piles borrow immutable root cards, never mutable previews.
/// A reader is owned by one lane and cannot be retained in a candidate. This is not an execution API.
/// </summary>
internal abstract class CompletedStateReadView
{
    internal abstract CombatPredictionSimulator Root { get; }
    internal abstract int Energy { get; }
    internal abstract int Block { get; }
    internal abstract CreatureReadValues ReadCreature(Creature creature);
    internal abstract IReadOnlyList<Creature> EnemyRoster { get; }
    internal abstract CombatTerminalStamp? TerminalStamp { get; }
    // Enemy summaries may only be cached when every enemy value and roster membership is invariant.
    internal abstract bool EnemyValuesInvariant { get; }
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

internal readonly record struct CreatureReadValues(int CurrentHp, int MaxHp, int Block, bool Present)
{
    internal bool IsAlive => CurrentHp > 0;
    internal bool IsDead => !IsAlive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static CreatureReadValues Capture(CombatPredictionSimulator simulator, Creature creature)
    {
        SimCreatureState state = simulator.State.GetCreature(creature);
        return new(state.CurrentHp, state.MaxHp, state.Block,
            ((SimulatedCombatState)simulator.State.CombatState).ContainsCreature(creature));
    }
}

// Null means preserve the original map entry (including absence); zero is an explicit entry.
internal readonly record struct CardHistoryReadValues(Player Owner, int? BlockPlays, int? SkillPlays,
    int? Discards, int? EnergySpent, int? Draws, int? Series, int? Starts, int? Plays, int? ManualPlays);
