using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRemainingHealingTaintedAsync(CombatState live, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        var nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card is DefendSilent);
        if (!root.CanCertifyRemainingHealing || nativeCard.Affliction is not Tainted
            || !live.Enemies.Single().Powers.Any(power => power is VitalSparkPower))
            throw new InvalidOperationException("Tainted certificate requires the native Infested Prism and its afflicted skill.");
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatPredictionSimulator prediction = parent.Fork();
        var combat = (SimulatedCombatState)prediction.State.CombatState;
        using (SimulationNotificationIsolation.Enter())
        {
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0)
                throw new InvalidOperationException("Vital Spark and native Tainted skills must have zero healing potential.");
            using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                prediction.ManualPlay(prediction.State.FindCard(nativeCard)!, null, out _);
            CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
            if (combat.GetAmount<TaintedPower>(player.Creature) <= 0
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction.Fork(), player, 0) != 0)
                throw new InvalidOperationException("Playing a Tainted skill must retain the zero-healing proof across Fork.");

            CombatPredictionSimulator unknownAttachment = prediction.Fork();
            PredictedCard unknownCard = PredictedCard.Create(ModelDb.Card<StrikeSilent>(), player);
            unknownAttachment.AddGeneratedCardToCombat(unknownCard, PileType.Hand, player,
                resultKind: CardGenerationResultKind.Fixed);
            if (unknownAttachment.Afflict<Hexed>(unknownCard, 1) is null
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(unknownAttachment, player, 0) != int.MaxValue
                || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(unknownAttachment, player))
                throw new InvalidOperationException("An unaudited affliction must still disable the healing certificate.");
            CombatPredictionSimulator unknownPower = prediction.Fork();
            ((SimulatedCombatState)unknownPower.State.CombatState).SetAmount<EntropyPower>(player.Creature, 1);
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(unknownPower, player, 0) != int.MaxValue)
                throw new InvalidOperationException("Tainted support must not certify random-transform powers.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Tainted inspection changed the parent or native combat.");
        }
        string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played
                && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
            () =>
            {
                if (!nativeCard.TryManualPlay(null))
                    throw new InvalidOperationException("Native Tainted skill could not play.");
            }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        if (ContinuationStamp.CaptureLive(live).StateText != expected)
            throw new InvalidOperationException("Tainted skill native play disagrees with the complete predicted state.");
        using (SimulationNotificationIsolation.Enter())
            if (!EndTurnPowerSupport.TriggerRegular(prediction, combat, CombatSide.Enemy, live.Enemies))
                throw new InvalidOperationException("Tainted expiry unexpectedly suspended.");
        await TriggerActualSideTurnEndAsync(live, CombatSide.Enemy, live.Enemies);
        AssertSnapshotEqual(CaptureSimulated(prediction, combat, player, live.Enemies.Single()),
            CaptureActual(live, player, live.Enemies.Single()), "RemainingHealing", "TaintedEnemyTurnEnd");
        if (combat.GetAmount<TaintedPower>(player.Creature) != 0
            || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0
            || DescribeContinuationContractState(parent, root, player) != parentBefore)
            throw new InvalidOperationException("Tainted expiry lost its certificate or mutated the parent.");
        _completedChecks.Add("RemainingHealing:Tainted:VitalSpark:NativeSkillPlay:FullNativeState:Fork:UnknownAffliction:UnknownPower:NativeEnemyTurnExpiry:ParentIsolation");
        await AssertRemainingHealingCoordinatorAsync(live, player);
    }

    private async Task AssertRemainingHealingCoordinatorAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 35);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 9);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_SILENT", Pile = "Hand", UpgradeLevels = 1 });
        await InjectCardAsync(live, player, new() { CardId = "SPEEDSTER", Pile = "Hand", UpgradeLevels = 1 });
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = true, BeamWidthPortfolioWidths = [12, 8],
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
        };
        policy = policy with
        {
            Profile = policy.Profile with
            {
                BeamWidth = 12, MaxExpandedNodes = 80, SoftTimeBudgetMilliseconds = 10_000,
                StopPortfolioAtHpTarget = false,
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root,
            SolverDisplayNames.Capture(live), damage, policy, deadline.Token, null));
        if (!root.CanCertifyRemainingHealing
            || !result.SearchWorkAttributions.Any(work => work.Mechanism == nameof(ContinuationPurpose.OpeningPowerRouteMember)
                && work.RecordedSolverCount > 0)
            || !result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead || result.Snapshot.HasRisk
            || result.ProjectedBattleHpLost != damage.HpLostSoFar || result.ExplicitPotionCount != 0
            || ContinuationStamp.CaptureLive(live).StateText != before)
            throw new InvalidOperationException("Certified native opening-power coordinator failed strict incremental/no-additional-loss/live-isolation checks.");
        _completedChecks.Add("RemainingHealing:NativeRoot:Dop2:StrictIncremental:OpeningPowerPortfolio:MissingHp:NoAdditionalLoss:LiveIsolation");
        await AssertRefinementIncumbentAsync(live, player);
    }

    private async Task AssertRemainingHealingSilentAsync(CombatState live, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing)
            throw new InvalidOperationException("Silent fixture must have a certified native root.");
        var parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var prediction = parent.Fork();
        var combat = (SimulatedCombatState)prediction.State.CombatState;
        var nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card is ThrummingHatchet);
        using (SimulationNotificationIsolation.Enter())
        {
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0)
                throw new InvalidOperationException("Stable Serum and Forge must not create future healing.");
            using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                prediction.ManualPlay(prediction.State.FindCard(nativeCard)!, live.Enemies.Single(), out _);
            CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
            if (!combat.HasPendingReturningCards
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction.Fork(), player, 0) != 0)
                throw new InvalidOperationException("A certified card's self-return must preserve the non-healing closure across Fork.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Silent certificate mutated parent or native combat.");
        }
        string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played
                && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
            () =>
            {
                if (!nativeCard.TryManualPlay(live.Enemies.Single()))
                    throw new InvalidOperationException("Native returning-card fixture could not play.");
            }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        if (ContinuationStamp.CaptureLive(live).StateText != expected)
            throw new InvalidOperationException("Silent self-return native play disagrees with full predicted state.");
        _completedChecks.Add("RemainingHealing:Silent:NonHealingPotions:ScheduledSelfReturn:Fork:FullNativeState:ParentIsolation");
    }

    private async Task AssertRemainingHealingBoundAsync(CombatState live, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing)
            throw new InvalidOperationException("Remaining-healing fixture must have a certified native Regent root.");
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatPredictionSimulator parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        using (SimulationNotificationIsolation.Enter())
        {
            int Bound(CombatPredictionSimulator sim)
                => StrategicHpRecoveryBound.RemainingHealingUpperBound(sim, player, 0);
            CombatPredictionSimulator closed = parent.Fork();
            SimPlayerCombatState state = closed.State.GetPlayerCombatState(player);
            PredictedCard bundle = state.Hand.Cards.Single(card => card.Preview is BundleOfJoy);
            if (Bound(closed) != int.MaxValue)
                throw new InvalidOperationException("An unplayed generator must keep unknown future healing.");
            state.Hand.Remove(bundle);
            state.ExhaustPile.Add(bundle);
            if (Bound(closed) != 28)
                throw new InvalidOperationException("Existing Regen 2 and an unused Regen 5 dose must allow 28 healing.");

            var transforming = closed.Fork();
            ((SimulatedCombatState)transforming.State.CombatState).SetAmount<EntropyPower>(player.Creature, 1);
            if (Bound(transforming) != int.MaxValue)
                throw new InvalidOperationException("Future random transformation must retain unknown healing.");

            foreach (PileType pile in new[] { PileType.Hand, PileType.Exhaust })
            {
                var unknown = closed.Fork();
                unknown.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<NotYet>(), player),
                    pile, player, resultKind: CardGenerationResultKind.Fixed);
                if (Bound(unknown) != int.MaxValue
                    || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(unknown, player))
                    throw new InvalidOperationException("Unknown active or exhausted cards must disable the certificate.");
            }
            var returning = closed.Fork();
            var returningState = (SimulatedCombatState)returning.State.CombatState;
            var bolas = PredictedCard.Create(ModelDb.Card<Bolas>(), player);
            returning.AddGeneratedCardToCombat(bolas, PileType.Hand, player,
                resultKind: CardGenerationResultKind.Fixed);
            returningState.RecordCardLifecycle(returning, bolas);
            returning.State.GetPlayerCombatState(player).Hand.Remove(bolas);
            if (!returningState.HasPendingReturningCards || Bound(returning) != int.MaxValue)
                throw new InvalidOperationException("An off-pile scheduled return must retain unknown future healing.");

            var spent = closed.Fork();
            var combat = (SimulatedCombatState)spent.State.CombatState;
            int slot = Enumerable.Range(0, player.PotionSlots.Count)
                .Single(index => combat.GetPotionAtSlot(player, index)?.Id.Entry == "REGEN_POTION");
            combat.ConsumePotion(player, slot);
            if (Bound(spent) != 3 || Bound(closed) != 28)
                throw new InvalidOperationException("Consumed potion slots must belong to the branch, not the root.");
            combat.SetAmount<RegenPower>(player.Creature, 0);
            if (Bound(spent) != 0)
                throw new InvalidOperationException("Expired regeneration must leave zero future healing.");

            var evaluator = new SurgicalEvaluationDriver(root, SolverDisplayNames.Capture(live),
                BattleDamageTracker.Observe(live),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null));
            var noRecovery = spent.Fork();
            noRecovery.Damage(player.Creature, 7, ValueProp.Unblockable | ValueProp.Unpowered,
                live.Enemies.Single());
            var recovery = closed.Fork();
            recovery.Damage(player.Creature, 7, ValueProp.Unblockable | ValueProp.Unpowered,
                live.Enemies.Single());
            SimulationSnapshot reject = evaluator.Evaluate(noRecovery);
            SimulationSnapshot retain = evaluator.Evaluate(recovery);
            try
            {
                SearchNode Node(SimulationSnapshot snapshot) => new(null, 0, 0, 0, root.StartTurnNumber,
                    SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey, snapshot.HasRisk,
                    SearchBoundaryReason.None, false, null, snapshot, null!);
                SearchNode bad = Node(reject), good = Node(retain);
                List<SearchNode> pool = [bad, good];
                var output = CombatBeamSolver.ApplyPrimaryIncumbentBound(pool, new(5, 99), out int pruned,
                    remainingHealingPotential: snapshot => Bound((CombatPredictionSimulator)snapshot.Simulator));
                if (pruned != 1 || output.Count != 1 || !ReferenceEquals(output[0], good)
                    || pool.Count != 2)
                    throw new InvalidOperationException("The incumbent must prune only the branch unable to recover its excess loss.");
            }
            finally { reject.ReleaseSimulator(); retain.ReleaseSimulator(); }
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Remaining-healing inspection changed its parent or live combat.");
        }

        // Use the native public potion action, then compare every native regeneration
        // tick with its mirrored callback on a separate root fork. Both existing Regen
        // and the dose must be included before the first tick, even across later forks.
        CombatPredictionSimulator predicted = root.ForkSimulator();
        var shadow = (SimulatedCombatState)predicted.State.CombatState;
        int potionSlot = Enumerable.Range(0, player.PotionSlots.Count)
            .Single(index => shadow.GetPotionAtSlot(player, index)?.Id.Entry == "REGEN_POTION");
        var potion = shadow.GetPotionAtSlot(player, potionSlot)!;
        using (SimulationNotificationIsolation.Enter())
        {
            shadow.ConsumePotion(player, potionSlot);
            shadow.BeforePotionUsed(predicted, potion, player.Creature);
            if (!PotionOnUseSupport.Use(predicted, shadow, potion, player.Creature))
                throw new InvalidOperationException("Regen potion unexpectedly suspended.");
            shadow.AfterPotionUsed(predicted, potion, player.Creature);
        }
        player.GetPotionAtSlotIndex(potionSlot)!.EnqueueManualUse(player.Creature);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        for (int tick = 1; tick <= 7; tick++)
        {
            using (SimulationNotificationIsolation.Enter())
            {
                HookMirrors.BeforeSideTurnEnd(predicted, CombatSide.Player, [player.Creature]);
                CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(shadow, [player.Creature]);
            }
            await TriggerActualSideTurnEndAsync(live, CombatSide.Player, [player.Creature]);
            AssertSnapshotEqual(CaptureSimulated(predicted, shadow, player, live.Enemies.Single()),
                CaptureActual(live, player, live.Enemies.Single()), "RemainingHealing", $"RegenTick{tick}");
        }
        if (predicted.State.GetCreature(player.Creature).CurrentHp != root.InitialPlayerHp + 28
            || shadow.GetAmount<RegenPower>(player.Creature) != 0
            || DescribeContinuationContractState(parent, root, player) != parentBefore)
            throw new InvalidOperationException("Native stacked regeneration exceeded its bound or mutated the root fork.");
        if (StrategicHpRecoveryBound.RegenerationHealingUpperBound(long.MaxValue, 0) != int.MaxValue
            || StrategicHpRecoveryBound.RegenerationHealingUpperBound(65_536, 0) != int.MaxValue
            || StrategicHpRecoveryBound.RegenerationHealingUpperBound(-1, 6) != 6)
            throw new InvalidOperationException("Regeneration bound must saturate without overflow.");
        _completedChecks.Add("RemainingHealing:Generator:RandomTransform:UnknownHandAndExhaust:ScheduledReturn:PotionFork:Incumbent:NativeStackedRegen7Ticks:RootIsolation:Saturation");

        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 6);
        foreach (string id in new[] { "STRIKE_REGENT", "STRIKE_REGENT", "DEFEND_REGENT" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
        SetEnergy(player, 3);
        CombatRootSnapshot searchRoot = CombatRootSnapshot.Capture(live);
        if (!searchRoot.CanCertifyRemainingHealing
            || StrategicHpRecoveryBound.RemainingHealingUpperBound(searchRoot.ForkSimulator(), player, 0) != 0)
            throw new InvalidOperationException("The native search fixture must exercise the zero-healing certificate.");
        string searchBefore = ContinuationStamp.CaptureLive(live).StateText;
        var names = SolverDisplayNames.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
        };
        var profile = policy.Profile with { BeamWidth = 12, MaxExpandedNodes = 80 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Task.Run(() => new CombatBeamSolver(searchRoot, names, damage, policy,
            deadline.Token, searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        int expectedBattleLoss = damage.HpLostSoFar;
        if (result.CombatEndedTurn != searchRoot.StartTurnNumber
            || result.ProjectedBattleHpLost != expectedBattleLoss
            || result.HpLostByTurn.Values.Any(loss => loss != 0)
            || ContinuationStamp.CaptureLive(live).StateText != searchBefore)
            throw new InvalidOperationException($"Certified Regent strict incremental search changed native route quality or live state: "
                + $"turn={result.CombatEndedTurn}, loss={result.ProjectedBattleHpLost}, expectedLoss={expectedBattleLoss}.");
        _completedChecks.Add("RemainingHealing:CertifiedRegent:Dop2:StrictIncremental:CompleteVictory:NoAdditionalLoss:LiveIsolation");
    }
}
