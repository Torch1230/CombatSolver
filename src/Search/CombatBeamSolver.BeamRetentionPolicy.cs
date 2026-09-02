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

        public List<SearchNode> RankBest(
            IEnumerable<SearchNode> nodes,
            int limit,
            bool preserveDefensiveRoute = false,
            bool finalQualityFirst = false)
        {
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
                : (left, right) =>
                {
                    int byScore = BeamRankScore(right).CompareTo(BeamRankScore(left));
                    return byScore != 0 ? byScore : left.ActionCount.CompareTo(right.ActionCount);
                });
            List<SearchNode> routingChoices = [];
            if (preserveDefensiveRoute)
            {
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
                            FindBestTargetPressure(familyNodes)));
                    AddRoutingCandidate(routingChoices, FindBestDeckCuration(familyNodes));
                    AddRoutingCandidate(routingChoices, FindBestSetup(familyNodes));
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
                        AddRoutingCandidate(routingChoices, optionLeader);
                    }
                }
                foreach (SearchNode candidate in BuildDirectRoutingChoiceExtremes(ranked))
                {
                    if (routingChoices.Count >= RoutingChoiceLimit)
                        break;
                    AddRoutingCandidate(routingChoices, candidate);
                }
                int routingRound = 0;
                while (routingChoices.Count < RoutingChoiceLimit
                    && paretoByRoutingChoice.Any(group => routingRound < group.Count))
                {
                    foreach (IReadOnlyList<SearchNode> group in paretoByRoutingChoice)
                    {
                        if (routingRound < group.Count)
                            AddRoutingCandidate(routingChoices, group[routingRound]);
                        if (routingChoices.Count >= RoutingChoiceLimit)
                            break;
                    }
                    routingRound++;
                }
            }
            if (ranked.Count <= limit)
            {
                AssignRetentionRanks(ranked, []);
                return ranked;
            }

            int effectiveLimit = limit;
            bool preserveOrderedPile = preserveDefensiveRoute
                && _profile.Phase == SolverSearchPhase.Deep
                && ranked.Any(node => node.Snapshot.PocketwatchCardThreshold >= 0);
            int routingChoiceQuota = preserveOrderedPile
                ? routingChoices.Count
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
                AddRequired(required, routingChoice, effectiveLimit);
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
            if (_potionPolicy != SolverPotionPolicy.Disabled
                && quotaPool.Any(UsesPotion)
                && quotaPool.Any(node => !UsesPotion(node)))
            {
                int usedPotionQuota = Math.Max(2, limit / 3);
                int unusedPotionQuota = Math.Max(usedPotionQuota, limit - usedPotionQuota);
                EnforcePotionUseQuota(ranked, quotaPool, required, usesPotion: true, usedPotionQuota);
                EnforcePotionUseQuota(ranked, quotaPool, required, usesPotion: false, unusedPotionQuota);
            }
            ranked.Sort(finalQualityFirst
                ? CompareFinalCandidates
                : (left, right) =>
                {
                    int byScore = BeamRankScore(right).CompareTo(BeamRankScore(left));
                    return byScore != 0 ? byScore : left.ActionCount.CompareTo(right.ActionCount);
                });
            AssignRetentionRanks(ranked, required);
            return ranked;
        }

        private static StateFingerprint BuildOrderedPileTacticalKey(SearchNode node)
        {
            SimulationSnapshot snapshot = node.Snapshot;
            StateFingerprintBuilder key = new();
            key.Add(node.Turn);
            key.Add(node.PotionCount);
            key.Add(node.PotionStrategicCost);
            key.Add(node.FutureSoldHp);
            key.Add(snapshot.PlayerHp);
            key.Add(snapshot.ProjectedPlayerHp);
            key.Add(snapshot.PlayerBlock);
            key.Add(snapshot.EnemyHp);
            key.Add(snapshot.RawEnemyHp);
            key.Add(snapshot.MaxCurrentEnemyHp);
            key.Add(snapshot.EnemyCombatDistributionKey.First);
            key.Add(snapshot.EnemyCombatDistributionKey.Second);
            key.Add(snapshot.AliveEnemyMask);
            key.Add(snapshot.RevivingEnemyCount);
            key.Add(snapshot.FocusTargetCombatId ?? uint.MaxValue);
            key.Add(snapshot.PersistentBuffValue);
            key.Add((int)snapshot.StrategicSetupTraits);
            key.Add(snapshot.FutureResourceValue);
            key.Add(snapshot.DelayedDamageValue);
            key.Add(snapshot.EnemyStrengthSuppression);
            key.Add(snapshot.EnemyWeakTurns);
            key.Add(snapshot.EnemyVulnerableTurns);
            key.Add(snapshot.FocusTargetVulnerableTurns);
            key.Add(snapshot.EnemyControlDistributionKey.First);
            key.Add(snapshot.EnemyControlDistributionKey.Second);
            key.Add(snapshot.SandpitRemaining);
            key.Add(snapshot.LiveDeckClutter);
            key.Add(snapshot.OutstandingStolenResource);
            key.Add(snapshot.Energy);
            key.Add(snapshot.Stars);
            key.Add(snapshot.HandCount);
            key.Add(snapshot.PocketwatchCardsPlayedThisTurn);
            key.Add(snapshot.PocketwatchCardsPlayedLastTurn);
            key.Add(snapshot.PocketwatchCardThreshold);
            key.Add(snapshot.ShufflesCrossed);
            key.Add((int)snapshot.BoundaryReason);
            key.Add(snapshot.UnorderedPileKey.First);
            key.Add(snapshot.UnorderedPileKey.Second);
            return key.Finish();
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

        private static void AddRoutingCandidate(List<SearchNode> selected, SearchNode? candidate)
        {
            if (candidate != null && !ContainsReference(selected, candidate))
                selected.Add(candidate);
        }

        private static void AddRoutingContext(
            List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> selected,
            KeyValuePair<RoutingChoiceSignature, List<SearchNode>> candidate)
        {
            if (!selected.Any(pair => pair.Key == candidate.Key))
                selected.Add(candidate);
        }

        private static bool IsPersistentRoutingEffect(PlanChoiceEffect effect)
            => effect is PlanChoiceEffect.SetFreeThisCombat
                or PlanChoiceEffect.Exhaust
                or PlanChoiceEffect.Transform
                or PlanChoiceEffect.Modify
                or PlanChoiceEffect.ApplyRetain;

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
                .GroupBy(pair => new RoutingChoiceOptionSignature(
                    pair.Key.CardId,
                    pair.Key.Upgrade,
                    pair.Key.CardStateKey))
                .OrderBy(group => group.Min(pair => RoutingParentRetentionRank(pair.Value)))
                .ThenByDescending(group => group.Max(pair => RoutingParentScore(pair.Value)))
                .ThenByDescending(group => group.Max(pair => pair.Value.Max(BeamRankScore)))
                .Select(group => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)group
                    .OrderBy(pair => RoutingParentRetentionRank(pair.Value))
                    .ThenByDescending(pair => RoutingParentScore(pair.Value))
                    .ThenByDescending(pair => pair.Value.Max(BeamRankScore))
                    .ToList())
                .ToList();
            List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> ordered = [];
            int roundStart = 0;
            while (optionGroups.Any(group => roundStart < group.Count))
            {
                foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> group in optionGroups)
                {
                    for (int index = roundStart;
                         index < Math.Min(group.Count, roundStart + PersistentRoutingContextRounds);
                         index++)
                    {
                        ordered.Add(group[index]);
                    }
                }
                roundStart += PersistentRoutingContextRounds;
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

        private static void EnforcePotionUseQuota(
            List<SearchNode> selected,
            IReadOnlyList<SearchNode> pool,
            IReadOnlyList<SearchNode> protectedNodes,
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
                    && !ContainsReference(protectedNodes, node));
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
                || choice.Effect is not (PlanChoiceEffect.MoveToHand
                    or PlanChoiceEffect.MoveToDrawTop
                    or PlanChoiceEffect.Discard
                    or PlanChoiceEffect.DiscardAndDraw
                    or PlanChoiceEffect.MoveToHandFreeThisTurn
                    or PlanChoiceEffect.SetFreeThisCombat
                    or PlanChoiceEffect.GenerateToHand
                    or PlanChoiceEffect.Exhaust
                    or PlanChoiceEffect.Transform
                    or PlanChoiceEffect.Modify
                    or PlanChoiceEffect.ApplyRetain))
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
