namespace CombatSolver.Engine.Common;

internal sealed partial class PredictionTrace
{
    internal TraceScope ResumeManualCardChoice(PredictionTraceFrame frame)
    {
        if (_depth != 0 || frame.Parent != null || frame.Invocation.Action != PredictionActionKind.CardPlay)
            throw new InvalidOperationException("Manual card continuation requires an inactive trace and one root CardPlay frame.");
        _slots[0] = new Slot { Source = frame.Source, Invocation = frame.Invocation, Materialized = frame };
        _depth = 1;
        return new TraceScope(this, 1);
    }
}
