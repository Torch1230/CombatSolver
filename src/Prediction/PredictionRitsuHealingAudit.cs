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
// certificates. Active global listeners require their own combat semantics.
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
        if (patches is null)
            return null;
        if (patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0
            || patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0)
            return "ritsu-heal-patch";
        if (patches.Prefixes.Count == 0)
            return null;
        if (!IsAuditedRuntimeMvid(runtimeMvidOverrideForTest ?? typeof(HealHook).Module.ModuleVersionId))
            return "ritsu-heal-version";
        if (Metadata.Value is not { } metadata)
            return "ritsu-heal-layout";
        if (patches.Prefixes.Count != 1 || patches.Prefixes[0].PatchMethod != metadata.Prefix)
            return "ritsu-heal-patch";
        if (metadata.Registry.GetValue(null) is not { } registry
            || metadata.Snapshot.Invoke(registry, null) is not Array snapshot)
            return "ritsu-heal-layout";
        return snapshot.Length == 0 ? null : "ritsu-heal-listener";
    }

    internal static void ValidateGlobalListeners()
    {
        MethodInfo? heal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
            [typeof(Creature), typeof(decimal), typeof(bool)]);
        if (heal is null)
            return;
        if (Metadata.Value is { } audited
            && Harmony.GetPatchInfo(heal) is { } patches
            && patches.Prefixes.Any(patch => patch.PatchMethod == audited.Prefix)
            && audited.Registry.GetValue(null) is { } registry
            && audited.Snapshot.Invoke(registry, null) is Array { Length: > 0 })
            throw new PredictionUnsupportedException(
                "Unsupported RitsuLib global healing listener; its healing effects are not mirrored.");
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
