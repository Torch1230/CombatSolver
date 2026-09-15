namespace CombatSolver.Engine.Common;

// Domain-owned choice/card/death transactions participate in the explicit continuation;
// the engine never guesses at concrete Search or SimulatedCombatState fields.
internal interface ICombatPredictionCardContinuationState
{
    bool CanCaptureManualCardChoice { get; }
    ICombatPredictionCapturedCardChoice CaptureManualCardChoice();
    IDisposable DetachPendingManualCardChoice();
}

// Preserve the exact request: some selectors consume RNG while constructing their options.
// Its domain implementation owns option remapping and invokes the existing choice resolver.
internal interface ICombatPredictionCapturedCardChoice
{
    ICombatPredictionCapturedCardChoice Fork(PredictionForkContext context);
    bool Resolve(InCombat.Simulation.CombatPredictionSimulator simulator, PredictedCard card);
}
