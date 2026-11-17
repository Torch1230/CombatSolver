using System.Collections;
using System.Reflection;
using CombatSolver.Engine.Common;
using STS2RitsuLib.Models.Capabilities;

namespace CombatSolver;

// Native models can carry external behavior without changing their CLR type or
// ModHelper subscriber lists. Check the published hosts, including canonical
// future sources, before cloning. Unaudited Ritsu versions only revoke finite
// healing certificates; concrete unmirrored capabilities or defaults still fail.
internal static class PredictionRitsuCapabilityAudit
{
    private static readonly Guid AuditedRuntimeMvid0614 = new("387ddce6-a82c-4ac1-9665-bc4799859e10");
    private static readonly Guid AuditedSharedMvid0614 = new("26f9c040-0f11-41dc-8031-633ea5598f41");
    private static readonly Guid AuditedRuntimeMvid0606 = new("f7f18a36-8ebb-4db2-b1ea-7bd340579646");
    private static readonly Guid AuditedSharedMvid0606 = new("67c05374-6f96-4bbc-831c-5313d1cc0fd1");

    private sealed record Metadata(FieldInfo Collections, FieldInfo Table,
        PropertyInfo EntryValue, FieldInfo BoxValue, FieldInfo Capabilities, FieldInfo UnknownEntries,
        FieldInfo Modifiers, FieldInfo ModifierLock);

    private static readonly Lazy<Metadata?> AuditedMetadata = new(TryCaptureMetadata);

    internal static bool IsAuditedRuntimeMvid(Guid mvid)
        => mvid == AuditedRuntimeMvid0614 || mvid == AuditedRuntimeMvid0606;

    internal static bool IsAuditedSharedMvid(Guid mvid)
        => mvid == AuditedSharedMvid0614 || mvid == AuditedSharedMvid0606;

    internal static string? CaptureHealingRejection(
        Guid? runtimeMvidOverrideForTest = null,
        Guid? sharedMvidOverrideForTest = null)
    {
        if (!IsAuditedRuntimeMvid(runtimeMvidOverrideForTest ?? typeof(ModelCapabilities).Module.ModuleVersionId))
            return "ritsu-capability-version";
        if (AuditedMetadata.Value is not { } metadata)
            return "ritsu-capability-layout";
        Guid sharedMvid = sharedMvidOverrideForTest ?? metadata.Collections.FieldType.Module.ModuleVersionId;
        if (!IsAuditedSharedMvid(sharedMvid))
            return "ritsu-capability-version";
        return HasActiveCapabilitySources(metadata) ? "ritsu-capability-source" : null;
    }

    internal static void Validate()
    {
        if (AuditedMetadata.Value is not { } metadata)
            return;
        if (metadata.ModifierLock.GetValue(null) is Lock modifierLock)
        {
            using (modifierLock.EnterScope())
            {
                if (metadata.Modifiers.GetValue(null) is ICollection modifiers && modifiers.Count != 0)
                    throw new PredictionUnsupportedException(
                        "Unsupported RitsuLib default capability modifier; future model behavior is not mirrored.");
            }
        }
        if (metadata.Collections.GetValue(null) is not { } collections
            || metadata.Table.GetValue(collections) is not IEnumerable table)
            return;
        // The audited ConditionalWeakTable enumerates existing values, including
        // prototypes that may only become reachable later. It creates no hosts.
        foreach (object entry in table)
        {
            if (metadata.EntryValue.GetValue(entry) is not { } box
                || metadata.BoxValue.GetValue(box) is not { } collection)
                continue;
            if (metadata.Capabilities.GetValue(collection) is ICollection capabilities && capabilities.Count != 0)
                throw new PredictionUnsupportedException(
                    "Unsupported RitsuLib attached model capability; its callbacks are not mirrored.");
            if (metadata.UnknownEntries.GetValue(collection) is ICollection unknown && unknown.Count != 0)
                throw new PredictionUnsupportedException(
                    "Unsupported RitsuLib saved model capability; an empty active list does not certify its future behavior.");
        }
    }

    private static bool HasActiveCapabilitySources(Metadata metadata)
    {
        if (metadata.ModifierLock.GetValue(null) is not Lock modifierLock)
            return true;
        using (modifierLock.EnterScope())
        {
            if (metadata.Modifiers.GetValue(null) is not ICollection modifiers || modifiers.Count != 0)
                return true;
        }
        if (metadata.Collections.GetValue(null) is not { } collections
            || metadata.Table.GetValue(collections) is not IEnumerable table)
            return true;
        foreach (object entry in table)
        {
            if (metadata.EntryValue.GetValue(entry) is not { } box
                || metadata.BoxValue.GetValue(box) is not { } collection)
                return true;
            if (metadata.Capabilities.GetValue(collection) is not ICollection capabilities || capabilities.Count != 0)
                return true;
            if (metadata.UnknownEntries.GetValue(collection) is not ICollection unknown || unknown.Count != 0)
                return true;
        }
        return false;
    }

    private static Metadata? TryCaptureMetadata()
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo? collections = typeof(ModelCapabilities).GetField("Collections", BindingFlags.NonPublic | BindingFlags.Static);
        Type? host = collections?.FieldType;
        FieldInfo? table = host?.GetField("_table", fields);
        if (collections is null || table is null || !table.FieldType.IsGenericType)
            return null;
        Type[] tableArgs = table.FieldType.GetGenericArguments();
        if (tableArgs.Length != 2)
            return null;
        Type box = tableArgs[1];
        Type entry = typeof(KeyValuePair<,>).MakeGenericType(tableArgs);
        PropertyInfo? entryValue = entry.GetProperty("Value");
        Type? defaults = typeof(ModelCapabilities).Assembly.GetType("STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults");
        FieldInfo? boxValue = box.GetField("<Value>k__BackingField", fields);
        FieldInfo? capabilities = typeof(ModelCapabilitySet).GetField("_capabilities", fields);
        FieldInfo? unknownEntries = typeof(ModelCapabilitySet).GetField("_unknownEntries", fields);
        FieldInfo? modifiers = defaults?.GetField("Modifiers", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo? modifierLock = defaults?.GetField("SyncRoot", BindingFlags.NonPublic | BindingFlags.Static);
        return entryValue is null || boxValue is null || capabilities is null
            || unknownEntries is null || modifiers is null || modifierLock is null
                ? null
                : new(collections, table, entryValue, boxValue, capabilities, unknownEntries, modifiers, modifierLock);
    }
}
