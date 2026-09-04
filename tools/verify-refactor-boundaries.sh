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

mapfile -d '' -t search_files < <(
    find "$search_root" -type f -name '*.cs' -print0 | sort -z
)
shopt -s nullglob
beam_files=("$search_root"/CombatBeamSolver*.cs)
runtime_files=("$repository_root"/src/Runtime/*.cs)
shopt -u nullglob

cycle_planning_path="$search_root/CombatBeamSolver.CyclePlanning.cs"
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
forbid_regex \
    "$cycle_planning_path" \
    "$scenario_specific_cycle_model_pattern" \
    'generic cycle planning contains a scenario-specific model name or ID:'
for direct_model_lookup_pattern in \
    '\bModelDb\.(?:Card|Power|Relic|Monster)\b' \
    '\bGetAmount<[A-Za-z_][A-Za-z0-9_]*(?:Power|Relic|Monster)>' \
    '\btypeof\([A-Za-z_][A-Za-z0-9_]*(?:Card|Power|Relic|Monster)\)'; do
    forbid_regex \
        "$cycle_planning_path" \
        "$direct_model_lookup_pattern" \
        'generic cycle planning performs a direct concrete-model lookup:'
done

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

for file in "${search_files[@]}"; do
    for reference in \
        'SolverSettings.Current' \
        'Entry.Logger' \
        'SolverController' \
        'SolverOverlay' \
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
    'failure == null && (_regionExitRequired || _reclaimRequired)'; do
    require_fixed "$search_gc_policy_path" "$gc_chain_rule" 'missing serialized reclaim-chain rule'
done
forbid_fixed \
    "$search_gc_policy_path" \
    'ReclaimAfterActiveCheckpointAsync' \
    'recursive reclaim handoff returned:'

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
    CombatBeamSolver.BeamRetentionPolicy.cs
    CombatBeamSolver.CrossTurnPlanning.cs
    CombatBeamSolver.CyclePlanning.cs
    CombatBeamSolver.Expansion.cs
    CombatBeamSolver.FinalPlanOrdering.cs
    CombatBeamSolver.Models.cs
    CombatBeamSolver.ParallelExpansion.cs
    CombatBeamSolver.Phases.cs
    CombatBeamSolver.Retention.cs
    CombatBeamSolver.StateEvaluation.cs
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
CombatBeamSolver.cs	private readonly SearchRunContext _run = new(
CombatBeamSolver.cs	private BeamRetentionPolicy Retention =>
CombatBeamSolver.cs	private FinalPlanOrdering FinalOrdering =>
CombatBeamSolver.BeamRetentionPolicy.cs	private sealed class BeamRetentionPolicy(
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankBest(
CombatBeamSolver.Models.cs	private readonly record struct TranspositionLabel(
CombatBeamSolver.Models.cs	private sealed class SearchRunContext(
CombatBeamSolver.Models.cs	private readonly record struct SearchFeatures(
CombatBeamSolver.ParallelExpansion.cs	private sealed class ParallelExpansionExecutor : IDisposable
CombatBeamSolver.ParallelExpansion.cs	public ExpansionWorkerOutcome[] Evaluate(
CombatBeamSolver.ParallelExpansion.cs	private void CommitExpansionBatch(
CombatBeamSolver.Phases.cs	public SolverResult Solve()
CombatBeamSolver.Expansion.cs	private IEnumerable<SearchNode> Expand(SearchNode node)
CombatBeamSolver.BeamRetentionPolicy.cs	public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)
CombatBeamSolver.FinalPlanOrdering.cs	private sealed class FinalPlanOrdering(
CombatBeamSolver.FinalPlanOrdering.cs	public FinalPlanSelection Select(
CombatBeamSolver.Terminal.cs	private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)
CombatBeamSolver.StateEvaluation.cs	private SimulationSnapshot Snapshot(
EOF

require_fixed \
    "$search_root/CombatBeamSolver.Expansion.cs" \
    'ResolveWholeActionChoiceBranchLimit' \
    'repeated card choices are missing their whole-action branch quota:'

beam_entry_path="$search_root/CombatBeamSolver.cs"
forbid_fixed "$beam_entry_path" 'public SolverResult Solve()' 'Solve returned to the entry/field declaration file:'
beam_retention_facade_path="$search_root/CombatBeamSolver.Retention.cs"
forbid_fixed "$beam_retention_facade_path" 'private List<SearchNode> RankBest(' 'RankBest returned outside BeamRetentionPolicy:'
beam_phases_path="$search_root/CombatBeamSolver.Phases.cs"
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
    require_fixed "$repository_root/$relative_path" "$text" 'missing unattended protocol boundary'
done <<'EOF'
src/Testing/UnattendedTestRunner.cs	private static readonly ProtocolHost Host = new();
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private sealed class ProtocolHost
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private async Task RunRequestLoopAsync(NGame host)
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private void Activate(UnattendedTestRequest request)
src/Testing/UnattendedTestRunner.ProtocolHost.cs	private void Reset()
src/Testing/UnattendedTestRunner.Writer.cs	private sealed class Writer(
src/Testing/UnattendedTestRunner.Writer.cs	public RuntimeMemorySnapshot Write(
src/Testing/UnattendedTestRunner.Writer.cs	private static void WriteResult(UnattendedTestResult result)
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
    'private static void WriteResult(UnattendedTestResult result)' \
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
bug_report_uploader_path="$repository_root/src/Runtime/CombatBugReportUploader.cs"
solver_settings_panel_path="$repository_root/src/UI/SolverSettingsPanel.cs"
solver_settings_general_path="$repository_root/src/UI/SolverSettingsPanel.General.cs"
solver_settings_performance_path="$repository_root/src/UI/SolverSettingsPanel.Performance.cs"
solver_settings_bug_reports_path="$repository_root/src/UI/SolverSettingsPanel.BugReports.cs"
solver_settings_controls_path="$repository_root/src/UI/SolverSettingsPanel.Controls.cs"
while IFS=$'\t' read -r path text; do
    require_fixed "$path" "$text" 'missing bug-report ownership boundary'
done <<EOF
$bug_report_exporter_path	private static readonly BlockingCollection<Action> BackgroundOperations = new();
$bug_report_exporter_path	QueueCheckpointWrite(session, capture);
$bug_report_exporter_path	Task<ForensicArchiveBundle> forensicsTask = QueueBackground(
$bug_report_exporter_path	ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);
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

if ((${#violations[@]} > 0)); then
    printf '%s\n' "${violations[@]}" >&2
    printf 'Refactor boundary verification failed with %d violation(s).\n' "${#violations[@]}" >&2
    exit 1
fi

printf 'REFACTOR_BOUNDARIES_OK search_files=%d\n' "${#search_files[@]}"
