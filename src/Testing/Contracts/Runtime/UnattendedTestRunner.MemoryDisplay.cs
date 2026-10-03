namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal static SearchMemoryUsageSnapshot AssertMemoryDisplayContract()
    {
        if (!SolverMemoryUsageBar.ExerciseFormattingForTesting())
            throw new InvalidOperationException("Memory display formatting/pressure contract failed.");
        SearchMemoryUsageSnapshot sample = SolverController.CaptureSearchMemoryUsage();
        if (OperatingSystem.IsWindows()
            && (!sample.HasPhysicalMemorySample || sample.PhysicalMemoryAvailableBytes is not long available
                || sample.PhysicalMemoryUsedBytes + available != sample.PhysicalMemoryTotalBytes
                || sample.SampledAtUtc == default || sample.SampleDurationMilliseconds < 0
                || sample.IsServerGc != System.Runtime.GCSettings.IsServerGC))
            throw new InvalidOperationException("Memory display OS sample contract failed.");
        SolverController.LogSearchMemoryDisplayState(sample, "memory_contract", sample.CleanupPressureRatio);
        return sample;
    }
}
