using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using CombatSolver;
using CombatSolver.Beam.Novelty;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal sealed record PortfolioCoordinatorResult(
    ForecastPassOutcome ChosenPass,
    EvaluatedForecastCandidate[] CombinedCandidates,
    long TotalExpandedNodes,
    SearchWorkSnapshot WorkSnapshot);

internal static class BeamWidthPortfolioRunner
{
    internal static async Task<PortfolioCoordinatorResult> SolveAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        SearchWorkLedger ledger,
        InCombatSearchTracker? tracker,
        CancellationToken ct,
        int maxDegreeOfParallelism = 1)
    {
        ct.ThrowIfCancellationRequested();
        int effectiveDop = Math.Max(1, maxDegreeOfParallelism);
        using ForecastLanePool? sharedPool = effectiveDop > 1 ? new ForecastLanePool(effectiveDop) : null;

        PolicyRootOperands rootPolicyOperands = PolicyRootOperands.Capture(root, policy);
        var baseOptions = new BeamPassOptions
        {
            OpeningPreRootBattleHpLost = openingPreRootBattleHpLost,
            UseParentNodeBudget = portfolio.UsesParentNodeBudget,
            UseTranspositionAdmission = portfolio.UsesParentNodeBudget,
            RootPolicyOperands = rootPolicyOperands,
            WorkLedger = ledger,
            SharedLanePool = sharedPool,
        };

        var allPasses = new List<ForecastPassOutcome>(8);
        ForecastPassOutcome selected;

        if (portfolio.PortfolioOptionsVersion < 2)
        {
            selected = await SolveLegacyV1BeamPortfolioAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions with { EnablePowerCommitmentSeats = false },
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct).ConfigureAwait(false);
        }
        else if (portfolio.UseNoveltyPortfolio)
        {
            selected = await SolveWithNoveltyAsync(
                root,
                budget,
                policy,
                portfolio,
                rootPolicyOperands,
                baseOptions,
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct).ConfigureAwait(false);
        }
        else
        {
            var sharedNodes = new SharedNodeBudget(budget.NodeLimit);
            selected = await SolveBeamPortfolioAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions,
                sharedNodes,
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct).ConfigureAwait(false);
        }

        if (portfolio.UseEarlyTurnExploration && portfolio.EarlyTurnExplorationDepth > 0)
        {
            ForecastPassOutcome? beforeEarly = selected;
            selected = await EarlyTurnExplorationRunner.RunAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions,
                selected,
                watch,
                ledger,
                sharedPool,
                tracker,
                ct).ConfigureAwait(false);
            if (!ReferenceEquals(selected, beforeEarly))
                allPasses.Add(selected);
        }

        EvaluatedForecastCandidate[] combined = CombineCandidates(allPasses);
        if (tracker is not null && selected.InCombatSelectionPool is { } comparison
            && selected.PassBoundary is ForecastSearchBoundary.NodeLimit or ForecastSearchBoundary.TimeLimit
            && selected.Selected.State.Players[0].Creature.IsDead)
        {
            // Change only the delivered route. The portfolio has already finished all
            // incumbent, refinement, and early-turn decisions with its ordinary ranking.
            var owners = new Dictionary<SearchNode, ForecastPassOutcome>(ReferenceEqualityComparer.Instance);
            foreach (ForecastPassOutcome pass in allPasses)
            {
                if (pass.PassBoundary is not (ForecastSearchBoundary.NodeLimit or ForecastSearchBoundary.TimeLimit))
                    continue;
                foreach (var candidate in pass.EvaluatedCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.ProjectedPlayerHp > 0)
                        owners.TryAdd(candidate.Node, pass);
                }
            }
            var survivor = PlanSelector.PreferSurvivingTurnBoundary(selected.Selected, comparison.Nodes,
                root.RoundNumber, comparison.InitialHp, comparison.Evaluator, owners.ContainsKey, ct);
            if (!ReferenceEquals(survivor, selected.Selected))
                selected = owners[survivor] with { Selected = survivor };
        }
        long reportedExpanded = portfolio.UsesParentNodeBudget
            ? ledger.ExpandedParents
            : ledger.ChoiceReplayAttempts;
        return new PortfolioCoordinatorResult(
            selected,
            combined,
            reportedExpanded > 0 ? reportedExpanded : selected.ExpandedNodes,
            ledger.Snapshot());
    }

    private static async Task<ForecastPassOutcome> SolveWithNoveltyAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        PolicyRootOperands rootPolicyOperands,
        BeamPassOptions baseOptions,
        Stopwatch watch,
        SearchWorkLedger ledger,
        ForecastLanePool? sharedPool,
        int effectiveDop,
        InCombatSearchTracker? tracker,
        List<ForecastPassOutcome> allPasses,
        CancellationToken ct)
    {
        bool isBoss = NoveltySearchRunner.IsActEndingBoss(root, policy, rootPolicyOperands);
        if (portfolio.AdaptiveNoveltyRefinement)
        {
            var sharedNodes = new SharedNodeBudget(budget.NodeLimit);
            long beforeUnits = ledger.BudgetUnits;
            long beforeMs = watch.ElapsedMilliseconds;
            var adaptiveBeamComparablePasses = new List<ForecastPassOutcome>(8);

            ForecastPassOutcome baselineOutcome = await SolveBeamPortfolioAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions,
                sharedNodes,
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct,
                comparablePassesOut: adaptiveBeamComparablePasses).ConfigureAwait(false);

            long baselineUnits = Math.Max(0L, ledger.BudgetUnits - beforeUnits);
            long baselineMs = Math.Max(0L, watch.ElapsedMilliseconds - beforeMs);

            if (BeamIncumbentPolicy.CanFinishTargetPortfolio(
                policy,
                baselineOutcome.Evaluator,
                baselineOutcome,
                baseOptions.OpeningPreRootBattleHpLost,
                portfolio.StopPortfolioAtHpTarget))
            {
                return baselineOutcome;
            }

            BeamBudget? refinementBudget = NoveltySearchRunner.ComputeRefinementAfterBaselineBudget(
                budget,
                portfolio,
                baselineMs,
                baselineUnits,
                budget.TimeLimit - watch.Elapsed,
                sharedNodes.Remaining,
                isBoss);
            if (refinementBudget is null)
                return baselineOutcome;

            using (ledger.EnterPhase("AdaptiveNovelty"))
            {
                var refCtrl = new MemberBudgetController(refinementBudget.NodeLimit, sharedNodes);
                BeamPassOptions refOptions = baseOptions with
                {
                    BudgetController = refCtrl,
                    HomeLaneOffset = 0,
                    ActivePhaseLabel = "AdaptiveNovelty",
                    AllowPolicyUnsatisfiedSelection = true,
                };
                ForecastPassOutcome? scout = await Task.Run(
                    () => NoveltySearchRunner.RunNoveltyPassOrNull(
                        root.Clone(),
                        refinementBudget,
                        policy,
                        portfolio,
                        refOptions,
                        tracker?.RegisterPass(),
                        ct),
                    CancellationToken.None).ConfigureAwait(false);
                if (scout is not null)
                    allPasses.Add(scout);

                if (scout is { Selected.Won: true })
                    return ChooseBeamAndScoutOutcome(baselineOutcome, adaptiveBeamComparablePasses, scout);
                return baselineOutcome;
            }
        }

        BeamBudget? scoutBudget = NoveltySearchRunner.ComputeExplorationBudget(budget, portfolio, isBoss);
        if (scoutBudget is null)
        {
            var sharedNodes = new SharedNodeBudget(budget.NodeLimit);
            return await SolveBeamPortfolioAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions,
                sharedNodes,
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct).ConfigureAwait(false);
        }

        bool runConcurrentHybrid = effectiveDop > 1
            && portfolio.SchedulingMode == PortfolioSchedulingMode.Parallel;

        if (!runConcurrentHybrid)
        {
            long beforeUnits = ledger.BudgetUnits;
            ForecastPassOutcome? scout;
            using (ledger.EnterPhase("NoveltyScout"))
            {
                var scoutShared = new SharedNodeBudget(scoutBudget.NodeLimit);
                var scoutCtrl = new MemberBudgetController(scoutBudget.NodeLimit, scoutShared);
                scout = await Task.Run(
                    () => NoveltySearchRunner.RunNoveltyPassOrNull(
                        root.Clone(),
                        scoutBudget,
                        policy,
                        portfolio,
                        baseOptions with
                        {
                            BudgetController = scoutCtrl,
                            ActivePhaseLabel = "NoveltyScout",
                            AllowPolicyUnsatisfiedSelection = true,
                        },
                        tracker?.RegisterPass(),
                        ct),
                    CancellationToken.None).ConfigureAwait(false);
            }
            if (scout is not null)
                allPasses.Add(scout);

            long scoutUnits = Math.Max(0L, ledger.BudgetUnits - beforeUnits);
            long remNodes = budget.NodeLimit - scoutUnits;
            TimeSpan remTime = budget.TimeLimit - watch.Elapsed;
            if (scout is not null
                && (remNodes <= 0 || remTime <= TimeSpan.Zero
                    || BeamIncumbentPolicy.CanFinishTargetPortfolio(
                        policy,
                        scout.Evaluator,
                        scout,
                        baseOptions.OpeningPreRootBattleHpLost,
                        portfolio.StopPortfolioAtHpTarget)))
            {
                return scout;
            }

            long beamAllowance = Math.Max(1L, remNodes);
            TimeSpan beamTime = remTime > TimeSpan.Zero ? remTime : TimeSpan.FromMilliseconds(1);
            BeamBudget remBudget = budget with
            {
                NodeLimit = (int)Math.Min(int.MaxValue, beamAllowance),
                TimeLimit = beamTime,
            };
            var beamShared = new SharedNodeBudget(remBudget.NodeLimit);
            var beamWatch = Stopwatch.StartNew();
            var serialBeamComparablePasses = new List<ForecastPassOutcome>(8);
            ForecastPassOutcome beamOutcome = await SolveBeamPortfolioAsync(
                root,
                remBudget,
                policy,
                portfolio,
                baseOptions,
                beamShared,
                beamWatch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                allPasses,
                ct,
                comparablePassesOut: serialBeamComparablePasses).ConfigureAwait(false);

            if (scout is { Selected.Won: true })
                return ChooseBeamAndScoutOutcome(beamOutcome, serialBeamComparablePasses, scout);
            return beamOutcome;
        }

        int scoutAllowance = Math.Min(scoutBudget.NodeLimit, Math.Max(1, budget.NodeLimit / 4));
        long initialBeamAllowance = Math.Max(0L, (long)budget.NodeLimit - scoutAllowance);
        var scoutExclusiveBudget = new SharedNodeBudget(scoutAllowance);
        var sharedHybridNodes = new SharedNodeBudget(initialBeamAllowance);
        using var hybridStopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var scoutController = new MemberBudgetController(scoutAllowance, scoutExclusiveBudget);
        IDisposable scoutPhase = ledger.EnterPhase("NoveltyScout");
        Task<ForecastPassOutcome?> scoutTask;
        try
        {
            scoutTask = Task.Run(() =>
            {
                try
                {
                    ForecastPassOutcome? result = NoveltySearchRunner.RunNoveltyPassOrNull(
                        root.Clone(),
                        scoutBudget with { NodeLimit = scoutAllowance },
                        policy,
                        portfolio,
                        baseOptions with
                        {
                            BudgetController = scoutController,
                            HomeLaneOffset = effectiveDop - 1,
                            ActivePhaseLabel = "HybridNovelty+Beam",
                            AllowPolicyUnsatisfiedSelection = true,
                        },
                        tracker?.RegisterPass(),
                        hybridStopCts.Token);
                    if (result is { Selected.Won: true }
                        && BeamIncumbentPolicy.CanFinishTargetPortfolio(
                            policy,
                            result.Evaluator,
                            result,
                            baseOptions.OpeningPreRootBattleHpLost,
                            portfolio.StopPortfolioAtHpTarget))
                    {
                        hybridStopCts.Cancel();
                    }
                    return result;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && hybridStopCts.IsCancellationRequested)
                {
                    // fail-open: 并发成员提前达标中止同组兄弟搜索。
                    return null;
                }
                catch (Exception)
                {
                    hybridStopCts.Cancel();
                    throw;
                }
                finally
                {
                    scoutController.ReclaimUnspent();
                    long unusedScout = scoutExclusiveBudget.TryAcquireUpTo(scoutAllowance);
                    if (unusedScout > 0)
                        sharedHybridNodes.ReleaseUnspent(unusedScout);
                    scoutPhase.Dispose();
                }
            }, CancellationToken.None);
        }
        catch
        {
            scoutPhase.Dispose();
            throw;
        }

        var beamPasses = new List<ForecastPassOutcome>(8);
        var hybridBeamComparablePasses = new List<ForecastPassOutcome>(8);
        ForecastPassOutcome? beamResult = null;
        ExceptionDispatchInfo? beamFault = null;
        try
        {
            beamResult = await SolveBeamPortfolioAsync(
                root,
                budget,
                policy,
                portfolio,
                baseOptions,
                sharedHybridNodes,
                watch,
                ledger,
                sharedPool,
                effectiveDop,
                tracker,
                beamPasses,
                hybridStopCts.Token,
                comparablePassesOut: hybridBeamComparablePasses).ConfigureAwait(false);
            if (BeamIncumbentPolicy.CanFinishTargetPortfolio(
                policy,
                beamResult.Evaluator,
                beamResult,
                baseOptions.OpeningPreRootBattleHpLost,
                portfolio.StopPortfolioAtHpTarget))
            {
                hybridStopCts.Cancel();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && hybridStopCts.IsCancellationRequested)
        {
            // fail-open: 侦察成员先达标触发早停。
        }
        catch (Exception ex)
        {
            // fail-open: 保存主梁异常并通知侦察分支退出，待双方汇合后再重抛以避免并发释放共享池。
            beamFault = ExceptionDispatchInfo.Capture(ex);
            hybridStopCts.Cancel();
        }

        ForecastPassOutcome? scoutOutcome = null;
        try
        {
            scoutOutcome = await scoutTask.ConfigureAwait(false);
        }
        catch (Exception) when (beamFault is not null)
        {
            // fail-open: 主梁已因异常退出并取消侦察分支，丢弃次级异常以保留原始主梁异常。
        }
        if (beamFault is not null)
            beamFault.Throw();
        ct.ThrowIfCancellationRequested();

        if (scoutOutcome is not null)
            allPasses.Add(scoutOutcome);
        allPasses.AddRange(beamPasses);

        if (beamResult is null && scoutOutcome is null)
            throw new OperationCanceledException(ct);
        if (beamResult is null)
            return scoutOutcome!;
        if (scoutOutcome is { Selected.Won: true })
            return ChooseBeamAndScoutOutcome(beamResult, hybridBeamComparablePasses, scoutOutcome);
        return beamResult;
    }

    private static ForecastPassOutcome ChooseBeamAndScoutOutcome(
        ForecastPassOutcome beamOutcome,
        List<ForecastPassOutcome> beamComparablePasses,
        ForecastPassOutcome scout)
    {
        if (beamOutcome.Selected.Won && (!scout.Selected.Ended || !scout.Selected.Won))
            return beamOutcome;

        var candidates = new List<ForecastPassOutcome>(beamComparablePasses.Count + 2);
        if (beamComparablePasses.Count > 0)
            candidates.AddRange(beamComparablePasses);
        else
            candidates.Add(beamOutcome);
        candidates.Add(scout);
        return PreCombatBeamPortfolio.ChooseWinningPass(
            candidates,
            beamOutcome.Evaluator.InitialHp,
            beamOutcome.Evaluator);
    }

    private static async Task<ForecastPassOutcome> SolveBeamPortfolioAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        BeamPassOptions baseOptions,
        SharedNodeBudget sharedNodes,
        Stopwatch watch,
        SearchWorkLedger ledger,
        ForecastLanePool? sharedPool,
        int effectiveDop,
        InCombatSearchTracker? tracker,
        List<ForecastPassOutcome> allPasses,
        CancellationToken ct,
        List<ForecastPassOutcome>? comparablePassesOut = null)
    {
        using (ledger.EnterPhase("Beam"))
        {
            bool hasReachablePower = PowerCardValuationModels.Registry.HasReachableRegisteredPower(root);
            bool useReallocation = portfolio.PortfolioOptionsVersion >= 2
                && portfolio.ReallocatedRefinementPortfolio
                && portfolio.UseBeamWidthPortfolio
                && !portfolio.UseNoveltyPortfolio
                && !portfolio.AdaptiveNoveltyRefinement
                && portfolio.BeamWidthPortfolioWidths is not { Count: > 0 }
                && portfolio.BeamWidthPortfolioPlainBaselineMember
                && !portfolio.OffensiveRefinementPortfolio
                && !portfolio.BoundedOffensiveRefinementPortfolio;

            IReadOnlyList<BeamWidthPortfolioMemberSpec> specs = (portfolio.UseBeamWidthPortfolio || hasReachablePower)
                ? BeamWidthPortfolio.ProductionMembers(
                    budget.BeamWidth,
                    portfolio.UseBeamWidthPortfolio ? portfolio.BeamWidthPortfolioWidths : [budget.BeamWidth],
                    includePlainBaseline: portfolio.BeamWidthPortfolioPlainBaselineMember && !useReallocation,
                    includePowerCommitmentMember: hasReachablePower,
                    useOffensiveRefinement: portfolio.OffensiveRefinementPortfolio,
                    appendBoundedOffensiveRefinement: portfolio.BoundedOffensiveRefinementPortfolio || useReallocation)
                : [new BeamWidthPortfolioMemberSpec(budget.BeamWidth)];

            BeamWidthPortfolioMemberSpec primarySpec = specs[0];
            var dedicatedPowerReserve = hasReachablePower
                ? new SharedNodeBudget(BeamWidthPortfolio.DedicatedPowerNodeReserve(budget.NodeLimit))
                : null;
            long dedicatedPowerFloor = BeamWidthPortfolio.DedicatedPowerNodeReserve(budget.NodeLimit);
            TimeSpan dedicatedPowerTime = TimeSpan.FromMilliseconds(BeamIncumbentPolicy.DedicatedMemberMilliseconds(budget.TimeLimit));
            long totalUnitsBefore = ledger.BudgetUnits;
            long primaryUnitsCap = primarySpec.AggressivePowerCommitment
                ? Math.Max(budget.NodeLimit, dedicatedPowerFloor)
                : budget.NodeLimit;
            TimeSpan primaryTimeLimit = budget.TimeLimit;

            ForecastPassOutcome primaryOutcome = (await RunMemberSpecOrNullAsync(
                root,
                budget,
                policy,
                baseOptions,
                primarySpec,
                sharedNodes,
                primarySpec.AggressivePowerCommitment ? dedicatedPowerReserve : null,
                baselineUnitsCap: primaryUnitsCap,
                memberTimeLimit: primaryTimeLimit,
                primaryIncumbent: null,
                sharedPool,
                memberDop: effectiveDop,
                homeLaneOffset: 0,
                tracker,
                allowPolicyUnsatisfied: false,
                ct).ConfigureAwait(false))!;
            allPasses.Add(primaryOutcome);

            StateEvaluator baselineEvaluator = primaryOutcome.Evaluator;
            int initialHp = baselineEvaluator.InitialHp;
            ForecastPassOutcome firstRan = primaryOutcome;
            var comparablePasses = new List<ForecastPassOutcome>(specs.Count);
            if (IsComparableMemberOutcome(primarySpec, primaryOutcome))
                comparablePasses.Add(primaryOutcome);
            ForecastPassOutcome? bestComparable = comparablePasses.Count > 0 ? primaryOutcome : null;
            ForecastPassOutcome? reusableIncumbentPass = BeamIncumbentPolicy.IsReusablePotionFreeVictory(
                policy, baselineEvaluator, primaryOutcome)
                ? primaryOutcome
                : null;

            if (specs.Count == 1
                || (bestComparable is not null
                    && BeamIncumbentPolicy.CanFinishTargetPortfolio(
                        policy,
                        baselineEvaluator,
                        bestComparable,
                        baseOptions.OpeningPreRootBattleHpLost,
                        portfolio.StopPortfolioAtHpTarget)))
            {
                return ReturnWithComparablePasses(bestComparable ?? firstRan, comparablePasses, firstRan, comparablePassesOut);
            }

            var baselineFacts = new BeamWidthPortfolioBaseline(
                FrontierExhausted: primaryOutcome.PassBoundary == ForecastSearchBoundary.None,
                ProvenZeroDamage: baselineEvaluator.IsProvenZeroDamageRoute(primaryOutcome.Selected),
                ElapsedMilliseconds: Math.Max(0L, primaryOutcome.ElapsedMilliseconds),
                ExpandedNodes: primaryOutcome.ExpandedNodes,
                BeamWidth: primarySpec.BeamWidth);

            bool parallelMembers = effectiveDop > 1
                && portfolio.SchedulingMode == PortfolioSchedulingMode.Parallel;
            long totalBudgetMs = (long)Math.Max(1d, budget.TimeLimit.TotalMilliseconds);

            if (!parallelMembers)
            {
                for (int index = 1; index < specs.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (bestComparable is not null
                        && BeamIncumbentPolicy.CanFinishTargetPortfolio(
                            policy,
                            baselineEvaluator,
                            bestComparable,
                            baseOptions.OpeningPreRootBattleHpLost,
                            portfolio.StopPortfolioAtHpTarget))
                    {
                        break;
                    }

                    BeamWidthPortfolioMemberSpec spec = specs[index];
                    long remainingNodes = sharedNodes.Remaining;
                    TimeSpan remTime = budget.TimeLimit - watch.Elapsed;
                    if (remTime <= TimeSpan.Zero && !spec.AggressivePowerCommitment)
                        break;

                    string? rejection = spec.AggressivePowerCommitment
                        ? PowerCommitmentPortfolioGate.Reject(hasReachablePower)
                        : BeamWidthPortfolioGate.RejectRefinement(
                            in baselineFacts,
                            spec.BeamWidth,
                            remainingNodes,
                            (long)Math.Max(0d, remTime.TotalMilliseconds),
                            totalBudgetMs);
                    if (rejection is not null)
                        continue;

                    long priorTotal = Math.Max(0L, ledger.BudgetUnits - totalUnitsBefore);
                    long cap = spec.AggressivePowerCommitment
                        ? Math.Max(remainingNodes, dedicatedPowerFloor)
                        : remainingNodes;
                    if (spec.BoundedRefinement)
                        cap = Math.Min(cap, priorTotal / BeamWidthPortfolio.BoundedRefinementWorkDivisor);
                    if (cap <= 0)
                        continue;

                    TimeSpan memberTime = spec.AggressivePowerCommitment && remTime < dedicatedPowerTime
                        ? (dedicatedPowerTime < budget.TimeLimit ? dedicatedPowerTime : budget.TimeLimit)
                        : (remTime > TimeSpan.Zero ? remTime : TimeSpan.FromMilliseconds(1));

                    BeamPrimarySearchIncumbent? incumbentBound = BeamIncumbentPolicy.BuildRefinementIncumbent(
                        policy, baselineEvaluator, reusableIncumbentPass);

                    ForecastPassOutcome? memberOutcome;
                    using (ledger.EnterPhase(spec.AggressivePowerCommitment ? "PortfolioPower" : "PortfolioRefinement"))
                    {
                        memberOutcome = await RunMemberSpecOrNullAsync(
                            root.Clone(),
                            budget,
                            policy,
                            baseOptions,
                            spec,
                            sharedNodes,
                            spec.AggressivePowerCommitment ? dedicatedPowerReserve : null,
                            baselineUnitsCap: cap,
                            memberTimeLimit: memberTime,
                            primaryIncumbent: incumbentBound,
                            sharedPool,
                            memberDop: effectiveDop,
                            homeLaneOffset: 0,
                            tracker,
                            allowPolicyUnsatisfied: true,
                            ct).ConfigureAwait(false);
                    }
                    if (memberOutcome is null)
                        continue;
                    allPasses.Add(memberOutcome);

                    if (IsComparableMemberOutcome(spec, memberOutcome))
                    {
                        comparablePasses.Add(memberOutcome);
                        bestComparable = PreCombatBeamPortfolio.ChooseWinningPass(
                            comparablePasses, initialHp, baselineEvaluator);
                        if (BeamIncumbentPolicy.IsReusablePotionFreeVictory(policy, baselineEvaluator, bestComparable))
                            reusableIncumbentPass = bestComparable;
                    }
                }

                return ReturnWithComparablePasses(bestComparable ?? firstRan, comparablePasses, firstRan, comparablePassesOut);
            }

            // 并行调度：按 Gate 筛选并发成员后共享 Fixed ForecastLanePool 同步执行，最终严格按 Spec 顺序比较与决胜。
            var concurrentAdmitted = new List<(int SpecIndex, BeamWidthPortfolioMemberSpec Spec)>(specs.Count);
            var deferredBounded = new List<(int SpecIndex, BeamWidthPortfolioMemberSpec Spec)>(2);
            long remMsForGate = (long)Math.Max(0d, (budget.TimeLimit - watch.Elapsed).TotalMilliseconds);
            long remNodesForGate = sharedNodes.Remaining;

            for (int index = 1; index < specs.Count; index++)
            {
                BeamWidthPortfolioMemberSpec spec = specs[index];
                string? rejection = spec.AggressivePowerCommitment
                    ? PowerCommitmentPortfolioGate.Reject(hasReachablePower)
                    : BeamWidthPortfolioGate.RejectRefinement(
                        in baselineFacts,
                        spec.BeamWidth,
                        remNodesForGate,
                        remMsForGate,
                        totalBudgetMs);
                if (rejection is not null)
                    continue;

                if (spec.BoundedRefinement)
                    deferredBounded.Add((index, spec));
                else
                    concurrentAdmitted.Add((index, spec));
            }

            if (concurrentAdmitted.Count > 0)
            {
                using (ledger.EnterPhase("BeamPortfolio"))
                {
                    BeamPrimarySearchIncumbent? incumbentBound = BeamIncumbentPolicy.BuildRefinementIncumbent(
                        policy, baselineEvaluator, reusableIncumbentPass);

                    var slotCts = new CancellationTokenSource[concurrentAdmitted.Count];
                    for (int c = 0; c < concurrentAdmitted.Count; c++)
                        slotCts[c] = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var results = new ForecastPassOutcome?[concurrentAdmitted.Count];
                    var tasks = new Task[concurrentAdmitted.Count];

                    try
                    {
                        for (int c = 0; c < concurrentAdmitted.Count; c++)
                        {
                            int slotIndex = c;
                            tasks[c] = Task.Run(async () =>
                            {
                                CancellationToken slotToken = slotCts[slotIndex].Token;
                                BeamWidthPortfolioMemberSpec spec = concurrentAdmitted[slotIndex].Spec;
                                try
                                {
                                    TimeSpan remTime = budget.TimeLimit - watch.Elapsed;
                                    TimeSpan memberTime = spec.AggressivePowerCommitment && remTime < dedicatedPowerTime
                                        ? (dedicatedPowerTime < budget.TimeLimit ? dedicatedPowerTime : budget.TimeLimit)
                                        : (remTime > TimeSpan.Zero ? remTime : TimeSpan.FromMilliseconds(1));
                                    long cap = spec.AggressivePowerCommitment
                                        ? Math.Max(budget.NodeLimit, dedicatedPowerFloor)
                                        : budget.NodeLimit;

                                    ForecastPassOutcome? outcome = await RunMemberSpecOrNullAsync(
                                        root.Clone(),
                                        budget,
                                        policy,
                                        baseOptions,
                                        spec,
                                        sharedNodes,
                                        spec.AggressivePowerCommitment ? dedicatedPowerReserve : null,
                                        baselineUnitsCap: cap,
                                        memberTimeLimit: memberTime,
                                        primaryIncumbent: incumbentBound,
                                        sharedPool,
                                        memberDop: effectiveDop,
                                        homeLaneOffset: (slotIndex + 1) % effectiveDop,
                                        tracker,
                                        allowPolicyUnsatisfied: true,
                                        slotToken).ConfigureAwait(false);
                                    results[slotIndex] = outcome;

                                    if (outcome is not null
                                        && BeamIncumbentPolicy.CanFinishTargetPortfolio(
                                            policy,
                                            baselineEvaluator,
                                            outcome,
                                            baseOptions.OpeningPreRootBattleHpLost,
                                            portfolio.StopPortfolioAtHpTarget))
                                    {
                                        for (int j = slotIndex + 1; j < slotCts.Length; j++)
                                            slotCts[j].Cancel();
                                    }
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested && slotCts[slotIndex].IsCancellationRequested)
                                {
                                    // fail-open: 更高优先级成员提前满足目标，按序取消更低优先级分支。
                                }
                            }, CancellationToken.None);
                        }

                        try
                        {
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                        catch
                        {
                            // fail-open: 在重抛异常或外部取消之前确保所有并发成员结束，避免共享池被提前释放。
                            for (int i = 0; i < slotCts.Length; i++)
                                slotCts[i].Cancel();
                            try
                            {
                                await Task.WhenAll(tasks).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // fail-open: 汇合清理阶段忽略次级异常。
                            }
                            throw;
                        }
                    }
                    finally
                    {
                        for (int i = 0; i < slotCts.Length; i++)
                            slotCts[i].Dispose();
                    }
                    ct.ThrowIfCancellationRequested();

                    for (int c = 0; c < concurrentAdmitted.Count; c++)
                    {
                        ForecastPassOutcome? outcome = results[c];
                        if (outcome is null)
                            continue;
                        allPasses.Add(outcome);
                        BeamWidthPortfolioMemberSpec spec = concurrentAdmitted[c].Spec;
                        if (IsComparableMemberOutcome(spec, outcome))
                        {
                            comparablePasses.Add(outcome);
                            bestComparable = PreCombatBeamPortfolio.ChooseWinningPass(
                                comparablePasses, initialHp, baselineEvaluator);
                            if (BeamIncumbentPolicy.IsReusablePotionFreeVictory(policy, baselineEvaluator, bestComparable))
                                reusableIncumbentPass = bestComparable;
                        }
                    }
                }
            }

            if (deferredBounded.Count > 0
                && (bestComparable is null
                    || !BeamIncumbentPolicy.CanFinishTargetPortfolio(
                        policy,
                        baselineEvaluator,
                        bestComparable,
                        baseOptions.OpeningPreRootBattleHpLost,
                        portfolio.StopPortfolioAtHpTarget)))
            {
                using (ledger.EnterPhase("PortfolioRefinement"))
                {
                    for (int d = 0; d < deferredBounded.Count; d++)
                    {
                        ct.ThrowIfCancellationRequested();
                        BeamWidthPortfolioMemberSpec spec = deferredBounded[d].Spec;
                        long priorTotal = Math.Max(0L, ledger.BudgetUnits - totalUnitsBefore);
                        long remainingNodes = sharedNodes.Remaining;
                        TimeSpan remTime = budget.TimeLimit - watch.Elapsed;
                        if (remTime <= TimeSpan.Zero || remainingNodes <= 0)
                            break;
                        string? rejection = BeamWidthPortfolioGate.RejectRefinement(
                            in baselineFacts,
                            spec.BeamWidth,
                            remainingNodes,
                            (long)remTime.TotalMilliseconds,
                            totalBudgetMs);
                        if (rejection is not null)
                            continue;

                        long boundedCap = Math.Min(remainingNodes, priorTotal / BeamWidthPortfolio.BoundedRefinementWorkDivisor);
                        if (boundedCap <= 0)
                            continue;

                        BeamPrimarySearchIncumbent? incumbentBound = BeamIncumbentPolicy.BuildRefinementIncumbent(
                            policy, baselineEvaluator, reusableIncumbentPass);
                        ForecastPassOutcome? boundedOutcome = await RunMemberSpecOrNullAsync(
                            root.Clone(),
                            budget,
                            policy,
                            baseOptions,
                            spec,
                            sharedNodes,
                            dedicatedPowerReserve: null,
                            baselineUnitsCap: boundedCap,
                            memberTimeLimit: remTime,
                            primaryIncumbent: incumbentBound,
                            sharedPool,
                            memberDop: effectiveDop,
                            homeLaneOffset: 0,
                            tracker,
                            allowPolicyUnsatisfied: true,
                            ct).ConfigureAwait(false);
                        if (boundedOutcome is null)
                            continue;
                        allPasses.Add(boundedOutcome);
                        if (IsComparableMemberOutcome(spec, boundedOutcome))
                        {
                            comparablePasses.Add(boundedOutcome);
                            bestComparable = PreCombatBeamPortfolio.ChooseWinningPass(
                                comparablePasses, initialHp, baselineEvaluator);
                        }
                    }
                }
            }

            return ReturnWithComparablePasses(bestComparable ?? firstRan, comparablePasses, firstRan, comparablePassesOut);
        }

        static ForecastPassOutcome ReturnWithComparablePasses(
            ForecastPassOutcome chosen,
            List<ForecastPassOutcome> comparablePasses,
            ForecastPassOutcome firstRan,
            List<ForecastPassOutcome>? comparablePassesOut)
        {
            if (comparablePassesOut is not null)
            {
                if (comparablePasses.Count > 0)
                    comparablePassesOut.AddRange(comparablePasses);
                else
                    comparablePassesOut.Add(firstRan);
            }
            return chosen;
        }
    }

    private static Task<ForecastPassOutcome?> RunMemberSpecOrNullAsync(
        CombatState root,
        BeamBudget fullBudget,
        CombatPolicy policy,
        BeamPassOptions baseOptions,
        BeamWidthPortfolioMemberSpec spec,
        SharedNodeBudget sharedNodes,
        SharedNodeBudget? dedicatedPowerReserve,
        long baselineUnitsCap,
        TimeSpan memberTimeLimit,
        BeamPrimarySearchIncumbent? primaryIncumbent,
        ForecastLanePool? sharedPool,
        int memberDop,
        int homeLaneOffset,
        InCombatSearchTracker? tracker,
        bool allowPolicyUnsatisfied,
        CancellationToken ct)
    {
        BeamMemberSearchMode mode = spec.AggressivePowerCommitment
            ? BeamMemberSearchMode.AggressivePowerCommitment
            : spec.BaseScoreOnly
                ? BeamMemberSearchMode.BaseScoreOnly
                : spec.OffensiveRefinement
                    ? BeamMemberSearchMode.OffensiveRefinement
                    : BeamMemberSearchMode.Default;

        BeamBudget memberBudget = fullBudget with
        {
            BeamWidth = spec.BeamWidth,
            NodeLimit = (int)Math.Clamp(baselineUnitsCap, 1L, int.MaxValue),
            TimeLimit = memberTimeLimit > TimeSpan.Zero ? memberTimeLimit : TimeSpan.FromMilliseconds(1),
        };

        long dedicatedFloor = spec.AggressivePowerCommitment && dedicatedPowerReserve is not null
            ? Math.Min(baselineUnitsCap, BeamWidthPortfolio.DedicatedPowerNodeReserve(fullBudget.NodeLimit))
            : 0L;
        var controller = new MemberBudgetController(
            baselineUnitsCap,
            sharedNodes,
            dedicatedPowerFloor: dedicatedFloor,
            dedicatedPowerReserve: spec.AggressivePowerCommitment ? dedicatedPowerReserve : null);

        BeamPassOptions memberOptions = baseOptions with
        {
            MemberMode = mode,
            SecondRankBand = spec.SecondRankBand,
            PrimaryIncumbent = primaryIncumbent,
            DisablePrimaryIncumbentPruning = primaryIncumbent is null,
            BudgetController = controller,
            SharedLanePool = memberDop > 1 ? sharedPool : null,
            HomeLaneOffset = homeLaneOffset,
            MaxConcurrency = Math.Max(1, memberDop),
            AllowPolicyUnsatisfiedSelection = allowPolicyUnsatisfied,
            ActivePhaseLabel = spec.AggressivePowerCommitment
                ? "PortfolioPower"
                : spec.OffensiveRefinement
                    ? "PortfolioOffense"
                    : "BeamPortfolio",
        };

        return BeamSearch.RunSingleForecastPassOrNullAsync(
            root,
            memberBudget,
            policy,
            ct,
            aggressivePowerCommitment: spec.AggressivePowerCommitment,
            openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
            maxDegreeOfParallelism: Math.Max(1, memberDop),
            inCombatPassTrack: tracker?.RegisterPass(),
            passOptions: memberOptions);
    }

    private static async Task<ForecastPassOutcome> SolveLegacyV1BeamPortfolioAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        PreCombatPortfolioOptions portfolio,
        BeamPassOptions baseOptions,
        Stopwatch watch,
        SearchWorkLedger ledger,
        ForecastLanePool? sharedPool,
        int effectiveDop,
        InCombatSearchTracker? tracker,
        List<ForecastPassOutcome> allPasses,
        CancellationToken ct)
    {
        if (!portfolio.UseBeamWidthPortfolio)
        {
            ForecastPassOutcome single = await BeamSearch.RunSingleForecastPassAsync(
                root,
                budget,
                policy,
                ct,
                openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                maxDegreeOfParallelism: effectiveDop,
                inCombatPassTrack: tracker?.RegisterPass(),
                passOptions: baseOptions).ConfigureAwait(false);
            allPasses.Add(single);
            return single;
        }

        bool hasReachablePower = HasLegacyReachablePower(root);
        int member0Nodes = PreCombatBeamPortfolio.ComputeMember0NodeLimit(budget.NodeLimit, hasReachablePower);
        BeamBudget member0Budget = budget with { NodeLimit = member0Nodes };

        if (effectiveDop >= 2 && portfolio.SchedulingMode == PortfolioSchedulingMode.Parallel)
        {
            return await SolveLegacyV1ConcurrentAsync(
                root,
                budget,
                policy,
                baseOptions,
                watch,
                sharedPool,
                hasReachablePower,
                member0Nodes,
                member0Budget,
                effectiveDop,
                tracker,
                allPasses,
                ct).ConfigureAwait(false);
        }

        ForecastPassOutcome member0 = await BeamSearch.RunSingleForecastPassAsync(
            root,
            member0Budget,
            policy,
            ct,
            openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
            maxDegreeOfParallelism: 1,
            inCombatPassTrack: tracker?.RegisterPass(),
            passOptions: baseOptions).ConfigureAwait(false);
        allPasses.Add(member0);

        long totalExpanded = member0.ExpandedNodes;
        long remainingNodes = budget.NodeLimit - totalExpanded;
        TimeSpan remainingTime = budget.TimeLimit - watch.Elapsed;

        if (member0.AcceptableVictoryFound || remainingNodes <= 0 || remainingTime <= TimeSpan.Zero)
            return member0;

        if (hasReachablePower)
        {
            int powerNodes = checked((int)(remainingNodes <= 2
                ? remainingNodes
                : Math.Max(1L, remainingNodes * 3 / 4)));
            BeamBudget powerBudget = budget with { NodeLimit = powerNodes, TimeLimit = remainingTime };
            long beforePower = ledger.ChoiceReplayAttempts;
            ForecastPassOutcome? powerMember = await BeamSearch.RunSingleForecastPassOrNullAsync(
                root,
                powerBudget,
                policy,
                ct,
                aggressivePowerCommitment: true,
                openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                maxDegreeOfParallelism: 1,
                inCombatPassTrack: tracker?.RegisterPass(),
                passOptions: baseOptions with
                {
                    MemberMode = BeamMemberSearchMode.AggressivePowerCommitment,
                    AllowPolicyUnsatisfiedSelection = true,
                }).ConfigureAwait(false);
            long powerExpanded = powerMember?.ExpandedNodes ?? Math.Max(0L, ledger.ChoiceReplayAttempts - beforePower);
            totalExpanded += powerExpanded;
            remainingNodes = budget.NodeLimit - totalExpanded;
            remainingTime = budget.TimeLimit - watch.Elapsed;
            if (powerMember is not null)
                allPasses.Add(powerMember);

            if (powerMember is not { AcceptableVictoryFound: true }
                && remainingNodes > 0
                && remainingTime > TimeSpan.Zero)
            {
                ForecastPassOutcome? narrow = await RunLegacyNarrowMemberOrNullAsync(
                    root, budget, policy, baseOptions, remainingNodes, remainingTime, 1, tracker, ct).ConfigureAwait(false);
                if (narrow is not null)
                    allPasses.Add(narrow);
            }
        }
        else
        {
            ForecastPassOutcome? narrow = await RunLegacyNarrowMemberOrNullAsync(
                root, budget, policy, baseOptions, remainingNodes, remainingTime, 1, tracker, ct).ConfigureAwait(false);
            if (narrow is not null)
                allPasses.Add(narrow);
        }

        return PreCombatBeamPortfolio.ChooseWinningPass(
            allPasses,
            root.Players[0].Creature.CurrentHp,
            member0.Evaluator);
    }

    private static async Task<ForecastPassOutcome> SolveLegacyV1ConcurrentAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        BeamPassOptions baseOptions,
        Stopwatch watch,
        ForecastLanePool? sharedPool,
        bool hasReachablePower,
        int member0Nodes,
        BeamBudget member0Budget,
        int effectiveDop,
        InCombatSearchTracker? tracker,
        List<ForecastPassOutcome> allPasses,
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
                openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                maxDegreeOfParallelism: effectiveDop,
                inCombatPassTrack: tracker?.RegisterPass(),
                passOptions: baseOptions).ConfigureAwait(false);
            allPasses.Add(single);
            return single;
        }

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
        long primaryCanceledExpanded = 0;
        long secondaryCanceledExpanded = 0;

        Task<ForecastPassOutcome?> primaryTask = Task.Run(async () =>
        {
            try
            {
                ForecastPassOutcome? outcome = await BeamSearch.RunSingleForecastPassOrNullAsync(
                    root.Clone(),
                    member0Budget,
                    policy,
                    earlyStopCts.Token,
                    aggressivePowerCommitment: false,
                    openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                    maxDegreeOfParallelism: primaryDop,
                    inCombatPassTrack: primaryTrack,
                    onCanceledExpanded: spent => Interlocked.Exchange(ref primaryCanceledExpanded, spent),
                    passOptions: baseOptions with
                    {
                        SharedLanePool = primaryDop > 1 ? sharedPool : null,
                        HomeLaneOffset = 0,
                        MaxConcurrency = primaryDop,
                    }).ConfigureAwait(false);
                if (outcome is { AcceptableVictoryFound: true })
                    earlyStopCts.Cancel();
                return outcome;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && earlyStopCts.IsCancellationRequested)
            {
                // fail-open: 并行组合成员命中可接受胜利触发 earlyStopCts 提前中止其余分支，外部未取消时由已获胜成员完成返回。
                return null;
            }
        }, CancellationToken.None);

        Task<ForecastPassOutcome?> secondaryTask = Task.Run(async () =>
        {
            try
            {
                ForecastPassOutcome? outcome = await BeamSearch.RunSingleForecastPassOrNullAsync(
                    root.Clone(),
                    secondaryBudget,
                    policy,
                    earlyStopCts.Token,
                    aggressivePowerCommitment: hasReachablePower,
                    openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
                    maxDegreeOfParallelism: secondaryDop,
                    inCombatPassTrack: secondaryTrack,
                    onCanceledExpanded: spent => Interlocked.Exchange(ref secondaryCanceledExpanded, spent),
                    passOptions: baseOptions with
                    {
                        MemberMode = hasReachablePower
                            ? BeamMemberSearchMode.AggressivePowerCommitment
                            : BeamMemberSearchMode.Default,
                        SharedLanePool = secondaryDop > 1 ? sharedPool : null,
                        HomeLaneOffset = primaryDop % effectiveDop,
                        MaxConcurrency = secondaryDop,
                        AllowPolicyUnsatisfiedSelection = true,
                    }).ConfigureAwait(false);
                if (outcome is { AcceptableVictoryFound: true })
                    earlyStopCts.Cancel();
                return outcome;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && earlyStopCts.IsCancellationRequested)
            {
                // fail-open: 并行组合成员命中可接受胜利触发 earlyStopCts 提前中止其余分支，外部未取消时由已获胜成员完成返回。
                return null;
            }
        }, CancellationToken.None);

        ForecastPassOutcome?[] initialResults;
        try
        {
            initialResults = await Task.WhenAll(primaryTask, secondaryTask).ConfigureAwait(false);
        }
        catch
        {
            // fail-open: 任一分支异常或取消时先通知另一分支停止并等待汇合，避免共享池提前释放。
            earlyStopCts.Cancel();
            try
            {
                await Task.WhenAll(primaryTask, secondaryTask).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // fail-open: 清理汇合阶段忽略次级异常，保留原始故障。
            }
            throw;
        }
        ct.ThrowIfCancellationRequested();

        var passes = new List<ForecastPassOutcome>(3);
        long canceledExpanded = 0;
        if (initialResults[0] is { } p0)
            passes.Add(p0);
        else
            canceledExpanded += Volatile.Read(ref primaryCanceledExpanded);
        if (initialResults[1] is { } p1)
            passes.Add(p1);
        else
            canceledExpanded += Volatile.Read(ref secondaryCanceledExpanded);

        long totalExpanded = canceledExpanded;
        bool anyAcceptableVictory = false;
        for (int i = 0; i < passes.Count; i++)
        {
            totalExpanded += passes[i].ExpandedNodes;
            if (passes[i].AcceptableVictoryFound)
                anyAcceptableVictory = true;
        }
        if (hasReachablePower && !anyAcceptableVictory)
        {
            long remainingNodes = budget.NodeLimit - totalExpanded;
            TimeSpan remainingTime = budget.TimeLimit - watch.Elapsed;
            if (remainingNodes > 0 && remainingTime > TimeSpan.Zero)
            {
                ForecastPassOutcome? narrow = await RunLegacyNarrowMemberOrNullAsync(
                    root.Clone(), budget, policy, baseOptions, remainingNodes, remainingTime, effectiveDop, tracker, ct).ConfigureAwait(false);
                if (narrow is not null)
                    passes.Add(narrow);
            }
        }

        if (passes.Count == 0)
            throw new InvalidOperationException("Forecast portfolio produced no policy-satisfied outcome.");

        allPasses.AddRange(passes);
        return PreCombatBeamPortfolio.ChooseWinningPass(
            passes,
            root.Players[0].Creature.CurrentHp,
            passes[0].Evaluator);
    }

    private static Task<ForecastPassOutcome?> RunLegacyNarrowMemberOrNullAsync(
        CombatState root,
        BeamBudget budget,
        CombatPolicy policy,
        BeamPassOptions baseOptions,
        long remainingNodes,
        TimeSpan remainingTime,
        int maxDegreeOfParallelism,
        InCombatSearchTracker? tracker,
        CancellationToken ct)
    {
        int secondaryWidth = Math.Max(12, budget.BeamWidth / 2);
        BeamBudget narrowBudget = budget with
        {
            BeamWidth = secondaryWidth,
            NodeLimit = (int)Math.Clamp(remainingNodes, 1L, int.MaxValue),
            TimeLimit = remainingTime,
        };
        return BeamSearch.RunSingleForecastPassOrNullAsync(
            root,
            narrowBudget,
            policy,
            ct,
            openingPreRootBattleHpLost: baseOptions.OpeningPreRootBattleHpLost,
            maxDegreeOfParallelism: maxDegreeOfParallelism,
            inCombatPassTrack: tracker?.RegisterPass(),
            passOptions: baseOptions with
            {
                AllowPolicyUnsatisfiedSelection = true,
                SharedLanePool = maxDegreeOfParallelism > 1 ? baseOptions.SharedLanePool : null,
                MaxConcurrency = Math.Max(1, maxDegreeOfParallelism),
            });
    }

    private static bool HasLegacyReachablePower(CombatState root)
    {
        PlayerCombatState? pcs = root.Players[0].PlayerCombatState;
        if (pcs is null)
            return false;
        return HasPowerInPile(pcs.Hand)
            || HasPowerInPile(pcs.DrawPile)
            || HasPowerInPile(pcs.DiscardPile);

        static bool HasPowerInPile(CardPile pile)
        {
            IReadOnlyList<CardModel> cards = pile.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Type == CardType.Power)
                    return true;
            }
            return false;
        }
    }

    private static bool IsComparableMemberOutcome(
        BeamWidthPortfolioMemberSpec spec,
        ForecastPassOutcome outcome)
    {
        bool won = outcome.Selected.Won;
        bool terminal = won || !outcome.Selected.State.Players[0].Creature.IsAlive;
        if (spec.AggressivePowerCommitment)
            return terminal;
        return terminal || outcome.PassBoundary != ForecastSearchBoundary.NodeLimit;
    }

    private static EvaluatedForecastCandidate[] CombineCandidates(List<ForecastPassOutcome> passes)
    {
        int totalCount = 0;
        for (int i = 0; i < passes.Count; i++)
            totalCount += passes[i].EvaluatedCandidates.Length;
        var combined = new EvaluatedForecastCandidate[totalCount];
        int offset = 0;
        for (int i = 0; i < passes.Count; i++)
        {
            EvaluatedForecastCandidate[] src = passes[i].EvaluatedCandidates;
            Array.Copy(src, 0, combined, offset, src.Length);
            offset += src.Length;
        }
        return combined;
    }
}
