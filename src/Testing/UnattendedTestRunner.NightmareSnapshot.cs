using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNightmareSnapshotAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        FindActualHandCard(player, "PINPOINT", 0).EnergyCost.SetThisTurn(0);
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var probe = root.ForkSimulator();
        var nightmare = probe.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "NIGHTMARE");
        var spec = CardChoiceSupport.GetSpec(probe, nightmare)
            ?? throw new InvalidOperationException("Nightmare choice missing.");
        var choice = CardChoiceSupport.BuildRequestedChoice(spec, ["PINPOINT"]);
        PlanAction action = new(PlanActionKind.PlayCard, turn, CardId: "NIGHTMARE", Choice: choice);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var next = InvokeForcedTerminalReplay(driver, [action, new PlanAction(PlanActionKind.EndTurn, turn)], null, 0, null);
        try
        {
            var shadow = (SimulatedCombatState)next.Simulator.State.CombatState;
            var expected = CaptureSimulated(next.Simulator, shadow, player, enemy);
            var fork = next.Simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), "Nightmare", "Fork");
            using (CardSelectCmd.PushSelector(new UnattendedCardSelector(["PINPOINT"])))
            {
                if (!FindActualHandCard(player, "NIGHTMARE", 0).TryManualPlay(null))
                    throw new InvalidOperationException("Native Nightmare failed.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                if (!CombatManager.Instance.IsInProgress)
                    throw new InvalidOperationException("Nightmare fixture ended combat.");
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "Nightmare", "NextTurnCopies");
        }
        finally
        {
            next.ReleaseSimulator();
        }
    }
}
