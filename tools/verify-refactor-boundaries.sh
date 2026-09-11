#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"
search_root="$repository_root/src/Search"
violations=()

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

for relative_path in \
    src/Search/CombatBeamSolver.Expansion.cs \
    src/Runtime/LiveEndTurnRiskEvaluator.cs \
    src/Testing/UnattendedTestRunner.cs \
    src/Testing/UnattendedTestRunner.Potions.cs; do
    for reference in \
        'CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(' \
        'TurnStartRelicSupport.TriggerAfterSideTurnEnd(' \
        'EndTurnPowerSupport.TriggerLate('; do
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
    "$search_root/CombatBeamSolver.OrderedMutationRetention.cs"
)
legacy_loop_guard_paths=(
    "$search_root/CombatBeamSolver.Expansion.cs"
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
    else
        ordered_coordinator_path="$search_root/CombatBeamSolver.BeamRetentionPolicy.cs"
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
opening_channel_line="$(rg --line-number --fixed-strings \
    'List<List<SearchNode>> openingChannels = pool' \
    "$search_root/CombatBeamSolver.Retention.cs" | head -n 1 | cut -d: -f1)"
ordered_coordinator_line="$(rg --line-number --fixed-strings \
    'Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);' \
    "$search_root/CombatBeamSolver.Retention.cs" | head -n 1 | cut -d: -f1)"
cycle_region_line="$(rg --line-number --fixed-strings \
    'cycleRegionTransaction = ApplyCycleRegionRetention(' \
    "$search_root/CombatBeamSolver.Retention.cs" | head -n 1 | cut -d: -f1)"
if [[ -z "$opening_channel_line" || -z "$ordered_coordinator_line" \
    || -z "$cycle_region_line" \
    || "$opening_channel_line" -ge "$ordered_coordinator_line" \
    || "$ordered_coordinator_line" -ge "$cycle_region_line" ]]; then
    add_violation \
        "$search_root/CombatBeamSolver.Retention.cs: opening/independent channels must settle before ordered admission, which must settle before CycleRegion"
fi
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

for file in "${search_files[@]}"; do
    for reference in \
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
for session_type in SolverCombatSession SolverSearchSession SolverDeploymentSession; do
    require_fixed "$session_path" "class $session_type" 'missing controller session type'
done

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing Fork boundary'
done <<'EOF'
src/Engine/Common/PredictionForking.cs	interface IPredictionForkBoundary
src/Engine/Common/PredictionStateStore.cs	boundary.AssertForkable()
src/Search/SimulatedCombatState.Fork.cs	_activeActionChoices
src/Search/SimulatedCombatState.Fork.cs	_activeCardExecutionDeaths
src/Engine/InCombat/Mirrors/Hooks/Card/CardPlayHookPredictionStates.cs	Cannot fork Pen Nib
src/Engine/InCombat/Mirrors/Hooks/Card/AfterCardPlayedMirrors.cs	Cannot fork Curl Up
EOF

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing pre-combat isolation boundary'
done <<'EOF'
src/Api/PreCombatForecastApi.cs	public static class PreCombatForecastApi
src/Api/PreCombatLiveStateSnapshot.cs	RunManager.Instance.ToSave(null)
src/Api/PreCombatRunSerialization.cs	point["can_modify"] = false
src/Api/PreCombatRunSerialization.cs	eventChoice["variables"] is JsonObject { Count: 0 }
src/Api/PreCombatForecastWorker.cs	COMBATSOLVER_PRECOMBAT_WORKER
src/Api/PreCombatForecastWorker.cs	ExpectedLoadedMods = expectedMods
src/Api/PreCombatForecastWorker.cs	EnableNoGcRegionForTest = false
src/Api/PreCombatForecastWorker.cs	PreCombatInterveningMapPoints = options.InterveningMapPoints
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	EnterMapCoordDebug
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	PreCombatPlayerHp:
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	DirectRunSnapshot:ExactStateRestored
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
for gc_chain_rule in \
    'return WaitForReclaimChainAsync(_reclaimTask)' \
    'CollectGeneration2InBackgroundAsync(inSearchCheckpoint: true)' \
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
src/Search/CombatBeamSolver.Phases.cs	SearchWaveMemoryPolicy.Capacity(
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
src/Runtime/SolverController.cs	CombatRootSnapshot.Capture(state)
src/Runtime/PlayerTurnSetupPatches.cs	CombatRootSnapshot.Capture(combat)
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
    'Shiv when combat.GetAmount<FanOfKnivesPower>' \
    'SovereignBlade when combat.GetAmount<SeekingEdgePower>'; do
    require_fixed "$card_targeting_path" "$targeting_rule" 'missing simulated card targeting rule'
done

expected_beam_files=(
    CombatBeamSolver.cs
    CombatBeamSolver.AdmittedExpansion.cs
    CombatBeamSolver.BeamRetentionPolicy.cs
    CombatBeamSolver.CrossTurnPlanning.cs
    CombatBeamSolver.CyclePlanning.cs
    CombatBeamSolver.CycleRegionRetention.cs
    CombatBeamSolver.DeferredFrontier.cs
    CombatBeamSolver.Expansion.cs
    CombatBeamSolver.FinalPlanOrdering.cs
    CombatBeamSolver.Models.cs
    CombatBeamSolver.OrderedMutationRetention.cs
    CombatBeamSolver.ParallelExpansion.cs
    CombatBeamSolver.PathDiagnostics.cs
    CombatBeamSolver.Phases.cs
    CombatBeamSolver.PrimaryChoiceReplay.cs
    CombatBeamSolver.ReadView.cs
    CombatBeamSolver.Retention.cs
    CombatBeamSolver.RetentionJobs.cs
    CombatBeamSolver.RoundLifecycle.cs
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
CombatBeamSolver.cs	private readonly SearchRunContext _run = new(
CombatBeamSolver.cs	private BeamRetentionPolicy Retention =>
CombatBeamSolver.cs	private FinalPlanOrdering FinalOrdering =>
CombatBeamSolver.BeamRetentionPolicy.cs	private sealed class BeamRetentionPolicy(
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankBest(
CombatBeamSolver.BeamRetentionPolicy.cs	private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>
CombatBeamSolver.BeamRetentionPolicy.cs	public void Clear() => NodesByChoice.Clear();
CombatBeamSolver.BeamRetentionPolicy.cs	routingNodes = new RoutingChoiceNodes(node);
CombatBeamSolver.BeamRetentionPolicy.cs	ReturnRoutingChoiceScratch(scratch);
CombatBeamSolver.Models.cs	private readonly record struct TranspositionLabel(
CombatBeamSolver.Models.cs	private sealed class SearchRunContext(
CombatBeamSolver.Models.cs	private readonly record struct SearchFeatures(
CombatBeamSolver.ParallelExpansion.cs	private sealed partial class ParallelExpansionExecutor : IDisposable
CombatBeamSolver.ParallelExpansion.cs	public ExpansionWorkerOutcome[] Evaluate(
CombatBeamSolver.ParallelExpansion.cs	public int MaximumQueuedParents => SearchWaveMemoryPolicy.MaximumQueuedParents(DegreeOfParallelism);
CombatBeamSolver.ParallelExpansion.cs	List<ExpansionLane> lanes = new(DegreeOfParallelism);
CombatBeamSolver.AdmittedExpansion.cs	private ExpansionWorkerOutcome[] EvaluateQueuedParents(
CombatBeamSolver.AdmittedExpansion.cs	private sealed class AdmittedParent(
CombatBeamSolver.AdmittedExpansion.cs	public object ForkGate { get; } = new();
CombatBeamSolver.AdmittedExpansion.cs	_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
CombatBeamSolver.AdmittedExpansion.cs	wave.BackgroundCompleted.Wait();
CombatBeamSolver.AdmittedExpansion.cs	while (committed < parents.Length && parents[committed]!.TailCompleted)
CombatBeamSolver.AdmittedExpansion.cs	_completedActions == Actions.Count && _completedPotions == Potions.Count
CombatBeamSolver.AdmittedExpansion.cs	ready.TransferPotionTo(Aggregate!, candidate);
CombatBeamSolver.PrimaryChoiceReplay.cs	private sealed class PrimaryChoiceReplayFrontier : IDisposable
CombatBeamSolver.PrimaryChoiceReplay.cs	=> branches >= 2 && finals >= branches && attempts >= branches;
CombatBeamSolver.PrimaryChoiceReplay.cs	public bool CanDispatchContinuation => CompletedReplays == Actions.Length
CombatBeamSolver.PrimaryChoiceReplay.cs	if (!budget.TrySpendReplayAttempt())
CombatBeamSolver.PrimaryChoiceReplay.cs	frontier.AssertConsumed();
CombatBeamSolver.PrimaryChoiceReplay.cs	if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)
CombatBeamSolver.Models.cs	public ParallelExpansionExecutor? ActiveParallelExpansion;
CombatBeamSolver.ParallelExpansion.cs	_coordinator._run.ActiveParallelExpansion = null;
CombatBeamSolver.StandPatJobs.cs	private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)
CombatBeamSolver.StandPatJobs.cs	seen.Add(node.StateKey)
CombatBeamSolver.StandPatJobs.cs	_run.StandPatCache.Add(pending[index].StateKey, evaluations[index]);
CombatBeamSolver.StandPatJobs.cs	ExpansionLane[] lanes = EnsureBackgroundLanes();
CombatBeamSolver.StandPatJobs.cs	_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);
CombatBeamSolver.StandPatJobs.cs	wave.Completed.Wait();
CombatBeamSolver.RetentionJobs.cs	public void EvaluateRetentionIndices(
CombatBeamSolver.RetentionJobs.cs	ExpansionLane[] lanes = EnsureBackgroundLanes();
CombatBeamSolver.RetentionJobs.cs	wave.Completed.Wait();
CombatBeamSolver.RetentionJobs.cs	_coordinator._run.OffThreadAllocatedBytes += job.AllocatedBytes;
CombatBeamSolver.RetentionJobs.cs	wave.Error?.Throw();
CombatBeamSolver.BeamRetentionPolicy.cs	_run.RoutingChoiceSummaryBuilds += summaryGroups.Length;
CombatBeamSolver.BeamRetentionPolicy.cs	RequestOrderedMutationObservation(candidate);
SearchWaveMemoryPolicy.cs	return checked(degreeOfParallelism * 2);
SearchWaveMemoryPolicy.cs	current >= maximum - current ? maximum : current * 2
CombatBeamSolver.Phases.cs	SearchWaveMemoryPolicy.GrowCapacity(
CombatBeamSolver.Retention.cs	end.ReleaseSimulator();
CombatBeamSolver.ParallelExpansion.cs	private void CommitExpansionBatch(
CombatBeamSolver.Phases.cs	public SolverResult Solve()
CombatBeamSolver.Expansion.cs	private IEnumerable<SearchNode> Expand(SearchNode node)
CombatBeamSolver.RoundLifecycle.cs	private SearchBoundaryReason AdvanceRound(
CombatBeamSolver.RoundLifecycle.cs	private SearchBoundaryReason AdvancePlayerTurnStart(
CombatBeamSolver.RoundLifecycle.cs	return AdvancePlayerTurnStart(
CombatBeamSolver.RoundLifecycle.cs	private sealed class RoundPrefixReplayContext(
CombatBeamSolver.RoundLifecycle.cs	private SearchBoundaryReason ResumeRoundPrefix(
CombatBeamSolver.Expansion.cs	using RoundPrefixReplayContext? roundPrefix
SimulatedCombatState.ActionChoices.cs	internal CombatPredictionSimulator ForkCompletedRoundPrefix(
SimulatedCombatState.ActionChoices.cs	!cursor.IsEmptyCompletedPhaseCursor
SimulatedCombatState.ActionChoices.cs	return simulator.Fork();
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)
CombatBeamSolver.FinalPlanOrdering.cs	private sealed class FinalPlanOrdering(
CombatBeamSolver.FinalPlanOrdering.cs	public FinalPlanSelection Select(
CombatBeamSolver.Terminal.cs	private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)
CombatBeamSolver.StateEvaluation.cs	private SimulationSnapshot Snapshot(
EOF

require_fixed \
    "$search_root/CombatBeamSolver.Expansion.cs" \
    'CreateWholeActionChoiceBudget' \
    'repeated card choices are missing their whole-action branch quota:'

if rg -q 'private SearchBoundaryReason (AdvanceRound|AdvancePlayerTurnStart)\(' "$search_root/CombatBeamSolver.Expansion.cs"; then
    add_violation 'CombatBeamSolver.Expansion.cs: round lifecycle must remain in RoundLifecycle'
fi

path_diagnostics_path="$search_root/CombatBeamSolver.PathDiagnostics.cs"
require_fixed "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" 'HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null' 'ordinary tactical ties must use the existing retained routing semantics:'
require_fixed "$search_root/CombatBeamSolver.BeamRetentionPolicy.cs" 'if (values.HasRetainedRoutingChoice)' 'ordinary tactical ties must leave routing positions unchanged:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.SearchPolicy.cs" 'seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);' 'ordinary tactical ties lost the interleaved routing-position contract:'
deferred_frontier_path="$search_root/CombatBeamSolver.DeferredFrontier.cs"
require_fixed "$deferred_frontier_path" 'private sealed class DeferredTurnFrontier(' 'deferred frontier ownership is missing:'
require_fixed "$deferred_frontier_path" '_run.DeferredFrontierReplayActions++;' 'deferred replay action accounting is missing:'
require_fixed "$deferred_frontier_path" 'node with { Snapshot = replayed }' 'deferred replay must preserve post-final node history:'
require_fixed "$search_root/CombatBeamSolver.Phases.cs" 'CaptureDeferredFrontier(nextPlays, prunedPlays);' 'deferred capture must follow final prune:'
require_fixed "$search_root/CombatSearchCoordinator.FailureRecovery.cs" 'RecoverDeferredTurnFrontier = true' 'deferred frontier must remain tied to failed-layer recovery:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111' 'deferred frontier contract lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownCustomDeferredFrontier.cs" 'MetadataContractOnly:NotFrontierQualityOrPerformance' 'deferred contract must distinguish metadata from search quality:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-GENERATION-CONTEXT-V0111' 'generation context replay lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-GENERATION-SUFFIX-V0111' 'generation context frozen suffix replay lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-VARIANT-PATH-TRACE-V0111' 'proved variant path trace lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-SOUL-RETAINED-PATH-TRACE-V0111' 'retained variant alias proof lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs" 'requiredRetentionStep: 18, proveRetentionAliases: true' 'retained variant must strictly prove the actual observed prefix suffix:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs" 'RunKnownSoulGenerationContext(combat, player, fullKnownSuffix: true, frozenVariants: variants);' 'variant trace must prove the complete alternative suffixes before search:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs" 'watched.UnionWith(variants.Values.SelectMany(variant => variant.Prefixes)' 'variant trace must watch all proved prefix states:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs" 'exact.GroupBy(item => new { item.PolicyLabel, item.ParentPolicyLabel })' 'variant trace must report separate observed current and parent policy buckets:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111' 'multi-enemy known route lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-PATH-TRACE-V0111' 'multi-enemy path trace lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111' 'multi-enemy post-generation path trace lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.Executor.cs" 'KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111' 'multi-enemy native replay lost its executor entry:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs" 'CaptureKnownRouteRootStates(root, player, enemies)' 'path trace must guard all original enemy identities:'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.KnownExoskeletonsPathTrace.cs" 'RunKnownExoskeletonsRouteReplay(combat, player, freeze: frozen);' 'multi-enemy path trace must first prove and freeze the real route:'
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
beam_phases_path="$search_root/CombatBeamSolver.Phases.cs"
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
src/Testing/UnattendedTestRunner.CombatRootSnapshot.cs	workerLiveConstructorRejected
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
src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardPile.cs	int maxHandSize = GetMaxHandSize(player)
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

unattended_entry_path="$repository_root/src/Testing/UnattendedTestRunner.cs"
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing headless infrastructure ownership boundary'
done <<'EOF'
tools/run-unattended-test.sh	source "$script_dir/headless-runtime.sh"
tools/run-unattended-test.sh	hr_acquire "$process_pid" "$process_identity_start_time"
tools/run-unattended-test.sh	if ((option_value[stop-instance] == 1)); then
tools/run-unattended-test.ps1	. (Join-Path $PSScriptRoot 'headless-runtime.ps1')
tools/run-unattended-test.ps1	if ($StopInstance) {
tools/run-headless-matrix.sh	--stop-instance
tools/run-headless-matrix.ps1	"-StopInstance"
tools/headless-runtime.sh	hr_prepare_snapshot() {
tools/headless-runtime.sh	hr_bind() {
tools/headless-runtime.ps1	function Set-HeadlessGameSnapshot(
tools/headless-runtime.ps1	function Enter-HeadlessHostLease(
tools/headless-runtime.ps1	function Set-HeadlessHostGame(
EOF
for matrix in "$repository_root/tools/run-headless-matrix.sh" "$repository_root/tools/run-headless-matrix.ps1"; do
    forbid_fixed "$matrix" 'MATRIX-CLEANUP' 'matrix cleanup must not dispatch a new game request:'
done
for helper in "$repository_root/tools/headless-runtime.sh" "$repository_root/tools/headless-runtime.ps1"; do
    forbid_fixed "$helper" 'combat_solver_test_request.json' 'request protocol leaked into headless resource owner:'
    forbid_fixed "$helper" 'SolverSettings' 'game settings leaked into headless resource owner:'
done
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing unattended protocol boundary'
done <<'EOF'
src/Testing/UnattendedTestRunner.cs	private static readonly ProtocolHost Host = new();
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private sealed class ProtocolHost
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private async Task RunRequestLoopAsync(NGame host)
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private void Activate(UnattendedTestRequest request)
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private void Reset()
src/Testing/UnattendedTestRunner.Writer.cs	private sealed class Writer(
src/Testing/UnattendedTestRunner.Writer.cs	public RuntimeMemorySnapshot Write(
src/Testing/UnattendedTestRunner.Writer.cs	private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	private sealed class ScenarioBuilder(
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	public async Task<ScenarioContext> BuildAsync()
src/Testing/UnattendedTestRunner.ScenarioBuilder.cs	public CombatState? CombatState { get; private set; }
src/Testing/UnattendedTestRunner.Assertions.cs	private sealed class Assertions(
src/Testing/UnattendedTestRunner.Assertions.cs	public async Task RunBeforeExecutionAsync(ScenarioContext scenario)
src/Testing/UnattendedTestRunner.Assertions.cs	public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)
src/Testing/UnattendedTestRunner.Executor.cs	private sealed class Executor(
src/Testing/UnattendedTestRunner.Executor.cs	public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)
src/Testing/UnattendedTestRunner.Executor.cs	private FastModeType? ApplySettingsOverrides()
src/Testing/UnattendedTestRunner.Executor.cs	public void RestoreSettings()
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
coverage_catalog_path="$repository_root/tools/CoverageCatalog/Program.cs"
while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing mirror registry descriptor boundary'
done <<'EOF'
src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs	public interface IMethodMirrorRegistryDescriptorProvider
src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs	public sealed record MethodMirrorRegistryDescriptor(
src/Engine/Common/Mirrors/MethodMirrorRegistry.cs	: IMethodMirrorRegistryDescriptorProvider
src/Engine/Common/Mirrors/MethodMirrorRegistry.cs	public MethodMirrorRegistryDescriptor DescribeMirrorSupport()
tools/CoverageCatalog/Program.cs	registry is not IMethodMirrorRegistryDescriptorProvider descriptorProvider
tools/CoverageCatalog/Program.cs	descriptorProvider.DescribeMirrorSupport()
EOF
for private_registry_field in '"_registrations"' '"_inferrer"' '"_strictInferrer"'; do
    forbid_fixed "$coverage_catalog_path" "$private_registry_field" 'private registry reflection returned:'
done
forbid_fixed \
    "$repository_root/src/Search/SimulatedCombatState.cs" \
    '_monsterAiStates?.Remove(creature)' \
    'active-roster removal must retain known-monster AI state through move completion:'

for rule in 'ConditionalWeakTable<Assembly, Resolution>' 'SimulationNotificationIsolation.IsActive' '__0.IsDynamic' 'callbacks.Length != 1'; do
    require_fixed "$repository_root/src/Runtime/RitsuBaseLibTargetTypeLookupPatch.cs" "$rule" 'missing metadata cache boundary'
done

while IFS=$'\t' read -r relative_path text; do
    require_fixed "$repository_root/$relative_path" "$text" 'missing exact metadata reuse boundary'
done <<'EOF'
src/Runtime/PowerAmountComparisonPatch.cs	Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)
src/Runtime/PowerAmountComparisonPatch.cs	if (matches.Count != 2
src/Runtime/PowerAmountComparisonPatch.cs	code[i].labels.Count != 0 || code[i].blocks.Count != 0
src/Runtime/AssemblyTypeAbsenceCache.cs	WeakReference<Assembly>[] DynamicAssemblies
src/Runtime/AssemblyTypeAbsenceCache.cs	AppDomain.CurrentDomain.AssemblyLoad
src/Runtime/AssemblyTypeAbsenceCache.cs	absence.Generation == Volatile.Read(ref _assemblyGeneration)
src/Runtime/AssemblyTypeAbsenceCache.cs	assembly.GetType(markerTypeName, throwOnError: false)
src/Runtime/RitsuBaseLibTargetTypeResolutionPatches.cs	!SimulationNotificationIsolation.IsActive
src/Runtime/RitsuBaseLibTargetTypeResolutionPatches.cs	MissingType.ObserveResult(__state, __result)
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

for file in CombatBeamSolver.RetentionJobs.cs CombatBeamSolver.BeamRetentionPolicy.cs; do
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

# Native duration skips affect both search equivalence and live continuation checks.
for duration_consumer in src/Search/SimulatedCombatState.cs src/Runtime/ContinuationStamp.cs; do
    require_fixed "$repository_root/$duration_consumer" 'PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)' 'duration skip semantics must be shared by keys and continuation'
done
require_fixed "$repository_root/src/Prediction/PowerLifecycleSupport.cs" 'UsesNativeDurationSkip(power.GetType()) && power.SkipNextDurationTick' 'irrelevant debuff metadata must not split state equivalence'

require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" 'simulated.SkipNextDurationTick = true;' 'new native duration state must belong to the owned Power'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" '!PowerLifecycleSupport.UsesNativeDurationSkip(powerType) && !alreadyPresent' 'typed duration application must not duplicate native skip state'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" '!PowerLifecycleSupport.UsesNativeDurationSkip(typeof(T)) && !alreadyPresent' 'monster duration application must not duplicate native skip state'

# The compact executor remains a value-only experiment behind a Testing adapter.
while IFS= read -r compact_path; do
    for native_reference in 'MegaCrit.' 'Godot' 'CombatPredictionSimulator' 'SimulatedCombatState' 'CardModel' 'Task' 'IEnumerator' 'Func<' 'Action<'; do
        forbid_fixed "$compact_path" "$native_reference" 'compact execution must contain only owned values:'
    done
done < <(rg --files "$repository_root/src/Engine/InCombat/Simulation/Compact" -g '*.cs')
while IFS= read -r production_path; do
    for prototype_reference in 'ResumableDiscardProgram' 'CompactDiscardProjection' 'CompactDiscardReadView' 'CompactPhaseProbe' 'CompactCardMetadataReadBinding' 'CompactCardProgramCompiler' 'MonsterEffectProgram'; do
        forbid_fixed "$production_path" "$prototype_reference" 'unvalidated compact prototype reached production:'
    done
done < <(rg --files "$search_root" "$repository_root/src/Runtime" -g '*.cs')
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" 'private T? PreparePowerApplication<T>' 'Power preparation must remain separate from ordered application'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" 'private PowerModel ApplyPreparedPower<T>' 'Power application must preserve the prepared command'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" '_events.Append(State, [item.Data, item.Metadata]);' 'event identities must retain full-width indexed storage'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ReversibleValueBuffer.cs" 'state.Write(_header + TailOffset, leaf);' 'buffer append cursor must belong to reversible values'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'private readonly ReversibleValueBuffer[] _piles;' 'growing piles must use reversible indexed buffers'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" "history?.Owner.Creature, history?.ShivPlays" 'Shiv history must participate in completed state reads'
compact_projection="$repository_root/src/Testing/CompactDiscardProjection.cs"
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/MonsterEffectProgram.cs" '_instructions = instructions.ToArray();' 'monster commands must own immutable captured instructions'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.Monsters.cs" 'if (_monsterMoves == null || !Complete || Terminal || Ending' 'monster execution must require root admission and an idle boundary'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" '_monsterMoves = source._monsterMoves;' 'frozen candidates must retain monster admission and commands'
require_fixed "$compact_projection" 'metadata.CurrentMonsterMove(_creatures[1])' 'monster parameters must come from captured branch metadata'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" 'history?.CreatureAttacks, combatHistory?.CreatureAttacks' 'completed creature attack counts must share the original map encoding'
damage_simulator="$repository_root/src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs"
forbid_fixed "$damage_simulator" 'dealer?.IsDead' 'damage dealers must read branch vitals'
require_fixed "$damage_simulator" 'effects.CompletePlayerDeath(player);' 'player death must run its domain cleanup before orb/pet handling'
for semantic_replay in '.ManualPlay(' '.AutoPlay(' '.Discard(' 'CardOnPlayMirrors.Invoke(' 'HookMirrors.'; do
    forbid_fixed "$compact_projection" "$semantic_replay" 'compact projection must decode events without replaying effects:'
done
require_fixed "$compact_projection" 'Program.State.HasSameRoot(program.State)' 'compact projection lost root ownership guard'
require_fixed "$compact_projection" 'AssertRepresentedHooks(runListeners[index], runPrefix: true, includeHandEnd);' 'compact deck listeners need a separate run-hook audit'
require_fixed "$compact_projection" '(key.RunPrefix || !RepresentedHook(key.Type, method.Name))' 'compact deck hooks cannot borrow combat effect exemptions'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.CompactKernelProfile.cs" 'if (!SimulationNotificationIsolation.IsActive)' 'compact measurements lost production simulation context guard'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.CompactKernel.cs" 'initialIsolation.Dispose();' 'compact thread-static isolation must close before worker await'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.CompactKernel.cs" 'continuationIsolation.Dispose();' 'compact simulation isolation must close before native deployment'
compact_reader="$repository_root/src/Testing/CompactDiscardReadView.cs"
for replay_or_write in '.Materialize(' '.ManualPlay(' '.AutoPlay(' '.MutablePreview' '.State.Write('; do
    forbid_fixed "$compact_reader" "$replay_or_write" 'completed reader must not reconstruct or mutate branch models:'
done
compact_power_reads="$search_root/SimulatedCombatState.CompletedPowerReads.cs"
for replay in '.ManualPlay(' '.AutoPlay(' '.Fork(' 'HookMirrors.' 'PowerCmd.' '.State.Write('; do
    forbid_fixed "$compact_power_reads" "$replay" 'completed Power binding may only import supplied values:'
done
while IFS= read -r production_path; do
    [[ $production_path == "$compact_power_reads" ]] && continue
    forbid_fixed "$production_path" 'CompletedPowerReadBinding' 'completed Power binding is not admitted to production execution:'
done < <(rg --files "$search_root" "$repository_root/src/Runtime" -g '*.cs')
require_fixed "$compact_power_reads" 'state.AssertForkable();' 'completed Power binding requires a stable setup root'
require_fixed "$compact_power_reads" 'model._owner = source.Owner;' 'Power clone must restore captured ownership'
require_fixed "$compact_power_reads" 'private readonly PowerModel[] _replacementModels;' 'reacquired Power metadata must have its own prepared read model'
require_fixed "$compact_power_reads" 'PowerModel model = value.Retired ? _replacementModels[index] : _models[index];' 'Power read lifetime must follow journaled root retirement'
require_fixed "$compact_power_reads" 'model._amount = value.Amount;' 'completed Power reads lost supplied amount authority'
require_fixed "$compact_power_reads" '_state.InvalidateBaseHookListeners();' 'roster changes must invalidate Power owner-anchor order'
compact_card_reads="$repository_root/src/Testing/CompactCardMetadataReadBinding.cs"
for replay in '.ManualPlay(' '.AutoPlay(' '.Fork(' 'HookMirrors.' 'CardCmd.' '.State.Write('; do
    forbid_fixed "$compact_card_reads" "$replay" 'card metadata binding may only import supplied completed values:'
done
require_fixed "$compact_card_reads" 'private readonly List<Binding> _active;' 'card read previews must belong to a private binding'
require_fixed "$compact_card_reads" 'model.EnergyCost.CapturedXValue = captured;' 'completed card metadata lost captured X values'
require_fixed "$compact_card_reads" 'model.HasBeenRemovedFromState = removed;' 'completed card metadata lost removal state'
require_fixed "$compact_reader" '_cards.Read(program);' 'completed card metadata must come from the current program'
require_fixed "$search_root/SimulatedCombatState.cs" "history?.Owner.Creature, history?.Exhausts" 'completed keys lost supplied exhaust history'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'strategicRequirements, view?.CardValuesInvariant == true ? view.Invariants : null' 'card-set changes must bypass invariant strategic summaries'
require_fixed "$compact_reader" '_adapter.CopyPowerReadValues(program, _powerValues);' 'completed Power inputs must come from the value program'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'SnapshotCore(view.EvaluationContext,' 'completed evaluator must consume the lane-owned evaluation context'
require_fixed "$compact_reader" '!_adapter.Program.State.HasSameRoot(program.State) || !program.Complete' 'completed reader lost ownership/stability guard'
require_fixed "$compact_reader" 'ValueRng rng = _program.ShuffleRng;' 'completed reader lost authoritative shuffle state'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'view?.ShuffleRng ?? simulator.Rng.Shuffle.CaptureState()' 'completed state key lost branch shuffle RNG'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'view is null ? simulator.TerminalStamp : view.TerminalStamp' 'completed terminal values must not fall back to stale root state'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'view?.CardHistory, view?.EnemyRoster, view?.CombatHistory' 'completed state key lost the ordered branch roster'
require_fixed "$search_root/CombatBeamSolver.ReadView.cs" 'view?.EnemyValuesInvariant == true ? view.Invariants : null' 'mutable enemy values must bypass root invariant summaries'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/SimCreatureState.cs" '_values.LoseHp(amount)' 'legacy and compact scalar damage must share one arithmetic implementation'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/CreatureValueSlots.cs" 'state.Write(Offset + 3, present ? 1 : 0)' 'creature roster membership must remain journaled separately from HP'
require_fixed "$compact_projection" '=> new(this, _root.Fork(), _player, _risks)' 'each completed reader must own legacy simulator scratch'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'if (ResultPile(card) == Pile.Removed || !Ending)' 'last-hit result movement must respect the native ending gate'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs" 'if (target != 0) _creatures[target].SetPresent(state, false);' 'only enemy death may remove roster membership'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs" 'state.Write(_terminalSlot, DeathCompleted(state, 0) ? 2 : 1);' 'compact victory and defeat must be journaled'
require_fixed "$repository_root/src/Search/SimulatedCombatState.cs" 'history?.Owner, history?.StatusDraws' 'status draws must participate in the original history key'
require_fixed "$repository_root/src/Search/CombatBeamSolver.StateEvaluation.cs" 'view?.CumulativePlayerHpLost ?? combat.GetCumulativeHpLost(_player.Creature)' 'completed evaluation must use current cumulative HP loss'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.HandEnd.cs" 'if (!_handEndAdmitted || !Complete || !Enum.IsDefined(staging))' 'hand-end execution must require root phase admission'
forbid_fixed "$repository_root/src/Engine/InCombat/Simulation/CombatPredictionSimulator.EndTurn.cs" 'SaveManager' 'turn-end execution must not read live animation settings'
require_fixed "$search_root/CompletedStateReadView.cs" 'A new stable root requires a new cache.' 'completed invariant cache lost its root lifetime contract'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/CardEffectProgram.cs" '_instructions = instructions.ToArray();' 'compact definitions must own immutable instruction storage'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'State.Write(Frame + EffectIndexOffset, Read(Frame + EffectIndexOffset) + 1);' 'compact effect position must belong to journaled values'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'State.Write(Frame + FirstDrawnOffset, drawn);' 'draw return values must survive in the journaled frame'
require_fixed "$compact_projection" 'CompactCardProgramCompiler.Compile(card, includeAttacks, shivTemplate, inkyShivTemplate)' 'card admission must use the shared immutable program compiler'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'view?.EnergyCostRng ?? simulator.Rng.CombatEnergyCosts.CaptureState()' 'completed keys must read branch energy-cost RNG'
require_fixed "$compact_card_reads" 'int amount = program.CostModifierAt(card, index);' 'cost previews must import the complete ordered branch modifiers'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/RandomDrawCost.cs" 'Buffer(card).Append(state, [cost]);' 'random draw-cost modifiers must remain journaled'
compact_compiler="$repository_root/src/Testing/CompactCardProgramCompiler.cs"
for replay in '.ManualPlay(' '.AutoPlay(' 'CardOnPlayMirrors.Invoke(' 'HookMirrors.' 'CardCmd.' 'PowerCmd.'; do
    forbid_fixed "$compact_compiler" "$replay" 'card admission must compile definitions without executing effects:'
done
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'State.Write(Frame + DrawResumeIpOffset, resumeIp);' 'shuffle return must retain the pending draw stage'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'sum = checked(sum + _powers!.Amount(State, target, instruction.Power));' 'calculated Power sums must read current values'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'if (CreaturePresent(target) && Creature(target).CurrentHp > 0)' 'calculated Power sums must exclude removed and dead enemies'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'if (!PreparePower(card, target, BasicPowerKind.PiercingWail, amount)) return;' 'temporary effects must pass modifiers before their nested first application'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'CommitPower(card, target, BasicPowerKind.PiercingWail, amount);' 'prepared temporary effects must not apply modifiers twice'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'private void ApplyTemporaryStrengthLoss(int card, int target, int amount)' 'temporary Strength ordering must belong to the value program'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'ValidateBlockReturns(definitions, powers);' 'block-return admission must exclude unrepresented zero-amount instances'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'ApplyPower(card, 0, instruction.Power, (int)returned);' 'deferred Power must use the block command return'
require_fixed "$compact_reader" 'ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.NoDealer' 'indirect damage must not count as powered attack hits'
require_fixed "$repository_root/src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs" 'WriteRng(rng);' 'compact shuffle lost journaled RNG writes'
require_fixed "$repository_root/src/Testing/UnattendedTestRunner.CompactShufflePower.cs" 'isolation.Dispose();' 'expanded compact isolation must close before worker/native awaits'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" '=> SnapshotCore(simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths, null);' 'legacy and completed readers must share full SnapshotCore'
require_fixed "$search_root/CombatBeamSolver.StateEvaluation.cs" 'result.ReleaseSimulator();' 'completed evaluations must release their borrowed root'
while IFS= read -r production_path; do
    [[ $production_path == "$search_root/CombatBeamSolver.StateEvaluation.cs" ]] && continue
    forbid_fixed "$production_path" 'SnapshotFromReadView(' 'closed completed reader is not admitted to production execution:'
done < <(rg --files "$search_root" "$repository_root/src/Runtime" -g '*.cs')
compact_values="$repository_root/src/Engine/InCombat/Simulation/Compact/ReversibleValueState.cs"
compact_frozen="$repository_root/src/Engine/InCombat/Simulation/Compact/ReversibleValueState.FrozenValues.cs"
require_fixed "$compact_values" 'private long[] _values;' 'compact workspace lost exclusive values'
require_fixed "$compact_values" '_dirtyPages[entry.Slot / PageWidth] = true;' 'compact rollback must invalidate frozen pages'
require_fixed "$compact_values" 'if (!source.HasRoot(_rootIdentity))' 'compact restore lost root ownership guard'
require_fixed "$compact_values" 'if (_checkpoints.Count != 0)' 'compact restore lost active checkpoint guard'
require_fixed "$compact_values" 'Resize(checkpoint.SlotCount);' 'compact rollback lost allocated slot ownership'
require_fixed "$compact_frozen" 'workspace.Resize(Count);' 'compact restore lost candidate slot count'
require_fixed "$compact_frozen" '_pages = source._pages.AsSpan(0, PageCount(source.Count)).ToArray();' 'compact candidate must own its published page directory'
require_fixed "$compact_frozen" 'private readonly long[] _values = values;' 'compact page values must remain private and immutable'
for mutable_owner in 'ReversibleValueState _owner' 'ReversibleValueState _workspace' 'FrozenValues _parent'; do
    forbid_fixed "$compact_frozen" "$mutable_owner" 'compact candidate must not retain mutable workers or ancestor chains:'
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
if grep -Fq 'ApplyReplayStateAsync(' "$repository_root/src/Testing/UnattendedTestRunner.NativeReplay.cs"; then
    echo 'Native recorded replay must reconstruct state through native actions.' >&2
    exit 1
fi
printf 'REFACTOR_BOUNDARIES_OK search_files=%d\n' "${#search_files[@]}"
