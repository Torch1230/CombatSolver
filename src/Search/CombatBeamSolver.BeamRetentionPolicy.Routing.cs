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
            foreach (IGrouping<StateFingerprint, SearchNode> group in selected)
            {
                if (ReferenceEquals(group, candidate))
                    return;
            }
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

        private double MaximumRoutingBeamScore(IReadOnlyList<SearchNode> nodes)
        {
            if (nodes is RoutingChoiceNodes { RankSummary: { } summary })
            {
                _run.RoutingChoiceSummaryHits++;
                return summary.MaximumBeamScore;
            }
            _run.RoutingChoiceSummaryBypasses++;
            return nodes.Max(BeamRankScore);
        }

        private double RoutingParentScore(IReadOnlyList<SearchNode> nodes)
        {
            if (nodes is RoutingChoiceNodes { RankSummary: { } summary })
            {
                _run.RoutingChoiceSummaryHits++;
                return summary.MaximumParentScore;
            }
            _run.RoutingChoiceSummaryBypasses++;
            return ComputeRoutingParentScore(nodes);
        }

        private int RoutingParentRetentionRank(IReadOnlyList<SearchNode> nodes)
        {
            if (nodes is RoutingChoiceNodes { RankSummary: { } summary })
            {
                _run.RoutingChoiceSummaryHits++;
                return summary.MinimumParentRank;
            }
            _run.RoutingChoiceSummaryBypasses++;
            return ComputeRoutingParentRetentionRank(nodes);
        }

        private double ComputeRoutingParentScore(IReadOnlyList<SearchNode> nodes)
            => nodes.Max(node =>
            {
                if (TryGetRetainedRoutingChoice(node, out _, out SearchNode choiceNode)
                    && choiceNode.Parent is { } choiceParent)
                {
                    return BeamRankScore(choiceParent);
                }
                return BeamRankScore(node);
            });

        private static int ComputeRoutingParentRetentionRank(IReadOnlyList<SearchNode> nodes)
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
                .ThenByDescending(group => group.Max(pair => MaximumRoutingBeamScore(pair.Value)))
                .Select(group => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)group
                    .OrderBy(pair => RoutingParentRetentionRank(pair.Value))
                    .ThenByDescending(pair => RoutingParentScore(pair.Value))
                    .ThenByDescending(pair => MaximumRoutingBeamScore(pair.Value))
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
                // Action turns are monotonic along the parent chain. An end-turn action
                // may hold next-turn choices, so retain that one-turn boundary; everything
                // older is already rejected by TryBuildRoutingChoice's turn window.
                if (action.Turn < minimumChoiceTurn - 1)
                    break;
                // Enumerable.Reverse 先把整段选择缓冲成一个数组再倒着走。这两段本来就是可按
                // 下标访问的只读列表，直接倒序索引给出同样的访问序列与同样的首个命中即返回。
                if (action.TurnStartChoices is { Count: > 0 } turnStartChoices)
                {
                    for (int index = turnStartChoices.Count - 1; index >= 0; index--)
                    {
                        if (TryBuildRoutingChoice(
                                node,
                                cursor,
                                turnStartChoices[index],
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

                if (action.NestedChoices is { Count: > 0 } nestedChoices)
                {
                    for (int index = nestedChoices.Count - 1; index >= 0; index--)
                    {
                        if (TryBuildRoutingChoice(
                                node,
                                cursor,
                                nestedChoices[index],
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
            // 原来的 Any(lambda) 捕获 candidate，每次调用都要建闭包与委托；
            // ContainsReference 是同样的顺序扫描 + 短路，只是不分配。
            if (candidate == null
                || required.Count >= limit
                || ContainsReference(required, candidate))
            {
                return;
            }
            required.Add(candidate);
        }

        private static bool ContainsReference(IReadOnlyList<SearchNode> nodes, SearchNode candidate)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                if (ReferenceEquals(nodes[index], candidate))
                    return true;
            }
            return false;
        }

    }

}
