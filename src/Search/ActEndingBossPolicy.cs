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

/// <summary>
/// The post-combat healing the player's relics will apply the moment this fight is won, expressed in the
/// shape the three vanilla relics share.
/// </summary>
/// <remarks>
/// Burning Blood and Black Blood heal a fixed amount on every victory. Meat on the Bone heals only when the
/// player ends the fight at or below a percentage of max HP, and it settles first, so its threshold is
/// tested against the HP the route actually ends on rather than post-heal HP.
///
/// The amounts are read from the live relic models at root capture, so a mod that rebalances the heal value
/// at the data layer is followed automatically.
/// </remarks>
internal readonly record struct PostCombatRelicHealProfile(
    int UnconditionalHeal,
    int WoundedHeal,
    int WoundedHpPercent)
{
    public static PostCombatRelicHealProfile None => default;

    public bool HasAnyHeal => UnconditionalHeal > 0 || WoundedHeal > 0;

    /// <summary>
    /// HP these relics will actually restore after a won fight that ends on the given HP.
    /// </summary>
    /// <remarks>
    /// This is what the player will see happen, so it is what the route summary reports. It is deliberately
    /// not what route ranking uses: see <see cref="MonotoneHealFor"/>.
    /// </remarks>
    public int HealFor(int finalHp, int finalMaxHp)
    {
        int capacity = Math.Max(0, UnconditionalHeal);
        if (WoundedHeal > 0 && finalHp <= WoundedThreshold(finalMaxHp))
            capacity += WoundedHeal;
        return ClampToHeadroom(capacity, finalHp, finalMaxHp);
    }

    /// <summary>
    /// The part of <see cref="HealFor"/> that route ranking is allowed to count.
    /// </summary>
    /// <remarks>
    /// <c>min(heal, maxHp - finalHp)</c> is non-decreasing in final HP, so counting the unconditional relics
    /// keeps "ending on more HP is never worse" true and leaves every dominance test and search bound in the
    /// solver valid exactly as written.
    ///
    /// Meat on the Bone breaks that: ending one HP above its threshold is worse than ending on it, so a
    /// healthier node no longer dominates a wounded one. Several Pareto and transposition tests read raw HP
    /// and raw damage taken, and relaxing them wrongly would silently drop better routes with nothing in the
    /// diagnostics to show for it. Until those tests are reworked to compare post-combat HP, the threshold
    /// heal is reported to the player but kept out of ranking, which can only make the solver value HP
    /// slightly too highly near the threshold - never the reverse.
    /// </remarks>
    public int MonotoneHealFor(int finalHp, int finalMaxHp)
        => ClampToHeadroom(Math.Max(0, UnconditionalHeal), finalHp, finalMaxHp);

    /// <summary>Vanilla truncates the percentage, so 50% of 75 max HP is 37, not 38.</summary>
    private int WoundedThreshold(int finalMaxHp)
        => Math.Max(0, finalMaxHp) * Math.Max(0, WoundedHpPercent) / 100;

    private static int ClampToHeadroom(int capacity, int finalHp, int finalMaxHp)
        => Math.Clamp(capacity, 0, Math.Max(0, finalMaxHp - finalHp));
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
        BossHpRelief bossHpRelief,
        int deathSaveHpRestored = 0)
        => cumulativeHpLost
            + maxHpDeficit
            - PersistentValueOfRecoveredHp(recoveredHp - deathSaveHpRestored, bossHpRelief)
            + DeathSavePremium(deathSaveHpRestored);

    /// <summary>
    /// Extra recovered HP a finished route earns from post-combat relics, in the same units as the in-combat
    /// healing it is added to.
    /// </summary>
    /// <remarks>
    /// These relics only fire on a won fight with the player alive, so a route that fails to clear the room
    /// earns nothing. Only the monotone part is counted; see <see cref="PostCombatRelicHealProfile"/>.
    /// </remarks>
    public static int RankedPostCombatRelicHeal(
        PostCombatRelicHealProfile profile,
        bool completeVictory,
        int finalHp,
        int finalMaxHp)
        => completeVictory && finalHp > 0
            ? profile.MonotoneHealFor(finalHp, finalMaxHp)
            : 0;

    /// <summary>
    /// What burning a one-shot death save costs a route, in the same units as its battle HP loss.
    /// </summary>
    /// <remarks>
    /// Lizard Tail and Fairy in a Bottle restore HP only by consuming an irreplaceable survival resource.
    /// Subtracting that restoration from earned healing prevents a route from ranking intentional death as
    /// recovery; the premium keeps a preserving victory ahead of a revival victory in every fight.
    ///
    /// The premium is a multiple of the restored HP rather than a match for it, because HP is not the only
    /// thing the revive buys. Enemy HP is worth <see cref="SolverWeights.EnemyHp"/> a point inside Beam
    /// ranking, so a two-boss fight puts a hundred-odd HP-equivalents of tempo on the table, and a
    /// one-to-one premium loses to it — measured, not assumed. At the current multiple every route that
    /// wins while alive beats every route that wins on a death save, while the charge stays orders of
    /// magnitude below <see cref="SolverWeights.VictoryBonus"/> and <see cref="SolverWeights.DeathPenalty"/>,
    /// so a route with no surviving alternative still spends it without hesitation.
    /// </remarks>
    public static int DeathSavePremium(int deathSaveHpRestored)
        => Math.Max(0, deathSaveHpRestored)
            * SolverWeights.DeathSavePremiumPercent
            / 100;

    /// <summary>
    /// What a route's spent death saves cost inside Beam ranking, where the revive's own HP is still sitting
    /// in the node's projected HP: it is taken back out, and the premium is charged on top.
    /// </summary>
    public static int DeathSaveBeamCost(int deathSaveHpRestored)
    {
        int restored = Math.Max(0, deathSaveHpRestored);
        return restored + DeathSavePremium(restored);
    }

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
