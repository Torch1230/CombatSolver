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
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        ClearPendingChoiceUidFilters();
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
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
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        SetPendingFilteredChoiceUids(allowedUids);
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
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
            selected = scratch.Choices[scratch.ChoiceCursor++];
            return true;
        }

        SetPendingExcludedChoiceUids(excludedUids);
        scratch.NeedsChoice = true;
        scratch.PendingChoiceKind = kind;
        scratch.PendingChoiceSourceCardDefId = sourceCardDefId;
        scratch.PendingChoiceMin = minCount;
        scratch.PendingChoiceMax = maxCount;
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
                dst[written++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);

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
                        dst[written++] = new HotChoiceSelection(HotChoiceKind.GeneratedCard, (ushort)i, defId);
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
            // KiChant lets the player pick from the top N cards of DrawPile (max stored in PendingChoiceMax if >1, else 3).
            int topLimit = scratch.PendingChoiceMax > 0 ? scratch.PendingChoiceMax : 3;
            if (zoneCount > topLimit)
                zoneCount = topLimit;
        }

        if (scratch.PendingChoiceMin == 0 && written < dst.Length)
            dst[written++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);

        Span<int> emittedCardIndices = stackalloc int[HotLimits.MaxStackAllocElements];
        int emittedCount = 0;

        ReadOnlySpan<ushort> allowedUids = GetPendingFilteredChoiceUids();
        ReadOnlySpan<ushort> excludedUids = GetPendingExcludedChoiceUids();

        for (int offset = 0; offset < zoneCount; offset++)
        {
            if (written >= dst.Length)
                break;

            int cardIdx = zoneStart + offset;
            ref readonly HotCard candidate = ref s.Cards[cardIdx];
            if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, candidate.CardUid))
                continue;
            if (!excludedUids.IsEmpty && ContainsUid(excludedUids, candidate.CardUid))
                continue;
            if (!MatchesChoiceFilter(in s, in candidate, rules, sourceCardId))
                continue;

            bool isDup = false;
            int compareLimit = Math.Min(emittedCount, emittedCardIndices.Length);
            for (int prev = 0; prev < compareLimit; prev++)
            {
                ref readonly HotCard prior = ref s.Cards[emittedCardIndices[prev]];
                if (AreChoiceEquivalentCards(in prior, in candidate, cold, rules))
                {
                    isDup = true;
                    break;
                }
            }

            if (isDup)
                continue;

            if (emittedCount < emittedCardIndices.Length)
                emittedCardIndices[emittedCount] = cardIdx;
            emittedCount++;

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
            HotCustomCardId.HeirloomHammer => def.Domain == HotCardDomain.Colorless,
            HotCustomCardId.SculptingStrike => (card.LocalKeywordsMask & HotCardKeywordMask.Ethereal) == 0,
            HotCustomCardId.Snap => (card.LocalKeywordsMask & HotCardKeywordMask.Retain) == 0,
            HotCustomCardId.DecisionsDecisions => def.Type == CardType.Skill && (card.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0,
            HotCustomCardId.LockedIn or HotCustomCardId.ArrowStab => def.IsShootTag,
            HotCustomCardId.WellLaidPlans => (card.Flags & HotCardFlags.SingleTurnRetain) == 0
                && (card.LocalKeywordsMask & HotCardKeywordMask.Retain) == 0,
            _ => true,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreChoiceEquivalentCards(
        in HotCard a,
        in HotCard b,
        HotColdSidecar? cold,
        HotRules rules)
    {
        if (a.CardDefId != b.CardDefId
            || a.UpgradeLevel != b.UpgradeLevel
            || a.EnchId != b.EnchId
            || a.EnchAmt != b.EnchAmt
            || a.EnchStatus != b.EnchStatus
            || a.AfflId != b.AfflId
            || a.AfflAmt != b.AfflAmt
            || a.LocalKeywordsMask != b.LocalKeywordsMask
            || a.BaseReplayCount != b.BaseReplayCount
            || a.ExtraReplayThisTurn != b.ExtraReplayThisTurn
            || a.Var0Delta != b.Var0Delta
            || a.Var1Delta != b.Var1Delta
            || a.CardPrivate != b.CardPrivate)
        {
            return false;
        }

        const HotCardFlags semanticFlags = HotCardFlags.CostsX
            | HotCardFlags.HasStarCostX
            | HotCardFlags.ExhaustOnNextPlay
            | HotCardFlags.IsSlyThisTurn
            | HotCardFlags.SingleTurnRetain
            | HotCardFlags.IsDupe
            | HotCardFlags.ReturnToHandNextTurn;
        if ((a.Flags & semanticFlags) != (b.Flags & semanticFlags))
            return false;

        int costA = (a.Flags & HotCardFlags.CostsX) != 0
            ? a.CapturedEnergy
            : HotCostModifiers.GetLocalEnergyCost(in a, cold);
        int costB = (b.Flags & HotCardFlags.CostsX) != 0
            ? b.CapturedEnergy
            : HotCostModifiers.GetLocalEnergyCost(in b, cold);
        if (costA != costB)
            return false;

        int starA = HotCostModifiers.GetEffectiveStarCost(in a, cold);
        int starB = HotCostModifiers.GetEffectiveStarCost(in b, cold);
        return starA == starB;
    }
}
