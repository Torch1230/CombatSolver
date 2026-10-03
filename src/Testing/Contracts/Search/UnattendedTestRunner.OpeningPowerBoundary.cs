using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOpeningPowerBoundaryAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 50);
        await InjectCardAsync(combat, player, new() { CardId = "PREPARED", Pile = "Hand" });
        await InjectCardAsync(combat, player, new() { CardId = "PANACHE", Pile = "Hand" });
        await InjectCardAsync(combat, player, new()
        {
            CardId = "STRIKE_SILENT", Pile = "Hand", DynamicVars = new() { ["Damage"] = 100 },
        });
        await InjectCardAsync(combat, player, new() { CardId = "WOUND", Pile = "Draw", Count = 2 });
        SetEnergy(player, 3);

        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, DetailedDiagnostics = false,
            MaxDegreeOfParallelism = 1, BudgetOverrideMilliseconds = 5000,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
            Profile = SolverSearchProfile.Default with { BeamWidth = 8, MaxExpandedNodes = 100 },
        };
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat), policy, searchProfile: policy.Profile);
        if (!driver.BuildPowerActionsAfterPrefix([]).Any(action => action.CardId == "PANACHE"))
            throw new InvalidOperationException("正常状态未保留可出能力牌探针。");

        PlanAction prepared = new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "PREPARED");
        SimulationSnapshot pending = InvokeForcedTerminalReplay(driver, [prepared], null, 0, null);
        try
        {
            if (pending.BoundaryReason != SearchBoundaryReason.PendingChoice)
                throw new InvalidOperationException("前缀未抵达原生早有准备的选牌边界。");
        }
        finally { pending.ReleaseSimulator(); }
        if (driver.BuildPowerActionsAfterPrefix([prepared]).Count != 0)
            throw new InvalidOperationException("未决选牌前缀仍产生能力牌探针。");

        PlanAction strike = new(PlanActionKind.PlayCard, root.StartTurnNumber,
            CardId: "STRIKE_SILENT", TargetCombatId: combat.Enemies.Single().CombatId);
        SimulationSnapshot victory = InvokeForcedTerminalReplay(driver, [strike], null, 0, null);
        try
        {
            if (!victory.AllEnemiesDead)
                throw new InvalidOperationException("终局前缀未击败敌人。");
        }
        finally { victory.ReleaseSimulator(); }
        if (driver.BuildPowerActionsAfterPrefix([strike]).Count != 0)
            throw new InvalidOperationException("已胜利前缀仍产生能力牌探针。");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore
            || !driver.BuildPowerActionsAfterPrefix([]).Any(action => action.CardId == "PANACHE"))
            throw new InvalidOperationException("能力牌边界检查改变了 live 或根状态。");
        _completedChecks.Add("OpeningPowerBoundary:Normal:PendingChoice:Victory:RootAndLiveUnchanged");
    }
}
