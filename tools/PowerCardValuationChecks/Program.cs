using CombatSolver;
using MegaCrit.Sts2.Core.Models;

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
        (PowerCardValuationRequirements.CurrentTurnSkills | PowerCardValuationRequirements.FutureSkills),
    "登记需求不正确。");
PowerCardValuationContext context = new(
    EnemyHp: 50,
    IncomingDamage: 12,
    IncomingHitCount: 2,
    RemainingTurns: 3,
    CurrentEnergy: 3,
    CurrentStars: 0,
    EffectiveEnergyCost: 1,
    EffectiveStarCost: 0,
    CurrentTurnAttacks: 0,
    CurrentTurnSkills: 2,
    CurrentTurnPowers: 0,
    CurrentTurnExhausts: 0,
    FutureAttacks: 0,
    FutureSkills: 5,
    FuturePowers: 0,
    FutureExhausts: 0,
    AverageCardValue: 8,
    BestCardValue: 12,
    ExpiresAtTurnEnd: false);
Require(registry.TryEvaluate(new TestPowerCard(), in context, out PowerCardValuationResult result),
    "已登记卡牌没有命中模型。");
Require(result.Reward.CardAccess == 7 && result.Penalty.ActivationCost == 1,
    "模型没有收到统一上下文。");
Require(result.Timing == PowerCardTiming.BeforeSkill,
    "模型时机没有透传。");
Require(registry.RegisteredCardTypes(PowerCardPool.Silent).SequenceEqual([typeof(TestPowerCard)]),
    "角色卡池分类不正确。");
Require(PowerCardValuationModels.Registry.Count == 0,
    "用户尚未确认任何能力牌，默认登记表必须为空。");
RequireThrows<InvalidOperationException>(() => new PowerCardValuationRegistry([model, model]));

Console.WriteLine("POWER_CARD_VALUATION_CHECKS_OK");

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
        PowerCardValuationRequirements.CurrentTurnSkills |
        PowerCardValuationRequirements.FutureSkills;

    protected override PowerCardValuationResult Evaluate(
        TestPowerCard card,
        in PowerCardValuationContext context)
        => new(
            new PowerCardValuationReward(cardAccess:
                context.CurrentTurnSkills + context.FutureSkills),
            new PowerCardValuationPenalty(activationCost:
                context.EffectiveEnergyCost + context.EffectiveStarCost),
            PowerCardTiming.BeforeSkill);
}
