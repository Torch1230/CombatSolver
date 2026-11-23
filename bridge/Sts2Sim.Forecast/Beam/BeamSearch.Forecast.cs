using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Cards;
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
    StateEvaluator Evaluator);

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

    internal static async Task<InCombatBeamResult> SolveForInCombatAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int priorBattleHpLost,
        Action<InCombatProgressUpdate>? progressCallback,
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

        return await PreCombatBeamPortfolio.SolveForInCombatAsync(
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

        bool onlyDeathRoutes = allEvaluatedCandidates.All(candidate =>
            !candidate.Node.State.Players[0].Creature.IsAlive ||
            candidate.Node.State.Players[0].Creature.CurrentHp <= 0 ||
            candidate.ProjectedPlayerHp <= 0);

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

        EvaluatedForecastCandidate selectedCandidate = allEvaluatedCandidates
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Node, selected));
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

        ForecastPotionUse[] potionUses = route
            .Where(action => action.Kind == "UsePotion" && !string.IsNullOrWhiteSpace(action.PotionId))
            .Select(action => new ForecastPotionUse(
                action.PotionId!,
                Math.Max(1, action.Turn),
                action.PotionSlot))
            .ToArray();

        return new BeamForecastResult(
            route,
            selected.Won,
            projectedHpLoss,
            finalHp,
            combatEndedTurn,
            onlyDeathRoutes,
            confidence,
            boundary,
            potionUses,
            totalExpandedNodes,
            searchElapsedMilliseconds);
    }

    internal static async Task<ForecastPassOutcome> RunSingleForecastPassAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        CancellationToken ct,
        bool aggressivePowerCommitment = false,
        int openingPreRootBattleHpLost = 0,
        int maxDegreeOfParallelism = 1,
        PolicyRootOperands? preCapturedPolicyOperands = null,
        ForecastNodeBudgetTracker? budgetTracker = null,
        InCombatPassTrack? inCombatPassTrack = null)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        if (budgetTracker is not null && budgetTracker.NodeLimit != budget.NodeLimit)
            throw new ArgumentException("Budget tracker node limit must match budget.NodeLimit.", nameof(budgetTracker));

        ct.ThrowIfCancellationRequested();
        int effectiveDop = maxDegreeOfParallelism;
        int initialHp = root.Players[0].Creature.CurrentHp;
        int rootInitialCumulativeHpLost = root.Players[0].Creature.CumulativeHpLost;
        var evaluator = new StateEvaluator(root, policy, aggressivePowerCommitment, preCapturedPolicyOperands);
        inCombatPassTrack?.AttachEvaluator(evaluator, initialHp);
        SearchNode start = SearchNode.Create(root.Clone(), null, null, evaluator);
        var frontier = new List<SearchNode> { start };
        var completed = new List<SearchNode>();
        var lastActive = new List<SearchNode>();
        var truncatedParents = new List<SearchNode>();
        var watch = Stopwatch.StartNew();
        ForecastNodeBudgetTracker nodeBudget = budgetTracker ?? new ForecastNodeBudgetTracker(budget.NodeLimit);
        inCombatPassTrack?.AttachBudgetTracker(nodeBudget);
        ForecastSearchBoundary passBoundary = ForecastSearchBoundary.None;
        bool hitPendingChoiceBoundary = false;
        bool acceptableVictoryFound = false;
        SearchNode? potionFreeBoundaryFallback = null;
        SearchNode? potionBoundaryFallback = null;

        for (int layer = 0; layer < 100 && frontier.Count > 0; layer++)
        {
            ct.ThrowIfCancellationRequested();
            List<SearchNode> active = frontier;
            var ended = new List<SearchNode>();
            for (int depth = 0; depth < 100 && active.Count > 0 && !acceptableVictoryFound; depth++)
            {
                ct.ThrowIfCancellationRequested();
                var next = new List<SearchNode>();

                if (effectiveDop <= 1 || active.Count <= 1)
                {
                    for (int parentIndex = 0; parentIndex < active.Count; parentIndex++)
                    {
                        SearchNode parent = active[parentIndex];
                        ct.ThrowIfCancellationRequested();
                        if (CheckLimitReached(nodeBudget.Consumed, budget, watch, ref passBoundary))
                        {
                            CaptureRemainingParents(parentIndex);
                            break;
                        }

                        ParentExpansionResult result = await ExpandForecastParentAsync(
                            parent,
                            layer,
                            budget,
                            policy,
                            evaluator,
                            openingPreRootBattleHpLost,
                            watch,
                            nodeBudget,
                            () => false,
                            () => false,
                            ct,
                            inCombatPassTrack is null ? null : delta => inCombatPassTrack.ObserveExpansion(delta, parent.Turn)).ConfigureAwait(false);

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
                    var waveResults = new ParentExpansionResult?[active.Count];
                    int victoryFoundFlag = 0;
                    int limitReachedFlag = 0;
                    int recordedLimitBoundary = 0;
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(effectiveDop, active.Count),
                        CancellationToken = ct,
                    };

                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, active.Count),
                        parallelOptions,
                        async (parentIndex, waveCt) =>
                        {
                            waveCt.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref victoryFoundFlag) != 0)
                                return;

                            ForecastSearchBoundary preBoundary = ForecastSearchBoundary.None;
                            if (Volatile.Read(ref limitReachedFlag) != 0
                                || CheckLimitReached(nodeBudget.Consumed, budget, watch, ref preBoundary))
                            {
                                if (preBoundary != ForecastSearchBoundary.None)
                                {
                                    Interlocked.CompareExchange(ref recordedLimitBoundary, (int)preBoundary, 0);
                                    Interlocked.Exchange(ref limitReachedFlag, 1);
                                }
                                return;
                            }

                            SearchNode waveParent = active[parentIndex];
                            ParentExpansionResult result = await ExpandForecastParentAsync(
                                waveParent,
                                layer,
                                budget,
                                policy,
                                evaluator,
                                openingPreRootBattleHpLost,
                                watch,
                                nodeBudget,
                                () => Volatile.Read(ref victoryFoundFlag) != 0 || Volatile.Read(ref limitReachedFlag) != 0,
                                () => Volatile.Read(ref victoryFoundFlag) == 0 && Volatile.Read(ref limitReachedFlag) != 0,
                                waveCt,
                                inCombatPassTrack is null ? null : delta => inCombatPassTrack.ObserveExpansion(delta, waveParent.Turn)).ConfigureAwait(false);

                            waveResults[parentIndex] = result;
                            inCombatPassTrack?.ObserveParentResult(result);
                            if (result.AcceptableVictoryFound)
                                Interlocked.Exchange(ref victoryFoundFlag, 1);
                            if (result.LimitBoundary != ForecastSearchBoundary.None)
                            {
                                Interlocked.CompareExchange(ref recordedLimitBoundary, (int)result.LimitBoundary, 0);
                                Interlocked.Exchange(ref limitReachedFlag, 1);
                            }
                        }).ConfigureAwait(false);

                    ct.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref victoryFoundFlag) != 0)
                        acceptableVictoryFound = true;
                    int rawRecordedBoundary = Volatile.Read(ref recordedLimitBoundary);
                    if (!acceptableVictoryFound && rawRecordedBoundary != 0 && passBoundary == ForecastSearchBoundary.None)
                        passBoundary = (ForecastSearchBoundary)rawRecordedBoundary;

                    for (int parentIndex = 0; parentIndex < active.Count; parentIndex++)
                    {
                        SearchNode parent = active[parentIndex];
                        ParentExpansionResult? result = waveResults[parentIndex];
                        if (result is null)
                        {
                            if (!acceptableVictoryFound)
                                truncatedParents.Add(parent);
                            continue;
                        }

                        MergeParentResult(parent, result, next);
                        bool incompleteParent = result.LimitBoundary != ForecastSearchBoundary.None
                            || result.StoppedByExternalLimit;
                        if (!acceptableVictoryFound && incompleteParent)
                            truncatedParents.Add(parent);
                        else if (result.ParentHitPendingChoice)
                            truncatedParents.Add(parent);
                    }
                }

                lastActive = SelectForecastFrontier(next, budget, aggressivePowerCommitment);
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

                void MergeParentResult(SearchNode parent, ParentExpansionResult result, List<SearchNode> next)
                {
                    completed.AddRange(result.Completed);
                    ended.AddRange(result.Ended);
                    next.AddRange(result.Next);
                    truncatedParents.AddRange(result.ExtraTruncatedParents);
                    if (result.AnyHitPendingChoice)
                        hitPendingChoiceBoundary = true;
                    if (result.PotionFreeBoundaryFallback is not null)
                        ConsiderBoundary(result.PotionFreeBoundaryFallback, parent);
                    if (result.PotionBoundaryFallback is not null)
                        ConsiderBoundary(result.PotionBoundaryFallback, parent);
                }
            }

            frontier = SelectForecastFrontier(ended, budget, aggressivePowerCommitment);
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

        ForecastPassOutcome outcome = EvaluateForecastFinalPool(
            rawCandidates,
            initialHp,
            rootInitialCumulativeHpLost,
            evaluator,
            passBoundary,
            nodeBudget.Consumed,
            acceptableVictoryFound,
            MeetsAcceptableVictory,
            ct);
        inCombatPassTrack?.Complete(outcome);
        return outcome;

        bool MeetsAcceptableVictory(SearchNode node) => BeamSearch.MeetsAcceptableVictory(
            node,
            policy,
            evaluator,
            openingPreRootBattleHpLost);

        void ConsiderBoundary(SearchNode candidate, SearchNode unusedParent)
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

    private static async Task<ParentExpansionResult> ExpandForecastParentAsync(
        SearchNode parent,
        int layer,
        BeamBudget budget,
        CombatPolicy policy,
        StateEvaluator evaluator,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        ForecastNodeBudgetTracker nodeBudget,
        Func<bool> shouldStopExternal,
        Func<bool> isExternalLimitReached,
        CancellationToken ct,
        Action<long>? onExpansionAttempts = null)
    {
        var cardChildren = new List<SearchNode>();
        var completed = new List<SearchNode>();
        var ended = new List<SearchNode>();
        var next = new List<SearchNode>();
        var extraTruncatedParents = new List<SearchNode>();
        bool parentHitPendingChoice = false;
        bool anyHitPendingChoice = false;
        bool acceptableVictoryFound = false;
        bool stoppedByExternalLimit = false;
        ForecastSearchBoundary limitBoundary = ForecastSearchBoundary.None;
        SearchNode? potionFreeBoundaryFallback = null;
        SearchNode? potionBoundaryFallback = null;

        bool CheckLocalLimit() => CheckLimitReached(nodeBudget.Consumed, budget, watch, ref limitBoundary);
        bool MeetsLocalVictory(SearchNode node) => MeetsAcceptableVictory(node, policy, evaluator, openingPreRootBattleHpLost);

        void ConsiderLocalBoundary(SearchNode child, SearchNode sourceParent)
        {
            if (!child.Ended && child.Turn == sourceParent.Turn && child.NodeBoundary == ForecastSearchBoundary.None) return;
            if (child.PotionUses == 0)
            {
                if (potionFreeBoundaryFallback is null || child.Score > potionFreeBoundaryFallback.Score)
                    potionFreeBoundaryFallback = child;
            }
            else if (potionBoundaryFallback is null || child.Score > potionBoundaryFallback.Score)
            {
                potionBoundaryFallback = child;
            }
        }

        foreach (PlannedAction action in ActionGenerator.GenerateForForecast(parent.State, policy))
        {
            ct.ThrowIfCancellationRequested();
            if (shouldStopExternal())
            {
                if (isExternalLimitReached()) stoppedByExternalLimit = true;
                break;
            }
            if (CheckLocalLimit())
                break;

            ChoiceExpansion.Expansion expansion = await ChoiceExpansion.ExpandAsync(
                parent, action, budget, evaluator, nodeBudget.TryReserveSlot, watch, ct).ConfigureAwait(false);
            onExpansionAttempts?.Invoke(expansion.Attempts);
            if (expansion.HitPendingChoiceBoundary)
            {
                anyHitPendingChoice = true;
                parentHitPendingChoice = true;
            }
            if (expansion.BudgetTruncated && limitBoundary == ForecastSearchBoundary.None)
            {
                limitBoundary = watch.Elapsed >= budget.TimeLimit
                    ? ForecastSearchBoundary.TimeLimit
                    : ForecastSearchBoundary.NodeLimit;
            }

            foreach (SearchNode child in expansion.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (action.Kind == "PlayCard" && child.NodeBoundary == ForecastSearchBoundary.None)
                    cardChildren.Add(child);
                else
                    ClassifyForecastChild(child, parent, completed, ended, next);
                ConsiderLocalBoundary(child, parent);
                if (MeetsLocalVictory(child)) acceptableVictoryFound = true;
            }
            if (limitBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
            if (shouldStopExternal())
            {
                if (isExternalLimitReached()) stoppedByExternalLimit = true;
                break;
            }
        }

        if (policy.Act3BossStrategy && layer == 0 && limitBoundary == ForecastSearchBoundary.None
            && !acceptableVictoryFound && !shouldStopExternal())
        {
            foreach (SearchNode commitment in cardChildren
                .Where(node => node.NodeBoundary == ForecastSearchBoundary.None && HasPlayableFetchedPower(node))
                .ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (shouldStopExternal())
                {
                    if (isExternalLimitReached()) stoppedByExternalLimit = true;
                    break;
                }
                if (CheckLocalLimit())
                    break;

                bool commitmentHitPendingChoice = false;
                foreach (PlannedAction followup in ActionGenerator.GenerateForForecast(commitment.State, policy))
                {
                    ct.ThrowIfCancellationRequested();
                    if (shouldStopExternal())
                    {
                        if (isExternalLimitReached()) stoppedByExternalLimit = true;
                        break;
                    }
                    if (CheckLocalLimit())
                        break;

                    ChoiceExpansion.Expansion expansion = await ChoiceExpansion.ExpandAsync(
                        commitment, followup, budget, evaluator, nodeBudget.TryReserveSlot, watch, ct).ConfigureAwait(false);
                    onExpansionAttempts?.Invoke(expansion.Attempts);
                    if (expansion.HitPendingChoiceBoundary)
                    {
                        anyHitPendingChoice = true;
                        commitmentHitPendingChoice = true;
                    }
                    if (expansion.BudgetTruncated && limitBoundary == ForecastSearchBoundary.None)
                    {
                        limitBoundary = watch.Elapsed >= budget.TimeLimit
                            ? ForecastSearchBoundary.TimeLimit
                            : ForecastSearchBoundary.NodeLimit;
                    }

                    foreach (SearchNode successor in expansion.Children)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (followup.Kind == "PlayCard" && successor.NodeBoundary == ForecastSearchBoundary.None)
                            cardChildren.Add(successor);
                        else
                            ClassifyForecastChild(successor, commitment, completed, ended, next);
                        ConsiderLocalBoundary(successor, commitment);
                        if (MeetsLocalVictory(successor)) acceptableVictoryFound = true;
                    }
                    if (limitBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
                    if (shouldStopExternal())
                    {
                        if (isExternalLimitReached()) stoppedByExternalLimit = true;
                        break;
                    }
                }
                if (commitmentHitPendingChoice)
                    extraTruncatedParents.Add(commitment);
                cardChildren.Remove(commitment);
                if (limitBoundary != ForecastSearchBoundary.None || acceptableVictoryFound || stoppedByExternalLimit) break;
            }
        }

        foreach (SearchNode child in DeduplicateCardChildren(cardChildren, budget.MaxCardBranches))
        {
            ct.ThrowIfCancellationRequested();
            ClassifyForecastChild(child, parent, completed, ended, next);
        }
        if (cardChildren.Any(MeetsLocalVictory)) acceptableVictoryFound = true;

        return new ParentExpansionResult(
            completed,
            ended,
            next,
            extraTruncatedParents,
            potionFreeBoundaryFallback,
            potionBoundaryFallback,
            parentHitPendingChoice,
            anyHitPendingChoice,
            acceptableVictoryFound,
            limitBoundary,
            stoppedByExternalLimit);
    }

    private static bool CheckLimitReached(
        long currentExpanded,
        BeamBudget activeBudget,
        Stopwatch timer,
        ref ForecastSearchBoundary boundary)
    {
        if (timer.Elapsed >= activeBudget.TimeLimit)
        {
            boundary = ForecastSearchBoundary.TimeLimit;
            return true;
        }
        if (currentExpanded >= activeBudget.NodeLimit)
        {
            boundary = ForecastSearchBoundary.NodeLimit;
            return true;
        }
        return false;
    }

    private static bool HasPlayableFetchedPower(SearchNode node)
    {
        if (node.Action?.ChoiceCardIds.Count is not > 0) return false;
        var beforeHand = node.Parent?.State.Players[0].PlayerCombatState?.Hand.Cards;
        foreach (var card in node.State.Players[0].PlayerCombatState!.Hand.Cards)
        {
            if (card.Type != CardType.Power || !card.CanPlay(out _)
                || !node.Action.ChoiceCardIds.Contains(card.Id.Entry)) continue;
            int beforeCount = beforeHand?.Count(other => other.Id.Entry == card.Id.Entry) ?? 0;
            int afterCount = node.State.Players[0].PlayerCombatState!.Hand.Cards
                .Count(other => other.Id.Entry == card.Id.Entry);
            if (afterCount > beforeCount) return true;
        }
        return false;
    }

    internal static bool MeetsAcceptableVictory(
        SearchNode node,
        CombatPolicy policy,
        StateEvaluator evaluator,
        int openingPreRootBattleHpLost) =>
        policy.StopAtAcceptableBattleHpLoss
        && node.Won
        && node.DeathSaveUseCount == 0
        && evaluator.SatisfiesPotionPolicy(node)
        && node.PotionUses <= evaluator.MinimumRequiredPotionUses
        && evaluator.SatisfiesTheftPolicy(node)
        && evaluator.SatisfiesRelicTargets(node)
        && evaluator.SatisfiesGrowthTargets(node)
        && checked(openingPreRootBattleHpLost + evaluator.BattleHpLost(node)) <= policy.AcceptableBattleHpLoss;

    private static void ClassifyForecastChild(
        SearchNode child,
        SearchNode parent,
        List<SearchNode> completed,
        List<SearchNode> ended,
        List<SearchNode> next)
    {
        if (child.Ended || child.NodeBoundary != ForecastSearchBoundary.None)
            completed.Add(child);
        else if (child.Turn != parent.Turn)
            ended.Add(child);
        else
            next.Add(child);
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
        CancellationToken ct)
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

        IEnumerable<SearchNode> selectionPool = acceptableVictoryFound
            ? fullPool.Where(meetsAcceptableVictory)
            : fullPool;
        SearchNode selected = PlanSelector.Select(selectionPool, initialHp, evaluator);

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
            evaluator);
    }

    private static IEnumerable<SearchNode> DeduplicateCardChildren(
        IReadOnlyList<SearchNode> cardChildren,
        int maxCardBranches)
    {
        if (cardChildren.Count <= 1)
            return cardChildren.Take(maxCardBranches);

        var seenExact = new HashSet<CombatStateDescriptionDigest>();
        var representedBuckets = new HashSet<CombatStateDescriptionDigest>();
        var result = new List<SearchNode>(Math.Min(cardChildren.Count, maxCardBranches));
        (SearchNode Node, CombatStateDescriptionDigest Key)[] ordered = cardChildren
            .Select(node => (Node: node, Key: ForecastRetentionKey.Compute(node)))
            .OrderByDescending(entry => entry.Node.Score)
            .ThenBy(entry => entry.Node.Depth)
            .ThenBy(entry => entry.Key.First)
            .ThenBy(entry => entry.Key.Second)
            .ThenBy(entry => entry.Node.Fingerprint.First)
            .ThenBy(entry => entry.Node.Fingerprint.Second)
            .ToArray();

        foreach ((SearchNode node, CombatStateDescriptionDigest key) in ordered)
        {
            if (seenExact.Contains(node.Fingerprint) || !representedBuckets.Add(key))
                continue;
            seenExact.Add(node.Fingerprint);
            result.Add(node);
            if (result.Count >= maxCardBranches)
                return result;
        }

        foreach ((SearchNode node, _) in ordered)
        {
            if (!seenExact.Add(node.Fingerprint))
                continue;
            result.Add(node);
            if (result.Count >= maxCardBranches)
                break;
        }

        return result;
    }

    private static List<SearchNode> SelectForecastFrontier(
        IEnumerable<SearchNode> candidates,
        BeamBudget budget,
        bool aggressivePowerCommitment = false)
    {
        if (budget.PotionFreeBeamWidth is null && budget.PotionBeamWidth is null)
        {
            return Retention.SelectWithKey(
                candidates,
                budget.BeamWidth,
                ForecastRetentionKey.Compute,
                includeParetoProgress: true,
                aggressivePowerCommitment: aggressivePowerCommitment);
        }

        SearchNode[] pool = candidates.ToArray();
        int freeWidth = budget.PotionFreeBeamWidth ?? budget.BeamWidth;
        int potionWidth = budget.PotionBeamWidth ?? budget.BeamWidth;
        return Retention.SelectWithKey(
                pool.Where(node => node.PotionUses == 0),
                freeWidth,
                ForecastRetentionKey.Compute,
                includeParetoProgress: true,
                aggressivePowerCommitment: aggressivePowerCommitment)
            .Concat(Retention.SelectWithKey(
                pool.Where(node => node.PotionUses > 0),
                potionWidth,
                ForecastRetentionKey.Compute,
                includeParetoProgress: true,
                aggressivePowerCommitment: aggressivePowerCommitment))
            .ToList();
    }
}
