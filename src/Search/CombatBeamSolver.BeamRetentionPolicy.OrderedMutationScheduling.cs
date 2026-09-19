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
    private sealed partial class BeamRetentionPolicy
    {
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
            HashSet<SearchNode> selectedSet,
            List<SearchNode>? deferredObservations = null)
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
                        if (deferredObservations == null)
                            RequestOrderedMutationObservation(selectedCandidate);
                        else
                            deferredObservations.Add(selectedCandidate);
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

        private static OrderedMutationContinuationPacket
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

        private static OrderedMutationRetentionLease CreateOrderedMutationLease(
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

    }

}
