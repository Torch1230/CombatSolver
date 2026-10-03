using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class UnattendedHeadlessFtuePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_headless_ftue";
    public static string Description => "无人测试不创建无窗口战斗基础教学界面";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(NCombatRulesFtue),
            nameof(NCombatRulesFtue.Create),
            Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(ref NCombatRulesFtue? __result)
    {
        if (!UnattendedTestRunner.IsActive)
            return true;
        __result = null;
        Entry.Logger.Info("[CombatSolver/Unattended] FTUE_SKIPPED id=combat_rules_ftue reason=headless_ui");
        return false;
    }
}
