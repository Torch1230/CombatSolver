using System.Diagnostics;
using Sts2Sim.Core.Combat;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal readonly record struct PortfolioMemberRun(
    ForecastPassOutcome? Outcome,
    long ExpandedNodes);

internal static class PreCombatBeamPortfolio
{
    private static readonly TimeSpan PartialTraceFallbackBudget = TimeSpan.FromMilliseconds(250);

    public static async Task<BeamForecastResult> SolveAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1)
    {
        var ledger = new SearchWorkLedger(portfolio.UsesParentNodeBudget);
        PortfolioCoordinatorResult result = await BeamWidthPortfolioRunner.SolveAsync(
            root,
            budget,
            policy,
            portfolio,
            openingPreRootBattleHpLost,
            watch,
            ledger,
            tracker: null,
            ct,
            maxDegreeOfParallelism,
            rankByBaseAttackIntent: true).ConfigureAwait(false);
        return BeamSearch.BuildForecastResult(
            result.ChosenPass,
            result.CombinedCandidates,
            result.TotalExpandedNodes,
            openingPreRootBattleHpLost,
            watch.Elapsed.TotalMilliseconds);
    }

    public static Task<InCombatBeamResult> SolveForInCombatAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int priorBattleHpLost,
        Stopwatch watch,
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
            watch,
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
        Stopwatch watch,
        Func<InCombatProgressUpdate, bool>? progressCallback,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1,
        Action<InCombatBeamResult>? onPartialResult = null,
        CancellationToken hardDeadlineToken = default)
    {
        var ledger = new SearchWorkLedger(portfolio.UsesParentNodeBudget);
        bool emitExtendedMetrics = portfolio.UsesParentNodeBudget;
        var tracker = InCombatSearchTracker.CreateWithDeliveryTracking(
            root,
            priorBattleHpLost,
            watch,
            progressCallback,
            searchCancellation: ct,
            hardDeadlineToken: hardDeadlineToken,
            ledger: ledger,
            emitExtendedMetrics: emitExtendedMetrics);
        try
        {
            PortfolioCoordinatorResult result = await BeamWidthPortfolioRunner.SolveAsync(
                root,
                budget,
                policy,
                portfolio,
                openingPreRootBattleHpLost: priorBattleHpLost,
                watch,
                ledger,
                tracker,
                ct,
                maxDegreeOfParallelism).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            hardDeadlineToken.ThrowIfCancellationRequested();
            tracker.StopRegularSampling();
            SearchWorkSnapshot workSnapshot = ledger.Snapshot();
            InCombatBeamResult built = InCombatRouteTracer.BuildResult(
                root,
                result.ChosenPass,
                result.CombinedCandidates,
                result.TotalExpandedNodes,
                priorBattleHpLost,
                watch.Elapsed.TotalMilliseconds,
                traceCancellation: ct,
                workSnapshot: emitExtendedMetrics ? workSnapshot : null);
            ct.ThrowIfCancellationRequested();
            hardDeadlineToken.ThrowIfCancellationRequested();
            tracker.StopAndJoin(hardDeadlineToken, emitFinalMetricsFlush: false, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            hardDeadlineToken.ThrowIfCancellationRequested();
            return built;
        }
        catch (OperationCanceledException)
        {
            tracker.StopAndJoin(hardDeadlineToken, emitFinalMetricsFlush: true, cancellationToken: ct);
            if (onPartialResult is not null
                && tracker.TryGetBestSoFarOnCancel(hardDeadlineToken, PartialTraceFallbackBudget) is { } partial)
            {
                onPartialResult(partial);
            }
            throw;
        }
        finally
        {
            tracker.StopAndJoin(hardDeadlineToken, emitFinalMetricsFlush: false, cancellationToken: ct);
        }
    }

    internal static async Task<PortfolioMemberRun> RunPortfolioMemberAsync(
        CombatState clonedRoot,
        BeamBudget memberBudget,
        CombatPolicy policy,
        PolicyRootOperands rootPolicyOperands,
        bool aggressivePowerCommitment,
        int openingPreRootBattleHpLost,
        int memberDop,
        CancellationTokenSource earlyStopCts,
        CancellationToken outerCt,
        ForecastNodeBudgetTracker? budgetTracker = null,
        InCombatPassTrack? passTrack = null,
        StateEvaluatorBaseline? evaluatorBaseline = null)
    {
        ForecastNodeBudgetTracker memberTracker = budgetTracker ?? new ForecastNodeBudgetTracker(memberBudget.NodeLimit);
        try
        {
            ForecastPassOutcome outcome = await BeamSearch.RunSingleForecastPassAsync(
                clonedRoot,
                memberBudget,
                policy,
                earlyStopCts.Token,
                aggressivePowerCommitment: aggressivePowerCommitment,
                openingPreRootBattleHpLost: openingPreRootBattleHpLost,
                maxDegreeOfParallelism: memberDop,
                preCapturedPolicyOperands: rootPolicyOperands,
                budgetTracker: memberTracker,
                inCombatPassTrack: passTrack,
                evaluatorBaseline: evaluatorBaseline).ConfigureAwait(false);
            if (outcome.AcceptableVictoryFound)
                earlyStopCts.Cancel();
            return new PortfolioMemberRun(outcome, memberTracker.Consumed);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested && earlyStopCts.IsCancellationRequested)
        {
            // fail-open: 并行组合成员命中可接受胜利触发 earlyStopCts 提前中止其余分支，保留已消耗节点数并由已获胜成员完成返回。
            return new PortfolioMemberRun(null, memberTracker.Consumed);
        }
    }

    internal static int ComputeMember0NodeLimit(int nodeLimit, bool hasReachablePower) =>
        hasReachablePower
            ? checked((int)Math.Max(1L, (long)nodeLimit * 40L / 100L))
            : (nodeLimit < 4000 ? nodeLimit : nodeLimit / 2);

    internal static ForecastPassOutcome ChooseWinningPass(
        IReadOnlyList<ForecastPassOutcome> passes,
        int initialHp,
        StateEvaluator evaluator) =>
        ChooseWinningPassWithOwner(passes, initialHp, evaluator).Chosen;

    internal static (ForecastPassOutcome Chosen, int WinnerPassIndex) ChooseWinningPassWithOwner(
        IReadOnlyList<ForecastPassOutcome> passes,
        int initialHp,
        StateEvaluator evaluator)
    {
        if (passes.Count == 0)
            throw new ArgumentException("At least one forecast pass outcome is required.", nameof(passes));
        if (passes.Count == 1)
            return (passes[0], 0);

        bool anyPassWon = passes.Any(pass => pass.Selected.Won);
        var combinedNodes = new List<SearchNode>();
        var ownerByNode = new Dictionary<SearchNode, (ForecastPassOutcome Pass, int Index)>(
            ReferenceEqualityComparer.Instance);

        for (int index = 0; index < passes.Count; index++)
        {
            ForecastPassOutcome pass = passes[index];
            // 若已有成员找到完整胜利，未获胜成员及其候选不参与终选，防止非胜局候选干扰完整胜利。
            if (anyPassWon && !pass.Selected.Won)
                continue;

            if (ownerByNode.TryAdd(pass.Selected, (pass, index)))
                combinedNodes.Add(pass.Selected);

            if (pass.SharedPotionFreeBaseline is { } carriedBaseline
                && ownerByNode.TryAdd(carriedBaseline, (pass, index)))
            {
                combinedNodes.Add(carriedBaseline);
            }

            if (pass.AcceptableVictoryFound)
            {
                if (pass.Selected.PotionUses > 0
                    && SelectBestPotionFreeCandidate(pass.EvaluatedCandidates, initialHp, evaluator) is { } acceptableBaseline
                    && ownerByNode.TryAdd(acceptableBaseline, (pass, index)))
                {
                    combinedNodes.Add(acceptableBaseline);
                }
                continue;
            }

            foreach (EvaluatedForecastCandidate candidate in pass.EvaluatedCandidates)
            {
                if (ownerByNode.TryAdd(candidate.Node, (pass, index)))
                    combinedNodes.Add(candidate.Node);
            }
        }

        SearchNode? sharedPotionFree = PlanSelector.SelectPotionFreeBaseline(combinedNodes, initialHp, evaluator);
        SearchNode winner = PlanSelector.SelectWithPool(combinedNodes, initialHp, evaluator, out var eligible);
        (ForecastPassOutcome owner, int winnerIndex) = ownerByNode[winner];
        SearchNode? carriedOnOwner = ShouldAttachSharedPotionFreeBaseline(owner, sharedPotionFree)
            ? sharedPotionFree
            : owner.SharedPotionFreeBaseline;
        ForecastPassOutcome chosen = ReferenceEquals(winner, owner.Selected)
            && ReferenceEquals(carriedOnOwner, owner.SharedPotionFreeBaseline)
            ? owner
            : owner with
            {
                Selected = winner,
                SharedPotionFreeBaseline = carriedOnOwner,
            };
        if (owner.InCombatSelectionPool is not null && winner.State.Players[0].Creature.IsDead)
            chosen = chosen with { InCombatSelectionPool = (eligible, initialHp, evaluator) };
        return (chosen, winnerIndex);
    }

    private static SearchNode? SelectBestPotionFreeCandidate(
        EvaluatedForecastCandidate[] candidates,
        int initialHp,
        StateEvaluator evaluator)
    {
        if (candidates.Length == 0)
            return null;

        var nodes = new SearchNode[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
            nodes[i] = candidates[i].Node;
        return PlanSelector.SelectPotionFreeBaseline(nodes, initialHp, evaluator);
    }

    private static bool ShouldAttachSharedPotionFreeBaseline(
        ForecastPassOutcome owner,
        SearchNode? sharedPotionFree)
    {
        if (sharedPotionFree is null
            || ReferenceEquals(owner.Selected, sharedPotionFree)
            || ReferenceEquals(owner.SharedPotionFreeBaseline, sharedPotionFree))
        {
            return false;
        }

        if (!owner.AcceptableVictoryFound)
        {
            EvaluatedForecastCandidate[] evals = owner.EvaluatedCandidates;
            for (int i = 0; i < evals.Length; i++)
            {
                if (ReferenceEquals(evals[i].Node, sharedPotionFree))
                    return false;
            }
        }

        return true;
    }
}
