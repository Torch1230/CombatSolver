using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardOnPlayMirrors
{
    private static bool ApplyRemainingCardSpec(CombatPredictionSimulator simulator, PredictedCard card, Creature? target)
    {
        using (simulator.BeginExecutionDispatch())
            if (simulator.State.CombatState is SimulatedCombatState combat)
                CardEffectSpecRegistry.Apply(simulator, combat, card, target);
        return !simulator.HasPendingChoice;
    }

    private sealed record CardSpecExecutionFrame(PredictedCard Card, Creature? Target) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card) };
        public bool Resume(CombatPredictionSimulator simulator) => ApplyRemainingCardSpec(simulator, Card, Target);
    }
}
