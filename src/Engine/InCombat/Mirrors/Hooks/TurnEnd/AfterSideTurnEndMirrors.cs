using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

using Registry = MethodMirrorRegistry<AbstractModel, AfterSideTurnEndMirrorContext>;

/// <summary>
/// Registration entry for the regular <see cref="AbstractModel.AfterSideTurnEnd" /> pass.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AfterSideTurnEndLateMirrors" /> already lets adapters register the late pass, but the regular pass
/// had no entry at all: <c>EndTurnPowerSupport.TriggerRegular</c> dispatches the audited vanilla powers through a
/// type switch and everything else falls through silently. A third-party power whose end-of-turn effect lives here
/// (for example a character mod that damages all enemies at end of turn) therefore produced no prediction at all,
/// without a risk marker — the solver simply behaved as if the power did nothing.
/// </para>
/// <para>
/// This registry closes that gap. Dispatch stays in native listener order: the regular pass consults each third-party
/// power in the same loop, right where the type switch would have handled a vanilla one. A type without a registration
/// keeps the previous behaviour (no effect) so this entry cannot change how already-supported content resolves.
/// </para>
/// </remarks>
internal static class AfterSideTurnEndMirrors
{
    private static readonly MirrorMethodSpec AfterSideTurnEnd = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterSideTurnEnd),
        [
            typeof(PlayerChoiceContext),
            typeof(CombatSide),
            typeof(IEnumerable<Creature>)
        ]);

    private static readonly Registry Registry = new(AfterSideTurnEnd);
    private static readonly object RegistrationLock = new();
    private static bool _sealed;

    /// <summary>Registers the regular <see cref="AbstractModel.AfterSideTurnEnd" /> for one runtime type.</summary>
    public static void Register<TModel>(Action<TModel, AfterSideTurnEndMirrorContext> handler)
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

    /// <summary>
    /// Registers the regular pass for one runtime type addressed by <see cref="Type" />.
    /// </summary>
    /// <remarks>
    /// The entry point for external adapters, which resolve the other mod's types at runtime and therefore cannot
    /// supply a generic type argument (see <see cref="ThirdPartyMirrorRegistration" />).
    /// </remarks>
    public static void Register(Type modelType, Action<AbstractModel, AfterSideTurnEndMirrorContext> handler)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        if (modelType.IsAbstract)
            throw new ArgumentException("Turn-phase mirrors require a concrete runtime model type.", nameof(modelType));
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            ThirdPartyMirrorRegistration.Register(Registry, modelType, handler);
        }
    }

    /// <summary>
    /// Declares that one runtime type's override of the regular pass has no prediction-relevant behavior.
    /// </summary>
    /// <remarks>
    /// For reviewed overrides that only play sounds, text or visuals. The type must really override this phase; the
    /// underlying registry enforces that.
    /// </remarks>
    public static void RegisterIgnored(Type modelType)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Turn-phase mirrors must be registered before root capture or dispatch.");
            Registry.RegisterIgnored(modelType);
        }
    }

    internal static void Seal()
    {
        if (Volatile.Read(ref _sealed))
            return;
        lock (RegistrationLock)
            Volatile.Write(ref _sealed, true);
    }

    /// <summary>
    /// Dispatches one third-party listener on the regular pass, with the same explicit-failure rule as the late pass.
    /// </summary>
    /// <returns>
    /// <see langword="true" /> when this listener was accounted for here (a registered handler ran, or an ignored
    /// declaration matched), so the caller must not run the vanilla type switch for it. <see langword="false" /> when the
    /// type neither registered nor declares a third-party override of this phase: the caller keeps its previous
    /// behaviour, which is what lets a third-party subclass of an audited vanilla power still reach the switch.
    /// </returns>
    /// <exception cref="PredictionUnsupportedException">
    /// The type overrides this phase from a third-party assembly but has neither a registration nor an ignored
    /// declaration — the same outcome the late pass produces, so an unmodelled end-of-turn effect is never silent.
    /// </exception>
    internal static bool TryDispatch(
        CombatPredictionSimulator simulator,
        AbstractModel listener,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        Seal();
        Type type = listener.GetType();
        // Nothing to dispatch and no third-party override: keep the caller's original path. This gate is also what
        // keeps the hot listener loop allocation-free for content that does not touch this phase.
        if (!Registry.HasRegisteredHandler(listener) && !OverridesFromThirdParty(type))
            return false;

        var context = new AfterSideTurnEndMirrorContext
        {
            Simulator = simulator,
            Side = side,
            Participants = participants
        };
        if (Registry.Invoke(listener, context).Kind == MirrorDispatchKind.Unsupported)
            throw PredictionUnsupportedException.ForContent(
                $"No AfterSideTurnEnd mirror is registered for {type.FullName}.", type);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="type" /> overrides the regular pass with a method declared outside the game assembly.
    /// </summary>
    /// <remarks>
    /// The distinction matters at the call site: an override inherited from an audited vanilla power keeps the
    /// audited switch behaviour, while an override the mod wrote itself gets the explicit-failure rule. Deciding it
    /// from the declaring type rather than from the concrete type is what keeps third-party subclasses of vanilla
    /// powers predicting exactly as they do today.
    /// </remarks>
    private static bool OverridesFromThirdParty(Type type)
    {
        MethodInfo? method = type.GetMethod(
            nameof(AbstractModel.AfterSideTurnEnd),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)],
            modifiers: null);
        Type? declaring = method?.DeclaringType;
        return declaring is not null && !IsBaseGameType(declaring);
    }

    /// <summary>
    /// Whether one listener belongs to a loaded mod rather than the base game, using the engine's own lookup.
    /// </summary>
    /// <remarks>
    /// The caller filters on this before touching the registry so base-game powers (the large majority of listeners in
    /// a normal fight) never pay a lookup here.
    /// </remarks>
    internal static bool IsThirdParty(AbstractModel listener) => !IsBaseGameType(listener.GetType());

    private static bool IsBaseGameType(Type type)
    {
        AssemblyInfo.ModForType(type, out bool isBaseGame);
        return isBaseGame;
    }
}

/// <summary>Context for one listener of the regular <see cref="AbstractModel.AfterSideTurnEnd" /> pass.</summary>
internal sealed class AfterSideTurnEndMirrorContext : CombatMirrorContext
{
    public required CombatSide Side { get; init; }

    public required IReadOnlyList<Creature> Participants { get; init; }
}
