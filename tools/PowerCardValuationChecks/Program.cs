using CombatSolver;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

// 1. 基础数学与值对象合同。
PowerCardValuationReward reward = new(
    damage: int.MaxValue,
    prevention: 5,
    resource: 7,
    cardAccess: 11,
    scaling: 13,
    control: 17);
PowerCardValuationPenalty penalty = new(
    activationCost: 3,
    delayedPayoff: 5,
    triggerScarcity: 7,
    antiSynergy: 11,
    expirationLoss: 13);
Require(reward.Total == int.MaxValue, "奖励合计没有饱和。");
Require(penalty.Total == 39, "惩罚合计不正确。");
Require(new PowerCardValuationResult(
        new PowerCardValuationReward(damage: 10),
        new PowerCardValuationPenalty(activationCost: 14),
        PowerCardTiming.BeforeSkill).NetStrategicValue == -4,
    "净战略价值没有保留负向惩罚。");
RequireThrows<ArgumentOutOfRangeException>(() => new PowerCardValuationReward(damage: -1));
RequireThrows<ArgumentOutOfRangeException>(() => new PowerCardValuationPenalty(activationCost: -1));

// 2. 空登记表与未登记旁路。
PowerCardValuationRegistry empty = new([]);
Require(empty.Count == 0, "空登记表包含模型。");
Require(empty.RequirementsFor(typeof(TestPowerCard)) == PowerCardValuationRequirements.None,
    "未登记卡牌返回了需求。");
Require(!empty.TryEvaluate(new TestPowerCard(), default, out _),
    "未登记卡牌不应命中新接口。");
Require(!empty.TryGetCommitmentDescriptor("TEST_POWER_CARD", out _),
    "空登记表仍返回了能力承诺描述。");

// 3. 单模型登记表。
TestPowerCardModel model = new();
PowerCardValuationRegistry registry = new([model]);
Require(registry.Count == 1, "登记表没有保存模型。");
Require(registry.RequirementsFor(typeof(TestPowerCard)) ==
        (PowerCardValuationRequirements.CurrentTurnCards |
         PowerCardValuationRequirements.FutureCards),
    "登记需求不正确。");
PowerCardValuationContext context = Context();
Require(registry.TryEvaluate(new TestPowerCard(), in context, out PowerCardValuationResult result),
    "已登记卡牌没有命中模型。");
Require(result.Reward.CardAccess == 6 && result.Penalty.ActivationCost == 1,
    "模型没有收到统一上下文。");
Require(result.Timing == PowerCardTiming.BeforeSkill, "模型时机没有透传。");
Require(registry.RegisteredCardTypes(PowerCardPool.Silent).SequenceEqual([typeof(TestPowerCard)]),
    "角色卡池分类不正确。");
RequireThrows<InvalidOperationException>(() => new PowerCardValuationRegistry([model, model]));
Require(PowerCardValuationRegistry.CardIdFor(typeof(WellLaidPlans)) == "WELL_LAID_PLANS",
    "卡牌类型没有稳定转换为运行时 CardId。");
Require(PowerCardValuationRegistry.CardIdFor(typeof(BiasedCognition)) == "BIASED_COGNITION"
    && PowerCardValuationRegistry.CardIdFor(typeof(CreativeAi)) == "CREATIVE_AI"
    && PowerCardValuationRegistry.CardIdFor(typeof(TheSealedThrone)) == "THE_SEALED_THRONE"
    && PowerCardValuationRegistry.CardIdFor(typeof(TrashToTreasure)) == "TRASH_TO_TREASURE",
    "新卡池 CardId 推导不一致。");

// 4. 全池登记数量与唯一性。
PowerCardValuationRegistry all = PowerCardValuationModels.Registry;
Require(all.Count == 104, $"单人能力牌登记总数不正确：{all.Count}。");
Require(all.RegisteredCardTypes(PowerCardPool.Silent).Count == 17, "静默猎手登记数量不正确。");
Require(all.RegisteredCardTypes(PowerCardPool.Ironclad).Count == 19, "铁甲战士登记数量不正确。");
Require(all.RegisteredCardTypes(PowerCardPool.Defect).Count == 20, "故障机器人登记数量不正确。");
Require(all.RegisteredCardTypes(PowerCardPool.Regent).Count == 18, "储君登记数量不正确。");
Require(all.RegisteredCardTypes(PowerCardPool.Necrobinder).Count == 18, "亡灵契约师登记数量不正确。");
Require(all.RegisteredCardTypes(PowerCardPool.Colorless).Count == 12, "无色登记数量不正确。");

HashSet<string> seenIds = new(StringComparer.Ordinal);
HashSet<Type> seenTypes = [];
foreach (PowerCardPool pool in Enum.GetValues<PowerCardPool>())
{
    foreach (Type cardType in all.RegisteredCardTypes(pool))
    {
        Require(seenTypes.Add(cardType), $"{cardType.Name} 重复登记。");
        string cardId = PowerCardValuationRegistry.CardIdFor(cardType);
        Require(seenIds.Add(cardId), $"{cardId} 重复登记。");
        Require(all.TryGetPool(cardId, out PowerCardPool actualPool) && actualPool == pool,
            $"{cardId} 卡池登记不一致。");
        Require(all.ContainsCardId(cardId), $"{cardId} 未登记。");
    }
}

// 5. MultiplayerOnly 明确排除。
string[] multiplayerOnlyIds =
[
    "TANK", "SNEAKY", "ONE_FOR_ALL", "HAMMER_TIME", "CACOPHONY", "SOULBOUND", "BEACON_OF_HOPE",
];
foreach (string cardId in multiplayerOnlyIds)
    Require(!all.ContainsCardId(cardId), $"MultiplayerOnly 卡牌 {cardId} 不应登记。");
// WhiteNoise 是 CardType.Skill，不属能力牌模型范围。
Require(!all.ContainsCardId("WHITE_NOISE"), "WhiteNoise 不应登记为能力牌。");

// 6. 未登记 / 纯战后收益不创建承诺。
Require(!all.TryGetCommitmentDescriptor("NOT_A_REGISTERED_POWER", out _),
    "未登记卡牌创建了能力承诺描述。");
Require(all.ContainsCardId("ROYALTIES")
    && !all.TryGetCommitmentDescriptor("ROYALTIES", out _),
    "纯战后收益的 ROYALTIES 不应创建战斗内承诺。");
Require(all.ContainsCardId("FORBIDDEN_GRIMOIRE")
    && !all.TryGetCommitmentDescriptor("FORBIDDEN_GRIMOIRE", out _),
    "纯战后收益的 FORBIDDEN_GRIMOIRE 不应创建战斗内承诺。");

// 7. 静默猎手 17 张模型保持。
string[] silentIds =
[
    "ABRASIVE", "ACCELERANT", "ACCURACY", "AFTERIMAGE", "ENVENOM", "FAN_OF_KNIVES",
    "FOOTWORK", "INFINITE_BLADES", "MASTER_PLANNER", "NOXIOUS_FUMES", "PHANTOM_BLADES",
    "SERPENT_FORM", "SPEEDSTER", "TOOLS_OF_THE_TRADE", "TRACKING", "WELL_LAID_PLANS",
    "WRAITH_FORM",
];
foreach (string cardId in silentIds)
{
    Require(all.TryGetCommitmentDescriptor(cardId, out PowerCommitmentDescriptor descriptor)
        && descriptor.Family != PowerCommitmentFamily.None
        && descriptor.Pool == PowerCardPool.Silent,
        $"{cardId} 没有完整的静默猎手能力承诺描述符。");
}
Require(SilentPowerRoutePolicy.For("AFTERIMAGE").MinimumProjection == 5,
    "余像没有执行累计至少5点格挡的开牌阈值。");
Require(SilentPowerRoutePolicy.For("ENVENOM").RequireFreeOrSpareActivation
    && SilentPowerRoutePolicy.For("INFINITE_BLADES").RequireFreeOrSpareActivation,
    "低效率能力没有限制为免费或有余费启动。");
Require(SilentPowerRoutePolicy.For("WELL_LAID_PLANS").PreferDedicatedSearch,
    "计划妥当没有标记为专用路线搜索优先。");
Require(SilentPowerRoutePolicy.For("WRAITH_FORM").RequireImmediateDefenseGain,
    "幽魂形态没有要求当前防伤窗口，可能被过早保护。");
Require(SilentPowerRoutePolicy.FamilyFor("ABRASIVE") == PowerCommitmentFamily.DefenseEfficiency
    && SilentPowerRoutePolicy.FamilyFor("ACCURACY") == PowerCommitmentFamily.ShivEngine
    && SilentPowerRoutePolicy.FamilyFor("ACCELERANT") == PowerCommitmentFamily.PoisonEngine
    && SilentPowerRoutePolicy.FamilyFor("MASTER_PLANNER") == PowerCommitmentFamily.HandEngine
    && SilentPowerRoutePolicy.FamilyFor("SERPENT_FORM") == PowerCommitmentFamily.DamageEngine,
    "静默猎手机制族映射被改动。");

// 8. 每个卡池代表覆盖：防御/成长、资源/牌流、延迟收益、反协同/启动风险、专搜。
AssertFamily("INFLAME", PowerCommitmentFamily.StrengthGrowth, "铁甲战士力量成长");
AssertFamily("BARRICADE", PowerCommitmentFamily.DefenseEfficiency, "铁甲战士防御");
AssertFamily("PYRE", PowerCommitmentFamily.EnergyEngine, "铁甲战士能量");
AssertFamily("DARK_EMBRACE", PowerCommitmentFamily.ExhaustEngine, "铁甲战士消耗延迟收益");
AssertFamily("INFERNO", PowerCommitmentFamily.LifeInvestment, "铁甲战士生命投资反协同");
AssertFamily("COOLANT", PowerCommitmentFamily.OrbEngine, "故障机器人球防御");
AssertFamily("DEFRAGMENT", PowerCommitmentFamily.FocusEngine, "故障机器人集中成长");
AssertFamily("MACHINE_LEARNING", PowerCommitmentFamily.HandEngine, "故障机器人抽牌");
AssertFamily("CREATIVE_AI", PowerCommitmentFamily.CardGenerationEngine, "故障机器人延迟生成");
AssertFamily("BULK_UP", PowerCommitmentFamily.DexterityGrowth, "故障机器人启动风险");
AssertFamily("GENESIS", PowerCommitmentFamily.StarEngine, "储君星星资源");
AssertFamily("NEUTRON_AEGIS", PowerCommitmentFamily.DefenseEfficiency, "储君防御");
AssertFamily("FURNACE", PowerCommitmentFamily.StarEngine, "储君铸造延迟收益");
AssertFamily("VOID_FORM", PowerCommitmentFamily.CostReductionEngine, "储君费用引擎");
AssertFamily("COUNTDOWN", PowerCommitmentFamily.DoomEngine, "亡灵契约师灾厄延迟收益");
AssertFamily("DEMESNE", PowerCommitmentFamily.EnergyEngine, "亡灵契约师能量");
AssertFamily("SHROUD", PowerCommitmentFamily.BlockTriggerEngine, "亡灵契约师触发防御");
AssertFamily("NEUROSURGE", PowerCommitmentFamily.LifeInvestment, "亡灵契约师生命投资");
AssertFamily("ETERNAL_ARMOR", PowerCommitmentFamily.DefenseEfficiency, "无色防御");
AssertFamily("AUTOMATION", PowerCommitmentFamily.EnergyEngine, "无色能量");
AssertFamily("MAYHEM", PowerCommitmentFamily.AutoPlayEngine, "无色延迟自动出牌");
AssertFamily("PANACHE", PowerCommitmentFamily.DamageEngine, "无色延迟伤害");
Require(all.TryGetCommitmentDescriptor("CORRUPTION", out PowerCommitmentDescriptor corruption)
    && corruption.Admission.PreferDedicatedSearch,
    "铁甲战士缺少需要专搜的能力。");
Require(all.TryGetCommitmentDescriptor("ECHO_FORM", out PowerCommitmentDescriptor echo)
    && echo.Admission.PreferDedicatedSearch,
    "故障机器人缺少需要专搜的能力。");
Require(all.TryGetCommitmentDescriptor("VOID_FORM", out PowerCommitmentDescriptor voidForm)
    && voidForm.Admission.PreferDedicatedSearch,
    "储君缺少需要专搜的能力。");
Require(all.TryGetCommitmentDescriptor("REAPER_FORM", out PowerCommitmentDescriptor reaper)
    && reaper.Admission.PreferDedicatedSearch,
    "亡灵契约师缺少需要专搜的能力。");
Require(all.TryGetCommitmentDescriptor("PANACHE", out PowerCommitmentDescriptor panache)
    && !panache.Admission.PreferDedicatedSearch,
    "神气制胜按玩家复核不应再占用专搜席位。");
Require(all.TryGetCommitmentDescriptor("AUTOMATION", out PowerCommitmentDescriptor automation)
    && automation.Admission.PreferDedicatedSearch
    && automation.Admission.Priority == PowerRoutePriority.Core,
    "自动化按玩家复核应取得核心优先级与专搜席位。");
Require(all.TryGetCommitmentDescriptor("BARRICADE", out PowerCommitmentDescriptor barricade)
    && barricade.Admission.PreferDedicatedSearch
    && barricade.Admission.Priority == PowerRoutePriority.Strong,
    "壁垒按玩家复核应取得专搜标记，但保留原优先级以避免劣化选路。");
Require(all.TryGetCommitmentDescriptor("DARK_EMBRACE", out PowerCommitmentDescriptor darkEmbrace)
    && darkEmbrace.Admission.PreferDedicatedSearch,
    "黑暗之拥按玩家复核应取得专搜席位。");
Require(all.TryGetCommitmentDescriptor("VICIOUS", out PowerCommitmentDescriptor vicious)
    && vicious.Admission.Priority == PowerRoutePriority.Core,
    "凶恶按玩家复核应提升为核心优先级。");
Require(all.TryGetCommitmentDescriptor("CONSUMING_SHADOW", out PowerCommitmentDescriptor consuming)
    && consuming.Admission.Priority == PowerRoutePriority.Low
    && consuming.Admission.RequireFreeOrSpareActivation,
    "吞噬暗影按玩家复核应降为低优先级且仅在免费或有余费时开启。");
Require(all.TryGetCommitmentDescriptor("COOLANT", out PowerCommitmentDescriptor coolant)
    && coolant.Admission.Priority == PowerRoutePriority.Low
    && coolant.Admission.RequireFreeOrSpareActivation,
    "冷却剂按玩家复核应降为低优先级且不卖血开启。");
Require(all.TryGetCommitmentDescriptor("ORBIT", out PowerCommitmentDescriptor orbit)
    && orbit.Admission.PreferDedicatedSearch
    && orbit.Admission.Priority == PowerRoutePriority.Core,
    "环绕轨道按玩家复核应提升优先级并取得专搜席位。");
Require(all.TryGetCommitmentDescriptor("FURNACE", out PowerCommitmentDescriptor furnace)
    && !furnace.Admission.PreferDedicatedSearch,
    "熔炉按玩家复核不应占用专搜席位。");

// 9. 路线准入：零触发拒绝、阈值、免费与高费硬开、当前/未来窗口。
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "IRONCLAD_TEST",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: false,
        ImmediateDefenseGain: 0,
        SetupGain: 10,
        ProjectedPotential: 10,
        TriggerProjectionFloor: 0,
        Investment: 8),
    new PowerRouteAdmissionPolicy(PowerRoutePriority.Core)).Admitted,
    "零触发证据仍取得了能力承诺。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "ABRASIVE",
        IsAutoPlay: false,
        SpentEnergy: 3,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 10,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 24),
    SilentPowerRoutePolicy.For("ABRASIVE")).Admitted
    && PowerRouteAdmission.Evaluate(new(
        CardId: "ABRASIVE",
        IsAutoPlay: true,
        SpentEnergy: 0,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 10,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 0),
    SilentPowerRoutePolicy.For("ABRASIVE")).Admitted,
    "磨蚀没有区分3费硬开与奇巧免费开。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "AFTERIMAGE",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 4,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 4,
        Investment: 8),
    SilentPowerRoutePolicy.For("AFTERIMAGE")).Admitted
    && PowerRouteAdmission.Evaluate(new(
        CardId: "AFTERIMAGE",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 5,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 5,
        Investment: 8),
    SilentPowerRoutePolicy.For("AFTERIMAGE")).Admitted,
    "余像没有执行累计5点格挡的路线准入边界。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "WRAITH_FORM",
        IsAutoPlay: false,
        SpentEnergy: 3,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 30,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 24),
    SilentPowerRoutePolicy.For("WRAITH_FORM")).Admitted,
    "幽魂形态在没有当前防伤窗口时仍被过早保护。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "MASTER_PLANNER",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 20,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 8),
    SilentPowerRoutePolicy.For("MASTER_PLANNER")).Admitted,
    "谋划专家在没有重新入手与弃牌兑现链时仍取得了承诺。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "RUPTURE",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 8,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 8),
    IroncladPolicy("RUPTURE")).Admitted
    && PowerRouteAdmission.Evaluate(new(
        CardId: "RUPTURE",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 8,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 8),
    IroncladPolicy("RUPTURE")).Admitted,
    "撕裂没有限制为免费或支付后仍有余费启动。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "ROYALTIES",
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 30,
        ProjectedPotential: 30,
        TriggerProjectionFloor: 0,
        Investment: 8),
    RegentPolicy("ROYALTIES")).Admitted,
    "纯战后收益的 ROYALTIES 仍创建了战斗内承诺。");
Require(!PowerRouteAdmission.Evaluate(new(
        CardId: "BUFFER",
        IsAutoPlay: false,
        SpentEnergy: 2,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 20,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 16),
    DefectPolicy("BUFFER")).Admitted,
    "缓冲在没有当前防伤窗口时仍被过早保护。");
Require(PowerActivationInvestmentPolicy.EnergyInvestment(1, totalFloor: 10, combatTurnOffset: 0) == 8
    && PowerActivationInvestmentPolicy.EnergyInvestment(1, totalFloor: 20, combatTurnOffset: 0) == 5
    && PowerActivationInvestmentPolicy.EnergyInvestment(1, totalFloor: 33, combatTurnOffset: 0) == 3,
    "能力启动投资没有随楼层推进降低。");
Require(PowerActivationInvestmentPolicy.EnergyInvestment(1, totalFloor: 33, combatTurnOffset: 2) == 8,
    "楼层先验错误降低了战斗中后段的能力启动投资。");

// 10. 单能力与双能力承诺：家族 OR、优先级取高、卡牌去重。
PowerCommitmentDescriptor inflameDescriptor = Descriptor("INFLAME");
PowerCommitment commitment = PowerCommitmentLifecycle.Create(
    inflameDescriptor,
    turn: 1,
    actionCount: 1,
    historyEntryCount: 10,
    investment: 8,
    provisionalPotential: 12);
Require(commitment.Cards.Count == 1 && commitment.HasCard("INFLAME")
    && commitment.Priority == PowerRoutePriority.Core,
    "单能力承诺创建不正确。");
PowerCommitment combined = PowerCommitmentLifecycle.AddPower(
    commitment,
    Descriptor("PYRE"),
    investment: 8,
    provisionalPotential: 12,
    progressEvidence: 0,
    realizedEvidence: 0,
    turn: 1);
Require(combined.Cards.Count == 2
    && combined.HasCard("INFLAME") && combined.HasCard("PYRE")
    && combined.Family.HasFlag(PowerCommitmentFamily.StrengthGrowth)
    && combined.Family.HasFlag(PowerCommitmentFamily.EnergyEngine)
    && combined.PowerCardsPlayed == 2,
    "双能力承诺没有合并家族与卡牌身份。");
PowerCommitment deduped = PowerCommitmentLifecycle.AddPower(
    combined,
    Descriptor("INFLAME"),
    investment: 0,
    provisionalPotential: 4,
    progressEvidence: 1,
    realizedEvidence: 0,
    turn: 2);
Require(deduped.Cards.Count == 2, "重复能力没有去重卡牌身份。");
PowerCommitmentAdvanceResult progressed = PowerCommitmentLifecycle.Advance(
    commitment,
    parentTurn: 1,
    childTurn: 1,
    maximumTransitions: 2,
    progressEvidence: 5,
    realizedEvidence: 0,
    terminal: false);
Require(progressed.Disposition == PowerCommitmentDisposition.Active
    && progressed.Commitment is { ProgressEvidence: 5, ProvisionalPotential: 12 },
    "中间证据错误消耗了尚未兑现的能力潜力。");
PowerCommitmentAdvanceResult realized = PowerCommitmentLifecycle.Advance(
    progressed.Commitment!,
    parentTurn: 1,
    childTurn: 2,
    maximumTransitions: 2,
    progressEvidence: 0,
    realizedEvidence: 12,
    terminal: false);
Require(realized.Disposition == PowerCommitmentDisposition.Realized
    && realized.Commitment == null,
    "真实收益出现后能力承诺没有退出。");
PowerCommitmentAdvanceResult expired = PowerCommitmentLifecycle.Advance(
    commitment,
    parentTurn: 1,
    childTurn: 4,
    maximumTransitions: 2,
    progressEvidence: 0,
    realizedEvidence: 0,
    terminal: false);
Require(expired.Disposition == PowerCommitmentDisposition.Expired
    && expired.Commitment == null,
    "能力承诺越过回合上限后没有到期。");
Require(PowerCommitmentSeatPolicy.SeatQuota(24, aggressive: false)
        < PowerCommitmentSeatPolicy.SeatQuota(24, aggressive: true),
    "能力偏好成员没有获得更高的承诺席位。");

// 11. 逐卡估值合同：升级差异、零触发稀缺、机制取值。
PowerCardValuationResult inflameNormal = Evaluate(new Inflame(), Context());
PowerCardValuationResult inflameUpgraded = Evaluate(new Inflame { IsUpgraded = true }, Context());
Require(inflameNormal.Reward.Scaling == 10 && inflameUpgraded.Reward.Scaling == 15,
    "燃烧没有按力量成长与攻击次数计价。");
PowerCardValuationResult inflameIdle = Evaluate(
    new Inflame(),
    Context(current: new(), future: new()));
Require(inflameIdle.Penalty.TriggerScarcity == 0,
    "燃烧的攻击次数为0时不应以触发稀缺否定力量成长。");

PowerCardValuationResult countdown = Evaluate(new Countdown(), Context());
Require(countdown.Reward.Damage == 12 && countdown.Penalty.DelayedPayoff > 0,
    "倒数计时没有按未来回合开始次数的灾厄估值。");
PowerCardValuationResult countdownUpgraded = Evaluate(
    new Countdown { IsUpgraded = true },
    Context());
Require(countdownUpgraded.Reward.Damage == 18, "倒数计时升级数值不正确。");

PowerCardValuationResult hailstormIdle = Evaluate(
    new Hailstorm(),
    Context(frostOrbs: 0));
Require(hailstormIdle.Reward.Damage == 0 && hailstormIdle.Penalty.TriggerScarcity > 0,
    "没有冰霜球时冰雹风暴仍虚构了伤害。");

PowerCardValuationResult prowess = Evaluate(new Prowess(), Context());
PowerCardValuationResult prowessUpgraded = Evaluate(
    new Prowess { IsUpgraded = true },
    Context());
Require(prowess.Reward.Scaling == 10 && prowessUpgraded.Reward.Scaling == 20,
    "非凡技艺没有按力量与敏捷共同兑现计价。");

PowerCardValuationResult royalties = Evaluate(new Royalties(), Context());
Require(royalties.Reward.Scaling == 30, "王国资产没有按战后金币估值。");

PowerCardValuationResult defragment = Evaluate(new Defragment(), Context(orbCount: 2));
Require(defragment.Reward.Scaling == 6, "碎片整理没有按集中与球数计价。");

// 11b. 生产投影公式的纯合同（这些公式被 solver partial 调用）。
Require(PowerCardProjectionMath.AutomationPayouts(drawsPerTurn: 5, turns: 3) == 1
    && PowerCardProjectionMath.AutomationPayouts(drawsPerTurn: 10, turns: 2) == 2
    && PowerCardProjectionMath.AutomationPayouts(drawsPerTurn: 4, turns: 2) == 0,
    "自动化没有按实际抽牌量折算每10抽返能。");
Require(PowerCardProjectionMath.OrbitPayouts(maxEnergy: 3, turns: 3) == 2
    && PowerCardProjectionMath.OrbitPayouts(maxEnergy: 4, turns: 1) == 1,
    "环绕轨道没有按每回合最大能量折算每4费返能。");
Require(PowerCardProjectionMath.ChildOfTheStarsTriggers(stars: 5, starSpendCapacity: 2) == 2
    && PowerCardProjectionMath.ChildOfTheStarsTriggers(stars: 0, starSpendCapacity: 3) == 0
    && PowerCardProjectionMath.ChildOfTheStarsTriggers(stars: 5, starSpendCapacity: 5) == 5
    && PowerCardProjectionMath.ChildOfTheStarsBlock(amountPerStar: 3, triggers: 5) == 15,
    "群星之子应按实际可花费星能点数（而非牌张数）线性换算格挡，一张5星牌应计5点。");
Require(PowerCardProjectionMath.ChildOfTheStarsBlock(3, 4)
        == 2 * PowerCardProjectionMath.ChildOfTheStarsBlock(3, 2),
    "群星之子格挡应随触发次数线性增长，而不是平方级。");
Require(PowerCardProjectionMath.BufferPrevention(charges: 1, incomingDamage: 15, incomingHits: 3) == 5
    && PowerCardProjectionMath.BufferPrevention(charges: 2, incomingDamage: 15, incomingHits: 3) == 10
    && PowerCardProjectionMath.BufferPrevention(charges: 1, incomingDamage: 0, incomingHits: 3) == 0,
    "缓冲没有按多段攻击的每次伤害折算。");
Require(PowerCardProjectionMath.ViciousDrawTriggers(vulnerableSources: 3) == 3,
    "凶恶没有按易伤来源数计触发，而不是按敌人数放大。");
RequireThrows<ArgumentOutOfRangeException>(
    () => PowerCardProjectionMath.AutomationPayouts(-1, 1));
RequireThrows<ArgumentOutOfRangeException>(
    () => PowerCardProjectionMath.BufferPrevention(-1, 1, 1));

// 12. 存在性：无已登记能力时不增加组合成员。
Require(!all.ContainsCardId("STRIKE_IRONCLAD") && !all.ContainsCardId("DEFEND_SILENT"),
    "普通牌被错误登记为能力牌。");
Require(Enum.GetValues<PowerCardPool>().All(pool =>
        all.RegisteredCardIds(pool).All(all.ContainsCardId)),
    "存在已登记但不被识别的卡牌 ID。");

Console.WriteLine(
    "POWER_CARD_VALUATION_CHECKS_OK total=104 silent=17 ironclad=19 defect=20 regent=18 necrobinder=18 colorless=12");

PowerCardValuationResult Evaluate(CardModel card, PowerCardValuationContext evaluationContext)
{
    Require(all.TryEvaluate(card, in evaluationContext, out PowerCardValuationResult evaluation),
        $"{card.GetType().Name} 没有命中估值模型。");
    return evaluation;
}

void AssertFamily(string cardId, PowerCommitmentFamily family, string description)
{
    Require(all.TryGetCommitmentDescriptor(cardId, out PowerCommitmentDescriptor descriptor),
        $"{description}（{cardId}）没有承诺描述。");
    Require(descriptor.Family.HasFlag(family), $"{description}（{cardId}）机制族缺失。");
}

PowerCommitmentDescriptor Descriptor(string cardId)
{
    Require(all.TryGetCommitmentDescriptor(cardId, out PowerCommitmentDescriptor descriptor),
        $"{cardId} 没有承诺描述。");
    return descriptor;
}

PowerRouteAdmissionPolicy IroncladPolicy(string cardId) => IroncladPowerRoutePolicy.For(cardId);
PowerRouteAdmissionPolicy DefectPolicy(string cardId) => DefectPowerRoutePolicy.For(cardId);
PowerRouteAdmissionPolicy RegentPolicy(string cardId) => RegentPowerRoutePolicy.For(cardId);

static PowerCardValuationContext Context(
    int effectiveEnergyCost = 1,
    int nextTurnIncomingDamage = 12,
    int nextTurnIncomingHitCount = 2,
    int followingTurnIncomingDamage = 9,
    int followingTurnIncomingHitCount = 1,
    int dexterityLossValue = 4,
    int orbCount = 0,
    int frostOrbs = 0,
    int lightningOrbs = 0,
    int darkOrbs = 0,
    int distinctOrbTypes = 0,
    PowerCardTurnProjection? current = null,
    PowerCardTurnProjection? future = null)
    => new(
        EnemyHp: 500,
        EnemyCount: 2,
        RemainingTurns: 3,
        CurrentEnergy: 3,
        CurrentStars: 0,
        EffectiveEnergyCost: effectiveEnergyCost,
        EffectiveStarCost: 0,
        AverageCardValue: 8,
        BestCardValue: 12,
        ShivDamage: 6,
        ShivTargetsPerPlay: 1,
        PoisonStackValue: 2,
        PoisonTriggerDamage: 9,
        RetainedHandValue: 15,
        SlyCardValue: 10,
        DiscardPayoffValue: 3,
        NextTurnIncomingDamage: nextTurnIncomingDamage,
        NextTurnIncomingHitCount: nextTurnIncomingHitCount,
        FollowingTurnIncomingDamage: followingTurnIncomingDamage,
        FollowingTurnIncomingHitCount: followingTurnIncomingHitCount,
        DexterityLossValue: dexterityLossValue,
        CurrentTurn: current ?? new PowerCardTurnProjection(
            UsefulCardPlays: 4,
            AttackPlays: 2,
            UnblockedAttackHits: 3,
            SkillPlays: 2,
            BlockSkillPlays: 2,
            PowerPlays: 0,
            Exhausts: 0,
            ShivPlays: 2,
            DrawsAfterOpening: 2,
            Discards: 1,
            WeakTargetAttackDamage: 20,
            IncomingDamage: 15,
            IncomingHitCount: 3),
        Future: future ?? new PowerCardTurnProjection(
            UsefulCardPlays: 6,
            AttackPlays: 3,
            UnblockedAttackHits: 5,
            SkillPlays: 4,
            BlockSkillPlays: 3,
            PowerPlays: 0,
            Exhausts: 0,
            ShivPlays: 4,
            DrawsAfterOpening: 3,
            Discards: 3,
            WeakTargetAttackDamage: 40,
            IncomingDamage: 20,
            IncomingHitCount: 4),
        OrbCount: orbCount,
        DistinctOrbTypes: distinctOrbTypes,
        FrostOrbs: frostOrbs,
        LightningOrbs: lightningOrbs,
        DarkOrbs: darkOrbs);

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RequireThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"预期异常 {typeof(TException).Name} 没有抛出。");
}

internal sealed class TestPowerCard : CardModel;

internal sealed class TestPowerCardModel : PowerCardValuationModel<TestPowerCard>
{
    public override PowerCardPool Pool => PowerCardPool.Silent;
    public override PowerCardValuationRequirements Requirements =>
        PowerCardValuationRequirements.CurrentTurnCards |
        PowerCardValuationRequirements.FutureCards;

    protected override PowerCardValuationResult Evaluate(
        TestPowerCard card,
        in PowerCardValuationContext context)
        => new(
            new PowerCardValuationReward(cardAccess:
                context.CurrentTurn.SkillPlays + context.Future.SkillPlays),
            new PowerCardValuationPenalty(activationCost:
                context.EffectiveEnergyCost + context.EffectiveStarCost),
            PowerCardTiming.BeforeSkill);
}
