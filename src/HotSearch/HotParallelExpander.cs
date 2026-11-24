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
    public HotEvalFeatures Eval;
    public long RolloutScore;
    public int ChoiceOffset;
    public HotAction Action;
    public byte ChoiceCount;
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
    public ushort LastGroupSeq;
    public ushort LastChoiceUid;
    public HotChoiceGroupFlags LastGroupFlags;
    public byte Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<HotChoiceSelection> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<HotChoiceInline, HotChoiceSelection>(ref Unsafe.AsRef(in Selections)),
            Count);
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotResolvedChoice
{
    public HotChoicePrefix Prefix;
    public HotStateHandle StagedHandle;
    public HotColdSidecar? Cold;
    public HotHash128 Hash;
    public HotFastSummary Summary;
    public HotEvalFeatures Eval;
    public long LookaheadScore;
    public int LookaheadHpLost;
    public int LookaheadBoardInvestment;
    public int LookaheadSetupScore;
    public bool HasLookahead;
    public bool LookaheadVictory;
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
    internal const int ChoiceLayerCapacity = 256;
    internal const int ChoiceResolvedCapacity = 160;
    internal const int ChoiceTtSize = 512;
    internal const int ChoiceTtMask = ChoiceTtSize - 1;

    public CombatHotState ParentState;
    public CombatHotState WorkingState;
    public CombatHotState EndTurnPrefixState;
    public CombatHotState ChoiceProbeState;
    public readonly HotExecScratch Scratch = new();
    public readonly HotExecScratch ChoiceProbeScratch = new();
    public readonly HotAction[] ActionBuf = new HotAction[256];
    public readonly HotAction[] ChoiceProbeActionBuf = new HotAction[128];
    public readonly HotChoiceSelection[] ChoiceOptBuf = new HotChoiceSelection[128];
    public readonly HotChoiceSelection[] ChoiceSubOptBuf = new HotChoiceSelection[128];
    public readonly HotChoiceSelection[] ChoiceProbeChoiceBuf = new HotChoiceSelection[128];
    public readonly HotChoicePrefix[] ChoiceLayerA = new HotChoicePrefix[ChoiceLayerCapacity];
    public readonly HotChoicePrefix[] ChoiceLayerB = new HotChoicePrefix[ChoiceLayerCapacity];
    public readonly HotResolvedChoice[] ChoiceResolved = new HotResolvedChoice[ChoiceResolvedCapacity];
    public readonly HotHash128[] LocalChoiceTt = new HotHash128[ChoiceTtSize];
    public readonly byte[] LocalChoiceTtUsed = new byte[ChoiceTtSize];
    public readonly long[] MarginalScoreBuf = new long[128];
    public readonly int[] MarginalOrderBuf = new int[128];
    public readonly byte[] ResolvedSelectedMarks = new byte[ChoiceResolvedCapacity];
    // Exact 256 KiB total rent size (blockPayloadBytes + MaxPackedStateBytes == 262,144) so ArrayPool never rounds up 2x!
    public readonly PackedStateArena StagingArena = new(blockPayloadBytes: (256 * 1024) - HotLimits.MaxPackedStateBytes);

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
    public readonly bool EnableTurnBoundaryRollouts;

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
        bool allowEndTurn = true,
        bool enableTurnBoundaryRollouts = true)
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
        EnableTurnBoundaryRollouts = enableTurnBoundaryRollouts;
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
        // ExpandAndCommitWave and RunStandPatBatch never execute concurrently, so 1 workspace per worker suffices!
        _workspaces = new HotExpansionWorkspace[_maxWorkers];
        for (int i = 0; i < _maxWorkers; i++)
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

                bool isOngoing = staged.Summary.Outcome == HotCombatOutcome.Ongoing
                    && !staged.Summary.IsDefeat
                    && !staged.Summary.IsEventDefeat;

                HotTtProbeData probe = new(
                    staged.Hash,
                    staged.Eval.Score,
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
                if (endedTurn && staged.ChoiceCount == 0)
                    flags &= ~HotNodeFlags.HasChoiceLookahead;
                if (endedTurn) flags |= HotNodeFlags.EndedTurn;
                if (staged.ChoiceCount > 0) flags |= HotNodeFlags.HasChoice;
                if (staged.Summary.DeathSaveUseCount > 0) flags |= HotNodeFlags.HasDeathSave;
                if (staged.Summary.ForfeitsGold) flags |= HotNodeFlags.ForfeitsGold;
                if (!isOngoing) flags |= HotNodeFlags.IsTerminal;
                if (staged.Summary.AllGrowthTargetsSatisfied) flags |= HotNodeFlags.AllGrowthTargetsSatisfied;
                else flags &= ~HotNodeFlags.AllGrowthTargetsSatisfied;
                if (staged.Summary.AllRelicTargetsSatisfied) flags |= HotNodeFlags.AllRelicTargetsSatisfied;
                else flags &= ~HotNodeFlags.AllRelicTargetsSatisfied;

                HotSearchNode childNode = new()
                {
                    Hash = staged.Hash,
                    Score = staged.Eval.Score,
                    RolloutScore = (flags & HotNodeFlags.HasChoiceLookahead) != 0 ? staged.RolloutScore : 0,
                    Cold = staged.Cold,
                    StateHandle = committedHandle,
                    ParentId = parentNodeId,
                    ChoiceOffset = choiceOffset,
                    HpLost = staged.Summary.HpLost,
                    HpRecovered = staged.Summary.HpRecovered,
                    PlayerHp = staged.Summary.PlayerHp,
                    EnemyHp = staged.Summary.EnemyHpSum,
                    SetupScore = staged.Eval.SetupScore,
                    BoardInvestment = staged.Eval.BoardInvestment,
                    MaxDarkEvoke = staged.Eval.MaxDarkEvoke,
                    TotalPoisonAndDoom = staged.Eval.TotalPoisonAndDoom,
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, short.MinValue, short.MaxValue),
                    PlayerBlock = (short)Math.Clamp(staged.Summary.PlayerBlock, 0, short.MaxValue),
                    EffectiveDefense = staged.Eval.EffectiveDefense,
                    OptionalityScore = staged.Eval.OptionalityScore,
                    OutstandingStolen = (short)Math.Clamp(staged.Summary.OutstandingStolen, 0, short.MaxValue),
                    GrowthHpCredit = (short)Math.Clamp(staged.Summary.GrowthHpCredit, short.MinValue, short.MaxValue),
                    RelicHpCredit = staged.Summary.RelicHpCredit,
                    RelicHealingHpCredit = staged.Summary.RelicHealingHpCredit,
                    RelicSatisfiedMask = staged.Summary.RelicSatisfiedMask,
                    RelicDistance = staged.Summary.RelicDistance,
                    RelicSatisfiedPriority = staged.Summary.RelicSatisfiedPriority,
                    FeedCount = staged.Summary.FeedCount,
                    PotionStrategicCost = nextPotionCost,
                    DeathSaveHpRestored = (short)Math.Clamp(staged.Summary.DeathSaveHpRestored, 0, short.MaxValue),
                    PotionMask = nextPotionMask,
                    ActionCount = nextActionCount,
                    GrowthRewardCount = (ushort)Math.Clamp(staged.Summary.GrowthRewardCount, 0, ushort.MaxValue),
                    Flags = flags,
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
                    PlayableHandCount = staged.Eval.PlayableHandCount,
                    DeathSaveUseCount = staged.Summary.DeathSaveUseCount,
                    Outcome = staged.Summary.Outcome,
                };

                int childNodeId = nodeArena.AddNode(in childNode);

                if (childNode.IsVictory)
                {
                    victoriesOut.Add(childNodeId);
                }
                else if (childNode.IsDefeat || childNode.IsEventDefeat)
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
            const int wsBase = 0;
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
                    Score: staged.Eval.Score,
                    SetupScore: staged.Eval.SetupScore,
                    PlayableHandCount: staged.Eval.PlayableHandCount,
                    RevivingEnemyCount: staged.Summary.RevivingEnemyCount,
                    CommittedHandle: committed,
                    Cold: staged.Cold,
                    Hash: staged.Hash);
                _standPatCache[parent.Hash] = cached;

                HotNodeFlags standPatFlags = HotNodeFlags.EndedTurn | HotNodeFlags.StandPat
                    | (staged.Summary.Outcome != HotCombatOutcome.Ongoing ? HotNodeFlags.IsTerminal : HotNodeFlags.None);
                if (staged.Summary.DeathSaveUseCount > 0) standPatFlags |= HotNodeFlags.HasDeathSave;
                if (staged.Summary.ForfeitsGold) standPatFlags |= HotNodeFlags.ForfeitsGold;
                if (staged.Summary.AllGrowthTargetsSatisfied) standPatFlags |= HotNodeFlags.AllGrowthTargetsSatisfied;
                if (staged.Summary.AllRelicTargetsSatisfied) standPatFlags |= HotNodeFlags.AllRelicTargetsSatisfied;

                HotSearchNode childNode = new()
                {
                    Hash = staged.Hash,
                    Score = staged.Eval.Score,
                    RolloutScore = 0,
                    Cold = staged.Cold,
                    StateHandle = committed,
                    ParentId = parentId,
                    ChoiceOffset = -1,
                    HpLost = staged.Summary.HpLost,
                    HpRecovered = staged.Summary.HpRecovered,
                    PlayerHp = staged.Summary.PlayerHp,
                    EnemyHp = staged.Summary.EnemyHpSum,
                    SetupScore = staged.Eval.SetupScore,
                    BoardInvestment = staged.Eval.BoardInvestment,
                    MaxDarkEvoke = staged.Eval.MaxDarkEvoke,
                    TotalPoisonAndDoom = staged.Eval.TotalPoisonAndDoom,
                    PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - staged.Summary.PlayerMaxHp, short.MinValue, short.MaxValue),
                    PlayerBlock = (short)Math.Clamp(staged.Summary.PlayerBlock, 0, short.MaxValue),
                    EffectiveDefense = staged.Eval.EffectiveDefense,
                    OptionalityScore = staged.Eval.OptionalityScore,
                    OutstandingStolen = (short)Math.Clamp(staged.Summary.OutstandingStolen, 0, short.MaxValue),
                    GrowthHpCredit = (short)Math.Clamp(staged.Summary.GrowthHpCredit, short.MinValue, short.MaxValue),
                    RelicHpCredit = staged.Summary.RelicHpCredit,
                    RelicHealingHpCredit = staged.Summary.RelicHealingHpCredit,
                    RelicSatisfiedMask = staged.Summary.RelicSatisfiedMask,
                    RelicDistance = staged.Summary.RelicDistance,
                    RelicSatisfiedPriority = staged.Summary.RelicSatisfiedPriority,
                    FeedCount = staged.Summary.FeedCount,
                    PotionStrategicCost = parent.PotionStrategicCost,
                    DeathSaveHpRestored = (short)Math.Clamp(staged.Summary.DeathSaveHpRestored, 0, short.MaxValue),
                    PotionMask = parent.PotionMask,
                    ActionCount = (ushort)Math.Min(ushort.MaxValue, parent.ActionCount + 1),
                    GrowthRewardCount = (ushort)Math.Clamp(staged.Summary.GrowthRewardCount, 0, ushort.MaxValue),
                    Flags = standPatFlags,
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
                    PlayableHandCount = staged.Eval.PlayableHandCount,
                    DeathSaveUseCount = staged.Summary.DeathSaveUseCount,
                    Outcome = staged.Summary.Outcome,
                };

                int childId = nodeArena.AddNode(in childNode);
                if (childNode.IsVictory)
                {
                    victoriesOut.Add(childId);
                }
                else if (childNode.IsDefeat || childNode.IsEventDefeat)
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

        if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
            && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
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
            out HotEvalFeatures eval);

        staged = new HotStagedChild
        {
            StagedHandle = handle,
            Cold = cold,
            Hash = hash,
            Summary = summary,
            Eval = eval,
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

        // Distinct-card-first capping: when unique PlayCard actions exceed MaxCardBranchesPerNode,
        // allocate budget round-robin by per-card target rank so rightmost newly-drawn cards are NEVER dropped.
        Span<byte> keepPlayCardAction = stackalloc byte[256];
        bool filterPlayCards = false;
        if (canPlayMoreThisTurn && config.MaxCardBranchesPerNode > 0 && actionCount > config.MaxCardBranchesPerNode)
        {
            filterPlayCards = BuildDistinctCardFirstMask(
                ws.ActionBuf.AsSpan(0, actionCount),
                duplicateHandMask,
                config.MaxCardBranchesPerNode,
                keepPlayCardAction);
        }

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
                if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue && handIdx < ws.ParentState.HandCount)
                {
                    ref readonly HotCard playCard = ref ws.ParentState.Cards[handIdx];
                    if (playCard.CardDefId < rules.Cards.Length
                        && rules.Cards[playCard.CardDefId].CardId == HotCustomCardId.BrightestFlame)
                    {
                        int flameCost = HotCombatEngine.GetNamedVarInt(in playCard, rules.Cards[playCard.CardDefId], "MaxHp", 2);
                        if (!policy.AllowsBrightestFlame(ws.ParentState.BrightestFlameMaxHpSpent + flameCost))
                            continue;
                    }
                }
                if (filterPlayCards)
                {
                    if ((uint)a >= (uint)keepPlayCardAction.Length || keepPlayCardAction[a] == 0)
                        continue;
                }
                else if (config.MaxCardBranchesPerNode > 0 && cardBranchesEmitted >= config.MaxCardBranchesPerNode)
                {
                    continue;
                }

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
    /// Selects up to <paramref name="maxCardBranches"/> non-duplicate <see cref="HotActionKind.PlayCard"/>
    /// indices in round-robin order by per-hand-card target rank (emitting at least 1 action per distinct
    /// playable card before multi-enemy targeting fills the cap). Zero heap allocation, 100% deterministic.
    /// Returns <c>false</c> when total non-duplicate PlayCard actions do not exceed the cap.
    /// </summary>
    private static bool BuildDistinctCardFirstMask(
        ReadOnlySpan<HotAction> actions,
        ulong duplicateHandMask,
        int maxCardBranches,
        Span<byte> keepMask)
    {
        int count = Math.Min(actions.Length, keepMask.Length);
        Span<byte> nextRankForHand = stackalloc byte[64];
        Span<byte> actionRank = stackalloc byte[256];
        nextRankForHand.Clear();

        int nonDupPlayCardCount = 0;
        int maxRank = 0;

        for (int a = 0; a < count; a++)
        {
            HotAction act = actions[a];
            if (act.Kind != HotActionKind.PlayCard)
            {
                actionRank[a] = byte.MaxValue;
                continue;
            }

            byte handIdx = act.SlotOrHandIdx;
            if (handIdx < 64 && (duplicateHandMask & (1UL << handIdx)) != 0)
            {
                actionRank[a] = byte.MaxValue;
                continue;
            }

            nonDupPlayCardCount++;
            byte rank = handIdx < 64 ? nextRankForHand[handIdx]++ : (byte)0;
            actionRank[a] = rank;
            if (rank > maxRank)
                maxRank = rank;
        }

        if (nonDupPlayCardCount <= maxCardBranches)
            return false;

        keepMask[..count].Clear();
        int remaining = maxCardBranches;

        for (int targetRank = 0; targetRank <= maxRank && remaining > 0; targetRank++)
        {
            for (int a = 0; a < count && remaining > 0; a++)
            {
                if (actionRank[a] == targetRank)
                {
                    keepMask[a] = 1;
                    remaining--;
                }
            }
        }

        return true;
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
        int rawFirstCount = HotCombatEngine.EnumerateChoiceOptions(
            in ws.WorkingState,
            firstPausedCold,
            rules,
            ws.Scratch,
            ws.ChoiceOptBuf);
        if (rawFirstCount <= 0)
            return;

        HotChoiceKind firstKind = ws.Scratch.PendingChoiceKind;
        ushort firstGroupSeq = ws.Scratch.PendingChoiceGroupSeq;
        HotChoiceGroupFlags firstGroupFlags = ws.Scratch.PendingChoiceGroupFlags;
        int maxBranches = ResolveChoiceBranchCap(firstKind, action.Kind, in config);

        bool hasNone = false;
        int optCount = 0;
        for (int i = 0; i < rawFirstCount; i++)
        {
            HotChoiceSelection opt = ws.ChoiceOptBuf[i];
            if (opt.Kind == HotChoiceKind.None)
            {
                hasNone = true;
                continue;
            }
            ws.ChoiceOptBuf[optCount++] = opt;
        }

        if (optCount == 0 && !hasNone)
            return;

        // Clear per-action local TT for completed post-choice states.
        ws.LocalChoiceTtUsed.AsSpan().Clear();
        int resolvedCount = 0;

        int nextExplicit = parent.ExplicitPotionCount + explicitPotionDelta;
        int nextOptionalPaid = parent.OptionalPaidPotionCount + optionalPaidCountDelta;
        short nextPotionCost = (short)Math.Clamp(parent.PotionStrategicCost + potionCostDelta, 0, short.MaxValue);

        // Domain-agnostic 2-round coordinate-ascent marginal pre-screening when a multi-pick / optional-subset
        // prompt has combinatorial fanout exceeding the exact exhaustive layer cap (zero card-ID hardcoding).
        Span<ushort> screenedUids = stackalloc ushort[16];
        Span<ushort> screenedDefIds = stackalloc ushort[16];
        int screenedCount = 0;
        bool hasScreenedFilter = false;

        bool isMultiPick = (firstGroupFlags & HotChoiceGroupFlags.MultiPick) != 0;
        bool isOptional = (firstGroupFlags & HotChoiceGroupFlags.Optional) != 0;
        if (isMultiPick && ((isOptional && optCount > 6) || (!isOptional && optCount > 10)))
        {
            hasScreenedFilter = ScreenHighBranchingMultiPick(
                ws,
                in parent,
                in replayBaseState,
                replayBaseCold,
                rules,
                in policy,
                in evalLookup,
                in config,
                in frozenIncumbent,
                action,
                isEndTurnStartPrefix,
                firstGroupSeq,
                firstGroupFlags,
                nextExplicit,
                nextOptionalPaid,
                nextPotionCost,
                ref optCount,
                screenedUids,
                screenedDefIds,
                out screenedCount,
                ref resolvedCount);
        }

        // Canonical UID sort at depth 0 for unordered groups ensures clean deterministic expansion.
        if ((firstGroupFlags & HotChoiceGroupFlags.Unordered) != 0 && optCount > 1)
            SortChoiceOptionsByUid(ws.ChoiceOptBuf.AsSpan(0, optCount));

        HotChoicePrefix[] currLayer = ws.ChoiceLayerA;
        HotChoicePrefix[] nextLayer = ws.ChoiceLayerB;
        int currCount = 0;

        if (hasNone && currCount < HotExpansionWorkspace.ChoiceLayerCapacity)
        {
            HotChoicePrefix nonePfx = default;
            nonePfx.Selections[0] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            nonePfx.Count = 1;
            nonePfx.LastGroupSeq = firstGroupSeq;
            nonePfx.LastGroupFlags = firstGroupFlags;
            currLayer[currCount++] = nonePfx;
        }

        int depth0Limit = Math.Min(optCount, HotExpansionWorkspace.ChoiceLayerCapacity - currCount);
        for (int i = 0; i < depth0Limit; i++)
        {
            HotChoiceSelection opt = ws.ChoiceOptBuf[i];
            HotChoicePrefix pfx = default;
            pfx.Selections[0] = opt;
            pfx.Count = 1;
            pfx.LastGroupSeq = firstGroupSeq;
            pfx.LastGroupFlags = firstGroupFlags;
            pfx.LastChoiceUid = opt.CardUidOrIndex;
            currLayer[currCount++] = pfx;
        }

        // Layer-by-layer BFS over choice sequences (eliminates DFS c_0 fixation and supports
        // exact 1-to-1 multiset bijection via strictly increasing min-class UIDs).
        while (currCount > 0 && resolvedCount < HotExpansionWorkspace.ChoiceResolvedCapacity)
        {
            int nextCount = 0;

            for (int idx = 0; idx < currCount && resolvedCount < HotExpansionWorkspace.ChoiceResolvedCapacity; idx++)
            {
                HotChoicePrefix pfx = currLayer[idx];

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
                    TryRecordResolvedChoice(
                        ws,
                        in parent,
                        branchCold,
                        rules,
                        in policy,
                        in evalLookup,
                        in config,
                        in frozenIncumbent,
                        in pfx,
                        nextExplicit,
                        nextOptionalPaid,
                        nextPotionCost,
                        ref resolvedCount);
                }
                else if (pfx.Count < HotLimits.MaxChoicesPerAction)
                {
                    if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
                        && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
                    {
                        continue;
                    }

                    int subOptCount;
                    try
                    {
                        subOptCount = HotCombatEngine.EnumerateChoiceOptions(
                            in ws.WorkingState,
                            branchCold,
                            rules,
                            ws.Scratch,
                            ws.ChoiceSubOptBuf);
                    }
                    catch (HotCapacityExceededException)
                    {
                        ws.WaveCapacityExceededDrops++;
                        continue;
                    }

                    if (subOptCount <= 0)
                        continue;

                    ushort subGroupSeq = ws.Scratch.PendingChoiceGroupSeq;
                    HotChoiceGroupFlags subGroupFlags = ws.Scratch.PendingChoiceGroupFlags;
                    bool sameGroup = subGroupSeq != 0 && subGroupSeq == pfx.LastGroupSeq;
                    bool enforceCanonicalUid = sameGroup
                        && (subGroupFlags & HotChoiceGroupFlags.Unordered) != 0
                        && IsPileChoiceKind(ws.Scratch.PendingChoiceKind);
                    bool applyScreenedFilter = hasScreenedFilter && subGroupSeq == firstGroupSeq;

                    for (int sIdx = 0; sIdx < subOptCount && nextCount < HotExpansionWorkspace.ChoiceLayerCapacity; sIdx++)
                    {
                        HotChoiceSelection opt = ws.ChoiceSubOptBuf[sIdx];
                        if (opt.Kind == HotChoiceKind.None)
                        {
                            HotChoicePrefix stopPfx = pfx;
                            stopPfx.Selections[stopPfx.Count] = opt;
                            stopPfx.Count++;
                            stopPfx.LastGroupSeq = subGroupSeq;
                            stopPfx.LastGroupFlags = subGroupFlags;
                            nextLayer[nextCount++] = stopPfx;
                            continue;
                        }

                        if (enforceCanonicalUid && opt.CardUidOrIndex <= pfx.LastChoiceUid)
                            continue;

                        if (applyScreenedFilter
                            && !ContainsUShort(screenedUids[..screenedCount], opt.CardUidOrIndex)
                            && !ContainsUShort(screenedDefIds[..screenedCount], opt.CardDefId))
                        {
                            continue;
                        }

                        HotChoicePrefix nextPfx = pfx;
                        nextPfx.Selections[nextPfx.Count] = opt;
                        nextPfx.Count++;
                        nextPfx.LastGroupSeq = subGroupSeq;
                        nextPfx.LastGroupFlags = subGroupFlags;
                        nextPfx.LastChoiceUid = opt.CardUidOrIndex;
                        nextLayer[nextCount++] = nextPfx;
                    }
                }
                else
                {
                    ws.WaveCapacityExceededDrops++;
                }
            }

            (currLayer, nextLayer) = (nextLayer, currLayer);
            currCount = nextCount;
        }

        if (resolvedCount <= 0)
            return;

        try
        {
            CommitResolvedChoices(
                ws,
                in parent,
                rules,
                in policy,
                in evalLookup,
                in config,
                action,
                firstKind,
                isEndTurnStartPrefix,
                resolvedCount,
                maxBranches,
                potionCostDelta,
                potionSlotBit,
                optionalPaidCountDelta,
                explicitPotionDelta,
                ambergrisDelta);
        }
        finally
        {
            for (int i = 0; i < resolvedCount; i++)
                ws.ChoiceResolved[i].Cold = null;
        }
    }

    private static bool ScreenHighBranchingMultiPick(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        in CombatHotState replayBaseState,
        HotColdSidecar? replayBaseCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        HotAction action,
        bool isEndTurnStartPrefix,
        ushort firstGroupSeq,
        HotChoiceGroupFlags firstGroupFlags,
        int nextExplicit,
        int nextOptionalPaid,
        short nextPotionCost,
        ref int optCount,
        Span<ushort> screenedUids,
        Span<ushort> screenedDefIds,
        out int screenedCount,
        ref int resolvedCount)
    {
        screenedCount = 0;
        int n = optCount;
        bool isOptional = (firstGroupFlags & HotChoiceGroupFlags.Optional) != 0;

        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            nextPotionCost,
            nextOptionalPaid,
            parent.AlchemizeCount,
            parent.EmptyPotionSlots,
            in policy);

        if (isOptional)
        {
            // Optional multi-pick (0..N, e.g. GamblingChip / Purity+ / Guards / Ashwater on 7..10 distinct cards):
            // 1) Measure O(N) single-item marginal value of [opt[i], None] via 1-turn lookahead rollout.
            for (int i = 0; i < n; i++)
            {
                ws.MarginalOrderBuf[i] = i;
                ws.MarginalScoreBuf[i] = ProbeOptionalSingleItem(
                    ws,
                    in replayBaseState,
                    replayBaseCold,
                    rules,
                    in policy,
                    in evalLookup,
                    in config,
                    action,
                    isEndTurnStartPrefix,
                    ws.ChoiceOptBuf[i],
                    netPotionCostCenti,
                    parent.ActionCount + 1);
            }

            SortMarginalIndicesDescending(ws.MarginalOrderBuf.AsSpan(0, n), ws.MarginalScoreBuf, ws.ChoiceOptBuf);

            // 2) Directly evaluate greedy cumulative subsets of size 7..min(N, MaxChoicesPerAction-1)
            //    so keeping 1..3 cards and mulliganing 7..9 bricks on a 10-card hand is always explored!
            int maxGreedy = Math.Min(n, HotLimits.MaxChoicesPerAction - 1);
            for (int subsetSize = 7; subsetSize <= maxGreedy && resolvedCount < HotExpansionWorkspace.ChoiceResolvedCapacity; subsetSize++)
            {
                HotChoicePrefix greedyPfx = default;
                for (int j = 0; j < subsetSize; j++)
                    greedyPfx.Selections[j] = ws.ChoiceOptBuf[ws.MarginalOrderBuf[j]];
                greedyPfx.Count = (byte)subsetSize;
                if ((firstGroupFlags & HotChoiceGroupFlags.Unordered) != 0)
                    SortPrefixSelectionsByUid(ref greedyPfx);

                ws.WorkingState = replayBaseState;
                HotColdSidecar? branchCold = replayBaseCold;
                if (isEndTurnStartPrefix)
                    ws.WaveEndTurnPrefixReuses++;
                ws.WaveChoiceBranches++;

                HotStepResult res;
                try
                {
                    res = isEndTurnStartPrefix
                        ? HotCombatEngine.StepStartPlayerTurn(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, greedyPfx.AsSpan())
                        : HotCombatEngine.Step(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, action, greedyPfx.AsSpan());

                    if (res.Executed && res.NeedsChoice && ws.Scratch.PendingChoiceMin == 0 && greedyPfx.Count < HotLimits.MaxChoicesPerAction)
                    {
                        greedyPfx.Selections[greedyPfx.Count++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                        ws.WorkingState = replayBaseState;
                        branchCold = replayBaseCold;
                        ws.WaveChoiceBranches++;
                        res = isEndTurnStartPrefix
                            ? HotCombatEngine.StepStartPlayerTurn(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, greedyPfx.AsSpan())
                            : HotCombatEngine.Step(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, action, greedyPfx.AsSpan());
                    }
                }
                catch (HotCapacityExceededException)
                {
                    ws.WaveCapacityExceededDrops++;
                    continue;
                }

                if (res.Executed && !res.NeedsChoice)
                {
                    ws.WaveTransitions++;
                    TryRecordResolvedChoice(
                        ws,
                        in parent,
                        branchCold,
                        rules,
                        in policy,
                        in evalLookup,
                        in config,
                        in frozenIncumbent,
                        in greedyPfx,
                        nextExplicit,
                        nextOptionalPaid,
                        nextPotionCost,
                        ref resolvedCount);
                }
            }

            // 3) Exhaustively explore all 2^6 = 64 subsets over the Top-6 marginal items.
            int keep = Math.Min(6, Math.Min(n, screenedUids.Length));
            CompactScreenedOptions(ws, keep, screenedUids, screenedDefIds, out screenedCount, out optCount);
            return true;
        }

        // Fixed-k multi-pick (N > 10, e.g. Charge!! k=2, Dredge k=3, Seance k=2):
        // Probe option 0 first to observe exact k for this group.
        Span<ushort> tailAnchors = stackalloc ushort[4];
        int tailAnchorCount = 0;
        for (int a = n - 1; a >= 0 && tailAnchorCount < 4; a--)
            tailAnchors[tailAnchorCount++] = ws.ChoiceOptBuf[a].CardUidOrIndex;

        // Combined mid+head anchor list eliminates toxic-edge bias for k=2..4 while keeping Round 1 to 2 anchors.
        Span<ushort> headMidAnchors = stackalloc ushort[4];
        int headMidAnchorCount = 0;
        if (n > 2) headMidAnchors[headMidAnchorCount++] = ws.ChoiceOptBuf[n / 2].CardUidOrIndex;
        headMidAnchors[headMidAnchorCount++] = ws.ChoiceOptBuf[0].CardUidOrIndex;
        if (n > 3) headMidAnchors[headMidAnchorCount++] = ws.ChoiceOptBuf[(n / 2) + 1].CardUidOrIndex;
        if (n > 1) headMidAnchors[headMidAnchorCount++] = ws.ChoiceOptBuf[1].CardUidOrIndex;

        long probe0Tail = ProbeFixedKItem(
            ws,
            in replayBaseState,
            replayBaseCold,
            rules,
            in policy,
            in evalLookup,
            in config,
            action,
            isEndTurnStartPrefix,
            firstGroupSeq,
            ws.ChoiceOptBuf[0],
            tailAnchors[..tailAnchorCount],
            netPotionCostCenti,
            parent.ActionCount + 1,
            out int observedK);

        if (observedK <= 1)
            return false;

        // Round 1: Evaluate every option i across 2 complementary anchors (tail + head/mid).
        ws.MarginalOrderBuf[0] = 0;
        long probe0HeadMid = ProbeFixedKItem(
            ws,
            in replayBaseState,
            replayBaseCold,
            rules,
            in policy,
            in evalLookup,
            in config,
            action,
            isEndTurnStartPrefix,
            firstGroupSeq,
            ws.ChoiceOptBuf[0],
            headMidAnchors[..headMidAnchorCount],
            netPotionCostCenti,
            parent.ActionCount + 1,
            out _);
        ws.MarginalScoreBuf[0] = Math.Max(probe0Tail, probe0HeadMid);

        for (int i = 1; i < n; i++)
        {
            ws.MarginalOrderBuf[i] = i;
            long sTail = ProbeFixedKItem(
                ws,
                in replayBaseState,
                replayBaseCold,
                rules,
                in policy,
                in evalLookup,
                in config,
                action,
                isEndTurnStartPrefix,
                firstGroupSeq,
                ws.ChoiceOptBuf[i],
                tailAnchors[..tailAnchorCount],
                netPotionCostCenti,
                parent.ActionCount + 1,
                out int kSeen);
            if (kSeen > observedK)
                observedK = kSeen;

            long sHeadMid = ProbeFixedKItem(
                ws,
                in replayBaseState,
                replayBaseCold,
                rules,
                in policy,
                in evalLookup,
                in config,
                action,
                isEndTurnStartPrefix,
                firstGroupSeq,
                ws.ChoiceOptBuf[i],
                headMidAnchors[..headMidAnchorCount],
                netPotionCostCenti,
                parent.ActionCount + 1,
                out _);
            ws.MarginalScoreBuf[i] = Math.Max(sTail, sHeadMid);
        }

        // Round 2 (Coordinate Ascent): Pair every option i directly alongside the Round-1 MVP winners
        // so 2-card and 3-card combos (e.g. Bludgeon+ + Slice + Anger on Dredge) surface immediately!
        SortMarginalIndicesDescending(ws.MarginalOrderBuf.AsSpan(0, n), ws.MarginalScoreBuf, ws.ChoiceOptBuf);
        Span<ushort> mvpAnchors = stackalloc ushort[4];
        int mvpAnchorCount = 0;
        for (int a = 0; a < Math.Min(3, n); a++)
            mvpAnchors[mvpAnchorCount++] = ws.ChoiceOptBuf[ws.MarginalOrderBuf[a]].CardUidOrIndex;
        mvpAnchors[mvpAnchorCount++] = ws.ChoiceOptBuf[n - 1].CardUidOrIndex;

        for (int i = 0; i < n; i++)
        {
            ws.MarginalOrderBuf[i] = i;
            long sMvp = ProbeFixedKItem(
                ws,
                in replayBaseState,
                replayBaseCold,
                rules,
                in policy,
                in evalLookup,
                in config,
                action,
                isEndTurnStartPrefix,
                firstGroupSeq,
                ws.ChoiceOptBuf[i],
                mvpAnchors[..mvpAnchorCount],
                netPotionCostCenti,
                parent.ActionCount + 1,
                out _);
            if (sMvp > ws.MarginalScoreBuf[i])
                ws.MarginalScoreBuf[i] = sMvp;
        }

        SortMarginalIndicesDescending(ws.MarginalOrderBuf.AsSpan(0, n), ws.MarginalScoreBuf, ws.ChoiceOptBuf);
        int targetS = observedK <= 2 ? 10 : observedK == 3 ? 9 : 7;
        int keepFixed = Math.Min(targetS, Math.Min(n, screenedUids.Length));
        CompactScreenedOptions(ws, keepFixed, screenedUids, screenedDefIds, out screenedCount, out optCount);
        return true;
    }

    private static void CompactScreenedOptions(
        HotExpansionWorkspace ws,
        int keep,
        Span<ushort> screenedUids,
        Span<ushort> screenedDefIds,
        out int screenedCount,
        out int optCount)
    {
        Span<HotChoiceSelection> kept = stackalloc HotChoiceSelection[16];
        for (int i = 0; i < keep; i++)
        {
            HotChoiceSelection sel = ws.ChoiceOptBuf[ws.MarginalOrderBuf[i]];
            kept[i] = sel;
            screenedUids[i] = sel.CardUidOrIndex;
            screenedDefIds[i] = sel.CardDefId;
        }
        for (int i = 0; i < keep; i++)
            ws.ChoiceOptBuf[i] = kept[i];
        screenedCount = keep;
        optCount = keep;
    }

    private static long ProbeOptionalSingleItem(
        HotExpansionWorkspace ws,
        in CombatHotState replayBaseState,
        HotColdSidecar? replayBaseCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        HotAction action,
        bool isEndTurnStartPrefix,
        HotChoiceSelection item,
        int netPotionCostCenti,
        int childActionCount)
    {
        HotChoicePrefix pfx = default;
        pfx.Selections[0] = item;
        pfx.Count = 1;

        for (int step = 0; step < 4; step++)
        {
            ws.WorkingState = replayBaseState;
            HotColdSidecar? branchCold = replayBaseCold;
            HotStepResult res;
            try
            {
                res = isEndTurnStartPrefix
                    ? HotCombatEngine.StepStartPlayerTurn(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, pfx.AsSpan())
                    : HotCombatEngine.Step(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, action, pfx.AsSpan());
            }
            catch (HotCapacityExceededException)
            {
                return long.MinValue / 4;
            }

            if (!res.Executed)
                return long.MinValue / 4;

            if (!res.NeedsChoice)
            {
                if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
                    && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
                {
                    return long.MinValue / 4;
                }

                AssignDeterministicCardUids(ref ws.WorkingState);
                HotMiniRolloutResult probe = HotRolloutEvaluator.EvaluateChoiceProbe(
                    in ws.WorkingState,
                    branchCold,
                    ref ws.ChoiceProbeState,
                    ws.ChoiceProbeScratch,
                    ws.ChoiceProbeActionBuf,
                    ws.ChoiceProbeChoiceBuf,
                    rules,
                    in policy,
                    in evalLookup,
                    config.EvalMode,
                    maxTurnsAhead: 1,
                    parentNetPotionCostCenti: netPotionCostCenti,
                    parentActionCount: childActionCount);
                return EncodeProbeRankScore(in probe, in policy);
            }

            if (pfx.Count >= HotLimits.MaxChoicesPerAction)
                return long.MinValue / 4;

            if (ws.Scratch.PendingChoiceMin == 0)
            {
                pfx.Selections[pfx.Count++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            }
            else
            {
                int subCount = HotCombatEngine.EnumerateChoiceOptions(
                    in ws.WorkingState, branchCold, rules, ws.Scratch, ws.ChoiceSubOptBuf);
                if (subCount <= 0)
                    return long.MinValue / 4;
                pfx.Selections[pfx.Count++] = ws.ChoiceSubOptBuf[subCount - 1];
            }
        }

        return long.MinValue / 4;
    }

    private static long ProbeFixedKItem(
        HotExpansionWorkspace ws,
        in CombatHotState replayBaseState,
        HotColdSidecar? replayBaseCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        HotAction action,
        bool isEndTurnStartPrefix,
        ushort firstGroupSeq,
        HotChoiceSelection item,
        ReadOnlySpan<ushort> anchorUids,
        int netPotionCostCenti,
        int childActionCount,
        out int observedGroupPicks)
    {
        observedGroupPicks = 1;
        HotChoicePrefix pfx = default;
        pfx.Selections[0] = item;
        pfx.Count = 1;

        for (int step = 0; step < HotLimits.MaxChoicesPerAction; step++)
        {
            ws.WorkingState = replayBaseState;
            HotColdSidecar? branchCold = replayBaseCold;
            HotStepResult res;
            try
            {
                res = isEndTurnStartPrefix
                    ? HotCombatEngine.StepStartPlayerTurn(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, pfx.AsSpan())
                    : HotCombatEngine.Step(ref ws.WorkingState, ref branchCold, rules, ws.Scratch, action, pfx.AsSpan());
            }
            catch (HotCapacityExceededException)
            {
                return long.MinValue / 4;
            }

            if (!res.Executed)
                return long.MinValue / 4;

            if (!res.NeedsChoice)
            {
                if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
                    && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
                {
                    return long.MinValue / 4;
                }

                AssignDeterministicCardUids(ref ws.WorkingState);
                HotMiniRolloutResult probe = HotRolloutEvaluator.EvaluateChoiceProbe(
                    in ws.WorkingState,
                    branchCold,
                    ref ws.ChoiceProbeState,
                    ws.ChoiceProbeScratch,
                    ws.ChoiceProbeActionBuf,
                    ws.ChoiceProbeChoiceBuf,
                    rules,
                    in policy,
                    in evalLookup,
                    config.EvalMode,
                    maxTurnsAhead: 0,
                    parentNetPotionCostCenti: netPotionCostCenti,
                    parentActionCount: childActionCount);
                return EncodeProbeRankScore(in probe, in policy);
            }

            if (pfx.Count >= HotLimits.MaxChoicesPerAction)
                return long.MinValue / 4;

            if (ws.Scratch.PendingChoiceGroupSeq == firstGroupSeq)
                observedGroupPicks++;

            int subCount = HotCombatEngine.EnumerateChoiceOptions(
                in ws.WorkingState, branchCold, rules, ws.Scratch, ws.ChoiceSubOptBuf);
            if (subCount <= 0)
                return long.MinValue / 4;

            HotChoiceSelection nextSel = default;
            bool picked = false;
            if (ws.Scratch.PendingChoiceGroupSeq == firstGroupSeq)
            {
                for (int a = 0; a < anchorUids.Length && !picked; a++)
                {
                    ushort targetUid = anchorUids[a];
                    for (int j = 0; j < subCount; j++)
                    {
                        HotChoiceSelection cand = ws.ChoiceSubOptBuf[j];
                        if (cand.Kind != HotChoiceKind.None && cand.CardUidOrIndex == targetUid)
                        {
                            nextSel = cand;
                            picked = true;
                            break;
                        }
                    }
                }
            }

            if (!picked)
            {
                // Fallback deterministically to last non-None option (or None if only None exists).
                nextSel = ws.ChoiceSubOptBuf[subCount - 1];
                for (int j = subCount - 1; j >= 0; j--)
                {
                    if (ws.ChoiceSubOptBuf[j].Kind != HotChoiceKind.None)
                    {
                        nextSel = ws.ChoiceSubOptBuf[j];
                        break;
                    }
                }
            }

            pfx.Selections[pfx.Count++] = nextSel;
        }

        return long.MinValue / 4;
    }

    private static void TryRecordResolvedChoice(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotColdSidecar? branchCold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        in HotIncumbentBound frozenIncumbent,
        in HotChoicePrefix pfx,
        int nextExplicit,
        int nextOptionalPaid,
        short nextPotionCost,
        ref int resolvedCount)
    {
        if (resolvedCount >= HotExpansionWorkspace.ChoiceResolvedCapacity)
            return;

        if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
            && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
        {
            return;
        }

        HotFastSummary summary = HotEvaluator.ExtractStage1(in ws.WorkingState, in policy);
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
        int written = HotStatePacker.Pack(ref ws.WorkingState, ref branchCold, buf, canonicalize: true);
        HotStateHandle stagedHandle = ws.StagingArena.CommitReserved(written);
        HotHash128 hash = HotHash.ComputeHash(ws.StagingArena.GetSpan(stagedHandle), branchCold);

        // Local per-action TT deduplicates equivalent completed post-choice states (e.g. {A,B} == {B,A}).
        if (!TryInsertLocalChoiceTt(ws, hash))
            return;

        HotEvaluator.EvaluateState(
            in ws.WorkingState,
            branchCold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            netPotionCostCenti,
            parent.ActionCount + 1,
            config.EvalMode,
            out HotEvalFeatures eval);

        ws.ChoiceResolved[resolvedCount++] = new HotResolvedChoice
        {
            Prefix = pfx,
            StagedHandle = stagedHandle,
            Cold = branchCold,
            Hash = hash,
            Summary = summary,
            Eval = eval,
            LookaheadScore = 0,
            LookaheadHpLost = summary.HpLost,
            LookaheadBoardInvestment = eval.BoardInvestment,
            LookaheadSetupScore = eval.SetupScore,
            HasLookahead = false,
            LookaheadVictory = summary.IsVictory,
        };
    }

    private static void CommitResolvedChoices(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        HotAction action,
        HotChoiceKind firstKind,
        bool isEndTurnStartPrefix,
        int resolvedCount,
        int maxBranches,
        short potionCostDelta,
        ushort potionSlotBit,
        byte optionalPaidCountDelta,
        byte explicitPotionDelta,
        byte ambergrisDelta)
    {
        int maxPrefixLen = 0;
        for (int i = 0; i < resolvedCount; i++)
        {
            int len = ws.ChoiceResolved[i].Prefix.Count;
            if (len > maxPrefixLen)
                maxPrefixLen = len;
        }

        bool isStartTurn = parent.Turn == policy.StartTurnNumber;
        int effectiveMaxBranches = (isStartTurn && maxPrefixLen >= 2)
            ? Math.Max(maxBranches, 36)
            : maxBranches;
        int cap = Math.Min(resolvedCount, effectiveMaxBranches);
        bool needsPruning = resolvedCount > cap;

        bool isDelayedOrTurnSetup = isEndTurnStartPrefix
            || action.Kind == HotActionKind.TurnSetup
            || firstKind is HotChoiceKind.DrawPile or HotChoiceKind.DiscardPile or HotChoiceKind.ExhaustPile;

        int nextOptionalPaid = parent.OptionalPaidPotionCount + optionalPaidCountDelta;
        short nextPotionCost = (short)Math.Clamp(parent.PotionStrategicCost + potionCostDelta, 0, short.MaxValue);

        Span<int> selectedIndices = stackalloc int[HotExpansionWorkspace.ChoiceResolvedCapacity];
        int selectedCount;

        if (!needsPruning)
        {
            bool turnBoundaryRolloutsEnabled = config.EnableTurnBoundaryRollouts;
            bool runUnprunedProbe = resolvedCount >= 2
                && (action.Kind == HotActionKind.TurnSetup
                    || (isStartTurn && isDelayedOrTurnSetup && !isEndTurnStartPrefix && resolvedCount <= 12)
                    || (isEndTurnStartPrefix && !turnBoundaryRolloutsEnabled));

            if (runUnprunedProbe)
            {
                for (int i = 0; i < resolvedCount; i++)
                {
                    EvaluateResolvedChoiceLookahead(
                        ws,
                        in parent,
                        rules,
                        in policy,
                        in evalLookup,
                        in config,
                        nextPotionCost,
                        nextOptionalPaid,
                        maxTurnsAhead: 1,
                        ref ws.ChoiceResolved[i]);
                }
            }

            selectedCount = resolvedCount;
            for (int i = 0; i < resolvedCount; i++)
                selectedIndices[i] = i;
            SortResolvedIndicesByPrimary(selectedIndices[..selectedCount], ws.ChoiceResolved);
        }
        else
        {
            int probeBudget = Math.Min(resolvedCount, Math.Max(16, effectiveMaxBranches * 2));
            Span<int> probeIndices = stackalloc int[HotExpansionWorkspace.ChoiceResolvedCapacity];
            int probeCount;

            if (probeBudget >= resolvedCount)
            {
                probeCount = resolvedCount;
                for (int i = 0; i < resolvedCount; i++)
                    probeIndices[i] = i;
            }
            else
            {
                Span<byte> preFilterMarks = ws.ResolvedSelectedMarks.AsSpan(0, resolvedCount);
                PreFilterStaticProbeCandidates(
                    ws.ChoiceResolved,
                    resolvedCount,
                    probeBudget,
                    preFilterMarks,
                    probeIndices,
                    out probeCount);
            }

            int probeMaxTurnsAhead = firstKind == HotChoiceKind.HandCard ? 0 : 1;
            for (int p = 0; p < probeCount; p++)
            {
                EvaluateResolvedChoiceLookahead(
                    ws,
                    in parent,
                    rules,
                    in policy,
                    in evalLookup,
                    in config,
                    nextPotionCost,
                    nextOptionalPaid,
                    probeMaxTurnsAhead,
                    ref ws.ChoiceResolved[probeIndices[p]]);
            }

            // Domain-agnostic Depth-0 diversity + MAP-Elites choice shortlisting (zero card-ID hardcoding):
            // First guarantee 1 best Primary representative per distinct Depth-0 option (up to cap / 2),
            // then fill remaining slots via ~55% Lookahead/Primary, ~15% Immediate Eval, ~15% Survival, ~15% Investment.
            Span<byte> marks = ws.ResolvedSelectedMarks.AsSpan(0, resolvedCount);
            marks.Clear();
            selectedCount = 0;

            Span<int> probedPool = probeIndices[..probeCount];
            SortResolvedIndicesByPrimary(probedPool, ws.ChoiceResolved);

            int depth0Cap = cap / 2;
            if (depth0Cap > 0)
            {
                SelectDepth0RepresentativesFromSortedPool(
                    ws.ChoiceResolved,
                    probedPool,
                    marks,
                    depth0Cap,
                    selectedIndices,
                    ref selectedCount);
            }

            int remaining = cap - selectedCount;
            if (remaining > 0)
            {
                int primaryQuota = Math.Max(1, (remaining * 55 + 50) / 100);
                int immediateQuota = remaining >= 4 ? Math.Max(1, (remaining * 15) / 100) : 0;
                int survivalQuota = remaining >= 3 ? Math.Max(1, (remaining * 15) / 100) : 0;
                int investQuota = Math.Max(0, remaining - primaryQuota - immediateQuota - survivalQuota);

                SelectResolvedTopKFromPool(ws.ChoiceResolved, probedPool, marks, primaryQuota, 0, selectedIndices, ref selectedCount);
                SelectResolvedTopKFromPool(ws.ChoiceResolved, probedPool, marks, immediateQuota, 1, selectedIndices, ref selectedCount);
                SelectResolvedTopKFromPool(ws.ChoiceResolved, probedPool, marks, survivalQuota, 2, selectedIndices, ref selectedCount);
                SelectResolvedTopKFromPool(ws.ChoiceResolved, probedPool, marks, investQuota, 3, selectedIndices, ref selectedCount);

                if (selectedCount < cap)
                    SelectResolvedTopKFromPool(ws.ChoiceResolved, probedPool, marks, cap - selectedCount, 0, selectedIndices, ref selectedCount);
            }

            SortResolvedIndicesByPrimary(selectedIndices[..selectedCount], ws.ChoiceResolved);
        }

        int beforeFilled = CountFilledPotions(in ws.ParentState);

        for (int sIdx = 0; sIdx < selectedCount; sIdx++)
        {
            ref readonly HotResolvedChoice cand = ref ws.ChoiceResolved[selectedIndices[sIdx]];
            int afterFilled = Math.Max(0, Math.Min((int)ws.ParentState.PotionSlotCount, HotLimits.MaxPotions) - cand.Summary.EmptyPotionSlots);
            int totalConsumed = Math.Max(0, beforeFilled - afterFilled);
            byte autoPotionDelta = totalConsumed > explicitPotionDelta ? (byte)(totalConsumed - explicitPotionDelta) : (byte)0;
            byte totalPotionDelta = (byte)(explicitPotionDelta + autoPotionDelta);

            ReadOnlySpan<HotChoiceSelection> choiceSpan = cand.Prefix.AsSpan();
            int choiceOffset = choiceSpan.Length > 0 ? ws.AppendStagedChoices(choiceSpan) : -1;

            ws.AddStagedChild(new HotStagedChild
            {
                StagedHandle = cand.StagedHandle,
                Cold = cand.Cold,
                Hash = cand.Hash,
                Summary = cand.Summary,
                Eval = cand.Eval,
                RolloutScore = cand.HasLookahead ? cand.LookaheadScore : 0,
                ChoiceOffset = choiceOffset,
                Action = action,
                ChoiceCount = (byte)choiceSpan.Length,
                PotionCostDelta = potionCostDelta,
                PotionSlotBit = potionSlotBit,
                PotionCountDelta = totalPotionDelta,
                ExplicitPotionDelta = explicitPotionDelta,
                OptionalPaidCountDelta = optionalPaidCountDelta,
                AmbergrisDelta = ambergrisDelta,
                Flags = cand.HasLookahead ? HotNodeFlags.HasChoiceLookahead : HotNodeFlags.None,
            });
        }
    }

    private static void EvaluateResolvedChoiceLookahead(
        HotExpansionWorkspace ws,
        in HotSearchNode parent,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        in HotWaveExpansionConfig config,
        short nextPotionCost,
        int nextOptionalPaid,
        int maxTurnsAhead,
        ref HotResolvedChoice cand)
    {
        HotStatePacker.Unpack(ws.StagingArena.GetSpan(cand.StagedHandle), ref ws.WorkingState);
        AssignDeterministicCardUids(ref ws.WorkingState);

        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            nextPotionCost,
            nextOptionalPaid,
            cand.Summary.AlchemizeCount,
            cand.Summary.EmptyPotionSlots,
            in policy);

        HotMiniRolloutResult probe = HotRolloutEvaluator.EvaluateChoiceProbe(
            in ws.WorkingState,
            cand.Cold,
            ref ws.ChoiceProbeState,
            ws.ChoiceProbeScratch,
            ws.ChoiceProbeActionBuf,
            ws.ChoiceProbeChoiceBuf,
            rules,
            in policy,
            in evalLookup,
            config.EvalMode,
            maxTurnsAhead: maxTurnsAhead,
            parentNetPotionCostCenti: netPotionCostCenti,
            parentActionCount: parent.ActionCount + 1);

        cand.LookaheadScore = EncodeProbeRankScore(in probe, in policy);
        cand.LookaheadHpLost = probe.HpLost;
        cand.LookaheadBoardInvestment = probe.BoardInvestment;
        cand.LookaheadSetupScore = probe.SetupScore;
        cand.LookaheadVictory = probe.IsVictory;
        cand.HasLookahead = true;
    }

    private static void PreFilterStaticProbeCandidates(
        HotResolvedChoice[] resolved,
        int resolvedCount,
        int probeBudget,
        Span<byte> marks,
        Span<int> probeIndices,
        out int probeCount)
    {
        marks.Clear();
        probeCount = 0;

        Span<int> staticOrder = stackalloc int[HotExpansionWorkspace.ChoiceResolvedCapacity];
        Span<int> orderedSlice = staticOrder[..resolvedCount];
        for (int i = 0; i < resolvedCount; i++)
            orderedSlice[i] = i;
        SortResolvedIndicesByStatic(orderedSlice, resolved);

        // 1) Guarantee at least 1 best static representative per distinct Depth-0 option while reserving
        //    at least ~35% (min 6) of probeBudget for immediate Eval, Survival, and BoardInvestment+Setup.
        int minNicheReserve = Math.Max(6, (probeBudget * 35) / 100);
        int depth0ProbeCap = Math.Clamp(probeBudget - minNicheReserve, 0, probeBudget);
        SelectDepth0RepresentativesFromSortedPool(
            resolved,
            orderedSlice,
            marks,
            depth0ProbeCap,
            probeIndices,
            ref probeCount);

        // 2) Fill remaining probe slots across static Eval.Score, Survival (min HpLost), and BoardInvestment+Setup.
        int remaining = probeBudget - probeCount;
        if (remaining > 0)
        {
            int survQuota = remaining >= 3 ? Math.Max(1, (remaining * 25 + 50) / 100) : 0;
            int invQuota = remaining >= 2 ? Math.Max(1, (remaining * 25 + 50) / 100) : 0;
            int evalQuota = Math.Max(0, remaining - survQuota - invQuota);

            SelectResolvedStaticTopK(resolved, resolvedCount, marks, evalQuota, 0, probeIndices, ref probeCount);
            SelectResolvedStaticTopK(resolved, resolvedCount, marks, survQuota, 1, probeIndices, ref probeCount);
            SelectResolvedStaticTopK(resolved, resolvedCount, marks, invQuota, 2, probeIndices, ref probeCount);

            if (probeCount < probeBudget)
                SelectResolvedStaticTopK(resolved, resolvedCount, marks, probeBudget - probeCount, 0, probeIndices, ref probeCount);
        }
    }

    private static void SelectDepth0RepresentativesFromSortedPool(
        HotResolvedChoice[] resolved,
        ReadOnlySpan<int> sortedPool,
        Span<byte> marks,
        int maxTake,
        Span<int> dst,
        ref int dstCount)
    {
        if (maxTake <= 0 || sortedPool.Length == 0)
            return;

        Span<HotChoiceSelection> seenDepth0 = stackalloc HotChoiceSelection[HotExpansionWorkspace.ChoiceResolvedCapacity];
        int seenCount = 0;

        for (int i = 0; i < sortedPool.Length && seenCount < maxTake; i++)
        {
            int idx = sortedPool[i];
            if (marks[idx] != 0)
                continue;

            HotChoiceSelection d0 = GetDepth0Selection(in resolved[idx]);
            if (ContainsChoiceSelection(seenDepth0[..seenCount], d0))
                continue;

            seenDepth0[seenCount++] = d0;
            marks[idx] = 1;
            dst[dstCount++] = idx;
        }
    }

    private static void SelectResolvedStaticTopK(
        HotResolvedChoice[] resolved,
        int count,
        Span<byte> marks,
        int take,
        int staticCriterion,
        Span<int> dst,
        ref int dstCount)
    {
        for (int k = 0; k < take; k++)
        {
            int bestIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (marks[i] != 0)
                    continue;
                if (bestIdx < 0 || CompareResolvedStatic(in resolved[i], in resolved[bestIdx], staticCriterion) < 0)
                    bestIdx = i;
            }
            if (bestIdx < 0)
                break;
            marks[bestIdx] = 1;
            dst[dstCount++] = bestIdx;
        }
    }

    private static void SortResolvedIndicesByStatic(Span<int> indices, HotResolvedChoice[] resolved)
    {
        for (int i = 1; i < indices.Length; i++)
        {
            int key = indices[i];
            int j = i - 1;
            while (j >= 0 && CompareResolvedStatic(in resolved[key], in resolved[indices[j]], staticCriterion: 0) < 0)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareResolvedStatic(in HotResolvedChoice a, in HotResolvedChoice b, int staticCriterion)
    {
        int cmp = b.Summary.IsVictory.CompareTo(a.Summary.IsVictory);
        if (cmp != 0) return cmp;

        switch (staticCriterion)
        {
            case 1: // Static survival (alive > defeat, min HpLost, max EffectiveDefense)
                cmp = a.Summary.IsDefeat.CompareTo(b.Summary.IsDefeat);
                if (cmp != 0) return cmp;
                cmp = a.Summary.HpLost.CompareTo(b.Summary.HpLost);
                if (cmp != 0) return cmp;
                cmp = b.Eval.EffectiveDefense.CompareTo(a.Eval.EffectiveDefense);
                if (cmp != 0) return cmp;
                break;
            case 2: // Static board investment + setup + optionality
            {
                long aInvest = (long)a.Eval.BoardInvestment * 16_000L + (long)a.Eval.SetupScore * 18_000L;
                long bInvest = (long)b.Eval.BoardInvestment * 16_000L + (long)b.Eval.SetupScore * 18_000L;
                cmp = bInvest.CompareTo(aInvest);
                if (cmp != 0) return cmp;
                cmp = b.Eval.OptionalityScore.CompareTo(a.Eval.OptionalityScore);
                if (cmp != 0) return cmp;
                break;
            }
        }

        cmp = b.Eval.Score.CompareTo(a.Eval.Score);
        if (cmp != 0) return cmp;
        cmp = a.Hash.Lo.CompareTo(b.Hash.Lo);
        if (cmp != 0) return cmp;
        return a.Hash.Hi.CompareTo(b.Hash.Hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HotChoiceSelection GetDepth0Selection(in HotResolvedChoice cand)
    {
        ReadOnlySpan<HotChoiceSelection> span = cand.Prefix.AsSpan();
        return span.Length > 0 ? span[0] : default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsChoiceSelection(ReadOnlySpan<HotChoiceSelection> span, HotChoiceSelection value)
    {
        for (int i = 0; i < span.Length; i++)
        {
            HotChoiceSelection s = span[i];
            if (s.Kind == value.Kind
                && s.CardUidOrIndex == value.CardUidOrIndex
                && s.CardDefId == value.CardDefId)
            {
                return true;
            }
        }
        return false;
    }

    private static void SelectResolvedTopKFromPool(
        HotResolvedChoice[] resolved,
        ReadOnlySpan<int> pool,
        Span<byte> marks,
        int take,
        int criterion,
        Span<int> dst,
        ref int dstCount)
    {
        for (int k = 0; k < take; k++)
        {
            int bestIdx = -1;
            for (int p = 0; p < pool.Length; p++)
            {
                int idx = pool[p];
                if (marks[idx] != 0)
                    continue;
                if (bestIdx < 0 || CompareResolved(in resolved[idx], in resolved[bestIdx], criterion) < 0)
                    bestIdx = idx;
            }
            if (bestIdx < 0)
                break;
            marks[bestIdx] = 1;
            dst[dstCount++] = bestIdx;
        }
    }

    private static void SortResolvedIndicesByPrimary(Span<int> indices, HotResolvedChoice[] resolved)
    {
        for (int i = 1; i < indices.Length; i++)
        {
            int key = indices[i];
            int j = i - 1;
            while (j >= 0 && CompareResolved(in resolved[key], in resolved[indices[j]], criterion: 0) < 0)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareResolved(in HotResolvedChoice a, in HotResolvedChoice b, int criterion)
    {
        int cmp;
        switch (criterion)
        {
            case 1: // Immediate static eval
                cmp = b.Eval.Score.CompareTo(a.Eval.Score);
                if (cmp != 0) return cmp;
                break;
            case 2: // Survival (min lookahead HP lost)
                cmp = a.LookaheadHpLost.CompareTo(b.LookaheadHpLost);
                if (cmp != 0) return cmp;
                cmp = a.Summary.HpLost.CompareTo(b.Summary.HpLost);
                if (cmp != 0) return cmp;
                break;
            case 3: // Persistent board investment + SetupScore + optionality (aligned with HotBeamRetention Niche 4)
            {
                long aLookInvest = (long)a.LookaheadBoardInvestment * 16_000L + (long)a.LookaheadSetupScore * 18_000L;
                long bLookInvest = (long)b.LookaheadBoardInvestment * 16_000L + (long)b.LookaheadSetupScore * 18_000L;
                cmp = bLookInvest.CompareTo(aLookInvest);
                if (cmp != 0) return cmp;
                long aEvalInvest = (long)a.Eval.BoardInvestment * 16_000L + (long)a.Eval.SetupScore * 18_000L;
                long bEvalInvest = (long)b.Eval.BoardInvestment * 16_000L + (long)b.Eval.SetupScore * 18_000L;
                cmp = bEvalInvest.CompareTo(aEvalInvest);
                if (cmp != 0) return cmp;
                cmp = b.Eval.OptionalityScore.CompareTo(a.Eval.OptionalityScore);
                if (cmp != 0) return cmp;
                break;
            }
        }

        // Primary criterion & tie-break: Victory -> HasLookahead -> LookaheadScore -> Immediate Eval -> deterministic Hash.
        cmp = b.LookaheadVictory.CompareTo(a.LookaheadVictory);
        if (cmp != 0) return cmp;
        if (a.HasLookahead != b.HasLookahead)
            return b.HasLookahead.CompareTo(a.HasLookahead);
        if (a.HasLookahead)
        {
            cmp = b.LookaheadScore.CompareTo(a.LookaheadScore);
            if (cmp != 0) return cmp;
        }
        cmp = b.Eval.Score.CompareTo(a.Eval.Score);
        if (cmp != 0) return cmp;
        cmp = a.Hash.Lo.CompareTo(b.Hash.Lo);
        if (cmp != 0) return cmp;
        return a.Hash.Hi.CompareTo(b.Hash.Hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EncodeProbeRankScore(in HotMiniRolloutResult probe, in HotSearchPolicy policy)
    {
        long s = Math.Clamp(probe.Score, -1_000_000_000_000_000L, 1_000_000_000_000_000L);
        if (probe.IsVictory)
        {
            long rank = 2_000_000_000_000_000L + s - (long)probe.HpLost * 100_000L;
            if (policy.HasAnyStrategyTargets)
            {
                rank += (long)probe.StrategyGoalHpCredit * 100_000L
                    + (long)probe.StrategyGoalCount * 25_000L;
            }
            return rank;
        }
        if (probe.IsDefeat)
            return -2_000_000_000_000_000L + s;
        if (probe.IsEventDefeat)
            return -1_500_000_000_000_000L + s;
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryInsertLocalChoiceTt(HotExpansionWorkspace ws, HotHash128 hash)
    {
        int slot = (int)(hash.Lo & HotExpansionWorkspace.ChoiceTtMask);
        for (int probe = 0; probe < HotExpansionWorkspace.ChoiceTtSize; probe++)
        {
            if (ws.LocalChoiceTtUsed[slot] == 0)
            {
                ws.LocalChoiceTtUsed[slot] = 1;
                ws.LocalChoiceTt[slot] = hash;
                return true;
            }
            if (ws.LocalChoiceTt[slot] == hash)
                return false;
            slot = (slot + 1) & HotExpansionWorkspace.ChoiceTtMask;
        }
        return true;
    }

    private static void SortChoiceOptionsByUid(Span<HotChoiceSelection> opts)
    {
        for (int i = 1; i < opts.Length; i++)
        {
            HotChoiceSelection key = opts[i];
            int j = i - 1;
            while (j >= 0 && opts[j].CardUidOrIndex > key.CardUidOrIndex)
            {
                opts[j + 1] = opts[j];
                j--;
            }
            opts[j + 1] = key;
        }
    }

    private static void SortPrefixSelectionsByUid(ref HotChoicePrefix pfx)
    {
        int count = pfx.Count;
        for (int i = 1; i < count; i++)
        {
            HotChoiceSelection key = pfx.Selections[i];
            int j = i - 1;
            while (j >= 0 && pfx.Selections[j].CardUidOrIndex > key.CardUidOrIndex)
            {
                pfx.Selections[j + 1] = pfx.Selections[j];
                j--;
            }
            pfx.Selections[j + 1] = key;
        }
    }

    private static void SortMarginalIndicesDescending(Span<int> order, long[] scores, HotChoiceSelection[] opts)
    {
        for (int i = 1; i < order.Length; i++)
        {
            int key = order[i];
            long keyScore = scores[key];
            ushort keyUid = opts[key].CardUidOrIndex;
            int j = i - 1;
            while (j >= 0 && (scores[order[j]] < keyScore
                || (scores[order[j]] == keyScore && opts[order[j]].CardUidOrIndex > keyUid)))
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsUShort(ReadOnlySpan<ushort> span, ushort value)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == value)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountFilledPotions(in CombatHotState state)
    {
        int filled = 0;
        int slots = Math.Min((int)state.PotionSlotCount, HotLimits.MaxPotions);
        for (int i = 0; i < slots; i++)
        {
            if (state.Potions[i] != 0)
                filled++;
        }
        return filled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPileChoiceKind(HotChoiceKind kind)
        => kind is HotChoiceKind.HandCard or HotChoiceKind.DrawPile or HotChoiceKind.DiscardPile or HotChoiceKind.ExhaustPile;

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
        if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
            && !policy.AllowsBrightestFlame(ws.WorkingState.BrightestFlameMaxHpSpent))
        {
            return;
        }

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
            out HotEvalFeatures eval);

        int choiceOffset = choices.Length > 0 ? ws.AppendStagedChoices(choices) : -1;

        // Propagate mid-turn choice lookahead across remaining same-turn non-choice actions
        // (e.g. Headbutt(Bludgeon+) -> Strike) so tie-breaks stay lookahead-guided until EndTurn.
        bool inheritLookahead = choices.Length == 0
            && action.Kind != HotActionKind.EndTurn
            && action.Kind != HotActionKind.TurnSetup
            && parent.HasChoiceLookahead;

        ws.AddStagedChild(new HotStagedChild
        {
            StagedHandle = stagedHandle,
            Cold = childCold,
            Hash = hash,
            Summary = summary,
            Eval = eval,
            RolloutScore = inheritLookahead ? parent.RolloutScore : 0,
            ChoiceOffset = choiceOffset,
            Action = action,
            ChoiceCount = (byte)choices.Length,
            PotionCostDelta = potionCostDelta,
            PotionSlotBit = potionSlotBit,
            PotionCountDelta = totalPotionDelta,
            ExplicitPotionDelta = explicitPotionDelta,
            OptionalPaidCountDelta = optionalPaidCountDelta,
            AmbergrisDelta = ambergrisDelta,
            Flags = inheritLookahead ? HotNodeFlags.HasChoiceLookahead : HotNodeFlags.None,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveChoiceBranchCap(HotChoiceKind kind, HotActionKind actionKind, in HotWaveExpansionConfig config)
    {
        int raw = kind == HotChoiceKind.HandCard
            ? config.MaxHandChoiceBranches
            : config.MaxPileChoiceBranches;
        int cap = Math.Clamp(raw > 0 ? raw : 18, 1, 64);
        if (actionKind == HotActionKind.TurnSetup)
            cap = Math.Max(cap, 64);
        return cap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DetectAutoPotionDelta(
        in CombatHotState before,
        in CombatHotState after,
        byte explicitPotionDelta)
    {
        int beforeFilled = CountFilledPotions(in before);
        int afterFilled = CountFilledPotions(in after);
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
            if (cj.CardUid != 0 && HotCombatEngine.IsCardUidReferenced(in s, cold, cj.CardUid))
                continue;

            ReadOnlySpan<byte> bytesJ = HotStatePacker.AsReadOnlyBytes(in cj);
            for (int i = 0; i < j; i++)
            {
                if ((dupMask & (1UL << i)) != 0)
                    continue;
                ref readonly HotCard ci = ref s.Cards[i];
                if (ci.CardDefId != cj.CardDefId)
                    continue;
                if (ci.CardUid != 0 && HotCombatEngine.IsCardUidReferenced(in s, cold, ci.CardUid))
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
