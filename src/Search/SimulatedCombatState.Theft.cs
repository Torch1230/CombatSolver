using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private int? _outstandingStolenGold;
    private int? _outstandingStolenCards;

    public int OutstandingStolenResource(CombatPredictionSimulator simulator)
    {
        EnsureOutstandingStolenResourcesInitialized(simulator);
        return _outstandingStolenGold!.Value + _outstandingStolenCards!.Value;
    }

    public (int Gold, int Cards) UnrecoveredLoot(CombatPredictionSimulator simulator)
    {
        EnsureOutstandingStolenResourcesInitialized(simulator);
        return (_outstandingStolenGold!.Value, _outstandingStolenCards!.Value);
    }

    public void RecordStolenCard(CombatPredictionSimulator simulator)
    {
        EnsureOutstandingStolenResourcesInitialized(simulator);
        _outstandingStolenCards++;
    }

    public void RecoverStolenResources(CombatPredictionSimulator simulator, Creature dead)
    {
        EnsureOutstandingStolenResourcesInitialized(simulator);
        foreach (var power in EffectivePowers().Where(power => ReferenceEquals(power.Owner, dead)))
        {
            switch (power)
            {
                case SwipePower { StolenCard: not null }:
                    _outstandingStolenCards = Math.Max(0, _outstandingStolenCards.GetValueOrDefault() - 1);
                    break;
                case HeistPower heist:
                    _outstandingStolenGold = Math.Max(0, _outstandingStolenGold.GetValueOrDefault() - heist.Amount);
                    break;
            }
        }
    }

    private void RecordStolenGold(CombatPredictionSimulator simulator, int amount)
    {
        EnsureOutstandingStolenResourcesInitialized(simulator);
        _outstandingStolenGold += amount;
    }

    private void EnsureOutstandingStolenResourcesInitialized(CombatPredictionSimulator simulator)
    {
        if (_outstandingStolenGold.HasValue)
            return;
        int gold = 0;
        int cards = 0;
        foreach (var power in EffectivePowers())
        {
            if (!simulator.State.GetCreature(power.Owner).IsAlive)
                continue;
            switch (power)
            {
                case SwipePower { StolenCard: not null }:
                    cards++;
                    break;
                case ThieveryPower thievery:
                    gold += Math.Max(0, thievery.DynamicVars.Gold.IntValue);
                    break;
                case HeistPower heist:
                    gold += Math.Max(0, heist.Amount);
                    break;
            }
        }
        _outstandingStolenGold = gold;
        _outstandingStolenCards = cards;
    }
}
