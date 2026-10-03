using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertDrawHistoryDoesNotLimitFutureActions(CombatState combat, Player player)
    {
        CombatPredictionSimulator parent = CreateIsolatedDrawSimulator(combat, player);
        DrawAndReturnCard(parent, player, 120);
        AssertCompletedDrawCount(parent, 120);
        CombatPredictionSimulator first = parent.Fork();
        CombatPredictionSimulator second = parent.Fork();
        DrawAndReturnCard(first, player, 125);
        DrawAndReturnCard(second, player, 135);
        AssertCompletedDrawCount(first, 245);
        AssertCompletedDrawCount(second, 255);
        AssertCompletedDrawCount(parent, 120);
        DrawAndReturnCard(parent, player, 25);
        AssertCompletedDrawCount(parent, 145);
        _ = first.Fork();
        _ = second.Fork();
        _ = parent.Fork();
        AssertDrawRecursionFailsExplicitlyAndUnwinds(combat, player);
    }

    private static CombatPredictionSimulator CreateIsolatedDrawSimulator(CombatState combat, Player player)
    {
        SimulatedCombatState predicted = new(combat);
        CombatPredictionSimulator simulator = new(predicted);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in predicted.EffectivePowers().ToArray())
            predicted.SetPowerAmount(power, 0);
        ReplaceRootRelicsForTurnBoundaryTest(predicted, player);
        StabilizeForkBoundaryEnemies(simulator);
        simulator.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendIronclad>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        return simulator;
    }

    private static void DrawAndReturnCard(CombatPredictionSimulator simulator, Player player, int count)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        for (int index = 0; index < count; index++)
        {
            PredictedCard expected = playerState.DrawPile.Cards.Single();
            IReadOnlyList<PredictedCard> drawn = simulator.Draw(player, 1);
            if (drawn.Count != 1
                || !ReferenceEquals(drawn[0], expected)
                || !ReferenceEquals(playerState.Hand.Cards.Single(), expected)
                || !playerState.DrawPile.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"累计抽牌历史截断了后续合法 Draw：completed={simulator.History.Count<CombatPredictionCardDrawnEntry>()}。");
            }
            simulator.AddToPile(expected, PileType.Draw);
        }
    }

    private static void AssertCompletedDrawCount(CombatPredictionSimulator simulator, int expected)
    {
        if (simulator.History.Count<CombatPredictionCardDrawnEntry>() != expected
            || simulator.History.OfType<CombatPredictionCardDrawResolvedEntry>().Count() != expected
            || simulator.History.OfType<CombatPredictionRiskEntry>().Any())
        {
            throw new InvalidOperationException(
                $"连续抽牌/Fork 没有产生恰好 {expected} 次完整抽牌，或注入了虚假的抽牌风险边界。");
        }
    }

    private static void AssertDrawRecursionFailsExplicitlyAndUnwinds(CombatState combat, Player player)
    {
        FieldInfo depthField = typeof(CombatPredictionSimulator).GetField(
            "_activeDrawDepth", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(CombatPredictionSimulator), "_activeDrawDepth");
        FieldInfo limitField = typeof(CombatPredictionSimulator).GetField(
            "MaximumNestedDrawDepth", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(CombatPredictionSimulator), "MaximumNestedDrawDepth");
        int limit = (int)(limitField.GetRawConstantValue()
            ?? throw new InvalidOperationException("缺少同步抽牌递归上限。"));
        CombatPredictionSimulator simulator = CreateIsolatedDrawSimulator(combat, player);
        int historyBefore = simulator.History.Entries.Count;
        depthField.SetValue(simulator, limit);
        try
        {
            AssertDrawRecursionBoundaryThrows(simulator, player);
            if ((int)depthField.GetValue(simulator)! != limit
                || simulator.History.Entries.Count != historyBefore)
            {
                throw new InvalidOperationException("抽牌递归准入失败修改了深度或模拟状态。");
            }
        }
        finally
        {
            depthField.SetValue(simulator, 0);
        }
        DrawAndReturnCard(simulator, player, 1);
        AssertCompletedDrawCount(simulator, 1);

        // Enter one genuine recursive draw callback at the depth boundary. The outer scope
        // must unwind even though its deferred draw is incomplete and the action is rejected.
        CombatPredictionSimulator nested = CreateIsolatedDrawSimulator(combat, player);
        SimulatedCombatState predicted = (SimulatedCombatState)nested.State.CombatState;
        SimPlayerCombatState playerState = nested.State.GetPlayerCombatState(player);
        nested.RemoveFromCombat(playerState.AllCards.ToArray());
        nested.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<Dazed>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        predicted.AddPowerInstance<PagestormPower>(player.Creature, 1, player.Creature);
        depthField.SetValue(nested, limit - 1);
        try
        {
            AssertDrawRecursionBoundaryThrows(nested, player);
            if ((int)depthField.GetValue(nested)! != limit - 1
                || nested.History.Count<CombatPredictionCardDrawnEntry>() != 1
                || nested.History.OfType<CombatPredictionCardDrawResolvedEntry>().Any())
            {
                throw new InvalidOperationException("嵌套抽牌失败没有恢复外层深度，或错误提交了未完成抽牌。");
            }
        }
        finally
        {
            depthField.SetValue(nested, 0);
        }
        AssertForkRejected(nested, "unresolved deferred entries");
    }

    private static void AssertDrawRecursionBoundaryThrows(CombatPredictionSimulator simulator, Player player)
    {
        try
        {
            simulator.Draw(player, 1);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("模拟抽牌同步递归达到", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("同步抽牌递归触顶没有明确失败，而是返回了部分结果。");
    }
}
