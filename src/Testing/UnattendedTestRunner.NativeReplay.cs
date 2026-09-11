using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task<ScenarioContext> BuildNativeRecordedScenarioAsync()
    {
        SetStage("native_replay_startup");
        await _host.GameStartupComplete;
        RecordCheckpointModDifferencesAfterStartup();
        ApplyHeadlessFastModeOverride();
        EnsureWithinDeadline();
        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("native_replay_requires_idle_run");
        JsonObject import = _checkpointImport!;
        JsonObject recording = import["index"]!["recording"]!.AsObject();
        string RootPath(string key) => Path.Combine(_checkpointImportDirectory!, recording[key]!.GetValue<string>());
        JsonObject origin = JsonNode.Parse(await File.ReadAllTextAsync(RootPath("originPath")))!.AsObject();
        if (origin["modelIdHash"]!.GetValue<uint>() != ModelIdSerializationCache.Hash)
        {
            _writer.ReplayVerification!["modelSerializationComparison"] = new JsonObject
            {
                ["field"] = "serialization.modelIdHash",
                ["expected"] = origin["modelIdHash"]!.DeepClone(),
                ["actual"] = ModelIdSerializationCache.Hash,
            };
        }
        SerializableRun save = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(RootPath("runSavePath")), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
        if (save.Players.Count != 1)
            throw new InvalidDataException("native_replay_requires_single_player");
        RecordedCombatEvent[] events = File.ReadLines(RootPath("eventsPath"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<RecordedCombatEvent>(line)!).ToArray();
        for (int index = 0; index < events.Length; index++)
            if (events[index].Sequence != index)
                throw new InvalidDataException($"recording_sequence_gap:{index}");
        JsonObject checkpoint = import["checkpoint"]!.AsObject();
        int target = checked((int)checkpoint["eventCursor"]!.GetValue<long>());
        if ((uint)target > (uint)events.Length)
            throw new InvalidDataException("checkpoint_event_cursor_out_of_range");
        JsonObject metadata = JsonNode.Parse(await File.ReadAllTextAsync(
            import["paths"]!["metadataPath"]!.GetValue<string>()))!.AsObject();
        string expectedState = metadata["exactContinuationState"]!.GetValue<string>();
        bool combatStart = checkpoint["label"]!.GetValue<string>() == "combat_start";
        bool combatEnd = checkpoint["combatEnded"]?.GetValue<bool>() == true;
        JsonObject? readyCheckpoint = null;
        if (combatStart && _request.ReplayMode == "RestoreOnly")
        {
            JsonObject? firstPlayable = import["index"]!["checkpoints"]!.AsArray().OfType<JsonObject>()
                .FirstOrDefault(item => item["canSearch"]?.GetValue<bool>() == true);
            if (firstPlayable != null)
            {
                readyCheckpoint = firstPlayable;
                target = checked((int)firstPlayable["eventCursor"]!.GetValue<long>());
                _writer.ReplayVerification!["readyCheckpointId"] = firstPlayable["checkpointId"]!.DeepClone();
            }
        }

        SetStage("native_replay_load_run");
        RunState state = RunState.FromSerializable(save);
        await RunManager.Instance.SetUpSavedSingleplayer(state, save);
        await PreloadManager.LoadRunAssets(state.Players.Select(player => player.Character));
        await PreloadManager.LoadActAssets(state.Act);
        RunManager.Instance.Launch();
        _host.RootSceneContainer.SetCurrentScene(NRun.Create(state));
        await RunManager.Instance.GenerateMap();
        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(origin["nextActionId"]!.GetValue<uint>());
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(origin["nextHookId"]!.GetValue<uint>());
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(origin["choiceIds"]!.Deserialize<List<uint>>()!);
        RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(origin["rewardIds"]!.Deserialize<List<int>>()!);

        Player player = state.Players.Single();
        EncounterModel encounter = ResolveUnique(ModelDb.All.OfType<EncounterModel>(), _request.EncounterId, "遭遇");
        using NativeReplayDriver driver = new(this, events, target, player);
        bool openingVerified = false;
        bool endingVerified = false;
        CombatReplayRecording.TestCombatStartObserver = combat => driver.ObserveBoundary(() =>
        {
            if (combatStart)
            {
                AssertRecordedContinuation(expectedState, combat, 0, _request.NativeStatePath);
                openingVerified = true;
                if (_request.ReplayMode is "SearchOnly" or "DeploySolver")
                {
                    driver.Dispose();
                    _writer.ReplayVerification!["openingChoiceAuthority"] = "solver";
                }
            }
        });
        CombatReplayRecording.TestCombatEndObserver = combat => driver.ObserveBoundary(() =>
        {
            if (combatEnd)
            {
                AssertRecordedContinuation(expectedState, combat, target, _request.NativeStatePath);
                endingVerified = true;
                _writer.ReplayVerification!["recordedOutcome"] = JsonSerializer.SerializeToNode(
                    CombatBugReportExporter.CaptureOutcome(combat) with { CombatEnded = true }, UnattendedTestFiles.JsonOptions);
            }
        });
        SetStage("native_replay_enter_combat");
        Task<AbstractRoom> entering = RunManager.Instance.EnterRoomDebug(
            encounter.RoomType, MapPointType.Unassigned, encounter.ToMutable(), showTransition: false);
        await driver.AdvanceAsync(entering);
        CombatState combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("native_replay_missing_combat_state");
        if (combatStart && !openingVerified)
            throw new InvalidDataException("native_replay_missing_combat_start_boundary");
        if (combatEnd && !endingVerified)
            throw new InvalidDataException("native_replay_missing_combat_end_boundary");
        if (!combatStart && !combatEnd)
            AssertRecordedContinuation(expectedState, combatState, target, _request.NativeStatePath);
        if (readyCheckpoint != null)
        {
            JsonObject readyMetadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
                _checkpointImportDirectory!, readyCheckpoint["metadataPath"]!.GetValue<string>())))!.AsObject();
            AssertRecordedContinuation(readyMetadata["exactContinuationState"]!.GetValue<string>(), combatState, target,
                Path.Combine(_checkpointImportDirectory!, readyCheckpoint["nativeStatePath"]!.GetValue<string>()));
            _writer.ReplayVerification!["readyCheckpointVerified"] = true;
        }
        if (!combatEnd && !combatStart && player.PlayerCombatState?.Phase.ToString() != "Play")
            throw new InvalidDataException("checkpoint_is_not_searchable");
        _writer.ReplayVerification!["replayedEvents"] = driver.Cursor;
        _writer.ReplayVerification["eventKinds"] = JsonSerializer.SerializeToNode(events.Take(target)
            .GroupBy(item => item.Kind ?? "legacy_unspecified").ToDictionary(group => group.Key, group => group.Count()));
        _writer.ReplayVerification["comparisonScope"] = "checkpoint";
        RecordCheckpointRestored();
        if (_request.ReplayMode == "ReplayRecorded")
        {
            bool nativeEncodingComparable = _writer.ReplayVerification["nativeStateVerification"] == null;
            _writer.ReplayVerification["status"] = nativeEncodingComparable
                ? combatEnd ? "recorded_completed" : "recorded_prefix_verified"
                : "recorded_continuation_only";
            _writer.ReplayVerification["comparisonScope"] = combatEnd ? "full_combat" : "checkpoint";
        }
        return new ScenarioContext(player.Character, encounter, combatState, player,
            player.PlayerCombatState?.TurnNumber ?? 0, [], [], []);
    }

    private void AssertRecordedContinuation(string expected, CombatState state, long cursor, string? nativePath)
    {
        string actual = ContinuationStamp.CaptureLive(state).StateText;
        if (ReplayContinuationMatches(expected, actual))
        {
            bool differentEncoding = _writer.ReplayVerification!["modelSerializationComparison"] != null;
            bool nativeVerified = AssertNativeCheckpoint(state, nativePath, differentEncoding);
            _writer.ReplayVerification["continuationVerified"] = true;
            _writer.ReplayVerification["nativeStateVerified"] = nativeVerified;
            if (!nativeVerified && differentEncoding && !string.IsNullOrWhiteSpace(nativePath))
            {
                _writer.ReplayVerification["nativeStateVerification"] = new JsonObject
                {
                    ["status"] = "not_comparable",
                    ["reason"] = "legacy_model_id_mapping_not_recorded",
                };
            }
            return;
        }
        _writer.ReplayVerification!["status"] = "restore_mismatch";
        _writer.ReplayVerification["firstDifference"] = new JsonObject
        {
            ["eventCursor"] = cursor, ["field"] = "continuation",
            ["detail"] = new ContinuationStamp(expected).DescribeFirstDifference(new ContinuationStamp(actual)),
            ["expected"] = expected, ["actual"] = actual,
        };
        throw new InvalidDataException("native_replay_continuation_mismatch:" +
            new ContinuationStamp(expected).DescribeFirstDifference(new ContinuationStamp(actual)));
    }

    private sealed class NativeReplayDriver : ICardSelector, IDisposable
    {
        private readonly UnattendedTestRunner _runner;
        private readonly RecordedCombatEvent[] _events;
        private readonly int _target;
        private readonly Player _player;
        private readonly IDisposable _selector;
        private Exception? _failure;
        private bool _disposed;
        private bool _openingTakeoverRequested;
        public int Cursor { get; private set; }

        public NativeReplayDriver(UnattendedTestRunner runner, RecordedCombatEvent[] events, int target, Player player)
        {
            _runner = runner;
            _events = events;
            _target = target;
            _player = player;
            CombatReplayRecording.TestObserver = Observe;
            _selector = CardSelectCmd.PushSelector(this, localOnly: true);
        }

        private void Observe(RecordedCombatEvent actual)
        {
            if (_failure != null)
                return;
            if (Cursor >= _target || !_events[Cursor].Payload.AsSpan().SequenceEqual(actual.Payload)
                || _events[Cursor].ChoiceContext != null && JsonSerializer.Serialize(_events[Cursor].ChoiceContext) != JsonSerializer.Serialize(actual.ChoiceContext))
            {
                _runner._writer.ReplayVerification!["status"] = "recorded_action_mismatch";
                _runner._writer.ReplayVerification["firstDifference"] = new JsonObject
                {
                    ["eventCursor"] = Cursor, ["field"] = "native_event",
                    ["expected"] = Cursor < _target ? Convert.ToBase64String(_events[Cursor].Payload) : "end_of_recording",
                    ["actual"] = Convert.ToBase64String(actual.Payload),
                    ["expectedDescription"] = Cursor < _target ? _events[Cursor].Description : "end_of_recording",
                    ["actualDescription"] = actual.Description,
                    ["expectedChoice"] = Cursor < _target ? JsonSerializer.SerializeToNode(_events[Cursor].ChoiceContext) : null,
                    ["actualChoice"] = JsonSerializer.SerializeToNode(actual.ChoiceContext),
                    ["actionWindow"] = JsonSerializer.SerializeToNode(_events.Skip(Math.Max(0, Cursor - 3)).Take(7)),
                };
                _failure = new InvalidDataException($"recorded_action_mismatch:{Cursor}");
                return;
            }
            Cursor++;
        }

        public void ObserveBoundary(Action verify)
        {
            try { verify(); }
            catch (Exception error)
            {
                // Lifecycle subscribers isolate exceptions. Forward the failure to the
                // awaited replay driver so the original mismatch terminates this request.
                _failure ??= error;
            }
        }

        public async Task AdvanceAsync(Task entering)
        {
            _runner.SetStage("native_replay_events");
            long lastProgress = _runner._stopwatch.ElapsedMilliseconds;
            string progressKey = string.Empty;
            while (!entering.IsCompleted || Cursor < _target || RunManager.Instance.ActionExecutor.IsRunning
                   || (CombatManager.Instance.IsInProgress && _player.PlayerCombatState?.Phase.ToString() != "Play"))
            {
                _runner.EnsureWithinDeadline();
                if (_failure != null)
                    throw _failure;
                if (entering.IsFaulted)
                    await entering;
                if (_disposed && !_openingTakeoverRequested && _player.Creature.CombatState is CombatState opening)
                    _openingTakeoverRequested = PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                        _runner._host, opening, deployAfterSetup: false);
                string currentProgress = $"{Cursor}:{entering.IsCompleted}:{_player.PlayerCombatState?.Phase}:{RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id}";
                if (currentProgress != progressKey)
                {
                    progressKey = currentProgress;
                    lastProgress = _runner._stopwatch.ElapsedMilliseconds;
                }
                else if (!_disposed && _runner._stopwatch.ElapsedMilliseconds - lastProgress > 5000)
                {
                    _runner._writer.ReplayVerification!["status"] = "recorded_input_stalled";
                    _runner._writer.ReplayVerification["stalledAt"] = progressKey;
                    throw new InvalidDataException($"recorded_input_stalled:{progressKey}");
                }
                if (Cursor < _target)
                {
                    RecordedCombatEvent next = _events[Cursor];
                    CombatReplayEvent value = Decode(next);
                    if (value.eventType == CombatReplayEventType.GameAction && next.Origin != "system"
                        && RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id == next.DuringActionId
                        && _player.PlayerCombatState?.Phase.ToString() == "Play")
                    {
                        GameAction action = value.action!.ToGameAction(_player);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                    }
                }
                await _runner.NextFrameAsync();
            }
            await entering;
            if (_failure != null)
                throw _failure;
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            if (Cursor >= _target)
                throw new InvalidDataException("recorded_choice_missing");
            CombatReplayEvent value = Decode(_events[Cursor]);
            if (value.eventType != CombatReplayEventType.PlayerChoice || value.playerId != _player.NetId)
                throw new InvalidDataException($"recorded_choice_order_mismatch:{Cursor}");
            NetPlayerChoiceResult net = value.playerChoiceResult!.Value;
            PlayerChoiceResult choice = PlayerChoiceResult.FromNetData(_player, _player.Creature.CombatState!.RunState, net);
            CardModel[] candidates = options.ToArray();
            CardModel[] selected = net.type == PlayerChoiceType.Index
                ? choice.AsIndexes().Where(index => index >= 0).Select(index =>
                    (uint)index < (uint)candidates.Length ? candidates[index]
                        : throw new InvalidDataException("recorded_choice_index_out_of_range")).ToArray()
                : choice.AsCards(net.type).Select(card => candidates.Single(candidate =>
                    ReferenceEquals(card, candidate)
                    || (net.type == PlayerChoiceType.CanonicalCard && card.Id == candidate.Id))).ToArray();
            if (selected.Length < minSelect || selected.Length > maxSelect || selected.Distinct().Count() != selected.Length)
                throw new InvalidDataException("recorded_choice_count_mismatch");
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(_player, value.choiceId!.Value, choice);
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new InvalidDataException("combat_recording_contains_post_combat_reward");

        private static CombatReplayEvent Decode(RecordedCombatEvent record)
        {
            PacketReader reader = new();
            reader.Reset(record.Payload);
            return reader.Read<CombatReplayEvent>();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _selector.Dispose();
            CombatReplayRecording.TestObserver = null;
            CombatReplayRecording.TestCombatStartObserver = null;
            CombatReplayRecording.TestCombatEndObserver = null;
        }
    }
}
