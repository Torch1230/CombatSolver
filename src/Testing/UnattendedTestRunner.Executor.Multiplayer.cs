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
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
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
            if (input.VerifyEnetTutorChoice)
                return await ExecuteEnetTutorChoiceProbeAsync(scenario, input);
            if (input.VerifyEnetControllerRng)
                return await ExecuteEnetControllerRngProbeAsync(scenario, input);
            if (input.VerifyEnetPaelsEyeExtraTurn)
                return await ExecuteEnetPaelsEyeExtraTurnProbeAsync(scenario, input);
            if (input.VerifyControllerSearchCancel)
            {
                CombatState cancelCombat = scenario.CombatState;
                Player localPlayer = scenario.Player;
                Player otherPlayer = cancelCombat.Players.First(player => player != localPlayer);
                SolverController.MonitorCombatPresence();
                SolverController.RequestSearch(runner._host, cancelCombat, SearchReason.Manual);
                SolverController.StopSearchByUser(runner._host);
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsSearching);
                if (SolverController.IsDeploying
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(localPlayer)
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(otherPlayer)
                    || SolverController.SearchesStartedForTesting != 1)
                    throw new InvalidOperationException("Multiplayer user stop changed another player's turn or started a second search.");
                runner._completedChecks.Add("MultiplayerController:SearchStoppedByUser:NoDeployment:TeammateUntouched");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerLifecycleReset)
            {
                CombatState lifecycleCombat = scenario.CombatState;
                Player localPlayer = scenario.Player;
                Player otherPlayer = lifecycleCombat.Players.First(player => player != localPlayer);
                SolverController.MonitorCombatPresence();
                SolverController.RequestSearch(runner._host, lifecycleCombat, SearchReason.Manual);
                SolverController.Reset("multiplayer_probe_exit");
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsSearching
                    && !SolverController.IsDeploying);
                if (SolverOverlay.IsVisible
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(localPlayer)
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(otherPlayer))
                    throw new InvalidOperationException("Multiplayer lifecycle reset retained local UI or changed player turns.");
                runner._completedChecks.Add("MultiplayerController:LifecycleReset:NoSearchOrDeployment:OverlayHidden:TeammateUntouched");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerStaleSearchCallback)
            {
                CombatState callbackCombat = scenario.CombatState;
                SolverController.MonitorCombatPresence();
                SolverController.RequestSearch(runner._host, callbackCombat, SearchReason.Manual);
                SolverController.Reset("multiplayer_probe_replace_search");
                SolverController.MonitorCombatPresence();
                SolverController.RequestSearch(runner._host, callbackCombat, SearchReason.Manual);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("Replacement multiplayer search failed.", failure);
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsSearching);
                if (SolverController.LastCompletedResultForTesting is not
                    { MultiplayerStyle: MultiplayerPlanStyle.Output }
                    || !SolverController.CanExecuteCurrentTurn
                    || !SolverOverlay.IsVisible
                    || SolverController.SearchesStartedForTesting != 1)
                    throw new InvalidOperationException("Old multiplayer callback displaced the replacement search result.");
                runner._completedChecks.Add("MultiplayerController:StaleSearchCanceled:ReplacementResultVisible:NoOldCallbackOverwrite");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            CombatState combat = scenario.CombatState;
            Creature enemy = input.VerifyControllerTeammateKillsTarget
                || input.VerifyControllerMidDeploymentKill || input.VerifyControllerMidDeploymentDamage
                || input.VerifyControllerMidDeploymentRng || input.VerifyControllerManualTakeover
                || input.UseFirstEnemyForProbe
                ? combat.Enemies.First() : combat.Enemies.Single();
            if ((input.VerifyControllerTeammateKillsTarget
                    || input.VerifyControllerMidDeploymentKill || input.VerifyControllerMidDeploymentDamage
                    || input.VerifyControllerMidDeploymentRng || input.VerifyControllerManualTakeover)
                && combat.Enemies.Count < 2)
                throw new InvalidOperationException("Invalidation probe requires a surviving second enemy.");
            if (enemy.CurrentHp <= input.PlayerCount * 6)
                throw new InvalidOperationException("Probe requires an enemy surviving all scripted attacks.");
            if (CardSelectCmd.Selector != null || CardSelectCmd.LocalSelector != null)
                throw new InvalidOperationException("Probe requires exclusive ownership of the test selector.");
            if (input.VerifyRandomPowerHooks)
            {
                Player actor = scenario.Player;
                Creature owner = actor.Creature;
                await PowerCmd.Apply<JuggernautPower>(new ThrowingPlayerChoiceContext(), owner, 3, owner, null);
                await PowerCmd.Apply<HauntPower>(new ThrowingPlayerChoiceContext(), owner, 4, owner, null);
                await PowerCmd.Apply<CountdownPower>(new ThrowingPlayerChoiceContext(), owner, 5, owner, null);
                int[] hpBeforeBlock = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                ContinuationStamp blockPrediction = PredictOrdinaryCard(actor, "DEFEND_IRONCLAD", null);
                CardModel defend = actor.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "DEFEND_IRONCLAD");
                var defendPlay = new PlayCardAction(defend, null);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(defendPlay);
                await defendPlay.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("juggernaut-block", combat);
                CheckPrediction(blockPrediction, actor, "JUGGERNAUT_BLOCK");
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeBlock[index] - creature.CurrentHp == 3).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeBlock[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Juggernaut did not hit one enemy for 3.");
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = "SOUL", Pile = "Hand" });
                int[] hpBeforeSoul = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                ContinuationStamp soulPrediction = PredictOrdinaryCard(actor, "SOUL", null);
                CardModel soul = actor.PlayerCombatState.Hand.Cards.Single(card => card.Id.Entry == "SOUL");
                var soulPlay = new PlayCardAction(soul, null);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(soulPlay);
                await soulPlay.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("haunt-soul", combat);
                CheckPrediction(soulPrediction, actor, "HAUNT_SOUL");
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeSoul[index] - creature.CurrentHp == 4).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeSoul[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Haunt did not hit one enemy for 4.");
                ContinuationStamp countdownPrediction = PredictMultiplayerRound(combat, actor);
                var end = new EndPlayerTurnAction(actor, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("countdown-next-turn", combat);
                CheckPrediction(countdownPrediction, actor, "COUNTDOWN_NEXT_TURN");
                if (combat.Enemies.Count(creature => creature.GetPowerAmount<DoomPower>() == 5) != 1
                    || combat.Enemies.Count(creature => creature.GetPowerAmount<DoomPower>() > 0) != 1)
                    throw new InvalidOperationException("Countdown did not give one random enemy 5 Doom.");
                runner._completedChecks.Add("MultiplayerContent:JuggernautHauntCountdown:SharedTargetsRng:FullState");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyCalamityGeneration)
            {
                Player actor = scenario.Player;
                await PowerCmd.Apply<CalamityPower>(new ThrowingPlayerChoiceContext(),
                    actor.Creature, 1, actor.Creature, null);
                HashSet<CardModel> cardsBefore = [.. actor.PlayerCombatState!.AllCards];
                int[] teammateHandCounts = combat.Players.Where(player => player != actor)
                    .Select(player => player.PlayerCombatState!.Hand.Cards.Count).ToArray();
                Creature target = combat.Enemies.Single();
                ContinuationStamp generationPrediction = PredictOrdinaryCard(actor, "STRIKE_IRONCLAD", target);
                CardModel strike = actor.PlayerCombatState.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var play = new PlayCardAction(strike, target);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                await play.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("calamity-generation", combat);
                CheckPrediction(generationPrediction, actor, "CALAMITY_GENERATION");
                CardModel[] generated = actor.PlayerCombatState.AllCards
                    .Where(card => !cardsBefore.Contains(card)).ToArray();
                if (generated.Length != 1 || generated[0].Owner != actor
                    || generated[0].Type != CardType.Attack
                    || !actor.PlayerCombatState.Hand.Cards.Contains(generated[0])
                    || combat.Players.Where(player => player != actor)
                        .Select(player => player.PlayerCombatState!.Hand.Cards.Count)
                        .Where((count, index) => count != teammateHandCounts[index]).Any())
                    throw new InvalidOperationException("Calamity generated an attack for the wrong player.");
                runner._completedChecks.Add("MultiplayerContent:Calamity:OwnerAttackGeneration:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyPowerGenerationPools)
            {
                Player actor = scenario.Player;
                Creature owner = actor.Creature;
                await PowerCmd.Apply<CreativeAiPower>(new ThrowingPlayerChoiceContext(), owner, 1, owner, null);
                await PowerCmd.Apply<HelloWorldPower>(new ThrowingPlayerChoiceContext(), owner, 1, owner, null);
                await PowerCmd.Apply<SpectrumShiftPower>(new ThrowingPlayerChoiceContext(), owner, 1, owner, null);
                await PowerCmd.Apply<CallOfTheVoidPower>(new ThrowingPlayerChoiceContext(), owner, 1, owner, null);
                HashSet<CardModel> cardsBefore = [.. actor.PlayerCombatState!.AllCards];
                int[] teammateHandCounts = combat.Players.Where(player => player != actor)
                    .Select(player => player.PlayerCombatState!.Hand.Cards.Count).ToArray();
                ContinuationStamp generationPrediction = PredictMultiplayerRound(combat, actor);
                var end = new EndPlayerTurnAction(actor, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("power-generation-pools", combat);
                CheckPrediction(generationPrediction, actor, "POWER_GENERATION_POOLS");
                CardModel[] generated = actor.PlayerCombatState!.AllCards
                    .Where(card => !cardsBefore.Contains(card)).ToArray();
                if (generated.Length != 4 || generated.Any(card => card.Owner != actor)
                    || !generated.Any(card => card.Keywords.Contains(CardKeyword.Ethereal))
                    || combat.Players.Where(player => player != actor)
                        .Select(player => player.PlayerCombatState!.Hand.Cards.Count)
                        .Where((count, index) => count != teammateHandCounts[index]).Any())
                    throw new InvalidOperationException("Turn-start generation powers did not create four owner cards.");
                runner._completedChecks.Add("MultiplayerContent:FourTurnStartGenerationPowers:Owner:FilteredPools:FullState:FullRng");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyLightningOrbTargets)
            {
                Player actor = scenario.Player;
                await OrbCmd.AddSlots(actor, 2);
                await OrbCmd.Channel<LightningOrb>(new ThrowingPlayerChoiceContext(), actor);
                int[] hpBeforePassive = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                ContinuationStamp passivePrediction = PredictMultiplayerRound(combat, actor);
                var end = new EndPlayerTurnAction(actor, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("lightning-passive", combat);
                CheckPrediction(passivePrediction, actor, "LIGHTNING_PASSIVE");
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforePassive[index] - creature.CurrentHp == 3).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforePassive[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Lightning passive did not hit one enemy for 3.");
                int[] hpBeforeEvoke = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                CombatRootSnapshot evokeRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator evokeSimulator = evokeRoot.ForkSimulator();
                evokeSimulator.OrbEvokeNext(actor);
                if (!CombatBeamSolver.SettleReplayActionBoundary(evokeSimulator,
                        (SimulatedCombatState)evokeSimulator.State.CombatState))
                    throw new InvalidOperationException("Lightning evoke prediction did not settle.");
                ContinuationStamp evokePrediction = ContinuationStamp.CapturePredicted(
                    actor, evokeSimulator, 2, evokeRoot.Forecast, 2);
                await OrbCmd.EvokeNext(new ThrowingPlayerChoiceContext(), actor);
                await runner.MultiplayerProbeBarrierAsync("lightning-evoke", combat);
                ContinuationStamp evokeActual = ContinuationStamp.CaptureLive(combat);
                if (evokePrediction != evokeActual)
                    throw new InvalidOperationException("Lightning evoke differs: "
                        + evokePrediction.DescribeFirstDifference(evokeActual));
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeEvoke[index] - creature.CurrentHp == 8).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeEvoke[index] != creature.CurrentHp).Count() != 1
                    || actor.PlayerCombatState!.OrbQueue.Orbs.Count != 0
                    || combat.Players.Where(player => player != actor)
                        .Any(player => player.PlayerCombatState!.OrbQueue.Orbs.Count != 0))
                    throw new InvalidOperationException("Lightning evoke did not use its holder's orb and random enemy.");
                runner._completedChecks.Add("MultiplayerContent:LightningOrb:PassiveAndEvoke:OwnerQueue:RandomTarget:FullState:FullRng");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyFrostHibernatePropagation)
            {
                Player actor = scenario.Player;
                Player teammate = combat.Players.Single(player => player != actor);
                await PowerCmd.Apply<HibernatePower>(new ThrowingPlayerChoiceContext(),
                    actor.Creature, 1, actor.Creature, null);
                await OrbCmd.AddSlots(actor, 2);
                await OrbCmd.Channel<FrostOrb>(new ThrowingPlayerChoiceContext(), actor);
                FrostOrb orb = actor.PlayerCombatState!.OrbQueue.Orbs.OfType<FrostOrb>().Single();
                CombatRootSnapshot passiveRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator passiveSimulator = passiveRoot.ForkSimulator();
                passiveSimulator.TriggerOrbPassive(orb, target: null);
                if (!CombatBeamSolver.SettleReplayActionBoundary(passiveSimulator,
                        (SimulatedCombatState)passiveSimulator.State.CombatState))
                    throw new InvalidOperationException("Frost passive prediction did not settle.");
                ContinuationStamp passivePrediction = ContinuationStamp.CapturePredicted(
                    actor, passiveSimulator, 1, passiveRoot.Forecast, 1);
                await orb.TriggerPassive(new ThrowingPlayerChoiceContext(), null);
                await runner.MultiplayerProbeBarrierAsync("frost-hibernate-passive", combat);
                ContinuationStamp passiveActual = ContinuationStamp.CaptureLive(combat);
                if (passivePrediction != passiveActual
                    || actor.Creature.Block != 2 || teammate.Creature.Block != 2)
                    throw new InvalidOperationException("Hibernate Frost passive did not block both players: "
                        + passivePrediction.DescribeFirstDifference(passiveActual));
                CombatRootSnapshot evokeRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator evokeSimulator = evokeRoot.ForkSimulator();
                evokeSimulator.OrbEvokeNext(actor);
                if (!CombatBeamSolver.SettleReplayActionBoundary(evokeSimulator,
                        (SimulatedCombatState)evokeSimulator.State.CombatState))
                    throw new InvalidOperationException("Frost evoke prediction did not settle.");
                ContinuationStamp evokePrediction = ContinuationStamp.CapturePredicted(
                    actor, evokeSimulator, 1, evokeRoot.Forecast, 1);
                await OrbCmd.EvokeNext(new ThrowingPlayerChoiceContext(), actor);
                await runner.MultiplayerProbeBarrierAsync("frost-hibernate-evoke", combat);
                ContinuationStamp evokeActual = ContinuationStamp.CaptureLive(combat);
                if (evokePrediction != evokeActual
                    || actor.Creature.Block != 7 || teammate.Creature.Block != 7
                    || actor.PlayerCombatState.OrbQueue.Orbs.Count != 0
                    || teammate.PlayerCombatState!.OrbQueue.Orbs.Count != 0)
                    throw new InvalidOperationException("Hibernate Frost evoke did not block both players: "
                        + evokePrediction.DescribeFirstDifference(evokeActual));
                runner._completedChecks.Add("MultiplayerFrostHibernate:PassiveAndEvoke:BothPlayers:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyKusarigamaRandomTarget || input.VerifyKusarigamaOwnerAndReset)
            {
                Player actor = scenario.Player;
                await UnattendedTestRunner.InjectRelicAsync(actor,
                    new UnattendedRelicInjection { RelicId = "KUSARIGAMA" });
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
                UnattendedTestRunner.SetEnergy(actor, 10);
                Creature target = combat.Enemies[0];
                if (combat.Enemies.Count(creature => creature.IsAlive) < 2)
                    throw new InvalidOperationException("Kusarigama probe requires two live enemies.");
                for (int index = 0; index < 3; index++)
                {
                    int[] hpBefore = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                    ContinuationStamp predicted = PredictOrdinaryCard(actor, "STRIKE_IRONCLAD", target);
                    CardModel strike = actor.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var play = new PlayCardAction(strike, target);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                    await play.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync($"kusarigama-attack-{index + 1}", combat);
                    CheckPrediction(predicted, actor, $"KUSARIGAMA_ATTACK_{index + 1}");
                    int totalDamage = combat.Enemies.Select((creature, enemyIndex) =>
                        hpBefore[enemyIndex] - creature.CurrentHp).Sum();
                    if (totalDamage != (index == 2 ? 12 : 6))
                        throw new InvalidOperationException("Kusarigama attack threshold dealt incorrect native damage.");
                    if (index == 1 && input.VerifyKusarigamaOwnerAndReset)
                    {
                        Player teammate = combat.Players.Single(player => player != actor);
                        int[] hpBeforeTeammate = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                        ContinuationStamp teammatePrediction = PredictOrdinaryCard(
                            teammate, "STRIKE_IRONCLAD", target);
                        CardModel teammateStrike = teammate.PlayerCombatState!.Hand.Cards.First(card =>
                            card.Id.Entry == "STRIKE_IRONCLAD");
                        var teammatePlay = new PlayCardAction(teammateStrike, target);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammatePlay);
                        await teammatePlay.CompletionTask;
                        await runner.MultiplayerProbeBarrierAsync("kusarigama-teammate-attack", combat);
                        CheckPrediction(teammatePrediction, teammate, "KUSARIGAMA_TEAMMATE_ATTACK");
                        if (combat.Enemies.Select((creature, enemyIndex) =>
                                hpBeforeTeammate[enemyIndex] - creature.CurrentHp).Sum() != 6)
                            throw new InvalidOperationException("Teammate attack advanced Kusarigama's owner counter.");
                    }
                }
                if (input.VerifyKusarigamaOwnerAndReset)
                {
                    ContinuationStamp nextRoundPrediction = PredictMultiplayerRound(combat, actor);
                    var end = new EndPlayerTurnAction(actor, 1);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                    await end.CompletionTask;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                        player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                    await runner.MultiplayerProbeBarrierAsync("kusarigama-next-turn", combat);
                    CheckPrediction(nextRoundPrediction, actor, "KUSARIGAMA_NEXT_TURN");
                    await UnattendedTestRunner.InjectCardAsync(combat, actor,
                        new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
                    int[] hpBeforeResetAttack = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                    ContinuationStamp resetPrediction = PredictOrdinaryCard(actor, "STRIKE_IRONCLAD", target);
                    CardModel resetStrike = actor.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var resetPlay = new PlayCardAction(resetStrike, target);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(resetPlay);
                    await resetPlay.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("kusarigama-reset-attack", combat);
                    CheckPrediction(resetPrediction, actor, "KUSARIGAMA_RESET_ATTACK");
                    if (combat.Enemies.Select((creature, enemyIndex) =>
                            hpBeforeResetAttack[enemyIndex] - creature.CurrentHp).Sum() != 6)
                        throw new InvalidOperationException("Kusarigama owner counter did not reset next turn.");
                    runner._completedChecks.Add("MultiplayerContent:Kusarigama:TeammateIgnored:NextTurnReset:FullState:FullRng");
                    return new ExecutionOutcome(false, 2, true, true, true, false);
                }
                runner._completedChecks.Add("MultiplayerContent:Kusarigama:ThirdOwnerAttack:RandomTarget:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyRandomRelicHooks)
            {
                Player actor = scenario.Player;
                await UnattendedTestRunner.InjectRelicAsync(actor,
                    new UnattendedRelicInjection { RelicId = "TINGSHA" });
                await UnattendedTestRunner.InjectRelicAsync(actor,
                    new UnattendedRelicInjection { RelicId = "FORGOTTEN_SOUL" });
                await UnattendedTestRunner.InjectRelicAsync(actor,
                    new UnattendedRelicInjection { RelicId = "PARRYING_SHIELD" });
                if (combat.Enemies.Count(creature => creature.IsAlive) < 2)
                    throw new InvalidOperationException("Random relic probe requires two live native enemies.");
                int[] hpBeforeDiscard = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                CardModel discardCard = actor.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "DEFEND_IRONCLAD");
                CombatRootSnapshot discardRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator discardSimulator = discardRoot.ForkSimulator();
                PredictedCard predictedDiscard = discardSimulator.State.GetPlayerCombatState(actor).Hand.Cards
                    .Single(card => ReferenceEquals(card.Original, discardCard));
                discardSimulator.Discard(predictedDiscard);
                if (!CombatBeamSolver.SettleReplayActionBoundary(discardSimulator,
                        (SimulatedCombatState)discardSimulator.State.CombatState))
                    throw new InvalidOperationException("Tingsha discard prediction did not settle.");
                ContinuationStamp discardPrediction = ContinuationStamp.CapturePredicted(
                    actor, discardSimulator, 1, discardRoot.Forecast, 1);
                await CardCmd.Discard(new ThrowingPlayerChoiceContext(), discardCard);
                await runner.MultiplayerProbeBarrierAsync("tingsha-discard", combat);
                ContinuationStamp discardActual = ContinuationStamp.CaptureLive(combat);
                if (discardPrediction != discardActual)
                    throw new InvalidOperationException("Tingsha discard differs: "
                        + discardPrediction.DescribeFirstDifference(discardActual));
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeDiscard[index] - creature.CurrentHp == 3).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeDiscard[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Tingsha did not hit exactly one native enemy for 3.");
                int[] hpBeforeExhaust = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                CardModel exhaustCard = actor.PlayerCombatState.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                CombatRootSnapshot exhaustRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator exhaustSimulator = exhaustRoot.ForkSimulator();
                PredictedCard predictedExhaust = exhaustSimulator.State.GetPlayerCombatState(actor).Hand.Cards
                    .Single(card => ReferenceEquals(card.Original, exhaustCard));
                exhaustSimulator.Exhaust(predictedExhaust);
                if (!CombatBeamSolver.SettleReplayActionBoundary(exhaustSimulator,
                        (SimulatedCombatState)exhaustSimulator.State.CombatState))
                    throw new InvalidOperationException("Forgotten Soul exhaust prediction did not settle.");
                ContinuationStamp exhaustPrediction = ContinuationStamp.CapturePredicted(
                    actor, exhaustSimulator, 1, exhaustRoot.Forecast, 1);
                await CardCmd.Exhaust(new ThrowingPlayerChoiceContext(), exhaustCard);
                await runner.MultiplayerProbeBarrierAsync("forgotten-soul-exhaust", combat);
                ContinuationStamp exhaustActual = ContinuationStamp.CaptureLive(combat);
                if (exhaustPrediction != exhaustActual)
                    throw new InvalidOperationException("Forgotten Soul exhaust differs: "
                        + exhaustPrediction.DescribeFirstDifference(exhaustActual));
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeExhaust[index] - creature.CurrentHp == 1).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeExhaust[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Forgotten Soul did not hit exactly one native enemy for 1.");
                await UnattendedTestRunner.SetBlockAsync(actor.Creature, 10);
                int[] hpBeforeTurn = combat.Enemies.Select(creature => creature.CurrentHp).ToArray();
                ContinuationStamp turnPrediction = PredictMultiplayerRound(combat, actor);
                var end = new EndPlayerTurnAction(actor, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("parrying-shield-turn", combat);
                CheckPrediction(turnPrediction, actor, "PARRYING_SHIELD_TURN");
                if (combat.Enemies.Where((creature, index) =>
                        hpBeforeTurn[index] - creature.CurrentHp == 6).Count() != 1
                    || combat.Enemies.Where((creature, index) =>
                        hpBeforeTurn[index] != creature.CurrentHp).Count() != 1)
                    throw new InvalidOperationException("Parrying Shield did not hit exactly one native enemy for 6.");
                runner._completedChecks.Add("MultiplayerContent:ThreeRandomRelicHooks:SharedTargetsRng:FullState");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyDeadAllyGroupPower || input.VerifyDeadAllyGroupEnergy)
            {
                Player actor = scenario.Player;
                Player teammate = combat.Players.Single(player => player != actor);
                teammate.Creature.SetCurrentHpInternal(0);
                int teammateEnergy = teammate.PlayerCombatState!.Energy;
                string cardId = input.VerifyDeadAllyGroupPower ? "ONE_FOR_ALL" : "ENERGY_SURGE";
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = cardId, Pile = "Hand" });
                ContinuationStamp groupPrediction = PredictOrdinaryCard(actor, cardId, null);
                CardModel groupCard = actor.PlayerCombatState!.Hand.Cards.Single(card =>
                    card.Id.Entry == cardId);
                var play = new PlayCardAction(groupCard, null);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                await play.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("dead-ally-group-power", combat);
                CheckPrediction(groupPrediction, actor, "DEAD_ALLY_GROUP_POWER");
                if (!teammate.Creature.IsDead)
                    throw new InvalidOperationException("Dead ally group fixture lost its death state.");
                if (input.VerifyDeadAllyGroupPower)
                {
                    if (actor.Creature.GetPowerAmount<OneForAllPower>() != 3
                        || teammate.Creature.GetPowerAmount<OneForAllPower>() != 3)
                        throw new InvalidOperationException("One For All did not apply to every original player.");
                    runner._completedChecks.Add("MultiplayerContent:OneForAll:DeadAllyIncluded:FullState:FullRng");
                }
                else
                {
                    if (actor.PlayerCombatState.Energy != 4
                        || teammate.PlayerCombatState.Energy != teammateEnergy)
                        throw new InvalidOperationException("Energy Surge gave energy to a dead teammate.");
                    runner._completedChecks.Add("MultiplayerContent:EnergySurge:DeadAllyExcluded:FullState:FullRng");
                }
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyBeetleDamageWake)
            {
                Creature beetle = combat.Enemies.Single(creature => creature.Monster is SlumberingBeetle);
                SlumberPower slumber = beetle.GetPower<SlumberPower>()
                    ?? throw new InvalidOperationException("Beetle has no Slumber power.");
                await PowerCmd.Decrement(slumber);
                await PowerCmd.Decrement(slumber);
                await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(), beetle, beetle.Block, null);
                ContinuationStamp wakePrediction = PredictOrdinaryCard(
                    scenario.Player, "STRIKE_IRONCLAD", beetle);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var attack = new PlayCardAction(strike, beetle);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(attack);
                await attack.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("beetle-damage-wake", combat);
                CheckPrediction(wakePrediction, scenario.Player, "BEETLE_DAMAGE_WAKE");
                if (beetle.Monster!.NextMove.Id != "STUNNED"
                    || beetle.HasPower<SlumberPower>())
                    throw new InvalidOperationException("Unblocked damage did not wake and stun the beetle.");
                ContinuationStamp stunnedPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var end = new EndPlayerTurnAction(scenario.Player, 1);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await runner.MultiplayerProbeBarrierAsync("beetle-after-stun", combat);
                CheckPrediction(stunnedPrediction, scenario.Player, "BEETLE_AFTER_STUN");
                if (beetle.HasPower<PlatingPower>() || !((SlumberingBeetle)beetle.Monster).IsAwake)
                    throw new InvalidOperationException("Beetle did not remove Plating after its stunned wake move.");
                runner._completedChecks.Add("MultiplayerContent:BeetleDamageWake:Stun:NextTurnPlatingRemoved:FullState:FullRng");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyPlayerDoomHook || input.VerifyPlayerDoomRound)
            {
                Player actor = input.VerifyPlayerDoomRound ? combat.Players[0] : scenario.Player;
                Player teammate = combat.Players.Single(player => player != actor);
                actor.AddRelicInternal(ModelDb.Relic<BookRepairKnife>().ToMutable());
                await CreatureCmd.SetCurrentHp(actor.Creature, 70);
                await CreatureCmd.SetCurrentHp(teammate.Creature, 1);
                DoomPower doom = await PowerCmd.Apply<DoomPower>(
                    new ThrowingPlayerChoiceContext(), teammate.Creature, 2,
                    actor.Creature, null)
                    ?? throw new InvalidOperationException("Doom fixture did not apply power.");
                if (input.VerifyPlayerDoomRound)
                {
                    ContinuationStamp? doomRoundPrediction = input.Seat == 0
                        ? PredictMultiplayerRound(combat, actor) : null;
                    foreach (Player player in input.IsVirtual ? new[] { actor } : combat.Players)
                    {
                        if (!input.IsVirtual && !LocalContext.IsMe(player))
                            continue;
                        var end = new EndPlayerTurnAction(player, 1);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                        if (input.Mode != "client")
                            await end.CompletionTask;
                    }
                    await runner.WaitForMultiplayerProbeAsync(() => actor.PlayerCombatState is
                        { Phase: PlayerTurnPhase.Play, TurnNumber: 2 } && teammate.Creature.IsDead);
                    await runner.MultiplayerProbeBarrierAsync("player-doom-round", combat);
                    if (doomRoundPrediction != null)
                        CheckPrediction(doomRoundPrediction, actor, "PLAYER_DOOM_ROUND");
                    if (actor.Creature.CurrentHp != 69)
                        throw new InvalidOperationException("Survivor health differs after the Doom heal and enemy attack.");
                    runner._completedChecks.Add("MultiplayerContent:PlayerDoomRound:TeammateDeath:OwnerRelicHeal:FullState:FullRng");
                    return new ExecutionOutcome(false, 2, true, true, true, false);
                }
                CombatRootSnapshot doomRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator doomSimulator = doomRoot.ForkSimulator();
                SimulatedCombatState doomCombat = (SimulatedCombatState)doomSimulator.State.CombatState;
                if (!EndTurnPowerSupport.TriggerRegular(doomSimulator, doomCombat,
                        CombatSide.Player, combat.PlayerCreatures))
                    throw new InvalidOperationException("Player Doom prediction did not finish.");
                ContinuationStamp predictedDoom = ContinuationStamp.CapturePredicted(
                    actor, doomSimulator, 1, doomRoot.Forecast, 1);
                await doom.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(),
                    CombatSide.Player, combat.PlayerCreatures);
                await runner.MultiplayerProbeBarrierAsync("player-doom-hook", combat);
                ContinuationStamp actualDoom = ContinuationStamp.CaptureLive(combat);
                if (!doomSimulator.State.GetCreature(teammate.Creature).IsDead
                    || !teammate.Creature.IsDead
                    || doomSimulator.State.GetCreature(actor.Creature).CurrentHp != 73
                    || actor.Creature.CurrentHp != 73
                    || predictedDoom.RngStateText != actualDoom.RngStateText)
                    throw new InvalidOperationException("Player Doom or repair knife effect differs: "
                        + predictedDoom.DescribeFirstDifference(actualDoom));
                runner._completedChecks.Add("MultiplayerContent:PlayerDoomHook:TeammateDeath:OwnerRelicHeal:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerSearchDrift)
            {
                SolverController.MonitorCombatPresence();
                int searchesBefore = SolverController.SearchesStartedForTesting;
                SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                Player teammate = combat.Players.First(player => player != scenario.Player);
                CardModel teammateCard = teammate.PlayerCombatState!.Hand.Cards
                    .First(card => card.Id.Entry == (input.VerifyControllerSearchRngDrift
                        ? "LARGESSE" : "STRIKE_IRONCLAD"));
                var teammatePlay = new PlayCardAction(teammateCard,
                    input.VerifyControllerSearchRngDrift ? scenario.Player.Creature : enemy);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammatePlay);
                await teammatePlay.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("search-drift", combat);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } driftFailure)
                    throw new InvalidOperationException("Changed multiplayer search failed.", driftFailure);
                SolverResult retained = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("Changed multiplayer search did not retain its route.");
                if (SolverController.SearchesStartedForTesting != searchesBefore + 1
                    || retained.MultiplayerStyle != MultiplayerPlanStyle.Output
                    || !SolverController.CanExecuteCurrentTurn)
                    throw new InvalidOperationException("Teammate action during search discarded the legal local route.");
                if (input.VerifyControllerSearchRngDrift
                    && (!SolverOverlay.MultiplayerRngDeviationSeenForTesting
                        || !SolverOverlay.MultiplayerRngReevaluatedForTesting))
                    throw new InvalidOperationException("Search-time teammate RNG change was not reported after re-evaluation.");
                SolverController.RequestDeploy(runner._host, combat);
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                    && SolverController.LastSolverDeployedTurnForBugReport == 1
                    && CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player));
                if (SolverController.SearchesStartedForTesting != searchesBefore + 1)
                    throw new InvalidOperationException("Search drift deployment started an extra full search.");
                runner._completedChecks.Add(input.VerifyControllerSearchRngDrift
                    ? "MultiplayerController:SearchTimeTeammateRng:RouteReevaluated:LocalDeployment:NoFullRescan"
                    : "MultiplayerController:SearchTimeTeammateDamage:RouteReevaluated:LocalDeployment:NoFullRescan");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerAutomaticCalculation || input.VerifyControllerFullAuto)
            {
                SolverController.MonitorCombatPresence();
                int searchesBefore = SolverController.SearchesStartedForTesting;
                if (input.VerifyControllerAutomaticCalculation)
                {
                    UnattendedTestRunner.EnableAutomaticTurnSearchForTesting();
                    SolverController.SetAutomaticCalculationEnabled(true);
                }
                else
                {
                    if (input.VerifyControllerAutoNextTurn)
                        UnattendedTestRunner.EnableAutomaticTurnSearchForTesting();
                    SolverController.SetFullAuto(runner._host, combat, true);
                }
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } automaticFailure)
                    throw new InvalidOperationException("Automatic multiplayer search failed.", automaticFailure);
                SolverResult automaticResult = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("Automatic multiplayer search did not publish a result.");
                if (SolverController.SearchesStartedForTesting != searchesBefore + 1
                    || automaticResult.MultiplayerStyle != MultiplayerPlanStyle.Output
                    || automaticResult.BestNode.Actions.Any(action => action.Turn == 1
                        && action.Kind == PlanActionKind.PlayCard
                        && !scenario.Player.PlayerCombatState!.AllCards.Any(card => card.Id.Entry == action.CardId)))
                    throw new InvalidOperationException("Automatic multiplayer search did not retain a local plan.");
                if (input.VerifyControllerAutomaticCalculation)
                {
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsSearching);
                    if (SolverController.IsDeploying
                        || CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player))
                        throw new InvalidOperationException("Automatic calculation deployed without user execution.");
                    runner._completedChecks.Add("MultiplayerController:AutomaticCalculation:LocalPlan:NoDeployment");
                }
                else
                {
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                        && SolverController.LastSolverDeployedTurnForBugReport == 1
                        && CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player));
                    PlanAction[] localPlannedCards = automaticResult.BestNode.Actions
                        .Where(action => action.Turn == 1 && action.Kind == PlanActionKind.PlayCard)
                        .ToArray();
                    if (!SolverController.FullAutoEnabled
                        || localPlannedCards.Length == 0
                        || localPlannedCards.Any(action => !SolverController.WasCardDeployedForTesting(action.CardId))
                        || input.IsVirtual && combat.Players.Where(player => player != scenario.Player)
                            .Any(CombatManager.Instance.IsPlayerReadyToEndTurn))
                        throw new InvalidOperationException("Full auto did not deploy its local cards and finish its local turn.");
                    if (!input.IsVirtual)
                    {
                        if (!input.VerifyControllerAutoNextTurn)
                            SolverController.SetFullAuto(runner._host, combat, false);
                        await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                            player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                        await runner.MultiplayerProbeBarrierAsync("full-auto-second-turn", combat);
                        if (input.VerifyControllerAutoNextTurn)
                        {
                            await runner.WaitForMultiplayerProbeAsync(() =>
                                SolverController.SearchesStartedForTesting >= searchesBefore + 2);
                            await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                                && SolverController.LastSolverDeployedTurnForBugReport == 2
                                && (CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player)
                                    || scenario.Player.PlayerCombatState?.TurnNumber == 3));
                            SolverController.SetFullAuto(runner._host, combat, false);
                            await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                                player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                            await runner.MultiplayerProbeBarrierAsync("full-auto-third-turn", combat);
                            runner._completedChecks.Add("MultiplayerController:FullAuto:SecondTurnSearchAndDeployment:ThirdTurnFullState:FullRng");
                            return new ExecutionOutcome(false, 3, true, true, true, false);
                        }
                        runner._completedChecks.Add("MultiplayerController:FullAuto:EnetPeers:LocalDeployment:SecondTurn:FullState:FullRng");
                        return new ExecutionOutcome(false, 2, true, true, true, false);
                    }
                    runner._completedChecks.Add("MultiplayerController:FullAuto:LocalPlan:NativeDeployment:TeammateUntouched");
                }
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerSearch)
            {
                if (input.VerifyControllerProjectedDeathContinuation)
                {
                    CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards
                        .First(card => card.Id.Entry == "STRIKE_IRONCLAD");
                    foreach (CardModel other in scenario.Player.PlayerCombatState.Hand.Cards
                                 .Where(card => card != strike).ToArray())
                        if (!(await CardPileCmd.Add(other, PileType.Discard)).success)
                            throw new InvalidOperationException("Projected-death fixture could not isolate Strike.");
                }
                SolverController.MonitorCombatPresence();
                if (!SolverOverlay.IsVisible)
                    throw new InvalidOperationException("Multiplayer manual controls were not attached.");
                SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("Multiplayer controller search failed.", failure);
                SolverResult completed = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("Multiplayer controller did not publish a result.");
                if (completed.MultiplayerStyle != MultiplayerPlanStyle.Output
                    || completed.BestNode.Actions.Any(action => action.Kind == PlanActionKind.PlayCard
                        && !scenario.Player.PlayerCombatState!.AllCards.Any(card =>
                            card.Id.Entry == action.CardId)))
                    throw new InvalidOperationException("Multiplayer controller published a non-local route.");
                runner._completedChecks.Add("MultiplayerController:ManualSearch:LocalResult:VisibleOverlay");
                if (input.VerifyControllerProjectedDeathContinuation)
                {
                    if (completed.BestNode.Actions.FirstOrDefault(action => action.Kind == PlanActionKind.PlayCard)
                        is not { CardId: "STRIKE_IRONCLAD" } plannedStrike)
                        throw new InvalidOperationException("Projected-death fixture needs a planned Strike.");
                    await CreatureCmd.SetCurrentHp(scenario.Player.Creature, 1);
                    if (!LiveEndTurnRiskEvaluator.Evaluate(combat, null).PlayerDead)
                        throw new InvalidOperationException("Projected-death fixture is not lethal after end turn.");
                    CombatRootSnapshot changedRoot = CombatRootSnapshot.Capture(combat);
                    SearchPolicySnapshot changedPolicy = SolverController.CaptureSearchPolicy(
                        SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
                    CombatBeamSolver replay = new(changedRoot, SolverDisplayNames.Capture(combat),
                        BattleDamageTracker.Observe(combat), changedPolicy);
                    SolverCurrentTurnPreview? revised = replay.ReevaluateMultiplayerCurrentTurn(completed);
                    if (revised == null || revised.Actions.FirstOrDefault()?.CardId != "STRIKE_IRONCLAD")
                        throw new InvalidOperationException("Legal Strike was rejected by the projected-death replay.");
                    Creature target = combat.GetCreature(plannedStrike.TargetCombatId)
                        ?? throw new InvalidOperationException("Projected-death Strike target is missing.");
                    int enemyHp = target.CurrentHp;
                    SolverController.RequestDeploy(runner._host, combat);
                    await runner.WaitForMultiplayerProbeAsync(() => target.CurrentHp == enemyHp - 6);
                    SolverController.SetSolverDisabled(true, persist: false);
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
                    if (!scenario.Player.Creature.IsAlive
                        || CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player))
                        throw new InvalidOperationException("Projected death stopped a still-legal current action.");
                    runner._completedChecks.Add("MultiplayerController:ProjectedDeath:LegalStrikeExecuted:NoPrematureTurnEnd");
                    return new ExecutionOutcome(false, 1, true, true, true, false);
                }
                if (input.VerifyControllerDeploy)
                {
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsSearching);
                    if (!SolverController.CanExecuteCurrentTurn)
                        throw new InvalidOperationException("Multiplayer controller route is not executable.");
                    PlanAction[] plannedCards = completed.BestNode.Actions
                        .Where(action => action.Turn == 1 && action.Kind == PlanActionKind.PlayCard)
                        .ToArray();
                    if (plannedCards.Length == 0)
                        throw new InvalidOperationException("Deployment fixture has no planned local card.");
                    if ((input.VerifyControllerMidDeploymentKill || input.VerifyControllerMidDeploymentDamage
                            || input.VerifyControllerManualTakeover
                            || input.VerifyControllerMidDeploymentRng)
                        && (plannedCards.Length < 2
                            || plannedCards[0].TargetCombatId == null
                            || plannedCards[1].TargetCombatId != plannedCards[0].TargetCombatId))
                        throw new InvalidOperationException("Mid-deployment fixture needs two attacks on one enemy.");
                    int searchesBeforeDrift = SolverController.SearchesStartedForTesting;
                    if (input.VerifyControllerTeammateDrift)
                    {
                        Player teammate = combat.Players.First(player => player != scenario.Player);
                        CardModel teammateCard = teammate.PlayerCombatState!.Hand.Cards
                            .First(card => card.Id.Entry == (input.VerifyControllerPreDeployRng
                                ? "LARGESSE" : "STRIKE_IRONCLAD"));
                        Creature teammateTarget = input.VerifyControllerTeammateKillsTarget
                            ? combat.GetCreature(plannedCards.First(action => action.TargetCombatId != null).TargetCombatId)
                                ?? throw new InvalidOperationException("Planned enemy target disappeared before probe setup.")
                            : combat.Enemies.Single();
                        if (input.VerifyControllerTeammateKillsTarget)
                            await CreatureCmd.SetCurrentHp(teammateTarget, 6);
                        var teammatePlay = new PlayCardAction(teammateCard,
                            input.VerifyControllerPreDeployRng ? scenario.Player.Creature : teammateTarget);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammatePlay);
                        await teammatePlay.CompletionTask;
                        await runner.MultiplayerProbeBarrierAsync("teammate-drift", combat);
                        SolverOverlay.RefreshControls();
                        if (!SolverController.MultiplayerPredictionNeedsReview
                            || SolverOverlay.RouteHeadingForTesting?.Contains(
                                SolverText.Get("已保存路线（数值待更新）"), StringComparison.Ordinal) != true)
                            throw new InvalidOperationException("Teammate action did not mark stale multiplayer numbers.");
                        if (SolverOverlay.ExecuteButtonDisabledForTesting)
                            throw new InvalidOperationException("Teammate action disabled the retained local route button.");
                        if (input.VerifyControllerPreDeployRng
                            && (!SolverController.MultiplayerRngNeedsReview
                                || !SolverOverlay.MultiplayerRngDeviationSeenForTesting
                                || SolverOverlay.SearchSummaryTextForTesting?.Contains(
                                    SolverText.Get("随机流曾变化，后续预测可能不准。"),
                                    StringComparison.Ordinal) != true))
                            throw new InvalidOperationException("Pre-deployment teammate RNG change was not explained in the overlay.");
                    }
                    if (input.VerifyControllerTeammateDrift
                        && !input.VerifyControllerTeammateKillsTarget)
                        SolverOverlay.PressExecuteButtonForTesting();
                    else
                        SolverController.RequestDeploy(runner._host, combat);
                    if (input.VerifyControllerMidDeploymentKill || input.VerifyControllerMidDeploymentDamage
                        || input.VerifyControllerManualTakeover
                        || input.VerifyControllerMidDeploymentRng)
                    {
                        Creature target = combat.GetCreature(plannedCards[0].TargetCombatId)
                            ?? throw new InvalidOperationException("Mid-deployment target is missing.");
                        int startingHp = target.CurrentHp;
                        await runner.WaitForMultiplayerProbeAsync(() => target.CurrentHp == startingHp - 6
                            && SolverController.IsDeploying);
                        if (input.VerifyControllerManualTakeover)
                        {
                            Player remotePlayer = combat.Players.First(player => player != scenario.Player);
                            int teammateEnergy = remotePlayer.PlayerCombatState!.Energy;
                            SolverController.SetSolverDisabled(true, persist: false);
                            await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
                            if (target.CurrentHp != startingHp - 6
                                || scenario.Player.PlayerCombatState!.Energy != 2
                                || remotePlayer.PlayerCombatState.Energy != teammateEnergy
                                || CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player)
                                || SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                                throw new InvalidOperationException("Manual takeover failed to stop the local deployment only.");
                            runner._completedChecks.Add("MultiplayerController:ManualTakeover:FirstLocalAttackOnly:TeammateUntouched:NoFullSearch");
                            return new ExecutionOutcome(false, 1, true, true, true, false);
                        }
                        if (input.VerifyControllerMidDeploymentKill)
                            await CreatureCmd.SetCurrentHp(target, 6);
                        Creature teammateTarget = input.VerifyControllerMidDeploymentKill
                            ? target : combat.Enemies.Single(candidate => candidate != target);
                        int teammateTargetHp = teammateTarget.CurrentHp;
                        Player teammate = combat.Players.First(player => player != scenario.Player);
                        CardModel teammateCard = teammate.PlayerCombatState!.Hand.Cards
                            .First(card => card.Id.Entry == (input.VerifyControllerMidDeploymentRng
                                ? "LARGESSE" : "STRIKE_IRONCLAD"));
                        HashSet<CardModel> localHandBefore = [.. scenario.Player.PlayerCombatState!.Hand.Cards];
                        var teammatePlay = new PlayCardAction(teammateCard,
                            input.VerifyControllerMidDeploymentRng ? scenario.Player.Creature : teammateTarget);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammatePlay);
                        await teammatePlay.CompletionTask;
                        CardModel? generatedForLocal = input.VerifyControllerMidDeploymentRng
                            ? scenario.Player.PlayerCombatState!.AllCards.SingleOrDefault(card =>
                                !localHandBefore.Contains(card) && card.Id.Entry is not
                                    ("STRIKE_IRONCLAD" or "DEFEND_IRONCLAD" or "SURVIVOR"))
                            : null;
                        await runner.MultiplayerProbeBarrierAsync("mid-deployment-drift", combat);
                        await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
                        if (input.VerifyControllerMidDeploymentRng)
                        {
                            if (target.CurrentHp != startingHp - 12
                                || generatedForLocal is null
                                || !ReferenceEquals(generatedForLocal.Owner, scenario.Player)
                                || !scenario.Player.PlayerCombatState!.AllCards.Contains(generatedForLocal)
                                || teammate.PlayerCombatState!.AllCards.Contains(generatedForLocal)
                                || teammate.PlayerCombatState!.Hand.Cards.Any(card =>
                                    card.Id.Entry == "LARGESSE")
                                || !SolverOverlay.MultiplayerRngDeviationSeenForTesting
                                || !SolverOverlay.MultiplayerRngReevaluatedForTesting
                                || SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                                throw new InvalidOperationException(
                                    $"Teammate Largesse RNG change failed: target_hp={target.CurrentHp}/{startingHp - 12} " +
                                    $"generated_for_local={generatedForLocal?.Id.Entry ?? "-"} " +
                                    $"local_cards={string.Join(',', scenario.Player.PlayerCombatState.Hand.Cards.Select(card => card.Id.Entry))} " +
                                    $"teammate_cards={string.Join(',', teammate.PlayerCombatState.Hand.Cards.Select(card => card.Id.Entry))} " +
                                    $"rng_hint={SolverOverlay.MultiplayerRngDeviationSeenForTesting} " +
                                    $"reevaluated={SolverOverlay.MultiplayerRngReevaluatedForTesting} " +
                                    $"searches={SolverController.SearchesStartedForTesting}/{searchesBeforeDrift}.");
                            runner._completedChecks.Add("MultiplayerController:MidDeploymentTeammateLargesse:RecipientLocal:RngHint:Reevaluated:BothLocalAttacks:NoFullSearch");
                            return new ExecutionOutcome(false, 1, true, true, true, false);
                        }
                        if (input.VerifyControllerMidDeploymentDamage)
                        {
                            if (target.CurrentHp != startingHp - 12
                                || teammateTarget.CurrentHp != teammateTargetHp - 6
                                || scenario.Player.PlayerCombatState!.Energy != 1
                                || !CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player)
                                || SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                                throw new InvalidOperationException("Legal mid-deployment teammate damage lost the original local route.");
                            runner._completedChecks.Add("MultiplayerController:MidDeploymentTeammateDamage:BothLocalAttacks:Ready:NoFullSearch");
                            return new ExecutionOutcome(false, 1, true, true, true, false);
                        }
                        if (scenario.Player.PlayerCombatState!.Energy != 2
                            || CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player)
                            || SolverController.SearchesStartedForTesting != searchesBeforeDrift
                            || SolverOverlay.SearchSummaryTextForTesting?.Contains(
                                SolverText.Get("原路线在当前状态已失效，未执行。请重新计算。"),
                                StringComparison.Ordinal) != true)
                            throw new InvalidOperationException("Mid-deployment target loss did not pause before the second attack.");
                        runner._completedChecks.Add("MultiplayerController:MidDeploymentTeammateKill:FirstAttackOnly:Paused:NoFullSearch");
                        return new ExecutionOutcome(false, 1, true, true, true, false);
                    }
                    if (input.VerifyControllerTeammateDrift
                        && SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                        throw new InvalidOperationException("Teammate damage started a new search instead of retaining the legal route.");
                    if (input.VerifyControllerPreDeployRng)
                    {
                        await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                            && SolverController.LastSolverDeployedTurnForBugReport == 1);
                        if (!SolverOverlay.MultiplayerRngReevaluatedForTesting
                            || SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                            throw new InvalidOperationException("Pre-deployment RNG drift did not re-evaluate the retained route.");
                        runner._completedChecks.Add("MultiplayerController:PreDeployTeammateRng:StaleHint:RouteReevaluated:NoFullSearch");
                        return new ExecutionOutcome(false, 1, true, true, true, false);
                    }
                    if (input.VerifyControllerTeammateKillsTarget)
                    {
                        if (SolverController.IsDeploying
                            || plannedCards.Any(action => SolverController.WasCardDeployedForTesting(action.CardId))
                            || SolverOverlay.SearchSummaryTextForTesting?.Contains(
                                SolverText.Get("原路线在当前状态已失效，未执行。请重新计算。"),
                                StringComparison.Ordinal) != true)
                            throw new InvalidOperationException(
                                $"Invalidated teammate target did not pause before local execution: " +
                                $"deploying={SolverController.IsDeploying} " +
                                $"deployed={string.Join(',', plannedCards.Where(action => SolverController.WasCardDeployedForTesting(action.CardId)).Select(action => action.CardId))} " +
                                $"summary={SolverOverlay.SearchSummaryTextForTesting} " +
                                $"enemy_alive={string.Join(',', combat.Enemies.Where(candidate => !candidate.IsDead).Select(candidate => candidate.Monster?.Id.Entry))}.");
                        runner._completedChecks.Add("MultiplayerController:TeammateKilledPlannedTarget:PausedBeforeAction:NoFullSearch");
                        return new ExecutionOutcome(false, 1, true, true, true, false);
                    }
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                        && SolverController.LastSolverDeployedTurnForBugReport == 1
                        && (CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player)
                            || scenario.Player.PlayerCombatState?.TurnNumber == 2));
                    bool cardsDeployed = plannedCards.All(action =>
                        SolverController.WasCardDeployedForTesting(action.CardId));
                    bool localReady = CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player);
                    int actualTurn = scenario.Player.PlayerCombatState?.TurnNumber ?? 0;
                    if (!cardsDeployed || !localReady && actualTurn != 2)
                        throw new InvalidOperationException(
                            $"Controller deployment did not complete its local route: cards={cardsDeployed} " +
                            $"ready={localReady} turn={actualTurn} " +
                            $"planned={string.Join(',', plannedCards.Select(action => action.CardId))}.");
                    if (input.VerifyControllerTeammateDrift
                        && SolverController.SearchesStartedForTesting != searchesBeforeDrift)
                        throw new InvalidOperationException("Teammate drift started a deferred full search.");
                    if (!input.IsVirtual)
                    {
                        await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                            player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                        await runner.MultiplayerProbeBarrierAsync("controller-second-turn", combat);
                        runner._completedChecks.Add("MultiplayerController:EnetPeers:LocalDeployment:SecondTurn:FullState:FullRng");
                        return new ExecutionOutcome(false, 2, true, true, true, false);
                    }
                    if (combat.Players.Where(player => player != scenario.Player)
                        .Any(CombatManager.Instance.IsPlayerReadyToEndTurn))
                        throw new InvalidOperationException("Controller ended a virtual teammate's turn.");
                    runner._completedChecks.Add("MultiplayerController:DeployCurrentLocalTurn:NativeCards:TeammateUntouched");
                    return new ExecutionOutcome(false, 1, true, true, true, false);
                }
            }
            if (input.VerifyEnemyPowerScaling)
            {
                await VerifyScaledPowerAsync<ArtifactPower>();
                await VerifyScaledPowerAsync<PlatingPower>();
                await VerifyScaledPowerAsync<SlipperyPower>();
                await VerifyScaledPowerAsync<SkittishPower>();
                await VerifyScaledPowerAsync<CurlUpPower>();
                if (input.VerifyAllEnemyPowerScaling)
                {
                    await VerifyScaledPowerAsync<PlowPower>();
                    await VerifyScaledPowerAsync<ReattachPower>();
                    await VerifyScaledPowerAsync<FlutterPower>();
                    await VerifyScaledPowerAsync<RegenPower>();
                    await VerifyScaledPowerAsync<RampartPower>();
                    await VerifyScaledPowerAsync<ShriekPower>();
                    await VerifyScaledPowerAsync<HardenedShellPower>();
                }
            }
            if (input.VerifySearch)
            {
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchPolicySnapshot defaultMultiplayerPolicy = SolverController.CaptureSearchPolicy(
                    settings with
                    {
                        MultiplayerTurnDepth = 2,
                        MultiplayerTimeLimitMilliseconds = 3_000,
                    }, combat, includeTurnSetup: false, theftPolicy: null);
                SearchPolicySnapshot customMultiplayerPolicy = SolverController.CaptureSearchPolicy(
                    settings with
                    {
                        MultiplayerTurnDepth = 3,
                        MultiplayerTimeLimitMilliseconds = 6_000,
                    }, combat, includeTurnSetup: false, theftPolicy: null);
                if (defaultMultiplayerPolicy.MaxTurnLayers != 2
                    || defaultMultiplayerPolicy.Profile.SoftTimeBudgetMilliseconds != 3_000
                    || customMultiplayerPolicy.MaxTurnLayers != 3
                    || customMultiplayerPolicy.Profile.SoftTimeBudgetMilliseconds != 6_000)
                    throw new InvalidOperationException("Multiplayer depth/time policy did not follow settings.");
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                    SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null) with
                {
                    Profile = SolverSearchProfile.Default with
                    {
                        MaxExpandedNodes = 10_000,
                        SoftTimeBudgetMilliseconds = 3_000,
                    },
                    BudgetOverrideMilliseconds = 3_000,
                    MaxDegreeOfParallelism = 1,
                    UseBeamWidthPortfolio = false,
                    EarlyTurnExplorationBudgetMilliseconds = 0,
                };
                SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                SolverResult result = await Task.Run(() => new CombatBeamSolver(
                    root, displayNames, damage,
                    policy, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                if (result.StartTurnNumber != 1 || result.BestNode.Actions.Count == 0
                    || result.SearchedTurns > policy.MaxTurnLayers
                    || result.BestNode.Actions.Any(action => action.Kind == PlanActionKind.PlayCard
                        && action.Turn == 1 && !scenario.Player.PlayerCombatState!.AllCards.Any(card =>
                            card.Id.Entry == action.CardId)))
                    throw new InvalidOperationException("Multiplayer search produced no legal local turn-one route.");
                SolverResult[] plans = [result, .. result.MultiplayerAlternatives];
                if (plans.Length is < 1 or > 3
                    || plans[0].MultiplayerStyle != MultiplayerPlanStyle.Output
                    || plans.Length != 1
                    || plans.Any(plan => plan.MultiplayerStyle == null
                        || plan.Snapshot.PlayerDead || plan.Snapshot.ProjectedPlayerHp <= 0)
                    || plans.Select(plan => plan.MultiplayerStyle).Distinct().Count() != plans.Length
                    || plans.Select(plan => string.Join('|', plan.BestNode.Actions
                            .Where(action => action.Turn == 1)
                            .Select(action => $"{action.Kind}:{action.CardId}:{action.TargetCombatId}:{action.Choice}")))
                        .Distinct(StringComparer.Ordinal).Count() != plans.Length)
                    throw new InvalidOperationException("Multiplayer search plan styles were invalid or duplicated.");
                runner._completedChecks.Add(
                    $"MultiplayerSearch:Players={input.PlayerCount}:Seat={input.Seat}:LocalActions:Budget=3000ms:Styles={string.Join(',', plans.Select(plan => plan.MultiplayerStyle))}");
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
            Dictionary<Player, CardModel> dampenedCards = new();
            if (input.VerifyKnightsDampenUpgraded)
            {
                foreach (Player member in combat.Players)
                {
                    dampenedCards.Add(member, (await UnattendedTestRunner.InjectCardAsync(combat, member,
                        new UnattendedCardInjection
                        { CardId = "BASH", Pile = "Discard", UpgradeLevels = 1 })).Single());
                }
                await runner.MultiplayerProbeBarrierAsync("knights-upgraded-cards", combat);
            }
            if (input.VerifyKnowledgeDemonChoiceBoundary)
            {
                bool stoppedAtExternalChoice = false;
                try { _ = PredictMultiplayerRound(combat, scenario.Player); }
                catch (NotSupportedException error) when
                    (error.Message.Contains("每名玩家分别选择诅咒", StringComparison.Ordinal))
                { stoppedAtExternalChoice = true; }
                if (!stoppedAtExternalChoice)
                    throw new InvalidOperationException("Knowledge Demon predicted teammates' unknown curse choices.");
                using (CardSelectCmd.UseSelector(new UnattendedCardSelector(["__FIRST__"]), localOnly: false))
                {
                    var end = new EndPlayerTurnAction(scenario.Player, 1);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                    await end.CompletionTask;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                        member.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                }
                await runner.MultiplayerProbeBarrierAsync("knowledge-demon-native-choices", combat);
                if (combat.Players.Any(member => !member.Creature.HasPower<DisintegrationPower>()))
                    throw new InvalidOperationException("Knowledge Demon did not resolve each player's native choice.");
                runner._completedChecks.Add("MultiplayerContent:KnowledgeDemon:ExternalChoiceBoundary:PerPlayerNativeChoice");
                Creature demon = combat.Enemies.Single(creature => creature.Monster is KnowledgeDemon);
                for (int turn = 2; turn <= 4; turn++)
                {
                    foreach (Player member in combat.Players)
                        await UnattendedTestRunner.SetBlockAsync(member.Creature, 100);
                    if (turn == 4)
                        await CreatureCmd.SetCurrentHp(demon, demon.MaxHp - 80);
                    ContinuationStamp prediction = PredictMultiplayerRound(combat, scenario.Player);
                    var nextTurn = new EndPlayerTurnAction(scenario.Player, turn);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(nextTurn);
                    await nextTurn.CompletionTask;
                    int expectedTurn = turn + 1;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                        member.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
                        && member.PlayerCombatState.TurnNumber == expectedTurn));
                    await runner.MultiplayerProbeBarrierAsync($"knowledge-demon-turn-{expectedTurn}", combat);
                    CheckPrediction(prediction, scenario.Player, $"KNOWLEDGE_DEMON_TURN_{expectedTurn}");
                }
                if (demon.CurrentHp != demon.MaxHp - 20)
                    throw new InvalidOperationException("Knowledge Demon did not heal 30 HP per player.");
                runner._completedChecks.Add("MultiplayerContent:KnowledgeDemon:PostChoiceThreeRounds:TwoPlayerHeal:FullState:FullRng");
                return new ExecutionOutcome(false, 5, true, true, true, false);
            }
            if (input.VerifyPlayerDeathRound)
            {
                Creature fabricator = combat.Enemies.Single(creature => creature.Monster is Fabricator);
                if (fabricator.Monster!.NextMove.Id != "FABRICATING_STRIKE_MOVE")
                    throw new InvalidOperationException("Player death fixture did not queue the group attack.");
                await CreatureCmd.SetCurrentHp(combat.Players[1].Creature, 1);
                await runner.MultiplayerProbeBarrierAsync("player-death-ready", combat);
            }
            ContinuationStamp? roundPrediction = input.VerifyRoundDifferential
                && (!input.VerifyPlayerDeathRound || input.Seat == 0)
                ? PredictMultiplayerRound(combat, scenario.Player) : null;
            if (input.VerifyPlayerDeathRound)
                await runner.MultiplayerProbeBarrierAsync("player-death-predicted", combat);
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
            if (input.VerifyPlayerDeathRound)
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players[0].PlayerCombatState is
                    { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }
                    && combat.Players[1].Creature.IsDead);
            else
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
            await runner.MultiplayerProbeBarrierAsync("second-turn", combat);
            CheckPrediction(roundPrediction, scenario.Player, "END_TURN");
            if (input.VerifyPlayerDeathRound)
            {
                if (!combat.Players[0].Creature.IsAlive || !combat.Players[1].Creature.IsDead)
                    throw new InvalidOperationException("Enemy group attack did not kill only the low-HP teammate.");
                runner._completedChecks.Add("MultiplayerContent:PlayerDeath:TeammateDead:SurvivorNextTurn:FullState:FullRng");
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyRatSummonAfterRound || input.VerifyRatSummonLimit)
            {
                Creature rat = combat.Enemies.First(creature => creature.Monster is TwoTailedRat);
                await CreatureCmd.SetCurrentHp(rat, 1);
                ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player,
                    "STRIKE_IRONCLAD", rat);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var killRat = new PlayCardAction(strike, rat);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(killRat);
                await killRat.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("rat-death", combat);
                CheckPrediction(deathPrediction, scenario.Player, "RAT_DEATH");
                if (!rat.IsDead || combat.Enemies.Count(creature => creature.IsAlive) != 2)
                    throw new InvalidOperationException("Two-tailed rat death did not free one monster slot.");
                if (input.VerifyRatSummonLimit)
                {
                    foreach (Creature survivor in combat.Enemies.Where(creature => creature.IsAlive))
                        ((TwoTailedRat)survivor.Monster!).CallForBackupCount = 3;
                }
                ContinuationStamp secondPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var secondEnd = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(secondEnd);
                await secondEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                    member.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("rat-summon-ready", combat);
                CheckPrediction(secondPrediction, scenario.Player, "RAT_SUMMON_READY");
                if (input.VerifyRatSummonLimit)
                {
                    if (combat.Enemies.Count(creature => creature.IsAlive) != 2
                        || combat.Enemies.Any(creature => creature.IsAlive
                            && creature.Monster is TwoTailedRat monster
                            && monster.NextMove.Id == "CALL_FOR_BACKUP_MOVE"))
                        throw new InvalidOperationException("Rats summoned after reaching the backup limit.");
                    runner._completedChecks.Add("MultiplayerContent:TwoTailedRat:SummonLimit:FullState:FullRng");
                    return new ExecutionOutcome(false, 3, true, true, true, false);
                }
                if (!combat.Enemies.Any(creature => creature.IsAlive
                    && creature.Monster is TwoTailedRat monster
                    && monster.NextMove.Id.Equals("CALL_FOR_BACKUP_MOVE")))
                    throw new InvalidOperationException("Rat summon fixture did not queue CALL_FOR_BACKUP_MOVE.");
                ContinuationStamp summonPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var thirdEnd = new EndPlayerTurnAction(scenario.Player, 3);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(thirdEnd);
                await thirdEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                    member.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                await runner.MultiplayerProbeBarrierAsync("rat-summoned", combat);
                CheckPrediction(summonPrediction, scenario.Player, "RAT_SUMMONED");
                if (combat.Enemies.Count(creature => creature.IsAlive
                    && creature.Monster is TwoTailedRat) != 3)
                    throw new InvalidOperationException("Two-tailed rat did not refill the empty monster slot.");
                runner._completedChecks.Add("MultiplayerContent:TwoTailedRat:AllyDeathAndSummon:FullState:FullRng");
                return new ExecutionOutcome(false, 4, true, true, true, false);
            }
            if (input.VerifyThievingHopperPerPlayer)
            {
                Creature hopper = combat.Enemies.Single(creature => creature.Monster is ThievingHopper);
                SwipePower[] swipes = hopper.GetPowerInstances<SwipePower>().ToArray();
                if (swipes.Length != combat.Players.Count
                    || combat.Players.Any(member => swipes.Count(power =>
                        ReferenceEquals(power.Target, member.Creature)
                        && ReferenceEquals(power.StolenCard?.Owner, member)) != 1))
                    throw new InvalidOperationException("Thieving Hopper did not steal one deck card per player.");
                runner._completedChecks.Add("MultiplayerContent:ThievingHopper:PerPlayerSwipe:FullState:FullRng");
            }
            if (input.VerifyLivingShieldAllyDeath)
            {
                Creature operatorEnemy = combat.Enemies.Single(creature => creature.Monster is TurretOperator);
                Creature shield = combat.Enemies.Single(creature => creature.Monster is LivingShield);
                await CreatureCmd.SetCurrentHp(operatorEnemy, 6);
                await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(),
                    operatorEnemy, operatorEnemy.Block, null);
                ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player,
                    "STRIKE_IRONCLAD", operatorEnemy);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var strikeAction = new PlayCardAction(strike, operatorEnemy);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                await strikeAction.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("shield-ally-death", combat);
                CheckPrediction(deathPrediction, scenario.Player, "SHIELD_ALLY_DEATH");
                if (!operatorEnemy.IsDead || !shield.IsAlive)
                    throw new InvalidOperationException("Living Shield ally death fixture did not hold.");
                ContinuationStamp queuedPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var end = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("shield-queued-slam", combat);
                CheckPrediction(queuedPrediction, scenario.Player, "SHIELD_QUEUED_SLAM");
                if (shield.GetPowerAmount<StrengthPower>() != 0)
                    throw new InvalidOperationException("Living Shield changed its queued move after ally death.");
                ContinuationStamp followingPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var followingEnd = new EndPlayerTurnAction(scenario.Player, 3);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(followingEnd);
                await followingEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                await runner.MultiplayerProbeBarrierAsync("shield-following-smash", combat);
                CheckPrediction(followingPrediction, scenario.Player, "SHIELD_FOLLOWING_SMASH");
                if (shield.GetPowerAmount<StrengthPower>() != 3)
                    throw new InvalidOperationException("Living Shield did not gain Strength after ally death.");
                runner._completedChecks.Add("MultiplayerContent:LivingShield:AllyDeath:SmashStrength:FullState:FullRng");
                return new ExecutionOutcome(false, 4, true, true, true, false);
            }
            if (input.VerifyTestSubjectFirstRevive)
            {
                Creature subject = combat.Enemies.Single(creature => creature.Monster is TestSubject);
                await CreatureCmd.SetCurrentHp(subject, 6);
                await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(),
                    subject, subject.Block, null);
                ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player,
                    "STRIKE_IRONCLAD", subject);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var strikeAction = new PlayCardAction(strike, subject);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                await strikeAction.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("subject-dead", combat);
                CheckPrediction(deathPrediction, scenario.Player, "SUBJECT_DEAD");
                if (!subject.IsDead || !subject.HasPower<AdaptablePower>())
                    throw new InvalidOperationException("Test Subject did not enter its reviving death state.");
                ContinuationStamp revivePrediction = PredictMultiplayerRound(combat, scenario.Player);
                var end = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("subject-revived", combat);
                CheckPrediction(revivePrediction, scenario.Player, "SUBJECT_REVIVED");
                if (!subject.IsAlive || !subject.HasPower<PainfulStabsPower>())
                    throw new InvalidOperationException("Test Subject did not revive into its second form.");
                runner._completedChecks.Add("MultiplayerContent:TestSubject:FirstScaledRevive:FullState:FullRng");
                if (input.VerifyTestSubjectSecondRevive)
                {
                    await CreatureCmd.SetCurrentHp(subject, 6);
                    await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(),
                        subject, subject.Block, null);
                    ContinuationStamp secondDeathPrediction = PredictOrdinaryCard(scenario.Player,
                        "STRIKE_IRONCLAD", subject);
                    CardModel secondStrike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var secondStrikeAction = new PlayCardAction(secondStrike, subject);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(secondStrikeAction);
                    await secondStrikeAction.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("subject-second-dead", combat);
                    CheckPrediction(secondDeathPrediction, scenario.Player, "SUBJECT_SECOND_DEAD");
                    if (!subject.IsDead || !subject.HasPower<AdaptablePower>())
                        throw new InvalidOperationException("Test Subject did not enter its second reviving death state.");
                    ContinuationStamp secondRevivePrediction = PredictMultiplayerRound(combat, scenario.Player);
                    var secondEnd = new EndPlayerTurnAction(scenario.Player, 3);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(secondEnd);
                    await secondEnd.CompletionTask;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                        player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                    await runner.MultiplayerProbeBarrierAsync("subject-second-revived", combat);
                    CheckPrediction(secondRevivePrediction, scenario.Player, "SUBJECT_SECOND_REVIVED");
                    if (!subject.IsAlive || !subject.HasPower<NemesisPower>()
                        || subject.HasPower<AdaptablePower>() || subject.HasPower<PainfulStabsPower>())
                        throw new InvalidOperationException("Test Subject did not revive into its final form.");
                    runner._completedChecks.Add("MultiplayerContent:TestSubject:SecondScaledRevive:FullState:FullRng");
                    if (input.VerifyTestSubjectFinalDeath)
                    {
                        await CreatureCmd.SetCurrentHp(subject, 1);
                        await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(),
                            subject, subject.Block, null);
                        ContinuationStamp finalDeathPrediction = PredictOrdinaryCard(scenario.Player,
                            "STRIKE_IRONCLAD", subject);
                        CardModel finalStrike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                            card.Id.Entry == "STRIKE_IRONCLAD");
                        var finalStrikeAction = new PlayCardAction(finalStrike, subject);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(finalStrikeAction);
                        await finalStrikeAction.CompletionTask;
                        CheckPrediction(finalDeathPrediction, scenario.Player, "SUBJECT_FINAL_DEATH");
                        if (!subject.IsDead || !CombatManager.Instance.IsOverOrEnding)
                            throw new InvalidOperationException("Test Subject final death did not end combat.");
                        runner._completedChecks.Add("MultiplayerContent:TestSubject:FinalDeath:CombatEnd:FullRng");
                        return new ExecutionOutcome(true, 4, true, true, true, false);
                    }
                    return new ExecutionOutcome(false, 4, true, true, true, false);
                }
                return new ExecutionOutcome(false, 3, true, true, true, false);
            }
            if (input.VerifySegmentReattachAfterRound)
            {
                Creature segment = combat.Enemies.First(creature => creature.Monster is DecimillipedeSegment);
                await CreatureCmd.SetCurrentHp(segment, 1);
                ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player, "STRIKE_IRONCLAD", segment);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var strikeAction = new PlayCardAction(strike, segment);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                await strikeAction.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("segment-death", combat);
                CheckPrediction(deathPrediction, scenario.Player, "SEGMENT_DEATH");
                if (!segment.IsDead || combat.Enemies.Count(creature => creature.IsAlive) != 2)
                    throw new InvalidOperationException("Segment death fixture did not leave two living segments.");
                ContinuationStamp reattachPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var end = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("segment-reattach", combat);
                CheckPrediction(reattachPrediction, scenario.Player, "SEGMENT_REATTACH");
                if (segment.IsAlive)
                    throw new InvalidOperationException("Segment reattached during its dead move instead of the following move.");
                ContinuationStamp actualReattachPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var followingEnd = new EndPlayerTurnAction(scenario.Player, 3);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(followingEnd);
                await followingEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                await runner.MultiplayerProbeBarrierAsync("segment-revived", combat);
                CheckPrediction(actualReattachPrediction, scenario.Player, "SEGMENT_REVIVED");
                if (!segment.IsAlive)
                    throw new InvalidOperationException("Segment did not reattach after the enemy turn.");
                runner._completedChecks.Add("MultiplayerContent:SegmentDeathAndReattach:FullState:FullRng");
                return new ExecutionOutcome(false, 4, true, true, true, false);
            }
            if (input.VerifyMonsterDeathAfterRound)
            {
                Creature merc = combat.Enemies.Single(creature => creature.Monster is GremlinMerc);
                await CreatureCmd.SetCurrentHp(merc, 6);
                ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player, "STRIKE_IRONCLAD", merc);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var strikeAction = new PlayCardAction(strike, merc);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                await strikeAction.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("merc-death", combat);
                CheckPrediction(deathPrediction, scenario.Player, "MERC_DEATH");
                Creature fat = combat.Enemies.Single(creature => creature.Monster is FatGremlin);
                HeistPower[] heists = fat.GetPowerInstances<HeistPower>().ToArray();
                if (heists.Length != combat.Players.Count
                    || combat.Enemies.Count(creature => creature.Monster is SneakyGremlin) != 1
                    || combat.Players.Any(player => heists.Count(power =>
                        ReferenceEquals(power.Target, player.Creature) && power.Amount == 20) != 1))
                    throw new InvalidOperationException("Gremlin Merc death did not transfer each player's stolen gold.");
                runner._completedChecks.Add("MultiplayerContent:GremlinMercDeath:PerPlayerHeistTransfer:FullState:FullRng");
                if (input.VerifyHeistRecoveryAfterRound)
                {
                    await CreatureCmd.SetCurrentHp(fat, 6);
                    ContinuationStamp recoveryPrediction = PredictOrdinaryCard(scenario.Player, "STRIKE_IRONCLAD", fat);
                    CardModel secondStrike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var recoveryAction = new PlayCardAction(secondStrike, fat);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(recoveryAction);
                    await recoveryAction.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("heist-recovery", combat);
                    CheckPrediction(recoveryPrediction, scenario.Player, "HEIST_RECOVERY");
                    CombatRoom room = combat.RunState.CurrentRoom as CombatRoom
                        ?? throw new InvalidOperationException("Heist recovery combat room is missing.");
                    if (combat.Players.Any(player => !room.ExtraRewards.TryGetValue(player, out List<Reward>? rewards)
                        || rewards.OfType<GoldReward>().Count(reward => reward.Amount == 20) != 1))
                        throw new InvalidOperationException("Heist recovery did not return each player's stolen gold reward.");
                    runner._completedChecks.Add("MultiplayerContent:HeistRecovery:PerPlayerGoldReward:FullState:FullRng");
                }
                return new ExecutionOutcome(false, 2, true, true, true, false);
            }
            if (input.VerifyIllusionRevive)
            {
                Creature illusion = combat.Enemies.Single(creature => creature.Monster is Parafright);
                await CreatureCmd.SetCurrentHp(illusion, 6);
                await UnattendedTestRunner.InjectCardAsync(combat, scenario.Player,
                    new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
                ContinuationStamp deathPrediction = PredictOrdinaryCard(
                    scenario.Player, "STRIKE_IRONCLAD", illusion);
                CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                var strikeAction = new PlayCardAction(strike, illusion);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                await strikeAction.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync("illusion-death", combat);
                CheckPrediction(deathPrediction, scenario.Player, "ILLUSION_DEATH");
                IllusionPower power = illusion.GetPower<IllusionPower>()
                    ?? throw new InvalidOperationException("Parafright lost its illusion power after death.");
                if (!illusion.IsDead || !power.IsReviving || illusion.Monster!.NextMove.Id != "REVIVE_MOVE")
                    throw new InvalidOperationException("Parafright did not queue its revival after death.");
                ContinuationStamp revivePrediction = PredictMultiplayerRound(combat, scenario.Player);
                var end = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                await end.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("illusion-revived", combat);
                CheckPrediction(revivePrediction, scenario.Player, "ILLUSION_REVIVED");
                if (!illusion.IsAlive || power.IsReviving || illusion.CurrentHp != illusion.MaxHp)
                    throw new InvalidOperationException("Parafright did not revive at full health.");
                runner._completedChecks.Add("MultiplayerContent:ParafrightDeathAndRevive:FullState:FullRng");
                return new ExecutionOutcome(false, 3, true, true, true, false);
            }
            if (input.VerifySecondRoundDifferential)
            {
                ContinuationStamp secondRoundPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var secondEnd = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(secondEnd);
                await secondEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("third-turn", combat);
                CheckPrediction(secondRoundPrediction, scenario.Player, "SECOND_END_TURN");
                if (input.VerifyQueenAmalgamDeath)
                {
                    Creature queen = combat.Enemies.Single(creature => creature.Monster is Queen);
                    Creature amalgam = combat.Enemies.Single(creature => creature.Monster is TorchHeadAmalgam);
                    if (queen.Monster!.NextMove.Id != "BURN_BRIGHT_FOR_ME_MOVE")
                        throw new InvalidOperationException("Queen did not queue Burn Bright before amalgam death.");
                    await CreatureCmd.SetCurrentHp(amalgam, 1);
                    await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(), amalgam, amalgam.Block, null);
                    await UnattendedTestRunner.InjectCardAsync(combat, scenario.Player,
                        new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
                    ContinuationStamp deathPrediction = PredictOrdinaryCard(
                        scenario.Player, "STRIKE_IRONCLAD", amalgam);
                    CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var strikeAction = new PlayCardAction(strike, amalgam);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(strikeAction);
                    await strikeAction.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("queen-amalgam-death", combat);
                    CheckPrediction(deathPrediction, scenario.Player, "QUEEN_AMALGAM_DEATH");
                    if (!amalgam.IsDead || queen.Monster.NextMove.Id != "ENRAGE_MOVE")
                        throw new InvalidOperationException("Queen did not replace Burn Bright after amalgam death.");
                    ContinuationStamp enragePrediction = PredictMultiplayerRound(combat, scenario.Player);
                    var end = new EndPlayerTurnAction(scenario.Player, 3);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                    await end.CompletionTask;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                        player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                    await runner.MultiplayerProbeBarrierAsync("queen-enraged", combat);
                    CheckPrediction(enragePrediction, scenario.Player, "QUEEN_ENRAGED");
                    if (queen.GetPowerAmount<StrengthPower>() < 2)
                        throw new InvalidOperationException("Queen did not gain strength after enraging.");
                    runner._completedChecks.Add("MultiplayerContent:QueenAmalgamDeath:QueuedMoveReplaced:Enrage:FullState:FullRng");
                    return new ExecutionOutcome(false, 4, true, true, true, false);
                }
                if (input.VerifyFabricatorFullRoster)
                {
                    Creature fabricator = combat.Enemies.Single(creature => creature.Monster is Fabricator);
                    for (int turn = 3; turn <= 4; turn++)
                    {
                        foreach (Player member in combat.Players)
                            await UnattendedTestRunner.SetBlockAsync(member.Creature, 100);
                        ContinuationStamp prediction = PredictMultiplayerRound(combat, scenario.Player);
                        var end = new EndPlayerTurnAction(scenario.Player, turn);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                        await end.CompletionTask;
                        int expectedTurn = turn + 1;
                        await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                            member.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
                            && member.PlayerCombatState.TurnNumber == expectedTurn));
                        await runner.MultiplayerProbeBarrierAsync($"fabricator-turn-{expectedTurn}", combat);
                        CheckPrediction(prediction, scenario.Player, $"FABRICATOR_TURN_{expectedTurn}");
                    }
                    if (combat.GetTeammatesOf(fabricator).Count(creature => creature.IsAlive) != 4
                        || fabricator.Monster!.NextMove.Id != "DISINTEGRATE_MOVE")
                        throw new InvalidOperationException("Fabricator did not reach four living teammates and switch to Disintegrate.");
                    runner._completedChecks.Add("MultiplayerContent:Fabricator:ThreeMinions:FullRosterMove:FullState:FullRng");
                    if (!input.VerifyFabricatorMinionDeath)
                        return new ExecutionOutcome(false, 5, true, true, true, false);
                    Creature minion = combat.Enemies.First(creature => creature.IsAlive
                        && creature.Monster is Stabbot);
                    await CreatureCmd.SetCurrentHp(minion, 1);
                    ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player,
                        "STRIKE_IRONCLAD", minion);
                    CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var killMinion = new PlayCardAction(strike, minion);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(killMinion);
                    await killMinion.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("fabricator-minion-death", combat);
                    CheckPrediction(deathPrediction, scenario.Player, "FABRICATOR_MINION_DEATH");
                    if (!minion.IsDead || combat.GetTeammatesOf(fabricator).Count(creature => creature.IsAlive) != 3)
                        throw new InvalidOperationException("Fabricator minion death did not reopen one roster slot.");
                    for (int turn = 5; turn <= 6; turn++)
                    {
                        foreach (Player member in combat.Players)
                            await UnattendedTestRunner.SetBlockAsync(member.Creature, 100);
                        ContinuationStamp prediction = PredictMultiplayerRound(combat, scenario.Player);
                        var end = new EndPlayerTurnAction(scenario.Player, turn);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                        await end.CompletionTask;
                        int expectedTurn = turn + 1;
                        await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                            member.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
                            && member.PlayerCombatState.TurnNumber == expectedTurn));
                        await runner.MultiplayerProbeBarrierAsync($"fabricator-refill-turn-{expectedTurn}", combat);
                        CheckPrediction(prediction, scenario.Player, $"FABRICATOR_REFILL_TURN_{expectedTurn}");
                        if (turn == 5 && fabricator.Monster!.NextMove.Id is not
                            ("FABRICATE_MOVE" or "FABRICATING_STRIKE_MOVE"))
                            throw new InvalidOperationException("Fabricator did not reselect a summon after its queued attack.");
                    }
                    if (combat.GetTeammatesOf(fabricator).Count(creature => creature.IsAlive) < 4)
                        throw new InvalidOperationException("Fabricator did not refill its roster after minion death.");
                    runner._completedChecks.Add("MultiplayerContent:Fabricator:MinionDeath:QueuedAttack:Refill:FullState:FullRng");
                    return new ExecutionOutcome(false, 7, true, true, true, false);
                }
                if (input.VerifyKnightsDampenUpgraded)
                {
                    Creature mage = combat.Enemies.Single(creature => creature.Monster is MagiKnight);
                    if (dampenedCards.Any(pair => pair.Value.CurrentUpgradeLevel != 0
                        || !pair.Key.Creature.HasPower<DampenPower>()))
                        throw new InvalidOperationException("Magi Knight did not downgrade each player's upgraded card.");
                    await CreatureCmd.SetCurrentHp(mage, 6);
                    await CreatureCmd.LoseBlock(new ThrowingPlayerChoiceContext(), mage, mage.Block, null);
                    await UnattendedTestRunner.InjectCardAsync(combat, scenario.Player,
                        injection: new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
                    UnattendedTestRunner.SetEnergy(scenario.Player, 1);
                    ContinuationStamp deathPrediction = PredictOrdinaryCard(scenario.Player,
                        "STRIKE_IRONCLAD", mage);
                    CardModel strike = scenario.Player.PlayerCombatState!.Hand.Cards.First(card =>
                        card.Id.Entry == "STRIKE_IRONCLAD");
                    var killMage = new PlayCardAction(strike, mage);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(killMage);
                    await killMage.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("knights-dampen-restored", combat);
                    CheckPrediction(deathPrediction, scenario.Player, "KNIGHTS_DAMPEN_RESTORED");
                    if (!mage.IsDead || dampenedCards.Any(pair => pair.Value.CurrentUpgradeLevel != 1
                        || pair.Key.Creature.HasPower<DampenPower>()))
                        throw new InvalidOperationException("Magi Knight death did not restore each player's card.");
                    runner._completedChecks.Add("MultiplayerContent:KnightsDampen:TwoPlayersDowngradedAndRestored:FullState:FullRng");
                    return new ExecutionOutcome(false, 3, true, true, true, false);
                }
                if (input.UseFirstEnemyForProbe && runner._request.EncounterId == "OVICOPTER_NORMAL")
                {
                    ToughEgg[] eggs = combat.Enemies.Select(creature => creature.Monster)
                        .OfType<ToughEgg>().ToArray();
                    if (eggs.Length != 3 || eggs.Any(egg => !egg.IsHatched
                        || egg.Creature.HasPower<HatchPower>()))
                        throw new InvalidOperationException("Ovicopter eggs did not hatch before the third-turn diff.");
                    runner._completedChecks.Add("MultiplayerContent:ToughEgg:ThreeHatched:ScaledHp:FullState:FullRng");
                }
                if (input.VerifyThirdRoundDifferential)
                {
                    ContinuationStamp thirdRoundPrediction = PredictMultiplayerRound(combat, scenario.Player);
                    var thirdEnd = new EndPlayerTurnAction(scenario.Player, 3);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(thirdEnd);
                    await thirdEnd.CompletionTask;
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                        player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 4 }));
                    await runner.MultiplayerProbeBarrierAsync("fourth-turn", combat);
                    CheckPrediction(thirdRoundPrediction, scenario.Player, "THIRD_END_TURN");
                    if (input.VerifyFourthRoundDifferential)
                    {
                        Creature? giant = input.VerifyWaterfallSiphon
                            ? combat.Enemies.Single(creature => creature.Monster is WaterfallGiant)
                            : null;
                        if (giant != null)
                            await CreatureCmd.SetCurrentHp(giant, giant.CurrentHp - 40);
                        int giantHpBefore = giant?.CurrentHp ?? 0;
                        ContinuationStamp fourthRoundPrediction = PredictMultiplayerRound(combat, scenario.Player);
                        var fourthEnd = new EndPlayerTurnAction(scenario.Player, 4);
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(fourthEnd);
                        await fourthEnd.CompletionTask;
                        await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                            player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 5 }));
                        await runner.MultiplayerProbeBarrierAsync("fifth-turn", combat);
                        CheckPrediction(fourthRoundPrediction, scenario.Player, "FOURTH_END_TURN");
                        if (giant != null)
                        {
                            int expectedHeal = ((WaterfallGiant)giant.Monster!).SiphonHeal * combat.Players.Count;
                            if (giant.CurrentHp != giantHpBefore + expectedHeal)
                                throw new InvalidOperationException("Waterfall Giant Siphon did not heal by player count.");
                            runner._completedChecks.Add("MultiplayerContent:WaterfallSiphon:PlayerCountHealing:FullState:FullRng");
                        }
                        runner._completedChecks.Add("MultiplayerRoundDiff:FourthEnemyTurn:AllPlayers:FullRng");
                        return new ExecutionOutcome(false, 5, true, true, true, false);
                    }
                    runner._completedChecks.Add("MultiplayerRoundDiff:ThirdEnemyTurn:AllPlayers:FullRng");
                    return new ExecutionOutcome(false, 4, true, true, true, false);
                }
                runner._completedChecks.Add("MultiplayerRoundDiff:SecondEnemyTurn:AllPlayers:FullRng");
                return new ExecutionOutcome(false, 3, true, true, true, false);
            }
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
                    scenario.Player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
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

        private async Task<ExecutionOutcome> ExecuteEnetPaelsEyeExtraTurnProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Player hostPlayer = combat.Players[0];
            Player joiningPlayer = combat.Players[1];
            await UnattendedTestRunner.InjectRelicAsync(hostPlayer,
                new UnattendedRelicInjection { RelicId = "PAELS_EYE" });
            if (input.VerifyEnetPaelsEyeBothOwners)
                await UnattendedTestRunner.InjectRelicAsync(joiningPlayer,
                    new UnattendedRelicInjection { RelicId = "PAELS_EYE" });
            await runner.MultiplayerProbeBarrierAsync("paels-eye-equipped", combat);
            if (input.VerifyEnetPaelsEyeBothOwners)
            {
                Creature enemy = combat.Enemies.Single();
                int enemyHp = enemy.CurrentHp;
                if (input.Seat == 1)
                {
                    CardModel strike = joiningPlayer.PlayerCombatState!.Hand.Cards
                        .First(card => card.Id.Entry == "STRIKE_IRONCLAD");
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new PlayCardAction(strike, enemy));
                }
                await runner.WaitForMultiplayerProbeAsync(() => enemy.CurrentHp == enemyHp - 6);
                await runner.MultiplayerProbeBarrierAsync("paels-eye-joining-play", combat);
            }
            string readySignal = Path.Combine(input.CoordinationDirectory, "peer-0", "paels-eye-predicted.signal");
            ContinuationStamp? prediction = null;
            if (input.Seat == 0)
            {
                runner.SetStage("multiplayer_enet_paels_eye_predict");
                prediction = PredictMultiplayerRound(combat, hostPlayer);
                File.WriteAllText(readySignal, "predicted");
            }
            else
            {
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(readySignal));
            }
            runner.SetStage("multiplayer_enet_paels_eye_end");
            var end = new EndPlayerTurnAction(scenario.Player, 1);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            if (input.Seat == 0)
                await end.CompletionTask;
            else
                await runner.WaitForMultiplayerProbeAsync(() =>
                    CombatManager.Instance.IsPlayerReadyToEndTurn(scenario.Player));
            await runner.WaitForMultiplayerProbeAsync(() =>
                hostPlayer.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 });
            await runner.MultiplayerProbeBarrierAsync("paels-eye-extra-turn", combat);
            ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
            if (combat.RoundNumber != 1
                || joiningPlayer.PlayerCombatState?.TurnNumber != 1
                || !CombatManager.Instance.IsPartOfPlayerTurn(hostPlayer)
                || CombatManager.Instance.IsPartOfPlayerTurn(joiningPlayer)
                || prediction != null && prediction != actual)
                throw new InvalidOperationException("ENet Pael's Eye owner-only extra turn differs: "
                    + prediction?.DescribeFirstDifference(actual)
                    + $" round={combat.RoundNumber} host={hostPlayer.PlayerCombatState?.Phase}/{hostPlayer.PlayerCombatState?.TurnNumber}"
                    + $" join={joiningPlayer.PlayerCombatState?.Phase}/{joiningPlayer.PlayerCombatState?.TurnNumber}");
            if (input.VerifyEnetPaelsEyeBothOwners)
            {
                PaelsEye joiningEye = joiningPlayer.Relics.OfType<PaelsEye>().Single();
                if (joiningEye._wasOwnerPartOfLastPlayerTurn || joiningEye._usedThisCombat)
                    throw new InvalidOperationException("Excluded Pael's Eye owner retained extra-turn eligibility.");
            }
            runner._completedChecks.Add($"MultiplayerPaelsEye:Enet:Seat={input.Seat}:OwnerOnlyExtraTurn:FullState:FullRng");
            string followingSignal = Path.Combine(input.CoordinationDirectory,
                "peer-0", "paels-eye-following-predicted.signal");
            ContinuationStamp? followingPrediction = null;
            if (input.Seat == 0)
            {
                runner.SetStage("multiplayer_enet_paels_eye_following_predict");
                followingPrediction = PredictMultiplayerRound(combat, hostPlayer);
                File.WriteAllText(followingSignal, "predicted");
            }
            else
            {
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(followingSignal));
            }
            runner.SetStage("multiplayer_enet_paels_eye_following_end");
            if (input.Seat == 0)
            {
                var extraTurnEnd = new EndPlayerTurnAction(hostPlayer, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(extraTurnEnd);
                await extraTurnEnd.CompletionTask;
            }
            await runner.WaitForMultiplayerProbeAsync(() =>
                hostPlayer.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }
                && joiningPlayer.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 });
            await runner.MultiplayerProbeBarrierAsync("paels-eye-following-round", combat);
            ContinuationStamp followingActual = ContinuationStamp.CaptureLive(combat);
            if (combat.RoundNumber != 2
                || !CombatManager.Instance.IsPartOfPlayerTurn(hostPlayer)
                || !CombatManager.Instance.IsPartOfPlayerTurn(joiningPlayer)
                || followingPrediction != null && followingPrediction != followingActual)
                throw new InvalidOperationException("ENet Pael's Eye following round differs: "
                    + followingPrediction?.DescribeFirstDifference(followingActual)
                    + $" round={combat.RoundNumber} host={hostPlayer.PlayerCombatState?.Phase}/{hostPlayer.PlayerCombatState?.TurnNumber}"
                    + $" join={joiningPlayer.PlayerCombatState?.Phase}/{joiningPlayer.PlayerCombatState?.TurnNumber}");
            if (input.VerifyEnetPaelsEyeBothOwners)
            {
                PaelsEye joiningEye = joiningPlayer.Relics.OfType<PaelsEye>().Single();
                if (!joiningEye._wasOwnerPartOfLastPlayerTurn || joiningEye._usedThisCombat)
                    throw new InvalidOperationException("Returning Pael's Eye owner did not regain turn eligibility.");
            }
            runner._completedChecks.Add($"MultiplayerPaelsEye:Enet:Seat={input.Seat}:FollowingSharedRound:FullState:FullRng");
            return new ExecutionOutcome(false, 3, true, true, true, false);
        }

        private async Task<ExecutionOutcome> ExecuteEnetTutorChoiceProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Player actor = combat.Players[0];
            Player teammate = combat.Players[1];
            CardModel tutor = actor.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "TUTOR");
            CardModel[] offered = teammate.PlayerCombatState!.DrawPile.Cards.ToArray();
            if (offered.Length != 2 || teammate.PlayerCombatState.Hand.Cards.Count != 5)
                throw new InvalidOperationException("ENet Tutor fixture needs two teammate draw-pile choices.");
            if (actor.PlayerCombatState.Hand.Cards.Count != 1)
                throw new InvalidOperationException("ENet Tutor fixture needs only Tutor in the host hand.");
            SolverController.MonitorCombatPresence();
            SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
            await runner.WaitForMultiplayerProbeAsync(() =>
                SolverController.LastCompletedResultForTesting != null
                || SolverController.LastSearchFailureForTesting != null);
            if (SolverController.LastSearchFailureForTesting is { } failure)
                throw new InvalidOperationException("ENet Tutor search failed.", failure);
            SolverResult route = SolverController.LastCompletedResultForTesting
                ?? throw new InvalidOperationException("ENet Tutor search produced no route.");
            PlanAction[] plays = route.BestNode.Actions.Where(action => action.CardId == "TUTOR").ToArray();
            if (plays.Length != 1 || plays[0].TargetCombatId != teammate.Creature.CombatId
                || plays[0].Choice != null || route.BoundaryReason != SearchBoundaryReason.PendingChoice)
                throw new InvalidOperationException("ENet Tutor search did not retain the external teammate choice boundary.");
            SolverController.RequestDeploy(runner._host, combat);
            await runner.WaitForMultiplayerProbeAsync(() =>
                !actor.PlayerCombatState.Hand.Cards.Contains(tutor));
            string choiceSignal = Path.Combine(input.CoordinationDirectory, "peer-1", "tutor-choice.signal");
            await runner.WaitForMultiplayerProbeAsync(() => File.Exists(choiceSignal));
            await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
            File.WriteAllText(Path.Combine(input.CoordinationDirectory, "peer-0", "tutor-host-stable.signal"),
                "Tutor deployment completed");
            await runner.MultiplayerProbeBarrierAsync("enet-tutor-choice", combat);
            CardModel[] selected = offered.Where(card => teammate.PlayerCombatState.Hand.Cards.Contains(card)).ToArray();
            if (selected.Length != 1 || selected[0].Owner != teammate
                || teammate.PlayerCombatState.DrawPile.Cards.Count != 1
                || actor.PlayerCombatState!.AllCards.Contains(selected[0])
                || teammate.PlayerCombatState.Phase != PlayerTurnPhase.Play
                || CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
                throw new InvalidOperationException("ENet Tutor did not leave the chosen card with its teammate.");
            runner._completedChecks.Add("MultiplayerTutor:EnetNoSolverPeer:SearchDeploy:TeammateNativeChoice:OwnerAndPile:FullState:FullRng");
            return new ExecutionOutcome(false, 1, true, true, true, false);
        }

        private async Task<ExecutionOutcome> ExecuteEnetControllerRngProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Creature[] enemies = combat.Enemies.ToArray();
            Player hostPlayer = combat.Players[0];
            Player joiningPlayer = combat.Players[1];
            string hostSignal = Path.Combine(input.CoordinationDirectory, "peer-0", "first-attack.signal");
            string joinSignal = Path.Combine(input.CoordinationDirectory, "peer-1", "largesse-complete.signal");
            if (input.Seat == 0)
            {
                SolverController.MonitorCombatPresence();
                int searchesBefore = SolverController.SearchesStartedForTesting;
                HashSet<CardModel> localCardsBefore = [.. hostPlayer.PlayerCombatState!.AllCards];
                SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("ENet RNG controller search failed.", failure);
                SolverResult result = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("ENet RNG controller produced no route.");
                PlanAction[] attacks = result.BestNode.Actions.Where(action =>
                    action.Turn == 1 && action.CardId == "STRIKE_IRONCLAD").ToArray();
                if (attacks.Length != 2 || attacks.Any(action =>
                        enemies.All(target => target.CombatId != action.TargetCombatId)))
                    throw new InvalidOperationException("ENet RNG fixture needs two local attacks on living enemies.");
                Creature firstTarget = enemies.Single(target => target.CombatId == attacks[0].TargetCombatId);
                Dictionary<Creature, int> startingHp = enemies.ToDictionary(target => target, target => target.CurrentHp);
                SolverController.RequestDeploy(runner._host, combat);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    firstTarget.CurrentHp == startingHp[firstTarget] - 6 && SolverController.IsDeploying);
                File.WriteAllText(hostSignal, "first attack complete");
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(joinSignal)
                    && hostPlayer.PlayerCombatState!.AllCards.Any(card => !localCardsBefore.Contains(card)));
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
                CardModel generated = hostPlayer.PlayerCombatState!.AllCards.Single(card =>
                    !localCardsBefore.Contains(card));
                if (enemies.Any(target => target.CurrentHp != startingHp[target]
                        - 6 * attacks.Count(action => action.TargetCombatId == target.CombatId))
                    || !ReferenceEquals(generated.Owner, hostPlayer)
                    || joiningPlayer.PlayerCombatState!.AllCards.Contains(generated)
                    || !SolverOverlay.MultiplayerRngDeviationSeenForTesting
                    || !SolverOverlay.MultiplayerRngReevaluatedForTesting
                    || SolverController.SearchesStartedForTesting != searchesBefore + 1
                    || !CombatManager.Instance.IsPlayerReadyToEndTurn(hostPlayer)
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(joiningPlayer))
                    throw new InvalidOperationException("ENet teammate Largesse did not preserve the local route and RNG hint.");
            }
            else
            {
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(hostSignal));
                CardModel largesse = joiningPlayer.PlayerCombatState!.Hand.Cards.Single(card =>
                    card.Id.Entry == "LARGESSE");
                HashSet<CardModel> hostCardsBefore = [.. hostPlayer.PlayerCombatState!.AllCards];
                var teammatePlay = new PlayCardAction(largesse, hostPlayer.Creature);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammatePlay);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    !joiningPlayer.PlayerCombatState.Hand.Cards.Contains(largesse)
                    && hostPlayer.PlayerCombatState.AllCards.Any(card => !hostCardsBefore.Contains(card)));
                File.WriteAllText(joinSignal, "Largesse complete");
            }
            await runner.MultiplayerProbeBarrierAsync("enet-rng-drift", combat);
            runner._completedChecks.Add($"MultiplayerController:EnetRngDrift:Seat={input.Seat}:LocalRoute:TargetRecipient:FullState:FullRng");
            return new ExecutionOutcome(false, 1, true, true, true, false);
        }

        private async Task<ExecutionOutcome> ExecuteMultiplayerContentProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Player actor = scenario.Player;
            if (input.ContentCardIds.Any(id => id is "OMNISLICE" or "BEAT_DOWN" or "BOUNCING_FLASK")
                && combat.Enemies.Count < 2)
                throw new InvalidOperationException("Shared-target content probe requires two native enemies.");
            foreach (Player member in combat.Players)
                for (int index = 0; index < input.ContentExtraDrawCardsPerPlayer; index++)
                    await UnattendedTestRunner.InjectCardAsync(combat, member,
                        new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Draw" });
            for (int index = 0; index < input.ContentStokeHandCards; index++)
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
            for (int index = 0; index < input.ContentBeatDownDiscardAttacks; index++)
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Discard" });
            await UnattendedTestRunner.SetBlockAsync(actor.Creature, input.ContentActorBlock);
            await UnattendedTestRunner.SetBlockAsync(
                combat.Players[input.ContentTargetSeat].Creature, input.ContentTargetBlock);
            UnattendedTestRunner.SetEnergy(actor, input.ContentActorEnergy);
            UnattendedTestRunner.SetStars(actor, input.VerifyStarSupport ? 0 : 5);
            if (input.VerifyMidnightExhaustHistory)
            {
                Midnight midnight = actor.PlayerCombatState!.Hand.Cards.OfType<Midnight>().Single();
                for (int index = 0; index < 2; index++)
                {
                    CardModel toExhaust = (await UnattendedTestRunner.InjectCardAsync(combat, actor,
                        new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" })).Single();
                    CombatRootSnapshot exhaustRoot = CombatRootSnapshot.Capture(combat);
                    CombatPredictionSimulator exhaustSimulator = exhaustRoot.ForkSimulator();
                    PredictedCard predictedDefend = exhaustSimulator.State.GetPlayerCombatState(actor).Hand.Cards
                        .Single(card => ReferenceEquals(card.Original, toExhaust));
                    exhaustSimulator.Exhaust(predictedDefend);
                    SimulatedCombatState exhaustCombat = (SimulatedCombatState)exhaustSimulator.State.CombatState;
                    if (!CombatBeamSolver.SettleReplayActionBoundary(exhaustSimulator, exhaustCombat))
                        throw new InvalidOperationException("Midnight exhaust prediction did not settle.");
                    ContinuationStamp predictedExhaust = ContinuationStamp.CapturePredicted(
                        actor, exhaustSimulator, 1, exhaustRoot.Forecast, 1);
                    await CardCmd.Exhaust(new ThrowingPlayerChoiceContext(), toExhaust);
                    await runner.MultiplayerProbeBarrierAsync($"midnight-exhaust-{index}", combat);
                    ContinuationStamp actualExhaust = ContinuationStamp.CaptureLive(combat);
                    if (predictedExhaust != actualExhaust)
                        throw new InvalidOperationException("Midnight exhaust differs: "
                            + predictedExhaust.DescribeFirstDifference(actualExhaust));
                    if (midnight.EnergyCost.GetAmountToSpend() != 11 - index)
                        throw new InvalidOperationException("Midnight did not discount after a card exhaust.");
                    runner._completedChecks.Add($"MultiplayerMidnight:Exhaust={index + 1}:Cost={11 - index}:FullState:FullRng");
                }
            }
            if (input.ContentRelicId.Length > 0)
            {
                    string relicId = input.ContentRelicId;
                    await UnattendedTestRunner.InjectRelicAsync(actor,
                        new UnattendedRelicInjection { RelicId = relicId });
                    RelicModel relic = actor.Relics.Single(candidate => candidate.Id.Entry == relicId);
                    if (relic is InfusedCore)
                    {
                        await OrbCmd.AddSlots(actor, 3);
                        CombatRootSnapshot orbRoot = CombatRootSnapshot.Capture(combat);
                        CombatPredictionSimulator orbSimulator = orbRoot.ForkSimulator();
                        SimulatedCombatState orbCombat = (SimulatedCombatState)orbSimulator.State.CombatState;
                        if (!orbCombat.TriggerRelicsAfterSideTurnStart(orbSimulator,
                                CombatSide.Player, [actor.Creature])
                            || !CombatBeamSolver.SettleReplayActionBoundary(orbSimulator, orbCombat))
                            throw new InvalidOperationException("Infused Core prediction did not settle.");
                        ContinuationStamp predictedOrbs = ContinuationStamp.CapturePredicted(
                            actor, orbSimulator, 1, orbRoot.Forecast, 1);
                        await relic.AfterSideTurnStart(CombatSide.Player, [actor.Creature], combat);
                        await runner.MultiplayerProbeBarrierAsync("relic-INFUSED_CORE", combat);
                        ContinuationStamp actualOrbs = ContinuationStamp.CaptureLive(combat);
                        if (predictedOrbs != actualOrbs
                            || actor.PlayerCombatState!.OrbQueue.Orbs.Count != 3
                            || actor.PlayerCombatState.OrbQueue.Orbs.Any(orb => orb is not LightningOrb)
                            || combat.Players.Where(member => member != actor)
                                .Any(member => member.PlayerCombatState!.OrbQueue.Orbs.Count != 0))
                            throw new InvalidOperationException("Infused Core owner-only channel differs: "
                                + predictedOrbs.DescribeFirstDifference(actualOrbs));
                        runner._completedChecks.Add("MultiplayerRelic:InfusedCore:OwnerThreeLightning:TeammateNone:FullState:FullRng");
                        return new ExecutionOutcome(false, 1, true, true, true, false);
                    }
                    int actorHandBefore = actor.PlayerCombatState!.Hand.Cards.Count;
                    HashSet<CardModel> actorHandCardsBefore = [.. actor.PlayerCombatState.Hand.Cards];
                    int[] teammateHandsBefore = combat.Players.Where(member => member != actor)
                        .Select(member => member.PlayerCombatState!.Hand.Cards.Count).ToArray();
                    CombatRootSnapshot relicRoot = CombatRootSnapshot.Capture(combat);
                    CombatPredictionSimulator relicSimulator = relicRoot.ForkSimulator();
                    SimulatedCombatState relicCombat = (SimulatedCombatState)relicSimulator.State.CombatState;
                    string selectedGeneratedCardId = "";
                    TurnStartChoiceCursor relicChoices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                    {
                        CardChoiceSpec spec = TurnStartChoiceSupport.BuildSpec(relicSimulator, actor, request);
                        selectedGeneratedCardId = spec.Options.First().Preview.Id.Entry;
                        return CardChoiceSupport.BuildRequestedChoice(spec, [selectedGeneratedCardId]) with
                        {
                            SourceId = request.SourceId,
                            ContextId = request.ContextId,
                            Timing = request.Timing,
                        };
                    });
                    if (relic is VexingPuzzlebox)
                    {
                        if (relicCombat.ApplyRelicAfterPlayerTurnStart(relicSimulator, actor,
                                new TurnStartChoiceCursor(null), relic))
                            throw new InvalidOperationException("Vexing Puzzlebox prediction requested a choice.");
                    }
                    else if (relic is OrangeDough && !relicCombat.TriggerRelicsAfterSideTurnStart(relicSimulator,
                                 CombatSide.Player, combat.PlayerCreatures))
                        throw new InvalidOperationException("Orange Dough prediction requested a choice.");
                    else if (relic is Toolbox && relicCombat.PrepareRelicsBeforeHandDraw(
                                 relicSimulator, actor, relicChoices))
                        throw new InvalidOperationException("Toolbox prediction did not resolve its choice.");
                    else if (relic is ChoicesParadox && relicCombat.TriggerRelicsAfterPlayerTurnStart(
                                 relicSimulator, actor, relicChoices))
                        throw new InvalidOperationException("Choices Paradox prediction did not resolve its choice.");
                    if (!CombatBeamSolver.SettleReplayActionBoundary(relicSimulator, relicCombat))
                        throw new InvalidOperationException($"{relicId} prediction did not settle.");
                    ContinuationStamp predictedRelic = ContinuationStamp.CapturePredicted(
                        actor, relicSimulator, 1, relicRoot.Forecast, 1);
                    runner.SetStage($"multiplayer_relic_{relicId}");
                    using var selector = relic is Toolbox or ChoicesParadox
                        ? CardSelectCmd.UseSelector(new UnattendedCardSelector(["__FIRST__"]), localOnly: false)
                        : null;
                    if (relic is VexingPuzzlebox)
                        await relic.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), actor);
                    else if (relic is OrangeDough)
                        await relic.AfterSideTurnStart(CombatSide.Player, combat.PlayerCreatures, combat);
                    else if (relic is Toolbox)
                        await relic.BeforeHandDraw(actor, new BlockingPlayerChoiceContext(), combat);
                    else
                        await relic.AfterPlayerTurnStart(new BlockingPlayerChoiceContext(), actor);
                    await runner.MultiplayerProbeBarrierAsync($"relic-{relicId}", combat);
                    ContinuationStamp actualRelic = ContinuationStamp.CaptureLive(combat);
                    if (predictedRelic != actualRelic)
                        throw new InvalidOperationException($"{relicId} differs: "
                            + predictedRelic.DescribeFirstDifference(actualRelic));
                    int generatedCount = relic is OrangeDough ? 2 : 1;
                    CardModel[] generatedCards = actor.PlayerCombatState!.Hand.Cards
                        .Where(card => !actorHandCardsBefore.Contains(card)).ToArray();
                    if (actor.PlayerCombatState!.Hand.Cards.Count != actorHandBefore + generatedCount
                        || generatedCards.Length != generatedCount
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.PlayerCombatState!.Hand.Cards.Count)
                            .Where((count, index) => count != teammateHandsBefore[index]).Any()
                        || actor.PlayerCombatState.Hand.Cards.Any(card => card.Owner != actor))
                        throw new InvalidOperationException($"{relicId} generated cards for the wrong player.");
                    if (relic is Toolbox or ChoicesParadox
                        && (selectedGeneratedCardId.Length == 0
                            || generatedCards[0].Id.Entry != selectedGeneratedCardId
                            || relic is ChoicesParadox
                                && !generatedCards[0].Keywords.Contains(CardKeyword.Retain)))
                        throw new InvalidOperationException($"{relicId} choice or retain keyword differs.");
                    runner._completedChecks.Add($"MultiplayerRelic:{relicId}:OwnerOnly:FullState:FullRng");
                    await RelicCmd.Remove(relic);
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyWhisperingEarringTarget)
            {
                await UnattendedTestRunner.ClearPlayerPilesAsync(actor);
                await UnattendedTestRunner.InjectCardAsync(combat, actor,
                    new UnattendedCardInjection { CardId = "BLAZE", Pile = "Hand" });
                await UnattendedTestRunner.InjectRelicAsync(actor,
                    new UnattendedRelicInjection { RelicId = "WHISPERING_EARRING" });
                CombatRootSnapshot relicRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator relicSimulator = relicRoot.ForkSimulator();
                SimulatedCombatState relicCombat = (SimulatedCombatState)relicSimulator.State.CombatState;
                relicCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                try
                {
                    if (!relicCombat.TriggerWhisperingEarring(relicSimulator, actor, 1, new HashSet<uint>())
                        || !CombatBeamSolver.SettleReplayActionBoundary(relicSimulator, relicCombat))
                        throw new InvalidOperationException("Whispering Earring prediction did not complete.");
                }
                finally
                {
                    relicCombat.EndActionChoices();
                }
                ContinuationStamp predictedRelic = ContinuationStamp.CapturePredicted(
                    actor, relicSimulator, 1, relicRoot.Forecast, 1);
                WhisperingEarring relic = actor.Relics.OfType<WhisperingEarring>().Single();
                runner.SetStage("multiplayer_whispering_earring");
                await relic.AfterAutoPrePlayPhaseEnteredLate(new ThrowingPlayerChoiceContext(), actor);
                await runner.MultiplayerProbeBarrierAsync("whispering-earring", combat);
                ContinuationStamp actualRelic = ContinuationStamp.CaptureLive(combat);
                if (predictedRelic != actualRelic)
                    throw new InvalidOperationException(
                        "Whispering Earring differs: " + predictedRelic.DescribeFirstDifference(actualRelic));
                if (actor.Creature.GetPowerAmount<StrengthPower>() != 0
                    || combat.Players.Where(member => member != actor).Count(member =>
                        member.Creature.GetPowerAmount<StrengthPower>() == 5) != 1)
                    throw new InvalidOperationException("Whispering Earring Blaze did not target exactly one other player.");
                runner._completedChecks.Add("MultiplayerRelic:WhisperingEarring:BlazeOtherPlayer:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerSelfPotionDeploy)
            {
                SolverSettingsData originalSettings = SolverSettings.Current;
                try
                {
                    PotionModel potion = UnattendedTestRunner.InjectPotionForTest(actor, "BLOCK_POTION");
                    SolverSettings.Update(originalSettings with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne });
                    SolverController.MonitorCombatPresence();
                    SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                    await runner.WaitForMultiplayerProbeAsync(() =>
                        SolverController.LastCompletedResultForTesting != null
                        || SolverController.LastSearchFailureForTesting != null);
                    if (SolverController.LastSearchFailureForTesting is { } potionSearchFailure)
                        throw new InvalidOperationException("Multiplayer self-potion controller search failed.", potionSearchFailure);
                    SolverResult plan = SolverController.LastCompletedResultForTesting
                        ?? throw new InvalidOperationException("Self-potion controller search published no result.");
                    PlanAction[] plannedUses = plan.BestNode.Actions
                        .Where(action => action.Turn == 1 && action.Kind == PlanActionKind.UsePotion)
                        .ToArray();
                    if (plannedUses.Length != 1 || plannedUses[0].PotionId != potion.Id.Entry
                        || plannedUses[0].TargetCombatId is { } targetId
                            && targetId != actor.Creature.CombatId)
                        throw new InvalidOperationException("Multiplayer search did not plan only the owner's block potion.");
                    SolverController.RequestDeploy(runner._host, combat);
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                        && SolverController.LastSolverDeployedTurnForBugReport == 1
                        && CombatManager.Instance.IsPlayerReadyToEndTurn(actor));
                    if (!SolverController.WasPotionDeployedForTesting("BLOCK_POTION")
                        || actor.Creature.Block < 12
                        || combat.Players.Where(member => member != actor)
                            .Any(member => member.Creature.Block != 0))
                        throw new InvalidOperationException("Controller block potion was not used on the owner only.");
                    runner._completedChecks.Add("MultiplayerController:BlockPotion:OwnerOnly:NativeDeployment:TeammatesUnchanged");
                    return new ExecutionOutcome(false, 1, true, true, true, false);
                }
                finally
                {
                    SolverSettings.Update(originalSettings);
                }
            }
            if (input.VerifyControllerStyleSelection)
            {
                SolverController.MonitorCombatPresence();
                SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("Multiplayer style controller search failed.", failure);
                SolverResult primary = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("Multiplayer search did not publish a result.");
                SolverResult[] options = [primary, .. primary.MultiplayerAlternatives];
                IReadOnlyList<SolverMultiplayerOptionSnapshot> presented = SolverController.MultiplayerOptionsForUi;
                MultiplayerPlanStyle alternativeStyle = input.ContentCardIds.Contains("INFLAME")
                    ? MultiplayerPlanStyle.Setup : MultiplayerPlanStyle.Defense;
                if (options.Length != 2
                    || options[0].MultiplayerStyle != MultiplayerPlanStyle.Output
                    || options[1].MultiplayerStyle != alternativeStyle
                    || presented.Count != 2 || !presented[0].Selected
                    || SolverOverlay.MultiplayerOptionCountForTesting != 2
                    || SolverOverlay.SelectedMultiplayerStyleForTesting != MultiplayerPlanStyle.Output)
                    throw new InvalidOperationException("Multiplayer style options were not shown in the overlay.");
                if (input.VerifyDashboardLayout)
                {
                    foreach (SolverResult option in options)
                    {
                        MultiplayerPlanStyle style = option.MultiplayerStyle
                            ?? throw new InvalidOperationException("Displayed multiplayer route has no style.");
                        string card = SolverOverlay.MultiplayerOptionTextForTesting(style);
                        int turn = option.StartTurnNumber;
                        string hpChange = HpChangeText.Signed(
                            option.HpRecoveredByTurn[turn] - option.HpLostByTurn[turn]);
                        if (!card.Contains(SolverText.Format(
                                $"本回合伤害 {option.EnemyHpLostByTurn[turn]} · 血量变化 {hpChange} HP"),
                                StringComparison.Ordinal)
                            || !card.Contains(SolverText.Format(
                                $"预计余血 {option.MultiplayerCurrentTurnProjectedHp} HP · 余 {option.EnergyLeftByTurn[turn]} 费"),
                                StringComparison.Ordinal))
                            throw new InvalidOperationException("Multiplayer dashboard mixes turn metrics or omits an option.");
                    }
                    for (int frame = 0; frame < 3; frame++)
                        await runner.NextFrameAsync();
                    var firstCard = SolverOverlay.MultiplayerOptionRectForTesting(
                        MultiplayerPlanStyle.Output);
                    var secondCard = SolverOverlay.MultiplayerOptionRectForTesting(
                        alternativeStyle);
                    if (Math.Abs(firstCard.Position.Y - secondCard.Position.Y) > 1
                        || secondCard.Position.X < firstCard.Position.X + firstCard.Size.X
                        || firstCard.Size.Y < 92 || secondCard.Size.Y < 92)
                        throw new InvalidOperationException("Multiplayer options are not aligned comparison cards.");
                    if (primary.SearchedTurns > 1)
                    {
                        if (!SolverOverlay.MultiplayerFutureTurnsVisibleForTesting
                            || SolverOverlay.MultiplayerLaterTurnVisibleForTesting)
                            throw new InvalidOperationException("Later multiplayer turns were not collapsed by default.");
                        SolverOverlay.PressMultiplayerFutureTurnsForTesting();
                        if (!SolverOverlay.MultiplayerLaterTurnVisibleForTesting)
                            throw new InvalidOperationException("Later multiplayer turns did not expand.");
                        SolverOverlay.PressMultiplayerFutureTurnsForTesting();
                        if (SolverOverlay.MultiplayerLaterTurnVisibleForTesting)
                            throw new InvalidOperationException("Later multiplayer turns did not collapse again.");
                    }
                    runner._completedChecks.Add("MultiplayerDashboard:SameTurnMetrics:LaterTurnsToggle");
                }
                if (input.VerifyVisibleOverlayCapture)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await runner.NextFrameAsync();
                    string screenshotPath = Path.Combine(input.CoordinationDirectory, "overlay-visible.png");
                    Godot.Error captureError = runner._host.GetViewport().GetTexture().GetImage()
                        .SavePng(screenshotPath);
                    if (captureError != Godot.Error.Ok)
                        throw new InvalidOperationException($"Visible multiplayer overlay capture failed: {captureError}.");
                    runner._completedChecks.Add("MultiplayerOptions:VisibleOverlayImageCaptured");
                }
                if (input.VerifyVisibleSettingsCapture)
                {
                    SolverOverlay.OpenMultiplayerSettingsForTesting();
                    await runner.NextFrameAsync();
                    SolverOverlay.ScrollMultiplayerSettingsIntoViewForTesting();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await runner.NextFrameAsync();
                    string screenshotPath = Path.Combine(input.CoordinationDirectory, "settings-visible.png");
                    Godot.Error captureError = runner._host.GetViewport().GetTexture().GetImage()
                        .SavePng(screenshotPath);
                    if (captureError != Godot.Error.Ok)
                        throw new InvalidOperationException($"Visible multiplayer settings capture failed: {captureError}.");
                    runner._completedChecks.Add("MultiplayerSettings:VisiblePerformancePageCaptured");
                }
                SolverOverlay.PressMultiplayerStyleForTesting(alternativeStyle);
                if (!ReferenceEquals(SolverController.LastCompletedResultForTesting, options[1])
                    || SolverOverlay.SelectedMultiplayerStyleForTesting != alternativeStyle
                    || options[0].BestNode.Actions.Where(action => action.Turn == 1)
                        .SequenceEqual(options[1].BestNode.Actions.Where(action => action.Turn == 1))
                    || SolverOverlay.SearchSummaryTextForTesting?.Contains(
                        "条件预测：队友后续不主动出牌；仅安排自己的动作。", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Selecting a multiplayer style did not update the route and assumptions.");
                runner._completedChecks.Add($"MultiplayerOptions:OutputAnd{alternativeStyle}:OverlaySelection:DistinctLocalActions:Assumption");
                if (input.VerifyControllerStyleDeploy)
                {
                    string expectedCard = alternativeStyle == MultiplayerPlanStyle.Setup
                        ? "INFLAME" : "DEFEND_IRONCLAD";
                    SolverController.RequestDeploy(runner._host, combat);
                    await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                        && SolverController.LastSolverDeployedTurnForBugReport == 1
                        && CombatManager.Instance.IsPlayerReadyToEndTurn(actor));
                    if (!SolverController.WasCardDeployedForTesting(expectedCard)
                        || SolverController.WasCardDeployedForTesting("STRIKE_IRONCLAD"))
                        throw new InvalidOperationException("Selected multiplayer style was not the deployed route.");
                    runner._completedChecks.Add($"MultiplayerOptions:{alternativeStyle}:SelectedRouteDeployed");
                }
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyControllerTargetedDeploy)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                string supportCardId = input.ContentCardIds.Single(id => id is "BLAZE" or "LARGESSE");
                HashSet<CardModel> teammateCardsBefore = [.. teammate.PlayerCombatState!.AllCards];
                string rngBeforeDeployment = ContinuationStamp.CaptureLive(combat).RngStateText;
                SolverController.MonitorCombatPresence();
                if (!SolverOverlay.IsVisible)
                    throw new InvalidOperationException("Targeted multiplayer controls were not attached.");
                SolverController.RequestSearch(runner._host, combat, SearchReason.Manual);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    SolverController.LastCompletedResultForTesting != null
                    || SolverController.LastSearchFailureForTesting != null);
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException("Targeted multiplayer controller search failed.", failure);
                SolverResult result = SolverController.LastCompletedResultForTesting
                    ?? throw new InvalidOperationException("Targeted search did not publish a route.");
                PlanAction[] current = result.BestNode.Actions.Where(action => action.Turn == 1).ToArray();
                if (current.All(action => action.CardId != "STRIKE_IRONCLAD")
                    || current.All(action => action.CardId != supportCardId
                        || action.TargetCombatId != teammate.Creature.CombatId)
                    || current.Any(action => action.CardId == supportCardId
                        && action.TargetCombatId == actor.Creature.CombatId))
                    throw new InvalidOperationException("Controller route did not preserve a legal teammate target.");
                SolverController.RequestDeploy(runner._host, combat);
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying
                    && SolverController.LastSolverDeployedTurnForBugReport == 1);
                if (!SolverController.WasCardDeployedForTesting(supportCardId)
                    || !CombatManager.Instance.IsPlayerReadyToEndTurn(actor)
                    || CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
                    throw new InvalidOperationException("Targeted deployment did not act on the teammate and end the local turn.");
                if (supportCardId == "BLAZE")
                {
                    if (teammate.Creature.Powers.OfType<StrengthPower>().All(power => power.Amount < 5)
                        || actor.Creature.Powers.OfType<StrengthPower>().Any())
                        throw new InvalidOperationException("Targeted deployment did not apply Blaze to the teammate only.");
                    runner._completedChecks.Add("MultiplayerController:BlazeTargetedTeammate:NativeDeployment:LocalTurnOnly");
                }
                else
                {
                    CardModel generated = teammate.PlayerCombatState!.AllCards.Single(card =>
                        !teammateCardsBefore.Contains(card));
                    if (!ReferenceEquals(generated.Owner, teammate)
                        || actor.PlayerCombatState!.AllCards.Contains(generated)
                        || ContinuationStamp.CaptureLive(combat).RngStateText == rngBeforeDeployment
                        || SolverOverlay.MultiplayerRngDeviationSeenForTesting)
                        throw new InvalidOperationException(
                            $"Own Largesse check failed: generated={generated.Id.Entry} " +
                            $"owner={generated.Owner.NetId}/{teammate.NetId} " +
                            $"in_hand={teammate.PlayerCombatState.Hand.Cards.Contains(generated)} " +
                            $"in_actor={actor.PlayerCombatState!.AllCards.Contains(generated)} " +
                            $"rng_changed={ContinuationStamp.CaptureLive(combat).RngStateText != rngBeforeDeployment} " +
                            $"rng_hint={SolverOverlay.MultiplayerRngDeviationSeenForTesting}.");
                    runner._completedChecks.Add("MultiplayerController:OwnLargesse:TargetTeammate:GeneratedCardRecipient:ExpectedRngNoFalseHint");
                }
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyAllyTarget)
            {
                if (input.VerifyDeadTeammateTarget)
                {
                    Player deadTeammate = combat.Players[1];
                    deadTeammate.Creature.SetCurrentHpInternal(0);
                    await runner.MultiplayerProbeBarrierAsync("dead-teammate", combat);
                    if (!deadTeammate.Creature.IsDead || !actor.Creature.IsAlive
                        || combat.Players.Any(member => member.PlayerCombatState is not
                            { Phase: PlayerTurnPhase.Play, TurnNumber: 1 }))
                        throw new InvalidOperationException("Dead teammate target fixture did not hold.");
                }
                CombatRootSnapshot targetRoot = CombatRootSnapshot.Capture(combat);
                SearchPolicySnapshot targetPolicy = SolverController.CaptureSearchPolicy(
                    SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
                CombatBeamSolver targetDriver = new(targetRoot, SolverDisplayNames.Capture(combat),
                    BattleDamageTracker.Observe(combat), targetPolicy);
                string firstRng = ContinuationStamp.CaptureLive(combat).StateText;
                uint[] targets = [];
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    CombatPredictionSimulator fork = targetRoot.ForkSimulator();
                    PredictedCard source = fork.State.GetPlayerCombatState(actor).Hand.Cards
                        .First(candidate => candidate.Preview.Id.Entry == input.ContentCardIds[0]);
                    if (input.VerifyAllyAfterEnergyGain)
                    {
                        if (source.Original.CanPlayTargeting(combat.Players[input.ContentTargetSeat].Creature))
                            throw new InvalidOperationException("Energy-gain fixture card is already live-playable.");
                        fork.GainEnergy(actor, 2);
                        if (!fork.CanPlay(source))
                            throw new InvalidOperationException("Energy-gain fixture card did not become simulated-playable.");
                    }
                    uint[] current = targetDriver.AllyTargetsForTesting(source, fork);
                    if (current.Length != 1
                        || !combat.Players.Any(member => member != actor
                            && member.Creature.CombatId == current[0]
                            && member.Creature.IsAlive
                            && source.Original.IsValidTarget(member.Creature))
                        || iteration > 0 && !targets.SequenceEqual(current))
                        throw new InvalidOperationException("Ally target was not legal and stable across forks.");
                    targets = current;
                }
                if (ContinuationStamp.CaptureLive(combat).StateText != firstRng)
                    throw new InvalidOperationException("Ally target selection changed game state or RNG.");
                runner._completedChecks.Add("MultiplayerAllyTarget:OneOtherLivingPlayer:StableForks:GameRngUnchanged");
                if (input.VerifyDeadTeammateTarget)
                {
                    foreach (int excludedSeat in new[] { 0, 1 })
                    {
                        CombatBeamSolver excludedDriver = new(targetRoot, SolverDisplayNames.Capture(combat),
                            BattleDamageTracker.Observe(combat), targetPolicy with
                            { MultiplayerAllyTargetSeatForTesting = excludedSeat });
                        CombatPredictionSimulator excludedFork = targetRoot.ForkSimulator();
                        PredictedCard excludedSource = excludedFork.State.GetPlayerCombatState(actor).Hand.Cards
                            .Single(candidate => candidate.Preview.Id.Entry == "BLAZE");
                        bool rejected = false;
                        try { _ = excludedDriver.AllyTargetsForTesting(excludedSource, excludedFork); }
                        catch (InvalidOperationException error) when
                            (error.Message == "Fixed ally target is not legal in this branch.")
                        { rejected = true; }
                        if (!rejected)
                            throw new InvalidOperationException("Blaze accepted its owner or dead teammate.");
                    }
                    runner._completedChecks.Add("MultiplayerAllyTarget:OwnerAndDeadTeammateRejected");
                }
                if (input.VerifyAllyAfterEnergyGain)
                {
                    runner._completedChecks.Add("MultiplayerAllyTarget:SimulatedEnergyGain:LiveUnplayable:TargetRetained");
                    return new ExecutionOutcome(false, 1, true, true, true, false);
                }
            }
            if (input.VerifySelfPotion)
            {
                if (input.SelfPotionId == "BLOOD_POTION")
                    await CreatureCmd.SetCurrentHp(actor.Creature, actor.Creature.MaxHp - 20);
                if (input.SelfPotionId == "ESSENCE_OF_DARKNESS")
                    await OrbCmd.AddSlots(actor, 2);
                if (input.VerifyPotionAccounting)
                {
                    BattleDamageTracker.Begin(combat);
                    Player teammate = combat.Players.First(member => member != actor);
                    PotionModel teammatePotion = UnattendedTestRunner.InjectPotionForTest(teammate, "STRENGTH_POTION");
                    var teammateUse = new UsePotionAction(teammatePotion, null, isCombatInProgress: true);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(teammateUse);
                    await teammateUse.CompletionTask;
                    await runner.MultiplayerProbeBarrierAsync("teammate-self-potion", combat);
                    if (BattleDamageTracker.Observe(combat).PotionsUsedSoFar != 0)
                        throw new InvalidOperationException("Teammate potion was counted as local use.");
                }
                PotionModel potion = UnattendedTestRunner.InjectPotionForTest(actor, input.SelfPotionId);
                var costsRngBefore = combat.RunState.Rng.CombatEnergyCosts.CaptureState();
                var shuffleRngBefore = combat.RunState.Rng.Shuffle.CaptureState();
                int energyBeforePotion = actor.PlayerCombatState!.Energy;
                int exhaustBeforePotion = actor.PlayerCombatState.ExhaustPile.Cards.Count;
                int actorBlockBeforePotion = actor.Creature.Block;
                int[] teammateBlocksBeforePotion = combat.Players.Where(member => member != actor)
                    .Select(member => member.Creature.Block).ToArray();
                HashSet<CardModel> existingHand = [.. actor.PlayerCombatState!.Hand.Cards];
                int[] teammateHandCounts = combat.Players.Where(member => member != actor)
                    .Select(member => member.PlayerCombatState!.Hand.Cards.Count).ToArray();
                var potionRngBefore = combat.RunState.Rng.CombatPotionGeneration.CaptureState();
                string?[] teammatePotionIds = combat.Players.Where(member => member != actor)
                    .SelectMany(member => member.PotionSlots).Select(item => item?.Id.Entry).ToArray();
                (CardModel Card, int Replay)[] holderStrikes = actor.PlayerCombatState.AllCards
                    .Where(card => card.Tags.Contains(CardTag.Strike))
                    .Select(card => (card, card.BaseReplayCount)).ToArray();
                (CardModel Card, int Replay)[] teammateStrikes = combat.Players.Where(member => member != actor)
                    .SelectMany(member => member.PlayerCombatState!.AllCards)
                    .Where(card => card.Tags.Contains(CardTag.Strike))
                    .Select(card => (card, card.BaseReplayCount)).ToArray();
                int[] playerHpBeforePotion = combat.Players.Select(member => member.Creature.CurrentHp).ToArray();
                int actorHpBeforePotion = actor.Creature.CurrentHp;
                int actorMaxHpBeforePotion = actor.Creature.MaxHp;
                int[] teammateHpBeforePotion = combat.Players.Where(member => member != actor)
                    .Select(member => member.Creature.CurrentHp).ToArray();
                int[] teammateMaxHpBeforePotion = combat.Players.Where(member => member != actor)
                    .Select(member => member.Creature.MaxHp).ToArray();
                int[] enemyHpBeforePotion = combat.Enemies.Select(enemy => enemy.CurrentHp).ToArray();
                int slot = actor.PotionSlots.ToList().IndexOf(potion);
                if (slot < 0)
                    throw new InvalidOperationException("Injected potion has no slot.");
                CombatRootSnapshot potionRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator potionSimulator = potionRoot.ForkSimulator();
                SearchPolicySnapshot potionPolicy = SolverController.CaptureSearchPolicy(
                    SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
                CombatBeamSolver potionDriver = new(potionRoot, SolverDisplayNames.Capture(combat),
                    BattleDamageTracker.Observe(combat), potionPolicy);
                (int Index, uint? TargetCombatId)[] potionTargets = potionDriver.PotionTargetsForTesting(
                    potion, potionSimulator);
                if (potionTargets.Length != 1 || potionTargets[0] != (-1, null))
                    throw new InvalidOperationException("Player-target potion generated a teammate-use candidate.");
                runner._completedChecks.Add("MultiplayerSelfPotion:SearchCandidateSelfOnly");
                SimulatedCombatState potionCombat = (SimulatedCombatState)potionSimulator.State.CombatState;
                int historyStart = potionSimulator.History.Entries.Count;
                if (!PotionExecutionSupport.Prepare(potionSimulator, potionCombat, potion, slot, null))
                    throw new InvalidOperationException("Self potion prediction did not prepare.");
                PlanCardChoice? potionChoice = null;
                if (PotionChoiceSupport.GeneratesCardChoice(potion))
                {
                    CardChoiceSpec spec = PotionChoiceSupport.GetSpec(potionSimulator, potion);
                    potionChoice = CardChoiceSupport.BuildChoices(
                            spec, SolverDisplayNames.Capture(combat), 256, 256)
                        .First(choice => choice.Cards.Count == 1) with { SourceId = potion.Id.Entry };
                }
                if (!PotionExecutionSupport.Complete(potionSimulator, potionCombat, potion,
                        null, potionChoice, historyStart, new HashSet<uint>())
                    || !CombatBeamSolver.SettleReplayActionBoundary(potionSimulator, potionCombat))
                    throw new InvalidOperationException("Self potion prediction did not complete.");
                ContinuationStamp predictedPotion = ContinuationStamp.CapturePredicted(
                    actor, potionSimulator, 1, potionRoot.Forecast, 1);
                runner.SetStage("multiplayer_self_potion");
                if (potionChoice == null)
                {
                    potion.EnqueueManualUse(null);
                }
                else
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
                    using var session = NativeChoiceRuntime.Begin(combat, actor, "test:multiplayer-self-choice-potion");
                    session.SetPlanAndStartDriving(NGame.Instance!, [potionChoice], deadline.Token);
                    GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                        queued => queued is UsePotionAction use && use.PotionIndex == (uint)slot
                            && ReferenceEquals(use.Player, actor),
                        () => potion.EnqueueManualUse(null), deadline.Token);
                    await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
                }
                await runner.WaitForMultiplayerProbeAsync(() => actor.GetPotionAtSlotIndex(slot) == null);
                await runner.MultiplayerProbeBarrierAsync("self-potion", combat);
                ContinuationStamp actualPotion = ContinuationStamp.CaptureLive(combat);
                if (predictedPotion != actualPotion)
                    throw new InvalidOperationException(
                        "Multiplayer self potion differs: " + predictedPotion.DescribeFirstDifference(actualPotion));
                if (potionChoice != null
                    && !actor.PlayerCombatState!.Hand.Cards.Any(card =>
                        card.Id.Entry == potionChoice.Cards[0].CardId && ReferenceEquals(card.Owner, actor)))
                    throw new InvalidOperationException("Choice potion did not give its generated card to its holder.");
                if (potion is AttackPotion or SkillPotion or PowerPotion or ColorlessPotion
                    or OrobicAcid or CosmicConcoction)
                {
                    CardModel[] generated = actor.PlayerCombatState!.Hand.Cards
                        .Where(card => !existingHand.Contains(card)).ToArray();
                    int expectedCount = potion is OrobicAcid or CosmicConcoction ? 3 : 1;
                    if (generated.Length != expectedCount || generated.Any(card => card.Owner != actor)
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.PlayerCombatState!.Hand.Cards.Count)
                            .Where((count, index) => count != teammateHandCounts[index]).Any())
                        throw new InvalidOperationException("Generated potion cards did not belong to the holder.");
                }
                if (input.SelfPotionId == "ESSENCE_OF_DARKNESS"
                    && (actor.PlayerCombatState!.OrbQueue.Orbs.Count != 2
                        || combat.Players.Any(member => member != actor
                            && member.PlayerCombatState!.OrbQueue.Orbs.Count != 0)))
                    throw new InvalidOperationException("Dark orb potion did not channel only to its holder.");
                if (input.SelfPotionId == "DISTILLED_CHAOS"
                    && (actor.Creature.Block != actorBlockBeforePotion + 15
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.Creature.Block)
                            .Where((block, index) => block != teammateBlocksBeforePotion[index]).Any()))
                    throw new InvalidOperationException("Distilled Chaos did not autoplay only the holder's three Defends.");
                if (input.SelfPotionId == "SNECKO_OIL"
                    && (actor.PlayerCombatState!.Hand.Cards.Count != 4
                        || combat.RunState.Rng.CombatEnergyCosts.CaptureState().Equals(costsRngBefore)
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.PlayerCombatState!.Hand.Cards.Count)
                            .Where((count, index) => count != teammateHandCounts[index]).Any()))
                    throw new InvalidOperationException("Snecko Oil did not randomize only the holder's hand costs.");
                if (potion is BottledPotential or Clarity or CureAll or GlowwaterPotion or SwiftPotion)
                {
                    int expectedHandCount = potion switch
                    {
                        Clarity => 2,
                        CureAll or GlowwaterPotion => 3,
                        BottledPotential or SwiftPotion => 4,
                        _ => throw new InvalidOperationException("Unknown draw potion fixture."),
                    };
                    if (actor.PlayerCombatState!.Hand.Cards.Count != expectedHandCount
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.PlayerCombatState!.Hand.Cards.Count)
                            .Where((count, index) => count != teammateHandCounts[index]).Any()
                        || potion is BottledPotential
                            && combat.RunState.Rng.Shuffle.CaptureState().Equals(shuffleRngBefore)
                        || potion is Clarity && actor.Creature.GetPowerAmount<ClarityPower>() != 3
                        || potion is CureAll && actor.PlayerCombatState.Energy != energyBeforePotion + 1
                        || potion is GlowwaterPotion
                            && actor.PlayerCombatState.ExhaustPile.Cards.Count != exhaustBeforePotion + 1)
                        throw new InvalidOperationException("Draw potion changed the wrong player's cards or resources.");
                }
                if (potion is EntropicBrew
                    && (actor.PotionSlots.Any(item => item == null || item.Owner != actor)
                        || combat.RunState.Rng.CombatPotionGeneration.CaptureState().Equals(potionRngBefore)
                        || !combat.Players.Where(member => member != actor)
                            .SelectMany(member => member.PotionSlots)
                            .Select(item => item?.Id.Entry).SequenceEqual(teammatePotionIds)))
                    throw new InvalidOperationException("Entropic Brew did not fill only the holder's potion slots.");
                if (potion is SoldiersStew
                    && (holderStrikes.Length == 0
                        || holderStrikes.Any(item => item.Card.BaseReplayCount != item.Replay + 1)
                        || teammateStrikes.Any(item => item.Card.BaseReplayCount != item.Replay)))
                    throw new InvalidOperationException("Soldier's Stew changed the wrong player's Strikes.");
                if (potion is BoneBrew
                    && (actor.Osty is not { IsAlive: true, MaxHp: 15 }
                        || combat.Players.Where(member => member != actor)
                            .Any(member => member.Osty != null)))
                    throw new InvalidOperationException("Bone Brew did not summon only the holder's Osty.");
                if (potion is FoulPotion
                    && (!combat.Players.Select((member, index) =>
                            member.Creature.CurrentHp < playerHpBeforePotion[index]).All(hit => hit)
                        || !combat.Enemies.Select((enemy, index) =>
                            enemy.CurrentHp < enemyHpBeforePotion[index]).All(hit => hit)))
                    throw new InvalidOperationException("Foul Potion did not damage every player and enemy.");
                if (potion is PotionOfBinding
                    && (combat.Enemies.Count < 2
                        || combat.Enemies.Any(enemy => enemy.GetPowerAmount<WeakPower>() != 1
                            || enemy.GetPowerAmount<VulnerablePower>() != 1)
                        || combat.Players.Any(member => member.Creature.GetPowerAmount<WeakPower>() != 0
                            || member.Creature.GetPowerAmount<VulnerablePower>() != 0)))
                    throw new InvalidOperationException("Potion of Binding did not debuff only both enemies.");
                if (potion is ShipInABottle
                    && (actor.Creature.Block != actorBlockBeforePotion + 10
                        || actor.Creature.GetPowerAmount<BlockNextTurnPower>() != 10
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.Creature.Block)
                            .Where((block, index) => block != teammateBlocksBeforePotion[index]).Any()
                        || combat.Players.Where(member => member != actor)
                            .Any(member => member.Creature.HasPower<BlockNextTurnPower>())))
                    throw new InvalidOperationException("Ship in a Bottle did not protect only its holder.");
                if (potion is FruitJuice
                    && (actor.Creature.MaxHp != actorMaxHpBeforePotion + 5
                        || actor.Creature.CurrentHp != actorHpBeforePotion + 5
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.Creature.MaxHp)
                            .Where((hp, index) => hp != teammateMaxHpBeforePotion[index]).Any()
                        || combat.Players.Where(member => member != actor)
                            .Select(member => member.Creature.CurrentHp)
                            .Where((hp, index) => hp != teammateHpBeforePotion[index]).Any()))
                    throw new InvalidOperationException("Fruit Juice changed the wrong player's maximum HP.");
                if (input.VerifyPotionAccounting)
                {
                    BattleDamageSnapshot observed = BattleDamageTracker.Observe(combat);
                    if (observed.PotionsUsedSoFar != 1
                        || !observed.PotionIdsUsedSoFar.SequenceEqual([input.SelfPotionId]))
                        throw new InvalidOperationException("Local potion accounting includes another player or misses self use.");
                    runner._completedChecks.Add("MultiplayerPotionAccounting:TeammateIgnored:LocalUseCounted");
                }
                runner._completedChecks.Add("MultiplayerSelfPotion:OwnerOnly:FullState:FullRng");
                if (potion is GlowwaterPotion)
                    return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifyEnemyPotionTargets)
            {
                PotionModel potion = UnattendedTestRunner.InjectPotionForTest(actor, "FIRE_POTION");
                CombatRootSnapshot potionRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator potionSimulator = potionRoot.ForkSimulator();
                SearchPolicySnapshot potionPolicy = SolverController.CaptureSearchPolicy(
                    SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
                CombatBeamSolver potionDriver = new(potionRoot, SolverDisplayNames.Capture(combat),
                    BattleDamageTracker.Observe(combat), potionPolicy);
                (int Index, uint? TargetCombatId)[] targets = potionDriver.PotionTargetsForTesting(
                    potion, potionSimulator);
                uint[] aliveEnemyIds = combat.Enemies.Where(enemy => enemy.IsAlive)
                    .Select(enemy => enemy.CombatId ?? throw new InvalidOperationException("Enemy has no combat ID."))
                    .ToArray();
                if (targets.Length != aliveEnemyIds.Length
                    || targets.Any(candidate => candidate.TargetCombatId is not { } targetId
                        || !aliveEnemyIds.Contains(targetId)))
                    throw new InvalidOperationException("Enemy potion targets did not match all living enemies.");
                runner._completedChecks.Add("MultiplayerEnemyPotion:AllLivingEnemies:NoPlayerTarget");
                if (input.VerifyEnemyPotionUse)
                {
                    Creature target = combat.Enemies.First(enemy => enemy.IsAlive);
                    int slot = actor.PotionSlots.ToList().IndexOf(potion);
                    if (slot < 0)
                        throw new InvalidOperationException("Injected enemy potion has no slot.");
                    SimulatedCombatState potionCombat = (SimulatedCombatState)potionSimulator.State.CombatState;
                    int historyStart = potionSimulator.History.Entries.Count;
                    if (!PotionExecutionSupport.Prepare(potionSimulator, potionCombat, potion, slot, target)
                        || !PotionExecutionSupport.Complete(potionSimulator, potionCombat, potion,
                            target, null, historyStart, new HashSet<uint>())
                        || !CombatBeamSolver.SettleReplayActionBoundary(potionSimulator, potionCombat))
                        throw new InvalidOperationException("Enemy potion prediction did not complete.");
                    ContinuationStamp predictedPotion = ContinuationStamp.CapturePredicted(
                        actor, potionSimulator, 1, potionRoot.Forecast, 1);
                    runner.SetStage("multiplayer_enemy_potion");
                    potion.EnqueueManualUse(target);
                    await runner.WaitForMultiplayerProbeAsync(() => actor.GetPotionAtSlotIndex(slot) == null);
                    await runner.MultiplayerProbeBarrierAsync("enemy-potion", combat);
                    ContinuationStamp actualPotion = ContinuationStamp.CaptureLive(combat);
                    if (predictedPotion != actualPotion)
                        throw new InvalidOperationException(
                            "Multiplayer enemy potion differs: " + predictedPotion.DescribeFirstDifference(actualPotion));
                    runner._completedChecks.Add("MultiplayerEnemyPotion:NativeUse:FullState:FullRng");
                }
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifySearch)
            {
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchPolicySnapshot configured = SolverController.CaptureSearchPolicy(
                    settings with
                    {
                        MultiplayerTurnDepth = 2,
                        MultiplayerTimeLimitMilliseconds = 3_000,
                    }, combat, includeTurnSetup: false, theftPolicy: null);
                if (configured.MaxTurnLayers != 2
                    || configured.Profile.SoftTimeBudgetMilliseconds != 3_000)
                    throw new InvalidOperationException("Multiplayer content policy did not apply depth/time settings.");
                CombatRootSnapshot searchRoot = CombatRootSnapshot.Capture(combat);
                SearchPolicySnapshot searchPolicy = SolverController.CaptureSearchPolicy(
                    SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null) with
                {
                    MaxTurnLayers = input.ContentSearchOnly
                        ? input.ContentSearchTurnDepth : configured.MaxTurnLayers,
                    MultiplayerAllyTargetSeatForTesting = input.VerifyTargetedSupport || input.VerifyStarSupport
                        ? input.ContentTargetSeat
                        : input.VerifyMultipleSupport ? input.ContentTargetSeat : null,
                    Profile = SolverSearchProfile.Default with
                    {
                        MaxExpandedNodes = 10_000,
                        SoftTimeBudgetMilliseconds = 3_000,
                    },
                    BudgetOverrideMilliseconds = 3_000,
                    MaxDegreeOfParallelism = 1,
                    UseBeamWidthPortfolio = false,
                    EarlyTurnExplorationBudgetMilliseconds = 0,
                };
                if (input.VerifyStarSupport)
                {
                    CombatRootSnapshot noStarsRoot = searchRoot;
                    SolverDisplayNames noStarsNames = SolverDisplayNames.Capture(combat);
                    BattleDamageSnapshot noStarsDamage = BattleDamageTracker.Observe(combat);
                    SolverResult withoutStars = await Task.Run(() => CombatSearchCoordinator.Solve(
                        noStarsRoot, noStarsNames, noStarsDamage,
                        searchPolicy, CancellationToken.None, null));
                    if (withoutStars.MultiplayerSupportAdded
                        || new[] { withoutStars }.Concat(withoutStars.MultiplayerAlternatives)
                            .SelectMany(plan => plan.BestNode.Actions)
                            .Any(action => action.CardId == "CONSTELLATION"))
                        throw new InvalidOperationException("Constellation was planned without its two stars.");
                    runner._completedChecks.Add("MultiplayerSupport:Constellation:ZeroStarsRejected");
                    UnattendedTestRunner.SetStars(actor, 2);
                    searchRoot = CombatRootSnapshot.Capture(combat);
                }
                SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                SolverResult search = await Task.Run(() => input.ContentSearchOnly
                    ? CombatSearchCoordinator.Solve(searchRoot, displayNames,
                        damage, searchPolicy, CancellationToken.None, null)
                    : new CombatBeamSolver(searchRoot,
                        displayNames, damage,
                        searchPolicy, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                if (search.StartTurnNumber != 1 || search.BestNode.Actions.Count == 0
                    || search.SearchedTurns > searchPolicy.MaxTurnLayers
                    || search.BestNode.Actions.Any(action => action.Kind == PlanActionKind.PlayCard
                        && action.Turn == 1 && !actor.PlayerCombatState!.AllCards.Any(card =>
                            card.Id.Entry == action.CardId)))
                    throw new InvalidOperationException("Multiplayer content search produced no legal local route.");
                SolverResult[] contentPlans = [search, .. search.MultiplayerAlternatives];
                if (contentPlans[0].MultiplayerStyle != MultiplayerPlanStyle.Output
                    || contentPlans.Length > 3
                    || contentPlans.Select(plan => plan.MultiplayerStyle).Distinct().Count() != contentPlans.Length
                    || contentPlans.Any(plan => plan.Snapshot.PlayerDead
                        || plan.Snapshot.ProjectedPlayerHp <= 0))
                    throw new InvalidOperationException("Multiplayer content search styles are invalid.");
                if (input.ContentSearchOnly
                    && (search.TotalExpandedNodes != search.ExpandedNodes
                        || !search.SingleSessionSearch
                        || search.ComparisonRootState != searchRoot.ContinuationStamp.StateText))
                    throw new InvalidOperationException("Multiplayer production search did not use one shared session.");
                if (input.ContentSearchOnly && !input.VerifyHybridPlanStyles
                    && input.ContentCardIds.Contains("INFLAME"))
                {
                    SolverResult? setup = contentPlans.FirstOrDefault(plan =>
                        plan.MultiplayerStyle == MultiplayerPlanStyle.Setup);
                    if (setup == null || setup.MultiplayerSetupValue <= search.MultiplayerSetupValue
                        || setup.BestNode.Actions.Where(action => action.Turn == 1)
                            .SequenceEqual(search.BestNode.Actions.Where(action => action.Turn == 1)))
                        throw new InvalidOperationException("Setup search did not preserve a real opening tradeoff: "
                            + string.Join("; ", contentPlans.Select(plan =>
                                $"{plan.MultiplayerStyle}:setup={plan.MultiplayerSetupValue}:damage={plan.MultiplayerEffectiveDamage}:"
                                + string.Join(',', plan.BestNode.Actions.Select(action =>
                                    $"{action.Turn}/{action.Kind}/{action.CardId}")))));
                    runner._completedChecks.Add("MultiplayerPlans:OutputAndSetup:DistinctActions:SetupEstimateGain");
                }
                if (input.ContentSearchOnly && !input.VerifyHybridPlanStyles
                    && input.ContentCardIds.Contains("DEFEND_IRONCLAD"))
                {
                    SolverResult? defense = contentPlans.FirstOrDefault(plan =>
                        plan.MultiplayerStyle == MultiplayerPlanStyle.Defense);
                    if (defense == null
                        || defense.MultiplayerCurrentTurnProjectedHp
                            <= search.MultiplayerCurrentTurnProjectedHp
                        || defense.BestNode.Actions.Where(action => action.Turn == 1)
                            .SequenceEqual(search.BestNode.Actions.Where(action => action.Turn == 1)))
                        throw new InvalidOperationException("Defense search did not preserve a real HP tradeoff.");
                    runner._completedChecks.Add("MultiplayerPlans:OutputAndDefense:DistinctActions:ProjectedHpGain");
                }
                if (input.VerifyHybridPlanStyles)
                {
                    MultiplayerPlanStyle expectedStyle;
                    string[] expectedCards;
                    if (input.ContentCardIds.SequenceEqual(
                            ["DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "UPPERCUT"])
                        && input.ContentActorEnergy == 2)
                    {
                        expectedStyle = MultiplayerPlanStyle.Defense;
                        expectedCards = ["DEFEND_IRONCLAD", "STRIKE_IRONCLAD"];
                    }
                    else if (input.ContentCardIds.SequenceEqual(
                            ["INFLAME", "STRIKE_IRONCLAD", "UPPERCUT"])
                        && input.ContentActorEnergy == 2)
                    {
                        expectedStyle = MultiplayerPlanStyle.Setup;
                        expectedCards = ["INFLAME", "STRIKE_IRONCLAD"];
                    }
                    else if (input.ContentCardIds.SequenceEqual(
                            ["DEFEND_IRONCLAD", "UPPERCUT"])
                        && input.ContentActorEnergy == 3)
                    {
                        expectedStyle = MultiplayerPlanStyle.Output;
                        expectedCards = ["UPPERCUT", "DEFEND_IRONCLAD"];
                    }
                    else
                        throw new InvalidOperationException("Unknown hybrid style search fixture.");
                    SolverResult chosen = contentPlans.Single(plan =>
                        plan.MultiplayerStyle == expectedStyle);
                    string[] played = chosen.BestNode.Actions
                        .Where(action => action.Turn == 1
                            && action.Kind == PlanActionKind.PlayCard)
                        .Select(action => action.CardId!).ToArray();
                    if (expectedCards.Except(played).Any()
                        || chosen.EnergyLeftByTurn[1] != 0)
                        throw new InvalidOperationException(
                            $"{expectedStyle} route omitted a useful affordable action: "
                            + string.Join(',', played));
                    runner._completedChecks.Add(
                        $"MultiplayerPlans:{expectedStyle}:HybridActions:{string.Join(',', expectedCards)}");
                }
                if (input.VerifyPureSupport)
                {
                    PlanAction[] current = search.BestNode.Actions
                        .Where(action => action.Turn == 1).ToArray();
                    int attack = Array.FindIndex(current, action => action.CardId == "STRIKE_IRONCLAD");
                    int support = Array.FindIndex(current, action => action.CardId == "BEACON_OF_HOPE");
                    if (attack < 0 || support <= attack || current[support].Kind != PlanActionKind.PlayCard
                        || !search.MultiplayerSupportAdded)
                        throw new InvalidOperationException("Pure support was not appended after the local attack.");
                    runner._completedChecks.Add("MultiplayerSupport:AfterMainAction:SpareEnergy:StableReplay");
                }
                if (input.VerifyTargetedSupport)
                {
                    PlanAction[] current = search.BestNode.Actions
                        .Where(action => action.Turn == 1).ToArray();
                    string supportCardId = input.ContentCardIds.Single(id => id is "BLAZE" or "LARGESSE");
                    int attack = Array.FindIndex(current, action => action.CardId == "STRIKE_IRONCLAD");
                    int support = Array.FindIndex(current, action => action.CardId == supportCardId);
                    if (attack < 0 || support <= attack || !search.MultiplayerSupportAdded
                        || current[support].TargetCombatId
                            != combat.Players[input.ContentTargetSeat].Creature.CombatId)
                        throw new InvalidOperationException("Targeted support did not keep the teammate target.");
                    runner._completedChecks.Add("MultiplayerSupport:FixedTeammateTarget:AfterMainAction");
                }
                if (input.VerifyStarSupport)
                {
                    PlanAction[] current = search.BestNode.Actions
                        .Where(action => action.Turn == 1).ToArray();
                    int attack = Array.FindIndex(current, action => action.CardId == "STRIKE_IRONCLAD");
                    int support = Array.FindIndex(current, action => action.CardId == "CONSTELLATION");
                    if (attack < 0 || support <= attack || !search.MultiplayerSupportAdded
                        || current[support].TargetCombatId
                            != combat.Players[input.ContentTargetSeat].Creature.CombatId)
                        throw new InvalidOperationException("Constellation did not use the two spare stars on a teammate.");
                    runner._completedChecks.Add("MultiplayerSupport:Constellation:TwoStars:AfterMainAction:TeammateTarget");
                }
                if (input.VerifyMultipleSupport)
                {
                    PlanAction[] current = search.BestNode.Actions
                        .Where(action => action.Turn == 1).ToArray();
                    int attack = Array.FindIndex(current, action => action.CardId == "STRIKE_IRONCLAD");
                    int beacon = Array.FindIndex(current, action => action.CardId == "BEACON_OF_HOPE");
                    int blaze = Array.FindIndex(current, action => action.CardId == "BLAZE");
                    if (attack < 0 || beacon <= attack || blaze <= attack
                        || !search.MultiplayerSupportAdded
                        || current[blaze].TargetCombatId
                            != combat.Players[input.ContentTargetSeat].Creature.CombatId)
                        throw new InvalidOperationException("Multiple support cards did not use spare resources.");
                    runner._completedChecks.Add("MultiplayerSupport:TwoCards:SpareEnergy:FixedTarget");
                }
                if (input.VerifyPureSupport || input.VerifyTargetedSupport
                    || input.VerifyMultipleSupport || input.VerifyStarSupport)
                {
                    CombatBeamSolver replayDriver = new(searchRoot, SolverDisplayNames.Capture(combat),
                        BattleDamageTracker.Observe(combat), searchPolicy);
                    SimulationSnapshot replayed = UnattendedTestRunner.InvokeForcedTerminalReplay(
                        replayDriver, search.BestNode.Actions, null, 0, null);
                    ContinuationStamp predictedRoute;
                    try
                    {
                        predictedRoute = ContinuationStamp.CapturePredicted(
                            actor, replayed.Simulator, replayed.Turn, searchRoot.Forecast, 1);
                    }
                    finally
                    {
                        replayed.ReleaseSimulator();
                    }
                    foreach (PlanAction planned in search.BestNode.Actions)
                    {
                        if (planned.Turn != 1)
                            throw new InvalidOperationException("Support fixture route exceeded the current turn.");
                        if (planned.Kind == PlanActionKind.PlayCard)
                        {
                            CardModel liveCard = actor.PlayerCombatState!.Hand.Cards.Single(card =>
                                card.Id.Entry == planned.CardId);
                            Creature? liveTarget = planned.TargetCombatId is { } id
                                ? combat.Creatures.Single(creature => creature.CombatId == id)
                                : null;
                            var play = new PlayCardAction(liveCard, liveTarget);
                            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                            await play.CompletionTask;
                        }
                        else if (planned.Kind == PlanActionKind.EndTurn)
                        {
                            var end = new EndPlayerTurnAction(actor, 1);
                            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                            await end.CompletionTask;
                        }
                        else
                            throw new InvalidOperationException("Support fixture has an unexpected action kind.");
                    }
                    await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                        player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                    await runner.MultiplayerProbeBarrierAsync("support-route", combat);
                    ContinuationStamp actualRoute = ContinuationStamp.CaptureLive(combat);
                    if (predictedRoute != actualRoute)
                        throw new InvalidOperationException("Support route differs: "
                            + predictedRoute.DescribeFirstDifference(actualRoute));
                    runner._completedChecks.Add("MultiplayerSupport:NativeRoute:AllPlayers:FullRng");
                }
                if (input.VerifyNoPureSupport)
                {
                    if (search.MultiplayerSupportAdded || search.BestNode.Actions.Any(action =>
                            action.CardId == "BEACON_OF_HOPE"))
                        throw new InvalidOperationException("Support consumed reserved main-plan energy.");
                    runner._completedChecks.Add("MultiplayerSupport:NoSpareEnergy:NotAdded");
                }
                if (input.VerifyGroupBenefitSearch)
                {
                    SolverResult? defense = contentPlans.FirstOrDefault(plan =>
                        plan.MultiplayerStyle == MultiplayerPlanStyle.Defense);
                    if (defense == null || defense.MultiplayerSupportAdded
                        || !defense.BestNode.Actions.Any(action => action.CardId == "RALLY"))
                        throw new InvalidOperationException("Self-benefiting group card was not searched normally.");
                    runner._completedChecks.Add("MultiplayerGroupCard:Rally:NormalDefenseSearch");
                }
                if (input.VerifySelfBenefitAllySearch)
                {
                    if (!contentPlans.Any(plan => !plan.MultiplayerSupportAdded
                        && plan.BestNode.Actions.Any(action =>
                            action.CardId == "MIMIC"
                            && action.TargetCombatId == combat.Players[input.ContentTargetSeat].Creature.CombatId)))
                        throw new InvalidOperationException("Mimic's owner benefit did not enter normal search: "
                            + string.Join("; ", contentPlans.Select(plan =>
                                $"{plan.MultiplayerStyle}:support={plan.MultiplayerSupportAdded}:hp={plan.MultiplayerCurrentTurnProjectedHp}:"
                                + string.Join(',', plan.BestNode.Actions.Select(action =>
                                    $"{action.Kind}/{action.CardId}/{action.TargetCombatId}")))));
                    runner._completedChecks.Add("MultiplayerSelfBenefitAlly:Mimic:NormalSearch:TeammateTarget");
                }
                runner._completedChecks.Add(
                    $"MultiplayerContentSearch:Players={input.PlayerCount}:LocalActions:Budget=3000ms:Styles={string.Join(',', contentPlans.Select(plan => plan.MultiplayerStyle))}");
            }
            if (input.ContentSearchOnly)
                return new ExecutionOutcome(false,
                    input.VerifyPureSupport || input.VerifyTargetedSupport
                        || input.VerifyMultipleSupport || input.VerifyStarSupport ? 2 : 1,
                    true, true, true, false);
            if (input.ContentTeammateStrikeBefore)
                await PlayTeammateStrikeAsync("before");
            for (int index = 0; index < input.ContentCardIds.Length; index++)
            {
                string cardId = input.ContentCardIds[index];
                CardModel card = actor.PlayerCombatState!.Hand.Cards.Single(candidate =>
                    candidate.Id.Entry == cardId);
                Creature? target = card.TargetType switch
                {
                    TargetType.AnyAlly => combat.Players[input.ContentTargetSeat].Creature,
                    TargetType.Self or TargetType.AllAllies => null,
                    TargetType.AnyEnemy => combat.Enemies.First(),
                    TargetType.RandomEnemy => null,
                    _ => throw new InvalidOperationException(
                        $"Content probe has no target rule for {card.Id.Entry}: {card.TargetType}."),
                };
                if (!card.CanPlayTargeting(target))
                    throw new InvalidOperationException($"Content probe card is not playable: {card.Id.Entry}.");
                Player? largesseRecipient = card is Largesse
                    ? target?.Player ?? throw new InvalidOperationException("Largesse requires a player target.")
                    : null;
                int targetHandBefore = largesseRecipient?.PlayerCombatState!.Hand.Cards.Count ?? 0;
                int actorHandBefore = card is Largesse
                    ? actor.PlayerCombatState!.Hand.Cards.Count : 0;
                int enemyHpBefore = combat.Enemies.First().CurrentHp;
                int totalEnemyHpBefore = input.VerifySerpentFormRandomTarget
                    ? combat.Enemies.Sum(creature => creature.CurrentHp) : 0;
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                PredictedCard predictedCard = simulator.State.GetPlayerCombatState(actor).Hand.Cards.Single(candidate =>
                    candidate.Preview.Id.Entry == card.Id.Entry);
                SimulatedCombatState predictedCombat = (SimulatedCombatState)simulator.State.CombatState;
                PlanCardChoice[]? plannedChoices = null;
                string? selectedGeneratedCardId = null;
                if (card is Tutor)
                {
                    Player targetPlayer = target?.Player
                        ?? throw new InvalidOperationException("Tutor fixture has no target player.");
                    var targetState = simulator.State.GetPlayerCombatState(targetPlayer);
                    var spec = new CardChoiceSpec(PlanChoiceEffect.MoveToHand, PileType.Draw,
                        1, 1, targetState.DrawPile.Cards, targetState.DrawPile.Cards, 0d);
                    plannedChoices = [CardChoiceSupport.BuildRequestedChoice(spec, ["DEFEND_IRONCLAD"])];
                }
                else if (card is Abundance or Discovery or Quasar or Splash)
                {
                    CombatPredictionSimulator optionSimulator = root.ForkSimulator();
                    PredictedCard optionCard = optionSimulator.State.GetPlayerCombatState(actor).Hand.Cards
                        .Single(candidate => candidate.Preview.Id.Entry == card.Id.Entry);
                    SimulatedCombatState optionCombat = (SimulatedCombatState)optionSimulator.State.CombatState;
                    optionCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                    try
                    {
                        optionSimulator.ManualPlay(optionCard, target, out _);
                        if (!optionSimulator.HasPendingChoice)
                            throw new InvalidOperationException($"{card.Id.Entry} did not request its generated-card choice.");
                        CardChoiceSpec spec = CardChoiceSupport.GetSpec(optionSimulator, optionCard)
                            ?? throw new InvalidOperationException($"{card.Id.Entry} has no generated-card choice spec.");
                        selectedGeneratedCardId = spec.Options.First().Preview.Id.Entry;
                        plannedChoices = [CardChoiceSupport.BuildRequestedChoice(spec, [selectedGeneratedCardId])];
                    }
                    finally
                    {
                        optionCombat.EndActionChoices();
                    }
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
                string? nativeChoiceCardId = card is Tutor ? "DEFEND_IRONCLAD" : selectedGeneratedCardId;
                using var selector = nativeChoiceCardId != null
                    ? CardSelectCmd.UseSelector(new UnattendedCardSelector([nativeChoiceCardId]), localOnly: false)
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
                if (input.VerifySerpentFormRandomTarget)
                {
                    int damage = totalEnemyHpBefore - combat.Enemies.Sum(creature => creature.CurrentHp);
                    if (damage != (index == 0 ? 0 : 10)
                        || actor.Creature.GetPowerAmount<SerpentFormPower>() != 4)
                        throw new InvalidOperationException("Serpent Form did not trigger on the following card only.");
                    runner._completedChecks.Add($"MultiplayerContent:SerpentForm:Card={index + 1}:RandomTarget:FullState:FullRng");
                }
                if (largesseRecipient is { } recipient)
                {
                    if (recipient.PlayerCombatState!.Hand.Cards.Count != targetHandBefore + 1
                        || actor.PlayerCombatState!.Hand.Cards.Count != actorHandBefore - 1
                        || recipient.PlayerCombatState.Hand.Cards.Any(generated =>
                            generated.Owner != recipient))
                        throw new InvalidOperationException("Largesse did not add the generated card to the teammate's hand.");
                    runner._completedChecks.Add("MultiplayerLargesse:TeammateReceivesGeneratedCard:Owner:FullState:FullRng");
                }
                runner._completedChecks.Add(
                    $"MultiplayerContent:{card.Id.Entry}:Upgrade={card.CurrentUpgradeLevel}:Target={combat.Players[input.ContentTargetSeat].NetId}:FullState:FullRng");
                if (card is HuddleUp && input.ContentCacophonyCardsRemaining > 0)
                {
                    CacophonyPower triggered = actor.Creature.Powers.OfType<CacophonyPower>().Single();
                    if (triggered.DynamicVars.Cards.IntValue <= input.ContentCacophonyCardsRemaining
                        || combat.Enemies.Single().CurrentHp != enemyHpBefore - triggered.Amount)
                        throw new InvalidOperationException("Cacophony draw fixture did not trigger and reset once.");
                    runner._completedChecks.Add("MultiplayerCacophony:DrawThreshold:Damage:Reset:FullRng");
                }
                if (card is Cacophony && input.ContentCacophonyCardsRemaining > 0)
                {
                    CacophonyPower power = actor.Creature.Powers.OfType<CacophonyPower>().Single();
                    power.DynamicVars.Cards.BaseValue = input.ContentCacophonyCardsRemaining;
                    runner._completedChecks.Add($"MultiplayerCacophonyFixture:Cards={input.ContentCacophonyCardsRemaining}");
                }
            }
            if (input.ContentTeammateStrikeAfter)
                await PlayTeammateStrikeAsync("after");
            if (input.ContentTeammateCardIdAfter.Length > 0)
                await PlayTeammateCardAfterAsync(input.ContentTeammateCardIdAfter);
            if (input.VerifyImitationTwice)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                ImitationLearningPower copy = actor.Creature.Powers.OfType<ImitationLearningPower>().Single();
                if (copy.PlayerTarget != teammate || copy.Amount != 2)
                    throw new InvalidOperationException("Imitation Learning did not watch the selected teammate twice.");
                await PlayTeammateCardAfterAsync("INFLAME");
                if (actor.Creature.GetPowerAmount<StrengthPower>() != 2
                    || teammate.Creature.GetPowerAmount<StrengthPower>() != 2
                    || copy.Amount != 1)
                    throw new InvalidOperationException("Imitation Learning did not copy the first teammate power.");
                await PlayTeammateCardAfterAsync("STONE_ARMOR");
                if (actor.Creature.GetPowerAmount<PlatingPower>() != 4
                    || teammate.Creature.GetPowerAmount<PlatingPower>() != 4
                    || actor.Creature.HasPower<ImitationLearningPower>())
                    throw new InvalidOperationException("Imitation Learning did not exhaust after the second teammate power.");
                runner._completedChecks.Add("MultiplayerImitation:TwoTeammatePowers:OwnerCopies:Consumed:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.VerifySoulboundStack)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                SoulboundPower linked = teammate.Creature.Powers.OfType<SoulboundPower>().Single();
                if (linked.Amount != 1 || linked.Applier != actor.Creature)
                    throw new InvalidOperationException("Soulbound was not applied by the local player to the teammate.");
                await PowerCmd.Apply<SoulboundPower>(new ThrowingPlayerChoiceContext(),
                    teammate.Creature, 1, actor.Creature, null);
                if (linked.Amount != 2)
                    throw new InvalidOperationException("Soulbound did not stack on the teammate.");
                await runner.MultiplayerProbeBarrierAsync("soulbound-stacked", combat);
                int actorSoulsBefore = actor.PlayerCombatState!.DrawPile.Cards.Count(card => card is Soul);
                int teammateSoulsBefore = teammate.PlayerCombatState!.DrawPile.Cards.Count(card => card is Soul);
                CombatRootSnapshot soulRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator soulSimulator = soulRoot.ForkSimulator();
                soulSimulator.CreateAndAddGeneratedCardsToCombat<Soul>(
                    actor, PileType.Draw, 1, actor, CardPilePosition.Random);
                if (!CombatBeamSolver.SettleReplayActionBoundary(
                    soulSimulator, (SimulatedCombatState)soulSimulator.State.CombatState))
                    throw new InvalidOperationException("Soulbound generation prediction did not settle.");
                ContinuationStamp predictedSouls = ContinuationStamp.CapturePredicted(
                    actor, soulSimulator, 1, soulRoot.Forecast, 1);
                await CardPileCmd.AddGeneratedCardsToCombat(
                    Soul.Create(actor, 1, combat), PileType.Draw, actor, CardPilePosition.Random);
                await runner.MultiplayerProbeBarrierAsync("soulbound-two-stack-generation", combat);
                ContinuationStamp actualSouls = ContinuationStamp.CaptureLive(combat);
                if (predictedSouls != actualSouls
                    || actor.PlayerCombatState.DrawPile.Cards.Count(card => card is Soul) != actorSoulsBefore + 1
                    || teammate.PlayerCombatState.DrawPile.Cards.Count(card => card is Soul) != teammateSoulsBefore + 2
                    || teammate.PlayerCombatState.DrawPile.Cards.Any(card => card is Soul && card.Owner != teammate)
                    || linked.Amount != 2)
                    throw new InvalidOperationException("Soulbound two-stack generation differs: "
                        + predictedSouls.DescribeFirstDifference(actualSouls));
                runner._completedChecks.Add("MultiplayerSoulbound:TwoStacks:TeammateOwnsTwoSouls:FullState:FullRng");
                await CreatureCmd.SetCurrentHp(teammate.Creature, 0);
                await runner.MultiplayerProbeBarrierAsync("soulbound-dead-target-root", combat);
                int deadTargetSoulsBefore = teammate.PlayerCombatState.DrawPile.Cards.Count(card => card is Soul);
                CombatRootSnapshot deadRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator deadSimulator = deadRoot.ForkSimulator();
                deadSimulator.CreateAndAddGeneratedCardsToCombat<Soul>(
                    actor, PileType.Draw, 1, actor, CardPilePosition.Random);
                if (!CombatBeamSolver.SettleReplayActionBoundary(
                    deadSimulator, (SimulatedCombatState)deadSimulator.State.CombatState))
                    throw new InvalidOperationException("Dead Soulbound target prediction did not settle.");
                ContinuationStamp predictedDeadTarget = ContinuationStamp.CapturePredicted(
                    actor, deadSimulator, 1, deadRoot.Forecast, 1);
                await CardPileCmd.AddGeneratedCardsToCombat(
                    Soul.Create(actor, 1, combat), PileType.Draw, actor, CardPilePosition.Random);
                await runner.MultiplayerProbeBarrierAsync("soulbound-dead-target-generation", combat);
                ContinuationStamp actualDeadTarget = ContinuationStamp.CaptureLive(combat);
                if (predictedDeadTarget != actualDeadTarget
                    || teammate.PlayerCombatState.DrawPile.Cards.Count(card => card is Soul)
                        != deadTargetSoulsBefore)
                    throw new InvalidOperationException("Dead Soulbound target generation differs: "
                        + predictedDeadTarget.DescribeFirstDifference(actualDeadTarget));
                runner._completedChecks.Add("MultiplayerSoulbound:HpZeroTarget:NoInsertedSouls:FullState:FullRng");
                return new ExecutionOutcome(false, 1, true, true, true, false);
            }
            if (input.ContentReplayTransferredBall)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                if (!teammate.PlayerCombatState!.AllCards.Any(card => card is TheBall))
                    throw new InvalidOperationException("Transferred Ball is missing from the teammate's cards.");
                CombatRootSnapshot drawRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator drawSimulator = drawRoot.ForkSimulator();
                drawSimulator.Draw(teammate, 5);
                if (!CombatBeamSolver.SettleReplayActionBoundary(
                    drawSimulator, (SimulatedCombatState)drawSimulator.State.CombatState))
                    throw new InvalidOperationException("Transferred Ball prediction draw did not complete.");
                ContinuationStamp predictedDraw = ContinuationStamp.CapturePredicted(
                    actor, drawSimulator, 1, drawRoot.Forecast, 1);
                await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 5, teammate);
                await runner.MultiplayerProbeBarrierAsync("ball-draw", combat);
                ContinuationStamp actualDraw = ContinuationStamp.CaptureLive(combat);
                if (predictedDraw != actualDraw)
                    throw new InvalidOperationException(
                        "Transferred Ball draw differs: " + predictedDraw.DescribeFirstDifference(actualDraw));
                if (!teammate.PlayerCombatState.Hand.Cards.Any(card => card is TheBall))
                    throw new InvalidOperationException("Transferred Ball was not drawn into the teammate's hand.");
                await PlayTeammateCardAfterAsync("THE_BALL");
                runner._completedChecks.Add("MultiplayerBall:Transfer:Draw:Replay:FullState:FullRng");
            }
            if (input.VerifyMidnightExhaustHistory)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                CardModel thirdExhaust = (await UnattendedTestRunner.InjectCardAsync(combat, teammate,
                    new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" })).Single();
                CombatRootSnapshot entryRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator entrySimulator = entryRoot.ForkSimulator();
                PredictedCard predictedThirdExhaust = entrySimulator.State.GetPlayerCombatState(teammate).Hand.Cards
                    .Single(card => ReferenceEquals(card.Original, thirdExhaust));
                entrySimulator.Exhaust(predictedThirdExhaust);
                entrySimulator.CreateAndAddGeneratedCardsToCombat<Midnight>(
                    actor, PileType.Hand, 1, actor);
                SimulatedCombatState entryCombat = (SimulatedCombatState)entrySimulator.State.CombatState;
                if (!CombatBeamSolver.SettleReplayActionBoundary(entrySimulator, entryCombat))
                    throw new InvalidOperationException("Generated Midnight prediction did not settle.");
                ContinuationStamp predictedEntry = ContinuationStamp.CapturePredicted(
                    actor, entrySimulator, 1, entryRoot.Forecast, 1);
                await CardCmd.Exhaust(new ThrowingPlayerChoiceContext(), thirdExhaust);
                CardModel generated = combat.CreateCard(ModelDb.Card<Midnight>(), actor);
                CardPileAddResult added = await CardPileCmd.AddGeneratedCardToCombat(
                    generated, PileType.Hand, actor);
                if (!added.success)
                    throw new InvalidOperationException("Generated Midnight did not enter combat.");
                await runner.MultiplayerProbeBarrierAsync("midnight-generated", combat);
                ContinuationStamp actualEntry = ContinuationStamp.CaptureLive(combat);
                if (predictedEntry != actualEntry)
                    throw new InvalidOperationException("Generated Midnight differs: "
                        + predictedEntry.DescribeFirstDifference(actualEntry));
                if (generated.Owner != actor || generated.EnergyCost.GetAmountToSpend() != 9)
                    throw new InvalidOperationException("Generated Midnight did not inherit combat exhaust history.");
                runner._completedChecks.Add("MultiplayerMidnight:GeneratedAfterThreeExhausts:Cost=9:FullState:FullRng");
            }
            if (input.VerifyInterceptApplierDeathHook)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                if (!teammate.Creature.HasPower<CoveredPower>()
                    || !actor.Creature.HasPower<InterceptPower>())
                    throw new InvalidOperationException("Intercept death fixture has no linked powers.");
                CombatRootSnapshot deathRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator deathSimulator = deathRoot.ForkSimulator();
                HookMirrors.AfterDeath(deathSimulator, actor.Creature, wasRemovalPrevented: false);
                ContinuationStamp predictedDeathHook = ContinuationStamp.CapturePredicted(
                    actor, deathSimulator, 1, deathRoot.Forecast, 1);
                await MegaCrit.Sts2.Core.Hooks.Hook.AfterDeath(
                    combat.RunState, combat, actor.Creature, wasRemovalPrevented: false, deathAnimLength: 0f);
                await runner.MultiplayerProbeBarrierAsync("intercept-applier-death-hook", combat);
                ContinuationStamp actualDeathHook = ContinuationStamp.CaptureLive(combat);
                if (predictedDeathHook != actualDeathHook
                    || teammate.Creature.HasPower<CoveredPower>())
                    throw new InvalidOperationException("Intercept applier death hook differs: "
                        + predictedDeathHook.DescribeFirstDifference(actualDeathHook));
                runner._completedChecks.Add("MultiplayerIntercept:ApplierDeathHook:CoveredRemoved:FullState:FullRng");
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

            async Task PlayTeammateStrikeAsync(string point)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                CardModel strike = teammate.PlayerCombatState!.Hand.Cards.First(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD");
                Creature enemy = combat.Enemies.Single();
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                PredictedCard predictedStrike = simulator.State.GetPlayerCombatState(teammate)
                    .Hand.Cards.First(card => card.Preview.Id.Entry == "STRIKE_IRONCLAD");
                if (!simulator.ManualPlay(predictedStrike, enemy, out _)
                    || !CombatBeamSolver.SettleReplayActionBoundary(
                        simulator, (SimulatedCombatState)simulator.State.CombatState))
                    throw new InvalidOperationException("Teammate strike prediction did not complete.");
                ContinuationStamp predicted = ContinuationStamp.CapturePredicted(
                    actor, simulator, 1, root.Forecast, 1);
                runner.SetStage($"multiplayer_teammate_strike_{point}");
                var action = new PlayCardAction(strike, enemy);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                await action.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync($"teammate-strike-{point}", combat);
                ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
                if (predicted != actual)
                    throw new InvalidOperationException(
                        $"Teammate strike {point} differs: " + predicted.DescribeFirstDifference(actual));
                runner._completedChecks.Add($"MultiplayerTeammateStrike:{point}:FullState:FullRng");
            }

            async Task PlayTeammateCardAfterAsync(string cardId)
            {
                Player teammate = combat.Players[input.ContentTargetSeat];
                CardModel card = teammate.PlayerCombatState!.Hand.Cards.Single(value => value.Id.Entry == cardId);
                Creature? target = card.TargetType switch
                {
                    TargetType.Self or TargetType.AllAllies => null,
                    TargetType.AnyEnemy => combat.Enemies.Single(),
                    _ => throw new InvalidOperationException($"Teammate card target is unsupported: {card.TargetType}."),
                };
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator simulator = root.ForkSimulator();
                PredictedCard predictedCard = simulator.State.GetPlayerCombatState(teammate)
                    .Hand.Cards.Single(value => value.Preview.Id.Entry == cardId);
                if (!simulator.ManualPlay(predictedCard, target, out _)
                    || !CombatBeamSolver.SettleReplayActionBoundary(
                        simulator, (SimulatedCombatState)simulator.State.CombatState))
                    throw new InvalidOperationException($"Teammate {cardId} prediction did not complete.");
                ContinuationStamp predicted = ContinuationStamp.CapturePredicted(
                    actor, simulator, 1, root.Forecast, 1);
                runner.SetStage($"multiplayer_teammate_card_{cardId}");
                var action = new PlayCardAction(card, target);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                await action.CompletionTask;
                await runner.MultiplayerProbeBarrierAsync($"teammate-card-{cardId}", combat);
                ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
                if (predicted != actual)
                    throw new InvalidOperationException(
                        $"Teammate {cardId} differs: " + predicted.DescribeFirstDifference(actual));
                runner._completedChecks.Add($"MultiplayerTeammateCard:{cardId}:FullState:FullRng");
            }
        }

        private static ContinuationStamp PredictMultiplayerRound(CombatState combat, Player localPlayer)
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat), policy);
            SimulationSnapshot predicted = UnattendedTestRunner.InvokeForcedTerminalReplay(
                driver, [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber)], null, 0, null);
            try
            {
                return ContinuationStamp.CapturePredicted(
                    localPlayer, predicted.Simulator, predicted.Turn, root.Forecast, root.StartTurnNumber);
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
