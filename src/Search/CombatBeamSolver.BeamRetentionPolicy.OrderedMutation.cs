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
            if (rawContinuationGroups.Count >= 4)
            {
                // The per-group packet build is read-only except for the deferred observation
                // requests, which are applied serially afterwards in group order. Packets are
                // written back by group index, so the flattened order is the input order.
                IReadOnlyList<OrderedMutationContinuationPacket>[] groupPackets =
                    new IReadOnlyList<OrderedMutationContinuationPacket>[rawContinuationGroups.Count];
                List<SearchNode>[] groupObservations = new List<SearchNode>[rawContinuationGroups.Count];
                ForEachRetentionIndex(rawContinuationGroups.Count,
                    ParallelExpansionWorkProfile.Kind.ContinuationPacket, index =>
                {
                    List<SearchNode> observations = [];
                    groupPackets[index] = BuildOrderedMutationContinuationPackets(
                        rawContinuationGroups[index],
                        selectedSet,
                        observations);
                    groupObservations[index] = observations;
                });
                for (int index = 0; index < rawContinuationGroups.Count; index++)
                {
                    foreach (SearchNode candidate in groupObservations[index])
                        RequestOrderedMutationObservation(candidate);
                    rawContinuationPackets.AddRange(groupPackets[index]);
                }
            }
            else
            {
                foreach (IGrouping<OrderedMutationContinuationLineageSignature, SearchNode> group in
                         rawContinuationGroups)
                {
                    foreach (OrderedMutationContinuationPacket packet in
                             BuildOrderedMutationContinuationPackets(group, selectedSet))
                    {
                        rawContinuationPackets.Add(packet);
                    }
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
            // Claims are coalesced by this immutable key. Hashing the record instead
            // walks mutable candidate/parent graphs and can change after admission.
            HashSet<OrderedMutationAdmissionClaimKey> appliedAdmissionClaims = [];
            foreach (OrderedMutationAdmissionClaim selectedClaim in admissionClaims
                         .Where(claim => HasPaidOrderedMutationAdmission(claim.Candidate)))
            {
                ApplyOrderedMutationAdmissionClaimOnce(appliedAdmissionClaims, selectedClaim);
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
                            if (HasPaidOrderedMutationAdmission(aliasedClaim.Candidate))
                                ApplyOrderedMutationAdmissionClaimOnce(appliedAdmissionClaims, aliasedClaim);
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
                    ApplyOrderedMutationAdmissionClaimOnce(appliedAdmissionClaims, claim);
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
                ApplyOrderedMutationAdmissionClaimOnce(appliedAdmissionClaims, claim);
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

        private static OrderedMutationContinuationPacket BuildNaturalOrderedMutationAdmissionPacket(
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

        private static OrderedMutationContinuationPacket BuildOrderedMutationHandoffPacket(
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

    }

}
