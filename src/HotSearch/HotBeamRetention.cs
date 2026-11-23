using System.Buffers;
using System.Runtime.CompilerServices;

namespace CombatSolver.HotSearch;

/// <summary>
/// Zero-LINQ, zero-allocation single-tree domain-agnostic MAP-Elites beam retention:
/// - Deterministic counting-sort bucketing by <c>PotionCount</c> (<c>0, 1, 2, 3+</c>) with non-starving
///   potion-signature diversity;
/// - 5 orthogonal domain-agnostic behavioral niches:
///   (1) Primary / Rollout-Blended Value (~35%),
///   (2) Survival / HP Preservation (~18%: min EffectiveNetHpLoss, max PlayerHp + EffectiveDefense),
///   (3) Tempo / Kill Speed (~15%: min Alive+Reviving, min EnemyHp),
///   (4) Generic Persistent Board Investment (~18%: unclamped domain-agnostic state delta, zero card IDs),
///   (5) Optionality + True Full-Pool Pareto (~14%: max Energy+Stars+PlayableHand+DrawNextTurn, and
///       Pareto scanning the union of top-32 candidates from EACH niche rather than only Top-128 by raw Score).
/// 100% deterministic across DOP=1 and DOP=N.
/// </summary>
internal sealed class HotBeamRetention : IDisposable
{
    public const int MaxActionsPerTurn = 64;
    public const int MaxPotionBuckets = 4; // 0, 1, 2, 3+
    private const int PerNicheParetoTopK = 32;
    private const int NicheSlotCount = 6; // 0=Primary, 1=Survival, 2=Tempo, 3=BoardInvestment, 4=Optionality, 5=PureRollout
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
    /// When <paramref name="useRolloutScores"/> is <c>true</c> (Stage 8C turn-boundary rollout prune),
    /// Primary ranking uses <see cref="HotSearchNode.EffectiveTurnBoundaryScore"/>, protects a pure
    /// top-<see cref="HotSearchNode.RolloutScore"/> slice inside Niche 1, and feeds rollout leaders into
    /// full-pool Pareto.
    /// </summary>
    public int PruneLayer(
        ReadOnlySpan<int> candidates,
        HotNodeArena arena,
        int beamWidth,
        Span<int> output,
        bool isTurnBoundary = true,
        bool preferPotionBuckets = false,
        bool useRolloutScores = false)
    {
        int n = candidates.Length;
        if (n == 0 || beamWidth <= 0)
            return 0;

        int limit = Math.Min(beamWidth, output.Length);
        if (n <= limit)
        {
            candidates.CopyTo(output);
            SortByPrimaryScoreDeterministic(output[..n], arena, useRolloutScores);
            return n;
        }

        EnsureCapacity(Math.Max(n, limit + PerNicheParetoTopK));

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

        // 3. Select diverse MAP-Elites survivors inside each bucket into output.
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
                useRolloutScores,
                output.Slice(totalSelected, limit - totalSelected));
            totalSelected += picked;
        }

        // 4. Deterministic final sort so subsequent expansion wave order is invariant.
        SortByPrimaryScoreDeterministic(output[..totalSelected], arena, useRolloutScores);
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
        // prefixes so late-turn potions can still emerge, while giving 65% to 1+/2+ potion routes.
        // Under Stage 1 / single-beam, preserve 60% for 0-potion routes.
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
        bool useRolloutScores,
        Span<int> dst)
    {
        int count = bucketNodes.Length;
        if (count <= quota)
        {
            bucketNodes.CopyTo(dst);
            return count;
        }

        // Canonical primary ranking: index 0..count-1 is sorted descending by PrimaryScore.
        SortByPrimaryScoreDeterministic(bucketNodes, arena, useRolloutScores);

        // Mark values: 0 = unselected, 1 = selected into dst, 2 = queued in full-pool Pareto candidate set.
        Span<byte> marks = _selectedMarks.AsSpan(0, count);
        marks.Clear();

        int selectedCount = 0;

        // 0. Non-starving potion-signature diversity inside potion buckets (b >= 1):
        // Cap strictly so at least 5 slots (and >= 75% of larger buckets) ALWAYS remain for the 5 MAP-Elites niches!
        if (bucketIndex >= 1 && quota >= 6)
        {
            int pctCap = preferPotionBuckets ? (quota * 25) / 100 : (quota * 15) / 100;
            int potionDiversityQuota = Math.Min(
                MaxDistinctPotionSignatures,
                Math.Max(1, Math.Min(pctCap, quota - 5)));

            Span<uint> seenKeys = stackalloc uint[MaxDistinctPotionSignatures];
            int seenCount = 0;

            // Round 1: pick best-PrimaryScore representative for each distinct (PotionMask, PotionStrategicCost).
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

            // Round 2 (on wide buckets with multiple potion signatures): protect 2nd best-PrimaryScore rep per signature.
            if (quota >= 16 && seenCount > 1 && selectedCount < potionDiversityQuota)
            {
                for (int s = 0; s < seenCount && selectedCount < potionDiversityQuota; s++)
                {
                    uint targetSig = seenKeys[s];
                    for (int i = 0; i < count; i++)
                    {
                        if (marks[i] != 0)
                            continue;
                        ref readonly HotSearchNode cand = ref arena.GetRef(bucketNodes[i]);
                        uint sig = ((uint)cand.PotionMask << 16) | (ushort)cand.PotionStrategicCost;
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

        // 5 Orthogonal MAP-Elites Niche Budgets (~35% Primary, ~18% Survival, ~15% Tempo, ~18% BoardInvestment, ~14% Optionality+Pareto).
        // Narrow buckets (R = 2..5) still guarantee Survival & BoardInvestment so scaling/survival NEVER starve!
        ComputeNicheQuotas(
            remainingForLanes,
            isTurnBoundary,
            out int primaryQuota,
            out int survivalQuota,
            out int tempoQuota,
            out int investmentQuota,
            out int optionalityQuota,
            out int paretoQuota);

        // Stack-allocated per-niche top-32 runners-up for true full-pool Pareto (6 * 32 = 192 ints = 768 B, 0 heap alloc).
        Span<int> nichePareto = stackalloc int[NicheSlotCount * PerNicheParetoTopK];
        Span<int> nicheParetoCounts = stackalloc int[NicheSlotCount];
        nicheParetoCounts.Clear();

        bool hasRolloutCandidates = useRolloutScores && HasAnyRolloutCandidate(bucketNodes, arena);
        bool hasChoiceLookaheadCandidates = !isTurnBoundary
            && !hasRolloutCandidates
            && HasAnyChoiceLookaheadCandidate(bucketNodes, arena);

        // Niche 1 — Primary Value (~35%; when useRolloutScores is active, splits ~70% Blended / ~30% Pure Rollout;
        // mid-turn with delayed-payoff choice lookahead, protects ~20% of Niche 1 for top choice-lookahead lines).
        int rolloutPureQuota = (hasRolloutCandidates && primaryQuota >= 3)
            ? Math.Max(1, (primaryQuota * 30) / 100)
            : 0;
        int choiceLookaheadQuota = (hasChoiceLookaheadCandidates && primaryQuota >= 3)
            ? Math.Max(1, (primaryQuota * 20) / 100)
            : 0;
        int blendedPrimaryQuota = primaryQuota - rolloutPureQuota - choiceLookaheadQuota;

        int primaryAdded = 0;
        int primaryParetoCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (marks[i] != 0)
                continue;
            if (primaryAdded < blendedPrimaryQuota && selectedCount < quota)
            {
                marks[i] = 1;
                dst[selectedCount++] = bucketNodes[i];
                primaryAdded++;
            }
            else if (primaryParetoCount < PerNicheParetoTopK)
            {
                nichePareto[primaryParetoCount++] = i;
            }
            else
            {
                break;
            }
        }
        nicheParetoCounts[0] = primaryParetoCount;

        if (hasRolloutCandidates && (rolloutPureQuota > 0 || paretoQuota > 0))
        {
            int takeRollout = Math.Min(rolloutPureQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                takeRollout,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(5 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[5],
                new PureRolloutComparer(useRolloutScores));
        }
        else if (hasChoiceLookaheadCandidates && (choiceLookaheadQuota > 0 || paretoQuota > 0))
        {
            int takeLookahead = Math.Min(choiceLookaheadQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                takeLookahead,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(5 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[5],
                new ChoiceLookaheadComparer(useRolloutScores));
        }

        // Niche 2 — Survival / HP Preservation (~18%: min DeathSave, min EffectiveNetHpLoss, max PlayerHp + EffectiveDefense).
        if ((survivalQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(survivalQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(1 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[1],
                new SurvivalComparer(useRolloutScores));
        }

        // Niche 3 — Tempo / Kill Speed (~15%: min Alive+Reviving, min EnemyHp).
        if ((tempoQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(tempoQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(2 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[2],
                new TempoComparer(useRolloutScores));
        }

        // Niche 4 — Generic Persistent Board Investment (~18%: unclamped domain-agnostic state delta, ZERO card IDs).
        if ((investmentQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(investmentQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(3 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[3],
                new BoardInvestmentComparer(useRolloutScores));
        }

        // Niche 5a — Optionality (max Energy + Stars + PlayableHandCount + DrawNextTurn).
        if ((optionalityQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(optionalityQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(4 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[4],
                new OptionalityComparer(useRolloutScores));
        }

        // Niche 5b — True Full-Pool Pareto scanning top-32 from EACH niche (round-robin interleaved so
        // deferred BoardInvestment / Survival / Tempo / Optionality lines ranked #150..#1000 by raw Score are visible!).
        if (paretoQuota > 0 && selectedCount < quota)
        {
            Span<int> paretoPool = stackalloc int[NicheSlotCount * PerNicheParetoTopK];
            int paretoPoolCount = 0;

            // Round-robin interleave order: BoardInvestment(3), Survival(1), Tempo(2), Optionality(4), PureRollout(5), Primary(0).
            ReadOnlySpan<int> nicheOrder = [3, 1, 2, 4, 5, 0];
            for (int rank = 0; rank < PerNicheParetoTopK; rank++)
            {
                for (int k = 0; k < nicheOrder.Length; k++)
                {
                    int slot = nicheOrder[k];
                    if (rank >= nicheParetoCounts[slot])
                        continue;
                    int idx = nichePareto[slot * PerNicheParetoTopK + rank];
                    if (marks[idx] != 0)
                        continue;
                    marks[idx] = 2;
                    paretoPool[paretoPoolCount++] = idx;
                }
            }

            int paretoBudget = Math.Min(paretoQuota, quota - selectedCount);
            int paretoAdded = 0;

            for (int p = 0; p < paretoPoolCount && paretoAdded < paretoBudget; p++)
            {
                int candIdx = paretoPool[p];
                int candNodeId = bucketNodes[candIdx];
                ref readonly HotSearchNode cand = ref arena.GetRef(candNodeId);

                if (IsDominatedInFullPoolPareto(
                        in cand,
                        bucketNodes,
                        dst[..selectedCount],
                        paretoPool[..paretoPoolCount],
                        p,
                        arena,
                        useRolloutScores))
                {
                    continue;
                }

                marks[candIdx] = 1;
                dst[selectedCount++] = candNodeId;
                paretoAdded++;
            }
        }

        // Spill any unused diversity quota back into Primary Top-K by PrimaryScore.
        for (int i = 0; i < count && selectedCount < quota; i++)
        {
            if (marks[i] == 1)
                continue;
            marks[i] = 1;
            dst[selectedCount++] = bucketNodes[i];
        }

        return selectedCount;
    }

    private static void ComputeNicheQuotas(
        int remaining,
        bool isTurnBoundary,
        out int primaryQuota,
        out int survivalQuota,
        out int tempoQuota,
        out int investmentQuota,
        out int optionalityQuota,
        out int paretoQuota)
    {
        primaryQuota = 0;
        survivalQuota = 0;
        tempoQuota = 0;
        investmentQuota = 0;
        optionalityQuota = 0;
        paretoQuota = 0;

        switch (remaining)
        {
            case <= 0:
                return;
            case 1:
                primaryQuota = 1;
                return;
            case 2:
                primaryQuota = 1;
                survivalQuota = 1;
                return;
            case 3:
                primaryQuota = 1;
                survivalQuota = 1;
                investmentQuota = 1;
                return;
            case 4:
                primaryQuota = 1;
                survivalQuota = 1;
                tempoQuota = 1;
                investmentQuota = 1;
                return;
            case 5:
                primaryQuota = 1;
                survivalQuota = 1;
                tempoQuota = 1;
                investmentQuota = 1;
                paretoQuota = 1;
                return;
            default:
                survivalQuota = Math.Max(1, (remaining * 18 + 50) / 100);
                tempoQuota = Math.Max(1, (remaining * 15 + 50) / 100);
                investmentQuota = Math.Max(1, (remaining * 18 + 50) / 100);
                int niche5Total = Math.Max(1, (remaining * 14 + 50) / 100);
                optionalityQuota = niche5Total >= 2
                    ? Math.Max(1, isTurnBoundary ? (niche5Total * 35 + 50) / 100 : niche5Total / 2)
                    : (isTurnBoundary ? 0 : 1);
                paretoQuota = niche5Total - optionalityQuota;
                primaryQuota = Math.Max(1, remaining - survivalQuota - tempoQuota - investmentQuota - niche5Total);
                return;
        }
    }

    private interface INodeRankComparer
    {
        int Compare(in HotSearchNode a, in HotSearchNode b);
    }

    private readonly struct SurvivalComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareSurvival(in a, in b, useRolloutScores);
    }

    private readonly struct TempoComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareTempo(in a, in b, useRolloutScores);
    }

    private readonly struct BoardInvestmentComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareBoardInvestment(in a, in b, useRolloutScores);
    }

    private readonly struct OptionalityComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareOptionality(in a, in b, useRolloutScores);
    }

    private readonly struct PureRolloutComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => ComparePureRollout(in a, in b, useRolloutScores);
    }

    private readonly struct ChoiceLookaheadComparer(bool useRolloutScores) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareChoiceLookahead(in a, in b, useRolloutScores);
    }

    /// <summary>
    /// Single-pass O(N) bounded max-heap top-K selection over unmarked candidates:
    /// selects up to <paramref name="take"/> best unmarked nodes into <paramref name="dst"/> (marking them <c>1</c>)
    /// AND returns up to <paramref name="paretoExtra"/> next-best unmarked runners-up in <paramref name="paretoOut"/>
    /// for full-pool Pareto scanning. Zero heap allocations, zero delegate indirection, 100% deterministic.
    /// </summary>
    private int SelectTopKUnmarked<TComparer>(
        ReadOnlySpan<int> bucketNodes,
        Span<byte> marks,
        HotNodeArena arena,
        int take,
        int paretoExtra,
        Span<int> dst,
        Span<int> paretoOut,
        out int paretoCount,
        TComparer comparer)
        where TComparer : struct, INodeRankComparer
    {
        paretoCount = 0;
        int totalK = take + paretoExtra;
        if (totalK <= 0)
            return 0;

        int count = bucketNodes.Length;
        Span<int> heap = _scratchIndices.AsSpan(0, totalK);
        int heapSize = 0;

        for (int i = 0; i < count; i++)
        {
            if (marks[i] != 0)
                continue;

            if (heapSize < totalK)
            {
                int pos = heapSize++;
                heap[pos] = i;
                while (pos > 0)
                {
                    int parent = (pos - 1) >> 1;
                    // Max-heap by "worse": keep WORST of top-K at heap[0].
                    if (IsBetter(heap[parent], heap[pos], bucketNodes, arena, comparer))
                    {
                        (heap[parent], heap[pos]) = (heap[pos], heap[parent]);
                        pos = parent;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else if (IsBetter(i, heap[0], bucketNodes, arena, comparer))
            {
                heap[0] = i;
                int pos = 0;
                while (true)
                {
                    int left = (pos << 1) + 1;
                    if (left >= heapSize)
                        break;
                    int worst = pos;
                    if (IsBetter(heap[worst], heap[left], bucketNodes, arena, comparer))
                        worst = left;
                    int right = left + 1;
                    if (right < heapSize && IsBetter(heap[worst], heap[right], bucketNodes, arena, comparer))
                        worst = right;
                    if (worst == pos)
                        break;
                    (heap[pos], heap[worst]) = (heap[worst], heap[pos]);
                    pos = worst;
                }
            }
        }

        if (heapSize == 0)
            return 0;

        // Insertion-sort the <= totalK winners best-first (canonical deterministic order).
        for (int i = 1; i < heapSize; i++)
        {
            int key = heap[i];
            int j = i - 1;
            while (j >= 0 && IsBetter(key, heap[j], bucketNodes, arena, comparer))
            {
                heap[j + 1] = heap[j];
                j--;
            }
            heap[j + 1] = key;
        }

        int added = Math.Min(take, Math.Min(heapSize, dst.Length));
        for (int i = 0; i < added; i++)
        {
            int idx = heap[i];
            marks[idx] = 1;
            dst[i] = bucketNodes[idx];
        }

        int extraLimit = Math.Min(paretoExtra, Math.Min(heapSize - added, paretoOut.Length));
        for (int i = 0; i < extraLimit; i++)
            paretoOut[i] = heap[added + i];
        paretoCount = extraLimit;

        return added;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBetter<TComparer>(
        int idxA,
        int idxB,
        ReadOnlySpan<int> bucketNodes,
        HotNodeArena arena,
        TComparer comparer)
        where TComparer : struct, INodeRankComparer
    {
        int idA = bucketNodes[idxA];
        int idB = bucketNodes[idxB];
        ref readonly HotSearchNode a = ref arena.GetRef(idA);
        ref readonly HotSearchNode b = ref arena.GetRef(idB);
        int cmp = comparer.Compare(in a, in b);
        return cmp < 0 || (cmp == 0 && TieBreakDeterministic(in a, idA, in b, idB) < 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PrimaryScore(in HotSearchNode n, bool useRolloutScores)
        => useRolloutScores ? n.EffectiveTurnBoundaryScore() : n.Score;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAnyRolloutCandidate(ReadOnlySpan<int> bucketNodes, HotNodeArena arena)
    {
        for (int i = 0; i < bucketNodes.Length; i++)
        {
            if (arena.GetRef(bucketNodes[i]).HasRolloutScore)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAnyChoiceLookaheadCandidate(ReadOnlySpan<int> bucketNodes, HotNodeArena arena)
    {
        for (int i = 0; i < bucketNodes.Length; i++)
        {
            if (arena.GetRef(bucketNodes[i]).HasChoiceLookahead)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EffectiveNetHpLoss(in HotSearchNode n)
        => n.HpLost - n.HpRecovered + (n.PlayerMaxHpDelta < 0 ? n.PlayerMaxHpDelta * 2 : n.PlayerMaxHpDelta);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSurvival(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        int cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        int aBuf = a.PlayerHp + a.EffectiveDefense;
        int bBuf = b.PlayerHp + b.EffectiveDefense;
        cmp = bBuf.CompareTo(aBuf);
        if (cmp != 0) return cmp;
        cmp = b.PlayerHp.CompareTo(a.PlayerHp);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareTempo(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        int aRem = a.AliveEnemyCount + a.RevivingEnemyCount;
        int bRem = b.AliveEnemyCount + b.RevivingEnemyCount;
        int cmp = aRem.CompareTo(bRem);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareBoardInvestment(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        int cmp = b.BoardInvestment.CompareTo(a.BoardInvestment);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        cmp = b.SetupScore.CompareTo(a.SetupScore);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareOptionality(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        int cmp = b.OptionalityScore.CompareTo(a.OptionalityScore);
        if (cmp != 0) return cmp;
        cmp = b.BoardInvestment.CompareTo(a.BoardInvestment);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLoss(in a);
        int bNetLoss = EffectiveNetHpLoss(in b);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComparePureRollout(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        bool aHas = a.HasRolloutScore;
        bool bHas = b.HasRolloutScore;
        if (aHas != bHas)
            return bHas.CompareTo(aHas);
        if (aHas)
        {
            int cmp = b.RolloutScore.CompareTo(a.RolloutScore);
            if (cmp != 0) return cmp;
        }
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareChoiceLookahead(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores)
    {
        bool aHas = a.HasChoiceLookahead;
        bool bHas = b.HasChoiceLookahead;
        if (aHas != bHas)
            return bHas.CompareTo(aHas);
        if (aHas)
        {
            int cmp = b.RolloutScore.CompareTo(a.RolloutScore);
            if (cmp != 0) return cmp;
        }
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    /// <summary>
    /// True full-pool Pareto dominance test across all selected niche survivors AND the union of
    /// top-32 runners-up from every niche:
    /// (1) EffectiveNetHpLoss asc, (2) EnemyHp asc, (3) Alive+Reviving asc,
    /// (4) BoardInvestment desc, (5) OptionalityScore desc, (6) PotionStrategicCost asc, (7) PrimaryScore desc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDominatedInFullPoolPareto(
        in HotSearchNode candidate,
        ReadOnlySpan<int> bucketNodes,
        ReadOnlySpan<int> selectedNodeIds,
        ReadOnlySpan<int> paretoPoolIndices,
        int candidatePoolPos,
        HotNodeArena arena,
        bool useRolloutScores)
    {
        int candNetLoss = EffectiveNetHpLoss(in candidate);
        int candRem = candidate.AliveEnemyCount + candidate.RevivingEnemyCount;
        long candPrimary = PrimaryScore(in candidate, useRolloutScores);

        for (int s = 0; s < selectedNodeIds.Length; s++)
        {
            ref readonly HotSearchNode other = ref arena.GetRef(selectedNodeIds[s]);
            if (DominatesCandidate(in other, in candidate, candNetLoss, candRem, candPrimary, useRolloutScores))
                return true;
        }

        for (int j = 0; j < paretoPoolIndices.Length; j++)
        {
            if (j == candidatePoolPos)
                continue;
            ref readonly HotSearchNode other = ref arena.GetRef(bucketNodes[paretoPoolIndices[j]]);
            if (DominatesCandidate(in other, in candidate, candNetLoss, candRem, candPrimary, useRolloutScores))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DominatesCandidate(
        in HotSearchNode other,
        in HotSearchNode candidate,
        int candNetLoss,
        int candRem,
        long candPrimary,
        bool useRolloutScores)
    {
        int otherNetLoss = EffectiveNetHpLoss(in other);
        if (otherNetLoss > candNetLoss || other.EnemyHp > candidate.EnemyHp)
            return false;
        int otherRem = other.AliveEnemyCount + other.RevivingEnemyCount;
        if (otherRem > candRem
            || other.BoardInvestment < candidate.BoardInvestment
            || other.OptionalityScore < candidate.OptionalityScore
            || other.PotionStrategicCost > candidate.PotionStrategicCost)
        {
            return false;
        }

        long otherPrimary = PrimaryScore(in other, useRolloutScores);
        if (otherPrimary < candPrimary)
            return false;

        return otherNetLoss < candNetLoss
            || other.EnemyHp < candidate.EnemyHp
            || otherRem < candRem
            || other.BoardInvestment > candidate.BoardInvestment
            || other.OptionalityScore > candidate.OptionalityScore
            || other.PotionStrategicCost < candidate.PotionStrategicCost
            || otherPrimary > candPrimary;
    }

    private readonly struct PrimaryScoreNodeComparer(HotNodeArena arena, bool useRolloutScores) : IComparer<int>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(int aId, int bId)
        {
            ref readonly HotSearchNode a = ref arena.GetRef(aId);
            ref readonly HotSearchNode b = ref arena.GetRef(bId);
            int cmp = PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
            return cmp != 0 ? cmp : TieBreakDeterministic(in a, aId, in b, bId);
        }
    }

    private static void SortByPrimaryScoreDeterministic(Span<int> nodeIds, HotNodeArena arena, bool useRolloutScores)
    {
        if (nodeIds.Length <= 1)
            return;

        nodeIds.Sort(new PrimaryScoreNodeComparer(arena, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TieBreakDeterministic(in HotSearchNode a, int aId, in HotSearchNode b, int bId)
    {
        int cmp = a.HpLost.CompareTo(b.HpLost);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        if (a.HasChoiceLookahead && b.HasChoiceLookahead)
        {
            cmp = b.RolloutScore.CompareTo(a.RolloutScore);
            if (cmp != 0) return cmp;
        }
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
