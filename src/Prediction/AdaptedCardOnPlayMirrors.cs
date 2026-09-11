using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed record AdaptedOnPlayPatch(HarmonyPatchType Kind, MethodInfo Method, string Owner,
    int Priority, string[] Before, string[] After);

internal sealed record AdaptedOnPlayRegistrationDescriptor(string Schema, MethodInfo Target,
    string PatchSignature, MethodMirrorRegistryDescriptor Mirror);

/// <summary>One complete replacement per exact card type and reviewed Harmony composition.</summary>
internal static class AdaptedCardOnPlayMirrors
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Registration> Registrations = [];
    private static bool _sealed;
    private static readonly MirrorMethodSpec OnPlay = new(typeof(CardModel), "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    internal sealed record Registration(MethodInfo Target, string Schema, string Signature, string Implementation,
        MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext> Mirror);

    // Conditional support is not an unconditional entry in the vanilla coverage catalog.
    // Consumers use the standard mirror descriptor plus the required composition.
    public static IReadOnlyList<AdaptedOnPlayRegistrationDescriptor> DescribeRegisteredCompositions()
    {
        lock (Gate)
            return Registrations.Values.Select(value => new AdaptedOnPlayRegistrationDescriptor(
                value.Schema, value.Target, value.Signature, value.Mirror.DescribeMirrorSupport())).ToArray();
    }

    public static void Register<TCard>(string schema, MethodInfo target,
        IReadOnlyList<AdaptedOnPlayPatch> patches, Action<TCard, CardOnPlayMirrorContext> handler)
        where TCard : CardModel
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(patches);
        if (typeof(TCard).IsAbstract || target != ResolveOnPlay(typeof(TCard)))
            throw new ArgumentException("The target must be the exact card type's OnPlay implementation.");
        if (patches.Count == 0) throw new ArgumentException("An adapted composition must contain patches.");
        string signature = DescribeExpected(patches);
        var mirror = new MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext>(OnPlay);
        mirror.Register(handler);
        lock (Gate)
        {
            if (_sealed) throw new InvalidOperationException("OnPlay adaptation registration is closed after first capture.");
            Registrations.Add(typeof(TCard), new(target, schema, signature, MethodIdentity(handler.Method), mirror));
        }
    }

    internal static bool Seal()
    {
        if (!Volatile.Read(ref _sealed))
            lock (Gate) Volatile.Write(ref _sealed, true);
        return Registrations.Count != 0;
    }

    internal static MethodInfo? ResolveOnPlay(Type type)
        => AccessTools.Method(type, "OnPlay", [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    internal static Registration? Select(Type type, MethodInfo target, Patches? patches)
    {
        if (!Registrations.TryGetValue(type, out Registration? registration)) return null;
        RejectPatchedStateMachine(target);
        string actual = DescribeActual(target, patches, includeIndex: false);
        if (actual.Length == 0) return null; // Unpatched native behavior uses the ordinary mirror.
        if (registration.Target != target || registration.Signature != actual)
            throw new PredictionUnsupportedException($"Unreviewed OnPlay patch composition for {type.FullName}.");
        return registration;
    }

    // A configuration stamp belongs to Runtime's live boundaries. Workers only use the frozen string.
    // Include all patched card OnPlay methods so later additions invalidate old plans as well.
    internal static string? CaptureLiveStamp()
    {
        if (!Seal()) return null;
        StringBuilder text = new();
        foreach (var pair in Registrations.OrderBy(pair => pair.Key.AssemblyQualifiedName, StringComparer.Ordinal))
        {
            Field(text, pair.Key.AssemblyQualifiedName!);
            Field(text, MethodIdentity(pair.Value.Target));
            Field(text, pair.Value.Schema);
            Field(text, pair.Value.Signature);
            Field(text, pair.Value.Implementation);
            RejectPatchedStateMachine(pair.Value.Target);
        }
        foreach (MethodBase method in Harmony.GetAllPatchedMethods()
            .Where(method => method.Name == "OnPlay" && method.DeclaringType is { } type && typeof(CardModel).IsAssignableFrom(type))
            .OrderBy(MethodIdentity, StringComparer.Ordinal))
        {
            string patches = DescribeActual(method, Harmony.GetPatchInfo(method), includeIndex: true);
            if (patches.Length == 0) continue;
            Field(text, MethodIdentity(method));
            Field(text, patches);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void RejectPatchedStateMachine(MethodInfo target)
    {
        Type? stateMachine = target.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (stateMachine is null) return;
        MethodInfo moveNext = AccessTools.Method(stateMachine, "MoveNext")
            ?? throw new PredictionUnsupportedException("Async OnPlay state machine has no MoveNext.");
        if (Harmony.GetPatchInfo(moveNext) is { Owners.Count: > 0 })
            throw new PredictionUnsupportedException("Patched async OnPlay MoveNext requires a separate adapter contract.");
    }

    internal static IEnumerable<(HarmonyPatchType Kind, Patch[] Patches)> Groups(Patches patches)
    {
        if (patches.InnerPrefixes.Count != 0 || patches.InnerPostfixes.Count != 0)
            throw new PredictionUnsupportedException("Inner OnPlay Harmony patches are not supported by this adapter contract.");
        yield return (HarmonyPatchType.Prefix, patches.Prefixes.ToArray());
        yield return (HarmonyPatchType.Postfix, patches.Postfixes.ToArray());
        yield return (HarmonyPatchType.Transpiler, patches.Transpilers.ToArray());
        yield return (HarmonyPatchType.Finalizer, patches.Finalizers.ToArray());
    }

    internal static string DescribeActual(MethodBase target, Patches? patches, bool includeIndex)
    {
        if (patches is null) return string.Empty;
        StringBuilder text = new();
        foreach (var group in Groups(patches))
        {
            // Harmony owns ordering. Duplicate methods and dynamic patch factories cannot be
            // unambiguously related back to one declaration, so they require a wider contract.
            Dictionary<MethodInfo, Patch> byMethod = [];
            foreach (Patch patch in group.Patches)
            {
                RejectPatchFactory(patch.PatchMethod);
                if (!byMethod.TryAdd(patch.PatchMethod, patch))
                    throw new PredictionUnsupportedException("Duplicate patch methods in one OnPlay category are unsupported.");
            }
            foreach (MethodInfo method in PatchProcessor.GetSortedPatchMethods(target, group.Patches))
            {
                if (!byMethod.TryGetValue(method, out Patch? patch))
                    throw new PredictionUnsupportedException("Dynamic OnPlay patch factories are unsupported.");
                Describe(text, new(group.Kind, method, patch.owner, patch.priority, patch.before, patch.after));
                if (includeIndex) Field(text, patch.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return text.ToString();
    }

    private static string DescribeExpected(IReadOnlyList<AdaptedOnPlayPatch> patches)
    {
        StringBuilder text = new();
        foreach (HarmonyPatchType kind in new[] { HarmonyPatchType.Prefix, HarmonyPatchType.Postfix,
            HarmonyPatchType.Transpiler, HarmonyPatchType.Finalizer })
        {
            HashSet<MethodInfo> seen = [];
            foreach (AdaptedOnPlayPatch patch in patches.Where(patch => patch.Kind == kind))
            {
                if (!seen.Add(patch.Method)) throw new ArgumentException("Duplicate patch methods are unsupported.");
                Describe(text, patch);
            }
        }
        if (patches.Any(patch => patch.Kind is not (HarmonyPatchType.Prefix or HarmonyPatchType.Postfix
            or HarmonyPatchType.Transpiler or HarmonyPatchType.Finalizer)))
            throw new ArgumentException("Unsupported patch category.");
        return text.ToString();
    }

    private static void Describe(StringBuilder text, AdaptedOnPlayPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch.Method);
        RejectPatchFactory(patch.Method);
        ArgumentException.ThrowIfNullOrWhiteSpace(patch.Owner);
        ArgumentNullException.ThrowIfNull(patch.Before);
        ArgumentNullException.ThrowIfNull(patch.After);
        Field(text, patch.Kind.ToString());
        Field(text, MethodIdentity(patch.Method));
        Field(text, patch.Owner);
        Field(text, patch.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(text, patch.Before.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string owner in patch.Before) Field(text, owner);
        Field(text, patch.After.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string owner in patch.After) Field(text, owner);
    }

    private static void RejectPatchFactory(MethodInfo method)
    {
        // Harmony's sorter calls Patch.GetMethod, which invokes static factory methods.
        // Reject before sorting so an observation cannot execute third-party factory code.
        if (method.ReturnType == typeof(MethodInfo) || method.ReturnType == typeof(DynamicMethod))
            throw new PredictionUnsupportedException("OnPlay patch factories are unsupported.");
    }

    private static string MethodIdentity(MethodBase method)
        => $"{method.Module.ModuleVersionId:N}/{method.MetadataToken}/{method.DeclaringType?.AssemblyQualifiedName}";

    private static void Field(StringBuilder text, string value)
        => text.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);
}

internal sealed class AdaptedOnPlaySnapshot(
    Dictionary<Type, AdaptedCardOnPlayMirrors.Registration?> selections, string stamp)
{
    public string Stamp { get; } = stamp;

    internal bool TryInvoke(CombatPredictionSimulator simulator, PredictedCard card, CardPlay play,
        out MirrorDispatchResult result)
    {
        result = default;
        if (!selections.TryGetValue(card.Preview.GetType(), out var registration))
            throw new PredictionUnsupportedException("Card type was not audited in this adapted OnPlay root.");
        if (registration is null) return false;
        result = registration.Mirror.Invoke(card.MutablePreview, new() { Simulator = simulator, Card = card, CardPlay = play });
        return true;
    }
}
