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

    }

    internal void VerifyNarrowOrderedPileCapacityForTesting(IReadOnlyList<SimulationSnapshot> snapshots)
    {
        List<SearchNode> nodes = snapshots.Select(snapshot => new SearchNode(
            new PlanAction(PlanActionKind.EndTurn, snapshot.Turn - 1), 5,
            snapshot.PotionUseCount, snapshot.PotionStrategicCost, snapshot.Turn,
            SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null,
            snapshot, CombatProgressState.Capture(snapshot))).ToList();
        List<SearchNode> narrow = Retention.RankBest(nodes, 6, preserveDefensiveRoute: true);
        if (narrow.Count is < 6 or > 7
            || narrow.Distinct(ReferenceEqualityComparer.Instance).Count() != narrow.Count
            || narrow.Any(node => !nodes.Contains(node)))
            throw new InvalidOperationException("Narrow ordered-pile retention exceeded its capacity or lost candidate identity.");
        List<SearchNode> full = Retention.RankBest(nodes, nodes.Count, preserveDefensiveRoute: true);
        if (full.Count != nodes.Count)
            throw new InvalidOperationException("An unsaturated retention channel lost candidates.");
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

    internal static void VerifyPotionUseLineageKeyForTesting()
    {
        static SearchNode Root() => new(null, 0, 0, 0, 1, default, 0, 0,
            default, false, SearchBoundaryReason.None, false, null, null!, null!);
        static SearchNode Append(SearchNode parent, PlanActionKind kind, string? id = null)
            => new(new PlanAction(kind, parent.Turn, PotionId: id!), parent.ActionCount + 1,
                parent.PotionCount + (kind == PlanActionKind.UsePotion ? 1 : 0),
                0, parent.Turn, default, 0, 0, default, false,
                SearchBoundaryReason.None, false, parent, null!, null!);
        static void Verify(SearchNode node)
        {
            string actual = BeamRetentionPolicy.PotionUseLineageKey(node);
            if (node.HasMaterializedActionsForTesting)
                throw new InvalidOperationException("药水分组不应物化完整动作链。");
            string expected = string.Join(',', node.Actions
                .Where(action => action.Kind == PlanActionKind.UsePotion)
                .Select(action => action.PotionId
                    ?? throw new InvalidOperationException("用药动作缺少药水 ID。"))
                .OrderBy(static id => id, StringComparer.Ordinal));
            if (!string.Equals(actual, expected, StringComparison.Ordinal)
                || BeamRetentionPolicy.PotionUseLineageKey(node) != expected)
                throw new InvalidOperationException("药水谱系键与原完整历史算法不同。");
        }
        Verify(Root());
        foreach (string[] ids in new string[][] { ["Z"], ["Z", "A", "Z"], ["", "a", "A", "药水", ","] })
        {
            SearchNode node = Root();
            foreach (string id in ids)
            {
                for (int index = 0; index < 257; index++)
                    node = Append(node, index % 2 == 0 ? PlanActionKind.PlayCard : PlanActionKind.EndTurn);
                node = Append(node, PlanActionKind.UsePotion, id);
            }
            Verify(node);
        }
        SearchNode shared = Append(Root(), PlanActionKind.UsePotion, "ROOT");
        Verify(Append(shared, PlanActionKind.UsePotion, "LEFT"));
        Verify(Append(shared, PlanActionKind.UsePotion, "RIGHT"));
        if (shared.HasMaterializedActionsForTesting)
            throw new InvalidOperationException("药水分组不应物化共享父链。");
        try
        {
            BeamRetentionPolicy.PotionUseLineageKey(Append(Root(), PlanActionKind.UsePotion));
        }
        catch (InvalidOperationException exception) when (exception.Message == "用药动作缺少药水 ID。")
        {
            return;
        }
        throw new InvalidOperationException("药水 ID 缺失必须显式失败。");
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
