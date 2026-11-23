using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;

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
    public byte PlayableHandCount;
    public short PotionCostDelta;
    public ushort PotionSlotBit;
    public byte PotionCountDelta;
    public byte ExplicitPotionDelta;
    public byte OptionalPaidCountDelta;
    public byte AmbergrisDelta;
    public HotNodeFlags Flags;
}

[InlineArray(HotLimits.MaxChoicesPerAction)]
internal struct HotChoiceInline
{
    private HotChoiceSelection _element0;
}

internal struct HotChoicePrefix
{
    public HotChoiceInline Selections;
    public int LastChoicePriority;
    public int BatchHistoryPlays;
    public ushort BatchSourceCardDefId;
    public ushort LastChoiceUid;
    public ushort BatchDrawCount;
    public HotChoiceKind BatchChoiceKind;
    public byte Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<HotChoiceSelection> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<HotChoiceInline, HotChoiceSelection>(ref Unsafe.AsRef(in Selections)),
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
    byte PlayableHandCount,
    byte RevivingEnemyCount,
    HotStateHandle CommittedHandle,
    HotColdSidecar? Cold,
    HotHash128 Hash);

/// <summary>
/// Thread-private lock-free expansion workspace owning reusable <see cref="CombatHotState"/> buffers,
/// <see cref="HotExecScratch"/>, local <see cref="PackedStateArena"/> staging slab, and choice stack.
/// Zero heap allocations after warm-up; reset between waves.
/// </summary>
internal sealed class HotExpansionWorkspace : IDisposable
{
    private const int InitialStagedChildCapacity = 1024;
    private const int InitialStagedChoiceCapacity = 512;
    private const int MaxChoiceStackItems = 512;

    public CombatHotState ParentState;
    public CombatHotState WorkingState;
    public CombatHotState EndTurnPrefixState;
    public readonly HotExecScratch Scratch = new();
    public readonly HotAction[] ActionBuf = new HotAction[256];
    public readonly HotChoiceSelection[] ChoiceOptBuf = new HotChoiceSelection[128];
    public readonly int[] ChoicePrioBuf = new int[128];
    public readonly HotChoicePrefix[] ChoiceStack = new HotChoicePrefix[MaxChoiceStackItems];
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
    public int WaveCapacityExceededDrops;
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
        WaveCapacityExceededDrops = 0;
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
    public readonly bool AllowPaidOptionalPotions;
    public readonly bool AllowZeroCostOptionalPotions;
    public readonly bool AllowForcedPotions;
    public readonly int MaxPotionUses;
    public readonly int MaxCardBranchesPerNode;
    public readonly int MaxHandChoiceBranches;
    public readonly int MaxPileChoiceBranches;
    public readonly HotEvalMode EvalMode;
    public readonly bool AllowEndTurn;

    public bool AllowAnyPotions => AllowPaidOptionalPotions || AllowZeroCostOptionalPotions || AllowForcedPotions;

    public HotWaveExpansionConfig(
        bool allowPaidOptionalPotions,
        bool allowZeroCostOptionalPotions,
        bool allowForcedPotions,
        int maxPotionUses,
        int maxCardBranchesPerNode,
        int maxHandChoiceBranches,
        int maxPileChoiceBranches,
        HotEvalMode evalMode = HotEvalMode.Balanced,
        bool allowEndTurn = true)
    {
        AllowPaidOptionalPotions = allowPaidOptionalPotions;
        AllowZeroCostOptionalPotions = allowZeroCostOptionalPotions;
        AllowForcedPotions = allowForcedPotions;
        MaxPotionUses = maxPotionUses;
        MaxCardBranchesPerNode = maxCardBranchesPerNode;
        MaxHandChoiceBranches = maxHandChoiceBranches;
        MaxPileChoiceBranches = maxPileChoiceBranches;
        EvalMode = evalMode;
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
    public int CapacityExceededDrops;
    public int ParallelWaves;
    public int ParallelWorkItems;
    public int MaxConcurrency;
    public long WorkerAllocatedBytes;
}

/// <summary>
/// Lock-free coarse-wave parallel expander using private worker <see cref="HotExpansionWorkspace"/>s
/// and strict parent-index ordered commit on the coordinator thread.
/// Guarantees 100% deterministic byte-for-byte parity between <c>DOP=1</c> and <c>DOP=8/16</c>,
/// graceful <see cref="HotCapacityExceededException"/> branch containment, sound potion dedup/no-op
/// hygiene, and <see cref="HotActionKind.TurnSetup"/> root expansion.
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
        _maxWorkers = Math.Clamp(maxDegreeOfParallelism, 1, HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);
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
            stats.CapacityExceededDrops += ws.WaveCapacityExceededDrops;
            stats.WorkerAllocatedBytes += ws.WaveAllocatedBytes;
        }

        // Strict deterministic ordered commit: iterate p = 0 .. parentCount - 1!
        for (int p = 0; p < parentCount; p++)
        {
            int parentNodeId = parentIds[p];
            ref readonly HotSearchNode parentNode = ref nodeArena.GetRef(parentNodeId);

            int parentTurn = parentNode.Turn;
            int parentActionsThisTurn = parentNode.ActionsThisTurn;
            ushort parentActionCount = parentNode.ActionCount;
            ushort parentPotionMask = parentNode.PotionMask;
            byte parentPotionCount = parentNode.PotionCount;
            byte parentExplicitPotions = parentNode.ExplicitPotionCount;
            byte parentOptionalPaid = parentNode.OptionalPaidPotionCount;
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
                byte nextOptionalPaid = (byte)Math.Min(byte.MaxValue, parentOptionalPaid + staged.OptionalPaidCountDelta);
                byte nextAmbergris = (byte)Math.Min(byte.MaxValue, parentAmbergris + staged.AmbergrisDelta);
                short nextPotionCost = (short)Math.Clamp(parentPotionCost + staged.PotionCostDelta, 0, short.MaxValue);
                ushort forcedMask = (ushort)(nextPotionMask & policy.ForcedPotionSlotMask);

                bool isOngoing = staged.Summary.Outcome == HotCombatOutcome.Ongoing && !staged.Summary.IsDefeat;

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

                int choiceOffset = -1;
                if (staged.ChoiceCount > 0 && staged.ChoiceOffset >= 0)
                {
                    if (!nodeArena.TryAppendChoices(
                            ws.StagedChoices.AsSpan(staged.ChoiceOffset, staged.ChoiceCount),
                            out choiceOffset))
                    {
                        stats.CapacityExceededDrops++;
                        continue;
                    }
                }

                HotStateHandle committedHandle = default;
                if (isOngoing)
                {
                    Span<byte> mainDst = mainStateArena.Reserve(stagedBytes.Length);
                    stagedBytes.CopyTo(mainDst);
                    committedHandle = mainStateArena.CommitReserved(stagedBytes.Length);

                    if (!disableTranspositionPruning)
                        tt.InsertAdmitted(in probe, committedHandle, staged.Cold, mainStateArena);
                }

                bool isTurnSetup = staged.Action.Kind == HotActionKind.TurnSetup;
                bool endedTurn = staged.Action.Kind == HotActionKind.EndTurn || staged.Summary.Turn > parentTurn;
                byte nextActionsThisTurn = (endedTurn || isTurnSetup)
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
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, short.MinValue, short.MaxValue),
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
                    OptionalPaidPotionCount = nextOptionalPaid,
                    AmbergrisCount = nextAmbergris,
                    AlchemizeCount = staged.Summary.AlchemizeCount,
                    EmptyPotionSlots = staged.Summary.EmptyPotionSlots,
                    Energy = (byte)Math.Clamp(staged.Summary.Energy, 0, byte.MaxValue),
                    Turn = (byte)Math.Clamp(staged.Summary.Turn, 0, byte.MaxValue),
                    ActionsThisTurn = nextActionsThisTurn,
                    AliveEnemyCount = staged.Summary.AliveEnemyCount,
                    RevivingEnemyCount = staged.Summary.RevivingEnemyCount,
                    PlayableHandCount = staged.PlayableHandCount,
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
                    if (HotPotionPolicy.SatisfiesHardConstraints(in defKey, in policy)
                        && (!hasBestFallback || HotRouteQuality.IsBetter(in defKey, in bestFallbackKey, in policy)))
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
    /// Runs lightweight Stand-Pat (<c>EndTurn</c>) lookahead on selected active candidates
    /// (cached by <see cref="HotHash128"/>, executed in parallel read-only batch when <c>DOP &gt; 1</c>).
    /// </summary>
    public void RunStandPatBatch(
        ReadOnlySpan<int> candidateIds,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
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
                        ws, missNodeIds[m], nodeArena, mainStateArena, rules, in policyCopy, in lookupCopy, evalMode, out results[m]);
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
                            ws, missNodeIds[m], nodeArena, mainStateArena, rules, in policyCopy, in lookupCopy, evalMode, out results[m]);
                    }
                });
            }

            for (int w = 0; w < workers; w++)
                stats.CapacityExceededDrops += _workspaces[wsBase + w].WaveCapacityExceededDrops;

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
                    PlayableHandCount: staged.PlayableHandCount,
                    RevivingEnemyCount: staged.Summary.RevivingEnemyCount,
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
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, short.MinValue, short.MaxValue),
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
                    OptionalPaidPotionCount = parent.OptionalPaidPotionCount,
                    AmbergrisCount = parent.AmbergrisCount,
                    AlchemizeCount = staged.Summary.AlchemizeCount,
                    EmptyPotionSlots = staged.Summary.EmptyPotionSlots,
                    Energy = (byte)Math.Clamp(staged.Summary.Energy, 0, byte.MaxValue),
                    Turn = (byte)Math.Clamp(staged.Summary.Turn, 0, byte.MaxValue),
                    ActionsThisTurn = 0,
                    AliveEnemyCount = staged.Summary.AliveEnemyCount,
                    RevivingEnemyCount = staged.Summary.RevivingEnemyCount,
                    PlayableHandCount = staged.PlayableHandCount,
                    DeathSaveUseCount = staged.Summary.DeathSaveUseCount,
                    Outcome = staged.Summary.Outcome,
                    Flags = HotNodeFlags.EndedTurn | HotNodeFlags.StandPat
                        | (staged.Summary.Outcome != HotCombatOutcome.Ongoing ? HotNodeFlags.IsTerminal : HotNodeFlags.None),
                };

                int childId = nodeArena.AddNode(in childNode);
                if (childNode.IsVictory)
                {
                    victoriesOut.Add(childId);
                }
                else if (childNode.IsDefeat)
                {
                    HotRouteKey key = HotRouteQuality.Capture(in childNode, in policy);
                    if (HotPotionPolicy.SatisfiesHardConstraints(in key, in policy)
                        && (!hasBestFallback || HotRouteQuality.IsBetter(in key, in bestFallbackKey, in policy)))
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
        HotEvalMode evalMode,
        out HotStagedChild staged)
    {
        ref readonly HotSearchNode parent = ref nodeArena.GetRef(parentNodeId);
        HotStatePacker.Unpack(mainStateArena.GetSpan(parent.StateHandle), ref ws.WorkingState);
        AssignDeterministicCardUids(ref ws.WorkingState);
        HotColdSidecar? cold = parent.Cold;

        HotStepResult res;
        try
        {
            res = HotCombatEngine.Step(
                ref ws.WorkingState,
                ref cold,
                rules,
                ws.Scratch,
                HotAction.EndTurn(),
                default);
        }
        catch (HotCapacityExceededException)
        {
            ws.WaveCapacityExceededDrops++;
            staged = default;
            return false;
        }

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
        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            parent.PotionStrategicCost,
            parent.OptionalPaidPotionCount,
            summary.AlchemizeCount,
            summary.EmptyPotionSlots,
            in policy);

        HotEvaluator.EvaluateState(
            in ws.WorkingState,
            cold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            netPotionCostCenti,
            parent.ActionCount + 1,
            evalMode,
            out long score,
            out int setupScore,
            out byte playableHandCount);

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
            PlayableHandCount = playableHandCount,
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

        HotStatePacker.Unpack(mainStateArena.GetSpan(parent.StateHandle), ref ws.ParentState);
        HotColdSidecar? parentCold = parent.Cold;

        bool allowPotions = config.AllowAnyPotions
            && (config.MaxPotionUses < 0 || parent.ExplicitPotionCount < config.MaxPotionUses);
        bool canPlayMoreThisTurn = parent.ActionsThisTurn < HotBeamRetention.MaxActionsPerTurn;

        ulong duplicateHandMask = canPlayMoreThisTurn
            ? ComputeDuplicateHandMask(in ws.ParentState, parentCold)
            : 0UL;

        AssignDeterministicCardUids(ref ws.ParentState);

        int actionCount;
        try
        {
            actionCount = HotCombatEngine.EnumerateLegalActions(
                in ws.ParentState,
                parentCold,
                rules,
                ws.ActionBuf,
                includePotions: allowPotions);
        }
        catch (HotCapacityExceededException)
        {
            ws.WaveCapacityExceededDrops++;
            return;
        }

        // Pre-compute duplicate unprotected potion slot mask (only expand lowest-index slot of identical
        // (PotionDefId, IsGenerated, IsForced) potions!).
        ushort duplicatePotionSlotMask = allowPotions
            ? ComputeDuplicatePotionSlotMask(in ws.ParentState, rules, rootPotionCosts, in policy, in config, parent.ExplicitPotionCount)
            : (ushort)0;

        int cardBranchesEmitted = 0;

        for (int a = 0; a < actionCount; a++)
        {
            HotAction action = ws.ActionBuf[a];

            if (action.Kind == HotActionKind.TurnSetup)
            {
                try
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
                        action,
                        potionCostDelta: 0,
                        potionSlotBit: 0,
                        optionalPaidCountDelta: 0,
                        explicitPotionDelta: 0,
                        ambergrisDelta: 0);
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                }
                continue;
            }

            if (action.Kind == HotActionKind.EndTurn)
            {
                if (!config.AllowEndTurn)
                    continue;
                try
                {
                    ExpandEndTurnAction(
                        ws,
                        in parent,
                        parentCold,
                        rules,
                        in policy,
                        in evalLookup,
                        in config,
                        in frozenIncumbent);
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                }
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
                try
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
                        action,
                        potionCostDelta: 0,
                        potionSlotBit: 0,
                        optionalPaidCountDelta: 0,
                        explicitPotionDelta: 0,
                        ambergrisDelta: 0);
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                }
            }
            else if (action.Kind == HotActionKind.UsePotion)
            {
                int slot = action.SlotOrHandIdx;
                if ((uint)slot >= (uint)ws.ParentState.PotionSlotCount || (uint)slot >= HotLimits.MaxPotions)
                    continue;
                if ((duplicatePotionSlotMask & (ushort)(1 << slot)) != 0)
                    continue;

                ushort pDefId = ws.ParentState.Potions[slot];
                if (!HotPotionPolicy.TryClassifyPotionUseForPass(
                        in ws.ParentState,
                        slot,
                        pDefId,
                        rules,
                        rootPotionCosts,
                        in policy,
                        config.AllowPaidOptionalPotions,
                        config.AllowZeroCostOptionalPotions,
                        config.AllowForcedPotions,
                        parent.ExplicitPotionCount,
                        config.MaxPotionUses,
                        out _,
                        out bool isForced,
                        out short optionalPaidCostDelta,
                        out byte optionalPaidCountDelta,
                        out ushort rootPotionSlotBit))
                {
                    continue;
                }

                HotPotionDef pDef = rules.Potions[pDefId];
                if (HotPotionPolicy.IsProvableNoOpPotionUse(in ws.ParentState, pDef, isForced, in policy))
                    continue;

                byte isAmbergris = HotPotionPolicy.ResolvePotionId(pDef) == HotCustomPotionId.Ambergris
                    ? (byte)1
                    : (byte)0;

                try
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
                        action,
                        potionCostDelta: optionalPaidCostDelta,
                        potionSlotBit: rootPotionSlotBit,
                        optionalPaidCountDelta: optionalPaidCountDelta,
                        explicitPotionDelta: 1,
                        ambergrisDelta: isAmbergris);
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                }
            }
        }
    }

    /// <summary>
    /// Deduplicates identical allowed potions across slots by <c>(PotionDefId, IsGenerated, IsForced)</c>,
    /// expanding only the lowest-index slot in each equivalence class.
    /// </summary>
    private static ushort ComputeDuplicatePotionSlotMask(
        in CombatHotState s,
        HotRules rules,
        short[] rootPotionCosts,
        in HotSearchPolicy policy,
        in HotWaveExpansionConfig config,
        int currentExplicitPotionCount)
    {
        int slotCount = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        if (slotCount <= 1)
            return 0;

        Span<ushort> seenDef = stackalloc ushort[HotLimits.MaxPotions];
        Span<byte> seenFlags = stackalloc byte[HotLimits.MaxPotions];
        Span<short> seenCost = stackalloc short[HotLimits.MaxPotions];
        int seenCount = 0;
        ushort dupMask = 0;

        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = s.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;

            if (!HotPotionPolicy.TryClassifyPotionUseForPass(
                    in s,
                    slot,
                    defId,
                    rules,
                    rootPotionCosts,
                    in policy,
                    config.AllowPaidOptionalPotions,
                    config.AllowZeroCostOptionalPotions,
                    config.AllowForcedPotions,
                    currentExplicitPotionCount,
                    config.MaxPotionUses,
                    out bool isGenerated,
                    out bool isForced,
                    out short costDelta,
                    out _,
                    out _))
            {
                continue;
            }

            byte flags = (byte)((isGenerated ? 1 : 0) | (isForced ? 2 : 0));
            bool duplicate = false;
            for (int k = 0; k < seenCount; k++)
            {
                if (seenDef[k] == defId && seenFlags[k] == flags && seenCost[k] == costDelta)
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                dupMask |= (ushort)(1 << slot);
            }
            else
            {
                seenDef[seenCount] = defId;
                seenFlags[seenCount] = flags;
                seenCost[seenCount] = costDelta;
                seenCount++;
            }
        }

        return dupMask;
    }

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
                optionalPaidCountDelta: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

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
                in config,
                in frozenIncumbent,
                HotAction.EndTurn(),
                default,
                potionCostDelta: 0,
                potionSlotBit: 0,
                optionalPaidCountDelta: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

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
                in config,
                in frozenIncumbent,
                HotAction.EndTurn(),
                default,
                potionCostDelta: 0,
                potionSlotBit: 0,
                optionalPaidCountDelta: 0,
                explicitPotionDelta: 0,
                ambergrisDelta: 0);
            return;
        }

        ws.WaveEndTurnPrefixCaptures++;
        ExpandChoiceTree(
            ws,
            in parent,
            in ws.EndTurnPrefixState,
            prefixCold,
            turnCold,
            rules,
            in policy,
            in evalLookup,
            in config,
            in frozenIncumbent,
            HotAction.EndTurn(),
            isEndTurnStartPrefix: true,
            potionCostDelta: 0,
            potionSlotBit: 0,
            optionalPaidCountDelta: 0,
            explicitPotionDelta: 0,
            ambergrisDelta: 0);
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
        byte optionalPaidCountDelta,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
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
                in config,
                in frozenIncumbent,
                action,
                default,
                potionCostDelta,
                potionSlotBit,
                optionalPaidCountDelta,
                explicitPotionDelta,
                ambergrisDelta);
            return;
        }

        ExpandChoiceTree(
            ws,
            in parent,
            in ws.ParentState,
            parentCold,
            childCold,
            rules,
            in policy,
            in evalLookup,
            in config,
            in frozenIncumbent,
            action,
            isEndTurnStartPrefix: false,
            potionCostDelta,
            potionSlotBit,
            optionalPaidCountDelta,
            explicitPotionDelta,
            ambergrisDelta);
    }

    private static void ExpandChoiceTree(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        in CombatHotState replayBaseState,
        HotColdSidecar? replayBaseCold,
        HotColdSidecar? firstPausedCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        HotAction action,
        bool isEndTurnStartPrefix,
        short potionCostDelta,
        ushort potionSlotBit,
        byte optionalPaidCountDelta,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
        int firstOptCount = HotCombatEngine.EnumerateChoiceOptions(
            in ws.WorkingState,
            firstPausedCold,
            rules,
            ws.Scratch,
            ws.ChoiceOptBuf);
        if (firstOptCount <= 0)
            return;

        HotChoiceKind firstKind = ws.Scratch.PendingChoiceKind;
        ushort firstSourceDefId = ws.Scratch.PendingChoiceSourceCardDefId;
        int firstHistoryPlays = ws.WorkingState.HistoryFinishedPlays;
        ushort firstDrawCount = ws.WorkingState.DrawCount;
        int maxBranches = ResolveChoiceBranchCap(firstKind, in config);

        int sortedFirstCount = PrepareChoiceOptions(
            in ws.WorkingState,
            firstPausedCold,
            rules,
            firstKind,
            firstSourceDefId,
            ws.ChoiceOptBuf,
            ws.ChoicePrioBuf,
            firstOptCount,
            hasPriorBatch: false,
            priorPrefix: default,
            enforceCanonicalOrder: false,
            out bool hasNoneOption);

        HotChoicePrefix[] stack = ws.ChoiceStack;
        int sp = 0;

        int firstLimit = Math.Min(sortedFirstCount, maxBranches);
        for (int i = firstLimit - 1; i >= 0 && sp < stack.Length; i--)
        {
            HotChoiceSelection opt = ws.ChoiceOptBuf[i];
            HotChoicePrefix pfx = default;
            pfx.Selections[0] = opt;
            pfx.Count = 1;
            pfx.BatchChoiceKind = firstKind;
            pfx.BatchSourceCardDefId = firstSourceDefId;
            pfx.BatchHistoryPlays = firstHistoryPlays;
            pfx.BatchDrawCount = firstDrawCount;
            pfx.LastChoicePriority = ws.ChoicePrioBuf[i];
            pfx.LastChoiceUid = opt.CardUidOrIndex;
            stack[sp++] = pfx;
        }

        if (hasNoneOption && sp < stack.Length)
        {
            HotChoicePrefix nonePfx = default;
            nonePfx.Selections[0] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            nonePfx.Count = 1;
            stack[sp++] = nonePfx;
        }

        int resolvedCount = 0;
        int maxWorkSteps = Math.Max(maxBranches * 6, 96);
        int workSteps = 0;

        while (sp > 0 && resolvedCount < maxBranches && workSteps < maxWorkSteps)
        {
            HotChoicePrefix pfx = stack[--sp];
            workSteps++;

            ws.WorkingState = replayBaseState;
            HotColdSidecar? branchCold = replayBaseCold;
            if (isEndTurnStartPrefix)
                ws.WaveEndTurnPrefixReuses++;
            ws.WaveChoiceBranches++;

            HotStepResult res;
            try
            {
                res = isEndTurnStartPrefix
                    ? HotCombatEngine.StepStartPlayerTurn(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, pfx.AsSpan())
                    : HotCombatEngine.Step(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, action, pfx.AsSpan());
            }
            catch (HotCapacityExceededException)
            {
                ws.WaveCapacityExceededDrops++;
                continue;
            }

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
                    in config,
                    in frozenIncumbent,
                    action,
                    pfx.AsSpan(),
                    potionCostDelta,
                    potionSlotBit,
                    optionalPaidCountDelta,
                    explicitPotionDelta,
                    ambergrisDelta);
            }
            else if (pfx.Count < HotLimits.MaxChoicesPerAction)
            {
                int subOptCount;
                try
                {
                    subOptCount = HotCombatEngine.EnumerateChoiceOptions(
                        in ws.WorkingState,
                        branchCold,
                        rules,
                        ws.Scratch,
                        ws.ChoiceOptBuf);
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                    continue;
                }

                if (subOptCount <= 0)
                    continue;

                HotChoiceKind subKind = ws.Scratch.PendingChoiceKind;
                ushort subSourceDefId = ws.Scratch.PendingChoiceSourceCardDefId;
                int subHistoryPlays = ws.WorkingState.HistoryFinishedPlays;
                ushort subDrawCount = ws.WorkingState.DrawCount;

                bool sameBatch = IsPileChoiceKind(subKind)
                    && subKind == pfx.BatchChoiceKind
                    && subSourceDefId == pfx.BatchSourceCardDefId
                    && subHistoryPlays == pfx.BatchHistoryPlays;

                bool enforceCanonical = sameBatch && subDrawCount == pfx.BatchDrawCount;

                int subPreparedCount = PrepareChoiceOptions(
                    in ws.WorkingState,
                    branchCold,
                    rules,
                    subKind,
                    subSourceDefId,
                    ws.ChoiceOptBuf,
                    ws.ChoicePrioBuf,
                    subOptCount,
                    hasPriorBatch: sameBatch,
                    priorPrefix: in pfx,
                    enforceCanonicalOrder: enforceCanonical,
                    out bool subHasNone);

                if (subHasNone && sp < stack.Length)
                {
                    HotChoicePrefix stopPfx = pfx;
                    stopPfx.Selections[stopPfx.Count] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                    stopPfx.Count++;
                    stack[sp++] = stopPfx;
                }

                int subLimit = Math.Min(subPreparedCount, maxBranches);
                for (int i = subLimit - 1; i >= 0 && sp < stack.Length; i--)
                {
                    HotChoiceSelection opt = ws.ChoiceOptBuf[i];
                    HotChoicePrefix nextPfx = pfx;
                    nextPfx.Selections[nextPfx.Count] = opt;
                    nextPfx.Count++;
                    nextPfx.BatchChoiceKind = subKind;
                    nextPfx.BatchSourceCardDefId = subSourceDefId;
                    nextPfx.BatchHistoryPlays = subHistoryPlays;
                    nextPfx.BatchDrawCount = subDrawCount;
                    nextPfx.LastChoicePriority = ws.ChoicePrioBuf[i];
                    nextPfx.LastChoiceUid = opt.CardUidOrIndex;
                    stack[sp++] = nextPfx;
                }
            }
            else
            {
                // Branch exceeded HotLimits.MaxChoicesPerAction (16): gracefully drop instead of crashing.
                ws.WaveCapacityExceededDrops++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPileChoiceKind(HotChoiceKind kind)
        => kind is HotChoiceKind.HandCard or HotChoiceKind.DrawPile or HotChoiceKind.DiscardPile or HotChoiceKind.ExhaustPile;

    private static int PrepareChoiceOptions(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotChoiceKind kind,
        ushort sourceCardDefId,
        HotChoiceSelection[] optBuf,
        int[] prioBuf,
        int rawCount,
        bool hasPriorBatch,
        in HotChoicePrefix priorPrefix,
        bool enforceCanonicalOrder,
        out bool hasNoneOption)
    {
        hasNoneOption = false;
        int outCount = 0;

        for (int i = 0; i < rawCount; i++)
        {
            HotChoiceSelection opt = optBuf[i];
            if (opt.Kind == HotChoiceKind.None)
            {
                hasNoneOption = true;
                continue;
            }

            if (hasPriorBatch && IsPileChoiceKind(opt.Kind))
            {
                if (IsUidInPrefix(in priorPrefix, opt.Kind, opt.CardUidOrIndex))
                {
                    if (!TryFindAlternateEquivalentUid(in s, opt.Kind, opt.CardUidOrIndex, in priorPrefix, out ushort altUid))
                        continue;
                    opt = new HotChoiceSelection(opt.Kind, altUid, opt.CardDefId);
                }
            }

            int prio = ComputeChoiceOptionPriority(in s, cold, rules, kind, sourceCardDefId, opt);

            if (enforceCanonicalOrder)
            {
                if (prio > priorPrefix.LastChoicePriority)
                    continue;
                if (prio == priorPrefix.LastChoicePriority && opt.CardUidOrIndex <= priorPrefix.LastChoiceUid)
                    continue;
            }

            bool dupOut = false;
            for (int j = 0; j < outCount; j++)
            {
                if (optBuf[j].Kind == opt.Kind && optBuf[j].CardUidOrIndex == opt.CardUidOrIndex)
                {
                    dupOut = true;
                    break;
                }
            }
            if (dupOut)
                continue;

            optBuf[outCount] = opt;
            prioBuf[outCount] = prio;
            outCount++;
        }

        for (int i = 1; i < outCount; i++)
        {
            HotChoiceSelection keyOpt = optBuf[i];
            int keyPrio = prioBuf[i];
            int j = i - 1;
            while (j >= 0 && (prioBuf[j] < keyPrio
                || (prioBuf[j] == keyPrio && optBuf[j].CardUidOrIndex > keyOpt.CardUidOrIndex)))
            {
                optBuf[j + 1] = optBuf[j];
                prioBuf[j + 1] = prioBuf[j];
                j--;
            }
            optBuf[j + 1] = keyOpt;
            prioBuf[j + 1] = keyPrio;
        }

        return outCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUidInPrefix(in HotChoicePrefix pfx, HotChoiceKind kind, ushort uid)
    {
        ReadOnlySpan<HotChoiceSelection> span = pfx.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Kind == kind && span[i].CardUidOrIndex == uid)
                return true;
        }
        return false;
    }

    private static bool TryFindAlternateEquivalentUid(
        in CombatHotState s,
        HotChoiceKind kind,
        ushort pickedUid,
        in HotChoicePrefix pfx,
        out ushort altUid)
    {
        HotPileKind pile = kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };

        int start = HotPiles.ZoneStart(in s, pile);
        int end = start + HotPiles.ZoneCount(in s, pile);
        int pickedIdx = -1;
        for (int i = start; i < end; i++)
        {
            if (s.Cards[i].CardUid == pickedUid)
            {
                pickedIdx = i;
                break;
            }
        }
        if (pickedIdx < 0)
        {
            altUid = 0;
            return false;
        }

        ref readonly HotCard pickedCard = ref s.Cards[pickedIdx];
        ReadOnlySpan<byte> pickedBytes = HotStatePacker.AsReadOnlyBytes(in pickedCard);

        for (int i = start; i < end; i++)
        {
            if (i == pickedIdx)
                continue;
            ref readonly HotCard cand = ref s.Cards[i];
            if (cand.CardDefId != pickedCard.CardDefId || cand.CardUid == 0)
                continue;
            if (IsUidInPrefix(in pfx, kind, cand.CardUid))
                continue;

            ReadOnlySpan<byte> candBytes = HotStatePacker.AsReadOnlyBytes(in cand);
            if (pickedBytes[4..].SequenceEqual(candBytes[4..]))
            {
                altUid = cand.CardUid;
                return true;
            }
        }

        altUid = 0;
        return false;
    }

    private static int ComputeChoiceOptionPriority(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotChoiceKind kind,
        ushort sourceCardDefId,
        HotChoiceSelection opt)
    {
        if (opt.CardDefId == 0 || opt.CardDefId >= rules.Cards.Length)
            return 0;

        HotCardDef def = rules.Cards[opt.CardDefId];
        HotCustomCardId sourceId = sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length
            ? rules.Cards[sourceCardDefId].CardId
            : HotCustomCardId.None;

        bool isDiscardOrExhaustChoice = kind == HotChoiceKind.HandCard
            && sourceId is HotCustomCardId.Purity
                or HotCustomCardId.Prepared
                or HotCustomCardId.Survivor
                or HotCustomCardId.TrueGrit
                or HotCustomCardId.BurningPact;

        int quality = 0;
        if (def.Type is CardType.Curse or CardType.Status)
        {
            quality = -400;
        }
        else if (def.IsStrikeTag || def.IsDefendTag)
        {
            quality = 60;
        }
        else
        {
            quality = def.Type switch
            {
                CardType.Power => 280,
                CardType.Skill => 200,
                CardType.Attack => 190,
                _ => 120,
            };
            quality += def.Rarity switch
            {
                CardRarity.Rare => 90,
                CardRarity.Uncommon => 50,
                CardRarity.Common => 20,
                _ => 10,
            };
            quality += Math.Min((int)def.SetupWeight * 4, 140);
            int baseCost = def.BaseCost;
            if (baseCost == 0)
                quality += 45;
            else if (baseCost == 1)
                quality += 20;
        }

        if (IsPileChoiceKind(opt.Kind))
        {
            HotPileKind pile = opt.Kind switch
            {
                HotChoiceKind.HandCard => HotPileKind.Hand,
                HotChoiceKind.DrawPile => HotPileKind.Draw,
                HotChoiceKind.DiscardPile => HotPileKind.Discard,
                _ => HotPileKind.Exhaust,
            };
            int idx = HotCombatEngine.FindCardIndexByUid(in s, pile, opt.CardUidOrIndex);
            if (idx >= 0)
            {
                ref readonly HotCard c = ref s.Cards[idx];
                if ((c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                    quality = -450;
                else
                {
                    if (c.UpgradeLevel > 0)
                        quality += 35;
                    int liveCost = HotCostModifiers.GetLocalEnergyCost(in c, cold);
                    if (liveCost == 0)
                        quality += 30;
                }
            }
        }

        return isDiscardOrExhaustChoice ? -quality : quality;
    }

    private static void StageCompletedTransition(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotColdSidecar? childCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices,
        short potionCostDelta,
        ushort potionSlotBit,
        byte optionalPaidCountDelta,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
        byte autoPotionDelta = DetectAutoPotionDelta(in ws.ParentState, in ws.WorkingState, explicitPotionDelta);
        byte totalPotionDelta = (byte)(explicitPotionDelta + autoPotionDelta);

        HotFastSummary summary = HotEvaluator.ExtractStage1(in ws.WorkingState, in policy);

        int nextExplicit = parent.ExplicitPotionCount + explicitPotionDelta;
        int nextOptionalPaid = parent.OptionalPaidPotionCount + optionalPaidCountDelta;
        short nextPotionCost = (short)Math.Clamp(parent.PotionStrategicCost + potionCostDelta, 0, short.MaxValue);

        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            nextPotionCost,
            nextOptionalPaid,
            summary.AlchemizeCount,
            summary.EmptyPotionSlots,
            in policy);

        if (HotEvaluator.IsPrunedByCertifiedIncumbent(
                in summary,
                nextOptionalPaid,
                nextExplicit,
                netPotionCostCenti,
                in frozenIncumbent,
                in policy))
        {
            ws.WaveIncumbentPrunes++;
            return;
        }

        int packedSize = HotStatePacker.ComputePackedSize(in ws.WorkingState);
        Span<byte> buf = ws.StagingArena.Reserve(packedSize);
        int written = HotStatePacker.Pack(ref ws.WorkingState, ref childCold, buf, canonicalize: true);
        HotStateHandle stagedHandle = ws.StagingArena.CommitReserved(written);
        HotHash128 hash = HotHash.ComputeHash(ws.StagingArena.GetSpan(stagedHandle), childCold);

        HotEvaluator.EvaluateState(
            in ws.WorkingState,
            childCold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            netPotionCostCenti,
            parent.ActionCount + 1,
            config.EvalMode,
            out long score,
            out int setupScore,
            out byte playableHandCount);

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
            PlayableHandCount = playableHandCount,
            PotionCostDelta = potionCostDelta,
            PotionSlotBit = potionSlotBit,
            PotionCountDelta = totalPotionDelta,
            ExplicitPotionDelta = explicitPotionDelta,
            OptionalPaidCountDelta = optionalPaidCountDelta,
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
    }
}
