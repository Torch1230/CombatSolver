using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// A deliberately small closed set of roots whose future actions cannot restore player HP.
/// Unclassified cards, relics, powers, potions and gameplay extensions retain the full HP headroom.
/// The supported native enemy effects only add harmful status cards; revisit this proof if an enemy
/// starts granting a healing card, potion, relic or player power.
/// </summary>
internal static class StrategicHpRecoveryBound
{
    internal static bool HasOnlyPostCombatHealing(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (player.Character is not (Ironclad or Necrobinder)
            || combat.KnownEnemies.Any(enemy => enemy.Monster?.GetType().Assembly != typeof(Ironclad).Assembly)
            || combat.Modifiers.Count != 0
            || combat.RootRunModSubscriberCount != 0
            || combat.RootCombatModSubscriberCount != 0
            || combat.RootHasBaseLibCardModifiers
            || combat.AdaptedOnPlay is not null
            || player.PotionSlots.Any(static potion => potion is not null)
            || combat.RelicsOf(player).Any(static relic => !relic.IsMelted && !IsSafeRelic(relic))
            || combat.EffectivePowers().Any(power => ReferenceEquals(power.Owner, player.Creature)
                && !IsSafePower(power)))
        {
            return false;
        }

        return simulator.State.GetPlayerCombatState(player).AllCards.All(card =>
            card.Preview.Enchantment is null
            && card.Preview.Affliction is null
            && IsSafeCard(card.Preview));
    }

    internal static int OptimisticRecoveredHp(
        int recoveredHp, int playerHp, int playerMaxHp, int futureHealPotential)
        => recoveredHp + Math.Min(
            Math.Max(0, playerMaxHp - playerHp), futureHealPotential);

    private static bool IsSafeRelic(MegaCrit.Sts2.Core.Models.RelicModel relic)
        => relic.GetType() == typeof(BurningBlood)
            || relic.GetType() == typeof(BlackBlood)
            || relic.GetType() == typeof(BoundPhylactery);

    private static bool IsSafePower(MegaCrit.Sts2.Core.Models.PowerModel power)
        => power.GetType() == typeof(NeurosurgePower)
            || power.GetType() == typeof(DoomPower);

    private static bool IsSafeCard(MegaCrit.Sts2.Core.Models.CardModel card)
    {
        Type type = card.GetType();
        return type == typeof(StrikeIronclad)
            || type == typeof(StrikeNecrobinder)
            || type == typeof(DefendIronclad)
            || type == typeof(DefendNecrobinder)
            || type == typeof(Bash)
            || type == typeof(Bodyguard)
            || type == typeof(Unleash)
            || type == typeof(Bloodletting)
            || type == typeof(Neurosurge);
    }
}
