using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using System.Diagnostics.CodeAnalysis;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

/// <summary>
/// Main-thread admission owns all model metadata. Workers receive immutable initial values
/// and create private execution/read lanes. Runtime uses this only after full-root
/// capability admission; unrepresented domains retain the existing model backend.
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

    // Only admission can choose the old backend. No execution or read exception is
    // caught here, and a solver never changes backend after publishing its root.
    internal static bool TryCreate(CombatPredictionSimulator capturedRoot, Player player,
        [NotNullWhen(true)] out CompactCombatRoot? admitted, out string rejection)
    {
        try
        {
            admitted = new(capturedRoot, player);
            rejection = "";
            return true;
        }
        catch (NotSupportedException error)
        {
            admitted = null;
            rejection = error.Message;
            return false;
        }
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
