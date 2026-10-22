using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

internal static class ProcessMemoryUsageChecks
{
    public static void Run(bool measure)
    {
        ProcessMemoryUsage current = ProcessMemoryUsage.CaptureCurrent()
            ?? throw new InvalidOperationException("Current-process memory counters are unavailable.");
        Check(current.WorkingSetBytes > 0 && current.PrivateBytes >= 0, "Invalid process memory counters.");

        if (OperatingSystem.IsWindows())
        {
            Check(ProcessMemoryUsage.CaptureWindows(0) == null, "An invalid handle must report unavailable counters.");
            Check(ProcessMemoryUsage.CaptureWindows(1) == null, "A failed native query must not return fabricated zero counters.");
            Check(ProcessMemoryUsage.CaptureCurrent() != null, "A failed query must not invalidate subsequent samples.");
            Parallel.For(0, 128, _ =>
            {
                var concurrent = ProcessMemoryUsage.CaptureCurrent();
                Check(concurrent is { WorkingSetBytes: > 0, PrivateBytes: > 0 }, "Concurrent native samples failed.");
            });
            Console.WriteLine("PASS native failure, subsequent sample and 128 concurrent current-process samples");
        }

        // Samples are sequential and can change while the process runs. Compare both
        // meanings independently, allowing bounded JIT/GC/working-set movement.
        ProcessMemoryUsage reference = CapturePrevious();
        current = ProcessMemoryUsage.CaptureCurrent()!.Value;
        const long tolerance = 16 * 1024 * 1024;
        Check(Math.Abs(current.WorkingSetBytes - reference.WorkingSetBytes) <= tolerance, "Working-set counter differs from the previous API.");
        Check(Math.Abs(current.PrivateBytes - reference.PrivateBytes) <= tolerance, "Private-byte counter differs from the previous API.");
        Console.WriteLine("PASS working-set and private-byte counter semantics (16 MiB sequential-sample tolerance)");

        if (!measure) return;
        _ = CapturePrevious();
        _ = ProcessMemoryUsage.CaptureCurrent();
        double[] previousMs = Measure(() => CapturePrevious());
        double[] currentMs = Measure(() => ProcessMemoryUsage.CaptureCurrent()
            ?? throw new InvalidOperationException("Benchmark query failed."));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            platform = OperatingSystem.IsWindows() ? "Windows" : "non-Windows fallback",
            runtime = Environment.Version.ToString(),
            previousMs,
            currentMs,
            previousMedianMs = Median(previousMs),
            currentMedianMs = Median(currentMs),
            scope = "One memory-counter pair in this check process; not rendered frame time.",
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ProcessMemoryUsage CapturePrevious()
    {
        using Process process = Process.GetCurrentProcess();
        return new ProcessMemoryUsage(process.WorkingSet64, process.PrivateMemorySize64);
    }

    private static double[] Measure(Func<ProcessMemoryUsage> capture)
        => Enumerable.Range(0, 8).Select(_ =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            ProcessMemoryUsage sample = capture();
            watch.Stop();
            Check(sample.WorkingSetBytes > 0 && sample.PrivateBytes >= 0, "Invalid benchmark counters.");
            return watch.Elapsed.TotalMilliseconds;
        }).ToArray();

    private static double Median(double[] samples)
    {
        double[] sorted = samples.Order().ToArray();
        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
