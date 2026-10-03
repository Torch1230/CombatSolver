using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertChoiceContinuationStepAuditAsync(CombatState combat)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        { FixedBudget = true, VerifyIncrementalSearch = false, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null };
        var profile = policy.Profile with { MaxExpandedNodes = 20000 };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        var result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy, deadline.Token,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
            maximumPotionUses: 2, minimumPotionUses: 2)
            { VerifyChoiceContinuationStepsForTesting = true }.Solve());
        if (result.ExecutionChoiceReuses <= 0)
            throw new InvalidOperationException("Choice step audit did not exercise execution continuation.");
        var legacy = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy, deadline.Token,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
            maximumPotionUses: 2, minimumPotionUses: 2)
            { DisableExecutionChoiceContinuationsForTesting = true }.Solve());
        AssertEquivalentSearchResults(legacy, result, "crab step audit complete search and budget");
        if (result.ExpandedNodes != profile.MaxExpandedNodes)
            throw new InvalidOperationException($"Choice step audit stopped before its required node boundary: {result.ExpandedNodes}/{profile.MaxExpandedNodes}.");
        _completedChecks.Add($"ChoiceContinuationStepAudit:complete-state:pending-spec:history:expanded={result.ExpandedNodes}:execution={result.ExecutionChoiceReuses}:cards={result.CardChoicePrefixReuses}");
    }

    private async Task AssertExecutionChoiceSolveAsync(CombatState combat, Player player, bool strictOnly, bool setup, bool budgetBoundary = false)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "HAVOC", "ACROBATICS", "PREPARED", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "PREPARED", "STRIKE_SILENT", "DEFEND_IRONCLAD", "STRIKE_DEFECT", "DEFEND_DEFECT" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Draw" });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_SILENT", "PREPARED", "STRIKE_REGENT", "DEFEND_REGENT" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Discard" });
        foreach (string id in setup && !budgetBoundary ? new[] { "FOREGONE_CONCLUSION_POWER" }
                     : new[] { "STRATAGEM_POWER", "TOOLS_OF_THE_TRADE_POWER", "TYRANNY_POWER" })
            await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = 1, Target = "Player" });
        if (setup)
        {
            if (budgetBoundary)
                foreach (string id in new[] { "FOREGONE_CONCLUSION_POWER", "ENTROPY_POWER" })
                    await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = 1, Target = "Player" });
            foreach (string id in budgetBoundary ? new[] { "TOOLBOX", "CHOICES_PARADOX", "GAMBLING_CHIP", "TOASTY_MITTENS" }
                         : new[] { "TOOLBOX", "TOASTY_MITTENS" })
                await InjectRelicAsync(player, new() { RelicId = id });
        }
        SetEnergy(player, 3);
        string before = ContinuationStamp.CaptureLive(combat).StateText;
        var root = CombatRootSnapshot.Capture(combat);
        if (setup)
        {
            // This fixture starts in Play. Move only its isolated root to the setup
            // boundary; the real combat and its UI remain under the native executor.
            typeof(CombatRootSnapshot).GetField("<PlayerPhase>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(root, PlayerTurnPhase.Start);
            var isolated = (CombatPredictionSimulator)typeof(CombatRootSnapshot)
                .GetField("_rootSimulator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
            isolated.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.Start;
        }
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var captured = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: setup,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        { FixedBudget = true, VerifyIncrementalSearch = false, MaxDegreeOfParallelism = 1, DetailedDiagnostics = false,
            MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null };
        var profile = captured.Profile with { BeamWidth = 12, MaxExpandedNodes = 80 };
        if (budgetBoundary)
        {
            await Task.Run(() => new CombatBeamSolver(root, names, damage, captured, CancellationToken.None,
                searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).VerifyExecutionSetupBudgetBoundaryForTesting());
            if (ContinuationStamp.CaptureLive(combat).StateText != before)
                throw new InvalidOperationException("Setup budget fixture changed live combat.");
            _completedChecks.Add("ExecutionChoiceSetup:deep-source-chain:unchanged-finite-budget-boundary");
            return;
        }
        Task<SolverResult> Solve(SearchPolicySnapshot policy, CancellationToken cancellation, bool disabled = false)
            => Task.Run(() => new CombatBeamSolver(root, names, damage, policy, cancellation,
                searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled)
                { DisableExecutionChoiceContinuationsForTesting = disabled }.Solve());
        if (strictOnly)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            var strict = await Solve(captured with { VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2 }, deadline.Token);
            if (strict.ExecutionChoiceCaptures <= 0 || strict.ExecutionChoiceReuses <= 0)
                throw new InvalidOperationException("Strict search missed execution continuation reuse.");
        }
        else
        {
            SolverResult? parallel = null;
            foreach (int mode in setup ? new[] { 0 } : new[] { 1, 2, 0 })
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
                using CountdownEvent both = new(2);
                object gate = new();
                StateFingerprint? selectedParent = null;
                int claimed = 0, active = 0, overlaps = 0;
                var injected = new InvalidOperationException("Execution continuation replay failure probe");
                SearchRequestWorkTotals totals = new();
                SearchPathObserver observer = new(_ => true, observation =>
                {
                    if (setup || observation.Stage != SearchPathObservationStage.ExecutionChoiceContinuationReplay) return;
                    int ordinal;
                    lock (gate)
                    {
                        selectedParent ??= observation.StateKey;
                        if (selectedParent != observation.StateKey || claimed >= 2) return;
                        ordinal = ++claimed;
                    }
                    Interlocked.Increment(ref active);
                    try
                    {
                        both.Signal();
                        if (!both.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Execution continuation siblings did not overlap.");
                        if (ordinal != 2) return;
                        Interlocked.Increment(ref overlaps);
                        if (mode == 1) deadline.Cancel();
                        if (mode == 2) throw injected;
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
                var policy = captured with { MaxDegreeOfParallelism = 2, RequestWorkTotals = totals,
                    Diagnostics = new SearchDiagnosticsSink(captured.Diagnostics.Info, captured.Diagnostics.Debug, observer) };
                try
                {
                    parallel = await Solve(policy, deadline.Token);
                    if (mode != 0) throw new InvalidOperationException("Execution continuation did not propagate the injected cancellation/failure.");
                }
                catch (OperationCanceledException error) when (mode == 1 && error.CancellationToken == deadline.Token) { }
                catch (InvalidOperationException error) when (mode == 2 && ReferenceEquals(error, injected)) { }
                var work = totals.Snapshot();
                if ((!setup && overlaps != 1) || active != 0 || totals.RecordedSolverCountForTesting != 1
                    || work.TransitionCount <= 0 || work.WorkerAllocatedBytes <= 0)
                    throw new InvalidOperationException("Execution continuation did not drain its parallel work.");
            }
            var serial = await Solve(captured, CancellationToken.None);
            var legacy = await Solve(captured, CancellationToken.None, disabled: true);
            AssertEquivalentSearchResults(legacy, serial, "execution continuation legacy/serial");
            AssertEquivalentSearchResults(serial, parallel!, "execution continuation DOP1/DOP2");
            if (parallel!.ExecutionChoiceCaptures <= 0 || parallel.ExecutionChoiceReuses <= 0 || legacy.ExecutionChoiceCaptures != 0)
                throw new InvalidOperationException("Search did not exercise execution continuation capture/reuse and the disabled reference.");
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != before)
            throw new InvalidOperationException("Execution continuation solve changed live combat.");
        _completedChecks.Add($"ExecutionChoiceSolve:setup={setup}:strict={strictOnly}:complete-routes:budgets:live-isolation");
    }
}
