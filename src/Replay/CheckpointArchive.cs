using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatSolver.Replay;

// Shared by the game test host and the platform-independent command line tool.
// This layer understands archive contracts, never live game objects.
internal static class CheckpointArchive
{
    public const string IndexPath = "replay/checkpoint.json";
    public const string LegacyIndexPath = "combat-solver/checkpoint.json";
    public const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private static readonly string[] StateArtifacts =
        ["metadataPath", "replayStatePath", "nativeStatePath", "runStatePath"];
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static JsonObject Inspect(string archivePath, string selector = "latest")
    {
        using ZipArchive archive = OpenValidated(archivePath);
        JsonObject index = archive.GetEntry(IndexPath) != null ? ReadObject(archive, IndexPath)
            : archive.GetEntry(LegacyIndexPath) != null ? ReadObject(archive, LegacyIndexPath)
            : BuildLegacyIndex(archive);
        int version = RequiredInt(index, "schemaVersion");
        if (version == 1 && archive.Entries.Any(entry => entry.FullName.StartsWith("combat-solver/forensics/", StringComparison.Ordinal)))
        {
            index = BuildLegacyIndex(archive);
            index["originalSchemaVersion"] = 1;
            version = 2;
        }
        JsonObject checkpoint;
        if (version == 1)
        {
            if (selector != "latest")
                return Blocked("legacy_missing_checkpoint_catalog", index);
            if (index["available"]?.GetValue<bool>() != true)
                return Blocked("missing_checkpoint", index);
            checkpoint = (JsonObject)index.DeepClone();
            checkpoint["checkpointId"] = RequiredString(index, "checkpoint");
            checkpoint["restoreMethod"] = "legacy_checkpoint";
        }
        else if (version == 2)
        {
            JsonArray checkpoints = index["checkpoints"] as JsonArray
                ?? throw new InvalidDataException("missing_checkpoint_catalog");
            HashSet<string> identities = new(StringComparer.Ordinal);
            foreach (JsonNode? item in checkpoints)
                if (item is not JsonObject entry || !identities.Add(RequiredString(entry, "checkpointId")))
                    throw new InvalidDataException("invalid_checkpoint_catalog_identity");
            string? id = selector switch
            {
                "latest" => index["defaultCheckpointId"]?.GetValue<string>(),
                "start" => index["combatStartCheckpointId"]?.GetValue<string>(),
                "end" => index["combatEndCheckpointId"]?.GetValue<string>(),
                "recorded" => index["combatEndCheckpointId"]?.GetValue<string>()
                    ?? index["defaultCheckpointId"]?.GetValue<string>(),
                _ => selector,
            };
            if (id == null)
                return Blocked("missing_selected_checkpoint", index);
            JsonObject[] matches = checkpoints.OfType<JsonObject>()
                .Where(item => RequiredString(item, "checkpointId") == id).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"checkpoint_identity_count:{id}:{matches.Length}");
            checkpoint = (JsonObject)matches[0].DeepClone();
        }
        else
        {
            return Blocked($"unsupported_index_schema:{version}", index);
        }
        if (index["diagnosticOnly"]?.GetValue<bool>() == true)
            return Blocked("diagnostic_only:" + (index["recording"]?["incompleteReason"]?.GetValue<string>()
                ?? index["captureErrors"]?.ToJsonString() ?? "capture_incomplete"), index, checkpoint);

        HashSet<string> materialPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string field in StateArtifacts)
        {
            string path = RequiredString(checkpoint, field);
            ValidateEntryPath(path);
            if (!materialPaths.Add(path))
                throw new InvalidDataException($"aliased_state_artifact:{field}:{path}");
            if (archive.GetEntry(path) == null)
                return Blocked($"missing_artifact:{field}:{path}", index, checkpoint);
            if (archive.GetEntry(path)!.Length == 0)
                return Blocked($"empty_artifact:{field}:{path}", index, checkpoint);
        }
        JsonObject metadata = ReadObject(archive, RequiredString(checkpoint, "metadataPath"));
        JsonObject replay = ReadObject(archive, RequiredString(checkpoint, "replayStatePath"));
        JsonObject run = ReadObject(archive, RequiredString(checkpoint, "runStatePath"));
        if (RequiredString(metadata, "exactContinuationState") != RequiredString(replay, "exactContinuationState"))
            throw new InvalidDataException("checkpoint_pair_mismatch:exactContinuationState");
        if (RequiredString(metadata, "encounterId") != RequiredString(replay, "encounterId"))
            throw new InvalidDataException("checkpoint_pair_mismatch:encounterId");
        if (version == 2 && RequiredString(metadata, "sessionId") != RequiredString(index, "sessionId"))
            throw new InvalidDataException("checkpoint_pair_mismatch:sessionId");
        if (RequiredInt(replay, "schemaVersion") != 1)
            return Blocked("unsupported_replay_state_schema", index, checkpoint);
        if (run["rng"] == null || replay["runRng"] == null)
            return Blocked("missing_rng", index, checkpoint);
        if (RequiredString(run["rng"]!.AsObject(), "seed") != RequiredString(replay["runRng"]!.AsObject(), "seed"))
            throw new InvalidDataException("checkpoint_pair_mismatch:seed");
        if (index["recording"] is JsonObject recording && recording["complete"]?.GetValue<bool>() == true)
        {
            foreach (string field in new[] { "originPath", "runSavePath", "eventsPath" })
            {
                string path = RequiredString(recording, field);
                ValidateEntryPath(path);
                if (archive.GetEntry(path) == null)
                    return Blocked($"missing_recording_artifact:{field}:{path}", index, checkpoint);
            }
            long count = 0;
            using StreamReader events = new(archive.GetEntry(RequiredString(recording, "eventsPath"))!.Open());
            while (events.ReadLine() is { } line)
            {
                JsonObject value = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("invalid_recording_event");
                if (value["Sequence"]?.GetValue<long>() != count++)
                    throw new InvalidDataException("recording_sequence_gap");
                try
                {
                    if (Convert.FromBase64String(RequiredString(value, "Payload")).Length == 0)
                        throw new InvalidDataException("empty_recording_event");
                }
                catch (FormatException error) { throw new InvalidDataException("invalid_recording_payload", error); }
            }
            if (count != recording["eventCount"]?.GetValue<long>()
                || checkpoint["eventCursor"]?.GetValue<long>() is not long cursor || cursor < 0 || cursor > count)
                throw new InvalidDataException("recording_event_cursor_mismatch");
        }
        JsonArray players = replay["players"] as JsonArray
            ?? throw new InvalidDataException("missing_players");
        if (players.Count != 1 || players[0] is not JsonObject player)
            return Blocked("requires_single_player", index, checkpoint);

        JsonObject request = new()
        {
            ["characterId"] = RequiredString(player, "characterId"),
            ["encounterId"] = RequiredString(replay, "encounterId"),
            ["seed"] = RequiredString(replay["runRng"]!.AsObject(), "seed"),
            ["ascension"] = RequiredInt(replay, "ascensionLevel"),
            ["actIndexForTest"] = RequiredInt(replay, "currentActIndex"),
        };
        return new JsonObject
        {
            ["status"] = "materials_valid",
            ["archivePath"] = Path.GetFullPath(archivePath),
            ["index"] = index.DeepClone(),
            ["checkpoint"] = checkpoint,
            ["request"] = request,
            ["recordedPolicy"] = (index["searchPolicies"]?.AsArray().OfType<JsonObject>()
                .LastOrDefault(item => item["checkpointId"]?.GetValue<string>() != null
                    && item["checkpointId"]?.GetValue<string>() == metadata["searchRootId"]?.GetValue<string>())?["policy"]
                ?? metadata["effectivePolicy"])?.DeepClone(),
            ["legacySettings"] = metadata["settings"]?.DeepClone(),
            ["legacySearchProfiles"] = replay["searchProfiles"]?.DeepClone(),
            ["sourceOutcome"] = metadata["outcome"]?.DeepClone(),
            ["report"] = archive.GetEntry("report.json") != null ? ReadObject(archive, "report.json")
                : archive.GetEntry("combat-solver/report.json") != null
                ? ReadObject(archive, "combat-solver/report.json") : null,
            ["restorationVerified"] = false,
        };
    }

    public static JsonObject Prepare(string archivePath, string selector, string destinationDirectory)
    {
        JsonObject result = Inspect(archivePath, selector);
        if (result["status"]!.GetValue<string>() != "materials_valid")
            return result;
        JsonObject checkpoint = result["checkpoint"]!.AsObject();
        string destination = Path.GetFullPath(destinationDirectory);
        using ZipArchive archive = OpenValidated(archivePath);
        Directory.CreateDirectory(destination);
        JsonObject paths = new();
        IEnumerable<string> artifacts = StateArtifacts.Select(field => RequiredString(checkpoint, field));
        JsonObject index = result["index"]!.AsObject();
        if (checkpoint["label"]?.GetValue<string>() == "combat_start"
            && index["checkpoints"] is JsonArray checkpoints)
        {
            JsonObject? ready = checkpoints.OfType<JsonObject>()
                .FirstOrDefault(item => item["canSearch"]?.GetValue<bool>() == true);
            if (ready != null)
                artifacts = artifacts.Concat(StateArtifacts.Select(field => RequiredString(ready, field)));
        }
        if (index["recording"] is JsonObject recording)
            artifacts = artifacts.Concat(recording.Where(item => item.Key.EndsWith("Path", StringComparison.Ordinal))
                .Select(item => item.Value!.GetValue<string>()));
        foreach (string relative in artifacts.Append(IndexPath).Distinct(StringComparer.Ordinal))
        {
            ValidateEntryPath(relative);
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"unsafe_destination:{relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (relative == IndexPath)
            {
                using FileStream generated = new(target, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(generated, index, JsonOptions);
                continue;
            }
            ZipArchiveEntry entry = archive.GetEntry(relative)
                ?? throw new InvalidDataException($"missing_artifact:{relative}");
            using Stream input = entry.Open();
            using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
        }
        foreach (string field in StateArtifacts)
            paths[field] = Path.Combine(destination, RequiredString(checkpoint, field));
        result["paths"] = paths;
        result["extractedRoot"] = destination;
        return result;
    }

    private static JsonObject BuildLegacyIndex(ZipArchive archive)
    {
        string? slot = new[] { "current", "recent" }.FirstOrDefault(candidate =>
            archive.GetEntry($"combat-solver/forensics/{candidate}/session.json") != null);
        if (slot == null)
            throw new InvalidDataException("legacy_missing_forensic_session");
        string prefix = $"combat-solver/forensics/{slot}/";
        JsonObject session = ReadObject(archive, prefix + "session.json");
        string sessionId = RequiredString(session, "sessionId");
        JsonArray checkpoints = [];
        string? latest = null;
        string? start = null;
        string? end = null;
        foreach (ZipArchiveEntry entry in archive.Entries.Where(item =>
                     item.FullName.StartsWith(prefix + "checkpoints/", StringComparison.Ordinal)
                     && item.FullName.EndsWith(".json", StringComparison.Ordinal))
                 .OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            JsonObject metadata = ReadObject(archive, entry.FullName);
            string name = Path.GetFileName(entry.FullName);
            string id = sessionId + ":" + name;
            string label = RequiredString(metadata, "label");
            string? phase = metadata["playerPhase"]?.GetValue<string>();
            bool searchable = phase == "Play" && label != "combat_end";
            checkpoints.Add(new JsonObject
            {
                ["checkpointId"] = id, ["label"] = label,
                ["restoreMethod"] = "legacy_checkpoint", ["canSearch"] = searchable,
                ["combatEnded"] = label == "combat_end", ["restorationVerified"] = false,
                ["metadataPath"] = entry.FullName,
                ["replayStatePath"] = prefix + "replay-state/" + name,
                ["nativeStatePath"] = prefix + "native-state/" + Path.ChangeExtension(name, ".bin"),
                ["runStatePath"] = prefix + "run-state/" + Path.ChangeExtension(name, ".save"),
            });
            if (searchable)
                latest = id;
            if (label == "combat_start")
                start = id;
            if (label == "combat_end")
                end = id;
        }
        return new JsonObject
        {
            ["schemaVersion"] = 2, ["legacyFormat"] = true, ["slot"] = slot,
            ["sessionId"] = sessionId, ["defaultCheckpointId"] = latest,
            ["combatStartCheckpointId"] = start, ["combatEndCheckpointId"] = end,
            ["checkpoints"] = checkpoints,
        };
    }

    internal static ZipArchive OpenValidated(string path, long maximumArchiveBytes = MaximumArchiveBytes,
        long maximumExpandedBytes = MaximumExpandedBytes)
    {
        FileInfo file = new(path);
        if (!file.Exists)
            throw new FileNotFoundException("archive_not_found", path);
        if (file.Length <= 0 || file.Length > maximumArchiveBytes)
            throw new InvalidDataException($"archive_size_limit:{file.Length}");
        ZipArchive archive = ZipFile.OpenRead(path);
        try
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ValidateEntryPath(entry.FullName.TrimEnd('/'));
                if (!names.Add(entry.FullName))
                    throw new InvalidDataException($"duplicate_entry:{entry.FullName}");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException($"symlink_entry:{entry.FullName}");
                expanded = checked(expanded + entry.Length);
                if (entry.Length > MaximumEntryBytes || expanded > maximumExpandedBytes)
                    throw new InvalidDataException($"expanded_size_limit:{entry.FullName}");
            }
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    internal static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\')
            || path.Contains(':') || path.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException($"unsafe_entry:{path}");
    }

    private static JsonObject ReadObject(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"missing_artifact:{path}");
        using Stream stream = entry.Open();
        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidDataException($"expected_json_object:{path}");
    }

    internal static string RequiredString(JsonObject node, string name)
        => node[name]?.GetValue<string>() is { Length: > 0 } value ? value
            : throw new InvalidDataException($"missing_string:{name}");
    private static int RequiredInt(JsonObject node, string name)
        => node[name]?.GetValue<int>() ?? throw new InvalidDataException($"missing_integer:{name}");
    private static JsonObject Blocked(string reason, JsonObject? index = null, JsonObject? checkpoint = null)
        => new()
        {
            ["status"] = "materials_missing", ["reason"] = reason,
            ["index"] = index?.DeepClone(), ["checkpoint"] = checkpoint?.DeepClone(),
            ["restorationVerified"] = false,
        };
}
