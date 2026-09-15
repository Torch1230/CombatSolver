using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class TurnStartChoiceSupport
{
    internal static bool ContinueExhaustSelection(CombatPredictionSimulator simulator,
        IReadOnlyList<PredictedCard> selected, int nextIndex)
    {
        for (int index = nextIndex; index < selected.Count; index++)
        {
            simulator.Exhaust(selected[index]);
            if (!simulator.HasPendingChoice) continue;
            simulator.AppendExecutionContinuation(new ExhaustSelectionExecutionFrame(selected, index + 1));
            return false;
        }
        return true;
    }

    private sealed record ExhaustSelectionExecutionFrame(IReadOnlyList<PredictedCard> Selected, int NextIndex)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            foreach (PredictedCard card in Selected)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Selected = Selected.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator) => ContinueExhaustSelection(simulator, Selected, NextIndex);
    }
}
