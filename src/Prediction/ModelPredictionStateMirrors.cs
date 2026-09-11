using System.Text;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Exact-type state adapters for relics and combat modifiers. Registration does not grant
/// hook coverage or bypass gameplay-mod audits. Model instances remain read-only identities.
/// </summary>
internal static class ModelPredictionStateMirrors
{
    private interface IRegistration
    {
        string Schema { get; }
        string TypeIdentity { get; }
        void Capture(CombatPredictionSimulator simulator, AbstractModel clone, AbstractModel live);
        void WriteLive(AbstractModel live, ref ModelPredictionStateWriter writer);
        void WritePredicted(CombatPredictionSimulator simulator, AbstractModel clone,
            ref ModelPredictionStateWriter writer);
    }

    private sealed class Registration<TModel, TState>(
        string schema,
        Func<CombatPredictionSimulator, TModel, TState> capture,
        ModelPredictionStateWrite<TModel> writeLive,
        ModelPredictionStateWrite<TState> writePredicted) : IRegistration
        where TModel : AbstractModel
        where TState : class, IPredictionStateForkable
    {
        public string Schema => schema;
        public string TypeIdentity { get; } = typeof(TModel).AssemblyQualifiedName
            ?? throw new InvalidOperationException("Adapter model type has no stable name.");

        public void Capture(CombatPredictionSimulator simulator, AbstractModel clone, AbstractModel live)
        {
            if (simulator.StateStore.TryGetReadOnly<CapturedState<TState>>(clone, out _))
                throw new InvalidOperationException($"Adapter state already captured for {clone.GetType().FullName}.");
            TState state = capture(simulator, (TModel)live)
                ?? throw new InvalidOperationException($"Adapter {schema} captured null state.");
            if (ReferenceEquals(state, live) || ReferenceEquals(state, clone))
                throw new InvalidOperationException($"Adapter {schema} must capture detached state.");
            simulator.StateStore.Get(clone, () => new CapturedState<TState>(state));
        }

        public void WriteLive(AbstractModel live, ref ModelPredictionStateWriter writer)
            => writeLive((TModel)live, ref writer);

        public void WritePredicted(CombatPredictionSimulator simulator, AbstractModel clone,
            ref ModelPredictionStateWriter writer)
            => writePredicted(Get<TState>(simulator, clone), ref writer);
    }

    private sealed class CapturedState<TState>(TState value) : IPredictionStateForkable, IPredictionForkBoundary
        where TState : class, IPredictionStateForkable
    {
        public TState Value => value;

        public void AssertForkable()
        {
            if (value is IPredictionForkBoundary boundary)
                boundary.AssertForkable();
        }

        public object Fork(PredictionForkContext context)
        {
            object? copy = context.TryRemap(value, out TState? existing) ? existing : value.Fork(context);
            if (copy is not TState typed || copy.GetType() != value.GetType() || ReferenceEquals(value, copy))
                throw new InvalidOperationException($"Adapter state {typeof(TState).FullName} must fork to a detached state of the same type.");
            context.Register(value, typed);
            return new CapturedState<TState>(typed);
        }
    }

    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<Type, IRegistration> Registry = [];
    private static bool _sealed;

    internal static bool HasAny
    {
        get
        {
            Seal();
            return Registry.Count != 0;
        }
    }

    public static void RegisterRelic<TRelic, TState>(string schema,
        Func<CombatPredictionSimulator, TRelic, TState> capture,
        ModelPredictionStateWrite<TRelic> writeLive,
        ModelPredictionStateWrite<TState> writePredicted)
        where TRelic : RelicModel
        where TState : class, IPredictionStateForkable
        => Register(schema, capture, writeLive, writePredicted);

    public static void RegisterModifier<TModifier, TState>(string schema,
        Func<CombatPredictionSimulator, TModifier, TState> capture,
        ModelPredictionStateWrite<TModifier> writeLive,
        ModelPredictionStateWrite<TState> writePredicted)
        where TModifier : ModifierModel
        where TState : class, IPredictionStateForkable
        => Register(schema, capture, writeLive, writePredicted);

    private static void Register<TModel, TState>(string schema,
        Func<CombatPredictionSimulator, TModel, TState> capture,
        ModelPredictionStateWrite<TModel> writeLive,
        ModelPredictionStateWrite<TState> writePredicted)
        where TModel : AbstractModel
        where TState : class, IPredictionStateForkable
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(writeLive);
        ArgumentNullException.ThrowIfNull(writePredicted);
        if (typeof(TModel).IsAbstract)
            throw new ArgumentException("State adapters require a concrete runtime model type.");
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("Model state registration is closed after the first prediction root or continuation capture.");
            Registry.Add(typeof(TModel), new Registration<TModel, TState>(schema, capture, writeLive, writePredicted));
        }
    }

    internal static void Seal()
    {
        if (Volatile.Read(ref _sealed))
            return;
        lock (RegistrationLock)
            Volatile.Write(ref _sealed, true);
    }

    internal static void CaptureRootState(CombatPredictionSimulator simulator, AbstractModel clone, AbstractModel live)
    {
        Seal();
        if (clone.GetType() != live.GetType())
            throw new InvalidOperationException("Adapter root source and clone must have the same runtime type.");
        if (Registry.TryGetValue(clone.GetType(), out IRegistration? registration))
            registration.Capture(simulator, clone, live);
    }

    /// <summary>Reads captured branch state. Missing capture is an error, never a lazy live-state fallback.</summary>
    public static TState Get<TState>(CombatPredictionSimulator simulator, AbstractModel model)
        where TState : class, IPredictionStateForkable
        => simulator.StateStore.TryGetReadOnly<CapturedState<TState>>(model, out var state)
            ? state!.Value
            : throw new InvalidOperationException($"No captured {typeof(TState).FullName} for {model.GetType().FullName}.");

    internal static void AppendLiveContinuation(StringBuilder text, ICombatState combat)
    {
        Seal();
        if (Registry.Count == 0)
            return;
        ModelPredictionStateWriter writer = new(new StateFingerprintBuilder(), text);
        foreach (Player player in combat.Players)
        {
            int slot = 0;
            foreach (RelicModel relic in player.Relics)
                AppendModel(relic, "relic", player.NetId, slot++, null, text, ref writer);
        }
        for (int slot = 0; slot < combat.Modifiers.Count; slot++)
            AppendModel(combat.Modifiers[slot], "modifier", 0, slot, null, text, ref writer);
    }

    internal static void AppendPredicted(ref StateFingerprintBuilder fingerprint, StringBuilder? text,
        CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        Seal();
        if (Registry.Count == 0)
            return;
        ModelPredictionStateWriter writer = new(fingerprint, text);
        for (int playerIndex = 0; playerIndex < combat.Players.Count; playerIndex++)
        {
            Player player = combat.Players[playerIndex];
            IReadOnlyList<RelicModel> relics = combat.RelicsOf(player);
            for (int slot = 0; slot < relics.Count; slot++)
                AppendModel(relics[slot], "relic", player.NetId, slot, simulator, text, ref writer);
        }
        for (int slot = 0; slot < combat.Modifiers.Count; slot++)
            AppendModel(combat.Modifiers[slot], "modifier", 0, slot, simulator, text, ref writer);
        fingerprint = writer.Fingerprint;
    }

    private static void AppendModel(AbstractModel model, string kind, ulong owner, int slot,
        CombatPredictionSimulator? simulator, StringBuilder? text, ref ModelPredictionStateWriter writer)
    {
        if (!Registry.TryGetValue(model.GetType(), out IRegistration? registration))
            return;
        // Bind fields to their ordered instance, not an unordered sum of same-type values.
        text?.Append(";model_state=");
        writer.Add("kind", kind);
        writer.Add("owner", owner);
        writer.Add("slot", (long)slot);
        writer.Add("type", registration.TypeIdentity);
        writer.Add("schema", registration.Schema);
        if (simulator is null)
            registration.WriteLive(model, ref writer);
        else
            registration.WritePredicted(simulator, model, ref writer);
        writer.Add("end", true);
    }
}
