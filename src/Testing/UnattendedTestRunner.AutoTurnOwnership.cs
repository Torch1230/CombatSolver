using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertAutoTurnRequestOwnershipAsync(CombatState combat, Player player)
    {
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 3);
        var host = NGame.Instance!;
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        long deadline = Environment.TickCount64 + 10_000;
        while (SolverController.IsSearching)
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Turn ownership fixture search exceeded 10 seconds.");
            await NextFrameAsync();
        }
        var result = SolverController.CurrentResultForBugReport ?? throw new InvalidOperationException("Turn ownership fixture has no plan.");
        string audit = SolverController.ReplanAuditForBugReport;
        // A late turn-start callback sees state after an action, not the original root.
        SetEnergy(player, 1);
        SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
        if (SolverController.IsSearching || !ReferenceEquals(SolverController.CurrentResultForBugReport, result)
            || SolverController.ReplanAuditForBugReport != audit)
            throw new InvalidOperationException("Late automatic turn request replaced the current turn plan.");
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching) throw new InvalidOperationException("Explicit recalculation was suppressed.");
        SolverController.CancelSearchForTesting();
        _completedChecks.Add("AutoTurnOwnership:LateCallbackPreservesPlan:ManualRecalculationAllowed");
    }

    private async Task AssertDeploymentTurnRequestOwnershipAsync(CombatState combat, Player player)
    {
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 10);
        await ClearPlayerPilesAsync(player);
        for (int index = 0; index < 2; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 3);
        var host = NGame.Instance!;
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        long deadline = Environment.TickCount64 + 10_000;
        while (SolverController.IsSearching)
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Deployment ownership search exceeded 10 seconds.");
            await NextFrameAsync();
        }
        string audit = SolverController.ReplanAuditForBugReport;
        SolverController.RequestDeploy(host, combat);
        if (!SolverController.IsDeploying) throw new InvalidOperationException("Expected an active native deployment.");
        SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
        while (SolverController.IsDeploying)
        {
            string countsBefore = audit.Split(" last_solver_deployed_turn=")[0];
            string countsAfter = SolverController.ReplanAuditForBugReport.Split(" last_solver_deployed_turn=")[0];
            if (SolverController.IsSearching || countsBefore != countsAfter)
                throw new InvalidOperationException($"Late automatic request replaced active deployment: before={countsBefore}; after={countsAfter}");
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Deployment ownership execution exceeded 10 seconds.");
            await NextFrameAsync();
        }
        if (SolverController.IsSearching || combat.Enemies.Any(enemy => enemy.IsAlive))
            throw new InvalidOperationException("Late automatic request interrupted native deployment or started another search.");
        _completedChecks.Add("AutoTurnOwnership:NativeDeploymentCompleted:NoExtraSearch");
    }
}
