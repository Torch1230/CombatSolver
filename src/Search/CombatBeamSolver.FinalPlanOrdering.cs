namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed class FinalPlanOrdering(
        SolverPotionPolicy potionPolicy,
        SolverTheftPolicy? theftPolicy,
        LongTermGoals pursuedLongTermGoals,
        BossHpRelief bossHpRelief,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int initialPlayerMaxHp,
        SearchDiagnosticsSink diagnostics,
        bool detailedDiagnostics,
        BattleDamageSnapshot battleDamage)
    {
        /// <summary>
        /// Quarters of a normal fight's HP weight. A boss whose act clear refunds most of the damage is worth a
        /// quarter; the run's last fight is worth nothing beyond surviving it.
        /// </summary>
        private readonly int _hpWeightQuarters = bossHpRelief switch
        {
            BossHpRelief.RunEnding => 0,
            BossHpRelief.ActClearHeal => 1,
            _ => 4,
        };

        /// <summary>
        /// The HP a potion must save to be worth spending, scaled by how much HP is worth in this fight. When HP
        /// buys nothing, no amount of saved HP justifies a potion and only the win/lose escape in
        /// <see cref="PotionUsePolicy.IsEligible"/> can still admit one.
        /// </summary>
        private int ScalePotionCost(int strategicHpCost)
            => _hpWeightQuarters == 0
                ? int.MaxValue / 4
                : strategicHpCost * 4 / _hpWeightQuarters;

        /// <summary>Distinct potion sets reported as alternatives. Enough to compare, few enough to read.</summary>
        private const int MaximumWorldLines = 6;

        public FinalPlanSelection Select(
            IReadOnlyList<(SearchNode Node, SimulationSnapshot Snapshot, RouteAnnotations Annotations)> evaluated,
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
                    int ambergrisCount = candidate.Node.Actions.Count(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
                    int hpDeficit = features.CumulativePlayerHpLost;
                    int maxHpDeficit = Math.Max(0, initialPlayerMaxHp - features.PlayerMaxHp);
                    int strategicHpDeficit = hpDeficit + maxHpDeficit;
                    int healthResourceCost = initialHp - features.PlayerHp
                        + initialPlayerMaxHp - features.PlayerMaxHp;
                    int strategicSold = battleSold;
                    int policyHpDeficit = strategicHpDeficit
                        + (potionPolicy == SolverPotionPolicy.RequireAtLeastOne
                            ? PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                                candidate.Node.PotionStrategicCost)
                            : 0);
                    return (candidate.Node, candidate.Snapshot, candidate.Annotations, Features: features,
                        FutureSold: sold, BattleSold: battleSold, PotionCount: potionCount, HpDeficit: hpDeficit,
                        StrategicHpDeficit: strategicHpDeficit, PolicyHpDeficit: policyHpDeficit,
                        MaxHpDeficit: maxHpDeficit, HealthResourceCost: healthResourceCost,
                        StrategicSold: strategicSold, PotionStrategicCost: candidate.Node.PotionStrategicCost,
                        AmbergrisCount: ambergrisCount, Score: features.Score);
                })
                .ToList();
            if (emitDiagnostics && detailedDiagnostics)
            {
                foreach (var potionGroup in policyCandidates
                             .GroupBy(candidate => candidate.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    var diagnostic = potionGroup
                        .OrderByDescending(candidate => candidate.Features.AllEnemiesDead)
                        .ThenByDescending(candidate => candidate.Features.ProjectedPlayerHp)
                        .ThenBy(candidate => candidate.Features.EnemyHp)
                        .ThenByDescending(candidate => candidate.Score)
                        .First();
                    diagnostics.Info(
                        $"[CombatSolver/Debug] POTION_FINAL_CANDIDATE count={potionGroup.Key} " +
                        $"won={diagnostic.Features.AllEnemiesDead} hp={diagnostic.Snapshot.PlayerHp} " +
                        $"projected_hp={diagnostic.Features.ProjectedPlayerHp} " +
                        $"enemy_hp={diagnostic.Features.EnemyHp} " +
                        $"actions={string.Join(',', diagnostic.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
            }
            int potionFreeBaselineIndex = policyCandidates
                .Select((candidate, index) => (Candidate: candidate, Index: index))
                .Where(item => item.Candidate.PotionCount == 0)
                .OrderByDescending(item => item.Candidate.Features.AllEnemiesDead)
                .ThenBy(item => theftPolicy == SolverTheftPolicy.PreserveResources
                    ? item.Candidate.Features.OutstandingStolenResource
                    : 0)
                .ThenBy(item => item.Candidate.StrategicHpDeficit)
                .ThenBy(item => item.Candidate.HealthResourceCost)
                .ThenByDescending(item => item.Candidate.Features.LongTermResourceValue)
                .ThenBy(item => item.Candidate.Features.AngerCopiesGenerated)
                .ThenBy(item => CombatBeamSolver.PolicyBoundaryRank(item.Candidate.Features.BoundaryReason))
                .ThenBy(item => item.Candidate.Features.EnemyHp)
                .ThenByDescending(item => item.Candidate.Score)
                .ThenBy(item => item.Candidate.StrategicSold)
                .ThenBy(item => item.Candidate.Features.ActionCount)
                .Select(item => item.Index)
                .DefaultIfEmpty(-1)
                .First();
            bool hasPotionFreeBaseline = potionFreeBaselineIndex >= 0;
            bool potionFreeWon = hasPotionFreeBaseline
                && policyCandidates[potionFreeBaselineIndex].Features.AllEnemiesDead;
            int potionFreeStrategicHpDeficit = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].StrategicHpDeficit
                : initialHp;
            int potionFreePlayerHp = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Snapshot.PlayerHp
                : 0;
            int potionFreeOutstandingResource = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Features.OutstandingStolenResource
                : int.MaxValue;
            if (potionFreePolicyBaseline is { } auditedBaseline)
            {
                hasPotionFreeBaseline = true;
                potionFreeWon = auditedBaseline.Won;
                potionFreeStrategicHpDeficit = auditedBaseline.HpDeficit;
                potionFreePlayerHp = auditedBaseline.PlayerHp;
            }
            bool anyRouteWon = potionFreeWon
                || policyCandidates.Any(candidate => candidate.Features.AllEnemiesDead);
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
            var selected = policyCandidates
                .Where(candidate =>
                    (PotionUsePolicy.IsEligible(
                         potionPolicy,
                         candidate.PotionCount,
                         candidate.Snapshot.AutomaticPotionUseCount,
                         ScalePotionCost(candidate.PotionStrategicCost),
                         potionFreeWon,
                         potionFreeStrategicHpDeficit,
                         anyRouteWon,
                         candidate.Features.AllEnemiesDead,
                         candidate.StrategicHpDeficit)
                     || theftPolicy == SolverTheftPolicy.PreserveResources
                        && candidate.PotionCount > 0
                        && candidate.Features.OutstandingStolenResource < potionFreeOutstandingResource)
                    && PotionUsePolicy.MeetsAmbergrisRestriction(
                        hasPotionFreeBaseline,
                        candidate.AmbergrisCount,
                        candidate.PotionStrategicCost,
                        initialPlayerMaxHp,
                        potionFreePlayerHp,
                        candidate.Snapshot.PlayerHp))
                .OrderByDescending(candidate => candidate.Features.AllEnemiesDead)
                // Survival used to be implied by the HP deficit being maximal on a death route. Once HP can be
                // weighted down to nothing it has to be stated, or a run-ending boss would rank a lethal route.
                .ThenBy(candidate => candidate.Snapshot.PlayerDead
                    || candidate.Snapshot.ProjectedPlayerHp <= 0
                        ? 1
                        : 0)
                .ThenBy(candidate => theftPolicy == SolverTheftPolicy.PreserveResources
                    ? candidate.Features.OutstandingStolenResource
                    : 0)
                .ThenBy(candidate => candidate.PolicyHpDeficit * _hpWeightQuarters)
                .ThenBy(candidate => candidate.HealthResourceCost * _hpWeightQuarters)
                .ThenByDescending(candidate => candidate.Features.LongTermResourceValue)
                .ThenBy(candidate => candidate.Features.AngerCopiesGenerated)
                .ThenBy(candidate => CombatBeamSolver.PolicyBoundaryRank(candidate.Features.BoundaryReason))
                .ThenBy(candidate => candidate.PotionCount)
                .ThenBy(candidate => candidate.StrategicSold)
                .ThenBy(candidate => candidate.Features.EnemyHp)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Features.ActionCount)
                .ToList();
            if (selected.Count == 0)
            {
                throw new PotionPolicyUnsatisfiedException(
                    potionPolicy == SolverPotionPolicy.RequireAtLeastOne
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
                // the horizon. Rejecting those was why the card kept getting spent anyway.
                // A compliant route may be lethal only when the unconstrained best already is. Equality here was
                // backwards: it also rejected a compliant route that survives while the unconstrained best dies.
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
            List<RouteWorldLine> worldLines = [];
            string SelectedPotionKey(int index) => string.Join(
                '+',
                selected[index].Node.Actions
                    .Where(action => action.Kind == PlanActionKind.UsePotion)
                    .Select(action => action.PotionTitle)
                    .OrderBy(title => title, StringComparer.Ordinal));
            string selectedKey = SelectedPotionKey(selectedIndex);
            HashSet<string> seenPotionKeys = [];
            int firstPotionFreeIndex = -1;
            for (int index = 0; index < selected.Count; index++)
            {
                string key = SelectedPotionKey(index);
                if (key.Length == 0 && firstPotionFreeIndex < 0)
                    firstPotionFreeIndex = index;
                if (!seenPotionKeys.Add(key) || worldLines.Count >= MaximumWorldLines)
                    continue;
                worldLines.Add(BuildWorldLine(index, key));
            }
            // The no-potion line is the one comparison a player always wants, so keep it even if the cap hit first.
            if (firstPotionFreeIndex >= 0 && !worldLines.Any(line => line.PotionCount == 0))
                worldLines.Add(BuildWorldLine(firstPotionFreeIndex, string.Empty));
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
            int potionBranchesRejected = policyCandidates.Count(candidate =>
                candidate.PotionCount > 0
                && (!(PotionUsePolicy.IsEligible(
                          potionPolicy,
                          candidate.PotionCount,
                          candidate.Snapshot.AutomaticPotionUseCount,
                          ScalePotionCost(candidate.PotionStrategicCost),
                          potionFreeWon,
                          potionFreeStrategicHpDeficit,
                          anyRouteWon,
                          candidate.Features.AllEnemiesDead,
                          candidate.StrategicHpDeficit)
                      || theftPolicy == SolverTheftPolicy.PreserveResources
                         && candidate.Features.OutstandingStolenResource < potionFreeOutstandingResource)
                    || !PotionUsePolicy.MeetsAmbergrisRestriction(
                        hasPotionFreeBaseline,
                        candidate.AmbergrisCount,
                        candidate.PotionStrategicCost,
                        initialPlayerMaxHp,
                        potionFreePlayerHp,
                        candidate.Snapshot.PlayerHp)));
            int potionHpSaved = selectedCandidate.PotionCount == 0
                ? 0
                : selectedCandidate.AmbergrisCount > 0
                    ? Math.Max(0, selectedCandidate.Snapshot.PlayerHp - potionFreePlayerHp)
                    : PotionUsePolicy.HpSaved(
                        potionFreeStrategicHpDeficit,
                        selectedCandidate.StrategicHpDeficit);
            int potionHpRequired = PotionUsePolicy.EffectiveStrategicHpCost(
                selectedCandidate.PotionStrategicCost,
                selectedCandidate.AmbergrisCount,
                initialPlayerMaxHp);
            if (potionPolicy == SolverPotionPolicy.RequireAtLeastOne)
            {
                potionHpRequired = PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                    potionHpRequired);
            }
            return new FinalPlanSelection(
                new FinalPlanCandidate(
                    selectedCandidate.Node,
                    selectedCandidate.Snapshot,
                    selectedCandidate.Annotations,
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
                worldLines,
                goalOutcome,
                compliantCount);

            RouteWorldLine BuildWorldLine(int index, string key) => new(
                selected[index].Node.Actions
                    .Where(action => action.Kind == PlanActionKind.UsePotion)
                    .Select(action => action.PotionTitle)
                    .OrderBy(title => title, StringComparer.Ordinal)
                    .ToArray(),
                selected[index].StrategicHpDeficit,
                selected[index].PotionCount,
                selected[index].Features.AllEnemiesDead,
                string.Equals(key, selectedKey, StringComparison.Ordinal));
        }
    }
}
