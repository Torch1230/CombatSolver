using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState : ICombatPredictionExecutionContinuationState
{
    bool ICombatPredictionExecutionContinuationState.CanSealExecutionPrefix =>
        _activeActionChoices is null && _cardExecutionScopeDepth == 0 && _activeCardExecutionDeaths is null
        && !HasPendingChoice && !_playerTurnEndRequested
        && _powerCardSources is not { Count: > 0 } && _pendingPowerAmountChanges is not { Count: > 0 }
        && _unsettlingLampTriggeringCards is not { Count: > 0 }
        && _unsettlingLampInternalPowerTypes is not { Count: > 0 };

    internal IDisposable SuspendExecutionChoicesForStablePrefix()
    {
        if (HasPendingChoice || _activeActionChoices is null)
            throw new InvalidOperationException("Stable execution prefix requires an active cursor without a request.");
        var scope = new SuspendedPrefixCursor(this, _activeActionChoices, _activeActionChoiceTiming);
        _activeActionChoices = null;
        _activeActionChoiceTiming = PlanChoiceTiming.Action;
        return scope;
    }

    private sealed class SuspendedPrefixCursor(SimulatedCombatState owner, TurnStartChoiceCursor cursor, PlanChoiceTiming timing) : IDisposable
    {
        public void Dispose()
        {
            if (owner._activeActionChoices != null || owner.HasPendingChoice)
                throw new InvalidOperationException("Stable execution prefix changed the suspended choice transaction.");
            owner._activeActionChoices = cursor;
            owner._activeActionChoiceTiming = timing;
        }
    }
    PredictedCard? ICombatPredictionExecutionContinuationState.FindExecutionCard(MegaCrit.Sts2.Core.Models.CardModel card)
        => _registeredCombatCards?.FirstOrDefault(candidate => candidate.References(card));

    bool ICombatPredictionExecutionContinuationState.CanCaptureExecutionContinuation =>
        _activeActionChoices is null && _cardExecutionScopeDepth == 0 && _activeCardExecutionDeaths is null
        && PendingTurnStartChoice is { Spec: not null, Effect: not PlanChoiceEffect.ModDefined }
        && PendingKnowledgeDemonChoice is null && !_playerTurnEndRequested
        && _powerCardSources is not { Count: > 0 } && _pendingPowerAmountChanges is not { Count: > 0 }
        && _unsettlingLampTriggeringCards is not { Count: > 0 }
        && _unsettlingLampInternalPowerTypes is not { Count: > 0 };

    IDisposable ICombatPredictionExecutionContinuationState.DetachPendingExecutionChoice()
    {
        if (!((ICombatPredictionExecutionContinuationState)this).CanCaptureExecutionContinuation)
            throw new InvalidOperationException("Execution continuation still owns a transaction or lost its request.");
        var scope = new DetachedManualChoice(this, PendingTurnStartChoice!);
        ClearPendingTurnStartChoice();
        return scope;
    }

    internal TurnStartChoiceCursor ActiveExecutionChoices => _activeActionChoices
        ?? throw new InvalidOperationException("Resuming execution without an active choice cursor.");

    internal static void PrepareExecutionChoiceFork(TurnStartChoiceRequest request, PredictionForkContext context)
    {
        foreach (PredictedCard card in request.Spec!.Options.Concat(request.Spec.SourceCards))
            if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
    }

    internal static TurnStartChoiceRequest ForkExecutionChoice(TurnStartChoiceRequest request, PredictionForkContext context)
        => request with { Spec = request.Spec! with
        {
            Options = request.Spec.Options.Select(context.RequireRemap).ToArray(),
            SourceCards = request.Spec.SourceCards.Select(context.RequireRemap).ToArray(),
        } };

    internal sealed record TurnSelectionExecutionFrame(Player Player, TurnStartChoiceRequest Request)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => PrepareExecutionChoiceFork(Request, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Request = ForkExecutionChoice(Request, context) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return TurnStartChoiceSupport.ResolveCapturedChoice(simulator, combat, Player,
                combat.ActiveExecutionChoices, Request);
        }
    }

    internal sealed record CardSelectionExecutionFrame(PredictedCard Card, TurnStartChoiceRequest Request)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => PrepareExecutionChoiceFork(Request, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Request = ForkExecutionChoice(Request, context) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return combat.ResolveActionCardChoice(simulator, Card, Request.SourceId, Request.Spec!,
                combat._activeCardExecutionDeaths
                    ?? throw new InvalidOperationException("Resuming a card selector without its execution scope."), Request.ContextId);
        }
    }
}
