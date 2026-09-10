using System.Diagnostics;

namespace CombatSolver;

internal enum CompactProfilePhase { SolverSetup, RootFork, ProjectEvents, Freeze, Snapshot, Sort, EmptyProbe }

/// <summary>Test-only, synchronous current-thread measurements. Snapshot subphases use the
/// existing production wall-time meter and are reported separately because they overlap.</summary>
internal sealed class CompactPhaseProbe
{
    internal readonly record struct Stamp(double Cpu, long Wall, long Bytes);
    internal sealed record Row(string Phase, long Calls, double CpuMilliseconds,
        double ElapsedMilliseconds, long AllocatedBytes);
    private readonly long[] _calls = new long[7], _ticks = new long[7], _bytes = new long[7];
    private readonly double[] _cpu = new double[7];

    internal Stamp Begin() => new(CompactThreadCpu.Milliseconds(), Stopwatch.GetTimestamp(),
        GC.GetAllocatedBytesForCurrentThread());

    internal void End(CompactProfilePhase phase, Stamp start)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start.Bytes;
        long ticks = Stopwatch.GetTimestamp() - start.Wall;
        double cpu = CompactThreadCpu.Milliseconds() - start.Cpu;
        int index = (int)phase;
        _calls[index]++; _ticks[index] += ticks; _bytes[index] += bytes; _cpu[index] += cpu;
    }

    internal Row[] Rows() => Enum.GetValues<CompactProfilePhase>().Select(phase =>
        new Row(phase.ToString(), _calls[(int)phase], _cpu[(int)phase],
            Stopwatch.GetElapsedTime(0, _ticks[(int)phase]).TotalMilliseconds, _bytes[(int)phase])).ToArray();
}
