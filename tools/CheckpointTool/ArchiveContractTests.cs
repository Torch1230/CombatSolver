using System.IO.Compression;
using System.Text.Json.Nodes;
using CombatSolver.Replay;

internal static class ArchiveContractTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CombatSolver-ArchiveTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int assertions = 0;
        try
        {
            string valid = WriteFixture(root, "valid", false);
            JsonObject result = CheckpointArchive.Prepare(valid, "latest", Path.Combine(root, "import"));
            string metadata = result["paths"]!["metadataPath"]!.GetValue<string>();
            string replay = result["paths"]!["replayStatePath"]!.GetValue<string>();
            Check(metadata != replay && File.ReadAllText(metadata) != File.ReadAllText(replay), "separate_paired_json");
            Check(result["checkpoint"]!["checkpointId"]!.GetValue<string>() == "s:1", "latest_is_playable_before_end");
            Check(result["restorationVerified"]!.GetValue<bool>() == false, "preflight_is_not_restore_proof");
            Check(CheckpointArchive.Inspect(valid, "end")["checkpoint"]!["checkpointId"]!.GetValue<string>() == "s:2", "explicit_end_selector");
            string legacy = WriteFixture(root, "legacy", true);
            Check(CheckpointArchive.Inspect(legacy)["status"]!.GetValue<string>() == "materials_valid", "legacy_without_index");
            string mismatch = WriteFixture(root, "mismatch", false, wrongSession: true);
            Reject(() => CheckpointArchive.Inspect(mismatch), "checkpoint_pair_mismatch");
            string duplicate = WriteFixture(root, "duplicate", false, duplicate: true);
            Reject(() => CheckpointArchive.Inspect(duplicate), "duplicate_entry");
            string missing = WriteFixture(root, "missing", false);
            using (ZipArchive archive = ZipFile.Open(missing, ZipArchiveMode.Update))
                archive.GetEntry("combat-solver/forensics/current/native-state/000001-search_completed.bin")!.Delete();
            Check(CheckpointArchive.Inspect(missing)["status"]!.GetValue<string>() == "materials_missing", "missing_native_material");
            string aggregate = Path.Combine(root, "aggregate.zip");
            using (ZipArchive archive = ZipFile.Open(aggregate, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(valid, "logs/first.zip");
                archive.CreateEntryFromFile(valid, "logs/duplicate.zip");
                archive.CreateEntryFromFile(legacy, "logs/old.zip");
            }
            BatchInputs discovery = new(Path.Combine(root, "batch-inputs"));
            Check(discovery.Discover(aggregate).Count == 2 && discovery.Duplicates == 1, "aggregate_deduplication");
            string journal = Path.Combine(root, "results.jsonl");
            File.WriteAllText(journal, "{\"status\":\"restored\"}\n{\"interrupted\":");
            Check(BatchRunner.ReadJournal(journal).Count == 1 && File.Exists(journal + ".interrupted-tail"), "resume_partial_last_record");
            Check(BatchRunner.Classify(new JsonObject
            {
                ["status"] = "Failed", ["solverMetrics"] = new JsonObject { ["projectedBattleHpLost"] = 0 },
                ["replayVerification"] = new JsonObject { ["status"] = "restore_mismatch" },
            }, 1) == "restore_mismatch", "technical_failure_precedes_hp");
            Check(BatchRunner.Classify(null, 124) == "timeout", "launcher_timeout_classification");
            Check(BatchRunner.Classify(null, 1, new JsonObject { ["status"] = "timeout" }) == "timeout", "launcher_deadline_is_not_crash");
            foreach (string fault in new[] { "diagnostic", "alias", "recording", "identity" })
            {
                string faulty = WriteFixture(root, fault, false);
                using (ZipArchive archive = ZipFile.Open(faulty, ZipArchiveMode.Update))
                {
                    ZipArchiveEntry item = archive.GetEntry(CheckpointArchive.IndexPath)!;
                    JsonObject index;
                    using (Stream source = item.Open()) index = JsonNode.Parse(source)!.AsObject();
                    item.Delete();
                    if (fault == "diagnostic") index["diagnosticOnly"] = true;
                    if (fault == "alias") index["checkpoints"]![0]!["nativeStatePath"] = index["checkpoints"]![0]!["metadataPath"]!.DeepClone();
                    if (fault == "identity") index["checkpoints"]!.AsArray().Add(index["checkpoints"]![0]!.DeepClone());
                    if (fault == "recording") index["recording"] = new JsonObject
                    { ["complete"] = true, ["originPath"] = "missing-origin.json", ["runSavePath"] = "missing.save", ["eventsPath"] = "missing.jsonl" };
                    using StreamWriter writer = new(archive.CreateEntry(CheckpointArchive.IndexPath).Open());
                    writer.Write(index.ToJsonString());
                }
                if (fault is "diagnostic" or "recording")
                    Check(CheckpointArchive.Inspect(faulty)["status"]!.GetValue<string>() == "materials_missing", fault + "_blocked");
                else Reject(() => CheckpointArchive.Inspect(faulty), fault == "alias" ? "aliased_state_artifact" : "invalid_checkpoint_catalog_identity");
            }
            AppendOnlyEventLog<int> events = new(value => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value));
            events.TryAppend(1, 4);
            Task<EventLogSnapshot> prefix = events.CaptureAsync();
            events.TryAppend(2, 4);
            EventLogSnapshot complete = events.CaptureAsync().GetAwaiter().GetResult();
            Check(prefix.GetAwaiter().GetResult().EventCount == 1 && complete.EventCount == 2,
                "event_log_snapshot_is_ordered_prefix");
            Check(System.Text.Encoding.UTF8.GetString(complete.JsonLines) == "1\n2\n", "event_log_append_order");
            events.Dispose();
            events.Completion.GetAwaiter().GetResult();
            AppendOnlyEventLog<int> bounded = new(value => [1, 2], maximumPendingBytes: 1);
            Check(!bounded.TryAppend(1, 4) && bounded.CaptureAsync().GetAwaiter().GetResult().Error == "event_pending_memory_limit",
                "event_memory_limit_is_explicit");
            bounded.Dispose();
            bounded.Completion.GetAwaiter().GetResult();
            JsonObject policy = JsonNode.Parse("""{"potionPolicy":"Smart","potionDirectives":[],"actTransitionBossHpStrategy":"ProgressionFirst","finalBossHpStrategy":"ProgressionFirst","acceptableBattleHpLoss":0,"searchMaxDegreeOfParallelism":4,"shortProfile":{},"deepProfile":{},"forceShortOnly":true}""")!.AsObject();
            JsonObject candidate = JsonNode.Parse("""{"status":"deployment_completed","inputIdentity":"same","comparisonScope":"full_combat","environment":{},"verification":{"actualOutcome":{"combatEnded":true,"survived":true,"hpLost":47}}}""")!.AsObject();
            candidate["verification"]!["executedPolicy"] = policy.DeepClone();
            candidate["verification"]!["recordedPolicy"] = policy.DeepClone();
            JsonObject reference = JsonNode.Parse("""{"status":"recorded_completed","inputIdentity":"same","comparisonScope":"full_combat","environment":{},"verification":{"recordedOutcome":{"combatEnded":true,"survived":true,"hpLost":50}}}""")!.AsObject();
            JsonObject inspection = JsonNode.Parse("""{"index":{"recording":{"inputOrigins":{"player":3,"system":2}}}}""")!.AsObject();
            BatchRunner.CompareVerifiedRoute(candidate, [reference], inspection);
            Check(candidate["relativeToManual"]!.GetValue<int>() == 3 && candidate["betterThanManual"]!.GetValue<bool>(), "verified_human_plus_three");
            candidate.Remove("relativeToManual");
            candidate.Remove("betterThanManual");
            inspection["index"]!["recording"]!["inputOrigins"]!["solver"] = 1;
            BatchRunner.CompareVerifiedRoute(candidate, [reference], inspection);
            Check(candidate["relativeToManual"] == null, "mixed_route_is_not_human_baseline");
            inspection["index"]!["recording"]!["inputOrigins"]!["solver"] = 0;
            candidate["verification"]!["actualOutcome"]!["combatEnded"] = false;
            BatchRunner.CompareVerifiedRoute(candidate, [reference], inspection);
            Check(candidate["relativeToManual"] == null, "unfinished_route_is_not_actual_improvement");
            JsonObject overridden = (JsonObject)policy.DeepClone();
            overridden["potionPolicy"] = "Disabled";
            Check(!BatchRunner.EquivalentPolicy(policy, overridden), "different_potion_policy_is_not_comparable");
            JsonObject unified = (JsonObject)policy.DeepClone();
            unified.Remove("shortProfile");
            unified.Remove("deepProfile");
            unified.Remove("forceShortOnly");
            unified["profile"] = new JsonObject { ["beamWidth"] = 60 };
            unified["fixedBudget"] = true;
            Check(BatchRunner.EquivalentPolicy(unified, unified.DeepClone()), "unified_policy_is_comparable");
            Check(!BatchRunner.EquivalentPolicy(unified, policy), "legacy_stage_budget_is_not_same_policy");
            foreach (string unsafePath in new[] { "../escape", "/absolute", "C:/absolute", "a\\b", "a/./b" })
                Reject(() => CheckpointArchive.ValidateEntryPath(unsafePath), "unsafe_entry");
            Console.WriteLine($"archive_contract_tests_passed assertions={assertions}");
            return 0;
        }
        finally
        {
            Directory.Delete(root, true);
        }

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            assertions++;
        }
        void Reject(Action action, string reason)
        {
            try { action(); }
            catch (InvalidDataException error) when (error.Message.StartsWith(reason, StringComparison.Ordinal))
            {
                assertions++;
                return;
            }
            throw new InvalidOperationException("expected_rejection:" + reason);
        }
    }

    private static string WriteFixture(string directory, string name, bool legacy, bool wrongSession = false, bool duplicate = false)
    {
        string path = Path.Combine(directory, name + ".zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        string prefix = "combat-solver/forensics/current/";
        Write(prefix + "session.json", "{\"sessionId\":\"s\"}");
        JsonArray checkpoints = [];
        for (int sequence = 1; sequence <= 2; sequence++)
        {
            string label = sequence == 1 ? "search_completed" : "combat_end";
            string file = $"{sequence:D6}-{label}.json";
            Write(prefix + "checkpoints/" + file, new JsonObject
            {
                ["sessionId"] = wrongSession ? "other" : "s", ["label"] = label,
                ["encounterId"] = "e", ["exactContinuationState"] = "root", ["playerPhase"] = "Play",
            }.ToJsonString());
            Write(prefix + "replay-state/" + file,
                """{"schemaVersion":1,"encounterId":"e","exactContinuationState":"root","ascensionLevel":0,"currentActIndex":0,"runRng":{"seed":"seed"},"players":[{"characterId":"c"}]}""");
            Write(prefix + "run-state/" + Path.ChangeExtension(file, ".save"), "{\"rng\":{\"seed\":\"seed\"}}");
            Write(prefix + "native-state/" + Path.ChangeExtension(file, ".bin"), "native");
            checkpoints.Add(new JsonObject
            {
                ["checkpointId"] = "s:" + sequence,
                ["metadataPath"] = prefix + "checkpoints/" + file,
                ["replayStatePath"] = prefix + "replay-state/" + file,
                ["nativeStatePath"] = prefix + "native-state/" + Path.ChangeExtension(file, ".bin"),
                ["runStatePath"] = prefix + "run-state/" + Path.ChangeExtension(file, ".save"),
            });
        }
        if (!legacy)
            Write(CheckpointArchive.IndexPath, new JsonObject
            {
                ["schemaVersion"] = 2, ["sessionId"] = "s", ["defaultCheckpointId"] = "s:1",
                ["combatEndCheckpointId"] = "s:2", ["checkpoints"] = checkpoints,
            }.ToJsonString());
        if (duplicate)
            Write(prefix + "session.json", "{}");
        return path;

        void Write(string entryName, string text)
        {
            using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
            writer.Write(text);
        }
    }
}
