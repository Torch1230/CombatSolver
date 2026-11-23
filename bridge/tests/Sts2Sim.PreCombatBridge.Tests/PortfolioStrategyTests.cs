using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Beam;
using CombatSolver.Beam.Novelty;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Encounters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.ValueProps;
using BeamSearchNode = CombatSolver.Beam.SearchNode;
using BeamPotionPolicy = CombatSolver.Beam.Policy.SolverPotionPolicy;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class PortfolioStrategyTests
{
    [Fact]
    public void PowerCardValuationCatalog_HasAll122Cards_AndDetectsReachableSilentPowers()
    {
        TestSaveBuilder.EnsurePrewarmed();
        PowerCardValuationRegistry registry = PowerCardValuationModels.Registry;
        Assert.Equal(122, registry.Count);
        Assert.True(registry.TryGetPool("FOOTWORK", out PowerCardPool pool));
        Assert.Equal(PowerCardPool.Silent, pool);
        Assert.True(registry.TryGetCommitmentDescriptor("AFTERIMAGE", out PowerCommitmentDescriptor afterimage));
        Assert.Equal(PowerCommitmentFamily.DefenseEfficiency, afterimage.Family);
        Assert.False(registry.TryGetCommitmentDescriptor("ROYALTIES", out _));
        Assert.True(registry.TryGetPool("ROYALTIES", out PowerCardPool regentPool));
        Assert.Equal(PowerCardPool.Regent, regentPool);
    }

    [Fact]
    public void BeamWidthPortfolio_ProductionMembers_AndGate_MatchContracts()
    {
        IReadOnlyList<BeamWidthPortfolioMemberSpec> defaultMembers = BeamWidthPortfolio.ProductionMembers(
            baselineBeamWidth: 24,
            configuredWidths: null,
            includePlainBaseline: true,
            includePowerCommitmentMember: true,
            useOffensiveRefinement: false,
            appendBoundedOffensiveRefinement: false);

        Assert.Equal(
            [
                "24",
                "24+power",
                "16",
                "36",
                "24+band",
                "24+base",
            ],
            defaultMembers.Select(m => m.ToString()).ToArray());

        IReadOnlyList<BeamWidthPortfolioMemberSpec> reallocated = BeamWidthPortfolio.ProductionMembers(
            baselineBeamWidth: 24,
            configuredWidths: null,
            includePlainBaseline: false,
            includePowerCommitmentMember: true,
            useOffensiveRefinement: false,
            appendBoundedOffensiveRefinement: true);
        Assert.Equal(
            [
                "24+power",
                "16",
                "36",
                "24+band",
                "24+base",
                "10+offense+bounded",
            ],
            reallocated.Select(m => m.ToString()).ToArray());

        IReadOnlyList<BeamWidthPortfolioMemberSpec> explicitWidths = BeamWidthPortfolio.ProductionMembers(
            baselineBeamWidth: 24,
            configuredWidths: [24, 48, 48, 12],
            includePlainBaseline: true,
            includePowerCommitmentMember: false);
        Assert.Equal(["24", "48", "12"], explicitWidths.Select(m => m.ToString()).ToArray());

        Assert.Throws<ArgumentException>(() => BeamWidthPortfolio.ProductionMembers(
            24, null, useOffensiveRefinement: true, appendBoundedOffensiveRefinement: true));

        Assert.Equal(200, BeamWidthPortfolio.DedicatedPowerNodeReserve(1000));
        Assert.Equal(
            CombatSolver.PowerCommitmentPortfolioGate.SkippedNoReachablePower,
            CombatSolver.PowerCommitmentPortfolioGate.Reject(hasReachablePower: false));
        Assert.Null(CombatSolver.PowerCommitmentPortfolioGate.Reject(hasReachablePower: true));

        var exhaustedFast = new BeamWidthPortfolioBaseline(
            FrontierExhausted: true, ProvenZeroDamage: false, ElapsedMilliseconds: 100, ExpandedNodes: 400, BeamWidth: 24);
        Assert.Null(BeamWidthPortfolioGate.RejectRefinement(exhaustedFast, 36, 500, 900, 1000));
        Assert.Equal(
            BeamWidthPortfolioGate.SkippedBaselineProvenZeroDamage,
            BeamWidthPortfolioGate.RejectRefinement(exhaustedFast with { ProvenZeroDamage = true }, 36, 500, 900, 1000));
        Assert.Equal(
            BeamWidthPortfolioGate.SkippedBaselineNotFrontierExhausted,
            BeamWidthPortfolioGate.RejectRefinement(exhaustedFast with { FrontierExhausted = false }, 36, 500, 900, 1000));
        Assert.Equal(
            BeamWidthPortfolioGate.SkippedBaselineTimeShareExceeded,
            BeamWidthPortfolioGate.RejectRefinement(exhaustedFast with { ElapsedMilliseconds = 300 }, 36, 500, 700, 1000));
        Assert.Equal(
            BeamWidthPortfolioGate.SkippedNodeHeadroom,
            BeamWidthPortfolioGate.RejectRefinement(exhaustedFast, 36, 399, 900, 1000));
        Assert.Equal(
            BeamWidthPortfolioGate.SkippedTimeHeadroom,
            BeamWidthPortfolioGate.RejectRefinement(exhaustedFast, 36, 500, 200, 1000));
    }

    [Fact]
    public async Task RetentionSecondRankBand_AndStateEvaluatorModes_BehaveDistinctly()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_WEAK");
        var defaultEval = new StateEvaluator(root, CombatPolicy.Default, BeamMemberSearchMode.Default);
        var baseEval = new StateEvaluator(root, CombatPolicy.Default, BeamMemberSearchMode.BaseScoreOnly);
        var offenseEval = new StateEvaluator(root, CombatPolicy.Default, BeamMemberSearchMode.OffensiveRefinement);

        CombatState boosted = root.Clone();
        boosted.Players[0].PlayerCombatState!.GainEnergy(3);
        boosted.Enemies[0].SetCurrentHpInternal(Math.Max(1, boosted.Enemies[0].CurrentHp - 5));

        double defScore = defaultEval.Score(boosted, 1, 0, 0, default, 0, 0);
        double baseScore = baseEval.Score(boosted, 1, 0, 0, default, 0, 0);
        double offenseScore = offenseEval.Score(boosted, 1, 0, 0, default, 0, 0);

        Assert.NotEqual(defScore, baseScore);
        Assert.NotEqual(defScore, offenseScore);

        BeamSearchNode baseNode = BeamSearchNode.Create(boosted, null, null, baseEval);
        BeamSearchNode offenseNode = BeamSearchNode.Create(boosted.Clone(), null, null, offenseEval);
        Assert.Equal(defaultEval.EvaluateBaselineScore(baseNode), offenseEval.EvaluateBaselineScore(offenseNode), 3);

        // 构造 6 个不同指纹桶，验证 SecondRankBand 将前 W=2 名普通候选移到末尾后选入中段名额。
        var nodes = new List<BeamSearchNode>(6);
        for (int i = 0; i < 6; i++)
        {
            CombatState st = root.Clone();
            st.Enemies[0].SetCurrentHpInternal(Math.Max(1, st.Enemies[0].CurrentHp - (5 - i)));
            nodes.Add(BeamSearchNode.Create(st, null, null, defaultEval));
        }

        List<BeamSearchNode> band1 = Retention.SelectWithKey(
            nodes,
            width: 2,
            static n => n.Fingerprint,
            secondRankBand: false);
        List<BeamSearchNode> band2 = Retention.SelectWithKey(
            nodes,
            width: 2,
            static n => n.Fingerprint,
            secondRankBand: true);

        Assert.Equal(2, band1.Count);
        Assert.Equal(2, band2.Count);
        Assert.Same(nodes[0], band1[0]);
        Assert.NotSame(nodes[0], band2[0]);
    }

    [Fact]
    public void BfwsAlgorithms_EnforceNoveltyOpenEvictionAndEscapeBudget()
    {
        var novelty = new BfwsPackedNovelty(width: 2, maximumEntries: 100);
        BfwsFact[] seedFacts = [new(BfwsFactKind.Energy, Value: 3), new(BfwsFactKind.Hp, Value: 14)];
        BfwsFact[] child1Facts = [new(BfwsFactKind.Energy, Value: 2), new(BfwsFactKind.Hp, Value: 14)];
        BfwsFact[] pairNovelFacts = [new(BfwsFactKind.Energy, Value: 3), new(BfwsFactKind.Hp, Value: 14)];

        Assert.Equal(1, novelty.Evaluate((5, 0), seedFacts));
        novelty.PrepareParent((5, 0), seedFacts);
        Assert.Equal(1, novelty.Evaluate((5, 0), child1Facts));
        Assert.Equal(3, novelty.Evaluate((5, 0), pairNovelFacts));
        // 新分区的同一组事实按独立分区重新计数为 novelty=1。
        Assert.Equal(1, novelty.Evaluate((4, 0), pairNovelFacts));

        var open = new BfwsBoundedOpen<string>(capacity: 2);
        Assert.False(open.Enqueue("a", (1, -10, 0), out _));
        Assert.False(open.Enqueue("b", (2, -20, 1), out _));
        Assert.True(open.Enqueue("c", (1, -5, 2), out string? dropped));
        Assert.Equal("b", dropped);
        Assert.Equal("a", open.Dequeue());
        Assert.Equal("c", open.Dequeue());

        var escape = new BfwsEscapeBudget(2);
        Assert.True(escape.TryAdmit());
        Assert.True(escape.TryAdmit());
        Assert.False(escape.TryAdmit());
    }

    [Theory]
    [InlineData(PortfolioSchedulingMode.Sequential, 1)]
    [InlineData(PortfolioSchedulingMode.Parallel, 4)]
    public async Task PortfolioCoordinator_NoveltyAndEarlyTurn_ProduceValidReplayAndExtendedMetrics(
        PortfolioSchedulingMode mode,
        int dop)
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL", addPowers: true);
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: true,
            PortfolioOptionsVersion: 2,
            UseNoveltyPortfolio: true,
            AdaptiveNoveltyRefinement: false,
            UseEarlyTurnExploration: true,
            EarlyTurnExplorationDepth: 2,
            EarlyTurnExplorationBudgetMilliseconds: 3_000,
            SchedulingMode: mode);
        var budget = new BeamBudget(16, 600, 10, 6, 6, TimeSpan.FromSeconds(6));
        CombatPolicy policy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Disabled,
            StopAtAcceptableBattleHpLoss = false,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var progressUpdates = new List<InCombatProgressUpdate>();

        InCombatBeamResult result = await BeamSearch.SolveForInCombatAsync(
            root,
            budget,
            policy,
            portfolio,
            priorBattleHpLost: 0,
            progressCallback: update =>
            {
                lock (progressUpdates)
                    progressUpdates.Add(update);
            },
            ct: cts.Token,
            maxDegreeOfParallelism: dop);

        Assert.NotEmpty(result.Actions);
        Assert.NotNull(result.ExpandedParents);
        Assert.NotNull(result.TransitionCount);
        Assert.NotNull(result.ChoiceReplayAttempts);
        Assert.NotNull(result.ObservedPeakParallelism);
        Assert.NotNull(result.ActivePhaseLabel);
        Assert.True(result.ExpandedParents > 0);
        Assert.True(result.TransitionCount > 0);
        Assert.True(result.ChoiceReplayAttempts > 0);
        Assert.True(result.ObservedPeakParallelism >= 1);

        CombatState replay = root.Clone();
        Player replayPlayer = replay.Players[0];
        foreach (InCombatActionTrace action in result.Actions)
        {
            Assert.True(replay.Engine!.IsInProgress);
            Assert.Equal(action.Turn, replayPlayer.PlayerCombatState!.TurnNumber);
            var choices = new TraceSelectionSource(action.Choices);
            replay.CardSelectionSource = choices;
            if (action.Kind == "EndTurn")
            {
                await replay.Engine.EndPlayerTurnAsync();
            }
            else
            {
                Assert.Equal("PlayCard", action.Kind); // Potions are disabled by this fixture.
                CardModel card = Assert.IsAssignableFrom<CardModel>(ActionExecutor.FindHandCardByOccurrence(
                    replayPlayer.PlayerCombatState.Hand.Cards, action.CardId, action.CardOccurrence));
                Assert.Equal(action.CardUpgradeLevel, card.CurrentUpgradeLevel);
                Creature? target = card.TargetType == TargetType.Self ? null
                    : ActionExecutor.ResolveTarget(replay, replayPlayer, card.TargetType, action.TargetIndex);
                Assert.NotNull(await replay.Engine.PlayCardWithResultAsync(replayPlayer, card, target));
            }
            replay.Engine.CheckWinCondition();
            Assert.Empty(choices.Pending);
        }
        Assert.Equal(!result.Terminal.PlayerDead && result.Terminal.AllEnemiesDead,
            replay.Engine!.Won && !replay.Engine.IsInProgress);

        BeamPrimarySearchIncumbent? kept = EarlyTurnExplorationRunner.SelectContinuationIncumbent(
            new BeamPrimarySearchIncumbent(2, 2, 0),
            preserveResources: false,
            selectedForcedUsesSatisfied: true,
            selectedHasExplicitPotionUses: false,
            prefixUsesPotion: false,
            selectedHasStrategicCredit: false,
            selectedHasRisk: false);
        Assert.NotNull(kept);
        Assert.Equal(int.MaxValue, kept!.Value.CombatEndedTurn);
        Assert.Null(EarlyTurnExplorationRunner.SelectContinuationIncumbent(
            new BeamPrimarySearchIncumbent(2, 2, 0),
            preserveResources: true,
            selectedForcedUsesSatisfied: true,
            selectedHasExplicitPotionUses: false,
            prefixUsesPotion: false,
            selectedHasStrategicCredit: false,
            selectedHasRisk: false));
    }

    [Fact]
    public async Task AdaptiveNoveltyRefinement_EvaluatesCleanlyOnSilentRoot()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL", addPowers: true);
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: true,
            PortfolioOptionsVersion: 2,
            UseNoveltyPortfolio: true,
            AdaptiveNoveltyRefinement: true,
            UseEarlyTurnExploration: false,
            SchedulingMode: PortfolioSchedulingMode.Sequential);
        var budget = new BeamBudget(12, 1500, 8, 6, 6, TimeSpan.FromSeconds(15));
        CombatPolicy policy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Disabled,
            StopAtAcceptableBattleHpLoss = false,
        };

        var ledger = new SearchWorkLedger(useParentBudget: true);
        PortfolioCoordinatorResult result = await BeamWidthPortfolioRunner.SolveAsync(
            root,
            budget,
            policy,
            portfolio,
            openingPreRootBattleHpLost: 0,
            watch: Stopwatch.StartNew(),
            ledger: ledger,
            tracker: null,
            ct: CancellationToken.None,
            maxDegreeOfParallelism: 1);

        Assert.NotNull(result.ChosenPass.Selected);
        Assert.True(ledger.ExpandedParents > 0);
        Assert.True(ledger.ExpandedParents <= budget.NodeLimit);
    }

    [Fact]
    public async Task InCombatRouteTracer_DoesNotMarkEscapedEnemyAsKilled()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Creature killedEnemy = root.Enemies[0];
        killedEnemy.SetCurrentHpInternal(2);
        uint killedId = killedEnemy.CombatId!.Value;

        var fleeingMonster = (FatGremlin)ModelDb.Monster<FatGremlin>().MutableClone();
        Creature fleeingEnemy = root.CreateCreature(fleeingMonster, CombatSide.Enemy, "flee");
        fleeingMonster.AssignCreature(fleeingEnemy);
        fleeingMonster.SetUpForCombat();
        root.AddCreature(fleeingEnemy);
        await fleeingMonster.AfterAddedToRoom();
        uint fleeingId = fleeingEnemy.CombatId!.Value;
        fleeingMonster.SetMoveImmediate(
            (MoveState)fleeingMonster.MoveStateMachine!.States["FLEE_MOVE"],
            forceTransition: true);

        Player player = root.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        var strike = (StrikeSilent)ModelDb.Card<StrikeSilent>().MutableClone();
        strike.AssignOwner(player);
        pcs.Hand.AddInternal(strike);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);

        static void ClearPile(CardPile pile)
        {
            while (pile.Cards.Count > 0)
                pile.RemoveInternal(pile.Cards[0]);
        }

        var playStrike = new PlannedAction(1, "PlayCard", strike.Id.Entry, 0, 0, null, -1, [], []);
        var endTurn = new PlannedAction(1, "EndTurn", null, 0, -1, null, -1, [], []);
        PlannedAction[] route = [playStrike, endTurn];

        CombatState sim = root.Clone();
        foreach (PlannedAction action in route)
        {
            await ActionExecutor.ExecuteAsync(sim, action, action.ChoiceIndices);
        }

        Assert.True(sim.SpawnedEnemies.First(e => e.CombatId == killedId).IsDead);
        Assert.Contains(sim.EscapedCreatures, e => e.CombatId == fleeingId && e.IsAlive);

        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        BeamSearchNode terminal = BeamSearchNode.Create(sim, null, endTurn, evaluator);

        InCombatBeamResult traced = InCombatRouteTracer.TraceRoute(
            root,
            route,
            terminal,
            terminal.State.Players[0].Creature.CurrentHp,
            onlyDeathRoutes: false,
            ForecastSearchBoundary.None,
            totalExpandedNodes: 2,
            priorBattleHpLost: 0,
            searchElapsedMilliseconds: 1d,
            evaluator: evaluator);

        Assert.Equal([killedId], traced.Actions[0].KilledEnemyCombatIds);
        Assert.Empty(traced.Actions[1].KilledEnemyCombatIds);
        Assert.DoesNotContain(fleeingId, traced.Actions[0].KilledEnemyCombatIds);
        Assert.DoesNotContain(fleeingId, traced.Actions[1].KilledEnemyCombatIds);
    }

    [Fact]
    public async Task ForecastWaveAndSerial_PartialNodeLimitAdmissionAndParity()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = root.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        pcs.Hand.AddInternal(CreateCard<Prepared>(player));
        pcs.Hand.AddInternal(CreateCard<StrikeSilent>(player));
        pcs.Hand.AddInternal(CreateCard<DefendSilent>(player));
        pcs.DrawPile.AddInternal(CreateCard<Footwork>(player));
        pcs.DrawPile.AddInternal(CreateCard<BladeDance>(player));

        CombatPolicy policy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Disabled,
            StopAtAcceptableBattleHpLoss = false,
            Act3BossStrategy = true,
        };
        var budget1 = new BeamBudget(8, 1, 6, 4, 4, TimeSpan.FromSeconds(10));
        var budget2 = budget1 with { NodeLimit = 2 };
        var opts = new BeamPassOptions
        {
            UseParentNodeBudget = true,
            UseTranspositionAdmission = true,
        };

        ForecastPassOutcome dop4Limit1 = await BeamSearch.RunSingleForecastPassAsync(
            root.Clone(), budget1, policy, CancellationToken.None, maxDegreeOfParallelism: 4, passOptions: opts);
        ForecastPassOutcome dop4Limit2 = await BeamSearch.RunSingleForecastPassAsync(
            root.Clone(), budget2, policy, CancellationToken.None, maxDegreeOfParallelism: 4, passOptions: opts);
        ForecastPassOutcome dop1Limit2 = await BeamSearch.RunSingleForecastPassAsync(
            root.Clone(), budget2, policy, CancellationToken.None, maxDegreeOfParallelism: 1, passOptions: opts);

        Assert.Equal(1, dop4Limit1.ExpandedParents);
        Assert.True(dop4Limit1.TransitionCount > 0);
        Assert.Equal(2, dop4Limit2.ExpandedParents);
        Assert.True(dop4Limit2.TransitionCount > dop4Limit1.TransitionCount);
        Assert.True(dop1Limit2.ExpandedParents > 0 && dop1Limit2.ExpandedParents <= 2);
        Assert.True(dop1Limit2.TransitionCount > 0);
        Assert.Equal(dop1Limit2.ExpandedParents, dop4Limit2.ExpandedParents);
        Assert.Equal(dop1Limit2.PassBoundary, dop4Limit2.PassBoundary);
    }

    [Fact]
    public async Task ForecastLanePool_DisposeUnblocksActiveGroupWithObjectDisposedException()
    {
        using var pool = new ForecastLanePool(2);
        using var entered = new ManualResetEventSlim(false);
        using var releaseWorker = new ManualResetEventSlim(false);
        ForecastWorkItem[] items = [new ForecastWorkItem { Kind = ForecastWorkItem.KindCustom }];

        Task disposeTask = Task.Run(() =>
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            pool.Dispose();
            releaseWorker.Set();
        });

        try
        {
            Assert.Throws<ObjectDisposedException>(() => pool.ExecuteGroup(
                items,
                0,
                (_, _) =>
                {
                    entered.Set();
                    releaseWorker.Wait(TimeSpan.FromSeconds(5));
                },
                ledger: null,
                CancellationToken.None));
        }
        finally
        {
            releaseWorker.Set();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ForecastLanePool_AnotherLaneCanRunOverflowActionsWhileOwnerIsBusy()
    {
        using var pool = new ForecastLanePool(2);
        using var peerStarted = new ManualResetEventSlim();
        using var actionsReady = new ManualResetEventSlim();
        using var lastActionRan = new ManualResetEventSlim();
        var seen = new int[2048];
        int owner = -1;
        pool.ExecuteGroup(
            [new ForecastWorkItem { ParentIndex = 0 }, new ForecastWorkItem { ParentIndex = 1 }],
            homeLaneOffset: 0,
            (lane, item) =>
            {
                if (item.Kind == ForecastWorkItem.KindAction)
                {
                    Interlocked.Increment(ref seen[item.ActionIndex]);
                    if (item.ActionIndex == seen.Length - 1 && lane != owner)
                        lastActionRan.Set();
                    return;
                }
                if (item.ParentIndex == 1)
                {
                    peerStarted.Set();
                    Assert.True(actionsReady.Wait(TimeSpan.FromSeconds(5)));
                    return;
                }

                Assert.True(peerStarted.Wait(TimeSpan.FromSeconds(5)));
                owner = lane;
                pool.ReservePending(item.Group, seen.Length);
                for (int i = 0; i < seen.Length; i++)
                {
                    pool.PushReserved(lane, new ForecastWorkItem
                    {
                        Group = item.Group,
                        Kind = ForecastWorkItem.KindAction,
                        ActionIndex = i,
                    }, shareOverflow: true);
                }
                actionsReady.Set();
                Assert.True(lastActionRan.Wait(TimeSpan.FromSeconds(5)));
            },
            ledger: null, CancellationToken.None);
        Assert.All(seen, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedForecastItemBecomesCollectibleWhileDequeIsAlive(bool steal)
    {
        var deque = new ForecastLanePool.WorkStealingDeque();
        WeakReference job = EnqueueAndTake(deque, steal);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        Assert.False(job.IsAlive);
        GC.KeepAlive(deque);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference EnqueueAndTake(ForecastLanePool.WorkStealingDeque deque, bool steal)
        {
            var item = new ForecastWorkItem();
            Assert.True(deque.TryPush(item));
            ForecastWorkItem? taken;
            Assert.True(steal ? deque.TrySteal(out taken) : deque.TryPop(out taken));
            Assert.Same(item, taken);
            return new WeakReference(item);
        }
    }

    [Fact]
    public async Task HealingCertificateAndStopConditions_EnforceSoundnessInvariants()
    {
        CombatState cleanRoot = await CreateSilentCombatAsync("NIBBITS_WEAK");
        (bool cleanCert, int cleanHeal) = HealingCertificateAnalyzer.Analyze(cleanRoot);
        Assert.True(cleanCert);
        Assert.Equal(0, cleanHeal);
        Assert.False(HealingCertificateAnalyzer.HasVisibleHealingSource(cleanRoot));

        CombatState feedState = cleanRoot.Clone();
        feedState.Players[0].PlayerCombatState!.Hand.AddInternal(CreateCard<Feed>(feedState.Players[0]));
        Assert.False(HealingCertificateAnalyzer.Analyze(feedState).CanCertify);
        Assert.True(HealingCertificateAnalyzer.HasVisibleHealingSource(feedState));

        CombatState alchemizeState = cleanRoot.Clone();
        alchemizeState.Players[0].PlayerCombatState!.Hand.AddInternal(CreateCard<Alchemize>(alchemizeState.Players[0]));
        Assert.False(HealingCertificateAnalyzer.Analyze(alchemizeState).CanCertify);

        CombatState chaosState = cleanRoot.Clone();
        var chaos = (PotionModel)ModelDb.Potion<DistilledChaos>().MutableClone();
        chaos.AssignOwner(chaosState.Players[0]);
        chaosState.Players[0].AddPotionInternal(chaos);
        Assert.False(HealingCertificateAnalyzer.Analyze(chaosState).CanCertify);

        CombatState firePotionRoot = cleanRoot.Clone();
        var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
        firePotion.AssignOwner(firePotionRoot.Players[0]);
        firePotionRoot.Players[0].AddPotionInternal(firePotion);
        HealingCertificateAssessment potionAssessment = HealingCertificateAnalyzer.Assess(firePotionRoot);
        Assert.False(potionAssessment.IsCertified);
        Assert.Equal("potion_present", potionAssessment.Reason);
        Assert.False(HealingCertificateAnalyzer.HasVisibleHealingSource(firePotionRoot));
        CombatPolicy stopPolicy = CombatPolicy.Default with { StopAtAcceptableBattleHpLoss = true };
        var potionEval = new StateEvaluator(firePotionRoot, stopPolicy);
        CombatState potionWon = firePotionRoot.Clone();
        foreach (Creature enemy in potionWon.HittableEnemies.ToArray())
            await CreatureCmd.Kill(enemy);
        Assert.True(potionWon.Engine!.CheckWinCondition());
        BeamSearchNode potionWonNode = BeamSearchNode.Create(potionWon, null, null, potionEval);
        var potionWinOutcome = new ForecastPassOutcome(
            potionWonNode,
            [new EvaluatedForecastCandidate(potionWonNode, ForecastSearchBoundary.None, potionWonNode.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            1,
            true,
            firePotionRoot.Players[0].Creature.CumulativeHpLost,
            potionEval);
        Assert.Null(BeamIncumbentPolicy.BuildRefinementIncumbent(stopPolicy, potionEval, potionWinOutcome));
        BeamSearchNode firePotionBranch = BeamSearchNode.Create(firePotionRoot.Clone(), null, null, potionEval);
        Assert.False(BeamIncumbentPolicy.ShouldPruneByIncumbent(
            firePotionBranch,
            new BeamPrimarySearchIncumbent(0, 1, 0),
            potionEval,
            stopPolicy));

        CombatState unreviewedCardRoot = cleanRoot.Clone();
        unreviewedCardRoot.Players[0].PlayerCombatState!.Hand.AddInternal(CreateCard<Footwork>(unreviewedCardRoot.Players[0]));
        HealingCertificateAssessment unreviewedCardAssessment = HealingCertificateAnalyzer.Assess(unreviewedCardRoot);
        Assert.False(unreviewedCardAssessment.IsCertified);
        Assert.Equal("unsupported_card", unreviewedCardAssessment.Reason);

        CombatState enchantedCardRoot = cleanRoot.Clone();
        CardModel cleanStrike = enchantedCardRoot.Players[0].PlayerCombatState!.Hand.Cards.First();
        await CardCmd.Enchant<Sts2Sim.Core.Models.Enchantments.Sharp>(cleanStrike, 2m);
        HealingCertificateAssessment enchantedAssessment = HealingCertificateAnalyzer.Assess(enchantedCardRoot);
        Assert.False(enchantedAssessment.IsCertified);
        Assert.Equal("card_enchantment", enchantedAssessment.Reason);

        CombatState afflictedCardRoot = cleanRoot.Clone();
        CardModel afflictedStrike = afflictedCardRoot.Players[0].PlayerCombatState!.Hand.Cards.First();
        await CardCmd.Afflict<Sts2Sim.Core.Models.Afflictions.Bound>(afflictedStrike, 1m);
        HealingCertificateAssessment afflictedAssessment = HealingCertificateAnalyzer.Assess(afflictedCardRoot);
        Assert.False(afflictedAssessment.IsCertified);
        Assert.Equal("card_affliction", afflictedAssessment.Reason);

        CombatState unreviewedRelicRoot = cleanRoot.Clone();
        var unreviewedRelic = (RelicModel)ModelDb.Relic<HappyFlower>().MutableClone();
        unreviewedRelic.AssignOwner(unreviewedRelicRoot.Players[0]);
        unreviewedRelicRoot.Players[0].AddRelicInternal(unreviewedRelic);
        HealingCertificateAssessment relicAssessment = HealingCertificateAnalyzer.Assess(unreviewedRelicRoot);
        Assert.False(relicAssessment.IsCertified);
        Assert.Equal("unsupported_relic", relicAssessment.Reason);

        CombatState unreviewedPowerRoot = cleanRoot.Clone();
        await PowerCmd.Apply<StrengthPower>(unreviewedPowerRoot, unreviewedPowerRoot.Players[0].Creature, 2m, unreviewedPowerRoot.Players[0].Creature, null);
        HealingCertificateAssessment powerAssessment = HealingCertificateAnalyzer.Assess(unreviewedPowerRoot);
        Assert.False(powerAssessment.IsCertified);
        Assert.Equal("unsupported_player_power", powerAssessment.Reason);

        CombatState tailState = cleanRoot.Clone();
        var tail = (RelicModel)ModelDb.Relic<LizardTail>().MutableClone();
        tail.AssignOwner(tailState.Players[0]);
        tailState.Players[0].AddRelicInternal(tail);
        Assert.False(HealingCertificateAnalyzer.Analyze(tailState).CanCertify);
        Assert.True(HealingCertificateAnalyzer.HasVisibleHealingSource(tailState));

        var cleanEval = new StateEvaluator(cleanRoot, stopPolicy);
        CombatState maxHpGained = cleanRoot.Clone();
        maxHpGained.Players[0].Creature.SetMaxHpInternal(maxHpGained.Players[0].Creature.MaxHp + 5);
        BeamSearchNode maxHpNode = BeamSearchNode.Create(maxHpGained, null, null, cleanEval);
        Assert.Equal(int.MaxValue, cleanEval.StrategicHpLowerBound(maxHpNode));

        CombatState damagedWon = cleanRoot.Clone();
        Player damagedPlayer = damagedWon.Players[0];
        damagedPlayer.Creature.SetCurrentHpInternal(damagedPlayer.Creature.MaxHp - 5);
        foreach (Creature enemy in damagedWon.HittableEnemies.ToArray())
            await CreatureCmd.Kill(enemy);
        Assert.True(damagedWon.Engine!.CheckWinCondition());
        BeamSearchNode damagedWonNode = BeamSearchNode.Create(damagedWon, null, null, cleanEval);
        Assert.False(cleanEval.IsProvenZeroDamageRoute(damagedWonNode));

        CombatState fullWon = cleanRoot.Clone();
        foreach (Creature enemy in fullWon.HittableEnemies.ToArray())
            await CreatureCmd.Kill(enemy);
        Assert.True(fullWon.Engine!.CheckWinCondition());
        BeamSearchNode fullWonNode = BeamSearchNode.Create(fullWon, null, null, cleanEval);
        Assert.True(cleanEval.IsProvenZeroDamageRoute(fullWonNode));

        var growthEval = new StateEvaluator(
            cleanRoot,
            stopPolicy with { GrowthBudgets = new GrowthBudgets { GeneticAlgorithm = 1 } });
        BeamSearchNode growthWonNode = BeamSearchNode.Create(fullWon.Clone(), null, null, growthEval);
        Assert.False(growthEval.IsProvenZeroDamageRoute(growthWonNode));

        var fullOutcome = new ForecastPassOutcome(
            fullWonNode,
            [new EvaluatedForecastCandidate(fullWonNode, ForecastSearchBoundary.None, fullWonNode.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            1,
            true,
            cleanRoot.Players[0].Creature.CumulativeHpLost,
            cleanEval);
        Assert.True(BeamIncumbentPolicy.HasReachedAcceptableBattleHpLoss(stopPolicy, cleanEval, fullOutcome, 0));
        Assert.False(BeamIncumbentPolicy.CanFinishTargetPortfolio(stopPolicy, cleanEval, fullOutcome, 0, stopPortfolioAtHpTarget: false));
        Assert.True(BeamIncumbentPolicy.CanFinishTargetPortfolio(stopPolicy, cleanEval, fullOutcome, 0, stopPortfolioAtHpTarget: true));
        Assert.True(BeamIncumbentPolicy.CanStopEarlyTurnExploration(stopPolicy, cleanEval, fullOutcome, 0));
        Assert.False(BeamIncumbentPolicy.CanStopEarlyTurnExploration(
            stopPolicy,
            cleanEval,
            fullOutcome with { PassBoundary = ForecastSearchBoundary.NodeLimit },
            0));

        CombatState regenRoot = cleanRoot.Clone();
        await PowerCmd.Apply<RegenPower>(regenRoot, regenRoot.Players[0].Creature, 3m, regenRoot.Players[0].Creature, null);
        var regenEval = new StateEvaluator(regenRoot, stopPolicy);
        CombatState regenWon = regenRoot.Clone();
        foreach (Creature enemy in regenWon.HittableEnemies.ToArray())
            await CreatureCmd.Kill(enemy);
        Assert.True(regenWon.Engine!.CheckWinCondition());
        BeamSearchNode regenWonNode = BeamSearchNode.Create(regenWon, null, null, regenEval);
        var regenOutcome = new ForecastPassOutcome(
            regenWonNode,
            [new EvaluatedForecastCandidate(regenWonNode, ForecastSearchBoundary.None, regenWonNode.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            1,
            true,
            regenRoot.Players[0].Creature.CumulativeHpLost,
            regenEval);
        Assert.False(BeamIncumbentPolicy.HasReachedAcceptableBattleHpLoss(stopPolicy, regenEval, regenOutcome, 0));

        CombatState partialHpStart = cleanRoot.Clone();
        partialHpStart.Players[0].Creature.SetCurrentHpInternal(partialHpStart.Players[0].Creature.MaxHp - 10);
        var partialEval = new StateEvaluator(partialHpStart, stopPolicy);
        CombatState recoveredWon = partialHpStart.Clone();
        recoveredWon.Players[0].Creature.SetCurrentHpInternal(recoveredWon.Players[0].Creature.MaxHp - 6);
        foreach (Creature enemy in recoveredWon.HittableEnemies.ToArray())
            await CreatureCmd.Kill(enemy);
        Assert.True(recoveredWon.Engine!.CheckWinCondition());
        BeamSearchNode recoveredWonNode = BeamSearchNode.Create(recoveredWon, null, null, partialEval);
        var recoveredOutcome = new ForecastPassOutcome(
            recoveredWonNode,
            [new EvaluatedForecastCandidate(recoveredWonNode, ForecastSearchBoundary.None, recoveredWonNode.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            1,
            true,
            partialHpStart.Players[0].Creature.CumulativeHpLost,
            partialEval);
        Assert.False(BeamIncumbentPolicy.HasReachedAcceptableBattleHpLoss(stopPolicy, partialEval, recoveredOutcome, 0));
    }

    [Fact]
    public async Task PortfolioCoordination_EdgeCasesAndOverflowSafety()
    {
        IReadOnlyList<BeamWidthPortfolioMemberSpec> hugeMembers = BeamWidthPortfolio.ProductionMembers(int.MaxValue / 2, null);
        Assert.All(hugeMembers, m => Assert.True(m.BeamWidth > 0));
        Assert.True(PowerCommitmentSeatPolicy.SeatQuota(int.MaxValue, aggressive: true) > 0);
        Assert.True(PowerCommitmentSeatPolicy.SeatQuota(int.MaxValue, aggressive: false) > 0);

        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        var rootEval = new StateEvaluator(root, CombatPolicy.Default);
        BeamSearchNode rootNode = BeamSearchNode.Create(root.Clone(), null, null, rootEval);
        List<BeamSearchNode> hugeWidthRetained = Retention.SelectWithKey(
            [rootNode],
            int.MaxValue,
            n => n.Fingerprint,
            includeParetoProgress: true,
            aggressivePowerCommitment: true);
        Assert.Single(hugeWidthRetained);

        var ledger = new SearchWorkLedger(useParentBudget: true);
        using (ledger.EnterPhase("HybridNovelty"))
        {
            using (ledger.EnterPhase("Beam"))
            {
                Assert.Equal("HybridNovelty+Beam", ledger.ActivePhaseLabel);
            }
            Assert.Equal("HybridNovelty+Beam", ledger.ActivePhaseLabel);
        }
        Assert.Equal("Beam", ledger.ActivePhaseLabel);

        var emitted = new List<InCombatProgressUpdate>();
        using var cancelledSearch = new CancellationTokenSource();
        cancelledSearch.Cancel();
        var tracker = new InCombatSearchTracker(
            root,
            0,
            Stopwatch.StartNew(),
            update => emitted.Add(update),
            searchCancellation: cancelledSearch.Token,
            ledger: ledger,
            emitExtendedMetrics: true);
        ledger.AddExpandedParents(3);
        ledger.AddTransitions(7);
        ledger.AddChoiceReplayAttempts(9);
        tracker.StopAndJoin(emitFinalMetricsFlush: true);
        InCombatProgressUpdate flush = Assert.Single(emitted);
        Assert.True(flush.IsFinalMetricsFlush);
        Assert.Null(flush.CurrentBest);
        Assert.Equal(3L, flush.ExpandedParents);
        Assert.Equal(7L, flush.TransitionCount);
        Assert.Equal(9L, flush.ChoiceReplayAttempts);

        var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
        firePotion.AssignOwner(root.Players[0]);
        root.Players[0].AddPotionInternal(firePotion);
        CombatPolicy requirePotionPolicy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.RequireAtLeastOne,
            StopAtAcceptableBattleHpLoss = false,
        };
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: true,
            PortfolioOptionsVersion: 2,
            UseEarlyTurnExploration: true,
            EarlyTurnExplorationDepth: 1,
            EarlyTurnExplorationBudgetMilliseconds: 60_000,
            SchedulingMode: PortfolioSchedulingMode.Sequential);
        var tightBudget = new BeamBudget(8, 2, 6, 4, 4, TimeSpan.FromSeconds(10));
        PortfolioCoordinatorResult portResult = await BeamWidthPortfolioRunner.SolveAsync(
            root,
            tightBudget,
            requirePotionPolicy,
            portfolio,
            0,
            Stopwatch.StartNew(),
            new SearchWorkLedger(useParentBudget: true),
            tracker: null,
            CancellationToken.None,
            maxDegreeOfParallelism: 2);
        Assert.NotNull(portResult.ChosenPass.Selected);
        Assert.True(portResult.TotalExpandedNodes > 0);
    }

    [Theory]
    [InlineData(CombatSolver.Beam.Policy.BossHpStrategy.ProgressionFirst)]
    [InlineData(CombatSolver.Beam.Policy.BossHpStrategy.MinimizeHpLoss)]
    public async Task NoveltyBossBudget_UsesEncounterRegardlessOfHpPreference(
        CombatSolver.Beam.Policy.BossHpStrategy preference)
    {
        CombatState root = await CreateAct0BossRootAsync();
        CombatPolicy policy = CombatPolicy.Default with { ActTransitionBossHpStrategy = preference };
        var budget = new BeamBudget(32, 2000, 10, 10, 10, TimeSpan.FromSeconds(8));
        var portfolio = new PreCombatPortfolioOptions(UseBeamWidthPortfolio: false, UseNoveltyPortfolio: true);
        Assert.Equal(preference == CombatSolver.Beam.Policy.BossHpStrategy.MinimizeHpLoss,
            PolicyRootOperands.Capture(root, policy).BossHpRelief == CombatSolver.Beam.Policy.BossHpRelief.None);
        bool isBoss = NoveltySearchRunner.IsActEndingBoss(root);
        Assert.Equal(TimeSpan.FromSeconds(2), NoveltySearchRunner.ComputeExplorationBudget(budget, portfolio, isBoss)!.TimeLimit);
    }

    [Theory]
    [InlineData(true, -12_000d)]
    [InlineData(false, 8_000d)]
    public async Task BeamRanking_UsesEffectivePolarityForNegativeCounter(bool onPlayer, double perStack)
    {
        CombatState state = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        var player = state.Players[0].Creature;
        Creature owner = onPlayer ? player : state.Enemies[0];
        foreach (Creature enemy in state.Enemies)
            typeof(MonsterModel).GetProperty(nameof(MonsterModel.NextMove))!.SetValue(
                enemy.Monster, new MoveState("IDLE", _ => Task.CompletedTask));
        var evaluator = new StateEvaluator(state, CombatPolicy.Default);
        double before = evaluator.Score(state, 0, 0, 0, default, 0, 0);
        await PowerCmd.Apply<StrengthPower>(state, owner, -3, player, null);
        Assert.Equal(before + 3 * perStack, evaluator.Score(state, 0, 0, 0, default, 0, 0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-3, true)]
    [InlineData(6, true)]
    public async Task BeamRanking_ProjectsModifiedDamageForEachAttackHit(int strength, bool weak)
    {
        CombatState state = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Creature player = state.Players[0].Creature;
        Creature enemy = state.Enemies[0];
        foreach (Creature other in state.Enemies.Skip(1).ToArray())
            await CreatureCmd.Kill(other);
        if (strength != 0)
            await PowerCmd.Apply<StrengthPower>(state, enemy, strength, player, null);
        if (weak)
            await PowerCmd.Apply<WeakPower>(state, enemy, 1, player, null);
        const int damage = 10, hits = 2;
        typeof(MonsterModel).GetProperty(nameof(MonsterModel.NextMove))!.SetValue(
            enemy.Monster, new MoveState("MULTI", _ => Task.CompletedTask, new MultiAttackIntent(damage, hits)));
        var evaluator = new StateEvaluator(state, CombatPolicy.Default);
        BeamSearchNode node = BeamSearchNode.Create(state, null, null, evaluator);
        for (int i = 0; i < hits; i++)
            await CreatureCmd.Damage(state, [player], damage, ValueProp.Move, enemy, null, null);
        Assert.Equal(player.CurrentHp, node.ProjectedHp);
        Assert.Equal(player.CurrentHp, node.UnblockedHpMargin);
    }

    [Fact]
    public async Task NoveltySearchAndFeatures_BoundedWitnessesEscapeOrderAndCoreFacts()
    {
        CombatState state = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = state.Players[0];
        Creature enemy = state.Enemies[0];
        enemy.SetMaxHpInternal(100);
        enemy.SetCurrentHpInternal(40);
        await OstyCmd.Summon(player, 9m, null);
        Assert.NotNull(player.Osty);
        await PowerCmd.Apply<StrengthPower>(state, player.Osty!, 4m, player.Osty, null);
        await PowerCmd.Apply<StrengthPower>(state, player.Creature, 1500m, player.Creature, null);
        await PowerCmd.Apply<DoomPower>(state, enemy, 10m, player.Creature, null);

        var evaluator = new StateEvaluator(state, CombatPolicy.Default);
        BeamSearchNode node = BeamSearchNode.Create(state.Clone(), null, null, evaluator);
        Assert.True(node.DelayedDamageValue > 0);

        var facts = new List<BfwsFact>(64);
        var counts = new Dictionary<(string Id, int Upgrade), int>(16);
        var order = new List<(string Id, int Upgrade)>(16);
        CoreNoveltyFeatures.CaptureNoveltyFacts(node, CombatPolicy.Default, facts, counts, order);
        Assert.Contains(facts, f => f.Kind == BfwsFactKind.Power && f.Slot == 0 && f.Key == "STRENGTH_POWER" && f.Value == 1500);
        Assert.Contains(facts, f => f.Kind == BfwsFactKind.Power && f.Slot == 1 && f.Key == "STRENGTH_POWER" && f.Value == 4);

        CombatState noveltyRoot = await CreateSilentCombatAsync("NIBBITS_NORMAL", addPowers: true);
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: false,
            PortfolioOptionsVersion: 2,
            UseNoveltyPortfolio: true,
            NoveltyFamiliarAllowance: 1,
            NoveltyMaxOpen: 4);
        var budget = new BeamBudget(8, 120, 6, 4, 4, TimeSpan.FromSeconds(10));
        var ledger = new SearchWorkLedger(useParentBudget: true);
        ForecastPassOutcome? noveltyOutcome = NoveltySearchRunner.RunNoveltyPassOrNull(
            noveltyRoot,
            budget,
            CombatPolicy.Default with { PotionPolicy = BeamPotionPolicy.Disabled, StopAtAcceptableBattleHpLoss = false },
            portfolio,
            new BeamPassOptions
            {
                UseParentNodeBudget = true,
                WorkLedger = ledger,
                AllowPolicyUnsatisfiedSelection = true,
            },
            track: null,
            CancellationToken.None);
        Assert.NotNull(noveltyOutcome);
        Assert.True(noveltyOutcome!.ExpandedParents > 0);
        Assert.True(noveltyOutcome.TransitionCount > 0);
        Assert.Contains(noveltyOutcome.EvaluatedCandidates, c => c.Node.State.Players[0].Creature.IsAlive && c.ProjectedPlayerHp > 0);

        // P2 #12: ThornsPower 只计入 StrategicDamagePotential，ReactiveDamageValue 仅源自 SleightOfFleshPower。
        CombatState thornsRoot = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        await PowerCmd.Apply<ThornsPower>(thornsRoot, thornsRoot.Players[0].Creature, 6m, thornsRoot.Players[0].Creature, null);
        NodeEvalMetrics thornsMetrics = CoreNoveltyFeatures.ComputeNodeMetrics(thornsRoot, CombatPolicy.Default, thornsRoot.Players[0].Creature.CurrentHp, 0);
        Assert.Equal(0, thornsMetrics.ReactiveDamageValue);
        await PowerCmd.Apply<SleightOfFleshPower>(thornsRoot, thornsRoot.Players[0].Creature, 5m, thornsRoot.Players[0].Creature, null);
        NodeEvalMetrics sleightMetrics = CoreNoveltyFeatures.ComputeNodeMetrics(thornsRoot, CombatPolicy.Default, thornsRoot.Players[0].Creature.CurrentHp, 0);
        Assert.Equal(5, sleightMetrics.ReactiveDamageValue);

        // P2 #13: 未使用 PaelsEye 时出牌记为 DeclinedExtraTurn（EndTurn 不记），非纯出牌（含抽牌与选卡）记为 Control。
        CombatState traitRoot = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player traitPlayer = traitRoot.Players[0];
        PlayerCombatState traitPcs = traitPlayer.PlayerCombatState!;
        ClearPile(traitPcs.Hand);
        ClearPile(traitPcs.DrawPile);
        ClearPile(traitPcs.DiscardPile);
        traitPcs.Hand.AddInternal(CreateCard<StrikeSilent>(traitPlayer));
        traitPcs.Hand.AddInternal(CreateCard<DaggerThrow>(traitPlayer));
        traitPcs.DrawPile.AddInternal(CreateCard<DefendSilent>(traitPlayer));
        var eye = (RelicModel)ModelDb.Relic<PaelsEye>().MutableClone();
        eye.AssignOwner(traitPlayer);
        traitPlayer.AddRelicInternal(eye);
        traitRoot.Enemies[0].SetCurrentHpInternal(40);
        var traitEval = new StateEvaluator(traitRoot, CombatPolicy.Default);
        BeamSearchNode traitRootNode = BeamSearchNode.Create(traitRoot.Clone(), null, null, traitEval);

        var strikePlay = new PlannedAction(1, "PlayCard", "STRIKE_SILENT", 0, 0, null, -1, [], []);
        CombatState afterStrike = traitRoot.Clone();
        var strikeSink = new PredictionLedgerSink();
        afterStrike.PredictionSink = strikeSink;
        await ActionExecutor.ExecuteAsync(afterStrike, strikePlay, []);
        BeamSearchNode strikeNode = BeamSearchNode.Create(
            afterStrike, traitRootNode, strikePlay, traitEval,
            playedCards: strikeSink.PlayedCards,
            hasImpureHistory: strikeSink.PotionsProcured > 0);
        Assert.True(strikeNode.RouteTraits.HasFlag(CombatSolver.Beam.Novelty.SearchRouteTraits.DeclinedExtraTurn));
        Assert.False(strikeNode.RouteTraits.HasFlag(CombatSolver.Beam.Novelty.SearchRouteTraits.Control));

        var endTurnPlay = new PlannedAction(1, "EndTurn", null, 0, -1, null, -1, [], []);
        CombatState afterEndTurn = traitRoot.Clone();
        await ActionExecutor.ExecuteAsync(afterEndTurn, endTurnPlay, []);
        BeamSearchNode endTurnNode = BeamSearchNode.Create(afterEndTurn, traitRootNode, endTurnPlay, traitEval);
        Assert.False(endTurnNode.RouteTraits.HasFlag(CombatSolver.Beam.Novelty.SearchRouteTraits.DeclinedExtraTurn));

        var daggerPlay = new PlannedAction(1, "PlayCard", "DAGGER_THROW", 0, 0, null, -1, [[0]], ["STRIKE_SILENT"]);
        CombatState afterDagger = traitRoot.Clone();
        var daggerSink = new PredictionLedgerSink();
        afterDagger.PredictionSink = daggerSink;
        await ActionExecutor.ExecuteAsync(afterDagger, daggerPlay, daggerPlay.ChoiceIndices);
        BeamSearchNode daggerNode = BeamSearchNode.Create(
            afterDagger, traitRootNode, daggerPlay, traitEval,
            playedCards: daggerSink.PlayedCards,
            hasImpureHistory: daggerSink.PotionsProcured > 0);
        Assert.True(daggerNode.RouteTraits.HasFlag(CombatSolver.Beam.Novelty.SearchRouteTraits.Control));

        // P2 #14: NoveltyMaxTurns = int.MaxValue 时地平线计算不溢出回绕。
        ForecastPassOutcome? maxTurnsOutcome = NoveltySearchRunner.RunNoveltyPassOrNull(
            noveltyRoot,
            new BeamBudget(8, 12, 6, 4, 4, TimeSpan.FromSeconds(10)),
            CombatPolicy.Default with { PotionPolicy = BeamPotionPolicy.Disabled, StopAtAcceptableBattleHpLoss = false },
            portfolio with { NoveltyMaxTurns = int.MaxValue },
            new BeamPassOptions
            {
                UseParentNodeBudget = true,
                AllowPolicyUnsatisfiedSelection = true,
            },
            track: null,
            CancellationToken.None);
        Assert.NotNull(maxTurnsOutcome);
        Assert.True(maxTurnsOutcome!.ExpandedParents > 0);
    }

    [Fact]
    public async Task PowerCommitmentAutoplayAndRouteTracerTurnEndTiming()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = root.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);

        await PowerCmd.Apply<StampedePower>(root, player.Creature, 1m, player.Creature, null);
        pcs.Hand.AddInternal(CreateCard<VoidForm>(player));
        pcs.Hand.AddInternal(CreateCard<DaggerThrow>(player));
        pcs.Hand.AddInternal(CreateCard<Footwork>(player));
        pcs.DrawPile.AddInternal(CreateCard<StrikeSilent>(player));
        pcs.DiscardPile.AddInternal(CreateCard<DefendSilent>(player));
        pcs.GainEnergy(3);
        root.Enemies[0].SetCurrentHpInternal(40);

        var ledger = new PredictionLedgerSink();
        CombatState autoPlaySim = root.Clone();
        autoPlaySim.PredictionSink = ledger;
        CardModel footwork = autoPlaySim.Players[0].PlayerCombatState!.Hand.Cards.First(c => c is Footwork);
        await AutoPlayCmd.FromCards(autoPlaySim, autoPlaySim.Players[0], [footwork]);
        Assert.NotNull(ledger.PlayedCards);
        Assert.Contains(ledger.PlayedCards!, p => p.CardId == "FOOTWORK" && p.IsAutoPlay);

        var eval = new StateEvaluator(root, CombatPolicy.Default);
        BeamSearchNode parentNode = BeamSearchNode.Create(root.Clone(), null, null, eval);
        BeamSearchNode autoChild = BeamSearchNode.Create(autoPlaySim, parentNode, null, eval, playedCards: ledger.PlayedCards);
        Assert.NotNull(autoChild.PowerCommitment);
        Assert.Equal(0, autoChild.PowerCommitment!.Investment);

        List<BeamSearchNode> nonAggressiveRetained = Retention.SelectWithKey(
            [parentNode, autoChild],
            width: 8,
            n => n.Fingerprint,
            includeParetoProgress: true,
            aggressivePowerCommitment: false,
            enablePowerCommitmentSeats: true);
        Assert.Contains(autoChild, nonAggressiveRetained);

        var playVoidForm = new PlannedAction(1, "PlayCard", "VOID_FORM", 0, -1, null, -1, [[1]], ["STRIKE_SILENT"]);
        CombatState traceSim = root.Clone();
        await ActionExecutor.ExecuteAsync(traceSim, playVoidForm, playVoidForm.ChoiceIndices);
        BeamSearchNode tracedTerminal = BeamSearchNode.Create(traceSim, null, playVoidForm, eval);
        InCombatBeamResult traced = InCombatRouteTracer.TraceRoute(
            root,
            [playVoidForm],
            tracedTerminal,
            tracedTerminal.State.Players[0].Creature.CurrentHp,
            onlyDeathRoutes: false,
            ForecastSearchBoundary.None,
            totalExpandedNodes: 1,
            priorBattleHpLost: 0,
            searchElapsedMilliseconds: 1d,
            evaluator: eval);
        Assert.Single(traced.Actions);
        Assert.Contains(traced.Actions[0].Choices, ch => ch.Timing == "PlayerTurnEnd" && ch.SourceId == "DAGGER_THROW");
    }

    [Fact]
    public async Task EarlyTurnExploration_ParallelWaiterCancellationAndLocalDeadline_NeverFailAndReturnIncumbent()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = root.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        pcs.Hand.AddInternal(CreateCard<Footwork>(player));
        pcs.Hand.AddInternal(CreateCard<Neutralize>(player));
        pcs.Hand.AddInternal(CreateCard<DefendSilent>(player));
        pcs.Hand.AddInternal(CreateCard<StrikeSilent>(player));
        pcs.DrawPile.AddInternal(CreateCard<StrikeSilent>(player));
        pcs.DrawPile.AddInternal(CreateCard<StrikeSilent>(player));
        pcs.DrawPile.AddInternal(CreateCard<StrikeSilent>(player));
        root.Enemies[0].SetCurrentHpInternal(9);

        CombatPolicy stopPolicy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Disabled,
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 100,
        };
        var rootEval = new StateEvaluator(root, stopPolicy);
        BeamSearchNode rootNode = BeamSearchNode.Create(root.Clone(), null, null, rootEval);
        var incompleteIncumbent = new ForecastPassOutcome(
            rootNode,
            [new EvaluatedForecastCandidate(rootNode, ForecastSearchBoundary.NodeLimit, rootNode.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.NodeLimit,
            1,
            false,
            root.Players[0].Creature.CumulativeHpLost,
            rootEval);

        var budget = new BeamBudget(8, 250, 6, 4, 4, TimeSpan.FromSeconds(10));
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: true,
            PortfolioOptionsVersion: 2,
            UseEarlyTurnExploration: true,
            EarlyTurnExplorationDepth: 1,
            EarlyTurnExplorationBudgetMilliseconds: 60_000,
            SchedulingMode: PortfolioSchedulingMode.Parallel);

        using var sharedPool = new ForecastLanePool(2);
        ForecastPassOutcome parallelOutcome = await EarlyTurnExplorationRunner.RunAsync(
            root,
            budget,
            stopPolicy,
            portfolio,
            new BeamPassOptions { UseParentNodeBudget = true, UseTranspositionAdmission = true },
            incompleteIncumbent,
            Stopwatch.StartNew(),
            new SearchWorkLedger(useParentBudget: true),
            sharedPool,
            tracker: null,
            CancellationToken.None);
        Assert.True(parallelOutcome.Selected.Won);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CombatState endedState = root.Clone();
        var endTurnAction = new PlannedAction(1, "EndTurn", null, 0, -1, null, -1, [], []);
        await ActionExecutor.ExecuteAsync(endedState, endTurnAction, []);
        BeamSearchNode turn2Frontier = BeamSearchNode.Create(endedState, rootNode, endTurnAction, rootEval);
        Assert.Throws<OperationCanceledException>(() =>
            EarlyTurnFrontierSelector.Select([turn2Frontier], completedTurns: 1, rootTurnNumber: 1, stopPolicy, cancelled.Token));

        var tinyWatch = Stopwatch.StartNew();
        ForecastPassOutcome expiredOutcome = await EarlyTurnExplorationRunner.RunAsync(
            root,
            budget,
            stopPolicy,
            portfolio with { EarlyTurnExplorationBudgetMilliseconds = 1, EarlyTurnExplorationDepth = 2 },
            new BeamPassOptions { UseParentNodeBudget = true, UseTranspositionAdmission = true },
            incompleteIncumbent,
            tinyWatch,
            new SearchWorkLedger(useParentBudget: true),
            sharedPool,
            tracker: null,
            CancellationToken.None,
            minRemainingReserveMs: 0L);
        Assert.NotNull(expiredOutcome);
    }

    [Fact]
    public async Task SmartPotionUnfinishedWitnesses_NoveltyAndV2PortfolioComparison_RetainSharedPotionFreeBaseline()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = root.Players[0];
        var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
        firePotion.AssignOwner(player);
        player.AddPotionInternal(firePotion);
        root.Enemies[0].SetCurrentHpInternal(120);

        CombatPolicy smartPolicy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Smart,
            PredictPotionReward = false,
            IgnoreLongTermRewards = false,
            StopAtAcceptableBattleHpLoss = false,
        };
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: false,
            PortfolioOptionsVersion: 2,
            UseNoveltyPortfolio: true,
            NoveltyFamiliarAllowance: 1,
            NoveltyMaxOpen: 6);
        ForecastPassOutcome? noveltyOutcome = NoveltySearchRunner.RunNoveltyPassOrNull(
            root,
            new BeamBudget(8, 12, 6, 4, 4, TimeSpan.FromSeconds(10)),
            smartPolicy,
            portfolio,
            new BeamPassOptions
            {
                UseParentNodeBudget = true,
                AllowPolicyUnsatisfiedSelection = true,
            },
            track: null,
            CancellationToken.None);
        Assert.NotNull(noveltyOutcome);

        var baselineEval = new StateEvaluator(root, smartPolicy, aggressivePowerCommitment: false);
        var powerEval = new StateEvaluator(root, smartPolicy, aggressivePowerCommitment: true);
        BeamSearchNode rootNode = BeamSearchNode.Create(root.Clone(), null, null, baselineEval);
        var usePotion = new PlannedAction(1, "UsePotion", null, 0, 0, "FIRE_POTION", 0, [], []);

        async Task<BeamSearchNode> CreateDamagedNodeAsync(int damage, bool withPotion, StateEvaluator eval)
        {
            CombatState state = root.Clone();
            if (withPotion)
                await ActionExecutor.ExecuteAsync(state, usePotion, []);
            await CreatureCmd.Damage(
                state,
                [state.Players[0].Creature],
                damage,
                ValueProp.Unblockable | ValueProp.Unpowered,
                null,
                null,
                null);
            return BeamSearchNode.Create(state, rootNode, withPotion ? usePotion : null, eval);
        }

        BeamSearchNode p0Free = await CreateDamagedNodeAsync(20, withPotion: false, baselineEval);
        BeamSearchNode p0Opt = await CreateDamagedNodeAsync(6, withPotion: true, baselineEval);
        BeamSearchNode p1Free = await CreateDamagedNodeAsync(18, withPotion: false, powerEval);
        BeamSearchNode p1Opt = await CreateDamagedNodeAsync(2, withPotion: true, powerEval);

        var pass0 = new ForecastPassOutcome(
            p0Opt,
            [
                new EvaluatedForecastCandidate(p0Opt, ForecastSearchBoundary.NodeLimit, p0Opt.State.Players[0].Creature.CurrentHp),
                new EvaluatedForecastCandidate(p0Free, ForecastSearchBoundary.NodeLimit, p0Free.State.Players[0].Creature.CurrentHp),
            ],
            ForecastSearchBoundary.NodeLimit,
            ExpandedNodes: 10,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            baselineEval);
        var pass1 = new ForecastPassOutcome(
            p1Opt,
            [
                new EvaluatedForecastCandidate(p1Opt, ForecastSearchBoundary.NodeLimit, p1Opt.State.Players[0].Creature.CurrentHp),
                new EvaluatedForecastCandidate(p1Free, ForecastSearchBoundary.NodeLimit, p1Free.State.Players[0].Creature.CurrentHp),
            ],
            ForecastSearchBoundary.NodeLimit,
            ExpandedNodes: 12,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            powerEval);

        int initialHp = root.Players[0].Creature.CurrentHp;
        Assert.Throws<InvalidOperationException>(() =>
            PlanSelector.Select([p0Opt, p1Opt], initialHp, baselineEval));
        Assert.True(BeamIncumbentPolicy.IsBetterPassOutcome(pass1, pass0, initialHp, baselineEval));
        ForecastPassOutcome chosen = BeamIncumbentPolicy.ChooseBetterPassOutcome(pass0, pass1, initialHp, baselineEval);
        Assert.Same(p1Opt, chosen.Selected);
    }

    [Fact]
    public async Task ForecastLanePool_EnforcesPerGroupMaxConcurrencyAndHomeInboxFairness()
    {
        using var pool = new ForecastLanePool(3);
        int activeA = 0;
        int peakA = 0;
        int executedA = 0;
        int activeB = 0;
        int peakB = 0;
        int executedB = 0;
        int enteredASlots = 0;
        var bothASlotsOccupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseA = new ManualResetEventSlim(false);
        int spawnedA = 0;

        var itemsA = Enumerable.Range(0, 6).Select(i => new ForecastWorkItem { Kind = ForecastWorkItem.KindCustom, ParentIndex = i }).ToArray();
        var itemsB = Enumerable.Range(0, 4).Select(i => new ForecastWorkItem { Kind = ForecastWorkItem.KindCustom, ParentIndex = i }).ToArray();

        Task taskA = Task.Run(() =>
        {
            pool.ExecuteGroup(
                itemsA,
                homeLaneOffset: 0,
                (lane, item) =>
                {
                    Interlocked.Increment(ref executedA);
                    int cur = Interlocked.Increment(ref activeA);
                    while (true)
                    {
                        int prev = Volatile.Read(ref peakA);
                        if (cur <= prev || Interlocked.CompareExchange(ref peakA, cur, prev) == prev)
                            break;
                    }
                    try
                    {
                        if (item.Kind == ForecastWorkItem.KindCustom && Interlocked.Exchange(ref spawnedA, 1) == 0)
                        {
                            for (int c = 0; c < 6; c++)
                            {
                                pool.PushChild(lane, new ForecastWorkItem
                                {
                                    Group = item.Group,
                                    Kind = ForecastWorkItem.KindChoice,
                                    ParentIndex = c,
                                });
                            }
                        }
                        int slotNum = Interlocked.Increment(ref enteredASlots);
                        if (slotNum <= 2)
                        {
                            if (slotNum == 2)
                                bothASlotsOccupied.TrySetResult();
                            releaseA.Wait(TimeSpan.FromSeconds(5));
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeA);
                    }
                },
                ledger: null,
                CancellationToken.None,
                maxConcurrency: 2);
        });

        await bothASlotsOccupied.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task taskB = Task.Run(() =>
        {
            pool.ExecuteGroup(
                itemsB,
                homeLaneOffset: 2,
                (_, _) =>
                {
                    Interlocked.Increment(ref executedB);
                    int cur = Interlocked.Increment(ref activeB);
                    while (true)
                    {
                        int prev = Volatile.Read(ref peakB);
                        if (cur <= prev || Interlocked.CompareExchange(ref peakB, cur, prev) == prev)
                            break;
                    }
                    try
                    {
                        Thread.Sleep(5);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeB);
                    }
                },
                ledger: null,
                CancellationToken.None,
                maxConcurrency: 1);
        });

        try
        {
            await taskB.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseA.Set();
        }
        await taskA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(Volatile.Read(ref peakA), 1, 2);
        Assert.Equal(1, Volatile.Read(ref peakB));
        Assert.Equal(itemsA.Length + 6, Volatile.Read(ref executedA));
        Assert.Equal(itemsB.Length, Volatile.Read(ref executedB));
    }

    [Fact]
    public async Task HybridNoveltyBudgetAndPhaseLifetime_ReservesScoutExclusivelyAndTransitionsPhaseOnExit()
    {
        var ledger = new SearchWorkLedger(useParentBudget: true);
        using (ledger.EnterPhase("Beam"))
        {
            using (ledger.EnterPhase("PortfolioRefinement"))
            {
                using (ledger.EnterPhase("NoveltyScout"))
                {
                    Assert.Equal("HybridNovelty+Beam", ledger.ActivePhaseLabel);
                }
                Assert.Equal("PortfolioRefinement", ledger.ActivePhaseLabel);
            }
            Assert.Equal("Beam", ledger.ActivePhaseLabel);
        }
        Assert.NotEqual("HybridNovelty+Beam", ledger.ActivePhaseLabel);

        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL", addPowers: true);
        var portfolio = new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: true,
            PortfolioOptionsVersion: 2,
            UseNoveltyPortfolio: true,
            NoveltyFamiliarAllowance: 1,
            NoveltyMaxOpen: 6,
            SchedulingMode: PortfolioSchedulingMode.Parallel);
        var hybridLedger = new SearchWorkLedger(useParentBudget: true);
        const int budgetLimit = 1_000;
        var budget = new BeamBudget(8, budgetLimit, 6, 4, 4, TimeSpan.FromSeconds(10));
        PortfolioCoordinatorResult result = await BeamWidthPortfolioRunner.SolveAsync(
            root,
            budget,
            CombatPolicy.Default with { StopAtAcceptableBattleHpLoss = false },
            portfolio,
            0,
            Stopwatch.StartNew(),
            hybridLedger,
            tracker: null,
            CancellationToken.None,
            maxDegreeOfParallelism: 2);
        Assert.NotNull(result.ChosenPass.Selected);
        Assert.True(hybridLedger.BudgetUnits <= budgetLimit + BeamWidthPortfolio.DedicatedPowerNodeReserve(budgetLimit));
        Assert.NotEqual("HybridNovelty+Beam", hybridLedger.ActivePhaseLabel);
    }

    [Fact]
    public async Task ChooseWinningPass_BeamMembersAndNoveltyScout_RetainsSharedPotionFreeBaselineAndRejectsOverpricedScout()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        Player player = root.Players[0];
        var blockPotion = (PotionModel)ModelDb.Potion<BlockPotion>().MutableClone();
        blockPotion.AssignOwner(player);
        player.AddPotionInternal(blockPotion);
        var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
        firePotion.AssignOwner(player);
        player.AddPotionInternal(firePotion);
        root.Enemies[0].SetCurrentHpInternal(120);

        int blockSlot = -1;
        int fireSlot = -1;
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            if (player.PotionSlots[i] is BlockPotion)
                blockSlot = i;
            else if (player.PotionSlots[i] is FirePotion)
                fireSlot = i;
        }
        Assert.True(blockSlot >= 0 && fireSlot >= 0);

        CombatPolicy smartPolicy = CombatPolicy.Default with
        {
            PotionPolicy = BeamPotionPolicy.Smart,
            PredictPotionReward = false,
            IgnoreLongTermRewards = false,
            StopAtAcceptableBattleHpLoss = false,
        };
        var smartEval = new StateEvaluator(root, smartPolicy, aggressivePowerCommitment: false);
        var powerEval = new StateEvaluator(root, smartPolicy, aggressivePowerCommitment: true);
        int initialHp = player.Creature.CurrentHp;
        int blockCost = smartEval.GetPotionStrategicCost("BLOCK_POTION");
        int twoPotionCost = blockCost + smartEval.GetPotionStrategicCost("FIRE_POTION");
        Assert.True(blockCost >= 2);
        Assert.True(twoPotionCost > blockCost + 1);

        BeamSearchNode rootNode = BeamSearchNode.Create(root.Clone(), null, null, smartEval);
        var useBlock = new PlannedAction(1, "UsePotion", null, 0, -1, "BLOCK_POTION", blockSlot, [], []);
        var useFire = new PlannedAction(1, "UsePotion", null, 0, 0, "FIRE_POTION", fireSlot, [], []);

        async Task<BeamSearchNode> CreateWinningRouteAsync(
            int damage,
            bool useBlockPotion,
            bool useFirePotion,
            StateEvaluator eval)
        {
            CombatState state = root.Clone();
            BeamSearchNode parent = rootNode;
            PlannedAction? lastAction = null;
            if (useBlockPotion)
            {
                await ActionExecutor.ExecuteAsync(state, useBlock, []);
                if (!useFirePotion)
                    lastAction = useBlock;
                else
                    parent = BeamSearchNode.Create(state.Clone(), parent, useBlock, eval);
            }
            if (useFirePotion)
            {
                await ActionExecutor.ExecuteAsync(state, useFire, []);
                lastAction = useFire;
            }
            if (damage > 0)
            {
                await CreatureCmd.Damage(
                    state,
                    [state.Players[0].Creature],
                    damage,
                    ValueProp.Unblockable | ValueProp.Unpowered,
                    null,
                    null,
                    null);
            }
            foreach (Creature enemy in state.HittableEnemies.ToArray())
                await CreatureCmd.Kill(enemy);
            Assert.True(state.Engine!.CheckWinCondition());
            return BeamSearchNode.Create(state, parent, lastAction, eval);
        }

        BeamSearchNode m0PotWin = await CreateWinningRouteAsync(2, useBlockPotion: true, useFirePotion: false, smartEval);
        BeamSearchNode m0FreeWeakWin = await CreateWinningRouteAsync(twoPotionCost + 10, useBlockPotion: false, useFirePotion: false, smartEval);
        BeamSearchNode m1FreeStrongWin = await CreateWinningRouteAsync(blockCost + 2, useBlockPotion: false, useFirePotion: false, powerEval);
        BeamSearchNode scoutOverpricedWin = await CreateWinningRouteAsync(1, useBlockPotion: true, useFirePotion: true, smartEval);

        var beamMember0 = new ForecastPassOutcome(
            m0PotWin,
            [
                new EvaluatedForecastCandidate(m0PotWin, ForecastSearchBoundary.None, m0PotWin.State.Players[0].Creature.CurrentHp),
                new EvaluatedForecastCandidate(m0FreeWeakWin, ForecastSearchBoundary.None, m0FreeWeakWin.State.Players[0].Creature.CurrentHp),
            ],
            ForecastSearchBoundary.None,
            ExpandedNodes: 10,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            smartEval);
        var beamMember1 = new ForecastPassOutcome(
            m1FreeStrongWin,
            [
                new EvaluatedForecastCandidate(m1FreeStrongWin, ForecastSearchBoundary.None, m1FreeStrongWin.State.Players[0].Creature.CurrentHp),
            ],
            ForecastSearchBoundary.None,
            ExpandedNodes: 12,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            powerEval);
        var scoutPass = new ForecastPassOutcome(
            scoutOverpricedWin,
            [
                new EvaluatedForecastCandidate(scoutOverpricedWin, ForecastSearchBoundary.None, scoutOverpricedWin.State.Players[0].Creature.CurrentHp),
            ],
            ForecastSearchBoundary.None,
            ExpandedNodes: 8,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            smartEval);

        // 对照组：若仅传递 member0 自身候选而丢失 member1 的强无药水基线，超额双药水 scout 会错误胜出。
        ForecastPassOutcome withoutSharedBaseline = PreCombatBeamPortfolio.ChooseWinningPass(
            [beamMember0, scoutPass], initialHp, smartEval);
        Assert.Same(scoutOverpricedWin, withoutSharedBaseline.Selected);

        // 修复路径 1：beam 成员先决胜后，产物上保留跨成员共享的最强无药水基线 m1FreeStrongWin，再与 scout 对比时拒绝超额 scout。
        ForecastPassOutcome beamChosen = PreCombatBeamPortfolio.ChooseWinningPass(
            [beamMember0, beamMember1], initialHp, smartEval);
        Assert.Same(m0PotWin, beamChosen.Selected);
        Assert.Same(m1FreeStrongWin, beamChosen.SharedPotionFreeBaseline);
        ForecastPassOutcome afterScoutViaCarriedBaseline = BeamIncumbentPolicy.ChooseBetterPassOutcome(
            beamChosen, scoutPass, initialHp, smartEval);
        Assert.Same(m0PotWin, afterScoutViaCarriedBaseline.Selected);
        Assert.Same(m1FreeStrongWin, afterScoutViaCarriedBaseline.SharedPotionFreeBaseline);

        // 修复路径 2：全部可比 beam 成员与 scout 联合决胜同样拒绝超额 scout 并保留 m0PotWin。
        ForecastPassOutcome combinedWinner = PreCombatBeamPortfolio.ChooseWinningPass(
            [beamMember0, beamMember1, scoutPass], initialHp, smartEval);
        Assert.Same(m0PotWin, combinedWinner.Selected);

        // AcceptableVictoryFound=true 的获胜药水成员同样携带并延续共享无药水基线。
        ForecastPassOutcome acceptableBeam0 = beamMember0 with
        {
            AcceptableVictoryFound = true,
            EvaluatedCandidates = [new EvaluatedForecastCandidate(m0PotWin, ForecastSearchBoundary.None, m0PotWin.State.Players[0].Creature.CurrentHp)],
        };
        ForecastPassOutcome acceptableBeamChosen = PreCombatBeamPortfolio.ChooseWinningPass(
            [acceptableBeam0, beamMember1], initialHp, smartEval);
        Assert.Same(m1FreeStrongWin, acceptableBeamChosen.SharedPotionFreeBaseline);
        ForecastPassOutcome acceptableVsScout = BeamIncumbentPolicy.ChooseBetterPassOutcome(
            acceptableBeamChosen, scoutPass, initialHp, smartEval);
        Assert.Same(m0PotWin, acceptableVsScout.Selected);

        // RequireAtLeastOne 药水策略下同样利用跨成员无药水基线拒绝不达标的多药水 scout。
        CombatPolicy requirePolicy = smartPolicy with { PotionPolicy = BeamPotionPolicy.RequireAtLeastOne };
        var reqEval = new StateEvaluator(root, requirePolicy, aggressivePowerCommitment: false);
        var reqPowerEval = new StateEvaluator(root, requirePolicy, aggressivePowerCommitment: true);
        Assert.True(twoPotionCost - 9 > 4);
        BeamSearchNode reqPot1Win = await CreateWinningRouteAsync(5, useBlockPotion: true, useFirePotion: false, reqEval);
        BeamSearchNode reqFreeStrongWin = await CreateWinningRouteAsync(5, useBlockPotion: false, useFirePotion: false, reqPowerEval);
        BeamSearchNode reqScout2PotWin = await CreateWinningRouteAsync(1, useBlockPotion: true, useFirePotion: true, reqEval);

        var reqMember0 = new ForecastPassOutcome(
            reqPot1Win,
            [new EvaluatedForecastCandidate(reqPot1Win, ForecastSearchBoundary.None, reqPot1Win.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            ExpandedNodes: 6,
            AcceptableVictoryFound: true,
            RootInitialCumulativeHpLost: 0,
            reqEval);
        var reqMember1 = new ForecastPassOutcome(
            reqFreeStrongWin,
            [new EvaluatedForecastCandidate(reqFreeStrongWin, ForecastSearchBoundary.None, reqFreeStrongWin.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            ExpandedNodes: 6,
            AcceptableVictoryFound: true,
            RootInitialCumulativeHpLost: 0,
            reqPowerEval);
        var reqScout = new ForecastPassOutcome(
            reqScout2PotWin,
            [new EvaluatedForecastCandidate(reqScout2PotWin, ForecastSearchBoundary.None, reqScout2PotWin.State.Players[0].Creature.CurrentHp)],
            ForecastSearchBoundary.None,
            ExpandedNodes: 6,
            AcceptableVictoryFound: false,
            RootInitialCumulativeHpLost: 0,
            reqEval);

        ForecastPassOutcome reqBeamChosen = PreCombatBeamPortfolio.ChooseWinningPass([reqMember0, reqMember1], initialHp, reqEval);
        Assert.Same(reqFreeStrongWin, reqBeamChosen.SharedPotionFreeBaseline);
        ForecastPassOutcome reqFinal = BeamIncumbentPolicy.ChooseBetterPassOutcome(reqBeamChosen, reqScout, initialHp, reqEval);
        Assert.Same(reqPot1Win, reqFinal.Selected);
    }

    [Fact]
    public async Task EvaluateForNode_PreservesOffhandCostHookFromAnotherCombat()
    {
        CombatState root = await CreateSilentCombatAsync("NIBBITS_NORMAL");
        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        CombatState other = root.Clone();
        Player owner = other.Players[0];
        await PowerCmd.Apply<BorrowedTimePower>(other, owner.Creature, 1m, owner.Creature, null);
        CardModel card = owner.PlayerCombatState!.Hand.Cards.First(c => !c.CostsXEnergy && c.LocalEnergyCost >= 0);
        card.SetTemporaryCostOverrideThisTurnOrUntilPlayed(int.MaxValue);
        root.Players[0].PlayerCombatState!.DrawPile.AddInternal(card);

        // The owner's Borrowed Time raises this card's cost beyond the representable range.
        Assert.Throws<OverflowException>(() => card.EnergyCost);
        Assert.Throws<OverflowException>(() => evaluator.EvaluateForNode(root, 0, 0, 0, default, 0, 0, out _));
    }

    private sealed class TraceSelectionSource(IReadOnlyList<InCombatChoiceTrace> choices) : ICardSelectionDecisionSource
    {
        public Queue<InCombatChoiceTrace> Pending { get; } = new(choices);

        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            InCombatChoiceTrace choice = Pending.Dequeue();
            Assert.Equal(choice.SourceId, request.Source?.Id.Entry ?? string.Empty);
            // OptionOccurrence counts preceding copies of the same ID and upgrade level.
            var selected = choice.Cards.Select(token => request.Candidates
                .Where(card => card.Id.Entry == token.CardId && card.CurrentUpgradeLevel == token.UpgradeLevel)
                .ElementAt(token.OptionOccurrence)).ToArray();
            return Task.FromResult<IReadOnlyList<CardModel>>(selected);
        }
    }

    private static CardModel CreateCard<TCard>(Player owner) where TCard : CardModel
    {
        var card = (CardModel)ModelDb.Card<TCard>().MutableClone();
        card.AssignOwner(owner);
        return card;
    }

    private static void ClearPile(CardPile pile)
    {
        while (pile.Cards.Count > 0)
            pile.RemoveInternal(pile.Cards[0]);
    }

    private static async Task<CombatState> CreateAct0BossRootAsync()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ActDefinition[] acts = [new Overgrowth(), new Hive(), new Glory()];
        var run = new RunState("PORTFOLIO-BOSS-TEST", acts, 0);
        Player player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        var room = Assert.IsType<CombatRoom>(RoomFactory.CreateRoom(run, RoomType.Boss));
        run.PushRoom(room);
        await room.Enter(run);
        return room.Engine.State;
    }

    private static async Task<CombatState> CreateSilentCombatAsync(
        string encounterId,
        bool addPowers = false)
    {
        TestSaveBuilder.EnsurePrewarmed();
        ActDefinition[] acts = [new Overgrowth(), new Hive(), new Glory()];
        var run = new RunState("PORTFOLIO-TEST-001", acts, 10);
        Player player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        if (addPowers)
        {
            void AddDeck<TCard>() where TCard : CardModel
            {
                var card = (CardModel)ModelDb.Card<TCard>().MutableClone();
                card.AssignOwner(player);
                player.Deck.AddInternal(card);
            }
            AddDeck<Footwork>();
            AddDeck<Afterimage>();
            AddDeck<Accuracy>();
            AddDeck<BladeDance>();
        }
        _ = run.CurrentAncientEventType;
        Overgrowth act = (Overgrowth)acts[0];
        EncounterDefinition encounter = act.MonsterEncounterCandidates
            .Concat(act.EliteEncounterCandidates)
            .Concat(act.BossEncounterCandidates)
            .Single(item => item.IdEntry == encounterId);
        RoomType roomType = encounterId.EndsWith("_ELITE", StringComparison.Ordinal)
            ? RoomType.Elite
            : RoomType.Monster;
        var room = new CombatRoom(
            () => encounter.CreateMonsters(new Rng(run.Rng.Seed + (ulong)run.TotalFloor, encounter.IdEntry)),
            roomType, encounter.Name);
        run.PushRoom(room);
        await room.Enter(run);
        return room.Engine.State;
    }
}
