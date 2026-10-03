using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRegentPotionCapBoundAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray())
            if (relic is not DivineRight) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            if (power is not ReattachPower) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_REGENT", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 35);
        InjectPotionForTest(player, "REGEN_POTION");
        InjectPotionForTest(player, "HEART_OF_IRON");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing || root.InitialRemainingHealingUpperBound != 15)
            throw new InvalidOperationException("用药次数界限夹具缺少已审查的再生剂量。");
        CombatPredictionSimulator parent = root.ForkSimulator();
        string before = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        int Bound(CombatPredictionSimulator sim, int? maximum, int postCombat = 0)
            => StrategicHpRecoveryBound.RemainingHealingUpperBound(sim, player, postCombat,
                maximumExplicitPotionUses: maximum);
        CombatPredictionSimulator Use(CombatPredictionSimulator source, int slot)
        {
            CombatPredictionSimulator child = source.Fork();
            var combat = (SimulatedCombatState)child.State.CombatState;
            PotionModel potion = combat.GetPotionAtSlot(player, slot)!;
            int historyStart = child.History.Entries.Count;
            combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            try
            {
                if (!PotionExecutionSupport.Prepare(child, combat, potion, slot, player.Creature)
                    || !PotionExecutionSupport.Complete(child, combat, potion, player.Creature, null,
                        historyStart, new ForkableSet<uint>())
                    || !CombatBeamSolver.SettleReplayActionBoundary(child, combat))
                    throw new InvalidOperationException("原版手动用药结算意外挂起。");
            }
            finally { combat.EndActionChoices(); }
            return child;
        }
        CombatPredictionSimulator heart;
        CombatPredictionSimulator both;
        using (SimulationNotificationIsolation.Enter())
        {
            if (Bound(parent, 0) != 0 || Bound(parent, 1) != 15 || Bound(parent, null) != 15)
                throw new InvalidOperationException("未用药根的0/1/无限额度错误。");
            heart = Use(parent, 1);
            var heartCombat = (SimulatedCombatState)heart.State.CombatState;
            if (heartCombat.PotionUses.Count != 1 || heartCombat.PotionUses[0].Automatic
                || Bound(heart, 1) != 0 || Bound(heart, 2) != 15 || Bound(heart, null) != 15
                || Bound(heart.Fork(), 1) != 0)
                throw new InvalidOperationException("已达到显式用药上限仍计入未用再生药水，或回退/Fork额度丢失。");
            CombatPredictionSimulator activeRegen = heart.Fork();
            ((SimulatedCombatState)activeRegen.State.CombatState).SetAmount<RegenPower>(player.Creature, 5);
            if (Bound(activeRegen, 1, 6) != 21)
                throw new InvalidOperationException("到达上限时已有再生与战后治疗被错误省略。");
            CombatPredictionSimulator automatic = parent.Fork();
            var automaticCombat = (SimulatedCombatState)automatic.State.CombatState;
            automaticCombat.ConsumePotion(automaticCombat.GetPotionAtSlot(player, 1)!);
            if (automaticCombat.PotionUses.Count != 1 || !automaticCombat.PotionUses[0].Automatic
                || Bound(automatic, 1) != 15)
                throw new InvalidOperationException("自动消耗错误占用了显式用药额度。");
            CombatPredictionSimulator regen = Use(parent, 0);
            if (Bound(regen, 1) != 15 || ((SimulatedCombatState)regen.State.CombatState).PotionUses.Count != 1)
                throw new InvalidOperationException("已用再生药水的活动能力治疗丢失。");
            both = Use(heart, 0);
            if (Bound(both, 2) != 15 || heartCombat.PotionUses.Count != 1
                || ((SimulatedCombatState)both.State.CombatState).PotionUses.Count != 2)
                throw new InvalidOperationException("两次显式用药计数或父子历史隔离错误。");
            if (DescribeContinuationContractState(parent, root, player) != before
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("次数界限检查修改了父分支、原生状态或RNG。");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (var (slot, expectedSim) in new[] { (1, heart), (0, both) })
        {
            PotionModel potion = player.GetPotionAtSlotIndex(slot)!;
            string expected = ContinuationStamp.CapturePredicted(player, expectedSim, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is UsePotionAction use && use.PotionIndex == (uint)slot
                    && ReferenceEquals(use.Player, player),
                () => potion.EnqueueManualUse(player.Creature), deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException($"原生药水槽{slot}的完整状态不一致。");
        }
        if (DescribeContinuationContractState(parent, root, player) != before
            || root.InitialRemainingHealingUpperBound != 15)
            throw new InvalidOperationException("原生用药修改了旧根或冻结元数据。");
        _completedChecks.Add("RegentPotionCapBound:ZeroOneUnlimited:ManualReplayExplicitCount:SpentHeartUnusedRegen:CapTwoFallback:AutomaticCountExcluded:ActiveRegenPreserved:ConsumedRegenPreserved:TwoManualUses:ForkHistoryIsolation:TwoNativePotionFullStates:ParentLiveRngIsolation:ImmutableRoot");
    }
}
