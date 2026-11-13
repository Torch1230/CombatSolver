using System.Collections.Frozen;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using Dominions = MegaCrit.Sts2.Core.Models.Dread.Dominions;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    // Component certificate pinned to the audited installed native assembly. Every
    // component below has an explicit review; assembly membership is never sufficient.
    private static readonly Guid ComponentAuditMvid = new("ad562b4e-7352-4ca2-ae45-0ff44cd284bc");
    // Audited in sts2-0.113-healing-audit.json: these sources add enemy attributes,
    // player debuffs and harmful statuses. Generated sources still pass ComponentPower/Card.
    private static readonly FrozenSet<Type> ComponentModeSources = new Type[]
    {
        typeof(MegaCrit.Sts2.Core.Models.Dread.DreadRunModel),
        typeof(MegaCrit.Sts2.Core.Models.Blitz.BlitzRunModel),
        typeof(Dominions.AeonglassDominion), typeof(Dominions.CeremonialBeastDominion),
        typeof(Dominions.InsatiableDominion), typeof(Dominions.KaiserCrabDominion),
        typeof(Dominions.KinDominion), typeof(Dominions.KnowledgeDemonDominion),
        typeof(Dominions.LagavulinMatriarchDominion), typeof(Dominions.QueenDominion),
        typeof(Dominions.SoulFyshDominion), typeof(Dominions.TestSubjectDominion),
        typeof(Dominions.VantomDominion), typeof(Dominions.WaterfallGiantDominion),
    }.ToFrozenSet();
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
    }.Concat(Standard113NonHealingCards()).ToFrozenSet();

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
        typeof(SongBow), typeof(Storyteller), typeof(ExerciseMat), typeof(Ocarina),
        typeof(BizarreLamp), typeof(Fertilizer), typeof(WhittlingKnife), typeof(ConductorsBaton),
    }.ToFrozenSet();

    private static bool ComponentInitialCard(CardModel card)
        => (ComponentInitialCards.Contains(card.GetType())
                || IronDecimillipedeSafeCards.Contains(card.GetType())
                || RemainingSafeCards.Contains(card.GetType()))
            && HasCertifiedRemainingAttachments(card) && !GrowthValues.HasTarget(card);

    private static bool ComponentRemainingCard(CardModel card)
        => (ComponentInitialCards.Contains(card.GetType())
                || IronDecimillipedeSafeCards.Contains(card.GetType())
                || RemainingSafeCards.Contains(card.GetType())
                || NativeNonHealingGeneratedCards.Contains(card.GetType()))
            && HasCertifiedRemainingAttachments(card) && !GrowthValues.HasTarget(card);

    private static bool ComponentPotion(Type type)
        => type == typeof(RegenPotion) || NativeZeroRecoveryPotions.Contains(type);

    private static bool ComponentPower(Type type)
        => type == typeof(RegenPower) || type == typeof(ReattachPower)
            || RemainingSafePowers.Contains(type) || NativeNonHealingGeneratedPowers.Contains(type)
            || NativeLouseClosure.Powers.Contains(type) || NativePotionZeroRecoveryPowers.Contains(type);

    // Called only at the stable main-thread root. The permanent deck prefix must be
    // checked independently: an unknown source cannot disappear into Exhaust and
    // thereby turn an uncertified root into a certified one.
    internal static string? ComponentHealingRejection(
        CombatPredictionSimulator simulator, Player player)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (typeof(CardModel).Module.ModuleVersionId != ComponentAuditMvid)
            return "native-version";
        if (combat.Players.Count != 1)
            return "player-count";
        Type character = player.Character.GetType();
        if (character != typeof(Ironclad) && character != typeof(Silent)
            && character != typeof(Regent) && character != typeof(Necrobinder)
            && character != typeof(MegaCrit.Sts2.Core.Models.Characters.Defect)
            && character != typeof(Fool))
            return "character:" + character.Name;
        if (combat.Modifiers.Count != 0 || !combat.RootHasCertifiedNonHealingSubscribers
            || combat.RootHasBaseLibCardModifiers
            || combat.AdaptedOnPlay is not null)
            return "extension";
        var state = simulator.State.GetPlayerCombatState(player);
        foreach (PredictedCard card in state.AllCards.Concat(combat.PendingReturningCards))
            if (!ComponentInitialCard(card.Preview))
                return "initial-card:" + card.Preview.GetType().Name;
        bool RootSource(AbstractModel source)
            => source switch
            {
                CardModel card => ComponentInitialCard(card),
                RelicModel relic => ComponentRelics.Contains(relic.GetType()),
                PowerModel power => ComponentPower(power.GetType()),
                MonsterModel monster => ComponentEnemies.Contains(monster.GetType()),
                PotionModel potion => ComponentPotion(potion.GetType()),
                _ => combat.IsCertifiedNonHealingSubscriberSource(source)
                    || source.GetType() == typeof(global::MegaCrit.Sts2.Core.Models.Enchantments.Instinct)
                    || source.GetType() == typeof(CccComboModel)
                    || source.GetType() == typeof(DebufferModel)
                    || source.GetType() == typeof(MultiplayerScalingModel)
                    || ComponentModeSources.Contains(source.GetType()),
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
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<FoolCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<StatusCardPool>())
            || !HasCertifiedNativeNonHealingGenerationPool(ModelDb.CardPool<CurseCardPool>()))
            return "generation-pool";
        return ComponentHealingUpperBound(simulator, player, 0) == int.MaxValue
            ? "branch-component" : null;
    }

    internal static int ComponentHealingUpperBound(
        CombatPredictionSimulator simulator, Player player, int postCombatHeal,
        bool includePotionHealing = true, int? maximumExplicitPotionUses = null,
        PotionStrategySnapshot? potionStrategy = null,
        SolverPotionPolicy effectivePotionPolicy = SolverPotionPolicy.Smart)
    {
        if (simulator.HasPendingChoice)
            return int.MaxValue;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        var state = simulator.State.GetPlayerCombatState(player);
        if (combat.KnownEnemies.Count == 0
            || combat.KnownEnemies.Any(enemy => enemy.Monster is null
                || !ComponentEnemies.Contains(enemy.Monster.GetType()))
            || combat.RelicsOf(player).Any(relic => !ComponentRelics.Contains(relic.GetType()))
            || state.AllCards.Any(card => !ComponentRemainingCard(card.Preview))
            || combat.PendingReturningCards.Any(card => !ComponentRemainingCard(card.Preview))
            || combat.Allies.Any(ally => ally.Player is null && ally.Monster?.GetType() != typeof(Osty))
            || state.OrbQueue.Orbs.Any(orb => orb.GetType() != typeof(LightningOrb)
                && orb.GetType() != typeof(FrostOrb) && orb.GetType() != typeof(DarkOrb)
                && orb.GetType() != typeof(PlasmaOrb) && orb.GetType() != typeof(GlassOrb)))
            return int.MaxValue;
        long regen = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            Type type = power.GetType();
            if (!ComponentPower(type))
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
