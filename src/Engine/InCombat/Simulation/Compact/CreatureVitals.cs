namespace CombatSolver.Engine.InCombat.Simulation.Compact;

// Scalar arithmetic shared with SimCreatureState. Command timing, modifiers, history and death
// hooks belong to their execution programs; zero HP alone does not remove a creature.
internal record struct CreatureVitals(int CurrentHp, int MaxHp, int Block)
{
    internal decimal DamageBlock(decimal amount, bool unblockable)
    {
        decimal blocked = unblockable ? 0m : Math.Min(Block, amount);
        Block -= (int)blocked;
        return blocked;
    }

    internal HpLossValues LoseHp(decimal amount)
    {
        bool killed = CurrentHp > 0 && amount >= CurrentHp;
        int before = CurrentHp;
        int damage = (int)Math.Min(amount, 999999999m);
        CurrentHp = Math.Max(CurrentHp - damage, 0);
        return new(before - CurrentHp, killed, killed ? Math.Max(damage - before, 0) : 0);
    }

    internal void GainBlock(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("amount must be positive. Use LoseBlock for block loss.", nameof(amount));
        Block = (int)Math.Min(Block + amount, 999999999m);
    }

    internal void Heal(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("amount must be positive.", nameof(amount));
        CurrentHp = (int)Math.Min(CurrentHp + amount, MaxHp);
    }

    internal void SetMaxHp(int amount)
    {
        MaxHp = Math.Clamp(amount, 1, 999_999_999);
        CurrentHp = Math.Min(CurrentHp, MaxHp);
    }
}

internal readonly record struct HpLossValues(int UnblockedDamage, bool WasTargetKilled, int OverkillDamage);
