using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

/// <summary>
/// Main-thread admission owns all model metadata. Workers receive immutable initial values
/// and create private execution/read lanes. Runtime selection remains disabled until the
/// complete supported-domain search differential has passed.
/// </summary>
internal sealed class CompactCombatRoot
{
    internal CompactDiscardProjection Adapter { get; }
    internal ResumableDiscardProgram.Candidate Initial { get; }
    private long _completedReplays, _pendingReplays, _materializations;
    internal (long CompletedReplays, long PendingReplays, long Materializations) Counts
        => (Interlocked.Read(ref _completedReplays), Interlocked.Read(ref _pendingReplays), Interlocked.Read(ref _materializations));

    internal CompactCombatRoot(CombatPredictionSimulator capturedRoot, Player player)
    {
        Adapter = new(capturedRoot, player, includeAttacks: true, includeHandEnd: true,
            includeMechaMoves: true, includePowerPhases: true, includeMechaAi: true, includeRounds: true);
        Initial = Adapter.Program.Freeze();
    }

    internal void RecordReplay(bool completed)
    {
        if (completed) Interlocked.Increment(ref _completedReplays);
        else Interlocked.Increment(ref _pendingReplays);
    }

    internal CombatPredictionSimulator Materialize(ResumableDiscardProgram.Candidate values)
    {
        Interlocked.Increment(ref _materializations);
        return Adapter.Materialize(values.Open());
    }
}

/// <summary>Published values and derived history scalar; never owns a worker or mutable graph.</summary>
internal sealed record CompactCombatCandidate(
    CompactCombatRoot Root,
    ResumableDiscardProgram.Candidate Values,
    int LastImpureHistoryIndex)
{
    internal CombatPredictionSimulator Materialize() => Root.Materialize(Values);
}
