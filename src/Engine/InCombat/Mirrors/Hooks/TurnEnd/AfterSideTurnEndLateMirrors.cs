using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

using Registry = MethodMirrorRegistry<AbstractModel, AfterSideTurnEndLateMirrorContext>;

// Registration describes effects only; model state and mod-patch admission have separate contracts.
internal static class AfterSideTurnEndLateMirrors
{
    private static readonly Registry Registry = CreateRegistry();
    private static readonly object RegistrationLock = new();
    private static bool _sealed;

    public static void Register<TModel>(Action<TModel, AfterSideTurnEndLateMirrorContext> handler)
        where TModel : AbstractModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (typeof(TModel).IsAbstract)
            throw new ArgumentException("Turn-phase mirrors require a concrete runtime model type.");
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            Registry.Register(handler);
        }
    }

    internal static void Seal()
    {
        if (Volatile.Read(ref _sealed))
            return;
        lock (RegistrationLock)
            Volatile.Write(ref _sealed, true);
    }

    internal static void Invoke(AbstractModel listener, AfterSideTurnEndLateMirrorContext context)
    {
        Seal();
        if (Registry.Invoke(listener, context).Kind == MirrorDispatchKind.Unsupported)
            throw new NotSupportedException(
                $"No AfterSideTurnEndLate mirror is registered for {listener.GetType().FullName}.");
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(MirrorMethodSpec.Hook(
            nameof(AbstractModel.AfterSideTurnEndLate),
            [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]));
        registry.Register<DisintegrationPower>(static (power, context) =>
        {
            if (context.Participants.Contains(power.Owner)
                && power.Amount > 0
                && context.State.GetCreature(power.Owner).IsAlive)
            {
                using (context.Simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(DisintegrationPower))))
                {
                    context.Simulator.Damage(power.Owner, power.Amount, ValueProp.Unpowered, power.Owner);
                }
            }
        });
        return registry;
    }
}

internal sealed class AfterSideTurnEndLateMirrorContext : CombatMirrorContext
{
    public required CombatSide Side { get; init; }
    public required IReadOnlyList<Creature> Participants { get; init; }
}
