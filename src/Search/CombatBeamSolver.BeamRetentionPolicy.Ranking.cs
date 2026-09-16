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
            int comparison = rightWon.CompareTo(leftWon);
            if (comparison != 0)
                return comparison;
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

            comparison = leftSnapshot.ProjectedDeathSaveUseCount.CompareTo(
                rightSnapshot.ProjectedDeathSaveUseCount);
            if (comparison != 0)
                return comparison;

            int recoveryComparison = TheftEncounterStrategy.CompareRecovery(_theftPolicy,
                leftWon, leftSnapshot.OutstandingStolenResource, rightWon, rightSnapshot.OutstandingStolenResource);
            if (recoveryComparison != 0)
                return recoveryComparison;
            comparison = SolverInterimResultOrdering.ComparePrimaryQuality(
                leftWon,
                StrategicHpDeficit(leftSnapshot, leftWon),
                leftWon ? CompletedCombatTurn(left) : null,
                rightWon,
                StrategicHpDeficit(rightSnapshot, rightWon),
                rightWon ? CompletedCombatTurn(right) : null,
                leftSnapshot.StrategyGoalHpCredit,
                rightSnapshot.StrategyGoalHpCredit,
                leftSnapshot.StrategyGoalCount,
                rightSnapshot.StrategyGoalCount,
                leftSnapshot.ProjectedDeathSaveUseCount,
                rightSnapshot.ProjectedDeathSaveUseCount);
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

        /// <summary>
        /// Route quality on the same axis the final ordering uses, so retention keeps the candidate that
        /// ordering would go on to pick.
        /// </summary>
        private int StrategicHpDeficit(SimulationSnapshot snapshot, bool completeVictory)
            => ActEndingBossPolicy.StrategicHpDeficit(
                snapshot.CumulativePlayerHpLost,
                Math.Max(0, _initialPlayerMaxHp - snapshot.PlayerMaxHp),
                snapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        _postCombatRelicHeal,
                        completeVictory,
                        snapshot.PlayerHp,
                        snapshot.PlayerMaxHp),
                _bossHpRelief,
                snapshot.DeathSaveHpRestored) - snapshot.StrategicHpCredit;

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

        internal static string PotionUseLineageKey(SearchNode node)
        {
            // Only the potion multiset participates in this key. Materializing Actions
            // would retain an array for the entire route on every grouped candidate.
            List<string>? potionIds = null;
            int actionCount = 0;
            for (SearchNode? current = node; current?.Action is { } action; current = current.Parent)
            {
                actionCount++;
                if (action.Kind == PlanActionKind.UsePotion)
                {
                    (potionIds ??= []).Add(action.PotionId
                        ?? throw new InvalidOperationException("用药动作缺少药水 ID。"));
                }
            }
            if (actionCount != node.ActionCount)
                throw new InvalidOperationException("搜索节点动作链长度不一致。");
            if (potionIds == null)
                return string.Empty;
            potionIds.Sort(StringComparer.Ordinal);
            return string.Join(',', potionIds);
        }

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

        /// <summary>
        /// 输出进度代表：按实际打掉的敌人血量（含受复活影响的等效血量）选一条"抢速度"路线。
        /// 现有各通道的主键几乎都是玩家投射血量，敌人血量只做末位 tie-break，大战损局面里
        /// 防守线会在每一层赢下全部代表席位，把"挨血换输出、更早击杀"的路线挤出 Beam，
        /// 实机表现就是同一分支上反复微调战损、输给敌人的成长。这里只加一个有名额的显式
        /// 席位，不改变任何评分或终局排序。同血量时优先真正掉的血、更少动作、更高分数。
        /// </summary>
        private static SearchNode? FindBestRaceProgress(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                        && (node.Snapshot.RawEnemyHp < best.Snapshot.RawEnemyHp
                            || node.Snapshot.RawEnemyHp == best.Snapshot.RawEnemyHp
                                && (node.ActionCount < best.ActionCount
                                    || node.ActionCount == best.ActionCount
                                        && node.Score > best.Score)))
                {
                    best = node;
                }
            }
            return best;
        }

        /// <summary>
        /// 安全引擎代表：在"接近本组最低已发生战损"的候选里选持续能力最完整的一个。
        /// 现有通道里最保血、最深引擎、最低敌人血量各自独立，实机零战损通关线所在的
        /// "满血 + 完整引擎"平衡态不在任何单通道的极值上，需要单独的席位保它。
        /// </summary>
        private static SearchNode? FindBestSafeSetup(IReadOnlyList<SearchNode> nodes)
        {
            int minimumLoss = int.MaxValue;
            foreach (SearchNode node in nodes)
                minimumLoss = Math.Min(minimumLoss, node.Snapshot.CumulativePlayerHpLost);
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (node.Snapshot.CumulativePlayerHpLost
                    > minimumLoss + SolverWeights.SafeSetupHpLossSlack)
                {
                    continue;
                }
                if (best == null
                    || node.Snapshot.PersistentBuffValue > best.Snapshot.PersistentBuffValue
                    || node.Snapshot.PersistentBuffValue == best.Snapshot.PersistentBuffValue
                        && (node.Snapshot.CumulativePlayerHpLost < best.Snapshot.CumulativePlayerHpLost
                            || node.Snapshot.CumulativePlayerHpLost == best.Snapshot.CumulativePlayerHpLost
                                && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                        && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                            || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                                && node.Score > best.Score))))
                {
                    best = node;
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

            _prepareStandPat?.Invoke(probes);
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
            List<SearchNode> probes = nodes.Where(node => node.Parent is { } parent
                         && (node.Snapshot.FutureResourceValue > parent.Snapshot.FutureResourceValue
                             || node.Snapshot.StrategicEffects.ResourcePotential
                                > parent.Snapshot.StrategicEffects.ResourcePotential)).ToList();
            _prepareStandPat?.Invoke(probes);
            SearchNode? best = null;
            StandPatEvaluation bestEvaluation = default;
            foreach (SearchNode node in probes)
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
                && left.Snapshot.StrategicHpCredit >= right.Snapshot.StrategicHpCredit
                && (left.Snapshot.RelicCounters.SatisfiedMask & right.Snapshot.RelicCounters.SatisfiedMask)
                    == right.Snapshot.RelicCounters.SatisfiedMask
                && left.Snapshot.StrategyGoalCount >= right.Snapshot.StrategyGoalCount
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
            // 基础分成员（见 SolverSearchProfile.BaseScoreOnly）：中途排序只用基础分；未置位时下面逐位不变。
            if (_profile.BaseScoreOnly)
                return node.Score;
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
                + PersistentBuffBeamValue(node.Snapshot, persistentBuffCap, persistentBuffValue)
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

        /// <summary>
        /// 持续能力通道：前 <paramref name="cap" /> 个单位按主单价计分，之后按溢出单价继续计分。
        /// 硬封顶会让"整套引擎"和"半套引擎"在 Beam 里同分；溢出单价保留完整引擎的区分度，
        /// 且每个单位的总权重有上界。终局排序不受影响。
        /// </summary>
        private double PersistentBuffBeamValue(
            SimulationSnapshot snapshot,
            int cap,
            double primaryValue)
        {
            int delta = Math.Max(0, snapshot.PersistentBuffValue - _run.InitialPersistentBuffValue);
            double overflowValue = _isActEndingBoss
                ? SolverWeights.PersistentBuffOverflowBeamValue
                : SolverWeights.StandardPersistentBuffOverflowBeamValue;
            return Math.Min(cap, delta) * primaryValue
                + Math.Max(0, delta - cap) * overflowValue;
        }

    }

}
