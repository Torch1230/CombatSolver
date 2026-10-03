using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTenderNestedAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator choiceRoot = root.ForkSimulator();
        bool discardAll = _request.ScenarioId == "TENDER-DISCARD-ALL-SLY";
        string cardId = discardAll ? "CALCULATED_GAMBLE" : "PREPARED";
        PlanCardChoice? choice = null;
        if (!discardAll)
        {
            var prepared = choiceRoot.State.GetPlayerCombatState(player).Hand.Cards
                .Single(card => card.Preview.Id.Entry == cardId);
            var spec = CardChoiceSupport.GetSpec(choiceRoot, prepared)
                ?? throw new InvalidOperationException("温柔夹具缺少准备的弃牌选择。");
            choice = CardChoiceSupport.BuildRequestedChoice(spec, ["UNTOUCHABLE"]);
        }
        PlanAction action = new(PlanActionKind.PlayCard, turn, CardId: cardId, Choice: choice);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot first = InvokeForcedTerminalReplay(driver, [action], null, 0, null);
        SimulationSnapshot next = InvokeForcedTerminalReplay(driver,
            [action, new PlanAction(PlanActionKind.EndTurn, turn)], null, 0, null);
        try
        {
            var shadow = (SimulatedCombatState)first.Simulator.State.CombatState;
            MoveStateSnapshot expected = CaptureSimulated(first.Simulator, shadow, player, enemy);
            CombatPredictionSimulator fork = first.Simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork,
                (SimulatedCombatState)fork.State.CombatState, player, enemy), "TenderNested", "Fork");
            using (CardSelectCmd.PushSelector(new UnattendedCardSelector(["UNTOUCHABLE"])))
            {
                if (!FindActualHandCard(player, cardId, 0).TryManualPlay(null))
                    throw new InvalidOperationException("温柔夹具原生准备未能执行。");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "TenderNested", "PreparedSly");
            if (shadow.GetTenderCardsPlayed(player.Creature) != 2)
                throw new InvalidOperationException("准备及其内层狡猾牌应各触发一次温柔。");
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                if (!CombatManager.Instance.IsInProgress)
                    throw new InvalidOperationException("温柔夹具意外结束战斗。");
                await NextFrameAsync();
            }
            var nextShadow = (SimulatedCombatState)next.Simulator.State.CombatState;
            AssertSnapshotEqual(CaptureSimulated(next.Simulator, nextShadow, player, enemy),
                CaptureActual(combat, player, enemy), "TenderNested", "NextTurnReset");
            if (nextShadow.GetTenderCardsPlayed(player.Creature) != 0)
                throw new InvalidOperationException("温柔完成恢复后计数应归零。");
        }
        finally
        {
            first.ReleaseSimulator();
            next.ReleaseSimulator();
        }
    }
}
