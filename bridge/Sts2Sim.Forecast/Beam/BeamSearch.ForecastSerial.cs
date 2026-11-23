using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

public static partial class BeamSearch
{
    private static ParentExpansionResult ExpandForecastParentSync(
        SearchNode parent,
        int layer,
        BeamBudget budget,
        CombatPolicy policy,
        StateEvaluator evaluator,
        BeamPassOptions options,
        MemberBudgetController budgetController,
        ParentAdmissionTable? admissionTable,
        ForecastNodeBudgetTracker nodeBudget,
        Stopwatch watch,
        Func<long> readSharedExpanded,
        Action<int> onParentAdmitted,
        Action onAttempt,
        Action onTransition,
        CancellationToken ct)
    {
        var cardChildren = new List<SearchNode>();
        var completed = new List<SearchNode>();
        var ended = new List<SearchNode>();
        var next = new List<SearchNode>();
        var extraTruncatedParents = new List<SearchNode>();
        bool parentHitPendingChoice = false;
        bool anyHitPendingChoice = false;
        bool acceptableVictoryFound = false;
        ForecastSearchBoundary limitBoundary = ForecastSearchBoundary.None;
        SearchNode? potionFreeBoundaryFallback = null;
        SearchNode? potionBoundaryFallback = null;

        bool CheckControllerLimit()
        {
            if (budgetController.Spent < budgetController.MemberCap)
                return false;
            if (limitBoundary == ForecastSearchBoundary.None)
                limitBoundary = ForecastSearchBoundary.NodeLimit;
            return true;
        }
        bool AssignTimeLimit()
        {
            limitBoundary = ForecastSearchBoundary.TimeLimit;
            return true;
        }
        bool CheckLocalLimit() => options.UseParentNodeBudget
            ? (watch.Elapsed >= budget.TimeLimit && AssignTimeLimit())
            : (CheckLimitReached(readSharedExpanded(), budget, watch, ref limitBoundary) || CheckControllerLimit());
        bool TryReserveAttempt()
        {
            if (watch.Elapsed >= budget.TimeLimit)
            {
                limitBoundary = ForecastSearchBoundary.TimeLimit;
                return false;
            }
            if (options.UseParentNodeBudget)
                return true;
            if (!budgetController.TryReserveOne()
                || (nodeBudget.NodeLimit == budget.NodeLimit && !nodeBudget.TryReserveSlot()))
            {
                limitBoundary = ForecastSearchBoundary.NodeLimit;
                return false;
            }
            return true;
        }
        bool MeetsLocalVictory(SearchNode node) =>
            !options.DisableTargetStop
            && MeetsAcceptableVictory(node, policy, evaluator, options.OpeningPreRootBattleHpLost);

        void ConsiderLocalBoundary(SearchNode child, SearchNode sourceParent)
        {
            if (!child.Ended && child.Turn == sourceParent.Turn && child.NodeBoundary == ForecastSearchBoundary.None) return;
            if (child.PotionUses == 0)
            {
                if (potionFreeBoundaryFallback is null || child.Score > potionFreeBoundaryFallback.Score)
                    potionFreeBoundaryFallback = child;
            }
            else if (potionBoundaryFallback is null || child.Score > potionBoundaryFallback.Score)
            {
                potionBoundaryFallback = child;
            }
        }

        List<PlannedAction> parentActions = _serialActionScratch ?? new List<PlannedAction>(16);
        _serialActionScratch = null;
        PlannedAction[] actionSnapshot;
        try
        {
            ActionGenerator.GenerateForForecastInto(parent.State, policy, parentActions);
            actionSnapshot = parentActions.ToArray();
        }
        finally
        {
            parentActions.Clear();
            _serialActionScratch = parentActions;
        }

        for (int a = 0; a < actionSnapshot.Length; a++)
        {
            PlannedAction action = actionSnapshot[a];
            ct.ThrowIfCancellationRequested();
            if (CheckLocalLimit())
                break;

            ChoiceExpansion.Expansion expansion = ChoiceExpansion.ExpandSync(
                parent, action, budget, evaluator, TryReserveAttempt, watch, ct, onAttempt, onTransition);
            if (expansion.HitPendingChoiceBoundary)
            {
                anyHitPendingChoice = true;
                parentHitPendingChoice = true;
            }
            if (expansion.BudgetTruncated && limitBoundary == ForecastSearchBoundary.None)
            {
                limitBoundary = watch.Elapsed >= budget.TimeLimit
                    ? ForecastSearchBoundary.TimeLimit
                    : ForecastSearchBoundary.NodeLimit;
            }

            for (int c = 0; c < expansion.Children.Count; c++)
            {
                SearchNode child = expansion.Children[c];
                ct.ThrowIfCancellationRequested();
                if (action.Kind == "PlayCard" && child.NodeBoundary == ForecastSearchBoundary.None)
                {
                    cardChildren.Add(child);
                }
                else
                {
                    ClassifyForecastChild(child, parent, completed, ended, next);
                    if (MeetsLocalVictory(child)) acceptableVictoryFound = true;
                }
                ConsiderLocalBoundary(child, parent);
            }
            if (limitBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
        }

        if (policy.Act3BossStrategy && layer == 0 && limitBoundary == ForecastSearchBoundary.None
            && !acceptableVictoryFound)
        {
            List<SearchNode>? commitments = null;
            for (int i = 0; i < cardChildren.Count; i++)
            {
                SearchNode node = cardChildren[i];
                if (node.NodeBoundary == ForecastSearchBoundary.None && HasPlayableFetchedPower(parent, node))
                {
                    commitments ??= [];
                    commitments.Add(node);
                }
            }

            if (commitments is not null)
            {
                for (int c = 0; c < commitments.Count; c++)
                {
                    SearchNode commitment = commitments[c];
                    ct.ThrowIfCancellationRequested();
                    if (admissionTable is not null && !admissionTable.TryAdmit(commitment))
                    {
                        cardChildren.Remove(commitment);
                        continue;
                    }
                    if (options.UseParentNodeBudget)
                    {
                        if (watch.Elapsed >= budget.TimeLimit)
                        {
                            limitBoundary = ForecastSearchBoundary.TimeLimit;
                            for (int rem = c; rem < commitments.Count; rem++)
                                extraTruncatedParents.Add(commitments[rem]);
                            break;
                        }
                        if (readSharedExpanded() >= budget.NodeLimit || !budgetController.TryReserveOne())
                        {
                            limitBoundary = ForecastSearchBoundary.NodeLimit;
                            for (int rem = c; rem < commitments.Count; rem++)
                                extraTruncatedParents.Add(commitments[rem]);
                            break;
                        }
                    }
                    else if (CheckLocalLimit())
                    {
                        for (int rem = c; rem < commitments.Count; rem++)
                            extraTruncatedParents.Add(commitments[rem]);
                        break;
                    }

                    onParentAdmitted(commitment.Turn);
                    bool commitmentHitPendingChoice = false;
                    bool commitmentIncomplete = false;
                    PlannedAction[] followups = ActionGenerator.GenerateForForecast(commitment.State, policy).ToArray();
                    for (int f = 0; f < followups.Length; f++)
                    {
                        PlannedAction followup = followups[f];
                        ct.ThrowIfCancellationRequested();
                        if (CheckLocalLimit())
                        {
                            commitmentIncomplete = true;
                            break;
                        }

                        ChoiceExpansion.Expansion expansion = ChoiceExpansion.ExpandSync(
                            commitment, followup, budget, evaluator, TryReserveAttempt, watch, ct, onAttempt, onTransition);
                        if (expansion.HitPendingChoiceBoundary)
                        {
                            anyHitPendingChoice = true;
                            commitmentHitPendingChoice = true;
                        }
                        if (expansion.BudgetTruncated && limitBoundary == ForecastSearchBoundary.None)
                        {
                            limitBoundary = watch.Elapsed >= budget.TimeLimit
                                ? ForecastSearchBoundary.TimeLimit
                                : ForecastSearchBoundary.NodeLimit;
                        }

                        for (int s = 0; s < expansion.Children.Count; s++)
                        {
                            SearchNode successor = expansion.Children[s];
                            ct.ThrowIfCancellationRequested();
                            if (followup.Kind == "PlayCard" && successor.NodeBoundary == ForecastSearchBoundary.None)
                            {
                                cardChildren.Add(successor);
                            }
                            else
                            {
                                ClassifyForecastChild(successor, commitment, completed, ended, next);
                                if (MeetsLocalVictory(successor)) acceptableVictoryFound = true;
                            }
                            ConsiderLocalBoundary(successor, commitment);
                        }
                        if (limitBoundary != ForecastSearchBoundary.None)
                        {
                            commitmentIncomplete = true;
                            break;
                        }
                        if (acceptableVictoryFound) break;
                    }
                    if (commitmentHitPendingChoice || (!acceptableVictoryFound && commitmentIncomplete))
                        extraTruncatedParents.Add(commitment);
                    cardChildren.Remove(commitment);
                    if (limitBoundary != ForecastSearchBoundary.None || acceptableVictoryFound) break;
                }
            }
        }

        List<SearchNode> retainedCardChildren = ForecastWaveExecutor.DeduplicateCardChildren(cardChildren, budget.MaxCardBranches);
        for (int i = 0; i < retainedCardChildren.Count; i++)
        {
            SearchNode child = retainedCardChildren[i];
            ct.ThrowIfCancellationRequested();
            ClassifyForecastChild(child, parent, completed, ended, next);
            if (MeetsLocalVictory(child))
                acceptableVictoryFound = true;
        }

        return new ParentExpansionResult(
            completed,
            ended,
            next,
            extraTruncatedParents,
            potionFreeBoundaryFallback,
            potionBoundaryFallback,
            parentHitPendingChoice,
            anyHitPendingChoice,
            acceptableVictoryFound,
            limitBoundary,
            StoppedByExternalLimit: false);
    }

    private static bool CheckLimitReached(
        long currentExpanded,
        BeamBudget activeBudget,
        Stopwatch timer,
        ref ForecastSearchBoundary boundary)
    {
        if (timer.Elapsed >= activeBudget.TimeLimit)
        {
            boundary = ForecastSearchBoundary.TimeLimit;
            return true;
        }
        if (currentExpanded >= activeBudget.NodeLimit)
        {
            boundary = ForecastSearchBoundary.NodeLimit;
            return true;
        }
        return false;
    }

    internal static bool HasPlayableFetchedPower(SearchNode parent, SearchNode node)
    {
        if (node.Action?.ChoiceCardIds.Count is not > 0) return false;
        IReadOnlyList<CardModel>? beforeHand = parent.State.Players[0].PlayerCombatState?.Hand.Cards;
        IReadOnlyList<CardModel> afterHand = node.State.Players[0].PlayerCombatState!.Hand.Cards;
        IReadOnlyList<string> choiceCardIds = node.Action.ChoiceCardIds;

        for (int i = 0; i < afterHand.Count; i++)
        {
            CardModel card = afterHand[i];
            if (card.Type != CardType.Power || !card.CanPlay(out _))
                continue;
            string entryId = card.Id.Entry;
            bool inChoices = false;
            for (int c = 0; c < choiceCardIds.Count; c++)
            {
                if (string.Equals(choiceCardIds[c], entryId, StringComparison.Ordinal))
                {
                    inChoices = true;
                    break;
                }
            }
            if (!inChoices)
                continue;

            int beforeCount = 0;
            if (beforeHand is not null)
            {
                for (int b = 0; b < beforeHand.Count; b++)
                {
                    if (string.Equals(beforeHand[b].Id.Entry, entryId, StringComparison.Ordinal))
                        beforeCount++;
                }
            }

            int afterCount = 0;
            for (int a = 0; a < afterHand.Count; a++)
            {
                if (string.Equals(afterHand[a].Id.Entry, entryId, StringComparison.Ordinal))
                    afterCount++;
            }

            if (afterCount > beforeCount)
                return true;
        }
        return false;
    }

    internal static bool MeetsAcceptableVictory(
        SearchNode node,
        CombatPolicy policy,
        StateEvaluator evaluator,
        int openingPreRootBattleHpLost) =>
        policy.StopAtAcceptableBattleHpLoss
        && node.Won
        && node.DeathSaveUseCount == 0
        && evaluator.SatisfiesPotionPolicy(node)
        && node.PotionUses <= evaluator.MinimumRequiredPotionUses
        && evaluator.SatisfiesTheftPolicy(node)
        && evaluator.SatisfiesRelicTargets(node)
        && evaluator.SatisfiesGrowthTargets(node)
        && checked(openingPreRootBattleHpLost + evaluator.BattleHpLost(node)) <= policy.AcceptableBattleHpLoss;

    internal static void ClassifyForecastChild(
        SearchNode child,
        SearchNode parent,
        List<SearchNode> completed,
        List<SearchNode> ended,
        List<SearchNode> next)
    {
        if (child.Ended || child.NodeBoundary != ForecastSearchBoundary.None)
            completed.Add(child);
        else if (child.Turn != parent.Turn)
            ended.Add(child);
        else
            next.Add(child);
    }

    private static List<SearchNode> SelectForecastFrontier(
        IReadOnlyList<SearchNode> candidates,
        BeamBudget budget,
        bool aggressivePowerCommitment = false,
        bool secondRankBand = false,
        bool enablePowerCommitmentSeats = true)
    {
        if (budget.PotionFreeBeamWidth is null && budget.PotionBeamWidth is null)
        {
            return Retention.SelectWithKey(
                candidates,
                budget.BeamWidth,
                ForecastRetentionKey.Compute,
                includeParetoProgress: true,
                aggressivePowerCommitment: aggressivePowerCommitment,
                secondRankBand: secondRankBand,
                enablePowerCommitmentSeats: enablePowerCommitmentSeats);
        }

        var freeCandidates = new List<SearchNode>(candidates.Count);
        var potionCandidates = new List<SearchNode>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            SearchNode node = candidates[i];
            if (node.PotionUses == 0)
                freeCandidates.Add(node);
            else
                potionCandidates.Add(node);
        }

        int freeWidth = budget.PotionFreeBeamWidth ?? budget.BeamWidth;
        int potionWidth = budget.PotionBeamWidth ?? budget.BeamWidth;
        List<SearchNode> result = Retention.SelectWithKey(
            freeCandidates,
            freeWidth,
            ForecastRetentionKey.Compute,
            includeParetoProgress: true,
            aggressivePowerCommitment: aggressivePowerCommitment,
            secondRankBand: secondRankBand,
            enablePowerCommitmentSeats: enablePowerCommitmentSeats);
        List<SearchNode> potionResult = Retention.SelectWithKey(
            potionCandidates,
            potionWidth,
            ForecastRetentionKey.Compute,
            includeParetoProgress: true,
            aggressivePowerCommitment: aggressivePowerCommitment,
            secondRankBand: secondRankBand,
            enablePowerCommitmentSeats: enablePowerCommitmentSeats);
        result.AddRange(potionResult);
        return result;
    }
}
