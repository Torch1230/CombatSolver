using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using System.Runtime.CompilerServices;

namespace CombatSolver;

/// <summary>
/// Completed, synchronous evaluation of a closed effect program. All omitted state must be
/// invariant in EvaluationContext: identities, card metadata, the other eight RNGs, relics,
/// potions and omitted lifecycle state. Supplied Power cells are copied one way into lane-owned
/// evaluation models before reading, so existing formulas consume the current values/order.
/// Piles borrow immutable root cards. The context also owns the old formulas' mutable scratch;
/// it is never executed, retained in a candidate, or read back into the authoritative program.
/// </summary>
internal abstract class CompletedStateReadView
{
    internal abstract CombatPredictionSimulator EvaluationContext { get; }
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
    internal virtual CombatHistoryReadValues? CombatHistory => null;
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
    int? Discards, int? EnergySpent, int? Draws, int? Series, int? Starts, int? Plays, int? ManualPlays,
    int? AttackPlays = null, int? CreatureAttacks = null, int? ZeroCostAttackStarts = null);

// Lane-owned derived read data. These collections preserve explicit entries (including zero)
// from the captured root; event consumers extend them without mutating root models or maps.
internal sealed class CombatHistoryReadValues
{
    internal HashSet<Creature> LostHp { get; } = [];
    internal Dictionary<(Creature Dealer, Creature Receiver), int> PoweredHits { get; } = [];
    internal Dictionary<Player, PredictedCard> LastAttacks { get; } = [];
    internal Dictionary<Creature, PredictedDeathPhase> DeathPhases { get; } = [];

    internal void ResetFrom(CombatHistoryReadValues source)
    {
        LostHp.Clear(); LostHp.UnionWith(source.LostHp);
        PoweredHits.Clear();
        foreach (var pair in source.PoweredHits) PoweredHits.Add(pair.Key, pair.Value);
        LastAttacks.Clear();
        foreach (var pair in source.LastAttacks) LastAttacks.Add(pair.Key, pair.Value);
        DeathPhases.Clear();
        foreach (var pair in source.DeathPhases) DeathPhases.Add(pair.Key, pair.Value);
    }
}
