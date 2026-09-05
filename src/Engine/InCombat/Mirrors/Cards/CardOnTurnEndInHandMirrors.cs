using System.Reflection;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards;

using Registry = MethodMirrorRegistry<CardModel, CardOnTurnEndInHandMirrorContext>;

// Simulation-facing facade and central registration index for mirrored CardModel.OnTurnEndInHand behavior.
internal static class CardOnTurnEndInHandMirrors
{
    private static readonly MirrorMethodSpec OnTurnEndInHand = new(
        typeof(CardModel),
        "OnTurnEndInHand",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(PlayerChoiceContext)]);

    private static readonly Registry Registry = CreateRegistry();

    public static MirrorDispatchResult Invoke(CombatPredictionSimulator simulator, PredictedCard card)
    {
        // Do not force a clone for read-only handlers. Regret's BeforeSideTurnEnd mirror creates its mutable preview
        // before this dispatch because that override reads and resets card-local state.
        return Registry.Invoke(card.Preview, new()
        {
            Simulator = simulator,
            Card = card
        });
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(OnTurnEndInHand);

        registry.Register<BadLuck>(HandleHpLoss);
        registry.Register<Beckon>(HandleHpLoss);

        registry.Register<Burn>(HandleDamage);
        registry.Register<Decay>(HandleDamage);
        registry.Register<Infection>(HandleDamage);
        registry.Register<Toxic>(HandleDamage);
        registry.Register<Wither>(HandleDamage);

        registry.Register<Regret>(HandleRegret);

        registry.Register<Debt>(HandleDebt);
        registry.Register<Doubt>(HandleDoubt);
        registry.Register<Shame>(HandleShame);

        return registry;
    }

    private static void HandleDamage(CardModel card, CardOnTurnEndInHandMirrorContext context)
    {
        DamageOwner(context, card.DynamicVars.Damage.BaseValue, card.DynamicVars.Damage.Props);
    }

    private static void HandleHpLoss(CardModel card, CardOnTurnEndInHandMirrorContext context)
    {
        DamageOwner(context, card.DynamicVars.HpLoss.BaseValue, DamageProps.cardHpLoss);
    }

    private static void HandleRegret(Regret card, CardOnTurnEndInHandMirrorContext context)
    {
        var previewCard = (Regret)context.MutablePreviewCard;
        DamageOwner(context, previewCard.CardsInHand, DamageProps.cardHpLoss);
        if (context.Simulator.HasPendingChoice)
            return;
        previewCard.CardsInHand = 0;
    }

    private static void HandleDebt(Debt card, CardOnTurnEndInHandMirrorContext context)
    {
        Effects(context).LosePlayerGold(card.Owner, card.DynamicVars.Gold.IntValue);
    }

    private static void HandleDoubt(Doubt card, CardOnTurnEndInHandMirrorContext context)
    {
        Effects(context).ApplyPowerSkippingNextDurationTick(
            typeof(MegaCrit.Sts2.Core.Models.Powers.WeakPower),
            card.Owner.Creature,
            card.DynamicVars.Weak.IntValue);
    }

    private static void HandleShame(Shame card, CardOnTurnEndInHandMirrorContext context)
    {
        Effects(context).ApplyPowerSkippingNextDurationTick(
            typeof(MegaCrit.Sts2.Core.Models.Powers.FrailPower),
            card.Owner.Creature,
            card.DynamicVars["Frail"].IntValue);
    }

    private static ICombatPredictionEffectSink Effects(CardOnTurnEndInHandMirrorContext context)
        => context.State.CombatState as ICombatPredictionEffectSink
            ?? throw new InvalidOperationException("手牌回合末效果缺少可写的预测状态。");

    /// <summary>
    /// Mirrors the shared damage behavior for turn-end-in-hand cards.
    /// </summary>
    private static void DamageOwner(CardOnTurnEndInHandMirrorContext context, decimal amount, ValueProp props)
    {
        var owner = context.PreviewCard.Owner.Creature;
        context.Simulator.Damage([owner], amount, props, owner, context.Card, cardPlay: null);
    }
}

internal sealed class CardOnTurnEndInHandMirrorContext : CombatCardMirrorContext<CardModel>
{
    // The dispatch trace belongs to the real card, not its optional detached preview.
    protected override AbstractModel GetDispatchSource(CardModel _) => OriginalCard;
}
