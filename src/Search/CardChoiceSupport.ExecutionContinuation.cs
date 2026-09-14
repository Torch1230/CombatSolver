using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

internal static partial class CardChoiceSupport
{
    private static bool ContinueRepeatedSelection(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        PredictedCard source, PredictedCard selected, ISet<uint> deaths, int nextIndex, int? awaitedHistoryStart)
    {
        if (awaitedHistoryStart is { } start)
        {
            bool played = false;
            foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(start))
                if (entry is CombatPredictionCardPlayStartedEntry cardPlay && ReferenceEquals(cardPlay.Card.Original, selected.Original))
                { played = true; break; }
            if (!played) return true;
        }
        for (int index = nextIndex; index < source.Preview.DynamicVars.Repeat.IntValue; index++)
        {
            int historyStart = simulator.History.Entries.Count;
            bool played = CardExecutionSupport.AutoPlay(simulator, combat, selected, target: null, deaths,
                nestedChoiceSourceId: source.Preview.Id.Entry);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(new RepeatedSelectionExecutionFrame(source, selected, deaths, index + 1, historyStart));
                return false;
            }
            if (!played) break;
        }
        return true;
    }

    private sealed record RepeatedSelectionExecutionFrame(PredictedCard Source, PredictedCard Selected,
        ISet<uint> Deaths, int NextIndex, int AwaitedHistoryStart) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Source = context.RequireRemap(Source), Selected = context.RequireRemap(Selected), Deaths = context.RequireRemap(Deaths) };
        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueRepeatedSelection(simulator, (SimulatedCombatState)simulator.State.CombatState,
                Source, Selected, Deaths, NextIndex, AwaitedHistoryStart);
    }

    private sealed record PostSelectionExecutionFrame(PredictedCard Card) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this with { Card = context.RequireRemap(Card) };
        public bool Resume(CombatPredictionSimulator simulator)
            => ApplyPostChoiceEffects(simulator, (SimulatedCombatState)simulator.State.CombatState, Card);
    }

    private sealed record RestoreBurningPactPileFrame(PredictedCard Card) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this with { Card = context.RequireRemap(Card) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            if (Card.GetPile(simulator.State)?.Type == PileType.Play) simulator.AddToPile(Card, PileType.Discard);
            return !simulator.HasPendingChoice;
        }
    }
}
