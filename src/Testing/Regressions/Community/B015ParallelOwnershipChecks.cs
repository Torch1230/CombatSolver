using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task ObserveB015ParallelOwnershipAsync(B015OriginalPrefixContext context,
        SearchNode before, CombatState nativeCombat, Player player, string evidence)
    {
        JsonArray trace = [];
        List<Exception> failures = [];
        SimulationSnapshot? afterSnapshot = null;
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        try
        {
            EnsureWithinDeadline();
            object[] prepared = ((System.Collections.IEnumerable)InvokeForcedTerminalMethod(context.Driver,
                "PrepareCardActions", [before, true])!).Cast<object>().ToArray();
            PlanAction legalShiv = prepared.Select(item => (PlanAction)item.GetType().GetProperty("Action")!.GetValue(item)!)
                .Single(action => action.CardId == "SHIV" && action.TargetCombatId == 1);
            afterSnapshot = (SimulationSnapshot)InvokeForcedTerminalMethod(context.Driver, "Replay",
                [new[] { legalShiv }, before.Snapshot, before.Turn, before.ActionCount,
                    null, null, null, null, null, null, null, null, true, false, null])!;
            SearchNode after = (SearchNode)InvokeForcedTerminalMethod(context.Driver,
                "CreatePlannedCardChild", [before, legalShiv, afterSnapshot])!;
            if (((SimulatedCombatState)afterSnapshot.Simulator.State.CombatState).Enemies
                    .Select(enemy => enemy.CombatId).SequenceEqual(new uint?[] { 2 }) != true)
                throw new InvalidOperationException("B015 parallel probe requires the exact legitimate Stock child with active ID 2.");
            MoveStateSnapshot beforeState = CaptureSimulated(before.Snapshot.Simulator,
                (SimulatedCombatState)before.Snapshot.Simulator.State.CombatState, player, context.Enemy);
            MoveStateSnapshot afterState = CaptureSimulated(after.Snapshot.Simulator,
                (SimulatedCombatState)after.Snapshot.Simulator.State.CombatState, player, context.Enemy);
            bool producedInvalidTarget = false;
            B015ParallelOwnershipMetrics metrics = context.Driver.RunB015ParallelOwnershipForTesting(
                before, after, (stage, parent, action, actual, expected) =>
                {
                    EnsureWithinDeadline();
                    bool originalParent = ReferenceEquals(parent, before);
                    if (!originalParent && !ReferenceEquals(parent, after))
                        throw new InvalidOperationException("B015 scheduler returned an unadmitted parent reference.");
                    MoveStateSnapshot capturedParent = CaptureSimulated(parent.Snapshot.Simulator,
                        (SimulatedCombatState)parent.Snapshot.Simulator.State.CombatState, player, context.Enemy);
                    JsonObject entry = new()
                    {
                        ["stage"] = stage, ["parentActionCount"] = parent.ActionCount,
                        ["parent"] = JsonSerializer.SerializeToNode(capturedParent, UnattendedTestFiles.JsonOptions),
                        ["action"] = JsonSerializer.SerializeToNode(action, UnattendedTestFiles.JsonOptions),
                    };
                    trace.Add(entry);
                    uint expectedTarget = originalParent ? 1u : 2u;
                    if (action?.TargetCombatId is uint target && target != expectedTarget)
                    {
                        producedInvalidTarget = true;
                        _writer.ReplayVerification!["currentGeneratorProducedInvalidTarget"] = true;
                        entry["currentGeneratorProducedInvalidTarget"] = true;
                        throw new InvalidOperationException("B015 generated action targets another parent's creature.");
                    }
                    AssertSnapshotEqual(originalParent ? beforeState : afterState, capturedParent,
                        "B015T016Parallel", stage + "ParentUnchanged");
                    AssertSnapshotEqual(beforeState, CaptureActual(nativeCombat, player, context.Enemy),
                        "B015T016Parallel", stage + "NativeUnchanged");
                    if (actual != null && expected != null)
                    {
                        MoveStateSnapshot expectedState = CaptureSimulated(expected.Simulator,
                            (SimulatedCombatState)expected.Simulator.State.CombatState, player, context.Enemy);
                        MoveStateSnapshot actualState = CaptureSimulated(actual.Simulator,
                            (SimulatedCombatState)actual.Simulator.State.CombatState, player, context.Enemy);
                        entry["expectedChild"] = JsonSerializer.SerializeToNode(expectedState, UnattendedTestFiles.JsonOptions);
                        entry["actualChild"] = JsonSerializer.SerializeToNode(actualState, UnattendedTestFiles.JsonOptions);
                        AssertSnapshotEqual(expectedState, actualState, "B015T016Parallel", "JobParentActionSeedOwnership");
                    }
                });
            trace.Add(new JsonObject
            {
                ["stage"] = "bounded_parallel_probe_complete",
                ["metrics"] = JsonSerializer.SerializeToNode(metrics, UnattendedTestFiles.JsonOptions),
                ["currentGeneratorProducedInvalidTarget"] = producedInvalidTarget,
                ["scope"] = "Two original-prefix-derived parents; real scheduler, lanes and receive; one DOP2 run; current loaded dependency environment only.",
            });
            _writer.ReplayVerification!["currentGeneratorProducedInvalidTarget"] = producedInvalidTarget;
            _writer.ReplayVerification["boundedParallelOwnership"] = JsonSerializer.SerializeToNode(metrics, UnattendedTestFiles.JsonOptions);
            _completedChecks.Add("B015T016Parallel:TwoParents:ProductionSchedulerAndLanes:BoundedActions:FullStateOwnership");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "t016-parallel-ownership-probe.json"), trace.ToJsonString(UnattendedTestFiles.JsonOptions));
            }
            catch (Exception error) { failures.Add(error); }
            finally { afterSnapshot?.ReleaseSimulator(); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("B015 parallel ownership probe and evidence failed.", failures);
    }
}

internal sealed record B015ParallelOwnershipMetrics(int ConfiguredDegreeOfParallelism,
    int PrepareJobs, int ActionJobs, int MaximumActiveWorkers, int MaximumActiveActionReplayWorkers);

internal sealed partial class CombatBeamSolver
{
    // Testing controls only the admitted scope: two parents, two prepare jobs and
    // at most four already-generated no-choice attacks. Scheduling, worker dispatch,
    // seed creation, action replay, publication and Receive are production methods.
    internal B015ParallelOwnershipMetrics RunB015ParallelOwnershipForTesting(SearchNode before, SearchNode after,
        Action<string, SearchNode, PlanAction?, SimulationSnapshot?, SimulationSnapshot?> observe)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        CombatBeamSolver coordinator = new(root, displayNames, battleDamage, policy, deadline.Token,
            searchProfile: _profile, potionPolicyOverride: _potionPolicy,
            potionFreePolicyBaseline: _potionFreePolicyBaseline);
        using ParallelExpansionExecutor executor = new(coordinator, 2);
        return executor.VerifyB015Ownership([before, after], observe, deadline.Token);
    }

    private sealed partial class ParallelExpansionExecutor
    {
        private sealed class B015ObservedLaneWork(AdmittedExpansionJob job, AutoResetEvent ready)
            : IExpansionLaneWorkItem
        {
            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker) => job.Execute(owner, worker);
            public void Signal()
            {
                try { ready.Set(); }
                finally { job.Signal(); }
            }
        }

        public B015ParallelOwnershipMetrics VerifyB015Ownership(SearchNode[] nodes,
            Action<string, SearchNode, PlanAction?, SimulationSnapshot?, SimulationSnapshot?> observe,
            CancellationToken deadline)
        {
            if (nodes.Length != 2 || ReferenceEquals(nodes[0], nodes[1]))
                throw new InvalidOperationException("B015 requires exactly two distinct admitted parents.");
            ExpansionLane[] lanes = EnsureBackgroundLanes();
            using AdmittedJobWave wave = new(DegreeOfParallelism);
            using AutoResetEvent outcomeReady = new(false);
            WaitHandle[] waitHandles = [outcomeReady, deadline.WaitHandle];
            AdmittedParent[] parents = nodes.Select(node => new AdmittedParent(node)
                { Aggregate = _coordinator.RentExpansionBatch() }).ToArray();
            AdmittedJobScheduler scheduler = new(parents, DegreeOfParallelism, wave);
            Dictionary<(AdmittedParent Parent, int Index), SimulationSnapshot> expected = [];
            Dictionary<int, AdmittedExpansionJob> inFlight = [];
            int prepareCount = 0;
            int actionCount = 0;
            Exception? primaryFailure = null;
            void ObserveOwned(string stage, AdmittedParent parent, PlanAction? action,
                SimulationSnapshot? actual = null, SimulationSnapshot? baseline = null)
            {
                // Snapshot capture can materialize COW metadata; share the production
                // parent's fork gate rather than racing an in-flight worker seed.
                lock (parent.ForkGate)
                    observe(stage, parent.Node, action, actual, baseline);
            }
            try
            {
                RunPhase(SerialJobPhase.Prepare);
                foreach (AdmittedParent parent in parents)
                {
                    if (parent.Actions == null || parent.Actions.Count is < 1 or > 2
                        || !parent.Actions.Any(item => item.Action.CardId == "SHIV"))
                        throw new InvalidOperationException("B015 bounded parent lacks its production Shiv candidate.");
                    for (int index = 0; index < parent.Actions.Count; index++)
                    {
                        PreparedCardAction action = parent.Actions[index];
                        ObserveOwned("baseline_parent", parent, action.Action);
                        // All Prepare jobs have drained before baseline forks; no worker
                        // can concurrently fork a parent during this reference replay.
                        SimulationSnapshot baseline = _coordinator.Replay([action.Action], parent.Node.Snapshot,
                            parent.Node.Turn, parent.Node.ActionCount, allowExecutionCapture: false);
                        if (baseline.HasRisk || baseline.BoundaryReason != SearchBoundaryReason.None)
                        {
                            baseline.ReleaseSimulator();
                            throw new InvalidOperationException("B015 bounded reference action has an unresolved choice or risk.");
                        }
                        expected.Add((parent, index), baseline);
                    }
                }
                RunPhase(SerialJobPhase.Card);
                if (prepareCount != 2 || actionCount != expected.Count || actionCount > 4)
                    throw new InvalidOperationException("B015 bounded scheduler did not drain the exact admitted job count.");
                foreach (AdmittedParent parent in parents)
                {
                    if (parent.Aggregate!.Cards.Count != parent.Actions!.Count
                        || parent.Aggregate.Cards.Any(candidate => !ReferenceEquals(candidate.Node.Parent, parent.Node)))
                        throw new InvalidOperationException("B015 Receive assigned a child batch to the wrong parent.");
                    ObserveOwned("after_receive", parent, null);
                }
                return new(DegreeOfParallelism, prepareCount, actionCount,
                    Volatile.Read(ref _maximumActiveWorkers), Volatile.Read(ref _maximumActiveActionReplayWorkers));
            }
            catch (Exception error) { primaryFailure = error; throw; }
            finally
            {
                // Match production's sentinel/drain ordering before releasing any roots.
                wave.BackgroundCompleted.Signal();
                wave.BackgroundCompleted.Wait();
                List<Exception> cleanupFailures = [];
                while (wave.TryTake(out AdmittedJobOutcome? pending))
                {
                    try
                    {
                        using (pending)
                            _coordinator.MergeExpansionWorker(pending!.Worker, pending.AllocatedBytes);
                    }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
                foreach (AdmittedParent parent in parents)
                {
                    try { parent.Dispose(); }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
                foreach (SimulationSnapshot snapshot in expected.Values)
                {
                    try { snapshot.ReleaseSimulator(); }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
                if (cleanupFailures.Count != 0)
                {
                    if (primaryFailure != null) cleanupFailures.Insert(0, primaryFailure);
                    throw new AggregateException("B015 bounded worker cleanup failed.", cleanupFailures);
                }
            }

            void RunPhase(SerialJobPhase phase)
            {
                Stack<int> idle = new(Enumerable.Range(0, lanes.Length).Reverse());
                DispatchAvailable();
                while (inFlight.Count > 0)
                {
                    AdmittedJobOutcome? published;
                    while (!wave.TryTake(out published))
                    {
                        int signaled = WaitHandle.WaitAny(waitHandles, TimeSpan.FromSeconds(15));
                        if (signaled == WaitHandle.WaitTimeout)
                            throw new TimeoutException("B015 bounded worker did not publish within 15 seconds.");
                        deadline.ThrowIfCancellationRequested();
                    }
                    using AdmittedJobOutcome outcome = published!;
                    if (!inFlight.Remove(outcome.Job.LaneIndex, out AdmittedExpansionJob? dispatched)
                        || !ReferenceEquals(dispatched, outcome.Job))
                        throw new InvalidOperationException("B015 lane published another job's outcome.");
                    idle.Push(outcome.Job.LaneIndex);
                    _coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
                    outcome.Error?.Throw();
                    AdmittedParent parent = outcome.Job.Parent;
                    if (!parents.Any(item => ReferenceEquals(item, parent)))
                        throw new InvalidOperationException("B015 outcome parent is outside the admitted set.");
                    if (outcome.Job.Kind == ParallelExpansionWorkProfile.Kind.Prepare)
                    {
                        prepareCount++;
                        foreach (PreparedCardAction action in outcome.Actions!)
                            ObserveOwned("prepared_candidate", parent, action.Action);
                        outcome.Actions = outcome.Actions!
                            .Where(item => item.Action.CardId is "SHIV" or "BACKSTAB")
                            .GroupBy(item => item.Action.CardId).Select(group => group.First()).ToList();
                        outcome.Potions = [];
                    }
                    else
                    {
                        actionCount++;
                        if (outcome.Job.Kind != ParallelExpansionWorkProfile.Kind.Action
                            || outcome.Probe != null || outcome.Frontier != null || outcome.Batch?.Cards.Count != 1)
                            throw new InvalidOperationException("B015 bounded action entered an unadmitted branch or choice.");
                        RawCardCandidate raw = outcome.Batch.Cards[0];
                        PlanAction action = outcome.Job.Action!.Value.Action;
                        if (!ReferenceEquals(raw.Node.Parent, parent.Node)
                            || raw.Node.Action != action || raw.TargetCombatId != action.TargetCombatId)
                            throw new InvalidOperationException("B015 action batch lost its originating parent or target.");
                        ObserveOwned("completed_action", parent, action, raw.Node.Snapshot,
                            expected[(parent, outcome.Job.ItemIndex)]);
                    }
                    parent.Receive(outcome);
                    DispatchAvailable();
                }

                void DispatchAvailable()
                {
                    while (idle.TryPeek(out int laneIndex))
                    {
                        _coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                        AdmittedExpansionJob? job = scheduler.NextJob(laneIndex, phase);
                        if (job == null) break;
                        if (phase == SerialJobPhase.Prepare && job.Kind != ParallelExpansionWorkProfile.Kind.Prepare
                            || phase == SerialJobPhase.Card && job.Kind != ParallelExpansionWorkProfile.Kind.Action)
                            throw new InvalidOperationException("B015 scheduler produced a job outside the bounded phase.");
                        if (job.Kind == ParallelExpansionWorkProfile.Kind.Action)
                        {
                            if (job.Action != job.Parent.Actions![job.ItemIndex]
                                || !expected.ContainsKey((job.Parent, job.ItemIndex)))
                                throw new InvalidOperationException("B015 scheduler paired another parent's prepared action or slot.");
                            ObserveOwned("dispatch_action", job.Parent, job.Action!.Value.Action);
                        }
                        bool registered = false;
                        try
                        {
                            job.Parent.MarkDispatched(job);
                            wave.BackgroundCompleted.AddCount();
                            registered = true;
                            inFlight.Add(laneIndex, job);
                            lanes[laneIndex].Dispatch(new B015ObservedLaneWork(job, outcomeReady));
                        }
                        catch
                        {
                            inFlight.Remove(laneIndex);
                            if (registered) wave.BackgroundCompleted.Signal();
                            throw;
                        }
                        idle.Pop();
                    }
                }
            }
        }
    }
}
