using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertForegoneSelectionAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var names = SolverDisplayNames.Capture(combat);
        var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            CardChoiceSupport.BuildChoices(request.Spec!, names, 10, 10).Single()
                with { SourceId = request.SourceId, Timing = request.Timing });
        if (TurnStartPowerSupport.TriggerBeforeHandDraw(simulator, shadow, player, cursor))
            throw new InvalidOperationException("Foregone selection remained pending.");
        var expected = CaptureSimulated(simulator, shadow, player, enemy);
        var fork = simulator.Fork();
        AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), "Foregone", "Fork");
        await player.Creature.GetPower<ForegoneConclusionPower>()!
            .BeforeHandDraw(player, new BlockingPlayerChoiceContext(), combat);
        AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "Foregone", "ImplicitAllOrder");
    }
}
