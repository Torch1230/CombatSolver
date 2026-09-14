namespace CombatSolver.Engine.Common;

// Domain-owned choice/card/death transactions participate in the explicit continuation;
// the engine never guesses at concrete Search or SimulatedCombatState fields.
internal interface ICombatPredictionCardContinuationState
{
    bool CanCaptureManualCardChoice { get; }
    IDisposable DetachPendingManualCardChoice();
}
