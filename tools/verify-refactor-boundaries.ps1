#requires -Version 7.0

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$searchRoot = Join-Path $repositoryRoot "src\Search"

$forbiddenSearchReferences = @(
    "SolverSearchPhase",
    "ShortProfile",
    "DeepProfile",
    "shortCheckpointMilliseconds",
    "SolveWithNarrowBeamRecovery",
    "BuildNarrowBeamRecoveryProfile",
    "RecoverDeferredTurnFrontier",
    "DeferredTurnFrontier",
    "SolverSettings.Current",
    "Entry.Logger",
    "SolverController",
    "SolverOverlay",
    "SolverText",
    "SolverRelicEffectText",
    "SolverUiModelNames",
    "SolverActionTextIdentity",
    "SolverLocaleRefresh",
    "SolvedRouteCache",
    "RunStatistics",
    "UnattendedTestRunner"
)

$violations = [System.Collections.Generic.List[string]]::new()
$poolLifetime = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Runtime/NodePoolSignalLifetimePatch.cs'))
foreach ($required in @('using ((Godot.Collections.Array)signals)', 'using var ownedArray', 'using (connection)', 'using (callable.Method)', 'using (signal.Name)')) {
    if (-not $poolLifetime.Contains($required)) { $violations.Add("Node pool wrapper ownership missing: $required") }
}
if ($poolLifetime.Contains('GC.Collect') -or $poolLifetime.Contains('QueueFree')) {
    $violations.Add('Node pool signal cleanup owns temporary wrappers, not nodes or process GC.')
}
$normalityMirror = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Engine/InCombat/Mirrors/Hooks/Card/ShouldPlayMirrors.cs'))
if (-not $normalityMirror.Contains('registry.Register<Normality>(HandleNormality)')) {
    $violations.Add('Normality must use the shared ShouldPlay mirror for manual and automatic cards.')
}
$playerTurnEndCallers = @(
    "src/Search/CombatBeamSolver.Expansion.cs",
    "src/Runtime/LiveEndTurnRiskEvaluator.cs",
    "src/Testing/UnattendedTestRunner.cs",
    "src/Testing/UnattendedTestRunner.Potions.cs"
)
foreach ($relativePath in $playerTurnEndCallers) {
    $callerPath = Join-Path $repositoryRoot $relativePath
    foreach ($reference in @(
        "CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(",
        "TurnStartRelicSupport.TriggerAfterSideTurnEnd(",
        "HookMirrors.AfterSideTurnEndLate(")) {
        foreach ($match in Select-String -LiteralPath $callerPath -SimpleMatch $reference) {
            $violations.Add("$($match.Path):$($match.LineNumber): player phase two must use PlayerTurnEndLifecycle")
        }
    }
}
$searchFiles = Get-ChildItem -LiteralPath $searchRoot -Filter *.cs -File -Recurse
$beamFiles = Get-ChildItem -LiteralPath $searchRoot -Filter "CombatBeamSolver*.cs" -File
$beamPaths = @($beamFiles.FullName)
$cyclePolicyPaths = @(
    (Join-Path $searchRoot "CombatBeamSolver.CyclePlanning.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.CycleRegionRetention.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.OrderedMutationRetention.cs")
)
$legacyLoopGuardPaths = @(
    (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs"),
    (Join-Path $searchRoot "CombatBeamSolver.ParallelExpansion.cs"),
    (Join-Path $searchRoot "SolverWeights.cs")
)
foreach ($file in $searchFiles) {
    foreach ($reference in $forbiddenSearchReferences) {
        foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch $reference) {
            $violations.Add("$($file.FullName):$($match.LineNumber): forbidden Search reference '$reference'")
        }
    }
}

$blockPotionInsertionPath = Join-Path $searchRoot "CombatBeamSolver.BlockPotionInsertion.cs"
foreach ($requiredBlockPotionRule in @(
    'HpLostByTurn',
    'SolverWeights.PotionMinimumHpSaved',
    'ReplayInsertedRoute(',
    'ProjectedDeathSaveUseCount',
    'expanded_nodes_added=0')) {
    if (-not (Select-String -LiteralPath $blockPotionInsertionPath -SimpleMatch $requiredBlockPotionRule -Quiet)) {
        $violations.Add("${blockPotionInsertionPath}: deterministic block-potion route rule is missing '$requiredBlockPotionRule'")
    }
}
$searchCoordinatorPath = Join-Path $searchRoot "CombatSearchCoordinator.cs"
if (-not (Select-String -LiteralPath $searchCoordinatorPath -SimpleMatch 'passResult.DeterministicBlockPotionInserted' -Quiet)) {
    $violations.Add("${searchCoordinatorPath}: deterministic block-potion result must settle before supplemental potion audits")
}

# Cycle planning must infer recurrence and payoff from generic simulated-state deltas. Keeping
# scenario names out of this policy file prevents a regression to card/power/relic/enemy allowlists.
$scenarioSpecificCycleModelPattern = '\b(?:Body[\s_.-]*Slam|Lunar[\s_.-]*Blast|Gold[\s_.-]*Axe|Slow[\s_.-]*Power|Hellraiser|Pillage|Bloodletting|Particle[\s_.-]*Wall|Pale[\s_.-]*Blue[\s_.-]*Dot|Flash[\s_.-]*Of[\s_.-]*Steel|Finesse|Speedster|Black[\s_.-]*Hole|Glow|Alignment|Spoils[\s_.-]*Of[\s_.-]*Battle)\b'
foreach ($cyclePolicyPath in $cyclePolicyPaths) {
    foreach ($match in Select-String -LiteralPath $cyclePolicyPath -Pattern $scenarioSpecificCycleModelPattern) {
        $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning contains a scenario-specific model name or ID")
    }
    foreach ($directModelLookupPattern in @(
        '\bModelDb\.(?:Card|Power|Relic|Monster)\b',
        '\bGetAmount<[A-Za-z_][A-Za-z0-9_]*(?:Power|Relic|Monster)>',
        '\btypeof\([A-Za-z_][A-Za-z0-9_]*(?:Card|Power|Relic|Monster)\)')) {
        foreach ($match in Select-String -LiteralPath $cyclePolicyPath -Pattern $directModelLookupPattern) {
            $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning performs a direct concrete-model lookup")
        }
    }
}

$cycleRegionRetentionPath = Join-Path $searchRoot "CombatBeamSolver.CycleRegionRetention.cs"
foreach ($cycleTransactionRule in @(
    'CycleRegionRetentionTransaction',
    'CloneCycleRegionLedger(',
    'ObservationBaseline',
    'FindBestCycleRegionProgressWitness(',
    'lanePriority: -1',
    'SelectCycleRegionAdmissionKind(',
    'normalAdmissionSucceeded',
    'HasActiveOrderedMutationCycleRegionAdmission(',
    'node.CycleExitRetentionRank != int.MaxValue')) {
    if (-not (Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch $cycleTransactionRule -Quiet)) {
        $violations.Add("${cycleRegionRetentionPath}: cycle-region final-survivor transaction invariant is missing '$cycleTransactionRule'")
    }
}
foreach ($retiredCycleOrderedCoupling in @(
    'CycleRegionOrderedProgressTail',
    'OrderCycleRegionOrderedMutationLane(',
    'TryStageCycleRegionOrderedProgressTailAdmission(')) {
    foreach ($match in Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch $retiredCycleOrderedCoupling) {
        $violations.Add("$($match.Path):$($match.LineNumber): retired cycle-region/ordered joint ledger returned '$retiredCycleOrderedCoupling'")
    }
}
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Retention.cs") -SimpleMatch 'FinalizeCycleRegionRetention(cycleRegionTransaction, finalized);' -Quiet)) {
    $violations.Add("${searchRoot}/CombatBeamSolver.Retention.cs: cycle-region provisional admissions are no longer reconciled after final arbitration")
}
$orderedRetentionPath = Join-Path $searchRoot "CombatBeamSolver.OrderedMutationRetention.cs"
foreach ($orderedTransactionRule in @(
    'MaximumOrderedMutationRunAdmissions = 2048',
    'HasFullyPendingAtomicOrderedMutationPair(',
    'ExpireOrderedMutationSchedulingLeaseForOrdinaryFallback(node);',
    'PendingOrderedMutationOrdinaryFallbackNodes',
    'ValidateOrderedMutationAdmissionLedger(',
    'typeof(OrderedMutationRetentionLease).IsValueType')) {
    if (-not (Select-String -LiteralPath $orderedRetentionPath -SimpleMatch $orderedTransactionRule -Quiet)) {
        $violations.Add("${orderedRetentionPath}: ordered-mutation atomic accounting invariant is missing '$orderedTransactionRule'")
    }
}
$orderedCoordinatorPaths = @{
    'BuildOrderedMutationContinuationAdmissionLease(candidate);' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"
    'Every independent retention channel must finish before the ordered coordinator.' = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
    'Any inherited lane left outside this prune' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"
    'HasOrdinaryAnchor' = Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"
}
foreach ($entry in $orderedCoordinatorPaths.GetEnumerator()) {
    if (-not (Select-String -LiteralPath $entry.Value -SimpleMatch $entry.Key -Quiet)) {
        $violations.Add("$($entry.Value): unified ordered-mutation coordinator invariant is missing '$($entry.Key)'")
    }
}
$solverDiagnosticsPath = Join-Path $repositoryRoot "src\Runtime\SolverDiagnostics.cs"
foreach ($orderedMetric in @(
    'ordered_admitted=',
    'ordered_lease_expired_budget=',
    'ordered_ordinary_fallback=',
    'cold_atomic_committed=',
    'cold_atomic_rejected=')) {
    if (-not (Select-String -LiteralPath $solverDiagnosticsPath -SimpleMatch $orderedMetric -Quiet)) {
        $violations.Add("${solverDiagnosticsPath}: ordered-mutation acceptance metric is missing '$orderedMetric'")
    }
}
$retentionPath = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
$orderedCoordinatorMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);' | Select-Object -First 1
$cycleRegionMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'cycleRegionTransaction = ApplyCycleRegionRetention(' | Select-Object -First 1
if ($null -eq $orderedCoordinatorMatch `
    -or $null -eq $cycleRegionMatch `
    -or $orderedCoordinatorMatch.LineNumber -ge $cycleRegionMatch.LineNumber) {
    $violations.Add("${retentionPath}: ordered admission must settle before CycleRegion")
}
if (Select-String -LiteralPath $retentionPath -SimpleMatch 'List<List<SearchNode>> openingChannels = pool' -Quiet) {
    $violations.Add("${retentionPath}: legacy additive opening-power channel returned")
}
$beamRetentionPolicyPath = Join-Path $searchRoot 'CombatBeamSolver.BeamRetentionPolicy.cs'
if (-not (Select-String -LiteralPath $beamRetentionPolicyPath -SimpleMatch 'AdmitPowerCommitmentRepresentatives(quotaPool, ranked, required, limit);' -Quiet)) {
    $violations.Add("${beamRetentionPolicyPath}: bounded power commitment replacement is missing")
}
foreach ($match in Select-String -LiteralPath $cycleRegionRetentionPath -SimpleMatch 'selectedSet.Add(node);') {
    $violations.Add("$($match.Path):$($match.LineNumber): CycleRegion rebuilt an O(pool) selected-set shadow")
}

# PR #28's fixed repeat count and named payoff exceptions are retired. These checks intentionally
# stay scoped to expansion and policy files so unrelated combat-semantic mirrors remain legal.
foreach ($legacyLoopGuardPath in $legacyLoopGuardPaths) {
    foreach ($retiredLoopGuard in @(
        'MaxRepeatableNoProgressPlays',
        'IsRepeatableNoProgressStep',
        'ShouldPruneRepeatableNoProgress',
        'RepeatableNoProgressCardId',
        'RepeatableNoProgressCount')) {
        foreach ($match in Select-String -LiteralPath $legacyLoopGuardPath -SimpleMatch $retiredLoopGuard) {
            $violations.Add("$($match.Path):$($match.LineNumber): retired fixed repeatable-no-progress guard '$retiredLoopGuard' returned")
        }
    }
    foreach ($match in Select-String -LiteralPath $legacyLoopGuardPath -Pattern '\b(?:Body[\s_.-]*Slam|Lunar[\s_.-]*Blast|Gold[\s_.-]*Axe|Slow[\s_.-]*Power)\b') {
        $violations.Add("$($match.Path):$($match.LineNumber): retired named loop-payoff exception returned")
    }
}

$semanticFiles = @($beamPaths) + @(
    (Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionDynamicVarExtensions.cs")
)
foreach ($file in $semanticFiles) {
    foreach ($match in Select-String -LiteralPath $file -Pattern 'catch\s*\(Exception') {
        $violations.Add("${file}:$($match.LineNumber): broad semantic catch is not allowed")
    }
}

$removedFallbacks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionDynamicVarExtensions.cs"
        Text = "return 0m;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Cards\OnPlay\CardOnPlayInferrer.cs"
        Text = "Inferred card mirror failed"
    },
    @{
        Path = $beamPaths
        Text = "跳过无法回放"
    }
)
foreach ($fallback in $removedFallbacks) {
    foreach ($match in Select-String -LiteralPath $fallback.Path -SimpleMatch $fallback.Text) {
        $violations.Add("$($fallback.Path):$($match.LineNumber): removed fallback '$($fallback.Text)' returned")
    }
}

$controllerPath = Join-Path $repositoryRoot "src\Runtime\SolverController.cs"
$removedControllerFields = @(
    "_searchCancellation",
    "_deploymentCancellation",
    "_generation",
    "_searching",
    "_deployAfterSearch",
    "_searchStamp",
    "_searchProgress",
    "_renderedProgress",
    "_lastProgressRenderAt",
    "_searchFrameCount",
    "_searchFramesOver33Ms",
    "_searchFramesOver50Ms",
    "_searchFramesOver100Ms",
    "_maxSearchFrameGapMs"
)
foreach ($field in $removedControllerFields) {
    foreach ($match in Select-String -LiteralPath $controllerPath -SimpleMatch $field) {
        $violations.Add("${controllerPath}:$($match.LineNumber): retired controller field '$field' returned")
    }
}

$onPlayFacade = Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs"
if (Select-String -LiteralPath $onPlayFacade -SimpleMatch "Harmony.GetPatchInfo" -Quiet) {
    $violations.Add("${onPlayFacade}: worker must not query Harmony")
}

$sessionPath = Join-Path $repositoryRoot "src\Runtime\SolverControllerSessions.cs"
foreach ($sessionType in @("SolverCombatSession", "SolverSearchSession", "SolverDeploymentSession")) {
    if (-not (Select-String -LiteralPath $sessionPath -SimpleMatch "class $sessionType" -Quiet)) {
        $violations.Add("${sessionPath}: missing controller session type '$sessionType'")
    }
}

$forkBoundaryChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src/Prediction/PredictionModHookSubscriberCapture.cs"
        Text = "PredictionModPatchAudit.CaptureCardOnPlay"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Search/SimulatedCombatState.cs"
        Text = "_modHookSubscribers = source._modHookSubscribers;"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Runtime/ContinuationStamp.cs"
        Text = "AdaptedCardOnPlayMirrors.CaptureLiveStamp()"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Runtime/ContinuationStamp.cs"
        Text = "adaptedOnPlay.Stamp"
    },
    @{
        Path = Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/Cards/OnPlay/CardOnPlayMirrors.cs"
        Text = "return replacement;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\ModelPredictionStateMirrors.cs"
        Text = "context.Register(value, typed)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\ModelPredictionStateMirrors.cs"
        Text = "boundary.AssertForkable()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "ModelPredictionStateMirrors.CaptureRootState(simulator,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "ModelPredictionStateMirrors.AppendPredicted(ref fingerprint,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_rootModifierSources = null;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\ContinuationStamp.cs"
        Text = "ModelPredictionStateMirrors.AppendLiveContinuation(text, state)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\ContinuationStamp.cs"
        Text = "ModelPredictionStateMirrors.AppendPredicted(ref adapterFingerprint,"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionForking.cs"
        Text = "interface IPredictionForkBoundary"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionStateStore.cs"
        Text = "boundary.AssertForkable()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "_activeActionChoices"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "_activeCardExecutionDeaths"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\CardPlayHookPredictionStates.cs"
        Text = "Cannot fork Pen Nib"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"
        Text = "Cannot fork Curl Up"
    }
)
foreach ($check in $forkBoundaryChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing Fork boundary '$($check.Text)'")
    }
}

$searchGcPolicyPath = Join-Path $repositoryRoot "src\Runtime\SearchGcPolicy.cs"
$searchGcRecoveryPath = Join-Path $repositoryRoot "src\Runtime\SearchGcPolicy.Recovery.cs"
foreach ($forbiddenRecoveryCall in @("GC.Collect(", "CollectGeneration2")) {
    if (Select-String -LiteralPath $searchGcRecoveryPath -SimpleMatch $forbiddenRecoveryCall -Quiet) {
        $violations.Add("${searchGcRecoveryPath}: NoGC recovery must not induce a collection or enter the reclaim chain '$forbiddenRecoveryCall'")
    }
}
foreach ($gcChainRule in @(
    "return WaitForReclaimChainAsync(_reclaimTask)",
    "CollectGeneration2ForAutomaticReclaimAsync(inSearchCheckpoint: true)",
    "_inSearchManualReclaimTask = manualCompletion.Task",
    "failure == null && (_regionExitRequired || _reclaimRequired)")) {
    if (-not (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch $gcChainRule -Quiet)) {
        $violations.Add("${searchGcPolicyPath}: missing serialized reclaim-chain rule '$gcChainRule'")
    }
}
if (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch "ReclaimAfterActiveCheckpointAsync" -Quiet) {
    $violations.Add("${searchGcPolicyPath}: recursive reclaim handoff returned")
}

# GC admission accounting and scratch-container ownership remain in their existing layers.
foreach ($check in @(
    @{ RelativePath = "src/Runtime/SearchGcPolicy.cs"; Text = "scope.CompleteLifecycle(CaptureLifecycle())" },
    @{ RelativePath = "src/Runtime/SolverController.cs"; Text = "SearchGcPolicy.EnterSearchScope(" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Models.cs"; Text = "ExpansionBatchPool = new(static snapshot => snapshot.ReleaseSimulator())" },
    @{ RelativePath = "src/Search/CombatBeamSolver.ParallelExpansion.cs"; Text = "new(_run.ExpansionBatchPool)" },
    @{ RelativePath = "src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs"; Text = "private sealed class FrozenStream(PredictionRngState state)" },
    @{ RelativePath = "src/Engine/InCombat/Simulation/CombatPredictionRngSet.cs"; Text = "new FrozenStream(_mutable.CaptureState())" },
    @{ RelativePath = "src/Testing/UnattendedTestRunner.Assertions.cs"; Text = "AssertLazyRngFork(scenario.CombatState.RunState.Rng)" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.ShuffleState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.ShuffleState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatCardGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatCardGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatPotionGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatPotionGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatCardSelectionState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatCardSelectionState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatEnergyCostsState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatEnergyCostsState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatTargetsState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatTargetsState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.CombatOrbGenerationState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.CombatOrbGenerationState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.MonsterAiState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.MonsterAiState" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "AppendRngState(ref key, simulator.Rng.NicheState);" },
    @{ RelativePath = "src/Runtime/ContinuationStamp.cs"; Text = "simulator.Rng.NicheState" },
    @{ RelativePath = "src/Engine/Common/PredictedCard.cs"; Text = "internal bool TryMarkPowerAfflictionEntryChecked()" },
    @{ RelativePath = "src/Engine/Common/PredictedCard.cs"; Text = "HasCheckedPowerAfflictionEntry = HasCheckedPowerAfflictionEntry," },
    @{ RelativePath = "src/Search/SimulatedCombatState.PowerLifecycle.cs"; Text = "private HashSet<CardModel>? _liveCardsAtSnapshot;" },
    @{ RelativePath = "src/Search/SimulatedCombatState.Fork.cs"; Text = "_liveCardsAtSnapshot = _liveCardsAtSnapshot," },
    @{ RelativePath = "src/Search/SimulatedCombatState.Fork.cs"; Text = "ReferenceEquals(view.Prefix, _rootRunHookListeners)" },
    @{ RelativePath = "src/Testing/UnattendedTestRunner.Assertions.cs"; Text = "AssertFrozenRootRunListeners(scenario.CombatState, scenario.Player);" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Models.cs"; Text = "SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "_run.SnapshotLiveCards.Rent()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Phases.cs"; Text = "SearchWaveMemoryPolicy.ParentWaveCapacity(" })) {
    $checkPath = Join-Path $repositoryRoot $check.RelativePath
    if (-not (Select-String -LiteralPath $checkPath -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${checkPath}: missing GC research ownership boundary '$($check.Text)'")
    }
}

$cardPlayPredictionStatePath = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\CardPlayHookPredictionStates.cs"
foreach ($stableVambraceState in @(
    "internal sealed class VambracePredictionState(Vambrace relic) : IPredictionStateForkable",
    "public CardModel? TriggeringCard { get; set; } = relic._triggeringCard;",
    "public bool BlockGainedThisCombat { get; set; } = relic._blockGainedThisCombat;")) {
    if (-not (Select-String -LiteralPath $cardPlayPredictionStatePath -SimpleMatch $stableVambraceState -Quiet)) {
        $violations.Add("${cardPlayPredictionStatePath}: missing stable Vambrace state '$stableVambraceState'")
    }
}

$rootSnapshotChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatRootSnapshot.cs"
        Text = "Combat root snapshot must be captured on the main thread."
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\SolverController.cs"
        Text = "CombatRootSnapshot.Capture(state)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\PlayerTurnSetupPatches.cs"
        Text = "CombatRootSnapshot.Capture(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatSearchCoordinator.cs"
        Text = "CombatRootSnapshot root"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\RootCombatHistorySnapshot.cs"
        Text = "history.CardPlaysStarted.ToArray()"
    }
)

$preCombatApiChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastApi.cs"
        Text = "public static class PreCombatForecastApi"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\CombatShowcaseApi.cs"
        Text = "public static class CombatShowcaseApi"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatShowcaseRuntime.cs"
        Text = "SolverController.AcceptShowcaseRoute"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatShowcaseCollector.cs"
        Text = "CombatShowcaseCollector.FlushPendingAsync"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatShowcaseModEligibility.cs"
        Text = "FindGameplayModificationNames"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatLiveStateSnapshot.cs"
        Text = "RunManager.Instance.ToSave(null)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatRunSerialization.cs"
        Text = 'point["can_modify"] = false'
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatRunSerialization.cs"
        Text = 'eventChoice["variables"] is JsonObject { Count: 0 }'
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "COMBATSOLVER_PRECOMBAT_WORKER"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "ExpectedLoadedMods = expectedMods"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "EnableNoGcRegionForTest = false"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Api\PreCombatForecastWorker.cs"
        Text = "PreCombatInterveningMapPoints = options.InterveningMapPoints"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "EnterMapCoordDebug"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "PreCombatPlayerHp:"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
        Text = "DirectRunSnapshot:ExactStateRestored"
    }
)
foreach ($check in $preCombatApiChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing pre-combat isolation boundary '$($check.Text)'")
    }
}
foreach ($apiFile in Get-ChildItem (Join-Path $repositoryRoot "src\Api") -Filter "*.cs" -File) {
    foreach ($forbiddenCall in @(
        "SolverController.RequestSearch",
        "CombatManager.Instance.SetUpCombat",
        "RunManager.Instance.EnterRoomDebug")) {
        foreach ($match in Select-String -LiteralPath $apiFile.FullName -SimpleMatch $forbiddenCall) {
            $violations.Add("$($apiFile.FullName):$($match.LineNumber): pre-combat API directly mutates live combat via '$forbiddenCall'")
        }
    }
}

$nativeChoiceRuntimePath = Join-Path $repositoryRoot "src\Runtime\NativeChoiceRuntime.cs"
$turnSetupPath = Join-Path $repositoryRoot "src\Runtime\PlayerTurnSetupPatches.cs"
foreach ($check in @(
    @{ Path = $nativeChoiceRuntimePath; Text = "internal static class NativeChoiceRuntime" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.Hand" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.SimpleGrid" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.CombatPile" },
    @{ Path = $nativeChoiceRuntimePath; Text = "NativeChoiceSurfaceKind.ChooseCard" },
    @{ Path = $turnSetupPath; Text = "TryGetPlannedTurnSetupChoices" },
    @{ Path = $turnSetupPath; Text = "source=continuation choices=" },
    @{ Path = $controllerPath; Text = "ResumeAfterTurnSetupAsync" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing native choice boundary '$($check.Text)'")
    }
}
foreach ($runtimePath in Get-ChildItem (Join-Path $repositoryRoot "src\Runtime") -Filter "*.cs" -File) {
    if ($runtimePath.FullName -eq $nativeChoiceRuntimePath) {
        continue
    }
    foreach ($match in Select-String -LiteralPath $runtimePath.FullName -SimpleMatch "CardSelectCmd.PushSelector") {
        $violations.Add("$($runtimePath.FullName):$($match.LineNumber): production runtime bypasses native choice UI")
    }
}
$cardTargetingPath = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardTargeting.cs"
foreach ($targetingRule in @(
    "Shiv => combat.GetAmount<FanOfKnivesPower>",
    "SovereignBlade => combat.GetAmount<SeekingEdgePower>")) {
    if (-not (Select-String -LiteralPath $cardTargetingPath -SimpleMatch $targetingRule -Quiet)) {
        $violations.Add("${cardTargetingPath}: missing simulated card targeting rule '$targetingRule'")
    }
}
foreach ($check in $rootSnapshotChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing root snapshot boundary '$($check.Text)'")
    }
}

$expectedBeamFiles = @(
    "CombatBeamSolver.cs",
    "CombatBeamSolver.AdmittedExpansion.cs",
    "CombatBeamSolver.EndTurnChoiceReplay.cs",
    "CombatBeamSolver.RoundTransition.cs",
    "CombatBeamSolver.CardChoiceContinuation.cs",
    "CombatBeamSolver.PotionChoiceContinuation.cs",
    "CombatBeamSolver.ExecutionChoiceContinuation.cs",
    "CombatBeamSolver.ExecutionChoiceContinuation.Testing.cs",
    "CombatBeamSolver.TurnExecutionContinuation.cs",
    "CombatBeamSolver.BeamRetentionPolicy.cs",
    "CombatBeamSolver.BlockPotionInsertion.cs",
    "CombatBeamSolver.CrossTurnPlanning.cs",
    "CombatBeamSolver.CyclePlanning.cs",
    "CombatBeamSolver.CycleRegionRetention.cs",
    "CombatBeamSolver.Expansion.cs",
    "CombatBeamSolver.FinalPlanOrdering.cs",
    "CombatBeamSolver.Models.cs",
    "CombatBeamSolver.NoveltySearch.cs",
    "CombatBeamSolver.Transpositions.cs",
    "CombatBeamSolver.OrderedMutationRetention.cs",
    "CombatBeamSolver.ParallelExpansion.cs",
    "CombatBeamSolver.PathDiagnostics.cs",
    "CombatBeamSolver.Phases.cs",
    "CombatBeamSolver.PrimaryChoiceReplay.cs",
    "CombatBeamSolver.Retention.cs",
    "CombatBeamSolver.RetentionJobs.cs",
    "CombatBeamSolver.StateEvaluation.cs",
    "CombatBeamSolver.StandPatJobs.cs",
    "CombatBeamSolver.Terminal.cs"
)
$pathDiagnosticsPath = Join-Path $searchRoot "CombatBeamSolver.PathDiagnostics.cs"
foreach ($required in @(
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'if (values.HasRetainedRoutingChoice)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.SearchPolicy.cs"); Text = 'seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-GENERATION-CONTEXT-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-GENERATION-SUFFIX-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-VARIANT-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-SOUL-RETAINED-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs"); Text = 'requiredRetentionStep: 18, proveRetentionAliases: true' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownSoulVariantPathTrace.cs"); Text = 'RunKnownSoulGenerationContext(combat, player, fullKnownSuffix: true, frozenVariants: variants);' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'watched.UnionWith(variants.Values.SelectMany(variant => variant.Prefixes)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'exact.GroupBy(item => new { item.PolicyLabel, item.ParentPolicyLabel })' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownRoutePathTrace.cs"); Text = 'CaptureKnownRouteRootStates(root, player, enemies)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownExoskeletonsPathTrace.cs"); Text = 'RunKnownExoskeletonsRouteReplay(combat, player, freeze: frozen);' },
    @{ Path = $pathDiagnosticsPath; Text = 'observer.WantsState(node.StateKey)' },
    @{ Path = $pathDiagnosticsPath; Text = 'observer.WantsRetentionPool(node.StateKey)' },
    @{ Path = $pathDiagnosticsPath; Text = 'SearchPathObservationStage.RetentionPoolInput' },
    @{ Path = $pathDiagnosticsPath; Text = 'Evaluation: new SearchPathEvaluationValues(' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'SearchPathObservationStage.RetentionPoolFinal' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'observedOptionLeaders.Add(optionLeader)' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Retention.cs"); Text = 'SearchPathObservationStage.PruneFinal' },
    @{ Path = (Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"); Text = 'private static bool ApplyRelicStatPower(' },
    @{ Path = (Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardPlayedMirrors.cs"); Text = 'if (context.Simulator.IsEnding)' })) {
    if (-not (Select-String -LiteralPath $required.Path -SimpleMatch $required.Text -Quiet)) {
        $violations.Add("$($required.Path): path observation or relic command boundary is missing '$($required.Text)'")
    }
}
foreach ($forbidden in @(
    @{ Path = $pathDiagnosticsPath; Text = 'node.Actions;' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'case Kunai' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'case Shuriken' },
    @{ Path = (Join-Path $searchRoot "SimulatedCombatState.Relics.cs"); Text = 'Apply<DexterityPower>' })) {
    foreach ($match in Select-String -LiteralPath $forbidden.Path -SimpleMatch $forbidden.Text) {
        $violations.Add("$($match.Path):$($match.LineNumber): observer cache mutation or deferred relic stat application returned '$($forbidden.Text)'")
    }
}
$actualBeamFiles = @($beamFiles.Name | Sort-Object)
if (($actualBeamFiles -join "|") -ne (($expectedBeamFiles | Sort-Object) -join "|")) {
    $violations.Add(
        "CombatBeamSolver partial file set differs: actual=$($actualBeamFiles -join ',') " +
        "expected=$(($expectedBeamFiles | Sort-Object) -join ',')")
}
$beamStructureChecks = @(
    @{ File = "GrowthPolicy.cs"; Text = "internal readonly record struct GrowthValues(" },
    @{ File = "SearchPolicySnapshot.cs"; Text = "public GrowthValues GrowthBudgets { get; init; }" },
    @{ File = "SearchPolicySnapshot.cs"; Text = "public bool UseNoveltyPortfolio { get; init; }" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public NoveltySearchRun? Novelty;" },
    @{ File = "CombatBeamSolver.NoveltySearch.cs"; Text = "private bool RunNoveltyOpen(" },
    @{ File = "CombatBeamSolver.NoveltySearch.cs"; Text = "CaptureNoveltyFacts(SearchNode node)" },
    @{ File = "CombatSearchCoordinator.NoveltyPortfolio.cs"; Text = "NoveltyPortfolioBudget.Remaining(profile," },
    @{ File = "CombatSearchCoordinator.NoveltyPortfolio.cs"; Text = "IsBetterPotionPolicyResult(root, policy, exploration, baseline)" },
    @{ File = "BfwsPackedNovelty.cs"; Text = "Dictionary<BfwsFact, int> _atoms" },
    @{ File = "BfwsPackedNovelty.cs"; Text = "_parentPartition == partition" },
    @{ File = "BfwsBoundedOpen.cs"; Text = "private readonly SortedSet<Entry> _entries" },
    @{ File = "NoveltyPortfolioBudget.cs"; Text = "profile.MaxExpandedNodes - (int)expandedNodes" },
    @{ File = "CombatBeamSolver.cs"; Text = "internal sealed partial class CombatBeamSolver(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private readonly SearchRunContext _run = new(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private BeamRetentionPolicy Retention =>" },
    @{ File = "CombatBeamSolver.cs"; Text = "private FinalPlanOrdering FinalOrdering =>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class BeamRetentionPolicy(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankBest(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public void Clear() => NodesByChoice.Clear();" },
    @{ File = "CombatBeamSolver.BlockPotionInsertion.cs"; Text = "private BlockPotionInsertion? TryInsertBlockPotion(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "routingNodes = new RoutingChoiceNodes(node);" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "ReturnRoutingChoiceScratch(scratch);" },
    @{ File = "CombatBeamSolver.Transpositions.cs"; Text = "private readonly record struct TranspositionLabel(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private sealed class SearchRunContext(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private readonly record struct SearchFeatures(" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "private sealed partial class ParallelExpansionExecutor : IDisposable" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "public ExpansionWorkerOutcome[] Evaluate(" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "public int MaximumQueuedParents => SearchWaveMemoryPolicy.MaximumQueuedParents(DegreeOfParallelism);" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "List<ExpansionLane> lanes = new(DegreeOfParallelism);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "private ExpansionWorkerOutcome[] EvaluateQueuedParents(" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "private sealed class AdmittedParent(" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "public object ForkGate { get; } = new();" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "wave.BackgroundCompleted.Wait();" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "while (committed < parents.Length && parents[committed]!.TailCompleted)" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_completedActions != Actions!.Count" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_completedPotions != Potions!.Count" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "private PreparedEndTurnEvaluation EvaluatePreparedEndTurn(" },
    @{ File = "CombatBeamSolver.TurnExecutionContinuation.cs"; Text = "private static SearchBoundaryReason ContinuePlayerStart(" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "private sealed class RoundReplayCheckpoint(" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "combat.EndActionChoices();" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "combat.BeginActionChoices(cursor);" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "internal int VerifyRoundReplayCheckpointForTesting(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public bool HasObservedPostDrawRoundChoice;" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public HashSet<string>? ObservedHandDrawShuffleChoiceSources;" },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "public void CaptureBeforeHandDraw(CombatBeamSolver owner, CombatPredictionSimulator simulator," },
    @{ File = "CombatBeamSolver.RoundTransition.cs"; Text = "checkpoint.HandDrawCount.HasValue ? PlayerStartStage.Draw : PlayerStartStage.AfterPlayer" },
    @{ File = "RootCombatCardGenerationPoolSnapshot.cs"; Text = "public bool TryGetEligibleCharacterCards(" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "var maximum = BeamRetentionPolicy.GetLongTermResourceMaximum(pool);" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "if (maximum.Count == pool.Count)" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "capture.ObservePendingChoice(this, pendingSourceId);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "endTurn.TransferEndTurnTo(Aggregate!, candidate);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "PublishCrossTurnStandPatBaselines(Node, _endTurnBaselines);" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "ready.TransferPotionTo(Aggregate!, candidate);" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "private sealed class PrimaryChoiceReplayFrontier : IDisposable" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "=> branches >= 2 && finals >= branches && attempts >= branches;" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "public bool CanDispatchContinuation => CompletedReplays == Actions.Length" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (!budget.TrySpendReplayAttempt())" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "frontier.AssertConsumed();" },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "CanReservePrimaryReplayPrefix(layer.Layer.Branches.Count," },
    @{ File = "CombatBeamSolver.EndTurnChoiceReplay.cs"; Text = "ResolveCollectedOccurrenceChoiceBranches(parent, layer.Occurrences)" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_endTurnFrontier?.Dispose();" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public ParallelExpansionExecutor? ActiveParallelExpansion;" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "_coordinator._run.ActiveParallelExpansion = null;" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "seen.Add(node.StateKey)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "_run.StandPatCache.Add(batch[index].StateKey, evaluations[index]);" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "ExpansionLane[] lanes = EnsureBackgroundLanes();" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "_coordinator.MergeExpansionWorker(outcome.Worker, outcome.AllocatedBytes);" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "wave.Completed.Wait();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "public void EvaluateRetentionIndices(" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "ExpansionLane[] lanes = EnsureBackgroundLanes();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "wave.Completed.Wait();" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "_coordinator._run.OffThreadAllocatedBytes += job.AllocatedBytes;" },
    @{ File = "CombatBeamSolver.RetentionJobs.cs"; Text = "wave.Error?.Throw();" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "_run.RoutingChoiceSummaryBuilds += summaryGroups.Length;" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "RequestOrderedMutationObservation(candidate);" },
    @{ File = "SearchWaveMemoryPolicy.cs"; Text = "return checked(degreeOfParallelism * 2);" },
    @{ File = "SearchWaveMemoryPolicy.cs"; Text = "current >= maximum - current ? maximum : current * 2" },
    @{ File = "CombatBeamSolver.Phases.cs"; Text = "SearchWaveMemoryPolicy.GrowCapacity(" },
    @{ File = "CombatBeamSolver.Retention.cs"; Text = "end.ReleaseSimulator();" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "private void CommitExpansionBatch(" },
    @{ File = "CombatBeamSolver.Phases.cs"; Text = "public SolverResult Solve()" },
    @{ File = "CombatBeamSolver.Expansion.cs"; Text = "private IEnumerable<SearchNode> Expand(SearchNode node)" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)" },
    @{ File = "CombatBeamSolver.FinalPlanOrdering.cs"; Text = "private sealed class FinalPlanOrdering(" },
    @{ File = "CombatBeamSolver.FinalPlanOrdering.cs"; Text = "public FinalPlanSelection Select(" },
    @{ File = "CombatBeamSolver.Terminal.cs"; Text = "private List<SearchNode> AnnotateTurnOutcomes(List<SearchNode> ended)" },
    @{ File = "CombatBeamSolver.StateEvaluation.cs"; Text = "private SimulationSnapshot Snapshot(" }
)
foreach ($check in $beamStructureChecks) {
    $path = Join-Path $searchRoot $check.File
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing CombatBeamSolver stage member '$($check.Text)'")
    }
}
$powerValuationRoot = Join-Path $searchRoot 'PowerCardValuation'
foreach ($check in @(
    @{ Path = 'PowerCardValuationContracts.cs'; Text = 'internal readonly record struct PowerCardValuationReward' },
    @{ Path = 'PowerCardValuationContracts.cs'; Text = 'internal readonly record struct PowerCardValuationPenalty' },
    @{ Path = 'IPowerCardValuationModel.cs'; Text = 'internal interface IPowerCardValuationModel' },
    @{ Path = 'PowerCardValuationRegistry.cs'; Text = 'internal sealed class PowerCardValuationRegistry' },
    @{ Path = 'PowerCardValuationModels.cs'; Text = 'internal static class PowerCardValuationModels' },
    @{ Path = 'PowerCardValuationRegistration.cs'; Text = 'internal sealed class DelegatingPowerCardValuationModel' },
    @{ Path = 'PowerRouteAdmission.cs'; Text = 'internal static class PowerRouteAdmission' },
    @{ Path = 'PowerCardValueFacts.cs'; Text = 'internal static class PowerCardValueFacts' },
    @{ Path = 'Projection\PowerCardProjectionSupport.cs'; Text = 'internal sealed partial class CombatBeamSolver' },
    @{ Path = 'Projection\PowerCardMechanismFacts.cs'; Text = 'internal sealed partial class CombatBeamSolver' },
    @{ Path = 'Projection\PowerTurnFrontier.cs'; Text = 'internal static class PowerTurnFrontier' },
    @{ Path = 'Projection\RetainedHandTransition.cs'; Text = 'internal static class RetainedHandTransition' },
    @{ Path = 'Projection\DrawDiscardTransition.cs'; Text = 'internal static class DrawDiscardTransition' },
    @{ Path = 'Projection\PoisonStackProjection.cs'; Text = 'internal static class PoisonStackProjection' },
    @{ Path = 'Projection\MasterPlannerProjection.cs'; Text = 'internal static class MasterPlannerProjection' },
    @{ Path = 'Projection\SilentPowerOpeningProjection.cs'; Text = 'private int SilentPowerOpeningProjectionPotential' },
    @{ Path = 'Projection\SilentShivPowerProjection.cs'; Text = 'private int AccuracyProjectionPotential' },
    @{ Path = 'Projection\SilentPoisonPowerProjection.cs'; Text = 'private int AccelerantProjectionPotential' },
    @{ Path = 'Projection\SilentDamageDefensePowerProjection.cs'; Text = 'private int SerpentFormProjectionPotential' },
    @{ Path = 'Commitments\PowerCommitment.cs'; Text = 'internal sealed record PowerCommitment' },
    @{ Path = 'Commitments\PowerCommitmentLifecycle.cs'; Text = 'internal static class PowerCommitmentLifecycle' },
    @{ Path = 'Commitments\PowerCommitmentPolicy.cs'; Text = 'private void AttachPowerCommitment' },
    @{ Path = 'Commitments\PowerCommitmentEvidence.cs'; Text = 'private int PowerCommitmentRealizedEvidence' },
    @{ Path = 'Commitments\PowerCardPlayOccurrence.cs'; Text = 'internal readonly record struct PowerCardPlayOccurrence' },
    @{ Path = 'Commitments\PowerCommitmentRetention.cs'; Text = 'internal static class PowerCommitmentRetention' },
    @{ Path = 'Commitments\PowerCommitmentSeatPolicy.cs'; Text = 'internal static class PowerCommitmentSeatPolicy' },
    @{ Path = 'Commitments\PowerActivationInvestmentPolicy.cs'; Text = 'internal static class PowerActivationInvestmentPolicy' },
    @{ Path = 'Commitments\PowerCardMechanismDispatch.cs'; Text = 'private bool PowerHasTriggerEvidence' },
    @{ Path = 'Cards\Ironclad\IroncladPowerCardValuationModels.cs'; Text = 'internal static class IroncladPowerCardValuationModels' },
    @{ Path = 'Cards\Ironclad\IroncladPowerRoutePolicy.cs'; Text = 'internal static class IroncladPowerRoutePolicy' },
    @{ Path = 'Cards\Ironclad\IroncladPowerTriggerEvidence.cs'; Text = 'private bool IroncladPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Ironclad\IroncladPowerOpeningProjection.cs'; Text = 'private int IroncladPowerOpeningProjectionPotential' },
    @{ Path = 'Cards\Ironclad\IroncladStrengthPowerCardValuationModels.cs'; Text = 'internal static class IroncladStrengthPowerCardValuationModels' },
    @{ Path = 'Cards\Silent\SilentPowerCardValuationModels.cs'; Text = 'internal static class SilentPowerCardValuationModels' },
    @{ Path = 'Cards\Silent\SilentPowerCardValuationModel.cs'; Text = 'internal abstract class SilentPowerCardValuationModel' },
    @{ Path = 'Cards\Silent\SilentDefensePowerCardValuationModels.cs'; Text = 'internal sealed class WraithFormPowerCardValuationModel' },
    @{ Path = 'Cards\Silent\SilentPoisonPowerCardValuationModels.cs'; Text = 'internal sealed class NoxiousFumesPowerCardValuationModel' },
    @{ Path = 'Cards\Silent\SilentShivPowerCardValuationModels.cs'; Text = 'internal sealed class FanOfKnivesPowerCardValuationModel' },
    @{ Path = 'Cards\Silent\SilentCardFlowPowerCardValuationModels.cs'; Text = 'internal sealed class MasterPlannerPowerCardValuationModel' },
    @{ Path = 'Cards\Silent\SilentCardFlowFacts.cs'; Text = 'internal static class SilentCardFlowFacts' },
    @{ Path = 'Cards\Silent\SilentDiscardWindowFacts.cs'; Text = 'internal static class SilentDiscardWindowFacts' },
    @{ Path = 'Cards\Silent\SilentPowerRoutePolicy.cs'; Text = 'internal static class SilentPowerRoutePolicy' },
    @{ Path = 'Cards\Silent\SilentPowerTriggerEvidence.cs'; Text = 'private bool SilentPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Silent\SilentPowerCommitmentEvidence.cs'; Text = 'private int SilentPowerProgressEvidence' },
    @{ Path = 'Cards\Silent\SilentWraithOpeningWindow.cs'; Text = 'internal static class SilentWraithOpeningWindow' },
    @{ Path = 'Cards\Silent\SilentDamagePowerCardValuationModels.cs'; Text = 'internal sealed class TrackingPowerCardValuationModel' },
    @{ Path = 'Cards\Defect\DefectPowerCardValuationModels.cs'; Text = 'internal static class DefectPowerCardValuationModels' },
    @{ Path = 'Cards\Defect\DefectPowerRoutePolicy.cs'; Text = 'internal static class DefectPowerRoutePolicy' },
    @{ Path = 'Cards\Defect\DefectPowerTriggerEvidence.cs'; Text = 'private bool DefectPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Defect\DefectPowerOpeningProjection.cs'; Text = 'private int DefectPowerOpeningProjectionPotential' },
    @{ Path = 'Cards\Defect\DefectOrbPowerCardValuationModels.cs'; Text = 'internal static class DefectOrbPowerCardValuationModels' },
    @{ Path = 'Cards\Regent\RegentPowerCardValuationModels.cs'; Text = 'internal static class RegentPowerCardValuationModels' },
    @{ Path = 'Cards\Regent\RegentPowerRoutePolicy.cs'; Text = 'internal static class RegentPowerRoutePolicy' },
    @{ Path = 'Cards\Regent\RegentPowerTriggerEvidence.cs'; Text = 'private bool RegentPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Regent\RegentPowerOpeningProjection.cs'; Text = 'private int RegentPowerOpeningProjectionPotential' },
    @{ Path = 'Cards\Regent\RegentStarPowerCardValuationModels.cs'; Text = 'internal static class RegentStarPowerCardValuationModels' },
    @{ Path = 'Cards\Necrobinder\NecrobinderPowerCardValuationModels.cs'; Text = 'internal static class NecrobinderPowerCardValuationModels' },
    @{ Path = 'Cards\Necrobinder\NecrobinderPowerRoutePolicy.cs'; Text = 'internal static class NecrobinderPowerRoutePolicy' },
    @{ Path = 'Cards\Necrobinder\NecrobinderPowerTriggerEvidence.cs'; Text = 'private bool NecrobinderPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Necrobinder\NecrobinderPowerOpeningProjection.cs'; Text = 'private int NecrobinderPowerOpeningProjectionPotential' },
    @{ Path = 'Cards\Necrobinder\NecrobinderDoomPowerCardValuationModels.cs'; Text = 'internal static class NecrobinderDoomPowerCardValuationModels' },
    @{ Path = 'Cards\Colorless\ColorlessPowerCardValuationModels.cs'; Text = 'internal static class ColorlessPowerCardValuationModels' },
    @{ Path = 'Cards\Colorless\ColorlessPowerRoutePolicy.cs'; Text = 'internal static class ColorlessPowerRoutePolicy' },
    @{ Path = 'Cards\Colorless\ColorlessPowerTriggerEvidence.cs'; Text = 'private bool ColorlessPowerHasTriggerEvidence' },
    @{ Path = 'Cards\Colorless\ColorlessPowerOpeningProjection.cs'; Text = 'private int ColorlessPowerOpeningProjectionPotential' },
    @{ Path = 'Cards\Colorless\ColorlessGrowthPowerCardValuationModels.cs'; Text = 'internal static class ColorlessGrowthPowerCardValuationModels' }
)) {
    $path = Join-Path $powerValuationRoot $check.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        -not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing power-card valuation boundary '$($check.Text)'")
    }
}
$powerPortfolioGatePath = Join-Path $searchRoot 'PowerCommitmentPortfolioGate.cs'
if (-not (Select-String -LiteralPath $powerPortfolioGatePath -SimpleMatch 'internal static class PowerCommitmentPortfolioGate' -Quiet)) {
    $violations.Add("${powerPortfolioGatePath}: missing power commitment portfolio gate")
}
$powerRoutePortfolioPath = Join-Path $searchRoot 'CombatSearchCoordinator.PowerRoutes.cs'
foreach ($powerRouteRule in @(
    'private static SolverResult RunOpeningPowerRoutePortfolio(',
    'fixedPrefixActions: prefix',
    'PowerRoutePortfolioMemberReport')) {
    if (-not (Select-String -LiteralPath $powerRoutePortfolioPath -SimpleMatch $powerRouteRule -Quiet)) {
        $violations.Add("${powerRoutePortfolioPath}: missing power route portfolio boundary '$powerRouteRule'")
    }
}
$finalOrderingPath = Join-Path $searchRoot 'CombatBeamSolver.FinalPlanOrdering.cs'
if (Select-String -LiteralPath $finalOrderingPath -SimpleMatch 'PowerCardValuation' -Quiet) {
    $violations.Add("${finalOrderingPath}: power-card valuation must not enter final plan ordering")
}
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs") -SimpleMatch "CreateWholeActionChoiceBudget" -Quiet)) {
    $violations.Add("CombatBeamSolver.Expansion.cs: repeated card choices are missing their whole-action branch quota")
}
$beamEntryPath = Join-Path $searchRoot "CombatBeamSolver.cs"
if (Select-String -LiteralPath $beamEntryPath -SimpleMatch "public SolverResult Solve()" -Quiet) {
    $violations.Add("${beamEntryPath}: Solve returned to the entry/field declaration file")
}
$beamRetentionFacadePath = Join-Path $searchRoot "CombatBeamSolver.Retention.cs"
if (Select-String -LiteralPath $beamRetentionFacadePath -SimpleMatch "private List<SearchNode> RankBest(" -Quiet) {
    $violations.Add("${beamRetentionFacadePath}: RankBest returned outside BeamRetentionPolicy")
}
$beamPhasesPath = Join-Path $searchRoot "CombatBeamSolver.Phases.cs"
if (-not (Select-String -LiteralPath $beamPhasesPath -SimpleMatch "TightenPrimarySearchIncumbentAtTurnLayer(" -Quiet)) {
    $violations.Add("${beamPhasesPath}: turn-layer incumbent is no longer tightened before coordinator pruning")
}
foreach ($match in Select-String -LiteralPath $beamPhasesPath -SimpleMatch "FinalizePrunedSelection(") {
    $violations.Add("$($match.Path):$($match.LineNumber): turn-layer incumbent pruning performs a second post-commit finalization")
}
foreach ($directPruneFinalizer in @(
    "ApplyPrimaryIncumbentBound(",
    "FinalizePrunedCycleExitProbeTickets(")) {
    foreach ($match in Select-String -LiteralPath $beamPhasesPath -SimpleMatch $directPruneFinalizer) {
        $violations.Add("$($match.Path):$($match.LineNumber): turn-layer pruning bypasses observation-debt finalization '$directPruneFinalizer'")
    }
}
foreach ($finalOrderingImplementation in @(
    "POLICY_BASELINE kind=potion_free",
    "PotionUsePolicy.IsEligible(",
    "PotionUsePolicy.MeetsAmbergrisRestriction(")) {
    if (Select-String -LiteralPath $beamPhasesPath -SimpleMatch $finalOrderingImplementation -Quiet) {
        $violations.Add("${beamPhasesPath}: final ordering implementation '$finalOrderingImplementation' returned outside FinalPlanOrdering")
    }
}
foreach ($retiredRunField in @(
    "private readonly SearchPerformanceMetrics _performance",
    "private int _expanded",
    "private readonly SearchWorkPacer _workPacer",
    "private readonly Dictionary<StateFingerprint, TranspositionFrontier> _transpositions")) {
    if (Select-String -LiteralPath $beamEntryPath -SimpleMatch $retiredRunField -Quiet) {
        $violations.Add("${beamEntryPath}: retired run-local field '$retiredRunField' returned")
    }
}
foreach ($removedWorkerRoot in @(
    "new SimulatedCombatState(",
    "IntentForecaster.Build(state",
    "_player.PotionSlots",
    "_player.Relics",
    "_player.Creature.MaxHp")) {
    foreach ($match in Select-String -LiteralPath $beamPaths -SimpleMatch $removedWorkerRoot) {
        $violations.Add("$($match.Path):$($match.LineNumber): worker root fallback '$removedWorkerRoot' returned")
    }
}

$rootModelBoundaryChecks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "Live combat state can only be captured on the main thread."
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "PredictionUtils.CreateRelic(relic, player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "RunRngSet.FromSave(_runRngSnapshot)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\RelicPredictionStateSupport.cs"
        Text = "CaptureRootState("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PowerPredictionStateSupport.cs"
        Text = "HardenedShellPredictionState(original)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "PowerPredictionStateSupport.CaptureRootState(simulator, mutable, power)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.CombatRootSnapshot.cs"
        Text = "workerLiveConstructorRejected"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.cs"
        Text = "ICombatPredictionRootMaterializable materializable"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.cs"
        Text = "public CombatTerminalStamp? TerminalStamp { get; private set; }"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatPlan.cs"
        Text = "public CombatTerminalStamp? TerminalStamp { get; } = terminalStamp;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\CombatBeamSolver.Terminal.cs"
        Text = "combatEndedTurn = node.Snapshot.CombatEndedTurn;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = ".Select(PredictionUtils.CloneModelForSimulation)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardGeneratedForCombatMirrors.cs"
        Text = "GetAeonglassWitherUpgradeCount(monster.Creature)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = ".SelectMany(combat.RelicsOf)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "foreach (BadgeModel badge in inner.BadgeModels)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "MultiplayerScalingRunStateField.SetValue(detachedMultiplayerScaling, null)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Block\ModifyBlockMultiplicativeMirrors.cs"
        Text = "registry.Register<MultiplayerScalingModel>(HandleMultiplayerScaling)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PredictionModHookSubscriberCapture.cs"
        Text = "ModHelper.IterateAllRunStateSubscribers(runState)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionUtils.cs"
        Text = "PredictionModModelSupport.CloneCardAttachedModels(source, clone)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardPile.cs"
        Text = "ContinueDrawExecution(player, drawCount, fromHandDraw, GetMaxHandSize(player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.CardPile.cs"
        Text = "limits.GetMaxHandSize(player)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = ".Take(standardCombatListenerCount)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "UpdatePowerListenerOrder("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "fork._powerListenerOrder ="
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\Common\PredictionModModelSupport.cs"
        Text = "ConditionalWeakTable<CardModel, object> BaseLibModifierCards"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.PowerRelics.cs"
        Text = "(_powerCardSources ??= []).Add(card)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "and not OrbModel"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\SimOrbQueue.cs"
        Text = "SetMutationObserver("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Potions\OnUse\EntropicBrewMirrors.cs"
        Text = "limits.GetPotionSlotCount(target)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "combat.DoomKill(simulator, doomed)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "BranchMonsterStaticSnapshot.Capture(monster)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "state.Static.AttacksByMove"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_encounterSlots = inner.Encounter?.Slots.ToArray()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.MonsterAi.cs"
        Text = "Root monster AI state was not captured"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "Root intent state was not captured"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterMoveEffects.StaticValues.cs"
        Text = "CaptureStaticIntValues(MonsterModel monster)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.MonsterAi.cs"
        Text = "GetMonsterStaticInt(Creature creature, string name)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionState.cs"
        Text = "boundary.AssertCanCaptureCreature(creature)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionState.cs"
        Text = "boundary.AssertCanCapturePlayer(player)"
    }
)
foreach ($check in $rootModelBoundaryChecks) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing root model boundary '$($check.Text)'")
    }
}

$removedModelFallbacks = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "inner.ContainsCard(card)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "player.PlayerCombatState?.TurnNumber"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.RelicTurnStart.cs"
        Text = "RunState.CardMultiplayerConstraint"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Relics.cs"
        Text = "player.RunState.CardMultiplayerConstraint"
    }
)
foreach ($fallback in $removedModelFallbacks) {
    foreach ($match in Select-String -LiteralPath $fallback.Path -SimpleMatch $fallback.Text) {
        $violations.Add("$($fallback.Path):$($match.LineNumber): removed model fallback '$($fallback.Text)' returned")
    }
}

$removedWorkerReads = @(
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.Fork.cs"
        Text = "new(InnerState)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Card\AfterCardGeneratedForCombatMirrors.cs"
        Text = "monster.WitherUpgradeCount"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Runtime\CombatRootSnapshot.cs"
        Text = ".MaterializeRoot("
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "_multiplayerScalingModel = inner.MultiplayerScalingModel"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.PowerRelics.cs"
        Text = "private CardModel? _powerCardSource;"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\PotionOnUseSupport.cs"
        Text = "playerTarget.MaxHp"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Simulation\CombatPredictionSimulator.Damage.cs"
        Text = "creature.MaxHp <= 0"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Hooks\Death\DeathPreventerMirrors.cs"
        Text = "context.Creature.MaxHp"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\CardOnPlaySupport.Batch042.cs"
        Text = "creature.Powers"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\TurnStartRelicSupport.cs"
        Text = "player.Relics"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Engine\InCombat\Mirrors\Potions\OnUse\EntropicBrewMirrors.cs"
        Text = "target.PotionSlots.Count"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "return branch.GetNextState(owner, rng)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "return state.GetWeight()"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\BranchMonsterAi.cs"
        Text = "combat.Encounter?.GetNextSlot(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "combat.Encounter?.GetNextSlot(combat)"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterSpawnSupport.cs"
        Text = "combat.Encounter?.Slots"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs"
        Text = "IReadOnlyList<string> slots = Encounter?.Slots"
    },
    @{
        Path = Join-Path $repositoryRoot "src\Prediction\MonsterMoveEffects.cs"
        Text = "MonsterValueReader.ReadInt(monster"
    }
)
foreach ($removedWorkerRead in $removedWorkerReads) {
    foreach ($match in Select-String -LiteralPath $removedWorkerRead.Path -SimpleMatch $removedWorkerRead.Text) {
        $violations.Add("$($removedWorkerRead.Path):$($match.LineNumber): worker live read '$($removedWorkerRead.Text)' returned")
    }
}

$unattendedEntryPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.cs"
foreach ($check in @(
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'source "$script_dir/headless-runtime.sh"' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'hr_acquire "$process_pid" "$process_identity_start_time"' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'if ((option_value[stop-instance] == 1)); then' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'add_option cleanup-instance-on-exit 0 switch none' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'add_option checkpoint-selector "start" string raw_string' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = '$repo_root/.local/headless-instances/$headless_instance' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'hr_remove_instance' },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = ". (Join-Path `$PSScriptRoot 'headless-runtime.ps1')" },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = '[string]$CheckpointSelector = "start"' },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = 'if ($StopInstance) {' },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = '[switch]$CleanupInstanceOnExit' },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = 'Remove-HeadlessRuntimeInstance $runtimeContext' },
    @{ Path = 'tools/run-headless-matrix.sh'; Text = '--stop-instance' },
    @{ Path = 'tools/run-headless-matrix.ps1'; Text = '"-StopInstance"' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_prepare_snapshot() {' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_bind() {' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_remove_instance() {' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessGameSnapshot(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'Join-Path $repository ".local\headless-instances\$Instance"' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Remove-HeadlessRuntimeInstance(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Enter-HeadlessHostLease(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessHostGame(' })) {
    $path = Join-Path $repositoryRoot $check.Path
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing headless infrastructure ownership boundary '$($check.Text)'")
    }
}
foreach ($legacyInstanceRoot in @(
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'CombatSolver\headless-instances' },
    @{ Path = 'tools/run-unattended-test.sh'; Text = 'CombatSolver/headless-instances' },
    @{ Path = 'tools/run-headless-matrix.sh'; Text = 'CombatSolver/headless-instances' })) {
    $path = Join-Path $repositoryRoot $legacyInstanceRoot.Path
    if (Select-String -LiteralPath $path -SimpleMatch $legacyInstanceRoot.Text -Quiet) {
        $violations.Add("${path}: user-local headless instance root returned '$($legacyInstanceRoot.Text)'")
    }
}
$checkpointArchivePath = Join-Path $repositoryRoot 'src\Replay\CheckpointArchive.cs'
if (-not (Select-String -LiteralPath $checkpointArchivePath -SimpleMatch 'public const string DefaultFixtureSelector = "start";' -Quiet)) {
    $violations.Add("${checkpointArchivePath}: checkpoint fixture default must remain combat start")
}
foreach ($matrix in @('tools/run-headless-matrix.sh', 'tools/run-headless-matrix.ps1')) {
    $path = Join-Path $repositoryRoot $matrix
    if (Select-String -LiteralPath $path -SimpleMatch 'MATRIX-CLEANUP' -Quiet) {
        $violations.Add("${path}: matrix cleanup must not dispatch a new game request")
    }
}
foreach ($helper in @('tools/headless-runtime.sh', 'tools/headless-runtime.ps1')) {
    $path = Join-Path $repositoryRoot $helper
    foreach ($forbidden in @('combat_solver_test_request.json', 'SolverSettings')) {
        if (Select-String -LiteralPath $path -SimpleMatch $forbidden -Quiet) {
            $violations.Add("${path}: protocol/game settings leaked into headless resource owner '$forbidden'")
        }
    }
}
$unattendedProtocolHostPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ProtocolHost.cs"
$unattendedWriterPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Writer.cs"
$unattendedScenarioBuilderPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.ScenarioBuilder.cs"
$unattendedAssertionsPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Assertions.cs"
$unattendedExecutorPath = Join-Path $repositoryRoot "src\Testing\UnattendedTestRunner.Executor.cs"
foreach ($check in @(
    @{ Path = $unattendedEntryPath; Text = "private static readonly ProtocolHost Host = new();" },
    @{ Path = $unattendedProtocolHostPath; Text = "private sealed partial class ProtocolHost" },
    @{ Path = $unattendedProtocolHostPath; Text = "private async Task RunRequestLoopAsync(NGame host)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Activate(UnattendedTestRequest request)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Reset()" },
    @{ Path = $unattendedWriterPath; Text = "private sealed partial class Writer(" },
    @{ Path = $unattendedWriterPath; Text = "public RuntimeMemorySnapshot Write(" },
    @{ Path = $unattendedWriterPath; Text = "private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "private sealed partial class ScenarioBuilder(" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/GeneratedCombatScenario.cs"); Text = "internal static ResolvedGeneratedCombatScenario Resolve(" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.GeneratedScenario.cs"); Text = "private void PrepareGeneratedScenario()" },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.GeneratedScenario.cs"); Text = "private void CaptureGeneratedOpening(" },
    @{ Path = $unattendedWriterPath; Text = "public void WriteGeneratedArtifact(" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "public async Task<ScenarioContext> BuildAsync()" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "public CombatState? CombatState { get; private set; }" },
    @{ Path = $unattendedAssertionsPath; Text = "private sealed class Assertions(" },
    @{ Path = $unattendedAssertionsPath; Text = "public async Task RunBeforeExecutionAsync(ScenarioContext scenario)" },
    @{ Path = $unattendedAssertionsPath; Text = "public void AssertAfterExecution(ScenarioContext scenario, ExecutionOutcome outcome)" },
    @{ Path = $unattendedExecutorPath; Text = "private sealed class Executor(" },
    @{ Path = $unattendedExecutorPath; Text = "public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)" },
    @{ Path = $unattendedExecutorPath; Text = "private FastModeType? ApplySettingsOverrides()" },
    @{ Path = $unattendedExecutorPath; Text = "public void RestoreSettings()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing unattended protocol boundary '$($check.Text)'")
    }
}
foreach ($retiredProtocolHostMember in @(
    "private static bool _requestLoopStarted",
    "private static async Task RunRequestLoopAsync",
    "private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)",
    "private static RuntimeMemorySnapshot CaptureRuntimeMemory()")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $retiredProtocolHostMember -Quiet) {
        $violations.Add("${unattendedEntryPath}: protocol host member '$retiredProtocolHostMember' returned to runner entry")
    }
}
if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch "StartNewSingleplayerRun(" -Quiet) {
    $violations.Add("${unattendedEntryPath}: scenario construction returned outside ScenarioBuilder")
}
foreach ($assertionImplementation in @(
    "VerifyPredictionFailureBoundaries",
    "ExpectedFinishedTurn is")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $assertionImplementation -Quiet) {
        $violations.Add("${unattendedEntryPath}: unattended assertion '$assertionImplementation' returned outside Assertions")
    }
}
foreach ($executorImplementation in @(
    "SolverController.SetFullAuto(",
    "StopAfterExpectedReuse",
    "orb_differential_",
    "potion_differential_")) {
    if (Select-String -LiteralPath $unattendedEntryPath -SimpleMatch $executorImplementation -Quiet) {
        $violations.Add("${unattendedEntryPath}: unattended executor implementation '$executorImplementation' returned outside Executor")
    }
}

$overlaySnapshotPath = Join-Path $repositoryRoot "src\UI\SolverOverlaySnapshot.cs"
$overlayRendererPaths = @(
    (Join-Path $repositoryRoot "src\UI\SolverOverlay.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverRouteRow.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverActionPill.cs"),
    (Join-Path $repositoryRoot "src\UI\SolverActionBar.cs")
)
foreach ($check in @(
    @{ Path = $overlaySnapshotPath; Text = "internal sealed record SolverOverlaySnapshot(" },
    @{ Path = $overlaySnapshotPath; Text = "public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverOverlay.cs"; Text = "public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverRouteRow.cs"; Text = "public void Populate(SolverOverlayTurnSnapshot turn)" },
    @{ Path = Join-Path $repositoryRoot "src\UI\SolverActionPill.cs"; Text = "public static Control Create(SolverOverlayActionSnapshot action)" },
    @{ Path = Join-Path $repositoryRoot "src\Runtime\SolverController.cs"; Text = "SolverOverlaySnapshot.CaptureWithReviewedWorldlines(" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing overlay snapshot boundary '$($check.Text)'")
    }
}
foreach ($rendererPath in $overlayRendererPaths) {
    foreach ($mutableSearchType in @("SolverResult", "PlanAction", "PlanCardChoice", "ModelDb")) {
        foreach ($match in Select-String -LiteralPath $rendererPath -SimpleMatch $mutableSearchType) {
            $violations.Add("${rendererPath}:$($match.LineNumber): mutable search type '$mutableSearchType' returned to renderer")
        }
    }
}

$bugReportExporterPath = Join-Path $repositoryRoot "src\Runtime\CombatBugReportExporter.cs"
$diagnosticJournalPath = Join-Path $repositoryRoot "src\Runtime\CombatDiagnosticJournal.cs"
$bugReportUploaderPath = Join-Path $repositoryRoot "src\Runtime\CombatBugReportUploader.cs"
$solverSettingsPanelPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.cs"
$solverSettingsGeneralPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.General.cs"
$solverSettingsPerformancePath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Performance.cs"
$solverSettingsBugReportsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.BugReports.cs"
$solverSettingsControlsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Controls.cs"
foreach ($check in @(
    @{ Path = $diagnosticJournalPath; Text = "AppendOnlyEventLog<CombatLogEntry>" },
    @{ Path = $diagnosticJournalPath; Text = "_session?.Log.CaptureAsync()" },
    @{ Path = $bugReportExporterPath; Text = "Entry.Logger.Journal.CaptureAsync()" },
    @{ Path = $bugReportExporterPath; Text = "WriteDiagnosticLogs(archive, diagnosticLogs)" },
    @{ Path = $bugReportExporterPath; Text = "private static readonly BlockingCollection<Action> BackgroundOperations = new();" },
    @{ Path = $bugReportExporterPath; Text = "QueueCheckpointWrite(session, capture);" },
    @{ Path = $bugReportExporterPath; Text = "Task<ForensicArchiveBundle> forensicsTask = QueueBackground(" },
    @{ Path = $bugReportExporterPath; Text = "ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);" },
    @{ Path = $bugReportExporterPath; Text = "CombatBugReportMetadata.CaptureCombat" },
    @{ Path = $bugReportUploaderPath; Text = "ReadMetadata(zipPath, submissionId, description)" },
    @{ Path = $bugReportUploaderPath; Text = "AllowAutoRedirect = false" },
    @{ Path = $bugReportUploaderPath; Text = "IProgress<CombatBugReportUploadProgress>" },
    @{ Path = $bugReportUploaderPath; Text = "HttpCompletionOption.ResponseHeadersRead" },
    @{ Path = $bugReportUploaderPath; Text = "CancellationToken requestCancellationToken" },
    @{ Path = $bugReportUploaderPath; Text = "ReadServerReceipt(body)" },
    @{ Path = $bugReportUploaderPath; Text = "UseProxy = false" },
    @{ Path = $solverSettingsBugReportsPath; Text = "private ProgressBar _uploadProgress = null!;" },
    @{ Path = $solverSettingsBugReportsPath; Text = "private volatile bool _uploadInProgress;" },
    @{ Path = $solverSettingsBugReportsPath; Text = "Interlocked.Exchange(ref _uploadCompletion, completion)" },
    @{ Path = $solverSettingsBugReportsPath; Text = "TryApplyUploadCompletion()" },
    @{ Path = $solverSettingsBugReportsPath; Text = "等待服务器确认" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing bug-report ownership boundary '$($check.Text)'")
    }
}
if (Select-String -LiteralPath $bugReportUploaderPath -SimpleMatch "using Godot" -Quiet) {
    $violations.Add("${bugReportUploaderPath}: uploader must not own Godot UI state")
}
foreach ($legacyLogRead in @("AddFileTail(", "CaptureLogStarts(", '"*.log"')) {
    if (Select-String -LiteralPath $bugReportExporterPath -SimpleMatch $legacyLogRead -Quiet) {
        $violations.Add("${bugReportExporterPath}: global log collection must stay out of report exports")
    }
}

$searchCompletionNotifierPath = Join-Path $repositoryRoot "src\Runtime\SearchCompletionNotifier.cs"
foreach ($check in @(
    @{ Path = $searchCompletionNotifierPath; Text = "if (!OperatingSystem.IsWindows())" },
    @{ Path = $searchCompletionNotifierPath; Text = "DisplayServer.GetName()" },
    @{ Path = $searchCompletionNotifierPath; Text = 'EntryPoint = "Shell_NotifyIconW"' },
    @{ Path = $searchCompletionNotifierPath; Text = 'EntryPoint = "LoadIconW"' },
    @{ Path = $searchCompletionNotifierPath; Text = "GetWindowThreadProcessId(foreground, out uint processId)" },
    @{ Path = $searchCompletionNotifierPath; Text = "ShellNotifyIcon(NotifyIconDelete, ref data)" },
    @{ Path = $controllerPath; Text = "SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Stale)" },
    @{ Path = $turnSetupPath; Text = "SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed)" },
    @{ Path = $solverSettingsGeneralPath; Text = "CreateSearchCompletionNotificationPolicyInput()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing search completion notification boundary '$($check.Text)'")
    }
}

foreach ($check in @(
    @{ Path = $solverSettingsPanelPath; Text = "TrySelectPage(SettingsPage page)" },
    @{ Path = $solverSettingsPanelPath; Text = "CommitPending()" },
    @{ Path = $solverSettingsGeneralPath; Text = "CreateGeneralPage()" },
    @{ Path = $solverSettingsPerformancePath; Text = "CreatePerformancePage()" },
    @{ Path = $solverSettingsPerformancePath; Text = "SetAdvancedParametersExpanded" },
    @{ Path = $solverSettingsBugReportsPath; Text = "CreateBugReportsPage()" },
    @{ Path = $solverSettingsControlsPath; Text = "CreatePageScroll(Control content)" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing settings panel ownership boundary '$($check.Text)'")
    }
}

$mirrorRegistryPath = Join-Path $repositoryRoot "src\Engine\Common\Mirrors\MethodMirrorRegistry.cs"
foreach ($check in @(
    @{ File = 'src/Search/SearchPolicySnapshot.cs'; Text = 'IReadOnlyList<RelicCounterTarget> RelicTargets' },
    @{ File = 'src/Search/CombatBeamSolver.Phases.cs'; Text = 'policy.RelicTargetsSatisfied(node.Snapshot.RelicCounters)' },
    @{ File = 'src/Search/CombatSearchCoordinator.cs'; Text = 'policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)' },
    @{ File = 'src/Runtime/SolvedRouteCache.cs'; Text = 'policy.RelicTargets' },
    @{ File = 'src/Search/CombatBeamSolver.Expansion.cs'; Text = 'ApplyFixedPrefix(seed, prefix)' },
    @{ File = 'src/UI/SolverRelicStrategyPanel.cs'; Text = 'row.Enabled.ButtonPressed' })) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $check.File) -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.File): missing relic policy ownership '$($check.Text)'")
    }
}
$dynamicVarMetadataPath = Join-Path $repositoryRoot "src\Runtime\DynamicVarCloneMetadataPatches.cs"
foreach ($rule in @('SimulationNotificationIsolation.IsActive', '"DynamicVarUpgrades"', 'table.TryGetValue(source', 'Tips.TryGetValue(__0')) {
    if (-not (Select-String -LiteralPath $dynamicVarMetadataPath -SimpleMatch $rule -Quiet)) {
        $violations.Add("DynamicVarCloneMetadataPatches.cs: missing sparse metadata boundary '$rule'")
    }
}
if (Select-String -LiteralPath $dynamicVarMetadataPath -SimpleMatch '.Clear()' -Quiet) {
    $violations.Add('DynamicVarCloneMetadataPatches.cs: global metadata clearing is forbidden')
}
$mirrorDescriptorPath = Join-Path $repositoryRoot "src\Engine\Common\Mirrors\MethodMirrorRegistryDescriptor.cs"
$coverageCatalogPath = Join-Path $repositoryRoot "tools\CoverageCatalog\Program.cs"
foreach ($check in @(
    @{ Path = $mirrorDescriptorPath; Text = "public interface IMethodMirrorRegistryDescriptorProvider" },
    @{ Path = $mirrorDescriptorPath; Text = "public sealed record MethodMirrorRegistryDescriptor(" },
    @{ Path = $mirrorRegistryPath; Text = ": IMethodMirrorRegistryDescriptorProvider" },
    @{ Path = $mirrorRegistryPath; Text = "public MethodMirrorRegistryDescriptor DescribeMirrorSupport()" },
    @{ Path = $coverageCatalogPath; Text = "registry is not IMethodMirrorRegistryDescriptorProvider descriptorProvider" },
    @{ Path = $coverageCatalogPath; Text = "descriptorProvider.DescribeMirrorSupport()" })) {
    if (-not (Select-String -LiteralPath $check.Path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("$($check.Path): missing mirror registry descriptor boundary '$($check.Text)'")
    }
}
foreach ($privateRegistryField in @('"_registrations"', '"_inferrer"', '"_strictInferrer"')) {
    foreach ($match in Select-String -LiteralPath $coverageCatalogPath -SimpleMatch $privateRegistryField) {
        $violations.Add("${coverageCatalogPath}:$($match.LineNumber): private registry reflection '$privateRegistryField' returned")
    }
}
if (Select-String -LiteralPath (Join-Path $repositoryRoot "src\Search\SimulatedCombatState.cs") `
        -SimpleMatch "_monsterAiStates?.Remove(creature)" -Quiet) {
    $violations.Add("SimulatedCombatState.cs: active-roster removal must retain known-monster AI state through move completion")
}

$metadataReuseChecks = @(
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'if (matches.Count != 2' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'code[i].labels.Count != 0 || code[i].blocks.Count != 0' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'IReadOnlyList<PowerModel>? powers = effectivePrefix is not null ? _effectivePowers : null;' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = '_effectiveHookListenerPrefix = null;' },
    @{ File = 'src/Search/SimulatedCombatState.Fork.cs'; Text = 'ReferenceEquals(_activeHookListenerPrefix, _effectiveHookListenerPrefix)' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'private IReadOnlyList<AbstractModel> GetBaseHookListenerPrefix()' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'if (insertionIndex < 0 && requirePrefixAnchor)' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = '_baseHookListenerPrefix = null;' },
    @{ File = 'src/Search/SimulatedCombatState.cs'; Text = 'private void InvalidateCardAndOrbHookListeners()' },
    @{ File = 'src/Search/SimulatedCombatState.Fork.cs'; Text = 'fork._baseHookListenerPrefix = RemapCachedModels(_baseHookListenerPrefix, context);' },
    @{ File = 'src/Search/CombatBeamSolver.BeamRetentionPolicy.cs'; Text = 'group.RankSummary = new(' },
    @{ File = 'src/Search/CombatBeamSolver.BeamRetentionPolicy.cs'; Text = 'ComputeRoutingParentRetentionRank(group)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'shared.Matches(source)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'Volatile.Write(ref _sharedLayouts[slot], layout)' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'source.Count <= MaxSharedLayoutLength' },
    @{ File = 'src/Engine/Common/MirroredHookListenerFilter.cs'; Text = 'BaseHooks.Append(NativeKeywordHook)' },
    @{ File = 'src/Engine/InCombat/Simulation/CombatPredictedCardExtensions.cs'; Text = '!listeners.HasAny(MirroredHookMask.TryModifyKeywordsInCombat)' }
)
foreach ($check in $metadataReuseChecks) {
    $path = Join-Path $repositoryRoot $check.File
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing exact metadata reuse boundary '$($check.Text)'")
    }
}

# Keep the no-op dispatch metadata complete when callbacks are added to the facade.
foreach ($file in @("CombatBeamSolver.RetentionJobs.cs", "CombatBeamSolver.BeamRetentionPolicy.cs")) {
    foreach ($forbidden in @("Parallel.For(", "Task.Run(")) {
        if (Select-String -LiteralPath (Join-Path $searchRoot $file) -SimpleMatch $forbidden -Quiet) {
            $violations.Add("$($file): retention work bypassed fixed lanes '$forbidden'")
        }
    }
}

$mirroredFilterText = Get-Content -LiteralPath (Join-Path $repositoryRoot "src/Engine/Common/MirroredHookListenerFilter.cs") -Raw
$mirroredHookNames = [System.Collections.Generic.HashSet[string]]::new()
[void]$mirroredHookNames.Add('TryModifyKeywordsInCombat')
foreach ($sourceFile in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors") -Filter '*.cs' -Recurse) {
    $sourceText = Get-Content -LiteralPath $sourceFile.FullName -Raw
    foreach ($match in [regex]::Matches($sourceText, 'nameof\(AbstractModel\.([A-Za-z][A-Za-z0-9]*)\)')) {
        [void]$mirroredHookNames.Add($match.Groups[1].Value)
    }
}
$hookFacadeText = Get-Content -LiteralPath (Join-Path $repositoryRoot "src/Engine/InCombat/Mirrors/HookMirrors.cs") -Raw
foreach ($match in [regex]::Matches($hookFacadeText, '(?:listener|modifier)\.([A-Za-z][A-Za-z0-9]*)\(')) {
    [void]$mirroredHookNames.Add($match.Groups[1].Value)
}
foreach ($hookName in $mirroredHookNames) {
    if (-not $mirroredFilterText.Contains("nameof(AbstractModel.$hookName)")) {
        $violations.Add("Missing mirrored hook participation metadata: $hookName")
    }
}

# Native clone eligibility stays outside search scheduling and keeps the runtime gate.
foreach ($requiredCloneBoundary in @(
    @{ Path = 'src/Runtime/BaseLibCloneConcurrencyPatch.cs'; Text = 'BaseLibCloneConcurrency.Enter()' },
    @{ Path = 'src/Engine/Common/PredictionUtils.cs'; Text = 'NativeModelCloneConcurrency.CanCloneIndependently(source)' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $requiredCloneBoundary.Path) -SimpleMatch $requiredCloneBoundary.Text -Quiet)) {
        $violations.Add("Missing clone boundary: $($requiredCloneBoundary.Path)")
    }
}
if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Engine/Common/NativeModelCloneConcurrency.cs') -SimpleMatch 'CombatSolver.Search' -Quiet) {
    $violations.Add('Clone eligibility depends on search policy.')
}

# Stable manual-potion prefixes share action completion and retain ordinary Fork guards.
foreach ($rule in @(
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = 'seed.AssertForkable();' },
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Prediction/PotionChoiceContinuation.cs'; Text = '!PotionChoiceMirrors.RequiresChoice(potion)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PotionChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PotionChoiceContinuation.cs'; Text = '_run.PotionChoicePrefixForks++;' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.Expansion.cs'; Text = 'PotionExecutionSupport.Complete(' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs'; Text = 'PotionCheckpoint?.Dispose();' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ParallelExpansion.cs'; Text = '_run.PotionChoicePrefixForks += source.PotionChoicePrefixForks;' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing potion continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
    if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Prediction/PotionChoiceContinuation.cs') -SimpleMatch $forbidden -Quiet) {
        $violations.Add("Potion continuation retained an executable closure: $forbidden")
    }
}

# Nested execution saves owned data frames and preserves the ordinary transaction guards.
foreach ($rule in @(
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs'; Text = 'GuardOrdinaryExecutionContinuationFork();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'if (_owner.HasCapturedExecutionContinuation && !_acknowledged)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'StateStore.SupportsManualCardChoiceContinuation' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'DetachPendingExecutionChoice();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'CombatPredictionState state = State.Fork(context);' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'step.Scopes?.Fork(context), step.Frame.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs'; Text = 'using (_trace.ResumeExecution(step.Trace))' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.DrawContinuation.cs'; Text = 'mapped! : card.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs'; Text = 'unresolved.SetEquals(deferred)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.ExecutionContinuation.cs'; Text = 'active.SetEquals(activePlays)' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.ExecutionScopes.cs'; Text = 'ForkExecutionDeaths(Deaths, context);' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'tail.ConsumedChoices != prefix.Count' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'; Text = '_simulator = null; _parent = null; _action = null; _prefix = null; _continuation = null;' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.ParallelExpansion.cs'; Text = '_run.ExecutionChoiceReuses += source.ExecutionChoiceReuses;' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing execution continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($relativePath in @(
    'src/Engine/InCombat/Simulation/CombatPredictionSimulator.ExecutionContinuation.cs',
    'src/Search/SimulatedCombatState.ExecutionScopes.cs',
    'src/Search/CombatBeamSolver.ExecutionChoiceContinuation.cs'
)) {
    foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
        if (Select-String -LiteralPath (Join-Path $repositoryRoot $relativePath) -SimpleMatch $forbidden -Quiet) {
            $violations.Add("Execution continuation retained an executable closure: $relativePath : $forbidden")
        }
    }
}

# A suspended own-choice frame belongs to its continuation; ordinary Fork remains strict.
foreach ($rule in @(
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs'; Text = 'GuardOrdinaryCardContinuationFork();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'context.Register(source.Play, play);' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'StateStore.SupportsManualCardChoiceContinuation' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'DetachPendingManualCardChoice();' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'source.Choice.Fork(context)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'frame.Choice.Resolve(this, frame.Card)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs'; Text = 'child._blockGainedByCardPlay.Add(play, block)' },
    @{ RelativePath = 'src/Engine/InCombat/Simulation/CombatPredictionHistory.CardContinuation.cs'; Text = 'e.Options.Select(CopyOption)' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.CardContinuation.cs'; Text = 'Options = spec.Options.Select(context.RequireRemap)' },
    @{ RelativePath = 'src/Prediction/CardChoiceContinuation.cs'; Text = 'lock (_gate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.CardChoiceContinuation.cs'; Text = 'ReferenceEquals(_parent, candidate)' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.CardChoiceContinuation.cs'; Text = 'return Enumerate(this, checkpoint, branches);' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.Expansion.cs'; Text = 'countTransition: false' },
    @{ RelativePath = 'src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs'; Text = 'CardCheckpoint?.Dispose();' },
    @{ RelativePath = 'src/Search/SimulatedCombatState.CardContinuation.cs'; Text = '_cardExecutionScopeDepth != 0' }
)) {
    if (-not (Select-String -LiteralPath (Join-Path $repositoryRoot $rule.RelativePath) -SimpleMatch $rule.Text -Quiet)) {
        $violations.Add("Missing card continuation ownership boundary: $($rule.RelativePath): $($rule.Text)")
    }
}
foreach ($forbidden in @('Task<', 'Func<', 'Action<')) {
    if (Select-String -LiteralPath (Join-Path $repositoryRoot 'src/Prediction/CardChoiceContinuation.cs') -SimpleMatch $forbidden -Quiet) {
        $violations.Add("Continuation retained an executable closure: $forbidden")
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Refactor boundary verification failed with $($violations.Count) violation(s)."
}

$archiveContract = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Replay/CheckpointArchive.cs'))
if ($archiveContract -match '\b(Godot|SolverController|RunManager)\b') {
    throw 'Checkpoint archive contract must remain independent of the game runtime.'
}
$nativeReplay = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Testing/UnattendedTestRunner.NativeReplay.cs'))
if ($nativeReplay.Contains('ApplyReplayStateAsync(')) {
    throw 'Native recorded replay must reconstruct state through native actions.'
}
Write-Output "REFACTOR_BOUNDARIES_OK search_files=$($searchFiles.Count)"
