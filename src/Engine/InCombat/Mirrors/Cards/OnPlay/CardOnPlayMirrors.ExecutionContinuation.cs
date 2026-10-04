using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardOnPlayMirrors
{
    private static bool ApplyRemainingCardSpec(CombatPredictionSimulator simulator, PredictedCard card, CardPlay play)
    {
        using (simulator.BeginExecutionDispatch())
            if (simulator.State.CombatState is SimulatedCombatState combat)
                CardEffectSpecRegistry.Apply(simulator, combat, card, play.Target, play);
        return !simulator.HasPendingChoice;
    }

    private sealed record CardSpecExecutionFrame(PredictedCard Card, CardPlay Play) : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CardPlay> ActiveCardPlays => [Play];
        public void PrepareFork(PredictionForkContext context) => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Play = context.RequireRemap(Play) };
        public bool Resume(CombatPredictionSimulator simulator) => ApplyRemainingCardSpec(simulator, Card, Play);
    }
}
