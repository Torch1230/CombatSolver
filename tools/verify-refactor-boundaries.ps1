#requires -Version 7.0

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$searchRoot = Join-Path $repositoryRoot "src\Search"

$forbiddenSearchReferences = @(
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
    "UnattendedTestRunner"
)

$violations = [System.Collections.Generic.List[string]]::new()
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
        "EndTurnPowerSupport.TriggerLate(")) {
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
$openingChannelMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'List<List<SearchNode>> openingChannels = pool' | Select-Object -First 1
$orderedCoordinatorMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);' | Select-Object -First 1
$cycleRegionMatch = Select-String -LiteralPath $retentionPath -SimpleMatch 'cycleRegionTransaction = ApplyCycleRegionRetention(' | Select-Object -First 1
if ($null -eq $openingChannelMatch `
    -or $null -eq $orderedCoordinatorMatch `
    -or $null -eq $cycleRegionMatch `
    -or $openingChannelMatch.LineNumber -ge $orderedCoordinatorMatch.LineNumber `
    -or $orderedCoordinatorMatch.LineNumber -ge $cycleRegionMatch.LineNumber) {
    $violations.Add("${retentionPath}: opening/independent channels must settle before ordered admission, which must settle before CycleRegion")
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

$sessionPath = Join-Path $repositoryRoot "src\Runtime\SolverControllerSessions.cs"
foreach ($sessionType in @("SolverCombatSession", "SolverSearchSession", "SolverDeploymentSession")) {
    if (-not (Select-String -LiteralPath $sessionPath -SimpleMatch "class $sessionType" -Quiet)) {
        $violations.Add("${sessionPath}: missing controller session type '$sessionType'")
    }
}

$forkBoundaryChecks = @(
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
foreach ($gcChainRule in @(
    "return WaitForReclaimChainAsync(_reclaimTask)",
    "CollectGeneration2InBackgroundAsync(inSearchCheckpoint: true)",
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
    @{ RelativePath = "src/Search/CombatBeamSolver.Models.cs"; Text = "SnapshotListBuffer<PredictedCard> SnapshotLiveCards = new()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.StateEvaluation.cs"; Text = "_run.SnapshotLiveCards.Rent()" },
    @{ RelativePath = "src/Search/CombatBeamSolver.Phases.cs"; Text = "SearchWaveMemoryPolicy.Capacity(" })) {
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
    "Shiv when combat.GetAmount<FanOfKnivesPower>",
    "SovereignBlade when combat.GetAmount<SeekingEdgePower>")) {
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
    "CombatBeamSolver.ActionPreparation.cs",
    "CombatBeamSolver.AdmittedExpansion.cs",
    "CombatBeamSolver.BeamRetentionPolicy.cs",
    "CombatBeamSolver.CompactReplay.cs",
    "CombatBeamSolver.CrossTurnPlanning.cs",
    "CombatBeamSolver.CyclePlanning.cs",
    "CombatBeamSolver.CycleRegionRetention.cs",
    "CombatBeamSolver.DeferredFrontier.cs",
    "CombatBeamSolver.Expansion.cs",
    "CombatBeamSolver.FinalPlanOrdering.cs",
    "CombatBeamSolver.Models.cs",
    "CombatBeamSolver.OrderedMutationRetention.cs",
    "CombatBeamSolver.ParallelExpansion.cs",
    "CombatBeamSolver.PathDiagnostics.cs",
    "CombatBeamSolver.Phases.cs",
    "CombatBeamSolver.PrimaryChoiceReplay.cs",
    "CombatBeamSolver.ReadView.cs",
    "CombatBeamSolver.Retention.cs",
    "CombatBeamSolver.RetentionJobs.cs",
    "CombatBeamSolver.RoundLifecycle.cs",
    "CombatBeamSolver.StateEvaluation.cs",
    "CombatBeamSolver.StandPatJobs.cs",
    "CombatBeamSolver.Terminal.cs"
)
$pathDiagnosticsPath = Join-Path $searchRoot "CombatBeamSolver.PathDiagnostics.cs"
$deferredFrontierPath = Join-Path $searchRoot "CombatBeamSolver.DeferredFrontier.cs"
foreach ($required in @(
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.BeamRetentionPolicy.cs"); Text = 'if (values.HasRetainedRoutingChoice)' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.SearchPolicy.cs"); Text = 'seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);' },
    @{ Path = $deferredFrontierPath; Text = 'private sealed class DeferredTurnFrontier(' },
    @{ Path = $deferredFrontierPath; Text = '_run.DeferredFrontierReplayActions++;' },
    @{ Path = $deferredFrontierPath; Text = 'node with { Snapshot = replayed }' },
    @{ Path = (Join-Path $searchRoot "CombatBeamSolver.Phases.cs"); Text = 'CaptureDeferredFrontier(nextPlays, prunedPlays);' },
    @{ Path = (Join-Path $searchRoot "CombatSearchCoordinator.FailureRecovery.cs"); Text = 'RecoverDeferredTurnFrontier = true' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.Executor.cs"); Text = 'KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111' },
    @{ Path = (Join-Path $repositoryRoot "src/Testing/UnattendedTestRunner.KnownCustomDeferredFrontier.cs"); Text = 'MetadataContractOnly:NotFrontierQualityOrPerformance' },
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
    @{ File = "CombatBeamSolver.cs"; Text = "internal sealed partial class CombatBeamSolver(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private readonly SearchRunContext _run = new(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private BeamRetentionPolicy Retention =>" },
    @{ File = "CombatBeamSolver.cs"; Text = "private FinalPlanOrdering FinalOrdering =>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class BeamRetentionPolicy(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankBest(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public void Clear() => NodesByChoice.Clear();" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "routingNodes = new RoutingChoiceNodes(node);" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "ReturnRoutingChoiceScratch(scratch);" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private readonly record struct TranspositionLabel(" },
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
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "_completedActions == Actions.Count && _completedPotions == Potions.Count" },
    @{ File = "CombatBeamSolver.AdmittedExpansion.cs"; Text = "ready.TransferPotionTo(Aggregate!, candidate);" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "private sealed class PrimaryChoiceReplayFrontier : IDisposable" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "=> branches >= 2 && finals >= branches && attempts >= branches;" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "public bool CanDispatchContinuation => CompletedReplays == Actions.Length" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (!budget.TrySpendReplayAttempt())" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "frontier.AssertConsumed();" },
    @{ File = "CombatBeamSolver.PrimaryChoiceReplay.cs"; Text = "if (index != NextReplay || count < 1 || count > 4 || index + count > Actions.Length)" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "public ParallelExpansionExecutor? ActiveParallelExpansion;" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "_coordinator._run.ActiveParallelExpansion = null;" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "private void PrepareStandPatProbes(IEnumerable<SearchNode> nodes)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "seen.Add(node.StateKey)" },
    @{ File = "CombatBeamSolver.StandPatJobs.cs"; Text = "_run.StandPatCache.Add(pending[index].StateKey, evaluations[index]);" },
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
    @{ File = "CombatBeamSolver.RoundLifecycle.cs"; Text = "private SearchBoundaryReason AdvanceRound(" },
    @{ File = "CombatBeamSolver.RoundLifecycle.cs"; Text = "private SearchBoundaryReason AdvancePlayerTurnStart(" },
    @{ File = "CombatBeamSolver.RoundLifecycle.cs"; Text = "return AdvancePlayerTurnStart(" },
    @{ File = "CombatBeamSolver.RoundLifecycle.cs"; Text = "private sealed class RoundPrefixReplayContext(" },
    @{ File = "CombatBeamSolver.RoundLifecycle.cs"; Text = "private SearchBoundaryReason ResumeRoundPrefix(" },
    @{ File = "CombatBeamSolver.Expansion.cs"; Text = "using RoundPrefixReplayContext? roundPrefix" },
    @{ File = "SimulatedCombatState.ActionChoices.cs"; Text = "internal CombatPredictionSimulator ForkCompletedRoundPrefix(" },
    @{ File = "SimulatedCombatState.ActionChoices.cs"; Text = "!cursor.IsEmptyCompletedPhaseCursor" },
    @{ File = "SimulatedCombatState.ActionChoices.cs"; Text = "return simulator.Fork();" },
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
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs") -SimpleMatch "CreateWholeActionChoiceBudget" -Quiet)) {
    $violations.Add("CombatBeamSolver.Expansion.cs: repeated card choices are missing their whole-action branch quota")
}
if (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs") -Pattern 'private SearchBoundaryReason (AdvanceRound|AdvancePlayerTurnStart)\(' -Quiet) {
    $violations.Add("CombatBeamSolver.Expansion.cs: round lifecycle must remain in RoundLifecycle")
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
        Text = "int maxHandSize = GetMaxHandSize(player)"
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
    @{ Path = 'tools/run-unattended-test.ps1'; Text = ". (Join-Path `$PSScriptRoot 'headless-runtime.ps1')" },
    @{ Path = 'tools/run-unattended-test.ps1'; Text = 'if ($StopInstance) {' },
    @{ Path = 'tools/run-headless-matrix.sh'; Text = '--stop-instance' },
    @{ Path = 'tools/run-headless-matrix.ps1'; Text = '"-StopInstance"' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_prepare_snapshot() {' },
    @{ Path = 'tools/headless-runtime.sh'; Text = 'hr_bind() {' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessGameSnapshot(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Enter-HeadlessHostLease(' },
    @{ Path = 'tools/headless-runtime.ps1'; Text = 'function Set-HeadlessHostGame(' })) {
    $path = Join-Path $repositoryRoot $check.Path
    if (-not (Select-String -LiteralPath $path -SimpleMatch $check.Text -Quiet)) {
        $violations.Add("${path}: missing headless infrastructure ownership boundary '$($check.Text)'")
    }
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
    @{ Path = $unattendedProtocolHostPath; Text = "private sealed class ProtocolHost" },
    @{ Path = $unattendedProtocolHostPath; Text = "private async Task RunRequestLoopAsync(NGame host)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Activate(UnattendedTestRequest request)" },
    @{ Path = $unattendedProtocolHostPath; Text = "private void Reset()" },
    @{ Path = $unattendedWriterPath; Text = "private sealed class Writer(" },
    @{ Path = $unattendedWriterPath; Text = "public RuntimeMemorySnapshot Write(" },
    @{ Path = $unattendedWriterPath; Text = "private static void WriteResult(UnattendedTestResult result, UnattendedTestRequest request)" },
    @{ Path = $unattendedScenarioBuilderPath; Text = "private sealed class ScenarioBuilder(" },
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
    (Join-Path $repositoryRoot "src\UI\SolverActionPill.cs")
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

$ritsuTargetLookupPath = Join-Path $repositoryRoot "src/Runtime/RitsuBaseLibTargetTypeLookupPatch.cs"
foreach ($rule in @('ConditionalWeakTable<Assembly, Resolution>', 'SimulationNotificationIsolation.IsActive', '__0.IsDynamic', 'callbacks.Length != 1')) {
    if (-not (Select-String -LiteralPath $ritsuTargetLookupPath -SimpleMatch $rule -Quiet)) {
        $violations.Add("${ritsuTargetLookupPath}: missing metadata cache boundary '$rule'")
    }
}

$metadataReuseChecks = @(
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'Enum.GetUnderlyingType(typeof(PowerStackType)) != typeof(int)' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'if (matches.Count != 2' },
    @{ File = 'src/Runtime/PowerAmountComparisonPatch.cs'; Text = 'code[i].labels.Count != 0 || code[i].blocks.Count != 0' },
    @{ File = 'src/Runtime/AssemblyTypeAbsenceCache.cs'; Text = 'WeakReference<Assembly>[] DynamicAssemblies' },
    @{ File = 'src/Runtime/AssemblyTypeAbsenceCache.cs'; Text = 'AppDomain.CurrentDomain.AssemblyLoad' },
    @{ File = 'src/Runtime/AssemblyTypeAbsenceCache.cs'; Text = 'absence.Generation == Volatile.Read(ref _assemblyGeneration)' },
    @{ File = 'src/Runtime/AssemblyTypeAbsenceCache.cs'; Text = 'assembly.GetType(markerTypeName, throwOnError: false)' },
    @{ File = 'src/Runtime/RitsuBaseLibTargetTypeResolutionPatches.cs'; Text = '!SimulationNotificationIsolation.IsActive' },
    @{ File = 'src/Runtime/RitsuBaseLibTargetTypeResolutionPatches.cs'; Text = 'MissingType.ObserveResult(__state, __result)' },
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

# The value executor is admitted only through the captured-root search replay seam.
$compactRoot = Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/Compact'
foreach ($file in Get-ChildItem -LiteralPath $compactRoot -Filter *.cs -File -Recurse) {
    foreach ($reference in @('MegaCrit.', 'Godot', 'CombatPredictionSimulator', 'SimulatedCombatState', 'CardModel', 'Task', 'IEnumerator', 'Func<', 'Action<')) {
        foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch $reference) {
            $violations.Add("$($match.Path):$($match.LineNumber): compact execution must contain only owned values: $reference")
        }
    }
}
$compactProductionFiles = @($searchFiles) + @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src/Runtime') -Filter *.cs -File -Recurse)
foreach ($file in $compactProductionFiles) {
    foreach ($reference in @('ResumableDiscardProgram', 'CompactDiscardProjection', 'CompactDiscardReadView', 'CompactPhaseProbe', 'CompactCardMetadataReadBinding', 'CompactCardProgramCompiler', 'MonsterEffectProgram', 'DeterministicMonsterAi', 'CompactMonsterAiReadBinding', 'CompactRoundRoot', 'CompactRoundLayout', 'CompactPlanReplay')) {
        if ($file.Name -eq 'CombatBeamSolver.CompactReplay.cs' -and $reference -in @('ResumableDiscardProgram', 'CompactDiscardReadView', 'CompactPlanReplay')) { continue }
        foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch $reference) {
            $violations.Add("$($match.Path):$($match.LineNumber): compact model admission escaped the captured-root replay seam: $reference")
        }
    }
}
$compactProjection = Join-Path $repositoryRoot 'src/Prediction/Compact/CompactDiscardProjection.cs'
if (-not ([IO.File]::ReadAllText((Join-Path $compactRoot 'ResumableDiscardProgram.Rounds.cs'))).Contains('_round!.TryBeginPlayerSideStart(State)')) {
    $violations.Add('Side-start completion must belong to the reversible round state.')
}
if (-not ([IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Prediction/Compact/CompactDiscardReadView.cs'))).Contains('combat.ImportCompletedDoomAppliers(_combatHistory.DoomAppliers);')) {
    $violations.Add('Doom application history must be imported from committed values.')
}
$damageSimulator = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs'))
if ($damageSimulator.Contains('dealer?.IsDead')) { $violations.Add('Damage dealers must read branch vitals.') }
if (-not $damageSimulator.Contains('effects.CompletePlayerDeath(player);')) { $violations.Add('Player death lost domain cleanup before orb/pet handling.') }
if (-not $damageSimulator.Contains('petEffects.RemovePowersAfterDeath(creature);')) { $violations.Add('Pet death must clean Powers outside the enemy sweep.') }
if (-not ([IO.File]::ReadAllText((Join-Path $searchRoot 'SimulatedCombatState.cs'))).Contains('_rootOsties = source._rootOsties;')) { $violations.Add('Pet root identities and absence must survive forks.') }
if (([IO.File]::ReadAllText((Join-Path $searchRoot 'SimulatedCombatState.CardLifecycle.cs'))).Contains('?? player.Osty')) { $violations.Add('Pet reads must not fall through to live ownership.') }
if (([IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Prediction/MonsterMoveSemantics.cs'))).Contains('SetAmount<DieForYouPower>')) { $violations.Add('Monster damage must not retire pet protection Powers.') }
$compactPetReads = [IO.File]::ReadAllText((Join-Path $searchRoot 'SimulatedCombatState.CompletedOstyReads.cs'))
foreach ($replay in @('SummonOsty(', '.Damage(', '.Fork(', 'HookMirrors.', 'PowerCmd.', '.State.Write(')) {
    if ($compactPetReads.Contains($replay)) { $violations.Add("Pet read binding may only import supplied values: $replay") }
}
if ([IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.EndTurn.cs')).Contains('SaveManager')) { $violations.Add('Turn-end execution must not read live animation settings.') }
foreach ($required in @('AssertRepresentedHooks(runListeners[index], runPrefix: true, includeHandEnd, includePowerPhases, includeRounds);', '(key.RunPrefix || !RepresentedHook(key.Type, method.Name))')) {
    if (-not ([IO.File]::ReadAllText($compactProjection)).Contains($required)) {
        $violations.Add("Compact deck listeners lost their independent run-hook audit: $required")
    }
}
foreach ($reference in @('.ManualPlay(', '.AutoPlay(', '.Discard(', 'CardOnPlayMirrors.Invoke(', 'HookMirrors.')) {
    foreach ($match in Select-String -LiteralPath $compactProjection -SimpleMatch $reference) {
        $violations.Add("$($match.Path):$($match.LineNumber): compact projection must decode events without replaying effects: $reference")
    }
}
if (-not ([IO.File]::ReadAllText($compactProjection)).Contains('Program.State.HasSameRoot(program.State)')) {
    $violations.Add('Compact projection lost root ownership guard.')
}
$compactContextGuards = @(
    @('src/Testing/UnattendedTestRunner.CompactKernelProfile.cs', 'if (!SimulationNotificationIsolation.IsActive)'),
    @('src/Testing/UnattendedTestRunner.CompactKernel.cs', 'initialIsolation.Dispose();'),
    @('src/Testing/UnattendedTestRunner.CompactKernel.cs', 'continuationIsolation.Dispose();')
)
foreach ($guard in $compactContextGuards) {
    if (-not ([IO.File]::ReadAllText((Join-Path $repositoryRoot $guard[0]))).Contains($guard[1])) {
        $violations.Add("Compact profile lost simulation context boundary: $($guard[0]) / $($guard[1])")
    }
}
$compactCompiler = Join-Path $repositoryRoot 'src/Prediction/Compact/CompactCardProgramCompiler.cs'
foreach ($reference in @('.ManualPlay(', '.AutoPlay(', 'CardOnPlayMirrors.Invoke(', 'HookMirrors.', 'CardCmd.', 'PowerCmd.')) {
    foreach ($match in Select-String -LiteralPath $compactCompiler -SimpleMatch $reference) {
        $violations.Add("$($match.Path):$($match.LineNumber): card admission must compile definitions without executing effects: $reference")
    }
}
$compactReader = Join-Path $repositoryRoot 'src/Prediction/Compact/CompactDiscardReadView.cs'
foreach ($reference in @('.Materialize(', '.ManualPlay(', '.AutoPlay(', '.MutablePreview', '.State.Write(')) {
    foreach ($match in Select-String -LiteralPath $compactReader -SimpleMatch $reference) {
        $violations.Add("$($match.Path):$($match.LineNumber): completed reader must not reconstruct or mutate branch models: $reference")
    }
}
$compactPowerReads = Join-Path $searchRoot 'SimulatedCombatState.CompletedPowerReads.cs'
foreach ($reference in @('.ManualPlay(', '.AutoPlay(', '.Fork(', 'HookMirrors.', 'PowerCmd.', '.State.Write(')) {
    foreach ($match in Select-String -LiteralPath $compactPowerReads -SimpleMatch $reference) {
        $violations.Add("$($match.Path):$($match.LineNumber): completed Power binding may only import supplied values: $reference")
    }
}
foreach ($file in $compactProductionFiles) {
    if ($file.FullName -eq $compactPowerReads) { continue }
    foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch 'CompletedPowerReadBinding') {
        $violations.Add("$($match.Path):$($match.LineNumber): completed Power binding is not admitted to production execution.")
    }
}
$compactCardReads = Join-Path $repositoryRoot 'src/Prediction/Compact/CompactCardMetadataReadBinding.cs'
foreach ($replay in @('.ManualPlay(', '.AutoPlay(', '.Fork(', 'HookMirrors.', 'CardCmd.', '.State.Write(')) {
    foreach ($match in Select-String -LiteralPath $compactCardReads -SimpleMatch $replay) {
        $violations.Add("Card metadata binding may only import supplied completed values: $($match.LineNumber) / $replay")
    }
}
$compactReadGuards = @(
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'state.AssertForkable();'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'model._owner = source.Owner;'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'private readonly PowerModel[] _replacementModels;'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'PowerModel model = value.Retired ? _replacementModels[index] : _models[index];'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'model._amount = value.Amount;'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', '_state.InvalidateBaseHookListeners();'),
    @('src/Prediction/Compact/CompactCardMetadataReadBinding.cs', 'private readonly List<Binding> _active;'),
    @('src/Prediction/Compact/CompactCardMetadataReadBinding.cs', 'model.EnergyCost.CapturedXValue = captured;'),
    @('src/Prediction/Compact/CompactCardMetadataReadBinding.cs', 'model.HasBeenRemovedFromState = removed;'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', '_cards.Read(program);'),
    @('src/Search/SimulatedCombatState.cs', 'history?.Owner.Creature, history?.Exhausts'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'strategicRequirements, view?.CardValuesInvariant == true ? view.Invariants : null'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', '_adapter.CopyPowerReadValues(program, _powerValues);'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'SnapshotCore(view.EvaluationContext,'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', '!_adapter.Program.State.HasSameRoot(program.State) || !program.Complete'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', 'ValueRng rng = _program.ShuffleRng;'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'view?.EnergyCostRng ?? simulator.Rng.CombatEnergyCosts.CaptureState()'),
    @('src/Prediction/Compact/CompactCardMetadataReadBinding.cs', 'int amount = program.CostModifierAt(card, index);'),
    @('src/Engine/InCombat/Simulation/Compact/RandomDrawCost.cs', 'Buffer(card).Append(state, [cost]);'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'view?.ShuffleRng ?? simulator.Rng.Shuffle.CaptureState()'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'view is null ? simulator.TerminalStamp : view.TerminalStamp'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'view?.CardHistory, view?.EnemyRoster, view?.CombatHistory'),
    @('src/Search/CombatBeamSolver.ReadView.cs', 'view?.EnemyValuesInvariant == true ? view.Invariants : null'),
    @('src/Engine/InCombat/Simulation/SimCreatureState.cs', '_values.LoseHp(amount)'),
    @('src/Engine/InCombat/Simulation/Compact/CreatureValueSlots.cs', 'state.Write(Offset + 3, present ? 1 : 0)'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', '=> new(this, ForkRoot(), _player, _risks)'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'if (ResultPile(card) == Pile.Removed || !Ending)'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '!card.Effects.ExhaustsCards'),
    @('src/Prediction/Compact/CompactPlanReplay.cs', 'bool retrieve = lane.ChoiceRetrieves;'),
    @('src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs', 'if (target != 0 && target != Pet) _creatures[target].SetPresent(state, false);'),
    @('src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs', 'internal int EnemyEnd => Pet < 0 ? Count : Pet;'),
    @('src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs', 'state.Write(_petSummonedSlot, 1);'),
    @('src/Engine/InCombat/Simulation/Compact/BasicPowerLayout.cs', '_definitions[index].Kind == BasicPowerKind.DieForYou'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', 'int dealer = item.Dealer;'),
    @('src/Search/SimulatedCombatState.CompletedOstyReads.cs', '_combat._simulatedOstyMaxHp = summoned || _hadMap ? _maxHp : null;'),
    @('src/Engine/InCombat/Mirrors/Hooks/Damage/ModifyUnblockedDamageTargetMirrors.cs', 'context.State.GetCreature(power.Owner).IsAlive'),
    @('src/Search/CombatBeamSolver.RoundLifecycle.cs', 'participants = takingExtraTurn ? [_player.Creature] : simulatedCombat.Allies.ToArray();'),
    @('src/Engine/InCombat/Simulation/Compact/CreatureAttackLayout.cs', 'state.Write(_terminalSlot, DeathCompleted(state, 0) ? 2 : 1);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.Rounds.cs', 'if (PlayerTurn != 1) SummonPet(-1, _round.Root.TurnStartSummon);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.Rounds.cs', 'Emit(EventKind.CommitPlayerTurnHistory, -1);'),
    @('src/Search/SimulatedCombatState.cs', 'combatHistory?.LastAttacks, combatHistory?.PreviousTurnAttacks'),
    @('src/Search/SimulatedCombatState.cs', 'history?.Owner, history?.StatusDraws'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'view?.CumulativePlayerHpLost ?? combat.GetCumulativeHpLost(_player.Creature)'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.HandEnd.cs', 'if (!_handEndAdmitted || !Complete || !Enum.IsDefined(staging))'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.PowerPhases.cs', 'if (!_powerPhasesAdmitted || !Complete'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '_powerPhasesAdmitted = source._powerPhasesAdmitted;'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'model.AmountOnTurnStart = value.AmountOnTurnStart;'),
    @('src/Search/SimulatedCombatState.CompletedPowerReads.cs', 'model.SkipNextDurationTick = value.SkipNextDurationTick;'),
    @('src/Search/CompletedStateReadView.cs', 'A new stable root requires a new cache.'),
    @('src/Search/SimulatedCombatState.cs', 'private T? PreparePowerApplication<T>'),
    @('src/Search/SimulatedCombatState.cs', 'private PowerModel ApplyPreparedPower<T>'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '_events.Append(State, [item.Data, item.Metadata]);'),
    @('src/Engine/InCombat/Simulation/Compact/ReversibleValueBuffer.cs', 'state.Write(_header + TailOffset, leaf);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'private readonly ReversibleValueBuffer[] _piles;'),
    @('src/Search/SimulatedCombatState.cs', 'history?.Owner.Creature, history?.ShivPlays'),
    @('src/Engine/InCombat/Simulation/Compact/CardEffectProgram.cs', '_instructions = instructions.ToArray();'),
    @('src/Search/SimulatedCombatState.cs', 'PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)'),
    @('src/Runtime/ContinuationStamp.cs', 'PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)'),
    @('src/Prediction/PowerLifecycleSupport.cs', 'UsesNativeDurationSkip(power.GetType()) && power.SkipNextDurationTick'),
    @('src/Search/SimulatedCombatState.cs', 'simulated.SkipNextDurationTick = true;'),
    @('src/Search/SimulatedCombatState.cs', '!PowerLifecycleSupport.UsesNativeDurationSkip(powerType) && !alreadyPresent'),
    @('src/Search/SimulatedCombatState.cs', '!PowerLifecycleSupport.UsesNativeDurationSkip(typeof(T)) && !alreadyPresent'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '_round = source._round;'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.Rounds.cs', 'Emit(EventKind.BeginSide, -1);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'Emit(EventKind.Draw, drawn, card < 0 ? 1 : 0);'),
    @('src/Engine/InCombat/Simulation/Compact/DeterministicMonsterAi.cs', '_log.Append(state, [next]);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '_monsterAi = source._monsterAi;'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', 'combat.RequireCapturedMonsterAi(_creatures[1])'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', '_monsterAiBinding?.Read(program);'),
    @('src/Engine/InCombat/Simulation/Compact/MonsterEffectProgram.cs', '_instructions = instructions.ToArray();'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.Monsters.cs', 'if (_monsterMoves == null || !Complete || Terminal || Ending'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', '_monsterMoves = source._monsterMoves;'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', 'metadata.CurrentMonsterMove(_creatures[1])'),
    @('src/Search/SimulatedCombatState.cs', 'history?.CreatureAttacks, combatHistory?.CreatureAttacks'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'State.Write(Frame + EffectIndexOffset, Read(Frame + EffectIndexOffset) + 1);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'State.Write(Frame + FirstDrawnOffset, drawn);'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', 'CompactCardProgramCompiler.Compile(card, includeAttacks, shivTemplate, inkyShivTemplate)'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'State.Write(Frame + DrawResumeIpOffset, resumeIp);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'private void ApplyTemporaryStrengthLoss(int card, int target, int amount)'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'if (!PreparePower(card, target, BasicPowerKind.PiercingWail, amount)) return;'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'CommitPower(card, target, BasicPowerKind.PiercingWail, amount);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'ValidateBlockReturns(definitions, powers);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'ApplyPower(card, 0, instruction.Power, (int)returned);'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'sum = checked(sum + _powers!.Amount(State, target, instruction.Power));'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'if (CreaturePresent(target) && Creature(target).CurrentHp > 0)'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', 'ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.NoDealer'),
    @('src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs', 'WriteRng(rng);'),
    @('src/Testing/UnattendedTestRunner.CompactShufflePower.cs', 'isolation.Dispose();'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', '=> SnapshotCore(simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths, null);'),
    @('src/Search/CombatBeamSolver.StateEvaluation.cs', 'result.ReleaseSimulator();')
)
foreach ($guard in $compactReadGuards) {
    if (-not ([IO.File]::ReadAllText((Join-Path $repositoryRoot $guard[0]))).Contains($guard[1])) {
        $violations.Add("Completed reader lost ownership/formula boundary: $($guard[0]) / $($guard[1])")
    }
}
foreach ($file in $compactProductionFiles) {
    if ($file.Name -in @('CombatBeamSolver.StateEvaluation.cs', 'CombatBeamSolver.CompactReplay.cs')) { continue }
    foreach ($match in Select-String -LiteralPath $file.FullName -SimpleMatch 'SnapshotFromReadView(') {
        $violations.Add("$($match.Path):$($match.LineNumber): closed completed reader is not admitted to production execution.")
    }
}
$compactStorageGuards = @(
    @('ReversibleValueState.cs', 'private long[] _values;'),
    @('ReversibleValueState.cs', '_dirtyPages[entry.Slot / PageWidth] = true;'),
    @('ReversibleValueState.cs', 'if (!source.HasRoot(_rootIdentity))'),
    @('ReversibleValueState.cs', 'if (_checkpoints.Count != 0)'),
    @('ReversibleValueState.cs', 'Resize(checkpoint.SlotCount);'),
    @('ReversibleValueState.FrozenValues.cs', 'workspace.Resize(Count);'),
    @('ReversibleValueState.FrozenValues.cs', '_pages = source._pages.AsSpan(0, PageCount(source.Count)).ToArray();'),
    @('ReversibleValueState.FrozenValues.cs', 'private readonly long[] _values = values;')
)
foreach ($guard in $compactStorageGuards) {
    if (-not ([IO.File]::ReadAllText((Join-Path $compactRoot $guard[0]))).Contains($guard[1])) {
        $violations.Add("Compact storage lost ownership boundary: $($guard[0]) / $($guard[1])")
    }
}
foreach ($reference in @('ReversibleValueState _owner', 'ReversibleValueState _workspace', 'FrozenValues _parent')) {
    foreach ($match in Select-String -LiteralPath (Join-Path $compactRoot 'ReversibleValueState.FrozenValues.cs') -SimpleMatch $reference) {
        $violations.Add("$($match.Path):$($match.LineNumber): compact candidate must not retain mutable workers or ancestor chains: $reference")
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

$compactRoundReads = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.CompletedRoundReads.cs'
$compactRoundText = Get-Content -LiteralPath $compactRoundReads -Raw
foreach ($forbidden in @('.Fork(', '.ManualPlay(', 'TriggerSideTurnStart(', 'SnapshotPowerAmountsAtTurnStart(', '.State.Write(')) {
    if ($compactRoundText.Contains($forbidden)) { throw "Completed round reads must only import clock and history maps: $forbidden" }
}
Get-ChildItem (Join-Path $repositoryRoot 'src/Search'), (Join-Path $repositoryRoot 'src/Runtime') -Recurse -Filter '*.cs' | ForEach-Object {
    if ($_.FullName -ne $compactRoundReads -and (Get-Content -LiteralPath $_.FullName -Raw).Contains('CompletedRoundReadBinding')) {
        throw "Completed round binding is not admitted to production execution: $($_.FullName)"
    }
}

$compactAiReads = Join-Path $repositoryRoot 'src/Search/SimulatedCombatState.CompletedMonsterAiReads.cs'
$compactAiText = Get-Content -LiteralPath $compactAiReads -Raw
foreach ($forbidden in @('.Fork(', 'RollMove(', 'AdvanceMonsterAi(', 'BranchMonsterAi.Capture(', '.State.Write(')) {
    if ($compactAiText.Contains($forbidden)) { throw "Completed monster AI reads may only import supplied values: $forbidden" }
}
Get-ChildItem (Join-Path $repositoryRoot 'src/Search'), (Join-Path $repositoryRoot 'src/Runtime') -Recurse -Filter '*.cs' | ForEach-Object {
    if ($_.FullName -ne $compactAiReads -and (Get-Content -LiteralPath $_.FullName -Raw).Contains('CompletedMonsterAiReadBinding')) {
        throw "Completed AI binding is not admitted to production execution: $($_.FullName)"
    }
}

# Runtime admission has one main-thread owner; workers only consume the captured policy.
Get-ChildItem (Join-Path $repositoryRoot 'src/Runtime') -Recurse -Filter '*.cs' | ForEach-Object {
    if ($_.Name -ne 'SearchBackendPolicy.cs' -and (Get-Content -LiteralPath $_.FullName -Raw).Contains('CompactRoot')) {
        throw "Runtime compact selection must belong to SearchBackendPolicy: $($_.FullName)"
    }
}
foreach ($rule in @(
    @('src/Runtime/SearchBackendPolicy.cs', 'if (!NGame.IsMainThread())'),
    @('src/Runtime/SearchBackendPolicy.cs', 'CompactCombatRoot.TryCreate(root.ForkSimulator(), root.PlayerIdentity, out compact, out rejection);'),
    @('src/Runtime/SolverController.cs', 'searchPolicy = SearchBackendPolicy.Capture(rootSnapshot, searchPolicy);'),
    @('src/Runtime/PlayerTurnSetupPatches.cs', 'searchPolicy = SearchBackendPolicy.Capture(rootSnapshot, searchPolicy);'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', 'Any(slot => combat.GetPotionAtSlot(player, slot) != null)'),
    @('src/Prediction/Compact/CompactCardProgramCompiler.cs', '!AdmittedTypes.Contains(card.GetType())'),
    @('src/Prediction/Compact/CompactDiscardProjection.cs', 'lock (_rootForkGate) return _root.Fork();'),
    @('src/Search/CombatPlan.cs', '_compact = null;'),
    @('src/Search/CombatPlan.cs', 'CompactPendingChoice = null;'),
    @('src/Prediction/Compact/CompactDiscardReadView.cs', '!_adapter.Program.State.HasSameRoot(program.State) || !program.NeedsChoice'),
    @('src/Prediction/Compact/CompactPlanReplay.cs', 'var cursor = new TurnStartChoiceCursor(choices);'),
    @('src/Prediction/Compact/CompactPlanReplay.cs', '_metadata[id].Clone()'),
    @('src/Prediction/Compact/CompactMonsterAiReadBinding.cs', '_attacks[program.PublishedMonsterIntentMove]'),
    @('src/Search/CombatBeamSolver.CompactReplay.cs', 'SnapshotFromReadView(lane.Reader,'),
    @('src/Search/CombatBeamSolver.ParallelExpansion.cs', 'ReferenceEquals(_compact, parent)'),
    @('src/Search/CombatBeamSolver.CompactReplay.cs', 'private sealed class CompactPolicyReadLane'),
    @('src/Search/CombatBeamSolver.ActionPreparation.cs', 'TargetsFor(card, simulator, view)'),
    @('src/Search/CombatBeamSolver.Expansion.cs', 'foreach (PreparedCardAction prepared in PrepareCardActions(node))'),
    @('src/Search/CombatBeamSolver.Expansion.cs', 'foreach (PreparedPotionAction prepared in PreparePotionActions(node))'),
    @('src/Runtime/ContinuationStamp.cs', '!ReferenceEquals(simulator, readView.EvaluationContext)'),
    @('src/Runtime/ContinuationStamp.cs', 'combat.AppendPredictedTurnCardHistory(text, player, readView?.CardHistory);'),
    @('src/Runtime/ContinuationStamp.cs', 'readView?.EnergyCostRng ?? simulator.Rng.CombatEnergyCosts.CaptureState()'),
    @('src/Engine/InCombat/Simulation/CombatPredictionState.cs', 'internal bool IsHittable(Creature creature, bool presentAndAlive)')
)) {
    if (-not (Get-Content -LiteralPath (Join-Path $repositoryRoot $rule[0]) -Raw).Contains($rule[1])) {
        throw "Compact captured-root ownership/evaluation guard missing: $($rule[0]): $($rule[1])"
    }
}

Write-Output "REFACTOR_BOUNDARIES_OK search_files=$($searchFiles.Count)"
