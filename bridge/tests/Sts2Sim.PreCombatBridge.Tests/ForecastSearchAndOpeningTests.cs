using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed partial class ForecastSearchAndOpeningTests
{
    [Fact]
    public async Task LivingProjectedLethalFrontierAtNodeLimit_ReturnsDeathOnlyAndNullCombatEndedTurn()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto tightBudget = TestSaveBuilder.DefaultPolicy(beamWidth: 1, maxExpandedNodes: 1);

        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "MAWLER_NORMAL",
            target.coord,
            playerCurrentHpOverride: 1,
            simulationSeed: 42UL,
            policy: tightBudget);

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        Assert.Equal("Succeeded", dto.Outcome);
        Assert.Equal("NodeLimit", dto.SearchBoundary);
        Assert.Null(dto.CombatEndedTurn);
        Assert.True(dto.OnlyDeathRoutes);
        Assert.Equal("DeathOnly", dto.Confidence);
    }

    [Fact]
    public async Task OpeningDefeat_BothInBridgeAndTerminalRoot_ReturnsDeathOnlyWithNullEndedTurn()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        // Bridge full-combat defeat when player enters at 1 HP with zero playable cards against Nibbits.
        BridgeRequestDto bridgeDefeatReq = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            playerCurrentHpOverride: 1,
            mutateSave: root =>
            {
                root["players"]![0]!["deck"] = new JsonArray
                {
                    new JsonObject { ["id"] = "CARD.ASCENDERS_BANE", ["floor_added_to_deck"] = 1 },
                };
            });

        (BridgeResponseDto defeatDto, _) = await TestSaveBuilder.RunAsync(bridgeDefeatReq);
        Assert.Equal("Succeeded", defeatDto.Outcome);
        Assert.True(defeatDto.OnlyDeathRoutes);
        Assert.Equal("DeathOnly", defeatDto.Confidence);
        Assert.Equal("None", defeatDto.SearchBoundary);
        Assert.Null(defeatDto.CombatEndedTurn);
        Assert.Equal(0, defeatDto.FinalHp);

        // Direct terminal-root defeat before BeamSearch consumes any nodes.
        RunState terminalRun = TestSaveBuilder.CreateRun();
        MapPoint terminalTarget = TestSaveBuilder.FindFirstUnvisitedPoint(terminalRun, MapPointType.Monster);
        EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry);
        ForecastRoomEntryResult entered = await terminalRun.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, terminalTarget.coord.row + 1, terminalTarget.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        CombatState combat = entered.Room.Engine.State;
        combat.Players[0].Creature.SetCurrentHpInternal(1);
        await CreatureCmd.Damage(combat, [combat.Players[0].Creature], 10m, Sts2Sim.Core.ValueProps.ValueProp.Unblockable | Sts2Sim.Core.ValueProps.ValueProp.Unpowered, null, null, null);
        Assert.True(combat.Engine!.CheckWinCondition());
        Assert.False(combat.Engine.IsInProgress);
        Assert.False(combat.Engine.Won);

        var budget = new BeamBudget(8, 200, 8, 6, 6, TimeSpan.FromSeconds(5));
        BeamForecastResult directResult = await BeamSearch.SolveForForecastAsync(
            combat, budget, CombatPolicy.Default, new PreCombatPortfolioOptions(false), 1, CancellationToken.None);
        Assert.Equal(0, directResult.ExpandedNodes);
        Assert.False(directResult.Won);
        Assert.Null(directResult.CombatEndedTurn);
        Assert.True(directResult.OnlyDeathRoutes);
        Assert.Equal(ForecastConfidence.DeathOnly, directResult.Confidence);
        Assert.Equal(ForecastSearchBoundary.None, directResult.SearchBoundary);
    }

    [Fact]
    public async Task TerminalOpeningVictory_DuringStartCombatAsync_ReturnsCompleteWithZeroExpandedNodes()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        // Five Festive Poppers deal 45 opening turn-1 damage, slaying ShrinkerBeetle (max 42 HP) before branch search.
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "SHRINKER_BEETLE_WEAK",
            target.coord,
            mutateSave: root =>
            {
                var relics = (JsonArray)root["players"]![0]!["relics"]!;
                for (int i = 0; i < 5; i++)
                    relics.Add(new JsonObject { ["id"] = "RELIC.FESTIVE_POPPER" });
            });

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        Assert.Equal("Succeeded", dto.Outcome);
        Assert.False(dto.OnlyDeathRoutes);
        Assert.Equal("Complete", dto.Confidence);
        Assert.Equal("None", dto.SearchBoundary);
        Assert.Equal(1, dto.CombatEndedTurn);
        Assert.Equal(0L, dto.ExpandedNodes);
        Assert.Equal(0, dto.ProjectedHpLoss);
    }

    [Theory]
    [InlineData("GREMLIN_MERC_NORMAL", "Normal", true)]
    [InlineData("THIEVING_HOPPER_WEAK", "Normal", true)]
    [InlineData("NIBBITS_WEAK", "Normal", false)]
    public async Task RosterDependentTheftPolicy_ResolvedOnReconstructedCombatRoot(
        string encounterId,
        string roomKind,
        bool expectPreserveResources)
    {
        RunState run = TestSaveBuilder.CreateRun(actIndex: 1);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet(encounterId, out EncounterCatalogEntry entry));

        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatPolicy resolved = BridgePolicyResolver.ResolveForCombatRoot(entered.Room.Engine.State, CombatPolicy.Default);
        Assert.Equal(
            expectPreserveResources ? SolverTheftPolicy.PreserveResources : null,
            resolved.TheftPolicy);
        Assert.Equal(roomKind == "Boss", resolved.Act3BossStrategy);
    }

    [Fact]
    public async Task ToolboxOpeningChoice_SolvesCleanly_AndHandlesSoftBudgetExhaustionVsGamblingChip()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        BridgeRequestDto toolboxReq = TestSaveBuilder.CreateRequest(
            run,
            "SHRINKER_BEETLE_WEAK",
            target.coord,
            simulationSeed: 123UL,
            policy: TestSaveBuilder.DefaultPolicy(beamWidth: 8, maxExpandedNodes: 600),
            mutateSave: root =>
            {
                ((JsonArray)root["players"]![0]!["relics"]!).Add(new JsonObject { ["id"] = "RELIC.TOOLBOX" });
            });
        (BridgeResponseDto okDto, _) = await TestSaveBuilder.RunAsync(toolboxReq);
        Assert.Equal("Succeeded", okDto.Outcome);
        Assert.True(okDto.ExpandedNodes > 0);

        // NodeLimit soft exhaustion after at least 1 leaf discovered (openingCap = 32 / 16 = 2 leaves max).
        BridgeRequestDto softNodeReq = toolboxReq with
        {
            Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 4, maxExpandedNodes: 32),
            OverallTimeoutMilliseconds = 20_000,
        };
        (BridgeResponseDto softNodeDto, _) = await TestSaveBuilder.RunAsync(softNodeReq);
        Assert.Equal("Succeeded", softNodeDto.Outcome);
        Assert.Equal("NodeLimit", softNodeDto.SearchBoundary);

        // Soft exhaustion with 0 verified leaves while hard deadline token is still live -> Failed (never TimedOut).
        BridgeRequestDto zeroLeafReq = toolboxReq with
        {
            Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 4, maxExpandedNodes: 2),
            OverallTimeoutMilliseconds = 20_000,
        };
        (BridgeResponseDto zeroLeafDto, _) = await TestSaveBuilder.RunAsync(zeroLeafReq);
        Assert.Equal("Failed", zeroLeafDto.Outcome);

        // GamblingChip (optional multi-discard) -> opening_choice_unsupported.
        BridgeRequestDto gamblingReq = TestSaveBuilder.CreateRequest(
            run,
            "SHRINKER_BEETLE_WEAK",
            target.coord,
            mutateSave: root =>
            {
                ((JsonArray)root["players"]![0]!["relics"]!).Add(new JsonObject { ["id"] = "RELIC.GAMBLING_CHIP" });
            });
        (BridgeResponseDto missDto, _) = await TestSaveBuilder.RunAsync(gamblingReq);
        Assert.Equal("CapabilityMiss", missDto.Outcome);
        Assert.Equal("opening_choice_unsupported", missDto.FailureCode);
    }

    [Fact]
    public async Task PreCombatBeamPortfolio_WorksWithPortfolioOffAndOn()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);

        BridgeRequestDto offReq = TestSaveBuilder.CreateRequest(
            run,
            "SHRINKER_BEETLE_WEAK",
            target.coord,
            simulationSeed: 99UL,
            policy: TestSaveBuilder.DefaultPolicy(beamWidth: 12, maxExpandedNodes: 800, useBeamWidthPortfolio: false));
        (BridgeResponseDto offDto, _) = await TestSaveBuilder.RunAsync(offReq);
        Assert.Equal("Succeeded", offDto.Outcome);

        BridgeRequestDto onReq = offReq with
        {
            Policy = TestSaveBuilder.DefaultPolicy(beamWidth: 16, maxExpandedNodes: 4500, useBeamWidthPortfolio: true),
        };
        (BridgeResponseDto onDto, _) = await TestSaveBuilder.RunAsync(onReq);
        Assert.Equal("Succeeded", onDto.Outcome);
        Assert.True(onDto.ExpandedNodes >= 1);
    }

    [Theory]
    [InlineData("novelty")]
    [InlineData("early_turn")]
    [InlineData("show_off")]
    [InlineData("third_party")]
    [InlineData("unknown_relic_counter")]
    public async Task UnsupportedPolicyFlags_ReturnPolicyUnsupportedCapabilityMiss(string flag)
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy();

        policy = flag switch
        {
            "novelty" => policy with { UseNoveltyPortfolio = true },
            "early_turn" => policy with { UseEarlyTurnExploration = true },
            "show_off" => policy with
            {
                GrowthBudgets = policy.GrowthBudgets with { ShowOff = 4 },
            },
            "third_party" => policy with
            {
                GrowthBudgets = policy.GrowthBudgets with
                {
                    ThirdParty = new Dictionary<string, int> { ["ModCard"] = 3 },
                },
            },
            "unknown_relic_counter" => policy with
            {
                RelicCounterRules =
                [
                    new BridgeRelicCounterRuleDto("FutureUnmappedRelic", true, 1, 2, 0, 1),
                ],
            },
            _ => policy,
        };

        BridgeRequestDto req = TestSaveBuilder.CreateRequest(run, "NIBBITS_WEAK", target.coord, policy: policy);
        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        Assert.Equal("CapabilityMiss", dto.Outcome);
        Assert.Equal("policy_unsupported", dto.FailureCode);
    }

    [Fact]
    public async Task ExternalCancellation_AndOverallTimeout_ReturnCancelledAndTimedOutWithNullMetrics()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "MAWLER_NORMAL",
            target.coord,
            policy: TestSaveBuilder.DefaultPolicy(beamWidth: 128, maxExpandedNodes: 200_000, softTimeBudgetMs: 10_000));

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        (BridgeResponseDto cancelledDto, _) = await TestSaveBuilder.RunAsync(req, preCancelled.Token);
        Assert.Equal("Cancelled", cancelledDto.Outcome);
        Assert.Null(cancelledDto.ProjectedHpLoss);
        Assert.Null(cancelledDto.ExpandedNodes);
        Assert.Equal(1, cancelledDto.EffectiveDegreeOfParallelism);

        using var midCancelled = new CancellationTokenSource(5);
        BridgeRequestDto heavyReq = req with { SearchBudgetMilliseconds = 10_000, OverallTimeoutMilliseconds = 10_000 };
        (BridgeResponseDto midDto, _) = await TestSaveBuilder.RunAsync(heavyReq, midCancelled.Token);
        Assert.True(midDto.Outcome is "Cancelled" or "Succeeded");
        if (midDto.Outcome == "Cancelled")
            Assert.Null(midDto.ProjectedHpLoss);

        BridgeRequestDto shortOverallReq = req with
        {
            SearchBudgetMilliseconds = 10_000,
            OverallTimeoutMilliseconds = 1,
        };
        (BridgeResponseDto timedOutDto, _) = await TestSaveBuilder.RunAsync(shortOverallReq);
        Assert.Equal("TimedOut", timedOutDto.Outcome);
        Assert.Null(timedOutDto.ProjectedHpLoss);
        Assert.Null(timedOutDto.SearchElapsedMilliseconds);
        Assert.NotNull(timedOutDto.TotalElapsedMilliseconds);
    }

    [Fact]
    public async Task EvaluateForecastFinalPool_HandlesEmptyPool_MixedPotionPool_AndPerNodePendingChoiceBoundary()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        var potion = (PotionModel)ModelDb.Potion<BlockPotion>().MutableClone();
        potion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(potion);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState root = entered.Room.Engine.State;
        CombatPolicy requirePotionPolicy = CombatPolicy.Default with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne };
        var requiredEvaluator = new StateEvaluator(root, requirePotionPolicy);

        Assert.Throws<InvalidOperationException>(() =>
            BeamSearch.EvaluateForecastFinalPool(
                [],
                root.Players[0].Creature.CurrentHp,
                0,
                requiredEvaluator,
                ForecastSearchBoundary.NodeLimit,
                0,
                false,
                _ => false,
                CancellationToken.None));

        // Construct a surviving potion-free node (excluded by RequireAtLeastOne) and a dead potion-using node (selected).
        CombatState survivorState = root.Clone();
        foreach (Creature enemy in survivorState.Enemies.ToArray())
            await CreatureCmd.Kill(enemy);
        SearchNode rootWrapper = SearchNode.Create(root.Clone(), null, null, requiredEvaluator);
        SearchNode survivorWithoutPotion = SearchNode.Create(survivorState, null, null, requiredEvaluator);

        CombatState deadPotionState = root.Clone();
        await CreatureCmd.Damage(
            deadPotionState,
            [deadPotionState.Players[0].Creature],
            999m,
            Sts2Sim.Core.ValueProps.ValueProp.Unblockable | Sts2Sim.Core.ValueProps.ValueProp.Unpowered,
            null,
            null,
            null);
        var potionAction = new PlannedAction(1, "UsePotion", null, 0, -1, "BLOCK_POTION", 0, [], []);
        SearchNode deadWithPotion = SearchNode.Create(deadPotionState, rootWrapper, potionAction, requiredEvaluator);

        ForecastPassOutcome pass = BeamSearch.EvaluateForecastFinalPool(
            [survivorWithoutPotion, deadWithPotion],
            root.Players[0].Creature.CurrentHp,
            0,
            requiredEvaluator,
            ForecastSearchBoundary.NodeLimit,
            10,
            false,
            _ => false,
            CancellationToken.None);
        BeamForecastResult mixedResult = BeamSearch.BuildForecastResult(
            pass, pass.EvaluatedCandidates, pass.ExpandedNodes, 0, 1.0);
        Assert.Same(deadWithPotion, pass.Selected);
        Assert.False(mixedResult.OnlyDeathRoutes);
        Assert.NotEqual(ForecastConfidence.DeathOnly, mixedResult.Confidence);

        // Neither NodeLimit nor PendingChoice suppresses live lethal threat on an unchanged parent state;
        // UnsupportedEffect still suppresses projection, while passBoundary PendingChoice surfaces cleanly.
        CombatState lethalRoot = root.Clone();
        lethalRoot.Players[0].Creature.SetCurrentHpInternal(5);
        Creature attacker = lethalRoot.Enemies.First(e => e.IsAlive);
        Assert.True(attacker.Monster!.NextMove!.Intents.OfType<AttackIntent>().Sum(i =>
            i.GetTotalDamage([lethalRoot.Players[0].Creature], attacker)) > 5);
        var defaultEvaluator = new StateEvaluator(lethalRoot, CombatPolicy.Default);

        SearchNode nodeLimitCandidate = SearchNode.Create(
            lethalRoot.Clone(), null, null, defaultEvaluator, nodeBoundary: ForecastSearchBoundary.NodeLimit);
        Assert.Equal(0, defaultEvaluator.ComputeProjectedPlayerHp(nodeLimitCandidate, nodeLimitCandidate.NodeBoundary));

        SearchNode livePreChoiceCandidate = SearchNode.Create(
            lethalRoot.Clone(), null, null, defaultEvaluator, nodeBoundary: ForecastSearchBoundary.PendingChoice);
        Assert.Equal(0, defaultEvaluator.ComputeProjectedPlayerHp(livePreChoiceCandidate, livePreChoiceCandidate.NodeBoundary));

        SearchNode unsupportedCandidate = SearchNode.Create(
            lethalRoot.Clone(), null, null, defaultEvaluator, nodeBoundary: ForecastSearchBoundary.UnsupportedEffect);
        Assert.Equal(5, defaultEvaluator.ComputeProjectedPlayerHp(unsupportedCandidate, unsupportedCandidate.NodeBoundary));

        ForecastPassOutcome lethalPendingPass = BeamSearch.EvaluateForecastFinalPool(
            [SearchNode.Create(lethalRoot.Clone(), null, null, defaultEvaluator)],
            5,
            0,
            defaultEvaluator,
            ForecastSearchBoundary.PendingChoice,
            5,
            false,
            _ => false,
            CancellationToken.None);
        BeamForecastResult lethalPendingResult = BeamSearch.BuildForecastResult(
            lethalPendingPass, lethalPendingPass.EvaluatedCandidates, lethalPendingPass.ExpandedNodes, 0, 1.0);
        Assert.Equal(ForecastSearchBoundary.PendingChoice, lethalPendingResult.SearchBoundary);
        Assert.True(lethalPendingResult.OnlyDeathRoutes);
        Assert.Equal(ForecastConfidence.DeathOnly, lethalPendingResult.Confidence);

        CombatState survivingRoot = root.Clone();
        survivingRoot.Players[0].Creature.SetMaxHpInternal(500);
        survivingRoot.Players[0].Creature.SetCurrentHpInternal(500);
        var survivingEvaluator = new StateEvaluator(survivingRoot, CombatPolicy.Default);
        ForecastPassOutcome survivingPendingPass = BeamSearch.EvaluateForecastFinalPool(
            [SearchNode.Create(survivingRoot.Clone(), null, null, survivingEvaluator)],
            500,
            0,
            survivingEvaluator,
            ForecastSearchBoundary.PendingChoice,
            5,
            false,
            _ => false,
            CancellationToken.None);
        BeamForecastResult survivingPendingResult = BeamSearch.BuildForecastResult(
            survivingPendingPass, survivingPendingPass.EvaluatedCandidates, survivingPendingPass.ExpandedNodes, 0, 1.0);
        Assert.Equal(ForecastSearchBoundary.PendingChoice, survivingPendingResult.SearchBoundary);
        Assert.False(survivingPendingResult.OnlyDeathRoutes);
        Assert.Equal(ForecastConfidence.Bounded, survivingPendingResult.Confidence);
    }

    [Theory]
    [InlineData(2, "TEST_SUBJECT_BOSS", true)]
    [InlineData(2, "AEONGLASS_BOSS", true)]
    [InlineData(2, "QUEEN_BOSS", true)]
    [InlineData(2, "VANTOM_BOSS", false)]
    [InlineData(0, "TEST_SUBJECT_BOSS", false)]
    [InlineData(0, "VANTOM_BOSS", false)]
    [InlineData(1, "KNOWLEDGE_DEMON_BOSS", false)]
    public async Task BridgePolicyResolver_Act3BossStrategy_MatchesStrictAct3BossAllowlistOnly(
        int actIndex,
        string bossEncounterId,
        bool expectedAct3BossStrategy)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(actIndex: actIndex);
        Assert.True(EncounterCatalog.TryGet(bossEncounterId, out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Boss,
                run.Map.BossMapPoint.coord.row + 1,
                run.Map.BossMapPoint.coord.col,
                MapPointType.Boss,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatPolicy resolved = BridgePolicyResolver.ResolveForCombatRoot(entered.Room.Engine.State, CombatPolicy.Default);
        Assert.Equal(expectedAct3BossStrategy, resolved.Act3BossStrategy);
    }

    [Fact]
    public async Task PreCombatBeamPortfolio_SmallNodeBudgetUnder4000_StillRunsMember1WhenUnacceptable()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        combat.Players[0].Creature.SetCurrentHpInternal(5);
        var smallBudget = new BeamBudget(8, 1200, 8, 6, 6, TimeSpan.FromSeconds(10));
        CombatPolicy stopOnZeroLoss = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };

        BeamForecastResult singlePass = await BeamSearch.SolveForForecastAsync(
            combat.Clone(),
            smallBudget,
            stopOnZeroLoss,
            new PreCombatPortfolioOptions(false),
            0,
            CancellationToken.None);
        BeamForecastResult portfolioPass = await BeamSearch.SolveForForecastAsync(
            combat.Clone(),
            smallBudget,
            stopOnZeroLoss,
            new PreCombatPortfolioOptions(true),
            0,
            CancellationToken.None);

        Assert.True(singlePass.ExpandedNodes < smallBudget.NodeLimit);
        Assert.True(
            portfolioPass.ExpandedNodes > singlePass.ExpandedNodes,
            $"Expected portfolio pass ({portfolioPass.ExpandedNodes}) > single pass ({singlePass.ExpandedNodes}) for small NodeLimit < 4000.");
    }

    [Fact]
    public async Task ChoiceExpansion_AndForecastSearch_ThinkingAheadPendingChoice_ReplaysCleanlyToExactEndpoint()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardPile pile in pcs.AllPiles)
            ClearCombatPile(pile);

        ThinkingAhead thinkingAhead = CreateCombatCard<ThinkingAhead>(player);
        thinkingAhead.BaseReplayCount = 13;
        pcs.Hand.AddInternal(thinkingAhead);
        pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        player.Creature.SetCurrentHpInternal(5);

        Creature attacker = combat.Enemies.First(e => e.IsAlive);
        Assert.True(attacker.Monster!.NextMove!.Intents.OfType<AttackIntent>().Sum(i =>
            i.GetTotalDamage([player.Creature], attacker)) > 5);

        var budget = new BeamBudget(8, 100, 8, 1, 1, TimeSpan.FromSeconds(10));
        var evaluator = new StateEvaluator(combat, CombatPolicy.Default);
        SearchNode rootNode = SearchNode.Create(combat.Clone(), null, null, evaluator);
        PlannedAction playThinkingAhead = ActionGenerator.GenerateForForecast(combat, CombatPolicy.Default)
            .Single(a => a.Kind == "PlayCard" && a.CardId == "THINKING_AHEAD");

        ChoiceExpansion.Expansion direct = await ChoiceExpansion.ExpandAsync(
            rootNode, playThinkingAhead, budget, evaluator, budget.NodeLimit, Stopwatch.StartNew(), CancellationToken.None);
        Assert.Empty(direct.Children);
        Assert.True(direct.HitPendingChoiceBoundary);

        ForecastPassOutcome pass = await BeamSearch.RunSingleForecastPassAsync(
            combat.Clone(), budget, CombatPolicy.Default, CancellationToken.None);
        BeamForecastResult result = await BeamSearch.SolveForForecastAsync(
            combat.Clone(), budget, CombatPolicy.Default, new PreCombatPortfolioOptions(false), 0, CancellationToken.None);

        Assert.Equal(ForecastSearchBoundary.PendingChoice, pass.PassBoundary);
        Assert.Equal(ForecastSearchBoundary.PendingChoice, result.SearchBoundary);
        Assert.True(result.OnlyDeathRoutes);
        Assert.DoesNotContain(result.Actions, action => action.CardId == "THINKING_AHEAD");

        CombatState replayed = combat.Clone();
        foreach (PlannedAction step in result.Actions)
            await ActionExecutor.ExecuteAsync(replayed, step, step.ChoiceIndices);

        Assert.Equal(result.FinalHp, Math.Max(0, replayed.Players[0].Creature.CurrentHp));
        Assert.Equal(result.Won, replayed.Engine?.Won == true);
        Assert.Equal(ComputeExactDigest(pass.Selected.State), ComputeExactDigest(replayed));
    }

    [Fact]
    public async Task ChoiceExpansion_AndForecastSearch_InterruptedUsePotionChoice_DoesNotIncrementPotionUsesOrSuppressLethalThreat()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        var potion = (PotionModel)ModelDb.Potion<DistilledChaos>().MutableClone();
        potion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(potion);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardPile pile in pcs.AllPiles)
            ClearCombatPile(pile);

        ThinkingAhead thinkingAhead = CreateCombatCard<ThinkingAhead>(player);
        thinkingAhead.BaseReplayCount = 13;
        pcs.DrawPile.AddInternal(thinkingAhead);
        pcs.DrawPile.AddInternal(CreateCombatCard<DefendIronclad>(player));
        pcs.DrawPile.AddInternal(CreateCombatCard<DefendIronclad>(player));
        pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        player.Creature.SetCurrentHpInternal(5);

        CombatPolicy allowPotion = CombatPolicy.Default with { PotionPolicy = SolverPotionPolicy.Smart };
        PlannedAction usePotion = ActionGenerator.GenerateForForecast(combat, allowPotion)
            .Single(a => a.Kind == "UsePotion" && a.PotionId == "DISTILLED_CHAOS");

        var evaluator = new StateEvaluator(combat, allowPotion);
        SearchNode rootNode = SearchNode.Create(combat.Clone(), null, null, evaluator);
        var fullBudget = new BeamBudget(8, 100, 8, 1, 1, TimeSpan.FromSeconds(10));
        ChoiceExpansion.Expansion expansion = await ChoiceExpansion.ExpandAsync(
            rootNode, usePotion, fullBudget, evaluator, fullBudget.NodeLimit, Stopwatch.StartNew(), CancellationToken.None);
        Assert.Empty(expansion.Children);
        Assert.True(expansion.HitPendingChoiceBoundary);

        foreach (int nodeLimit in new[] { 100, 13 })
        {
            var budget = new BeamBudget(8, nodeLimit, 8, 1, 1, TimeSpan.FromSeconds(10));
            BeamForecastResult result = await BeamSearch.SolveForForecastAsync(
                combat.Clone(), budget, allowPotion, new PreCombatPortfolioOptions(false), 0, CancellationToken.None);

            Assert.Empty(result.PotionUses);
            Assert.True(result.OnlyDeathRoutes);
            Assert.Equal(ForecastConfidence.DeathOnly, result.Confidence);
            if (nodeLimit == 100)
                Assert.Equal(ForecastSearchBoundary.PendingChoice, result.SearchBoundary);

            CombatState replayed = combat.Clone();
            foreach (PlannedAction step in result.Actions)
                await ActionExecutor.ExecuteAsync(replayed, step, step.ChoiceIndices);
        }
    }

    [Fact]
    public async Task RunSingleForecastPass_AcceptableVictoryEarlyStop_IncludesOpeningPreRootBattleHpLost()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardPile pile in pcs.AllPiles)
            ClearCombatPile(pile);
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.GainEnergy(Math.Max(0, 1 - pcs.Energy));

        Creature survivingEnemy = combat.Enemies.First(e => e.IsAlive);
        foreach (Creature other in combat.Enemies.Where(e => !ReferenceEquals(e, survivingEnemy)))
            await CreatureCmd.Kill(other);
        survivingEnemy.SetCurrentHpInternal(6);

        var budget = new BeamBudget(8, 200, 8, 4, 4, TimeSpan.FromSeconds(10));
        CombatPolicy stopAtZero = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        ForecastPassOutcome zeroThresholdPass = await BeamSearch.RunSingleForecastPassAsync(
            combat.Clone(),
            budget,
            stopAtZero,
            CancellationToken.None,
            openingPreRootBattleHpLost: 3);
        BeamForecastResult zeroThresholdResult = BeamSearch.BuildForecastResult(
            zeroThresholdPass,
            zeroThresholdPass.EvaluatedCandidates,
            zeroThresholdPass.ExpandedNodes,
            openingPreRootBattleHpLost: 3,
            searchElapsedMilliseconds: 1.0);
        Assert.True(zeroThresholdPass.Selected.Won);
        Assert.False(zeroThresholdPass.AcceptableVictoryFound);
        Assert.Equal(3, zeroThresholdResult.ProjectedHpLoss);

        CombatPolicy stopAtThree = stopAtZero with { AcceptableBattleHpLoss = 3 };
        ForecastPassOutcome threeThresholdPass = await BeamSearch.RunSingleForecastPassAsync(
            combat.Clone(),
            budget,
            stopAtThree,
            CancellationToken.None,
            openingPreRootBattleHpLost: 3);
        BeamForecastResult threeThresholdResult = BeamSearch.BuildForecastResult(
            threeThresholdPass,
            threeThresholdPass.EvaluatedCandidates,
            threeThresholdPass.ExpandedNodes,
            openingPreRootBattleHpLost: 3,
            searchElapsedMilliseconds: 1.0);
        Assert.True(threeThresholdPass.Selected.Won);
        Assert.True(threeThresholdPass.AcceptableVictoryFound);
        Assert.Equal(3, threeThresholdResult.ProjectedHpLoss);
    }

    [Fact]
    public async Task SmartPotionConservation_ZeroLossEarlyStop_PrefersTwoStrikesOverOptionalFirePotion()
    {
        CombatState combat = await CreateMawlerTwoStrikesWithPotionAsync<FirePotion>(playerHp: 80, enemyHp: 10);
        BridgePolicyDto dtoPolicy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [],
            AcceptableBattleHpLoss = 0,
            StopAtAcceptableBattleHpLoss = true,
        };

        (BeamForecastResult forecast, BridgeResponseDto wire) = await RunBridgeSolveAsync(combat, dtoPolicy);

        Assert.Equal("Succeeded", wire.Outcome);
        Assert.Equal(0, wire.ProjectedHpLoss);
        Assert.Equal(80, wire.FinalHp);
        Assert.NotNull(wire.CombatEndedTurn);
        Assert.Empty(wire.PotionUses!);
        Assert.Empty(forecast.PotionUses);
    }

    [Fact]
    public async Task ForcedFirePotion_ZeroLossEarlyStop_RemainsEligibleAndSpendsSlot0()
    {
        CombatState combat = await CreateMawlerTwoStrikesWithPotionAsync<FirePotion>(playerHp: 80, enemyHp: 10);
        BridgePolicyDto dtoPolicy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [new BridgePotionDirectiveDto(0, "FIRE_POTION", "Force")],
            AcceptableBattleHpLoss = 0,
            StopAtAcceptableBattleHpLoss = true,
        };

        (CombatPolicy basePolicy, BeamBudget budget, _) = BridgePolicyResolver.ValidateAndBuildBasePolicy(dtoPolicy, 4000, 20000, 1);
        CombatPolicy resolved = BridgePolicyResolver.ResolveForCombatRoot(combat, basePolicy);
        ForecastPassOutcome singlePass = await BeamSearch.RunSingleForecastPassAsync(
            combat.Clone(),
            budget,
            resolved,
            CancellationToken.None);
        Assert.True(singlePass.AcceptableVictoryFound);
        Assert.True(singlePass.ExpandedNodes <= 16);

        (_, BridgeResponseDto wire) = await RunBridgeSolveAsync(combat, dtoPolicy);
        Assert.Equal("Succeeded", wire.Outcome);
        Assert.Equal(0, wire.ProjectedHpLoss);
        Assert.Equal(80, wire.FinalHp);
        BridgePotionUseDto use = Assert.Single(wire.PotionUses!);
        Assert.Equal(("FIRE_POTION", 1, 0), (use.Id, use.Turn, use.Slot));
    }

    [Fact]
    public async Task DeathSaveGuard_PreventsEarlyStopWhenVictoryConsumesDeathSave()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(actIndex: 1, characterId: "CHARACTER.IRONCLAD", ascension: 0);
        var lizardTail = (RelicModel)ModelDb.Relic<LizardTail>().MutableClone();
        lizardTail.AssignOwner(run.Players[0]);
        run.Players[0].AddRelicInternal(lizardTail);

        Assert.True(EncounterCatalog.TryGet("THE_INSATIABLE_BOSS", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Boss, run.Map.BossMapPoint.coord.row + 1, run.Map.BossMapPoint.coord.col, MapPointType.Boss, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        player.Creature.SetMaxHpInternal(80);
        player.Creature.SetCurrentHpInternal(1);
        Creature boss = combat.Enemies.First(e => e.IsAlive);
        boss.SetCurrentHpInternal(6);

        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardPile pile in pcs.AllPiles)
            ClearCombatPile(pile);
        pcs.DrawPile.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.GainEnergy(Math.Max(0, 3 - pcs.Energy));

        CombatPolicy stopAtZero = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        var zeroEvaluator = new StateEvaluator(combat, stopAtZero);
        CombatState wonState = combat.Clone();
        await CreatureCmd.Kill(wonState.Enemies.First(e => e.IsAlive));
        Assert.True(wonState.Engine!.CheckWinCondition());
        SearchNode cleanWin = SearchNode.Create(wonState.Clone(), null, null, zeroEvaluator);
        SearchNode deathSaveWin = SearchNode.Create(
            wonState.Clone(), null, null, zeroEvaluator, branchDeathSaveHpRestored: 39, branchDeathSaveUseCount: 1);
        Assert.True(BeamSearch.MeetsAcceptableVictory(cleanWin, stopAtZero, zeroEvaluator, 0));
        Assert.False(BeamSearch.MeetsAcceptableVictory(deathSaveWin, stopAtZero, zeroEvaluator, 0));

        Assert.True(boss.Monster!.NextMove?.Intents.All(i => i is not AttackIntent));
        SandpitPower sandpit = (await PowerCmd.Apply<SandpitPower>(combat, boss, 1m, boss, null))!;
        sandpit.Target = player.Creature;

        CombatPolicy stopAtOne = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 1,
        };
        var budget = new BeamBudget(8, 200, 8, 4, 4, TimeSpan.FromSeconds(10));
        ForecastPassOutcome pass = await BeamSearch.RunSingleForecastPassAsync(
            combat.Clone(),
            budget,
            stopAtOne,
            CancellationToken.None);

        Assert.True(pass.Selected.Won);
        Assert.Equal(1, pass.Selected.DeathSaveUseCount);
        Assert.False(pass.AcceptableVictoryFound);
    }

    [Fact]
    public async Task ForcedFoulPotion_MixedPoolDefeat_ProducesValidBoundedNoneBoundaryResponse()
    {
        CombatState combat = await CreateMawlerTwoStrikesWithPotionAsync<FoulPotion>(playerHp: 1, enemyHp: 6);
        BridgePolicyDto dtoPolicy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true) with
        {
            PotionPolicy = "Smart",
            PotionDirectives = [new BridgePotionDirectiveDto(0, "FOUL_POTION", "Force")],
            StopAtAcceptableBattleHpLoss = false,
        };

        (_, BridgeResponseDto wire) = await RunBridgeSolveAsync(combat, dtoPolicy);

        Assert.Equal("Succeeded", wire.Outcome);
        Assert.Equal(1, wire.ProjectedHpLoss);
        Assert.Equal(0, wire.FinalHp);
        Assert.Null(wire.CombatEndedTurn);
        Assert.False(wire.OnlyDeathRoutes);
        Assert.Equal("Bounded", wire.Confidence);
        Assert.Equal("None", wire.SearchBoundary);
        BridgePotionUseDto use = Assert.Single(wire.PotionUses!);
        Assert.Equal(("FOUL_POTION", 1, 0), (use.Id, use.Turn, use.Slot));
    }

    private static async Task<CombatState> CreateMawlerTwoStrikesWithPotionAsync<TPotion>(int playerHp, int enemyHp)
        where TPotion : PotionModel
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        var potion = (PotionModel)ModelDb.Potion<TPotion>().MutableClone();
        potion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(potion);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Player player = combat.Players[0];
        player.Creature.SetMaxHpInternal(80);
        player.Creature.SetCurrentHpInternal(playerHp);
        combat.Enemies.First(e => e.IsAlive).SetCurrentHpInternal(enemyHp);

        PlayerCombatState pcs = player.PlayerCombatState!;
        foreach (CardPile pile in pcs.AllPiles)
            ClearCombatPile(pile);
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.Hand.AddInternal(CreateCombatCard<StrikeIronclad>(player));
        pcs.GainEnergy(Math.Max(0, 3 - pcs.Energy));
        return combat;
    }

    private static async Task<(BeamForecastResult Forecast, BridgeResponseDto Wire)> RunBridgeSolveAsync(
        CombatState combat,
        BridgePolicyDto dtoPolicy)
    {
        (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
            BridgePolicyResolver.ValidateAndBuildBasePolicy(dtoPolicy, 4000, 20000, 1);
        CombatPolicy resolved = BridgePolicyResolver.ResolveForCombatRoot(combat, basePolicy);
        BeamForecastResult forecast = await BeamSearch.SolveForForecastAsync(
            combat.Clone(),
            budget,
            resolved,
            portfolio,
            0,
            CancellationToken.None);
        BridgePotionUseDto[] wirePotionUses = forecast.PotionUses
            .Select(u => new BridgePotionUseDto(u.Id, u.Turn, u.Slot))
            .ToArray();
        BridgeResponseDto wire = BridgeResponseDto.Succeeded(
            forecast.ProjectedHpLoss,
            forecast.FinalHp,
            forecast.CombatEndedTurn,
            forecast.OnlyDeathRoutes,
            forecast.Confidence.ToString(),
            forecast.SearchBoundary.ToString(),
            wirePotionUses,
            forecast.ExpandedNodes,
            forecast.SearchElapsedMilliseconds,
            forecast.SearchElapsedMilliseconds);
        return (forecast, wire);
    }

    private static TCard CreateCombatCard<TCard>(Player owner) where TCard : CardModel
    {
        var card = (TCard)ModelDb.Card<TCard>().MutableClone();
        card.AssignOwner(owner);
        return card;
    }

    private static void ClearCombatPile(CardPile pile)
    {
        foreach (CardModel card in pile.Cards.ToArray())
            pile.RemoveInternal(card);
    }

    private static CombatStateDescriptionDigest ComputeExactDigest(CombatState state)
    {
        var builder = new CombatStateDescriptionBuilder();
        CombatStateDescription.AppendExactState(ref builder, state);
        return builder.Build();
    }
}
