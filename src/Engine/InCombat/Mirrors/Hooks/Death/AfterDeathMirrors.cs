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

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;

using Registry = MethodMirrorRegistry<AbstractModel, AfterDeathMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterDeath.
internal static class AfterDeathMirrors
{
    private static readonly MirrorMethodSpec AfterDeath = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterDeath),
        [
            typeof(PlayerChoiceContext),
            typeof(Creature),
            typeof(bool),
            typeof(float)
        ]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterDeathMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterDeath);

        registry.RegisterIgnored<Aeonglass>();
        registry.RegisterIgnored<DecimillipedeSegment>();
        registry.RegisterIgnored<KinPriest>();
        registry.RegisterIgnored<LagavulinMatriarch>();
        registry.Register<Queen>(HandleQueen);
        registry.RegisterIgnored<SoulFysh>();
        registry.RegisterIgnored<TestSubject>();
        registry.RegisterIgnored<TheInsatiable>();
        registry.RegisterIgnored<Vantom>();
        registry.RegisterIgnored<WaterfallGiant>();

        registry.Register<GremlinHorn>(HandleGremlinHorn);
        registry.Register<Melancholy>(HandleMelancholy);
        registry.Register<StockPower>(HandleStock);
        registry.Register<CrabRagePower>(HandleCrabRage);
        registry.Register<DampenPower>(HandleDampen);

        return registry;
    }

    private static void HandleDampen(DampenPower power, AfterDeathMirrorContext context)
    {
        if (!context.WasRemovalPrevented)
        {
            if (context.CombatState is not SimulatedCombatState combat)
                throw new InvalidOperationException("Dampen death requires captured caster and card state.");
            combat.RemoveDampenCaster(context.Creature);
        }
    }

    private static void HandleCrabRage(CrabRagePower power, AfterDeathMirrorContext context)
    {
        if (context.Creature != power.Owner && context.Creature.Side == power.Owner.Side)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("Crab rage requires writable branch state.");
            effects.ApplyPowerFromSource(typeof(StrengthPower), power.Owner,
                power.DynamicVars.Strength.IntValue, power.Owner, null);
            context.Simulator.GainBlock(power.Owner, power.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
            effects.SetPowerAmount(power, 0);
        }
    }

    private static void HandleStock(StockPower power, AfterDeathMirrorContext context)
    {
        if (!context.WasRemovalPrevented && context.Creature == power.Owner && power.Amount > 0)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("补货效果缺少可写的预测状态。");
            effects.SpawnStockReplacement(context.Simulator, power);
        }
    }

    private static void HandleGremlinHorn(GremlinHorn relic, AfterDeathMirrorContext context)
    {
        if (context.Creature.Side != relic.Owner.Creature.Side)
        {
            context.Simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
            context.Simulator.Draw(relic.Owner, relic.DynamicVars.Cards.BaseValue);
        }
    }

    private static void HandleQueen(Queen queen, AfterDeathMirrorContext context)
    {
        if (context.Creature.Monster is not TorchHeadAmalgam ||
            !context.State.GetCreature(queen.Creature).IsAlive)
        {
            return;
        }
        if (context.CombatState is not ICombatPredictionMonsterStateSink monsterState)
            throw new InvalidOperationException("女王死亡联动缺少可写的预测怪物状态。");
        monsterState.SetMonsterBool(queen.Creature, "_hasAmalgamDied", true);
        if (monsterState.GetPredictedMoveId(queen.Creature) == "BURN_BRIGHT_FOR_ME_MOVE")
            monsterState.ForceMonsterMove(queen.Creature, "ENRAGE_MOVE");
    }

    private static void HandleMelancholy(Melancholy card, AfterDeathMirrorContext context)
    {
        if (!context.WasRemovalPrevented)
        {
            var previewCard = context.State.FindCard(card)?.MutablePreview;
            previewCard?.EnergyCost.AddThisCombat(-previewCard.DynamicVars.Energy.IntValue);
        }
    }
}

internal sealed class AfterDeathMirrorContext : CombatMirrorContext
{
    public required Creature Creature { get; init; }

    public required bool WasRemovalPrevented { get; init; }
}
