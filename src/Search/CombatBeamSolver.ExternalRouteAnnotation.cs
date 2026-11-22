using System.Globalization;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;

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
    int? CombatEndedTurn,
    int? DeathTurn);

internal readonly record struct ExternalRouteSnapshotSummary(
    int Turn,
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int ProjectedPlayerHp,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int OutstandingStolenResource,
    int ShufflesCrossed,
    bool HasRisk);

internal readonly record struct ExternalRouteVerificationResult(
    ExternalRouteVerificationStatus Status,
    IReadOnlyList<CachedContinuation> Continuations,
    int VerifiedPrefixActionCount,
    int VerifiedLastTurn,
    ExternalRouteSnapshotSummary? VerifiedPrefixSummary,
    string? FailureReason);

internal sealed partial class CombatBeamSolver
{
    internal static ExternalRouteVerificationResult VerifyAndAnnotateExternalRoute(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        IReadOnlyList<PlanAction> actions,
        IReadOnlyDictionary<int, ExternalRouteTurnExpectation> turnExpectations,
        ExternalRouteTerminalExpectation terminalExpectation,
        bool verifyCurrentTurnOnly,
        CancellationToken cancellationToken)
    {
        if (!ValidateActionTurnOrdering(actions, root.StartTurnNumber, out string? orderingFailure))
            return Illegal(orderingFailure!);

        int actionsToReplayCount = ResolveReplayCount(actions, root.StartTurnNumber, verifyCurrentTurnOnly, out string? countFailure);
        if (countFailure is not null)
            return Illegal(countFailure);

        CombatBeamSolver solver = new(root, displayNames, battleDamage, policy, cancellationToken);
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

            List<CachedContinuation> continuations = [];
            int verifiedPrefixActionCount = 0;
            int verifiedLastTurn = root.StartTurnNumber - 1;
            ExternalRouteSnapshotSummary? verifiedPrefixSummary = null;
            int turnHpLost = 0;
            int turnHpRecovered = 0;
            int turnEnemyHpLost = 0;

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
                if (action.TargetCombatId is uint targetCombatId)
                {
                    Creature? target = preCombat.GetCreature(targetCombatId);
                    if (target is null || !preCombat.ContainsCreature(target) || preSim.State.GetCreature(target).IsDead)
                        return Illegal($"Action[{index}] targets missing or dead creature combatId={targetCombatId}.");
                }

                int turnBeforeAction = currentSnapshot.Turn;
                int hpLostBefore = currentSnapshot.CumulativePlayerHpLost;
                int hpBefore = currentSnapshot.PlayerHp;
                int rawEnemyHpBefore = SumAliveEnemyHp(preSim, preCombat);
                int energyBefore = currentSnapshot.Energy;

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
                        continuations,
                        verifiedPrefixActionCount,
                        verifiedLastTurn,
                        verifiedPrefixSummary);
                }
                catch (NotSupportedException ex)
                {
                    return HandleUnsupportedStep(
                        ex,
                        index,
                        action.Turn,
                        root.StartTurnNumber,
                        continuations,
                        verifiedPrefixActionCount,
                        verifiedLastTurn,
                        verifiedPrefixSummary);
                }
                catch (InvalidPlannedChoiceBranchException ex)
                {
                    return Illegal($"Action[{index}] choice branch failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    return Illegal($"Action[{index}] replay failed: {ex.GetType().Name}: {ex.Message}");
                }

                currentSnapshot.ReleaseSimulator();
                currentSnapshot = nextSnapshot;

                CombatPredictionSimulator postSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
                SimulatedCombatState postCombat = (SimulatedCombatState)postSim.State.CombatState;

                int hpLossDelta = Math.Max(0, currentSnapshot.CumulativePlayerHpLost - hpLostBefore);
                int hpRecoveredDelta = Math.Max(0, currentSnapshot.PlayerHp - hpBefore + hpLossDelta);
                int rawEnemyHpAfter = SumAliveEnemyHp(postSim, postCombat);
                int enemyHpLossDelta = Math.Max(0, rawEnemyHpBefore - rawEnemyHpAfter);
                turnHpLost += hpLossDelta;
                turnHpRecovered += hpRecoveredDelta;
                turnEnemyHpLost += enemyHpLossDelta;

                bool combatEnded = currentSnapshot.PlayerDead || currentSnapshot.AllEnemiesDead || currentSnapshot.TerminalStamp != null;
                bool endsTurn = action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn || currentSnapshot.Turn > turnBeforeAction;
                bool isLastReplayed = index == actionsToReplayCount - 1;
                bool hasNextAction = index + 1 < actions.Count;

                if (currentSnapshot.BoundaryReason is SearchBoundaryReason.UnsupportedEffect or SearchBoundaryReason.DynamicResolution)
                {
                    if (action.Turn > root.StartTurnNumber
                        && verifiedLastTurn >= root.StartTurnNumber
                        && verifiedPrefixActionCount > 0)
                    {
                        return UnsupportedFuture(
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
                    verifiedPrefixSummary = CaptureSummary(currentSnapshot, postSim, postCombat);
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
                    verifiedPrefixSummary = CaptureSummary(currentSnapshot, postSim, postCombat);

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
                    verifiedPrefixSummary = CaptureSummary(currentSnapshot, postSim, postCombat);
                }
            }

            if (!verifyCurrentTurnOnly || actionsToReplayCount == actions.Count)
            {
                CombatPredictionSimulator finalSim = (CombatPredictionSimulator)currentSnapshot.Simulator;
                SimulatedCombatState finalCombat = (SimulatedCombatState)finalSim.State.CombatState;
                if (!CheckTerminalExpectation(currentSnapshot, finalSim, finalCombat, terminalExpectation, out string? terminalFailure))
                    return Illegal(terminalFailure!);
            }

            return new ExternalRouteVerificationResult(
                ExternalRouteVerificationStatus.Annotated,
                continuations,
                actionsToReplayCount,
                Math.Max(root.StartTurnNumber, verifiedLastTurn),
                verifiedPrefixSummary,
                null);
        }
        finally
        {
            currentSnapshot?.ReleaseSimulator();
        }
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
            failureReason = null;
            return true;
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

    private static bool CheckTerminalExpectation(
        SimulationSnapshot snapshot,
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        ExternalRouteTerminalExpectation expected,
        out string? failureReason)
    {
        int actualEnemyHp = SumAliveEnemyHp(sim, combat);
        int actualAliveEnemies = CountAliveEnemies(sim, combat);
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
            || snapshot.CombatEndedTurn != expected.CombatEndedTurn
            || snapshot.DeathTurn != expected.DeathTurn)
        {
            failureReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Terminal snapshot mismatch: expected(turn={expected.Turn}, dead={expected.PlayerDead}, won={expected.AllEnemiesDead}, " +
                $"hp={expected.PlayerHp}/{expected.PlayerMaxHp}, blk={expected.PlayerBlock}, enemyHp={expected.EnemyHp}, " +
                $"alive={expected.AliveEnemyCount}, energy={expected.Energy}, stars={expected.Stars}, hand={expected.HandCount}, " +
                $"endedTurn={expected.CombatEndedTurn}, deathTurn={expected.DeathTurn}) " +
                $"actual(turn={snapshot.Turn}, dead={snapshot.PlayerDead}, won={snapshot.AllEnemiesDead}, " +
                $"hp={snapshot.PlayerHp}/{snapshot.PlayerMaxHp}, blk={snapshot.PlayerBlock}, enemyHp={actualEnemyHp}, " +
                $"alive={actualAliveEnemies}, energy={snapshot.Energy}, stars={snapshot.Stars}, hand={snapshot.HandCount}, " +
                $"endedTurn={snapshot.CombatEndedTurn}, deathTurn={snapshot.DeathTurn}).");
            return false;
        }
        failureReason = null;
        return true;
    }

    private static ExternalRouteSnapshotSummary CaptureSummary(
        SimulationSnapshot snapshot,
        CombatPredictionSimulator sim,
        SimulatedCombatState combat)
        => new(
            Turn: snapshot.Turn,
            PlayerDead: snapshot.PlayerDead,
            AllEnemiesDead: snapshot.AllEnemiesDead,
            PlayerHp: snapshot.PlayerHp,
            PlayerMaxHp: snapshot.PlayerMaxHp,
            CumulativePlayerHpLost: snapshot.CumulativePlayerHpLost,
            RecoveredPlayerHp: snapshot.RecoveredPlayerHp,
            ProjectedPlayerHp: snapshot.ProjectedPlayerHp,
            PlayerBlock: snapshot.PlayerBlock,
            EnemyHp: SumAliveEnemyHp(sim, combat),
            AliveEnemyCount: CountAliveEnemies(sim, combat),
            Energy: snapshot.Energy,
            Stars: snapshot.Stars,
            HandCount: snapshot.HandCount,
            OutstandingStolenResource: snapshot.OutstandingStolenResource,
            ShufflesCrossed: snapshot.ShufflesCrossed,
            HasRisk: snapshot.HasRisk);

    private static ExternalRouteVerificationResult HandleUnsupportedStep(
        NotSupportedException ex,
        int index,
        int actionTurn,
        int startTurnNumber,
        List<CachedContinuation> continuations,
        int verifiedPrefixActionCount,
        int verifiedLastTurn,
        ExternalRouteSnapshotSummary? verifiedPrefixSummary)
    {
        string reason = $"{ex.GetType().Name}: {ex.Message}";
        if (actionTurn > startTurnNumber
            && verifiedLastTurn >= startTurnNumber
            && verifiedPrefixActionCount > 0)
        {
            return UnsupportedFuture(
                continuations,
                verifiedPrefixActionCount,
                verifiedLastTurn,
                verifiedPrefixSummary,
                reason);
        }
        return Illegal($"Unsupported prediction at action[{index}] turn={actionTurn}: {reason}");
    }

    private static ExternalRouteVerificationResult Illegal(string reason)
        => new(ExternalRouteVerificationStatus.IllegalRoute, [], 0, 0, null, reason);

    private static ExternalRouteVerificationResult UnsupportedFuture(
        List<CachedContinuation> continuations,
        int verifiedPrefixActionCount,
        int verifiedLastTurn,
        ExternalRouteSnapshotSummary? summary,
        string reason)
        => new(
            ExternalRouteVerificationStatus.UnsupportedFutureContinuation,
            continuations.Where(c => c.StartTurnNumber <= verifiedLastTurn).ToArray(),
            verifiedPrefixActionCount,
            verifiedLastTurn,
            summary,
            reason);
}
