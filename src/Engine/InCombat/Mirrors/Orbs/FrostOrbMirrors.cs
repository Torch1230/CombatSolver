using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Orbs;

internal static class FrostOrbMirrors
{
    // Mirrors FrostOrb.BeforeTurnEndOrbTrigger by forwarding through OrbModel.TriggerPassive.
    public static void BeforeTurnEndOrbTrigger(FrostOrb orb, OrbMirrorContext context)
    {
        context.Simulator.TriggerOrbPassive(orb, target: null, context.ProcessedEnemyDeaths);
    }

    // Mirrors FrostOrb.Passive without VFX/SFX or waits.
    public static void Passive(FrostOrb orb, OrbPassiveMirrorContext context)
    {
        Block(orb, context, OrbMirrors.ModifyValue(context.Simulator, orb, 2m));
    }

    // Mirrors FrostOrb.Evoke without VFX/SFX or waits.
    public static IReadOnlyList<Creature> Evoke(FrostOrb orb, OrbMirrorContext context)
    {
        return Block(orb, context, OrbMirrors.ModifyValue(context.Simulator, orb, 5m));
    }

    private static IReadOnlyList<Creature> Block(FrostOrb orb, OrbMirrorContext context, decimal value)
    {
        List<Creature> completedTargets = [orb.Owner.Creature];
        context.Simulator.GainBlock(orb.Owner.Creature, value, ValueProp.Unpowered);
        if (context.Simulator.HasPendingChoice)
            return completedTargets;

        if (!orb.Owner.Creature.HasPower<HibernatePower>())
        {
            return completedTargets;
        }

        // StS2 v0.108.0 grants the owner block first, then the same block to all other players.
        var allPlayers = context.State.CombatState.Players;
        foreach (var player in allPlayers)
        {
            if (player != orb.Owner)
            {
                context.Simulator.GainBlock(player.Creature, value, ValueProp.Unpowered);
                completedTargets.Add(player.Creature);
                if (context.Simulator.HasPendingChoice)
                    return completedTargets;
            }
        }

        return completedTargets;
    }
}
