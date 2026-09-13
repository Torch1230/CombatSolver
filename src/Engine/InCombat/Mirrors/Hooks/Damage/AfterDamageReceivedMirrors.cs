using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, AfterDamageReceivedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterDamageReceived and its late phase.
internal static class AfterDamageReceivedMirrors
{
    private static readonly MirrorMethodSpec AfterDamageReceived = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterDamageReceived),
        [
            typeof(PlayerChoiceContext),
            typeof(Creature),
            typeof(DamageResult),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel)
        ]);

    private static readonly MirrorMethodSpec AfterDamageReceivedLate = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterDamageReceivedLate),
        [
            typeof(PlayerChoiceContext),
            typeof(Creature),
            typeof(DamageResult),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel)
        ]);

    private static readonly Registry Registry = CreateRegistry();
    private static readonly Registry LateRegistry = new(AfterDamageReceivedLate);

    public static void Invoke(AbstractModel listener, AfterDamageReceivedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    public static void InvokeLate(AbstractModel listener, AfterDamageReceivedMirrorContext context)
    {
        LateRegistry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterDamageReceived);

        registry.Register<AsleepPower>(HandleAsleepPower);
        registry.Register<BeatingRemnant>(HandleBeatingRemnant);
        registry.Register<CentennialPuzzle>(HandleCentennialPuzzle);
        registry.Register<CurlUpPower>(HandleCurlUpPower);
        registry.Register<DemonTongue>(HandleDemonTongue);
        registry.RegisterIgnored<EmotionChip>();
        registry.Register<FlameBarrierPower>(HandleFlameBarrierPower);
        registry.Register<FlutterPower>(HandleFlutterPower);
        registry.Register<HardenedShellPower>(HandleHardenedShellPower);
        registry.RegisterIgnored<LagavulinMatriarch>();
        registry.RegisterIgnored<LavaLamp>();
        registry.Register<InfernoPower>(HandleInfernoPower);
        registry.Register<PersonalHivePower>(HandlePersonalHivePower);
        registry.Register<PlowPower>(HandlePlowPower);
        registry.Register<ReflectPower>(HandleReflectPower);
        registry.Register<RupturePower>(HandleRupturePower);
        registry.Register<SelfFormingClay>(HandleSelfFormingClay);
        registry.Register<ShriekPower>(HandleShriekPower);
        registry.Register<SlipperyPower>(HandleSlipperyPower);
        registry.Register<SlumberPower>(HandleSlumberPower);
        registry.Register<TheGambitPower>(HandleTheGambitPower);

        return registry;
    }

    private static void HandleAsleepPower(AsleepPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && context.Result.UnblockedDamage != 0)
        {
            ICombatPredictionEffectSink effects = Effects(context);
            if (context.CombatState is not ICombatPredictionHookListenerSource listeners
                || context.CombatState is not ICombatPredictionMonsterStateSink monsterState)
                throw new InvalidOperationException("Asleep requires branch power and monster state.");
            foreach (PlatingPower plating in listeners.HookListeners.OfType<PlatingPower>()
                         .Where(current => current.Owner == power.Owner).ToArray())
                effects.SetPowerAmount(plating, 0);
            monsterState.SetMonsterBool(power.Owner, "_isAwake", true);
            effects.ForceStunnedMove(power.Owner, "SLASH_MOVE");
            effects.SetPowerAmount(power, 0);
        }
    }

    private static void HandleSlumberPower(SlumberPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && context.Result.UnblockedDamage != 0)
        {
            ICombatPredictionEffectSink effects = Effects(context);
            int remaining = power.Amount - 1;
            effects.SetPowerAmount(power, remaining);
            if (remaining <= 0)
                effects.ForceStunnedMove(power.Owner, "ROLL_OUT_MOVE");
        }
    }

    private static void HandleBeatingRemnant(BeatingRemnant relic, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == relic.Owner.Creature)
        {
            var state = context.StateStore.Get(relic, () => new BeatingRemnantPredictionState(relic));
            state.DamageReceivedThisTurn += context.Result.UnblockedDamage;
        }
    }

    private static void HandleCentennialPuzzle(CentennialPuzzle relic, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target != relic.Owner.Creature || context.Result.UnblockedDamage <= 0)
        {
            return;
        }

        var state = context.StateStore.Get(relic, () => new CentennialPuzzlePredictionState(relic));
        if (!state.UsedThisCombat)
        {
            state.UsedThisCombat = true;
            context.Simulator.Draw(relic.Owner, relic.DynamicVars.Cards.BaseValue);
        }
    }

    private static void HandleCurlUpPower(CurlUpPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target != power.Owner || !context.Props.IsPoweredAttack() || context.Source is null)
        {
            return;
        }

        var state = context.StateStore.Get<CurlUpPredictionState>(power);
        if (state is { Consumed: false, PlayedCard: null })
        {
            state.PlayedCard = context.Source.Original;
        }
    }

    private static void HandleDemonTongue(DemonTongue relic, AfterDamageReceivedMirrorContext context)
    {
        if (context.CombatState.CurrentSide == relic.Owner.Creature.Side &&
            context.Target == relic.Owner.Creature &&
            context.Result.UnblockedDamage > 0)
        {
            var state = context.StateStore.Get(relic, () => new DemonTonguePredictionState(relic));
            if (!state.TriggeredThisTurn)
            {
                state.TriggeredThisTurn = true;
                context.Simulator.Heal(relic.Owner.Creature, context.Result.UnblockedDamage);
            }
        }
    }

    private static void HandleFlameBarrierPower(FlameBarrierPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && context.Dealer is not null && context.Props.IsPoweredAttack())
        {
            context.Simulator.Damage(context.Dealer, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleFlutterPower(FlutterPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage == 0
            || !context.Props.IsPoweredAttack())
            return;

        PowerAmountPredictionState amount = context.StateStore.GetPowerAmount(power);
        if (!amount.IsActive)
            return;
        amount.Decrement();
        if (amount.IsActive)
            return;

        if (context.CombatState is not ICombatPredictionMonsterStateSink monsterState)
            throw new InvalidOperationException("振翅 Power 缺少分支怪物行动状态。");
        Effects(context).ForceStunnedMove(
            power.Owner,
            monsterState.GetNextMoveIdFromStateLog(power.Owner, context.Rng.MonsterAi));
    }

    private static void HandleHardenedShellPower(HardenedShellPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && !context.Result.WasFullyBlocked)
        {
            var state = context.StateStore.Get(power, () => new HardenedShellPredictionState(power));
            state.DamageReceivedThisTurn += context.Result.UnblockedDamage;

            if (state.DamageReceivedThisTurn >= power.Amount)
            {
                context.State.GetCreature(power.Owner).HpDisplay = HpDisplay.InfiniteWithNumbers;
            }
        }
    }

    private static void HandleInfernoPower(InfernoPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner &&
            context.Result.UnblockedDamage > 0 &&
            context.CombatState.CurrentSide == power.Owner.Side)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandlePersonalHivePower(PersonalHivePower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && context.Dealer is not null && context.Props.IsPoweredAttack())
        {
            var dealer = context.Dealer;
            if (dealer.Monster is Osty)
            {
                dealer = dealer.PetOwner?.Creature;
            }

            if (dealer?.Player is { } player)
            {
                context.Simulator.CreateAndAddGeneratedCardsToCombat<Dazed>(
                    player,
                    PileType.Draw,
                    power.Amount,
                    creator: null,
                    CardPilePosition.Random);
            }
        }
    }

    private static void HandleReflectPower(ReflectPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner &&
            context.Result.BlockedDamage > 0 &&
            context.Props.IsPoweredAttack() &&
            context.Dealer is not null)
        {
            context.Simulator.Damage(context.Dealer, context.Result.BlockedDamage, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandlePlowPower(PlowPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || context.State.GetCreature(context.Target).CurrentHp > power.Amount)
        {
            return;
        }
        ICombatPredictionEffectSink effects = Effects(context);
        if (context.CombatState is not ICombatPredictionHookListenerSource listeners)
            throw new InvalidOperationException("犁击效果缺少预测 Power 列表。");
        foreach (PowerModel current in listeners.HookListeners.OfType<PowerModel>()
                     .Where(current => current.Owner == power.Owner
                         && current is TemporaryStrengthPower or StrengthPower)
                     .ToArray())
        {
            effects.SetPowerAmount(current, 0);
        }
        effects.SetPowerAmount(power, 0);
        string? nextMove = power.Owner.Monster is CeremonialBeast beast
            ? beast.BeastCryState.StateId
            : null;
        effects.ForceStunnedMove(power.Owner, nextMove);
    }

    private static void HandleShriekPower(ShriekPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || context.State.GetCreature(context.Target).CurrentHp > power.Amount)
        {
            return;
        }
        ICombatPredictionEffectSink effects = Effects(context);
        effects.SetPowerAmount(power, 0);
        string nextMove = power.Owner.Monster is TerrorEel eel
            ? eel.TerrorState.StateId
            : throw new InvalidOperationException("尖啸 Power 的持有者不是骇鳗。");
        effects.ForceStunnedMove(power.Owner, nextMove);
    }

    private static void HandleRupturePower(RupturePower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner &&
            context.Result.UnblockedDamage > 0 &&
            context.CombatState.CurrentSide == power.Owner.Side)
        {
            RupturePredictionState state = context.StateStore.Get(
                power,
                static () => new RupturePredictionState());
            if (context.Source != null
                && state.StrengthByCard.ContainsKey(context.Source.Original))
            {
                state.StrengthByCard[context.Source.Original] += power.Amount;
            }
            else
            {
                Effects(context).ApplyPower(typeof(StrengthPower), power.Owner, power.Amount, power.Owner);
            }
        }
    }

    private static void HandleSelfFormingClay(SelfFormingClay relic, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == relic.Owner.Creature && context.Result.UnblockedDamage > 0)
        {
            Effects(context).ApplyPower(
                typeof(SelfFormingClayPower),
                relic.Owner.Creature,
                relic.DynamicVars["BlockNextTurn"].IntValue,
                relic.Owner.Creature);
        }
    }

    private static void HandleSlipperyPower(SlipperyPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner && context.Result.UnblockedDamage >= 1)
        {
            context.StateStore.GetPowerAmount(power).Decrement();
        }
    }

    private static void HandleTheGambitPower(TheGambitPower power, AfterDamageReceivedMirrorContext context)
    {
        if (context.Target == power.Owner &&
            context.Props.IsPoweredAttack() &&
            context.Result.UnblockedDamage > 0)
        {
            Effects(context).SetPowerAmount(power, 0);
            context.Simulator.Kill(power.Owner);
        }
    }

    private static ICombatPredictionEffectSink Effects(AfterDamageReceivedMirrorContext context)
        => context.CombatState as ICombatPredictionEffectSink
            ?? throw new InvalidOperationException("受伤后效果缺少可写的预测状态。");
}

internal sealed class AfterDamageReceivedMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required DamageResult Result { get; init; }

    public required ValueProp Props { get; init; }

    public required Creature? Dealer { get; init; }

    public required PredictedCard? Source { get; init; }
}

internal sealed class CentennialPuzzlePredictionState(CentennialPuzzle relic) : IPredictionStateForkable
{
    public bool UsedThisCombat { get; set; } = relic.UsedThisCombat;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class DemonTonguePredictionState(DemonTongue relic) : IPredictionStateForkable
{
    public bool TriggeredThisTurn { get; set; } = relic._triggeredThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class BeatingRemnantPredictionState(BeatingRemnant relic) : IPredictionStateForkable
{
    public decimal DamageReceivedThisTurn { get; set; } = relic._damageReceivedThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class HardenedShellPredictionState(HardenedShellPower power) : IPredictionStateForkable
{
    public decimal DamageReceivedThisTurn { get; set; } =
        power.GetInternalData<HardenedShellPower.Data>().damageReceivedThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
