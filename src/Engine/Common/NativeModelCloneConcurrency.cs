using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Cards.DynamicVars;

namespace CombatSolver.Engine.Common;

// Evidence belongs to one isolation scope on one thread. Patches installed between
// searches must be inspected again; no model or branch values enter this cache.
internal static class NativeModelCloneConcurrency
{
    [ThreadStatic] private static object? _scope;
    [ThreadStatic] private static Dictionary<Type, bool>? _types;
    [ThreadStatic] private static bool _metadataSafe;

    public static bool CanCloneIndependently(AbstractModel source)
    {
        object? scope = SimulationNotificationIsolation.ScopeIdentity;
        if (scope == null || source.GetType().Assembly != typeof(AbstractModel).Assembly)
            return false;
        // Read the backing field: selecting a clone path must never materialize shared
        // source variables. Attached card models and custom Power initialization retain
        // their original boundary until their extra callbacks are independently audited.
        DynamicVarSet? variables = source switch
        {
            CardModel card when card.Enchantment == null && card.Affliction == null => card._dynamicVars,
            PowerModel power => power._dynamicVars,
            _ => null,
        };
        if (variables == null)
            return false;
        foreach (var variable in variables._vars.Values)
        {
            if (variable.GetType().Assembly != typeof(AbstractModel).Assembly)
                return false;
        }
        if (!ReferenceEquals(_scope, scope))
        {
            _scope = scope;
            _types = [];
            _metadataSafe = HasConcurrentVariableMetadata();
        }
        if (!_metadataSafe)
            return false;
        Type type = source.GetType();
        if (!_types!.TryGetValue(type, out bool eligible))
        {
            Type owner = source is CardModel ? typeof(CardModel) : typeof(PowerModel);
            eligible = UsesUnpatchedStage(type, "DeepCloneFields", owner)
                && UsesUnpatchedStage(type, "AfterCloned", owner)
                && UsesUnpatchedStage(typeof(AbstractModel), "AfterCloned", typeof(AbstractModel))
                && (source is CardModel || HasDefaultPowerInitialization(type));
            _types.Add(type, eligible);
        }
        return eligible;
    }

    private static bool HasDefaultPowerInitialization(Type type)
        => UsesUnpatchedStage(typeof(AbstractModel), "DeepCloneFields", typeof(AbstractModel))
            && UsesUnpatchedStage(type, "InitInternalData", typeof(PowerModel))
            && HasExactPatches(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.DynamicVars)),
                [AccessTools.Method(typeof(PowerDynamicVarMaterializationGuardPatch), "Prefix")], []);

    private static bool HasConcurrentVariableMetadata()
    {
        Type? baseLibCopy = AccessTools.TypeByName("BaseLib.Extensions.DynamicVarExtensions+CloneTooltips");
        Type? ritsuCopy = AccessTools.TypeByName("STS2RitsuLib.Cards.Patches.DynamicVarTooltipClonePatch");
        if (baseLibCopy == null || ritsuCopy == null)
            return false;
        MethodInfo copy = AccessTools.Method(baseLibCopy, "Copy");
        MethodInfo ritsuPostfix = AccessTools.Method(ritsuCopy, "Postfix");
        return HasExactPatches(AccessTools.Method(typeof(DynamicVarSet), "Clone"), [], [])
            && HasExactPatches(AccessTools.Method(typeof(DynamicVar), "Clone"), [], [copy, ritsuPostfix])
            && HasExactPatches(copy,
                [AccessTools.Method(typeof(BaseLibDynamicVarCloneMetadataPatch), "Prefix")], [])
            && HasExactPatches(AccessTools.Method(typeof(DynamicVarTooltipRegistry), "CopyTo"),
                [AccessTools.Method(typeof(RitsuDynamicVarCloneMetadataPatch), "Prefix")], []);
    }

    private static bool HasExactPatches(MethodInfo? method, MethodInfo[] prefixes, MethodInfo[] postfixes)
    {
        if (method == null || prefixes.Any(patch => patch == null) || postfixes.Any(patch => patch == null))
            return false;
        Patches? patches = Harmony.GetPatchInfo(method);
        if (patches == null)
            return prefixes.Length == 0 && postfixes.Length == 0;
        return patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0
            && patches.Prefixes.Count == prefixes.Length && patches.Postfixes.Count == postfixes.Length
            && patches.Prefixes.All(patch => prefixes.Contains(patch.PatchMethod))
            && patches.Postfixes.All(patch => postfixes.Contains(patch.PatchMethod));
    }

    private static bool UsesUnpatchedStage(Type type, string name, Type owner)
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method?.DeclaringType != owner)
            return false;
        // Harmony indexes the declaring method, not an inherited MethodInfo whose
        // ReflectedType is the concrete card type.
        method = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Patches? patches = Harmony.GetPatchInfo(method);
        return patches == null || (patches.Prefixes.Count == 0 && patches.Postfixes.Count == 0
            && patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0);
    }
}
