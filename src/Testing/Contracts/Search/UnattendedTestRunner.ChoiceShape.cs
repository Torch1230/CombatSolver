using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertChoiceShapeInspectionAsync(CombatState live, Player player)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        foreach (string id in new[] { "SEEKER_STRIKE", "RECALL", "NIGHTMARE" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_FOOL", Pile = "Draw", Count = 4 });
        var root = CombatRootSnapshot.Capture(live);
        var simulator = root.ForkSimulator();
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var before = CaptureSimulated(simulator, combat, player, live.Enemies[0]);
        PlanChoiceEffect[] expected = [PlanChoiceEffect.MoveToHand, PlanChoiceEffect.MoveToDrawTop, PlanChoiceEffect.Nightmare];
        var hand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
        for (int index = 0; index < hand.Count; index++)
            if (CardChoiceSupport.GetPotentialEffect(simulator, hand[index]) != expected[index])
                throw new InvalidOperationException("Pre-play choice shape differs from the native card effect.");
        AssertSnapshotEqual(before, CaptureSimulated(simulator, combat, player, live.Enemies[0]),
            _request.ScenarioId, "ReadOnlyChoiceShape");
        var solver = new CombatBeamSolver(root, SolverDisplayNames.Capture(live), BattleDamageTracker.Observe(live),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null));
        if (!solver.ContainsChoiceEffectInRoot(PlanChoiceEffect.Nightmare)
            || !solver.ContainsChoiceEffectInRoot(PlanChoiceEffect.MoveToDrawTop)
            || solver.ContainsChoiceEffectInRoot(PlanChoiceEffect.Duplicate))
            throw new InvalidOperationException("Opening discovery lost the potential choice effect.");
        AssertSnapshotEqual(before, CaptureActual(live, player, live.Enemies[0]),
            _request.ScenarioId, "ChoiceShapeLiveIsolation");
        _completedChecks.Add("ChoiceShape:RecallBeforePlay:SeekerRngUnchanged:OpeningDiscovery:FullState:LiveIsolation");
    }
}
