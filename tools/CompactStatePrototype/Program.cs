using System.Runtime.InteropServices;
using System.Text.Json;
using CompactStatePrototype;

int entities = 256;
int depth = 5;
int fanout = 4;
int repetitions = 5;
string? outputPath = null;
for (int index = 0; index < args.Length; index++)
{
    if (index + 1 >= args.Length)
        throw new ArgumentException("Options require a value.");
    string value = args[++index];
    switch (args[index - 1])
    {
        case "--entities": entities = int.Parse(value); break;
        case "--depth": depth = int.Parse(value); break;
        case "--fanout": fanout = int.Parse(value); break;
        case "--repetitions": repetitions = int.Parse(value); break;
        case "--output": outputPath = value; break;
        default: throw new ArgumentException("Unknown option: " + args[index - 1]);
    }
}
if (entities is < 2 or > 4096 || depth is < 1 or > 8 || fanout is < 1 or > 8
    || repetitions is < 1 or > 20 || Math.Pow(fanout, depth) > 32768)
{
    throw new ArgumentOutOfRangeException(nameof(args), "Keep this prototype's bounded synthetic workload small.");
}

string[] checks = PrototypeChecks.Run();
Console.WriteLine($"Passed {checks.Length} semantic checks.");
RootSnapshot root = new(entities, 0x0123456789abcdefUL);
BenchmarkResult[] results = Benchmarks.Run(root, depth, fanout, repetitions);
Console.WriteLine("shape,pattern,strategy,median_ms,bytes_per_transition,ns_per_transition");
foreach (BenchmarkResult row in results)
    Console.WriteLine(FormattableString.Invariant($"{row.Shape},{row.Pattern},{row.Strategy},{row.MedianMilliseconds:F3},{row.BytesPerTransition:F1},{row.NanosecondsPerTransition:F1}"));

if (outputPath is not null)
{
    var report = new
    {
        Scope = "Synthetic compact-state kernel; no game models, hooks, choices, history or parallel workers.",
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Runtime = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        ProjectTieredCompilation = false,
        TieredCompilationEnvironmentOverride = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
            ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
        ProcessorCount = Environment.ProcessorCount,
        Entities = entities,
        Words = root.WordCount,
        Depth = depth,
        Fanout = fanout,
        Repetitions = repetitions,
        PageWords = PageCowState.PageWords,
        AllocationScope = "Single-thread allocations in the measured workload; root construction and one full warmup per sample are excluded.",
        TimingScope = "Stopwatch wall time of the measured workload; explicit pre-sample Gen2 collection is excluded.",
        Checks = checks,
        Results = results,
    };
    string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    Directory.CreateDirectory(directory!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine("Wrote " + Path.GetFullPath(outputPath));
}
