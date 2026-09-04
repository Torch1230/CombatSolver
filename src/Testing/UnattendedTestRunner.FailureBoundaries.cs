using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using STS2RitsuLib.Cards.DynamicVars;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class TrackingComputedDynamicVar : IComputedDynamicVar
    {
        public bool CalculateCalled { get; private set; }

        public decimal Calculate(Creature? target)
        {
            CalculateCalled = true;
            throw new InvalidOperationException("Computed dynamic variable native evaluator was called.");
        }
    }

    private static void AssertPredictionFailureBoundaries(CombatState combat, Player player)
    {
        CardModel card = player.PlayerCombatState?.Hand.Cards.FirstOrDefault()
            ?? throw new InvalidOperationException("失败边界测试要求手牌中至少有一张牌。");
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        TrackingComputedDynamicVar computed = new();
        try
        {
            computed.InvokeCalculate(
                simulator,
                new PredictedCard(card),
                combat.Enemies.FirstOrDefault());
            throw new InvalidOperationException("未知计算型动态变量没有显式失败。");
        }
        catch (PredictionUnsupportedException ex)
        {
            if (!ex.Message.Contains(card.Id.Entry, StringComparison.Ordinal)
                || !ex.Message.Contains(nameof(TrackingComputedDynamicVar), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("计算型动态变量失败缺少卡牌或变量类型上下文。", ex);
            }
        }
        if (computed.CalculateCalled)
            throw new InvalidOperationException("未知计算型动态变量仍调用了原生求值器。");

        bool scoreEvaluatorCalled = false;
        ComputedDynamicVar computedDamage = new(
            "Damage",
            17m,
            _ =>
            {
                scoreEvaluatorCalled = true;
                return 99m;
            });
        DynamicVarSet computedVars = new([computedDamage]);
        if (CardChoiceSupport.DynamicVarBaseValue(computedVars, "Damage") != 17d)
            throw new InvalidOperationException("选牌估值没有读取计算型动态变量的基础值。");
        if (scoreEvaluatorCalled)
            throw new InvalidOperationException("选牌估值错误调用了计算型动态变量的实机求值器。");

        IncompatibleGameplayModException incompatible = new(
            "Watcher",
            "The Watcher [Test]",
            "WatcherMod.WatcherEnchantStackHookProxy",
            "combat");
        string playerMessage = SolverController.FormatSearchSetupFailure(incompatible);
        if (!playerMessage.Contains("The Watcher ［Test］（Watcher）", StringComparison.Ordinal)
            || !playerMessage.Contains("不兼容的第三方 Mod", StringComparison.Ordinal)
            || !playerMessage.Contains("建议卸载", StringComparison.Ordinal)
            || !playerMessage.Contains(SolverUiTokens.BugReportUploadInstruction, StringComparison.Ordinal)
            || playerMessage.Contains("WatcherEnchantStackHookProxy", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("第三方玩法 Mod 初始化失败提示缺少名称、卸载建议或上传入口。");
        }
        if (!incompatible.Message.Contains("WatcherEnchantStackHookProxy", StringComparison.Ordinal)
            || !incompatible.Message.Contains("Watcher", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("第三方玩法 Mod 初始化失败日志缺少 Mod 或订阅器上下文。");
        }
        AssertModHookSubscriberInertness();

        bool firstInferredActionRan = false;
        InvalidOperationException inferredFailure = new("inferred-action-failure");
        try
        {
            CardOnPlayInferrer.ExecuteInferredActions(
                [
                    (_, _) => firstInferredActionRan = true,
                    (_, _) => throw inferredFailure,
                ],
                null!,
                null!);
            throw new InvalidOperationException("推断动作失败没有向外传播。");
        }
        catch (InvalidOperationException ex) when (ReferenceEquals(ex, inferredFailure))
        {
        }
        if (!firstInferredActionRan)
            throw new InvalidOperationException("推断动作失败测试没有执行前置动作。");

        AssertSearchTransitionFailure(new PlanAction(
            PlanActionKind.PlayCard,
            Turn: 1,
            CardId: "FAILURE_CARD"));
        AssertSearchTransitionFailure(new PlanAction(
            PlanActionKind.UsePotion,
            Turn: 1,
            PotionSlot: 0,
            PotionId: "FAILURE_POTION"));
        AssertExpectedSearchTransitionExceptionsPassThrough();
    }

    // 只覆写战斗外 hook 的订阅器（例如通过 RitsuLib HookedSingletonModel(HookType.Run) 注册、
    // 只做地图恢复的模型）必须被视为与战斗无关；任何战斗 hook 覆写，包括从基类继承来的，
    // 都必须继续被拒绝。
    private class MapOnlySubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => false;
        public override MegaCrit.Sts2.Core.Map.ActMap ModifyGeneratedMapLate(
            MegaCrit.Sts2.Core.Runs.IRunState runState,
            MegaCrit.Sts2.Core.Map.ActMap map,
            int actIndex) => map;
        public override Task AfterMapGenerated(MegaCrit.Sts2.Core.Map.ActMap map, int actIndex) => Task.CompletedTask;
    }

    private sealed class MapAndCombatSubscriber : MapOnlySubscriber
    {
        public override Task AfterCardPlayed(
            MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext,
            MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay) => Task.CompletedTask;
    }

    private class CombatBaseSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => true;
        public override Task AfterCardPlayed(
            MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext,
            MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay) => Task.CompletedTask;
    }

    private sealed class MapOnlyOverCombatBaseSubscriber : CombatBaseSubscriber
    {
        public override Task AfterMapGenerated(MegaCrit.Sts2.Core.Map.ActMap map, int actIndex) => Task.CompletedTask;
    }

    // 战斗开始 hook 早于根捕获执行且从不被镜像；这个形状就是 RitsuLib HookedSingletonModel(HookType.Combat)
    // 常见的"只在自定义遭遇战开始时布置一次"的泛型基类 + 具体子类。
    private abstract class CombatStartOnlyBase<TMarker> : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => true;
        public sealed override Task BeforeCombatStart() => Task.CompletedTask;
    }

    private sealed class CombatStartOnlySubscriber : CombatStartOnlyBase<string>
    {
    }

    private sealed class CombatStartAndVictorySubscriber : CombatStartOnlyBase<long>
    {
        public override Task AfterCombatVictory(MegaCrit.Sts2.Core.Rooms.CombatRoom room) => Task.CompletedTask;
    }

    private sealed class CombatStartAndTurnSubscriber : CombatStartOnlyBase<int>
    {
        public override Task AfterPlayerTurnStart(
            MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext,
            MegaCrit.Sts2.Core.Entities.Players.Player player) => Task.CompletedTask;
    }

    private sealed class RoomEnteredSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => false;
        public override Task AfterRoomEntered(MegaCrit.Sts2.Core.Rooms.AbstractRoom room) => Task.CompletedTask;
    }

    private sealed class NoOverrideSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => true;
    }

    private abstract class AbstractSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => false;
    }

    private static void AssertModHookSubscriberInertness()
    {
        AssertInert(typeof(MapOnlySubscriber), expected: true, "只覆写地图 hook 的订阅器应视为与战斗无关");
        AssertInert(typeof(NoOverrideSubscriber), expected: true, "没有覆写任何 hook 的订阅器应视为与战斗无关");
        AssertInert(typeof(MapAndCombatSubscriber), expected: false, "同时覆写战斗 hook 的订阅器必须被拒绝");
        AssertInert(typeof(MapOnlyOverCombatBaseSubscriber), expected: false, "从基类继承战斗 hook 覆写的订阅器必须被拒绝");
        AssertInert(typeof(RoomEnteredSubscriber), expected: false, "AfterRoomEntered 在战斗房也会触发，必须被拒绝");
        AssertInert(typeof(CombatStartOnlySubscriber), expected: true, "只覆写战斗开始 hook 的泛型基类子类应视为与预测无关");
        AssertInert(typeof(CombatStartAndVictorySubscriber), expected: true, "战斗开始加战斗胜利 hook 都在搜索窗口之外，应视为与预测无关");
        AssertInert(typeof(CombatStartAndTurnSubscriber), expected: false, "战斗开始之外还覆写回合 hook 的订阅器必须被拒绝");
        AssertInert(typeof(AbstractSubscriber), expected: false, "抽象类型无法判定，必须被拒绝");
        AssertInert(typeof(string), expected: false, "非 AbstractModel 类型必须被拒绝");

        if (!PredictionModHookSubscriberInertness.IsCombatInert(typeof(MapOnlySubscriber), out string hooks)
            || hooks != "AfterMapGenerated,ModifyGeneratedMapLate")
        {
            throw new InvalidOperationException($"订阅器覆写清单不正确：{hooks}");
        }
        PredictionModHookSubscriberInertness.IsCombatInert(typeof(MapOnlyOverCombatBaseSubscriber), out hooks);
        if (hooks != "AfterCardPlayed,AfterMapGenerated")
            throw new InvalidOperationException($"继承的覆写没有被收集：{hooks}");
    }

    private static void AssertInert(Type type, bool expected, string message)
    {
        if (PredictionModHookSubscriberInertness.IsCombatInert(type, out string hooks) != expected)
            throw new InvalidOperationException($"{message}（{type.Name}，覆写：{hooks}）。");
    }

    private static void AssertSearchTransitionFailure(PlanAction action)
    {
        StateFingerprint parentState = new(11, 29);
        InvalidOperationException transitionFailure = new("transition-failure");
        try
        {
            SearchTransitionGuard.Execute<int>(
                action,
                parentState,
                parentActionCount: 3,
                () => throw transitionFailure);
            throw new InvalidOperationException($"{action.Kind} 回放失败没有终止搜索转移。");
        }
        catch (SearchTransitionException ex)
        {
            if (!ReferenceEquals(ex.InnerException, transitionFailure)
                || ex.Action != action
                || ex.ParentState != parentState
                || ex.ParentActionCount != 3)
            {
                throw new InvalidOperationException($"{action.Kind} 回放失败上下文不完整。", ex);
            }
        }
    }

    private static void AssertExpectedSearchTransitionExceptionsPassThrough()
    {
        PlanAction action = new(PlanActionKind.EndTurn, Turn: 1);
        try
        {
            SearchTransitionGuard.Execute<int>(
                action,
                default,
                0,
                static () => throw new OperationCanceledException("canceled"));
            throw new InvalidOperationException("取消异常没有传播。");
        }
        catch (OperationCanceledException)
        {
        }

        InvalidPlannedChoiceBranchException invalidChoice = new("invalid-choice");
        try
        {
            SearchTransitionGuard.Execute<int>(
                action,
                default,
                0,
                () => throw invalidChoice);
            throw new InvalidOperationException("无效选择异常没有传播。");
        }
        catch (InvalidPlannedChoiceBranchException ex) when (ReferenceEquals(ex, invalidChoice))
        {
        }
    }
}
