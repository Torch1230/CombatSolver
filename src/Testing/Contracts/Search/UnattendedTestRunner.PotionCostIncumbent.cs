using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPotionCostIncumbentAsync(CombatState live, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 2 });
        await SetBlockAsync(player.Creature, 100);
        InjectPotionForTest(player, "CURE_ALL");
        InjectPotionForTest(player, "SHACKLING_POTION");
        SetEnergy(player, 3);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 12);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.UsesComponentHealingCertificate)
            throw new InvalidOperationException("Potion-cost fixture requires a certified healing root: " + root.ComponentHealingRejection);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
            StopAtAcceptableBattleHpLoss = false, UseNoveltyPortfolio = false,
            PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []),
            RelicTargets = [], PrimaryIncumbents = new(),
        };
        policy = policy with { Profile = policy.Profile with
            { BeamWidth = 8, MaxExpandedNodes = 200, SoftTimeBudgetMilliseconds = 10000 } };
        var parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var proof = await Task.Run(() => CombatBeamSolver.PotionCostIncumbentProbeForTesting(root, names, damage, policy));
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Potion-cost proof changed parent/live/RNG.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int previous = player.PlayerCombatState!.TurnNumber;
        CombatManager.Instance.OnEndedTurnLocally();
        var endTurn = new EndPlayerTurnAction(player, previous);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(endTurn);
        await endTurn.CompletionTask.WaitAsync(deadline.Token);
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= previous)
        {
            deadline.Token.ThrowIfCancellationRequested(); EnsureWithinDeadline(); await NextFrameAsync();
        }
        string endedExpected = ContinuationStamp.CapturePredicted(player, proof.LatePartial.Parent!.Snapshot.Simulator,
            proof.LatePartial.Parent.Turn, root.Forecast, root.StartTurnNumber).StateText;
        if (ContinuationStamp.CaptureLive(live).StateText != endedExpected)
            throw new InvalidOperationException("Native later-turn cost prefix full state differs.");
        PotionModel nativePotion = player.GetPotionAtSlotIndex(1)!;
        GameAction use = await SolverController.EnqueueAndCaptureActionAsync(
            action => action is UsePotionAction potion && potion.PotionIndex == 1 && ReferenceEquals(potion.Player, player),
            () => nativePotion.EnqueueManualUse(null), deadline.Token);
        await use.CompletionTask.WaitAsync(deadline.Token);
        string expected = ContinuationStamp.CapturePredicted(player, proof.LatePartial.Snapshot.Simulator,
            proof.LatePartial.Turn, root.Forecast, root.StartTurnNumber).StateText;
        if (ContinuationStamp.CaptureLive(live).StateText != expected)
            throw new InvalidOperationException("Native cheap potion full state differs from proof branch.");
        _completedChecks.Add($"PotionCostIncumbent:RealCompleteVictories:SameHpTurnPotionCount:Costs={proof.ExpensiveCost}/{proof.CheapCost}:SharedKept={proof.SharedKept}:LocalKept={proof.LocalKept}:SharedAblationKept={proof.AblationKept}:SameCostPruned={proof.SameCostPruned}:MissingCostKept={proof.MissingCostKept}:ZeroCostPruned={proof.ZeroCostPruned}:LateCheapSharedKept={proof.LateSharedKept}:LateCheapLocalKept={proof.LateLocalKept}:NativeLateEndTurnAndPotionFullStates:ParentLiveRngIsolation");
        if (!proof.SharedKept || !proof.LocalKept || !proof.LateSharedKept || !proof.LateLocalKept)
            throw new InvalidOperationException("Lower-cost equal-HP branch was pruned despite complete same-turn and later-turn victories: "
                + $"costs={proof.ExpensiveCost}/{proof.CheapCost} shared_kept={proof.SharedKept} local_kept={proof.LocalKept} late_shared_kept={proof.LateSharedKept} late_local_kept={proof.LateLocalKept}");
    }
}

internal sealed partial class CombatBeamSolver
{
    internal sealed record PotionCostIncumbentProof(SearchNode CheapPartial, int ExpensiveCost, int CheapCost,
        bool SharedKept, bool LocalKept, bool AblationKept,
        bool SameCostPruned, bool MissingCostKept, bool ZeroCostPruned,
        SearchNode LatePartial, bool LateSharedKept, bool LateLocalKept);

    internal static PotionCostIncumbentProof PotionCostIncumbentProbeForTesting(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SolverResult counterfactual = new CombatBeamSolver(root, names, damage, policy, deadline.Token,
            searchProfile: policy.Profile with { MaxExpandedNodes = 1 },
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve();
        if (counterfactual.Snapshot.AllEnemiesDead)
            throw new InvalidOperationException("Fixture counterfactual should require multiple actions.");
        PotionFreePolicyBaseline baseline = new(false, counterfactual.ProjectedBattleHpLost,
            counterfactual.Snapshot.PlayerHp, counterfactual.CombatEndedTurn);
        CombatBeamSolver Member(SearchPolicySnapshot p) => new(root, names, damage, p, deadline.Token,
            potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne, potionFreePolicyBaseline: baseline,
            maximumPotionUses: 1, minimumPotionUses: 1, directSearchPurpose: DirectSearchPurpose.SmartPotionGradient);
        var expensive = Member(policy);
        var cheap = Member(policy);
        var ablated = Member(policy with { DisableSharedPrimaryIncumbentsForTesting = true });
        using var isolation = SimulationNotificationIsolation.Enter();
        SearchNode ExpensiveWinner = expensive.CostRouteForTesting(0, out SearchNode expensivePartial);
        SearchNode CheapWinner = cheap.CostRouteForTesting(1, out SearchNode cheapPartial);
        if (!ExpensiveWinner.IsTerminal || !CheapWinner.IsTerminal
            || ExpensiveWinner.Snapshot.HasRisk || CheapWinner.Snapshot.HasRisk
            || ExpensiveWinner.Snapshot.CumulativePlayerHpLost != CheapWinner.Snapshot.CumulativePlayerHpLost
            || ExpensiveWinner.Snapshot.RecoveredPlayerHp != CheapWinner.Snapshot.RecoveredPlayerHp
            || ExpensiveWinner.Snapshot.PlayerMaxHp != CheapWinner.Snapshot.PlayerMaxHp
            || ExpensiveWinner.Snapshot.CombatEndedTurn != CheapWinner.Snapshot.CombatEndedTurn
            || ExplicitPotionUseCount(ExpensiveWinner) != 1 || ExplicitPotionUseCount(CheapWinner) != 1
            || ExpensiveWinner.PotionStrategicCost <= CheapWinner.PotionStrategicCost)
            throw new InvalidOperationException("Routes do not prove a strictly cheaper equivalent resource outcome: "
                + $"expensive terminal={ExpensiveWinner.IsTerminal} risk={ExpensiveWinner.Snapshot.HasRisk} hpLost={ExpensiveWinner.Snapshot.CumulativePlayerHpLost} healed={ExpensiveWinner.Snapshot.RecoveredPlayerHp} maxHp={ExpensiveWinner.Snapshot.PlayerMaxHp} turn={ExpensiveWinner.Snapshot.CombatEndedTurn} count={ExplicitPotionUseCount(ExpensiveWinner)} cost={ExpensiveWinner.PotionStrategicCost} enemyHp={ExpensiveWinner.Snapshot.EnemyHp}; "
                + $"cheap terminal={CheapWinner.IsTerminal} risk={CheapWinner.Snapshot.HasRisk} hpLost={CheapWinner.Snapshot.CumulativePlayerHpLost} healed={CheapWinner.Snapshot.RecoveredPlayerHp} maxHp={CheapWinner.Snapshot.PlayerMaxHp} turn={CheapWinner.Snapshot.CombatEndedTurn} count={ExplicitPotionUseCount(CheapWinner)} cost={CheapWinner.PotionStrategicCost} enemyHp={CheapWinner.Snapshot.EnemyHp}");
        if (!expensive.TightenPrimarySearchIncumbentAtTurnLayer([ExpensiveWinner], 0))
            throw new InvalidOperationException("Actual eligible expensive victory did not publish its bound.");
        bool sharedKept = cheap.ApplyPrimaryIncumbentBound([cheapPartial]).Count != 0;
        bool localKept = expensive.ApplyPrimaryIncumbentBound([cheapPartial]).Count != 0;
        bool ablationKept = ablated.ApplyPrimaryIncumbentBound([cheapPartial]).Count != 0;
        if (!ablationKept) throw new InvalidOperationException("Bound sharing is not isolated by the ablation.");
        bool sameCostPruned = cheap.ApplyPrimaryIncumbentBound([expensivePartial]).Count == 0
            && expensive.ApplyPrimaryIncumbentBound([expensivePartial]).Count == 0;
        var late = Member(policy);
        SearchNode lateWinner = late.CostRouteForTesting(1, out SearchNode latePartial, laterTurn: true);
        if (!lateWinner.IsTerminal || lateWinner.Snapshot.HasRisk
            || lateWinner.Snapshot.CumulativePlayerHpLost != ExpensiveWinner.Snapshot.CumulativePlayerHpLost
            || lateWinner.Snapshot.RecoveredPlayerHp != ExpensiveWinner.Snapshot.RecoveredPlayerHp
            || lateWinner.Snapshot.PlayerMaxHp != ExpensiveWinner.Snapshot.PlayerMaxHp
            || lateWinner.Snapshot.CombatEndedTurn <= ExpensiveWinner.Snapshot.CombatEndedTurn
            || ExplicitPotionUseCount(lateWinner) != 1
            || lateWinner.PotionStrategicCost >= ExpensiveWinner.PotionStrategicCost)
            throw new InvalidOperationException("Later turn does not prove an actually complete lower-cost victory at equal HP.");
        bool lateSharedKept = late.ApplyPrimaryIncumbentBound([latePartial]).Count == 1;
        bool lateLocalKept = expensive.ApplyPrimaryIncumbentBound([latePartial]).Count == 1;
        var unknownTable = new PrimaryIncumbentTable();
        if (!policy.PrimaryIncumbents!.TryGet(ResourceIncumbentPolicy.CompletedBucket(ExpensiveWinner.Snapshot, 1), out var witnessed))
            throw new InvalidOperationException("Completed victory's shared metadata is absent.");
        unknownTable.Tighten(ResourceIncumbentPolicy.CompletedBucket(ExpensiveWinner.Snapshot, 1),
            witnessed with { ExplicitPotionStrategicCost = null });
        var unknown = Member(policy with { PrimaryIncumbents = unknownTable });
        bool missingCostKept = unknown.ApplyPrimaryIncumbentBound([cheapPartial]).Count == 1
            && unknown.ApplyPrimaryIncumbentBound([expensivePartial]).Count == 1;
        var zeroTable = new PrimaryIncumbentTable();
        var zero = new CombatBeamSolver(root, names, damage, policy with { PrimaryIncumbents = zeroTable }, deadline.Token,
            potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0, minimumPotionUses: 0);
        SearchNode zeroWinner = zero.CostRouteForTesting(null, out SearchNode zeroPartial);
        if (!zeroWinner.IsTerminal || zeroWinner.Snapshot.HasRisk || zeroWinner.PotionStrategicCost != 0
            || !zero.TightenPrimarySearchIncumbentAtTurnLayer([zeroWinner], 0))
            throw new InvalidOperationException("Actual potion-free victory did not publish its zero-cost bound.");
        bool zeroCostPruned = zero.ApplyPrimaryIncumbentBound([zeroPartial]).Count == 0;
        var exactFloor = Member(policy with { DisableSharedPrimaryIncumbentsForTesting = true });
        if (!exactFloor.TightenPrimarySearchIncumbentAtTurnLayer([CheapWinner], 0)
            || exactFloor.ApplyPrimaryIncumbentBound([zeroPartial]).Count != 1
            || expensive.ApplyPrimaryIncumbentBound([zeroPartial]).Count != 1)
            throw new InvalidOperationException("Unpaid exact-layer prefixes must remain: initial bottle prices do not prove unavoidable future potion cost.");
        if (!sameCostPruned || !missingCostKept || !zeroCostPruned)
            throw new InvalidOperationException($"Potion-cost guard failed: same={sameCostPruned} unknown={missingCostKept} zero={zeroCostPruned}.");
        return new(cheapPartial, ExpensiveWinner.PotionStrategicCost, CheapWinner.PotionStrategicCost,
            sharedKept, localKept, ablationKept, sameCostPruned, missingCostKept, zeroCostPruned, latePartial, lateSharedKept, lateLocalKept);
    }

    private SearchNode CostRouteForTesting(int? slot, out SearchNode potionOnly, bool laterTurn = false)
    {
        SearchNode node = CreateOpeningSearchSeed(Replay([]));
        if (laterTurn) node = CostChildForTesting(node, new(PlanActionKind.EndTurn, node.Turn));
        if (slot is { } selectedSlot)
        {
            PlanAction potion = PreparePotionActions(node).Single(action => action.Action.PotionSlot == selectedSlot).Action;
            node = CostChildForTesting(node, potion);
        }
        potionOnly = node;
        for (int i = 0; i < 2; i++)
        {
            PlanAction strike = PrepareCardActions(node).First(action => action.Action.CardId == "STRIKE_IRONCLAD").Action;
            node = CostChildForTesting(node, strike);
        }
        return node;
    }

    private SearchNode CostChildForTesting(SearchNode parent, PlanAction action)
    {
        SimulationSnapshot snapshot = ReplayAction(parent, action);
        return new(action, parent.ActionCount + 1, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, parent.Traits, parent.FutureSoldHp, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, snapshot.AllEnemiesDead && !snapshot.PlayerDead,
            parent, snapshot, CombatProgressState.Capture(snapshot));
    }
}
