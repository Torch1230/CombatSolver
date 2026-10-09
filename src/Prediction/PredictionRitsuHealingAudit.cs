using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using STS2RitsuLib.Combat.Healing;

namespace CombatSolver;

// The pinned native Heal prefix adds a listener stream outside ModHelper's
// subscriber lists. Its contents are not mirrored, so an empty subscriber list
// alone cannot certify native healing. Only metadata is cached; every stable
// root/live boundary re-reads the published listener snapshot and patch table.
internal static class PredictionRitsuHealingAudit
{
    private static readonly Guid AuditedRuntimeMvid = new("f7f18a36-8ebb-4db2-b1ea-7bd340579646");
    private static readonly Lazy<(FieldInfo Registry, MethodInfo Snapshot, MethodInfo Prefix)> Metadata
        = new(CaptureMetadata);

    internal static void Validate()
    {
        MethodInfo heal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
            [typeof(Creature), typeof(decimal), typeof(bool)]);
        Patches? patches = Harmony.GetPatchInfo(heal);
        // An unpatched native command does not dispatch the Ritsu listener stream.
        if (patches is null || patches.Prefixes.Count + patches.Postfixes.Count
            + patches.Transpilers.Count + patches.Finalizers.Count == 0)
            return;
        if (typeof(HealHook).Module.ModuleVersionId != AuditedRuntimeMvid)
            throw new PredictionUnsupportedException("Unaudited RitsuLib healing runtime; healing callbacks cannot be certified.");
        var metadata = Metadata.Value;
        if (patches.Prefixes.Count != 1
            || patches.Prefixes[0].PatchMethod != metadata.Prefix
            || patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0)
            throw new PredictionUnsupportedException("Unreviewed native Heal patch combination.");
        object registry = metadata.Registry.GetValue(null)
            ?? throw new PredictionUnsupportedException("Missing RitsuLib healing listener registry.");
        Array snapshot = metadata.Snapshot.Invoke(registry, null) as Array
            ?? throw new PredictionUnsupportedException("Unknown RitsuLib healing listener snapshot.");
        if (snapshot.Length != 0)
            throw new PredictionUnsupportedException("Unsupported RitsuLib global healing listener; its healing effects are not mirrored.");
    }

    private static (FieldInfo, MethodInfo, MethodInfo) CaptureMetadata()
    {
        Type hook = typeof(HealHook);
        FieldInfo registry = hook.GetField("GlobalListeners", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new PredictionUnsupportedException("RitsuLib healing registry contract changed.");
        MethodInfo snapshot = registry.FieldType.GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new PredictionUnsupportedException("RitsuLib healing snapshot contract changed.");
        Type prefixType = hook.Assembly.GetType("STS2RitsuLib.Combat.Healing.Patches.CreatureCmdHealHookPatch")
            ?? throw new PredictionUnsupportedException("RitsuLib healing prefix contract changed.");
        MethodInfo prefix = prefixType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)
            ?? throw new PredictionUnsupportedException("RitsuLib healing prefix contract changed.");
        return (registry, snapshot, prefix);
    }
}
