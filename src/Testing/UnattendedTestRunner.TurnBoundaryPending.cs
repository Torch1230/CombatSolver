using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertTurnStartChoiceCursorRunsNestedChoiceFirst()
    {
        PlanCardChoice nestedChoice = new(
            PlanChoiceEffect.Discard,
            PileType.Hand,
            [],
            SourceId: "NESTED");
        PlanCardChoice outerChoice = new(
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            [],
            SourceId: "OUTER");
        TurnStartChoiceRequest nestedRequest = new(
            "NESTED",
            PlanChoiceEffect.Discard,
            PileType.Hand,
            0);
        TurnStartChoiceRequest outerRequest = new(
            "OUTER",
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            0);

        TurnStartChoiceCursor nestedFirst = new([nestedChoice, outerChoice]);
        bool nestedConsumed = false;
        using (nestedFirst.BeforeNextTake(() =>
               {
                   nestedConsumed = nestedFirst.TryTake(nestedRequest, out PlanCardChoice? choice)
                       && ReferenceEquals(choice, nestedChoice);
                   return nestedConsumed;
               }))
        {
            if (!nestedFirst.TryTake(outerRequest, out PlanCardChoice? choice)
                || !ReferenceEquals(choice, outerChoice))
            {
                throw new InvalidOperationException("回合开始选择游标没有先消费同步触发的内层选择。");
            }
        }
        if (!nestedConsumed)
            throw new InvalidOperationException("回合开始选择游标没有运行消费前回调。");
        nestedFirst.AssertConsumed();

        TurnStartChoiceCursor suspended = new([outerChoice]);
        using (suspended.BeforeNextTake(static () => false))
        {
            if (suspended.TryTake(outerRequest, out _))
                throw new InvalidOperationException("消费前阶段挂起后仍消费了外层选择。");
        }
        if (!suspended.TryTake(outerRequest, out PlanCardChoice? resumedChoice)
            || !ReferenceEquals(resumedChoice, outerChoice))
        {
            throw new InvalidOperationException("消费前阶段挂起后无法恢复外层选择。");
        }
        suspended.AssertConsumed();

        TurnStartChoiceCursor empty = new(null);
        bool emptyCallbackRan = false;
        using (empty.BeforeNextTake(() =>
               {
                   emptyCallbackRan = true;
                   return true;
               }))
        {
            if (empty.TryTake(outerRequest, out _))
                throw new InvalidOperationException("空选择游标错误生成了外层选择。");
        }
        if (!emptyCallbackRan)
            throw new InvalidOperationException("空选择游标在报告缺失计划前没有运行消费前阶段。");
    }

    private static void AssertTurnStartChoicePreservesNestedPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);
        _ = simulatedCombat.AddPowerInstance<HellraiserPower>(
            player.Creature,
            1,
            player.Creature);
        _ = simulatedCombat.AddPowerInstance<DarkEmbracePower>(
            player.Creature,
            1,
            player.Creature);

        PredictedCard selected = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        PredictedCard nested = PredictedCard.Create(ModelDb.Card<SeekerStrike>(), player);
        PredictedCard option = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddGeneratedCardToCombat(
            selected,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            nested,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            option,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);

        const string sourceId = "TURN_START_OUTER";
        CardChoiceSpec spec = new(
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            1,
            1,
            [selected],
            playerState.Hand.Cards,
            ReplacementValue: 0d);
        PlanCardChoice outerChoice = CardChoiceSupport.BuildRequestedChoice(spec, ["__FIRST__"]) with
        {
            SourceId = sourceId,
        };
        TurnStartChoiceCursor choices = simulatedCombat.BeginActionChoices([outerChoice]);
        try
        {
            bool completed = TurnStartChoiceSupport.Resolve(
                simulator,
                simulatedCombat,
                player,
                choices,
                sourceId,
                PlanChoiceEffect.Exhaust,
                requestedCount: 1);
            if (completed)
                throw new InvalidOperationException("外层回合开始选择错误吞掉了同步触发的内层选择。");
            AssertPendingChoice(
                simulatedCombat,
                CanonicalModels.Power<HellraiserPower>().Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "回合开始外层选择");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertSideTurnStartPropagatesPendingChoice(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);
        HellraiserPower hellraiser = simulatedCombat.AddPowerInstance<HellraiserPower>(
            player.Creature,
            1,
            player.Creature);
        _ = simulatedCombat.AddPowerInstance<ViciousPower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard nested = PredictedCard.Create(ModelDb.Card<SeekerStrike>(), player);
        PredictedCard option = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddGeneratedCardToCombat(
            nested,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            option,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);

        Creature enemy = simulatedCombat.Enemies.First();
        simulatedCombat.Apply<VulnerablePower>(enemy, 1, player.Creature);
        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = simulatedCombat.TriggerSideTurnStart(
                simulator,
                CombatSide.Player,
                [player.Creature],
                decrementPlating: false);
            if (completed)
                throw new InvalidOperationException("阶段开始的 Power 变更产生选择后仍报告完成。");
            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "阶段开始 Power 变更");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertOrbTurnEndStopsAfterPendingChoice(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);
        StabilizeForkBoundaryEnemies(simulator);
        HellraiserPower hellraiser = simulatedCombat.AddPowerInstance<HellraiserPower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard nested = PredictedCard.Create(ModelDb.Card<SeekerStrike>(), player);
        PredictedCard option = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddGeneratedCardToCombat(
            nested,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            option,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        ReplaceRootRelicsForTurnBoundaryTest(
            simulatedCombat,
            player,
            (GremlinHorn)PredictionUtils.CreateRelic(CanonicalModels.Relic<GremlinHorn>(), player));

        Creature source = simulatedCombat.Enemies.First();
        Creature victim = MonsterSpawnSupport.Spawn<GasBomb>(
            simulator,
            simulatedCombat,
            source,
            slot: null,
            minion: true);
        simulator.State.GetCreature(victim).CurrentHp = 1;

        SimOrbQueue queue = playerState.OrbQueue;
        queue.Clear();
        queue.AddCapacity(2);
        GlassOrb first = (GlassOrb)ModelDb.Orb<GlassOrb>().ToMutable();
        first.Owner = player;
        first._passiveVal = 4m;
        GlassOrb sentinel = (GlassOrb)ModelDb.Orb<GlassOrb>().ToMutable();
        sentinel.Owner = player;
        sentinel._passiveVal = 17m;
        if (!queue.TryEnqueue(first) || !queue.TryEnqueue(sentinel))
            throw new InvalidOperationException("轨道挂起测试无法建立球队列。");

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = queue.BeforeTurnEnd(simulator);
            if (completed)
                throw new InvalidOperationException("球被动产生选择后仍报告回合末轨道阶段完成。");
            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "回合末轨道被动");
            if (sentinel._passiveVal != 17m)
                throw new InvalidOperationException("球被动挂起后仍执行了后续球。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void ReplaceRootRelicsForTurnBoundaryTest(
        SimulatedCombatState combat,
        Player player,
        params RelicModel[] relics)
    {
        FieldInfo relicsField = typeof(SimulatedCombatState).GetField(
            "_rootRelics",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SimulatedCombatState).FullName, "_rootRelics");
        if (relicsField.GetValue(combat) is not IDictionary<Player, RelicModel[]> rootRelics)
            throw new InvalidOperationException("回合边界挂起测试无法写入模拟遗物账本。");
        rootRelics[player] = relics;

        FieldInfo listenersField = typeof(SimulatedCombatState).GetField(
            "_rootHookListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SimulatedCombatState).FullName, "_rootHookListeners");
        AbstractModel[] existing = (AbstractModel[]?)listenersField.GetValue(combat)
            ?? throw new InvalidOperationException("回合边界挂起测试找不到根 Hook listener 快照。");
        AbstractModel[] replacement = existing
            .Where(listener => listener is not RelicModel relic
                || !ReferenceEquals(relic.Owner, player))
            .Concat(relics)
            .ToArray();
        listenersField.SetValue(combat, replacement);

        foreach (string cacheName in new[]
                 {
                     "_baseHookListeners",
                     "_effectiveHookListeners",
                     "_effectiveRunHookListeners",
                     "_effectivePowers",
                 })
        {
            FieldInfo cache = typeof(SimulatedCombatState).GetField(
                cacheName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(SimulatedCombatState).FullName, cacheName);
            cache.SetValue(combat, null);
        }
    }
}
