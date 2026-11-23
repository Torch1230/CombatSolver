using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;

namespace CombatSolver.Beam.Policy;

internal readonly record struct HealingCertificateAssessment(
    bool IsCertified,
    int MaxPostCombatHeal,
    string Reason,
    string? BlockingSourceId = null);

internal static class HealingCertificateAnalyzer
{
    internal static (bool CanCertify, int MaxPostCombatHeal) Analyze(CombatState root)
    {
        HealingCertificateAssessment assessment = Assess(root);
        return (assessment.IsCertified, assessment.MaxPostCombatHeal);
    }

    internal static HealingCertificateAssessment Assess(CombatState root)
    {
        if (root.Players.Count != 1)
            return new HealingCertificateAssessment(false, int.MaxValue, "multi_player");

        Player player = root.Players[0];
        if (player.Character is not (Ironclad or Necrobinder or Silent))
            return new HealingCertificateAssessment(false, int.MaxValue, "unsupported_character", player.Character.Id.Entry);

        IReadOnlyList<PotionModel?> potions = player.PotionSlots;
        for (int i = 0; i < potions.Count; i++)
        {
            if (potions[i] is { } potion)
                return new HealingCertificateAssessment(false, int.MaxValue, "potion_present", potion.Id.Entry);
        }

        int maxPostCombatHeal = 0;
        IReadOnlyList<RelicModel> relics = player.Relics;
        for (int i = 0; i < relics.Count; i++)
        {
            RelicModel relic = relics[i];
            if (relic.IsMelted)
                continue;
            if (relic is BurningBlood)
            {
                maxPostCombatHeal += 6;
                continue;
            }
            if (relic is BlackBlood)
            {
                maxPostCombatHeal += 12;
                continue;
            }
            if (relic is BoundPhylactery or RingOfTheSnake)
                continue;

            return new HealingCertificateAssessment(false, int.MaxValue, "unsupported_relic", relic.Id.Entry);
        }

        IReadOnlyList<PowerModel> playerPowers = player.Creature.Powers;
        for (int i = 0; i < playerPowers.Count; i++)
        {
            PowerModel power = playerPowers[i];
            if (power is not (NeurosurgePower or DoomPower))
                return new HealingCertificateAssessment(false, int.MaxValue, "unsupported_player_power", power.Id.Entry);
        }

        IReadOnlyList<Creature> allies = root.Allies;
        for (int i = 0; i < allies.Count; i++)
        {
            if (ContainsUnsafePower(allies[i].Powers))
                return new HealingCertificateAssessment(false, int.MaxValue, "unsupported_ally_power");
        }

        IReadOnlyList<Creature> enemies = root.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (ContainsUnsafePower(enemies[i].Powers))
                return new HealingCertificateAssessment(false, int.MaxValue, "unsupported_enemy_power");
        }

        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null)
            return new HealingCertificateAssessment(false, int.MaxValue, "missing_combat_state");

        if (!AssessPile(pcs.Hand.Cards, out HealingCertificateAssessment failed)
            || !AssessPile(pcs.DrawPile.Cards, out failed)
            || !AssessPile(pcs.DiscardPile.Cards, out failed)
            || !AssessPile(pcs.ExhaustPile.Cards, out failed)
            || !AssessPile(pcs.PlayPile.Cards, out failed))
        {
            return failed;
        }

        return new HealingCertificateAssessment(true, maxPostCombatHeal, "certified");

        static bool AssessPile(IReadOnlyList<CardModel> cards, out HealingCertificateAssessment failure)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                CardModel card = cards[i];
                if (card.Enchantments.Count != 0)
                {
                    failure = new HealingCertificateAssessment(false, int.MaxValue, "card_enchantment", card.Id.Entry);
                    return false;
                }
                if (card.Affliction is not null)
                {
                    failure = new HealingCertificateAssessment(false, int.MaxValue, "card_affliction", card.Id.Entry);
                    return false;
                }
                if (!IsSafeCard(card))
                {
                    failure = new HealingCertificateAssessment(false, int.MaxValue, "unsupported_card", card.Id.Entry);
                    return false;
                }
            }
            failure = default;
            return true;
        }
    }

    internal static bool HasVisibleHealingSource(CombatState root)
    {
        Player player = root.Players[0];
        IReadOnlyList<PowerModel> powers = player.Creature.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is RegenPower)
                return true;
        }

        IReadOnlyList<PotionModel?> potions = player.PotionSlots;
        for (int i = 0; i < potions.Count; i++)
        {
            if (potions[i] is BloodPotion or Ambergris or RegenPotion or FruitJuice or FairyInABottle)
                return true;
        }

        IReadOnlyList<RelicModel> relics = player.Relics;
        for (int i = 0; i < relics.Count; i++)
        {
            RelicModel relic = relics[i];
            if (relic.IsMelted)
                continue;
            if (relic is DemonTongue or BookRepairKnife or BookOfFiveRings or BathTowel
                or BathRobes or ChosenCheese or DragonFruit or DarkstonePeriapt
                or BloodVial or FakeBloodVial or EternalFeather or Pantograph
                or MealTicket or Planisphere or FakeLeesWaffle or LeesWaffle
                || (relic is LizardTail tail && !tail.IsUsedUp))
            {
                return true;
            }
        }

        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is not null)
        {
            if (ContainsVisibleHealingCard(pcs.Hand.Cards)
                || ContainsVisibleHealingCard(pcs.DrawPile.Cards)
                || ContainsVisibleHealingCard(pcs.DiscardPile.Cards)
                || ContainsVisibleHealingCard(pcs.ExhaustPile.Cards)
                || ContainsVisibleHealingCard(pcs.PlayPile.Cards))
            {
                return true;
            }
        }

        return false;

        static bool ContainsVisibleHealingCard(IReadOnlyList<CardModel> cards)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] is Feed or NotYet or Spur)
                    return true;
            }
            return false;
        }
    }

    internal static bool ContainsUnsafePower(IReadOnlyList<PowerModel> powers)
    {
        for (int i = 0; i < powers.Count; i++)
        {
            PowerModel power = powers[i];
            if (power is RegenPower or ReattachPower or IllusionPower
                or CreativeAiPower or HelloWorldPower or CalamityPower
                or CallOfTheVoidPower or SpectrumShiftPower)
            {
                return true;
            }
            if (power is SwipePower swipe && swipe.StolenCard is { } stolen
                && (stolen.Enchantments.Count != 0 || stolen.Affliction is not null || !IsSafeCard(stolen)))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ContainsPresentPotion(Player player)
    {
        IReadOnlyList<PotionModel?> potions = player.PotionSlots;
        for (int i = 0; i < potions.Count; i++)
        {
            if (potions[i] is not null)
                return true;
        }
        return false;
    }

    internal static bool IsSafeCard(CardModel card)
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
            || type == typeof(Neurosurge)
            || type == typeof(AscendersBane);
    }
}
