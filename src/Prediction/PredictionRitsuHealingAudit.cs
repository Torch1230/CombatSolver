using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using STS2RitsuLib.Combat.Healing;

namespace CombatSolver;

// The pinned native Heal prefix adds a listener stream outside ModHelper's
// subscriber lists. Its contents are not mirrored, so an empty subscriber list
// alone cannot certify native healing. Version mismatches revoke finite healing
// certificates instead of blocking runs; only concrete unmirrored patches or
// active global listeners throw PredictionUnsupportedException.
internal static class PredictionRitsuHealingAudit
{
    private static readonly Guid AuditedRuntimeMvid0614 = new("387ddce6-a82c-4ac1-9665-bc4799859e10");
    private static readonly Guid AuditedRuntimeMvid0606 = new("f7f18a36-8ebb-4db2-b1ea-7bd340579646");
    private static readonly Lazy<(FieldInfo Registry, MethodInfo Snapshot, MethodInfo Prefix)?> Metadata
        = new(TryCaptureMetadata);

    internal static bool IsAuditedRuntimeMvid(Guid mvid)
        => mvid == AuditedRuntimeMvid0614 || mvid == AuditedRuntimeMvid0606;

    internal static string? CaptureHealingRejection(Guid? runtimeMvidOverrideForTest = null)
    {
        MethodInfo? heal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
            [typeof(Creature), typeof(decimal), typeof(bool)]);
        if (heal is null)
            return "ritsu-heal-layout";
        Patches? patches = Harmony.GetPatchInfo(heal);
        if (patches is null || patches.Prefixes.Count + patches.Postfixes.Count
            + patches.Transpilers.Count + patches.Finalizers.Count == 0)
            return null;
        if (!IsAuditedRuntimeMvid(runtimeMvidOverrideForTest ?? typeof(HealHook).Module.ModuleVersionId))
            return "ritsu-heal-version";
        if (Metadata.Value is not { } metadata)
            return "ritsu-heal-layout";
        if (patches.Prefixes.Count != 1
            || patches.Prefixes[0].PatchMethod != metadata.Prefix
            || patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0)
            return "ritsu-heal-patch";
        if (metadata.Registry.GetValue(null) is not { } registry
            || metadata.Snapshot.Invoke(registry, null) is not Array snapshot)
            return "ritsu-heal-layout";
        return snapshot.Length == 0 ? null : "ritsu-heal-listener";
    }

    internal static void Validate()
    {
        MethodInfo? heal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
            [typeof(Creature), typeof(decimal), typeof(bool)]);
        if (heal is null)
            return;
        Patches? patches = Harmony.GetPatchInfo(heal);
        // An unpatched native command does not dispatch the Ritsu listener stream.
        if (patches is null || patches.Prefixes.Count + patches.Postfixes.Count
            + patches.Transpilers.Count + patches.Finalizers.Count == 0)
            return;
        var metadata = Metadata.Value;
        Assembly ritsuAssembly = typeof(HealHook).Assembly;
        if (metadata is { } audited)
        {
            if (patches.Prefixes.Count != 1
                || patches.Prefixes[0].PatchMethod != audited.Prefix
                || patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0)
                throw new PredictionUnsupportedException("Unreviewed native Heal patch combination.");
            if (audited.Registry.GetValue(null) is { } registry
                && audited.Snapshot.Invoke(registry, null) is Array snapshot
                && snapshot.Length != 0)
                throw new PredictionUnsupportedException(
                    "Unsupported RitsuLib global healing listener; its healing effects are not mirrored.");
        }
        else if (patches.Prefixes.Count != 1
            || patches.Prefixes[0].PatchMethod.DeclaringType?.Assembly != ritsuAssembly
            || patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0)
        {
            throw new PredictionUnsupportedException("Unreviewed native Heal patch combination.");
        }
    }

    private static (FieldInfo, MethodInfo, MethodInfo)? TryCaptureMetadata()
    {
        Type hook = typeof(HealHook);
        FieldInfo? registry = hook.GetField("GlobalListeners", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? snapshot = registry?.FieldType.GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance);
        Type? prefixType = hook.Assembly.GetType("STS2RitsuLib.Combat.Healing.Patches.CreatureCmdHealHookPatch");
        MethodInfo? prefix = prefixType?.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
        return registry is null || snapshot is null || prefix is null
            ? null
            : (registry, snapshot, prefix);
    }
}
