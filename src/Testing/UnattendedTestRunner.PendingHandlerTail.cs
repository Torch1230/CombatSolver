using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Kept in its own partial because these checks exercise the common suspension contract shared
    // by potion, card-hook, power, and monster-move handlers. AssertForkBoundaries is the sole caller.
    private static void AssertPendingHandlerTailBoundaries(CombatState combat, Player player)
    {
        AssertMultiTargetPotionStopsAtPending(combat, player);
        AssertPotionExhaustChoiceStopsAtNestedPending(combat, player);
        AssertPotionDiscardAndDrawStopsAtNestedPending(combat, player);
        AssertSacrificeStopsBeforeBlockAtPending(combat, player);
        AssertEndTurnDamageStopsBeforePowerClearAtPending(combat, player);
        AssertPanacheStopsBeforeCounterResetAtPending(combat, player);
        AssertExhaustDamageStopsBeforeRelicRecordAtPending(combat, player);
        AssertMonsterSelfKillStopsBeforeCompletionAtPending(combat, player);
        AssertTheBombStopsBeforeAmountConsumptionAtPending(combat, player);
        AssertDelayedStarsStopBeforePowerClearAtPending(combat, player);
        AssertInitialPlatingStopsAtPending(combat, player);
        AssertShipInBottleStopsBeforeDelayedBlockAtPending(combat, player);
        AssertMirroredPotionStopsBeforePostDrawEffectAtPending(combat, player);
        AssertManualPotionWrapperStopsBeforeAfterPotionHooksAtPending(combat, player);
        AssertReplaySettlementStopsBeforeNormalizationAtPending(combat, player);
    }

    private static void AssertMultiTargetPotionStopsAtPending(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player, victimCount: 2);
        ExplosiveAmpoule potion = (ExplosiveAmpoule)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<ExplosiveAmpoule>(),
            player);

        bool completed = PotionOnUseSupport.Use(fixture.Simulator, fixture.Combat, potion, target: null);

        AssertSuspended(fixture, completed, "multi-target potion");
        if (fixture.Victims.Any(victim =>
                fixture.Simulator.State.GetCreature(victim).CurrentHp != 0))
        {
            throw new InvalidOperationException(
                "多目标药水没有先完成同一批次内的全部目标伤害，再进入死亡选择链。");
        }
        if (fixture.Victims.Any(victim => !fixture.Combat.Enemies.Contains(victim)))
        {
            throw new InvalidOperationException(
                "多目标药水在第一个死亡选择挂起后仍从 roster 移除了批次目标。");
        }
        if (fixture.Simulator.History.OfType<CombatPredictionCardDrawnEntry>().Count() != 1
            || fixture.Simulator.History.OfType<CombatPredictionCardDrawResolvedEntry>().Any())
        {
            throw new InvalidOperationException(
                "多目标药水的首个死亡选择没有停在单次抽牌已开始、尚未完成的边界。");
        }
    }

    private static void AssertPotionExhaustChoiceStopsAtNestedPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PrepareDamagePendingChoiceFixture(simulator, simulatedCombat, player);
        _ = simulatedCombat.AddPowerInstance<DarkEmbracePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard first = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        PredictedCard second = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddGeneratedCardToCombat(
            first,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            second,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        Ashwater potion = (Ashwater)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<Ashwater>(),
            player);
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(
            PotionChoiceSupport.GetSpec(simulator, potion),
            [first.Preview.Id.Entry, second.Preview.Id.Entry]);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = PotionChoiceSupport.Apply(simulator, potion, choice);

            if (completed)
                throw new InvalidOperationException("药水多选穷尽在内层选择待定时错误报告完成。");
            AssertPendingChoice(
                simulatedCombat,
                CanonicalModels.Power<HellraiserPower>().Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "药水多选穷尽");
            if (first.GetPile(simulator.State)?.Type != PileType.Exhaust
                || second.GetPile(simulator.State)?.Type != PileType.Hand)
            {
                throw new InvalidOperationException("药水多选穷尽挂起后仍穷尽了后续牌。");
            }
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertPotionDiscardAndDrawStopsAtNestedPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);

        PredictedCard sly = PredictedCard.Create(ModelDb.Card<Discovery>(), player);
        sly.MutablePreview.GiveSingleTurnSly();
        PredictedCard discarded = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        PredictedCard drawSentinel = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        simulator.AddGeneratedCardToCombat(
            sly,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            discarded,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            drawSentinel,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        GamblersBrew potion = (GamblersBrew)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<GamblersBrew>(),
            player);
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(
            PotionChoiceSupport.GetSpec(simulator, potion),
            [sly.Preview.Id.Entry, discarded.Preview.Id.Entry]);
        PredictedCard[] drawPileBefore = playerState.DrawPile.Cards.ToArray();

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = PotionChoiceSupport.Apply(simulator, potion, choice);

            if (completed)
                throw new InvalidOperationException("丢弃重抽药水在狡猾自动牌选择待定时错误报告完成。");
            AssertPendingChoice(
                simulatedCombat,
                sly.Preview.Id.Entry,
                PlanChoiceEffect.GenerateToHand,
                "丢弃重抽药水");
            if (!playerState.DrawPile.Cards.SequenceEqual(drawPileBefore))
                throw new InvalidOperationException("丢弃重抽药水在内层选择挂起后仍执行了后续抽牌。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertSacrificeStopsBeforeBlockAtPending(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        fixture.Combat.SummonOsty(fixture.Simulator, player, amount: 1);
        _ = fixture.Combat.AddPowerInstance<NecroMasteryPower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard predicted = PredictedCard.Create(ModelDb.Card<Sacrifice>(), player);
        Sacrifice card = (Sacrifice)predicted.Preview;
        CardPlay cardPlay = CreatePendingTailCardPlay(predicted, player);
        int blockBefore = fixture.Simulator.State.GetCreature(player.Creature).Block;

        BespokeCardMirrors.SacrificeOnPlay(
            card,
            new CardOnPlayMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = predicted,
                CardPlay = cardPlay,
            });

        AssertSuspended(fixture, completed: false, "Sacrifice");
        if (fixture.Simulator.State.GetCreature(player.Creature).Block != blockBefore)
            throw new InvalidOperationException("牺牲在击杀链选择挂起后仍结算了格挡。");
    }

    private static void AssertEndTurnDamageStopsBeforePowerClearAtPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        CentennialPuzzle centennial = (CentennialPuzzle)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<CentennialPuzzle>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(simulatedCombat, player, centennial);
        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        SimCreatureState playerState = simulator.State.GetCreature(player.Creature);
        playerState.SetMaxHp(Math.Max(50, playerState.MaxHp));
        playerState.CurrentHp = playerState.MaxHp;
        Creature applier = simulatedCombat.Enemies.First();
        MagicBombPower bomb = simulatedCombat.AddPowerInstance<MagicBombPower>(
            player.Creature,
            1,
            applier);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = EndTurnPowerSupport.TriggerRegular(
                simulator,
                simulatedCombat,
                CombatSide.Player,
                [player.Creature]);

            if (completed)
                throw new InvalidOperationException("Magic Bomb 在内层选择待定时错误报告完成。");
            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "Magic Bomb");
            if (bomb.Amount != 1)
                throw new InvalidOperationException("Magic Bomb 在伤害选择挂起后仍被清除。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertPanacheStopsBeforeCounterResetAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PanachePower power = fixture.Combat.AddPowerInstance<PanachePower>(
            player.Creature,
            1,
            player.Creature);
        PanachePredictionState panache = fixture.Simulator.StateStore.Get(
            power,
            () => new PanachePredictionState(power));
        panache.AlreadyApplied = true;
        panache.CardsLeft = 1;
        PredictedCard card = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);

        AfterCardPlayedMirrors.Invoke(
            power,
            new AfterCardPlayedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = card,
                CardPlay = CreatePendingTailCardPlay(card, player),
            });

        AssertSuspended(fixture, completed: false, "Panache");
        if (panache.CardsLeft != 0)
            throw new InvalidOperationException($"Panache 在伤害选择挂起后仍重置了计数：{panache.CardsLeft}。");
    }

    private static void AssertExhaustDamageStopsBeforeRelicRecordAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        CharonsAshes relic = (CharonsAshes)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<CharonsAshes>(),
            player);
        PredictedCard card = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        ActionRelicTriggerRecorder recorder = new();
        recorder.BeginAction(0);
        fixture.Simulator.ActionRelicTriggers = recorder;

        AfterCardExhaustedMirrors.Invoke(
            relic,
            new AfterCardExhaustedMirrorContext
            {
                Simulator = fixture.Simulator,
                Card = card,
                CausedByEthereal = false,
            });

        AssertSuspended(fixture, completed: false, "exhaust damage relic");
        if (recorder.ForAction(0).Any(trigger => trigger.RelicId == relic.Id.Entry))
            throw new InvalidOperationException("穷尽伤害遗物在选择挂起后仍记录了已完成触发。");
    }

    private static void AssertMonsterSelfKillStopsBeforeCompletionAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        Creature victim = fixture.Victims[0];
        ForecastMove move = new(
            victim,
            new MoveState("EXPLODE_MOVE", _ => Task.CompletedTask, new StunIntent()),
            []);

        bool supported = MonsterMoveEffects.Apply(
            fixture.Simulator,
            fixture.Combat,
            move,
            player.Creature,
            out bool killedOwner);

        AssertSuspended(fixture, completed: false, "monster self-kill");
        if (!supported || killedOwner)
            throw new InvalidOperationException("怪物自杀在死亡选择挂起后错误提交了 killedOwner。");
    }

    private static void AssertTheBombStopsBeforeAmountConsumptionAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        TheBombPower bomb = fixture.Combat.AddPowerInstance<TheBombPower>(
            player.Creature,
            1,
            player.Creature);
        PowerAmountPredictionState amount = fixture.Simulator.StateStore.GetPowerAmount(bomb);

        BeforeSideTurnEndMirrors.Invoke(
            bomb,
            new BeforeSideTurnEndMirrorContext
            {
                Simulator = fixture.Simulator,
                Side = CombatSide.Player,
                Participants = [player.Creature],
            });

        AssertSuspended(fixture, completed: false, "The Bomb");
        if (amount.Amount != 1)
            throw new InvalidOperationException($"The Bomb 在伤害选择挂起后仍消耗了层数：{amount.Amount}。");
    }

    private static void AssertDelayedStarsStopBeforePowerClearAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        _ = fixture.Combat.AddPowerInstance<BlackHolePower>(
            player.Creature,
            1,
            player.Creature);
        fixture.Combat.Apply<StarNextTurnPower>(
            player.Creature,
            1,
            player.Creature);

        bool completed = PersistentPowerSupport.TriggerAfterEnergyReset(
            fixture.Simulator,
            fixture.Combat,
            player);

        AssertSuspended(fixture, completed, "delayed stars");
        if (fixture.Combat.GetAmount<StarNextTurnPower>(player.Creature) != 1)
            throw new InvalidOperationException("下回合星能在 GainStars 触发选择挂起后仍被清除。");
    }

    private static void AssertInitialPlatingStopsAtPending(CombatState combat, Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareLethalHittableEnemies(fixture);
        Creature firstOwner = fixture.PrimaryEnemy;
        Creature secondOwner = fixture.Victims[0];
        _ = fixture.Combat.AddPowerInstance<PlatingPower>(firstOwner, 1, firstOwner);
        _ = fixture.Combat.AddPowerInstance<PlatingPower>(secondOwner, 1, secondOwner);
        _ = fixture.Combat.AddPowerInstance<JuggernautPower>(firstOwner, 20, firstOwner);
        fixture.Combat.CurrentSide = CombatSide.Player;
        fixture.Combat.RoundNumber = 1;
        int secondBlockBefore = fixture.Simulator.State.GetCreature(secondOwner).Block;

        bool pending = TurnStartPowerSupport.TriggerBeforeSideTurnStart(
            fixture.Simulator,
            fixture.Combat,
            [player.Creature]);

        if (!pending)
            throw new InvalidOperationException("初始 Plating 格挡触发内层选择后没有报告挂起。");
        AssertPendingChoice(
            fixture.Combat,
            fixture.Hellraiser.Id.Entry,
            PlanChoiceEffect.MoveToHand,
            "initial Plating");
        if (fixture.Simulator.State.GetCreature(secondOwner).Block != secondBlockBefore)
            throw new InvalidOperationException("初始 Plating 挂起后仍给后续生物增加了格挡。");
    }

    private static void AssertShipInBottleStopsBeforeDelayedBlockAtPending(
        CombatState combat,
        Player player)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareLethalHittableEnemies(fixture);
        _ = fixture.Combat.AddPowerInstance<JuggernautPower>(
            player.Creature,
            1,
            player.Creature);
        ShipInABottle potion = (ShipInABottle)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<ShipInABottle>(),
            player);

        bool completed = PotionOnUseSupport.Use(
            fixture.Simulator,
            fixture.Combat,
            potion,
            player.Creature);

        AssertSuspended(fixture, completed, "Ship in a Bottle");
        if (fixture.Combat.GetAmount<BlockNextTurnPower>(player.Creature) != 0)
            throw new InvalidOperationException("瓶中船在 GainBlock 选择挂起后仍施加了下回合格挡。");
    }

    private static void AssertMirroredPotionStopsBeforePostDrawEffectAtPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PrepareDamagePendingChoiceFixture(simulator, simulatedCombat, player);
        Clarity potion = (Clarity)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<Clarity>(),
            player);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = PotionOnUseSupport.Use(
                simulator,
                simulatedCombat,
                potion,
                player.Creature);

            if (completed)
                throw new InvalidOperationException("镜像抽牌药水在内层选择待定时错误报告完成。");
            AssertPendingChoice(
                simulatedCombat,
                CanonicalModels.Power<HellraiserPower>().Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "mirrored draw potion");
            if (simulatedCombat.GetAmount<ClarityPower>(player.Creature) != 0)
                throw new InvalidOperationException("清醒药水抽牌挂起后仍施加了后续 Power。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertReplaySettlementStopsBeforeNormalizationAtPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);
        StabilizeForkBoundaryEnemies(simulator);

        PredictedCard sentinel = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        PredictedCard option = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        simulator.AddGeneratedCardToCombat(
            sentinel,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            option,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        Creature applier = simulatedCombat.Enemies.First();
        simulatedCombat.Apply<HexPower>(player.Creature, 1, applier);

        const string choiceSource = "REPLAY_SETTLEMENT_TEST";
        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool choiceResolved = ((ICombatPredictionChoiceSink)simulatedCombat).ResolvePileChoice(
                simulator,
                choiceSource,
                player,
                PileType.Draw,
                1);
            if (choiceResolved)
                throw new InvalidOperationException("动作边界测试没有建立待处理选择。");

            bool completed = CombatBeamSolver.SettleReplayActionBoundary(simulator, simulatedCombat);

            if (completed)
                throw new InvalidOperationException("动作边界存在待处理选择时错误报告结算完成。");
            AssertPendingChoice(
                simulatedCombat,
                choiceSource,
                PlanChoiceEffect.MoveToHand,
                "replay settlement");
            if (sentinel.Preview.Affliction != null)
                throw new InvalidOperationException("动作边界待处理选择后仍执行了卡牌异常状态归一化。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertManualPotionWrapperStopsBeforeAfterPotionHooksAtPending(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PrepareDamagePendingChoiceFixture(simulator, simulatedCombat, player);
        ReptileTrinket relic = (ReptileTrinket)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<ReptileTrinket>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(simulatedCombat, player, relic);
        Clarity potion = (Clarity)PredictionUtils.CreatePotion(
            CanonicalModels.Potion<Clarity>(),
            player);
        ReplaceFirstPotionSlotForPendingTailTest(simulatedCombat, player, potion);

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            if (!simulator.ManualUse(potion, player.Creature, out _))
                throw new InvalidOperationException("手动药水包装器没有启动有效的药水使用。");
            AssertPendingChoice(
                simulatedCombat,
                CanonicalModels.Power<HellraiserPower>().Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "manual potion wrapper");
            if (simulatedCombat.GetAmount<ReptileTrinketPower>(player.Creature) != 0)
            {
                throw new InvalidOperationException(
                    "手动药水镜像挂起后仍执行了 AfterPotionUsed 遗物阶段。");
            }
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void PrepareLethalHittableEnemies(PendingEnemyDeathFixture fixture)
    {
        foreach (Creature enemy in fixture.Simulator.State.HittableEnemies.ToArray())
        {
            SimCreatureState state = fixture.Simulator.State.GetCreature(enemy);
            state.SetMaxHp(1);
            state.CurrentHp = 1;
        }
    }

    private static void ReplaceFirstPotionSlotForPendingTailTest(
        SimulatedCombatState combat,
        Player player,
        PotionModel potion)
    {
        FieldInfo slotsField = typeof(SimulatedCombatState).GetField(
            "_potionSlots",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SimulatedCombatState).FullName, "_potionSlots");
        ForkableDictionary<(Player Player, int Slot), PotionModel?> slots =
            (ForkableDictionary<(Player Player, int Slot), PotionModel?>?)slotsField.GetValue(combat)
            ?? throw new InvalidOperationException("药水挂起测试找不到模拟药水槽账本。");
        (Player Player, int Slot) key = slots.Keys
            .Where(key => ReferenceEquals(key.Player, player))
            .OrderBy(key => key.Slot)
            .FirstOrDefault();
        if (!ReferenceEquals(key.Player, player))
            throw new InvalidOperationException("药水挂起测试找不到可替换的玩家药水槽。");
        slots[key] = potion;
    }

    private static PendingEnemyDeathFixture CreatePendingEnemyDeathFixture(
        CombatState combat,
        Player player,
        int victimCount = 1)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        GremlinHorn horn = (GremlinHorn)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GremlinHorn>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(simulatedCombat, player, horn);
        Creature primary = simulatedCombat.Enemies.First(enemy => enemy.IsPrimaryEnemy);
        List<Creature> victims = new(victimCount);
        for (int index = 0; index < victimCount; index++)
        {
            Creature victim = MonsterSpawnSupport.Spawn<GasBomb>(
                simulator,
                simulatedCombat,
                primary,
                slot: null,
                minion: true);
            SimCreatureState victimState = simulator.State.GetCreature(victim);
            victimState.SetMaxHp(1);
            victimState.CurrentHp = 1;
            victims.Add(victim);
        }
        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        return new PendingEnemyDeathFixture(
            simulator,
            simulatedCombat,
            primary,
            victims,
            hellraiser);
    }

    private static void AssertSuspended(
        PendingEnemyDeathFixture fixture,
        bool completed,
        string label)
    {
        if (completed)
            throw new InvalidOperationException($"{label} 在内层选择待定时错误报告完成。");
        AssertPendingChoice(
            fixture.Combat,
            fixture.Hellraiser.Id.Entry,
            PlanChoiceEffect.MoveToHand,
            label);
    }

    private static CardPlay CreatePendingTailCardPlay(PredictedCard card, Player player)
        => new()
        {
            Card = card.Original,
            Player = player,
            Target = null,
            ResultPile = PileType.Discard,
            Resources = default,
            IsAutoPlay = false,
            PlayIndex = 0,
            PlayCount = 1,
        };

    private sealed class PendingEnemyDeathFixture(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature primaryEnemy,
        IReadOnlyList<Creature> victims,
        HellraiserPower hellraiser) : IDisposable
    {
        public CombatPredictionSimulator Simulator { get; } = simulator;
        public SimulatedCombatState Combat { get; } = combat;
        public Creature PrimaryEnemy { get; } = primaryEnemy;
        public IReadOnlyList<Creature> Victims { get; } = victims;
        public HellraiserPower Hellraiser { get; } = hellraiser;

        public void Dispose() => Combat.EndActionChoices();
    }
}
