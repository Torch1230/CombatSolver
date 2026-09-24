using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatSolver;

// Only prepares the next process. Never treats an edited file as evidence that
// the current CLR has switched GC, and never writes through a game-file hardlink.
internal static class RuntimeGcStartupConfig
{
    internal const string ProfileKey = "CombatSolver.RuntimeProfile";
    internal const string PreviousKey = "CombatSolver.PreviousServerGc";
    private const string ServerKey = "System.GC.Server";

    internal static string Apply(string path, bool enabled)
    {
        byte[] original = File.ReadAllBytes(path);
        JsonObject document = JsonNode.Parse(original) as JsonObject
            ?? throw new InvalidDataException("Empty runtime configuration.");
        if (document["runtimeOptions"] is not JsonObject options)
            throw new InvalidDataException("Missing runtimeOptions.");
        JsonObject properties;
        if (options["configProperties"] is null)
        {
            if (!enabled) return "Unchanged";
            properties = new JsonObject();
            options["configProperties"] = properties;
        }
        else if (options["configProperties"] is JsonObject existing)
            properties = existing;
        else
            throw new InvalidDataException("Invalid configProperties.");

        bool owned = properties.ContainsKey(ProfileKey) || properties.ContainsKey(PreviousKey);
        string? previous = null;
        if (owned)
        {
            if (properties[ProfileKey] is not JsonValue profile
                || !profile.TryGetValue<string>(out string? value)
                || value != RuntimeGcProfile.ServerGenerational
                || properties[PreviousKey] is not JsonValue saved
                || !saved.TryGetValue<string>(out previous)
                || previous is not ("absent" or "true" or "false"))
                throw new InvalidDataException("Unknown or incomplete GC configuration ownership.");
            if (properties[ServerKey] is not JsonValue current
                || !current.TryGetValue<bool>(out bool server) || !server)
                throw new InvalidDataException("Owned GC configuration was changed externally.");
            if (enabled) return "AlreadyConfigured";
            if (previous == "absent") properties.Remove(ServerKey);
            else properties[ServerKey] = previous == "true";
            properties.Remove(ProfileKey);
            properties.Remove(PreviousKey);
        }
        else
        {
            if (!enabled) return "Unchanged";
            previous = "absent";
            if (properties.ContainsKey(ServerKey))
            {
                if (properties[ServerKey] is not JsonValue current
                    || !current.TryGetValue<bool>(out bool server))
                    throw new InvalidDataException("Invalid System.GC.Server value.");
                previous = server ? "true" : "false";
            }
            properties[ServerKey] = true;
            properties[ProfileKey] = RuntimeGcProfile.ServerGenerational;
            properties[PreviousKey] = previous;
        }

        // Same-directory rename is atomic; a crash cannot leave half a JSON file.
        // Preserve unrelated fields and decline a detected concurrent edit.
        string temporary = path + ".combatsolver-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document,
                new JsonSerializerOptions { WriteIndented = true });
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new IOException("Runtime configuration changed while preparing GC mode.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return enabled ? "Prepared" : "Restored";
    }
}
