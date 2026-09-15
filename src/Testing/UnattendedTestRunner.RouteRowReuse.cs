using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRouteRowReuseAndMeasureAsync()
    {
        await AssertRouteRowReuseAsync();
        List<object> measurements = [];
        SolverOverlayTurnSnapshot turn = ProbeRoute();
        SolverOverlayTurnSnapshot[] updates = Enumerable.Range(0, 64)
            .Select(_ => CopyProbeRoute(turn)).ToArray();
        SolverRouteRow row = new(0);
        _host.AddChild(row);
        try
        {
            row.Populate(turn);
            for (int sample = 0; sample < 2; sample++)
            {
                await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int index = 0; index < updates.Length; index++) row.Populate(updates[index]);
                measurements.Add(new { kind = "unchanged-route", sample, iterations = 64,
                    milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
            }
        }
        finally { row.Free(); }
        _writer.WriteGeneratedArtifact("route-row-reuse.json", measurements);
        _completedChecks.Add("RouteRowReuse:MeasuredUnchangedRow");
    }

    private static SolverOverlayTurnSnapshot ProbeRoute()
        => new(1, ["Choice"], Enumerable.Range(0, 16)
            .Select(index => new SolverOverlayActionSnapshot(
                $"Action {index}", "Enemy", null, [], [], $"Action {index}",
                SolverOverlayActionVisualKind.Attack, 0)).ToArray(),
            null, 16, 0, 0, 0, false);

    private static SolverOverlayTurnSnapshot CopyProbeRoute(SolverOverlayTurnSnapshot turn)
        => turn with { TurnStartChoices = turn.TurnStartChoices.ToArray(),
            Actions = turn.Actions.Select(action => action with {
                RelicLabels = action.RelicLabels.ToArray(), Kills = action.Kills.ToArray(),
                TextIdentity = action.TextIdentity is not { } identity ? null : identity with {
                    Choices = identity.Choices.Select(choice => (IReadOnlyList<SolverCardTextIdentity>)
                        choice.Select(card => card with { }).ToArray()).ToArray(),
                    Relics = identity.Relics.Select(relic => relic with { }).ToArray() }
            }).ToArray() };

    private async Task AssertRouteRowReuseAsync()
    {
        int subscriptions = SolverLocaleRefresh.SubscriptionCountForTesting;
        string language = LocManager.Instance.Language;
        SolverRouteRow row = new(0);
        _host.AddChild(row);
        try
        {
            SolverActionTextIdentity identity = new("STRIKE_IRONCLAD", 0, "", false, false,
                [[new("STRIKE_IRONCLAD", 0, "Strike")]], [new("TEST_RELIC", "Relic", "")]);
            SolverOverlayActionSnapshot action = ProbeRoute().Actions[0] with { TextIdentity = identity };
            SolverOverlayActionSnapshot endTurn = action with { Kills = ["Enemy"], TextIdentity = null };
            SolverOverlayTurnSnapshot turn = ProbeRoute() with { Actions = [action, action with { Title = "Second" }], EndTurnAction = endTurn };
            row.Populate(turn);
            ulong[] ids = ChildIds();
            row.SetDeploymentProgress(1, 1);
            row.SetEndTurnDeploymentState(active: true, completed: false);
            row.Populate(CopyProbeRoute(turn) with { HpLoss = 9, EnergyLeft = 2 });
            if (!ids.SequenceEqual(ChildIds()) || row.DeploymentActionCount != 2
                || row.ActionFlow.GetChildren().OfType<CanvasItem>().Any(child => child.Modulate != Colors.White))
                throw new InvalidOperationException("Equal route rebuilt controls or retained stale deployment colors.");

            SolverOverlayActionSnapshot[] changed = [action with { Title = "Changed" },
                action with { TargetName = "Other" }, action with { ChoiceText = "Other" },
                action with { Tooltip = "Other" }, action with { VisualKind = SolverOverlayActionVisualKind.Skill },
                action with { ReplayCount = 1 }, action with { RelicLabels = ["Relic"] },
                action with { Kills = ["Enemy"] }, action with { TextIdentity = null },
                action with { TextIdentity = identity with { Upgrade = 1 } },
                action with { TextIdentity = identity with { CardId = "DEFEND_IRONCLAD" } },
                action with { TextIdentity = identity with { CardEnchantmentId = "INKY" } },
                action with { TextIdentity = identity with { PotionId = "BLOCK_POTION" } },
                action with { TextIdentity = identity with { EndTurn = true } },
                action with { TextIdentity = identity with { DirectEndTurn = true } },
                action with { TextIdentity = identity with { Choices = [[]] } },
                action with { TextIdentity = identity with { Choices = [[new("STRIKE_IRONCLAD", 1, "Strike")]] } },
                action with { TextIdentity = identity with { Relics = [new("TEST_RELIC", "Other", "")] } }];
            foreach (SolverOverlayActionSnapshot replacement in changed)
            {
                row.Populate(turn);
                ulong before = ChildIds()[1];
                row.Populate(turn with { Actions = [replacement, turn.Actions[1]] });
                if (ChildIds()[1] == before)
                    throw new InvalidOperationException("Changed display or locale identity reused a stale action.");
            }
            foreach (SolverOverlayTurnSnapshot replacement in new[] {
                turn with { TurnStartChoices = ["Other"] }, turn with { Actions = [turn.Actions[1], action] },
                turn with { Actions = [action] }, turn with { EndTurnAction = endTurn with { Kills = ["Other"] } } })
            {
                row.Populate(turn);
                ulong before = ChildIds()[0];
                row.Populate(replacement);
                if (ChildIds()[0] == before) throw new InvalidOperationException("Changed route structure was ignored.");
            }
            row.ShowStatus("Waiting");
            row.Populate(turn);
            if (row.DeploymentActionCount != 2 || ChildIds().Length != 4)
                throw new InvalidOperationException("Status transition retained stale route state.");
            row.ShowStatus("Waiting");
            SolverOverlayTurnSnapshot interrupted = turn with { Actions = new InterruptedRouteActions(turn.Actions) };
            bool failed = false;
            try { row.Populate(interrupted); }
            catch (RoutePopulateProbeException) { failed = true; }
            if (!failed || row.DeploymentActionCount != 1)
                throw new InvalidOperationException("Route construction failure did not propagate at the intended boundary.");
            row.Populate(interrupted);
            if (row.DeploymentActionCount != 2 || ChildIds().Length != 4)
                throw new InvalidOperationException("Incomplete route construction was reused as a complete row.");
            SolverOverlayTurnSnapshot empty = turn with { Actions = [], EndTurnAction = null };
            row.Populate(empty);
            if (row.DeploymentActionCount != 0 || ChildIds().Length != 2)
                throw new InvalidOperationException("Empty route lost its direct end-turn action.");
            LocManager.Instance.SetLanguage(language == "eng" ? "zhs" : "eng");
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            row.Populate(empty);
            if (((Label)row.ActionFlow.GetChild(1).GetChild(0)).Text != SolverText.Get("直接结束"))
                throw new InvalidOperationException("Reused empty route kept a stale language.");
            row.Populate(turn);
            ulong retained = ChildIds()[1];
            LocManager.Instance.SetLanguage(language);
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (ChildIds()[1] != retained || ((Label)row.ActionFlow.GetChild(1).GetChild(0).GetChild(1)).Text
                != SolverActionTextIdentity.Refresh(action).Title)
                throw new InvalidOperationException("Retained action lost its locale subscription.");
            LocManager.Instance.SetLanguage(language == "eng" ? "zhs" : "eng");
            row.ShowStatus("Waiting");
            row.Populate(turn);
            LocManager.Instance.SetLanguage(language);
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (((Label)row.ActionFlow.GetChild(1).GetChild(0).GetChild(1)).Text
                != SolverActionTextIdentity.Refresh(action).Title)
                throw new InvalidOperationException("A coalesced locale round trip skipped a newly created action.");
            _completedChecks.Add("RouteRowReuse:ValueEquality:ChangedFields:Choices:Kills:Order:Empty:Status:FailedPopulateRetry:Deployment:Locale:CoalescedRoundTrip");
        }
        finally { LocManager.Instance.SetLanguage(language); row.Free(); }
        if (SolverLocaleRefresh.SubscriptionCountForTesting != subscriptions)
            throw new InvalidOperationException("Reused or replaced route controls leaked locale subscriptions.");
        ulong[] ChildIds() => row.ActionFlow.GetChildren().Select(child => child.GetInstanceId()).ToArray();
    }

    private sealed class RoutePopulateProbeException : Exception;

    private sealed class InterruptedRouteActions(IReadOnlyList<SolverOverlayActionSnapshot> actions)
        : IReadOnlyList<SolverOverlayActionSnapshot>
    {
        private bool _interrupted;
        public int Count => actions.Count;
        public SolverOverlayActionSnapshot this[int index] => actions[index];
        public IEnumerator<SolverOverlayActionSnapshot> GetEnumerator()
        {
            for (int index = 0; index < actions.Count; index++)
            {
                if (index == 1 && !_interrupted)
                {
                    _interrupted = true;
                    throw new RoutePopulateProbeException();
                }
                yield return actions[index];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
