using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using CombatSolver.Replay;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private JsonObject? _checkpointImport;
    private string? _checkpointImportDirectory;

    private void PrepareCheckpointRequest()
    {
        if (string.IsNullOrWhiteSpace(_request.CheckpointArchivePath))
            return;
        SetStage("archive_preflight");
        if (_request.ReplayMode is not ("RestoreOnly" or "SearchOnly" or "DeploySolver" or "ReplayRecorded"))
            throw new InvalidDataException($"unsupported_replay_mode:{_request.ReplayMode}");
        _checkpointImportDirectory = Path.Combine(
            ProjectSettings.GlobalizePath("user://checkpoint-imports"), Guid.NewGuid().ToString("N"));
        _checkpointImport = CheckpointArchive.Prepare(
            _request.CheckpointArchivePath,
            _request.ReplayMode == "ReplayRecorded" && _request.CheckpointSelector == "latest"
                ? "recorded" : _request.CheckpointSelector,
            _checkpointImportDirectory);
        _writer.ReplayVerification = new JsonObject
        {
            ["mode"] = _request.ReplayMode,
            ["status"] = _checkpointImport["status"]!.DeepClone(),
            ["reason"] = _checkpointImport["reason"]?.DeepClone(),
            ["checkpoint"] = _checkpointImport["checkpoint"]?.DeepClone(),
            ["recordedPolicy"] = _checkpointImport["recordedPolicy"]?.DeepClone(),
            ["restorationVerified"] = false,
            ["comparisonScope"] = "checkpoint",
        };
        if (_checkpointImport["status"]!.GetValue<string>() != "materials_valid")
            throw new InvalidDataException(_checkpointImport["reason"]!.GetValue<string>());
        JsonObject index = _checkpointImport["index"]!.AsObject();
        if (index["build"] is JsonObject build
            && build["gameModuleId"]?.GetValue<string>() != typeof(MegaCrit.Sts2.Core.Combat.CombatState)
                .Assembly.ManifestModule.ModuleVersionId.ToString())
        {
            _writer.ReplayVerification["status"] = "environment_mismatch";
            throw new InvalidDataException("environment_mismatch:gameModuleId");
        }
        if (_request.ReplayMode == "ReplayRecorded" && !HasNativeRecording)
            throw new InvalidDataException("missing_native_event_recording");
        _writer.ReplayVerification["historySource"] = HasNativeRecording ? "native_events" : "legacy_checkpoint_fields";
        ResolveCheckpointPolicy();
        JsonObject input = JsonSerializer.SerializeToNode(_request, UnattendedTestFiles.JsonOptions)!.AsObject();
        foreach ((string key, JsonNode? value) in _checkpointImport["request"]!.AsObject())
            input[key] = value?.DeepClone();
        input["runSnapshotPath"] = _checkpointImport["paths"]!["runStatePath"]!.DeepClone();
        input["replayStatePath"] = _checkpointImport["paths"]!["replayStatePath"]!.DeepClone();
        input["nativeStatePath"] = _checkpointImport["paths"]!["nativeStatePath"]!.DeepClone();
        if (_request.ReplayMode is "RestoreOnly" or "ReplayRecorded")
            input["stopAfterCombatRootSnapshotAssertion"] = true;
        if (_request.ReplayMode == "SearchOnly")
            input["stopAfterInitialSolverResultAssertion"] = true;
        if (_request.ReplayMode == "DeploySolver")
        {
            input["deploymentFastModeForTest"] = "Instant";
            input["deploymentInterActionDelaySecondsForTest"] = 0;
            input["expectedUnexpectedReplansAtMost"] = 0;
        }
        _request = input.Deserialize<UnattendedTestRequest>(UnattendedTestFiles.JsonOptions)!;
        if (_checkpointImport["resolvedPolicy"]?["forceShortOnly"] is JsonValue forceShort)
            _protocolHost.ApplyRecordedShortSearchMode(_request.ForceShortSearchOnly || forceShort.GetValue<bool>());
    }

    private void RecordCheckpointRestored()
    {
        if (_writer.ReplayVerification == null)
            return;
        _writer.ReplayVerification["restorationVerified"] = true;
        _writer.ReplayVerification["nativeStateVerified"] = !string.IsNullOrWhiteSpace(_request.NativeStatePath);
        _writer.ReplayVerification["status"] = "restored";
        _completedChecks.Add("CheckpointContinuationMatched");
    }

    private SolverSettingsData ApplyRecordedCheckpointPolicy(SolverSettingsData current)
    {
        if (_checkpointImport == null)
            return current;
        if (_checkpointImport["resolvedPolicy"] is not JsonObject recorded)
        {
            if (_request.ReplayMode is "SearchOnly" or "DeploySolver")
                throw new InvalidDataException("legacy_missing_effective_policy");
            return current;
        }
        JsonObject settings = JsonSerializer.SerializeToNode(current, UnattendedTestFiles.JsonOptions)!.AsObject();
        settings["objective"] = recorded["objective"]?.DeepClone()
            ?? JsonSerializer.SerializeToNode(SearchObjectivePolicy.Default, UnattendedTestFiles.JsonOptions);
        // Archives recorded before growth policy existed used zero willingness for this feature.
        settings["growthBudgets"] = recorded["growthBudgets"]?.DeepClone()
            ?? JsonSerializer.SerializeToNode(default(GrowthValues), UnattendedTestFiles.JsonOptions);
        // Archives recorded before the ignore switch existed considered long-term rewards.
        settings["ignoreLongTermRewards"] = recorded["ignoreLongTermRewards"]?.DeepClone()
            ?? JsonSerializer.SerializeToNode(false, UnattendedTestFiles.JsonOptions);
        foreach (string name in new[] { "potionDirectives", "actTransitionBossHpStrategy", "finalBossHpStrategy", "acceptableBattleHpLoss", "searchMaxDegreeOfParallelism" })
            settings[name] = recorded[name]?.DeepClone() ?? throw new InvalidDataException($"missing_policy:{name}");
        SolverSearchProfile shortProfile = recorded["shortProfile"]!.Deserialize<SolverSearchProfile>(UnattendedTestFiles.JsonOptions)!;
        SolverSearchProfile deepProfile = recorded["deepProfile"]!.Deserialize<SolverSearchProfile>(UnattendedTestFiles.JsonOptions)!;
        SolverSettingsData restored = settings.Deserialize<SolverSettingsData>(UnattendedTestFiles.JsonOptions)!;
        return restored with
        {
            PotionPolicy = recorded["potionPolicy"]!.Deserialize<SolverPotionPolicy>(UnattendedTestFiles.JsonOptions),
            PerformancePreset = SolverPerformancePreset.Custom,
            ShortBeamWidth = shortProfile.BeamWidth,
            DeepBeamWidth = deepProfile.BeamWidth,
            ShortMaxExpandedNodes = shortProfile.MaxExpandedNodes,
            DeepMaxExpandedNodes = deepProfile.MaxExpandedNodes,
            ShortMaxCardBranchesPerNode = shortProfile.MaxCardBranchesPerNode,
            DeepMaxCardBranchesPerNode = deepProfile.MaxCardBranchesPerNode,
            ShortMaxPileChoiceBranchesPerAction = shortProfile.MaxPileChoiceBranchesPerAction,
            DeepMaxPileChoiceBranchesPerAction = deepProfile.MaxPileChoiceBranchesPerAction,
            ShortMaxHandChoiceBranchesPerAction = shortProfile.MaxHandChoiceBranchesPerAction,
            DeepMaxHandChoiceBranchesPerAction = deepProfile.MaxHandChoiceBranchesPerAction,
            ShortTimeLimitSeconds = shortProfile.SoftTimeBudgetMilliseconds / 1000d,
            DeepTimeLimitSeconds = deepProfile.SoftTimeBudgetMilliseconds / 1000d,
        };
    }

    private void ReleaseCheckpointImport()
    {
        CombatReplayRecording.TestObserver = null;
        CombatReplayRecording.TestCombatStartObserver = null;
        CombatReplayRecording.TestCombatEndObserver = null;
        CombatReplayRecording.TestSearchResultObserver = null;
        if (_checkpointImportDirectory != null && Directory.Exists(_checkpointImportDirectory))
            Directory.Delete(_checkpointImportDirectory, recursive: true);
    }

    private bool HasNativeRecording => _checkpointImport?["index"]?["recording"]?["complete"]?.GetValue<bool>() == true;

    private void ValidateCheckpointModsAfterStartup()
    {
        if (_checkpointImport?["index"]?["build"]?["mods"] is not JsonArray mods) return;
        JsonNode actual = JsonSerializer.SerializeToNode(CombatReplayRecording.CaptureModIdentity(), UnattendedTestFiles.JsonOptions)!;
        if (JsonNode.DeepEquals(mods, actual)) return;
        _writer.ReplayVerification!["firstDifference"] = new JsonObject
            { ["field"] = "environment.mods", ["expected"] = mods.DeepClone(), ["actual"] = actual };
        throw new InvalidDataException("environment_mismatch:mods");
    }

    private void ResolveCheckpointPolicy()
    {
        JsonObject? recorded = _checkpointImport!["recordedPolicy"] as JsonObject;
        JsonObject policy = recorded == null ? new JsonObject() : (JsonObject)recorded.DeepClone();
        if (recorded == null && _checkpointImport["legacySettings"] is JsonObject legacy)
        {
            foreach (string key in new[] { "potionPolicy", "potionDirectives", "objective", "growthBudgets", "actTransitionBossHpStrategy", "finalBossHpStrategy", "acceptableBattleHpLoss", "searchMaxDegreeOfParallelism" })
                if (legacy[key] != null)
                    policy[key] = legacy[key]!.DeepClone();
            if (_checkpointImport["legacySearchProfiles"] is JsonObject profiles)
                foreach (string key in new[] { "shortProfile", "deepProfile" })
                    policy[key] = profiles[key]?.DeepClone();
        }
        if (!string.IsNullOrWhiteSpace(_request.ReplayPolicyOverridePath))
        {
            JsonObject overrides = JsonNode.Parse(File.ReadAllText(_request.ReplayPolicyOverridePath)) as JsonObject
                ?? throw new InvalidDataException("expected_replay_policy_override_object");
            HashSet<string> allowed = new(StringComparer.Ordinal)
            {
                "potionPolicy", "potionDirectives", "objective", "growthBudgets", "actTransitionBossHpStrategy", "finalBossHpStrategy",
                "acceptableBattleHpLoss", "searchMaxDegreeOfParallelism", "shortProfile", "deepProfile", "forceShortOnly",
            };
            foreach ((string key, JsonNode? value) in overrides)
            {
                if (!allowed.Contains(key) || value == null)
                    throw new InvalidDataException($"invalid_policy_override:{key}");
                policy[key] = value.DeepClone();
            }
            _writer.ReplayVerification!["policyOverrides"] = overrides.DeepClone();
        }
        string[] required = ["potionPolicy", "potionDirectives", "actTransitionBossHpStrategy", "finalBossHpStrategy",
            "acceptableBattleHpLoss", "searchMaxDegreeOfParallelism", "shortProfile", "deepProfile"];
        string[] missing = required.Where(key => policy[key] == null).ToArray();
        _writer.ReplayVerification!["missingPolicyFields"] = JsonSerializer.SerializeToNode(missing);
        if (missing.Length > 0 && _request.ReplayMode is "SearchOnly" or "DeploySolver")
            throw new InvalidDataException("missing_recorded_policy:" + string.Join(',', missing));
        if (missing.Length == 0)
            _checkpointImport["resolvedPolicy"] = policy;
    }
}
