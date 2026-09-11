using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class PerformanceRecording : Node
{
    private sealed record Configuration(string TraceToolPath, string WatcherScriptPath, string OutputDirectory,
        string SourceRevision, int SegmentSeconds = 300, string? DumpToolPath = null, int HandleWindowSeconds = 10);
    private sealed record Tracked(long Id, string Kind, WeakReference<object> Reference, long CreatedMs, int Gen2);
    private static PerformanceRecording? _instance;
    private static PerformanceSession? _processSession;
    private static string _processDirectory = "";
    private static string? _processFailure;
    private static bool _processCanSnapshot;
    private PerformanceSession? _session;
    private string _directory = "";
    private string? _startupFailure;
    private Label _status = null!;
    private long _lastFrame;
    private long _lastWindow;
    private long _lastInventory;
    private static long _nextId;
    private static readonly List<Tracked> _tracked = [];
    private readonly double[] _frameBins = new double[6];
    private double _maxFrame;
    private double _frameSum;
    private double _dispatcherMs;
    private long _dispatcherAllocated;
    private static long _trackingEvicted;
    private static WeakReference<object>? _lastRun, _lastCombat, _lastResult;
    private string? _lastContext;
    private static long _runId, _combatId;
    private long _mainAllocated;
    private static Harmony? _lifecyclePatches;
    private bool _canSnapshot;

    internal static void Start(NGame host)
    {
        if (_instance != null) return;
        string configPath = Path.Combine(Path.GetDirectoryName(typeof(Entry).Assembly.Location)!, "performance-recording.json");
        if (!File.Exists(configPath)) return;
        _instance = new PerformanceRecording { Name = "CombatSolverPerformanceRecording", ProcessMode = ProcessModeEnum.Always };
        host.AddChild(_instance);
        _instance.Initialize(configPath);
    }

    private void Initialize(string configPath)
    {
        CanvasLayer layer = new() { Layer = 120 };
        _status = new Label { Position = new Vector2(16, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeColorOverride("font_color", Colors.Yellow);
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 6);
        _status.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(_status);
        AddChild(layer);
        try
        {
            if (_processSession != null)
            {
                _session = _processSession;
                _directory = _processDirectory;
                _canSnapshot = _processCanSnapshot;
                _startupFailure = _processFailure;
                _lastWindow = _lastFrame = Stopwatch.GetTimestamp();
                _mainAllocated = GC.GetAllocatedBytesForCurrentThread();
                _session.Write(new { kind = "host_reattached", utcMs = PerformanceSession.Now });
                return;
            }
            Configuration config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configPath))
                ?? throw new InvalidDataException("Performance recording configuration is empty.");
            if (config.SegmentSeconds < 10 || config.SegmentSeconds > 1800)
                throw new InvalidDataException("SegmentSeconds must be between 10 and 1800.");
            if (config.HandleWindowSeconds < 0 || config.HandleWindowSeconds > 30)
                throw new InvalidDataException("HandleWindowSeconds must be between 0 and 30.");
            if (!File.Exists(config.TraceToolPath) || !File.Exists(config.WatcherScriptPath))
                throw new FileNotFoundException("Performance trace tool or watcher script is missing.");
            _canSnapshot = config.DumpToolPath != null && File.Exists(config.DumpToolPath);
            _directory = Path.Combine(config.OutputDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + System.Environment.ProcessId);
            _processSession = _session = new PerformanceSession(_directory, CreateWrapperRegistryProbe());
            _processDirectory = _directory;
            _processCanSnapshot = _canSnapshot;
            GetTree().Root.TreeExiting += StopProcessRecording;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            _lifecyclePatches = PerformanceLifecycle.Start();
            _session.Write(new { kind = "start", utcMs = PerformanceSession.Now, pid = System.Environment.ProcessId,
                mainThread = System.Environment.CurrentManagedThreadId, nativeMainThread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0,
                config.SourceRevision, config.SegmentSeconds, config.HandleWindowSeconds, version = typeof(Entry).Assembly.GetName().Version?.ToString(),
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Entry).Assembly.Location))),
                onlineStatistics = SolverSettings.Current.OnlineStatisticsEnabled,
                cpus = System.Environment.ProcessorCount, stopwatchFrequency = Stopwatch.Frequency,
                settings = SolverSettings.Capture(), diagnosticOnly = true });
            File.WriteAllText(Path.Combine(_directory, "configuration.json"), JsonSerializer.Serialize(config));
            ProcessStartInfo start = new("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", config.WatcherScriptPath,
                "-TargetProcessId", System.Environment.ProcessId.ToString(), "-SessionDirectory", _directory,
                "-TraceToolPath", config.TraceToolPath, "-SegmentSeconds", config.SegmentSeconds.ToString(),
                "-GameLogDirectory", Path.Combine(OS.GetUserDataDir(), "logs") }) start.ArgumentList.Add(argument);
            using Process collector = Process.Start(start) ?? throw new InvalidOperationException("Trace collector did not start.");
            _lastWindow = _lastFrame = Stopwatch.GetTimestamp();
            _mainAllocated = GC.GetAllocatedBytesForCurrentThread();
            Inventory();
        }
        catch (Exception error)
        {
            _startupFailure = error.ToString();
            _processFailure = _startupFailure;
            Entry.Logger.Error("[PerformanceRecording] START_FAILED " + error);
        }
    }

    internal static void Log(string level, string message) => _processSession?.Write(new
        { kind = "solver", utcMs = PerformanceSession.Now, level, message });
    internal static void Dispatcher(double milliseconds, long allocated)
    {
        if (_instance == null) return;
        _instance._dispatcherMs += milliseconds;
        _instance._dispatcherAllocated += allocated;
    }
    internal static bool Enabled => _instance != null;

    public override void _Input(InputEvent input)
    {
        if (!_canSnapshot || _session == null || input is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, Keycode: Key.F8 }) return;
        long id = PerformanceSession.Now;
        string temporary = Path.Combine(_directory, "snapshot-request.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { id, utcMs = id, context = _lastContext }));
        File.Move(temporary, Path.Combine(_directory, "snapshot-request.json"), true);
        _session.Write(new { kind = "user_stutter_marker", utcMs = id, context = _lastContext });
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        try { CaptureFrame(); }
        catch (Exception error)
        {
            _startupFailure = error.ToString();
            _processFailure = _startupFailure;
            _session?.Write(new { kind = "recorder_failed", utcMs = PerformanceSession.Now, error = error.ToString() });
            Entry.Logger.Error("[PerformanceRecording] CAPTURE_FAILED " + error);
        }
    }

    private void CaptureFrame()
    {
        if (_startupFailure != null)
        {
            _status.Text = SolverText.Get("性能录制失败：请勿开始测试");
            _status.Modulate = Colors.Red;
            return;
        }
        if (_session == null) return;
        _session.Heartbeat();
        long now = Stopwatch.GetTimestamp();
        double gap = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
        _lastFrame = now;
        _frameSum += gap;
        _maxFrame = Math.Max(_maxFrame, gap);
        int bin = gap <= 16.7 ? 0 : gap <= 33.4 ? 1 : gap <= 50 ? 2 : gap <= 100 ? 3 : gap <= 250 ? 4 : 5;
        _frameBins[bin]++;
        if (gap >= 100) _session.Write(new { kind = "long_frame", utcMs = PerformanceSession.Now, gapMs = gap, context = _lastContext });
        if (Stopwatch.GetElapsedTime(_lastWindow, now).TotalSeconds < 1) return;
        _lastWindow = now;
        long captureStart = Stopwatch.GetTimestamp();
        var run = RunManager.Instance.DebugOnlyGetState();
        var combat = CombatManager.Instance.DebugOnlyGetState();
        var result = SolverController.CurrentResultForBugReport;
        Track(run, "run", ref _lastRun, ref _runId);
        Track(combat, "combat", ref _lastCombat, ref _combatId);
        long resultId = 0;
        Track(result, "result", ref _lastResult, ref resultId);
        string context = JsonSerializer.Serialize(new { run = _runId, combat = _combatId,
            inRun = RunManager.Instance.IsInProgress, inCombat = CombatManager.Instance.IsInProgress,
            floor = run?.TotalFloor, room = run?.CurrentRoom?.GetType().FullName,
            encounter = combat?.Encounter?.Id.Entry, searching = SolverController.IsSearching,
            deploying = SolverController.IsDeploying, fullAuto = SolverController.FullAutoEnabled,
            focused = GetWindow().HasFocus(), paused = GetTree().Paused,
            timeScale = Godot.Engine.TimeScale, maxFps = Godot.Engine.MaxFps,
            onlineStatistics = SolverSettings.Current.OnlineStatisticsEnabled });
        if (context != _lastContext) _session.Write(new { kind = "context", utcMs = PerformanceSession.Now, context });
        _lastContext = context;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        _session.Write(new { kind = "frames", utcMs = PerformanceSession.Now, context,
            bins = (double[])_frameBins.Clone(), binUpperMs = new[] { 16.7, 33.4, 50, 100, 250, double.MaxValue },
            maxMs = _maxFrame, sumMs = _frameSum, dispatcherMs = _dispatcherMs,
            dispatcherAllocated = _dispatcherAllocated, mainThreadAllocated = allocated - _mainAllocated,
            godot = Enum.GetValues<Performance.Monitor>().Where(m => m != Performance.Monitor.MonitorMax)
                .ToDictionary(m => m.ToString(), m => Performance.GetMonitor(m)),
            surviving = _tracked.Where(t => t.Reference.TryGetTarget(out _)).Select(t => new
                { t.Id, t.Kind, ageMs = PerformanceSession.Now - t.CreatedMs, gen2SinceCreated = GC.CollectionCount(2) - t.Gen2 }).ToArray(),
            trackingEvicted = _trackingEvicted,
            captureMs = Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds });
        _mainAllocated = allocated;
        Array.Clear(_frameBins);
        _frameSum = _maxFrame = _dispatcherMs = 0;
        _dispatcherAllocated = 0;
        UpdateStatus();
        if (Stopwatch.GetElapsedTime(_lastInventory, now).TotalSeconds >= 60) Inventory();
    }

    private void Track(object? target, string kind, ref WeakReference<object>? previous, ref long id)
    {
        if (target == null) { previous = null; id = 0; return; }
        if (previous?.TryGetTarget(out object? old) == true && ReferenceEquals(old, target)) return;
        previous = new(target);
        id = ++_nextId;
        _tracked.RemoveAll(t => !t.Reference.TryGetTarget(out _));
        if (_tracked.Count == 256) { _tracked.RemoveAt(0); _trackingEvicted++; }
        _tracked.Add(new(id, kind, previous, PerformanceSession.Now, GC.CollectionCount(2)));
        _session!.Write(new { kind = "object_seen", utcMs = PerformanceSession.Now, objectKind = kind, id,
            identity = RuntimeHelpers.GetHashCode(target) });
    }

    private void UpdateStatus()
    {
        string healthPath = Path.Combine(_directory, "collector-health.json");
        bool healthy = false;
        bool collectorFailed = false;
        if (File.Exists(healthPath))
        {
            using FileStream healthStream = new(healthPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument health = JsonDocument.Parse(healthStream);
            healthy = health.RootElement.GetProperty("status").GetString() == "recording"
                && PerformanceSession.Now - health.RootElement.GetProperty("utcMs").GetInt64() < 15000;
            collectorFailed = health.RootElement.GetProperty("status").GetString() == "failed";
        }
        bool failed = _session!.Failure != null || _session.Dropped > 0 || collectorFailed;
        _status.Text = SolverText.Get(failed ? "性能录制失败：请勿继续测试" : healthy ? "性能录制中（状态日志＋调用栈）" : "性能录制待就绪／中断：请稍候");
        if (_canSnapshot) _status.Text += "\n" + SolverText.Get("Ctrl＋F8：卡顿时保存内存现场（会短暂停顿）");
        _status.Modulate = failed ? Colors.Red : healthy ? Colors.LightGreen : Colors.Yellow;
    }

    internal static Func<WrapperRegistrySnapshot> CreateWrapperRegistryProbe()
    {
        Type tracker = typeof(Node).Assembly.GetType("Godot.DisposablesTracker", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var objects = (System.Collections.ICollection)(tracker.GetProperty("GodotObjectInstances", flags)
            ?? throw new MissingMemberException(tracker.FullName, "GodotObjectInstances")).GetValue(null)!;
        var wrappers = (System.Collections.ICollection)(tracker.GetProperty("OtherInstances", flags)
            ?? throw new MissingMemberException(tracker.FullName, "OtherInstances")).GetValue(null)!;
        // Counts only: no traversal or promotion of weak targets into diagnostic state.
        return () => new(objects.Count, wrappers.Count);
    }

    private void Inventory()
    {
        long started = Stopwatch.GetTimestamp();
        _lastInventory = Stopwatch.GetTimestamp();
        // Metadata only. No gameplay subscribers are invoked and no game models are retained.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => new
            { name = a.FullName, path = a.IsDynamic ? "dynamic" : a.Location }).ToArray();
        var patches = Harmony.GetAllPatchedMethods().Select(method => new
        {
            original = method.DeclaringType?.FullName + "." + method.Name,
            assembly = method.DeclaringType?.Assembly.GetName().Name,
            patches = DescribePatches(Harmony.GetPatchInfo(method))
        }).ToArray();
        _session!.Write(new { kind = "inventory", utcMs = PerformanceSession.Now, assemblies, patches,
            captureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
    }

    private static object[] DescribePatches(Patches? info)
    {
        if (info == null) return [];
        return new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) }
            .SelectMany(group => group.Item2.Select(p =>
            {
                MethodInfo method = p.PatchMethod;
                return (object)new { type = group.Item1, p.owner, p.priority,
                    method = method.DeclaringType?.FullName + "." + method.Name,
                    assembly = method.DeclaringType?.Assembly.FullName };
            })).ToArray();
    }

    public override void _ExitTree()
    {
        Log("lifecycle", "performance_host_detached");
        _instance = null;
    }

    internal static void VerifyHostReattachmentForTesting()
    {
        if (!UnattendedTestRunner.IsActive || _instance?._session == null)
            throw new InvalidOperationException("A diagnostic-enabled unattended session is required.");
        PerformanceSession session = _instance._session;
        NGame host = (NGame)_instance.GetParent();
        PerformanceRecording old = _instance;
        host.RemoveChild(old);
        old.Free();
        Start(host);
        if (_instance?._startupFailure != null || !ReferenceEquals(_instance?._session, session))
            throw new InvalidOperationException("Reattaching the observer changed the process recorder.");
        Log("test", "performance_host_reattachment_passed");
    }

    private static void OnProcessExit(object? sender, EventArgs args) => StopProcessRecording();
    private static void StopProcessRecording()
    {
        PerformanceSession? session = Interlocked.Exchange(ref _processSession, null);
        session?.Write(new { kind = "process_recording_end", utcMs = PerformanceSession.Now });
        session?.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
