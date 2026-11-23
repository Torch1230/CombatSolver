using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

/// <summary>Player-turn layered beam search over exact Core clones. Method: CombatBeamSolver.Phases.cs SolveCore @ d231e9e5.</summary>
public static partial class BeamSearch
{
    public static BeamResult Solve(CombatState root, BeamBudget budget, CancellationToken ct) =>
        Solve(root, budget, CombatPolicy.Default, ct);

    public static BeamResult Solve(CombatState root, BeamBudget budget, CombatPolicy policy, CancellationToken ct) =>
        SolveAsync(root, policy.ResolveBudget(budget), policy, ct).GetAwaiter().GetResult();

    private static async Task<BeamResult> SolveAsync(CombatState root, BeamBudget budget, CombatPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (budget.BeamWidth < 1 || budget.NodeLimit < 1 || budget.MaxCardBranches < 1
            || budget.MaxPileChoiceBranches < 1 || budget.MaxHandChoiceBranches < 1
            || budget.PotionFreeBeamWidth is < 1 || budget.PotionBeamWidth is < 1
            || budget.TimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));
        int initialHp = root.Players[0].Creature.CurrentHp;
        var evaluator = new StateEvaluator(root, policy);
        SearchNode start = SearchNode.Create(root.Clone(), null, null, evaluator);
        var frontier = new List<SearchNode> { start };
        var completed = new List<SearchNode>();
        var lastActive = new List<SearchNode>();
        var watch = Stopwatch.StartNew();
        long expanded = 0;
        bool truncated = false;
        bool acceptableVictoryFound = false;
        SearchNode? potionFreeBoundaryFallback = null;
        SearchNode? potionBoundaryFallback = null;

        // Each outer pass starts at a player-turn boundary. The inner passes
        // resolve playable actions; end-turn children form the next boundary.
        for (int layer = 0; layer < 100 && frontier.Count > 0; layer++)
        {
            List<SearchNode> active = frontier;
            var ended = new List<SearchNode>();
            for (int depth = 0; depth < 100 && active.Count > 0 && !acceptableVictoryFound; depth++)
            {
                var next = new List<SearchNode>();
                foreach (SearchNode parent in active)
                {
                    ct.ThrowIfCancellationRequested();
                    if (expanded >= budget.NodeLimit || watch.Elapsed >= budget.TimeLimit)
                    {
                        truncated = true;
                        break;
                    }
                    var cardChildren = new List<SearchNode>();
                    foreach (PlannedAction action in ActionGenerator.Generate(parent.State, policy))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (expanded >= budget.NodeLimit || watch.Elapsed >= budget.TimeLimit)
                        {
                            truncated = true;
                            break;
                        }
                        ChoiceExpansion.Expansion expansion = await ChoiceExpansion.ExpandAsync(
                            parent, action, budget, evaluator,
                            (int)(budget.NodeLimit - expanded), watch, ct);
                        expanded += expansion.Attempts;
                        if (expansion.BudgetTruncated) truncated = true;
                        foreach (SearchNode child in expansion.Children)
                        {
                            if (action.Kind == "PlayCard") cardChildren.Add(child);
                            else Classify(child, parent, completed, ended, next);
                            ConsiderBoundary(child, parent);
                            if (MeetsAcceptableVictory(child)) acceptableVictoryFound = true;
                        }
                        if (truncated || acceptableVictoryFound) break;
                    }
                    if (policy.Act3BossStrategy && layer == 0 && !truncated
                        && !acceptableVictoryFound)
                    {
                        // A fetched payable Power needs its next decision settled before
                        // pruning the intermediate hand state (upstream Phases.cs).
                        foreach (SearchNode commitment in cardChildren
                            .Where(HasPlayableFetchedPower).ToArray())
                        {
                            if (expanded >= budget.NodeLimit || watch.Elapsed >= budget.TimeLimit)
                            {
                                truncated = true;
                                break;
                            }
                            foreach (PlannedAction followup in ActionGenerator.Generate(
                                commitment.State, policy))
                            {
                                if (expanded >= budget.NodeLimit || watch.Elapsed >= budget.TimeLimit)
                                {
                                    truncated = true;
                                    break;
                                }
                                ChoiceExpansion.Expansion expansion = await ChoiceExpansion.ExpandAsync(
                                    commitment, followup, budget, evaluator,
                                    (int)(budget.NodeLimit - expanded), watch, ct);
                                expanded += expansion.Attempts;
                                if (expansion.BudgetTruncated) truncated = true;
                                foreach (SearchNode successor in expansion.Children)
                                {
                                    if (followup.Kind == "PlayCard") cardChildren.Add(successor);
                                    else Classify(successor, commitment, completed, ended, next);
                                    ConsiderBoundary(successor, commitment);
                                    if (MeetsAcceptableVictory(successor)) acceptableVictoryFound = true;
                                }
                                if (truncated || acceptableVictoryFound) break;
                            }
                            cardChildren.Remove(commitment);
                            if (truncated || acceptableVictoryFound) break;
                        }
                    }
                    foreach (SearchNode child in cardChildren.OrderByDescending(node => node.Score)
                        .Take(budget.MaxCardBranches))
                        Classify(child, parent, completed, ended, next);
                    if (cardChildren.Any(MeetsAcceptableVictory)) acceptableVictoryFound = true;
                    if (acceptableVictoryFound) break;
                }
                lastActive = SelectFrontier(next, budget);
                if (truncated || acceptableVictoryFound) break;
                active = lastActive;
            }
            frontier = SelectFrontier(ended, budget);
            if (truncated || acceptableVictoryFound) break;
        }
        if (!acceptableVictoryFound && (frontier.Count > 0 || lastActive.Count > 0))
            truncated = true;
        IEnumerable<SearchNode> candidates = completed.Concat(frontier).Concat(lastActive)
            .DefaultIfEmpty(start);
        if (potionFreeBoundaryFallback is not null)
            candidates = candidates.Append(potionFreeBoundaryFallback);
        if (potionBoundaryFallback is not null)
            candidates = candidates.Append(potionBoundaryFallback);
        if (acceptableVictoryFound)
            candidates = candidates.Where(MeetsAcceptableVictory);
        SearchNode selected = PlanSelector.Select(candidates, initialHp, evaluator);
        if (!selected.Ended) truncated = true;
        return new BeamResult(selected.Route(), selected.Won,
            Math.Max(0, initialHp - selected.State.Players[0].Creature.CurrentHp),
            expanded, truncated);

        bool MeetsAcceptableVictory(SearchNode node) => policy.StopAtAcceptableBattleHpLoss
            && node.Won && evaluator.SatisfiesPotionPolicy(node)
            && evaluator.SatisfiesTheftPolicy(node)
            && evaluator.SatisfiesRelicTargets(node)
            && evaluator.SatisfiesGrowthTargets(node)
            && evaluator.BattleHpLost(node) <= policy.AcceptableBattleHpLoss;

        bool HasPlayableFetchedPower(SearchNode node)
        {
            if (node.Action?.ChoiceCardIds.Count is not > 0) return false;
            var beforeHand = node.Parent?.State.Players[0].PlayerCombatState?.Hand.Cards;
            foreach (var card in node.State.Players[0].PlayerCombatState!.Hand.Cards)
            {
                if (card.Type != CardType.Power || !card.CanPlay(out _)
                    || !node.Action.ChoiceCardIds.Contains(card.Id.Entry)) continue;
                int beforeCount = beforeHand?.Count(other => other.Id.Entry == card.Id.Entry) ?? 0;
                int afterCount = node.State.Players[0].PlayerCombatState.Hand.Cards
                    .Count(other => other.Id.Entry == card.Id.Entry);
                if (afterCount > beforeCount) return true;
            }
            return false;
        }

        void ConsiderBoundary(SearchNode child, SearchNode parent)
        {
            if (!child.Ended && child.Turn == parent.Turn) return;
            if (child.PotionUses == 0)
            {
                if (potionFreeBoundaryFallback is null
                    || child.Score > potionFreeBoundaryFallback.Score)
                    potionFreeBoundaryFallback = child;
            }
            else if (potionBoundaryFallback is null || child.Score > potionBoundaryFallback.Score)
            {
                potionBoundaryFallback = child;
            }
        }
    }

    private static List<SearchNode> SelectFrontier(IEnumerable<SearchNode> candidates,
        BeamBudget budget)
    {
        if (budget.PotionFreeBeamWidth is null && budget.PotionBeamWidth is null)
            return Retention.Select(candidates, budget.BeamWidth);
        SearchNode[] pool = candidates.ToArray();
        int freeWidth = budget.PotionFreeBeamWidth ?? budget.BeamWidth;
        int potionWidth = budget.PotionBeamWidth ?? budget.BeamWidth;
        return Retention.Select(pool.Where(node => node.PotionUses == 0), freeWidth)
            .Concat(Retention.Select(pool.Where(node => node.PotionUses > 0), potionWidth))
            .ToList();
    }

    private static void Classify(SearchNode child, SearchNode parent,
        List<SearchNode> completed, List<SearchNode> ended, List<SearchNode> next)
    {
        if (child.Ended) completed.Add(child);
        else if (child.Turn != parent.Turn) ended.Add(child);
        else next.Add(child);
    }

    public static async Task<BeamReplayResult> ReplayAsync(CombatState state,
        IReadOnlyList<PlannedAction> actions, PolicyTargetTracker? targets = null,
        Action<CombatState, int>? afterAction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        int initialHp = state.Players[0].Creature.CurrentHp;
        for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlannedAction action = actions[actionIndex];
            if (state.Engine is not { IsInProgress: true })
                throw new InvalidOperationException("Route contains actions after combat ended.");
            targets?.BeginAction(state);
            IReadOnlyList<string> observed = await ActionExecutor.ExecuteAsync(state, action,
                action.ChoiceIndices);
            if (!observed.SequenceEqual(action.ChoiceCardIds, StringComparer.Ordinal))
                throw new InvalidOperationException("Route card choices differ from search prediction.");
            targets?.EndAction(state, action);
            afterAction?.Invoke(state, actionIndex);
        }
        return new BeamReplayResult(state.Engine?.Won == true,
            Math.Max(0, initialHp - state.Players[0].Creature.CurrentHp),
            state.Engine?.IsInProgress == false);
    }
}
