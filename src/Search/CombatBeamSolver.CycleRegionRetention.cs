namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const int BaseCycleRegionAdmissionBudget = 64;
    private const int CycleRegionProgressEpochAdmissionIncrement = 32;
    private const int MaximumCycleRegionAdmissionBudget = 256;
    private const int BaseCycleRegionProbeAdmissionBudget = 64;
    private const int CycleRegionProbeProgressEpochAdmissionIncrement = 32;
    private const int MaximumCycleRegionProbeAdmissionBudget = 128;
    private const int MaximumCycleRegionActionFamilyRepresentatives = 4;
    private readonly record struct CycleRegionCandidate(
        SearchNode Node,
        int LanePriority);

    private enum CycleRegionAdmissionKind : byte
    {
        Normal,
        Probe,
        Progress,
    }

    private sealed record CycleRegionBatch(
        CycleRegionKey Region,
        int CandidateCount,
        int ActionFamilyCount,
        CycleRegionLedgerEntry Ledger,
        CycleRegionLedgerEntry ObservationBaseline,
        CycleRegionLedgerEntry? CommittedLedger,
        SearchNode? ActiveProbeRepresentative,
        SearchNode? ProgressRepresentative,
        int ProgressActionsRemaining,
        IReadOnlyList<CycleRegionCandidate> Representatives,
        int AdmittedBefore,
        int DroppedBefore);

    private readonly record struct CycleRegionPendingAdmission(
        CycleRegionBatch Batch,
        CycleRegionAdmissionKind Kind,
        int Sequence);

    private sealed class CycleRegionRetentionTransaction
    {
        public readonly List<CycleRegionBatch> Batches = [];
        public readonly Dictionary<SearchNode, CycleRegionPendingAdmission> Admissions =
            new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<int, int> NormalAdmissionsByTurn;
        public readonly Dictionary<int, int> ProbeAdmissionsByTurn;

        public CycleRegionRetentionTransaction(SearchRunContext run)
        {
            NormalAdmissionsByTurn = new(run.CycleRegionAdmissionsByTurn);
            ProbeAdmissionsByTurn = new(run.CycleRegionProbeAdmissionsByTurn);
        }
    }

    private readonly record struct CycleRegionAdmissionCandidate(
        CycleRegionBatch Batch,
        SearchNode Node,
        int LanePriority,
        bool UsesProbeReserve);

    private readonly record struct CycleRegionFairnessToken(
        int Region,
        int Lane,
        int Quality);

    /// <summary>
    /// Coalesces exact cycle-sequence permutations after every deterministic coordinator prune.
    /// Exact family leases remain responsible for proving delayed payoff; this pass only decides
    /// how many nodes from one recurrent semantic region may enter the next frontier.
    /// </summary>
    private CycleRegionRetentionTransaction? ApplyCycleRegionRetention(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected)
    {
        Dictionary<CycleRegionKey, List<SearchNode>> regions = [];
        foreach (SearchNode node in pool)
        {
            if (IsCycleRegionBudgetExempt(node)
                || !TryBuildCycleRegionKey(node, out CycleRegionKey region))
            {
                continue;
            }
            if (!regions.TryGetValue(region, out List<SearchNode>? candidates))
            {
                candidates = [];
                regions.Add(region, candidates);
            }
            candidates.Add(node);
        }
        if (regions.Count == 0)
            return null;

        CycleRegionRetentionTransaction transaction = new(_run);

        // Phase one is observation only. No region may consume the run-global budget while
        // another region's candidates have not yet been classified into the same lane round.
        List<CycleRegionBatch> batches = [];
        foreach ((CycleRegionKey region, List<SearchNode> candidates) in regions
                     .OrderBy(item => item.Key.Turn)
                     .ThenBy(item => item.Key.ShapeKey.First)
                     .ThenBy(item => item.Key.ShapeKey.Second))
        {
            _run.CycleRegionCandidatesConsidered += candidates.Count;
            if (!TryGetCycleRegionLedgerForObservation(
                    _run.CycleRegionLedger,
                    _run.CycleRegionAdmissionsByTurn,
                    _run.CycleRegionProbeAdmissionsByTurn,
                    region,
                    CycleRegionGlobalAdmissionBudget(region.Turn),
                    CycleRegionGlobalProbeAdmissionBudget(),
                    out CycleRegionLedgerEntry ledger))
            {
                // No per-key dropped counter is worth retaining after both admission lanes have
                // become impossible. All independent channels settled before this final region
                // coordinator, so no later append can bypass the exhausted budgets.
                if (!candidates.Any(HasConsistentCycleDurabilityProgress))
                {
                    _run.CycleRegionCandidatesDropped = checked(
                        _run.CycleRegionCandidatesDropped + candidates.Count);
                    continue;
                }
                ledger = new CycleRegionLedgerEntry();
            }
            CycleRegionLedgerEntry? committedLedger =
                _run.CycleRegionLedger.GetValueOrDefault(region);
            ledger = CloneCycleRegionLedger(ledger);
            CycleRegionLedgerEntry observationBaseline = CloneCycleRegionLedger(ledger);
            SearchNode? progressRepresentative = FindCycleRegionDurabilityContinuation(
                candidates,
                observationBaseline,
                out int progressActionsRemaining);
            SearchNode? progressWitness = FindBestCycleRegionProgressWitness(
                candidates,
                observationBaseline);
            CycleRegionLedgerEntry provisionalObservation = CloneCycleRegionLedger(
                observationBaseline);
            ObserveCycleRegionProgress(provisionalObservation, candidates);
            // A strict witness is admitted ahead of action-family breadth. Only its eventual
            // survival can publish the provisional epoch; until then the epoch exists solely on
            // this staging ledger and may fund this batch's first post-progress slot.
            ledger.ProgressEpochs = provisionalObservation.ProgressEpochs;
            int admittedBefore = ledger.AdmittedNodes + ledger.ProbeAdmittedNodes
                + ledger.ProgressAdmittedNodes;
            int droppedBefore = ledger.DroppedNodes;

            Dictionary<SearchNode, int> representatives =
                new(ReferenceEqualityComparer.Instance);
            AddCycleRegionRepresentative(
                representatives,
                FindBestCycleRegionDurabilityCandidate(candidates),
                lanePriority: 0);
            AddCycleRegionRepresentative(
                representatives,
                FindBestCycleRegionSurvivalCandidate(candidates),
                lanePriority: 1);
            AddCycleRegionRepresentative(
                representatives,
                FindBestCycleRegionSetupCandidate(candidates),
                lanePriority: 2);
            SearchNode? activeProbeRepresentative =
                FindBestCycleRegionActiveProbeCandidate(candidates);
            AddCycleRegionRepresentative(
                representatives,
                activeProbeRepresentative,
                lanePriority: 3);
            AddCycleRegionRepresentative(
                representatives,
                progressWitness,
                // A provisional epoch can fund only its own strict witness until that witness
                // survives and publishes the turn-global extension. This lane precedes every
                // ordinary representative across all regions, preventing non-progress nodes from
                // spending another region's uncommitted +32 slots.
                lanePriority: -1);
            AddCycleRegionRepresentative(
                representatives,
                progressRepresentative,
                lanePriority: -2);

            Dictionary<StateFingerprint, SearchNode> actionFamilies = [];
            foreach (SearchNode candidate in candidates)
            {
                StateFingerprint actionFamily = BuildCycleRegionActionFamilyKey(candidate);
                if (!actionFamilies.TryGetValue(actionFamily, out SearchNode? current)
                    || CompareCycleRegionGeneralCandidates(candidate, current) < 0)
                {
                    actionFamilies[actionFamily] = candidate;
                }
            }
            _run.CycleRegionMaxActionFamilies = Math.Max(
                _run.CycleRegionMaxActionFamilies,
                actionFamilies.Count);
            foreach (SearchNode candidate in actionFamilies
                         .OrderBy(item => item.Key.First)
                         .ThenBy(item => item.Key.Second)
                         .Select(item => item.Value)
                         .OrderBy(node => node, CycleRegionGeneralComparer.Instance)
                         .Take(MaximumCycleRegionActionFamilyRepresentatives))
            {
                AddCycleRegionRepresentative(
                    representatives,
                    candidate,
                    lanePriority: 5);
            }

            batches.Add(new CycleRegionBatch(
                region,
                candidates.Count,
                actionFamilies.Count,
                ledger,
                observationBaseline,
                committedLedger,
                activeProbeRepresentative,
                progressRepresentative,
                progressActionsRemaining,
                representatives
                    .Select(item => new CycleRegionCandidate(
                        item.Key,
                        item.Value))
                    .ToArray(),
                admittedBefore,
                droppedBefore));
        }

        transaction.Batches.AddRange(batches);

        // Phase two reserves provisional nodes. Each lane is a round across every region;
        // within a round generic lane quality wins and the region key is only a final tie-break.
        // Permanent counters are untouched until incumbent and ordered atomic arbitration finish.
        List<CycleRegionAdmissionCandidate> admissionCandidates = [];
        foreach (CycleRegionBatch batch in batches)
        {
            foreach (CycleRegionCandidate representative in batch.Representatives)
            {
                admissionCandidates.Add(new CycleRegionAdmissionCandidate(
                    batch,
                    representative.Node,
                    representative.LanePriority,
                    UsesProbeReserve: ReferenceEquals(
                        representative.Node,
                        batch.ActiveProbeRepresentative)));
            }
        }

        HashSet<SearchNode> admitted = new(ReferenceEqualityComparer.Instance);
        Dictionary<SearchNode, int> admittedRanks = new(ReferenceEqualityComparer.Instance);
        Dictionary<CycleRegionKey, int> admittedByRegion = [];
        int pendingAdmissionSequence = 0;
        foreach (CycleRegionAdmissionCandidate representative in
                 OrderCycleRegionAdmissionCandidates(admissionCandidates))
        {
            // Exactly one representative per region/layer owns the independent probe
            // reserve. Every other quality/action-family representative consumes normal
            // region capacity. Ordered portfolio work is already exempt at the method boundary.
            bool progressAdmission = ReferenceEquals(
                representative.Node,
                representative.Batch.ProgressRepresentative);
            bool probeAdmissionSucceeded = !progressAdmission
                && representative.UsesProbeReserve
                && TryStageCycleRegionProbeAdmission(
                    representative.Batch,
                    transaction);
            // Probe is an independent reserve, not an exclusive node class. When that reserve is
            // full, the same representative may still spend ordinary capacity exactly once.
            bool normalAdmissionSucceeded = !progressAdmission && !probeAdmissionSucceeded
                && TryStageCycleRegionAdmission(
                    representative.Batch,
                    transaction);
            CycleRegionAdmissionKind? admissionKind = progressAdmission
                ? CycleRegionAdmissionKind.Progress
                : SelectCycleRegionAdmissionKind(
                    representative.UsesProbeReserve,
                    probeAdmissionSucceeded,
                    normalAdmissionSucceeded);
            if (representative.LanePriority == -1)
            {
                SettleCycleRegionProvisionalProgressWitness(
                    representative.Batch.Ledger,
                    representative.Batch.ObservationBaseline,
                    witnessAdmitted: admissionKind != null);
            }
            if (admissionKind is not { } committedKind)
                continue;

            if (!transaction.Admissions.TryAdd(
                    representative.Node,
                    new CycleRegionPendingAdmission(
                        representative.Batch,
                        committedKind,
                        pendingAdmissionSequence++)))
            {
                throw new InvalidOperationException(
                    "同一循环 region 节点被重复登记 provisional admission。");
            }

            int regionRank = admittedByRegion.GetValueOrDefault(representative.Batch.Region);
            admittedByRegion[representative.Batch.Region] = regionRank + 1;
            admitted.Add(representative.Node);
            admittedRanks[representative.Node] = _profile.BeamWidth + 16 + regionRank;
        }

        selected.RemoveAll(node => !IsCycleRegionBudgetExempt(node)
            && TryBuildCycleRegionKey(node, out _)
            && !admitted.Contains(node));
        HashSet<SearchNode> retained = new(selected, ReferenceEqualityComparer.Instance);
        foreach (SearchNode candidate in admitted
                     .OrderBy(node => admittedRanks[node])
                     .ThenBy(node => node, CycleRegionGeneralComparer.Instance))
        {
            candidate.CycleRetentionRank = Math.Min(
                candidate.CycleRetentionRank,
                admittedRanks[candidate]);
            if (!retained.Add(candidate))
                continue;
            // RankBest writes ranks to every candidate it examines. A region-only lane stays
            // behind ordinary retained routes while still receiving a deterministic rank.
            candidate.RetentionRank = int.MaxValue;
            candidate.LongTermResourceRetentionRank = int.MaxValue;
            selected.Add(candidate);
        }
        return transaction;
    }

    private int CompareCycleRegionAdmissionCandidates(
        CycleRegionAdmissionCandidate left,
        CycleRegionAdmissionCandidate right)
    {
        int comparison = left.LanePriority.CompareTo(right.LanePriority);
        if (comparison != 0)
            return comparison;
        comparison = left.LanePriority switch
        {
            0 => CompareCycleRegionDurabilityQuality(left.Node, right.Node),
            1 => CompareCycleRegionSurvivalQuality(left.Node, right.Node),
            2 => CompareCycleRegionSetupQuality(left.Node, right.Node),
            3 => CompareCycleRegionProbeQuality(left.Node, right.Node),
            _ => CompareCycleRegionGeneralQuality(left.Node, right.Node),
        };
        if (comparison != 0)
            return comparison;
        comparison = CompareCycleRegionKeys(left.Batch.Region, right.Batch.Region);
        return comparison != 0
            ? comparison
            : CompareCycleRegionStableCandidates(left.Node, right.Node);
    }

    private int CompareCycleRegionDurabilityQuality(SearchNode left, SearchNode right)
    {
        int comparison = TotalEnemyDurability(left.Snapshot).CompareTo(
            TotalEnemyDurability(right.Snapshot));
        if (comparison != 0)
            return comparison;
        comparison = left.Snapshot.AliveEnemyCount.CompareTo(
            right.Snapshot.AliveEnemyCount);
        return comparison != 0
            ? comparison
            : CycleRegionHealthRisk(left).CompareTo(CycleRegionHealthRisk(right));
    }

    private int CompareCycleRegionSurvivalQuality(SearchNode left, SearchNode right)
    {
        int comparison = CycleRegionHealthRisk(left).CompareTo(
            CycleRegionHealthRisk(right));
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        return comparison != 0
            ? comparison
            : UsefulDefensiveBlockReserve(right.Snapshot).CompareTo(
                UsefulDefensiveBlockReserve(left.Snapshot));
    }

    private int CompareCycleRegionSetupQuality(SearchNode left, SearchNode right)
    {
        int comparison = CycleRegionSetupValue(right.Snapshot).CompareTo(
            CycleRegionSetupValue(left.Snapshot));
        return comparison != 0
            ? comparison
            : CycleRegionHealthRisk(left).CompareTo(CycleRegionHealthRisk(right));
    }

    private static int CompareCycleRegionProbeQuality(SearchNode left, SearchNode right)
    {
        int comparison = (right.CycleExitProbe != null).CompareTo(
            left.CycleExitProbe != null);
        if (comparison != 0)
            return comparison;
        comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue).CompareTo(
            right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
        return comparison != 0
            ? comparison
            : (right.CycleProbeLease?.NextActionIndex ?? -1).CompareTo(
                left.CycleProbeLease?.NextActionIndex ?? -1);
    }

    private static int CompareCycleRegionKeys(CycleRegionKey left, CycleRegionKey right)
    {
        int comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ShapeKey.First.CompareTo(right.ShapeKey.First);
        return comparison != 0
            ? comparison
            : left.ShapeKey.Second.CompareTo(right.ShapeKey.Second);
    }

    private static IEnumerable<T> OrderCycleRegionRoundRobin<T, TRegion>(
        IEnumerable<T> candidates,
        Func<T, int> laneSelector,
        Func<T, TRegion> regionSelector,
        Comparison<T> withinLaneComparison,
        Func<int, IEnumerable<T>, List<T>>? orderRegionQueue = null)
        where TRegion : notnull
    {
        foreach (IGrouping<int, T> lane in candidates
                     .GroupBy(laneSelector)
                     .OrderBy(group => group.Key))
        {
            List<List<T>> regionQueues = lane
                .GroupBy(regionSelector)
                .Select(group =>
                {
                    if (orderRegionQueue != null)
                        return orderRegionQueue(lane.Key, group);
                    List<T> ordered = group.ToList();
                    ordered.Sort(withinLaneComparison);
                    return ordered;
                })
                .ToList();
            int rounds = regionQueues.Count == 0
                ? 0
                : regionQueues.Max(queue => queue.Count);
            for (int round = 0; round < rounds; round++)
            {
                List<T> roundCandidates = [];
                foreach (List<T> queue in regionQueues)
                {
                    if (round < queue.Count)
                        roundCandidates.Add(queue[round]);
                }
                roundCandidates.Sort(withinLaneComparison);
                foreach (T candidate in roundCandidates)
                    yield return candidate;
            }
        }
    }

    private List<CycleRegionAdmissionCandidate> OrderCycleRegionAdmissionCandidates(
        IReadOnlyList<CycleRegionAdmissionCandidate> candidates)
        => OrderCycleRegionRoundRobin(
                candidates,
                candidate => candidate.LanePriority,
                candidate => candidate.Batch.Region,
                CompareCycleRegionAdmissionCandidates)
            .ToList();

    private static bool TryGetCycleRegionLedgerForObservation(
        IDictionary<CycleRegionKey, CycleRegionLedgerEntry> ledgers,
        IDictionary<int, int> normalAdmissionsByTurn,
        IDictionary<int, int> probeAdmissionsByTurn,
        CycleRegionKey region,
        int normalBudget,
        int probeBudget,
        out CycleRegionLedgerEntry ledger)
    {
        if (ledgers.TryGetValue(region, out ledger!))
            return true;

        _ = normalAdmissionsByTurn.TryGetValue(region.Turn, out int normalAdmissions);
        _ = probeAdmissionsByTurn.TryGetValue(region.Turn, out int probeAdmissions);
        if (normalAdmissions >= normalBudget && probeAdmissions >= probeBudget)
            return false;

        // Observation happens before the cross-region fairness pass. Keep a new ledger local
        // to this batch until one representative actually consumes a normal/probe admission;
        // otherwise one wide layer could persist O(frontier) never-admitted region keys.
        ledger = new CycleRegionLedgerEntry();
        return true;
    }

    private static CycleRegionLedgerEntry CloneCycleRegionLedger(
        CycleRegionLedgerEntry source)
        => new()
        {
            AdmittedNodes = source.AdmittedNodes,
            ProbeAdmittedNodes = source.ProbeAdmittedNodes,
            ProgressAdmittedNodes = source.ProgressAdmittedNodes,
            ProgressContinuationNode = source.ProgressContinuationNode,
            ProgressActionsRemaining = source.ProgressActionsRemaining,
            DroppedNodes = source.DroppedNodes,
            ProgressEpochs = source.ProgressEpochs,
            HasObservation = source.HasObservation,
            EnemyDurabilityFloor = source.EnemyDurabilityFloor,
            BestHealthRisk = source.BestHealthRisk,
            BestProjectedPlayerHp = source.BestProjectedPlayerHp,
            BestUsefulBlock = source.BestUsefulBlock,
            BestSetupValue = source.BestSetupValue,
        };

    private static void SettleCycleRegionProvisionalProgressWitness(
        CycleRegionLedgerEntry provisional,
        CycleRegionLedgerEntry observationBaseline,
        bool witnessAdmitted)
    {
        if (!witnessAdmitted)
            provisional.ProgressEpochs = observationBaseline.ProgressEpochs;
    }

    private static void CommitCycleRegionObservation(
        CycleRegionLedgerEntry source,
        CycleRegionLedgerEntry destination)
    {
        destination.ProgressEpochs = source.ProgressEpochs;
        destination.HasObservation = source.HasObservation;
        destination.EnemyDurabilityFloor = source.EnemyDurabilityFloor;
        destination.BestHealthRisk = source.BestHealthRisk;
        destination.BestProjectedPlayerHp = source.BestProjectedPlayerHp;
        destination.BestUsefulBlock = source.BestUsefulBlock;
        destination.BestSetupValue = source.BestSetupValue;
    }

    private void FinalizeCycleRegionRetention(
        CycleRegionRetentionTransaction? transaction,
        List<SearchNode> retained)
    {
        if (transaction == null)
            return;

        HashSet<SearchNode> retainedSet = new(
            retained,
            ReferenceEqualityComparer.Instance);
        (SearchNode Node, CycleRegionPendingAdmission Admission)[] survivors =
            transaction.Admissions
                .Where(item => retainedSet.Contains(item.Key))
                .Select(item => (item.Key, item.Value))
                .OrderBy(item => item.Value.Sequence)
                .ToArray();

        if (survivors.Any(item =>
                item.Admission.Kind == CycleRegionAdmissionKind.Probe
                && !HasActiveCycleRegionProbe(item.Node)))
        {
            throw new InvalidOperationException(
                "循环 region probe provisional admission 在 ticket 仲裁后已无有效 probe。");
        }

        HashSet<CycleRegionBatch> survivingBatches = new(
            survivors.Select(item => item.Admission.Batch),
            ReferenceEqualityComparer.Instance);
        Dictionary<CycleRegionBatch, List<SearchNode>> survivorsByBatch = new(
            ReferenceEqualityComparer.Instance);
        foreach ((SearchNode node, CycleRegionPendingAdmission admission) in survivors)
        {
            if (!survivorsByBatch.TryGetValue(
                    admission.Batch,
                    out List<SearchNode>? batchSurvivors))
            {
                batchSurvivors = [];
                survivorsByBatch.Add(admission.Batch, batchSurvivors);
            }
            batchSurvivors.Add(node);
        }
        Dictionary<CycleRegionBatch, CycleRegionLedgerEntry> committedLedgers = new(
            ReferenceEqualityComparer.Instance);

        // Commit observations before admissions. The maximum progress epoch determines the
        // turn-global normal budget, and every observation committed here belongs to a batch
        // with at least one actual final-frontier survivor.
        foreach (CycleRegionBatch batch in transaction.Batches)
        {
            if (!survivingBatches.Contains(batch))
                continue;

            CycleRegionLedgerEntry committed;
            if (batch.CommittedLedger is { } existing)
            {
                if (!_run.CycleRegionLedger.TryGetValue(
                        batch.Region,
                        out CycleRegionLedgerEntry? current)
                    || !ReferenceEquals(existing, current))
                {
                    throw new InvalidOperationException(
                        "循环 region provisional transaction 的原账本发生了替换。");
                }
                committed = existing;
            }
            else
            {
                committed = new CycleRegionLedgerEntry();
                _run.CycleRegionLedger.Add(batch.Region, committed);
                _run.CycleRegionsDetected++;
            }

            // Provisional candidates which disappeared in later arbitration must not mint an
            // observation epoch. Recompute from the immutable pre-batch observation baseline;
            // the staging ledger may contain a self-funded provisional epoch and is never copied.
            CycleRegionLedgerEntry finalObservation = CloneCycleRegionLedger(
                batch.ObservationBaseline);
            ObserveCycleRegionProgress(finalObservation, survivorsByBatch[batch]);
            int priorProgressEpochs = committed.ProgressEpochs;
            CommitCycleRegionObservation(finalObservation, committed);
            int earnedProgressEpochs = checked(
                committed.ProgressEpochs - priorProgressEpochs);
            if (earnedProgressEpochs < 0)
            {
                throw new InvalidOperationException(
                    "循环 region provisional observation 回退了 progress epoch。");
            }
            _run.CycleRegionProgressEpochs = checked(
                _run.CycleRegionProgressEpochs + earnedProgressEpochs);
            ObserveCycleRegionMaximumProgressEpoch(
                _run.CycleRegionMaxProgressEpochsByTurn,
                batch.Region.Turn,
                committed.ProgressEpochs);
            committedLedgers.Add(batch, committed);
        }

        Dictionary<CycleRegionBatch, int> admittedByBatch = new(
            ReferenceEqualityComparer.Instance);
        foreach ((SearchNode node, CycleRegionPendingAdmission admission) in survivors)
        {
            CycleRegionBatch batch = admission.Batch;
            CycleRegionLedgerEntry ledger = committedLedgers[batch];
            bool committed = admission.Kind switch
            {
                CycleRegionAdmissionKind.Normal =>
                    TryConsumeCycleRegionAdmissionForTurn(
                        ledger,
                        _run.CycleRegionAdmissionsByTurn,
                        batch.Region.Turn,
                        CycleRegionGlobalAdmissionBudget(batch.Region.Turn)),
                CycleRegionAdmissionKind.Probe =>
                    TryConsumeCycleRegionProbeAdmissionForTurn(
                        ledger,
                        _run.CycleRegionProbeAdmissionsByTurn,
                        batch.Region.Turn,
                        CycleRegionGlobalProbeAdmissionBudget()),
                CycleRegionAdmissionKind.Progress => true,
                _ => throw new ArgumentOutOfRangeException(),
            };
            if (!committed)
            {
                throw new InvalidOperationException(
                    "循环 region provisional admission 在最终提交时超出硬预算。");
            }

            _run.CycleRegionCandidatesAdmitted = checked(
                _run.CycleRegionCandidatesAdmitted + 1);
            admittedByBatch[batch] = checked(
                admittedByBatch.GetValueOrDefault(batch) + 1);
            switch (admission.Kind)
            {
                case CycleRegionAdmissionKind.Normal:
                    _run.CycleRegionPortfolioNodesConsumed = checked(
                        _run.CycleRegionPortfolioNodesConsumed + 1);
                    break;
                case CycleRegionAdmissionKind.Probe:
                    _run.CycleRegionProbeCandidatesAdmitted = checked(
                        _run.CycleRegionProbeCandidatesAdmitted + 1);
                    break;
                case CycleRegionAdmissionKind.Progress:
                    if (node.CycleExitProbe is { } completedProbe)
                    {
                        // The admitted route has demonstrated a productive cycle or is paying
                        // its exact remaining phase credit. Its old uncertain-exit ticket must
                        // not subsequently stop ordinary expansion at a probe-only budget.
                        completedProbe.OriginTracker.CompleteExitProbe(
                            completedProbe.OriginPhaseIndex,
                            completedProbe.ExitActionKey,
                            completedProbe.OriginGeneration);
                        node.CycleExitProbe = null;
                        node.CycleExitRetentionRank = int.MaxValue;
                    }
                    ledger.ProgressAdmittedNodes = checked(ledger.ProgressAdmittedNodes + 1);
                    ledger.ProgressContinuationNode = batch.ProgressActionsRemaining > 0
                        && batch.ProgressRepresentative is { } continuation
                        ? new WeakReference<SearchNode>(continuation)
                        : null;
                    ledger.ProgressActionsRemaining = batch.ProgressActionsRemaining;
                    _run.CycleRegionProgressCandidatesAdmitted = checked(
                        _run.CycleRegionProgressCandidatesAdmitted + 1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        foreach (CycleRegionBatch batch in transaction.Batches)
        {
            int regionAdmitted = admittedByBatch.GetValueOrDefault(batch);
            int regionDropped = checked(batch.CandidateCount - regionAdmitted);
            _run.CycleRegionCandidatesDropped = checked(
                _run.CycleRegionCandidatesDropped + regionDropped);

            CycleRegionLedgerEntry? ledger = committedLedgers.GetValueOrDefault(batch)
                ?? batch.CommittedLedger;
            if (ledger == null)
                continue;
            if (batch.ProgressRepresentative == null
                || !retainedSet.Contains(batch.ProgressRepresentative))
            {
                ledger.ProgressContinuationNode = null;
                ledger.ProgressActionsRemaining = 0;
            }
            ledger.DroppedNodes = checked(ledger.DroppedNodes + regionDropped);
            int admittedAfter = ledger.AdmittedNodes + ledger.ProbeAdmittedNodes
                + ledger.ProgressAdmittedNodes;
            if (_detailedDiagnostics
                && (CycleRegionTelemetryMagnitude(batch.AdmittedBefore)
                        != CycleRegionTelemetryMagnitude(admittedAfter)
                    || CycleRegionTelemetryMagnitude(batch.DroppedBefore)
                        != CycleRegionTelemetryMagnitude(ledger.DroppedNodes)))
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Debug] CYCLE_REGION turn={batch.Region.Turn} " +
                    $"shape={batch.Region.ShapeKey.First:x16}{batch.Region.ShapeKey.Second:x16} " +
                    $"batch={batch.CandidateCount} admitted_batch={regionAdmitted} " +
                    $"normal_admitted={ledger.AdmittedNodes} " +
                    $"probe_admitted={ledger.ProbeAdmittedNodes} " +
                    $"progress_admitted={ledger.ProgressAdmittedNodes} " +
                    $"dropped={ledger.DroppedNodes} " +
                    $"normal_budget={CycleRegionAdmissionBudget(ledger.ProgressEpochs)} " +
                    $"normal_global_budget={CycleRegionGlobalAdmissionBudget(batch.Region.Turn)} " +
                    $"probe_budget={CycleRegionProbeAdmissionBudget(ledger.ProgressEpochs)} " +
                    $"progress_epochs={ledger.ProgressEpochs} " +
                    $"action_families={batch.ActionFamilyCount} " +
                    $"run_normal={_run.CycleRegionPortfolioNodesConsumed} " +
                    $"run_probe={_run.CycleRegionProbeCandidatesAdmitted} " +
                    "phase=committed");
            }
        }
    }

    private void ObserveCycleRegionProgress(
        CycleRegionLedgerEntry ledger,
        IReadOnlyList<SearchNode> candidates)
    {
        SearchNode first = candidates[0];
        EnemyDurabilityVector enemyFloor = first.Snapshot.EnemyDurabilityByCombatId;
        long bestHealthRisk = CycleRegionHealthRisk(first);
        int bestProjectedHp = first.Snapshot.ProjectedPlayerHp;
        int bestUsefulBlock = UsefulDefensiveBlockReserve(first.Snapshot);
        long bestSetupValue = CycleRegionSetupValue(first.Snapshot);
        for (int index = 1; index < candidates.Count; index++)
        {
            SearchNode candidate = candidates[index];
            enemyFloor = EnemyDurabilityProgress.MergeMinimum(
                enemyFloor,
                candidate.Snapshot.EnemyDurabilityByCombatId,
                out _);
            bestHealthRisk = Math.Min(bestHealthRisk, CycleRegionHealthRisk(candidate));
            bestProjectedHp = Math.Max(
                bestProjectedHp,
                candidate.Snapshot.ProjectedPlayerHp);
            bestUsefulBlock = Math.Max(
                bestUsefulBlock,
                UsefulDefensiveBlockReserve(candidate.Snapshot));
            bestSetupValue = Math.Max(
                bestSetupValue,
                CycleRegionSetupValue(candidate.Snapshot));
        }

        if (!ledger.HasObservation)
        {
            ledger.HasObservation = true;
            ledger.EnemyDurabilityFloor = enemyFloor;
            ledger.BestHealthRisk = bestHealthRisk;
            ledger.BestProjectedPlayerHp = bestProjectedHp;
            ledger.BestUsefulBlock = bestUsefulBlock;
            ledger.BestSetupValue = bestSetupValue;
            return;
        }

        ledger.EnemyDurabilityFloor = EnemyDurabilityProgress.MergeMinimum(
            ledger.EnemyDurabilityFloor,
            enemyFloor,
            out bool enemyDurabilityImproved);
        bool improved = enemyDurabilityImproved
            || bestHealthRisk < ledger.BestHealthRisk
            || bestProjectedHp > ledger.BestProjectedPlayerHp
            || bestUsefulBlock > ledger.BestUsefulBlock
            || bestSetupValue > ledger.BestSetupValue;
        ledger.BestHealthRisk = Math.Min(ledger.BestHealthRisk, bestHealthRisk);
        ledger.BestProjectedPlayerHp = Math.Max(
            ledger.BestProjectedPlayerHp,
            bestProjectedHp);
        ledger.BestUsefulBlock = Math.Max(ledger.BestUsefulBlock, bestUsefulBlock);
        ledger.BestSetupValue = Math.Max(ledger.BestSetupValue, bestSetupValue);
        if (!improved)
            return;
        ledger.ProgressEpochs = checked(ledger.ProgressEpochs + 1);
    }

    private static bool HasConsistentCycleDurabilityProgress(SearchNode node)
        => node.Cycle is
        {
            HasNewEnemyDurabilityProgress: true,
            HasConsistentDamagePhases: true,
            PeriodActions: > 0 and <= MaximumDetectedCyclePeriodActions,
        };

    private SearchNode? FindCycleRegionDurabilityContinuation(
        IReadOnlyList<SearchNode> candidates,
        CycleRegionLedgerEntry baseline,
        out int remainingActions)
    {
        SearchNode? best = null;
        foreach (SearchNode candidate in candidates)
        {
            if (!HasConsistentCycleDurabilityProgress(candidate))
                continue;
            if (baseline.HasObservation)
            {
                _ = EnemyDurabilityProgress.MergeMinimum(
                    baseline.EnemyDurabilityFloor,
                    candidate.Snapshot.EnemyDurabilityByCombatId,
                    out bool freshDurabilityProgress);
                if (!freshDurabilityProgress)
                    continue;
            }
            if (best == null || CompareCycleRegionProgressCandidates(candidate, best) < 0)
                best = candidate;
        }
        if (best != null)
        {
            remainingActions = CycleRegionProgressPhaseAllowance(best.Cycle!.PeriodActions);
            return best;
        }

        // Fresh all-time progress buys only the remaining quiet phases of its certified
        // period. Only one exact descendant receives the credit; siblings cannot copy it or
        // renew it with a recovery to previously observed enemy durability.
        foreach (SearchNode candidate in candidates)
        {
            if (!CanContinueCycleRegionProgressPhase(
                    candidate.Parent,
                    baseline.ProgressContinuationNode,
                    baseline.ProgressActionsRemaining))
            {
                continue;
            }
            if (best == null || CompareCycleRegionProgressCandidates(candidate, best) < 0)
                best = candidate;
        }
        remainingActions = best == null ? 0 : baseline.ProgressActionsRemaining - 1;
        return best;
    }

    private int CompareCycleRegionProgressCandidates(SearchNode left, SearchNode right)
    {
        int comparison = CycleRegionHealthRisk(left).CompareTo(CycleRegionHealthRisk(right));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        return comparison != 0 ? comparison : CompareCycleRegionGeneralCandidates(left, right);
    }

    private static int CycleRegionProgressPhaseAllowance(int periodActions)
        => Math.Clamp(periodActions - 1, 0, MaximumDetectedCyclePeriodActions - 1);

    private static bool CanContinueCycleRegionProgressPhase(
        SearchNode? parent,
        WeakReference<SearchNode>? continuationNode,
        int remainingActions)
        // A usable candidate owns its Parent strongly throughout this identity comparison;
        // GC can clear the weak target only after no such continuation remains reachable.
        => parent != null
            && continuationNode != null
            && remainingActions > 0
            && continuationNode.TryGetTarget(out SearchNode? expectedParent)
            && ReferenceEquals(parent, expectedParent);

    private SearchNode? FindBestCycleRegionProgressWitness(
        IReadOnlyList<SearchNode> candidates,
        CycleRegionLedgerEntry baseline)
    {
        if (!baseline.HasObservation)
            return null;

        SearchNode? best = null;
        foreach (SearchNode candidate in candidates)
        {
            if (!HasStrictCycleRegionProgress(candidate, baseline)
                || best != null
                    && CompareCycleRegionGeneralCandidates(candidate, best) >= 0)
            {
                continue;
            }
            best = candidate;
        }
        return best;
    }

    private bool HasStrictCycleRegionProgress(
        SearchNode candidate,
        CycleRegionLedgerEntry baseline)
    {
        _ = EnemyDurabilityProgress.MergeMinimum(
            baseline.EnemyDurabilityFloor,
            candidate.Snapshot.EnemyDurabilityByCombatId,
            out bool enemyDurabilityImproved);
        return enemyDurabilityImproved
            || CycleRegionHealthRisk(candidate) < baseline.BestHealthRisk
            || candidate.Snapshot.ProjectedPlayerHp > baseline.BestProjectedPlayerHp
            || UsefulDefensiveBlockReserve(candidate.Snapshot) > baseline.BestUsefulBlock
            || CycleRegionSetupValue(candidate.Snapshot) > baseline.BestSetupValue;
    }

    private long CycleRegionHealthRisk(SearchNode node)
        => (long)node.Snapshot.CumulativePlayerHpLost
            + node.FutureSoldHp
            + Math.Max(0, root.InitialPlayerMaxHp - node.Snapshot.PlayerMaxHp);

    private static long CycleRegionSetupValue(SimulationSnapshot snapshot)
        => (long)snapshot.PersistentBuffValue
            + snapshot.StrategicEffects.RetentionValue
            + snapshot.LatentSetupValue
            + snapshot.FutureResourceValue
            + snapshot.LongTermResourceValue
            + snapshot.ReplayPotentialValue
            + snapshot.RetainedAttackValue
            + snapshot.OffensiveProgressValue
            + snapshot.DelayedDamageValue
            + snapshot.ReactiveDamageValue;

    private static long TotalEnemyDurability(SimulationSnapshot snapshot)
    {
        long total = 0;
        EnemyDurabilityVector vector = snapshot.EnemyDurabilityByCombatId;
        for (int index = 0; index < vector.Count; index++)
            total += Math.Max(0, vector[index].Durability);
        return total;
    }

    private SearchNode FindBestCycleRegionDurabilityCandidate(
        IReadOnlyList<SearchNode> candidates)
    {
        SearchNode best = candidates[0];
        foreach (SearchNode candidate in candidates.Skip(1))
        {
            int comparison = TotalEnemyDurability(candidate.Snapshot)
                .CompareTo(TotalEnemyDurability(best.Snapshot));
            if (comparison == 0)
            {
                comparison = candidate.Snapshot.AliveEnemyCount.CompareTo(
                    best.Snapshot.AliveEnemyCount);
            }
            if (comparison == 0)
                comparison = CycleRegionHealthRisk(candidate).CompareTo(
                    CycleRegionHealthRisk(best));
            if (comparison < 0
                || comparison == 0
                    && CompareCycleRegionStableCandidates(candidate, best) < 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    private SearchNode FindBestCycleRegionSurvivalCandidate(
        IReadOnlyList<SearchNode> candidates)
    {
        SearchNode best = candidates[0];
        foreach (SearchNode candidate in candidates.Skip(1))
        {
            int comparison = CycleRegionHealthRisk(candidate).CompareTo(
                CycleRegionHealthRisk(best));
            if (comparison == 0)
            {
                comparison = best.Snapshot.ProjectedPlayerHp.CompareTo(
                    candidate.Snapshot.ProjectedPlayerHp);
            }
            if (comparison == 0)
            {
                comparison = UsefulDefensiveBlockReserve(best.Snapshot).CompareTo(
                    UsefulDefensiveBlockReserve(candidate.Snapshot));
            }
            if (comparison < 0
                || comparison == 0
                    && CompareCycleRegionStableCandidates(candidate, best) < 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    private SearchNode FindBestCycleRegionSetupCandidate(
        IReadOnlyList<SearchNode> candidates)
    {
        SearchNode best = candidates[0];
        foreach (SearchNode candidate in candidates.Skip(1))
        {
            int comparison = CycleRegionSetupValue(best.Snapshot).CompareTo(
                CycleRegionSetupValue(candidate.Snapshot));
            if (comparison == 0)
                comparison = CycleRegionHealthRisk(candidate).CompareTo(
                    CycleRegionHealthRisk(best));
            if (comparison < 0
                || comparison == 0
                    && CompareCycleRegionStableCandidates(candidate, best) < 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    private static SearchNode? FindBestCycleRegionActiveProbeCandidate(
        IReadOnlyList<SearchNode> candidates)
    {
        SearchNode? best = null;
        foreach (SearchNode candidate in candidates)
        {
            if (!HasActiveCycleRegionProbe(candidate))
                continue;
            if (best == null || CompareCycleRegionProbeCandidates(candidate, best) < 0)
                best = candidate;
        }
        return best;
    }

    private static int CompareCycleRegionProbeCandidates(SearchNode left, SearchNode right)
    {
        int comparison = (right.CycleExitProbe != null).CompareTo(
            left.CycleExitProbe != null);
        if (comparison != 0)
            return comparison;
        comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue).CompareTo(
            right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
        if (comparison != 0)
            return comparison;
        comparison = (right.CycleProbeLease?.NextActionIndex ?? -1).CompareTo(
            left.CycleProbeLease?.NextActionIndex ?? -1);
        return comparison != 0
            ? comparison
            : CompareCycleRegionStableCandidates(left, right);
    }

    private static int CompareCycleRegionGeneralCandidates(SearchNode left, SearchNode right)
    {
        int comparison = CompareCycleRegionGeneralQuality(left, right);
        return comparison != 0
            ? comparison
            : CompareCycleRegionStableCandidates(left, right);
    }

    private static int CompareCycleRegionGeneralQuality(SearchNode left, SearchNode right)
    {
        int comparison = (right.CycleExitProbe != null).CompareTo(
            left.CycleExitProbe != null);
        if (comparison != 0)
            return comparison;
        comparison = TotalEnemyDurability(left.Snapshot).CompareTo(
            TotalEnemyDurability(right.Snapshot));
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = CycleRegionSetupValue(right.Snapshot).CompareTo(
            CycleRegionSetupValue(left.Snapshot));
        return comparison;
    }

    private sealed class CycleRegionGeneralComparer : IComparer<SearchNode>
    {
        public static CycleRegionGeneralComparer Instance { get; } = new();

        public int Compare(SearchNode? left, SearchNode? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left == null)
                return 1;
            if (right == null)
                return -1;
            return CompareCycleRegionGeneralCandidates(left, right);
        }
    }

    private static int CompareCycleRegionStableCandidates(SearchNode left, SearchNode right)
    {
        int comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.StateKey.First.CompareTo(right.StateKey.First);
        if (comparison != 0)
            return comparison;
        comparison = left.StateKey.Second.CompareTo(right.StateKey.Second);
        if (comparison != 0)
            return comparison;
        PlanAction? leftAction = left.Action;
        PlanAction? rightAction = right.Action;
        comparison = (leftAction?.Kind ?? PlanActionKind.EndTurn).CompareTo(
            rightAction?.Kind ?? PlanActionKind.EndTurn);
        if (comparison != 0)
            return comparison;
        comparison = string.Compare(
            leftAction?.CardId,
            rightAction?.CardId,
            StringComparison.Ordinal);
        if (comparison != 0)
            return comparison;
        comparison = Nullable.Compare(
            leftAction?.TargetCombatId,
            rightAction?.TargetCombatId);
        if (comparison != 0)
            return comparison;
        comparison = string.Compare(
            leftAction?.PotionId,
            rightAction?.PotionId,
            StringComparison.Ordinal);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static void AddCycleRegionRepresentative(
        Dictionary<SearchNode, int> representatives,
        SearchNode? candidate,
        int lanePriority)
    {
        if (candidate == null)
            return;
        if (!representatives.TryGetValue(candidate, out int currentPriority)
            || lanePriority < currentPriority)
        {
            representatives[candidate] = lanePriority;
        }
    }

    private static bool TryBuildCycleRegionKey(
        SearchNode node,
        out CycleRegionKey region)
    {
        if (node.CycleExitProbe is { } exitProbe)
        {
            region = new CycleRegionKey(
                exitProbe.OriginNode.Turn,
                exitProbe.OriginShapeKey);
            return true;
        }
        if (node.CycleProbeLease is { } lease)
        {
            region = new CycleRegionKey(node.Turn, lease.Tracker.ShapeKey);
            return true;
        }
        if (node.Cycle is { } cycle)
        {
            region = new CycleRegionKey(node.Turn, cycle.ShapeKey);
            return true;
        }
        region = default;
        return false;
    }

    private static bool IsCycleRegionBudgetExempt(SearchNode node)
        => node.IsTerminal
            || node.Action is { } action
                && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
            // A paid admission may be pruned again at the immediate turn boundary without
            // paying twice. A pending admission is an active, independently hard-capped
            // ordered scheduling lane; its finalizer either charges it this prune or removes
            // it atomically. Merely carrying an inherited lease is deliberately not exempt.
            || HasActiveOrderedMutationCycleRegionAdmission(
                node.OrderedMutationAdmissionCharged,
                node.OrderedMutationAdmissionPending,
                node.OrderedMutationRetentionLease != null);

    private static bool HasActiveOrderedMutationCycleRegionAdmission(
        bool admissionCharged,
        bool admissionPending,
        bool hasLease)
        => admissionCharged || admissionPending && hasLease;

    private static bool HasActiveCycleRegionProbe(SearchNode node)
        // An exit probe without a portfolio rank is cleared by the final ticket coordinator.
        // It therefore cannot reserve the independent region-probe lane provisionally.
        => node.CycleExitProbe is { RemainingActions: > 0 }
                && node.CycleExitRetentionRank != int.MaxValue
            || node.CycleProbeLease != null;

    private static CycleRegionAdmissionKind? SelectCycleRegionAdmissionKind(
        bool prefersProbe,
        bool probeAdmissionSucceeded,
        bool normalAdmissionSucceeded)
    {
        if (prefersProbe && probeAdmissionSucceeded)
            return CycleRegionAdmissionKind.Probe;
        return normalAdmissionSucceeded
            ? CycleRegionAdmissionKind.Normal
            : null;
    }

    private static StateFingerprint BuildCycleRegionActionFamilyKey(SearchNode node)
    {
        if (node.Action is { } action)
            return BuildCycleFamilyActionKey(action);
        StateFingerprintBuilder key = new();
        key.Add('N');
        return key.Finish();
    }

    private static int CycleRegionAdmissionBudget(int progressEpochs)
        => Math.Min(
            MaximumCycleRegionAdmissionBudget,
            checked(BaseCycleRegionAdmissionBudget
                + Math.Min(6, Math.Max(0, progressEpochs))
                    * CycleRegionProgressEpochAdmissionIncrement));

    private static int CycleRegionTelemetryMagnitude(int value)
    {
        int magnitude = 0;
        for (int remaining = Math.Max(0, value); remaining > 0; remaining >>= 1)
            magnitude++;
        return magnitude;
    }

    private int CycleRegionGlobalAdmissionBudget(int turn)
        => CycleRegionGlobalAdmissionBudget(
            _profile.MaxExpandedNodes,
            _run.CycleRegionMaxProgressEpochsByTurn.GetValueOrDefault(turn));

    private int CycleRegionProvisionalGlobalAdmissionBudget(CycleRegionBatch batch)
        // A newly observed epoch may fund its own batch immediately, avoiding a deadlock when
        // the previously committed turn budget is full. It is not published to the run-global
        // maximum here, so a stagnant sibling region cannot spend uncommitted progress. Any
        // final subset which used the extension necessarily commits at least that batch's epoch.
        => CycleRegionGlobalAdmissionBudget(
            _profile.MaxExpandedNodes,
            Math.Max(
                _run.CycleRegionMaxProgressEpochsByTurn.GetValueOrDefault(
                    batch.Region.Turn),
                batch.Ledger.ProgressEpochs));

    private static int CycleRegionGlobalAdmissionBudget(
        int maxExpandedNodes,
        int maximumProgressEpochs)
    {
        // Stagnant regions retain the original turn-global cap. Proven simulator-state progress
        // may buy a few bounded windows, keyed by the largest epoch in this turn rather than a
        // sum across regions, so manufacturing many shallow shapes cannot mint extra work.
        int baseline = Math.Clamp(maxExpandedNodes / 32, 256, 512);
        int progressExtension = Math.Min(6, Math.Max(0, maximumProgressEpochs))
            * CycleRegionProgressEpochAdmissionIncrement;
        return Math.Min(512, checked(baseline + progressExtension));
    }

    private static void ObserveCycleRegionMaximumProgressEpoch(
        IDictionary<int, int> maximumProgressEpochsByTurn,
        int turn,
        int progressEpochs)
    {
        _ = maximumProgressEpochsByTurn.TryGetValue(turn, out int currentMaximum);
        if (progressEpochs > currentMaximum)
        {
            maximumProgressEpochsByTurn[turn] = progressEpochs;
        }
    }

    private static int CycleRegionProbeAdmissionBudget(int progressEpochs)
        => Math.Min(
            MaximumCycleRegionProbeAdmissionBudget,
            checked(BaseCycleRegionProbeAdmissionBudget
                + Math.Min(2, Math.Max(0, progressEpochs))
                    * CycleRegionProbeProgressEpochAdmissionIncrement));

    private int CycleRegionGlobalProbeAdmissionBudget()
        => Math.Clamp(_profile.MaxExpandedNodes / 128, 64, 256);

    private bool TryStageCycleRegionAdmission(
        CycleRegionBatch batch,
        CycleRegionRetentionTransaction transaction)
    {
        if (!TryConsumeCycleRegionAdmissionForTurn(
                batch.Ledger,
                transaction.NormalAdmissionsByTurn,
                batch.Region.Turn,
                CycleRegionProvisionalGlobalAdmissionBudget(batch)))
        {
            return false;
        }
        return true;
    }

    private bool TryStageCycleRegionProbeAdmission(
        CycleRegionBatch batch,
        CycleRegionRetentionTransaction transaction)
    {
        if (!TryConsumeCycleRegionProbeAdmissionForTurn(
                batch.Ledger,
                transaction.ProbeAdmissionsByTurn,
                batch.Region.Turn,
                CycleRegionGlobalProbeAdmissionBudget()))
        {
            return false;
        }
        return true;
    }

    private static bool TryConsumeCycleRegionAdmission(
        CycleRegionLedgerEntry ledger,
        ref int runAdmittedNodes,
        int globalBudget)
    {
        if (!CanConsumeCycleRegionAdmission(ledger, runAdmittedNodes, globalBudget))
            return false;
        ledger.AdmittedNodes++;
        runAdmittedNodes++;
        return true;
    }

    private static bool CanConsumeCycleRegionAdmission(
        CycleRegionLedgerEntry ledger,
        int runAdmittedNodes,
        int globalBudget)
        => ledger.AdmittedNodes < CycleRegionAdmissionBudget(ledger.ProgressEpochs)
            && runAdmittedNodes < globalBudget;

    private static bool TryConsumeCycleRegionAdmissionForTurn(
        CycleRegionLedgerEntry ledger,
        IDictionary<int, int> admissionsByTurn,
        int turn,
        int globalBudget)
    {
        _ = admissionsByTurn.TryGetValue(turn, out int turnAdmissions);
        if (!TryConsumeCycleRegionAdmission(
                ledger,
                ref turnAdmissions,
                globalBudget))
        {
            return false;
        }
        admissionsByTurn[turn] = turnAdmissions;
        return true;
    }

    private static bool TryConsumeCycleRegionProbeAdmission(
        CycleRegionLedgerEntry ledger,
        ref int runAdmittedProbeNodes,
        int globalBudget)
    {
        if (ledger.ProbeAdmittedNodes
                >= CycleRegionProbeAdmissionBudget(ledger.ProgressEpochs)
            || runAdmittedProbeNodes >= globalBudget)
        {
            return false;
        }
        ledger.ProbeAdmittedNodes++;
        runAdmittedProbeNodes++;
        return true;
    }

    private static bool TryConsumeCycleRegionProbeAdmissionForTurn(
        CycleRegionLedgerEntry ledger,
        IDictionary<int, int> admissionsByTurn,
        int turn,
        int globalBudget)
    {
        _ = admissionsByTurn.TryGetValue(turn, out int turnAdmissions);
        if (!TryConsumeCycleRegionProbeAdmission(
                ledger,
                ref turnAdmissions,
                globalBudget))
        {
            return false;
        }
        admissionsByTurn[turn] = turnAdmissions;
        return true;
    }

    internal static void VerifyCycleRegionRetentionPolicyForTesting()
    {
        VerifyCycleRegionProgressPhasePolicyForTesting();
        if (CycleRegionGlobalAdmissionBudget(10_000, maximumProgressEpochs: 0) != 312
            || CycleRegionGlobalAdmissionBudget(10_000, maximumProgressEpochs: 1) != 344
            || CycleRegionGlobalAdmissionBudget(10_000, maximumProgressEpochs: 6) != 504
            || CycleRegionGlobalAdmissionBudget(10_000, maximumProgressEpochs: 100) != 504
            || CycleRegionGlobalAdmissionBudget(50_000, maximumProgressEpochs: 0) != 512)
        {
            throw new InvalidOperationException(
                "循环 region 的进展扩容没有保持原停滞上限或 512 硬上限。");
        }
        Dictionary<int, int> progressByTurn = [];
        ObserveCycleRegionMaximumProgressEpoch(progressByTurn, turn: 7, progressEpochs: 1);
        ObserveCycleRegionMaximumProgressEpoch(progressByTurn, turn: 7, progressEpochs: 5);
        ObserveCycleRegionMaximumProgressEpoch(progressByTurn, turn: 7, progressEpochs: 3);
        ObserveCycleRegionMaximumProgressEpoch(progressByTurn, turn: 8, progressEpochs: 2);
        if (progressByTurn.GetValueOrDefault(7) != 5
            || progressByTurn.GetValueOrDefault(8) != 2)
        {
            throw new InvalidOperationException(
                "循环 region 全局预算错误累加了多个 region，或没有按 turn 隔离。");
        }

        StateFingerprint shape = new(0x1234UL, 0x5678UL);
        CycleRegionKey first = new(7, shape);
        CycleRegionKey sameRegion = new(7, shape);
        CycleRegionKey nextTurn = new(8, shape);
        if (first != sameRegion || first == nextTurn)
        {
            throw new InvalidOperationException(
                "循环 region key 没有仅按回合与稳定形状聚合。");
        }
        if (HasActiveOrderedMutationCycleRegionAdmission(
                admissionCharged: false,
                admissionPending: false,
                hasLease: true)
            || HasActiveOrderedMutationCycleRegionAdmission(
                admissionCharged: false,
                admissionPending: true,
                hasLease: false)
            || !HasActiveOrderedMutationCycleRegionAdmission(
                admissionCharged: false,
                admissionPending: true,
                hasLease: true)
            || !HasActiveOrderedMutationCycleRegionAdmission(
                admissionCharged: true,
                admissionPending: false,
                hasLease: false))
        {
            throw new InvalidOperationException(
                "循环 region 错把未付费 inherited lease 当成 ordered 豁免。");
        }
        if (SelectCycleRegionAdmissionKind(
                prefersProbe: true,
                probeAdmissionSucceeded: false,
                normalAdmissionSucceeded: true)
                != CycleRegionAdmissionKind.Normal
            || SelectCycleRegionAdmissionKind(
                prefersProbe: true,
                probeAdmissionSucceeded: true,
                normalAdmissionSucceeded: false)
                != CycleRegionAdmissionKind.Probe
            || SelectCycleRegionAdmissionKind(
                prefersProbe: false,
                probeAdmissionSucceeded: false,
                normalAdmissionSucceeded: true)
                != CycleRegionAdmissionKind.Normal
            || SelectCycleRegionAdmissionKind(
                prefersProbe: true,
                probeAdmissionSucceeded: false,
                normalAdmissionSucceeded: false) != null)
        {
            throw new InvalidOperationException(
                "循环 region active probe 在 probe 满额时没有回退普通额度。");
        }

        CycleRegionLedgerEntry committedLedger = new();
        CycleRegionLedgerEntry provisionalLedger = CloneCycleRegionLedger(
            committedLedger);
        Dictionary<int, int> committedAdmissions = [];
        Dictionary<int, int> provisionalAdmissions = new(committedAdmissions);
        if (!TryConsumeCycleRegionAdmissionForTurn(
                provisionalLedger,
                provisionalAdmissions,
                turn: 7,
                globalBudget: 512)
            || !TryConsumeCycleRegionAdmissionForTurn(
                provisionalLedger,
                provisionalAdmissions,
                turn: 7,
                globalBudget: 512)
            || committedLedger.AdmittedNodes != 0
            || committedAdmissions.Count != 0)
        {
            throw new InvalidOperationException(
                "循环 region provisional admission 在最终 frontier 前污染了永久账本。");
        }
        // Simulate incumbent/atomic arbitration retaining only one of two provisional nodes.
        // Only that actual survivor may reach the permanent ledger.
        if (!TryConsumeCycleRegionAdmissionForTurn(
                committedLedger,
                committedAdmissions,
                turn: 7,
                globalBudget: 512)
            || committedLedger.AdmittedNodes != 1
            || committedAdmissions.GetValueOrDefault(7) != 1)
        {
            throw new InvalidOperationException(
                "循环 region 最终 survivor 没有按实际存活数量对账。");
        }

        CycleRegionLedgerEntry ledger = new();
        int runAdmitted = 0;
        for (int index = 0; index < BaseCycleRegionAdmissionBudget; index++)
        {
            if (!TryConsumeCycleRegionAdmission(ledger, ref runAdmitted, globalBudget: 512))
                throw new InvalidOperationException("循环 region 基础实际入选预算提前耗尽。");
        }
        if (TryConsumeCycleRegionAdmission(ledger, ref runAdmitted, globalBudget: 512))
            throw new InvalidOperationException("停滞循环 region 超出了基础实际入选预算。");

        ledger.ProgressEpochs = 1;
        for (int index = 0; index < CycleRegionProgressEpochAdmissionIncrement; index++)
        {
            if (!TryConsumeCycleRegionAdmission(ledger, ref runAdmitted, globalBudget: 512))
                throw new InvalidOperationException("循环 region 进展 epoch 没有扩展预算。");
        }
        ledger.ProgressEpochs = int.MaxValue;
        while (TryConsumeCycleRegionAdmission(ledger, ref runAdmitted, globalBudget: 512))
        {
        }
        if (ledger.AdmittedNodes != MaximumCycleRegionAdmissionBudget
            || runAdmitted != MaximumCycleRegionAdmissionBudget)
        {
            throw new InvalidOperationException("循环 region 没有遵守最终实际入选硬上限。");
        }

        CycleRegionLedgerEntry competingLedger = new();
        int runProbeAdmitted = 0;
        int competingPortfolioAdmitted = 0;
        for (int layer = 0; layer < MaximumDetectedCyclePeriodActions; layer++)
        {
            if (!TryConsumeCycleRegionProbeAdmission(
                    competingLedger,
                    ref runProbeAdmitted,
                    globalBudget: BaseCycleRegionProbeAdmissionBudget))
            {
                throw new InvalidOperationException(
                    "循环 region 的独立 probe 保留不足以完成最大周期的一轮。");
            }
            // Simulate seven other leased/quality representatives in the same layer. Only the
            // single chosen active-probe slot above is entitled to the reserve; these siblings
            // exhaust the ordinary base budget without shortening that 32-layer continuation.
            for (int sibling = 0; sibling < 7; sibling++)
            {
                _ = TryConsumeCycleRegionAdmission(
                    competingLedger,
                    ref competingPortfolioAdmitted,
                    globalBudget: 512);
            }
        }
        if (competingLedger.ProbeAdmittedNodes != MaximumDetectedCyclePeriodActions
            || competingLedger.AdmittedNodes != BaseCycleRegionAdmissionBudget
            || ledger.AdmittedNodes != MaximumCycleRegionAdmissionBudget)
        {
            throw new InvalidOperationException(
                "循环 region 的 probe 与普通 portfolio 没有独立计费。");
        }

        const int testTurnAdmissionBudget = 256;
        Dictionary<int, int> normalAdmissionsByTurn = [];
        for (int regionIndex = 0;
             regionIndex < testTurnAdmissionBudget / BaseCycleRegionAdmissionBudget;
             regionIndex++)
        {
            CycleRegionLedgerEntry turnOneRegion = new();
            for (int admission = 0;
                 admission < BaseCycleRegionAdmissionBudget;
                 admission++)
            {
                if (!TryConsumeCycleRegionAdmissionForTurn(
                        turnOneRegion,
                        normalAdmissionsByTurn,
                        turn: 1,
                        globalBudget: testTurnAdmissionBudget))
                {
                    throw new InvalidOperationException(
                        "同回合循环 region 的全局预算提前耗尽。");
                }
            }
        }
        if (TryConsumeCycleRegionAdmissionForTurn(
                new CycleRegionLedgerEntry(),
                normalAdmissionsByTurn,
                turn: 1,
                globalBudget: testTurnAdmissionBudget)
            || !TryConsumeCycleRegionAdmissionForTurn(
                new CycleRegionLedgerEntry(),
                normalAdmissionsByTurn,
                turn: 2,
                globalBudget: testTurnAdmissionBudget))
        {
            throw new InvalidOperationException(
                "循环 region 预算没有按 turn 隔离，或同 turn 硬上限失效。");
        }

        Dictionary<int, int> probeAdmissionsByTurn = [];
        CycleRegionLedgerEntry turnOneProbeRegion = new();
        for (int admission = 0;
             admission < BaseCycleRegionProbeAdmissionBudget;
             admission++)
        {
            if (!TryConsumeCycleRegionProbeAdmissionForTurn(
                    turnOneProbeRegion,
                    probeAdmissionsByTurn,
                    turn: 1,
                    globalBudget: BaseCycleRegionProbeAdmissionBudget))
            {
                throw new InvalidOperationException(
                    "同回合循环 probe 的全局预算提前耗尽。");
            }
        }
        if (TryConsumeCycleRegionProbeAdmissionForTurn(
                new CycleRegionLedgerEntry(),
                probeAdmissionsByTurn,
                turn: 1,
                globalBudget: BaseCycleRegionProbeAdmissionBudget)
            || !TryConsumeCycleRegionProbeAdmissionForTurn(
                new CycleRegionLedgerEntry(),
                probeAdmissionsByTurn,
                turn: 2,
                globalBudget: BaseCycleRegionProbeAdmissionBudget))
        {
            throw new InvalidOperationException(
                "循环 probe 预算没有按 turn 隔离，或同 turn 硬上限失效。");
        }

        CycleRegionFairnessToken[] fairnessCandidates = Enumerable.Range(0, 4)
            .SelectMany(region => new[]
            {
                new CycleRegionFairnessToken(region, Lane: 0, Quality: 30 - region * 10),
                new CycleRegionFairnessToken(region, Lane: 1, Quality: region),
            })
            .ToArray();
        CycleRegionFairnessToken[] globallyAdmitted = OrderCycleRegionRoundRobin(
                fairnessCandidates,
                candidate => candidate.Lane,
                candidate => candidate.Region,
                static (left, right) =>
                {
                    int comparison = left.Quality.CompareTo(right.Quality);
                    return comparison != 0
                        ? comparison
                        : left.Region.CompareTo(right.Region);
                })
            .Take(3)
            .ToArray();
        if (globallyAdmitted.Select(candidate => candidate.Region).Distinct().Count() != 3
            || globallyAdmitted.Any(candidate => candidate.Region == 0))
        {
            throw new InvalidOperationException(
                "循环 region 的 run-global 预算仍可被首个 region 垄断，或错误按 hash 优先。");
        }

        CycleRegionFairnessToken[] selfFundingRound = OrderCycleRegionRoundRobin(
                Enumerable.Range(0, 33).SelectMany(region => new[]
                {
                    new CycleRegionFairnessToken(region, Lane: -1, Quality: region),
                    new CycleRegionFairnessToken(region, Lane: 0, Quality: -region),
                }),
                candidate => candidate.Lane,
                candidate => candidate.Region,
                static (left, right) =>
                {
                    int comparison = left.Quality.CompareTo(right.Quality);
                    return comparison != 0
                        ? comparison
                        : left.Region.CompareTo(right.Region);
                })
            .Take(CycleRegionProgressEpochAdmissionIncrement)
            .ToArray();
        if (selfFundingRound.Length != CycleRegionProgressEpochAdmissionIncrement
            || selfFundingRound.Any(candidate => candidate.Lane != -1)
            || selfFundingRound.Select(candidate => candidate.Region).Distinct().Count()
                != CycleRegionProgressEpochAdmissionIncrement)
        {
            throw new InvalidOperationException(
                "循环 region 的自筹进展 slot 被非进展代表抢占，或跨 region 失去公平性。");
        }

        // The 33rd region can lose the normal +32 witness round after the preceding regions fill
        // the turn-global extension. Its unrelated active probe must not then spend the rejected
        // witness's provisional per-region epoch and become an uncommittable 65th probe.
        CycleRegionLedgerEntry rejectedWitnessBaseline = new()
        {
            AdmittedNodes = BaseCycleRegionAdmissionBudget,
            ProbeAdmittedNodes = BaseCycleRegionProbeAdmissionBudget,
            ProgressEpochs = 0,
            HasObservation = true,
        };
        CycleRegionLedgerEntry rejectedWitnessProvisional = CloneCycleRegionLedger(
            rejectedWitnessBaseline);
        rejectedWitnessProvisional.ProgressEpochs = 1;
        Dictionary<int, int> filledProgressExtension = new()
        {
            [11] = CycleRegionGlobalAdmissionBudget(
                maxExpandedNodes: 8192,
                maximumProgressEpochs: 1),
        };
        bool rejectedWitnessAdmitted = TryConsumeCycleRegionAdmissionForTurn(
            rejectedWitnessProvisional,
            filledProgressExtension,
            turn: 11,
            globalBudget: CycleRegionGlobalAdmissionBudget(
                maxExpandedNodes: 8192,
                maximumProgressEpochs: 1));
        SettleCycleRegionProvisionalProgressWitness(
            rejectedWitnessProvisional,
            rejectedWitnessBaseline,
            rejectedWitnessAdmitted);
        int unrelatedProbeAdmissions = 0;
        if (rejectedWitnessAdmitted
            || TryConsumeCycleRegionProbeAdmission(
                rejectedWitnessProvisional,
                ref unrelatedProbeAdmissions,
                globalBudget: BaseCycleRegionProbeAdmissionBudget + 1)
            || rejectedWitnessProvisional.ProgressEpochs != 0
            || rejectedWitnessProvisional.ProbeAdmittedNodes
                != BaseCycleRegionProbeAdmissionBudget)
        {
            throw new InvalidOperationException(
                "循环 region 被拒的进展见证仍向无关 probe 泄漏了 provisional epoch。");
        }

        CycleRegionFairnessToken[] sameLaneRound = OrderCycleRegionRoundRobin(
                new[]
                {
                    new CycleRegionFairnessToken(0, Lane: 4, Quality: 0),
                    new CycleRegionFairnessToken(0, Lane: 4, Quality: 1),
                    new CycleRegionFairnessToken(0, Lane: 4, Quality: 2),
                    new CycleRegionFairnessToken(1, Lane: 4, Quality: 10),
                    new CycleRegionFairnessToken(2, Lane: 4, Quality: 20),
                },
                candidate => candidate.Lane,
                candidate => candidate.Region,
                static (left, right) =>
                {
                    int comparison = left.Quality.CompareTo(right.Quality);
                    return comparison != 0
                        ? comparison
                        : left.Region.CompareTo(right.Region);
                })
            .Take(3)
            .ToArray();
        if (sameLaneRound.Select(candidate => candidate.Region).Distinct().Count() != 3)
        {
            throw new InvalidOperationException(
                "循环 region 同 lane 的多 lease/action-family 仍可连续抢占全局预算。");
        }

        CycleRegionFairnessToken[] probeRound = OrderCycleRegionRoundRobin(
                Enumerable.Range(0, 4)
                    .Select(region => new CycleRegionFairnessToken(
                        region,
                        Lane: 3,
                        Quality: 3 - region)),
                candidate => candidate.Lane,
                candidate => candidate.Region,
                static (left, right) =>
                {
                    int comparison = left.Quality.CompareTo(right.Quality);
                    return comparison != 0
                        ? comparison
                        : left.Region.CompareTo(right.Region);
                })
            .Take(3)
            .ToArray();
        if (probeRound.Select(candidate => candidate.Region).Distinct().Count() != 3)
        {
            throw new InvalidOperationException(
                "循环 region 的 probe run-global 保留没有跨 region 公平轮转。");
        }

        Dictionary<CycleRegionKey, CycleRegionLedgerEntry> boundedLedgers = [];
        Dictionary<int, int> fullNormalAdmissions =
            new() { [7] = testTurnAdmissionBudget };
        Dictionary<int, int> fullProbeAdmissions =
            new() { [7] = BaseCycleRegionProbeAdmissionBudget };
        CycleRegionKey existingRegion = new(
            7,
            new StateFingerprint(0xaaaaUL, 0xbbbbUL));
        CycleRegionLedgerEntry existingRegionLedger = new();
        boundedLedgers.Add(existingRegion, existingRegionLedger);
        for (int index = 0; index < 1024; index++)
        {
            CycleRegionKey rejectedRegion = new(
                7,
                new StateFingerprint((ulong)index + 1, (ulong)index + 2));
            if (rejectedRegion == existingRegion)
                continue;
            if (TryGetCycleRegionLedgerForObservation(
                    boundedLedgers,
                    fullNormalAdmissions,
                    fullProbeAdmissions,
                    rejectedRegion,
                    testTurnAdmissionBudget,
                    BaseCycleRegionProbeAdmissionBudget,
                    out _))
            {
                throw new InvalidOperationException(
                    "循环 region 双预算耗尽后仍为新键创建了观测账本。");
            }
        }
        if (boundedLedgers.Count != 1
            || !TryGetCycleRegionLedgerForObservation(
                boundedLedgers,
                fullNormalAdmissions,
                fullProbeAdmissions,
                existingRegion,
                testTurnAdmissionBudget,
                BaseCycleRegionProbeAdmissionBudget,
                out CycleRegionLedgerEntry settledRegionLedger)
            || !ReferenceEquals(existingRegionLedger, settledRegionLedger))
        {
            throw new InvalidOperationException(
                "循环 region 账本上界失效，或双预算耗尽后无法处理已有键。");
        }

        Dictionary<int, int> normalOnlyCapacity =
            new() { [8] = testTurnAdmissionBudget - 1 };
        Dictionary<int, int> probeFull =
            new() { [8] = BaseCycleRegionProbeAdmissionBudget };
        Dictionary<int, int> normalFull =
            new() { [9] = testTurnAdmissionBudget };
        Dictionary<int, int> probeOnlyCapacity =
            new() { [9] = BaseCycleRegionProbeAdmissionBudget - 1 };
        if (!TryGetCycleRegionLedgerForObservation(
                boundedLedgers,
                normalOnlyCapacity,
                probeFull,
                new CycleRegionKey(8, new StateFingerprint(8, 80)),
                testTurnAdmissionBudget,
                BaseCycleRegionProbeAdmissionBudget,
                out _)
            || !TryGetCycleRegionLedgerForObservation(
                boundedLedgers,
                normalFull,
                probeOnlyCapacity,
                new CycleRegionKey(9, new StateFingerprint(9, 90)),
                testTurnAdmissionBudget,
                BaseCycleRegionProbeAdmissionBudget,
                out _)
            || boundedLedgers.Count != 1)
        {
            throw new InvalidOperationException(
                "循环 region 的有序变异普通名额或已签发 probe 名额被误拒，" +
                "或未实际入选的临时账本被持久化。");
        }
    }

    private static void VerifyCycleRegionProgressPhasePolicyForTesting()
    {
        SearchNode witness = new(
            Action: null,
            ActionCount: 3,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: default,
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: null!,
            CombatProgress: null!);
        SearchNode firstPhase = witness with { Parent = witness, ActionCount = 4 };
        SearchNode secondPhase = witness with { Parent = firstPhase, ActionCount = 5 };
        SearchNode growingDamage = witness with
        {
            Cycle = new CycleSearchState(default, default, 1, 2, default, false)
            {
                HasNewEnemyDurabilityProgress = true,
                HasConsistentDamagePhases = true,
            },
        };
        int allowance = CycleRegionProgressPhaseAllowance(3);
        WeakReference<SearchNode> witnessIdentity = new(witness);
        WeakReference<SearchNode> firstPhaseIdentity = new(firstPhase);
        WeakReference<SearchNode> secondPhaseIdentity = new(secondPhase);
        WeakReference<SearchNode> expiredIdentity = new(null!);
        if (!HasConsistentCycleDurabilityProgress(growingDamage)
            || growingDamage.Cycle!.HasConsistentDelta
            || allowance != 2
            || !CanContinueCycleRegionProgressPhase(firstPhase.Parent, witnessIdentity, allowance)
            || CanContinueCycleRegionProgressPhase(secondPhase.Parent, witnessIdentity, allowance)
            || !CanContinueCycleRegionProgressPhase(
                secondPhase.Parent, firstPhaseIdentity, allowance - 1)
            || CanContinueCycleRegionProgressPhase(
                secondPhase, secondPhaseIdentity, allowance - 2)
            || CanContinueCycleRegionProgressPhase(firstPhase.Parent, expiredIdentity, allowance)
            || CanContinueCycleRegionProgressPhase(null, null, allowance)
            || CycleRegionProgressPhaseAllowance(1) != 0
            || CycleRegionProgressPhaseAllowance(1000) != MaximumDetectedCyclePeriodActions - 1)
        {
            throw new InvalidOperationException(
                "循环进展的静默阶段续行没有绑定唯一存活父节点或超出了已证实周期长度。");
        }

        CycleRegionLedgerEntry committed = new()
        {
            ProgressContinuationNode = witnessIdentity,
            ProgressActionsRemaining = allowance,
        };
        CycleRegionLedgerEntry provisional = CloneCycleRegionLedger(committed);
        if (!ReferenceEquals(
                provisional.ProgressContinuationNode,
                committed.ProgressContinuationNode))
        {
            throw new InvalidOperationException(
                "循环进展账本副本没有共享不可变的父节点弱身份。");
        }
        provisional.ProgressContinuationNode = firstPhaseIdentity;
        provisional.ProgressActionsRemaining--;
        if (!ReferenceEquals(committed.ProgressContinuationNode, witnessIdentity)
            || committed.ProgressContinuationNode is not { } committedIdentity
            || !committedIdentity.TryGetTarget(out SearchNode? committedWitness)
            || !ReferenceEquals(committedWitness, witness)
            || provisional.ProgressContinuationNode is not { } provisionalIdentity
            || !provisionalIdentity.TryGetTarget(out SearchNode? provisionalWitness)
            || !ReferenceEquals(provisionalWitness, firstPhase)
            || committed.ProgressActionsRemaining != allowance)
        {
            throw new InvalidOperationException(
                "未提交的循环进展候选提前改写了父节点的静默阶段额度。");
        }
    }
}
