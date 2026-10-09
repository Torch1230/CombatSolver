using System.Collections.Frozen;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    // These actions neither restore HP nor retrieve another card from Exhaust. Their only
    // fixed generated card types are Debris, Minion Strike, Shiv and Sovereign Blade;
    // Blade of Ink only adds Inky.
    // This is a closed semantic certificate, not a test for visible healing variables.
    private static readonly FrozenSet<Type> RemainingSafeCards = new Type[]
    {
        typeof(StrikeRegent), typeof(DefendRegent), typeof(FallingStar), typeof(Venerate),
        typeof(AscendersBane), typeof(Hegemony), typeof(SolarStrike), typeof(IAmInvincible),
        typeof(NeutronAegis), typeof(VoidForm), typeof(CrashLanding), typeof(Glow),
        typeof(Begone), typeof(SevenStars), typeof(MakeItSo), typeof(ThinkingAhead),
        typeof(Debris), typeof(SovereignBlade), typeof(MinionStrike),
        typeof(StrikeSilent), typeof(DefendSilent), typeof(Neutralize), typeof(Survivor),
        typeof(Adrenaline), typeof(Deflect), typeof(PreciseCut), typeof(SerpentForm),
        typeof(Accelerant), typeof(Expertise), typeof(Abrasive), typeof(BulletTime),
        typeof(Speedster), typeof(Prepared), typeof(DaggerThrow), typeof(EscapePlan),
        typeof(Mayhem), typeof(ThrummingHatchet), typeof(Dazed), typeof(Wound), typeof(Burn),
        typeof(Backflip), typeof(Outbreak), typeof(Footwork), typeof(BladeOfInk),
        typeof(Snakebite), typeof(PiercingWail), typeof(CalculatedGamble), typeof(CloakAndDagger),
        typeof(Malaise), typeof(Suppress), typeof(BubbleBubble), typeof(Haze), typeof(DeadlyPoison),
        typeof(DodgeAndRoll), typeof(Mirage), typeof(ToolsOfTheTrade), typeof(UltimateDefend), typeof(Shiv),
    }.ToFrozenSet();

    private static readonly FrozenSet<Type> RemainingSafePowers = new Type[]
    {
        typeof(StrengthPower), typeof(DexterityPower), typeof(WeakPower), typeof(VulnerablePower),
        typeof(FrailPower), typeof(ArtifactPower), typeof(PlatingPower),
        typeof(VoidFormPower), typeof(EnergyNextTurnPower), typeof(DrawCardsNextTurnPower),
        typeof(RetainHandPower), typeof(VigorPower), typeof(NoDrawPower),
        typeof(SerpentFormPower), typeof(AccelerantPower), typeof(SpeedsterPower),
        typeof(ThornsPower), typeof(PoisonPower), typeof(MayhemPower),
        typeof(VitalSparkPower), typeof(TaintedPower),
        typeof(BlockNextTurnPower), typeof(PiercingWailPower), typeof(ToolsOfTheTradePower),
    }.ToFrozenSet();

    // Keep the additional native closure local to Regent / Louse Progenitor. Other
    // certified encounters retain their original accepted card and power sets.
    private static class NativeLouseClosure
    {
        internal static readonly FrozenSet<Type> Cards = new Type[]
        {
            typeof(SeekingEdge), typeof(Glimmer), typeof(Arsenal), typeof(Comet),
            typeof(RefineBlade), typeof(GuidingStar), typeof(SecretWeapon),
        }.ToFrozenSet();

        internal static readonly FrozenSet<Type> Powers = new Type[]
        {
            typeof(SeekingEdgePower), typeof(ArsenalPower), typeof(CurlUpPower),
            typeof(RitualPower), typeof(DemisePower),
        }.ToFrozenSet();
    }

    private static bool IsNativeRegentLouseEnvironment(SimulatedCombatState combat, Player player)
        => player.Character.GetType() == typeof(Regent)
            && combat.KnownEnemies.Count > 0
            && combat.KnownEnemies.All(enemy => enemy.Monster?.GetType() == typeof(LouseProgenitor));

    private static bool IsRemainingSafeCard(Type type, bool nativeLouse)
        => RemainingSafeCards.Contains(type) || nativeLouse && NativeLouseClosure.Cards.Contains(type);

    // Native Vital Spark only attaches Tainted to skills and applies TaintedPower.
    // Tainted has no OnPlay callback; TaintedPower only adds powered attack damage
    // and removes itself after the enemy turn. Neither can restore player HP.
    // Slither only randomizes this card's energy cost on draw. Inky only applies Weak.
    // Neither generates cards or restores HP; other attachments stay unknown.
    private static bool HasCertifiedRemainingAttachments(CardModel card)
        => (card.Enchantment is null || card.Enchantment.GetType() == typeof(Slither)
                || card.Enchantment.GetType() == typeof(Inky)
                || card.Enchantment.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Enchantments.Instinct))
            && (card.Affliction is null || card.Affliction.GetType() == typeof(Tainted));

    internal static bool CanCertifyRemainingHealingEnvironment(
        CombatPredictionSimulator simulator, Player player)
    {
        if (((SimulatedCombatState)simulator.State.CombatState).RootHealingCallbackRejection is not null)
            return false;
        if (player.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Defect))
            return CanCertifyDefectPrismHealingEnvironment(simulator, player);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (IsNativeIronDecimillipedeEnvironment(combat, player))
            return CanCertifyIronDecimillipedeHealingEnvironment(simulator, player);
        if (IsNativeRegentDecimillipedeEnvironment(combat, player))
            return CanCertifyRegentDecimillipedeHealingEnvironment(simulator, player);
        bool nativeLouse = IsNativeRegentLouseEnvironment(combat, player);
        bool copiesExhaustedSkills = combat.RelicsOf(player)
            .Any(relic => relic.GetType() == typeof(BurningSticks));
        return (player.Character.GetType() == typeof(Regent)
                || player.Character.GetType() == typeof(Silent))
            && combat.Players.Count == 1
            // Reviewed native move closures: attacks/block/Vital Spark for Prism;
            // attacks/Strength for Crawler; attacks/Weak/Vulnerable for Soul Nexus;
            // attacks/Frail/block/Strength/Curl Up for native Louse Progenitor.
            // Assembly membership alone does not prove
            // that an arbitrary native enemy cannot grant player healing later.
            && (nativeLouse || combat.KnownEnemies.All(enemy => enemy.Monster?.GetType() == typeof(InfestedPrism)
                || enemy.Monster?.GetType() == typeof(FuzzyWurmCrawler)
                || enemy.Monster?.GetType() == typeof(SoulNexus)))
            && combat.Modifiers.Count == 0
            && combat.RootRunModSubscriberCount == 0
            && combat.RootCombatModSubscriberCount == 0
            && !combat.RootHasBaseLibCardModifiers
            && combat.AdaptedOnPlay is null
            && simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Count == 0
            && simulator.State.GetPlayerCombatState(player).AllCards.All(card =>
                HasCertifiedRemainingAttachments(card.Preview)
                && (IsRemainingSafeCard(card.Preview.GetType(), nativeLouse)
                    || !copiesExhaustedSkills && (card.Preview.GetType() == typeof(BundleOfJoy)
                        || card.Preview.GetType() == typeof(Entropy))))
            && combat.RelicsOf(player).All(relic => relic.GetType() == typeof(DivineRight)
                || relic.GetType() == typeof(Girya) || relic.GetType() == typeof(OldCoin)
                || relic.GetType() == typeof(VitruvianMinion)
                || relic.GetType() == typeof(RingOfTheSnake) || relic.GetType() == typeof(PotionBelt)
                || relic.GetType() == typeof(Shovel) || relic.GetType() == typeof(IceCream)
                // Cauldron only grants rewards on pickup, which the closed card set
                // cannot trigger. Burning Sticks copies an existing skill once; every
                // accepted skill remains in the same closed set. Kusarigama only damages.
                || nativeLouse && (relic.GetType() == typeof(Cauldron)
                    || relic.GetType() == typeof(BurningSticks) || relic.GetType() == typeof(Kusarigama)));
    }

    internal static int RemainingHealingUpperBound(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal,
        bool includePotionHealing = true, int? maximumExplicitPotionUses = null)
    {
        if (((SimulatedCombatState)simulator.State.CombatState).RootHealingCallbackRejection is not null)
            return int.MaxValue;
        if (player.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Defect))
            return DefectPrismHealingUpperBound(simulator, player, postCombatHeal);
        if (simulator.HasPendingChoice)
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (IsNativeIronDecimillipedeEnvironment(combat, player))
            return IronDecimillipedeHealingUpperBound(simulator, player, postCombatHeal);
        if (IsNativeRegentDecimillipedeEnvironment(combat, player))
            return RegentDecimillipedeHealingUpperBound(simulator, player, postCombatHeal,
                includePotionHealing, maximumExplicitPotionUses);
        bool nativeLouse = IsNativeRegentLouseEnvironment(combat, player);
        if (combat.PendingReturningCards.Any(card => !IsRemainingSafeCard(card.Preview.GetType(), nativeLouse)
                || !HasCertifiedRemainingAttachments(card.Preview)))
            return int.MaxValue;
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
        {
            CardModel preview = card.Preview;
            if (!HasCertifiedRemainingAttachments(preview))
                return int.MaxValue;
            // Bundle of Joy has no passive callbacks. Once in Exhaust it cannot be
            // replayed by the active closed set above. Burning Sticks deliberately rejects
            // this shortcut because it can copy an exhausted skill. Every unknown card,
            // including an unknown exhausted card with passive callbacks, still rejects.
            if (preview.GetType() == typeof(BundleOfJoy)
                && card.OwnerPile?.Type == PileType.Exhaust
                && !combat.RelicsOf(player).Any(relic => relic.GetType() == typeof(BurningSticks)))
                continue;
            if (!IsRemainingSafeCard(preview.GetType(), nativeLouse))
                return int.MaxValue;
        }

        long regen = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (power.GetType() == typeof(RegenPower))
            {
                if (ReferenceEquals(power.Owner, player.Creature))
                    regen += Math.Max(0, power.Amount);
            }
            else if (power.GetType() == typeof(ReattachPower)
                && power.Owner.Player is null && combat.KnownEnemies.Contains(power.Owner))
            {
                // Its only heal targets the owning native enemy, never the player.
            }
            else if (!RemainingSafePowers.Contains(power.GetType())
                && !(nativeLouse && NativeLouseClosure.Powers.Contains(power.GetType())))
                return int.MaxValue;
        }
        int potionSlots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        for (int slot = 0; slot < potionSlots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion is null || !combat.IsPotionAvailable(player, slot))
                continue;
            if (potion.GetType() == typeof(RegenPotion))
                regen += Math.Max(0, potion.DynamicVars["RegenPower"].IntValue);
            else if (potion.GetType() != typeof(HeartOfIron)
                && potion.GetType() != typeof(StableSerum)
                && potion.GetType() != typeof(BlessingOfTheForge)
                && !(nativeLouse && (potion.GetType() == typeof(MazalethsGift)
                    || potion.GetType() == typeof(PowderedDemise))))
                return int.MaxValue;
        }
        return RegenerationHealingUpperBound(regen, postCombatHeal);
    }

    internal static int RegenerationHealingUpperBound(long regen, int postCombatHeal)
    {
        // Stacking all remaining doses before the first tick is an upper bound on every
        // later ordering. Regen's native callback heals the amount, then decrements it.
        // Saturate before multiplication; arbitrary large amounts remain conservative.
        if (regen >= 65_536)
            return int.MaxValue;
        long amount = Math.Max(0, regen);
        return (int)Math.Min(int.MaxValue,
            amount * (amount + 1) / 2 + Math.Max(0, postCombatHeal));
    }
}
