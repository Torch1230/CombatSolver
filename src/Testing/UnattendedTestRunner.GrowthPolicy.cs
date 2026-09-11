using MegaCrit.Sts2.Core.Combat;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertGrowthPolicyAsync(CombatState combat)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Growth policy: " + message);
        }
        SolverSettingsData original = SolverSettings.Current;
        GrowthValues budgets = new(1, 2, 3, 4, 5, 6, 7, 8);
        Check(new SolverSettingsData().GrowthBudgets == default, "default budgets");
        Check(SolverSettings.RoundTripForTesting(original with { GrowthBudgets = budgets }).GrowthBudgets == budgets,
            "settings round trip");
        foreach (string retiredMode in new[] { "Survival", "PermanentGrowth", "NetResources" })
        {
            string previousSettings = "{\"objective\":{\"mode\":\"" + retiredMode
                + "\",\"maximumBattleHpLoss\":0,\"minimumEndingHp\":100,\"growthTarget\":999},"
                + "\"growthBudgets\":{\"geneticAlgorithm\":7},\"ignoreLongTermRewards\":false}";
            SolverSettingsData migrated = SolverSettings.DeserializeForTesting(previousSettings);
            Check(migrated.GrowthBudgets.GeneticAlgorithm == 7 && !migrated.IgnoreLongTermRewards,
                "0.34.9 objective settings preserve the original per-source growth budget");
        }
        try
        {
            SolverSettings.ApplyForTesting(original with { GrowthBudgets = budgets });
            Check(SolverSettings.Capture().GrowthBudgets == budgets, "immutable capture");
            using SolverGrowthStrategyPanel panel = new();
            Check(panel.SettingsConfiguredForTesting, "sidebar reload");

            SolverSettings.ApplyForTesting(original with { GrowthBudgets = budgets, IgnoreLongTermRewards = true });
            Check(SolverSettings.Capture().IgnoreLongTermRewards, "immutable capture carries the ignore switch");
            using SolverGrowthStrategyPanel ignoringPanel = new();
            // SettingsConfiguredForTesting 同时核对开关状态和「额度被灰掉」，所以这一条就够。
            Check(ignoringPanel.SettingsConfiguredForTesting, "sidebar reloads with the switch on and budgets greyed out");
            Check(!ignoringPanel.ToggleIgnoreLongTermRewardsForTesting(), "clicking the switch flips it");
        }
        finally { SolverSettings.ApplyForTesting(original); }
        Check(await SolverOverlay.ExerciseGrowthPolicyUiForTesting(), "sidebar toggle, bounds and mutual exclusion");
        AssertThirdPartyGrowthSources(combat);

        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        SimulatedCombatState parent = (SimulatedCombatState)simulator.State.CombatState;
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
            parent.RecordGrowthReward(source);
        CombatPredictionSimulator fork = simulator.Fork();
        SimulatedCombatState child = (SimulatedCombatState)fork.State.CombatState;
        child.RecordGrowthReward(GrowthSource.GeneticAlgorithm);
        Check(parent.GrowthRewards.GeneticAlgorithm == 1 && child.GrowthRewards.GeneticAlgorithm == 2,
            "fork event isolation");
        Check(budgets.Credit(parent.GrowthRewards) == 36 && budgets.Credit(child.GrowthRewards) == 42,
            "each successful event earns its own budget");
        Check(SolverInterimResultOrdering.ComparePrimaryQuality(true, 0, 2, true, 0, 1, 2, 0) < 0,
            "budget equality prefers realized growth");
        Check(SolverInterimResultOrdering.ComparePrimaryQuality(true, 1, 2, true, 0, 1, 2, 0) > 0,
            "over-budget damage rejected");
        Check(SolverInterimResultOrdering.ComparePrimaryQuality(false, -100, 1, true, 0, 1, 100, 0) > 0,
            "growth never outranks survival and victory");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            GrowthBudgets = default,
            AcceptableBattleHpLoss = 100,
            PotionPolicy = SolverPotionPolicy.Disabled,
            FixedBudget = true,
            BudgetOverrideMilliseconds = 1500,
            MaxDegreeOfParallelism = 1,
            VerifyIncrementalSearch = true,
        };
        bool paidFixture = _request.ScenarioId == "GROWTH-POLICY-PAID";
        int allowance = paidFixture ? 100 : 2;
        SolverResult baseline = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        Check(baseline.Snapshot.GrowthHpCredit == 0 && baseline.Snapshot.GrowthRewards.GeneticAlgorithm == (paidFixture ? 0 : 1),
            "zero budget takes free growth and rejects paid growth");
        SearchPolicySnapshot growthPolicy = policy with { GrowthBudgets = new GrowthValues(GeneticAlgorithm: allowance) };
        Check(CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy with { HasGrowthTargets = false }, baseline), "no-target early stop");
        Check(!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy, baseline), "free growth overrides early stop");
        Check(!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(growthPolicy, baseline), "growth overrides early stop");
        SolverResult growth = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, growthPolicy, CancellationToken.None, null));
        Check(growth.Snapshot.AllEnemiesDead && !growth.Snapshot.PlayerDead, "growth route wins");
        Check(growth.Snapshot.GrowthRewards.GeneticAlgorithm == 1 && growth.Snapshot.GrowthHpCredit == allowance,
            $"permanent growth reached through search and replay: rewards={growth.Snapshot.GrowthRewards} credit={growth.Snapshot.GrowthHpCredit} actions={string.Join(',', growth.BestNode.Actions.Select(action => action.CardId))}");
        Check(growth.ProjectedBattleHpLost <= baseline.ProjectedBattleHpLost + allowance, "paid HP stays within earned credit");
        if (paidFixture)
            Check(growth.ProjectedBattleHpLost > baseline.ProjectedBattleHpLost, "paid fixture actually spends HP");

        // 「不考虑局外收益」：同一份额度，开关一开就不再拿血去换收益，也不再靠它们在 Beam 里保留路线。
        Check(!new SolverSettingsData().IgnoreLongTermRewards, "the ignore switch defaults to off");
        Check(SolverSettings.RoundTripForTesting(original with { IgnoreLongTermRewards = true }).IgnoreLongTermRewards,
            "the ignore switch round trips through settings");
        SearchPolicySnapshot ignoring = growthPolicy with { IgnoreLongTermRewards = true };
        Check(ignoring.GrowthBudgets == growthPolicy.GrowthBudgets && ignoring.EffectiveGrowthBudgets == default,
            "the raw budget is kept and only the effective one is zeroed");
        Check((policy with { HasGrowthTargets = true }).EffectiveHasGrowthTargets
            && !(policy with { HasGrowthTargets = true, IgnoreLongTermRewards = true }).EffectiveHasGrowthTargets,
            "ignoring takes growth targets back out");
        Check(CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(
                policy with { HasGrowthTargets = true, IgnoreLongTermRewards = true }, baseline),
            "ignoring re-enables the early stop that growth targets had switched off");
        SolverResult ignored = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, ignoring, CancellationToken.None, null));
        Check(ignored.Snapshot.AllEnemiesDead && !ignored.Snapshot.PlayerDead, "the ignoring route still wins");
        // 源头清零，不是下游逐处判断：局外收益还是终局排序键、保路泳道、必留泳道、Pareto 维度
        // 和 CompareFinalCandidates 的比较键，逐处列举漏过两次。免费夹具本来会拿到成长，所以这
        // 三项同时为零才说明是从源头清的。
        Check(ignored.Snapshot.LongTermResourceValue == 0
            && ignored.Snapshot.GrowthRewards.Total == 0
            && ignored.Snapshot.GrowthHpCredit == 0,
            $"ignoring zeroes long-term resource and growth counts at the source: "
                + $"resource={ignored.Snapshot.LongTermResourceValue} rewards={ignored.Snapshot.GrowthRewards} credit={ignored.Snapshot.GrowthHpCredit}");
        Check(ignored.ProjectedBattleHpLost <= baseline.ProjectedBattleHpLost,
            $"ignoring never pays more HP than the zero-budget baseline: {ignored.ProjectedBattleHpLost} vs {baseline.ProjectedBattleHpLost}");
        if (paidFixture)
        {
            Check(ignored.ProjectedBattleHpLost < growth.ProjectedBattleHpLost,
                $"ignoring gives up the paid growth a full budget would have bought: "
                    + $"{ignored.ProjectedBattleHpLost} vs {growth.ProjectedBattleHpLost}");
        }

        Entry.Logger.Info($"[CombatSolver/Test] GROWTH_POLICY_OK baseline_hp={baseline.ProjectedBattleHpLost} baseline_turn={baseline.CombatEndedTurn} growth_hp={growth.ProjectedBattleHpLost} growth_turn={growth.CombatEndedTurn} credit={growth.Snapshot.GrowthHpCredit}");
    }
}
