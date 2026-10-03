using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace OfflineSearchHarness;

/// <summary>
/// Opt-in long-root measurement of the production fixed-prefix Solve path. Root setup,
/// warmup, result checks and serialization are outside the measured Solve interval.
/// This is an offline cost comparison, not native correctness or general search speed evidence.
/// </summary>
internal static class FixedPrefixContinuationBenchmark
{
    private const int MeasuredSamples = 3;
    private const int BudgetMilliseconds = 5000;

    internal static object Run(CombatState combat, HarnessOptions options, MainLoopContext loop)
    {
        if (options.RequestPath == null
            || GeneratedScenarioSetup.ReadRequest(options.RequestPath).ScenarioId
                != "GENERIC-CROSS-TURN-HIDDEN-BUFFER-POSITIVE-V0111")
            throw new ArgumentException("Fixed-prefix benchmark requires generic-cross-turn-hidden-buffer-positive-v0111.json.");
        if (options.VerifyIncremental || options.EnableNoGcRegion || options.ProductionBudget
            || options.MaxDegreeOfParallelism != 1 || options.SearchMode != "Evaluate")
            throw new ArgumentException("Fixed-prefix benchmark requires fixed Evaluate/DOP1, without incremental verification or NoGC.");
        Player player = combat.Players.Single();
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("Fixed-prefix benchmark requires a playable root.");
        if (playerState.Hand.Cards.Count != 1 || playerState.Hand.Cards[0].Id.Entry != "STRIKE_IRONCLAD"
            || playerState.DrawPile.Cards.Count != 0 || playerState.DiscardPile.Cards.Count != 0
            || playerState.ExhaustPile.Cards.Count != 0 || combat.Enemies.Count != 1)
            throw new InvalidOperationException("Fixed-prefix benchmark requires one Strike, empty other piles and one enemy.");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan Remaining()
        {
            deadline.Token.ThrowIfCancellationRequested();
            TimeSpan remaining = TimeSpan.FromSeconds(100) - elapsed.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : throw new TimeoutException("Fixed-prefix benchmark exceeded 100 seconds.");
        }
        List<object> cases = [];
        foreach (int turns in new[] { 4, 8, 17 })
        {
            // Solves never advance live state; only the enemy Buffer count differs between roots.
            // Reuse the existing fixture injector, which is also available in the saved baseline DLL.
            var injection = UnattendedTestRunner.OfflineScenarioSession.Create(new UnattendedTestRequest
            {
                EnemyCurrentHp = 6,
                InitialEnemyBlocks = [0],
                InitialPlayerMaxHp = 999,
                InitialPlayerHp = 999,
                InitialPlayerBlock = 0,
                InitialPlayerEnergy = 3,
                Cards = [],
                ClearAllPowers = true,
                Powers =
                [
                    new UnattendedPowerInjection { PowerId = "BUFFER_POWER", Target = "Enemy", TargetIndex = 0, Amount = turns - 1 },
                    new UnattendedPowerInjection { PowerId = "BUFFER_POWER", Target = "Player", Amount = 64 },
                ],
            });
            loop.RunUntilCompleted(injection.InjectInitialStateAsync(combat, player), Remaining(), $"N{turns} fixture");
            string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null) with
            {
                FixedBudget = true,
                VerifyIncrementalSearch = false,
                DetailedDiagnostics = false,
                MeasurePhasePerformance = false,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = BudgetMilliseconds,
                PotionPolicy = SolverPotionPolicy.Disabled,
                PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
                UseBeamWidthPortfolio = false,
                UseNoveltyPortfolio = false,
                Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }),
                Profile = SolverSearchProfile.Default with
                {
                    BeamWidth = 8, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = BudgetMilliseconds,
                },
            };
            List<PlanAction> prefix = [];
            for (int offset = 0; offset < turns; offset++)
            {
                int turn = root.StartTurnNumber + offset;
                prefix.Add(new PlanAction(PlanActionKind.PlayCard, turn,
                    CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId));
                if (offset + 1 < turns)
                    prefix.Add(new PlanAction(PlanActionKind.EndTurn, turn));
            }

            // One worker owns all measurements, so current-thread allocation includes the entire
            // synchronous DOP1 Solve and excludes task scheduling, setup and evidence serialization.
            Task<CaseResult> task = Task.Run(() => Measure(root, names, damage, policy, prefix, turns, deadline.Token));
            loop.RunUntilCompleted(task, Remaining(), $"N{turns} fixed-prefix Solve");
            CaseResult measured = task.GetAwaiter().GetResult();
            if (!string.Equals(liveBefore, ContinuationStamp.CaptureLive(combat).StateText, StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixed-prefix benchmark N{turns} changed the live root.");
            cases.Add(new
            {
                turns,
                prefixActionCount = prefix.Count,
                rootStateText = root.ContinuationStamp.StateText,
                policy = ModRuntime.DescribePolicy(policy),
                samples = measured.Samples,
                independentPrefixOracle = "Passed: complete StateText, turn, offset, order and count; outside measured samples",
                annotatedResult = measured.AnnotatedResult,
            });
            foreach (Sample sample in measured.Samples)
                Console.WriteLine($"FIXED_PREFIX_CONTINUATIONS n={turns} sample={sample.Index} "
                    + $"solve_ms={sample.SolveMilliseconds:F3} allocated_current_thread={sample.CurrentThreadAllocatedBytes} "
                    + $"continuations={sample.ContinuationCount} replay_count={sample.ReplayCount} "
                    + $"expanded={sample.ExpandedNodes} transitions={sample.TransitionCount}");
        }
        object report = new
        {
            status = "Passed",
            label = options.Label,
            sourceRequest = options.RequestPath,
            solverDll = AssemblyBootstrap.CombatSolverDll,
            solverAssemblyId = typeof(CombatBeamSolver).Module.ModuleVersionId,
            scope = "long deterministic fixed-prefix roots only; offline costs, no general speed claim or native validation",
            warmupsPerCase = 1,
            measuredSamplesPerCase = MeasuredSamples,
            budgetMilliseconds = BudgetMilliseconds,
            maxExpandedNodes = 100,
            verifyIncrementalSearch = false,
            cases,
        };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "fixed-prefix-continuations.json"),
            JsonSerializer.Serialize(report, UnattendedTestFiles.JsonOptions));
        return report;
    }

    private static CaseResult Measure(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, IReadOnlyList<PlanAction> prefix,
        int turns, CancellationToken cancellationToken)
    {
        SolverResult warmup = CreateSolver().Solve();
        Validate(warmup);
        string expected = DescribeResult(warmup);
        List<Sample> samples = [];
        for (int index = 0; index < MeasuredSamples; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatBeamSolver solver = CreateSolver();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            SolverResult result = solver.Solve();
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Validate(result);
            if (!string.Equals(expected, DescribeResult(result), StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixed-prefix benchmark N{turns} changed annotated result between samples.");
            samples.Add(new Sample(index, milliseconds, allocated, result.Elapsed.TotalMilliseconds,
                result.TotalWorkerAllocatedBytes, result.Continuations.Count, result.ReplayCount,
                result.ForkCount, result.ExpandedNodes, result.TransitionCount));
        }
        VerifyIndependentPrefixes(root, names, damage, policy, warmup, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(expected);
        return new CaseResult(samples, document.RootElement.Clone());

        CombatBeamSolver CreateSolver() => new(root, names, damage, policy, cancellationToken,
            searchProfile: policy.Profile, fixedPrefixActions: prefix);

        void Validate(SolverResult result)
        {
            if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead
                || result.Snapshot.CumulativePlayerHpLost != 0
                || result.CombatEndedTurn != root.StartTurnNumber + turns - 1
                || result.BestNode.Actions.Count != prefix.Count
                || result.Continuations.Count != turns - 1 || !result.TryValidateTurnOutcomes(out _)
                || result.BoundaryReason == SearchBoundaryReason.TimeLimit)
                throw new InvalidOperationException($"Fixed-prefix benchmark N{turns} did not complete its deterministic prefix.");
            for (int index = 0; index < prefix.Count; index++)
            {
                PlanAction expectedAction = prefix[index], actual = result.BestNode.Actions[index];
                if (actual.Kind != expectedAction.Kind || actual.Turn != expectedAction.Turn
                    || actual.CardId != expectedAction.CardId || actual.TargetCombatId != expectedAction.TargetCombatId)
                    throw new InvalidOperationException($"Fixed-prefix benchmark N{turns} changed action {index}.");
            }
            for (int index = 0; index < result.Continuations.Count; index++)
            {
                CachedContinuation continuation = result.Continuations[index];
                if (continuation.StartTurnNumber != root.StartTurnNumber + index + 1
                    || continuation.ForecastOffset != index + 1)
                    throw new InvalidOperationException($"Fixed-prefix benchmark N{turns} changed continuation {index}.");
            }
        }
    }

    private static void VerifyIndependentPrefixes(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverResult result,
        CancellationToken cancellationToken)
    {
        CombatBeamSolver oracle = new(root, names, damage, policy, cancellationToken, searchProfile: policy.Profile);
        MethodInfo replay = typeof(CombatBeamSolver).GetMethod("Replay", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CombatBeamSolver).FullName, "Replay");
        ParameterInfo[] parameters = replay.GetParameters();
        if (parameters.Length is not (14 or 15)
            || parameters[0].Name != "actions" || parameters[1].Name != "parentSnapshot"
            || parameters[2].Name != "startingTurn" || parameters[3].Name != "priorActionCount"
            || parameters[12].Name != "countTransition" || parameters[13].Name != "allowExecutionCapture"
            || parameters.Length == 15 && parameters[14].Name != "continuationCapture")
            throw new InvalidOperationException("Unexpected Replay signature in fixed-prefix oracle.");
        int continuationIndex = 0;
        IReadOnlyList<PlanAction> actions = result.BestNode.Actions;
        for (int index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanAction action = actions[index];
            if (action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn)
                continue;
            // Keep this old prefix algorithm exclusively in the benchmark oracle. The runtime
            // assembly may be baseline (14 args) or current (15 args); capture stays null in both.
            object?[] arguments = new object?[parameters.Length];
            arguments[0] = actions.Take(index + 1).ToArray();
            arguments[2] = root.StartTurnNumber;
            arguments[3] = 0;
            arguments[12] = true;
            arguments[13] = true;
            SimulationSnapshot snapshot;
            try
            {
                snapshot = (SimulationSnapshot)(replay.Invoke(oracle, arguments)
                    ?? throw new InvalidOperationException("Independent prefix Replay returned null."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            try
            {
                if (snapshot.PlayerDead || snapshot.AllEnemiesDead
                    || snapshot.BoundaryReason != SearchBoundaryReason.None
                    || !actions.Skip(index + 1).Any(later => later.Turn == snapshot.Turn))
                    continue;
                if (continuationIndex >= result.Continuations.Count)
                    throw new InvalidOperationException($"Independent prefix oracle: missing continuation {continuationIndex}.");
                CachedContinuation actual = result.Continuations[continuationIndex++];
                ContinuationStamp expected = ContinuationStamp.CapturePredicted(root.PlayerIdentity,
                    snapshot.Simulator, snapshot.Turn, root.Forecast, root.StartTurnNumber);
                if (actual.StartTurnNumber != snapshot.Turn
                    || actual.ForecastOffset != snapshot.Turn - root.StartTurnNumber
                    || !string.Equals(expected.StateText, actual.ExpectedState.StateText, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Independent prefix oracle at action {index}: "
                        + $"turn={snapshot.Turn}/{actual.StartTurnNumber} "
                        + $"offset={snapshot.Turn - root.StartTurnNumber}/{actual.ForecastOffset} "
                        + expected.DescribeFirstDifference(actual.ExpectedState));
            }
            finally { snapshot.ReleaseSimulator(); }
        }
        if (continuationIndex != result.Continuations.Count)
            throw new InvalidOperationException("Independent prefix oracle: extra continuations.");
    }

    private static string DescribeResult(SolverResult result)
        => JsonSerializer.Serialize(new
        {
            result.ResultScope,
            result.StartTurnNumber,
            result.BestNode,
            result.Snapshot,
            result.BoundaryReason,
            result.FutureSoldHp,
            result.ProjectedBattleHpLost,
            result.CombatEndedTurn,
            result.DeathTurn,
            result.PotionCount,
            result.PotionUses,
            result.HpLostByTurn,
            result.HpRecoveredByTurn,
            result.EnemyHpLostByTurn,
            result.SoldHpByTurn,
            result.MaxBlockByTurn,
            result.ActualBlockByTurn,
            result.EnergyLeftByTurn,
            result.PotionCountByTurn,
            result.PotionStrategicCostByTurn,
            result.KillsAfterAction,
            result.TurnSetupChoices,
            result.TurnSetupPlayState,
            continuations = result.Continuations.Select(item => new
            {
                item.StartTurnNumber, item.ForecastOffset, item.ExpectedState.StateText,
            }).ToArray(),
        }, UnattendedTestFiles.JsonOptions);

    private sealed record CaseResult(IReadOnlyList<Sample> Samples, JsonElement AnnotatedResult);
    private sealed record Sample(int Index, double SolveMilliseconds, long CurrentThreadAllocatedBytes,
        double ReportedSolveMilliseconds, long ReportedWorkerAllocatedBytes, int ContinuationCount,
        int ReplayCount, int ForkCount, int ExpandedNodes, int TransitionCount);
}
