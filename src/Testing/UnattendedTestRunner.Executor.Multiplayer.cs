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
using MegaCrit.Sts2.Core.Models.Potions;
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
                SolverResult result = await Task.Run(() => new CombatBeamSolver(
                    root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
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
            if (input.VerifyAllyTarget)
            {
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
                    uint[] current = targetDriver.AllyTargetsForTesting(source, fork);
                    if (current.Length != 1
                        || !combat.Players.Any(member => member.Creature.CombatId == current[0]
                            && member.Creature.IsAlive
                            && source.Original.CanPlayTargeting(member.Creature))
                        || iteration > 0 && !targets.SequenceEqual(current))
                        throw new InvalidOperationException("Ally target was not legal and stable across forks.");
                    targets = current;
                }
                if (ContinuationStamp.CaptureLive(combat).StateText != firstRng)
                    throw new InvalidOperationException("Ally target selection changed game state or RNG.");
                runner._completedChecks.Add("MultiplayerAllyTarget:OneLegalTarget:StableForks:GameRngUnchanged");
            }
            if (input.VerifySelfPotion)
            {
                PotionModel potion = UnattendedTestRunner.InjectPotionForTest(actor, "STRENGTH_POTION");
                int slot = actor.PotionSlots.ToList().IndexOf(potion);
                if (slot < 0)
                    throw new InvalidOperationException("Injected potion has no slot.");
                CombatRootSnapshot potionRoot = CombatRootSnapshot.Capture(combat);
                CombatPredictionSimulator potionSimulator = potionRoot.ForkSimulator();
                SimulatedCombatState potionCombat = (SimulatedCombatState)potionSimulator.State.CombatState;
                int historyStart = potionSimulator.History.Entries.Count;
                if (!PotionExecutionSupport.Prepare(potionSimulator, potionCombat, potion, slot, null)
                    || !PotionExecutionSupport.Complete(potionSimulator, potionCombat, potion,
                        null, null, historyStart, new HashSet<uint>())
                    || !CombatBeamSolver.SettleReplayActionBoundary(potionSimulator, potionCombat))
                    throw new InvalidOperationException("Self potion prediction did not complete.");
                ContinuationStamp predictedPotion = ContinuationStamp.CapturePredicted(
                    actor, potionSimulator, 1, potionRoot.Forecast, 1);
                runner.SetStage("multiplayer_self_potion");
                potion.EnqueueManualUse(null);
                await runner.WaitForMultiplayerProbeAsync(() => actor.GetPotionAtSlotIndex(slot) == null);
                await runner.MultiplayerProbeBarrierAsync("self-potion", combat);
                ContinuationStamp actualPotion = ContinuationStamp.CaptureLive(combat);
                if (predictedPotion != actualPotion)
                    throw new InvalidOperationException(
                        "Multiplayer self potion differs: " + predictedPotion.DescribeFirstDifference(actualPotion));
                runner._completedChecks.Add("MultiplayerSelfPotion:OwnerOnly:FullState:FullRng");
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
                    MultiplayerAllyTargetSeatForTesting = input.VerifyTargetedSupport
                        ? input.ContentTargetSeat
                        : input.VerifyMultipleSupport ? input.ContentTargetSeat
                        : input.VerifySelfTargetNormal ? 0 : null,
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
                SolverResult search = await Task.Run(() => input.ContentSearchOnly
                    ? CombatSearchCoordinator.Solve(searchRoot, SolverDisplayNames.Capture(combat),
                        BattleDamageTracker.Observe(combat), searchPolicy, CancellationToken.None, null)
                    : new CombatBeamSolver(searchRoot,
                        SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
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
                    int attack = Array.FindIndex(current, action => action.CardId == "STRIKE_IRONCLAD");
                    int support = Array.FindIndex(current, action => action.CardId == "BLAZE");
                    if (attack < 0 || support <= attack || !search.MultiplayerSupportAdded
                        || current[support].TargetCombatId
                            != combat.Players[input.ContentTargetSeat].Creature.CombatId)
                        throw new InvalidOperationException("Targeted support did not keep the teammate target.");
                    runner._completedChecks.Add("MultiplayerSupport:FixedTeammateTarget:AfterMainAction");
                }
                if (input.VerifySelfTargetNormal)
                {
                    SolverResult? setup = contentPlans.FirstOrDefault(plan =>
                        plan.MultiplayerStyle == MultiplayerPlanStyle.Setup);
                    if (setup == null || setup.MultiplayerSupportAdded
                        || !setup.BestNode.Actions.Any(action => action.CardId == "BLAZE"
                            && action.TargetCombatId == actor.Creature.CombatId))
                        throw new InvalidOperationException("Self-target Blaze was not searched normally.");
                    runner._completedChecks.Add("MultiplayerSupport:SelfTargetBlaze:NormalSetupSearch");
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
                if (input.VerifyPureSupport || input.VerifyTargetedSupport || input.VerifyMultipleSupport)
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
                    input.VerifyPureSupport || input.VerifyTargetedSupport || input.VerifyMultipleSupport ? 2 : 1,
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
                    TargetType.AnyEnemy => combat.Enemies.Single(),
                    _ => throw new InvalidOperationException(
                        $"Content probe has no target rule for {card.Id.Entry}: {card.TargetType}."),
                };
                if (!card.CanPlayTargeting(target))
                    throw new InvalidOperationException($"Content probe card is not playable: {card.Id.Entry}.");
                int enemyHpBefore = combat.Enemies.Single().CurrentHp;
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
