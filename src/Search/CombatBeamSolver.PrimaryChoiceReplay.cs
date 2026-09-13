namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct PreparedChoiceEvaluation(
        ExpansionBatch? Batch,
        PrimaryChoiceReplayFrontier? Frontier);

    private static bool CanReservePrimaryReplayPrefix(int branches, int finals, int attempts)
        => branches >= 2 && finals >= branches && attempts >= branches;

    private static PrimaryChoiceReplayFrontier? PreparePrimaryChoiceReplays(
        PrimaryCardChoiceLayer layer,
        PreparedCardAction? card = null,
        PreparedPotionAction? potion = null)
    {
        ChoiceSearchBudget budget = layer.WholeActionBudget.SemanticSearchBudget;
        if (!CanReservePrimaryReplayPrefix(
                layer.SemanticBranchCount, budget.ActiveFinalQuota, budget.ReplayAttemptQuota)
            || layer.Choices.Take(layer.SemanticBranchCount).Any(choice => choice == null))
        {
            return null;
        }
        return new PrimaryChoiceReplayFrontier(layer, card, potion);
    }

    /// <summary>
    /// Owns only the mandatory first replay of each semantic sibling. If F,R >= N, the original
    /// ceil(F/N),ceil(R/N) child lease leaves F',R' >= N-1 even when fully spent. Inductively every
    /// sibling must execute once; precomputing these snapshots adds no replay or choice branch.
    /// Quotas, nested traversal, and occurrence collection still have one ordered consumer.
    /// </summary>
    private sealed class PrimaryChoiceReplayFrontier : IDisposable
    {
        private readonly SimulationSnapshot?[] _snapshots;
        private readonly bool[] _received;
        private int _consumed;
        private readonly PrimaryCardChoiceLayer? _layer;
        public PrimaryCardChoiceLayer Layer => _layer
            ?? throw new InvalidOperationException("回合尾部没有卡牌主选择层。");
        public EndTurnChoiceLayer? EndTurn { get; }
        public bool IsEndTurn => EndTurn != null;
        public PreparedCardAction? Card { get; }
        public PreparedPotionAction? Potion { get; }
        public bool IsPotion => Potion.HasValue;
        public PlanAction[] Actions { get; }
        // Dispatch/receive fields belong to the coordinator. After all replays have completed,
        // one continuation lane exclusively takes snapshots; no producer can then still write.
        public int NextReplay { get; private set; }
        public int CompletedReplays { get; private set; }
        public bool ContinuationDispatched { get; private set; }
        public bool CanDispatchReplay => NextReplay < Actions.Length;
        public bool CanDispatchContinuation => CompletedReplays == Actions.Length
            && !ContinuationDispatched;

        public PrimaryChoiceReplayFrontier(
            PrimaryCardChoiceLayer layer,
            PreparedCardAction? card,
            PreparedPotionAction? potion)
        {
            if (card.HasValue == potion.HasValue)
                throw new ArgumentException("选择回放 frontier 必须有且只有一个动作所有者。");
            _layer = layer;
            Card = card;
            Potion = potion;
            PlanAction action = card?.Action ?? potion!.Value.Action;
            Actions = new PlanAction[layer.SemanticBranchCount];
            for (int index = 0; index < Actions.Length; index++)
                Actions[index] = action with { Choice = layer.Choices[index] };
            _snapshots = new SimulationSnapshot?[Actions.Length];
            _received = new bool[Actions.Length];
        }

        public PrimaryChoiceReplayFrontier(EndTurnChoiceLayer endTurn)
        {
            EndTurn = endTurn;
            Actions = endTurn.Layer.Branches.Select(branch => branch.Action).ToArray();
            _snapshots = new SimulationSnapshot?[Actions.Length];
            _received = new bool[Actions.Length];
        }

        public void MarkReplayDispatched(int index, int count)
        {
            if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)
                throw new InvalidOperationException("首层选择回放派发顺序错误。");
            NextReplay += count;
        }

        public void Receive(int index, SimulationSnapshot? snapshot)
        {
            if (_received[index] || index >= NextReplay || ContinuationDispatched)
                throw new InvalidOperationException("首层选择回放重复返回或越过所有权边界。");
            _snapshots[index] = snapshot;
            _received[index] = true;
            CompletedReplays++;
        }

        public void MarkContinuationDispatched()
        {
            if (!CanDispatchContinuation)
                throw new InvalidOperationException("首层回放未排空就启动了选择续接。");
            ContinuationDispatched = true;
        }

        public SimulationSnapshot? Take(int index, ChoiceSearchBudget budget)
        {
            if (!ContinuationDispatched || !_received[index] || index != _consumed)
                throw new InvalidOperationException("首层回放快照没有保持原选择顺序。");
            // Physical attempts were counted on their replay lanes. Only the original logical
            // lease is charged here, at exactly the same traversal position as serial search.
            if (!budget.TrySpendReplayAttempt())
                throw new InvalidOperationException("已保证准入的首层回放没有获得原选择额度。");
            SimulationSnapshot? snapshot = _snapshots[index];
            _snapshots[index] = null;
            _received[index] = false;
            _consumed++;
            return snapshot; // null is the original explicit InvalidPlannedChoiceBranch outcome.
        }

        public void AssertConsumed()
        {
            if (_consumed != Actions.Length)
                throw new InvalidOperationException("已保证准入的首层回放未全部按原序消费。");
        }

        public void Dispose()
        {
            for (int index = 0; index < _snapshots.Length; index++)
            {
                _snapshots[index]?.ReleaseSimulator();
                _snapshots[index] = null;
            }
        }
    }

    private SimulationSnapshot? ReplayPrimaryChoice(
        SearchNode parent,
        PlanAction action,
        object forkGate,
        bool pruneInvalidBranch = true)
    {
        if (_parallelActionReplayForkGate != null)
            throw new InvalidOperationException("不能嵌套首层选择回放的 Fork 上下文。");
        _parallelActionReplayForkGate = forkGate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _run.WorkPacer.YieldIfNeeded();
            _run.ChoiceReplayAttempts++;
            _run.ChoiceBranchesEvaluated++;
            SimulationSnapshot? snapshot = ReplayPendingChoiceBranch(parent,
                new PendingChoiceReplayBranch(action, pruneInvalidBranch));
            try
            {
                if (action.Kind == PlanActionKind.EndTurn)
                    ObserveSearchPath(parent, SearchPathObservationStage.EndTurnChoiceReplay,
                        "mandatory_end_turn_choice_replayed");
                return snapshot;
            }
            catch
            {
                snapshot?.ReleaseSimulator();
                throw;
            }
        }
        finally
        {
            _parallelActionReplayForkGate = null;
        }
    }

    private ExpansionBatch CompletePrimaryChoices(
        SearchNode parent,
        PrimaryChoiceReplayFrontier frontier,
        object forkGate)
    {
        if (_parallelActionReplayForkGate != null)
            throw new InvalidOperationException("不能嵌套选择续接的 Fork 上下文。");
        ExpansionBatch batch = RentExpansionBatch();
        bool completed = false;
        _parallelActionReplayForkGate = forkGate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frontier.Potion is { } potion)
            {
                AddResolvedPotionCandidates(parent,
                    ResolveExplicitCardChoiceBranches(
                        parent, potion.Action, probeSnapshot: null,
                        frontier.Layer.Choices, choiceSpec: null, replayedChoices: frontier),
                    batch);
            }
            else
            {
                PreparedCardAction card = frontier.Card!.Value;
                AddResolvedCardCandidates(parent, card,
                    ResolvePrimaryCardChoiceLayer(
                        parent, card.Action, probeSnapshot: null, frontier.Layer, frontier),
                    batch);
            }
            frontier.AssertConsumed();
            completed = true;
            return batch;
        }
        finally
        {
            _parallelActionReplayForkGate = null;
            if (!completed)
                batch.Dispose();
        }
    }
}
