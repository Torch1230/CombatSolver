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

    /// <summary>Clearing the act restores most of the damage taken, so HP spent here is largely refunded.</summary>
    ActClearHeal,

    /// <summary>Nothing follows this fight, so only surviving it matters.</summary>
    RunEnding,
}

internal static class ActEndingBossPolicy
{
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
