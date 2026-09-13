using CombatSolver;

CombatBeamSolver.Check();

namespace CombatSolver
{
partial class CombatBeamSolver
{
    private readonly Run _run = new();
    private readonly List<SimulationSnapshot> inputs = [];
    private bool revokeLease;
    private bool failGeneration;
    private int publishedBaselines;
    private int admissions;
    private readonly OwnedExpansionBatch<SimulationSnapshot, object, SearchNode>.Pool pool = new(s => s.ReleaseSimulator());
    private ExpansionBatch RentExpansionBatch() => new(pool);
    private sealed class ExpansionBatch(OwnedExpansionBatch<SimulationSnapshot, object, SearchNode>.Pool pool)
        : OwnedExpansionBatch<SimulationSnapshot, object, SearchNode>(pool)
    {
        public void AddEndTurn(SearchNode node) => base.AddEndTurn(node.Snapshot, node);
    }
    private sealed class Run
    {
        public int RepeatableNoProgressBranchesPruned;
        public Dictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> CycleFamilyLedger = [];
    }
    private record CanonicalCycleFamilyKey;
    private record CycleFamilyLedgerEntry;
    private record ActionCandidate(SearchNode Node);
    private record CrossTurnStandPatBaseline(int Key, int Quality);
    private enum SearchBoundaryReason { None, PendingChoice }
    private record PlanAction;
    private sealed class Progress { public Progress Advance(SimulationSnapshot _) => this; }
    private sealed class SimulationSnapshot
    {
        public bool PlayerDead, AllEnemiesDead, HasRisk, Prune, Reject;
        public int PotionUseCount, PotionStrategicCost, StateKey, Score;
        public int Turn = 2, PlayerMaxHp = 80;
        public SearchBoundaryReason BoundaryReason;
        public int Releases;
        public void ReleaseSimulator() { if (++Releases != 1) throw new Exception("Double release"); }
    }
    private record SearchNode(PlanAction? Action, int ActionCount, int PotionCount,
        int PotionStrategicCost, int Turn, int Traits, int FutureSoldHp, int Score,
        int StateKey, bool HasRisk, SearchBoundaryReason BoundaryReason, bool IsTerminal,
        SearchNode? Parent, SimulationSnapshot Snapshot, Progress CombatProgress)
    {
        public int CumulativeEnemyHpLost;
        public object? CycleProbeLease, PendingCycleExitObservation, CycleExitProbe;
    }
    private IEnumerable<(PlanAction, SimulationSnapshot)> BuildEndTurnBranches(SearchNode _, object[] choices)
    {
        foreach (var snapshot in inputs)
        {
            if (failGeneration && snapshot == inputs[1]) throw new InvalidOperationException("injected generation failure");
            yield return (new(), snapshot);
        }
    }
    private SearchNode AttachCycleSchedulingEvidence(SearchNode child)
    {
        if (child.Parent!.CycleProbeLease != null && !child.IsTerminal)
            child.PendingCycleExitObservation = new();
        return child;
    }
    private void PromoteOrderedMutationProgressTail(SearchNode child)
    {
        if (revokeLease) child.Parent!.CycleProbeLease = null;
    }
    private static void CommitCycleExitObservation(SearchNode _) { }
    private static bool ShouldPruneCrossTurnNoProgress(SearchNode node) => node.Snapshot.Prune;
    private bool TryAcceptTransposition(SearchNode node)
    {
        if (node.PendingCycleExitObservation != null)
            throw new InvalidOperationException("Pending cycle exit crossed end-turn admission");
        admissions++;
        return !node.Snapshot.Reject;
    }
    private static SearchNode FindTurnStart(SearchNode node) => node;
    private static int ClassifyRoundTransitionTraits(int traits, SimulationSnapshot a, SimulationSnapshot b) => traits;
    private static int ApplySoldHpPenalty(int score, int sold) => score;
    private static int AccumulateEnemyHpLost(SearchNode node, SimulationSnapshot snapshot) => 0;
    private static bool IsComparableCrossTurnOutcome(SearchBoundaryReason reason) => reason == SearchBoundaryReason.None;
    private static int MeasureCycleExitQuality(SearchNode node, SearchNode child) => child.Score;
    private void PublishCrossTurnStandPatBaselines(SearchNode node, List<CrossTurnStandPatBaseline> baselines) => publishedBaselines = baselines.Count;
    private static void AnnotateCycleExitProgress(SearchNode node, IEnumerable<SearchNode> children) { }
    private static bool HasValidPendingCycleExitObservation(SearchNode node) => node.PendingCycleExitObservation != null && node.Parent!.CycleProbeLease != null;
    private static int ComparePendingCycleExitAdmissionCandidates(SearchNode a, SearchNode b, int maxHp) => b.Score.CompareTo(a.Score);
    private static bool TryMaterializePendingCycleExitObservation(SearchNode node, IReadOnlyDictionary<CanonicalCycleFamilyKey, CycleFamilyLedgerEntry> ledger)
    {
        node.PendingCycleExitObservation = null;
        node.CycleExitProbe = new();
        return true;
    }
    public static void Check()
    {
        int checks = 0;
        void Require(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        foreach (bool leased in new[] { true, false })
        foreach (bool revoked in new[] { false, true })
        {
            var solver = new CombatBeamSolver { revokeLease = revoked };
            for (int i = 0; i < 5; i++) solver.inputs.Add(new() { StateKey = i, Score = i });
            solver.inputs[1].Reject = true;
            solver.inputs[2].Prune = true;
            solver.inputs[3].BoundaryReason = SearchBoundaryReason.PendingChoice;
            SearchNode parent = new(null, 8, 0, 0, 1, 0, 0, 0, 0, false, SearchBoundaryReason.None, false, null, new(), new())
            { CycleProbeLease = leased ? new() : null };
            var result = solver.BuildAcceptedEndTurnNodes(parent).ToList();
            Require(result.Select(n => n.StateKey).SequenceEqual(new[] { 0, 3, 4 }), "Order/filter changed");
            Require(result.All(n => n.PendingCycleExitObservation == null), "Unsettled observation");
            Require(result.Count(n => n.CycleExitProbe != null) == (leased && !revoked ? 1 : 0), "Exit lane must be bounded to one");
            Require(solver._run.RepeatableNoProgressBranchesPruned == 1, "Prune accounting changed");
            Require(solver.publishedBaselines == 4, "Stand-pat baselines lost");
            Require(solver.inputs[1].Releases == 1 && solver.inputs[2].Releases == 1, "Rejected snapshots leaked");
            Require(result.All(n => n.Snapshot.Releases == 0), "Transferred snapshots released early");
            foreach (var node in result) node.Snapshot.ReleaseSimulator();
        }
        foreach (bool fail in new[] { false, true })
        {
            var solver = new CombatBeamSolver { failGeneration = fail };
            solver.inputs.AddRange([new(), new(), new()]);
            SearchNode parent = new(null, 0, 0, 0, 1, 0, 0, 0, 0, false, SearchBoundaryReason.None, false, null, new(), new());
            if (fail)
            {
                try { solver.BuildAcceptedEndTurnNodes(parent).ToList(); throw new Exception("Expected failure"); }
                catch (InvalidOperationException e) when (e.Message == "injected generation failure") { }
                Require(solver.inputs[0].Releases == 1, "Generation failure leaked owned snapshot");
            }
            else
            {
                using (var iterator = solver.BuildAcceptedEndTurnNodes(parent).GetEnumerator())
                    Require(iterator.MoveNext(), "Missing first result");
                Require(solver.inputs[0].Releases == 0, "Transferred result not owned by caller");
                Require(solver.inputs.Skip(1).All(s => s.Releases == 1), "Early disposal leaked siblings");
                Require(solver.publishedBaselines == 3, "Baselines must be published before yield");
                solver.inputs[0].ReleaseSimulator();
            }
        }
        {
            var solver = new CombatBeamSolver();
            solver.inputs.AddRange([new() { StateKey = 10 }, new() { StateKey = 11 }]);
            SearchNode parent = new(null, 0, 0, 0, 1, 0, 0, 0, 0, false, SearchBoundaryReason.None, false, null, new(), new());
            using var batch = solver.RentExpansionBatch();
            var baselines = solver.GenerateRawEndTurnCandidates(parent, batch, publishBaselines: false);
            Require(solver.publishedBaselines == 0 && solver.admissions == 0,
                "Independent EndTurn preparation published shared baselines or admitted children.");
            Require(batch.EndTurns.Select(n => n.StateKey).SequenceEqual(new[] { 10, 11 })
                && baselines?.Count == 2, "Independent preparation lost candidates or baseline values.");
            batch.Dispose();
            Require(solver.inputs.All(s => s.Releases == 1), "Unconsumed early EndTurn results leaked.");
        }
        Console.WriteLine($"Passed {checks} end-turn admission pipeline checks.");
    }
}
}
