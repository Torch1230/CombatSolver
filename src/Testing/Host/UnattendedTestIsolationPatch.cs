using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class UnattendedTestIsolationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_test_isolation";
    public static string Description => "无人测试不写入玩家的遭遇与击杀进度";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(SaveManager),
            nameof(SaveManager.UpdateProgressAfterCombatWon),
            [typeof(Player), typeof(CombatRoom)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix()
    {
        if (!UnattendedTestRunner.IsActive)
            return true;
        Entry.Logger.Info("[CombatSolver/Unattended] PROGRESS_WRITE_SKIPPED scope=combat_win_stats");
        return false;
    }
}
