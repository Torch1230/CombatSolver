namespace CombatSolver.Engine.Common;

internal sealed partial class PredictionStateStore
{
    // A started CardPlay is copied explicitly by the continuation. Other transactions and
    // opaque external states have no such protocol and must retain full action replay.
    internal bool SupportsManualCardChoiceContinuation
    {
        get
        {
            if (_states is null) return true;
            foreach (object state in _states.Values)
                if (state.GetType().Assembly != typeof(PredictionStateStore).Assembly
                    || state is IPredictionForkBoundary)
                    return false;
            return true;
        }
    }
}
