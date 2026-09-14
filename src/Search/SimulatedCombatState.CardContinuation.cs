using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState : ICombatPredictionCardContinuationState
{
    private bool HasOwnManualDiscardRequest => PendingTurnStartChoice is
        { SourceId: "", Effect: PlanChoiceEffect.Discard, SourcePile: PileType.Hand,
            Timing: PlanChoiceTiming.Action, Spec: not null };

    bool ICombatPredictionCardContinuationState.CanCaptureManualCardChoice =>
        _activeActionChoices is { IsEmptyExplicitChoiceCursor: true }
        && _activeActionChoiceTiming == PlanChoiceTiming.Action
        && _cardExecutionScopeDepth == 1 && _activeCardExecutionDeaths != null
        && !_playerTurnEndRequested && _powerCardSources is not { Count: > 0 }
        && PendingKnowledgeDemonChoice is null && _pendingPowerAmountChanges is not { Count: > 0 }
        && _unsettlingLampTriggeringCards is not { Count: > 0 }
        && _unsettlingLampInternalPowerTypes is not { Count: > 0 }
        && HasOwnManualDiscardRequest;

    IDisposable ICombatPredictionCardContinuationState.DetachPendingManualCardChoice()
    {
        if (_activeActionChoices != null || _cardExecutionScopeDepth != 0
            || _activeCardExecutionDeaths != null || !HasOwnManualDiscardRequest)
            throw new InvalidOperationException("Manual choice continuation still owns active CLR scopes or lost its request.");
        var scope = new DetachedManualChoice(this, PendingTurnStartChoice!);
        ClearPendingTurnStartChoice();
        return scope;
    }

    private sealed class DetachedManualChoice(SimulatedCombatState owner, TurnStartChoiceRequest request) : IDisposable
    {
        public void Dispose() => owner.SetPendingTurnStartChoice(request);
    }
}
