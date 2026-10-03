#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/../.." && pwd)"
search_root="$repository_root/src/Search"
violations=()
for corpus_input in tools/search/StrategyCorpus/run.py tools/search/StrategyCorpus/compare.py coverage/corpora/strategy/p0.json; do
    [[ -f "$repository_root/$corpus_input" ]] || violations+=("Strategy corpus input missing: $corpus_input")
done
for quality_input in src/Search/RouteQuality.cs src/Search/RouteQualityPolicy.cs; do
    [[ -f "$repository_root/$quality_input" ]] || violations+=("Route quality model missing: $quality_input")
done
[[ -f "$repository_root/src/Search/PotionValuationRegistry.cs" ]] || violations+=("Potion valuation registry missing")
if ! rg -Fq 'PotionValuationRegistry.Default' "$repository_root/src/Search/PotionUsePolicy.cs" ||
   rg -Fq 'HighValuePotionIds' "$repository_root/src/Search/PotionUsePolicy.cs" ||
   rg -Fq 'ElevatedValuePotionIds' "$repository_root/src/Search/PotionUsePolicy.cs"; then
    violations+=("Potion valuation classifications remain outside the registry")
fi
[[ -f "$repository_root/src/Search/OpeningActionRegistry.cs" ]] || violations+=("Opening action registry missing")
if ! rg -Fq 'OpeningActionRegistry.Default' "$repository_root/src/Search/CombatBeamSolver.Expansion.Opening.cs" ||
   rg -q '"(WHITE_NOISE|NIGHTMARE|DUPLICATOR)"' "$repository_root/src/Search/CombatBeamSolver.Expansion.Opening.cs"; then
    violations+=("Opening action IDs remain outside the registry")
fi
if [[ ! -f "$repository_root/src/Search/TargetPlanRegistry.cs" ]] ||
   ! rg -Fq 'TargetPlanRegistry.Default' "$repository_root/src/Search/CombatBeamSolver.Expansion.Opening.cs"; then
    violations+=("Opening target plans are not registered")
fi
strategy_id_literal_count=$(rg -o --no-filename '"[A-Z][A-Z0-9_]{4,}"' "$repository_root/src/Search" \
    --glob '*.cs' \
    --glob '!PotionValuationRegistry.cs' \
    --glob '!OpeningActionRegistry.cs' \
    --glob '!TargetPlanRegistry.cs' | wc -l)
if (( strategy_id_literal_count > 645 )); then
    violations+=("Search strategy ID literals increased: $strategy_id_literal_count > 645")
fi
[[ -f "$repository_root/src/Search/CombatBeamSolver.ExpansionPlan.cs" ]] || violations+=("Shared expansion plan missing")
if ! rg -Fq 'cardJobs.PrepareSerialCards(this,' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'EnumeratePlannedCardActions(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   ! rg -Fq 'cardJobs.PrepareSerialPotions(this)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'EnumeratePlannedPotionActions(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Serial and parallel card/potion paths do not share the expansion plan")
fi
if ! rg -Fq 'cardJobs.RunSerialCardAction(this, cardJob)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'CreatePlannedPotionChild(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'CreatePlannedCardChild(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   ! rg -Fq 'CreatePlannedPotionChild(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Card and potion children must use the shared construction path")
fi
if ! rg -Fq 'cardJobs.RunSerialCardAction(this, cardJob)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'TryResolvePlannedCardChoices(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Serial and parallel paths must share ordinary card choice dispatch")
fi
if ! rg -Fq 'TryAdmitExpansionParent(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'TryAdmitExpansionParent(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Serial and parallel paths duplicate parent expansion admission")
fi
if ! rg -Fq 'ProcessExpandedCardCandidate(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'ProcessExpandedCardCandidate(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   rg -Fq 'AddNonDominatedParallelCandidate(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Serial and parallel card admission remains duplicated")
fi
if ! rg -Fq 'TryAdmitPlannedPotionChild(' "$repository_root/src/Search/CombatBeamSolver.ExpansionPlan.cs" ||
   ! rg -Fq 'TryAdmitPlannedPotionChild(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'TryAdmitPlannedPotionChild(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Serial and parallel potion admission remains duplicated")
fi
if ! rg -Fq 'AdmitPlannedEndTurnChildren(' "$repository_root/src/Search/CombatBeamSolver.ExpansionPlan.cs" ||
   ! rg -Fq 'AdmitPlannedEndTurnChildren(cycleExitBatch)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'AdmitPlannedEndTurnChildren(batch)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'AdmitPlannedEndTurnChildren(batch)' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("End-turn candidate admission has multiple owners")
fi
if [[ ! -f "$repository_root/src/Search/CombatBeamSolver.ExpansionExecutor.cs" ]] ||
   ! rg -Fq 'private interface IExpansionExecutor' "$repository_root/src/Search/CombatBeamSolver.ExpansionExecutor.cs" ||
   ! rg -Fq 'private sealed class SerialExpansionExecutor' "$repository_root/src/Search/CombatBeamSolver.ExpansionExecutor.cs" ||
   ! rg -Fq 'ParallelExpansionExecutor : IExpansionExecutor' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   ! rg -Fq 'serialExpansionExecutor.Execute(' "$repository_root/src/Search/CombatBeamSolver.Phases.cs" ||
   ! rg -Fq 'parallelExpansionExecutor!.Execute(' "$repository_root/src/Search/CombatBeamSolver.Phases.cs"; then
    violations+=("Serial and parallel expansion do not share the executor contract")
fi
if ! rg -Uq '    }\r?\n\s*\r?\n    private readonly record struct ChoiceJob\(' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -q '^    private sealed class AdmittedParent\(' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'private sealed class AdmittedJobScheduler(' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'public void RunSerialCardAction(CombatBeamSolver solver, AdmittedExpansionJob job)' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'public void RunSerialChoiceJob(CombatBeamSolver solver, AdmittedExpansionJob job)' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'public IEnumerable<SearchNode> RunSerialPotionJob(' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'public IEnumerable<SearchNode> RunSerialEndTurnJob(' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'scheduler.NextJob(laneIndex)' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   rg -Fq 'AdmittedExpansionJob? NextJob(int laneIndex)' "$repository_root/src/Search/CombatBeamSolver.AdmittedExpansion.cs" ||
   ! rg -Fq 'scheduler.NextJob(0, SerialJobPhase.Prepare)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'scheduler.NextJob(0, SerialJobPhase.Card)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'scheduler.NextJob(0, SerialJobPhase.Potion)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'scheduler.NextJob(0, SerialJobPhase.Tail)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs"; then
    violations+=("Admitted parent job state must belong to CombatBeamSolver, outside the parallel executor")
fi
if ! rg -Fq 'private sealed class PreparedPotionChoiceWork(' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   ! rg -Fq 'work.Resolve(this, node, baseAction)' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs" ||
   ! rg -Fq 'work.Resolve(this, node, planned.Action)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs"; then
    violations+=("Serial and parallel potion choice replay must use the same prepared work owner")
fi
if ! rg -Fq 'IEnumerable<PreparedCardAction> EnumeratePlannedCardActions(' "$repository_root/src/Search/CombatBeamSolver.ExpansionPlan.cs" ||
   rg -Fq 'PredictedCard? sourceCard' "$repository_root/src/Search/CombatBeamSolver.ExpansionPlan.cs" ||
   ! rg -Fq 'cardJobs.RunSerialCardAction(this, cardJob)' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   rg -Fq 'TryResolvePlannedCardChoices(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs" ||
   ! rg -Fq 'TryResolvePlannedCardChoices(node, action, probeSnapshot,' "$repository_root/src/Search/CombatBeamSolver.ParallelExpansion.cs"; then
    violations+=("Card choice replay and requirements must use the shared planned action path")
fi
if ! rg -Fq 'DirectSearchPurpose' "$repository_root/src/Search/SearchRequestWorkTotals.cs" ||
   ! rg -Fq 'DirectSearchPurpose? directSearchPurpose' "$repository_root/src/Search/CombatBeamSolver.cs" ||
   ! rg -Fq 'DirectSearchPurpose.RefinementBeam' "$repository_root/src/Search/CombatSearchCoordinator.cs" ||
   ! rg -Fq 'DirectSearchPurpose.PrimaryBeam' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Primary and refinement Beam work attribution missing")
fi
if ! rg -Fq 'DirectSearchPurpose.PotionFreeAudit' "$repository_root/src/Search/CombatSearchCoordinator.cs" ||
   ! rg -Fq 'DirectSearchPurpose.PotionFreeAudit' "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs" ||
   ! rg -Fq 'DirectSearchPurpose.RequiredPotionAudit' "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs" ||
   ! rg -Fq 'DirectSearchPurpose.SmartPotionGradient' "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs" ||
   ! rg -Fq 'DirectSearchPurpose.TurnBoundaryDiscovery' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'DirectSearchPurpose.EarlyTurnScout' "$repository_root/src/Search/CombatSearchCoordinator.EarlyTurnExploration.cs" ||
   ! rg -Fq 'DirectSearchPurpose.NoveltyExploration' "$repository_root/src/Search/CombatSearchCoordinator.NoveltyPortfolio.cs" ||
   ! rg -Fq 'DirectSearchPurpose.AdaptiveNoveltyRefinement' "$repository_root/src/Search/CombatSearchCoordinator.NoveltyPortfolio.cs"; then
    violations+=("Direct search work attribution missing")
fi
for plan_source in src/Search/PlanCommitment.cs src/Search/PlanMechanismRegistry.cs src/Search/CombatSearchCoordinator.PlanSearch.cs; do
    [[ -f "$repository_root/$plan_source" ]] || violations+=("Plan search boundary missing: $plan_source")
done
if ! rg -Fq 'context.Budget.RequestWindow(' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'ContinuationPurpose.PlanCommitment' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'IsBetterPotionPolicyResult(' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'TryRunPlanMember(' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs"; then
    violations+=("Plan search bypasses shared budget, continuation or final quality policy")
fi
if ! rg -Fq 'PlanMechanismRegistry.Default.DeferredCopies' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'BuildOpeningCopyActionsAfterPrefix(' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   rg -Fq 'PlanChoiceEffect.Nightmare' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   rg -Fq 'OpeningCandidatePurpose.NightmareCopyCard' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'HasPlayableChoiceEffect(' "$repository_root/src/Search/CombatBeamSolver.Expansion.Opening.cs"; then
    violations+=("Plan copy discovery must follow registered choice semantics")
fi
if ! rg -Fq 'ImmutableArray<DeferredCopyPlanRule>' "$repository_root/src/Search/PlanMechanismRegistry.cs" ||
   ! rg -Fq 'PlanChoiceEffect.Nightmare' "$repository_root/src/Search/PlanMechanismRegistry.cs"; then
    violations+=("Deferred copy plan rules must be internally immutable and registered")
fi
if ! rg -Fq 'PlanPayoffEvidenceKind' "$repository_root/src/Search/PlanCommitment.cs" ||
   ! rg -Fq 'FreePotionUsed' "$repository_root/src/Search/PlanCommitment.cs" ||
   ! rg -Fq 'RegisteredPowerBenefit' "$repository_root/src/Search/PlanCommitment.cs" ||
   rg -Fq 'PayoffCardId' "$repository_root/src/Search/PlanCommitment.cs"; then
    violations+=("Plan payoff evidence must be typed and shared across mechanisms")
fi
if ! rg -Fq 'PlanCommitmentKind.PotionChain' "$repository_root/src/Search/CombatSearchCoordinator.PotionChain.cs" ||
   ! rg -Fq 'PlanPayoffEvidenceKind.FreePotionUsed' "$repository_root/src/Search/CombatSearchCoordinator.PotionChain.cs" ||
   ! rg -Fq 'Commitment = plan' "$repository_root/src/Search/CombatSearchCoordinator.PotionChain.cs"; then
    violations+=("Generated free-potion chain must carry plan payoff evidence")
fi
if ! rg -Fq 'RunDeferredPowerPlanSearchPass(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'EarlyTurnScoutDepth = 1' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs" ||
   ! rg -Fq 'PlanCommitmentKind.CrossTurnBenefit' "$repository_root/src/Search/CombatSearchCoordinator.PlanSearch.cs"; then
    violations+=("Deferred power plan must use the ordered post-search continuation")
fi
if ! rg -Fq 'AdmitPlanCommitmentRepresentatives(' "$repository_root/src/Search/CombatBeamSolver.BeamRetentionPolicy.cs" ||
   ! rg -Fq '_planCommitment' "$repository_root/src/Search/CombatBeamSolver.BeamRetentionPolicy.cs"; then
    violations+=("Plan commitment has no protected retention representatives")
fi
if [[ ! -f "$repository_root/src/Search/PlanHorizonPolicy.cs" ]] ||
   ! rg -Fq 'PlanHorizonPolicy.ShouldExtend(' "$repository_root/src/Search/CombatBeamSolver.Expansion.cs"; then
    violations+=("Realized plans have no bounded horizon extension")
fi
[[ -f "$repository_root/src/Search/SearchBudgetLedger.cs" ]] || violations+=("Search budget ledger missing")
if ! rg -Fq 'internal readonly record struct SearchBudgetWindow' "$repository_root/src/Search/SearchBudgetLedger.cs" ||
   ! rg -Fq 'internal SearchBudgetWindow RequestWindow(' "$repository_root/src/Search/SearchBudgetLedger.cs"; then
    violations+=("Request budget window missing")
fi
[[ -f "$repository_root/src/Search/SearchPassContext.cs" ]] || violations+=("Search pass context missing")
[[ -f "$repository_root/src/Search/SearchPassResult.cs" ]] || violations+=("Search pass result missing")
for pass_field in 'RouteQuality? Quality' 'SearchRequestWorkSnapshot WorkTotals' \
    'SolverResultScope PassScope' 'SearchBoundaryReason PassBoundary'; do
    if ! rg -Fq "$pass_field" "$repository_root/src/Search/SearchPassResult.cs"; then
        violations+=("Search pass result contract missing: $pass_field")
    fi
done
[[ -f "$repository_root/src/Search/SearchRequestPipeline.cs" ]] || violations+=("Search request pipeline missing")
[[ -f "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ]] || violations+=("Post-search passes missing")
[[ -f "$repository_root/src/Search/CombatSearchCoordinator.BeamPortfolio.cs" ]] || violations+=("Beam portfolio owner missing")
[[ -f "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs" ]] || violations+=("Supplemental audit owner missing")
for continuation_input in src/Search/FrontierContinuationScheduler.cs src/Search/OpeningPotionPairContinuationSource.cs src/Search/EarlierCopyDelayedDamageContinuationSource.cs src/Search/OpeningNoCostContinuationSource.cs src/Search/TurnEndChoiceContinuationSource.cs src/Search/SinglePrefixContinuationSource.cs src/Search/TurnBoundaryContinuationSource.cs; do
    [[ -f "$repository_root/$continuation_input" ]] || violations+=("Frontier continuation component missing: $continuation_input")
done
if [[ $(wc -l < "$repository_root/src/Search/CombatSearchCoordinator.cs") -gt 1200 ]]; then
    violations+=("Search coordinator main file exceeds the P3 1200-line ownership limit")
fi
if ! rg -Fq 'SearchBudgetLedger ledger = new(' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Search coordinator does not own a request budget ledger")
fi
if rg -Fq 'policy.RequestWorkTotals?.Snapshot().ExpandedNodes ?? 0L' "$repository_root/src/Search/CombatSearchCoordinator.cs" ||
   rg -Fq -- '- (int)passClock.ElapsedMilliseconds' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Primary pass prefix budgets bypass the request budget ledger")
fi
if rg -Fq 'MaxExpandedNodes = (int)Math.Min(' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Primary pass member budget bypasses the pass budget window")
fi
for purpose in EarlyDiscardBeforeGeneration OpeningTargetVariant \
    OpeningTargetPowerVariant OpeningTargetPowerDefensiveVariant \
    DeferredOpeningPower FreeAttackHandSetup; do
    if ! rg -Fq "ContinuationPurpose.$purpose" "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
        violations+=("Primary pass fixed-prefix request missing: $purpose")
    fi
done
for purpose in OpeningResourceDefense PotionResourcePosterior \
    PotionPowerPosterior PotionPowerDefensivePosterior; do
    if ! rg -Fq "ContinuationPurpose.$purpose" "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs"; then
        violations+=("Opening audit fixed-prefix request missing: $purpose")
    fi
done
for purpose in RequiredOpeningPotion RequiredPotionPair \
    RequiredPotionPairDefensive; do
    if ! rg -Fq "ContinuationPurpose.$purpose" "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs"; then
        violations+=("Required potion audit fixed-prefix request missing: $purpose")
    fi
done
if rg -Fq 'fixedPrefixActions:' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Search coordinator constructs a fixed-prefix solver outside the scheduler")
fi
if rg -Fq 'SearchRequestWorkTotals requestWorkTotals = new()' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Search coordinator creates request work totals outside the budget ledger")
fi
if ! rg -Fq 'RunSupplementalAudits(auditContext,' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Supplemental audits do not consume the search pass context")
fi
if ! rg -Fq 'SearchPassResult RunSearchPass(' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Search pass does not return its termination state")
fi
if ! rg -Fq 'SearchPassResult RunSearchPass(SearchPassContext passContext)' "$repository_root/src/Search/CombatSearchCoordinator.cs" ||
   ! rg -Fq 'new SearchRequestPipeline(requestContext, RunSearchPass, postSearch).Run()' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Primary search pass does not consume the search pass context")
fi
if ! rg -Fq 'RunEarlyPotionPairRescue(context, selected)' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'RunEarlyPotionPairRescue(SearchPassContext context, SolverResult selected)' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   rg -Fq 'EARLY_POTION_PAIR prefix=' "$repository_root/src/Search/CombatSearchCoordinator.cs"; then
    violations+=("Early potion pair rescue is not a post-search pass")
fi
if ! rg -Fq 'new FrontierContinuationScheduler(context).Run(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'RequestWindow(' "$repository_root/src/Search/FrontierContinuationScheduler.cs" ||
   ! rg -Fq 'fixedPrefixActions: request.Prefix' "$repository_root/src/Search/FrontierContinuationScheduler.cs"; then
    violations+=("Early potion pair rescue bypasses the frontier continuation scheduler")
fi
if ! rg -Fq 'SolverResult Dispatch(ContinuationSearchRequest request)' "$repository_root/src/Search/FrontierContinuationScheduler.cs" ||
   ! rg -Fq 'result = Dispatch(request);' "$repository_root/src/Search/FrontierContinuationScheduler.cs" ||
   ! rg -Fq 'CombatBeamSolver CreateSolver(ContinuationSearchRequest request)' "$repository_root/src/Search/FrontierContinuationScheduler.cs"; then
    violations+=("Frontier continuation request does not own solver construction")
fi
if ! rg -Fq 'new EarlierCopyDelayedDamageContinuationSource(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'context, selected.BestNode.Actions)' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Earlier copy potion rescue bypasses the frontier continuation scheduler")
fi
if ! rg -Fq 'OpeningNoCostContinuationSource source = new(context);' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'source.DeduplicatePrefixes' "$repository_root/src/Search/FrontierContinuationScheduler.cs"; then
    violations+=("No-cost opening rescue bypasses source-specific continuation identity")
fi
if ! rg -Fq 'new TurnEndChoiceContinuationSource(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'new SinglePrefixContinuationSource(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Turn-end choice posterior bypasses the frontier continuation scheduler")
fi
if ! rg -Fq 'new TurnBoundaryContinuationSource(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'optionalPotionDiagnostic' "$repository_root/src/Search/FrontierContinuationScheduler.cs"; then
    violations+=("Turn-boundary rescue bypasses the frontier continuation scheduler")
fi
if rg -Fq 'fixedPrefixActions: combinedPrefix,' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   rg -Fq 'fixedPrefixActions: [.. nextTurnPrefix, nextAttack,' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Turn-boundary follow-up bypasses fixed-prefix request dispatch")
fi
if rg -Fq 'fixedPrefixActions: focusedOpening,' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   rg -Fq 'fixedPrefixActions: reordered,' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Forced potion opening bypasses fixed-prefix request dispatch")
fi
if rg -Fq 'fixedPrefixActions:' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Post-search pass constructs a fixed-prefix solver outside the scheduler")
fi
if ! rg -Fq 'SearchBudgetWindow discoveryWindow = ledger.RequestWindow(policy.Profile);' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'SearchBudgetWindow continuationWindow = ledger.RequestWindow(policy.Profile);' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
   ! rg -Fq 'SearchBudgetWindow reorderedWindow = ledger.RequestWindow(policy.Profile);' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Forced potion opening rescue bypasses the request budget window")
fi
if rg -Fq 'MaxExpandedNodes = (int)Math.Min(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Post-search member budget bypasses the request budget window")
fi
for pass in RunForcedPotionOpeningRescue RunTurnBoundaryRescue \
    RunZeroCostOpeningRescue RunMidCombatRefinement \
    RunTurnEndChoicePosterior RunEarlierCopyDelayedDamage; do
    if ! rg -Fq "$pass(context, selected" "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs" ||
       ! rg -Fq "SolverResult $pass(" "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
        violations+=("Post-search pass ownership missing: $pass")
    fi
done
if ! rg -Fq 'RunEarlyTurnExploration(context, selected)' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Early-turn exploration bypasses the search pass context")
fi
if ! rg -Fq '_postSearch(' "$repository_root/src/Search/SearchRequestPipeline.cs" ||
   ! rg -Fq 'RunPostSearchPasses(' "$repository_root/src/Search/CombatSearchCoordinator.PostSearch.cs"; then
    violations+=("Post-search passes bypass the search request pipeline")
fi
if ! rg -Fq 'CombatSearchCoordinator.EscalateSearchWhenNoVictory(' "$repository_root/src/Search/SearchRequestPipeline.cs" ||
   ! rg -Fq '_context,' "$repository_root/src/Search/SearchRequestPipeline.cs"; then
    violations+=("No-victory escalation does not consume the search request context")
fi
if ! rg -Fq 'internal static SearchPassResult EscalateSearchWhenNoVictory(' "$repository_root/src/Search/CombatSearchCoordinator.FailureRecovery.cs"; then
    violations+=("No-victory escalation does not return search pass state")
fi
if ! rg -Fq 'Func<SearchPassContext, SearchPassResult> runPass' "$repository_root/src/Search/CombatSearchCoordinator.FailureRecovery.cs"; then
    violations+=("No-victory escalation does not dispatch a search pass context")
fi
if ! rg -Fq 'Quality = selectedPass.Quality' "$repository_root/src/Search/CombatSearchCoordinator.FailureRecovery.cs"; then
    violations+=("No-victory escalation does not retain the selected route quality")
fi
if ! rg -Fq 'RunOpeningNightmarePortfolio(' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs" ||
   ! rg -Fq 'RunOpeningPowerRoutePortfolio(' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs" ||
   rg -Fq 'policy.RequestWorkTotals?.Snapshot()' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs"; then
    violations+=("Opening route passes bypass the search pass budget ledger")
fi
if ! rg -Fq 'context.Budget.ProfileWindow(profile)' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs" ||
   rg -Fq 'Math.Min(30_000L, remainingNodes)' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs"; then
    violations+=("Nightmare opening member bypasses the profile budget window")
fi
if rg -Fq 'fixedPrefixActions:' "$repository_root/src/Search/CombatSearchCoordinator.PowerRoutes.cs"; then
    violations+=("Opening power routes construct a fixed-prefix solver outside the scheduler")
fi
if rg -Fq 'fixedPrefixActions:' "$repository_root/src/Search/CombatSearchCoordinator.EarlyTurnExploration.cs" ||
   ! rg -Fq 'ContinuationPurpose.EarlyTurnContinuation' "$repository_root/src/Search/CombatSearchCoordinator.EarlyTurnExploration.cs"; then
    violations+=("Early-turn experiment bypasses fixed-prefix request dispatch")
fi
if ! rg -Fq 'RunNoveltyPortfolioPass(' "$repository_root/src/Search/CombatSearchCoordinator.NoveltyPortfolio.cs" ||
   ! rg -Fq 'SearchPassContext context,' "$repository_root/src/Search/CombatSearchCoordinator.NoveltyPortfolio.cs" ||
   rg -Fq 'policy.RequestWorkTotals' "$repository_root/src/Search/CombatSearchCoordinator.NoveltyPortfolio.cs"; then
    violations+=("Novelty portfolio bypasses the search pass context or budget ledger")
fi
if ! rg -Fq 'RunBeamWidthPortfolioPass(' "$repository_root/src/Search/CombatSearchCoordinator.cs" ||
   ! rg -Fq 'SearchRequestWorkTotals totals = context.Budget.WorkTotals;' "$repository_root/src/Search/CombatSearchCoordinator.BeamPortfolio.cs"; then
    violations+=("Beam portfolio bypasses the search pass budget ledger")
fi
for audit in AuditRequiredPotionUse AuditSmartPotionUse AuditOpeningPowerUse; do
    if ! rg -Uq "private static SolverResult ${audit}\\(\\s*SearchPassContext context" "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs"; then
        violations+=("Supplemental audit bypasses the search pass context: $audit")
    fi
done
if rg -Fq 'policy.RequestWorkTotals?.Snapshot()' "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs" ||
   rg -Fq 'policy.RequestWorkTotals?.RecordCoordinatorOverhead(' "$repository_root/src/Search/CombatSearchCoordinator.Audits.cs"; then
    violations+=("Potion gradient work accounting bypasses the request budget ledger")
fi
for quality_contract in \
    'src/Search/SolverInterimResultOrdering.cs|RouteQualityProjection.Interim' \
    'src/Search/CombatSearchCoordinator.cs|RouteQualityProjection.PotionPolicy' \
    'src/Search/CombatBeamSolver.FinalPlanOrdering.cs|candidate.Quality.StrategicHpDeficit' \
    'src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs|RouteQualityProjection.Primary' \
    'src/Search/CombatBeamSolver.Retention.cs|RouteQualityProjection.RetentionCost'; do
    relative="${quality_contract%%|*}"
    marker="${quality_contract#*|}"
    rg -Fq "$marker" "$repository_root/$relative" || violations+=("Route quality projection missing: $relative")
done

usage() {
    cat <<'EOF'
Usage: verify-refactor-boundaries.sh

Checks the repository's source ownership and architecture boundaries.
EOF

}

if (($# > 0)); then
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "verify-refactor-boundaries.sh: unknown argument: $1" >&2
            exit 2
            ;;
    esac
fi

command -v rg >/dev/null 2>&1 || {
    echo "verify-refactor-boundaries.sh: rg is required" >&2
    exit 1
}

add_violation() {
    violations+=("$1")
}

contains_fixed() {
    local path="$1"
    local text="$2"
    local status

    [[ -f "$path" ]] || {
        echo "verify-refactor-boundaries.sh: verification input is missing: $path" >&2
        exit 1
    }

    set +e
    rg --quiet --ignore-case --fixed-strings -- "$text" "$path"
    status=$?
    set -e
    if ((status > 1)); then
        echo "verify-refactor-boundaries.sh: rg failed for $path" >&2
        exit "$status"
    fi
    return "$status"
}

require_fixed() {
    local path="$1"
    local text="$2"
    local message="$3"
    if ! contains_fixed "$path" "$text"; then
        add_violation "$path: $message '$text'"
    fi
}

strategy_search="$search_root/DevelopmentSearchStrategy.cs"
strategy_loader="$repository_root/src/Testing/Host/DevelopmentStrategyLoader.cs"
monitor_publisher="$repository_root/src/Testing/Host/DevelopmentMonitorPublisher.cs"
require_fixed "$monitor_publisher" "PeriodicTimer" "Monitor update cadence missing"
require_fixed "$monitor_publisher" "CurrentBestResult" "Monitor scalar result projection missing"
require_fixed "$monitor_publisher" "File.Move(temp, path, true)" "Monitor atomic publication missing"
for monitor_entry in strategy-monitor.ps1 strategy-monitor.sh strategy-monitor-view.sh; do
    [[ -f "$repository_root/tools/replay/$monitor_entry" ]] || add_violation "Development monitor entry missing: $monitor_entry"
done
require_fixed "$strategy_search" "IDevelopmentSearchStrategy" "Strategy search contract missing"
require_fixed "$strategy_search" "StrategyNodeFeatures" "Strategy node view missing"
require_fixed "$strategy_search" "OrganizeMembers(" "Strategy member hook missing"
for forbidden in AssemblyLoadContext File.ReadAllText SolverSettings.Current; do
    if contains_fixed "$strategy_search" "$forbidden"; then
        add_violation "$strategy_search: strategy search contract owns runtime loading: $forbidden"
    fi
done
require_fixed "$strategy_loader" "AssemblyLoadContext(isCollectible: true)" "Collectible loader missing"
require_fixed "$strategy_loader" "DevelopmentSearchStrategy(script" "Strategy loader handoff missing"
require_fixed "$strategy_loader" "_context.Unload()" "Strategy unload missing"
require_fixed "$repository_root/tools/testing/run-unattended-test.ps1" "developmentStrategyAssemblyPath" "Windows strategy request wire missing"
require_fixed "$repository_root/tools/testing/run-unattended-test.sh" "development-strategy-assembly-path" "Linux strategy request wire missing"

forbid_fixed() {
    local path="$1"
    local text="$2"
    local message="$3"
    local matches
    local status
    local match_path
    local line_number
    local ignored

    [[ -f "$path" ]] || {
        echo "verify-refactor-boundaries.sh: verification input is missing: $path" >&2
        exit 1
    }

    set +e
    matches="$(rg --line-number --with-filename --ignore-case --fixed-strings -- "$text" "$path")"
    status=$?
    set -e
    if ((status > 1)); then
        echo "verify-refactor-boundaries.sh: rg failed for $path" >&2
        exit "$status"
    fi
    if ((status == 0)); then
        while IFS=: read -r match_path line_number ignored; do
            add_violation "$match_path:$line_number: $message '$text'"
        done <<<"$matches"
    fi
}

forbid_regex() {
    local path="$1"
    local pattern="$2"
    local message="$3"
    local matches
    local status
    local match_path
    local line_number
    local ignored

    [[ -f "$path" ]] || {
        echo "verify-refactor-boundaries.sh: verification input is missing: $path" >&2
        exit 1
    }

    set +e
    matches="$(rg --line-number --with-filename --ignore-case -- "$pattern" "$path")"
    status=$?
    set -e
    if ((status > 1)); then
        echo "verify-refactor-boundaries.sh: rg failed for $path" >&2
        exit "$status"
    fi
    if ((status == 0)); then
        while IFS=: read -r match_path line_number ignored; do
            add_violation "$match_path:$line_number: $message"
        done <<<"$matches"
    fi
}

forbid_fixed "$search_root/CombatBeamSolver.Phases.cs" 'CaptureContinuation(node)' \
    'only the selected route may build continuation stamps'
require_fixed "$search_root/CombatBeamSolver.Terminal.cs" 'ContinuationStamp.CapturePredicted(' \
    'Terminal must build the selected route continuation stamp'
for required in 'PrepareContinuationCapture(best)' 'continuationCapture: continuationCapture' 'continuationCapture.Complete()'; do
    require_fixed "$search_root/CombatBeamSolver.Phases.cs" "$required" \
        'selected route must capture continuations in its annotation replay'
done
forbid_fixed "$search_root/CombatBeamSolver.Terminal.cs" 'Replay(node.Actions' \
    'continuation capture must not replay every selected turn prefix'

for relative_path in \
    src/Search/CombatBeamSolver.Expansion.cs \
    src/Search/CombatBeamSolver.Expansion.Candidates.cs \
    src/Search/CombatBeamSolver.Expansion.Choices.cs \
    src/Search/CombatBeamSolver.Expansion.Opening.cs \
    src/Search/CombatBeamSolver.Expansion.Replay.cs \
    src/Runtime/LiveEndTurnRiskEvaluator.cs \
    src/Testing/Host/UnattendedTestRunner.cs \
    src/Testing/Support/UnattendedTestRunner.Potions.cs; do
    for reference in \
        'CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(' \
        'TurnStartRelicSupport.TriggerAfterSideTurnEnd(' \
        'HookMirrors.AfterSideTurnEndLate('; do
        forbid_fixed "$repository_root/$relative_path" "$reference" \
            'player phase two must use PlayerTurnEndLifecycle'
    done
done

mapfile -d '' -t search_files < <(
    find "$search_root" -type f -name '*.cs' -print0 | sort -z
)
shopt -s nullglob
beam_files=("$search_root"/CombatBeamSolver*.cs)
runtime_files=("$repository_root"/src/Runtime/*.cs)
shopt -u nullglob

cycle_policy_paths=(
    "$search_root/CombatBeamSolver.CyclePlanning.cs"
    "$search_root/CombatBeamSolver.CycleRegionRetention.cs"
    "$search_root/CombatBeamSolver.CycleReplay.cs"
    "$search_root/CombatBeamSolver.OrderedMutationRetention.cs"
)
legacy_loop_guard_paths=(
    "$search_root/CombatBeamSolver.Expansion.cs"
    "$search_root/CombatBeamSolver.Expansion.Candidates.cs"
    "$search_root/CombatBeamSolver.Expansion.Choices.cs"
    "$search_root/CombatBeamSolver.Expansion.Opening.cs"
    "$search_root/CombatBeamSolver.Expansion.Replay.cs"
    "$search_root/CombatBeamSolver.ParallelExpansion.cs"
    "$search_root/SolverWeights.cs"
)

((${#search_files[@]} > 0)) || {
    echo "verify-refactor-boundaries.sh: no Search source files were found" >&2
    exit 1
}
((${#beam_files[@]} > 0)) || {
    echo "verify-refactor-boundaries.sh: no CombatBeamSolver source files were found" >&2
    exit 1
}

# Cycle planning must infer recurrence and payoff from generic simulated-state deltas. Keeping
# scenario names out of this policy file prevents a regression to card/power/relic/enemy allowlists.
scenario_specific_cycle_model_pattern='\b(?:Body[[:space:]_.-]*Slam|Lunar[[:space:]_.-]*Blast|Gold[[:space:]_.-]*Axe|Slow[[:space:]_.-]*Power|Hellraiser|Pillage|Bloodletting|Particle[[:space:]_.-]*Wall|Pale[[:space:]_.-]*Blue[[:space:]_.-]*Dot|Flash[[:space:]_.-]*Of[[:space:]_.-]*Steel|Finesse|Speedster|Black[[:space:]_.-]*Hole|Glow|Alignment|Spoils[[:space:]_.-]*Of[[:space:]_.-]*Battle)\b'
for cycle_policy_path in "${cycle_policy_paths[@]}"; do
    forbid_regex \
        "$cycle_policy_path" \
        "$scenario_specific_cycle_model_pattern" \
        'generic cycle planning contains a scenario-specific model name or ID:'
    for direct_model_lookup_pattern in \
        '\bModelDb\.(?:Card|Power|Relic|Monster)\b' \
        '\bGetAmount<[A-Za-z_][A-Za-z0-9_]*(?:Power|Relic|Monster)>' \
        '\btypeof\([A-Za-z_][A-Za-z0-9_]*(?:Card|Power|Relic|Monster)\)'; do
        forbid_regex \
            "$cycle_policy_path" \
            "$direct_model_lookup_pattern" \
            'generic cycle planning performs a direct concrete-model lookup:'
    done
done

cycle_region_retention_path="$search_root/CombatBeamSolver.CycleRegionRetention.cs"
for cycle_transaction_rule in \
    'CycleRegionRetentionTransaction' \
    'CloneCycleRegionLedger(' \
    'ObservationBaseline' \
    'FindBestCycleRegionProgressWitness(' \
    'lanePriority: -1' \
    'SelectCycleRegionAdmissionKind(' \
    'normalAdmissionSucceeded' \
    'HasActiveOrderedMutationCycleRegionAdmission(' \
    'node.CycleExitRetentionRank != int.MaxValue'; do
    require_fixed \
        "$cycle_region_retention_path" \
        "$cycle_transaction_rule" \
        'cycle-region final-survivor transaction invariant is missing:'
done
for retired_cycle_ordered_coupling in \
    'CycleRegionOrderedProgressTail' \
    'OrderCycleRegionOrderedMutationLane(' \
    'TryStageCycleRegionOrderedProgressTailAdmission('; do
    forbid_fixed \
        "$cycle_region_retention_path" \
        "$retired_cycle_ordered_coupling" \
        'retired cycle-region/ordered joint ledger returned:'
done
require_fixed \
    "$search_root/CombatBeamSolver.Retention.cs" \
    'FinalizeCycleRegionRetention(cycleRegionTransaction, finalized);' \
    'cycle-region provisional admissions are no longer reconciled after final arbitration:'
for ordered_transaction_rule in \
    'MaximumOrderedMutationRunAdmissions = 2048' \
    'HasFullyPendingAtomicOrderedMutationPair(' \
    'ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(node);' \
    'PendingOrderedMutationOrdinaryFallbackNodes' \
    'ValidateOrderedMutationAdmissionLedger(' \
    'typeof(OrderedMutationRetentionLease).IsValueType'; do
    require_fixed \
        "$search_root/CombatBeamSolver.OrderedMutationRetention.cs" \
        "$ordered_transaction_rule" \
        'ordered-mutation atomic accounting invariant is missing:'
done
for ordered_coordinator_rule in \
    'BuildOrderedMutationContinuationAdmissionLease(candidate);' \
    'Every independent retention channel must finish before the ordered coordinator.' \
    'Any inherited lane left outside this prune' \
    'HasOrdinaryAnchor'; do
    if [[ "$ordered_coordinator_rule" == 'Every independent retention channel must finish before the ordered coordinator.' ]]; then
        ordered_coordinator_path="$search_root/CombatBeamSolver.Retention.cs"
    elif [[ "$ordered_coordinator_rule" == 'BuildOrderedMutationContinuationAdmissionLease(candidate);' ]]; then
        ordered_coordinator_path="$search_root/CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs"
    else
        ordered_coordinator_path="$search_root/CombatBeamSolver.BeamRetentionPolicy.OrderedMutation.cs"
    fi
    require_fixed \
        "$ordered_coordinator_path" \
        "$ordered_coordinator_rule" \
        'unified ordered-mutation coordinator invariant is missing:'
done
for ordered_metric in \
    'ordered_admitted=' \
    'ordered_lease_expired_budget=' \
    'ordered_ordinary_fallback=' \
    'cold_atomic_committed=' \
    'cold_atomic_rejected='; do
    require_fixed \
        "$repository_root/src/Runtime/SolverDiagnostics.cs" \
        "$ordered_metric" \
        'ordered-mutation acceptance metric is missing:'
done
ordered_coordinator_line="$(rg --line-number --fixed-strings \
    'Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);' \
    "$search_root/CombatBeamSolver.Retention.cs" | head -n 1 | cut -d: -f1)"
cycle_region_line="$(rg --line-number --fixed-strings \
    'cycleRegionTransaction = ApplyCycleRegionRetention(' \
    "$search_root/CombatBeamSolver.Retention.cs" | head -n 1 | cut -d: -f1)"
if [[ -z "$ordered_coordinator_line" \
    || -z "$cycle_region_line" \
    || "$ordered_coordinator_line" -ge "$cycle_region_line" ]]; then
    add_violation \
        "$search_root/CombatBeamSolver.Retention.cs: ordered admission must settle before CycleRegion"
fi
forbid_fixed \
    "$search_root/CombatBeamSolver.Retention.cs" \
    'List<List<SearchNode>> openingChannels = pool' \
    'legacy additive opening-power channel returned:'
require_fixed \
    "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" \
    'AdmitPowerCommitmentRepresentatives(quotaPool, ranked, required, limit);' \
    'bounded power commitment replacement is missing:'
forbid_fixed \
    "$cycle_region_retention_path" \
    'selectedSet.Add(node);' \
    'CycleRegion rebuilt an O(pool) selected-set shadow:'

# PR #28's fixed repeat count and named payoff exceptions are retired. These checks intentionally
# stay scoped to expansion and policy files so unrelated combat-semantic mirrors remain legal.
for legacy_loop_guard_path in "${legacy_loop_guard_paths[@]}"; do
    for retired_loop_guard in \
        'MaxRepeatableNoProgressPlays' \
        'IsRepeatableNoProgressStep' \
        'ShouldPruneRepeatableNoProgress' \
        'RepeatableNoProgressCardId' \
        'RepeatableNoProgressCount'; do
        forbid_fixed \
            "$legacy_loop_guard_path" \
            "$retired_loop_guard" \
            'retired fixed repeatable-no-progress guard returned:'
    done
    forbid_regex \
        "$legacy_loop_guard_path" \
        '\b(?:Body[[:space:]_.-]*Slam|Lunar[[:space:]_.-]*Blast|Gold[[:space:]_.-]*Axe|Slow[[:space:]_.-]*Power)\b' \
        'retired named loop-payoff exception returned:'
done

require_fixed "$repository_root/src/Engine/InCombat/Mirrors/Hooks/Card/ShouldPlayMirrors.cs" \
    'registry.Register<Normality>(HandleNormality)' \
    'Normality must use the shared ShouldPlay mirror for manual and automatic cards.'

pool_lifetime="$repository_root/src/Runtime/NodePoolSignalLifetimePatch.cs"
for required in 'using ((Godot.Collections.Array)signals)' 'using var ownedArray' 'using (connection)' 'using (callable.Method)' 'using (signal.Name)'; do
    require_fixed "$pool_lifetime" "$required" 'Node pool wrapper ownership missing:'
done
for forbidden in 'GC.Collect' 'QueueFree'; do
    forbid_fixed "$pool_lifetime" "$forbidden" 'Node pool signal cleanup owns temporary wrappers, not nodes or process GC:'
done

for file in "${search_files[@]}"; do
    for reference in \
        'SolverSearchPhase' \
        'ShortProfile' \
        'DeepProfile' \
        'shortCheckpointMilliseconds' \
        'SolveWithNarrowBeamRecovery' \
        'BuildNarrowBeamRecoveryProfile' \
        'RecoverDeferredTurnFrontier' \
        'DeferredTurnFrontier' \
        'SolverSettings.Current' \
        'Entry.Logger' \
        'SolverController' \
        'SolverOverlay' \
        'SolverText' \
        'SolverRelicEffectText' \
        'SolverUiModelNames' \
        'SolverActionTextIdentity' \
        'SolverLocaleRefresh' \
        'SolvedRouteCache' \
        'RunStatistics' \
        'UnattendedTestRunner'; do
        forbid_fixed "$file" "$reference" 'forbidden Search reference'
    done
done

semantic_files=(
    "${beam_files[@]}"
    "$repository_root/src/Engine/InCombat/Simulation/CombatPredictionDynamicVarExtensions.cs"
)
for file in "${semantic_files[@]}"; do
    forbid_regex "$file" 'catch\s*\(Exception' 'broad semantic catch is not allowed'
done

forbid_fixed \
    "$repository_root/src/Engine/InCombat/Simulation/CombatPredictionDynamicVarExtensions.cs" \
    'return 0m;' \
    'removed fallback returned:'
forbid_fixed \
    "$repository_root/src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayInferrer.cs" \
    'Inferred card mirror failed' \
    'removed fallback returned:'
for file in "${beam_files[@]}"; do
    forbid_fixed "$file" '跳过无法回放' 'removed fallback returned:'
done

controller_path="$repository_root/src/Runtime/SolverController.cs"
for field in \
    '_searchCancellation' \
    '_deploymentCancellation' \
    '_generation' \
    '_searching' \
    '_deployAfterSearch' \
    '_searchStamp' \
    '_searchProgress' \
    '_renderedProgress' \
    '_lastProgressRenderAt' \
    '_searchFrameCount' \
    '_searchFramesOver33Ms' \
    '_searchFramesOver50Ms' \
    '_searchFramesOver100Ms' \
    '_maxSearchFrameGapMs'; do
    forbid_fixed "$controller_path" "$field" 'retired controller field returned:'
done

session_path="$repository_root/src/Runtime/SolverControllerSessions.cs"
require_fixed "$repository_root/src/Prediction/PredictionModHookSubscriberCapture.cs" 'PredictionModPatchAudit.CaptureCardOnPlay' 'missing adapted OnPlay root capture'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" '_modHookSubscribers = source._modHookSubscribers;' 'missing immutable OnPlay fork ownership'
require_fixed "$repository_root/src/Runtime/ContinuationStamp.cs" 'AdaptedCardOnPlayMirrors.CaptureLiveStamp()' 'missing live OnPlay configuration check'
require_fixed "$repository_root/src/Runtime/ContinuationStamp.cs" 'adaptedOnPlay.Stamp' 'missing frozen predicted OnPlay configuration'
require_fixed "$repository_root/src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs" 'return replacement;' 'missing exclusive adapted OnPlay dispatch'
forbid_fixed "$repository_root/src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs" 'Harmony.GetPatchInfo' 'worker must not query Harmony'
forbid_fixed "$repository_root/src/Prediction/AdaptedCardOnPlayMirrors.cs" 'PredictionModPatchAudit.AuditCardOnPlay(' 'generated cards must use frozen root patch evidence'
require_fixed "$repository_root/src/Prediction/AdaptedCardOnPlayMirrors.cs" 'patchedOnPlayTargets.Contains(target)' 'missing frozen generated-card patch decision'
for session_type in SolverCombatSession SolverSearchSession SolverDeploymentSession; do
    require_fixed "$session_path" "class $session_type" 'missing controller session type'
done

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing Fork boundary'
done <<'EOF'
src/Engine/Common/PredictionForking.cs	interface IPredictionForkBoundary
src/Engine/Common/PredictionStateStore.cs	boundary.AssertForkable()
src/Prediction/ModelPredictionStateMirrors.cs	context.Register(value, typed)
src/Prediction/ModelPredictionStateMirrors.cs	boundary.AssertForkable()
src/Search/SimulatedCombatState.cs	ModelPredictionStateMirrors.CaptureRootState(simulator,
src/Search/SimulatedCombatState.cs	ModelPredictionStateMirrors.AppendPredicted(ref fingerprint,
src/Search/SimulatedCombatState.cs	_rootModifierSources = null;
src/Runtime/ContinuationStamp.cs	ModelPredictionStateMirrors.AppendLiveContinuation(text, state)
src/Runtime/ContinuationStamp.cs	ModelPredictionStateMirrors.AppendPredicted(ref adapterFingerprint,
src/Search/SimulatedCombatState.Fork.cs	_activeActionChoices
src/Search/SimulatedCombatState.Fork.cs	_activeCardExecutionDeaths
src/Engine/InCombat/Mirrors/Hooks/Card/CardPlayHookPredictionStates.cs	Cannot fork Pen Nib
src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs	Cannot fork Curl Up
EOF

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing pre-combat isolation boundary'
done <<'EOF'
src/Api/PreCombatForecastApi.cs	public static class PreCombatForecastApi
src/Runtime/Entry.cs	public static bool IsPreCombatWorker
src/Runtime/SolverDispatcher.cs	if (!Entry.IsPreCombatWorker)
src/Api/PreCombatLiveStateSnapshot.cs	RunManager.Instance.ToSave(null)
src/Api/PreCombatRunSerialization.cs	point["can_modify"] = false
src/Api/PreCombatRunSerialization.cs	eventChoice["variables"] is JsonObject { Count: 0 }
src/Api/PreCombatForecastWorker.cs	COMBATSOLVER_PRECOMBAT_WORKER
src/Api/PreCombatForecastWorker.cs	ExpectedLoadedMods = expectedMods
src/Api/PreCombatForecastWorker.cs	EnableNoGcRegionForTest = false
src/Api/PreCombatForecastWorker.cs	PreCombatInterveningMapPoints = options.InterveningMapPoints
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	EnterMapCoordDebug
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	PreCombatPlayerHp:
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	DirectRunSnapshot:ExactStateRestored
EOF

while IFS= read -r -d '' api_file; do
    for forbidden_call in \
        'SolverController.RequestSearch' \
        'CombatManager.Instance.SetUpCombat' \
        'RunManager.Instance.EnterRoomDebug'; do
        forbid_fixed "$api_file" "$forbidden_call" 'pre-combat API directly mutates live combat via'
    done
done < <(find "$repository_root/src/Api" -type f -name '*.cs' -print0 | sort -z)

search_gc_policy_path="$repository_root/src/Runtime/SearchGcPolicy.cs"
forbid_fixed "$repository_root/src/Runtime/SearchGcPolicy.Recovery.cs" \
    'GC.Collect(' 'NoGC recovery must not induce a collection:'
forbid_fixed "$repository_root/src/Runtime/SearchGcPolicy.Recovery.cs" \
    'CollectGeneration2' 'NoGC recovery must not enter the reclaim chain:'
for gc_chain_rule in \
    'return WaitForReclaimChainAsync(_reclaimTask)' \
    'CollectGeneration2ForAutomaticReclaimAsync(inSearchCheckpoint: true)' \
    '_inSearchManualReclaimTask = manualCompletion.Task' \
    'failure == null && (_regionExitRequired || _reclaimRequired)'; do
    require_fixed "$search_gc_policy_path" "$gc_chain_rule" 'missing serialized reclaim-chain rule'
done
forbid_fixed \
    "$search_gc_policy_path" \
    'ReclaimAfterActiveCheckpointAsync' \
    'recursive reclaim handoff returned:'

# GC admission accounting and scratch-container ownership remain in their existing layers.
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing GC research ownership boundary'
done <<'EOF'
src/Runtime/SearchGcPolicy.cs	scope.CompleteLifecycle(CaptureLifecycle())
src/Runtime/SolverController.cs	SearchGcPolicy.EnterSearchScope(
src/Search/CombatBeamSolver.Models.cs	ExpansionBatchPool = new(static snapshot => snapshot.ReleaseSimulator())
src/Search/CombatBeamSolver.ParallelExpansion.cs	new(_run.ExpansionBatchPool)
src/Search/CombatBeamSolver.Phases.cs	SearchWaveMemoryPolicy.ParentWaveCapacity(
src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs	private sealed class FrozenStream(PredictionRngState state)
src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs	new FrozenStream(_mutable.CaptureState())
src/Testing/Host/UnattendedTestRunner.Assertions.cs	AssertLazyRngFork(scenario.CombatState.RunState.Rng)
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.ShuffleState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.ShuffleState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatCardGenerationState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatCardGenerationState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatPotionGenerationState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatPotionGenerationState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatCardSelectionState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatCardSelectionState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatEnergyCostsState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatEnergyCostsState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatTargetsState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatTargetsState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.CombatOrbGenerationState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.CombatOrbGenerationState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.MonsterAiState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.MonsterAiState
src/Search/CombatBeamSolver.StateEvaluation.cs	AppendRngState(ref key, simulator.Rng.NicheState);
src/Runtime/ContinuationStamp.cs	simulator.Rng.NicheState
src/Engine/Common/PredictedCard.cs	internal bool TryMarkPowerAfflictionEntryChecked()
src/Engine/Common/PredictedCard.cs	HasCheckedPowerAfflictionEntry = HasCheckedPowerAfflictionEntry,
src/Search/SimulatedCombatState.PowerLifecycle.cs	private HashSet<CardModel>? _liveCardsAtSnapshot;
src/Search/SimulatedCombatState.Fork.cs	_liveCardsAtSnapshot = _liveCardsAtSnapshot,
src/Search/SimulatedCombatState.Fork.cs	ReferenceEquals(view.Prefix, _rootRunHookListeners)
src/Testing/Host/UnattendedTestRunner.Assertions.cs	AssertFrozenRootRunListeners(scenario.CombatState, scenario.Player);
src/Search/CombatBeamSolver.Models.cs	SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new()
src/Search/CombatBeamSolver.StateEvaluation.cs	_run.SnapshotLiveCards.Rent()
EOF

card_play_prediction_state_path="$repository_root/src/Engine/InCombat/Mirrors/Hooks/Card/CardPlayHookPredictionStates.cs"
for stable_vambrace_state in \
    'internal sealed class VambracePredictionState(Vambrace relic) : IPredictionStateForkable' \
    'public CardModel? TriggeringCard { get; set; } = relic._triggeringCard;' \
    'public bool BlockGainedThisCombat { get; set; } = relic._blockGainedThisCombat;'; do
    require_fixed "$card_play_prediction_state_path" "$stable_vambrace_state" 'missing stable Vambrace state'
done

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing root snapshot boundary'
done <<'EOF'
src/Runtime/CombatRootSnapshot.cs	Combat root snapshot must be captured on the main thread.
src/Runtime/SolverController.cs	CombatRootSnapshot.Capture(state, settings.PredictPotionReward)
src/Runtime/PlayerTurnSetupPatches.cs	CombatRootSnapshot.Capture(combat, settings.PredictPotionReward)
src/Search/CombatSearchCoordinator.cs	CombatRootSnapshot root
src/Search/RootCombatHistorySnapshot.cs	history.CardPlaysStarted.ToArray()
EOF

native_choice_runtime_path="$repository_root/src/Runtime/NativeChoiceRuntime.cs"
turn_setup_path="$repository_root/src/Runtime/PlayerTurnSetupPatches.cs"
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing native choice boundary'
done <<'EOF'
src/Runtime/NativeChoiceRuntime.cs	internal static class NativeChoiceRuntime
src/Runtime/NativeChoiceRuntime.cs	NativeChoiceSurfaceKind.Hand
src/Runtime/NativeChoiceRuntime.cs	NativeChoiceSurfaceKind.SimpleGrid
src/Runtime/NativeChoiceRuntime.cs	NativeChoiceSurfaceKind.CombatPile
src/Runtime/NativeChoiceRuntime.cs	NativeChoiceSurfaceKind.ChooseCard
src/Runtime/PlayerTurnSetupPatches.cs	TryGetPlannedTurnSetupChoices
src/Runtime/PlayerTurnSetupPatches.cs	source=continuation choices=
src/Runtime/SolverController.cs	ResumeAfterTurnSetupAsync
EOF
for runtime_path in "${runtime_files[@]}"; do
    [[ "$runtime_path" == "$native_choice_runtime_path" ]] && continue
    forbid_fixed "$runtime_path" 'CardSelectCmd.PushSelector' 'production runtime bypasses native choice UI:'
done

card_targeting_path="$repository_root/src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardTargeting.cs"
for targeting_rule in \
    'Shiv => combat.GetAmount<FanOfKnivesPower>' \
    'SovereignBlade => combat.GetAmount<SeekingEdgePower>'; do
    require_fixed "$card_targeting_path" "$targeting_rule" 'missing simulated card targeting rule'
done

block_potion_insertion_path="$search_root/CombatBeamSolver.BlockPotionInsertion.cs"
for required_rule in 'HpLostByTurn' 'SolverWeights.PotionMinimumHpSaved' 'ReplayAdjustedRoute(' 'ProjectedDeathSaveUseCount' 'expanded_nodes_added=0'; do
    require_fixed "$block_potion_insertion_path" "$required_rule" 'deterministic block-potion route rule is missing'
done
require_fixed "$search_root/CombatSearchCoordinator.cs" 'passResult.DeterministicBlockPotionInserted' 'deterministic block-potion result must settle before supplemental potion audits'

expected_beam_files=(
    CombatBeamSolver.cs
    CombatBeamSolver.AdmittedExpansion.cs
    CombatBeamSolver.EndTurnChoiceReplay.cs
    CombatBeamSolver.RoundTransition.cs
    CombatBeamSolver.CardChoiceContinuation.cs
    CombatBeamSolver.PotionChoiceContinuation.cs
    CombatBeamSolver.ExecutionChoiceContinuation.cs
    CombatBeamSolver.ExecutionChoiceContinuation.Testing.cs
    CombatBeamSolver.TurnExecutionContinuation.cs
    CombatBeamSolver.BeamRetentionPolicy.cs
    CombatBeamSolver.BeamRetentionPolicy.OrderedMutation.cs
    CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs
    CombatBeamSolver.BeamRetentionPolicy.Ranking.cs
    CombatBeamSolver.BeamRetentionPolicy.Routing.cs
    CombatBeamSolver.BeamRetentionPolicy.Testing.cs
    CombatBeamSolver.AfterimageFrontloading.cs
    CombatBeamSolver.BlockPotionInsertion.cs
    CombatBeamSolver.CrossTurnPlanning.cs
    CombatBeamSolver.CyclePlanning.cs
    CombatBeamSolver.CycleRegionRetention.cs
    CombatBeamSolver.CycleReplay.cs
    CombatBeamSolver.EarlyTurnFrontier.cs
    CombatBeamSolver.Expansion.cs
    CombatBeamSolver.Expansion.Candidates.cs
    CombatBeamSolver.Expansion.Choices.cs
    CombatBeamSolver.Expansion.Opening.cs
    CombatBeamSolver.ExpansionExecutor.cs
    CombatBeamSolver.ExpansionPlan.cs
    CombatBeamSolver.Expansion.Replay.cs
    CombatBeamSolver.FinalPlanOrdering.cs
    CombatBeamSolver.Models.cs
    CombatBeamSolver.NoveltySearch.cs
    CombatBeamSolver.Transpositions.cs
    CombatBeamSolver.OrderedMutationRetention.cs
    CombatBeamSolver.ParallelExpansion.cs
    CombatBeamSolver.PathDiagnostics.cs
    CombatBeamSolver.Phases.cs
    CombatBeamSolver.PrimaryChoiceReplay.cs
    CombatBeamSolver.Retention.cs
    CombatBeamSolver.RetentionJobs.cs
    CombatBeamSolver.SmartPotionBound.cs
    CombatBeamSolver.StateEvaluation.cs
    CombatBeamSolver.StandPatJobs.cs
    CombatBeamSolver.Terminal.cs
)
mapfile -t actual_beam_names < <(
    printf '%s\n' "${beam_files[@]##*/}" | sort
)
mapfile -t expected_beam_names < <(
    printf '%s\n' "${expected_beam_files[@]}" | sort
)
actual_beam_joined="$(IFS='|'; printf '%s' "${actual_beam_names[*]}")"
expected_beam_joined="$(IFS='|'; printf '%s' "${expected_beam_names[*]}")"
if [[ "$actual_beam_joined" != "$expected_beam_joined" ]]; then
    actual_beam_csv="$(IFS=','; printf '%s' "${actual_beam_names[*]}")"
    expected_beam_csv="$(IFS=','; printf '%s' "${expected_beam_names[*]}")"
    add_violation "CombatBeamSolver partial file set differs: actual=$actual_beam_csv expected=$expected_beam_csv"
fi

while IFS=$'\t' read -r file_name text; do
    require_fixed "$search_root/$file_name" "$text" 'missing CombatBeamSolver stage member'
done <<'EOF'
CombatBeamSolver.cs	internal sealed partial class CombatBeamSolver(
GrowthPolicy.cs	internal readonly record struct GrowthValues(
SearchPolicySnapshot.cs	public GrowthValues GrowthBudgets { get; init; }
SearchPolicySnapshot.cs	public bool UseNoveltyPortfolio { get; init; }
CombatBeamSolver.Models.cs	public NoveltySearchRun? Novelty;
CombatBeamSolver.NoveltySearch.cs	private bool RunNoveltyOpen(
CombatBeamSolver.NoveltySearch.cs	CaptureNoveltyFacts(SearchNode node)
CombatSearchCoordinator.NoveltyPortfolio.cs	NoveltyPortfolioBudget.Remaining(profile,
CombatSearchCoordinator.NoveltyPortfolio.cs	IsBetterPotionPolicyResult(root, policy, exploration, baseline)
BfwsPackedNovelty.cs	Dictionary<BfwsFact, int> _atoms
BfwsPackedNovelty.cs	_parentPartition == partition
BfwsBoundedOpen.cs	private readonly SortedSet<Entry> _entries
NoveltyPortfolioBudget.cs	profile.MaxExpandedNodes - (int)expandedNodes
CombatBeamSolver.cs	private readonly SearchRunContext _run = new(
CombatBeamSolver.cs	private BeamRetentionPolicy Retention =>
CombatBeamSolver.cs	private FinalPlanOrdering FinalOrdering =>
CombatBeamSolver.BeamRetentionPolicy.cs	private sealed partial class BeamRetentionPolicy(
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankBest(
CombatBeamSolver.BeamRetentionPolicy.cs	private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>
CombatBeamSolver.BeamRetentionPolicy.cs	public void Clear() => NodesByChoice.Clear();
CombatBeamSolver.BeamRetentionPolicy.cs	routingNodes = new RoutingChoiceNodes(node);
CombatBeamSolver.BeamRetentionPolicy.cs	ReturnRoutingChoiceScratch(scratch);
CombatBeamSolver.Transpositions.cs	private readonly record struct TranspositionLabel(
CombatBeamSolver.Models.cs	private sealed class SearchRunContext(
CombatBeamSolver.Models.cs	private readonly record struct SearchFeatures(
CombatBeamSolver.ParallelExpansion.cs	private sealed partial class ParallelExpansionExecutor : IExpansionExecutor, IDisposable
CombatBeamSolver.ParallelExpansion.cs	public ExpansionWorkerOutcome[] Evaluate(
CombatBeamSolver.ParallelExpansion.cs	public int MaximumQueuedParents => SearchWaveMemoryPolicy.MaximumQueuedParents(DegreeOfParallelism);
CombatBeamSolver.ParallelExpansion.cs	List<ExpansionLane> lanes = new(DegreeOfParallelism);
CombatBeamSolver.AdmittedExpansion.cs	private ExpansionWorkerOutcome[] EvaluateQueuedParents(
CombatBeamSolver.AdmittedExpansion.cs	private sealed class AdmittedParent(
CombatBeamSolver.AdmittedExpansion.cs	public object ForkGate { get; } = new();
CombatBeamSolver.AdmittedExpansion.cs	_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
CombatBeamSolver.AdmittedExpansion.cs	wave.BackgroundCompleted.Wait();
CombatBeamSolver.AdmittedExpansion.cs	while (committed < parents.Length && parents[committed]!.TailCompleted)
CombatBeamSolver.AdmittedExpansion.cs	_completedActions != Actions!.Count
CombatBeamSolver.AdmittedExpansion.cs	_completedPotions != Potions!.Count
CombatBeamSolver.EndTurnChoiceReplay.cs	private PreparedEndTurnEvaluation EvaluatePreparedEndTurn(
CombatBeamSolver.TurnExecutionContinuation.cs	private static SearchBoundaryReason ContinuePlayerStart(
CombatBeamSolver.RoundTransition.cs	private sealed class RoundReplayCheckpoint(
CombatBeamSolver.RoundTransition.cs	combat.EndActionChoices();
CombatBeamSolver.RoundTransition.cs	combat.BeginActionChoices(cursor);
CombatBeamSolver.RoundTransition.cs	internal int VerifyRoundReplayCheckpointForTesting(
CombatBeamSolver.Models.cs	public bool HasObservedPostDrawRoundChoice;
CombatBeamSolver.Models.cs	public HashSet<string>? ObservedHandDrawShuffleChoiceSources;
CombatBeamSolver.RoundTransition.cs	public void CaptureBeforeHandDraw(CombatBeamSolver owner, CombatPredictionSimulator simulator,
CombatBeamSolver.RoundTransition.cs	checkpoint.HandDrawCount.HasValue ? PlayerStartStage.Draw : PlayerStartStage.AfterPlayer
RootCombatCardGenerationPoolSnapshot.cs	public bool TryGetEligibleCharacterCards(
CombatBeamSolver.Retention.cs	var maximum = BeamRetentionPolicy.GetLongTermResourceMaximum(pool);
CombatBeamSolver.Retention.cs	if (maximum.Count == pool.Count)
CombatBeamSolver.EndTurnChoiceReplay.cs	capture.ObservePendingChoice(this, pendingSourceId);
CombatBeamSolver.AdmittedExpansion.cs	endTurn.TransferEndTurnTo(Aggregate!, candidate);
CombatBeamSolver.AdmittedExpansion.cs	PublishCrossTurnStandPatBaselines(Node, _endTurnBaselines);
CombatBeamSolver.AdmittedExpansion.cs	ready.TransferPotionTo(Aggregate!, candidate);
CombatBeamSolver.PrimaryChoiceReplay.cs	private sealed class PrimaryChoiceReplayFrontier : IDisposable
CombatBeamSolver.PrimaryChoiceReplay.cs	=> branches >= 2 && finals >= branches && attempts >= branches;
CombatBeamSolver.PrimaryChoiceReplay.cs	public bool CanDispatchContinuation => CompletedReplays == Actions.Length
CombatBeamSolver.PrimaryChoiceReplay.cs	if (!budget.TrySpendReplayAttempt())
CombatBeamSolver.PrimaryChoiceReplay.cs	frontier.AssertConsumed();
CombatBeamSolver.EndTurnChoiceReplay.cs	CanReservePrimaryReplayPrefix(layer.Layer.Branches.Count,
CombatBeamSolver.EndTurnChoiceReplay.cs	ResolveCollectedOccurrenceChoiceBranches(parent, layer.Occurrences)
CombatBeamSolver.AdmittedExpansion.cs	_endTurnFrontier?.Dispose();
CombatBeamSolver.PrimaryChoiceReplay.cs	if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)
CombatBeamSolver.Models.cs	public ParallelExpansionExecutor? ActiveParallelExpansion;
CombatBeamSolver.ParallelExpansion.cs	_coordinator._run.ActiveParallelExpansion = null;
CombatBeamSolver.StandPatJobs.cs	private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)
CombatBeamSolver.StandPatJobs.cs	seen.Add(node.StateKey)
CombatBeamSolver.StandPatJobs.cs	_run.StandPatCache.Add(batch[index].StateKey, evaluations[index]);
CombatBeamSolver.StandPatJobs.cs	ExpansionLane[] lanes = EnsureBackgroundLanes();
CombatBeamSolver.StandPatJobs.cs	_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
CombatBeamSolver.StandPatJobs.cs	wave.Completed.Wait();
CombatBeamSolver.RetentionJobs.cs	public void EvaluateRetentionIndices(
CombatBeamSolver.RetentionJobs.cs	ExpansionLane[] lanes = EnsureBackgroundLanes();
CombatBeamSolver.RetentionJobs.cs	wave.Completed.Wait();
CombatBeamSolver.RetentionJobs.cs	_coordinator._run.OffThreadAllocatedBytes += job.AllocatedBytes;
CombatBeamSolver.RetentionJobs.cs	wave.Error?.Throw();
CombatBeamSolver.BeamRetentionPolicy.cs	_run.RoutingChoiceSummaryBuilds += summaryGroups.Length;
CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs	RequestOrderedMutationObservation(candidate);
SearchWaveMemoryPolicy.cs	return checked(degreeOfParallelism * 2);
SearchWaveMemoryPolicy.cs	current >= maximum - current ? maximum : current * 2
CombatBeamSolver.Phases.cs	SearchWaveMemoryPolicy.GrowCapacity(
CombatBeamSolver.Retention.cs	end.ReleaseSimulator();
CombatBeamSolver.ParallelExpansion.cs	private void CommitExpansionBatch(
CombatBeamSolver.Phases.cs	public SolverResult Solve()
CombatBeamSolver.Expansion.cs	private IEnumerable<SearchNode> Expand(SearchNode node)
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)
CombatBeamSolver.FinalPlanOrdering.cs	private sealed class FinalPlanOrdering(
CombatBeamSolver.FinalPlanOrdering.cs	public FinalPlanSelection Select(
CombatBeamSolver.Terminal.cs	private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)
CombatBeamSolver.StateEvaluation.cs	private SimulationSnapshot Snapshot(
EOF

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$search_root/PowerCardValuation/$relative_path" "$text" 'missing power-card valuation boundary'
done <<'EOF'
PowerCardValuationContracts.cs	internal readonly record struct PowerCardValuationReward
PowerCardValuationContracts.cs	internal readonly record struct PowerCardValuationPenalty
IPowerCardValuationModel.cs	internal interface IPowerCardValuationModel
PowerCardValuationRegistry.cs	internal sealed class PowerCardValuationRegistry
PowerCardValuationModels.cs	internal static class PowerCardValuationModels
PowerCardValuationRegistration.cs	internal sealed class DelegatingPowerCardValuationModel
PowerRouteAdmission.cs	internal static class PowerRouteAdmission
PowerCardValueFacts.cs	internal static class PowerCardValueFacts
Projection/PowerCardProjectionSupport.cs	internal sealed partial class CombatBeamSolver
Projection/PowerCardMechanismFacts.cs	internal sealed partial class CombatBeamSolver
Projection/PowerTurnFrontier.cs	internal static class PowerTurnFrontier
Projection/RetainedHandTransition.cs	internal static class RetainedHandTransition
Projection/DrawDiscardTransition.cs	internal static class DrawDiscardTransition
Projection/PoisonStackProjection.cs	internal static class PoisonStackProjection
Projection/MasterPlannerProjection.cs	internal static class MasterPlannerProjection
Projection/SilentPowerOpeningProjection.cs	private int SilentPowerOpeningProjectionPotential
Projection/SilentShivPowerProjection.cs	private int AccuracyProjectionPotential
Projection/SilentPoisonPowerProjection.cs	private int AccelerantProjectionPotential
Projection/SilentDamageDefensePowerProjection.cs	private int SerpentFormProjectionPotential
Commitments/PowerCommitment.cs	internal sealed record PowerCommitment
Commitments/PowerCommitmentLifecycle.cs	internal static class PowerCommitmentLifecycle
Commitments/PowerCommitmentPolicy.cs	private void AttachPowerCommitment
Commitments/PowerCommitmentEvidence.cs	private int PowerCommitmentRealizedEvidence
Commitments/PowerCardPlayOccurrence.cs	internal readonly record struct PowerCardPlayOccurrence
Commitments/PowerCommitmentRetention.cs	internal static class PowerCommitmentRetention
Commitments/PowerCommitmentSeatPolicy.cs	internal static class PowerCommitmentSeatPolicy
Commitments/PowerActivationInvestmentPolicy.cs	internal static class PowerActivationInvestmentPolicy
Cards/Ironclad/IroncladPowerCardValuationModels.cs	internal static class IroncladPowerCardValuationModels
Cards/Ironclad/IroncladPowerRoutePolicy.cs	internal static class IroncladPowerRoutePolicy
Cards/Ironclad/IroncladPowerTriggerEvidence.cs	private bool IroncladPowerHasTriggerEvidence
Cards/Ironclad/IroncladPowerOpeningProjection.cs	private int IroncladPowerOpeningProjectionPotential
Cards/Ironclad/IroncladStrengthPowerCardValuationModels.cs	internal static class IroncladStrengthPowerCardValuationModels
Cards/Silent/SilentPowerCardValuationModels.cs	internal static class SilentPowerCardValuationModels
Cards/Silent/SilentPowerCardValuationModel.cs	internal abstract class SilentPowerCardValuationModel
Cards/Silent/SilentDefensePowerCardValuationModels.cs	internal sealed class WraithFormPowerCardValuationModel
Cards/Silent/SilentPoisonPowerCardValuationModels.cs	internal sealed class NoxiousFumesPowerCardValuationModel
Cards/Silent/SilentShivPowerCardValuationModels.cs	internal sealed class FanOfKnivesPowerCardValuationModel
Cards/Silent/SilentCardFlowPowerCardValuationModels.cs	internal sealed class MasterPlannerPowerCardValuationModel
Cards/Silent/SilentCardFlowFacts.cs	internal static class SilentCardFlowFacts
Cards/Silent/SilentDiscardWindowFacts.cs	internal static class SilentDiscardWindowFacts
Cards/Silent/SilentPowerRoutePolicy.cs	internal static class SilentPowerRoutePolicy
Cards/Silent/SilentPowerTriggerEvidence.cs	private bool SilentPowerHasTriggerEvidence
Cards/Silent/SilentPowerCommitmentEvidence.cs	private int SilentPowerProgressEvidence
Cards/Silent/SilentWraithOpeningWindow.cs	internal static class SilentWraithOpeningWindow
Cards/Silent/SilentDamagePowerCardValuationModels.cs	internal sealed class TrackingPowerCardValuationModel
Cards/Defect/DefectPowerCardValuationModels.cs	internal static class DefectPowerCardValuationModels
Cards/Defect/DefectPowerRoutePolicy.cs	internal static class DefectPowerRoutePolicy
Cards/Defect/DefectPowerTriggerEvidence.cs	private bool DefectPowerHasTriggerEvidence
Cards/Defect/DefectPowerOpeningProjection.cs	private int DefectPowerOpeningProjectionPotential
Cards/Defect/DefectOrbPowerCardValuationModels.cs	internal static class DefectOrbPowerCardValuationModels
Cards/Regent/RegentPowerCardValuationModels.cs	internal static class RegentPowerCardValuationModels
Cards/Regent/RegentPowerRoutePolicy.cs	internal static class RegentPowerRoutePolicy
Cards/Regent/RegentPowerTriggerEvidence.cs	private bool RegentPowerHasTriggerEvidence
Cards/Regent/RegentPowerOpeningProjection.cs	private int RegentPowerOpeningProjectionPotential
Cards/Regent/RegentStarPowerCardValuationModels.cs	internal static class RegentStarPowerCardValuationModels
Cards/Necrobinder/NecrobinderPowerCardValuationModels.cs	internal static class NecrobinderPowerCardValuationModels
Cards/Necrobinder/NecrobinderPowerRoutePolicy.cs	internal static class NecrobinderPowerRoutePolicy
Cards/Necrobinder/NecrobinderPowerTriggerEvidence.cs	private bool NecrobinderPowerHasTriggerEvidence
Cards/Necrobinder/NecrobinderPowerOpeningProjection.cs	private int NecrobinderPowerOpeningProjectionPotential
Cards/Necrobinder/NecrobinderDoomPowerCardValuationModels.cs	internal static class NecrobinderDoomPowerCardValuationModels
Cards/Colorless/ColorlessPowerCardValuationModels.cs	internal static class ColorlessPowerCardValuationModels
Cards/Colorless/ColorlessPowerRoutePolicy.cs	internal static class ColorlessPowerRoutePolicy
Cards/Colorless/ColorlessPowerTriggerEvidence.cs	private bool ColorlessPowerHasTriggerEvidence
Cards/Colorless/ColorlessPowerOpeningProjection.cs	private int ColorlessPowerOpeningProjectionPotential
Cards/Colorless/ColorlessGrowthPowerCardValuationModels.cs	internal static class ColorlessGrowthPowerCardValuationModels
EOF

require_fixed "$search_root/PowerCommitmentPortfolioGate.cs" \
    'internal static class PowerCommitmentPortfolioGate' \
    'missing power commitment portfolio gate'
for power_route_rule in \
    'private static SolverResult RunOpeningPowerRoutePortfolio(' \
    'ContinuationPurpose.OpeningPowerRouteMember' \
    'PowerRoutePortfolioMemberReport'; do
    require_fixed "$search_root/CombatSearchCoordinator.PowerRoutes.cs" \
        "$power_route_rule" \
        'missing power route portfolio boundary'
done
forbid_fixed "$search_root/CombatBeamSolver.FinalPlanOrdering.cs" 'PowerCardValuation' \
    'power-card valuation must not enter final plan ordering:'

require_fixed \
    "$search_root/CombatBeamSolver.Expansion.Choices.cs" \
    'CreateWholeActionChoiceBudget' \
    'repeated card choices are missing their whole-action branch quota:'

path_diagnostics_path="$search_root/CombatBeamSolver.PathDiagnostics.cs"
require_fixed "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" 'HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null' 'ordinary tactical ties must use the existing retained routing semantics:'
require_fixed "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" 'if (values.HasRetainedRoutingChoice)' 'ordinary tactical ties must leave routing positions unchanged:'
require_fixed "$repository_root/src/Testing/Contracts/Search/UnattendedTestRunner.SearchPolicy.cs" 'seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);' 'ordinary tactical ties lost the interleaved routing-position contract:'
require_fixed "$repository_root/src/Testing/Host/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-GENERATION-CONTEXT-V0111' 'generation context replay lost its executor entry:'
require_fixed "$repository_root/src/Testing/Host/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-GENERATION-SUFFIX-V0111' 'generation context frozen suffix replay lost its executor entry:'
require_fixed "$repository_root/src/Testing/Host/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111' 'multi-enemy known route lost its executor entry:'
require_fixed "$repository_root/src/Testing/Host/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111' 'multi-enemy native replay lost its executor entry:'
require_fixed "$path_diagnostics_path" 'observer.WantsState(node.StateKey)' 'path observer no longer filters before copying:'
require_fixed "$path_diagnostics_path" 'observer.WantsRetentionPool(node.StateKey)' 'retention pool observer no longer requires an explicit match:'
require_fixed "$path_diagnostics_path" 'SearchPathObservationStage.RetentionPoolInput' 'retention pool input observation is missing:'
require_fixed "$path_diagnostics_path" 'Evaluation: new SearchPathEvaluationValues(' 'retention evaluation value copy is missing:'
require_fixed "$search_root/CombatBeamSolver.Retention.cs" 'SearchPathObservationStage.RetentionPoolFinal' 'retention pool final observation is missing:'
require_fixed "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" 'observedOptionLeaders.Add(optionLeader)' 'routing observation no longer captures the actual option leader:'
forbid_fixed "$path_diagnostics_path" 'node.Actions;' 'path observer populates retained action caches:'
require_fixed "$search_root/CombatBeamSolver.Retention.cs" 'SearchPathObservationStage.PruneFinal' 'final prune observation is missing:'
stat_relic_mirror_path="$repository_root/src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs"
require_fixed "$stat_relic_mirror_path" 'private static bool ApplyRelicStatPower(' 'relic stat application left its exact hook boundary:'
require_fixed "$stat_relic_mirror_path" 'if (context.Simulator.IsEnding)' 'relic stat command ending guard is missing:'
forbid_fixed "$search_root/SimulatedCombatState.Relics.cs" 'case Kunai' 'relic stat application returned to deferred lifecycle:'
forbid_fixed "$search_root/SimulatedCombatState.Relics.cs" 'case Shuriken' 'relic stat application returned to deferred lifecycle:'
forbid_fixed "$search_root/SimulatedCombatState.Relics.cs" 'Apply<DexterityPower>' 'relic stat application returned to deferred lifecycle:'

beam_entry_path="$search_root/CombatBeamSolver.cs"
forbid_fixed "$beam_entry_path" 'public SolverResult Solve()' 'Solve returned to the entry/field declaration file:'
beam_retention_facade_path="$search_root/CombatBeamSolver.Retention.cs"
forbid_fixed "$beam_retention_facade_path" 'private List<SearchNode> RankBest(' 'RankBest returned outside BeamRetentionPolicy:'
remaining_healing_bound_path="$search_root/StrategicHpRecoveryBound.Remaining.cs"
require_fixed "$repository_root/src/Runtime/CombatRootSnapshot.cs" 'CanCertifyRemainingHealingEnvironment(' 'remaining-healing environment proof is not frozen at the root:'
require_fixed "$beam_retention_facade_path" 'root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy' 'healing pruning requires a frozen certificate or native-source policy:'
require_fixed "$repository_root/src/Runtime/CombatRootSnapshot.cs" 'CanUseKnownNativeHealingPolicy(' 'native healing policy eligibility must be frozen at the root:'
for closure_component in 'PendingReturningCards' 'AllCards' 'EffectivePowers()' 'GetPotionSlotCount(player)' 'HasCertifiedRemainingAttachments' 'typeof(InfestedPrism)' 'typeof(FuzzyWurmCrawler)'; do
    require_fixed "$remaining_healing_bound_path" "$closure_component" 'remaining-healing proof lost a closure component:'
done
beam_phases_path="$search_root/CombatBeamSolver.Phases.cs"
require_fixed "$beam_retention_facade_path" '_strictHpBoundWithRelicTargets = CanUseStrictHpRelicBound(root, policy)' 'missing common relic healing bound:'
require_fixed "$beam_retention_facade_path" 'targets.All(target => target.HpAllowance == 0)' 'missing zero-allowance objective gate:'
require_fixed "$beam_retention_facade_path" 'allowTurnTieBound: !_strictHpBoundWithRelicTargets' 'equal-HP counter routes must keep later turns:'
require_fixed "$search_root/CombatSearchCoordinator.cs" '!CombatBeamSolver.CanUseStrictHpRelicBound(root, policy)' 'shared incumbent must retain objective eligibility:'
require_fixed "$search_root/CombatSearchCoordinator.PlanSearch.cs" 'context.Root.CanCertifyRemainingHealing || context.Root.UsesKnownNativeHealingPolicy' 'opening incumbent must consume the common healing policy:'
require_fixed \
    "$beam_phases_path" \
    'TightenPrimarySearchIncumbentAtTurnLayer(' \
    'turn-layer incumbent is no longer tightened before coordinator pruning:'
forbid_fixed \
    "$beam_phases_path" \
    'FinalizePrunedSelection(' \
    'turn-layer incumbent pruning performs a second post-commit finalization:'
for direct_prune_finalizer in \
    'ApplyPrimaryIncumbentBound(' \
    'FinalizePrunedCycleExitProbeTickets('; do
    forbid_fixed \
        "$beam_phases_path" \
        "$direct_prune_finalizer" \
        'turn-layer pruning bypasses observation-debt finalization:'
done
for implementation in \
    'POLICY_BASELINE kind=potion_free' \
    'PotionUsePolicy.IsEligible(' \
    'PotionUsePolicy.MeetsAmbergrisRestriction('; do
    forbid_fixed "$beam_phases_path" "$implementation" 'final ordering implementation returned outside FinalPlanOrdering:'
done
for retired_run_field in \
    'private readonly SearchPerformanceMetrics _performance' \
    'private int _expanded' \
    'private readonly SearchWorkPacer _workPacer' \
    'private readonly Dictionary<StateFingerprint, TranspositionFrontier> _transpositions'; do
    forbid_fixed "$beam_entry_path" "$retired_run_field" 'retired run-local field returned:'
done
for removed_worker_root in \
    'new SimulatedCombatState(' \
    'IntentForecaster.Build(state' \
    '_player.PotionSlots' \
    '_player.Relics' \
    '_player.Creature.MaxHp'; do
    for beam_path in "${beam_files[@]}"; do
        forbid_fixed "$beam_path" "$removed_worker_root" 'worker root fallback returned:'
    done
done

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing root model boundary'
done <<'EOF'
src/Search/SimulatedCombatState.cs	Live combat state can only be captured on the main thread.
src/Search/SimulatedCombatState.cs	PredictionUtils.CreateRelic(relic, player)
src/Search/SimulatedCombatState.cs	RunRngSet.FromSave(_runRngSnapshot)
src/Prediction/RelicPredictionStateSupport.cs	CaptureRootState(
src/Prediction/PowerPredictionStateSupport.cs	HardenedShellPredictionState(original)
src/Search/SimulatedCombatState.cs	PowerPredictionStateSupport.CaptureRootState(simulator, mutable, power)
src/Testing/Contracts/Runtime/UnattendedTestRunner.CombatRootSnapshot.cs	workerLiveConstructorRejected
src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs	ICombatPredictionRootMaterializable materializable
src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs	public CombatTerminalStamp? TerminalStamp { get; private set; }
src/Search/CombatPlan.cs	public CombatTerminalStamp? TerminalStamp { get; } = terminalStamp;
src/Search/CombatBeamSolver.Terminal.cs	combatEndedTurn = node.Snapshot.CombatEndedTurn;
src/Search/SimulatedCombatState.cs	.Select(PredictionUtils.CloneModelForSimulation)
src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs	GetAeonglassWitherUpgradeCount(monster.Creature)
src/Prediction/MonsterSpawnSupport.cs	.SelectMany(combat.RelicsOf)
src/Search/SimulatedCombatState.cs	foreach (BadgeModel badge in inner.BadgeModels)
src/Search/SimulatedCombatState.cs	MultiplayerScalingRunStateField.SetValue(detachedMultiplayerScaling, null)
src/Engine/InCombat/Mirrors/Hooks/Block/ModifyBlockMultiplicativeMirrors.cs	registry.Register<MultiplayerScalingModel>(HandleMultiplayerScaling)
src/Prediction/PredictionModHookSubscriberCapture.cs	ModHelper.IterateAllRunStateSubscribers(runState)
src/Engine/Common/PredictionUtils.cs	PredictionModModelSupport.CloneCardAttachedModels(source, clone)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardPile.cs	ContinueDrawExecution(player, drawCount, fromHandDraw, GetMaxHandSize(player)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardPile.cs	limits.GetMaxHandSize(player)
src/Search/SimulatedCombatState.cs	.Take(standardCombatListenerCount)
src/Search/SimulatedCombatState.cs	UpdatePowerListenerOrder(
src/Search/SimulatedCombatState.Fork.cs	fork._powerListenerOrder =
src/Engine/Common/PredictionModModelSupport.cs	ConditionalWeakTable<CardModel, object> BaseLibModifierCards
src/Search/SimulatedCombatState.PowerRelics.cs	(_powerCardSources ??= []).Add(card)
src/Search/SimulatedCombatState.cs	and not OrbModel
src/Engine/InCombat/Simulation/SimOrbQueue.cs	SetMutationObserver(
src/Engine/InCombat/Mirrors/Potions/OnUse/EntropicBrewMirrors.cs	limits.GetPotionSlotCount(target)
src/Prediction/CardOnPlaySupport.Batch042.cs	combat.DoomKill(simulator, doomed)
src/Prediction/BranchMonsterAi.cs	BranchMonsterStaticSnapshot.Capture(monster)
src/Prediction/BranchMonsterAi.cs	state.Static.AttacksByMove
src/Search/SimulatedCombatState.cs	_encounterSlots = inner.Encounter?.Slots.ToArray()
src/Search/SimulatedCombatState.MonsterAi.cs	Root monster AI state was not captured
src/Search/SimulatedCombatState.cs	Root intent state was not captured
src/Prediction/MonsterMoveEffects.StaticValues.cs	CaptureStaticIntValues(MonsterModel monster)
src/Search/SimulatedCombatState.MonsterAi.cs	GetMonsterStaticInt(Creature creature, string name)
src/Engine/InCombat/Simulation/CombatPredictionState.cs	boundary.AssertCanCaptureCreature(creature)
src/Engine/InCombat/Simulation/CombatPredictionState.cs	boundary.AssertCanCapturePlayer(player)
EOF

while IFS=$'\t' read -r relative_path text; do
    forbid_fixed "$repository_root/$relative_path" "$text" 'removed model fallback returned:'
done <<'EOF'
src/Search/SimulatedCombatState.cs	inner.ContainsCard(card)
src/Search/SimulatedCombatState.cs	player.PlayerCombatState?.TurnNumber
src/Search/SimulatedCombatState.RelicTurnStart.cs	RunState.CardMultiplayerConstraint
src/Search/SimulatedCombatState.Relics.cs	player.RunState.CardMultiplayerConstraint
EOF

while IFS=$'\t' read -r relative_path text; do
    forbid_fixed "$repository_root/$relative_path" "$text" 'worker live read returned:'
done <<'EOF'
src/Search/SimulatedCombatState.Fork.cs	new(InnerState)
src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardGeneratedForCombatMirrors.cs	monster.WitherUpgradeCount
src/Prediction/MonsterSpawnSupport.cs	player.Relics
src/Runtime/CombatRootSnapshot.cs	.MaterializeRoot(
src/Search/SimulatedCombatState.cs	_multiplayerScalingModel = inner.MultiplayerScalingModel
src/Search/SimulatedCombatState.PowerRelics.cs	private CardModel? _powerCardSource;
src/Prediction/PotionOnUseSupport.cs	playerTarget.MaxHp
src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs	creature.MaxHp <= 0
src/Engine/InCombat/Mirrors/Hooks/Death/DeathPreventerMirrors.cs	context.Creature.MaxHp
src/Prediction/CardOnPlaySupport.Batch042.cs	player.Relics
src/Prediction/CardOnPlaySupport.Batch042.cs	creature.Powers
src/Prediction/TurnStartRelicSupport.cs	player.Relics
src/Engine/InCombat/Mirrors/Potions/OnUse/EntropicBrewMirrors.cs	target.PotionSlots.Count
src/Prediction/BranchMonsterAi.cs	return branch.GetNextState(owner, rng)
src/Prediction/BranchMonsterAi.cs	return state.GetWeight()
src/Prediction/BranchMonsterAi.cs	combat.Encounter?.GetNextSlot(combat)
src/Prediction/MonsterSpawnSupport.cs	combat.Encounter?.GetNextSlot(combat)
src/Prediction/MonsterSpawnSupport.cs	combat.Encounter?.Slots
src/Search/SimulatedCombatState.cs	IReadOnlyList<string> slots = Encounter?.Slots
src/Prediction/MonsterMoveEffects.cs	MonsterValueReader.ReadInt(monster
EOF

unattended_entry_path="$repository_root/src/Testing/Host/UnattendedTestRunner.cs"
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing headless infrastructure ownership boundary'
done <<'EOF'
tools/testing/run-unattended-test.sh	source "$script_dir/headless-runtime.sh"
tools/testing/run-unattended-test.sh	hr_acquire "$process_pid" "$process_identity_start_time"
tools/testing/run-unattended-test.sh	if ((option_value[stop-instance] == 1)); then
tools/testing/run-unattended-test.sh	add_option cleanup-instance-on-exit 0 switch none
tools/testing/run-unattended-test.sh	add_option checkpoint-selector "start" string raw_string
tools/testing/run-unattended-test.sh	$repo_root/.local/headless-instances/$headless_instance
tools/testing/run-unattended-test.sh	hr_remove_instance
tools/testing/run-unattended-test.ps1	. (Join-Path $PSScriptRoot 'headless-runtime.ps1')
tools/testing/run-unattended-test.ps1	[string]$CheckpointSelector = "start"
tools/testing/run-unattended-test.ps1	if ($StopInstance) {
tools/testing/run-unattended-test.ps1	[switch]$CleanupInstanceOnExit
tools/testing/run-unattended-test.ps1	Remove-HeadlessRuntimeInstance $runtimeContext
tools/testing/run-headless-matrix.sh	--stop-instance
tools/testing/run-headless-matrix.ps1	"-StopInstance"
tools/testing/headless-runtime.sh	hr_prepare_snapshot() {
tools/testing/headless-runtime.sh	hr_bind() {
tools/testing/headless-runtime.sh	hr_remove_instance() {
tools/testing/headless-runtime.ps1	function Set-HeadlessGameSnapshot(
tools/testing/headless-runtime.ps1	Join-Path $repository ".local\headless-instances\$Instance"
tools/testing/headless-runtime.ps1	function Remove-HeadlessRuntimeInstance(
tools/testing/headless-runtime.ps1	function Enter-HeadlessHostLease(
tools/testing/headless-runtime.ps1	function Set-HeadlessHostGame(
EOF
for legacy_instance_root in \
    "$repository_root/tools/testing/headless-runtime.ps1|CombatSolver\\headless-instances" \
    "$repository_root/tools/testing/run-unattended-test.sh|CombatSolver/headless-instances" \
    "$repository_root/tools/testing/run-headless-matrix.sh|CombatSolver/headless-instances"; do
    legacy_path="${legacy_instance_root%%|*}"
    legacy_text="${legacy_instance_root#*|}"
    forbid_fixed "$legacy_path" "$legacy_text" 'user-local headless instance root returned:'
done
require_fixed "$repository_root/src/Replay/CheckpointArchive.cs" \
    'public const string DefaultFixtureSelector = "start";' \
    'checkpoint fixture default must remain combat start'
for matrix in "$repository_root/tools/testing/run-headless-matrix.sh" "$repository_root/tools/testing/run-headless-matrix.ps1"; do
    forbid_fixed "$matrix" 'MATRIX-CLEANUP' 'matrix cleanup must not dispatch a new game request:'
done
for helper in "$repository_root/tools/testing/headless-runtime.sh" "$repository_root/tools/testing/headless-runtime.ps1"; do
    forbid_fixed "$helper" 'combat_solver_test_request.json' 'request protocol leaked into headless resource owner:'
    forbid_fixed "$helper" 'SolverSettings' 'game settings leaked into headless resource owner:'
done
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing unattended protocol boundary'
done <<'EOF'
src/Testing/Host/UnattendedTestRunner.cs	private static readonly ProtocolHost Host = new();
src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs	private sealed partial class ProtocolHost
src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs	private async Task RunRequestLoopAsync(NGame host)
src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs	private void Activate(UnattendedTestRequest request)
src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs	private void Reset()
src/Testing/Host/UnattendedTestRunner.Writer.cs	private sealed partial class Writer(
src/Testing/Host/UnattendedTestRunner.Writer.cs	public RuntimeMemorySnapshot Write(
src/Testing/Host/UnattendedTestRunner.Writer.cs	private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	private sealed partial class ScenarioBuilder(
src/Testing/Host/GeneratedCombatScenario.cs	internal static ResolvedGeneratedCombatScenario Resolve(
src/Testing/Host/UnattendedTestRunner.GeneratedScenario.cs	private void PrepareGeneratedScenario()
src/Testing/Host/UnattendedTestRunner.GeneratedScenario.cs	private void CaptureGeneratedOpening(
src/Testing/Host/UnattendedTestRunner.Writer.cs	public void WriteGeneratedArtifact(
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	public async Task<ScenarioContext> BuildAsync()
src/Testing/Host/UnattendedTestRunner.ScenarioBuilder.cs	public CombatState? CombatState { get; private set; }
src/Testing/Host/UnattendedTestRunner.Assertions.cs	private sealed class Assertions(
src/Testing/Host/UnattendedTestRunner.Assertions.cs	public async Task RunBeforeExecutionAsync(ScenarioContext scenario)
src/Testing/Host/UnattendedTestRunner.Assertions.cs	public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)
src/Testing/Host/UnattendedTestRunner.Executor.cs	private sealed class Executor(
src/Testing/Host/UnattendedTestRunner.Executor.cs	public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)
src/Testing/Host/UnattendedTestRunner.Executor.cs	private FastModeType? ApplySettingsOverrides()
src/Testing/Host/UnattendedTestRunner.Executor.cs	public void RestoreSettings()
EOF

for retired_protocol_host_member in \
    'private static bool _requestLoopStarted' \
    'private static async Task RunRequestLoopAsync' \
    'private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)' \
    'private static RuntimeMemorySnapshot CaptureRuntimeMemory()'; do
    forbid_fixed "$unattended_entry_path" "$retired_protocol_host_member" 'protocol host member returned to runner entry:'
done
forbid_fixed "$unattended_entry_path" 'StartNewSingleplayerRun(' 'scenario construction returned outside ScenarioBuilder:'
for assertion_implementation in \
    'VerifyPredictionFailureBoundaries' \
    'ExpectedFinishedTurn is'; do
    forbid_fixed "$unattended_entry_path" "$assertion_implementation" 'unattended assertion returned outside Assertions:'
done
for executor_implementation in \
    'SolverController.SetFullAuto(' \
    'StopAfterExpectedReuse' \
    'orb_differential_' \
    'potion_differential_'; do
    forbid_fixed "$unattended_entry_path" "$executor_implementation" 'unattended executor implementation returned outside Executor:'
done

overlay_snapshot_path="$repository_root/src/UI/SolverOverlaySnapshot.cs"
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing overlay snapshot boundary'
done <<'EOF'
src/UI/SolverOverlaySnapshot.cs	internal sealed record SolverOverlaySnapshot(
src/UI/SolverOverlaySnapshot.cs	public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)
src/UI/SolverOverlay.cs	public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)
src/UI/SolverRouteRow.cs	public void Populate(SolverOverlayTurnSnapshot turn)
src/UI/SolverActionPill.cs	public static Control Create(SolverOverlayActionSnapshot action)
src/Runtime/SolverController.cs	SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
EOF
overlay_renderer_paths=(
    "$repository_root/src/UI/SolverOverlay.cs"
    "$repository_root/src/UI/SolverRouteRow.cs"
    "$repository_root/src/UI/SolverActionPill.cs"
    "$repository_root/src/UI/SolverActionBar.cs"
)
for renderer_path in "${overlay_renderer_paths[@]}"; do
    for mutable_search_type in SolverResult PlanAction PlanCardChoice ModelDb; do
        forbid_fixed "$renderer_path" "$mutable_search_type" 'mutable search type returned to renderer:'
    done
done

bug_report_exporter_path="$repository_root/src/Runtime/CombatBugReportExporter.cs"
diagnostic_journal_path="$repository_root/src/Runtime/CombatDiagnosticJournal.cs"
bug_report_uploader_path="$repository_root/src/Runtime/CombatBugReportUploader.cs"
solver_settings_panel_path="$repository_root/src/UI/SolverSettingsPanel.cs"
solver_settings_general_path="$repository_root/src/UI/SolverSettingsPanel.General.cs"
solver_settings_performance_path="$repository_root/src/UI/SolverSettingsPanel.Performance.cs"
solver_settings_bug_reports_path="$repository_root/src/UI/SolverSettingsPanel.BugReports.cs"
solver_settings_controls_path="$repository_root/src/UI/SolverSettingsPanel.Controls.cs"
while IFS=$'\t' read -r path text; do
    require_fixed "$path" "$text" 'missing bug-report ownership boundary'
done <<EOF
$diagnostic_journal_path	AppendOnlyEventLog<CombatLogEntry>
$diagnostic_journal_path	_session?.Log.CaptureAsync()
$bug_report_exporter_path	Entry.Logger.Journal.CaptureAsync()
$bug_report_exporter_path	WriteDiagnosticLogs(archive, diagnosticLogs)
$bug_report_exporter_path	private static readonly BlockingCollection<Action> BackgroundOperations = new();
$bug_report_exporter_path	QueueCheckpointWrite(session, capture);
$bug_report_exporter_path	Task<ForensicArchiveBundle> forensicsTask = QueueBackground(
$bug_report_exporter_path	ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);
$bug_report_exporter_path	CombatBugReportMetadata.CaptureCombat
$bug_report_uploader_path	ReadMetadata(zipPath, submissionId, description)
$bug_report_uploader_path	AllowAutoRedirect = false
$bug_report_uploader_path	IProgress<CombatBugReportUploadProgress>
$bug_report_uploader_path	HttpCompletionOption.ResponseHeadersRead
$bug_report_uploader_path	CancellationToken requestCancellationToken
$bug_report_uploader_path	ReadServerReceipt(body)
$bug_report_uploader_path	UseProxy = false
$solver_settings_bug_reports_path	private ProgressBar _uploadProgress = null!;
$solver_settings_bug_reports_path	private volatile bool _uploadInProgress;
$solver_settings_bug_reports_path	Interlocked.Exchange(ref _uploadCompletion, completion)
$solver_settings_bug_reports_path	TryApplyUploadCompletion()
$solver_settings_bug_reports_path	等待服务器确认
EOF
forbid_fixed "$bug_report_uploader_path" 'using Godot' 'uploader must not own Godot UI state:'
for legacy_log_read in 'AddFileTail(' 'CaptureLogStarts(' '"*.log"'; do
    forbid_fixed "$bug_report_exporter_path" "$legacy_log_read" 'global log collection must stay out of report exports:'
done

search_completion_notifier_path="$repository_root/src/Runtime/SearchCompletionNotifier.cs"
while IFS=$'\t' read -r path text; do
    require_fixed "$path" "$text" 'missing search completion notification boundary'
done <<EOF
$search_completion_notifier_path	if (!OperatingSystem.IsWindows())
$search_completion_notifier_path	DisplayServer.GetName()
$search_completion_notifier_path	EntryPoint = "Shell_NotifyIconW"
$search_completion_notifier_path	EntryPoint = "LoadIconW"
$search_completion_notifier_path	GetWindowThreadProcessId(foreground, out uint processId)
$search_completion_notifier_path	ShellNotifyIcon(NotifyIconDelete, ref data)
$repository_root/src/Runtime/SolverController.cs	SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Stale)
$repository_root/src/Runtime/PlayerTurnSetupPatches.cs	SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed)
$solver_settings_general_path	CreateSearchCompletionNotificationPolicyInput()
EOF

while IFS=$'\t' read -r path text; do
    require_fixed "$path" "$text" 'missing settings panel ownership boundary'
done <<EOF
$solver_settings_panel_path	TrySelectPage(SettingsPage page)
$solver_settings_panel_path	CommitPending()
$solver_settings_general_path	CreateGeneralPage()
$solver_settings_performance_path	CreatePerformancePage()
$solver_settings_performance_path	SetAdvancedParametersExpanded
$solver_settings_bug_reports_path	CreateBugReportsPage()
$solver_settings_controls_path	CreatePageScroll(Control content)
EOF

mirror_registry_path="$repository_root/src/Engine/Common/Mirrors/MethodMirrorRegistry.cs"
mirror_descriptor_path="$repository_root/src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs"
coverage_catalog_path="$repository_root/tools/inspection/CoverageCatalog/Program.cs"
for text in \
    'Path.Combine(coverageDirectory, "catalog", "generated")' \
    'Path.Combine(coverageDirectory, "catalog", "classifications.json")' \
    'Path.Combine(coverageDirectory, "evidence", "test-evidence.json")' \
    'Path.Combine(repositoryRoot, ".local", "coverage-fixtures")'; do
    require_fixed "$coverage_catalog_path" "$text" 'missing coverage material ownership'
done
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing mirror registry descriptor boundary'
done <<'EOF'
src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs	public interface IMethodMirrorRegistryDescriptorProvider
src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs	public sealed record MethodMirrorRegistryDescriptor(
src/Engine/Common/Mirrors/MethodMirrorRegistry.cs	: IMethodMirrorRegistryDescriptorProvider
src/Engine/Common/Mirrors/MethodMirrorRegistry.cs	public MethodMirrorRegistryDescriptor DescribeMirrorSupport()
tools/inspection/CoverageCatalog/Program.cs	registry is not IMethodMirrorRegistryDescriptorProvider descriptorProvider
tools/inspection/CoverageCatalog/Program.cs	descriptorProvider.DescribeMirrorSupport()
EOF
for private_registry_field in '"_registrations"' '"_inferrer"' '"_strictInferrer"'; do
    forbid_fixed "$coverage_catalog_path" "$private_registry_field" 'private registry reflection returned:'
done
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing relic policy ownership'
done <<'EOF'
src/Search/SearchPolicySnapshot.cs	IReadOnlyList<RelicCounterTarget> RelicTargets
src/Search/CombatBeamSolver.Phases.cs	policy.RelicTargetsSatisfied(node.Snapshot.RelicCounters)
src/Search/CombatSearchCoordinator.cs	policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)
src/Runtime/SolvedRouteCache.cs	policy.RelicTargets
src/Search/CombatBeamSolver.Expansion.Opening.cs	ApplyFixedPrefix(seed, prefix)
src/UI/SolverRelicStrategyPanel.cs	row.Enabled.ButtonPressed
EOF
for rule in 'SimulationNotificationIsolation.IsActive' '"DynamicVarUpgrades"' 'table.TryGetValue(source' 'Tips.TryGetValue(__0'; do
    require_fixed "$repository_root/src/Runtime/DynamicVarCloneMetadataPatches.cs" "$rule" 'missing sparse metadata boundary'
done
forbid_fixed "$repository_root/src/Runtime/DynamicVarCloneMetadataPatches.cs" '.Clear()' 'global metadata clearing is forbidden:'
forbid_fixed \
    "$repository_root/src/Search/SimulatedCombatState.cs" \
    '_monsterAiStates?.Remove(creature)' \
    'active-roster removal must retain known-monster AI state through move completion:'

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing exact metadata reuse boundary'
done <<'EOF'
src/Runtime/PowerAmountComparisonPatch.cs	Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)
src/Runtime/PowerAmountComparisonPatch.cs	if (matches.Count != 2
src/Runtime/PowerAmountComparisonPatch.cs	code[i].labels.Count != 0 || code[i].blocks.Count != 0
src/Search/SimulatedCombatState.cs	IReadOnlyList<PowerModel>? powers = effectivePrefix is not null ? _effectivePowers : null;
src/Search/SimulatedCombatState.cs	_effectiveHookListenerPrefix = null;
src/Search/SimulatedCombatState.Fork.cs	ReferenceEquals(_activeHookListenerPrefix, _effectiveHookListenerPrefix)
src/Search/SimulatedCombatState.cs	private IReadOnlyList<AbstractModel> GetBaseHookListenerPrefix()
src/Search/SimulatedCombatState.cs	if (insertionIndex < 0 && requirePrefixAnchor)
src/Search/SimulatedCombatState.cs	_baseHookListenerPrefix = null;
src/Search/SimulatedCombatState.cs	private void InvalidateCardAndOrbHookListeners()
src/Search/SimulatedCombatState.Fork.cs	fork._baseHookListenerPrefix = RemapCachedModels(_baseHookListenerPrefix, context);
src/Search/CombatBeamSolver.BeamRetentionPolicy.cs	group.RankSummary = new(
src/Search/CombatBeamSolver.BeamRetentionPolicy.cs	ComputeRoutingParentRetentionRank(group)
src/Engine/Common/MirroredHookListenerFilter.cs	shared.Matches(source)
src/Engine/Common/MirroredHookListenerFilter.cs	Volatile.Write(ref _sharedLayouts[slot], layout)
src/Engine/Common/MirroredHookListenerFilter.cs	source.Count <= MaxSharedLayoutLength
src/Engine/Common/MirroredHookListenerFilter.cs	BaseHooks.Append(NativeKeywordHook)
src/Engine/InCombat/Simulation/CombatPredictedCardExtensions.cs	!listeners.HasAny(MirroredHookMask.TryModifyKeywordsInCombat)
EOF

for file in CombatBeamSolver.RetentionJobs.cs CombatBeamSolver.BeamRetentionPolicy.cs CombatBeamSolver.BeamRetentionPolicy.OrderedMutation.cs CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs CombatBeamSolver.BeamRetentionPolicy.Ranking.cs CombatBeamSolver.BeamRetentionPolicy.Routing.cs CombatBeamSolver.BeamRetentionPolicy.Testing.cs; do
    forbid_fixed "$search_root/$file" 'Parallel.For(' 'retention work bypassed fixed lanes:'
    forbid_fixed "$search_root/$file" 'Task.Run(' 'retention work bypassed fixed lanes:'
done

# A new facade/default-hook callback must join the dispatch layout before it can be skipped.
mirrored_filter_path="$repository_root/src/Engine/Common/MirroredHookListenerFilter.cs"
while IFS= read -r mirrored_hook_name; do
    require_fixed "$mirrored_filter_path" "nameof(AbstractModel.$mirrored_hook_name)" 'missing mirrored hook participation metadata'
done < <(
    {
        printf '%s\n' 'TryModifyKeywordsInCombat'
        rg --no-filename -o 'nameof\(AbstractModel\.[A-Za-z][A-Za-z0-9]*\)' "$repository_root/src/Engine/InCombat/Mirrors" | sed -E 's/nameof\(AbstractModel\.([A-Za-z0-9]+)\)/\1/'
        rg --no-filename -o '(listener|modifier)\.[A-Za-z][A-Za-z0-9]*\(' "$repository_root/src/Engine/InCombat/Mirrors/HookMirrors.cs" | sed -E 's/(listener|modifier)\.([A-Za-z0-9]+)\(/\2/'
    } | sort -u
)

# Native clone eligibility stays outside search scheduling and keeps the runtime gate.
require_fixed "$repository_root/src/Runtime/BaseLibCloneConcurrencyPatch.cs" 'BaseLibCloneConcurrency.Enter()' 'missing native framework clone gate'
require_fixed "$repository_root/src/Engine/Common/PredictionUtils.cs" 'NativeModelCloneConcurrency.CanCloneIndependently(source)' 'missing audited prediction clone boundary'
forbid_fixed "$repository_root/src/Engine/Common/NativeModelCloneConcurrency.cs" 'CombatSolver.Search' 'clone eligibility depends on search policy:'

# Stable manual-potion prefixes share action completion and retain ordinary Fork guards.
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing potion continuation ownership boundary'
done <<'EOF'
src/Prediction/PotionChoiceContinuation.cs	seed.AssertForkable();
src/Prediction/PotionChoiceContinuation.cs	lock (_gate)
src/Prediction/PotionChoiceContinuation.cs	!PotionChoiceMirrors.RequiresChoice(potion)
src/Search/CombatBeamSolver.PotionChoiceContinuation.cs	ReferenceEquals(_parent, candidate)
src/Search/CombatBeamSolver.PotionChoiceContinuation.cs	_run.PotionChoicePrefixForks++;
src/Search/CombatBeamSolver.Expansion.Replay.cs	PotionExecutionSupport.Complete(
src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs	PotionCheckpoint?.Dispose();
src/Search/CombatBeamSolver.ParallelExpansion.cs	_run.PotionChoicePrefixForks += source.PotionChoicePrefixForks;
EOF
for forbidden in 'Task<' 'Func<' 'Action<'; do
    forbid_fixed "$repository_root/src/Prediction/PotionChoiceContinuation.cs" "$forbidden" 'potion continuation retained an executable closure:'
done

# Nested execution saves owned data frames and preserves the ordinary transaction guards.
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing execution continuation ownership boundary'
done <<'EOF'
src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs	GuardOrdinaryExecutionContinuationFork();
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	if (_owner.HasCapturedExecutionContinuation && !_acknowledged)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	StateStore.SupportsManualCardChoiceContinuation
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	DetachPendingExecutionChoice();
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	CombatPredictionState state = State.Fork(context);
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	step.Scopes?.Fork(context), step.Frame.Fork(context)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs	using (_trace.ResumeExecution(step.Trace))
src/Engine/InCombat/Simulation/CombatPredictionSimulator.DrawContinuation.cs	mapped! : card.Fork(context)
src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs	unresolved.SetEquals(deferred)
src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs	active.SetEquals(activePlays)
src/Search/SimulatedCombatState.ExecutionScopes.cs	ForkExecutionDeaths(Deaths, context);
src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs	tail.ConsumedChoices != prefix.Count
src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs	ReferenceEquals(_parent, candidate)
src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs	lock (_gate)
src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs	_simulator = null; _parent = null; _action = null; _prefix = null; _continuation = null;
src/Search/CombatBeamSolver.ParallelExpansion.cs	_run.ExecutionChoiceReuses += source.ExecutionChoiceReuses;
EOF
for relative_path in \
    src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs \
    src/Search/SimulatedCombatState.ExecutionScopes.cs \
    src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs; do
    for forbidden in 'Task<' 'Func<' 'Action<'; do
        forbid_fixed "$repository_root/$relative_path" "$forbidden" 'execution continuation retained an executable closure:'
    done
done

# A suspended own-choice frame belongs to its continuation; ordinary Fork remains strict.
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing card continuation ownership boundary'
done <<'EOF'
src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs	GuardOrdinaryCardContinuationFork();
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	context.Register(source.Play, play);
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	StateStore.SupportsManualCardChoiceContinuation
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	DetachPendingManualCardChoice();
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	source.Choice.Fork(context)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	frame.Choice.Resolve(this, frame.Card)
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs	child._blockGainedByCardPlay.Add(play, block)
src/Engine/InCombat/Simulation/CombatPredictionHistory.CardContinuation.cs	e.Options.Select(CopyOption)
src/Search/SimulatedCombatState.CardContinuation.cs	Options = spec.Options.Select(context.RequireRemap)
src/Prediction/CardChoiceContinuation.cs	lock (_gate)
src/Search/CombatBeamSolver.CardChoiceContinuation.cs	ReferenceEquals(_parent, candidate)
src/Search/CombatBeamSolver.CardChoiceContinuation.cs	return Enumerate(this, checkpoint, branches);
src/Search/CombatBeamSolver.Expansion.Replay.cs	countTransition: false
src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs	CardCheckpoint?.Dispose();
src/Search/SimulatedCombatState.CardContinuation.cs	_cardExecutionScopeDepth != 0
EOF
for forbidden in 'Task<' 'Func<' 'Action<'; do
    forbid_fixed "$repository_root/src/Prediction/CardChoiceContinuation.cs" "$forbidden" 'continuation retained an executable closure:'
done

require_fixed "$repository_root/src/Search/CombatBeamSolver.cs" 'policy.RequestWorkTotals ?? new()' 'loop budget/history ownership changed'
require_fixed "$repository_root/src/Search/CombatBeamSolver.CycleReplay.cs" '_replayWork.TryConsumeCycleReplayAction()' 'loop budget/history ownership changed'
require_fixed "$repository_root/src/Runtime/CombatRootSnapshot.cs" 'playerState.AllCards.Cast<AbstractModel>()' 'loop budget/history ownership changed'
require_fixed "$repository_root/src/Search/CombatBeamSolver.StateEvaluation.cs" '_historyDependencies' 'loop budget/history ownership changed'
require_fixed "$repository_root/src/Search/CombatHistoryCounterKey.cs" 'simulator.History.GetCounters(owner)' 'history key must consume incremental totals'
for history_file in CombatPredictionHistory.cs CombatPredictionHistory.CardContinuation.cs CombatPredictionHistory.ExecutionContinuation.cs; do
    require_fixed "$repository_root/src/Engine/InCombat/Simulation/$history_file" '_counterOwner, _counters' 'history forks must inherit counters'
done
require_fixed "$repository_root/src/Search/CombatBeamSolver.Models.cs" 'TranspositionCapDiagnostics TranspositionDiagnostics' 'cap observations must be owned by the search run'
require_fixed "$repository_root/src/Search/SearchPolicySnapshot.cs" 'DefaultTranspositionEntryLimit = 1_000_000' 'production transposition entry limit changed'

# Contextual estimates may influence intermediate ordering only; loading stays outside workers.
require_fixed "$repository_root/tools/search/ContextualOrdering/first_loss.py" "key = (o['solverId'], o['boundaryId'])" 'search loss query must group by solver and boundary identity'
require_fixed "$search_root/FrontierContinuationScheduler.cs" 'attributionPurpose: request.Purpose' 'missing request work attribution boundary'
require_fixed "$search_root/SearchRequestWorkTotals.cs" 'AttributionSnapshot()' 'missing request work attribution boundary'
require_fixed "$repository_root/tools/replay/CheckpointTool/StrategySessionRunner.cs" 'timeout-progress.json' 'strategy session timeout must preserve its last progress snapshot'
require_fixed "$repository_root/src/Testing/Host/DevelopmentMonitorPublisher.cs" '["memberMaxNodes"] = progress?.MaxNodes' 'timeout progress must include the active member node limit'
require_fixed "$repository_root/src/Search/CombatSearchCoordinator.PotionChain.cs" 'FrontierContinuationScheduler' 'missing generated potion chain boundary'
require_fixed "$repository_root/src/Search/CombatBeamSolver.Expansion.Opening.cs" 'BuildFreeEntropicPotionActionsAfterPrefix' 'missing generated potion chain boundary'
require_fixed "$repository_root/src/Search/SimulatedCombatState.Potions.cs" 'IsFreeEntropicPotionAtSlot' 'missing generated potion chain boundary'
require_fixed "$repository_root/src/Testing/Host/UnattendedTestRunner.ProtocolHost.cs" 'new BeamWeightPerturbation(' 'missing frozen Beam weight probe boundary'
require_fixed "$repository_root/src/Runtime/SolverController.cs" 'UnattendedTestRunner.BeamWeightPerturbationOverride' 'missing frozen Beam weight probe boundary'
require_fixed "$search_root/ContextualRankingModel.cs" 'stackalloc double[FeatureCount]' 'contextual ranking must keep its feature buffer local'
require_fixed "$search_root/ContextualRankingModel.cs" 'ModuleVersionId' 'contextual model must validate assembly identity'
for token in 'File.' 'SolverSettings.Current' 'SolverController' 'ComparePrimaryQuality'; do
    forbid_fixed "$search_root/ContextualRankingModel.cs" "$token" 'contextual estimate crossed its pure ranking boundary:'
done
for file in CombatBeamSolver.FinalPlanOrdering.cs CombatBeamSolver.Transpositions.cs; do
    for token in ContextualRanking ContinuousThreatRanking StopPortfolioAtHpTarget BeamWeightPerturbation OffensiveRefinementPortfolio BoundedOffensiveRefinementPortfolio ReallocatedRefinementPortfolio; do
        forbid_fixed "$search_root/$file" "$token" 'intermediate estimate must not become final policy or exact dominance:'
    done
done

if rg -q -F '._vars' "$repository_root/src" --glob '!**/DynamicVarSetAccess.cs'; then
    violations+=("DynamicVarSet._vars direct field access must go through DynamicVarSetAccess")
fi

maintained_testing_root="$repository_root/src/Testing"
for file in "$maintained_testing_root"/*.cs; do
    if [[ -f "$file" ]]; then
        violations+=("Testing source belongs in its responsibility directory; keep the root for navigation.")
    fi
done
for directory in "$maintained_testing_root"/*/; do
    case "$(basename "$directory")" in
        Host|Support|Replay|Contracts|Regressions) ;;
        *) violations+=("Unexpected Testing responsibility directory: $directory") ;;
    esac
done
require_fixed "$repository_root/CombatSolver.csproj" '<Compile Remove=".local/**/*.cs" />' 'normal builds must exclude temporary test source'
for member in 'private sealed record KnownRoutePrefix(' 'private static MoveStateSnapshot[] CaptureKnownRouteRootStates(' 'private static KnownRoutePrefix FreezeKnownRoutePrefix(' 'private void AssertKnownRouteAliasSnapshot('; do
    require_fixed "$maintained_testing_root/Support/UnattendedTestRunner.RouteSnapshots.cs" "$member" 'missing shared route snapshot helper'
done

if ((${#violations[@]} > 0)); then
    printf '%s\n' "${violations[@]}" >&2
    printf 'Refactor boundary verification failed with %d violation(s).\n' "${#violations[@]}" >&2
    exit 1
fi

if grep -Eq '\b(Godot|SolverController|RunManager)\b' "$repository_root/src/Replay/CheckpointArchive.cs"; then
    echo 'Checkpoint archive contract must remain independent of the game runtime.' >&2
    exit 1
fi
if grep -Fq 'ApplyReplayStateAsync(' "$repository_root/src/Testing/Replay/UnattendedTestRunner.NativeReplay.cs"; then
    echo 'Native recorded replay must reconstruct state through native actions.' >&2
    exit 1
fi
printf 'REFACTOR_BOUNDARIES_OK search_files=%d\n' "${#search_files[@]}"
