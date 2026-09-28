using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed partial class Executor
    {
        private async Task<ExecutionOutcome> ExecuteMultiplayerProbeAsync(ScenarioContext scenario)
        {
            MultiplayerProbeInput input = runner._multiplayerProbe!;
            if (input.ContentCardIds.Length > 0)
                return await ExecuteMultiplayerContentProbeAsync(scenario, input);
            CombatState combat = scenario.CombatState;
            Creature enemy = combat.Enemies.Single();
            if (enemy.CurrentHp <= input.PlayerCount * 6)
                throw new InvalidOperationException("Probe requires an enemy surviving all scripted attacks.");
            if (CardSelectCmd.Selector != null || CardSelectCmd.LocalSelector != null)
                throw new InvalidOperationException("Probe requires exclusive ownership of the test selector.");
            if (input.VerifyEnemyPowerScaling)
            {
                await VerifyScaledPowerAsync<ArtifactPower>();
                await VerifyScaledPowerAsync<PlatingPower>();
                await VerifyScaledPowerAsync<SlipperyPower>();
                await VerifyScaledPowerAsync<SkittishPower>();
                await VerifyScaledPowerAsync<CurlUpPower>();
            }
            ICardSelector selector = input.IsVirtual
                ? new UnattendedCardSelector(["DEFEND_IRONCLAD"])
                : new MultiplayerProbeNetworkSelector(combat);
            using (CardSelectCmd.UseSelector(selector, localOnly: !input.IsVirtual))
            {
                foreach (Player player in combat.Players)
                {
                    ContinuationStamp? defendPrediction = input.VerifyActionDifferential
                        ? PredictOrdinaryCard(player, "DEFEND_IRONCLAD", null) : null;
                    await PlayAsync(player, "DEFEND_IRONCLAD", null, () => player.PlayerCombatState!.Energy == 2 && player.Creature.Block == 5);
                    await runner.MultiplayerProbeBarrierAsync($"defend-{player.NetId}", combat);
                    CheckPrediction(defendPrediction, player, "DEFEND_IRONCLAD");
                    int hp = enemy.CurrentHp;
                    ContinuationStamp? strikePrediction = input.VerifyActionDifferential
                        ? PredictOrdinaryCard(player, "STRIKE_IRONCLAD", enemy) : null;
                    await PlayAsync(player, "STRIKE_IRONCLAD", enemy, () => player.PlayerCombatState!.Energy == 1 && enemy.CurrentHp == hp - 6);
                    await runner.MultiplayerProbeBarrierAsync($"strike-{player.NetId}", combat);
                    CheckPrediction(strikePrediction, player, "STRIKE_IRONCLAD");
                    await PlayAsync(player, "SURVIVOR", null, () => player.PlayerCombatState!.Energy == 0
                        && player.Creature.Block == 13 && player.PlayerCombatState.Hand.Cards.Count == 1
                        && player.PlayerCombatState.DiscardPile.Cards.Count == 4);
                    await runner.MultiplayerProbeBarrierAsync($"choice-{player.NetId}", combat);
                }
            }
            ContinuationStamp? roundPrediction = input.VerifyRoundDifferential
                ? PredictMultiplayerRound(combat, scenario.Player) : null;
            foreach (Player player in input.IsVirtual ? new[] { scenario.Player } : combat.Players)
            {
                if (input.IsVirtual || LocalContext.IsMe(player))
                {
                    var end = new EndPlayerTurnAction(player, 1);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                    if (input.Mode != "client")
                        await end.CompletionTask;
                }
                if (!input.IsVirtual && player != combat.Players[^1])
                {
                    await runner.WaitForMultiplayerProbeAsync(() => CombatManager.Instance.IsPlayerReadyToEndTurn(player));
                    if (combat.Players.Any(member => member.PlayerCombatState!.TurnNumber != 1))
                        throw new InvalidOperationException("Native multiplayer advanced before all players ended.");
                    await runner.MultiplayerProbeBarrierAsync($"ready-{player.NetId}", combat);
                }
            }
            runner.SetStage("multiplayer_second_turn");
            await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
            await runner.MultiplayerProbeBarrierAsync("second-turn", combat);
            CheckPrediction(roundPrediction, scenario.Player, "END_TURN");
            runner._completedChecks.Add($"MultiplayerNativeProbe:Mode={input.Mode}:Players={input.PlayerCount}:Seat={input.Seat}:ScriptedCards:NativeChoice:EnemyTurn:NextDraw");
            return new ExecutionOutcome(false, 2, true, true, true, false);

            async Task VerifyScaledPowerAsync<T>() where T : PowerModel
            {
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                SimulatedCombatState predictedCombat = (SimulatedCombatState)simulator.State.CombatState;
                predictedCombat.Apply<T>(enemy, 1, enemy);
                if (!CombatBeamSolver.SettleReplayActionBoundary(simulator, predictedCombat))
                    throw new InvalidOperationException($"Predicted {typeof(T).Name} application requested a choice.");
                ContinuationStamp predicted = ContinuationStamp.CapturePredicted(
                    scenario.Player, simulator, 1, root.Forecast, 1);
                T applied = await PowerCmd.Apply<T>(
                    new ThrowingPlayerChoiceContext(), enemy, 1, enemy, null)
                    ?? throw new InvalidOperationException($"Native {typeof(T).Name} application failed.");
                ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
                if (predicted != actual)
                    throw new InvalidOperationException(
                        $"Multiplayer power differs: {typeof(T).Name} " + predicted.DescribeFirstDifference(actual));
                runner._completedChecks.Add($"MultiplayerPowerScaling:{typeof(T).Name}:Players={input.PlayerCount}:FullState");
                await PowerCmd.Remove(applied);
            }

            ContinuationStamp PredictOrdinaryCard(Player actor, string cardId, Creature? target)
            {
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                PredictedCard card = simulator.State.GetPlayerCombatState(actor).Hand.Cards
                    .First(candidate => candidate.Preview.Id.Entry == cardId);
                if (!simulator.CanPlay(card)
                    || !simulator.ManualPlay(card, target, out _)
                    || !CombatBeamSolver.SettleReplayActionBoundary(
                        simulator, (SimulatedCombatState)simulator.State.CombatState))
                    throw new InvalidOperationException($"Multiplayer predicted action did not complete: actor={actor.NetId} card={cardId}.");
                return ContinuationStamp.CapturePredicted(
                    scenario.Player, simulator, scenario.StartedTurn, root.Forecast, scenario.StartedTurn);
            }

            void CheckPrediction(ContinuationStamp? predicted, Player actor, string cardId)
            {
                if (predicted == null)
                    return;
                ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
                if (predicted != actual)
                    throw new InvalidOperationException(
                        $"Multiplayer action differs: actor={actor.NetId} card={cardId} " +
                        predicted.DescribeFirstDifference(actual));
                runner._completedChecks.Add($"MultiplayerActionDiff:Actor={actor.NetId}:Card={cardId}:AllPlayers:FullRng");
            }

            async Task PlayAsync(Player actor, string cardId, Creature? target, Func<bool> observed)
            {
                runner.SetStage($"multiplayer_play_{actor.NetId}_{cardId}");
                if (input.IsVirtual || LocalContext.IsMe(actor))
                {
                    CardModel card = actor.PlayerCombatState!.Hand.Cards.First(candidate => candidate.Id.Entry == cardId);
                    var action = new PlayCardAction(card, target);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                    if (input.Mode != "client")
                        await action.CompletionTask;
                }
                await runner.WaitForMultiplayerProbeAsync(observed);
            }
        }

        private async Task<ExecutionOutcome> ExecuteMultiplayerContentProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Player actor = scenario.Player;
            foreach (Player member in combat.Players)
                for (int index = 0; index < input.ContentExtraDrawCardsPerPlayer; index++)
                    await UnattendedTestRunner.InjectCardAsync(combat, member,
                        new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Draw" });
            await UnattendedTestRunner.SetBlockAsync(actor.Creature, input.ContentActorBlock);
            await UnattendedTestRunner.SetBlockAsync(
                combat.Players[input.ContentTargetSeat].Creature, input.ContentTargetBlock);
            UnattendedTestRunner.SetEnergy(actor, input.ContentActorEnergy);
            UnattendedTestRunner.SetStars(actor, 5);
            for (int index = 0; index < input.ContentCardIds.Length; index++)
            {
                string cardId = input.ContentCardIds[index];
                CardModel card = actor.PlayerCombatState!.Hand.Cards.Single(candidate =>
                    candidate.Id.Entry == cardId);
                Creature? target = card.TargetType switch
                {
                    TargetType.AnyAlly => combat.Players[input.ContentTargetSeat].Creature,
                    TargetType.Self or TargetType.AllAllies => null,
                    TargetType.AnyEnemy => combat.Enemies.Single(),
                    _ => throw new InvalidOperationException(
                        $"Content probe has no target rule for {card.Id.Entry}: {card.TargetType}."),
                };
                if (!card.CanPlayTargeting(target))
                    throw new InvalidOperationException($"Content probe card is not playable: {card.Id.Entry}.");
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                PredictedCard predictedCard = simulator.State.GetPlayerCombatState(actor).Hand.Cards.Single(candidate =>
                    candidate.Preview.Id.Entry == card.Id.Entry);
                SimulatedCombatState predictedCombat = (SimulatedCombatState)simulator.State.CombatState;
                PlanCardChoice[]? plannedChoices = null;
                if (card is Tutor)
                {
                    Player targetPlayer = target?.Player
                        ?? throw new InvalidOperationException("Tutor fixture has no target player.");
                    var targetState = simulator.State.GetPlayerCombatState(targetPlayer);
                    var spec = new CardChoiceSpec(PlanChoiceEffect.MoveToHand, PileType.Draw,
                        1, 1, targetState.DrawPile.Cards, targetState.DrawPile.Cards, 0d);
                    plannedChoices = [CardChoiceSupport.BuildRequestedChoice(spec, ["DEFEND_IRONCLAD"])];
                }
                predictedCombat.BeginActionChoices(plannedChoices);
                try
                {
                    if (!simulator.CanPlay(predictedCard)
                        || !simulator.ManualPlay(predictedCard, target, out _)
                        || !CombatBeamSolver.SettleReplayActionBoundary(simulator, predictedCombat))
                        throw new InvalidOperationException($"Content prediction did not complete: {card.Id.Entry}.");
                }
                finally
                {
                    predictedCombat.EndActionChoices();
                }
                ContinuationStamp predicted = ContinuationStamp.CapturePredicted(
                    actor, simulator, 1, root.Forecast, 1);
                runner.SetStage($"multiplayer_content_play_{cardId}");
                using var selector = card is Tutor
                    ? CardSelectCmd.UseSelector(new UnattendedCardSelector(["DEFEND_IRONCLAD"]), localOnly: false)
                    : null;
                var action = new PlayCardAction(card, target);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                await action.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync($"content-{index}", combat);
                ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
                if (predicted != actual)
                    throw new InvalidOperationException(
                        $"Multiplayer content differs: {card.Id.Entry}+{card.CurrentUpgradeLevel} " +
                        predicted.DescribeFirstDifference(actual));
                runner._completedChecks.Add(
                    $"MultiplayerContent:{card.Id.Entry}:Upgrade={card.CurrentUpgradeLevel}:Target={combat.Players[input.ContentTargetSeat].NetId}:FullState:FullRng");
            }
            if (input.VerifyContentRound)
            {
                ContinuationStamp predictedRound = PredictMultiplayerRound(combat, actor);
                runner.SetStage("multiplayer_content_round");
                var end = new EndPlayerTurnAction(actor, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("content-round", combat);
                ContinuationStamp actualRound = ContinuationStamp.CaptureLive(combat);
                if (predictedRound != actualRound)
                    throw new InvalidOperationException(
                        "Multiplayer content round differs: " + predictedRound.DescribeFirstDifference(actualRound));
                runner._completedChecks.Add("MultiplayerContentRound:AllPlayers:FullState:FullRng");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            return new ExecutionOutcome(false, 1, true, true, true, false);
        }

        private static ContinuationStamp PredictMultiplayerRound(CombatState combat, Player localPlayer)
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat), policy);
            SimulationSnapshot predicted = UnattendedTestRunner.InvokeForcedTerminalReplay(
                driver, [new PlanAction(PlanActionKind.EndTurn, 1)], null, 0, null);
            try
            {
                return ContinuationStamp.CapturePredicted(
                    localPlayer, predicted.Simulator, predicted.Turn, root.Forecast, 1);
            }
            finally
            {
                predicted.ReleaseSimulator();
            }
        }

        private sealed class MultiplayerProbeNetworkSelector(CombatState combat) : ICardSelector
        {
            private readonly UnattendedCardSelector _selector = new(["DEFEND_IRONCLAD"]);

            public async Task<IEnumerable<CardModel>> GetSelectedCards(
                IEnumerable<CardModel> options, int minSelect, int maxSelect)
            {
                CardModel[] selected = (await _selector.GetSelectedCards(options, minSelect, maxSelect)).ToArray();
                Player player = selected.Single().Owner;
                if (!LocalContext.IsMe(player))
                    throw new InvalidOperationException("Probe network selector received a remote player's choice.");
                int slot = combat.Players.ToList().IndexOf(player);
                var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
                if (slot < 0 || slot >= synchronizer.ChoiceIds.Count || synchronizer.ChoiceIds[slot] == 0)
                    throw new InvalidOperationException($"Probe choice ID was not reserved for player {player.NetId}.");
                synchronizer.SyncLocalChoice(player, synchronizer.ChoiceIds[slot] - 1,
                    PlayerChoiceResult.FromMutableCombatCards(selected));
                return selected;
            }

            public CardRewardSelection GetSelectedCardReward(
                IReadOnlyList<CardCreationResult> options,
                IReadOnlyList<CardRewardAlternative> alternatives)
                => throw new InvalidOperationException("Probe does not select card rewards.");
        }
    }
}
