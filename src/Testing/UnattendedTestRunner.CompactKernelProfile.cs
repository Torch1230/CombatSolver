using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class CompactEvaluationDriver
    {
        private static readonly MethodInfo SnapshotMethod = typeof(CombatBeamSolver).GetMethod(
            "Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RunField = typeof(CombatBeamSolver).GetField(
            "_run", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo PerformanceField = RunField.FieldType.GetField("Performance")!;
        private static readonly FieldInfo ThreatField = RunField.FieldType.GetField("ThreatProjectionCache")!;
        private static readonly FieldInfo CoverageField = RunField.FieldType.GetField("CoverageCache")!;
        private readonly Func<CombatPredictionSimulator, int, int, int, SearchBoundaryReason,
            IReadOnlySet<uint>, SimulationSnapshot> _snapshot;
        private readonly ForkableSet<uint> _deaths = new();
        private readonly int _turn;
        private readonly CombatBeamSolver _solver;
        private readonly ICollection _threat, _coverage;
        internal readonly SearchPerformanceMetrics Metrics;
        internal int ThreatKeys => _threat.Count;
        internal int CoverageKeys => _coverage.Count;

        internal CompactEvaluationDriver(CombatRootSnapshot root, SolverDisplayNames display,
            BattleDamageSnapshot damage, SearchPolicySnapshot policy)
        {
            CombatBeamSolver solver = _solver = new(root, display, damage, policy with { MeasurePhasePerformance = true });
            _snapshot = SnapshotMethod.CreateDelegate<Func<CombatPredictionSimulator, int, int, int,
                SearchBoundaryReason, IReadOnlySet<uint>, SimulationSnapshot>>(solver);
            object run = RunField.GetValue(solver)!;
            Metrics = (SearchPerformanceMetrics)PerformanceField.GetValue(run)!;
            _threat = (ICollection)ThreatField.GetValue(run)!;
            _coverage = (ICollection)CoverageField.GetValue(run)!;
            _turn = root.StartTurnNumber;
        }

        internal SimulationSnapshot Evaluate(CompletedStateReadView view, int? turn = null)
            => _solver.SnapshotFromReadView(view, turn ?? _turn, 1, 0, SearchBoundaryReason.None, _deaths);

        internal SimulationSnapshot Evaluate(CombatPredictionSimulator sim, int? turn = null)
            => _snapshot(sim, turn ?? _turn, 1, 0, SearchBoundaryReason.None, _deaths);
    }

    private void ProfileCompactEvaluation(CombatRootSnapshot root, SolverDisplayNames display,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, CompactDiscardProjection adapter,
        IReadOnlyList<CompactCase> cases, int played)
    {
        if (!SimulationNotificationIsolation.IsActive)
            throw new InvalidOperationException("Compact profiling requires the production simulation isolation context.");
        int emptyCapabilityEligibleCards = Enumerable.Range(0, adapter.CardCount)
            .Count(id => RitsuEmptyCapabilityFastPath.CanSkip(adapter.Original(id)));
        CompactEvaluationDriver shared = new(root, display, damage, policy);
        string? requestedIterations = Environment.GetEnvironmentVariable("COMBATSOLVER_COMPACT_PROFILE_ITERATIONS");
        int iterations = requestedIterations is null ? 16 : int.Parse(requestedIterations,
            System.Globalization.CultureInfo.InvariantCulture);
        if (iterations is < 1 or > 256) throw new InvalidOperationException("Compact profile iterations must be 1..256.");
        var samples = new List<object>();
        long checksum = 0;
        int Batch(bool fresh, CompactPhaseProbe probe, SearchPerformanceMetrics metrics, bool verify, bool direct = false)
        {
            var setup = probe.Begin();
            CompactEvaluationDriver evaluator = fresh ? new(root, display, damage, policy) : shared;
            CompactDiscardReadView? reader = direct ? adapter.CreateReadView() : null;
            probe.End(CompactProfilePhase.SolverSetup, setup);
            int beforeThreat = evaluator.ThreatKeys;
            ResumableDiscardProgram lane = adapter.Program;
            var mark = lane.State.Mark();
            List<(ResumableDiscardProgram.Candidate Handle, SimulationSnapshot Evaluation)> retained = new(cases.Count);
            int index = 0;
            try
            {
                lane.Begin(played); lane.Run();
                Walk(lane, [], (program, _) =>
                {
                    var freeze = probe.Begin();
                    var handle = program.Freeze();
                    probe.End(CompactProfilePhase.Freeze, freeze);
                    CombatPredictionSimulator? projection = direct ? null : adapter.Materialize(program, probe);
                    var snapshot = probe.Begin();
                    reader?.Read(program);
                    SimulationSnapshot value = reader is null ? Release(evaluator.Evaluate(projection!))
                        : evaluator.Evaluate(reader);
                    probe.End(CompactProfilePhase.Snapshot, snapshot);
                    if (verify) AssertCompactEvaluation(cases[index].Evaluation, value);
                    retained.Add((handle, value));
                    index++;
                });
                var sort = probe.Begin();
                retained.Sort((a, b) => CompareCompactEvaluation(a.Evaluation, b.Evaluation));
                foreach (var item in retained) checksum ^= (long)item.Evaluation.StateKey.First;
                probe.End(CompactProfilePhase.Sort, sort);
                if (index != cases.Count) throw new InvalidOperationException("Profile omitted a compact leaf.");
                if (verify)
                {
                    var expected = cases.Select(c => c.Evaluation).ToList();
                    expected.Sort(CompareCompactEvaluation);
                    for (int i = 0; i < expected.Count; i++) AssertCompactEvaluation(expected[i], retained[i].Evaluation);
                }
            }
            finally { lane.State.Rollback(mark); }
            metrics.DrainFrom(evaluator.Metrics);
            return evaluator.ThreatKeys - beforeThreat;
        }

        // Verify both cache policies against the full oracle outside timed samples. Each new
        // solver still sees all 34 siblings, including their legitimate duplicate-state hits.
        var warmMetrics = new SearchPerformanceMetrics(true);
        var warmProbe = new CompactPhaseProbe();
        int warmUniqueKeys = Batch(false, warmProbe, warmMetrics, true);
        int freshUniqueKeys = Batch(true, warmProbe, warmMetrics, true);
        if (warmUniqueKeys != freshUniqueKeys || warmUniqueKeys <= 0)
            throw new InvalidOperationException("Fresh/warm cache-key populations differ.");
        Batch(false, warmProbe, warmMetrics, true, true);
        Batch(true, warmProbe, warmMetrics, true, true);
        for (int sample = 0; sample < 4; sample++)
        {
            foreach (bool fresh in sample % 2 == 0 ? new[] { false, true } : new[] { true, false })
            foreach (bool direct in sample % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                EnsureWithinDeadline();
                var probe = new CompactPhaseProbe();
                var metrics = new SearchPerformanceMetrics(true);
                long before = GC.GetAllocatedBytesForCurrentThread();
                double cpuBefore = CompactThreadCpu.Milliseconds();
                long wallBefore = Stopwatch.GetTimestamp();
                int threatNewKeys = 0;
                for (int i = 0; i < iterations; i++) threatNewKeys += Batch(fresh, probe, metrics, false, direct);
                double elapsed = Stopwatch.GetElapsedTime(wallBefore).TotalMilliseconds;
                double cpu = CompactThreadCpu.Milliseconds() - cpuBefore;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (threatNewKeys != (fresh ? freshUniqueKeys * iterations : 0))
                    throw new InvalidOperationException("Profile cache policy changed during measurement.");
                samples.Add(new { backend = direct ? "completed_read_view" : "legacy_projection", mode = fresh ? "fresh_solver_per_expansion" : "warm_shared_solver", sample,
                    iterations, leaves = cases.Count, cpuMilliseconds = cpu, elapsedMilliseconds = elapsed,
                    allocatedBytes = allocated, threatNewKeys, phases = probe.Rows(),
                    snapshotSubphases = Enum.GetValues<SearchMetricPhase>()
                        .Select(phase => new { phase, value = metrics.Snapshot(phase) })
                        .Where(item => item.value.Elapsed != TimeSpan.Zero || item.value.AllocatedBytes != 0)
                        .Select(item => new { phase = item.phase.ToString(),
                            elapsedMilliseconds = item.value.Elapsed.TotalMilliseconds, item.value.AllocatedBytes }).ToArray() });
            }
        }
        // Report the instrumentation floor; do not silently subtract it from tiny phases.
        var empty = new CompactPhaseProbe();
        for (int i = 0; i < 10_000; i++) empty.End(CompactProfilePhase.EmptyProbe, empty.Begin());
        var evidence = new { schemaVersion = 1,
            scope = "34-leaf closed prototype; diagnostic profile, no production search or performance claim",
            cpuClock = "current-thread CPU", nestedClock = "Stopwatch wall time, inclusive and overlapping",
            cachePolicy = "fresh solver per 34-leaf expansion versus prewarmed solver; root/model/type caches stay warm in both",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            tieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            perfMapEnvironment = Environment.GetEnvironmentVariable("DOTNET_PerfMapEnabled"),
            simulationIsolationActive = SimulationNotificationIsolation.IsActive, emptyCapabilityEligibleCards,
            fullPropertiesAndSortedOrderVerified = true, directReadViewVerified = true,
            readViewSetup = "one reader and isolated history-metadata fork per 34-leaf expansion, included in SolverSetup", leaves = cases.Count, uniqueThreatKeys = freshUniqueKeys,
            sharedCoverageKeys = shared.CoverageKeys, sharedThreatKeys = shared.ThreatKeys,
            samples, emptyProbe = empty.Rows().Single(r => r.Phase == nameof(CompactProfilePhase.EmptyProbe)), checksum };
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-evaluation-profile.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        _completedChecks.Add($"CompactEvaluationProfile:{cases.Count}Leaves:FullPropertiesAndSortedOrder:WarmAndFreshSolver");
    }
}
