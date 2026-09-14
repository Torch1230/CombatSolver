using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Test orchestration only: all frame capture, cloning and card semantics are production code.
    private static CardContinuationContractCheckpoint? CaptureCardContinuationContract(
        CombatPredictionSimulator simulator, PredictedCard card, Creature? target, ForkableSet<uint> deaths)
    {
        if (!simulator.BeginManualCardChoiceCapture(card)) return null;
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        bool returned = false;
        try
        {
            using (combat.BeginCardExecutionScope(deaths)) simulator.ManualPlay(card, target, out _);
            returned = true;
        }
        finally
        {
            simulator.EndManualCardChoiceCapture();
            if (returned) combat.EndActionChoices();
        }
        CardChoiceContinuation? continuation = CardChoiceContinuation.Take(simulator, deaths);
        return continuation is null ? null : new CardContinuationContractCheckpoint(continuation);
    }

    private sealed class CardContinuationContractCheckpoint(CardChoiceContinuation continuation) : IDisposable
    {
        public (CombatPredictionSimulator Simulator, ForkableSet<uint> Deaths, bool Completed) Resume(
            IReadOnlyList<PlanCardChoice> choices, CancellationToken token, Action? insideChildScope = null)
        {
            var child = continuation.Fork(token);
            var combat = (SimulatedCombatState)child.Simulator.State.CombatState;
            combat.BeginActionChoices(choices);
            bool returned = false;
            try
            {
                using (combat.BeginCardExecutionScope(child.Deaths))
                {
                    insideChildScope?.Invoke();
                    token.ThrowIfCancellationRequested();
                    bool complete = child.Simulator.ResumeManualCardChoice(child.Frame);
                    returned = true;
                    return (child.Simulator, child.Deaths, complete);
                }
            }
            finally
            {
                // A failed child is unreachable. Do not mask the original error with cursor
                // validation, or turn failure cleanup into a reusable snapshot boundary.
                if (returned) combat.EndActionChoices();
            }
        }
        public void Dispose() => continuation.Dispose();
    }
}
