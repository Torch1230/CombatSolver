using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Net.Http.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed partial class RunStatistics : Node
{
    private sealed record Signal(string Kind, RunStatisticsRecord? Run = null, string Battle = "",
        bool Enabled = false, bool InCombat = false, bool Auto = false, HistoricalStatistics? History = null, string? NativeHistoryDirectory = null, int NativeBattles = 0);
    private static RunStatistics? _instance;
    internal static bool NewRunPrepared;
    private readonly Channel<Signal> _signals = Channel.CreateBounded<Signal>(256);
    private RunStatisticsRecord? _run;
    private volatile RunStatisticsSnapshot? _snapshot;
    private readonly object _transportGate = new();
    private readonly HashSet<string> _activities = new();
    private Task? _worker;
    private double _elapsed;
    private bool _disabled;
    private volatile bool _uploadEnabled;
    private CancellationTokenSource? _uploadCancellation;
    internal static RunStatisticsSnapshot? Snapshot => _instance?._run is { } run && _instance._snapshot?.ProfileId != run.ProfileId ? null : _instance?._snapshot;
    internal static void Start(NGame host)
    {
        if (_instance != null || OnlinePresence.IsHeadless() || UnattendedTestRunner.IsActive) return;
        _instance = new RunStatistics { Name = "CombatSolverRunStatistics" };
        host.AddChild(_instance);
        string directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CombatSolver", "run-statistics-v1");
        var instance = _instance;
        instance._worker = Task.Run(() => instance.ProcessAsync(directory));
    }
    private void Enqueue(Signal signal)
    {
        if (!_signals.Writer.TryWrite(signal)) throw new InvalidOperationException("Run statistics queue capacity exceeded.");
    }
    private static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    internal static void Launched(RunManager manager)
    {
        bool fromStart = NewRunPrepared;
        NewRunPrepared = false;
        if (_instance != null) { _instance._run = null; _instance._snapshot = null; }
        if (OnlinePresence.IsHeadless() || UnattendedTestRunner.IsActive || !manager.ShouldSave || manager.State == null
            || manager.NetService.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Singleplayer
            || manager.State.Players.Count != 1 || manager.State.GameMode != GameMode.Standard) return;
        Start(NGame.Instance!);
        var instance = _instance!;
        string profile = Key(OS.GetUserDataDir() + ":" + SaveManager.Instance.CurrentProfileId);
        string character = LocalContext.GetMe(manager.State)!.Character.Id.Entry;
        string id = Key(profile + ":" + manager._startTime + ":" + manager.State.Rng.StringSeed);
        var run = new RunStatisticsRecord(id, profile, manager._startTime * 1000, null, character,
            manager.State.AscensionLevel, CombatBugReportDescription.CurrentModVersion,
            "none", "pending", fromStart, !SolverController.SolverDisabled, false, [], [], [], []);
        instance._run = run;
        instance._activities.Clear();
        instance._disabled = SolverController.SolverDisabled;
        int nativeBattles = manager.State.MapPointHistory.SelectMany(points => points).Sum(point => point.Rooms.Count(room =>
            room.RoomType is MegaCrit.Sts2.Core.Rooms.RoomType.Monster or MegaCrit.Sts2.Core.Rooms.RoomType.Elite or MegaCrit.Sts2.Core.Rooms.RoomType.Boss));
        instance.Enqueue(new("resume", run, NativeHistoryDirectory: SaveManager.Instance.GetProfileScopedPath(Path.Combine(UserDataPathProvider.SavesDir, "history")), NativeBattles: nativeBattles));
        var characters = SaveManager.Instance.Progress.CharacterStats.Select(pair => new HistoricalCharacterStatistics(
            pair.Key.Entry, pair.Value.TotalWins, pair.Value.TotalLosses, pair.Value.CurrentWinStreak, pair.Value.BestWinStreak)).ToArray();
        instance.Enqueue(new("history", run, History: new(profile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), characters)));
    }
    internal static void Battle(ICombatState? state)
    {
        if (state == null || _instance?._run == null || OnlinePresence.IsHeadless() || UnattendedTestRunner.IsActive
            || state.Players.Count != 1 || !ReferenceEquals(state, CombatManager.Instance.DebugOnlyGetState())) return;
        _instance.Enqueue(new("battle", _instance._run, Battle: BattleKey(state), Enabled: !SolverController.SolverDisabled, InCombat: true));
    }
    private static string BattleKey(ICombatState state) => state.RunState.TotalFloor + ":" + state.Encounter?.Id.Entry;
    internal static void Activity(CombatState state, bool execution = false, bool auto = false)
    {
        if (_instance?._run == null || OnlinePresence.IsHeadless() || SolverController.IsMultiplayerSession) return;
        string battle = BattleKey(state);
        if (!_instance._activities.Add(battle + ":" + execution + ":" + auto)) return;
        _instance.Enqueue(new(execution ? "execute" : "solve", _instance._run, battle, true, true, auto));
    }
    internal static void Ended(RunHistory history)
    {
        if (_instance?._run == null || history.Players.Count != 1 || history.GameMode != GameMode.Standard) return;
        if (_instance._run.StartedAt != history.StartTime * 1000) return;
        var ended = _instance._run with { Outcome = history.WasAbandoned ? "abandoned" : history.Win ? "win" : "loss",
            EndedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        _instance.Enqueue(new("end", ended));
        _instance._run = null;
    }
    internal static void SettingsChanged()
    {
        if (_instance == null) return;
        _instance.SetUploading(SolverSettings.Current.OnlineStatisticsEnabled && !SolverController.IsMultiplayerSession && !UnattendedTestRunner.IsActive);
        _instance.ObserveSetting();
    }
    private void ObserveSetting()
    {
        bool disabled = SolverController.SolverDisabled;
        if (disabled == _disabled) return;
        _disabled = disabled;
        if (_run != null) Enqueue(new("setting", _run, Enabled: !disabled, InCombat: CombatManager.Instance.IsInProgress));
    }
    private void SetUploading(bool enabled)
    {
        if (_uploadEnabled == enabled) return;
        _uploadEnabled = enabled;
        if (!enabled) lock (_transportGate) _uploadCancellation?.Cancel();
    }
    public override void _Process(double delta)
    {
        if (_worker?.IsFaulted == true) { SetProcess(false); Entry.Logger.Error($"Run statistics stopped: {_worker.Exception}"); return; }
        ObserveSetting();
        SetUploading(SolverSettings.Current.OnlineStatisticsEnabled && !SolverController.IsMultiplayerSession && !UnattendedTestRunner.IsActive);
        _elapsed += delta;
        if (_elapsed < 30) return;
        _elapsed = 0;
        Enqueue(new("sync"));
    }
    private async Task ProcessAsync(string directory)
    {
        var store = new RunStatisticsStore(directory);
        using var client = OnlinePresence.CreateStatisticsClient();
        string? identity = null;
        string? profile = null;
        await foreach (var signal in _signals.Reader.ReadAllAsync())
        {
            if (signal.Kind == "sync")
            {
                if (!_uploadEnabled || client == null) continue;
                identity ??= OnlinePresence.LoadIdentity();
                CancellationToken token;
                lock (_transportGate) { _uploadCancellation?.Dispose(); _uploadCancellation = new(); token = _uploadCancellation.Token; }
                try
                {
                    foreach (var record in store.Pending(20))
                    {
                        if (!_uploadEnabled) break;
                        using var response = await client.PostAsJsonAsync("v1/runs", new { sessionId = identity, run = record }, RunStatisticsStore.Json, token);
                        response.EnsureSuccessStatusCode();
                        store.Acknowledge(record);
                    }
                    if (_uploadEnabled && profile != null && store.Snapshot(profile).Historical is { } historical)
                    {
                        using var response = await client.PostAsJsonAsync("v1/run-history", new { sessionId = identity, historical }, RunStatisticsStore.Json, token);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (System.Net.Http.HttpRequestException error) { Entry.Logger.Warn($"Run statistics upload pending: {error.Message}"); }
                catch (OperationCanceledException) { }
                continue;
            }
            profile = signal.Run!.ProfileId;
            if (signal.NativeHistoryDirectory != null) store.Reconcile(profile, signal.NativeHistoryDirectory);
            if (signal.History != null) store.Import(signal.History);
            else
            {
                var previous = store.Find(signal.Run.RunId);
                if (previous?.Outcome != "pending" && previous != null) continue;
                var record = previous ?? signal.Run;
                if (signal.Kind == "resume" && previous != null && signal.NativeBattles > previous.Battles.Length)
                    record = record with { ObservedFromStart = false };
                static string[] Add(string[] values, string value) => values.Contains(value) ? values : [..values, value];
                record = signal.Kind switch
                {
                    "battle" => record with { Battles = Add(record.Battles, signal.Battle), EverEnabled = record.EverEnabled || signal.Enabled,
                        DisabledInCombat = record.DisabledInCombat || !signal.Enabled },
                    "solve" => record with { SolvedBattles = Add(record.SolvedBattles, signal.Battle), EverEnabled = true },
                    "execute" => record with { ExecutedBattles = Add(record.ExecutedBattles, signal.Battle),
                        AutoBattles = signal.Auto ? Add(record.AutoBattles, signal.Battle) : record.AutoBattles, EverEnabled = true },
                    "setting" => record with { EverEnabled = record.EverEnabled || signal.Enabled,
                        DisabledInCombat = record.DisabledInCombat || signal.InCombat && !signal.Enabled },
                    "end" => record with { Outcome = signal.Run.Outcome, EndedAt = signal.Run.EndedAt },
                    _ => record,
                };
                record = record with { Participation = !record.EverEnabled ? "none" : record.ObservedFromStart && !record.DisabledInCombat ? "full" : "partial" };
                if (record != previous) store.Save(record);
            }
            _snapshot = store.Snapshot(profile);
        }
    }
    public override void _ExitTree() { _uploadEnabled = false; lock (_transportGate) _uploadCancellation?.Cancel(); _signals.Writer.TryComplete(); _instance = null; }
}

internal sealed class RunStatisticsNewRunPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_statistics_new_run";
    public static string Description => "标记新单人跑局";
    public static ModPatchTarget[] GetTargets() => [new(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer), [typeof(RunState), typeof(bool), typeof(DateTimeOffset?)])];
    public static void Postfix() => RunStatistics.NewRunPrepared = true;
}
internal sealed class RunStatisticsLaunchPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_statistics_launch";
    public static string Description => "记录新局与续档身份";
    public static ModPatchTarget[] GetTargets() => [new(typeof(RunManager), nameof(RunManager.Launch), [])];
    public static void Postfix(RunManager __instance) => RunStatistics.Launched(__instance);
}
internal sealed class RunStatisticsEndPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_statistics_end";
    public static string Description => "记录原生跑局胜负结算";
    public static ModPatchTarget[] GetTargets() => [new(typeof(SaveManager), nameof(SaveManager.SaveRunHistory), [typeof(RunHistory)])];
    public static void Postfix(RunHistory history) => RunStatistics.Ended(history);
}
