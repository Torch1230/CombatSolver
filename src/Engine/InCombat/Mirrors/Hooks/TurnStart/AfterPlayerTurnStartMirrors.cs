using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;

using Registry = MethodMirrorRegistry<AbstractModel, AfterPlayerTurnStartMirrorContext>;

// 三个原生方法分别登记；冻结和精确类型规则与其它回合阶段表一致。
internal static partial class AfterPlayerTurnStartMirrors
{
    private static readonly Registry EarlyRegistry = CreateRegistry(nameof(AbstractModel.AfterPlayerTurnStartEarly));
    private static readonly Registry Registry = CreateRegistry(nameof(AbstractModel.AfterPlayerTurnStart));
    private static readonly Registry LateRegistry = CreateRegistry(nameof(AbstractModel.AfterPlayerTurnStartLate));
    private static readonly object RegistrationLock = new();
    private static bool _sealed;
    private static bool _hasExternalRegistrations;

    public static void RegisterEarly<TModel>(Action<TModel, AfterPlayerTurnStartMirrorContext> handler)
        where TModel : AbstractModel => Register(EarlyRegistry, handler);

    public static void Register<TModel>(Action<TModel, AfterPlayerTurnStartMirrorContext> handler)
        where TModel : AbstractModel => Register(Registry, handler);

    public static void RegisterLate<TModel>(Action<TModel, AfterPlayerTurnStartMirrorContext> handler)
        where TModel : AbstractModel => Register(LateRegistry, handler);

    /// <summary>
    /// Registers the Early phase for one runtime type.
    /// </summary>
    /// <remarks>
    /// The entry points for external adapters, which resolve the other mod's types at runtime and therefore cannot
    /// supply a generic type argument (see <see cref="ThirdPartyMirrorRegistration"/>). All three phases are hard
    /// gates: a third-party override that is neither registered nor ignored makes <see cref="Invoke"/> throw.
    /// </remarks>
    public static void RegisterEarly(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler)
        => Register(EarlyRegistry, modelType, handler);

    /// <summary>Registers the ordinary phase for one runtime type.</summary>
    public static void Register(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler)
        => Register(Registry, modelType, handler);

    /// <summary>Registers the Late phase for one runtime type.</summary>
    public static void RegisterLate(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler)
        => Register(LateRegistry, modelType, handler);

    /// <summary>
    /// Declares that one runtime type's override has no prediction-relevant behavior.
    /// </summary>
    /// <remarks>
    /// Written into whichever of the three registries the type actually overrides; a type that overrides none of them
    /// is rejected instead of silently accepted. Without it the hard gate above would reject the whole combat.
    /// </remarks>
    public static void RegisterIgnored(Type modelType)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            bool registered = false;
            foreach (Registry registry in new[] { EarlyRegistry, Registry, LateRegistry })
            {
                if (!registry.OverridesMethod(modelType))
                    continue;
                registry.RegisterIgnored(modelType);
                registered = true;
            }
            if (!registered)
            {
                throw new InvalidOperationException(
                    $"{modelType.FullName} overrides none of AfterPlayerTurnStart/Early/Late, so it cannot be ignored.");
            }
            Volatile.Write(ref _hasExternalRegistrations, true);
        }
    }

    private static void Register(
        Registry registry,
        Type modelType,
        Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        if (modelType.IsAbstract)
            throw new ArgumentException("Turn-phase mirrors require a concrete runtime model type.", nameof(modelType));
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            ThirdPartyMirrorRegistration.Register(registry, modelType, handler);
            Volatile.Write(ref _hasExternalRegistrations, true);
        }
    }

    private static void Register<TModel>(Registry registry, Action<TModel, AfterPlayerTurnStartMirrorContext> handler)
        where TModel : AbstractModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (typeof(TModel).IsAbstract)
            throw new ArgumentException("Turn-phase mirrors require a concrete runtime model type.");
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            registry.Register(handler);
            Volatile.Write(ref _hasExternalRegistrations, true);
        }
    }

    internal static bool HasExternalRegistrations => Volatile.Read(ref _hasExternalRegistrations);

    internal static void Seal()
    {
        if (Volatile.Read(ref _sealed)) return;
        lock (RegistrationLock) Volatile.Write(ref _sealed, true);
    }

    internal static bool HasOverride(AbstractModel model)
        => EarlyRegistry.ResolveDispatchKind(model) != MirrorDispatchKind.NotOverridden
            || Registry.ResolveDispatchKind(model) != MirrorDispatchKind.NotOverridden
            || LateRegistry.ResolveDispatchKind(model) != MirrorDispatchKind.NotOverridden;

    internal static void Invoke(AbstractModel listener, AfterPlayerTurnStartMirrorContext context, int phase)
    {
        Seal();
        Registry registry = phase switch { 0 => EarlyRegistry, 1 => Registry, 2 => LateRegistry,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)) };
        if (registry.Invoke(listener, context).Kind == MirrorDispatchKind.Unsupported)
            throw new NotSupportedException($"No {registry.DescribeMirrorSupport().BaseMethod.Name} mirror is registered for {listener.GetType().FullName}.");
    }

    private static partial void RegisterVanilla(Registry registry, string hook);

    private static Registry CreateRegistry(string hook)
    {
        var registry = new Registry(MirrorMethodSpec.Hook(hook, [typeof(PlayerChoiceContext), typeof(Player)]));
        RegisterVanilla(registry, hook);
        return registry;
    }
}

internal sealed class AfterPlayerTurnStartMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
    internal required TurnStartChoiceCursor Choices { get; init; }
}
