using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static class WitnessPrefixChecks
{
    internal static void Run(CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SearchOutcomeValueModel.CorrectionQuery[] queries, string output)
    {
        var crossTurn = queries.FirstOrDefault(q => q.Replay.Steps[^1].Turn > root.StartTurnNumber)
            ?? throw new InvalidOperationException("Prefix contract needs an observed cross-turn query.");
        int checks = 0;
        using var isolation = SimulationNotificationIsolation.Enter();
        var teacher = policy with { UseObjectiveSearch = false, ObjectiveValueModel = null };
        var driver = new CombatBeamSolver(root, names, damage, teacher);
        string RootStamp()
        {
            var snapshot = driver.ReplayDiagnosticPrefix([]);
            try { return driver.CaptureDiagnosticContinuation(snapshot).StateText; }
            finally { snapshot.ReleaseSimulator(); }
        }
        string before = RootStamp();
        Check(queries.Length is > 0 and <= 6 && queries.Length % 2 == 0,
            "corrective continuations are complete bounded retained/dropped pairs");
        for (int pair = 0; pair < queries.Length; pair += 2)
        {
            var left = queries[pair]; var right = queries[pair + 1];
            int turn = left.Replay.Steps[^1].Turn;
            Check(turn == right.Replay.Steps[^1].Turn && left.Prefix.Length == right.Prefix.Length
                && left.Prefix.Count(a => a.Turn == turn) == right.Prefix.Count(a => a.Turn == turn),
                "each sampled competition has the same global and within-turn action depth");
        }
        foreach (var query in queries)
        {
            var whole = driver.ReplayDiagnosticPrefix(query.Prefix);
            try
            {
                var incremental = driver.ReplayWitnessPrefixForTesting(query.Replay);
                try
                {
                    Check(whole.StateKey == incremental.StateKey && whole.StateKey == query.State,
                        "observed, full and incremental prefix identities agree");
                    Check(driver.CaptureDiagnosticContinuation(whole).StateText
                        == driver.CaptureDiagnosticContinuation(incremental).StateText,
                        "full continuation agrees across replay/Fork, including history and RNG");
                    Check(SearchWitnessPrefix.HpCost(whole) == query.HpCost
                        && whole.PotionStrategicCost == query.PotionCost, "cumulative policy labels agree");
                }
                finally { incremental.ReleaseSimulator(); }
            }
            finally { whole.ReleaseSimulator(); }
        }
        var badState = crossTurn.Replay.Steps.ToArray();
        badState[^1] = badState[^1] with { State = new(badState[^1].State.First ^ 1, badState[^1].State.Second) };
        Reject(new(badState), "Continuation identity mismatch");
        var badCost = crossTurn.Replay.Steps.ToArray();
        badCost[^1] = badCost[^1] with { HpCost = badCost[^1].HpCost + 1 };
        Reject(new(badCost), "Continuation identity mismatch");
        var badTurn = crossTurn.Replay.Steps.ToArray();
        badTurn[0] = badTurn[0] with { Action = badTurn[0].Action with { Turn = badTurn[0].Action.Turn + 1 } };
        Reject(new(badTurn), "Observed continuation action cannot be replayed");
        Reject(new([]), "Invalid continuation prefix length");
        bool oldRejected = false;
        try { driver.CanReplayOpeningPrefix([crossTurn.Prefix[0] with { EndsPlayerTurn = true }]); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("固定搜索前缀动作无效", StringComparison.Ordinal))
        { oldRejected = true; }
        Check(oldRejected, "ordinary fixed prefix retains its explicit turn-end restriction");
        Check(RootStamp() == before, "successful and rejected replays leave the frozen root intact");
        File.WriteAllText(Path.Combine(output, "outcome-prefix-checks.json"), JsonSerializer.Serialize(new
        {
            passed = checks, queries = queries.Length, turns = queries.Select(q => q.Replay.Steps[^1].Turn).ToArray(),
            withinTurnDepths = queries.Select(q => q.Prefix.Count(a => a.Turn == q.Replay.Steps[^1].Turn)).ToArray(),
            maximumActions = queries.Max(q => q.Prefix.Length), rootUnchanged = true,
        }));

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Witness prefix: " + name);
            checks++;
        }
        void Reject(SearchWitnessPrefix prefix, string message)
        {
            try
            {
                var snapshot = driver.ReplayWitnessPrefixForTesting(prefix);
                snapshot.ReleaseSimulator();
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith(message, StringComparison.Ordinal))
            { checks++; return; }
            throw new InvalidOperationException("Invalid witness prefix was accepted.");
        }
    }
}
