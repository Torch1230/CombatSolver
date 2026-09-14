using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

// Root-frozen dispatch metadata for mirrors and the native keyword no-op guard.
// Domain/native listener enumeration remains complete. External and patched hooks
// always retain the original dispatch path.
internal sealed class MirroredHookListenerFilter(bool enabled)
{
    // This filter belongs to one captured root. Only immutable runtime-type layouts
    // cross branches; receiver models remain exclusively in each returned snapshot.
    // A direct-mapped table has bounded retention and needs no worker-side lock.
    internal const int SharedLayoutSlots = 2048;
    private const int MaxSharedLayoutLength = 1024;
    private readonly MirroredHookListenerLayout?[] _sharedLayouts =
        enabled ? new MirroredHookListenerLayout?[SharedLayoutSlots] : [];
    private long _sharedHits;
    private long _sharedMisses;
    private long _sharedCollisions;
    private long _sharedBypasses;
    private long _listenerPrefixReuses;
    private long _listenerPrefixBuilds;
    private long _listenerSplitBuilds;
    private long _listenerWholeBuilds;
    private long _effectivePrefixReuses;
    private long _effectivePrefixBuilds;

    internal HookListenerSegmentStatistics ListenerSegmentStatistics => new(
        Volatile.Read(ref _listenerPrefixReuses), Volatile.Read(ref _listenerPrefixBuilds),
        Volatile.Read(ref _listenerSplitBuilds), Volatile.Read(ref _listenerWholeBuilds),
        Volatile.Read(ref _effectivePrefixReuses), Volatile.Read(ref _effectivePrefixBuilds));

    internal void RecordEffectivePrefix(bool reused)
    {
        if (reused) Interlocked.Increment(ref _effectivePrefixReuses);
        else Interlocked.Increment(ref _effectivePrefixBuilds);
    }

    internal void RecordListenerPrefix(bool reused)
    {
        if (reused) Interlocked.Increment(ref _listenerPrefixReuses);
        else Interlocked.Increment(ref _listenerPrefixBuilds);
    }

    internal void RecordListenerSegmentResult(bool split)
    {
        if (split) Interlocked.Increment(ref _listenerSplitBuilds);
        else Interlocked.Increment(ref _listenerWholeBuilds);
    }

    internal HookLayoutCacheStatistics Statistics => new(
        Volatile.Read(ref _sharedHits), Volatile.Read(ref _sharedMisses),
        Volatile.Read(ref _sharedCollisions), Volatile.Read(ref _sharedBypasses));

    private static readonly Dictionary<string, MirroredHookMask> HookNames = new()
    {
        [nameof(AbstractModel.AfterAttack)] = MirroredHookMask.AfterAttack,
        [nameof(AbstractModel.AfterAutoPostPlayPhaseEntered)] = MirroredHookMask.AfterAutoPostPlayPhaseEntered,
        [nameof(AbstractModel.AfterBlockBroken)] = MirroredHookMask.AfterBlockBroken,
        [nameof(AbstractModel.AfterBlockGained)] = MirroredHookMask.AfterBlockGained,
        [nameof(AbstractModel.AfterCardDiscarded)] = MirroredHookMask.AfterCardDiscarded,
        [nameof(AbstractModel.AfterCardDrawn)] = MirroredHookMask.AfterCardDrawn,
        [nameof(AbstractModel.AfterCardDrawnEarly)] = MirroredHookMask.AfterCardDrawnEarly,
        [nameof(AbstractModel.AfterCardExhausted)] = MirroredHookMask.AfterCardExhausted,
        [nameof(AbstractModel.AfterCardGeneratedForCombat)] = MirroredHookMask.AfterCardGeneratedForCombat,
        [nameof(AbstractModel.AfterCardPlayed)] = MirroredHookMask.AfterCardPlayed,
        [nameof(AbstractModel.AfterCardPlayedLate)] = MirroredHookMask.AfterCardPlayedLate,
        [nameof(AbstractModel.AfterCurrentHpChanged)] = MirroredHookMask.AfterCurrentHpChanged,
        [nameof(AbstractModel.AfterDamageGiven)] = MirroredHookMask.AfterDamageGiven,
        [nameof(AbstractModel.AfterDamageReceived)] = MirroredHookMask.AfterDamageReceived,
        [nameof(AbstractModel.AfterDamageReceivedLate)] = MirroredHookMask.AfterDamageReceivedLate,
        [nameof(AbstractModel.AfterDeath)] = MirroredHookMask.AfterDeath,
        [nameof(AbstractModel.AfterModifyingBlockAmount)] = MirroredHookMask.AfterModifyingBlockAmount,
        [nameof(AbstractModel.AfterModifyingCardPlayCount)] = MirroredHookMask.AfterModifyingCardPlayCount,
        [nameof(AbstractModel.AfterModifyingCardPlayResultLocation)] = MirroredHookMask.AfterModifyingCardPlayResultLocation,
        [nameof(AbstractModel.AfterModifyingHpLostAfterOsty)] = MirroredHookMask.AfterModifyingHpLostAfterOsty,
        [nameof(AbstractModel.AfterOrbChanneled)] = MirroredHookMask.AfterOrbChanneled,
        [nameof(AbstractModel.AfterOrbEvoked)] = MirroredHookMask.AfterOrbEvoked,
        [nameof(AbstractModel.AfterPreventingDeath)] = MirroredHookMask.AfterPreventingDeath,
        [nameof(AbstractModel.AfterShuffle)] = MirroredHookMask.AfterShuffle,
        [nameof(AbstractModel.AfterSideTurnEndLate)] = MirroredHookMask.AfterSideTurnEndLate,
        [nameof(AbstractModel.AfterStarsGained)] = MirroredHookMask.AfterStarsGained,
        [nameof(AbstractModel.BeforeAttack)] = MirroredHookMask.BeforeAttack,
        [nameof(AbstractModel.BeforeBlockGained)] = MirroredHookMask.BeforeBlockGained,
        [nameof(AbstractModel.BeforeCardPlayed)] = MirroredHookMask.BeforeCardPlayed,
        [nameof(AbstractModel.BeforeDamageReceived)] = MirroredHookMask.BeforeDamageReceived,
        [nameof(AbstractModel.BeforeDeath)] = MirroredHookMask.BeforeDeath,
        [nameof(AbstractModel.BeforeSideTurnEnd)] = MirroredHookMask.BeforeSideTurnEnd,
        [nameof(AbstractModel.BeforeSideTurnEndEarly)] = MirroredHookMask.BeforeSideTurnEndEarly,
        [nameof(AbstractModel.BeforeSideTurnEndVeryEarly)] = MirroredHookMask.BeforeSideTurnEndVeryEarly,
        [nameof(AbstractModel.ModifyAttackHitCount)] = MirroredHookMask.ModifyAttackHitCount,
        [nameof(AbstractModel.ModifyBlockAdditive)] = MirroredHookMask.ModifyBlockAdditive,
        [nameof(AbstractModel.ModifyBlockMultiplicative)] = MirroredHookMask.ModifyBlockMultiplicative,
        [nameof(AbstractModel.ModifyCardPlayCount)] = MirroredHookMask.ModifyCardPlayCount,
        [nameof(AbstractModel.ModifyCardPlayResultLocation)] = MirroredHookMask.ModifyCardPlayResultLocation,
        [nameof(AbstractModel.ModifyDamageAdditive)] = MirroredHookMask.ModifyDamageAdditive,
        [nameof(AbstractModel.ModifyDamageCap)] = MirroredHookMask.ModifyDamageCap,
        [nameof(AbstractModel.ModifyDamageMultiplicative)] = MirroredHookMask.ModifyDamageMultiplicative,
        [nameof(AbstractModel.ModifyHpLostAfterOsty)] = MirroredHookMask.ModifyHpLostAfterOsty,
        [nameof(AbstractModel.ModifyHpLostAfterOstyLate)] = MirroredHookMask.ModifyHpLostAfterOstyLate,
        [nameof(AbstractModel.ModifyHpLostBeforeOsty)] = MirroredHookMask.ModifyHpLostBeforeOsty,
        [nameof(AbstractModel.ModifyHpLostBeforeOstyLate)] = MirroredHookMask.ModifyHpLostBeforeOstyLate,
        [nameof(AbstractModel.ModifyOrbPassiveTriggerCounts)] = MirroredHookMask.ModifyOrbPassiveTriggerCounts,
        [nameof(AbstractModel.ModifyShuffleOrder)] = MirroredHookMask.ModifyShuffleOrder,
        [nameof(AbstractModel.ShouldDie)] = MirroredHookMask.ShouldDie,
        [nameof(AbstractModel.ShouldDieLate)] = MirroredHookMask.ShouldDieLate,
        [nameof(AbstractModel.ShouldDraw)] = MirroredHookMask.ShouldDraw,
        [nameof(AbstractModel.ShouldPlay)] = MirroredHookMask.ShouldPlay,
        [nameof(AbstractModel.TryModifyEnergyCostInCombat)] = MirroredHookMask.TryModifyEnergyCostInCombat,
        [nameof(AbstractModel.TryModifyEnergyCostInCombatLate)] = MirroredHookMask.TryModifyEnergyCostInCombatLate,
        [nameof(AbstractModel.TryModifyStarCost)] = MirroredHookMask.TryModifyStarCost,
        [nameof(AbstractModel.TryModifyKeywordsInCombat)] = MirroredHookMask.TryModifyKeywordsInCombat,
    };
    private static readonly MethodInfo[] BaseHooks = typeof(AbstractModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(method => HookNames.ContainsKey(method.Name))
        .ToArray();
    private static readonly ConditionalWeakTable<Type, Participation> TypeParticipation = new();
    private static readonly MethodInfo NativeKeywordHook = typeof(Hook)
        .GetMethod(nameof(Hook.ModifyKeywordsInCombat))!;

    // Re-read the patch table for every captured root, as with the OnPlay patch audit.
    // The immutable decision is shared by its forks; no model or live state is cached.
    internal static MirroredHookListenerFilter Capture()
        => new(!BaseHooks.Append(NativeKeywordHook).Any(method =>
            Harmony.GetPatchInfo(method) is { } patches && patches.Owners.Count != 0));

    internal IReadOnlyList<AbstractModel> Filter(
        IReadOnlyList<AbstractModel> source,
        ref MirroredHookListenerLayout? layout)
    {
        if (!enabled || source.Count == 0)
        {
            Interlocked.Increment(ref _sharedBypasses);
            return source;
        }
        // Only runtime types determine participation. The layout has no model references
        // and can survive model remapping, value changes and Fork without copying.
        if (layout is null || !layout.Matches(source))
        {
            int slot = -1;
            if (source.Count <= MaxSharedLayoutLength)
            {
                uint hash = unchecked((uint)source.Count);
                for (int index = 0; index < source.Count; index++)
                    hash = unchecked(hash * 16777619u
                        ^ (uint)RuntimeHelpers.GetHashCode(source[index].GetType()));
                slot = (int)(hash & (SharedLayoutSlots - 1));
                MirroredHookListenerLayout? shared = Volatile.Read(ref _sharedLayouts[slot]);
                // Hashes select a slot only. Exact type order establishes reuse, so
                // collisions and racing replacement can only cause a cache miss.
                if (shared is not null && shared.Matches(source))
                {
                    Interlocked.Increment(ref _sharedHits);
                    layout = shared;
                }
                else
                {
                    Interlocked.Increment(ref _sharedMisses);
                    if (shared is not null)
                        Interlocked.Increment(ref _sharedCollisions);
                    layout = null;
                }
            }
            else
            {
                Interlocked.Increment(ref _sharedBypasses);
                layout = null;
            }
            if (layout is null)
            {
                var entries = new MirroredHookListenerLayout.Entry[source.Count];
                MirroredHookMask combined = 0;
                for (int index = 0; index < source.Count; index++)
                {
                    Type type = source[index].GetType();
                    MirroredHookMask mask = MaskFor(type);
                    entries[index] = new(type, mask);
                    combined |= mask;
                }
                layout = new(entries, combined);
                if (slot >= 0)
                    Volatile.Write(ref _sharedLayouts[slot], layout);
            }
        }
        return layout.HasAny(MirroredHookMask.All)
            ? new MirroredHookListenerSnapshot(source, layout)
            : Array.Empty<AbstractModel>();
    }

    internal IReadOnlyList<AbstractModel> Filter(IReadOnlyList<AbstractModel> source)
    {
        MirroredHookListenerLayout? layout = null;
        return Filter(source, ref layout);
    }

    private static MirroredHookMask MaskFor(Type type)
        => TypeParticipation.GetValue(type, static value => new Participation(Participates(value))).Mask;

    private static MirroredHookMask Participates(Type type)
    {
        if (type.Assembly != typeof(AbstractModel).Assembly || type.Assembly.IsDynamic)
            return MirroredHookMask.All;
        MirroredHookMask result = 0;
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.DeclaringType != typeof(AbstractModel)
                && HookNames.TryGetValue(method.Name, out MirroredHookMask mask)
                && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel))
                result |= mask;
        }
        return result;
    }

    private sealed record Participation(MirroredHookMask Mask);
}

internal readonly record struct HookLayoutCacheStatistics(
    long Hits, long Misses, long Collisions, long Bypasses);

internal readonly record struct HookListenerSegmentStatistics(
    long PrefixReuses, long PrefixBuilds, long SplitBuilds, long WholeBuilds,
    long EffectivePrefixReuses, long EffectivePrefixBuilds);

[Flags]
internal enum MirroredHookMask : ulong
{
    AfterAttack = 1UL << 0,
    AfterAutoPostPlayPhaseEntered = 1UL << 1,
    AfterBlockBroken = 1UL << 2,
    AfterBlockGained = 1UL << 3,
    AfterCardDiscarded = 1UL << 4,
    AfterCardDrawn = 1UL << 5,
    AfterCardDrawnEarly = 1UL << 6,
    AfterCardExhausted = 1UL << 7,
    AfterCardGeneratedForCombat = 1UL << 8,
    AfterCardPlayed = 1UL << 9,
    AfterCardPlayedLate = 1UL << 10,
    AfterCurrentHpChanged = 1UL << 11,
    AfterDamageGiven = 1UL << 12,
    AfterDamageReceived = 1UL << 13,
    AfterDamageReceivedLate = 1UL << 14,
    AfterDeath = 1UL << 15,
    AfterModifyingBlockAmount = 1UL << 16,
    AfterModifyingCardPlayCount = 1UL << 17,
    AfterModifyingCardPlayResultLocation = 1UL << 18,
    AfterModifyingHpLostAfterOsty = 1UL << 19,
    AfterOrbChanneled = 1UL << 20,
    AfterOrbEvoked = 1UL << 21,
    AfterPreventingDeath = 1UL << 22,
    AfterShuffle = 1UL << 23,
    AfterStarsGained = 1UL << 24,
    BeforeAttack = 1UL << 25,
    BeforeBlockGained = 1UL << 26,
    BeforeCardPlayed = 1UL << 27,
    BeforeDamageReceived = 1UL << 28,
    BeforeDeath = 1UL << 29,
    BeforeSideTurnEnd = 1UL << 30,
    BeforeSideTurnEndEarly = 1UL << 31,
    BeforeSideTurnEndVeryEarly = 1UL << 32,
    ModifyAttackHitCount = 1UL << 33,
    ModifyBlockAdditive = 1UL << 34,
    ModifyBlockMultiplicative = 1UL << 35,
    ModifyCardPlayCount = 1UL << 36,
    ModifyCardPlayResultLocation = 1UL << 37,
    ModifyDamageAdditive = 1UL << 38,
    ModifyDamageCap = 1UL << 39,
    ModifyDamageMultiplicative = 1UL << 40,
    ModifyHpLostAfterOsty = 1UL << 41,
    ModifyHpLostAfterOstyLate = 1UL << 42,
    ModifyHpLostBeforeOsty = 1UL << 43,
    ModifyHpLostBeforeOstyLate = 1UL << 44,
    ModifyOrbPassiveTriggerCounts = 1UL << 45,
    ModifyShuffleOrder = 1UL << 46,
    ShouldDie = 1UL << 47,
    ShouldDieLate = 1UL << 48,
    ShouldDraw = 1UL << 49,
    ShouldPlay = 1UL << 50,
    TryModifyEnergyCostInCombat = 1UL << 51,
    TryModifyEnergyCostInCombatLate = 1UL << 52,
    TryModifyStarCost = 1UL << 53,
    TryModifyKeywordsInCombat = 1UL << 54,
    AfterSideTurnEndLate = 1UL << 55,
    All = ulong.MaxValue,
}

// Immutable type metadata, shared across forks. It never retains any combat models.
internal sealed class MirroredHookListenerLayout(
    MirroredHookListenerLayout.Entry[] entries,
    MirroredHookMask combined)
{
    internal readonly record struct Entry(Type Type, MirroredHookMask Mask);
    internal Entry[] Entries { get; } = entries;
    internal bool HasAny(MirroredHookMask mask) => (combined & mask) != 0;
    internal bool Matches(IReadOnlyList<AbstractModel> source)
    {
        if (source.Count != Entries.Length)
            return false;
        for (int index = 0; index < source.Count; index++)
            if (source[index].GetType() != Entries[index].Type)
                return false;
        return true;
    }
}

// The original complete listener sequence plus its type-only dispatch layout.
// Model identity and order come exclusively from the branch's native snapshot.
internal sealed class MirroredHookListenerSnapshot(
    IReadOnlyList<AbstractModel> source,
    MirroredHookListenerLayout layout) : IReadOnlyList<AbstractModel>
{
    internal MirroredHookListenerLayout Layout { get; } = layout;
    internal bool HasAny(MirroredHookMask mask) => Layout.HasAny(mask);
    public int Count => source.Count;
    public AbstractModel this[int index] => source[index];
    public IEnumerator<AbstractModel> GetEnumerator() => source.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
