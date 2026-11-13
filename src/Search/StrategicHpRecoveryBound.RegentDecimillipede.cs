using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    private static bool IsNativeRegentDecimillipedeEnvironment(SimulatedCombatState combat, Player player)
        => player.Character.GetType() == typeof(Regent) && combat.Players.Count == 1
            && combat.KnownEnemies.Count > 0
            && combat.KnownEnemies.All(enemy => enemy.Monster?.GetType() == typeof(DecimillipedeSegmentFront)
                || enemy.Monster?.GetType() == typeof(DecimillipedeSegmentMiddle)
                || enemy.Monster?.GetType() == typeof(DecimillipedeSegmentBack));

    internal static bool CanCertifyRegentDecimillipedeHealingEnvironment(
        CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return IsNativeRegentDecimillipedeEnvironment(combat, player)
            && combat.Modifiers.Count == 0 && combat.RootRunModSubscriberCount == 0
            && combat.RootCombatModSubscriberCount == 0 && !combat.RootHasBaseLibCardModifiers
            && combat.AdaptedOnPlay is null
            && simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Count == 0
            && simulator.State.GetPlayerCombatState(player).AllCards.All(card =>
                HasCertifiedRemainingAttachments(card.Preview)
                && (RemainingSafeCards.Contains(card.Preview.GetType())
                    || card.Preview.GetType() == typeof(BundleOfJoy)
                    || card.Preview.GetType() == typeof(Entropy)))
            && combat.RelicsOf(player).All(IsRegentDecimillipedeRelic)
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<RegentCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<ColorlessCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<IroncladCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<SilentCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<NecrobinderCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<DefectCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<FoolCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<StatusCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<CurseCardPool>())
            // Reject an unknown initial source permanently, even if it can later vanish.
            && RegentDecimillipedeHealingUpperBound(simulator, player, 0) != int.MaxValue;
    }

    private static bool IsRegentDecimillipedeRelic(RelicModel relic)
        => relic.GetType() == typeof(DivineRight) || relic.GetType() == typeof(Girya)
            || relic.GetType() == typeof(OldCoin) || relic.GetType() == typeof(VitruvianMinion);

    internal static int RegentDecimillipedeHealingUpperBound(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal,
        bool includePotionHealing = true, int? maximumExplicitPotionUses = null)
    {
        if (simulator.HasPendingChoice)
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        // PotionUses is the branch-owned log used by normal manual replay. In this
        // single-player closure it counts the same explicit uses as the solver cap;
        // automatic consumption does not spend that cap. Do not remove active Regen.
        if (maximumExplicitPotionUses is { } maximum
            && combat.PotionUses.Count(static use => !use.Automatic) >= maximum)
            includePotionHealing = false;
        var state = simulator.State.GetPlayerCombatState(player);
        bool SafeCard(PredictedCard card)
            => HasCertifiedRemainingAttachments(card.Preview)
                && !GrowthValues.HasTarget(card.Preview)
                && (RemainingSafeCards.Contains(card.Preview.GetType())
                    || NativeNonHealingGeneratedCards.Contains(card.Preview.GetType()));
        if (!combat.RelicsOf(player).All(IsRegentDecimillipedeRelic)
            || !state.AllCards.All(SafeCard) || !combat.PendingReturningCards.All(SafeCard)
            || combat.Allies.Any(ally => ally.Player is null && ally.Monster?.GetType() != typeof(Osty))
            || state.OrbQueue.Orbs.Any(orb => orb.GetType() != typeof(LightningOrb)
                && orb.GetType() != typeof(FrostOrb) && orb.GetType() != typeof(DarkOrb)
                && orb.GetType() != typeof(PlasmaOrb) && orb.GetType() != typeof(GlassOrb)))
            return int.MaxValue;
        long regen = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            Type type = power.GetType();
            if (type == typeof(RegenPower))
            {
                if (ReferenceEquals(power.Owner, player.Creature))
                    regen += Math.Max(0, power.Amount);
            }
            else if (type == typeof(ReattachPower))
            {
                // Native Decimillipede revival restores only an enemy segment.
                if (power.Owner.Player is not null || !combat.KnownEnemies.Contains(power.Owner))
                    return int.MaxValue;
            }
            else if (!RemainingSafePowers.Contains(type)
                && !NativeNonHealingGeneratedPowers.Contains(type))
                return int.MaxValue;
        }
        int slots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        for (int slot = 0; slot < slots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion is null || !combat.IsPotionAvailable(player, slot))
                continue;
            if (potion.GetType() == typeof(RegenPotion))
            {
                // Omit this dose only when explicit use is forbidden or the branch
                // has spent its allowance. Existing RegenPower continues to heal.
                if (includePotionHealing)
                    regen += Math.Max(0, potion.DynamicVars["RegenPower"].IntValue);
            }
            else if (potion.GetType() != typeof(HeartOfIron))
                return int.MaxValue;
        }
        return RegenerationHealingUpperBound(regen, postCombatHeal);
    }
}
