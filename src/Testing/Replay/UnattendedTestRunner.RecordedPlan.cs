using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private int? _recordedPlanSearchesBeforeDeployment;
    private SolverResult? _recordedPlanExpectedResult;

    // A saved prediction is a feasibility witness, never evidence of search discovery.
    // Keep every recorded state key and choice; incompatible legacy identities fail.
    private async Task PrepareRecordedPlanDeploymentAsync(CombatState combat, bool deploy = true)
    {
        if (_checkpointImport == null || !HasNativeRecording
            || _request.ReplayMode != (deploy ? "DeploySolver" : "SearchOnly"))
            throw new InvalidDataException("Recorded plan deployment requires a native checkpoint in DeploySolver mode.");
        int cursor = _checkpointImport["checkpoint"]!["eventCursor"]!.GetValue<int>();
        int turn = CombatRootSnapshot.Capture(combat).StartTurnNumber;
        JsonObject recorded = _request.RecordedPlanOverridePathForTest is { } overridePath
            ? LoadRecordedPlanOverride(overridePath, cursor, turn)
            : _checkpointImport["index"]!["searchResults"]!.AsArray()
                .OfType<JsonObject>().LastOrDefault(item =>
                    item["eventCursor"]?.GetValue<int>() == cursor
                    && item["startTurnNumber"]?.GetValue<int>() == turn
                    && item["combatEndedTurn"] != null && item["deathTurn"] == null)
                ?? throw new InvalidDataException("No complete winning prediction at the selected native event cursor.");
        PlanAction[] actions = recorded["plannedActions"]!.Deserialize<PlanAction[]>(UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidDataException("Missing recorded actions.");
        if (actions.Length == 0)
            throw new InvalidDataException("Recorded winning prediction has no actions.");
        _writer.ReplayVerification!["recordedPrediction"] = recorded.DeepClone();
        _writer.ReplayVerification["comparisonScope"] = "recorded_prediction_feasibility";
        _writer.WriteGeneratedArtifact("recorded-plan.json", recorded);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null);
        CombatBugReportExporter.RecordSearchPolicy(combat, policy);
        var player = combat.Players.Single();
        var enemy = combat.Enemies.First();
        List<KnownRoutePrefix> prefixes = [];
        CombatBeamSolver replay = new(root, names, damage, policy);
        SimulationSnapshot? previous = null;
        try
        {
            for (int index = 0; index < actions.Length; index++)
            {
                EnsureWithinDeadline();
                SetStage($"recorded_plan_action_{index + 1}_{actions[index].Kind}_T{actions[index].Turn}");
                SimulationSnapshot next = await Task.Run(() =>
                {
                    SimulationSnapshot incremental = (SimulationSnapshot)InvokeForcedTerminalMethod(replay, "Replay",
                        [new[] { actions[index] }, previous, actions[index].Turn, index,
                            null, null, null, null, null, null, null, null, true, true, null])!;
                    try
                    {
                        SimulationSnapshot full = InvokeForcedTerminalReplay(replay,
                            actions.Take(index + 1).ToArray(), null, 0, null);
                        try
                        {
                            InvokeForcedTerminalMethod(replay, "AssertIncrementalEquivalent",
                                [actions[index], actions.Take(index + 1).ToArray(), incremental, full]);
                        }
                        finally { full.ReleaseSimulator(); }
                        return incremental;
                    }
                    catch { incremental.ReleaseSimulator(); throw; }
                });
                prefixes.Add(FreezeKnownRoutePrefix(actions[index], CaptureSimulated(next.Simulator,
                    (SimulatedCombatState)next.Simulator.State.CombatState, player, enemy), next));
                previous?.ReleaseSimulator();
                previous = next;
            }
        }
        finally { previous?.ReleaseSimulator(); }
        SolverResult result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile, fixedPrefixActions: actions).Solve());
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Recorded prediction replay changed the live root.");
        if (!result.Snapshot.AllEnemiesDead || result.DeathTurn != null
            || result.CombatEndedTurn != recorded["combatEndedTurn"]!.GetValue<int>()
            || result.ProjectedBattleHpLost != recorded["projectedBattleHpLost"]!.GetValue<int>()
            || result.BestNode.Actions.Count != actions.Length || result.ExpandedNodes != 0)
            throw new InvalidOperationException("Strict recorded prediction differs from its saved winning outcome: "
                + $"actual won={result.Snapshot.AllEnemiesDead}, deathTurn={result.DeathTurn}, "
                + $"turn={result.CombatEndedTurn}, hpLost={result.ProjectedBattleHpLost}, "
                + $"actions={result.BestNode.Actions.Count}, expanded={result.ExpandedNodes}; "
                + $"expected turn={recorded["combatEndedTurn"]}, "
                + $"hpLost={recorded["projectedBattleHpLost"]}, actions={actions.Length}.");
        _completedChecks.Add($"RecordedPrediction:StrictReplay:IncrementalEquivalent:Actions={actions.Length}:Expanded=0");
        if (!deploy)
        {
            await RunKnownRoutePathTraceAsync(combat, player, prefixes, "RecordedPrediction",
                "recorded_plan_search_path",
                observedRetentionStep: _request.RecordedPlanRetentionStepForTest
                    ?? (actions.Length > 1 ? actions.Length - 1 : null),
                frozenSearchContext: new(root, names, damage, policy));
            return;
        }
        _writer.CaptureSolverResult(result);
        _recordedPlanExpectedResult = result;
        _recordedPlanSearchesBeforeDeployment = SolverController.SearchGenerationForTesting;
        SolverController.AcceptShowcaseRoute(_host, combat, result);
    }

    // Diagnostic-only route target, e.g. a recorded player prefix joined to a later saved prediction.
    // It must carry the same winning shape as an index entry and be anchored to the selected root.
    private static JsonObject LoadRecordedPlanOverride(string path, int cursor, int turn)
    {
        JsonObject recorded = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"Recorded plan override is not a JSON object: {path}");
        if (recorded["eventCursor"]?.GetValue<int>() != cursor
            || recorded["startTurnNumber"]?.GetValue<int>() != turn)
            throw new InvalidDataException(
                $"Recorded plan override is anchored to cursor {recorded["eventCursor"]} turn {recorded["startTurnNumber"]}, "
                + $"but the selected root is cursor {cursor} turn {turn}.");
        if (recorded["combatEndedTurn"] == null || recorded["deathTurn"] != null
            || recorded["projectedBattleHpLost"] == null || recorded["plannedActions"] is not JsonArray)
            throw new InvalidDataException("Recorded plan override must be a complete winning prediction with plannedActions.");
        return recorded;
    }

    private void AssertRecordedPlanDeployment(CombatState combat)
    {
        if (_recordedPlanSearchesBeforeDeployment is not { } before)
            throw new InvalidOperationException("Recorded prediction was not prepared before deployment.");
        int searches = SolverController.SearchGenerationForTesting - before;
        if (searches != 0 || SolverController.UnexpectedReplanCountForTesting != 0)
            throw new InvalidOperationException("Recorded prediction deployment required an unplanned search or replan.");
        SolverResult expected = _recordedPlanExpectedResult
            ?? throw new InvalidOperationException("Missing recorded prediction outcome.");
        // The solver's mutable ledger is reset at combat end. Use the independent
        // forensic terminal observation, which retains the battle and restored prefix.
        CombatReplayOutcomeSnapshot actual = CombatBugReportExporter.CaptureOutcome(combat);
        _writer.WriteGeneratedArtifact("recorded-plan-native-outcome.json", new
        {
            actual, expected.ProjectedBattleHpLost, expected.ProjectedBattlePotionCount,
            expected.CombatEndedTurn,
        });
        if (!actual.CombatEnded || !actual.Survived || actual.FinalEnemyHp != 0
            || actual.UnattributedHpLoss != 0 || actual.HpLost != expected.ProjectedBattleHpLost
            || actual.Potions.Length != expected.ProjectedBattlePotionCount
            || !actual.Potions.Select(potion => potion.PotionId).SequenceEqual(
                expected.BattlePotionIdsUsedSoFar.Concat(expected.PlannedPotionIds))
            || combat.Players.Single().PlayerCombatState!.TurnNumber != expected.CombatEndedTurn)
            throw new InvalidOperationException("Native recorded prediction outcome differs in HP loss, potion count or terminal turn.");
        _completedChecks.Add("RecordedPrediction:NativeDeployment:LocalSearches=0:UnexpectedReplans=0");
    }

}
