using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotSearch;

internal struct HotRolloutStats
{
    public int RolloutCandidates;
    public int RolloutSteps;
    public int RolloutVictoriesFound;
    public int RolloutVictoriesPromoted;
    public int RolloutCapacityDrops;
    public long RolloutElapsedTicks;
    public long RolloutAllocatedBytes;

    public readonly double RolloutElapsedMilliseconds
        => (double)RolloutElapsedTicks * 1000.0 / Stopwatch.Frequency;
}

internal readonly struct HotMiniRolloutResult
{
    public readonly long Score;
    public readonly int HpLost;
    public readonly int BoardInvestment;
    public readonly int SetupScore;
    public readonly int StrategyGoalHpCredit;
    public readonly int StrategyGoalCount;
    public readonly bool IsVictory;
    public readonly bool IsDefeat;
    public readonly bool IsEventDefeat;
    public readonly int StepsExecuted;

    public HotMiniRolloutResult(
        long score,
        int hpLost,
        int boardInvestment,
        bool isVictory,
        bool isDefeat,
        bool isEventDefeat,
        int stepsExecuted,
        int setupScore = 0,
        int strategyGoalHpCredit = 0,
        int strategyGoalCount = 0)
    {
        Score = score;
        HpLost = hpLost;
        BoardInvestment = boardInvestment;
        SetupScore = setupScore;
        StrategyGoalHpCredit = strategyGoalHpCredit;
        StrategyGoalCount = strategyGoalCount;
        IsVictory = isVictory;
        IsDefeat = isDefeat;
        IsEventDefeat = isEventDefeat;
        StepsExecuted = stepsExecuted;
    }
}

/// <summary>
/// Turn-boundary zero-allocation 2-pilot deterministic fast rollout evaluator and exact victory promoter:
/// - Unpacks each shortlisted turn-boundary candidate into a worker-private <see cref="CombatHotState"/>
///   and simulates up to <c>horizonTurns</c> full turns in-place using exact <c>DrawPile</c> and <c>HotRng10</c>
///   (zero Pack, zero Hash, zero TT, zero Arena writes during rollout steps, zero paid potions).
/// - Runs 2 complementary 100% domain-agnostic pilots (zero card-ID rules):
///   Pilot A (Defensive/Tempo) and Pilot B (Setup-First), taking <c>max(Eval(A), Eval(B))</c> while honoring
///   unified centi-HP economic value <c>V</c> and treating <see cref="HotCombatOutcome.EventDefeat"/> strictly
///   below <see cref="HotCombatOutcome.Victory"/>.
/// - Records winning pilot suffixes into worker-private fixed buffers and replays promotable victories into
///   <see cref="HotNodeArena"/> and <c>victoriesOut</c> in strict deterministic <c>0..M-1</c> shortlist order,
///   guaranteeing 100% bit-exact DOP=1 vs DOP=8 determinism and exact <see cref="HotPlanMaterializer"/> parity.
/// - Also exposes <see cref="EvaluateChoiceProbe"/> for Stage 8D zero-allocation 1-2 turn choice shortlisting.
/// </summary>
internal sealed class HotRolloutEvaluator : IDisposable
{
    private const int MaxActionsPerRolloutTurn = 24;
    private const int MaxTotalRolloutSteps = 96;
    private const int MaxTraceChoicesPerPilot = 256;
    private const int ActionBufferSize = 128;
    private const int ChoiceBufferSize = 128;
    private const int InitialPromotedStepCapacity = 256;
    private const int InitialPromotedChoiceCapacity = 256;
    private const int TrimPromotedStepThreshold = 4096;

    private enum RolloutPilotKind : byte
    {
        DefensiveTempo = 0,
        SetupFirst = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HotRolloutTraceStep
    {
        public HotAction Action;
        public byte ChoiceCount;
        public int ChoiceOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HotRolloutCandidateResult
    {
        public long RolloutScore;
        public int OwnerWorker;
        public int PromotedStepStart;
        public int PromotedStepCount;
        public bool Completed;
        public bool HasPromotableVictory;
    }

    private const ulong AttackRelicMask = (1UL << (int)RelicCounterId.PenNib) | (1UL << (int)RelicCounterId.Nunchaku);
    private const ulong SkillRelicMask = 1UL << (int)RelicCounterId.TuningFork;
    private const ulong CardRelicMask = 1UL << (int)RelicCounterId.IronClub;
    private const ulong ExhaustRelicMask = 1UL << (int)RelicCounterId.JossPaper;
    private const ulong StarRelicMask = 1UL << (int)RelicCounterId.GalacticDust;
    private const ulong TurnTickRelicMask = (1UL << (int)RelicCounterId.HappyFlower)
        | (1UL << (int)RelicCounterId.FakeHappyFlower)
        | (1UL << (int)RelicCounterId.Pendulum)
        | (1UL << (int)RelicCounterId.PollinousCore);

    private struct FastTurnContext
    {
        public int UnblockedIncoming;
        public int PlayerStrength;
        public int PlayerDexterity;
        public int AliveEnemies;
        public int RevivingEnemies;
        public bool PlayerWeak;
        public bool PlayerFrail;
        public bool HasActiveScaling;
        public bool HasRetainHand;
        public bool PreservesExcessBlock;
        public bool HasStratagemPower;
    }

    private struct RolloutStrategyContext
    {
        public bool Active;
        public bool PureGrowthUnsatisfied;
        public bool RelicUnsatisfied;
        public ulong UnsatisfiedRelicMask;
        public int RelicDistance;
        public bool HasLethalInHand;
        public bool HoldLastEnemyLethal;
        public bool HasEmptyPotionSlot;
        public bool HasAnyNonMinionEnemy;
        public int RemainingStrategyBudget;
    }

    private sealed class HotRolloutWorkspace
    {
        public CombatHotState BaseState;
        public CombatHotState RolloutState;
        public CombatHotState PreStepState;
        public readonly HotExecScratch Scratch = new();
        public readonly HotAction[] ActionBuf = new HotAction[ActionBufferSize];
        public readonly HotChoiceSelection[] ChoiceOptBuf = new HotChoiceSelection[ChoiceBufferSize];
        public readonly HotChoiceSelection[] StepChoiceBuf = new HotChoiceSelection[HotLimits.MaxChoicesPerAction];

        // Per-candidate fixed scratch traces for Pilot A and Pilot B (0 heap alloc per rollout).
        public readonly HotRolloutTraceStep[] TraceStepsA = new HotRolloutTraceStep[MaxTotalRolloutSteps];
        public readonly HotChoiceSelection[] TraceChoicesA = new HotChoiceSelection[MaxTraceChoicesPerPilot];
        public readonly HotRolloutTraceStep[] TraceStepsB = new HotRolloutTraceStep[MaxTotalRolloutSteps];
        public readonly HotChoiceSelection[] TraceChoicesB = new HotChoiceSelection[MaxTraceChoicesPerPilot];

        // Per-wave compact slab holding only promotable victory traces discovered by this worker.
        public HotRolloutTraceStep[] PromotedSteps = new HotRolloutTraceStep[InitialPromotedStepCapacity];
        public int PromotedStepCount;
        public HotChoiceSelection[] PromotedChoices = new HotChoiceSelection[InitialPromotedChoiceCapacity];
        public int PromotedChoiceCount;

        public int WaveSteps;
        public int WaveVictoriesFound;
        public int WaveCapacityDrops;
        public long WaveAllocatedBytes;

        public void ResetWave()
        {
            if (PromotedSteps.Length > TrimPromotedStepThreshold)
                PromotedSteps = new HotRolloutTraceStep[InitialPromotedStepCapacity];
            if (PromotedChoices.Length > TrimPromotedStepThreshold)
                PromotedChoices = new HotChoiceSelection[InitialPromotedChoiceCapacity];
            PromotedStepCount = 0;
            PromotedChoiceCount = 0;
            WaveSteps = 0;
            WaveVictoriesFound = 0;
            WaveCapacityDrops = 0;
            WaveAllocatedBytes = 0;
        }

        public int SavePromotedTrace(
            HotRolloutTraceStep[] srcSteps,
            int stepCount,
            HotChoiceSelection[] srcChoices)
        {
            if (stepCount <= 0)
                return -1;

            int totalChoices = 0;
            for (int i = 0; i < stepCount; i++)
                totalChoices += srcSteps[i].ChoiceCount;

            if (PromotedStepCount + stepCount > PromotedSteps.Length)
                Array.Resize(ref PromotedSteps, Math.Max(PromotedSteps.Length * 2, PromotedStepCount + stepCount));
            if (totalChoices > 0 && PromotedChoiceCount + totalChoices > PromotedChoices.Length)
                Array.Resize(ref PromotedChoices, Math.Max(PromotedChoices.Length * 2, PromotedChoiceCount + totalChoices));

            int stepStart = PromotedStepCount;
            for (int i = 0; i < stepCount; i++)
            {
                HotRolloutTraceStep step = srcSteps[i];
                int dstChoiceOffset = -1;
                if (step.ChoiceCount > 0)
                {
                    dstChoiceOffset = PromotedChoiceCount;
                    srcChoices.AsSpan(step.ChoiceOffset, step.ChoiceCount)
                        .CopyTo(PromotedChoices.AsSpan(dstChoiceOffset, step.ChoiceCount));
                    PromotedChoiceCount += step.ChoiceCount;
                }
                PromotedSteps[PromotedStepCount++] = new HotRolloutTraceStep
                {
                    Action = step.Action,
                    ChoiceCount = step.ChoiceCount,
                    ChoiceOffset = dstChoiceOffset,
                };
            }
            return stepStart;
        }
    }

    private readonly int _maxWorkers;
    private readonly HotRolloutWorkspace[] _workspaces;
    private HotRolloutCandidateResult[] _results;
    private CombatHotState _commitState;
    private CombatHotState _commitBeforeStepState;
    private readonly HotExecScratch _commitScratch = new();
    private readonly HotSearchNode[] _stagedPromoteNodes = new HotSearchNode[MaxTotalRolloutSteps];
    private readonly int[] _stagedPromoteChoiceStarts = new int[MaxTotalRolloutSteps];
    private readonly byte[] _stagedPromoteChoiceCounts = new byte[MaxTotalRolloutSteps];
    private bool _disposed;

    public HotRolloutEvaluator(int maxDegreeOfParallelism)
    {
        _maxWorkers = Math.Clamp(maxDegreeOfParallelism, 1, HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);
        _workspaces = new HotRolloutWorkspace[_maxWorkers];
        for (int i = 0; i < _maxWorkers; i++)
            _workspaces[i] = new HotRolloutWorkspace();
        _results = new HotRolloutCandidateResult[256];
    }

    /// <summary>
    /// Runs 2-pilot deterministic <paramref name="horizonTurns"/>-turn rollouts over
    /// <paramref name="shortlistNodeIds"/> across <c>1..DOP</c> worker-private workspaces, then commits
    /// <see cref="HotSearchNode.RolloutScore"/> and any promotable exact Victory suffixes in strict
    /// deterministic <c>0..shortlistNodeIds.Length-1</c> order.
    /// </summary>
    public void EvaluateTurnBoundary(
        ReadOnlySpan<int> shortlistNodeIds,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
        int horizonTurns,
        List<int> victoriesOut,
        ref HotRolloutStats stats,
        CancellationToken cancellationToken)
    {
        int count = shortlistNodeIds.Length;
        if (count == 0 || horizonTurns <= 0)
            return;

        long startTicks = Stopwatch.GetTimestamp();
        EnsureResultCapacity(count);

        int workerCount = _maxWorkers == 1 || count <= 2
            ? 1
            : Math.Min(_maxWorkers, count);

        for (int w = 0; w < workerCount; w++)
            _workspaces[w].ResetWave();

        if (workerCount == 1)
        {
            HotRolloutWorkspace ws = _workspaces[0];
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EvaluateCandidate(
                    ws,
                    workerIdx: 0,
                    shortlistNodeIds[i],
                    nodeArena,
                    mainStateArena,
                    rules,
                    in policy,
                    in evalLookup,
                    evalMode,
                    horizonTurns,
                    out _results[i]);
            }
            ws.WaveAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
        }
        else
        {
            int[] nodeArray = System.Buffers.ArrayPool<int>.Shared.Rent(count);
            shortlistNodeIds.CopyTo(nodeArray);
            int cursor = 0;
            int batchSize = Math.Clamp(count / (workerCount * 4), 1, 4);

            HotSearchPolicy policyCopy = policy;
            HotEvalLookup lookupCopy = evalLookup;

            try
            {
                Parallel.For(0, workerCount, workerIdx =>
                {
                    HotRolloutWorkspace ws = _workspaces[workerIdx];
                    long allocBefore = GC.GetAllocatedBytesForCurrentThread();

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int start = Interlocked.Add(ref cursor, batchSize) - batchSize;
                        if (start >= count)
                            break;
                        int end = Math.Min(start + batchSize, count);
                        for (int i = start; i < end; i++)
                        {
                            EvaluateCandidate(
                                ws,
                                workerIdx,
                                nodeArray[i],
                                nodeArena,
                                mainStateArena,
                                rules,
                                in policyCopy,
                                in lookupCopy,
                                evalMode,
                                horizonTurns,
                                out _results[i]);
                        }
                    }

                    ws.WaveAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
                });
            }
            finally
            {
                System.Buffers.ArrayPool<int>.Shared.Return(nodeArray, clearArray: false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Aggregate worker counters deterministically (0..workerCount-1).
        stats.RolloutCandidates += count;
        for (int w = 0; w < workerCount; w++)
        {
            HotRolloutWorkspace ws = _workspaces[w];
            stats.RolloutSteps += ws.WaveSteps;
            stats.RolloutVictoriesFound += ws.WaveVictoriesFound;
            stats.RolloutCapacityDrops += ws.WaveCapacityDrops;
            stats.RolloutAllocatedBytes += ws.WaveAllocatedBytes;
        }

        // Strict deterministic commit order: 0 .. count - 1!
        for (int i = 0; i < count; i++)
        {
            int nodeId = shortlistNodeIds[i];
            ref readonly HotRolloutCandidateResult res = ref _results[i];
            if (!res.Completed)
                continue;

            ref HotSearchNode node = ref nodeArena.GetRef(nodeId);
            node.RolloutScore = res.RolloutScore;
            node.Flags |= HotNodeFlags.HasRolloutScore;

            if (res.HasPromotableVictory && res.PromotedStepCount > 0)
            {
                HotRolloutWorkspace ownerWs = _workspaces[res.OwnerWorker];
                if (TryPromoteRolloutVictory(
                    nodeId,
                    ownerWs,
                    res.PromotedStepStart,
                    res.PromotedStepCount,
                    nodeArena,
                    mainStateArena,
                    rules,
                    in policy,
                    in evalLookup,
                    evalMode,
                    victoriesOut))
                {
                    stats.RolloutVictoriesPromoted++;
                }
            }
        }

        stats.RolloutElapsedTicks += Stopwatch.GetTimestamp() - startTicks;
    }

    /// <summary>
    /// Shared zero-allocation 1-2 turn greedy rollout probe for Stage 8D choice shortlisting (#294 Pillar 3).
    /// Plays out the remainder of the current turn plus up to <paramref name="maxTurnsAhead"/> subsequent turns
    /// using the exact deterministic <c>DrawPile</c> and <c>HotRng10</c>, with zero paid potions and no recursion.
    /// </summary>
    internal static HotMiniRolloutResult EvaluateChoiceProbe(
        in CombatHotState postChoiceState,
        HotColdSidecar? postChoiceCold,
        ref CombatHotState probeState,
        HotExecScratch probeScratch,
        HotAction[] probeActionBuf,
        HotChoiceSelection[] probeChoiceBuf,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
        int maxTurnsAhead,
        int parentNetPotionCostCenti = 0,
        int parentActionCount = 0)
    {
        probeState = postChoiceState;
        HotParallelExpander.AssignDeterministicCardUids(ref probeState);
        HotColdSidecar? cold = postChoiceCold;
        Span<HotChoiceSelection> stepChoices = stackalloc HotChoiceSelection[HotLimits.MaxChoicesPerAction];

        int clampedTurns = Math.Clamp(maxTurnsAhead, 0, 4);
        int stepsExecuted = 0;

        try
        {
            // Play remainder of current turn (+ EndTurn) plus clampedTurns subsequent full turns.
            int totalTurnSlots = clampedTurns + 1;
            CombatHotState preStepState = default;

            for (int t = 0; t < totalTurnSlots; t++)
            {
                if (probeState.TerminalOutcome != HotCombatOutcome.Ongoing || !HotCombatEngine.IsInProgress(in probeState))
                    break;

                bool turnEnded = PlaySinglePilotTurn(
                    ref probeState,
                    ref cold,
                    ref preStepState,
                    probeScratch,
                    probeActionBuf,
                    probeChoiceBuf,
                    stepChoices,
                    rules,
                    in policy,
                    in evalLookup,
                    RolloutPilotKind.DefensiveTempo,
                    recordTrace: false,
                    traceSteps: null,
                    traceChoices: null,
                    ref stepsExecuted,
                    out int traceStepsDummy,
                    out int traceChoicesDummy,
                    traceStepOffset: 0,
                    traceChoiceOffset: 0);
                _ = traceStepsDummy;
                _ = traceChoicesDummy;

                if (!turnEnded)
                    break;
                if (stepsExecuted >= MaxTotalRolloutSteps)
                    break;
            }
        }
        catch (HotCapacityExceededException)
        {
            // Evaluate best reached valid state without throwing.
        }

        HotFastSummary summary = HotEvaluator.ExtractStage1(in probeState, in policy);
        HotEvaluator.EvaluateState(
            in probeState,
            cold,
            rules,
            in policy,
            in evalLookup,
            in summary,
            parentNetPotionCostCenti,
            parentActionCount + stepsExecuted,
            evalMode,
            out HotEvalFeatures features);

        return new HotMiniRolloutResult(
            score: features.Score,
            hpLost: summary.HpLost,
            boardInvestment: features.BoardInvestment,
            isVictory: summary.IsVictory
                && probeState.TerminalOutcome == HotCombatOutcome.Victory
                && (probeState.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) == 0,
            isDefeat: summary.IsDefeat,
            isEventDefeat: summary.IsEventDefeat,
            stepsExecuted: stepsExecuted,
            setupScore: features.SetupScore,
            strategyGoalHpCredit: policy.HasAnyStrategyTargets ? summary.StrategyGoalHpCredit : 0,
            strategyGoalCount: policy.HasAnyStrategyTargets ? summary.StrategyGoalCount : 0);
    }

    private static void EvaluateCandidate(
        HotRolloutWorkspace ws,
        int workerIdx,
        int nodeId,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
        int horizonTurns,
        out HotRolloutCandidateResult result)
    {
        result = default;

        ref readonly HotSearchNode parent = ref nodeArena.GetRef(nodeId);
        if (parent.IsTerminal || parent.StateHandle.Length <= 0)
            return;

        HotStatePacker.Unpack(mainStateArena.GetSpan(parent.StateHandle), ref ws.BaseState);
        if (ws.BaseState.TerminalOutcome != HotCombatOutcome.Ongoing || !HotCombatEngine.IsInProgress(in ws.BaseState))
            return;

        // RO-02: assign deterministic non-zero CardUids once on the unpacked (already canonicalized)
        // base state so rollout steps only re-canonicalize lazily when a choice screen triggers.
        HotParallelExpander.AssignDeterministicCardUids(ref ws.BaseState);

        bool parentSatisfiesHardPotions = CanPromotePotionFreeSuffix(in parent, in policy);

        // Pilot A: Defensive / Tempo
        bool okA = RunPilotFromBase(
            ws,
            in parent,
            rules,
            in policy,
            in evalLookup,
            evalMode,
            horizonTurns,
            RolloutPilotKind.DefensiveTempo,
            recordTrace: parentSatisfiesHardPotions,
            ws.TraceStepsA,
            ws.TraceChoicesA,
            out long scoreA,
            out bool victoryA,
            out bool defeatA,
            out bool eventDefeatA,
            out byte deathSavesA,
            out long vCentiA,
            out int goalCreditA,
            out int goalCountA,
            out int endTurnA,
            out int traceLenA,
            out int stepsA,
            out bool cleanWinA);

        // RO-02: Smart Pilot B skip when Pilot A already achieved a clean zero-loss, zero-death-save,
        // all-strategy-targets-satisfied victory, or when no setup/power/engine/debuff/retain cards exist
        // in live piles (and no strategy targets require SetupFirst stalling).
        bool skipPilotB = okA
            && (cleanWinA
                || (!policy.HasAnyStrategyTargets
                    && !HasSetupFirstRelevantCards(in ws.BaseState, rules, in evalLookup)));

        bool okB = false;
        long scoreB = 0;
        bool victoryB = false;
        bool defeatB = false;
        bool eventDefeatB = false;
        byte deathSavesB = 0;
        long vCentiB = 0;
        int goalCreditB = 0;
        int goalCountB = 0;
        int endTurnB = 0;
        int traceLenB = 0;
        int stepsB = 0;

        if (!skipPilotB)
        {
            // Pilot B: Setup-First
            okB = RunPilotFromBase(
                ws,
                in parent,
                rules,
                in policy,
                in evalLookup,
                evalMode,
                horizonTurns,
                RolloutPilotKind.SetupFirst,
                recordTrace: parentSatisfiesHardPotions,
                ws.TraceStepsB,
                ws.TraceChoicesB,
                out scoreB,
                out victoryB,
                out defeatB,
                out eventDefeatB,
                out deathSavesB,
                out vCentiB,
                out goalCreditB,
                out goalCountB,
                out endTurnB,
                out traceLenB,
                out stepsB,
                out _);
        }

        ws.WaveSteps += stepsA + stepsB;

        if (!okA && !okB)
            return;

        bool chooseB;
        if (!okA)
        {
            chooseB = true;
        }
        else if (!okB)
        {
            chooseB = false;
        }
        else
        {
            int rankA = GetPilotOutcomeRank(victoryA, defeatA, eventDefeatA);
            int rankB = GetPilotOutcomeRank(victoryB, defeatB, eventDefeatB);
            if (rankA != rankB)
                chooseB = rankB > rankA;
            else if (victoryA && victoryB)
                chooseB = IsBetterVictory(
                    deathSavesB, vCentiB, goalCreditB, goalCountB, endTurnB, scoreB,
                    deathSavesA, vCentiA, goalCreditA, goalCountA, endTurnA, scoreA);
            else
                chooseB = scoreB > scoreA;
        }

        long bestScore = chooseB ? scoreB : scoreA;
        bool foundVictory = victoryA || victoryB;
        if (foundVictory)
            ws.WaveVictoriesFound++;

        int promotedStart = -1;
        int promotedCount = 0;
        bool hasPromotable = false;

        if (foundVictory && parentSatisfiesHardPotions)
        {
            // When both pilots win, rank strictly by (DeathSaveUseCount ASC, vCenti DESC, StrategyGoalHpCredit DESC, StrategyGoalCount DESC, Turn ASC).
            bool promoteB = victoryB
                && (!victoryA || IsBetterVictory(
                    deathSavesB, vCentiB, goalCreditB, goalCountB, endTurnB, scoreB,
                    deathSavesA, vCentiA, goalCreditA, goalCountA, endTurnA, scoreA));
            if (promoteB && traceLenB <= 0 && victoryA && traceLenA > 0)
                promoteB = false;
            else if (!promoteB && traceLenA <= 0 && victoryB && traceLenB > 0)
                promoteB = true;
            int chosenTraceLen = promoteB ? traceLenB : traceLenA;
            if (chosenTraceLen > 0)
            {
                HotRolloutTraceStep[] srcSteps = promoteB ? ws.TraceStepsB : ws.TraceStepsA;
                HotChoiceSelection[] srcChoices = promoteB ? ws.TraceChoicesB : ws.TraceChoicesA;
                promotedStart = ws.SavePromotedTrace(srcSteps, chosenTraceLen, srcChoices);
                if (promotedStart >= 0)
                {
                    promotedCount = chosenTraceLen;
                    hasPromotable = true;
                }
            }
        }

        result = new HotRolloutCandidateResult
        {
            RolloutScore = bestScore,
            OwnerWorker = workerIdx,
            PromotedStepStart = promotedStart,
            PromotedStepCount = promotedCount,
            Completed = true,
            HasPromotableVictory = hasPromotable,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPilotOutcomeRank(bool isVictory, bool isDefeat, bool isEventDefeat)
        => isVictory ? 3 : (!isDefeat && !isEventDefeat ? 2 : (isEventDefeat ? 1 : 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBetterVictory(
        byte deathSavesB,
        long vCentiB,
        int goalCreditB,
        int goalCountB,
        int turnB,
        long scoreB,
        byte deathSavesA,
        long vCentiA,
        int goalCreditA,
        int goalCountA,
        int turnA,
        long scoreA)
    {
        if (deathSavesB != deathSavesA)
            return deathSavesB < deathSavesA;
        if (vCentiB != vCentiA)
            return vCentiB > vCentiA;
        if (goalCreditB != goalCreditA)
            return goalCreditB > goalCreditA;
        if (goalCountB != goalCountA)
            return goalCountB > goalCountA;
        if (turnB != turnA)
            return turnB < turnA;
        return scoreB > scoreA;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanPromotePotionFreeSuffix(in HotSearchNode parent, in HotSearchPolicy policy)
    {
        if (policy.ForcedPotionSlotMask != 0
            && (parent.PotionMask & policy.ForcedPotionSlotMask) != policy.ForcedPotionSlotMask)
        {
            return false;
        }
        if (policy.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
            && parent.ExplicitPotionCount < policy.MinimumRequiredPotionUses)
        {
            return false;
        }
        if (policy.EffectivePotionPolicy == SolverPotionPolicy.Disabled
            && (parent.OptionalPaidPotionCount > 0 || parent.ExplicitPotionCount > policy.ForcedDirectiveCount))
        {
            return false;
        }
        return true;
    }

    private static bool HasSetupFirstRelevantCards(
        in CombatHotState s,
        HotRules rules,
        in HotEvalLookup evalLookup)
    {
        const HotCardRoleFlags setupRelevantRoles = HotCardRoleFlags.HasSelfBuff
            | HotCardRoleFlags.HasEngineOp
            | HotCardRoleFlags.HasDraw
            | HotCardRoleFlags.HasEnemyDebuff
            | HotCardRoleFlags.AppliesWeak;

        HotCardRoleFlags[]? cardRoles = evalLookup.CardRoles;
        int liveLimit = Math.Min(s.DiscardStart + s.DiscardCount, s.TotalCardCount);
        for (int idx = 0; idx < liveLimit; idx++)
        {
            ref readonly HotCard c = ref s.Cards[idx];
            if (c.CardDefId == 0 || c.CardDefId >= rules.Cards.Length)
                continue;
            HotCardDef d = rules.Cards[c.CardDefId];
            if (d.Type is CardType.Status or CardType.Curse)
                continue;
            if (d.Type == CardType.Power || d.SetupWeight > 0)
                return true;
            if (HotCombatEngine.HasEffectiveRetainKeyword(in s, in c, rules)
                || (c.Flags & HotCardFlags.SingleTurnRetain) != 0)
            {
                return true;
            }
            if (cardRoles != null && (uint)c.CardDefId < (uint)cardRoles.Length
                && (cardRoles[c.CardDefId] & setupRelevantRoles) != 0)
            {
                return true;
            }
            if (d.OpCount > 0)
            {
                ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(c.CardDefId);
                for (int k = 0; k < ops.Length; k++)
                {
                    switch (ops[k].Op)
                    {
                        case HotCardOpKind.Draw:
                        case HotCardOpKind.ChannelOrb:
                        case HotCardOpKind.EvokeOrb:
                        case HotCardOpKind.SummonOsty:
                        case HotCardOpKind.Forge:
                        case HotCardOpKind.ShootArrow:
                        case HotCardOpKind.ApplyPowerSelf:
                        case HotCardOpKind.ApplyTempStat:
                        case HotCardOpKind.ApplyPowerTarget:
                        case HotCardOpKind.ApplyPowerAllEnemies:
                            return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool RunPilotFromBase(
        HotRolloutWorkspace ws,
        in HotSearchNode parent,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
        int horizonTurns,
        RolloutPilotKind pilot,
        bool recordTrace,
        HotRolloutTraceStep[] traceSteps,
        HotChoiceSelection[] traceChoices,
        out long finalScore,
        out bool isRealVictory,
        out bool isDefeat,
        out bool isEventDefeat,
        out byte deathSaveUseCount,
        out long victoryVCenti,
        out int strategyGoalHpCredit,
        out int strategyGoalCount,
        out int endTurn,
        out int traceStepCount,
        out int stepsExecuted,
        out bool isCleanPerfectVictory)
    {
        ws.RolloutState = ws.BaseState;
        HotColdSidecar? cold = parent.Cold;

        stepsExecuted = 0;
        traceStepCount = 0;
        isCleanPerfectVictory = false;
        int traceChoiceCount = 0;
        bool traceOverflowed = false;

        try
        {
            for (int t = 0; t < horizonTurns; t++)
            {
                if (ws.RolloutState.TerminalOutcome != HotCombatOutcome.Ongoing
                    || !HotCombatEngine.IsInProgress(in ws.RolloutState))
                {
                    break;
                }
                if (stepsExecuted >= MaxTotalRolloutSteps)
                    break;

                bool turnEnded = PlaySinglePilotTurn(
                    ref ws.RolloutState,
                    ref cold,
                    ref ws.PreStepState,
                    ws.Scratch,
                    ws.ActionBuf,
                    ws.ChoiceOptBuf,
                    ws.StepChoiceBuf,
                    rules,
                    in policy,
                    in evalLookup,
                    pilot,
                    recordTrace: recordTrace && !traceOverflowed,
                    traceSteps: traceSteps,
                    traceChoices: traceChoices,
                    ref stepsExecuted,
                    out int nextTraceSteps,
                    out int nextTraceChoices,
                    traceStepOffset: traceStepCount,
                    traceChoiceOffset: traceChoiceCount);

                if (nextTraceSteps < 0)
                {
                    traceOverflowed = true;
                }
                else
                {
                    traceStepCount = nextTraceSteps;
                    traceChoiceCount = nextTraceChoices;
                }

                if (!turnEnded)
                    break;
            }
        }
        catch (HotCapacityExceededException)
        {
            ws.WaveCapacityDrops++;
            finalScore = 0;
            isRealVictory = false;
            isDefeat = false;
            isEventDefeat = false;
            deathSaveUseCount = 0;
            victoryVCenti = 0;
            strategyGoalHpCredit = 0;
            strategyGoalCount = 0;
            endTurn = 0;
            traceStepCount = 0;
            return false;
        }

        HotFastSummary summary = HotEvaluator.ExtractStage1(in ws.RolloutState, in policy);
        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            parent.PotionStrategicCost,
            parent.OptionalPaidPotionCount,
            summary.AlchemizeCount,
            summary.EmptyPotionSlots,
            in policy);

        // Strict Victory gate (#287): NEVER treat EventDefeat / BattlewornDummyTimedOut as Victory!
        isRealVictory = summary.IsVictory
            && ws.RolloutState.TerminalOutcome == HotCombatOutcome.Victory
            && (ws.RolloutState.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) == 0;
        isDefeat = !isRealVictory && summary.IsDefeat;
        isEventDefeat = !isRealVictory && !isDefeat && summary.IsEventDefeat;
        deathSaveUseCount = summary.DeathSaveUseCount;
        victoryVCenti = 0;
        strategyGoalHpCredit = summary.StrategyGoalHpCredit;
        strategyGoalCount = summary.StrategyGoalCount;
        endTurn = ws.RolloutState.PlayerTurnNumber;

        if (isRealVictory)
        {
            int postCombatHeal = HotRouteQuality.RankedPostCombatRelicHeal(
                policy.PostCombatRelicHeal,
                completeVictory: true,
                summary.PlayerHp,
                summary.PlayerMaxHp);
            int netPersistentHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
                endPlayerHp: summary.PlayerHp,
                postCombatMonotoneHeal: postCombatHeal,
                initialPlayerHp: policy.InitialPlayerHp,
                endPlayerMaxHp: summary.PlayerMaxHp,
                initialPlayerMaxHp: policy.InitialPlayerMaxHp,
                deathSaveHpRestored: summary.DeathSaveHpRestored,
                growthHpCredit: summary.GrowthHpCredit,
                bossHpRelief: policy.StrategicBossHpRelief,
                ignoreLongTermRewards: policy.IgnoreLongTermRewards,
                relicHpCredit: summary.RelicHpCredit,
                relicHealingHpCredit: summary.RelicHealingHpCredit,
                feedMaxHpExcluded: summary.FeedCount * policy.FeedMaxHpPerKill);
            victoryVCenti = (long)netPersistentHpDeltaCenti - netPotionCostCenti;

            if (!policy.HasAnyStrategyTargets)
            {
                long rawWin = 25_000_000L
                    + victoryVCenti * 390L
                    - (long)ws.RolloutState.PlayerTurnNumber * 5_000L
                    - (long)summary.DeathSaveUseCount * 4_000_000L;
                finalScore = Math.Max(summary.DeathSaveUseCount == 0 ? 12_000_000L : 8_000_000L, rawWin);
            }
            else if (summary.AllStrategyTargetsSatisfied)
            {
                long rawWin = 25_000_000L
                    + victoryVCenti * 390L
                    + (long)summary.StrategyGoalCount * 25_000L
                    - (long)ws.RolloutState.PlayerTurnNumber * 5_000L
                    - (long)summary.DeathSaveUseCount * 4_000_000L;
                finalScore = Math.Max(summary.DeathSaveUseCount == 0 ? 12_000_000L : 8_000_000L, rawWin);
            }
            else if (summary.StrategyGoalCount > 0 || summary.StrategyGoalHpCredit > 0)
            {
                long rawWin = 16_000_000L
                    + victoryVCenti * 390L
                    + (long)summary.StrategyGoalCount * 25_000L
                    - (long)ws.RolloutState.PlayerTurnNumber * 5_000L
                    - (long)summary.DeathSaveUseCount * 4_000_000L;
                finalScore = Math.Max(summary.DeathSaveUseCount == 0 ? 7_000_000L : 4_000_000L, rawWin);
            }
            else
            {
                // EV-04: keep 0-progress unsatisfied strategy-mode rollout wins well below healthy
                // ongoing farming states so stalling/farming lines aren't crushed at turn boundaries.
                long rawWin = 800_000L
                    + victoryVCenti * 390L
                    - (long)ws.RolloutState.PlayerTurnNumber * 5_000L
                    - (long)summary.DeathSaveUseCount * 4_000_000L;
                finalScore = Math.Max(summary.DeathSaveUseCount == 0 ? 200_000L : 100_000L, rawWin);
            }
        }
        else if (isDefeat || isEventDefeat)
        {
            bool hasUnusedPaidPotions = policy.EffectivePotionPolicy != SolverPotionPolicy.Disabled
                && policy.PaidOptionalPotionCount > 0
                && (policy.PaidOptionalSlotMask & ~parent.PotionMask) != 0;
            long defeatBase = (evalMode == HotEvalMode.DesperateRescue || hasUnusedPaidPotions)
                ? -12_000_000L
                : -20_000_000L;
            if (isEventDefeat)
                defeatBase += 2_000_000L;
            finalScore = defeatBase
                + (long)Math.Min((int)ws.RolloutState.PlayerTurnNumber, 30) * 60_000L
                - (long)Math.Min(summary.EnemyHpSum, 2000) * 10_000L;
        }
        else
        {
            HotEvaluator.EvaluateState(
                in ws.RolloutState,
                cold,
                rules,
                in policy,
                in evalLookup,
                in summary,
                netPotionCostCenti,
                parent.ActionCount + stepsExecuted,
                evalMode,
                out HotEvalFeatures features);
            finalScore = features.Score;
        }

        if (!isRealVictory || traceOverflowed || traceStepCount <= 0)
            traceStepCount = 0;

        isCleanPerfectVictory = isRealVictory
            && summary.DeathSaveUseCount == 0
            && summary.HpLost == 0
            && summary.PlayerHp >= policy.InitialPlayerMaxHp
            && !policy.HasAnyHealOrUnknownGeneration
            && !policy.HasAlchemizeInDeck
            && (!policy.HasAnyStrategyTargets || summary.AllStrategyTargetsSatisfied)
            && (!recordTrace || traceStepCount > 0);

        return true;
    }

    private static bool PlaySinglePilotTurn(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        ref CombatHotState preStepState,
        HotExecScratch scratch,
        HotAction[] actionBuf,
        HotChoiceSelection[] choiceOptBuf,
        Span<HotChoiceSelection> stepChoices,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        RolloutPilotKind pilot,
        bool recordTrace,
        HotRolloutTraceStep[]? traceSteps,
        HotChoiceSelection[]? traceChoices,
        ref int totalStepsExecuted,
        out int outTraceSteps,
        out int outTraceChoices,
        int traceStepOffset,
        int traceChoiceOffset)
    {
        outTraceSteps = traceStepOffset;
        outTraceChoices = traceChoiceOffset;

        int actionsThisTurn = 0;
        int startTurn = state.PlayerTurnNumber;

        Span<ulong> blockedActionBits = stackalloc ulong[4]; // covers up to 256 legal actions
        Span<int> enemyHp = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyBlock = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyIncoming = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyVuln = stackalloc byte[HotLimits.MaxEnemies];

        while (totalStepsExecuted < MaxTotalRolloutSteps)
        {
            if (state.TerminalOutcome != HotCombatOutcome.Ongoing || !HotCombatEngine.IsInProgress(in state))
                return false;

            // RO-02: Lazy canonicalization — cards already carry unique non-zero deterministic UIDs
            // from base-state initialization, so only re-canonicalize here on extreme UID growth,
            // and lazily inside ExecuteActionWithGreedyChoices when res.NeedsChoice actually triggers.
            if (state.NextCardUid >= 60_000)
            {
                HotStatePacker.CanonicalizeForPack(ref state, ref cold);
                HotParallelExpander.AssignDeterministicCardUids(ref state);
            }

            int actionCount = HotCombatEngine.EnumerateLegalActions(
                in state,
                cold,
                rules,
                actionBuf,
                includePotions: false);

            if (actionCount <= 0)
                return false;

            ComputeFastTurnContext(
                in state,
                rules,
                in evalLookup,
                enemyHp,
                enemyBlock,
                enemyIncoming,
                enemyVuln,
                out FastTurnContext ctx);

            blockedActionBits.Clear();
            if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue)
            {
                int maxCheck = Math.Min(actionCount, 256);
                for (int a = 0; a < maxCheck; a++)
                {
                    HotAction act = actionBuf[a];
                    if (act.Kind != HotActionKind.PlayCard || act.SlotOrHandIdx >= state.HandCount)
                        continue;
                    ref readonly HotCard c = ref state.Cards[act.SlotOrHandIdx];
                    if (c.CardDefId < rules.Cards.Length
                        && rules.Cards[c.CardDefId].CardId == HotCustomCardId.BrightestFlame)
                    {
                        int flameCost = HotCombatEngine.GetNamedVarInt(in c, rules.Cards[c.CardDefId], "MaxHp", 2);
                        if (!policy.AllowsBrightestFlame(state.BrightestFlameMaxHpSpent + flameCost))
                            blockedActionBits[a >> 6] |= 1UL << (a & 63);
                    }
                }
            }

            bool stepSucceeded = false;

            while (true)
            {
                int chosenIdx;
                if (actionsThisTurn >= MaxActionsPerRolloutTurn)
                {
                    chosenIdx = FindUnblockedEndTurnIndex(actionBuf, actionCount, blockedActionBits);
                }
                else
                {
                    chosenIdx = SelectBestPilotAction(
                        in state,
                        cold,
                        rules,
                        in policy,
                        in evalLookup,
                        actionBuf,
                        actionCount,
                        blockedActionBits,
                        in ctx,
                        enemyHp,
                        enemyBlock,
                        enemyIncoming,
                        enemyVuln,
                        pilot);
                }

                if (chosenIdx < 0)
                    return false;

                HotAction chosenAction = actionBuf[chosenIdx];
                bool skipPreStepCopy = CanSkipPreStepCopy(in state, chosenAction, rules, in ctx);

                int choiceCount = 0;
                bool resolved;
                HotColdSidecar? preStepCold = null;

                if (skipPreStepCopy)
                {
                    // RO-02: Pure bytecode non-choice card with no StratagemPower cannot trigger NeedsChoice;
                    // execute directly without copying the full CombatHotState into preStepState.
                    HotStepResult fastRes = HotCombatEngine.Step(
                        ref state,
                        ref cold,
                        rules,
                        scratch,
                        chosenAction,
                        default);
                    resolved = fastRes.Executed && !fastRes.NeedsChoice;
                }
                else
                {
                    preStepState = state;
                    preStepCold = cold;

                    resolved = ExecuteActionWithGreedyChoices(
                        ref state,
                        ref cold,
                        ref preStepState,
                        ref preStepCold,
                        rules,
                        evalLookup.CardRoles,
                        scratch,
                        chosenAction,
                        ctx.UnblockedIncoming,
                        ctx.HasRetainHand,
                        choiceOptBuf,
                        stepChoices,
                        out choiceCount);
                }

                if (resolved
                    && policy.BrightestFlameEffectiveMaxHpLimit.HasValue
                    && !policy.AllowsBrightestFlame(state.BrightestFlameMaxHpSpent))
                {
                    resolved = false;
                }

                if (resolved)
                {
                    totalStepsExecuted++;
                    actionsThisTurn++;

                    if (recordTrace && traceSteps != null && traceChoices != null)
                    {
                        if (outTraceSteps < 0
                            || outTraceSteps >= traceSteps.Length
                            || outTraceChoices + choiceCount > traceChoices.Length)
                        {
                            outTraceSteps = -1;
                        }
                        else
                        {
                            int savedChoiceOffset = -1;
                            if (choiceCount > 0)
                            {
                                savedChoiceOffset = outTraceChoices;
                                stepChoices[..choiceCount].CopyTo(traceChoices.AsSpan(savedChoiceOffset, choiceCount));
                                outTraceChoices += choiceCount;
                            }
                            traceSteps[outTraceSteps++] = new HotRolloutTraceStep
                            {
                                Action = chosenAction,
                                ChoiceCount = (byte)choiceCount,
                                ChoiceOffset = savedChoiceOffset,
                            };
                        }
                    }

                    stepSucceeded = true;

                    if (state.TerminalOutcome != HotCombatOutcome.Ongoing || !HotCombatEngine.IsInProgress(in state))
                        return false;

                    if (chosenAction.Kind == HotActionKind.EndTurn || state.PlayerTurnNumber > startTurn)
                        return true;

                    break;
                }

                // If a fast-path step ever unexpectedly failed without a pre-step snapshot, abort cleanly.
                if (skipPreStepCopy)
                    return false;

                // Failed to execute or resolve choices: restore pre-step state and block this action index.
                state = preStepState;
                cold = preStepCold;
                if ((uint)chosenIdx < 256u)
                    blockedActionBits[chosenIdx >> 6] |= 1UL << (chosenIdx & 63);
                else
                    return false;

                if (chosenAction.Kind == HotActionKind.EndTurn)
                    return false;
            }

            if (!stepSucceeded)
                return false;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanSkipPreStepCopy(
        in CombatHotState s,
        HotAction action,
        HotRules rules,
        in FastTurnContext ctx)
    {
        if (action.Kind != HotActionKind.PlayCard || action.SlotOrHandIdx >= s.HandCount || ctx.HasStratagemPower)
            return false;
        ref readonly HotCard card = ref s.Cards[action.SlotOrHandIdx];
        if (card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return false;
        HotCardDef def = rules.Cards[card.CardDefId];
        if (def.OpCount == 0
            || def.CustomHandlerId != 0
            || def.CanGenerateUnknown
            || def.CardId == HotCustomCardId.BrightestFlame)
        {
            return false;
        }
        ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(def.DefId);
        for (int k = 0; k < ops.Length; k++)
        {
            HotCardOpKind op = ops[k].Op;
            if (op == HotCardOpKind.Custom || op == HotCardOpKind.DiscardHand)
                return false;
        }
        return true;
    }

    private static bool ExecuteActionWithGreedyChoices(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        ref CombatHotState preStepState,
        ref HotColdSidecar? preStepCold,
        HotRules rules,
        HotCardRoleFlags[]? cardRoles,
        HotExecScratch scratch,
        HotAction action,
        int unblockedIncoming,
        bool hasRetainHand,
        HotChoiceSelection[] choiceOptBuf,
        Span<HotChoiceSelection> stepChoices,
        out int choiceCount)
    {
        choiceCount = 0;

        HotStepResult res = HotCombatEngine.Step(ref state, ref cold, rules, scratch, action, default);
        if (!res.Executed)
            return false;

        if (res.NeedsChoice)
        {
            // RO-02: Lazily canonicalize + assign deterministic CardUids on the pre-step snapshot
            // right before choice enumeration/picking so UID tie-breaks (DOP=1 == DOP=N) and
            // recorded HotChoiceSelection UIDs match TryPromoteRolloutVictory / HotPlanMaterializer
            // bit-for-bit without paying canonicalization on non-choice steps.
            state = preStepState;
            cold = preStepCold;
            HotStatePacker.CanonicalizeForPack(ref state, ref cold);
            HotParallelExpander.AssignDeterministicCardUids(ref state);
            preStepState = state;
            preStepCold = cold;

            res = HotCombatEngine.Step(ref state, ref cold, rules, scratch, action, default);
            if (!res.Executed)
                return false;
        }

        while (res.NeedsChoice)
        {
            if (choiceCount >= stepChoices.Length || choiceCount >= HotLimits.MaxChoicesPerAction)
                return false;

            int optCount = HotCombatEngine.EnumerateChoiceOptions(
                in state,
                cold,
                rules,
                scratch,
                choiceOptBuf);

            int choiceUnblockedIncoming = state.PlayerTurnNumber != preStepState.PlayerTurnNumber
                ? 0
                : unblockedIncoming;

            if (!TryPickDeterministicGreedyChoice(
                    in state,
                    cold,
                    rules,
                    cardRoles,
                    scratch.PendingChoiceKind,
                    scratch.PendingChoiceSourceCardDefId,
                    scratch.PendingChoiceMin,
                    scratch.PendingChoiceMax,
                    choiceUnblockedIncoming,
                    hasRetainHand,
                    choiceOptBuf,
                    optCount,
                    stepChoices[..choiceCount],
                    out HotChoiceSelection picked))
            {
                return false;
            }

            stepChoices[choiceCount++] = picked;

            state = preStepState;
            cold = preStepCold;
            res = HotCombatEngine.Step(
                ref state,
                ref cold,
                rules,
                scratch,
                action,
                stepChoices[..choiceCount]);

            if (!res.Executed)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Fast, zero-allocation, 100% domain-agnostic deterministic choice picker inside rollouts.
    /// Avoids re-picking the same card UID within a single action's multi-pick sequence and cleanly
    /// terminates optional (<c>minCount == 0</c>) prompts so rollouts never stall on Scry/Discard/Exhaust/Select.
    /// </summary>
    private static bool TryPickDeterministicGreedyChoice(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotCardRoleFlags[]? cardRoles,
        HotChoiceKind pendingKind,
        ushort sourceCardDefId,
        byte minCount,
        byte maxCount,
        int unblockedIncoming,
        bool hasRetainHand,
        HotChoiceSelection[] optBuf,
        int optCount,
        ReadOnlySpan<HotChoiceSelection> alreadyPicked,
        out HotChoiceSelection picked)
    {
        picked = default;
        if (optCount <= 0)
        {
            if (minCount == 0)
            {
                picked = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                return true;
            }
            return false;
        }

        bool hasNoneOption = minCount == 0;
        for (int i = 0; i < optCount; i++)
        {
            if (optBuf[i].Kind == HotChoiceKind.None)
            {
                hasNoneOption = true;
                break;
            }
        }

        // RO-01: Beneficial HandCard prompts (Retain, Setup, Cost-reduce, Copy, Upgrade, Forge, Replay)
        // classified via card + HotCardDef metadata pick highest quality rather than lowest.
        bool isUpgradePrompt = false;
        bool isBeneficialHandChoice = pendingKind == HotChoiceKind.HandCard
            && ShouldPreferHighestHandChoice(sourceCardDefId, rules, minCount, out isUpgradePrompt);

        // Destructive DrawPile prompts (Charge, Cleanse, Seance) transform/exhaust draw-pile cards and
        // MUST pick lowest quality (Curses/Statuses/Strikes) rather than best Rare/Power cards!
        bool isDestructiveDrawChoice = pendingKind == HotChoiceKind.DrawPile
            && ShouldPreferLowestDrawChoice(sourceCardDefId, rules);

        bool isGuardsHandChoice = pendingKind == HotChoiceKind.HandCard
            && sourceCardDefId != 0
            && sourceCardDefId < rules.Cards.Length
            && rules.Cards[sourceCardDefId].CardId == HotCustomCardId.Guards;

        // RO-01: Allow optional multi-pick prompts (discard/exhaust/scry/thin/Guards) to pick multiple
        // eligible candidates up to a sensible cap (<= 5 or maxCount) instead of breaking after 1 pick.
        // GeneratedCard options do not deduplicate by UID, so still stop after 1 pick for optional GeneratedCard.
        int maxOptionalCap = Math.Min(5, HotLimits.MaxChoicesPerAction - 1);
        int optionalPickLimit = maxCount > 1 ? Math.Min((int)maxCount, maxOptionalCap) : maxOptionalCap;
        if (isGuardsHandChoice)
            optionalPickLimit = Math.Min(4, optionalPickLimit);

        if (hasNoneOption
            && alreadyPicked.Length > 0
            && !isBeneficialHandChoice
            && (pendingKind == HotChoiceKind.GeneratedCard || alreadyPicked.Length >= optionalPickLimit))
        {
            picked = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            return true;
        }

        // HandCard selections without a beneficial source card, plus destructive DrawPile prompts
        // (Charge, Cleanse, Seance), prefer lowest quality first; other Draw/Discard/Exhaust/Generated
        // and beneficial HandCard prompts prefer highest quality first.
        bool preferLowestQuality = (pendingKind == HotChoiceKind.HandCard && !isBeneficialHandChoice)
            || isDestructiveDrawChoice;

        // Next-turn / future-pile choices (CosmicIndifference / Headbutt putting DiscardPile cards on Draw[0],
        // destructive DrawPile thinning like Charge/Cleanse/Seance, and beneficial HandCard prompts) do not depend
        // on current-turn remaining energy.
        bool isNextTurnPileChoice = isDestructiveDrawChoice
            || (pendingKind == HotChoiceKind.DiscardPile
                && sourceCardDefId != 0
                && sourceCardDefId < rules.Cards.Length
                && rules.Cards[sourceCardDefId].CardId is HotCustomCardId.CosmicIndifference or HotCustomCardId.Headbutt);

        // Guards transforms low-value hand cards into 0-cost 9-Block MinionSacrifice, so basic Defends
        // remain eligible to upgrade when under attack instead of getting inflated above maxAllowedQuality.
        int evalUnblockedIncoming = isGuardsHandChoice ? 0 : unblockedIncoming;
        int evalAvailableEnergy = (isBeneficialHandChoice || isNextTurnPileChoice) ? -1 : s.Energy;

        int bestIdx = -1;
        int bestQuality = preferLowestQuality ? int.MaxValue : int.MinValue;
        ushort bestUid = ushort.MaxValue;

        for (int i = 0; i < optCount; i++)
        {
            HotChoiceSelection opt = optBuf[i];
            if (opt.Kind == HotChoiceKind.None)
                continue;

            if (opt.Kind != HotChoiceKind.GeneratedCard && IsUidAlreadyPicked(alreadyPicked, opt.Kind, opt.CardUidOrIndex))
                continue;

            // Never transform SovereignBlade (especially forged blades) via optional Guards prompts.
            if (hasNoneOption
                && isGuardsHandChoice
                && opt.CardDefId < rules.Cards.Length
                && rules.Cards[opt.CardDefId].CardId == HotCustomCardId.SovereignBlade)
            {
                continue;
            }

            int q = ComputeGenericChoiceCardQuality(
                in s,
                cold,
                rules,
                cardRoles,
                opt,
                evalUnblockedIncoming,
                evalAvailableEnergy,
                isUpgradePrompt,
                hasRetainHand);

            bool better;
            if (bestIdx < 0)
            {
                better = true;
            }
            else if (preferLowestQuality)
            {
                better = q < bestQuality || (q == bestQuality && opt.CardUidOrIndex < bestUid);
            }
            else
            {
                better = q > bestQuality || (q == bestQuality && opt.CardUidOrIndex < bestUid);
            }

            if (better)
            {
                bestIdx = i;
                bestQuality = q;
                bestUid = opt.CardUidOrIndex;
            }
        }

        if (bestIdx < 0)
        {
            if (hasNoneOption)
            {
                picked = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                return true;
            }
            return false;
        }

        // RO-01: For optional lowest-quality prompts (GamblingChip / Purity / optional scry & thinning),
        // allow discarding/scrying/exhausting Basic Strikes/Defends (quality == 60) when player has
        // non-lethal safety / spare block (affordable Defends under threat are already boosted > 75).
        // For Guards under unblocked incoming damage, allow transforming cards <= 80 into MinionSacrifice.
        if (hasNoneOption && preferLowestQuality)
        {
            bool nonLethalSafe = unblockedIncoming == 0
                || (unblockedIncoming < s.Player.CurrentHp
                    && s.Player.CurrentHp - unblockedIncoming > Math.Max(4, s.Player.MaxHp / 5));
            int maxAllowedQuality;
            if (isGuardsHandChoice)
                maxAllowedQuality = unblockedIncoming > 0 ? 80 : -1;
            else if (nonLethalSafe)
                maxAllowedQuality = 75;
            else if (unblockedIncoming < s.Player.CurrentHp)
                maxAllowedQuality = 65;
            else
                maxAllowedQuality = -1;

            if (bestQuality > maxAllowedQuality)
            {
                picked = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                return true;
            }
        }

        // For optional non-Hand prompts, pass if best available card is negative clutter,
        // or skip low-value optional GeneratedCard picks when hand is nearly full (>= 9) to avoid 10-card overflow.
        if (hasNoneOption && !preferLowestQuality)
        {
            if (bestQuality < 0
                || (pendingKind == HotChoiceKind.GeneratedCard && s.HandCount >= 9 && bestQuality < 250))
            {
                picked = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                return true;
            }
        }

        picked = optBuf[bestIdx];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUidAlreadyPicked(
        ReadOnlySpan<HotChoiceSelection> alreadyPicked,
        HotChoiceKind kind,
        ushort uid)
    {
        for (int i = 0; i < alreadyPicked.Length; i++)
        {
            if (alreadyPicked[i].Kind == kind && alreadyPicked[i].CardUidOrIndex == uid)
                return true;
        }
        return false;
    }

    private static int ComputeGenericChoiceCardQuality(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotCardRoleFlags[]? cardRoles,
        HotChoiceSelection opt,
        int unblockedIncoming,
        int availableEnergy,
        bool isUpgradePrompt,
        bool hasRetainHand)
    {
        if (opt.CardDefId == 0 || opt.CardDefId >= rules.Cards.Length)
            return 0;

        HotCardDef def = rules.Cards[opt.CardDefId];
        if (opt.Kind is HotChoiceKind.HandCard or HotChoiceKind.DrawPile or HotChoiceKind.DiscardPile or HotChoiceKind.ExhaustPile)
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
                return ComputeCardInstanceQuality(
                    in s,
                    in s.Cards[idx],
                    def,
                    cold,
                    rules,
                    unblockedIncoming,
                    availableEnergy,
                    isUpgradePrompt,
                    cardRoles,
                    hasRetainHand);
            }
        }

        int baseQ = ComputeCardBaseQuality(def, rules);
        if (def.Type is not (CardType.Curse or CardType.Status))
        {
            int genCost = Math.Max(0, (int)def.BaseCost);
            bool affordableNow = availableEnergy < 0 || genCost <= availableEnergy;
            if (unblockedIncoming > 0 && (def.GainsBlock || def.IsDefendTag) && affordableNow)
            {
                int rawBlock = def.BaseBlock > 0 ? def.BaseBlock : 5;
                int usefulBlock = Math.Min(rawBlock, unblockedIncoming);
                bool lethal = unblockedIncoming >= s.Player.CurrentHp;
                bool heavyThreat = lethal
                    || unblockedIncoming >= 12
                    || (unblockedIncoming * 4 >= Math.Max(1, s.Player.CurrentHp));
                if (lethal)
                    baseQ += 220 + Math.Min(usefulBlock * 14, 180);
                else if (heavyThreat)
                    baseQ += 170 + Math.Min(usefulBlock * 10, 130);
                else
                    baseQ += 70 + Math.Min(usefulBlock * 8, 80);
            }
            bool isRetained = hasRetainHand || (((HotCardKeywordMask)def.BaseKeywords & HotCardKeywordMask.Retain) != 0);
            bool hasDraw = (cardRoles != null && (uint)def.DefId < (uint)cardRoles.Length
                    && (cardRoles[def.DefId] & HotCardRoleFlags.HasDraw) != 0)
                || def.CardId == HotCustomCardId.CosmicIndifference;
            if (availableEnergy >= 0 && genCost > availableEnergy && !isRetained && !hasDraw)
            {
                int prePenalty = baseQ;
                bool urgentThreat = unblockedIncoming > 0
                    && (unblockedIncoming >= s.Player.CurrentHp
                        || unblockedIncoming >= 12
                        || (unblockedIncoming * 4 >= Math.Max(1, s.Player.CurrentHp)));
                baseQ -= urgentThreat ? 25 : 15;
                int floor = (!isUpgradePrompt && prePenalty > 80) ? 85 : 10;
                if (!isUpgradePrompt && baseQ < floor)
                    baseQ = floor;
            }
        }
        return baseQ;
    }

    private static bool IsDestructiveHandChoice(HotCardDef def, HotRules rules)
    {
        if (def.CardId is
            HotCustomCardId.Acrobatics
            or HotCustomCardId.DaggerThrow
            or HotCustomCardId.HiddenDaggers
            or HotCustomCardId.Prepared
            or HotCustomCardId.Survivor
            or HotCustomCardId.Brand
            or HotCustomCardId.BurningPact
            or HotCustomCardId.TrueGrit
            or HotCustomCardId.Scavenge
            or HotCustomCardId.Purity
            or HotCustomCardId.Begone
            or HotCustomCardId.Guards
            or HotCustomCardId.TheHermit
            or HotCustomCardId.SculptingStrike
            or HotCustomCardId.ThinkingAhead
            or HotCustomCardId.Glimmer
            or HotCustomCardId.PhotonCut)
        {
            return true;
        }

        if (def.OpCount > 0)
        {
            ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(def.DefId);
            for (int k = 0; k < ops.Length; k++)
            {
                if (ops[k].Op == HotCardOpKind.DiscardHand)
                    return true;
            }
        }

        return false;
    }

    private static bool ShouldPreferHighestHandChoice(
        ushort sourceCardDefId,
        HotRules rules,
        byte minCount,
        out bool isUpgradePrompt)
    {
        isUpgradePrompt = false;
        if (sourceCardDefId == 0 || sourceCardDefId >= rules.Cards.Length)
            return false;

        HotCardDef def = rules.Cards[sourceCardDefId];

        switch (def.CardId)
        {
            case HotCustomCardId.Armaments:
                isUpgradePrompt = true;
                return true;
            case HotCustomCardId.Nightmare:
            case HotCustomCardId.DualWield:
            case HotCustomCardId.DecisionsDecisions:
            case HotCustomCardId.HeirloomHammer:
            case HotCustomCardId.Transfigure:
            case HotCustomCardId.HandTrick:
            case HotCustomCardId.Snap:
                return true;
        }

        if (IsDestructiveHandChoice(def, rules))
            return false;

        // Metadata-driven classification for unlisted beneficial HandCard prompts
        // (Retain, Setup, Cost-reduce, Copy, Upgrade, Forge) via HotCardDef ops/entry/vars.
        if (def.OpCount > 0)
        {
            ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(def.DefId);
            for (int k = 0; k < ops.Length; k++)
            {
                if (ops[k].Op == HotCardOpKind.Forge)
                    return true;
            }
        }

        string entry = def.Entry;
        if (!string.IsNullOrEmpty(entry))
        {
            if (entry.Contains("UPGRADE", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("ARMAMENT", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("APOTHEOSIS", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("SMITH", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("TEMPER", StringComparison.OrdinalIgnoreCase))
            {
                isUpgradePrompt = true;
                return true;
            }
            if (entry.Contains("RETAIN", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("SETUP", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("FORETHOUGHT", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("COPY", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("CLONE", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("FORGE", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("ENLIGHTEN", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("DISCOUNT", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("REDUCE", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("MIRROR", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("REPEAT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string[] varNames = def.OrderedVarNames;
        if (varNames != null)
        {
            for (int i = 0; i < varNames.Length; i++)
            {
                string? vn = varNames[i];
                if (string.IsNullOrEmpty(vn))
                    continue;
                if (vn.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    isUpgradePrompt = true;
                    return true;
                }
                if (vn.Contains("Repeat", StringComparison.OrdinalIgnoreCase)
                    || vn.Contains("Copy", StringComparison.OrdinalIgnoreCase)
                    || vn.Contains("Copies", StringComparison.OrdinalIgnoreCase)
                    || vn.Contains("Forge", StringComparison.OrdinalIgnoreCase)
                    || vn.Contains("Retain", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return minCount > 0 && (def.Type == CardType.Power || def.SetupWeight > 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldPreferLowestDrawChoice(ushort sourceCardDefId, HotRules rules)
    {
        if (sourceCardDefId == 0 || sourceCardDefId >= rules.Cards.Length)
            return false;
        return rules.Cards[sourceCardDefId].CardId is
            HotCustomCardId.Charge
            or HotCustomCardId.Cleanse
            or HotCustomCardId.Seance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetEffectiveCardSetupWeight(HotCardDef def, HotRules rules)
    {
        int sw = def.SetupWeight;
        if (sw > 0)
            return sw;
        if (def.OpCount > 0)
        {
            ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(def.DefId);
            for (int k = 0; k < ops.Length; k++)
            {
                if (ops[k].Op == HotCardOpKind.ApplyPowerSelf)
                {
                    ushort pDefId = (ushort)ops[k].Arg0;
                    if (pDefId < rules.Powers.Length && rules.Powers[pDefId].SetupWeight > sw)
                        sw = rules.Powers[pDefId].SetupWeight;
                }
            }
        }
        return sw;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeCardBaseQuality(HotCardDef def, HotRules rules)
    {
        if (def.Type is CardType.Curse or CardType.Status)
            return -400;
        if (def.IsStrikeTag || def.IsDefendTag)
            return 60;

        int quality = def.Type switch
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
        quality += Math.Min(GetEffectiveCardSetupWeight(def, rules) * 4, 140);
        if (def.BaseCost == 0)
            quality += 40;
        else if (def.BaseCost == 1)
            quality += 15;
        return quality;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeCardInstanceQuality(
        in CombatHotState s,
        in HotCard c,
        HotCardDef def,
        HotColdSidecar? cold,
        HotRules rules,
        int unblockedIncoming = 0,
        int availableEnergy = -1,
        bool isUpgradePrompt = false,
        HotCardRoleFlags[]? cardRoles = null,
        bool hasRetainHand = false)
    {
        if ((c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
            return -450;

        int quality = ComputeCardBaseQuality(def, rules);
        if (def.CardId == HotCustomCardId.SovereignBlade)
        {
            int forgedDelta = Math.Max((int)c.Var0Delta, c.CardPrivate);
            if (forgedDelta > 0)
                quality += Math.Min(forgedDelta * 8, 240);
        }

        if (isUpgradePrompt)
        {
            // RO-01: Suppress the +35 already-upgraded bonus during upgrade prompts and strongly prefer
            // cards that are not yet maxed and gain real cost/value improvements from upgrading.
            if (c.UpgradeLevel >= def.MaxUpgradeLevel)
            {
                quality -= 300;
            }
            else
            {
                if (def.BaseCost > 0 && def.UpgradedCost >= 0 && def.UpgradedCost < def.BaseCost)
                    quality += (def.BaseCost - def.UpgradedCost) * 70;
                int dmgGain = Math.Max(
                    0,
                    def.UpgradedDamage * Math.Max(1, (int)def.UpgradedHits)
                        - def.BaseDamage * Math.Max(1, (int)def.BaseHits));
                int blkGain = Math.Max(0, def.UpgradedBlock - def.BaseBlock);
                int magGain = Math.Max(0, def.UpgradedMagic - def.BaseMagic);
                quality += Math.Min(dmgGain * 4 + blkGain * 5 + magGain * 10, 90);
            }
        }
        else if (c.UpgradeLevel > 0)
        {
            quality += 35;
        }

        bool isCostsX = (c.Flags & HotCardFlags.CostsX) != 0;
        int effCost = isCostsX
            ? 0
            : Math.Max(0, HotCombatEngine.GetEffectiveEnergyCost(in s, in c, cold, rules, isInPlayablePile: true));

        if (!isCostsX && (effCost == 0 || HotCostModifiers.GetLocalEnergyCost(in c, cold) == 0))
            quality += 30;

        if (def.Type is not (CardType.Curse or CardType.Status))
        {
            bool affordableNow = availableEnergy < 0 || effCost <= availableEnergy;

            // RO-01: Context-aware block urgency under unblocked incoming attack so Defend (60)
            // outranks generic 190/200 non-block picks when affordable and facing heavy threat.
            if (unblockedIncoming > 0 && (def.GainsBlock || def.IsDefendTag) && affordableNow)
            {
                int rawBlock = def.GetBlock(c.UpgradeLevel, c.Var1Delta);
                if (rawBlock <= 0)
                    rawBlock = 5;
                int usefulBlock = Math.Min(rawBlock, unblockedIncoming);
                bool lethal = unblockedIncoming >= s.Player.CurrentHp;
                bool heavyThreat = lethal
                    || unblockedIncoming >= 12
                    || (unblockedIncoming * 4 >= Math.Max(1, s.Player.CurrentHp));
                if (lethal)
                    quality += 220 + Math.Min(usefulBlock * 14, 180);
                else if (heavyThreat)
                    quality += 170 + Math.Min(usefulBlock * 10, 130);
                else
                    quality += 70 + Math.Min(usefulBlock * 8, 80);
            }

            bool isRetained = hasRetainHand
                || HotCombatEngine.HasEffectiveRetainKeyword(in s, in c, rules)
                || (c.Flags & HotCardFlags.SingleTurnRetain) != 0;
            bool hasDraw = (cardRoles != null && (uint)def.DefId < (uint)cardRoles.Length
                    && (cardRoles[def.DefId] & HotCardRoleFlags.HasDraw) != 0)
                || def.CardId == HotCustomCardId.CosmicIndifference;

            if (availableEnergy >= 0 && effCost > availableEnergy && !isRetained && !hasDraw)
            {
                int prePenalty = quality;
                bool urgentThreat = unblockedIncoming > 0
                    && (unblockedIncoming >= s.Player.CurrentHp
                        || unblockedIncoming >= 12
                        || (unblockedIncoming * 4 >= Math.Max(1, s.Player.CurrentHp)));
                quality -= urgentThreat ? 25 : 15;
                int floor = (!isUpgradePrompt && prePenalty > 80) ? 85 : 10;
                if (!isUpgradePrompt && quality < floor)
                    quality = floor;
            }
        }

        return quality;
    }

    private static bool ShouldHoldOrSkipCopyCard(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        int copyCardHandIdx,
        bool attackOrPowerOnly,
        bool canRetain,
        bool lethalThreat)
    {
        int bestHandQuality = int.MinValue;
        int bestHandSetup = 0;

        for (int h = 0; h < s.HandCount; h++)
        {
            if (h == copyCardHandIdx)
                continue;
            ref readonly HotCard c = ref s.Cards[h];
            HotCardDef d = rules.Cards[c.CardDefId];
            if (attackOrPowerOnly && d.Type is not (CardType.Attack or CardType.Power))
                continue;

            int q = ComputeCardInstanceQuality(in s, in c, d, cold, rules);
            int sw = GetEffectiveCardSetupWeight(d, rules);
            if (q > bestHandQuality)
                bestHandQuality = q;
            if (sw > bestHandSetup)
                bestHandSetup = sw;
        }

        // Zero-payoff guard: no eligible target in Hand at all.
        if (bestHandQuality == int.MinValue)
            return true;

        if (!canRetain || lethalThreat || s.DrawCount == 0)
            return false;

        int bestDrawQuality = int.MinValue;
        int bestDrawSetup = 0;
        int drawEnd = s.DrawStart + s.DrawCount;
        for (int dIdx = s.DrawStart; dIdx < drawEnd; dIdx++)
        {
            ref readonly HotCard c = ref s.Cards[dIdx];
            HotCardDef d = rules.Cards[c.CardDefId];
            if (attackOrPowerOnly && d.Type is not (CardType.Attack or CardType.Power))
                continue;

            int q = ComputeCardInstanceQuality(in s, in c, d, cold, rules);
            int sw = GetEffectiveCardSetupWeight(d, rules);
            if (q > bestDrawQuality)
                bestDrawQuality = q;
            if (sw > bestDrawSetup)
                bestDrawSetup = sw;
        }

        if (bestDrawQuality == int.MinValue)
            return false;
        if (bestHandQuality < 0 && bestDrawQuality >= 0)
            return true;
        if (bestHandSetup < 18 && bestDrawSetup >= 18)
            return true;
        if (bestDrawSetup >= bestHandSetup + 15 && bestDrawSetup >= 24)
            return true;
        return bestHandQuality < 280 && bestDrawQuality >= bestHandQuality + 120;
    }

    private static int FindUnblockedEndTurnIndex(
        HotAction[] actionBuf,
        int actionCount,
        ReadOnlySpan<ulong> blockedBits)
    {
        for (int i = 0; i < actionCount; i++)
        {
            if (actionBuf[i].Kind != HotActionKind.EndTurn)
                continue;
            if ((uint)i < 256u && (blockedBits[i >> 6] & (1UL << (i & 63))) != 0)
                continue;
            return i;
        }
        return -1;
    }

    private static void ComputeFastTurnContext(
        in CombatHotState s,
        HotRules rules,
        in HotEvalLookup lookup,
        Span<int> enemyHp,
        Span<int> enemyBlock,
        Span<int> enemyIncoming,
        Span<byte> enemyVuln,
        out FastTurnContext ctx)
    {
        enemyHp.Clear();
        enemyBlock.Clear();
        enemyIncoming.Clear();
        enemyVuln.Clear();

        int playerStr = 0;
        int playerDex = 0;
        int playerVuln = 0;
        int playerWeak = 0;
        int playerFrail = 0;
        int playerIntangible = 0;
        int playerAccelerant = 0;
        int playerEndTurnBlock = 0;
        bool hasActiveScaling = false;
        bool hasRetainHand = lookup.HasRunicPyramid;
        bool preservesExcessBlock = false;
        ushort stratagemDefId = rules.GetPowerDefId(HotPowerId.StratagemPower);
        bool hasStratagemPower = false;

        Span<int> enemyStr = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyWeak = stackalloc byte[HotLimits.MaxEnemies];
        Span<int> enemyPoison = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyHasDamageCap = stackalloc byte[HotLimits.MaxEnemies];
        Span<byte> enemyHasReattach = stackalloc byte[HotLimits.MaxEnemies];

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0 || p.PowerDefId == 0)
                continue;

            ushort defId = p.PowerDefId;
            if (p.OwnerSlot == 0)
            {
                if (defId == lookup.StrengthDefId) playerStr += p.Amount;
                else if (defId == lookup.DexterityDefId) playerDex += p.Amount;
                else if (defId == lookup.VulnerableDefId) playerVuln += p.Amount;
                else if (defId == lookup.WeakDefId) playerWeak += p.Amount;
                else if (defId == lookup.FrailDefId) playerFrail += p.Amount;
                else if (defId == lookup.IntangibleDefId) playerIntangible += p.Amount;
                else if (defId == lookup.AccelerantDefId) playerAccelerant += p.Amount;
                else if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) playerEndTurnBlock += p.Amount;
                else if (defId == lookup.BarricadeDefId || defId == lookup.BlurDefId) preservesExcessBlock = true;
                else if ((defId == lookup.RetainHandDefId || defId == lookup.WellLaidPlansDefId) && p.Amount > 0) hasRetainHand = true;

                if (stratagemDefId != 0 && defId == stratagemDefId && p.Amount > 0)
                    hasStratagemPower = true;

                if (p.Amount > 0 && defId < rules.Powers.Length && rules.Powers[defId].Type != MegaCrit.Sts2.Core.Entities.Powers.PowerType.Debuff)
                    hasActiveScaling = true;
            }
            else if (p.OwnerSlot >= 3)
            {
                int eSlot = p.OwnerSlot - 3;
                if ((uint)eSlot >= HotLimits.MaxEnemies)
                    continue;
                if (defId == lookup.ReattachDefId && p.Amount > 0)
                    enemyHasReattach[eSlot] = 1;
                else if (defId == lookup.StrengthDefId)
                    enemyStr[eSlot] += p.Amount;
                else if (defId == lookup.WeakDefId && p.Amount > 0)
                    enemyWeak[eSlot] = 1;
                else if (defId == lookup.VulnerableDefId && p.Amount > 0)
                    enemyVuln[eSlot] = 1;
                else if (defId == lookup.PoisonDefId && p.Amount > 0)
                    enemyPoison[eSlot] += p.Amount;
                else if (p.Amount > 0
                    && (defId == lookup.IntangibleDefId
                        || defId == lookup.BufferDefId
                        || defId == lookup.SlipperyDefId
                        || defId == lookup.HardToKillDefId
                        || defId == lookup.HardenedShellDefId))
                {
                    enemyHasDamageCap[eSlot] = 1;
                }
            }
        }

        int frostPassiveBlock = 0;
        int orbCount = s.OrbCount;
        for (int o = 0; o < orbCount; o++)
        {
            HotOrb orb = s.Orbs[o];
            HotOrbId orbId = HotCombatEngine.GetOrbId(rules, orb.OrbTypeId);
            if (orbId == HotOrbId.Frost)
            {
                int triggers = (o == 0 && lookup.HasGoldPlatedCables) ? 2 : 1;
                frostPassiveBlock += Math.Max(0, (int)orb.PassiveVal) * triggers;
            }
        }

        int totalIncoming = 0;
        int aliveCount = 0;
        int revivingCount = 0;
        int rosterCount = s.RosterCount;

        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
            {
                if (enemy.DeathPhase == HotDeathPhase.Reviving)
                    revivingCount++;
                continue;
            }

            aliveCount++;
            int hp = Math.Max(0, enemy.Vital.CurrentHp);
            enemyHp[eSlot] = hp;
            enemyBlock[eSlot] = Math.Max(0, enemy.Vital.Block);

            int poison = enemyPoison[eSlot];
            long poisonTickDmg = 0;
            if (poison > 0)
            {
                int ticks = Math.Min(poison, 1 + Math.Max(0, playerAccelerant));
                poisonTickDmg = ((long)ticks * (2L * poison - ticks + 1L)) / 2L;
            }
            bool poisonLethal = hp > 0
                && poisonTickDmg >= hp
                && enemyHasReattach[eSlot] == 0
                && enemyHasDamageCap[eSlot] == 0;

            if ((enemy.Flags & HotEnemyFlags.SkipNextMove) != 0 || poisonLethal)
                continue;

            int inc = EstimateEnemyIncomingDamageFast(
                in enemy,
                rules,
                enemyStr[eSlot],
                enemyWeak[eSlot] != 0,
                playerVuln > 0,
                playerIntangible > 0,
                in lookup);
            enemyIncoming[eSlot] = inc;
            totalIncoming += inc;
        }

        int defense = s.Player.Block + playerEndTurnBlock + frostPassiveBlock;
        if ((s.CombatFlags & HotCombatFlags.HasWuppy) != 0 && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
            defense += s.Wuppy.CurrentHp;
        if ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            defense += s.Osty.CurrentHp;

        ctx = new FastTurnContext
        {
            UnblockedIncoming = Math.Max(0, totalIncoming - defense),
            PlayerStrength = playerStr,
            PlayerDexterity = playerDex,
            AliveEnemies = aliveCount,
            RevivingEnemies = revivingCount,
            PlayerWeak = playerWeak > 0,
            PlayerFrail = playerFrail > 0,
            HasActiveScaling = hasActiveScaling || orbCount > 0,
            HasRetainHand = hasRetainHand,
            PreservesExcessBlock = preservesExcessBlock,
            HasStratagemPower = hasStratagemPower,
        };
    }

    private static int SelectBestPilotAction(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotAction[] actionBuf,
        int actionCount,
        ReadOnlySpan<ulong> blockedBits,
        in FastTurnContext ctx,
        ReadOnlySpan<int> enemyHp,
        ReadOnlySpan<int> enemyBlock,
        ReadOnlySpan<int> enemyIncoming,
        ReadOnlySpan<byte> enemyVuln,
        RolloutPilotKind pilot)
    {
        int bestIdx = -1;
        int bestPriority = int.MinValue;

        // Check whether any non-retained playable card exists (used by Pilot B to naturally defer
        // non-lethal retained scaling attacks without any card-ID hardcoding).
        bool hasNonRetainedCardOption = false;
        if (pilot == RolloutPilotKind.SetupFirst)
        {
            for (int i = 0; i < actionCount; i++)
            {
                if ((uint)i < 256u && (blockedBits[i >> 6] & (1UL << (i & 63))) != 0)
                    continue;
                HotAction act = actionBuf[i];
                if (act.Kind != HotActionKind.PlayCard || act.SlotOrHandIdx >= s.HandCount)
                    continue;
                ref readonly HotCard hc = ref s.Cards[act.SlotOrHandIdx];
                HotCardDef hd = rules.Cards[hc.CardDefId];
                if (hd.Type is CardType.Status or CardType.Curse)
                    continue;
                bool isRetained = HotCombatEngine.HasEffectiveRetainKeyword(in s, in hc, rules)
                    || (hc.Flags & HotCardFlags.SingleTurnRetain) != 0;
                if (!isRetained)
                {
                    hasNonRetainedCardOption = true;
                    break;
                }
            }
        }

        bool lethalThreat = ctx.UnblockedIncoming >= s.Player.CurrentHp;
        int missingHp = Math.Max(0, s.Player.MaxHp - s.Player.CurrentHp);

        RolloutStrategyContext strat = default;
        if (policy.HasAnyStrategyTargets)
        {
            BuildRolloutStrategyContext(
                in s,
                cold,
                rules,
                in policy,
                actionBuf,
                actionCount,
                blockedBits,
                in ctx,
                enemyHp,
                enemyBlock,
                enemyVuln,
                pilot,
                lethalThreat,
                out strat);
        }

        for (int i = 0; i < actionCount; i++)
        {
            if ((uint)i < 256u && (blockedBits[i >> 6] & (1UL << (i & 63))) != 0)
                continue;

            HotAction act = actionBuf[i];
            int prio;

            if (act.Kind == HotActionKind.TurnSetup)
            {
                prio = 2_000_000;
            }
            else if (act.Kind == HotActionKind.EndTurn)
            {
                prio = 0;
            }
            else if (act.Kind == HotActionKind.PlayCard && act.SlotOrHandIdx < s.HandCount)
            {
                prio = ScorePlayCardAction(
                    in s,
                    cold,
                    rules,
                    in policy,
                    in evalLookup,
                    act,
                    in ctx,
                    in strat,
                    enemyHp,
                    enemyBlock,
                    enemyIncoming,
                    enemyVuln,
                    pilot,
                    lethalThreat,
                    missingHp,
                    hasNonRetainedCardOption);
            }
            else
            {
                continue;
            }

            if (bestIdx < 0 || prio > bestPriority)
            {
                bestIdx = i;
                bestPriority = prio;
            }
        }

        return bestIdx;
    }

    private static void BuildRolloutStrategyContext(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy policy,
        HotAction[] actionBuf,
        int actionCount,
        ReadOnlySpan<ulong> blockedBits,
        in FastTurnContext ctx,
        ReadOnlySpan<int> enemyHp,
        ReadOnlySpan<int> enemyBlock,
        ReadOnlySpan<byte> enemyVuln,
        RolloutPilotKind pilot,
        bool lethalThreat,
        out RolloutStrategyContext strat)
    {
        strat = default;
        bool pureGrowthUnsatisfied = policy.HasPureGrowthTargets && !policy.AreBoundedGrowthTargetsSatisfied(in s);
        ulong unsatisfiedRelicMask = 0UL;
        int relicDistance = 0;
        if (policy.HasRelicTargets)
        {
            RelicCounterEvaluation reIfWin = policy.EvaluateRelicCounters(in s, won: true);
            unsatisfiedRelicMask = reIfWin.TargetMask & ~reIfWin.SatisfiedMask;
            relicDistance = reIfWin.Distance;
        }
        bool relicUnsatisfied = unsatisfiedRelicMask != 0UL;
        if (!pureGrowthUnsatisfied && !relicUnsatisfied)
            return;

        bool hasEmptyPotionSlot = pureGrowthUnsatisfied && HasEmptyPotionSlot(in s);
        bool hasAnyNonMinion = pureGrowthUnsatisfied && HasAnyNonMinionLivingEnemy(in s, rules);

        strat.Active = true;
        strat.PureGrowthUnsatisfied = pureGrowthUnsatisfied;
        strat.RelicUnsatisfied = relicUnsatisfied;
        strat.UnsatisfiedRelicMask = unsatisfiedRelicMask;
        strat.RelicDistance = relicDistance;
        strat.HasEmptyPotionSlot = hasEmptyPotionSlot;
        strat.HasAnyNonMinionEnemy = hasAnyNonMinion;

        // Check whether any unblocked playable attack can end combat right now.
        if (ctx.RevivingEnemies == 0 && ctx.AliveEnemies > 0)
        {
            for (int i = 0; i < actionCount; i++)
            {
                if ((uint)i < 256u && (blockedBits[i >> 6] & (1UL << (i & 63))) != 0)
                    continue;
                HotAction act = actionBuf[i];
                if (act.Kind != HotActionKind.PlayCard || act.SlotOrHandIdx >= s.HandCount)
                    continue;
                ref readonly HotCard c = ref s.Cards[act.SlotOrHandIdx];
                HotCardDef d = rules.Cards[c.CardDefId];
                if (d.Type != CardType.Attack)
                    continue;
                int baseDmg = d.GetDamage(c.UpgradeLevel, c.Var0Delta);
                if (baseDmg <= 0)
                    continue;
                int hits = d.GetHits(c.UpgradeLevel);
                int perHit = Math.Max(0, baseDmg + ctx.PlayerStrength);
                if (ctx.PlayerWeak)
                    perHit = (perHit * 75) / 100;
                TargetType et = HotCombatEngine.GetEffectiveCardTargetType(in s, in c, d, rules);
                int kills = 0;
                if (et == TargetType.AnyEnemy && (uint)act.TargetEnemySlot < HotLimits.MaxEnemies)
                {
                    int eSlot = act.TargetEnemySlot;
                    int scaled = enemyVuln[eSlot] != 0 ? (perHit * 150) / 100 : perHit;
                    if (enemyHp[eSlot] > 0 && scaled * hits >= enemyHp[eSlot] + enemyBlock[eSlot])
                        kills = 1;
                }
                else if (et == TargetType.AllEnemies)
                {
                    int rosterCount = s.RosterCount;
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte eSlot = s.ActiveEnemyRoster[r];
                        if (enemyHp[eSlot] <= 0)
                            continue;
                        int scaled = enemyVuln[eSlot] != 0 ? (perHit * 150) / 100 : perHit;
                        if (scaled * hits >= enemyHp[eSlot] + enemyBlock[eSlot])
                            kills++;
                    }
                }
                if (kills >= ctx.AliveEnemies)
                {
                    strat.HasLethalInHand = true;
                    break;
                }
            }
        }

        // Scan Hand + Draw (and Discard if Draw is low) for unplayed growth / counter-advancing cards & remaining HP budget.
        bool hasUnplayedGrowth = false;
        bool hasCounterOrTurnTick = (unsatisfiedRelicMask & TurnTickRelicMask) != 0UL;
        int growthBudgetSum = 0;
        int madScienceCount = s.Growth[9];

        int liveLimit = s.DrawCount < 5
            ? (s.DiscardStart + s.DiscardCount)
            : (s.DrawStart + s.DrawCount);
        for (int idx = 0; idx < liveLimit; idx++)
        {
            // Skip gap if any between Hand and Draw (none in packed layout: Hand[0..HandCount), Draw[DrawStart..), Discard[DiscardStart..))
            ref readonly HotCard c = ref s.Cards[idx];
            if (c.CardDefId == 0 || c.CardDefId >= rules.Cards.Length)
                continue;
            HotCardDef d = rules.Cards[c.CardDefId];
            if (d.Type is CardType.Status or CardType.Curse)
                continue;

            if (pureGrowthUnsatisfied)
            {
                int b = GetGrowthCardBudget(
                    in c, d, rules, in policy, hasEmptyPotionSlot, hasAnyNonMinion, madScienceCount,
                    out _, out _);
                if (b >= 0)
                {
                    hasUnplayedGrowth = true;
                    if (growthBudgetSum < 60)
                        growthBudgetSum += b;
                }
            }

            if (relicUnsatisfied && !hasCounterOrTurnTick)
            {
                bool exhaustsCard = (c.LocalKeywordsMask & HotCardKeywordMask.Exhaust) != 0
                    || (c.Flags & HotCardFlags.ExhaustOnNextPlay) != 0;
                int starCost = Math.Max(0, HotCombatEngine.GetEffectiveStarCost(in s, in c, cold, rules, isInPlayablePile: false));
                if (CardAdvancesUnsatisfiedRelic(d.Type, exhaustsCard, starCost, unsatisfiedRelicMask))
                    hasCounterOrTurnTick = true;
            }
        }

        int relicBudgetSum = 0;
        if (relicUnsatisfied && policy.RelicTargets != null)
        {
            HotRelicTargetEntry[] relicTargets = policy.RelicTargets;
            for (int i = 0; i < relicTargets.Length; i++)
            {
                HotRelicTargetEntry entry = relicTargets[i];
                if ((unsatisfiedRelicMask & (1UL << (int)entry.Id)) != 0UL && entry.HpAllowance > 0)
                    relicBudgetSum += entry.HpAllowance;
            }
        }

        int spentSoFar = Math.Max(0, policy.InitialPlayerHp - s.Player.CurrentHp);
        int remainingBudget = Math.Max(0, growthBudgetSum + relicBudgetSum - spentSoFar);
        strat.RemainingStrategyBudget = remainingBudget;

        if (pilot == RolloutPilotKind.SetupFirst
            && !lethalThreat
            && (hasUnplayedGrowth || hasCounterOrTurnTick))
        {
            int hpAfterHit = s.Player.CurrentHp - ctx.UnblockedIncoming;
            bool comfortablySafe = ctx.UnblockedIncoming == 0
                || (hpAfterHit > Math.Max(4, s.Player.MaxHp / 4) && ctx.UnblockedIncoming <= remainingBudget);
            if (comfortablySafe)
                strat.HoldLastEnemyLethal = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasEmptyPotionSlot(in CombatHotState s)
    {
        int slots = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        for (int i = 0; i < slots; i++)
        {
            if (s.Potions[i] == 0)
                return true;
        }
        return false;
    }

    private static bool HasAnyNonMinionLivingEnemy(in CombatHotState s, HotRules rules)
    {
        ushort minionDefId = rules.GetPowerDefId(HotPowerId.MinionPower);
        int rosterCount = s.RosterCount;
        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0 || enemy.Vital.CurrentHp <= 0)
                continue;
            if (minionDefId == 0)
                return true;
            byte creatureSlot = (byte)(3 + eSlot);
            bool isMinion = false;
            int pCount = s.PowerCount;
            for (int p = 0; p < pCount; p++)
            {
                ref readonly HotPower pw = ref s.Powers[p];
                if (pw.OwnerSlot == creatureSlot && pw.PowerDefId == minionDefId && pw.Amount > 0)
                {
                    isMinion = true;
                    break;
                }
            }
            if (!isMinion)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMadScienceImprovement(in HotCard card, HotCardDef def)
    {
        if (def.CardId != HotCustomCardId.MadScience)
            return false;
        CardType msType = (CardType)(card.CardPrivate & 0xFFFF);
        if (msType == CardType.None)
            msType = def.Type;
        int rider = (card.CardPrivate >> 16) & 0xFFFF;
        return msType == CardType.Power && rider == 9; // HotTinkerRiderEffect.Improvement == 9
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGoopyDeckBacked(in HotCard card, HotRules rules)
        => (card.Flags & HotCardFlags.HasDeckVersion) != 0
            && card.EnchId != 0
            && card.EnchId < rules.Enchantments.Length
            && rules.Enchantments[card.EnchId].EnchantmentId == HotEnchantmentId.Goopy;

    private static int GetGrowthCardBudget(
        in HotCard card,
        HotCardDef def,
        HotRules rules,
        in HotSearchPolicy policy,
        bool hasEmptyPotionSlot,
        bool hasAnyNonMinionEnemy,
        int madScienceCount,
        out bool isFatalFinisher,
        out bool isPlayableGrowth)
    {
        isFatalFinisher = false;
        isPlayableGrowth = false;
        GrowthValues b = policy.EffectiveGrowthBudgets;
        int budget = -1;

        switch (def.CardId)
        {
            case HotCustomCardId.HandOfGreed:
                if (hasAnyNonMinionEnemy) { isFatalFinisher = true; budget = Math.Max(budget, b.HandOfGreed); }
                break;
            case HotCustomCardId.TheHunt:
                if (hasAnyNonMinionEnemy) { isFatalFinisher = true; budget = Math.Max(budget, b.TheHunt); }
                break;
            case HotCustomCardId.Feed:
                if (hasAnyNonMinionEnemy) { isFatalFinisher = true; budget = Math.Max(budget, b.Feed); }
                break;
            case HotCustomCardId.Royalties:
                isPlayableGrowth = true;
                budget = Math.Max(budget, b.Royalties);
                break;
            case HotCustomCardId.Alchemize:
                if (!policy.HasSozu && hasEmptyPotionSlot)
                {
                    isPlayableGrowth = true;
                    budget = Math.Max(budget, b.Alchemize);
                }
                break;
            case HotCustomCardId.GeneticAlgorithm:
                if ((card.Flags & HotCardFlags.HasDeckVersion) != 0)
                {
                    isPlayableGrowth = true;
                    budget = Math.Max(budget, b.GeneticAlgorithm);
                }
                break;
            case HotCustomCardId.TheScythe:
                if ((card.Flags & HotCardFlags.HasDeckVersion) != 0)
                {
                    isPlayableGrowth = true;
                    budget = Math.Max(budget, b.TheScythe);
                }
                break;
            case HotCustomCardId.ForbiddenGrimoire:
                isPlayableGrowth = true;
                budget = Math.Max(budget, b.ForbiddenGrimoire);
                break;
            case HotCustomCardId.MadScience:
                if (IsMadScienceImprovement(in card, def) && madScienceCount < rules.MadScienceUpgradeCapacity)
                {
                    isPlayableGrowth = true;
                    budget = Math.Max(budget, b.MadScience);
                }
                break;
            case HotCustomCardId.ShowOff:
                if (hasAnyNonMinionEnemy)
                {
                    isPlayableGrowth = true;
                    budget = Math.Max(budget, b.ShowOff);
                }
                break;
        }

        if (IsGoopyDeckBacked(in card, rules))
        {
            isPlayableGrowth = true;
            budget = Math.Max(budget, b.Goopy);
        }

        return budget;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CardAdvancesUnsatisfiedRelic(
        CardType cardType,
        bool exhausts,
        int starCost,
        ulong unsatisfiedRelicMask)
    {
        if ((unsatisfiedRelicMask & CardRelicMask) != 0UL)
            return true;
        if ((unsatisfiedRelicMask & AttackRelicMask) != 0UL && cardType == CardType.Attack)
            return true;
        if ((unsatisfiedRelicMask & SkillRelicMask) != 0UL && cardType == CardType.Skill)
            return true;
        if ((unsatisfiedRelicMask & ExhaustRelicMask) != 0UL && exhausts)
            return true;
        if ((unsatisfiedRelicMask & StarRelicMask) != 0UL && starCost > 0)
            return true;
        return false;
    }

    private static bool HasFatalEligibleKillTarget(
        in CombatHotState s,
        HotRules rules,
        TargetType effTarget,
        int targetSlot,
        ReadOnlySpan<int> enemyHp,
        ReadOnlySpan<int> enemyBlock,
        ReadOnlySpan<byte> enemyVuln,
        int perHit,
        int hits)
    {
        if (effTarget == TargetType.AnyEnemy)
        {
            return (uint)targetSlot < HotLimits.MaxEnemies
                && enemyHp[targetSlot] > 0
                && HotCombatEngine.ShouldEnemyDeathTriggerFatal(in s, rules, targetSlot);
        }
        if (effTarget == TargetType.AllEnemies)
        {
            int rosterCount = s.RosterCount;
            for (int r = 0; r < rosterCount; r++)
            {
                byte eSlot = s.ActiveEnemyRoster[r];
                if (enemyHp[eSlot] <= 0)
                    continue;
                int scaled = enemyVuln[eSlot] != 0 ? (perHit * 150) / 100 : perHit;
                if (scaled * hits >= enemyHp[eSlot] + enemyBlock[eSlot]
                    && HotCombatEngine.ShouldEnemyDeathTriggerFatal(in s, rules, eSlot))
                {
                    return true;
                }
            }
            return false;
        }
        return HasAnyNonMinionLivingEnemy(in s, rules);
    }

    private static int ScorePlayCardAction(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotAction act,
        in FastTurnContext ctx,
        in RolloutStrategyContext strat,
        ReadOnlySpan<int> enemyHp,
        ReadOnlySpan<int> enemyBlock,
        ReadOnlySpan<int> enemyIncoming,
        ReadOnlySpan<byte> enemyVuln,
        RolloutPilotKind pilot,
        bool lethalThreat,
        int missingHp,
        bool hasNonRetainedCardOption)
    {
        ref readonly HotCard card = ref s.Cards[act.SlotOrHandIdx];
        HotCardDef def = rules.Cards[card.CardDefId];

        int energyCost = Math.Max(0, HotCombatEngine.GetEffectiveEnergyCost(in s, in card, cold, rules, isInPlayablePile: true));
        int starCost = Math.Max(0, HotCombatEngine.GetEffectiveStarCost(in s, in card, cold, rules, isInPlayablePile: true));
        bool exhausts = (card.LocalKeywordsMask & HotCardKeywordMask.Exhaust) != 0
            || (card.Flags & HotCardFlags.ExhaustOnNextPlay) != 0;
        bool isRetained = HotCombatEngine.HasEffectiveRetainKeyword(in s, in card, rules)
            || (card.Flags & HotCardFlags.SingleTurnRetain) != 0;

        bool hasDraw = false;
        bool hasEnergyOrStarGain = false;
        bool hasEngineOp = false;
        bool hasSelfBuff = false;
        bool hasEnemyDebuff = false;
        bool appliesWeak = false;
        bool hasHeal = def.CanHealPlayer;
        bool hasSelfHpLoss = false;
        bool hasBlockOp = def.GainsBlock;
        bool hasAttackOp = def.Type == CardType.Attack;
        int effectiveSetupWeight = def.SetupWeight;

        HotCardRoleFlags[]? cardRoles = evalLookup.CardRoles;
        if (cardRoles != null && (uint)card.CardDefId < (uint)cardRoles.Length)
        {
            HotCardRoleFlags roleFlags = cardRoles[card.CardDefId];
            hasDraw |= (roleFlags & HotCardRoleFlags.HasDraw) != 0;
            hasEnergyOrStarGain |= (roleFlags & HotCardRoleFlags.HasEnergyOrStarGain) != 0;
            hasEngineOp |= (roleFlags & HotCardRoleFlags.HasEngineOp) != 0;
            hasSelfBuff |= (roleFlags & HotCardRoleFlags.HasSelfBuff) != 0;
            hasEnemyDebuff |= (roleFlags & HotCardRoleFlags.HasEnemyDebuff) != 0;
            appliesWeak |= (roleFlags & HotCardRoleFlags.AppliesWeak) != 0;
            hasBlockOp |= (roleFlags & HotCardRoleFlags.HasBlock) != 0;
            hasSelfHpLoss |= (roleFlags & HotCardRoleFlags.HasSelfHpLoss) != 0;
            if (appliesWeak)
                hasEnemyDebuff = true;
        }

        ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(card.CardDefId);
        for (int k = 0; k < ops.Length; k++)
        {
            switch (ops[k].Op)
            {
                case HotCardOpKind.Draw:
                    hasDraw = true;
                    break;
                case HotCardOpKind.GainEnergy:
                case HotCardOpKind.GainStars:
                    hasEnergyOrStarGain = true;
                    break;
                case HotCardOpKind.ChannelOrb:
                case HotCardOpKind.EvokeOrb:
                case HotCardOpKind.SummonOsty:
                case HotCardOpKind.Forge:
                case HotCardOpKind.ShootArrow:
                    hasEngineOp = true;
                    break;
                case HotCardOpKind.ApplyPowerSelf:
                {
                    hasSelfBuff = true;
                    ushort pDefId = (ushort)ops[k].Arg0;
                    if (pDefId < rules.Powers.Length && rules.Powers[pDefId].SetupWeight > effectiveSetupWeight)
                        effectiveSetupWeight = rules.Powers[pDefId].SetupWeight;
                    break;
                }
                case HotCardOpKind.ApplyTempStat:
                    hasSelfBuff = true;
                    break;
                case HotCardOpKind.ApplyPowerTarget:
                case HotCardOpKind.ApplyPowerAllEnemies:
                {
                    hasEnemyDebuff = true;
                    ushort pDefId = (ushort)ops[k].Arg0;
                    if (pDefId < rules.Powers.Length && rules.Powers[pDefId].PowerId == HotPowerId.WeakPower)
                        appliesWeak = true;
                    break;
                }
                case HotCardOpKind.Heal:
                    hasHeal = true;
                    break;
                case HotCardOpKind.LoseHp:
                    hasSelfHpLoss = true;
                    break;
                case HotCardOpKind.GainBlock:
                    hasBlockOp = true;
                    break;
                case HotCardOpKind.Attack:
                case HotCardOpKind.AttackAll:
                case HotCardOpKind.AttackRandom:
                case HotCardOpKind.AttackOsty:
                    hasAttackOp = true;
                    break;
            }
        }

        // Avoid wasting energy on non-helpful Status/Curse cards.
        if (def.Type is CardType.Status or CardType.Curse)
        {
            if (exhausts && !hasSelfHpLoss && energyCost == 0 && starCost == 0)
                return 80;
            if (exhausts && !hasSelfHpLoss && ctx.UnblockedIncoming == 0 && s.Energy > energyCost)
                return 25;
            return -200;
        }

        // Zero-payoff & RetainHandPower hold guards (zero heap allocation):
        if (def.CardId == HotCustomCardId.GrandFinale && s.DrawCount > 0)
            return (ctx.HasRetainHand || isRetained) ? -120 : -20;
        if (def.CardId == HotCustomCardId.CalculatedGamble && s.HandCount <= 1)
            return -120;
        if (def.CardId is HotCustomCardId.Nightmare or HotCustomCardId.DualWield
            && ShouldHoldOrSkipCopyCard(
                in s,
                cold,
                rules,
                act.SlotOrHandIdx,
                attackOrPowerOnly: def.CardId == HotCustomCardId.DualWield,
                canRetain: ctx.HasRetainHand || isRetained,
                lethalThreat: lethalThreat))
        {
            return -120;
        }
        bool isFatalFinisher = false;
        bool isPlayableGrowth = false;
        int cardGrowthBudget = -1;
        bool advancesRelic = false;
        if (strat.Active)
        {
            if (strat.PureGrowthUnsatisfied)
            {
                cardGrowthBudget = GetGrowthCardBudget(
                    in card, def, rules, in policy,
                    strat.HasEmptyPotionSlot, strat.HasAnyNonMinionEnemy, s.Growth[9],
                    out isFatalFinisher, out isPlayableGrowth);
            }
            if (strat.RelicUnsatisfied && !hasSelfHpLoss)
            {
                advancesRelic = CardAdvancesUnsatisfiedRelic(def.Type, exhausts, starCost, strat.UnsatisfiedRelicMask);
            }
        }

        bool isPureBlockCard = hasBlockOp
            && !hasAttackOp
            && !hasDraw
            && !hasEnergyOrStarGain
            && !hasEngineOp
            && !hasSelfBuff
            && !hasEnemyDebuff
            && !hasHeal
            && def.Type != CardType.Power;
        if ((ctx.HasRetainHand || isRetained)
            && ctx.UnblockedIncoming == 0
            && !ctx.PreservesExcessBlock
            && isPureBlockCard
            && !isPlayableGrowth
            && !advancesRelic
            && s.HandCount <= 7)
        {
            return -60;
        }

        // Estimate generic Block and Damage strictly from HotCardDef + generic combat stats.
        int estBlock = 0;
        if (hasBlockOp)
        {
            int rawBlock = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
            if (rawBlock > 0)
            {
                int b = rawBlock + ctx.PlayerDexterity;
                if (ctx.PlayerFrail)
                    b = (b * 75) / 100;
                estBlock = Math.Max(0, b);
            }
            else
            {
                estBlock = 5;
            }
        }

        int estSingleDmg = 0;
        int estTotalDmg = 0;
        int estPerHit = 0;
        int estHits = 1;
        int killsCount = 0;
        int killedIncoming = 0;
        int targetSlot = act.TargetEnemySlot;
        TargetType effTarget = HotCombatEngine.GetEffectiveCardTargetType(in s, in card, def, rules);

        if (hasAttackOp)
        {
            int baseDmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
            int hits = def.GetHits(card.UpgradeLevel);
            estHits = hits;
            if (baseDmg > 0)
            {
                int perHit = Math.Max(0, baseDmg + ctx.PlayerStrength);
                if (ctx.PlayerWeak)
                    perHit = (perHit * 75) / 100;
                estPerHit = perHit;

                if (effTarget == TargetType.AnyEnemy && (uint)targetSlot < HotLimits.MaxEnemies)
                {
                    int scaledHit = enemyVuln[targetSlot] != 0 ? (perHit * 150) / 100 : perHit;
                    estSingleDmg = scaledHit * hits;
                    estTotalDmg = estSingleDmg;
                    int eTotal = enemyHp[targetSlot] + enemyBlock[targetSlot];
                    if (enemyHp[targetSlot] > 0 && estSingleDmg >= eTotal)
                    {
                        killsCount = 1;
                        killedIncoming = enemyIncoming[targetSlot];
                    }
                }
                else if (effTarget == TargetType.AllEnemies)
                {
                    int rosterCount = s.RosterCount;
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte eSlot = s.ActiveEnemyRoster[r];
                        if (enemyHp[eSlot] <= 0)
                            continue;
                        int scaledHit = enemyVuln[eSlot] != 0 ? (perHit * 150) / 100 : perHit;
                        int dmg = scaledHit * hits;
                        estTotalDmg += dmg;
                        if (dmg >= enemyHp[eSlot] + enemyBlock[eSlot])
                        {
                            killsCount++;
                            killedIncoming += enemyIncoming[eSlot];
                        }
                    }
                    estSingleDmg = perHit * hits;
                }
                else
                {
                    estSingleDmg = perHit * hits;
                    estTotalDmg = estSingleDmg;
                }
            }
            else
            {
                estSingleDmg = 6;
                estTotalDmg = effTarget == TargetType.AllEnemies ? 6 * Math.Max(1, ctx.AliveEnemies) : 6;
            }
        }

        // Immediate fight-ending lethal ALWAYS wins priority in both pilots when no unsatisfied strategy targets remain!
        if (killsCount > 0 && killsCount >= ctx.AliveEnemies && ctx.RevivingEnemies == 0)
        {
            if (!strat.Active)
                return 1_000_000 + estTotalDmg * 10 - energyCost;

            if (isFatalFinisher
                && HasFatalEligibleKillTarget(in s, rules, effTarget, targetSlot, enemyHp, enemyBlock, enemyVuln, estPerHit, estHits))
            {
                return 1_500_000 + estTotalDmg * 10 - energyCost;
            }

            if (isPlayableGrowth && (!strat.HoldLastEnemyLethal || !strat.RelicUnsatisfied))
                return 1_400_000 + estTotalDmg * 10 - energyCost;

            if (advancesRelic && strat.RelicDistance <= 1 && !strat.PureGrowthUnsatisfied)
                return 1_300_000 + estTotalDmg * 10 - energyCost;

            if (strat.HoldLastEnemyLethal)
                return -100;

            return 1_000_000 + estTotalDmg * 10 - energyCost;
        }

        // Avoid committing suicide via self-HP loss cards unless lethal.
        if (hasSelfHpLoss && s.Player.CurrentHp <= 3)
            return -500;

        if (strat.Active && !lethalThreat)
        {
            // Playable non-lethal growth cards (Alchemize, Royalties, ForbiddenGrimoire, MadScience(Improvement),
            // deck-backed GeneticAlgorithm/Goopy/TheScythe, ShowOff) rank ABOVE non-Fatal last-enemy lethal (1_200_000).
            if (isPlayableGrowth)
            {
                bool safePlay = ctx.UnblockedIncoming == 0
                    || (strat.HasLethalInHand && (s.Energy > energyCost || ctx.UnblockedIncoming <= Math.Max(0, cardGrowthBudget)));
                if (safePlay)
                {
                    int tieBoost = (energyCost == 0 && starCost == 0) || def.CardId == HotCustomCardId.ShowOff ? 50 : 0;
                    return 1_200_000 + tieBoost - energyCost * 100;
                }
            }

            // Safe relic counter-advancing cards rank above non-target last-enemy lethal (1_150_000).
            if (advancesRelic)
            {
                bool safeCounterPlay = ctx.UnblockedIncoming == 0
                    || (strat.HasLethalInHand && (s.Energy > energyCost || ctx.UnblockedIncoming <= strat.RemainingStrategyBudget));
                if (safeCounterPlay)
                    return 1_150_000 - energyCost * 100;
            }
        }

        int prio = 50;

        if (strat.Active)
        {
            if (isPlayableGrowth && !lethalThreat)
                prio += 1_800 + Math.Max(0, cardGrowthBudget) * 160;
            if (isFatalFinisher)
            {
                if (killsCount > 0
                    && HasFatalEligibleKillTarget(in s, rules, effTarget, targetSlot, enemyHp, enemyBlock, enemyVuln, estPerHit, estHits))
                {
                    prio += 25_000 + Math.Max(0, cardGrowthBudget) * 200;
                }
                else if (killsCount == 0)
                {
                    prio -= 300;
                }
            }
            if (advancesRelic && !lethalThreat)
                prio += 650;
        }

        // Free resource / draw accelerators run early so the pilot sees the full hand & energy pool.
        if (energyCost == 0 && starCost == 0)
        {
            if (hasEnergyOrStarGain)
                prio += 2_400;
            else if (hasDraw && !hasSelfHpLoss)
                prio += 1_800;
            else if (def.Type == CardType.Power)
                prio += 2_200;
            else
                prio += 250;
        }
        else if (hasEnergyOrStarGain && energyCost <= 1)
        {
            prio += 1_300;
        }

        // Kill & threat-removal credit (killing an attacking enemy prevents its entire incoming damage!).
        if (killsCount > 0)
        {
            prio += killsCount * 1_600;
            if (killedIncoming > 0)
                prio += Math.Min(killedIncoming, ctx.UnblockedIncoming + 10) * (lethalThreat ? 260 : 140);
        }

        // Healing when wounded
        if (hasHeal && missingHp > 0)
            prio += Math.Min(missingHp, 20) * 35;

        int usefulBlock = ctx.UnblockedIncoming > 0 ? Math.Min(estBlock, ctx.UnblockedIncoming) : 0;

        if (pilot == RolloutPilotKind.DefensiveTempo)
        {
            // Pilot A (Defensive / Tempo): block lethal or critical (<25% MaxHp) incoming first,
            // otherwise scale Power priority by catalog SetupWeight so scaling engines get played!
            int postHitHp = s.Player.CurrentHp - ctx.UnblockedIncoming;
            bool criticalDanger = lethalThreat
                || (ctx.UnblockedIncoming > 0 && postHitHp * 4 < Math.Max(1, s.Player.MaxHp));

            if (ctx.UnblockedIncoming > 0)
            {
                if (usefulBlock > 0)
                    prio += usefulBlock * (lethalThreat ? 260 : criticalDanger ? 185 : 135) + 450;
                else if (estBlock > 0)
                    prio += estBlock * 15;

                if (hasEnemyDebuff)
                {
                    bool targetsAttacker = (uint)targetSlot < HotLimits.MaxEnemies && enemyIncoming[targetSlot] > 0;
                    prio += (targetsAttacker || effTarget == TargetType.AllEnemies) ? 420 : 180;
                    if (appliesWeak)
                    {
                        int targetIncoming = effTarget == TargetType.AllEnemies
                            ? ctx.UnblockedIncoming
                            : ((uint)targetSlot < HotLimits.MaxEnemies ? enemyIncoming[targetSlot] : 0);
                        int weakMitigation = Math.Min(ctx.UnblockedIncoming, Math.Max(0, targetIncoming / 4));
                        if (weakMitigation > 0)
                            prio += weakMitigation * (lethalThreat ? 220 : 120);
                    }
                }
            }
            else
            {
                if (estBlock > 0)
                {
                    // Already fully blocked: small credit only (or higher if excess block is preserved).
                    prio += Math.Min(estBlock, 15) * (ctx.PreservesExcessBlock ? 28 : 6);
                }
                if (hasEnemyDebuff)
                    prio += 180;
            }

            if (estTotalDmg > 0)
                prio += estTotalDmg * 45;

            if (def.Type == CardType.Power)
            {
                int powerWeight = Math.Clamp(effectiveSetupWeight > 0 ? effectiveSetupWeight : 12, 0, 50);
                if (ctx.UnblockedIncoming == 0)
                    prio += 720 + powerWeight * 24;
                else if (!criticalDanger)
                    prio += 360 + powerWeight * 16;
                else if (!lethalThreat)
                    prio += 200 + powerWeight * 8;
                else
                    prio += 80;
            }

            if (hasDraw)
                prio += 320;
            if (hasEngineOp)
                prio += 340;
            if (hasSelfBuff)
                prio += 280;

            // Spend available energy productively when threat is covered.
            if (ctx.UnblockedIncoming == 0 && energyCost > 0)
                prio += energyCost * 90;

            // Target tie-breaker for AnyEnemy attacks: focus lowest HP (and prioritize attackers).
            if (effTarget == TargetType.AnyEnemy && (uint)targetSlot < HotLimits.MaxEnemies && enemyHp[targetSlot] > 0)
            {
                prio += Math.Max(0, 200 - Math.Min(enemyHp[targetSlot], 200));
                if (enemyIncoming[targetSlot] > 0)
                    prio += 60;
            }
        }
        else
        {
            // Pilot B (Setup-First): play Powers and scaling/engine Skills before Attacks, while still
            // guarding against immediate lethal hits and spending remaining energy productively.
            if (lethalThreat && usefulBlock > 0)
                prio += usefulBlock * 240 + 1_200;

            if (def.Type == CardType.Power)
                prio += 1_550 + Math.Clamp(effectiveSetupWeight, 0, 50) * 25;

            if (hasEngineOp)
                prio += 950;
            if (hasSelfBuff)
                prio += 900;
            if (hasDraw)
                prio += 820;
            if (hasEnemyDebuff)
                prio += 800;

            if (ctx.UnblockedIncoming > 0 && usefulBlock > 0 && !lethalThreat)
                prio += usefulBlock * 70 + 220;

            if (estTotalDmg > 0)
            {
                prio += estTotalDmg * 32;
                // Natural non-hardcoded retention awareness: when a Retained Attack is NOT lethal right now
                // and other non-retained plays or active scaling exist, let Setup-First defer it to cook!
                if (isRetained && killsCount == 0 && (hasNonRetainedCardOption || ctx.HasActiveScaling))
                    prio -= 420;
            }

            // Target tie-breaker: debuffs prefer highest HP enemy; attacks focus lowest HP enemy.
            if (effTarget == TargetType.AnyEnemy && (uint)targetSlot < HotLimits.MaxEnemies && enemyHp[targetSlot] > 0)
            {
                if (hasEnemyDebuff && !hasAttackOp)
                    prio += Math.Min(enemyHp[targetSlot], 250);
                else
                    prio += Math.Max(0, 200 - Math.Min(enemyHp[targetSlot], 200));
            }
        }

        if (hasSelfHpLoss && !lethalThreat)
            prio -= 180;

        return prio;
    }

    private bool TryPromoteRolloutVictory(
        int parentNodeId,
        HotRolloutWorkspace ownerWs,
        int stepStart,
        int stepCount,
        HotNodeArena nodeArena,
        PackedStateArena mainStateArena,
        HotRules rules,
        in HotSearchPolicy policy,
        in HotEvalLookup evalLookup,
        HotEvalMode evalMode,
        List<int> victoriesOut)
    {
        if (stepCount <= 0 || stepCount > MaxTotalRolloutSteps)
            return false;

        ref readonly HotSearchNode parentNode = ref nodeArena.GetRef(parentNodeId);
        if (parentNode.IsTerminal || parentNode.StateHandle.Length <= 0)
            return false;

        HotStatePacker.Unpack(mainStateArena.GetSpan(parentNode.StateHandle), ref _commitState);
        HotColdSidecar? cold = parentNode.Cold;

        ushort curActionCount = parentNode.ActionCount;
        byte curPotionCount = parentNode.PotionCount;
        byte curActionsThisTurn = parentNode.ActionsThisTurn;
        int curTurn = parentNode.Turn;

        int builtCount = 0;

        try
        {
            for (int k = 0; k < stepCount; k++)
            {
                HotRolloutTraceStep step = ownerWs.PromotedSteps[stepStart + k];
                ReadOnlySpan<HotChoiceSelection> choices = step.ChoiceCount > 0
                    ? ownerWs.PromotedChoices.AsSpan(step.ChoiceOffset, step.ChoiceCount)
                    : ReadOnlySpan<HotChoiceSelection>.Empty;

                // Exact match with HotPlanMaterializer.cs:115-116!
                HotStatePacker.CanonicalizeForPack(ref _commitState, ref cold);
                HotParallelExpander.AssignDeterministicCardUids(ref _commitState);

                _commitBeforeStepState = _commitState;
                HotStepResult res = HotCombatEngine.Step(
                    ref _commitState,
                    ref cold,
                    rules,
                    _commitScratch,
                    step.Action,
                    choices);

                if (!res.Executed || res.NeedsChoice)
                    return false;

                if (policy.BrightestFlameEffectiveMaxHpLimit.HasValue
                    && !policy.AllowsBrightestFlame(_commitState.BrightestFlameMaxHpSpent))
                {
                    return false;
                }

                byte autoPotionDelta = DetectAutoPotionDelta(in _commitBeforeStepState, in _commitState);
                curPotionCount = (byte)Math.Min(byte.MaxValue, curPotionCount + autoPotionDelta);
                curActionCount = (ushort)Math.Min(ushort.MaxValue, curActionCount + 1);

                bool isLast = k == stepCount - 1;
                bool endedTurn = step.Action.Kind == HotActionKind.EndTurn || _commitState.PlayerTurnNumber > curTurn;
                curActionsThisTurn = (endedTurn || step.Action.Kind == HotActionKind.TurnSetup)
                    ? (byte)0
                    : (byte)Math.Min(byte.MaxValue, curActionsThisTurn + 1);

                _stagedPromoteChoiceStarts[builtCount] = step.ChoiceOffset;
                _stagedPromoteChoiceCounts[builtCount] = step.ChoiceCount;

                if (!isLast)
                {
                    if (_commitState.TerminalOutcome != HotCombatOutcome.Ongoing)
                    {
                        // Reached terminal earlier than recorded length — check if clean Victory right here.
                        isLast = true;
                    }
                }

                if (isLast)
                {
                    HotFastSummary summary = HotEvaluator.ExtractStage1(in _commitState, in policy);
                    // Strict Victory verification (#287): NEVER promote EventDefeat or non-Victory!
                    if (!summary.IsVictory
                        || _commitState.TerminalOutcome != HotCombatOutcome.Victory
                        || (_commitState.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) != 0)
                    {
                        return false;
                    }

                    int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
                        parentNode.PotionStrategicCost,
                        parentNode.OptionalPaidPotionCount,
                        summary.AlchemizeCount,
                        summary.EmptyPotionSlots,
                        in policy);

                    HotEvaluator.EvaluateState(
                        in _commitState,
                        cold,
                        rules,
                        in policy,
                        in evalLookup,
                        in summary,
                        netPotionCostCenti,
                        curActionCount,
                        evalMode,
                        out HotEvalFeatures eval);

                    HotNodeFlags flags = HotNodeFlags.IsTerminal | HotNodeFlags.RolloutPromoted;
                    if (endedTurn) flags |= HotNodeFlags.EndedTurn;
                    if (step.ChoiceCount > 0) flags |= HotNodeFlags.HasChoice;
                    if (summary.DeathSaveUseCount > 0) flags |= HotNodeFlags.HasDeathSave;
                    if (summary.ForfeitsGold) flags |= HotNodeFlags.ForfeitsGold;
                    if (summary.AllGrowthTargetsSatisfied) flags |= HotNodeFlags.AllGrowthTargetsSatisfied;
                    if (summary.AllRelicTargetsSatisfied) flags |= HotNodeFlags.AllRelicTargetsSatisfied;

                    _stagedPromoteNodes[builtCount++] = new HotSearchNode
                    {
                        Hash = default,
                        Score = eval.Score,
                        RolloutScore = eval.Score,
                        Cold = null,
                        StateHandle = default,
                        ParentId = -1, // linked during commit below
                        ChoiceOffset = -1,
                        HpLost = summary.HpLost,
                        HpRecovered = summary.HpRecovered,
                        PlayerHp = summary.PlayerHp,
                        EnemyHp = summary.EnemyHpSum,
                        SetupScore = eval.SetupScore,
                        BoardInvestment = eval.BoardInvestment,
                        MaxDarkEvoke = eval.MaxDarkEvoke,
                        TotalPoisonAndDoom = eval.TotalPoisonAndDoom,
                        PlayerMaxHpDelta = (short)Math.Clamp(policy.InitialPlayerMaxHp - summary.PlayerMaxHp, short.MinValue, short.MaxValue),
                        PlayerBlock = (short)Math.Clamp(summary.PlayerBlock, 0, short.MaxValue),
                        EffectiveDefense = eval.EffectiveDefense,
                        OptionalityScore = eval.OptionalityScore,
                        OutstandingStolen = (short)Math.Clamp(summary.OutstandingStolen, 0, short.MaxValue),
                        GrowthHpCredit = (short)Math.Clamp(summary.GrowthHpCredit, short.MinValue, short.MaxValue),
                        RelicHpCredit = summary.RelicHpCredit,
                        RelicHealingHpCredit = summary.RelicHealingHpCredit,
                        RelicSatisfiedMask = summary.RelicSatisfiedMask,
                        RelicDistance = summary.RelicDistance,
                        RelicSatisfiedPriority = summary.RelicSatisfiedPriority,
                        FeedCount = summary.FeedCount,
                        PotionStrategicCost = parentNode.PotionStrategicCost,
                        DeathSaveHpRestored = (short)Math.Clamp(summary.DeathSaveHpRestored, 0, short.MaxValue),
                        PotionMask = parentNode.PotionMask,
                        ActionCount = curActionCount,
                        GrowthRewardCount = (ushort)Math.Clamp(summary.GrowthRewardCount, 0, ushort.MaxValue),
                        Flags = flags,
                        Action = step.Action,
                        ChoiceCount = step.ChoiceCount,
                        PotionCount = curPotionCount,
                        ExplicitPotionCount = parentNode.ExplicitPotionCount,
                        OptionalPaidPotionCount = parentNode.OptionalPaidPotionCount,
                        AmbergrisCount = parentNode.AmbergrisCount,
                        AlchemizeCount = summary.AlchemizeCount,
                        EmptyPotionSlots = summary.EmptyPotionSlots,
                        Energy = (byte)Math.Clamp(summary.Energy, 0, byte.MaxValue),
                        Turn = (byte)Math.Clamp(summary.Turn, 0, byte.MaxValue),
                        ActionsThisTurn = curActionsThisTurn,
                        AliveEnemyCount = summary.AliveEnemyCount,
                        RevivingEnemyCount = summary.RevivingEnemyCount,
                        PlayableHandCount = eval.PlayableHandCount,
                        DeathSaveUseCount = summary.DeathSaveUseCount,
                        Outcome = HotCombatOutcome.Victory,
                    };
                    break;
                }

                HotNodeFlags midFlags = HotNodeFlags.RolloutPromoted;
                if (endedTurn) midFlags |= HotNodeFlags.EndedTurn;
                if (step.ChoiceCount > 0) midFlags |= HotNodeFlags.HasChoice;

                _stagedPromoteNodes[builtCount++] = new HotSearchNode
                {
                    ParentId = -1,
                    ChoiceOffset = -1,
                    ActionCount = curActionCount,
                    Flags = midFlags,
                    Action = step.Action,
                    ChoiceCount = step.ChoiceCount,
                    PotionMask = parentNode.PotionMask,
                    PotionStrategicCost = parentNode.PotionStrategicCost,
                    PotionCount = curPotionCount,
                    ExplicitPotionCount = parentNode.ExplicitPotionCount,
                    OptionalPaidPotionCount = parentNode.OptionalPaidPotionCount,
                    AmbergrisCount = parentNode.AmbergrisCount,
                    Turn = (byte)Math.Clamp(_commitState.PlayerTurnNumber, 0, byte.MaxValue),
                    ActionsThisTurn = curActionsThisTurn,
                    Outcome = HotCombatOutcome.Ongoing,
                };

                curTurn = _commitState.PlayerTurnNumber;
            }
        }
        catch (HotCapacityExceededException)
        {
            return false;
        }

        if (builtCount <= 0)
            return false;

        // Verify the final terminal node satisfies all hard potion constraints before touching nodeArena.
        HotRouteKey victoryKey = HotRouteQuality.Capture(in _stagedPromoteNodes[builtCount - 1], in policy);
        if (!victoryKey.Won || !victoryKey.Survives || !HotPotionPolicy.SatisfiesHardConstraints(in victoryKey, in policy))
            return false;

        // Pre-validate choice slice counts so TryAppendChoices cannot fail mid-chain.
        for (int i = 0; i < builtCount; i++)
        {
            if (_stagedPromoteChoiceCounts[i] > HotLimits.MaxChoicesPerAction)
                return false;
        }

        // All steps validated cleanly -> link and append the verified suffix chain into nodeArena!
        int linkParentId = parentNodeId;
        for (int i = 0; i < builtCount; i++)
        {
            HotSearchNode nodeToAdd = _stagedPromoteNodes[i];
            nodeToAdd.ParentId = linkParentId;

            byte cCount = _stagedPromoteChoiceCounts[i];
            if (cCount > 0)
            {
                if (!nodeArena.TryAppendChoices(
                        ownerWs.PromotedChoices.AsSpan(_stagedPromoteChoiceStarts[i], cCount),
                        out int choiceOffset))
                {
                    return false;
                }
                nodeToAdd.ChoiceOffset = choiceOffset;
            }

            linkParentId = nodeArena.AddNode(in nodeToAdd);
        }

        victoriesOut.Add(linkParentId);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DetectAutoPotionDelta(in CombatHotState before, in CombatHotState after)
    {
        int beforeFilled = 0;
        int afterFilled = 0;
        int slots = Math.Min((int)before.PotionSlotCount, HotLimits.MaxPotions);
        for (int i = 0; i < slots; i++)
        {
            if (before.Potions[i] != 0) beforeFilled++;
            if (after.Potions[i] != 0) afterFilled++;
        }
        int delta = beforeFilled - afterFilled;
        return delta > 0 ? (byte)delta : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EstimateEnemyIncomingDamageFast(
        in HotEnemy enemy,
        HotRules rules,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        if (enemy.MonsterDefId == 0 || enemy.CurrentMoveNode < 0)
            return 0;

        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null || enemy.CurrentMoveNode >= machine.Nodes.Length)
            return 0;

        HotMonsterNode node = machine.Nodes[enemy.CurrentMoveNode];
        int total = 0;

        if (node.Attacks.Length > 0)
        {
            for (int i = 0; i < node.Attacks.Length; i++)
            {
                HotMonsterAttack atk = node.Attacks[i];
                if (atk.Repeats == 0)
                    continue;
                total += ScaleEnemyHitFast(atk.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup) * atk.Repeats;
            }
        }
        else if (node.Repeats > 0)
        {
            int hits = node.Repeats;
            if (enemy.MonsterDefId < rules.Monsters.Length
                && rules.Monsters[enemy.MonsterDefId].StateKind == HotMonsterStateKind.TestSubject
                && string.Equals(node.MoveId, "MULTI_CLAW_MOVE", StringComparison.Ordinal))
            {
                hits = Math.Max(1, rules.Monsters[enemy.MonsterDefId].TestSubjectBaseMultiClawCount + enemy.AiRegs[1]);
            }
            total += ScaleEnemyHitFast(node.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup) * hits;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleEnemyHitFast(
        int baseDamage,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        int dmg = baseDamage + enemyStrength;
        if (dmg <= 0)
            return 0;
        if (enemyWeak)
            dmg = lookup.HasPaperKrane ? (dmg * 60) / 100 : (dmg * 75) / 100;
        if (playerVulnerable)
            dmg = lookup.HasOddMushroom ? (dmg * 125) / 100 : (dmg * 150) / 100;
        if (playerIntangible && dmg > 1)
            dmg = 1;
        return Math.Max(0, dmg);
    }

    private void EnsureResultCapacity(int required)
    {
        if (required <= _results.Length)
            return;
        int next = _results.Length;
        while (next < required)
            next <<= 1;
        _results = new HotRolloutCandidateResult[next];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = 0; i < _workspaces.Length; i++)
            _workspaces[i].ResetWave();
    }
}
