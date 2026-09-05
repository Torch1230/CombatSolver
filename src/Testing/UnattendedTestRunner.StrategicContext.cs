using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertStrategicEffectContextRequirements()
    {
        // Real model metadata is sufficient here: these cards are read-only inputs, not
        // injected into live piles, and no power or card effect is being simulated.
        PredictedCard attack = new(ModelDb.Card<StrikeIronclad>());
        PredictedCard skill = new(ModelDb.Card<DefendIronclad>());
        IReadOnlyList<PredictedCard> mixedCards = [attack, skill];
        IReadOnlyList<PredictedCard> noAttacks = [skill];
        StrategicEffectRequirements unrelatedPower = StrategicEffectModel.Requirements(
            ModelDb.Power<BufferPower>());
        StrategicEffectRequirements attackPower = StrategicEffectModel.Requirements(
            ModelDb.Power<StrengthPower>());
        const int playerBlock = 23;

        static StrategicEffectContext Build(
            IReadOnlyList<PredictedCard> cards,
            StrategicEffectRequirements requirements)
            => StrategicEffectContext.Build(
                cards,
                enemyHp: 80,
                incomingDamage: 15,
                incomingHitCount: 2,
                playerBlock: playerBlock,
                requirements);

        if (unrelatedPower.HasFlag(StrategicEffectRequirements.AttackPlays)
            || !attackPower.HasFlag(StrategicEffectRequirements.AttackPlays)
            || Build(mixedCards, StrategicEffectRequirements.None).AttackPlays != 0
            || Build(mixedCards, unrelatedPower).AttackPlays != 0)
        {
            throw new InvalidOperationException("能力上下文反例没有覆盖缺少潜在攻击需求的旧输入。");
        }

        StrategicEffectContext expected = Build(mixedCards, attackPower);
        if (expected.AttackPlays <= 0)
            throw new InvalidOperationException("真实攻击牌没有形成可达攻击机会。");
        foreach (StrategicEffectRequirements active in new[]
                 {
                     StrategicEffectRequirements.None,
                     unrelatedPower,
                     attackPower,
                     unrelatedPower | attackPower,
                 })
        {
            StrategicEffectRequirements completed = CombatBeamSolver.CompleteStrategicEffectRequirements(active);
            StrategicEffectContext actual = Build(mixedCards, completed);
            StrategicEffectContext emptyAttackContext = Build(noAttacks, completed);
            if (actual.AttackPlays != expected.AttackPlays
                || actual.RemainingTurns != expected.RemainingTurns
                || actual.PlayerBlock != playerBlock
                || emptyAttackContext.AttackPlays != 0
                || emptyAttackContext.PlayerBlock != playerBlock)
            {
                throw new InvalidOperationException(
                    "共享能力上下文遗漏潜在消费者输入、依赖无关能力，或伪造了无攻击牌的机会。");
            }
        }

        foreach (StrategicEffectRequirements active in Enum.GetValues<StrategicEffectRequirements>())
        {
            StrategicEffectRequirements completed = CombatBeamSolver.CompleteStrategicEffectRequirements(active);
            if (completed != (active | StrategicEffectRequirements.AttackPlays)
                || CombatBeamSolver.CompleteStrategicEffectRequirements(completed) != completed)
            {
                throw new InvalidOperationException("能力需求闭包覆盖了既有消费者需求或不满足幂等性。");
            }
        }
    }
}
