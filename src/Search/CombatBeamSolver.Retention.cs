using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    private readonly record struct CycleProbeFamilyKey(
        int Turn,
        StateFingerprint ShapeKey,
        StateFingerprint SequenceKey,
        int PeriodActions,
        int HealthRiskBucket,
        CycleProbeTracker? Tracker);

    private readonly record struct CycleStartupRetentionKey(
        int HealthRiskBucket,
        long HealthRisk,
        int PotionStrategicCost,
        int LiveDeckClutter,
        int LiveDeckSize,
        long SetupValue,
        StateFingerprint StableFingerprint);

    private sealed record CycleStartupRetentionTestCandidate(
        int Identity,
        int ProjectedPlayerHp,
        CycleStartupRetentionKey Key);

    [InlineArray(5)]
    private struct CycleStartupNodeBuckets
    {
        private SearchNode? _element0;
    }

    [InlineArray(5)]
    private struct CycleStartupKeyBuckets
    {
        private CycleStartupRetentionKey _element0;
    }

    private readonly record struct CycleExitProbeFamilyKey(
        StateFingerprint OriginShapeKey,
        StateFingerprint OriginSequenceKey,
        int OriginPeriodActions,
        int OriginPhaseIndex,
        CycleProbeTracker OriginTracker,
        long OriginGeneration,
        StateFingerprint ExitActionKey);

    private readonly record struct CycleExitProbeTicketKey(
        CycleProbeTracker OriginTracker,
        int OriginPhaseIndex,
        StateFingerprint ExitActionKey,
        long OriginGeneration);

    private readonly record struct CrossTurnProbeFamilyKey(
        StateFingerprint ShapeKey,
        StateFingerprint SemanticStateKey,
        int PotionCount,
        CrossTurnProbeTracker? Tracker);

    private SearchNode RefreshReleasedFallback(SearchNode fallback)
    {
        if (fallback.Snapshot.HasSimulator)
            return fallback;
        SimulationSnapshot? turnSetupRoot = _includeTurnSetup
            ? ReplayTurnSetup(fallback.GetTurnSetupChoices())
            : null;
        SimulationSnapshot snapshot;
        try
        {
            snapshot = Replay(
                fallback.Actions,
                turnSetupRoot,
                _startTurnNumber,
                priorActionCount: 0);
        }
        finally
        {
            turnSetupRoot?.ReleaseSimulator();
        }
        return fallback with
        {
            Score = snapshot.Score,
            StateKey = snapshot.StateKey,
            HasPredictionRisk = snapshot.HasRisk,
            BoundaryReason = snapshot.BoundaryReason,
            IsTerminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None,
            Snapshot = snapshot,
        };
    }

    private List<SearchNode> Prune(IEnumerable<SearchNode> nodes)
    {
        SearchMeasurement measurement = _run.Performance.Begin();
        try
        {
            // Rank the complete candidate pool before applying the incumbent. Filtering first
            // backfills the beam with weaker branches and changes which exact lineages win later
            // transposition races; an incumbent is a bound, not a request to refill every lane.
            List<SearchNode> pool = nodes.ToList();
            int pathBoundaryId = ObserveSearchPathBoundaryInput(
                pool, SearchPathObservationStage.PruneInput, "prune_input");
            Action<GlobalRetentionDecision>? observeGlobalRetention =
                CreateGlobalRetentionObserver(pool, pathBoundaryId);
            List<SearchNode> global = Retention.RankBest(
                pool,
                _profile.BeamWidth,
                preserveDefensiveRoute: true,
                observe: observeGlobalRetention);
            List<SearchNode> selected = [.. global];
            HashSet<SearchNode> selectedSet = new(global, ReferenceEqualityComparer.Instance);
            Dictionary<SearchNode, int> globalRetentionRanks = new(ReferenceEqualityComparer.Instance);
            foreach (SearchNode candidate in global)
                globalRetentionRanks.Add(candidate, candidate.RetentionRank);
            Dictionary<SearchNode, int> ancestorRetentionRanks = new(ReferenceEqualityComparer.Instance);
            foreach (SearchNode candidate in pool)
            {
                for (SearchNode? ancestor = candidate.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    // The first visit records this ancestor and its complete parent chain.
                    // A repeated ancestor therefore proves every remaining parent is recorded too.
                    if (!ancestorRetentionRanks.TryAdd(ancestor, ancestor.RetentionRank))
                        break;
                    if (ancestor.LongTermResourceRetentionRank != int.MaxValue)
                        ancestor.RetentionRank = ancestor.LongTermResourceRetentionRank;
                }
            }
            List<SearchNode> longTermResource = Retention.RankLongTermResource(pool, _profile.BeamWidth);
            foreach (SearchNode candidate in longTermResource)
                candidate.LongTermResourceRetentionRank = candidate.RetentionRank;
            foreach ((SearchNode ancestor, int retentionRank) in ancestorRetentionRanks)
                ancestor.RetentionRank = retentionRank;
            foreach ((SearchNode candidate, int retentionRank) in globalRetentionRanks)
                candidate.RetentionRank = retentionRank;
            foreach (SearchNode candidate in longTermResource
                         .OrderBy(node => node.RetentionRank)
                         .ThenByDescending(node => node.Score))
            {
                if (!selectedSet.Add(candidate))
                    continue;
                selected.Add(candidate);
            }
            bool hasCyclePortfolioWork = false;
            bool hasCycleExitWork = false;
            bool hasCrossTurnWork = false;
            bool hasCycleRegionWork = false;
            bool hasOrderedMutationWork = false;
            foreach (SearchNode candidate in pool)
            {
                hasCyclePortfolioWork |= candidate.CycleProbeLease != null
                    || RequiresBoundedCyclePlanning(candidate);
                hasCycleExitWork |= candidate.CycleExitProbe != null;
                hasCrossTurnWork |= candidate.CrossTurnProbe != null
                    || RequiresCrossTurnPlanning(candidate);
                hasCycleRegionWork |= !IsCycleRegionBudgetExempt(candidate)
                    && (candidate.CycleExitProbe != null
                        || candidate.CycleProbeLease != null
                        || candidate.Cycle != null);
                hasOrderedMutationWork |= candidate.OrderedMutationLineage != null
                    || candidate.OrderedMutationBoundaryLineage != null
                    || candidate.OrderedMutationRetentionLease != null
                    || candidate.OrderedMutationActivationTicket != null
                    || candidate.OrderedMutationLeaseTransitionPending
                    || candidate.OrderedMutationAdmissionPending
                    || candidate.OrderedMutationContinuationHandoff
                    || candidate.OrderedMutationContinuationBridge
                    || candidate.OrderedMutationObservationRequested
                    || candidate.OrderedMutationObservationDebtSettlementPending
                    || candidate.OrderedMutationObservationStepsRemaining > 0;
            }
            if (hasCyclePortfolioWork)
                AddCyclePortfolio(pool, selected, selectedSet);
            if (hasCycleExitWork)
                AddCycleExitPortfolio(pool, selected, selectedSet);
            if (hasCrossTurnWork)
                AddCrossTurnPortfolio(pool, selected, selectedSet);
            // Every independent retention channel must finish before the ordered coordinator.
            // In particular a late opening-channel winner with an inherited lease must pay this
            // layer's ordered admission (or lose only that lease) before CycleRegion arbitration.
            if (_profile.Phase == SolverSearchPhase.Deep
                && pool.Count > _profile.BeamWidth
                && root.HasUnusedCardReplayAllocator)
            {
                int channelWidth = Math.Clamp(_profile.BeamWidth / 12, 6, 12);
                List<List<SearchNode>> openingChannels = pool
                    .Select(node => (Node: node, Opening: FindOpeningCardNode(node)))
                    .Where(item => item.Opening?.Parent is { } parent
                        && (item.Opening.Snapshot.PersistentBuffValue
                                > parent.Snapshot.PersistentBuffValue
                            || item.Opening.Snapshot.StrategicEffects.RetentionValue
                                > parent.Snapshot.StrategicEffects.RetentionValue))
                    .GroupBy(item => (
                        item.Node.PotionCount,
                        FirstCardId: item.Opening!.Action!.CardId))
                    .OrderByDescending(group => group.Max(item =>
                        item.Opening!.Snapshot.StrategicEffects.RetentionValue))
                    .ThenByDescending(group => group.Max(item => item.Node.Score))
                    .Take(8)
                    .Select(group => Retention.RankBest(
                        group.Select(item => item.Node),
                        channelWidth,
                        preserveDefensiveRoute: true))
                    .ToList();
                int expandedLimit = Math.Min(
                    pool.Count,
                    checked(selected.Count + Math.Max(12, _profile.BeamWidth / 3)));
                for (int round = 0;
                     selected.Count < expandedLimit
                         && openingChannels.Any(channel => round < channel.Count);
                     round++)
                {
                    foreach (IReadOnlyList<SearchNode> channel in openingChannels)
                    {
                        if (round >= channel.Count || !selectedSet.Add(channel[round]))
                            continue;
                        selected.Add(channel[round]);
                        if (selected.Count >= expandedLimit)
                            break;
                    }
                }
            }

            CycleRegionRetentionTransaction? cycleRegionTransaction = null;
            if (hasOrderedMutationWork)
                Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);
            if (hasCycleRegionWork)
            {
                cycleRegionTransaction = ApplyCycleRegionRetention(
                    pool,
                    selected);
            }
            SortRetained(selected);
            List<SearchNode> finalized = FinalizePrunedSelection(
                pool,
                selected,
                hasOrderedMutationWork,
                hasCycleExitWork,
                cycleRegionTransaction);
            List<SearchNode> bounded = ApplyPrimaryIncumbentBound(finalized);
            // Emit all watched final aliases, after every portfolio and the incumbent.
            // The paired value events avoid equating a `with` clone with a dropped route.
            ObserveSearchPathBoundary(
                bounded, SearchPathObservationStage.PruneFinal, "after_incumbent", pathBoundaryId);
            if (observeGlobalRetention != null)
            {
                ObserveSearchPathRetentionPool(
                    bounded, SearchPathObservationStage.RetentionPoolFinal, "outer_prune_final", pathBoundaryId);
            }
            return bounded;
        }
        finally
        {
            _run.Performance.End(SearchMetricPhase.Prune, measurement);
        }
    }

    private List<SearchNode> FinalizePrunedSelection(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        bool hasOrderedMutationWork,
        bool hasCycleExitWork,
        CycleRegionRetentionTransaction? cycleRegionTransaction = null)
    {
        // Ordered settlement may remove only its own exempt pending work; ordinary CycleRegion
        // survivors are stable and its provisional slots therefore cannot develop backfill holes.
        // The primary incumbent is applied by the caller after every transaction has settled, so
        // removed candidates deliberately leave holes instead of changing the ranked population.
        List<SearchNode> bounded = selected;
        if (hasOrderedMutationWork)
        {
            FinalizeOrderedMutationPortfolio(bounded);
            Retention.ArmOrderedMutationObservationBridges(pool, bounded);
        }
        else if (_run.PendingOrderedMutationHandoffSourceByNode.Count != 0)
        {
            throw new InvalidOperationException(
                "无 ordered-mutation frontier 时遗留了 pending source admission。");
        }
        List<SearchNode> finalized = hasCycleExitWork
            ? FinalizePrunedCycleExitProbeTickets(pool, bounded)
            : bounded;
        FinalizeCycleRegionRetention(cycleRegionTransaction, finalized);
        return finalized;
    }

    private List<SearchNode> ApplyPrimaryIncumbentBound(List<SearchNode> retained)
    {
        if (_primaryIncumbent is not { } incumbent)
            return retained;

        List<SearchNode> bounded = ApplyPrimaryIncumbentBound(
            retained,
            incumbent,
            out int pruned);
        _run.PrimaryIncumbentBranchesPruned += pruned;
        return bounded;
    }

    internal static List<SearchNode> ApplyPrimaryIncumbentBound(
        List<SearchNode> retained,
        PrimarySearchIncumbent incumbent,
        out int pruned)
    {
        pruned = 0;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            if (ShouldPruneByPrimaryIncumbent(
                    node.Snapshot.CumulativePlayerHpLost,
                    node.Turn,
                    incumbent))
            {
                if (bounded == null)
                {
                    bounded = new List<SearchNode>(retained.Count);
                    if (index > 0)
                        bounded.AddRange(retained.GetRange(0, index));
                }
                pruned++;
                continue;
            }
            bounded?.Add(node);
        }
        return bounded ?? retained;
    }

    internal static bool ShouldPruneByPrimaryIncumbent(
        int cumulativePlayerHpLost,
        int turn,
        PrimarySearchIncumbent incumbent)
        => cumulativePlayerHpLost > incumbent.StrategicHpDeficit
            || cumulativePlayerHpLost == incumbent.StrategicHpDeficit
                && turn > incumbent.CombatEndedTurn;

    internal static bool TryTightenPrimarySearchIncumbent(
        PotionFreePolicyBaseline? auditedPotionFreeBaseline,
        int minimumPotionUses,
        int? maximumPotionUses,
        bool candidateCompleteVictory,
        bool candidateSatisfiesHardRules,
        int candidateExplicitPotionUses,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        ref PrimarySearchIncumbent? incumbent,
        SolverPotionPolicy? effectivePotionPolicy = null)
    {
        if (!candidateCompleteVictory
            || !candidateSatisfiesHardRules
            || candidateExplicitPotionUses != minimumPotionUses
            || candidateCombatEndedTurn is not { } combatEndedTurn)
        {
            return false;
        }

        // A complete, hard-policy-compliant victory without explicit potion use is
        // already eligible under Disabled/Smart. It needs no separate potion audit.
        // Positive exact layers retain their stricter, audited eligibility proof.
        bool eligiblePotionFreeVictory = minimumPotionUses == 0
            && effectivePotionPolicy is SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart;
        bool eligibleExactPotionVictory = auditedPotionFreeBaseline is { } baseline
            && minimumPotionUses > 0
            && maximumPotionUses == minimumPotionUses
            && SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit,
                candidateCombatEndedTurn,
                currentCompleteVictory: baseline.Won,
                currentStrategicHpDeficit: baseline.HpDeficit,
                currentCombatEndedTurn: baseline.CombatEndedTurn) < 0;
        if (!eligiblePotionFreeVictory && !eligibleExactPotionVictory)
        {
            return false;
        }

        PrimarySearchIncumbent candidate = new(
            candidateStrategicHpDeficit,
            combatEndedTurn);
        if (incumbent is { } current
            && SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidate.StrategicHpDeficit,
                candidate.CombatEndedTurn,
                currentCompleteVictory: true,
                currentStrategicHpDeficit: current.StrategicHpDeficit,
                currentCombatEndedTurn: current.CombatEndedTurn) >= 0)
        {
            return false;
        }

        incumbent = candidate;
        return true;
    }

    private bool TightenPrimarySearchIncumbentAtTurnLayer(
        IReadOnlyList<SearchNode> retained,
        int completedTurnLayers)
    {
        bool canEstablishPotionFreeIncumbent = _minimumPotionUses == 0
            && _potionPolicy is SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart;
        // The strict-primary escape in FinalPlanOrdering is guaranteed to make an
        // exact-layer victory policy-eligible only when every explicit use is optional.
        // Smart-gradient exact layers use a policy override and therefore do not enforce
        // per-slot directives here. Future forced-directive exact solvers must prove their
        // optional-use facts separately before they may tighten this bound.
        bool canEstablishExactPotionIncumbent = _potionFreePolicyBaseline != null
            && _minimumPotionUses > 0
            && _maximumPotionUses == _minimumPotionUses
            && !_enforcePotionDirectives;
        if (!canEstablishPotionFreeIncumbent && !canEstablishExactPotionIncumbent)
        {
            return false;
        }

        PrimarySearchIncumbent? tightened = _primaryIncumbent;
        foreach (SearchNode node in retained)
        {
            int explicitPotionUses = ExplicitPotionUseCount(node);
            bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount,
                node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead,
                node.Snapshot.ProjectedPlayerHp);
            if (!completeVictory
                || explicitPotionUses != _minimumPotionUses
                || _enforcePotionDirectives
                    && !_potionStrategy.EvaluateForcedUses(
                            node.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied)
            {
                continue;
            }

            // PlayerMaxHp is part of the incumbent only after combat has actually ended.
            // ApplyPrimaryIncumbentBound deliberately keeps using cumulative HP loss alone
            // as the lower bound for incomplete nodes because max HP may still recover.
            int strategicHpDeficit = node.Snapshot.CumulativePlayerHpLost
                + Math.Max(0, root.InitialPlayerMaxHp - node.Snapshot.PlayerMaxHp);
            TryTightenPrimarySearchIncumbent(
                _potionFreePolicyBaseline,
                _minimumPotionUses,
                _maximumPotionUses,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                explicitPotionUses,
                strategicHpDeficit,
                node.Snapshot.CombatEndedTurn,
                ref tightened,
                effectivePotionPolicy: _potionPolicy);
        }

        if (Nullable.Equals(tightened, _primaryIncumbent))
            return false;

        PrimarySearchIncumbent? previous = _primaryIncumbent;
        _primaryIncumbent = tightened;
        _run.PrimaryIncumbentUpdates++;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] PRIMARY_INCUMBENT_UPDATE " +
            $"source={(canEstablishPotionFreeIncumbent ? "no_explicit_potion" : "exact_potion_layer")} " +
            $"completed_turns={completedTurnLayers} " +
            $"previous_deficit={previous?.StrategicHpDeficit.ToString() ?? "-"} " +
            $"previous_turn={previous?.CombatEndedTurn.ToString() ?? "-"} " +
            $"deficit={tightened!.Value.StrategicHpDeficit} " +
            $"turn={tightened.Value.CombatEndedTurn}");
        return true;
    }

    private static void SortRetained(List<SearchNode> selected)
        => selected.Sort((left, right) =>
        {
            int leftRank = Math.Min(
                left.RetentionRank,
                Math.Min(
                    left.LongTermResourceRetentionRank,
                    Math.Min(
                        left.CycleRetentionRank,
                        Math.Min(left.CycleExitRetentionRank, left.CrossTurnRetentionRank))));
            int rightRank = Math.Min(
                right.RetentionRank,
                Math.Min(
                    right.LongTermResourceRetentionRank,
                    Math.Min(
                        right.CycleRetentionRank,
                        Math.Min(right.CycleExitRetentionRank, right.CrossTurnRetentionRank))));
            int byRetention = leftRank.CompareTo(rightRank);
            return byRetention != 0 ? byRetention : right.Score.CompareTo(left.Score);
        });

    private void AddCyclePortfolio(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        HashSet<SearchNode> selectedSet)
    {
        foreach (SearchNode node in pool)
        {
            if (node.CycleProbeLease is { NextActionIndex: 0 } lease
                && node.Cycle is { } cycle
                && cycle.ShapeKey == lease.Tracker.ShapeKey
                && cycle.SequenceKey == lease.Tracker.SequenceKey
                && cycle.PeriodActions == lease.Tracker.PeriodActions
                && !RequiresBoundedCyclePlanning(node))
            {
                node.CycleProbeLease = null;
            }
        }
        List<SearchNode> eligible = [];
        foreach (SearchNode node in pool)
        {
            if (node.CycleProbeLease == null && !RequiresBoundedCyclePlanning(node))
                continue;
            eligible.Add(node);
        }
        if (eligible.Count == 0)
            return;

        // Compute every candidate's startup key once. The five fixed-size buffers stay on this
        // stack frame, avoiding both repeated setup fingerprint work and per-bucket collections.
        CycleStartupNodeBuckets activeByBucket = default;
        CycleStartupNodeBuckets startupByBucket = default;
        CycleStartupKeyBuckets startupKeysByBucket = default;
        SearchNode? purificationAnchor = null;
        CycleStartupRetentionKey purificationKey = default;
        bool hasFirstDeckShape = false;
        int firstClutter = 0;
        int firstSize = 0;
        bool hasMeaningfulDeckImprovement = false;
        foreach (SearchNode candidate in eligible)
        {
            int healthRiskBucket = CycleStartupHealthRiskBucket(candidate);
            if (candidate.CycleProbeLease is { NextActionIndex: > 0 })
            {
                SearchNode? active = activeByBucket[healthRiskBucket];
                if (active == null
                    || CompareCycleProbeCandidates(
                        candidate,
                        active,
                        root.InitialPlayerMaxHp) < 0)
                {
                    activeByBucket[healthRiskBucket] = candidate;
                }
                continue;
            }
            if (!CanOccupyCycleStartupReserve(candidate.Snapshot.ProjectedPlayerHp))
                continue;

            CycleStartupRetentionKey key = BuildCycleStartupRetentionKey(candidate);
            SearchNode? startup = startupByBucket[healthRiskBucket];
            if (startup == null
                || CompareCycleStartupKeys(
                    key,
                    startupKeysByBucket[healthRiskBucket]) < 0)
            {
                startupByBucket[healthRiskBucket] = candidate;
                startupKeysByBucket[healthRiskBucket] = key;
            }

            if (!hasFirstDeckShape)
            {
                hasFirstDeckShape = true;
                firstClutter = key.LiveDeckClutter;
                firstSize = key.LiveDeckSize;
            }
            else if (key.LiveDeckClutter != firstClutter
                     || key.LiveDeckSize != firstSize)
            {
                hasMeaningfulDeckImprovement = true;
            }
            if (purificationAnchor == null
                || CompareCyclePurificationKeys(key, purificationKey) < 0)
            {
                purificationAnchor = candidate;
                purificationKey = key;
            }
        }

        // Each stable health-investment bucket owns one bounded lane. An already-issued
        // mid-period lease settles that bucket's obligation first; otherwise the slot starts
        // the deepest still-survivable setup in the bucket. This merges debt and startup into
        // one hard-capped portfolio instead of letting either reserve grow independently.
        List<SearchNode> leased = new(6);
        for (int healthRiskBucket = 0; healthRiskBucket <= 4; healthRiskBucket++)
        {
            SearchNode? candidate = activeByBucket[healthRiskBucket]
                ?? startupByBucket[healthRiskBucket];
            if (candidate != null)
                leased.Add(candidate);
        }
        if (hasMeaningfulDeckImprovement
            && purificationAnchor != null
            && !ContainsReference(leased, purificationAnchor))
        {
            leased.Add(purificationAnchor);
        }
        if (leased.Count > 6)
            throw new InvalidOperationException("循环 startup portfolio 超过 6 条硬上限。");

        HashSet<SearchNode> leasedSet = new(leased, ReferenceEqualityComparer.Instance);
        foreach (SearchNode candidate in pool)
        {
            if (candidate.CycleProbeLease != null && !leasedSet.Contains(candidate))
                candidate.CycleProbeLease = null;
        }

        int rank = 0;
        foreach (SearchNode candidate in leased)
        {
            if (candidate.CycleProbeLease == null)
                StartCycleProbeLease(candidate);
            // Only final portfolio winners reach this point. A retained health-investment
            // lane asks its already-admitted family for the matching staged observation
            // budget; rejected siblings cannot mint an epoch or a family ledger. The issued
            // tracker supplies the exact canonical family without re-hashing the action path.
            RequestRetainedCycleStartupImprovementEpoch(
                candidate,
                CycleStartupHealthRiskBucket(candidate));
            candidate.CycleRetentionRank = _profile.BeamWidth + rank++;
            if (!selectedSet.Add(candidate))
                continue;
            // RankBest mutates ranks for every examined node. A cycle-only admission must
            // remain behind all ordinary and long-term retained routes.
            candidate.RetentionRank = int.MaxValue;
            candidate.LongTermResourceRetentionRank = int.MaxValue;
            selected.Add(candidate);
            _run.CycleCandidatesProtected++;
        }
    }

    private static long CycleHealthRisk(SearchNode node, int referenceMaxHp)
        => (long)node.Snapshot.CumulativePlayerHpLost
            + node.FutureSoldHp
            + Math.Max(0, referenceMaxHp - node.Snapshot.PlayerMaxHp);

    private long CycleStartupHealthRisk(SearchNode node)
        => CycleHealthRisk(node, root.InitialPlayerMaxHp);

    private int CycleStartupHealthRiskBucket(SearchNode node)
        => CycleStartupHealthRiskBucket(
            root.InitialPlayerHp,
            CycleStartupHealthRisk(node));

    private static int CycleStartupHealthRiskBucket(
        int initialPlayerHp,
        long healthRisk)
    {
        if (healthRisk <= 0)
            return 0;
        long survivableRisk = Math.Max(1L, (long)initialPlayerHp - 1);
        long quartile = Math.Min(3L, checked((healthRisk - 1) * 4) / survivableRisk);
        return checked(1 + (int)quartile);
    }

    private static bool CanOccupyCycleStartupReserve(int projectedPlayerHp)
        => projectedPlayerHp > 0;

    private CycleStartupRetentionKey BuildCycleStartupRetentionKey(SearchNode node)
    {
        long healthRisk = CycleStartupHealthRisk(node);
        StateFingerprint actionFingerprint =
            BuildCycleDeterministicActionFingerprint(node.Action);
        StateFingerprint parentFingerprint = node.Parent?.StateKey ?? default;
        return new CycleStartupRetentionKey(
            CycleStartupHealthRiskBucket(root.InitialPlayerHp, healthRisk),
            healthRisk,
            node.PotionStrategicCost,
            node.Snapshot.LiveDeckClutter,
            node.Snapshot.LiveDeckSize,
            CycleRegionSetupValue(node.Snapshot),
            BuildCycleStartupStableFingerprint(
                node.StateKey,
                actionFingerprint,
                parentFingerprint));
    }

    private static StateFingerprint BuildCycleStartupStableFingerprint(
        StateFingerprint state,
        StateFingerprint action,
        StateFingerprint parent)
    {
        StateFingerprintBuilder stable = new();
        stable.Add(state.First);
        stable.Add(state.Second);
        stable.Add(action.First);
        stable.Add(action.Second);
        stable.Add(parent.First);
        stable.Add(parent.Second);
        return stable.Finish();
    }

    private static T? SelectCycleStartupBucketRepresentative<T>(
        IEnumerable<T> candidates,
        int healthRiskBucket,
        Func<T, bool> isEligible,
        Func<T, CycleStartupRetentionKey> keySelector)
        where T : class
    {
        T? best = null;
        CycleStartupRetentionKey bestKey = default;
        foreach (T candidate in candidates)
        {
            if (!isEligible(candidate))
                continue;
            CycleStartupRetentionKey key = keySelector(candidate);
            if (key.HealthRiskBucket != healthRiskBucket)
                continue;
            if (best == null || CompareCycleStartupKeys(key, bestKey) < 0)
            {
                best = candidate;
                bestKey = key;
            }
        }
        return best;
    }

    private static T? SelectCyclePurificationAnchor<T>(
        IEnumerable<T> candidates,
        Func<T, bool> isEligible,
        Func<T, CycleStartupRetentionKey> keySelector)
        where T : class
    {
        T? best = null;
        CycleStartupRetentionKey bestKey = default;
        bool hasFirstDeckShape = false;
        int firstClutter = 0;
        int firstSize = 0;
        bool hasMeaningfulDeckImprovement = false;
        foreach (T candidate in candidates)
        {
            if (!isEligible(candidate))
                continue;
            CycleStartupRetentionKey key = keySelector(candidate);
            if (!hasFirstDeckShape)
            {
                hasFirstDeckShape = true;
                firstClutter = key.LiveDeckClutter;
                firstSize = key.LiveDeckSize;
            }
            else if (key.LiveDeckClutter != firstClutter
                     || key.LiveDeckSize != firstSize)
            {
                hasMeaningfulDeckImprovement = true;
            }
            if (best == null || CompareCyclePurificationKeys(key, bestKey) < 0)
            {
                best = candidate;
                bestKey = key;
            }
        }
        return hasMeaningfulDeckImprovement ? best : null;
    }

    private static int CompareCycleStartupKeys(
        CycleStartupRetentionKey left,
        CycleStartupRetentionKey right)
    {
        int comparison = right.HealthRisk.CompareTo(left.HealthRisk);
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.LiveDeckClutter.CompareTo(right.LiveDeckClutter);
        if (comparison != 0)
            return comparison;
        comparison = left.LiveDeckSize.CompareTo(right.LiveDeckSize);
        if (comparison != 0)
            return comparison;
        comparison = right.SetupValue.CompareTo(left.SetupValue);
        return comparison != 0
            ? comparison
            : CompareCycleStartupStableFingerprints(left, right);
    }

    private static int CompareCyclePurificationKeys(
        CycleStartupRetentionKey left,
        CycleStartupRetentionKey right)
    {
        int comparison = left.LiveDeckClutter.CompareTo(right.LiveDeckClutter);
        if (comparison != 0)
            return comparison;
        comparison = left.LiveDeckSize.CompareTo(right.LiveDeckSize);
        if (comparison != 0)
            return comparison;
        comparison = right.SetupValue.CompareTo(left.SetupValue);
        if (comparison != 0)
            return comparison;
        comparison = left.HealthRisk.CompareTo(right.HealthRisk);
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        return comparison != 0
            ? comparison
            : CompareCycleStartupStableFingerprints(left, right);
    }

    private static int CompareCycleStartupStableFingerprints(
        CycleStartupRetentionKey left,
        CycleStartupRetentionKey right)
    {
        int comparison = left.StableFingerprint.First.CompareTo(
            right.StableFingerprint.First);
        return comparison != 0
            ? comparison
            : left.StableFingerprint.Second.CompareTo(right.StableFingerprint.Second);
    }

    private void AddCycleExitPortfolio(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        HashSet<SearchNode> selectedSet)
    {
        List<SearchNode> eligible = [];
        foreach (SearchNode node in pool)
        {
            if (node.CycleExitProbe is not { RemainingActions: > 0 })
                continue;
            eligible.Add(node);
        }
        if (eligible.Count == 0)
            return;

        List<SearchNode> leased = new(6);
        List<SearchNode> newestRepresentatives = [];
        for (int healthRiskBucket = 0; healthRiskBucket <= 4; healthRiskBucket++)
        {
            Dictionary<CycleExitProbeFamilyKey, int> familyIndexes = [];
            List<SearchNode> representatives = [];
            foreach (SearchNode node in eligible)
            {
                if (CycleStartupHealthRiskBucket(node) != healthRiskBucket)
                    continue;
                CycleExitProbeFamilyKey family = BuildCycleExitProbeFamilyKey(node);
                if (!familyIndexes.TryGetValue(family, out int index))
                {
                    familyIndexes.Add(family, representatives.Count);
                    representatives.Add(node);
                    continue;
                }
                if (CompareCycleExitFamilyCandidates(
                        node,
                        representatives[index],
                        root.InitialPlayerMaxHp) < 0)
                {
                    representatives[index] = node;
                }
            }
            if (representatives.Count == 0)
                continue;
            newestRepresentatives.AddRange(representatives);

            // At most one already-issued lookahead debt survives per stable health-risk bucket.
            // New tickets share one global newest slot below, keeping the combined hard cap six.
            SearchNode? inFlight = FindActiveCycleExitCandidate(
                representatives,
                leased,
                root.InitialPlayerMaxHp,
                CycleExitCandidateRank.InFlight);
            if (inFlight != null)
                leased.Add(inFlight);
        }

        SearchNode? newest = FindActiveCycleExitCandidate(
            newestRepresentatives,
            leased,
            root.InitialPlayerMaxHp,
            CycleExitCandidateRank.Newest);
        if (newest != null && !ContainsReference(leased, newest))
            leased.Add(newest);
        if (leased.Count > 6)
            throw new InvalidOperationException("循环出口 portfolio 超过 6 条硬上限。");

        int rank = 0;
        foreach (SearchNode candidate in leased)
        {
            candidate.CycleExitRetentionRank = _profile.BeamWidth + 4 + rank++;
            if (!selectedSet.Add(candidate))
                continue;
            candidate.RetentionRank = int.MaxValue;
            candidate.LongTermResourceRetentionRank = int.MaxValue;
            candidate.CycleRetentionRank = int.MaxValue;
            selected.Add(candidate);
        }

        // Ticket settlement is intentionally delayed until the final Prune survivor set is
        // known. Region retention and the in-Prune primary-incumbent bound run after this
        // portfolio; any later destructive filter must invoke the same settlement finalizer.
    }

    private enum CycleExitCandidateRank : byte
    {
        InFlight,
        Newest,
    }

    private static SearchNode? FindActiveCycleExitCandidate(
        IReadOnlyList<SearchNode> representatives,
        IReadOnlyList<SearchNode> bandLeases,
        int bestMaxHp,
        CycleExitCandidateRank rank)
    {
        while (true)
        {
            SearchNode? best = null;
            foreach (SearchNode candidate in representatives)
            {
                if (ContainsReference(bandLeases, candidate)
                    || candidate.CycleExitProbe is not { } probe
                    || rank == CycleExitCandidateRank.InFlight && !probe.LeaseIssued
                    || rank == CycleExitCandidateRank.Newest && probe.LeaseIssued)
                {
                    continue;
                }
                if (best == null
                    || CompareCycleExitCandidates(candidate, best, bestMaxHp, rank) < 0)
                {
                    best = candidate;
                }
            }
            if (best == null || TryLeaseCycleExitCandidate(best))
                return best;
            // A newer pending generation can supersede siblings created in the same wave.
            // Never let that stale ticket consume one of the two bounded portfolio slots.
        }
    }

    private static bool TryLeaseCycleExitCandidate(SearchNode candidate)
    {
        CycleExitProbeState probe = candidate.CycleExitProbe
            ?? throw new InvalidOperationException("循环出口探测候选缺少票据。");
        // Once a ticket has been issued, every exact simulator child produced from that
        // branch owns an independent bounded continuation. One sibling may reach a terminal
        // or budget boundary before another; settling the tracker generation must not revoke
        // the already-issued lease carried by the latter sibling.
        if (probe.LeaseIssued)
            return true;
        if (!probe.OriginTracker.TryMarkExitProbeIssued(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration))
        {
            candidate.CycleExitProbe = null;
            return false;
        }
        candidate.CycleExitProbe = probe with { LeaseIssued = true };
        return true;
    }

    private static bool ContainsReference(
        IReadOnlyList<SearchNode> candidates,
        SearchNode target)
    {
        foreach (SearchNode candidate in candidates)
        {
            if (ReferenceEquals(candidate, target))
                return true;
        }
        return false;
    }

    private static int CompareCycleExitFamilyCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        // A freshly issued ticket is already an in-flight obligation even before its first
        // expansion consumes horizon. Prefer it over pending siblings from the same exact
        // family so the per-bucket debt lane cannot silently discard a live ticket.
        int leftLeasePriority = left.CycleExitProbe is { LeaseIssued: true } ? 0 : 1;
        int rightLeasePriority = right.CycleExitProbe is { LeaseIssued: true } ? 0 : 1;
        int comparison = leftLeasePriority.CompareTo(rightLeasePriority);
        if (comparison != 0)
            return comparison;
        comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue)
            .CompareTo(right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
        if (comparison != 0)
            return comparison;
        comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static int CompareCycleExitCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp,
        CycleExitCandidateRank rank)
        => rank switch
        {
            CycleExitCandidateRank.InFlight => CompareCycleExitInFlightCandidates(
                left,
                right,
                bestMaxHp),
            CycleExitCandidateRank.Newest => CompareCycleExitNewestCandidates(
                left,
                right,
                bestMaxHp),
            _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, null),
        };

    private static int CompareCycleExitInFlightCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        int comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue)
            .CompareTo(right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
        if (comparison != 0)
            return comparison;
        comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static int CompareCycleExitNewestCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        int comparison = (right.CycleExitProbe?.OriginNode.ActionCount ?? 0)
            .CompareTo(left.CycleExitProbe?.OriginNode.ActionCount ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = (right.CycleExitProbe?.OriginGeneration ?? 0)
            .CompareTo(left.CycleExitProbe?.OriginGeneration ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static CycleExitProbeFamilyKey BuildCycleExitProbeFamilyKey(SearchNode node)
    {
        CycleExitProbeState probe = node.CycleExitProbe
            ?? throw new InvalidOperationException("循环出口探测候选缺少族证据。");
        return new CycleExitProbeFamilyKey(
            probe.OriginShapeKey,
            probe.OriginSequenceKey,
            probe.OriginPeriodActions,
            probe.OriginPhaseIndex,
            probe.OriginTracker,
            probe.OriginGeneration,
            probe.ExitActionKey);
    }

    private static CycleExitProbeTicketKey BuildCycleExitProbeTicketKey(SearchNode node)
    {
        CycleExitProbeState probe = node.CycleExitProbe
            ?? throw new InvalidOperationException("循环出口探测候选缺少票据。");
        return new CycleExitProbeTicketKey(
            probe.OriginTracker,
            probe.OriginPhaseIndex,
            probe.ExitActionKey,
            probe.OriginGeneration);
    }

    private void AddCrossTurnPortfolio(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        HashSet<SearchNode> selectedSet)
    {
        List<SearchNode> eligible = [];
        int bestMaxHp = int.MinValue;
        foreach (SearchNode node in pool)
        {
            if (node.CrossTurnProbe == null && !RequiresCrossTurnPlanning(node))
                continue;
            eligible.Add(node);
            bestMaxHp = Math.Max(bestMaxHp, node.Snapshot.PlayerMaxHp);
        }
        if (eligible.Count == 0)
            return;

        long minimumHealthRisk = long.MaxValue;
        foreach (SearchNode node in eligible)
            minimumHealthRisk = Math.Min(minimumHealthRisk, CycleHealthRisk(node, bestMaxHp));
        int availableFutureSoldHp = Math.Max(
            0,
            SoldHpThreshold() - battleDamage.SoldHpCommitted);
        List<SearchNode> retained = [];
        foreach (bool investmentBand in new[] { false, true })
        {
            bool InBand(SearchNode node)
                => (node.FutureSoldHp > availableFutureSoldHp
                        || CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk)
                    == investmentBand;

            Dictionary<CrossTurnProbeFamilyKey, int> inFlightIndexes = [];
            List<SearchNode> inFlight = [];
            Dictionary<CrossTurnProbeFamilyKey, int> newFamilyIndexes = [];
            List<SearchNode> newFamilies = [];
            foreach (SearchNode node in eligible)
            {
                if (!InBand(node))
                    continue;
                if (node.CrossTurnProbe != null)
                {
                    AddCrossTurnFamilyBest(
                        inFlightIndexes,
                        inFlight,
                        node,
                        bestMaxHp);
                }
                else
                {
                    AddCrossTurnFamilyBest(
                        newFamilyIndexes,
                        newFamilies,
                        node,
                        bestMaxHp);
                }
            }

            List<SearchNode> band = [];
            SearchNode? continuing = FindBestCrossTurnCandidate(inFlight, bestMaxHp);
            if (continuing != null)
                band.Add(continuing);
            SearchNode? newest = FindBestCrossTurnCandidate(newFamilies, bestMaxHp);
            if (newest != null)
                band.Add(newest);

            SearchNode? fallbackFirst = null;
            SearchNode? fallbackSecond = null;
            foreach (SearchNode candidate in inFlight)
            {
                AddCrossTurnFallbackCandidate(
                    candidate,
                    band,
                    bestMaxHp,
                    ref fallbackFirst,
                    ref fallbackSecond);
            }
            foreach (SearchNode candidate in newFamilies)
            {
                AddCrossTurnFallbackCandidate(
                    candidate,
                    band,
                    bestMaxHp,
                    ref fallbackFirst,
                    ref fallbackSecond);
            }
            if (band.Count < 2 && fallbackFirst != null)
                band.Add(fallbackFirst);
            if (band.Count < 2 && fallbackSecond != null)
                band.Add(fallbackSecond);
            foreach (SearchNode candidate in band)
                retained.Add(candidate);
        }

        HashSet<SearchNode> retainedSet = new(retained, ReferenceEqualityComparer.Instance);
        foreach (SearchNode node in pool)
        {
            if (node.CrossTurnProbe != null && !retainedSet.Contains(node))
                node.CrossTurnProbe = null;
        }

        int rank = 0;
        foreach (SearchNode candidate in retained)
        {
            if (candidate.CrossTurnProbe == null)
                StartCrossTurnProbe(candidate);
            candidate.CrossTurnRetentionRank = _profile.BeamWidth + 8 + rank++;
            if (!selectedSet.Add(candidate))
                continue;
            candidate.RetentionRank = int.MaxValue;
            candidate.LongTermResourceRetentionRank = int.MaxValue;
            candidate.CycleRetentionRank = int.MaxValue;
            candidate.CycleExitRetentionRank = int.MaxValue;
            selected.Add(candidate);
        }
    }

    private static void AddCrossTurnFamilyBest(
        Dictionary<CrossTurnProbeFamilyKey, int> familyIndexes,
        List<SearchNode> familyBest,
        SearchNode candidate,
        int bestMaxHp)
    {
        CrossTurnProbeFamilyKey family = BuildCrossTurnProbeFamilyKey(candidate);
        if (!familyIndexes.TryGetValue(family, out int index))
        {
            familyIndexes.Add(family, familyBest.Count);
            familyBest.Add(candidate);
            return;
        }
        if (CompareCrossTurnCandidates(candidate, familyBest[index], bestMaxHp) < 0)
            familyBest[index] = candidate;
    }

    private static SearchNode? FindBestCrossTurnCandidate(
        IReadOnlyList<SearchNode> candidates,
        int bestMaxHp)
    {
        SearchNode? best = null;
        foreach (SearchNode candidate in candidates)
        {
            if (best == null || CompareCrossTurnCandidates(candidate, best, bestMaxHp) < 0)
                best = candidate;
        }
        return best;
    }

    private static void AddCrossTurnFallbackCandidate(
        SearchNode candidate,
        IReadOnlyList<SearchNode> alreadySelected,
        int bestMaxHp,
        ref SearchNode? first,
        ref SearchNode? second)
    {
        if (ContainsReference(alreadySelected, candidate))
            return;
        if (first == null || CompareCrossTurnCandidates(candidate, first, bestMaxHp) < 0)
        {
            second = first;
            first = candidate;
        }
        else if (second == null
                 || CompareCrossTurnCandidates(candidate, second, bestMaxHp) < 0)
        {
            second = candidate;
        }
    }

    private static int CompareCrossTurnCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        int comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        bool leftChanged = left.CrossTurnProbe?.LastTurnChangedSemanticState
            ?? left.CrossTurnSemanticStateChanged;
        bool rightChanged = right.CrossTurnProbe?.LastTurnChangedSemanticState
            ?? right.CrossTurnSemanticStateChanged;
        comparison = rightChanged.CompareTo(leftChanged);
        if (comparison != 0)
            return comparison;
        int leftConsecutiveChanges =
            left.CrossTurnProbe?.ConsecutiveSemanticStateChangeTransitions
                ?? (left.CrossTurnSemanticStateChanged ? 1 : 0);
        int rightConsecutiveChanges =
            right.CrossTurnProbe?.ConsecutiveSemanticStateChangeTransitions
                ?? (right.CrossTurnSemanticStateChanged ? 1 : 0);
        comparison = rightConsecutiveChanges.CompareTo(leftConsecutiveChanges);
        if (comparison != 0)
            return comparison;
        int leftChanges = left.CrossTurnProbe?.SemanticStateChangeTransitions
            ?? (left.CrossTurnSemanticStateChanged ? 1 : 0);
        int rightChanges = right.CrossTurnProbe?.SemanticStateChangeTransitions
            ?? (right.CrossTurnSemanticStateChanged ? 1 : 0);
        comparison = rightChanges.CompareTo(leftChanges);
        if (comparison != 0)
            return comparison;
        comparison = (right.CrossTurnProbe?.CompletedTurnTransitions ?? 0)
            .CompareTo(left.CrossTurnProbe?.CompletedTurnTransitions ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = right.CombatProgress.TurnsWithoutProgress.CompareTo(
            left.CombatProgress.TurnsWithoutProgress);
        if (comparison != 0)
            return comparison;
        comparison = (right.CrossTurnProbe?.BestKnownProgressMagnitude ?? 0)
            .CompareTo(left.CrossTurnProbe?.BestKnownProgressMagnitude ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static int CompareCycleProbeCandidates(
        SearchNode left,
        SearchNode right,
        int bestMaxHp)
    {
        // Finish an already-issued exact phase lease before rotating to another family.
        // The lease remains bounded by the repetition budget and never affects final quality.
        int comparison = CycleHealthRisk(left, bestMaxHp)
            .CompareTo(CycleHealthRisk(right, bestMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = left.PotionStrategicCost.CompareTo(right.PotionStrategicCost);
        if (comparison != 0)
            return comparison;
        comparison = left.Turn.CompareTo(right.Turn);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.Snapshot.ProjectedPlayerHp.CompareTo(
            left.Snapshot.ProjectedPlayerHp);
        if (comparison != 0)
            return comparison;
        comparison = (right.Cycle?.TotalStructuralRepetitions ?? 0)
            .CompareTo(left.Cycle?.TotalStructuralRepetitions ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        return comparison != 0
            ? comparison
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }

    private static CrossTurnProbeFamilyKey BuildCrossTurnProbeFamilyKey(SearchNode node)
        => node.CrossTurnProbe is { } probe
            ? new CrossTurnProbeFamilyKey(
                probe.Tracker.OriginShapeKey,
                probe.Tracker.OriginNode.StateKey,
                node.PotionCount,
                probe.Tracker)
            : new CrossTurnProbeFamilyKey(
                node.Snapshot.CycleShapeKey,
                node.StateKey,
                node.PotionCount,
                null);

    private CycleProbeFamilyKey BuildCycleProbeFamilyKey(SearchNode node)
    {
        if (node.CycleProbeLease is { } lease)
        {
            return new CycleProbeFamilyKey(
                node.Turn,
                lease.Tracker.ShapeKey,
                lease.Tracker.SequenceKey,
                lease.Tracker.PeriodActions,
                CycleStartupHealthRiskBucket(node),
                lease.Tracker);
        }
        CycleSearchState cycle = node.Cycle
            ?? throw new InvalidOperationException("循环探测候选缺少族证据。");
        return new CycleProbeFamilyKey(
            node.Turn,
            cycle.ShapeKey,
            cycle.SequenceKey,
            cycle.PeriodActions,
            CycleStartupHealthRiskBucket(node),
            null);
    }

    private static SearchNode? FindOpeningCardNode(SearchNode node)
    {
        SearchNode? opening = null;
        for (SearchNode? cursor = node; cursor?.Action != null; cursor = cursor.Parent)
        {
            if (cursor.Action.Kind == PlanActionKind.PlayCard)
                opening = cursor;
        }
        return opening;
    }

    private void CaptureContinuation(SearchNode node)
    {
        if (node.Action is not { } action
            || action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn
            || node.Snapshot.Continuation != null
            || node.Snapshot.PlayerDead
            || node.Snapshot.AllEnemiesDead
            || node.Snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            return;
        }
        node.Snapshot.SetContinuation(ContinuationStamp.CapturePredicted(
            _player,
            node.Snapshot.Simulator,
            node.Turn,
            _forecast,
            _startTurnNumber));
    }

    private static void ValidateHistoricalSimulatorsReleased(IReadOnlyList<SearchNode> candidates)
    {
        foreach (SearchNode candidate in candidates)
        {
            for (SearchNode? parent = candidate.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.Snapshot.HasSimulator)
                    throw new InvalidOperationException("历史搜索节点仍在保留完整模拟器。");
            }
        }
    }

    private static void ReleaseDroppedSnapshots(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyList<SearchNode> retained)
    {
        foreach (SearchNode candidate in candidates)
        {
            bool keepSnapshot = false;
            foreach (SearchNode survivor in retained)
            {
                if (!ReferenceEquals(candidate.Snapshot, survivor.Snapshot))
                    continue;
                keepSnapshot = true;
                break;
            }
            if (!keepSnapshot)
                candidate.Snapshot.ReleaseSimulator();
        }
    }

    private static List<SearchNode> FinalizePrunedCycleExitProbeTickets(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> retained)
    {
        SettleDroppedCycleExitProbeTickets(pool, retained);
        return retained;
    }

    private static void SettleDroppedCycleExitProbeTickets(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyList<SearchNode> retained)
    {
        HashSet<SearchNode> retainedSet = new(
            retained,
            ReferenceEqualityComparer.Instance);
        HashSet<CycleExitProbeTicketKey> survivingIssuedTickets = [];
        List<CycleExitProbeTicketKey> issuedCandidates = [];

        foreach (SearchNode survivor in retained)
        {
            if (survivor.CycleExitProbe is not { } probe)
                continue;
            bool retainsPortfolioLease = survivor.CycleExitRetentionRank != int.MaxValue;
            if (probe.LeaseIssued)
            {
                CycleExitProbeTicketKey ticket = BuildCycleExitProbeTicketKey(survivor);
                if (retainsPortfolioLease)
                    survivingIssuedTickets.Add(ticket);
                else
                    issuedCandidates.Add(ticket);
            }
            if (!retainsPortfolioLease)
            {
                // Ordinary Beam/long-term retention may keep the route, but it cannot bypass
                // the bounded exit-probe portfolio merely by surviving another prune channel.
                survivor.CycleExitProbe = null;
            }
        }

        foreach (SearchNode candidate in candidates)
        {
            bool retainsPortfolioLease = retainedSet.Contains(candidate)
                && candidate.CycleExitRetentionRank != int.MaxValue;
            if (candidate.CycleExitProbe is { LeaseIssued: true })
                issuedCandidates.Add(BuildCycleExitProbeTicketKey(candidate));
            if (!retainsPortfolioLease)
                candidate.CycleExitProbe = null;
        }

        SettleAbandonedCycleExitProbeTickets(
            issuedCandidates,
            survivingIssuedTickets);
    }

    private static void SettleAbandonedCycleExitProbeTickets(
        IEnumerable<CycleExitProbeTicketKey> issuedCandidates,
        IEnumerable<CycleExitProbeTicketKey> survivingIssuedTickets)
    {
        HashSet<CycleExitProbeTicketKey> surviving = [.. survivingIssuedTickets];
        HashSet<CycleExitProbeTicketKey> settled = [];
        foreach (CycleExitProbeTicketKey ticket in issuedCandidates)
        {
            if (surviving.Contains(ticket) || !settled.Add(ticket))
                continue;
            // Settle one whole ticket, not each sibling. Losing one branch while another
            // survives must never mint duplicate generations.
            ticket.OriginTracker.RetryAbandonedExitProbe(
                ticket.OriginPhaseIndex,
                ticket.ExitActionKey,
                ticket.OriginGeneration);
        }
    }

    internal static void VerifyCycleExitTicketSettlementPolicyForTesting()
    {
        VerifyCycleStartupRetentionPolicyForTesting();

        StateFingerprint shapeKey = new(0x1001UL, 0x1002UL);
        StateFingerprint sequenceKey = new(0x2001UL, 0x2002UL);
        StateFingerprint firstActionKey = new(0x3001UL, 0x3002UL);
        StateFingerprint droppedActionKey = new(0x4001UL, 0x4002UL);
        CycleProbeTracker tracker = new(
            shapeKey,
            sequenceKey,
            [firstActionKey],
            default);
        long firstGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        long droppedGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (!tracker.TryMarkExitProbeIssued(0, firstActionKey, firstGeneration)
            || !tracker.TryMarkExitProbeIssued(0, droppedActionKey, droppedGeneration))
        {
            throw new InvalidOperationException("循环出口测试票据无法签发。");
        }

        int activeBefore = tracker.ActiveExitProbeTicketCountForTesting;
        CycleExitProbeTicketKey survivor = new(
            tracker,
            0,
            firstActionKey,
            firstGeneration);
        CycleExitProbeTicketKey dropped = new(
            tracker,
            0,
            droppedActionKey,
            droppedGeneration);
        SettleAbandonedCycleExitProbeTickets(
            [survivor, survivor, dropped, dropped],
            [survivor]);
        long survivingPendingGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        if (survivingPendingGeneration != 0)
        {
            throw new InvalidOperationException(
                "同一出口票据仍有 sibling 存活时被错误 rearm。");
        }
        long rearmedGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (!tracker.HasPendingExitProbe(0, droppedActionKey, rearmedGeneration)
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore)
        {
            throw new InvalidOperationException(
                "同一出口票据的全部 sibling 删除后没有唯一变回 pending，或 ActiveTickets 发生增长。");
        }
        tracker.RearmExitProbes();
        long checkpointGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        if (checkpointGeneration <= firstGeneration
            || !tracker.HasPendingExitProbe(0, firstActionKey, checkpointGeneration)
            || !tracker.HasPendingExitProbe(0, droppedActionKey, rearmedGeneration)
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore + 1)
        {
            throw new InvalidOperationException(
                "循环检查点没有在保留旧探测的同时唯一签发新一代出口票据。");
        }
        tracker.RearmExitProbes();
        long repeatedCheckpointGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        long repeatedRearmGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (repeatedCheckpointGeneration != checkpointGeneration
            || repeatedRearmGeneration != rearmedGeneration
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore + 1)
        {
            throw new InvalidOperationException(
                "同一循环检查点被重复处理时重复签发了出口票据。");
        }
    }

    private static void VerifyCycleStartupRetentionPolicyForTesting()
    {
        const int initialPlayerHp = 17;
        long[] risks = [0, 1, 5, 9, 13];
        int[] expectedBuckets = [0, 1, 2, 3, 4];
        for (int index = 0; index < risks.Length; index++)
        {
            int bucket = CycleStartupHealthRiskBucket(initialPlayerHp, risks[index]);
            if (bucket != expectedBuckets[index])
            {
                throw new InvalidOperationException(
                    $"循环 startup 风险桶错误：risk={risks[index]}，" +
                    $"bucket={bucket}/{expectedBuckets[index]}。");
            }
        }
        if (CanOccupyCycleStartupReserve(0)
            || CanOccupyCycleStartupReserve(-1)
            || !CanOccupyCycleStartupReserve(1))
        {
            throw new InvalidOperationException(
                "循环 startup reserve 没有严格拒绝 projected HP 非正的路线。");
        }

        static CycleStartupRetentionKey Key(
            long risk,
            int potionCost,
            int clutter,
            int size,
            long setup,
            ulong stable)
            => new(
                CycleStartupHealthRiskBucket(initialPlayerHp, risk),
                risk,
                potionCost,
                clutter,
                size,
                setup,
                new StateFingerprint(stable, stable + 100));

        CycleStartupRetentionTestCandidate[] candidates =
        [
            new(0, 1, Key(0, 0, 4, 10, 1, 10)),
            new(1, 1, Key(1, 0, 3, 10, 99, 11)),
            // The deep edge of a bucket wins before potion/deck/setup tie-breakers.
            new(2, 1, Key(4, 9, 8, 20, 0, 12)),
            // Global purification prefers the smallest clutter/size, independently of depth.
            new(3, 1, Key(5, 1, 0, 7, 2, 13)),
            new(4, 1, Key(8, 0, 5, 12, 50, 14)),
            new(5, 1, Key(9, 0, 4, 11, 3, 15)),
            new(6, 1, Key(13, 0, 4, 11, 3, 16)),
            // A numerically attractive but non-surviving startup may never own a reserve.
            new(7, 0, Key(4, 0, 0, 1, 999, 1)),
        ];
        static bool Eligible(CycleStartupRetentionTestCandidate candidate)
            => CanOccupyCycleStartupReserve(candidate.ProjectedPlayerHp);
        static CycleStartupRetentionKey SelectKey(
            CycleStartupRetentionTestCandidate candidate)
            => candidate.Key;

        static int[] SelectBucketIdentities(
            IReadOnlyList<CycleStartupRetentionTestCandidate> source)
        {
            int[] identities = new int[5];
            for (int bucket = 0; bucket <= 4; bucket++)
            {
                CycleStartupRetentionTestCandidate selected =
                    SelectCycleStartupBucketRepresentative(
                        source,
                        bucket,
                        Eligible,
                        SelectKey)
                    ?? throw new InvalidOperationException(
                        $"循环 startup 测试缺少第 {bucket} 桶代表。");
                identities[bucket] = selected.Identity;
            }
            return identities;
        }

        int[] expectedIdentities = [0, 2, 4, 5, 6];
        int[] forward = SelectBucketIdentities(candidates);
        CycleStartupRetentionTestCandidate[] reversed = [.. candidates.Reverse()];
        int[] backward = SelectBucketIdentities(reversed);
        if (!forward.SequenceEqual(expectedIdentities)
            || !backward.SequenceEqual(expectedIdentities))
        {
            throw new InvalidOperationException(
                "循环 startup 桶内深端选择错误，或结果受候选遍历顺序影响。");
        }

        StateFingerprint sharedState = new(700, 701);
        StateFingerprint sharedParent = new(800, 801);
        StateFingerprint firstRoute = BuildCycleStartupStableFingerprint(
            sharedState,
            new StateFingerprint(900, 901),
            sharedParent);
        StateFingerprint secondRoute = BuildCycleStartupStableFingerprint(
            sharedState,
            new StateFingerprint(902, 903),
            sharedParent);
        if (firstRoute == secondRoute)
            throw new InvalidOperationException("循环 startup 稳定键没有区分同状态的不同动作路线。");
        CycleStartupRetentionTestCandidate[] tiedStates =
        [
            new(30, 1, Key(1, 0, 4, 10, 1, 30) with
            {
                StableFingerprint = firstRoute,
            }),
            new(31, 1, Key(1, 0, 4, 10, 1, 31) with
            {
                StableFingerprint = secondRoute,
            }),
        ];
        CycleStartupRetentionTestCandidate tiedForward =
            SelectCycleStartupBucketRepresentative(
                tiedStates,
                1,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("循环 startup 同状态稳定性测试没有代表。");
        CycleStartupRetentionTestCandidate tiedBackward =
            SelectCycleStartupBucketRepresentative(
                tiedStates.Reverse(),
                1,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("逆序循环 startup 同状态稳定性测试没有代表。");
        if (tiedForward.Identity != tiedBackward.Identity)
        {
            throw new InvalidOperationException(
                "循环 startup 同状态路线的选择仍受候选遍历顺序影响。");
        }

        CycleStartupRetentionTestCandidate forwardAnchor = SelectCyclePurificationAnchor(
                candidates,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("循环 purification anchor 意外缺失。");
        CycleStartupRetentionTestCandidate reverseAnchor = SelectCyclePurificationAnchor(
                reversed,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("逆序循环 purification anchor 意外缺失。");
        if (forwardAnchor.Identity != 3
            || reverseAnchor.Identity != 3
            || expectedIdentities.Append(forwardAnchor.Identity).Distinct().Count() != 6)
        {
            throw new InvalidOperationException(
                "循环 purification anchor 没有稳定选择真实 deck 净化路线，或组合上限失效。");
        }

        CycleStartupRetentionTestCandidate[] uniformDeck =
        [
            new(20, 1, Key(1, 0, 4, 10, 1, 20)),
            new(21, 1, Key(4, 0, 4, 10, 99, 21)),
        ];
        if (SelectCyclePurificationAnchor(
                uniformDeck,
                Eligible,
                SelectKey) != null)
        {
            throw new InvalidOperationException(
                "候选集没有 deck clutter/size 改善时仍占用了 purification reserve。");
        }

        CycleStartupRetentionTestCandidate stableHigh =
            new(30, 1, Key(1, 0, 4, 10, 1, 30));
        CycleStartupRetentionTestCandidate stableLow =
            new(31, 1, Key(1, 0, 4, 10, 1, 29));
        foreach (CycleStartupRetentionTestCandidate[] order in
                 new[]
                 {
                     new[] { stableHigh, stableLow },
                     new[] { stableLow, stableHigh },
                 })
        {
            if (SelectCycleStartupBucketRepresentative(
                    order,
                    1,
                    Eligible,
                    SelectKey)?.Identity != stableLow.Identity)
            {
                throw new InvalidOperationException(
                    "循环 startup 完全同质候选没有按稳定 fingerprint 决胜。");
            }
        }
    }

    private static string SummarizePotionCandidates(IEnumerable<SearchNode> nodes)
    {
        string summary = string.Join(';', nodes
            .GroupBy(node => node.PotionCount)
            .OrderBy(group => group.Key)
            .Select(group =>
                $"{group.Key}:{group.Count()}:hp{group.Max(node => node.Snapshot.ProjectedPlayerHp)}:" +
                $"enemy{group.Min(node => node.Snapshot.EnemyHp)}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizeDiagnosticRoutes(
        IEnumerable<SearchNode> nodes,
        int limit)
    {
        string summary = string.Join(';', nodes
            .Take(limit)
            .Select(node =>
                $"{string.Join('>', node.Actions.Select(PolicyActionIdentityToken))}:" +
                $"score{node.Score:F0}:hp{node.Snapshot.ProjectedPlayerHp}:" +
                $"enemy{node.Snapshot.EnemyHp}:hand{node.Snapshot.HandCount}/" +
                $"{node.Snapshot.ReachableHandValue}/{node.Snapshot.ZeroCostPlayableCount}:" +
                $"traits{node.Traits}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizeOpeningLineages(IEnumerable<SearchNode> nodes)
    {
        string summary = string.Join(';', nodes
            .GroupBy(node => (
                node.PotionCount,
                FirstCardId: node.Actions.FirstOrDefault(action =>
                    action.Kind == PlanActionKind.PlayCard)?.CardId ?? "-"))
            .OrderBy(group => group.Key.PotionCount)
            .ThenBy(group => group.Key.FirstCardId, StringComparer.Ordinal)
            .Select(group =>
                $"p{group.Key.PotionCount}/{group.Key.FirstCardId}:{group.Count()}:" +
                $"hp{group.Max(node => node.Snapshot.ProjectedPlayerHp)}:" +
                $"setup{group.Max(node => node.Snapshot.StrategicEffects.RetentionValue)}:" +
                $"order{group.Max(node => node.Snapshot.ProjectedShuffleOrderValue)}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizePotionChoiceTargets(
        IEnumerable<SearchNode> nodes,
        string potionId)
    {
        string summary = string.Join(',', nodes
            .Select(node => node.Actions.LastOrDefault(action =>
                action.Kind == PlanActionKind.UsePotion
                && string.Equals(action.PotionId, potionId, StringComparison.Ordinal))?.Choice)
            .Where(choice => choice != null)
            .Select(choice => choice!.Cards.Count == 0
                ? "skip"
                : string.Join('+', choice.Cards.Select(card => card.CardId)))
            .GroupBy(cardIds => cardIds, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static RoutingChoiceSignature? CurrentTurnRoutingChoice(SearchNode node)
        => BeamRetentionPolicy.CurrentTurnRoutingChoice(node);

    private StandPatEvaluation EvaluateStandPat(SearchNode node)
    {
        if (_run.StandPatCache.TryGetValue(node.StateKey, out StandPatEvaluation cached))
            return cached;
        SimulationSnapshot end = ReplayAction(node, new PlanAction(PlanActionKind.EndTurn, node.Turn));
        StandPatEvaluation evaluation = new(
            end.AllEnemiesDead,
            Math.Max(0, node.Snapshot.EnemyHp - end.EnemyHp),
            end.ProjectedPlayerHp,
            end.Energy * 16
                + end.Stars * 8
                + end.HandCount
                + end.ReachableHandValue
                + end.FutureResourceValue
                + end.OstyHp * 16
                + end.OstyMaxHp * 4);
        end.ReleaseSimulator();
        _run.StandPatCache.Add(node.StateKey, evaluation);
        _run.StandPatProbes++;
        return evaluation;
    }

    private static int PolicyBoundaryRank(SearchBoundaryReason reason)
        => reason switch
        {
            SearchBoundaryReason.None => 0,
            SearchBoundaryReason.NoCards or SearchBoundaryReason.Shuffle
                or SearchBoundaryReason.TurnLimit or SearchBoundaryReason.NodeLimit
                or SearchBoundaryReason.TimeLimit => 1,
            SearchBoundaryReason.PendingChoice => 2,
            SearchBoundaryReason.UnsupportedEffect => 3,
            SearchBoundaryReason.EventDefeat => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    private static string PolicyActionToken(PlanAction action)
        => action.Kind switch
        {
            PlanActionKind.PlayCard => action.Choice == null
                ? $"{action.Turn}:C:{action.CardId}"
                : $"{action.Turn}:C:{action.CardId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.UsePotion => action.Choice == null
                ? $"{action.Turn}:P:{action.PotionId}"
                : $"{action.Turn}:P:{action.PotionId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.EndTurn => action.TurnStartChoices is not { Count: > 0 }
                ? $"{action.Turn}:E"
                : $"{action.Turn}:E:" + string.Join(';', action.TurnStartChoices.Select(choice =>
                    $"{choice.SourceId}={string.Join(',', choice.Cards.Select(card => card.CardId))}")),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null),
        };

    private static string PolicyActionIdentityToken(PlanAction action)
    {
        string token = PolicyActionToken(action);
        if (action.Kind == PlanActionKind.PlayCard)
        {
            token += $"#card{action.CardOccurrence}/state{action.CardStateOccurrence}";
        }
        if (action.Choice != null)
            token += $"#primary={PolicyChoiceIdentityToken(action.Choice)}";
        if (action.NestedChoices is { Count: > 0 })
        {
            token += $"#nested_before={action.NestedChoicesBeforePrimary}:" +
                string.Join(',', action.NestedChoices.Select(PolicyChoiceIdentityToken));
        }
        return token;
    }

    private static string PolicyChoiceIdentityToken(PlanCardChoice choice)
        => $"{choice.Effect}[{string.Join(',', choice.Cards.Select(card =>
            $"{card.CardId}+{card.UpgradeLevel}@src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))}]";

}
