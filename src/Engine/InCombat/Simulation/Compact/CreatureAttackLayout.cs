namespace CombatSolver.Engine.InCombat.Simulation.Compact;

// Immutable locations only. Vitals, presence, death completion and terminal state all belong
// to the caller's journal. This initial command domain has primary enemies without death hooks.
internal sealed class CreatureAttackLayout
{
    private readonly CreatureValueSlots[] _creatures;
    private readonly int _deathStart, _terminalSlot;
    internal int Count => _creatures.Length;

    internal CreatureAttackLayout(ReversibleValueState state, IReadOnlyList<CreatureVitals> creatures)
    {
        if (creatures.Count is < 2 or > 254 || creatures.Any(c => c.CurrentHp <= 0 || c.MaxHp < c.CurrentHp || c.Block < 0))
            throw new ArgumentException("Attack roots require a living player and living primary enemies.");
        _creatures = creatures.Select(c => CreatureValueSlots.Allocate(state, c, true)).ToArray();
        _deathStart = state.Allocate(creatures.Count);
        _terminalSlot = state.Allocate(1);
    }

    internal CreatureVitals Read(ReversibleValueState state, int creature) => _creatures[creature].Read(state);
    internal void Write(ReversibleValueState state, int creature, CreatureVitals values) => _creatures[creature].Write(state, values);
    internal bool Present(ReversibleValueState state, int creature) => _creatures[creature].IsPresent(state);
    internal bool DeathCompleted(ReversibleValueState state, int creature) => state[_deathStart + creature] != 0;
    internal bool Terminal(ReversibleValueState state) => state[_terminalSlot] != 0;

    internal DamageValues Damage(ReversibleValueState state, int target, decimal amount, bool unblockable = false)
    {
        CreatureVitals values = Read(state, target);
        decimal blocked = values.DamageBlock(amount, unblockable);
        decimal unblocked = Math.Max(amount - blocked, 0m);
        HpLossValues loss = values.LoseHp(unblocked);
        Write(state, target, values);
        return new((int)blocked, loss.UnblockedDamage, loss.OverkillDamage, loss.WasTargetKilled,
            values.Block <= 0 && blocked > 0m, !unblockable && (blocked > 0m || values.Block > 0) && (int)unblocked == 0);
    }

    internal void CompleteDeath(ReversibleValueState state, int target)
    {
        if (Read(state, target).CurrentHp != 0 || DeathCompleted(state, target))
            throw new InvalidOperationException("Death completion requires an unprocessed dead creature.");
        _creatures[target].SetPresent(state, false);
        state.Write(_deathStart + target, 1);
    }

    internal bool IsEnding(ReversibleValueState state)
    {
        for (int index = 1; index < Count; index++)
            if (Present(state, index)) return false;
        return true;
    }

    internal bool CheckWinCondition(ReversibleValueState state)
    {
        if (Terminal(state)) return true;
        if (!IsEnding(state)) return false;
        state.Write(_terminalSlot, 1);
        return true;
    }
}

internal readonly record struct DamageValues(int Blocked, int Unblocked, int Overkill,
    bool Killed, bool BlockBroken, bool FullyBlocked)
{
    internal int Flags => (Killed ? 1 : 0) | (BlockBroken ? 2 : 0) | (FullyBlocked ? 4 : 0);
}
