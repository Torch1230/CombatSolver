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
        CycleProbeTracker? Tracker);

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
            // Prune 只读取 pool，从不改动它，调用方传进来的若已经是 List 就直接沿用，
            // 省掉一次整张候选表的复制。
            List<SearchNode> pool = nodes as List<SearchNode> ?? nodes.ToList();
            List<SearchNode> global = Retention.RankBest(
                pool,
                _profile.BeamWidth,
                preserveDefensiveRoute: true);
            List<SearchNode> selected = [.. global];
            HashSet<SearchNode> selectedSet = new(global, ReferenceEqualityComparer.Instance);
            // RankBest 的返回表按引用去重，保存/还原名次只需要一条与它同序的并行数组，
            // 还原顺序与原来按插入序枚举字典完全一致。
            int[] globalRetentionRanks = new int[global.Count];
            for (int index = 0; index < global.Count; index++)
                globalRetentionRanks[index] = global[index].RetentionRank;
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
            for (int index = 0; index < global.Count; index++)
                global[index].RetentionRank = globalRetentionRanks[index];
            foreach (SearchNode candidate in longTermResource
                         .OrderBy(node => node.RetentionRank)
                         .ThenByDescending(node => node.Score))
            {
                if (!selectedSet.Add(candidate))
                    continue;
                selected.Add(candidate);
            }
            AddCyclePortfolio(pool, selected, selectedSet);
            AddCycleExitPortfolio(pool, selected, selectedSet);
            AddCrossTurnPortfolio(pool, selected, selectedSet);
            SortRetained(selected);
            if (_profile.Phase != SolverSearchPhase.Deep || pool.Count <= _profile.BeamWidth)
                return ApplyPrimaryIncumbentBound(selected);
            if (!root.HasUnusedCardReplayAllocator)
                return ApplyPrimaryIncumbentBound(selected);

            int channelWidth = Math.Clamp(_profile.BeamWidth / 12, 6, 12);
            List<List<SearchNode>> openingChannels = pool
                .Select(node => (Node: node, Opening: FindOpeningCardNode(node)))
                .Where(item => item.Opening?.Parent is { } parent
                    && (item.Opening.Snapshot.PersistentBuffValue > parent.Snapshot.PersistentBuffValue
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
            if (openingChannels.Count == 0)
                return ApplyPrimaryIncumbentBound(selected);

            int expandedLimit = Math.Min(
                pool.Count,
                checked(selected.Count + Math.Max(12, _profile.BeamWidth / 3)));
            for (int round = 0;
                 selected.Count < expandedLimit && HasChannelAtRound(openingChannels, round);
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
            SortRetained(selected);
            return ApplyPrimaryIncumbentBound(selected);
        }
        finally
        {
            _run.Performance.End(SearchMetricPhase.Prune, measurement);
        }
    }

    // 循环条件里的 Any(lambda) 捕获 round，每轮都要新建闭包与委托。
    private static bool HasChannelAtRound(List<List<SearchNode>> channels, int round)
    {
        foreach (List<SearchNode> channel in channels)
        {
            if (round < channel.Count)
                return true;
        }
        return false;
    }

    private List<SearchNode> ApplyPrimaryIncumbentBound(List<SearchNode> retained)
    {
        if (_primaryIncumbent is not { } incumbent)
            return retained;

        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            if (ShouldPruneByPrimaryIncumbent(
                    node.Snapshot.CumulativePlayerHpLost,
                    node.Turn,
                    incumbent))
            {
                bounded ??= new List<SearchNode>(retained.Count);
                if (bounded.Count == 0 && index > 0)
                    bounded.AddRange(retained.GetRange(0, index));
                _run.PrimaryIncumbentBranchesPruned++;
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
        ref PrimarySearchIncumbent? incumbent)
    {
        if (auditedPotionFreeBaseline is not { } baseline
            || minimumPotionUses <= 0
            || maximumPotionUses != minimumPotionUses
            || !candidateCompleteVictory
            || !candidateSatisfiesHardRules
            || candidateExplicitPotionUses != minimumPotionUses
            || candidateCombatEndedTurn is not { } combatEndedTurn
            || SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit,
                candidateCombatEndedTurn,
                currentCompleteVictory: baseline.Won,
                currentStrategicHpDeficit: baseline.HpDeficit,
                currentCombatEndedTurn: baseline.CombatEndedTurn) >= 0)
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
        if (_potionFreePolicyBaseline == null
            || _minimumPotionUses <= 0
            || _maximumPotionUses != _minimumPotionUses
            // The strict-primary escape in FinalPlanOrdering is guaranteed to make an
            // exact-layer victory policy-eligible only when every explicit use is optional.
            // Smart-gradient exact layers use a policy override and therefore do not enforce
            // per-slot directives here. Future forced-directive exact solvers must prove their
            // optional-use facts separately before they may tighten this bound.
            || _enforcePotionDirectives)
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
                node.Action?.Turn,
                ref tightened);
        }

        if (Nullable.Equals(tightened, _primaryIncumbent))
            return false;

        PrimarySearchIncumbent? previous = _primaryIncumbent;
        _primaryIncumbent = tightened;
        _run.PrimaryIncumbentUpdates++;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] PRIMARY_INCUMBENT_UPDATE " +
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
        int bestMaxHp = int.MinValue;
        foreach (SearchNode node in pool)
        {
            if (node.CycleProbeLease == null && !RequiresBoundedCyclePlanning(node))
                continue;
            eligible.Add(node);
            bestMaxHp = Math.Max(bestMaxHp, node.Snapshot.PlayerMaxHp);
        }
        if (eligible.Count == 0)
            return;

        long minimumHealthRisk = long.MaxValue;
        foreach (SearchNode node in eligible)
            minimumHealthRisk = Math.Min(minimumHealthRisk, CycleHealthRisk(node, bestMaxHp));
        int familyQuotaPerBand = Math.Clamp(_profile.BeamWidth / 12, 1, 2);
        int totalFamilyQuota = familyQuotaPerBand * 2;
        List<SearchNode> leased = [];
        foreach (bool investmentBand in new[] { false, true })
        {
            Dictionary<CycleProbeFamilyKey, int> familyIndexes = [];
            List<SearchNode> familyBest = [];
            foreach (SearchNode node in eligible)
            {
                // An exact pattern that is mid-period gets priority only inside its
                // current health band. A newly arrived lower-risk family can therefore
                // pre-empt excess high-risk probes immediately.
                if (node.CycleProbeLease is not { NextActionIndex: > 0 }
                    || (CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk)
                        != investmentBand)
                {
                    continue;
                }
                AddCycleProbeFamilyBest(familyIndexes, familyBest, node, bestMaxHp);
            }
            AddTopCycleProbeCandidates(
                familyBest,
                familyQuotaPerBand,
                bestMaxHp,
                leased);
        }
        HashSet<(CycleProbeFamilyKey Family, bool InvestmentBand)> leasedFamilies = [];
        foreach (SearchNode node in leased)
        {
            leasedFamilies.Add((
                BuildCycleProbeFamilyKey(node),
                CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk));
        }
        foreach (bool investmentBand in new[] { false, true })
        {
            if (leased.Count >= totalFamilyQuota)
                break;
            int activeInBand = 0;
            foreach (SearchNode node in leased)
            {
                if ((CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk) == investmentBand)
                    activeInBand++;
            }
            int openBandSlots = Math.Max(0, familyQuotaPerBand - activeInBand);
            if (openBandSlots == 0)
                continue;

            Dictionary<CycleProbeFamilyKey, int> familyIndexes = [];
            List<SearchNode> familyBest = [];
            foreach (SearchNode node in eligible)
            {
                if (node.CycleProbeLease is { NextActionIndex: > 0 }
                    || (CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk)
                        != investmentBand)
                {
                    continue;
                }
                CycleProbeFamilyKey family = BuildCycleProbeFamilyKey(node);
                if (leasedFamilies.Contains((family, investmentBand)))
                    continue;
                AddCycleProbeFamilyBest(
                    familyIndexes,
                    familyBest,
                    family,
                    node,
                    bestMaxHp);
            }
            int previousCount = leased.Count;
            AddTopCycleProbeCandidates(
                familyBest,
                Math.Min(openBandSlots, totalFamilyQuota - leased.Count),
                bestMaxHp,
                leased);
            for (int index = previousCount; index < leased.Count; index++)
            {
                SearchNode candidate = leased[index];
                leasedFamilies.Add((BuildCycleProbeFamilyKey(candidate), investmentBand));
            }
        }

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

    private static long CycleHealthRisk(SearchNode node, int bestMaxHp)
        => (long)node.Snapshot.CumulativePlayerHpLost
            + node.FutureSoldHp
            + Math.Max(0, bestMaxHp - node.Snapshot.PlayerMaxHp);

    private static void AddCycleProbeFamilyBest(
        Dictionary<CycleProbeFamilyKey, int> familyIndexes,
        List<SearchNode> familyBest,
        SearchNode candidate,
        int bestMaxHp)
        => AddCycleProbeFamilyBest(
            familyIndexes,
            familyBest,
            BuildCycleProbeFamilyKey(candidate),
            candidate,
            bestMaxHp);

    private static void AddCycleProbeFamilyBest(
        Dictionary<CycleProbeFamilyKey, int> familyIndexes,
        List<SearchNode> familyBest,
        CycleProbeFamilyKey family,
        SearchNode candidate,
        int bestMaxHp)
    {
        if (!familyIndexes.TryGetValue(family, out int index))
        {
            familyIndexes.Add(family, familyBest.Count);
            familyBest.Add(candidate);
            return;
        }
        if (CompareCycleProbeCandidates(candidate, familyBest[index], bestMaxHp) < 0)
            familyBest[index] = candidate;
    }

    private static void AddTopCycleProbeCandidates(
        IReadOnlyList<SearchNode> candidates,
        int limit,
        int bestMaxHp,
        List<SearchNode> destination)
    {
        SearchNode? first = null;
        SearchNode? second = null;
        foreach (SearchNode candidate in candidates)
        {
            if (first == null
                || CompareCycleProbeCandidates(candidate, first, bestMaxHp) < 0)
            {
                second = first;
                first = candidate;
            }
            else if (second == null
                     || CompareCycleProbeCandidates(candidate, second, bestMaxHp) < 0)
            {
                second = candidate;
            }
        }
        if (limit > 0 && first != null)
            destination.Add(first);
        if (limit > 1 && second != null)
            destination.Add(second);
    }

    private void AddCycleExitPortfolio(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        HashSet<SearchNode> selectedSet)
    {
        List<SearchNode> eligible = [];
        int bestMaxHp = int.MinValue;
        foreach (SearchNode node in pool)
        {
            if (node.CycleExitProbe is not { RemainingActions: > 0 })
                continue;
            eligible.Add(node);
            bestMaxHp = Math.Max(bestMaxHp, node.Snapshot.PlayerMaxHp);
        }
        if (eligible.Count == 0)
            return;
        long minimumHealthRisk = long.MaxValue;
        foreach (SearchNode node in eligible)
            minimumHealthRisk = Math.Min(minimumHealthRisk, CycleHealthRisk(node, bestMaxHp));
        int rank = 0;
        List<SearchNode> leased = [];
        foreach (bool investmentBand in new[] { false, true })
        {
            Dictionary<CycleExitProbeFamilyKey, int> familyIndexes = [];
            List<SearchNode> representatives = [];
            foreach (SearchNode node in eligible)
            {
                if ((CycleHealthRisk(node, bestMaxHp) > minimumHealthRisk)
                    != investmentBand)
                {
                    continue;
                }
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
                        bestMaxHp) < 0)
                {
                    representatives[index] = node;
                }
            }
            if (representatives.Count == 0)
                continue;

            // Each health band has two bounded obligations: one finishes an already-issued
            // lookahead while one preserves the newest exact origin. A later generation can
            // therefore expose a hidden N-th-cycle payoff without cancelling the older probe.
            List<SearchNode> bandLeases = [];
            SearchNode? inFlight = FindActiveCycleExitCandidate(
                representatives,
                bandLeases,
                bestMaxHp,
                CycleExitCandidateRank.InFlight);
            if (inFlight != null)
                bandLeases.Add(inFlight);

            SearchNode? newest = FindActiveCycleExitCandidate(
                representatives,
                bandLeases,
                bestMaxHp,
                CycleExitCandidateRank.Newest);
            if (newest != null)
                bandLeases.Add(newest);

            AddActiveCycleExitFallbacks(
                representatives,
                bandLeases,
                bestMaxHp);

            foreach (SearchNode candidate in bandLeases)
            {
                leased.Add(candidate);
                candidate.CycleExitRetentionRank = _profile.BeamWidth + 4 + rank++;
                if (!selectedSet.Add(candidate))
                    continue;
                candidate.RetentionRank = int.MaxValue;
                candidate.LongTermResourceRetentionRank = int.MaxValue;
                candidate.CycleRetentionRank = int.MaxValue;
                selected.Add(candidate);
            }
        }

        HashSet<SearchNode> leasedSet = new(leased, ReferenceEqualityComparer.Instance);
        HashSet<CycleExitProbeTicketKey> survivingTickets = [];
        foreach (SearchNode retained in leased)
        {
            if (retained.CycleExitProbe == null)
                continue;
            survivingTickets.Add(BuildCycleExitProbeTicketKey(retained));
        }

        HashSet<CycleExitProbeTicketKey> settledTickets = [];
        foreach (SearchNode dropped in eligible)
        {
            if (leasedSet.Contains(dropped))
                continue;
            if (dropped.CycleExitProbe is { LeaseIssued: true } probe)
            {
                CycleExitProbeTicketKey ticket = BuildCycleExitProbeTicketKey(dropped);
                if (!survivingTickets.Contains(ticket) && settledTickets.Add(ticket))
                {
                    // Settle one whole ticket, not each sibling. Losing one branch while
                    // another survives must never mint duplicate generations.
                    probe.OriginTracker.RetryAbandonedExitProbe(
                        probe.OriginPhaseIndex,
                        probe.ExitActionKey,
                        probe.OriginGeneration);
                }
            }
            // Ordinary Beam/long-term retention does not bypass the fixed two-per-band
            // cycle-exit lease portfolio. It may retain the route, but not the probe lease.
            dropped.CycleExitProbe = null;
        }
    }

    private enum CycleExitCandidateRank : byte
    {
        InFlight,
        Newest,
        Fallback,
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

    private static void AddActiveCycleExitFallbacks(
        IReadOnlyList<SearchNode> representatives,
        List<SearchNode> bandLeases,
        int bestMaxHp)
    {
        while (bandLeases.Count < 2)
        {
            SearchNode? candidate = FindActiveCycleExitCandidate(
                representatives,
                bandLeases,
                bestMaxHp,
                CycleExitCandidateRank.Fallback);
            if (candidate == null)
                break;
            bandLeases.Add(candidate);
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
        int leftLeasePriority = left.CycleExitProbe is
            { LeaseIssued: true, RemainingActions: < MaximumCycleExitProbeActions }
                ? 0
                : 1;
        int rightLeasePriority = right.CycleExitProbe is
            { LeaseIssued: true, RemainingActions: < MaximumCycleExitProbeActions }
                ? 0
                : 1;
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
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
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
            CycleExitCandidateRank.Fallback => CompareCycleExitFallbackCandidates(
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
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
    }

    private static int CompareCycleExitNewestCandidates(
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
        comparison = (right.CycleExitProbe?.OriginNode.ActionCount ?? 0)
            .CompareTo(left.CycleExitProbe?.OriginNode.ActionCount ?? 0);
        if (comparison != 0)
            return comparison;
        comparison = (right.CycleExitProbe?.OriginGeneration ?? 0)
            .CompareTo(left.CycleExitProbe?.OriginGeneration ?? 0);
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
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
    }

    private static int CompareCycleExitFallbackCandidates(
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
        comparison = (left.CycleExitProbe?.RemainingActions ?? int.MaxValue)
            .CompareTo(right.CycleExitProbe?.RemainingActions ?? int.MaxValue);
        if (comparison != 0)
            return comparison;
        comparison = (right.CycleExitProbe?.OriginNode.ActionCount ?? 0)
            .CompareTo(left.CycleExitProbe?.OriginNode.ActionCount ?? 0);
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
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
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
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
        return comparison != 0 ? comparison : right.Score.CompareTo(left.Score);
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

    private static CycleProbeFamilyKey BuildCycleProbeFamilyKey(SearchNode node)
    {
        if (node.CycleProbeLease is { } lease)
        {
            return new CycleProbeFamilyKey(
                node.Turn,
                lease.Tracker.ShapeKey,
                lease.Tracker.SequenceKey,
                lease.Tracker.PeriodActions,
                lease.Tracker);
        }
        CycleSearchState cycle = node.Cycle
            ?? throw new InvalidOperationException("循环探测候选缺少族证据。");
        return new CycleProbeFamilyKey(
            node.Turn,
            cycle.ShapeKey,
            cycle.SequenceKey,
            cycle.PeriodActions,
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
                $"{string.Join('>', node.Actions.Select(PolicyActionToken))}:" +
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

}
