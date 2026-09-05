using System.Diagnostics;

namespace CompactStatePrototype;

internal readonly record struct WorkResult(long Transitions, ulong Checksum);
internal readonly record struct RunSample(double Milliseconds, long AllocatedBytes, long Transitions, string Checksum);
internal sealed record BenchmarkResult(string Shape, string Pattern, string Strategy,
    double MedianMilliseconds, double BytesPerTransition, double NanosecondsPerTransition,
    RunSample[] Samples);

internal static class Benchmarks
{
    public static BenchmarkResult[] Run(RootSnapshot root, int depth, int fanout, int repetitions)
    {
        List<BenchmarkResult> rows = [];
        foreach (string shape in new[] { "DepthFirstRollback", "RetainedFrontier" })
        foreach (WritePattern pattern in Enum.GetValues<WritePattern>())
        {
            Dictionary<string, List<RunSample>> measurements = [];
            WorkResult? expected = null;
            // Rotate the implementation order between rounds to reduce systematic order bias.
            for (int round = 0; round < repetitions; round++)
            for (int offset = 0; offset < PrototypeChecks.Factories.Length; offset++)
            {
                int strategy = (round + offset) % PrototypeChecks.Factories.Length;
                IBranchState state = PrototypeChecks.Factories[strategy](root);
                _ = RunWork(state, shape, depth, fanout, pattern);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                WorkResult work = RunWork(state, shape, depth, fanout, pattern);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                if (expected is { } reference && reference != work)
                    throw new InvalidOperationException($"Benchmark workload mismatch: {shape}/{pattern}/{state.Name}.");
                expected ??= work;
                if (!measurements.TryGetValue(state.Name, out List<RunSample>? samples))
                    measurements.Add(state.Name, samples = []);
                samples.Add(new RunSample(elapsed, allocated, work.Transitions, work.Checksum.ToString("x16")));
            }
            foreach ((string strategy, List<RunSample> samples) in measurements.OrderBy(pair => pair.Key))
            {
                double elapsed = Median(samples.Select(sample => sample.Milliseconds));
                double allocation = Median(samples.Select(sample => (double)sample.AllocatedBytes));
                long transitions = samples[0].Transitions;
                rows.Add(new BenchmarkResult(shape, pattern.ToString(), strategy, elapsed,
                    allocation / transitions, elapsed * 1_000_000 / transitions, samples.ToArray()));
            }
        }
        return rows.ToArray();
    }

    private static WorkResult RunWork(IBranchState state, string shape, int depth, int fanout, WritePattern pattern)
        => shape == "DepthFirstRollback"
            ? Visit(state, depth, fanout, 1, pattern)
            : RetainFrontier(state, depth, fanout, pattern);

    private static WorkResult Visit(IBranchState state, int depth, int fanout, ulong path, WritePattern pattern)
    {
        long transitions = 0;
        ulong checksum = 0;
        for (int child = 0; child < fanout; child++)
        {
            state.BeginBranch();
            try
            {
                ulong childPath = path * (ulong)fanout + (ulong)child;
                Workload.Apply(state, childPath, pattern);
                transitions++;
                checksum = unchecked(checksum + Workload.Sample(state, childPath));
                if (depth > 1)
                {
                    WorkResult nested = Visit(state, depth - 1, fanout, childPath, pattern);
                    transitions += nested.Transitions;
                    checksum = unchecked(checksum + nested.Checksum);
                }
            }
            finally { state.RollbackBranch(); }
        }
        return new WorkResult(transitions, checksum);
    }

    private static WorkResult RetainFrontier(IBranchState state, int depth, int fanout, WritePattern pattern)
    {
        List<(IBranchState State, ulong Path)> frontier = [(state, 1)];
        long transitions = 0;
        ulong checksum = 0;
        for (int level = 0; level < depth; level++)
        {
            List<(IBranchState State, ulong Path)> next = new(frontier.Count * fanout);
            foreach ((IBranchState parent, ulong path) in frontier)
            {
                for (int child = 0; child < fanout; child++)
                {
                    IBranchState branch = parent.ForkRetained();
                    ulong childPath = path * (ulong)fanout + (ulong)child;
                    Workload.Apply(branch, childPath, pattern);
                    transitions++;
                    checksum = unchecked(checksum + Workload.Sample(branch, childPath));
                    next.Add((branch, childPath));
                }
            }
            frontier = next;
        }
        GC.KeepAlive(frontier);
        return new WorkResult(transitions, checksum);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
    }
}
