using System.Buffers;
using System.Numerics;
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
    private const int NicheSlotCount = 7; // 0=Primary, 1=Survival, 2=Tempo, 3=BoardInvestment, 4=Optionality, 5=PureRollout, 6=Strategy
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
    /// <c>30%</c> to <c>25%</c> on <c>turnLayer &lt;= 1</c> and from <c>26%</c> to <c>25%</c> on <c>turnLayer &gt;= 2</c>,
    /// never collapsing below the <c>25%</c> Bucket 0 survival floor so delayed-potion lines survive.
    /// </summary>
    public bool RelaxZeroPotionReserve { get; set; }

    /// <summary>
    /// Optional minimum percentage (<c>0..60</c>) of beam width protected for Bucket 0 (0 optional paid potions)
    /// during <c>preferPotionBuckets: true</c> passes when no forced potion directives require immediate spending.
    /// When <c>&gt; 0</c>, acts as a survival floor across all turns (<c>turnLayer &gt;= 0</c>) so late-turn
    /// T2-T6 hold-potion trajectories survive Turn 1 pruning.
    /// </summary>
    public int MinZeroPotionReservePercent { get; set; }

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
        HotSearchPolicy defaultPolicy = default;
        return PruneLayer(
            candidates,
            arena,
            beamWidth,
            output,
            in defaultPolicy,
            isTurnBoundary,
            preferPotionBuckets,
            useRolloutScores,
            turnLayer,
            allowDeathSaveRescue);
    }

    public int PruneLayer(
        ReadOnlySpan<int> candidates,
        HotNodeArena arena,
        int beamWidth,
        Span<int> output,
        in HotSearchPolicy policy,
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

        // 1. Single-pass counting sort bucketing by policy-aware paid/forced potion count (0, 1, 2, 3+) — ZERO LINQ GroupBy.
        Span<int> bucketCounts = stackalloc int[MaxPotionBuckets];
        bucketCounts.Clear();
        for (int i = 0; i < n; i++)
        {
            int b = ComputePotionBucketIndex(in arena.GetRef(candidates[i]), in policy);
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
            int b = ComputePotionBucketIndex(in arena.GetRef(nodeId), in policy);
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
            MinZeroPotionReservePercent,
            hasForcedDirectives: policy.ForcedDirectiveCount > 0,
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
                in policy,
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputePotionBucketIndex(in HotSearchNode node, in HotSearchPolicy policy)
    {
        int count;
        if (policy.InitialPlayerMaxHp <= 0)
        {
            // Legacy 9-arg PruneLayer overload with default(HotSearchPolicy): preserve synthetic PotionCount tests.
            count = node.OptionalPaidPotionCount > 0 ? node.OptionalPaidPotionCount : node.PotionCount;
        }
        else
        {
            count = node.OptionalPaidPotionCount;
            if (policy.ForcedDirectiveCount > 0)
            {
                ushort forcedUsedMask = (ushort)(node.PotionMask & policy.ForcedPotionSlotMask);
                if (forcedUsedMask != 0)
                    count += BitOperations.PopCount(forcedUsedMask);
            }
            if (count == 0
                && policy.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && node.ExplicitPotionCount > 0)
            {
                count = 1;
            }
        }

        return count >= MaxPotionBuckets ? MaxPotionBuckets - 1 : count;
    }

    private static void AllocateBucketQuotas(
        ReadOnlySpan<int> counts,
        int activeBuckets,
        int totalQuota,
        bool preferPotionBuckets,
        bool relaxZeroPotionReserve,
        int minZeroPotionReservePercent,
        bool hasForcedDirectives,
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

        // Bucket 0 reserve:
        // - When ForcedDirectiveCount > 0, shift quota out of Bucket 0 into forced-potion progress buckets (1..3+).
        // - Under Smart Phase B, protect early-turn 0-potion setup prefixes and enforce a >=25% Bucket 0 survival
        //   floor across all turns (turnLayer >= 0), honoring any higher MinZeroPotionReservePercent (up to 60%),
        //   so delayed T2-T6 potion trajectories survive even when Phase A lost.
        // - Under Phase A / single-beam without forced directives, reserve 60%.
        if (counts[0] > 0)
        {
            int zeroPct;
            bool enforceCeilFloor = false;
            if (hasForcedDirectives)
            {
                zeroPct = !preferPotionBuckets
                    ? (turnLayer <= 1 ? 25 : 15)
                    : (turnLayer <= 1 ? 20 : 12);
            }
            else if (!preferPotionBuckets)
            {
                zeroPct = 60;
            }
            else
            {
                zeroPct = turnLayer <= 1
                    ? (relaxZeroPotionReserve ? 25 : 30)
                    : (relaxZeroPotionReserve ? 25 : 26);
                int floorPct = Math.Clamp(Math.Max(25, minZeroPotionReservePercent), 25, 60);
                if (zeroPct < floorPct)
                    zeroPct = floorPct;
                enforceCeilFloor = true;
            }
            int zeroTarget = enforceCeilFloor
                ? Math.Max(1, (totalQuota * zeroPct + 99) / 100)
                : Math.Max(1, (totalQuota * zeroPct) / 100);
            if (enforceCeilFloor && totalQuota > 1 && activeBuckets > 1 && zeroTarget >= totalQuota)
                zeroTarget = totalQuota - 1;
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
            if (!preferPotionBuckets)
            {
                // Phase A tapering across non-zero buckets: 55% / 30% / 15% for buckets 1 / 2 / 3+.
                ReadOnlySpan<int> phaseAWeights = [0, 55, 30, 15];
                int weightSum = 0;
                for (int b = 1; b < MaxPotionBuckets; b++)
                {
                    if (counts[b] > 0)
                        weightSum += phaseAWeights[b];
                }

                int budgetForOther = remaining;
                for (int b = 1; b < MaxPotionBuckets && remaining > 0; b++)
                {
                    if (counts[b] == 0)
                        continue;
                    int target = otherBuckets == 1
                        ? remaining
                        : Math.Max(1, (budgetForOther * phaseAWeights[b]) / weightSum);
                    int take = Math.Min(counts[b], Math.Min(target, remaining));
                    quotas[b] = take;
                    remaining -= take;
                }
            }
            else
            {
                if (counts[1] > 0 && otherBuckets > 1)
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
        }

        int startBucket = (preferPotionBuckets || hasForcedDirectives) ? 1 : 0;
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
        in HotSearchPolicy policy,
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

        bool hasPureGrowth = policy.HasPureGrowthTargets;
        bool hasRelic = policy.HasRelicTargets;
        bool hasAnyStrategy = policy.HasAnyStrategyTargets;
        bool preserveTheft = policy.TheftPolicy == SolverTheftPolicy.PreserveResources;
        ushort forcedPotionSlotMask = policy.ForcedPotionSlotMask;
        bool requireExplicitPotion = policy.InitialPlayerMaxHp > 0
            && policy.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne;
        HpEvalContext hpCtx = new(in policy);

        // Canonical primary ranking: index 0..count-1 is sorted descending by PrimaryScore.
        SortByPrimaryScoreDeterministic(bucketNodes, arena, useRolloutScores);

        // Precompute deterministic tactical signatures once per bucket (zero node IDs, zero DiscardPile order).
        Span<ulong> nodeSigs = _nodeSigs.AsSpan(0, count);
        for (int i = 0; i < count; i++)
            nodeSigs[i] = ComputeTacticalSignature(in arena.GetRef(bucketNodes[i]), hasPureGrowth, hasRelic, preserveTheft);

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

        // Carved Strategy Niche (ZERO beam width increase!): ONLY when policy.HasAnyStrategyTargets,
        // carve Math.Clamp(remainingForLanes / 10, 2, 12) slots from Primary & Optionality proportionally.
        int strategyQuota = 0;
        if (hasAnyStrategy && remainingForLanes >= 3)
        {
            int desiredStrategy = remainingForLanes < 6
                ? 1
                : Math.Clamp(remainingForLanes / 10, 2, 12);
            int availPrimary = Math.Max(0, primaryQuota - 1);
            int availOpt = optionalityQuota;
            int pool = availPrimary + availOpt;
            if (desiredStrategy <= pool)
            {
                int fromOpt = pool > 0 ? Math.Min(availOpt, (desiredStrategy * availOpt + (pool >> 1)) / pool) : 0;
                int fromPrimary = desiredStrategy - fromOpt;
                if (fromPrimary > availPrimary)
                {
                    int excess = fromPrimary - availPrimary;
                    fromPrimary = availPrimary;
                    fromOpt = Math.Min(availOpt, fromOpt + excess);
                }
                primaryQuota -= fromPrimary;
                optionalityQuota -= fromOpt;
                strategyQuota = fromPrimary + fromOpt;
            }
            else
            {
                primaryQuota -= availPrimary;
                optionalityQuota -= availOpt;
                strategyQuota = availPrimary + availOpt;
                int stillNeed = desiredStrategy - strategyQuota;
                if (stillNeed > 0 && paretoQuota > 0) { int d = Math.Min(stillNeed, paretoQuota); paretoQuota -= d; strategyQuota += d; stillNeed -= d; }
                if (stillNeed > 0 && tempoQuota > 0) { int d = Math.Min(stillNeed, tempoQuota); tempoQuota -= d; strategyQuota += d; stillNeed -= d; }
                if (stillNeed > 0 && investmentQuota > 0) { int d = Math.Min(stillNeed, investmentQuota); investmentQuota -= d; strategyQuota += d; stillNeed -= d; }
                if (stillNeed > 0 && survivalQuota > 1) { int d = Math.Min(stillNeed, survivalQuota - 1); survivalQuota -= d; strategyQuota += d; }
            }
        }

        // Stack-allocated per-niche top-32 runners-up for true full-pool Pareto (7 * 32 = 224 ints = 896 B, 0 heap alloc).
        Span<int> nichePareto = stackalloc int[NicheSlotCount * PerNicheParetoTopK];
        Span<int> nicheParetoCounts = stackalloc int[NicheSlotCount];
        nicheParetoCounts.Clear();

        bool hasRolloutCandidates = useRolloutScores && HasAnyRolloutCandidate(bucketNodes, arena);
        bool hasChoiceLookaheadCandidates = !isTurnBoundary
            && !hasRolloutCandidates
            && HasAnyChoiceLookaheadCandidate(bucketNodes, arena);

        // Niche 1 — Primary Value (>=34% of post-carve non-strategy budget guaranteed for blended Primary,
        // while guaranteeing >=2 sidecar slots for PureRollout / ChoiceLookahead whenever eligible candidates
        // exist and primaryQuota >= 6 so strategy carve never starves choice/rollout lookahead).
        int nonStrategyBudget = Math.Max(1, remainingForLanes - strategyQuota);
        int minBlendedPrimary = Math.Min(primaryQuota, Math.Max(1, (nonStrategyBudget * 34 + 99) / 100));
        int maxPrimarySidecar = Math.Max(0, primaryQuota - minBlendedPrimary);
        int sidecarFloor = primaryQuota >= 6 ? 2 : (primaryQuota >= 3 ? 1 : 0);
        if ((hasRolloutCandidates || hasChoiceLookaheadCandidates) && primaryQuota >= 6 && maxPrimarySidecar < 2)
            maxPrimarySidecar = Math.Min(2, primaryQuota - 1);
        int rolloutPureQuota = (hasRolloutCandidates && maxPrimarySidecar > 0 && primaryQuota >= 3)
            ? Math.Min(Math.Max(sidecarFloor, (primaryQuota * 20) / 100), maxPrimarySidecar)
            : 0;
        int choiceLookaheadQuota = (hasChoiceLookaheadCandidates && maxPrimarySidecar > 0 && primaryQuota >= 3)
            ? Math.Min(Math.Max(sidecarFloor, (primaryQuota * 18) / 100), maxPrimarySidecar)
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

        // Carved Strategy Niche (ONLY when policy.HasAnyStrategyTargets; zero overhead otherwise).
        if (hasAnyStrategy && (strategyQuota > 0 || paretoQuota > 0) && selectedCount < quota)
        {
            int take = Math.Min(strategyQuota, quota - selectedCount);
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
                nichePareto.Slice(6 * PerNicheParetoTopK, PerNicheParetoTopK),
                out nicheParetoCounts[6],
                new StrategyComparer(useRolloutScores, allowDeathSaveRescue, preserveTheft, hpCtx));
        }

        // Niche 2 — Survival / HP Preservation (~18%: min DeathSave [or deferred after PlayerHp when rescuing],
        // min EffectiveNetHpLossCenti, max PlayerHp + EffectiveDefense).
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
                new SurvivalComparer(useRolloutScores, allowDeathSaveRescue, preserveTheft, hpCtx, hasAnyStrategy));
        }

        // Niche 3 — Tempo / Kill Speed (~15%: min DeathSave, min Alive+Reviving, min EnemyHp).
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
                new TempoComparer(useRolloutScores, allowDeathSaveRescue, preserveTheft, hpCtx));
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
                new BoardInvestmentComparer(useRolloutScores, allowDeathSaveRescue, preserveTheft, hpCtx));
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
                new OptionalityComparer(useRolloutScores, isTurnBoundary, allowDeathSaveRescue, preserveTheft, hpCtx));
        }

        // Niche 5b — True Full-Pool Pareto scanning top-32 from EACH niche (round-robin interleaved so
        // deferred BoardInvestment / Survival / Tempo / Optionality lines ranked #150..#1000 by raw Score are visible!).
        if (paretoQuota > 0 && selectedCount < quota)
        {
            Span<int> paretoPool = stackalloc int[NicheSlotCount * PerNicheParetoTopK];
            int paretoPoolCount = 0;

            // Round-robin interleave order: Strategy(6, if active), BoardInvestment(3), Survival(1), Tempo(2), Optionality(4), PureRollout(5), Primary(0).
            ReadOnlySpan<int> nicheOrderWithStrategy = [6, 3, 1, 2, 4, 5, 0];
            ReadOnlySpan<int> nicheOrderDefault = [3, 1, 2, 4, 5, 0];
            ReadOnlySpan<int> nicheOrder = hasAnyStrategy ? nicheOrderWithStrategy : nicheOrderDefault;
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
                        allowDeathSaveRescue,
                        preserveTheft,
                        forcedPotionSlotMask,
                        requireExplicitPotion,
                        hasPureGrowth,
                        hasRelic,
                        in hpCtx))
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

    private readonly struct HpEvalContext
    {
        public readonly PostCombatRelicHealProfile PostCombatRelicHeal;
        public readonly BossHpRelief BossHpRelief;
        public readonly int InitialPlayerHp;
        public readonly int InitialPlayerMaxHp;
        public readonly byte FeedMaxHpPerKill;
        public readonly bool IgnoreLongTermRewards;
        public readonly bool HasMeatOnTheBoneTarget;

        public HpEvalContext(in HotSearchPolicy policy)
        {
            PostCombatRelicHeal = policy.PostCombatRelicHeal;
            BossHpRelief = policy.StrategicBossHpRelief;
            InitialPlayerHp = policy.InitialPlayerHp;
            InitialPlayerMaxHp = policy.InitialPlayerMaxHp;
            FeedMaxHpPerKill = policy.FeedMaxHpPerKill;
            IgnoreLongTermRewards = policy.IgnoreLongTermRewards;
            HasMeatOnTheBoneTarget = policy.HasMeatOnTheBoneTarget;
        }
    }

    private interface INodeRankComparer
    {
        int Compare(in HotSearchNode a, in HotSearchNode b);
    }

    private readonly struct StrategyComparer(
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        HpEvalContext hpCtx) : INodeRankComparer
    {
        private readonly HpEvalContext _hpCtx = hpCtx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareStrategy(in a, in b, useRolloutScores, allowDeathSaveRescue, preserveTheft, in _hpCtx);
    }

    private readonly struct SurvivalComparer(
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        HpEvalContext hpCtx,
        bool hasAnyStrategyTargets) : INodeRankComparer
    {
        private readonly HpEvalContext _hpCtx = hpCtx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareSurvival(in a, in b, useRolloutScores, allowDeathSaveRescue, preserveTheft, in _hpCtx, hasAnyStrategyTargets);
    }

    private readonly struct TempoComparer(
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        HpEvalContext hpCtx) : INodeRankComparer
    {
        private readonly HpEvalContext _hpCtx = hpCtx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareTempo(in a, in b, useRolloutScores, allowDeathSaveRescue, preserveTheft, in _hpCtx);
    }

    private readonly struct BoardInvestmentComparer(
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        HpEvalContext hpCtx) : INodeRankComparer
    {
        private readonly HpEvalContext _hpCtx = hpCtx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareBoardInvestment(in a, in b, useRolloutScores, allowDeathSaveRescue, preserveTheft, in _hpCtx);
    }

    private readonly struct OptionalityComparer(
        bool useRolloutScores,
        bool isTurnBoundary,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        HpEvalContext hpCtx) : INodeRankComparer
    {
        private readonly HpEvalContext _hpCtx = hpCtx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in HotSearchNode a, in HotSearchNode b)
            => CompareOptionality(in a, in b, useRolloutScores, isTurnBoundary, allowDeathSaveRescue, preserveTheft, in _hpCtx);
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
            if (heapSize == totalK && !IsBetter(i, heap[0], bucketNodes, arena, comparer))
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
                else
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
    private static int CompareTheft(in HotSearchNode a, in HotSearchNode b)
    {
        int cmp = a.OutstandingStolen.CompareTo(b.OutstandingStolen);
        if (cmp != 0) return cmp;
        bool aForfeit = (a.Flags & HotNodeFlags.ForfeitsGold) != 0;
        bool bForfeit = (b.Flags & HotNodeFlags.ForfeitsGold) != 0;
        return aForfeit.CompareTo(bForfeit);
    }

    /// <summary>
    /// Intermediate persistent net HP loss in centi-HP (<c>100 = 1.00 Persistent HP</c>, lower is better),
    /// aligned with <see cref="HotRouteQuality.ComputeNetPersistentHpDeltaCenti"/>:
    /// applies <see cref="HotSearchPolicy.StrategicBossHpRelief"/> scaling (<c>35%</c>/<c>20%</c>, <c>0</c> MaxHp
    /// loss on <see cref="BossHpRelief.RunEnding"/>), <see cref="HotSearchNode.DeathSaveHpRestored"/>, and
    /// <see cref="PostCombatRelicHealProfile"/> strictly without subtracting unconditional post-combat heal
    /// (Burning Blood / Black Blood) on non-victory intermediate nodes (preventing early damage erasure up to 6 HP),
    /// while preserving safe Meat on the Bone threshold preview when targeted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EffectiveNetHpLossCenti(
        in HotSearchNode n,
        in HpEvalContext ctx)
    {
        int hpScale = HotRouteQuality.PersistentHpScale(ctx.BossHpRelief);
        int deathSaveHp = n.DeathSaveHpRestored > 0 ? n.DeathSaveHpRestored : 0;

        int rawHpLoss;
        if (ctx.InitialPlayerMaxHp > 0)
        {
            int endMaxHp = Math.Max(1, ctx.InitialPlayerMaxHp - n.PlayerMaxHpDelta);
            int projectedHeal = 0;
            if (n.PlayerHp > 0 && ctx.PostCombatRelicHeal.HasAnyHeal)
            {
                if (n.IsVictory)
                {
                    projectedHeal = ctx.PostCombatRelicHeal.MonotoneHealFor(n.PlayerHp, endMaxHp);
                }
                else if (ctx.HasMeatOnTheBoneTarget
                    && n.DeathSaveUseCount == 0
                    && (n.Flags & HotNodeFlags.HasRisk) == 0)
                {
                    int safeFloor = Math.Max(4, Math.Min(12, endMaxHp / 4));
                    if (n.PlayerHp >= safeFloor)
                    {
                        int woundedBonus = ctx.PostCombatRelicHeal.HealFor(n.PlayerHp, endMaxHp)
                            - ctx.PostCombatRelicHeal.MonotoneHealFor(n.PlayerHp, endMaxHp);
                        if (woundedBonus > 0)
                            projectedHeal = woundedBonus;
                    }
                }
                int maxCap = Math.Max(0, endMaxHp - n.PlayerHp);
                if (projectedHeal > maxCap)
                    projectedHeal = maxCap;
            }

            rawHpLoss = (ctx.InitialPlayerHp - n.PlayerHp) + deathSaveHp - projectedHeal;
        }
        else
        {
            // Fallback when default(HotSearchPolicy) is supplied (legacy 9-arg PruneLayer overload).
            rawHpLoss = n.HpLost - n.HpRecovered + deathSaveHp;
        }

        int maxHpDelta = n.PlayerMaxHpDelta;
        int maxHpTermCenti;
        if (maxHpDelta >= 0)
        {
            maxHpTermCenti = ctx.BossHpRelief != BossHpRelief.RunEnding
                ? maxHpDelta * HotSearchConstants.MaxHpLossWeightCenti
                : 0;
        }
        else if (ctx.IgnoreLongTermRewards || ctx.BossHpRelief == BossHpRelief.RunEnding)
        {
            maxHpTermCenti = 0;
        }
        else
        {
            int maxHpGain = -maxHpDelta;
            int feedExcluded = n.FeedCount * ctx.FeedMaxHpPerKill;
            int creditedGain = maxHpGain > feedExcluded ? maxHpGain - feedExcluded : 0;
            maxHpTermCenti = -(creditedGain * HotSearchConstants.MaxHpGainWeightCenti);
        }

        return rawHpLoss * hpScale + maxHpTermCenti;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareStrategy(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        in HpEvalContext hpCtx)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
        if (preserveTheft)
        {
            cmp = CompareTheft(in a, in b);
            if (cmp != 0) return cmp;
        }
        cmp = b.StrategyGoalHpCredit.CompareTo(a.StrategyGoalHpCredit);
        if (cmp != 0) return cmp;
        cmp = b.StrategyGoalCount.CompareTo(a.StrategyGoalCount);
        if (cmp != 0) return cmp;
        cmp = a.RelicDistance.CompareTo(b.RelicDistance);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLossCenti(in a, in hpCtx);
        int bNetLoss = EffectiveNetHpLossCenti(in b, in hpCtx);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSurvival(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        in HpEvalContext hpCtx,
        bool hasAnyStrategyTargets)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
        if (preserveTheft)
        {
            cmp = CompareTheft(in a, in b);
            if (cmp != 0) return cmp;
        }
        int aNetLoss = EffectiveNetHpLossCenti(in a, in hpCtx);
        int bNetLoss = EffectiveNetHpLossCenti(in b, in hpCtx);
        if (hasAnyStrategyTargets)
        {
            aNetLoss -= a.StrategicHpCredit * HotSearchConstants.HpFixedScale;
            bNetLoss -= b.StrategicHpCredit * HotSearchConstants.HpFixedScale;
        }
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
    private static int CompareTempo(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        in HpEvalContext hpCtx)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
        if (preserveTheft)
        {
            cmp = CompareTheft(in a, in b);
            if (cmp != 0) return cmp;
        }
        int aRem = a.AliveEnemyCount + a.RevivingEnemyCount;
        int bRem = b.AliveEnemyCount + b.RevivingEnemyCount;
        cmp = aRem.CompareTo(bRem);
        if (cmp != 0) return cmp;
        cmp = a.EnemyHp.CompareTo(b.EnemyHp);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLossCenti(in a, in hpCtx);
        int bNetLoss = EffectiveNetHpLossCenti(in b, in hpCtx);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareBoardInvestment(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        in HpEvalContext hpCtx)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
        if (preserveTheft)
        {
            cmp = CompareTheft(in a, in b);
            if (cmp != 0) return cmp;
        }
        long aInvest = (long)a.BoardInvestment * 16_000L + (long)a.SetupScore * 18_000L;
        long bInvest = (long)b.BoardInvestment * 16_000L + (long)b.SetupScore * 18_000L;
        cmp = bInvest.CompareTo(aInvest);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLossCenti(in a, in hpCtx);
        int bNetLoss = EffectiveNetHpLossCenti(in b, in hpCtx);
        cmp = aNetLoss.CompareTo(bNetLoss);
        if (cmp != 0) return cmp;
        cmp = b.SetupScore.CompareTo(a.SetupScore);
        if (cmp != 0) return cmp;
        return PrimaryScore(in b, useRolloutScores).CompareTo(PrimaryScore(in a, useRolloutScores));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareOptionality(
        in HotSearchNode a,
        in HotSearchNode b,
        bool useRolloutScores,
        bool isTurnBoundary,
        bool allowDeathSaveRescue,
        bool preserveTheft,
        in HpEvalContext hpCtx)
    {
        int cmp;
        if (!allowDeathSaveRescue)
        {
            cmp = a.DeathSaveUseCount.CompareTo(b.DeathSaveUseCount);
            if (cmp != 0) return cmp;
        }
        if (preserveTheft)
        {
            cmp = CompareTheft(in a, in b);
            if (cmp != 0) return cmp;
        }
        if (!isTurnBoundary)
        {
            int aCombo = a.OptionalityScore + a.PlayableHandCount + Math.Min((int)a.Energy, 6);
            int bCombo = b.OptionalityScore + b.PlayableHandCount + Math.Min((int)b.Energy, 6);
            int comboCmp = bCombo.CompareTo(aCombo);
            if (comboCmp != 0) return comboCmp;
        }
        cmp = b.OptionalityScore.CompareTo(a.OptionalityScore);
        if (cmp != 0) return cmp;
        long aInvest = (long)a.BoardInvestment * 16_000L + (long)a.SetupScore * 18_000L;
        long bInvest = (long)b.BoardInvestment * 16_000L + (long)b.SetupScore * 18_000L;
        cmp = bInvest.CompareTo(aInvest);
        if (cmp != 0) return cmp;
        int aNetLoss = EffectiveNetHpLossCenti(in a, in hpCtx);
        int bNetLoss = EffectiveNetHpLossCenti(in b, in hpCtx);
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
        bool allowDeathSaveRescue,
        bool preserveTheft,
        ushort forcedPotionSlotMask,
        bool requireExplicitPotion,
        bool hasPureGrowthTargets,
        bool hasRelicTargets,
        in HpEvalContext hpCtx)
    {
        int candNetLoss = EffectiveNetHpLossCenti(in candidate, in hpCtx);
        int candRem = candidate.AliveEnemyCount + candidate.RevivingEnemyCount;
        long candPrimary = PrimaryScore(in candidate, useRolloutScores);

        for (int s = 0; s < selectedNodeIds.Length; s++)
        {
            ref readonly HotSearchNode other = ref arena.GetRef(selectedNodeIds[s]);
            if (DominatesCandidate(
                in other, in candidate, candNetLoss, candRem, candPrimary,
                useRolloutScores, allowDeathSaveRescue, preserveTheft, forcedPotionSlotMask, requireExplicitPotion,
                hasPureGrowthTargets, hasRelicTargets, in hpCtx))
            {
                return true;
            }
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
            if (DominatesCandidate(
                in other, in candidate, candNetLoss, candRem, candPrimary,
                useRolloutScores, allowDeathSaveRescue, preserveTheft, forcedPotionSlotMask, requireExplicitPotion,
                hasPureGrowthTargets, hasRelicTargets, in hpCtx))
            {
                return true;
            }
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
        bool allowDeathSaveRescue,
        bool preserveTheft,
        ushort forcedPotionSlotMask,
        bool requireExplicitPotion,
        bool hasPureGrowthTargets,
        bool hasRelicTargets,
        in HpEvalContext hpCtx)
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

        bool strictlyBetterSpecial = false;

        if (preserveTheft)
        {
            bool otherForfeit = (other.Flags & HotNodeFlags.ForfeitsGold) != 0;
            bool candForfeit = (candidate.Flags & HotNodeFlags.ForfeitsGold) != 0;
            if (other.OutstandingStolen > candidate.OutstandingStolen
                || (otherForfeit && !candForfeit))
            {
                return false;
            }
            if (other.OutstandingStolen < candidate.OutstandingStolen
                || (!otherForfeit && candForfeit))
            {
                strictlyBetterSpecial = true;
            }
        }

        if (forcedPotionSlotMask != 0)
        {
            ushort otherForced = (ushort)(other.PotionMask & forcedPotionSlotMask);
            ushort candForced = (ushort)(candidate.PotionMask & forcedPotionSlotMask);
            if ((otherForced & candForced) != candForced)
                return false;
            if (otherForced != candForced)
                strictlyBetterSpecial = true;
        }

        if (requireExplicitPotion)
        {
            if (candidate.ExplicitPotionCount > 0 && other.ExplicitPotionCount == 0)
                return false;
            if (other.ExplicitPotionCount > 0 && candidate.ExplicitPotionCount == 0)
                strictlyBetterSpecial = true;
        }

        int otherNetLoss = EffectiveNetHpLossCenti(in other, in hpCtx);
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
            || other.OptionalPaidPotionCount > candidate.OptionalPaidPotionCount
            || other.PotionStrategicCost > candidate.PotionStrategicCost)
        {
            return false;
        }

        if (hasPureGrowthTargets)
        {
            if (other.GrowthHpCredit < candidate.GrowthHpCredit
                || other.GrowthRewardCount < candidate.GrowthRewardCount)
            {
                return false;
            }
            if (other.GrowthHpCredit > candidate.GrowthHpCredit
                || other.GrowthRewardCount > candidate.GrowthRewardCount)
            {
                strictlyBetterSpecial = true;
            }
        }

        if (hasRelicTargets)
        {
            if ((other.RelicSatisfiedMask & candidate.RelicSatisfiedMask) != candidate.RelicSatisfiedMask)
                return false;
            int otherRelicCredit = other.RelicHpCredit + other.RelicHealingHpCredit;
            int candRelicCredit = candidate.RelicHpCredit + candidate.RelicHealingHpCredit;
            if (otherRelicCredit < candRelicCredit
                || other.RelicSatisfiedPriority < candidate.RelicSatisfiedPriority
                || other.RelicDistance > candidate.RelicDistance)
            {
                return false;
            }
            if (other.RelicSatisfiedMask != candidate.RelicSatisfiedMask
                || otherRelicCredit > candRelicCredit
                || other.RelicSatisfiedPriority > candidate.RelicSatisfiedPriority
                || other.RelicDistance < candidate.RelicDistance)
            {
                strictlyBetterSpecial = true;
            }
        }

        long otherPrimary = PrimaryScore(in other, useRolloutScores);
        if (otherPrimary < candPrimary)
            return false;

        return strictlyBetterSpecial
            || other.DeathSaveUseCount < candidate.DeathSaveUseCount
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
            || other.OptionalPaidPotionCount < candidate.OptionalPaidPotionCount
            || other.PotionStrategicCost < candidate.PotionStrategicCost
            || otherPrimary > candPrimary;
    }

    /// <summary>
    /// Deterministic SplitMix64 tactical state signature used to cap discard-order permutation clones
    /// without inspecting node IDs or <c>DiscardPile</c> ordering. Guaranteed non-zero (<c>0UL</c> is the
    /// empty-slot sentinel in the open-addressed scratch hash table).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeTacticalSignature(
        in HotSearchNode n,
        bool hasPureGrowthTargets,
        bool hasRelicTargets,
        bool preserveTheft)
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
            if (preserveTheft)
            {
                ulong wTheft = (ulong)(ushort)n.OutstandingStolen
                    | ((n.Flags & HotNodeFlags.ForfeitsGold) != 0 ? (1UL << 16) : 0UL);
                h = SplitMix64(h ^ wTheft);
            }
            if (hasPureGrowthTargets)
            {
                ulong wGrowth = (ulong)(ushort)n.GrowthHpCredit
                    | ((ulong)n.GrowthRewardCount << 16)
                    | ((ulong)n.AlchemizeCount << 32)
                    | ((ulong)n.FeedCount << 40);
                h = SplitMix64(h ^ wGrowth);
            }
            if (hasRelicTargets)
            {
                ulong wRelic = (ulong)n.RelicSatisfiedMask
                    | ((ulong)n.RelicDistance << 16)
                    | ((ulong)n.RelicSatisfiedPriority << 32)
                    | ((ulong)(ushort)n.RelicHpCredit << 48);
                h = SplitMix64(h ^ wRelic);
            }
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
