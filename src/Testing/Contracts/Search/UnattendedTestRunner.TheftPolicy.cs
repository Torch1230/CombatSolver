using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertGremlinEscapeGoldAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        bool stateOnly = _request.ScenarioId != "GREMLIN-ESCAPE-GOLD";
        bool stolen = _request.ScenarioId.EndsWith("-STOLEN", StringComparison.Ordinal);
        var merc = live.Enemies.Single(enemy => enemy.Monster is GremlinMerc);
        if (stolen)
        {
            ThieveryPower thievery = merc.Powers.OfType<ThieveryPower>().Single();
            thievery.DynamicVars.Gold.BaseValue = 20;
            thievery._target = player.Creature;
        }
        await CreatureCmd.Kill(merc);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (stateOnly)
        {
            await AssertGremlinGoldEscapeStateAsync(live, player, stolen);
            return;
        }
        await CreatureCmd.Kill(live.Enemies.Single(enemy => enemy.Monster is SneakyGremlin));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var fat = live.Enemies.Single(enemy => enemy.Monster is FatGremlin);
        await CreatureCmd.SetMaxHp(fat, 18);
        await CreatureCmd.SetCurrentHp(fat, 18);
        var flee = (MoveState)fat.Monster!.MoveStateMachine!.States["FLEE_MOVE"];
        fat.Monster.SetMoveImmediate(flee, true);
        for (int i = 0; i < 3; i++)
            await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 3);
        if (live.Encounter!.CalculateGoldProportion(live) != 1f)
            throw new InvalidOperationException("Gremlin gold fixture did not start with the full gold reward.");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live,
            includeTurnSetup: false, theftPolicy: SolverTheftPolicy.PreserveResources);
        policy = policy with
        {
            Profile = policy.Profile with { MaxExpandedNodes = 256, SoftTimeBudgetMilliseconds = 1500 },
            FixedBudget = true,
            BudgetOverrideMilliseconds = 1500,
            PotionPolicy = SolverPotionPolicy.Disabled,
            UseNoveltyPortfolio = false,
            UseBeamWidthPortfolio = false,
            EarlyTurnExplorationDepth = 0,
            IgnoreLongTermRewards = true,
        };
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        List<object> evidence = [];
        foreach (SolverTheftPolicy theft in new[] { SolverTheftPolicy.LetEscape, SolverTheftPolicy.PreserveResources })
        {
            SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                policy with { TheftPolicy = theft }, CancellationToken.None, progressCallback: null));
            evidence.Add(new
            {
                policy = theft.ToString(),
                actions = result.BestNode.Actions.Select(a => a.Kind.ToString()).ToArray(),
                result.ProjectedBattleHpLost, result.PotionCount, result.CombatEndedTurn,
                result.ExpandedNodes, result.TransitionCount, result.ForkCount,
                elapsedMilliseconds = result.Elapsed.TotalMilliseconds, result.WorkerAllocatedBytes,
                result.ForfeitsEncounterGold,
            });
            _writer.WriteGeneratedArtifact("gremlin-gold-search.json", new
            {
                root = ContinuationStamp.CaptureLive(live).StateText,
                policy.Profile.MaxExpandedNodes, policy.Profile.SoftTimeBudgetMilliseconds,
                results = evidence,
            });
            int attacks = result.BestNode.Actions.Count(action => action.Kind == PlanActionKind.PlayCard);
            if (theft == SolverTheftPolicy.PreserveResources
                    && (attacks != 3 || result.ForfeitsEncounterGold)
                || theft == SolverTheftPolicy.LetEscape
                    && (attacks != 0 || !result.ForfeitsEncounterGold))
                throw new InvalidOperationException($"Gremlin gold policy {theft} selected an incorrect escape/kill route.");
            if (result.ProjectedBattleHpLost != 0 || result.PotionCount != 0)
                throw new InvalidOperationException("Gremlin gold fixture changed HP or potion cost.");
            _completedChecks.Add($"GremlinEscapeGold:{theft}:attacks={attacks}:HP=0:potions=0:forfeitsGold={result.ForfeitsEncounterGold}");
        }
        AssertGremlinGoldPresentation();
        CombatPredictionSimulator prediction = root.ForkSimulator();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        var nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
        for (int index = 0; index < nativeCards.Length; index++)
        {
            var nativeCard = nativeCards[index];
            var shadow = (SimulatedCombatState)prediction.State.CombatState;
            using (SimulationNotificationIsolation.Enter())
            using (shadow.BeginCardExecutionScope(new ForkableSet<uint>()))
                if (!prediction.ManualPlay(prediction.State.FindCard(nativeCard)!, fat, out _))
                    throw new InvalidOperationException("Gremlin lethal attack unexpectedly suspended.");
            if (!CombatBeamSolver.SettleReplayActionBoundary(prediction, shadow))
                throw new InvalidOperationException("Gremlin lethal attack did not settle.");
            GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(fat)) throw new InvalidOperationException("Native Gremlin lethal attack was illegal."); },
                deadline.Token);
            await native.CompletionTask.WaitAsync(deadline.Token);
            if (index < nativeCards.Length - 1)
            {
                string expected = ContinuationStamp.CapturePredicted(player, prediction,
                    root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
                string actual = ContinuationStamp.CaptureLive(live).StateText;
                if (actual != expected)
                    throw new InvalidOperationException($"Gremlin attack {index} differs:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
            }
        }
        if (fat.CurrentHp != 0 || live.Encounter!.CalculateGoldProportion(live) != 1f
            || TheftEncounterStrategy.ForfeitsEncounterGold(live))
            throw new InvalidOperationException("Three native attacks did not kill the gold carrier and preserve base gold.");
        _completedChecks.Add("GremlinEscapeGold:NativeThreeAttacks:ExactIntermediateContinuations:KilledCarrier:FullGoldReward");
    }

    private async Task AssertGremlinGoldEscapeStateAsync(CombatState live, Player player, bool stolen)
    {
        var fat = live.Enemies.Single(enemy => enemy.Monster is FatGremlin);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        StateFingerprint Key(CombatPredictionSimulator simulator)
        {
            StateFingerprintBuilder key = new();
            ((SimulatedCombatState)simulator.State.CombatState).AppendFingerprint(ref key, simulator);
            return key.Finish();
        }
        string before = Stamp(parent);
        StateFingerprint beforeKey = Key(parent);
        CombatPredictionSimulator child = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        using (SimulationNotificationIsolation.Enter())
            shadow.CreatureEscaped(fat);
        if (!TheftEncounterStrategy.ForfeitsEncounterGold(shadow)
            || TheftEncounterStrategy.ForfeitsEncounterGold((SimulatedCombatState)parent.State.CombatState)
            || Stamp(parent) != before || Key(parent) != beforeKey || Key(child) == beforeKey
            || Stamp(child.Fork()) != Stamp(child))
            throw new InvalidOperationException("Escaped gold carrier state was lost across identity, fingerprint or Fork.");
        string expected = Stamp(child);
        await CreatureCmd.Escape(fat);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        string actual = ContinuationStamp.CaptureLive(live).StateText;
        if (actual != expected)
            throw new InvalidOperationException($"Gremlin escape continuation differs:\nEXPECTED\n{expected}\nACTUAL\n{actual}");
        float expectedProportion = stolen ? 0f : 0.5f;
        if (live.Encounter!.CalculateGoldProportion(live) != expectedProportion
            || !TheftEncounterStrategy.ForfeitsEncounterGold(live))
            throw new InvalidOperationException("Gremlin escape did not lose the native base gold reward.");
        _completedChecks.Add($"GremlinEscapeGold:NativeGoldProportion={expectedProportion}:ExactContinuation:StateKey:ParentAndForkIsolation:stolen={stolen}");
    }

    private static void AssertGremlinGoldPresentation()
    {
        string language = LocManager.Instance.Language;
        try
        {
            foreach (string locale in new[] { "zhs", "eng", "zht" })
            {
                LocManager.Instance.SetLanguage(locale);
                string warning = locale == "eng" ? "Reduced post-combat gold reward" : "预计战后金币奖励减少";
                if (SolverOverlaySnapshot.DescribeUnrecoveredLoot(0, 0, 0, true) != warning
                    || SolverOverlaySnapshot.DescribeUnrecoveredLoot(0, 0, 0, false) != null
                    || !SolverOverlaySnapshot.DescribeUnrecoveredLoot(20, 20, 0, true)!.EndsWith("\n" + warning, StringComparison.Ordinal))
                    throw new InvalidOperationException("Escaped gold reward warning lost its locale or stole a loot quantity.");
            }
        }
        finally { LocManager.Instance.SetLanguage(language); }
    }

    private static async Task AssertTheftRecoveryPolicyAsync(CombatState combat)
    {
        AssertRequiredPotionAuditSelectionAndTotals();
        SolverInterimResult goldEscape = new(true, 0, 0, 0, 0, 0, 0, 100, 1)
        { TheftPolicy = SolverTheftPolicy.PreserveResources, ForfeitsEncounterGold = true, Survives = true };
        SolverInterimResult goldPreserved = goldEscape with
        { ForfeitsEncounterGold = false, ProjectedBattleHpLost = 1, StrategicHpDeficit = 1 };
        if (!SolverInterimResultOrdering.IsBetter(goldPreserved, goldEscape)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(goldPreserved, goldEscape)
            || TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.PreserveResources, 0, true)
            || !TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.LetEscape, 0, true)
            || SolverInterimResultOrdering.IsBetter(goldPreserved with { Won = false }, goldEscape)
            || !CombatSearchCoordinator.IsBetterPotionPolicyResult(SolverTheftPolicy.PreserveResources, goldPreserved, goldEscape))
            throw new InvalidOperationException("Encounter gold loss escaped ordering, promotion or early-stop policy.");
        SolverInterimResult escape = new(true, 20, 0, 0, 0, 0, 0, 100, 2)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        SolverInterimResult recovered = new(true, 0, 15, 15, 18, 2, 0, 0, 4)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        if (!SolverInterimResultOrdering.IsBetter(recovered, escape)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(recovered, escape)
            || SolverInterimResultOrdering.IsBetter(escape, recovered)
            || CombatSearchCoordinator.IsBetterPotionPolicyResult(SolverTheftPolicy.LetEscape, recovered, escape)
            || SolverInterimResultOrdering.IsBetter(recovered with { TheftPolicy = SolverTheftPolicy.LetEscape },
                escape with { TheftPolicy = SolverTheftPolicy.LetEscape })
            || TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.PreserveResources, 20)
            || !TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.LetEscape, 20)
            || SolverInterimResultOrdering.IsBetter(recovered with { Won = false }, escape))
            throw new InvalidOperationException("Theft policy mixed recovery, HP, potion, early-stop or victory priorities.");

        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
            includeTurnSetup: false, theftPolicy: SolverTheftPolicy.PreserveResources);
        SolverSearchProfile profile = policy.Profile with { MaxExpandedNodes = 256, SoftTimeBudgetMilliseconds = 1500 };
        policy = policy with
        {
            Profile = profile, FixedBudget = true,
            BudgetOverrideMilliseconds = 1500,
            VerifyIncrementalSearch = false,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        foreach (SolverTheftPolicy theft in Enum.GetValues<SolverTheftPolicy>())
        {
            SearchPolicySnapshot selectedPolicy = policy with { TheftPolicy = theft };
            SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                selectedPolicy, CancellationToken.None, progressCallback: null));
            if (theft == SolverTheftPolicy.PreserveResources && result.OutstandingStolenResource > 0
                && CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(selectedPolicy, result))
                throw new InvalidOperationException("Unrecovered loot incorrectly satisfied HP early stop.");
            Entry.Logger.Info($"[CombatSolver/Test] THEFT_RECOVERY_POLICY policy={theft} outstanding={result.OutstandingStolenResource} hp_loss={result.ProjectedBattleHpLost} potions={result.PotionCount} ended={result.CombatEndedTurn}");
        }
    }
}
