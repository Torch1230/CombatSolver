namespace CombatSolver;

internal static class PoisonStackProjection
{
    internal static int ExtraTriggerDamage(
        int poison,
        int extraTriggers,
        int targetHp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poison);
        ArgumentOutOfRangeException.ThrowIfNegative(extraTriggers);
        ArgumentOutOfRangeException.ThrowIfNegative(targetHp);
        return (int)Math.Min(targetHp, (long)poison * extraTriggers);
    }

    internal static int RecurringApplicationDamage(
        int poisonPerTurn,
        int futureTurns,
        int targetHp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(poisonPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(futureTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(targetHp);
        int incrementalPoison = 0;
        long damage = 0;
        for (int turn = 0; turn < futureTurns; turn++)
        {
            incrementalPoison = (int)Math.Min(
                int.MaxValue,
                (long)Math.Max(0, incrementalPoison - 1) + poisonPerTurn);
            damage += incrementalPoison;
        }
        return (int)Math.Min(targetHp, damage);
    }
}
