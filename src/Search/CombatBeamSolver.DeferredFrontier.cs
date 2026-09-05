namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed record DeferredFrontierTicket(
        SearchNode Node,
        ContinuationStamp? VerificationStamp);

    // Only released leaf metadata is retained. Round-robin service gives each recorded cut a
    // chance; neither a ticket nor a rejected restore is placed back in this queue.
    private sealed class DeferredTurnFrontier(int beamWidth, int maximumNodes)
    {
        private readonly Queue<Queue<DeferredFrontierTicket>> _cuts = [];
        private readonly int _ticketLimit = Math.Min(1_024, beamWidth * 16);
        private readonly int _pathNodeLimit = Math.Min(16_384, maximumNodes);
        private int _tickets;
        private int _pathNodes;

        public int PerCutLimit { get; } = Math.Min(128, beamWidth * 2);
        public int Count => _tickets;

        public void AddCut(IEnumerable<DeferredFrontierTicket> candidates)
        {
            Queue<DeferredFrontierTicket> cut = [];
            foreach (DeferredFrontierTicket ticket in candidates)
            {
                int pathNodes = ticket.Node.ActionCount + 1;
                if (_tickets >= _ticketLimit || _pathNodes + pathNodes > _pathNodeLimit)
                    break;
                cut.Enqueue(ticket);
                _tickets++;
                _pathNodes += pathNodes;
            }
            if (cut.Count > 0)
                _cuts.Enqueue(cut);
        }

        public DeferredFrontierTicket? Take()
        {
            if (!_cuts.TryDequeue(out Queue<DeferredFrontierTicket>? cut))
                return null;
            DeferredFrontierTicket ticket = cut.Dequeue();
            _tickets--;
            _pathNodes -= ticket.Node.ActionCount + 1;
            if (cut.Count > 0)
                _cuts.Enqueue(cut);
            return ticket;
        }

        public void Clear()
        {
            _cuts.Clear();
            _tickets = 0;
            _pathNodes = 0;
        }
    }

    private bool IsDeferredOrdinaryCandidate(SearchNode node)
        => node.ActionCount is > 0 and <= 128
            && !node.IsTerminal
            && !node.HasPredictionRisk
            && node.BoundaryReason == SearchBoundaryReason.None
            && node.Snapshot is { HasSimulator: true, HasRisk: false,
                PlayerDead: false, AllEnemiesDead: false, BoundaryReason: SearchBoundaryReason.None,
                Continuation: null }
            && node.CycleProbeLease == null
            && node.CycleExitProbe == null
            && node.CycleExitObservation == null
            && node.PendingCycleExitObservation == null
            && node.CrossTurnProbe == null
            && node.OrderedMutationRetentionLease == null
            && node.OrderedMutationActivationTicket == null
            && !node.OrderedMutationLeaseTransitionPending
            && !node.OrderedMutationAdmissionPending
            && !node.OrderedMutationAdmissionCharged
            && !node.OrderedMutationContinuationHandoff
            && !node.OrderedMutationContinuationBridge
            && !node.OrderedMutationObservationRequested
            && !node.OrderedMutationObservationDebtSettlementPending
            && node.OrderedMutationObservationStepsRemaining == 0
            && !_run.PendingOrderedMutationHandoffSourceByNode.ContainsKey(node)
            && !_run.PendingOrderedMutationOrdinaryFallbackNodes.Contains(node);

    private void CaptureDeferredFrontier(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyList<SearchNode> finalSurvivors)
    {
        if (_run.DeferredFrontier is not { } bank)
            return;
        HashSet<SimulationSnapshot> survivorSnapshots = new(
            finalSurvivors.Select(node => node.Snapshot), ReferenceEqualityComparer.Instance);
        List<SearchNode> dropped = Retention.RankDeferredCandidates(
            candidates.Where(node => !survivorSnapshots.Contains(node.Snapshot)
                && IsDeferredOrdinaryCandidate(node)),
            bank.PerCutLimit);
        ValidateHistoricalSimulatorsReleased(dropped);
        int countBefore = bank.Count;
        bank.AddCut(dropped.Select(node => new DeferredFrontierTicket(
            node,
            policy.VerifyIncrementalSearch
                ? ContinuationStamp.CapturePredicted(
                    _player, node.Snapshot.Simulator, node.Turn, _forecast, _startTurnNumber)
                : null)));
        _run.DeferredFrontierCaptured += bank.Count - countBefore;
        // The caller immediately runs normal ReleaseDroppedSnapshots on this same pool.
    }

    private bool HasHardPolicyVictory(IEnumerable<SearchNode> candidates)
        => candidates.Any(node =>
            ExplicitPotionUseCount(node) >= _minimumPotionUses
            && (_potionPolicy != SolverPotionPolicy.RequireAtLeastOne
                || ExplicitPotionUseCount(node) > 0)
            && (!_maximumPotionUses.HasValue
                || ExplicitPotionUseCount(node) <= _maximumPotionUses.Value)
            && (!_enforcePotionDirectives
                || _potionStrategy.EvaluateForcedUses(
                    node.Actions, root.HasRenewablePotionShapedRock).AllForcedUsesSatisfied)
            && SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount, node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead, node.Snapshot.ProjectedPlayerHp));

    private SearchNode? RestoreDeferredFrontierTicket(
        DeferredFrontierTicket ticket,
        Func<bool> canContinue,
        Action beforeReplayStep,
        Action<SimulationSnapshot>? observer = null)
    {
        SearchNode node = ticket.Node;
        if (node.Snapshot.HasSimulator)
            throw new InvalidOperationException("落选分支恢复票据仍在保留模拟器。");
        // Pay for the root replay and every actual prefix edge, leaving one normal expansion.
        if (_profile.MaxExpandedNodes - _run.Expanded < node.ActionCount + 2 || !canContinue())
            return null;
        SimulationSnapshot? replayed = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeReplayStep();
            if (!canContinue())
                return null;
            _run.Expanded++;
            _run.DeferredFrontierReplayRoots++;
            replayed = _includeTurnSetup
                ? ReplayTurnSetup(node.GetTurnSetupChoices())
                : Replay([]);
            observer?.Invoke(replayed);
            IReadOnlyList<PlanAction> actions = node.Actions;
            for (int index = 0; index < actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                beforeReplayStep();
                if (!canContinue())
                    return null;
                _run.Expanded++;
                _run.DeferredFrontierReplayActions++;
                SimulationSnapshot next = Replay(
                    [actions[index]], replayed, replayed.Turn, priorActionCount: index);
                replayed.ReleaseSimulator();
                replayed = next;
                observer?.Invoke(replayed);
            }
            AssertDeferredReplayEquivalent(node.Snapshot, replayed);
            if (ticket.VerificationStamp is { } expectedStamp)
            {
                ContinuationStamp actualStamp = ContinuationStamp.CapturePredicted(
                    _player, replayed.Simulator, replayed.Turn, _forecast, _startTurnNumber);
                if (expectedStamp != actualStamp)
                {
                    throw new InvalidOperationException(
                        "落选分支完整回放状态不一致："
                        + string.Join(" || ", expectedStamp.DescribeDifferences(actualStamp)));
                }
            }
            // Keep actual post-final scheduling history and the old parent chain. In particular,
            // no previously expired lease or admission is reconstructed from combat state.
            SearchNode restored = node with { Snapshot = replayed };
            replayed = null;
            _run.DeferredFrontierRestored++;
            return restored;
        }
        finally
        {
            replayed?.ReleaseSimulator();
        }
    }

    internal static void VerifyDeferredTurnFrontierPolicyForTesting(SimulationSnapshot releasedTemplate)
    {
        if (releasedTemplate.HasSimulator)
            throw new InvalidOperationException("落选队列合同只能借用已释放的快照元数据。");
        VerifyOrderedMutationLineageBuilderForTesting();

        // These shells supply only identity and ActionCount-based queue cost. They deliberately
        // do not claim to be admitted routes or to prove any frontier-quality improvement.
        DeferredFrontierTicket Ticket(int actionCount)
            => new(new SearchNode(null, actionCount, releasedTemplate.PotionUseCount,
                releasedTemplate.PotionStrategicCost, releasedTemplate.Turn,
                SearchRouteTraits.None, 0, releasedTemplate.Score, releasedTemplate.StateKey,
                releasedTemplate.HasRisk, releasedTemplate.BoundaryReason, false, null,
                releasedTemplate, CombatProgressState.Capture(releasedTemplate)), null);

        DeferredTurnFrontier ticketBound = new(beamWidth: 1, maximumNodes: 200);
        for (int cut = 0; cut < 8; cut++)
            ticketBound.AddCut([Ticket(1), Ticket(1)]);
        ticketBound.AddCut([Ticket(1)]);
        if (ticketBound.PerCutLimit != 2 || ticketBound.Count != 16 || ticketBound.Take() == null)
            throw new InvalidOperationException("落选票据容量未遵守 Beam×16，或取票未消费名额。");
        DeferredFrontierTicket replacement = Ticket(1);
        ticketBound.AddCut([replacement]);
        if (ticketBound.Count != 16)
            throw new InvalidOperationException("已消费的落选票据容量没有交还。");
        int drained = 0, replacementCount = 0;
        while (ticketBound.Take() is { } taken)
        {
            drained++;
            if (ReferenceEquals(taken, replacement))
                replacementCount++;
        }
        if (drained != 16 || replacementCount != 1 || ticketBound.Count != 0)
            throw new InvalidOperationException("落选票据重复出队或未精确退休。");

        DeferredTurnFrontier pathBound = new(beamWidth: 10, maximumNodes: 8);
        DeferredFrontierTicket pathFirst = Ticket(2), pathSecond = Ticket(2);
        pathBound.AddCut([pathFirst, pathSecond, Ticket(2)]);
        if (pathBound.Count != 2 || !ReferenceEquals(pathBound.Take(), pathFirst))
            throw new InvalidOperationException("落选路径成本没有计入根节点或未按原顺序消费。");
        DeferredFrontierTicket exactRemainder = Ticket(4);
        pathBound.AddCut([exactRemainder]);
        pathBound.AddCut([Ticket(1)]);
        if (pathBound.Count != 2 || !ReferenceEquals(pathBound.Take(), pathSecond)
            || !ReferenceEquals(pathBound.Take(), exactRemainder) || pathBound.Take() != null)
            throw new InvalidOperationException("落选路径成本释放或恰好到达容量边界时不正确。");

        DeferredTurnFrontier fair = new(beamWidth: 10, maximumNodes: 100);
        DeferredFrontierTicket a1 = Ticket(1), a2 = Ticket(1);
        DeferredFrontierTicket b1 = Ticket(1), b2 = Ticket(1), c1 = Ticket(1), d1 = Ticket(1);
        fair.AddCut([a1, a2]);
        fair.AddCut([b1, b2]);
        fair.AddCut([c1]);
        if (!ReferenceEquals(fair.Take(), a1))
            throw new InvalidOperationException("落选队列没有从首个裁剪开始服务。");
        fair.AddCut([d1]);
        foreach (DeferredFrontierTicket expected in new[] { b1, c1, a2, d1, b2 })
        {
            if (!ReferenceEquals(fair.Take(), expected))
                throw new InvalidOperationException("落选裁剪队列未轮流服务，或新裁剪插入了队首。");
        }
        if (fair.Count != 0 || fair.Take() != null)
            throw new InvalidOperationException("已退休的落选票据重新出现。");
        fair.AddCut([a1, b1]);
        fair.Clear();
        if (fair.Count != 0 || fair.Take() != null)
            throw new InvalidOperationException("回合结束未清空落选队列。");
        fair.AddCut([c1]);
        if (!ReferenceEquals(fair.Take(), c1) || fair.Count != 0)
            throw new InvalidOperationException("清空后的落选队列不能正常重新使用。");

        // AddCut owns aggregate limits; CaptureDeferredFrontier owns per-cut selection. This
        // deliberately oversized synthetic cut isolates the aggregate hard cap only.
        DeferredTurnFrontier absoluteTickets = new(beamWidth: 100, maximumNodes: 20_000);
        absoluteTickets.AddCut(Enumerable.Range(0, 1_025).Select(_ => Ticket(1)));
        if (absoluteTickets.PerCutLimit != 128 || absoluteTickets.Count != 1_024)
            throw new InvalidOperationException("落选队列没有遵守1024票据硬上限。");
        absoluteTickets.Clear();
        DeferredTurnFrontier absolutePaths = new(beamWidth: 100, maximumNodes: 20_000);
        absolutePaths.AddCut(Enumerable.Range(0, 129).Select(_ => Ticket(127)));
        if (absolutePaths.Count != 128)
            throw new InvalidOperationException("落选队列没有遵守16384路径节点硬上限。");
        absolutePaths.Clear();
        if (releasedTemplate.HasSimulator)
            throw new InvalidOperationException("纯队列合同意外重建了模拟器。");
    }

    internal int VerifyDeferredFrontierReplayForTesting(
        SimulationSnapshot initial,
        IReadOnlyList<(PlanAction Action, SimulationSnapshot Snapshot)> prefixes,
        ContinuationStamp verificationStamp,
        PlanAction suffixAction,
        SimulationSnapshot expectedSuffix,
        Action checkDeadline)
    {
        if (_includeTurnSetup || initial.HasSimulator || expectedSuffix.HasSimulator
            || prefixes.Count is < 2 or > 128
            || prefixes.Any(item => item.Snapshot.HasSimulator)
            || prefixes[^1].Snapshot is { PlayerDead: true } or { AllEnemiesDead: true })
            throw new InvalidOperationException("恢复合同要求已严格回放并释放的 Play 根与非终局前缀。");

        SearchNode parent = new(null, 0, initial.PotionUseCount, initial.PotionStrategicCost,
            initial.Turn, SearchRouteTraits.None, 0, initial.Score, initial.StateKey,
            initial.HasRisk, initial.BoundaryReason, false, null, initial,
            CombatProgressState.Capture(initial));
        for (int index = 0; index < prefixes.Count; index++)
        {
            (PlanAction action, SimulationSnapshot snapshot) = prefixes[index];
            parent = new SearchNode(action, index + 1, snapshot.PotionUseCount,
                snapshot.PotionStrategicCost, snapshot.Turn, SearchRouteTraits.None,
                0, snapshot.Score, snapshot.StateKey, snapshot.HasRisk,
                snapshot.BoundaryReason, false, parent, snapshot,
                CombatProgressState.Capture(snapshot));
        }
        StateFingerprint evidenceKey = new(0xd3f311UL, 0xd3f312UL);
        // The snapshots/actions are real formal replay results. The deliberately distinct
        // policy/evidence labels below test copying only, never real search admission.
        SearchNode original = parent with
        {
            FutureSoldHp = 2,
            Score = ApplySoldHpPenalty(parent.Snapshot.Score, 2),
            Traits = SearchRouteTraits.Resource | SearchRouteTraits.HpInvestment,
            CumulativeEnemyHpLost = 73,
            RetentionRank = 17,
            LongTermResourceRetentionRank = 23,
            CycleRetentionRank = 31,
            CycleExitRetentionRank = 37,
            CrossTurnRetentionRank = 41,
            Cycle = new CycleSearchState(parent.Snapshot.CycleShapeKey, evidenceKey, 2, 1,
                default, false) { PriorCycleEndpoint = parent.Parent },
            OrderedMutationLineage = new OrderedMutationLineage(parent.Turn, 2, evidenceKey, evidenceKey),
        };
        ValidateHistoricalSimulatorsReleased([original]);
        DeferredFrontierTicket ticket = new(original, verificationStamp);
        int actionCount = prefixes.Count;
        int completedAttempts = 0;
        RunAttempt("insufficient_budget", actionCount + 1, 0, 0, 0);
        RunAttempt("stopped_before_root", actionCount + 2, 0, 0, 0, stopBeforeStep: 0);
        RunAttempt("stopped_after_root", actionCount + 2, 1, 0, 2, stopBeforeStep: 2);
        RunAttempt("callback_failure", actionCount + 2, 1, 1, 3, throwBeforeStep: 3);
        RunAttempt("observer_failure", actionCount + 2, 1, 0, 1, throwOnSnapshot: 1);
        RunAttempt("edge_observer_failure", actionCount + 2, 1, 1, 2, throwOnSnapshot: 2);
        RunAttempt("canceled_before_root", actionCount + 2, 0, 0, 0, cancelBeforeRoot: true);
        RunAttempt("strict_mismatch", actionCount + 2, 1, actionCount, actionCount + 1,
            corruptExpectedSnapshot: true);
        RunAttempt("success_and_suffix", actionCount + 2, 1, actionCount, actionCount + 1,
            expectSuccess: true);
        return completedAttempts;

        void RunAttempt(string label, int maximumNodes, int expectedRoots, int expectedActions,
            int expectedBeforeSteps, int stopBeforeStep = int.MaxValue,
            int throwBeforeStep = 0, int throwOnSnapshot = 0,
            bool cancelBeforeRoot = false, bool corruptExpectedSnapshot = false,
            bool expectSuccess = false)
        {
            checkDeadline();
            using CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (cancelBeforeRoot)
                cancellation.Cancel();
            CombatBeamSolver replayDriver = new(root, displayNames, battleDamage, policy,
                cancellation.Token, searchProfile: _profile with
                {
                    MaxExpandedNodes = maximumNodes,
                    RecoverDeferredTurnFrontier = true,
                });
            Action assertLedgers = SeedDeferredReplayLedgersForTesting(replayDriver, original);
            DeferredTurnFrontier bank = new(beamWidth: 1, maximumNodes: actionCount + 1);
            bank.AddCut([corruptExpectedSnapshot
                ? ticket with { Node = original with { Snapshot = initial, StateKey = initial.StateKey } }
                : ticket]);
            DeferredFrontierTicket taken = bank.Take()
                ?? throw new InvalidOperationException("恢复合同未取到唯一票据。");
            List<SimulationSnapshot> holders = [];
            SearchNode? restored = null;
            SimulationSnapshot? suffix = null;
            int beforeSteps = 0;
            bool expectedFailureObserved = false;
            InvalidOperationException injected = new("deferred_replay_injected_" + label);
            try
            {
                try
                {
                    restored = replayDriver.RestoreDeferredFrontierTicket(taken,
                        () => beforeSteps < stopBeforeStep,
                        () =>
                        {
                            checkDeadline();
                            beforeSteps++;
                            if (beforeSteps == throwBeforeStep)
                                throw injected;
                        },
                        snapshot =>
                        {
                            holders.Add(snapshot);
                            if (holders.Count == throwOnSnapshot)
                                throw injected;
                        });
                }
                catch (InvalidOperationException error) when (ReferenceEquals(error, injected))
                {
                    expectedFailureObserved = true;
                }
                catch (OperationCanceledException) when (cancelBeforeRoot && cancellation.IsCancellationRequested)
                {
                    expectedFailureObserved = true;
                }
                catch (InvalidOperationException error) when (corruptExpectedSnapshot
                    && error.Message.StartsWith("落选分支回放与原快照不一致：", StringComparison.Ordinal))
                {
                    expectedFailureObserved = true;
                }

                bool expectsFailure = throwBeforeStep > 0 || throwOnSnapshot > 0
                    || cancelBeforeRoot || corruptExpectedSnapshot;
                if (expectedFailureObserved != expectsFailure || (restored != null) != expectSuccess
                    || beforeSteps != expectedBeforeSteps || bank.Count != 0 || bank.Take() != null
                    || replayDriver._run.Expanded != expectedRoots + expectedActions
                    || replayDriver._run.DeferredFrontierReplayRoots != expectedRoots
                    || replayDriver._run.DeferredFrontierReplayActions != expectedActions
                    || replayDriver._run.DeferredFrontierRestored != (expectSuccess ? 1 : 0)
                    || replayDriver._run.ReplayCount != expectedRoots
                    || replayDriver._run.TransitionCount != expectedActions
                    || replayDriver._run.ForkCount != expectedActions
                    || replayDriver._run.TranspositionBranchesPruned != 0
                    || holders.Count != expectedRoots + expectedActions)
                    throw new InvalidOperationException($"恢复合同 {label} 的工作、失败或票据退休计数不一致：" +
                        $"before={beforeSteps}/{expectedBeforeSteps} holders={holders.Count} " +
                        $"expanded={replayDriver._run.Expanded}/{expectedRoots + expectedActions} " +
                        $"roots={replayDriver._run.DeferredFrontierReplayRoots}/{expectedRoots} " +
                        $"actions={replayDriver._run.DeferredFrontierReplayActions}/{expectedActions} " +
                        $"replays={replayDriver._run.ReplayCount} forks={replayDriver._run.ForkCount} " +
                        $"transitions={replayDriver._run.TransitionCount} " +
                        $"restored={replayDriver._run.DeferredFrontierRestored} failure={expectedFailureObserved}。");

                for (int index = 0; index < holders.Count; index++)
                {
                    bool returnedLeaf = expectSuccess && index == holders.Count - 1;
                    if (holders[index].HasSimulator != returnedLeaf)
                        throw new InvalidOperationException($"恢复合同 {label} 未释放临时快照 {index}。");
                }
                assertLedgers();
                if (restored != null)
                {
                    if (!ReferenceEquals(restored.Parent, original.Parent)
                        || !ReferenceEquals(restored.Action, original.Action)
                        || !ReferenceEquals(restored.CombatProgress, original.CombatProgress)
                        || !ReferenceEquals(restored.Cycle, original.Cycle)
                        || !ReferenceEquals(restored.OrderedMutationLineage, original.OrderedMutationLineage)
                        || (restored with { Snapshot = original.Snapshot }) != original
                        || !replayDriver.IsDeferredOrdinaryCandidate(restored)
                        || replayDriver._profile.MaxExpandedNodes - replayDriver._run.Expanded != 1)
                        throw new InvalidOperationException("成功恢复未仅更换快照，或丢失了最后一次正常工作额度。");
                    AssertDeferredReplayEquivalent(original.Snapshot, restored.Snapshot);
                    // This is one explicitly charged formal suffix action, not an ExpandedTT
                    // admission or evidence that a synthetic node would survive a real frontier.
                    replayDriver._run.Expanded++;
                    suffix = replayDriver.Replay([suffixAction], restored.Snapshot,
                        restored.Turn, priorActionCount: actionCount);
                    AssertDeferredReplayEquivalent(expectedSuffix, suffix);
                    if (!suffix.AllEnemiesDead || suffix.PlayerDead || suffix.Simulator.IsInProgress
                        || replayDriver._run.Expanded != maximumNodes
                        || replayDriver._run.TransitionCount != actionCount + 1
                        || replayDriver._run.DeferredFrontierReplayActions != actionCount)
                        throw new InvalidOperationException("恢复后的正式后缀没有在剩余额度内到达原胜利状态。");
                    assertLedgers();
                }
            }
            finally
            {
                suffix?.ReleaseSimulator();
                restored?.Snapshot.ReleaseSimulator();
                foreach (SimulationSnapshot holder in holders)
                    holder.ReleaseSimulator();
                bank.Clear();
            }
            if (holders.Any(snapshot => snapshot.HasSimulator)
                || original.Snapshot.HasSimulator || initial.HasSimulator)
                throw new InvalidOperationException($"恢复合同 {label} 结束后仍在持有模拟器。");
            ValidateHistoricalSimulatorsReleased([original]);
            completedAttempts++;
        }
    }

    private static Action SeedDeferredReplayLedgersForTesting(CombatBeamSolver driver, SearchNode node)
    {
        SearchRunContext run = driver._run;
        StateFingerprint key = new(0xd3f321UL, 0xd3f322UL);
        TranspositionLabel label = new(node.PotionCount, node.PotionStrategicCost, node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost, node.ActionCount, node.Score);
        Dictionary<StateFingerprint, TranspositionFrontier> admissions = run.Transpositions;
        Dictionary<StateFingerprint, TranspositionFrontier> expansions = run.ExpandedTranspositions;
        admissions.Add(node.StateKey, new TranspositionFrontier(label));
        expansions.Add(node.StateKey, new TranspositionFrontier(label));
        CanonicalCycleFamilyKey family = new(node.Turn, 1, key);
        CycleFamilyLedgerEntry familyLedger = new()
        {
            ProbeStarts = 2, ProbeExpandedNodes = 3, EarnedImprovementEpoch = 2,
            ActiveImprovementEpoch = 1, RequestedImprovementEpoch = 3,
            ImprovementEvidenceActionCounts = [4],
        };
        familyLedger.AdmittedActionCounts.Add(4);
        run.CycleFamilyLedger.Add(family, familyLedger);
        CycleRegionLedgerEntry regionLedger = new()
        {
            AdmittedNodes = 5, ProbeAdmittedNodes = 6, ProgressAdmittedNodes = 7,
            ProgressEpochs = 2, ProgressActionsRemaining = 3,
            ProgressContinuationNode = new WeakReference<SearchNode>(node.Parent!),
        };
        run.CycleRegionLedger.Add(new CycleRegionKey(node.Turn, key), regionLedger);
        CycleProbeTracker tracker = new(key, key, [key], family);
        CycleExitProbeTicketKey expandedTicket = new(tracker, 0, key, 1);
        run.ExpandedCycleProbeTickets.Add(expandedTicket);
        run.CycleRegionAdmissionsByTurn.Add(node.Turn, 11);
        run.CycleRegionProbeAdmissionsByTurn.Add(node.Turn, 3);
        run.CycleRegionMaxProgressEpochsByTurn.Add(node.Turn, 2);
        run.CycleFamilyDepthsConsumedByTurn.Add(node.Turn, 4);
        run.CycleProbeExpandedNodesConsumedByTurn.Add(node.Turn, 5);
        run.OrderedMutationAdmissionsByRootLease.Add(key, 13);
        run.OrderedMutationAdmissionsByInitialLease.Add(key, 7);
        run.OrderedMutationAdmissionsByLease.Add(key, 3);
        run.OrderedMutationHandoffAdmissionsByInitialAndSource.Add(new(key, key), 2);
        run.CycleRegionPortfolioNodesConsumed = 17;
        run.OrderedMutationPortfolioNodesConsumed = 19;
        run.OrderedMutationLeaseExpiredBudget = 2;
        run.OrderedMutationOrdinaryFallbacks = 3;
        run.OrderedMutationColdAtomicCommitted = 4;
        run.OrderedMutationColdAtomicRejected = 5;
        Action[] compareDictionaries =
        [
            Capture(admissions), Capture(expansions), Capture(run.CycleFamilyLedger),
            Capture(run.CycleRegionLedger), Capture(run.CycleRegionAdmissionsByTurn),
            Capture(run.CycleRegionProbeAdmissionsByTurn), Capture(run.CycleRegionMaxProgressEpochsByTurn),
            Capture(run.CycleFamilyDepthsConsumedByTurn), Capture(run.CycleProbeExpandedNodesConsumedByTurn),
            Capture(run.OrderedMutationAdmissionsByRootLease), Capture(run.OrderedMutationAdmissionsByInitialLease),
            Capture(run.OrderedMutationAdmissionsByLease), Capture(run.OrderedMutationHandoffAdmissionsByInitialAndSource),
        ];
        return () =>
        {
            foreach (Action compare in compareDictionaries)
                compare();
            if (!ReferenceEquals(run.Transpositions, admissions)
                || !ReferenceEquals(run.ExpandedTranspositions, expansions)
                || run.ExpandedCycleProbeTickets.Count != 1
                || !run.ExpandedCycleProbeTickets.Contains(expandedTicket)
                || tracker.ExitQualityEpoch != 0
                || familyLedger.ProbeStarts != 2 || familyLedger.ProbeExpandedNodes != 3
                || familyLedger.EarnedImprovementEpoch != 2 || familyLedger.ActiveImprovementEpoch != 1
                || familyLedger.RequestedImprovementEpoch != 3
                || !familyLedger.AdmittedActionCounts.SetEquals([4])
                || familyLedger.ImprovementEvidenceActionCounts == null
                || !familyLedger.ImprovementEvidenceActionCounts.SetEquals([4])
                || regionLedger.AdmittedNodes != 5 || regionLedger.ProbeAdmittedNodes != 6
                || regionLedger.ProgressAdmittedNodes != 7 || regionLedger.ProgressEpochs != 2
                || regionLedger.ProgressActionsRemaining != 3
                || regionLedger.ProgressContinuationNode == null
                || !regionLedger.ProgressContinuationNode.TryGetTarget(out SearchNode? witness)
                || !ReferenceEquals(witness, node.Parent)
                || run.CycleRegionPortfolioNodesConsumed != 17
                || run.OrderedMutationPortfolioNodesConsumed != 19
                || run.OrderedMutationLeaseExpiredBudget != 2 || run.OrderedMutationOrdinaryFallbacks != 3
                || run.OrderedMutationColdAtomicCommitted != 4 || run.OrderedMutationColdAtomicRejected != 5
                || run.PendingOrderedMutationHandoffSourceByNode.Count != 0
                || run.PendingOrderedMutationOrdinaryFallbackNodes.Count != 0)
                throw new InvalidOperationException("前缀回放重建、消费或推进了原转置/循环/有序调度账本。");
        };

        static Action Capture<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
        {
            KeyValuePair<TKey, TValue>[] expected = dictionary.ToArray();
            return () =>
            {
                if (!dictionary.SequenceEqual(expected))
                    throw new InvalidOperationException("前缀回放修改了已消费的调度账本或转置标签。");
            };
        }
    }

    private static void AssertDeferredReplayEquivalent(
        SimulationSnapshot expected, SimulationSnapshot actual)
    {
        if (expected.StateKey != actual.StateKey
            || !expected.Score.Equals(actual.Score)
            || expected.Turn != actual.Turn
            || expected.ShufflesCrossed != actual.ShufflesCrossed
            || expected.HasRisk != actual.HasRisk
            || expected.PlayerDead != actual.PlayerDead
            || expected.AllEnemiesDead != actual.AllEnemiesDead
            || expected.TerminalStamp != actual.TerminalStamp
            || expected.CombatEndedTurn != actual.CombatEndedTurn
            || expected.DeathTurn != actual.DeathTurn
            || expected.BoundaryReason != actual.BoundaryReason
            || expected.Continuation != actual.Continuation
            || expected.PlayerHp != actual.PlayerHp
            || expected.PlayerMaxHp != actual.PlayerMaxHp
            || expected.CumulativePlayerHpLost != actual.CumulativePlayerHpLost
            || expected.AngerCopiesGenerated != actual.AngerCopiesGenerated
            || expected.PlayerBlock != actual.PlayerBlock
            || expected.EnemyHp != actual.EnemyHp
            || expected.EnemyBlock != actual.EnemyBlock
            || expected.AliveEnemyCount != actual.AliveEnemyCount
            || expected.AliveEnemyMask != actual.AliveEnemyMask
            || expected.RawEnemyHp != actual.RawEnemyHp
            || expected.MaxCurrentEnemyHp != actual.MaxCurrentEnemyHp
            || expected.EnemyCombatDistributionKey != actual.EnemyCombatDistributionKey
            || !EnemyDurabilityEquals(
                expected.EnemyDurabilityByCombatId, actual.EnemyDurabilityByCombatId)
            || expected.RevivingEnemyCount != actual.RevivingEnemyCount
            || expected.PotionUseCount != actual.PotionUseCount
            || expected.AutomaticPotionUseCount != actual.AutomaticPotionUseCount
            || expected.PotionStrategicCost != actual.PotionStrategicCost
            || expected.UnorderedPileKey != actual.UnorderedPileKey
            || expected.CycleShapeKey != actual.CycleShapeKey
            || expected.ProjectedShuffleOrderKey != actual.ProjectedShuffleOrderKey
            || expected.ProjectedShuffleOrderValue != actual.ProjectedShuffleOrderValue
            || expected.StrategicEffects != actual.StrategicEffects
            || expected.ProjectedPlayerHp != actual.ProjectedPlayerHp
            || expected.PersistentBuffValue != actual.PersistentBuffValue
            || expected.PersistentSetupTraits != actual.PersistentSetupTraits
            || expected.LatentSetupValue != actual.LatentSetupValue
            || expected.LatentSetupTraits != actual.LatentSetupTraits
            || expected.StrategicSetupTraits != actual.StrategicSetupTraits
            || expected.FocusTargetCombatId != actual.FocusTargetCombatId
            || expected.FocusTargetPressure != actual.FocusTargetPressure
            || expected.FocusTargetRemainingHp != actual.FocusTargetRemainingHp
            || expected.FocusTargetCurrentThreat != actual.FocusTargetCurrentThreat
            || expected.FocusTargetVulnerableTurns != actual.FocusTargetVulnerableTurns
            || expected.MostVulnerableTargetCombatId != actual.MostVulnerableTargetCombatId
            || expected.RetainedAttackValue != actual.RetainedAttackValue
            || expected.ReplayPotentialValue != actual.ReplayPotentialValue
            || expected.FutureResourceValue != actual.FutureResourceValue
            || expected.LongTermResourceValue != actual.LongTermResourceValue
            || expected.OstyHp != actual.OstyHp
            || expected.OstyMaxHp != actual.OstyMaxHp
            || expected.DelayedDamageValue != actual.DelayedDamageValue
            || expected.ReactiveDamageValue != actual.ReactiveDamageValue
            || expected.EnemyStrengthSuppression != actual.EnemyStrengthSuppression
            || expected.EnemyWeakTurns != actual.EnemyWeakTurns
            || expected.EnemyVulnerableTurns != actual.EnemyVulnerableTurns
            || expected.EnemyControlDistributionKey != actual.EnemyControlDistributionKey
            || expected.SandpitRemaining != actual.SandpitRemaining
            || expected.LiveDeckClutter != actual.LiveDeckClutter
            || expected.LiveDeckSize != actual.LiveDeckSize
            || expected.OutstandingStolenResource != actual.OutstandingStolenResource
            || expected.OffensiveProgressValue != actual.OffensiveProgressValue
            || expected.Energy != actual.Energy
            || expected.Stars != actual.Stars
            || expected.HistoryEntryCount != actual.HistoryEntryCount
            || expected.HandCount != actual.HandCount
            || expected.ReachableHandValue != actual.ReachableHandValue
            || expected.ZeroCostPlayableCount != actual.ZeroCostPlayableCount
            || expected.CanTriggerArtOfWarNextTurn != actual.CanTriggerArtOfWarNextTurn
            || expected.PocketwatchCardsPlayedThisTurn != actual.PocketwatchCardsPlayedThisTurn
            || expected.PocketwatchCardsPlayedLastTurn != actual.PocketwatchCardsPlayedLastTurn
            || expected.PocketwatchCardThreshold != actual.PocketwatchCardThreshold
            || expected.CanStillTriggerPocketwatch != actual.CanStillTriggerPocketwatch
            || !expected.ProcessedEnemyDeaths.SetEquals(actual.ProcessedEnemyDeaths)
            || !expected.PredictionGaps.SequenceEqual(actual.PredictionGaps))
        {
            throw new InvalidOperationException(
                $"落选分支回放与原快照不一致：expected={expected.StateKey} "
                + $"actual={actual.StateKey} expected_score={expected.Score} actual_score={actual.Score}。");
        }

        static bool EnemyDurabilityEquals(EnemyDurabilityVector left, EnemyDurabilityVector right)
        {
            if (left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }
    }
}
