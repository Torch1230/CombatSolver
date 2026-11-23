using System.Buffers;
using System.Runtime.CompilerServices;

namespace CombatSolver.HotSearch;

/// <summary>
/// Lean, zero-LINQ beam retention with deterministic counting-sort bucketing by <c>PotionCount</c>,
/// potion-slot/cost-tier diversity inside potion buckets (protecting scaling/draw/combo potions like
/// <c>StrengthPotion</c>/<c>SwiftPotion</c>/<c>Duplicator</c>/<c>FruitJuice</c> from being crushed on Turn 1 by
/// <c>BlockPotion</c>), orthogonal diversity lanes (Survival, Offense, Setup/Scaling, mid-turn Resource/Combo),
/// and a compact 6-dimensional Pareto front — 100% deterministic.
/// </summary>
internal sealed class HotBeamRetention : IDisposable
{
    public const int MaxActionsPerTurn = 64;
    public const int MaxPotionBuckets = 4; // 0, 1, 2, 3+
    private const int MaxParetoScanCandidates = 128;
    private const int MaxDistinctPotionSignatures = 16;

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
        Span<int> output,
        bool isTurnBoundary = true,
        bool preferPotionBuckets = false)
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
        AllocateBucketQuotas(bucketCounts, activeBuckets, limit, preferPotionBuckets, bucketQuotas);

        // 3. Select diverse survivors inside each bucket into output.
        int totalSelected = 0;
        for (int b = 0; b < MaxPotionBuckets; b++)
        {
            int count = bucketCounts[b];
            int quota = bucketQuotas[b];
            if (count == 0 || quota <= 0)
                continue;

            Span<int> slice = _bucketBuffer.AsSpan(bucketStarts[b], count);
            int picked = SelectFromBucket(
                slice,
                arena,
                quota,
                bucketIndex: b,
                isTurnBoundary,
                preferPotionBuckets,
                output.Slice(totalSelected, limit - totalSelected));
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
        bool preferPotionBuckets,
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

        // Under Smart Stage 2 (where Stage 1 already searched 0-paid-potion deeply), preserve ~35% for 0-potion
        // prefixes so late-turn potions can still emerge, while giving 65% to 1+/2+ potion routes!
        // Under single-pass/Stage 1 (0-cost potions), preserve 60% for 0-potion routes.
        if (counts[0] > 0)
        {
            int zeroPct = preferPotionBuckets ? 35 : 60;
            int zeroTarget = Math.Max(1, (totalQuota * zeroPct) / 100);
            int take = Math.Min(counts[0], zeroTarget);
            quotas[0] = take;
            remaining -= take;
        }

        int otherBuckets = 0;
        for (int b = 1; b < MaxPotionBuckets; b++)
        {
            if (counts[b] > 0)
                otherBuckets++;
        }

        if (otherBuckets > 0 && remaining > 0)
        {
            if (preferPotionBuckets && counts[1] > 0 && otherBuckets > 1)
            {
                int onePotTarget = Math.Max(1, (remaining * 55) / 100);
                int take1 = Math.Min(counts[1], onePotTarget);
                quotas[1] = take1;
                remaining -= take1;
                otherBuckets--;
            }

            if (otherBuckets > 0 && remaining > 0)
            {
                int share = Math.Max(1, remaining / otherBuckets);
                for (int b = 1; b < MaxPotionBuckets && remaining > 0; b++)
                {
                    if (counts[b] == 0 || quotas[b] > 0)
                        continue;
                    int take = Math.Min(counts[b], Math.Min(share, remaining));
                    quotas[b] = take;
                    remaining -= take;
                }
            }
        }

        int startBucket = preferPotionBuckets ? 1 : 0;
        while (remaining > 0)
        {
            bool progressed = false;
            for (int step = 0; step < MaxPotionBuckets && remaining > 0; step++)
            {
                int b = (startBucket + step) % MaxPotionBuckets;
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
        int bucketIndex,
        bool isTurnBoundary,
        bool preferPotionBuckets,
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

        // 0. Potion-slot & cost-tier diversity inside potion buckets (b >= 1):
        // Guarantee top representatives for each distinct (PotionMask, CostTier) so high-tier scaling/draw/combo
        // potions (SwiftPotion / Duplicator / GamblersBrew / StrengthPotion / FruitJuice / BloodPotion) are NOT
        // wiped out on Turn 1 by immediate BlockPotion / FirePotion!
        int potionDiversityQuota = 0;
        if (bucketIndex >= 1 && quota >= 4)
        {
            potionDiversityQuota = preferPotionBuckets
                ? Math.Min(MaxDistinctPotionSignatures, Math.Max(2, (quota * 28) / 100))
                : Math.Min(MaxDistinctPotionSignatures, Math.Max(1, quota / 5));

            Span<uint> seenKeys = stackalloc uint[MaxDistinctPotionSignatures];
            int seenCount = 0;

            // Round 1: pick best-Score representative for each distinct (PotionMask, PotionStrategicCost) signature.
            for (int i = 0; i < count && selectedCount < potionDiversityQuota && seenCount < MaxDistinctPotionSignatures; i++)
            {
                ref readonly HotSearchNode node = ref arena.GetRef(bucketNodes[i]);
                uint sig = ((uint)node.PotionMask << 16) | (ushort)node.PotionStrategicCost;
                bool seen = false;
                for (int s = 0; s < seenCount; s++)
                {
                    if (seenKeys[s] == sig)
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen)
                    continue;

                seenKeys[seenCount++] = sig;
                marks[i] = 1;
                dst[selectedCount++] = bucketNodes[i];
            }

            // Round 2: if multiple distinct signatures exist and budget remains, grant a second representative per signature.
            if (seenCount > 1 && selectedCount < potionDiversityQuota)
            {
                for (int s = 0; s < seenCount && selectedCount < potionDiversityQuota; s++)
                {
                    uint targetSig = seenKeys[s];
                    for (int i = 0; i < count; i++)
                    {
                        if (marks[i] != 0)
                            continue;
                        ref readonly HotSearchNode node = ref arena.GetRef(bucketNodes[i]);
                        uint sig = ((uint)node.PotionMask << 16) | (ushort)node.PotionStrategicCost;
                        if (sig == targetSig)
                        {
                            marks[i] = 1;
                            dst[selectedCount++] = bucketNodes[i];
                            break;
                        }
                    }
                }
            }
        }

        int remainingForLanes = quota - selectedCount;
        if (remainingForLanes <= 0)
            return selectedCount;

        // Lane budgets over remaining quota:
        // - Turn boundary: ~48% Primary, ~18% Survival, ~12% Offense, ~14% Setup, ~8% 6-dim Pareto
        // - Mid-turn:      ~44% Primary, ~14% Survival, ~10% Offense, ~12% Setup, ~12% Resource/Combo, ~8% Pareto
        int primaryQuota = Math.Max(1, (remainingForLanes * (isTurnBoundary ? 48 : 44)) / 100);
        int survivalQuota = remainingForLanes >= 6 ? Math.Max(1, (remainingForLanes * (isTurnBoundary ? 18 : 14)) / 100) : 0;
        int offenseQuota = remainingForLanes >= 8 ? Math.Max(1, (remainingForLanes * (isTurnBoundary ? 12 : 10)) / 100) : 0;
        int setupQuota = remainingForLanes >= 10 ? Math.Max(1, (remainingForLanes * (isTurnBoundary ? 14 : 12)) / 100) : 0;
        int comboQuota = !isTurnBoundary && remainingForLanes >= 10 ? Math.Max(1, (remainingForLanes * 12) / 100) : 0;
        int paretoQuota = remainingForLanes >= 12
            ? Math.Max(1, remainingForLanes - primaryQuota - survivalQuota - offenseQuota - setupQuota - comboQuota)
            : 0;

        // 1. Primary Top-K by Score
        int primaryAdded = 0;
        for (int i = 0; i < count && primaryAdded < primaryQuota && selectedCount < quota; i++)
        {
            if (marks[i] != 0)
                continue;
            marks[i] = 1;
            dst[selectedCount++] = bucketNodes[i];
            primaryAdded++;
        }

        // 2. Survival lane (min DeathSave -> min net HpDeficit incl. MaxHpDelta -> max PlayerHp -> max Block -> max Score)
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

        // 3. Offense / Fastest-Kills lane (min Alive+Reviving -> min EnemyHp -> min HpLost -> max Score)
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

        // 4. Setup / Scaling lane (max SetupScore -> min net HpDeficit -> max Score)
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

        // 5. Mid-turn Resource / Combo-Generation lane (preserves BundleOfJoy/Discovery/Dredge/SwiftPotion chains!)
        if (comboQuota > 0 && selectedCount < quota)
        {
            selectedCount += PickTopByCustomOrder(
                bucketNodes,
                marks,
                arena,
                Math.Min(comboQuota, quota - selectedCount),
                dst[selectedCount..],
                CompareMidTurnCombo);
        }

        // 6. Compact 6-dim Pareto front over top candidates
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

        // 7. Spill any unused diversity quota back into Primary Top-K by Score
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
    private static int EffectiveNetHpLoss(in HotSearchNode n)
        => n.HpLost - n.HpRecovered + (n.PlayerMaxHpDelta < 0 ? n.PlayerMaxHpDelta * 2 : n.PlayerMaxHpDelta);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSurvival(in HotSearchNode a, in HotSearchNode b)
    {
        int cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
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
        int aRem = a.AliveEnemyCount + a.RevivingEnemyCount;
        int bRem = b.AliveEnemyCount + b.RevivingEnemyCount;
        int cmp = aRem.CompareTo(bRem);
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
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        return b.Score.CompareTo(a.Score);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareMidTurnCombo(in HotSearchNode a, in HotSearchNode b)
    {
        int aPotential = a.PlayableHandCount * 2 + Math.Min((int)a.Energy, 6) * 3;
        int bPotential = b.PlayableHandCount * 2 + Math.Min((int)b.Energy, 6) * 3;
        int cmp = bPotential.CompareTo(aPotential);
        if (cmp != 0) return cmp;
        cmp = b.SetupScore.CompareTo(a.SetupScore);
        if (cmp != 0) return cmp;
        return b.Score.CompareTo(a.Score);
    }

    /// <summary>
    /// 6-dimensional Pareto dominance check across:
    /// (1) Effective net HpLoss asc (including MaxHpGain), (2) EnemyHp asc, (3) Alive+Reviving asc,
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
        int candNetLoss = EffectiveNetHpLoss(in candidate);
        int candRem = candidate.AliveEnemyCount + candidate.RevivingEnemyCount;

        for (int j = 0; j < scanLen; j++)
        {
            if (j == candidateIdx)
                continue;
            ref readonly HotSearchNode other = ref arena.GetRef(pool[j]);
            int otherNetLoss = EffectiveNetHpLoss(in other);
            int otherRem = other.AliveEnemyCount + other.RevivingEnemyCount;
            if (otherNetLoss <= candNetLoss
                && other.EnemyHp <= candidate.EnemyHp
                && otherRem <= candRem
                && other.SetupScore >= candidate.SetupScore
                && other.PotionStrategicCost <= candidate.PotionStrategicCost
                && other.Score >= candidate.Score
                && (otherNetLoss < candNetLoss
                    || other.EnemyHp < candidate.EnemyHp
                    || otherRem < candRem
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
