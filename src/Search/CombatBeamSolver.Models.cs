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
    private readonly record struct TranspositionLabel(
        int PotionCount,
        int PotionStrategicCost,
        int FutureSoldHp,
        int CumulativePlayerHpLost,
        int ActionCount,
        double Score);

    private sealed class TranspositionFrontier(TranspositionLabel first)
    {
        private readonly List<TranspositionLabel> _labels = [first];

        public bool TryAccept(TranspositionLabel next)
        {
            foreach (TranspositionLabel current in _labels)
            {
                if (Dominates(current, next))
                    return false;
            }
            _labels.RemoveAll(current => Dominates(next, current));
            _labels.Add(next);
            return true;
        }

        private static bool Dominates(TranspositionLabel left, TranspositionLabel right)
            => left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.CumulativePlayerHpLost <= right.CumulativePlayerHpLost
                && left.ActionCount <= right.ActionCount
                && left.Score >= right.Score;
    }

    private readonly record struct StandPatEvaluation(
        bool AllEnemiesDead,
        int DelayedDamage,
        int ProjectedPlayerHp,
        int ResourceValue);

    private readonly record struct ThreatFocus(
        uint? CombatId,
        int Pressure,
        int RemainingHp,
        int CurrentThreat,
        int TotalThreat,
        int IncomingHitCount);

    private sealed record CoverageSummary(
        IReadOnlyList<PredictionGap> Gaps,
        bool HasUncompensatedRisk)
    {
        public static CoverageSummary None { get; } = new([], false);
    }

    private sealed class SearchRunContext(
        bool measurePhasePerformance,
        SearchFramePressureSignal framePressureSignal)
    {
        public readonly SearchPerformanceMetrics Performance = new(measurePhasePerformance);
        public readonly SearchWorkPacer WorkPacer = new(framePressureSignal);
        public Dictionary<StateFingerprint, TranspositionFrontier> Transpositions = [];
        public Dictionary<StateFingerprint, TranspositionFrontier> ExpandedTranspositions = [];
        public Dictionary<StateFingerprint, StandPatEvaluation> StandPatCache = [];
        public Dictionary<(StateFingerprint State, int RoundIndex), int> ThreatProjectionCache = [];
        public Dictionary<PredictionRiskSignature, CoverageSummary> CoverageCache = [];
        public int Expanded;
        public int DominatedActionsPruned;
        public int TopQueueActionsDropped;
        public int ActionAdmissionRepresentativesProtected;
        public int DuplicateCardBranchesPruned;
        public int ChoiceBranchesEvaluated;
        public int ChoiceReplayAttempts;
        public int ChoiceReplayBudgetExhaustions;
        public int ShuffleBranchesPruned = 0;
        public int SoldHpBranchesPruned;
        public int HpInvestmentBranchesProtected;
        public int ReplayCount;
        public int ForkCount;
        public int TransitionCount;
        public int ReusedNodeSnapshots;
        public int TranspositionBranchesPruned;
        public int RepeatableNoProgressBranchesPruned;
        public int CycleShapesDetected;
        public int CycleProbeContinuationsExpanded;
        public int CycleCandidatesProtected;
        public int CycleContinuationsStopped;
        public int CrossTurnCandidatesProtected;
        public int CrossTurnContinuationsStopped;
        public int PrimaryIncumbentBranchesPruned;
        public int PrimaryIncumbentUpdates;
        public int StandPatProbes;
        public long OffThreadAllocatedBytes;
        public int ParallelExpansionWaves;
        public int ParallelExpansionWorkItems;
        public int MaxParallelExpansionConcurrency;
        public int ParallelActionReplayWaves;
        public int ParallelActionReplayWorkItems;
        public int MaxParallelActionReplayConcurrency;
        public int DeferredRoundChoiceActions;
        public int DeferredRoundChoiceLayerWidthTotal;
        public int MaxDeferredRoundChoiceLayerWidth;
        public int DeferredRoundChoiceFiniteQuotaFallbacks;
        public int DeferredRoundChoiceFinitePrimaryLayers;
        public int DeferredRoundChoiceFinitePendingFallbacks;
        public int ParallelRoundChoiceReplayWaves;
        public int ParallelRoundChoiceReplayWorkItems;
        public int MaxParallelRoundChoiceReplayConcurrency;
        public int NodeLimitSnapshotsReleased;
        public int InitialPersistentBuffValue;
        public int InitialEnemyStrengthSuppression;
        public int InitialEnemyWeakTurns;
        public int InitialRetainedAttackValue;

        public void ResetRebuildableCaches(IReadOnlyList<SearchNode> frontier)
        {
            Transpositions = [];
            foreach (SearchNode node in frontier)
            {
                Transpositions[node.StateKey] = new TranspositionFrontier(
                    new TranspositionLabel(
                        node.PotionCount,
                        node.PotionStrategicCost,
                        node.FutureSoldHp,
                        node.Snapshot.CumulativePlayerHpLost,
                        node.ActionCount,
                        node.Score));
            }
            ExpandedTranspositions = [];
            StandPatCache = [];
            ThreatProjectionCache = [];
            CoverageCache = [];
        }

        public void ResetReclaimableCaches()
        {
            StandPatCache = [];
            ThreatProjectionCache = [];
            CoverageCache = [];
        }
    }

    [Flags]
    private enum ActionOptionFamily
    {
        None = 0,
        ImmediateDefense = 1 << 0,
        ImmediateOffense = 1 << 1,
        ResourceAndCycle = 1 << 2,
        PersistentSetup = 1 << 3,
        Control = 1 << 4,
        TargetRemoval = 1 << 5,
        HpInvestment = 1 << 6,
    }

    private readonly record struct ActionCandidate(
        SearchNode Node,
        CardType CardType,
        uint? TargetCombatId,
        int EnergySpent,
        int StarsSpent,
        int Damage,
        int Block,
        int Hp,
        int MaxHp,
        int CumulativeHpLost,
        int LongTermResourceValue,
        int AngerCopiesGenerated,
        ActionOptionFamily OptionFamilies,
        bool IsPure,
        double NormalizedValue);

    [InlineArray(10)]
    private struct HandFingerprintBuffer
    {
        private StateFingerprint _element0;
    }

    private sealed record RouteAnnotations(
        IReadOnlyDictionary<int, int> HpLostByTurn,
        IReadOnlyDictionary<int, int> EnemyHpLostByTurn,
        IReadOnlyDictionary<int, int> SoldHpByTurn,
        IReadOnlyDictionary<int, int> MaxBlockByTurn,
        IReadOnlyDictionary<int, int> ActualBlockByTurn,
        IReadOnlyDictionary<int, int> EnergyLeftByTurn,
        IReadOnlyDictionary<int, int> PotionCountByTurn,
        IReadOnlyDictionary<int, int> PotionStrategicCostByTurn,
        IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction,
        int? CombatEndedTurn,
        int? DeathTurn);

    private readonly record struct SearchFeatures(
        bool AllEnemiesDead,
        int ProjectedPlayerHp,
        int PlayerHp,
        int PlayerMaxHp,
        int CumulativePlayerHpLost,
        int LongTermResourceValue,
        LongTermGoals LongTermGoals,
        LongTermGoals LongTermGoalCardsPlayed,
        int AngerCopiesGenerated,
        int PlayerBlock,
        int AliveEnemyCount,
        int EnemyHp,
        int RawEnemyHp,
        int MaxCurrentEnemyHp,
        int OutstandingStolenResource,
        int PersistentBuffValue,
        int LatentSetupValue,
        int FutureResourceValue,
        int RetainedAttackValue,
        int DelayedDamageValue,
        int ReactiveDamageValue,
        int SandpitRemaining,
        int LiveDeckClutter,
        int Energy,
        int Stars,
        int HandCount,
        int RevivingEnemyCount,
        int FocusTargetPressure,
        int FocusTargetRemainingHp,
        int FocusTargetCurrentThreat,
        int PotionCount,
        int FutureSoldHp,
        int ActionCount,
        double Score,
        SearchRouteTraits Traits,
        SearchBoundaryReason BoundaryReason)
    {
        public static SearchFeatures Capture(SearchNode node)
        {
            SimulationSnapshot snapshot = node.Snapshot;
            return new SearchFeatures(
                snapshot.AllEnemiesDead,
                snapshot.ProjectedPlayerHp,
                snapshot.PlayerHp,
                snapshot.PlayerMaxHp,
                snapshot.CumulativePlayerHpLost,
                snapshot.LongTermResourceValue,
                snapshot.LongTermGoals,
                snapshot.LongTermGoalCardsPlayed,
                snapshot.AngerCopiesGenerated,
                snapshot.PlayerBlock,
                snapshot.AliveEnemyCount,
                snapshot.EnemyHp,
                snapshot.RawEnemyHp,
                snapshot.MaxCurrentEnemyHp,
                snapshot.OutstandingStolenResource,
                snapshot.PersistentBuffValue,
                snapshot.LatentSetupValue,
                snapshot.FutureResourceValue,
                snapshot.RetainedAttackValue,
                snapshot.DelayedDamageValue,
                snapshot.ReactiveDamageValue,
                snapshot.SandpitRemaining,
                snapshot.LiveDeckClutter,
                snapshot.Energy,
                snapshot.Stars,
                snapshot.HandCount,
                snapshot.RevivingEnemyCount,
                snapshot.FocusTargetPressure,
                snapshot.FocusTargetRemainingHp,
                snapshot.FocusTargetCurrentThreat,
                node.PotionCount,
                node.FutureSoldHp,
                node.ActionCount,
                node.Score,
                node.Traits,
                snapshot.BoundaryReason);
        }
    }

    private sealed record FinalPlanCandidate(
        SearchNode Node,
        SimulationSnapshot Snapshot,
        SearchFeatures Features,
        int FutureSold,
        int BattleSold,
        int PotionCount,
        double Score);

    private sealed record FinalPlanSelection(
        FinalPlanCandidate Candidate,
        int PotionBranchesRejected,
        int PotionHpSaved,
        int PotionHpRequired,
        LongTermGoals RequiredLongTermGoals,
        LongTermGoals BankedLongTermGoals,
        int LongTermGoalHpPrice,
        int LongTermGoalPotionPrice,
        LongTermGoalOutcome LongTermGoalOutcome,
        int CompliantRouteCount);

    private sealed record PendingTurnOutcome(
        SearchNode Node,
        SearchNode TurnStart,
        int Turn,
        int HpLost,
        int ActualBlock,
        int EnergyLeft,
        ulong PotionSlotsUsed,
        bool IsComparable);

}
