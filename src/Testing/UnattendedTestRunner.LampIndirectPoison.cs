using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLampIndirectPoisonAsync(CombatState combat, Player player)
    {
        foreach (string sourcePower in _request.ScenarioId == "LAMP-INDIRECT-TEMPORARY-STRENGTH"
                     ? new[] { "MONARCHS_GAZE_POWER" }
                     : new[] { "ENVENOM_POWER", "CONCOCT_POWER" })
        {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<UnsettlingLamp>().ToMutable());
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 100);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = sourcePower, Target = "Player", Amount = 2 });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEADLY_POISON" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        SetEnergy(player, 3);
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var actions = new List<PlanAction>();
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEADLY_POISON" })
        {
            actions.Add(new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: id, TargetCombatId: combat.Enemies[0].CombatId));
            var prediction = InvokeForcedTerminalReplay(driver, actions.ToArray(), null, 0, null);
            try
            {
                if (!FindActualHandCard(player, id, 0).TryManualPlay(combat.Enemies[0])) throw new InvalidOperationException("Lamp fixture native play failed.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var expected = ContinuationStamp.CapturePredicted(player, prediction.Simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
                var actual = ContinuationStamp.CaptureLive(combat);
                if (expected.StateText != actual.StateText)
                    throw new InvalidOperationException("Lamp source mismatch: " + string.Join("; ", expected.DescribeDifferences(actual)));
            }
            finally { prediction.ReleaseSimulator(); }
        }
        _completedChecks.Add($"LampIndirectPoison:{sourcePower}:PreservesCharge:DirectPoisonDoubles:FullContinuationState");
        }
    }

    /// <summary>
    /// 不安油灯不该被「打在已被同一张牌打死、并因此移出战斗的目标身上」的减益触发。
    /// </summary>
    /// <remarks>
    /// 实机 <c>CreatureCmd.Kill</c> 在击杀当时就移除个体，中和随后那条
    /// <c>PowerCmd.Apply&lt;WeakPower&gt;</c> 在 <c>!target.CanReceivePowers</c> 上直接返回，
    /// 油灯因此没有触发牌、计数保持 0（问题包 `24b8f299` 的
    /// <c>relicCounters expected={UNSETTLING_LAMP/1/0} actual={UNSETTLING_LAMP/0/0}</c>）。
    /// 预测侧曾经只看死亡阶段，而死亡效果要到 <c>ApplyEnemyDeathPowers</c> 才结算，
    /// 于是把这一层虚弱也施加了、还顺手点亮了油灯。
    /// </remarks>
    private async Task AssertLampDebuffOnKilledTargetAsync(CombatState combat, Player player)
    {
        if (combat.Enemies.Count < 2)
        {
            throw new InvalidOperationException(
                "LAMP-DEBUFF-ON-KILL 需要至少两个敌人：被杀死的那个不能同时结束战斗。");
        }

        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<UnsettlingLamp>().ToMutable());
        // 中和的 3 点伤害必须真的打死目标：否则实机也会正常挂上虚弱，这条夹具就失去意义。
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "NEUTRALIZE", Pile = "Hand" });
        SetEnergy(player, 3);

        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var actions = new List<PlanAction>
        {
            new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "NEUTRALIZE", TargetCombatId: combat.Enemies[0].CombatId),
        };
        var prediction = InvokeForcedTerminalReplay(driver, actions.ToArray(), null, 0, null);
        try
        {
            if (!FindActualHandCard(player, "NEUTRALIZE", 0).TryManualPlay(combat.Enemies[0]))
                throw new InvalidOperationException("Lamp kill fixture native play failed.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var expected = ContinuationStamp.CapturePredicted(player, prediction.Simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
            var actual = ContinuationStamp.CaptureLive(combat);
            if (expected.StateText != actual.StateText)
                throw new InvalidOperationException("Lamp kill mismatch: " + string.Join("; ", expected.DescribeDifferences(actual)));
        }
        finally { prediction.ReleaseSimulator(); }

        _completedChecks.Add("LampDebuffOnKilledTarget:SkipsDebuffOnRemovedTarget:KeepsCharge:FullContinuationState");
    }
}
