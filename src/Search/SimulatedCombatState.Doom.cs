using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    public void DoomKill(CombatPredictionSimulator simulator, IReadOnlyList<Creature> creatures)
    {
        if (creatures.Count == 0)
            return;
        Dictionary<Player, int> fatalCounts = [];
        foreach (Player player in Players)
        {
            int count = creatures.Count(creature => creature != player.Creature
                && EffectivePowers()
                    .Where(power => power.Owner == creature)
                    .All(power => power.ShouldOwnerDeathTriggerFatal()));
            fatalCounts[player] = count;
        }
        foreach (Creature creature in creatures)
        {
            if (!simulator.Kill(creature))
                return;
        }
        foreach ((Player player, int count) in fatalCounts)
        {
            if (count <= 0)
                continue;
            foreach (BookRepairKnife relic in RelicsOf(player)
                         .OfType<BookRepairKnife>()
                         .Where(relic => !relic.IsMelted))
            {
                simulator.Heal(player.Creature, relic.DynamicVars.Heal.BaseValue * count);
                if (simulator.HasPendingChoice)
                    return;
            }
        }
    }
}
