using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTestSubjectReportAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        int turn = player.PlayerCombatState!.TurnNumber;
        var root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        List<PlanAction> actions = [
            new(PlanActionKind.UsePotion, turn, PotionSlot: 1, PotionId: "OROBIC_ACID"),
            new(PlanActionKind.UsePotion, turn, PotionSlot: 0, PotionId: "FLEX_POTION"),
            new(PlanActionKind.UsePotion, turn, PotionSlot: 2, PotionId: "LIQUID_BRONZE"),
            new(PlanActionKind.PlayCard, turn, CardId: "SPECTRUM_SHIFT"),
            new(PlanActionKind.PlayCard, turn, CardId: "TYRANNY"),
            new(PlanActionKind.PlayCard, turn, CardId: "HEAVENLY_DRILL", TargetCombatId: enemy.CombatId),
            new(PlanActionKind.PlayCard, turn, CardId: "BULWARK"),
            new(PlanActionKind.EndTurn, turn)];
        var pending = InvokeForcedTerminalReplay(driver, actions, null, 0, null);
        var request = ((SimulatedCombatState)pending.Simulator.State.CombatState).PendingTurnStartChoice
            ?? throw new InvalidOperationException("Report Tyranny choice was not requested.");
        var choice = CardChoiceSupport.BuildRequestedChoice(request.Spec!, ["ASCENDERS_BANE"]) with
            { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
        pending.ReleaseSimulator();
        actions[^1] = actions[^1] with { TurnStartChoices = [choice] };
        var predicted = InvokeForcedTerminalReplay(driver, actions, null, 0, null);
        List<MoveStateSnapshot> prefixes = [];
        for (int index = 0; index < actions.Count - 1; index++)
        {
            var prefix = InvokeForcedTerminalReplay(driver, actions.Take(index + 1).ToArray(), null, 0, null);
            prefixes.Add(CaptureSimulated(prefix.Simulator, (SimulatedCombatState)prefix.Simulator.State.CombatState, player, enemy));
            prefix.ReleaseSimulator();
        }
        try
        {
            var expected = CaptureSimulated(predicted.Simulator, (SimulatedCombatState)predicted.Simulator.State.CombatState, player, enemy);
            using var selector = CardSelectCmd.PushSelector(new UnattendedCardSelector(["ASCENDERS_BANE"]));
            int actionIndex = 0;
            foreach (var action in actions)
            {
                if (action.Kind == PlanActionKind.UsePotion)
                    player.PotionSlots[action.PotionSlot]!.EnqueueManualUse(null);
                else if (action.Kind == PlanActionKind.PlayCard)
                {
                    var card = FindActualHandCard(player, action.CardId!, 0);
                    if (!card.TryManualPlay(card.TargetType == TargetType.AnyEnemy ? enemy : null))
                        throw new InvalidOperationException($"Report card failed: {card.Id}");
                }
                else
                {
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                }
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (action.Kind != PlanActionKind.EndTurn)
                    AssertSnapshotEqual(prefixes[actionIndex], CaptureActual(combat, player, enemy),
                        "TestSubjectOriginalReport", $"Step{actionIndex}:{action.CardId ?? action.PotionId}");
                actionIndex++;
            }
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "TestSubjectOriginalReport", "Turn2");
        }
        finally { predicted.ReleaseSimulator(); }
    }
}
