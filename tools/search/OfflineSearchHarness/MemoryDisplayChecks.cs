using System.Reflection;
using CombatSolver;

namespace OfflineSearchHarness;

internal static class MemoryDisplayChecks
{
    // Load the saved baseline DLL through OFFLINE_HARNESS_COMBATSOLVER_DLL for this probe.
    internal static void ReproduceOldCapacity()
    {
        SearchMemoryUsageSnapshot sample = new(6_000_000_000L, 24_000_000_000L,
            16_000_000_000L, true, 0, long.MaxValue, 24_000_000_000L, 22_000_000_000L, false, false);
        var property = typeof(SearchMemoryUsageSnapshot).GetProperty("ProcessMemoryLimitBytes")
            ?? throw new MissingMemberException("This probe requires the pre-fix production DLL.");
        long capacity = (long)property.GetValue(sample)!;
        var build = typeof(SolverMemoryUsageBar).GetMethod("BuildDisplay", BindingFlags.NonPublic | BindingFlags.Static)!;
        object display = build.Invoke(null, [sample])!;
        string text = (string)display.GetType().GetProperty("Text")!.GetValue(display)!;
        if (capacity != 4_000_000_000L || !text.Contains("6.0 GB") || !text.Contains("4.0 GB"))
            throw new InvalidOperationException("Old production capacity reproduction changed: " + text);
        Console.WriteLine($"MEMORY_OLD_CAPACITY_REPRODUCED process=6000000000 physical_used=24000000000 "
            + $"pressure_threshold=22000000000 derived_capacity={capacity} text={text}");
    }

    internal static void Run()
    {
        var format = typeof(SolverMemoryUsageBar).GetMethod("FormatSummary", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("Memory bar summary formatter");
        string text = (string)format.Invoke(null, [6_000_000_000L, 4_000_000_000L])!;
        string expected = SolverText.IsEnglish
            ? "Game used 6.0 GB · System available 4.0 GB"
            : "游戏占用 6.0 GB · 系统可用 4.0 GB";
        if (text != expected)
            throw new InvalidOperationException($"Memory display unit contract: expected '{expected}', actual '{text}'.");
        SearchMemoryUsageSnapshot sample = UnattendedTestRunner.AssertMemoryDisplayContract();
        Console.WriteLine($"MEMORY_DISPLAY_CHECKS Passed scope=offline_display_contract server_gc={sample.IsServerGc} "
            + $"working_set={sample.ProcessWorkingSetBytes} physical_used={sample.PhysicalMemoryUsedBytes} "
            + $"physical_total={sample.PhysicalMemoryTotalBytes} physical_available={sample.PhysicalMemoryAvailableBytes} "
            + $"gc_threshold={sample.GcHighMemoryLoadThresholdBytes} pressure_threshold={sample.SystemMemoryLimitBytes} "
            + $"no_gc_limit={sample.SearchAllocationLimitBytes} sample_utc={sample.SampledAtUtc:O} sample_duration_ms={sample.SampleDurationMilliseconds}");
    }
}
