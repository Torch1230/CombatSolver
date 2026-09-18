using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 非静默卡池共用的量化尺度与有界公式原语。逐卡差异由各角色目录决定，
/// 这里只提供统一单位、时机惩罚和前沿/上限工具。
/// </summary>
internal static class PowerCardValueFacts
{
    public static int EnergyUnit(in PowerCardValuationContext context)
        => Math.Max(3, context.AverageCardValue);

    public static int ActivationCost(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            PowerCardValuationMath.SaturatingProduct(
                context.EffectiveEnergyCost,
                EnergyUnit(in context)),
            PowerCardValuationMath.SaturatingProduct(
                context.EffectiveStarCost,
                Math.Max(EnergyUnit(in context), context.BestCardValue)));

    public static PowerCardValuationPenalty Penalty(
        in PowerCardValuationContext context,
        int triggers,
        bool delayed = false,
        int antiSynergy = 0)
        => new(
            activationCost: ActivationCost(in context),
            delayedPayoff: delayed && triggers > 0
                ? Math.Max(1, EnergyUnit(in context) / 2)
                : 0,
            triggerScarcity: triggers == 0 ? EnergyUnit(in context) : 0,
            antiSynergy: antiSynergy);

    public static int UpgradeValue(bool upgraded, int normal, int upgradedValue)
        => upgraded ? upgradedValue : normal;

    public static int DamageCap(int potential, in PowerCardValuationContext context)
        => Math.Min(context.EnemyHp, Math.Max(0, potential));

    public static int FutureTurnStarts(in PowerCardValuationContext context)
        => Math.Max(0, context.RemainingTurns - 1);

    public static int TotalCards(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.UsefulCardPlays,
            context.Future.UsefulCardPlays);

    public static int TotalAttackPlays(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.AttackPlays,
            context.Future.AttackPlays);

    public static int TotalSkillPlays(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.SkillPlays,
            context.Future.SkillPlays);

    public static int TotalBlockSkills(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.BlockSkillPlays,
            context.Future.BlockSkillPlays);

    public static int TotalPowerPlays(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.PowerPlays,
            context.Future.PowerPlays);

    public static int TotalExhausts(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.Exhausts,
            context.Future.Exhausts);

    public static int TotalUnblockedAttackHits(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.UnblockedAttackHits,
            context.Future.UnblockedAttackHits);

    public static int TotalDraws(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.DrawsAfterOpening,
            context.Future.DrawsAfterOpening);

    public static int TotalDiscards(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.Discards,
            context.Future.Discards);

    public static int TotalShivs(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.ShivPlays,
            context.Future.ShivPlays);

    public static int TotalWeakTargetDamage(in PowerCardValuationContext context)
        => PowerCardValuationMath.SaturatingSum(
            context.CurrentTurn.WeakTargetAttackDamage,
            context.Future.WeakTargetAttackDamage);

    /// <summary>
    /// 防伤上限：优先计入预计会真正减少战损的格挡；没有预计来袭伤害时只给部分信用，
    /// 不把未跨阈值的格挡当成完整收益。
    /// </summary>
    public static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBlock);
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    public static int IncomingDamage(in PowerCardValuationContext context)
        => Math.Max(0, context.CurrentTurn.IncomingDamage);

    public static PowerCardValuationResult StatGrowth(
        CardModel card,
        in PowerCardValuationContext context,
        int normal,
        int upgraded,
        bool perTurn)
    {
        int stat = UpgradeValue(card.IsUpgraded, normal, upgraded);
        int triggers = Math.Max(1, TotalAttackPlays(in context));
        return new(
            new PowerCardValuationReward(scaling: PowerCardValuationMath.SaturatingProduct(stat, triggers)),
            Penalty(in context, triggers, delayed: perTurn),
            PowerCardTiming.BeforeAttack
            | PowerCardTiming.CurrentTurn
            | PowerCardTiming.FutureTurns);
    }

    public static PowerCardValuationResult BlockPerTrigger(
        CardModel card,
        in PowerCardValuationContext context,
        int normal,
        int upgraded,
        int triggers,
        bool delayed = false)
    {
        int perTrigger = UpgradeValue(card.IsUpgraded, normal, upgraded);
        int raw = PowerCardValuationMath.SaturatingProduct(perTrigger, Math.Max(0, triggers));
        return new(
            new PowerCardValuationReward(
                prevention: DamageCap(BoundedPrevention(raw, IncomingDamage(in context)), in context)),
            Penalty(in context, triggers, delayed),
            PowerCardTiming.BeforeIncomingDamage
            | PowerCardTiming.CurrentTurn
            | PowerCardTiming.FutureTurns);
    }

    public static PowerCardValuationResult DamagePerTrigger(
        CardModel card,
        in PowerCardValuationContext context,
        int normal,
        int upgraded,
        int triggers,
        bool delayed = false,
        PowerCardTiming timing = PowerCardTiming.BeforeAttack
            | PowerCardTiming.CurrentTurn
            | PowerCardTiming.FutureTurns)
    {
        int perTrigger = UpgradeValue(card.IsUpgraded, normal, upgraded);
        int raw = PowerCardValuationMath.SaturatingProduct(perTrigger, Math.Max(0, triggers));
        return new(
            new PowerCardValuationReward(damage: DamageCap(raw, in context)),
            Penalty(in context, triggers, delayed),
            timing);
    }

    public static PowerCardValuationResult EnergyPerTurn(
        CardModel card,
        in PowerCardValuationContext context,
        int normal,
        int upgraded,
        int starCost = 0)
    {
        int perTurn = UpgradeValue(card.IsUpgraded, normal, upgraded);
        int turns = Math.Max(1, FutureTurnStarts(in context));
        int resource = PowerCardValuationMath.SaturatingProduct(perTurn, turns);
        _ = starCost;
        return new(
            new PowerCardValuationReward(resource: resource),
            Penalty(in context, turns, delayed: true),
            PowerCardTiming.BeforeTurnEnd | PowerCardTiming.FutureTurns);
    }

    public static PowerCardValuationResult CardAccessPerTrigger(
        CardModel card,
        in PowerCardValuationContext context,
        int perTrigger,
        int triggers,
        bool delayed = false)
    {
        _ = card;
        int value = Math.Max(1, context.AverageCardValue);
        int gain = PowerCardValuationMath.SaturatingProduct(perTrigger, Math.Max(0, triggers), value);
        return new(
            new PowerCardValuationReward(cardAccess: gain),
            Penalty(in context, triggers, delayed),
            PowerCardTiming.BeforeDraw
            | PowerCardTiming.CurrentTurn
            | PowerCardTiming.FutureTurns);
    }

    public static PowerCardValuationResult CostReduction(
        CardModel card,
        in PowerCardValuationContext context,
        int energySaved,
        int triggers)
    {
        _ = card;
        int saved = PowerCardValuationMath.SaturatingProduct(
            energySaved,
            Math.Max(0, triggers),
            EnergyUnit(in context));
        return new(
            new PowerCardValuationReward(resource: saved),
            Penalty(in context, triggers, delayed: true),
            PowerCardTiming.BeforeSkill
            | PowerCardTiming.CurrentTurn
            | PowerCardTiming.FutureTurns);
    }

    public static PowerCardValuationResult CrossCombat(
        CardModel card,
        in PowerCardValuationContext context,
        int value)
    {
        _ = card;
        return new(
            new PowerCardValuationReward(scaling: Math.Max(0, value)),
            new PowerCardValuationPenalty(
                activationCost: ActivationCost(in context),
                delayedPayoff: Math.Max(1, EnergyUnit(in context) / 2)),
            PowerCardTiming.FutureTurns);
    }
}

