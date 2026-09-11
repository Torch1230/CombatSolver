using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    public TargetType GetTargetType(PredictedCard card)
    {
        if (State.CombatState is not SimulatedCombatState combat)
            return card.Preview.TargetType;

        return card.Preview switch
        {
            Shiv => combat.GetAmount<FanOfKnivesPower>(card.Preview.Owner.Creature) > 0
                ? TargetType.AllEnemies : TargetType.AnyEnemy,
            SovereignBlade => combat.GetAmount<SeekingEdgePower>(card.Preview.Owner.Creature) > 0
                ? TargetType.AllEnemies : TargetType.AnyEnemy,
            _ => card.Preview.TargetType,
        };
    }
}
