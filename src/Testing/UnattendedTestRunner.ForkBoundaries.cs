using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using STS2RitsuLib.Models.Capabilities;
using System.Collections;
using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static readonly object RitsuDefaultCapabilityRegistrationTestLock = new();
    private static bool _ritsuDefaultCapabilityRegistrationTestCompleted;

    private static void AssertForkBoundaries(CombatState combat, Player player)
    {
        CardModel card = player.PlayerCombatState?.Hand.Cards.FirstOrDefault()
            ?? throw new InvalidOperationException("Fork 边界测试要求手牌中至少有一张牌。");
        AssertHistoryRetentionBoundaries(combat, player, card);
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        AssertSimulationCardPileLookupFastPath(player, card);
        AssertRootColorlessGenerationPoolCache(simulator, player);
        AssertRootCharacterAttackGenerationPoolCache(simulator, player);
        AssertRitsuExtendedCapabilityFastPaths(player);
        AssertRitsuCapabilityFastPath(simulator, player, card);
        AssertChoiceKeyCache(simulator, player, card);
        AssertChoiceTokenSurvivesStateMutation(combat, player, card);
        AssertMonsterAiUsesCapturedMachine(combat);
        AssertCardCompletionSettlesPowerAmountChanges(combat, player);
        AssertBeforeCardPlayedPowerConsumptionCommits(combat, player);
        AssertPlayerPowerHooksPrecedeCombatCards(combat, player);
        AssertNestedVoidFormRequestsTurnEnd(combat, player);
        AssertVoidFormOpportunityUsesAreFinite(combat, player);
        AssertKnowledgeDemonCurseStaysOutOfCardChoiceCursor();
        AssertExistingPilePotionChoiceReplaysAcrossFork(combat, player);
        AssertGeneratedCardCreatorDrivesSupermassive(combat, player);
        AssertLiveOriginalRemovalDoesNotAffectSnapshot(combat, player, card);
        AssertReplayCardIdentityDistinguishesGeneratedCopies(simulator, player);
        AssertDeploymentCardIdentitySurvivesEarlierCopyLeavingHand(card);
        AssertMissingSandpitIsACompletedFranticEscape(combat, player);
        AssertTerminalMonsterMovesStopScheduling(combat, player);
        AssertRevivingCreatureRejectsNewPowers(combat, player);
        AssertRosterSinkRemovalUsesUpdatedRoster(combat);

        using (simulator.PushActionSource(card, PredictionActionKind.CardPlay))
            AssertForkRejected(simulator, "completed actions");

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            AssertForkRejected(simulator, "action choice resolution");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }

        using (simulatedCombat.BeginCardExecutionScope())
            AssertForkRejected(simulator, "card execution");

        simulator.ActionRelicTriggers = new ActionRelicTriggerRecorder();
        AssertForkRejected(simulator, "action relic triggers");
        simulator.ActionRelicTriggers = null;

        PenNib relic = ModelDb.All.OfType<PenNib>().Single();
        PenNibPredictionState penNib = simulator.StateStore.Get(
            (AbstractModel)relic,
            () => new PenNibPredictionState(relic));
        penNib.AttackToDouble = card;
        AssertForkRejected(simulator, "Pen Nib");
        penNib.AttackToDouble = null;

        PaelsLegion paelsLegion = ModelDb.All.OfType<PaelsLegion>().Single();
        PaelsLegionPredictionState paelsState = simulator.StateStore.Get(
            (AbstractModel)paelsLegion,
            () => new PaelsLegionPredictionState(paelsLegion));
        CardPlay paelsPlay = new()
        {
            Card = card,
            Player = player,
            Target = null,
            ResultPile = PileType.Discard,
            Resources = default,
            IsAutoPlay = false,
            PlayIndex = 0,
            PlayCount = 1,
        };
        paelsState.AffectedCardPlay = paelsPlay;
        AssertForkRejected(simulator, "Pael's Legion");
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, paelsPlay, completed: true);
        if (paelsState.AffectedCardPlay != null
            || paelsState.Cooldown != paelsLegion.DynamicVars["Turns"].IntValue
            || !paelsState.TriggeredBlockLastTurn)
        {
            throw new InvalidOperationException("佩尔军团没有在完整 CardPlay 边界提交格挡触发。");
        }
        paelsState.AffectedCardPlay = paelsPlay;
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, paelsPlay, completed: false);
        if (paelsState.AffectedCardPlay != null)
            throw new InvalidOperationException("佩尔军团没有在中止 CardPlay 边界清理瞬时状态。");

        Vambrace vambraceRelic = ModelDb.All.OfType<Vambrace>().Single();
        VambracePredictionState vambrace = simulator.StateStore.Get(
            (AbstractModel)vambraceRelic,
            () => new VambracePredictionState(vambraceRelic));
        vambrace.TriggeringCard = card;
        vambrace.BlockGainedThisCombat = true;
        CombatPredictionSimulator vambraceFork = simulator.Fork();
        VambracePredictionState forkedVambrace = vambraceFork.StateStore.GetReadOnly(
            (AbstractModel)vambraceRelic,
            () => new VambracePredictionState(vambraceRelic));
        if (!ReferenceEquals(forkedVambrace.TriggeringCard, card)
            || !forkedVambrace.BlockGainedThisCombat)
        {
            throw new InvalidOperationException("Vambrace 稳定战斗状态没有跨 Fork 保留。");
        }
        vambrace.TriggeringCard = null;
        vambrace.BlockGainedThisCombat = false;

        CurlUpPredictionState curlUp = simulator.StateStore.Get<CurlUpPredictionState>(card);
        curlUp.PlayedCard = card;
        AssertForkRejected(simulator, "Curl Up");
        curlUp.PlayedCard = null;

        CombatPredictionSimulator pendingHistory = new(new SimulatedCombatState(combat));
        pendingHistory.History.CardDrawn(new PredictedCard(card), fromHandDraw: false);
        AssertForkRejected(pendingHistory, "unresolved deferred entries");

        int originalEnergy = simulator.State.GetPlayerCombatState(player).Energy;
        CombatPredictionSimulator fork = simulator.Fork();
        fork.State.GetPlayerCombatState(player).GainEnergy(1);
        if (simulator.State.GetPlayerCombatState(player).Energy != originalEnergy)
            throw new InvalidOperationException("稳定边界 Fork 没有隔离玩家能量状态。");

        AssertPredictedCardForkOwnershipAndObservers(combat, player, card);
        AssertAmountOnTurnStartCacheReuse(combat, player);
        AssertPowerListenerCacheTransitionsAndForkIsolation(combat, player);
        AssertSparsePowerAfflictionCardTracking(combat, player, card);
        AssertProjectedShuffleEquivalence(simulator, player);
        AssertSpawnHpUsesSimulatedCreatureState(combat);
        AssertPendingSpawnCanEnterIllusionRevive(combat);
        AssertPendingRandomBranchSpawnRollsAtTurnBoundary(combat);
        AssertDefeatedEnemyRejectsLatePowerApplication(combat, player);
        AssertOrbSlotAdditionCapsAtVanillaMaximum(combat, player);
        AssertAutoPlayedBlockHonorsPriorTurnHistory(combat, player, card);
        AssertOrbDeathsSettleBetweenTurnEndPassives(combat, player);
        AssertWhisperingEarringOnlyRunsOnFirstTurn(simulator, simulatedCombat, player);
        AssertPredictionForkContextIdentityIndex();
        AssertForkableListEnumeration();
    }

    private static void AssertOrbSlotAdditionCapsAtVanillaMaximum(CombatState combat, Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimOrbQueue queue = simulator.State.GetPlayerCombatState(player).OrbQueue;
        if (queue.Capacity >= OrbQueue.maxCapacity)
            throw new InvalidOperationException("轨道上限测试要求初始容量低于原版上限。");

        queue.AddCapacity(OrbQueue.maxCapacity - queue.Capacity - 1);
        simulator.AddOrbSlots(player, 2);
        if (queue.Capacity != OrbQueue.maxCapacity)
            throw new InvalidOperationException("增加轨道槽位没有遵守原版容量上限。");

        simulator.AddOrbSlots(player, 1);
        if (queue.Capacity != OrbQueue.maxCapacity)
            throw new InvalidOperationException("已满的轨道仍然增加了容量。");
    }

    private static void AssertAutoPlayedBlockHonorsPriorTurnHistory(
        CombatState combat,
        Player player,
        CardModel liveCard)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PredictedCard priorBlockCard = simulator.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("自动出牌历史测试找不到根卡牌。");
        simulatedCombat.RecordCardPlayed(priorBlockCard, gainedBlock: true);
        simulatedCombat.Apply<UnmovablePower>(player.Creature, 1, player.Creature);

        PredictedCard defend = new(simulatedCombat.CreateCard<DefendDefect>(player));
        simulator.AddToPile(defend, PileType.Draw, CardPilePosition.Top);
        int blockBefore = simulator.State.GetCreature(player.Creature).Block;
        simulator.AutoPlayFromDrawPile(player, 1, CardPilePosition.Top);
        int gained = simulator.State.GetCreature(player.Creature).Block - blockBefore;
        int expected = defend.Preview.DynamicVars.Block.IntValue;
        if (gained != expected)
        {
            throw new InvalidOperationException(
                $"自动出牌忽略本回合既有卡牌格挡历史：expected={expected} actual={gained}。");
        }
    }

    private static void AssertOrbDeathsSettleBetweenTurnEndPassives(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature enemy = simulatedCombat.Enemies.First();
        simulator.State.GetCreature(enemy).CurrentHp = 3;
        simulatedCombat.Apply<InfestedPower>(enemy, 1, enemy);

        SimOrbQueue queue = simulator.State.GetPlayerCombatState(player).OrbQueue;
        queue.Clear();
        queue.AddCapacity(2);
        LightningOrb lightning = (LightningOrb)ModelDb.Orb<LightningOrb>().ToMutable();
        lightning.Owner = player;
        GlassOrb glass = (GlassOrb)ModelDb.Orb<GlassOrb>().ToMutable();
        glass.Owner = player;
        if (!queue.TryEnqueue(lightning) || !queue.TryEnqueue(glass))
            throw new InvalidOperationException("回合末球结算测试无法建立球队列。");

        queue.BeforeTurnEnd(simulator);
        Creature[] wrigglers = simulatedCombat.Enemies
            .Where(candidate => candidate.Monster is MegaCrit.Sts2.Core.Models.Monsters.Wriggler)
            .ToArray();
        if (wrigglers.Length != 4)
            throw new InvalidOperationException($"感染死亡后生成扭动虫数量错误：{wrigglers.Length}。");
        foreach (Creature wriggler in wrigglers)
        {
            SimCreatureState state = simulator.State.GetCreature(wriggler);
            if (state.MaxHp - state.CurrentHp != 4)
            {
                throw new InvalidOperationException(
                    $"后续玻璃球没有命中新生成扭动虫：hp={state.CurrentHp}/{state.MaxHp}。");
            }
        }
    }

    private static void AssertKnowledgeDemonCurseStaysOutOfCardChoiceCursor()
    {
        PlanCardChoice actionChoice = new(
            PlanChoiceEffect.Discard,
            PileType.Hand,
            [],
            "ACTION");
        PlanCardChoice turnStartChoice = new(
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            [],
            "TURN_START",
            Timing: PlanChoiceTiming.PlayerTurnEnd);
        PlanCardChoice knowledgeCurse = new(
            PlanChoiceEffect.ApplyKnowledgeCurse,
            PileType.None,
            [],
            "KNOWLEDGE_DEMON:1:0",
            Timing: PlanChoiceTiming.EnemyTurn);
        PlanAction action = new(
            PlanActionKind.PlayCard,
            Turn: 1,
            Choice: actionChoice,
            TurnStartChoices: [turnStartChoice, knowledgeCurse]);
        MethodInfo method = typeof(CombatBeamSolver).GetMethod(
            "ActionChoicesForReplay",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(CombatBeamSolver).FullName,
                "ActionChoicesForReplay");
        IReadOnlyList<PlanCardChoice> choices =
            (IReadOnlyList<PlanCardChoice>?)method.Invoke(null, [action])
            ?? throw new InvalidOperationException("出牌选牌游标测试没有生成选择列表。");
        if (!choices.Contains(actionChoice)
            || !choices.Contains(turnStartChoice)
            || choices.Contains(knowledgeCurse))
        {
            throw new InvalidOperationException(
                "出牌选牌游标没有精确排除知识恶魔诅咒选择。");
        }
    }

    private static void AssertRevivingCreatureRejectsNewPowers(CombatState combat, Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        _ = new CombatPredictionSimulator(simulatedCombat);
        Creature enemy = simulatedCombat.Enemies.First();
        FieldInfo phasesField = typeof(SimulatedCombatState).GetField(
            "_deathPhases",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SimulatedCombatState).FullName, "_deathPhases");
        phasesField.SetValue(
            simulatedCombat,
            new ForkableDictionary<Creature, PredictedDeathPhase>
            {
                [enemy] = PredictedDeathPhase.Reviving,
            });
        simulatedCombat.Apply<WeakPower>(enemy, 1, player.Creature);
        if (simulatedCombat.GetAmount<WeakPower>(enemy) != 0)
            throw new InvalidOperationException("复活中的怪物错误接受了新 Power。");
    }

    private static void AssertMonsterAiUsesCapturedMachine(CombatState combat)
    {
        MonsterModel live = combat.Enemies.FirstOrDefault()?.Monster
            ?? throw new InvalidOperationException("怪物行动快照测试要求至少有一名敌人。");
        MonsterModel detached = PredictionUtils.CloneModelForSimulation(live);
        BranchMonsterAiState state = BranchMonsterAi.Capture(detached);
        FieldInfo field = typeof(MonsterModel).GetField(
            "_moveStateMachine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MonsterModel).FullName, "_moveStateMachine");
        field.SetValue(detached, null);

        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        _ = BranchMonsterAi.Advance(state, simulator, (SimulatedCombatState)simulator.State.CombatState);
    }

    private static void AssertChoiceTokenSurvivesStateMutation(
        CombatState combat,
        Player player,
        CardModel liveCard)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        PredictedCard card = state.FindCard(liveCard)
            ?? throw new InvalidOperationException("选牌身份测试找不到目标手牌。");
        CardChoiceSpec spec = new(
            PlanChoiceEffect.Discard,
            PileType.Hand,
            1,
            1,
            [card],
            state.Hand.Cards,
            ReplacementValue: 0d);
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(
            spec,
            [card.Preview.Id.Entry]);

        card.MutablePreview.ExhaustOnNextPlay = !card.Preview.ExhaustOnNextPlay;
        IReadOnlyList<PredictedCard> selected = CardChoiceSupport.ResolveStandaloneChoice(
            simulator,
            choice,
            [card],
            expectedCount: 1,
            PileType.Hand);
        if (!ReferenceEquals(selected.Single(), card))
            throw new InvalidOperationException("选牌令牌没有在卡牌状态变化后保持实体身份。");
    }

    private static void AssertCardCompletionSettlesPowerAmountChanges(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        simulatedCombat.Apply<StrengthPower>(
            player.Creature,
            1,
            player.Creature);
        StrengthPower power = simulatedCombat.GetPower<StrengthPower>(player.Creature)
            ?? throw new InvalidOperationException("力量影子层数测试没有建立力量。");
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, simulatedCombat);
        PowerAmountPredictionState shadow = simulator.StateStore.GetPowerAmount(power);
        shadow.Amount = power.Amount + 1;

        ((ICombatPredictionCardExecutionSink)simulatedCombat).CompleteCardExecution(simulator);
        if (power.Amount != shadow.Amount)
            throw new InvalidOperationException("出牌事务结束后没有提交力量影子层数。");
        _ = simulator.Fork();
    }

    private static void AssertBeforeCardPlayedPowerConsumptionCommits(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        simulatedCombat.Apply<FreePowerPower>(player.Creature, 2, player.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, simulatedCombat);
        PredictedCard powerCard = PredictedCard.Create(
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.MachineLearning>(),
            player);
        simulator.AddGeneratedCardToCombat(
            powerCard,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);

        simulator.ManualPlay(powerCard, target: null, out _);
        if (simulatedCombat.GetAmount<FreePowerPower>(player.Creature) != 1)
            throw new InvalidOperationException("免费能力层数没有在卡牌效果开始前提交消耗。");
        _ = simulator.Fork();
    }

    private static void AssertPlayerPowerHooksPrecedeCombatCards(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PredictedCard howl = PredictedCard.Create(ModelDb.Card<HowlFromBeyond>(), player);
        simulator.AddGeneratedCardToCombat(
            howl,
            PileType.Exhaust,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulatedCombat.Apply<StampedePower>(player.Creature, 1, player.Creature);

        IReadOnlyList<AbstractModel> listeners = simulatedCombat.IterateHookListeners().ToArray();
        int powerIndex = listeners.ToList().FindIndex(listener => listener is StampedePower);
        int cardIndex = listeners.ToList().FindIndex(listener => ReferenceEquals(listener, howl.Preview));
        if (powerIndex < 0 || cardIndex < 0 || powerIndex >= cardIndex)
        {
            throw new InvalidOperationException(
                $"玩家能力与战斗卡牌 Hook 顺序错误：power={powerIndex} card={cardIndex}。");
        }
    }

    private static void AssertNestedVoidFormRequestsTurnEnd(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.DrawPile.Cards.ToArray());

        PredictedCard catastrophe = PredictedCard.Create(ModelDb.Card<Catastrophe>(), player);
        PredictedCard voidForm = PredictedCard.Create(ModelDb.Card<VoidForm>(), player);
        simulator.AddGeneratedCardToCombat(
            catastrophe,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            voidForm,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.ManualPlay(catastrophe, target: null, out _);

        if (!simulatedCombat.PlayerTurnEndRequested
            || !simulatedCombat.ConsumePlayerTurnEndRequest()
            || simulatedCombat.PlayerTurnEndRequested)
        {
            throw new InvalidOperationException("横祸自动打出虚空形态后没有产生一次性结束回合请求。");
        }
        _ = simulator.Fork();
    }

    private static void AssertVoidFormOpportunityUsesAreFinite(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.Hand.Cards.ToArray());
        for (int index = 0; index < 2; index++)
        {
            simulator.AddGeneratedCardToCombat(
                PredictedCard.Create(ModelDb.Card<DefendDefect>(), player),
                PileType.Hand,
                player,
                resultKind: CardGenerationResultKind.Fixed);
        }
        simulatedCombat.Apply<VoidFormPower>(player.Creature, 1, player.Creature);
        VoidFormPower power = simulatedCombat.GetPower<VoidFormPower>(player.Creature)
            ?? throw new InvalidOperationException("虚空形态机会价值测试没有建立 Power。");
        int oneFreeUse = CombatBeamSolver.CaptureVoidFormOpportunityValueForTesting(
            simulator,
            simulatedCombat,
            playerState,
            player.Creature);
        simulatedCombat.SetPowerAmount(power, 2);
        int twoFreeUses = CombatBeamSolver.CaptureVoidFormOpportunityValueForTesting(
            simulator,
            simulatedCombat,
            playerState,
            player.Creature);
        if (oneFreeUse <= 0 || twoFreeUses != oneFreeUse * 2)
        {
            throw new InvalidOperationException(
                $"虚空形态免费格没有按剩余次数计价：one={oneFreeUse} two={twoFreeUses}。");
        }
    }

    private static void AssertExistingPilePotionChoiceReplaysAcrossFork(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parent = new(parentCombat);
        for (int index = 0; index < 3; index++)
        {
            PredictedCard pommel = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
            pommel.Upgrade();
            parent.AddGeneratedCardToCombat(
                pommel,
                PileType.Draw,
                player,
                resultKind: CardGenerationResultKind.Fixed);
        }

        DropletOfPrecognition potion = (DropletOfPrecognition)ModelDb.Potion<DropletOfPrecognition>().ToMutable();
        potion.Owner = player;
        CombatPredictionSimulator probe = parent.Fork();
        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(
            PotionChoiceSupport.GetSpec(probe, potion),
            ["POMMEL_STRIKE"]);
        CombatPredictionSimulator replay = parent.Fork();
        PotionChoiceSupport.Apply(replay, potion, choice);
        if (!replay.State.GetPlayerCombatState(player).Hand.Cards.Any(card =>
                card.Preview is PommelStrike && card.Preview.IsUpgraded))
        {
            throw new InvalidOperationException("预知水滴的重复牌候选没有在同父节点 Fork 上稳定回放。");
        }
    }

    private static void AssertGeneratedCardCreatorDrivesSupermassive(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PredictedCard supermassive = PredictedCard.Create(
            ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Supermassive>(),
            player);
        decimal baseline = CalculateSupermassive(simulator, supermassive);

        simulator.CreateAndAddGeneratedCardsToCombat<MegaCrit.Sts2.Core.Models.Cards.Debris>(
            player,
            PileType.Discard,
            1,
            creator: null);
        if (CalculateSupermassive(simulator, supermassive) != baseline)
            throw new InvalidOperationException("无创建者的生成牌被超质量体计入。");

        simulator.CreateAndAddGeneratedCardsToCombat<MegaCrit.Sts2.Core.Models.Cards.Debris>(
            player,
            PileType.Discard,
            1,
            creator: player);
        decimal expected = baseline + supermassive.Preview.DynamicVars.ExtraDamage.BaseValue;
        if (CalculateSupermassive(simulator, supermassive) != expected)
            throw new InvalidOperationException("玩家创建的生成牌没有被超质量体计入。");
    }

    private static void AssertLiveOriginalRemovalDoesNotAffectSnapshot(
        CombatState combat,
        Player player,
        CardModel liveCard)
    {
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        PredictedCard predicted = simulator.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("实机原牌隔离测试找不到预测卡牌。");
        predicted.MaterializePreview();
        bool removed = liveCard.HasBeenRemovedFromState;
        try
        {
            liveCard.HasBeenRemovedFromState = true;
            SimCardPileAddResult result = simulator.AddToPile(predicted, PileType.Discard);
            if (!result.Success
                || predicted.GetPile(simulator.State)?.Type != PileType.Discard)
            {
                throw new InvalidOperationException("实机原牌移出战斗污染了预测快照移牌。");
            }
        }
        finally
        {
            liveCard.HasBeenRemovedFromState = removed;
        }
    }

    private static void AssertReplayCardIdentityDistinguishesGeneratedCopies(
        CombatPredictionSimulator simulator,
        Player player)
    {
        PredictedCard deckCard = simulator.State.GetPlayerCombatState(player).AllCards
            .FirstOrDefault(card => card.Preview.DeckVersion != null)
            ?? throw new InvalidOperationException("回放卡牌身份测试找不到带牌组版本的卡牌。");
        deckCard.MaterializePreview();
        PredictedCard generatedCopy = deckCard.CreateClone();
        string deckKey = CardChoiceSupport.ChoiceCardKey(deckCard);
        string generatedKey = CardChoiceSupport.ChoiceCardKey(generatedCopy);
        if (string.Equals(deckKey, generatedKey, StringComparison.Ordinal))
            throw new InvalidOperationException("回放卡牌身份没有区分牌组原牌与生成复制。");
        PlanAction action = new(
            PlanActionKind.PlayCard,
            1,
            generatedCopy.Preview.Id.Entry,
            CardStateKey: generatedKey);
        if (!ReferenceEquals(
                CombatBeamSolver.FindCardForReplay([deckCard, generatedCopy], action),
                generatedCopy))
        {
            throw new InvalidOperationException("回放卡牌身份没有选中计划中的生成复制。");
        }
    }

    private static void AssertDeploymentCardIdentitySurvivesEarlierCopyLeavingHand(
        CardModel liveCard)
    {
        CardModel canonical = ModelDb.AllCards.Single(card => card.Id == liveCard.Id);
        CardModel earlierCopy = canonical.ToMutable();
        CardModel plannedCopy = canonical.ToMutable();
        plannedCopy.ExhaustOnNextPlay = !earlierCopy.ExhaustOnNextPlay;
        string plannedStateKey = CardChoiceSupport.ChoiceCardKey(plannedCopy);
        PlanAction action = new(
            PlanActionKind.PlayCard,
            1,
            plannedCopy.Id.Entry,
            CardOccurrence: 1,
            CardStateKey: plannedStateKey,
            CardStateOccurrence: 0);

        if (!ReferenceEquals(
                SolverController.FindCardForDeployment([plannedCopy], action),
                plannedCopy))
        {
            throw new InvalidOperationException(
                "实机部署没有在前一个同名实例离手后保持计划卡牌身份。");
        }
    }

    private static void AssertMissingSandpitIsACompletedFranticEscape(
        CombatState combat,
        Player player)
    {
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        simulatedCombat.IncrementSandpitTargeting(player.Creature);
    }

    private static void AssertTerminalMonsterMovesStopScheduling(
        CombatState combat,
        Player player)
    {
        MonsterModel gasBomb = ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.GasBomb>();
        if (!MonsterMoveEffects.RemovesOwner(gasBomb, "EXPLODE_MOVE")
            || MonsterMoveEffects.RemovesOwner(gasBomb, "STUNNED"))
        {
            throw new InvalidOperationException("终止型怪物行动分类不正确。");
        }

        SimulatedCombatState simulatedCombat = new(combat)
        {
            CurrentSide = CombatSide.Enemy,
        };
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature source = simulatedCombat.Enemies.First();
        Creature spawned = MonsterSpawnSupport.Spawn<MegaCrit.Sts2.Core.Models.Monsters.GasBomb>(
            simulator,
            simulatedCombat,
            source,
            slot: null);
        simulatedCombat.ForceMonsterMove(spawned, "EXPLODE_MOVE");
        ForecastMove explode = simulatedCombat.CurrentMonsterMove(spawned);
        if (!MonsterMoveEffects.Apply(
                simulator,
                simulatedCombat,
                explode,
                player.Creature,
                out bool killedOwner)
            || !killedOwner
            || simulatedCombat.ContainsCreature(spawned))
        {
            throw new InvalidOperationException("毒气弹自爆没有从活动怪物阵容移除自身。");
        }

        if (simulatedCombat.GetPredictedMoveId(spawned) != "EXPLODE_MOVE")
            throw new InvalidOperationException("终局行动结束时提前删除了怪物 AI 快照。");

        simulatedCombat.PrepareMonsterMovesForNextRound(
            simulator,
            new Dictionary<Creature, MoveState> { [spawned] = explode.Move });
        if (simulatedCombat.GetPredictedMoveId(spawned) != "EXPLODE_MOVE")
            throw new InvalidOperationException("已经离场的怪物仍推进了下一行动。");
    }

    private static void AssertRosterSinkRemovalUsesUpdatedRoster(CombatState combat)
    {
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        Creature removed = simulator.State.Enemies.First();
        simulator.State.RemoveCreature(removed);

        if (simulator.State.Enemies.Contains(removed))
            throw new InvalidOperationException("预测 roster sink 没有移除敌人。");
        if (!ReferenceEquals(simulator.State.Enemies, simulator.State.CombatState.Enemies))
        {
            throw new InvalidOperationException(
                "预测 roster sink 已更新底层列表后仍重复构造过滤视图。");
        }

        CombatPredictionSimulator fork = simulator.Fork();
        if (fork.State.Enemies.Contains(removed)
            || !ReferenceEquals(fork.State.Enemies, fork.State.CombatState.Enemies))
        {
            throw new InvalidOperationException(
                "预测 roster sink 的移除结果没有跨 Fork 保持直接 roster 视图。");
        }
    }

    private static decimal CalculateSupermassive(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        CalculatedVar calculated = (CalculatedVar)card.Preview.DynamicVars.CalculatedDamage;
        if (!CalculatedVarSpecRegistry.TryCalculate(calculated, simulator, card, target: null, out decimal value))
            throw new InvalidOperationException("超质量体计算夹具未命中支持注册表。");
        return value;
    }

    private static void AssertSpawnHpUsesSimulatedCreatureState(CombatState combat)
    {
        MegaCrit.Sts2.Core.Models.Monsters.ToughEgg canonical =
            ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.ToughEgg>();
        int minimum = canonical.MinInitialHp;
        int maximum = canonical.MaxInitialHp;
        int reserved = Math.Clamp(17, minimum, maximum);
        if (combat.Enemies.Any(enemy => enemy.MaxHp >= minimum && enemy.MaxHp <= maximum))
            return;

        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature existing = simulatedCombat.CreatePredictedMonster(
            simulator,
            (MegaCrit.Sts2.Core.Models.Monsters.ToughEgg)ModelDb
                .Monster<MegaCrit.Sts2.Core.Models.Monsters.ToughEgg>()
                .ToMutable(),
            CombatSide.Enemy,
            slot: null);
        simulatedCombat.AddPredictedMonster(existing);
        simulator.State.GetCreature(existing).SetMaxHp(reserved);
        existing.SetMaxHpInternal(minimum);
        if (simulator.State.GetCreature(existing).MaxHp != reserved)
            throw new InvalidOperationException("产卵生命判重夹具没有建立模拟/原生最大生命差异。");

        HashSet<int> spawned = [];
        for (int index = 0; index < maximum - minimum; index++)
        {
            Creature creature = simulatedCombat.CreatePredictedMonster(
                simulator,
                (MegaCrit.Sts2.Core.Models.Monsters.ToughEgg)ModelDb
                    .Monster<MegaCrit.Sts2.Core.Models.Monsters.ToughEgg>()
                    .ToMutable(),
                CombatSide.Enemy,
                slot: null);
            simulatedCombat.AddPredictedMonster(creature);
            spawned.Add(simulator.State.GetCreature(creature).MaxHp);
        }
        HashSet<int> expected = Enumerable.Range(minimum, maximum - minimum + 1)
            .Where(value => value != reserved)
            .ToHashSet();
        if (!spawned.SetEquals(expected))
        {
            throw new InvalidOperationException(
                $"新怪物生命判重没有采用模拟状态；actual={string.Join(',', spawned.Order())}。");
        }
    }

    private static void AssertPendingSpawnCanEnterIllusionRevive(CombatState combat)
    {
        SimulatedCombatState simulatedCombat = new(combat)
        {
            CurrentSide = CombatSide.Enemy,
        };
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature source = simulatedCombat.Enemies.First();
        Creature illusion = MonsterSpawnSupport.Spawn<MegaCrit.Sts2.Core.Models.Monsters.Parafright>(
            simulator,
            simulatedCombat,
            source,
            slot: null,
            minion: true);
        if (simulatedCombat.GetPredictedMoveId(illusion) != "SLAM_MOVE")
            throw new InvalidOperationException("敌方回合生成的幻象没有保留原版初始行动记录。");

        simulatedCombat.BeginIllusionRevive(illusion);
        simulatedCombat.PrepareMonsterMoveForNextRound(simulator, illusion, performedMove: null);
        if (simulatedCombat.GetPredictedMoveId(illusion) != "REVIVE_MOVE")
            throw new InvalidOperationException("幻象复活动作被待处理的初始行动覆盖。");
    }

    private static void AssertPendingRandomBranchSpawnRollsAtTurnBoundary(CombatState combat)
    {
        SimulatedCombatState simulatedCombat = new(combat)
        {
            CurrentSide = CombatSide.Enemy,
        };
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature source = simulatedCombat.Enemies.First();
        int rngBeforeSpawn = simulator.Rng.MonsterAi.Counter();
        Creature rat = MonsterSpawnSupport.Spawn<MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat>(
            simulator,
            simulatedCombat,
            source,
            slot: null);
        if (simulator.Rng.MonsterAi.Counter() != rngBeforeSpawn)
            throw new InvalidOperationException("敌方回合生成的随机初始行动怪物提前消费了怪物 RNG。");

        simulatedCombat.PrepareMonsterMoveForNextRound(simulator, rat, performedMove: null);
        if (simulator.Rng.MonsterAi.Counter() <= rngBeforeSpawn)
            throw new InvalidOperationException("随机初始行动怪物没有在回合边界消费怪物 RNG。");
        if (simulatedCombat.GetPredictedMoveId(rat) is not (
                "SCRATCH_MOVE" or "DISEASE_BITE_MOVE" or "SCREECH_MOVE"))
        {
            throw new InvalidOperationException("双尾鼠没有在回合边界得到合法的初始行动。");
        }
    }

    private static void AssertDefeatedEnemyRejectsLatePowerApplication(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        Creature enemy = simulatedCombat.Enemies.First();
        simulator.State.GetCreature(enemy).CurrentHp = 0;
        CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            simulatedCombat,
            simulatedCombat.KnownEnemies,
            new HashSet<uint>());
        simulatedCombat.Apply<VulnerablePower>(enemy, 2, player.Creature);
        if (simulatedCombat.GetAmount<VulnerablePower>(enemy) != 0)
            throw new InvalidOperationException("永久死亡的敌人仍然接收了后续 Power。");
    }

    private static void AssertWhisperingEarringOnlyRunsOnFirstTurn(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        if (!combat.RelicsOf(player).Any(static relic => relic is WhisperingEarring && !relic.IsMelted))
            return;
        int cardsBefore = simulator.State.GetPlayerCombatState(player).Hand.Cards.Count;
        combat.TriggerWhisperingEarring(simulator, player, 2, new HashSet<uint>());
        int cardsAfter = simulator.State.GetPlayerCombatState(player).Hand.Cards.Count;
        if (cardsAfter != cardsBefore)
            throw new InvalidOperationException("低语耳饰在第二回合再次自动出牌。");
    }

    private static void AssertPredictedCardForkOwnershipAndObservers(
        CombatState combat,
        Player player,
        CardModel liveCard)
    {
        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parentSimulator = new(parentCombat);
        SimPlayerCombatState parentState = parentSimulator.State.GetPlayerCombatState(player);
        PredictedCard parentCard = parentState.FindCard(liveCard)
            ?? throw new InvalidOperationException("预测卡牌 Fork 所有权测试找不到父卡牌。");
        if (!ReferenceEquals(parentCard.GetPile(parentState), parentState.Hand))
            throw new InvalidOperationException("预测卡牌没有记录父分支手牌所有权。");

        Action parentObserver = GetCardMutationObserver(parentCard);
        if (parentState.AllCards.Any(card =>
                !ReferenceEquals(GetCardMutationObserver(card), parentObserver)))
        {
            throw new InvalidOperationException("同一模拟分支没有共享单一卡牌变更 observer。");
        }
        IEnumerable<AbstractModel> parentListeners = parentCombat.IterateHookListeners();

        CombatPredictionSimulator childSimulator = parentSimulator.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)childSimulator.State.CombatState;
        SimPlayerCombatState childState = childSimulator.State.GetPlayerCombatState(player);
        PredictedCard childCard = childState.FindCard(liveCard)
            ?? throw new InvalidOperationException("预测卡牌 Fork 所有权测试找不到子卡牌。");
        Action childObserver = GetCardMutationObserver(childCard);
        if (ReferenceEquals(parentObserver, childObserver)
            || childState.AllCards.Any(card =>
                !ReferenceEquals(GetCardMutationObserver(card), childObserver)))
        {
            throw new InvalidOperationException("卡牌变更 observer 没有按父子 Fork 隔离。");
        }
        if (!ReferenceEquals(childCard.GetPile(childState), childState.Hand)
            || childCard.GetPile(parentState) is not null
            || parentCard.GetPile(childState) is not null)
        {
            throw new InvalidOperationException("预测卡牌牌堆反向引用跨 Fork 泄漏。");
        }

        IEnumerable<AbstractModel> childListenersBefore = childCombat.IterateHookListeners();
        CardModel childPreviewBefore = childCard.Preview;
        childCard.MutablePreview.ExhaustOnNextPlay = !childPreviewBefore.ExhaustOnNextPlay;
        IEnumerable<AbstractModel> childListenersAfter = childCombat.IterateHookListeners();
        if (ReferenceEquals(childListenersBefore, childListenersAfter)
            || !childListenersAfter.Contains(childCard.Preview)
            || childListenersAfter.Contains(childPreviewBefore))
        {
            throw new InvalidOperationException("子分支卡牌变更没有精确重建 Hook listener 缓存。");
        }
        if (!ReferenceEquals(parentListeners, parentCombat.IterateHookListeners())
            || !parentListeners.Contains(parentCard.Preview)
            || parentListeners.Contains(childCard.Preview))
        {
            throw new InvalidOperationException("子分支卡牌变更污染了父 Hook listener 缓存。");
        }

        childCard.MutablePreview.BaseReplayCount++;
        if (!ReferenceEquals(childListenersAfter, childCombat.IterateHookListeners()))
        {
            throw new InvalidOperationException(
                "不改变卡牌 listener 身份的字段写入错误重建了 Hook listener 缓存。");
        }

        PredictedCard attachedListenerProbe = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        if (!childSimulator.AddToPile(attachedListenerProbe, PileType.Discard).Success)
            throw new InvalidOperationException("卡牌附属 listener 测试无法加入生成牌。");
        IEnumerable<AbstractModel> listenersBeforeEnchant = childCombat.IterateHookListeners();
        attachedListenerProbe.Enchant(ModelDb.Enchantment<Clone>().ToMutable(), 1m);
        EnchantmentModel enchantment = attachedListenerProbe.Preview.Enchantment
            ?? throw new InvalidOperationException("卡牌附属 listener 测试没有添加附魔。");
        IEnumerable<AbstractModel> listenersAfterEnchant = childCombat.IterateHookListeners();
        if (ReferenceEquals(listenersBeforeEnchant, listenersAfterEnchant)
            || !listenersAfterEnchant.Contains(enchantment))
        {
            throw new InvalidOperationException("新增附魔没有精确失效 Hook listener 缓存。");
        }

        AfflictionModel affliction = childSimulator.Afflict<Bound>(attachedListenerProbe, 1)
            ?? throw new InvalidOperationException("卡牌附属 listener 测试没有添加苦难。");
        IEnumerable<AbstractModel> listenersAfterAfflict = childCombat.IterateHookListeners();
        if (ReferenceEquals(listenersAfterEnchant, listenersAfterAfflict)
            || !listenersAfterAfflict.Contains(affliction))
        {
            throw new InvalidOperationException("新增苦难没有精确失效 Hook listener 缓存。");
        }
        attachedListenerProbe.ClearAffliction();
        IEnumerable<AbstractModel> listenersAfterClear = childCombat.IterateHookListeners();
        if (ReferenceEquals(listenersAfterAfflict, listenersAfterClear)
            || listenersAfterClear.Contains(affliction))
        {
            throw new InvalidOperationException("清除苦难没有精确失效 Hook listener 缓存。");
        }
        childSimulator.RemoveFromCombat(attachedListenerProbe);

        if (!childState.Hand.Remove(childCard))
            throw new InvalidOperationException("预测卡牌所有权测试无法从子手牌移除卡牌。");
        childState.DiscardPile.Add(childCard);
        if (!ReferenceEquals(childCard.GetPile(childState), childState.DiscardPile)
            || !ReferenceEquals(parentCard.GetPile(parentState), parentState.Hand))
        {
            throw new InvalidOperationException("预测卡牌移动后没有保持父子牌堆所有权隔离。");
        }

        IEnumerable<AbstractModel> childListenersBeforeRemoval = childCombat.IterateHookListeners();
        childSimulator.RemoveFromCombat(childCard);
        IEnumerable<AbstractModel> childListenersAfterRemoval = childCombat.IterateHookListeners();
        if (ReferenceEquals(childListenersBeforeRemoval, childListenersAfterRemoval)
            || childListenersAfterRemoval.Contains(childCard.Preview)
            || childCard.GetPile(childState) is not null)
        {
            throw new InvalidOperationException(
                "卡牌移出战斗后没有精确失效 Hook listener 缓存或清理牌堆反向引用。");
        }

        PredictedCard clearProbe = new(liveCard);
        SimCardPile clearPile = new(PileType.Hand, [clearProbe]);
        clearPile.Clear();
        if (clearProbe.OwnerPile is not null)
            throw new InvalidOperationException("预测牌堆清空后没有清理卡牌反向引用。");
    }

    private static Action GetCardMutationObserver(PredictedCard card)
    {
        FieldInfo observerField = typeof(PredictedCard).GetField(
            "_mutationObserver",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PredictedCard).FullName, "_mutationObserver");
        return (Action?)observerField.GetValue(card)
            ?? throw new InvalidOperationException("预测卡牌没有安装变更 observer。");
    }

    private static void AssertAmountOnTurnStartCacheReuse(CombatState combat, Player player)
    {
        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parentSimulator = new(parentCombat);
        parentCombat.Apply<StrengthPower>(player.Creature, 2, player.Creature);
        _ = parentCombat.DrainPowerAmountChanges();
        StrengthPower parentPower = parentCombat.EffectivePowers()
            .OfType<StrengthPower>()
            .Single(power => ReferenceEquals(power.Owner, player.Creature));
        int parentAmountOnTurnStart = parentPower.AmountOnTurnStart;

        CombatPredictionSimulator childSimulator = parentSimulator.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)childSimulator.State.CombatState;
        StrengthPower childPower = childCombat.EffectivePowers()
            .OfType<StrengthPower>()
            .Single(power => ReferenceEquals(power.Owner, player.Creature));
        IReadOnlyList<PowerModel> listenersBefore = childCombat.EffectivePowers();
        childPower.AmountOnTurnStart = childPower.Amount + 1;
        childCombat.SnapshotPowerAmountsAtTurnStart([player.Creature]);
        if (!ReferenceEquals(listenersBefore, childCombat.EffectivePowers())
            || childPower.AmountOnTurnStart != childPower.Amount)
        {
            throw new InvalidOperationException(
                "AmountOnTurnStart 更新没有复用同一分支的 Power listener 缓存。");
        }
        if (parentPower.AmountOnTurnStart != parentAmountOnTurnStart)
            throw new InvalidOperationException("AmountOnTurnStart 更新跨 Fork 污染父 Power。");
    }

    private static void AssertPowerListenerCacheTransitionsAndForkIsolation(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parentSimulator = new(parentCombat);
        Creature owner = player.Creature;
        parentCombat.SetAmount<StrengthPower>(owner, 1);
        IReadOnlyList<AbstractModel> parentListenersAtOne =
            ((ICombatPredictionHookListenerSource)parentCombat).HookListeners;
        IReadOnlyList<AbstractModel> parentRunListenersAtOne =
            ((ICombatPredictionHookListenerSource)parentCombat).RunHookListeners;
        IReadOnlyList<PowerModel> parentPowersAtOne = parentCombat.EffectivePowers();
        StrengthPower parentStrength = parentPowersAtOne
            .OfType<StrengthPower>()
            .Single(power => ReferenceEquals(power.Owner, owner));
        bool canReuseListenerCache = !parentCombat.RootHasBaseLibCardModifiers;

        parentCombat.SetAmount<StrengthPower>(owner, 2);
        if (canReuseListenerCache
                && (!ReferenceEquals(
                    parentListenersAtOne,
                    ((ICombatPredictionHookListenerSource)parentCombat).HookListeners)
                    || !ReferenceEquals(
                        parentRunListenersAtOne,
                        ((ICombatPredictionHookListenerSource)parentCombat).RunHookListeners))
            || !ReferenceEquals(parentPowersAtOne, parentCombat.EffectivePowers())
            || !ReferenceEquals(parentStrength, parentCombat.GetPower<StrengthPower>(owner))
            || parentStrength.Amount != 2)
        {
            throw new InvalidOperationException(
                "Power 数量从 1 增加到 2 时错误重建了身份不变的 listener 缓存。");
        }

        CombatPredictionSimulator childSimulator = parentSimulator.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)childSimulator.State.CombatState;
        StrengthPower childStrength = childCombat.EffectivePowers()
            .OfType<StrengthPower>()
            .Single(power => ReferenceEquals(power.Owner, owner));
        IReadOnlyList<AbstractModel> childListenersAtTwo =
            ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
        IReadOnlyList<AbstractModel> childRunListenersAtTwo =
            ((ICombatPredictionHookListenerSource)childCombat).RunHookListeners;
        IReadOnlyList<PowerModel> childPowersAtTwo = childCombat.EffectivePowers();
        if (ReferenceEquals(parentStrength, childStrength)
            || CountReferences(childListenersAtTwo, childStrength) != 1
            || CountReferences(childRunListenersAtTwo, childStrength) != 1
            || CountReferences(childListenersAtTwo, parentStrength) != 0
            || CountReferences(childRunListenersAtTwo, parentStrength) != 0)
            throw new InvalidOperationException("Power listener 缓存没有按 Fork 映射到子分支 Power。");

        childCombat.SetAmount<StrengthPower>(owner, 0);
        IReadOnlyList<AbstractModel> childListenersAtZero =
            ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
        IReadOnlyList<AbstractModel> childRunListenersAtZero =
            ((ICombatPredictionHookListenerSource)childCombat).RunHookListeners;
        IReadOnlyList<PowerModel> childPowersAtZero = childCombat.EffectivePowers();
        if (ReferenceEquals(childListenersAtTwo, childListenersAtZero)
            || ReferenceEquals(childRunListenersAtTwo, childRunListenersAtZero)
            || ReferenceEquals(childPowersAtTwo, childPowersAtZero)
            || childListenersAtZero.Any(listener => ReferenceEquals(listener, childStrength))
            || childPowersAtZero.Any(power => ReferenceEquals(power, childStrength)))
        {
            throw new InvalidOperationException(
                "Power 数量从 2 归零时没有失效缓存并移除对应 listener。");
        }
        if (parentStrength.Amount != 2
            || canReuseListenerCache
                && (!ReferenceEquals(
                    parentListenersAtOne,
                    ((ICombatPredictionHookListenerSource)parentCombat).HookListeners)
                    || !ReferenceEquals(
                        parentRunListenersAtOne,
                        ((ICombatPredictionHookListenerSource)parentCombat).RunHookListeners))
            || !ReferenceEquals(parentPowersAtOne, parentCombat.EffectivePowers()))
        {
            throw new InvalidOperationException("子分支 Power 归零污染了父分支 listener 缓存。");
        }

        childCombat.SetAmount<StrengthPower>(owner, 1);
        IReadOnlyList<AbstractModel> childListenersRestored =
            ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
        IReadOnlyList<PowerModel> childPowersRestored = childCombat.EffectivePowers();
        if (ReferenceEquals(childListenersAtZero, childListenersRestored)
            || ReferenceEquals(childPowersAtZero, childPowersRestored)
            || CountReferences(childListenersRestored, childStrength) != 1
            || CountReferences(childPowersRestored, childStrength) != 1)
        {
            throw new InvalidOperationException(
                "Power 数量从 0 恢复到 1 时没有失效缓存或唯一恢复对应 listener。");
        }

        DexterityPower firstAdded = childCombat.AddPowerInstance<DexterityPower>(owner, 1, owner);
        NoDrawPower secondAdded = childCombat.AddPowerInstance<NoDrawPower>(owner, 1, owner);
        IReadOnlyList<AbstractModel> listenersWithAddedPowers =
            ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
        int firstIndex = IndexOfReference(listenersWithAddedPowers, firstAdded);
        int secondIndex = IndexOfReference(listenersWithAddedPowers, secondAdded);
        if (firstIndex < 0
            || secondIndex <= firstIndex
            || CountReferences(listenersWithAddedPowers, firstAdded) != 1
            || CountReferences(listenersWithAddedPowers, secondAdded) != 1)
        {
            throw new InvalidOperationException("新增 Power listener 没有保持唯一身份和施加顺序。");
        }
        AssertUniquePowerListenerReferences(listenersWithAddedPowers);
    }

    private static int IndexOfReference<T>(IReadOnlyList<T> items, T candidate) where T : class
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], candidate))
                return index;
        }
        return -1;
    }

    private static int CountReferences<T>(IReadOnlyList<T> items, T candidate) where T : class
    {
        int count = 0;
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], candidate))
                count++;
        }
        return count;
    }

    private static void AssertUniquePowerListenerReferences(IReadOnlyList<AbstractModel> listeners)
    {
        for (int left = 0; left < listeners.Count; left++)
        {
            if (listeners[left] is not PowerModel power)
                continue;
            for (int right = left + 1; right < listeners.Count; right++)
            {
                if (ReferenceEquals(power, listeners[right]))
                    throw new InvalidOperationException("Power listener 序列包含重复身份。");
            }
        }
    }

    private static void AssertSparsePowerAfflictionCardTracking(
        CombatState combat,
        Player player,
        CardModel liveCard)
    {
        SimulatedCombatState entryCombat = new(combat);
        CombatPredictionSimulator entrySimulator = new(entryCombat);
        SimPlayerCombatState entryState = entrySimulator.State.GetPlayerCombatState(player);
        PredictedCard entryCard = entryState.FindCard(liveCard)
            ?? throw new InvalidOperationException("Power affliction 首张生成牌测试找不到父卡牌。");
        PredictedCard firstGeneratedCard = entryCard.CreateClone();
        entryState.DiscardPile.Add(firstGeneratedCard);
        entryCombat.RegisterGeneratedCombatCard(firstGeneratedCard);
        entryCombat.NormalizePowerCardState(entrySimulator);
        HashSet<PredictedCard> entryKnown = GetPowerAfflictionKnownCards(entryCombat)
            ?? throw new InvalidOperationException("Power affliction 漏记第一张生成牌。");
        if (entryKnown.Count != 1 || !entryKnown.Contains(firstGeneratedCard))
            throw new InvalidOperationException("Power affliction 把第一张生成牌误认成根卡牌。");

        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parentSimulator = new(parentCombat);
        SimPlayerCombatState parentState = parentSimulator.State.GetPlayerCombatState(player);
        PredictedCard parentCard = parentState.FindCard(liveCard)
            ?? throw new InvalidOperationException("Power affliction 稀疏集合测试找不到父卡牌。");

        parentCombat.NormalizePowerCardState(parentSimulator);
        if (GetPowerAfflictionKnownCards(parentCombat) is not null)
        {
            throw new InvalidOperationException(
                "Power affliction 首次归一化不应记录战斗快照中的初始牌。");
        }

        PredictedCard generatedCard = parentCard.CreateClone();
        parentState.DiscardPile.Add(generatedCard);
        parentCombat.RegisterGeneratedCombatCard(generatedCard);
        parentCombat.NormalizePowerCardState(parentSimulator);
        HashSet<PredictedCard> parentKnown = GetPowerAfflictionKnownCards(parentCombat)
            ?? throw new InvalidOperationException("Power affliction 没有记录生成牌。");
        if (parentKnown.Count != 1 || !parentKnown.Contains(generatedCard))
            throw new InvalidOperationException("Power affliction 稀疏集合记录了非生成牌或漏掉生成牌。");

        parentCombat.NormalizePowerCardState(parentSimulator);
        if (parentKnown.Count != 1)
            throw new InvalidOperationException("Power affliction 重复归一化再次记录了同一生成牌。");

        CombatPredictionSimulator childSimulator = parentSimulator.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)childSimulator.State.CombatState;
        PredictedCard childGeneratedCard = childSimulator.State
            .GetPlayerCombatState(player)
            .FindCard(generatedCard.Original)
            ?? throw new InvalidOperationException("Power affliction Fork 后找不到生成牌。");
        HashSet<PredictedCard> childKnown = GetPowerAfflictionKnownCards(childCombat)
            ?? throw new InvalidOperationException("Power affliction Fork 后丢失生成牌集合。");
        if (childKnown.Count != 1
            || !childKnown.Contains(childGeneratedCard)
            || childKnown.Contains(generatedCard)
            || !parentKnown.Contains(generatedCard)
            || parentKnown.Contains(childGeneratedCard))
        {
            throw new InvalidOperationException("Power affliction 稀疏集合没有按 Fork 重映射或隔离。");
        }

        if (!parentState.DiscardPile.Remove(generatedCard))
            throw new InvalidOperationException("Power affliction 测试无法移除生成牌。");
        parentCombat.UnregisterGeneratedCombatCard(generatedCard);
        parentState.DiscardPile.Add(generatedCard);
        parentCombat.RegisterGeneratedCombatCard(generatedCard);
        parentCombat.NormalizePowerCardState(parentSimulator);
        if (parentKnown.Count != 1)
        {
            throw new InvalidOperationException(
                "Power affliction 把同一 wrapper 重新入场误判为新的生成牌。");
        }

        PredictedCard secondGeneratedCard = parentCard.CreateClone();
        parentState.DiscardPile.Add(secondGeneratedCard);
        parentCombat.RegisterGeneratedCombatCard(secondGeneratedCard);
        parentCombat.NormalizePowerCardState(parentSimulator);
        if (parentKnown.Count != 2 || !parentKnown.Contains(secondGeneratedCard))
            throw new InvalidOperationException("Power affliction 没有区分两个独立生成牌 wrapper。");
    }

    private static HashSet<PredictedCard>? GetPowerAfflictionKnownCards(
        SimulatedCombatState combat)
    {
        FieldInfo field = typeof(SimulatedCombatState).GetField(
            "_powerAfflictionKnownCards",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(
                typeof(SimulatedCombatState).FullName,
                "_powerAfflictionKnownCards");
        return (HashSet<PredictedCard>?)field.GetValue(combat);
    }

    private static void AssertProjectedShuffleEquivalence(
        CombatPredictionSimulator simulator,
        Player player)
    {
        List<PredictedCard> source = simulator.State
            .GetPlayerCombatState(player)
            .AllCards
            .ToList();
        if (source.Count == 0)
            throw new InvalidOperationException("投影洗牌等价测试要求至少一张牌。");
        source.AddRange(source.AsEnumerable().Reverse().ToArray());
        source.Add(source[0]);

        List<PredictedCard> baseline = [.. source];
        List<PredictedCard> optimized = [.. source];
        var baselineRng = simulator.Rng.Shuffle.Clone();
        var optimizedRng = simulator.Rng.Shuffle.Clone();
        int sourceCounter = simulator.Rng.Shuffle.Counter();

        baseline.StableShuffle(baselineRng);
        CombatBeamSolver.StableShuffleProjection(optimized, optimizedRng);
        if (!baseline.SequenceEqual(optimized)
            || baselineRng.Counter() != optimizedRng.Counter()
            || simulator.Rng.Shuffle.Counter() != sourceCounter)
        {
            throw new InvalidOperationException(
                "投影洗牌的卡牌顺序、RNG 消耗或原 RNG 隔离与 StableShuffle 不等价。");
        }
    }

    private static void AssertPredictionForkContextIdentityIndex()
    {
        using (PredictionForkContext small = new())
        {
            for (int index = 0; index < 32; index++)
            {
                ForkIdentityProbe source = new(index);
                ForkIdentityProbe fork = new(index);
                small.Register(source, fork);
                if (!ReferenceEquals(small.RequireRemap(source), fork))
                    throw new InvalidOperationException("PredictionForkContext 线性映射不正确。");
            }
        }

        const int count = 512;
        ForkIdentityProbe[] sources = new ForkIdentityProbe[count];
        ForkIdentityProbe[] forks = new ForkIdentityProbe[count];
        using PredictionForkContext indexed = new();
        for (int index = 0; index < count; index++)
        {
            // All probes deliberately compare equal through their virtual equality members.
            // Prediction forks must nevertheless be keyed strictly by object identity.
            sources[index] = new ForkIdentityProbe(1);
            forks[index] = new ForkIdentityProbe(1);
            indexed.Register(sources[index], forks[index]);
        }
        FieldInfo bucketsField = typeof(PredictionForkContext).GetField(
            "_buckets",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PredictionForkContext).FullName, "_buckets");
        if (bucketsField.GetValue(indexed) is not int[])
            throw new InvalidOperationException("PredictionForkContext 大映射没有启用身份哈希索引。");
        for (int index = 0; index < count; index++)
        {
            if (!indexed.TryRemap(sources[index], out ForkIdentityProbe? mapped)
                || !ReferenceEquals(mapped, forks[index])
                || !ReferenceEquals(indexed.RemapOrSelf(sources[index]), forks[index]))
            {
                throw new InvalidOperationException("PredictionForkContext 身份哈希扩容后映射不正确。");
            }
        }

        indexed.Register(sources[0], forks[0]);
        ForkIdentityProbe equalButUnknown = new(1);
        if (indexed.TryRemap(equalButUnknown, out ForkIdentityProbe? unexpected)
            || unexpected is not null
            || !ReferenceEquals(indexed.RemapOrSelf(equalButUnknown), equalButUnknown))
        {
            throw new InvalidOperationException("PredictionForkContext 错把值相等对象当成同一引用。");
        }
        try
        {
            indexed.Register(sources[0], new ForkIdentityProbe(1));
            throw new InvalidOperationException("PredictionForkContext 接受了同一源对象的不同 Fork。");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("was forked twice", StringComparison.Ordinal))
        {
        }
    }

    private sealed class ForkIdentityProbe(int equalityKey)
    {
        private int EqualityKey { get; } = equalityKey;

        public override bool Equals(object? obj)
            => obj is ForkIdentityProbe other && EqualityKey == other.EqualityKey;

        public override int GetHashCode()
            => EqualityKey;
    }

    private static void AssertForkableListEnumeration()
    {
        ForkableList<int> parent = new([1, 2, 3]);
        List<int>.Enumerator concreteEnumerator = parent.GetEnumerator();
        List<int> concreteValues = [];
        while (concreteEnumerator.MoveNext())
            concreteValues.Add(concreteEnumerator.Current);
        concreteEnumerator.Dispose();
        if (!concreteValues.SequenceEqual([1, 2, 3]))
            throw new InvalidOperationException("ForkableList 具体 enumerator 顺序不正确。");

        ForkableList<int> child = parent.Fork();
        child.Add(4);
        parent.Remove(1);
        if (!parent.SequenceEqual([2, 3])
            || !child.SequenceEqual([1, 2, 3, 4])
            || !((IEnumerable<int>)parent).SequenceEqual([2, 3])
            || !((IEnumerable)parent).Cast<int>().SequenceEqual([2, 3]))
        {
            throw new InvalidOperationException("ForkableList 枚举或 COW 父子隔离不正确。");
        }
    }

    private static void AssertRitsuCapabilityFastPath(
        CombatPredictionSimulator simulator,
        Player player,
        CardModel liveCard)
    {
        PredictedCard card = simulator.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("Ritsu capability 快通道测试找不到预测卡牌。");
        CardModel preview = card.MutablePreview;
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        if (!RitsuEmptyCapabilityFastPath.CanSkip(preview))
            throw new InvalidOperationException("无 capability 卡牌没有进入 Ritsu 空路径。");
        AssertRitsuDefaultCapabilityRegistrationInvalidatesCache(preview);

        CardType overrideType = preview.Type == CardType.Attack ? CardType.Curse : CardType.Attack;
        TestCardTypeCapability capability = new(overrideType);
        ModelCapabilitySet capabilities = ModelCapabilities.Get(preview);
        List<IModelCapability> attached = (List<IModelCapability>)(typeof(ModelCapabilitySet).GetField(
                "_capabilities",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(capabilities)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_capabilities"));
        FieldInfo attachedSnapshot = typeof(ModelCapabilitySet).GetField(
            "_attachedSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_attachedSnapshot");
        capability.Attach(preview, isInternal: true);
        attached.Add(capability);
        attachedSnapshot.SetValue(capabilities, null);
        try
        {
            if (RitsuEmptyCapabilityFastPath.CanSkip(preview) || preview.Type != overrideType)
                throw new InvalidOperationException("有 capability 卡牌没有保留 Ritsu 属性贡献逻辑。");
        }
        finally
        {
            attached.Remove(capability);
            capability.Detach(isInternal: true);
            attachedSnapshot.SetValue(capabilities, null);
        }
    }

    private static void AssertSimulationCardPileLookupFastPath(Player player, CardModel liveCard)
    {
        if (!SimulationCardPileLookupFastPath.CanUse())
            return;

        CardPile? expected = liveCard.Pile;
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CardPile? actual = liveCard.Pile;
        if (!ReferenceEquals(actual, expected)
            || !ReferenceEquals(actual, SimulationCardPileLookupFastPath.Find(liveCard)))
        {
            throw new InvalidOperationException("求解牌堆无分配快路径没有保持原版牌堆身份。");
        }

        CardModel transient = PredictionUtils.CloneCardStateForSimulation(liveCard);
        transient._owner = player;
        if (transient.Pile != null || SimulationCardPileLookupFastPath.Find(transient) != null)
            throw new InvalidOperationException("求解牌堆无分配快路径错误归属了临时卡牌。");
    }

    private static void AssertRootColorlessGenerationPoolCache(
        CombatPredictionSimulator simulator,
        Player player)
    {
        if (simulator.State.CombatState is not ICombatPredictionRunSnapshot runSnapshot
            || simulator.State.CombatState is not ICombatPredictionCardGenerationPoolSnapshot poolSnapshot)
        {
            throw new InvalidOperationException("生成牌根缓存测试缺少预测根状态接口。");
        }

        CardMultiplayerConstraint constraint = runSnapshot.CardMultiplayerConstraint;
        CardPoolModel canonicalPool = ModelDb.CardPool<ColorlessCardPool>();
        if (!poolSnapshot.TryGetRootEligibleCards(
                player,
                canonicalPool,
                constraint,
                out IReadOnlyList<CardModel>? rootEligible))
        {
            throw new InvalidOperationException("原生无色牌池没有建立根级生成候选缓存。");
        }

        CardModel[] uncachedEligible = player.GetUnlockedCards(canonicalPool, constraint)
            .FilterForCombatAndPlayerCount(constraint)
            .ToArray();
        if (rootEligible.Count != uncachedEligible.Length
            || rootEligible.Where((card, index) =>
                    !ReferenceEquals(card, uncachedEligible[index]))
                .Any())
        {
            throw new InvalidOperationException("根级无色生成候选与未缓存过滤结果的顺序不同。");
        }

        CombatPredictionSimulator fork = simulator.Fork();
        if (fork.State.CombatState is not ICombatPredictionCardGenerationPoolSnapshot forkPoolSnapshot
            || !forkPoolSnapshot.TryGetRootEligibleCards(
                player,
                canonicalPool,
                constraint,
                out IReadOnlyList<CardModel>? forkEligible)
            || !ReferenceEquals(rootEligible, forkEligible))
        {
            throw new InvalidOperationException("模拟 Fork 没有共享不可变的根级无色生成候选。");
        }

        CardPoolModel characterPool = player.Character.CardPool;
        if (!ReferenceEquals(characterPool, canonicalPool)
            && poolSnapshot.TryGetRootEligibleCards(
                player,
                characterPool,
                constraint,
                out _))
        {
            throw new InvalidOperationException("根级无色候选缓存错误命中了非原生/自定义牌池。");
        }

        PredictionRngState sourceRng = simulator.Rng.CombatCardGeneration.CaptureState();
        foreach (int count in new[] { 1, 3 })
        {
            var baselineRng = simulator.Rng.CombatCardGeneration.Clone();
            var cachedRng = simulator.Rng.CombatCardGeneration.Clone();
            PredictedCard[] baseline = player.GetUnlockedCards(canonicalPool, constraint)
                .GetDistinctForCombat(
                    player,
                    count,
                    baselineRng,
                    constraint)
                .ToArray();
            PredictedCard[] cached = simulator
                .GetDistinctUnlockedColorlessForCombat(
                    player,
                    count,
                    cachedRng,
                    constraint)
                .ToArray();
            PredictionRngState baselineState = baselineRng.CaptureState();
            PredictionRngState cachedState = cachedRng.CaptureState();
            if (baseline.Length != cached.Length
                || baseline.Where((card, index) =>
                        ReferenceEquals(card, cached[index])
                        || ReferenceEquals(card.Original, cached[index].Original)
                        || card.Preview.GetType() != cached[index].Preview.GetType()
                        || card.Preview.Id != cached[index].Preview.Id
                        || card.Preview.CurrentUpgradeLevel
                            != cached[index].Preview.CurrentUpgradeLevel
                        || !ReferenceEquals(card.Preview.Owner, player)
                        || !ReferenceEquals(cached[index].Preview.Owner, player)
                        || CombatBeamSolver.CaptureCardStateFingerprintForTesting(card)
                            != CombatBeamSolver.CaptureCardStateFingerprintForTesting(cached[index]))
                    .Any()
                || baselineState != cachedState)
            {
                throw new InvalidOperationException(
                    $"根级无色生成候选缓存改变了 count={count} 的抽取顺序、卡牌或 RNG 状态：" +
                    $"baseline_ids=[{string.Join(',', baseline.Select(card => card.Original.Id.Entry))}] " +
                    $"cached_ids=[{string.Join(',', cached.Select(card => card.Original.Id.Entry))}] " +
                    $"baseline_upgrades=[{string.Join(',', baseline.Select(card => card.Preview.CurrentUpgradeLevel))}] " +
                    $"cached_upgrades=[{string.Join(',', cached.Select(card => card.Preview.CurrentUpgradeLevel))}] " +
                    $"baseline_rng=(counter={baselineState.Counter},s0=0x{baselineState.State0:X16}," +
                    $"s1=0x{baselineState.State1:X16},s2=0x{baselineState.State2:X16}," +
                    $"s3=0x{baselineState.State3:X16}) " +
                    $"cached_rng=(counter={cachedState.Counter},s0=0x{cachedState.State0:X16}," +
                    $"s1=0x{cachedState.State1:X16},s2=0x{cachedState.State2:X16}," +
                    $"s3=0x{cachedState.State3:X16})。");
            }
        }

        if (simulator.Rng.CombatCardGeneration.CaptureState() != sourceRng)
            throw new InvalidOperationException("生成牌缓存 shadow 测试推进了原模拟器 RNG。");

        if (rootEligible.Count == 0)
            return;
        var firstRng = simulator.Rng.CombatCardGeneration.Clone();
        var secondRng = simulator.Rng.CombatCardGeneration.Clone();
        PredictedCard first = simulator
            .GetDistinctUnlockedColorlessForCombat(player, 1, firstRng, constraint)
            .Single();
        PredictedCard second = simulator
            .GetDistinctUnlockedColorlessForCombat(player, 1, secondRng, constraint)
            .Single();
        if (ReferenceEquals(first, second)
            || ReferenceEquals(first.Original, second.Original)
            || first.Preview.Id != second.Preview.Id
            || CombatBeamSolver.CaptureCardStateFingerprintForTesting(first)
                != CombatBeamSolver.CaptureCardStateFingerprintForTesting(second))
        {
            throw new InvalidOperationException("缓存没有为等价抽取创建隔离的分支级卡牌实例。");
        }

        CardModel canonicalSelected = rootEligible.Single(card => card.Id == first.Preview.Id);
        int canonicalReplayCount = canonicalSelected.BaseReplayCount;
        int secondReplayCount = second.Preview.BaseReplayCount;
        first.MutablePreview.BaseReplayCount++;
        if (second.Preview.BaseReplayCount != secondReplayCount
            || canonicalSelected.BaseReplayCount != canonicalReplayCount)
        {
            throw new InvalidOperationException("缓存生成牌的分支突变污染了兄弟分支或 canonical CardModel。");
        }
    }

    private static void AssertRitsuDefaultCapabilityRegistrationInvalidatesCache(
        AbstractModel cachedModel)
    {
        lock (RitsuDefaultCapabilityRegistrationTestLock)
        {
            if (_ritsuDefaultCapabilityRegistrationTestCompleted)
                return;

            int generationBefore =
                RitsuEmptyCapabilityFastPath.DefaultCapabilitySourceGenerationForTesting;
            Type cachedModelType = cachedModel.GetType();
            if (!RitsuEmptyCapabilityFastPath.HasCachedDefaultCapabilitySourceGenerationForTesting(
                    cachedModelType,
                    generationBefore))
            {
                throw new InvalidOperationException("Ritsu 默认 capability 注册测试缺少旧缓存条目。");
            }

            RegisterRitsuDefaultCapabilityCacheProbe();

            int generationAfter =
                RitsuEmptyCapabilityFastPath.DefaultCapabilitySourceGenerationForTesting;
            if (unchecked(generationAfter - generationBefore) != 1)
                throw new InvalidOperationException("Ritsu 默认 capability 注册没有推进缓存 generation。");
            if (RitsuEmptyCapabilityFastPath.HasCachedDefaultCapabilitySourceGenerationForTesting(
                    cachedModelType,
                    generationAfter))
            {
                throw new InvalidOperationException("Ritsu 默认 capability 注册后没有清理旧类型缓存。");
            }
            if (!RitsuEmptyCapabilityFastPath.CanSkip(cachedModel)
                || !RitsuEmptyCapabilityFastPath.HasCachedDefaultCapabilitySourceGenerationForTesting(
                    cachedModelType,
                    generationAfter))
            {
                throw new InvalidOperationException("Ritsu 默认 capability 注册后没有按新 generation 重建缓存。");
            }

            _ritsuDefaultCapabilityRegistrationTestCompleted = true;
        }
    }

    private static void RegisterRitsuDefaultCapabilityCacheProbe()
    {
        Type defaults = typeof(ModelCapabilities).Assembly.GetType(
            "STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults")
            ?? throw new TypeLoadException(
                "STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults");
        MethodInfo modify = defaults.GetMethod(
            "Modify",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(string),
                typeof(string),
                typeof(Type),
                typeof(Action<AbstractModel, ModelCapabilityList>),
                typeof(int),
            ],
            modifiers: null)
            ?? throw new MissingMethodException(defaults.FullName, "Modify");
        Action<AbstractModel, ModelCapabilityList> noOpModifier = static (_, _) => { };
        modify.Invoke(
            null,
            [
                Entry.ModId,
                "unattended_default_capability_cache_probe",
                typeof(RitsuDefaultCapabilityCacheProbeModel),
                noOpModifier,
                0,
            ]);
    }

    private static void AssertChoiceKeyCache(
        CombatPredictionSimulator simulator,
        Player player,
        CardModel liveCard)
    {
        PredictedCard card = simulator.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("选牌键缓存测试找不到预测卡牌。");
        string originalKey = CardChoiceSupport.ChoiceCardKey(card);
        CombatPredictionSimulator fork = simulator.Fork();
        PredictedCard forkedCard = fork.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("选牌键缓存测试找不到 Fork 卡牌。");
        if (!forkedCard.TryGetCachedChoiceKey(out string forkedCachedKey)
            || forkedCachedKey != originalKey)
        {
            throw new InvalidOperationException("选牌键缓存没有直接跨 Fork 复制。");
        }
        if (CardChoiceSupport.ChoiceCardKey(forkedCard) != originalKey)
            throw new InvalidOperationException("选牌键缓存没有跨 Fork 保留。");

        bool originalExhaust = forkedCard.Preview.ExhaustOnNextPlay;
        forkedCard.MutablePreview.ExhaustOnNextPlay = !originalExhaust;
        if (CardChoiceSupport.ChoiceCardKey(forkedCard) == originalKey)
            throw new InvalidOperationException("选牌键缓存没有在卡牌变更后失效。");
        if (CardChoiceSupport.ChoiceCardKey(card) != originalKey)
            throw new InvalidOperationException("选牌键缓存在 Fork 变更后泄漏到父状态。");
    }

    private sealed class TestCardTypeCapability(CardType type)
        : IModelCapability, ICardPropertyContributor
    {
        public string CapabilityId => "combat_solver_test_card_type";
        public AbstractModel? Owner { get; private set; }

        public void Attach(AbstractModel owner, bool isInternal = false)
            => Owner = owner;

        public void Detach(bool isInternal = false)
            => Owner = null;

        public CardType? GetCardType(CardModel card)
            => type;
    }

    private abstract class RitsuDefaultCapabilityCacheProbeModel : AbstractModel;

    private static void AssertForkRejected(
        CombatPredictionSimulator simulator,
        string expectedMessage)
    {
        try
        {
            simulator.Fork();
            throw new InvalidOperationException($"Fork 边界未拒绝：{expectedMessage}。");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
