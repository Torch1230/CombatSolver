using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal readonly record struct PortfolioMemberRun(
    ForecastPassOutcome? Outcome,
    long ExpandedNodes);

internal static class PreCombatBeamPortfolio
{
    private static readonly TimeSpan PartialTraceFallbackBudget = TimeSpan.FromMilliseconds(250);

    private sealed record PortfolioOutcome(
        ForecastPassOutcome ChosenPass,
        EvaluatedForecastCandidate[] CombinedCandidates,
        long TotalExpandedNodes);

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
        PortfolioOutcome result = await SolveCoreAsync(
            root,
            budget,
            policy,
            portfolio,
            openingPreRootBattleHpLost,
            watch,
            tracker: null,
            ct,
            maxDegreeOfParallelism).ConfigureAwait(false);
        return BeamSearch.BuildForecastResult(
            result.ChosenPass,
            result.CombinedCandidates,
            result.TotalExpandedNodes,
            openingPreRootBattleHpLost,
            watch.Elapsed.TotalMilliseconds);
    }

    public static async Task<InCombatBeamResult> SolveForInCombatAsync(
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
        CancellationToken hardDeadlineToken = default)
    {
        var tracker = new InCombatSearchTracker(
            root,
            priorBattleHpLost,
            watch,
            progressCallback,
            searchCancellation: ct,
            hardDeadlineToken: hardDeadlineToken);
        try
        {
            PortfolioOutcome result = await SolveCoreAsync(
                root,
                budget,
                policy,
                portfolio,
                openingPreRootBattleHpLost: priorBattleHpLost,
                watch,
                tracker,
                ct,
                maxDegreeOfParallelism).ConfigureAwait(false);
            tracker.StopAndJoin(hardDeadlineToken);
            ct.ThrowIfCancellationRequested();
            hardDeadlineToken.ThrowIfCancellationRequested();
            return InCombatRouteTracer.BuildResult(
                root,
                result.ChosenPass,
                result.CombinedCandidates,
                result.TotalExpandedNodes,
                priorBattleHpLost,
                watch.Elapsed.TotalMilliseconds,
                ct);
        }
        catch (OperationCanceledException)
        {
            tracker.StopAndJoin(hardDeadlineToken);
            if (onPartialResult is not null
                && tracker.TryGetBestSoFarOnCancel(hardDeadlineToken, PartialTraceFallbackBudget) is { } partial)
            {
                onPartialResult(partial);
            }
            throw;
        }
        finally
        {
            tracker.StopAndJoin(hardDeadlineToken);
        }
    }

    private static async Task<PortfolioOutcome> SolveCoreAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        InCombatSearchTracker? tracker,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1)
    {
        ct.ThrowIfCancellationRequested();
        int effectiveDop = Math.Max(1, maxDegreeOfParallelism);
        PolicyRootOperands rootPolicyOperands = PolicyRootOperands.Capture(root, policy);
        if (!portfolio.UseBeamWidthPortfolio)
        {
            ForecastPassOutcome single = await BeamSearch.RunSingleForecastPassAsync(
                root,
                budget,
                policy,
                ct,
                openingPreRootBattleHpLost: openingPreRootBattleHpLost,
                maxDegreeOfParallelism: effectiveDop,
                preCapturedPolicyOperands: rootPolicyOperands,
                inCombatPassTrack: tracker?.RegisterPass()).ConfigureAwait(false);
            return new PortfolioOutcome(single, single.EvaluatedCandidates, single.ExpandedNodes);
        }

        bool hasReachablePower = HasReachablePower(root);
        int member0Nodes = ComputeMember0NodeLimit(budget.NodeLimit, hasReachablePower);
        BeamBudget member0Budget = budget with { NodeLimit = member0Nodes };

        if (effectiveDop >= 2)
        {
            return await SolveConcurrentPortfolioAsync(
                root,
                budget,
                policy,
                rootPolicyOperands,
                openingPreRootBattleHpLost,
                watch,
                hasReachablePower,
                member0Nodes,
                member0Budget,
                effectiveDop,
                tracker,
                ct).ConfigureAwait(false);
        }

        ForecastPassOutcome member0 = await BeamSearch.RunSingleForecastPassAsync(
            root,
            member0Budget,
            policy,
            ct,
            openingPreRootBattleHpLost: openingPreRootBattleHpLost,
            preCapturedPolicyOperands: rootPolicyOperands,
            inCombatPassTrack: tracker?.RegisterPass()).ConfigureAwait(false);

        var passes = new List<ForecastPassOutcome>(3) { member0 };
        long totalExpanded = member0.ExpandedNodes;
        long remainingNodes = budget.NodeLimit - totalExpanded;
        TimeSpan remainingTime = budget.TimeLimit - watch.Elapsed;

        if (member0.AcceptableVictoryFound
            || remainingNodes <= 0
            || remainingTime <= TimeSpan.Zero)
        {
            return new PortfolioOutcome(member0, member0.EvaluatedCandidates, totalExpanded);
        }

        if (hasReachablePower)
        {
            int powerNodes = checked((int)(remainingNodes <= 2
                ? remainingNodes
                : Math.Max(1L, remainingNodes * 3 / 4)));
            BeamBudget powerBudget = budget with
            {
                NodeLimit = powerNodes,
                TimeLimit = remainingTime,
            };
            ForecastPassOutcome powerMember = await BeamSearch.RunSingleForecastPassAsync(
                root,
                powerBudget,
                policy,
                ct,
                aggressivePowerCommitment: true,
                openingPreRootBattleHpLost: openingPreRootBattleHpLost,
                preCapturedPolicyOperands: rootPolicyOperands,
                inCombatPassTrack: tracker?.RegisterPass()).ConfigureAwait(false);
            passes.Add(powerMember);
            totalExpanded += powerMember.ExpandedNodes;
            remainingNodes = budget.NodeLimit - totalExpanded;
            remainingTime = budget.TimeLimit - watch.Elapsed;

            if (!powerMember.AcceptableVictoryFound
                && remainingNodes > 0
                && remainingTime > TimeSpan.Zero)
            {
                await RunNarrowRefinementMemberAsync(
                    root, budget, policy, rootPolicyOperands, openingPreRootBattleHpLost, remainingNodes, remainingTime, passes, 1, tracker, ct).ConfigureAwait(false);
                totalExpanded += passes[^1].ExpandedNodes;
            }
        }
        else
        {
            await RunNarrowRefinementMemberAsync(
                root, budget, policy, rootPolicyOperands, openingPreRootBattleHpLost, remainingNodes, remainingTime, passes, 1, tracker, ct).ConfigureAwait(false);
            totalExpanded += passes[^1].ExpandedNodes;
        }

        EvaluatedForecastCandidate[] combinedCandidates = passes
            .SelectMany(pass => pass.EvaluatedCandidates)
            .ToArray();

        ForecastPassOutcome chosenPass = ChooseWinningPass(
            passes,
            root.Players[0].Creature.CurrentHp,
            member0.Evaluator);
        return new PortfolioOutcome(chosenPass, combinedCandidates, totalExpanded);
    }

    private static async Task<PortfolioOutcome> SolveConcurrentPortfolioAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PolicyRootOperands rootPolicyOperands,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        bool hasReachablePower,
        int member0Nodes,
        BeamBudget member0Budget,
        int effectiveDop,
        InCombatSearchTracker? tracker,
        CancellationToken ct)
    {
        int remainingInitialNodes = budget.NodeLimit - member0Nodes;
        if (remainingInitialNodes <= 0)
        {
            ForecastPassOutcome single = await BeamSearch.RunSingleForecastPassAsync(
                root,
                member0Budget,
                policy,
                ct,
                openingPreRootBattleHpLost: openingPreRootBattleHpLost,
                maxDegreeOfParallelism: effectiveDop,
                preCapturedPolicyOperands: rootPolicyOperands,
                inCombatPassTrack: tracker?.RegisterPass()).ConfigureAwait(false);
            return new PortfolioOutcome(single, single.EvaluatedCandidates, single.ExpandedNodes);
        }

        var passes = new List<ForecastPassOutcome>(3);
        int secondaryDop = Math.Max(1, effectiveDop / 2);
        int primaryDop = Math.Max(1, effectiveDop - secondaryDop);

        BeamBudget secondaryBudget = hasReachablePower
            ? budget with
            {
                NodeLimit = checked((int)(remainingInitialNodes <= 2
                    ? remainingInitialNodes
                    : Math.Max(1L, (long)remainingInitialNodes * 3 / 4))),
            }
            : budget with
            {
                BeamWidth = Math.Max(12, budget.BeamWidth / 2),
                NodeLimit = remainingInitialNodes,
            };

        using var earlyStopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        InCombatPassTrack? primaryTrack = tracker?.RegisterPass();
        InCombatPassTrack? secondaryTrack = tracker?.RegisterPass();
        Task<PortfolioMemberRun> primaryTask = Task.Run(
            () => RunPortfolioMemberAsync(
                root.Clone(),
                member0Budget,
                policy,
                rootPolicyOperands,
                aggressivePowerCommitment: false,
                openingPreRootBattleHpLost,
                primaryDop,
                earlyStopCts,
                ct,
                passTrack: primaryTrack),
            CancellationToken.None);
        Task<PortfolioMemberRun> secondaryTask = Task.Run(
            () => RunPortfolioMemberAsync(
                root.Clone(),
                secondaryBudget,
                policy,
                rootPolicyOperands,
                aggressivePowerCommitment: hasReachablePower,
                openingPreRootBattleHpLost,
                secondaryDop,
                earlyStopCts,
                ct,
                passTrack: secondaryTrack),
            CancellationToken.None);

        PortfolioMemberRun[] initialResults = await Task.WhenAll(primaryTask, secondaryTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        long totalExpanded = 0;
        foreach (PortfolioMemberRun memberRun in initialResults)
        {
            totalExpanded += memberRun.ExpandedNodes;
            if (memberRun.Outcome is not null)
                passes.Add(memberRun.Outcome);
        }

        bool anyAcceptableVictory = passes.Any(pass => pass.AcceptableVictoryFound);
        if (hasReachablePower && !anyAcceptableVictory)
        {
            long remainingNodes = budget.NodeLimit - totalExpanded;
            TimeSpan remainingTime = budget.TimeLimit - watch.Elapsed;
            if (remainingNodes > 0 && remainingTime > TimeSpan.Zero)
            {
                await RunNarrowRefinementMemberAsync(
                    root.Clone(),
                    budget,
                    policy,
                    rootPolicyOperands,
                    openingPreRootBattleHpLost,
                    remainingNodes,
                    remainingTime,
                    passes,
                    effectiveDop,
                    tracker,
                    ct).ConfigureAwait(false);
                totalExpanded += passes[^1].ExpandedNodes;
            }
        }

        EvaluatedForecastCandidate[] combinedCandidates = passes
            .SelectMany(pass => pass.EvaluatedCandidates)
            .ToArray();

        ForecastPassOutcome chosenPass = ChooseWinningPass(
            passes,
            root.Players[0].Creature.CurrentHp,
            passes[0].Evaluator);
        return new PortfolioOutcome(chosenPass, combinedCandidates, totalExpanded);
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
        InCombatPassTrack? passTrack = null)
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
                inCombatPassTrack: passTrack).ConfigureAwait(false);
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

    private static async Task RunNarrowRefinementMemberAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PolicyRootOperands rootPolicyOperands,
        int openingPreRootBattleHpLost,
        long remainingNodes,
        TimeSpan remainingTime,
        List<ForecastPassOutcome> passes,
        int maxDegreeOfParallelism,
        InCombatSearchTracker? tracker,
        CancellationToken ct)
    {
        int secondaryWidth = Math.Max(12, budget.BeamWidth / 2);
        BeamBudget narrowBudget = budget with
        {
            BeamWidth = secondaryWidth,
            NodeLimit = checked((int)remainingNodes),
            TimeLimit = remainingTime,
        };
        ForecastPassOutcome narrowMember = await BeamSearch.RunSingleForecastPassAsync(
            root,
            narrowBudget,
            policy,
            ct,
            openingPreRootBattleHpLost: openingPreRootBattleHpLost,
            maxDegreeOfParallelism: maxDegreeOfParallelism,
            preCapturedPolicyOperands: rootPolicyOperands,
            inCombatPassTrack: tracker?.RegisterPass()).ConfigureAwait(false);
        passes.Add(narrowMember);
    }

    private static bool HasReachablePower(CombatState root)
    {
        PlayerCombatState? pcs = root.Players[0].PlayerCombatState;
        if (pcs is null)
            return false;

        return pcs.Hand.Cards.Any(IsPower)
            || pcs.DrawPile.Cards.Any(IsPower)
            || pcs.DiscardPile.Cards.Any(IsPower);

        static bool IsPower(CardModel card) => card.Type == CardType.Power;
    }

    internal static ForecastPassOutcome ChooseWinningPass(
        IReadOnlyList<ForecastPassOutcome> passes,
        int initialHp,
        StateEvaluator evaluator)
    {
        if (passes.Count == 0)
            throw new ArgumentException("At least one forecast pass outcome is required.", nameof(passes));
        if (passes.Count == 1)
            return passes[0];

        bool anyPassWon = passes.Any(pass => pass.Selected.Won);
        var combinedNodes = new List<SearchNode>();
        var ownerByNode = new Dictionary<SearchNode, ForecastPassOutcome>(ReferenceEqualityComparer.Instance);

        foreach (ForecastPassOutcome pass in passes)
        {
            // 若已有成员找到完整胜利，未获胜成员及其候选不参与终选，防止非胜局候选干扰完整胜利。
            if (anyPassWon && !pass.Selected.Won)
                continue;

            if (ownerByNode.TryAdd(pass.Selected, pass))
                combinedNodes.Add(pass.Selected);

            if (pass.AcceptableVictoryFound)
                continue;

            foreach (EvaluatedForecastCandidate candidate in pass.EvaluatedCandidates)
            {
                if (ownerByNode.TryAdd(candidate.Node, pass))
                    combinedNodes.Add(candidate.Node);
            }
        }

        SearchNode winner = PlanSelector.Select(combinedNodes, initialHp, evaluator);
        ForecastPassOutcome owner = ownerByNode[winner];
        return ReferenceEquals(winner, owner.Selected)
            ? owner
            : owner with { Selected = winner };
    }
}
