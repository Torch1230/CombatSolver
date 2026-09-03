using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class MonsterSpawnSupport
{
    public static Creature Spawn<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        string? slot,
        bool minion = false,
        Action<T>? configure = null)
        where T : MonsterModel
    {
        Creature creature = Create(simulator, combat, slot, configure);
        AddCreated(simulator, combat, source, creature, minion);
        return creature;
    }

    public static Creature Create<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        string? slot,
        Action<T>? configure = null)
        where T : MonsterModel
    {
        T monster = (T)CanonicalModels.Monster<T>().ToMutable();
        configure?.Invoke(monster);
        return combat.CreatePredictedMonster(simulator, monster, CombatSide.Enemy, slot);
    }

    public static void AddCreated(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        Creature creature,
        bool minion = false)
    {
        combat.AddPredictedMonster(creature);
        ApplyNativeEntrancePowers(combat, creature);
        if (minion && combat.GetAmount<MinionPower>(creature) <= 0)
            combat.Apply<MinionPower>(creature, 1, source);
        if (creature.Side == CombatSide.Enemy && combat.Modifiers.Any(static modifier => modifier is Murderous))
            combat.Apply<StrengthPower>(creature, 3);
        ApplyCreatureAddedRelics(simulator, combat, creature);
        combat.PreparePredictedMonster(simulator, creature);
    }

    public static string? NextSlot(SimulatedCombatState combat)
        => combat.NextFreeSlot();

    public static string? LastFreeSlot(SimulatedCombatState combat)
        => combat.LastFreeSlot();

    private static void ApplyNativeEntrancePowers(SimulatedCombatState combat, Creature creature)
    {
        switch (creature.Monster)
        {
            case GasBomb:
                combat.Apply<MinionPower>(creature, 1, creature);
                break;
            case EyeWithTeeth or Parafright:
                combat.Apply<IllusionPower>(creature, 1, creature);
                combat.Apply<MinionPower>(creature, 1, null);
                break;
            case ToughEgg:
                combat.Apply<HatchPower>(creature, combat.CurrentSide == CombatSide.Enemy ? 2 : 1, creature);
                break;
            case Zapbot:
                combat.Apply<HighVoltagePower>(creature, 2, creature);
                break;
            case Axebot axebot when axebot.StockAmount > 0:
                combat.Apply<StockPower>(creature, axebot.StockAmount, null);
                break;
        }
    }

    private static void ApplyCreatureAddedRelics(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature creature)
    {
        foreach (RelicModel relic in combat.Players
                     .SelectMany(combat.RelicsOf)
                     .Where(relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case PhilosophersStone when creature.Side != relic.Owner.Creature.Side:
                    combat.Apply<StrengthPower>(creature, relic.DynamicVars["StrengthPower"].IntValue);
                    break;
                case FurCoat furCoat when creature.Side == CombatSide.Enemy:
                    if (combat.CurrentMapCoord is { } currentMapCoord
                        && furCoat.GetMarkedCoords()?.Contains(currentMapCoord) == true)
                        simulator.State.GetCreature(creature).CurrentHp = 1;
                    break;
            }
        }
    }
}
