using System.Collections;
using System.Reflection;
using CombatSolver.Engine.Common;
using STS2RitsuLib.Models.Capabilities;

namespace CombatSolver;

// Native models can carry external behavior without changing their CLR type or
// ModHelper subscriber lists. Check the published hosts, including canonical
// future sources, before cloning. No model/capability factory or listener runs.
internal static class PredictionRitsuCapabilityAudit
{
    private static readonly Guid AuditedRuntimeMvid = new("f7f18a36-8ebb-4db2-b1ea-7bd340579646");
    private static readonly Guid AuditedSharedMvid = new("67c05374-6f96-4bbc-831c-5313d1cc0fd1");
    private sealed record Metadata(FieldInfo Collections, FieldInfo Table,
        PropertyInfo EntryValue, FieldInfo BoxValue, FieldInfo Capabilities, FieldInfo UnknownEntries,
        FieldInfo Modifiers, FieldInfo ModifierLock);
    private static readonly Lazy<Metadata> AuditedMetadata = new(CaptureMetadata);

    internal static void Validate()
    {
        if (typeof(ModelCapabilities).Module.ModuleVersionId != AuditedRuntimeMvid)
            throw new PredictionUnsupportedException("Unaudited RitsuLib model capability runtime.");
        Metadata metadata = AuditedMetadata.Value;
        var modifierLock = metadata.ModifierLock.GetValue(null) as Lock
            ?? throw new PredictionUnsupportedException("Unknown RitsuLib capability registration lock.");
        using (modifierLock.EnterScope())
        {
            var modifiers = metadata.Modifiers.GetValue(null) as ICollection
                ?? throw new PredictionUnsupportedException("Unknown RitsuLib default capability registry.");
            if (modifiers.Count != 0)
                throw new PredictionUnsupportedException("Unsupported RitsuLib default capability modifier; future model behavior is not mirrored.");
        }
        object collections = metadata.Collections.GetValue(null)
            ?? throw new PredictionUnsupportedException("Missing RitsuLib model capability hosts.");
        var table = metadata.Table.GetValue(collections) as IEnumerable
            ?? throw new PredictionUnsupportedException("Unknown RitsuLib model capability host table.");
        // The audited ConditionalWeakTable enumerates existing values, including
        // prototypes that may only become reachable later. It creates no hosts.
        foreach (object entry in table)
        {
            object box = metadata.EntryValue.GetValue(entry)
                ?? throw new PredictionUnsupportedException("Unknown RitsuLib capability host entry.");
            object collection = metadata.BoxValue.GetValue(box)
                ?? throw new PredictionUnsupportedException("Missing RitsuLib capability collection.");
            var capabilities = metadata.Capabilities.GetValue(collection) as ICollection
                ?? throw new PredictionUnsupportedException("Unknown RitsuLib attached capability list.");
            if (capabilities.Count != 0)
                throw new PredictionUnsupportedException("Unsupported RitsuLib attached model capability; its callbacks are not mirrored.");
            var unknown = metadata.UnknownEntries.GetValue(collection) as ICollection
                ?? throw new PredictionUnsupportedException("Unknown RitsuLib saved capability list.");
            if (unknown.Count != 0)
                throw new PredictionUnsupportedException("Unsupported RitsuLib saved model capability; an empty active list does not certify its future behavior.");
        }
    }

    private static Metadata CaptureMetadata()
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo RequireField(Type type, string name, BindingFlags flags)
            => type.GetField(name, flags)
                ?? throw new PredictionUnsupportedException("RitsuLib capability contract changed: " + name);
        FieldInfo collections = RequireField(typeof(ModelCapabilities), "Collections", BindingFlags.NonPublic | BindingFlags.Static);
        Type host = collections.FieldType;
        if (host.Module.ModuleVersionId != AuditedSharedMvid)
            throw new PredictionUnsupportedException("Unaudited RitsuLib attached state implementation.");
        FieldInfo table = RequireField(host, "_table", fields);
        Type box = table.FieldType.GetGenericArguments()[1];
        Type entry = typeof(KeyValuePair<,>).MakeGenericType(table.FieldType.GetGenericArguments());
        PropertyInfo entryValue = entry.GetProperty("Value")
            ?? throw new PredictionUnsupportedException("Missing capability host entry value.");
        Type defaults = typeof(ModelCapabilities).Assembly.GetType("STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults")
            ?? throw new PredictionUnsupportedException("Missing RitsuLib default capability source registry.");
        return new(collections, table, entryValue,
            RequireField(box, "<Value>k__BackingField", fields),
            RequireField(typeof(ModelCapabilitySet), "_capabilities", fields),
            RequireField(typeof(ModelCapabilitySet), "_unknownEntries", fields),
            RequireField(defaults, "Modifiers", BindingFlags.NonPublic | BindingFlags.Static),
            RequireField(defaults, "SyncRoot", BindingFlags.NonPublic | BindingFlags.Static));
    }
}
