using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct PowerCardPlayOccurrence(
    string CardId,
    PowerCommitmentDescriptor Descriptor,
    bool IsAutoPlay);

internal sealed partial class CombatBeamSolver
{
    private static IReadOnlyList<PowerCardPlayOccurrence> RegisteredPowerPlays(
        SearchNode parent,
        SearchNode child)
    {
        List<PowerCardPlayOccurrence> plays = [];
        foreach (CombatPredictionHistoryEntry entry in child.Snapshot.Simulator.History
                     .EntriesFrom(parent.Snapshot.HistoryEntryCount))
        {
            if (entry is not CombatPredictionCardPlayStartedEntry started
                || !PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                    started.Card.Id,
                    out PowerCommitmentDescriptor descriptor))
            {
                continue;
            }
            plays.Add(new PowerCardPlayOccurrence(
                started.Card.Id,
                descriptor,
                started.CardPlay.IsAutoPlay));
        }
        if (plays.Count > 0)
            return plays;

        PlanAction? action = child.Action;
        if (action?.Kind == PlanActionKind.PlayCard
            && PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                action.CardId,
                out PowerCommitmentDescriptor directDescriptor))
        {
            plays.Add(new PowerCardPlayOccurrence(
                action.CardId,
                directDescriptor,
                IsAutoPlay: false));
        }
        return plays;
    }
}
