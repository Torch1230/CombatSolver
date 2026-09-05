using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Achievements;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, AfterDamageGivenMirrorContext>;

internal static class AfterDamageGivenMirrors
{
    private static readonly MirrorMethodSpec AfterDamageGiven = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterDamageGiven),
        [
            typeof(PlayerChoiceContext),
            typeof(Creature),
            typeof(DamageResult),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel)
        ]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterDamageGivenMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterDamageGiven);

        registry.RegisterIgnored<SkillIronclad2Achievement>();
        registry.Register<ConcoctPower>(HandleConcoctPower);
        registry.Register<EnvenomPower>(HandleEnvenomPower);
        registry.RegisterIgnored<ImbalancedPower>();
        registry.Register<MonarchsGazePower>(HandleMonarchsGazePower);
        registry.Register<PaperCutsPower>(HandlePaperCutsPower);
        registry.Register<ReaperFormPower>(HandleReaperFormPower);
        registry.Register<SicEmPower>(HandleSicEmPower);
        registry.Register<UnderworldPower>(HandleUnderworldPower);

        return registry;
    }

    private static void HandleConcoctPower(ConcoctPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer == power.Owner &&
            context.Props.IsPoweredAttack() &&
            context.Result.UnblockedDamage > 0)
        {
            Effects(context).ApplyPower(typeof(PoisonPower), context.Target, power.Amount, power.Owner);
        }
    }

    private static void HandleEnvenomPower(EnvenomPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer == power.Owner &&
            context.Props.IsPoweredAttack() &&
            context.Result.UnblockedDamage > 0)
        {
            Effects(context).ApplyPower(typeof(PoisonPower), context.Target, power.Amount, power.Owner);
        }
    }

    private static void HandleMonarchsGazePower(MonarchsGazePower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer == power.Owner && context.Props.IsPoweredAttack())
        {
            Effects(context).ApplyTemporaryStrengthLoss(
                typeof(MonarchsGazeStrengthDownPower),
                context.Target,
                power.Amount,
                power.Owner);
        }
    }

    private static void HandleReaperFormPower(ReaperFormPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer != null &&
            (context.Dealer == power.Owner || context.Dealer.PetOwner?.Creature == power.Owner) &&
            context.Props.IsPoweredAttack() &&
            context.Result.TotalDamage > 0)
        {
            Effects(context).ApplyPower(
                typeof(DoomPower),
                context.Target,
                context.Result.TotalDamage * power.Amount,
                power.Owner);
        }
    }

    private static void HandlePaperCutsPower(PaperCutsPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer != power.Owner ||
            !context.Target.IsPlayer ||
            !context.Props.IsPoweredAttack() ||
            context.Result.UnblockedDamage <= 0)
        {
            return;
        }

        SimCreatureState target = context.Simulator.State.GetCreature(context.Target);
        int newMaxHp = target.MaxHp - power.Amount;
        if (newMaxHp < target.CurrentHp)
        {
            context.Simulator.Damage(
                context.Target,
                target.CurrentHp - newMaxHp,
                ValueProp.Unblockable | ValueProp.Unpowered,
                dealer: null);
            if (context.Simulator.HasPendingChoice)
                return;
        }
        target.SetMaxHp(Math.Max(1, newMaxHp));
    }

    private static void HandleSicEmPower(SicEmPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer?.Monster is Osty osty &&
            power.Applier != null &&
            osty.Creature.PetOwner?.Creature == power.Applier &&
            context.Target == power.Owner)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effectSink
                || context.Dealer.PetOwner is not { } owner)
            {
                throw new InvalidOperationException("SicEmPower 缺少可写的预测效果状态。");
            }
            effectSink.SummonOsty(context.Simulator, owner, power.Amount);
        }
    }

    private static void HandleUnderworldPower(UnderworldPower power, AfterDamageGivenMirrorContext context)
    {
        if (context.Dealer != null &&
            context.Dealer.Side == power.Owner.Side &&
            context.Dealer != power.Owner &&
            context.Dealer.PetOwner != power.Owner.Player &&
            context.Props.IsPoweredAttack() &&
            context.Result.TotalDamage > 0)
        {
            Effects(context).ApplyPower(
                typeof(DoomPower),
                context.Target,
                context.Result.TotalDamage * power.Amount,
                power.Owner);
        }
    }

    private static ICombatPredictionEffectSink Effects(AfterDamageGivenMirrorContext context)
        => context.CombatState as ICombatPredictionEffectSink
            ?? throw new InvalidOperationException("伤害后效果缺少可写的预测状态。");
}

internal sealed class AfterDamageGivenMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required DamageResult Result { get; init; }

    public required ValueProp Props { get; init; }

    public required Creature? Dealer { get; init; }

    public required PredictedCard? Source { get; init; }
}
