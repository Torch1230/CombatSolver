using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
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

    private sealed partial class BeamRetentionPolicy(
        SolverSearchProfile _profile,
        bool _isActEndingBoss,
        BossHpRelief _bossHpRelief,
        PostCombatRelicHealProfile _postCombatRelicHeal,
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
        Func<SearchNode, StandPatEvaluation> _evaluateStandPat,
        Action<IEnumerable<SearchNode>>? _prepareStandPat = null)
    {
        private void ForEachRetentionIndex(
            int count,
            ParallelExpansionWorkProfile.Kind kind,
            Action<int> evaluate)
        {
            if (count >= 4 && _run.ActiveParallelExpansion is { } executor)
                executor.EvaluateRetentionIndices(count, kind, evaluate);
            else
                for (int index = 0; index < count; index++)
                    evaluate(index);
        }

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
            List<SearchNode> candidates = nodes.Distinct((IEqualityComparer<SearchNode>)ReferenceEqualityComparer.Instance).ToList();
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
                            _bossHpRelief,
                            _postCombatRelicHeal,
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
                explicitPotionStrategicCost += _run.PotionStrategicCosts.Get(
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
                    forcedStrategicHpCost += _run.PotionStrategicCosts.Get(
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

        public static (int Value, int Count) GetLongTermResourceMaximum(
            IReadOnlyList<SearchNode> nodes)
        {
            int highestValue = int.MinValue;
            int highestCount = 0;
            for (int index = 0; index < nodes.Count; index++)
            {
                int value = nodes[index].Snapshot.LongTermResourceValue;
                if (value > highestValue)
                {
                    highestValue = value;
                    highestCount = 1;
                }
                else if (value == highestValue)
                {
                    highestCount++;
                }
            }
            return (highestValue, highestCount);
        }

        public List<SearchNode> RankLongTermResource(
            IReadOnlyList<SearchNode> nodes,
            int limit,
            (int Value, int Count) maximum)
        {
            if (maximum.Count == nodes.Count)
                return [];
            List<SearchNode> highest = new(maximum.Count);
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index].Snapshot.LongTermResourceValue == maximum.Value)
                    highest.Add(nodes[index]);
            }
            return RankBest(
                highest,
                limit,
                preserveDefensiveRoute: true);
        }

        // One signature lookup reaches both the ordered candidates and their five extrema.
        // Keep this as a List so the existing family/option ordering consumes the same sequence.
        private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>
        {
            public SearchNode BestScore = first;
            public SearchNode BestOffense = first;
            public SearchNode BestDefense = first;
            public SearchNode BestSetup = first;
            public SearchNode BestPileOrder = first;
            public RoutingRankSummary? RankSummary;
        }

        private readonly record struct RoutingRankSummary(
            double MaximumBeamScore, double MaximumParentScore, int MinimumParentRank);

        private sealed class RoutingChoiceScratch
        {
            public Dictionary<RoutingChoiceSignature, List<SearchNode>> NodesByChoice { get; } = [];
            public void Clear() => NodesByChoice.Clear();
        }

        private RoutingChoiceScratch? _routingChoiceScratch;

        private RoutingChoiceScratch RentRoutingChoiceScratch()
        {
            RoutingChoiceScratch? scratch = _routingChoiceScratch;
            if (scratch is null)
                return new RoutingChoiceScratch();
            _routingChoiceScratch = null;
            scratch.Clear();
            return scratch;
        }

        private void ReturnRoutingChoiceScratch(RoutingChoiceScratch scratch)
        {
            // 归还时清空，避免把这一轮的 SearchNode 一直钉在缓冲里。
            scratch.Clear();
            _routingChoiceScratch = scratch;
        }

        private Comparison<SearchNode>? _finalCandidateComparison;

        private void SortByBeamRank(List<SearchNode> ranked)
        {
            if (ranked.Count < 2)
                return;
            // Score inputs are frozen during this sort. Preserve the same List.Sort
            // comparison and tie behavior while evaluating the formula once per entry.
            List<(SearchNode Node, double Score)> scored = new(ranked.Count);
            foreach (SearchNode node in ranked)
                scored.Add((node, BeamRankScore(node)));
            scored.Sort(static (left, right) =>
            {
                return CompareBeamRankOrder(
                    left.Score, left.Node.Snapshot.OffensiveProgressValue, left.Node.ActionCount,
                    right.Score, right.Node.Snapshot.OffensiveProgressValue, right.Node.ActionCount);
            });
            for (int index = 0; index < ranked.Count; index++)
                ranked[index] = scored[index].Node;
        }

        private Comparison<SearchNode> FinalCandidateComparison
            => _finalCandidateComparison ??= CompareFinalCandidates;
        public List<SearchNode> RankDeferredCandidates(IEnumerable<SearchNode> nodes, int limit)
        {
            List<SearchNode> ranked = nodes.ToList();
            SortByBeamRank(ranked);
            if (ranked.Count > limit)
                ranked.RemoveRange(limit, ranked.Count - limit);
            return ranked;
        }

        public List<SearchNode> RankBest(
            IEnumerable<SearchNode> nodes,
            int limit,
            bool preserveDefensiveRoute = false,
            bool finalQualityFirst = false,
            bool useSecondRankBand = false,
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

            if (finalQualityFirst)
                ranked.Sort(FinalCandidateComparison);
            else
                SortByBeamRank(ranked);
            List<SearchNode> routingChoices = [];
            if (preserveDefensiveRoute)
            {
                foreach (SearchNode candidate in BuildAmbiguousCompressedChoicePortfolio(ranked, limit))
                    AddRoutingCandidate(routingChoices, candidate, RoutingChoiceLimit);
                RoutingChoiceScratch scratch = RentRoutingChoiceScratch();
                Dictionary<RoutingChoiceSignature, List<SearchNode>> nodesByRoutingChoice = scratch.NodesByChoice;
                // The routing signature is a pure walk of the node's parent chain, so it can be
                // computed off-thread; grouping stays serial to preserve insertion order.
                RoutingChoiceSignature?[] signatureByIndex = new RoutingChoiceSignature?[ranked.Count];
                if (ranked.Count >= 64)
                {
                    ForEachRetentionIndex(ranked.Count,
                        ParallelExpansionWorkProfile.Kind.RoutingSignature, index =>
                        signatureByIndex[index] = RetainedRoutingChoice(ranked[index]));
                }
                else
                {
                    for (int index = 0; index < ranked.Count; index++)
                        signatureByIndex[index] = RetainedRoutingChoice(ranked[index]);
                }
                for (int rankedIndex = 0; rankedIndex < ranked.Count; rankedIndex++)
                {
                    SearchNode node = ranked[rankedIndex];
                    RoutingChoiceSignature? signature = signatureByIndex[rankedIndex];
                    if (signature == null)
                        continue;
                    if (observedRoutingSignatures != null)
                        observedRoutingSignatures[node] = signature.Value;
                    if (!nodesByRoutingChoice.TryGetValue(signature.Value, out List<SearchNode>? routingNodes))
                    {
                        routingNodes = new RoutingChoiceNodes(node);
                        nodesByRoutingChoice.Add(signature.Value, routingNodes);
                    }
                    else
                    {
                        RoutingChoiceNodes group = (RoutingChoiceNodes)routingNodes;
                        if (IsBetterSearchNode(node, group.BestScore))
                            group.BestScore = node;
                        if (IsBetterOffensive(node, group.BestOffense))
                            group.BestOffense = node;
                        if (IsBetterDefensive(node, group.BestDefense))
                            group.BestDefense = node;
                        if (IsBetterSetup(node, group.BestSetup))
                            group.BestSetup = node;
                        if (node.Snapshot.ProjectedShuffleOrderValue > group.BestPileOrder.Snapshot.ProjectedShuffleOrderValue
                            || node.Snapshot.ProjectedShuffleOrderValue == group.BestPileOrder.Snapshot.ProjectedShuffleOrderValue
                                && IsBetterSearchNode(node, group.BestPileOrder))
                            group.BestPileOrder = node;
                    }
                    routingNodes.Add(node);
                }
                // The ordered groups are now complete. Parent ranks and score inputs remain
                // unchanged until AssignRetentionRanks, after this entire routing block.
                // Use the original reductions once, including their NaN behavior.
                RoutingChoiceNodes[] summaryGroups = nodesByRoutingChoice.Values
                    .Cast<RoutingChoiceNodes>().ToArray();
                ForEachRetentionIndex(summaryGroups.Length,
                    ParallelExpansionWorkProfile.Kind.RoutingSummary, index =>
                {
                    RoutingChoiceNodes group = summaryGroups[index];
                    group.RankSummary = new(
                        group.Max(BeamRankScore),
                        ComputeRoutingParentScore(group),
                        ComputeRoutingParentRetentionRank(group));
                });
                _run.RoutingChoiceSummaryBuilds += summaryGroups.Length;
                List<IReadOnlyList<SearchNode>> paretoByRoutingChoice = [];
                List<IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>> routingFamilies =
                    nodesByRoutingChoice
                        .OrderByDescending(pair => MaximumRoutingBeamScore(pair.Value))
                        .GroupBy(pair => BuildRoutingChoiceFamilySignature(pair.Key))
                        .OrderBy(family => family.Min(pair => RoutingParentRetentionRank(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => RoutingParentScore(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => MaximumRoutingBeamScore(pair.Value)))
                        .Select(family => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)
                            OrderRoutingChoiceEventContexts(family))
                        .ToList();
                // Each context comes from a unique dictionary key and belongs to exactly one
                // family. The only repeat is the persistent prefix emitted in the first pass.
                List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> orderedRoutingContexts =
                    new(nodesByRoutingChoice.Count);
                for (int round = 0; round < PersistentRoutingContextRounds; round++)
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in
                        routingFamilies.Where(family => IsPersistentRoutingEffect(family[0].Key.Effect)))
                    {
                        if (round < family.Count)
                            orderedRoutingContexts.Add(family[round]);
                    }
                }
                int routingContextRound = 0;
                while (routingFamilies.Any(family => routingContextRound < family.Count))
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in routingFamilies)
                    {
                        if (routingContextRound < family.Count
                            && (routingContextRound >= PersistentRoutingContextRounds
                                || !IsPersistentRoutingEffect(family[0].Key.Effect)))
                        {
                            orderedRoutingContexts.Add(family[routingContextRound]);
                        }
                    }
                    routingContextRound++;
                }
                List<SearchNode>[] paretoByContext = new List<SearchNode>[orderedRoutingContexts.Count];
                void BuildContextPareto(int contextIndex)
                {
                    List<SearchNode> routingNodes = orderedRoutingContexts[contextIndex].Value;
                    RoutingChoiceNodes group = (RoutingChoiceNodes)routingNodes;
                    SearchNode? bestDeckCuration = FindBestDeckCuration(routingNodes);
                    SearchNode? bestTargetPressure = PreferMostVulnerableTargetVariant(
                        routingNodes,
                        FindBestTargetPressure(routingNodes));
                    List<SearchNode> candidates = [];
                    if (routingNodes.Min(ActionsSinceRetainedRoutingChoice) <= 1)
                    {
                        AddRoutingCandidate(candidates, group.BestSetup);
                        AddRoutingCandidate(candidates, bestTargetPressure);
                    }
                    else
                    {
                        AddRoutingCandidate(candidates, bestTargetPressure);
                        AddRoutingCandidate(candidates, bestDeckCuration);
                        AddRoutingCandidate(candidates, group.BestSetup);
                    }
                    foreach (SearchNode node in routingNodes.Take(16))
                        AddRoutingCandidate(candidates, node);
                    AddRoutingCandidate(candidates, group.BestScore);
                    AddRoutingCandidate(candidates, group.BestOffense);
                    AddRoutingCandidate(candidates, group.BestDefense);
                    AddRoutingCandidate(candidates, group.BestPileOrder);
                    paretoByContext[contextIndex] = candidates
                        .Where(candidate => !candidates.Any(other =>
                            !ReferenceEquals(candidate, other)
                            && MultiObjectiveDominates(other, candidate)))
                        .ToList();
                }
                if (orderedRoutingContexts.Count >= 8)
                    ForEachRetentionIndex(orderedRoutingContexts.Count,
                        ParallelExpansionWorkProfile.Kind.RoutingPareto, BuildContextPareto);
                else
                    for (int contextIndex = 0; contextIndex < orderedRoutingContexts.Count; contextIndex++)
                        BuildContextPareto(contextIndex);
                foreach (List<SearchNode> pareto in paretoByContext)
                    paretoByRoutingChoice.Add(pareto);
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
                ReturnRoutingChoiceScratch(scratch);
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
                && ranked.Any(node => node.Snapshot.PocketwatchCardThreshold >= 0);
            int routingChoiceQuota = preserveOrderedPile
                ? BoundedRoutingChoiceQuota(routingChoices.Count)
                : _isActEndingBoss
                    ? Math.Max(10, (limit + 3) / 2)
                    : Math.Max(8, limit * 2 / 5);
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
            if (preserveDefensiveRoute)
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
            int orderedPileQuota = Math.Min(effectiveLimit, orderedPileCohorts.Count == 0
                ? 0
                : endTurnFrontier
                    || ranked.Any(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                    ? Math.Max(8, limit * 2 / 3)
                    : limit + 1);
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
            if (preserveDefensiveRoute)
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
            // 次段成员（见 SolverSearchProfile.SecondRankBand）：只由全局剪枝入口显式启用，
            // 把分数序前 effectiveLimit 位挪到队尾再截断，于是普通席位落在第 W+1 至 2W 位；挪走的
            // 一段只在后面候选不够时回填。quotaPool 仍是纯分数序，必保置换、边界多样化和药水配额照旧。
            if (_profile.SecondRankBand && useSecondRankBand)
                BeamWidthPortfolio.MoveLeadingBandToTail(ranked, effectiveLimit);
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
            AdmitPowerCommitmentRepresentatives(quotaPool, ranked, required, limit);
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
            if (finalQualityFirst)
                ranked.Sort(FinalCandidateComparison);
            else
                SortByBeamRank(ranked);
            observe?.Invoke(new GlobalRetentionDecision(
                quotaPool, required, routingChoices, ranked, limit, effectiveLimit,
                routingChoiceQuota, RoutingChoiceLimit,
                observedRoutingSignatures, observedOptionLeaders, BeamRankScore));
            AssignRetentionRanks(ranked, required);
            return ranked;
        }

        private void AdmitPowerCommitmentRepresentatives(
            IReadOnlyList<SearchNode> pool,
            List<SearchNode> selected,
            List<SearchNode> required,
            int limit)
        {
            if (_run.PowerCommitmentsCreated == 0)
                return;
            int quota = PowerCommitmentSeatPolicy.SeatQuota(
                limit,
                _profile.AggressivePowerCommitment);
            _run.PowerValuationCandidates += pool.Count(node => node.PowerCommitment != null);
            int retained = selected.Count(node => node.PowerCommitment != null);
            _run.PowerCommitmentSeatsPeak = Math.Max(
                _run.PowerCommitmentSeatsPeak,
                Math.Min(retained, quota));
            if (retained >= quota)
                return;

            foreach (SearchNode candidate in PowerCommitmentRetention.RankRepresentatives(pool, quota))
            {
                if (retained >= quota || ContainsReference(selected, candidate))
                    continue;
                int replaceIndex = selected.FindLastIndex(node =>
                    node.PowerCommitment == null
                    && !ContainsReference(required, node));
                if (replaceIndex < 0)
                    return;
                selected[replaceIndex] = candidate;
                AddRequired(required, candidate, limit);
                retained++;
                _run.PowerCommitmentsAdmitted++;
                _run.PowerCommitmentSeatsPeak = Math.Max(
                    _run.PowerCommitmentSeatsPeak,
                    retained);
            }
        }

    }

}
