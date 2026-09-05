using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Achievements;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, BeforeCardPlayedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.BeforeCardPlayed.
internal static class BeforeCardPlayedMirrors
{
    private static readonly MirrorMethodSpec BeforeCardPlayed = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeCardPlayed),
        [typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, BeforeCardPlayedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    public static void CompleteOrAbort(CombatPredictionSimulator simulator, CardPlay cardPlay)
    {
        foreach ((_, CardPlayPairPredictionState state) in
                 simulator.StateStore.ReadEntries<CardPlayPairPredictionState>())
        {
            state.Amounts.Remove(cardPlay);
        }
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(BeforeCardPlayed);

        registry.RegisterIgnored<SkillSilent1Achievement>();

        registry.Register<Stomp>(HandleStomp);

        registry.Register<AfterimagePower>(HandleAfterimagePower);
        registry.Register<CalamityPower>(HandleCalamityPower);
        registry.Register<ChainsOfBindingPower>(HandleChainsOfBindingPower);
        registry.Register<DanseMacabrePower>(HandleDanseMacabrePower);
        registry.Register<FreeAttackPower>(HandleFreeAttackPower);
        registry.Register<FreePowerPower>(HandleFreePowerPower);
        registry.Register<FreeSkillPower>(HandleFreeSkillPower);
        registry.Register<GravityPower>(HandleGravityPower);
        registry.Register<ImitationLearningPower>(HandleImitationLearningPower);
        registry.Register<JugglingPower>(HandleJugglingPower);
        registry.Register<MonologuePower>(HandleMonologuePower);
        registry.Register<OblivionPower>(HandleOblivionPower);
        registry.Register<RupturePower>(HandleRupturePower);
        registry.Register<SerpentFormPower>(HandleSerpentFormPower);
        registry.Register<SlothPower>(HandleSlothPower);
        registry.Register<SpiritOfAshPower>(HandleSpiritOfAshPower);
        registry.Register<StormPower>(HandleStormPower);
        registry.Register<StranglePower>(HandleStranglePower);
        registry.Register<SubroutinePower>(HandleSubroutinePower);
        registry.Register<SurroundedPower>(HandleSurroundedPower);
        registry.Register<TheSealedThronePower>(HandleTheSealedThronePower);
        registry.Register<VeilpiercerPower>(HandleVeilpiercerPower);

        registry.RegisterIgnored<ChemicalX>();
        registry.Register<IntimidatingHelmet>(HandleIntimidatingHelmet);
        registry.Register<MusicBox>(HandleMusicBox);
        registry.RegisterIgnored<PaelsEye>();
        registry.Register<PenNib>(HandlePenNib);

        return registry;
    }

    private static void HandleStomp(Stomp stomp, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == stomp.Owner &&
            context.PreviewCard.Type == CardType.Attack &&
            context.State.FindCard(stomp) is { } predictedStomp)
        {
            predictedStomp.MutablePreview.EnergyCost.AddThisTurn(-1);
        }
    }

    private static void HandleAfterimagePower(AfterimagePower power, BeforeCardPlayedMirrorContext context)
    {
        SnapshotOwnerCard(power, context, power.Amount);
    }

    private static void HandleCalamityPower(CalamityPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Type == CardType.Attack)
        {
            SnapshotOwnerCard(power, context, power.Amount);
        }
    }

    private static void HandleDanseMacabrePower(DanseMacabrePower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner &&
            context.CardPlay.Resources.EnergyValue >= power.DynamicVars.Energy.IntValue)
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleChainsOfBindingPower(
        ChainsOfBindingPower power,
        BeforeCardPlayedMirrorContext context)
    {
        if (!context.PreviewCard.IsDupe &&
            context.PreviewCard.Owner.Creature == power.Owner &&
            context.PreviewCard.Affliction is Bound)
        {
            var state = context.StateStore.Get(power, () => new ChainsOfBindingPredictionState(power));
            state.BoundCardPlayed = true;
        }
    }

    private static void HandleFreeAttackPower(FreeAttackPower power, BeforeCardPlayedMirrorContext context)
    {
        ConsumeFreeCardPower(power, CardType.Attack, context);
    }

    private static void HandleFreePowerPower(FreePowerPower power, BeforeCardPlayedMirrorContext context)
    {
        ConsumeFreeCardPower(power, CardType.Power, context);
    }

    private static void HandleFreeSkillPower(FreeSkillPower power, BeforeCardPlayedMirrorContext context)
    {
        ConsumeFreeCardPower(power, CardType.Skill, context);
    }

    private static void HandleGravityPower(GravityPower power, BeforeCardPlayedMirrorContext context)
    {
        SnapshotOwnerCard(power, context, power.Amount);
    }

    private static void HandleJugglingPower(JugglingPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != power.Owner.Player || context.PreviewCard.Type != CardType.Attack)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new JugglingPredictionState(power));
        state.AttacksPlayedThisTurn++;
        if (state.AttacksPlayedThisTurn != 3)
        {
            return;
        }

        for (var i = 0; i < power.Amount; i++)
        {
            context.Simulator.AddGeneratedCardToCombat(
                context.Card.CreateClone(),
                PileType.Hand,
                power.Owner.Player,
                resultKind: CardGenerationResultKind.Contextual);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    private static void HandleImitationLearningPower(
        ImitationLearningPower power,
        BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != power._playerTarget ||
            context.PreviewCard.Type != CardType.Power ||
            !context.CardPlay.IsFirstInSeries ||
            power.Owner.Player is not { } owner)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new ImitationLearningPredictionState(power));
        if (state.Amount > 0)
        {
            state.CardAndClones.Add((context.Card, context.Card.CreateCloneForPlayer(owner)));
        }
    }

    private static void HandleMonologuePower(MonologuePower power, BeforeCardPlayedMirrorContext context)
    {
        SnapshotOwnerCard(power, context, power.DynamicVars.Strength.IntValue);
    }

    private static void HandleOblivionPower(OblivionPower power, BeforeCardPlayedMirrorContext context)
    {
        if (power.Applier?.Player == context.PreviewCard.Owner)
        {
            GetPairState(power, context).Amounts.Add(context.CardPlay, power.Amount);
        }
    }

    private static void HandleRupturePower(RupturePower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner
            && context.CombatState.CurrentSide == power.Owner.Side)
        {
            context.StateStore.Get(power, static () => new RupturePredictionState())
                .StrengthByCard[context.Card.Original] = 0;
        }
    }

    private static void HandleSerpentFormPower(SerpentFormPower power, BeforeCardPlayedMirrorContext context)
    {
        SnapshotOwnerCard(power, context, power.Amount);
    }

    private static void HandleSlothPower(SlothPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player)
        {
            var state = context.StateStore.Get(power, () => new CounterPredictionState(power._cardsPlayedThisTurn));
            state.Value++;
        }
    }

    private static void HandleSpiritOfAshPower(SpiritOfAshPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player &&
            context.Card.HasKeyword(context.State, CardKeyword.Ethereal))
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleStormPower(StormPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Type == CardType.Power)
        {
            SnapshotOwnerCard(power, context, power.Amount);
        }
    }

    private static void HandleStranglePower(StranglePower power, BeforeCardPlayedMirrorContext context)
    {
        if (power.Applier?.Player == context.PreviewCard.Owner)
        {
            GetPairState(power, context).Amounts.Add(context.CardPlay, power.Amount);
        }
    }

    private static void HandleSubroutinePower(SubroutinePower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Type == CardType.Power)
        {
            SnapshotOwnerCard(power, context, power.Amount);
        }
    }

    private static void HandleTheSealedThronePower(
        TheSealedThronePower power,
        BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player)
        {
            context.Simulator.GainStars(context.PreviewCard.Owner, power.Amount);
        }
    }

    private static void HandleSurroundedPower(SurroundedPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.CardPlay.Target is not { } target || context.PreviewCard.Owner != power.Owner.Player)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new SurroundedPredictionState(power));
        if (target.HasPower<BackAttackLeftPower>())
        {
            state.Facing = SurroundedPower.Direction.Left;
        }
        else if (target.HasPower<BackAttackRightPower>())
        {
            state.Facing = SurroundedPower.Direction.Right;
        }
    }

    private static void HandleVeilpiercerPower(VeilpiercerPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner &&
            context.Card.HasKeyword(context.State, CardKeyword.Ethereal) &&
            context.Card.GetPile(context.State)?.Type is PileType.Hand or PileType.Play)
        {
            context.StateStore.GetPowerAmount(power).Decrement();
        }
    }

    private static void HandleIntimidatingHelmet(
        IntimidatingHelmet relic,
        BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner &&
            context.CardPlay.Resources.EnergyValue >= relic.DynamicVars.Energy.IntValue)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        }
    }

    private static void HandleMusicBox(MusicBox relic, BeforeCardPlayedMirrorContext context)
    {
        var state = context.StateStore.Get(relic, () => new MusicBoxPredictionState(relic));
        if (state.CardBeingPlayed is null &&
            !state.WasUsedThisTurn &&
            context.PreviewCard.Owner == relic.Owner &&
            context.PreviewCard.Type == CardType.Attack)
        {
            state.CardBeingPlayed = context.Card.Original;
        }
    }

    private static void HandlePenNib(PenNib relic, BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner != relic.Owner || context.PreviewCard.Type != CardType.Attack)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new PenNibPredictionState(relic));
        state.AttacksPlayed = (state.AttacksPlayed + 1) % 10;
        if (state.AttacksPlayed == 0)
        {
            state.AttackToDouble = context.Card.Original;
        }
    }

    private static void ConsumeFreeCardPower(
        PowerModel power,
        CardType type,
        BeforeCardPlayedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner &&
            context.PreviewCard.Type == type &&
            context.Card.GetPile(context.State)?.Type is PileType.Hand or PileType.Play)
        {
            context.StateStore.GetPowerAmount(power).Decrement();
        }
    }

    private static void SnapshotOwnerCard(
        PowerModel power,
        BeforeCardPlayedMirrorContext context,
        int amount)
    {
        if (context.PreviewCard.Owner == power.Owner.Player)
        {
            GetPairState(power, context).Amounts.Add(context.CardPlay, amount);
        }
    }

    private static CardPlayPairPredictionState GetPairState(
        AbstractModel model,
        BeforeCardPlayedMirrorContext context)
    {
        return context.StateStore.Get<CardPlayPairPredictionState>(model);
    }
}

internal sealed class BeforeCardPlayedMirrorContext : CombatCardMirrorContext
{
    public required CardPlay CardPlay { get; init; }
}

internal sealed class CardPlayPairPredictionState : IPredictionStateForkable, IPredictionForkBoundary
{
    public Dictionary<CardPlay, int> Amounts { get; } = new(ReferenceEqualityComparer.Instance);

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        return new CardPlayPairPredictionState();
    }

    public void AssertForkable()
    {
        if (Amounts.Count != 0)
            throw new InvalidOperationException("Cannot fork while paired card-play state is pending.");
    }
}

internal sealed class JugglingPredictionState(JugglingPower power) : IPredictionStateForkable
{
    public int AttacksPlayedThisTurn { get; set; } =
        power.GetInternalData<JugglingPower.Data>().attacksPlayedThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class MusicBoxPredictionState(MusicBox relic) : IPredictionStateForkable, IPredictionForkBoundary
{
    public bool WasUsedThisTurn { get; set; } = relic._wasUsedThisTurn;

    public CardModel? CardBeingPlayed { get; set; }

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        return MemberwiseClone();
    }

    public void AssertForkable()
    {
        if (CardBeingPlayed is not null)
            throw new InvalidOperationException("Cannot fork Music Box during card-play resolution.");
    }
}

internal sealed class ImitationLearningPredictionState(ImitationLearningPower power)
    : IPredictionStateForkable, IPredictionForkBoundary
{
    public List<(PredictedCard Card, PredictedCard Clone)> CardAndClones = [];

    public int Amount { get; set; } = power.Amount;

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        ImitationLearningPredictionState fork = (ImitationLearningPredictionState)MemberwiseClone();
        fork.CardAndClones = [];
        return fork;
    }

    public void AssertForkable()
    {
        if (CardAndClones.Count != 0)
            throw new InvalidOperationException("Cannot fork Imitation Learning during card-play resolution.");
    }
}
