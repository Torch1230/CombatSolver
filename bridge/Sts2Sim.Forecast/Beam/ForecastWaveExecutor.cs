using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Commands;
using CombatSolver;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal sealed class ForecastWaveExecutor
{
    internal enum ChoiceNodeStatus : byte
    {
        Unresolved = 0,
        Expanded = 1,
        Child = 2,
        Rejected = 3,
    }

    internal sealed class ChoiceTreeNode
    {
        public ChoiceNodeStatus Status;
        public SearchNode? Child;
        public ChoiceTreeNode[]? Branches;
    }

    // Each attempt owns its tree node. Reconciliation runs after ExecuteGroup
    // has joined every attempt. Only the pruning index is shared by siblings.
    private struct ActionSlotState
    {
        private List<int[]>? _completedPaths;
        private ChoiceTreeNode? _rootChoice;
        private SearchNode? _directChild;
        private ChoiceNodeStatus _directStatus;
        private int _successCount;

        public readonly int CompletedChoiceCount => _successCount;

        public void MarkChild(ChoiceTreeNode? choiceNode, SearchNode node, ChoiceScriptNode? script)
        {
            if (choiceNode is null)
            {
                _directChild = node;
                _directStatus = ChoiceNodeStatus.Child;
                return;
            }

            int[] dfsPath = script!.MaterializeBranchPath();
            choiceNode.Child = node;
            choiceNode.Status = ChoiceNodeStatus.Child;
            List<int[]> paths = _completedPaths!;
            lock (paths)
            {
                paths.Add(dfsPath);
                Volatile.Write(ref _successCount, paths.Count);
            }
        }

        public void MarkRejected(ChoiceTreeNode? choiceNode)
        {
            if (choiceNode is null)
                _directStatus = ChoiceNodeStatus.Rejected;
            else
                choiceNode.Status = ChoiceNodeStatus.Rejected;
        }

        public ChoiceTreeNode[] ExpandChoice(ChoiceTreeNode? choiceNode, int branchCount)
        {
            var branches = new ChoiceTreeNode[branchCount];
            for (int i = 0; i < branchCount; i++)
                branches[i] = new ChoiceTreeNode();

            if (choiceNode is null)
            {
                _completedPaths = new List<int[]>(2);
                _rootChoice = new ChoiceTreeNode { Status = ChoiceNodeStatus.Expanded, Branches = branches };
                _directStatus = ChoiceNodeStatus.Expanded;
            }
            else
            {
                choiceNode.Branches = branches;
                choiceNode.Status = ChoiceNodeStatus.Expanded;
            }
            return branches;
        }

        public void CollectProvenDfsPrefix(List<SearchNode> destination, int maxCount, out bool hitUnresolvedGap)
        {
            destination.Clear();
            hitUnresolvedGap = false;
            if (maxCount <= 0)
                return;

            switch (_directStatus)
            {
                case ChoiceNodeStatus.Unresolved:
                    hitUnresolvedGap = true;
                    return;
                case ChoiceNodeStatus.Child:
                    destination.Add(_directChild!);
                    return;
                case ChoiceNodeStatus.Expanded:
                    WalkChoiceTree(_rootChoice!, destination, maxCount, ref hitUnresolvedGap);
                    return;
            }
        }

        private static bool WalkChoiceTree(
            ChoiceTreeNode node,
            List<SearchNode> destination,
            int maxCount,
            ref bool hitUnresolvedGap)
        {
            switch (node.Status)
            {
                case ChoiceNodeStatus.Child:
                    destination.Add(node.Child!);
                    return destination.Count < maxCount;
                case ChoiceNodeStatus.Rejected:
                    return true;
                case ChoiceNodeStatus.Expanded:
                    ChoiceTreeNode[]? branches = node.Branches;
                    if (branches is null)
                    {
                        hitUnresolvedGap = true;
                        return false;
                    }
                    for (int i = 0; i < branches.Length; i++)
                    {
                        if (!WalkChoiceTree(branches[i], destination, maxCount, ref hitUnresolvedGap))
                            return false;
                    }
                    return true;
                default:
                    hitUnresolvedGap = true;
                    return false;
            }
        }

        public bool HasAtLeastPreceding(ChoiceScriptNode? script, int limit)
        {
            if (script is null || Volatile.Read(ref _successCount) < limit)
                return false;

            int[] candidatePath = script.MaterializeBranchPath();
            List<int[]> paths = _completedPaths!;
            lock (paths)
            {
                int preceding = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (CompareDfsPaths(paths[i], candidatePath) < 0)
                    {
                        preceding++;
                        if (preceding >= limit)
                            return true;
                    }
                }
                return false;
            }
        }
    }

    private sealed class ParentSlotState(SearchNode parent, bool admitted)
    {
        public readonly SearchNode Parent = parent;
        public readonly bool Admitted = admitted;
        public int Initialized;
        public int PlayCardActionCount;
        public ActionSlotState[] Actions = [];
        public PlannedAction[] ActionList = [];
        public int HitPendingChoice;
        public int IncompleteByLimit;

        public void SetActions(PlannedAction[] actionList)
        {
            var slots = new ActionSlotState[actionList.Length];
            int playCardCount = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (actionList[i].Kind == "PlayCard")
                    playCardCount++;
            }
            PlayCardActionCount = playCardCount;
            ActionList = actionList;
            Actions = slots;
            Volatile.Write(ref Initialized, 1);
        }

        public void MarkIncomplete() => Volatile.Write(ref IncompleteByLimit, 1);
    }

    [ThreadStatic]
    private static List<PlannedAction>? _actionListScratch;

    [ThreadStatic]
    private static List<string>? _observedCardIdScratch;

    [ThreadStatic]
    private static List<(SearchNode Node, CombatStateDescriptionDigest Key, int Index)>? _dedupeCardScratch;

    [ThreadStatic]
    private static HashSet<CombatStateDescriptionDigest>? _dedupeExactScratch;

    [ThreadStatic]
    private static HashSet<CombatStateDescriptionDigest>? _dedupeBucketScratch;

    private readonly ForecastLanePool _pool;
    private readonly BeamBudget _budget;
    private readonly CombatPolicy _policy;
    private readonly StateEvaluator _evaluator;
    private readonly BeamPassOptions _options;
    private readonly MemberBudgetController _budgetController;
    private readonly SearchWorkLedger? _ledger;
    private readonly ParentAdmissionTable? _admissionTable;
    private readonly int _homeLaneOffset;
    private readonly int _maxConcurrency;
    private readonly int _openingPreRootBattleHpLost;
    private readonly Stopwatch _watch;
    private readonly ForecastNodeBudgetTracker _nodeBudget;
    private readonly InCombatPassTrack? _track;
    private readonly CancellationToken _ct;
    private readonly Action<PlannedAction, ChoiceScriptNode?>? _beforeChoiceAttempt;

    private long _sharedAttempts;
    private long _sharedParents;
    private long _sharedTransitions;
    private int _victoryFoundFlag;
    private int _limitReachedFlag;
    private int _recordedLimitBoundary;

    public long SharedExpanded => _options.UseParentNodeBudget
        ? Volatile.Read(ref _sharedParents)
        : Math.Max(_nodeBudget.Consumed, Volatile.Read(ref _sharedAttempts));
    public long SharedParents => Volatile.Read(ref _sharedParents);
    public long SharedTransitions => Volatile.Read(ref _sharedTransitions);
    public long SharedAttempts => Volatile.Read(ref _sharedAttempts);
    public bool AcceptableVictoryFound => Volatile.Read(ref _victoryFoundFlag) != 0;
    public ForecastSearchBoundary LimitBoundary => (ForecastSearchBoundary)Volatile.Read(ref _recordedLimitBoundary);

    public ForecastWaveExecutor(
        ForecastLanePool pool,
        BeamBudget budget,
        CombatPolicy policy,
        StateEvaluator evaluator,
        int openingPreRootBattleHpLost,
        Stopwatch watch,
        ForecastNodeBudgetTracker nodeBudget,
        InCombatPassTrack? track,
        CancellationToken ct,
        Action<PlannedAction, ChoiceScriptNode?>? beforeChoiceAttempt = null)
        : this(
            pool,
            budget,
            policy,
            evaluator,
            openingPreRootBattleHpLost == 0
                ? BeamPassOptions.Default
                : BeamPassOptions.Default with { OpeningPreRootBattleHpLost = openingPreRootBattleHpLost },
            new MemberBudgetController(budget.NodeLimit, sharedBudget: null),
            admissionTable: null,
            watch,
            nodeBudget,
            track,
            ct,
            beforeChoiceAttempt: beforeChoiceAttempt)
    {
    }

    public ForecastWaveExecutor(
        ForecastLanePool pool,
        BeamBudget budget,
        CombatPolicy policy,
        StateEvaluator evaluator,
        BeamPassOptions options,
        MemberBudgetController budgetController,
        ParentAdmissionTable? admissionTable,
        Stopwatch watch,
        ForecastNodeBudgetTracker nodeBudget,
        InCombatPassTrack? track,
        CancellationToken ct,
        int maxConcurrency = int.MaxValue,
        Action<PlannedAction, ChoiceScriptNode?>? beforeChoiceAttempt = null)
    {
        _pool = pool;
        _budget = budget;
        _policy = policy;
        _evaluator = evaluator;
        _options = options;
        _budgetController = budgetController;
        _ledger = options.WorkLedger;
        _admissionTable = admissionTable;
        _homeLaneOffset = options.HomeLaneOffset;
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, pool.LaneCount);
        _openingPreRootBattleHpLost = options.OpeningPreRootBattleHpLost;
        _watch = watch;
        _nodeBudget = nodeBudget;
        _track = track;
        _ct = ct;
        _beforeChoiceAttempt = beforeChoiceAttempt;
    }

    public bool CheckExternalStop(ref ForecastSearchBoundary passBoundary)
    {
        if (Volatile.Read(ref _victoryFoundFlag) != 0)
            return true;
        int raw = Volatile.Read(ref _recordedLimitBoundary);
        if (raw != 0 && passBoundary == ForecastSearchBoundary.None)
            passBoundary = (ForecastSearchBoundary)raw;
        if (_watch.Elapsed >= _budget.TimeLimit)
        {
            RecordWorkerLimitBoundary(ForecastSearchBoundary.TimeLimit);
            if (passBoundary == ForecastSearchBoundary.None)
                passBoundary = ForecastSearchBoundary.TimeLimit;
            return true;
        }
        if (_options.UseParentNodeBudget)
        {
            if (SharedParents >= _budget.NodeLimit || _budgetController.Spent >= _budgetController.MemberCap)
            {
                RecordAdmissionNodeLimit();
                if (passBoundary == ForecastSearchBoundary.None)
                    passBoundary = ForecastSearchBoundary.NodeLimit;
                return true;
            }
            return Volatile.Read(ref _limitReachedFlag) != 0;
        }
        if (SharedExpanded >= _budget.NodeLimit || _budgetController.Spent >= _budgetController.MemberCap)
        {
            RecordWorkerLimitBoundary(ForecastSearchBoundary.NodeLimit);
            if (passBoundary == ForecastSearchBoundary.None)
                passBoundary = ForecastSearchBoundary.NodeLimit;
            return true;
        }
        return Volatile.Read(ref _limitReachedFlag) != 0;
    }

    public void RunWave(
        IReadOnlyList<SearchNode> active,
        int layer,
        List<SearchNode> completed,
        List<SearchNode> ended,
        List<SearchNode> next,
        List<SearchNode> truncatedParents,
        ref ForecastSearchBoundary passBoundary,
        ref bool hitPendingChoiceBoundary,
        ref bool acceptableVictoryFound,
        Action<SearchNode> considerBoundary)
    {
        if (active.Count == 0)
            return;

        for (int i = 0; i < active.Count; i++)
            active[i].State.FreezeForConcurrentReads();

        var parentSlots = new ParentSlotState[active.Count];
        var admittedIndices = new List<int>(active.Count);
        for (int p = 0; p < active.Count; p++)
        {
            _ct.ThrowIfCancellationRequested();
            SearchNode parent = active[p];
            if (_admissionTable is not null && !_admissionTable.TryAdmit(parent))
            {
                parentSlots[p] = new ParentSlotState(parent, admitted: false);
                continue;
            }
            if (_options.UseParentNodeBudget)
            {
                if (_watch.Elapsed >= _budget.TimeLimit)
                {
                    RecordWorkerLimitBoundary(ForecastSearchBoundary.TimeLimit);
                    for (int rem = p; rem < active.Count; rem++)
                    {
                        parentSlots[rem] = new ParentSlotState(active[rem], admitted: false);
                        truncatedParents.Add(active[rem]);
                    }
                    break;
                }
                if (Volatile.Read(ref _sharedParents) >= _budget.NodeLimit || !_budgetController.TryReserveOne())
                {
                    RecordAdmissionNodeLimit();
                    for (int rem = p; rem < active.Count; rem++)
                    {
                        parentSlots[rem] = new ParentSlotState(active[rem], admitted: false);
                        truncatedParents.Add(active[rem]);
                    }
                    break;
                }
            }
            Interlocked.Increment(ref _sharedParents);
            _ledger?.AddExpandedParents(1);
            if (_options.UseParentNodeBudget)
                _track?.ObserveExpansion(1, parent.Turn);

            parentSlots[p] = new ParentSlotState(parent, admitted: true);
            admittedIndices.Add(p);
        }

        if (admittedIndices.Count == 0)
        {
            int b = Volatile.Read(ref _recordedLimitBoundary);
            if (!acceptableVictoryFound && b != 0 && passBoundary == ForecastSearchBoundary.None)
                passBoundary = (ForecastSearchBoundary)b;
            return;
        }

        bool dispatchActions = admittedIndices.Count < _pool.LaneCount * 2;
        var initialItems = new List<ForecastWorkItem>(admittedIndices.Count * (dispatchActions ? 4 : 1));
        if (dispatchActions)
        {
            List<PlannedAction> tempActions = AcquireActionScratch();
            try
            {
                for (int idx = 0; idx < admittedIndices.Count; idx++)
                {
                    _ct.ThrowIfCancellationRequested();
                    int p = admittedIndices[idx];
                    SearchNode parent = active[p];
                    ActionGenerator.GenerateForForecastInto(parent.State, _policy, tempActions);
                    PlannedAction[] actions = tempActions.ToArray();
                    parentSlots[p].SetActions(actions);
                    for (int a = 0; a < actions.Length; a++)
                    {
                        initialItems.Add(new ForecastWorkItem
                        {
                            Kind = ForecastWorkItem.KindAction,
                            ParentIndex = p,
                            ActionIndex = a,
                            ChoiceDepth = 0,
                            ParentNode = parent,
                            Action = actions[a],
                            Script = null,
                        });
                    }
                }
            }
            finally
            {
                ReleaseActionScratch(tempActions);
            }
        }
        else
        {
            for (int idx = 0; idx < admittedIndices.Count; idx++)
            {
                int p = admittedIndices[idx];
                initialItems.Add(new ForecastWorkItem
                {
                    Kind = ForecastWorkItem.KindParent,
                    ParentIndex = p,
                    ParentNode = active[p],
                });
            }
        }

        // With a full queue of independent parents, lanes can steal whole actions.
        // Keep each action's choices in DFS order so later scripts never run past its cap.
        bool keepChoicesLocal = _options.UseParentNodeBudget && !dispatchActions;
        _pool.ExecuteGroup(
            initialItems,
            _homeLaneOffset,
            (laneIndex, item) => ProcessWorkItem(laneIndex, item, parentSlots, keepChoicesLocal),
            _ledger,
            _ct,
            maxConcurrency: _maxConcurrency);

        _ct.ThrowIfCancellationRequested();

        int rawBoundary = Volatile.Read(ref _recordedLimitBoundary);
        bool retainedAcceptableVictory = acceptableVictoryFound;

        var commitmentNodes = _policy.Act3BossStrategy && layer == 0 && rawBoundary == 0 && !retainedAcceptableVictory
            ? new List<SearchNode>()
            : null;
        var parentCardChildrenByParent = new List<SearchNode>[active.Count];
        var parentPendingOrIncomplete = new bool[active.Count];
        var parentIncompleteOnly = new bool[active.Count];
        var provenChildren = new List<SearchNode>(Math.Clamp(_budget.MaxCardBranches, 1, 64));

        for (int idx = 0; idx < admittedIndices.Count; idx++)
        {
            int p = admittedIndices[idx];
            ParentSlotState parentSlot = parentSlots[p];
            SearchNode parent = parentSlot.Parent;

            if (Volatile.Read(ref parentSlot.Initialized) == 0)
            {
                parentIncompleteOnly[p] = true;
                continue;
            }

            var parentCardChildren = new List<SearchNode>();
            parentCardChildrenByParent[p] = parentCardChildren;
            bool parentHitPendingChoice = Volatile.Read(ref parentSlot.HitPendingChoice) != 0;
            hitPendingChoiceBoundary |= parentHitPendingChoice;
            bool parentIncomplete = Volatile.Read(ref parentSlot.IncompleteByLimit) != 0;

            for (int a = 0; a < parentSlot.ActionList.Length; a++)
            {
                PlannedAction action = parentSlot.ActionList[a];
                ref ActionSlotState actionSlot = ref parentSlot.Actions[a];
                actionSlot.CollectProvenDfsPrefix(provenChildren, _budget.MaxCardBranches, out bool hitUnresolvedGap);
                if (hitUnresolvedGap)
                    parentIncomplete = true;

                for (int c = 0; c < provenChildren.Count; c++)
                {
                    SearchNode child = provenChildren[c];
                    if (action.Kind == "PlayCard" && child.NodeBoundary == ForecastSearchBoundary.None)
                    {
                        parentCardChildren.Add(child);
                    }
                    else
                    {
                        BeamSearch.ClassifyForecastChild(child, parent, completed, ended, next);
                        _track?.RecordInFlightCandidate(
                            laneIndex: 0,
                            child,
                            child.Ended || child.Turn != parent.Turn || child.NodeBoundary != ForecastSearchBoundary.None);
                        if (!_options.DisableTargetStop
                            && BeamSearch.MeetsAcceptableVictory(child, _policy, _evaluator, _openingPreRootBattleHpLost))
                        {
                            retainedAcceptableVictory = true;
                        }
                    }

                    if (child.Ended || child.Turn != parent.Turn || child.NodeBoundary != ForecastSearchBoundary.None)
                        considerBoundary(child);
                }
            }

            if (commitmentNodes is not null)
            {
                for (int i = 0; i < parentCardChildren.Count; i++)
                {
                    SearchNode cand = parentCardChildren[i];
                    if (cand.NodeBoundary == ForecastSearchBoundary.None
                        && BeamSearch.HasPlayableFetchedPower(parent, cand))
                    {
                        commitmentNodes.Add(cand);
                    }
                }
            }

            parentPendingOrIncomplete[p] = parentHitPendingChoice;
            parentIncompleteOnly[p] = parentIncomplete;
        }

        if (commitmentNodes is { Count: > 0 } && !retainedAcceptableVictory && rawBoundary == 0)
        {
            RunSecondaryCommitmentWave(
                commitmentNodes,
                parentCardChildrenByParent,
                completed,
                ended,
                next,
                truncatedParents,
                ref hitPendingChoiceBoundary,
                ref retainedAcceptableVictory,
                considerBoundary);
        }

        for (int p = 0; p < active.Count; p++)
        {
            List<SearchNode>? cardChildren = parentCardChildrenByParent[p];
            if (cardChildren is null || cardChildren.Count == 0)
                continue;
            SearchNode parent = active[p];
            List<SearchNode> retainedCardChildren = DeduplicateCardChildren(cardChildren, _budget.MaxCardBranches);
            for (int i = 0; i < retainedCardChildren.Count; i++)
            {
                SearchNode child = retainedCardChildren[i];
                _ct.ThrowIfCancellationRequested();
                BeamSearch.ClassifyForecastChild(child, parent, completed, ended, next);
                _track?.RecordInFlightCandidate(
                    laneIndex: 0,
                    child,
                    child.Ended || child.Turn != parent.Turn || child.NodeBoundary != ForecastSearchBoundary.None);
                if (!_options.DisableTargetStop
                    && BeamSearch.MeetsAcceptableVictory(child, _policy, _evaluator, _openingPreRootBattleHpLost))
                {
                    retainedAcceptableVictory = true;
                }
            }
        }

        if (retainedAcceptableVictory)
        {
            acceptableVictoryFound = true;
        }
        else
        {
            Volatile.Write(ref _victoryFoundFlag, 0);
            int latestBoundary = Volatile.Read(ref _recordedLimitBoundary);
            if (latestBoundary != 0 && passBoundary == ForecastSearchBoundary.None)
                passBoundary = (ForecastSearchBoundary)latestBoundary;
        }

        for (int p = 0; p < active.Count; p++)
        {
            if (parentPendingOrIncomplete[p] || (!retainedAcceptableVictory && parentIncompleteOnly[p]))
                truncatedParents.Add(active[p]);
        }
    }

    private void RunSecondaryCommitmentWave(
        List<SearchNode> commitments,
        List<SearchNode>[] parentCardChildrenByParent,
        List<SearchNode> completed,
        List<SearchNode> ended,
        List<SearchNode> next,
        List<SearchNode> extraTruncatedParents,
        ref bool hitPendingChoiceBoundary,
        ref bool retainedAcceptableVictory,
        Action<SearchNode> considerBoundary)
    {
        for (int i = 0; i < commitments.Count; i++)
            commitments[i].State.FreezeForConcurrentReads();

        var commitSlots = new ParentSlotState[commitments.Count];
        var admittedCommitIndices = new List<int>(commitments.Count);
        var initialItems = new List<ForecastWorkItem>(commitments.Count * 4);
        List<PlannedAction> tempActions = AcquireActionScratch();
        try
        {
            for (int c = 0; c < commitments.Count; c++)
            {
                SearchNode commitment = commitments[c];
                if (_admissionTable is not null && !_admissionTable.TryAdmit(commitment))
                {
                    commitSlots[c] = new ParentSlotState(commitment, admitted: false);
                    FindOwningCardList(parentCardChildrenByParent, commitment)?.Remove(commitment);
                    continue;
                }
                if (_options.UseParentNodeBudget)
                {
                    if (_watch.Elapsed >= _budget.TimeLimit)
                    {
                        RecordWorkerLimitBoundary(ForecastSearchBoundary.TimeLimit);
                        for (int rem = c; rem < commitments.Count; rem++)
                            extraTruncatedParents.Add(commitments[rem]);
                        break;
                    }
                    if (Volatile.Read(ref _sharedParents) >= _budget.NodeLimit || !_budgetController.TryReserveOne())
                    {
                        RecordAdmissionNodeLimit();
                        for (int rem = c; rem < commitments.Count; rem++)
                            extraTruncatedParents.Add(commitments[rem]);
                        break;
                    }
                }
                Interlocked.Increment(ref _sharedParents);
                _ledger?.AddExpandedParents(1);
                if (_options.UseParentNodeBudget)
                    _track?.ObserveExpansion(1, commitment.Turn);

                commitSlots[c] = new ParentSlotState(commitment, admitted: true);
                admittedCommitIndices.Add(c);
                ActionGenerator.GenerateForForecastInto(commitment.State, _policy, tempActions);
                PlannedAction[] actions = tempActions.ToArray();
                commitSlots[c].SetActions(actions);
                for (int a = 0; a < actions.Length; a++)
                {
                    initialItems.Add(new ForecastWorkItem
                    {
                        Kind = ForecastWorkItem.KindAction,
                        ParentIndex = c,
                        ActionIndex = a,
                        ChoiceDepth = 0,
                        ParentNode = commitment,
                        Action = actions[a],
                        Script = null,
                    });
                }
            }
        }
        finally
        {
            ReleaseActionScratch(tempActions);
        }

        if (admittedCommitIndices.Count == 0)
            return;

        _pool.ExecuteGroup(
            initialItems,
            _homeLaneOffset,
            (laneIndex, item) => ProcessWorkItem(laneIndex, item, commitSlots),
            _ledger,
            _ct,
            maxConcurrency: _maxConcurrency);

        _ct.ThrowIfCancellationRequested();

        var provenChildren = new List<SearchNode>(Math.Clamp(_budget.MaxCardBranches, 1, 64));
        for (int idx = 0; idx < admittedCommitIndices.Count; idx++)
        {
            int c = admittedCommitIndices[idx];
            ParentSlotState slot = commitSlots[c];
            SearchNode commitment = slot.Parent;
            List<SearchNode>? ownerCardList = FindOwningCardList(parentCardChildrenByParent, commitment);
            bool hitPending = Volatile.Read(ref slot.HitPendingChoice) != 0;
            hitPendingChoiceBoundary |= hitPending;
            bool incomplete = Volatile.Read(ref slot.Initialized) == 0 || Volatile.Read(ref slot.IncompleteByLimit) != 0;

            for (int a = 0; a < slot.ActionList.Length; a++)
            {
                PlannedAction followup = slot.ActionList[a];
                ref ActionSlotState actionSlot = ref slot.Actions[a];
                actionSlot.CollectProvenDfsPrefix(provenChildren, _budget.MaxCardBranches, out bool hitUnresolvedGap);
                if (hitUnresolvedGap)
                    incomplete = true;

                for (int k = 0; k < provenChildren.Count; k++)
                {
                    SearchNode successor = provenChildren[k];
                    if (followup.Kind == "PlayCard" && successor.NodeBoundary == ForecastSearchBoundary.None && ownerCardList is not null)
                    {
                        ownerCardList.Add(successor);
                    }
                    else
                    {
                        BeamSearch.ClassifyForecastChild(successor, commitment, completed, ended, next);
                        _track?.RecordInFlightCandidate(
                            laneIndex: 0,
                            successor,
                            successor.Ended || successor.Turn != commitment.Turn || successor.NodeBoundary != ForecastSearchBoundary.None);
                        if (!_options.DisableTargetStop
                            && BeamSearch.MeetsAcceptableVictory(successor, _policy, _evaluator, _openingPreRootBattleHpLost))
                        {
                            retainedAcceptableVictory = true;
                        }
                    }

                    if (successor.Ended || successor.Turn != commitment.Turn || successor.NodeBoundary != ForecastSearchBoundary.None)
                        considerBoundary(successor);
                }
            }

            if (hitPending || (!retainedAcceptableVictory && incomplete))
                extraTruncatedParents.Add(commitment);
            ownerCardList?.Remove(commitment);
        }
    }

    private static List<SearchNode>? FindOwningCardList(List<SearchNode>[] lists, SearchNode target)
    {
        for (int i = 0; i < lists.Length; i++)
        {
            List<SearchNode>? list = lists[i];
            if (list is not null && list.Contains(target))
                return list;
        }
        return null;
    }

    private void ProcessWorkItem(int laneIndex, ForecastWorkItem item, ParentSlotState[] parentSlots,
        bool keepChoicesLocal = false)
    {
        if (ShouldStopWorkers())
            return;

        ParentSlotState parentSlot = parentSlots[item.ParentIndex];
        if (!parentSlot.Admitted)
            return;

        if (item.Kind == ForecastWorkItem.KindParent)
        {
            List<PlannedAction> actions = AcquireActionScratch();
            PlannedAction[] actionArray;
            try
            {
                ActionGenerator.GenerateForForecastInto(item.ParentNode.State, _policy, actions);
                actionArray = actions.ToArray();
            }
            finally
            {
                ReleaseActionScratch(actions);
            }

            parentSlot.SetActions(actionArray);
            if (actionArray.Length == 0)
                return;

            if (actionArray.Length > 1)
            {
                _pool.ReservePending(item.Group, actionArray.Length - 1);
                for (int actionIdx = actionArray.Length - 1; actionIdx >= 1; actionIdx--)
                {
                    _pool.PushReserved(laneIndex, new ForecastWorkItem
                    {
                        Group = item.Group,
                        Kind = ForecastWorkItem.KindAction,
                        ParentIndex = item.ParentIndex,
                        ActionIndex = actionIdx,
                        ChoiceDepth = 0,
                        ParentNode = item.ParentNode,
                        Action = actionArray[actionIdx],
                        Script = null,
                    }, shareOverflow: keepChoicesLocal);
                }
            }

            ExecuteChoiceStep(
                laneIndex,
                item.ParentIndex,
                parentSlot,
                actionIndex: 0,
                ref parentSlot.Actions[0],
                actionArray[0],
                script: null,
                choiceNode: null,
                choiceDepth: 0,
                item.Group,
                keepChoicesLocal);
            return;
        }

        ExecuteChoiceStep(
            laneIndex,
            item.ParentIndex,
            parentSlot,
            item.ActionIndex,
            ref parentSlot.Actions[item.ActionIndex],
            item.Action!,
            item.Script,
            item.ChoiceNode,
            item.ChoiceDepth,
            item.Group,
            keepChoicesLocal);
    }

    private void ExecuteChoiceStep(
        int laneIndex,
        int parentIndex,
        ParentSlotState parentSlot,
        int actionIndex,
        ref ActionSlotState actionSlot,
        PlannedAction action,
        ChoiceScriptNode? script,
        ChoiceTreeNode? choiceNode,
        int choiceDepth,
        ForecastWorkGroup workGroup,
        bool keepChoicesLocal)
    {
        SearchNode parent = parentSlot.Parent;

        while (true)
        {
            if (_ct.IsCancellationRequested || Volatile.Read(ref _victoryFoundFlag) != 0)
                return;

            if (Volatile.Read(ref _limitReachedFlag) != 0)
            {
                parentSlot.MarkIncomplete();
                return;
            }

            if (actionSlot.HasAtLeastPreceding(script, _budget.MaxCardBranches))
            {
                return;
            }

            if (choiceDepth > 12)
            {
                Volatile.Write(ref parentSlot.HitPendingChoice, 1);
                actionSlot.MarkRejected(choiceNode);
                return;
            }

            _beforeChoiceAttempt?.Invoke(action, script);

            if (!TryReserveAttempt(parent.Turn))
            {
                parentSlot.MarkIncomplete();
                return;
            }

            CombatState child = parent.State.Clone();
            var ledger = new PredictionLedgerSink();
            child.PredictionSink = ledger;
            List<string> observed = AcquireObservedScratch();
            try
            {
                using (CardSelectCmd.IsolateWorkerScope())
                {
                    ActionExecutor.ExecuteSyncCore(child, action, script, observed);
                }

                bool matches = script is null
                    ? observed.Count == 0
                    : script.MatchesObserved(observed);
                if (!matches)
                    throw new InvalidOperationException("Choice replay did not match its parent script.");

                if (!_evaluator.AllowsMaxHpLoss(parent.BrightestFlameMaxHpLost + ledger.BrightestFlameMaxHpLost))
                {
                    actionSlot.MarkRejected(choiceNode);
                    return;
                }

                PlannedAction resolved = ChoiceExpansion.MaterializeResolvedAction(action, script);
                SearchNode childNode = SearchNode.Create(
                    child,
                    parent,
                    resolved,
                    _evaluator,
                    ledger.BrightestFlameMaxHpLost,
                    _evaluator.CaptureGrowth
                        ? GrowthLedger.Delta(parent.State, child, resolved, ledger)
                        : default,
                    ledger.DeathSaveHpRestored,
                    ledger.DeathSaveUseCount,
                    playedCards: ledger.PlayedCards,
                    hasImpureHistory: ledger.PotionsProcured > 0);
                childNode.PrecomputeRetentionKey();

                Interlocked.Increment(ref _sharedTransitions);
                _ledger?.AddTransitions(1);
                actionSlot.MarkChild(choiceNode, childNode, script);

                if (IsGuaranteedRetainedChild(parentSlot, action, script, childNode))
                {
                    bool isBoundaryOrEnded = childNode.Ended
                        || childNode.Turn != parent.Turn
                        || childNode.NodeBoundary != ForecastSearchBoundary.None;
                    _track?.RecordInFlightCandidate(laneIndex, childNode, isBoundaryOrEnded);

                    if (!_options.DisableTargetStop
                        && BeamSearch.MeetsAcceptableVictory(
                            childNode,
                            _policy,
                            _evaluator,
                            _openingPreRootBattleHpLost))
                    {
                        Interlocked.Exchange(ref _victoryFoundFlag, 1);
                    }
                }
                return;
            }
            catch (SolverSelectionRequiredException pending)
            {
                // fail-open: 选卡悬挂通过回放并分发分支组合解决。
                if (actionSlot.HasAtLeastPreceding(script, _budget.MaxCardBranches))
                {
                    return;
                }

                CardSelectionRequest request = pending.Request;
                int limit = ChoiceExpansion.IsHandChoice(request)
                    ? _budget.MaxHandChoiceBranches
                    : _budget.MaxPileChoiceBranches;
                List<(int[] Indices, string[] CardIds)> selections = ChoiceExpansion.AcquireSelectionScratch();
                try
                {
                    ChoiceExpansion.ExtractSelectionsInto(request, limit, selections);
                    if (selections.Count == 0)
                    {
                        Volatile.Write(ref parentSlot.HitPendingChoice, 1);
                        actionSlot.MarkRejected(choiceNode);
                        return;
                    }

                    ChoiceTreeNode[] branches = actionSlot.ExpandChoice(choiceNode, selections.Count);
                    if (keepChoicesLocal && selections.Count > 1)
                    {
                        for (int ordinal = 0; ordinal < selections.Count; ordinal++)
                        {
                            if (actionSlot.CompletedChoiceCount >= _budget.MaxCardBranches
                                || ShouldStopWorkers() || Volatile.Read(ref workGroup.AbortRequested) != 0)
                                break;
                            (int[] indices, string[] ids) = selections[ordinal];
                            ExecuteChoiceStep(laneIndex, parentIndex, parentSlot, actionIndex, ref actionSlot,
                                action, new ChoiceScriptNode(script, indices, ids, ordinal), branches[ordinal],
                                choiceDepth + 1, workGroup, keepChoicesLocal);
                        }
                        return;
                    }

                    if (selections.Count > 1 && !ShouldStopWorkers())
                    {
                        _pool.ReservePending(workGroup, selections.Count - 1);
                        for (int choiceOrdinal = selections.Count - 1; choiceOrdinal >= 1; choiceOrdinal--)
                        {
                            (int[] indices, string[] ids) = selections[choiceOrdinal];
                            _pool.PushReserved(laneIndex, new ForecastWorkItem
                            {
                                Group = workGroup,
                                Kind = ForecastWorkItem.KindChoice,
                                ParentIndex = parentIndex,
                                ActionIndex = actionIndex,
                                ChoiceDepth = choiceDepth + 1,
                                ParentNode = parent,
                                Action = action,
                                Script = new ChoiceScriptNode(script, indices, ids, choiceOrdinal),
                                ChoiceNode = branches[choiceOrdinal],
                            });
                        }
                    }

                    (int[] firstIndices, string[] firstIds) = selections[0];
                    script = new ChoiceScriptNode(script, firstIndices, firstIds, branchOrdinal: 0);
                    choiceNode = branches[0];
                    choiceDepth++;
                }
                finally
                {
                    ChoiceExpansion.ReleaseSelectionScratch(selections);
                }
            }
            catch (CandidateRejectedException)
            {
                // fail-open: 明确拒绝的动作仅丢弃本分支。
                actionSlot.MarkRejected(choiceNode);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (!error.Data.Contains("Beam.Action"))
                {
                    error.Data["Beam.Action"] = action;
                    error.Data["Beam.ParentFingerprint"] = parent.Fingerprint;
                    error.Data["Beam.ParentDepth"] = parent.Depth;
                    error.Data["Beam.ParentTurn"] = parent.Turn;
                    error.Data["Beam.ChoiceScript"] = script?.MaterializeIndices() ?? Array.Empty<IReadOnlyList<int>>();
                }
                throw;
            }
            finally
            {
                ReleaseObservedScratch(observed);
            }
        }
    }

    private bool ShouldStopWorkers() =>
        _ct.IsCancellationRequested
        || Volatile.Read(ref _victoryFoundFlag) != 0
        || Volatile.Read(ref _limitReachedFlag) != 0;

    private bool TryReserveAttempt(int turn)
    {
        if (_watch.Elapsed >= _budget.TimeLimit)
        {
            RecordWorkerLimitBoundary(ForecastSearchBoundary.TimeLimit);
            return false;
        }

        if (!_options.UseParentNodeBudget)
        {
            if (!_budgetController.TryReserveOne()
                || (_nodeBudget.NodeLimit == _budget.NodeLimit && !_nodeBudget.TryReserveSlot()))
            {
                RecordWorkerLimitBoundary(ForecastSearchBoundary.NodeLimit);
                return false;
            }
            Interlocked.Increment(ref _sharedAttempts);
            _ledger?.AddChoiceReplayAttempts(1);
            _track?.ObserveExpansion(1, turn);
            return true;
        }

        Interlocked.Increment(ref _sharedAttempts);
        _ledger?.AddChoiceReplayAttempts(1);
        return true;
    }

    private void RecordAdmissionNodeLimit() =>
        Interlocked.CompareExchange(ref _recordedLimitBoundary, (int)ForecastSearchBoundary.NodeLimit, 0);

    private void RecordWorkerLimitBoundary(ForecastSearchBoundary boundary)
    {
        if (boundary != ForecastSearchBoundary.None)
        {
            Interlocked.CompareExchange(ref _recordedLimitBoundary, (int)boundary, 0);
            Interlocked.Exchange(ref _limitReachedFlag, 1);
        }
    }

    private bool IsGuaranteedRetainedChild(
        ParentSlotState parentSlot,
        PlannedAction action,
        ChoiceScriptNode? script,
        SearchNode childNode) =>
        _budget.MaxCardBranches > 0
        && IsGuaranteedWithinBranchCap(script)
        && (action.Kind != "PlayCard"
            || childNode.NodeBoundary != ForecastSearchBoundary.None
            || (parentSlot.PlayCardActionCount == 1 && script is null));

    private static bool IsGuaranteedWithinBranchCap(ChoiceScriptNode? script)
    {
        for (ChoiceScriptNode? current = script; current is not null; current = current.Parent)
        {
            if (current.BranchOrdinal != 0)
                return false;
        }
        return true;
    }

    private static int CompareDfsPaths(int[] pa, int[] pb)
    {
        int len = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0)
                return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    internal static List<SearchNode> DeduplicateCardChildren(
        IReadOnlyList<SearchNode> cardChildren,
        int maxCardBranches)
    {
        var result = new List<SearchNode>(Math.Min(cardChildren.Count, maxCardBranches));
        if (cardChildren.Count == 0 || maxCardBranches <= 0)
            return result;
        if (cardChildren.Count == 1)
        {
            result.Add(cardChildren[0]);
            return result;
        }

        List<(SearchNode Node, CombatStateDescriptionDigest Key, int Index)> ordered = _dedupeCardScratch ?? new List<(SearchNode, CombatStateDescriptionDigest, int)>(32);
        HashSet<CombatStateDescriptionDigest> seenExact = _dedupeExactScratch ?? new HashSet<CombatStateDescriptionDigest>(32);
        HashSet<CombatStateDescriptionDigest> representedBuckets = _dedupeBucketScratch ?? new HashSet<CombatStateDescriptionDigest>(32);
        _dedupeCardScratch = null;
        _dedupeExactScratch = null;
        _dedupeBucketScratch = null;
        try
        {
            for (int i = 0; i < cardChildren.Count; i++)
            {
                SearchNode node = cardChildren[i];
                ordered.Add((node, ForecastRetentionKey.Compute(node), i));
            }
            ordered.Sort(DedupeComparer.Instance);

            for (int i = 0; i < ordered.Count; i++)
            {
                (SearchNode node, CombatStateDescriptionDigest key, _) = ordered[i];
                if (seenExact.Contains(node.Fingerprint) || !representedBuckets.Add(key))
                    continue;
                seenExact.Add(node.Fingerprint);
                result.Add(node);
                if (result.Count >= maxCardBranches)
                    return result;
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                SearchNode node = ordered[i].Node;
                if (!seenExact.Add(node.Fingerprint))
                    continue;
                result.Add(node);
                if (result.Count >= maxCardBranches)
                    break;
            }

            return result;
        }
        finally
        {
            ordered.Clear();
            seenExact.Clear();
            representedBuckets.Clear();
            _dedupeCardScratch = ordered;
            _dedupeExactScratch = seenExact;
            _dedupeBucketScratch = representedBuckets;
        }
    }

    private sealed class DedupeComparer : IComparer<(SearchNode Node, CombatStateDescriptionDigest Key, int Index)>
    {
        public static readonly DedupeComparer Instance = new();

        public int Compare(
            (SearchNode Node, CombatStateDescriptionDigest Key, int Index) x,
            (SearchNode Node, CombatStateDescriptionDigest Key, int Index) y)
        {
            int c = y.Node.Score.CompareTo(x.Node.Score);
            if (c != 0) return c;
            c = x.Node.Depth.CompareTo(y.Node.Depth);
            if (c != 0) return c;
            c = x.Key.First.CompareTo(y.Key.First);
            if (c != 0) return c;
            c = x.Key.Second.CompareTo(y.Key.Second);
            if (c != 0) return c;
            c = x.Node.Fingerprint.First.CompareTo(y.Node.Fingerprint.First);
            if (c != 0) return c;
            c = x.Node.Fingerprint.Second.CompareTo(y.Node.Fingerprint.Second);
            if (c != 0) return c;
            return x.Index.CompareTo(y.Index);
        }
    }

    private static List<PlannedAction> AcquireActionScratch()
    {
        List<PlannedAction>? list = _actionListScratch;
        _actionListScratch = null;
        return list ?? new List<PlannedAction>(16);
    }

    private static void ReleaseActionScratch(List<PlannedAction> list)
    {
        list.Clear();
        _actionListScratch = list;
    }

    private static List<string> AcquireObservedScratch()
    {
        List<string>? list = _observedCardIdScratch;
        _observedCardIdScratch = null;
        return list ?? new List<string>(8);
    }

    private static void ReleaseObservedScratch(List<string> list)
    {
        list.Clear();
        _observedCardIdScratch = list;
    }
}
