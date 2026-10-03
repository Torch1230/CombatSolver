using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static Action CaptureAeonglassPreviewForkIsolation(
        CombatPredictionSimulator simulator,
        Player player,
        UnattendedMonsterMoveCheck check)
    {
        if (check.MonsterId != "Aeonglass" || check.MoveId != "INCREASING_INTENSITY_MOVE")
            throw new InvalidOperationException("预览所有权断言要求永世沙漏的递加强度行动。");
        PredictedCard[] cards = simulator.State.GetPlayerCombatState(player).AllCards.ToArray();
        CardModel[] previews = cards.Select(card => card.Preview).ToArray();
        if (!previews.Any(card => card is not Wither))
            throw new InvalidOperationException("预览所有权测试要求至少一张不会升级的普通牌。");
        int[] damageBefore = previews.Select(card => card is Wither ? card.DynamicVars.Damage.IntValue : 0).ToArray();
        CombatPredictionSimulator sibling = simulator.Fork();
        PredictedCard[] siblingCards = sibling.State.GetPlayerCombatState(player).AllCards.ToArray();
        CardModel[] siblingPreviews = siblingCards.Select(card => card.Preview).ToArray();
        if (cards.Length != siblingCards.Length)
            throw new InvalidOperationException("预览所有权测试的 Fork 改变了既有卡牌数量。");

        // The caller executes the actual production move, followed by the native differential.
        // Keep this sibling alive but unexecuted to detect writes through shared previews.
        return () =>
        {
            for (int index = 0; index < cards.Length; index++)
            {
                if (previews[index] is Wither)
                {
                    if (cards[index].Preview.DynamicVars.Damage.IntValue <= damageBefore[index])
                        throw new InvalidOperationException("递加强度没有升级既有凋零。");
                    if (siblingCards[index].Preview.DynamicVars.Damage.IntValue != damageBefore[index])
                        throw new InvalidOperationException("凋零升级越过了 Fork 所有权边界。");
                }
                else if (!ReferenceEquals(cards[index].Preview, previews[index]))
                {
                    throw new InvalidOperationException("只读牌型判断克隆了不会升级的非凋零预览。");
                }
                if (!ReferenceEquals(siblingCards[index].Preview, siblingPreviews[index]))
                    throw new InvalidOperationException("执行行动改变了未执行兄弟分支的预览身份。");
            }
            GC.KeepAlive(sibling);
        };
    }
}
