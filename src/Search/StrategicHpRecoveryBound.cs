using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct StrategicHpRecoveryBoundAssessment(
    bool IsCertified,
    string Reason,
    string? BlockingSourceId = null);

/// <summary>
/// A deliberately small closed set of roots whose future actions cannot restore player HP.
/// Unclassified cards, relics, powers, potions and gameplay extensions retain the full HP headroom.
/// The supported native enemy effects only add harmful status cards; revisit this proof if an enemy
/// starts granting a healing card, potion, relic or player power.
/// </summary>
internal static partial class StrategicHpRecoveryBound
{
    internal static bool HasOnlyPostCombatHealing(CombatPredictionSimulator simulator, Player player)
        => Assess(simulator, player).IsCertified;

    internal static StrategicHpRecoveryBoundAssessment Assess(
        CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.RootHealingCallbackRejection is { } callbackRejection)
            return new(false, "player-potion-callback", callbackRejection);
        if (player.Character is not (Ironclad or Necrobinder or Silent))
            return new(false, "unsupported_character", player.Character.Id.Entry);
        var nonNativeEnemy = combat.KnownEnemies.FirstOrDefault(
            enemy => enemy.Monster?.GetType().Assembly != typeof(Ironclad).Assembly);
        if (nonNativeEnemy is not null)
            return new(false, "non_native_enemy", nonNativeEnemy.Monster?.Id.Entry);
        if (combat.Modifiers.Count != 0)
            return new(false, "encounter_modifier", combat.Modifiers[0].Id.Entry);
        if (combat.RootRunModSubscriberCount != 0)
            return new(false, "run_mod_subscriber");
        if (combat.RootCombatModSubscriberCount != 0)
            return new(false, "combat_mod_subscriber");
        if (combat.RootHasBaseLibCardModifiers)
            return new(false, "base_lib_card_modifier");
        if (combat.AdaptedOnPlay is not null)
            return new(false, "adapted_on_play");
        var presentPotion = player.PotionSlots.FirstOrDefault(static potion => potion is not null);
        if (presentPotion is not null)
            return new(false, "potion_present", presentPotion.Id.Entry);
        var unsupportedRelic = combat.RelicsOf(player)
            .FirstOrDefault(static relic => !relic.IsMelted && !IsSafeRelic(relic));
        if (unsupportedRelic is not null)
            return new(false, "unsupported_relic", unsupportedRelic.Id.Entry);
        var unsupportedPower = combat.EffectivePowers().FirstOrDefault(power =>
            ReferenceEquals(power.Owner, player.Creature) && !IsSafePower(power));
        if (unsupportedPower is not null)
            return new(false, "unsupported_player_power", unsupportedPower.Id.Entry);

        foreach (var card in simulator.State.GetPlayerCombatState(player).AllCards)
        {
            if (card.Preview.Enchantment is not null)
                return new(false, "card_enchantment", card.Preview.Id.Entry);
            if (card.Preview.Affliction is not null)
                return new(false, "card_affliction", card.Preview.Id.Entry);
            if (!IsSafeCard(card.Preview))
                return new(false, "unsupported_card", card.Preview.Id.Entry);
        }

        return new(true, "certified");
    }

    internal static int OptimisticRecoveredHp(
        int recoveredHp, int playerHp, int playerMaxHp, int futureHealPotential)
        => recoveredHp + Math.Min(
            Math.Max(0, playerMaxHp - playerHp), futureHealPotential);

    private static bool IsSafeRelic(MegaCrit.Sts2.Core.Models.RelicModel relic)
        => relic.GetType() == typeof(BurningBlood)
            || relic.GetType() == typeof(BlackBlood)
            || relic.GetType() == typeof(BoundPhylactery)
            || relic.GetType() == typeof(RingOfTheSnake);

    private static bool IsSafePower(MegaCrit.Sts2.Core.Models.PowerModel power)
        => power.GetType() == typeof(NeurosurgePower)
            || power.GetType() == typeof(DoomPower);

    private static bool IsSafeCard(MegaCrit.Sts2.Core.Models.CardModel card)
    {
        Type type = card.GetType();
        return type == typeof(StrikeIronclad)
            || type == typeof(StrikeNecrobinder)
            || type == typeof(StrikeSilent)
            || type == typeof(DefendIronclad)
            || type == typeof(DefendNecrobinder)
            || type == typeof(DefendSilent)
            || type == typeof(Neutralize)
            || type == typeof(Survivor)
            || type == typeof(Bash)
            || type == typeof(Bodyguard)
            || type == typeof(Unleash)
            || type == typeof(Bloodletting)
            || type == typeof(Neurosurge);
    }
}
