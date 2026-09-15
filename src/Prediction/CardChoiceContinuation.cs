using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// A discovery snapshot and this checkpoint temporarily refer to the same suspended seed.
// The snapshot is read only and released before resumes begin; no ordinary Fork may use it.
internal sealed class CardChoiceContinuation : IDisposable
{
    private readonly object _gate = new();
    private CombatPredictionSimulator? _seed;
    private ManualCardChoiceFrame? _frame;
    private ForkableSet<uint>? _deaths;

    private CardChoiceContinuation(CombatPredictionSimulator seed, ManualCardChoiceFrame frame,
        ForkableSet<uint> deaths)
    { _seed = seed; _frame = frame; _deaths = deaths; }

    internal static CardChoiceContinuation? Take(CombatPredictionSimulator seed, ForkableSet<uint> deaths)
    {
        ManualCardChoiceFrame? frame = seed.TakeManualCardChoiceFrame();
        return frame is null ? null : new CardChoiceContinuation(seed, frame, deaths.Fork());
    }

    internal (CombatPredictionSimulator Simulator, ManualCardChoiceFrame Frame, ForkableSet<uint> Deaths)
        Fork(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_seed is null, this);
            CombatPredictionSimulator child = _seed.ForkManualCardChoice(_frame!, out var frame);
            return (child, frame, _deaths!.Fork());
        }
    }

    public void Dispose()
    {
        lock (_gate) { _seed = null; _frame = null; _deaths = null; }
    }
}
