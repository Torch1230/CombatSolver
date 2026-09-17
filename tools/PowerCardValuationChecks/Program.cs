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

PowerCardValuationRegistry silent = PowerCardValuationModels.Registry;
Require(silent.Count == 17, "静默猎手单人能力牌没有完整登记。");
Require(silent.RegisteredCardTypes(PowerCardPool.Silent).Count == 17,
    "静默猎手卡池登记数量不正确。");
Require(Enum.GetValues<PowerCardPool>()
        .Where(pool => pool != PowerCardPool.Silent)
        .All(pool => silent.RegisteredCardTypes(pool).Count == 0),
    "首批模型越过了静默猎手卡池边界。");

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
