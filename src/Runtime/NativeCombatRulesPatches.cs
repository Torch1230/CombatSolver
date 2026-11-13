using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Helpers;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class NativeDreadRulesPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_captured_dread_rules";
    public static string Description => "同步原生怪物构建读取根模式";
    public static ModPatchTarget[] GetTargets() => [new(typeof(AscensionHelper), nameof(AscensionHelper.IsDread), [])];
    public static bool Prefix(ref bool __result)
    {
        if (NativeCombatRulesScope.Current is not { } rules) return true;
        __result = rules.IsDread;
        return false;
    }
}

internal sealed class NativeAscensionRulesPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_captured_ascension_rules";
    public static string Description => "同步原生怪物构建读取根进阶集合";
    public static ModPatchTarget[] GetTargets() => [new(typeof(AscensionHelper), nameof(AscensionHelper.HasAscension), [typeof(AscensionLevel)])];
    public static bool Prefix(AscensionLevel __0, ref bool __result)
    {
        if (NativeCombatRulesScope.Current is not { } rules) return true;
        __result = (rules.Ascensions & (1UL << (int)__0)) != 0;
        return false;
    }
}
