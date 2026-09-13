using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSignedGoldLossAsync(CombatState combat, Player player)
    {
        player.Gold = 137;
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        foreach (int amount in new[] { -5, 0, 3, 200, -5 })
        {
            shadow.LosePlayerGold(player, amount);
            await PlayerCmd.LoseGold(amount, player);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
                CaptureActual(combat, player, enemy), _request.ScenarioId, $"LoseGold:{amount}");
        }
        _completedChecks.Add("SignedGoldLoss:NegativeZeroPositiveAndFloor:FullStateAndRng");
    }
}
