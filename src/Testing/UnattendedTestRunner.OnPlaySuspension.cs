using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertOnPlaySuspensionBoundaries(CombatState combat, Player player)
    {
        AssertDrawOnPlayStopsBeforeEnergyAndBlock(combat, player);
        AssertAttackOnPlayStopsBeforeOrbChannel(combat, player);
        AssertExhaustBatchStopsBeforeRemainingCardsAndAttack(combat, player);
        AssertAttackStopsBeforePermanentCardMutation(combat, player);
        AssertGenericEffectSequencesStopAtPending(combat, player);
        AssertRebootStopsBeforeDrawAfterShuffleChoice(combat, player);
        AssertCardCompletionStopsBeforePlayedHistory(combat, player);
        AssertAfterBlockClearedStopsBeforePowerConsumption(combat, player);
        AssertGeneratedCardRelicStopsBeforeStateCommit(combat, player);
        AssertExhaustDrawRelicStopsBeforeCounterCommit(combat, player);
        AssertAfterPlayedBlockRelicStopsBeforeStateCommit(combat, player);
        AssertAfterPlayedDrawStopsBeforeRelicRecord(combat, player);
        AssertMusicBoxStopsBeforeStateCommit(combat, player);
        AssertManualPlayStopsAfterResourceTriggeredChoice(combat, player);
        AssertDiamondDiademStopsBeforeBlur(combat, player);
    }

    private static void AssertDrawOnPlayStopsBeforeEnergyAndBlock(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(
            combat,
            player,
            victimCount: 0);
        PredictedCard predicted = PredictedCard.Create(ModelDb.Card<Constellation>(), player);
        predicted.MutablePreview.DynamicVars.Cards.BaseValue = 1;
        predicted.MutablePreview.DynamicVars.Energy.BaseValue = 1;
        predicted.MutablePreview.DynamicVars.Block.BaseValue = 1;
        SimPlayerCombatState playerState = fixture.Simulator.State.GetPlayerCombatState(player);
        int energyBefore = playerState.Energy;
        int blockBefore = fixture.Simulator.State.GetCreature(player.Creature).Block;

        CardDrawCardMirrors.ConstellationOnPlay(
            (Constellation)predicted.Preview,
            CreateOnPlayContext(fixture.Simulator, predicted, player, player.Creature));

        AssertSuspended(fixture, completed: false, "draw OnPlay tail");
        if (playerState.Energy != energyBefore
            || fixture.Simulator.State.GetCreature(player.Creature).Block != blockBefore)
        {
            throw new InvalidOperationException("抽牌产生选择后仍结算了同一 OnPlay 的能量或格挡尾部。");
        }
    }

    private static void AssertAttackOnPlayStopsBeforeOrbChannel(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PredictedCard predicted = PredictedCard.Create(ModelDb.Card<BallLightning>(), player);
        int channelsBefore = fixture.Simulator.History
            .OfType<CombatPredictionOrbChanneledEntry>()
            .Count();

        CardOnPlayMirrorContext context = CreateOnPlayContext(
            fixture.Simulator,
            predicted,
            player,
            fixture.Victims[0]);
        OrbCardMirrors.BallLightningOnPlay((BallLightning)predicted.Preview, context);

        AssertSuspended(fixture, completed: false, "attack OnPlay tail");
        int channelsAfter = fixture.Simulator.History
            .OfType<CombatPredictionOrbChanneledEntry>()
            .Count();
        if (channelsAfter != channelsBefore)
            throw new InvalidOperationException("攻击产生选择后仍执行了同一 OnPlay 的充能球尾部。");
    }

    private static void AssertCardCompletionStopsBeforePlayedHistory(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        _ = simulatedCombat.AddPowerInstance<ViciousPower>(
            player.Creature,
            1,
            player.Creature);
        Creature enemy = simulatedCombat.HittableEnemies.First();
        simulatedCombat.Apply<VulnerablePower>(enemy, 1, player.Creature);
        PredictedCard card = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        int cardsPlayedBefore = simulatedCombat.GetCardsPlayedThisTurn(player.Creature);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            ((ICombatPredictionCardExecutionSink)simulatedCombat).CompleteCardPlayEffects(
                simulator,
                card,
                simulator.State.GetCreature(player.Creature).Block,
                simulator.History.Entries.Count);

            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "card completion");
            if (simulatedCombat.GetCardsPlayedThisTurn(player.Creature) != cardsPlayedBefore)
                throw new InvalidOperationException("完成链产生选择后仍提交了 RecordCardPlayed。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertExhaustBatchStopsBeforeRemainingCardsAndAttack(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(
            combat,
            player,
            victimCount: 0);
        _ = fixture.Combat.AddPowerInstance<DarkEmbracePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard first = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        PredictedCard second = PredictedCard.Create(ModelDb.Card<Zap>(), player);
        fixture.Simulator.AddToPile(first, PileType.Hand);
        fixture.Simulator.AddToPile(second, PileType.Hand);
        PredictedCard fiendFire = PredictedCard.Create(ModelDb.Card<FiendFire>(), player);

        BespokeCardMirrors.FiendFireOnPlay(
            (FiendFire)fiendFire.Preview,
            CreateOnPlayContext(
                fixture.Simulator,
                fiendFire,
                player,
                fixture.PrimaryEnemy));

        AssertSuspended(fixture, completed: false, "Fiend Fire exhaust batch");
        if (first.GetPile(fixture.Simulator.State)?.Type != PileType.Exhaust
            || second.GetPile(fixture.Simulator.State)?.Type != PileType.Hand)
        {
            throw new InvalidOperationException("批量穷尽产生选择后仍穷尽了后续手牌。");
        }
        if (fixture.Simulator.History
            .OfType<CombatPredictionDamageReceivedEntry>()
            .Any(entry => ReferenceEquals(entry.CardSource, fiendFire)))
        {
            throw new InvalidOperationException("批量穷尽产生选择后仍执行了卡牌攻击尾部。");
        }
    }

    private static void AssertAttackStopsBeforePermanentCardMutation(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PredictedCard maul = PredictedCard.Create(ModelDb.Card<Maul>(), player);
        fixture.Simulator.AddToPile(maul, PileType.Hand);
        decimal damageBefore = maul.Preview.DynamicVars.Damage.BaseValue;

        BespokeCardMirrors.MaulOnPlay(
            (Maul)maul.Preview,
            CreateOnPlayContext(
                fixture.Simulator,
                maul,
                player,
                fixture.Victims[0]));

        AssertSuspended(fixture, completed: false, "attack permanent mutation");
        if (maul.Preview.DynamicVars.Damage.BaseValue != damageBefore)
            throw new InvalidOperationException("攻击产生选择后仍提交了卡牌永久伤害成长。");
    }

    private static void AssertRebootStopsBeforeDrawAfterShuffleChoice(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        _ = PrepareDamagePendingChoiceFixture(simulator, simulatedCombat, player);
        StratagemPower stratagem = simulatedCombat.AddPowerInstance<StratagemPower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard handCard = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddToPile(handCard, PileType.Hand);
        PredictedCard reboot = PredictedCard.Create(ModelDb.Card<Reboot>(), player);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            CardDrawCardMirrors.RebootOnPlay(
                (Reboot)reboot.Preview,
                CreateOnPlayContext(simulator, reboot, player, player.Creature));

            AssertPendingChoice(
                simulatedCombat,
                stratagem.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "Reboot shuffle");
            if (!simulator.State.GetPlayerCombatState(player).Hand.IsEmpty)
                throw new InvalidOperationException("洗牌产生选择后 Reboot 仍执行了后续抽牌。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertGenericEffectSequencesStopAtPending(
        CombatState combat,
        Player player)
    {
        using (PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player))
        {
            PredictedCard card = PredictedCard.Create(ModelDb.Card<IronWave>(), player);
            int blockBefore = fixture.Simulator.State.GetCreature(player.Creature).Block;
            CardOnPlayMirrorContext context = CreateOnPlayContext(
                fixture.Simulator,
                card,
                player,
                fixture.Victims[0]);

            new CardEffectRecipe([CardEffectKind.Attack, CardEffectKind.Block])
                .Execute(card.Preview, context);

            AssertSuspended(fixture, completed: false, "strict card-effect recipe");
            if (fixture.Simulator.State.GetCreature(player.Creature).Block != blockBefore)
                throw new InvalidOperationException("通用效果配方在攻击挂起后仍执行了后续格挡。");
        }

        using (PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player))
        {
            PredictedCard card = PredictedCard.Create(ModelDb.Card<IronWave>(), player);
            bool tailRan = false;
            CardOnPlayInferrer.ExecuteInferredActions(
                [
                    (_, context) => context.AttackSingle(),
                    (_, _) => tailRan = true,
                ],
                card.Preview,
                CreateOnPlayContext(
                    fixture.Simulator,
                    card,
                    player,
                    fixture.Victims[0]));

            AssertSuspended(fixture, completed: false, "inferred card-effect sequence");
            if (tailRan)
                throw new InvalidOperationException("推断效果序列在攻击挂起后仍执行了后续动作。");
        }
    }

    private static void AssertAfterBlockClearedStopsBeforePowerConsumption(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        fixture.Combat.Apply<BlockNextTurnPower>(
            player.Creature,
            3,
            player.Creature);
        BlockNextTurnPower power = fixture.Combat.GetPower<BlockNextTurnPower>(player.Creature)
            ?? throw new InvalidOperationException("AfterBlockCleared 测试找不到下回合格挡 Power。");

        bool completed = CorePowerSupport.TriggerAfterBlockCleared(
            fixture.Simulator,
            fixture.Combat,
            player.Creature);

        AssertSuspended(fixture, completed, "AfterBlockCleared");
        if (fixture.Combat.GetAmount<BlockNextTurnPower>(player.Creature) != 3
            || power.Amount != 3)
        {
            throw new InvalidOperationException("格挡清除后效果产生选择时仍消耗了后续格挡 Power。");
        }
    }

    private static void AssertGeneratedCardRelicStopsBeforeStateCommit(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        _ = fixture.Combat.AddPowerInstance<PillarOfCreationPower>(
            player.Creature,
            1,
            player.Creature);
        BurningSticks relic = (BurningSticks)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<BurningSticks>(),
            player);
        BurningSticksPredictionState state = fixture.Simulator.StateStore.Get(
            relic,
            () => new BurningSticksPredictionState(relic));
        PredictedCard exhausted = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);

        AfterCardExhaustedMirrors.Invoke(
            relic,
            new AfterCardExhaustedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = exhausted,
                CausedByEthereal = false,
            });

        AssertSuspended(fixture, completed: false, "generated-card relic");
        if (state.WasUsedThisCombat)
            throw new InvalidOperationException("生成牌产生选择后仍提交了遗物已使用状态。");
    }

    private static void AssertExhaustDrawRelicStopsBeforeCounterCommit(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        JossPaper relic = (JossPaper)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<JossPaper>(),
            player);
        int threshold = relic.DynamicVars[JossPaper._exhaustAmountKey].IntValue;
        JossPaperPredictionState state = simulator.StateStore.Get(
            relic,
            () => new JossPaperPredictionState(relic));
        state.CardsExhausted = threshold - 1;
        PredictedCard exhausted = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            AfterCardExhaustedMirrors.Invoke(
                relic,
                new AfterCardExhaustedMirrorContext
                {
                    Simulator = simulator,
                    Card = exhausted,
                    CausedByEthereal = false,
                });

            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "exhaust draw relic");
            if (state.CardsExhausted != threshold)
                throw new InvalidOperationException("穷尽抽牌产生选择后仍提交了遗物计数取模尾部。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertAfterPlayedBlockRelicStopsBeforeStateCommit(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        Permafrost relic = (Permafrost)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<Permafrost>(),
            player);
        FlagPredictionState state = fixture.Simulator.StateStore.Get(
            relic,
            () => new FlagPredictionState(false));
        PredictedCard card = PredictedCard.Create(ModelDb.Card<Inflame>(), player);

        AfterCardPlayedMirrors.Invoke(
            relic,
            new AfterCardPlayedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = card,
                CardPlay = CreatePendingTailCardPlay(card, player),
            });

        AssertSuspended(fixture, completed: false, "AfterCardPlayed block relic");
        if (state.Value)
            throw new InvalidOperationException("出牌后格挡产生选择时仍提交了遗物激活状态。");
    }

    private static void AssertManualPlayStopsAfterResourceTriggeredChoice(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        _ = fixture.Combat.AddPowerInstance<ChildOfTheStarsPower>(
            player.Creature,
            1,
            player.Creature);
        CardModel starModel = ModelDb.All
            .OfType<CardModel>()
            .Where(candidate => candidate.BaseStarCost > 0)
            .OrderBy(candidate => candidate.BaseStarCost)
            .First();
        PredictedCard card = PredictedCard.Create(starModel, player);
        SimPlayerCombatState playerState = fixture.Simulator.State.GetPlayerCombatState(player);
        playerState.GainStars(100);
        fixture.Simulator.AddToPile(card, PileType.Hand);
        int startsBefore = fixture.Simulator.History
            .OfType<CombatPredictionCardPlayStartedEntry>()
            .Count(entry => ReferenceEquals(entry.Card, card));

        bool completed = fixture.Simulator.ManualPlay(card, target: null, out PredictionTraceFrame? frame);

        AssertSuspended(fixture, completed, "manual resource spending");
        if (frame != null
            || fixture.Simulator.History
                .OfType<CombatPredictionCardPlayStartedEntry>()
                .Count(entry => ReferenceEquals(entry.Card, card)) != startsBefore)
        {
            throw new InvalidOperationException("资源消耗产生选择后仍进入了卡牌 OnPlayWrapper。");
        }
    }

    private static void AssertAfterPlayedDrawStopsBeforeRelicRecord(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(
            combat,
            player,
            victimCount: 0);
        GamePiece relic = (GamePiece)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GamePiece>(),
            player);
        PredictedCard card = PredictedCard.Create(ModelDb.Card<Inflame>(), player);
        ActionRelicTriggerRecorder recorder = new();
        recorder.BeginAction(0);
        fixture.Simulator.ActionRelicTriggers = recorder;

        AfterCardPlayedMirrors.Invoke(
            relic,
            new AfterCardPlayedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = card,
                CardPlay = CreatePendingTailCardPlay(card, player),
            });

        AssertSuspended(fixture, completed: false, "AfterCardPlayed draw relic");
        if (recorder.ForAction(0).Any(trigger => trigger.RelicId == relic.Id.Entry))
            throw new InvalidOperationException("出牌后抽牌产生选择时仍记录了遗物触发元数据。");
    }

    private static void AssertMusicBoxStopsBeforeStateCommit(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        _ = fixture.Combat.AddPowerInstance<PillarOfCreationPower>(
            player.Creature,
            1,
            player.Creature);
        MusicBox relic = (MusicBox)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<MusicBox>(),
            player);
        PredictedCard card = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
        MusicBoxPredictionState state = fixture.Simulator.StateStore.Get(
            relic,
            () => new MusicBoxPredictionState(relic));
        state.CardBeingPlayed = card.Original;

        AfterCardPlayedMirrors.Invoke(
            relic,
            new AfterCardPlayedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = card,
                CardPlay = CreatePendingTailCardPlay(card, player),
            });

        AssertSuspended(fixture, completed: false, "Music Box generation");
        if (state.WasUsedThisTurn)
            throw new InvalidOperationException("生成牌产生选择后仍提交了八音盒已使用状态。");
    }

    private static void AssertDiamondDiademStopsBeforeBlur(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        DiamondDiadem relic = (DiamondDiadem)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<DiamondDiadem>(),
            player);
        GremlinHorn horn = (GremlinHorn)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GremlinHorn>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(fixture.Combat, player, relic, horn);

        bool completed = fixture.Combat.TriggerRelicsAfterSideTurnStart(
            fixture.Simulator,
            CombatSide.Player,
            [player.Creature]);

        AssertSuspended(fixture, completed, "Diamond Diadem");
        if (fixture.Combat.GetAmount<BlurPower>(player.Creature) != 0)
            throw new InvalidOperationException("钻石冠冕的格挡产生选择后仍施加了 Blur。");
    }

    private static CardOnPlayMirrorContext CreateOnPlayContext(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Player player,
        Creature? target)
        => new()
        {
            Simulator = simulator,
            Card = card,
            CardPlay = new CardPlay
            {
                Card = card.Preview,
                Player = player,
                Target = target,
                ResultPile = PileType.Discard,
                Resources = default,
                IsAutoPlay = false,
                PlayIndex = 0,
                PlayCount = 1,
            },
        };

    private static void PrepareBlockTriggeredPending(
        PendingEnemyDeathFixture fixture,
        Player player)
    {
        foreach (Creature enemy in fixture.Combat.HittableEnemies)
        {
            SimCreatureState state = fixture.Simulator.State.GetCreature(enemy);
            state.SetMaxHp(1);
            state.CurrentHp = 1;
        }
        _ = fixture.Combat.AddPowerInstance<JuggernautPower>(
            player.Creature,
            20,
            player.Creature);
    }
}
