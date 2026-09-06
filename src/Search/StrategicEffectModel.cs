using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;

namespace CombatSolver;

[Flags]
internal enum StrategicEffectRequirements
{
    None = 0,
    RemainingTurns = 1 << 0,
    UsefulCardPlays = 1 << 1,
    AttackPlays = 1 << 2,
    SkillPlays = 1 << 3,
    BlockSkillPlays = 1 << 4,
    PowerPlays = 1 << 5,
    ExhaustPlays = 1 << 6,
    ShivPlays = 1 << 7,
    DebuffApplications = 1 << 8,
    SkillEnergySpend = 1 << 9,
    PowerEnergySpend = 1 << 10,
    AverageCardValue = 1 << 11,
    BestCardValue = 1 << 12,
    AverageAttackValue = 1 << 13,
    StatusDrawTriggers = 1 << 14,
}

internal readonly record struct StrategicEffectVector(
    int DamagePotential,
    int PreventionPotential,
    int ResourcePotential,
    int CardAccessPotential,
    int ScalingPotential)
{
    public static StrategicEffectVector Zero { get; } = new(0, 0, 0, 0, 0);

    // 快照与剪枝每个节点都读这个值；固定五项直接累加，不走 params 数组分配。
    public int RetentionValue => (int)Math.Clamp(
        (long)DamagePotential
            + PreventionPotential
            + ResourcePotential
            + CardAccessPotential
            + ScalingPotential,
        0L,
        int.MaxValue);

    public static StrategicEffectVector operator +(
        StrategicEffectVector left,
        StrategicEffectVector right)
        => new(
            SaturatingAdd(left.DamagePotential, right.DamagePotential),
            SaturatingAdd(left.PreventionPotential, right.PreventionPotential),
            SaturatingAdd(left.ResourcePotential, right.ResourcePotential),
            SaturatingAdd(left.CardAccessPotential, right.CardAccessPotential),
            SaturatingAdd(left.ScalingPotential, right.ScalingPotential));

    private static int SaturatingSum(params int[] values)
    {
        long total = 0;
        foreach (int value in values)
            total += value;
        return (int)Math.Clamp(total, 0L, int.MaxValue);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}

internal readonly record struct StrategicEffectContext(
    int EnemyHp,
    int IncomingDamage,
    int IncomingHitCount,
    int PlayerBlock,
    int RemainingTurns,
    int UsefulCardPlays,
    int AttackPlays,
    int SkillPlays,
    int BlockSkillPlays,
    int PowerPlays,
    int ExhaustPlays,
    int ShivPlays,
    int DebuffApplications,
    int SkillEnergySpend,
    int PowerEnergySpend,
    int AverageCardValue,
    int BestCardValue,
    int AverageAttackValue,
    int ReachableBlockValue,
    int AverageBlockValue,
    int StatusDrawTriggers)
{
    public static StrategicEffectContext Build(
        IReadOnlyList<PredictedCard> liveCards,
        int enemyHp,
        int incomingDamage,
        int incomingHitCount,
        int playerBlock,
        StrategicEffectRequirements requirements)
    {
        if (requirements == StrategicEffectRequirements.None)
        {
            return new StrategicEffectContext(
                Math.Max(0, enemyHp),
                Math.Max(0, incomingDamage),
                Math.Max(0, incomingHitCount),
                Math.Max(0, playerBlock),
                1,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                1,
                1,
                0,
                0,
                0,
                0);
        }

        const StrategicEffectRequirements reachablePlayRequirements =
            StrategicEffectRequirements.UsefulCardPlays
            | StrategicEffectRequirements.AttackPlays
            | StrategicEffectRequirements.SkillPlays
            | StrategicEffectRequirements.BlockSkillPlays
            | StrategicEffectRequirements.PowerPlays
            | StrategicEffectRequirements.ExhaustPlays
            | StrategicEffectRequirements.ShivPlays
            | StrategicEffectRequirements.DebuffApplications
            | StrategicEffectRequirements.StatusDrawTriggers;
        bool needsRemainingTurns = requirements.HasFlag(StrategicEffectRequirements.RemainingTurns)
            || (requirements & reachablePlayRequirements) != 0;
        bool needsAllCardValues = requirements.HasFlag(StrategicEffectRequirements.AverageCardValue)
            || requirements.HasFlag(StrategicEffectRequirements.BestCardValue);
        bool needsAttackValues = needsRemainingTurns
            || requirements.HasFlag(StrategicEffectRequirements.AverageAttackValue);
        bool needsAttackCount = requirements.HasFlag(StrategicEffectRequirements.AttackPlays)
            || requirements.HasFlag(StrategicEffectRequirements.AverageAttackValue);
        bool needsSkillCount = requirements.HasFlag(StrategicEffectRequirements.SkillPlays);
        bool needsBlockSkillCount = requirements.HasFlag(StrategicEffectRequirements.BlockSkillPlays);
        bool needsPowerCount = requirements.HasFlag(StrategicEffectRequirements.PowerPlays);
        bool needsExhaustCount = requirements.HasFlag(StrategicEffectRequirements.ExhaustPlays);
        bool needsShivCount = requirements.HasFlag(StrategicEffectRequirements.ShivPlays);
        bool needsDebuffCount = requirements.HasFlag(StrategicEffectRequirements.DebuffApplications);
        bool needsStatusCount = requirements.HasFlag(StrategicEffectRequirements.StatusDrawTriggers);
        bool needsSkillEnergy = requirements.HasFlag(StrategicEffectRequirements.SkillEnergySpend);
        bool needsPowerEnergy = requirements.HasFlag(StrategicEffectRequirements.PowerEnergySpend);

        int attackCount = 0;
        int skillCount = 0;
        int blockSkillCount = 0;
        int powerCount = 0;
        int exhaustCount = 0;
        int shivCount = 0;
        int debuffCount = 0;
        int statusCount = 0;
        int skillEnergy = 0;
        int powerEnergy = 0;
        int totalCardValue = 0;
        int bestCardValue = 0;
        int totalAttackValue = 0;
        int totalBlockValue = 0;
        foreach (PredictedCard predicted in liveCards)
        {
            CardModel card = predicted.Preview;
            CardType cardType = card.Type;
            int cardValue = 0;
            if (needsAllCardValues || (needsAttackValues && cardType == CardType.Attack))
            {
                cardValue = Math.Max(1, (int)Math.Ceiling(CardChoiceSupport.CardValue(card)));
                if (needsAllCardValues)
                {
                    totalCardValue += cardValue;
                    bestCardValue = Math.Max(bestCardValue, cardValue);
                }
                if (needsAttackValues && cardType == CardType.Attack)
                    totalAttackValue += cardValue;
            }

            if (needsBlockSkillCount && cardType == CardType.Skill)
            {
                int blockValue = Math.Max(
                    0,
                    (int)Math.Ceiling(CardChoiceSupport.DynamicVarBaseValue(
                        card.DynamicVars,
                        "Block")));
                totalBlockValue += blockValue;
            }

            bool hasBlockDynamicVar = false;
            bool hasDebuffDynamicVar = false;
            if ((needsBlockSkillCount && cardType == CardType.Skill) || needsDebuffCount)
            {
                // DynamicVarSet 的 GetEnumerator 会把内部 Dictionary 的结构体枚举器装箱，
                // 每张牌每次快照都要跑一遍；直接枚举其（已 publicize 的）内部字典。
                foreach (KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> dynamicVar in card.DynamicVars._vars)
                {
                    if (ObserveDynamicVarKey(
                            dynamicVar.Key,
                            cardType,
                            needsBlockSkillCount,
                            needsDebuffCount,
                            ref hasBlockDynamicVar,
                            ref hasDebuffDynamicVar))
                    {
                        break;
                    }
                }
            }

            switch (cardType)
            {
                case CardType.Attack:
                    if (needsAttackCount)
                        attackCount++;
                    break;
                case CardType.Skill:
                    if (needsSkillCount)
                        skillCount++;
                    if (needsSkillEnergy)
                        skillEnergy += EnergyCost(card);
                    if (needsBlockSkillCount && hasBlockDynamicVar)
                        blockSkillCount++;
                    break;
                case CardType.Power:
                    if (needsPowerCount)
                        powerCount++;
                    if (needsPowerEnergy)
                        powerEnergy += EnergyCost(card);
                    break;
                case CardType.Status:
                    if (needsStatusCount)
                        statusCount++;
                    break;
            }
            if (needsExhaustCount && card.Keywords.Contains(CardKeyword.Exhaust))
                exhaustCount++;
            if (needsShivCount && card.Tags.Contains(CardTag.Shiv))
                shivCount++;
            if (needsDebuffCount && hasDebuffDynamicVar)
                debuffCount++;
        }

        int actualDeckSize = liveCards.Count;
        int deckSize = Math.Max(1, actualDeckSize);
        int remainingTurns = 1;
        int reachableCards = 0;
        if (needsRemainingTurns)
        {
            int cardsPerTurn = Math.Min(10, Math.Max(1, Math.Min(5, deckSize)));
            int damagePerCycle = Math.Max(1, totalAttackValue);
            int estimatedCycles = (int)Math.Ceiling((double)Math.Max(1, enemyHp) / damagePerCycle);
            remainingTurns = Math.Clamp(
                estimatedCycles * Math.Max(1, (int)Math.Ceiling(deckSize / 5d)),
                1,
                SolverWeights.SetupValueHorizonTurns);
            reachableCards = actualDeckSize == 0
                ? 0
                : Math.Min(deckSize * 2, remainingTurns * cardsPerTurn);
        }
        int attackPlays = requirements.HasFlag(StrategicEffectRequirements.AttackPlays)
            ? ReachablePlays(attackCount, deckSize, reachableCards)
            : 0;
        int skillPlays = requirements.HasFlag(StrategicEffectRequirements.SkillPlays)
            ? ReachablePlays(skillCount, deckSize, reachableCards)
            : 0;
        int blockSkillPlays = requirements.HasFlag(StrategicEffectRequirements.BlockSkillPlays)
            ? ReachablePlays(blockSkillCount, deckSize, reachableCards)
            : 0;
        int powerPlays = requirements.HasFlag(StrategicEffectRequirements.PowerPlays)
            ? Math.Min(powerCount, reachableCards)
            : 0;
        int exhaustPlays = requirements.HasFlag(StrategicEffectRequirements.ExhaustPlays)
            ? Math.Min(exhaustCount, reachableCards)
            : 0;
        int shivPlays = requirements.HasFlag(StrategicEffectRequirements.ShivPlays)
            ? ReachablePlays(shivCount, deckSize, reachableCards)
            : 0;
        int debuffApplications = requirements.HasFlag(StrategicEffectRequirements.DebuffApplications)
            ? ReachablePlays(debuffCount, deckSize, reachableCards)
            : 0;
        int reachableBlockValue = requirements.HasFlag(StrategicEffectRequirements.BlockSkillPlays)
            ? ReachableValue(totalBlockValue, deckSize, reachableCards)
            : 0;
        return new StrategicEffectContext(
            Math.Max(0, enemyHp),
            Math.Max(0, incomingDamage),
            Math.Max(0, incomingHitCount),
            Math.Max(0, playerBlock),
            remainingTurns,
            reachableCards,
            attackPlays,
            skillPlays,
            blockSkillPlays,
            powerPlays,
            exhaustPlays,
            shivPlays,
            debuffApplications,
            skillEnergy,
            powerEnergy,
            requirements.HasFlag(StrategicEffectRequirements.AverageCardValue)
                ? Math.Max(1, totalCardValue / deckSize)
                : 1,
            requirements.HasFlag(StrategicEffectRequirements.BestCardValue)
                ? Math.Max(1, bestCardValue)
                : 1,
            requirements.HasFlag(StrategicEffectRequirements.AverageAttackValue) && attackCount > 0
                ? Math.Max(1, totalAttackValue / attackCount)
                : 0,
            reachableBlockValue,
            requirements.HasFlag(StrategicEffectRequirements.BlockSkillPlays) && blockSkillCount > 0
                ? Math.Max(1, totalBlockValue / blockSkillCount)
                : 0,
            requirements.HasFlag(StrategicEffectRequirements.StatusDrawTriggers)
                ? Math.Min(remainingTurns, ReachablePlays(statusCount, deckSize, reachableCards))
                : 0);
    }

    private static int EnergyCost(CardModel card)
        => card.EnergyCost.CostsX
            ? 0
            : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));

    private static int ReachablePlays(int matchingCards, int deckSize, int reachableCards)
        => matchingCards == 0
            ? 0
            : Math.Max(1, (int)Math.Ceiling((double)matchingCards * reachableCards / deckSize));

    private static int ReachableValue(int totalValue, int deckSize, int reachableCards)
        => totalValue == 0
            ? 0
            : Math.Max(1, (int)Math.Ceiling((double)totalValue * reachableCards / deckSize));

    /// <summary>返回 true 表示需要的标记都已确定，可以停止扫描。</summary>
    private static bool ObserveDynamicVarKey(
        string key,
        CardType cardType,
        bool needsBlockSkillCount,
        bool needsDebuffCount,
        ref bool hasBlockDynamicVar,
        ref bool hasDebuffDynamicVar)
    {
        if (needsBlockSkillCount && !hasBlockDynamicVar && cardType == CardType.Skill)
            hasBlockDynamicVar = IsBlockDynamicVar(key);
        if (needsDebuffCount && !hasDebuffDynamicVar)
            hasDebuffDynamicVar = IsDebuffDynamicVar(key);
        return (!needsDebuffCount || hasDebuffDynamicVar)
            && (!needsBlockSkillCount || cardType != CardType.Skill || hasBlockDynamicVar);
    }

    private static bool IsDebuffDynamicVar(string key)
        => key.Contains("Weak", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Poison", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Doom", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Debuff", StringComparison.OrdinalIgnoreCase)
            || key.Contains("StrengthLoss", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockDynamicVar(string key)
        => key.Contains("Block", StringComparison.OrdinalIgnoreCase);
}

internal static class StrategicEffectModel
{
    public static StrategicEffectRequirements Requirements(PowerModel power)
        => power switch
        {
            AfterimagePower => StrategicEffectRequirements.UsefulCardPlays,
            BufferPower => StrategicEffectRequirements.RemainingTurns,
            FeelNoPainPower => StrategicEffectRequirements.ExhaustPlays,
            EchoFormPower => StrategicEffectRequirements.UsefulCardPlays
                | StrategicEffectRequirements.BestCardValue,
            EnvenomPower or StrengthPower => StrategicEffectRequirements.AttackPlays,
            AccuracyPower => StrategicEffectRequirements.ShivPlays,
            SleightOfFleshPower => StrategicEffectRequirements.DebuffApplications,
            LethalityPower or ReaperFormPower => StrategicEffectRequirements.AttackPlays
                | StrategicEffectRequirements.AverageAttackValue,
            DexterityPower => StrategicEffectRequirements.BlockSkillPlays,
            BarricadePower => StrategicEffectRequirements.RemainingTurns
                | StrategicEffectRequirements.BlockSkillPlays,
            RagePower => StrategicEffectRequirements.AttackPlays,
            UnmovablePower => StrategicEffectRequirements.BlockSkillPlays,
            BlockNextTurnPower => StrategicEffectRequirements.RemainingTurns,
            StratagemPower or WellLaidPlansPower => StrategicEffectRequirements.RemainingTurns
                | StrategicEffectRequirements.BestCardValue,
            DemonFormPower => StrategicEffectRequirements.AttackPlays
                | StrategicEffectRequirements.RemainingTurns,
            CuriousPower => StrategicEffectRequirements.PowerEnergySpend
                | StrategicEffectRequirements.PowerPlays
                | StrategicEffectRequirements.AverageCardValue,
            CorruptionPower => StrategicEffectRequirements.SkillEnergySpend
                | StrategicEffectRequirements.AverageCardValue,
            CreativeAiPower => StrategicEffectRequirements.RemainingTurns
                | StrategicEffectRequirements.AverageCardValue,
            IterationPower => StrategicEffectRequirements.StatusDrawTriggers
                | StrategicEffectRequirements.AverageCardValue,
            MasterPlannerPower => StrategicEffectRequirements.SkillPlays
                | StrategicEffectRequirements.AverageCardValue,
            FocusPower or FurnacePower or ThunderPower or LightningRodPower
                => StrategicEffectRequirements.RemainingTurns,
            _ => StrategicEffectRequirements.None,
        };

    public static StrategicEffectVector Evaluate(
        PowerModel power,
        StrategicEffectContext context)
    {
        int amount = Math.Max(1, power.Amount);
        int enemyHp = context.EnemyHp;
        int energyUnit = Math.Max(3, context.AverageCardValue);
        int cardAccessUnit = context.AverageCardValue;
        return power switch
        {
            AfterimagePower => Prevention(amount * context.UsefulCardPlays, context),
            BufferPower => Prevention(BufferPrevention(amount, context), context),
            FeelNoPainPower => Prevention(amount * context.ExhaustPlays, context),
            ThornsPower => Damage(amount * context.IncomingHitCount, enemyHp),
            EchoFormPower when context.UsefulCardPlays > 0 => Damage(
                context.BestCardValue
                    * Math.Min(amount, Math.Max(1, context.UsefulCardPlays))
                    * context.RemainingTurns,
                enemyHp),
            EchoFormPower => StrategicEffectVector.Zero,
            EnvenomPower => Damage(amount * context.AttackPlays, enemyHp),
            AccuracyPower => Damage(amount * context.ShivPlays, enemyHp),
            SleightOfFleshPower => Damage(amount * context.DebuffApplications, enemyHp),
            StrengthPower => Damage(amount * context.AttackPlays, enemyHp),
            LethalityPower when context.AttackPlays > 0 => Damage(
                context.AverageAttackValue
                    * Math.Min(context.AttackPlays, context.RemainingTurns)
                    * amount / 100,
                enemyHp),
            ReaperFormPower when context.AttackPlays > 0 => Damage(
                context.AverageAttackValue * context.AttackPlays * amount,
                enemyHp),
            LethalityPower or ReaperFormPower => StrategicEffectVector.Zero,
            DexterityPower => Prevention(amount * context.BlockSkillPlays, context),
            BarricadePower => PersistentBlockPrevention(context),
            RagePower => Prevention(amount * context.AttackPlays, context),
            UnmovablePower => Prevention(
                Math.Min(amount, context.BlockSkillPlays) * context.AverageBlockValue,
                context),
            BlockNextTurnPower => Prevention(amount, context),
            StratagemPower or WellLaidPlansPower => CardAccess(
                amount
                    * context.BestCardValue),
            DemonFormPower when context.AttackPlays > 0 => Damage(
                amount * Math.Max(1, context.AttackPlays / Math.Max(1, context.RemainingTurns))
                    * context.RemainingTurns * (context.RemainingTurns + 1) / 2,
                enemyHp),
            DemonFormPower => StrategicEffectVector.Zero,
            CuriousPower => Resource(
                Math.Min(context.PowerEnergySpend, amount * context.PowerPlays) * energyUnit),
            CorruptionPower => Resource(context.SkillEnergySpend * energyUnit),
            CreativeAiPower => CardAccess(
                amount * context.RemainingTurns * cardAccessUnit),
            IterationPower => CardAccess(
                amount * context.StatusDrawTriggers * cardAccessUnit),
            MasterPlannerPower => CardAccess(context.SkillPlays * cardAccessUnit),
            FocusPower => Scaling(amount * context.RemainingTurns * 2),
            FurnacePower => Scaling(amount * context.RemainingTurns * 2),
            ThunderPower => Damage(amount * context.RemainingTurns, enemyHp),
            LightningRodPower => Scaling(amount * context.RemainingTurns),
            _ => Scaling(amount),
        };
    }

    private static StrategicEffectVector Damage(int value, int enemyHp)
        => new(Math.Min(Math.Max(0, enemyHp), Math.Max(0, value)), 0, 0, 0, 0);

    private static StrategicEffectVector Prevention(int value, StrategicEffectContext context)
    {
        int cap = context.IncomingDamage == 0
            ? value
            : context.IncomingDamage * Math.Min(2, context.RemainingTurns);
        return new(0, Math.Min(Math.Max(0, cap), Math.Max(0, value)), 0, 0, 0);
    }

    private static int BufferPrevention(int amount, StrategicEffectContext context)
    {
        if (context.IncomingDamage == 0)
            return amount * 8;
        int averageHit = Math.Max(
            1,
            context.IncomingDamage / Math.Max(1, context.IncomingHitCount));
        return Math.Min(context.IncomingDamage, amount * averageHit);
    }

    private static StrategicEffectVector PersistentBlockPrevention(
        StrategicEffectContext context)
    {
        int value = context.PlayerBlock;
        int turns = Math.Min(4, Math.Max(1, context.RemainingTurns));
        int cap = context.IncomingDamage == 0
            ? value
            : context.IncomingDamage * turns;
        return new(0, Math.Min(Math.Max(0, cap), Math.Max(0, value)), 0, 0, 0);
    }

    private static StrategicEffectVector Resource(int value)
        => new(0, 0, Math.Max(0, value), 0, 0);

    private static StrategicEffectVector CardAccess(int value)
        => new(0, 0, 0, Math.Max(0, value), 0);

    private static StrategicEffectVector Scaling(int value)
        => new(0, 0, 0, 0, Math.Max(0, value));
}
