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
}
