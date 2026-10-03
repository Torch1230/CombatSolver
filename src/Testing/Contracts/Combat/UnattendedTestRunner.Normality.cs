using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNormalityAutoPlayAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        bool replay = _request.ScenarioId.EndsWith("-REPLAY", StringComparison.Ordinal);
        int prefix = replay ? 1 : 2;
        if (replay)
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "ECHO_FORM_POWER", Target = "Player", Amount = 1 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "NORMALITY", Pile = "Hand" });
        for (int index = 0; index < prefix + 1; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "CASCADE", Pile = "Hand" });
        foreach (string id in new[] { "FLAME_BARRIER", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        SetEnergy(player, prefix + 2);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        List<PlanAction> actions = [];
        for (int index = 0; index < prefix; index++) actions.Add(new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "DEFEND_IRONCLAD"));
        actions.Add(new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "CASCADE"));
        SimulationSnapshot prediction = InvokeForcedTerminalReplay(driver, actions, null, 0, null);
        try
        {
            var simulator = prediction.Simulator;
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
            foreach (PlanAction action in actions)
            {
                if (!FindActualHandCard(player, action.CardId, 0).TryManualPlay(null))
                    throw new InvalidOperationException($"Native fixture action was refused: {action.CardId}");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "NormalityAutoPlay", "CascadeAtThirdStart");
            CombatPredictionSimulator fork = simulator.Fork();
            SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
            CombatPredictionSimulator recaptured = CombatRootSnapshot.Capture(combat).ForkSimulator();
            if (shadow.GetCardPlayStartsThisTurn(player.Creature) != 3
                || forkState.GetCardPlayStartsThisTurn(player.Creature) != 3
                || ((SimulatedCombatState)recaptured.State.CombatState).GetCardPlayStartsThisTurn(player.Creature) != 3)
                throw new InvalidOperationException("Card-play starts differ across root, replay and fork.");
            forkState.BeginSideTurn(player.Creature);
            if (!forkState.TriggerSideTurnStart(fork, CombatSide.Player, [player.Creature], decrementPlating: false)
                || forkState.GetCardPlayStartsThisTurn(player.Creature) != 0
                || shadow.GetCardPlayStartsThisTurn(player.Creature) != 3)
                throw new InvalidOperationException("Turn reset leaked between forked card-play histories.");

            var nativeDefend = FindActualHandCard(player, "DEFEND_IRONCLAD", 0);
            PredictedCard predictedDefend = simulator.State.FindCard(nativeDefend)!;
            if (simulator.AutoPlay(predictedDefend))
                throw new InvalidOperationException("Normality allowed another auto-play after three starts.");
            await CardCmd.AutoPlay(new ThrowingPlayerChoiceContext(), nativeDefend, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, combat.Enemies[0]),
                CaptureActual(combat, player, combat.Enemies[0]), "NormalityAutoPlay", "BlockedCardResultPile");

            var normality = FindActualHandCard(player, "NORMALITY", 0);
            simulator.AddToPile(simulator.State.FindCard(normality)!, PileType.Discard);
            await CardPileCmd.Add(normality, PileType.Discard);
            simulator.AddToPile(predictedDefend, PileType.Hand);
            await CardPileCmd.Add(nativeDefend, PileType.Hand);
            if (!simulator.AutoPlay(predictedDefend))
                throw new InvalidOperationException("Normality outside the hand still prevented auto-play.");
            await CardCmd.AutoPlay(new ThrowingPlayerChoiceContext(), nativeDefend, null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, combat.Enemies[0]),
                CaptureActual(combat, player, combat.Enemies[0]), "NormalityAutoPlay", "CurseLeftHand");
            _completedChecks.Add($"NormalityCascadeNative:{(replay ? "RepeatedFirstCard" : "TwoPriorCards")}:FullState");
            _completedChecks.Add("Normality:StartedHistory:ForkIsolation:TurnReset:BlockedPile:CurseLeftHand");
        }
        finally { prediction.ReleaseSimulator(); }
    }
}
