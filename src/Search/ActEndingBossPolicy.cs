using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// How much the HP this fight costs actually matters to the run.
/// </summary>
internal enum BossHpRelief
{
    /// <summary>Normal fight: HP carries straight into the next one and is weighted in full.</summary>
    None,

    /// <summary>Clearing acts one and two restores 80% of the damage taken.</summary>
    ActClearHeal,

    /// <summary>Nothing follows this fight, so only surviving it matters.</summary>
    RunEnding,
}

internal static class ActEndingBossPolicy
{
    public static BossHpRelief ResolveStrategicHpRelief(
        BossHpRelief encounterHpRelief,
        BossHpStrategy actTransitionStrategy,
        BossHpStrategy finalBossStrategy)
        => encounterHpRelief switch
        {
            BossHpRelief.ActClearHeal when actTransitionStrategy == BossHpStrategy.MinimizeHpLoss
                => BossHpRelief.None,
            BossHpRelief.RunEnding when finalBossStrategy == BossHpStrategy.MinimizeHpLoss
                => BossHpRelief.None,
            _ => encounterHpRelief,
        };

    public static int RawHpRequiredForPersistentValue(
        int persistentHpValue,
        BossHpRelief bossHpRelief)
    {
        if (persistentHpValue <= 0)
            return 0;
        return bossHpRelief switch
        {
            BossHpRelief.ActClearHeal => persistentHpValue * 5,
            BossHpRelief.RunEnding => int.MaxValue / 4,
            _ => persistentHpValue,
        };
    }

    /// <summary>
    /// What HP a route restored during this fight is worth once the fight is over, in the same units as the
    /// strategic HP deficit it offsets.
    /// </summary>
    /// <remarks>
    /// This is the exact inverse of <see cref="RawHpRequiredForPersistentValue"/>, and is derived from it so the
    /// two cannot drift apart. Clearing an act gives 80% of the damage back for free, so healing during that
    /// boss only keeps the remaining fifth; nothing follows the run's last fight, so healing there buys nothing.
    /// </remarks>
    public static int PersistentValueOfRecoveredHp(int recoveredHp, BossHpRelief bossHpRelief)
        => recoveredHp <= 0
            ? 0
            : recoveredHp / RawHpRequiredForPersistentValue(1, bossHpRelief);

    /// <summary>
    /// Battle HP loss net of what the route healed back, which is the quantity route quality is ranked on.
    /// </summary>
    /// <remarks>
    /// Gross damage alone cannot see a heal: <c>CumulativePlayerHpLost</c> only ever accumulates unblocked
    /// damage. Without this correction a route that ends the fight ten HP higher ranks behind one that ends a
    /// turn sooner, even though those ten HP carry into every fight that follows.
    ///
    /// The result is bounded below by the HP the player was already missing when the fight started, because
    /// current HP is capped by max HP: a route can at most heal back to full, so no amount of extra turns can
    /// farm this axis indefinitely.
    /// </remarks>
    public static int StrategicHpDeficit(
        int cumulativeHpLost,
        int maxHpDeficit,
        int recoveredHp,
        BossHpRelief bossHpRelief)
        => cumulativeHpLost
            + maxHpDeficit
            - PersistentValueOfRecoveredHp(recoveredHp, bossHpRelief);

    public static BossHpRelief ResolveHpRelief(CombatState combatState)
    {
        if (combatState.Encounter?.RoomType != RoomType.Boss)
            return BossHpRelief.None;

        RunState runState = combatState.RunState as RunState
            ?? throw new InvalidOperationException("Boss 战没有可识别的 RunState。");
        if (runState.CurrentActIndex < runState.Acts.Count - 1)
            return BossHpRelief.ActClearHeal;

        // Final act. A single boss is the run's last fight; when the act has two, only the second one is,
        // and HP carries from the first into it exactly like a normal fight.
        return runState.Act.SecondBossEncounter is not { } second
            || second.Id == combatState.Encounter.Id
                ? BossHpRelief.RunEnding
                : BossHpRelief.None;
    }

    public static bool IsRecoveryFight(CombatState combatState)
        => ResolveHpRelief(combatState) != BossHpRelief.None;
}
