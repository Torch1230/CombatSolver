using System.Collections.Concurrent;
using System.Reflection;
using CombatSolver;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal enum CardRiskTier : byte
{
    /// <summary>纯静态数值伤害/格挡/基础打击防御/简单线性数值 Buff-Debuff/纯摸牌回能的平凡卡（从高风险池彻底剔除）。</summary>
    Excluded = 0,
    /// <summary>含轻量非平凡语义的中风险卡（Exhaust/Innate 简单牌、自残/回血、条件分支或简单非事件 Power，高风险池约 20% 采样权重）。</summary>
    Moderate = 1,
    /// <summary>核心高危机制卡（选牌/变费/X费/星能/重放/Sly/Retain/Ethereal/AutoPlay/生成/复制/变形/洗牌/锻造/充能球/Osty/Doom/塔罗/旋律/箭矢/Fatal/私有状态/动态计算/战斗钩子/复杂 Power，高风险池约 80% 采样权重）。</summary>
    UltraHighRisk = 2,
}

internal readonly record struct CardRiskClassification(
    CardRiskTier Tier,
    string ReasonTag)
{
    public bool IsHighRisk => Tier != CardRiskTier.Excluded;
    public bool IsUltraHighRisk => Tier == CardRiskTier.UltraHighRisk;
}

internal readonly record struct RelicRiskClassification(
    bool IsHighRisk,
    string ReasonTag);

internal readonly record struct EncounterRiskClassification(
    bool IsHighRisk,
    int PriorityTier,
    string ReasonTag);

internal readonly record struct ActEncounterChoice(
    int ActIndex,
    EncounterModel Encounter,
    EncounterRiskClassification Risk);

/// <summary>
/// 元数据与静态目录驱动的三档高风险实体筛选器：
/// - 卡牌三档分级：Tier 0 (Excluded/Naive 剔除)、Tier 1 (Moderate 中风险 ~20% 权重)、Tier 2 (UltraHighRisk 超高风险 ~80% 权重)；
/// - 遗物过滤：剔除无战斗钩子/无状态的纯属性/地图/战后遗物，仅保留挂钩或有状态战斗遗物；
/// - 遭遇过滤：优先 Boss / Elite 与召唤/复活/塞牌/偷窃/阶段切换/复杂 Power 普通战，剔除无趣 Act-1 凑数弱怪。
/// </summary>
internal static class HighRiskPoolFilter
{
    private static readonly ConcurrentDictionary<ModelId, CardRiskClassification> CanonicalCardCache = new();
    private static readonly ConcurrentDictionary<Type, string?> UltraReflectionOverrideCache = new();
    private static readonly ConcurrentDictionary<Type, string?> GlowReflectionOverrideCache = new();

    private static readonly HashSet<HotPowerId> TrivialLinearPowers =
    [
        HotPowerId.VulnerablePower,
        HotPowerId.WeakPower,
        HotPowerId.StrengthPower,
        HotPowerId.DexterityPower,
        HotPowerId.FrailPower,
        HotPowerId.PoisonPower,
        HotPowerId.VigorPower,
        HotPowerId.BlockNextTurnPower,
    ];

    /// <summary>
    /// 8 种允许归类为“简单线性数值 Buff/Debuff”的 Power 自身合法携带的 Hook 位掩码。
    /// </summary>
    private const HotHookMask AllowedTrivialPowerHookMask =
        HotHookMask.ModifyDamageAdditive
        | HotHookMask.ModifyDamageMultiplicative
        | HotHookMask.ModifyBlockAdditive
        | HotHookMask.ModifyBlockMultiplicative
        | HotHookMask.AfterTurnEnd
        | HotHookMask.AfterSideTurnStart
        | HotHookMask.BeforeAttack
        | HotHookMask.AfterAttack
        | HotHookMask.ShouldClearBlock;

    /// <summary>
    /// 属于 Tier 2 (UltraHighRisk) 的非平凡 Power 战斗事件 Hook 位掩码（涉及出牌/摸牌/洗牌/弃牌/消耗/生成/变费/重放/落点/球/死亡/掉血/治疗/自动出牌/Power 联动等）。
    /// </summary>
    private const HotHookMask UltraHighRiskPowerHookMask =
        HotHookMask.BeforeCardPlayed
        | HotHookMask.AfterCardPlayed
        | HotHookMask.ModifyDamageCap
        | HotHookMask.BeforeDamageReceived
        | HotHookMask.AfterDamageReceived
        | HotHookMask.AfterDamageGiven
        | HotHookMask.AfterBlockGained
        | HotHookMask.AfterBlockBroken
        | HotHookMask.ShouldDraw
        | HotHookMask.ModifyHandDraw
        | HotHookMask.BeforeHandDraw
        | HotHookMask.AfterCardDrawn
        | HotHookMask.AfterShuffle
        | HotHookMask.BeforeTurnEnd
        | HotHookMask.BeforeSideTurnStart
        | HotHookMask.AfterEnergySpent
        | HotHookMask.AfterStarsSpent
        | HotHookMask.AfterPowerAmountChanged
        | HotHookMask.BeforePowerAmountChanged
        | HotHookMask.AfterCardExhausted
        | HotHookMask.AfterCardDiscarded
        | HotHookMask.AfterCardGenerated
        | HotHookMask.AfterCardEnteredCombat
        | HotHookMask.AfterDeath
        | HotHookMask.ShouldDie
        | HotHookMask.ModifyEnergyCost
        | HotHookMask.ModifyStarCost
        | HotHookMask.ModifyCardPlayCount
        | HotHookMask.ModifyCardPlayResultLocation
        | HotHookMask.AfterOrbChanneled
        | HotHookMask.AfterOrbEvoked
        | HotHookMask.ShouldTakeExtraTurn
        | HotHookMask.AfterHpLost
        | HotHookMask.AfterHeal
        | HotHookMask.ModifyHealAmount
        | HotHookMask.AfterAutoPostPlay;

    /// <summary>
    /// 目录 HookMask 仅标为 None/AfterSideTurnStart/AfterTurnEnd/ModifyDamage，但实际驱动选牌、变形、自动打出、锻造、充能球、星能、召唤、箭矢、旋律或跨回合特殊机制的 Tier 2 Power。
    /// </summary>
    private static readonly HashSet<HotPowerId> ExtraUltraHighRiskPowers =
    [
        HotPowerId.AccelerantPower,
        HotPowerId.ArcheryPower,
        HotPowerId.BarricadePower,
        HotPowerId.BiasedCognitionPower,
        HotPowerId.CalcifyPower,
        HotPowerId.ChoBarbsPower,
        HotPowerId.ConsumingShadowPower,
        HotPowerId.CoolantPower,
        HotPowerId.CountdownPower,
        HotPowerId.CrueltyPower,
        HotPowerId.DebilitatePower,
        HotPowerId.DislikeYouPower,
        HotPowerId.EcstasyPower,
        HotPowerId.EntropyPower,
        HotPowerId.ExplosiveArrowsPower,
        HotPowerId.FanOfKnivesPower,
        HotPowerId.FurnacePower,
        HotPowerId.GenesisPower,
        HotPowerId.GodByrdPower,
        HotPowerId.HiddenRootsPower,
        HotPowerId.HoningPower,
        HotPowerId.LethalityPower,
        HotPowerId.LightningRodPower,
        HotPowerId.LoopPower,
        HotPowerId.MayhemPower,
        HotPowerId.NeurosurgePower,
        HotPowerId.ParryPower,
        HotPowerId.QuietusPower,
        HotPowerId.RadiancePower,
        HotPowerId.RollingBoulderPower,
        HotPowerId.SeekingEdgePower,
        HotPowerId.SerratedArrowsPower,
        HotPowerId.ShadowStepPower,
        HotPowerId.SpinnerPower,
        HotPowerId.StarNextTurnPower,
        HotPowerId.SummonNextTurnPower,
        HotPowerId.TunePower,
        HotPowerId.WellLaidPlansPower,
        HotPowerId.WraithFormPower,
    ];

    /// <summary>
    /// 虽携带常规回合勾子但仅做线性护甲/局后金币的简单 Power，维持在 Tier 1 (Moderate)。
    /// </summary>
    private static readonly HashSet<HotPowerId> ModeratePowerOverrides =
    [
        HotPowerId.PlatingPower,
        HotPowerId.RoyaltiesPower,
    ];

    /// <summary>
    /// 经逐卡核实（对照 STS2 0.113.1 反编译源码）的 11 张纯静态数值自定义臂平凡卡：
    /// 仅因多变量/VFX 循环未走字节码，但零选牌、零生成/复制/变形/弃牌/消耗触发、零变费、零关键字、零 X/星能/重放/球/锻造/Osty/Doom/箭矢/旋律/塔罗/Fatal、零掉血治疗、零钩子/私有状态/非平凡 Power。
    /// 实际分类时仍需通过 CardModel、反射与升级形态双重核验才归入 Tier 0。
    /// </summary>
    private static readonly HashSet<HotCustomCardId> PureStaticCustomCards =
    [
        HotCustomCardId.Conflagration, // 1 费群攻 2x4（+1 段）
        HotCustomCardId.DaggerSpray,   // 1 费群攻 4x2（+2 伤）
        HotCustomCardId.DeadlyPoison,  // 1 费单体 5 毒（+2 毒）
        HotCustomCardId.FightMe,       // 2 费 5x2 + 双方力量
        HotCustomCardId.FlashOfSteel,  // 0 费单体伤害 + 抽 1
        HotCustomCardId.Haze,          // 2 费全体 4 毒 + 1 虚弱
        HotCustomCardId.Neutralize,    // 0 费单体 3 伤 + 1 虚弱
        HotCustomCardId.Prowess,       // 1 费能力 +1 力量 +1 敏捷
        HotCustomCardId.Slice,         // 0 费单体 6 伤（+3 伤）
        HotCustomCardId.SweepingBeam,  // 1 费群攻 6 伤 + 抽 1
        HotCustomCardId.Uppercut,      // 2 费单体 13 伤 + 1 虚弱 + 1 易伤
    ];

    /// <summary>
    /// 经逐卡核实的 Tier 1 (Moderate) 自定义臂中风险卡：
    /// 含简单条件数值、自残、Exhaust/Innate 简单效果、倍增格挡/能量、简单临时/静态 Power，但不含选牌/生成/复制/变形/变费/重放/Sly/Retain/Ethereal/X/星能/锻造/球/Osty/Doom/箭矢/旋律/塔罗/Fatal/Stun/复杂钩子。
    /// </summary>
    private static readonly HashSet<HotCustomCardId> ModerateCustomCards =
    [
        HotCustomCardId.Adrenaline,
        HotCustomCardId.Bloodletting,
        HotCustomCardId.BouncingFlask,
        HotCustomCardId.BrightestFlame,
        HotCustomCardId.BubbleBubble,
        HotCustomCardId.CrushUnder,
        HotCustomCardId.Dismantle,
        HotCustomCardId.DodgeAndRoll,
        HotCustomCardId.Dominate,
        HotCustomCardId.DoubleEnergy,
        HotCustomCardId.Entrench,
        HotCustomCardId.EscapePlan,
        HotCustomCardId.EvilEye,
        HotCustomCardId.Expose,
        HotCustomCardId.FeedingFrenzy,
        HotCustomCardId.Fisticuffs,
        HotCustomCardId.ForgottenRitual,
        HotCustomCardId.Friendship,
        HotCustomCardId.Ftl,
        HotCustomCardId.Glitterstream,
        HotCustomCardId.GoForTheEyes,
        HotCustomCardId.Hang,
        HotCustomCardId.Hyperbeam,
        HotCustomCardId.Impatience,
        HotCustomCardId.Joy,
        HotCustomCardId.KnowThyPlace,
        HotCustomCardId.MoltenFist,
        HotCustomCardId.Offering,
        HotCustomCardId.Omnislice,
        HotCustomCardId.Outbreak,
        HotCustomCardId.PactsEnd,
        HotCustomCardId.PiercingWail,
        HotCustomCardId.Pillage,
        HotCustomCardId.Prolong,
        HotCustomCardId.Putrefy,
        HotCustomCardId.SharedFate,
        HotCustomCardId.Shockwave,
        HotCustomCardId.Spite,
        HotCustomCardId.Suppress,
        HotCustomCardId.Tracking,
    ];

    private static readonly HashSet<string> TrivialDynamicVarKeys = new(StringComparer.Ordinal)
    {
        "Damage",
        "Block",
        "Cards",
        "Energy",
        "HitCount",
        "VulnerablePower",
        "WeakPower",
        "StrengthPower",
        "DexterityPower",
        "FrailPower",
        "PoisonPower",
        "VigorPower",
        "BlockNextTurnPower",
    };

    private static readonly HashSet<string> PureStaticCustomDynamicVarKeys = new(TrivialDynamicVarKeys, StringComparer.Ordinal)
    {
        "EnemyStrength", // FightMe
        "Power",         // Uppercut
    };

    private static readonly string[] UltraCardBehaviorProperties =
    [
        "IsPlayable",
        "HasTurnEndInHandEffect",
        "OrbEvokeType",
        "HasEnergyCostX",
        "HasStarCostX",
        "CanonicalStarCost",
    ];

    private static readonly string[] GlowOnlyCardProperties =
    [
        "ShouldGlowGoldInternal",
        "ShouldGlowRedInternal",
    ];

    private static readonly string[] UltraCardBehaviorMethods =
    [
        "GetResultLocationForCardPlay",
        "OnTurnEndInHand",
        "AfterCreated",
        "AfterThisWasTransformedIntoInternal",
        "AfterThisWasTransformedFrom",
    ];

    private static readonly MethodInfo[] AbstractModelCombatHooks = BuildAbstractModelCombatHooks();

    /// <summary>
    /// 仅在战斗外获牌/获金阶段触发的局外遗物（在 HotCoverageAuditor 中标注为 NonCombat）。
    /// </summary>
    private static readonly HashSet<string> NonCombatMetaRelicEntries = new(StringComparer.Ordinal)
    {
        "BING_BONG",
        "BOOK_OF_FIVE_RINGS",
        "DARKSTONE_PERIAPT",
        "DRAGON_FRUIT",
        "LUCKY_FYSH",
    };

    private static readonly HashSet<HotMonsterId> TheftOrEscapeMonsters =
    [
        HotMonsterId.ThievingHopper,
        HotMonsterId.GremlinMerc,
        HotMonsterId.FatGremlin,
        HotMonsterId.TheLost,
        HotMonsterId.TheForgotten,
        HotMonsterId.VineShambler,
    ];

    private static readonly HashSet<HotMonsterId> StatusOrAfflictionInjectorMonsters =
    [
        HotMonsterId.Chomper,
        HotMonsterId.Flyconid,
        HotMonsterId.HauntedShip,
        HotMonsterId.Myte,
        HotMonsterId.SlimedBerserker,
        HotMonsterId.LeafSlimeM,
        HotMonsterId.TwigSlimeM,
        HotMonsterId.SlitheringStrangler,
        HotMonsterId.Wriggler,
    ];

    private static readonly HashSet<HotMonsterId> ComplexMechanicNormalMonsters =
    [
        HotMonsterId.BowlbugEgg,
        HotMonsterId.BowlbugRock,
        HotMonsterId.BowlbugSilk,
        HotMonsterId.CorpseSlug,
        HotMonsterId.Exoskeleton,
        HotMonsterId.FossilStalker,
        HotMonsterId.GlobeHead,
        HotMonsterId.HunterKiller,
        HotMonsterId.Inklet,
        HotMonsterId.LouseProgenitor,
        HotMonsterId.OwlMagistrate,
        HotMonsterId.ScrollOfBiting,
        HotMonsterId.SpinyToad,
        HotMonsterId.AssassinRubyRaider,
        HotMonsterId.TurretOperator,
        HotMonsterId.LivingShield,
    ];

    public static CardRiskTier GetCardTier(CardModel card) => ClassifyCard(card).Tier;

    public static bool IsHighRiskCard(CardModel card) => ClassifyCard(card).IsHighRisk;

    public static bool IsUltraHighRiskCard(CardModel card) => ClassifyCard(card).IsUltraHighRisk;

    public static bool IsModerateCard(CardModel card) => ClassifyCard(card).Tier == CardRiskTier.Moderate;

    public static CardRiskClassification ClassifyCard(CardModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.IsCanonical)
            return CanonicalCardCache.GetOrAdd(card.Id, _ => ClassifyCardCore(card));
        return ClassifyCardCore(card);
    }

    private static CardRiskClassification ClassifyCardCore(CardModel card)
    {
        // 1. HotCardCatalog 元数据核查（未知卡保守归为 Tier 2）
        HotCustomCardId cardId = HotCardCatalog.FindByEntry(card.Id.Entry);
        if (cardId == HotCustomCardId.None)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "unknown_catalog_card");

        ref readonly HotCardCatalogEntry cat = ref HotCardCatalog.Get(cardId);

        // 2. 特殊域与特殊卡牌类型（塔罗 / 状态 / 诅咒 / 任务 / 复杂衍生物 -> Tier 2；纯回能摸牌消耗衍生物 Joy -> Tier 1）
        if (cat.Domain is HotCardDomain.Tarot or HotCardDomain.Status or HotCardDomain.Curse or HotCardDomain.Quest)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"domain_{cat.Domain.ToString().ToLowerInvariant()}");

        if (cat.Domain == HotCardDomain.Token)
        {
            return ModerateCustomCards.Contains(cardId)
                ? new CardRiskClassification(CardRiskTier.Moderate, "moderate_token_card")
                : new CardRiskClassification(CardRiskTier.UltraHighRisk, "domain_token");
        }

        if (card.Type is CardType.Status or CardType.Curse or CardType.Quest)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"card_type_{card.Type.ToString().ToLowerInvariant()}");

        // 3. HotCardCatalog 超高风险静态信号（动态计算 / 战斗钩子 / 私有可变状态 / 随机生成）
        if (cat.CalcVar != HotCalcVarKind.None)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"calc_var_{cat.CalcVar}");

        if (cat.HookMask != HotHookMask.None)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "card_hook_mask");

        HotCardPrivateKind privateKind = ClassifyCardPrivate(cardId);
        if (privateKind != HotCardPrivateKind.None)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"private_state_{privateKind}");

        if (cat.CanGenerateUnknown)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "can_generate_unknown");

        // 4. CardModel 自身与升级形态的超高风险属性（X 费 / 不可打出 / 星能 / 重放 / 充能球激发 / 回合末手牌 / Retain/Ethereal/Sly/Unplayable/Eternal / 特殊 Tag / CalculatedVar）
        if (TryDetectUltraCardModelProperties(card) is { } ultraModelReason)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, ultraModelReason);

        if (GetUltraReflectionOverrideReason(card.GetType()) is { } ultraOverrideReason)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, ultraOverrideReason);

        CardModel? upgradedCard = null;
        if (card.IsCanonical && card.MaxUpgradeLevel > 0)
        {
            upgradedCard = card.ToMutable();
            upgradedCard.UpgradeInternal();
            if (TryDetectUltraCardModelProperties(upgradedCard) is { } upgUltraReason)
                return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"upgraded_{upgUltraReason}");
        }

        // 5. 自定义臂卡（!cat.UsesBytecode）：区分纯静态平凡卡(Tier 0) / 中风险自定义卡(Tier 1) / 超高风险自定义卡(Tier 2)
        if (!cat.UsesBytecode)
        {
            if (PureStaticCustomCards.Contains(cardId))
            {
                // 双重核验：即便在纯静态自定义白名单中，也必须通过无治疗、无发光条件覆写、无任何非白名单动态变量及升级形态核验，方允许剔除为 Tier 0
                if (cat.CanHealPlayer)
                    return new CardRiskClassification(CardRiskTier.Moderate, "moderate_heal_custom");
                if (GetGlowReflectionOverrideReason(card.GetType()) is { } customGlowReason)
                    return new CardRiskClassification(CardRiskTier.Moderate, customGlowReason);
                if (TryDetectModerateCardModelProperties(card, allowPureStaticCustomVars: true) is { } customModReason)
                    return new CardRiskClassification(CardRiskTier.Moderate, customModReason);
                if (upgradedCard != null
                    && TryDetectModerateCardModelProperties(upgradedCard, allowPureStaticCustomVars: true) is { } upgCustomModReason)
                {
                    return new CardRiskClassification(CardRiskTier.Moderate, $"upgraded_{upgCustomModReason}");
                }

                return new CardRiskClassification(CardRiskTier.Excluded, "naive_pure_static_custom");
            }

            if (ModerateCustomCards.Contains(cardId))
                return new CardRiskClassification(CardRiskTier.Moderate, "moderate_custom_handler");

            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "ultra_custom_handler");
        }

        // 6. 字节码卡（cat.UsesBytecode == true）：先完整扫描所有指令，确保任意超高风险指令/Power 严格优先于中风险信号
        List<HotCardEffectOp> ops = new(4);
        HotCardCatalog.CompileCardBytecode(
            cardId,
            cardDefId: 1,
            ensurePowerDefId: static powerId => (ushort)powerId,
            ops,
            out _,
            out byte opCount,
            out HotCustomCardId customOut);

        if (customOut != HotCustomCardId.None)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "custom_bytecode_fallback");

        if (opCount == 0)
            return new CardRiskClassification(CardRiskTier.UltraHighRisk, "zero_op_special");

        bool hasAttack = false;
        bool hasBlock = false;
        bool hasDrawOrEnergy = false;
        bool hasTrivialPower = false;
        HotPowerId moderatePowerId = HotPowerId.None;
        HotCardOpKind moderateOp = HotCardOpKind.Nop;

        for (int i = 0; i < ops.Count; i++)
        {
            HotCardEffectOp op = ops[i];
            if ((op.Flags & (byte)HotCardOpFlags.AddCloneToDiscard) != 0)
                return new CardRiskClassification(CardRiskTier.UltraHighRisk, "op_add_clone_to_discard");

            switch (op.Op)
            {
                case HotCardOpKind.Attack:
                case HotCardOpKind.AttackAll:
                case HotCardOpKind.AttackRandom:
                    hasAttack = true;
                    break;

                case HotCardOpKind.GainBlock:
                    hasBlock = true;
                    break;

                case HotCardOpKind.Draw:
                case HotCardOpKind.GainEnergy:
                    hasDrawOrEnergy = true;
                    break;

                case HotCardOpKind.LoseHp:
                case HotCardOpKind.Heal:
                case HotCardOpKind.ApplyTempStat:
                    if (moderateOp == HotCardOpKind.Nop)
                        moderateOp = op.Op;
                    break;

                case HotCardOpKind.ApplyPowerSelf:
                case HotCardOpKind.ApplyPowerTarget:
                case HotCardOpKind.ApplyPowerAllEnemies:
                {
                    HotPowerId appliedPowerId = (HotPowerId)op.Arg0;
                    if (TrivialLinearPowers.Contains(appliedPowerId))
                    {
                        ref readonly HotPowerCatalogEntry pwEntry = ref HotPowerCatalog.Get(appliedPowerId);
                        if (pwEntry.Id == HotPowerId.None
                            || pwEntry.CanHealPlayer
                            || pwEntry.HasRelevantTurnStartAmount
                            || (pwEntry.HookMask & ~AllowedTrivialPowerHookMask) != HotHookMask.None)
                        {
                            return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"ultra_power_flag_{appliedPowerId}");
                        }
                        hasTrivialPower = true;
                    }
                    else if (IsUltraHighRiskPower(appliedPowerId))
                    {
                        return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"ultra_power_{appliedPowerId}");
                    }
                    else if (moderatePowerId == HotPowerId.None)
                    {
                        moderatePowerId = appliedPowerId;
                    }
                    break;
                }

                // Forge / ChannelOrb / EvokeOrb / SummonOsty / AttackOsty / ShootArrow / AddFixedCard / DiscardHand / GainStars / Custom
                default:
                    return new CardRiskClassification(CardRiskTier.UltraHighRisk, $"ultra_op_{op.Op}");
            }
        }

        // 7. 中风险判定（仅在未触发任何 Tier 2 信号后进入 Tier 1）
        if (moderatePowerId != HotPowerId.None)
            return new CardRiskClassification(CardRiskTier.Moderate, $"moderate_power_{moderatePowerId}");

        if (moderateOp != HotCardOpKind.Nop)
            return new CardRiskClassification(CardRiskTier.Moderate, $"moderate_op_{moderateOp}");

        if (cat.CanHealPlayer)
            return new CardRiskClassification(CardRiskTier.Moderate, "moderate_can_heal_player");

        if (TryDetectModerateCardModelProperties(card, allowPureStaticCustomVars: false) is { } modModelReason)
            return new CardRiskClassification(CardRiskTier.Moderate, modModelReason);

        if (GetGlowReflectionOverrideReason(card.GetType()) is { } glowReason)
            return new CardRiskClassification(CardRiskTier.Moderate, glowReason);

        if (upgradedCard != null
            && TryDetectModerateCardModelProperties(upgradedCard, allowPureStaticCustomVars: false) is { } upgModReason)
        {
            return new CardRiskClassification(CardRiskTier.Moderate, $"upgraded_{upgModReason}");
        }

        // 8. 全部校验通过 -> 确认为纯平凡/naive 卡，归入 Tier 0 剔除
        if (card.IsBasicStrikeOrDefend)
            return new CardRiskClassification(CardRiskTier.Excluded, "naive_basic_strike_defend");
        if (hasTrivialPower)
            return new CardRiskClassification(CardRiskTier.Excluded, "naive_linear_power");
        if (hasDrawOrEnergy)
            return new CardRiskClassification(CardRiskTier.Excluded, "naive_cantrip_draw_energy");
        if (hasAttack && hasBlock)
            return new CardRiskClassification(CardRiskTier.Excluded, "naive_vanilla_damage_block");
        if (hasBlock)
            return new CardRiskClassification(CardRiskTier.Excluded, "naive_vanilla_block");
        return new CardRiskClassification(CardRiskTier.Excluded, "naive_vanilla_damage");
    }

    internal static bool IsUltraHighRiskPower(HotPowerId powerId)
    {
        if (powerId == HotPowerId.None)
            return true;
        if (TrivialLinearPowers.Contains(powerId) || ModeratePowerOverrides.Contains(powerId))
            return false;
        if (ClassifyPowerSpecial(powerId) != HotPowerSpecialKind.None)
            return true;
        if (ExtraUltraHighRiskPowers.Contains(powerId))
            return true;

        ref readonly HotPowerCatalogEntry pw = ref HotPowerCatalog.Get(powerId);
        if (pw.Id == HotPowerId.None)
            return true;
        if (pw.CanHealPlayer || pw.HasRelevantTurnStartAmount)
            return true;
        return (pw.HookMask & UltraHighRiskPowerHookMask) != HotHookMask.None;
    }

    internal static HotPowerSpecialKind ClassifyPowerSpecial(HotPowerId powerId)
        => powerId switch
        {
            HotPowerId.SurroundedPower => HotPowerSpecialKind.Surrounded,
            HotPowerId.NightmarePower => HotPowerSpecialKind.Nightmare,
            HotPowerId.OrbitPower => HotPowerSpecialKind.Orbit,
            HotPowerId.ShowOffPower => HotPowerSpecialKind.ShowOff,
            HotPowerId.ImpaledPower => HotPowerSpecialKind.Impaled,
            HotPowerId.AimPower => HotPowerSpecialKind.Aim,
            HotPowerId.EncorePower => HotPowerSpecialKind.Encore,
            HotPowerId.YahPower => HotPowerSpecialKind.Yah,
            HotPowerId.LullabyPower => HotPowerSpecialKind.Lullaby,
            _ => HotPowerSpecialKind.None,
        };

    private static string? TryDetectUltraCardModelProperties(CardModel card)
    {
        // X 费 / 不可打出负费 / 星能 / 重放 / 充能球激发 / 回合末手牌效果
        if (card.EnergyCost.CostsX)
            return "energy_cost_x";
        if (card.EnergyCost.Canonical < 0)
            return "unplayable_energy_cost";
        if (card.HasStarCostX || card.CanonicalStarCost >= 0 || card.BaseStarCost >= 0)
            return "star_cost";
        if (card.BaseReplayCount > 0 || card.ExtraReplayThisTurn > 0)
            return "replay_count";
        if (card.OrbEvokeType != OrbEvokeType.None)
            return "orb_evoke";
        if (card.HasTurnEndInHandEffect)
            return "turn_end_in_hand";

        // 超高风险关键字：Retain / Ethereal / Sly / Unplayable / Eternal
        foreach (CardKeyword kw in card.CanonicalKeywords)
        {
            if (IsUltraKeyword(kw))
                return $"ultra_keyword_{kw.ToString().ToLowerInvariant()}";
        }
        foreach (CardKeyword kw in card.Keywords)
        {
            if (IsUltraKeyword(kw))
                return $"ultra_keyword_{kw.ToString().ToLowerInvariant()}";
        }

        // 特殊卡牌标签：Minion / OstyAttack / Shiv / Shoot / 旋律(Wah/Cho/Ki) 等
        foreach (CardTag tag in card.Tags)
        {
            if (tag is not (CardTag.None or CardTag.Strike or CardTag.Defend))
                return $"card_tag_{tag}";
        }

        // CalculatedVar 动态计算变量一律属超高风险
        foreach (KeyValuePair<string, DynamicVar> kv in card.DynamicVars)
        {
            if (kv.Value is CalculatedVar)
                return $"calculated_var_{kv.Key}";
        }

        return null;
    }

    private static string? TryDetectModerateCardModelProperties(CardModel card, bool allowPureStaticCustomVars)
    {
        // 中风险关键字（Exhaust / Innate 等非 Tier 2 关键字）
        foreach (CardKeyword kw in card.CanonicalKeywords)
        {
            if (kw != CardKeyword.None && !IsUltraKeyword(kw))
                return $"moderate_keyword_{kw.ToString().ToLowerInvariant()}";
        }
        foreach (CardKeyword kw in card.Keywords)
        {
            if (kw != CardKeyword.None && !IsUltraKeyword(kw))
                return $"moderate_keyword_{kw.ToString().ToLowerInvariant()}";
        }

        // 多级升级（如 DingyDagger 无限升级）
        if (card.MaxUpgradeLevel > 1)
            return "moderate_multi_upgrade";

        // DynamicVars 白名单校验
        HashSet<string> allowedVars = allowPureStaticCustomVars ? PureStaticCustomDynamicVarKeys : TrivialDynamicVarKeys;
        foreach (KeyValuePair<string, DynamicVar> kv in card.DynamicVars)
        {
            if (!allowedVars.Contains(kv.Key))
                return $"moderate_var_{kv.Key}";
        }

        return null;
    }

    private static bool IsUltraKeyword(CardKeyword kw)
        => kw is CardKeyword.Retain
            or CardKeyword.Ethereal
            or CardKeyword.Sly
            or CardKeyword.Unplayable
            or CardKeyword.Eternal;

    private static string? GetUltraReflectionOverrideReason(Type cardType)
        => UltraReflectionOverrideCache.GetOrAdd(cardType, static t =>
        {
            // 1. 子类私有实例字段（潜在跨出牌/跨回合私有状态）
            for (Type? cur = t; cur != null && cur != typeof(CardModel); cur = cur.BaseType)
            {
                FieldInfo[] fields = cur.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (fields.Length > 0)
                    return $"private_field_{fields[0].Name}";
            }

            // 2. CardModel 超高风险行为属性覆写（IsPlayable / HasTurnEndInHandEffect / OrbEvokeType / Star / XCost 等）
            foreach (string propName in UltraCardBehaviorProperties)
            {
                PropertyInfo? prop = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo? getter = prop?.GetGetMethod(nonPublic: true);
                if (getter != null && getter.DeclaringType != typeof(CardModel) && getter.DeclaringType != typeof(AbstractModel))
                    return $"override_{propName}";
            }

            // 3. CardModel 生命周期/结果位置覆写
            foreach (string methodName in UltraCardBehaviorMethods)
            {
                MethodInfo? method = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null && method.DeclaringType != typeof(CardModel) && method.DeclaringType != typeof(AbstractModel))
                    return $"override_{methodName}";
            }

            // 4. AbstractModel 战斗钩子方法覆写（Before* / After* / Modify* / Should* / TryModify*）
            foreach (MethodInfo baseHook in AbstractModelCombatHooks)
            {
                ParameterInfo[] parameters = baseHook.GetParameters();
                Type[] paramTypes = new Type[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    paramTypes[i] = parameters[i].ParameterType;

                MethodInfo? derived = t.GetMethod(
                    baseHook.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: paramTypes,
                    modifiers: null);
                if (derived != null && derived.DeclaringType != typeof(AbstractModel) && derived.DeclaringType != typeof(CardModel))
                    return $"override_hook_{baseHook.Name}";
            }

            return null;
        });

    private static string? GetGlowReflectionOverrideReason(Type cardType)
        => GlowReflectionOverrideCache.GetOrAdd(cardType, static t =>
        {
            foreach (string propName in GlowOnlyCardProperties)
            {
                PropertyInfo? prop = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo? getter = prop?.GetGetMethod(nonPublic: true);
                if (getter != null && getter.DeclaringType != typeof(CardModel) && getter.DeclaringType != typeof(AbstractModel))
                    return $"moderate_override_{propName}";
            }
            return null;
        });

    private static MethodInfo[] BuildAbstractModelCombatHooks()
    {
        List<MethodInfo> list = [];
        foreach (MethodInfo m in typeof(AbstractModel).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (!m.IsVirtual || m.IsSpecialName)
                continue;
            string name = m.Name;
            if (name is "AfterCloned" or "AfterDeserialized")
                continue;
            if (name.StartsWith("Before", StringComparison.Ordinal)
                || name.StartsWith("After", StringComparison.Ordinal)
                || name.StartsWith("Modify", StringComparison.Ordinal)
                || name.StartsWith("Should", StringComparison.Ordinal)
                || name.StartsWith("TryModify", StringComparison.Ordinal))
            {
                list.Add(m);
            }
        }
        return list.ToArray();
    }

    internal static HotCardPrivateKind ClassifyCardPrivate(HotCustomCardId cardId)
        => cardId switch
        {
            HotCustomCardId.Claw => HotCardPrivateKind.Claw,
            HotCustomCardId.GeneticAlgorithm => HotCardPrivateKind.GeneticAlgorithm,
            HotCustomCardId.Maul => HotCardPrivateKind.Maul,
            HotCustomCardId.MadScience => HotCardPrivateKind.MadScience,
            HotCustomCardId.Rampage => HotCardPrivateKind.Rampage,
            HotCustomCardId.TheScythe => HotCardPrivateKind.TheScythe,
            HotCustomCardId.TheShot => HotCardPrivateKind.TheShot,
            HotCustomCardId.ShowOff => HotCardPrivateKind.ShowOff,
            HotCustomCardId.SovereignBlade => HotCardPrivateKind.SovereignBlade,
            _ => HotCardPrivateKind.None,
        };

    public static bool IsHighRiskRelic(RelicModel relic) => ClassifyRelic(relic).IsHighRisk;

    public static bool IsStatefulOrTrackedRelic(RelicModel relic)
    {
        HotRelicId rid = HotRelicCatalog.FindByEntry(relic.Id.Entry);
        if (rid == HotRelicId.None)
            return false;
        ref readonly HotRelicCatalogEntry entry = ref HotRelicCatalog.Get(rid);
        return entry.IsStateful || entry.IsTracked || ClassifyRelicSpecial(rid) != HotRelicSpecialKind.None;
    }

    public static RelicRiskClassification ClassifyRelic(RelicModel relic)
    {
        ArgumentNullException.ThrowIfNull(relic);
        HotRelicId rid = HotRelicCatalog.FindByEntry(relic.Id.Entry);
        if (rid == HotRelicId.None)
            return new RelicRiskClassification(false, "unknown_relic");

        ref readonly HotRelicCatalogEntry entry = ref HotRelicCatalog.Get(rid);
        HotRelicSpecialKind special = ClassifyRelicSpecial(rid);
        bool hasSpecial = special != HotRelicSpecialKind.None || entry.IsStateful || entry.IsTracked;

        if (entry.HookMask == HotHookMask.None && !hasSpecial)
            return new RelicRiskClassification(false, "passive_or_map_relic");

        if (NonCombatMetaRelicEntries.Contains(relic.Id.Entry) && !hasSpecial)
            return new RelicRiskClassification(false, "non_combat_meta_relic");

        if ((entry.HookMask & ~HotHookMask.AfterCombatEnd) == HotHookMask.None && !hasSpecial)
            return new RelicRiskClassification(false, "post_combat_only_relic");

        if (hasSpecial)
            return new RelicRiskClassification(true, $"stateful_or_tracked_{special}");

        return new RelicRiskClassification(true, "hooked_combat_relic");
    }

    internal static HotRelicSpecialKind ClassifyRelicSpecial(HotRelicId relicId)
        => relicId switch
        {
            HotRelicId.ArtOfWar => HotRelicSpecialKind.ArtOfWar,
            HotRelicId.ConductorsBaton => HotRelicSpecialKind.ConductorsBaton,
            HotRelicId.Wuppy => HotRelicSpecialKind.Wuppy,
            HotRelicId.Byrdpip => HotRelicSpecialKind.Byrdpip,
            HotRelicId.BeltBuckle => HotRelicSpecialKind.BeltBuckle,
            HotRelicId.BoneTea => HotRelicSpecialKind.BoneTea,
            HotRelicId.EmotionChip => HotRelicSpecialKind.EmotionChip,
            HotRelicId.FakeHappyFlower => HotRelicSpecialKind.FakeHappyFlower,
            HotRelicId.FakeVenerableTeaSet => HotRelicSpecialKind.FakeVenerableTeaSet,
            HotRelicId.HappyFlower => HotRelicSpecialKind.HappyFlower,
            HotRelicId.MiniRegent => HotRelicSpecialKind.MiniRegent,
            HotRelicId.GalacticDust => HotRelicSpecialKind.GalacticDust,
            HotRelicId.PaelsEye => HotRelicSpecialKind.PaelsEye,
            HotRelicId.PaelsTears => HotRelicSpecialKind.PaelsTears,
            HotRelicId.RainbowRing => HotRelicSpecialKind.RainbowRing,
            HotRelicId.RedSkull => HotRelicSpecialKind.RedSkull,
            HotRelicId.Pendulum => HotRelicSpecialKind.Pendulum,
            HotRelicId.Pocketwatch => HotRelicSpecialKind.Pocketwatch,
            HotRelicId.PollinousCore => HotRelicSpecialKind.PollinousCore,
            HotRelicId.RuinedHelmet => HotRelicSpecialKind.RuinedHelmet,
            HotRelicId.UnsettlingLamp => HotRelicSpecialKind.UnsettlingLamp,
            HotRelicId.VenerableTeaSet => HotRelicSpecialKind.VenerableTeaSet,
            HotRelicId.BeatingRemnant => HotRelicSpecialKind.BeatingRemnant,
            HotRelicId.BrilliantScarf => HotRelicSpecialKind.BrilliantScarf,
            HotRelicId.BurningSticks => HotRelicSpecialKind.BurningSticks,
            HotRelicId.CentennialPuzzle => HotRelicSpecialKind.CentennialPuzzle,
            HotRelicId.DemonTongue => HotRelicSpecialKind.DemonTongue,
            HotRelicId.IronClub => HotRelicSpecialKind.IronClub,
            HotRelicId.JossPaper => HotRelicSpecialKind.JossPaper,
            HotRelicId.Kunai => HotRelicSpecialKind.Kunai,
            HotRelicId.Kusarigama => HotRelicSpecialKind.Kusarigama,
            HotRelicId.LetterOpener => HotRelicSpecialKind.LetterOpener,
            HotRelicId.LizardTail => HotRelicSpecialKind.LizardTail,
            HotRelicId.Metronome => HotRelicSpecialKind.Metronome,
            HotRelicId.MusicBox => HotRelicSpecialKind.MusicBox,
            HotRelicId.Nunchaku => HotRelicSpecialKind.Nunchaku,
            HotRelicId.OrnamentalFan => HotRelicSpecialKind.OrnamentalFan,
            HotRelicId.PaelsLegion => HotRelicSpecialKind.PaelsLegion,
            HotRelicId.PenNib => HotRelicSpecialKind.PenNib,
            HotRelicId.Permafrost => HotRelicSpecialKind.Permafrost,
            HotRelicId.Regalite => HotRelicSpecialKind.Regalite,
            HotRelicId.Shuriken => HotRelicSpecialKind.Shuriken,
            HotRelicId.ThrowingAxe => HotRelicSpecialKind.ThrowingAxe,
            HotRelicId.TuningFork => HotRelicSpecialKind.TuningFork,
            HotRelicId.Vambrace => HotRelicSpecialKind.Vambrace,
            _ => HotRelicSpecialKind.None,
        };

    public static EncounterRiskClassification ClassifyEncounter(
        EncounterModel encounter,
        bool includeEventEncounters = false)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        if (HotCoverageAuditor.IsMockOrDeprecated(encounter))
            return new EncounterRiskClassification(false, 0, "mock_or_deprecated");

        string typeName = encounter.GetType().Name;
        if (typeName is "TheArchitectEventEncounter"
            or "BattlewornDummyEventV2Encounter"
            or "BattlewornDummyEventV3Encounter")
        {
            return new EncounterRiskClassification(false, 0, "excluded_event_variant");
        }

        MonsterModel[] monsters = encounter.AllPossibleMonsters
            .Where(static m => !HotCoverageAuditor.IsMockOrDeprecated(m))
            .DistinctBy(static m => m.Id)
            .ToArray();
        if (monsters.Length == 0)
            return new EncounterRiskClassification(false, 0, "empty_encounter");

        if (encounter.RoomType == RoomType.Boss)
            return new EncounterRiskClassification(true, PriorityTier: 3, "boss");

        if (encounter.RoomType == RoomType.Elite)
            return new EncounterRiskClassification(true, PriorityTier: 2, "elite");

        if (typeName == "BattlewornDummyEventV1Encounter")
        {
            return includeEventEncounters
                ? new EncounterRiskClassification(true, PriorityTier: 1, "event_timelimit")
                : new EncounterRiskClassification(false, 0, "event_only");
        }

        if (!includeEventEncounters && typeName.EndsWith("EventEncounter", StringComparison.Ordinal))
            return new EncounterRiskClassification(false, 0, "event_only");

        bool hasSummoner = false;
        bool hasPhaseChanger = false;
        bool hasTheftOrEscape = encounter.CustomRewardDescription != null;
        bool hasStatusInjector = false;
        bool hasComplexPower = false;
        bool hasUniqueWeakMechanic = false;

        foreach (MonsterModel monster in monsters)
        {
            HotMonsterId mid = HotMonsterCatalog.FindByEntry(monster.Id.Entry);
            if (mid == HotMonsterId.None)
                continue;

            if (!HotMonsterCatalog.GetSpawnClosure(mid).IsEmpty)
                hasSummoner = true;

            if ((int)mid < HotMonsterCatalog.ById.Length
                && HotMonsterCatalog.ById[(int)mid].StateKind != HotMonsterStateKind.None)
            {
                hasPhaseChanger = true;
            }

            if (TheftOrEscapeMonsters.Contains(mid))
                hasTheftOrEscape = true;

            if (StatusOrAfflictionInjectorMonsters.Contains(mid))
                hasStatusInjector = true;

            if (ComplexMechanicNormalMonsters.Contains(mid))
                hasComplexPower = true;

            if (mid is HotMonsterId.ThievingHopper or HotMonsterId.TurretOperator or HotMonsterId.LivingShield)
                hasUniqueWeakMechanic = true;
        }

        // Act-1 弱怪仅保留机制不可替代的偷牌逃跑/活体护盾遭遇，剔除单蠕虫/弱怪重复填充
        if (encounter.IsWeak)
        {
            if (hasUniqueWeakMechanic)
                return new EncounterRiskClassification(true, PriorityTier: 1, hasTheftOrEscape ? "weak_thief_escaper" : "weak_unique_mechanic");
            return new EncounterRiskClassification(false, 0, "weak_act1_filler");
        }

        if (hasSummoner)
            return new EncounterRiskClassification(true, PriorityTier: 1, "summoner_or_reviver");
        if (hasPhaseChanger)
            return new EncounterRiskClassification(true, PriorityTier: 1, "phase_changer");
        if (hasTheftOrEscape)
            return new EncounterRiskClassification(true, PriorityTier: 1, "theft_or_escape");
        if (hasStatusInjector)
            return new EncounterRiskClassification(true, PriorityTier: 1, "status_or_affliction_injector");
        if (hasComplexPower)
            return new EncounterRiskClassification(true, PriorityTier: 1, "complex_monster_power");

        // 多构装体组合遭遇（如 ConstructMenagerieNormal）保留，但单怪版 CubexConstructNormal/PunchConstructNormal 剔除
        if (typeName == "ConstructMenagerieNormal")
            return new EncounterRiskClassification(true, PriorityTier: 1, "multi_construct_artifact");

        return new EncounterRiskClassification(false, 0, "trivial_stat_encounter");
    }

    public static EncounterModel[] SelectHighRiskEncounters(
        IEnumerable<EncounterModel> candidates,
        bool includeEventEncounters = false)
    {
        return candidates
            .DistinctBy(static e => e.Id)
            .Select(e => (Encounter: e, Risk: ClassifyEncounter(e, includeEventEncounters)))
            .Where(static x => x.Risk.IsHighRisk)
            .OrderByDescending(static x => x.Risk.PriorityTier)
            .ThenBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .Select(static x => x.Encounter)
            .ToArray();
    }

    /// <summary>
    /// 从全部 Act 遭遇池中筛选高风险遭遇（含 Boss、Elite 与具备召唤/复活/塞牌/偷窃/阶段切换/复杂 Power 的普通战）。
    /// </summary>
    public static List<ActEncounterChoice> SelectHighRiskActEncounters()
    {
        List<ActEncounterChoice> list = [];
        ActModel[] acts = ModelDb.Acts.ToArray();
        for (int a = 0; a < acts.Length; a++)
        {
            foreach (EncounterModel e in acts[a].AllEncounters)
            {
                EncounterRiskClassification risk = ClassifyEncounter(e, includeEventEncounters: false);
                if (risk.IsHighRisk)
                    list.Add(new ActEncounterChoice(Math.Min(a, 2), e, risk));
            }
        }

        return list
            .DistinctBy(static x => x.Encounter.Id)
            .OrderByDescending(static x => x.Risk.PriorityTier)
            .ThenBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 判断卡牌是否适合作为清空初始打击/防御后的低费可打出骨架牌（非 Unplayable、非 Power、0~1 费、无星能门槛）。
    /// </summary>
    public static bool IsCheapPlayableBackboneCard(CardModel card)
        => card.Type is CardType.Attack or CardType.Skill
            && !card.EnergyCost.CostsX
            && card.EnergyCost.Canonical is >= 0 and <= 1
            && !card.HasStarCostX
            && card.CanonicalStarCost <= 0
            && !card.CanonicalKeywords.Contains(CardKeyword.Unplayable);

    /// <summary>
    /// 返回角色初始牌组中非基础打击/防御且通过高风险筛选的特征起始牌（如 Zap/Dualcast、Bodyguard/Unleash、FallingStar/Venerate、FoolishShot、Survivor）。
    /// </summary>
    public static CardModel[] GetHighRiskStarterBackbone(CharacterModel character, GameMode mode)
    {
        return character.StartingDeck
            .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                && !c.IsBasicStrikeOrDefend
                && IsHighRiskCard(c)
                && (mode != GameMode.Blitz || c.IsAllowedInBlitz))
            .DistinctBy(static c => c.Id)
            .OrderByDescending(static c => (int)GetCardTier(c))
            .ThenBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 80% Tier 2 (UltraHighRisk) / 20% Tier 1 (Moderate) 加权采样辅助（严格不含 Tier 0；某档为空时自动回退至另一档）。
    /// </summary>
    public static CardModel PickTierWeighted(CardModel[] tier2, CardModel[] tier1, Random rng)
    {
        if (tier2.Length > 0 && (tier1.Length == 0 || rng.Next(5) != 0))
            return tier2[rng.Next(tier2.Length)];
        return tier1[rng.Next(tier1.Length)];
    }

    public static CardModel[] GetAllSingleplayerCards()
    {
        HashSet<ModelId> excludedPoolIds = [];
        TryAddPoolCardIds<MockCardPool>(excludedPoolIds);
        TryAddPoolCardIds<DeprecatedCardPool>(excludedPoolIds);

        return ModelDb.AllCards
            .Concat(ModelDb.All.OfType<CardModel>())
            .DistinctBy(static c => c.Id)
            .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                && !excludedPoolIds.Contains(c.Id)
                && c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 独立 CLI 审计命令（<c>--dump-high-risk-pool</c>）：
    /// 输出三档风险分级总计与按角色/域分布（Tier 0 Excluded / Tier 1 Moderate / Tier 2 UltraHighRisk）、
    /// 全部被剔除 Tier 0 平凡卡清单及原因、Tier 2 与 Tier 1 样例，以及高风险遗物/遭遇统计。
    /// </summary>
    public static int RunDumpCommand(string[] args)
    {
        if (args.Length > 0)
            throw new ArgumentException($"--dump-high-risk-pool 不接受额外参数：{string.Join(' ', args)}");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        GameBootstrap.EnsureBootstrapped(loop);

        CardModel[] allCards = GetAllSingleplayerCards();
        List<(CardModel Card, CardRiskClassification Risk, string Group)> classified = new(allCards.Length);

        Dictionary<ModelId, string> charByCardId = [];
        foreach (CharacterModel ch in ModelDb.AllCharacters.OrderBy(static c => c.Id.Entry, StringComparer.Ordinal))
        {
            if (HotCoverageAuditor.IsMockOrDeprecated(ch) || ch.CardPool == null)
                continue;
            foreach (CardModel c in ch.CardPool.AllCards)
                charByCardId.TryAdd(c.Id, ch.Id.Entry);
        }

        foreach (CardModel card in allCards)
        {
            CardRiskClassification risk = ClassifyCard(card);
            HotCustomCardId cid = HotCardCatalog.FindByEntry(card.Id.Entry);
            string group = charByCardId.TryGetValue(card.Id, out string? charEntry)
                ? charEntry
                : cid != HotCustomCardId.None
                    ? HotCardCatalog.Get(cid).Domain.ToString().ToUpperInvariant()
                    : "OTHER";
            classified.Add((card, risk, group));
        }

        var tier2 = classified.Where(static x => x.Risk.Tier == CardRiskTier.UltraHighRisk).ToList();
        var tier1 = classified.Where(static x => x.Risk.Tier == CardRiskTier.Moderate).ToList();
        var tier0 = classified.Where(static x => x.Risk.Tier == CardRiskTier.Excluded).ToList();
        int activeTotal = tier2.Count + tier1.Count;

        Console.WriteLine("=== --dump-high-risk-pool (3-Tier High-Risk Test Pool Audit) ===");
        Console.WriteLine(
            $"Singleplayer Cards: total={allCards.Length} | " +
            $"Tier2(UltraHighRisk)={tier2.Count} ({(double)tier2.Count / Math.Max(1, allCards.Length):P1}) | " +
            $"Tier1(Moderate)={tier1.Count} ({(double)tier1.Count / Math.Max(1, allCards.Length):P1}) | " +
            $"Tier0(ExcludedNaive)={tier0.Count} ({(double)tier0.Count / Math.Max(1, allCards.Length):P1}) | " +
            $"ActiveHighRisk(T2+T1)={activeTotal} ({(double)activeTotal / Math.Max(1, allCards.Length):P1})");
        Console.WriteLine("Sampling Policy (--pool high-risk / --phase high-risk): 0% Tier0 | ~80% Tier2 (UltraHighRisk) | ~20% Tier1 (Moderate)");
        Console.WriteLine();

        Console.WriteLine("Breakdown by Character / Domain:");
        Console.WriteLine("| Group | Total | Tier 0 (Excluded) | Tier 1 (Moderate) | Tier 2 (UltraHighRisk) | Tier 2 % |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var grp in classified
            .GroupBy(static x => x.Group, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            int gTotal = grp.Count();
            int gT0 = grp.Count(static x => x.Risk.Tier == CardRiskTier.Excluded);
            int gT1 = grp.Count(static x => x.Risk.Tier == CardRiskTier.Moderate);
            int gT2 = grp.Count(static x => x.Risk.Tier == CardRiskTier.UltraHighRisk);
            Console.WriteLine($"| {grp.Key} | {gTotal} | {gT0} | {gT1} | {gT2} | {(double)gT2 / Math.Max(1, gTotal):P1} |");
        }
        Console.WriteLine();

        Console.WriteLine("Tier 0 (Excluded Naive) Reasons Summary:");
        foreach (var rg in tier0
            .GroupBy(static x => x.Risk.ReasonTag, StringComparer.Ordinal)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {rg.Key,-30} : {rg.Count(),3}");
        }
        Console.WriteLine();

        Console.WriteLine($"Full Tier 0 Excluded Naive Cards List ({tier0.Count}):");
        foreach (var item in tier0
            .OrderBy(static x => x.Group, StringComparer.Ordinal)
            .ThenBy(static x => x.Card.Id.Entry, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"  - [{item.Group,-11}] {item.Card.Id.Entry,-24} | type={item.Card.Type,-6} | cost={item.Card.EnergyCost.Canonical,2} | {item.Risk.ReasonTag}");
        }
        Console.WriteLine();

        Console.WriteLine($"Tier 2 (UltraHighRisk, {tier2.Count}) by Reason Category (with samples):");
        foreach (var rg in tier2
            .GroupBy(static x => SummarizeReasonCategory(x.Risk.ReasonTag), StringComparer.Ordinal)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            string samples = string.Join(", ", rg
                .OrderBy(static x => x.Card.Id.Entry, StringComparer.Ordinal)
                .Take(6)
                .Select(static x => $"{x.Card.Id.Entry}({x.Risk.ReasonTag})"));
            Console.WriteLine($"  {rg.Key,-24} ({rg.Count(),3}): {samples}");
        }
        Console.WriteLine();

        Console.WriteLine($"Tier 1 (Moderate, {tier1.Count}) by Reason Category (with samples):");
        foreach (var rg in tier1
            .GroupBy(static x => SummarizeReasonCategory(x.Risk.ReasonTag), StringComparer.Ordinal)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            string samples = string.Join(", ", rg
                .OrderBy(static x => x.Card.Id.Entry, StringComparer.Ordinal)
                .Take(6)
                .Select(static x => $"{x.Card.Id.Entry}({x.Risk.ReasonTag})"));
            Console.WriteLine($"  {rg.Key,-24} ({rg.Count(),3}): {samples}");
        }
        Console.WriteLine();

        // 遗物统计
        RelicModel[] allRelics = ModelDb.AllRelics
            .Where(static r => !HotCoverageAuditor.IsMockOrDeprecated(r) && GeneratedCombatScenario.IsSingleplayerRelic(r))
            .DistinctBy(static r => r.Id)
            .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        var keptRelics = allRelics.Where(IsHighRiskRelic).ToArray();
        var statefulRelics = keptRelics.Where(IsStatefulOrTrackedRelic).ToArray();
        Console.WriteLine(
            $"Singleplayer Relics: total={allRelics.Length} | kept_high_risk={keptRelics.Length} (stateful/tracked={statefulRelics.Length}) | dropped_passive_map={allRelics.Length - keptRelics.Length}");

        // 遭遇统计
        EncounterModel[] allEncounters = ModelDb.AllEncounters
            .Where(static e => !HotCoverageAuditor.IsMockOrDeprecated(e))
            .DistinctBy(static e => e.Id)
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        var classifiedEncs = allEncounters
            .Select(e => (Enc: e, Risk: ClassifyEncounter(e, includeEventEncounters: true)))
            .ToArray();
        int bosses = classifiedEncs.Count(static x => x.Risk.IsHighRisk && x.Risk.PriorityTier == 3);
        int elites = classifiedEncs.Count(static x => x.Risk.IsHighRisk && x.Risk.PriorityTier == 2);
        int complexNormals = classifiedEncs.Count(static x => x.Risk.IsHighRisk && x.Risk.PriorityTier == 1);
        int droppedEncs = classifiedEncs.Count(static x => !x.Risk.IsHighRisk);
        Console.WriteLine(
            $"Singleplayer Encounters: total={allEncounters.Length} | kept_high_risk={bosses + elites + complexNormals} (Boss={bosses}, Elite={elites}, ComplexNormal/Special={complexNormals}) | dropped_trivial={droppedEncs}");
        Console.WriteLine("Excluded Trivial/Event Encounters:");
        foreach (var item in classifiedEncs
            .Where(static x => !x.Risk.IsHighRisk)
            .OrderBy(static x => x.Enc.Id.Entry, StringComparer.Ordinal))
        {
            Console.WriteLine($"  - {item.Enc.Id.Entry,-36} | room={item.Enc.RoomType,-7} | weak={item.Enc.IsWeak,-5} | {item.Risk.ReasonTag}");
        }

        return 0;
    }

    private static string SummarizeReasonCategory(string reasonTag)
    {
        if (reasonTag.StartsWith("ultra_custom", StringComparison.Ordinal)
            || reasonTag.StartsWith("custom_", StringComparison.Ordinal)) return "ultra_custom_handler";
        if (reasonTag.StartsWith("ultra_power_", StringComparison.Ordinal)) return "ultra_power";
        if (reasonTag.StartsWith("ultra_op_", StringComparison.Ordinal)
            || reasonTag.StartsWith("op_", StringComparison.Ordinal)) return "ultra_op";
        if (reasonTag.StartsWith("ultra_keyword_", StringComparison.Ordinal)
            || reasonTag.StartsWith("upgraded_ultra_keyword_", StringComparison.Ordinal)) return "ultra_keyword";
        if (reasonTag.StartsWith("domain_", StringComparison.Ordinal)
            || reasonTag.StartsWith("card_type_", StringComparison.Ordinal)) return "special_domain";
        if (reasonTag.StartsWith("calc_var_", StringComparison.Ordinal)
            || reasonTag.StartsWith("calculated_var_", StringComparison.Ordinal)) return "calc_var";
        if (reasonTag.StartsWith("private_state_", StringComparison.Ordinal)
            || reasonTag.StartsWith("private_field_", StringComparison.Ordinal)) return "private_state";
        if (reasonTag.StartsWith("override_", StringComparison.Ordinal)) return "ultra_override";
        if (reasonTag.StartsWith("card_tag_", StringComparison.Ordinal)) return "special_card_tag";
        if (reasonTag.StartsWith("moderate_custom", StringComparison.Ordinal)) return "moderate_custom";
        if (reasonTag.StartsWith("moderate_power_", StringComparison.Ordinal)) return "moderate_power";
        if (reasonTag.StartsWith("moderate_op_", StringComparison.Ordinal)
            || reasonTag.StartsWith("moderate_can_heal", StringComparison.Ordinal)
            || reasonTag.StartsWith("moderate_heal", StringComparison.Ordinal)) return "moderate_hp_or_tempstat";
        if (reasonTag.StartsWith("moderate_keyword_", StringComparison.Ordinal)
            || reasonTag.StartsWith("upgraded_moderate_keyword_", StringComparison.Ordinal)) return "moderate_keyword";
        if (reasonTag.StartsWith("moderate_override_", StringComparison.Ordinal)) return "moderate_glow_override";
        if (reasonTag.StartsWith("moderate_var_", StringComparison.Ordinal)
            || reasonTag.StartsWith("upgraded_moderate_var_", StringComparison.Ordinal)) return "moderate_var";
        return reasonTag;
    }

    private static void TryAddPoolCardIds<TPool>(HashSet<ModelId> dst) where TPool : CardPoolModel
    {
        try
        {
            foreach (ModelId id in ModelDb.CardPool<TPool>().AllCardIds)
                dst.Add(id);
        }
        catch
        {
            // ignore if pool absent
        }
    }
}
