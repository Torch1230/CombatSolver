using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver.Engine.Common.Mirrors;

/// <summary>
/// Describes the exact-runtime-type policy selected for one mirrored virtual method.
/// </summary>
internal enum MirrorDispatchKind
{
    /// <summary>The runtime type inherits the base implementation and needs no mirror handler.</summary>
    NotOverridden,

    /// <summary>The runtime type has a registered prediction handler.</summary>
    Handled,

    /// <summary>The runtime type has an unregistered override with a best-effort inferred prediction handler.</summary>
    Inferred,

    /// <summary>The override was reviewed and intentionally has no prediction-relevant behavior.</summary>
    Ignored,

    /// <summary>The override is gameplay-relevant but has no safe prediction handler.</summary>
    Unsupported
}

internal readonly record struct MirrorDispatchResult(MirrorDispatchKind Kind);

internal readonly record struct MirrorDispatchResult<TResult>(MirrorDispatchKind Kind, TResult Value);

/// <summary>
/// Analyzes one unregistered virtual action override and, when structurally supported, creates its inferred handler.
/// </summary>
/// <remarks>
/// Inference is performed once per exact runtime type and must depend only on type-level method structure. The returned
/// handler may resolve instance-dependent values or skip inapplicable candidates, but it must not capture a receiver or
/// context instance. The registry owns incomplete-risk recording for inferred invocations.
/// </remarks>
internal delegate Action<TBase, TContext>? MethodMirrorInferrer<in TBase, in TContext>(Type runtimeType, MethodInfo overrideMethod)
    where TBase : class
    where TContext : IMethodMirrorContext<TBase>;

/// <summary>
/// Dispatches one mirrored virtual action method against the exact runtime type of one receiver.
/// </summary>
/// <remarks>
/// Invocation-wide behavior such as listener enumeration and hook ordering belongs to the facade layered over this
/// registry. All registrations must finish before the first query or invocation because lookup results are cached.
/// </remarks>
internal sealed class MethodMirrorRegistry<TBase, TContext>(MirrorMethodSpec method)
    : IMethodMirrorRegistryDescriptorProvider
    where TBase : class
    where TContext : IMethodMirrorContext<TBase>
{
    // All registrations must be completed before the first invocation. Registries are built during
    // static initialization and do not support runtime registration.
    private readonly Dictionary<Type, LookupResult> _registrations = [];
    private readonly ConcurrentDictionary<Type, Lazy<LookupResult>> _lookupCache = new();
    private readonly object _lookupResolutionGate = new();
    // 热路径上每个 listener × 每个 hook 都要 Lookup 一次。已解析结果放进一张写时复制的普通字典，
    // 读取无锁且只查一次哈希；ConcurrentDictionary + Lazy 只留给首次解析。
    private Dictionary<Type, LookupResult> _resolvedSnapshot = [];

    private MethodMirrorInferrer<TBase, TContext>? _inferrer;
    private MethodMirrorInferrer<TBase, TContext>? _strictInferrer;
    private bool _allowInference = true;

    /// <summary>
    /// Gets or sets whether unregistered gameplay overrides may use the registered Type-level inferrer.
    /// </summary>
    /// <remarks>
    /// Changing this policy clears only resolved Type lookups. Explicit handled and ignored registrations remain
    /// intact, and the registered inferrer remains available if inference is enabled again.
    /// </remarks>
    public bool AllowInference
    {
        get => _allowInference;
        set
        {
            if (_allowInference != value)
            {
                _allowInference = value;
                _lookupCache.Clear();
                lock (_lookupResolutionGate)
                    Volatile.Write(ref _resolvedSnapshot, []);
            }
        }
    }

    public void Register<TModel>(Action<TModel, TContext> handler)
        where TModel : TBase
    {
        var type = typeof(TModel);
        ValidateOverride(type);
        // Exact type matching is intentional: derived models must be reviewed independently.
        _registrations.Add(type, new LookupResult(
            MirrorDispatchKind.Handled,
            (receiver, context) => handler((TModel)receiver, context)));
    }

    public void RegisterIgnored<TModel>()
        where TModel : TBase
    {
        var type = typeof(TModel);
        ValidateOverride(type);
        _registrations.Add(type, new LookupResult(MirrorDispatchKind.Ignored, null));
    }

    /// <summary>
    /// Registers the single type-level fallback used to infer unregistered, gameplay-relevant overrides.
    /// </summary>
    public void RegisterInferrer(MethodMirrorInferrer<TBase, TContext> inferrer)
    {
        ArgumentNullException.ThrowIfNull(inferrer);

        _inferrer = _inferrer is not null
            ? throw new InvalidOperationException($"Mirror for {method.Name} already has a registered inferrer.")
            : inferrer;
    }

    /// <summary>
    /// Registers a type-level analyzer whose handler is treated as exact. The analyzer must return
    /// a handler only after proving that the complete override is represented.
    /// </summary>
    public void RegisterStrictInferrer(MethodMirrorInferrer<TBase, TContext> inferrer)
    {
        ArgumentNullException.ThrowIfNull(inferrer);
        _strictInferrer = _strictInferrer is not null
            ? throw new InvalidOperationException($"Mirror for {method.Name} already has a strict inferrer.")
            : inferrer;
    }

    public MethodMirrorRegistryDescriptor DescribeMirrorSupport()
    {
        MethodMirrorRegistrationDescriptor[] registrations = _registrations
            .Select(pair => new MethodMirrorRegistrationDescriptor(
                pair.Key,
                pair.Value.Kind switch
                {
                    MirrorDispatchKind.Handled => MethodMirrorRegistrationKind.Handled,
                    MirrorDispatchKind.Ignored => MethodMirrorRegistrationKind.Ignored,
                    _ => throw new InvalidOperationException(
                        $"Explicit registration for {pair.Key.FullName} has invalid kind {pair.Value.Kind}."),
                }))
            .ToArray();
        return new MethodMirrorRegistryDescriptor(
            typeof(TBase),
            method.BaseMethod,
            registrations,
            _strictInferrer,
            _allowInference ? _inferrer : null);
    }

    /// <summary>
    /// Returns whether the exact runtime type has an explicitly registered handler without resolving or caching a
    /// fallback lookup.
    /// </summary>
    public bool HasRegisteredHandler(TBase receiver)
    {
        return _registrations.TryGetValue(receiver.GetType(), out var registration) &&
            registration.Kind is MirrorDispatchKind.Handled;
    }

    /// <summary>
    /// Invokes only an explicit exact-type registration, without resolving inference or unsupported fallbacks.
    /// </summary>
    /// <remarks>
    /// Selective hook mirrors use this to replace the few listeners that need prediction state while allowing their
    /// facade to call every other listener's original read-only implementation without recording mirror risk.
    /// </remarks>
    public bool TryInvokeRegistered(
        TBase receiver,
        TContext context,
        out MirrorDispatchResult dispatchResult)
    {
        if (!_registrations.TryGetValue(receiver.GetType(), out var registration))
        {
            dispatchResult = default;
            return false;
        }

        if (registration.Kind == MirrorDispatchKind.Handled)
        {
            using (context.PushDispatchSource(receiver, method))
            {
                registration.Handler!(receiver, context);
            }
        }

        dispatchResult = new MirrorDispatchResult(registration.Kind);
        return true;
    }

    public MirrorDispatchResult Invoke(TBase receiver, TContext context)
    {
        var (kind, handler) = Lookup(receiver.GetType());
        if (kind is MirrorDispatchKind.NotOverridden or MirrorDispatchKind.Ignored)
        {
            return new MirrorDispatchResult(kind);
        }

        using (context.PushDispatchSource(receiver, method))
        {
            switch (kind)
            {
                case MirrorDispatchKind.Handled:
                    handler!(receiver, context);
                    break;

                case MirrorDispatchKind.Inferred:
                    // Inferred mirrors are always incomplete by definition. Record the risk before invoking the handler.
                    context.RecordMethodMirrorIncompleteRisk();
                    handler!(receiver, context);
                    break;

                case MirrorDispatchKind.Unsupported:
                    context.RecordMethodNotMirroredRisk();
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected mirror dispatch kind {kind}.");
            }

            return new MirrorDispatchResult(kind);
        }
    }

    private LookupResult Lookup(Type type)
    {
        if (Volatile.Read(ref _resolvedSnapshot).TryGetValue(type, out var cached))
            return cached;
        if (!_registrations.TryGetValue(type, out var result))
        {
            result = _lookupCache.GetOrAdd(
                type,
                static (runtimeType, registry) => new Lazy<LookupResult>(
                    () => registry.ResolveLookup(runtimeType),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                this).Value;
        }
        PublishResolvedLookup(type, result);
        return result;
    }

    private void PublishResolvedLookup(Type type, LookupResult result)
    {
        lock (_lookupResolutionGate)
        {
            Dictionary<Type, LookupResult> current = _resolvedSnapshot;
            if (current.ContainsKey(type))
                return;
            Dictionary<Type, LookupResult> next = new(current) { [type] = result };
            Volatile.Write(ref _resolvedSnapshot, next);
        }
    }

    private LookupResult ResolveLookup(Type type)
    {
        lock (_lookupResolutionGate)
            return ResolveLookupUnderLock(type);
    }

    private LookupResult ResolveLookupUnderLock(Type type)
    {
        LookupResult result;
        if (!method.TryGetOverride(type, out var overrideMethod))
        {
            result = new LookupResult(MirrorDispatchKind.NotOverridden, null);
        }
        else if (TryGetMod(overrideMethod, out var mod) && mod.manifest?.affectsGameplay is false)
        {
            Entry.Logger.Info(
                $"Mirror for {method.Name} ignored unsupported {type.FullName} from non-gameplay mod {mod.manifest?.id}.");
            result = new LookupResult(MirrorDispatchKind.Ignored, null);
        }
        else if (_strictInferrer?.Invoke(type, overrideMethod) is { } exactHandler)
        {
            result = new LookupResult(MirrorDispatchKind.Handled, exactHandler);
        }
        else if (_allowInference && _inferrer?.Invoke(type, overrideMethod) is { } inferredHandler)
        {
            Entry.Logger.Info(
                $"Mirror for {method.Name} will best-effort infer behavior for unregistered {type.FullName}.");
            result = new LookupResult(MirrorDispatchKind.Inferred, inferredHandler);
        }
        else
        {
            Entry.Logger.Info(
                $"No mirror is registered for {method.Name} on {type.FullName}; preview results may be incomplete.");
            result = new LookupResult(MirrorDispatchKind.Unsupported, null);
        }

        return result;
    }

    private static bool TryGetMod(MethodInfo overrideMethod, [NotNullWhen(true)] out Mod? mod)
    {
        var declaringType = overrideMethod.DeclaringType;
        if (declaringType is null)
        {
            mod = null;
            return false;
        }

        // StS2 v0.109.0 centralizes base-game, mod, and test mock type lookup here.
        mod = AssemblyInfo.ModForType(declaringType, out var isBaseGame);
        return !isBaseGame && mod is not null;
    }

    private void ValidateOverride(Type type)
    {
        if (!method.TryGetOverride(type, out _))
        {
            throw new InvalidOperationException(
                $"{type.FullName} does not override {method.BaseMethod.DeclaringType?.FullName}.{method.Name}.");
        }
    }

    private readonly record struct LookupResult(
        MirrorDispatchKind Kind,
        Action<TBase, TContext>? Handler);
}

/// <summary>
/// Dispatches one mirrored virtual result method and falls back to a caller-provided value when no result is available.
/// </summary>
/// <remarks>
/// This result-producing counterpart is used by methods that return a value. Registrations must finish
/// before the first invocation because exact-type lookup results are cached.
/// </remarks>
internal sealed class MethodMirrorRegistry<TBase, TContext, TResult>(MirrorMethodSpec method)
    : IMethodMirrorRegistryDescriptorProvider
    where TBase : class
    where TContext : IMethodMirrorContext<TBase>
{
    // All registrations must be completed before the first invocation. Registries are built during
    // static initialization and do not support runtime registration.
    private readonly Dictionary<Type, LookupResult> _registrations = [];
    private readonly ConcurrentDictionary<Type, Lazy<LookupResult>> _lookupCache = new();
    private readonly object _lookupResolutionGate = new();
    // 热路径上每个 listener × 每个 hook 都要 Lookup 一次。已解析结果放进一张写时复制的普通字典，
    // 读取无锁且只查一次哈希；ConcurrentDictionary + Lazy 只留给首次解析。
    private Dictionary<Type, LookupResult> _resolvedSnapshot = [];

    public void Register<TModel>(Func<TModel, TContext, TResult> handler)
        where TModel : TBase
    {
        var type = typeof(TModel);
        ValidateOverride(type);
        _registrations.Add(type, new LookupResult(
            MirrorDispatchKind.Handled,
            (receiver, context) => handler((TModel)receiver, context)));
    }

    public MethodMirrorRegistryDescriptor DescribeMirrorSupport()
    {
        MethodMirrorRegistrationDescriptor[] registrations = _registrations
            .Select(pair => new MethodMirrorRegistrationDescriptor(
                pair.Key,
                pair.Value.Kind switch
                {
                    MirrorDispatchKind.Handled => MethodMirrorRegistrationKind.Handled,
                    MirrorDispatchKind.Ignored => MethodMirrorRegistrationKind.Ignored,
                    _ => throw new InvalidOperationException(
                        $"Explicit registration for {pair.Key.FullName} has invalid kind {pair.Value.Kind}."),
                }))
            .ToArray();
        return new MethodMirrorRegistryDescriptor(
            typeof(TBase),
            method.BaseMethod,
            registrations,
            StrictInferrer: null,
            Inferrer: null);
    }

    /// <summary>
    /// Invokes only an explicit exact-type registration, without resolving unsupported fallbacks.
    /// </summary>
    /// <remarks>
    /// The caller owns the strongly typed original-method fallback when this returns <see langword="false"/>.
    /// No unsupported lookup is cached and no prediction risk is recorded.
    /// </remarks>
    public bool TryInvokeRegistered(
        TBase receiver,
        TContext context,
        out MirrorDispatchResult<TResult> dispatchResult)
    {
        if (!_registrations.TryGetValue(receiver.GetType(), out var registration))
        {
            dispatchResult = default;
            return false;
        }

        using (context.PushDispatchSource(receiver, method))
        {
            dispatchResult = new MirrorDispatchResult<TResult>(
                registration.Kind,
                registration.Handler!(receiver, context));
        }

        return true;
    }

    public MirrorDispatchResult<TResult> Invoke(
        TBase receiver,
        TContext context,
        TResult defaultResult)
    {
        var (kind, handler) = Lookup(receiver.GetType());
        if (kind is MirrorDispatchKind.NotOverridden or MirrorDispatchKind.Ignored)
        {
            return new MirrorDispatchResult<TResult>(kind, defaultResult);
        }

        using (context.PushDispatchSource(receiver, method))
        {
            if (kind is MirrorDispatchKind.Handled)
            {
                return new MirrorDispatchResult<TResult>(kind, handler!(receiver, context));
            }

            context.RecordMethodNotMirroredRisk();
            return new MirrorDispatchResult<TResult>(kind, defaultResult);
        }
    }

    private LookupResult Lookup(Type type)
    {
        if (Volatile.Read(ref _resolvedSnapshot).TryGetValue(type, out var cached))
            return cached;
        if (!_registrations.TryGetValue(type, out var result))
        {
            result = _lookupCache.GetOrAdd(
                type,
                static (runtimeType, registry) => new Lazy<LookupResult>(
                    () => registry.ResolveLookup(runtimeType),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                this).Value;
        }
        PublishResolvedLookup(type, result);
        return result;
    }

    private void PublishResolvedLookup(Type type, LookupResult result)
    {
        lock (_lookupResolutionGate)
        {
            Dictionary<Type, LookupResult> current = _resolvedSnapshot;
            if (current.ContainsKey(type))
                return;
            Dictionary<Type, LookupResult> next = new(current) { [type] = result };
            Volatile.Write(ref _resolvedSnapshot, next);
        }
    }

    private LookupResult ResolveLookup(Type type)
    {
        lock (_lookupResolutionGate)
            return ResolveLookupUnderLock(type);
    }

    private LookupResult ResolveLookupUnderLock(Type type)
    {
        LookupResult result;
        if (!method.TryGetOverride(type, out _))
        {
            result = new LookupResult(MirrorDispatchKind.NotOverridden, null);
        }
        else
        {
            Entry.Logger.Info(
                $"No mirror is registered for {method.Name} on {type.FullName}; preview results may be incomplete.");
            result = new LookupResult(MirrorDispatchKind.Unsupported, null);
        }

        return result;
    }

    private void ValidateOverride(Type type)
    {
        if (!method.TryGetOverride(type, out _))
        {
            throw new InvalidOperationException(
                $"{type.FullName} does not override {method.BaseMethod.DeclaringType?.FullName}.{method.Name}.");
        }
    }

    private readonly record struct LookupResult(
        MirrorDispatchKind Kind,
        Func<TBase, TContext, TResult>? Handler);
}
