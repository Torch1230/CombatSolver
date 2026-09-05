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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CompareBeamRankOrder(
        double leftBeamRankScore,
        int leftOffensiveProgressValue,
        int leftActionCount,
        double rightBeamRankScore,
        int rightOffensiveProgressValue,
        int rightActionCount)
    {
        int comparison = rightBeamRankScore.CompareTo(leftBeamRankScore);
        if (comparison != 0)
            return comparison;
        comparison = leftActionCount.CompareTo(rightActionCount);
        return comparison != 0
            ? comparison
            : rightOffensiveProgressValue.CompareTo(leftOffensiveProgressValue);
    }

    internal readonly record struct OrdinaryBeamTacticalValues(
        int Turn,
        int PotionCount,
        int PotionStrategicCost,
        int FutureSoldHp,
        int CumulativePlayerHpLost,
        int ActionCount,
        double Score,
        int ZeroCostPlayableCount,
        int ReachableHandValue,
        int HandCount,
        bool HasRetainedRoutingChoice = false);

    internal static void DiversifyOrdinaryBeamBoundary<T>(
        IReadOnlyList<T> rankedPool,
        List<T> selected,
        IReadOnlyList<T> required,
        Func<T, (double Score, int Actions, int OffensiveProgress, int Potions, bool Victory)> describe,
        bool finalQualityFirst,
        Func<T, OrdinaryBeamTacticalValues>? describeTactical = null)
        where T : class
    {
        if (finalQualityFirst || selected.Count >= rankedPool.Count)
            return;

        HashSet<T> requiredSet = new(required, ReferenceEqualityComparer.Instance);
        Dictionary<T, int> selectedPositions = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < selected.Count; index++)
            selectedPositions.Add(selected[index], index);

        // Required replacement leaves selected unsorted. Locate the last ordinary survivor
        // in the original ranking, not the last selected slot or the configured beam width.
        int boundary = rankedPool.Count - 1;
        while (boundary >= 0
            && (requiredSet.Contains(rankedPool[boundary])
                || !selectedPositions.ContainsKey(rankedPool[boundary])))
        {
            boundary--;
        }
        if (boundary < 0)
            return;
        var boundaryValue = describe(rankedPool[boundary]);
        bool SamePrimary(T candidate)
        {
            var value = describe(candidate);
            return value.Score.Equals(boundaryValue.Score)
                && value.Actions == boundaryValue.Actions;
        }

        int end = boundary + 1;
        while (end < rankedPool.Count && SamePrimary(rankedPool[end]))
            end++;
        if (end == boundary + 1)
            return;
        int start = boundary;
        while (start > 0 && SamePrimary(rankedPool[start - 1]))
            start--;

        Dictionary<int, List<T>> byPotionCount = [];
        for (int index = start; index < end; index++)
        {
            T candidate = rankedPool[index];
            var value = describe(candidate);
            // Completed outcomes remain exclusively under the existing final policy.
            if (value.Victory)
                return;
            if (requiredSet.Contains(candidate))
                continue;
            if (!byPotionCount.TryGetValue(value.Potions, out List<T>? candidates))
            {
                candidates = [];
                byPotionCount.Add(value.Potions, candidates);
            }
            candidates.Add(candidate);
        }

        foreach (List<T> candidates in byPotionCount.Values)
        {
            List<int> slots = [];
            foreach (T candidate in candidates)
            {
                if (selectedPositions.TryGetValue(candidate, out int slot))
                    slots.Add(slot);
            }
            if (slots.Count <= 1 || slots.Count == candidates.Count)
                continue;

            List<List<T>> progressGroups = candidates
                .GroupBy(candidate => describe(candidate).OffensiveProgress)
                .OrderByDescending(group => group.Key)
                .Select(group => group.ToList())
                .ToList();
            if (progressGroups.Count <= 1)
                continue;

            if (describeTactical != null)
            {
                foreach (List<T> group in progressGroups)
                    OrderOrdinaryBeamTacticalCohorts(group, describeTactical);
            }

            // The supplemental progress key still supplies the first representative, but
            // one value cannot monopolize a partially retained primary tie. Tactical order
            // changes only route-less, equal-policy cohort slots within a progress group;
            // preserve routing positions, progress rotation and each potion count's seats.
            List<T> replacements = new(slots.Count);
            for (int round = 0; replacements.Count < slots.Count; round++)
            {
                foreach (List<T> group in progressGroups)
                {
                    if (round < group.Count)
                        replacements.Add(group[round]);
                    if (replacements.Count == slots.Count)
                        break;
                }
            }
            for (int index = 0; index < slots.Count; index++)
                selected[slots[index]] = replacements[index];
        }
    }

    private static void OrderOrdinaryBeamTacticalCohorts<T>(
        List<T> group,
        Func<T, OrdinaryBeamTacticalValues> describeTactical)
        where T : class
    {
        Dictionary<(int Turn, TranspositionLabel Policy),
            List<(int Position, T Candidate, OrdinaryBeamTacticalValues Values)>> cohorts = [];
        for (int position = 0; position < group.Count; position++)
        {
            T candidate = group[position];
            OrdinaryBeamTacticalValues values = describeTactical(candidate);
            // Existing routing representatives already have their own diversity policy.
            // Leave their positions fixed without blocking other positions in this group.
            if (values.HasRetainedRoutingChoice)
                continue;
            var key = (values.Turn, new TranspositionLabel(
                values.PotionCount,
                values.PotionStrategicCost,
                values.FutureSoldHp,
                values.CumulativePlayerHpLost,
                values.ActionCount,
                values.Score));
            if (!cohorts.TryGetValue(key, out var cohort))
            {
                cohort = [];
                cohorts.Add(key, cohort);
            }
            cohort.Add((position, candidate, values));
        }

        foreach (var cohort in cohorts.Values)
        {
            if (cohort.Count <= 1)
                continue;
            // Stable LINQ ordering preserves raw rank for fully equal tactical values.
            // Rewrite the cohort's original positions rather than flattening cohorts:
            // interleaved, unequal policy labels must retain their existing seats.
            T[] ordered = cohort
                .OrderByDescending(item => item.Values.ZeroCostPlayableCount)
                .ThenByDescending(item => item.Values.ReachableHandValue)
                .ThenByDescending(item => item.Values.HandCount)
                .Select(item => item.Candidate)
                .ToArray();
            for (int index = 0; index < cohort.Count; index++)
                group[cohort[index].Position] = ordered[index];
        }
    }

    private readonly record struct RoutingChoiceSignature(
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile,
        string CardId,
        int Upgrade,
        string CardStateKey,
        int Occurrence,
        string ContextId,
        int StateContext,
        StateFingerprint EnemyCombatDistributionKey,
        StateFingerprint EnemyControlDistributionKey,
        StateFingerprint UnorderedPileKey);
    private readonly record struct RoutingChoiceFamilySignature(
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile);
    private readonly record struct RoutingChoiceOptionSignature(
        string CardId,
        int Upgrade,
        string CardStateKey);
    private readonly record struct AmbiguousChoiceDecisionSignature(
        int PotionCount,
        StateFingerprint ParentStateKey,
        int ParentActionCount,
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile,
        int ChoiceCount,
        string ContextId);
    private readonly record struct OrderedMutationOutcomeFamilySignature(
        int Turn,
        int PotionCount,
        int ChoiceCount,
        StateFingerprint EffectMultisetKey,
        StateFingerprint UnorderedOutcomeKey,
        OrderedMutationBoundaryStamp? Boundary);
    private readonly record struct OrderedMutationBoundaryStamp(
        int FromTurn,
        int FromShufflesCrossed,
        int ToTurn,
        int ToShufflesCrossed);
    private readonly record struct OrderedMutationActivationCandidate(
        SearchNode Node,
        OrderedMutationRetentionLease Lease,
        StateFingerprint SequenceKey);
    private sealed record OrderedMutationActivationCohort(
        OrderedMutationOutcomeFamilySignature Family,
        IReadOnlyList<OrderedMutationActivationCandidate> Candidates,
        bool HasOrdinaryAnchor);
    private readonly record struct OrderedMutationContinuationLineageSignature(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint ParentStateKey);
    private readonly record struct OrderedMutationContinuationSourceFamilySignature(
        StateFingerprint Key,
        bool HasPersistentMutation);
    private readonly record struct OrderedMutationContinuationOutcomeKey(
        StateFingerprint OptionKey,
        StateFingerprint ChildStateKey);
    private readonly record struct OrderedMutationHandoffSourceLedgerKey(
        StateFingerprint InitialLeaseKey,
        StateFingerprint RecurrenceSourceFamilyKey);
    private sealed record OrderedMutationContinuationPacket(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint SourceFamilyKey,
        StateFingerprint OptionUniverseKey,
        bool HasPersistentMutationFamily,
        bool HasSelectedSibling,
        bool HasRotatedInteriorOption,
        int PortfolioPriority,
        SearchNode Parent,
        IReadOnlyList<SearchNode> Candidates);
    private readonly record struct OrderedMutationContinuationPacketOutcome(
        OrderedMutationContinuationPacket Packet,
        SearchNode Candidate);
    private sealed record OrderedMutationLateInitialPacingResult(
        List<OrderedMutationContinuationPacket> ContinuationPackets,
        List<OrderedMutationContinuationPacket> CounterfactualPackets,
        HashSet<SearchNode> PacedOutcomes,
        HashSet<SearchNode> ExplorerOutcomes);
    private readonly record struct OrderedMutationContinuationLaneKey(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint SourceFamilyKey,
        StateFingerprint OptionUniverseKey);
    private readonly record struct OrderedMutationContinuationFamilyKey(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint SourceFamilyKey,
        StateFingerprint OptionUniverseKey);
    private readonly record struct OrderedMutationContinuationBudgetKey(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint ParentStateKey,
        StateFingerprint SourceFamilyKey);
    private readonly record struct OrderedMutationParentObligationKey(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint ParentStateKey);
    private readonly record struct OrderedMutationParentObligationCandidate(
        OrderedMutationParentObligationKey Obligation,
        SearchNode Node,
        bool IsAlreadySelected);
    private sealed record OrderedMutationHandoffCohort(
        OrderedMutationParentObligationKey Obligation,
        OrderedMutationContinuationPacket AnchorPacket,
        IReadOnlyList<OrderedMutationContinuationPacketOutcome> CompanionOutcomes,
        bool AnchorAlreadySelected);
    private enum OrderedMutationAdmissionClaimReason
    {
        Handoff = 0,
        Observation = 1,
        Counterfactual = 2,
        Alternative = 3,
        Ordinary = 4,
    }
    private readonly record struct OrderedMutationAdmissionClaimKey(
        StateFingerprint RootKey,
        StateFingerprint InitialLeaseKey,
        StateFingerprint LeaseKey,
        StateFingerprint ParentLineageKey,
        StateFingerprint ParentStateKey,
        StateFingerprint SourceFamilyKey,
        OrderedMutationContinuationOutcomeKey Outcome);
    private readonly record struct OrderedMutationAdmissionClaimSource(
        OrderedMutationAdmissionClaimKey Key,
        OrderedMutationContinuationPacket Packet,
        SearchNode Candidate,
        OrderedMutationAdmissionClaimReason Reason,
        bool CrossedProofBoundary,
        bool ContinuationHandoff,
        bool RequestsObservation);
    private sealed record OrderedMutationAdmissionClaim(
        OrderedMutationAdmissionClaimKey Key,
        OrderedMutationContinuationPacket Packet,
        SearchNode Candidate,
        IReadOnlySet<OrderedMutationAdmissionClaimReason> Reasons,
        bool HandoffCrossedProofBoundary,
        bool ObservationCrossedProofBoundary,
        bool CounterfactualContinuationHandoff,
        bool CounterfactualRequestsObservation,
        bool OrdinaryCrossedProofBoundary,
        bool OrdinaryContinuationHandoff,
        bool OrdinaryRequestsObservation)
    {
        public OrderedMutationAdmissionClaimReason PrimaryReason => Reasons.Min();
    }
    private sealed record OrderedMutationAdmissionWorkItem(
        OrderedMutationParentObligationKey Parent,
        int PortfolioPriority,
        OrderedMutationAdmissionClaimReason Reason,
        OrderedMutationContinuationPacket Packet,
        OrderedMutationHandoffCohort? Cohort,
        OrderedMutationAdmissionClaim? Claim,
        IReadOnlyList<OrderedMutationAdmissionClaim> AliasedClaims);

    private static OrderedMutationParentObligationKey
        BuildOrderedMutationParentObligationKey(SearchNode parent)
    {
        OrderedMutationRetentionLease lease = parent.OrderedMutationRetentionLease
            ?? throw new InvalidOperationException(
                "有序变异 parent obligation 缺少 lease。");
        return new OrderedMutationParentObligationKey(
            lease.RootKey,
            lease.InitialKey,
            lease.Key,
            parent.OrderedMutationLineage?.SequenceKey ?? default,
            parent.StateKey);
    }

    private readonly record struct DirectRoutingChoice(
        SearchNode Node,
        SearchNode ChoiceNode,
        SearchNode Parent,
        RoutingChoiceSignature Signature);
    private readonly record struct RootActionLineageSignature(
        PlanActionKind Kind,
        string CardId,
        string PotionId,
        uint? TargetCombatId,
        string FirstCardId,
        uint? FirstCardTargetCombatId);

    private sealed class BeamRetentionPolicy(
        SolverSearchProfile _profile,
        bool _isActEndingBoss,
        int _initialEnemyCount,
        int _initialPlayerHp,
        int _initialPlayerMaxHp,
        bool _preserveReplayAllocatorOpening,
        SolverTheftPolicy? _theftPolicy,
        SolverPotionPolicy _potionPolicy,
        PotionStrategySnapshot _potionStrategy,
        bool _enforcePotionDirectives,
        bool _renewablePotionShapedRock,
        SearchRunContext _run,
        Func<SearchNode, StandPatEvaluation> _evaluateStandPat)
    {
        private const int PersistentRoutingContextRounds = 8;
        private const int RoutingChoiceLimit = 96;
        private const int AmbiguousCompressedChoiceLimit = 48;
        private sealed record OrderedPileCohort(IReadOnlyList<SearchNode> PrefixVariants);
        private readonly record struct PocketwatchCadenceSignature(
            int PotionCount,
            uint? FocusTargetCombatId,
            int RetainedAttackGrowth,
            StateFingerprint EnemyControlDistributionKey,
            bool TriggeredLastTurn,
            bool CanTriggerThisTurn);
        private readonly record struct PocketwatchCadenceFamilySignature(
            int PotionCount,
            uint? FocusTargetCombatId,
            int RetainedAttackGrowth,
            bool TriggeredLastTurn,
            bool CanTriggerThisTurn);
        private readonly record struct FinalPolicyQualificationFacts(
            bool ForcedUsesSatisfied,
            int ExplicitPotionUseCount,
            SolverPotionPolicy EffectivePotionPolicy,
            int OptionalPotionUseCount,
            int OptionalPotionStrategicCost,
            int OptionalAmbergrisCount);
        private readonly record struct FinalPolicyQualificationSignature(
            bool ForcedUsesSatisfied,
            int ExplicitPotionUseCount,
            SolverPotionPolicy EffectivePotionPolicy,
            int OptionalPotionUseCount,
            int OptionalPotionStrategicCost,
            int OptionalAmbergrisCount,
            bool TheftEscapeEligible,
            int OptionalAmbergrisFinalPlayerHpCohort);

        public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)
        {
            List<SearchNode> candidates = nodes.ToList();
            List<SearchNode> ranked = RankBest(
                candidates,
                _profile.BeamWidth * 4,
                finalQualityFirst: true);

            // FinalPlanOrdering has policy eligibility dimensions that are not monotone in
            // ordinary final quality (forced directives, Ambergris HP and theft recovery).
            // Preserve one representative per compact eligibility cohort, not per ordered
            // potion history: order and exact automatic-use count do not affect the policy.
            FinalPolicyQualificationFacts[] facts = new FinalPolicyQualificationFacts[candidates.Count];
            SearchNode? potionFreeBaseline = null;
            for (int index = 0; index < candidates.Count; index++)
            {
                SearchNode candidate = candidates[index];
                facts[index] = BuildFinalPolicyQualificationFacts(candidate);
                if (facts[index].ExplicitPotionUseCount == 0
                    && (potionFreeBaseline == null
                        || ComparePotionFreePolicyBaselines(
                            candidate,
                            potionFreeBaseline,
                            _initialPlayerHp,
                            _initialPlayerMaxHp,
                            _theftPolicy) < 0))
                {
                    potionFreeBaseline = candidate;
                }
            }
            int potionFreeOutstandingResource = potionFreeBaseline?.Snapshot.OutstandingStolenResource
                ?? int.MaxValue;

            Dictionary<FinalPolicyQualificationSignature, SearchNode> qualificationLeaders = [];
            Dictionary<SearchNode, FinalPolicyQualificationSignature> signatures =
                new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < candidates.Count; index++)
            {
                SearchNode candidate = candidates[index];
                FinalPolicyQualificationSignature signature = BuildFinalPolicyQualificationSignature(
                    facts[index],
                    candidate,
                    potionFreeOutstandingResource);
                signatures.Add(candidate, signature);
                if (!qualificationLeaders.TryGetValue(signature, out SearchNode? current)
                    || CompareFinalCandidates(candidate, current) < 0)
                {
                    qualificationLeaders[signature] = candidate;
                }
            }
            foreach (SearchNode leader in qualificationLeaders.Values)
            {
                if (!ContainsReference(ranked, leader))
                    ranked.Add(leader);
            }
            if (potionFreeBaseline != null && !ContainsReference(ranked, potionFreeBaseline))
                ranked.Add(potionFreeBaseline);
            ranked.Sort((left, right) =>
            {
                int comparison = CompareFinalCandidates(left, right);
                return comparison != 0
                    ? comparison
                    : CompareFinalPolicyQualificationSignatures(
                        signatures[left],
                        signatures[right]);
            });
            AssignRetentionRanks(ranked, []);
            return ranked;
        }

        private FinalPolicyQualificationFacts BuildFinalPolicyQualificationFacts(SearchNode node)
        {
            int explicitPotionStrategicCost = 0;
            int explicitAmbergrisCount = 0;
            for (SearchNode? cursor = node; cursor?.Action is { } action; cursor = cursor.Parent)
            {
                if (action.Kind != PlanActionKind.UsePotion)
                    continue;
                if (string.IsNullOrEmpty(action.PotionId))
                    throw new InvalidOperationException("用药动作缺少药水 ID。");
                explicitPotionStrategicCost += PotionUsePolicy.StrategicHpCost(
                    action.PotionId,
                    _renewablePotionShapedRock);
                if (string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal))
                    explicitAmbergrisCount++;
            }

            int forcedUseCount = 0;
            int forcedStrategicHpCost = 0;
            int forcedAmbergrisCount = 0;
            bool forcedUsesSatisfied = true;
            if (_enforcePotionDirectives)
            {
                foreach (PotionSlotDirective directive in _potionStrategy.Directives)
                {
                    if (directive.Directive != SolverPotionDirective.Force)
                        continue;
                    bool used = false;
                    for (SearchNode? cursor = node; cursor?.Action is { } action; cursor = cursor.Parent)
                    {
                        if (action.Kind != PlanActionKind.UsePotion
                            || action.PotionSlot != directive.Slot
                            || !string.Equals(
                                action.PotionId,
                                directive.PotionId,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        used = true;
                        break;
                    }
                    if (!used)
                    {
                        forcedUsesSatisfied = false;
                        continue;
                    }
                    forcedUseCount++;
                    forcedStrategicHpCost += PotionUsePolicy.StrategicHpCost(
                        directive.PotionId,
                        _renewablePotionShapedRock);
                    if (string.Equals(directive.PotionId, "AMBERGRIS", StringComparison.Ordinal))
                        forcedAmbergrisCount++;
                }
            }

            int explicitPotionUseCount = ExplicitPotionUseCount(node);
            int optionalPotionUseCount = Math.Max(0, explicitPotionUseCount - forcedUseCount);
            int optionalPotionStrategicCost = Math.Max(
                0,
                explicitPotionStrategicCost - forcedStrategicHpCost);
            int optionalAmbergrisCount = Math.Max(0, explicitAmbergrisCount - forcedAmbergrisCount);
            SolverPotionPolicy effectivePotionPolicy = _potionPolicy switch
            {
                SolverPotionPolicy.RequireAtLeastOne when forcedUseCount > 0
                    => SolverPotionPolicy.Smart,
                SolverPotionPolicy.Disabled when optionalPotionUseCount > 0
                    => SolverPotionPolicy.Smart,
                _ => _potionPolicy,
            };
            return new FinalPolicyQualificationFacts(
                forcedUsesSatisfied,
                explicitPotionUseCount,
                effectivePotionPolicy,
                optionalPotionUseCount,
                optionalPotionStrategicCost,
                optionalAmbergrisCount);
        }

        private FinalPolicyQualificationSignature BuildFinalPolicyQualificationSignature(
            FinalPolicyQualificationFacts facts,
            SearchNode candidate,
            int potionFreeOutstandingResource)
        {
            if (!facts.ForcedUsesSatisfied)
            {
                // Every partial forced-use history is rejected by the same hard rule.
                return new FinalPolicyQualificationSignature(
                    false,
                    0,
                    default,
                    0,
                    0,
                    0,
                    false,
                    int.MinValue);
            }

            bool theftEscapeEligible = FinalPolicyTheftEscapeEligible(
                _theftPolicy,
                candidate.PotionCount,
                candidate.Snapshot.OutstandingStolenResource,
                potionFreeOutstandingResource);
            return new FinalPolicyQualificationSignature(
                true,
                facts.ExplicitPotionUseCount,
                facts.EffectivePotionPolicy,
                facts.OptionalPotionUseCount,
                facts.OptionalPotionStrategicCost,
                facts.OptionalAmbergrisCount,
                theftEscapeEligible,
                FinalPolicyOptionalAmbergrisPlayerHpCohort(
                    facts.OptionalAmbergrisCount,
                    candidate.Snapshot.PlayerHp));
        }

        internal static int FinalPolicyOptionalAmbergrisPlayerHpCohort(
            int optionalAmbergrisCount,
            int playerHp)
            => optionalAmbergrisCount > 0 ? playerHp : int.MinValue;

        internal static bool FinalPolicyTheftEscapeEligible(
            SolverTheftPolicy? theftPolicy,
            int potionCount,
            int outstandingStolenResource,
            int potionFreeOutstandingResource)
            => theftPolicy == SolverTheftPolicy.PreserveResources
                && potionCount > 0
                && outstandingStolenResource < potionFreeOutstandingResource;

        private static int CompareFinalPolicyQualificationSignatures(
            FinalPolicyQualificationSignature left,
            FinalPolicyQualificationSignature right)
        {
            int comparison = right.ForcedUsesSatisfied.CompareTo(left.ForcedUsesSatisfied);
            if (comparison != 0)
                return comparison;
            comparison = left.ExplicitPotionUseCount.CompareTo(right.ExplicitPotionUseCount);
            if (comparison != 0)
                return comparison;
            comparison = left.EffectivePotionPolicy.CompareTo(right.EffectivePotionPolicy);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalPotionUseCount.CompareTo(right.OptionalPotionUseCount);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalPotionStrategicCost.CompareTo(right.OptionalPotionStrategicCost);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalAmbergrisCount.CompareTo(right.OptionalAmbergrisCount);
            if (comparison != 0)
                return comparison;
            comparison = right.TheftEscapeEligible.CompareTo(left.TheftEscapeEligible);
            return comparison != 0
                ? comparison
                : left.OptionalAmbergrisFinalPlayerHpCohort.CompareTo(
                    right.OptionalAmbergrisFinalPlayerHpCohort);
        }

        public List<SearchNode> RankLongTermResource(
            IReadOnlyList<SearchNode> nodes,
            int limit)
        {
            if (nodes.Count == 0)
                return [];
            int highestValue = nodes.Max(node => node.Snapshot.LongTermResourceValue);
            if (nodes.All(node => node.Snapshot.LongTermResourceValue == highestValue))
                return [];
            return RankBest(
                nodes.Where(node => node.Snapshot.LongTermResourceValue == highestValue),
                limit,
                preserveDefensiveRoute: true);
        }

        /// <summary>
        /// Preserves only proven non-commutative mutation collisions. This is deliberately a
        /// single coordinator pass: secondary RankBest calls cannot mint or extend leases.
        /// Existing leases are continued by semantic next-action family, not by a fixed number
        /// of actions, and every admission is charged to one small hard portfolio.
        /// </summary>
        public void AddOrderedMutationPortfolio(
            IReadOnlyList<SearchNode> pool,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet)
        {
            int admissionLimit = OrderedMutationLayerAdmissionLimit(
                _profile.BeamWidth);
            int admissions = 0;
            int pendingAdmissionSequence = 0;
            Dictionary<StateFingerprint, int> reservedAdmissionsByRootLease = [];
            Dictionary<StateFingerprint, int> reservedAdmissionsByInitialLease = [];
            Dictionary<StateFingerprint, int> reservedAdmissionsByLease = [];
            int reservedRunAdmissions = 0;
            Dictionary<OrderedMutationContinuationBudgetKey, int>
                continuationAdmissionsByLineage = [];
            Dictionary<OrderedMutationContinuationBudgetKey, int>
                counterfactualAdmissionsByLineage = [];

            Dictionary<OrderedMutationOutcomeFamilySignature,
                Dictionary<StateFingerprint, List<SearchNode>>> collisionCandidates = [];
            foreach (SearchNode node in pool)
            {
                OrderedMutationLineage? lineage = OrderedMutationCollisionLineage(node);
                if (node.OrderedMutationRetentionLease != null || lineage == null)
                    continue;
                // A completed source segment deliberately has the old turn number while its
                // visible unordered outcome belongs to the post-boundary child.
                if (node.OrderedMutationBoundaryLineage == null && lineage.Turn != node.Turn)
                    continue;
                OrderedMutationOutcomeFamilySignature family = new(
                    lineage.Turn,
                    node.PotionCount,
                    lineage.ChoiceCount,
                    lineage.EffectMultisetKey,
                    BuildOrderedPileTacticalKey(node),
                    OrderedMutationBoundaryStampFor(node));
                if (!collisionCandidates.TryGetValue(
                        family,
                        out Dictionary<StateFingerprint, List<SearchNode>>? bySequence))
                {
                    bySequence = [];
                    collisionCandidates.Add(family, bySequence);
                }
                if (!bySequence.TryGetValue(
                        lineage.SequenceKey,
                        out List<SearchNode>? sequenceCandidates))
                {
                    sequenceCandidates = [];
                    bySequence.Add(lineage.SequenceKey, sequenceCandidates);
                }
                sequenceCandidates.Add(node);
            }

            List<OrderedMutationActivationCohort> coldActivationCohorts = [];
            foreach ((OrderedMutationOutcomeFamilySignature family,
                         Dictionary<StateFingerprint, List<SearchNode>> bySequence) in
                     collisionCandidates)
            {
                if (bySequence.Count < 2)
                    continue;

                List<OrderedMutationActivationCandidate> representatives = [];
                foreach ((StateFingerprint sequenceKey, List<SearchNode> candidates) in bySequence)
                {
                    SearchNode representative = FindBestOrderedMutationRepresentative(
                        candidates,
                        selectedSet);
                    OrderedMutationRetentionLease lease = CreateOrderedMutationLease(
                        representative,
                        family,
                        sequenceKey);
                    if (!CanMintOrderedMutationLease(_run, lease))
                        continue;
                    representatives.Add(new OrderedMutationActivationCandidate(
                        representative,
                        lease,
                        sequenceKey));
                }
                if (representatives.Count < 2)
                    continue;

                representatives.Sort(CompareOrderedMutationActivationCandidates);
                // A collision which already has one sequence in an ordinary lane is still a cold
                // activation. Do not mint a one-sided lease on that node: select it as the pair
                // anchor and reserve/commit both distinct sequences through one atomic ticket.
                int preferredAnchorIndex = representatives.FindIndex(candidate =>
                    selectedSet.Contains(candidate.Node));
                StateFingerprint[] orderedSequenceKeys = representatives
                    .Select(candidate => candidate.SequenceKey)
                    .ToArray();
                if (!TrySelectAtomicOrderedMutationPair(
                        orderedSequenceKeys,
                        admissionLimit,
                        preferredAnchorIndex,
                        out int firstIndex,
                        out int secondIndex))
                {
                    continue;
                }
                OrderedMutationActivationCandidate[] pair =
                [
                    representatives[firstIndex],
                    representatives[secondIndex],
                ];
                if (!CanReserveOrderedMutationAdmissions(
                        _run,
                        pair.Select(candidate => candidate.Lease)))
                {
                    continue;
                }
                coldActivationCohorts.Add(new OrderedMutationActivationCohort(
                    family,
                    pair,
                    HasOrdinaryAnchor: pair.Any(candidate =>
                        selectedSet.Contains(candidate.Node))));
            }

            // Beam ownership is not payment for ordered protection. Natural winners enter the
            // same bounded service queue as extra outcomes; do not consume the whole layer or
            // expire their inherited leases before that shared queue has run. Cold seeds are
            // captured separately and still require their atomic pair transaction.
            SearchNode[] naturallySelectedLeaseNodes = selected
                .Where(node => node.OrderedMutationRetentionLease != null
                    && !HasPaidOrderedMutationAdmission(node))
                .ToArray();

            coldActivationCohorts.Sort(CompareOrderedMutationActivationCohorts);
            OrderedMutationActivationCohort? initiallyAdmittedColdCohort = null;
            for (int cohortIndex = 0;
                 cohortIndex < coldActivationCohorts.Count;
                 cohortIndex++)
            {
                OrderedMutationActivationCohort cohort = coldActivationCohorts[cohortIndex];
                if (!HasOrderedMutationLayerCapacity(
                        admissions,
                        admissionLimit,
                        requested: 2)
                    || !TryReserveOrderedMutationAdmissions(
                        _run,
                        reservedAdmissionsByRootLease,
                        reservedAdmissionsByInitialLease,
                        reservedAdmissionsByLease,
                        ref reservedRunAdmissions,
                        cohort.Candidates.Select(candidate => candidate.Lease))
                    || !TryAdmitOrderedMutationActivationCohort(
                        cohort,
                        cohortIndex,
                        selected,
                        selectedSet))
                {
                    continue;
                }
                admissions += 2;
                initiallyAdmittedColdCohort = cohort;
                break;
            }

            IComparer<OrderedMutationContinuationPacket> packetComparer =
                Comparer<OrderedMutationContinuationPacket>.Create(
                    CompareOrderedMutationContinuationPackets);
            List<OrderedMutationParentObligationCandidate> handoffCandidates = [];
            foreach (SearchNode node in pool)
            {
                bool alreadySelected = selectedSet.Contains(node);
                if (node.OrderedMutationRetentionLease == null
                    || node.Parent is not
                        { OrderedMutationContinuationHandoff: true,
                          OrderedMutationRetentionLease: not null }
                    || node.Action is not { } action
                    || !TryBuildOrderedMutationContinuationSourceFamilyKey(
                        action,
                        out _)
                    || !alreadySelected && !CanRetainOrderedMutationLease(_run, node))
                {
                    continue;
                }
                handoffCandidates.Add(new OrderedMutationParentObligationCandidate(
                    BuildOrderedMutationParentObligationKey(node.Parent),
                    node,
                    alreadySelected));
            }
            List<OrderedMutationParentObligationCandidate> handoffFulfillments =
                SelectOneOrderedMutationFulfillmentPerObligation(
                    handoffCandidates,
                    candidate => candidate.Obligation,
                    candidate => candidate.IsAlreadySelected,
                    (left, right) => CompareOrderedMutationRepresentatives(
                        left.Node,
                        right.Node));
            List<IGrouping<OrderedMutationContinuationLineageSignature, SearchNode>>
                rawContinuationGroups = pool
                .Where(node => CanRetainOrderedMutationLease(_run, node)
                    && node.Action != null)
                .GroupBy(BuildOrderedMutationContinuationLineageSignature)
                .ToList();
            List<OrderedMutationContinuationPacket> rawContinuationPackets = [];
            foreach (IGrouping<OrderedMutationContinuationLineageSignature, SearchNode> group in
                     rawContinuationGroups)
            {
                foreach (OrderedMutationContinuationPacket packet in
                         BuildOrderedMutationContinuationPackets(group, selectedSet))
                {
                    rawContinuationPackets.Add(packet);
                }
            }
            List<OrderedMutationHandoffCohort> boundaryHandoffCohorts =
                BuildOrderedMutationHandoffCohorts(
                    handoffFulfillments,
                    rawContinuationPackets,
                    packetComparer);
            List<OrderedMutationContinuationPacket> continuationPackets =
                rawContinuationPackets
                .GroupBy(packet => new OrderedMutationContinuationLaneKey(
                    packet.RootKey,
                    packet.InitialLeaseKey,
                    packet.LeaseKey,
                    packet.ParentLineageKey,
                    packet.SourceFamilyKey,
                    packet.OptionUniverseKey))
                .Select(group => SelectOrderedMutationContinuationPacketForLease(
                    group,
                    packetComparer))
                .GroupBy(packet => new OrderedMutationContinuationFamilyKey(
                    packet.RootKey,
                    packet.InitialLeaseKey,
                    packet.LeaseKey,
                    packet.SourceFamilyKey,
                    packet.OptionUniverseKey))
                .Select(group => SelectOrderedMutationContinuationPacketForLease(
                    group,
                    packetComparer))
                .ToList();
            // Cross-parent compaction is useful for ordinary quality lanes, but it must not
            // erase an exact parent that owes one post-mutation observation. Plan one
            // fulfillment from the complete candidate view. An ordinary winner needs no extra
            // Beam entry, but still pays for ordered service; without one, the strongest eligible
            // non-mutation fallback enters the same shared admission queue below.
            List<OrderedMutationParentObligationCandidate> observationCandidates = [];
            foreach (SearchNode node in pool)
            {
                bool alreadySelected = selectedSet.Contains(node);
                if (node.OrderedMutationRetentionLease == null
                    || node.Parent is not
                        { OrderedMutationContinuationBridge: true,
                          OrderedMutationRetentionLease: not null } parent
                    || node.Action is not { } action
                    || TryBuildOrderedMutationContinuationSourceFamilyKey(action, out _)
                    || !alreadySelected && !CanRetainOrderedMutationLease(_run, node))
                {
                    continue;
                }
                observationCandidates.Add(new OrderedMutationParentObligationCandidate(
                    BuildOrderedMutationParentObligationKey(parent),
                    node,
                    alreadySelected));
            }
            List<OrderedMutationParentObligationCandidate> observationFulfillments =
                SelectOneOrderedMutationFulfillmentPerObligation(
                    observationCandidates,
                    candidate => candidate.Obligation,
                    candidate => candidate.IsAlreadySelected,
                    (left, right) => CompareOrderedMutationRepresentatives(
                        left.Node,
                        right.Node));
            List<OrderedMutationContinuationPacket> observationBridgePackets =
                OrderOrderedMutationContinuationPacketsFairly(
                    observationFulfillments
                        .Select(candidate =>
                            BuildOrderedMutationObservationPacket(candidate.Node)),
                    packetComparer);
            continuationPackets = OrderOrderedMutationContinuationPacketsFairly(
                continuationPackets,
                packetComparer);
            IComparer<OrderedMutationContinuationPacket> counterfactualComparer =
                Comparer<OrderedMutationContinuationPacket>.Create((left, right) =>
                {
                    int comparison = left.PortfolioPriority.CompareTo(right.PortfolioPriority);
                    if (comparison != 0)
                        return comparison;
                    comparison = left.Parent.RetentionRank.CompareTo(right.Parent.RetentionRank);
                    return comparison != 0
                        ? comparison
                        : packetComparer.Compare(left, right);
                });
            List<OrderedMutationContinuationPacket> rankedCounterfactualPackets =
                rawContinuationPackets
                    .Where(packet => packet.HasPersistentMutationFamily
                        && packet.HasSelectedSibling
                        && packet.Parent.Snapshot.ShufflesCrossed
                            > MaximumOrderedMutationContinuationsPerLineagePerPrune)
                    .OrderBy(packet => packet, counterfactualComparer)
                    .ToList();
            OrderedMutationLateInitialPacingResult lateInitialPacing =
                PaceLateOrderedMutationInitials(
                    continuationPackets,
                    rankedCounterfactualPackets,
                    selectedSet,
                    reservedAdmissionsByRootLease,
                    reservedAdmissionsByInitialLease,
                    reservedAdmissionsByLease,
                    reservedRunAdmissions,
                    packetComparer,
                    counterfactualComparer);
            continuationPackets = lateInitialPacing.ContinuationPackets;
            rankedCounterfactualPackets = lateInitialPacing.CounterfactualPackets;
            List<OrderedMutationContinuationPacket> mutationAlternativePackets =
                OrderOrderedMutationContinuationPacketsFairly(
                    rawContinuationPackets.Where(packet =>
                        packet.HasPersistentMutationFamily
                        && (packet.HasSelectedSibling
                            || packet.HasRotatedInteriorOption)),
                    packetComparer);
            List<OrderedMutationContinuationPacket> counterfactualPackets =
                rankedCounterfactualPackets.ToList();
            int handoffAdmissions = 0;
            int observationAdmissions = 0;
            int counterfactualAdmissions = 0;
            int alternativeAdmissions = 0;
            Dictionary<OrderedMutationHandoffSourceLedgerKey, int>
                successfulSourceAdmissionsThisPrune = [];

            // An already-paid handoff anchor settles its obligation at zero service cost. Its
            // bounded companion is scheduled below beside all one-node claims; admitting every
            // boundary cohort up front would let a busy boundary consume the shared 48-node
            // portfolio before another exact parent's first ordinary continuation is seen.
            foreach (OrderedMutationHandoffCohort cohort in boundaryHandoffCohorts)
            {
                if (!HasPaidOrderedMutationAdmission(cohort.AnchorPacket.Candidates[0]))
                    continue;
                SearchNode anchor = cohort.AnchorPacket.Candidates[0];
                ApplyOrderedMutationHandoffOutcome(
                    anchor,
                    anchor.OrderedMutationRetentionLease is { BoundaryReached: true });
            }

            // Every reason below spends the same 48-node layer portfolio. Scheduling whole
            // reason queues serially lets a busy handoff/observation root consume the layer
            // before another root's first ordinary continuation is considered. Normalize all
            // reasons to one semantic outcome claim, coalesce aliases, then round-robin the
            // shared root -> initial -> current hierarchy. An obligation which overlaps an
            // ordinary outcome therefore costs one node, never one node per reason.
            List<OrderedMutationAdmissionClaimSource> claimSources = [];
            foreach (SearchNode candidate in naturallySelectedLeaseNodes)
            {
                claimSources.Add(BuildOrderedMutationAdmissionClaimSource(
                    BuildNaturalOrderedMutationAdmissionPacket(candidate),
                    candidate,
                    OrderedMutationAdmissionClaimReason.Ordinary,
                    crossedProofBoundary: candidate.OrderedMutationRetentionLease is
                        { BoundaryReached: true },
                    continuationHandoff: false,
                    requestsObservation: false));
            }
            foreach (OrderedMutationContinuationPacket packet in observationBridgePackets)
            {
                SearchNode candidate = packet.Candidates[0];
                claimSources.Add(BuildOrderedMutationAdmissionClaimSource(
                    packet,
                    candidate,
                    OrderedMutationAdmissionClaimReason.Observation,
                    crossedProofBoundary: candidate.OrderedMutationRetentionLease is
                        { BoundaryReached: true },
                    continuationHandoff: false,
                    requestsObservation: false));
            }
            foreach (OrderedMutationContinuationPacket packet in counterfactualPackets)
            {
                for (int index = 0;
                     index < Math.Min(
                         packet.Candidates.Count,
                         MaximumOrderedMutationContinuationsPerLineagePerPrune);
                     index++)
                {
                    SearchNode candidate = packet.Candidates[index];
                    bool continuationHandoff =
                        lateInitialPacing.PacedOutcomes.Contains(candidate)
                            ? lateInitialPacing.ExplorerOutcomes.Contains(candidate)
                            : index == packet.Candidates.Count - 1;
                    claimSources.Add(BuildOrderedMutationAdmissionClaimSource(
                        packet,
                        candidate,
                        OrderedMutationAdmissionClaimReason.Counterfactual,
                        crossedProofBoundary: false,
                        continuationHandoff,
                        requestsObservation: packet.HasPersistentMutationFamily));
                }
            }
            foreach (OrderedMutationContinuationPacket packet in mutationAlternativePackets)
            {
                SearchNode candidate = packet.Candidates[
                    Math.Min(1, packet.Candidates.Count - 1)];
                claimSources.Add(BuildOrderedMutationAdmissionClaimSource(
                    packet,
                    candidate,
                    OrderedMutationAdmissionClaimReason.Alternative,
                    crossedProofBoundary: false,
                    continuationHandoff: false,
                    requestsObservation: true));
            }
            foreach (OrderedMutationContinuationPacket packet in continuationPackets)
            {
                // BuildOrderedMutationContinuationPacket has already reduced an arbitrary
                // option set to the fixed two-outcome lineage budget. Both bounded outcomes
                // must enter the shared scheduler: admitting only the quality leader silently
                // discarded the semantic explorer unless an unrelated alternative condition
                // happened to alias it.
                foreach (SearchNode candidate in packet.Candidates)
                {
                    claimSources.Add(BuildOrderedMutationAdmissionClaimSource(
                        packet,
                        candidate,
                        OrderedMutationAdmissionClaimReason.Ordinary,
                        crossedProofBoundary: candidate.OrderedMutationRetentionLease is
                            { BoundaryReached: true },
                        continuationHandoff: lateInitialPacing.PacedOutcomes.Contains(candidate)
                            && lateInitialPacing.ExplorerOutcomes.Contains(candidate),
                        requestsObservation: packet.HasPersistentMutationFamily));
                }
            }

            List<OrderedMutationAdmissionClaim> admissionClaims =
                CoalesceOrderedMutationAdmissionClaims(
                    claimSources,
                    selectedSet,
                    packetComparer);
            HashSet<OrderedMutationAdmissionClaim> appliedAdmissionClaims = [];
            foreach (OrderedMutationAdmissionClaim selectedClaim in admissionClaims
                         .Where(claim => HasPaidOrderedMutationAdmission(claim.Candidate)))
            {
                ApplyZeroWidthOrderedMutationObligations(selectedClaim);
                appliedAdmissionClaims.Add(selectedClaim);
            }
            List<OrderedMutationAdmissionWorkItem> admissionWork = [];
            bool HasDistinctCompanionOutcome(OrderedMutationHandoffCohort cohort)
            {
                OrderedMutationContinuationOutcomeKey anchorOutcome =
                    BuildOrderedMutationContinuationOutcomeKey(
                        cohort.AnchorPacket.Candidates[0]);
                return cohort.CompanionOutcomes.Any(outcome =>
                    BuildOrderedMutationContinuationOutcomeKey(outcome.Candidate)
                        != anchorOutcome);
            }
            // The anchor is owned exclusively by its atomic handoff work. Companion outcomes
            // remain ordinary shared-scheduler claims until one concrete packet is admitted;
            // pending/charged admission then folds duplicate claims to zero service cost.
            // Treating every possible fallback as cohort-owned would discard the unchosen families'
            // independent counterfactual/ordinary/alternative eligibility.
            HashSet<SearchNode> handoffCohortAnchors = new(
                boundaryHandoffCohorts
                    .SelectMany(cohort => cohort.AnchorPacket.Candidates),
                ReferenceEqualityComparer.Instance);
            Dictionary<SearchNode, List<OrderedMutationAdmissionClaim>>
                admissionClaimsByCandidate = admissionClaims
                    .GroupBy(
                        claim => claim.Candidate,
                        (IEqualityComparer<SearchNode>)ReferenceEqualityComparer.Instance)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToList(),
                        (IEqualityComparer<SearchNode>)ReferenceEqualityComparer.Instance);
            foreach (OrderedMutationHandoffCohort cohort in boundaryHandoffCohorts)
            {
                if (HasPaidOrderedMutationAdmission(cohort.AnchorPacket.Candidates[0])
                    && !HasDistinctCompanionOutcome(cohort))
                    continue;
                List<OrderedMutationAdmissionClaim> aliasedClaims = [];
                foreach (SearchNode member in cohort.AnchorPacket.Candidates.Concat(
                             cohort.CompanionOutcomes.Select(outcome => outcome.Candidate)))
                {
                    if (admissionClaimsByCandidate.TryGetValue(
                            member,
                            out List<OrderedMutationAdmissionClaim>? memberClaims))
                    {
                        aliasedClaims.AddRange(memberClaims);
                    }
                }
                admissionWork.Add(new OrderedMutationAdmissionWorkItem(
                    cohort.Obligation,
                    cohort.AnchorPacket.PortfolioPriority,
                    OrderedMutationAdmissionClaimReason.Handoff,
                    cohort.AnchorPacket,
                    cohort,
                    null,
                    aliasedClaims));
            }
            foreach (OrderedMutationAdmissionClaim claim in
                     SelectOrderedMutationClaimsForSharedScheduling(
                         admissionClaims,
                         claim => claim.Candidate,
                         new HashSet<SearchNode>(
                             selected.Where(HasPaidOrderedMutationAdmission),
                             ReferenceEqualityComparer.Instance),
                         handoffCohortAnchors))
            {
                admissionWork.Add(new OrderedMutationAdmissionWorkItem(
                    new OrderedMutationParentObligationKey(
                        claim.Key.RootKey,
                        claim.Key.InitialLeaseKey,
                        claim.Key.LeaseKey,
                        claim.Key.ParentLineageKey,
                        claim.Key.ParentStateKey),
                    claim.Packet.PortfolioPriority,
                    claim.PrimaryReason,
                    claim.Packet,
                    null,
                    claim,
                    []));
            }

            bool TryProcessAdmissionWork(
                OrderedMutationAdmissionWorkItem work,
                int maximumAdmissionWidth,
                bool allowAnchorOnlyHandoffFallback,
                out int admittedWidth)
            {
                admittedWidth = 0;
                if (work.Cohort is { } cohort)
                {
                    SearchNode anchor = cohort.AnchorPacket.Candidates[0];
                    bool crossedProofBoundary = anchor.OrderedMutationRetentionLease is
                        { BoundaryReached: true };
                    int newAnchorCount = OrderedMutationAdmissionServiceCost(anchor);
                    // Each unpaid companion adds service cost. Reject before constructing the
                    // coverage order when even the anchor cannot fit this service or a hard
                    // admission bound. An already-paid anchor deliberately continues at zero cost:
                    // it may still settle an already-paid companion or the empty-packet
                    // anchor special case below.
                    if (!CanAttemptOrderedMutationHandoffAnchor(
                            admissions,
                            admissionLimit,
                            handoffAdmissions,
                            newAnchorCount,
                            maximumAdmissionWidth))
                    {
                        return false;
                    }

                    bool TryAttemptCompanionPacket(
                        OrderedMutationContinuationPacket? companionPacket,
                        out int successfulWidth)
                    {
                        successfulWidth = 0;
                        IReadOnlyList<SearchNode> companions =
                            companionPacket?.Candidates ?? [];
                        int newCompanionCount = 0;
                        foreach (SearchNode companion in companions)
                        {
                            newCompanionCount += OrderedMutationAdmissionServiceCost(companion);
                        }
                        int cohortWidth = newAnchorCount + newCompanionCount;
                        if (!CanAttemptOrderedMutationAdmissionWithinService(
                                cohortWidth,
                                maximumAdmissionWidth)
                            || !HasOrderedMutationLayerCapacity(
                                admissions,
                                admissionLimit,
                                cohortWidth)
                            || !CanAdmitOrderedMutationAlternatives(
                                alternativeAdmissions,
                                newCompanionCount))
                        {
                            return false;
                        }

                        List<OrderedMutationContinuationPacket> packets =
                            companionPacket is not null
                                ? [cohort.AnchorPacket, companionPacket]
                                : [cohort.AnchorPacket];
                        if (!TryAdmitOrderedMutationContinuationCohort(
                                packets,
                                selected,
                                selectedSet,
                                reservedAdmissionsByRootLease,
                                reservedAdmissionsByInitialLease,
                                reservedAdmissionsByLease,
                                continuationAdmissionsByLineage,
                                ref reservedRunAdmissions,
                                out List<SearchNode> newlyReserved))
                        {
                            return false;
                        }

                        foreach (SearchNode candidate in newlyReserved)
                        {
                            candidate.OrderedMutationAdmissionSequence =
                                pendingAdmissionSequence++;
                        }
                        // A cold cohort may reuse a companion which its independent claim
                        // admitted earlier in this prune. Stage source coverage for either kind
                        // of successful, still-pending admission exactly once. A companion
                        // settled in an earlier prune cannot publish another source admission.
                        foreach (SearchNode companion in companions.Where(
                                     candidate => candidate.OrderedMutationAdmissionPending
                                         && !candidate.OrderedMutationAdmissionCharged))
                        {
                            if (companion.OrderedMutationRetentionLease is not
                                    { } companionLease
                                || companion.Action is not { } companionAction
                                || !TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                                    companionAction,
                                    out StateFingerprint recurrenceSourceFamily))
                            {
                                continue;
                            }
                            var sourceLedgerKey = new OrderedMutationHandoffSourceLedgerKey(
                                companionLease.InitialKey,
                                recurrenceSourceFamily);
                            _ = TryStageOrderedMutationHandoffSourceAdmission(
                                _run.PendingOrderedMutationHandoffSourceByNode,
                                successfulSourceAdmissionsThisPrune,
                                companion,
                                sourceLedgerKey,
                                companion.OrderedMutationAdmissionPending,
                                companion.OrderedMutationAdmissionCharged);
                        }
                        ApplyOrderedMutationHandoffOutcome(
                            anchor,
                            crossedProofBoundary);
                        foreach (SearchNode companion in companions)
                            RequestOrderedMutationObservation(companion);
                        foreach (OrderedMutationAdmissionClaim aliasedClaim in
                                 work.AliasedClaims)
                        {
                            if (HasPaidOrderedMutationAdmission(aliasedClaim.Candidate)
                                && appliedAdmissionClaims.Add(aliasedClaim))
                            {
                                ApplyZeroWidthOrderedMutationObligations(aliasedClaim);
                            }
                        }
                        handoffAdmissions += newAnchorCount;
                        alternativeAdmissions += newCompanionCount;
                        admissions += cohortWidth;
                        successfulWidth = cohortWidth;
                        return true;
                    }

                    var anchorOutcome = new OrderedMutationContinuationPacketOutcome(
                        cohort.AnchorPacket,
                        anchor);
                    List<OrderedMutationContinuationPacketOutcome> orderedCompanions =
                        OrderOrderedMutationCoverageBalancedCompanions(
                            anchorOutcome,
                            cohort.CompanionOutcomes,
                            outcome => TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                                    outcome.Candidate.Action!,
                                    out StateFingerprint sourceFamily)
                                ? sourceFamily
                                : default,
                            sourceFamily =>
                            {
                                var ledgerKey = new OrderedMutationHandoffSourceLedgerKey(
                                    cohort.AnchorPacket.InitialLeaseKey,
                                    sourceFamily);
                                return _run
                                        .OrderedMutationHandoffAdmissionsByInitialAndSource
                                        .GetValueOrDefault(ledgerKey)
                                    + successfulSourceAdmissionsThisPrune
                                        .GetValueOrDefault(ledgerKey);
                            },
                            outcome => BuildOrderedMutationContinuationOutcomeKey(
                                outcome.Candidate),
                            (left, right) =>
                                OrderedMutationContinuationSemanticDistance(
                                    left.Candidate,
                                    right.Candidate),
                            (left, right) => CompareOrderedMutationPacingOutcomes(
                                left,
                                right,
                                packetComparer));
                    List<OrderedMutationContinuationPacket> seenPackets = [];
                    int companionPacketCount = 0;
                    foreach (OrderedMutationContinuationPacketOutcome companion in
                             orderedCompanions)
                    {
                        bool packetAlreadySeen = false;
                        foreach (OrderedMutationContinuationPacket seenPacket in seenPackets)
                        {
                            if (!ReferenceEquals(seenPacket, companion.Packet))
                                continue;
                            packetAlreadySeen = true;
                            break;
                        }
                        if (packetAlreadySeen)
                            continue;
                        seenPackets.Add(companion.Packet);
                        List<SearchNode> packetCandidates =
                            SelectDistinctOrderedMutationCompanionPacketCandidates(
                                anchor,
                                companion.Packet.Candidates,
                                BuildOrderedMutationContinuationOutcomeKey,
                                CompareOrderedMutationRepresentatives);
                        if (packetCandidates.Count == 0)
                            continue;
                        companionPacketCount++;
                        OrderedMutationContinuationPacket companionPacket =
                            companion.Packet with
                        {
                            Candidates = packetCandidates,
                        };
                        if (TryAttemptCompanionPacket(
                                companionPacket,
                                out int successfulWidth))
                        {
                            admittedWidth = successfulWidth;
                            return true;
                        }
                    }
                    if (ShouldAppendOrderedMutationAnchorOnlyAttempt(
                            companionPacketCount,
                            allowAnchorOnlyHandoffFallback))
                    {
                        bool admittedAnchor = TryAttemptCompanionPacket(
                            companionPacket: null,
                            out int successfulWidth);
                        admittedWidth = successfulWidth;
                        return admittedAnchor;
                    }
                    return false;
                }

                OrderedMutationAdmissionClaim claim = work.Claim
                    ?? throw new InvalidOperationException(
                        "ordered-mutation admission work 缺少 claim 与 cohort。");
                SearchNode claimCandidate = claim.Candidate;
                if (HasPaidOrderedMutationAdmission(claimCandidate))
                {
                    if (selectedSet.Add(claimCandidate))
                        selected.Add(claimCandidate);
                    if (appliedAdmissionClaims.Add(claim))
                        ApplyZeroWidthOrderedMutationObligations(claim);
                    return true;
                }
                if (!HasOrderedMutationLayerCapacity(admissions, admissionLimit, 1))
                {
                    return false;
                }
                if (!CanAttemptOrderedMutationAdmissionWithinService(
                        requestedWidth: 1,
                        maximumAdmissionWidth: maximumAdmissionWidth))
                    return false;
                bool TryAdmitForReason(OrderedMutationAdmissionClaimReason reason)
                {
                    Dictionary<OrderedMutationContinuationBudgetKey, int> lineageAdmissions =
                        reason == OrderedMutationAdmissionClaimReason.Counterfactual
                            ? counterfactualAdmissionsByLineage
                            : continuationAdmissionsByLineage;
                    return TryAdmitOrderedMutationContinuationPacket(
                        claim.Packet,
                        selected,
                        selectedSet,
                        reservedAdmissionsByRootLease,
                        reservedAdmissionsByInitialLease,
                        reservedAdmissionsByLease,
                        lineageAdmissions,
                        ref reservedRunAdmissions);
                }
                if (!TrySelectOrderedMutationAdmissionReason(
                        claim.Reasons,
                        handoffAdmissions,
                        observationAdmissions,
                        counterfactualAdmissions,
                        alternativeAdmissions,
                        TryAdmitForReason,
                        out OrderedMutationAdmissionClaimReason admissionReason))
                {
                    return false;
                }
                admissions++;
                claimCandidate.OrderedMutationAdmissionSequence = pendingAdmissionSequence++;
                IncrementOrderedMutationAdmissionReason(
                    admissionReason,
                    ref handoffAdmissions,
                    ref observationAdmissions,
                    ref counterfactualAdmissions,
                    ref alternativeAdmissions);
                if (appliedAdmissionClaims.Add(claim))
                    ApplyOrderedMutationAdmissionClaim(claim);
                admittedWidth = 1;
                return true;
            }

            List<OrderedMutationAdmissionWorkItem> orderedAdmissionWork =
                OrderOrderedMutationAdmissionWorkFairly(
                    admissionWork,
                    packetComparer);
            List<OrderedMutationAdmissionWorkItem> deferredAdmissionWork = [];
            (int GenericClaims, int PaidCohorts) serviceLimits =
                OrderedMutationServiceLimits(admissionLimit);

            static bool IsWarmHandoffCohort(
                OrderedMutationAdmissionWorkItem work)
                => work.Cohort is { AnchorAlreadySelected: true };

            static bool IsPromisedClaim(
                OrderedMutationAdmissionWorkItem work)
                => work.Claim is { } claim
                    && claim.Reasons.Any(reason => reason is
                        OrderedMutationAdmissionClaimReason.Handoff
                        or OrderedMutationAdmissionClaimReason.Observation
                        or OrderedMutationAdmissionClaimReason.Counterfactual);

            void ProcessProtectedService(
                IEnumerable<OrderedMutationAdmissionWorkItem> workItems,
                ref int serviceAdmissions,
                int serviceLimit)
            {
                foreach (OrderedMutationAdmissionWorkItem work in workItems)
                {
                    int availableServiceAdmissions = serviceLimit - serviceAdmissions;
                    if (TryProcessAdmissionWork(
                            work,
                            availableServiceAdmissions,
                            allowAnchorOnlyHandoffFallback: false,
                            out int admittedWidth))
                    {
                        serviceAdmissions += admittedWidth;
                    }
                    else
                    {
                        deferredAdmissionWork.Add(work);
                    }
                }
            }

            // Complete an already independently retained boundary before paying three nodes to
            // open a cold boundary. This maximizes semantic coverage per added node and prevents
            // cold cohorts from exhausting Alternative while a live exact parent is still owed
            // its bounded companion packet.
            int paidCohortServiceAdmissions = Math.Min(
                admissions,
                serviceLimits.PaidCohorts);
            ProcessProtectedService(
                orderedAdmissionWork.Where(IsWarmHandoffCohort),
                ref paidCohortServiceAdmissions,
                serviceLimits.PaidCohorts);

            // Observation, counterfactual, and standalone handoff claims are bounded work already
            // promised by an earlier retention decision. Schedule those obligations together in
            // hierarchy-fair order before unpromised ordinary/alternative exploration. This
            // avoids letting whichever debt kind happens to be most numerous monopolize the
            // generic share; aliases still cost one node.
            int genericClaimServiceAdmissions = 0;
            ProcessProtectedService(
                orderedAdmissionWork.Where(IsPromisedClaim),
                ref genericClaimServiceAdmissions,
                serviceLimits.GenericClaims);
            ProcessProtectedService(
                orderedAdmissionWork.Where(work =>
                    work.Claim != null
                    && !IsPromisedClaim(work)),
                ref genericClaimServiceAdmissions,
                serviceLimits.GenericClaims);

            // The initial cold activation pair is already included in paid service. Spend only
            // what remains of that share on other cold handoffs after warm obligations.
            ProcessProtectedService(
                orderedAdmissionWork.Where(work =>
                    work.Cohort != null && !IsWarmHandoffCohort(work)),
                ref paidCohortServiceAdmissions,
                serviceLimits.PaidCohorts);

            // The protected shares are starvation floors, not additional hard caps. Borrow any
            // unused global service capacity in obligation order: warm handoff, promised claims,
            // ordinary, then cold expansion. Each category remains root/initial/lease/exact-parent fair.
            foreach (OrderedMutationAdmissionWorkItem work in deferredAdmissionWork)
            {
                _ = TryProcessAdmissionWork(
                    work,
                    admissionLimit - admissions,
                    allowAnchorOnlyHandoffFallback: true,
                    out _);
            }

            // A layer without leased continuations may expose several independent collision
            // families at once. Spend only otherwise-unused lane capacity, two seeds at a
            // time, so hash/enumeration order cannot make the single first family monopolize
            // activation or leave a one-sided orphan.
            for (int cohortIndex = 0;
                 cohortIndex < coldActivationCohorts.Count;
                 cohortIndex++)
            {
                OrderedMutationActivationCohort cohort = coldActivationCohorts[cohortIndex];
                if (ReferenceEquals(cohort, initiallyAdmittedColdCohort))
                {
                    continue;
                }
                bool hasLayerCapacity = admissions <= admissionLimit - 2;
                bool reserved = hasLayerCapacity
                    && TryReserveOrderedMutationAdmissions(
                        _run,
                        reservedAdmissionsByRootLease,
                        reservedAdmissionsByInitialLease,
                        reservedAdmissionsByLease,
                        ref reservedRunAdmissions,
                        cohort.Candidates.Select(candidate => candidate.Lease));
                if (!reserved
                    || !TryAdmitOrderedMutationActivationCohort(
                        cohort,
                        cohortIndex,
                        selected,
                        selectedSet))
                {
                    _run.OrderedMutationColdAtomicRejected = checked(
                        _run.OrderedMutationColdAtomicRejected + 1);
                    if (!reserved)
                    {
                        _run.OrderedMutationLeaseExpiredBudget = checked(
                            _run.OrderedMutationLeaseExpiredBudget + 2);
                    }
                    if (cohort.HasOrdinaryAnchor)
                    {
                        _run.OrderedMutationOrdinaryFallbacks = checked(
                            _run.OrderedMutationOrdinaryFallbacks + 1);
                    }
                    continue;
                }
                admissions += 2;
            }
            // Any inherited lane left outside this prune's paid/pending portfolio has exhausted
            // (or lost) scheduling eligibility. Clear only scheduling state before CycleRegion
            // runs, so the underlying route remains available through ordinary bounded lanes.
            foreach (SearchNode candidate in pool)
            {
                if (candidate.OrderedMutationRetentionLease != null
                    && !candidate.OrderedMutationAdmissionCharged
                    && !candidate.OrderedMutationAdmissionPending)
                {
                    if (selectedSet.Contains(candidate))
                    {
                        _run.OrderedMutationLeaseExpiredBudget = checked(
                            _run.OrderedMutationLeaseExpiredBudget + 1);
                        _run.OrderedMutationOrdinaryFallbacks = checked(
                            _run.OrderedMutationOrdinaryFallbacks + 1);
                    }
                    ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(candidate);
                }
            }
            if (admissions > admissionLimit)
            {
                throw new InvalidOperationException(
                    "有序变异单层 admission 超出共享硬上限。");
            }
        }

        /// <summary>
        /// Records a bounded observation obligation only after all retention coordinators have
        /// settled the actual frontier. A provisional selection can be removed by cycle-region
        /// arbitration, so arming this during portfolio construction would silently lose the
        /// outcome it was meant to protect. The remaining-step counter lets the chosen branch
        /// expose a short delayed payoff without widening any layer.
        /// </summary>
        private static void RequestOrderedMutationObservation(SearchNode candidate)
        {
            candidate.OrderedMutationObservationRequested = true;
            candidate.OrderedMutationObservationStepsRemaining = Math.Max(
                candidate.OrderedMutationObservationStepsRemaining,
                MaximumOrderedMutationObservationSteps);
        }

        public void ArmOrderedMutationObservationBridges(
            IReadOnlyList<SearchNode> pool,
            IReadOnlyList<SearchNode> retained)
        {
            HashSet<SearchNode> retainedSet = new(
                retained,
                ReferenceEqualityComparer.Instance);
            // Every retained inherited lease was either derived and paid by the unified ordered
            // coordinator or expired before CycleRegion. Never derive an uncharged transition in
            // this post-settlement phase: doing so would create a fresh 16-slot leaf for free.
            foreach (SearchNode candidate in retained)
            {
                if (!candidate.OrderedMutationLeaseTransitionPending)
                    continue;
                ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(candidate);
            }

            // A handoff is one obligation owned by the exact semantic parent, not one
            // entitlement per persistent source family. Prefer the outcome already staged by
            // the handoff planner, then settle exactly one actual survivor at zero extra width.
            List<OrderedMutationParentObligationCandidate> retainedHandoffCandidates = [];
            foreach (SearchNode candidate in retained)
            {
                if (candidate.Parent is not
                        { OrderedMutationContinuationHandoff: true,
                          OrderedMutationRetentionLease: not null } parent
                    || candidate.Action is not { } action
                    || !TryBuildOrderedMutationContinuationSourceFamilyKey(action, out _))
                {
                    continue;
                }
                retainedHandoffCandidates.Add(new OrderedMutationParentObligationCandidate(
                    BuildOrderedMutationParentObligationKey(parent),
                    candidate,
                    candidate.OrderedMutationObservationRequested));
            }
            List<OrderedMutationParentObligationCandidate> retainedHandoffFulfillments =
                SelectOneOrderedMutationFulfillmentPerObligation(
                    retainedHandoffCandidates,
                    candidate => candidate.Obligation,
                    candidate => candidate.IsAlreadySelected,
                    (left, right) => CompareOrderedMutationRepresentatives(
                        left.Node,
                        right.Node));
            HashSet<OrderedMutationParentObligationKey> fulfilledHandoffs = [];
            foreach (OrderedMutationParentObligationCandidate fulfillment in
                     retainedHandoffFulfillments)
            {
                RequestOrderedMutationObservation(fulfillment.Node);
                fulfilledHandoffs.Add(fulfillment.Obligation);
            }
            foreach (OrderedMutationParentObligationCandidate candidate in retainedHandoffCandidates)
            {
                if (fulfilledHandoffs.Contains(candidate.Obligation))
                    candidate.Node.Parent!.OrderedMutationContinuationHandoff = false;
            }
            List<(SearchNode Candidate, StateFingerprint Root,
                StateFingerprint Initial, StateFingerprint Current,
                StateFingerprint ParentLineage, StateFingerprint ParentState,
                StateFingerprint SourceFamily,
                OrderedMutationContinuationOutcomeKey Outcome)> candidates = [];
            foreach (SearchNode candidate in pool)
            {
                if (candidate.OrderedMutationRetentionLease is not { } childLease
                    || candidate.Parent is not { } parent
                    || candidate.Action == null
                    || !TryBuildOrderedMutationContinuationSourceFamilyKey(
                        candidate.Action,
                        out StateFingerprint sourceFamily))
                {
                    continue;
                }
                OrderedMutationRetentionLease groupingLease =
                    parent.OrderedMutationRetentionLease ?? childLease;
                candidates.Add((
                    candidate,
                    groupingLease.RootKey,
                    groupingLease.InitialKey,
                    groupingLease.Key,
                    parent.OrderedMutationLineage?.SequenceKey ?? default,
                    parent.StateKey,
                    sourceFamily,
                    BuildOrderedMutationContinuationOutcomeKey(candidate)));
            }

            foreach (IGrouping<(StateFingerprint Root, StateFingerprint Initial,
                         StateFingerprint Current, StateFingerprint ParentLineage,
                         StateFingerprint ParentState, StateFingerprint SourceFamily,
                         OrderedMutationContinuationOutcomeKey Outcome),
                         (SearchNode Candidate, StateFingerprint Root,
                         StateFingerprint Initial, StateFingerprint Current,
                         StateFingerprint ParentLineage, StateFingerprint ParentState,
                         StateFingerprint SourceFamily,
                         OrderedMutationContinuationOutcomeKey Outcome)> outcome in
                     candidates.GroupBy(item => (
                         item.Root,
                         item.Initial,
                         item.Current,
                         item.ParentLineage,
                         item.ParentState,
                         item.SourceFamily,
                         item.Outcome)))
            {
                if (!outcome.Any(item =>
                        item.Candidate.OrderedMutationObservationRequested))
                {
                    continue;
                }
                List<SearchNode> survivors = outcome
                    .Select(item => item.Candidate)
                    .Where(retainedSet.Contains)
                    .ToList();
                if (survivors.Count == 0)
                    continue;
                SearchNode survivor = survivors.Aggregate((best, candidate) =>
                    IsBetterOrderedMutationRepresentative(candidate, best)
                        ? candidate
                        : best);
                survivor.OrderedMutationContinuationBridge = true;
            }

            // If ordinary ranking already retained a different child of the observed parent,
            // carry the bounded window through that real survivor too. The admission queue
            // above selects one representative action family; without this transfer, a
            // naturally retained sibling would paradoxically lose the remaining observation
            // credit and its next delayed-payoff edge could be pruned.
            foreach (IGrouping<SearchNode, SearchNode> children in retained
                         .Where(candidate =>
                             candidate.Parent is
                                 { OrderedMutationContinuationBridge: true }
                             && candidate.OrderedMutationObservationStepsRemaining > 0)
                         .GroupBy(
                             candidate => candidate.Parent!,
                             (IEqualityComparer<SearchNode>)
                                 ReferenceEqualityComparer.Instance))
            {
                List<SearchNode> survivors = children.ToList();
                // Exactly one child carries the remaining window. Prefer a child which the
                // ordinary ranker already selected; a portfolio-only backup has MaxValue rank.
                // This transfers, rather than duplicates, the observation obligation.
                SearchNode carrier = survivors
                    .OrderBy(candidate => candidate.RetentionRank)
                    .ThenBy(candidate => candidate,
                        Comparer<SearchNode>.Create(
                            CompareOrderedMutationRepresentatives))
                    .First();
                foreach (SearchNode survivor in survivors)
                    survivor.OrderedMutationContinuationBridge = false;
                carrier.OrderedMutationContinuationBridge = true;
            }
        }

        private static int AvailableOrderedMutationLayerAdmissions(
            int admissions,
            int admissionLimit,
            int reasonAdmissions,
            int reasonAdmissionLimit)
            => Math.Max(
                0,
                Math.Min(
                    admissionLimit - admissions,
                    reasonAdmissionLimit - reasonAdmissions));

        private static int OrderedMutationLayerAdmissionLimit(int beamWidth)
            => Math.Max(
                0,
                Math.Min(beamWidth, MaximumOrderedMutationLayerAdmissions));

        private static List<T> SelectOneOrderedMutationFulfillmentPerObligation<T, TKey>(
            IEnumerable<T> candidates,
            Func<T, TKey> obligationSelector,
            Func<T, bool> isAlreadySelected,
            Comparison<T> comparison)
            where TKey : notnull
        {
            IComparer<T> comparer = Comparer<T>.Create(comparison);
            List<T> representatives = [];
            foreach (IGrouping<TKey, T> obligation in candidates.GroupBy(obligationSelector))
            {
                List<T> members = obligation.ToList();
                List<T> selected = members.Where(isAlreadySelected).ToList();
                representatives.Add((selected.Count > 0 ? selected : members)
                    .OrderBy(candidate => candidate, comparer)
                    .First());
            }
            representatives.Sort(comparison);
            return representatives;
        }

        private OrderedMutationContinuationPacket BuildNaturalOrderedMutationAdmissionPacket(
            SearchNode candidate)
        {
            OrderedMutationRetentionLease lease = candidate.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException("自然入选有序变异候选缺少 lease。");
            SearchNode parent = candidate.Parent
                ?? throw new InvalidOperationException("自然入选有序变异候选缺少 parent。");
            PlanAction action = candidate.Action
                ?? throw new InvalidOperationException("自然入选有序变异候选缺少 action。");
            bool hasPersistentMutation = TryBuildOrderedMutationContinuationSourceFamilyKey(
                action,
                out StateFingerprint sourceFamily);
            return new OrderedMutationContinuationPacket(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                parent.OrderedMutationLineage?.SequenceKey ?? default,
                sourceFamily,
                BuildOrderedMutationContinuationOptionUniverseKey([candidate]),
                hasPersistentMutation,
                true,
                false,
                lease.PortfolioPriority,
                parent,
                [candidate]);
        }

        private OrderedMutationContinuationPacket BuildOrderedMutationHandoffPacket(
            SearchNode candidate)
        {
            OrderedMutationRetentionLease lease = candidate.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException(
                    "有序变异 handoff candidate 缺少 lease。");
            SearchNode parent = candidate.Parent
                ?? throw new InvalidOperationException(
                    "有序变异 handoff candidate 缺少 parent。");
            PlanAction action = candidate.Action
                ?? throw new InvalidOperationException(
                    "有序变异 handoff candidate 缺少 action。");
            if (!TryBuildOrderedMutationContinuationSourceFamilyKey(
                    action,
                    out StateFingerprint sourceFamily))
            {
                throw new InvalidOperationException(
                    "有序变异 handoff candidate 不含持久变异。");
            }
            return new OrderedMutationContinuationPacket(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                parent.OrderedMutationLineage?.SequenceKey ?? default,
                sourceFamily,
                BuildOrderedMutationContinuationOptionUniverseKey([candidate]),
                true,
                false,
                false,
                lease.PortfolioPriority,
                parent,
                [candidate]);
        }

        private List<OrderedMutationHandoffCohort> BuildOrderedMutationHandoffCohorts(
            IReadOnlyList<OrderedMutationParentObligationCandidate> fulfillments,
            IReadOnlyList<OrderedMutationContinuationPacket> rawPackets,
            IComparer<OrderedMutationContinuationPacket> packetComparer)
        {
            Dictionary<OrderedMutationParentObligationKey,
                List<OrderedMutationContinuationPacketOutcome>> candidatesByObligation = [];
            foreach (OrderedMutationContinuationPacket packet in rawPackets)
            {
                if (!packet.HasPersistentMutationFamily
                    || packet.Parent is not
                        { OrderedMutationContinuationHandoff: true })
                {
                    continue;
                }
                OrderedMutationParentObligationKey obligation =
                    BuildOrderedMutationParentObligationKey(packet.Parent);
                if (!candidatesByObligation.TryGetValue(
                        obligation,
                        out List<OrderedMutationContinuationPacketOutcome>? candidates))
                {
                    candidates = [];
                    candidatesByObligation.Add(obligation, candidates);
                }
                foreach (SearchNode candidate in packet.Candidates)
                {
                    candidates.Add(new OrderedMutationContinuationPacketOutcome(
                        packet,
                        candidate));
                }
            }

            List<OrderedMutationHandoffCohort> cohorts = [];
            foreach (OrderedMutationParentObligationCandidate fulfillment in fulfillments)
            {
                OrderedMutationContinuationPacket anchorPacket =
                    BuildOrderedMutationHandoffPacket(fulfillment.Node);
                IReadOnlyList<OrderedMutationContinuationPacketOutcome> companionOutcomes =
                    candidatesByObligation.TryGetValue(
                        fulfillment.Obligation,
                        out List<OrderedMutationContinuationPacketOutcome>? candidates)
                        ? candidates
                        : [];
                cohorts.Add(new OrderedMutationHandoffCohort(
                    fulfillment.Obligation,
                    anchorPacket,
                    companionOutcomes,
                    fulfillment.IsAlreadySelected));
            }

            return OrderOrderedMutationHierarchy(
                cohorts,
                cohort => cohort.AnchorPacket.RootKey,
                cohort => cohort.AnchorPacket.InitialLeaseKey,
                cohort => cohort.AnchorPacket.LeaseKey,
                key => _run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByLease.GetValueOrDefault(key),
                cohort => cohort.AnchorPacket.PortfolioPriority,
                current => current
                    .OrderBy(cohort => cohort.AnchorPacket, packetComparer)
                    .ThenBy(cohort => cohort.Obligation.ParentLineageKey.First)
                    .ThenBy(cohort => cohort.Obligation.ParentLineageKey.Second)
                    .ThenBy(cohort => cohort.Obligation.ParentStateKey.First)
                    .ThenBy(cohort => cohort.Obligation.ParentStateKey.Second)
                    .ToList());
        }

        /// <summary>
        /// First covers the least-served persistent source family, then completes the anchor
        /// family once every available family has received a successful handoff. Counts live in
        /// the run coordinator rather than simulator/search-node state, so derived leases cannot
        /// restart coverage and rejected work cannot advance it.
        /// </summary>
        internal static bool TrySelectOrderedMutationCoverageBalancedCompanion<
            T,
            TFamily,
            TOutcome>(
            T anchor,
            IEnumerable<T> candidates,
            Func<T, TFamily> familySelector,
            Func<TFamily, int> admissionsByFamily,
            Func<T, TOutcome> outcomeSelector,
            Func<T, T, long> semanticDistance,
            Comparison<T> comparison,
            out T companion)
            where TFamily : notnull
            where TOutcome : notnull
        {
            List<T> ordered = OrderOrderedMutationCoverageBalancedCompanions(
                anchor,
                candidates,
                familySelector,
                admissionsByFamily,
                outcomeSelector,
                semanticDistance,
                comparison);
            if (ordered.Count == 0)
            {
                companion = default!;
                return false;
            }
            companion = ordered[0];
            return true;
        }

        internal static List<T> OrderOrderedMutationCoverageBalancedCompanions<
            T,
            TFamily,
            TOutcome>(
            T anchor,
            IEnumerable<T> candidates,
            Func<T, TFamily> familySelector,
            Func<TFamily, int> admissionsByFamily,
            Func<T, TOutcome> outcomeSelector,
            Func<T, T, long> semanticDistance,
            Comparison<T> comparison)
            where TFamily : notnull
            where TOutcome : notnull
        {
            TOutcome anchorOutcome = outcomeSelector(anchor);
            IComparer<T> comparer = Comparer<T>.Create(comparison);
            List<T> representatives = candidates
                .Where(candidate => !EqualityComparer<TOutcome>.Default.Equals(
                    outcomeSelector(candidate),
                    anchorOutcome))
                .GroupBy(outcomeSelector)
                .Select(group => group.OrderBy(candidate => candidate, comparer).First())
                .ToList();
            if (representatives.Count == 0)
                return [];

            List<(TFamily Family, int Admissions, List<T> Candidates)> families =
                representatives
                    .GroupBy(familySelector)
                    .Select(group => (
                        Family: group.Key,
                        Admissions: admissionsByFamily(group.Key),
                        Candidates: group
                            .OrderByDescending(candidate =>
                                semanticDistance(anchor, candidate))
                            .ThenBy(candidate => candidate, comparer)
                            .ToList()))
                    .ToList();
            int minimumAdmissions = families.Min(family => family.Admissions);
            TFamily anchorFamily = familySelector(anchor);
            bool completeAnchorFamily = minimumAdmissions > 0
                && admissionsByFamily(anchorFamily) == minimumAdmissions;
            families.Sort((left, right) =>
            {
                if (completeAnchorFamily)
                {
                    bool leftIsAnchor = EqualityComparer<TFamily>.Default.Equals(
                        left.Family,
                        anchorFamily);
                    bool rightIsAnchor = EqualityComparer<TFamily>.Default.Equals(
                        right.Family,
                        anchorFamily);
                    int anchorComparison = rightIsAnchor.CompareTo(leftIsAnchor);
                    if (anchorComparison != 0)
                        return anchorComparison;
                }
                int admissionComparison = left.Admissions.CompareTo(right.Admissions);
                if (admissionComparison != 0)
                    return admissionComparison;
                long leftDistance = semanticDistance(anchor, left.Candidates[0]);
                long rightDistance = semanticDistance(anchor, right.Candidates[0]);
                int distanceComparison = rightDistance.CompareTo(leftDistance);
                return distanceComparison != 0
                    ? distanceComparison
                    : comparison(left.Candidates[0], right.Candidates[0]);
            });

            // Round-robin outcomes across source families. If the fairest packet cannot fit a
            // hard lease/layer bound, the caller can try the next family rather than repeatedly
            // starving every admissible fallback behind one impossible packet.
            List<T> ordered = new(representatives.Count);
            for (int round = 0;
                 families.Any(family => round < family.Candidates.Count);
                 round++)
            {
                foreach (var family in families)
                {
                    if (round < family.Candidates.Count)
                        ordered.Add(family.Candidates[round]);
                }
            }
            return ordered;
        }

        internal static bool TrySelectOrderedMutationSemanticCompanion<T, TOutcome>(
            T anchor,
            IEnumerable<T> candidates,
            Func<T, TOutcome> outcomeSelector,
            Func<T, T, long> semanticDistance,
            Comparison<T> comparison,
            out T companion)
            where TOutcome : notnull
        {
            TOutcome anchorOutcome = outcomeSelector(anchor);
            IComparer<T> comparer = Comparer<T>.Create(comparison);
            List<T> distinctCandidates = candidates
                .Where(candidate => !EqualityComparer<TOutcome>.Default.Equals(
                    outcomeSelector(candidate),
                    anchorOutcome))
                .GroupBy(outcomeSelector)
                .Select(group => group.OrderBy(candidate => candidate, comparer).First())
                .OrderBy(candidate => candidate, comparer)
                .ToList();
            if (distinctCandidates.Count == 0)
            {
                companion = default!;
                return false;
            }

            companion = distinctCandidates[0];
            long bestDistance = semanticDistance(anchor, companion);
            for (int index = 1; index < distinctCandidates.Count; index++)
            {
                T candidate = distinctCandidates[index];
                long distance = semanticDistance(anchor, candidate);
                if (distance > bestDistance
                    || distance == bestDistance
                        && comparison(candidate, companion) < 0)
                {
                    companion = candidate;
                    bestDistance = distance;
                }
            }
            return true;
        }

        internal static List<T> SelectDistinctOrderedMutationCompanionPacketCandidates<
            T,
            TOutcome>(
            T anchor,
            IEnumerable<T> packetCandidates,
            Func<T, TOutcome> outcomeSelector,
            Comparison<T> comparison)
            where TOutcome : notnull
        {
            TOutcome anchorOutcome = outcomeSelector(anchor);
            IComparer<T> comparer = Comparer<T>.Create(comparison);
            return packetCandidates
                .Where(candidate => !EqualityComparer<TOutcome>.Default.Equals(
                    outcomeSelector(candidate),
                    anchorOutcome))
                .GroupBy(outcomeSelector)
                .Select(group => group.OrderBy(candidate => candidate, comparer).First())
                .OrderBy(candidate => candidate, comparer)
                .ToList();
        }

        internal static int OrderedMutationHandoffCohortAdmissionWidth(
            bool anchorAlreadySelected,
            int companionCount)
            => (anchorAlreadySelected ? 0 : 1) + companionCount;

        internal static bool CanAdmitOrderedMutationAlternatives(
            int admittedAlternatives,
            int requestedAlternatives)
            => requestedAlternatives >= 0
                && admittedAlternatives
                    <= MaximumOrderedMutationAlternativeAdmissions - requestedAlternatives;

        internal static bool CanAdmitOrderedMutationHandoffs(
            int admittedHandoffs,
            int requestedHandoffs)
            => requestedHandoffs >= 0
                && admittedHandoffs >= 0
                && admittedHandoffs
                    <= MaximumOrderedMutationBoundaryHandoffAdmissions
                        - requestedHandoffs;

        internal static bool HasOrderedMutationLayerCapacity(
            int admitted,
            int admissionLimit,
            int requested)
            => requested >= 0
                && admitted >= 0
                && admissionLimit >= 0
                && admitted <= admissionLimit - requested;

        internal static bool CanAttemptOrderedMutationAdmissionWithinService(
            int requestedWidth,
            int maximumAdmissionWidth)
            => requestedWidth >= 0
                && maximumAdmissionWidth >= 0
                && requestedWidth <= maximumAdmissionWidth;

        internal static bool CanAttemptOrderedMutationHandoffAnchor(
            int admitted,
            int admissionLimit,
            int admittedHandoffs,
            int requestedAnchorWidth,
            int maximumAdmissionWidth)
            => CanAttemptOrderedMutationAdmissionWithinService(
                    requestedAnchorWidth,
                    maximumAdmissionWidth)
                && HasOrderedMutationLayerCapacity(
                    admitted,
                    admissionLimit,
                    requestedAnchorWidth)
                && CanAdmitOrderedMutationHandoffs(
                    admittedHandoffs,
                    requestedAnchorWidth);

        internal static bool ShouldAppendOrderedMutationAnchorOnlyAttempt(
            int companionPacketCount,
            bool allowAnchorOnlyHandoffFallback)
            => companionPacketCount >= 0
                && (companionPacketCount == 0
                    || allowAnchorOnlyHandoffFallback);

        internal static IEnumerable<TClaim>
            SelectOrderedMutationClaimsForSharedScheduling<TClaim, TCandidate>(
                IEnumerable<TClaim> claims,
                Func<TClaim, TCandidate> candidateSelector,
                IReadOnlySet<TCandidate> paidCandidates,
                IReadOnlySet<TCandidate> handoffAnchors)
            where TCandidate : notnull
            => claims.Where(claim =>
            {
                TCandidate candidate = candidateSelector(claim);
                return !paidCandidates.Contains(candidate)
                    && !handoffAnchors.Contains(candidate);
            });

        internal static (int GenericClaims, int PaidCohorts)
            OrderedMutationServiceLimits(int admissionLimit)
        {
            int boundedLimit = Math.Clamp(
                admissionLimit,
                0,
                MaximumOrderedMutationLayerAdmissions);
            int genericClaims = Math.Min(
                MaximumOrderedMutationGenericClaimServiceAdmissions,
                boundedLimit * 2 / 3);
            return (genericClaims, boundedLimit - genericClaims);
        }

        private bool TryAdmitOrderedMutationContinuationCohort(
            IReadOnlyList<OrderedMutationContinuationPacket> packets,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet,
            IDictionary<StateFingerprint, int> reservedAdmissionsByRootLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByInitialLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByLease,
            Dictionary<OrderedMutationContinuationBudgetKey, int>
                continuationAdmissionsByLineage,
            ref int reservedRunAdmissions,
            out List<SearchNode> newlyReserved)
        {
            newlyReserved = [];
            if (packets.Count == 0
                || packets.Any(packet => packet.Candidates.Count == 0
                    || packet.Candidates.Count
                        > MaximumOrderedMutationContinuationsPerLineagePerPrune))
            {
                return false;
            }
            (OrderedMutationContinuationPacket Packet, SearchNode Candidate)[] members =
                packets
                    .SelectMany(packet => packet.Candidates.Select(candidate => (
                        Packet: packet,
                        Candidate: candidate)))
                    .ToArray();
            SearchNode[] candidates = members
                .Select(member => member.Candidate)
                .ToArray();
            if (candidates.Distinct(ReferenceEqualityComparer.Instance).Count()
                    != candidates.Length
                || candidates.Any(candidate => !selectedSet.Contains(candidate)
                    && !CanRetainOrderedMutationLease(_run, candidate)))
            {
                return false;
            }
            foreach ((OrderedMutationContinuationPacket packet, SearchNode candidate) in members)
            {
                if (candidate.OrderedMutationRetentionLease is not { } lease
                    || lease.RootKey != packet.RootKey
                    || lease.InitialKey != packet.InitialLeaseKey
                    || !HasPaidOrderedMutationAdmission(candidate)
                        && lease.Key != packet.LeaseKey)
                {
                    return false;
                }
            }

            (OrderedMutationContinuationPacket Packet, SearchNode Candidate)[] pendingMembers =
                members
                    .Where(member => !HasPaidOrderedMutationAdmission(member.Candidate))
                    .ToArray();
            if (pendingMembers.Length == 0)
            {
                foreach (SearchNode candidate in candidates)
                {
                    if (selectedSet.Add(candidate))
                        selected.Add(candidate);
                }
                return true;
            }
            OrderedMutationRetentionLease[] leases = pendingMembers
                .Select(member => BuildOrderedMutationContinuationAdmissionLease(
                    member.Candidate))
                .ToArray();
            OrderedMutationContinuationBudgetKey[] budgetKeys = pendingMembers
                .Select(member => new OrderedMutationContinuationBudgetKey(
                    member.Packet.RootKey,
                    member.Packet.InitialLeaseKey,
                    member.Packet.LeaseKey,
                    member.Candidate.Parent?.OrderedMutationLineage?.SequenceKey ?? default,
                    member.Candidate.Parent?.StateKey ?? default,
                    member.Packet.SourceFamilyKey))
                .ToArray();
            if (budgetKeys.GroupBy(key => key).Any(group =>
                    continuationAdmissionsByLineage.GetValueOrDefault(group.Key)
                        > MaximumOrderedMutationContinuationsPerLineagePerPrune
                            - group.Count())
                || leases.Any(lease => !HasRemainingOrderedMutationLeaseBudget(_run, lease))
                || !TryReserveOrderedMutationAdmissions(
                    _run,
                    reservedAdmissionsByRootLease,
                    reservedAdmissionsByInitialLease,
                    reservedAdmissionsByLease,
                    ref reservedRunAdmissions,
                    leases))
            {
                return false;
            }

            foreach (IGrouping<OrderedMutationContinuationBudgetKey,
                         OrderedMutationContinuationBudgetKey> group in budgetKeys.GroupBy(
                         key => key))
            {
                continuationAdmissionsByLineage[group.Key] = checked(
                    continuationAdmissionsByLineage.GetValueOrDefault(group.Key)
                        + group.Count());
            }
            for (int index = 0; index < pendingMembers.Length; index++)
            {
                SearchNode candidate = pendingMembers[index].Candidate;
                candidate.OrderedMutationRetentionLease = leases[index];
                candidate.OrderedMutationLeaseTransitionPending = false;
                candidate.OrderedMutationAdmissionPending = true;
                if (selectedSet.Add(candidate))
                    selected.Add(candidate);
                else
                    _run.PendingOrderedMutationOrdinaryFallbackNodes.Add(candidate);
                newlyReserved.Add(candidate);
            }
            foreach (SearchNode candidate in candidates)
            {
                if (selectedSet.Add(candidate))
                    selected.Add(candidate);
            }
            return true;
        }

        private static void ApplyOrderedMutationHandoffOutcome(
            SearchNode candidate,
            bool crossedProofBoundary)
        {
            candidate.OrderedMutationContinuationHandoff |= crossedProofBoundary;
            RequestOrderedMutationObservation(candidate);
        }

        private OrderedMutationContinuationPacket BuildOrderedMutationObservationPacket(
            SearchNode candidate)
        {
            OrderedMutationRetentionLease lease = candidate.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException(
                    "有序变异 observation candidate 缺少 lease。");
            SearchNode parent = candidate.Parent
                ?? throw new InvalidOperationException(
                    "有序变异 observation candidate 缺少 parent。");
            PlanAction action = candidate.Action
                ?? throw new InvalidOperationException(
                    "有序变异 observation candidate 缺少 action。");
            bool hasPersistentMutation =
                TryBuildOrderedMutationContinuationSourceFamilyKey(
                    action,
                    out StateFingerprint sourceFamily);
            if (hasPersistentMutation)
            {
                throw new InvalidOperationException(
                    "有序变异 observation debt 不能由新持久变异偿还。");
            }
            return new OrderedMutationContinuationPacket(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                parent.OrderedMutationLineage?.SequenceKey ?? default,
                sourceFamily,
                BuildOrderedMutationContinuationOptionUniverseKey([candidate]),
                false,
                false,
                false,
                lease.PortfolioPriority,
                parent,
                [candidate]);
        }

        private static OrderedMutationContinuationLineageSignature
            BuildOrderedMutationContinuationLineageSignature(SearchNode node)
        {
            OrderedMutationRetentionLease lease = node.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException(
                    "有序变异 continuation 分组时缺少 lease。");
            return new OrderedMutationContinuationLineageSignature(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                node.Parent?.OrderedMutationLineage?.SequenceKey ?? default,
                node.Parent?.StateKey ?? default);
        }

        public List<SearchNode> RankDeferredCandidates(IEnumerable<SearchNode> nodes, int limit)
        {
            List<SearchNode> ranked = nodes.ToList();
            ranked.Sort((left, right) => CompareBeamRankOrder(
                BeamRankScore(left), left.Snapshot.OffensiveProgressValue, left.ActionCount,
                BeamRankScore(right), right.Snapshot.OffensiveProgressValue, right.ActionCount));
            if (ranked.Count > limit)
                ranked.RemoveRange(limit, ranked.Count - limit);
            return ranked;
        }

        public List<SearchNode> RankBest(
            IEnumerable<SearchNode> nodes,
            int limit,
            bool preserveDefensiveRoute = false,
            bool finalQualityFirst = false,
            Action<GlobalRetentionDecision>? observe = null)
        {
            Dictionary<SearchNode, RoutingChoiceSignature>? observedRoutingSignatures =
                observe != null && preserveDefensiveRoute
                    ? new(ReferenceEqualityComparer.Instance)
                    : null;
            HashSet<SearchNode>? observedOptionLeaders = observe != null && preserveDefensiveRoute
                ? new(ReferenceEqualityComparer.Instance)
                : null;
            List<SearchNode> ranked;
            if (finalQualityFirst)
            {
                // Equal simulator states can still have different cumulative battle loss or
                // policy-relevant action histories. Do not erase those distinctions before the
                // final policy pass has inspected them.
                ranked = nodes.ToList();
            }
            else
            {
                Dictionary<StateFingerprint, SearchNode> bestByState = [];
                foreach (SearchNode node in nodes)
                {
                    if (!bestByState.TryGetValue(node.StateKey, out SearchNode? current)
                        || IsBetterSearchNode(node, current))
                    {
                        bestByState[node.StateKey] = node;
                    }
                }
                ranked = [.. bestByState.Values];
            }

            ranked.Sort(finalQualityFirst
                ? CompareFinalCandidates
                : (left, right) => CompareBeamRankOrder(
                    BeamRankScore(left),
                    left.Snapshot.OffensiveProgressValue,
                    left.ActionCount,
                    BeamRankScore(right),
                    right.Snapshot.OffensiveProgressValue,
                    right.ActionCount));
            List<SearchNode> routingChoices = [];
            if (preserveDefensiveRoute)
            {
                if (_profile.Phase == SolverSearchPhase.Deep)
                {
                    foreach (SearchNode candidate in
                             BuildAmbiguousCompressedChoicePortfolio(ranked, limit))
                    {
                        AddRoutingCandidate(routingChoices, candidate, RoutingChoiceLimit);
                    }
                }
                Dictionary<RoutingChoiceSignature, SearchNode> bestScoreByRoutingChoice = [];
                Dictionary<RoutingChoiceSignature, SearchNode> bestOffenseByRoutingChoice = [];
                Dictionary<RoutingChoiceSignature, SearchNode> bestDefenseByRoutingChoice = [];
                Dictionary<RoutingChoiceSignature, SearchNode> bestSetupByRoutingChoice = [];
                Dictionary<RoutingChoiceSignature, SearchNode> bestPileOrderByRoutingChoice = [];
                Dictionary<RoutingChoiceSignature, List<SearchNode>> nodesByRoutingChoice = [];
                foreach (SearchNode node in ranked)
                {
                    RoutingChoiceSignature? signature = RetainedRoutingChoice(node);
                    if (signature == null)
                        continue;
                    if (observedRoutingSignatures != null)
                        observedRoutingSignatures[node] = signature.Value;
                    if (!nodesByRoutingChoice.TryGetValue(signature.Value, out List<SearchNode>? routingNodes))
                    {
                        routingNodes = [];
                        nodesByRoutingChoice.Add(signature.Value, routingNodes);
                    }
                    routingNodes.Add(node);
                    if (!bestScoreByRoutingChoice.TryGetValue(signature.Value, out SearchNode? current)
                        || IsBetterSearchNode(node, current))
                    {
                        bestScoreByRoutingChoice[signature.Value] = node;
                    }
                    bestOffenseByRoutingChoice.TryGetValue(signature.Value, out SearchNode? currentOffense);
                    if (IsBetterOffensive(node, currentOffense))
                        bestOffenseByRoutingChoice[signature.Value] = node;
                    bestDefenseByRoutingChoice.TryGetValue(signature.Value, out SearchNode? currentDefense);
                    if (IsBetterDefensive(node, currentDefense))
                        bestDefenseByRoutingChoice[signature.Value] = node;
                    bestSetupByRoutingChoice.TryGetValue(signature.Value, out SearchNode? currentSetup);
                    if (IsBetterSetup(node, currentSetup))
                        bestSetupByRoutingChoice[signature.Value] = node;
                    if (!bestPileOrderByRoutingChoice.TryGetValue(signature.Value, out SearchNode? currentPileOrder)
                        || node.Snapshot.ProjectedShuffleOrderValue
                            > currentPileOrder.Snapshot.ProjectedShuffleOrderValue
                        || node.Snapshot.ProjectedShuffleOrderValue
                            == currentPileOrder.Snapshot.ProjectedShuffleOrderValue
                            && IsBetterSearchNode(node, currentPileOrder))
                    {
                        bestPileOrderByRoutingChoice[signature.Value] = node;
                    }
                }
                List<IReadOnlyList<SearchNode>> paretoByRoutingChoice = [];
                List<IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>> routingFamilies =
                    nodesByRoutingChoice
                        .OrderByDescending(pair => pair.Value.Max(BeamRankScore))
                        .GroupBy(pair => BuildRoutingChoiceFamilySignature(pair.Key))
                        .OrderBy(family => family.Min(pair => RoutingParentRetentionRank(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => RoutingParentScore(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => pair.Value.Max(BeamRankScore)))
                        .Select(family => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)
                            OrderRoutingChoiceEventContexts(family))
                        .ToList();
                List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> orderedRoutingContexts = [];
                for (int round = 0; round < PersistentRoutingContextRounds; round++)
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in
                        routingFamilies.Where(family => IsPersistentRoutingEffect(family[0].Key.Effect)))
                    {
                        if (round < family.Count)
                            AddRoutingContext(orderedRoutingContexts, family[round]);
                    }
                }
                int routingContextRound = 0;
                while (routingFamilies.Any(family => routingContextRound < family.Count))
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in routingFamilies)
                    {
                        if (routingContextRound < family.Count)
                            AddRoutingContext(orderedRoutingContexts, family[routingContextRound]);
                    }
                    routingContextRound++;
                }
                foreach ((RoutingChoiceSignature signature, List<SearchNode> routingNodes) in orderedRoutingContexts)
                {
                    SearchNode? bestDeckCuration = FindBestDeckCuration(routingNodes);
                    SearchNode? bestTargetPressure = PreferMostVulnerableTargetVariant(
                        routingNodes,
                        FindBestTargetPressure(routingNodes));
                    List<SearchNode> candidates = [];
                    if (routingNodes.Min(ActionsSinceRetainedRoutingChoice) <= 1)
                    {
                        AddRoutingCandidate(candidates, bestSetupByRoutingChoice[signature]);
                        AddRoutingCandidate(candidates, bestTargetPressure);
                    }
                    else
                    {
                        AddRoutingCandidate(candidates, bestTargetPressure);
                        AddRoutingCandidate(candidates, bestDeckCuration);
                        AddRoutingCandidate(candidates, bestSetupByRoutingChoice[signature]);
                    }
                    foreach (SearchNode node in routingNodes.Take(16))
                        AddRoutingCandidate(candidates, node);
                    AddRoutingCandidate(candidates, bestScoreByRoutingChoice[signature]);
                    AddRoutingCandidate(candidates, bestOffenseByRoutingChoice[signature]);
                    AddRoutingCandidate(candidates, bestDefenseByRoutingChoice[signature]);
                    AddRoutingCandidate(candidates, bestPileOrderByRoutingChoice[signature]);
                    List<SearchNode> pareto = candidates
                        .Where(candidate => !candidates.Any(other =>
                            !ReferenceEquals(candidate, other)
                            && MultiObjectiveDominates(other, candidate)))
                        .ToList();
                    paretoByRoutingChoice.Add(pareto);
                }
                foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in routingFamilies)
                {
                    IReadOnlyList<SearchNode> familyNodes = family
                        .SelectMany(pair => pair.Value)
                        .ToList();
                    AddRoutingCandidate(
                        routingChoices,
                        PreferMostVulnerableTargetVariant(
                            familyNodes,
                            FindBestTargetPressure(familyNodes)),
                        RoutingChoiceLimit);
                    AddRoutingCandidate(
                        routingChoices,
                        FindBestDeckCuration(familyNodes),
                        RoutingChoiceLimit);
                    AddRoutingCandidate(
                        routingChoices,
                        FindBestSetup(familyNodes),
                        RoutingChoiceLimit);
                    foreach (IGrouping<RoutingChoiceOptionSignature,
                                 KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> optionGroup in family
                                 .GroupBy(pair => BuildRoutingChoiceOptionSignature(pair.Key)))
                    {
                        IReadOnlyList<SearchNode> optionNodes = optionGroup
                            .SelectMany(pair => pair.Value)
                            .ToList();
                        int actionsSinceChoice = optionNodes.Min(ActionsSinceRetainedRoutingChoice);
                        SearchNode? optionLeader;
                        if (actionsSinceChoice == 0)
                        {
                            optionLeader = optionGroup
                                .OrderBy(pair => RoutingParentRetentionRank(pair.Value))
                                .ThenByDescending(pair => RoutingParentScore(pair.Value))
                                .First()
                                .Value
                                .MaxBy(BeamRankScore);
                        }
                        else if (actionsSinceChoice == 1)
                        {
                            optionLeader = FindBestSetup(optionNodes);
                        }
                        else
                        {
                            optionLeader = PreferMostVulnerableTargetVariant(
                                optionNodes,
                                FindBestTargetPressure(optionNodes));
                        }
                        if (observedOptionLeaders != null && optionLeader != null)
                            observedOptionLeaders.Add(optionLeader);
                        AddRoutingCandidate(routingChoices, optionLeader, RoutingChoiceLimit);
                    }
                }
                foreach (SearchNode candidate in BuildDirectRoutingChoiceExtremes(ranked))
                {
                    if (routingChoices.Count >= RoutingChoiceLimit)
                        break;
                    AddRoutingCandidate(routingChoices, candidate, RoutingChoiceLimit);
                }
                int routingRound = 0;
                while (routingChoices.Count < RoutingChoiceLimit
                    && paretoByRoutingChoice.Any(group => routingRound < group.Count))
                {
                    foreach (IReadOnlyList<SearchNode> group in paretoByRoutingChoice)
                    {
                        if (routingRound < group.Count)
                            AddRoutingCandidate(routingChoices, group[routingRound], RoutingChoiceLimit);
                        if (routingChoices.Count >= RoutingChoiceLimit)
                            break;
                    }
                    routingRound++;
                }
            }
            if (ranked.Count <= limit)
            {
                observe?.Invoke(new GlobalRetentionDecision(
                    ranked, [], routingChoices, ranked, limit, limit, null, RoutingChoiceLimit,
                    observedRoutingSignatures, observedOptionLeaders, BeamRankScore));
                AssignRetentionRanks(ranked, []);
                return ranked;
            }

            int effectiveLimit = limit;
            bool preserveOrderedPile = preserveDefensiveRoute
                && _profile.Phase == SolverSearchPhase.Deep
                && ranked.Any(node => node.Snapshot.PocketwatchCardThreshold >= 0);
            int routingChoiceQuota = preserveOrderedPile
                ? BoundedRoutingChoiceQuota(routingChoices.Count)
                : _profile.Phase == SolverSearchPhase.Deep
                ? _isActEndingBoss
                    ? Math.Max(10, (limit + 3) / 2)
                    : Math.Max(8, limit * 2 / 5)
                : Math.Max(4, limit / 4);
            List<OrderedPileCohort> orderedPileCohorts = [];
            if (preserveOrderedPile)
            {
                List<IGrouping<StateFingerprint, SearchNode>> tacticalGroups = ranked
                    .Where(node => node.Snapshot.PocketwatchCardThreshold >= 0)
                    .GroupBy(BuildOrderedPileTacticalKey)
                    .OrderByDescending(group => group.Max(BeamRankScore))
                    .ToList();
                List<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> cadenceBuckets = tacticalGroups
                    .GroupBy(group => BuildPocketwatchCadenceSignature(group.First()))
                    .OrderByDescending(bucket => bucket.Max(group => group.Max(BeamRankScore)))
                    .Select(bucket => (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>)bucket
                        .OrderByDescending(group => group.Max(BeamRankScore))
                        .ToList())
                    .ToList();
                List<IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>>> cadenceFamilies =
                    cadenceBuckets
                        .GroupBy(bucket => BuildPocketwatchCadenceFamilySignature(bucket[0].First()))
                        .OrderByDescending(family => family.Max(bucket => bucket.Max(group => group.Max(BeamRankScore))))
                        .Select(family => (IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>>)family
                            .OrderByDescending(bucket => bucket.Max(group => group.Max(BeamRankScore)))
                            .ToList())
                        .ToList();
                cadenceBuckets = [];
                int cadenceRound = 0;
                while (cadenceFamilies.Any(family => cadenceRound < family.Count))
                {
                    foreach (IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> family in cadenceFamilies)
                    {
                        if (cadenceRound < family.Count)
                            cadenceBuckets.Add(family[cadenceRound]);
                    }
                    cadenceRound++;
                }
                List<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> paretoByCadence = [];
                foreach (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>> bucket in cadenceBuckets)
                {
                    List<IGrouping<StateFingerprint, SearchNode>> candidates = [];
                    AddTacticalGroup(candidates, bucket[0]);
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node => node.Snapshot.ProjectedPlayerHp))
                        .ThenBy(group => group.Min(node => node.Snapshot.EnemyHp))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderBy(group => group.Min(node => node.Snapshot.AliveEnemyCount))
                        .ThenBy(group => group.Min(node => node.Snapshot.EnemyHp))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node =>
                            LaneValue(node.Snapshot, SearchRouteTraits.Control)))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node =>
                            LaneValue(node.Snapshot, SearchRouteTraits.Resource)))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group =>
                            group.Max(node => node.Snapshot.ProjectedShuffleOrderValue))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    foreach (IGrouping<StateFingerprint, SearchNode> group in bucket)
                    {
                        if (candidates.Count >= SolverWeights.PocketwatchParetoCandidatesPerCadence)
                            break;
                        AddTacticalGroup(candidates, group);
                    }
                    List<IGrouping<StateFingerprint, SearchNode>> pareto = [];
                    foreach (IGrouping<StateFingerprint, SearchNode> candidate in candidates)
                    {
                        bool dominated = false;
                        foreach (IGrouping<StateFingerprint, SearchNode> other in candidates)
                        {
                            if (ReferenceEquals(candidate, other)
                                || !MultiObjectiveDominates(other.First(), candidate.First()))
                                continue;
                            dominated = true;
                            break;
                        }
                        if (!dominated)
                            pareto.Add(candidate);
                    }
                    paretoByCadence.Add(pareto);
                }
                List<IGrouping<StateFingerprint, SearchNode>> selectedTacticalGroups = [];
                int paretoRound = 0;
                while (paretoByCadence.Any(bucket => paretoRound < bucket.Count))
                {
                    foreach (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>> bucket in paretoByCadence)
                    {
                        if (paretoRound < bucket.Count)
                            AddTacticalGroup(selectedTacticalGroups, bucket[paretoRound]);
                    }
                    paretoRound++;
                }
                orderedPileCohorts = selectedTacticalGroups
                    .Select(group => new OrderedPileCohort(group
                        .GroupBy(node => node.Snapshot.ProjectedShuffleOrderKey)
                        .SelectMany(prefixGroup => prefixGroup
                            .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                            .ThenByDescending(BeamRankScore)
                            .Take(SolverWeights.ExactStatesPerProjectedShuffleOrder))
                        .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                        .ThenByDescending(BeamRankScore)
                        .Take(SolverWeights.OrderedPileVariantsPerTacticalState)
                        .ToList()))
                    .ToList();
                int orderedPileRepresentativeCount = orderedPileCohorts.Sum(cohort => cohort.PrefixVariants.Count);
                effectiveLimit = Math.Max(
                    limit,
                    Math.Min(
                        checked(limit + Math.Min(routingChoiceQuota, routingChoices.Count) + 1),
                        limit + orderedPileRepresentativeCount));
            }

            SearchNode? bestPotionFree = null;
            SearchNode? bestPotion = null;
            SearchNode? bestPotionFreeDefensive = null;
            SearchNode? bestPotionDefensive = null;
            SearchNode? bestDefensive = null;
            SearchNode? bestUtilityDefensive = null;
            SearchNode? bestPotionFreeUtilityDefensive = null;
            SearchNode? bestOffensive = null;
            SearchNode? bestPotionFreeOffensive = null;
            SearchNode? bestPotionOffensive = null;
            SearchNode? bestResourcePreserving = null;
            foreach (SearchNode node in ranked)
            {
                bool potion = UsesPotion(node);
                if (potion)
                {
                    bestPotion ??= node;
                    if (IsBetterDefensive(node, bestPotionDefensive))
                        bestPotionDefensive = node;
                    if (IsBetterOffensive(node, bestPotionOffensive))
                        bestPotionOffensive = node;
                }
                else
                {
                    bestPotionFree ??= node;
                    if (IsBetterDefensive(node, bestPotionFreeDefensive))
                        bestPotionFreeDefensive = node;
                    if (node.Traits != SearchRouteTraits.None
                        && IsBetterUtilityDefensive(node, bestPotionFreeUtilityDefensive))
                    {
                        bestPotionFreeUtilityDefensive = node;
                    }
                    if (IsBetterOffensive(node, bestPotionFreeOffensive))
                        bestPotionFreeOffensive = node;
                }
                if (!preserveDefensiveRoute)
                    continue;
                if (IsBetterDefensive(node, bestDefensive))
                    bestDefensive = node;
                if (node.Traits != SearchRouteTraits.None && IsBetterUtilityDefensive(node, bestUtilityDefensive))
                    bestUtilityDefensive = node;
                if (IsBetterOffensive(node, bestOffensive))
                    bestOffensive = node;
                if (_theftPolicy == SolverTheftPolicy.PreserveResources
                    && IsBetterResourcePreserving(node, bestResourcePreserving))
                {
                    bestResourcePreserving = node;
                }
            }

            List<SearchNode> required = [];
            foreach (IGrouping<int, SearchNode> victoryGroup in ranked
                         .Where(IsCompleteVictory)
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                AddRequired(required, victoryGroup.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterCompletedVictory(node, best) ? node : best), limit);
            }
            if (preserveDefensiveRoute && _profile.Phase == SolverSearchPhase.Deep)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    IReadOnlyList<SearchNode> group = potionGroup.ToList();
                    AddRequired(required, FindBestFreshResourceStandPat(group), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Scaling), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Resource), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Control), limit);
                }

                int rootLineageLimit = Math.Clamp(limit / 8, 4, 16);
                foreach (IGrouping<RootActionLineageSignature, SearchNode> lineage in ranked
                             .Where(node => node.Action != null)
                             .GroupBy(BuildRootActionLineageSignature)
                             .OrderBy(group => RootActionLineageNode(group.First()).RetentionRank)
                             .ThenByDescending(group => group.Max(BeamRankScore))
                             .Take(rootLineageLimit))
                {
                    IReadOnlyList<SearchNode> candidates = lineage.ToList();
                    AddRequired(required, candidates.MaxBy(BeamRankScore), limit);
                    AddRequired(required, candidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                    AddRequired(required, candidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                    AddRequired(required, FindBestSetup(candidates), limit);
                    if (_preserveReplayAllocatorOpening)
                    {
                        AddRequired(required, FindBestCuratedTurnBoundaryHand(candidates), limit);
                        AddRequired(required, FindBestTacticalEnabler(candidates), limit);
                        AddRequired(required, FindBestTargetPressure(candidates), limit);
                        AddRequired(required, FindBestDeckCuration(candidates), limit);
                        AddRequired(required, candidates
                            .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                            .ThenByDescending(BeamRankScore)
                            .First(), limit);
                    }
                }
            }
            bool endTurnFrontier = ranked.All(node =>
                node.Action is { } action
                && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
            if (endTurnFrontier && preserveDefensiveRoute)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    AddRequired(required, FindBestTurnBoundaryHand(potionGroup), effectiveLimit);
                }
            }
            int orderedPileQuota = orderedPileCohorts.Count == 0
                ? 0
                : endTurnFrontier
                    || ranked.Any(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                    ? Math.Max(8, limit * 2 / 3)
                    : limit + 1;
            int orderedPileRounds = orderedPileCohorts.Count == 0
                ? 0
                : orderedPileCohorts.Max(cohort => cohort.PrefixVariants.Count);
            if (endTurnFrontier && orderedPileQuota > 0)
            {
                int strategicExactQuota = Math.Min(16, orderedPileQuota / 2);
                foreach (var cadence in ranked
                             .Where(node => node.PotionCount > 0
                                 && node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                             .GroupBy(node => (
                                 Cadence: BuildPocketwatchCadenceSignature(node),
                                 node.Snapshot.RetainedAttackValue))
                             .OrderByDescending(group => group.Max(node => node.Snapshot.FocusTargetPressure))
                             .ThenBy(group => group.Min(node => node.Snapshot.FocusTargetRemainingHp))
                             .ThenByDescending(group => group.Max(node => node.Snapshot.ProjectedShuffleOrderValue))
                             .Take(Math.Max(1, strategicExactQuota /
                                 SolverWeights.PotionEndTurnExactStatesPerProjectedShuffleOrder)))
                {
                    SearchNode? representative = FindMostCompressedDeck(cadence.ToList());
                    if (representative == null)
                        continue;
                    StateFingerprint tacticalKey = BuildOrderedPileTacticalKey(representative);
                    foreach (SearchNode exactState in cadence
                                 .Where(node => BuildOrderedPileTacticalKey(node) == tacticalKey
                                     && node.Snapshot.ProjectedShuffleOrderKey ==
                                        representative.Snapshot.ProjectedShuffleOrderKey)
                                 .OrderByDescending(BeamRankScore)
                                 .Take(SolverWeights.PotionEndTurnExactStatesPerProjectedShuffleOrder))
                    {
                        AddRequired(required, exactState, strategicExactQuota);
                    }
                }
            }
            int exactStateRounds = Math.Min(
                SolverWeights.ExactStatesPerProjectedShuffleOrder,
                orderedPileRounds);
            for (int round = 0; round < exactStateRounds && required.Count < orderedPileQuota; round++)
            {
                foreach (OrderedPileCohort cohort in orderedPileCohorts)
                {
                    if (round < cohort.PrefixVariants.Count)
                        AddRequired(required, cohort.PrefixVariants[round], orderedPileQuota);
                }
            }
            for (int round = exactStateRounds;
                 round < orderedPileRounds && required.Count < orderedPileQuota;
                 round++)
            {
                foreach (OrderedPileCohort cohort in orderedPileCohorts)
                {
                    if (round < cohort.PrefixVariants.Count)
                        AddRequired(required, cohort.PrefixVariants[round], orderedPileQuota);
                }
            }
            if (ranked.Any(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression)))
            {
                List<IGrouping<StateFingerprint, SearchNode>> compressionLineages = ranked
                             .Where(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                             .GroupBy(EndTurnDeckCompressionLineageKey)
                             .OrderBy(group => group.Min(node =>
                                 EndTurnDeckCompressionLineageRoot(node).RetentionRank))
                             .ThenByDescending(group => group.Max(BeamRankScore))
                             .ToList();
                foreach (IGrouping<StateFingerprint, SearchNode> compressionLineage in compressionLineages.Take(12))
                {
                    IReadOnlyList<SearchNode> lineageCandidates = compressionLineage.ToList();
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestLane(
                                lineageCandidates,
                                SearchRouteTraits.EndTurnDeckCompression)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestCompressionAttackGrowth(lineageCandidates)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        FindBestLane(lineageCandidates, SearchRouteTraits.Resource),
                        effectiveLimit);
                    AddRequired(required, FindBestDeckCuration(lineageCandidates), effectiveLimit);
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestTargetPressure(lineageCandidates)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        lineageCandidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterOffensive(node, best) ? node : best),
                        effectiveLimit);
                }
                foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (var lineage in potionCountGroup
                                 .Where(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                                 .GroupBy(node => (
                                     Lineage: EndTurnDeckCompressionLineageKey(node),
                                     Parent: node.Parent?.StateKey ?? default))
                                 .OrderBy(group => group.Min(node =>
                                     node.Parent?.RetentionRank ?? node.RetentionRank))
                                 .ThenByDescending(group => group.Max(BeamRankScore))
                                 .Take(12))
                    {
                        IReadOnlyList<SearchNode> group = lineage.ToList();
                        SearchNode? compressionLeader = PreferMostVulnerableTargetVariant(
                            group,
                            FindBestLane(group, SearchRouteTraits.EndTurnDeckCompression));
                        AddRequired(required, compressionLeader, effectiveLimit);
                        foreach (IGrouping<(PlanActionKind Kind, string CardId, string PotionId), SearchNode>
                                     actionGroup in group
                                 .Where(node => node.Action != null)
                                 .GroupBy(node => (
                                     node.Action!.Kind,
                                     node.Action.CardId,
                                     node.Action.PotionId))
                                 .OrderByDescending(candidates => candidates.Max(node =>
                                     LaneValue(node.Snapshot, SearchRouteTraits.EndTurnDeckCompression)))
                                 .ThenByDescending(candidates => candidates.Max(BeamRankScore))
                                 .Take(8))
                        {
                            IReadOnlyList<SearchNode> actionCandidates = actionGroup.ToList();
                            AddRequired(
                                required,
                                PreferMostVulnerableTargetVariant(
                                    actionCandidates,
                                    FindBestLane(
                                        actionCandidates,
                                        SearchRouteTraits.EndTurnDeckCompression)),
                                effectiveLimit);
                        }
                    }
                }
            }
            foreach (SearchNode routingChoice in routingChoices.Take(routingChoiceQuota))
            {
                AddRequired(required, routingChoice, effectiveLimit);
            }
            if (preserveDefensiveRoute)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    IReadOnlyList<SearchNode> artOfWarCandidates = potionGroup
                        .Where(node => node.Snapshot.CanTriggerArtOfWarNextTurn)
                        .ToList();
                    AddRequired(required, artOfWarCandidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterDefensive(node, best) ? node : best), effectiveLimit);
                    AddRequired(required, FindBestSetup(artOfWarCandidates), effectiveLimit);
                }
            }
            if (preserveDefensiveRoute && _profile.Phase == SolverSearchPhase.Deep)
            {
                int signatureLimitPerPotionGroup = Math.Max(4, limit / 6);
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<PersistentSetupTraits, SearchNode> setupGroup in potionGroup
                                 .Where(node => node.Snapshot.StrategicSetupTraits != PersistentSetupTraits.None)
                                 .GroupBy(node => node.Snapshot.StrategicSetupTraits)
                                 .OrderByDescending(group => group.Max(BeamRankScore))
                                 .Take(signatureLimitPerPotionGroup))
                    {
                        IReadOnlyList<SearchNode> candidates = setupGroup.ToList();
                        AddRequired(required, candidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                        AddRequired(required, candidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterSetup(node, best) ? node : best), limit);
                    }
                }

                int focusTargetsPerPotionGroup = Math.Clamp(limit / 10, 2, 4);
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<uint?, SearchNode> targetGroup in potionGroup
                                 .Where(node => node.Snapshot.FocusTargetCombatId != null)
                                 .GroupBy(node => node.Snapshot.FocusTargetCombatId)
                                 .OrderByDescending(group => group.Max(node => node.Snapshot.FocusTargetPressure))
                                 .Take(focusTargetsPerPotionGroup))
                    {
                        IReadOnlyList<SearchNode> candidates = targetGroup.ToList();
                        AddRequired(required, FindBestTargetPressure(candidates), limit);
                        AddRequired(required, FindBestTargetSetup(candidates), limit);
                    }
                }
            }
            IReadOnlyList<SearchNode> declinedExtraTurn = ranked
                .Where(node => node.Traits.HasFlag(SearchRouteTraits.DeclinedExtraTurn))
                .ToList();
            if (declinedExtraTurn.Count > 0)
            {
                AddRequired(required, declinedExtraTurn[0], limit);
                AddRequired(required, declinedExtraTurn.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                AddRequired(required, declinedExtraTurn.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                AddRequired(required, FindBestSetup(declinedExtraTurn), limit);
            }
            if (_potionPolicy != SolverPotionPolicy.Disabled)
            {
                int potionLineageLimit = Math.Clamp(limit / 6, 2, 6);
                foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                             .Where(UsesPotion)
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<string, SearchNode> potionLineage in potionCountGroup
                                 .GroupBy(PotionUseLineageKey, StringComparer.Ordinal)
                                 .OrderByDescending(group => group.Max(BeamRankScore))
                                 .Take(potionLineageLimit))
                    {
                        AddRequired(
                            required,
                            FindBestPotionLineage(potionLineage),
                            limit);
                    }
                }
            }
            foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                IReadOnlyList<SearchNode> group = potionCountGroup.ToList();
                AddRequired(required, group[0], limit);
                AddRequired(required, group.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                AddRequired(required, group.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                AddRequired(required, FindBestEnemyStrengthControl(group), limit);
                AddRequired(required, FindBestEnemyWeakControl(group), limit);
                AddRequired(required, FindBestDeckCuration(group), limit);
                AddRequired(required, FindMostCompressedDeck(group), limit);
                AddRequired(required, FindBestTacticalEnabler(group), limit);
                AddRequired(required, FindBestSetup(group), limit);
                if (_theftPolicy == SolverTheftPolicy.PreserveResources)
                {
                    AddRequired(required, group.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterResourcePreserving(node, best) ? node : best), limit);
                }
            }
            AddRequired(required, bestPotionFree, limit);
            AddRequired(required, bestPotionFreeDefensive, limit);
            AddRequired(required, bestPotionFreeOffensive, limit);
            AddRequired(required, FindBestSetup(ranked.Where(node => !UsesPotion(node))), limit);
            AddRequired(required, bestPotion, limit);
            AddRequired(required, bestPotionDefensive, limit);
            AddRequired(required, bestPotionOffensive, limit);
            AddRequired(required, FindBestSetup(ranked.Where(UsesPotion)), limit);
            AddRequired(required, bestDefensive, limit);
            AddRequired(required, bestUtilityDefensive, limit);
            AddRequired(required, bestPotionFreeUtilityDefensive, limit);
            AddRequired(required, bestOffensive, limit);
            AddRequired(required, bestResourcePreserving, limit);
            AddRequired(required, FindBestLane(ranked, SearchRouteTraits.LongTermResource), limit);
            AddRequired(required, FindBestLane(ranked, SearchRouteTraits.HpInvestment), limit);
            if (preserveDefensiveRoute
                && _profile.Phase == SolverSearchPhase.Deep
                && limit >= 18)
            {
                foreach (SearchRouteTraits trait in new[]
                         {
                             SearchRouteTraits.Scaling,
                             SearchRouteTraits.Resource,
                             SearchRouteTraits.Control,
                             SearchRouteTraits.RevivalWindow,
                             SearchRouteTraits.ReactiveDamage,
                             SearchRouteTraits.EndTurnDeckCompression,
                             SearchRouteTraits.LongTermResource,
                             SearchRouteTraits.HpInvestment,
                         })
                {
                    foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                                 .GroupBy(node => node.PotionCount)
                                 .OrderBy(group => group.Key))
                    {
                        AddRequired(required, FindBestLane(potionCountGroup.ToList(), trait), limit);
                        if (trait == SearchRouteTraits.HpInvestment)
                        {
                            AddRequired(
                                required,
                                FindSafestHpInvestment(potionCountGroup),
                                limit);
                        }
                    }
                }
                // MultiObjectiveDominates intentionally cannot compare nodes from different
                // combat/control/pile cohorts. Looking at the whole ranked pool therefore did
                // O(n^2) fingerprint checks at large turn boundaries (tens of thousands of
                // ended candidates) even though nearly every pair was incomparable.
                Dictionary<(
                    StateFingerprint EnemyCombat,
                    StateFingerprint EnemyControl,
                    StateFingerprint UnorderedPile), List<SearchNode>> paretoCohorts = [];
                foreach (SearchNode node in ranked)
                {
                    var cohortKey = (
                        node.Snapshot.EnemyCombatDistributionKey,
                        node.Snapshot.EnemyControlDistributionKey,
                        node.Snapshot.UnorderedPileKey);
                    if (!paretoCohorts.TryGetValue(cohortKey, out List<SearchNode>? cohort))
                    {
                        cohort = [];
                        paretoCohorts.Add(cohortKey, cohort);
                    }
                    cohort.Add(node);
                }

                List<SearchNode> pareto = new(3);
                foreach (SearchNode candidate in ranked)
                {
                    bool dominated = false;
                    var cohortKey = (
                        candidate.Snapshot.EnemyCombatDistributionKey,
                        candidate.Snapshot.EnemyControlDistributionKey,
                        candidate.Snapshot.UnorderedPileKey);
                    foreach (SearchNode other in paretoCohorts[cohortKey])
                    {
                        if (!MultiObjectiveDominates(other, candidate))
                            continue;
                        dominated = true;
                        break;
                    }
                    if (dominated)
                        continue;
                    int insertIndex = 0;
                    while (insertIndex < pareto.Count
                           && (pareto[insertIndex].Score > candidate.Score
                               || pareto[insertIndex].Score.Equals(candidate.Score)
                                   && pareto[insertIndex].ActionCount <= candidate.ActionCount))
                    {
                        insertIndex++;
                    }
                    if (insertIndex >= 3)
                        continue;
                    pareto.Insert(insertIndex, candidate);
                    if (pareto.Count > 3)
                        pareto.RemoveAt(3);
                }
                foreach (SearchNode candidate in pareto)
                    AddRequired(required, candidate, limit);
            }

            List<SearchNode> quotaPool = ranked.ToList();
            if (ranked.Count > effectiveLimit)
                ranked.RemoveRange(effectiveLimit, ranked.Count - effectiveLimit);
            foreach (SearchNode requiredNode in required)
            {
                if (ContainsReference(ranked, requiredNode))
                    continue;
                int replaceIndex = -1;
                for (int index = ranked.Count - 1; index >= 0; index--)
                {
                    if (ContainsReference(required, ranked[index]))
                        continue;
                    replaceIndex = index;
                    break;
                }
                if (replaceIndex < 0)
                    throw new InvalidOperationException("Beam 容量不足以保留策略必需分支。");
                ranked[replaceIndex] = requiredNode;
            }
            DiversifyOrdinaryBeamBoundary(
                quotaPool,
                ranked,
                required,
                node => (
                    BeamRankScore(node),
                    node.ActionCount,
                    node.Snapshot.OffensiveProgressValue,
                    node.PotionCount,
                    IsCompleteVictory(node)),
                finalQualityFirst,
                node => new OrdinaryBeamTacticalValues(
                    node.Turn,
                    node.PotionCount,
                    node.PotionStrategicCost,
                    node.FutureSoldHp,
                    node.Snapshot.CumulativePlayerHpLost,
                    node.ActionCount,
                    node.Score,
                    node.Snapshot.ZeroCostPlayableCount,
                    node.Snapshot.ReachableHandValue,
                    node.Snapshot.HandCount,
                    HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null));
            if (_potionPolicy != SolverPotionPolicy.Disabled
                && quotaPool.Any(UsesPotion)
                && quotaPool.Any(node => !UsesPotion(node)))
            {
                (int usedPotionQuota, int unusedPotionQuota) =
                    FeasiblePotionUseQuotas(limit);
                HashSet<SearchNode> quotaReservations = new(
                    required,
                    ReferenceEqualityComparer.Instance);
                ReservePotionQuotaLeaders(
                    quotaReservations,
                    quotaPool,
                    usesPotion: true,
                    usedPotionQuota);
                ReservePotionQuotaLeaders(
                    quotaReservations,
                    quotaPool,
                    usesPotion: false,
                    unusedPotionQuota);
                EnforcePotionUseQuota(
                    ranked,
                    quotaPool,
                    quotaReservations,
                    usesPotion: true,
                    usedPotionQuota);
                EnforcePotionUseQuota(
                    ranked,
                    quotaPool,
                    quotaReservations,
                    usesPotion: false,
                    unusedPotionQuota);
            }
            ranked.Sort(finalQualityFirst
                ? CompareFinalCandidates
                : (left, right) => CompareBeamRankOrder(
                    BeamRankScore(left),
                    left.Snapshot.OffensiveProgressValue,
                    left.ActionCount,
                    BeamRankScore(right),
                    right.Snapshot.OffensiveProgressValue,
                    right.ActionCount));
            observe?.Invoke(new GlobalRetentionDecision(
                quotaPool, required, routingChoices, ranked, limit, effectiveLimit,
                routingChoiceQuota, RoutingChoiceLimit,
                observedRoutingSignatures, observedOptionLeaders, BeamRankScore));
            AssignRetentionRanks(ranked, required);
            return ranked;
        }

        private SearchNode FindBestOrderedMutationRepresentative(
            IEnumerable<SearchNode> candidates,
            HashSet<SearchNode> selectedSet)
        {
            SearchNode? selectedBest = null;
            SearchNode? best = null;
            foreach (SearchNode candidate in candidates)
            {
                if (IsBetterOrderedMutationRepresentative(candidate, best))
                    best = candidate;
                if (selectedSet.Contains(candidate)
                    && IsBetterOrderedMutationRepresentative(candidate, selectedBest))
                {
                    selectedBest = candidate;
                }
            }
            return selectedBest ?? best
                ?? throw new InvalidOperationException("有序变异候选组为空。");
        }

        private static OrderedMutationBoundaryStamp? OrderedMutationBoundaryStampFor(
            SearchNode node)
            => node.OrderedMutationBoundaryLineage is { } boundary
                ? new OrderedMutationBoundaryStamp(
                    boundary.FromTurn,
                    boundary.FromShufflesCrossed,
                    boundary.ToTurn,
                    boundary.ToShufflesCrossed)
                : null;

        private bool IsBetterOrderedMutationRepresentative(
            SearchNode candidate,
            SearchNode? current)
            => current == null
                || CompareOrderedMutationRepresentatives(candidate, current) < 0;

        private int CompareOrderedMutationRepresentatives(
            SearchNode left,
            SearchNode right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            bool leftBetterSetup = IsBetterSetup(left, right);
            bool rightBetterSetup = IsBetterSetup(right, left);
            if (leftBetterSetup != rightBetterSetup)
                return leftBetterSetup ? -1 : 1;
            int comparison = left.RetentionRank.CompareTo(right.RetentionRank);
            if (comparison != 0)
                return comparison;
            comparison = BeamRankScore(right).CompareTo(BeamRankScore(left));
            if (comparison != 0)
                return comparison;
            comparison = left.ActionCount.CompareTo(right.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(left.StateKey, right.StateKey);
            if (comparison != 0)
                return comparison;
            StateFingerprint leftOption = left.Action == null
                ? default
                : BuildOrderedMutationContinuationOptionKey(left.Action);
            StateFingerprint rightOption = right.Action == null
                ? default
                : BuildOrderedMutationContinuationOptionKey(right.Action);
            comparison = CompareOrderedMutationFingerprints(leftOption, rightOption);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(
                left.Parent?.StateKey ?? default,
                right.Parent?.StateKey ?? default);
            if (comparison != 0)
                return comparison;
            return CompareOrderedMutationFingerprints(
                left.OrderedMutationLineage?.SequenceKey ?? default,
                right.OrderedMutationLineage?.SequenceKey ?? default);
        }

        private static int CompareOrderedMutationFingerprints(
            StateFingerprint left,
            StateFingerprint right)
        {
            int comparison = left.First.CompareTo(right.First);
            return comparison != 0
                ? comparison
                : left.Second.CompareTo(right.Second);
        }

        private int CompareOrderedMutationActivationCandidates(
            OrderedMutationActivationCandidate left,
            OrderedMutationActivationCandidate right)
        {
            int comparison = (left.Node.Parent?.RetentionRank ?? int.MaxValue).CompareTo(
                right.Node.Parent?.RetentionRank ?? int.MaxValue);
            if (comparison != 0)
                return comparison;
            bool leftBetterSetup = IsBetterSetup(left.Node, right.Node);
            bool rightBetterSetup = IsBetterSetup(right.Node, left.Node);
            if (leftBetterSetup != rightBetterSetup)
                return leftBetterSetup ? -1 : 1;
            comparison = left.Node.RetentionRank.CompareTo(right.Node.RetentionRank);
            if (comparison != 0)
                return comparison;
            comparison = BeamRankScore(right.Node).CompareTo(BeamRankScore(left.Node));
            if (comparison != 0)
                return comparison;
            comparison = left.Node.ActionCount.CompareTo(right.Node.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = left.Node.StateKey.First.CompareTo(right.Node.StateKey.First);
            if (comparison != 0)
                return comparison;
            comparison = left.Node.StateKey.Second.CompareTo(right.Node.StateKey.Second);
            if (comparison != 0)
                return comparison;
            comparison = left.SequenceKey.First.CompareTo(right.SequenceKey.First);
            return comparison != 0
                ? comparison
                : left.SequenceKey.Second.CompareTo(right.SequenceKey.Second);
        }

        private int CompareOrderedMutationActivationCohorts(
            OrderedMutationActivationCohort left,
            OrderedMutationActivationCohort right)
        {
            // A cold family is useful only if both sides survive, so rank by the weaker seed
            // first. Outcome hashes are deterministic final tie-breaks, never quality signals.
            int comparison = CompareOrderedMutationActivationCandidates(
                left.Candidates[1],
                right.Candidates[1]);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationActivationCandidates(
                left.Candidates[0],
                right.Candidates[0]);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.Turn.CompareTo(right.Family.Turn);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.PotionCount.CompareTo(right.Family.PotionCount);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.ChoiceCount.CompareTo(right.Family.ChoiceCount);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.EffectMultisetKey.First.CompareTo(
                right.Family.EffectMultisetKey.First);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.EffectMultisetKey.Second.CompareTo(
                right.Family.EffectMultisetKey.Second);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.UnorderedOutcomeKey.First.CompareTo(
                right.Family.UnorderedOutcomeKey.First);
            if (comparison != 0)
                return comparison;
            comparison = left.Family.UnorderedOutcomeKey.Second.CompareTo(
                right.Family.UnorderedOutcomeKey.Second);
            return comparison != 0
                ? comparison
                : CompareOrderedMutationBoundaryStamps(
                    left.Family.Boundary,
                    right.Family.Boundary);
        }

        private static int CompareOrderedMutationBoundaryStamps(
            OrderedMutationBoundaryStamp? left,
            OrderedMutationBoundaryStamp? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue.CompareTo(right.HasValue);
            int comparison = left.Value.FromTurn.CompareTo(right.Value.FromTurn);
            if (comparison != 0)
                return comparison;
            comparison = left.Value.FromShufflesCrossed.CompareTo(
                right.Value.FromShufflesCrossed);
            if (comparison != 0)
                return comparison;
            comparison = left.Value.ToTurn.CompareTo(right.Value.ToTurn);
            return comparison != 0
                ? comparison
                : left.Value.ToShufflesCrossed.CompareTo(
                    right.Value.ToShufflesCrossed);
        }

        private bool TryAdmitOrderedMutationActivationCohort(
            OrderedMutationActivationCohort cohort,
            int portfolioPriority,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet)
        {
            if (cohort.Candidates.Count != 2
                || cohort.Candidates[0].SequenceKey == cohort.Candidates[1].SequenceKey
                || !CanReserveOrderedMutationAdmissions(
                    _run,
                    cohort.Candidates.Select(candidate => candidate.Lease)))
            {
                return false;
            }

            OrderedMutationActivationTicket ticket = new(
                BuildOrderedMutationActivationKey(cohort.Family));
            foreach (OrderedMutationActivationCandidate candidate in cohort.Candidates)
            {
                bool alreadySelected = selectedSet.Contains(candidate.Node);
                candidate.Node.OrderedMutationRetentionLease = candidate.Lease with
                {
                    PortfolioPriority = portfolioPriority,
                };
                candidate.Node.OrderedMutationActivationTicket = ticket;
                candidate.Node.OrderedMutationAdmissionPending = true;
                if (alreadySelected)
                {
                    _run.PendingOrderedMutationOrdinaryFallbackNodes.Add(
                        candidate.Node);
                }
            }
            if (cohort.Candidates.Any(candidate =>
                    !CanRetainOrderedMutationLease(_run, candidate.Node)))
            {
                foreach (OrderedMutationActivationCandidate candidate in cohort.Candidates)
                {
                    candidate.Node.OrderedMutationRetentionLease = null;
                    candidate.Node.OrderedMutationActivationTicket = null;
                    candidate.Node.OrderedMutationAdmissionPending = false;
                }
                return false;
            }
            foreach (OrderedMutationActivationCandidate candidate in cohort.Candidates)
            {
                if (selectedSet.Add(candidate.Node))
                    selected.Add(candidate.Node);
            }
            return true;
        }

        private static StateFingerprint BuildOrderedMutationActivationKey(
            OrderedMutationOutcomeFamilySignature family)
        {
            StateFingerprintBuilder key = new();
            key.Add('A');
            key.Add(family.Turn);
            key.Add(family.PotionCount);
            key.Add(family.ChoiceCount);
            key.Add(family.EffectMultisetKey.First);
            key.Add(family.EffectMultisetKey.Second);
            key.Add(family.UnorderedOutcomeKey.First);
            key.Add(family.UnorderedOutcomeKey.Second);
            AppendOrderedMutationBoundaryStamp(ref key, family.Boundary);
            return key.Finish();
        }

        private IReadOnlyList<OrderedMutationContinuationPacket>
            BuildOrderedMutationContinuationPackets(
            IEnumerable<SearchNode> candidates,
            HashSet<SearchNode> selectedSet)
        {
            List<OrderedMutationContinuationPacket> packets = [];
            foreach (IGrouping<OrderedMutationContinuationSourceFamilySignature, SearchNode>
                     sourceFamily in candidates
                         .GroupBy(candidate =>
                         {
                             bool hasPersistentMutation =
                                 TryBuildOrderedMutationContinuationSourceFamilyKey(
                                     candidate.Action!,
                                     out StateFingerprint key);
                             return new OrderedMutationContinuationSourceFamilySignature(
                                 key,
                                 hasPersistentMutation);
                         })
                         .OrderByDescending(group => group.Key.HasPersistentMutation)
                         .ThenBy(group => group.Key.Key.First)
                         .ThenBy(group => group.Key.Key.Second))
            {
                List<SearchNode> unselectedCandidates = sourceFamily
                    .Where(candidate => !selectedSet.Contains(candidate))
                    .ToList();
                if (unselectedCandidates.Count == 0)
                    continue;
                HashSet<OrderedMutationContinuationOutcomeKey> selectedOutcomeKeys = sourceFamily
                    .Where(selectedSet.Contains)
                    .Select(BuildOrderedMutationContinuationOutcomeKey)
                    .ToHashSet();
                if (sourceFamily.Key.HasPersistentMutation
                    && selectedOutcomeKeys.Count > 0)
                {
                    HashSet<OrderedMutationContinuationOutcomeKey> suppressedOutcomeKeys =
                        unselectedCandidates
                            .Select(BuildOrderedMutationContinuationOutcomeKey)
                            .Where(selectedOutcomeKeys.Contains)
                            .ToHashSet();
                    foreach (SearchNode selectedCandidate in sourceFamily
                                 .Where(selectedSet.Contains)
                                 .Where(candidate => suppressedOutcomeKeys.Contains(
                                     BuildOrderedMutationContinuationOutcomeKey(candidate))))
                    {
                        // Only an outcome which actually suppresses an equivalent backup has
                        // spent coverage credit. Record that dependency explicitly; a real
                        // final survivor below must repay it with one observed edge.
                        RequestOrderedMutationObservation(selectedCandidate);
                    }
                }
                List<SearchNode> representatives = unselectedCandidates
                    .GroupBy(BuildOrderedMutationContinuationOutcomeKey)
                    .Where(group => !selectedOutcomeKeys.Contains(group.Key))
                    .Select(group => FindBestOrderedMutationRepresentative(group, selectedSet))
                    .ToList();
                if (representatives.Count == 0)
                    continue;
                packets.Add(BuildOrderedMutationContinuationPacket(
                    representatives,
                    sourceFamily.Key,
                    BuildOrderedMutationContinuationOptionUniverseKey(representatives),
                    sourceFamily.Any(selectedSet.Contains)));
            }
            return packets;
        }

        private OrderedMutationContinuationPacket BuildOrderedMutationContinuationPacket(
            IReadOnlyList<SearchNode> representatives,
            OrderedMutationContinuationSourceFamilySignature sourceFamily,
            StateFingerprint optionUniverseKey,
            bool hasSelectedSibling)
        {
            List<SearchNode> sampledOutcomes = SelectOrderedMutationOptionCohort(
                representatives,
                CompareOrderedMutationRepresentatives,
                OrderedMutationContinuationSemanticDistance,
                (left, right) => ReferenceEquals(left, right)
                    || BuildOrderedMutationContinuationOutcomeKey(left)
                        == BuildOrderedMutationContinuationOutcomeKey(right),
                sourceFamily.HasPersistentMutation
                    ? MaximumOrderedMutationOptionCohortOutcomes
                    : 1);
            SearchNode leader = sampledOutcomes[0];

            OrderedMutationRetentionLease lease = leader.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException("有序变异 continuation 缺少 lease。");
            int priorAdmissions = _run.OrderedMutationAdmissionsByLease.GetValueOrDefault(
                lease.Key);
            List<SearchNode> selectedOutcomes = SelectActiveOrderedMutationOptionPair(
                sampledOutcomes,
                priorAdmissions);
            bool hasRotatedInteriorOption = sampledOutcomes.Count > 2
                && ReferenceEquals(selectedOutcomes[1], sampledOutcomes[2]);
            SearchNode parent = leader.Parent
                ?? throw new InvalidOperationException("有序变异 continuation 缺少 parent。");
            return new OrderedMutationContinuationPacket(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                parent.OrderedMutationLineage?.SequenceKey ?? default,
                sourceFamily.Key,
                optionUniverseKey,
                sourceFamily.HasPersistentMutation,
                hasSelectedSibling,
                hasRotatedInteriorOption,
                lease.PortfolioPriority,
                parent,
                selectedOutcomes);
        }

        /// <summary>
        /// Samples a bounded categorical option set without assuming that quality is monotone
        /// with long-horizon value. The leader protects immediate quality, the farthest outcome
        /// protects a semantic extreme, and the quality median protects an interior option that
        /// neither extreme can represent. Input enumeration order is deliberately irrelevant.
        /// </summary>
        internal static List<T> SelectOrderedMutationOptionCohort<T>(
            IReadOnlyList<T> candidates,
            Comparison<T> qualityComparison,
            Func<T, T, long> semanticDistance,
            Func<T, T, bool> isSameCandidate,
            int maximumOutcomes)
        {
            if (candidates.Count == 0 || maximumOutcomes <= 0)
                return [];

            List<T> ordered = candidates
                .OrderBy(candidate => candidate, Comparer<T>.Create(qualityComparison))
                .ToList();
            List<T> selected = [ordered[0]];
            if (selected.Count >= maximumOutcomes)
                return selected;

            T leader = selected[0];
            T? farthest = default;
            bool hasFarthest = false;
            long farthestDistance = long.MinValue;
            foreach (T candidate in ordered)
            {
                if (isSameCandidate(candidate, leader))
                    continue;
                long distance = semanticDistance(leader, candidate);
                if (!hasFarthest
                    || distance > farthestDistance
                    || distance == farthestDistance
                        && qualityComparison(candidate, farthest!) < 0)
                {
                    farthest = candidate;
                    hasFarthest = true;
                    farthestDistance = distance;
                }
            }
            if (hasFarthest)
                selected.Add(farthest!);
            if (selected.Count >= maximumOutcomes)
                return selected;

            int medianIndex = (ordered.Count - 1) / 2;
            for (int distance = 0; distance < ordered.Count; distance++)
            {
                int lower = medianIndex - distance;
                if (lower >= 0
                    && !selected.Any(candidate =>
                        isSameCandidate(candidate, ordered[lower])))
                {
                    selected.Add(ordered[lower]);
                    break;
                }
                int upper = medianIndex + distance;
                if (upper < ordered.Count
                    && upper != lower
                    && !selected.Any(candidate =>
                        isSameCandidate(candidate, ordered[upper])))
                {
                    selected.Add(ordered[upper]);
                    break;
                }
            }
            return selected;
        }

        /// <summary>
        /// Reuses the existing two-outcome packet budget as temporal stratified sampling.
        /// A fresh source observes the semantic extreme first. Repeated admissions on the same
        /// lease rotate through the remaining bounded categories. This exposes interior
        /// delayed-payoff options without adding a node to any layer or ledger.
        /// </summary>
        internal static List<T> SelectActiveOrderedMutationOptionPair<T>(
            IReadOnlyList<T> sampledOutcomes,
            int priorAdmissions)
        {
            if (sampledOutcomes.Count <= MaximumOrderedMutationContinuationsPerLineagePerPrune)
                return sampledOutcomes.ToList();

            int explorerCount = sampledOutcomes.Count - 1;
            int explorerRotation = Math.Max(0, priorAdmissions)
                / MaximumOrderedMutationContinuationsPerLineagePerPrune;
            int explorerIndex = 1 + explorerRotation % explorerCount;
            return [sampledOutcomes[0], sampledOutcomes[explorerIndex]];
        }

        private int CompareOrderedMutationContinuationPackets(
            OrderedMutationContinuationPacket left,
            OrderedMutationContinuationPacket right)
        {
            int comparison = left.PortfolioPriority.CompareTo(right.PortfolioPriority);
            if (comparison != 0)
                return comparison;
            comparison = CycleRegionSetupValue(right.Parent.Snapshot).CompareTo(
                CycleRegionSetupValue(left.Parent.Snapshot));
            if (comparison != 0)
                return comparison;
            comparison = left.Parent.RetentionRank.CompareTo(right.Parent.RetentionRank);
            if (comparison != 0)
                return comparison;
            comparison = BeamRankScore(right.Parent).CompareTo(BeamRankScore(left.Parent));
            if (comparison != 0)
                return comparison;
            comparison = left.Parent.ActionCount.CompareTo(right.Parent.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = left.Parent.StateKey.First.CompareTo(right.Parent.StateKey.First);
            if (comparison != 0)
                return comparison;
            comparison = left.Parent.StateKey.Second.CompareTo(right.Parent.StateKey.Second);
            if (comparison != 0)
                return comparison;
            comparison = right.HasPersistentMutationFamily.CompareTo(
                left.HasPersistentMutationFamily);
            if (comparison != 0)
                return comparison;
            // Source families are scheduled independently so that a non-leading persistent
            // choice cannot disappear before fairness is applied. Within the same class, keep
            // the family's best child ahead of fingerprint-only tie breaks; otherwise an
            // arbitrary source hash can consume this lane's first round while a materially
            // stronger continuation is deferred beyond the layer cap.
            comparison = CompareOrderedMutationRepresentatives(
                left.Candidates[0],
                right.Candidates[0]);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(left.RootKey, right.RootKey);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(
                left.InitialLeaseKey,
                right.InitialLeaseKey);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(left.LeaseKey, right.LeaseKey);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(
                left.ParentLineageKey,
                right.ParentLineageKey);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(
                left.SourceFamilyKey,
                right.SourceFamilyKey);
            if (comparison != 0)
                return comparison;
            comparison = left.Candidates.Count.CompareTo(right.Candidates.Count);
            if (comparison != 0)
                return comparison;
            for (int index = 0; index < left.Candidates.Count; index++)
            {
                SearchNode leftCandidate = left.Candidates[index];
                SearchNode rightCandidate = right.Candidates[index];
                comparison = CompareOrderedMutationFingerprints(
                    BuildOrderedMutationContinuationOptionKey(leftCandidate.Action!),
                    BuildOrderedMutationContinuationOptionKey(rightCandidate.Action!));
                if (comparison != 0)
                    return comparison;
                comparison = CompareOrderedMutationFingerprints(
                    leftCandidate.StateKey,
                    rightCandidate.StateKey);
                if (comparison != 0)
                    return comparison;
                comparison = CompareOrderedMutationFingerprints(
                    leftCandidate.Parent?.StateKey ?? default,
                    rightCandidate.Parent?.StateKey ?? default);
                if (comparison != 0)
                    return comparison;
                comparison = CompareOrderedMutationFingerprints(
                    leftCandidate.Parent?.OrderedMutationLineage?.SequenceKey ?? default,
                    rightCandidate.Parent?.OrderedMutationLineage?.SequenceKey ?? default);
                if (comparison != 0)
                    return comparison;
            }
            return 0;
        }

        private OrderedMutationContinuationPacket
            SelectOrderedMutationContinuationPacketForLease(
                IEnumerable<OrderedMutationContinuationPacket> packets,
                IComparer<OrderedMutationContinuationPacket> comparer)
        {
            List<OrderedMutationContinuationPacket> candidates = packets.ToList();
            OrderedMutationContinuationPacket qualityLeader = candidates
                .OrderBy(packet => packet, comparer)
                .First();
            SearchNode qualityOutcome = qualityLeader.Candidates[0];
            StateFingerprint qualityOption =
                BuildOrderedMutationContinuationOptionKey(qualityOutcome.Action!);
            StateFingerprint qualityParentState =
                qualityOutcome.Parent?.StateKey ?? default;
            SearchNode? explorerOutcome = null;
            OrderedMutationContinuationPacket? explorerPacket = null;
            bool explorerHasDifferentUniverse = false;
            bool explorerHasDifferentParent = false;
            bool explorerHasDifferentOption = false;
            bool explorerHasDifferentPile = false;
            bool explorerHasDifferentShuffle = false;
            long explorerParentDistance = long.MinValue;
            long explorerOutcomeDistance = long.MinValue;
            foreach (OrderedMutationContinuationPacket packet in candidates)
            {
                foreach (SearchNode candidate in packet.Candidates)
                {
                    if (ReferenceEquals(candidate, qualityOutcome))
                        continue;
                    SearchNode candidateParent = candidate.Parent ?? packet.Parent;
                    bool hasDifferentUniverse = packet.OptionUniverseKey
                        != qualityLeader.OptionUniverseKey;
                    bool hasDifferentParent = candidateParent.StateKey
                        != qualityParentState;
                    bool hasDifferentOption =
                        BuildOrderedMutationContinuationOptionKey(candidate.Action!)
                        != qualityOption;
                    bool hasDifferentPile = candidateParent.Snapshot.UnorderedPileKey
                        != qualityLeader.Parent.Snapshot.UnorderedPileKey;
                    bool hasDifferentShuffle =
                        candidateParent.Snapshot.ProjectedShuffleOrderKey
                        != qualityLeader.Parent.Snapshot.ProjectedShuffleOrderKey;
                    long parentDistance = OrderedMutationContinuationSemanticDistance(
                        qualityLeader.Parent,
                        candidateParent);
                    long outcomeDistance = OrderedMutationContinuationSemanticDistance(
                        qualityOutcome,
                        candidate);
                    if (explorerOutcome == null
                        || hasDifferentUniverse && !explorerHasDifferentUniverse
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent && !explorerHasDifferentParent
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption && !explorerHasDifferentOption
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption == explorerHasDifferentOption
                            && hasDifferentPile && !explorerHasDifferentPile
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption == explorerHasDifferentOption
                            && hasDifferentPile == explorerHasDifferentPile
                            && hasDifferentShuffle && !explorerHasDifferentShuffle
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption == explorerHasDifferentOption
                            && hasDifferentPile == explorerHasDifferentPile
                            && hasDifferentShuffle == explorerHasDifferentShuffle
                            && parentDistance > explorerParentDistance
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption == explorerHasDifferentOption
                            && hasDifferentPile == explorerHasDifferentPile
                            && hasDifferentShuffle == explorerHasDifferentShuffle
                            && parentDistance == explorerParentDistance
                            && outcomeDistance > explorerOutcomeDistance
                        || hasDifferentUniverse == explorerHasDifferentUniverse
                            && hasDifferentParent == explorerHasDifferentParent
                            && hasDifferentOption == explorerHasDifferentOption
                            && hasDifferentPile == explorerHasDifferentPile
                            && hasDifferentShuffle == explorerHasDifferentShuffle
                            && parentDistance == explorerParentDistance
                            && outcomeDistance == explorerOutcomeDistance
                            && (explorerPacket == null
                                || comparer.Compare(packet, explorerPacket) < 0
                                || comparer.Compare(packet, explorerPacket) == 0
                                    && IsBetterOrderedMutationRepresentative(
                                        candidate,
                                        explorerOutcome)))
                    {
                        explorerOutcome = candidate;
                        explorerPacket = packet;
                        explorerHasDifferentUniverse = hasDifferentUniverse;
                        explorerHasDifferentParent = hasDifferentParent;
                        explorerHasDifferentOption = hasDifferentOption;
                        explorerHasDifferentPile = hasDifferentPile;
                        explorerHasDifferentShuffle = hasDifferentShuffle;
                        explorerParentDistance = parentDistance;
                        explorerOutcomeDistance = outcomeDistance;
                    }
                }
            }
            if (explorerOutcome == null)
                return qualityLeader;
            return qualityLeader with
            {
                Candidates = [qualityOutcome, explorerOutcome],
            };
        }

        private List<OrderedMutationContinuationPacket>
            OrderOrderedMutationContinuationPacketsFairly(
                IEnumerable<OrderedMutationContinuationPacket> packets,
                IComparer<OrderedMutationContinuationPacket> comparer)
            => OrderOrderedMutationHierarchy(
                packets,
                packet => packet.RootKey,
                packet => packet.InitialLeaseKey,
                packet => packet.LeaseKey,
                key => _run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByLease.GetValueOrDefault(key),
                packet => packet.PortfolioPriority,
                current => current
                    .OrderByDescending(packet => packet.HasPersistentMutationFamily)
                    .ThenBy(packet => packet, comparer)
                    .ToList());

        private static OrderedMutationAdmissionClaimSource
            BuildOrderedMutationAdmissionClaimSource(
                OrderedMutationContinuationPacket packet,
                SearchNode candidate,
                OrderedMutationAdmissionClaimReason reason,
                bool crossedProofBoundary,
                bool continuationHandoff,
                bool requestsObservation)
            => new(
                new OrderedMutationAdmissionClaimKey(
                    packet.RootKey,
                    packet.InitialLeaseKey,
                    packet.LeaseKey,
                    packet.ParentLineageKey,
                    packet.Parent.StateKey,
                    packet.SourceFamilyKey,
                    BuildOrderedMutationContinuationOutcomeKey(candidate)),
                packet with { Candidates = [candidate] },
                candidate,
                reason,
                crossedProofBoundary,
                continuationHandoff,
                requestsObservation);

        private List<OrderedMutationAdmissionClaim>
            CoalesceOrderedMutationAdmissionClaims(
                IEnumerable<OrderedMutationAdmissionClaimSource> sources,
                IReadOnlySet<SearchNode> selected,
                IComparer<OrderedMutationContinuationPacket> packetComparer)
        {
            List<OrderedMutationAdmissionClaim> claims = [];
            foreach (IGrouping<OrderedMutationAdmissionClaimKey,
                         OrderedMutationAdmissionClaimSource> outcome in
                     sources.GroupBy(source => source.Key))
            {
                List<OrderedMutationAdmissionClaimSource> members = outcome.ToList();
                OrderedMutationAdmissionClaimSource representative = members
                    .OrderByDescending(source => selected.Contains(source.Candidate))
                    .ThenBy(source => source.Packet, packetComparer)
                    .ThenBy(source => source.Reason)
                    .First();
                HashSet<OrderedMutationAdmissionClaimReason> reasons = members
                    .Select(source => source.Reason)
                    .ToHashSet();
                claims.Add(new OrderedMutationAdmissionClaim(
                    outcome.Key,
                    representative.Packet with
                    {
                        Candidates = [representative.Candidate],
                    },
                    representative.Candidate,
                    reasons,
                    HandoffCrossedProofBoundary: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Handoff
                        && source.CrossedProofBoundary),
                    ObservationCrossedProofBoundary: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Observation
                        && source.CrossedProofBoundary),
                    CounterfactualContinuationHandoff: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Counterfactual
                        && source.ContinuationHandoff),
                    CounterfactualRequestsObservation: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Counterfactual
                        && source.RequestsObservation),
                    OrdinaryCrossedProofBoundary: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Ordinary
                        && source.CrossedProofBoundary),
                    OrdinaryContinuationHandoff: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Ordinary
                        && source.ContinuationHandoff),
                    OrdinaryRequestsObservation: members.Any(source =>
                        source.Reason == OrderedMutationAdmissionClaimReason.Ordinary
                        && source.RequestsObservation)));
            }
            return claims;
        }

        private List<OrderedMutationAdmissionWorkItem>
            OrderOrderedMutationAdmissionWorkFairly(
                IEnumerable<OrderedMutationAdmissionWorkItem> work,
                IComparer<OrderedMutationContinuationPacket> packetComparer)
            => OrderOrderedMutationAdmissionWorkFairlyCore(
                work,
                item => item.Parent.RootKey,
                item => item.Parent.InitialLeaseKey,
                item => item.Parent.LeaseKey,
                item => item.Parent.ParentLineageKey,
                item => item.Parent.ParentStateKey,
                key => _run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(key),
                key => _run.OrderedMutationAdmissionsByLease.GetValueOrDefault(key),
                item => item.PortfolioPriority,
                item => item.Reason,
                Comparer<OrderedMutationAdmissionWorkItem>.Create((left, right) =>
                {
                    int comparison = packetComparer.Compare(left.Packet, right.Packet);
                    if (comparison != 0)
                        return comparison;
                    comparison = left.Reason.CompareTo(right.Reason);
                    if (comparison != 0)
                        return comparison;
                    comparison = CompareOrderedMutationFingerprints(
                        left.Parent.ParentLineageKey,
                        right.Parent.ParentLineageKey);
                    if (comparison != 0)
                        return comparison;
                    comparison = CompareOrderedMutationFingerprints(
                        left.Parent.ParentStateKey,
                        right.Parent.ParentStateKey);
                    if (comparison != 0)
                        return comparison;
                    comparison = (left.Cohort == null).CompareTo(right.Cohort == null);
                    if (comparison != 0)
                        return comparison;
                    if (left.Claim is not { } leftClaim
                        || right.Claim is not { } rightClaim)
                    {
                        return 0;
                    }
                    comparison = CompareOrderedMutationFingerprints(
                        leftClaim.Key.SourceFamilyKey,
                        rightClaim.Key.SourceFamilyKey);
                    if (comparison != 0)
                        return comparison;
                    comparison = CompareOrderedMutationFingerprints(
                        leftClaim.Key.Outcome.OptionKey,
                        rightClaim.Key.Outcome.OptionKey);
                    return comparison != 0
                        ? comparison
                        : CompareOrderedMutationFingerprints(
                            leftClaim.Key.Outcome.ChildStateKey,
                            rightClaim.Key.Outcome.ChildStateKey);
                }));

        private static List<T> OrderOrderedMutationAdmissionWorkFairlyCore<T>(
            IEnumerable<T> work,
            Func<T, StateFingerprint> rootSelector,
            Func<T, StateFingerprint> initialSelector,
            Func<T, StateFingerprint> leaseSelector,
            Func<T, StateFingerprint> parentLineageSelector,
            Func<T, StateFingerprint> parentStateSelector,
            Func<StateFingerprint, int> rootAdmissions,
            Func<StateFingerprint, int> initialAdmissions,
            Func<StateFingerprint, int> leaseAdmissions,
            Func<T, int> prioritySelector,
            Func<T, OrderedMutationAdmissionClaimReason> reasonSelector,
            IComparer<T> comparer)
            => OrderOrderedMutationHierarchy(
                work,
                rootSelector,
                initialSelector,
                leaseSelector,
                rootAdmissions,
                initialAdmissions,
                leaseAdmissions,
                prioritySelector,
                current => RoundRobinOrderedMutationQueues(current
                    .GroupBy(item => (
                        Lineage: parentLineageSelector(item),
                        State: parentStateSelector(item)))
                    .OrderBy(group => group.Min(prioritySelector))
                    .ThenBy(group => group.Key.Lineage.First)
                    .ThenBy(group => group.Key.Lineage.Second)
                    .ThenBy(group => group.Key.State.First)
                    .ThenBy(group => group.Key.State.Second)
                    .Select(parent => RoundRobinOrderedMutationQueues(parent
                        .GroupBy(reasonSelector)
                        .OrderBy(group => group.Key)
                        .Select(group => group.OrderBy(item => item, comparer).ToList())
                        .ToList()))
                    .ToList()));

        private static List<T> OrderOrderedMutationAdmissionClaimsFairlyCore<T>(
            IEnumerable<T> claims,
            Func<T, StateFingerprint> rootSelector,
            Func<T, StateFingerprint> initialSelector,
            Func<T, StateFingerprint> leaseSelector,
            Func<StateFingerprint, int> rootAdmissions,
            Func<StateFingerprint, int> initialAdmissions,
            Func<StateFingerprint, int> leaseAdmissions,
            Func<T, OrderedMutationAdmissionClaimReason> reasonSelector,
            IComparer<T> comparer)
            => OrderOrderedMutationHierarchy(
                claims,
                rootSelector,
                initialSelector,
                leaseSelector,
                rootAdmissions,
                initialAdmissions,
                leaseAdmissions,
                claim => (int)reasonSelector(claim),
                current => RoundRobinOrderedMutationQueues(current
                    .GroupBy(reasonSelector)
                    .OrderBy(group => group.Key)
                    .Select(group => group.OrderBy(claim => claim, comparer).ToList())
                    .ToList()));

        private static bool TrySelectOrderedMutationAdmissionReason(
            IReadOnlySet<OrderedMutationAdmissionClaimReason> reasons,
            int handoffAdmissions,
            int observationAdmissions,
            int counterfactualAdmissions,
            int alternativeAdmissions,
            Func<OrderedMutationAdmissionClaimReason, bool> tryAdmit,
            out OrderedMutationAdmissionClaimReason selected)
        {
            foreach (OrderedMutationAdmissionClaimReason reason in reasons.Order())
            {
                bool available = reason switch
                {
                    OrderedMutationAdmissionClaimReason.Handoff =>
                        handoffAdmissions < MaximumOrderedMutationBoundaryHandoffAdmissions,
                    OrderedMutationAdmissionClaimReason.Observation =>
                        observationAdmissions < MaximumOrderedMutationObservationAdmissions,
                    OrderedMutationAdmissionClaimReason.Counterfactual =>
                        counterfactualAdmissions
                            < MaximumOrderedMutationCounterfactualSiblingAdmissions,
                    OrderedMutationAdmissionClaimReason.Alternative =>
                        alternativeAdmissions < MaximumOrderedMutationAlternativeAdmissions,
                    OrderedMutationAdmissionClaimReason.Ordinary => true,
                    _ => false,
                };
                if (!available || !tryAdmit(reason))
                    continue;
                selected = reason;
                return true;
            }
            selected = default;
            return false;
        }

        private static void IncrementOrderedMutationAdmissionReason(
            OrderedMutationAdmissionClaimReason reason,
            ref int handoffAdmissions,
            ref int observationAdmissions,
            ref int counterfactualAdmissions,
            ref int alternativeAdmissions)
        {
            switch (reason)
            {
                case OrderedMutationAdmissionClaimReason.Handoff:
                    handoffAdmissions++;
                    break;
                case OrderedMutationAdmissionClaimReason.Observation:
                    observationAdmissions++;
                    break;
                case OrderedMutationAdmissionClaimReason.Counterfactual:
                    counterfactualAdmissions++;
                    break;
                case OrderedMutationAdmissionClaimReason.Alternative:
                    alternativeAdmissions++;
                    break;
                case OrderedMutationAdmissionClaimReason.Ordinary:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
            }
        }

        private static void ApplyZeroWidthOrderedMutationObligations(
            OrderedMutationAdmissionClaim claim)
            => ApplyOrderedMutationAdmissionClaim(claim);

        private static void ApplyOrderedMutationAdmissionClaim(
            OrderedMutationAdmissionClaim claim)
        {
            SearchNode candidate = claim.Candidate;
            bool requestsObservation =
                claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Handoff)
                || claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Alternative)
                || claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Counterfactual)
                    && claim.CounterfactualRequestsObservation
                || claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Ordinary)
                    && claim.OrdinaryRequestsObservation;
            candidate.OrderedMutationContinuationHandoff |=
                claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Handoff)
                    && claim.HandoffCrossedProofBoundary
                || claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Counterfactual)
                    && claim.CounterfactualContinuationHandoff
                || claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Ordinary)
                    && (claim.OrdinaryContinuationHandoff
                        || claim.OrdinaryCrossedProofBoundary);
            if (requestsObservation)
                RequestOrderedMutationObservation(candidate);

            // The same semantic outcome can be both an ordinary continuation and a queued
            // obligation. Apply every reason's semantic effect at zero extra width, then settle
            // observation last so it consumes the old request while preserving a newly opened
            // bounded bridge through its remaining-step counter.
            if (claim.Reasons.Contains(OrderedMutationAdmissionClaimReason.Observation))
                SettleOrderedMutationObservationDebt(candidate, claim);
        }

        private static void SettleOrderedMutationObservationDebt(
            SearchNode candidate,
            OrderedMutationAdmissionClaim claim)
        {
            candidate.OrderedMutationContinuationHandoff |=
                claim.ObservationCrossedProofBoundary;
            candidate.OrderedMutationContinuationBridge =
                candidate.OrderedMutationObservationStepsRemaining > 0;
            candidate.OrderedMutationObservationRequested = false;
            candidate.OrderedMutationObservationDebtSettlementPending = true;
        }

        private OrderedMutationLateInitialPacingResult PaceLateOrderedMutationInitials(
            List<OrderedMutationContinuationPacket> continuationPackets,
            List<OrderedMutationContinuationPacket> counterfactualPackets,
            HashSet<SearchNode> selectedSet,
            IDictionary<StateFingerprint, int> reservedAdmissionsByRootLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByInitialLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByLease,
            int reservedRunAdmissions,
            IComparer<OrderedMutationContinuationPacket> packetComparer,
            IComparer<OrderedMutationContinuationPacket> counterfactualComparer)
        {
            Dictionary<SearchNode, OrderedMutationContinuationPacketOutcome>?
                eligibleOutcomes = null;
            bool hasLateOutcome = false;
            foreach (OrderedMutationContinuationPacket sourcePacket in
                     continuationPackets.Concat(counterfactualPackets))
            {
                foreach (SearchNode candidate in sourcePacket.Candidates)
                {
                    bool isLateOutcome = IsLateOrderedMutationInitial(candidate);
                    hasLateOutcome |= isLateOutcome;
                    if (!isLateOutcome
                        || selectedSet.Contains(candidate)
                        || !CanRetainOrderedMutationLease(_run, candidate))
                    {
                        continue;
                    }

                    OrderedMutationContinuationPacket packet =
                        RebuildOrderedMutationContinuationPacketForOutcome(
                            sourcePacket,
                            candidate);
                    var outcome = new OrderedMutationContinuationPacketOutcome(
                        packet,
                        candidate);
                    eligibleOutcomes ??= new(ReferenceEqualityComparer.Instance);
                    if (!eligibleOutcomes.TryGetValue(
                            candidate,
                            out OrderedMutationContinuationPacketOutcome current)
                        || CompareOrderedMutationPacingOutcomes(
                            outcome,
                            current,
                            packetComparer) < 0)
                    {
                        eligibleOutcomes[candidate] = outcome;
                    }
                }
            }

            if (!hasLateOutcome)
            {
                return BuildNoLateOrderedMutationPacingResult(
                    continuationPackets,
                    counterfactualPackets);
            }
            eligibleOutcomes ??= new(ReferenceEqualityComparer.Instance);
            HashSet<SearchNode> pacedOutcomes = new(ReferenceEqualityComparer.Instance);
            HashSet<SearchNode> explorerOutcomes = new(ReferenceEqualityComparer.Instance);
            Dictionary<SearchNode, OrderedMutationContinuationPacket> pacedPackets =
                new(ReferenceEqualityComparer.Instance);
            foreach (IGrouping<StateFingerprint, OrderedMutationContinuationPacketOutcome> group in
                     eligibleOutcomes.Values
                         .GroupBy(outcome => outcome.Packet.InitialLeaseKey)
                         .OrderBy(group => group.Key.First)
                         .ThenBy(group => group.Key.Second))
            {
                List<OrderedMutationContinuationPacketOutcome> selectedOutcomes =
                    SelectDeterministicQualityAndExplorer(
                        group.ToList(),
                        (left, right) => CompareOrderedMutationPacingOutcomes(
                            left,
                            right,
                            packetComparer),
                        (left, right) => OrderedMutationContinuationSemanticDistance(
                            left.Candidate,
                            right.Candidate),
                        outcomes => CanReserveOrderedMutationPacingOutcomes(
                            outcomes,
                            reservedAdmissionsByRootLease,
                            reservedAdmissionsByInitialLease,
                            reservedAdmissionsByLease,
                            reservedRunAdmissions),
                        (left, right) => ReferenceEquals(
                            left.Candidate,
                            right.Candidate));
                for (int index = 0; index < selectedOutcomes.Count; index++)
                {
                    OrderedMutationContinuationPacketOutcome outcome =
                        selectedOutcomes[index];
                    pacedOutcomes.Add(outcome.Candidate);
                    pacedPackets[outcome.Candidate] = outcome.Packet;
                    if (index == 1)
                        explorerOutcomes.Add(outcome.Candidate);
                }
            }

            List<OrderedMutationContinuationPacket> pacedContinuationPackets =
                RebuildLateOrderedMutationPacketList(
                    continuationPackets,
                    pacedOutcomes,
                    packetComparer);
            HashSet<SearchNode> mainQueueOutcomes = new(
                pacedContinuationPackets.SelectMany(packet => packet.Candidates),
                ReferenceEqualityComparer.Instance);
            foreach ((SearchNode candidate, OrderedMutationContinuationPacket packet) in
                     pacedPackets)
            {
                // A handoff-only outcome can exist only in the counterfactual view. Keep every
                // globally selected late outcome in the ordinary fair queue as well, so the
                // global counterfactual cap cannot make an Initial's reserved seats disappear.
                if (mainQueueOutcomes.Add(candidate))
                    pacedContinuationPackets.Add(packet);
            }
            pacedContinuationPackets = OrderOrderedMutationContinuationPacketsFairly(
                pacedContinuationPackets,
                packetComparer);
            List<OrderedMutationContinuationPacket> pacedCounterfactualPackets =
                RebuildLateOrderedMutationPacketList(
                        counterfactualPackets,
                        pacedOutcomes,
                        counterfactualComparer)
                    .OrderBy(packet => packet, counterfactualComparer)
                    .ToList();
            return new OrderedMutationLateInitialPacingResult(
                pacedContinuationPackets,
                pacedCounterfactualPackets,
                pacedOutcomes,
                explorerOutcomes);
        }

        private static OrderedMutationLateInitialPacingResult
            BuildNoLateOrderedMutationPacingResult(
                List<OrderedMutationContinuationPacket> continuationPackets,
                List<OrderedMutationContinuationPacket> counterfactualPackets)
            => new(
                continuationPackets,
                counterfactualPackets,
                new HashSet<SearchNode>(ReferenceEqualityComparer.Instance),
                new HashSet<SearchNode>(ReferenceEqualityComparer.Instance));

        internal static bool ReusesOrderedMutationPacketListsWithoutLateOutcomesForTesting()
        {
            List<OrderedMutationContinuationPacket> continuationPackets = [];
            List<OrderedMutationContinuationPacket> counterfactualPackets = [];
            OrderedMutationLateInitialPacingResult result =
                BuildNoLateOrderedMutationPacingResult(
                    continuationPackets,
                    counterfactualPackets);
            return ReferenceEquals(result.ContinuationPackets, continuationPackets)
                && ReferenceEquals(result.CounterfactualPackets, counterfactualPackets)
                && result.PacedOutcomes.Count == 0
                && result.ExplorerOutcomes.Count == 0;
        }

        private bool IsLateOrderedMutationInitial(SearchNode candidate)
        {
            OrderedMutationRetentionLease lease =
                BuildOrderedMutationContinuationAdmissionLease(candidate);
            return IsLateOrderedMutationInitial(
                _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(
                    lease.InitialKey),
                lease);
        }

        internal static bool IsLateOrderedMutationInitial(
            int consumedInitialAdmissions,
            OrderedMutationRetentionLease lease)
            => consumedInitialAdmissions
                >= OrderedMutationInitialAdmissionLimit(lease)
                    - OrderedMutationRetentionLease.MaximumProtectedAdmissions;

        private OrderedMutationContinuationPacket
            RebuildOrderedMutationContinuationPacketForOutcome(
                OrderedMutationContinuationPacket sourcePacket,
                SearchNode candidate)
        {
            OrderedMutationRetentionLease lease = candidate.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException(
                    "有序变异 late pacing outcome 缺少 lease。");
            SearchNode parent = candidate.Parent
                ?? throw new InvalidOperationException(
                    "有序变异 late pacing outcome 缺少 parent。");
            PlanAction action = candidate.Action
                ?? throw new InvalidOperationException(
                    "有序变异 late pacing outcome 缺少 action。");
            bool hasPersistentMutation =
                TryBuildOrderedMutationContinuationSourceFamilyKey(
                    action,
                    out StateFingerprint sourceFamilyKey);
            return new OrderedMutationContinuationPacket(
                lease.RootKey,
                lease.InitialKey,
                lease.Key,
                parent.OrderedMutationLineage?.SequenceKey ?? default,
                sourceFamilyKey,
                sourcePacket.OptionUniverseKey,
                hasPersistentMutation,
                sourcePacket.HasSelectedSibling,
                sourcePacket.HasRotatedInteriorOption,
                lease.PortfolioPriority,
                parent,
                [candidate]);
        }

        private int CompareOrderedMutationPacingOutcomes(
            OrderedMutationContinuationPacketOutcome left,
            OrderedMutationContinuationPacketOutcome right,
            IComparer<OrderedMutationContinuationPacket> packetComparer)
        {
            int comparison = CompareOrderedMutationRepresentatives(
                left.Candidate,
                right.Candidate);
            if (comparison != 0)
                return comparison;
            comparison = packetComparer.Compare(left.Packet, right.Packet);
            if (comparison != 0)
                return comparison;
            comparison = CompareOrderedMutationFingerprints(
                left.Packet.OptionUniverseKey,
                right.Packet.OptionUniverseKey);
            return comparison != 0
                ? comparison
                : right.Packet.HasSelectedSibling.CompareTo(
                    left.Packet.HasSelectedSibling);
        }

        private bool CanReserveOrderedMutationPacingOutcomes(
            IReadOnlyList<OrderedMutationContinuationPacketOutcome> outcomes,
            IDictionary<StateFingerprint, int> reservedAdmissionsByRootLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByInitialLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByLease,
            int reservedRunAdmissions)
        {
            if (outcomes.Count == 0
                || outcomes.Count > MaximumOrderedMutationAdmissionsPerInitialPerPrune
                || outcomes.Select(outcome => outcome.Packet.InitialLeaseKey).Distinct().Count()
                    != 1)
            {
                return false;
            }
            int count = outcomes.Count;
            if (_run.OrderedMutationPortfolioNodesConsumed
                    > MaximumOrderedMutationRunAdmissions
                        - reservedRunAdmissions
                        - count)
            {
                return false;
            }

            OrderedMutationRetentionLease first =
                BuildOrderedMutationContinuationAdmissionLease(
                    outcomes[0].Candidate);
            OrderedMutationRetentionLease second = count == 2
                ? BuildOrderedMutationContinuationAdmissionLease(
                    outcomes[1].Candidate)
                : first;
            if (!HasOrderedMutationPacingRootCapacity(
                    first.RootKey,
                    count == 2 && second.RootKey == first.RootKey
                        ? Math.Min(
                            OrderedMutationRootAdmissionLimit(first),
                            OrderedMutationRootAdmissionLimit(second))
                        : OrderedMutationRootAdmissionLimit(first),
                    1 + (count == 2 && second.RootKey == first.RootKey ? 1 : 0),
                    reservedAdmissionsByRootLease)
                || count == 2
                    && second.RootKey != first.RootKey
                    && !HasOrderedMutationPacingRootCapacity(
                        second.RootKey,
                        OrderedMutationRootAdmissionLimit(second),
                        1,
                        reservedAdmissionsByRootLease))
            {
                return false;
            }

            StateFingerprint initialKey = first.InitialKey;
            int consumedInitialAdmissions =
                _run.OrderedMutationAdmissionsByInitialLease.GetValueOrDefault(initialKey);
            int reservedInitialAdmissions =
                OrderedMutationReservationCount(
                    reservedAdmissionsByInitialLease,
                    initialKey);
            int initialAdmissionLimit = count == 2
                ? Math.Min(
                    OrderedMutationInitialAdmissionLimit(first),
                    OrderedMutationInitialAdmissionLimit(second))
                : OrderedMutationInitialAdmissionLimit(first);
            if (consumedInitialAdmissions
                    > initialAdmissionLimit
                        - reservedInitialAdmissions
                        - count
                || consumedInitialAdmissions
                    >= initialAdmissionLimit
                        - OrderedMutationRetentionLease.MaximumProtectedAdmissions
                    && reservedInitialAdmissions
                        > MaximumOrderedMutationAdmissionsPerInitialPerPrune - count)
            {
                return false;
            }

            return HasOrderedMutationPacingLeaseCapacity(
                    first.Key,
                    1 + (count == 2 && second.Key == first.Key ? 1 : 0),
                    reservedAdmissionsByLease)
                && (count != 2
                    || second.Key == first.Key
                    || HasOrderedMutationPacingLeaseCapacity(
                        second.Key,
                        1,
                        reservedAdmissionsByLease));
        }

        private bool HasOrderedMutationPacingRootCapacity(
            StateFingerprint rootKey,
            int admissionLimit,
            int requested,
            IDictionary<StateFingerprint, int> reservedAdmissionsByRootLease)
            => _run.OrderedMutationAdmissionsByRootLease.GetValueOrDefault(rootKey)
                <= admissionLimit
                    - OrderedMutationReservationCount(
                        reservedAdmissionsByRootLease,
                        rootKey)
                    - requested;

        private bool HasOrderedMutationPacingLeaseCapacity(
            StateFingerprint leaseKey,
            int requested,
            IDictionary<StateFingerprint, int> reservedAdmissionsByLease)
            => _run.OrderedMutationAdmissionsByLease.GetValueOrDefault(leaseKey)
                <= OrderedMutationRetentionLease.MaximumProtectedAdmissions
                    - OrderedMutationReservationCount(
                        reservedAdmissionsByLease,
                        leaseKey)
                    - requested;

        private static int OrderedMutationReservationCount(
            IDictionary<StateFingerprint, int> reservations,
            StateFingerprint key)
            => reservations.TryGetValue(key, out int count) ? count : 0;

        private List<OrderedMutationContinuationPacket>
            RebuildLateOrderedMutationPacketList(
                List<OrderedMutationContinuationPacket> packets,
                HashSet<SearchNode> pacedOutcomes,
                IComparer<OrderedMutationContinuationPacket> comparer)
        {
            bool hasLateOutcome = false;
            foreach (OrderedMutationContinuationPacket packet in packets)
            {
                foreach (SearchNode candidate in packet.Candidates)
                {
                    if (!IsLateOrderedMutationInitial(candidate))
                        continue;
                    hasLateOutcome = true;
                    break;
                }
                if (hasLateOutcome)
                    break;
            }
            if (!hasLateOutcome)
                return packets;

            List<OrderedMutationContinuationPacket> retained = [];
            Dictionary<SearchNode, OrderedMutationContinuationPacket> rebuilt =
                new(ReferenceEqualityComparer.Instance);
            foreach (OrderedMutationContinuationPacket packet in packets)
            {
                bool packetHasLateOutcome = false;
                foreach (SearchNode candidate in packet.Candidates)
                {
                    if (!IsLateOrderedMutationInitial(candidate))
                        continue;
                    packetHasLateOutcome = true;
                    break;
                }
                if (!packetHasLateOutcome)
                {
                    retained.Add(packet);
                    continue;
                }
                foreach (SearchNode candidate in packet.Candidates)
                {
                    if (IsLateOrderedMutationInitial(candidate)
                        && !pacedOutcomes.Contains(candidate))
                    {
                        continue;
                    }
                    OrderedMutationContinuationPacket singleton =
                        RebuildOrderedMutationContinuationPacketForOutcome(
                            packet,
                            candidate);
                    if (!rebuilt.TryGetValue(
                            candidate,
                            out OrderedMutationContinuationPacket? current)
                        || comparer.Compare(singleton, current) < 0
                        || comparer.Compare(singleton, current) == 0
                            && CompareOrderedMutationFingerprints(
                                singleton.OptionUniverseKey,
                                current.OptionUniverseKey) < 0)
                    {
                        rebuilt[candidate] = singleton;
                    }
                }
            }
            retained.AddRange(rebuilt.Values);
            return retained;
        }

        internal static List<T> SelectDeterministicQualityAndExplorer<T>(
            IReadOnlyList<T> candidates,
            Comparison<T> qualityComparison,
            Func<T, T, long> semanticDistance,
            Func<IReadOnlyList<T>, bool> canSelect,
            Func<T, T, bool> isSameCandidate)
        {
            List<T> ordered = candidates
                .OrderBy(candidate => candidate, Comparer<T>.Create(qualityComparison))
                .ToList();
            int leaderIndex = ordered.FindIndex(candidate => canSelect([candidate]));
            if (leaderIndex < 0)
                return [];

            T leader = ordered[leaderIndex];
            T? explorer = default;
            bool hasExplorer = false;
            long explorerDistance = long.MinValue;
            foreach (T candidate in ordered)
            {
                if (isSameCandidate(candidate, leader)
                    || !canSelect([leader, candidate]))
                {
                    continue;
                }
                long distance = semanticDistance(leader, candidate);
                if (!hasExplorer
                    || distance > explorerDistance
                    || distance == explorerDistance
                        && qualityComparison(candidate, explorer!) < 0)
                {
                    explorer = candidate;
                    hasExplorer = true;
                    explorerDistance = distance;
                }
            }
            return hasExplorer ? [leader, explorer!] : [leader];
        }

        private static long OrderedMutationContinuationSemanticDistance(
            SearchNode left,
            SearchNode right)
        {
            SimulationSnapshot first = left.Snapshot;
            SimulationSnapshot second = right.Snapshot;
            return AbsoluteDifference(
                    CycleRegionSetupValue(first),
                    CycleRegionSetupValue(second))
                + AbsoluteDifference(first.PersistentBuffValue, second.PersistentBuffValue)
                + AbsoluteDifference(
                    first.StrategicEffects.DamagePotential,
                    second.StrategicEffects.DamagePotential)
                + AbsoluteDifference(
                    first.StrategicEffects.PreventionPotential,
                    second.StrategicEffects.PreventionPotential)
                + AbsoluteDifference(
                    first.StrategicEffects.ResourcePotential,
                    second.StrategicEffects.ResourcePotential)
                + AbsoluteDifference(
                    first.StrategicEffects.CardAccessPotential,
                    second.StrategicEffects.CardAccessPotential)
                + AbsoluteDifference(
                    first.StrategicEffects.ScalingPotential,
                    second.StrategicEffects.ScalingPotential)
                + AbsoluteDifference(first.LatentSetupValue, second.LatentSetupValue)
                + AbsoluteDifference(first.FutureResourceValue, second.FutureResourceValue)
                + AbsoluteDifference(first.LongTermResourceValue, second.LongTermResourceValue)
                + AbsoluteDifference(first.ReplayPotentialValue, second.ReplayPotentialValue)
                + AbsoluteDifference(first.RetainedAttackValue, second.RetainedAttackValue)
                + AbsoluteDifference(first.OffensiveProgressValue, second.OffensiveProgressValue)
                + AbsoluteDifference(first.DelayedDamageValue, second.DelayedDamageValue)
                + AbsoluteDifference(first.ReactiveDamageValue, second.ReactiveDamageValue)
                + AbsoluteDifference(first.LiveDeckClutter, second.LiveDeckClutter)
                + AbsoluteDifference(first.LiveDeckSize, second.LiveDeckSize)
                + AbsoluteDifference(first.Energy, second.Energy)
                + AbsoluteDifference(first.Stars, second.Stars)
                + AbsoluteDifference(first.HandCount, second.HandCount)
                + AbsoluteDifference(first.ReachableHandValue, second.ReachableHandValue);
        }

        private static long AbsoluteDifference(long left, long right)
            => Math.Abs(left - right);

        private static StateFingerprint BuildOrderedMutationContinuationOptionKey(
            PlanAction action)
        {
            StateFingerprintBuilder key = new();
            bool ignored = false;
            AppendOrderedMutationContinuationActionKey(
                ref key,
                action,
                omitPersistentTargets: false,
                omitMutableSourceState: false,
                ref ignored);
            return key.Finish();
        }

        private static OrderedMutationContinuationOutcomeKey
            BuildOrderedMutationContinuationOutcomeKey(SearchNode candidate)
            => new(
                BuildOrderedMutationContinuationOptionKey(
                    candidate.Action
                    ?? throw new InvalidOperationException(
                        "有序变异 outcome key 缺少 action。")),
                candidate.StateKey);

        private static StateFingerprint BuildOrderedMutationContinuationOptionUniverseKey(
            IEnumerable<SearchNode> candidates)
        {
            StateFingerprint[] options = candidates
                .Select(candidate => BuildOrderedMutationContinuationOptionKey(
                    candidate.Action!))
                .Distinct()
                .OrderBy(option => option.First)
                .ThenBy(option => option.Second)
                .ToArray();
            StateFingerprintBuilder key = new();
            key.Add('O');
            key.Add(options.Length);
            foreach (StateFingerprint option in options)
            {
                key.Add(option.First);
                key.Add(option.Second);
            }
            return key.Finish();
        }

        private static bool TryBuildOrderedMutationContinuationSourceFamilyKey(
            PlanAction action,
            out StateFingerprint family)
        {
            StateFingerprintBuilder key = new();
            bool hasPersistentMutation = false;
            AppendOrderedMutationContinuationActionKey(
                ref key,
                action,
                omitPersistentTargets: true,
                omitMutableSourceState: false,
                ref hasPersistentMutation);
            family = key.Finish();
            return hasPersistentMutation;
        }

        private static bool TryBuildOrderedMutationRecurrenceSourceFamilyKey(
            PlanAction action,
            out StateFingerprint family)
        {
            StateFingerprintBuilder key = new();
            bool hasPersistentMutation = false;
            AppendOrderedMutationContinuationActionKey(
                ref key,
                action,
                omitPersistentTargets: true,
                omitMutableSourceState: true,
                ref hasPersistentMutation);
            family = key.Finish();
            return hasPersistentMutation;
        }

        private static void AppendOrderedMutationContinuationActionKey(
            ref StateFingerprintBuilder key,
            PlanAction action,
            bool omitPersistentTargets,
            bool omitMutableSourceState,
            ref bool hasPersistentMutation)
        {
            key.Add((int)action.Kind);
            key.Add(action.CardId);
            if (!omitMutableSourceState)
                key.Add(action.CardStateKey);
            else
                key.Add('R');
            key.Add(action.TargetCombatId ?? uint.MaxValue);
            key.Add(action.PotionId);
            key.Add(action.ReplayCount);
            key.Add(action.EndsPlayerTurn);
            key.Add(action.NestedChoicesBeforePrimary);

            IReadOnlyList<PlanCardChoice> nested = action.NestedChoices ?? [];
            key.Add(nested.Count);
            for (int index = 0; index < action.NestedChoicesBeforePrimary; index++)
            {
                AppendOrderedMutationContinuationChoiceKey(
                    ref key,
                    nested[index],
                    omitPersistentTargets,
                    ref hasPersistentMutation);
            }
            AppendOrderedMutationContinuationChoiceKey(
                ref key,
                action.Choice,
                omitPersistentTargets,
                ref hasPersistentMutation);
            for (int index = action.NestedChoicesBeforePrimary; index < nested.Count; index++)
            {
                AppendOrderedMutationContinuationChoiceKey(
                    ref key,
                    nested[index],
                    omitPersistentTargets,
                    ref hasPersistentMutation);
            }

            IReadOnlyList<PlanCardChoice> turnStartChoices = action.TurnStartChoices ?? [];
            key.Add(turnStartChoices.Count);
            foreach (PlanCardChoice choice in turnStartChoices)
            {
                AppendOrderedMutationContinuationChoiceKey(
                    ref key,
                    choice,
                    omitPersistentTargets,
                    ref hasPersistentMutation);
            }
        }

        private static void AppendOrderedMutationContinuationChoiceKey(
            ref StateFingerprintBuilder key,
            PlanCardChoice? choice,
            bool omitPersistentTargets,
            ref bool hasPersistentMutation)
        {
            if (choice == null)
            {
                key.Add(-1);
                return;
            }

            bool persistentMutation = IsOrderedPersistentMutationEffect(choice.Effect);
            hasPersistentMutation |= persistentMutation;
            key.Add((int)choice.Effect);
            key.Add((int)choice.SourcePile);
            key.Add(choice.SourceId);
            key.Add(choice.ContextId);
            key.Add((int)choice.Timing);
            key.Add(choice.Cards.Count);
            if (persistentMutation && omitPersistentTargets)
                return;
            foreach (PlanCardToken card in choice.Cards)
            {
                key.Add(card.CardId);
                key.Add(card.UpgradeLevel);
                key.Add(card.StateKey);
            }
        }

        private bool TryAdmitOrderedMutationContinuationPacket(
            OrderedMutationContinuationPacket packet,
            List<SearchNode> selected,
            HashSet<SearchNode> selectedSet,
            IDictionary<StateFingerprint, int> reservedAdmissionsByRootLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByInitialLease,
            IDictionary<StateFingerprint, int> reservedAdmissionsByLease,
            Dictionary<OrderedMutationContinuationBudgetKey, int>
                continuationAdmissionsByLineage,
            ref int reservedRunAdmissions)
        {
            int count = packet.Candidates.Count;
            if (count == 0
                || count > MaximumOrderedMutationContinuationsPerLineagePerPrune
                || packet.Candidates.Any(HasPaidOrderedMutationAdmission)
                || packet.Candidates.Any(candidate =>
                    !CanRetainOrderedMutationLease(_run, candidate))
                || packet.Candidates.Any(candidate =>
                    candidate.OrderedMutationRetentionLease is not { } lease
                    || lease.RootKey != packet.RootKey
                    || lease.InitialKey != packet.InitialLeaseKey
                    || lease.Key != packet.LeaseKey))
            {
                return false;
            }

            OrderedMutationRetentionLease[] admissionLeases = packet.Candidates
                .Select(BuildOrderedMutationContinuationAdmissionLease)
                .ToArray();
            OrderedMutationContinuationBudgetKey[] budgetKeys = packet.Candidates
                .Select(candidate => new OrderedMutationContinuationBudgetKey(
                    packet.RootKey,
                    packet.InitialLeaseKey,
                    packet.LeaseKey,
                    candidate.Parent?.OrderedMutationLineage?.SequenceKey ?? default,
                    candidate.Parent?.StateKey ?? default,
                    packet.SourceFamilyKey))
                .ToArray();
            if (budgetKeys.GroupBy(key => key).Any(group =>
                    continuationAdmissionsByLineage.GetValueOrDefault(group.Key)
                        > MaximumOrderedMutationContinuationsPerLineagePerPrune
                            - group.Count())
                )
            {
                return false;
            }
            if (admissionLeases.Any(lease =>
                    !HasRemainingOrderedMutationLeaseBudget(_run, lease)))
            {
                return false;
            }
            if (!TryReserveOrderedMutationAdmissions(
                    _run,
                    reservedAdmissionsByRootLease,
                    reservedAdmissionsByInitialLease,
                    reservedAdmissionsByLease,
                    ref reservedRunAdmissions,
                    admissionLeases))
            {
                return false;
            }
            foreach (IGrouping<OrderedMutationContinuationBudgetKey,
                         OrderedMutationContinuationBudgetKey> group in budgetKeys.GroupBy(
                         key => key))
            {
                continuationAdmissionsByLineage[group.Key] = checked(
                    continuationAdmissionsByLineage.GetValueOrDefault(group.Key)
                        + group.Count());
            }
            for (int index = 0; index < packet.Candidates.Count; index++)
            {
                SearchNode candidate = packet.Candidates[index];
                candidate.OrderedMutationRetentionLease = admissionLeases[index];
                candidate.OrderedMutationLeaseTransitionPending = false;
                candidate.OrderedMutationAdmissionPending = true;
                if (selectedSet.Add(candidate))
                    selected.Add(candidate);
                else
                    _run.PendingOrderedMutationOrdinaryFallbackNodes.Add(candidate);
            }
            return true;
        }

        private OrderedMutationRetentionLease
            BuildOrderedMutationContinuationAdmissionLease(SearchNode candidate)
        {
            OrderedMutationRetentionLease inherited =
                candidate.OrderedMutationRetentionLease
                ?? throw new InvalidOperationException(
                    "有序变异 continuation 派生时缺少父 lease。");
            if (!candidate.OrderedMutationLeaseTransitionPending)
                return inherited;
            OrderedMutationLineage? parentLineage =
                candidate.Parent?.OrderedMutationLineage;
            OrderedMutationLineage? childLineage = candidate.OrderedMutationLineage;
            bool boundaryCrossed = inherited.BoundaryReached;
            StateFingerprint transitionedKey = inherited.Key;
            bool transitionOccurred = false;
            if (boundaryCrossed)
            {
                OrderedMutationLineage? completedBoundary =
                    candidate.OrderedMutationBoundaryLineage?.CompletedLineage;
                StateFingerprint? preBoundaryMutationSequence =
                    HasOrderedMutationLineageAdvanced(parentLineage, completedBoundary)
                        ? completedBoundary!.SequenceKey
                        : null;
                // A post-boundary lineage starts from an empty baseline, so any materialized
                // value is a real mutation after the checkpoint.
                transitionedKey = BuildOrderedMutationBoundaryTransitionKey(
                    transitionedKey,
                    preBoundaryMutationSequence,
                    BuildOrderedMutationCheckpointStateKey(candidate),
                    childLineage?.SequenceKey);
                transitionOccurred = true;
            }
            else if (HasOrderedMutationLineageAdvanced(parentLineage, childLineage))
            {
                transitionedKey = BuildOrderedMutationDerivedKey(
                    transitionedKey,
                    childLineage!.SequenceKey);
                transitionOccurred = true;
            }
            OrderedMutationRetentionLease committed =
                CommitOrderedMutationLeaseTransition(
                inherited,
                candidate.OrderedMutationLeaseTransitionPending,
                transitionedKey,
                transitionOccurred,
                candidate.Turn,
                candidate.Snapshot.ShufflesCrossed);
            return committed;
        }

        private static bool HasOrderedMutationLineageAdvanced(
            OrderedMutationLineage? parent,
            OrderedMutationLineage? child)
            => child is { } current
                && (parent is not { } prior
                    || current.Turn != prior.Turn
                    || current.ChoiceCount > prior.ChoiceCount);

        private static OrderedMutationRetentionLease CommitOrderedMutationLeaseTransition(
            OrderedMutationRetentionLease inherited,
            bool transitionPending,
            StateFingerprint transitionedKey,
            bool transitionOccurred,
            int turn,
            int shufflesCrossed)
        {
            if (!transitionPending)
                return inherited;
            if (!transitionOccurred)
                return inherited;
            return new OrderedMutationRetentionLease(
                inherited.RootKey,
                inherited.InitialKey,
                transitionedKey,
                turn,
                shufflesCrossed,
                inherited.PortfolioPriority,
                BoundaryReached: false)
            {
                ProgressTailEligible = inherited.ProgressTailEligible,
            };
        }

        private OrderedMutationRetentionLease CreateOrderedMutationLease(
            SearchNode node,
            OrderedMutationOutcomeFamilySignature family,
            StateFingerprint sequenceKey)
        {
            StateFingerprint rootKey = BuildOrderedMutationRootKey(family);
            StateFingerprint initialKey = BuildOrderedMutationInitialKey(rootKey, sequenceKey);
            StateFingerprint leaseKey = initialKey;
            if (node.OrderedMutationBoundaryLineage != null
                && node.OrderedMutationLineage is { } postBoundaryLineage)
            {
                leaseKey = BuildOrderedMutationDerivedKey(
                    leaseKey,
                    postBoundaryLineage.SequenceKey);
            }
            return new OrderedMutationRetentionLease(
                rootKey,
                initialKey,
                leaseKey,
                node.Turn,
                node.Snapshot.ShufflesCrossed,
                PortfolioPriority: int.MaxValue,
                BoundaryReached: false);
        }

        private static StateFingerprint BuildOrderedMutationRootKey(
            OrderedMutationOutcomeFamilySignature family)
        {
            StateFingerprintBuilder key = new();
            key.Add('R');
            key.Add(family.Turn);
            key.Add(family.PotionCount);
            key.Add(family.ChoiceCount);
            key.Add(family.EffectMultisetKey.First);
            key.Add(family.EffectMultisetKey.Second);
            key.Add(family.UnorderedOutcomeKey.First);
            key.Add(family.UnorderedOutcomeKey.Second);
            AppendOrderedMutationBoundaryStamp(ref key, family.Boundary);
            return key.Finish();
        }

        private static void AppendOrderedMutationBoundaryStamp(
            ref StateFingerprintBuilder key,
            OrderedMutationBoundaryStamp? boundary)
        {
            key.Add(boundary.HasValue);
            if (boundary is not { } stamp)
                return;
            key.Add(stamp.FromTurn);
            key.Add(stamp.FromShufflesCrossed);
            key.Add(stamp.ToTurn);
            key.Add(stamp.ToShufflesCrossed);
        }

        private static StateFingerprint BuildOrderedMutationInitialKey(
            StateFingerprint rootKey,
            StateFingerprint sequenceKey)
        {
            StateFingerprintBuilder key = new();
            key.Add('L');
            key.Add(rootKey.First);
            key.Add(rootKey.Second);
            key.Add(sequenceKey.First);
            key.Add(sequenceKey.Second);
            return key.Finish();
        }

        private static void VerifySharedNaturalOrderedMutationServiceForTesting()
        {
            int[] candidates = Enumerable.Range(0, 49).ToArray();
            HashSet<int> ordinaryOwners = Enumerable.Range(0, 48).ToHashSet();
            HashSet<int> paid = [];
            HashSet<int> cohortOwned = [];
            int[] shared = SelectOrderedMutationClaimsForSharedScheduling(
                    candidates, candidate => candidate, paid, cohortOwned)
                .ToArray();
            if (shared.Length != 49 || ordinaryOwners.Count != 48)
                throw new InvalidOperationException("未收费的自然候选没有进入共享服务队列。");
            for (int scope = 0; scope < 2; scope++)
            {
                StateFingerprint Root(int candidate)
                    => new(scope == 0 && candidate == 48 ? 2UL : 1UL, 1);
                StateFingerprint Initial(int candidate)
                    => new(candidate == 48 ? 2UL : 1UL, 2);
                List<int> ordered = OrderOrderedMutationAdmissionWorkFairlyCore(
                    shared,
                    Root,
                    Initial,
                    candidate => Initial(candidate),
                    _ => default,
                    _ => default,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    _ => OrderedMutationAdmissionClaimReason.Ordinary,
                    Comparer<int>.Default);
                int[] served = ordered.Take(MaximumOrderedMutationLayerAdmissions).ToArray();
                if (served.Length != 48 || !served.Contains(48)
                    || !candidates.SequenceEqual(Enumerable.Range(0, 49)))
                {
                    throw new InvalidOperationException(
                        "自然候选预占了共享服务，饿死另一 root/initial 的额外路线。");
                }
            }
            paid.Add(0);
            if (SelectOrderedMutationClaimsForSharedScheduling(
                    candidates, candidate => candidate, paid, cohortOwned).Contains(0)
                || !ordinaryOwners.Contains(0))
            {
                throw new InvalidOperationException("已付费别名重复进入服务，或普通归属被修改。");
            }
        }

        internal static void VerifyOrderedMutationKeyPolicyForTesting()
        {
            VerifySharedNaturalOrderedMutationServiceForTesting();
            OrderedMutationOutcomeFamilySignature family = new(
                Turn: 3,
                PotionCount: 1,
                ChoiceCount: 2,
                EffectMultisetKey: new StateFingerprint(0x101UL, 0x102UL),
                UnorderedOutcomeKey: new StateFingerprint(0x201UL, 0x202UL),
                Boundary: null);
            StateFingerprint rootKey = BuildOrderedMutationRootKey(family);
            OrderedMutationOutcomeFamilySignature boundaryFamily = family with
            {
                Boundary = new OrderedMutationBoundaryStamp(
                    FromTurn: 3,
                    FromShufflesCrossed: 0,
                    ToTurn: 4,
                    ToShufflesCrossed: 1),
            };
            StateFingerprint firstInitial = BuildOrderedMutationInitialKey(
                rootKey,
                new StateFingerprint(0x301UL, 0x302UL));
            StateFingerprint secondInitial = BuildOrderedMutationInitialKey(
                rootKey,
                new StateFingerprint(0x303UL, 0x304UL));
            if (rootKey != BuildOrderedMutationRootKey(family)
                || rootKey == BuildOrderedMutationRootKey(boundaryFamily)
                || BuildOrderedMutationActivationKey(family)
                    == BuildOrderedMutationActivationKey(boundaryFamily)
                || firstInitial == secondInitial)
            {
                throw new InvalidOperationException(
                    "同一碰撞族没有共享 root、不同 ordering lane 被合并，或边界/非边界族混合。");
            }

            OrderedMutationRetentionLease initialLease = new(
                rootKey,
                firstInitial,
                firstInitial,
                OriginTurn: family.Turn,
                OriginShufflesCrossed: 0,
                PortfolioPriority: 0,
                BoundaryReached: false);
            OrderedMutationRetentionLease firstDerived = initialLease with
            {
                Key = BuildOrderedMutationDerivedKey(
                    initialLease.Key,
                    new StateFingerprint(0x401UL, 0x402UL)),
            };
            OrderedMutationRetentionLease secondDerived = firstDerived with
            {
                Key = BuildOrderedMutationDerivedKey(
                    firstDerived.Key,
                    new StateFingerprint(0x403UL, 0x404UL)),
            };
            if (firstDerived.Key == initialLease.Key
                || secondDerived.Key == firstDerived.Key
                || firstDerived.RootKey != rootKey
                || secondDerived.RootKey != rootKey
                || firstDerived.InitialKey != firstInitial
                || secondDerived.InitialKey != firstInitial)
            {
                throw new InvalidOperationException(
                    "派生 ordered-mutation lease 改写了稳定 Root/Initial 身份。");
            }

            static PlanAction Action(
                PlanChoiceEffect effect,
                string source,
                string context,
                string target,
                string cardStateKey = "",
                int sourceOccurrence = 0,
                int optionOccurrence = 0)
                => new(
                    PlanActionKind.PlayCard,
                    Turn: 3,
                    CardId: "TEST.ORDERED_MUTATION_SOURCE",
                    Choice: new PlanCardChoice(
                        effect,
                        PileType.Hand,
                        [new PlanCardToken(
                            target,
                            0,
                            "",
                            sourceOccurrence,
                            optionOccurrence,
                            target)],
                        SourceId: source,
                        ContextId: context),
                    CardStateKey: cardStateKey);

            PlanAction persistentFirst = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_A");
            PlanAction persistentSecond = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_B");
            PlanAction persistentOtherOccurrence = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_A",
                sourceOccurrence: 1,
                optionOccurrence: 1);
            PlanAction persistentMutatedSource = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_A",
                cardStateKey: "MUTATED_CARD_STATE");
            PlanAction otherSource = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_B",
                "CONTEXT_A",
                "TARGET_A");
            PlanAction otherEffect = Action(
                PlanChoiceEffect.Upgrade,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_A");
            PlanAction otherContext = Action(
                PlanChoiceEffect.Exhaust,
                "SOURCE_A",
                "CONTEXT_B",
                "TARGET_A");
            bool firstPersistent = TryBuildOrderedMutationContinuationSourceFamilyKey(
                persistentFirst,
                out StateFingerprint firstFamily);
            bool secondPersistent = TryBuildOrderedMutationContinuationSourceFamilyKey(
                persistentSecond,
                out StateFingerprint secondFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                otherSource,
                out StateFingerprint otherSourceFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                otherEffect,
                out StateFingerprint otherEffectFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                otherContext,
                out StateFingerprint otherContextFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                persistentOtherOccurrence,
                out StateFingerprint otherOccurrenceFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                persistentMutatedSource,
                out StateFingerprint mutatedContinuationFamily);
            _ = TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                persistentFirst,
                out StateFingerprint firstRecurrenceFamily);
            _ = TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                persistentMutatedSource,
                out StateFingerprint mutatedRecurrenceFamily);
            _ = TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                otherSource,
                out StateFingerprint otherSourceRecurrenceFamily);
            _ = TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                otherEffect,
                out StateFingerprint otherEffectRecurrenceFamily);
            _ = TryBuildOrderedMutationRecurrenceSourceFamilyKey(
                otherContext,
                out StateFingerprint otherContextRecurrenceFamily);
            StateFingerprint firstOption =
                BuildOrderedMutationContinuationOptionKey(persistentFirst);
            StateFingerprint otherOccurrenceOption =
                BuildOrderedMutationContinuationOptionKey(persistentOtherOccurrence);
            if (!firstPersistent
                || !secondPersistent
                || firstFamily != secondFamily
                || firstOption
                    == BuildOrderedMutationContinuationOptionKey(persistentSecond)
                || firstFamily != otherOccurrenceFamily
                || firstFamily == mutatedContinuationFamily
                || firstRecurrenceFamily != mutatedRecurrenceFamily
                || firstOption != otherOccurrenceOption
                || firstFamily == otherSourceFamily
                || firstFamily == otherEffectFamily
                || firstFamily == otherContextFamily
                || firstRecurrenceFamily == otherSourceRecurrenceFamily
                || firstRecurrenceFamily == otherEffectRecurrenceFamily
                || firstRecurrenceFamily == otherContextRecurrenceFamily)
            {
                throw new InvalidOperationException(
                    "persistent source-family 分组错误地按目标拆分、跨 source/effect/context 合并，或 recurrence 身份随可变 card state 重启。");
            }

            StateFingerprint firstChildState = new(0x501UL, 0x502UL);
            StateFingerprint secondChildState = new(0x503UL, 0x504UL);
            OrderedMutationContinuationOutcomeKey firstOutcome = new(
                firstOption,
                firstChildState);
            OrderedMutationContinuationOutcomeKey equivalentOccurrenceOutcome = new(
                otherOccurrenceOption,
                firstChildState);
            OrderedMutationContinuationOutcomeKey distinctOccurrenceOutcome = new(
                otherOccurrenceOption,
                secondChildState);
            HashSet<OrderedMutationContinuationOutcomeKey> selectedOutcomes = [firstOutcome];
            int retainedUnselectedOutcomes = new[]
                {
                    equivalentOccurrenceOutcome,
                    distinctOccurrenceOutcome,
                }
                .Where(outcome => !selectedOutcomes.Contains(outcome))
                .Distinct()
                .Count();
            if (firstOutcome != equivalentOccurrenceOutcome
                || firstOutcome == distinctOccurrenceOutcome
                || retainedUnselectedOutcomes != 1)
            {
                throw new InvalidOperationException(
                    "ordered-mutation outcome 去重没有合并等价 occurrence，或吞掉不同 child state。");
            }

            PlanAction transientFirst = Action(
                PlanChoiceEffect.Discard,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_A");
            PlanAction transientSecond = Action(
                PlanChoiceEffect.Discard,
                "SOURCE_A",
                "CONTEXT_A",
                "TARGET_B");
            bool transientPersistent = TryBuildOrderedMutationContinuationSourceFamilyKey(
                transientFirst,
                out StateFingerprint transientFirstFamily);
            _ = TryBuildOrderedMutationContinuationSourceFamilyKey(
                transientSecond,
                out StateFingerprint transientSecondFamily);
            if (transientPersistent || transientFirstFamily == transientSecondFamily)
            {
                throw new InvalidOperationException(
                    "非 persistent choice 的目标被 source-family key 省略。");
            }

            PlanAction[] groupedActions = [persistentFirst, persistentSecond, otherSource];
            int[] optionsPerFamily = groupedActions
                .GroupBy(action =>
                {
                    bool persistent = TryBuildOrderedMutationContinuationSourceFamilyKey(
                        action,
                        out StateFingerprint key);
                    return new OrderedMutationContinuationSourceFamilySignature(key, persistent);
                })
                .Select(group => group
                    .Select(BuildOrderedMutationContinuationOptionKey)
                    .Distinct()
                    .Count())
                .Order()
                .ToArray();
            if (!optionsPerFamily.SequenceEqual([1, 2]))
            {
                throw new InvalidOperationException(
                    "raw ordered-mutation 候选没有先按 source family 拆成独立 packet。");
            }

            StateFingerprint checkpointStateKey = new(0x601UL, 0x602UL);
            StateFingerprint preBoundarySequence = new(0x603UL, 0x604UL);
            StateFingerprint postBoundarySequence = new(0x605UL, 0x606UL);
            StateFingerprint boundaryTransitionKey =
                BuildOrderedMutationBoundaryTransitionKey(
                    initialLease.Key,
                    preBoundarySequence,
                    checkpointStateKey,
                    postBoundarySequence);
            OrderedMutationRetentionLease boundaryPending = initialLease with
            {
                BoundaryReached = true,
            };
            OrderedMutationRetentionLease committedBoundary =
                CommitOrderedMutationLeaseTransition(
                    boundaryPending,
                    transitionPending: true,
                    boundaryTransitionKey,
                    transitionOccurred: true,
                    turn: 4,
                    shufflesCrossed: 1);
            StateFingerprint expectedCommittedKey = BuildOrderedMutationDerivedKey(
                BuildOrderedMutationCheckpointKey(
                    BuildOrderedMutationDerivedKey(
                        initialLease.Key,
                        preBoundarySequence),
                    checkpointStateKey),
                postBoundarySequence);
            StateFingerprint wrongCheckpointFirst = BuildOrderedMutationDerivedKey(
                BuildOrderedMutationDerivedKey(
                    BuildOrderedMutationCheckpointKey(
                        initialLease.Key,
                        checkpointStateKey),
                    preBoundarySequence),
                postBoundarySequence);
            OrderedMutationRetentionLease reenteredBoundary =
                CommitOrderedMutationLeaseTransition(
                    committedBoundary,
                    transitionPending: false,
                    transitionedKey: new StateFingerprint(0x607UL, 0x608UL),
                    transitionOccurred: true,
                    turn: 5,
                    shufflesCrossed: 2);
            StateFingerprint transitionSequence = new(0x609UL, 0x60aUL);
            OrderedMutationRetentionLease normalSelectedMutation =
                CommitOrderedMutationLeaseTransition(
                    initialLease,
                    transitionPending: true,
                    transitionedKey: BuildOrderedMutationDerivedKey(
                        initialLease.Key,
                        transitionSequence),
                    transitionOccurred: true,
                    turn: family.Turn,
                    shufflesCrossed: 0);
            OrderedMutationLineage previousTurnLineage = new(
                family.Turn,
                5,
                new StateFingerprint(0x60bUL, 0x60cUL),
                new StateFingerprint(0x60dUL, 0x60eUL));
            OrderedMutationLineage newTurnLineage = new(
                family.Turn + 1,
                1,
                new StateFingerprint(0x60fUL, 0x610UL),
                new StateFingerprint(0x611UL, 0x612UL));
            if (committedBoundary.Key != expectedCommittedKey
                || committedBoundary.Key == wrongCheckpointFirst
                || committedBoundary.BoundaryReached
                || committedBoundary.OriginTurn != 4
                || committedBoundary.OriginShufflesCrossed != 1
                || committedBoundary.RootKey != initialLease.RootKey
                || committedBoundary.InitialKey != initialLease.InitialKey
                || reenteredBoundary != committedBoundary
                || normalSelectedMutation.Key
                    != BuildOrderedMutationDerivedKey(
                        initialLease.Key,
                        transitionSequence)
                || normalSelectedMutation.BoundaryReached
                || !HasOrderedMutationLineageAdvanced(
                    previousTurnLineage,
                    newTurnLineage)
                || HasOrderedMutationLineageAdvanced(
                    previousTurnLineage,
                    previousTurnLineage))
            {
                throw new InvalidOperationException(
                    "ordered-mutation transition 未提交 checkpoint/derived key，或 finalizer 重入重复派生。");
            }

            Dictionary<StateFingerprint, int> naturalRootAdmissions =
                new() { [initialLease.RootKey] =
                    OrderedMutationRetentionLease.MaximumProtectedAdmissions };
            Dictionary<StateFingerprint, int> naturalInitialAdmissions =
                new() { [initialLease.InitialKey] =
                    OrderedMutationRetentionLease.MaximumProtectedAdmissions };
            Dictionary<StateFingerprint, int> naturalLeaseAdmissions =
                new() { [initialLease.Key] =
                    OrderedMutationRetentionLease.MaximumProtectedAdmissions };
            int naturalRunAdmissions = OrderedMutationRetentionLease.MaximumProtectedAdmissions;
            for (int admission = 0;
                 admission < OrderedMutationRetentionLease.MaximumProtectedAdmissions;
                 admission++)
            {
                if (!TryConsumeOrderedMutationAdmission(
                        naturalRootAdmissions,
                        naturalInitialAdmissions,
                        naturalLeaseAdmissions,
                        ref naturalRunAdmissions,
                        normalSelectedMutation,
                        out _))
                {
                    throw new InvalidOperationException(
                        "普通榜 mutation/boundary winner 没有对 derived leaf 逐层付费。");
                }
            }
            if (naturalLeaseAdmissions.GetValueOrDefault(initialLease.Key)
                    != OrderedMutationRetentionLease.MaximumProtectedAdmissions
                || naturalLeaseAdmissions.GetValueOrDefault(normalSelectedMutation.Key)
                    != OrderedMutationRetentionLease.MaximumProtectedAdmissions
                || TryConsumeOrderedMutationAdmission(
                    naturalRootAdmissions,
                    naturalInitialAdmissions,
                    naturalLeaseAdmissions,
                    ref naturalRunAdmissions,
                    normalSelectedMutation,
                    out _))
            {
                throw new InvalidOperationException(
                    "普通榜 derived lease 错扣旧 Key，或超过 16/lease 后仍未降级。");
            }

            var handoffCandidates = new[]
            {
                (Parent: 1, Quality: 0, Selected: false, Id: 10),
                (Parent: 1, Quality: 9, Selected: true, Id: 11),
                (Parent: 1, Quality: 1, Selected: false, Id: 12),
                (Parent: 2, Quality: 4, Selected: false, Id: 20),
                (Parent: 2, Quality: 2, Selected: false, Id: 21),
            };
            static List<(int Parent, int Quality, bool Selected, int Id)>
                SelectHandoffFixtures(
                    IEnumerable<(int Parent, int Quality, bool Selected, int Id)> candidates)
                => SelectOneOrderedMutationFulfillmentPerObligation(
                    candidates,
                    candidate => candidate.Parent,
                    candidate => candidate.Selected,
                    (left, right) =>
                    {
                        int comparison = left.Quality.CompareTo(right.Quality);
                        return comparison != 0
                            ? comparison
                            : left.Id.CompareTo(right.Id);
                    });
            int[] selectedHandoffIds = SelectHandoffFixtures(handoffCandidates)
                .Select(candidate => candidate.Id)
                .ToArray();
            int[] reversedHandoffIds = SelectHandoffFixtures(handoffCandidates.Reverse())
                .Select(candidate => candidate.Id)
                .ToArray();
            if (!selectedHandoffIds.SequenceEqual([21, 11])
                || !reversedHandoffIds.SequenceEqual(selectedHandoffIds)
                || SelectHandoffFixtures(handoffCandidates)
                    .Select(candidate => candidate.Parent)
                    .Distinct()
                    .Count() != selectedHandoffIds.Length
                || SelectHandoffFixtures(handoffCandidates)
                    .Count(candidate => !candidate.Selected) != 1)
            {
                throw new InvalidOperationException(
                    "ordered-mutation handoff 未按 parent obligation 唯一选择，或 selected fulfillment 被计为新增 admission。");
            }

            if (AvailableOrderedMutationLayerAdmissions(
                    admissions: 47,
                    admissionLimit: MaximumOrderedMutationLayerAdmissions,
                    reasonAdmissions: 0,
                    reasonAdmissionLimit:
                        MaximumOrderedMutationCounterfactualSiblingAdmissions) != 1
                || AvailableOrderedMutationLayerAdmissions(
                    admissions: MaximumOrderedMutationLayerAdmissions,
                    admissionLimit: MaximumOrderedMutationLayerAdmissions,
                    reasonAdmissions: 0,
                    reasonAdmissionLimit:
                        MaximumOrderedMutationBoundaryHandoffAdmissions) != 0
                || AvailableOrderedMutationLayerAdmissions(
                    admissions: 0,
                    admissionLimit: 8,
                    reasonAdmissions: 0,
                    reasonAdmissionLimit:
                        MaximumOrderedMutationBoundaryHandoffAdmissions) != 8
                || OrderedMutationLayerAdmissionLimit(3) != 3
                || OrderedMutationLayerAdmissionLimit(512)
                    != MaximumOrderedMutationLayerAdmissions)
            {
                throw new InvalidOperationException(
                    "ordered-mutation reason queue 绕过共享单层 admission 上限。");
            }

            var admissionOrderFixtures = new[]
            {
                (Root: 1, Initial: 11, Lease: 111,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 0, Id: 101),
                (Root: 1, Initial: 11, Lease: 111,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 1, Id: 102),
                (Root: 2, Initial: 21, Lease: 211,
                    Reason: OrderedMutationAdmissionClaimReason.Observation,
                    Quality: 0, Id: 201),
                (Root: 2, Initial: 21, Lease: 211,
                    Reason: OrderedMutationAdmissionClaimReason.Observation,
                    Quality: 1, Id: 202),
                (Root: 3, Initial: 31, Lease: 311,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 0, Id: 301),
                (Root: 3, Initial: 31, Lease: 311,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 1, Id: 302),
            };
            static List<(int Root, int Initial, int Lease,
                OrderedMutationAdmissionClaimReason Reason, int Quality, int Id)>
                OrderAdmissionFixtures(
                    IEnumerable<(int Root, int Initial, int Lease,
                        OrderedMutationAdmissionClaimReason Reason, int Quality, int Id)>
                        fixtures)
                => OrderOrderedMutationAdmissionClaimsFairlyCore(
                    fixtures,
                    fixture => new StateFingerprint((ulong)fixture.Root, 0UL),
                    fixture => new StateFingerprint((ulong)fixture.Initial, 0UL),
                    fixture => new StateFingerprint((ulong)fixture.Lease, 0UL),
                    _ => 0,
                    _ => 0,
                    _ => 0,
                    fixture => fixture.Reason,
                    Comparer<(int Root, int Initial, int Lease,
                        OrderedMutationAdmissionClaimReason Reason, int Quality, int Id)>
                        .Create((left, right) =>
                        {
                            int comparison = left.Quality.CompareTo(right.Quality);
                            return comparison != 0
                                ? comparison
                                : left.Id.CompareTo(right.Id);
                        }));
            int[] admissionOrder = OrderAdmissionFixtures(admissionOrderFixtures)
                .Select(fixture => fixture.Id)
                .ToArray();
            int[] reverseAdmissionOrder = OrderAdmissionFixtures(
                    admissionOrderFixtures.Reverse())
                .Select(fixture => fixture.Id)
                .ToArray();
            if (!admissionOrder.SequenceEqual([101, 201, 301, 102, 202, 302])
                || !reverseAdmissionOrder.SequenceEqual(admissionOrder)
                || OrderAdmissionFixtures(admissionOrderFixtures)
                    .Take(3)
                    .Select(fixture => fixture.Root)
                    .Distinct()
                    .Count() != 3)
            {
                throw new InvalidOperationException(
                    "统一 ordered-mutation admission 没有按 root 首轮公平调度，或依赖输入顺序。");
            }

            var reasonRoundRobinFixtures = new[]
            {
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 0, Id: 10),
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 1, Id: 11),
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Observation,
                    Quality: 0, Id: 20),
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Observation,
                    Quality: 1, Id: 21),
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 0, Id: 30),
                (Root: 1, Initial: 1, Lease: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 1, Id: 31),
            };
            int[] reasonRoundRobinOrder = OrderAdmissionFixtures(
                    reasonRoundRobinFixtures)
                .Select(fixture => fixture.Id)
                .ToArray();
            if (!reasonRoundRobinOrder.SequenceEqual([10, 20, 30, 11, 21, 31])
                || !OrderAdmissionFixtures(reasonRoundRobinFixtures.Reverse())
                    .Select(fixture => fixture.Id)
                    .SequenceEqual(reasonRoundRobinOrder))
            {
                throw new InvalidOperationException(
                    "统一 ordered-mutation admission 在同一 lease 内串行耗尽理由队列，或排序不稳定。");
            }

            var workFairnessFixtures = new[]
            {
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 1, ParentState: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 0, Id: 10),
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 1, ParentState: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 1, Id: 11),
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 1, ParentState: 1,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 0, Id: 12),
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 2, ParentState: 2,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 0, Id: 20),
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 2, ParentState: 2,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff,
                    Quality: 1, Id: 21),
                (Root: 1, Initial: 1, Lease: 1, ParentLineage: 3, ParentState: 3,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary,
                    Quality: 0, Id: 30),
            };
            static int[] OrderWorkFixtures(
                IEnumerable<(int Root, int Initial, int Lease, int ParentLineage,
                    int ParentState, OrderedMutationAdmissionClaimReason Reason,
                    int Quality, int Id)> fixtures)
                => OrderOrderedMutationAdmissionWorkFairlyCore(
                        fixtures,
                        fixture => new StateFingerprint((ulong)fixture.Root, 0UL),
                        fixture => new StateFingerprint((ulong)fixture.Initial, 0UL),
                        fixture => new StateFingerprint((ulong)fixture.Lease, 0UL),
                        fixture => new StateFingerprint(
                            (ulong)fixture.ParentLineage,
                            0UL),
                        fixture => new StateFingerprint((ulong)fixture.ParentState, 0UL),
                        _ => 0,
                        _ => 0,
                        _ => 0,
                        _ => 0,
                        fixture => fixture.Reason,
                        Comparer<(int Root, int Initial, int Lease, int ParentLineage,
                            int ParentState, OrderedMutationAdmissionClaimReason Reason,
                            int Quality, int Id)>.Create((left, right) =>
                        {
                            int comparison = left.Quality.CompareTo(right.Quality);
                            return comparison != 0
                                ? comparison
                                : left.Id.CompareTo(right.Id);
                        }))
                    .Select(fixture => fixture.Id)
                    .ToArray();
            int[] fairWorkOrder = OrderWorkFixtures(workFairnessFixtures);
            if (!fairWorkOrder.SequenceEqual([10, 20, 30, 12, 21, 11])
                || !OrderWorkFixtures(workFairnessFixtures.Reverse())
                    .SequenceEqual(fairWorkOrder)
                || !fairWorkOrder.Take(3).Contains(30)
                || Array.IndexOf(fairWorkOrder, 12) > Array.IndexOf(fairWorkOrder, 11))
            {
                throw new InvalidOperationException(
                    "paid handoff cohorts 饿死 exact-parent ordinary peer，或 work 排序依赖枚举顺序。");
            }

            OrderedMutationContinuationBudgetKey firstParentBudget = new(
                new StateFingerprint(1, 0),
                new StateFingerprint(2, 0),
                new StateFingerprint(3, 0),
                new StateFingerprint(4, 0),
                new StateFingerprint(5, 0),
                new StateFingerprint(6, 0));
            OrderedMutationContinuationBudgetKey secondParentBudget =
                firstParentBudget with
                {
                    ParentStateKey = new StateFingerprint(7, 0),
                };
            Dictionary<OrderedMutationContinuationBudgetKey, int> parentBudgets = new()
            {
                [firstParentBudget] =
                    MaximumOrderedMutationContinuationsPerLineagePerPrune,
            };
            if (firstParentBudget == secondParentBudget
                || parentBudgets.GetValueOrDefault(secondParentBudget) != 0)
            {
                throw new InvalidOperationException(
                    "同 lineage/source 的不同 exact parent 错误共享了 per-prune continuation 限额。");
            }

            OrderedMutationAdmissionClaimKey sharedClaimKey = new(
                new StateFingerprint(0x701UL, 0x702UL),
                new StateFingerprint(0x703UL, 0x704UL),
                new StateFingerprint(0x705UL, 0x706UL),
                new StateFingerprint(0x707UL, 0x708UL),
                new StateFingerprint(0x709UL, 0x70aUL),
                new StateFingerprint(0x70bUL, 0x70cUL),
                new OrderedMutationContinuationOutcomeKey(
                    new StateFingerprint(0x70dUL, 0x70eUL),
                    new StateFingerprint(0x70fUL, 0x710UL)));
            var semanticAliases = new[]
            {
                (Key: sharedClaimKey,
                    Reason: OrderedMutationAdmissionClaimReason.Handoff),
                (Key: sharedClaimKey,
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary),
                (Key: sharedClaimKey with
                    {
                        Outcome = sharedClaimKey.Outcome with
                        {
                            ChildStateKey = new StateFingerprint(0x711UL, 0x712UL),
                        },
                    },
                    Reason: OrderedMutationAdmissionClaimReason.Ordinary),
            };
            var coalescedSemanticAliases = semanticAliases
                .GroupBy(alias => alias.Key)
                .Select(group => group.Select(alias => alias.Reason).ToHashSet())
                .ToList();
            if (coalescedSemanticAliases.Count != 2
                || !coalescedSemanticAliases.Any(reasons =>
                    reasons.SetEquals([
                        OrderedMutationAdmissionClaimReason.Handoff,
                        OrderedMutationAdmissionClaimReason.Ordinary])))
            {
                throw new InvalidOperationException(
                    "同一 ordered-mutation semantic outcome 没有折叠为一次 admission claim。");
            }

            IReadOnlySet<OrderedMutationAdmissionClaimReason> cappedCounterfactualAlias =
                new HashSet<OrderedMutationAdmissionClaimReason>
                {
                    OrderedMutationAdmissionClaimReason.Counterfactual,
                    OrderedMutationAdmissionClaimReason.Ordinary,
                };
            IReadOnlySet<OrderedMutationAdmissionClaimReason> cappedAlternativeAlias =
                new HashSet<OrderedMutationAdmissionClaimReason>
                {
                    OrderedMutationAdmissionClaimReason.Alternative,
                    OrderedMutationAdmissionClaimReason.Ordinary,
                };
            if (!TrySelectOrderedMutationAdmissionReason(
                    cappedCounterfactualAlias,
                    handoffAdmissions: 0,
                    observationAdmissions: 0,
                    counterfactualAdmissions: 0,
                    alternativeAdmissions: 0,
                    reason => reason !=
                        OrderedMutationAdmissionClaimReason.Counterfactual,
                    out OrderedMutationAdmissionClaimReason counterfactualFallback)
                || counterfactualFallback != OrderedMutationAdmissionClaimReason.Ordinary
                || !TrySelectOrderedMutationAdmissionReason(
                    cappedAlternativeAlias,
                    handoffAdmissions: 0,
                    observationAdmissions: 0,
                    counterfactualAdmissions: 0,
                    alternativeAdmissions: MaximumOrderedMutationAlternativeAdmissions,
                    _ => true,
                    out OrderedMutationAdmissionClaimReason alternativeFallback)
                || alternativeFallback != OrderedMutationAdmissionClaimReason.Ordinary)
            {
                throw new InvalidOperationException(
                    "多理由 admission claim 在专用理由满额后没有复用 ordinary 资格。");
            }

        }

        private RootActionLineageSignature BuildRootActionLineageSignature(SearchNode node)
        {
            PlanAction action = RootActionLineageNode(node).Action
                ?? throw new InvalidOperationException("搜索首步谱系缺少动作。");
            PlanAction? firstCard = _preserveReplayAllocatorOpening
                ? node.Actions.FirstOrDefault(candidate => candidate.Kind == PlanActionKind.PlayCard)
                : null;
            return new RootActionLineageSignature(
                action.Kind,
                action.CardId,
                action.PotionId,
                action.TargetCombatId,
                firstCard?.CardId ?? "",
                firstCard?.TargetCombatId);
        }

        private static SearchNode RootActionLineageNode(SearchNode node)
        {
            SearchNode cursor = node;
            while (cursor.Parent?.Action != null)
                cursor = cursor.Parent;
            return cursor;
        }

        private PocketwatchCadenceSignature BuildPocketwatchCadenceSignature(SearchNode node)
        {
            SimulationSnapshot snapshot = node.Snapshot;
            int threshold = snapshot.PocketwatchCardThreshold;
            return new PocketwatchCadenceSignature(
                node.PotionCount,
                snapshot.FocusTargetCombatId,
                RetainedAttackGrowth(snapshot),
                snapshot.EnemyControlDistributionKey,
                threshold >= 0 && snapshot.PocketwatchCardsPlayedLastTurn <= threshold,
                snapshot.CanStillTriggerPocketwatch);
        }

        private PocketwatchCadenceFamilySignature BuildPocketwatchCadenceFamilySignature(SearchNode node)
        {
            SimulationSnapshot snapshot = node.Snapshot;
            int threshold = snapshot.PocketwatchCardThreshold;
            return new PocketwatchCadenceFamilySignature(
                node.PotionCount,
                snapshot.FocusTargetCombatId,
                RetainedAttackGrowth(snapshot),
                threshold >= 0 && snapshot.PocketwatchCardsPlayedLastTurn <= threshold,
                snapshot.CanStillTriggerPocketwatch);
        }

        private static void AddTacticalGroup(
            List<IGrouping<StateFingerprint, SearchNode>> selected,
            IGrouping<StateFingerprint, SearchNode> candidate)
        {
            if (!selected.Any(group => ReferenceEquals(group, candidate)))
                selected.Add(candidate);
        }

        private static void AddRoutingCandidate(
            List<SearchNode> selected,
            SearchNode? candidate,
            int limit = int.MaxValue)
        {
            if (candidate != null
                && selected.Count < limit
                && !ContainsReference(selected, candidate))
            {
                selected.Add(candidate);
            }
        }

        private static void AddRoutingContext(
            List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> selected,
            KeyValuePair<RoutingChoiceSignature, List<SearchNode>> candidate)
        {
            if (!selected.Any(pair => pair.Key == candidate.Key))
                selected.Add(candidate);
        }

        private static bool IsPersistentRoutingEffect(PlanChoiceEffect effect)
            => IsOrderedPersistentMutationEffect(effect);

        private static bool IsRoutingChoiceEffect(PlanChoiceEffect effect)
            => IsPersistentRoutingEffect(effect)
                || effect is PlanChoiceEffect.MoveToHand
                    or PlanChoiceEffect.MoveToDrawTop
                    or PlanChoiceEffect.Discard
                    or PlanChoiceEffect.DiscardAndDraw
                    or PlanChoiceEffect.MoveToHandFreeThisTurn
                    or PlanChoiceEffect.GenerateToHand;

        private double RoutingParentScore(IReadOnlyList<SearchNode> nodes)
            => nodes.Max(node =>
            {
                if (TryGetRetainedRoutingChoice(node, out _, out SearchNode choiceNode)
                    && choiceNode.Parent is { } choiceParent)
                {
                    return BeamRankScore(choiceParent);
                }
                return BeamRankScore(node);
            });

        private static int RoutingParentRetentionRank(IReadOnlyList<SearchNode> nodes)
            => nodes.Min(node =>
            {
                if (TryGetRetainedRoutingChoice(node, out _, out SearchNode choiceNode)
                    && choiceNode.Parent is { } choiceParent)
                {
                    return choiceParent.RetentionRank;
                }
                return node.RetentionRank;
            });

        private static RoutingChoiceFamilySignature BuildRoutingChoiceFamilySignature(
            RoutingChoiceSignature signature)
            => new(
                signature.Turn,
                signature.SourceId,
                signature.Effect,
                signature.Pile);

        private List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> OrderRoutingChoiceEventContexts(
            IEnumerable<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> contexts)
        {
            List<IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>> optionGroups = contexts
                .GroupBy(pair => BuildRoutingChoiceOptionSignature(pair.Key))
                .OrderBy(group => group.Min(pair => RoutingParentRetentionRank(pair.Value)))
                .ThenByDescending(group => group.Max(pair => RoutingParentScore(pair.Value)))
                .ThenByDescending(group => group.Max(pair => pair.Value.Max(BeamRankScore)))
                .Select(group => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)group
                    .OrderBy(pair => RoutingParentRetentionRank(pair.Value))
                    .ThenByDescending(pair => RoutingParentScore(pair.Value))
                    .ThenByDescending(pair => pair.Value.Max(BeamRankScore))
                    .ToList())
                .ToList();
            return InterleaveRoutingChoiceContexts(optionGroups);
        }

        /// <summary>
        /// Interleaves bounded context blocks rather than individual contexts. The separate
        /// option-leader lane gives every option breadth coverage; this lane must preserve enough
        /// consecutive depth for a low-immediate-value option to reach delayed-payoff machinery.
        /// No option contributes more than the fixed quantum in one round.
        /// </summary>
        internal static List<T> InterleaveRoutingChoiceContexts<T>(
            IReadOnlyList<IReadOnlyList<T>> optionGroups)
        {
            int rounds = 0;
            int total = 0;
            foreach (IReadOnlyList<T> group in optionGroups)
            {
                rounds = Math.Max(rounds, group.Count);
                total = checked(total + group.Count);
            }

            List<T> ordered = new(total);
            for (int roundStart = 0;
                 roundStart < rounds;
                 roundStart += PersistentRoutingContextRounds)
            {
                foreach (IReadOnlyList<T> group in optionGroups)
                {
                    int roundEnd = Math.Min(
                        group.Count,
                        roundStart + PersistentRoutingContextRounds);
                    for (int index = roundStart; index < roundEnd; index++)
                        ordered.Add(group[index]);
                }
            }
            return ordered;
        }

        private List<SearchNode> BuildDirectRoutingChoiceExtremes(IReadOnlyList<SearchNode> ranked)
        {
            List<DirectRoutingChoice> direct = [];
            foreach (SearchNode node in ranked)
            {
                if (!TryGetCurrentTurnRoutingChoice(node, out RoutingChoiceSignature signature, out SearchNode choiceNode)
                    || !ReferenceEquals(node, choiceNode)
                    || choiceNode.Parent is not { } parent
                    || parent.Snapshot.Energy != 0)
                {
                    continue;
                }
                direct.Add(new DirectRoutingChoice(node, choiceNode, parent, signature));
            }

            List<IReadOnlyList<DirectRoutingChoice>> byFamily = direct
                .GroupBy(item => BuildRoutingChoiceFamilySignature(item.Signature))
                .OrderBy(family => family.Min(item => item.Parent.RetentionRank))
                .ThenByDescending(family => family.Max(item => BeamRankScore(item.Parent)))
                .Select(family => (IReadOnlyList<DirectRoutingChoice>)family
                    .GroupBy(item => (item.Parent.StateKey, item.Parent.ActionCount))
                    .Select(parent => parent
                        .OrderByDescending(item => RoutingChoiceCardinality(item.Signature))
                        .ThenByDescending(item => AttackDensity(item.Node.Snapshot))
                        .ThenByDescending(item => BeamRankScore(item.Node))
                        .First())
                    .OrderBy(item => item.Parent.RetentionRank)
                    .ThenByDescending(item => BeamRankScore(item.Parent))
                    .Take(RoutingChoiceLimit)
                    .ToList())
                .ToList();
            return byFamily
                .SelectMany(family => family)
                .OrderBy(item => item.Parent.RetentionRank)
                .ThenByDescending(item => BeamRankScore(item.Parent))
                .Take(RoutingChoiceLimit)
                .Select(item => item.Node)
                .ToList();
        }

        private static int RoutingChoiceCardinality(RoutingChoiceSignature signature)
            => signature.CardId.EndsWith(" cards", StringComparison.Ordinal)
                ? signature.Upgrade
                : 1;

        internal static int BoundedRoutingChoiceQuota(int candidateCount)
        {
            if (candidateCount < 0)
                throw new ArgumentOutOfRangeException(nameof(candidateCount));
            return Math.Min(RoutingChoiceLimit, candidateCount);
        }

        private static StateFingerprint EndTurnDeckCompressionLineageKey(SearchNode node)
            => EndTurnDeckCompressionLineageRoot(node).StateKey;

        private static SearchNode EndTurnDeckCompressionLineageRoot(SearchNode node)
        {
            SearchNode cursor = node;
            while (cursor.Parent is { } parent
                && parent.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
            {
                cursor = parent;
            }
            return cursor;
        }

        private static RoutingChoiceOptionSignature BuildRoutingChoiceOptionSignature(
            RoutingChoiceSignature signature)
            => new(signature.CardId, signature.Upgrade, signature.CardStateKey);

        private static void AssignRetentionRanks(
            IReadOnlyList<SearchNode> ranked,
            IReadOnlyList<SearchNode> required)
        {
            for (int rankedIndex = 0; rankedIndex < ranked.Count; rankedIndex++)
            {
                SearchNode node = ranked[rankedIndex];
                int requiredIndex = -1;
                for (int index = 0; index < required.Count; index++)
                {
                    if (!ReferenceEquals(required[index], node))
                        continue;
                    requiredIndex = index;
                    break;
                }
                node.RetentionRank = requiredIndex >= 0
                    ? requiredIndex
                    : required.Count + rankedIndex;
            }
        }

        internal static void ReservePotionQuotaLeaders(
            HashSet<SearchNode> reservations,
            IReadOnlyList<SearchNode> rankedPool,
            bool usesPotion,
            int quota)
        {
            if (quota < 0)
                throw new ArgumentOutOfRangeException(nameof(quota));
            if (quota == 0)
                return;
            int reserved = 0;
            foreach (SearchNode candidate in rankedPool)
            {
                if (UsesPotion(candidate) != usesPotion)
                    continue;
                reservations.Add(candidate);
                reserved++;
                if (reserved >= quota)
                    return;
            }
        }

        internal static (int Used, int Unused) FeasiblePotionUseQuotas(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit));
            int used = limit < 4
                ? 1
                : Math.Max(2, limit / 3);
            return (used, limit - used);
        }

        internal static void EnforcePotionUseQuota(
            List<SearchNode> selected,
            IReadOnlyList<SearchNode> pool,
            IReadOnlySet<SearchNode> protectedNodes,
            bool usesPotion,
            int quota)
        {
            int retained = selected.Count(node => UsesPotion(node) == usesPotion);
            if (retained >= quota)
                return;

            foreach (SearchNode candidate in pool.Where(node => UsesPotion(node) == usesPotion))
            {
                if (retained >= quota)
                    return;
                if (ContainsReference(selected, candidate))
                    continue;
                int replaceIndex = selected.FindLastIndex(node =>
                    UsesPotion(node) != usesPotion
                    && !protectedNodes.Contains(node));
                if (replaceIndex < 0)
                    return;
                selected[replaceIndex] = candidate;
                retained++;
            }
        }

        internal static RoutingChoiceSignature? CurrentTurnRoutingChoice(SearchNode node)
            => TryGetCurrentTurnRoutingChoice(node, out RoutingChoiceSignature signature, out _)
                ? signature
                : null;

        private static RoutingChoiceSignature? RetainedRoutingChoice(SearchNode node)
            => TryGetRetainedRoutingChoice(node, out RoutingChoiceSignature signature, out _)
                ? signature
                : null;

        private static bool TryGetRetainedRoutingChoice(
            SearchNode node,
            out RoutingChoiceSignature signature,
            out SearchNode choiceNode)
        {
            int minimumChoiceTurn = node.Snapshot.CanTriggerArtOfWarNextTurn
                ? Math.Max(0, node.Turn - PersistentRoutingContextRounds)
                : node.Turn;
            return TryGetRoutingChoice(node, minimumChoiceTurn, out signature, out choiceNode);
        }

        private static bool TryGetCurrentTurnRoutingChoice(
            SearchNode node,
            out RoutingChoiceSignature signature,
            out SearchNode choiceNode)
            => TryGetRoutingChoice(node, node.Turn, out signature, out choiceNode);

        private static bool TryGetRoutingChoice(
            SearchNode node,
            int minimumChoiceTurn,
            out RoutingChoiceSignature signature,
            out SearchNode choiceNode)
        {
            signature = default;
            choiceNode = node;
            for (SearchNode? cursor = node;
                 cursor?.Action is { } action;
                 cursor = cursor.Parent)
            {
                if (action.TurnStartChoices is { Count: > 0 })
                {
                    foreach (PlanCardChoice choice in action.TurnStartChoices.Reverse())
                    {
                        if (TryBuildRoutingChoice(
                                node,
                                cursor,
                                choice,
                                action.Turn + 1,
                                minimumChoiceTurn,
                                out RoutingChoiceSignature turnStartSignature))
                        {
                            signature = turnStartSignature;
                            choiceNode = cursor;
                            return true;
                        }
                    }
                }

                if (action.NestedChoices is { Count: > 0 })
                {
                    foreach (PlanCardChoice choice in action.NestedChoices.Reverse())
                    {
                        if (TryBuildRoutingChoice(
                                node,
                                cursor,
                                choice,
                                action.Turn,
                                minimumChoiceTurn,
                                out RoutingChoiceSignature nestedSignature))
                        {
                            signature = nestedSignature;
                            choiceNode = cursor;
                            return true;
                        }
                    }
                }

                if (action.Choice != null
                    && TryBuildRoutingChoice(
                        node,
                        cursor,
                        action.Choice,
                        action.Turn,
                        minimumChoiceTurn,
                        out RoutingChoiceSignature actionSignature))
                {
                    signature = actionSignature;
                    choiceNode = cursor;
                    return true;
                }
            }
            return false;
        }

        private static int ActionsSinceRetainedRoutingChoice(SearchNode node)
        {
            if (!TryGetRetainedRoutingChoice(node, out _, out SearchNode choiceNode))
                return int.MaxValue;
            int count = 0;
            for (SearchNode? cursor = node; cursor != null && !ReferenceEquals(cursor, choiceNode); cursor = cursor.Parent)
                count++;
            return count;
        }

        private static bool TryBuildRoutingChoice(
            SearchNode node,
            SearchNode cursor,
            PlanCardChoice choice,
            int choiceTurn,
            int minimumChoiceTurn,
            out RoutingChoiceSignature signature)
        {
            signature = default;
            if (choice.Cards.Count == 0
                || !IsRoutingChoiceEffect(choice.Effect))
            {
                return false;
            }

            bool generated = choice.Effect == PlanChoiceEffect.GenerateToHand;
            if (choiceTurn < minimumChoiceTurn)
                return false;

            bool multiCard = choice.Cards.Count > 1;
            PlanCardToken card = choice.Cards[0];
            signature = new RoutingChoiceSignature(
                choiceTurn,
                choice.SourceId,
                choice.Effect,
                choice.SourcePile,
                multiCard ? $"{choice.Cards.Count} cards" : card.CardId,
                multiCard ? choice.Cards.Count : card.UpgradeLevel,
                multiCard ? string.Empty : card.StateKey,
                multiCard ? choice.Cards.Count : card.OptionOccurrence,
                choice.ContextId,
                generated ? cursor.Snapshot.HandCount : 0,
                cursor.Snapshot.EnemyCombatDistributionKey,
                cursor.Snapshot.EnemyControlDistributionKey,
                cursor.Snapshot.UnorderedPileKey);
            return true;
        }

        private static void AddRequired(List<SearchNode> required, SearchNode? candidate, int limit)
        {
            if (candidate == null
                || required.Count >= limit
                || required.Any(node => ReferenceEquals(node, candidate)))
            {
                return;
            }
            required.Add(candidate);
        }

        private static bool ContainsReference(IReadOnlyList<SearchNode> nodes, SearchNode candidate)
        {
            foreach (SearchNode node in nodes)
            {
                if (ReferenceEquals(node, candidate))
                    return true;
            }
            return false;
        }

        private static bool IsBetterDefensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                    && (candidate.Snapshot.OstyHp > current.Snapshot.OstyHp
                        || candidate.Snapshot.OstyHp == current.Snapshot.OstyHp
                            && (candidate.Snapshot.OstyMaxHp > current.Snapshot.OstyMaxHp
                                || candidate.Snapshot.OstyMaxHp == current.Snapshot.OstyMaxHp
                                    && (candidate.Snapshot.PlayerBlock > current.Snapshot.PlayerBlock
                                        || candidate.Snapshot.PlayerBlock == current.Snapshot.PlayerBlock
                                            && candidate.Score > current.Score)));

        private bool IsBetterCompletedVictory(SearchNode candidate, SearchNode? current)
            => current == null || CompareFinalCandidates(candidate, current) < 0;

        private int CompareFinalCandidates(SearchNode left, SearchNode right)
        {
            SimulationSnapshot leftSnapshot = left.Snapshot;
            SimulationSnapshot rightSnapshot = right.Snapshot;
            bool leftWon = IsCompleteVictory(left);
            bool rightWon = IsCompleteVictory(right);
            if (!leftWon && !rightWon)
            {
                bool leftSurvives = !leftSnapshot.PlayerDead
                    && leftSnapshot.ProjectedPlayerHp > 0;
                bool rightSurvives = !rightSnapshot.PlayerDead
                    && rightSnapshot.ProjectedPlayerHp > 0;
                int survivalComparison = rightSurvives.CompareTo(leftSurvives);
                if (survivalComparison != 0)
                    return survivalComparison;
            }

            int comparison = SolverInterimResultOrdering.ComparePrimaryQuality(
                leftWon,
                StrategicHpDeficit(leftSnapshot),
                leftWon ? CompletedCombatTurn(left) : null,
                rightWon,
                StrategicHpDeficit(rightSnapshot),
                rightWon ? CompletedCombatTurn(right) : null);
            if (comparison != 0)
                return comparison;

            int leftOutstanding = _theftPolicy == SolverTheftPolicy.PreserveResources
                ? leftSnapshot.OutstandingStolenResource
                : 0;
            int rightOutstanding = _theftPolicy == SolverTheftPolicy.PreserveResources
                ? rightSnapshot.OutstandingStolenResource
                : 0;
            comparison = leftOutstanding.CompareTo(rightOutstanding);
            if (comparison != 0)
                return comparison;
            comparison = HealthResourceCost(leftSnapshot).CompareTo(HealthResourceCost(rightSnapshot));
            if (comparison != 0)
                return comparison;
            comparison = rightSnapshot.LongTermResourceValue.CompareTo(leftSnapshot.LongTermResourceValue);
            if (comparison != 0)
                return comparison;
            comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
            if (comparison != 0)
                return comparison;
            comparison = PolicyBoundaryRank(leftSnapshot.BoundaryReason)
                .CompareTo(PolicyBoundaryRank(rightSnapshot.BoundaryReason));
            if (comparison != 0)
                return comparison;
            comparison = ExplicitPotionUseCount(left).CompareTo(ExplicitPotionUseCount(right));
            if (comparison != 0)
                return comparison;
            comparison = left.FutureSoldHp.CompareTo(right.FutureSoldHp);
            if (comparison != 0)
                return comparison;
            comparison = leftSnapshot.EnemyHp.CompareTo(rightSnapshot.EnemyHp);
            if (comparison != 0)
                return comparison;
            comparison = right.Score.CompareTo(left.Score);
            if (comparison != 0)
                return comparison;
            comparison = left.ActionCount.CompareTo(right.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = left.StateKey.First.CompareTo(right.StateKey.First);
            return comparison != 0
                ? comparison
                : left.StateKey.Second.CompareTo(right.StateKey.Second);
        }

        private bool IsCompleteVictory(SearchNode node)
            => SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount,
                node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead,
                node.Snapshot.ProjectedPlayerHp);

        private int StrategicHpDeficit(SimulationSnapshot snapshot)
            => snapshot.CumulativePlayerHpLost
                + Math.Max(0, _initialPlayerMaxHp - snapshot.PlayerMaxHp);

        private int HealthResourceCost(SimulationSnapshot snapshot)
            => _initialPlayerHp - snapshot.PlayerHp
                + _initialPlayerMaxHp - snapshot.PlayerMaxHp;

        private static int CompletedCombatTurn(SearchNode node)
            => node.Action?.Turn ?? node.Turn;

        private static bool IsBetterUtilityDefensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                    && candidate.Score > current.Score;

        private static bool IsBetterOffensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.AliveEnemyCount < current.Snapshot.AliveEnemyCount
                || candidate.Snapshot.AliveEnemyCount == current.Snapshot.AliveEnemyCount
                    && (candidate.Snapshot.RawEnemyHp < current.Snapshot.RawEnemyHp
                        || candidate.Snapshot.RawEnemyHp == current.Snapshot.RawEnemyHp
                            && (candidate.Snapshot.EnemyHp < current.Snapshot.EnemyHp
                        || candidate.Snapshot.EnemyHp == current.Snapshot.EnemyHp
                            && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                                    && candidate.Score > current.Score)));

        private static bool IsBetterResourcePreserving(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.OutstandingStolenResource < current.Snapshot.OutstandingStolenResource
                || candidate.Snapshot.OutstandingStolenResource == current.Snapshot.OutstandingStolenResource
                    && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                        || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                            && candidate.Score > current.Score);

        private static SearchNode? FindBestEnemyStrengthControl(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.EnemyStrengthSuppression > best.Snapshot.EnemyStrengthSuppression
                    || node.Snapshot.EnemyStrengthSuppression == best.Snapshot.EnemyStrengthSuppression
                        && (node.Snapshot.EnemyWeakTurns > best.Snapshot.EnemyWeakTurns
                            || node.Snapshot.EnemyWeakTurns == best.Snapshot.EnemyWeakTurns
                                && IsBetterDefensive(node, best))
                        ? node
                        : best);

        private static SearchNode? FindBestEnemyWeakControl(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.EnemyWeakTurns > best.Snapshot.EnemyWeakTurns
                    || node.Snapshot.EnemyWeakTurns == best.Snapshot.EnemyWeakTurns
                        && (node.Snapshot.EnemyStrengthSuppression > best.Snapshot.EnemyStrengthSuppression
                            || node.Snapshot.EnemyStrengthSuppression == best.Snapshot.EnemyStrengthSuppression
                                && IsBetterDefensive(node, best))
                        ? node
                        : best);

        private static bool IsBetterSetup(SearchNode candidate, SearchNode? current)
        {
            if (current == null)
                return true;
            int candidateValue = SetupLaneValue(candidate.Snapshot);
            int currentValue = SetupLaneValue(current.Snapshot);
            return candidateValue > currentValue
                || candidateValue == currentValue
                    && (candidate.Snapshot.RetainedAttackValue > current.Snapshot.RetainedAttackValue
                        || candidate.Snapshot.RetainedAttackValue == current.Snapshot.RetainedAttackValue
                            && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                                    && candidate.Score > current.Score));
        }

        private static SearchNode? FindBestTargetPressure(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                    || node.Snapshot.FocusTargetPressure == best.Snapshot.FocusTargetPressure
                        && (node.Snapshot.FocusTargetRemainingHp < best.Snapshot.FocusTargetRemainingHp
                            || node.Snapshot.FocusTargetRemainingHp == best.Snapshot.FocusTargetRemainingHp
                                && (node.Snapshot.FocusTargetCurrentThreat > best.Snapshot.FocusTargetCurrentThreat
                                    || node.Snapshot.FocusTargetCurrentThreat == best.Snapshot.FocusTargetCurrentThreat
                                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                                && node.Score > best.Score))))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindBestDeckCuration(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || AttackDensity(node.Snapshot) > AttackDensity(best.Snapshot)
                    || AttackDensity(node.Snapshot) == AttackDensity(best.Snapshot)
                        && (node.Snapshot.LiveDeckClutter < best.Snapshot.LiveDeckClutter
                            || node.Snapshot.LiveDeckClutter == best.Snapshot.LiveDeckClutter
                                && IsBetterSetup(node, best)))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindMostCompressedDeck(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.LiveDeckSize < best.Snapshot.LiveDeckSize
                    || node.Snapshot.LiveDeckSize == best.Snapshot.LiveDeckSize
                        && (AttackDensity(node.Snapshot) > AttackDensity(best.Snapshot)
                            || AttackDensity(node.Snapshot) == AttackDensity(best.Snapshot)
                                && IsBetterSetup(node, best)))
                {
                    best = node;
                }
            }
            return best;
        }

        /// <summary>
        /// A multi-card choice can produce several exact states which the compressed-deck
        /// comparator genuinely cannot order. Picking the first such state makes search quality
        /// depend on parallel enumeration order. Keep a bounded, canonical and round-robin
        /// ambiguity portfolio for one layer so ordinary expansion can expose the next action's
        /// real value. It consumes only the existing routing/effective-width budget and assigns
        /// no value to a card ID.
        /// </summary>
        private List<SearchNode> BuildAmbiguousCompressedChoicePortfolio(
            IReadOnlyList<SearchNode> nodes,
            int selectionLimit)
        {
            List<(AmbiguousChoiceDecisionSignature Decision, List<SearchNode> Variants)> cohorts = [];
            foreach (IGrouping<int, SearchNode> potionGroup in nodes
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                IReadOnlyList<SearchNode> group = potionGroup.ToList();
                SearchNode? winner = FindMostCompressedDeck(group);
                if (winner == null)
                    continue;

                List<(SearchNode Node, AmbiguousChoiceDecisionSignature Decision)> tied = [];
                foreach (SearchNode candidate in group)
                {
                    if (!HasEqualCompressedDeckRank(candidate, winner)
                        || !TryGetCurrentTurnRoutingChoice(
                            candidate,
                            out RoutingChoiceSignature choice,
                            out SearchNode choiceNode)
                        || !IsAmbiguousCompressedChoiceCardinality(
                            RoutingChoiceCardinality(choice))
                        || !ReferenceEquals(candidate, choiceNode)
                        || choiceNode.Parent is not { } parent)
                    {
                        continue;
                    }
                    tied.Add((
                        candidate,
                        BuildAmbiguousChoiceDecisionSignature(
                            candidate,
                            parent,
                            choice)));
                }

                foreach (IGrouping<AmbiguousChoiceDecisionSignature,
                             (SearchNode Node, AmbiguousChoiceDecisionSignature Decision)> decisionGroup in
                         tied.GroupBy(item => item.Decision))
                {
                    List<SearchNode> variants = decisionGroup
                        .GroupBy(item => item.Node.Snapshot.UnorderedPileKey)
                        .Select(outcome => outcome
                            .Select(item => item.Node)
                            .OrderBy(candidate => candidate.StateKey.First)
                            .ThenBy(candidate => candidate.StateKey.Second)
                            .First())
                        .OrderBy(candidate => candidate.StateKey.First)
                        .ThenBy(candidate => candidate.StateKey.Second)
                        .ToList();
                    if (variants.Count > 1)
                        cohorts.Add((decisionGroup.Key, variants));
                }
            }

            int limit = BoundedAmbiguousCompressedChoiceQuota(selectionLimit);
            List<(AmbiguousChoiceDecisionSignature Decision, List<SearchNode> Variants)> ordered = cohorts
                .OrderBy(cohort => cohort.Decision.PotionCount)
                .ThenBy(cohort => cohort.Decision.ParentStateKey.First)
                .ThenBy(cohort => cohort.Decision.ParentStateKey.Second)
                .ThenBy(cohort => cohort.Decision.ParentActionCount)
                .ThenBy(cohort => cohort.Decision.Turn)
                .ThenBy(cohort => cohort.Decision.SourceId, StringComparer.Ordinal)
                .ThenBy(cohort => cohort.Decision.Effect)
                .ThenBy(cohort => cohort.Decision.Pile)
                .ThenByDescending(cohort => cohort.Decision.ChoiceCount)
                .ThenBy(cohort => cohort.Decision.ContextId, StringComparer.Ordinal)
                .ToList();
            List<SearchNode> selected = new(limit);
            int round = 0;
            while (selected.Count < limit
                   && ordered.Any(cohort => round < cohort.Variants.Count))
            {
                foreach ((AmbiguousChoiceDecisionSignature _, List<SearchNode> variants) in ordered)
                {
                    if (round < variants.Count)
                        AddRoutingCandidate(selected, variants[round], limit);
                    if (selected.Count >= limit)
                        break;
                }
                round++;
            }
            return selected;
        }

        internal static int BoundedAmbiguousCompressedChoiceQuota(int beamWidth)
        {
            if (beamWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(beamWidth));
            return Math.Min(AmbiguousCompressedChoiceLimit, beamWidth / 3);
        }

        /// <summary>
        /// Single-card outcomes already have an exact option identity and receive fair service
        /// from the ordinary option round-robin. The ambiguity portfolio is needed only after a
        /// multi-card decision has deliberately been collapsed to cardinality, where several
        /// selected sets can otherwise remain indistinguishable to that scheduler.
        /// </summary>
        internal static bool IsAmbiguousCompressedChoiceCardinality(int choiceCardinality)
        {
            if (choiceCardinality < 0)
                throw new ArgumentOutOfRangeException(nameof(choiceCardinality));
            return choiceCardinality > 1;
        }

        private static AmbiguousChoiceDecisionSignature
            BuildAmbiguousChoiceDecisionSignature(
                SearchNode node,
                SearchNode parent,
                RoutingChoiceSignature choice)
            => new(
                node.PotionCount,
                parent.StateKey,
                parent.ActionCount,
                choice.Turn,
                choice.SourceId,
                choice.Effect,
                choice.Pile,
                RoutingChoiceCardinality(choice),
                choice.ContextId);

        private static bool HasEqualCompressedDeckRank(
            SearchNode left,
            SearchNode right)
            => left.Snapshot.LiveDeckSize == right.Snapshot.LiveDeckSize
                && AttackDensity(left.Snapshot) == AttackDensity(right.Snapshot)
                && SetupLaneValue(left.Snapshot) == SetupLaneValue(right.Snapshot)
                && left.Snapshot.RetainedAttackValue == right.Snapshot.RetainedAttackValue
                && left.Snapshot.ProjectedPlayerHp == right.Snapshot.ProjectedPlayerHp
                && left.Score.Equals(right.Score);

        private static string PotionUseLineageKey(SearchNode node)
            => string.Join(',', node.Actions
                .Where(action => action.Kind == PlanActionKind.UsePotion)
                .Select(action => action.PotionId
                    ?? throw new InvalidOperationException("用药动作缺少药水 ID。"))
                .OrderBy(static id => id, StringComparer.Ordinal));

        private static SearchNode? FindBestPotionLineage(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.AllEnemiesDead && !best.Snapshot.AllEnemiesDead
                    || node.Snapshot.AllEnemiesDead == best.Snapshot.AllEnemiesDead
                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                        && node.Score > best.Score))
                        ? node
                        : best);

        private static SearchNode? FindBestTacticalEnabler(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.ZeroCostPlayableCount > best.Snapshot.ZeroCostPlayableCount
                    || node.Snapshot.ZeroCostPlayableCount == best.Snapshot.ZeroCostPlayableCount
                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                && (node.Snapshot.HandCount > best.Snapshot.HandCount
                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                        && IsBetterSearchNode(node, best))))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindBestTurnBoundaryHand(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.OstyHp > best.Snapshot.OstyHp
                            || node.Snapshot.OstyHp == best.Snapshot.OstyHp
                                && (node.Snapshot.HandCount > best.Snapshot.HandCount
                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                                        && node.Score > best.Score))))
                    ? node
                    : best);

        private static SearchNode? FindBestCuratedTurnBoundaryHand(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.OstyHp > best.Snapshot.OstyHp
                            || node.Snapshot.OstyHp == best.Snapshot.OstyHp
                                && (node.Snapshot.ProjectedShuffleOrderValue
                                        > best.Snapshot.ProjectedShuffleOrderValue
                                    || node.Snapshot.ProjectedShuffleOrderValue
                                        == best.Snapshot.ProjectedShuffleOrderValue
                                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                                && (node.Snapshot.HandCount < best.Snapshot.HandCount
                                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                                        && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                                            || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                                                && node.Score > best.Score)))))
                    ? node
                    : best);

        private SearchNode? FindBestCompressionAttackGrowth(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || RetainedAttackGrowth(node.Snapshot) > RetainedAttackGrowth(best.Snapshot)
                    || RetainedAttackGrowth(node.Snapshot) == RetainedAttackGrowth(best.Snapshot)
                        && (node.Snapshot.Energy > best.Snapshot.Energy
                            || node.Snapshot.Energy == best.Snapshot.Energy
                                && (node.Snapshot.FutureResourceValue > best.Snapshot.FutureResourceValue
                                    || node.Snapshot.FutureResourceValue == best.Snapshot.FutureResourceValue
                                        && (node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                                            || node.Snapshot.FocusTargetPressure ==
                                                best.Snapshot.FocusTargetPressure
                                                && node.Score > best.Score))))
                {
                    best = node;
                }
            }
            return best;
        }

        private SearchNode? PreferMostVulnerableTargetVariant(
            IReadOnlyList<SearchNode> nodes,
            SearchNode? candidate)
        {
            if (candidate?.Action is not { TargetCombatId: not null } candidateAction)
                return candidate;
            SearchNode? preferred = nodes
                .Where(node => node.Action is { } action
                    && action.Kind == candidateAction.Kind
                    && action.CardId == candidateAction.CardId
                    && action.PotionId == candidateAction.PotionId
                    && action.TargetCombatId == node.Snapshot.MostVulnerableTargetCombatId)
                .MaxBy(BeamRankScore);
            return preferred ?? candidate;
        }

        private static long AttackDensity(SimulationSnapshot snapshot)
            => (long)snapshot.RetainedAttackValue * 1024 / Math.Max(1, snapshot.LiveDeckSize);

        private static SearchNode? FindBestTargetSetup(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            int bestSetup = int.MinValue;
            foreach (SearchNode node in nodes)
            {
                int setup = SetupLaneValue(node.Snapshot);
                if (best == null
                    || setup > bestSetup
                    || setup == bestSetup
                        && (node.Snapshot.RetainedAttackValue > best.Snapshot.RetainedAttackValue
                            || node.Snapshot.RetainedAttackValue == best.Snapshot.RetainedAttackValue
                                && (node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                                    || node.Snapshot.FocusTargetPressure == best.Snapshot.FocusTargetPressure
                                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                                && node.Score > best.Score))))
                {
                    best = node;
                    bestSetup = setup;
                }
            }
            return best;
        }

        private static int SetupLaneValue(SimulationSnapshot snapshot)
            => snapshot.StrategicEffects.RetentionValue * 16
                + snapshot.LatentSetupValue * 8
                + snapshot.ReplayPotentialValue * 16
                + snapshot.FutureResourceValue;

        private static SearchNode? FindBestLane(IReadOnlyList<SearchNode> nodes, SearchRouteTraits trait)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (!node.Traits.HasFlag(trait))
                    continue;
                int value = LaneValue(node.Snapshot, trait);
                int bestValue = best == null ? int.MinValue : LaneValue(best.Snapshot, trait);
                if (best == null
                    || value > bestValue
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.AliveEnemyCount < best.Snapshot.AliveEnemyCount
                            || node.Snapshot.AliveEnemyCount == best.Snapshot.AliveEnemyCount
                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp && node.Score > best.Score)))
                {
                    best = node;
                }
            }
            return best;
        }

        private SearchNode? FindSafestHpInvestment(
            IEnumerable<SearchNode> nodes)
            => nodes
                .Where(node => node.Traits.HasFlag(SearchRouteTraits.HpInvestment))
                .OrderBy(node => node.FutureSoldHp)
                .ThenByDescending(BeamRankScore)
                .ThenByDescending(node => node.Snapshot.ProjectedPlayerHp)
                .FirstOrDefault();

        private static SearchNode? FindBestSetup(IEnumerable<SearchNode> nodes)
        {
            SearchNode? best = null;
            int bestValue = int.MinValue;
            foreach (SearchNode node in nodes)
            {
                int value = LaneValue(node.Snapshot, SearchRouteTraits.Scaling)
                    + LaneValue(node.Snapshot, SearchRouteTraits.Resource)
                    + LaneValue(node.Snapshot, SearchRouteTraits.Control);
                if (best == null
                    || value > bestValue
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && node.Score > best.Score)
                {
                    best = node;
                    bestValue = value;
                }
            }
            return best;
        }

        private static int LaneValue(SimulationSnapshot snapshot, SearchRouteTraits trait)
            => trait switch
            {
                SearchRouteTraits.Scaling => SetupLaneValue(snapshot) + snapshot.DelayedDamageValue,
                SearchRouteTraits.Resource => snapshot.Energy * 16
                    + snapshot.Stars * 8
                    + snapshot.HandCount
                    + snapshot.ReachableHandValue
                    + snapshot.FutureResourceValue
                    + snapshot.OstyHp * 16
                    + snapshot.OstyMaxHp * 4,
                SearchRouteTraits.LongTermResource => snapshot.LongTermResourceValue,
                SearchRouteTraits.Control => snapshot.SandpitRemaining * 32
                    + snapshot.EnemyStrengthSuppression * 32
                    + snapshot.EnemyWeakTurns * 8
                    + snapshot.FocusTargetVulnerableTurns * 4
                        * Math.Min(SolverWeights.VulnerableAttackWindowCap, snapshot.RetainedAttackValue)
                    + Math.Max(0, snapshot.EnemyVulnerableTurns - snapshot.FocusTargetVulnerableTurns)
                        * Math.Min(SolverWeights.VulnerableAttackWindowCap, snapshot.RetainedAttackValue)
                    + snapshot.DelayedDamageValue
                    - snapshot.LiveDeckClutter * 8,
                SearchRouteTraits.RevivalWindow => snapshot.RevivingEnemyCount * 1024
                    - snapshot.RawEnemyHp * 4
                    - snapshot.MaxCurrentEnemyHp * 8,
                SearchRouteTraits.DeclinedExtraTurn => 0,
                SearchRouteTraits.ReactiveDamage => snapshot.ReactiveDamageValue,
                SearchRouteTraits.EndTurnDeckCompression => snapshot.Energy * 64
                    + snapshot.FutureResourceValue * 16
                    + (int)Math.Min(int.MaxValue, AttackDensity(snapshot))
                    + snapshot.FocusTargetPressure
                    - snapshot.LiveDeckSize * 16,
                SearchRouteTraits.HpInvestment => snapshot.StrategicEffects.RetentionValue * 16
                    + snapshot.FutureResourceValue * 8
                    + snapshot.DelayedDamageValue * 8
                    + snapshot.FocusTargetPressure,
                _ => throw new ArgumentOutOfRangeException(nameof(trait), trait, null),
            };

        private SearchNode? FindBestStandPat(
            IReadOnlyList<SearchNode> nodes,
            SearchRouteTraits trait)
        {
            const int limit = 8;
            List<SearchNode> probes = nodes
                .Where(node => node.Traits.HasFlag(trait))
                .OrderByDescending(node => node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(node => LaneValue(node.Snapshot, trait))
                .ThenByDescending(node => node.Score)
                .Take(limit)
                .ToList();

            SearchNode? best = null;
            StandPatEvaluation bestEvaluation = default;
            foreach (SearchNode node in probes)
            {
                StandPatEvaluation evaluation = _evaluateStandPat(node);
                int evaluationValue = trait == SearchRouteTraits.Resource
                    ? evaluation.ResourceValue
                    : evaluation.DelayedDamage;
                int bestEvaluationValue = trait == SearchRouteTraits.Resource
                    ? bestEvaluation.ResourceValue
                    : bestEvaluation.DelayedDamage;
                if (best == null
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluationValue > bestEvaluationValue
                                    || evaluationValue == bestEvaluationValue
                                        && node.Score > best.Score)))
                {
                    best = node;
                    bestEvaluation = evaluation;
                }
            }
            return best;
        }

        private SearchNode? FindBestFreshResourceStandPat(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            StandPatEvaluation bestEvaluation = default;
            foreach (SearchNode node in nodes.Where(node => node.Parent is { } parent
                         && (node.Snapshot.FutureResourceValue > parent.Snapshot.FutureResourceValue
                             || node.Snapshot.StrategicEffects.ResourcePotential
                                > parent.Snapshot.StrategicEffects.ResourcePotential)))
            {
                StandPatEvaluation evaluation = _evaluateStandPat(node);
                if (best == null
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluation.ResourceValue > bestEvaluation.ResourceValue
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && node.Snapshot.CumulativePlayerHpLost
                                            < best.Snapshot.CumulativePlayerHpLost
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && node.Snapshot.CumulativePlayerHpLost
                                            == best.Snapshot.CumulativePlayerHpLost
                                        && node.Score > best.Score)))
                {
                    best = node;
                    bestEvaluation = evaluation;
                }
            }
            return best;
        }

        private bool MultiObjectiveDominates(SearchNode left, SearchNode right)
        {
            if (ReferenceEquals(left, right))
                return false;
            if (left.Snapshot.EnemyCombatDistributionKey != right.Snapshot.EnemyCombatDistributionKey
                || left.Snapshot.EnemyControlDistributionKey != right.Snapshot.EnemyControlDistributionKey
                || left.Snapshot.UnorderedPileKey != right.Snapshot.UnorderedPileKey)
            {
                return false;
            }
            bool noWorse = left.Snapshot.ProjectedPlayerHp >= right.Snapshot.ProjectedPlayerHp
                && left.Snapshot.PlayerMaxHp >= right.Snapshot.PlayerMaxHp
                && left.Snapshot.CumulativePlayerHpLost <= right.Snapshot.CumulativePlayerHpLost
                && left.Snapshot.LongTermResourceValue >= right.Snapshot.LongTermResourceValue
                && left.Snapshot.AngerCopiesGenerated <= right.Snapshot.AngerCopiesGenerated
                && (_theftPolicy != SolverTheftPolicy.PreserveResources
                    || left.Snapshot.OutstandingStolenResource <= right.Snapshot.OutstandingStolenResource)
                && left.Snapshot.AliveEnemyCount <= right.Snapshot.AliveEnemyCount
                && left.Snapshot.EnemyHp <= right.Snapshot.EnemyHp
                && left.Snapshot.RawEnemyHp <= right.Snapshot.RawEnemyHp
                && left.Snapshot.MaxCurrentEnemyHp <= right.Snapshot.MaxCurrentEnemyHp
                && left.Snapshot.PersistentBuffValue >= right.Snapshot.PersistentBuffValue
                && left.Snapshot.LatentSetupValue >= right.Snapshot.LatentSetupValue
                && left.Snapshot.DelayedDamageValue >= right.Snapshot.DelayedDamageValue
                && left.Snapshot.ReactiveDamageValue >= right.Snapshot.ReactiveDamageValue
                && left.Snapshot.EnemyStrengthSuppression >= right.Snapshot.EnemyStrengthSuppression
                && left.Snapshot.EnemyWeakTurns >= right.Snapshot.EnemyWeakTurns
                && left.Snapshot.EnemyVulnerableTurns >= right.Snapshot.EnemyVulnerableTurns
                && left.Snapshot.FocusTargetVulnerableTurns >= right.Snapshot.FocusTargetVulnerableTurns
                && left.Snapshot.Energy >= right.Snapshot.Energy
                && left.Snapshot.Stars >= right.Snapshot.Stars
                && left.Snapshot.FutureResourceValue >= right.Snapshot.FutureResourceValue
                && left.Snapshot.OstyHp >= right.Snapshot.OstyHp
                && left.Snapshot.OstyMaxHp >= right.Snapshot.OstyMaxHp
                && RetainedAttackGrowth(left.Snapshot) >= RetainedAttackGrowth(right.Snapshot)
                && left.Snapshot.ReplayPotentialValue >= right.Snapshot.ReplayPotentialValue
                && left.Snapshot.FocusTargetPressure >= right.Snapshot.FocusTargetPressure
                && left.Snapshot.SandpitRemaining >= right.Snapshot.SandpitRemaining
                && left.Snapshot.LiveDeckClutter <= right.Snapshot.LiveDeckClutter
                && left.Snapshot.LiveDeckSize <= right.Snapshot.LiveDeckSize
                && left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.ActionCount <= right.ActionCount;
            bool strictlyBetter = left.Snapshot.ProjectedPlayerHp > right.Snapshot.ProjectedPlayerHp
                || left.Snapshot.PlayerMaxHp > right.Snapshot.PlayerMaxHp
                || left.Snapshot.CumulativePlayerHpLost < right.Snapshot.CumulativePlayerHpLost
                || left.Snapshot.LongTermResourceValue > right.Snapshot.LongTermResourceValue
                || left.Snapshot.AngerCopiesGenerated < right.Snapshot.AngerCopiesGenerated
                || _theftPolicy == SolverTheftPolicy.PreserveResources
                    && left.Snapshot.OutstandingStolenResource < right.Snapshot.OutstandingStolenResource
                || left.Snapshot.AliveEnemyCount < right.Snapshot.AliveEnemyCount
                || left.Snapshot.EnemyHp < right.Snapshot.EnemyHp
                || left.Snapshot.RawEnemyHp < right.Snapshot.RawEnemyHp
                || left.Snapshot.MaxCurrentEnemyHp < right.Snapshot.MaxCurrentEnemyHp
                || left.Snapshot.PersistentBuffValue > right.Snapshot.PersistentBuffValue
                || left.Snapshot.LatentSetupValue > right.Snapshot.LatentSetupValue
                || left.Snapshot.DelayedDamageValue > right.Snapshot.DelayedDamageValue
                || left.Snapshot.ReactiveDamageValue > right.Snapshot.ReactiveDamageValue
                || left.Snapshot.EnemyStrengthSuppression > right.Snapshot.EnemyStrengthSuppression
                || left.Snapshot.EnemyWeakTurns > right.Snapshot.EnemyWeakTurns
                || left.Snapshot.EnemyVulnerableTurns > right.Snapshot.EnemyVulnerableTurns
                || left.Snapshot.FocusTargetVulnerableTurns > right.Snapshot.FocusTargetVulnerableTurns
                || left.Snapshot.Energy > right.Snapshot.Energy
                || left.Snapshot.Stars > right.Snapshot.Stars
                || left.Snapshot.FutureResourceValue > right.Snapshot.FutureResourceValue
                || left.Snapshot.OstyHp > right.Snapshot.OstyHp
                || left.Snapshot.OstyMaxHp > right.Snapshot.OstyMaxHp
                || RetainedAttackGrowth(left.Snapshot) > RetainedAttackGrowth(right.Snapshot)
                || left.Snapshot.ReplayPotentialValue > right.Snapshot.ReplayPotentialValue
                || left.Snapshot.FocusTargetPressure > right.Snapshot.FocusTargetPressure
                || left.Snapshot.SandpitRemaining > right.Snapshot.SandpitRemaining
                || left.Snapshot.LiveDeckClutter < right.Snapshot.LiveDeckClutter
                || left.Snapshot.LiveDeckSize < right.Snapshot.LiveDeckSize
                || left.PotionCount < right.PotionCount
                || left.PotionStrategicCost < right.PotionStrategicCost
                || left.FutureSoldHp < right.FutureSoldHp
                || left.ActionCount < right.ActionCount;
            return noWorse && strictlyBetter;
        }

        private static bool IsBetterSearchNode(SearchNode candidate, SearchNode current)
            => candidate.Score > current.Score
                || candidate.Score.Equals(current.Score) && candidate.ActionCount < current.ActionCount;

        private static bool UsesPotion(SearchNode node)
            => node.PotionCount > 0;

        private double BeamRankScore(SearchNode node)
        {
            int persistentBuffCap = _isActEndingBoss
                ? SolverWeights.PersistentBuffDeltaBeamCap
                : SolverWeights.StandardPersistentBuffDeltaBeamCap;
            double persistentBuffValue = _isActEndingBoss
                ? SolverWeights.PersistentBuffDeltaBeamValue
                : SolverWeights.StandardPersistentBuffDeltaBeamValue;
            bool useLatentSetup = _isActEndingBoss || _initialEnemyCount > 1;
            int strengthSuppressionHorizon = _isActEndingBoss
                ? SolverWeights.BossEnemyStrengthSuppressionHorizon
                : SolverWeights.StandardEnemyStrengthSuppressionHorizon;
            int weakExpectedHpSaved = _isActEndingBoss
                ? SolverWeights.BossEnemyWeakExpectedHpSaved
                : SolverWeights.StandardEnemyWeakExpectedHpSaved;
            return node.Score
                + Math.Min(SolverWeights.CurrentEnergyBeamCap, node.Snapshot.Energy)
                    * SolverWeights.CurrentEnergyBeamValue
                + Math.Min(
                        persistentBuffCap,
                        Math.Max(0, node.Snapshot.PersistentBuffValue - _run.InitialPersistentBuffValue))
                    * persistentBuffValue
                + (useLatentSetup
                    ? Math.Min(SolverWeights.LatentSetupBeamCap, node.Snapshot.LatentSetupValue)
                        * SolverWeights.LatentSetupBeamValue
                    : 0d)
                + (_isActEndingBoss
                    ? node.Snapshot.FutureResourceValue * SolverWeights.FutureResourceBeamValue
                    : 0d)
                + Math.Min(
                        SolverWeights.ReplayPotentialBeamCap,
                        node.Snapshot.ReplayPotentialValue)
                    * SolverWeights.ReplayPotentialBeamValue
                + RetainedAttackGrowth(node.Snapshot) * SolverWeights.RetainedAttackGrowthBeamValue
                + node.Snapshot.DelayedDamageValue * SolverWeights.DelayedDamageBeamValue
                + node.Snapshot.SandpitRemaining * SolverWeights.SandpitTurnBeamValue
                + Math.Min(
                        SolverWeights.EnemyStrengthSuppressionBeamCap,
                        Math.Max(
                            0,
                            node.Snapshot.EnemyStrengthSuppression
                            - _run.InitialEnemyStrengthSuppression))
                    * strengthSuppressionHorizon
                    * SolverWeights.Hp
                + Math.Min(
                        SolverWeights.EnemyWeakTurnsBeamCap,
                        Math.Max(0, node.Snapshot.EnemyWeakTurns - _run.InitialEnemyWeakTurns))
                    * weakExpectedHpSaved
                    * SolverWeights.Hp;
        }

        private int RetainedAttackGrowth(SimulationSnapshot snapshot)
            => Math.Min(
                SolverWeights.RetainedAttackGrowthBeamCap,
                Math.Max(0, snapshot.RetainedAttackValue - _run.InitialRetainedAttackValue));

    }

    internal static void VerifyRoutingChoicePortfolioBoundsForTesting()
    {
        if (BeamRetentionPolicy.BoundedRoutingChoiceQuota(0) != 0
            || BeamRetentionPolicy.BoundedRoutingChoiceQuota(42) != 42
            || BeamRetentionPolicy.BoundedRoutingChoiceQuota(200) != 96
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(0) != 0
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(6) != 2
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(12) != 4
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(60) != 20
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(135) != 45
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(300) != 48
            || BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(0)
            || BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(1)
            || !BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(2)
            || !BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(3))
        {
            throw new InvalidOperationException(
                "选牌歧义 portfolio 没有排除已有精确 option 身份的单卡选择，" +
                "或没有保留多卡三分之一公平子配额及硬上界。");
        }

        bool rejectedNegativeCardinality = false;
        try
        {
            BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectedNegativeCardinality = true;
        }
        if (!rejectedNegativeCardinality)
            throw new InvalidOperationException("选牌歧义 portfolio 接受了负 cardinality。");

        IReadOnlyList<IReadOnlyList<int>> optionContexts =
        [
            [10, 11, 12, 13],
            [20],
            [30, 31],
        ];
        int[] interleaved =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(optionContexts)];
        int[] repeated =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(
                optionContexts)];
        if (!interleaved.SequenceEqual([10, 11, 12, 13, 20, 30, 31])
            || !repeated.SequenceEqual(interleaved)
            || interleaved.Length != optionContexts.Sum(group => group.Count)
            || interleaved.Distinct().Count() != interleaved.Length)
        {
            throw new InvalidOperationException(
                "routing context 的 8-wide 分块调度不完整、不确定，或重复/遗漏了 context。");
        }

        IReadOnlyList<IReadOnlyList<int>> saturatedOptions = Enumerable.Range(0, 12)
            .Select(option => (IReadOnlyList<int>)Enumerable.Range(0, 12)
                .Select(context => option * 100 + context)
                .ToList())
            .ToList();
        int hardLimit = BeamRetentionPolicy.BoundedRoutingChoiceQuota(
            saturatedOptions.Sum(group => group.Count));
        int[] saturatedSchedule =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(saturatedOptions)];
        int[] boundedPrefix =
        [
            .. saturatedSchedule.Take(hardLimit),
        ];
        if (hardLimit != 96
            || boundedPrefix.Length != hardLimit
            || Enumerable.Range(0, 12).Any(option =>
                boundedPrefix.Count(value => value / 100 == option) != 8)
            || Enumerable.Range(0, 12).Any(option =>
                saturatedSchedule.Skip(hardLimit).Count(value => value / 100 == option) != 4))
        {
            throw new InvalidOperationException(
                "routing context 分块没有保持每轮每 option 至多 8 个，" +
                "或 12x12 输入在 96 硬上限内没有给每个 option 留出代表。");
        }
    }

    internal static void VerifyPotionQuotaReservationPolicyForTesting()
    {
        if (BeamRetentionPolicy.FeasiblePotionUseQuotas(1) != (1, 0)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(2) != (1, 1)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(3) != (1, 2)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(4) != (2, 2)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(5) != (2, 3)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(6) != (2, 4)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(135) != (45, 90))
        {
            throw new InvalidOperationException(
                "小 Beam 的双侧药水 quota 不可行，或标准三分之一分区发生变化。");
        }

        static SearchNode Candidate(int identity, bool usesPotion)
            => new(
                null,
                0,
                usesPotion ? 1 : 0,
                0,
                1,
                SearchRouteTraits.None,
                0,
                1000d - identity,
                new StateFingerprint((ulong)identity, 0),
                false,
                SearchBoundaryReason.None,
                false,
                null,
                null!,
                null!);

        List<SearchNode> used = Enumerable.Range(1, 6)
            .Select(identity => Candidate(identity, usesPotion: true))
            .ToList();
        List<SearchNode> unused = Enumerable.Range(101, 6)
            .Select(identity => Candidate(identity, usesPotion: false))
            .ToList();
        List<SearchNode> pool = [.. used, .. unused];
        List<SearchNode> oneSlot = [unused[0]];
        HashSet<SearchNode> oneSlotReservations = new(ReferenceEqualityComparer.Instance);
        (int oneUsedQuota, int oneUnusedQuota) =
            BeamRetentionPolicy.FeasiblePotionUseQuotas(1);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            oneSlotReservations,
            pool,
            usesPotion: true,
            oneUsedQuota);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            oneSlotReservations,
            pool,
            usesPotion: false,
            oneUnusedQuota);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            oneSlot,
            pool,
            oneSlotReservations,
            usesPotion: true,
            oneUsedQuota);
        if (oneSlot.Count != 1 || oneSlot[0].PotionCount == 0)
        {
            throw new InvalidOperationException(
                "单槽 Beam 的零 quota 侧错误预约，阻止了可行的用药最低保障。");
        }

        List<SearchNode> selected = [.. used];
        HashSet<SearchNode> reservations = new(ReferenceEqualityComparer.Instance);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            reservations,
            pool,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            reservations,
            pool,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            selected,
            pool,
            reservations,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            selected,
            pool,
            reservations,
            usesPotion: false,
            quota: 4);
        if (selected.Count(node => node.PotionCount > 0) != 2
            || selected.Count(node => node.PotionCount == 0) != 4
            || selected.Distinct(ReferenceEqualityComparer.Instance).Count() != selected.Count
            || !selected.Contains(used[0], ReferenceEqualityComparer.Instance)
            || !selected.Contains(used[1], ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "双侧药水 quota 没有同时保护两类质量最高的最低保障集合。");
        }

        List<SearchNode> reverseSelected = [.. unused];
        BeamRetentionPolicy.EnforcePotionUseQuota(
            reverseSelected,
            pool,
            reservations,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            reverseSelected,
            pool,
            reservations,
            usesPotion: true,
            quota: 2);
        if (reverseSelected.Count(node => node.PotionCount > 0) != 2
            || reverseSelected.Count(node => node.PotionCount == 0) != 4
            || reverseSelected.Distinct(ReferenceEqualityComparer.Instance).Count()
                != reverseSelected.Count
            || !reverseSelected.Contains(used[0], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(used[1], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[0], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[1], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[2], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[3], ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "双侧药水 quota 的结果依赖补齐调用顺序或删除了反侧预约路线。");
        }

        SearchNode requiredUsed = used[^1];
        List<SearchNode> constrained = [.. used];
        HashSet<SearchNode> constrainedReservations = new(
            [requiredUsed],
            ReferenceEqualityComparer.Instance);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            constrainedReservations,
            pool,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            constrainedReservations,
            pool,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            constrained,
            pool,
            constrainedReservations,
            usesPotion: false,
            quota: 4);
        if (!constrained.Contains(requiredUsed, ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "不可同时满足药水 quota 时删除了更高优先级的 required 路线。");
        }
    }

    internal void VerifyFinalPolicyQualificationRetentionForTesting(string potionId, int forcedSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(potionId);
        const int cohortHp = 37;
        if (BeamRetentionPolicy.FinalPolicyOptionalAmbergrisPlayerHpCohort(
                optionalAmbergrisCount: 1,
                playerHp: cohortHp) != cohortHp
            || BeamRetentionPolicy.FinalPolicyOptionalAmbergrisPlayerHpCohort(
                optionalAmbergrisCount: 0,
                playerHp: cohortHp) != int.MinValue
            || !BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                SolverTheftPolicy.PreserveResources,
                potionCount: 1,
                outstandingStolenResource: 2,
                potionFreeOutstandingResource: 3)
            || BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                SolverTheftPolicy.PreserveResources,
                potionCount: 1,
                outstandingStolenResource: 3,
                potionFreeOutstandingResource: 3)
            || BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                theftPolicy: null,
                potionCount: 1,
                outstandingStolenResource: 2,
                potionFreeOutstandingResource: 3))
        {
            throw new InvalidOperationException(
                "最终策略资格的 Ambergris 或偷窃分组不符合终局政策。 ");
        }
        using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
        SimulationSnapshot snapshot = Replay([]);
        try
        {
            int limit = checked(_profile.BeamWidth * 4);
            int potionCount = checked(snapshot.PotionUseCount + 1);
            CombatProgressState combatProgress = CombatProgressState.Capture(snapshot);
            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchNode rootNode = new(
                null,
                0,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.None,
                0,
                snapshot.Score,
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                terminal,
                null,
                snapshot,
                combatProgress);

            SearchNode MakeNode(
                SearchNode parent,
                PlanAction action,
                int actionCount,
                int candidatePotionCount)
                => new(
                    action,
                    actionCount,
                    candidatePotionCount,
                    snapshot.PotionStrategicCost,
                    _startTurnNumber,
                    SearchRouteTraits.None,
                    0,
                    snapshot.Score,
                    snapshot.StateKey,
                    snapshot.HasRisk,
                    snapshot.BoundaryReason,
                    terminal,
                    parent,
                    snapshot,
                    combatProgress);

            SearchNode sharedPrefix = MakeNode(
                rootNode,
                new PlanAction(
                    PlanActionKind.PlayCard,
                    _startTurnNumber,
                    CardId: "TEST.FINAL_POLICY_PREFIX"),
                1,
                snapshot.PotionUseCount);
            int ordinarySlot = forcedSlot == 0 ? 1 : 0;
            List<SearchNode> candidates = new(limit + 2);
            for (int index = 0; index <= limit; index++)
            {
                candidates.Add(MakeNode(
                    sharedPrefix,
                    new PlanAction(
                        PlanActionKind.UsePotion,
                        _startTurnNumber,
                        PotionSlot: ordinarySlot,
                        PotionId: potionId),
                    2,
                    potionCount));
            }
            SearchNode forcedPrefix = MakeNode(
                sharedPrefix,
                new PlanAction(
                    PlanActionKind.PlayCard,
                    _startTurnNumber,
                    CardId: "TEST.FINAL_POLICY_DELAY"),
                2,
                snapshot.PotionUseCount);
            SearchNode forcedCandidate = MakeNode(
                forcedPrefix,
                new PlanAction(
                    PlanActionKind.UsePotion,
                    _startTurnNumber,
                    PotionSlot: forcedSlot,
                    PotionId: potionId),
                3,
                potionCount);
            candidates.Add(forcedCandidate);

            List<SearchNode> ordinaryTop = Retention.RankBest(
                candidates,
                limit,
                finalQualityFirst: true);
            if (ordinaryTop.Any(node => ReferenceEquals(node, forcedCandidate)))
            {
                throw new InvalidOperationException(
                    "最终策略历史保留回归的普通 Top-N 截断前置条件没有成立。");
            }

            List<SearchNode> retained = Retention.RankFinal(candidates);
            if (!retained.Any(node => ReferenceEquals(node, forcedCandidate)))
            {
                throw new InvalidOperationException(
                    "最终候选截断丢失了同药水不同槽位的策略历史代表。");
            }
            if (retained.Count > limit + 2)
            {
                throw new InvalidOperationException(
                    "未满足的强制用药历史没有折叠为有界资格分组。");
            }

            PotionStrategySnapshot forcedStrategy = new(
                SolverPotionPolicy.Smart,
                [new PotionSlotDirective(forcedSlot, potionId, SolverPotionDirective.Force)]);
            if (!forcedStrategy.EvaluateForcedUses(
                    forcedCandidate.Actions,
                    renewablePotionShapedRock: false).AllForcedUsesSatisfied
                || forcedStrategy.EvaluateForcedUses(
                    candidates[0].Actions,
                    renewablePotionShapedRock: false).AllForcedUsesSatisfied)
            {
                throw new InvalidOperationException(
                    "最终策略历史保留回归没有维持精确槽位的强制用药资格。");
            }
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }
}
