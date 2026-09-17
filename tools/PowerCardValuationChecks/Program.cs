using CombatSolver;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

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

PowerCardValuationRegistry empty = new([]);
Require(empty.Count == 0, "空登记表包含模型。");
Require(empty.RequirementsFor(typeof(TestPowerCard)) == PowerCardValuationRequirements.None,
    "未登记卡牌返回了需求。");
Require(!empty.TryEvaluate(new TestPowerCard(), default, out _),
    "未登记卡牌不应命中新接口。");

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
Require(result.Timing == PowerCardTiming.BeforeSkill,
    "模型时机没有透传。");
Require(registry.RegisteredCardTypes(PowerCardPool.Silent).SequenceEqual([typeof(TestPowerCard)]),
    "角色卡池分类不正确。");
RequireThrows<InvalidOperationException>(() => new PowerCardValuationRegistry([model, model]));
Require(PowerCardValuationRegistry.CardIdFor(typeof(WellLaidPlans)) == "WELL_LAID_PLANS",
    "卡牌类型没有稳定转换为运行时 CardId。");
PowerCardValuationRegistry silent = PowerCardValuationModels.Registry;
Require(silent.Count == 17, "静默猎手单人能力牌没有完整登记。");
Require(silent.RegisteredCardTypes(PowerCardPool.Silent).Count == 17,
    "静默猎手卡池登记数量不正确。");
Require(Enum.GetValues<PowerCardPool>()
        .Where(pool => pool != PowerCardPool.Silent)
        .All(pool => silent.RegisteredCardTypes(pool).Count == 0),
    "首批模型越过了静默猎手卡池边界。");
foreach (Type cardType in silent.RegisteredCardTypes(PowerCardPool.Silent))
{
    string cardId = PowerCardValuationRegistry.CardIdFor(cardType);
    Require(silent.TryGetCommitmentDescriptor(cardId, out PowerCommitmentDescriptor descriptor)
        && descriptor.Family != PowerCommitmentFamily.None
        && descriptor.Card != SilentPowerCardIdentity.None,
        $"{cardId} 没有完整的能力承诺描述符。");
}
foreach (SilentPowerCardIdentity card in Enum.GetValues<SilentPowerCardIdentity>()
             .Where(card => card != SilentPowerCardIdentity.None))
{
    _ = SilentPowerRoutePolicy.For(card);
}
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.Abrasive).PreferSlyActivation,
    "磨蚀没有优先使用奇巧免费启动。");
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.Afterimage).MinimumProjection == 5,
    "余像没有执行累计至少5点格挡的开牌阈值。");
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.Envenom).RequireFreeOrSpareActivation
    && SilentPowerRoutePolicy.For(SilentPowerCardIdentity.InfiniteBlades).RequireFreeOrSpareActivation,
    "低效率能力没有限制为免费或有余费启动。");
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.WellLaidPlans).PreferDedicatedSearch,
    "计划妥当没有标记为专用路线搜索优先。");
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.WellLaidPlans).Priority
        == SilentPowerRoutePriority.Dedicated,
    "计划妥当没有取得高于普通强能力的专搜保留优先级。");
Require(SilentPowerRoutePolicy.For(SilentPowerCardIdentity.WraithForm).RequireImmediateDefenseGain,
    "幽魂形态没有要求当前防伤窗口，可能被过早保护。");
Require(SilentPowerRoutePolicy.HighestPriority(
        SilentPowerCardIdentity.ToolsOfTheTrade | SilentPowerCardIdentity.Envenom)
        == SilentPowerRoutePriority.Core,
    "多能力承诺没有保留最强牌的路线优先级。");
Require(!SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.Abrasive,
        IsAutoPlay: false,
        SpentEnergy: 3,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 10,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 24)).Admitted
    && SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.Abrasive,
        IsAutoPlay: true,
        SpentEnergy: 0,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 10,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 0)).Admitted,
    "磨蚀没有区分3费硬开与奇巧免费开。");
Require(!SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.Afterimage,
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 4,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 4,
        Investment: 8)).Admitted
    && SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.Afterimage,
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 5,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 5,
        Investment: 8)).Admitted,
    "余像没有执行累计5点格挡的路线准入边界。");
Require(!SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.Envenom,
        IsAutoPlay: false,
        SpentEnergy: 2,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 8,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 16)).Admitted,
    "涂毒在耗尽能量时仍取得了专用路线保护。");
Require(!SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.WraithForm,
        IsAutoPlay: false,
        SpentEnergy: 3,
        RemainingEnergy: 0,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 30,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 24)).Admitted,
    "幽魂形态在没有当前防伤窗口时仍被过早保护。");
Require(!SilentWraithOpeningWindow.ShouldProtect(
        remainingTurns: 5,
        intangibleTurns: 2,
        projectedHpBeforeOpening: 20)
    && SilentWraithOpeningWindow.ShouldProtect(
        remainingTurns: 3,
        intangibleTurns: 2,
        projectedHpBeforeOpening: 20)
    && SilentWraithOpeningWindow.ShouldProtect(
        remainingTurns: 5,
        intangibleTurns: 2,
        projectedHpBeforeOpening: 0),
    "幽魂形态没有区分长线过早启动、覆盖战斗尾段和致死救场。");
Require(!SilentPowerRouteAdmission.Evaluate(new(
        Card: SilentPowerCardIdentity.MasterPlanner,
        IsAutoPlay: false,
        SpentEnergy: 1,
        RemainingEnergy: 2,
        HasTriggerEvidence: true,
        ImmediateDefenseGain: 0,
        SetupGain: 20,
        ProjectedPotential: 0,
        TriggerProjectionFloor: 0,
        Investment: 8)).Admitted,
    "谋划专家在没有重新入手与弃牌兑现链时仍取得了承诺。");

PowerCardValuationResult masterPlanner = Evaluate(silent, new MasterPlanner(), Context());
Require(masterPlanner.Reward.CardAccess == 40,
    "谋划专家没有按可奇巧技能与弃牌窗口的交集计价。");
Require(masterPlanner.Penalty.ActivationCost == 8 && masterPlanner.Penalty.DelayedPayoff == 4,
    "谋划专家没有计入启动与延迟兑现成本。");
Require(masterPlanner.Timing.HasFlag(PowerCardTiming.BeforeSkill) &&
        masterPlanner.Timing.HasFlag(PowerCardTiming.BeforeDiscard),
    "谋划专家没有要求先于技能和弃牌窗口。");

PowerCardValuationResult accuracy = Evaluate(silent, new Accuracy(), Context());
PowerCardValuationResult upgradedAccuracy = Evaluate(
    silent,
    new Accuracy { IsUpgraded = true },
    Context());
Require(accuracy.Reward.Damage == 24 && upgradedAccuracy.Reward.Damage == 36,
    "精准的普通与升级小刀增伤不正确。");
PowerCardValuationResult idleAccuracy = Evaluate(
    silent,
    new Accuracy(),
    Context(current: new(), future: new()));
Require(idleAccuracy.Reward.Damage == 0 && idleAccuracy.Penalty.TriggerScarcity == 8,
    "精准在没有小刀时没有受到触发稀缺惩罚。");

PowerCardValuationResult fan = Evaluate(silent, new FanOfKnives(), Context());
Require(fan.Reward.Damage == 84,
    "刀扇没有合计生成小刀伤害与现有小刀的群攻增量。");

PowerCardValuationResult noxious = Evaluate(silent, new NoxiousFumes(), Context());
Require(noxious.Reward.Damage == 16 && noxious.Penalty.DelayedPayoff == 4,
    "毒雾没有按未来回合开始次数和敌人数估值。");

Require(Evaluate(silent, new Abrasive(), Context()).Reward ==
        new PowerCardValuationReward(damage: 28, prevention: 5),
    "磨蚀没有合计荆棘与敏捷收益。");
Require(Evaluate(silent, new Accelerant(), Context()).Reward.Damage == 9,
    "触媒没有按额外中毒触发伤害估值。");
Require(Evaluate(silent, new Afterimage(), Context()).Reward.Prevention == 10,
    "余像没有按后续出牌数估值。");
Require(Evaluate(silent, new Envenom(), Context()).Reward.Damage == 16,
    "涂毒没有按未格挡攻击命中估值。");
Require(Evaluate(silent, new Footwork(), Context()).Reward.Prevention == 10,
    "灵动步法没有按后续格挡技能估值。");
Require(Evaluate(silent, new InfiniteBlades(), Context()).Reward.Damage == 12,
    "无尽刀刃没有按未来回合开始次数估值。");
Require(Evaluate(silent, new PhantomBlades(), Context()).Reward.Damage == 27,
    "幻影之刃没有限制每回合第一张小刀的触发次数。");
Require(Evaluate(silent, new SerpentForm(), Context()).Reward.Damage == 40,
    "群蛇形态没有按后续出牌数估值。");
Require(Evaluate(silent, new Speedster(), Context()).Reward.Damage == 20,
    "速行者没有按回合内抽牌与敌人数估值。");
Require(Evaluate(silent, new ToolsOfTheTrade(), Context()).Reward.CardAccess == 22,
    "必备工具没有按未来回合的选牌和弃牌收益估值。");
Require(Evaluate(silent, new Tracking(), Context()).Reward.Damage == 30,
    "跟踪没有按虚弱目标攻击伤害的50%估值。");
Require(Evaluate(silent, new WellLaidPlans(), Context()).Reward.CardAccess == 15,
    "计划妥当没有使用整手牌保留价值。");

PowerCardValuationContext wraithContext = Context(
    effectiveEnergyCost: 3,
    nextTurnIncomingDamage: 12,
    nextTurnIncomingHitCount: 2,
    followingTurnIncomingDamage: 9,
    followingTurnIncomingHitCount: 1,
    dexterityLossValue: 4);
PowerCardValuationResult wraith = Evaluate(silent, new WraithForm(), wraithContext);
PowerCardValuationResult upgradedWraith = Evaluate(
    silent,
    new WraithForm { IsUpgraded = true },
    wraithContext);
Require(wraith.Reward.Prevention == 22 && upgradedWraith.Reward.Prevention == 30,
    "幽魂形态没有按无实体覆盖的逐次伤害估值。");
Require(wraith.Penalty.AntiSynergy == 4,
    "幽魂形态没有计入敏捷流失代价。");

PowerTurnCardOption[] thresholdHand =
[
    new(EnergyCost: 1, Damage: 0, Block: 5),
    new(EnergyCost: 1, Damage: 0, Block: 5),
    new(EnergyCost: 1, Damage: 9, Block: 0),
];
IReadOnlyList<PowerTurnFrontierState> baselineFrontier = PowerTurnFrontier.Build(
    energy: 2,
    incomingDamage: 8,
    thresholdHand);
IReadOnlyList<PowerTurnFrontierState> footworkFrontier = PowerTurnFrontier.Build(
    energy: 2,
    incomingDamage: 8,
    thresholdHand,
    blockPerSkillBonus: 3);
Require(PowerTurnFrontier.DefensiveDamageUplift(baselineFrontier, footworkFrontier) == 9,
    "灵动步法跨过格挡阈值后没有把省下的能量转成输出。");
Require(PowerTurnFrontier.DefensiveHpUplift(baselineFrontier, footworkFrontier) == 3,
    "灵动步法没有量化同等输出下减少的战损。");
IReadOnlyList<PowerTurnFrontierState> noThresholdFrontier = PowerTurnFrontier.Build(
    energy: 2,
    incomingDamage: 9,
    thresholdHand,
    blockPerSkillBonus: 2);
Require(noThresholdFrontier.Where(state => state.HpLost == 0).Max(state => state.Damage) == 0,
    "未跨过同一防伤阈值时虚构了能量转化收益。");
PowerTurnCardOption[] cardTriggerHand =
[
    new(EnergyCost: 1, Damage: 0, Block: 5),
    new(EnergyCost: 1, Damage: 9, Block: 0),
];
IReadOnlyList<PowerTurnFrontierState> noAfterimage = PowerTurnFrontier.Build(
    2, 8, cardTriggerHand);
IReadOnlyList<PowerTurnFrontierState> withAfterimage = PowerTurnFrontier.Build(
    2, 8, cardTriggerHand, blockPerCardBonus: 1);
Require(PowerTurnFrontier.DefensiveHpUplift(noAfterimage, withAfterimage) == 2,
    "余像没有按实际出牌序列改变同等输出下的格挡阈值。");
PowerTurnCardOption[] drawHand =
[
    new(EnergyCost: 1, Damage: 0, Block: 0, CardAccess: 2, Draws: 2),
    new(EnergyCost: 1, Damage: 9, Block: 0),
];
IReadOnlyList<PowerTurnFrontierState> noSpeedster = PowerTurnFrontier.Build(
    2, 0, drawHand);
IReadOnlyList<PowerTurnFrontierState> withSpeedster = PowerTurnFrontier.Build(
    2, 0, drawHand, damagePerDraw: 2, damageTargets: 2);
Require(PowerTurnFrontier.DefensiveDamageUplift(noSpeedster, withSpeedster) == 8,
    "速行者没有把实际回合内抽牌转成多目标伤害前沿。");
PowerTurnCardOption[] shivHand =
[
    new(EnergyCost: 0, Damage: 6, Block: 0, IsShiv: true),
    new(EnergyCost: 0, Damage: 6, Block: 0, IsShiv: true),
];
IReadOnlyList<PowerTurnFrontierState> plainShivs = PowerTurnFrontier.Build(
    0, 0, shivHand);
IReadOnlyList<PowerTurnFrontierState> accurateShivs = PowerTurnFrontier.Build(
    0, 0, shivHand, damagePerShiv: 4);
Require(PowerTurnFrontier.DefensiveDamageUplift(plainShivs, accurateShivs) == 8,
    "精准没有按实际可打出的两张小刀逐张兑现增伤。");
IReadOnlyList<PowerTurnFrontierState> phantomShivs = PowerTurnFrontier.Build(
    0, 0, shivHand, firstShivDamageBonus: 9);
Require(PowerTurnFrontier.DefensiveDamageUplift(plainShivs, phantomShivs) == 9,
    "幻影之刃把每回合第一张小刀增伤重复计算到了后续小刀。");
PowerTurnCardOption[] triggerHand =
[
    new(EnergyCost: 1, Damage: 6, Block: 0, UnblockedAttackHits: 2),
    new(EnergyCost: 1, Damage: 0, Block: 5),
];
IReadOnlyList<PowerTurnFrontierState> noCardTriggers = PowerTurnFrontier.Build(
    2, 0, triggerHand);
IReadOnlyList<PowerTurnFrontierState> withSerpent = PowerTurnFrontier.Build(
    2, 0, triggerHand, damagePerCard: 4);
Require(PowerTurnFrontier.DefensiveDamageUplift(noCardTriggers, withSerpent) == 8,
    "群蛇形态没有按实际可打出的两张牌逐张触发伤害。");
IReadOnlyList<PowerTurnFrontierState> withEnvenom = PowerTurnFrontier.Build(
    2, 0, triggerHand, damagePerUnblockedAttackHit: 1);
Require(PowerTurnFrontier.DefensiveDamageUplift(noCardTriggers, withEnvenom) == 2,
    "涂毒没有按实际未格挡攻击命中兑现中毒。");
Require(PoisonStackProjection.ExtraTriggerDamage(9, 1, 100) == 9,
    "触媒没有按当前毒层兑现至少一次额外触发。");
Require(PoisonStackProjection.RecurringApplicationDamage(2, 3, 100) == 9,
    "毒雾没有在逐回合上毒后保留剩余毒层的滚动收益。");

RetainedHandTransitionResult usefulRetain = RetainedHandTransition.Evaluate(
    nextTurnEnergy: 3,
    handLimit: 10,
    normalDrawCount: 5,
    retainedCards: [new(Value: 12, EnergyCost: 1)],
    nextDrawValues: [4, 4, 4, 4, 4]);
Require(usefulRetain.NetValue == 12,
    "计划妥当没有保留可支付的高价值牌。");
RetainedHandTransitionResult cloggedRetain = RetainedHandTransition.Evaluate(
    nextTurnEnergy: 3,
    handLimit: 5,
    normalDrawCount: 5,
    retainedCards:
    [
        new(Value: 1, EnergyCost: 2),
        new(Value: 1, EnergyCost: 2),
        new(Value: 1, EnergyCost: 2),
        new(Value: 1, EnergyCost: 2),
        new(Value: 1, EnergyCost: 2),
    ],
    nextDrawValues: [8, 8, 8, 8, 8]);
Require(cloggedRetain.NetValue < 0,
    "计划妥当塞满低价值手牌时没有扣除被阻塞的抽牌。");
DrawDiscardTransitionResult usefulTool = DrawDiscardTransition.Evaluate(
    handLimit: 5,
    baseDrawCount: 2,
    retainedCards: [],
    nextDrawCards:
    [
        new(Value: 5),
        new(Value: 4),
        new(Value: 10),
    ]);
Require(usefulTool.NetValue == 6,
    "必备工具没有用额外抽牌替换下一手最低价值牌。");
DrawDiscardTransitionResult cloggedTool = DrawDiscardTransition.Evaluate(
    handLimit: 5,
    baseDrawCount: 5,
    retainedCards:
    [
        new(Value: 1), new(Value: 1), new(Value: 1), new(Value: 1), new(Value: 1),
    ],
    nextDrawCards: []);
Require(cloggedTool.NetValue == -1,
    "必备工具在满手且无法抽牌时没有计入强制弃牌损失。");
DrawDiscardTransitionResult slyTool = DrawDiscardTransition.Evaluate(
    handLimit: 5,
    baseDrawCount: 2,
    retainedCards: [],
    nextDrawCards:
    [
        new(Value: 5),
        new(Value: 4),
        new(Value: 1, DiscardPayoff: 8),
    ]);
Require(slyTool.NetValue == 8,
    "必备工具没有把奇巧牌的真实弃牌收益计入换牌。");
Require(SilentCardFlowFacts.DrawCount("BACKFLIP", cards: 2, handCount: 4) == 2
    && SilentCardFlowFacts.DrawCount("CALCULATED_GAMBLE", cards: 0, handCount: 4) == 3
    && SilentCardFlowFacts.DrawCount("BLADE_DANCE", cards: 3, handCount: 4) == 0,
    "静默猎手回合内抽牌事实不正确。");

MasterPlannerSkillFact[] plannerSkills =
[
    new(Value: 12, EnergyCost: 1, TurnsUntilSeed: 0, TurnsUntilPayoff: 1),
    new(Value: 8, EnergyCost: 1, TurnsUntilSeed: 0, TurnsUntilPayoff: 1),
];
MasterPlannerProjectionResult earlyPlanner = MasterPlannerProjection.Evaluate(
    currentEnergy: 2,
    futureEnergyPerTurn: 3,
    remainingTurns: 4,
    discardWindows: 1,
    plannerSkills);
Require(earlyPlanner is { SeededSkillCount: 1, EarliestPayoffTurns: 1, CardAccessValue: 12 },
    "谋划专家没有选择当前可支付且价值最高的技能建立奇巧循环。");
MasterPlannerProjectionResult latePlanner = MasterPlannerProjection.Evaluate(
    currentEnergy: 1,
    futureEnergyPerTurn: 3,
    remainingTurns: 4,
    discardWindows: 1,
    [plannerSkills[1]]);
Require(latePlanner.CardAccessValue == 8 && earlyPlanner.CardAccessValue > latePlanner.CardAccessValue,
    "谋划专家晚于高价值技能打出时没有失去对应潜力。");
Require(!MasterPlannerProjection.Evaluate(
        2, 3, 4, discardWindows: 0, plannerSkills).HasPayoff,
    "没有弃牌窗口时谋划专家虚构了奇巧兑现。");
Require(!MasterPlannerProjection.Evaluate(
        2, 3, 1, discardWindows: 1, plannerSkills).HasPayoff,
    "战斗在重新入手前结束时谋划专家仍获得了未来收益。");
MasterPlannerProjectionResult futurePlanner = MasterPlannerProjection.Evaluate(
    currentEnergy: 0,
    futureEnergyPerTurn: 3,
    remainingTurns: 4,
    discardWindows: 1,
    [new(Value: 10, EnergyCost: 2, TurnsUntilSeed: 1, TurnsUntilPayoff: 2)]);
Require(futurePlanner is { SeededSkillCount: 1, EarliestPayoffTurns: 2, CardAccessValue: 5 },
    "谋划专家没有保留两回合窗口内可播种并重新入手的技能。");
Require(SilentDiscardWindowFacts.Capacity("PREPARED", selectedCards: 2, handCount: 5) == 2
    && SilentDiscardWindowFacts.Capacity("CALCULATED_GAMBLE", selectedCards: 0, handCount: 5) == 4
    && SilentDiscardWindowFacts.Capacity("STRIKE_SILENT", selectedCards: 0, handCount: 5) == 0,
    "静默猎手弃牌窗口事实不正确。");

PowerCommitmentDescriptor plannerDescriptor = new(
    PowerCommitmentFamily.HandEngine,
    SilentPowerCardIdentity.MasterPlanner);
PowerCommitment lifecycle = PowerCommitmentLifecycle.Create(
    plannerDescriptor,
    turn: 1,
    actionCount: 1,
    historyEntryCount: 10,
    investment: 8,
    provisionalPotential: 12);
PowerCommitmentAdvanceResult progressed = PowerCommitmentLifecycle.Advance(
    lifecycle,
    parentTurn: 1,
    childTurn: 1,
    maximumTransitions: 2,
    progressEvidence: 5,
    realizedEvidence: 0,
    terminal: false);
Require(progressed.Disposition == PowerCommitmentDisposition.Active
    && progressed.Commitment is { ProgressEvidence: 5, ProvisionalPotential: 12 },
    "中间奇巧证据错误消耗了尚未兑现的能力潜力。");
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
    "真实奇巧自动出牌后能力承诺没有退出。");
PowerCommitmentAdvanceResult expired = PowerCommitmentLifecycle.Advance(
    lifecycle,
    parentTurn: 1,
    childTurn: 4,
    maximumTransitions: 2,
    progressEvidence: 0,
    realizedEvidence: 0,
    terminal: false);
Require(expired.Disposition == PowerCommitmentDisposition.Expired
    && expired.Commitment == null,
    "能力承诺越过回合上限后没有到期。");

Console.WriteLine("POWER_CARD_VALUATION_CHECKS_OK silent_models=17");

static PowerCardValuationResult Evaluate(
    PowerCardValuationRegistry registry,
    CardModel card,
    PowerCardValuationContext context)
{
    Require(registry.TryEvaluate(card, in context, out PowerCardValuationResult result),
        $"{card.GetType().Name} 没有命中估值模型。");
    return result;
}

static PowerCardValuationContext Context(
    int effectiveEnergyCost = 1,
    int nextTurnIncomingDamage = 12,
    int nextTurnIncomingHitCount = 2,
    int followingTurnIncomingDamage = 9,
    int followingTurnIncomingHitCount = 1,
    int dexterityLossValue = 4,
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
            IncomingHitCount: 4));

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
