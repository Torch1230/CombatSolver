using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private const int EarlyTurnExplorationNodeBudget = 1_000_000;

    private static SolverResult RunEarlyTurnExploration(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Stopwatch requestClock,
        SolverResult selected)
    {
        if (policy.EarlyTurnExplorationDepth == 0
            || selected.ResultScope != SolverResultScope.SearchCompletion
            || IsProvenZeroDamageRoute(root, policy, selected))
            return selected;

        int RemainingMilliseconds() => policy.EarlyTurnExplorationBudgetMilliseconds
            - (int)requestClock.ElapsedMilliseconds;
        if (RemainingMilliseconds() <= 10_000)
            return selected;
        SearchRequestWorkTotals workTotals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("早期回合探索缺少请求工作量账本。");
        long expandedAtStart = workTotals.Snapshot().ExpandedNodes;
        long RemainingNodes() => EarlyTurnExplorationNodeBudget
            - (workTotals.Snapshot().ExpandedNodes - expandedAtStart);

        SearchPolicySnapshot scoutPolicy = policy with
        {
            NoveltySearch = null,
            UseNoveltyPortfolio = false,
            StopAtAcceptableBattleHpLoss = false,
        };
        SolverSearchProfile scoutProfile = policy.Profile with
        {
            BeamWidth = Math.Min(384, Math.Max(256, policy.Profile.BeamWidth * 4)),
            MaxExpandedNodes = Math.Min(150_000, policy.Profile.MaxExpandedNodes),
            MaxCardBranchesPerNode = Math.Min(128,
                policy.Profile.MaxCardBranchesPerNode * 2),
            MaxHandChoiceBranchesPerAction = Math.Min(96,
                policy.Profile.MaxHandChoiceBranchesPerAction * 2),
            MaxPileChoiceBranchesPerAction = Math.Min(72,
                policy.Profile.MaxPileChoiceBranchesPerAction * 2),
            SoftTimeBudgetMilliseconds = RemainingMilliseconds(),
            StopPortfolioAtHpTarget = false,
        };
        HashSet<(int Turn, StateFingerprint State)> seenFrontiers = [];
        List<EarlyTurnFrontierCandidate> frontiers = [];
        void Observe(int completedTurns, IReadOnlyList<EarlyTurnFrontierCandidate> candidates)
        {
            foreach (EarlyTurnFrontierCandidate candidate in candidates)
            {
                var key = (completedTurns, candidate.StateKey);
                if (seenFrontiers.Add(key))
                    frontiers.Add(candidate);
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] EARLY_TURN_FRONTIER depth={completedTurns} " +
                $"candidates={candidates.Count} unique={frontiers.Count} " +
                $"potion_prefixes={candidates.Count(candidate => candidate.Actions.Any(action => action.Kind == PlanActionKind.UsePotion))}");
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] EARLY_TURN_EXPLORATION start " +
            $"depth={policy.EarlyTurnExplorationDepth} beam={scoutProfile.BeamWidth} " +
            $"nodes={scoutProfile.MaxExpandedNodes} remaining_ms={RemainingMilliseconds()}");
        try
        {
            SolverResult scout = new CombatBeamSolver(root, displayNames, battleDamage,
                scoutPolicy, cancellationToken, progressCallback, scoutProfile,
                earlyTurnScoutDepth: policy.EarlyTurnExplorationDepth,
                earlyTurnScoutObserver: Observe).Solve();
            if (IsCompleteVictory(scout)
                && IsBetterPotionPolicyResult(root, policy, scout, selected))
                selected = scout;
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            policy.Diagnostics.Info("[CombatSolver/Test] EARLY_TURN_EXPLORATION scout_potion_policy_unsatisfied=true");
        }

        static List<EarlyTurnFrontierCandidate> InterleavePotionStates(
            IEnumerable<EarlyTurnFrontierCandidate> candidates)
        {
            EarlyTurnFrontierCandidate[] withPotion = candidates
                .Where(candidate => candidate.Actions.Any(action => action.Kind == PlanActionKind.UsePotion))
                .ToArray();
            EarlyTurnFrontierCandidate[] withoutPotion = candidates
                .Where(candidate => candidate.Actions.All(action => action.Kind != PlanActionKind.UsePotion))
                .ToArray();
            List<EarlyTurnFrontierCandidate> ordered = [];
            for (int index = 0; index < Math.Max(withPotion.Length, withoutPotion.Length); index++)
            {
                if (index < withPotion.Length) ordered.Add(withPotion[index]);
                if (index < withoutPotion.Length) ordered.Add(withoutPotion[index]);
            }
            return ordered;
        }

        List<EarlyTurnFrontierCandidate>[] layers =
        [
            InterleavePotionStates(frontiers.Where(candidate => candidate.CompletedTurns == 1)),
            InterleavePotionStates(frontiers.Where(candidate => candidate.CompletedTurns == 2)),
        ];
        int attempted = 0;
        for (int rank = 0; rank < 24; rank++)
        {
            foreach (List<EarlyTurnFrontierCandidate> layer in layers)
            {
                if (rank >= layer.Count || RemainingMilliseconds() <= 10_000
                    || RemainingNodes() < 100)
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (policy.MemoryPressureSignal.IsEnabled
                    && !policy.MemoryPressureSignal.CanReachCommit(512L * 1024 * 1024))
                    policy.MemoryPressureSignal.ReclaimAndContinue(
                        cancellationToken, "early_turn_continuation");
                EarlyTurnFrontierCandidate frontier = layer[rank];
                bool usesPotion = frontier.Actions.Any(action => action.Kind == PlanActionKind.UsePotion);
                SolverSearchProfile continuationProfile = policy.Profile with
                {
                    MaxExpandedNodes = (int)Math.Min(RemainingNodes(),
                        Math.Min(120_000, policy.Profile.MaxExpandedNodes)),
                    SoftTimeBudgetMilliseconds = RemainingMilliseconds() - 5_000,
                };
                SolverResult? candidate = SolveOptionalPotionPosterior(
                    new CombatBeamSolver(root, displayNames, battleDamage, policy,
                        cancellationToken, progressCallback, continuationProfile,
                        potionPolicyOverride: usesPotion
                            ? SolverPotionPolicy.RequireAtLeastOne : null,
                        fixedPrefixActions: frontier.Actions,
                        resetFixedPrefixSchedulingBaseline: true),
                    policy, "EARLY_TURN_CONTINUATION");
                attempted++;
                if (candidate == null)
                    continue;
                if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                    return candidate;
                bool improved = IsCompleteVictory(candidate)
                    && policy.PotionStrategy.EvaluateForcedUses(
                        candidate.BestNode.Actions, renewablePotionShapedRock: false)
                    .AllForcedUsesSatisfied
                    && IsBetterPotionPolicyResult(root, policy, candidate, selected);
                if (improved)
                    selected = candidate;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] EARLY_TURN_CONTINUATION " +
                    $"depth={frontier.CompletedTurns} rank={rank} " +
                    $"opening={frontier.Actions[0].Kind}:{frontier.Actions[0].CardId}:{frontier.Actions[0].PotionId} " +
                    $"prefix_potions={frontier.Actions.Count(action => action.Kind == PlanActionKind.UsePotion)} " +
                    $"prefix={string.Join(',', frontier.Actions.Take(12).Select(action => action.Kind switch
                    {
                        PlanActionKind.UsePotion => $"P:{action.PotionId}",
                        PlanActionKind.PlayCard => $"C:{action.CardId}" +
                            (action.Choice is { Cards.Count: > 0 } choice
                                ? $"[{string.Join('+', choice.Cards.Select(card => card.CardId))}]" : ""),
                        _ => "E",
                    }))} " +
                    $"won={IsCompleteVictory(candidate)} " +
                    $"hp_lost={candidate.ProjectedBattleHpLost} " +
                    $"potions={candidate.PotionCount} selected={improved}");
            }
            if (RemainingMilliseconds() <= 10_000
                || RemainingNodes() < 100
                || IsProvenZeroDamageRoute(root, policy, selected))
                break;
        }
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] EARLY_TURN_EXPLORATION result " +
            $"frontiers={frontiers.Count} attempted={attempted} " +
            $"won={IsCompleteVictory(selected)} remaining_ms={RemainingMilliseconds()} " +
            $"expanded={EarlyTurnExplorationNodeBudget - RemainingNodes()} " +
            $"node_limit={EarlyTurnExplorationNodeBudget}");
        return selected;
    }
}
