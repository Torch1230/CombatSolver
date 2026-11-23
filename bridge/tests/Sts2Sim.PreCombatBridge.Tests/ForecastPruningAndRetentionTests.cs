using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Afflictions;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class ForecastPruningAndRetentionTests
{
    [Theory]
    [InlineData(16, 5000)]
    [InlineData(60, 60000)]
    public async Task MawlerParity_IroncladSeedCombatSolver_FindsOptimal15HpLossVictory(
        int beamWidth,
        int maxExpandedNodes)
    {
        TestSaveBuilder.EnsurePrewarmed();
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "mawler-ironclad-combatsolver-save.json");
        string normalizedSaveJson = await File.ReadAllTextAsync(fixturePath);

        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: beamWidth,
            maxExpandedNodes: maxExpandedNodes,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000) with
        {
            MaxCardBranchesPerNode = 12,
        };

        var request = new BridgeRequestDto(
            AbiVersion: BridgeAbi.Version,
            RequestId: $"mawler-parity-{beamWidth}-{maxExpandedNodes}",
            NormalizedSaveJson: normalizedSaveJson,
            HasLiveSidecar: true,
            LiveActFloor: 1,
            NextRoomId: 1,
            SpoilsMapVerification: null,
            EncounterId: "MAWLER_NORMAL",
            TargetActFloor: 2,
            TargetMapColumn: 0,
            RoomKind: "Normal",
            MapPointKind: "Normal",
            IsSecondBoss: false,
            InterveningMapPoints: [],
            PlayerCurrentHpOverride: 55,
            SimulationSeed: null,
            SearchBudgetMilliseconds: 15_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 1,
            Policy: policy);

        (BridgeResponseDto response, _) = await TestSaveBuilder.RunAsync(request);

        Assert.Equal("Succeeded", response.Outcome);
        Assert.Equal(15, response.ProjectedHpLoss);
        Assert.Equal(40, response.FinalHp);
        Assert.Equal(4, response.CombatEndedTurn);
        Assert.Equal("None", response.SearchBoundary);
        Assert.Equal("Complete", response.Confidence);
        Assert.False(response.OnlyDeathRoutes);
    }

    [Fact]
    public async Task DuplicateCardPruning_PrunesAdjacentIdenticalCards_AndReducesExpandedNodes()
    {
        CombatState combat = await CreateCombatAsync("MAWLER_NORMAL");
        Player player = combat.Players[0];
        SetHand(player,
            CreateCard<StrikeIronclad>(player),
            CreateCard<StrikeIronclad>(player),
            CreateCard<StrikeIronclad>(player),
            CreateCard<DefendIronclad>(player),
            CreateCard<DefendIronclad>(player));

        IReadOnlyList<PlannedAction> unpruned = ActionGenerator.Generate(combat);
        IReadOnlyList<PlannedAction> pruned = ActionGenerator.GenerateForForecast(combat);

        Assert.Equal(3, unpruned.Count(a => a.Kind == "PlayCard" && a.CardId == "STRIKE_IRONCLAD"));
        Assert.Equal(2, unpruned.Count(a => a.Kind == "PlayCard" && a.CardId == "DEFEND_IRONCLAD"));

        Assert.Single(pruned, a => a.Kind == "PlayCard" && a.CardId == "STRIKE_IRONCLAD");
        Assert.Single(pruned, a => a.Kind == "PlayCard" && a.CardId == "DEFEND_IRONCLAD");
        Assert.Equal(0, pruned.Single(a => a.Kind == "PlayCard" && a.CardId == "STRIKE_IRONCLAD").CardOccurrence);
        Assert.Equal(0, pruned.Single(a => a.Kind == "PlayCard" && a.CardId == "DEFEND_IRONCLAD").CardOccurrence);

        var budget = new BeamBudget(16, 4000, 12, 6, 6, TimeSpan.FromSeconds(10));
        // 将敌人 HP 设为 18（三张 Strike 首回合即可击杀），并在达成 0 损血胜利时提前停止，
        // 避免在无尽后续回合中双方同时撞到同一 NodeLimit 上限。
        combat.Enemies[0].SetCurrentHpInternal(18);
        CombatPolicy stopOnZeroLoss = CombatPolicy.Default with
        {
            StopAtAcceptableBattleHpLoss = true,
            AcceptableBattleHpLoss = 0,
        };
        BeamResult regularResult = BeamSearch.Solve(
            combat.Clone(), budget, stopOnZeroLoss, CancellationToken.None);
        BeamForecastResult forecastResult = await BeamSearch.SolveForForecastAsync(
            combat.Clone(),
            budget,
            stopOnZeroLoss,
            new PreCombatPortfolioOptions(false),
            0,
            CancellationToken.None);

        Assert.True(regularResult.Won);
        Assert.True(forecastResult.Won);
        Assert.Equal(0, regularResult.HpLost);
        Assert.Equal(0, forecastResult.ProjectedHpLoss);
        Assert.True(forecastResult.ExpandedNodes < budget.NodeLimit);
        Assert.True(
            forecastResult.ExpandedNodes < regularResult.ExpandedNodes,
            $"Expected forecast expanded nodes ({forecastResult.ExpandedNodes}) < regular ({regularResult.ExpandedNodes}).");
    }

    [Fact]
    public async Task DuplicateCardPruning_DoesNotPruneUpgradedDiscountedEnchantedOrAfflictedCopies()
    {
        CombatState combat = await CreateCombatAsync("MAWLER_NORMAL");
        Player player = combat.Players[0];

        // 升级版与基础版
        StrikeIronclad upgraded = CreateCard<StrikeIronclad>(player);
        CardCmd.Upgrade(upgraded);
        SetHand(player, CreateCard<StrikeIronclad>(player), upgraded);
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "STRIKE_IRONCLAD"));

        // 临时减费与原费
        StrikeIronclad discounted = CreateCard<StrikeIronclad>(player);
        discounted.SetTemporaryCostOverrideThisTurn(0);
        SetHand(player, CreateCard<StrikeIronclad>(player), discounted);
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "STRIKE_IRONCLAD"));

        // 附魔与无附魔
        StrikeIronclad enchanted = CreateCard<StrikeIronclad>(player);
        await CardCmd.Enchant<Sharp>(enchanted, 2m);
        SetHand(player, CreateCard<StrikeIronclad>(player), enchanted);
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "STRIKE_IRONCLAD"));

        // 苦难与无苦难
        StrikeIronclad afflicted = CreateCard<StrikeIronclad>(player);
        AfflictionModel? attached = await CardCmd.Afflict<Bound>(afflicted, 1m);
        Assert.NotNull(attached);
        SetHand(player, CreateCard<StrikeIronclad>(player), afflicted);
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "STRIKE_IRONCLAD"));
    }

    [Fact]
    public async Task DuplicateCardPruning_PreservesCopiesSeparatedByDifferentPlayableCard()
    {
        CombatState combat = await CreateCombatAsync("MAWLER_NORMAL");
        Player player = combat.Players[0];

        // 被不同可打出卡牌隔开：必须保留两张 Strike 的分支及各自的 CardOccurrence。
        SetHand(player,
            CreateCard<StrikeIronclad>(player),
            CreateCard<DefendIronclad>(player),
            CreateCard<StrikeIronclad>(player));
        PlannedAction[] separatedStrikes = ActionGenerator.GenerateForForecast(combat)
            .Where(a => a.Kind == "PlayCard" && a.CardId == "STRIKE_IRONCLAD")
            .ToArray();
        Assert.Equal(2, separatedStrikes.Length);
        Assert.Equal(new[] { 0, 1 }, separatedStrikes.Select(a => a.CardOccurrence).ToArray());

        // 紧邻的相同 Strike：只保留首张代表。
        SetHand(player,
            CreateCard<StrikeIronclad>(player),
            CreateCard<StrikeIronclad>(player),
            CreateCard<DefendIronclad>(player));
        PlannedAction[] adjacentStrikes = ActionGenerator.GenerateForForecast(combat)
            .Where(a => a.Kind == "PlayCard" && a.CardId == "STRIKE_IRONCLAD")
            .ToArray();
        Assert.Single(adjacentStrikes);
        Assert.Equal(0, adjacentStrikes[0].CardOccurrence);
    }

    [Fact]
    public async Task RetentionKey_CollapsesCommutativeDiscardAndDamageOrder_ButPreservesDrawHandRngAndRelicCounters()
    {
        // 1) 可交换出牌顺序（先 Strike 后 Defend vs 先 Defend 后 Strike）：
        // 剩余手牌顺序、伤害/护甲数值与 RNG 均一致，仅弃牌堆顺序、DamageHistory 牌堆下标与 BlockGain PlayOrdinal 不同。
        CombatState root = await CreateCombatAsync("MAWLER_NORMAL");
        Player rootPlayer = root.Players[0];
        SetHand(rootPlayer,
            CreateCard<StrikeIronclad>(rootPlayer),
            CreateCard<DefendIronclad>(rootPlayer),
            CreateCard<StrikeIronclad>(rootPlayer),
            CreateCard<DefendIronclad>(rootPlayer));

        var evaluator = new StateEvaluator(root, CombatPolicy.Default);

        CombatState strikeThenDefend = root.Clone();
        await ActionExecutor.ExecuteAsync(
            strikeThenDefend,
            new PlannedAction(1, "PlayCard", "STRIKE_IRONCLAD", 0, 0, null, -1, [], []),
            []);
        await ActionExecutor.ExecuteAsync(
            strikeThenDefend,
            new PlannedAction(1, "PlayCard", "DEFEND_IRONCLAD", 0, -1, null, -1, [], []),
            []);

        CombatState defendThenStrike = root.Clone();
        await ActionExecutor.ExecuteAsync(
            defendThenStrike,
            new PlannedAction(1, "PlayCard", "DEFEND_IRONCLAD", 0, -1, null, -1, [], []),
            []);
        await ActionExecutor.ExecuteAsync(
            defendThenStrike,
            new PlannedAction(1, "PlayCard", "STRIKE_IRONCLAD", 0, 0, null, -1, [], []),
            []);

        SearchNode nodeA = SearchNode.Create(strikeThenDefend, null, null, evaluator);
        SearchNode nodeB = SearchNode.Create(defendThenStrike, null, null, evaluator);

        Assert.NotEqual(nodeA.Fingerprint, nodeB.Fingerprint);
        Assert.Equal(ForecastRetentionKey.Compute(nodeA), ForecastRetentionKey.Compute(nodeB));

        // 2) DrawPile 顺序不同 -> key 不同
        CombatState swappedDraw = root.Clone();
        PlayerCombatState drawPcs = swappedDraw.Players[0].PlayerCombatState!;
        CardModel bash = CreateCard<Bash>(swappedDraw.Players[0]);
        CardModel strike = CreateCard<StrikeIronclad>(swappedDraw.Players[0]);
        ClearPile(drawPcs.DrawPile);
        drawPcs.DrawPile.AddInternal(bash);
        drawPcs.DrawPile.AddInternal(strike);

        CombatState swappedDrawOther = root.Clone();
        PlayerCombatState drawPcsOther = swappedDrawOther.Players[0].PlayerCombatState!;
        ClearPile(drawPcsOther.DrawPile);
        drawPcsOther.DrawPile.AddInternal(CreateCard<StrikeIronclad>(swappedDrawOther.Players[0]));
        drawPcsOther.DrawPile.AddInternal(CreateCard<Bash>(swappedDrawOther.Players[0]));

        Assert.NotEqual(
            ForecastRetentionKey.Compute(SearchNode.Create(swappedDraw, null, null, evaluator)),
            ForecastRetentionKey.Compute(SearchNode.Create(swappedDrawOther, null, null, evaluator)));

        // 3) Hand 顺序不同 -> key 不同
        CombatState handOrderA = root.Clone();
        SetHand(handOrderA.Players[0],
            CreateCard<StrikeIronclad>(handOrderA.Players[0]),
            CreateCard<DefendIronclad>(handOrderA.Players[0]));
        CombatState handOrderB = root.Clone();
        SetHand(handOrderB.Players[0],
            CreateCard<DefendIronclad>(handOrderB.Players[0]),
            CreateCard<StrikeIronclad>(handOrderB.Players[0]));
        Assert.NotEqual(
            ForecastRetentionKey.Compute(SearchNode.Create(handOrderA, null, null, evaluator)),
            ForecastRetentionKey.Compute(SearchNode.Create(handOrderB, null, null, evaluator)));

        // 4) RunRng 计数器/状态不同 -> key 不同
        CombatState rngConsumed = root.Clone();
        _ = rngConsumed.RunState.Rng.Shuffle.NextInt(100);
        Assert.NotEqual(
            ForecastRetentionKey.Compute(SearchNode.Create(root.Clone(), null, null, evaluator)),
            ForecastRetentionKey.Compute(SearchNode.Create(rngConsumed, null, null, evaluator)));

        // 5) PenNib / 出牌次数历史计数器不同 -> key 不同
        CombatState withPenNib = await CreateCombatAsync("MAWLER_NORMAL", addPenNib: true);
        SetHand(withPenNib.Players[0],
            CreateCard<StrikeIronclad>(withPenNib.Players[0]),
            CreateCard<StrikeIronclad>(withPenNib.Players[0]));
        CombatState afterOneAttack = withPenNib.Clone();
        await ActionExecutor.ExecuteAsync(
            afterOneAttack,
            new PlannedAction(1, "PlayCard", "STRIKE_IRONCLAD", 0, 0, null, -1, [], []),
            []);
        // 将第二状态补回满能量、满敌血、同手牌，单独检验 PenNib 计数与出牌计数差异不会折叠。
        afterOneAttack.Players[0].PlayerCombatState!.GainEnergy(1);
        afterOneAttack.Enemies[0].SetCurrentHpInternal(withPenNib.Enemies[0].CurrentHp);
        ClearPile(afterOneAttack.Players[0].PlayerCombatState!.DiscardPile);
        SetHand(afterOneAttack.Players[0],
            CreateCard<StrikeIronclad>(afterOneAttack.Players[0]),
            CreateCard<StrikeIronclad>(afterOneAttack.Players[0]));

        Assert.NotEqual(
            ForecastRetentionKey.Compute(SearchNode.Create(withPenNib, null, null, evaluator)),
            ForecastRetentionKey.Compute(SearchNode.Create(afterOneAttack, null, null, evaluator)));
    }

    private static async Task<CombatState> CreateCombatAsync(string encounterId, bool addPenNib = false)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        if (addPenNib)
        {
            var penNib = (RelicModel)ModelDb.Relic<PenNib>().MutableClone();
            penNib.AssignOwner(run.Players[0]);
            run.Players[0].AddRelicInternal(penNib);
        }

        Assert.True(EncounterCatalog.TryGet(encounterId, out EncounterCatalogEntry entry));
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Monster,
                target.coord.row + 1,
                target.coord.col,
                MapPointType.Monster,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);
        return entered.Room.Engine.State;
    }

    private static TCard CreateCard<TCard>(Player owner) where TCard : CardModel
    {
        var card = (TCard)ModelDb.Card<TCard>().MutableClone();
        card.AssignOwner(owner);
        return card;
    }

    private static void SetHand(Player player, params CardModel[] cards)
    {
        CardPile hand = player.PlayerCombatState!.Hand;
        ClearPile(hand);
        foreach (CardModel card in cards)
        {
            hand.AddInternal(card);
        }
    }

    private static void ClearPile(CardPile pile)
    {
        foreach (CardModel card in pile.Cards.ToList())
        {
            pile.RemoveInternal(card);
        }
    }

    private static int PlayCardCount(IReadOnlyList<PlannedAction> actions, string cardId) =>
        actions.Count(action => action.Kind == "PlayCard" && action.CardId == cardId);

    [Fact]
    public async Task RetentionKeyAndSelectWithKey_AllForOneDiscardOrder_DistinguishesAndPreservesDistinctOutcomes()
    {
        CombatState root = await CreateCombatAsync("MAWLER_NORMAL");
        Player player = root.Players[0];
        player.PlayerCombatState!.GainEnergy(Math.Max(0, 2 - player.PlayerCombatState.Energy));

        var handCards = new List<CardModel>(CardPile.MaxCardsInHand);
        for (int i = 0; i < CardPile.MaxCardsInHand - 1; i++)
            handCards.Add(CreateCard<Wound>(player));
        handCards.Add(CreateCard<AllForOne>(player));
        SetHand(player, handCards.ToArray());

        CombatState angerFirst = root.Clone();
        PlayerCombatState pcsA = angerFirst.Players[0].PlayerCombatState!;
        ClearPile(pcsA.DiscardPile);
        pcsA.DiscardPile.AddInternal(CreateCard<Anger>(angerFirst.Players[0]));
        pcsA.DiscardPile.AddInternal(CreateCard<Claw>(angerFirst.Players[0]));

        CombatState clawFirst = root.Clone();
        PlayerCombatState pcsB = clawFirst.Players[0].PlayerCombatState!;
        ClearPile(pcsB.DiscardPile);
        pcsB.DiscardPile.AddInternal(CreateCard<Claw>(clawFirst.Players[0]));
        pcsB.DiscardPile.AddInternal(CreateCard<Anger>(clawFirst.Players[0]));

        var evaluator = new StateEvaluator(root, CombatPolicy.Default);
        SearchNode nodeA = SearchNode.Create(angerFirst, null, null, evaluator);
        SearchNode nodeB = SearchNode.Create(clawFirst, null, null, evaluator);

        Assert.NotEqual(nodeA.Fingerprint, nodeB.Fingerprint);
        Assert.Equal(ForecastRetentionKey.Compute(nodeA), ForecastRetentionKey.Compute(nodeB));
        List<SearchNode> retainedRoots = Retention.SelectWithKey(
            [nodeA, nodeB],
            width: 2,
            ForecastRetentionKey.Compute,
            includeParetoProgress: true);
        Assert.Equal(2, retainedRoots.Count);

        CombatState afterA = angerFirst.Clone();
        await ActionExecutor.ExecuteAsync(
            afterA,
            new PlannedAction(1, "PlayCard", "ALL_FOR_ONE", 0, 0, null, -1, [], []),
            []);
        CombatState afterB = clawFirst.Clone();
        await ActionExecutor.ExecuteAsync(
            afterB,
            new PlannedAction(1, "PlayCard", "ALL_FOR_ONE", 0, 0, null, -1, [], []),
            []);

        Assert.Contains(afterA.Players[0].PlayerCombatState!.Hand.Cards, card => card is Anger);
        Assert.DoesNotContain(afterA.Players[0].PlayerCombatState!.Hand.Cards, card => card is Claw);
        Assert.Contains(afterB.Players[0].PlayerCombatState!.Hand.Cards, card => card is Claw);
        Assert.DoesNotContain(afterB.Players[0].PlayerCombatState!.Hand.Cards, card => card is Anger);

        SearchNode afterNodeA = SearchNode.Create(afterA, null, null, evaluator);
        SearchNode afterNodeB = SearchNode.Create(afterB, null, null, evaluator);
        Assert.NotEqual(afterNodeA.Fingerprint, afterNodeB.Fingerprint);
        Assert.NotEqual(ForecastRetentionKey.Compute(afterNodeA), ForecastRetentionKey.Compute(afterNodeB));
        List<SearchNode> retainedAfter = Retention.SelectWithKey(
            [afterNodeA, afterNodeB],
            width: 2,
            ForecastRetentionKey.Compute,
            includeParetoProgress: true);
        Assert.Equal(2, retainedAfter.Count);
    }

    [Fact]
    public async Task DuplicateCardPruning_TwoGeneticAlgorithms_PreservesDeckVersionsAndRetainsBoth()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        ClearPile(player.Deck);

        GeneticAlgorithm deckGa0 = CreateCard<GeneticAlgorithm>(player);
        deckGa0.FloorAddedToDeck = 1;
        player.Deck.AddInternal(deckGa0);
        GeneticAlgorithm deckGa1 = CreateCard<GeneticAlgorithm>(player);
        deckGa1.FloorAddedToDeck = 2;
        player.Deck.AddInternal(deckGa1);

        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Monster,
                target.coord.row + 1,
                target.coord.col,
                MapPointType.Monster,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        PlayerCombatState pcs = combat.Players[0].PlayerCombatState!;
        CardModel[] allCards = pcs.AllPiles.SelectMany(pile => pile.Cards).ToArray();
        CardModel combatGa0 = allCards.Single(card => ReferenceEquals(card.DeckVersion, deckGa0));
        CardModel combatGa1 = allCards.Single(card => ReferenceEquals(card.DeckVersion, deckGa1));
        foreach (CardPile pile in pcs.AllPiles)
            ClearPile(pile);
        pcs.Hand.AddInternal(combatGa0);
        pcs.Hand.AddInternal(combatGa1);
        pcs.GainEnergy(Math.Max(0, 2 - pcs.Energy));

        PlannedAction[] gaActions = ActionGenerator.GenerateForForecast(combat)
            .Where(action => action.Kind == "PlayCard" && action.CardId == "GENETIC_ALGORITHM")
            .ToArray();
        Assert.Equal(2, gaActions.Length);
        Assert.Equal(new[] { 0, 1 }, gaActions.Select(action => action.CardOccurrence).ToArray());

        CombatState playFirst = combat.Clone();
        await ActionExecutor.ExecuteAsync(playFirst, gaActions[0], []);
        CombatState playSecond = combat.Clone();
        await ActionExecutor.ExecuteAsync(playSecond, gaActions[1], []);

        Assert.Equal(4, ((GeneticAlgorithm)playFirst.Players[0].Deck.Cards[0]).CurrentBlock);
        Assert.Equal(1, ((GeneticAlgorithm)playFirst.Players[0].Deck.Cards[1]).CurrentBlock);
        Assert.Equal(1, ((GeneticAlgorithm)playSecond.Players[0].Deck.Cards[0]).CurrentBlock);
        Assert.Equal(4, ((GeneticAlgorithm)playSecond.Players[0].Deck.Cards[1]).CurrentBlock);

        var evaluator = new StateEvaluator(combat, CombatPolicy.Default);
        SearchNode nodeFirst = SearchNode.Create(playFirst, null, gaActions[0], evaluator);
        SearchNode nodeSecond = SearchNode.Create(playSecond, null, gaActions[1], evaluator);
        Assert.NotEqual(nodeFirst.Fingerprint, nodeSecond.Fingerprint);
        Assert.NotEqual(ForecastRetentionKey.Compute(nodeFirst), ForecastRetentionKey.Compute(nodeSecond));
        List<SearchNode> retained = Retention.SelectWithKey(
            [nodeFirst, nodeSecond],
            width: 2,
            ForecastRetentionKey.Compute,
            includeParetoProgress: true);
        Assert.Equal(2, retained.Count);
    }

    [Fact]
    public async Task DeckVersionAccessAnalyzer_FlagsEveryDeckVersionReadingCardAndEnchantment_WithoutOverpruning()
    {
        Assembly coreAssembly = typeof(CardModel).Assembly;
        HashSet<Type> flaggedTypes = coreAssembly.GetTypes()
            .Where(t => !t.IsAbstract && (typeof(CardModel).IsAssignableFrom(t) || typeof(EnchantmentModel).IsAssignableFrom(t)))
            .Where(DeckVersionAccessAnalyzer.TypeReferencesDeckVersion)
            .ToHashSet();

        Assert.Contains(typeof(GeneticAlgorithm), flaggedTypes);
        Assert.Contains(typeof(TheScythe), flaggedTypes);
        Assert.Contains(typeof(ShowOff), flaggedTypes);
        Assert.Contains(typeof(Goopy), flaggedTypes);
        Assert.DoesNotContain(typeof(StrikeIronclad), flaggedTypes);
        Assert.DoesNotContain(typeof(DefendIronclad), flaggedTypes);

        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        ClearPile(player.Deck);

        ShowOff show0 = CreateCard<ShowOff>(player);
        ShowOff show1 = CreateCard<ShowOff>(player);
        TheScythe scythe0 = CreateCard<TheScythe>(player);
        TheScythe scythe1 = CreateCard<TheScythe>(player);
        DefendIronclad goopyDef0 = CreateCard<DefendIronclad>(player);
        DefendIronclad goopyDef1 = CreateCard<DefendIronclad>(player);
        await CardCmd.Enchant<Goopy>(goopyDef0, 1m);
        await CardCmd.Enchant<Goopy>(goopyDef1, 1m);
        StrikeIronclad strike0 = CreateCard<StrikeIronclad>(player);
        StrikeIronclad strike1 = CreateCard<StrikeIronclad>(player);

        foreach (CardModel c in new CardModel[] { show0, show1, scythe0, scythe1, goopyDef0, goopyDef1, strike0, strike1 })
        {
            c.FloorAddedToDeck = 1;
            player.Deck.AddInternal(c);
        }

        Assert.True(EncounterCatalog.TryGet("MAWLER_NORMAL", out EncounterCatalogEntry entry));
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                RoomType.Monster,
                target.coord.row + 1,
                target.coord.col,
                MapPointType.Monster,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        PlayerCombatState pcs = combat.Players[0].PlayerCombatState!;
        CardModel[] allCards = pcs.AllPiles.SelectMany(pile => pile.Cards).ToArray();
        CardModel FindCopy(CardModel deckCard) => allCards.Single(c => ReferenceEquals(c.DeckVersion, deckCard));
        pcs.GainEnergy(10);

        SetHand(combat.Players[0], FindCopy(show0), FindCopy(show1));
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "SHOW_OFF"));

        SetHand(combat.Players[0], FindCopy(scythe0), FindCopy(scythe1));
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "THE_SCYTHE"));

        SetHand(combat.Players[0], FindCopy(goopyDef0), FindCopy(goopyDef1));
        Assert.Equal(2, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "DEFEND_IRONCLAD"));

        SetHand(combat.Players[0], FindCopy(strike0), FindCopy(strike1));
        Assert.Equal(1, PlayCardCount(ActionGenerator.GenerateForForecast(combat), "STRIKE_IRONCLAD"));
    }

    [Theory]
    [InlineData(80, null)]
    [InlineData(55, 55)]
    [InlineData(80, 55)]
    public async Task MawlerInflameParity_WideBeamProbe_Reaches10HpLossVictory(
        int saveCurrentHp,
        int? hpOverride)
    {
        TestSaveBuilder.EnsurePrewarmed();
        string normalizedSaveJson = await BuildMawlerInflameSaveJsonAsync(saveCurrentHp);

        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 60,
            maxExpandedNodes: 60000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000) with
        {
            MaxCardBranchesPerNode = 12,
        };

        var request = new BridgeRequestDto(
            AbiVersion: BridgeAbi.Version,
            RequestId: $"mawler-inflame-wide-{saveCurrentHp}-{hpOverride}",
            NormalizedSaveJson: normalizedSaveJson,
            HasLiveSidecar: true,
            LiveActFloor: 1,
            NextRoomId: 1,
            SpoilsMapVerification: null,
            EncounterId: "MAWLER_NORMAL",
            TargetActFloor: 2,
            TargetMapColumn: 0,
            RoomKind: "Normal",
            MapPointKind: "Normal",
            IsSecondBoss: false,
            InterveningMapPoints: [],
            PlayerCurrentHpOverride: hpOverride,
            SimulationSeed: null,
            SearchBudgetMilliseconds: 15_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 1,
            Policy: policy);

        (BridgeResponseDto response, _) = await TestSaveBuilder.RunAsync(request);

        Assert.Equal("Succeeded", response.Outcome);
        Assert.True(
            response.ProjectedHpLoss <= 10,
            $"Expected ProjectedHpLoss <= 10 (saveCurrentHp={saveCurrentHp}, hpOverride={hpOverride}), got ProjectedHpLoss={response.ProjectedHpLoss}, FinalHp={response.FinalHp}, Turn={response.CombatEndedTurn}, Boundary={response.SearchBoundary}, Confidence={response.Confidence}");
        Assert.NotNull(response.CombatEndedTurn);
        Assert.Equal("None", response.SearchBoundary);
        Assert.False(response.OnlyDeathRoutes);
    }

    [Theory]
    [InlineData(80, null)]
    [InlineData(55, 55)]
    [InlineData(80, 55)]
    public async Task MawlerInflameParity_IroncladSeedCombatSolver_FindsOptimal10HpLossVictory(
        int saveCurrentHp,
        int? hpOverride)
    {
        TestSaveBuilder.EnsurePrewarmed();
        string normalizedSaveJson = await BuildMawlerInflameSaveJsonAsync(saveCurrentHp);

        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: 5000,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000) with
        {
            MaxCardBranchesPerNode = 12,
        };

        var request = new BridgeRequestDto(
            AbiVersion: BridgeAbi.Version,
            RequestId: $"mawler-inflame-parity-{saveCurrentHp}-{hpOverride}",
            NormalizedSaveJson: normalizedSaveJson,
            HasLiveSidecar: true,
            LiveActFloor: 1,
            NextRoomId: 1,
            SpoilsMapVerification: null,
            EncounterId: "MAWLER_NORMAL",
            TargetActFloor: 2,
            TargetMapColumn: 0,
            RoomKind: "Normal",
            MapPointKind: "Normal",
            IsSecondBoss: false,
            InterveningMapPoints: [],
            PlayerCurrentHpOverride: hpOverride,
            SimulationSeed: null,
            SearchBudgetMilliseconds: 15_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 1,
            Policy: policy);

        (BridgeResponseDto response, _) = await TestSaveBuilder.RunAsync(request);

        Assert.Equal("Succeeded", response.Outcome);
        Assert.True(
            response.ProjectedHpLoss <= 10,
            $"Expected ProjectedHpLoss <= 10 (saveCurrentHp={saveCurrentHp}, hpOverride={hpOverride}), got ProjectedHpLoss={response.ProjectedHpLoss}, FinalHp={response.FinalHp}, Turn={response.CombatEndedTurn}, Boundary={response.SearchBoundary}, Confidence={response.Confidence}");
        Assert.NotNull(response.CombatEndedTurn);
        Assert.Equal("None", response.SearchBoundary);
        Assert.False(response.OnlyDeathRoutes);
        Assert.Equal("Complete", response.Confidence);
        Assert.True(response.ExpandedNodes <= 5000);
    }

    [Fact]
    public async Task PreCombatBeamPortfolio_WithReachablePowerAndTightBudget_RespectsNodeLimit()
    {
        TestSaveBuilder.EnsurePrewarmed();
        string normalizedSaveJson = await BuildMawlerInflameSaveJsonAsync(80);
        const int budgetLimit = 1200;

        BridgePolicyDto policy = TestSaveBuilder.DefaultPolicy(
            beamWidth: 16,
            maxExpandedNodes: budgetLimit,
            useBeamWidthPortfolio: true,
            softTimeBudgetMs: 15_000) with
        {
            MaxCardBranchesPerNode = 12,
        };

        var request = new BridgeRequestDto(
            AbiVersion: BridgeAbi.Version,
            RequestId: "mawler-inflame-tight-budget",
            NormalizedSaveJson: normalizedSaveJson,
            HasLiveSidecar: true,
            LiveActFloor: 1,
            NextRoomId: 1,
            SpoilsMapVerification: null,
            EncounterId: "MAWLER_NORMAL",
            TargetActFloor: 2,
            TargetMapColumn: 0,
            RoomKind: "Normal",
            MapPointKind: "Normal",
            IsSecondBoss: false,
            InterveningMapPoints: [],
            PlayerCurrentHpOverride: null,
            SimulationSeed: null,
            SearchBudgetMilliseconds: 15_000,
            OverallTimeoutMilliseconds: 30_000,
            MaxDegreeOfParallelism: 1,
            Policy: policy);

        (BridgeResponseDto response, _) = await TestSaveBuilder.RunAsync(request);

        Assert.Equal("Succeeded", response.Outcome);
        Assert.True(response.ExpandedNodes <= budgetLimit);
    }

    [Fact]
    public async Task PortfolioFinalistSelection_UsesCommonBaselineScore_AndPreservesMember0OnTies()
    {
        CombatState root = await CreateCombatAsync("MAWLER_NORMAL");
        int initialHp = root.Players[0].Creature.CurrentHp;

        CombatState buffedRoot = root.Clone();
        await PowerCmd.Apply<StrengthPower>(buffedRoot, buffedRoot.Players[0].Creature, 3m, buffedRoot.Players[0].Creature, null);

        var baselineEvaluator = new StateEvaluator(buffedRoot, CombatPolicy.Default, aggressivePowerCommitment: false);
        var powerEvaluator = new StateEvaluator(buffedRoot, CombatPolicy.Default, aggressivePowerCommitment: true);

        SearchNode member0Duplicate = SearchNode.Create(buffedRoot.Clone(), null, null, baselineEvaluator);
        SearchNode powerDuplicate = SearchNode.Create(buffedRoot.Clone(), null, null, powerEvaluator);

        Assert.Equal(member0Duplicate.Fingerprint, powerDuplicate.Fingerprint);
        Assert.True(powerDuplicate.Score > member0Duplicate.Score);
        Assert.Equal(
            baselineEvaluator.EvaluateBaselineScore(member0Duplicate),
            baselineEvaluator.EvaluateBaselineScore(powerDuplicate));
        Assert.Same(
            member0Duplicate,
            PlanSelector.Select([member0Duplicate, powerDuplicate], initialHp, baselineEvaluator));

        var member0Pass = new ForecastPassOutcome(
            member0Duplicate,
            [],
            ForecastSearchBoundary.NodeLimit,
            10,
            false,
            0,
            baselineEvaluator);
        var powerPass = new ForecastPassOutcome(
            powerDuplicate,
            [],
            ForecastSearchBoundary.NodeLimit,
            10,
            false,
            0,
            powerEvaluator);
        Assert.Same(
            member0Pass,
            PreCombatBeamPortfolio.ChooseWinningPass([member0Pass, powerPass], initialHp, baselineEvaluator));

        var cleanBaselineEvaluator = new StateEvaluator(root, CombatPolicy.Default, aggressivePowerCommitment: false);
        var cleanPowerEvaluator = new StateEvaluator(root, CombatPolicy.Default, aggressivePowerCommitment: true);

        CombatState damagedEnemyState = root.Clone();
        damagedEnemyState.Enemies[0].SetCurrentHpInternal(damagedEnemyState.Enemies[0].CurrentHp - 6);
        SearchNode member0Distinct = SearchNode.Create(damagedEnemyState, null, null, cleanBaselineEvaluator);

        CombatState highPowerBonusState = root.Clone();
        await PowerCmd.Apply<StrengthPower>(
            highPowerBonusState,
            highPowerBonusState.Players[0].Creature,
            3m,
            highPowerBonusState.Players[0].Creature,
            null);
        SearchNode powerDistinct = SearchNode.Create(highPowerBonusState, null, null, cleanPowerEvaluator);

        Assert.Equal(
            cleanBaselineEvaluator.PolicyHpCost(member0Distinct, initialHp),
            cleanBaselineEvaluator.PolicyHpCost(powerDistinct, initialHp));
        Assert.True(powerDistinct.Score > member0Distinct.Score);
        Assert.True(
            cleanBaselineEvaluator.EvaluateBaselineScore(member0Distinct) >
            cleanBaselineEvaluator.EvaluateBaselineScore(powerDistinct));
        Assert.Same(
            member0Distinct,
            PlanSelector.Select([powerDistinct, member0Distinct], initialHp, cleanBaselineEvaluator));

        CombatState tiedEnemyState = damagedEnemyState.Clone();
        SearchNode tiedPowerNode = SearchNode.Create(tiedEnemyState, null, null, cleanPowerEvaluator);
        Assert.True(tiedPowerNode.Score == member0Distinct.Score);
        Assert.Equal(
            cleanBaselineEvaluator.EvaluateBaselineScore(member0Distinct),
            cleanBaselineEvaluator.EvaluateBaselineScore(tiedPowerNode));
        Assert.Same(
            member0Distinct,
            PlanSelector.Select([member0Distinct, tiedPowerNode], initialHp, cleanBaselineEvaluator));
    }

    [Fact]
    public void ComputeMember0NodeLimit_Uses64BitMathWithoutOverflow()
    {
        Assert.Equal(24_000_000, PreCombatBeamPortfolio.ComputeMember0NodeLimit(60_000_000, hasReachablePower: true));
        Assert.Equal((int)((long)int.MaxValue * 40L / 100L), PreCombatBeamPortfolio.ComputeMember0NodeLimit(int.MaxValue, hasReachablePower: true));
        Assert.Equal(480, PreCombatBeamPortfolio.ComputeMember0NodeLimit(1200, hasReachablePower: true));
        Assert.Equal(1200, PreCombatBeamPortfolio.ComputeMember0NodeLimit(1200, hasReachablePower: false));
        Assert.Equal(30_000_000, PreCombatBeamPortfolio.ComputeMember0NodeLimit(60_000_000, hasReachablePower: false));
    }

    private static async Task<string> BuildMawlerInflameSaveJsonAsync(int saveCurrentHp)
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "mawler-ironclad-combatsolver-save.json");
        string rawJson = await File.ReadAllTextAsync(fixturePath);
        JsonObject root = JsonNode.Parse(rawJson)!.AsObject();
        JsonObject player = root["players"]![0]!.AsObject();
        player["current_hp"] = saveCurrentHp;
        ((JsonArray)player["deck"]!).Add(new JsonObject
        {
            ["floor_added_to_deck"] = 1,
            ["id"] = "CARD.INFLAME",
        });
        return root.ToJsonString();
    }
}
