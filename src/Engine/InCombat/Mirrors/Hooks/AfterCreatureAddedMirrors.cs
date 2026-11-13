using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks;

internal static class AfterCreatureAddedMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, AfterCreatureAddedMirrorContext> Registry = Create();
    private static readonly object Gate = new();
    private static bool _sealed;
    internal static void Seal() { lock (Gate) _sealed = true; }
    public static void Register<T>(Action<T, AfterCreatureAddedMirrorContext> handler) where T : AbstractModel
    {
        lock (Gate)
        {
            if (_sealed) throw new InvalidOperationException("Register AfterCreatureAdded mirrors before root capture.");
            Registry.Register(handler);
        }
    }
    internal static void Invoke(AbstractModel model, AfterCreatureAddedMirrorContext context)
    {
        Seal();
        if (Registry.Invoke(model, context).Kind == MirrorDispatchKind.Unsupported)
            throw PredictionUnsupportedException.ForContent($"No AfterCreatureAddedToCombat mirror for {model.GetType().FullName}.", model.GetType());
    }
    private static MethodMirrorRegistry<AbstractModel, AfterCreatureAddedMirrorContext> Create()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, AfterCreatureAddedMirrorContext>(MirrorMethodSpec.Hook(
            nameof(AbstractModel.AfterCreatureAddedToCombat), [typeof(Creature)]));
        registry.Register<Murderous>(static (_, c) => ApplyEnemy<StrengthPower>(c, 3));
        registry.Register<CeremonialBeastDominion>(static (_, c) => ApplyEnemy<RitualPower>(c, 1));
        registry.Register<TestSubjectDominion>(static (_, c) => ApplyEnemy<StrengthPower>(c, 3));
        registry.Register<KaiserCrabDominion>(static (_, c) => ApplyEnemy<PlatingPower>(c, 7));
        registry.Register<VantomDominion>(static (_, c) => ApplyEnemy<SlipperyPower>(c, 1));
        registry.Register<WaterfallGiantDominion>(static (_, c) =>
        {
            if (c.Creature.Side != CombatSide.Enemy || c.Branch.GetAmount<MinionPower>(c.Creature) > 0) return;
            var encounter = c.Branch.Encounter ?? throw new InvalidOperationException("Explosive dominion requires an encounter.");
            int amount = encounter.MonstersWithSlots.Count > 1 ? 3 : encounter.IsWeak ? 4 : encounter.RoomType == RoomType.Elite ? 7 : 5;
            c.Branch.Apply<ExplosivePower>(c.Creature, amount);
        });
        registry.RegisterIgnored<AeonglassDominion>();
        registry.RegisterIgnored<InsatiableDominion>();
        registry.RegisterIgnored<KinDominion>();
        registry.RegisterIgnored<KnowledgeDemonDominion>();
        registry.RegisterIgnored<LagavulinMatriarchDominion>();
        registry.RegisterIgnored<QueenDominion>();
        registry.RegisterIgnored<SoulFyshDominion>();
        registry.RegisterIgnored<SandpitPower>(); // Native callback updates visual positions.
        registry.Register<PhilosophersStone>(static (relic, c) =>
        {
            if (!relic.IsMelted && c.Creature.Side != relic.Owner.Creature.Side)
                c.Branch.Apply<StrengthPower>(c.Creature, relic.DynamicVars["StrengthPower"].IntValue);
        });
        registry.Register<FurCoat>(static (relic, c) =>
        {
            if (!relic.IsMelted && c.Creature.Side == CombatSide.Enemy
                && c.Branch.CurrentMapCoord is { } coord && relic.GetMarkedCoords()?.Contains(coord) == true)
                c.State.GetCreature(c.Creature).CurrentHp = 1;
        });
        return registry;
    }
    private static void ApplyEnemy<T>(AfterCreatureAddedMirrorContext context, int amount) where T : PowerModel
    {
        if (context.Creature.Side == CombatSide.Enemy) context.Branch.Apply<T>(context.Creature, amount);
    }
}

internal sealed class AfterCreatureAddedMirrorContext : CombatMirrorContext
{
    public required Creature Creature { get; init; }
    internal SimulatedCombatState Branch => (SimulatedCombatState)CombatState;
}
