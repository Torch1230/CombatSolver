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
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
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
            if (input.VerifyEnetControllerRng)
                return await ExecuteEnetControllerRngProbeAsync(scenario, input);
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
            if (input.VerifyRatSummonAfterRound)
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
                ContinuationStamp secondPrediction = PredictMultiplayerRound(combat, scenario.Player);
                var secondEnd = new EndPlayerTurnAction(scenario.Player, 2);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(secondEnd);
                await secondEnd.CompletionTask;
                await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(member =>
                    member.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 3 }));
                await runner.MultiplayerProbeBarrierAsync("rat-summon-ready", combat);
                CheckPrediction(secondPrediction, scenario.Player, "RAT_SUMMON_READY");
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

        private async Task<ExecutionOutcome> ExecuteEnetControllerRngProbeAsync(
            ScenarioContext scenario, MultiplayerProbeInput input)
        {
            CombatState combat = scenario.CombatState;
            Creature enemy = combat.Enemies.First();
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
                if (attacks.Length != 2 || attacks.Any(action => action.TargetCombatId != enemy.CombatId))
                    throw new InvalidOperationException("ENet RNG fixture needs two local attacks on the same enemy.");
                int startingHp = enemy.CurrentHp;
                SolverController.RequestDeploy(runner._host, combat);
                await runner.WaitForMultiplayerProbeAsync(() =>
                    enemy.CurrentHp == startingHp - 6 && SolverController.IsDeploying);
                File.WriteAllText(hostSignal, "first attack complete");
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(joinSignal)
                    && hostPlayer.PlayerCombatState!.AllCards.Any(card => !localCardsBefore.Contains(card)));
                await runner.WaitForMultiplayerProbeAsync(() => !SolverController.IsDeploying);
                CardModel generated = hostPlayer.PlayerCombatState!.AllCards.Single(card =>
                    !localCardsBefore.Contains(card));
                if (enemy.CurrentHp != startingHp - 12
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
                if (potion is AttackPotion)
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
                if (input.SelfPotionId == "ESSENCE_OF_DARKNESS"
                    && (actor.PlayerCombatState!.OrbQueue.Orbs.Count != 2
                        || combat.Players.Any(member => member != actor
                            && member.PlayerCombatState!.OrbQueue.Orbs.Count != 0)))
                    throw new InvalidOperationException("Dark orb potion did not channel only to its holder.");
                if (input.VerifyPotionAccounting)
                {
                    BattleDamageSnapshot observed = BattleDamageTracker.Observe(combat);
                    if (observed.PotionsUsedSoFar != 1
                        || !observed.PotionIdsUsedSoFar.SequenceEqual([input.SelfPotionId]))
                        throw new InvalidOperationException("Local potion accounting includes another player or misses self use.");
                    runner._completedChecks.Add("MultiplayerPotionAccounting:TeammateIgnored:LocalUseCounted");
                }
                runner._completedChecks.Add("MultiplayerSelfPotion:OwnerOnly:FullState:FullRng");
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
                if (input.ContentSearchOnly
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
                if (input.ContentSearchOnly
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
