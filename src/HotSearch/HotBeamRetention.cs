using System.Buffers;
using System.Runtime.CompilerServices;

namespace CombatSolver.HotSearch;

/// <summary>
/// Zero-LINQ, zero-allocation single-tree domain-agnostic MAP-Elites beam retention:
/// - Deterministic counting-sort bucketing by <c>PotionCount</c> (<c>0, 1, 2, 3+</c>) with turn-aware
///   Phase-B reserves and non-starving potion-signature diversity;
/// - Deterministic SplitMix64 tactical-signature clone cap (anti-permutation-clone collapse: max 2 clones/sig
///   at turn boundaries, 3 mid-turn across Pass 1 niches/Pareto, with uncapped PrimaryScore Pass 2 spillover);
/// - 5 orthogonal domain-agnostic behavioral niches:
///   (1) Primary / Rollout-Blended Value (>=34% guaranteed even with PureRollout / ChoiceLookahead active),
///   (2) Survival / HP Preservation (~14-16%: min EffectiveNetHpLoss, max PlayerHp + EffectiveDefense, with
///       optional <see cref="AllowDeathSaveRescue"/> ordering during defeat-rescue passes),
///   (3) Tempo / Kill Speed (~11-13%: min Alive+Reviving, min EnemyHp),
///   (4) Generic Persistent Board Investment + SetupScore (~14-16%: 16_000*BoardInvestment + 18_000*SetupScore),
///   (5) Combo/Optionality + True Full-Pool Pareto (mid-turn Combo >=10% PlayableHandCount+Energy readiness, and
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
    private ulong[] _nodeSigs;
    private ulong[] _sigKeys;
    private byte[] _sigCounts;
    private ulong[] _heapSigKeys;
    private byte[] _heapSigCounts;
    private int[] _heapSigWorstIdx;
    private int _capacity;
    private int _sigCapacity;
    private bool _disposed;

    /// <summary>
    /// When <c>true</c> alongside <c>preferPotionBuckets: true</c> in <see cref="PruneLayer"/> (Phase B when
    /// Phase A found no clean zero-potion survival), lowers the turn-aware <c>0</c>-potion reserve from
    /// <c>30%</c> to <c>25%</c> on <c>turnLayer &lt;= 1</c> and from <c>26%</c> to <c>16%</c> on <c>turnLayer &gt;= 2</c>.
    /// </summary>
    public bool RelaxZeroPotionReserve { get; set; }

    /// <summary>
    /// When <c>true</c> (or when <c>allowDeathSaveRescue: true</c> is passed to <see cref="PruneLayer"/>),
    /// defers <c>DeathSaveUseCount</c> after <c>PlayerHp</c> in Survival ranking and disables cross-death-save
    /// Pareto domination so defeat-rescue passes can preserve death-save survival trajectories.
    /// </summary>
    public bool AllowDeathSaveRescue { get; set; }

    public HotBeamRetention(int initialCapacity = 4096)
    {
        int cap = Math.Max(64, initialCapacity);
        _capacity = cap;
        _bucketBuffer = ArrayPool<int>.Shared.Rent(cap);
        _scratchIndices = ArrayPool<int>.Shared.Rent(cap);
        _selectedMarks = ArrayPool<byte>.Shared.Rent(cap);
        _nodeSigs = ArrayPool<ulong>.Shared.Rent(cap);

        int sigCap = ComputeSigTableSize(Math.Min(cap, 2048));
        _sigCapacity = sigCap;
        _sigKeys = ArrayPool<ulong>.Shared.Rent(sigCap);
        _sigCounts = ArrayPool<byte>.Shared.Rent(sigCap);
        _heapSigKeys = ArrayPool<ulong>.Shared.Rent(sigCap);
        _heapSigCounts = ArrayPool<byte>.Shared.Rent(sigCap);
        _heapSigWorstIdx = ArrayPool<int>.Shared.Rent(sigCap);
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
        bool useRolloutScores = false,
        int turnLayer = 0,
        bool allowDeathSaveRescue = false)
    {
        int n = candidates.Length;
        if (n == 0 || beamWidth <= 0)
            return 0;

        int limit = Math.Min(beamWidth, output.Length);
        if (limit <= 0)
            return 0;

        if (n <= limit)
        {
            candidates.CopyTo(output);
            SortByPrimaryScoreDeterministic(output[..n], arena, useRolloutScores);
            return n;
        }

        bool effectiveRescue = allowDeathSaveRescue || AllowDeathSaveRescue;
        EnsureCapacity(Math.Max(n, limit + PerNicheParetoTopK), limit);

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
        AllocateBucketQuotas(
            bucketCounts,
            activeBuckets,
            limit,
            preferPotionBuckets,
            RelaxZeroPotionReserve,
            turnLayer,
            bucketQuotas);

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
                effectiveRescue,
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
        bool relaxZeroPotionReserve,
        int turnLayer,
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

        // Under Smart Phase B, protect early-turn (turnLayer <= 1) 0-potion setup prefixes (30% / 25% relaxed)
        // before shifting focus onto potion trajectories from turnLayer >= 2 (26% / 16% relaxed); under Phase A /
        // single-beam, reserve 60%.
        if (counts[0] > 0)
        {
            int zeroPct = !preferPotionBuckets
                ? 60
                : (turnLayer <= 1
                    ? (relaxZeroPotionReserve ? 25 : 30)
                    : (relaxZeroPotionReserve ? 16 : 26));
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
                int onePotPct = (turnLayer <= 1 && counts[2] > 0) ? 45 : 55;
                int onePotTarget = Math.Max(1, (remaining * onePotPct) / 100);
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
        bool allowDeathSaveRescue,
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

        // Precompute deterministic tactical signatures once per bucket (zero node IDs, zero DiscardPile order).
        Span<ulong> nodeSigs = _nodeSigs.AsSpan(0, count);
        for (int i = 0; i < count; i++)
            nodeSigs[i] = ComputeTacticalSignature(in arena.GetRef(bucketNodes[i]));

        int sigTableSize = ComputeSigTableSize(quota);
        int sigMask = sigTableSize - 1;
        Span<ulong> sigKeys = _sigKeys.AsSpan(0, sigTableSize);
        Span<byte> sigCounts = _sigCounts.AsSpan(0, sigTableSize);
        sigKeys.Clear();
        sigCounts.Clear();
        byte maxClonesPerSig = isTurnBoundary ? (byte)2 : (byte)3;

        // Mark values: 0 = unselected, 1 = selected into dst, 2 = queued in full-pool Pareto candidate set.
        Span<byte> marks = _selectedMarks.AsSpan(0, count);
        marks.Clear();

        int selectedCount = 0;

        // Pass 1: Enforce sigCount[sig] < maxClonesPerSig across potion-signature diversity, all MAP-Elites
        // niches, full-pool Pareto, and capped primary spillover.

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
                if (!TryConsumeSig(nodeSigs[i], sigKeys, sigCounts, sigMask, maxClonesPerSig))
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
                        if (sig == targetSig && TryConsumeSig(nodeSigs[i], sigKeys, sigCounts, sigMask, maxClonesPerSig))
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

        // Niche 1 — Primary Value (>=34% guaranteed for blended Primary even when PureRollout / ChoiceLookahead
        // is active, capping sidecar slices cleanly so Primary & mid-turn Combo never starve).
        int minBlendedPrimary = Math.Min(primaryQuota, Math.Max(1, (remainingForLanes * 34 + 99) / 100));
        int maxPrimarySidecar = Math.Max(0, primaryQuota - minBlendedPrimary);
        int rolloutPureQuota = (hasRolloutCandidates && maxPrimarySidecar > 0 && primaryQuota >= 3)
            ? Math.Min(Math.Max(1, (primaryQuota * 20) / 100), maxPrimarySidecar)
            : 0;
        int choiceLookaheadQuota = (hasChoiceLookaheadCandidates && maxPrimarySidecar > 0 && primaryQuota >= 3)
            ? Math.Min(Math.Max(1, (primaryQuota * 18) / 100), maxPrimarySidecar)
            : 0;
        int blendedPrimaryQuota = primaryQuota - rolloutPureQuota - choiceLookaheadQuota;

        int primaryAdded = 0;
        int primaryParetoCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (marks[i] != 0)
                continue;
            ulong sig = nodeSigs[i];
            if (primaryAdded < blendedPrimaryQuota && selectedCount < quota)
            {
                if (!TryConsumeSig(sig, sigKeys, sigCounts, sigMask, maxClonesPerSig))
                    continue;
                marks[i] = 1;
                dst[selectedCount++] = bucketNodes[i];
                primaryAdded++;
            }
            else if (paretoQuota > 0 && selectedCount < quota && primaryParetoCount < PerNicheParetoTopK)
            {
                int existingCount = GetSigCount(sig, sigKeys, sigCounts, sigMask);
                if (existingCount >= maxClonesPerSig)
                    continue;
                int queuedSameSig = 0;
                for (int q = 0; q < primaryParetoCount; q++)
                {
                    if (nodeSigs[nichePareto[q]] == sig)
                        queuedSameSig++;
                }
                if (existingCount + queuedSameSig >= maxClonesPerSig)
                    continue;
                nichePareto[primaryParetoCount++] = i;
            }
            else if (primaryAdded >= blendedPrimaryQuota || selectedCount >= quota)
            {
                break;
            }
        }
        nicheParetoCounts[0] = primaryParetoCount;

        if (hasRolloutCandidates && (rolloutPureQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int takeRollout = Math.Min(rolloutPureQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
                arena,
                takeRollout,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(5 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[5],
                new PureRolloutComparer(useRolloutScores));
        }
        else if (hasChoiceLookaheadCandidates && (choiceLookaheadQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int takeLookahead = Math.Min(choiceLookaheadQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
                arena,
                takeLookahead,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(5 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[5],
                new ChoiceLookaheadComparer(useRolloutScores));
        }

        // Niche 2 — Survival / HP Preservation (~18%: min DeathSave [or deferred after PlayerHp when rescuing],
        // min EffectiveNetHpLoss, max PlayerHp + EffectiveDefense).
        if ((survivalQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(survivalQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(1 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[1],
                new SurvivalComparer(useRolloutScores, allowDeathSaveRescue));
        }

        // Niche 3 — Tempo / Kill Speed (~15%: min Alive+Reviving, min EnemyHp).
        if ((tempoQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(tempoQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
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
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(3 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[3],
                new BoardInvestmentComparer(useRolloutScores));
        }

        // Niche 5a — Optionality / Mid-Turn Combo (max PlayableHandCount + Energy/Stars + DrawNextTurn).
        if ((optionalityQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(optionalityQuota, quota - selectedCount);
            selectedCount += SelectTopKUnmarked(
                bucketNodes,
                marks,
                nodeSigs,
                sigKeys,
                sigCounts,
                sigMask,
                maxClonesPerSig,
                arena,
                take,
                paretoQuota > 0 ? PerNicheParetoTopK : 0,
                dst[selectedCount..],
                nichePareto.Slice(4 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[4],
                new OptionalityComparer(useRolloutScores, isTurnBoundary));
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
                    if (GetSigCount(nodeSigs[idx], sigKeys, sigCounts, sigMask) >= maxClonesPerSig)
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
                ulong candSig = nodeSigs[candIdx];
                if (GetSigCount(candSig, sigKeys, sigCounts, sigMask) >= maxClonesPerSig)
                    continue;

                int candNodeId = bucketNodes[candIdx];
                ref readonly HotSearchNode cand = ref arena.GetRef(candNodeId);

                if (IsDominatedInFullPoolPareto(
                        in cand,
                        bucketNodes,
                        marks,
                        nodeSigs,
                        sigKeys,
                        sigCounts,
                        sigMask,
                        maxClonesPerSig,
                        dst[..selectedCount],
                        paretoPool[..paretoPoolCount],
                        p,
                        arena,
                        useRolloutScores,
                        allowDeathSaveRescue))
                {
                    continue;
                }

                if (!TryConsumeSig(candSig, sigKeys, sigCounts, sigMask, maxClonesPerSig))
                    continue;

                marks[candIdx] = 1;
                dst[selectedCount++] = candNodeId;
                paretoAdded++;
            }
        }

        // Pass 1 spillover: fill any unused niche/Pareto quota in PrimaryScore order while still respecting the signature cap.
        for (int i = 0; i < count && selectedCount < quota; i++)
        {
            if (marks[i] == 1)
                continue;
            if (!TryConsumeSig(nodeSigs[i], sigKeys, sigCounts, sigMask, maxClonesPerSig))
                continue;
            marks[i] = 1;
            dst[selectedCount++] = bucketNodes[i];
        }

        // Pass 2 spillover (narrow-tree fallback): if distinct tactical signatures are exhausted before reaching
        // quota, fill remaining slots in deterministic PrimaryScore order ignoring the cap so zero beam slots are wasted.
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
                if (isTurnBoundary)
                {
                    survivalQuota = Math.Max(1, (remaining * 16) / 100);
                    tempoQuota = Math.Max(1, (remaining * 13) / 100);
                    investmentQuota = Math.Max(1, (remaining * 16) / 100);
                    int niche5Total = Math.Max(1, (remaining * 12) / 100);
                    optionalityQuota = niche5Total >= 2
                        ? Math.Max(1, (niche5Total * 35 + 50) / 100)
                        : 0;
                    paretoQuota = niche5Total - optionalityQuota;
                    primaryQuota = Math.Max(1, remaining - survivalQuota - tempoQuota - investmentQuota - niche5Total);
                }
                else
                {
                    // Mid-turn protects Combo (PlayableHandCount + Energy readiness) >= 10% and Primary >= 34%.
                    survivalQuota = Math.Max(1, (remaining * 14) / 100);
                    tempoQuota = Math.Max(1, (remaining * 11) / 100);
                    investmentQuota = Math.Max(1, (remaining * 14) / 100);
                    optionalityQuota = Math.Max((remaining * 10 + 99) / 100, (remaining * 12) / 100);
                    paretoQuota = remaining >= 10 ? Math.Max(1, (remaining * 7) / 100) : 0;
                    primaryQuota = Math.Max(1, remaining - survivalQuota - tempoQuota - investmentQuota - optionalityQuota - paretoQuota);
                }
                return;
        }
    }

    private interface INodeRankComparer
    {
        int Compare(in HotSearchNode a, in HotSearchNode b);
    }

    private readonly struct SurvivalComparer(bool useRolloutScores, bool allowDeathSaveRescue) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareSurvival(in a, in b, useRolloutScores, allowDeathSaveRescue);
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

    private readonly struct OptionalityComparer(bool useRolloutScores, bool isTurnBoundary) : INodeRankComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareOptionality(in a, in b, useRolloutScores, isTurnBoundary);
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
    /// Single-pass O(N) per-signature-capped bounded max-heap top-K selection over unmarked candidates:
    /// enforces <c>globalSigCount[sig] + selectedInCall[sig] &lt;= maxClonesPerSig</c> directly inside the
    /// bounded heap so permutation clones can never saturate niche top-K or Pareto runner-up slots.
    /// Selects up to <paramref name="take"/> best unmarked nodes into <paramref name="dst"/> (marking them <c>1</c>
    /// and incrementing <paramref name="sigCounts"/>) AND returns up to <paramref name="paretoExtra"/> next-best
    /// unmarked non-saturated runners-up in <paramref name="paretoOut"/> for full-pool Pareto scanning.
    /// Zero heap allocations, zero delegate indirection, 100% deterministic.
    /// </summary>
    private int SelectTopKUnmarked<TComparer>(
        ReadOnlySpan<int> bucketNodes,
        Span<byte> marks,
        ReadOnlySpan<ulong> nodeSigs,
        Span<ulong> sigKeys,
        Span<byte> sigCounts,
        int sigMask,
        byte maxClonesPerSig,
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

        int heapSigTableSize = ComputeSigTableSize(totalK);
        int heapSigMask = heapSigTableSize - 1;
        Span<ulong> heapSigKeys = _heapSigKeys.AsSpan(0, heapSigTableSize);
        Span<byte> heapSigCounts = _heapSigCounts.AsSpan(0, heapSigTableSize);
        Span<int> heapSigWorstIdx = _heapSigWorstIdx.AsSpan(0, heapSigTableSize);
        heapSigKeys.Clear();

        for (int i = 0; i < count; i++)
        {
            if (marks[i] != 0)
                continue;

            ulong sig = nodeSigs[i];
            int rem = maxClonesPerSig - GetSigCount(sig, sigKeys, sigCounts, sigMask);
            if (rem <= 0)
                continue;

            int slot = (int)sig & heapSigMask;
            int inHeap = 0;
            while (true)
            {
                ulong k = heapSigKeys[slot];
                if (k == 0UL)
                    break;
                if (k == sig)
                {
                    inHeap = heapSigCounts[slot];
                    break;
                }
                slot = (slot + 1) & heapSigMask;
            }

            if (inHeap < rem)
            {
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

                    if (inHeap == 0)
                    {
                        heapSigKeys[slot] = sig;
                        heapSigCounts[slot] = 1;
                        heapSigWorstIdx[slot] = i;
                    }
                    else
                    {
                        heapSigCounts[slot] = (byte)(inHeap + 1);
                        int prevWorst = heapSigWorstIdx[slot];
                        if (prevWorst >= 0 && IsBetter(prevWorst, i, bucketNodes, arena, comparer))
                            heapSigWorstIdx[slot] = i;
                    }
                }
                else if (IsBetter(i, heap[0], bucketNodes, arena, comparer))
                {
                    int evicted = heap[0];
                    ulong evictedSig = nodeSigs[evicted];
                    heap[0] = i;
                    SiftDownHeap(heap, heapSize, 0, bucketNodes, arena, comparer);

                    if (evictedSig == sig)
                    {
                        heapSigWorstIdx[slot] = inHeap == 1 ? i : -1;
                    }
                    else
                    {
                        HeapSigDecrement(evictedSig, heapSigKeys, heapSigCounts, heapSigWorstIdx, heapSigMask);
                        HeapSigIncrement(
                            sig,
                            i,
                            heapSigKeys,
                            heapSigCounts,
                            heapSigWorstIdx,
                            heapSigMask,
                            bucketNodes,
                            arena,
                            comparer);
                    }
                }
            }
            else
            {
                // Heap already holds `rem` candidates for `sig`: only upgrade if `i` strictly beats the worst of `sig`.
                int cachedWorst = heapSigWorstIdx[slot];
                if (cachedWorst >= 0 && !IsBetter(i, cachedWorst, bucketNodes, arena, comparer))
                    continue;

                int worstPos = -1;
                for (int h = 0; h < heapSize; h++)
                {
                    if (nodeSigs[heap[h]] != sig)
                        continue;
                    if (worstPos < 0 || IsBetter(heap[worstPos], heap[h], bucketNodes, arena, comparer))
                        worstPos = h;
                }

                if (worstPos < 0 || !IsBetter(i, heap[worstPos], bucketNodes, arena, comparer))
                {
                    if (worstPos >= 0)
                        heapSigWorstIdx[slot] = heap[worstPos];
                    continue;
                }

                heap[worstPos] = i;
                SiftDownHeap(heap, heapSize, worstPos, bucketNodes, arena, comparer);
                heapSigWorstIdx[slot] = rem == 1 ? i : -1;
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
            TryConsumeSig(nodeSigs[idx], sigKeys, sigCounts, sigMask, maxClonesPerSig);
        }

        int extraLimit = Math.Min(paretoExtra, Math.Min(heapSize - added, paretoOut.Length));
        for (int i = 0; i < extraLimit; i++)
            paretoOut[i] = heap[added + i];
        paretoCount = extraLimit;

        return added;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SiftDownHeap<TComparer>(
        Span<int> heap,
        int heapSize,
        int startPos,
        ReadOnlySpan<int> bucketNodes,
        HotNodeArena arena,
        TComparer comparer)
        where TComparer : struct, INodeRankComparer
    {
        int pos = startPos;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeapSigIncrement<TComparer>(
        ulong sig,
        int candIdx,
        Span<ulong> keys,
        Span<byte> counts,
        Span<int> worstIdx,
        int mask,
        ReadOnlySpan<int> bucketNodes,
        HotNodeArena arena,
        TComparer comparer)
        where TComparer : struct, INodeRankComparer
    {
        int slot = (int)sig & mask;
        while (true)
        {
            ulong k = keys[slot];
            if (k == 0UL)
            {
                keys[slot] = sig;
                counts[slot] = 1;
                worstIdx[slot] = candIdx;
                return;
            }
            if (k == sig)
            {
                counts[slot] = (byte)(counts[slot] + 1);
                int prevWorst = worstIdx[slot];
                if (prevWorst >= 0 && IsBetter(prevWorst, candIdx, bucketNodes, arena, comparer))
                    worstIdx[slot] = candIdx;
                return;
            }
            slot = (slot + 1) & mask;
        }
    }

    private static void HeapSigDecrement(
        ulong sig,
        Span<ulong> keys,
        Span<byte> counts,
        Span<int> worstIdx,
        int mask)
    {
        int slot = (int)sig & mask;
        while (true)
        {
            ulong k = keys[slot];
            if (k == 0UL)
                return;
            if (k == sig)
                break;
            slot = (slot + 1) & mask;
        }

        byte c = counts[slot];
        if (c > 1)
        {
            counts[slot] = (byte)(c - 1);
            worstIdx[slot] = -1;
            return;
        }

        // Back-shift delete (Knuth Algorithm R) — preserves open-addressed linear probing with zero tombstones.
        int hole = slot;
        int cur = (hole + 1) & mask;
        while (true)
        {
            ulong k = keys[cur];
            if (k == 0UL)
            {
                keys[hole] = 0UL;
                counts[hole] = 0;
                worstIdx[hole] = -1;
                return;
            }

            int home = (int)k & mask;
            if (((hole - home) & mask) < ((cur - home) & mask))
            {
                keys[hole] = k;
                counts[hole] = counts[cur];
                worstIdx[hole] = worstIdx[cur];
                hole = cur;
            }

            cur = (cur + 1) & mask;
        }
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
    private static int CompareSurvival(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool allowDeathSaveRescue)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
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
        if (allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
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
        long aInvest = (long)a.BoardInvestment * 16_000L + (long)a.SetupScore * 18_000L;
        long bInvest = (long)b.BoardInvestment * 16_000L + (long)b.SetupScore * 18_000L;
        int cmp = bInvest.CompareTo(aInvest);
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
    private static int CompareOptionality(in HotSearchNode a, in HotSearchNode b, bool useRolloutScores, bool isTurnBoundary)
    {
        if (!isTurnBoundary)
        {
            int aCombo = a.OptionalityScore + a.PlayableHandCount + Math.Min((int)a.Energy, 6);
            int bCombo = b.OptionalityScore + b.PlayableHandCount + Math.Min((int)b.Energy, 6);
            int comboCmp = bCombo.CompareTo(aCombo);
            if (comboCmp != 0) return comboCmp;
        }
        int cmp = b.OptionalityScore.CompareTo(a.OptionalityScore);
        if (cmp != 0) return cmp;
        long aInvest = (long)a.BoardInvestment * 16_000L + (long)a.SetupScore * 18_000L;
        long bInvest = (long)b.BoardInvestment * 16_000L + (long)b.SetupScore * 18_000L;
        cmp = bInvest.CompareTo(aInvest);
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
    /// (1) DeathSaveUseCount asc, (2) EffectiveNetHpLoss asc, (3) PlayerHp desc, (4) EffectiveDefense desc,
    /// (5) EnemyHp asc, (6) Alive+Reviving asc, (7) SetupScore desc, (8) BoardInvestment desc,
    /// (9) MaxDarkEvoke desc, (10) TotalPoisonAndDoom desc, (11) OptionalityScore desc,
    /// (12) PlayableHandCount desc, (13) PotionStrategicCost asc, (14) PrimaryScore desc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDominatedInFullPoolPareto(
        in HotSearchNode candidate,
        ReadOnlySpan<int> bucketNodes,
        ReadOnlySpan<byte> marks,
        ReadOnlySpan<ulong> nodeSigs,
        ReadOnlySpan<ulong> sigKeys,
        ReadOnlySpan<byte> sigCounts,
        int sigMask,
        byte maxClonesPerSig,
        ReadOnlySpan<int> selectedNodeIds,
        ReadOnlySpan<int> paretoPoolIndices,
        int candidatePoolPos,
        HotNodeArena arena,
        bool useRolloutScores,
        bool allowDeathSaveRescue)
    {
        int candNetLoss = EffectiveNetHpLoss(in candidate);
        int candRem = candidate.AliveEnemyCount + candidate.RevivingEnemyCount;
        long candPrimary = PrimaryScore(in candidate, useRolloutScores);

        for (int s = 0; s < selectedNodeIds.Length; s++)
        {
            ref readonly HotSearchNode other = ref arena.GetRef(selectedNodeIds[s]);
            if (DominatesCandidate(in other, in candidate, candNetLoss, candRem, candPrimary, useRolloutScores, allowDeathSaveRescue))
                return true;
        }

        for (int j = 0; j < paretoPoolIndices.Length; j++)
        {
            if (j == candidatePoolPos)
                continue;
            int otherIdx = paretoPoolIndices[j];
            if (marks[otherIdx] == 1)
                continue; // Already tested via selectedNodeIds.
            if (GetSigCount(nodeSigs[otherIdx], sigKeys, sigCounts, sigMask) >= maxClonesPerSig)
                continue; // Saturated clone cannot be selected in Pass 1; do not let it block unsaturated lines.
            ref readonly HotSearchNode other = ref arena.GetRef(bucketNodes[otherIdx]);
            if (DominatesCandidate(in other, in candidate, candNetLoss, candRem, candPrimary, useRolloutScores, allowDeathSaveRescue))
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
        bool useRolloutScores,
        bool allowDeathSaveRescue)
    {
        if (allowDeathSaveRescue)
        {
            if (other.DeathSaveUseCount != candidate.DeathSaveUseCount)
                return false;
        }
        else if (other.DeathSaveUseCount > candidate.DeathSaveUseCount)
        {
            return false;
        }

        int otherNetLoss = EffectiveNetHpLoss(in other);
        if (otherNetLoss > candNetLoss
            || other.PlayerHp < candidate.PlayerHp
            || other.EffectiveDefense < candidate.EffectiveDefense
            || other.EnemyHp > candidate.EnemyHp)
        {
            return false;
        }

        int otherRem = other.AliveEnemyCount + other.RevivingEnemyCount;
        if (otherRem > candRem
            || other.SetupScore < candidate.SetupScore
            || other.BoardInvestment < candidate.BoardInvestment
            || other.MaxDarkEvoke < candidate.MaxDarkEvoke
            || other.TotalPoisonAndDoom < candidate.TotalPoisonAndDoom
            || other.OptionalityScore < candidate.OptionalityScore
            || other.PlayableHandCount < candidate.PlayableHandCount
            || other.PotionStrategicCost > candidate.PotionStrategicCost)
        {
            return false;
        }

        long otherPrimary = PrimaryScore(in other, useRolloutScores);
        if (otherPrimary < candPrimary)
            return false;

        return other.DeathSaveUseCount < candidate.DeathSaveUseCount
            || otherNetLoss < candNetLoss
            || other.PlayerHp > candidate.PlayerHp
            || other.EffectiveDefense > candidate.EffectiveDefense
            || other.EnemyHp < candidate.EnemyHp
            || otherRem < candRem
            || other.SetupScore > candidate.SetupScore
            || other.BoardInvestment > candidate.BoardInvestment
            || other.MaxDarkEvoke > candidate.MaxDarkEvoke
            || other.TotalPoisonAndDoom > candidate.TotalPoisonAndDoom
            || other.OptionalityScore > candidate.OptionalityScore
            || other.PlayableHandCount > candidate.PlayableHandCount
            || other.PotionStrategicCost < candidate.PotionStrategicCost
            || otherPrimary > candPrimary;
    }

    /// <summary>
    /// Deterministic SplitMix64 tactical state signature used to cap discard-order permutation clones
    /// without inspecting node IDs or <c>DiscardPile</c> ordering. Guaranteed non-zero (<c>0UL</c> is the
    /// empty-slot sentinel in the open-addressed scratch hash table).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeTacticalSignature(in HotSearchNode n)
    {
        unchecked
        {
            ulong w0 = (ulong)n.Turn
                | ((ulong)n.PotionMask << 8)
                | ((ulong)n.PotionCount << 24)
                | ((ulong)n.DeathSaveUseCount << 32)
                | ((ulong)n.AliveEnemyCount << 40)
                | ((ulong)n.RevivingEnemyCount << 48)
                | ((ulong)n.Energy << 56);
            ulong w1 = (ulong)(uint)n.HpLost | ((ulong)(uint)n.HpRecovered << 32);
            ulong w2 = (ulong)(uint)n.PlayerHp | ((ulong)(uint)n.EnemyHp << 32);
            ulong w3 = (ulong)(ushort)n.PlayerMaxHpDelta
                | ((ulong)(ushort)n.EffectiveDefense << 16)
                | ((ulong)(ushort)n.OptionalityScore << 32)
                | ((ulong)n.PlayableHandCount << 48);
            ulong w4 = (ulong)(uint)n.SetupScore | ((ulong)(uint)n.BoardInvestment << 32);
            ulong w5 = (ulong)(uint)n.MaxDarkEvoke | ((ulong)(uint)n.TotalPoisonAndDoom << 32);

            ulong h = 0xD1B54A32D192ED03UL;
            h = SplitMix64(h ^ w0);
            h = SplitMix64(h ^ w1);
            h = SplitMix64(h ^ w2);
            h = SplitMix64(h ^ w3);
            h = SplitMix64(h ^ w4);
            h = SplitMix64(h ^ w5);
            return h == 0UL ? 1UL : h;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SplitMix64(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeSigTableSize(int cap)
    {
        long target = Math.Max(64L, (long)cap * 4L);
        int size = 64;
        while (size < target && size < (1 << 30))
            size <<= 1;
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetSigCount(ulong sig, ReadOnlySpan<ulong> keys, ReadOnlySpan<byte> counts, int mask)
    {
        int slot = (int)sig & mask;
        while (true)
        {
            ulong k = keys[slot];
            if (k == 0UL)
                return 0;
            if (k == sig)
                return counts[slot];
            slot = (slot + 1) & mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeSig(ulong sig, Span<ulong> keys, Span<byte> counts, int mask, byte maxClones)
    {
        int slot = (int)sig & mask;
        while (true)
        {
            ulong k = keys[slot];
            if (k == 0UL)
            {
                keys[slot] = sig;
                counts[slot] = 1;
                return true;
            }
            if (k == sig)
            {
                byte c = counts[slot];
                if (c >= maxClones)
                    return false;
                counts[slot] = (byte)(c + 1);
                return true;
            }
            slot = (slot + 1) & mask;
        }
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
        if (a.HasChoiceLookahead != b.HasChoiceLookahead)
            return b.HasChoiceLookahead.CompareTo(a.HasChoiceLookahead);
        if (a.HasChoiceLookahead)
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

    private void EnsureCapacity(int required, int maxQuota)
    {
        if (required > _capacity)
        {
            int next = _capacity > 0 ? _capacity : 64;
            while (next < required)
                next <<= 1;

            if (_bucketBuffer.Length > 0)
                ArrayPool<int>.Shared.Return(_bucketBuffer, clearArray: false);
            if (_scratchIndices.Length > 0)
                ArrayPool<int>.Shared.Return(_scratchIndices, clearArray: false);
            if (_selectedMarks.Length > 0)
                ArrayPool<byte>.Shared.Return(_selectedMarks, clearArray: false);
            if (_nodeSigs.Length > 0)
                ArrayPool<ulong>.Shared.Return(_nodeSigs, clearArray: false);

            _bucketBuffer = ArrayPool<int>.Shared.Rent(next);
            _scratchIndices = ArrayPool<int>.Shared.Rent(next);
            _selectedMarks = ArrayPool<byte>.Shared.Rent(next);
            _nodeSigs = ArrayPool<ulong>.Shared.Rent(next);
            _capacity = next;
        }

        int requiredSigTable = ComputeSigTableSize(maxQuota + PerNicheParetoTopK);
        if (requiredSigTable > _sigCapacity)
        {
            if (_sigKeys.Length > 0)
                ArrayPool<ulong>.Shared.Return(_sigKeys, clearArray: false);
            if (_sigCounts.Length > 0)
                ArrayPool<byte>.Shared.Return(_sigCounts, clearArray: false);
            if (_heapSigKeys.Length > 0)
                ArrayPool<ulong>.Shared.Return(_heapSigKeys, clearArray: false);
            if (_heapSigCounts.Length > 0)
                ArrayPool<byte>.Shared.Return(_heapSigCounts, clearArray: false);
            if (_heapSigWorstIdx.Length > 0)
                ArrayPool<int>.Shared.Return(_heapSigWorstIdx, clearArray: false);

            _sigKeys = ArrayPool<ulong>.Shared.Rent(requiredSigTable);
            _sigCounts = ArrayPool<byte>.Shared.Rent(requiredSigTable);
            _heapSigKeys = ArrayPool<ulong>.Shared.Rent(requiredSigTable);
            _heapSigCounts = ArrayPool<byte>.Shared.Rent(requiredSigTable);
            _heapSigWorstIdx = ArrayPool<int>.Shared.Rent(requiredSigTable);
            _sigCapacity = requiredSigTable;
        }
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
        if (_nodeSigs.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_nodeSigs, clearArray: false);
            _nodeSigs = [];
        }
        if (_sigKeys.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_sigKeys, clearArray: false);
            _sigKeys = [];
        }
        if (_sigCounts.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_sigCounts, clearArray: false);
            _sigCounts = [];
        }
        if (_heapSigKeys.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_heapSigKeys, clearArray: false);
            _heapSigKeys = [];
        }
        if (_heapSigCounts.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_heapSigCounts, clearArray: false);
            _heapSigCounts = [];
        }
        if (_heapSigWorstIdx.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_heapSigWorstIdx, clearArray: false);
            _heapSigWorstIdx = [];
        }
        _capacity = 0;
        _sigCapacity = 0;
    }
}
