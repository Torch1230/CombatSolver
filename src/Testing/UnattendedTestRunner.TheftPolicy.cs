using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertTheftRecoveryPolicyAsync(CombatState combat)
    {
        AssertRequiredPotionAuditSelectionAndTotals();
        SolverInterimResult escape = new(true, 20, 0, 0, 0, 0, 0, 100, 2)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        SolverInterimResult recovered = new(true, 0, 15, 15, 18, 2, 0, 0, 4)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        if (!SolverInterimResultOrdering.IsBetter(recovered, escape)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(recovered, escape)
            || SolverInterimResultOrdering.IsBetter(escape, recovered)
            || CombatSearchCoordinator.IsBetterPotionPolicyResult(SolverTheftPolicy.LetEscape, recovered, escape)
            || SolverInterimResultOrdering.IsBetter(recovered with { TheftPolicy = SolverTheftPolicy.LetEscape },
                escape with { TheftPolicy = SolverTheftPolicy.LetEscape })
            || TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.PreserveResources, 20)
            || !TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.LetEscape, 20)
            || SolverInterimResultOrdering.IsBetter(recovered with { Won = false }, escape))
            throw new InvalidOperationException("Theft policy mixed recovery, HP, potion, early-stop or victory priorities.");

        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
            includeTurnSetup: false, theftPolicy: SolverTheftPolicy.PreserveResources);
        SolverSearchProfile profile = policy.Profile with { MaxExpandedNodes = 256, SoftTimeBudgetMilliseconds = 1500 };
        policy = policy with
        {
            Profile = profile, FixedBudget = true,
            BudgetOverrideMilliseconds = 1500,
            VerifyIncrementalSearch = false,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        foreach (SolverTheftPolicy theft in Enum.GetValues<SolverTheftPolicy>())
        {
            SearchPolicySnapshot selectedPolicy = policy with { TheftPolicy = theft };
            SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                selectedPolicy, CancellationToken.None, progressCallback: null));
            if (theft == SolverTheftPolicy.PreserveResources && result.OutstandingStolenResource > 0
                && CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(selectedPolicy, result))
                throw new InvalidOperationException("Unrecovered loot incorrectly satisfied HP early stop.");
            Entry.Logger.Info($"[CombatSolver/Test] THEFT_RECOVERY_POLICY policy={theft} outstanding={result.OutstandingStolenResource} hp_loss={result.ProjectedBattleHpLost} potions={result.PotionCount} ended={result.CombatEndedTurn}");
        }
    }
}
