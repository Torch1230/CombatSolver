using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

[StructLayout(LayoutKind.Sequential)]
internal struct HotStagedChild
{
    public HotStateHandle StagedHandle;
    public HotColdSidecar? Cold;
    public HotHash128 Hash;
    public HotFastSummary Summary;
    public long Score;
    public int SetupScore;
    public int ChoiceOffset;
    public HotAction Action;
    public byte ChoiceCount;
    public short PotionCostDelta;
    public ushort PotionSlotBit;
    public byte PotionCountDelta;
    public byte ExplicitPotionDelta;
    public byte AmbergrisDelta;
    public HotNodeFlags Flags;
}

[InlineArray(HotLimits.MaxChoicesPerAction)]
internal struct HotChoiceInline8
{
    private HotChoiceSelection _element0;
}

internal struct HotChoicePrefix
{
    public HotChoiceInline8 Selections;
    public byte Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<HotChoiceSelection> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<HotChoiceInline8, HotChoiceSelection>(ref Unsafe.AsRef(in Selections)),
            Count);
}

internal readonly record struct HotStandPatResult(
    bool Executed,
    bool Survived,
    HotCombatOutcome Outcome,
    int HpLost,
    int PlayerHp,
    int EnemyHp,
    long Score,
    int SetupScore,
    HotStateHandle CommittedHandle,
    HotColdSidecar? Cold,
    HotHash128 Hash);

/// <summary>
/// Thread-private lock-free expansion workspace owning reusable <see cref="CombatHotState"/> buffers,
/// <see cref="HotExecScratch"/>, local <see cref="PackedStateArena"/> staging slab, and choice queue.
/// Zero heap allocations after warm-up; reset between waves.
/// </summary>
internal sealed class HotExpansionWorkspace : IDisposable
{
    private const int InitialStagedChildCapacity = 1024;
    private const int InitialStagedChoiceCapacity = 512;
    private const int MaxChoiceQueueItems = 128;

    public CombatHotState ParentState;
    public CombatHotState WorkingState;
    public CombatHotState EndTurnPrefixState;
    public readonly HotExecScratch Scratch = new();
    public readonly HotAction[] ActionBuf = new HotAction[256];
    public readonly HotChoiceSelection[] ChoiceOptBuf = new HotChoiceSelection[128];
    public readonly HotChoicePrefix[] ChoiceQueue = new HotChoicePrefix[MaxChoiceQueueItems];
    public readonly PackedStateArena StagingArena = new(blockPayloadBytes: 512 * 1024);

    public HotStagedChild[] StagedChildren = new HotStagedChild[InitialStagedChildCapacity];
    public int StagedChildCount;
    public HotChoiceSelection[] StagedChoices = new HotChoiceSelection[InitialStagedChoiceCapacity];
    public int StagedChoiceCount;

    // Per-wave worker telemetry (aggregated deterministically during commit)
    public int WaveTransitions;
    public int WaveDuplicateCardsPruned;
    public int WaveChoiceBranches;
    public int WaveIncumbentPrunes;
    public int WaveEndTurnPrefixCaptures;
    public int WaveEndTurnPrefixReuses;
    public long WaveAllocatedBytes;

    public void ResetWave()
    {
        if (StagedChildCount > 0)
        {
            for (int i = 0; i < StagedChildCount; i++)
                StagedChildren[i].Cold = null;
        }
        StagedChildCount = 0;
        StagedChoiceCount = 0;
        WaveTransitions = 0;
        WaveDuplicateCardsPruned = 0;
        WaveChoiceBranches = 0;
        WaveIncumbentPrunes = 0;
        WaveEndTurnPrefixCaptures = 0;
        WaveEndTurnPrefixReuses = 0;
        WaveAllocatedBytes = 0;
        StagingArena.Reset();
    }

    public int AppendStagedChoices(ReadOnlySpan<HotChoiceSelection> choices)
    {
        int len = choices.Length;
        if (len == 0)
            return -1;
        if (StagedChoiceCount + len > StagedChoices.Length)
            Array.Resize(ref StagedChoices, Math.Max(StagedChoices.Length * 2, StagedChoiceCount + len));
        int start = StagedChoiceCount;
        choices.CopyTo(StagedChoices.AsSpan(start, len));
        StagedChoiceCount += len;
        return start;
    }

    public void AddStagedChild(in HotStagedChild child)
    {
        if (StagedChildCount >= StagedChildren.Length)
            Array.Resize(ref StagedChildren, StagedChildren.Length * 2);
        StagedChildren[StagedChildCount++] = child;
    }

    public void Dispose()
    {
        StagingArena.Dispose();
    }
}

internal readonly struct HotWaveExpansionConfig
{
    public readonly bool DisableAllPotions;
    public readonly bool OnlyForcedPotions;
    public readonly int MaxPotionUses;
    public readonly int MaxCardBranchesPerNode;
    public readonly int MaxHandChoiceBranches;
    public readonly int MaxPileChoiceBranches;
    public readonly bool AllowEndTurn;

    public HotWaveExpansionConfig(
        bool disableAllPotions,
        bool onlyForcedPotions,
        int maxPotionUses,
        int maxCardBranchesPerNode,
        int maxHandChoiceBranches,
        int maxPileChoiceBranches,
        bool allowEndTurn = true)
    {
        DisableAllPotions = disableAllPotions;
        OnlyForcedPotions = onlyForcedPotions;
        MaxPotionUses = maxPotionUses;
        MaxCardBranchesPerNode = maxCardBranchesPerNode;
        MaxHandChoiceBranches = maxHandChoiceBranches;
        MaxPileChoiceBranches = maxPileChoiceBranches;
        AllowEndTurn = allowEndTurn;
    }
}

internal struct HotWaveStats
{
    public int ExpandedParents;
    public int Transitions;
    public int DuplicateCardsPruned;
    public int ChoiceBranches;
    public int IncumbentPrunes;
    public int TranspositionPrunes;
    public int EndTurnPrefixCaptures;
    public int EndTurnPrefixReuses;
    public int StandPatProbes;
    public int ParallelWaves;
    public int ParallelWorkItems;
    public int MaxConcurrency;
    public long WorkerAllocatedBytes;
}

/// <summary>
/// Lock-free coarse-wave parallel expander using private worker <see cref="HotExpansionWorkspace"/>s
/// and strict parent-index ordered commit on the coordinator thread.
/// Guarantees 100% deterministic byte-for-byte parity between <c>DOP=1</c> and <c>DOP=8/16</c>.
/// </summary>
internal sealed class HotParallelExpander : IDisposable
{
    private readonly int _maxWorkers;
    private readonly HotExpansionWorkspace[] _workspaces;
    private int[] _parentOwner;
    private int[] _parentSliceStart;
    private int[] _parentSliceCount;
    private int _parentMapCapacity;
    private readonly Dictionary<HotHash128, HotStandPatResult> _standPatCache = [];
    private bool _disposed;

    public HotParallelExpander(int maxDegreeOfParallelism)
    {
        _maxWorkers = Math.Clamp(maxDegreeOfParallelism, 1, SolverWeights.MaximumSearchMaxDegreeOfParallelism);
        // Allocate 2N workspaces when DOP > 1 (or 1 when DOP == 1) so waves & stand-pat batches stay isolated.
        int wsCount = _maxWorkers == 1 ? 1 : _maxWorkers * 2;
        _workspaces = new HotExpansionWorkspace[wsCount];
        for (int i = 0; i < wsCount; i++)
            _workspaces[i] = new HotExpansionWorkspace();

        _parentMapCapacity = 512;
        _parentOwner = new int[_parentMapCapacity];
        _parentSliceStart = new int[_parentMapCapacity];
        _parentSliceCount = new int[_parentMapCapacity];
    }

    public int StandPatCacheCount => _standPatCache.Count;

    public void ClearStandPatCache() => _standPatCache.Clear();

    /// <summary>
    /// Expands <paramref name="parentIds"/> across <c>1..DOP</c> lock-free workspaces, then commits all
    /// surviving children in strict <c>0..parentIds.Length-1</c> order into <paramref name="nodeArena"/>,
    /// <paramref name="mainStateArena"/>, and <paramref name="tt"/>.
    /// </summary>
    public void ExpandAndCommitWave(
        ReadOnlySpan<int> parentIds,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotTranspositionTable tt,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        short[] rootPotionCosts,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        bool disableTranspositionPruning,
        List<int> nextStepOut,
        List<int> nextTurnOut,
        List<int> victoriesOut,
        ref int bestFallbackNodeId,
        ref HotRouteKey bestFallbackKey,
        ref bool hasBestFallback,
        ref HotWaveStats stats,
        CancellationToken cancellationToken)
    {
        int parentCount = parentIds.Length;
        if (parentCount == 0)
            return;

        EnsureParentMapCapacity(parentCount);

        int workerCount = _maxWorkers == 1 || parentCount <= 2
            ? 1
            : Math.Min(_maxWorkers, parentCount);

        for (int w = 0; w < workerCount; w++)
            _workspaces[w].ResetWave();

        // Copy parent IDs into a managed array only if parallel (Parallel.For cannot capture ReadOnlySpan).
        if (workerCount == 1)
        {
            HotExpansionWorkspace ws = _workspaces[0];
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int p = 0; p < parentCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _parentOwner[p] = 0;
                _parentSliceStart[p] = ws.StagedChildCount;
                ExpandSingleParent(
                    ws,
                    parentIds[p],
                    nodeArena,
                    mainStateArena,
                    rules,
                    in policy,
                    in evalLookup,
                    rootPotionCosts,
                    in config,
                    in frozenIncumbent);
                _parentSliceCount[p] = ws.StagedChildCount - _parentSliceStart[p];
            }
            ws.WaveAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
        }
        else
        {
            int[] parentArray = ArrayPool<int>.Shared.Rent(parentCount);
            parentIds.CopyTo(parentArray);
            int nextParentCursor = 0;
            int batchSize = Math.Clamp(parentCount / (workerCount * 4), 1, 6);

            HotSearchPolicy policyCopy = policy;
            HotEvalLookup lookupCopy = evalLookup;
            HotWaveExpansionConfig configCopy = config;
            HotIncumbentBound incumbentCopy = frozenIncumbent;

            stats.ParallelWaves++;
            stats.ParallelWorkItems += parentCount;
            if (workerCount > stats.MaxConcurrency)
                stats.MaxConcurrency = workerCount;

            try
            {
                Parallel.For(0, workerCount, workerIdx =>
                {
                    HotExpansionWorkspace ws = _workspaces[workerIdx];
                    long allocBefore = GC.GetAllocatedBytesForCurrentThread();

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int start = Interlocked.Add(ref nextParentCursor, batchSize) - batchSize;
                        if (start >= parentCount)
                            break;
                        int end = Math.Min(start + batchSize, parentCount);
                        for (int p = start; p < end; p++)
                        {
                            _parentOwner[p] = workerIdx;
                            _parentSliceStart[p] = ws.StagedChildCount;
                            ExpandSingleParent(
                                ws,
                                parentArray[p],
                                nodeArena,
                                mainStateArena,
                                rules,
                                in policyCopy,
                                in lookupCopy,
                                rootPotionCosts,
                                in configCopy,
                                in incumbentCopy);
                            _parentSliceCount[p] = ws.StagedChildCount - _parentSliceStart[p];
                        }
                    }

                    ws.WaveAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
                });
            }
            finally
            {
                ArrayPool<int>.Shared.Return(parentArray, clearArray: false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Aggregate worker counters deterministically (sum over 0..workerCount-1)
        stats.ExpandedParents += parentCount;
        for (int w = 0; w < workerCount; w++)
        {
            HotExpansionWorkspace ws = _workspaces[w];
            stats.Transitions += ws.WaveTransitions;
            stats.DuplicateCardsPruned += ws.WaveDuplicateCardsPruned;
            stats.ChoiceBranches += ws.WaveChoiceBranches;
            stats.IncumbentPrunes += ws.WaveIncumbentPrunes;
            stats.EndTurnPrefixCaptures += ws.WaveEndTurnPrefixCaptures;
            stats.EndTurnPrefixReuses += ws.WaveEndTurnPrefixReuses;
            stats.WorkerAllocatedBytes += ws.WaveAllocatedBytes;
        }

        // Strict deterministic ordered commit: iterate p = 0 .. parentCount - 1!
        for (int p = 0; p < parentCount; p++)
        {
            int parentNodeId = parentIds[p];
            ref readonly HotSearchNode parentNode = ref nodeArena.GetRef(parentNodeId);

            // Capture parent scalars before nodeArena potentially adds chunks
            int parentTurn = parentNode.Turn;
            int parentActionsThisTurn = parentNode.ActionsThisTurn;
            ushort parentActionCount = parentNode.ActionCount;
            ushort parentPotionMask = parentNode.PotionMask;
            byte parentPotionCount = parentNode.PotionCount;
            byte parentExplicitPotions = parentNode.ExplicitPotionCount;
            byte parentAmbergris = parentNode.AmbergrisCount;
            short parentPotionCost = parentNode.PotionStrategicCost;

            HotExpansionWorkspace ws = _workspaces[_parentOwner[p]];
            int sliceStart = _parentSliceStart[p];
            int sliceEnd = sliceStart + _parentSliceCount[p];

            for (int c = sliceStart; c < sliceEnd; c++)
            {
                ref readonly HotStagedChild staged = ref ws.StagedChildren[c];
                ReadOnlySpan<byte> stagedBytes = ws.StagingArena.GetSpan(staged.StagedHandle);

                ushort nextActionCount = (ushort)Math.Min(ushort.MaxValue, parentActionCount + 1);
                ushort nextPotionMask = (ushort)(parentPotionMask | staged.PotionSlotBit);
                byte nextPotionCount = (byte)Math.Min(byte.MaxValue, parentPotionCount + staged.PotionCountDelta);
                byte nextExplicitPotions = (byte)Math.Min(byte.MaxValue, parentExplicitPotions + staged.ExplicitPotionDelta);
                byte nextAmbergris = (byte)Math.Min(byte.MaxValue, parentAmbergris + staged.AmbergrisDelta);
                short nextPotionCost = (short)Math.Clamp(parentPotionCost + staged.PotionCostDelta, 0, short.MaxValue);
                ushort forcedMask = (ushort)(nextPotionMask & policy.ForcedPotionSlotMask);

                bool isOngoing = staged.Summary.Outcome == HotCombatOutcome.Ongoing && !staged.Summary.IsDefeat;

                // Transposition table dominance check on ongoing states (ZERO mainArena commit if dominated!)
                HotTtProbeData probe = new(
                    staged.Hash,
                    staged.Score,
                    staged.Summary.HpLost,
                    nextPotionCost,
                    nextActionCount,
                    forcedMask,
                    nextPotionCount,
                    staged.Summary.DeathSaveUseCount);

                if (isOngoing && !disableTranspositionPruning)
                {
                    HotTtResult ttResult = tt.CheckDominance(
                        in probe,
                        stagedBytes,
                        staged.Cold,
                        mainStateArena,
                        out _);
                    if (ttResult == HotTtResult.Dominated)
                    {
                        stats.TranspositionPrunes++;
                        continue;
                    }
                }

                // Commit packed state into mainStateArena ONLY for admitted ongoing children
                // (terminal Victory/Defeat nodes are never unpacked again; winning routes replay from root).
                HotStateHandle committedHandle = default;
                if (isOngoing)
                {
                    Span<byte> mainDst = mainStateArena.Reserve(stagedBytes.Length);
                    stagedBytes.CopyTo(mainDst);
                    committedHandle = mainStateArena.CommitReserved(stagedBytes.Length);

                    if (!disableTranspositionPruning)
                        tt.InsertAdmitted(in probe, committedHandle, staged.Cold, mainStateArena);
                }

                int choiceOffset = -1;
                if (staged.ChoiceCount > 0 && staged.ChoiceOffset >= 0)
                {
                    choiceOffset = nodeArena.AppendChoices(
                        ws.StagedChoices.AsSpan(staged.ChoiceOffset, staged.ChoiceCount));
                }

                bool endedTurn = staged.Action.Kind == HotActionKind.EndTurn || staged.Summary.Turn > parentTurn;
                byte nextActionsThisTurn = endedTurn
                    ? (byte)0
                    : (byte)Math.Min(byte.MaxValue, parentActionsThisTurn + 1);

                HotNodeFlags flags = staged.Flags;
                if (endedTurn) flags |= HotNodeFlags.EndedTurn;
                if (staged.ChoiceCount > 0) flags |= HotNodeFlags.HasChoice;
                if (staged.Summary.DeathSaveUseCount > 0) flags |= HotNodeFlags.HasDeathSave;
                if (staged.Summary.ForfeitsGold) flags |= HotNodeFlags.ForfeitsGold;
                if (!isOngoing) flags |= HotNodeFlags.IsTerminal;

                HotSearchNode childNode = new()
                {
                    Hash = staged.Hash,
                    Score = staged.Score,
                    Cold = staged.Cold,
                    StateHandle = committedHandle,
                    ParentId = parentNodeId,
                    ChoiceOffset = choiceOffset,
                    HpLost = staged.Summary.HpLost,
                    HpRecovered = staged.Summary.HpRecovered,
                    PlayerHp = staged.Summary.PlayerHp,
                    EnemyHp = staged.Summary.EnemyHpSum,
                    SetupScore = staged.SetupScore,
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, 0, short.MaxValue),
                    PlayerBlock = (short)Math.Clamp(staged.Summary.PlayerBlock, 0, short.MaxValue),
                    OutstandingStolen = (short)Math.Clamp(staged.Summary.OutstandingStolen, 0, short.MaxValue),
                    GrowthHpCredit = (short)Math.Clamp(staged.Summary.GrowthHpCredit, short.MinValue, short.MaxValue),
                    PotionStrategicCost = nextPotionCost,
                    DeathSaveHpRestored = (short)Math.Clamp(staged.Summary.DeathSaveHpRestored, 0, short.MaxValue),
                    PotionMask = nextPotionMask,
                    ActionCount = nextActionCount,
                    GrowthRewardCount = (ushort)Math.Clamp(staged.Summary.GrowthRewardCount, 0, ushort.MaxValue),
                    Action = staged.Action,
                    ChoiceCount = staged.ChoiceCount,
                    PotionCount = nextPotionCount,
                    ExplicitPotionCount = nextExplicitPotions,
                    AmbergrisCount = nextAmbergris,
                    Energy = (byte)Math.Clamp(staged.Summary.Energy, 0, byte.MaxValue),
                    Turn = (byte)Math.Clamp(staged.Summary.Turn, 0, byte.MaxValue),
                    ActionsThisTurn = nextActionsThisTurn,
                    AliveEnemyCount = staged.Summary.AliveEnemyCount,
                    DeathSaveUseCount = staged.Summary.DeathSaveUseCount,
                    Outcome = staged.Summary.Outcome,
                    Flags = flags,
                };

                int childNodeId = nodeArena.AddNode(in childNode);

                if (childNode.IsVictory)
                {
                    victoriesOut.Add(childNodeId);
                }
                else if (childNode.IsDefeat)
                {
                    HotRouteKey defKey = HotRouteQuality.Capture(in childNode, in policy);
                    if (!hasBestFallback || HotRouteQuality.IsBetter(in defKey, in bestFallbackKey, in policy))
                    {
                        hasBestFallback = true;
                        bestFallbackKey = defKey;
                        bestFallbackNodeId = childNodeId;
                    }
                }
                else if (endedTurn)
                {
                    nextTurnOut.Add(childNodeId);
                }
                else
                {
                    nextStepOut.Add(childNodeId);
                }
            }
        }
    }

    /// <summary>
    /// Runs lightweight Stand-Pat (<c>EndTurn</c>) lookahead on Top-K non-dominated active candidates
    /// (cached by <see cref="HotHash128"/>, executed in parallel read-only batch when <c>DOP &gt; 1</c>).
    /// </summary>
    public void RunStandPatBatch(
        ReadOnlySpan<int> candidateIds,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        List<int> nextTurnOut,
        List<int> victoriesOut,
        ref int bestFallbackNodeId,
        ref HotRouteKey bestFallbackKey,
        ref bool hasBestFallback,
        ref HotWaveStats stats,
        CancellationToken cancellationToken)
    {
        int count = candidateIds.Length;
        if (count == 0)
            return;

        // Partition into cache hits vs misses in deterministic order.
        int[] missIndices = ArrayPool<int>.Shared.Rent(count);
        int[] missNodeIds = ArrayPool<int>.Shared.Rent(count);
        int missCount = 0;

        for (int i = 0; i < count; i++)
        {
            int nodeId = candidateIds[i];
            ref readonly HotSearchNode node = ref nodeArena.GetRef(nodeId);
            if (_standPatCache.ContainsKey(node.Hash))
                continue;

            bool dupInBatch = false;
            for (int k = 0; k < missCount; k++)
            {
                if (nodeArena.GetRef(missNodeIds[k]).Hash == node.Hash)
                {
                    dupInBatch = true;
                    break;
                }
            }
            if (!dupInBatch)
            {
                missIndices[missCount] = i;
                missNodeIds[missCount] = nodeId;
                missCount++;
            }
        }

        if (missCount > 0)
        {
            int wsBase = _maxWorkers == 1 ? 0 : _maxWorkers;
            int workers = _maxWorkers == 1 || missCount <= 2 ? 1 : Math.Min(_maxWorkers, missCount);
            for (int w = 0; w < workers; w++)
                _workspaces[wsBase + w].ResetWave();

            HotStagedChild[] results = new HotStagedChild[missCount];
            bool[] executedFlags = new bool[missCount];
            int[] ownerWs = new int[missCount];

            HotSearchPolicy policyCopy = policy;
            HotEvalLookup lookupCopy = evalLookup;

            if (workers == 1)
            {
                HotExpansionWorkspace ws = _workspaces[wsBase];
                for (int m = 0; m < missCount; m++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ownerWs[m] = wsBase;
                    executedFlags[m] = RunSingleStandPat(
                        ws, missNodeIds[m], nodeArena, mainStateArena, rules, in policyCopy, in lookupCopy, out results[m]);
                }
            }
            else
            {
                int cursor = 0;
                Parallel.For(0, workers, w =>
                {
                    int wsIdx = wsBase + w;
                    HotExpansionWorkspace ws = _workspaces[wsIdx];
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int m = Interlocked.Increment(ref cursor) - 1;
                        if (m >= missCount)
                            break;
                        ownerWs[m] = wsIdx;
                        executedFlags[m] = RunSingleStandPat(
                            ws, missNodeIds[m], nodeArena, mainStateArena, rules, in policyCopy, in lookupCopy, out results[m]);
                    }
                });
            }

            // Deterministic commit of stand-pat results into cache & nextTurnOut.
            for (int m = 0; m < missCount; m++)
            {
                stats.StandPatProbes++;
                int parentId = missNodeIds[m];
                ref readonly HotSearchNode parent = ref nodeArena.GetRef(parentId);

                if (!executedFlags[m])
                {
                    _standPatCache[parent.Hash] = default;
                    continue;
                }

                ref readonly HotStagedChild staged = ref results[m];
                HotExpansionWorkspace ws = _workspaces[ownerWs[m]];
                ReadOnlySpan<byte> stagedBytes = ws.StagingArena.GetSpan(staged.StagedHandle);
                Span<byte> mainDst = mainStateArena.Reserve(stagedBytes.Length);
                stagedBytes.CopyTo(mainDst);
                HotStateHandle committed = mainStateArena.CommitReserved(stagedBytes.Length);

                HotStandPatResult cached = new(
                    Executed: true,
                    Survived: !staged.Summary.IsDefeat,
                    Outcome: staged.Summary.Outcome,
                    HpLost: staged.Summary.HpLost,
                    PlayerHp: staged.Summary.PlayerHp,
                    EnemyHp: staged.Summary.EnemyHpSum,
                    Score: staged.Score,
                    SetupScore: staged.SetupScore,
                    CommittedHandle: committed,
                    Cold: staged.Cold,
                    Hash: staged.Hash);
                _standPatCache[parent.Hash] = cached;

                HotSearchNode childNode = new()
                {
                    Hash = staged.Hash,
                    Score = staged.Score,
                    Cold = staged.Cold,
                    StateHandle = committed,
                    ParentId = parentId,
                    ChoiceOffset = -1,
                    HpLost = staged.Summary.HpLost,
                    HpRecovered = staged.Summary.HpRecovered,
                    PlayerHp = staged.Summary.PlayerHp,
                    EnemyHp = staged.Summary.EnemyHpSum,
                    SetupScore = staged.SetupScore,
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, 0, short.MaxValue),
                    PlayerBlock = (short)Math.Clamp(staged.Summary.PlayerBlock, 0, short.MaxValue),
                    OutstandingStolen = (short)Math.Clamp(staged.Summary.OutstandingStolen, 0, short.MaxValue),
                    GrowthHpCredit = (short)Math.Clamp(staged.Summary.GrowthHpCredit, short.MinValue, short.MaxValue),
                    PotionStrategicCost = parent.PotionStrategicCost,
                    DeathSaveHpRestored = (short)Math.Clamp(staged.Summary.DeathSaveHpRestored, 0, short.MaxValue),
                    PotionMask = parent.PotionMask,
                    ActionCount = (ushort)Math.Min(ushort.MaxValue, parent.ActionCount + 1),
                    GrowthRewardCount = (ushort)Math.Clamp(staged.Summary.GrowthRewardCount, 0, ushort.MaxValue),
                    Action = HotAction.EndTurn(),
                    ChoiceCount = 0,
                    PotionCount = parent.PotionCount,
                    ExplicitPotionCount = parent.ExplicitPotionCount,
                    AmbergrisCount = parent.AmbergrisCount,
                    Energy = (byte)Math.Clamp(staged.Summary.Energy, 0, byte.MaxValue),
                    Turn = (byte)Math.Clamp(staged.Summary.Turn, 0, byte.MaxValue),
                    ActionsThisTurn = 0,
                    AliveEnemyCount = staged.Summary.AliveEnemyCount,
                    DeathSaveUseCount = staged.Summary.DeathSaveUseCount,
                    Outcome = staged.Summary.Outcome,
                    Flags = HotNodeFlags.EndedTurn | HotNodeFlags.StandPat
                        | (staged.Summary.Outcome != HotCombatOutcome.Ongoing ? HotNodeFlags.IsTerminal : HotNodeFlags.None),
                };

                int childId = nodeArena.AddNode(in childNode);
                if (childNode.IsVictory)
                    victoriesOut.Add(childId);
                else if (childNode.IsDefeat)
                {
                    HotRouteKey key = HotRouteQuality.Capture(in childNode, in policy);
                    if (!hasBestFallback || HotRouteQuality.IsBetter(in key, in bestFallbackKey, in policy))
                    {
                        hasBestFallback = true;
                        bestFallbackKey = key;
                        bestFallbackNodeId = childId;
                    }
                }
                else
                {
                    nextTurnOut.Add(childId);
                }
            }
        }

        ArrayPool<int>.Shared.Return(missIndices, clearArray: false);
        ArrayPool<int>.Shared.Return(missNodeIds, clearArray: false);
    }

    private static bool RunSingleStandPat(
        HotExpansionWorkspace ws,
        int parentNodeId,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        out HotStagedChild staged)
    {
        ref readonly HotSearchNode parent = ref nodeArena.GetRef(parentNodeId);
        HotStatePacker.Unpack(mainStateArena.GetSpan(parent.StateHandle), ref ws.WorkingState);
        AssignDeterministicCardUids(ref ws.WorkingState);
        HotColdSidecar? cold = parent.Cold;

        HotStepResult res = HotCombatEngine.Step(
            ref ws.WorkingState,
            ref cold,
            rules,
            ws.Scratch,
            HotAction.EndTurn(),
            default);
        if (!res.Executed || res.NeedsChoice)
        {
            staged = default;
            return false;
        }

        int packedSize = HotStatePacker.ComputePackedSize(in ws.WorkingState);
        Span<byte> dst = ws.StagingArena.Reserve(packedSize);
        int written = HotStatePacker.Pack(ref ws.WorkingState, ref cold, dst, canonicalize: true);
        HotStateHandle handle = ws.StagingArena.CommitReserved(written);
        HotHash128 hash = HotHash.ComputeHash(ws.StagingArena.GetSpan(handle), cold);

        HotFastSummary summary = HotEvaluator.ExtractStage1(in ws.WorkingState, in policy);
        HotEvaluator.EvaluateState(
            in ws.WorkingState,
            cold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            parent.PotionStrategicCost,
            parent.ActionCount + 1,
            out long score,
            out int setupScore);

        staged = new HotStagedChild
        {
            StagedHandle = handle,
            Cold = cold,
            Hash = hash,
            Summary = summary,
            Score = score,
            SetupScore = setupScore,
            ChoiceOffset = -1,
            Action = HotAction.EndTurn(),
            ChoiceCount = 0,
            Flags = HotNodeFlags.EndedTurn | HotNodeFlags.StandPat,
        };
        return true;
    }

    private static void ExpandSingleParent(
        HotExpansionWorkspace ws,
        int parentNodeId,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        short[] rootPotionCosts,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent)
    {
        ref readonly HotSearchNode parent = ref nodeArena.GetRef(parentNodeId);
        if (parent.IsTerminal)
            return;

        // Unpack parent ONCE into ws.ParentState; every child restores via fast blittable struct copy (~16 KB L1 copy)
        HotStatePacker.Unpack(mainStateArena.GetSpan(parent.StateHandle), ref ws.ParentState);
        HotColdSidecar? parentCold = parent.Cold;

        bool allowPotions = !config.DisableAllPotions
            && (config.MaxPotionUses < 0 || parent.ExplicitPotionCount < config.MaxPotionUses);
        bool canPlayMoreThisTurn = parent.ActionsThisTurn < HotBeamRetention.MaxActionsPerTurn;

        // Compute sound hand-card duplicate mask BEFORE assigning fresh CardUids (unreferenced cards have CardUid == 0).
        ulong duplicateHandMask = canPlayMoreThisTurn
            ? ComputeDuplicateHandMask(in ws.ParentState, parentCold)
            : 0UL;

        // Assign deterministic non-zero CardUids to all cards so HotCombatEngine.EnumerateChoiceOptions / FindCardIndexByUid
        // match every card reliably during search AND match verbatim during HotPlanMaterializer replay!
        AssignDeterministicCardUids(ref ws.ParentState);

        int actionCount = HotCombatEngine.EnumerateLegalActions(
            in ws.ParentState,
            parentCold,
            rules,
            ws.ActionBuf,
            includePotions: allowPotions);

        int cardBranchesEmitted = 0;

        for (int a = 0; a < actionCount; a++)
        {
            HotAction action = ws.ActionBuf[a];

            if (action.Kind == HotActionKind.EndTurn)
            {
                if (!config.AllowEndTurn)
                    continue;
                ExpandEndTurnAction(
                    ws,
                    in parent,
                    parentCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in config,
                    in frozenIncumbent);
                continue;
            }

            if (!canPlayMoreThisTurn)
                continue;

            if (action.Kind == HotActionKind.PlayCard)
            {
                byte handIdx = action.SlotOrHandIdx;
                if (handIdx < 64 && (duplicateHandMask & (1UL << handIdx)) != 0)
                {
                    ws.WaveDuplicateCardsPruned++;
                    continue;
                }
                if (config.MaxCardBranchesPerNode > 0 && cardBranchesEmitted >= config.MaxCardBranchesPerNode)
                    continue;

                cardBranchesEmitted++;
                ExpandActionWithChoices(
                    ws,
                    in parent,
                    parentCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in config,
                    in frozenIncumbent,
                    action,
                    potionCostDelta: 0,
                    potionSlotBit: 0,
                    explicitPotionDelta: 0,
                    ambergrisDelta: 0);
            }
            else if (action.Kind == HotActionKind.UsePotion)
            {
                int slot = action.SlotOrHandIdx;
                if ((uint)slot >= (uint)ws.ParentState.PotionSlotCount)
                    continue;
                ushort pDefId = ws.ParentState.Potions[slot];
                if (!HotPotionPolicy.IsSlotAllowedForPass(
                        slot,
                        pDefId,
                        rules,
                        in policy,
                        config.DisableAllPotions,
                        config.OnlyForcedPotions,
                        parent.ExplicitPotionCount,
                        config.MaxPotionUses))
                {
                    continue;
                }

                HotPotionDef pDef = rules.Potions[pDefId];
                short pCost = slot < rootPotionCosts.Length && rootPotionCosts[slot] >= 0
                    ? rootPotionCosts[slot]
                    : HotPotionPolicy.ComputePotionDefCost(pDef, policy.HasRenewablePotionShapedRock);
                ushort slotBit = (ushort)(1 << slot);
                byte isAmbergris = (pDef.PotionId == HotCustomPotionId.Ambergris
                    || string.Equals(pDef.Entry, "AMBERGRIS", StringComparison.Ordinal))
                    ? (byte)1
                    : (byte)0;

                ExpandActionWithChoices(
                    ws,
                    in parent,
                    parentCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in config,
                    in frozenIncumbent,
                    action,
                    potionCostDelta: pCost,
                    potionSlotBit: slotBit,
                    explicitPotionDelta: 1,
                    ambergrisDelta: isAmbergris);
            }
        }
    }

    /// <summary>
    /// Expands <see cref="HotActionKind.EndTurn"/> using <c>EndTurnPrefix</c> sharing:
    /// runs <see cref="HotCombatEngine.StepEndTurnBeforePlayerStart"/> once into <c>ws.EndTurnPrefixState</c>,
    /// then branches any turn-start choices from that shared prefix without re-simulating the enemy turn!
    /// </summary>
    private static void ExpandEndTurnAction(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotColdSidecar? parentCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent)
    {
        ws.EndTurnPrefixState = ws.ParentState;
        HotColdSidecar? prefixCold = parentCold;

        HotStepResult preRes = HotCombatEngine.StepEndTurnBeforePlayerStart(
            ref ws.EndTurnPrefixState,
            ref prefixCold,
            rules,
            ws.Scratch,
            default);

        if (!preRes.Executed)
            return;

        // If enemy turn / player turn-end itself needed a choice (e.g. Well-Laid Plans), fall back to full choice tree on EndTurn.
        if (preRes.NeedsChoice)
        {
            ExpandActionWithChoices(
                ws,
                in parent,
                parentCold,
                rules,
                in policy,
                in evalLookup,
                in config,
                in frozenIncumbent,
                HotAction.EndTurn(),
                potionCostDelta: 0,
                potionSlotBit: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

        // Combat ended during player turn-end or enemy turn (victory or defeat) -> stage directly!
        if (preRes.Outcome != HotCombatOutcome.Ongoing || !HotCombatEngine.IsInProgress(in ws.EndTurnPrefixState))
        {
            ws.WaveTransitions++;
            ws.WorkingState = ws.EndTurnPrefixState;
            StageCompletedTransition(
                ws,
                in parent,
                prefixCold,
                rules,
                in policy,
                in evalLookup,
                in frozenIncumbent,
                HotAction.EndTurn(),
                default,
                potionCostDelta: 0,
                potionSlotBit: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

        // Now run StepStartPlayerTurn from shared EndTurnPrefixState!
        ws.WorkingState = ws.EndTurnPrefixState;
        HotColdSidecar? turnCold = prefixCold;
        HotStepResult startRes = HotCombatEngine.StepStartPlayerTurn(
            ref ws.WorkingState,
            ref turnCold,
            rules,
            ws.Scratch,
            default);

        if (!startRes.Executed)
            return;

        if (!startRes.NeedsChoice)
        {
            ws.WaveTransitions++;
            StageCompletedTransition(
                ws,
                in parent,
                turnCold,
                rules,
                in policy,
                in evalLookup,
                in frozenIncumbent,
                HotAction.EndTurn(),
                default,
                potionCostDelta: 0,
                potionSlotBit: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

        // EndTurnPrefix sharing: branch all turn-start choices directly from ws.EndTurnPrefixState!
        ws.WaveEndTurnPrefixCaptures++;
        int optCount = HotCombatEngine.EnumerateChoiceOptions(
            in ws.WorkingState,
            turnCold,
            rules,
            ws.Scratch,
            ws.ChoiceOptBuf);
        if (optCount <= 0)
            return;

        int maxBranches = ResolveChoiceBranchCap(ws.Scratch.PendingChoiceKind, in config);
        int limit = Math.Min(optCount, maxBranches);

        HotChoicePrefix[] queue = ws.ChoiceQueue;
        int qHead = 0;
        int qTail = 0;

        for (int i = 0; i < limit && qTail < queue.Length; i++)
        {
            HotChoicePrefix pfx = default;
            pfx.Selections[0] = ws.ChoiceOptBuf[i];
            pfx.Count = 1;
            queue[qTail++] = pfx;
        }

        int resolvedCount = 0;
        while (qHead < qTail && resolvedCount < maxBranches)
        {
            HotChoicePrefix pfx = queue[qHead++];
            ws.WorkingState = ws.EndTurnPrefixState;
            HotColdSidecar? branchCold = prefixCold;
            ws.WaveEndTurnPrefixReuses++;
            ws.WaveChoiceBranches++;

            HotStepResult res = HotCombatEngine.StepStartPlayerTurn(
                ref ws.WorkingState,
                ref branchCold,
                rules,
                ws.Scratch,
                pfx.AsSpan());

            if (!res.Executed)
                continue;

            if (!res.NeedsChoice)
            {
                ws.WaveTransitions++;
                resolvedCount++;
                StageCompletedTransition(
                    ws,
                    in parent,
                    branchCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in frozenIncumbent,
                    HotAction.EndTurn(),
                    pfx.AsSpan(),
                    potionCostDelta: 0,
                    potionSlotBit: 0,
                    explicitPotionDelta: 0,
                    ambergrisDelta: 0);
            }
            else if (pfx.Count < HotLimits.MaxChoicesPerAction)
            {
                int subCount = HotCombatEngine.EnumerateChoiceOptions(
                    in ws.WorkingState,
                    branchCold,
                    rules,
                    ws.Scratch,
                    ws.ChoiceOptBuf);
                int subLimit = Math.Min(subCount, maxBranches);
                for (int i = 0; i < subLimit && qTail < queue.Length; i++)
                {
                    HotChoicePrefix nextPfx = pfx;
                    nextPfx.Selections[nextPfx.Count] = ws.ChoiceOptBuf[i];
                    nextPfx.Count++;
                    queue[qTail++] = nextPfx;
                }
            }
        }
    }

    private static void ExpandActionWithChoices(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotColdSidecar? parentCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        HotAction action,
        short potionCostDelta,
        ushort potionSlotBit,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
        // Fast path: 0 choices (90%+ of all card plays and potions)
        ws.WorkingState = ws.ParentState;
        HotColdSidecar? childCold = parentCold;

        HotStepResult firstRes = HotCombatEngine.Step(
            ref ws.WorkingState,
            ref childCold,
            rules,
            ws.Scratch,
            action,
            default);

        if (!firstRes.Executed)
            return;

        if (!firstRes.NeedsChoice)
        {
            ws.WaveTransitions++;
            StageCompletedTransition(
                ws,
                in parent,
                childCold,
                rules,
                in policy,
                in evalLookup,
                in frozenIncumbent,
                action,
                default,
                potionCostDelta,
                potionSlotBit,
                explicitPotionDelta,
                ambergrisDelta);
            return;
        }

        // Choice path: iterative zero-alloc BFS over restart-from-parent ChoiceCursor
        int firstOptCount = HotCombatEngine.EnumerateChoiceOptions(
            in ws.WorkingState,
            childCold,
            rules,
            ws.Scratch,
            ws.ChoiceOptBuf);
        if (firstOptCount <= 0)
            return;

        int maxBranches = ResolveChoiceBranchCap(ws.Scratch.PendingChoiceKind, in config);
        int firstLimit = Math.Min(firstOptCount, maxBranches);

        HotChoicePrefix[] queue = ws.ChoiceQueue;
        int qHead = 0;
        int qTail = 0;

        for (int i = 0; i < firstLimit && qTail < queue.Length; i++)
        {
            HotChoicePrefix pfx = default;
            pfx.Selections[0] = ws.ChoiceOptBuf[i];
            pfx.Count = 1;
            queue[qTail++] = pfx;
        }

        int resolvedCount = 0;
        while (qHead < qTail && resolvedCount < maxBranches)
        {
            HotChoicePrefix pfx = queue[qHead++];
            ws.WorkingState = ws.ParentState;
            HotColdSidecar? branchCold = parentCold;
            ws.WaveChoiceBranches++;

            HotStepResult res = HotCombatEngine.Step(
                ref ws.WorkingState,
                ref branchCold,
                rules,
                ws.Scratch,
                action,
                pfx.AsSpan());

            if (!res.Executed)
                continue;

            if (!res.NeedsChoice)
            {
                ws.WaveTransitions++;
                resolvedCount++;
                StageCompletedTransition(
                    ws,
                    in parent,
                    branchCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in frozenIncumbent,
                    action,
                    pfx.AsSpan(),
                    potionCostDelta,
                    potionSlotBit,
                    explicitPotionDelta,
                    ambergrisDelta);
            }
            else if (pfx.Count < HotLimits.MaxChoicesPerAction)
            {
                int subOptCount = HotCombatEngine.EnumerateChoiceOptions(
                    in ws.WorkingState,
                    branchCold,
                    rules,
                    ws.Scratch,
                    ws.ChoiceOptBuf);
                int subLimit = Math.Min(subOptCount, maxBranches);
                for (int i = 0; i < subLimit && qTail < queue.Length; i++)
                {
                    HotChoicePrefix nextPfx = pfx;
                    nextPfx.Selections[nextPfx.Count] = ws.ChoiceOptBuf[i];
                    nextPfx.Count++;
                    queue[qTail++] = nextPfx;
                }
            }
        }
    }

    private static void StageCompletedTransition(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotColdSidecar? childCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotIncumbentBound frozenIncumbent,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices,
        short potionCostDelta,
        ushort potionSlotBit,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
        // Detect automatic potion consumption (e.g. Fairy in a Bottle triggered during damage/enemy turn)
        byte autoPotionDelta = DetectAutoPotionDelta(in ws.ParentState, in ws.WorkingState, explicitPotionDelta);
        byte totalPotionDelta = (byte)(explicitPotionDelta + autoPotionDelta);

        // Stage 1: zero-alloc scalar summary
        HotFastSummary summary = HotEvaluator.ExtractStage1(in ws.WorkingState, in policy);

        int nextExplicit = parent.ExplicitPotionCount + explicitPotionDelta;
        short nextPotionCost = (short)Math.Clamp(parent.PotionStrategicCost + potionCostDelta, 0, short.MaxValue);
        ushort nextPotionMask = (ushort)(parent.PotionMask | potionSlotBit);
        byte nextAmbergris = (byte)Math.Min(byte.MaxValue, parent.AmbergrisCount + ambergrisDelta);

        int optionalPotionReq = HotPotionPolicy.ComputeOptionalPotionRequiredHp(
            nextPotionCost,
            nextExplicit,
            nextAmbergris,
            nextPotionMask,
            in policy);

        // Certified 2-bit HP incumbent bound BEFORE packing/hashing/Stage 2!
        if (HotEvaluator.IsPrunedByCertifiedIncumbent(
                in summary,
                nextExplicit,
                optionalPotionReq,
                in frozenIncumbent,
                in policy))
        {
            ws.WaveIncumbentPrunes++;
            return;
        }

        // Pack & Hash into worker-local staging arena (canonicalizes CardUids & zeroes padding!)
        int packedSize = HotStatePacker.ComputePackedSize(in ws.WorkingState);
        Span<byte> buf = ws.StagingArena.Reserve(packedSize);
        int written = HotStatePacker.Pack(ref ws.WorkingState, ref childCold, buf, canonicalize: true);
        HotStateHandle stagedHandle = ws.StagingArena.CommitReserved(written);
        HotHash128 hash = HotHash.ComputeHash(ws.StagingArena.GetSpan(stagedHandle), childCold);

        // Stage 2: single-pass heuristic scoring
        HotEvaluator.EvaluateState(
            in ws.WorkingState,
            childCold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            nextPotionCost,
            parent.ActionCount + 1,
            out long score,
            out int setupScore);

        int choiceOffset = choices.Length > 0 ? ws.AppendStagedChoices(choices) : -1;

        ws.AddStagedChild(new HotStagedChild
        {
            StagedHandle = stagedHandle,
            Cold = childCold,
            Hash = hash,
            Summary = summary,
            Score = score,
            SetupScore = setupScore,
            ChoiceOffset = choiceOffset,
            Action = action,
            ChoiceCount = (byte)choices.Length,
            PotionCostDelta = potionCostDelta,
            PotionSlotBit = potionSlotBit,
            PotionCountDelta = totalPotionDelta,
            ExplicitPotionDelta = explicitPotionDelta,
            AmbergrisDelta = ambergrisDelta,
            Flags = HotNodeFlags.None,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveChoiceBranchCap(HotChoiceKind kind, in HotWaveExpansionConfig config)
    {
        int raw = kind == HotChoiceKind.HandCard
            ? config.MaxHandChoiceBranches
            : config.MaxPileChoiceBranches;
        return Math.Clamp(raw > 0 ? raw : 18, 1, 64);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DetectAutoPotionDelta(
        in CombatHotState before,
        in CombatHotState after,
        byte explicitPotionDelta)
    {
        int beforeFilled = 0;
        int afterFilled = 0;
        int slots = Math.Min((int)before.PotionSlotCount, HotLimits.MaxPotions);
        for (int i = 0; i < slots; i++)
        {
            if (before.Potions[i] != 0) beforeFilled++;
            if (after.Potions[i] != 0) afterFilled++;
        }
        int totalConsumed = Math.Max(0, beforeFilled - afterFilled);
        int auto = totalConsumed - explicitPotionDelta;
        return auto > 0 ? (byte)auto : (byte)0;
    }

    /// <summary>
    /// Computes a bitmask of hand indices <c>j</c> that are byte-for-byte semantically identical to an earlier
    /// hand card <c>i &lt; j</c> (excluding unreferenced <c>CardUid</c>). Playing either card produces the exact
    /// same post-state, so skipping duplicates eliminates redundant transitions soundly.
    /// </summary>
    private static ulong ComputeDuplicateHandMask(in CombatHotState s, HotColdSidecar? cold)
    {
        int handCount = Math.Min((int)s.HandCount, 64);
        if (handCount <= 1)
            return 0UL;

        ulong dupMask = 0UL;

        for (int j = 1; j < handCount; j++)
        {
            ref readonly HotCard cj = ref s.Cards[j];
            if (cj.CardUid != 0 && IsCardUidReferenced(in s, cold, cj.CardUid))
                continue;

            ReadOnlySpan<byte> bytesJ = HotStatePacker.AsReadOnlyBytes(in cj);
            for (int i = 0; i < j; i++)
            {
                if ((dupMask & (1UL << i)) != 0)
                    continue;
                ref readonly HotCard ci = ref s.Cards[i];
                if (ci.CardDefId != cj.CardDefId)
                    continue;
                if (ci.CardUid != 0 && IsCardUidReferenced(in s, cold, ci.CardUid))
                    continue;

                // Compare bytes [4..48] (everything after CardDefId [0..2] and CardUid [2..4])
                ReadOnlySpan<byte> bytesI = HotStatePacker.AsReadOnlyBytes(in ci);
                if (bytesI[4..].SequenceEqual(bytesJ[4..]))
                {
                    dupMask |= 1UL << j;
                    break;
                }
            }
        }

        return dupMask;
    }

    private static bool IsCardUidReferenced(in CombatHotState s, HotColdSidecar? cold, ushort uid)
    {
        if (s.HistoryCourseUid == uid)
            return true;
        if (s.Fool.FirstPlayCardUid[0] == uid || s.Fool.FirstPlayCardUid[1] == uid || s.Fool.FirstPlayCardUid[2] == uid)
            return true;

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            if (s.Powers[i].AuxCardUid == uid)
                return true;
        }

        if (cold != null)
        {
            if (cold.DecimalVars is { Length: > 0 } decs)
                for (int i = 0; i < decs.Length; i++) if (decs[i].CardUid == uid) return true;
            if (cold.CostOverflow is { Length: > 0 } co)
                for (int i = 0; i < co.Length; i++) if (co[i].CardUid == uid) return true;
            if (cold.RuptureEntries is { Length: > 0 } ru)
                for (int i = 0; i < ru.Length; i++) if (ru[i].CardUid == uid) return true;
            if (cold.DampenEntries is { Length: > 0 } da)
                for (int i = 0; i < da.Length; i++) if (da[i].CardUid == uid) return true;
        }

        return false;
    }

    /// <summary>
    /// Deterministically assigns dense non-zero <c>CardUid</c>s to all cards whose <c>CardUid == 0</c>
    /// after canonical unpacking, preserving any already-referenced <c>CardUid</c>s.
    /// Shared verbatim between expansion and <see cref="HotPlanMaterializer"/> replay so
    /// <see cref="HotChoiceSelection.CardUidOrIndex"/> aligns 100% across search and materialization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AssignDeterministicCardUids(ref CombatHotState s)
    {
        int total = s.TotalCardCount;
        ushort next = s.NextCardUid;
        if (next == 0)
            next = 1;
        for (int i = 0; i < total; i++)
        {
            if (s.Cards[i].CardUid == 0)
                s.Cards[i].CardUid = next++;
        }
        s.NextCardUid = next;
    }

    private void EnsureParentMapCapacity(int required)
    {
        if (required <= _parentMapCapacity)
            return;
        int next = _parentMapCapacity;
        while (next < required)
            next <<= 1;
        _parentOwner = new int[next];
        _parentSliceStart = new int[next];
        _parentSliceCount = new int[next];
        _parentMapCapacity = next;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (int i = 0; i < _workspaces.Length; i++)
            _workspaces[i].Dispose();
        _standPatCache.Clear();
    }
}
