using System.Reflection;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common.Mirrors;

/// <summary>
/// Registers one mirror handler addressed by runtime <see cref="Type"/> instead of a generic type argument.
/// </summary>
/// <remarks>
/// The solver's own mirrors register through <c>Register&lt;TModel&gt;</c> because the compiler already knows the
/// type. A third-party adapter resolves the other mod's types at runtime — it deliberately does not reference that
/// assembly — so it can only hand over a <see cref="Type"/>. This helper is the thin shell that turns that
/// <see cref="Type"/> back into the generic argument of the underlying registry.
///
/// <para>Passing <c>Action&lt;AbstractModel, TContext&gt;</c> to an <c>Action&lt;TModel, TContext&gt;</c> parameter
/// would rely on delegate contravariance; that happens to work, which is exactly why it is not done here. The call
/// goes through <c>RegisterCore</c> with <c>TModel : AbstractModel</c>, so the parameter types are equal by
/// construction and no variance assumption is involved.</para>
///
/// <para>Registration stays the caller's responsibility: it must finish before any root capture or dispatch. Callers
/// that own a freeze switch reject late registrations; in the others a resolved-type lookup cache makes a late
/// registration ineffective.</para>
/// </remarks>
internal static class ThirdPartyMirrorRegistration
{
    private static readonly MethodInfo RegisterCoreMethod = typeof(ThirdPartyMirrorRegistration)
        .GetMethod(nameof(RegisterCore), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(ThirdPartyMirrorRegistration).FullName, nameof(RegisterCore));

    private static readonly MethodInfo RegisterResultCoreMethod = typeof(ThirdPartyMirrorRegistration)
        .GetMethod(nameof(RegisterResultCore), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(ThirdPartyMirrorRegistration).FullName,
            nameof(RegisterResultCore));

    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="modelType"/> in the given mirror registry.
    /// </summary>
    /// <remarks>
    /// A type that is not a concrete model of the registry's base type, or that does not override the mirrored
    /// method, is rejected by the underlying registry.
    /// </remarks>
    public static void Register<TContext>(
        MethodMirrorRegistry<AbstractModel, TContext> registry,
        Type modelType,
        Action<AbstractModel, TContext> handler)
        where TContext : IMethodMirrorContext<AbstractModel>
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        InvokeCore(
            RegisterCoreMethod.MakeGenericMethod(modelType, typeof(TContext)),
            [registry, handler]);
    }

    /// <summary>
    /// Result-returning variant; <paramref name="modelType"/> must override the result-returning native method.
    /// </summary>
    public static void RegisterResult<TContext, TResult>(
        MethodMirrorRegistry<AbstractModel, TContext, TResult> registry,
        Type modelType,
        Func<AbstractModel, TContext, TResult> handler)
        where TContext : IMethodMirrorContext<AbstractModel>
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        InvokeCore(
            RegisterResultCoreMethod.MakeGenericMethod(modelType, typeof(TContext), typeof(TResult)),
            [registry, handler]);
    }

    /// <summary>
    /// Invokes the reflected registration method and surfaces its failure unchanged.
    /// </summary>
    /// <remarks>
    /// Why a registration failed — duplicate type, non-override, abstract type — is the failure semantics the caller
    /// is documented to see. Leaving it wrapped in <see cref="TargetInvocationException"/> would hide that reason
    /// from an adapter's self-check log.
    /// </remarks>
    private static void InvokeCore(MethodInfo method, object?[] arguments)
    {
        try
        {
            _ = method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void RegisterCore<TModel, TContext>(
        MethodMirrorRegistry<AbstractModel, TContext> registry,
        Action<AbstractModel, TContext> handler)
        where TModel : AbstractModel
        where TContext : IMethodMirrorContext<AbstractModel>
        => registry.Register<TModel>((model, context) => handler(model, context));

    private static void RegisterResultCore<TModel, TContext, TResult>(
        MethodMirrorRegistry<AbstractModel, TContext, TResult> registry,
        Func<AbstractModel, TContext, TResult> handler)
        where TModel : AbstractModel
        where TContext : IMethodMirrorContext<AbstractModel>
        => registry.Register<TModel>((model, context) => handler(model, context));
}