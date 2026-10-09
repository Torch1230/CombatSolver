using System.Collections.Frozen;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Singleton;
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
    // Component certificate pinned to the audited installed native assembly. Every
    // component below has an explicit review; assembly membership is never sufficient.
    private static readonly Guid ComponentAuditMvid = new("8a76776c-0ce1-4d4f-90bd-8cce653dad8e");
    private static readonly FrozenSet<Type> ComponentInitialCards = new Type[]
    {
        typeof(Aggression), typeof(Hemokinesis), typeof(TearAsunder), typeof(Rupture),
        typeof(Fisticuffs), typeof(TheGambit), typeof(DarkEmbrace), typeof(Stoke),
        typeof(InfernalBlade), typeof(JackOfAllTrades), typeof(Jackpot),
        typeof(Acrobatics), typeof(CorrosiveWave), typeof(FlickFlack),
        typeof(GrandFinale), typeof(Production), typeof(Ricochet),
        // Reuse the successful native eight-card mechanism contract on this DLL.
        typeof(Backstab), typeof(FanOfKnives), typeof(Flechettes), typeof(MasterPlanner),
        typeof(NoxiousFumes), typeof(Prowess), typeof(Reflex), typeof(RollingBoulder),
    }.ToFrozenSet();

    private static readonly FrozenSet<Type> ComponentEnemies = new Type[]
    {
        typeof(Nibbit), typeof(InfestedPrism), typeof(FuzzyWurmCrawler), typeof(SoulNexus),
        typeof(LouseProgenitor), typeof(DecimillipedeSegmentFront),
        typeof(DecimillipedeSegmentMiddle), typeof(DecimillipedeSegmentBack),
    }.ToFrozenSet();

    private static readonly FrozenSet<Type> ComponentRelics = new Type[]
    {
        typeof(BurningBlood), typeof(EternalFeather), typeof(Cauldron), typeof(Brimstone),
        typeof(IceCream), typeof(TungstenRod), typeof(Kunai),
        typeof(DivineRight), typeof(Girya), typeof(OldCoin), typeof(VitruvianMinion),
        typeof(RingOfTheSnake), typeof(PotionBelt), typeof(Shovel),
        typeof(Kaleidoscope), typeof(GhostSeed), typeof(Vajra), typeof(RippleBasin),
        typeof(Whetstone), typeof(VeryHotCocoa), typeof(TuningFork), typeof(FestivePopper),
        typeof(StrikeDummy), typeof(FakeHappyFlower), typeof(SneckoSkull),
        typeof(TriBoomerang), typeof(OrnamentalFan), typeof(Permafrost),
    }.ToFrozenSet();

    // Reuse the pinned initial-source / native lifecycle proofs. These sources
    // expand only pruning proof consumption. The original schedule flags remain frozen separately.
    private static readonly FrozenSet<Type> ReviewedComponentCards = new Type[]
    {
        typeof(DrainPower), typeof(HandOfGreed), typeof(NegativePulse), typeof(Pagestorm), typeof(Parse), typeof(Putrefy), typeof(SoulStorm), typeof(SpiritOfAsh), typeof(Squeeze), typeof(Entropy), typeof(BoostAway), typeof(BulkUp), typeof(Claw), typeof(Compact), typeof(Darkness), typeof(Dualcast), typeof(Modded), typeof(Null), typeof(Overclock), typeof(Refract), typeof(Scrape), typeof(StrikeDefect), typeof(Voltaic), typeof(Zap), typeof(Comet), typeof(Glimmer), typeof(GuidingStar), typeof(RefineBlade), typeof(SecretWeapon), typeof(SeekingEdge), typeof(TheHunt),
        typeof(Veilpiercer), typeof(BundleOfJoy), typeof(DefendDefect), typeof(Arsenal),
        typeof(PrepTime), typeof(StrikeRegent), typeof(Calamity), typeof(CrescentSpear),
        typeof(Furnace), typeof(GammaBlast), typeof(DefendRegent), typeof(Orbit),
        typeof(LunarBlast), typeof(SevenStars), typeof(GatherLight), typeof(Alignment),
        typeof(WroughtInWar), typeof(CollisionCourse), typeof(Bombardment), typeof(Venerate),
        typeof(FallingStar), typeof(Quasar), typeof(FranticEscape),
        typeof(Bodyguard), typeof(DefendNecrobinder), typeof(Defy), typeof(Delay),
        typeof(DevourLife), typeof(Dirge), typeof(Dredge), typeof(NecroMastery),
        typeof(NoEscape), typeof(Severance), typeof(SharedFate), typeof(SicEm),
        typeof(StrikeNecrobinder), typeof(Transfigure), typeof(Unleash),
    }.ToFrozenSet();

    private static readonly FrozenSet<Type> ReviewedComponentRelics = new Type[]
    {
        typeof(SparklingRouge), typeof(RainbowRing), typeof(CrackedCore), typeof(BurningSticks),
        typeof(Kusarigama), typeof(GnarledHammer), typeof(HornCleat), typeof(Bread), typeof(CaptainsWheel),
        typeof(MembershipCard), typeof(MercuryHourglass), typeof(VexingPuzzlebox),
        typeof(BoundPhylactery), typeof(LetterOpener), typeof(BowlerHat), typeof(WhiteStar),
    }.ToFrozenSet();

    private static bool ComponentEnemy(Type type, bool useReviewedSources)
        => ComponentEnemies.Contains(type) || useReviewedSources && type == typeof(TheInsatiable);

    private static bool ComponentRelic(Type type, bool useReviewedSources)
        => ComponentRelics.Contains(type) || useReviewedSources && ReviewedComponentRelics.Contains(type);

    private static bool ComponentAttachment(AbstractModel source, bool useReviewedSources)
        => source.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Enchantments.Instinct)
            || useReviewedSources && (source.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Enchantments.Slither)
                || source.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Enchantments.Inky)
                || source.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Afflictions.Tainted));

    private static bool ComponentInitialCard(CardModel card, bool useReviewedSources = false)
        => (ComponentInitialCards.Contains(card.GetType())
                || IronDecimillipedeSafeCards.Contains(card.GetType())
                || RemainingSafeCards.Contains(card.GetType())
                || useReviewedSources && ReviewedComponentCards.Contains(card.GetType()))
            && HasCertifiedRemainingAttachments(card) && (!GrowthValues.HasTarget(card)
                || useReviewedSources && card.GetType() is var growthType
                    && (growthType == typeof(HandOfGreed) || growthType == typeof(TheHunt)));

    private static bool ComponentRemainingCard(CardModel card, bool useReviewedSources = false)
        => (ComponentInitialCards.Contains(card.GetType())
                || IronDecimillipedeSafeCards.Contains(card.GetType())
                || RemainingSafeCards.Contains(card.GetType())
                || NativeNonHealingGeneratedCards.Contains(card.GetType())
                || useReviewedSources && ReviewedComponentCards.Contains(card.GetType()))
            && HasCertifiedRemainingAttachments(card) && (!GrowthValues.HasTarget(card)
                || useReviewedSources && card.GetType() is var growthType
                    && (growthType == typeof(HandOfGreed) || growthType == typeof(TheHunt)));

    private static bool ComponentPotion(Type type)
        => type == typeof(RegenPotion) || NativeZeroRecoveryPotions.Contains(type);

    private static bool ComponentPower(Type type, bool useReviewedSources = false)
        => type == typeof(RegenPower) || type == typeof(ReattachPower)
            || useReviewedSources && (type == typeof(SandpitPower) || type == typeof(TheHuntPower))
            || RemainingSafePowers.Contains(type) || NativeNonHealingGeneratedPowers.Contains(type)
            || NativeLouseClosure.Powers.Contains(type) || NativePotionZeroRecoveryPowers.Contains(type);

    // Called only at the stable main-thread root. The permanent deck prefix must be
    // checked independently: an unknown source cannot disappear into Exhaust and
    // thereby turn an uncertified root into a certified one.
    internal static string? ComponentHealingRejection(
        CombatPredictionSimulator simulator, Player player, bool useReviewedSources = false)
    {
        string? rejected = ComponentRootSourceRejection(simulator, player, useReviewedSources: useReviewedSources);
        if (rejected is not null) return rejected;
        return ComponentHealingUpperBound(simulator, player, 0, useReviewedSources: useReviewedSources) == int.MaxValue
            ? "branch-component" : null;
    }

    // This source review certifies recovery only; it is not a proof that native
    // inventory events or UI completion callbacks cannot replenish potion slots.
    internal static string? ComponentRootSourceRejection(
        CombatPredictionSimulator simulator, Player player, bool useReviewedSources = false)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.RootHealingCallbackRejection is { } callbackRejection)
            return callbackRejection;
        if (typeof(CardModel).Module.ModuleVersionId != ComponentAuditMvid)
            return "native-version";
        if (combat.Players.Count != 1)
            return "player-count";
        Type character = player.Character.GetType();
        if (character != typeof(Ironclad) && character != typeof(Silent)
            && character != typeof(Regent) && character != typeof(Necrobinder)
            && character != typeof(MegaCrit.Sts2.Core.Models.Characters.Defect))
            return "character:" + character.Name;
        if (combat.Modifiers.Count != 0 || !combat.RootHasCertifiedNonHealingSubscribers
            || combat.RootHasBaseLibCardModifiers
            || combat.AdaptedOnPlay is not null)
            return "extension";
        var state = simulator.State.GetPlayerCombatState(player);
        foreach (PredictedCard card in state.AllCards.Concat(combat.PendingReturningCards))
            if (!ComponentInitialCard(card.Preview, useReviewedSources))
                return "initial-card:" + card.Preview.GetType().Name;
        bool RootSource(AbstractModel source)
            => source switch
            {
                CardModel card => ComponentInitialCard(card, useReviewedSources),
                RelicModel relic => ComponentRelic(relic.GetType(), useReviewedSources),
                PowerModel power => ComponentPower(power.GetType(), useReviewedSources),
                MonsterModel monster => ComponentEnemy(monster.GetType(), useReviewedSources)
                    || useReviewedSources && monster.GetType() == typeof(Osty)
                        && combat.Allies.Any(ally => ReferenceEquals(ally, monster.Creature)
                            && ReferenceEquals(ally.PetOwner, player)),
                PotionModel potion => ComponentPotion(potion.GetType()),
                _ => combat.IsCertifiedNonHealingSubscriberSource(source)
                    || ComponentAttachment(source, useReviewedSources)
                    || source.GetType() == typeof(CccComboModel)
                    || source.GetType() == typeof(DebufferModel)
                    || source.GetType() == typeof(MultiplayerScalingModel),
            };
        string? rootSource = combat.FirstRejectedHealingRootSource(RootSource);
        if (rootSource is not null)
            return "root-listener:" + rootSource;
        if (!HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<RegentCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<ColorlessCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<IroncladCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<SilentCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<NecrobinderCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<DefectCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<StatusCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<CurseCardPool>()))
            return "generation-pool";
        return null;
    }

    internal static int ComponentHealingUpperBound(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal,
        bool includePotionHealing = true, int? maximumExplicitPotionUses = null,
        PotionStrategySnapshot? potionStrategy = null,
        SolverPotionPolicy effectivePotionPolicy = SolverPotionPolicy.Smart,
        bool useReviewedSources = false)
    {
        if (simulator.HasPendingChoice)
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.RootHealingCallbackRejection is not null)
            return int.MaxValue;
        var state = simulator.State.GetPlayerCombatState(player);
        if (combat.KnownEnemies.Count == 0
            || combat.KnownEnemies.Any(enemy => enemy.Monster is null
                || !ComponentEnemy(enemy.Monster.GetType(), useReviewedSources))
            || combat.RelicsOf(player).Any(relic => !ComponentRelic(relic.GetType(), useReviewedSources))
            || state.AllCards.Any(card => !ComponentRemainingCard(card.Preview, useReviewedSources))
            || combat.PendingReturningCards.Any(card => !ComponentRemainingCard(card.Preview, useReviewedSources))
            || combat.Allies.Any(ally => ally.Player is null && (ally.Monster?.GetType() != typeof(Osty)
                || useReviewedSources && !ReferenceEquals(ally.PetOwner, player)))
            || state.OrbQueue.Orbs.Any(orb => orb.GetType() != typeof(LightningOrb)
                && orb.GetType() != typeof(FrostOrb) && orb.GetType() != typeof(DarkOrb)
                && orb.GetType() != typeof(PlasmaOrb) && orb.GetType() != typeof(GlassOrb)))
            return int.MaxValue;
        long regen = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            Type type = power.GetType();
            if (!ComponentPower(type, useReviewedSources))
                return int.MaxValue;
            if (useReviewedSources && (type == typeof(SpeedPotionPower)
                    && !ReferenceEquals(power.Owner, player.Creature)
                || type == typeof(ShacklingPotionPower) && !combat.KnownEnemies.Contains(power.Owner)
                || type == typeof(SandpitPower) && (!combat.KnownEnemies.Contains(power.Owner)
                    || power.Owner.Monster?.GetType() != typeof(TheInsatiable)
                    || !ReferenceEquals(power.Target, player.Creature))))
                return int.MaxValue;
            if (type == typeof(RegenPower) && ReferenceEquals(power.Owner, player.Creature))
                regen += Math.Max(0, power.Amount);
            if (type == typeof(ReattachPower) && (power.Owner.Player is not null
                || !combat.KnownEnemies.Contains(power.Owner)
                || power.Owner.Monster?.GetType() != typeof(DecimillipedeSegmentFront)
                    && power.Owner.Monster?.GetType() != typeof(DecimillipedeSegmentMiddle)
                    && power.Owner.Monster?.GetType() != typeof(DecimillipedeSegmentBack)))
                return int.MaxValue;
        }
        if (maximumExplicitPotionUses is { } maximum
            && combat.PotionUses.Count(static use => !use.Automatic) >= maximum)
            includePotionHealing = false;
        int slots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        for (int slot = 0; slot < slots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            if (potion is null || !combat.IsPotionAvailable(player, slot))
                continue;
            if (!ComponentPotion(potion.GetType()))
                return int.MaxValue;
            // Existing Regen is retained even when manual potion use is forbidden.
            // Summing all legally available doses before the first tick overestimates
            // every staggered sequence, including a smaller remaining use allowance.
            if (includePotionHealing && potion.GetType() == typeof(RegenPotion)
                && (potionStrategy is null || potionStrategy.AllowsExplicitUse(
                    slot, potion.Id.Entry, effectivePotionPolicy, forceAllDisabled: false)))
                regen += Math.Max(0, potion.DynamicVars["RegenPower"].IntValue);
        }
        return RegenerationHealingUpperBound(regen, postCombatHeal);
    }
}
