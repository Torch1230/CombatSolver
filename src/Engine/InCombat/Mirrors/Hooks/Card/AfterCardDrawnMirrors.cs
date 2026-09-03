using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using Models = MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCardDrawnMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterCardDrawn.
internal static class AfterCardDrawnMirrors
{
    private static readonly MirrorMethodSpec AfterCardDrawnEarly = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardDrawnEarly),
        [
            typeof(PlayerChoiceContext),
            typeof(CardModel),
            typeof(bool)
        ]);

    private static readonly MirrorMethodSpec AfterCardDrawn = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardDrawn),
        [
            typeof(PlayerChoiceContext),
            typeof(CardModel),
            typeof(bool)
        ]);

    private static readonly Registry EarlyRegistry = CreateEarlyRegistry();
    private static readonly Registry Registry = CreateRegistry();

    public static void InvokeEarly(AbstractModel listener, AfterCardDrawnMirrorContext context)
    {
        EarlyRegistry.Invoke(listener, context);
    }

    public static void Invoke(AbstractModel listener, AfterCardDrawnMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateEarlyRegistry()
    {
        var registry = new Registry(AfterCardDrawnEarly);

        registry.Register<HellraiserPower>(HandleHellraiserPower);

        return registry;
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCardDrawn);

        registry.Register<ConfusedPower>(HandleConfusedPower);
        registry.Register<Slither>(HandleSlither);
        registry.Register<IterationPower>(HandleIterationPower);
        registry.Register<PagestormPower>(HandlePagestormPower);
        registry.Register<ChainsOfBindingPower>(HandleChainsOfBindingPower);
        registry.Register<CorrosiveWavePower>(HandleCorrosiveWavePower);
        registry.Register<SpeedsterPower>(HandleSpeedsterPower);
        registry.Register<CacophonyPower>(HandleCacophonyPower);
        registry.Register<KinglyKick>(HandleKinglyKick);
        registry.Register<KinglyPunch>(HandleKinglyPunch);
        registry.Register<AutomationPower>(HandleAutomationPower);
        registry.Register<Models.Cards.Void>(HandleVoid);

        return registry;
    }

    private static void HandleHellraiserPower(HellraiserPower power, AfterCardDrawnMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature != power.Owner ||
            !context.PreviewCard.Tags.Contains(CardTag.Strike))
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new HellraiserPredictionState(power));
        var shouldAutoPlay = true;

        if (context.State.HittableEnemies.All(c => context.State.GetCreature(c).HpDisplay.IsInfinite()))
        {
            if (state.InfiniteAutoPlaysThisTurn >= HellraiserPower._infiniteAutoPlayCap)
            {
                shouldAutoPlay = false;
            }

            state.InfiniteAutoPlaysThisTurn++;
        }
        else
        {
            state.InfiniteAutoPlaysThisTurn = 0;
        }

        if (shouldAutoPlay)
        {
            context.Simulator.AutoPlay(context.Card, nestedChoiceSourceId: power.Id.Entry);
        }
    }

    private static void HandleConfusedPower(ConfusedPower power, AfterCardDrawnMirrorContext context)
    {
        if (context.PreviewCard.Owner == power.Owner.Player &&
            context.PreviewCard.EnergyCost.Canonical >= 0)
        {
            SetRandomEnergyCost(context);
        }
    }

    private static void HandleSlither(Slither slither, AfterCardDrawnMirrorContext context)
    {
        if (context.Card.References(slither.Card) &&
            context.State.GetPlayerCombatState(context.PreviewCard.Owner).Hand.Cards.Contains(context.Card))
        {
            SetRandomEnergyCost(context);
        }
    }

    private static void HandleIterationPower(IterationPower power, AfterCardDrawnMirrorContext context)
    {
        if (power.Owner.Player is { } player &&
            context.PreviewCard.Owner == player &&
            context.PreviewCard.Type == CardType.Status &&
            CountStatusCardsDrawnThisTurn(context.Simulator, player) <= 1)
        {
            context.Simulator.Draw(player, power.Amount);
        }
    }

    private static void HandlePagestormPower(PagestormPower power, AfterCardDrawnMirrorContext context)
    {
        if (power.Owner.Player is { } player &&
            context.PreviewCard.Owner == player &&
            context.Card.HasKeyword(context.State, CardKeyword.Ethereal))
        {
            context.Simulator.Draw(player, power.Amount);
        }
    }

    private static void HandleChainsOfBindingPower(ChainsOfBindingPower power, AfterCardDrawnMirrorContext context)
    {
        if (power.Owner.Player is { } player &&
            context.PreviewCard.Owner == player &&
            context.CombatState.CurrentSide == power.Owner.Side &&
            // CanAfflict only checks card type, existing affliction, and Unplayable.
            // Vanilla currently has no global hook that adds Unplayable, so preview keywords are sufficient here.
            CanonicalModels.Affliction<Bound>().CanAfflict(context.PreviewCard))
        {
            ChainsOfBindingPredictionState state = context.StateStore.Get(
                power,
                () => new ChainsOfBindingPredictionState(power));
            if (state.BoundCardsAfflictedThisTurn < power.Amount
                && context.Simulator.Afflict<Bound>(context.Card, power.Amount) is not null)
            {
                state.BoundCardsAfflictedThisTurn++;
            }
        }
    }

    private static void HandleSpeedsterPower(SpeedsterPower power, AfterCardDrawnMirrorContext context)
    {
        if (!context.FromHandDraw &&
            context.PreviewCard.Owner.Creature == power.Owner &&
            context.PreviewCard.Owner.Creature.Side == context.CombatState.CurrentSide)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleCacophonyPower(CacophonyPower power, AfterCardDrawnMirrorContext context)
    {
        var state = context.StateStore.Get(power, () => new CacophonyPredictionState(power));
        state.CardsDrawn--;

        if (state.CardsDrawn <= 0)
        {
            var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);

            // StS2 v0.111.0 resets the counter before dealing damage so nested draws caused by damage hooks
            // count toward the next trigger instead of retriggering Cacophony immediately.
            // Use the canonical value to avoid hard-coding the number threshold in case it changes in the future.
            state.CardsDrawn = power.CanonicalInstance.DynamicVars.Cards.IntValue;

            if (target is not null)
            {
                context.Simulator.Damage(target, power.Amount, ValueProp.Unpowered, power.Owner);
            }
        }
    }

    private static void HandleKinglyKick(KinglyKick card, AfterCardDrawnMirrorContext context)
    {
        if (!context.IntrinsicCardHandled && context.Card.References(card))
        {
            context.IntrinsicCardHandled = true;
            context.MutablePreviewCard.EnergyCost.AddThisCombat(-1);
        }
    }

    private static void HandleKinglyPunch(KinglyPunch card, AfterCardDrawnMirrorContext context)
    {
        if (!context.IntrinsicCardHandled && context.Card.References(card))
        {
            context.IntrinsicCardHandled = true;
            var previewCard = (KinglyPunch)context.MutablePreviewCard;
            var damageIncrease = previewCard.DynamicVars[KinglyPunch._increaseKey].BaseValue;
            previewCard.DynamicVars.Damage.BaseValue += damageIncrease;
            previewCard.ExtraDamage += damageIncrease;
        }
    }

    private static void HandleAutomationPower(AutomationPower power, AfterCardDrawnMirrorContext context)
    {
        if (context.PreviewCard.Owner != power.Owner.Player)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new AutomationPredictionState(power));
        state.CardsLeft--;
        if (state.CardsLeft <= 0)
        {
            context.Simulator.GainEnergy(context.PreviewCard.Owner, power.Amount);
            state.CardsLeft = AutomationPower._baseCardsLeft;
        }
    }

    private static void HandleVoid(Models.Cards.Void card, AfterCardDrawnMirrorContext context)
    {
        if (!context.IntrinsicCardHandled && context.Card.References(card))
        {
            context.IntrinsicCardHandled = true;
            context.Simulator.LoseEnergy(
                context.PreviewCard.Owner,
                context.PreviewCard.DynamicVars.Energy.IntValue);
        }
    }

    private static void HandleCorrosiveWavePower(CorrosiveWavePower power, AfterCardDrawnMirrorContext context)
    {
        // CorrosiveWavePower applies Poison to all hittable enemies; power application
        // side effects are not mirrored here.
        if (context.PreviewCard.Owner.Creature == power.Owner)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("腐蚀波效果缺少可写的预测状态。");
            foreach (Creature enemy in context.State.HittableEnemies)
                effects.ApplyPower(typeof(PoisonPower), enemy, power.Amount, power.Owner);
        }
    }

    private static void SetRandomEnergyCost(AfterCardDrawnMirrorContext context)
    {
        context.MutablePreviewCard.EnergyCost.SetThisCombat(context.Rng.CombatEnergyCosts.NextInt(4));
    }

    private static int CountStatusCardsDrawnThisTurn(CombatPredictionSimulator simulator, Player player)
    {
        if (simulator.State.CombatState is not ICombatPredictionCardEventSink eventSink)
            throw new InvalidOperationException("迭代效果缺少可读取的本回合抽牌历史。");
        return eventSink.GetStatusCardsDrawnThisTurn(player);
    }

}

internal sealed class AfterCardDrawnMirrorContext : CombatCardMirrorContext
{
    public required bool FromHandDraw { get; init; }
    public bool IntrinsicCardHandled { get; set; }
}

internal sealed class HellraiserPredictionState(HellraiserPower power) : IPredictionStateForkable
{
    public int InfiniteAutoPlaysThisTurn { get; set; } =
        power.GetInternalData<HellraiserPower.Data>().infiniteAutoPlaysThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class CacophonyPredictionState(CacophonyPower power) : IPredictionStateForkable
{
    public int CardsDrawn { get; set; } = power.DynamicVars.Cards.IntValue;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class AutomationPredictionState(AutomationPower power) : IPredictionStateForkable
{
    public int CardsLeft { get; set; } = power.DisplayAmount;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
