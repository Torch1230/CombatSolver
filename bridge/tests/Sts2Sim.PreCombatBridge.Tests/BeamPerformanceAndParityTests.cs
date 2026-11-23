using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Orbs;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.ValueProps;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class BeamPerformanceAndParityTests
{
    [Fact]
    public async Task Retention_SelectWithKey_PreservesRankingQuotaParetoBackfillAndPotionFree()
    {
        CombatState root = await CreateMawlerCombatAsync();
        Player player = root.Players[0];
        var evaluator = new StateEvaluator(root, CombatPolicy.Default);

        // 构造两组不同 Buff/Debuff/Potion 配置并检查 SetupSignatureKey 区分、Pareto tie-break、回填和 preservePotionFree。
        var potionPolicy = CombatPolicy.Default with
        {
            PotionPolicy = SolverPotionPolicy.Smart,
        };
        var potionEval = new StateEvaluator(root, potionPolicy);

        CombatState sFreeStrong = root.Clone();
        sFreeStrong.Enemies[0].SetCurrentHpInternal(20);
        sFreeStrong.Players[0].Creature.SetCurrentHpInternal(65);
        await Sts2Sim.Core.Commands.PowerCmd.Apply<StrengthPower>(sFreeStrong, sFreeStrong.Players[0].Creature, 3m, sFreeStrong.Players[0].Creature, null);

        CombatState sFreeSameBucketBetterMargin = root.Clone();
        sFreeSameBucketBetterMargin.Enemies[0].SetCurrentHpInternal(30);
        sFreeSameBucketBetterMargin.Players[0].Creature.SetCurrentHpInternal(78);

        CombatState sFreeAggressiveLowHp = root.Clone();
        sFreeAggressiveLowHp.Enemies[0].SetCurrentHpInternal(12);
        sFreeAggressiveLowHp.Players[0].Creature.SetCurrentHpInternal(10);

        CombatState sFreeAggressiveBetterMargin = root.Clone();
        sFreeAggressiveBetterMargin.Enemies[0].SetCurrentHpInternal(12);
        sFreeAggressiveBetterMargin.Players[0].Creature.SetCurrentHpInternal(55);

        CombatState sPotionHighestScore = root.Clone();
        sPotionHighestScore.Enemies[0].SetCurrentHpInternal(5);
        sPotionHighestScore.Players[0].Creature.SetCurrentHpInternal(80);
        var usePotion = new PlannedAction(1, "UsePotion", null, 0, 0, "FIRE_POTION", 0, [], []);

        SearchNode nPotion = SearchNode.Create(sPotionHighestScore, null, usePotion, potionEval);
        SearchNode nStrong = SearchNode.Create(sFreeStrong, null, null, potionEval);
        SearchNode nBetterMargin = SearchNode.Create(sFreeAggressiveBetterMargin, null, null, potionEval);
        SearchNode nLowHp = SearchNode.Create(sFreeAggressiveLowHp, null, null, potionEval);
        SearchNode nBucketA = SearchNode.Create(sFreeSameBucketBetterMargin, null, null, potionEval);

        CombatState sFreeSameBucketWorse = sFreeSameBucketBetterMargin.Clone();
        sFreeSameBucketWorse.Players[0].PlayerCombatState!.DiscardPile.AddInternal(CreateCard<StrikeIronclad>(sFreeSameBucketWorse.Players[0]));
        CombatState sFreeSameBucketOrderDiff = sFreeSameBucketWorse.Clone();
        sFreeSameBucketOrderDiff.Players[0].PlayerCombatState!.DiscardPile.AddInternal(CreateCard<DefendIronclad>(sFreeSameBucketOrderDiff.Players[0]));
        SearchNode nBackfillCandidate = SearchNode.Create(sFreeSameBucketOrderDiff, null, null, potionEval);

        SearchNode[] pool = [nLowHp, nBackfillCandidate, nBucketA, nPotion, nStrong, nBetterMargin];
        List<SearchNode> retained = Retention.SelectWithKey(
            pool,
            width: 5,
            node => node.GetOrComputeRetentionKey(),
            preservePotionFree: false,
            includeParetoProgress: true,
            aggressivePowerCommitment: true);

        Assert.Equal(5, retained.Count);
        Assert.Same(nPotion, retained[0]);
        Assert.Contains(nStrong, retained);
        Assert.Contains(nBetterMargin, retained);

        // 验证 preservePotionFree 即使在宽为 2 时也会保留至少一条零药水分支（即使最高分都用过药水）。
        CombatState p1 = sPotionHighestScore.Clone();
        CombatState p2 = sPotionHighestScore.Clone();
        p2.Enemies[0].SetCurrentHpInternal(6);
        SearchNode potionNode1 = SearchNode.Create(p1, null, usePotion, potionEval);
        SearchNode potionNode2 = SearchNode.Create(p2, null, usePotion, potionEval);
        List<SearchNode> preserved = Retention.Select(
            [potionNode1, potionNode2, nStrong],
            width: 2,
            preservePotionFree: true);
        Assert.Equal(2, preserved.Count);
        Assert.Contains(preserved, n => n.PotionUses == 0);
    }

    [Fact]
    public async Task ForecastRetentionKey_OrderInvariantPilesOver32Cards_AndHistoryParity()
    {
        (string save, InCombatLiveStateDto opening, _) = await SyntheticLiveCombat.BuildTurn1CombatAsync("MAWLER_NORMAL");
        var turn = new InCombatTurnKeyDto(1, "Player", [1]);
        int playerHandle = opening.AllyHandles.Single();
        int enemyHandle = opening.EnemyHandles.Single();
        int strikeHandle = opening.Cards.First(c => c.CardId == "CARD.STRIKE_IRONCLAD").Handle;
        var outgoing = new InCombatDamageHistoryEntryDto(enemyHandle, playerHandle, enemyHandle,
            (int)ValueProp.Move, 4, 2, 4, 0, false, false, false, strikeHandle, 1, "Player", [1]);
        var incoming = new InCombatDamageHistoryEntryDto(playerHandle, enemyHandle, playerHandle,
            (int)ValueProp.Move, 0, 5, 0, 0, false, false, true, null, 1, "Player", [1]);

        CombatState Restore(bool reversed, int energySpent)
        {
            var dto = opening with
            {
                DamageHistory = reversed ? [incoming, outgoing] : [outgoing, incoming],
                SemanticHistory = opening.SemanticHistory with
                {
                    Exhausts = [new(turn, 0)],
                    EnergySpent = [new(turn, 0, energySpent)],
                    ActorEvents = [new("DoomApplied", turn, 0)],
                },
            };
            CombatState state = CombatState.RestoreLiveCombat(
                BridgeSaveRestore.RestoreLive(save), LiveCombatTransplantMapper.Map(dto)).Engine.State;
            Player player = state.Players[0];
            PlayerCombatState pcs = player.PlayerCombatState!;

            var discardOrder = new List<CardModel>(40);
            var exhaustOrder = new List<CardModel>(36);
            for (int i = 0; i < 40; i++)
            {
                CardModel card = (i % 3) switch
                {
                    0 => CreateCard<StrikeIronclad>(player),
                    1 => CreateCard<DefendIronclad>(player),
                    _ => CreateCard<Bash>(player),
                };
                if ((i & 1) == 1)
                    card.AddEnergyCostUntilPlayed(-1);
                discardOrder.Add(card);
            }
            for (int i = 0; i < 36; i++)
            {
                CardModel card = (i & 1) == 0 ? CreateCard<StrikeIronclad>(player) : CreateCard<DefendIronclad>(player);
                exhaustOrder.Add(card);
            }

            if (reversed)
            {
                discardOrder.Reverse();
                exhaustOrder.Reverse();
            }

            for (int i = 0; i < discardOrder.Count; i++)
                pcs.DiscardPile.AddInternal(discardOrder[i]);
            for (int i = 0; i < exhaustOrder.Count; i++)
                pcs.ExhaustPile.AddInternal(exhaustOrder[i]);

            return state;
        }

        CombatState stateA = Restore(reversed: false, energySpent: 2);
        CombatState stateB = Restore(reversed: true, energySpent: 2);

        var evaluator = new StateEvaluator(stateA, CombatPolicy.Default);
        SearchNode nodeA = SearchNode.Create(stateA, null, null, evaluator);
        SearchNode nodeB = SearchNode.Create(stateB, null, null, evaluator);

        Assert.NotEqual(nodeA.Fingerprint, nodeB.Fingerprint);
        CombatStateDescriptionDigest keyA = ForecastRetentionKey.ComputeUncached(nodeA);
        CombatStateDescriptionDigest keyB = ForecastRetentionKey.ComputeUncached(nodeB);
        Assert.Equal(keyA, keyB);
        Assert.Equal(keyA, ForecastRetentionKey.Compute(nodeA));

        // 变更弃牌堆任意一张卡或语义历史能量支出后，RetentionKey 必须变化。
        CombatState mutated = Restore(reversed: false, energySpent: 3);
        SearchNode mutatedNode = SearchNode.Create(mutated, null, null, evaluator);
        Assert.NotEqual(keyA, ForecastRetentionKey.Compute(mutatedNode));
    }

    [Fact]
    public async Task BeamSearch_ParallelWorkStealing_Turn0FanOutAndChoiceHeavy_DeterministicAcrossDops()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-CHOICE-PARITY", characterId: "CHARACTER.SILENT", ascension: 10);
        Player player = run.Players[0];
        void AddDeckCard<TCard>() where TCard : CardModel
        {
            var card = (CardModel)ModelDb.Card<TCard>().MutableClone();
            card.AssignOwner(player);
            player.Deck.AddInternal(card);
        }
        AddDeckCard<Prepared>();
        AddDeckCard<Acrobatics>();
        AddDeckCard<BladeDance>();

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        PlayerCombatState pcs = baseCombat.Players[0].PlayerCombatState!;
        foreach (CardModel card in pcs.Hand.Cards.ToArray())
            pcs.Hand.RemoveInternal(card);
        pcs.Hand.AddInternal(CreateCard<Prepared>(baseCombat.Players[0]));
        pcs.Hand.AddInternal(CreateCard<Acrobatics>(baseCombat.Players[0]));
        pcs.Hand.AddInternal(CreateCard<Survivor>(baseCombat.Players[0]));
        pcs.Hand.AddInternal(CreateCard<StrikeSilent>(baseCombat.Players[0]));
        pcs.Hand.AddInternal(CreateCard<DefendSilent>(baseCombat.Players[0]));
        pcs.Hand.AddInternal(CreateCard<Neutralize>(baseCombat.Players[0]));

        var budget = new BeamBudget(16, 1500, 10, 6, 6, TimeSpan.FromSeconds(10));
        var portfolio = new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true);
        CombatPolicy policy = CombatPolicy.Default;

        foreach (int dop in new[] { 1, 2, 4, 8 })
        {
            BeamForecastResult first = await BeamSearch.SolveForForecastAsync(
                baseCombat.Clone(), budget, policy, portfolio, 0, CancellationToken.None, maxDegreeOfParallelism: dop);
            BeamForecastResult second = await BeamSearch.SolveForForecastAsync(
                baseCombat.Clone(), budget, policy, portfolio, 0, CancellationToken.None, maxDegreeOfParallelism: dop);

            Assert.True(first.ExpandedNodes > 0);
            Assert.Equal(first.Won, second.Won);
            Assert.Equal(first.ProjectedHpLoss, second.ProjectedHpLoss);
            Assert.Equal(first.FinalHp, second.FinalHp);
            Assert.Equal(first.Actions.Count, second.Actions.Count);
            for (int i = 0; i < first.Actions.Count; i++)
                Assert.Equal(first.Actions[i].Kind, second.Actions[i].Kind);

            BeamReplayResult replay = await BeamSearch.ReplayAsync(baseCombat.Clone(), first.Actions);
            Assert.Equal(first.Won, replay.Won);
        }
    }

    [Fact]
    public async Task InCombatBridge_ProgressCoalescingAndHardDeadline_RespectsBoundsAndJoinsCleanly()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 10);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("PHROG_PARASITE_ELITE", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        InCombatLiveStateDto liveDto = SyntheticLiveCombat.CreateSnapshot(entered.Room, "PHROG_PARASITE_ELITE");
        var policy = new InCombatPolicyDto(
            PotionPolicy: "Smart",
            PotionDirectives: [],
            PredictPotionReward: false,
            RelicStrategyEnabled: true,
            RelicCounterRules: [],
            GrowthBudgets: new BridgeGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
            BrightestFlameMaxHpLossLimit: null,
            IgnoreLongTermRewards: false,
            ActTransitionBossHpStrategy: "ProgressionFirst",
            FinalBossHpStrategy: "ProgressionFirst",
            AcceptableBattleHpLoss: 0,
            StopAtAcceptableBattleHpLoss: false,
            UseBeamWidthPortfolio: true,
            UseNoveltyPortfolio: false,
            UseEarlyTurnExploration: false,
            BeamWidth: 48,
            PotionFreeBeamWidth: null,
            PotionBeamWidth: null,
            MaxExpandedNodes: 500_000,
            MaxCardBranchesPerNode: 12,
            MaxPileChoiceBranchesPerAction: 6,
            MaxHandChoiceBranchesPerAction: 6,
            SoftTimeBudgetMilliseconds: 10_000,
            TheftPolicy: null);

        // 触发硬期限超时并校验：收到 TimedOut，回调在 RunInCombatJsonAsync 返回后不再被调用。
        var req = new InCombatBridgeRequestDto(
            AbiVersion: InCombatBridgeAbi.Version,
            RequestId: Guid.NewGuid().ToString("N"),
            NormalizedSaveJson: saveJson,
            LiveState: liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 10_000,
            OverallTimeoutMilliseconds: 350,
            MaxDegreeOfParallelism: 4,
            Policy: policy);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions);
        var timestamps = new List<double>();
        int callbackAfterReturn = 0;
        int returnedFlag = 0;

        byte[] respBytes = await BridgeEntry.RunInCombatJsonAsync(
            payload,
            bytes =>
            {
                if (Volatile.Read(ref returnedFlag) != 0)
                    Interlocked.Increment(ref callbackAfterReturn);
                InCombatProgressDto? dto = JsonSerializer.Deserialize<InCombatProgressDto>(bytes, BridgeAbi.JsonOptions);
                if (dto is not null)
                {
                    lock (timestamps)
                        timestamps.Add(dto.ElapsedMilliseconds);
                }
            },
            CancellationToken.None);

        Volatile.Write(ref returnedFlag, 1);
        await Task.Delay(50);

        InCombatBridgeResponseDto resp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(respBytes, BridgeAbi.JsonOptions)!;
        Assert.Equal(0, Volatile.Read(ref callbackAfterReturn));
        Assert.True(resp.Outcome is "TimedOut" or "Succeeded", $"Unexpected outcome: {resp.Outcome} ({resp.Error})");
        Assert.True(resp.TotalElapsedMilliseconds < 2500, $"Hard deadline overshot: {resp.TotalElapsedMilliseconds}ms");
        lock (timestamps)
        {
            for (int i = 1; i < timestamps.Count; i++)
                Assert.True(timestamps[i] - timestamps[i - 1] >= 120.0, $"Progress intervals not coalesced: {timestamps[i - 1]} -> {timestamps[i]}");
        }
    }

    [Fact]
    public async Task ParallelPortfolio_ActEndingBossAndPredictedPotionDrop_PreservesRunStatePolicyAtDop4()
    {
        static async Task<CombatState> CreateAct0BossRootAsync()
        {
            TestSaveBuilder.EnsurePrewarmed();
            RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-BOSS-POTION-POLICY", characterId: "CHARACTER.IRONCLAD", ascension: 0);
            Player runPlayer = run.Players[0];

            var whiteBeast = (RelicModel)ModelDb.Relic<WhiteBeastStatue>().MutableClone();
            whiteBeast.AssignOwner(runPlayer);
            runPlayer.AddRelicInternal(whiteBeast);
            for (int i = 0; i < runPlayer.PotionSlots.Count; i++)
            {
                if (runPlayer.PotionSlots[i] is not null)
                    continue;
                var blockPotion = (PotionModel)ModelDb.Potion<BlockPotion>().MutableClone();
                blockPotion.AssignOwner(runPlayer);
                runPlayer.AddPotionInternal(blockPotion);
            }

            EncounterDefinition bossEncounter = run.PullNextEncounter(RoomType.Boss);
            ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
                new ForecastTargetSpec(
                    bossEncounter,
                    RoomType.Boss,
                    run.Map.BossMapPoint.coord.row + 1,
                    run.Map.BossMapPoint.coord.col,
                    MapPointType.Boss,
                    false),
                new ForecastOpeningRunner(CancellationToken.None),
                encounterMonsterSeedOverride: null);

            CombatState state = entered.Room.Engine.State;
            Player player = state.Players[0];
            PlayerCombatState pcs = player.PlayerCombatState!;
            ClearPile(pcs.Hand);
            ClearPile(pcs.DrawPile);
            ClearPile(pcs.DiscardPile);
            ClearPile(pcs.ExhaustPile);
            pcs.Hand.AddInternal(CreateCard<Inflame>(player));
            pcs.Hand.AddInternal(CreateCard<StrikeIronclad>(player));
            pcs.Hand.AddInternal(CreateCard<DefendIronclad>(player));
            pcs.DrawPile.AddInternal(CreateCard<StrikeIronclad>(player));
            pcs.DrawPile.AddInternal(CreateCard<DefendIronclad>(player));
            foreach (Creature enemy in state.HittableEnemies)
                enemy.SetCurrentHpInternal(Math.Min(enemy.CurrentHp, 18));
            return state;
        }

        CombatState combatRoot = await CreateAct0BossRootAsync();
        CombatPolicy policy = CombatPolicy.Default with
        {
            PotionPolicy = SolverPotionPolicy.Smart,
            PredictPotionReward = true,
            ActTransitionBossHpStrategy = BossHpStrategy.ProgressionFirst,
        };

        // 验证 StateEvaluator 必须从原始 root 捕获 BossHpRelief 与 PotionRewardOutlook，
        // 避免 root.Clone() 丢失真实 RunState 退化成 None / Unknown。
        StateEvaluatorBaseline captured = StateEvaluator.CaptureBaseline(combatRoot, policy);
        var evalFromBaseline = new StateEvaluator(captured);
        var evalFromClonedRoot = new StateEvaluator(combatRoot.Clone(), policy);

        Assert.Equal(BossHpRelief.ActClearHeal, captured.BossHpRelief);
        Assert.Equal(PotionRewardForecast.Drop, captured.PotionReward.Forecast);
        Assert.True(captured.PotionReward.ReplacementHpCredit > 0);
        Assert.Equal(BossHpRelief.None, evalFromClonedRoot.BossHpRelief);
        Assert.Equal(PotionRewardForecast.Unknown, evalFromClonedRoot.PotionReward.Forecast);

        // 构造一个用 BlockPotion（基础成本 9）省下 6 点真实战斗掉血的获胜对照分支：
        // ActClearHeal + ReplacementHpCredit=9..18 使有效成本降为 5（6 >= 5，允许使用）；
        // 丢失 RunState 的克隆基线退化成成本 9（6 < 9，错误拒绝）。
        CombatState freeWinState = combatRoot.Clone();
        await CreatureCmd.Damage(
            freeWinState,
            [freeWinState.Players[0].Creature],
            6m,
            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
            null,
            null,
            null);
        foreach (Creature enemy in freeWinState.HittableEnemies)
            await CreatureCmd.Kill(enemy);
        Assert.True(freeWinState.Engine!.CheckWinCondition());

        CombatState potionWinState = combatRoot.Clone();
        foreach (Creature enemy in potionWinState.HittableEnemies)
            await CreatureCmd.Kill(enemy);
        Assert.True(potionWinState.Engine!.CheckWinCondition());
        var usePotion = new PlannedAction(1, "UsePotion", null, 0, 0, "BLOCK_POTION", 0, [], []);
        SearchNode baselineNode = SearchNode.Create(freeWinState, null, null, evalFromBaseline);
        SearchNode potionNode = SearchNode.Create(potionWinState, null, usePotion, evalFromBaseline);
        Assert.True(evalFromBaseline.IsSmartPotionEligible(potionNode, baselineNode));
        Assert.False(evalFromClonedRoot.IsSmartPotionEligible(potionNode, baselineNode));

        var budget = new BeamBudget(12, 2000, 8, 4, 4, TimeSpan.FromSeconds(10));
        var portfolio = new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true);

        // 传入保留真实 RunState 的独立根节点，验证 DOP=1 与 DOP=4（内部克隆到并发成员）结果一致，并验证 DOP=4 可复现。
        BeamForecastResult dop1 = await BeamSearch.SolveForForecastAsync(
            await CreateAct0BossRootAsync(), budget, policy, portfolio, 0, CancellationToken.None, maxDegreeOfParallelism: 1);
        BeamForecastResult dop4 = await BeamSearch.SolveForForecastAsync(
            await CreateAct0BossRootAsync(), budget, policy, portfolio, 0, CancellationToken.None, maxDegreeOfParallelism: 4);
        BeamForecastResult dop4Repeat = await BeamSearch.SolveForForecastAsync(
            await CreateAct0BossRootAsync(), budget, policy, portfolio, 0, CancellationToken.None, maxDegreeOfParallelism: 4);

        Assert.Equal(dop1.Won, dop4.Won);
        Assert.Equal(dop1.FinalHp, dop4.FinalHp);
        Assert.Equal(dop1.ProjectedHpLoss, dop4.ProjectedHpLoss);
        Assert.Equal(dop1.PotionUses.Count, dop4.PotionUses.Count);
        for (int i = 0; i < dop1.PotionUses.Count; i++)
        {
            Assert.Equal(
                (dop1.PotionUses[i].Id, dop1.PotionUses[i].Turn, dop1.PotionUses[i].Slot),
                (dop4.PotionUses[i].Id, dop4.PotionUses[i].Turn, dop4.PotionUses[i].Slot));
        }

        Assert.Equal(dop4.Won, dop4Repeat.Won);
        Assert.Equal(dop4.FinalHp, dop4Repeat.FinalHp);
        Assert.Equal(dop4.ProjectedHpLoss, dop4Repeat.ProjectedHpLoss);
        Assert.Equal(dop4.Actions.Count, dop4Repeat.Actions.Count);
        for (int i = 0; i < dop4.Actions.Count; i++)
        {
            Assert.Equal(
                (dop4.Actions[i].Turn, dop4.Actions[i].Kind, dop4.Actions[i].CardId, dop4.Actions[i].CardOccurrence, dop4.Actions[i].TargetIndex, dop4.Actions[i].PotionId, dop4.Actions[i].PotionSlot),
                (dop4Repeat.Actions[i].Turn, dop4Repeat.Actions[i].Kind, dop4Repeat.Actions[i].CardId, dop4Repeat.Actions[i].CardOccurrence, dop4Repeat.Actions[i].TargetIndex, dop4Repeat.Actions[i].PotionId, dop4Repeat.Actions[i].PotionSlot));
        }

        CombatState replayState = await CreateAct0BossRootAsync();
        BeamReplayResult replay = await BeamSearch.ReplayAsync(replayState, dop4.Actions);
        Assert.Equal(dop4.Won, replay.Won);
        Assert.Equal(dop4.FinalHp, Math.Max(0, replayState.Players[0].Creature.CurrentHp));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(4, 8)]
    [InlineData(4, 1)]
    public async Task ForecastWaveExecutor_ChoicePrefix_ParallelismAcrossParentsAndWithinOneParent(int lanes, int parentCount)
    {
        CombatState root = await CreateMawlerCombatAsync();
        Player player = root.Players[0];
        var pcs = player.PlayerCombatState!;
        foreach (CardPile pile in new[] { pcs.Hand, pcs.DrawPile, pcs.DiscardPile, pcs.ExhaustPile })
            ClearPile(pile);
        pcs.LoseEnergy(pcs.Energy);
        CardModel prepared = CreateCard<Prepared>(player);
        prepared.Upgrade();
        prepared.BaseReplayCount = 1;
        pcs.Hand.AddInternal(prepared);
        for (int i = 0; i < 4; i++)
        {
            pcs.Hand.AddInternal(CreateCard<StrikeIronclad>(player));
            pcs.DrawPile.AddInternal(CreateCard<DefendIronclad>(player));
        }

        var policy = CombatPolicy.Default with { PotionPolicy = SolverPotionPolicy.Disabled };
        var budget = new BeamBudget(8, 64, 3, 12, 12, TimeSpan.FromMinutes(1));
        var evaluator = new StateEvaluator(root, policy);
        SearchNode[] parents = Enumerable.Range(0, parentCount)
            .Select(_ => SearchNode.Create(root.Clone(), null, null, evaluator)).ToArray();
        var play = Assert.Single(ActionGenerator.GenerateForForecast(root, policy), a => a.Kind == "PlayCard");
        var serial = ChoiceExpansion.ExpandSync(parents[0], play, budget, evaluator, () => true,
            Stopwatch.StartNew(), CancellationToken.None);
        Assert.Equal(budget.MaxCardBranches, serial.Children.Count);
        Assert.All(serial.Children, n => Assert.Equal(2, n.Action!.ChoiceIndices.Count));
        var expected = ForecastWaveExecutor.DeduplicateCardChildren(serial.Children, budget.MaxCardBranches);
        static string Describe(SearchNode n) => JsonSerializer.Serialize(new { n.Fingerprint, n.Action });

        using var pool = new ForecastLanePool(lanes);
        List<SearchNode> Run(CancellationToken ct, Action<PlannedAction, CombatSolver.ChoiceScriptNode?>? before = null)
        {
            var executor = new ForecastWaveExecutor(pool, budget, policy, evaluator,
                new BeamPassOptions { UseParentNodeBudget = true },
                new MemberBudgetController(budget.NodeLimit, null), null, Stopwatch.StartNew(),
                new ForecastNodeBudgetTracker(budget.NodeLimit), null, ct, beforeChoiceAttempt: before);
            var next = new List<SearchNode>();
            var boundary = ForecastSearchBoundary.None;
            bool pending = false, won = false;
            executor.RunWave(parents, 0, [], [], next, [], ref boundary, ref pending, ref won, _ => { });
            if (parentCount > 1)
            {
                // In the wide wave each slot does exactly the serial work, plus one EndTurn per parent.
                Assert.Equal((serial.Attempts + 1) * parents.Length, executor.SharedAttempts);
                Assert.Equal((serial.Children.Count + 1) * parents.Length, executor.SharedTransitions);
            }
            Assert.Equal(ForecastSearchBoundary.None, boundary);
            return next;
        }

        using var siblingStarted = new ManualResetEventSlim();
        void WaitForSibling(PlannedAction action, CombatSolver.ChoiceScriptNode? script)
        {
            if (action.CardId != "PREPARED" || script is not { Parent: null }) return;
            if (script.BranchOrdinal == 0)
                Assert.True(siblingStarted.Wait(TimeSpan.FromSeconds(5)), "A narrow wave must allow a sibling to run.");
            else
                siblingStarted.Set();
        }
        var children = Run(CancellationToken.None, parentCount == 1 ? WaitForSibling : null);
        Assert.Equal(parents.SelectMany(_ => expected).Select(Describe), children.Select(Describe));

        var fault = new InvalidOperationException("nested choice fault");
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => Run(CancellationToken.None,
            (_, script) => { if (script?.Parent is not null) throw fault; })));
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Run(stop.Token,
            (_, script) => { if (script?.Parent is not null) stop.Cancel(); }));
    }

    [Fact]
    public async Task ForecastWaveExecutor_PrunedLateOrdinalWin_DoesNotPublishAcceptableVictoryOrCrash()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-PRUNED-WIN", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);

        // 赋予 Mawler 额外力量（首回合 38 伤），使仅出 DefendRegent 无法活过第 1 回合；
        // 只有第 1 回合选 Dualcast（ordinal 1，Double-Evoke 16 伤）才能击杀获胜。
        player.Creature.SetCurrentHpInternal(10);
        baseCombat.Enemies[0].SetCurrentHpInternal(12);
        await PowerCmd.Apply<StrengthPower>(baseCombat, baseCombat.Enemies[0], 15m, baseCombat.Enemies[0], null);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy stopPolicy = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        var prunedBudget = new BeamBudget(8, 200, MaxCardBranches: 1, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));

        foreach (int dop in new[] { 1, 4 })
        {
            for (int iter = 0; iter < 5; iter++)
            {
                ForecastPassOutcome outcome = await BeamSearch.RunSingleForecastPassAsync(
                    baseCombat.Clone(),
                    prunedBudget,
                    stopPolicy,
                    CancellationToken.None,
                    maxDegreeOfParallelism: dop);
                Assert.False(outcome.AcceptableVictoryFound);
                Assert.False(outcome.Selected.Won);
            }
        }

        // MaxCardBranches = 2 时保留 ordinal 1 的 Dualcast 致死分支，应当命中可接受胜利。
        var unprunedBudget = prunedBudget with { MaxCardBranches = 2 };
        ForecastPassOutcome winOutcome = await BeamSearch.RunSingleForecastPassAsync(
            baseCombat.Clone(),
            unprunedBudget,
            stopPolicy,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);
        Assert.True(winOutcome.AcceptableVictoryFound);
        Assert.True(winOutcome.Selected.Won);
    }

    [Fact]
    public async Task ForecastWaveExecutor_TruncatedOutOfCapVictory_NeverLeaksIntoInCombatSearchTracker()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ModelDb.Prewarm();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-TRACKER-GATE", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);

        player.Creature.SetCurrentHpInternal(10);
        baseCombat.Enemies[0].SetCurrentHpInternal(12);
        await PowerCmd.Apply<StrengthPower>(baseCombat, baseCombat.Enemies[0], 15m, baseCombat.Enemies[0], null);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy stopPolicy = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        var prunedBudget = new BeamBudget(8, 200, MaxCardBranches: 1, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));

        // 即使在 CommitWave 之前直接读取处于并发槽位里的瞬时进度，超限被裁的 ordinal 1（Dualcast 致死）也不得泄漏至 Tracker。
        for (int iter = 0; iter < 5; iter++)
        {
            CombatState rootState = baseCombat.Clone();
            var evaluator = new StateEvaluator(rootState, stopPolicy);
            SearchNode start = SearchNode.Create(rootState.Clone(), null, null, evaluator);
            using var trackerPool = new ForecastLanePool(4);
            var tracker = new InCombatSearchTracker(rootState, 0, Stopwatch.StartNew(), progressCallback: null);
            InCombatPassTrack track = tracker.RegisterPass();
            var nodeBudget = new ForecastNodeBudgetTracker(prunedBudget.NodeLimit);
            track.AttachBudgetTracker(nodeBudget);
            track.AttachEvaluator(evaluator, rootState.Players[0].Creature.CurrentHp, laneCount: 4);

            var executor = new ForecastWaveExecutor(
                trackerPool,
                prunedBudget,
                stopPolicy,
                evaluator,
                openingPreRootBattleHpLost: 0,
                Stopwatch.StartNew(),
                nodeBudget,
                track,
                CancellationToken.None);

            var completed = new List<SearchNode>();
            var ended = new List<SearchNode>();
            var next = new List<SearchNode>();
            var truncatedParents = new List<SearchNode>();
            ForecastSearchBoundary boundary = ForecastSearchBoundary.None;
            bool hitPending = false;
            bool acceptableVictoryFound = false;

            executor.RunWave(
                [start],
                layer: 0,
                completed,
                ended,
                next,
                truncatedParents,
                ref boundary,
                ref hitPending,
                ref acceptableVictoryFound,
                _ => { });

            Assert.False(acceptableVictoryFound);
            InCombatBeamResult? preCommitPreview = tracker.TryBuildBestSoFarResult();
            Assert.NotNull(preCommitPreview);
            Assert.False(preCommitPreview!.Terminal.AllEnemiesDead);
            Assert.All(preCommitPreview.Actions, a =>
            {
                Assert.DoesNotContain("DUALCAST", a.CardId, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    a.Choices.SelectMany(c => c.Cards),
                    token => token.CardId.Contains("DUALCAST", StringComparison.Ordinal));
            });
            tracker.StopAndJoin();
        }

        // 对照组：MaxCardBranches: 2 保留 ordinal 1（Dualcast），同一局面应找到致死胜路并写入 Tracker。
        CombatState winRootState = baseCombat.Clone();
        var winEvaluator = new StateEvaluator(winRootState, stopPolicy);
        SearchNode winStart = SearchNode.Create(winRootState.Clone(), null, null, winEvaluator);
        var retainedBudget = new BeamBudget(8, 200, MaxCardBranches: 2, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));
        using var winPool = new ForecastLanePool(4);
        var winTracker = new InCombatSearchTracker(winRootState, 0, Stopwatch.StartNew(), progressCallback: null);
        InCombatPassTrack winTrack = winTracker.RegisterPass();
        var winNodeBudget = new ForecastNodeBudgetTracker(retainedBudget.NodeLimit);
        winTrack.AttachBudgetTracker(winNodeBudget);
        winTrack.AttachEvaluator(winEvaluator, winRootState.Players[0].Creature.CurrentHp, laneCount: 4);

        var winExecutor = new ForecastWaveExecutor(
            winPool,
            retainedBudget,
            stopPolicy,
            winEvaluator,
            openingPreRootBattleHpLost: 0,
            Stopwatch.StartNew(),
            winNodeBudget,
            winTrack,
            CancellationToken.None);

        var winCompleted = new List<SearchNode>();
        var winEnded = new List<SearchNode>();
        var winNext = new List<SearchNode>();
        var winTruncated = new List<SearchNode>();
        ForecastSearchBoundary winBoundary = ForecastSearchBoundary.None;
        bool winHitPending = false;
        bool winVictoryFound = false;

        winExecutor.RunWave(
            [winStart],
            layer: 0,
            winCompleted,
            winEnded,
            winNext,
            winTruncated,
            ref winBoundary,
            ref winHitPending,
            ref winVictoryFound,
            _ => { });

        Assert.True(winVictoryFound);
        InCombatBeamResult? retainedPreview = winTracker.TryBuildBestSoFarResult();
        Assert.NotNull(retainedPreview);
        Assert.True(retainedPreview!.Terminal.AllEnemiesDead);
        Assert.Contains(
            retainedPreview.Actions.SelectMany(a => a.Choices).SelectMany(c => c.Cards),
            token => token.CardId.Contains("DUALCAST", StringComparison.Ordinal));
        winTracker.StopAndJoin();
    }

    [Fact]
    public async Task ForecastWaveExecutor_CutoffWithOutOfCapHigherOrdinal_ReconcilesOnlyProvenDfsPrefix()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ModelDb.Prewarm();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S4-DFS-PREFIX-CUTOFF", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);

        player.Creature.SetCurrentHpInternal(10);
        baseCombat.Enemies[0].SetCurrentHpInternal(12);
        await PowerCmd.Apply<StrengthPower>(baseCombat, baseCombat.Enemies[0], 15m, baseCombat.Enemies[0], null);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy stopPolicy = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };

        int[] tightCaps = [2, 3, 4];
        foreach (int cap in tightCaps)
        {
            for (int iter = 0; iter < 5; iter++)
            {
                CombatState rootState = baseCombat.Clone();
                var evaluator = new StateEvaluator(rootState, stopPolicy);
                SearchNode start = SearchNode.Create(rootState.Clone(), null, null, evaluator);
                var tightBudget = new BeamBudget(8, cap, MaxCardBranches: 1, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));
                using var pool = new ForecastLanePool(4);
                var nodeBudget = new ForecastNodeBudgetTracker(tightBudget.NodeLimit);

                var executor = new ForecastWaveExecutor(
                    pool,
                    tightBudget,
                    stopPolicy,
                    evaluator,
                    openingPreRootBattleHpLost: 0,
                    Stopwatch.StartNew(),
                    nodeBudget,
                    track: null,
                    CancellationToken.None);

                var completed = new List<SearchNode>();
                var ended = new List<SearchNode>();
                var next = new List<SearchNode>();
                var truncatedParents = new List<SearchNode>();
                ForecastSearchBoundary boundary = ForecastSearchBoundary.None;
                bool hitPending = false;
                bool acceptableVictoryFound = false;

                executor.RunWave(
                    [start],
                    layer: 0,
                    completed,
                    ended,
                    next,
                    truncatedParents,
                    ref boundary,
                    ref hitPending,
                    ref acceptableVictoryFound,
                    _ => { });

                Assert.False(acceptableVictoryFound);
                foreach (SearchNode node in completed.Concat(ended).Concat(next))
                {
                    if (node.Action is not null)
                    {
                        Assert.DoesNotContain(
                            node.Action.ChoiceCardIds,
                            id => id.Contains("DUALCAST", StringComparison.Ordinal));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task ForecastWaveExecutor_CutoffWithUnresolvedOrdinal0_DoesNotPublishOrRetainStolenOrdinal1()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ModelDb.Prewarm();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-CONTIGUOUS-PREFIX", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);

        player.Creature.SetCurrentHpInternal(10);
        baseCombat.Enemies[0].SetCurrentHpInternal(12);
        await PowerCmd.Apply<StrengthPower>(baseCombat, baseCombat.Enemies[0], 15m, baseCombat.Enemies[0], null);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy stopPolicy = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };

        // 1) 当 NodeLimit=2 截断波次时：ordinal 1（被窃取并发执行并击杀敌人）先占满第 2 个预算槽位，
        //    ordinal 0 与 EndTurn 在到达 TryReserveAttempt 前等待直至预算耗尽。
        //    即使 MaxCardBranches 为 1 或 2，由于 ordinal 0 仍为 Unresolved，ordinal 1 绝不得被发布或保留。
        foreach (int maxCardBranches in new[] { 1, 2 })
        {
            CombatState rootState = baseCombat.Clone();
            var evaluator = new StateEvaluator(rootState, stopPolicy);
            SearchNode start = SearchNode.Create(rootState.Clone(), null, null, evaluator);
            var cutoffBudget = new BeamBudget(8, 2, MaxCardBranches: maxCardBranches, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));
            using var pool = new ForecastLanePool(4);
            var tracker = new InCombatSearchTracker(rootState, 0, Stopwatch.StartNew(), progressCallback: null);
            InCombatPassTrack track = tracker.RegisterPass();
            var nodeBudget = new ForecastNodeBudgetTracker(cutoffBudget.NodeLimit);
            track.AttachBudgetTracker(nodeBudget);
            track.AttachEvaluator(evaluator, rootState.Players[0].Creature.CurrentHp, laneCount: 4);

            int ordinal1Attempted = 0;
            var hookWatch = Stopwatch.StartNew();
            var executor = new ForecastWaveExecutor(
                pool,
                cutoffBudget,
                stopPolicy,
                evaluator,
                openingPreRootBattleHpLost: 0,
                Stopwatch.StartNew(),
                nodeBudget,
                track,
                CancellationToken.None,
                beforeChoiceAttempt: (action, script) =>
                {
                    if (script is { BranchOrdinal: 1 })
                    {
                        Interlocked.Increment(ref ordinal1Attempted);
                        return;
                    }
                    if (action.Kind == "EndTurn" || script is { BranchOrdinal: 0 })
                    {
                        var spin = new SpinWait();
                        while (nodeBudget.Consumed < 2 && hookWatch.Elapsed < TimeSpan.FromSeconds(5))
                            spin.SpinOnce();
                    }
                });

            var completed = new List<SearchNode>();
            var ended = new List<SearchNode>();
            var next = new List<SearchNode>();
            var truncatedParents = new List<SearchNode>();
            ForecastSearchBoundary boundary = ForecastSearchBoundary.None;
            bool hitPending = false;
            bool acceptableVictoryFound = false;

            executor.RunWave(
                [start],
                layer: 0,
                completed,
                ended,
                next,
                truncatedParents,
                ref boundary,
                ref hitPending,
                ref acceptableVictoryFound,
                _ => { });

            Assert.Equal(1, Volatile.Read(ref ordinal1Attempted));
            Assert.Equal(2, nodeBudget.Consumed);
            Assert.False(acceptableVictoryFound);
            Assert.Empty(completed);
            Assert.Empty(ended);
            Assert.Empty(next);
            Assert.Single(truncatedParents, start);
            Assert.Equal(ForecastSearchBoundary.NodeLimit, executor.LimitBoundary);
            Assert.Null(tracker.TryBuildBestSoFarResult());
            tracker.StopAndJoin();
        }

        // 2) 当 NodeLimit=3 且 MaxCardBranches=1 时：ordinal 1 先占满第 2 槽完成，
        //    随后 ordinal 0 占满第 3 槽完成 DefendRegent，EndTurn 触发 NodeLimit 截断；
        //    协调线程必须按连续 DFS 前缀仅保留 ordinal 0（DefendRegent），绝不保留 ordinal 1（Dualcast）。
        {
            CombatState rootState = baseCombat.Clone();
            var evaluator = new StateEvaluator(rootState, stopPolicy);
            SearchNode start = SearchNode.Create(rootState.Clone(), null, null, evaluator);
            var prefixBudget = new BeamBudget(8, 3, MaxCardBranches: 1, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));
            using var pool = new ForecastLanePool(4);
            var tracker = new InCombatSearchTracker(rootState, 0, Stopwatch.StartNew(), progressCallback: null);
            InCombatPassTrack track = tracker.RegisterPass();
            var nodeBudget = new ForecastNodeBudgetTracker(prefixBudget.NodeLimit);
            track.AttachBudgetTracker(nodeBudget);
            track.AttachEvaluator(evaluator, rootState.Players[0].Creature.CurrentHp, laneCount: 4);

            int ordinal1Attempted = 0;
            var hookWatch = Stopwatch.StartNew();
            var executor = new ForecastWaveExecutor(
                pool,
                prefixBudget,
                stopPolicy,
                evaluator,
                openingPreRootBattleHpLost: 0,
                Stopwatch.StartNew(),
                nodeBudget,
                track,
                CancellationToken.None,
                beforeChoiceAttempt: (action, script) =>
                {
                    if (script is { BranchOrdinal: 1 })
                    {
                        Interlocked.Increment(ref ordinal1Attempted);
                        return;
                    }
                    if (script is { BranchOrdinal: 0 })
                    {
                        var spin = new SpinWait();
                        while (nodeBudget.Consumed < 2 && hookWatch.Elapsed < TimeSpan.FromSeconds(5))
                            spin.SpinOnce();
                        return;
                    }
                    if (action.Kind == "EndTurn")
                    {
                        var spin = new SpinWait();
                        while (nodeBudget.Consumed < 3 && hookWatch.Elapsed < TimeSpan.FromSeconds(5))
                            spin.SpinOnce();
                    }
                });

            var completed = new List<SearchNode>();
            var ended = new List<SearchNode>();
            var next = new List<SearchNode>();
            var truncatedParents = new List<SearchNode>();
            ForecastSearchBoundary boundary = ForecastSearchBoundary.None;
            bool hitPending = false;
            bool acceptableVictoryFound = false;

            executor.RunWave(
                [start],
                layer: 0,
                completed,
                ended,
                next,
                truncatedParents,
                ref boundary,
                ref hitPending,
                ref acceptableVictoryFound,
                _ => { });

            Assert.Equal(1, Volatile.Read(ref ordinal1Attempted));
            Assert.Equal(3, nodeBudget.Consumed);
            Assert.False(acceptableVictoryFound);
            Assert.Empty(completed);
            Assert.Empty(ended);
            SearchNode retained = Assert.Single(next);
            Assert.Contains("DEFEND_REGENT", retained.Action!.ChoiceCardIds);
            Assert.DoesNotContain("DUALCAST", retained.Action.ChoiceCardIds);
            InCombatBeamResult? preview = tracker.TryBuildBestSoFarResult();
            Assert.NotNull(preview);
            Assert.False(preview!.Terminal.AllEnemiesDead);
            Assert.All(preview.Actions, a =>
            {
                Assert.DoesNotContain("DUALCAST", a.CardId, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    a.Choices.SelectMany(c => c.Cards),
                    token => token.CardId.Contains("DUALCAST", StringComparison.Ordinal));
            });
            tracker.StopAndJoin();
        }
    }

    [Fact]
    public async Task ForecastWaveExecutor_MaxCardBranchesIntMaxValue_DoesNotThrowOutOfMemory()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ModelDb.Prewarm();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S4-MAX-CARD-BRANCHES", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy policy = CombatPolicy.Default;
        var maxBranchBudget = new BeamBudget(
            BeamWidth: 8,
            NodeLimit: 20,
            MaxCardBranches: int.MaxValue,
            MaxPileChoiceBranches: 4,
            MaxHandChoiceBranches: 4,
            TimeLimit: TimeSpan.FromSeconds(10));

        CombatState waveRoot = baseCombat.Clone();
        var evaluator = new StateEvaluator(waveRoot, policy);
        SearchNode start = SearchNode.Create(waveRoot.Clone(), null, null, evaluator);
        using var pool = new ForecastLanePool(4);
        var nodeBudget = new ForecastNodeBudgetTracker(maxBranchBudget.NodeLimit);
        var executor = new ForecastWaveExecutor(
            pool,
            maxBranchBudget,
            policy,
            evaluator,
            openingPreRootBattleHpLost: 0,
            Stopwatch.StartNew(),
            nodeBudget,
            track: null,
            CancellationToken.None);

        var completed = new List<SearchNode>();
        var ended = new List<SearchNode>();
        var next = new List<SearchNode>();
        var truncatedParents = new List<SearchNode>();
        ForecastSearchBoundary boundary = ForecastSearchBoundary.None;
        bool hitPending = false;
        bool acceptableVictoryFound = false;

        executor.RunWave(
            [start],
            layer: 0,
            completed,
            ended,
            next,
            truncatedParents,
            ref boundary,
            ref hitPending,
            ref acceptableVictoryFound,
            _ => { });

        Assert.True(completed.Count + ended.Count + next.Count > 0);
        Assert.True(executor.SharedTransitions > 0);

        BeamForecastResult forecast = await BeamSearch.SolveForForecastAsync(
            baseCombat.Clone(),
            maxBranchBudget,
            policy,
            new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true, PortfolioOptionsVersion: 2),
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);
        Assert.True(forecast.ExpandedNodes > 0);
    }

    [Fact]
    public async Task ForecastWaveExecutor_MaxCardBranchesIntMaxValue_AllocatesSafelyAndMatchesBoundedCap()
    {
        TestSaveBuilder.EnsurePrewarmed();
        ModelDb.Prewarm();
        RunState run = TestSaveBuilder.CreateRun(seed: "PR-S3-INTMAX-CAP", characterId: "CHARACTER.REGENT", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        CombatState baseCombat = entered.Room.Engine.State;
        Player player = baseCombat.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.LoseEnergy(pcs.Energy);
        pcs.GainStars(6m);
        pcs.OrbQueue.AddCapacity(1);
        await OrbCmd.Channel<LightningOrb>(baseCombat, player);

        player.Creature.SetCurrentHpInternal(10);
        baseCombat.Enemies[0].SetCurrentHpInternal(12);
        await PowerCmd.Apply<StrengthPower>(baseCombat, baseCombat.Enemies[0], 15m, baseCombat.Enemies[0], null);
        pcs.Hand.AddInternal(CreateCard<DecisionsDecisions>(player));
        pcs.Hand.AddInternal(CreateCard<DefendRegent>(player));
        pcs.Hand.AddInternal(CreateCard<Dualcast>(player));

        CombatPolicy policy = CombatPolicy.Default;
        var bounded = new BeamBudget(16, 500, MaxCardBranches: 64, MaxPileChoiceBranches: 4, MaxHandChoiceBranches: 4, TimeSpan.FromSeconds(10));
        var unboundedCardBranches = bounded with { MaxCardBranches = int.MaxValue };
        var unboundedBoth = bounded with { BeamWidth = int.MaxValue, MaxCardBranches = int.MaxValue };

        BeamForecastResult boundedResult = await BeamSearch.SolveForForecastAsync(
            baseCombat.Clone(),
            bounded,
            policy,
            new PreCombatPortfolioOptions(UseBeamWidthPortfolio: false),
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);
        BeamForecastResult unboundedCardResult = await BeamSearch.SolveForForecastAsync(
            baseCombat.Clone(),
            unboundedCardBranches,
            policy,
            new PreCombatPortfolioOptions(UseBeamWidthPortfolio: false),
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);
        BeamForecastResult unboundedBothResult = await BeamSearch.SolveForForecastAsync(
            baseCombat.Clone(),
            unboundedBoth,
            policy,
            new PreCombatPortfolioOptions(UseBeamWidthPortfolio: true),
            openingPreRootBattleHpLost: 0,
            CancellationToken.None,
            maxDegreeOfParallelism: 4);

        Assert.True(boundedResult.Won);
        AssertMatchesBaseline(boundedResult, unboundedCardResult);
        AssertMatchesBaseline(boundedResult, unboundedBothResult);

        static void AssertMatchesBaseline(BeamForecastResult expected, BeamForecastResult actual)
        {
            Assert.Equal(expected.Won, actual.Won);
            Assert.Equal(expected.FinalHp, actual.FinalHp);
            Assert.Equal(expected.ProjectedHpLoss, actual.ProjectedHpLoss);
            Assert.Equal(expected.Actions.Count, actual.Actions.Count);
            for (int i = 0; i < expected.Actions.Count; i++)
            {
                PlannedAction exp = expected.Actions[i];
                PlannedAction act = actual.Actions[i];
                Assert.Equal((exp.Turn, exp.Kind, exp.CardId), (act.Turn, act.Kind, act.CardId));
                Assert.Equal(exp.ChoiceCardIds, act.ChoiceCardIds);
            }
        }
    }

    [Fact]
    public async Task InCombatBridge_BlockedProgressCallback_RespectsHardDeadlineWithoutHanging()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 10);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("PHROG_PARASITE_ELITE", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        InCombatLiveStateDto liveDto = SyntheticLiveCombat.CreateSnapshot(entered.Room, "PHROG_PARASITE_ELITE");
        var policy = new InCombatPolicyDto(
            PotionPolicy: "Smart",
            PotionDirectives: [],
            PredictPotionReward: false,
            RelicStrategyEnabled: true,
            RelicCounterRules: [],
            GrowthBudgets: new BridgeGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
            BrightestFlameMaxHpLossLimit: null,
            IgnoreLongTermRewards: false,
            ActTransitionBossHpStrategy: "ProgressionFirst",
            FinalBossHpStrategy: "ProgressionFirst",
            AcceptableBattleHpLoss: 0,
            StopAtAcceptableBattleHpLoss: false,
            UseBeamWidthPortfolio: true,
            UseNoveltyPortfolio: false,
            UseEarlyTurnExploration: false,
            BeamWidth: 48,
            PotionFreeBeamWidth: null,
            PotionBeamWidth: null,
            MaxExpandedNodes: 500_000,
            MaxCardBranchesPerNode: 12,
            MaxPileChoiceBranchesPerAction: 6,
            MaxHandChoiceBranchesPerAction: 6,
            SoftTimeBudgetMilliseconds: 10_000,
            TheftPolicy: null);

        var req = new InCombatBridgeRequestDto(
            AbiVersion: InCombatBridgeAbi.Version,
            RequestId: Guid.NewGuid().ToString("N"),
            NormalizedSaveJson: saveJson,
            LiveState: liveDto,
            PriorBattleHpLost: 0,
            SearchBudgetMilliseconds: 10_000,
            OverallTimeoutMilliseconds: 350,
            MaxDegreeOfParallelism: 4,
            Policy: policy);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeAbi.JsonOptions);
        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        int returnedFlag = 0;
        int callbackCount = 0;
        int postReturnCallbacks = 0;

        byte[] respBytes = await BridgeEntry.RunInCombatJsonAsync(
            payload,
            _ =>
            {
                if (Volatile.Read(ref returnedFlag) != 0)
                {
                    Interlocked.Increment(ref postReturnCallbacks);
                    return;
                }
                if (Interlocked.Increment(ref callbackCount) == 1)
                {
                    callbackEntered.Set();
                    releaseCallback.Wait(TimeSpan.FromSeconds(15));
                }
            },
            CancellationToken.None);

        Volatile.Write(ref returnedFlag, 1);
        InCombatBridgeResponseDto resp = JsonSerializer.Deserialize<InCombatBridgeResponseDto>(respBytes, BridgeAbi.JsonOptions)!;
        Assert.True(callbackEntered.IsSet);
        Assert.Equal("TimedOut", resp.Outcome);
        Assert.True(resp.TotalElapsedMilliseconds < 2500, $"Blocked callback bypassed hard deadline: {resp.TotalElapsedMilliseconds}ms");

        releaseCallback.Set();
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref postReturnCallbacks));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(100, false)]
    public async Task InCombat_BudgetedDeathCanYieldToAnExecutedEnemyTurn(int nodeLimit, bool bounded)
    {
        CombatState root = await CreateMawlerCombatAsync();
        Player player = root.Players[0];
        var pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.Hand.AddInternal(CreateCard<Impervious>(player));
        player.Creature.SetCurrentHpInternal(6);
        await PowerCmd.Apply<BarricadePower>(root, player.Creature, 1m, player.Creature, null);

        CombatPolicy policy = CombatPolicy.Default with { PotionPolicy = SolverPotionPolicy.Disabled };
        var budget = new BeamBudget(4, nodeLimit, 4, 4, 4, TimeSpan.FromMinutes(1));
        var portfolio = new PreCombatPortfolioOptions(false, PortfolioOptionsVersion: 2);
        var result = await BeamSearch.SolveForInCombatAsync(root, budget, policy, portfolio,
            priorBattleHpLost: 0, progressCallback: null, CancellationToken.None);
        Assert.Equal(!bounded, result.Terminal.PlayerDead);
        if (bounded)
        {
            Assert.Equal(ForecastSearchBoundary.NodeLimit, result.Terminal.SearchBoundary);
            Assert.True(result.Terminal.ProjectedPlayerHp > 0);
            Assert.Equal("EndTurn", result.Actions[^1].Kind);
            Assert.True(result.Terminal.Turn > result.Actions[^1].Turn);
            Assert.Contains(result.Actions, action => action.CardId == "IMPERVIOUS");
            Assert.Equal(nodeLimit, result.ExpandedParents);

            var evaluator = new StateEvaluator(root, policy);
            var start = SearchNode.Create(root.Clone(), null, null, evaluator);
            async Task<SearchNode> Step(SearchNode parent, string kind)
            {
                PlannedAction action = ActionGenerator.GenerateForForecast(parent.State, policy).Single(a => a.Kind == kind);
                CombatState child = parent.State.Clone();
                await ActionExecutor.ExecuteAsync(child, action, action.ChoiceIndices);
                return SearchNode.Create(child, parent, action, evaluator);
            }
            SearchNode death = await Step(start, "EndTurn");
            SearchNode afterDefense = await Step(start, "PlayCard");
            SearchNode survivor = await Step(afterDefense, "EndTurn");
            Assert.Same(death, PlanSelector.Select([death, survivor], 6, evaluator));
            var tracker = new InCombatSearchTracker(root, 0, Stopwatch.StartNew(), progressCallback: null);
            var pass = tracker.RegisterPass();
            pass.AttachEvaluator(evaluator, 6);
            pass.CommitWave([death], [survivor], [afterDefense], [], null, null, ForecastSearchBoundary.NodeLimit);
            var preview = Assert.IsType<InCombatBeamResult>(tracker.TryBuildBestSoFarResult());
            Assert.Equal(JsonSerializer.Serialize(result.Actions), JsonSerializer.Serialize(preview.Actions));
            Assert.Equal(result.Terminal, preview.Terminal);

            ClearPile(pcs.Hand);
            var doomed = await BeamSearch.SolveForInCombatAsync(root, budget, policy, portfolio,
                priorBattleHpLost: 0, progressCallback: null, CancellationToken.None);
            Assert.True(doomed.Terminal.PlayerDead);
            Assert.True(doomed.Terminal.OnlyDeathRoutes);
        }
        else
        {
            Assert.Equal(ForecastSearchBoundary.None, result.Terminal.SearchBoundary);
        }
    }

    [Fact]
    public async Task InCombatSearchTracker_CrossPassSelection_PreservesPotionFreeBaselineAndUpdatesOnlyDeathRoutes()
    {
        // 1) 验证 OnlyDeathRoutes 与 ExpandedNodes 在最佳节点命中轨迹缓存时仍实时刷新：
        // Mawler A0 首回合以 Claw（4x2=8 伤）攻击；玩家设 6 HP 且手牌含 Defend（5 格挡 -> 余 3 血存活）。
        CombatState root = await CreateMawlerCombatAsync();
        Player player = root.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        ClearPile(pcs.Hand);
        ClearPile(pcs.DrawPile);
        ClearPile(pcs.DiscardPile);
        ClearPile(pcs.ExhaustPile);
        pcs.Hand.AddInternal(CreateCard<DefendIronclad>(player));
        if (pcs.Energy < 1)
            pcs.GainEnergy(1 - pcs.Energy);
        player.Creature.SetCurrentHpInternal(6);

        CombatPolicy smartPolicy = CombatPolicy.Default with { PotionPolicy = SolverPotionPolicy.Smart };
        var evaluator = new StateEvaluator(root, smartPolicy);
        int initialHp = player.Creature.CurrentHp;
        SearchNode rootNode = SearchNode.Create(root.Clone(), null, null, evaluator);

        IReadOnlyList<PlannedAction> rootActions = ActionGenerator.GenerateForForecast(root, smartPolicy);
        PlannedAction endTurnAction = rootActions.First(a => a.Kind == "EndTurn");
        PlannedAction defendAction = rootActions.First(a => a.Kind == "PlayCard" && a.CardId == "DEFEND_IRONCLAD");

        CombatState endTurnDeadState = root.Clone();
        await ActionExecutor.ExecuteAsync(endTurnDeadState, endTurnAction, endTurnAction.ChoiceIndices);
        SearchNode nodeDeadEnded = SearchNode.Create(endTurnDeadState, rootNode, endTurnAction, evaluator);
        Assert.True(nodeDeadEnded.Ended);
        Assert.False(nodeDeadEnded.Won);
        Assert.Equal(1, nodeDeadEnded.Depth);

        CombatState defendAliveState = root.Clone();
        await ActionExecutor.ExecuteAsync(defendAliveState, defendAction, defendAction.ChoiceIndices);
        SearchNode nodeAliveFrontier = SearchNode.Create(defendAliveState, rootNode, defendAction, evaluator);
        Assert.False(nodeAliveFrontier.Ended);
        Assert.True(evaluator.ComputeProjectedPlayerHp(nodeAliveFrontier, ForecastSearchBoundary.None) > 0);

        var tracker = new InCombatSearchTracker(root, 0, Stopwatch.StartNew(), progressCallback: null);
        InCombatPassTrack track0 = tracker.RegisterPass();
        InCombatPassTrack track1 = tracker.RegisterPass();
        track0.AttachEvaluator(evaluator, initialHp);
        track1.AttachEvaluator(evaluator, initialHp);

        tracker.ObserveExpansion(10, 1);
        track0.CommitWave(
            completed: [nodeDeadEnded],
            frontierOrEnded: [],
            lastActive: [],
            truncatedParents: [],
            potionFreeBoundaryFallback: null,
            potionBoundaryFallback: null,
            passBoundary: ForecastSearchBoundary.None);

        InCombatBeamResult? firstPreview = tracker.TryBuildBestSoFarResult();
        Assert.NotNull(firstPreview);
        Assert.True(firstPreview!.Terminal.OnlyDeathRoutes);
        Assert.Equal(ForecastConfidence.DeathOnly, firstPreview.Terminal.Confidence);
        Assert.Equal(10, firstPreview.ExpandedNodes);

        // Pass 1 提交仍存活的前沿节点（Ended == false，PlanSelector 仍选 Ended == true 的 nodeDeadEnded，命中路由缓存）；
        // 即使最佳选中节点不变，OnlyDeathRoutes 与 ExpandedNodes 也必须刷新为 false / 35。
        tracker.ObserveExpansion(25, 1);
        track1.CommitWave(
            completed: [],
            frontierOrEnded: [nodeAliveFrontier],
            lastActive: [],
            truncatedParents: [],
            potionFreeBoundaryFallback: null,
            potionBoundaryFallback: null,
            passBoundary: ForecastSearchBoundary.None);

        InCombatBeamResult? secondPreview = tracker.TryBuildBestSoFarResult();
        Assert.NotNull(secondPreview);
        Assert.False(secondPreview!.Terminal.OnlyDeathRoutes);
        Assert.NotEqual(ForecastConfidence.DeathOnly, secondPreview.Terminal.Confidence);
        Assert.True(secondPreview.Terminal.PlayerDead); // Defend by itself has not crossed the enemy turn.
        Assert.Equal(35, secondPreview.ExpandedNodes);

        // 2) 跨 Pass Smart 药水过滤：Pass 0 有 0 掉血喝 FirePotion 胜利（成本 9），
        // Pass 1 有 Hemokinesis 自伤 2 掉血的零药水胜利（省 2 < 9）；
        // 合并跨 Pass 选择必须识别零药水基线并淘汰不划算的喝药胜利。
        CombatState potionRoot = await CreateMawlerCombatAsync();
        Player potionPlayer = potionRoot.Players[0];
        PlayerCombatState potionPcs = potionPlayer.PlayerCombatState!;
        var firePotion = (PotionModel)ModelDb.Potion<FirePotion>().MutableClone();
        firePotion.AssignOwner(potionPlayer);
        potionPlayer.AddPotionInternal(firePotion);
        ClearPile(potionPcs.Hand);
        ClearPile(potionPcs.DrawPile);
        ClearPile(potionPcs.DiscardPile);
        ClearPile(potionPcs.ExhaustPile);
        potionPcs.Hand.AddInternal(CreateCard<Hemokinesis>(potionPlayer));
        if (potionPcs.Energy < 1)
            potionPcs.GainEnergy(1 - potionPcs.Energy);
        potionRoot.Enemies[0].SetCurrentHpInternal(12);

        var potionEval = new StateEvaluator(potionRoot, smartPolicy);
        int potionInitialHp = potionPlayer.Creature.CurrentHp;
        SearchNode potionRootNode = SearchNode.Create(potionRoot.Clone(), null, null, potionEval);

        IReadOnlyList<PlannedAction> potionActions = ActionGenerator.GenerateForForecast(potionRoot, smartPolicy);
        PlannedAction useFirePotion = potionActions.First(a => a.Kind == "UsePotion" && a.PotionId == "FIRE_POTION");
        PlannedAction playHemokinesis = potionActions.First(a => a.Kind == "PlayCard" && a.CardId == "HEMOKINESIS");

        CombatState potionWin = potionRoot.Clone();
        await ActionExecutor.ExecuteAsync(potionWin, useFirePotion, useFirePotion.ChoiceIndices);
        SearchNode potionWinNode = SearchNode.Create(potionWin, potionRootNode, useFirePotion, potionEval);
        Assert.True(potionWinNode.Won);

        CombatState freeWin = potionRoot.Clone();
        await ActionExecutor.ExecuteAsync(freeWin, playHemokinesis, playHemokinesis.ChoiceIndices);
        SearchNode freeWinNode = SearchNode.Create(freeWin, potionRootNode, playHemokinesis, potionEval);
        Assert.True(freeWinNode.Won);
        Assert.Equal(2, potionEval.BattleHpLost(freeWinNode));

        var potionTracker = new InCombatSearchTracker(potionRoot, 0, Stopwatch.StartNew(), progressCallback: null);
        InCombatPassTrack potionPass = potionTracker.RegisterPass();
        InCombatPassTrack freePass = potionTracker.RegisterPass();
        potionPass.AttachEvaluator(potionEval, potionInitialHp);
        freePass.AttachEvaluator(potionEval, potionInitialHp);

        potionPass.CommitWave([potionWinNode], [], [], [], null, null, ForecastSearchBoundary.None);
        freePass.CommitWave([freeWinNode], [], [], [], null, null, ForecastSearchBoundary.None);

        InCombatBeamResult? crossPassBest = potionTracker.TryBuildBestSoFarResult();
        Assert.NotNull(crossPassBest);
        Assert.Equal("PlayCard", crossPassBest!.Actions[0].Kind);
        Assert.Equal("HEMOKINESIS", crossPassBest.Actions[0].CardId);
    }

    [Fact]
    public async Task Retention_SetupSignature_DistinguishesTrailingDebuffFreeEnemies()
    {
        TestSaveBuilder.EnsurePrewarmed();
        var run1 = new RunState("setup-sig-1", [new Overgrowth(), new Hive(), new Glory()], 0);
        Player p1 = Player.CreateForNewRun(ModelDb.Character<Ironclad>(), run1);
        run1.AddPlayer(p1);
        var stateOneEnemy = new CombatState(run1);
        var engine1 = new CombatEngine(stateOneEnemy);
        stateOneEnemy.AddMonster((TrainingDummy)ModelDb.Monster<TrainingDummy>().MutableClone(), CombatSide.Enemy, "e0");
        await engine1.StartCombatAsync();

        var run2 = new RunState("setup-sig-2", [new Overgrowth(), new Hive(), new Glory()], 0);
        Player p2 = Player.CreateForNewRun(ModelDb.Character<Ironclad>(), run2);
        run2.AddPlayer(p2);
        var stateTwoEnemies = new CombatState(run2);
        var engine2 = new CombatEngine(stateTwoEnemies);
        stateTwoEnemies.AddMonster((TrainingDummy)ModelDb.Monster<TrainingDummy>().MutableClone(), CombatSide.Enemy, "e0");
        stateTwoEnemies.AddMonster((TrainingDummy)ModelDb.Monster<TrainingDummy>().MutableClone(), CombatSide.Enemy, "e1");
        await engine2.StartCombatAsync();

        var evaluator = new StateEvaluator(stateOneEnemy, CombatPolicy.Default);

        // 构造 width = 4：前 3 个高分候选占用 ordinarySlots=3（均为 1 个无 Debuff 敌人）；
        // 第 4 候选仍是 1 个无 Debuff 敌人（得分高于第 5 候选但与前 3 个 Setup 签名相同），
        // 第 5 候选有 2 个无 Debuff 敌人，必须凭借不同的敌人数量 Setup 签名入选第 4 席位。
        CombatState s1a = stateOneEnemy.Clone();
        s1a.Players[0].Creature.SetCurrentHpInternal(80);
        CombatState s1b = stateOneEnemy.Clone();
        s1b.Players[0].Creature.SetCurrentHpInternal(79);
        CombatState s1c = stateOneEnemy.Clone();
        s1c.Players[0].Creature.SetCurrentHpInternal(78);
        CombatState s1DuplicateSetup = stateOneEnemy.Clone();
        s1DuplicateSetup.Players[0].Creature.SetCurrentHpInternal(60);

        CombatState s2DistinctEnemyCount = stateTwoEnemies.Clone();
        s2DistinctEnemyCount.Players[0].Creature.SetCurrentHpInternal(55);

        SearchNode n1a = SearchNode.Create(s1a, null, null, evaluator);
        SearchNode n1b = SearchNode.Create(s1b, null, null, evaluator);
        SearchNode n1c = SearchNode.Create(s1c, null, null, evaluator);
        SearchNode n1Dup = SearchNode.Create(s1DuplicateSetup, null, null, evaluator);
        SearchNode n2Distinct = SearchNode.Create(s2DistinctEnemyCount, null, null, evaluator);

        List<SearchNode> retained = Retention.SelectWithKey(
            [n1a, n1b, n1c, n1Dup, n2Distinct],
            width: 4,
            node => node.Fingerprint,
            preservePotionFree: false,
            includeParetoProgress: false,
            aggressivePowerCommitment: false);

        Assert.Equal(4, retained.Count);
        Assert.Contains(n2Distinct, retained);
        Assert.DoesNotContain(n1Dup, retained);
    }

    [Fact]
    public async Task LiveCombatTransplantMapper_NegativeOrOverflowingEnergySpent_FailsWithBridgeRequestFailed()
    {
        (_, InCombatLiveStateDto baseDto, _) = await SyntheticLiveCombat.BuildTurn1CombatAsync("NIBBITS_WEAK");

        InCombatLiveStateDto negativeDto = baseDto with
        {
            SemanticHistory = baseDto.SemanticHistory with
            {
                EnergySpent =
                [
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(1, "Player", [1]), 0, -1),
                ],
            },
        };
        BridgeRequestFailedException negativeEx = Assert.Throws<BridgeRequestFailedException>(
            () => LiveCombatTransplantMapper.Map(negativeDto));
        Assert.Equal("liveState.semanticHistory.energySpent[0].amount", negativeEx.FieldPath);

        InCombatLiveStateDto overflowDto = baseDto with
        {
            SemanticHistory = baseDto.SemanticHistory with
            {
                EnergySpent =
                [
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(1, "Player", [1]), 0, int.MaxValue - 1),
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(1, "Player", [1]), 0, 5),
                ],
            },
        };
        BridgeRequestFailedException overflowEx = Assert.Throws<BridgeRequestFailedException>(
            () => LiveCombatTransplantMapper.Map(overflowDto));
        Assert.Equal("liveState.semanticHistory.energySpent[1].amount", overflowEx.FieldPath);

        // 不同回合各自接近上限不应误报溢出。
        InCombatLiveStateDto distinctTurnsDto = baseDto with
        {
            SemanticHistory = baseDto.SemanticHistory with
            {
                EnergySpent =
                [
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(1, "Player", [1]), 0, int.MaxValue - 1),
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(2, "Player", [2]), 0, 5),
                ],
            },
        };
        LiveCombatSnapshotSpec okSpec = LiveCombatTransplantMapper.Map(distinctTurnsDto);
        Assert.Equal(2, okSpec.SemanticHistory.EnergySpent.Count);

        // 独立实例且含相同 PlayerTurnNumbers 列表时仍按结构键聚合累加并捕获溢出。
        InCombatLiveStateDto structuralOverflowDto = baseDto with
        {
            SemanticHistory = baseDto.SemanticHistory with
            {
                EnergySpent =
                [
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(7, "Player", [3, null]), 0, int.MaxValue - 2),
                    new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(7, "Player", [3, null]), 0, 10),
                ],
            },
        };
        BridgeRequestFailedException structuralOverflowEx = Assert.Throws<BridgeRequestFailedException>(
            () => LiveCombatTransplantMapper.Map(structuralOverflowDto));
        Assert.Equal("liveState.semanticHistory.energySpent[1].amount", structuralOverflowEx.FieldPath);

        // 5,000 条不同回合记录在线性时间内映射完毕。
        var manyEntries = new InCombatEnergySpentHistoryDto[5000];
        for (int i = 0; i < manyEntries.Length; i++)
            manyEntries[i] = new InCombatEnergySpentHistoryDto(new InCombatTurnKeyDto(i + 1, "Player", [i + 1]), 0, 1);
        InCombatLiveStateDto largeDto = baseDto with
        {
            SemanticHistory = baseDto.SemanticHistory with
            {
                EnergySpent = manyEntries,
            },
        };
        LiveCombatSnapshotSpec largeSpec = LiveCombatTransplantMapper.Map(largeDto);
        Assert.Equal(5000, largeSpec.SemanticHistory.EnergySpent.Count);

        // 已取消的 CancellationToken 在实时状态映射期间立即中止。
        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LiveCombatTransplantMapper.Map(distinctTurnsDto, cancelledCts.Token));
    }

    [Fact]
    public void ForecastLanePool_ThreadStartFailure_CleansUpPreviouslyStartedThreads()
    {
        var startedThreads = new List<Thread>();
        int attempts = 0;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var pool = new ForecastLanePool(
                laneCount: 4,
                threadStarter: thread =>
                {
                    attempts++;
                    if (attempts == 3)
                        throw new InvalidOperationException("Simulated thread creation failure.");
                    startedThreads.Add(thread);
                    thread.Start();
                });
        });

        Assert.Equal("Simulated thread creation failure.", ex.Message);
        Assert.Equal(2, startedThreads.Count);
        for (int i = 0; i < startedThreads.Count; i++)
            Assert.False(startedThreads[i].IsAlive);
    }

    private static void ClearPile(CardPile pile)
    {
        foreach (CardModel card in pile.Cards.ToArray())
            pile.RemoveInternal(card);
    }

    private static async Task<CombatState> CreateMawlerCombatAsync()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);
        return entered.Room.Engine.State;
    }

    private static CardModel CreateCard<TCard>(Player owner) where TCard : CardModel
    {
        var card = (CardModel)ModelDb.Card<TCard>().MutableClone();
        card.AssignOwner(owner);
        return card;
    }
}
