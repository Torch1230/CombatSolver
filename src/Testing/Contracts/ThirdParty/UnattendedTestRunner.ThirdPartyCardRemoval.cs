using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// 第三方起手牌的移除估值登记点（<see cref="CardRemovalValueMirrors"/>）。这里拿一张原版
    /// 非起手牌当替身自行登记、断言完再撤销，所以同一个进程里后面的用例看不到它。
    /// </summary>
    private static void AssertThirdPartyBasicCardRemoval(Player player)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Third-party card removal: " + message);
        }

        // 替身取拳锋打击：原版牌，但不在那张写死的起手牌表里，所以未登记时必然走通用估值。
        PredictedCard standIn = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        PredictedCard vanillaStrike = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
        CardChoiceSpec spec = new(
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            MinCount: 0,
            MaxCount: 2,
            Options: [standIn, vanillaStrike],
            SourceCards: [standIn, vanillaStrike],
            ReplacementValue: 0d);

        Check(CardRemovalValueMirrors.IsEmpty, "the registry starts empty");
        double unregistered = CardChoiceSupport.RemovalPriorityForTesting(spec, standIn);
        double vanilla = CardChoiceSupport.RemovalPriorityForTesting(spec, vanillaStrike);
        // 未登记时走通用估值：伤害记满，于是它排在被压过权重的原版起手打击后面，不会先被烧。
        Check(unregistered > vanilla,
            $"an unregistered card outranks the vanilla basic strike: {unregistered} vs {vanilla}");

        const double offset = -20d;
        CardRemovalValueMirrors.Register<PommelStrike>(offset);
        try
        {
            Check(!CardRemovalValueMirrors.IsEmpty
                && CardRemovalValueMirrors.Offset(standIn.Preview) == offset,
                "registration is visible");
            double registered = CardChoiceSupport.RemovalPriorityForTesting(spec, standIn);
            Check(Math.Abs(registered - (unregistered + offset)) < 1e-9,
                $"the offset lands on top of the generic value: {registered} vs {unregistered} + {offset}");
            // 负偏置要能把排序键压到零以下——这才是「烧它」这条分支排在「一张都不选」之前的前提，
            // 因为 ChoicePriority 对消耗返回 -Σ RemovalPriority 并按降序取分支。
            Check(registered < 0d, $"a negative offset makes exhausting worth doing: {registered}");
            try
            {
                CardRemovalValueMirrors.Register<PommelStrike>(offset);
                Check(false, "duplicate registration must throw");
            }
            catch (ArgumentException) { }
            try
            {
                CardRemovalValueMirrors.Register<StrikeSilent>(-1000d);
                Check(false, "an out-of-range offset must throw");
            }
            catch (ArgumentOutOfRangeException) { }
            // 原版那张表优先：已经写死的类型不会被登记表改写。
            Check(CardChoiceSupport.RemovalPriorityForTesting(spec, vanillaStrike) == vanilla,
                "the vanilla table still wins for vanilla starters");
        }
        finally { CardRemovalValueMirrors.UnregisterForTesting<PommelStrike>(); }

        Check(CardRemovalValueMirrors.IsEmpty, "the test registration is cleaned up");
        Check(CardChoiceSupport.RemovalPriorityForTesting(spec, standIn) == unregistered,
            "cleanup restores the unregistered key");
    }
}
