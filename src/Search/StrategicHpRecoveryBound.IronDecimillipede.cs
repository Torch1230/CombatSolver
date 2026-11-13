using System.Collections.Frozen;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    // Reviewed initial Ironclad cards. Generated cards can retrieve Exhaust, so
    // unknown initial cards are rejected permanently even in that pile. The
    // broader generated closure is certified separately at the root.
    private static readonly FrozenSet<Type> IronDecimillipedeSafeCards = new Type[]
    {
        typeof(StrikeIronclad), typeof(DefendIronclad), typeof(Bash), typeof(Headbutt),
        typeof(Bully), typeof(StoneArmor), typeof(Taunt), typeof(BurningPact),
        typeof(Mangle), typeof(Barricade), typeof(PerfectedStrike), typeof(TwinStrike),
        typeof(Rampage), typeof(Thunderclap), typeof(Anger), typeof(DramaticEntrance),
        typeof(HowlFromBeyond), typeof(Offering), typeof(Rage), typeof(Bloodletting),
        typeof(BattleTrance), typeof(Brand), typeof(PactsEnd), typeof(PrimalForce),
        typeof(Spite), typeof(GiantRock),
    }.ToFrozenSet();

    private static bool IsIronDecimillipedeSafeCard(Type type)
        => IronDecimillipedeSafeCards.Contains(type) || RemainingSafeCards.Contains(type);

    private static bool IsIronDecimillipedeGenerator(Type type)
        => type == typeof(InfernalBlade) || type == typeof(JackOfAllTrades)
            || type == typeof(Jackpot);

    private static bool IsIronDecimillipedeRelic(RelicModel relic)
        => relic.GetType() == typeof(BurningBlood) || relic.GetType() == typeof(IceCream)
            || relic.GetType() == typeof(TungstenRod) || relic.GetType() == typeof(Kunai);

    private static bool IsNativeIronDecimillipedeEnvironment(SimulatedCombatState combat, Player player)
        => player.Character.GetType() == typeof(Ironclad) && combat.Players.Count == 1
            && combat.KnownEnemies.Count > 0
            && combat.KnownEnemies.All(enemy => enemy.Monster?.GetType() == typeof(DecimillipedeSegmentFront)
                || enemy.Monster?.GetType() == typeof(DecimillipedeSegmentMiddle)
                || enemy.Monster?.GetType() == typeof(DecimillipedeSegmentBack));

    internal static bool CanCertifyIronDecimillipedeHealingEnvironment(
        CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return IsNativeIronDecimillipedeEnvironment(combat, player)
            && combat.Modifiers.Count == 0 && combat.RootRunModSubscriberCount == 0
            && combat.RootCombatModSubscriberCount == 0 && !combat.RootHasBaseLibCardModifiers
            && combat.AdaptedOnPlay is null
            && simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Count == 0
            && combat.RelicsOf(player).All(IsIronDecimillipedeRelic)
            && simulator.State.GetPlayerCombatState(player).AllCards.All(card =>
                HasCertifiedRemainingAttachments(card.Preview)
                && (IsIronDecimillipedeSafeCard(card.Preview.GetType())
                    || IsIronDecimillipedeGenerator(card.Preview.GetType())))
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<RegentCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<ColorlessCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<IroncladCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<SilentCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<NecrobinderCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<DefectCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<FoolCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<StatusCardPool>())
            && HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<CurseCardPool>())
            && IronDecimillipedeHealingUpperBound(simulator, player, 0) != int.MaxValue;
    }

    internal static int IronDecimillipedeHealingUpperBound(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal)
    {
        if (simulator.HasPendingChoice)
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (!combat.RelicsOf(player).All(IsIronDecimillipedeRelic))
            return int.MaxValue;
        bool SafeRemainingCard(PredictedCard card)
            => HasCertifiedRemainingAttachments(card.Preview)
                && !GrowthValues.HasTarget(card.Preview)
                && (IsIronDecimillipedeSafeCard(card.Preview.GetType())
                    || NativeNonHealingGeneratedCards.Contains(card.Preview.GetType()));
        var state = simulator.State.GetPlayerCombatState(player);
        if (!state.AllCards.All(SafeRemainingCard)
            || !combat.PendingReturningCards.All(SafeRemainingCard)
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
                // The reviewed revival heals only an owning native segment.
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
            if (potion.GetType() != typeof(BeetleJuice) && potion.GetType() != typeof(GamblersBrew))
                return int.MaxValue;
        }
        return RegenerationHealingUpperBound(regen, postCombatHeal);
    }
}
