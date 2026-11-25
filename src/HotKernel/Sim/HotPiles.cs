using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CombatSolver.HotKernel;

/// <summary>
/// Contiguous 5-pile operations over CombatHotState.Cards laid out as [Hand | Draw | Discard | Exhaust | Play].
/// </summary>
internal static class HotPiles
{
    [ThreadStatic]
    private static HotRules? t_activeRules;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static HotRules? PushActiveRules(HotRules? rules)
    {
        HotRules? prev = t_activeRules;
        t_activeRules = rules;
        return prev;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PopActiveRules(HotRules? prev)
        => t_activeRules = prev;

    /// <summary>
    /// Exact STS2 0.113.1 <c>CardPile.AddInternal</c> (lines 108-111):
    /// whenever any <c>CardTag.Shoot</c> card enters a combat pile during active combat while
    /// the player has 0 arrow slots (non-Fool with <c>BaseArrowSlotCount == 0</c>), auto-grant 1 slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MaybeGainArrowSlotOnPileEnter(ref CombatHotState s, ushort cardDefId, HotRules? rules = null)
    {
        if (s.ArrowCount != 0 || (s.CombatFlags & HotCombatFlags.IsInProgress) == 0)
            return;
        HotRules? r = rules ?? t_activeRules;
        if (r != null && cardDefId < r.Cards.Length && r.Cards[cardDefId].IsShootTag)
        {
            s.Fool.Arrows[0] = 0; // ArrowStatus.Retrieved (0), DestroyAfterImpaled = false
            s.ArrowCount = 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZoneStart(in CombatHotState s, HotPileKind pile) => pile switch
    {
        HotPileKind.Hand => 0,
        HotPileKind.Draw => s.HandCount,
        HotPileKind.Discard => s.HandCount + s.DrawCount,
        HotPileKind.Exhaust => s.HandCount + s.DrawCount + s.DiscardCount,
        HotPileKind.Play => s.HandCount + s.DrawCount + s.DiscardCount + s.ExhaustCount,
        _ => throw new ArgumentOutOfRangeException(nameof(pile), pile, null),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZoneCount(in CombatHotState s, HotPileKind pile) => pile switch
    {
        HotPileKind.Hand => s.HandCount,
        HotPileKind.Draw => s.DrawCount,
        HotPileKind.Discard => s.DiscardCount,
        HotPileKind.Exhaust => s.ExhaustCount,
        HotPileKind.Play => s.PlayCount,
        _ => throw new ArgumentOutOfRangeException(nameof(pile), pile, null),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HotPileKind PileOfIndex(in CombatHotState s, int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Card index must be non-negative.");
        int end = s.HandCount;
        if (index < end) return HotPileKind.Hand;
        end += s.DrawCount;
        if (index < end) return HotPileKind.Draw;
        end += s.DiscardCount;
        if (index < end) return HotPileKind.Discard;
        end += s.ExhaustCount;
        if (index < end) return HotPileKind.Exhaust;
        end += s.PlayCount;
        if (index < end) return HotPileKind.Play;
        throw new ArgumentOutOfRangeException(nameof(index), index, "Card index out of range.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPileCount(ref CombatHotState s, HotPileKind pile, int delta)
    {
        switch (pile)
        {
            case HotPileKind.Hand: s.HandCount = (ushort)(s.HandCount + delta); break;
            case HotPileKind.Draw: s.DrawCount = (ushort)(s.DrawCount + delta); break;
            case HotPileKind.Discard: s.DiscardCount = (ushort)(s.DiscardCount + delta); break;
            case HotPileKind.Exhaust: s.ExhaustCount = (ushort)(s.ExhaustCount + delta); break;
            case HotPileKind.Play: s.PlayCount = (ushort)(s.PlayCount + delta); break;
            default: throw new ArgumentOutOfRangeException(nameof(pile), pile, null);
        }
    }

    /// <summary>
    /// Zero-byte-move draw: shifts the Hand/Draw boundary by 1 so Draw[0] becomes Hand[^1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DrawTopToHand(ref CombatHotState s)
    {
        if (s.DrawCount == 0)
            return false;
        if (s.MaxHandSize > 0 && s.HandCount >= s.MaxHandSize)
            return false;
        s.HandCount++;
        s.DrawCount--;
        MaybeGainArrowSlotOnPileEnter(ref s, s.Cards[s.HandCount - 1].CardDefId);
        return true;
    }

    /// <summary>
    /// Moves the card at <paramref name="srcIndex"/> into <paramref name="dstPile"/> at <paramref name="dstOffsetInPile"/>
    /// (0 = top/front, ZoneCount = bottom/back) using overlap-safe contiguous slice rotation.
    /// Returns the card's new index in CombatHotState.Cards.
    /// </summary>
    public static int MoveCard(ref CombatHotState s, int srcIndex, HotPileKind dstPile, int dstOffsetInPile)
    {
        int total = s.TotalCardCount;
        if ((uint)srcIndex >= (uint)total)
            throw new ArgumentOutOfRangeException(nameof(srcIndex), srcIndex, "Source card index out of range.");

        HotPileKind srcPile = PileOfIndex(in s, srcIndex);
        int oldDstCount = ZoneCount(in s, dstPile);
        if ((uint)dstOffsetInPile > (uint)oldDstCount)
            throw new ArgumentOutOfRangeException(nameof(dstOffsetInPile), dstOffsetInPile, "Destination offset out of range.");

        AddPileCount(ref s, srcPile, -1);
        int newDstCount = ZoneCount(in s, dstPile);
        int clampedOffset = dstOffsetInPile > newDstCount ? newDstCount : dstOffsetInPile;
        int dstIndex = ZoneStart(in s, dstPile) + clampedOffset;
        AddPileCount(ref s, dstPile, +1);

        if (dstIndex != srcIndex)
        {
            Span<HotCard> cards = MemoryMarshal.CreateSpan(ref s.Cards[0], total);
            HotCard moved = cards[srcIndex];
            if (dstIndex > srcIndex)
            {
                cards.Slice(srcIndex + 1, dstIndex - srcIndex).CopyTo(cards.Slice(srcIndex));
            }
            else
            {
                cards.Slice(dstIndex, srcIndex - dstIndex).CopyTo(cards.Slice(dstIndex + 1));
            }
            cards[dstIndex] = moved;
        }

        MaybeGainArrowSlotOnPileEnter(ref s, s.Cards[dstIndex].CardDefId);
        return dstIndex;
    }

    /// <summary>
    /// Inserts <paramref name="card"/> into <paramref name="dstPile"/> at <paramref name="dstOffsetInPile"/>.
    /// Throws <see cref="HotCapacityExceededException"/> if total cards reach <see cref="HotLimits.MaxCards"/>.
    /// </summary>
    public static int AddCard(ref CombatHotState s, in HotCard card, HotPileKind dstPile, int dstOffsetInPile)
    {
        int total = s.TotalCardCount;
        if (total >= HotLimits.MaxCards)
            throw new HotCapacityExceededException($"Card count exceeded HotLimits.MaxCards ({HotLimits.MaxCards}).");

        int zoneCount = ZoneCount(in s, dstPile);
        if ((uint)dstOffsetInPile > (uint)zoneCount)
            throw new ArgumentOutOfRangeException(nameof(dstOffsetInPile), dstOffsetInPile, "Destination offset out of range.");

        int dstIndex = ZoneStart(in s, dstPile) + dstOffsetInPile;
        Span<HotCard> cards = MemoryMarshal.CreateSpan(ref s.Cards[0], total + 1);
        if (dstIndex < total)
            cards.Slice(dstIndex, total - dstIndex).CopyTo(cards.Slice(dstIndex + 1));
        cards[dstIndex] = card;
        AddPileCount(ref s, dstPile, +1);
        MaybeGainArrowSlotOnPileEnter(ref s, card.CardDefId);
        return dstIndex;
    }

    /// <summary>
    /// Removes the card at <paramref name="index"/> and compacts the trailing zones left by 1.
    /// </summary>
    public static void RemoveCard(ref CombatHotState s, int index)
    {
        int total = s.TotalCardCount;
        if ((uint)index >= (uint)total)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Card index out of range.");

        HotPileKind pile = PileOfIndex(in s, index);
        Span<HotCard> cards = MemoryMarshal.CreateSpan(ref s.Cards[0], total);
        int tailLen = total - index - 1;
        if (tailLen > 0)
            cards.Slice(index + 1, tailLen).CopyTo(cards.Slice(index));
        cards[total - 1] = default;
        AddPileCount(ref s, pile, -1);
    }

    /// <summary>
    /// Merges Discard followed by Draw into scratch.ShuffleCards, performs exact CombatCardOrdering stable sort,
    /// runs UnstableShuffle via HotRngStream.Shuffle, and writes the combined cards back into Draw.
    /// Zero heap allocations and zero large stackallocs.
    /// </summary>
    public static void ShuffleDiscardIntoDraw(
        ref CombatHotState s,
        HotRules? rules,
        HotExecScratch scratch,
        HotColdSidecar? cold = null)
    {
        int drawStart = s.HandCount;
        int drawCount = s.DrawCount;
        int discardStart = drawStart + drawCount;
        int discardCount = s.DiscardCount;
        int total = drawCount + discardCount;

        Span<HotCard> buf = scratch.ShuffleCards.AsSpan(0, total);
        if (total > 0)
        {
            Span<HotCard> allCards = MemoryMarshal.CreateSpan(ref s.Cards[0], s.TotalCardCount);
            if (discardCount > 0)
                allCards.Slice(discardStart, discardCount).CopyTo(buf.Slice(0, discardCount));
            if (drawCount > 0)
                allCards.Slice(drawStart, drawCount).CopyTo(buf.Slice(discardCount, drawCount));

            if (total > 1)
                StableSortShuffleBuffer(scratch, total, in s, rules, cold);
        }

        HotRng.UnstableShuffle(buf, ref s.Rng[(int)HotRngStream.Shuffle]);

        // PerfectFit enchantment (ModifyShuffleOrder, !isInitialShuffle; PlayerCombatState.AllPiles:
        // Hand, DrawPile, DiscardPile, ExhaustPile, PlayPile; CardPileCmd.Shuffle lines 1082-1090):
        // iterate pre-shuffle Draw then Discard (0..count-1 within each pile, still intact in s.Cards
        // before buf.CopyTo(dst)) so multiple PerfectFit cards move to buf[0] in exact vanilla listener order.
        if (total > 1 && rules != null)
        {
            byte perfectFitEnchId = rules.GetEnchantmentDefId(HotEnchantmentId.PerfectFit);
            if (perfectFitEnchId != 0)
            {
                Span<HotCard> preShuffleCards = MemoryMarshal.CreateSpan(ref s.Cards[0], s.TotalCardCount);
                for (int i = 0; i < drawCount; i++)
                {
                    ref readonly HotCard preCard = ref preShuffleCards[drawStart + i];
                    if (preCard.EnchId != perfectFitEnchId)
                        continue;
                    MoveMatchingCardToFront(buf, preCard.CardUid, preCard.CardDefId, perfectFitEnchId);
                }
                for (int i = 0; i < discardCount; i++)
                {
                    ref readonly HotCard preCard = ref preShuffleCards[discardStart + i];
                    if (preCard.EnchId != perfectFitEnchId)
                        continue;
                    MoveMatchingCardToFront(buf, preCard.CardUid, preCard.CardDefId, perfectFitEnchId);
                }
            }
        }

        if (total > 0)
        {
            Span<HotCard> dst = MemoryMarshal.CreateSpan(ref s.Cards[0], s.TotalCardCount).Slice(drawStart, total);
            buf.CopyTo(dst);
            if (s.ArrowCount == 0 && (s.CombatFlags & HotCombatFlags.IsInProgress) != 0)
            {
                HotRules? r = rules ?? t_activeRules;
                if (r != null)
                {
                    for (int i = 0; i < total; i++)
                    {
                        ushort cid = dst[i].CardDefId;
                        if (cid < r.Cards.Length && r.Cards[cid].IsShootTag)
                        {
                            s.Fool.Arrows[0] = 0;
                            s.ArrowCount = 1;
                            break;
                        }
                    }
                }
            }
        }

        s.DrawCount = (ushort)total;
        s.DiscardCount = 0;
        s.ShuffleEventCount++;
    }

    private static void MoveMatchingCardToFront(Span<HotCard> buf, ushort cardUid, ushort cardDefId, byte perfectFitEnchId)
    {
        int k = -1;
        if (cardUid != 0)
        {
            for (int j = 0; j < buf.Length; j++)
            {
                if (buf[j].CardUid == cardUid)
                {
                    k = j;
                    break;
                }
            }
        }
        else
        {
            for (int j = buf.Length - 1; j >= 0; j--)
            {
                if (buf[j].CardUid == 0 && buf[j].CardDefId == cardDefId && buf[j].EnchId == perfectFitEnchId)
                {
                    k = j;
                    break;
                }
            }
        }

        if (k > 0)
        {
            HotCard card = buf[k];
            buf.Slice(0, k).CopyTo(buf.Slice(1, k));
            buf[0] = card;
        }
    }

    [ThreadStatic]
    private static HotRules? t_sortRules;

    [ThreadStatic]
    private static HotColdSidecar? t_sortCold;

    private static readonly Comparison<HotCard> s_shuffleComparison = CompareShuffleCardsThreadStatic;

    private static void StableSortShuffleBuffer(
        HotExecScratch scratch,
        int count,
        in CombatHotState s,
        HotRules? rules,
        HotColdSidecar? cold)
    {
        // Match vanilla STS2 0.113.1 ListExtensions.StableShuffle -> List<CardModel>.Sort()
        // bit-for-bit, including .NET IntroSort's exact partitioning for count > 16 when
        // two cards compare equal under CardModel.CompareTo (e.g. a MusicBox Ethereal clone
        // and its original card in Discard). Static Comparison<HotCard> + [ThreadStatic]
        // context avoids struct IComparer boxing/delegate allocation (0 B alloc).
        HotRules? prevRules = t_sortRules;
        HotColdSidecar? prevCold = t_sortCold;
        t_sortRules = rules;
        t_sortCold = cold;
        try
        {
            scratch.ShuffleCards.AsSpan(0, count).Sort(s_shuffleComparison);
        }
        finally
        {
            t_sortRules = prevRules;
            t_sortCold = prevCold;
        }
    }

    private static int CompareShuffleCardsThreadStatic(HotCard a, HotCard b)
        => CompareShuffleCards(in a, in b, in Unsafe.NullRef<CombatHotState>(), t_sortRules, t_sortCold);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareShuffleCards(
        in HotCard a,
        in HotCard b,
        in CombatHotState s,
        HotRules? rules,
        HotColdSidecar? cold)
    {
        if (rules != null)
            return rules.CompareCardsForShuffle(in a, in b, in s, cold);

        int cmp = a.CardDefId.CompareTo(b.CardDefId);
        if (cmp != 0) return cmp;
        cmp = a.UpgradeLevel.CompareTo(b.UpgradeLevel);
        if (cmp != 0) return cmp;
        cmp = a.EnchId.CompareTo(b.EnchId);
        if (cmp != 0) return cmp;
        cmp = a.AfflId.CompareTo(b.AfflId);
        if (cmp != 0) return cmp;
        int costA = (a.Flags & HotCardFlags.CostsX) != 0 ? a.CapturedEnergy : HotCostModifiers.GetLocalEnergyCost(in a, cold);
        int costB = (b.Flags & HotCardFlags.CostsX) != 0 ? b.CapturedEnergy : HotCostModifiers.GetLocalEnergyCost(in b, cold);
        cmp = costA.CompareTo(costB);
        if (cmp != 0) return cmp;
        cmp = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (cmp != 0) return cmp;
        cmp = a.Var0Delta.CompareTo(b.Var0Delta);
        if (cmp != 0) return cmp;
        return a.Var1Delta.CompareTo(b.Var1Delta);
    }
}
