using System.Globalization;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal enum ExternalRouteVerificationStatus
{
    Annotated,
    UnsupportedFutureContinuation,
    IllegalRoute,
}

internal readonly record struct ExternalRouteTurnExpectation(
    int Turn,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded);

internal readonly record struct ExternalRouteTerminalExpectation(
    int Turn,
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int ProjectedBattleHpLost,
    int? CombatEndedTurn,
    int? DeathTurn);

internal readonly record struct ExternalRoutePotionUseExpectation(
    int Turn,
    int Slot,
    string PotionId);

internal readonly record struct ExternalRouteSnapshotSummary(
    int Turn,
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int ProjectedPlayerHp,
    int DeathSaveRelicHpRestored,
    int DeathSavePotionHpRestored,
    int DeathSaveUseCount,
    int ProjectedDeathSaveUseCount,
    int LongTermResourceValue,
    int AngerCopiesGenerated,
    int GrowthHpCredit,
    RelicCounterEvaluation RelicCounters,
    GrowthValues GrowthRewards,
    int? UnrecoveredGold,
    bool ForfeitsEncounterGold,
    int? UnrecoveredCards,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int OutstandingStolenResource,
    int ShufflesCrossed,
    bool HasRisk,
    int PostCombatRelicHeal,
    int ProjectedBattleHpLost,
    IReadOnlyList<PredictionGap> PredictionGaps,
    IReadOnlyList<PredictedPotionUse> VerifiedPotionUses);

internal readonly record struct ExternalRouteVerificationResult(
    ExternalRouteVerificationStatus Status,
    IReadOnlyList<PlanAction> AnnotatedActions,
    IReadOnlyList<CachedContinuation> Continuations,
    int VerifiedPrefixActionCount,
    int VerifiedLastTurn,
    ExternalRouteSnapshotSummary? VerifiedPrefixSummary,
    string? FailureReason);

internal sealed partial class CombatBeamSolver
{
    private readonly record struct ReplayedPotionConsumption(
        int Turn,
        int Slot,
        string PotionId,
        bool Automatic);

    private ExternalChoiceBinding? CreateExternalActionChoiceBinding(string? actionSourceId)
        => _validateExternalChoicesAgainstSpec
            ? new ExternalChoiceBinding(actionSourceId, choice => _externalBoundActionChoices?.Add(choice))
            : null;

    private ExternalChoiceBinding? CreateExternalTurnStartChoiceBinding()
        => _validateExternalChoicesAgainstSpec
            ? new ExternalChoiceBinding(null, choice => _externalBoundTurnStartChoices?.Add(choice))
            : null;

    internal static ExternalRouteVerificationResult VerifyAndAnnotateExternalRoute(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        IReadOnlyList<PlanAction> actions,
        IReadOnlyDictionary<int, ExternalRouteTurnExpectation> turnExpectations,
        ExternalRouteTerminalExpectation terminalExpectation,
        IReadOnlyList<ExternalRoutePotionUseExpectation> expectedPotionUses,
        bool verifyCurrentTurnOnly,
        CancellationToken cancellationToken)
    {
        if (!ValidateActionTurnOrdering(actions, root.StartTurnNumber, out string? orderingFailure))
            return Illegal(orderingFailure!);

        int actionsToReplayCount = ResolveReplayCount(actions, root.StartTurnNumber, verifyCurrentTurnOnly, out string? countFailure);
        if (countFailure is not null)
            return Illegal(countFailure);

        using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
        CombatBeamSolver solver = new(root, displayNames, battleDamage, policy, cancellationToken);
        solver._validateExternalChoicesAgainstSpec = true;
        SimulationSnapshot? currentSnapshot = null;
        try
        {
            try
            {
                currentSnapshot = solver.Replay([], countTransition: false, allowExecutionCapture: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PredictionUnsupportedException ex)
            {
                return Illegal($"Root replay unsupported: {ex.GetType().Name}: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                return Illegal($"Root replay not supported: {ex.GetType().Name}: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return Illegal($"Root replay failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (currentSnapshot.BoundaryReason != SearchBoundaryReason.None)
                return Illegal($"Root snapshot reached unexpected boundary {currentSnapshot.BoundaryReason}.");

            CombatPredictionSimulator rootSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
            SimulatedCombatState rootCombat = (SimulatedCombatState)rootSim.State.CombatState;

            PlanAction[] annotatedActions = [.. actions];
            List<CachedContinuation> continuations = [];
            List<ReplayedPotionConsumption> replayedPotions = [];
            int observedPotionUseCount = rootCombat.PotionUses.Count;
            int verifiedPrefixActionCount = 0;
            int verifiedLastTurn = root.StartTurnNumber - 1;
            int verifiedPotionCountAtCheckpoint = 0;
            ExternalRouteSnapshotSummary? verifiedPrefixSummary = CaptureSummary(
                currentSnapshot,
                rootSim,
                rootCombat,
                root,
                battleDamage,
                actionCount: 0);
            int turnHpLost = 0;
            int turnHpRecovered = 0;
            int turnEnemyHpLost = 0;
            int explicitPotionUses = 0;

            for (int index = 0; index < actionsToReplayCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlanAction action = actions[index];

                if (currentSnapshot.BoundaryReason != SearchBoundaryReason.None)
                    return Illegal($"Action[{index}] encountered prior boundary {currentSnapshot.BoundaryReason}.");
                if (currentSnapshot.PlayerDead || currentSnapshot.AllEnemiesDead || currentSnapshot.TerminalStamp != null)
                    return Illegal($"Action[{index}] occurred after combat already terminated.");
                if (action.Turn != currentSnapshot.Turn)
                    return Illegal($"Action[{index}] turn mismatch: expected {currentSnapshot.Turn}, got {action.Turn}.");

                CombatPredictionSimulator preSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
                SimulatedCombatState preCombat = (SimulatedCombatState)preSim.State.CombatState;
                if (!ValidateExternalActionLegality(
                        solver,
                        root,
                        preSim,
                        preCombat,
                        action,
                        explicitPotionUses,
                        index,
                        out PlanCardChoice? boundPotionChoice,
                        out string? legalityFailure))
                {
                    return Illegal(legalityFailure!);
                }

                int turnBeforeAction = currentSnapshot.Turn;
                int hpLostBefore = currentSnapshot.CumulativePlayerHpLost;
                int hpBefore = currentSnapshot.PlayerHp;
                int rawEnemyHpBefore = SumAliveEnemyHp(preSim, preCombat);
                int energyBefore = currentSnapshot.Energy;

                List<PlanCardChoice> boundActionChoices = [];
                List<PlanCardChoice> boundTurnStartChoices = [];
                solver._externalBoundActionChoices = boundActionChoices;
                solver._externalBoundTurnStartChoices = boundTurnStartChoices;
                SimulationSnapshot nextSnapshot;
                try
                {
                    nextSnapshot = solver.Replay(
                        [action],
                        currentSnapshot,
                        currentSnapshot.Turn,
                        index,
                        countTransition: false,
                        allowExecutionCapture: false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (PredictionUnsupportedException ex)
                {
                    return HandleUnsupportedStep(
                        ex,
                        index,
                        action.Turn,
                        root.StartTurnNumber,
                        annotatedActions,
                        continuations,
                        verifiedPrefixActionCount,
                        verifiedLastTurn,
                        verifiedPrefixSummary,
                        expectedPotionUses,
                        replayedPotions,
                        verifiedPotionCountAtCheckpoint);
                }
                catch (NotSupportedException ex)
                {
                    return HandleUnsupportedStep(
                        ex,
                        index,
                        action.Turn,
                        root.StartTurnNumber,
                        annotatedActions,
                        continuations,
                        verifiedPrefixActionCount,
                        verifiedLastTurn,
                        verifiedPrefixSummary,
                        expectedPotionUses,
                        replayedPotions,
                        verifiedPotionCountAtCheckpoint);
                }
                catch (InvalidPlannedChoiceBranchException ex)
                {
                    return Illegal($"Action[{index}] choice branch failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    return Illegal($"Action[{index}] replay failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    solver._externalBoundActionChoices = null;
                    solver._externalBoundTurnStartChoices = null;
                }

                currentSnapshot.ReleaseSimulator();
                currentSnapshot = nextSnapshot;

                if (action.Kind == PlanActionKind.UsePotion)
                {
                    explicitPotionUses++;
                    replayedPotions.Add(new ReplayedPotionConsumption(
                        action.Turn,
                        action.PotionSlot,
                        action.PotionId,
                        Automatic: false));
                }

                CombatPredictionSimulator postSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
                SimulatedCombatState postCombat = (SimulatedCombatState)postSim.State.CombatState;

                while (observedPotionUseCount < postCombat.PotionUses.Count)
                {
                    PredictedPotionUse recordedUse = postCombat.PotionUses[observedPotionUseCount++];
                    if (recordedUse.Automatic)
                    {
                        replayedPotions.Add(new ReplayedPotionConsumption(
                            turnBeforeAction,
                            recordedUse.Slot,
                            recordedUse.PotionId,
                            Automatic: true));
                    }
                }

                annotatedActions[index] = BindReplayedChoices(
                    action,
                    boundPotionChoice,
                    boundActionChoices,
                    boundTurnStartChoices);

                int hpLossDelta = Math.Max(0, currentSnapshot.CumulativePlayerHpLost - hpLostBefore);
                int hpRecoveredDelta = Math.Max(0, currentSnapshot.PlayerHp - hpBefore + hpLossDelta);
                int rawEnemyHpAfter = SumAliveEnemyHp(postSim, postCombat);
                int enemyHpLossDelta = Math.Max(0, rawEnemyHpBefore - rawEnemyHpAfter);
                turnHpLost += hpLossDelta;
                turnHpRecovered += hpRecoveredDelta;
                turnEnemyHpLost += enemyHpLossDelta;

                bool combatEnded = currentSnapshot.PlayerDead || currentSnapshot.AllEnemiesDead || currentSnapshot.TerminalStamp != null;
                bool endsTurn = action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn || currentSnapshot.Turn > turnBeforeAction;
                if (action.Kind != PlanActionKind.EndTurn
                    && action.TurnStartChoices is { Count: > 0 }
                    && !combatEnded
                    && currentSnapshot.Turn == turnBeforeAction)
                {
                    return Illegal($"Action[{index}] carried unconsumed TurnStartChoices without ending turn {turnBeforeAction}.");
                }
                bool isLastReplayed = index == actionsToReplayCount - 1;
                bool hasNextAction = index + 1 < actions.Count;

                if (currentSnapshot.BoundaryReason is SearchBoundaryReason.UnsupportedEffect or SearchBoundaryReason.DynamicResolution)
                {
                    if (action.Turn > root.StartTurnNumber
                        && verifiedLastTurn >= root.StartTurnNumber
                        && verifiedPrefixActionCount > 0)
                    {
                        if (!CheckPotionExpectations(
                                expectedPotionUses,
                                replayedPotions,
                                verifiedPotionCountAtCheckpoint,
                                verifiedLastTurn,
                                enforceNoFutureEntries: false,
                                out string? prefixPotionFailure))
                        {
                            return Illegal(prefixPotionFailure!);
                        }
                        return UnsupportedFuture(
                            annotatedActions,
                            continuations,
                            verifiedPrefixActionCount,
                            verifiedLastTurn,
                            verifiedPrefixSummary,
                            $"Boundary {currentSnapshot.BoundaryReason} at action[{index}] turn={action.Turn}.");
                    }
                    return Illegal($"Unsupported boundary {currentSnapshot.BoundaryReason} at action[{index}] turn={action.Turn}.");
                }

                if (currentSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice)
                {
                    bool allowedTurnStartPending = verifyCurrentTurnOnly
                        && isLastReplayed
                        && endsTurn
                        && !combatEnded
                        && currentSnapshot.Turn > turnBeforeAction;
                    if (!allowedTurnStartPending)
                        return Illegal($"Action[{index}] hit unresolved PendingChoice on turn {currentSnapshot.Turn}.");
                }
                else if (currentSnapshot.BoundaryReason != SearchBoundaryReason.None && (!isLastReplayed || hasNextAction))
                {
                    return Illegal($"Action[{index}] terminated early at boundary {currentSnapshot.BoundaryReason}.");
                }

                if (combatEnded)
                {
                    if (!isLastReplayed || (verifyCurrentTurnOnly && hasNextAction && actions[index + 1].Turn == root.StartTurnNumber))
                        return Illegal($"Combat ended at action[{index}], before subsequent route actions.");

                    int actualEnergyLeft = endsTurn ? energyBefore : currentSnapshot.Energy;
                    bool actualCombatWon = currentSnapshot.AllEnemiesDead && !currentSnapshot.PlayerDead;
                    if (!CheckTurnExpectation(
                            turnExpectations,
                            turnBeforeAction,
                            turnHpLost,
                            turnHpRecovered,
                            turnEnemyHpLost,
                            actualEnergyLeft,
                            actualCombatWon,
                            out string? turnFailure))
                    {
                        return Illegal(turnFailure!);
                    }

                    verifiedPrefixActionCount = index + 1;
                    verifiedLastTurn = turnBeforeAction;
                    verifiedPotionCountAtCheckpoint = replayedPotions.Count;
                    verifiedPrefixSummary = CaptureSummary(
                        currentSnapshot,
                        postSim,
                        postCombat,
                        root,
                        battleDamage,
                        verifiedPrefixActionCount);
                }
                else if (endsTurn)
                {
                    if (hasNextAction && currentSnapshot.Turn != actions[index + 1].Turn)
                    {
                        return Illegal(
                            $"Turn mismatch after EndTurn at action[{index}]: expected {actions[index + 1].Turn}, got {currentSnapshot.Turn}.");
                    }
                    if (!hasNextAction && !verifyCurrentTurnOnly && currentSnapshot.Turn != terminalExpectation.Turn)
                    {
                        return Illegal(
                            $"Terminal turn mismatch after final EndTurn: expected {terminalExpectation.Turn}, got {currentSnapshot.Turn}.");
                    }

                    if (!CheckTurnExpectation(
                            turnExpectations,
                            turnBeforeAction,
                            turnHpLost,
                            turnHpRecovered,
                            turnEnemyHpLost,
                            energyBefore,
                            actualCombatEnded: false,
                            out string? turnFailure))
                    {
                        return Illegal(turnFailure!);
                    }

                    verifiedPrefixActionCount = index + 1;
                    verifiedLastTurn = turnBeforeAction;
                    verifiedPotionCountAtCheckpoint = replayedPotions.Count;
                    verifiedPrefixSummary = CaptureSummary(
                        currentSnapshot,
                        postSim,
                        postCombat,
                        root,
                        battleDamage,
                        verifiedPrefixActionCount);

                    if (!verifyCurrentTurnOnly && hasNextAction && actions[index + 1].Turn > turnBeforeAction)
                    {
                        if (currentSnapshot.BoundaryReason != SearchBoundaryReason.None
                            || postSim.HasPendingChoice
                            || postSim.TerminalStamp != null)
                        {
                            return Illegal(
                                $"Continuation boundary at turn {currentSnapshot.Turn} is not clean " +
                                $"(boundary={currentSnapshot.BoundaryReason}, pending={postSim.HasPendingChoice}).");
                        }
                        try
                        {
                            postCombat.AssertForkable();
                        }
                        catch (InvalidOperationException ex)
                        {
                            return Illegal($"Continuation boundary at turn {currentSnapshot.Turn} is not forkable: {ex.Message}");
                        }

                        ContinuationStamp stamp = ContinuationStamp.CapturePredicted(
                            solver._player, postSim, currentSnapshot.Turn, solver._forecast, solver._startTurnNumber);
                        continuations.Add(new CachedContinuation(stamp, currentSnapshot.Turn, currentSnapshot.Turn - solver._startTurnNumber));
                    }

                    turnHpLost = 0;
                    turnHpRecovered = 0;
                    turnEnemyHpLost = 0;
                }
                else if (isLastReplayed && (verifyCurrentTurnOnly || !hasNextAction))
                {
                    if (!CheckTurnExpectation(
                            turnExpectations,
                            turnBeforeAction,
                            turnHpLost,
                            turnHpRecovered,
                            turnEnemyHpLost,
                            currentSnapshot.Energy,
                            actualCombatEnded: false,
                            out string? turnFailure))
                    {
                        return Illegal(turnFailure!);
                    }

                    verifiedPrefixActionCount = index + 1;
                    verifiedLastTurn = turnBeforeAction;
                    verifiedPotionCountAtCheckpoint = replayedPotions.Count;
                    verifiedPrefixSummary = CaptureSummary(
                        currentSnapshot,
                        postSim,
                        postCombat,
                        root,
                        battleDamage,
                        verifiedPrefixActionCount);
                }
            }

            int verifiedTurnLimit = Math.Max(root.StartTurnNumber, verifiedLastTurn);
            bool fullRouteReplayed = !verifyCurrentTurnOnly || actionsToReplayCount == actions.Count;
            if (!CheckPotionExpectations(
                    expectedPotionUses,
                    replayedPotions,
                    replayedPotions.Count,
                    verifiedTurnLimit,
                    enforceNoFutureEntries: fullRouteReplayed,
                    out string? finalPotionFailure))
            {
                return Illegal(finalPotionFailure!);
            }

            if (fullRouteReplayed)
            {
                CombatPredictionSimulator finalSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
                SimulatedCombatState finalCombat = (SimulatedCombatState)finalSim.State.CombatState;
                if (!CheckTerminalExpectation(
                        currentSnapshot,
                        finalSim,
                        finalCombat,
                        battleDamage,
                        terminalExpectation,
                        out string? terminalFailure))
                {
                    return Illegal(terminalFailure!);
                }
            }
            else
            {
                int minimumObservedBattleHpLost = battleDamage.HpLostSoFar + currentSnapshot.CumulativePlayerHpLost;
                if (terminalExpectation.ProjectedBattleHpLost < minimumObservedBattleHpLost)
                {
                    return Illegal(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Terminal ProjectedBattleHpLost={terminalExpectation.ProjectedBattleHpLost} is below " +
                        $"verified turn-1 loss {minimumObservedBattleHpLost}."));
                }
            }

            return new ExternalRouteVerificationResult(
                ExternalRouteVerificationStatus.Annotated,
                annotatedActions,
                continuations,
                actionsToReplayCount,
                verifiedTurnLimit,
                verifiedPrefixSummary,
                null);
        }
        finally
        {
            currentSnapshot?.ReleaseSimulator();
        }
    }

    private static PlanAction BindReplayedChoices(
        PlanAction action,
        PlanCardChoice? boundPotionChoice,
        IReadOnlyList<PlanCardChoice> boundActionChoices,
        IReadOnlyList<PlanCardChoice> boundTurnStartChoices)
    {
        PlanCardChoice? choice = action.Choice;
        IReadOnlyList<PlanCardChoice>? nestedChoices = action.NestedChoices;
        int nestedBeforePrimary = action.NestedChoicesBeforePrimary;
        IReadOnlyList<PlanCardChoice>? turnStartChoices = action.TurnStartChoices;
        bool updated = false;

        if (action.Kind == PlanActionKind.UsePotion)
        {
            if (boundPotionChoice is not null)
            {
                choice = boundPotionChoice;
                updated = true;
            }
            if (boundActionChoices.Count > 0)
            {
                nestedChoices = boundActionChoices.ToArray();
                nestedBeforePrimary = 0;
                updated = true;
            }
        }
        else if (action.Kind == PlanActionKind.PlayCard && boundActionChoices.Count > 0)
        {
            int firstPrimaryIndex = -1;
            for (int i = 0; i < boundActionChoices.Count; i++)
            {
                if (string.IsNullOrEmpty(boundActionChoices[i].SourceId))
                {
                    firstPrimaryIndex = i;
                    break;
                }
            }
            if (firstPrimaryIndex >= 0)
            {
                choice = boundActionChoices[firstPrimaryIndex];
                nestedBeforePrimary = firstPrimaryIndex;
                nestedChoices = boundActionChoices.Count > 1
                    ? boundActionChoices.Where((_, idx) => idx != firstPrimaryIndex).ToArray()
                    : null;
            }
            else
            {
                choice = null;
                nestedBeforePrimary = 0;
                nestedChoices = boundActionChoices.ToArray();
            }
            updated = true;
        }

        if (boundTurnStartChoices.Count > 0)
        {
            if (action.TurnStartChoices is { Count: > 0 } existingTurnStart
                && existingTurnStart.Any(static c =>
                    c.Effect == PlanChoiceEffect.ApplyKnowledgeCurse && c.Timing == PlanChoiceTiming.EnemyTurn))
            {
                PlanCardChoice[] merged = new PlanCardChoice[existingTurnStart.Count];
                int boundIndex = 0;
                for (int i = 0; i < existingTurnStart.Count; i++)
                {
                    PlanCardChoice item = existingTurnStart[i];
                    if (item.Effect == PlanChoiceEffect.ApplyKnowledgeCurse && item.Timing == PlanChoiceTiming.EnemyTurn)
                        merged[i] = item;
                    else
                        merged[i] = boundIndex < boundTurnStartChoices.Count ? boundTurnStartChoices[boundIndex++] : item;
                }
                turnStartChoices = merged;
            }
            else
            {
                turnStartChoices = boundTurnStartChoices.ToArray();
            }
            updated = true;
        }

        if (!updated)
            return action;

        return action with
        {
            Choice = choice,
            NestedChoices = nestedChoices,
            NestedChoicesBeforePrimary = nestedBeforePrimary,
            TurnStartChoices = turnStartChoices,
        };
    }

    private static bool ValidateActionTurnOrdering(
        IReadOnlyList<PlanAction> actions,
        int startTurnNumber,
        out string? failureReason)
    {
        if (actions.Count == 0)
        {
            failureReason = null;
            return true;
        }
        if (actions[0].Turn != startTurnNumber)
        {
            failureReason = $"First route action starts on turn {actions[0].Turn}, expected {startTurnNumber}.";
            return false;
        }
        for (int index = 1; index < actions.Count; index++)
        {
            PlanAction prev = actions[index - 1];
            PlanAction curr = actions[index];
            if (curr.Turn < prev.Turn)
            {
                failureReason = $"Action[{index}] turn {curr.Turn} decreased from {prev.Turn}.";
                return false;
            }
            if (curr.Turn > prev.Turn && prev.Kind != PlanActionKind.EndTurn && !prev.EndsPlayerTurn)
            {
                failureReason = $"Action[{index}] advanced turn from {prev.Turn} to {curr.Turn} without ending the prior turn.";
                return false;
            }
        }
        failureReason = null;
        return true;
    }

    private static int ResolveReplayCount(
        IReadOnlyList<PlanAction> actions,
        int startTurnNumber,
        bool verifyCurrentTurnOnly,
        out string? failureReason)
    {
        failureReason = null;
        if (actions.Count == 0)
            return 0;
        if (!verifyCurrentTurnOnly)
            return actions.Count;

        int count = 0;
        while (count < actions.Count && actions[count].Turn == startTurnNumber)
            count++;
        if (count == 0)
            failureReason = $"Current-turn route contains no actions on turn {startTurnNumber}.";
        return count;
    }

    private static int SumAliveEnemyHp(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int total = 0;
        IReadOnlyList<Creature> enemies = combat.KnownEnemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            if (!combat.ContainsCreature(enemy))
                continue;
            SimCreatureState enemyState = sim.State.GetCreature(enemy);
            if (enemyState.IsAlive)
                total += Math.Max(0, enemyState.CurrentHp);
        }
        return total;
    }

    private static int CountAliveEnemies(CombatPredictionSimulator sim, SimulatedCombatState combat)
    {
        int count = 0;
        IReadOnlyList<Creature> enemies = combat.KnownEnemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            if (combat.ContainsCreature(enemy) && sim.State.GetCreature(enemy).IsAlive)
                count++;
        }
        return count;
    }

    private static bool ValidateExternalActionLegality(
        CombatBeamSolver solver,
        CombatRootSnapshot root,
        CombatPredictionSimulator preSim,
        SimulatedCombatState preCombat,
        PlanAction action,
        int explicitPotionUses,
        int index,
        out PlanCardChoice? boundPotionChoice,
        out string? failureReason)
    {
        boundPotionChoice = null;
        if (action.TargetCombatId is uint targetCombatId)
        {
            Creature? target = preCombat.GetCreature(targetCombatId);
            if (target is null || !preCombat.ContainsCreature(target) || preSim.State.GetCreature(target).IsDead)
            {
                failureReason = $"Action[{index}] targets missing or dead creature combatId={targetCombatId}.";
                return false;
            }
        }

        switch (action.Kind)
        {
            case PlanActionKind.EndTurn:
                if (action.TargetCombatId is not null
                    || action.TargetIndex != -1
                    || !string.IsNullOrEmpty(action.CardId)
                    || action.CardOccurrence != 0
                    || !string.IsNullOrEmpty(action.PotionId)
                    || action.PotionSlot != -1
                    || action.Choice is not null
                    || action.NestedChoices is { Count: > 0 })
                {
                    failureReason = $"Action[{index}] EndTurn carries unexpected card, potion, target, or choice fields.";
                    return false;
                }
                failureReason = null;
                return true;

            case PlanActionKind.PlayCard:
            {
                if (!string.IsNullOrEmpty(action.PotionId)
                    || action.PotionSlot != -1
                    || string.IsNullOrEmpty(action.CardId)
                    || action.CardOccurrence < 0)
                {
                    failureReason = $"Action[{index}] PlayCard has invalid card/potion metadata.";
                    return false;
                }

                SimPlayerCombatState playerState = preSim.State.GetPlayerCombatState(solver._player);
                PredictedCard? card = FindCardForReplay(playerState.Hand.Cards, action);
                if (card is null)
                {
                    failureReason = $"Action[{index}] PlayCard could not locate hand card '{action.CardId}'#{action.CardOccurrence}.";
                    return false;
                }
                if (!string.Equals(card.Preview.Id.Entry, action.CardId, StringComparison.Ordinal)
                    || card.Preview.CurrentUpgradeLevel != action.CardUpgradeLevel
                    || !string.Equals(card.Preview.Enchantment?.Id.Entry ?? string.Empty, action.CardEnchantmentId, StringComparison.Ordinal))
                {
                    failureReason = $"Action[{index}] PlayCard card identity mismatch for '{action.CardId}'.";
                    return false;
                }
                if (!preCombat.CanPlayCard(preSim, card))
                {
                    failureReason = $"Action[{index}] PlayCard '{action.CardId}' is not currently playable.";
                    return false;
                }
                if (!MatchesAllowedTarget(solver.TargetsFor(card, preSim), action.TargetIndex, action.TargetCombatId))
                {
                    failureReason =
                        $"Action[{index}] PlayCard '{action.CardId}' target index={action.TargetIndex} " +
                        $"combatId={action.TargetCombatId?.ToString(CultureInfo.InvariantCulture) ?? "-"} is not legal.";
                    return false;
                }
                failureReason = null;
                return true;
            }

            case PlanActionKind.UsePotion:
            {
                if (!string.IsNullOrEmpty(action.CardId) || string.IsNullOrEmpty(action.PotionId))
                {
                    failureReason = $"Action[{index}] UsePotion has invalid potion/card metadata.";
                    return false;
                }
                if (solver._earliestPotionTurn is { } earliestTurn && action.Turn < earliestTurn)
                {
                    failureReason = $"Action[{index}] UsePotion '{action.PotionId}' disallowed before turn {earliestTurn}.";
                    return false;
                }
                if (solver._maximumPotionUses is { } maxPotions && explicitPotionUses >= maxPotions)
                {
                    failureReason = $"Action[{index}] UsePotion '{action.PotionId}' exceeds maximum potion budget {maxPotions}.";
                    return false;
                }
                if (action.PotionSlot < 0 || action.PotionSlot >= root.PotionSlotCount)
                {
                    failureReason = $"Action[{index}] UsePotion slot={action.PotionSlot} is out of range.";
                    return false;
                }
                PotionModel? potion = preCombat.GetPotionAtSlot(solver._player, action.PotionSlot);
                if (potion is null
                    || !string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal)
                    || !preCombat.IsPotionAvailable(solver._player, action.PotionSlot))
                {
                    failureReason = $"Action[{index}] UsePotion '{action.PotionId}' at slot={action.PotionSlot} is unavailable.";
                    return false;
                }
                if (!PotionOnUseSupport.CanSearch(potion) || !solver.AllowsPotionUse(action.PotionSlot, potion.Id.Entry))
                {
                    failureReason = $"Action[{index}] UsePotion '{action.PotionId}' is forbidden by search potion policy.";
                    return false;
                }
                if (!MatchesAllowedTarget(solver.TargetsForPotion(potion, preSim), action.TargetIndex, action.TargetCombatId))
                {
                    failureReason =
                        $"Action[{index}] UsePotion '{action.PotionId}' target index={action.TargetIndex} " +
                        $"combatId={action.TargetCombatId?.ToString(CultureInfo.InvariantCulture) ?? "-"} is not legal.";
                    return false;
                }
                if (!ValidateExternalPotionChoice(preSim, potion, action.Choice, index, out boundPotionChoice, out failureReason))
                    return false;
                failureReason = null;
                return true;
            }

            default:
                failureReason = $"Action[{index}] has unsupported kind {action.Kind}.";
                return false;
        }
    }

    private static bool MatchesAllowedTarget(
        IEnumerable<(int Index, Creature? Target)> allowedTargets,
        int actionTargetIndex,
        uint? actionTargetCombatId)
    {
        foreach ((int allowedIndex, Creature? allowedTarget) in allowedTargets)
        {
            if (allowedTarget is null)
            {
                if (actionTargetCombatId is null && actionTargetIndex == -1)
                    return true;
                continue;
            }
            if (allowedTarget.CombatId is uint combatId)
            {
                if (actionTargetCombatId == combatId && actionTargetIndex == allowedIndex)
                    return true;
            }
            else if (actionTargetCombatId is null && actionTargetIndex == allowedIndex)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ValidateExternalPotionChoice(
        CombatPredictionSimulator preSim,
        PotionModel potion,
        PlanCardChoice? choice,
        int index,
        out PlanCardChoice? boundChoice,
        out string? failureReason)
    {
        boundChoice = null;
        bool requiresChoice = PotionChoiceSupport.RequiresChoice(potion);
        if (!requiresChoice)
        {
            if (choice is not null)
            {
                failureReason = $"Action[{index}] UsePotion '{potion.Id.Entry}' does not take a primary choice.";
                return false;
            }
            failureReason = null;
            return true;
        }

        if (PotionChoiceSupport.GeneratesCardChoice(potion))
        {
            if (choice is not null
                && (choice.Effect != PlanChoiceEffect.GenerateToHand
                    || choice.SourcePile != PileType.None
                    || choice.Cards.Count > 1))
            {
                failureReason = $"Action[{index}] UsePotion '{potion.Id.Entry}' carries malformed generated choice.";
                return false;
            }
            if (choice is not null)
            {
                boundChoice = choice with
                {
                    SourceId = string.Empty,
                    Timing = PlanChoiceTiming.Action,
                };
            }
            failureReason = null;
            return true;
        }

        CardChoiceSpec spec;
        try
        {
            spec = PotionChoiceSupport.GetSpec(preSim, potion);
        }
        catch (InvalidOperationException ex)
        {
            failureReason = $"Action[{index}] UsePotion '{potion.Id.Entry}' choice spec unavailable: {ex.Message}";
            return false;
        }

        if (choice is null)
        {
            if (spec.MinCount > 0 && spec.Options.Count > 0)
            {
                failureReason = $"Action[{index}] UsePotion '{potion.Id.Entry}' requires a mandatory card choice.";
                return false;
            }
            failureReason = null;
            return true;
        }

        if (!string.IsNullOrEmpty(choice.ContextId)
            && !string.Equals(choice.ContextId, spec.ContextId, StringComparison.Ordinal))
        {
            failureReason =
                $"Action[{index}] UsePotion '{potion.Id.Entry}' context mismatch: " +
                $"planned='{choice.ContextId}', expected='{spec.ContextId}'.";
            return false;
        }

        try
        {
            TurnStartChoiceCursor.ValidateChoiceMatchesSpec(choice, spec);
        }
        catch (InvalidPlannedChoiceBranchException ex)
        {
            failureReason = $"Action[{index}] UsePotion '{potion.Id.Entry}' invalid choice: {ex.Message}";
            return false;
        }
        boundChoice = choice with
        {
            SourceId = string.Empty,
            ContextId = spec.ContextId,
            Timing = PlanChoiceTiming.Action,
        };
        failureReason = null;
        return true;
    }

    private static bool CheckTurnExpectation(
        IReadOnlyDictionary<int, ExternalRouteTurnExpectation> expectations,
        int turn,
        int actualHpLost,
        int actualHpRecovered,
        int actualEnemyHpLost,
        int actualEnergyLeft,
        bool actualCombatEnded,
        out string? failureReason)
    {
        if (!expectations.TryGetValue(turn, out ExternalRouteTurnExpectation expected))
        {
            failureReason = $"Missing TurnOutcome expectation for replayed turn {turn}.";
            return false;
        }
        if (actualHpLost != expected.HpLost
            || actualHpRecovered != expected.HpRecovered
            || actualEnemyHpLost != expected.EnemyHpLost
            || actualEnergyLeft != expected.EnergyLeft
            || actualCombatEnded != expected.CombatEnded)
        {
            failureReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Turn {turn} snapshot mismatch: expected(hpLost={expected.HpLost}, hpRec={expected.HpRecovered}, " +
                $"enemyHpLost={expected.EnemyHpLost}, energy={expected.EnergyLeft}, ended={expected.CombatEnded}) " +
                $"actual(hpLost={actualHpLost}, hpRec={actualHpRecovered}, enemyHpLost={actualEnemyHpLost}, " +
                $"energy={actualEnergyLeft}, ended={actualCombatEnded}).");
            return false;
        }
        failureReason = null;
        return true;
    }

    private static bool CheckPotionExpectations(
        IReadOnlyList<ExternalRoutePotionUseExpectation> expectedAll,
        IReadOnlyList<ReplayedPotionConsumption> replayedAll,
        int replayedCount,
        int verifiedTurnLimit,
        bool enforceNoFutureEntries,
        out string? failureReason)
    {
        if (enforceNoFutureEntries)
        {
            for (int i = 0; i < expectedAll.Count; i++)
            {
                if (expectedAll[i].Turn > verifiedTurnLimit)
                {
                    failureReason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"PotionUses[{i}] references unverified turn {expectedAll[i].Turn} > {verifiedTurnLimit}.");
                    return false;
                }
            }
        }

        List<ExternalRoutePotionUseExpectation> expectedScoped = [];
        for (int i = 0; i < expectedAll.Count; i++)
        {
            if (expectedAll[i].Turn <= verifiedTurnLimit)
                expectedScoped.Add(expectedAll[i]);
        }

        int expectedIndex = 0;
        for (int i = 0; i < replayedCount; i++)
        {
            ReplayedPotionConsumption replayed = replayedAll[i];
            bool matchesNextExpected = expectedIndex < expectedScoped.Count
                && expectedScoped[expectedIndex].Turn == replayed.Turn
                && expectedScoped[expectedIndex].Slot == replayed.Slot
                && string.Equals(expectedScoped[expectedIndex].PotionId, replayed.PotionId, StringComparison.Ordinal);

            if (!replayed.Automatic)
            {
                if (!matchesNextExpected)
                {
                    string actualDesc = expectedIndex < expectedScoped.Count
                        ? $"turn={expectedScoped[expectedIndex].Turn},slot={expectedScoped[expectedIndex].Slot},id={expectedScoped[expectedIndex].PotionId}"
                        : "<missing>";
                    failureReason =
                        $"Explicit UsePotion ({replayed.Turn},{replayed.Slot},{replayed.PotionId}) " +
                        $"does not match PotionUses[{expectedIndex}] ({actualDesc}).";
                    return false;
                }
                expectedIndex++;
            }
            else if (matchesNextExpected)
            {
                expectedIndex++;
            }
        }

        if (expectedIndex != expectedScoped.Count)
        {
            ExternalRoutePotionUseExpectation extra = expectedScoped[expectedIndex];
            failureReason = string.Create(
                CultureInfo.InvariantCulture,
                $"PotionUses contains unverified entry ({extra.Turn},{extra.Slot},{extra.PotionId}).");
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool CheckTerminalExpectation(
        SimulationSnapshot snapshot,
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        BattleDamageSnapshot battleDamage,
        ExternalRouteTerminalExpectation expected,
        out string? failureReason)
    {
        int actualEnemyHp = SumAliveEnemyHp(sim, combat);
        int actualAliveEnemies = CountAliveEnemies(sim, combat);
        int actualProjectedBattleHpLost = battleDamage.HpLostSoFar + snapshot.CumulativePlayerHpLost;
        if (snapshot.PlayerDead != expected.PlayerDead
            || snapshot.AllEnemiesDead != expected.AllEnemiesDead
            || snapshot.PlayerHp != expected.PlayerHp
            || snapshot.PlayerMaxHp != expected.PlayerMaxHp
            || snapshot.PlayerBlock != expected.PlayerBlock
            || actualEnemyHp != expected.EnemyHp
            || actualAliveEnemies != expected.AliveEnemyCount
            || snapshot.Energy != expected.Energy
            || snapshot.Stars != expected.Stars
            || snapshot.HandCount != expected.HandCount
            || snapshot.Turn != expected.Turn
            || actualProjectedBattleHpLost != expected.ProjectedBattleHpLost
            || snapshot.CombatEndedTurn != expected.CombatEndedTurn
            || snapshot.DeathTurn != expected.DeathTurn)
        {
            failureReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Terminal snapshot mismatch: expected(turn={expected.Turn}, dead={expected.PlayerDead}, won={expected.AllEnemiesDead}, " +
                $"hp={expected.PlayerHp}/{expected.PlayerMaxHp}, blk={expected.PlayerBlock}, enemyHp={expected.EnemyHp}, " +
                $"alive={expected.AliveEnemyCount}, energy={expected.Energy}, stars={expected.Stars}, hand={expected.HandCount}, " +
                $"battleLoss={expected.ProjectedBattleHpLost}, endedTurn={expected.CombatEndedTurn}, deathTurn={expected.DeathTurn}) " +
                $"actual(turn={snapshot.Turn}, dead={snapshot.PlayerDead}, won={snapshot.AllEnemiesDead}, " +
                $"hp={snapshot.PlayerHp}/{snapshot.PlayerMaxHp}, blk={snapshot.PlayerBlock}, enemyHp={actualEnemyHp}, " +
                $"alive={actualAliveEnemies}, energy={snapshot.Energy}, stars={snapshot.Stars}, hand={snapshot.HandCount}, " +
                $"battleLoss={actualProjectedBattleHpLost}, endedTurn={snapshot.CombatEndedTurn}, deathTurn={snapshot.DeathTurn}).");
            return false;
        }
        failureReason = null;
        return true;
    }

    private static ExternalRouteSnapshotSummary CaptureSummary(
        SimulationSnapshot snapshot,
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        CombatRootSnapshot root,
        BattleDamageSnapshot battleDamage,
        int actionCount)
    {
        int postCombatRelicHeal = SolverInterimResultOrdering.IsCompleteVictory(
            actionCount,
            snapshot.AllEnemiesDead,
            snapshot.PlayerDead,
            snapshot.ProjectedPlayerHp)
            ? root.PostCombatRelicHeal.HealFor(snapshot.PlayerHp, snapshot.PlayerMaxHp)
            : 0;
        return new ExternalRouteSnapshotSummary(
            Turn: snapshot.Turn,
            PlayerDead: snapshot.PlayerDead,
            AllEnemiesDead: snapshot.AllEnemiesDead,
            PlayerHp: snapshot.PlayerHp,
            PlayerMaxHp: snapshot.PlayerMaxHp,
            CumulativePlayerHpLost: snapshot.CumulativePlayerHpLost,
            RecoveredPlayerHp: snapshot.RecoveredPlayerHp,
            ProjectedPlayerHp: snapshot.ProjectedPlayerHp,
            DeathSaveRelicHpRestored: snapshot.DeathSaveRelicHpRestored,
            DeathSavePotionHpRestored: snapshot.DeathSavePotionHpRestored,
            DeathSaveUseCount: snapshot.DeathSaveUseCount,
            ProjectedDeathSaveUseCount: snapshot.ProjectedDeathSaveUseCount,
            LongTermResourceValue: snapshot.LongTermResourceValue,
            AngerCopiesGenerated: snapshot.AngerCopiesGenerated,
            GrowthHpCredit: snapshot.GrowthHpCredit,
            RelicCounters: snapshot.RelicCounters,
            GrowthRewards: snapshot.GrowthRewards,
            UnrecoveredGold: snapshot.UnrecoveredGold,
            ForfeitsEncounterGold: snapshot.ForfeitsEncounterGold,
            UnrecoveredCards: snapshot.UnrecoveredCards,
            PlayerBlock: snapshot.PlayerBlock,
            EnemyHp: SumAliveEnemyHp(sim, combat),
            AliveEnemyCount: CountAliveEnemies(sim, combat),
            Energy: snapshot.Energy,
            Stars: snapshot.Stars,
            HandCount: snapshot.HandCount,
            OutstandingStolenResource: snapshot.OutstandingStolenResource,
            ShufflesCrossed: snapshot.ShufflesCrossed,
            HasRisk: snapshot.HasRisk,
            PostCombatRelicHeal: postCombatRelicHeal,
            ProjectedBattleHpLost: battleDamage.HpLostSoFar + snapshot.CumulativePlayerHpLost,
            PredictionGaps: snapshot.PredictionGaps.ToArray(),
            VerifiedPotionUses: combat.PotionUses.ToArray());
    }

    private static ExternalRouteVerificationResult HandleUnsupportedStep(
        NotSupportedException ex,
        int index,
        int actionTurn,
        int startTurnNumber,
        PlanAction[] annotatedActions,
        List<CachedContinuation> continuations,
        int verifiedPrefixActionCount,
        int verifiedLastTurn,
        ExternalRouteSnapshotSummary? verifiedPrefixSummary,
        IReadOnlyList<ExternalRoutePotionUseExpectation> expectedPotionUses,
        IReadOnlyList<ReplayedPotionConsumption> replayedPotions,
        int verifiedPotionCountAtCheckpoint)
    {
        string reason = $"{ex.GetType().Name}: {ex.Message}";
        if (actionTurn > startTurnNumber
            && verifiedLastTurn >= startTurnNumber
            && verifiedPrefixActionCount > 0)
        {
            if (!CheckPotionExpectations(
                    expectedPotionUses,
                    replayedPotions,
                    verifiedPotionCountAtCheckpoint,
                    verifiedLastTurn,
                    enforceNoFutureEntries: false,
                    out string? potionFailure))
            {
                return Illegal(potionFailure!);
            }
            return UnsupportedFuture(
                annotatedActions,
                continuations,
                verifiedPrefixActionCount,
                verifiedLastTurn,
                verifiedPrefixSummary,
                reason);
        }
        return Illegal($"Unsupported prediction at action[{index}] turn={actionTurn}: {reason}");
    }

    private static ExternalRouteVerificationResult Illegal(string reason)
        => new(ExternalRouteVerificationStatus.IllegalRoute, [], [], 0, 0, null, reason);

    private static ExternalRouteVerificationResult UnsupportedFuture(
        PlanAction[] annotatedActions,
        List<CachedContinuation> continuations,
        int verifiedPrefixActionCount,
        int verifiedLastTurn,
        ExternalRouteSnapshotSummary? summary,
        string reason)
        => new(
            ExternalRouteVerificationStatus.UnsupportedFutureContinuation,
            annotatedActions[..verifiedPrefixActionCount],
            continuations.Where(c => c.StartTurnNumber <= verifiedLastTurn).ToArray(),
            verifiedPrefixActionCount,
            verifiedLastTurn,
            summary,
            reason);
}
