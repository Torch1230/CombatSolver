using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    // All five native characters and every native encounter use the policy requested
    // in #135 follow-up: reserve healing from materialized
    // sources. Random potion generation supplies no speculative healing allowance.
    // This is a policy estimate, separate from the closed semantic certificates.
    internal static bool CanUseKnownNativeHealingPolicy(CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return combat.Players.Count == 1 && IsNative(player.Character)
            && combat.Modifiers.Count == 0 && combat.RootHasOnlyNonHealingLoadoutSubscribers
            && !combat.RootHasBaseLibCardModifiers && combat.AdaptedOnPlay is null
            && combat.KnownEnemies.All(enemy => enemy.Monster is { } monster && IsNative(monster))
            && combat.RelicsOf(player).All(IsNative)
            && combat.EffectivePowers().All(IsNative)
            && simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.All(IsNative)
            && simulator.State.GetPlayerCombatState(player).AllCards.All(HasNativeHealingPolicyCard)
            && combat.PendingReturningCards.All(HasNativeHealingPolicyCard)
            && HasNativePotionSlots(combat, player);
    }

    private static bool HasNativePotionSlots(SimulatedCombatState combat, Player player)
    {
        int slots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        for (int slot = 0; slot < slots; slot++)
            if (combat.GetPotionAtSlot(player, slot) is { } potion && !IsNative(potion))
                return false;
        return true;
    }

    private static bool IsNative(AbstractModel model)
        => model.GetType().Assembly == typeof(CardModel).Assembly;

    private static bool HasNativeHealingPolicyCard(PredictedCard card)
        => IsNative(card.Preview)
            && (card.Preview.Enchantment is null || IsNative(card.Preview.Enchantment))
            && (card.Preview.Affliction is null || IsNative(card.Preview.Affliction));

    internal static int KnownNativeHealingPotential(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal,
        bool includePotionHealing = true, int? maximumExplicitPotionUses = null)
    {
        if (simulator.HasPendingChoice || !CanUseKnownNativeHealingPolicy(simulator, player))
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        var state = simulator.State.GetPlayerCombatState(player);
        // Not Yet can be copied or recovered; native Feed belongs to growth policy.
        // Recurring relic heals and death saves retain the complete HP allowance.
        // Max-HP gains also heal: permanent-deck curse additions and combat-end
        // callbacks must stay conservative until their complete source chains are closed.
        if (state.AllCards.Any(card => card.Preview is NotYet or Feed)
            || combat.PendingReturningCards.Any(card => card.Preview is NotYet or Feed)
            || combat.RelicsOf(player).Any(relic => !relic.IsMelted
                && relic is DemonTongue or BookOfFiveRings or BookRepairKnife or LizardTail
                    or DragonFruit or DarkstonePeriapt or ChosenCheese))
            return int.MaxValue;

        long regen = combat.EffectivePowers().OfType<RegenPower>()
            .Where(power => ReferenceEquals(power.Owner, player.Creature))
            .Sum(power => (long)Math.Max(0, power.Amount));
        long directHeal = 0;
        bool canUsePotion = includePotionHealing
            && (maximumExplicitPotionUses is not { } maximum
                || combat.PotionUses.Count(use => !use.Automatic) < maximum);
        int slots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        for (int slot = 0; slot < slots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion is null || !combat.IsPotionAvailable(player, slot))
                continue;
            if (!IsNative(potion) || potion is FairyInABottle)
                return int.MaxValue;
            if (!canUsePotion)
                continue;
            switch (potion)
            {
                case RegenPotion:
                    regen += Math.Max(0, potion.DynamicVars["RegenPower"].IntValue);
                    break;
                case BloodPotion or Ambergris:
                    directHeal += (long)Math.Ceiling(simulator.State.GetCreature(player.Creature).MaxHp
                        * potion.DynamicVars["HealPercent"].BaseValue / 100m);
                    break;
                case FruitJuice:
                    return int.MaxValue;
            }
        }
        return (int)Math.Min(int.MaxValue,
            directHeal + RegenerationHealingUpperBound(regen, postCombatHeal));
    }
}
