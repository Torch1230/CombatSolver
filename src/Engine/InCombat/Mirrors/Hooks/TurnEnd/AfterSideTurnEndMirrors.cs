using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

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
    /// Runs the registered handler for <paramref name="power" />, if any.
    /// </summary>
    /// <returns>
    /// <see langword="true" /> when this type has a registration (or an ignored declaration), so the caller knows the
    /// listener was accounted for; <see langword="false" /> when it has none and the caller should keep its previous
    /// behaviour.
    /// </returns>
    internal static bool TryInvoke(
        CombatPredictionSimulator simulator,
        AbstractModel power,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        var context = new AfterSideTurnEndMirrorContext
        {
            Simulator = simulator,
            Side = side,
            Participants = participants
        };
        return Registry.TryInvokeRegistered(power, context, out _);
    }
}

/// <summary>Context for one listener of the regular <see cref="AbstractModel.AfterSideTurnEnd" /> pass.</summary>
internal sealed class AfterSideTurnEndMirrorContext : CombatMirrorContext
{
    public required CombatSide Side { get; init; }

    public required IReadOnlyList<Creature> Participants { get; init; }
}
