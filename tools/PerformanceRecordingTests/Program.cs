using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSolver;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args[0] == "registry")
{
    using (PerformanceSession session = new(args[1], () => new WrapperRegistrySnapshot(11, 22)))
        session.Write(new { kind = "registry_fixture" });
    using JsonDocument row = JsonDocument.Parse(File.ReadLines(Path.Combine(args[1], "timeline.jsonl"))
        .First(line => line.Contains("\"kind\":\"sample\"")));
    var counts = row.RootElement.GetProperty("wrapperRegistry");
    if (counts.GetProperty("GodotObjects").GetInt32() != 11 || counts.GetProperty("OtherWrappers").GetInt32() != 22)
        throw new Exception("Wrapper registry counts did not reach the process timeline.");
    Console.WriteLine("PASS: background registry counts serialized.");
    return;
}

if (args[0] == "trace-stacks")
{
    string etlx = args[1].EndsWith(".etlx", StringComparison.OrdinalIgnoreCase) ? args[1]
        : TraceLog.CreateFromEventPipeDataFile(args[1], args[1] + ".validation.etlx", new TraceLogOptions { LocalSymbolsOnly = true });
    using TraceLog trace = new(etlx);
    long handles = 0, handleStacks = 0, triggered = 0, triggerStacks = 0;
    Dictionary<string, int> triggerOrigins = [];
    List<object> triggerEvents = [];
    foreach (var e in trace.Events)
    {
        if (e is SetGCHandleTraceData) { handles++; if (e.CallStack() != null) handleStacks++; }
        if (e.EventName == "GC/Triggered")
        {
            triggered++;
            if (e.CallStack() != null) triggerStacks++;
            List<string> frames = [];
            for (var stack = e.CallStack(); stack != null && frames.Count < 4; stack = stack.Caller)
                frames.Add(stack.CodeAddress.FullMethodName);
            string origin = frames.Count > 0 ? string.Join(" <- ", frames) : "[no stack]";
            triggerOrigins[origin] = triggerOrigins.GetValueOrDefault(origin) + 1;
            triggerEvents.Add(new { milliseconds = e.TimeStampRelativeMSec, origin });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { handles, handleStacks, triggered, triggerStacks, trace.EventsLost,
        triggerOrigins = triggerOrigins.OrderByDescending(pair => pair.Value).Take(10), triggerEvents }));
    if ((handles == 0 && triggered == 0) || trace.EventsLost != 0 || (handles > 0 && handleStacks == 0) || (triggered > 0 && triggerStacks == 0))
        throw new Exception("Requested event stacks were missing.");
    return;
}

if (args[0] is "trace" or "trace-handles")
{
    using EventPipeEventSource source = new(args[1]);
    long samples = 0, allocations = 0, collections = 0, contentions = 0;
    long handlesCreated = 0, handlesDestroyed = 0, stackWalks = 0;
    HashSet<int> threads = [];
    source.Dynamic.All += e =>
    {
        if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler") { samples++; threads.Add(e.ThreadID); }
    };
    source.Clr.GCAllocationTick += _ => allocations++;
    source.Clr.GCStart += _ => collections++;
    source.Clr.ContentionStart += _ => contentions++;
    source.Clr.GCSetGCHandle += _ => handlesCreated++;
    source.Clr.GCDestoryGCHandle += _ => handlesDestroyed++;
    source.Clr.ClrStackWalk += _ => stackWalks++;
    source.Process();
    Console.WriteLine(JsonSerializer.Serialize(new { samples, allocations, collections, contentions, handlesCreated, handlesDestroyed, stackWalks, sampledThreads = threads.Count, source.EventsLost }));
    if (samples == 0 || allocations == 0 || collections == 0 || source.EventsLost != 0)
        throw new Exception("Trace coverage contract failed.");
    // EventPipe stores event stacks in its stack blocks, not standalone CLR StackWalk events.
    // The trace-stacks mode verifies their association after ETLX conversion.
    if (args[0] == "trace-handles" && (handlesCreated == 0 || handlesDestroyed == 0))
        throw new Exception("Handle creation/destruction events were not captured.");
    return;
}

string directory = Path.GetFullPath(args[0]);
using (PerformanceSession session = new(directory))
{
    File.WriteAllText(Path.Combine(directory, "target-pid.txt"), Environment.ProcessId.ToString());
    Stopwatch duration = Stopwatch.StartNew();
    int counter = 0;
    while (duration.Elapsed.TotalSeconds < 36)
    {
        // A deliberate 3-second stopped main-thread heartbeat, while the writer continues.
        if (duration.Elapsed.TotalSeconds < 12 || duration.Elapsed.TotalSeconds > 15) session.Heartbeat();
        session.Write(new { kind = "fixture", utcMs = PerformanceSession.Now, counter = counter++ });
        AllocateAndCompute();
        Thread.Sleep(10);
    }
    if (session.Failure != null || session.Dropped != 0) throw new Exception("Recorder failed: " + session.Failure);
}
using (JsonDocument end = JsonDocument.Parse(File.ReadLines(Path.Combine(directory, "timeline.jsonl")).Last()))
    if (end.RootElement.GetProperty("kind").GetString() != "writer_end") throw new Exception("Writer did not drain.");
bool sawHang = false, sawThread = false;
foreach (string line in File.ReadLines(Path.Combine(directory, "timeline.jsonl")))
{
    using JsonDocument row = JsonDocument.Parse(line);
    string? kind = row.RootElement.GetProperty("kind").GetString();
    sawThread |= kind == "thread";
    sawHang |= kind == "sample" && row.RootElement.GetProperty("heartbeatAgeMs").GetDouble() > 1500;
}
if (!sawHang || !sawThread) throw new Exception("Missing stopped-heartbeat or thread samples.");
Console.WriteLine("PASS: writer drain, process/GC/thread samples, stopped-heartbeat detection.");

[MethodImpl(MethodImplOptions.NoInlining)]
static void AllocateAndCompute()
{
    for (int i = 0; i < 20; i++)
    {
        byte[] bytes = new byte[100_000];
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Weak);
        for (int j = 0; j < bytes.Length; j += 8) bytes[j] = (byte)(j * 17);
        handle.Free();
        GC.KeepAlive(bytes);
    }
}
