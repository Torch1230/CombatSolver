using System.Buffers;
using System.Runtime.CompilerServices;

namespace CombatSolver.HotSearch;

/// <summary>
/// Lean, zero-LINQ beam retention (~400 LOC replacing 18,481 LOC of legacy retention/cycle/ordered-mutation).
/// Single-pass counting-sort bucketing by <c>PotionCount</c> into pooled slices, followed by quota allocation
/// across Primary Top-K, orthogonal diversity lanes (Survival, Offense, Setup/Scaling), and a compact
/// 6-dimensional Pareto front — 100% deterministic.
/// </summary>
internal sealed class HotBeamRetention : IDisposable
{
    public const int MaxActionsPerTurn = 64;
    private const int MaxPotionBuckets = 4; // 0, 1, 2, 3+
    private const int MaxParetoScanCandidates = 128;

    private int[] _bucketBuffer;
    private int[] _scratchIndices;
    private byte[] _selectedMarks;
    private int _capacity;
    private bool _disposed;

    public HotBeamRetention(int initialCapacity = 4096)
    {
        _capacity = initialCapacity;
        _bucketBuffer = ArrayPool<int>.Shared.Rent(initialCapacity);
        _scratchIndices = ArrayPool<int>.Shared.Rent(initialCapacity);
        _selectedMarks = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>
    /// Prunes <paramref name="candidates"/> into <paramref name="output"/> up to <paramref name="beamWidth"/>.
    /// Guarantees deterministic ordering independent of worker thread scheduling.
    /// </summary>
    public int PruneLayer(
        ReadOnlySpan<int> candidates,
        HotNodeArena arena,
        int beamWidth,
        Span<int> output)
    {
        int n = candidates.Length;
        if (n == 0 || beamWidth <= 0)
            return 0;

        int limit = Math.Min(beamWidth, output.Length);
        if (n <= limit)
        {
            candidates.CopyTo(output);
            SortByScoreDeterministic(output[..n], arena);
            return n;
        }

        EnsureCapacity(n);

        // 1. Single-pass counting sort bucketing by PotionCount (0, 1, 2, 3+) — ZERO LINQ GroupBy.
        Span<int> bucketCounts = stackalloc int[MaxPotionBuckets];
        bucketCounts.Clear();
        for (int i = 0; i < n; i++)
        {
            byte pCount = arena.GetRef(candidates[i]).PotionCount;
            int b = pCount >= MaxPotionBuckets ? MaxPotionBuckets - 1 : pCount;
            bucketCounts[b]++;
        }

        Span<int> bucketStarts = stackalloc int[MaxPotionBuckets];
        Span<int> bucketWrite = stackalloc int[MaxPotionBuckets];
        int activeBuckets = 0;
        int offset = 0;
        for (int b = 0; b < MaxPotionBuckets; b++)
        {
            bucketStarts[b] = offset;
            bucketWrite[b] = offset;
            offset += bucketCounts[b];
            if (bucketCounts[b] > 0)
                activeBuckets++;
        }

        for (int i = 0; i < n; i++)
        {
            int nodeId = candidates[i];
            byte pCount = arena.GetRef(nodeId).PotionCount;
            int b = pCount >= MaxPotionBuckets ? MaxPotionBuckets - 1 : pCount;
            _bucketBuffer[bucketWrite[b]++] = nodeId;
        }

        // 2. Allocate beam width quotas across active potion buckets.
        Span<int> bucketQuotas = stackalloc int[MaxPotionBuckets];
        AllocateBucketQuotas(bucketCounts, activeBuckets, limit, bucketQuotas);

        // 3. Select diverse survivors inside each bucket into output.
        int totalSelected = 0;
        for (int b = 0; b < MaxPotionBuckets; b++)
        {
            int count = bucketCounts[b];
            int quota = bucketQuotas[b];
            if (count == 0 || quota <= 0)
                continue;

            Span<int> slice = _bucketBuffer.AsSpan(bucketStarts[b], count);
            int picked = SelectFromBucket(slice, arena, quota, output.Slice(totalSelected, limit - totalSelected));
            totalSelected += picked;
        }

        // 4. Deterministic final sort so subsequent expansion wave order is invariant.
        SortByScoreDeterministic(output[..totalSelected], arena);
        return totalSelected;
    }

    private static void AllocateBucketQuotas(
        ReadOnlySpan<int> counts,
        int activeBuckets,
        int totalQuota,
        Span<int> quotas)
    {
        quotas.Clear();
        if (activeBuckets <= 0 || totalQuota <= 0)
            return;

        if (activeBuckets == 1)
        {
            for (int b = 0; b < MaxPotionBuckets; b++)
            {
                if (counts[b] > 0)
                {
                    quotas[b] = Math.Min(counts[b], totalQuota);
                    return;
                }
            }
        }

        int remaining = totalQuota;

        // Guarantee strong preservation for 0-potion routes (at least 60% of beam when available).
        if (counts[0] > 0)
        {
            int zeroTarget = Math.Max(1, (totalQuota * 60) / 100);
            int take = Math.Min(counts[0], zeroTarget);
            quotas[0] = take;
            remaining -= take;
        }

        // Distribute remaining quota proportionally across remaining non-empty buckets (min 2 each if possible).
        int otherBuckets = 0;
        for (int b = 1; b < MaxPotionBuckets; b++)
        {
            if (counts[b] > 0)
                otherBuckets++;
        }

        if (otherBuckets > 0 && remaining > 0)
        {
            int share = Math.Max(1, remaining / otherBuckets);
            for (int b = 1; b < MaxPotionBuckets && remaining > 0; b++)
            {
                if (counts[b] == 0)
                    continue;
                int take = Math.Min(counts[b], Math.Min(share, remaining));
                quotas[b] = take;
                remaining -= take;
            }
        }

        // Spill any leftover slots round-robin starting from lowest potion count.
        while (remaining > 0)
        {
            bool progressed = false;
            for (int b = 0; b < MaxPotionBuckets && remaining > 0; b++)
            {
                if (quotas[b] < counts[b])
                {
                    quotas[b]++;
                    remaining--;
                    progressed = true;
                }
            }
            if (!progressed)
                break;
        }
    }

    private int SelectFromBucket(
        Span<int> bucketNodes,
        HotNodeArena arena,
        int quota,
        Span<int> dst)
    {
        int count = bucketNodes.Length;
        if (count <= quota)
        {
            bucketNodes.CopyTo(dst);
            return count;
        }

        // Sort bucket by Score descending once -> index 0..count-1 is canonical primary rank.
        SortByScoreDeterministic(bucketNodes, arena);

        Span<byte> marks = _selectedMarks.AsSpan(0, count);
        marks.Clear();

        int selectedCount = 0;

        // Lane budgets (~55% Primary, ~15% Survival, ~12% Offense, ~10% Setup, ~8% 6-dim Pareto)
        int primaryQuota = Math.Max(1, (quota * 55) / 100);
        int survivalQuota = quota >= 6 ? Math.Max(1, (quota * 15) / 100) : 0;
        int offenseQuota = quota >= 8 ? Math.Max(1, (quota * 12) / 100) : 0;
        int setupQuota = quota >= 10 ? Math.Max(1, (quota * 10) / 100) : 0;
        int paretoQuota = quota >= 12 ? Math.Max(1, quota - primaryQuota - survivalQuota - offenseQuota - setupQuota) : 0;

        // 1. Primary Top-K by Score
        for (int i = 0; i < count && selectedCount < primaryQuota; i++)
        {
            marks[i] = 1;
            dst[selectedCount++] = bucketNodes[i];
        }

        // 2. Survival lane (min HpLost -> max Block -> min EnemyHp -> max Score)
        if (survivalQuota > 0 && selectedCount < quota)
        {
            selectedCount += PickTopByCustomOrder(
                bucketNodes,
                marks,
                arena,
                Math.Min(survivalQuota, quota - selectedCount),
                dst[selectedCount..],
                CompareSurvival);
        }

        // 3. Offense / Fastest-Kills lane (min AliveEnemyCount -> min EnemyHp -> max Score)
        if (offenseQuota > 0 && selectedCount < quota)
        {
            selectedCount += PickTopByCustomOrder(
                bucketNodes,
                marks,
                arena,
                Math.Min(offenseQuota, quota - selectedCount),
                dst[selectedCount..],
                CompareOffense);
        }

        // 4. Setup / Scaling lane (max SetupScore -> min HpLost -> max Score)
        if (setupQuota > 0 && selectedCount < quota)
        {
            selectedCount += PickTopByCustomOrder(
                bucketNodes,
                marks,
                arena,
                Math.Min(setupQuota, quota - selectedCount),
                dst[selectedCount..],
                CompareSetup);
        }

        // 5. Compact 6-dim Pareto front over top candidates
        if (paretoQuota > 0 && selectedCount < quota)
        {
            int scanLen = Math.Min(count, MaxParetoScanCandidates);
            int paretoBudget = Math.Min(paretoQuota, quota - selectedCount);
            int paretoAdded = 0;

            for (int i = 0; i < scanLen && paretoAdded < paretoBudget; i++)
            {
                if (marks[i] != 0)
                    continue;

                ref readonly HotSearchNode cand = ref arena.GetRef(bucketNodes[i]);
                if (IsDominatedInSixDimPareto(in cand, bucketNodes, scanLen, i, arena))
                    continue;

                marks[i] = 1;
                dst[selectedCount++] = bucketNodes[i];
                paretoAdded++;
            }
        }

        // 6. Spill any unused diversity quota back into Primary Top-K by Score
        for (int i = 0; i < count && selectedCount < quota; i++)
        {
            if (marks[i] != 0)
                continue;
            marks[i] = 1;
            dst[selectedCount++] = bucketNodes[i];
        }

        return selectedCount;
    }

    private delegate int NodeIndexComparison(in HotSearchNode a, in HotSearchNode b);

    /// <summary>
    /// Selects up to <paramref name="take"/> unmarked indices using partial selection (O(count * take) for small take)
    /// — avoids full-array sorts for tiny diversity lanes!
    /// </summary>
    private static int PickTopByCustomOrder(
        ReadOnlySpan<int> bucketNodes,
        Span<byte> marks,
        HotNodeArena arena,
        int take,
        Span<int> dst,
        NodeIndexComparison compare)
    {
        int count = bucketNodes.Length;
        int added = 0;

        for (int round = 0; round < take; round++)
        {
            int bestIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (marks[i] != 0)
                    continue;
                if (bestIdx < 0)
                {
                    bestIdx = i;
                    continue;
                }

                ref readonly HotSearchNode cand = ref arena.GetRef(bucketNodes[i]);
                ref readonly HotSearchNode best = ref arena.GetRef(bucketNodes[bestIdx]);
                int cmp = compare(in cand, in best);
                if (cmp < 0 || (cmp == 0 && TieBreakDeterministic(in cand, bucketNodes[i], in best, bucketNodes[bestIdx]) < 0))
                    bestIdx = i;
            }

            if (bestIdx < 0)
                break;

            marks[bestIdx] = 1;
            dst[added++] = bucketNodes[bestIdx];
        }

        return added;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSurvival(in HotSearchNode a, in HotSearchNode b)
    {
        int cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
        if (cmp != 0) return cmp;
        cmp = a.HpLost.CompareTo(b.HpLost);
        if (cmp != 0) return cmp;
        cmp = b.PlayerHp.CompareTo(a.PlayerHp);
        if (cmp != 0) return cmp;
        cmp = b.PlayerBlock.CompareTo(a.PlayerBlock);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        return b.Score.CompareTo(a.Score);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareOffense(in HotSearchNode a, in HotSearchNode b)
    {
        int cmp = a.AliveEnemyCount.CompareTo(b.AliveEnemyCount);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        cmp = a.HpLost.CompareTo(b.HpLost);
        if (cmp != 0) return cmp;
        return b.Score.CompareTo(a.Score);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSetup(in HotSearchNode a, in HotSearchNode b)
    {
        int cmp = b.SetupScore.CompareTo(a.SetupScore);
        if (cmp != 0) return cmp;
        cmp = a.HpLost.CompareTo(b.HpLost);
        if (cmp != 0) return cmp;
        return b.Score.CompareTo(a.Score);
    }

    /// <summary>
    /// 6-dimensional Pareto dominance check across:
    /// (1) HpLost asc, (2) EnemyHp asc, (3) AliveEnemyCount asc,
    /// (4) SetupScore desc, (5) PotionStrategicCost asc, (6) Score desc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDominatedInSixDimPareto(
        in HotSearchNode candidate,
        ReadOnlySpan<int> pool,
        int scanLen,
        int candidateIdx,
        HotNodeArena arena)
    {
        for (int j = 0; j < scanLen; j++)
        {
            if (j == candidateIdx)
                continue;
            ref readonly HotSearchNode other = ref arena.GetRef(pool[j]);
            if (other.HpLost <= candidate.HpLost
                && other.EnemyHp <= candidate.EnemyHp
                && other.AliveEnemyCount <= candidate.AliveEnemyCount
                && other.SetupScore >= candidate.SetupScore
                && other.PotionStrategicCost <= candidate.PotionStrategicCost
                && other.Score >= candidate.Score
                && (other.HpLost < candidate.HpLost
                    || other.EnemyHp < candidate.EnemyHp
                    || other.AliveEnemyCount < candidate.AliveEnemyCount
                    || other.SetupScore > candidate.SetupScore
                    || other.PotionStrategicCost < candidate.PotionStrategicCost
                    || other.Score > candidate.Score))
            {
                return true;
            }
        }
        return false;
    }

    private static void SortByScoreDeterministic(Span<int> nodeIds, HotNodeArena arena)
    {
        if (nodeIds.Length <= 1)
            return;

        nodeIds.Sort((aId, bId) =>
        {
            ref readonly HotSearchNode a = ref arena.GetRef(aId);
            ref readonly HotSearchNode b = ref arena.GetRef(bId);
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0)
                return cmp;
            return TieBreakDeterministic(in a, aId, in b, bId);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TieBreakDeterministic(in HotSearchNode a, int aId, in HotSearchNode b, int bId)
    {
        int cmp = a.HpLost.CompareTo(b.HpLost);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        cmp = a.Hash.Lo.CompareTo(b.Hash.Lo);
        if (cmp != 0) return cmp;
        cmp = a.Hash.Hi.CompareTo(b.Hash.Hi);
        if (cmp != 0) return cmp;
        return aId.CompareTo(bId);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _capacity)
            return;

        int next = _capacity;
        while (next < required)
            next <<= 1;

        ArrayPool<int>.Shared.Return(_bucketBuffer, clearArray: false);
        ArrayPool<int>.Shared.Return(_scratchIndices, clearArray: false);
        ArrayPool<byte>.Shared.Return(_selectedMarks, clearArray: false);

        _bucketBuffer = ArrayPool<int>.Shared.Rent(next);
        _scratchIndices = ArrayPool<int>.Shared.Rent(next);
        _selectedMarks = ArrayPool<byte>.Shared.Rent(next);
        _capacity = next;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_bucketBuffer.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_bucketBuffer, clearArray: false);
            _bucketBuffer = [];
        }
        if (_scratchIndices.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_scratchIndices, clearArray: false);
            _scratchIndices = [];
        }
        if (_selectedMarks.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_selectedMarks, clearArray: false);
            _selectedMarks = [];
        }
    }
}
