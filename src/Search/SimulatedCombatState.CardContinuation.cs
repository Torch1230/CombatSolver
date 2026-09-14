using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState : ICombatPredictionCardContinuationState
{
    internal bool CanCaptureStableChoicePrefix => _activeActionChoices is null
        && _cardExecutionScopeDepth == 0 && _activeCardExecutionDeaths is null
        && !_playerTurnEndRequested && _powerCardSources is not { Count: > 0 }
        && !HasPendingChoice && _pendingPowerAmountChanges is not { Count: > 0 }
        && _unsettlingLampTriggeringCards is not { Count: > 0 }
        && _unsettlingLampInternalPowerTypes is not { Count: > 0 };

    private bool HasOwnManualChoiceRequest => PendingTurnStartChoice is
        { SourceId: "", Effect: not PlanChoiceEffect.ModDefined,
            Timing: PlanChoiceTiming.Action, Spec: not null };

    bool ICombatPredictionCardContinuationState.CanCaptureManualCardChoice =>
        _activeActionChoices is { IsEmptyExplicitChoiceCursor: true }
        && _activeActionChoiceTiming == PlanChoiceTiming.Action
        && _cardExecutionScopeDepth == 1 && _activeCardExecutionDeaths != null
        && !_playerTurnEndRequested && _powerCardSources is not { Count: > 0 }
        && PendingKnowledgeDemonChoice is null && _pendingPowerAmountChanges is not { Count: > 0 }
        && _unsettlingLampTriggeringCards is not { Count: > 0 }
        && _unsettlingLampInternalPowerTypes is not { Count: > 0 }
        && HasOwnManualChoiceRequest;

    ICombatPredictionCapturedCardChoice ICombatPredictionCardContinuationState.CaptureManualCardChoice()
        => HasOwnManualChoiceRequest ? new CapturedManualChoice(PendingTurnStartChoice!)
            : throw new InvalidOperationException("Manual continuation lost its own choice request.");

    IDisposable ICombatPredictionCardContinuationState.DetachPendingManualCardChoice()
    {
        if (_activeActionChoices != null || _cardExecutionScopeDepth != 0
            || _activeCardExecutionDeaths != null || !HasOwnManualChoiceRequest)
            throw new InvalidOperationException("Manual choice continuation still owns active CLR scopes or lost its request.");
        var scope = new DetachedManualChoice(this, PendingTurnStartChoice!);
        ClearPendingTurnStartChoice();
        return scope;
    }

    private sealed class DetachedManualChoice(SimulatedCombatState owner, TurnStartChoiceRequest request) : IDisposable
    {
        public void Dispose() => owner.SetPendingTurnStartChoice(request);
    }

    private sealed class CapturedManualChoice(TurnStartChoiceRequest request) : ICombatPredictionCapturedCardChoice
    {
        public ICombatPredictionCapturedCardChoice Fork(PredictionForkContext context)
        {
            CardChoiceSpec spec = request.Spec!;
            return new CapturedManualChoice(request with { Spec = spec with
            {
                Options = spec.Options.Select(context.RequireRemap).ToArray(),
                SourceCards = spec.SourceCards.Select(context.RequireRemap).ToArray(),
            } });
        }

        public bool Resolve(CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
            PredictedCard card)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return combat.ResolveActionCardChoice(simulator, card, request.SourceId, request.Spec!,
                combat._activeCardExecutionDeaths
                    ?? throw new InvalidOperationException("Resuming card choice without its execution scope."),
                request.ContextId);
        }
    }
}
