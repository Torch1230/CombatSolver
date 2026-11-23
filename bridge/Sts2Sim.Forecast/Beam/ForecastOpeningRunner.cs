using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Models;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal sealed class ForecastOpeningPlanningRequiredException : Exception;

internal sealed class ForecastOpeningRunner(CancellationToken cancellationToken)
    : ICardSelectionDecisionSource, ICombatOpeningPlanner
{
    public CombatOpeningContext? Context { get; private set; }
    public bool OpeningChoiceTriggered { get; private set; }
    public bool RequiresOpeningSnapshot => true;

    public Task PrepareOpeningAsync(CombatOpeningContext context)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Context = context;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsLiveCombatSelection)
            return CardSelectionDecisionSources.RunEngine.ChooseCardsAsync(request);

        OpeningChoiceTriggered = true;
        if (!BeamOpeningPlanner.IsSmallChoice(request))
        {
            throw new OpeningChoiceUnsupportedException(
                $"Opening card selection ({request.Source?.Id.Entry ?? "unknown"}, " +
                $"min={request.MinCount}, max={request.MaxCount}, cancelable={request.Cancelable}, " +
                $"bundle={request.IsBundleSelection}, candidates={request.Candidates.Count}) is not supported by the bridge.",
                "openingChoice");
        }

        throw new ForecastOpeningPlanningRequiredException();
    }

    public async Task<BeamForecastResult> SolveOpeningAndCombatAsync(
        BeamBudget budget,
        Func<CombatState, CombatPolicy> resolvePolicyForRoot,
        PreCombatPortfolioOptions portfolio,
        CancellationToken cancellationToken,
        int maxDegreeOfParallelism = 1)
    {
        CombatOpeningContext context = Context
            ?? throw new InvalidOperationException("Opening planning context was not captured.");
        cancellationToken.ThrowIfCancellationRequested();

        var watch = Stopwatch.StartNew();
        var baseline = new OpeningComparisonBaseline(context.CopyPreparedState());
        int openingCap = Math.Min(64, Math.Max(1, budget.NodeLimit / 16));
        var pending = new Stack<IReadOnlyList<OpeningChoiceStep>>();
        pending.Push(Array.Empty<OpeningChoiceStep>());

        var leaves = new List<(CombatState State, OpeningPolicyLedger Ledger, IReadOnlyList<OpeningChoiceStep> Choices)>();
        int openingAttempts = 0;
        ForecastSearchBoundary? softExhaustion = null;

        try
        {
            while (pending.Count > 0 && leaves.Count < 16 && openingAttempts < openingCap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (watch.Elapsed >= budget.TimeLimit)
                {
                    softExhaustion = ForecastSearchBoundary.TimeLimit;
                    break;
                }

                openingAttempts++;
                IReadOnlyList<OpeningChoiceStep> prefix = pending.Pop();
                var source = new ExploringOpeningSource(prefix, budget.TimeLimit, watch, cancellationToken);
                var ledger = new OpeningPolicyLedger();
                try
                {
                    CombatState state = await context.ReplayObservedAsync(source, ledger, cancellationToken).ConfigureAwait(false);
                    source.AssertConsumed();
                    ledger.Complete(state);
                    leaves.Add((state, ledger, source.Seen.ToArray()));
                }
                catch (OpeningSelectionRequired required)
                {
                    // fail-open: 展开小规模开局选择的每个合法候选分支；分支脚本从最后一个候选逆序入栈以保持左优先探索。
                    for (int i = required.Request.Candidates.Count - 1; i >= 0; i--)
                        pending.Push(source.Seen.Append(OpeningChoiceStep.Capture(required.Request, [i])).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or SharedCombatBudgetExhaustedException)
        {
            // fail-open: 开局分支发现阶段软预算耗尽时，若已收集到完整叶子则继续评估已发现叶子。
            softExhaustion ??= ex is TimeoutException
                ? ForecastSearchBoundary.TimeLimit
                : ForecastSearchBoundary.NodeLimit;
        }

        bool openingBudgetExhausted = pending.Count > 0;
        if (leaves.Count == 0)
        {
            throw new InvalidOperationException(
                watch.Elapsed >= budget.TimeLimit || softExhaustion == ForecastSearchBoundary.TimeLimit
                    ? "Soft opening search time budget expired before any complete opening leaf was produced."
                    : "Soft opening search node budget expired before any complete opening leaf was produced.");
        }

        var evaluatedLeaves = new List<(BeamForecastResult Result, SearchNode Ending, StateEvaluator Evaluator)>();
        long totalSearchNodes = 0;

        try
        {
            for (int i = 0; i < leaves.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan remainingTime = budget.TimeLimit - watch.Elapsed;
                if (remainingTime <= TimeSpan.Zero)
                {
                    softExhaustion = ForecastSearchBoundary.TimeLimit;
                    break;
                }

                long remainingNodes = budget.NodeLimit - openingAttempts - totalSearchNodes;
                if (remainingNodes <= 0)
                {
                    softExhaustion = ForecastSearchBoundary.NodeLimit;
                    break;
                }

                int remainingLeaves = leaves.Count - i;
                int allowance = Math.Max(1, (int)(remainingNodes / remainingLeaves));
                var leaf = leaves[i];
                CombatPolicy leafPolicy = resolvePolicyForRoot(leaf.State);
                var leafEvaluator = new StateEvaluator(baseline, leaf.State, leafPolicy);
                if (!leafEvaluator.AllowsMaxHpLoss(leaf.Ledger.BrightestFlameMaxHpLost))
                    continue;

                BeamBudget leafBudget = budget with
                {
                    NodeLimit = allowance,
                    TimeLimit = remainingTime,
                };
                int preRootHpLoss = leaf.State.Players[0].Creature.CumulativeHpLost;
                BeamForecastResult leafResult = await BeamSearch.SolveForForecastAsync(
                    leaf.State,
                    leafBudget,
                    leafPolicy,
                    portfolio,
                    preRootHpLoss,
                    cancellationToken,
                    maxDegreeOfParallelism).ConfigureAwait(false);
                totalSearchNodes += leafResult.ExpandedNodes;

                SearchNode ending = await BuildVerifiedLeafEndingAsync(
                    leaf.State, leaf.Ledger, leafResult, leafEvaluator, cancellationToken).ConfigureAwait(false);
                if (!leafEvaluator.AllowsMaxHpLoss(ending.BrightestFlameMaxHpLost))
                    continue;

                evaluatedLeaves.Add((leafResult, ending, leafEvaluator));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or SharedCombatBudgetExhaustedException)
        {
            // fail-open: 当硬期限令牌仍有效但软预算在开局叶子评估中耗尽时，若已有已验证叶子则降级返回最佳叶子，否则转为失败。
            softExhaustion ??= ex is TimeoutException
                ? ForecastSearchBoundary.TimeLimit
                : ForecastSearchBoundary.NodeLimit;
        }

        if (evaluatedLeaves.Count == 0)
        {
            throw new InvalidOperationException(
                "Soft opening budget was exhausted with no verified executable opening leaf.");
        }

        StateEvaluator comparisonEvaluator = evaluatedLeaves[0].Evaluator;
        SearchNode bestEnding = PlanSelector.Select(
            evaluatedLeaves.Select(item => item.Ending),
            baseline.InitialHp,
            comparisonEvaluator);
        var chosen = evaluatedLeaves.First(item => ReferenceEquals(item.Ending, bestEnding));

        ForecastSearchBoundary finalBoundary = chosen.Result.Won
            ? ForecastSearchBoundary.None
            : softExhaustion ?? (openingBudgetExhausted
                ? (watch.Elapsed >= budget.TimeLimit ? ForecastSearchBoundary.TimeLimit : ForecastSearchBoundary.NodeLimit)
                : chosen.Result.SearchBoundary);

        bool onlyDeathRoutes = evaluatedLeaves.All(item => item.Result.OnlyDeathRoutes);
        ForecastConfidence confidence = onlyDeathRoutes
            ? ForecastConfidence.DeathOnly
            : chosen.Result.CombatEndedTurn.HasValue
                ? ForecastConfidence.Complete
                : ForecastConfidence.Bounded;

        return chosen.Result with
        {
            OnlyDeathRoutes = onlyDeathRoutes,
            Confidence = confidence,
            SearchBoundary = finalBoundary,
            ExpandedNodes = openingAttempts + totalSearchNodes,
            SearchElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
        };
    }

    private static async Task<SearchNode> BuildVerifiedLeafEndingAsync(
        CombatState leafState,
        OpeningPolicyLedger ledger,
        BeamForecastResult leafResult,
        StateEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchNode node = SearchNode.Create(
            leafState.Clone(),
            null,
            null,
            evaluator,
            ledger.BrightestFlameMaxHpLost,
            ledger.Growth,
            ledger.DeathSaveHpRestored,
            ledger.DeathSaveUseCount);
        foreach (PlannedAction usedPotion in ledger.Potions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            node = SearchNode.Create(node.State, node, usedPotion, evaluator);
        }

        if (leafResult.Actions.Count == 0)
            return node;

        CombatState after = node.State.Clone();
        foreach (PlannedAction action in leafResult.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sink = new PredictionLedgerSink();
            after.PredictionSink = sink;
            await ActionExecutor.ExecuteAsync(after, action, action.ChoiceIndices).ConfigureAwait(false);
            node = SearchNode.Create(
                after,
                node,
                action,
                evaluator,
                sink.BrightestFlameMaxHpLost,
                evaluator.CaptureGrowth ? GrowthLedger.Delta(node.State, after, action, sink) : default,
                sink.DeathSaveHpRestored,
                sink.DeathSaveUseCount);
            after = after.Clone();
        }

        return node;
    }

    private sealed class OpeningSelectionRequired(CardSelectionRequest request) : Exception
    {
        internal CardSelectionRequest Request { get; } = request;
    }

    private sealed class ExploringOpeningSource(
        IReadOnlyList<OpeningChoiceStep> prefix,
        TimeSpan timeLimit,
        Stopwatch watch,
        CancellationToken cancellationToken) : ICardSelectionDecisionSource
    {
        internal List<OpeningChoiceStep> Seen { get; } = [];

        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed >= timeLimit)
                throw new TimeoutException("Soft opening time budget exhausted.");

            if (!BeamOpeningPlanner.IsSmallChoice(request))
            {
                throw new OpeningChoiceUnsupportedException(
                    $"Opening card selection ({request.Source?.Id.Entry ?? "unknown"}, " +
                    $"min={request.MinCount}, max={request.MaxCount}, cancelable={request.Cancelable}, " +
                    $"bundle={request.IsBundleSelection}, candidates={request.Candidates.Count}) is not supported by the bridge.",
                    "openingChoice");
            }

            OpeningChoiceStep step = Seen.Count < prefix.Count
                ? prefix[Seen.Count]
                : throw new OpeningSelectionRequired(request);

            IReadOnlyList<CardModel> selected = step.Apply(request);
            Seen.Add(step);
            return Task.FromResult(selected);
        }

        internal void AssertConsumed()
        {
            if (Seen.Count < prefix.Count)
                throw new InvalidOperationException("Opening script contains unconsumed choices.");
        }
    }
}
