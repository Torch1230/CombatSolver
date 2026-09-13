using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertAutomationRootAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_DEFECT", Pile = "Hand" });
        for (int index = 0; index < 3; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_DEFECT", Pile = "Draw" });
        var choice = new BlockingPlayerChoiceContext();
        var native = await PowerCmd.Apply<AutomationPower>(choice, player.Creature, 1, player.Creature, null)
            ?? throw new InvalidOperationException("Native Automation was not applied.");
        for (int index = 0; index < 9; index++)
            await native.AfterCardDrawn(choice, player.PlayerCombatState!.Hand.Cards[0], false);
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var fork = parent.Fork();
        var parentPower = ((SimulatedCombatState)parent.State.CombatState).EffectivePowers().OfType<AutomationPower>().Single();
        var forkPower = ((SimulatedCombatState)fork.State.CombatState).EffectivePowers().OfType<AutomationPower>().Single();
        var parentCounter = parent.StateStore.Peek(parentPower, () => new AutomationPredictionState(parentPower));
        var forkCounter = fork.StateStore.Get(forkPower, () => new AutomationPredictionState(forkPower));
        if (parentCounter.CardsLeft != 1 || forkCounter.CardsLeft != 1)
            throw new InvalidOperationException("Automation root lost its pending draw refund.");
        var enemy = combat.Enemies.Single();
        for (int index = 0; index < 3; index++)
        {
            fork.Draw(player, 1);
            await CardPileCmd.Draw(choice, 1, player, fromHandDraw: false);
            AssertSnapshotEqual(CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                CaptureActual(combat, player, enemy), _request.ScenarioId, "NativeDrawRefund");
            if (forkCounter.CardsLeft != native.DisplayAmount || parentCounter.CardsLeft != 1
                || parent.State.GetPlayerCombatState(player).Energy != 3)
                throw new InvalidOperationException("Automation draw counter violated native equality or Fork isolation.");
        }
        if (player.PlayerCombatState!.Energy != 4 || forkCounter.CardsLeft != 8)
            throw new InvalidOperationException("Automation did not refund once and reset its draw counter.");
        _completedChecks.Add("Automation:CapturedDrawCounter:NativeRefundAndReset:FullStateAndRng:ForkIsolation");
    }

    private async Task AssertAutomationNaturalDrawsAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        SetEnergy(player, 3);
        for (int index = 0; index < 10; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_DEFECT", Pile = "Draw" });
        var native = await PowerCmd.Apply<AutomationPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null)
            ?? throw new InvalidOperationException("Native Automation was not applied.");
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var advance = typeof(CombatBeamSolver).GetMethod("AdvanceRound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CombatBeamSolver), "AdvanceRound");
        for (int index = 0; index < 2; index++)
        {
            int turn = player.PlayerCombatState!.TurnNumber;
            var boundary = (SearchBoundaryReason)advance.Invoke(driver,
                [simulator, shadow, 0, new HashSet<uint>(), 0, null])!;
            if (boundary != SearchBoundaryReason.None || shadow.HasPendingChoice)
                throw new InvalidOperationException($"Automation natural draw fixture reached {boundary}.");
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            var enemy = combat.Enemies.Single();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "FiveNaturalDrawsPerTurn");
            int expectedEnergy = index == 0 ? 3 : 4;
            int expectedCardsLeft = index == 0 ? 5 : 10;
            if (player.PlayerCombatState.Energy != expectedEnergy || native.DisplayAmount != expectedCardsLeft)
                throw new InvalidOperationException("Automation natural draws did not refund energy every second turn.");
        }
        _completedChecks.Add("Automation:NaturalFiveCardDraw:TwoTurns:OneEnergyRefund:FullStateAndRng");
    }
}
