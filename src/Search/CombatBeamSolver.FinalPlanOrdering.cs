namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed class FinalPlanOrdering(
        SolverPotionPolicy potionPolicy,
        PotionStrategySnapshot potionStrategy,
        bool enforcePotionDirectives,
        bool renewablePotionShapedRock,
        SolverTheftPolicy? theftPolicy,
        BossHpRelief bossHpRelief,
        LongTermGoals pursuedLongTermGoals,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int initialPlayerMaxHp,
        int minimumPotionUses,
        SearchDiagnosticsSink diagnostics,
        bool detailedDiagnostics,
        BattleDamageSnapshot battleDamage)
    {
        /// <summary>
        /// The HP a potion must save to be worth spending, scaled by how much HP is worth in this fight. When HP
        /// buys nothing, no amount of saved HP justifies a potion and only the win/lose escape in
        /// <see cref="PotionUsePolicy.IsEligible"/> can still admit one.
        /// </summary>
        private int ScalePotionCost(int strategicHpCost)
            => PotionUsePolicy.SmartRequiredHpSaved(strategicHpCost, bossHpRelief);

        public FinalPlanSelection Select(
            IReadOnlyList<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated,
            int initialHp,
            bool emitDiagnostics)
        {
            var policyCandidates = evaluated
                .Select(candidate =>
                {
                    SearchFeatures features = SearchFeatures.Capture(candidate.Node);
                    int sold = features.FutureSoldHp;
                    int battleSold = battleDamage.SoldHpCommitted + sold;
                    int potionCount = features.PotionCount;
                    int explicitPotionCount = PotionUsePolicy.ExplicitUseCount(
                        potionCount,
                        candidate.Snapshot.AutomaticPotionUseCount);
                    int ambergrisCount = candidate.Node.Actions.Count(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
                    ForcedPotionUseEvaluation forced = enforcePotionDirectives
                        ? potionStrategy.EvaluateForcedUses(
                            candidate.Node.Actions,
                            renewablePotionShapedRock)
                        : new ForcedPotionUseEvaluation(true, 0, 0, 0);
                    int explicitPotionStrategicCost = candidate.Node.Actions
                        .Where(action => action.Kind == PlanActionKind.UsePotion)
                        .Sum(action => PotionUsePolicy.StrategicHpCost(
                            action.PotionId!,
                            renewablePotionShapedRock));
                    int optionalPotionCount = Math.Max(
                        0,
                        explicitPotionCount - forced.ForcedUseCount);
                    int optionalPotionStrategicCost = Math.Max(
                        0,
                        explicitPotionStrategicCost - forced.ForcedStrategicHpCost);
                    int optionalAmbergrisCount = Math.Max(0, ambergrisCount - forced.ForcedAmbergrisCount);
                    SolverPotionPolicy effectivePotionPolicy = potionPolicy switch
                    {
                        SolverPotionPolicy.RequireAtLeastOne when forced.ForcedUseCount > 0
                            => SolverPotionPolicy.Smart,
                        SolverPotionPolicy.Disabled when optionalPotionCount > 0
                            => SolverPotionPolicy.Smart,
                        _ => potionPolicy,
                    };
                    int hpDeficit = features.CumulativePlayerHpLost;
                    int maxHpDeficit = Math.Max(0, initialPlayerMaxHp - features.PlayerMaxHp);
                    int strategicHpDeficit = hpDeficit + maxHpDeficit;
                    int healthResourceCost = initialHp - features.PlayerHp
                        + initialPlayerMaxHp - features.PlayerMaxHp;
                    int strategicSold = battleSold;
                    int policyHpDeficit = strategicHpDeficit
                        + (effectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                            ? PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                                optionalPotionStrategicCost)
                            : 0);
                    bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                        candidate.Node.ActionCount,
                        features.AllEnemiesDead,
                        candidate.Snapshot.PlayerDead,
                        features.ProjectedPlayerHp);
                    return (candidate.Node, candidate.Snapshot, Features: features,
                        CompleteVictory: completeVictory,
                        CombatEndedTurn: completeVictory ? candidate.Node.Action?.Turn : null,
                        FutureSold: sold, BattleSold: battleSold, PotionCount: potionCount,
                        ExplicitPotionCount: explicitPotionCount, HpDeficit: hpDeficit,
                        StrategicHpDeficit: strategicHpDeficit, PolicyHpDeficit: policyHpDeficit,
                        MaxHpDeficit: maxHpDeficit, HealthResourceCost: healthResourceCost,
                        StrategicSold: strategicSold, PotionStrategicCost: candidate.Node.PotionStrategicCost,
                        AmbergrisCount: ambergrisCount, Score: features.Score,
                        ForcedUsesSatisfied: forced.AllForcedUsesSatisfied,
                        OptionalPotionCount: optionalPotionCount,
                        OptionalPotionStrategicCost: optionalPotionStrategicCost,
                        OptionalAmbergrisCount: optionalAmbergrisCount,
                        EffectivePotionPolicy: effectivePotionPolicy);
                })
                .ToList();
            if (emitDiagnostics && detailedDiagnostics)
            {
                foreach (var potionGroup in policyCandidates
                             .GroupBy(candidate => candidate.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    var diagnostic = potionGroup
                        .OrderByDescending(candidate => candidate.CompleteVictory)
                        .ThenByDescending(candidate => candidate.Features.ProjectedPlayerHp)
                        .ThenBy(candidate => candidate.Features.EnemyHp)
                        .ThenByDescending(candidate => candidate.Score)
                        .First();
                    diagnostics.Info(
                        $"[CombatSolver/Debug] POTION_FINAL_CANDIDATE count={potionGroup.Key} " +
                        $"won={diagnostic.CompleteVictory} hp={diagnostic.Snapshot.PlayerHp} " +
                        $"projected_hp={diagnostic.Features.ProjectedPlayerHp} " +
                        $"enemy_hp={diagnostic.Features.EnemyHp} " +
                        $"actions={string.Join(',', diagnostic.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
            }
            int potionFreeBaselineIndex = -1;
            for (int index = 0; index < policyCandidates.Count; index++)
            {
                if (policyCandidates[index].ExplicitPotionCount != 0
                    || potionFreeBaselineIndex >= 0
                        && ComparePotionFreePolicyBaselines(
                            policyCandidates[index].Node,
                            policyCandidates[potionFreeBaselineIndex].Node,
                            initialHp,
                            initialPlayerMaxHp,
                            theftPolicy) >= 0)
                {
                    continue;
                }
                potionFreeBaselineIndex = index;
            }
            bool hasPotionFreeBaseline = potionFreeBaselineIndex >= 0;
            bool potionFreeWon = hasPotionFreeBaseline
                && policyCandidates[potionFreeBaselineIndex].CompleteVictory;
            int potionFreeStrategicHpDeficit = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].StrategicHpDeficit
                : initialHp;
            int potionFreePlayerHp = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Snapshot.PlayerHp
                : 0;
            int? potionFreeCombatEndedTurn = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].CombatEndedTurn
                : null;
            int potionFreeOutstandingResource = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Features.OutstandingStolenResource
                : int.MaxValue;
            if (potionFreePolicyBaseline is { } auditedBaseline)
            {
                hasPotionFreeBaseline = true;
                potionFreeWon = auditedBaseline.Won;
                potionFreeStrategicHpDeficit = auditedBaseline.HpDeficit;
                potionFreePlayerHp = auditedBaseline.PlayerHp;
                potionFreeCombatEndedTurn = auditedBaseline.CombatEndedTurn;
            }
            bool anyRouteWon = potionFreeWon
                || policyCandidates.Any(candidate => candidate.CompleteVictory);
            if (emitDiagnostics)
            {
                if (potionFreeBaselineIndex >= 0)
                {
                    var potionFreeBaseline = policyCandidates[potionFreeBaselineIndex];
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE kind=potion_free " +
                        $"won={potionFreeWon} hp_deficit={potionFreeBaseline.HpDeficit} " +
                        $"enemy_hp={potionFreeBaseline.Features.EnemyHp} " +
                        $"boundary={potionFreeBaseline.Features.BoundaryReason} " +
                        $"actions={string.Join(',', potionFreeBaseline.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
                else
                {
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE kind=potion_free missing=true " +
                        $"won=false hp_deficit={initialHp}");
                }
                if (potionFreePolicyBaseline is { } baselineOverride)
                {
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE_OVERRIDE kind=potion_free " +
                        $"won={baselineOverride.Won} hp_deficit={baselineOverride.HpDeficit}");
                }
            }
            var policyEligibleCandidates = policyCandidates
                .Where(candidate =>
                {
                    bool strictPrimaryImprovement = hasPotionFreeBaseline
                        && potionPolicy != SolverPotionPolicy.Disabled
                        && candidate.OptionalPotionCount > 0
                        && SolverInterimResultOrdering.ComparePrimaryQuality(
                            candidate.CompleteVictory,
                            candidate.StrategicHpDeficit,
                            candidate.CombatEndedTurn,
                            potionFreeWon,
                            potionFreeStrategicHpDeficit,
                            potionFreeCombatEndedTurn) < 0;
                    bool passesSoftPotionPolicy = PotionUsePolicy.IsEligible(
                            candidate.EffectivePotionPolicy,
                            candidate.OptionalPotionCount,
                            ScalePotionCost(candidate.OptionalPotionStrategicCost),
                            potionFreeWon,
                            potionFreeStrategicHpDeficit,
                            anyRouteWon,
                            candidate.CompleteVictory,
                            candidate.StrategicHpDeficit)
                        || strictPrimaryImprovement
                        || theftPolicy == SolverTheftPolicy.PreserveResources
                            && candidate.PotionCount > 0
                            && candidate.Features.OutstandingStolenResource
                                < potionFreeOutstandingResource;
                    bool passesAmbergrisPolicy = strictPrimaryImprovement
                        || PotionUsePolicy.MeetsAmbergrisRestriction(
                            hasPotionFreeBaseline,
                            candidate.OptionalAmbergrisCount,
                            candidate.OptionalPotionStrategicCost,
                            initialPlayerMaxHp,
                            potionFreePlayerHp,
                            candidate.Snapshot.PlayerHp);
                    return candidate.ForcedUsesSatisfied
                        && candidate.ExplicitPotionCount >= minimumPotionUses
                        && passesSoftPotionPolicy
                        && passesAmbergrisPolicy;
                })
                .ToList();
            var selected = policyEligibleCandidates
                .OrderByDescending(candidate => candidate.CompleteVictory)
                // A live incomplete fallback is always preferable to a dead fallback. For
                // complete victories this key is uniformly zero and cannot weaken the
                // requested loss-then-duration ordering.
                .ThenBy(candidate => !candidate.CompleteVictory
                    && (candidate.Snapshot.PlayerDead
                        || candidate.Snapshot.ProjectedPlayerHp <= 0)
                        ? 1
                        : 0)
                // Final quality is lexicographic: any lower strategic battle loss wins;
                // combat duration is the immediate tie-breaker, including run-ending fights.
                .ThenBy(candidate => candidate.StrategicHpDeficit)
                .ThenBy(candidate => candidate.CombatEndedTurn ?? int.MaxValue)
                .ThenBy(candidate => theftPolicy == SolverTheftPolicy.PreserveResources
                    ? candidate.Features.OutstandingStolenResource
                    : 0)
                .ThenBy(candidate => candidate.PolicyHpDeficit)
                .ThenBy(candidate => candidate.HealthResourceCost)
                .ThenByDescending(candidate => candidate.Features.LongTermResourceValue)
                .ThenBy(candidate => candidate.Features.AngerCopiesGenerated)
                .ThenBy(candidate => CombatBeamSolver.PolicyBoundaryRank(candidate.Features.BoundaryReason))
                .ThenBy(candidate => candidate.OptionalPotionCount)
                .ThenBy(candidate => candidate.StrategicSold)
                .ThenBy(candidate => candidate.Features.EnemyHp)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Features.ActionCount)
                .ToList();
            if (selected.Count == 0)
            {
                throw new PotionPolicyUnsatisfiedException(
                    enforcePotionDirectives && potionStrategy.HasForcedDirectives
                        ? $"指定药水必须使用，但搜索没有找到可执行路线：{potionStrategy.DescribeForcedUses()}。"
                        : potionPolicy == SolverPotionPolicy.RequireAtLeastOne
                        ? "本场药水策略要求至少使用一瓶，但搜索没有找到可执行的用药路线。"
                        : "本场药水策略没有可执行路线。");
            }
            // The ordering is a total order that does not depend on the goal constraint, so the best route that
            // banks the required goals is simply the first ordered route that banks them. No second ranking, and
            // no second search: the price of insisting is the gap between that route and the unconstrained best.
            LongTermGoals reachableGoals = LongTermGoals.None;
            foreach (var candidate in selected)
                reachableGoals |= candidate.Features.LongTermGoals;
            LongTermGoals requiredGoals = pursuedLongTermGoals & reachableGoals;
            var unconstrainedCandidate = selected[0];
            bool unconstrainedLethal = unconstrainedCandidate.Snapshot.PlayerDead
                || unconstrainedCandidate.Snapshot.ProjectedPlayerHp <= 0;
            int compliantCount = 0;
            int selectableIndex = -1;
            for (int index = 0; index < selected.Count; index++)
            {
                var candidate = selected[index];
                // Bank every goal that some route can reach, and never spend a pursued goal's card without
                // banking it. Holding Hand of Greed is what the player asked for; throwing it away as a plain
                // attack is the outcome they complained about.
                bool banksRequired =
                    (candidate.Features.LongTermGoals & requiredGoals) == requiredGoals;
                bool wastesNothing = (candidate.Features.LongTermGoalCardsPlayed
                    & pursuedLongTermGoals
                    & ~candidate.Features.LongTermGoals) == LongTermGoals.None;
                if (!banksRequired || !wastesNothing)
                    continue;
                compliantCount++;
                // Only the player's life is off limits. Deliberately NOT AllEnemiesDead: that means "finished
                // inside the searched horizon", not "won", and holding a finisher usually pushes the kill past
                // the horizon, so gating on it rejected exactly the routes the player asked for.
                bool lethal = candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0;
                if (selectableIndex < 0 && (!lethal || unconstrainedLethal))
                    selectableIndex = index;
            }
            LongTermGoalOutcome goalOutcome = pursuedLongTermGoals == LongTermGoals.None
                ? LongTermGoalOutcome.Off
                : selectableIndex < 0
                    ? compliantCount == 0
                        ? LongTermGoalOutcome.NoCompliantRoute
                        : LongTermGoalOutcome.CompliantRouteWouldDie
                    : selectableIndex == 0
                        ? LongTermGoalOutcome.Free
                        : LongTermGoalOutcome.Paid;
            int selectedIndex = pursuedLongTermGoals == LongTermGoals.None || selectableIndex < 0
                ? 0
                : selectableIndex;
            var selectedCandidate = selected[selectedIndex];
            int longTermGoalHpPrice = Math.Max(
                0,
                selectedCandidate.StrategicHpDeficit - unconstrainedCandidate.StrategicHpDeficit);
            int longTermGoalPotionPrice = Math.Max(
                0,
                selectedCandidate.PotionCount - unconstrainedCandidate.PotionCount);
            if (pursuedLongTermGoals != LongTermGoals.None)
            {
                diagnostics.Info(
                    $"[CombatSolver/Test] LONG_TERM_GOAL_PRICE pursued={pursuedLongTermGoals} " +
                    $"reachable={reachableGoals} required={requiredGoals} " +
                    $"banked={selectedCandidate.Features.LongTermGoals} " +
                    $"cards_played={selectedCandidate.Features.LongTermGoalCardsPlayed} " +
                    $"candidates={selected.Count} compliant={compliantCount} outcome={goalOutcome} " +
                    $"hp_price={longTermGoalHpPrice} potion_price={longTermGoalPotionPrice}");
            }
            int potionBranchesRejected = policyCandidates.Count(candidate => candidate.PotionCount > 0)
                - policyEligibleCandidates.Count(candidate => candidate.PotionCount > 0);
            int potionHpSaved = selectedCandidate.PotionCount == 0
                ? 0
                : selectedCandidate.AmbergrisCount > 0
                    ? Math.Max(0, selectedCandidate.Snapshot.PlayerHp - potionFreePlayerHp)
                    : PotionUsePolicy.HpSaved(
                        potionFreeStrategicHpDeficit,
                        selectedCandidate.StrategicHpDeficit);
            int potionHpRequired = PotionUsePolicy.EffectiveStrategicHpCost(
                selectedCandidate.OptionalPotionStrategicCost,
                selectedCandidate.OptionalAmbergrisCount,
                initialPlayerMaxHp);
            if (selectedCandidate.EffectivePotionPolicy == SolverPotionPolicy.Smart
                && selectedCandidate.OptionalAmbergrisCount == 0
                && potionFreeWon)
            {
                potionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
                    potionHpRequired,
                    bossHpRelief);
            }
            if (selectedCandidate.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne)
            {
                potionHpRequired = PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                    potionHpRequired);
            }
            return new FinalPlanSelection(
                new FinalPlanCandidate(
                    selectedCandidate.Node,
                    selectedCandidate.Snapshot,
                    selectedCandidate.Features,
                    selectedCandidate.FutureSold,
                    selectedCandidate.BattleSold,
                    selectedCandidate.PotionCount,
                    selectedCandidate.Score),
                potionBranchesRejected,
                potionHpSaved,
                potionHpRequired,
                requiredGoals,
                selectedCandidate.Features.LongTermGoals,
                longTermGoalHpPrice,
                longTermGoalPotionPrice,
                goalOutcome,
                compliantCount);
        }
    }

    private static int ComparePotionFreePolicyBaselines(
        SearchNode left,
        SearchNode right,
        int initialPlayerHp,
        int initialPlayerMaxHp,
        SolverTheftPolicy? theftPolicy)
    {
        SimulationSnapshot leftSnapshot = left.Snapshot;
        SimulationSnapshot rightSnapshot = right.Snapshot;
        bool leftWon = SolverInterimResultOrdering.IsCompleteVictory(
            left.ActionCount,
            leftSnapshot.AllEnemiesDead,
            leftSnapshot.PlayerDead,
            leftSnapshot.ProjectedPlayerHp);
        bool rightWon = SolverInterimResultOrdering.IsCompleteVictory(
            right.ActionCount,
            rightSnapshot.AllEnemiesDead,
            rightSnapshot.PlayerDead,
            rightSnapshot.ProjectedPlayerHp);
        int comparison = rightWon.CompareTo(leftWon);
        if (comparison != 0)
            return comparison;
        if (!leftWon && !rightWon)
        {
            bool leftSurvives = !leftSnapshot.PlayerDead && leftSnapshot.ProjectedPlayerHp > 0;
            bool rightSurvives = !rightSnapshot.PlayerDead && rightSnapshot.ProjectedPlayerHp > 0;
            comparison = rightSurvives.CompareTo(leftSurvives);
            if (comparison != 0)
                return comparison;
        }
        comparison = (leftSnapshot.CumulativePlayerHpLost
                + Math.Max(0, initialPlayerMaxHp - leftSnapshot.PlayerMaxHp))
            .CompareTo(rightSnapshot.CumulativePlayerHpLost
                + Math.Max(0, initialPlayerMaxHp - rightSnapshot.PlayerMaxHp));
        if (comparison != 0)
            return comparison;
        comparison = (leftWon ? left.Action?.Turn ?? int.MaxValue : int.MaxValue)
            .CompareTo(rightWon ? right.Action?.Turn ?? int.MaxValue : int.MaxValue);
        if (comparison != 0)
            return comparison;
        if (theftPolicy == SolverTheftPolicy.PreserveResources)
        {
            comparison = leftSnapshot.OutstandingStolenResource.CompareTo(
                rightSnapshot.OutstandingStolenResource);
            if (comparison != 0)
                return comparison;
        }
        comparison = (initialPlayerHp - leftSnapshot.PlayerHp
                + initialPlayerMaxHp - leftSnapshot.PlayerMaxHp)
            .CompareTo(initialPlayerHp - rightSnapshot.PlayerHp
                + initialPlayerMaxHp - rightSnapshot.PlayerMaxHp);
        if (comparison != 0)
            return comparison;
        comparison = rightSnapshot.LongTermResourceValue.CompareTo(leftSnapshot.LongTermResourceValue);
        if (comparison != 0)
            return comparison;
        comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
        if (comparison != 0)
            return comparison;
        comparison = PolicyBoundaryRank(leftSnapshot.BoundaryReason)
            .CompareTo(PolicyBoundaryRank(rightSnapshot.BoundaryReason));
        if (comparison != 0)
            return comparison;
        comparison = leftSnapshot.EnemyHp.CompareTo(rightSnapshot.EnemyHp);
        if (comparison != 0)
            return comparison;
        comparison = right.Score.CompareTo(left.Score);
        if (comparison != 0)
            return comparison;
        comparison = left.FutureSoldHp.CompareTo(right.FutureSoldHp);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = left.StateKey.First.CompareTo(right.StateKey.First);
        return comparison != 0
            ? comparison
            : left.StateKey.Second.CompareTo(right.StateKey.Second);
    }
}
