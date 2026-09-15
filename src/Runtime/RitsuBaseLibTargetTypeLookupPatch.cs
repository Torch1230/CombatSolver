using System.Reflection;
using System.Runtime.CompilerServices;
using STS2RitsuLib.Models.Capabilities;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

// Cache immutable assembly metadata, not framework presence or target predicates. In
// particular, new assemblies still enter Ritsu's original ordered enumeration, and
// dynamic assemblies must remain queryable after a new type has been emitted.
internal sealed class RitsuBaseLibTargetTypeLookupPatch : IPatchMethod
{
    internal const string MarkerTypeName = "BaseLib.Patches.Features.CustomTargetType";
    private const string BridgeTypeName =
        "STS2RitsuLib.Combat.CardTargeting.BaseLibTargetTypeBridge";
    private const string ResolverName = "ResolveBaseLibCustomTargetType";
    private const string AssemblyResolverName = "ResolveAssemblyType";
    private sealed record Resolution(Type? Type);
    private static readonly ConditionalWeakTable<Assembly, Resolution> Resolutions = new();

    public static string PatchId => "combat_solver_ritsu_baselib_target_type_lookup_cache";
    public static string Description => "模拟期间复用静态程序集的 BaseLib 目标类型查询结果";

    /// <remarks>
    /// <para><b>先认真方法，认不出再退回闭包。</b>这里要的是那个「Assembly → Type」查询回调。
    /// <c>ResolveAssemblyType</c> 是桥接类里的真方法，签名正好，新旧两版 RitsuLib 都有，
    /// 而且它比闭包盖得更全——查询语法之外还有直接调用它的地方。</para>
    ///
    /// <para><b>为什么不能只盯闭包。</b>RitsuLib 0.5.x 里那一段是 LINQ 查询语法
    /// （<c>from a in … select ResolveAssemblyType(a)</c>），编译器会生成一个
    /// <c>&lt;ResolveBaseLibCustomTargetType&gt;b__14_0</c>；0.6.0 改成方法组转换
    /// （<c>.Select(ResolveAssemblyType)</c>），语义一个字没变，闭包却没了——整个桥接类
    /// 反编译出来就差这两行。原来的实现在闭包数不等于 1 时抛 <c>MissingMethodException</c>，
    /// 于是 <c>Entry.Initialize()</c> 整个挂掉，求解器对所有装了 RitsuLib 0.6.0 的人都起不来。</para>
    ///
    /// <para><b>而且认不出也不该抛。</b>这一处是一层缓存，不是正确性依赖——不打它只是模拟期间
    /// 多查几次类型。把整个 mod 拖下水不成比例，所以两条路都认不出时记一条警告、返回空表。</para>
    /// </remarks>
    public static ModPatchTarget[] GetTargets()
    {
        Type bridge = typeof(ModelCapabilities).Assembly.GetType(BridgeTypeName)
            ?? throw new TypeLoadException(BridgeTypeName);
        MethodInfo? resolver = bridge.GetMethod(
            AssemblyResolverName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
            binder: null,
            [typeof(Assembly)],
            modifiers: null);
        if (resolver != null && resolver.ReturnType == typeof(Type))
            return [new(resolver.DeclaringType!, resolver.Name, [typeof(Assembly)])];

        MethodInfo[] callbacks = bridge.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.StartsWith($"<{ResolverName}>", StringComparison.Ordinal)
                && method.ReturnType == typeof(Type)
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(Assembly))
            .ToArray();
        if (callbacks.Length == 1)
            return [new(callbacks[0].DeclaringType!, callbacks[0].Name, [typeof(Assembly)])];

        Entry.Logger.Warn(
            $"[CombatSolver] 认不出 {BridgeTypeName} 的程序集类型查询回调"
            + $"（没有 {AssemblyResolverName}，{ResolverName} 的生成闭包找到 {callbacks.Length} 个），"
            + "这一层缓存不打了；求解器其余功能不受影响。");
        return [];
    }

    public static bool Prefix(Assembly __0, ref Type? __result)
    {
        if (!SimulationNotificationIsolation.IsActive || __0.IsDynamic)
            return true;
        __result = Resolutions.GetValue(__0, static assembly =>
            new Resolution(assembly.GetType(MarkerTypeName, throwOnError: false))).Type;
        return false;
    }
}
