using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactSearchBackendAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var captured = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var original = CaptureActual(combat, player, combat.Enemies.Single());
        CompactCombatRoot compact;
        var preparation = Stopwatch.StartNew();
        using (SimulationNotificationIsolation.Enter()) compact = new(captured.ForkSimulator(), player);
        preparation.Stop();
        var baseline = await Task.Run(() => CombatSearchCoordinator.Solve(captured, display, damage, policy, default, null));
        Write("compact-search-baseline.json", Summary(baseline));
        var changed = await Task.Run(() => CombatSearchCoordinator.Solve(captured, display, damage,
            policy with { CompactRoot = compact }, default, null));
        Write("compact-search-candidate.json", Summary(changed));
        object Logical(SolverResult result) => new
        {
            result.BestNode.Actions, result.BestNode.Score, result.Snapshot, result.CombatEndedTurn,
            result.ProjectedBattleHpLost, result.ExpandedNodes, result.TransitionCount, result.ForkCount,
            result.ReplayCount, result.ReusedNodeSnapshots, result.ChoiceBranchesEvaluated,
            result.ChoiceReplayAttempts, result.ChoiceReplayBudgetExhaustions, result.ChoiceBranchesDroppedByBudget,
            result.DominatedActionsPruned, result.TopQueueActionsDropped, result.ActionAdmissionRepresentativesProtected,
            result.DuplicateCardBranchesPruned, result.ShuffleBranchesPruned, result.SoldHpBranchesPruned,
            result.HpInvestmentBranchesProtected, result.TranspositionBranchesPruned,
            result.RepeatableNoProgressBranchesPruned, result.StandPatProbes,
            result.OrderedMutationCandidatesAdmitted, result.CrossTurnCandidatesProtected,
            result.CrossTurnContinuationsStopped, result.CycleShapesDetected, result.CycleRegionsDetected,
            result.PrimaryIncumbentBranchesPruned, result.PrimaryIncumbentUpdates
        };
        if (JsonSerializer.Serialize(Logical(baseline)) != JsonSerializer.Serialize(Logical(changed)))
        {
            Write("compact-search-logical-baseline.json", Logical(baseline));
            Write("compact-search-logical-candidate.json", Logical(changed));
            throw new InvalidOperationException("Compact backend changed the complete route, evaluation or logical search work; see logical evidence.");
        }
        var counts = compact.Counts;
        if (counts.CompletedReplays <= 0 || counts.Materializations >= counts.CompletedReplays)
            throw new InvalidOperationException("Compact search did not avoid completed-candidate materialization.");
        AssertSnapshotEqual(original, CaptureActual(combat, player, combat.Enemies.Single()), "CompactSearchBackend", "ActualUnchanged");
        Write("compact-search-backend.json", new
        {
            equivalent = true, preparationMilliseconds = preparation.Elapsed.TotalMilliseconds,
            completedReplays = counts.CompletedReplays, pendingReplays = counts.PendingReplays,
            materializations = counts.Materializations, baseline = Summary(baseline), candidate = Summary(changed)
        });
        _completedChecks.Add($"CompactSearchBackend:WholeSearchLogicalEquivalent:{counts.CompletedReplays}Completed:{counts.PendingReplays}Pending:{counts.Materializations}Materializations:ActualUnchanged");

        static object Summary(SolverResult result) => new
        {
            result.ExpandedNodes, result.TransitionCount, result.ChoiceBranchesEvaluated,
            result.CombatEndedTurn, result.ProjectedBattleHpLost, result.Elapsed,
            actions = result.BestNode.Actions.Count, result.MaxParallelExpansionConcurrency
        };
        void Write(string file, object value)
        {
            if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory)) return;
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, file),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
