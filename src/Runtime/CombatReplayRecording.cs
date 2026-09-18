using System.Text.Json;
using System.Diagnostics;
using CombatSolver.Replay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed record RecordedCombatEvent(long Sequence, string Origin, byte[] Payload, uint? DuringActionId,
    string? Kind = null, string? Description = null, RecordedChoiceContext? ChoiceContext = null);
internal sealed record RecordedCardIdentity(int Index, string ModelId, uint? NativeId, int Upgrade, string State);
internal sealed record RecordedChoiceContext(string Surface, string Source, int Min, int Max, RecordedCardIdentity[] Options);
internal sealed record RecordedModIdentity(string Name, string? Version, Guid ModuleId);
internal sealed record RecordedCombatOrigin(
    byte[] RunSave, uint NextActionId, uint NextHookId, uint[] ChoiceIds,
    int[] RewardIds, string GameVersion, string GameCommit, uint ModelIdHash);
internal sealed record RecordedCombatArchive(
    RecordedCombatOrigin Origin, EventLogSnapshot Events, string? IncompleteReason,
    Dictionary<string, int> InputOrigins, double CaptureMilliseconds, double MaximumCaptureMilliseconds);

// Records the native input protocol. Combat histories are reconstructed by executing
// these inputs, not by interpreting reflection-based diagnostic field dumps.
internal sealed class CombatReplayRecording : IDisposable
{
    private static CombatReplayRecording? _pending;
    internal static CombatReplayRecording? Pending => _pending;
    internal static Action<RecordedCombatEvent>? TestObserver { get; set; }
    internal static Action<CombatState>? TestCombatStartObserver { get; set; }
    internal static Action<CombatState>? TestCombatEndObserver { get; set; }
    internal static Action<SolverResult>? TestSearchResultObserver { get; set; }
    private ActionQueueSet? _actions;
    private PlayerChoiceSynchronizer? _choices;
    private readonly AppendOnlyEventLog<RecordedCombatEvent> _events = new(item => JsonSerializer.SerializeToUtf8Bytes(item));
    private readonly Dictionary<string, int> _inputOrigins = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, RecordedChoiceContext> _choiceContexts = [];
    private readonly RecordedCombatOrigin _origin;
    private long _eventCursor;
    private long _captureTicks;
    private long _maximumCaptureTicks;
    private string? _incompleteReason;
    private bool _disposed;

    public long EventCursor => _eventCursor;
    public string? IncompleteReason => _incompleteReason ?? _events.Error;

    private CombatReplayRecording(SerializableRun run)
    {
        RunManager manager = RunManager.Instance;
        _actions = manager.ActionQueueSet;
        _choices = manager.PlayerChoiceSynchronizer;
        _origin = new RecordedCombatOrigin(
            JsonSerializer.SerializeToUtf8Bytes(run, JsonSerializationUtility.GetTypeInfo<SerializableRun>()),
            _actions.NextActionId, manager.ActionQueueSynchronizer.NextHookId,
            _choices.ChoiceIds.ToArray(), manager.RewardsSetSynchronizer.GetNextRewardIds().ToArray(),
            ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "UNRELEASED",
            ReleaseInfoManager.Instance.ReleaseInfo?.Commit ?? "UNKNOWN",
            ModelIdSerializationCache.Hash);
        _actions.ActionEnqueued += OnAction;
        _actions.ActionResumed += OnResume;
        _choices.PlayerChoiceReceived += OnChoice;
    }

    internal static void Start(SerializableRun run)
    {
        _pending?.Dispose();
        _pending?._events.Dispose();
        _pending = run.Players.Count == 1 ? new CombatReplayRecording(run) : null;
    }
    internal static RecordedModIdentity[] CaptureModIdentity() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => !assembly.IsDynamic && assembly != typeof(CombatReplayRecording).Assembly
            && (assembly.Location.Replace('\\', '/').Contains("/mods/", StringComparison.OrdinalIgnoreCase)
                || assembly.Location.Replace('\\', '/').Contains("/workshop/content/", StringComparison.OrdinalIgnoreCase)))
        .Select(assembly => new RecordedModIdentity(assembly.GetName().Name!, assembly.GetName().Version?.ToString(), assembly.ManifestModule.ModuleVersionId))
        .OrderBy(assembly => assembly.Name, StringComparer.Ordinal).ToArray();

    public void MarkIncomplete(string reason) => _incompleteReason ??= reason;

    internal static void ObserveChoiceCandidates(NativeChoiceSurfaceKind surface, Player player,
        IReadOnlyList<CardModel> cards, int minimum, int maximum, string source)
    {
        CombatReplayRecording? recording = _pending;
        if (recording == null || recording._disposed || recording.IncompleteReason != null
            || !CombatManager.Instance.IsInProgress || CardSelectCmd.Selector != null)
            return;
        if (cards.Count > 256) { recording.MarkIncomplete("choice_candidates_limit"); return; }
        if (recording._choiceContexts.Count >= 256) { recording.MarkIncomplete("pending_choice_context_limit"); return; }
        uint choiceId = recording._choices!.ChoiceIds.Count == 0 ? 0 : recording._choices.ChoiceIds[0];
        recording._choiceContexts[choiceId] = new RecordedChoiceContext(surface.ToString(), source, minimum, maximum,
            cards.Select((card, index) => new RecordedCardIdentity(index, card.Id.ToString(),
                NetCombatCardDb.Instance.TryGetCardId(card, out uint id) ? id : null,
                card.CurrentUpgradeLevel, CardChoiceSupport.ChoiceCardKey(card))).ToArray());
    }

    // Called on the main thread. Records and origin bytes are immutable after capture.
    public Task<RecordedCombatArchive> CaptureAsync()
    {
        Dictionary<string, int> origins = new(_inputOrigins, StringComparer.Ordinal);
        string? incomplete = IncompleteReason;
        double total = _captureTicks * 1000d / Stopwatch.Frequency;
        double maximum = _maximumCaptureTicks * 1000d / Stopwatch.Frequency;
        return Finish(_events.CaptureAsync());
        async Task<RecordedCombatArchive> Finish(Task<EventLogSnapshot> pending)
        {
            EventLogSnapshot snapshot = await pending.ConfigureAwait(false);
            return new RecordedCombatArchive(_origin, snapshot, incomplete ?? snapshot.Error, origins, total, maximum);
        }
    }

    private void OnAction(GameAction action)
    {
        if (!CombatManager.Instance.IsInProgress)
            return;
        if (action is GenericHookGameAction hook)
        {
            Record(new CombatReplayEvent
            {
                playerId = action.OwnerId, eventType = CombatReplayEventType.HookAction,
                hookId = hook.HookId, gameActionType = action.ActionType,
            }, "system", action.ToString());
        }
        else if (action.RecordableToReplay)
        {
            Record(new CombatReplayEvent
            {
                playerId = action.OwnerId, eventType = CombatReplayEventType.GameAction,
                action = action.ToNetAction(),
            }, action is ReadyToBeginEnemyTurnAction ? "system"
                : SolverController.IsDeploying ? "solver" : "player", action.ToString());
        }
        else
        {
            MarkIncomplete($"unrecordable_action:{action.GetType().FullName}");
        }
    }

    private void OnResume(uint actionId)
    {
        if (CombatManager.Instance.IsInProgress)
            Record(new CombatReplayEvent
            {
                eventType = CombatReplayEventType.ResumeAction, actionId = actionId,
            }, "system", $"Resume action {actionId}");
    }

    private void OnChoice(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        if (CombatManager.Instance.IsInProgress)
            Record(new CombatReplayEvent
            {
                eventType = CombatReplayEventType.PlayerChoice, playerId = player.NetId,
                choiceId = choiceId, playerChoiceResult = result,
            }, SolverController.IsDeploying || PlayerTurnSetupCoordinator.IsDrivingChoiceForRecording ? "solver" : "player",
                $"Choice {choiceId}: {result.type}; indexes={string.Join(',', result.indexes ?? [])}",
                _choiceContexts.Remove(choiceId, out RecordedChoiceContext? context) ? context : null);
    }

    private void Record(CombatReplayEvent value, string origin, string? description, RecordedChoiceContext? choiceContext = null)
    {
        long sequence = _eventCursor++;
        if (IncompleteReason != null)
            return;
        long started = Stopwatch.GetTimestamp();
        PacketWriter writer = new() { WarnOnGrow = false };
        value.Serialize(writer);
        writer.ZeroByteRemainder();
        byte[] payload = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
        RecordedCombatEvent captured = new(sequence, origin, payload,
            RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id, value.eventType.ToString(), description, choiceContext);
        _events.TryAppend(captured, checked(payload.Length + (description?.Length ?? 0) * 2
            + (choiceContext?.Options.Sum(option => option.State.Length * 2 + 128) ?? 0)));
        _inputOrigins[origin] = _inputOrigins.GetValueOrDefault(origin) + 1;
        TestObserver?.Invoke(captured);
        long elapsed = Stopwatch.GetTimestamp() - started;
        _captureTicks += elapsed;
        _maximumCaptureTicks = Math.Max(_maximumCaptureTicks, elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_actions != null)
        {
            _actions.ActionEnqueued -= OnAction;
            _actions.ActionResumed -= OnResume;
            _actions = null;
        }
        if (_choices != null)
        {
            _choices.PlayerChoiceReceived -= OnChoice;
            _choices = null;
        }
    }
}

internal sealed class CombatReplayRecordingPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_record_native_combat_inputs";
    public static string Description => "从原生战前存档记录单场动作与选择";
    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState), [typeof(SerializableRun)])];
    public static void Postfix(SerializableRun serializableRun)
        => CombatReplayRecording.Start(serializableRun);
}
