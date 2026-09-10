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
using MegaCrit.Sts2.Core.Models.Potions;
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
    private SearchBoundaryReason AdvanceRound(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
    {
        if (!simulator.IsInProgress)
            return SearchBoundaryReason.None;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PlanCardChoice>? roundChoicePlans = turnStartChoices?
            .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse)
            .ToArray();
        TurnStartChoiceCursor roundChoices = new(roundChoicePlans);
        simulatedCombat.BeginActionChoices(roundChoices);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
        try
        {
        int roundHistoryEntryStart = simulator.History.Entries.Count;
        if (!simulatedCombat.TryPrepareExtraPlayerTurn(
                simulator,
                _player,
                out bool takingExtraTurn,
                out bool hasActiveEmotionChip))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        int etherealExhaustCount = simulatedCombat.CountEtherealCardsInHand(simulator, _player);
        {
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerEnd);
            bool playerTurnEndCompleted;
            using (_run.Performance.Measure(SearchMetricPhase.RoundEndSimulation))
                playerTurnEndCompleted = PlayerTurnEndLifecycle.RunPhaseOne(
                    simulator,
                    simulatedCombat,
                    _player,
                    [_player.Creature]);
            if (!playerTurnEndCompleted)
                return SearchBoundaryReason.PendingChoice;
            simulatedCombat.CommitHistoryCourseTurn(_player);
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
            // Finish the already-started phase-one compensation above, but do not
            // flush the hand or enter phase two after either native phase-one check ended combat.
            if (!simulator.IsInProgress)
                return SearchBoundaryReason.None;
            using (_run.Performance.Measure(SearchMetricPhase.RoundFlush))
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, _player);
            int turnEndShuffleEvents = simulator.ShuffleEventCount;
            bool playerTurnEndCompletedPhaseTwo;
            using (_run.Performance.Measure(SearchMetricPhase.RoundPlayerEndPowers))
            {
                playerTurnEndCompletedPhaseTwo = PlayerTurnEndLifecycle.RunPhaseTwo(
                    simulator,
                    simulatedCombat,
                    [_player.Creature],
                    etherealExhaustCount);
            }
            shufflesCrossed += simulator.ShuffleEventCount - turnEndShuffleEvents;
            if (!playerTurnEndCompletedPhaseTwo)
                return SearchBoundaryReason.PendingChoice;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
        }

        SimCreatureState simulatedPlayer = simulator.State.GetCreature(_player.Creature);
        if (!takingExtraTurn)
        {
            simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundEnemyTurn);
            Creature[] actingEnemies = simulatedCombat.Enemies.ToArray();
            {
                using SearchMeasurementScope enemyStart = _run.Performance.Measure(SearchMetricPhase.RoundEnemyStart);
                simulatedCombat.CurrentSide = CombatSide.Enemy;
                foreach (Creature enemy in simulatedCombat.Enemies)
                    simulatedCombat.BeginSideTurn(enemy);
                simulatedCombat.SnapshotPowerAmountsAtTurnStart(simulatedCombat.Enemies);
                // 怪物方开始回合时，上一怪物回合留下的格挡先清除。
                if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                foreach (Creature enemy in simulatedCombat.Enemies)
                {
                    SimCreatureState simulatedEnemy = simulator.State.GetCreature(enemy);
                    if (simulatedEnemy.Block > 0)
                    {
                        if (simulatedCombat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                            simulatedEnemy.DamageBlock(simulatedEnemy.Block, ValueProp.Move);
                        else
                            PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
                    }
                    if (!CorePowerSupport.TriggerAfterBlockCleared(
                            simulator,
                            simulatedCombat,
                            enemy))
                    {
                        return SearchBoundaryReason.PendingChoice;
                    }
                }
                bool decrementEnemyPlating = simulatedCombat.RoundNumber > 1;
                if (!simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Enemy,
                        simulatedCombat.Enemies,
                        decrementEnemyPlating))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int enemyPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    enemyPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
            }
            // Vanilla checks after the entire enemy-side start, not between listeners.
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                return SearchBoundaryReason.None;
            Dictionary<Creature, MoveState> performedMoves;
            {
                using SearchMeasurementScope enemyMoves = _run.Performance.Measure(SearchMetricPhase.RoundEnemyMoves);
                performedMoves = new Dictionary<Creature, MoveState>(actingEnemies.Length);
                foreach (Creature actingEnemy in actingEnemies)
                {
                    if (!simulatedCombat.CanPerformMonsterMove(simulator, actingEnemy))
                        continue;
                    ForecastMove move = simulatedCombat.CurrentMonsterMove(actingEnemy);
                    if (simulatedCombat.ConsumeStunNextMove(actingEnemy))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                            return SearchBoundaryReason.None;
                        continue;
                    }
                    if (simulatedCombat.TryConsumeForcedMonsterMove(actingEnemy, out string forcedMove, out int forcedDamage))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (forcedMove == "EXPLODE_MOVE")
                        {
                            MonsterMoveSemantics.DamagePlayer(
                                simulator,
                                simulatedCombat,
                                move.Owner,
                                _player.Creature,
                                forcedDamage);
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            using (simulator.PushDamageSource(
                                CombatDamageSource.For(
                                    CombatDamageSourceKind.MonsterMove,
                                    move.Owner.Monster?.Id.Entry)))
                            {
                                simulator.Kill(move.Owner, force: true);
                            }
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                                    simulator,
                                    simulatedCombat,
                                    simulatedCombat.KnownEnemies,
                                    processedEnemyDeaths))
                            {
                                return SearchBoundaryReason.PendingChoice;
                            }
                        }
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                            return SearchBoundaryReason.None;
                        continue;
                    }
                    bool playerDied = MonsterMoveSemantics.ApplyForecastMove(
                            simulator,
                            simulatedCombat,
                            move,
                            _player.Creature,
                            processedEnemyDeaths,
                            turnStartChoices);
                    performedMoves[actingEnemy] = move.Move;
                    if (move.Owner.CombatId is uint revivedCombatId
                        && simulator.State.GetCreature(move.Owner).IsAlive)
                    {
                        processedEnemyDeaths.Remove(revivedCombatId);
                    }
                    if (simulatedCombat.HasPendingChoice)
                        return SearchBoundaryReason.PendingChoice;
                    // ApplyForecastMove has completed its attack finally and command tails.
                    if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player))
                        || playerDied)
                        return SearchBoundaryReason.None;
                }
            }

            using (_run.Performance.Measure(SearchMetricPhase.RoundEnemyEndPowers))
            {
                if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                if (simulatedCombat.BattlewornDummyTimedOut)
                    return SearchBoundaryReason.EventDefeat;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int playerPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        [_player.Creature]))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    playerPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                simulatedCombat.ClearNoDraw(_player.Creature);
                simulatedCombat.RecordRelicRoundDamage(simulator, _player, roundHistoryEntryStart);
            }
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                return SearchBoundaryReason.None;
            simulatedCombat.PrepareMonsterMovesForNextRound(simulator, performedMoves);
        }
        else
        {
            // An extra turn advances the player's turn number too, so damage from the
            // just-finished turn becomes Emotion Chip's "previous turn" window.
            if (hasActiveEmotionChip)
                simulatedCombat.RecordRelicRoundDamage(simulator, _player, roundHistoryEntryStart);
            simulatedCombat.ConsumeExtraTurnSources(_player);
        }

        return AdvancePlayerTurnStart(
            simulator,
            simulatedCombat,
            playerState,
            simulatedPlayer,
            roundIndex,
            processedEnemyDeaths,
            ref shufflesCrossed,
            roundChoices,
            takingExtraTurn);
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private SearchBoundaryReason AdvancePlayerTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        SimPlayerCombatState playerState,
        SimCreatureState simulatedPlayer,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn)
    {
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
        simulatedCombat.CurrentSide = CombatSide.Player;
        if (!takingExtraTurn)
            simulatedCombat.RoundNumber++;
        simulatedCombat.AdvancePlayerTurn(_player);
        simulatedCombat.BeginSideTurn(_player.Creature);
        simulatedCombat.SnapshotPowerAmountsAtTurnStart([_player.Creature]);

        if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(
                simulator,
                simulatedCombat,
                [_player.Creature]))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                simulator,
                simulatedCombat,
                [_player.Creature]))
        {
            return SearchBoundaryReason.PendingChoice;
        }

        if (simulatedPlayer.Block > 0)
        {
            if (simulatedCombat.ShouldClearBlock(_player.Creature, out AbstractModel? preventer))
                simulatedPlayer.DamageBlock(simulatedPlayer.Block, ValueProp.Move);
            else
                PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                    simulator,
                    preventer,
                    _player.Creature);
        }
        if (!CorePowerSupport.TriggerAfterBlockCleared(
                simulator,
                simulatedCombat,
                _player.Creature))
        {
            return SearchBoundaryReason.PendingChoice;
        }

        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, _player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, _player)
            + simulatedCombat.ConsumeEnergyNextTurn(_player));
        if (simulatedCombat.HasPendingChoice
            || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, _player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        int beforeHandDrawShuffleEvents = simulator.ShuffleEventCount;
        bool sideTurnStartTriggeredEarly = false;
        using (roundChoices.BeforeNextTake(() =>
               {
                   sideTurnStartTriggeredEarly = true;
                   return simulatedCombat.TriggerSideTurnStart(
                       simulator,
                       CombatSide.Player,
                       [_player.Creature],
                       decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                       takingExtraTurn);
               }))
        {
            if (simulatedCombat.PrepareBeforeHandDraw(simulator, _player, roundChoices))
                return SearchBoundaryReason.PendingChoice;
            shufflesCrossed += simulator.ShuffleEventCount - beforeHandDrawShuffleEvents;
            int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(
                simulatedCombat,
                _player,
                CombatManager.baseHandDrawCount);
            int effectiveDraw = Math.Min(
                drawCount,
                simulatedCombat.GetMaxHandSize(_player) - playerState.Hand.Cards.Count);
            bool willShuffle = effectiveDraw > playerState.DrawPile.Cards.Count
                && !playerState.DiscardPile.IsEmpty;
            int historyEntryStart = simulator.History.Entries.Count;
            using (_run.Performance.Measure(SearchMetricPhase.RoundDraw))
                simulator.Draw(_player, drawCount, fromHandDraw: true);
            if (willShuffle)
                shufflesCrossed++;
            if (simulatedCombat.HasPendingChoice)
                return SearchBoundaryReason.PendingChoice;
            TriggeredPowerSupport.CompensateHistorySince(simulator, simulatedCombat, historyEntryStart);
            if (simulatedCombat.HasPendingChoice)
                return SearchBoundaryReason.PendingChoice;
            if (simulatedCombat.TriggerAfterPlayerTurnStart(
                    simulator,
                    _player.Creature,
                    roundChoices))
                return SearchBoundaryReason.PendingChoice;
            if (!sideTurnStartTriggeredEarly)
            {
                if (!simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Player,
                        [_player.Creature],
                        decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                        takingExtraTurn))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
            }
        }
        if (!CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                simulatedCombat.KnownEnemies,
                processedEnemyDeaths))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, _player);
        if (simulatedCombat.TriggerAutoPrePlayEarly(
                simulator,
                _player,
                _startTurnNumber + roundIndex + 1,
                roundChoices,
                processedEnemyDeaths))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        roundChoices.AssertConsumed();
        simulatedCombat.NormalizeAeonglassWithers(simulator);
        simulatedCombat.NormalizeCardAfflictions(simulator);
        IReadOnlyList<ForecastMove> nextMoves = simulatedCombat.CurrentMonsterMoves();
        simulatedCombat.SetPredictedEnemyIntents(
            nextMoves.Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
        simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
        return SearchBoundaryReason.None;
    }
}
