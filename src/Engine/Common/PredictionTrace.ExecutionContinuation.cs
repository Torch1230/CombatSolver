namespace CombatSolver.Engine.Common;

internal sealed partial class PredictionTrace
{
    internal IDisposable ResumeExecution(PredictionTraceFrame? frame)
    {
        if (_depth != 0) throw new InvalidOperationException("Execution continuation requires an inactive trace.");
        PredictionTraceFrame[] ancestors = frame?.Ancestors().Reverse().ToArray() ?? [];
        if (_slots.Length < ancestors.Length) Array.Resize(ref _slots, ancestors.Length);
        for (int index = 0; index < ancestors.Length; index++)
        {
            PredictionTraceFrame item = ancestors[index];
            _slots[index] = new Slot { Source = item.Source, Invocation = item.Invocation, Materialized = item };
        }
        _depth = ancestors.Length;
        return new ResumedExecutionScope(this, _depth);
    }

    private sealed class ResumedExecutionScope(PredictionTrace trace, int depth) : IDisposable
    {
        public void Dispose()
        {
            if (trace._depth != depth) throw new InvalidOperationException("Execution continuation trace did not unwind.");
            while (trace._depth > 0) trace.Pop(trace._depth);
        }
    }
}
