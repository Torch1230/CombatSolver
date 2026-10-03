using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private B015OriginalPrefixContext? _b015OriginalPrefix;
    private static Action<Player>? _b015ObserveSetup;

    // Observe the same pre-energy-reset boundary used by PlayerTurnSetupCoordinator.
    // TestObserver keeps the production interception in native replay mode.
    private static void ObserveB015OriginalSetupPrefix(Player __1) => _b015ObserveSetup?.Invoke(__1);

    private sealed class B015OriginalPrefixContext(
        CombatBeamSolver driver, SimulationSnapshot setup, Creature enemy,
        PlanAction[] actions, JsonObject candidate, B015OpeningSelector selector,
        IDisposable selectorScope, List<RecordedCombatEvent> events) : IDisposable
    {
        public CombatBeamSolver Driver { get; } = driver;
        public SimulationSnapshot Setup { get; } = setup;
        public Creature Enemy { get; } = enemy;
        public PlanAction[] Actions { get; } = actions;
        public JsonObject Candidate { get; } = candidate;
        public B015OpeningSelector Selector { get; } = selector;
        public List<RecordedCombatEvent> Events { get; } = events;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception> errors = [];
            try { selectorScope.Dispose(); }
            catch (Exception error) { errors.Add(error); }
            try { Setup.ReleaseSimulator(); }
            catch (Exception error) { errors.Add(error); }
            finally { CombatReplayRecording.TestObserver = null; }
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("B015 prefix cleanup failed.", errors);
        }
    }

    private void ReleaseB015OriginalPrefix()
    {
        B015OriginalPrefixContext? context = _b015OriginalPrefix;
        _b015OriginalPrefix = null;
        context?.Dispose();
    }

    private static JsonObject ReadB015OriginalFailedCandidate(string archivePath)
    {
        const string entryPath = "diagnostics/logs/combat/000.jsonl";
        const int maxJournalBytes = 1024 * 1024;
        const string marker = "[CombatSolver/Evidence] FAILED_CANDIDATE ";
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry[] matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, entryPath, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].Length <= 0 || matches[0].Length > maxJournalBytes)
            throw new InvalidDataException("B015 requires exactly one bounded original combat journal ZIP entry.");
        using Stream entryStream = matches[0].Open();
        using MemoryStream boundedJournal = new();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = entryStream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (boundedJournal.Length + count > maxJournalBytes)
                throw new InvalidDataException("B015 original combat journal exceeds the byte limit.");
            boundedJournal.Write(buffer, 0, count);
        }
        if (boundedJournal.Length != matches[0].Length)
            throw new InvalidDataException("B015 original combat journal ZIP length differs from its content.");
        boundedJournal.Position = 0;
        using StreamReader reader = new(boundedJournal, new UTF8Encoding(false, true));
        JsonObject? candidate = null;
        while (reader.ReadLine() is string line)
        {
            string message = JsonNode.Parse(line)!.AsObject()["Message"]!.GetValue<string>();
            if (!message.StartsWith(marker, StringComparison.Ordinal)) continue;
            if (candidate != null)
                throw new InvalidDataException("B015 original combat journal contains multiple failed candidates.");
            candidate = JsonNode.Parse(message[marker.Length..])!.AsObject();
        }
        return candidate ?? throw new InvalidDataException("B015 original combat journal has no failed candidate.");
    }

    private async Task<ScenarioContext> BuildB015OriginalPrefixScenarioAsync()
    {
        SetStage("b015_t016_original_start");
        await _host.GameStartupComplete;
        RecordCheckpointModDifferencesAfterStartup();
        ApplyHeadlessFastModeOverride();
        EnsureWithinDeadline();
        if (RunManager.Instance.IsInProgress || _b015OriginalPrefix != null || _b015ObserveSetup != null
            || CombatReplayRecording.TestObserver != null)
            throw new InvalidOperationException("B015 original prefix requires an idle isolated process.");
        JsonObject import = _checkpointImport ?? throw new InvalidDataException("B015 requires the original report archive.");
        JsonObject checkpoint = import["checkpoint"]!.AsObject();
        if (checkpoint["label"]!.GetValue<string>() != "combat_start"
            || checkpoint["eventCursor"]!.GetValue<long>() != 0)
            throw new InvalidDataException("B015 prefix requires the original combat_start checkpoint at cursor zero.");
        string metadataPath = import["paths"]!["metadataPath"]!.GetValue<string>();
        JsonObject metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
        string expectedState = metadata["exactContinuationState"]!.GetValue<string>();
        // Checkpoint import deliberately extracts replay materials only. Read the
        // one diagnostic source directly from the original archive, without extraction.
        JsonObject candidate = ReadB015OriginalFailedCandidate(_request.CheckpointArchivePath
            ?? throw new InvalidDataException("B015 requires the original report archive path."));
        PlanCardChoice[] choices = candidate["turnSetupChoices"]!.Deserialize<PlanCardChoice[]>(UnattendedTestFiles.JsonOptions)!;
        PlanAction[] actions = [.. candidate["prefix"]!.Deserialize<PlanAction[]>(UnattendedTestFiles.JsonOptions)!,
            candidate["attemptedAction"]!.Deserialize<PlanAction>(UnattendedTestFiles.JsonOptions)!];
        if (candidate["Turn"]!.GetValue<int>() != 1 || candidate["ActionCount"]!.GetValue<int>() != 5
            || !actions.Select(action => action.CardId).SequenceEqual(new[] { "AFTERIMAGE", "STRANGLE", "UP_MY_SLEEVE", "SIDESTEP", "SHIV", "SHIV" })
            || !actions.Select(action => action.TargetCombatId).SequenceEqual(new uint?[] { null, 1, null, null, 1, 2 })
            || actions.Any(action => action.Kind != PlanActionKind.PlayCard || action.Turn != 1
                || action.CardStateKey.Length == 0 || action.GetActionChoicesInExecutionOrder().Count != 0)
            || choices.Length != 1 || choices[0].SourceId != "GAMBLING_CHIP"
            || choices[0].ContextId != "AFTER_PLAYER_TURN_START"
            || choices[0].Timing != PlanChoiceTiming.PlayerTurnStart
            || choices[0].Effect != PlanChoiceEffect.DiscardAndDraw || choices[0].SourcePile != PileType.Hand
            || !choices[0].Cards.Select(card => card.CardId).SequenceEqual(new[] { "SHADOWMELD", "DEFEND_SILENT", "DEFEND_SILENT" }))
            throw new InvalidDataException("B015 report does not contain the expected single exact candidate and opening choice.");
        JsonObject recording = import["index"]!["recording"]!.AsObject();
        string RootPath(string key) => Path.Combine(_checkpointImportDirectory!, recording[key]!.GetValue<string>());
        RecordedCombatEvent[] recordedEvents = File.ReadLines(RootPath("eventsPath"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<RecordedCombatEvent>(line)!).ToArray();
        if (recordedEvents.Length != 1 || recordedEvents[0].Kind != "HookAction" || recordedEvents[0].Sequence != 0)
            throw new InvalidDataException("B015 event inventory changed; review the fixture's choice authority.");
        JsonObject origin = JsonNode.Parse(await File.ReadAllTextAsync(RootPath("originPath")))!.AsObject();
        if (origin["modelIdHash"]!.GetValue<uint>() != ModelIdSerializationCache.Hash)
            _writer.ReplayVerification!["modelSerializationComparison"] = new JsonObject
            {
                ["field"] = "serialization.modelIdHash", ["expected"] = origin["modelIdHash"]!.DeepClone(),
                ["actual"] = ModelIdSerializationCache.Hash,
            };
        SerializableRun save = JsonSerializer.Deserialize(await File.ReadAllTextAsync(RootPath("runSavePath")),
            JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
        if (save.Players.Count != 1) throw new InvalidDataException("B015 requires a single-player original run.");
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
        B015OpeningSelector selector = new(player, choices[0]);
        IDisposable selectorScope = CardSelectCmd.PushSelector(selector, localOnly: true);
        List<RecordedCombatEvent> observedEvents = [];
        CombatReplayRecording.TestObserver = observedEvents.Add;
        bool openingVerified = false;
        ExceptionDispatchInfo? boundaryFailure = null;
        CombatBeamSolver? driver = null;
        SimulationSnapshot? setup = null;
        Creature? enemy = null;
        MethodInfo nativeSetup = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "SetupPlayerTurn" && method.GetParameters().Length == 3);
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveB015OriginalSetupPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony patch = new("CombatSolver.Testing.B015OriginalPrefix." + _request.RunId);
        try
        {
            CombatReplayRecording.TestCombatStartObserver = combat =>
            {
                try
                {
                    RestoreReplayOutOfCombatRngFromSnapshot((RunState)combat.RunState, _request.RunSnapshotPath!);
                    RestoreReplayInventoryFromPath(player, _request.ReplayStatePath);
                    AssertRecordedContinuation(expectedState, combat, 0, _request.NativeStatePath,
                        _request.ReplayStatePath, allowLegacyBattleStart: true);
                    if (_writer.ReplayVerification!["nativeStateVerified"]?.GetValue<bool>() != true)
                        throw new InvalidDataException("B015 prefix requires native checkpoint verification, not continuation only.");
                    openingVerified = true;
                }
                catch (Exception error) { boundaryFailure ??= ExceptionDispatchInfo.Capture(error); throw; }
            };
            _b015ObserveSetup = observedPlayer =>
            {
                try
                {
                    if (!ReferenceEquals(observedPlayer, player) || !openingVerified || driver != null)
                        throw new InvalidOperationException("B015 setup boundary was missing, repeated or preceded strict opening verification.");
                    CombatState combat = (CombatState)player.Creature.CombatState!;
                    enemy = combat.Enemies.Single();
                    SolverSettingsSnapshot settings = SolverSettings.Capture();
                    CombatRootSnapshot root = CombatRootSnapshot.Capture(combat, settings.PredictPotionReward);
                    driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                        SolverController.CaptureSearchPolicy(settings, combat, true, SolverController.ResolveTheftPolicy(combat)));

                }
                catch (Exception error) { boundaryFailure ??= ExceptionDispatchInfo.Capture(error); throw; }
            };
            patch.Patch(nativeSetup, prefix: new HarmonyMethod(prefix));
            Task<AbstractRoom> entering = RunManager.Instance.EnterRoomDebug(encounter.RoomType, MapPointType.Unassigned,
                encounter.ToMutable(), showTransition: false);
            while (!entering.IsCompleted || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            {
                EnsureWithinDeadline();
                boundaryFailure?.Throw();
                selector.ThrowIfFailed();
                if (entering.IsFaulted) await entering;
                await NextFrameAsync();
            }
            await entering;
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            boundaryFailure?.Throw();
            selector.AssertConsumed();
            if (!openingVerified || driver == null || enemy == null)
                throw new InvalidOperationException("B015 original opening did not reach all observed boundaries.");
            using (SimulationNotificationIsolation.Enter())
                setup = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "ReplayTurnSetup", [new[] { selector.CurrentChoice! }, false])!;
            if (setup.HasRisk) throw new InvalidOperationException("B015 planned opening encountered a prediction risk.");
            CombatState actual = CombatManager.Instance.DebugOnlyGetState()!;
            _b015OriginalPrefix = new(driver, setup, enemy, actions, candidate, selector, selectorScope, observedEvents);
            RecordCheckpointRestored();
            _writer.ReplayVerification!["comparisonScope"] = "original_combat_start_and_failure_log_prefix";
            _writer.ReplayVerification["historySource"] = "original_native_root_plus_failed_candidate";
            _writer.ReplayVerification["openingChoiceAuthority"] = "failed_candidate_turnSetupChoices";
            _writer.ReplayVerification["recordedEventCount"] = recordedEvents.Length;
            _writer.ReplayVerification["recordedPlayerChoiceCount"] = 0;
            return new ScenarioContext(player.Character, encounter, actual, player, player.PlayerCombatState!.TurnNumber, [], [], []);
        }
        catch
        {
            selectorScope.Dispose();
            setup?.ReleaseSimulator();
            CombatReplayRecording.TestObserver = null;
            throw;
        }
        finally
        {
            patch.Unpatch(nativeSetup, prefix);
            _b015ObserveSetup = null;
            CombatReplayRecording.TestCombatStartObserver = null;
        }
    }

    private async Task AssertB015OriginalPrefixAsync(CombatState combat, Player player)
    {
        B015OriginalPrefixContext context = _b015OriginalPrefix
            ?? throw new InvalidOperationException("B015 original prefix was not prepared by its builder.");
        string evidence = _request.EvidenceDirectory ?? throw new InvalidOperationException("B015 prefix requires evidence output.");
        bool adjustedRoute = _request.ScenarioId == "B015-T016-AFTERIMAGE-ROUTE";
        if (adjustedRoute)
            (context.Actions[0], context.Actions[1]) = (context.Actions[1], context.Actions[0]);
        JsonArray stages = [];
        JsonArray keyMappings = [];
        SimulationSnapshot parent = context.Setup;
        SearchNode candidateParent = new(null, 0, parent.PotionUseCount, parent.PotionStrategicCost,
            parent.Turn, SearchRouteTraits.None, 0, parent.Score, parent.StateKey, parent.HasRisk,
            parent.BoundaryReason, false, null, parent, CombatProgressState.Capture(parent),
            TurnSetupChoices: new[] { context.Selector.CurrentChoice! });
        List<Exception> failures = [];
        try
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "t016-source-failed-candidate.json"), context.Candidate.ToJsonString(UnattendedTestFiles.JsonOptions));
            AssertSnapshotEqual(CaptureSimulated(parent.Simulator, (SimulatedCombatState)parent.Simulator.State.CombatState,
                player, context.Enemy), CaptureActual(combat, player, context.Enemy), "B015T016Original", "NativeOpening");
            _completedChecks.Add("B015T016Original:StrictNativeRoot:LoggedGamblingChipChoice:NativeOpeningFullState");
            for (int index = 0; index < context.Actions.Length; index++)
            {
                EnsureWithinDeadline();
                PlanAction recordedAction = context.Actions[index];
                CardModel card = PileType.Hand.GetPile(player).Cards
                    .Where(candidate => B015LegacyCardKey(candidate) == recordedAction.CardStateKey)
                    .Skip(recordedAction.CardStateOccurrence).FirstOrDefault()
                    ?? throw new InvalidOperationException("B015 native prefix is missing the exact recorded legacy card instance.");
                if (card.Id.Entry != recordedAction.CardId || card.CurrentUpgradeLevel != recordedAction.CardUpgradeLevel)
                    throw new InvalidOperationException("B015 native card identity differs from the failed candidate.");
                string currentKey = CardChoiceSupport.ChoiceCardKey(card);
                int currentOccurrence = PileType.Hand.GetPile(player).Cards.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                    .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == currentKey);
                PlanAction action = recordedAction with { CardStateKey = currentKey, CardStateOccurrence = currentOccurrence };
                if (adjustedRoute)
                {
                    object[] prepared = ((System.Collections.IEnumerable)InvokeForcedTerminalMethod(context.Driver,
                        "PrepareCardActions", [candidateParent, true])!).Cast<object>().ToArray();
                    action = prepared.Select(item => (PlanAction)item.GetType().GetProperty("Action")!.GetValue(item)!)
                        .Single(item => item.CardId == action.CardId && item.CardStateKey == currentKey
                            && item.CardStateOccurrence == currentOccurrence);
                }
                context.Actions[index] = action;
                keyMappings.Add(new JsonObject { ["actionIndex"] = index, ["legacyKey"] = recordedAction.CardStateKey,
                    ["currentKey"] = currentKey, ["legacyOccurrence"] = recordedAction.CardStateOccurrence,
                    ["currentOccurrence"] = currentOccurrence });
                SetStage($"b015_t016_original_action_{index + 1}_{action.CardId}_target_{action.TargetCombatId}");
                if (index == 5 && !adjustedRoute)
                {
                    JsonObject expectedParent = context.Candidate;
                    if (parent.PlayerHp != expectedParent["PlayerHp"]!.GetValue<int>()
                        || parent.PlayerBlock != expectedParent["PlayerBlock"]!.GetValue<int>()
                        || parent.Energy != expectedParent["Energy"]!.GetValue<int>()
                        || parent.Stars != expectedParent["Stars"]!.GetValue<int>()
                        || parent.HandCount != expectedParent["HandCount"]!.GetValue<int>()
                        || parent.EnemyHp != expectedParent["EnemyHp"]!.GetValue<int>())
                        throw new InvalidOperationException("B015 prefix did not reconstruct the six recorded parent scalars.");
                    _completedChecks.Add("B015T016Original:RecordedFailedParentScalars");
                    await ObserveB015ProductionSiblingsAsync(context, candidateParent, combat, player, evidence);
                    await ObserveB015ParallelOwnershipAsync(context, candidateParent, combat, player, evidence);
                    // Retain the original illegal action as a separate negative replay.
                    // The sibling probe does not rewrite target 2 into a valid target.
                }
                SimulationSnapshot? next = null;
                SimulationSnapshot? direct = null;
                try
                {
                    MoveStateSnapshot predicted;
                    using (SimulationNotificationIsolation.Enter())
                    {
                        next = (SimulationSnapshot)InvokeForcedTerminalMethod(context.Driver, "Replay",
                            [new[] { action }, parent, action.Turn, index, null, null, null, null, null, null, null, null, true, false, null])!;
                        PlanAction[] full = context.Actions.Take(index + 1).ToArray();
                        direct = (SimulationSnapshot)InvokeForcedTerminalMethod(context.Driver, "Replay",
                            [full, context.Setup, action.Turn, 0, null, null, null, null, null, null, null, null, true, false, null])!;
                        _ = InvokeForcedTerminalMethod(context.Driver, "AssertIncrementalEquivalent", [action, full, next, direct]);
                        if (next.HasRisk || direct.HasRisk) throw new InvalidOperationException("B015 exact prefix encountered a prediction risk.");
                        predicted = CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, context.Enemy);
                    }
                    Creature? target = action.TargetCombatId is uint id
                        ? combat.Enemies.SingleOrDefault(creature => creature.CombatId == id)
                            ?? throw new InvalidOperationException($"B015 native target {id} is absent before manual play.")
                        : null;
                    using CancellationTokenSource actionDeadline = new(TimeSpan.FromSeconds(15));
                    GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
                        queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
                        () => { if (!card.TryManualPlay(target)) throw new InvalidOperationException("B015 native exact manual play was refused."); },
                        actionDeadline.Token);
                    await native.CompletionTask.WaitAsync(actionDeadline.Token);
                    if (native.Exception != null) ExceptionDispatchInfo.Capture(native.Exception).Throw();
                    context.Selector.AssertConsumed();
                    MoveStateSnapshot actual = CaptureActual(combat, player, context.Enemy);
                    stages.Add(new JsonObject { ["actionIndex"] = index, ["action"] = JsonSerializer.SerializeToNode(action, UnattendedTestFiles.JsonOptions),
                        ["predicted"] = JsonSerializer.SerializeToNode(predicted, UnattendedTestFiles.JsonOptions),
                        ["actual"] = JsonSerializer.SerializeToNode(actual, UnattendedTestFiles.JsonOptions) });
                    AssertSnapshotEqual(predicted, actual, "B015T016Original", $"NativePrefix{index + 1}");
                    _completedChecks.Add($"B015T016Original:Prefix{index + 1}:FullIncrementalNativeStateAndRng");
                    candidateParent = (SearchNode)InvokeForcedTerminalMethod(context.Driver,
                        "CreatePlannedCardChild", [candidateParent, action, next])!;
                    if (!ReferenceEquals(parent, context.Setup)) parent.ReleaseSimulator();
                    parent = next;
                    next = null;
                }
                finally { next?.ReleaseSimulator(); direct?.ReleaseSimulator(); }
            }
            if (adjustedRoute)
            {
                if (context.Actions[4].TargetCombatId != 1 || context.Actions[5].TargetCombatId != 2)
                    throw new InvalidOperationException("B015 baseline production actions did not cross the expected Stock target boundary.");
                _completedChecks.Add("B015T016Afterimage:ProductionGeneratedSixActions:NativeFullStateAndRng");
                await VerifyB015AdjustedRouteAsync(context, candidateParent, combat, player, evidence);
            }
            _writer.ReplayVerification!["failedCandidatePrefixVerified"] = !adjustedRoute;
            _writer.ReplayVerification["prefixActionCount"] = context.Actions.Length;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "t016-card-key-mappings.json"), new JsonObject
                {
                    ["conversion"] = "Verify every legacy field; append only the current native CardCostStateSupport suffix and recompute full-key occurrences.",
                    ["openingChoice"] = JsonSerializer.SerializeToNode(context.Selector.CurrentChoice, UnattendedTestFiles.JsonOptions),
                    ["actions"] = keyMappings,
                }.ToJsonString(UnattendedTestFiles.JsonOptions));
                await File.WriteAllTextAsync(Path.Combine(evidence, "t016-prefix-states.json"), stages.ToJsonString(UnattendedTestFiles.JsonOptions));
                await File.WriteAllTextAsync(Path.Combine(evidence, "t016-authored-native-events.json"), JsonSerializer.Serialize(context.Events, UnattendedTestFiles.JsonOptions));
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                try
                {
                    if (!ReferenceEquals(parent, context.Setup)) parent.ReleaseSimulator();
                }
                catch (Exception error) { failures.Add(error); }
                finally
                {
                    try { ReleaseB015OriginalPrefix(); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("B015 prefix failed; evidence or cleanup also failed.", failures);
    }

    private async Task ObserveB015ProductionSiblingsAsync(B015OriginalPrefixContext context,
        SearchNode parent, CombatState nativeCombat, Player player, string evidence)
    {
        JsonArray trace = [];
        List<Exception> failures = [];
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        MoveStateSnapshot before = CaptureSimulated(parent.Snapshot.Simulator,
            (SimulatedCombatState)parent.Snapshot.Simulator.State.CombatState, player, context.Enemy);
        object[] Prepare() => ((System.Collections.IEnumerable)InvokeForcedTerminalMethod(context.Driver,
            "PrepareCardActions", [parent, true])!).Cast<object>().ToArray();
        static PlanAction ActionOf(object prepared) => (PlanAction)prepared.GetType().GetProperty("Action")!.GetValue(prepared)!;
        void Observe(string stage, object[] candidates)
        {
            MoveStateSnapshot current = CaptureSimulated(parent.Snapshot.Simulator,
                (SimulatedCombatState)parent.Snapshot.Simulator.State.CombatState, player, context.Enemy);
            trace.Add(new JsonObject
            {
                ["stage"] = stage,
                ["parent"] = JsonSerializer.SerializeToNode(current, UnattendedTestFiles.JsonOptions),
                ["candidates"] = JsonSerializer.SerializeToNode(candidates.Select(ActionOf).ToArray(), UnattendedTestFiles.JsonOptions),
            });
            AssertSnapshotEqual(before, current, "B015T016Generation", stage + "ParentUnchanged");
            AssertSnapshotEqual(before, CaptureActual(nativeCombat, player, context.Enemy),
                "B015T016Generation", stage + "NativeUnchanged");
        }
        try
        {
            if (parent.ActionCount != 5 || !parent.Actions.SequenceEqual(context.Actions.Take(5)))
                throw new InvalidOperationException("B015 production generation requires the exact five-action parent chain.");
            object[] candidates = Prepare();
            Observe("initial_preparation", candidates);
            // Limit execution to two known no-choice attacks. Preparing the ordinary
            // list is production enumeration, not search or execution of other cards.
            foreach (string cardId in new[] { "BACKSTAB", "SHIV" })
            {
                EnsureWithinDeadline();
                object? prepared = candidates.FirstOrDefault(item => ActionOf(item).CardId == cardId);
                if (prepared == null)
                {
                    if (cardId == "SHIV") throw new InvalidOperationException("B015 production generator omitted the recorded available Shiv.");
                    trace.Add(new JsonObject { ["stage"] = "backstab_unavailable" });
                    continue;
                }
                PlanAction action = ActionOf(prepared);
                if (action.GetActionChoicesInExecutionOrder().Count != 0)
                    throw new InvalidOperationException("B015 sibling probe must not execute a planned-choice branch.");
                object? evaluation = null;
                IDisposable? batch = null;
                IDisposable? deferred = null;
                try
                {
                    evaluation = InvokeForcedTerminalMethod(context.Driver, "EvaluatePreparedCardAction",
                        [parent, prepared, null, new object(), false])!;
                    batch = evaluation.GetType().GetProperty("Batch")!.GetValue(evaluation) as IDisposable;
                    deferred = evaluation.GetType().GetProperty("DeferredProbe")!.GetValue(evaluation) as IDisposable;
                    if (deferred != null || batch == null)
                        throw new InvalidOperationException("B015 no-choice sibling unexpectedly deferred.");
                    object[] rawCards = ((System.Collections.IEnumerable)batch.GetType().GetProperty("Cards")!.GetValue(batch)!).Cast<object>().ToArray();
                    if (rawCards.Length != 1)
                        throw new InvalidOperationException("B015 no-choice sibling did not produce exactly one candidate.");
                    SearchNode child = (SearchNode)rawCards[0].GetType().GetProperty("Node")!.GetValue(rawCards[0])!;
                    SimulatedCombatState childState = (SimulatedCombatState)child.Snapshot.Simulator.State.CombatState;
                    trace.Add(new JsonObject
                    {
                        ["stage"] = "generated_sibling_" + cardId,
                        ["action"] = JsonSerializer.SerializeToNode(action, UnattendedTestFiles.JsonOptions),
                        ["child"] = JsonSerializer.SerializeToNode(CaptureSimulated(child.Snapshot.Simulator, childState,
                            player, context.Enemy), UnattendedTestFiles.JsonOptions),
                        ["activeEnemies"] = JsonSerializer.SerializeToNode(childState.Enemies.Select(enemy => new
                        {
                            enemy.CombatId, ModelId = enemy.Monster!.Id.Entry, Hp = child.Snapshot.Simulator.State.GetCreature(enemy).CurrentHp,
                        }).ToArray(), UnattendedTestFiles.JsonOptions),
                    });
                    if (child.Snapshot.HasRisk || child.Snapshot.BoundaryReason != SearchBoundaryReason.None)
                        throw new InvalidOperationException("B015 sibling execution encountered an unresolved prediction boundary.");
                    if (childState.GetCreature(2) == null || childState.Enemies.All(enemy => enemy.CombatId != 2))
                        throw new InvalidOperationException("B015 sibling attack did not exercise Stock replacement generation.");
                    Observe("after_sibling_" + cardId, Prepare());
                }
                finally
                {
                    try { deferred?.Dispose(); }
                    finally { batch?.Dispose(); }
                }
                candidates = Prepare();
                Observe("after_disposal_" + cardId, candidates);
            }
            if (candidates.Any(item => ActionOf(item).TargetCombatId is uint id && id != 1))
                throw new InvalidOperationException("B015 production generator exposed a child-only target in the unchanged parent.");
            trace.Add(new JsonObject { ["stage"] = "bounded_probe_complete",
                ["conclusion"] = "Two bounded production siblings did not reproduce generation of the historical illegal target; this is not a production fix." });
            _completedChecks.Add("B015T016Generation:ProductionPrepare:StockSiblings:ParentAndNativeUnchanged");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "t016-production-sibling-probe.json"), trace.ToJsonString(UnattendedTestFiles.JsonOptions));
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("B015 production sibling probe and evidence failed.", failures);
    }

    // Exact representation projection for report commit 1b910969. The only key schema
    // addition is CardCostStateSupport's appended native lifecycle suffix; all older
    // fields stay byte-equal. No state is removed from replay or native comparisons.
    private static string B015LegacyCardKey(CardModel card)
    {
        string current = CardChoiceSupport.ChoiceCardKey(card);
        StringBuilder suffix = new();
        CardCostStateSupport.Append(suffix, card);
        string added = suffix.ToString();
        if (!current.EndsWith(added, StringComparison.Ordinal))
            throw new InvalidOperationException("B015 card-key schema is no longer the audited append-only change.");
        return added.Length == 0 ? current : current[..^added.Length];
    }

    private sealed class B015OpeningSelector(Player player, PlanCardChoice choice) : ICardSelector
    {
        public PlanCardChoice? CurrentChoice { get; private set; }
        private int _consumed;
        private ExceptionDispatchInfo? _failure;
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            try
            {
                ThrowIfFailed();
                List<CardModel> available = options.ToList();
                IReadOnlyList<CardModel> source = PileType.Hand.GetPile(player).Cards;
                if (_consumed != 0 || player.PlayerCombatState?.Phase != PlayerTurnPhase.Start
                    || !player.Relics.Any(relic => relic.Id.Entry == "GAMBLING_CHIP")
                    || minSelect > choice.Cards.Count || maxSelect < choice.Cards.Count
                    || !available.SequenceEqual(source))
                    throw new InvalidOperationException("B015 unexpected native opening choice or candidate order.");
                // ICardSelector has no source/context arguments. The strictly restored
                // root, Start boundary and sole ordered callback establish that context.
                List<CardModel> selected = [];
                List<PlanCardToken> currentTokens = [];
                foreach (PlanCardToken token in choice.Cards)
                {
                    if (token.SourceOccurrence < 0 || token.OptionOccurrence < 0 || token.StateKey.Length == 0)
                        throw new InvalidDataException("B015 recorded choice lacks exact card identity.");
                    CardModel card = available.Where(candidate => B015LegacyCardKey(candidate) == token.StateKey)
                        .Skip(token.OptionOccurrence).FirstOrDefault()
                        ?? throw new InvalidOperationException("B015 native opening is missing a recorded choice instance.");
                    if (selected.Contains(card) || card.Id.Entry != token.CardId || card.CurrentUpgradeLevel != token.UpgradeLevel
                        || B015LegacyCardKey(card) != token.StateKey
                        || source.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => B015LegacyCardKey(candidate) == token.StateKey) != token.SourceOccurrence)
                        throw new InvalidOperationException("B015 recorded opening choice identity, state or occurrence differs.");
                    selected.Add(card);
                    string currentKey = CardChoiceSupport.ChoiceCardKey(card);
                    currentTokens.Add(token with { StateKey = currentKey,
                        SourceOccurrence = source.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == currentKey),
                        OptionOccurrence = available.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == currentKey) });
                }
                CurrentChoice = choice with { Cards = currentTokens };
                _consumed++;
                return Task.FromResult<IEnumerable<CardModel>>(selected);
            }
            catch (Exception error) { _failure = ExceptionDispatchInfo.Capture(error); throw; }
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            InvalidOperationException error = new("B015 exact prefix contains no card reward choice.");
            _failure = ExceptionDispatchInfo.Capture(error);
            throw error;
        }
        public void ThrowIfFailed() => _failure?.Throw();
        public void AssertConsumed()
        {
            ThrowIfFailed();
            if (_consumed != 1) throw new InvalidOperationException("B015 recorded opening choice was not consumed exactly once.");
        }
    }
}
