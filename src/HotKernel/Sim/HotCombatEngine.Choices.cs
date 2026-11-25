using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    [ThreadStatic]
    private static ushort[]? t_generatedChoiceOptions;

    [ThreadStatic]
    private static int t_generatedChoiceOptionCount;

    [ThreadStatic]
    private static ushort[]? t_filteredChoiceUids;

    [ThreadStatic]
    private static int t_filteredChoiceUidCount;

    [ThreadStatic]
    private static ushort[]? t_excludedChoiceUids;

    [ThreadStatic]
    private static int t_excludedChoiceUidCount;

    /// <summary>
    /// Stores the generated card option DefIds rolled for the currently pending
    /// <see cref="HotChoiceKind.GeneratedCard"/> choice (zero per-step heap allocations).
    /// </summary>
    internal static void SetPendingGeneratedChoiceOptions(ReadOnlySpan<ushort> cardDefIds)
    {
        ushort[] buf = t_generatedChoiceOptions ??= new ushort[16];
        int count = Math.Min(cardDefIds.Length, buf.Length);
        if (count > 0)
            cardDefIds[..count].CopyTo(buf);
        t_generatedChoiceOptionCount = count;
    }

    internal static ReadOnlySpan<ushort> GetPendingGeneratedChoiceOptions()
        => t_generatedChoiceOptions != null && t_generatedChoiceOptionCount > 0
            ? t_generatedChoiceOptions.AsSpan(0, t_generatedChoiceOptionCount)
            : ReadOnlySpan<ushort>.Empty;

    /// <summary>
    /// Returns the allowed card UIDs for the currently pending filtered pile choice
    /// (e.g., <c>SeekerStrike</c> 3-card shuffled draw subset, <c>TouchOfInsanity</c> eligible cards).
    /// Empty when no UID allow-list is active.
    /// </summary>
    internal static ReadOnlySpan<ushort> GetPendingFilteredChoiceUids()
        => t_filteredChoiceUids != null && t_filteredChoiceUidCount > 0
            ? t_filteredChoiceUids.AsSpan(0, t_filteredChoiceUidCount)
            : ReadOnlySpan<ushort>.Empty;

    internal static ReadOnlySpan<ushort> GetPendingExcludedChoiceUids()
        => t_excludedChoiceUids != null && t_excludedChoiceUidCount > 0
            ? t_excludedChoiceUids.AsSpan(0, t_excludedChoiceUidCount)
            : ReadOnlySpan<ushort>.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearPendingChoiceUidFilters()
    {
        t_filteredChoiceUidCount = 0;
        t_excludedChoiceUidCount = 0;
    }

    private static void SetPendingFilteredChoiceUids(ReadOnlySpan<ushort> allowedUids)
    {
        ushort[] buf = t_filteredChoiceUids ??= new ushort[HotLimits.MaxCards];
        int count = Math.Min(allowedUids.Length, buf.Length);
        if (count > 0)
            allowedUids[..count].CopyTo(buf);
        t_filteredChoiceUidCount = count;
        t_excludedChoiceUidCount = 0;
    }

    private static void SetPendingExcludedChoiceUids(ReadOnlySpan<ushort> excludedUids)
    {
        ushort[] buf = t_excludedChoiceUids ??= new ushort[HotLimits.MaxCards];
        int count = Math.Min(excludedUids.Length, buf.Length);
        if (count > 0)
            excludedUids[..count].CopyTo(buf);
        t_excludedChoiceUidCount = count;
        t_filteredChoiceUidCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordPendingChoiceGroup(HotExecScratch scratch, byte minCount)
    {
        if (scratch.ActiveChoiceGroupSeq != 0)
        {
            scratch.PendingChoiceGroupSeq = scratch.ActiveChoiceGroupSeq;
            scratch.PendingChoiceGroupFlags = scratch.ActiveChoiceGroupFlags;
        }
        else
        {
            scratch.PendingChoiceGroupSeq = ++scratch.NextChoiceGroupSeq;
            scratch.PendingChoiceGroupFlags = minCount == 0
                ? HotChoiceGroupFlags.Optional
                : HotChoiceGroupFlags.None;
        }
    }

    /// <summary>
    /// Restart-from-parent choice protocol: if a caller-supplied choice exists at
    /// <c>scratch.ChoiceCursor</c>, consumes and returns it immediately (<c>true</c>);
    /// otherwise records the pending choice request on <paramref name="scratch"/> and
    /// returns <c>false</c> so the caller can enumerate options via
    /// <see cref="EnumerateChoiceOptions"/> and replay from the parent state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool RequestChoice(
        ref CombatHotState s,
        HotExecScratch scratch,
        HotChoiceKind kind,
        ushort sourceCardDefId,
        byte minCount,
        byte maxCount,
        out HotChoiceSelection selected)
    {
        if (scratch.ChoiceCursor < scratch.ChoiceCount)
        {
            if (scratch.ActiveChoiceGroupSeq == 0)
                scratch.NextChoiceGroupSeq++;
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        ClearPendingChoiceUidFilters();
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
        RecordPendingChoiceGroup(scratch, minCount);
        selected = default;
        return false;
    }

    /// <summary>
    /// Variant of <see cref="RequestChoice"/> restricted to an explicit allow-list of card UIDs
    /// (e.g., <c>SeekerStrike</c> 3-card shuffled draw subset, <c>TouchOfInsanity</c> cost filter).
    /// </summary>
    internal static bool RequestChoiceFiltered(
        ref CombatHotState s,
        HotExecScratch scratch,
        HotChoiceKind kind,
        ushort sourceCardDefId,
        byte minCount,
        byte maxCount,
        ReadOnlySpan<ushort> allowedUids,
        out HotChoiceSelection selected)
    {
        if (scratch.ChoiceCursor < scratch.ChoiceCount)
        {
            if (scratch.ActiveChoiceGroupSeq == 0)
                scratch.NextChoiceGroupSeq++;
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        SetPendingFilteredChoiceUids(allowedUids);
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
        RecordPendingChoiceGroup(scratch, minCount);
        selected = default;
        return false;
    }

    /// <summary>
    /// Variant of <see cref="RequestChoice"/> that excludes already-picked card UIDs during
    /// simultaneous multi-card selection (e.g., <c>Prepared</c>, <c>GamblersBrew</c>, <c>GamblingChip</c>).
    /// </summary>
    internal static bool RequestChoiceExcluding(
        ref CombatHotState s,
        HotExecScratch scratch,
        HotChoiceKind kind,
        ushort sourceCardDefId,
        byte minCount,
        byte maxCount,
        ReadOnlySpan<ushort> excludedUids,
        out HotChoiceSelection selected)
    {
        if (scratch.ChoiceCursor < scratch.ChoiceCount)
        {
            if (scratch.ActiveChoiceGroupSeq == 0)
                scratch.NextChoiceGroupSeq++;
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        SetPendingExcludedChoiceUids(excludedUids);
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
        RecordPendingChoiceGroup(scratch, minCount);
        selected = default;
        return false;
    }

    /// <summary>
    /// Enumerates canonically deduplicated legal choices for a paused state whose
    /// <c>Step</c> call returned <c>NeedsChoice == true</c>. Zero managed heap allocations.
    /// </summary>
    public static int EnumerateChoiceOptions(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        Span<HotChoiceSelection> dst)
        => EnumerateChoiceOptionsWithClasses(in s, cold, rules, scratch, dst, Span<byte>.Empty);

    /// <summary>
    /// Lossless canonical choice enumerator:
    /// - Deduplicates identical cards using full 44-byte card state, UID-reference isolation, and
    ///   4-bucket <c>DrawPile</c> position preservation (<c>0</c>, <c>1..4</c>, <c>5..9</c>, <c>10+</c>);
    /// - Emits the <b>minimum non-excluded <c>CardUid</c></b> as the unique representative of each
    ///   equivalence class, guaranteeing a lossless 1-to-1 bijection between unordered multisets and
    ///   strictly-increasing-UID sequences;
    /// - Optionally records each class's available multiplicity into <paramref name="classMultiplicities"/>.
    /// </summary>
    public static int EnumerateChoiceOptionsWithClasses(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        Span<HotChoiceSelection> dst,
        Span<byte> classMultiplicities)
    {
        if (!scratch.NeedsChoice || dst.IsEmpty)
            return 0;

        int written = 0;
        HotChoiceKind kind = scratch.PendingChoiceKind;
        ushort sourceDefId = scratch.PendingChoiceSourceCardDefId;
        HotCustomCardId sourceCardId = sourceDefId > 0 && sourceDefId < rules.Cards.Length
            ? rules.Cards[sourceDefId].CardId
            : HotCustomCardId.None;

        if (kind == HotChoiceKind.GeneratedCard)
        {
            if (scratch.PendingChoiceMin == 0 && written < dst.Length)
            {
                if (written < classMultiplicities.Length)
                    classMultiplicities[written] = 1;
                dst[written++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            }

            ushort[]? opts = t_generatedChoiceOptions;
            int optCount = t_generatedChoiceOptionCount;
            if (opts != null && optCount > 0)
            {
                for (int i = 0; i < optCount && written < dst.Length; i++)
                {
                    ushort defId = opts[i];
                    if (defId == 0)
                        continue;

                    bool duplicate = false;
                    for (int j = 0; j < i; j++)
                    {
                        if (opts[j] == defId)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate)
                    {
                        if (written < classMultiplicities.Length)
                            classMultiplicities[written] = 1;
                        dst[written++] = new HotChoiceSelection(HotChoiceKind.GeneratedCard, (ushort)i, defId);
                    }
                }
            }
            return written;
        }

        HotPileKind pile;
        switch (kind)
        {
            case HotChoiceKind.HandCard:
                pile = HotPileKind.Hand;
                break;
            case HotChoiceKind.DrawPile:
                pile = HotPileKind.Draw;
                break;
            case HotChoiceKind.DiscardPile:
                pile = HotPileKind.Discard;
                break;
            case HotChoiceKind.ExhaustPile:
                pile = HotPileKind.Exhaust;
                break;
            default:
                return 0;
        }

        int zoneStart = HotPiles.ZoneStart(in s, pile);
        int zoneCount = HotPiles.ZoneCount(in s, pile);
        if (sourceCardId == HotCustomCardId.KiChant && pile == HotPileKind.Draw)
        {
            int topLimit = scratch.PendingChoiceMax > 0 ? scratch.PendingChoiceMax : 3;
            if (zoneCount > topLimit)
                zoneCount = topLimit;
        }

        if (scratch.PendingChoiceMin == 0 && written < dst.Length)
        {
            if (written < classMultiplicities.Length)
                classMultiplicities[written] = 1;
            dst[written++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
        }

        Span<int> emittedCardIndices = stackalloc int[HotLimits.MaxStackAllocElements];
        Span<int> emittedOffsets = stackalloc int[HotLimits.MaxStackAllocElements];
        Span<int> emittedDstSlots = stackalloc int[HotLimits.MaxStackAllocElements];
        int emittedCount = 0;

        ReadOnlySpan<ushort> allowedUids = GetPendingFilteredChoiceUids();
        ReadOnlySpan<ushort> excludedUids = GetPendingExcludedChoiceUids();

        for (int offset = 0; offset < zoneCount; offset++)
        {
            int cardIdx = zoneStart + offset;
            ref readonly HotCard candidate = ref s.Cards[cardIdx];
            if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, candidate.CardUid))
                continue;
            if (!excludedUids.IsEmpty && ContainsUid(excludedUids, candidate.CardUid))
                continue;
            if (!MatchesChoiceFilter(in s, in candidate, rules, sourceCardId))
                continue;

            int matchedClass = -1;
            int compareLimit = Math.Min(emittedCount, emittedCardIndices.Length);
            for (int prev = 0; prev < compareLimit; prev++)
            {
                ref readonly HotCard prior = ref s.Cards[emittedCardIndices[prev]];
                if (AreChoiceEquivalentCards(in s, pile, emittedOffsets[prev], offset, in prior, in candidate, cold))
                {
                    matchedClass = prev;
                    break;
                }
            }

            if (matchedClass >= 0)
            {
                int dstSlot = emittedDstSlots[matchedClass];
                if (dstSlot < classMultiplicities.Length && classMultiplicities[dstSlot] < byte.MaxValue)
                    classMultiplicities[dstSlot]++;

                // Always keep the minimum non-excluded CardUid as the unique class representative
                // so unordered multiset enumeration has an exact 1-to-1 strictly-increasing-UID bijection.
                ushort curRepUid = dst[dstSlot].CardUidOrIndex;
                if (candidate.CardUid != 0 && (curRepUid == 0 || candidate.CardUid < curRepUid))
                {
                    dst[dstSlot] = new HotChoiceSelection(kind, candidate.CardUid, candidate.CardDefId);
                    emittedCardIndices[matchedClass] = cardIdx;
                    emittedOffsets[matchedClass] = offset;
                }
                continue;
            }

            if (written >= dst.Length)
                continue;

            if (emittedCount < emittedCardIndices.Length)
            {
                emittedCardIndices[emittedCount] = cardIdx;
                emittedOffsets[emittedCount] = offset;
                emittedDstSlots[emittedCount] = written;
            }
            emittedCount++;

            if (written < classMultiplicities.Length)
                classMultiplicities[written] = 1;
            dst[written++] = new HotChoiceSelection(kind, candidate.CardUid, candidate.CardDefId);
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsUid(ReadOnlySpan<ushort> uids, ushort uid)
    {
        for (int i = 0; i < uids.Length; i++)
        {
            if (uids[i] == uid)
                return true;
        }
        return false;
    }

    internal static int FindCardIndexByUid(in CombatHotState s, HotPileKind pile, ushort cardUid)
    {
        if (cardUid == 0)
            return -1;
        int start = HotPiles.ZoneStart(in s, pile);
        int end = start + HotPiles.ZoneCount(in s, pile);
        for (int i = start; i < end; i++)
        {
            if (s.Cards[i].CardUid == cardUid)
                return i;
        }
        return -1;
    }

    internal static int FindCardIndexByUidAnyPile(in CombatHotState s, ushort cardUid)
    {
        if (cardUid == 0)
            return -1;
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            if (s.Cards[i].CardUid == cardUid)
                return i;
        }
        return -1;
    }

    private static bool MatchesChoiceFilter(
        in CombatHotState s,
        in HotCard card,
        HotRules rules,
        HotCustomCardId sourceCardId)
    {
        if (card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return false;

        HotCardDef def = rules.Cards[card.CardDefId];
        return sourceCardId switch
        {
            HotCustomCardId.Armaments => card.UpgradeLevel < def.MaxUpgradeLevel,
            HotCustomCardId.DualWield => def.Type is CardType.Attack or CardType.Power,
            HotCustomCardId.SecretWeapon => def.Type == CardType.Attack,
            HotCustomCardId.SecretTechnique => def.Type == CardType.Skill,
            HotCustomCardId.HandTrick => def.Type == CardType.Skill && (card.Flags & HotCardFlags.IsSlyThisTurn) == 0,
            HotCustomCardId.HeirloomHammer => def.IsVisualColorless,
            HotCustomCardId.SculptingStrike => (card.LocalKeywordsMask & HotCardKeywordMask.Ethereal) == 0,
            HotCustomCardId.Snap => !HasEffectiveRetainKeyword(in s, in card, rules),
            HotCustomCardId.DecisionsDecisions => def.Type == CardType.Skill && (card.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0,
            HotCustomCardId.LockedIn => def.IsShootTag,
            _ => true,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DrawPileChoiceBucket(int offset)
        => offset == 0 ? 0 : offset < 5 ? 1 : offset < 10 ? 2 : 3;

    internal static bool IsCardUidReferenced(in CombatHotState s, HotColdSidecar? cold, ushort uid)
    {
        if (uid == 0)
            return false;
        if (s.HistoryCourseUid == uid)
            return true;
        if (s.Fool.FirstPlayCardUid[0] == uid
            || s.Fool.FirstPlayCardUid[1] == uid
            || s.Fool.FirstPlayCardUid[2] == uid)
        {
            return true;
        }

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            if (s.Powers[i].AuxCardUid == uid)
                return true;
        }

        if (cold != null)
        {
            if (cold.DecimalVars is { Length: > 0 } decs)
            {
                for (int i = 0; i < decs.Length; i++)
                {
                    if (decs[i].CardUid == uid)
                        return true;
                }
            }
            if (cold.CostOverflow is { Length: > 0 } co)
            {
                for (int i = 0; i < co.Length; i++)
                {
                    if (co[i].CardUid == uid)
                        return true;
                }
            }
            if (cold.RuptureEntries is { Length: > 0 } ru)
            {
                for (int i = 0; i < ru.Length; i++)
                {
                    if (ru[i].CardUid == uid)
                        return true;
                }
            }
            if (cold.DampenEntries is { Length: > 0 } da)
            {
                for (int i = 0; i < da.Length; i++)
                {
                    if (da[i].CardUid == uid)
                        return true;
                }
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreChoiceEquivalentCards(
        in CombatHotState s,
        HotPileKind pile,
        int offsetA,
        int offsetB,
        in HotCard a,
        in HotCard b,
        HotColdSidecar? cold)
    {
        if (a.CardDefId != b.CardDefId)
            return false;

        if (pile == HotPileKind.Draw && DrawPileChoiceBucket(offsetA) != DrawPileChoiceBucket(offsetB))
            return false;

        if ((a.CardUid != 0 && IsCardUidReferenced(in s, cold, a.CardUid))
            || (b.CardUid != 0 && IsCardUidReferenced(in s, cold, b.CardUid)))
        {
            return false;
        }

        ReadOnlySpan<byte> bytesA = HotStatePacker.AsReadOnlyBytes(in a);
        ReadOnlySpan<byte> bytesB = HotStatePacker.AsReadOnlyBytes(in b);

        // Bytes 4..15 cover UpgradeLevel..CostModCount; byte 15 is TempStarCostPacked (masked if bit 7 == 0).
        if (!bytesA.Slice(4, 11).SequenceEqual(bytesB.Slice(4, 11)))
            return false;
        byte starA = (a.TempStarCostPacked & 0x80) != 0 ? a.TempStarCostPacked : (byte)0;
        byte starB = (b.TempStarCostPacked & 0x80) != 0 ? b.TempStarCostPacked : (byte)0;
        if (starA != starB)
            return false;

        // Active inline energy cost modifiers (bytes 16..28, 3 bytes each).
        int modCount = Math.Min((int)a.CostModCount, HotLimits.MaxEnergyCostMods);
        if (modCount > 0 && !bytesA.Slice(16, modCount * 3).SequenceEqual(bytesB.Slice(16, modCount * 3)))
            return false;

        // Bytes 28..48 cover EnchId..CardPrivate (20 bytes).
        return bytesA.Slice(28, 20).SequenceEqual(bytesB.Slice(28, 20));
    }
}
