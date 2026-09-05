using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Achievements;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCardPlayedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterCardPlayed and its late phase.
internal static class AfterCardPlayedMirrors
{
    private static readonly MirrorMethodSpec AfterCardPlayed = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardPlayed),
        [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    private static readonly MirrorMethodSpec AfterCardPlayedLate = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardPlayedLate),
        [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();
    private static readonly Registry LateRegistry = CreateLateRegistry();

    public static void Invoke(AbstractModel listener, AfterCardPlayedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    public static void InvokeLate(AbstractModel listener, AfterCardPlayedMirrorContext context)
    {
        LateRegistry.Invoke(listener, context);
    }

    public static void CompleteOrAbort(
        CombatPredictionSimulator simulator,
        CardPlay cardPlay,
        bool completed)
    {
        foreach ((AbstractModel model, PaelsLegionPredictionState state) in
                 simulator.StateStore.ReadEntries<PaelsLegionPredictionState>())
        {
            if (state.AffectedCardPlay != cardPlay)
                continue;
            state.AffectedCardPlay = null;
            if (!completed)
                continue;
            PaelsLegion relic = model as PaelsLegion
                ?? throw new InvalidOperationException(
                    $"佩尔军团预测状态绑定到了 {model.GetType().FullName}。");
            state.Cooldown = relic.DynamicVars["Turns"].IntValue;
            state.TriggeredBlockLastTurn = true;
        }
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCardPlayed);

        registry.RegisterIgnored<Play20CardsSingleTurnAchievement>();
        registry.RegisterIgnored<SkillSilent1Achievement>();

        registry.RegisterIgnored<CccComboModel>();

        registry.Register<BansheesCry>(HandleBansheesCry);
        registry.Register<Pinpoint>(HandlePinpoint);

        registry.Register<Glam>(HandleGlam);
        registry.Register<Goopy>(HandleGoopy);
        registry.Register<Vigorous>(HandleVigorous);

        registry.Register<AfterimagePower>(HandleAfterimagePower);
        registry.Register<BlackHolePower>(HandleBlackHolePower);
        registry.Register<CalamityPower>(HandleCalamityPower);
        registry.Register<CurlUpPower>(HandleCurlUpPower);
        registry.Register<DevourLifePower>(HandleDevourLifePower);
        registry.RegisterIgnored<EchoFormPower>();
        registry.Register<EnragePower>(HandleEnragePower);
        registry.Register<GalvanicPower>(HandleGalvanicPower);
        registry.Register<GravityPower>(HandleGravityPower);
        registry.Register<HauntPower>(HandleHauntPower);
        registry.Register<ImitationLearningPower>(HandleImitationLearningPower);
        registry.Register<MasterPlannerPower>(HandleMasterPlannerPower);
        registry.Register<MonologuePower>(HandleMonologuePower);
        registry.Register<OblivionPower>(HandleOblivionPower);
        registry.RegisterIgnored<PaleBlueDotPower>();
        registry.Register<PanachePower>(HandlePanachePower);
        registry.Register<RagePower>(HandleRagePower);
        registry.Register<RupturePower>(HandleRupturePower);
        registry.Register<SerpentFormPower>(HandleSerpentFormPower);
        registry.Register<SlowPower>(HandleSlowPower);
        registry.Register<SmoggyPower>(HandleSmoggyPower);
        registry.Register<SneakyPower>(HandleSneakyPower);
        registry.Register<StormPower>(HandleStormPower);
        registry.Register<StranglePower>(HandleStranglePower);
        registry.Register<SubroutinePower>(HandleSubroutinePower);
        registry.Register<TenderPower>(HandleTenderPower);
        registry.Register<VitalSparkPower>(HandleVitalSparkPower);
        registry.Register<VoidFormPower>(HandleVoidFormPower);
        registry.Register<WitheringPresencePower>(HandleWitheringPresencePower);

        registry.RegisterIgnored<ArtOfWar>();
        registry.Register<BrilliantScarf>(HandleBrilliantScarf);
        registry.Register<DaughterOfTheWind>(HandleDaughterOfTheWind);
        registry.Register<GamePiece>(HandleGamePiece);
        registry.Register<HelicalDart>(HandleHelicalDart);
        registry.Register<IronClub>(HandleIronClub);
        registry.Register<IvoryTile>(HandleIvoryTile);
        registry.Register<Kunai>(HandleKunai);
        registry.Register<Kusarigama>(HandleKusarigama);
        registry.Register<LetterOpener>(HandleLetterOpener);
        registry.Register<LostWisp>(HandleLostWisp);
        registry.Register<MummifiedHand>(HandleMummifiedHand);
        registry.Register<MusicBox>(HandleMusicBox);
        registry.Register<Nunchaku>(HandleNunchaku);
        registry.Register<OrnamentalFan>(HandleOrnamentalFan);
        registry.Register<PaelsLegion>(HandlePaelsLegion);
        registry.Register<PenNib>(HandlePenNib);
        registry.Register<Permafrost>(HandlePermafrost);
        registry.RegisterIgnored<Pocketwatch>();
        registry.Register<RainbowRing>(HandleRainbowRing);
        registry.Register<RazorTooth>(HandleRazorTooth);
        registry.RegisterIgnored<RippleBasin>();
        registry.Register<Shuriken>(HandleShuriken);
        registry.Register<TuningFork>(HandleTuningFork);
        registry.Register<UnsettlingLamp>(HandleUnsettlingLamp);
        registry.Register<Vambrace>(HandleVambrace);
        registry.Register<VelvetChoker>(HandleVelvetChoker);

        return registry;
    }

    private static Registry CreateLateRegistry()
    {
        var registry = new Registry(AfterCardPlayedLate);

        registry.Register<MakeItSo>(HandleMakeItSo);
        registry.Register<RightHandHand>(HandleRightHandHand);

        return registry;
    }

    private static void HandleDaughterOfTheWind(DaughterOfTheWind relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner && context.PreviewCard.Type == CardType.Attack)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：格挡+{relic.DynamicVars.Block.IntValue}");
        }
    }

    private static void HandleBrilliantScarf(BrilliantScarf relic, AfterCardPlayedMirrorContext context)
    {
        if (!context.CardPlay.IsAutoPlay && context.PreviewCard.Owner == relic.Owner)
        {
            var state = context.StateStore.Get(relic, () => new CounterPredictionState(relic._cardsPlayedThisTurn));
            state.Value++;
        }
    }

    private static void HandleGamePiece(GamePiece relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner && context.PreviewCard.Type == CardType.Power)
        {
            context.Simulator.Draw(relic.Owner, relic.DynamicVars.Cards.BaseValue);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：抽{relic.DynamicVars.Cards.IntValue}");
        }
    }

    private static void HandleHelicalDart(HelicalDart relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner && context.PreviewCard.Tags.Contains(CardTag.Shiv))
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("螺旋飞镖效果缺少可写的预测状态。");
            effects.ApplyTemporaryDexterity(
                typeof(HelicalDartPower),
                relic.Owner.Creature,
                relic.DynamicVars.Dexterity.IntValue,
                relic.Owner.Creature);
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：敏捷+{relic.DynamicVars.Dexterity.IntValue}");
        }
    }

    private static void HandleIronClub(IronClub relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new CounterPredictionState(relic.CardsPlayed));
        if (++state.Value % relic.DynamicVars.Cards.IntValue == 0)
        {
            context.Simulator.Draw(relic.Owner, 1);
            if (context.Simulator.HasPendingChoice)
                return;
            context.Simulator.RecordRelicTrigger(relic, "：抽1");
        }
    }

    private static void HandleIvoryTile(IvoryTile relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner &&
            context.CardPlay.Resources.EnergyValue >= relic.DynamicVars[IvoryTile._energyThresholdKey].IntValue)
        {
            context.Simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：能量+{relic.DynamicVars.Energy.IntValue}");
        }
    }

    private static void HandleKusarigama(Kusarigama relic, AfterCardPlayedMirrorContext context)
    {
        if (!IncrementCounter(relic, relic._attacksPlayedThisTurn, CardType.Attack, context))
        {
            return;
        }

        var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
        if (target is not null)
        {
            context.Simulator.Damage(target, relic.DynamicVars.Damage, relic.Owner.Creature);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：伤害{relic.DynamicVars.Damage.IntValue}");
        }
    }

    private static void HandleLetterOpener(LetterOpener relic, AfterCardPlayedMirrorContext context)
    {
        if (IncrementCounter(relic, relic._skillsPlayedThisTurn, CardType.Skill, context))
        {
            context.Simulator.Damage(context.State.HittableEnemies, relic.DynamicVars.Damage, relic.Owner.Creature);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：全体伤害{relic.DynamicVars.Damage.IntValue}");
        }
    }

    private static void HandleKunai(Kunai relic, AfterCardPlayedMirrorContext context)
    {
        if (context.Simulator.IsInProgress
            && IncrementCounter(relic, relic._attacksPlayedThisTurn, CardType.Attack, context)
            && ApplyRelicStatPower(relic, typeof(DexterityPower), relic.DynamicVars.Dexterity.IntValue, context))
        {
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：敏捷+{relic.DynamicVars.Dexterity.IntValue}");
        }
    }

    private static void HandleLostWisp(LostWisp relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner && context.PreviewCard.Type == CardType.Power)
        {
            context.Simulator.Damage(context.State.HittableEnemies, relic.DynamicVars.Damage, relic.Owner.Creature);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：全体伤害{relic.DynamicVars.Damage.IntValue}");
        }
    }

    private static void HandleMummifiedHand(MummifiedHand relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || context.PreviewCard.Type != CardType.Power)
        {
            return;
        }

        var playerState = context.State.GetPlayerCombatState(relic.Owner);
        var handCards = playerState.Hand.Cards;
        var naturallyCostly = handCards
            .Where(card => card.Preview.EnergyCost.GetWithModifiers(CostModifiers.None) > 0
                || card.Preview.BaseStarCost > 0)
            .ToList();
        bool CostsResources(PredictedCard card) =>
            card.GetEnergyCostWithModifiers(context.Simulator, playerState) > 0 ||
            card.GetStarCostWithModifiers(context.Simulator, playerState) > 0;

        var rng = context.Rng.CombatCardSelection;
        var selectedCard = rng.NextItem(naturallyCostly.Where(CostsResources))
            ?? rng.NextItem(handCards.Where(CostsResources))
            ?? rng.NextItem(naturallyCostly)
            ?? rng.NextItem(handCards);
        if (selectedCard is not null)
        {
            selectedCard.SetToFreeThisTurn();
            context.Simulator.History.CardsSelected([selectedCard]);
            context.Simulator.RecordRelicTrigger(relic, "：手牌0费");
        }
    }

    private static void HandleMusicBox(MusicBox relic, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new MusicBoxPredictionState(relic));
        if (state.CardBeingPlayed != context.Card.Original)
        {
            return;
        }

        var clone = context.Card.CreateClone();
        clone.MutablePreview.LocalKeywords.Add(CardKeyword.Ethereal);
        context.Simulator.AddGeneratedCardToCombat(
            clone,
            PileType.Hand,
            relic.Owner,
            resultKind: CardGenerationResultKind.Contextual);
        if (context.Simulator.HasPendingChoice)
            return;
        state.WasUsedThisTurn = true;
        state.CardBeingPlayed = null;
        context.Simulator.RecordRelicTrigger(relic, "：复制到手牌");
    }

    private static void HandleNunchaku(Nunchaku relic, AfterCardPlayedMirrorContext context)
    {
        if (IncrementCounter(relic, relic.AttacksPlayed, CardType.Attack, context))
        {
            context.Simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：能量+{relic.DynamicVars.Energy.IntValue}");
        }
    }

    private static void HandleOrnamentalFan(OrnamentalFan relic, AfterCardPlayedMirrorContext context)
    {
        if (IncrementCounter(relic, relic._attacksPlayedThisTurn, CardType.Attack, context))
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：格挡+{relic.DynamicVars.Block.IntValue}");
        }
    }

    private static void HandlePermafrost(Permafrost relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || context.PreviewCard.Type != CardType.Power)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new FlagPredictionState(relic._activatedThisCombat));
        if (!state.Value)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
            if (context.Simulator.HasPendingChoice)
                return;
            state.Value = true;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：格挡+{relic.DynamicVars.Block.IntValue}");
        }
    }

    private static void HandlePaelsLegion(PaelsLegion relic, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new PaelsLegionPredictionState(relic));
        if (state.AffectedCardPlay == context.CardPlay)
        {
            state.AffectedCardPlay = null;
            state.Cooldown = relic.DynamicVars["Turns"].IntValue;
            state.TriggeredBlockLastTurn = true;
        }
    }

    private static void HandlePenNib(PenNib relic, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new PenNibPredictionState(relic));
        if (state.AttackToDouble == context.Card.Original)
        {
            state.AttackToDouble = null;
        }
    }

    private static void HandleRazorTooth(RazorTooth relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner &&
            context.PreviewCard.Type is CardType.Attack or CardType.Skill &&
            context.PreviewCard.IsUpgradable)
        {
            context.Simulator.Upgrade(context.Card);
            context.Simulator.RecordRelicTrigger(relic, "：升级");
        }
    }

    private static void HandleRainbowRing(RainbowRing relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || !context.Simulator.IsInProgress)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new RainbowRingPredictionState(relic));
        if (state.ActivationCountThisTurn >= 1)
        {
            return;
        }

        state.AttacksPlayedThisTurn += context.PreviewCard.Type == CardType.Attack ? 1 : 0;
        state.SkillsPlayedThisTurn += context.PreviewCard.Type == CardType.Skill ? 1 : 0;
        state.PowersPlayedThisTurn += context.PreviewCard.Type == CardType.Power ? 1 : 0;
        if (state.AttacksPlayedThisTurn > 0 && state.SkillsPlayedThisTurn > 0 && state.PowersPlayedThisTurn > 0)
        {
            bool strengthApplied = ApplyRelicStatPower(
                relic, typeof(StrengthPower), relic.DynamicVars.Strength.IntValue, context);
            bool dexterityApplied = ApplyRelicStatPower(
                relic, typeof(DexterityPower), relic.DynamicVars.Dexterity.IntValue, context);
            state.ActivationCountThisTurn++;
            if ((strengthApplied || dexterityApplied) && context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic,
                    $"：力量+{relic.DynamicVars.Strength.IntValue} 敏捷+{relic.DynamicVars.Dexterity.IntValue}");
        }
    }

    private static void HandleTuningFork(TuningFork relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || context.PreviewCard.Type != CardType.Skill)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new CounterPredictionState(relic.SkillsPlayed));
        if (++state.Value >= relic.DynamicVars.Cards.IntValue)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
            if (context.Simulator.HasPendingChoice)
                return;
            state.Value -= relic.DynamicVars.Cards.IntValue;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：格挡+{relic.DynamicVars.Block.IntValue}");
        }
    }

    private static void HandleVambrace(Vambrace relic, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new VambracePredictionState(relic));
        if (context.PreviewCard.Owner != relic.Owner
            || context.Card.Original != state.TriggeringCard
            || state.BlockGainedThisCombat)
        {
            return;
        }
        state.BlockGainedThisCombat = true;
    }

    private static void HandleUnsettlingLamp(UnsettlingLamp relic, AfterCardPlayedMirrorContext context)
    {
        // Depends on Power hooks; mirror not available for now.
    }

    private static void HandleVelvetChoker(VelvetChoker relic, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner)
        {
            var state = context.StateStore.Get(relic, () => new CounterPredictionState(relic._cardsPlayedThisTurn));
            state.Value++;
        }
    }

    private static void HandleShuriken(Shuriken relic, AfterCardPlayedMirrorContext context)
    {
        if (context.Simulator.IsInProgress
            && IncrementCounter(relic, relic._attacksPlayedThisTurn, CardType.Attack, context)
            && ApplyRelicStatPower(relic, typeof(StrengthPower), relic.DynamicVars.Strength.IntValue, context))
        {
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：力量+{relic.DynamicVars.Strength.IntValue}");
        }
    }

    private static bool ApplyRelicStatPower(RelicModel relic, Type powerType, int amount,
        AfterCardPlayedMirrorContext context)
    {
        // Vanilla runs the listener/counter while the final attack is still in progress,
        // but PowerCmd.Apply rejects applications as soon as combat IsEnding. Evaluate at
        // this listener, not in the deferred card-lifecycle tail after other listeners.
        if (context.Simulator.IsEnding)
            return false;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("遗物属性效果缺少可写的预测状态。");
        effects.ApplyPower(powerType, relic.Owner.Creature, amount, relic.Owner.Creature);
        return true;
    }

    private static void HandleAfterimagePower(AfterimagePower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is > 0 and var amount)
        {
            context.Simulator.GainBlock(power.Owner, amount, ValueProp.Unpowered);
        }
    }

    private static void HandleBlackHolePower(BlackHolePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.CardPlay.Resources.StarsSpent > 0 &&
            context.PreviewCard.Owner == power.Owner.Player &&
            context.CardPlay.IsLastInSeries)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleCalamityPower(CalamityPower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is null || power.Owner.Player is not { } player)
        {
            return;
        }

        var cards = player.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(card => card.Type == CardType.Attack)
            .GetForCombat(
                player,
                power.Amount,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();
        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, player);
    }

    private static void HandleCurlUpPower(CurlUpPower power, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get<CurlUpPredictionState>(power);
        if (state.Consumed || state.PlayedCard != context.Card.Original)
        {
            return;
        }

        state.PlayedCard = null;
        context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        if (context.Simulator.HasPendingChoice)
            return;
        state.Consumed = true;
        context.StateStore.GetPowerAmount(power).Consume();
    }

    private static void HandleDevourLifePower(DevourLifePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard is Soul && context.PreviewCard.Owner.Creature == power.Owner)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("噬命效果缺少可写的预测状态。");
            effects.SummonOsty(context.Simulator, context.PreviewCard.Owner, power.Amount);
        }
    }

    private static void HandleEnragePower(EnragePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Type != CardType.Skill)
            return;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("激怒效果缺少可写的预测状态。");
        effects.ApplyPower(typeof(StrengthPower), power.Owner, power.Amount, power.Owner);
    }

    private static void HandleGalvanicPower(GalvanicPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Affliction is Galvanized)
        {
            context.Simulator.Damage(
                context.PreviewCard.Owner.Creature,
                power.Amount,
                DamageProps.cardUnpowered,
                dealer: null);
        }
    }

    private static void HandleGravityPower(GravityPower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is > 0 and var amount)
        {
            context.Simulator.Damage(context.State.HittableEnemies, amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleHauntPower(HauntPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard is Soul && context.PreviewCard.Owner.Creature == power.Owner)
        {
            var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            if (target is not null)
            {
                context.Simulator.Damage(target, power.Amount, DamageProps.nonCardHpLoss, dealer: null);
            }
        }
    }

    private static void HandleMasterPlannerPower(MasterPlannerPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player && context.PreviewCard.Type == CardType.Skill)
        {
            context.MutablePreviewCard.LocalKeywords.Add(CardKeyword.Sly);
        }
    }

    private static void HandleImitationLearningPower(
        ImitationLearningPower power,
        AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(power, () => new ImitationLearningPredictionState(power));
        if (state.Amount <= 0)
        {
            return;
        }

        var index = state.CardAndClones.FindIndex(pair => pair.Card == context.Card);
        if (index < 0)
        {
            return;
        }

        var clone = state.CardAndClones[index].Clone;
        state.CardAndClones.RemoveAt(index);

        state.Amount--;
        context.Simulator.AutoPlay(
            clone,
            nestedChoiceSourceId: power.Id.Entry);
    }

    private static void HandleMonologuePower(MonologuePower power, AfterCardPlayedMirrorContext context)
    {
        RecordRiskIfPaired(power, context);
    }

    private static void HandleOblivionPower(OblivionPower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is not int amount || amount <= 0)
            return;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("湮灭效果缺少可写的预测状态。");
        effects.ApplyPower(typeof(DoomPower), power.Owner, amount, power.Applier);
    }

    private static void HandlePanachePower(PanachePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != power.Owner.Player)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new PanachePredictionState(power));
        if (state.AlreadyApplied && --state.CardsLeft <= 0)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
            if (context.Simulator.HasPendingChoice)
                return;
            state.CardsLeft = PanachePower._baseCardsLeft;
        }
        state.AlreadyApplied = true;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("神气制胜效果缺少可写的预测状态。");
        effects.SetPowerDynamicVar(context.Simulator, power, "CardsLeft", state.CardsLeft);
    }

    private static void HandleRagePower(RagePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player && context.PreviewCard.Type == CardType.Attack)
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleSerpentFormPower(SerpentFormPower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is > 0 and var amount)
        {
            var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            if (target is not null)
            {
                context.Simulator.Damage(target, amount, ValueProp.Unpowered, power.Owner);
            }
        }
    }

    private static void HandleRupturePower(RupturePower power, AfterCardPlayedMirrorContext context)
    {
        RupturePredictionState state = context.StateStore.Get(
            power,
            static () => new RupturePredictionState());
        if (!state.StrengthByCard.Remove(context.Card.Original, out int amount) || amount == 0)
            return;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("撕裂效果缺少可写的预测状态。");
        effects.ApplyPower(typeof(StrengthPower), power.Owner, amount, power.Owner);
    }

    private static void HandleSmoggyPower(SmoggyPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature != power.Owner || context.PreviewCard.Type != CardType.Skill)
        {
            return;
        }

        foreach (var card in context.State.GetPlayerCombatState(context.PreviewCard.Owner).AllCards.ToList())
        {
            if (card.Preview.Type == CardType.Skill && card.Preview.Affliction is null)
            {
                context.Simulator.Afflict<Smog>(card, 1);
            }
        }
    }

    private static void HandleSlowPower(SlowPower power, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(
            power,
            () => new CounterPredictionState(power.DynamicVars[SlowPower._slowAmountKey].IntValue));
        state.Value++;
    }

    private static void HandleSneakyPower(SneakyPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature != power.Owner && context.PreviewCard.Type == CardType.Attack)
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleStormPower(StormPower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is > 0 and var amount && power.Owner.Player is { } player)
        {
            context.Simulator.OrbChannel<LightningOrb>(player, amount);
        }
    }

    private static void HandleStranglePower(StranglePower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is { } amount)
        {
            context.Simulator.Damage(power.Owner, amount, DamageProps.nonCardHpLoss, dealer: null);
        }
    }

    private static void HandleSubroutinePower(SubroutinePower power, AfterCardPlayedMirrorContext context)
    {
        if (TakePairAmount(power, context) is > 0 and var amount && power.Owner.Player is { } player)
        {
            context.Simulator.GainEnergy(player, amount);
        }
    }

    private static void HandleVoidFormPower(VoidFormPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner &&
            context.CardPlay is { IsAutoPlay: false, IsLastInSeries: true })
        {
            context.StateStore.Get(power, static value => new VoidFormPredictionState(value)).CardsPlayedThisTurn++;
        }
    }

    private static void HandleTenderPower(TenderPower power, AfterCardPlayedMirrorContext context)
    {
        // The card-play completion sink applies the paired Strength/Dexterity loss from this history entry.
    }

    private static void HandleVitalSparkPower(VitalSparkPower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Affliction is not Tainted)
            return;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("生命火花效果缺少可写的预测状态。");
        effects.ApplyPower(
            typeof(TaintedPower),
            context.PreviewCard.Owner.Creature,
            power.Amount,
            applier: null);
    }

    private static void HandleWitheringPresencePower(WitheringPresencePower power, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != power.Target?.Player)
        {
            return;
        }

        var state = context.StateStore.Get(power,
            () => new CounterPredictionState(power.DynamicVars[WitheringPresencePower._cardsLeftKey].IntValue));
        if (--state.Value <= 0)
        {
            context.Simulator.CreateAndAddGeneratedCardsToCombat<Wither>(
                context.PreviewCard.Owner,
                PileType.Hand,
                1,
                creator: null);
            if (context.Simulator.HasPendingChoice)
                return;
            state.Value = WitheringPresencePower._baseCardsLeft;
        }
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("凋零气场效果缺少可写的预测状态。");
        effects.SetPowerDynamicVar(
            context.Simulator,
            power,
            WitheringPresencePower._cardsLeftKey,
            state.Value);
    }

    private static void HandleBansheesCry(BansheesCry card, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == card.Owner &&
            context.Card.HasKeyword(context.State, CardKeyword.Ethereal) &&
            context.State.FindCard(card) is { } predictedCard)
        {
            predictedCard.MutablePreview.EnergyCost.AddThisCombat(-card.DynamicVars.Energy.IntValue);
        }
    }

    private static void HandlePinpoint(Pinpoint card, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == card.Owner &&
            context.PreviewCard.Type == CardType.Skill &&
            context.State.FindCard(card) is { } predictedCard)
        {
            predictedCard.MutablePreview.EnergyCost.AddThisTurn(-1);
        }
    }

    private static void HandleGoopy(Goopy enchantment, AfterCardPlayedMirrorContext context)
    {
        if (context.Card.References(enchantment.Card) && context.MutablePreviewCard.Enchantment is Goopy preview)
        {
            preview._amount++;
            if (context.MutablePreviewCard.DeckVersion != null
                && context.State.CombatState is SimulatedCombatState combat)
            {
                combat.RecordLongTermResource(1);
            }
        }
    }

    private static void HandleGlam(Glam enchantment, AfterCardPlayedMirrorContext context)
    {
        if (context.Card.References(enchantment.Card) && context.MutablePreviewCard.Enchantment is Glam preview)
        {
            preview._usedThisCombat = true;
            preview._status = EnchantmentStatus.Disabled;
        }
    }

    private static void HandleVigorous(Vigorous enchantment, AfterCardPlayedMirrorContext context)
    {
        if (context.Card.References(enchantment.Card) && context.MutablePreviewCard.Enchantment is Vigorous preview)
        {
            preview._status = EnchantmentStatus.Disabled;
        }
    }

    private static void HandleMakeItSo(MakeItSo card, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != card.Owner ||
            context.PreviewCard.Type != CardType.Skill ||
            context.State.FindCard(card) is not { } predictedCard ||
            predictedCard.GetPile(context.State)?.Type == PileType.Hand)
        {
            return;
        }

        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Make It So requires simulated combat state.");
        // Vanilla records CardPlayFinished before dispatching AfterCardPlayedLate.
        // The simulated lifecycle counter is committed after this hook, so include
        // the skill that is currently finishing.
        int count = combat.GetSkillCardsPlayedThisTurn(card.Owner.Creature) + 1;
        if (count % card.DynamicVars.Cards.IntValue == 0)
        {
            context.Simulator.AddToPile(predictedCard, PileType.Hand);
        }
    }

    private static void HandleRightHandHand(RightHandHand card, AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == card.Owner &&
            context.CardPlay.Resources.EnergyValue >= card.DynamicVars.Energy.IntValue &&
            context.State.FindCard(card) is { } predictedCard &&
            predictedCard.GetPile(context.State)?.Type == PileType.Discard)
        {
            context.Simulator.AddToPile(predictedCard, PileType.Hand);
        }
    }

    private static bool IncrementCounter(
        RelicModel relic,
        int initialValue,
        CardType cardType,
        AfterCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || context.PreviewCard.Type != cardType)
        {
            return false;
        }

        var state = context.StateStore.Get(relic, () => new CounterPredictionState(initialValue));
        return ++state.Value % relic.DynamicVars.Cards.IntValue == 0;
    }

    private static int? TakePairAmount(AbstractModel model, AfterCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get<CardPlayPairPredictionState>(model);
        return state.Amounts.Remove(context.CardPlay, out var amount) ? amount : null;
    }

    private static void RecordRiskIfPaired(AbstractModel model, AfterCardPlayedMirrorContext context)
    {
        _ = TakePairAmount(model, context);
    }
}

internal sealed class AfterCardPlayedMirrorContext : CombatCardMirrorContext
{
    public required CardPlay CardPlay { get; init; }
}

internal sealed class FlagPredictionState(bool value) : IPredictionStateForkable
{
    public bool Value { get; set; } = value;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class PanachePredictionState(PanachePower power) : IPredictionStateForkable
{
    public bool AlreadyApplied { get; set; } = power.GetInternalData<PanachePower.Data>().alreadyApplied;

    public int CardsLeft { get; set; } = power.DynamicVars["CardsLeft"].IntValue;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class RainbowRingPredictionState(RainbowRing relic) : IPredictionStateForkable
{
    public int AttacksPlayedThisTurn { get; set; } = relic._attacksPlayedThisTurn;

    public int SkillsPlayedThisTurn { get; set; } = relic._skillsPlayedThisTurn;

    public int PowersPlayedThisTurn { get; set; } = relic._powersPlayedThisTurn;

    public int ActivationCountThisTurn { get; set; } = relic._activationCountThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class CurlUpPredictionState : IPredictionStateForkable, IPredictionForkBoundary
{
    public CardModel? PlayedCard { get; set; }

    public bool Consumed { get; set; }

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        return MemberwiseClone();
    }

    public void AssertForkable()
    {
        if (PlayedCard is not null)
            throw new InvalidOperationException("Cannot fork Curl Up during card-play resolution.");
    }
}
