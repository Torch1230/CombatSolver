using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// #182/T025：玩家侧回合结束 AfterSideTurnEnd 的逐监听器分发。
    /// 原版 <c>Hook.AfterSideTurnEnd</c> 按 <c>combatState.IterateHookListeners()</c> 的顺序逐个调用监听器，
    /// 每个监听器在自己的实现里读取当时的 Focus：<c>ConsumingShadowPower.AfterSideTurnEnd</c> 激发末球时，
    /// <c>HotfixPower.AfterSideTurnEnd</c>（TemporaryFocusPower）可能还没有回收临时 Focus。
    /// 本夹具把 ConsumingShadow 施加在 Hotfix 之前，让监听顺序把它排在前面，再逐阶段比较 Focus、
    /// 球队列、敌人 HP 与 RNG：先激发（Focus 仍为 2）与先回收（Focus 为 0）相差正好 2 点激发伤害。
    /// </summary>
    private async Task AssertTurnEndFocusEvokeOrderAsync(CombatState combat, Player player)
    {
        bool reverse = _request.ScenarioId.EndsWith("-REVERSE", StringComparison.Ordinal);
        bool sentinel = _request.ScenarioId.EndsWith("-SENTINEL", StringComparison.Ordinal);
        Creature enemy = combat.Enemies[0];

        // 去掉夹具无关的监听器（遗物、既有 Power、牌堆、既有球），只保留本夹具注入的能力与一颗闪电球。
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel existing in combat.Creatures.SelectMany(static creature => creature.Powers).ToArray())
            await PowerCmd.Remove(existing);
        await ClearPlayerPilesAsync(player);
        // OrbQueue.Clear() 会把 Capacity 归零，这里只逐个移除充能球，保留 Defect 的球槽容量。
        foreach (OrbModel orb in player.PlayerCombatState!.OrbQueue.Orbs.ToArray())
            player.PlayerCombatState.OrbQueue.Remove(orb);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        var choiceContext = new BlockingPlayerChoiceContext();
        await OrbCmd.Channel(choiceContext, ResolveOrbForTest("LIGHTNING_ORB").ToMutable(), player);
        if (sentinel || !reverse)
        {
            await PowerCmd.Apply<ConsumingShadowPower>(choiceContext, player.Creature, 1, player.Creature, null);
            if (!sentinel)
                await PowerCmd.Apply<HotfixPower>(choiceContext, player.Creature, 2, player.Creature, null);
        }
        else
        {
            await PowerCmd.Apply<HotfixPower>(choiceContext, player.Creature, 2, player.Creature, null);
            await PowerCmd.Apply<ConsumingShadowPower>(choiceContext, player.Creature, 1, player.Creature, null);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        string listenerOrder = string.Join('|', player.Creature.Powers.Select(static power => power.GetType().Name));
        int shadowIndex = IndexOfPower<ConsumingShadowPower>(player.Creature);
        int hotfixIndex = IndexOfPower<HotfixPower>(player.Creature);
        // 原版按 Creature.Powers 的施加顺序分发：ConsumingShadowPower 必须排在 HotfixPower 之前
        // （TemporaryFocusPower.BeforeApplied 会先插入 FocusPower，所以监听序列里 FocusPower 位于两者之间）。
        bool orderMatches = sentinel
            ? shadowIndex >= 0 && hotfixIndex < 0
            : reverse
                ? hotfixIndex >= 0 && shadowIndex > hotfixIndex
                : shadowIndex >= 0 && hotfixIndex > shadowIndex;
        if (!orderMatches)
        {
            throw new InvalidOperationException(
                $"回合结束焦点/激发顺序夹具的原生监听顺序为 {listenerOrder}，ConsumingShadow={shadowIndex} Hotfix={hotfixIndex}，不符合预期相对顺序。");
        }
        int expectedFocus = sentinel ? 0 : 2;
        int focusBefore = player.Creature.GetPowerAmount<FocusPower>();
        if (focusBefore != expectedFocus)
        {
            throw new InvalidOperationException(
                $"回合结束焦点/激发顺序夹具的初始 Focus 为 {focusBefore}，预期 {expectedFocus}。");
        }
        int orbCount = player.PlayerCombatState!.OrbQueue.Orbs.Count;
        int orbCapacity = player.PlayerCombatState.OrbQueue.Capacity;
        if (orbCount != 1 || orbCapacity < 1)
        {
            throw new InvalidOperationException(
                $"回合结束焦点/激发顺序夹具的初始球数为 {orbCount}（容量 {orbCapacity}），预期 1 颗且容量至少 1。");
        }

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        CombatPredictionSimulator fork = simulator.Fork();
        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        string predictedOrder = string.Join('|', shadow.EffectivePowers().Select(static power => power.GetType().Name));
        if (predictedOrder != listenerOrder)
        {
            throw new InvalidOperationException(
                $"回合结束焦点/激发顺序夹具的预测监听顺序与原生不同：native={listenerOrder}，predicted={predictedOrder}。");
        }

        List<TurnEndFocusOrderStage> stages =
        [
            CaptureStage("Root", simulator, shadow, player, enemy),
        ];
        // 阶段证据：原版监听顺序下先激发的伤害（ConsumingShadow 排在 Hotfix 之前时 Focus 仍为 2）。
        CombatPredictionSimulator evokeFirstSimulator = root.ForkSimulator();
        SimulatedCombatState evokeFirstState = (SimulatedCombatState)evokeFirstSimulator.State.CombatState;
        EvokeLastOrb(evokeFirstSimulator, evokeFirstState, player);
        stages.Add(CaptureStage("EvokeBeforeFocusRetire", evokeFirstSimulator, evokeFirstState, player, enemy));
        // 阶段证据：现行固定顺序先回收临时 Focus。
        CombatPredictionSimulator retireFirstSimulator = root.ForkSimulator();
        SimulatedCombatState retireFirstState = (SimulatedCombatState)retireFirstSimulator.State.CombatState;
        retireFirstState.RestoreTemporaryFocus();
        PowerLifecycleSupport.ResolvePowerAmountChanges(retireFirstSimulator, retireFirstState);
        stages.Add(CaptureStage("FocusRetiredFirst", retireFirstSimulator, retireFirstState, player, enemy));
        CombatPredictionSimulator retireThenEvoke = retireFirstSimulator.Fork();
        SimulatedCombatState retireThenEvokeState = (SimulatedCombatState)retireThenEvoke.State.CombatState;
        EvokeLastOrb(retireThenEvoke, retireThenEvokeState, player);
        stages.Add(CaptureStage("EvokeAfterFocusRetire", retireThenEvoke, retireThenEvokeState, player, enemy));

        if (!PlayerTurnEndLifecycle.RunPhaseTwo(simulator, shadow, [player.Creature]))
            throw new InvalidOperationException("回合结束焦点/激发顺序夹具在预测回合结束遇到玩家选择。");
        MoveStateSnapshot predicted = CaptureSimulated(simulator, shadow, player, enemy);
        stages.Add(CaptureStage("SolverTurnEnd", simulator, shadow, player, enemy));
        if (!PlayerTurnEndLifecycle.RunPhaseTwo(fork, forkState, [player.Creature]))
            throw new InvalidOperationException("回合结束焦点/激发顺序夹具在 Fork 回合结束遇到玩家选择。");
        AssertSnapshotEqual(predicted, CaptureSimulated(fork, forkState, player, enemy), "TurnEndFocusOrder", "Fork");
        if (focusBefore != player.Creature.GetPowerAmount<FocusPower>())
        {
            throw new InvalidOperationException(
                $"回合结束焦点/激发顺序夹具改变了原生 Focus：{focusBefore} -> {player.Creature.GetPowerAmount<FocusPower>()}。");
        }

        await Hook.AfterSideTurnEnd(combat, CombatSide.Player, [player.Creature]);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        stages.Add(DescribeStage("NativeAfterSideTurnEnd", CaptureActual(combat, player, enemy)));
        WriteTurnEndFocusOrderEvidence(stages);
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] TURN_END_FOCUS_ORDER run_id={_request.RunId} scenario={_request.ScenarioId} "
            + string.Join(" | ", stages.Select(static stage => stage.ToString())));
        AssertSnapshotEqual(predicted, CaptureActual(combat, player, enemy), "TurnEndFocusOrder", "NativeAfterSideTurnEnd");
        _completedChecks.Add("TurnEndFocusOrder:" + (sentinel ? "PlainOrbSentinel" : reverse ? "HotfixFirst" : "ConsumingShadowFirst")
            + ":ListenerOrder:StagedFocus:OrbQueue:EnemyHp:Rng:ForkIsolation:NativeAfterSideTurnEnd");
    }

    private static int IndexOfPower<T>(Creature owner) where T : PowerModel
    {
        for (int index = 0; index < owner.Powers.Count; index++)
        {
            if (owner.Powers[index] is T)
                return index;
        }
        return -1;
    }

    private static void EvokeLastOrb(CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player)
    {
        OrbModel? orb = simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.LastOrDefault();
        if (orb is null)
            throw new InvalidOperationException("回合结束焦点/激发顺序夹具缺少末球。");
        simulator.OrbEvoke(player, orb);
    }

    private TurnEndFocusOrderStage CaptureStage(
        string name, CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player, Creature enemy)
        => DescribeStage(name, CaptureSimulated(simulator, combat, player, enemy));

    private static TurnEndFocusOrderStage DescribeStage(string name, MoveStateSnapshot snapshot)
        => new(
            name,
            snapshot.PlayerPowers.GetValueOrDefault("FOCUS_POWER"),
            string.Join(',', snapshot.PlayerPowers.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}")),
            $"{snapshot.PlayerOrbState}|" + string.Join(',', snapshot.PlayerOrbs.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}")),
            snapshot.EnemyHp,
            string.Join(',', snapshot.RngCounters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}")));

    private void WriteTurnEndFocusOrderEvidence(IReadOnlyList<TurnEndFocusOrderStage> stages)
    {
        if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            return;
        Directory.CreateDirectory(_request.EvidenceDirectory);
        File.WriteAllText(
            Path.Combine(_request.EvidenceDirectory, "turn-end-focus-order.json"),
            JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record TurnEndFocusOrderStage(
        string Name, int Focus, string Powers, string Orbs, int EnemyHp, string Rng)
    {
        public override string ToString()
            => $"{Name}:focus={Focus},enemy_hp={EnemyHp},orbs={Orbs},powers={Powers},rng={Rng}";
    }
}
