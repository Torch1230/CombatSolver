using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using CombatSolver;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam.Novelty;

internal static class NoveltySearchRunner
{
    private sealed class ActionChildSlot
    {
        public readonly object Sync = new();
        public readonly List<SearchNode> Children = new(2);
    }

    internal static BeamBudget? ComputeExplorationBudget(
        BeamBudget budget,
        PreCombatPortfolioOptions portfolio,
        bool isActEndingBoss)
    {
        long softMs = (long)Math.Max(1d, budget.TimeLimit.TotalMilliseconds);
        if (softMs < 2_000L || budget.NodeLimit < 1_000)
            return null;

        int divisor = isActEndingBoss
            ? Math.Max(4, portfolio.NoveltyBudgetTimeDivisor)
            : Math.Max(2, portfolio.NoveltyBudgetTimeDivisor);
        long maxMs = Math.Min(portfolio.NoveltyBudgetMaximumMilliseconds, softMs / divisor);
        int maxNodes = Math.Min(portfolio.NoveltyBudgetMaximumExpandedNodes, budget.NodeLimit / 4);
        if (maxMs < 1 || maxNodes < 1)
            return null;

        return budget with
        {
            TimeLimit = TimeSpan.FromMilliseconds(maxMs),
            NodeLimit = maxNodes,
        };
    }

    internal static BeamBudget? ComputeRefinementAfterBaselineBudget(
        BeamBudget fullBudget,
        PreCombatPortfolioOptions portfolio,
        long baselineElapsedMs,
        long baselineExpanded,
        TimeSpan remainingTime,
        long remainingNodes,
        bool isActEndingBoss)
    {
        BeamBudget? cap = ComputeExplorationBudget(fullBudget, portfolio, isActEndingBoss);
        if (cap is null || remainingTime <= TimeSpan.Zero || remainingNodes <= 0)
            return null;

        long nodes = Math.Min(baselineExpanded / 8, Math.Min((long)cap.NodeLimit, remainingNodes));
        long ms = Math.Min(baselineElapsedMs / 8,
            Math.Min((long)cap.TimeLimit.TotalMilliseconds, (long)remainingTime.TotalMilliseconds));
        if (nodes < 1 || ms < 1)
            return null;

        return fullBudget with
        {
            NodeLimit = (int)Math.Min(int.MaxValue, nodes),
            TimeLimit = TimeSpan.FromMilliseconds(ms),
        };
    }

    internal static bool IsActEndingBoss(
        CombatState root,
        CombatPolicy policy,
        PolicyRootOperands? rootPolicyOperands = null) =>
        (rootPolicyOperands?.BossHpRelief ?? BossHpPolicy.Resolve(root, policy)) != CombatSolver.Beam.Policy.BossHpRelief.None;

    internal static ForecastPassOutcome RunNoveltyPass(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        BeamPassOptions passOptions,
        InCombatPassTrack? track,
        CancellationToken ct) =>
        RunNoveltyPassOrNull(
            root,
            budget,
            policy,
            portfolio,
            passOptions with { AllowPolicyUnsatisfiedSelection = false },
            track,
            ct)
            ?? throw new InvalidOperationException("Novelty search produced no policy-satisfied outcome.");

    internal static ForecastPassOutcome? RunNoveltyPassOrNull(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        BeamPassOptions passOptions,
        InCombatPassTrack? track,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();

        CombatState startRoot = passOptions.StartNodeOverride?.State ?? root;
        int initialHp = passOptions.RootBaselineEvaluator?.InitialHp ?? root.Players[0].Creature.CurrentHp;
        int rootInitialCumulativeHpLost = passOptions.RootBaselineEvaluator?.InitialCumulativeHpLost
            ?? root.Players[0].Creature.CumulativeHpLost;
        StateEvaluator evaluator = passOptions.RootBaselineEvaluator is not null
            ? new StateEvaluator(
                startRoot,
                policy,
                BeamMemberSearchMode.Default,
                passOptions.RootBaselineEvaluator)
            : new StateEvaluator(
                StateEvaluator.CaptureBaseline(startRoot, policy, passOptions.RootPolicyOperands),
                memberModeOverride: BeamMemberSearchMode.Default);
        track?.AttachEvaluator(evaluator, initialHp, Math.Max(1, passOptions.SharedLanePool?.LaneCount ?? 1));

        SearchNode start = passOptions.StartNodeOverride is not null
            ? SearchNode.CreateContinuationStart(passOptions.StartNodeOverride, evaluator)
            : SearchNode.Create(root.Clone(), null, null, evaluator);
        start.State.FreezeForConcurrentReads();

        SearchWorkLedger? ledger = passOptions.WorkLedger;
        MemberBudgetController budgetController = passOptions.BudgetController
            ?? new MemberBudgetController(budget.NodeLimit, sharedBudget: null);

        const int MaxWitnessCount = 32;
        const int CompletedTrimThreshold = 64;
        const int CompletedRetainCount = 32;

        var open = new BfwsBoundedOpen<(SearchNode Node, BfwsEscapeBudget? Escape)>(portfolio.NoveltyMaxOpen);
        var novelty = new BfwsPackedNovelty(portfolio.NoveltyWidth, portfolio.NoveltyMaxEntries);
        var transpositionTable = new ParentAdmissionTable();
        var completed = new List<SearchNode>(CompletedTrimThreshold + 4);
        var boundaryWitnesses = new List<SearchNode>(MaxWitnessCount);
        SearchNode? bestPolicyWitness = null;
        SearchNode? bestPotionFreeWitness = null;
        SearchNode? bestPotionWitness = null;
        SearchNode? survivingWitness = null;
        SearchNode bestFallback = start;

        var factsScratch = new List<BfwsFact>(64);
        var zoneCountsScratch = new Dictionary<(string Id, int Upgrade), int>(32);
        var zoneOrderScratch = new List<(string Id, int Upgrade)>(32);
        int startTurn = start.Turn;
        long sequence = 0;
        long parentsExpanded = 0;
        long transitionsProduced = 0;
        long attemptsMade = 0;
        ForecastSearchBoundary passBoundary = ForecastSearchBoundary.None;
        bool acceptableVictoryFound = false;

        int NoveltyFor(SearchNode node)
        {
            CoreNoveltyFeatures.CaptureNoveltyFacts(node, policy, factsScratch, zoneCountsScratch, zoneOrderScratch);
            return novelty.Evaluate(CoreNoveltyFeatures.ComputePartition(node), factsScratch);
        }

        static bool IsSameTurnCycle(SearchNode node)
        {
            var fp = node.Fingerprint;
            int turn = node.Turn;
            for (SearchRouteStep? cur = node.Path.Previous; cur is not null && cur.Turn == turn; cur = cur.Previous)
            {
                if (cur.Fingerprint == fp)
                    return true;
            }
            return false;
        }

        void ObserveLivingRoute(SearchNode node)
        {
            if (survivingWitness is not null)
                return;
            Creature c = node.State.Players[0].Creature;
            if (!c.IsAlive || c.CurrentHp <= 0)
                return;
            if (node.Won || evaluator.ComputeProjectedPlayerHp(node, node.NodeBoundary) > 0)
                survivingWitness = node;
        }

        bool SatisfiesPolicyForWitness(SearchNode node) =>
            evaluator.SatisfiesPotionPolicy(node)
            && evaluator.SatisfiesTheftPolicy(node)
            && evaluator.SatisfiesRelicTargets(node)
            && evaluator.SatisfiesGrowthTargets(node);

        void ObservePotionFreeBaseline(SearchNode node)
        {
            if (node.PotionUses != 0)
                return;
            if (bestPotionFreeWitness is null || IsBetterPotionFreeBaseline(node, bestPotionFreeWitness))
                bestPotionFreeWitness = node;
        }

        bool IsBetterPotionFreeBaseline(SearchNode candidate, SearchNode current)
        {
            if (candidate.Won != current.Won)
                return candidate.Won;
            if (candidate.Ended != current.Ended)
                return candidate.Ended;
            int candCost = evaluator.PolicyHpCost(candidate, initialHp);
            int currCost = evaluator.PolicyHpCost(current, initialHp);
            if (candCost != currCost)
                return candCost < currCost;
            return candidate.Score > current.Score;
        }

        void RecordWitness(SearchNode node)
        {
            ObserveLivingRoute(node);
            ObservePotionFreeBaseline(node);
            if (SatisfiesPolicyForWitness(node))
            {
                if (bestPolicyWitness is null)
                {
                    bestPolicyWitness = node;
                }
                else
                {
                    var pool = new List<SearchNode>(3) { bestPolicyWitness, node };
                    if (bestPotionFreeWitness is not null
                        && !ReferenceEquals(bestPotionFreeWitness, bestPolicyWitness)
                        && !ReferenceEquals(bestPotionFreeWitness, node))
                    {
                        pool.Add(bestPotionFreeWitness);
                    }
                    SearchNode? selected = PlanSelector.TrySelect(pool, initialHp, evaluator);
                    if (selected is not null)
                    {
                        if (!ReferenceEquals(selected, bestPotionFreeWitness) || bestPolicyWitness.PotionUses == 0 || node.PotionUses == 0)
                            bestPolicyWitness = selected;
                    }
                    else if (node.Score > bestPolicyWitness.Score)
                    {
                        bestPolicyWitness = node;
                    }
                }
            }
            if (node.PotionUses > 0
                && (bestPotionWitness is null || node.Score > bestPotionWitness.Score))
            {
                bestPotionWitness = node;
            }

            if (boundaryWitnesses.Count < MaxWitnessCount)
            {
                boundaryWitnesses.Add(node);
                return;
            }

            int worstIdx = 0;
            double worstScore = boundaryWitnesses[0].Score;
            for (int i = 1; i < boundaryWitnesses.Count; i++)
            {
                if (boundaryWitnesses[i].Score < worstScore)
                {
                    worstScore = boundaryWitnesses[i].Score;
                    worstIdx = i;
                }
            }
            if (node.Score > worstScore)
                boundaryWitnesses[worstIdx] = node;
        }

        void TrimCompletedIfNeeded()
        {
            if (completed.Count <= CompletedTrimThreshold)
                return;
            for (int i = 0; i < completed.Count; i++)
            {
                ObserveLivingRoute(completed[i]);
                ObservePotionFreeBaseline(completed[i]);
            }

            IEnumerable<SearchNode> selectionInput = bestPotionFreeWitness is not null && !completed.Contains(bestPotionFreeWitness, ReferenceEqualityComparer.Instance)
                ? completed.Append(bestPotionFreeWitness)
                : completed;
            SearchNode? bestCompleted = PlanSelector.TrySelect(selectionInput, initialHp, evaluator);
            completed.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<SearchNode>(CompletedRetainCount);
            var seen = new HashSet<SearchNode>(ReferenceEqualityComparer.Instance);
            if (bestCompleted is not null && seen.Add(bestCompleted))
                kept.Add(bestCompleted);
            if (bestPotionFreeWitness is not null && seen.Add(bestPotionFreeWitness))
                kept.Add(bestPotionFreeWitness);
            if (survivingWitness is not null && seen.Add(survivingWitness))
                kept.Add(survivingWitness);
            for (int i = 0; i < completed.Count && kept.Count < CompletedRetainCount; i++)
            {
                SearchNode cand = completed[i];
                if (cand.PotionUses == 0 && seen.Add(cand))
                {
                    kept.Add(cand);
                    break;
                }
            }
            for (int i = 0; i < completed.Count && kept.Count < CompletedRetainCount; i++)
            {
                if (seen.Add(completed[i]))
                    kept.Add(completed[i]);
            }
            completed.Clear();
            completed.AddRange(kept);
        }

        void Enqueue(SearchNode node, bool initialSeed, BfwsEscapeBudget? parentEscape)
        {
            if (!initialSeed)
            {
                if (IsSameTurnCycle(node) || !transpositionTable.TryAdmit(node))
                    return;
            }
            int w = NoveltyFor(node);
            if (!initialSeed && w > portfolio.NoveltyWidth)
            {
                if (parentEscape?.TryAdmit() != true)
                    return;
            }
            BfwsEscapeBudget? nextEscape = portfolio.NoveltyFamiliarAllowance > 0
                ? (initialSeed || w <= portfolio.NoveltyWidth
                    ? new BfwsEscapeBudget(portfolio.NoveltyFamiliarAllowance)
                    : parentEscape)
                : null;
            if (open.Enqueue((node, nextEscape), ((double)w, -node.Score, sequence++), out (SearchNode Node, BfwsEscapeBudget? Escape) evicted))
            {
                ObserveLivingRoute(evicted.Node);
                ObservePotionFreeBaseline(evicted.Node);
            }
        }

        long maxTurnHorizon = (long)startTurn + portfolio.NoveltyMaxTurns;

        try
        {
            if (start.Ended)
            {
                _ = NoveltyFor(start);
                completed.Add(start);
                ObserveLivingRoute(start);
                ObservePotionFreeBaseline(start);
                if (!passOptions.DisableTargetStop
                    && BeamSearch.MeetsAcceptableVictory(start, policy, evaluator, passOptions.OpeningPreRootBattleHpLost))
                {
                    acceptableVictoryFound = true;
                }
            }
            else
            {
                Enqueue(start, initialSeed: true, parentEscape: null);
            }

            var actionListScratch = new List<PlannedAction>(16);
            while (open.Count > 0 && !acceptableVictoryFound)
            {
                ct.ThrowIfCancellationRequested();
                (SearchNode parent, BfwsEscapeBudget? escape) = open.Dequeue();

                if (parent.Depth >= portfolio.NoveltyMaxActions
                    || (long)parent.Turn >= maxTurnHorizon)
                {
                    RecordWitness(parent);
                    continue;
                }

                if (watch.Elapsed >= budget.TimeLimit)
                {
                    passBoundary = ForecastSearchBoundary.TimeLimit;
                    RecordWitness(parent);
                    break;
                }

                long usedUnits = passOptions.UseParentNodeBudget ? parentsExpanded : attemptsMade;
                if (usedUnits >= budget.NodeLimit || budgetController.Spent >= budgetController.MemberCap)
                {
                    passBoundary = ForecastSearchBoundary.NodeLimit;
                    RecordWitness(parent);
                    break;
                }

                if (passOptions.UseParentNodeBudget && !budgetController.TryReserveOne())
                {
                    passBoundary = ForecastSearchBoundary.NodeLimit;
                    RecordWitness(parent);
                    break;
                }

                parentsExpanded++;
                ledger?.AddExpandedParents(1);
                if (passOptions.UseParentNodeBudget)
                    track?.ObserveExpansion(1, parent.Turn);

                CoreNoveltyFeatures.CaptureNoveltyFacts(parent, policy, factsScratch, zoneCountsScratch, zoneOrderScratch);
                novelty.PrepareParent(CoreNoveltyFeatures.ComputePartition(parent), factsScratch);

                parent.State.FreezeForConcurrentReads();
                ActionGenerator.GenerateForForecastInto(parent.State, policy, actionListScratch);
                PlannedAction[] actions = actionListScratch.ToArray();
                actionListScratch.Clear();

                List<SearchNode> generatedChildren = ExpandParentActions(
                    parent,
                    actions,
                    budget,
                    policy,
                    evaluator,
                    passOptions,
                    budgetController,
                    ledger,
                    watch,
                    track,
                    ref attemptsMade,
                    ref transitionsProduced,
                    ref passBoundary,
                    ct);

                for (int c = 0; c < generatedChildren.Count; c++)
                {
                    SearchNode child = generatedChildren[c];
                    child.PrecomputeRetentionKey();
                    if (child.Score > bestFallback.Score)
                        bestFallback = child;

                    bool isBoundaryOrEnded = child.Ended
                        || child.Turn != parent.Turn
                        || child.NodeBoundary != ForecastSearchBoundary.None;
                    track?.RecordInFlightCandidate(0, child, isBoundaryOrEnded);
                    if (isBoundaryOrEnded)
                        RecordWitness(child);

                    if (child.Ended || child.NodeBoundary != ForecastSearchBoundary.None)
                    {
                        _ = NoveltyFor(child);
                        completed.Add(child);
                        ObserveLivingRoute(child);
                        if (!passOptions.DisableTargetStop
                            && BeamSearch.MeetsAcceptableVictory(
                                child,
                                policy,
                                evaluator,
                                passOptions.OpeningPreRootBattleHpLost))
                        {
                            acceptableVictoryFound = true;
                        }
                    }
                    else
                    {
                        Enqueue(child, initialSeed: false, parentEscape: escape);
                    }
                }

                TrimCompletedIfNeeded();

                if (passBoundary != ForecastSearchBoundary.None || novelty.LimitReached)
                {
                    if (passBoundary == ForecastSearchBoundary.None && novelty.LimitReached)
                        passBoundary = ForecastSearchBoundary.NodeLimit;
                    break;
                }
            }
        }
        finally
        {
            budgetController.ReclaimUnspent();
        }

        if (!acceptableVictoryFound && passBoundary == ForecastSearchBoundary.None && open.Count > 0)
        {
            passBoundary = watch.Elapsed >= budget.TimeLimit
                ? ForecastSearchBoundary.TimeLimit
                : ForecastSearchBoundary.NodeLimit;
        }

        var rawCandidates = new List<SearchNode>(completed.Count + open.Count + boundaryWitnesses.Count + 6);
        rawCandidates.AddRange(completed);
        foreach ((SearchNode node, _) in open.Values)
            rawCandidates.Add(node);
        open.Clear();
        rawCandidates.AddRange(boundaryWitnesses);
        if (bestPolicyWitness is not null)
            rawCandidates.Add(bestPolicyWitness);
        if (bestPotionFreeWitness is not null)
            rawCandidates.Add(bestPotionFreeWitness);
        if (bestPotionWitness is not null)
            rawCandidates.Add(bestPotionWitness);
        if (survivingWitness is not null)
            rawCandidates.Add(survivingWitness);
        if (rawCandidates.Count == 0)
            rawCandidates.Add(bestFallback);

        long passExpanded = passOptions.UseParentNodeBudget ? parentsExpanded : attemptsMade;
        ForecastPassOutcome? outcome = BeamSearch.EvaluateForecastFinalPoolOrNull(
            rawCandidates,
            initialHp,
            rootInitialCumulativeHpLost,
            evaluator,
            passBoundary,
            passExpanded,
            acceptableVictoryFound,
            node => !passOptions.DisableTargetStop
                && BeamSearch.MeetsAcceptableVictory(node, policy, evaluator, passOptions.OpeningPreRootBattleHpLost),
            passOptions.AllowPolicyUnsatisfiedSelection,
            ct, captureInCombatPool: track is not null);
        if (outcome is null)
            return null;

        outcome = outcome with
        {
            ExpandedParents = parentsExpanded,
            TransitionCount = transitionsProduced,
            ChoiceReplayAttempts = attemptsMade,
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
        };
        track?.Complete(outcome);
        return outcome;
    }

    private static List<SearchNode> ExpandParentActions(
        SearchNode parent,
        PlannedAction[] actions,
        BeamBudget budget,
        CombatPolicy policy,
        StateEvaluator evaluator,
        BeamPassOptions options,
        MemberBudgetController budgetController,
        SearchWorkLedger? ledger,
        Stopwatch watch,
        InCombatPassTrack? track,
        ref long attemptsMade,
        ref long transitionsProduced,
        ref ForecastSearchBoundary passBoundary,
        CancellationToken ct)
    {
        var collected = new List<SearchNode>(actions.Length * 2);
        if (actions.Length == 0)
            return collected;

        ForecastLanePool? pool = options.SharedLanePool;
        if (pool is null || pool.LaneCount <= 1 || actions.Length <= 1)
        {
            long localAttempts = attemptsMade;
            long localTransitions = transitionsProduced;
            try
            {
                for (int a = 0; a < actions.Length; a++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= budget.TimeLimit)
                    {
                        passBoundary = ForecastSearchBoundary.TimeLimit;
                        break;
                    }
                    if (!options.UseParentNodeBudget
                        && (localAttempts >= budget.NodeLimit || budgetController.Spent >= budgetController.MemberCap))
                    {
                        passBoundary = ForecastSearchBoundary.NodeLimit;
                        break;
                    }
                    ChoiceExpansion.Expansion expansion = ChoiceExpansion.ExpandSync(
                        parent,
                        actions[a],
                        budget,
                        evaluator,
                        tryReserveNode: () =>
                        {
                            if (options.UseParentNodeBudget)
                                return true;
                            if (localAttempts >= budget.NodeLimit || !budgetController.TryReserveOne())
                                return false;
                            return true;
                        },
                        watch,
                        ct,
                        onAttempt: () =>
                        {
                            localAttempts++;
                            ledger?.AddChoiceReplayAttempts(1);
                            if (!options.UseParentNodeBudget)
                                track?.ObserveExpansion(1, parent.Turn);
                        },
                        onTransition: () =>
                        {
                            localTransitions++;
                            ledger?.AddTransitions(1);
                        });
                    for (int c = 0; c < expansion.Children.Count; c++)
                        collected.Add(expansion.Children[c]);
                    if (expansion.BudgetTruncated && passBoundary == ForecastSearchBoundary.None)
                    {
                        passBoundary = watch.Elapsed >= budget.TimeLimit
                            ? ForecastSearchBoundary.TimeLimit
                            : ForecastSearchBoundary.NodeLimit;
                        break;
                    }
                }
            }
            finally
            {
                attemptsMade = localAttempts;
                transitionsProduced = localTransitions;
            }
            return collected;
        }

        var slots = new ActionChildSlot[actions.Length];
        var workItems = new List<ForecastWorkItem>(actions.Length);
        for (int a = 0; a < actions.Length; a++)
        {
            slots[a] = new ActionChildSlot();
            workItems.Add(new ForecastWorkItem
            {
                Kind = ForecastWorkItem.KindAction,
                ParentIndex = 0,
                ActionIndex = a,
                ParentNode = parent,
                Action = actions[a],
            });
        }

        long workerAttempts = 0;
        long workerTransitions = 0;
        long sharedAttemptBaseline = attemptsMade;
        int localBoundary = 0;

        try
        {
            pool.ExecuteGroup(
                workItems,
                options.HomeLaneOffset,
                (_, item) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= budget.TimeLimit)
                    {
                        Interlocked.CompareExchange(ref localBoundary, (int)ForecastSearchBoundary.TimeLimit, 0);
                        return;
                    }
                    ChoiceExpansion.Expansion expansion = ChoiceExpansion.ExpandSync(
                        parent,
                        item.Action!,
                        budget,
                        evaluator,
                        tryReserveNode: () =>
                        {
                            if (options.UseParentNodeBudget)
                                return true;
                            if (!budgetController.TryReserveOne())
                                return false;
                            if (sharedAttemptBaseline + Volatile.Read(ref workerAttempts) >= budget.NodeLimit)
                                return false;
                            return true;
                        },
                        watch,
                        ct,
                        onAttempt: () =>
                        {
                            Interlocked.Increment(ref workerAttempts);
                            ledger?.AddChoiceReplayAttempts(1);
                            if (!options.UseParentNodeBudget)
                                track?.ObserveExpansion(1, parent.Turn);
                        },
                        onTransition: () =>
                        {
                            Interlocked.Increment(ref workerTransitions);
                            ledger?.AddTransitions(1);
                        });
                    ActionChildSlot slot = slots[item.ActionIndex];
                    lock (slot.Sync)
                    {
                        for (int c = 0; c < expansion.Children.Count; c++)
                            slot.Children.Add(expansion.Children[c]);
                    }
                    if (expansion.BudgetTruncated)
                    {
                        Interlocked.CompareExchange(
                            ref localBoundary,
                            (int)(watch.Elapsed >= budget.TimeLimit ? ForecastSearchBoundary.TimeLimit : ForecastSearchBoundary.NodeLimit),
                            0);
                    }
                },
                ledger,
                ct,
                maxConcurrency: Math.Clamp(options.MaxConcurrency ?? pool.LaneCount, 1, pool.LaneCount));
        }
        finally
        {
            attemptsMade += Interlocked.Read(ref workerAttempts);
            transitionsProduced += Interlocked.Read(ref workerTransitions);
        }

        if (localBoundary != 0 && passBoundary == ForecastSearchBoundary.None)
            passBoundary = (ForecastSearchBoundary)localBoundary;

        for (int a = 0; a < slots.Length; a++)
            collected.AddRange(slots[a].Children);
        return collected;
    }
}
