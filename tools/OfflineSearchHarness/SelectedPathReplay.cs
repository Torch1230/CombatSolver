using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

// Post-search diagnostics only. SelectedSearchPlan intentionally owns no node
// ancestry; replay bounded prefixes instead of extending search object lifetimes.
internal static class SelectedPathReplay
{
    internal static void Write(CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverResult result, string directory)
    {
        var actions = result.BestNode.Actions;
        if (result.TurnSetupChoices.Count != 0 || actions.Count > SearchWitnessPrefix.MaximumActions)
            throw new InvalidOperationException("Selected-state replay requires a root without preparation choices and at most 96 actions.");
        var diagnostic = policy with
        {
            UseObjectiveSearch = false, ObjectiveValueModel = null, OutcomeTrainingCollector = null,
            SharedEvidence = null, RequestWorkTotals = new(), Diagnostics = new(_ => { }, _ => { }),
            MeasurePhasePerformance = false,
        };
        using var isolation = SimulationNotificationIsolation.Enter();
        var driver = new CombatBeamSolver(root, names, damage, diagnostic);
        var prefixes = JsonSerializer.Deserialize<string[]>(File.ReadAllText(
            Path.Combine(directory, "ordering-selected-prefixes.json")))
            ?? throw new InvalidDataException("Missing selected prefix identities.");
        if (prefixes.Length != actions.Count + 1)
            throw new InvalidDataException("Selected prefix count differs from the route.");
        List<object> states = [];
        string? rootBefore = null;
        for (int count = 0; count <= actions.Count; count++)
        {
            var snapshot = driver.ReplayDiagnosticPrefix(actions.Take(count).ToArray());
            try
            {
                if (snapshot.HasRisk || snapshot.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("Selected-state replay reached risk or an unresolved boundary.");
                if (count == 0) rootBefore = driver.CaptureDiagnosticContinuation(snapshot).StateText;
                states.Add(new { prefix = prefixes[count], actionCount = count,
                    snapshot.Turn, snapshot.StateKey, snapshot.BoundaryReason,
                    hasPredictionRisk = snapshot.HasRisk });
            }
            finally { snapshot.ReleaseSimulator(); }
        }
        var after = driver.ReplayDiagnosticPrefix([]);
        try
        {
            if (driver.CaptureDiagnosticContinuation(after).StateText != rootBefore)
                throw new InvalidOperationException("Selected-state replay changed the frozen root.");
        }
        finally { after.ReleaseSimulator(); }
        File.WriteAllText(Path.Combine(directory, "ordering-selected-states.json"),
            JsonSerializer.Serialize(new { source = "post-search-prefix-replay", rootUnchanged = true, states },
                UnattendedTestFiles.JsonOptions));
    }
}
