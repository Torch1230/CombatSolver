using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal readonly record struct EvaluatedForecastCandidate(
    SearchNode Node,
    ForecastSearchBoundary NodeBoundary,
    int ProjectedPlayerHp);

internal sealed record ForecastPassOutcome(
    SearchNode Selected,
    EvaluatedForecastCandidate[] EvaluatedCandidates,
    ForecastSearchBoundary PassBoundary,
    long ExpandedNodes,
    bool AcceptableVictoryFound,
    int RootInitialCumulativeHpLost,
    StateEvaluator Evaluator)
{
    public long ExpandedParents { get; init; }
    public long TransitionCount { get; init; }
    public long ChoiceReplayAttempts { get; init; }
    public long ElapsedMilliseconds { get; init; }
    // The policy-filtered pool from the comparison that actually chose this pass.
    // In-combat presentation can reconsider a budget-limited death after all search work.
    public (SearchNode[] Nodes, int InitialHp, StateEvaluator Evaluator)? InCombatSelectionPool { get; init; }
    public SearchNode? SharedPotionFreeBaseline { get; init; }
}

internal sealed record ParentExpansionResult(
    List<SearchNode> Completed,
    List<SearchNode> Ended,
    List<SearchNode> Next,
    List<SearchNode> ExtraTruncatedParents,
    SearchNode? PotionFreeBoundaryFallback,
    SearchNode? PotionBoundaryFallback,
    bool ParentHitPendingChoice,
    bool AnyHitPendingChoice,
    bool AcceptableVictoryFound,
    ForecastSearchBoundary LimitBoundary,
    bool StoppedByExternalLimit);

internal sealed class ForecastNodeBudgetTracker
{
    private long _consumed;

    public ForecastNodeBudgetTracker(int nodeLimit)
    {
        if (nodeLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(nodeLimit));
        NodeLimit = nodeLimit;
    }

    public int NodeLimit { get; }
    public long Consumed => Volatile.Read(ref _consumed);

    public bool TryReserveSlot()
    {
        while (true)
        {
            long current = Volatile.Read(ref _consumed);
            if (current >= NodeLimit)
                return false;
            if (Interlocked.CompareExchange(ref _consumed, current + 1, current) == current)
                return true;
        }
    }
}

public static partial class BeamSearch
{
    [ThreadStatic]
    private static List<PlannedAction>? _serialActionScratch;

    internal static async Task<BeamForecastResult> SolveForForecastAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int openingPreRootBattleHpLost,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1)
    {
        ValidateSearchArguments(root, budget, policy, portfolio, maxDegreeOfParallelism);
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();

        // 开局即终止的战斗直接按胜负构造预测结果，不消耗搜索预算。
        if (root.Engine is { IsInProgress: false } terminalEngine)
        {
            bool won = terminalEngine.Won;
            int finalHp = Math.Max(0, root.Players[0].Creature.CurrentHp);
            int? endedTurn = won ? Math.Max(1, root.Players[0].PlayerCombatState?.TurnNumber ?? 1) : null;
            bool onlyDeathRoutes = !won;
            ForecastConfidence confidence = won ? ForecastConfidence.Complete : ForecastConfidence.DeathOnly;
            return new BeamForecastResult(
                Actions: Array.Empty<PlannedAction>(),
                Won: won,
                ProjectedHpLoss: openingPreRootBattleHpLost,
                FinalHp: finalHp,
                CombatEndedTurn: endedTurn,
                OnlyDeathRoutes: onlyDeathRoutes,
                Confidence: confidence,
                SearchBoundary: ForecastSearchBoundary.None,
                PotionUses: Array.Empty<ForecastPotionUse>(),
                ExpandedNodes: 0,
                SearchElapsedMilliseconds: watch.Elapsed.TotalMilliseconds);
        }

        if (maxDegreeOfParallelism > 1)
            ModelDb.Prewarm();

        return await PreCombatBeamPortfolio.SolveAsync(
            root,
            budget,
            policy,
            portfolio,
            openingPreRootBattleHpLost,
            watch,
            ct,
            maxDegreeOfParallelism).ConfigureAwait(false);
    }

    internal static Task<InCombatBeamResult> SolveForInCombatAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int priorBattleHpLost,
        Action<InCombatProgressUpdate>? progressCallback,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1,
        Action<InCombatBeamResult>? onPartialResult = null,
        CancellationToken hardDeadlineToken = default) =>
        SolveForInCombatWithDeliveryTrackingAsync(
            root,
            budget,
            policy,
            portfolio,
            priorBattleHpLost,
            progressCallback is null
                ? null
                : update =>
                {
                    progressCallback(update);
                    return true;
                },
            ct,
            maxDegreeOfParallelism,
            onPartialResult,
            hardDeadlineToken);

    internal static async Task<InCombatBeamResult> SolveForInCombatWithDeliveryTrackingAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int priorBattleHpLost,
        Func<InCombatProgressUpdate, bool>? progressCallback,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1,
        Action<InCombatBeamResult>? onPartialResult = null,
        CancellationToken hardDeadlineToken = default)
    {
        ValidateSearchArguments(root, budget, policy, portfolio, maxDegreeOfParallelism);
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();

        if (root.Engine is { IsInProgress: false } terminalEngine)
        {
            var terminalEval = new StateEvaluator(root, policy);
            SearchNode terminalNode = SearchNode.Create(root.Clone(), null, null, terminalEval);
            return InCombatRouteTracer.TraceRoute(
                root,
                Array.Empty<PlannedAction>(),
                terminalNode,
                Math.Max(0, root.Players[0].Creature.CurrentHp),
                onlyDeathRoutes: !terminalEngine.Won,
                boundary: ForecastSearchBoundary.None,
                totalExpandedNodes: 0,
                priorBattleHpLost: priorBattleHpLost,
                searchElapsedMilliseconds: watch.Elapsed.TotalMilliseconds,
                evaluator: terminalEval,
                traceCancellation: ct);
        }

        if (maxDegreeOfParallelism > 1)
            ModelDb.Prewarm();

        return await PreCombatBeamPortfolio.SolveForInCombatWithDeliveryTrackingAsync(
            root,
            budget,
            policy,
            portfolio,
            priorBattleHpLost,
            watch,
            progressCallback,
            ct,
            maxDegreeOfParallelism,
            onPartialResult,
            hardDeadlineToken).ConfigureAwait(false);
    }

    private static void ValidateSearchArguments(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(portfolio);
        if (budget.BeamWidth < 1 || budget.NodeLimit < 1 || budget.MaxCardBranches < 1
            || budget.MaxPileChoiceBranches < 1 || budget.MaxHandChoiceBranches < 1
            || budget.PotionFreeBeamWidth is < 1 || budget.PotionBeamWidth is < 1
            || budget.TimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        if (maxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }
    }

    internal static BeamForecastResult BuildForecastResult(
        ForecastPassOutcome selectedPass,
        IReadOnlyList<EvaluatedForecastCandidate> allEvaluatedCandidates,
        long totalExpandedNodes,
        int openingPreRootBattleHpLost,
        double searchElapsedMilliseconds)
    {
        if (allEvaluatedCandidates.Count == 0)
            throw new InvalidOperationException("Forecast beam search produced no evaluated candidates.");

        SearchNode selected = selectedPass.Selected;
        IReadOnlyList<PlannedAction> route = selected.Route();
        int? combatEndedTurn = selected.Won ? Math.Max(1, selected.Turn) : null;

        bool onlyDeathRoutes = true;
        EvaluatedForecastCandidate selectedCandidate = default;
        for (int i = 0; i < allEvaluatedCandidates.Count; i++)
        {
            EvaluatedForecastCandidate cand = allEvaluatedCandidates[i];
            if (ReferenceEquals(cand.Node, selected))
                selectedCandidate = cand;
            if (onlyDeathRoutes
                && cand.Node.State.Players[0].Creature.IsAlive
                && cand.Node.State.Players[0].Creature.CurrentHp > 0
                && cand.ProjectedPlayerHp > 0)
            {
                onlyDeathRoutes = false;
            }
        }

        ForecastConfidence confidence = onlyDeathRoutes
            ? ForecastConfidence.DeathOnly
            : combatEndedTurn.HasValue
                ? ForecastConfidence.Complete
                : ForecastConfidence.Bounded;

        int postRootHpLoss = Math.Max(
            0,
            selected.State.Players[0].Creature.CumulativeHpLost - selectedPass.RootInitialCumulativeHpLost);
        int projectedHpLoss = checked(openingPreRootBattleHpLost + postRootHpLoss);
        int finalHp = Math.Max(0, selected.State.Players[0].Creature.CurrentHp);

        ForecastSearchBoundary boundary;
        if (selected.Won)
        {
            boundary = ForecastSearchBoundary.None;
        }
        else if (selectedCandidate.Node is not null && selectedCandidate.NodeBoundary != ForecastSearchBoundary.None)
        {
            boundary = selectedCandidate.NodeBoundary;
        }
        else if (selectedPass.PassBoundary != ForecastSearchBoundary.None)
        {
            boundary = selectedPass.PassBoundary;
        }
        else if (!selected.Ended)
        {
            boundary = ForecastSearchBoundary.NodeLimit;
        }
        else
        {
            boundary = ForecastSearchBoundary.None;
        }

        var potionUsesList = new List<ForecastPotionUse>();
        for (int i = 0; i < route.Count; i++)
        {
            PlannedAction action = route[i];
            if (action.Kind == "UsePotion" && !string.IsNullOrWhiteSpace(action.PotionId))
            {
                potionUsesList.Add(new ForecastPotionUse(
                    action.PotionId!,
                    Math.Max(1, action.Turn),
                    action.PotionSlot));
            }
        }

        return new BeamForecastResult(
            route,
            selected.Won,
            projectedHpLoss,
            finalHp,
            combatEndedTurn,
            onlyDeathRoutes,
            confidence,
            boundary,
            potionUsesList.ToArray(),
            totalExpandedNodes,
            searchElapsedMilliseconds);
    }

    internal static Task<ForecastPassOutcome> RunSingleForecastPassAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        CancellationToken ct,
        bool aggressivePowerCommitment = false,
        int openingPreRootBattleHpLost = 0,
        int maxDegreeOfParallelism = 1,
        PolicyRootOperands? preCapturedPolicyOperands = null,
        ForecastNodeBudgetTracker? budgetTracker = null,
        InCombatPassTrack? inCombatPassTrack = null,
        StateEvaluatorBaseline? evaluatorBaseline = null,
        Action<long>? onCanceledExpanded = null,
        BeamPassOptions? passOptions = null)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        if (budgetTracker is not null && budgetTracker.NodeLimit != budget.NodeLimit)
            throw new ArgumentException("Budget tracker node limit must match budget.NodeLimit.", nameof(budgetTracker));

        ForecastPassOutcome? outcome = RunSingleForecastPassCoreOrNull(
            root,
            budget,
            policy,
            ct,
            maxDegreeOfParallelism,
            preCapturedPolicyOperands,
            budgetTracker,
            inCombatPassTrack,
            evaluatorBaseline,
            onCanceledExpanded,
            ResolvePassOptions(aggressivePowerCommitment, openingPreRootBattleHpLost, passOptions))
            ?? throw new InvalidOperationException("Forecast beam search produced no policy-satisfied outcome.");
        return Task.FromResult(outcome);
    }

    internal static Task<ForecastPassOutcome?> RunSingleForecastPassOrNullAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        CancellationToken ct,
        bool aggressivePowerCommitment = false,
        int openingPreRootBattleHpLost = 0,
        int maxDegreeOfParallelism = 1,
        PolicyRootOperands? preCapturedPolicyOperands = null,
        ForecastNodeBudgetTracker? budgetTracker = null,
        InCombatPassTrack? inCombatPassTrack = null,
        StateEvaluatorBaseline? evaluatorBaseline = null,
        Action<long>? onCanceledExpanded = null,
        BeamPassOptions? passOptions = null)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        if (budgetTracker is not null && budgetTracker.NodeLimit != budget.NodeLimit)
            throw new ArgumentException("Budget tracker node limit must match budget.NodeLimit.", nameof(budgetTracker));

        ForecastPassOutcome? outcome = RunSingleForecastPassCoreOrNull(
            root,
            budget,
            policy,
            ct,
            maxDegreeOfParallelism,
            preCapturedPolicyOperands,
            budgetTracker,
            inCombatPassTrack,
            evaluatorBaseline,
            onCanceledExpanded,
            ResolvePassOptions(aggressivePowerCommitment, openingPreRootBattleHpLost, passOptions));
        return Task.FromResult(outcome);
    }

    private static BeamPassOptions ResolvePassOptions(
        bool aggressivePowerCommitment,
        int openingPreRootBattleHpLost,
        BeamPassOptions? passOptions)
    {
        return passOptions ?? (aggressivePowerCommitment || openingPreRootBattleHpLost != 0
            ? new BeamPassOptions
            {
                MemberMode = aggressivePowerCommitment
                    ? BeamMemberSearchMode.AggressivePowerCommitment
                    : BeamMemberSearchMode.Default,
                OpeningPreRootBattleHpLost = openingPreRootBattleHpLost,
            }
            : BeamPassOptions.Default);
    }

    private static ForecastPassOutcome? RunSingleForecastPassCoreOrNull(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        CancellationToken ct,
        int effectiveDop,
        PolicyRootOperands? preCapturedPolicyOperands,
        ForecastNodeBudgetTracker? budgetTracker,
        InCombatPassTrack? inCombatPassTrack,
        StateEvaluatorBaseline? evaluatorBaseline,
        Action<long>? onCanceledExpanded,
        BeamPassOptions options)
    {
        if (effectiveDop < 1)
            throw new ArgumentOutOfRangeException(nameof(effectiveDop));
        ct.ThrowIfCancellationRequested();
        if (effectiveDop > 1)
            ModelDb.Prewarm();

        CombatState startStateSource = options.StartNodeOverride?.State ?? root;
        int initialHp = options.RootBaselineEvaluator?.InitialHp ?? root.Players[0].Creature.CurrentHp;
        int rootInitialCumulativeHpLost = options.RootBaselineEvaluator?.InitialCumulativeHpLost
            ?? root.Players[0].Creature.CumulativeHpLost;
        int rootTurnNumber = startStateSource.Players[0].PlayerCombatState?.TurnNumber ?? 1;
        PolicyRootOperands? effectiveOperands = preCapturedPolicyOperands ?? options.RootPolicyOperands;
        StateEvaluator evaluator = options.RootBaselineEvaluator is not null
            ? new StateEvaluator(startStateSource, policy, options.MemberMode, options.RootBaselineEvaluator)
            : evaluatorBaseline is not null
                ? new StateEvaluator(evaluatorBaseline, memberModeOverride: options.MemberMode)
                : new StateEvaluator(
                    StateEvaluator.CaptureBaseline(startStateSource, policy, effectiveOperands),
                    memberModeOverride: options.MemberMode);
        inCombatPassTrack?.AttachEvaluator(
            evaluator,
            initialHp,
            effectiveDop > 1 ? Math.Max(effectiveDop, options.SharedLanePool?.LaneCount ?? 1) : 1);

        SearchNode start = options.StartNodeOverride is not null
            ? SearchNode.CreateContinuationStart(options.StartNodeOverride, evaluator)
            : SearchNode.Create(root.Clone(), null, null, evaluator);
        start.State.FreezeForConcurrentReads();
        var frontier = new List<SearchNode> { start };
        var completed = new List<SearchNode>();
        var lastActive = new List<SearchNode>();
        var truncatedParents = new List<SearchNode>();
        var watch = Stopwatch.StartNew();
        long serialAttempts = 0;
        long serialParents = 0;
        long serialTransitions = 0;
        ForecastNodeBudgetTracker nodeBudget = budgetTracker ?? new ForecastNodeBudgetTracker(budget.NodeLimit);
        inCombatPassTrack?.AttachBudgetTracker(nodeBudget);
        ForecastSearchBoundary passBoundary = ForecastSearchBoundary.None;
        bool hitPendingChoiceBoundary = false;
        bool acceptableVictoryFound = false;
        SearchNode? potionFreeBoundaryFallback = null;
        SearchNode? potionBoundaryFallback = null;

        SearchWorkLedger? ledger = options.WorkLedger;
        MemberBudgetController budgetController = options.BudgetController
            ?? new MemberBudgetController(budget.NodeLimit, sharedBudget: null);
        ParentAdmissionTable? admissionTable = options.UseTranspositionAdmission
            ? new ParentAdmissionTable()
            : null;
        ForecastLanePool? ownedPool = options.SharedLanePool is null && effectiveDop > 1
            ? new ForecastLanePool(effectiveDop)
            : null;
        ForecastLanePool? lanePool = effectiveDop > 1 ? (options.SharedLanePool ?? ownedPool) : null;
        ForecastWaveExecutor? waveExecutor = lanePool is { LaneCount: > 1 }
            ? new ForecastWaveExecutor(
                lanePool,
                budget,
                policy,
                evaluator,
                options,
                budgetController,
                admissionTable,
                watch,
                nodeBudget,
                inCombatPassTrack,
                ct,
                maxConcurrency: Math.Clamp(options.MaxConcurrency ?? effectiveDop, 1, lanePool.LaneCount))
            : null;

        try
        {
            for (int layer = 0; layer < 100 && frontier.Count > 0; layer++)
            {
                ct.ThrowIfCancellationRequested();
                List<SearchNode> active = frontier;
                var ended = new List<SearchNode>();
                for (int depth = 0; depth < 100 && active.Count > 0 && !acceptableVictoryFound; depth++)
                {
                    ct.ThrowIfCancellationRequested();
                    var next = new List<SearchNode>();

                    if (waveExecutor is null)
                    {
                        for (int parentIndex = 0; parentIndex < active.Count; parentIndex++)
                        {
                            SearchNode parent = active[parentIndex];
                            ct.ThrowIfCancellationRequested();
                            if (admissionTable is not null && !admissionTable.TryAdmit(parent))
                                continue;

                            if (options.UseParentNodeBudget)
                            {
                                if (CheckLimitReached(serialParents, budget, watch, ref passBoundary)
                                    || !budgetController.TryReserveOne())
                                {
                                    if (passBoundary == ForecastSearchBoundary.None)
                                    {
                                        passBoundary = watch.Elapsed >= budget.TimeLimit
                                            ? ForecastSearchBoundary.TimeLimit
                                            : ForecastSearchBoundary.NodeLimit;
                                    }
                                    CaptureRemainingParents(parentIndex);
                                    break;
                                }
                            }
                            else if (CheckLimitReached(Math.Max(nodeBudget.Consumed, serialAttempts), budget, watch, ref passBoundary)
                                || budgetController.Spent >= budgetController.MemberCap)
                            {
                                if (passBoundary == ForecastSearchBoundary.None)
                                {
                                    passBoundary = watch.Elapsed >= budget.TimeLimit
                                        ? ForecastSearchBoundary.TimeLimit
                                        : ForecastSearchBoundary.NodeLimit;
                                }
                                CaptureRemainingParents(parentIndex);
                                break;
                            }

                            serialParents++;
                            ledger?.AddExpandedParents(1);
                            if (options.UseParentNodeBudget)
                                inCombatPassTrack?.ObserveExpansion(1, parent.Turn);

                            ParentExpansionResult result = ExpandForecastParentSync(
                                parent,
                                layer,
                                budget,
                                policy,
                                evaluator,
                                options,
                                budgetController,
                                admissionTable,
                                nodeBudget,
                                watch,
                                () => options.UseParentNodeBudget
                                    ? serialParents
                                    : Math.Max(nodeBudget.Consumed, serialAttempts),
                                turn =>
                                {
                                    serialParents++;
                                    ledger?.AddExpandedParents(1);
                                    if (options.UseParentNodeBudget)
                                        inCombatPassTrack?.ObserveExpansion(1, turn);
                                },
                                () =>
                                {
                                    serialAttempts++;
                                    ledger?.AddChoiceReplayAttempts(1);
                                    if (!options.UseParentNodeBudget)
                                        inCombatPassTrack?.ObserveExpansion(1, parent.Turn);
                                },
                                () =>
                                {
                                    serialTransitions++;
                                    ledger?.AddTransitions(1);
                                },
                                ct);

                            MergeParentResult(parent, result, next);
                            inCombatPassTrack?.ObserveParentResult(result);
                            if (result.LimitBoundary != ForecastSearchBoundary.None && passBoundary == ForecastSearchBoundary.None)
                                passBoundary = result.LimitBoundary;
                            if (result.AcceptableVictoryFound)
                            {
                                acceptableVictoryFound = true;
                                break;
                            }
                            if (passBoundary != ForecastSearchBoundary.None)
                            {
                                CaptureRemainingParents(parentIndex);
                                break;
                            }
                            if (result.ParentHitPendingChoice)
                                truncatedParents.Add(parent);
                        }
                    }
                    else
                    {
                        if (!waveExecutor.CheckExternalStop(ref passBoundary))
                        {
                            waveExecutor.RunWave(
                                active,
                                layer,
                                completed,
                                ended,
                                next,
                                truncatedParents,
                                ref passBoundary,
                                ref hitPendingChoiceBoundary,
                                ref acceptableVictoryFound,
                                candidate => ConsiderBoundary(candidate));
                        }
                        else
                        {
                            CaptureRemainingParents(0);
                        }
                    }

                    lastActive = SelectForecastFrontier(
                        next,
                        budget,
                        options.AggressivePowerCommitment,
                        options.SecondRankBand,
                        options.EnablePowerCommitmentSeats);
                    if (!options.DisablePrimaryIncumbentPruning && options.PrimaryIncumbent is not null)
                    {
                        lastActive = BeamIncumbentPolicy.ApplyIncumbentBound(
                            lastActive,
                            options.PrimaryIncumbent,
                            evaluator,
                            policy);
                    }

                    inCombatPassTrack?.CommitWave(
                        completed,
                        ended,
                        lastActive,
                        truncatedParents,
                        potionFreeBoundaryFallback,
                        potionBoundaryFallback,
                        passBoundary);
                    if (passBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
                    if (depth == 99 && active.Count > 0 && next.Count > 0)
                    {
                        passBoundary = ForecastSearchBoundary.TurnLimit;
                        break;
                    }
                    active = lastActive;

                    void CaptureRemainingParents(int startIndex)
                    {
                        for (int rem = startIndex; rem < active.Count; rem++)
                            truncatedParents.Add(active[rem]);
                    }

                    void MergeParentResult(SearchNode parent, ParentExpansionResult result, List<SearchNode> dest)
                    {
                        completed.AddRange(result.Completed);
                        ended.AddRange(result.Ended);
                        dest.AddRange(result.Next);
                        truncatedParents.AddRange(result.ExtraTruncatedParents);
                        if (result.AnyHitPendingChoice)
                            hitPendingChoiceBoundary = true;
                        if (result.PotionFreeBoundaryFallback is not null)
                            ConsiderBoundary(result.PotionFreeBoundaryFallback);
                        if (result.PotionBoundaryFallback is not null)
                            ConsiderBoundary(result.PotionBoundaryFallback);
                    }
                }

                frontier = SelectForecastFrontier(
                    ended,
                    budget,
                    options.AggressivePowerCommitment,
                    options.SecondRankBand,
                    options.EnablePowerCommitmentSeats);
                if (!options.DisablePrimaryIncumbentPruning && options.PrimaryIncumbent is not null)
                {
                    frontier = BeamIncumbentPolicy.ApplyIncumbentBound(
                        frontier,
                        options.PrimaryIncumbent,
                        evaluator,
                        policy);
                }

                if (options.EarlyTurnScoutDepth is int maxScoutDepth && frontier.Count > 0)
                {
                    int completedTurns = frontier[0].Turn - rootTurnNumber;
                    if (completedTurns >= 1 && completedTurns <= maxScoutDepth)
                    {
                        ct.ThrowIfCancellationRequested();
                        IReadOnlyList<EarlyTurnFrontierCandidate> earlyCandidates =
                            EarlyTurnFrontierSelector.Select(ended, completedTurns, rootTurnNumber, policy, ct);
                        options.EarlyTurnScoutObserver?.Invoke(completedTurns, earlyCandidates);
                        if (completedTurns >= maxScoutDepth)
                        {
                            passBoundary = ForecastSearchBoundary.TurnLimit;
                            inCombatPassTrack?.CommitWave(
                                completed,
                                frontier,
                                lastActive,
                                truncatedParents,
                                potionFreeBoundaryFallback,
                                potionBoundaryFallback,
                                passBoundary);
                            break;
                        }
                    }
                }

                inCombatPassTrack?.CommitWave(
                    completed,
                    frontier,
                    lastActive,
                    truncatedParents,
                    potionFreeBoundaryFallback,
                    potionBoundaryFallback,
                    passBoundary);
                inCombatPassTrack?.ObserveExpansion(0, frontier.Count > 0 ? frontier[0].Turn : 1);
                if (passBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
                if (layer == 99 && frontier.Count > 0)
                {
                    passBoundary = ForecastSearchBoundary.TurnLimit;
                }
            }
        }
        catch (OperationCanceledException)
        {
            long spent = waveExecutor is not null
                ? waveExecutor.SharedExpanded
                : (options.UseParentNodeBudget ? serialParents : Math.Max(nodeBudget.Consumed, serialAttempts));
            onCanceledExpanded?.Invoke(spent);
            throw;
        }
        finally
        {
            budgetController.ReclaimUnspent();
            ownedPool?.Dispose();
        }

        if (!acceptableVictoryFound && passBoundary == ForecastSearchBoundary.None)
        {
            if (frontier.Count > 0 || lastActive.Count > 0)
            {
                passBoundary = watch.Elapsed >= budget.TimeLimit
                    ? ForecastSearchBoundary.TimeLimit
                    : ForecastSearchBoundary.TurnLimit;
            }
            else if (hitPendingChoiceBoundary)
            {
                passBoundary = ForecastSearchBoundary.PendingChoice;
            }
        }

        var rawCandidates = new List<SearchNode>(
            completed.Count + frontier.Count + lastActive.Count + truncatedParents.Count + 2);
        rawCandidates.AddRange(completed);
        rawCandidates.AddRange(frontier);
        rawCandidates.AddRange(lastActive);
        rawCandidates.AddRange(truncatedParents);
        if (potionFreeBoundaryFallback is not null)
            rawCandidates.Add(potionFreeBoundaryFallback);
        if (potionBoundaryFallback is not null)
            rawCandidates.Add(potionBoundaryFallback);

        long finalParents = waveExecutor is not null ? waveExecutor.SharedParents : serialParents;
        long finalTransitions = waveExecutor is not null ? waveExecutor.SharedTransitions : serialTransitions;
        long finalAttempts = waveExecutor is not null ? waveExecutor.SharedAttempts : serialAttempts;
        long finalExpanded = options.UseParentNodeBudget
            ? finalParents
            : Math.Max(nodeBudget.Consumed, finalAttempts);

        ForecastPassOutcome? outcome = EvaluateForecastFinalPoolOrNull(
            rawCandidates,
            initialHp,
            rootInitialCumulativeHpLost,
            evaluator,
            passBoundary,
            finalExpanded,
            acceptableVictoryFound,
            MeetsAcceptableVictoryLocal,
            options.AllowPolicyUnsatisfiedSelection,
            ct, captureInCombatPool: inCombatPassTrack is not null);
        if (outcome is null)
            return null;

        outcome = outcome with
        {
            ExpandedParents = finalParents,
            TransitionCount = finalTransitions,
            ChoiceReplayAttempts = finalAttempts,
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
        };
        inCombatPassTrack?.Complete(outcome);
        return outcome;

        bool MeetsAcceptableVictoryLocal(SearchNode node) =>
            !options.DisableTargetStop
            && MeetsAcceptableVictory(
                node,
                policy,
                evaluator,
                options.OpeningPreRootBattleHpLost);

        void ConsiderBoundary(SearchNode candidate)
        {
            if (candidate.PotionUses == 0)
            {
                if (potionFreeBoundaryFallback is null
                    || candidate.Score > potionFreeBoundaryFallback.Score)
                    potionFreeBoundaryFallback = candidate;
            }
            else if (potionBoundaryFallback is null || candidate.Score > potionBoundaryFallback.Score)
            {
                potionBoundaryFallback = candidate;
            }
        }
    }

    internal static ForecastPassOutcome EvaluateForecastFinalPool(
        IEnumerable<SearchNode> rawCandidates,
        int initialHp,
        int rootInitialCumulativeHpLost,
        StateEvaluator evaluator,
        ForecastSearchBoundary passBoundary,
        long expanded,
        bool acceptableVictoryFound,
        Func<SearchNode, bool> meetsAcceptableVictory,
        CancellationToken ct) =>
        EvaluateForecastFinalPoolOrNull(
            rawCandidates,
            initialHp,
            rootInitialCumulativeHpLost,
            evaluator,
            passBoundary,
            expanded,
            acceptableVictoryFound,
            meetsAcceptableVictory,
            allowPolicyUnsatisfied: false,
            ct)!;

    internal static ForecastPassOutcome? EvaluateForecastFinalPoolOrNull(
        IEnumerable<SearchNode> rawCandidates,
        int initialHp,
        int rootInitialCumulativeHpLost,
        StateEvaluator evaluator,
        ForecastSearchBoundary passBoundary,
        long expanded,
        bool acceptableVictoryFound,
        Func<SearchNode, bool> meetsAcceptableVictory,
        bool allowPolicyUnsatisfied,
        CancellationToken ct,
        bool captureInCombatPool = false)
    {
        var fullPool = new List<SearchNode>();
        var seenNodes = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
        foreach (SearchNode candidate in rawCandidates)
        {
            if (seenNodes.Add(candidate))
                fullPool.Add(candidate);
        }

        if (fullPool.Count == 0)
            throw new InvalidOperationException("Forecast beam search produced an empty final candidate pool.");

        var evaluatedCandidates = new EvaluatedForecastCandidate[fullPool.Count];
        for (int i = 0; i < fullPool.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SearchNode candidate = fullPool[i];
            int projectedHp = evaluator.ComputeProjectedPlayerHp(candidate, candidate.NodeBoundary);
            evaluatedCandidates[i] = new EvaluatedForecastCandidate(candidate, candidate.NodeBoundary, projectedHp);
        }

        IEnumerable<SearchNode> selectionPool;
        if (acceptableVictoryFound)
        {
            var winning = new List<SearchNode>();
            for (int i = 0; i < fullPool.Count; i++)
            {
                if (meetsAcceptableVictory(fullPool[i]))
                    winning.Add(fullPool[i]);
            }
            selectionPool = winning;
        }
        else
        {
            selectionPool = fullPool;
        }

        SearchNode[] eligible;
        SearchNode? selected = allowPolicyUnsatisfied
            ? PlanSelector.TrySelectWithPool(selectionPool, initialHp, evaluator, out eligible, ct)
            : PlanSelector.SelectWithPool(selectionPool, initialHp, evaluator, out eligible);
        if (selected is null)
            return null;

        ForecastSearchBoundary outcomeBoundary = selected.Won
            ? ForecastSearchBoundary.None
            : selected.NodeBoundary != ForecastSearchBoundary.None
                ? selected.NodeBoundary
                : passBoundary;

        return new ForecastPassOutcome(
            selected,
            evaluatedCandidates,
            outcomeBoundary,
            expanded,
            acceptableVictoryFound,
            rootInitialCumulativeHpLost,
            evaluator)
        {
            InCombatSelectionPool = captureInCombatPool ? (eligible, initialHp, evaluator) : null,
        };
    }
}
