using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertEndTurnChoiceReplayAsync(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            FixedBudget = true, VerifyIncrementalSearch = false, DetailedDiagnostics = false,
            MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        // Change only the isolated root: the next player turn must discard from its drawn hand.
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)typeof(CombatRootSnapshot)
            .GetField("_rootSimulator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        SimulatedCombatState simulated = (SimulatedCombatState)simulator.State.CombatState;
        simulated.Apply<ToolsOfTheTradePower>(combat.Players[0].Creature, 1, combat.Players[0].Creature);
        _ = simulated.DrainPowerAmountChanges();
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSearchProfile profile = capturedPolicy.Profile with { BeamWidth = 24, MaxExpandedNodes = 200 };
        SolverResult? parallelResult = null;
        foreach (int mode in new[] { 1, 2, 0 })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            using CountdownEvent bothReplays = new(2);
            object observationsGate = new();
            StateFingerprint? selectedParent = null;
            bool parentSelected = false;
            int claimed = 0, active = 0, overlaps = 0;
            InvalidOperationException injected = new("EndTurn choice replay failure probe");
            SearchRequestWorkTotals totals = new();
            SearchPathObserver observer = new(_ => true, observation =>
            {
                if (observation.Stage != SearchPathObservationStage.EndTurnChoiceReplay)
                    return;
                int ordinal;
                lock (observationsGate)
                {
                    if (!parentSelected)
                    {
                        selectedParent = observation.StateKey;
                        parentSelected = true;
                    }
                    if (observation.StateKey != selectedParent || claimed >= 2) return;
                    ordinal = ++claimed;
                }
                Interlocked.Increment(ref active);
                try
                {
                    bothReplays.Signal();
                    if (!bothReplays.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("同父EndTurn选择回放未在两个lane上重叠。");
                    if (ordinal != 2) return;
                    Interlocked.Increment(ref overlaps);
                    if (mode == 1) cancellation.Cancel();
                    if (mode == 2) throw injected;
                }
                finally { Interlocked.Decrement(ref active); }
            });
            SearchPolicySnapshot policy = capturedPolicy with
            {
                MaxDegreeOfParallelism = 2, RequestWorkTotals = totals,
                Diagnostics = new SearchDiagnosticsSink(capturedPolicy.Diagnostics.Info, capturedPolicy.Diagnostics.Debug, observer),
            };
            try
            {
                SolverResult result = await Task.Run(() => new CombatBeamSolver(root, names, damage,
                    policy, cancellation.Token, searchProfile: profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                if (mode != 0) throw new InvalidOperationException($"EndTurn选择回放的在途失败未传播：mode={mode} claimed={claimed} "
                    + SolverDiagnostics.DescribeResult(result));
                parallelResult = result;
            }
            catch (OperationCanceledException error) when (mode == 1 && error.CancellationToken == cancellation.Token) { }
            catch (InvalidOperationException error) when (mode == 2 && ReferenceEquals(error, injected)) { }
            SearchRequestWorkSnapshot work = totals.Snapshot();
            if (overlaps != 1 || active != 0 || totals.RecordedSolverCountForTesting != 1
                || work.TransitionCount <= 0 || work.WorkerAllocatedBytes <= 0)
                throw new InvalidOperationException("EndTurn选择回放没有证明并发、完整排空及部分工作记录。");
        }
        SolverResult serial = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            capturedPolicy with { MaxDegreeOfParallelism = 1 }, CancellationToken.None,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        AssertEquivalentSearchResults(serial, parallelResult!, "EndTurn choice replay DOP1/DOP2");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("EndTurn选择回放合同改变了live战斗。");
    }
}
