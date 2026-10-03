using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task VerifyB015AdjustedRouteAsync(B015OriginalPrefixContext context,
        SearchNode original, CombatState combat, Player player, string evidence)
    {
        JsonArray trace = [];
        List<Exception> failures = [];
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        try
        {
            MoveStateSnapshot native = CaptureActual(combat, player, context.Enemy);
            SearchNode unchanged = context.Driver.ReplayB015AdjustedRouteForTesting(original, context.Actions, false)
                ?? throw new InvalidOperationException("B015 unchanged generated route was rejected.");
            try
            {
                MoveStateSnapshot actual = CaptureSimulated(unchanged.Snapshot.Simulator,
                    (SimulatedCombatState)unchanged.Snapshot.Simulator.State.CombatState, player, context.Enemy);
                AssertSnapshotEqual(native, actual, "B015T016Afterimage", "UnchangedRouteNativeStateAndRng");
                trace.Add(new JsonObject { ["stage"] = "unchanged_route_passed", ["state"] = JsonSerializer.SerializeToNode(actual, UnattendedTestFiles.JsonOptions) });
            }
            finally { unchanged.Snapshot.ReleaseSimulator(); }
            _completedChecks.Add("B015T016Afterimage:UnchangedAdjustedRoute:NativeFullStateAndRng");

            SearchNode safe = context.Driver.ReplayB015AdjustedRouteForTesting(original, context.Actions[..2], true)
                ?? throw new InvalidOperationException("B015 valid short Afterimage reordering was rejected.");
            try
            {
                if (!safe.Actions.Select(action => action.CardId).SequenceEqual(new[] { "AFTERIMAGE", "STRANGLE" }))
                    throw new InvalidOperationException("B015 short control did not perform actual Afterimage frontloading.");
                trace.Add(new JsonObject { ["stage"] = "safe_frontloading_passed", ["state"] = JsonSerializer.SerializeToNode(
                    CaptureSimulated(safe.Snapshot.Simulator, (SimulatedCombatState)safe.Snapshot.Simulator.State.CombatState,
                        player, context.Enemy), UnattendedTestFiles.JsonOptions) });
            }
            finally { safe.Snapshot.ReleaseSimulator(); }
            _completedChecks.Add("B015T016Afterimage:ValidShortReorderingPreserved");

            trace.Add(new JsonObject { ["stage"] = "before_production_frontloading", ["actions"] = JsonSerializer.SerializeToNode(context.Actions, UnattendedTestFiles.JsonOptions) });
            SearchNode? reordered = context.Driver.ReplayB015AdjustedRouteForTesting(original, context.Actions, true);
            if (reordered != null)
            {
                reordered.Snapshot.ReleaseSimulator();
                throw new InvalidOperationException("B015 unsafe frontloading must reject the adjusted route, retaining the original.");
            }
            _completedChecks.Add("B015T016Afterimage:InvalidReorderedTargetRejectedWithoutException");
            trace.Add(new JsonObject { ["stage"] = "unsafe_frontloading_rejected" });

            PlanAction[] missingTarget = context.Actions.ToArray();
            missingTarget[^1] = missingTarget[^1] with { TargetCombatId = uint.MaxValue };
            SearchNode? invalidSharedPath = context.Driver.ReplayB015AdjustedRouteForTesting(original, missingTarget, false);
            if (invalidSharedPath != null)
            {
                invalidSharedPath.Snapshot.ReleaseSimulator();
                throw new InvalidOperationException("B015 shared adjusted-route path accepted an absent target.");
            }
            AssertSnapshotEqual(native, CaptureActual(combat, player, context.Enemy), "B015T016Afterimage", "NativeUnchangedAfterPostprocessing");
            AssertSnapshotEqual(native, CaptureSimulated(original.Snapshot.Simulator,
                (SimulatedCombatState)original.Snapshot.Simulator.State.CombatState, player, context.Enemy),
                "B015T016Afterimage", "OriginalRouteRetained");
            _completedChecks.Add("B015T016Afterimage:SharedInvalidTargetRejected:OriginalAndNativeUnchanged");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await File.WriteAllTextAsync(Path.Combine(evidence, "t016-adjusted-route-probe.json"), trace.ToJsonString(UnattendedTestFiles.JsonOptions)); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("B015 adjusted-route diagnostic and evidence failed.", failures);
    }
}

internal sealed partial class CombatBeamSolver
{
    internal void VerifyB015FixedPrefixTargetsForTesting()
    {
        foreach (bool invalidTarget in new[] { false, true })
        {
            SimulationSnapshot snapshot = Replay([]);
            SearchNode seed = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
                snapshot.Turn, SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
                snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot,
                CombatProgressState.Capture(snapshot));
            SearchNode? result = null;
            try
            {
                PlanAction action = PrepareCardActions(seed, true)
                    .First(item => item.Action.CardId == "STRIKE_IRONCLAD").Action;
                if (invalidTarget) action = action with { TargetCombatId = uint.MaxValue };
                result = ApplyFixedPrefix(seed, [action]);
                if (invalidTarget ? result != null : result == null || !result.Snapshot.AllEnemiesDead)
                    throw new InvalidOperationException("B015 fixed-prefix target acceptance differed from the expected branch.");
            }
            finally
            {
                result?.Snapshot.ReleaseSimulator();
                snapshot.ReleaseSimulator();
            }
        }
    }

    internal SearchNode? ReplayB015AdjustedRouteForTesting(SearchNode original, PlanAction[] actions, bool frontload)
        => ReplayAdjustedRoute(actions, original.GetTurnSetupChoices(), original.GetTurnSetupPlayState(),
            BuildRouteAnnotations(original), frontloadAfterimages: frontload);
}
