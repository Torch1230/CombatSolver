using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSolver;

internal static class ProcessMemoryUsageChecks
{
    public static void Run(bool measure)
    {
        CheckWindowsCountersContract();
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

        Console.WriteLine("PASS current-process memory counter smoke check");

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

    private static void CheckWindowsCountersContract()
    {
        string[] fields =
        [
            "Size", "PageFaultCount", "PeakWorkingSetSize", "WorkingSetSize",
            "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
            "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage",
        ];
        Check(Marshal.SizeOf<ProcessMemoryUsage.ProcessMemoryCountersEx>() == 8 + 9 * IntPtr.Size,
            "PROCESS_MEMORY_COUNTERS_EX size must match two DWORDs and nine SIZE_Ts.");
        for (int index = 0; index < fields.Length; index++)
        {
            int expectedOffset = index < 2 ? index * 4 : 8 + (index - 2) * IntPtr.Size;
            Check(Marshal.OffsetOf<ProcessMemoryUsage.ProcessMemoryCountersEx>(fields[index]).ToInt64() == expectedOffset,
                $"Unexpected native offset for {fields[index]}.");
        }

        // Distinct values catch swapped fields or accidentally reading PagefileUsage.
        // The x64 witness also catches narrowing to a 32-bit counter.
        long workingSet = IntPtr.Size == 8 ? 4_294_967_419L : 1_048_699L;
        long privateBytes = IntPtr.Size == 8 ? 8_589_935_048L : 2_097_608L;
        ProcessMemoryUsage.ProcessMemoryCountersEx counters = new()
        {
            WorkingSetSize = checked((nuint)workingSet),
            PrivateUsage = checked((nuint)privateBytes),
            PeakWorkingSetSize = 101,
            PagefileUsage = 202,
            PeakPagefileUsage = 303,
        };
        Check(ProcessMemoryUsage.FromWindowsCounters(in counters) == new ProcessMemoryUsage(workingSet, privateBytes),
            "Working-set/private-byte mapping must preserve the exact native values.");
        Console.WriteLine($"PASS native layout and exact counter mapping ({IntPtr.Size * 8}-bit)");
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
