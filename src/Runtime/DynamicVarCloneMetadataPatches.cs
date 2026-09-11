using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using STS2RitsuLib.Cards.DynamicVars;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.Utils;

namespace CombatSolver;

// These specific fields have null defaults. Sparse copies preserve their values without
// attaching empty entries to every transient simulation variable. Other SpireFields keep
// their own factories and clone behavior, and live calls keep the framework implementation.
internal sealed class BaseLibDynamicVarCloneMetadataPatch : IPatchMethod
{
    private static ConditionalWeakTable<DynamicVar, object?>? _tips;
    private static ConditionalWeakTable<DynamicVar, object?>? _upgrades;
    public static string PatchId => "combat_solver_baselib_sparse_dynamic_var_clone";
    public static string Description => "模拟克隆只登记已有的动态变量附加值";

    public static ModPatchTarget[] GetTargets()
    {
        Type? extensions = AccessTools.TypeByName("BaseLib.Extensions.DynamicVarExtensions");
        if (extensions == null) return [];
        _tips = ReadTable(extensions, "DynamicVarTips");
        _upgrades = ReadTable(extensions, "DynamicVarUpgrades");
        Type copy = extensions.GetNestedType("CloneTooltips", BindingFlags.NonPublic)
            ?? throw new TypeLoadException("BaseLib DynamicVarExtensions.CloneTooltips");
        return [new(copy, "Copy", [typeof(DynamicVar), typeof(DynamicVar)])];
    }

    private static ConditionalWeakTable<DynamicVar, object?> ReadTable(Type extensions, string name)
    {
        object field = extensions.GetField(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        return (ConditionalWeakTable<DynamicVar, object?>)field.GetType()
            .GetField("_table", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;
    }

    public static bool Prefix(DynamicVar __0, DynamicVar __1, ref DynamicVar __result)
    {
        if (!SimulationNotificationIsolation.IsActive) return true;
        Copy(_tips!, __1, __0);
        Copy(_upgrades!, __1, __0);
        __result = __0;
        return false;
    }

    private static void Copy(ConditionalWeakTable<DynamicVar, object?> table, DynamicVar source, DynamicVar destination)
    {
        table.TryGetValue(source, out object? value);
        if (value == null) table.Remove(destination);
        else table.AddOrUpdate(destination, value);
    }
}

internal sealed class RitsuDynamicVarCloneMetadataPatch : IPatchMethod
{
    private static readonly AttachedState<DynamicVar, Func<DynamicVar, IHoverTip>?> Tips =
        (AttachedState<DynamicVar, Func<DynamicVar, IHoverTip>?>)typeof(DynamicVarTooltipRegistry)
            .GetField("TooltipFactories", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    public static string PatchId => "combat_solver_ritsu_sparse_dynamic_var_clone";
    public static string Description => "模拟克隆查询提示工厂时保持空值未登记";
    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(DynamicVarTooltipRegistry), nameof(DynamicVarTooltipRegistry.CopyTo), [typeof(DynamicVar), typeof(DynamicVar)])];

    public static bool Prefix(DynamicVar __0, DynamicVar __1)
    {
        if (!SimulationNotificationIsolation.IsActive) return true;
        ArgumentNullException.ThrowIfNull(__0);
        ArgumentNullException.ThrowIfNull(__1);
        if (Tips.TryGetValue(__0, out var factory) && factory != null)
            Tips[__1] = factory;
        return false;
    }
}
