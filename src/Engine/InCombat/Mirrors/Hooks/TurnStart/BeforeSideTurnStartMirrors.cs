using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;

using Registry = MethodMirrorRegistry<AbstractModel, BeforeSideTurnStartMirrorContext>;

// 仅登记效果；模型状态捕获及第三方补丁准入仍各自遵守独立合同。
internal static partial class BeforeSideTurnStartMirrors
{
    private static readonly Registry Registry = CreateRegistry();
    private static readonly object RegistrationLock = new();
    private static bool _sealed;

    public static void Register<TModel>(Action<TModel, BeforeSideTurnStartMirrorContext> handler)
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

    internal static void Invoke(AbstractModel listener, BeforeSideTurnStartMirrorContext context)
    {
        Seal();
        if (Registry.Invoke(listener, context).Kind == MirrorDispatchKind.Unsupported)
            throw new NotSupportedException(
                $"No BeforeSideTurnStart mirror is registered for {listener.GetType().FullName}.");
    }

    private static partial void RegisterVanilla(Registry registry);

    private static Registry CreateRegistry()
    {
        var registry = new Registry(MirrorMethodSpec.Hook(
            nameof(AbstractModel.BeforeSideTurnStart),
            [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IReadOnlyList<Creature>), typeof(ICombatState)]));
        RegisterVanilla(registry);
        return registry;
    }
}

internal sealed class BeforeSideTurnStartMirrorContext : CombatMirrorContext
{
    public required CombatSide Side { get; init; }
    public required IReadOnlyList<Creature> Participants { get; init; }
}
