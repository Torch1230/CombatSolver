using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    internal RelicCounterEvaluation EvaluateRelicCounters(CombatPredictionSimulator simulator, Player player,
        IReadOnlyList<RelicCounterTarget> targets)
    {
        RelicCounterEvaluation result = default;
        foreach (var target in targets)
        {
            RelicModel relic = RelicsOf(player).Single(relic => RelicCounterCatalog.Identify(relic) == target.Id);
            int value = ReadRelicCounter(simulator, relic) % target.Period;
            result = RelicCounterPolicy.Add(result, target, value);
        }
        return result;
    }

    internal int ReadRelicCounter(CombatPredictionSimulator simulator, RelicModel relic) => relic switch
    {
        MeatOnTheBone meat => simulator.State.GetCreature(meat.Owner.Creature).CurrentHp * 100
            <= simulator.State.GetCreature(meat.Owner.Creature).MaxHp * meat.DynamicVars[MeatOnTheBone._hpThresholdKey].IntValue ? 1 : 0,
        HappyFlower or FakeHappyFlower or Pendulum or PollinousCore or GalacticDust => GetStatefulRelicState(relic).Current,
        Nunchaku n => RelicPredictionStateSupport.GetCounterValue(simulator, n, n.AttacksPlayed),
        TuningFork t => RelicPredictionStateSupport.GetCounterValue(simulator, t, t.SkillsPlayed),
        IronClub i => RelicPredictionStateSupport.GetCounterValue(simulator, i, i.CardsPlayed),
        JossPaper j => RelicPredictionStateSupport.GetJossPaperCardsExhausted(simulator, j),
        PenNib p => simulator.StateStore.TryGetReadOnly((AbstractModel)p, out PenNibPredictionState? state)
            ? state!.AttacksPlayed : throw new InvalidOperationException("Missing captured PenNib state."),
        _ => throw new ArgumentException("Unregistered relic counter.", nameof(relic)),
    };
}
