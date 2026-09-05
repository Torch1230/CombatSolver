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
    private static int AccumulateEnemyHpLost(
        SearchNode parent,
        SimulationSnapshot childSnapshot)
        => checked(parent.CumulativeEnemyHpLost
            + Math.Max(0, parent.Snapshot.RawEnemyHp - childSnapshot.RawEnemyHp));

    private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)
    {
        if (ended.Count == 0)
            return ended;

        int pathBoundaryId = ObserveSearchPathBoundaryInput(
            ended, SearchPathObservationStage.TurnInput, "before_turn_outcome_annotation");

        List<PendingTurnOutcome> pending = [];
        foreach (SearchNode node in ended)
        {
            SearchNode parent = node.Parent
                ?? throw new InvalidOperationException("回合结果节点没有父节点。");
            PlanAction action = node.Action
                ?? throw new InvalidOperationException("非根搜索节点缺少动作。");
            SearchNode turnStart = FindTurnStart(parent);
            bool endedByTurn = action.Kind == PlanActionKind.EndTurn || node.Turn > parent.Turn;
            int actualBlock = endedByTurn ? parent.Snapshot.PlayerBlock : node.Snapshot.PlayerBlock;
            int energyLeft = endedByTurn ? parent.Snapshot.Energy : node.Snapshot.Energy;
            bool comparable = node.Snapshot.BoundaryReason is not (
                SearchBoundaryReason.UnsupportedEffect or SearchBoundaryReason.PendingChoice);
            pending.Add(new PendingTurnOutcome(
                node,
                turnStart,
                action.Turn,
                Math.Max(
                    0,
                    node.Snapshot.CumulativePlayerHpLost
                    - turnStart.Snapshot.CumulativePlayerHpLost),
                actualBlock,
                energyLeft,
                CurrentTurnPotionSlotsUsed(turnStart, node),
                comparable));
        }

        int availableFutureSoldHp = Math.Max(0, SoldHpThreshold() - battleDamage.SoldHpCommitted);
        int absoluteFutureSoldHp = Math.Max(0, root.InitialPlayerHp - 1);
        List<SearchNode> annotated = [];
        foreach (IGrouping<(int Turn, StateFingerprint State, ulong PotionSlotsUsed), PendingTurnOutcome> group in pending.GroupBy(
                     item => (item.Turn, item.TurnStart.StateKey, item.PotionSlotsUsed)))
        {
            PendingTurnOutcome[] groupOutcomes = group.ToArray();
            IReadOnlyList<CrossTurnStandPatBaseline> publishedStandPatBaselines =
                groupOutcomes[0].TurnStart.CrossTurnStandPatBaselines ?? [];
            CrossTurnStandPatBaseline[] directStandPatBaselines = groupOutcomes
                .Where(item => item.IsComparable
                    && ReferenceEquals(item.Node.Parent, item.TurnStart)
                    && item.Node.Action is { Kind: PlanActionKind.EndTurn })
                .Select(item => new CrossTurnStandPatBaseline(
                    item.Node.StateKey,
                    MeasureCycleExitQuality(item.TurnStart, item.Node)))
                .ToArray();
            CrossTurnStandPatBaseline[] standPatBaselines = directStandPatBaselines
                .Concat(publishedStandPatBaselines)
                .Distinct()
                .ToArray();
            StateFingerprint[] standPatKeys = standPatBaselines
                .Select(baseline => baseline.StateKey)
                .Distinct()
                .ToArray();
            foreach (PendingTurnOutcome outcome in groupOutcomes)
            {
                AttachCrossTurnSemanticStateEvidence(
                    outcome.Node,
                    outcome.TurnStart,
                    standPatKeys,
                    standPatBaselines);
            }

            PendingTurnOutcome[] retained = groupOutcomes
                .Where(outcome =>
                {
                    if (!ShouldPruneCrossTurnNoProgress(outcome.Node))
                        return true;
                    _run.RepeatableNoProgressBranchesPruned++;
                    return false;
                })
                .ToArray();
            if (retained.Length == 0)
                continue;

            PendingTurnOutcome[] comparable = retained.Where(item => item.IsComparable).ToArray();
            int minimumHpLost = comparable.Length == 0 ? 0 : comparable.Min(item => item.HpLost);
            PendingTurnOutcome[] conservative = comparable
                .Where(item => item.HpLost == minimumHpLost)
                .ToArray();
            int maxBlock = retained.Max(item => item.ActualBlock);
            List<(PendingTurnOutcome Outcome, int FutureSold)> deferredInvestments = [];
            foreach (PendingTurnOutcome outcome in retained)
            {
                int soldThisTurn = outcome.IsComparable
                    ? Math.Max(0, outcome.HpLost - minimumHpLost)
                    : 0;
                int previousSold = outcome.Node.Parent!.FutureSoldHp;
                int futureSold = previousSold + soldThisTurn;
                bool exceedsPolicyThreshold = futureSold > availableFutureSoldHp;
                bool protectsInvestment = exceedsPolicyThreshold
                    && HasStrategicInvestmentPayoff(outcome, conservative);
                if (futureSold > absoluteFutureSoldHp)
                {
                    _run.SoldHpBranchesPruned++;
                    continue;
                }
                if (exceedsPolicyThreshold && !protectsInvestment)
                {
                    deferredInvestments.Add((outcome, futureSold));
                    continue;
                }
                if (protectsInvestment)
                    _run.HpInvestmentBranchesProtected++;
                annotated.Add(AnnotateTurnOutcome(
                    outcome,
                    soldThisTurn,
                    futureSold,
                    maxBlock,
                    exceedsPolicyThreshold));
            }

            // Immediate scalar payoff is not a proof that a route is worthwhile, and its
            // absence is not a proof that it is useless. Preserve a tiny structural portfolio
            // for delayed HP investments; the cross-turn lease supplies the hard time bound.
            List<(PendingTurnOutcome Outcome, int FutureSold)> deferredRepresentatives =
                deferredInvestments
                    .GroupBy(item => (
                        item.Outcome.Node.Snapshot.CycleShapeKey,
                        item.Outcome.Node.StateKey))
                    .Select(family => family
                        .OrderBy(item => item.FutureSold)
                        .ThenByDescending(item => item.Outcome.Node.ActionCount)
                        .ThenByDescending(item => item.Outcome.Node.Snapshot.ProjectedPlayerHp)
                        .ThenByDescending(item => item.Outcome.Node.Score)
                        .First())
                    .ToList();
            List<(PendingTurnOutcome Outcome, int FutureSold)> retainedDeferred = [];
            (PendingTurnOutcome Outcome, int FutureSold)? safest = deferredRepresentatives
                .OrderBy(item => item.FutureSold)
                .ThenByDescending(item => item.Outcome.Node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(item => item.Outcome.Node.ActionCount)
                .ThenByDescending(item => item.Outcome.Node.Score)
                .Select(item => ((PendingTurnOutcome, int)?)item)
                .FirstOrDefault();
            if (safest is { } safeInvestment)
                retainedDeferred.Add(safeInvestment);
            (PendingTurnOutcome Outcome, int FutureSold)? furthest = deferredRepresentatives
                .Where(item => !retainedDeferred.Contains(item))
                .OrderByDescending(item => item.Outcome.Node.ActionCount)
                .ThenBy(item => item.FutureSold)
                .ThenByDescending(item => item.Outcome.Node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(item => item.Outcome.Node.Score)
                .Select(item => ((PendingTurnOutcome, int)?)item)
                .FirstOrDefault();
            if (furthest is { } furthestInvestment)
                retainedDeferred.Add(furthestInvestment);
            _run.SoldHpBranchesPruned += deferredInvestments.Count - retainedDeferred.Count;
            foreach ((PendingTurnOutcome outcome, int futureSold) in retainedDeferred)
            {
                int soldThisTurn = Math.Max(0, futureSold - outcome.Node.Parent!.FutureSoldHp);
                _run.HpInvestmentBranchesProtected++;
                annotated.Add(AnnotateTurnOutcome(
                    outcome,
                    soldThisTurn,
                    futureSold,
                    maxBlock,
                    isInvestment: true));
            }
        }
        ObserveSearchPathTurnSelection(ended, annotated, pathBoundaryId);
        return annotated;
    }

    private SearchNode AnnotateTurnOutcome(
        PendingTurnOutcome outcome,
        int soldThisTurn,
        int futureSold,
        int maxBlock,
        bool isInvestment)
    {
        double scoreWithoutSoldPenalty = outcome.Node.Score
            - outcome.Node.FutureSoldHp * SoldHpPenalty();
        return outcome.Node with
        {
            FutureSoldHp = futureSold,
            Score = ApplySoldHpPenalty(scoreWithoutSoldPenalty, futureSold),
            Traits = isInvestment
                ? outcome.Node.Traits | SearchRouteTraits.HpInvestment
                : outcome.Node.Traits,
            Outcome = new TurnOutcome(
                outcome.Turn,
                outcome.HpLost,
                outcome.Node.CumulativeEnemyHpLost
                    - outcome.TurnStart.CumulativeEnemyHpLost,
                soldThisTurn,
                maxBlock,
                outcome.ActualBlock,
                outcome.EnergyLeft),
        };
    }

    private static bool HasStrategicInvestmentPayoff(
        PendingTurnOutcome outcome,
        IReadOnlyList<PendingTurnOutcome> conservative)
    {
        SimulationSnapshot candidate = outcome.Node.Snapshot;
        if (candidate.PlayerDead || candidate.ProjectedPlayerHp <= 0 || conservative.Count == 0)
            return false;
        CycleExitQuality candidateQuality = MeasureCycleExitQuality(
            outcome.TurnStart,
            outcome.Node);
        // Compare against real conservative routes one by one. Combining each route's best
        // coordinate into an unattainable synthetic baseline incorrectly deletes Pareto-safe
        // investments.
        return !conservative.Any(item => MeasureCycleExitQuality(
                item.TurnStart,
                item.Node)
            .DominatesOrEquals(candidateQuality));
    }

    private static ulong CurrentTurnPotionSlotsUsed(SearchNode turnStart, SearchNode outcome)
    {
        ulong slots = 0;
        int explicitUses = 0;
        for (SearchNode? node = outcome; node != null && !ReferenceEquals(node, turnStart); node = node.Parent)
        {
            PlanAction action = node.Action
                ?? throw new InvalidOperationException("卖血统计动作链提前抵达根节点。");
            if (action.Kind != PlanActionKind.UsePotion)
                continue;
            if ((uint)action.PotionSlot >= 63u)
                throw new InvalidOperationException($"药水槽位超出卖血分组范围：{action.PotionSlot}。");
            slots |= 1UL << action.PotionSlot;
            explicitUses++;
        }
        if (outcome.PotionCount - turnStart.PotionCount > explicitUses)
            slots |= 1UL << 63;
        return slots;
    }

    private RouteAnnotations BuildRouteAnnotations(
        SearchNode best,
        ActionRelicTriggerRecorder? killRecorder = null)
    {
        List<SearchNode> path = [];
        for (SearchNode? node = best; node?.Parent != null; node = node.Parent)
            path.Add(node);
        path.Reverse();

        Dictionary<int, int> losses = [];
        Dictionary<int, int> enemyHpLosses = [];
        Dictionary<int, int> sold = [];
        Dictionary<int, int> maxBlock = [];
        Dictionary<int, int> actualBlock = [];
        Dictionary<int, int> energy = [];
        Dictionary<int, int> potionCounts = [];
        Dictionary<int, int> potionCosts = [];
        Dictionary<int, IReadOnlyList<string>> kills = [];
        ulong aliveMask = root.InitialAliveEnemyMask;
        int? combatEndedTurn = null;
        int? deathTurn = null;

        foreach (SearchNode node in path)
        {
            SearchNode parent = node.Parent!;
            PlanAction action = node.Action
                ?? throw new InvalidOperationException("路线标注节点缺少动作。");
            int potionCount = node.PotionCount - parent.PotionCount;
            int potionCost = node.PotionStrategicCost - parent.PotionStrategicCost;
            if (potionCount > 0)
                potionCounts[action.Turn] = potionCounts.GetValueOrDefault(action.Turn) + potionCount;
            if (potionCost > 0)
                potionCosts[action.Turn] = potionCosts.GetValueOrDefault(action.Turn) + potionCost;
            ulong newlyKilledMask = aliveMask & ~node.Snapshot.AliveEnemyMask;
            if (action.IsExecutable && newlyKilledMask != 0)
            {
                List<string> newlyKilled = [];
                for (int enemyIndex = 0; enemyIndex < root.Enemies.Count; enemyIndex++)
                {
                    if ((newlyKilledMask & (1UL << enemyIndex)) != 0)
                        newlyKilled.Add(displayNames.Creature(root.Enemies[enemyIndex]));
                }
                kills[node.ActionCount - 1] = newlyKilled;
            }
            aliveMask = node.Snapshot.AliveEnemyMask;

            if (node.Outcome is { } outcome)
            {
                losses[outcome.Turn] = outcome.HpLost;
                enemyHpLosses[outcome.Turn] = outcome.EnemyHpLost;
                actualBlock[outcome.Turn] = outcome.ActualBlock;
                maxBlock[outcome.Turn] = outcome.MaxBlock;
                sold[outcome.Turn] = outcome.SoldHp;
                energy[outcome.Turn] = outcome.EnergyLeft;
            }
            if (combatEndedTurn == null
                && SolverInterimResultOrdering.IsCompleteVictory(
                    node.ActionCount,
                    node.Snapshot.AllEnemiesDead,
                    node.Snapshot.PlayerDead,
                    node.Snapshot.ProjectedPlayerHp)
                && node.Snapshot.BoundaryReason != SearchBoundaryReason.UnsupportedEffect)
            {
                combatEndedTurn = node.Snapshot.CombatEndedTurn;
            }
            if (deathTurn == null && node.Snapshot.PlayerDead)
                deathTurn = node.Snapshot.DeathTurn;
        }
        if (killRecorder != null)
        {
            Dictionary<int, IReadOnlyList<string>> attributedKills = [];
            foreach (SearchNode node in path)
            {
                int actionIndex = node.ActionCount - 1;
                IReadOnlyList<RecordedKill> recorded = killRecorder.KillsForAction(actionIndex);
                if (recorded.Count > 0)
                {
                    attributedKills[actionIndex] = recorded
                        .Select(kill =>
                        {
                            Creature? enemy = root.Enemies.FirstOrDefault(candidate => candidate.CombatId == kill.CombatId);
                            string targetName = enemy is null ? displayNames.Monster(kill.TargetId) : displayNames.Creature(enemy);
                            if (string.IsNullOrEmpty(targetName))
                                targetName = kill.TargetId;
                            return $"{targetName}（{displayNames.DamageSource(kill.Source)}）";
                        })
                        .ToArray();
                }
                else if (kills.TryGetValue(actionIndex, out IReadOnlyList<string>? fallback))
                {
                    attributedKills[actionIndex] = fallback
                        .Select(name => $"{name}（未知效果）")
                        .ToArray();
                }
            }
            kills = attributedKills;
        }

        return new RouteAnnotations(
            losses,
            enemyHpLosses,
            sold,
            maxBlock,
            actualBlock,
            energy,
            potionCounts,
            potionCosts,
            kills,
            combatEndedTurn,
            deathTurn);
    }

    private static SearchNode FindTurnStart(SearchNode node)
    {
        SearchNode current = node;
        while (current.Parent is { } parent && parent.Turn == current.Turn)
            current = parent;
        return current;
    }

    private int SoldHpThreshold()
        => ResolveSoldHpThreshold(
            root.InitialPlayerMaxHp,
            root.EncounterRoomType,
            _strategicBossHpRelief,
            _theftPolicy);

    internal static int ResolveSoldHpThreshold(
        int initialPlayerMaxHp,
        RoomType? encounterRoomType,
        BossHpRelief bossHpRelief,
        SolverTheftPolicy? theftPolicy)
    {
        if (theftPolicy == SolverTheftPolicy.PreserveResources)
            return initialPlayerMaxHp;
        int survivalLimit = Math.Max(0, initialPlayerMaxHp - 1);
        if (bossHpRelief == BossHpRelief.RunEnding)
            return survivalLimit;
        if (bossHpRelief == BossHpRelief.ActClearHeal)
        {
            return Math.Min(
                survivalLimit,
                ActEndingBossPolicy.RawHpRequiredForPersistentValue(
                    SolverWeights.BossSoldHpThreshold,
                    bossHpRelief));
        }
        return encounterRoomType switch
        {
            RoomType.Boss => SolverWeights.BossSoldHpThreshold,
            RoomType.Elite => SolverWeights.EliteSoldHpThreshold,
            _ => SolverWeights.NormalSoldHpThreshold,
        };
    }

    private double ApplySoldHpPenalty(double score, int futureSoldHp)
        => score + futureSoldHp * SoldHpPenalty();

    private static double SoldHpPenalty()
        => SolverWeights.SoldHpPenalty;

    private IReadOnlyList<CachedContinuation> BuildContinuations(SearchNode best)
    {
        List<CachedContinuation> continuations = [];
        List<SearchNode> path = [];
        for (SearchNode? node = best; node?.Parent != null; node = node.Parent)
            path.Add(node);
        path.Reverse();
        for (int pathIndex = 0; pathIndex < path.Count; pathIndex++)
        {
            SearchNode node = path[pathIndex];
            PlanAction action = node.Action
                ?? throw new InvalidOperationException("续用路径节点缺少动作。");
            if (action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn
                || node.Snapshot.PlayerDead
                || node.Snapshot.AllEnemiesDead
                || node.Snapshot.BoundaryReason != SearchBoundaryReason.None)
            {
                continue;
            }
            bool hasPlannedNextTurn = path
                .Skip(pathIndex + 1)
                .Any(later => later.Action?.Turn == node.Turn);
            if (!hasPlannedNextTurn)
                continue;
            int forecastOffset = node.Turn - _startTurnNumber;
            ContinuationStamp? expected = node.Snapshot.Continuation;
            if (expected == null)
            {
                SimulationSnapshot replayed = Replay(node.Actions);
                expected = ContinuationStamp.CapturePredicted(
                    _player,
                    replayed.Simulator,
                    node.Turn,
                    _forecast,
                    _startTurnNumber);
                replayed.ReleaseSimulator();
            }
            continuations.Add(new CachedContinuation(expected, node.Turn, forecastOffset));
        }
        return continuations;
    }

}
