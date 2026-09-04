#requires -Version 7.0

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$searchRoot = Join-Path $repositoryRoot "src\Search"

$forbiddenSearchReferences = @(
    "SolverSettings.Current",
    "Entry.Logger",
    "SolverController",
    "SolverOverlay",
    "UnattendedTestRunner"
)

$violations = [System.Collections.Generic.List[string]]::new()
$searchFiles = Get-ChildItem -LiteralPath $searchRoot -Filter *.cs -File -Recurse
$beamFiles = Get-ChildItem -LiteralPath $searchRoot -Filter "CombatBeamSolver*.cs" -File
$beamPaths = @($beamFiles.FullName)
$cyclePlanningPath = Join-Path $searchRoot "CombatBeamSolver.CyclePlanning.cs"
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
foreach ($match in Select-String -LiteralPath $cyclePlanningPath -Pattern $scenarioSpecificCycleModelPattern) {
    $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning contains a scenario-specific model name or ID")
}
foreach ($directModelLookupPattern in @(
    '\bModelDb\.(?:Card|Power|Relic|Monster)\b',
    '\bGetAmount<[A-Za-z_][A-Za-z0-9_]*(?:Power|Relic|Monster)>',
    '\btypeof\([A-Za-z_][A-Za-z0-9_]*(?:Card|Power|Relic|Monster)\)')) {
    foreach ($match in Select-String -LiteralPath $cyclePlanningPath -Pattern $directModelLookupPattern) {
        $violations.Add("$($match.Path):$($match.LineNumber): generic cycle planning performs a direct concrete-model lookup")
    }
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
    "failure == null && (_regionExitRequired || _reclaimRequired)")) {
    if (-not (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch $gcChainRule -Quiet)) {
        $violations.Add("${searchGcPolicyPath}: missing serialized reclaim-chain rule '$gcChainRule'")
    }
}
if (Select-String -LiteralPath $searchGcPolicyPath -SimpleMatch "ReclaimAfterActiveCheckpointAsync" -Quiet) {
    $violations.Add("${searchGcPolicyPath}: recursive reclaim handoff returned")
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
    "CombatBeamSolver.BeamRetentionPolicy.cs",
    "CombatBeamSolver.CrossTurnPlanning.cs",
    "CombatBeamSolver.CyclePlanning.cs",
    "CombatBeamSolver.Expansion.cs",
    "CombatBeamSolver.FinalPlanOrdering.cs",
    "CombatBeamSolver.Models.cs",
    "CombatBeamSolver.ParallelExpansion.cs",
    "CombatBeamSolver.Phases.cs",
    "CombatBeamSolver.Retention.cs",
    "CombatBeamSolver.StateEvaluation.cs",
    "CombatBeamSolver.Terminal.cs"
)
$actualBeamFiles = @($beamFiles.Name | Sort-Object)
if (($actualBeamFiles -join "|") -ne (($expectedBeamFiles | Sort-Object) -join "|")) {
    $violations.Add(
        "CombatBeamSolver partial file set differs: actual=$($actualBeamFiles -join ',') " +
        "expected=$(($expectedBeamFiles | Sort-Object) -join ',')")
}
$beamStructureChecks = @(
    @{ File = "CombatBeamSolver.cs"; Text = "internal sealed partial class CombatBeamSolver(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private readonly SearchRunContext _run = new(" },
    @{ File = "CombatBeamSolver.cs"; Text = "private BeamRetentionPolicy Retention =>" },
    @{ File = "CombatBeamSolver.cs"; Text = "private FinalPlanOrdering FinalOrdering =>" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "private sealed class BeamRetentionPolicy(" },
    @{ File = "CombatBeamSolver.BeamRetentionPolicy.cs"; Text = "public List<SearchNode> RankBest(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private readonly record struct TranspositionLabel(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private sealed class SearchRunContext(" },
    @{ File = "CombatBeamSolver.Models.cs"; Text = "private readonly record struct SearchFeatures(" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "private sealed class ParallelExpansionExecutor : IDisposable" },
    @{ File = "CombatBeamSolver.ParallelExpansion.cs"; Text = "public ExpansionWorkerOutcome[] Evaluate(" },
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
if (-not (Select-String -LiteralPath (Join-Path $searchRoot "CombatBeamSolver.Expansion.cs") -SimpleMatch "ResolveWholeActionChoiceBranchLimit" -Quiet)) {
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
    @{ Path = $unattendedWriterPath; Text = "private static void WriteResult(UnattendedTestResult result)" },
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
    "private static void WriteResult(UnattendedTestResult result)",
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
$bugReportUploaderPath = Join-Path $repositoryRoot "src\Runtime\CombatBugReportUploader.cs"
$solverSettingsPanelPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.cs"
$solverSettingsGeneralPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.General.cs"
$solverSettingsPerformancePath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Performance.cs"
$solverSettingsBugReportsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.BugReports.cs"
$solverSettingsControlsPath = Join-Path $repositoryRoot "src\UI\SolverSettingsPanel.Controls.cs"
foreach ($check in @(
    @{ Path = $bugReportExporterPath; Text = "private static readonly BlockingCollection<Action> BackgroundOperations = new();" },
    @{ Path = $bugReportExporterPath; Text = "QueueCheckpointWrite(session, capture);" },
    @{ Path = $bugReportExporterPath; Text = "Task<ForensicArchiveBundle> forensicsTask = QueueBackground(" },
    @{ Path = $bugReportExporterPath; Text = "ForensicArchiveBundle forensics = await forensicsTask.ConfigureAwait(false);" },
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

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Refactor boundary verification failed with $($violations.Count) violation(s)."
}

Write-Output "REFACTOR_BOUNDARIES_OK search_files=$($searchFiles.Count)"
