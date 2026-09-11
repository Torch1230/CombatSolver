using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private ManualResetEventSlim? _turnSetupWorkerGate;
    private int _turnSetupControlStage;
    private bool _turnSetupPowerInstalled;
    private long _manualChoiceCompletedAt;

    private void InitializeTurnSetupControlCheck()
    {
        _turnSetupWorkerGate = new(false);
        PlayerTurnSetupCoordinator.BeforeChoiceSearchForTesting = token => _turnSetupWorkerGate.Wait(token);
    }

    private void ReleaseTurnSetupControlCheck()
    {
        if (_turnSetupWorkerGate == null) return;
        PlayerTurnSetupCoordinator.BeforeChoiceSearchForTesting = null;
        _turnSetupWorkerGate.Set();
        _turnSetupWorkerGate.Dispose();
        _turnSetupWorkerGate = null;
    }

    private static bool HasChoiceBlocker(Node node)
        => node.Name.ToString().StartsWith("CombatSolverChoiceBlocker", StringComparison.Ordinal)
            || node.GetChildren().Any(HasChoiceBlocker);

    private async Task<bool> AdvanceTurnSetupControlCheckAsync(CombatState state, Player? player)
    {
        if (!_turnSetupPowerInstalled && _turnSetupControlStage == 0
            && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && (_request.ScenarioId.EndsWith("-TOOLS", StringComparison.Ordinal)
                || _request.ScenarioId.EndsWith("-TYRANNY", StringComparison.Ordinal)))
        {
            _turnSetupPowerInstalled = true;
            if (_request.ScenarioId.EndsWith("-TOOLS", StringComparison.Ordinal))
                await PowerCmd.Apply<ToolsOfTheTradePower>(new ThrowingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            else
                await PowerCmd.Apply<TyrannyPower>(new ThrowingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            int turn = player.PlayerCombatState.TurnNumber;
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            return false;
        }
        if (_turnSetupControlStage == 0 && PlayerTurnSetupCoordinator.IsInitialChoiceSearchPendingForTesting(state))
        {
            if (HasChoiceBlocker(_host)) throw new InvalidOperationException("Initial choice search blocks native input.");
            if (_request.ScenarioId.EndsWith("-MANUAL", StringComparison.Ordinal))
            {
                _turnSetupControlStage = 6;
                await PlayerTurnSetupCoordinator.InteractWithPendingChoiceForTesting(_host, confirm: true);
                _manualChoiceCompletedAt = System.Environment.TickCount64;
                _completedChecks.Add("TurnSetupControls:ManualSelectionWhileWorkerPending");
                return false;
            }
            SolverController.StopSearchByUser(_host);
            if (_request.ScenarioId.EndsWith("-FULL-AUTO", StringComparison.Ordinal))
            {
                SolverController.SetFullAuto(_host, state, true);
                if (!PlayerTurnSetupCoordinator.TakeoverRequestedForTesting || !SolverController.FullAutoEnabled)
                    throw new InvalidOperationException("Full-auto takeover was lost while the stopped worker was draining.");
                _turnSetupWorkerGate!.Set();
                _turnSetupControlStage = 7;
                return false;
            }
            _turnSetupControlStage = 1;
        }
        else if (_turnSetupControlStage == 1 && !SolverController.IsSearching)
        {
            if (!PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state) || !SolverController.CanExecuteCurrentTurn
                || SolverController.IsAdoptingCurrentRoute || HasChoiceBlocker(_host))
                throw new InvalidOperationException("Stopping initial search lost the pending native choice or controls.");
            SolverController.RequestSearch(_host, state, SearchReason.Manual);
            _turnSetupControlStage = 2;
        }
        else if (_turnSetupControlStage == 2 && PlayerTurnSetupCoordinator.IsSearching)
        {
            if (HasChoiceBlocker(_host)) throw new InvalidOperationException("Manual choice search blocks native input.");
            SolverController.StopSearchByUser(_host);
            _turnSetupControlStage = 3;
        }
        else if (_turnSetupControlStage == 3 && !SolverController.IsSearching)
        {
            if (!PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state) || SolverController.IsAdoptingCurrentRoute)
                throw new InvalidOperationException("Stopping a recalculation lost its choice session.");
            _completedChecks.Add("TurnSetupControls:StopInitial:Restart:StopManual:NativeInputAvailable");
            SolverController.RequestSearch(_host, state, SearchReason.Manual);
            _turnSetupWorkerGate!.Set();
            _turnSetupControlStage = 4;
        }
        else if (_turnSetupControlStage == 4 && !SolverController.IsSearching
            && PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state))
        {
            if (SolverController.IsAdoptingCurrentRoute || SolverController.IsApplyingCurrentTurn || HasChoiceBlocker(_host))
                throw new InvalidOperationException("Completed choice search retained takeover state or input blocker.");
            if (_request.ScenarioId.EndsWith("-PARTIAL", StringComparison.Ordinal))
                await PlayerTurnSetupCoordinator.InteractWithPendingChoiceForTesting(_host, confirm: false);
            if (!PlayerTurnSetupCoordinator.TryContinuePlannedChoice(_host, state, deployAfterSetup: false))
                throw new InvalidOperationException("Refreshed native choice could not be driven.");
            _turnSetupControlStage = 5;
        }
        if (_turnSetupControlStage is 5 or 6 or 7 && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && !PlayerTurnSetupCoordinator.IsManaging(state) && !SolverController.IsSearching)
        {
            if (SolverController.LastSearchFailureForTesting is { } error) throw error;
            if (SolverController.IsAdoptingCurrentRoute || SolverController.IsApplyingCurrentTurn || HasChoiceBlocker(_host))
                throw new InvalidOperationException("Choice completion retained busy controls.");
            if (_turnSetupControlStage == 6 && SolverController.LastTurnSetupResultForTesting != null)
                throw new InvalidOperationException("Manual choice installed a stale setup result.");
            if (_request.ScenarioId.EndsWith("-PARTIAL", StringComparison.Ordinal) && SolverController.UnexpectedReplanCount != 0)
                throw new InvalidOperationException("Partial hand selection changed the planned choice result.");
            if (_turnSetupControlStage == 7)
            {
                if (!NativeChoiceRuntime.TraceSnapshotForTesting.Any(trace => trace.Owner.StartsWith("turn_setup:") && trace.Stage == "Selected"))
                    throw new InvalidOperationException("Full auto reached Play without driving the native choice.");
                SolverController.SetFullAuto(_host, state, false);
                _completedChecks.Add("TurnSetupControls:StopThenImmediateFullAuto:QueuedAcrossCancellation:NativeChoiceDriven");
            }
            _completedChecks.Add("TurnSetupControls:NativePlayReached:BusyFlagsCleared");
            return true;
        }
        if (_turnSetupControlStage == 6 && System.Environment.TickCount64 - _manualChoiceCompletedAt > 8000)
            throw new InvalidOperationException("Manual choice did not retire its search: " + PlayerTurnSetupCoordinator.DescribeControlsForTesting()
                + $" controller_search={SolverController.IsSearching} actions_disabled={CombatManager.Instance.PlayerActionsDisabled}");
        await Task.CompletedTask;
        return false;
    }
}
