using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CombatSolver.HotKernel;

/// <summary>
/// Contiguous 5-pile operations over CombatHotState.Cards laid out as [Hand | Draw | Discard | Exhaust | Play].
/// </summary>
internal static class HotPiles
{
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

        if (total > 0)
        {
            Span<HotCard> dst = MemoryMarshal.CreateSpan(ref s.Cards[0], s.TotalCardCount).Slice(drawStart, total);
            buf.CopyTo(dst);
        }

        s.DrawCount = (ushort)total;
        s.DiscardCount = 0;
        s.ShuffleEventCount++;
    }

    private static void StableSortShuffleBuffer(
        HotExecScratch scratch,
        int count,
        in CombatHotState s,
        HotRules? rules,
        HotColdSidecar? cold)
    {
        HotCard[] primary = scratch.ShuffleCards;
        if (count <= 32)
        {
            // Insertion sort: strictly stable, zero-alloc, fast for typical STS2 deck sizes.
            for (int i = 1; i < count; i++)
            {
                HotCard key = primary[i];
                int j = i - 1;
                while (j >= 0 && CompareShuffleCards(in primary[j], in key, in s, rules, cold) > 0)
                {
                    primary[j + 1] = primary[j];
                    j--;
                }
                primary[j + 1] = key;
            }
            return;
        }

        // Bottom-up iterative stable merge sort using scratch.ShuffleScratch (O(n log n), zero heap alloc).
        HotCard[] src = primary;
        HotCard[] dst = scratch.ShuffleScratch;

        for (int width = 1; width < count; width <<= 1)
        {
            for (int left = 0; left < count; left += width << 1)
            {
                int mid = Math.Min(left + width, count);
                int right = Math.Min(left + (width << 1), count);
                int i = left;
                int j = mid;
                int k = left;

                while (i < mid && j < right)
                {
                    if (CompareShuffleCards(in src[i], in src[j], in s, rules, cold) <= 0)
                        dst[k++] = src[i++];
                    else
                        dst[k++] = src[j++];
                }
                while (i < mid)
                    dst[k++] = src[i++];
                while (j < right)
                    dst[k++] = src[j++];
            }
            (src, dst) = (dst, src);
        }

        if (!ReferenceEquals(src, primary))
            src.AsSpan(0, count).CopyTo(primary.AsSpan(0, count));
    }

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
