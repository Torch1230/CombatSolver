using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertFixedPrefixTurnLossAsync(CombatState combat, Player player)
    {
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection
        {
            CardId = "BLOODLETTING",
            Pile = "Hand",
        });
        await InjectCardAsync(combat, player, new UnattendedCardInjection
        {
            CardId = "STRIKE_IRONCLAD",
            Pile = "Draw",
        });

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null);
        PlanAction[] prefix =
        [
            new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "BLOODLETTING"),
            new(PlanActionKind.EndTurn, root.StartTurnNumber),
        ];
        CombatBeamSolver driver = new(root, names, damage, policy);
        SimulationSnapshot predicted = InvokeForcedTerminalReplay(driver, prefix, null, 0, null);
        int expectedLoss = predicted.CumulativePlayerHpLost;
        predicted.ReleaseSimulator();
        if (expectedLoss <= 0)
            throw new InvalidOperationException("Fixed-prefix turn did not lose HP.");

        SolverResult result = await Task.Run(() => new CombatBeamSolver(
            root, names, damage, policy, searchProfile: policy.Profile,
            fixedPrefixActions: prefix).Solve());
        int annotatedLoss = result.HpLostByTurn.GetValueOrDefault(root.StartTurnNumber);
        if (annotatedLoss != expectedLoss)
        {
            throw new InvalidOperationException(
                $"Fixed-prefix turn loss: annotation {annotatedLoss}, prediction {expectedLoss}.");
        }
        _completedChecks.Add("FixedPrefixTurnLoss:FirstTurnAnnotationMatchesSimulation");
    }
}
