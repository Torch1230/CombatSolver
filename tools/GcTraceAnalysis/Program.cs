using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

Options options = Options.Parse(args);
string inputPath = Path.GetFullPath(options.Input);
string outputPath = Path.GetFullPath(options.Output);
if (inputPath == outputPath)
    throw new ArgumentException("Input and report paths must differ.");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

string etlxPath = inputPath;
string? conversionLogPath = null;
bool? conversionReportedTruncated = null;
if (!inputPath.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase))
{
    etlxPath = outputPath + ".etlx";
    conversionLogPath = outputPath + ".conversion.log";
    if (File.Exists(etlxPath))
        throw new IOException($"Converted trace already exists: {etlxPath}. Use it as --input to reanalyze.");
    using StreamWriter conversionLog = new(conversionLogPath);
    etlxPath = TraceLog.CreateFromEventPipeDataFile(inputPath, etlxPath, new TraceLogOptions
    {
        ConversionLog = conversionLog,
        LocalSymbolsOnly = true,
        ContinueOnError = options.AllowIncomplete,
        OnLostEvents = (truncated, _, _) => conversionReportedTruncated = truncated,
    });
}

using TraceLog trace = new(etlxPath);
Dictionary<int, StackInfo> stackCache = [];
Dictionary<string, Totals> scopes = [];
Dictionary<string, Totals> categories = [];
Dictionary<string, Totals> inclusiveTags = [];
Dictionary<string, Totals> allTypes = [];
Dictionary<string, Totals> searchTypes = [];
Dictionary<string, Totals> bytesSources = [];
Dictionary<string, Totals> processes = [];
Dictionary<string, Totals> threads = [];
Dictionary<string, Totals> searchCategoryTypes = [];
Dictionary<int, Totals> stackTotals = [];
Totals all = new();
Totals search = new();
long allocationEventsBeforeFilters = 0;

foreach (var data in trace.Events)
{
    if (data is not GCAllocationTickTraceData allocation)
        continue;
    allocationEventsBeforeFilters++;
    if (options.ProcessId is { } processId && data.ProcessID != processId
        || options.StartMs is { } startMs && data.TimeStampRelativeMSec < startMs
        || options.EndMs is { } endMs && data.TimeStampRelativeMSec > endMs)
        continue;

    // Allocation ticks are weighted samples, not individual allocations. The tick's
    // type labels the sampled object; this amount covers allocation since the prior tick.
    long estimatedBytes = allocation.Version >= 2
        ? checked((long)allocation.AllocationAmount64)
        : unchecked((uint)allocation.AllocationAmount);
    if (estimatedBytes < 0)
        throw new InvalidDataException("Negative allocation tick weight.");
    string bytesSource = allocation.Version >= 2 ? "AllocationAmount64" : "AllocationAmount32";
    string type = string.IsNullOrEmpty(allocation.TypeName) ? "[type unavailable]" : allocation.TypeName;
    TraceCallStack? callStack = data.CallStack();
    int stackId = callStack is null ? -1 : (int)callStack.CallStackIndex;
    if (!stackCache.TryGetValue(stackId, out StackInfo? stack))
    {
        stack = StackInfo.Read(callStack);
        stackCache.Add(stackId, stack);
    }

    double time = data.TimeStampRelativeMSec;
    all.Add(estimatedBytes, time, stack);
    Add(scopes, stack.Scope, estimatedBytes, time, stack);
    Add(allTypes, type, estimatedBytes, time, stack);
    Add(bytesSources, bytesSource, estimatedBytes, time, stack);
    Add(processes, data.ProcessID.ToString(CultureInfo.InvariantCulture), estimatedBytes, time, stack);
    Add(threads, $"{data.ProcessID}/{data.ThreadID}/{stack.Scope}", estimatedBytes, time, stack);
    Add(stackTotals, stackId, estimatedBytes, time, stack);
    if (stack.IsConfirmedSearch)
    {
        search.Add(estimatedBytes, time, stack);
        Add(searchTypes, type, estimatedBytes, time, stack);
        Add(categories, stack.Category, estimatedBytes, time, stack);
        Add(searchCategoryTypes, $"{stack.Category} / {type}", estimatedBytes, time, stack);
        foreach (string tag in stack.Tags)
            Add(inclusiveTags, tag, estimatedBytes, time, stack);
    }
}

var report = new
{
    SchemaVersion = 1,
    InputPath = inputPath,
    EtlxPath = etlxPath,
    ConversionLogPath = conversionLogPath,
    Completeness = new
    {
        Status = conversionLogPath is null ? "UnknownFromExistingEtlx"
            : options.AllowIncomplete ? "PartialConversionExplicitlyAllowed" : "StrictConversionSucceeded",
        options.AllowIncomplete,
        ConversionReportedTruncated = conversionReportedTruncated,
    },
    TraceEventVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString(),
    TraceEvents = trace.EventCount,
    TraceEventsLost = trace.EventsLost,
    AllocationEventsBeforeFilters = allocationEventsBeforeFilters,
    Filters = new { options.ProcessId, options.StartMs, options.EndMs },
    Interpretation = new[]
    {
        "SampleCount counts GCAllocationTick events, not allocated objects.",
        "EstimatedBytes sums AllocationAmount64 (legacy events use AllocationAmount32). It is a sampling estimate, not an exact type allocation total.",
        "A tick labels one sampled object type, but its weight covers allocation since the preceding tick. Small/rare types may be absent or noisy.",
        "ConfirmedSearch requires a resolved solver/coordinator/work-item stack anchor. Root capture is explicitly excluded even with a search ancestor.",
        "Thread ID, a CombatSolver frame, or a generic simulator/Fork frame alone never establishes search ownership.",
        "SearchRequest includes coordination; SearchLaneInfrastructure is reported separately and is not part of ConfirmedSearch.",
        "Missing/unresolved stacks and unanchored simulation samples remain separate; they cannot safely be assigned to search or startup.",
        "SearchCategories are exclusive: Fork, ProjectedShuffle, SnapshotStateEvaluation, History, Other. InclusiveSearchTags overlap and must not be summed.",
        "Trace timing is diagnostic only; instrumentation and collection change execution cost. Compare allocation samples with independent exact phase counters.",
    },
    Rules = StackInfo.RuleDescription,
    AllIncludedSamples = all,
    ConfirmedSearch = search,
    ByteWeightSources = Rows(bytesSources),
    Processes = Rows(processes),
    Scopes = Rows(scopes),
    SearchCategories = Rows(categories),
    InclusiveSearchTags = Rows(inclusiveTags),
    ThreadsByScope = Rows(threads),
    TopAllTypes = Rows(allTypes).Take(options.Top).ToArray(),
    TopSearchTypes = Rows(searchTypes).Take(options.Top).ToArray(),
    TopSearchCategoryTypes = Rows(searchCategoryTypes).Take(options.Top).ToArray(),
    TopSearchStacks = StackRows(confirmedSearch: true),
    TopOtherStacks = StackRows(confirmedSearch: false),
};
string temporaryPath = outputPath + ".tmp";
File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
}) + Environment.NewLine);
File.Move(temporaryPath, outputPath, overwrite: true);
Console.WriteLine($"Report: {outputPath}");
Console.WriteLine($"Included: {all.SampleCount:N0} allocation ticks, {all.EstimatedBytes:N0} estimated bytes; "
    + $"confirmed search: {search.SampleCount:N0} ticks, {search.EstimatedBytes:N0} estimated bytes.");
Console.WriteLine($"Missing stacks: {all.SamplesWithoutStack:N0}; unresolved-only stacks: "
    + $"{all.SamplesWithUnresolvedOnlyStack:N0}; trace events lost: {trace.EventsLost}.");

object[] StackRows(bool confirmedSearch) => stackTotals
    .Where(pair => stackCache[pair.Key].IsConfirmedSearch == confirmedSearch)
    .OrderByDescending(pair => pair.Value.EstimatedBytes)
    .Take(options.Top)
    .Select(pair => (object)new
    {
        StackId = pair.Key,
        stackCache[pair.Key].Scope,
        stackCache[pair.Key].Category,
        stackCache[pair.Key].Tags,
        stackCache[pair.Key].Anchor,
        Totals = pair.Value,
        stackCache[pair.Key].Frames,
    })
    .ToArray();

static void Add<TKey>(Dictionary<TKey, Totals> buckets, TKey key, long bytes, double time, StackInfo stack)
    where TKey : notnull
{
    if (!buckets.TryGetValue(key, out Totals? totals))
        buckets.Add(key, totals = new());
    totals.Add(bytes, time, stack);
}

static Bucket[] Rows(Dictionary<string, Totals> buckets) => buckets
    .OrderByDescending(pair => pair.Value.EstimatedBytes)
    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
    .Select(pair => new Bucket(pair.Key, pair.Value))
    .ToArray();

sealed record Bucket(string Key, Totals Totals);

sealed class Totals
{
    public long SampleCount { get; private set; }
    public long EstimatedBytes { get; private set; }
    public long SamplesWithoutStack { get; private set; }
    public long SamplesWithUnresolvedOnlyStack { get; private set; }
    public long SamplesWithSomeUnresolvedFrames { get; private set; }
    public double? FirstSampleMs { get; private set; }
    public double? LastSampleMs { get; private set; }

    public void Add(long bytes, double time, StackInfo stack)
    {
        SampleCount++;
        EstimatedBytes = checked(EstimatedBytes + bytes);
        if (stack.Frames.Length == 0)
            SamplesWithoutStack++;
        else if (stack.ResolvedFrames == 0)
            SamplesWithUnresolvedOnlyStack++;
        if (stack.ResolvedFrames < stack.Frames.Length && stack.ResolvedFrames > 0)
            SamplesWithSomeUnresolvedFrames++;
        FirstSampleMs = FirstSampleMs is { } first ? Math.Min(first, time) : time;
        LastSampleMs = LastSampleMs is { } last ? Math.Max(last, time) : time;
    }
}

sealed record StackInfo(string[] Frames, int ResolvedFrames, string Scope, string Category,
    string[] Tags, string? Anchor)
{
    private const string Solver = "CombatSolver.CombatBeamSolver";
    private const string Simulator = "CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator";

    public bool IsConfirmedSearch => Scope is "ExpansionWork" or "SolverSearch" or "SearchRequest";

    public static readonly object RuleDescription = new
    {
        RootCapture = "CombatSolver.CombatRootSnapshot.Capture (highest precedence)",
        ExpansionWork = new[]
        {
            Solver + ".ParallelExpansionExecutor.Execute/ExecuteActionReplay/ExecuteRoundChoiceReplay",
            Solver + ".EvaluateRawExpansion/EvaluatePreparedCardAction/ReplayAction/Replay/Expand",
        },
        SolverSearch = Solver + ".Solve/SolveCore",
        SearchRequest = "CombatSolver.CombatSearchCoordinator.Solve/SolveCore",
        SearchLaneInfrastructure = Solver + ".ParallelExpansionExecutor.ExpansionLane.Run without a work/search anchor",
        Fork = Simulator + ".Fork or CombatSolver.CombatRootSnapshot.ForkSimulator or solver PrepareReplayForkSeed/Core",
        SnapshotStateEvaluation = Solver + ".Snapshot/BuildStateKey/BuildCardStateFingerprint/BuildUnorderedPileKey/ProjectHpAfterThreat/BuildThreatFocus",
        ProjectedShuffle = Solver + ".BuildProjectedShuffleOrder",
        History = "CombatPredictionHistory / CombatPredictionCardSnapshot.Capture / PredictionTrace",
        MethodMatching = "Type and method boundaries; + and / nested-type separators normalized; compiler-generated <Method> frames accepted.",
    };

    public static StackInfo Read(TraceCallStack? stack)
    {
        List<string> frames = [];
        List<string> names = [];
        for (TraceCallStack? current = stack; current is not null; current = current.Caller)
        {
            TraceCodeAddress address = current.CodeAddress;
            string? name = address.FullMethodName;
            if (!string.IsNullOrEmpty(name))
            {
                frames.Add(name);
                names.Add(name.Replace('+', '.').Replace('/', '.'));
            }
            else
                frames.Add($"[unresolved] {address.ModuleName}!0x{address.Address:x}");
        }

        string? Anchor(string type, params string[] methods) => names.FirstOrDefault(name =>
            methods.Any(method => MatchesMethod(name, type, method)));

        string scope;
        string? anchor = Anchor("CombatSolver.CombatRootSnapshot", "Capture");
        if (anchor is not null)
            scope = "RootCapture";
        else if ((anchor = Anchor(Solver + ".ParallelExpansionExecutor", "Execute", "ExecuteActionReplay", "ExecuteRoundChoiceReplay")
                 ?? Anchor(Solver, "EvaluateRawExpansion", "EvaluatePreparedCardAction", "ReplayAction", "Replay", "Expand")) is not null)
            scope = "ExpansionWork";
        else if ((anchor = Anchor(Solver, "Solve", "SolveCore")) is not null)
            scope = "SolverSearch";
        else if ((anchor = Anchor("CombatSolver.CombatSearchCoordinator", "Solve", "SolveCore")) is not null)
            scope = "SearchRequest";
        else if ((anchor = Anchor(Solver + ".ParallelExpansionExecutor.ExpansionLane", "Run")) is not null)
            scope = "SearchLaneInfrastructure";
        else if (frames.Count == 0)
            scope = "MissingStack";
        else if (names.Count == 0)
            scope = "UnresolvedStack";
        else if (names.Any(name => name.Contains("CombatSolver.Engine.", StringComparison.Ordinal)
                                  || name.Contains("CombatSolver.SimulatedCombatState.", StringComparison.Ordinal)))
            scope = "UnattributedSimulation";
        else
            scope = "Other";

        List<string> tags = [];
        if (Anchor(Simulator, "Fork") is not null
            || Anchor("CombatSolver.CombatRootSnapshot", "ForkSimulator") is not null
            || Anchor(Solver, "PrepareReplayForkSeed", "PrepareReplayForkSeedCore") is not null)
            tags.Add("Fork");
        if (Anchor(Solver, "BuildProjectedShuffleOrder") is not null)
            tags.Add("ProjectedShuffle");
        if (Anchor(Solver, "Snapshot", "BuildStateKey", "BuildCardStateFingerprint", "BuildUnorderedPileKey",
                "ProjectHpAfterThreat", "BuildThreatFocus") is not null)
            tags.Add("SnapshotStateEvaluation");
        if (names.Any(name => name.Contains("CombatSolver.Engine.InCombat.Simulation.CombatPredictionHistory.", StringComparison.Ordinal)
                              || name.Contains("CombatSolver.Engine.Common.PredictionTrace.", StringComparison.Ordinal))
            || Anchor("CombatSolver.Engine.InCombat.Simulation.CombatPredictionCardSnapshot", "Capture") is not null)
            tags.Add("History");
        return new(frames.ToArray(), names.Count, scope, tags.FirstOrDefault() ?? "Other", tags.ToArray(), anchor);
    }

    private static bool MatchesMethod(string name, string type, string method)
    {
        int index = name.IndexOf(type + ".", StringComparison.Ordinal);
        if (index < 0 || index > 0 && (char.IsLetterOrDigit(name[index - 1]) || name[index - 1] is '.' or '_'))
            return false;
        ReadOnlySpan<char> member = name.AsSpan(index + type.Length + 1);
        return member.StartsWith(method + "(", StringComparison.Ordinal)
               || member.StartsWith(method + "`", StringComparison.Ordinal)
               || member.Equals(method, StringComparison.Ordinal)
               || member.Contains("<" + method + ">", StringComparison.Ordinal);
    }
}

sealed record Options(string Input, string Output, int Top, int? ProcessId, double? StartMs, double? EndMs,
    bool AllowIncomplete)
{
    public static Options Parse(string[] args)
    {
        const string usage = "Usage: GcTraceAnalysis --input trace.nettrace|trace.etlx --output report.json "
            + "[--top 40] [--process-id ID] [--start-ms N] [--end-ms N] [--allow-incomplete true]";
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine(usage);
            Environment.Exit(0);
        }
        Dictionary<string, string> values = [];
        for (int index = 0; index < args.Length; index += 2)
        {
            string key = args[index];
            if (key is not ("--input" or "--output" or "--top" or "--process-id" or "--start-ms" or "--end-ms" or "--allow-incomplete")
                || index + 1 >= args.Length || !values.TryAdd(key, args[index + 1]))
                throw new ArgumentException(usage);
        }
        if (!values.TryGetValue("--input", out string? input) || !values.TryGetValue("--output", out string? output))
            throw new ArgumentException(usage);
        int top = values.TryGetValue("--top", out string? topText) ? int.Parse(topText, CultureInfo.InvariantCulture) : 40;
        int? processId = values.TryGetValue("--process-id", out string? pidText) ? int.Parse(pidText, CultureInfo.InvariantCulture) : null;
        double? start = values.TryGetValue("--start-ms", out string? startText) ? double.Parse(startText, CultureInfo.InvariantCulture) : null;
        double? end = values.TryGetValue("--end-ms", out string? endText) ? double.Parse(endText, CultureInfo.InvariantCulture) : null;
        if (top <= 0 || processId <= 0 || start is { } startValue && (!double.IsFinite(startValue) || startValue < 0)
            || end is { } endValue && (!double.IsFinite(endValue) || endValue < 0) || start > end)
            throw new ArgumentException("Invalid numeric option. " + usage);
        bool allowIncomplete = values.TryGetValue("--allow-incomplete", out string? incompleteText) && bool.Parse(incompleteText);
        return new(input, output, top, processId, start, end, allowIncomplete);
    }
}
