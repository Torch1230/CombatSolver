using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class UnattendedCombatStartReplay : IDisposable
{
    private static UnattendedCombatStartReplay? _pending;
    private readonly RunState _runState;
    private readonly Func<CombatState, Task> _restore;

    public Task? Restoration { get; private set; }

    public UnattendedCombatStartReplay(RunState runState, Func<CombatState, Task> restore)
    {
        if (_pending != null)
            throw new InvalidOperationException("A combat-start replay is already pending.");
        _runState = runState;
        _restore = restore;
        _pending = this;
    }

    internal static void WrapAfterCreatureAdded(Creature creature, CombatState state, ref Task task)
    {
        UnattendedCombatStartReplay? pending = _pending;
        if (pending == null
            || !UnattendedTestRunner.IsActive
            || !ReferenceEquals(state.RunState, pending._runState)
            || !CombatManager.Instance.IsStarting
            || !ReferenceEquals(creature, state.Creatures[^1]))
        {
            return;
        }

        // The native setup loop awaits the final creature before BeforeCombatStart and the first draw.
        _pending = null;
        pending.Restoration = pending.RestoreAsync(task, state);
        task = pending.Restoration;
    }

    private async Task RestoreAsync(Task creatureAdded, CombatState state)
    {
        await creatureAdded;
        await _restore(state);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_pending, this))
            _pending = null;
    }
}

internal sealed class UnattendedCombatStartReplayPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_combat_start_replay";
    public static string Description => "Restore a test replay before native combat-start effects";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CombatManager), "AfterCreatureAdded", [typeof(Creature), typeof(CombatState)]),
    ];

    public static void Postfix(Creature __0, CombatState __1, ref Task __result)
        => UnattendedCombatStartReplay.WrapAfterCreatureAdded(__0, __1, ref __result);
}
