using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyDamageMirrorContext, decimal>;

// Mirrors the additive and multiplicative listener passes inside Hook.ModifyDamage.
internal static class ModifyDamageMirrors
{
    private static readonly MirrorMethodSpec ModifyDamageAdditive = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyDamageAdditive),
        [
            typeof(Creature),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel),
            typeof(CardPlay)
        ]);

    private static readonly MirrorMethodSpec ModifyDamageMultiplicative = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyDamageMultiplicative),
        [
            typeof(Creature),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel),
            typeof(CardPlay)
        ]);

    private static readonly Registry AdditiveRegistry = CreateAdditiveRegistry();
    private static readonly Registry MultiplicativeRegistry = CreateMultiplicativeRegistry();

    public static decimal InvokeAdditive(AbstractModel listener, ModifyDamageMirrorContext context)
    {
        return AdditiveRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalAdditive(listener, context);
    }

    public static decimal InvokeMultiplicative(AbstractModel listener, ModifyDamageMirrorContext context)
    {
        return MultiplicativeRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalMultiplicative(listener, context);
    }

    private static decimal InvokeOriginalAdditive(AbstractModel listener, ModifyDamageMirrorContext context)
    {
        return listener.ModifyDamageAdditive(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview,
            context.CardPlay);
    }

    private static decimal InvokeOriginalMultiplicative(AbstractModel listener, ModifyDamageMirrorContext context)
    {
        return listener.ModifyDamageMultiplicative(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview,
            context.CardPlay);
    }

    private static Registry CreateAdditiveRegistry()
    {
        var registry = new Registry(ModifyDamageAdditive);

        registry.Register<PhantomBladesPower>(HandlePhantomBladesPower);
        registry.Register<VigorPower>(VigorPowerMirrors.ModifyDamageAdditive);

        return registry;
    }

    private static Registry CreateMultiplicativeRegistry()
    {
        var registry = new Registry(ModifyDamageMultiplicative);

        registry.Register<FlutterPower>(HandleFlutterPower);
        registry.Register<GigantificationPower>(GigantificationPowerMirrors.ModifyDamageMultiplicative);
        registry.Register<ColossusPower>(HandleColossusPower);
        registry.Register<LethalityPower>(HandleLethalityPower);
        registry.Register<SlowPower>(HandleSlowPower);
        registry.Register<SurroundedPower>(HandleSurroundedPower);
        registry.Register<TrackingPower>(HandleTrackingPower);
        registry.Register<VulnerablePower>(HandleVulnerablePower);
        registry.Register<WeakPower>(HandleWeakPower);

        registry.Register<PenNib>(HandlePenNib);
        registry.Register<UndyingSigil>(HandleUndyingSigil);

        return registry;
    }

    private static decimal HandleFlutterPower(FlutterPower power, ModifyDamageMirrorContext context)
    {
        return context.StateStore.GetPowerAmount(power).IsActive
            ? InvokeOriginalMultiplicative(power, context)
            : 1;
    }

    private static decimal HandlePhantomBladesPower(
        PhantomBladesPower power,
        ModifyDamageMirrorContext context)
    {
        if (!context.Props.IsPoweredAttack()
            || context.CardSource is null
            || !context.CardSource.Preview.Tags.Contains(CardTag.Shiv)
            || context.Dealer != power.Owner
            || context.CardPlay?.PlayIndex > 0)
        {
            return 0;
        }

        return Combat(context).GetShivsPlayedThisTurn(power.Owner) == 0
            ? power.Amount
            : 0;
    }

    private static decimal HandleColossusPower(ColossusPower power, ModifyDamageMirrorContext context)
    {
        if (context.Target != power.Owner
            || !context.Props.IsPoweredAttack()
            || context.Dealer is null
            || Combat(context).GetAmount<VulnerablePower>(context.Dealer) <= 0)
        {
            return 1;
        }

        return power.DynamicVars["DamageDecrease"].BaseValue;
    }

    private static decimal HandleLethalityPower(LethalityPower power, ModifyDamageMirrorContext context)
    {
        if (!context.Props.IsPoweredAttack()
            || context.CardSource is null
            || context.CardSource.Preview.Owner.Creature != power.Owner)
        {
            return 1;
        }

        bool inPlay = context.CardSource.GetPile(context.State)?.Type == PileType.Play;
        if (inPlay && context.CardSource.Preview.CurrentPlayIndex > 0)
            return 1;
        return Combat(context).GetAttackPlayStartsThisTurn(power.Owner) <= (inPlay ? 1 : 0)
            ? 1 + (decimal)power.Amount / 100m
            : 1;
    }

    private static decimal HandleSlowPower(SlowPower power, ModifyDamageMirrorContext context)
    {
        if (context.Target != power.Owner || !context.Props.IsPoweredAttack())
        {
            return 1;
        }

        var amount = context.StateStore.Get(power,
            () => new CounterPredictionState(power.DynamicVars["SlowAmount"].IntValue)).Value;
        return 1 + 0.1m * amount;
    }

    private static decimal HandleSurroundedPower(SurroundedPower power, ModifyDamageMirrorContext context)
    {
        if (context.Dealer is null || context.Target != power.Owner)
        {
            return 1;
        }

        var facing = context.StateStore.Get(power, () => new SurroundedPredictionState(power)).Facing;
        return facing switch
        {
            SurroundedPower.Direction.Right when context.Dealer.HasPower<BackAttackLeftPower>() => 1.5m,
            SurroundedPower.Direction.Left when context.Dealer.HasPower<BackAttackRightPower>() => 1.5m,
            _ => 1
        };
    }

    private static decimal HandleTrackingPower(TrackingPower power, ModifyDamageMirrorContext context)
    {
        if (!context.Props.IsPoweredAttack()
            || context.CardSource is null
            || context.Dealer is null
            || context.Dealer != power.Owner && !power.Owner.Pets.Contains<Creature>(context.Dealer)
            || context.Target is null)
        {
            return 1;
        }

        return Combat(context).GetAmount<WeakPower>(context.Target) > 0
            ? 1 + (decimal)power.Amount / 100m
            : 1;
    }

    private static decimal HandleWeakPower(WeakPower power, ModifyDamageMirrorContext context)
    {
        if (context.Dealer != power.Owner || !context.Props.IsPoweredAttack())
            return 1;

        decimal multiplier = power.DynamicVars["DamageDecrease"].BaseValue;
        if (context.Target?.Player?.GetRelic<PaperKrane>() is { } paperKrane)
        {
            multiplier = paperKrane.ModifyWeakMultiplier(
                context.Target,
                multiplier,
                context.Props,
                context.Dealer,
                context.CardSource?.Preview);
        }
        if (Combat(context).GetAmount<DebilitatePower>(power.Owner) > 0)
            multiplier -= 1m - multiplier;
        return multiplier;
    }

    private static decimal HandleVulnerablePower(
        VulnerablePower power,
        ModifyDamageMirrorContext context)
    {
        if (context.Target != power.Owner || !context.Props.IsPoweredAttack())
            return 1;

        decimal multiplier = power.DynamicVars["DamageIncrease"].BaseValue;
        if (context.Dealer?.Player?.GetRelic<PaperPhrog>() is { } paperPhrog)
        {
            multiplier = paperPhrog.ModifyVulnerableMultiplier(
                context.Target,
                multiplier,
                context.Props,
                context.Dealer,
                context.CardSource?.Preview);
        }
        if (context.Dealer is { } dealer)
        {
            int cruelty = Combat(context).GetAmount<CrueltyPower>(dealer);
            if (cruelty == 0 && dealer.PetOwner?.Creature is { } petOwner)
                cruelty = Combat(context).GetAmount<CrueltyPower>(petOwner);
            multiplier += (decimal)cruelty / 100m;
        }
        if (Combat(context).GetAmount<DebilitatePower>(power.Owner) > 0)
            multiplier += multiplier - 1m;
        return multiplier;
    }

    private static decimal HandleUndyingSigil(UndyingSigil relic, ModifyDamageMirrorContext context)
    {
        if (context.Dealer is null
            || !context.Props.IsPoweredAttack()
            || context.Target != relic.Owner.Creature
            || context.Dealer == relic.Owner.Creature)
        {
            return 1;
        }

        SimulatedCombatState combat = Combat(context);
        return context.State.GetCreature(context.Dealer).CurrentHp
                <= combat.GetAmount<DoomPower>(context.Dealer)
            ? relic.DynamicVars["DamageDecrease"].BaseValue
            : 1;
    }

    private static SimulatedCombatState Combat(ModifyDamageMirrorContext context)
        => context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("伤害修正缺少分支战斗状态。");

    private static decimal HandlePenNib(PenNib relic, ModifyDamageMirrorContext context)
    {
        if (!context.Props.IsPoweredAttack() ||
            context.CardSource is null ||
            context.Dealer != relic.Owner.Creature && context.Dealer != context.State.GetOsty(relic.Owner))
        {
            return 1;
        }

        var state = context.StateStore.Get(relic, () => new PenNibPredictionState(relic));
        if (state.AttackToDouble is not null)
        {
            return state.AttackToDouble == context.CardSource.Original ? 2 : 1;
        }

        return context.CardPlay is null &&
            context.CardSource.GetPile(context.State)?.Type is not PileType.Play &&
            state.AttacksPlayed == 9
                ? 2
                : 1;
    }
}

internal sealed class ModifyDamageMirrorContext : CombatMirrorContext
{
    public required Creature? Target { get; init; }

    public required Creature? Dealer { get; init; }

    public required decimal Amount { get; set; }

    public required ValueProp Props { get; init; }

    public required PredictedCard? CardSource { get; init; }

    public required CardPlay? CardPlay { get; init; }
}
