using System.IO.Compression;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class Executor(UnattendedTestRunner runner)
    {
        private SolverSettingsData? _settingsBeforeTest;

        public void RestoreSettings()
        {
            if (_settingsBeforeTest != null)
                SolverSettings.ApplyForTesting(_settingsBeforeTest);
        }
        public void PrepareArchiveSettings()
        {
            if (runner._checkpointImport?["resolvedPolicy"] == null) return;
            _settingsBeforeTest ??= SolverSettings.Current;
            SolverSettings.ApplyForTesting(runner.ApplyRecordedCheckpointPolicy(_settingsBeforeTest));
        }

        public async Task<ExecutionOutcome> ExecuteAsync(ScenarioContext scenario)
        {
            UnattendedTestRequest request = runner._request;
            CombatState combatState = scenario.CombatState;
            Player player = scenario.Player;
            int startedTurn = scenario.StartedTurn;
            bool expectedCardPlayed = request.ExpectedPlayedCardId == null;
            bool expectedPotionUsed = request.ExpectedUsedPotionId == null;
            bool expectedPlayerPowerObserved = request.ExpectedObservedPlayerPowerId == null;
            if (request.ScenarioId == "CALL-OF-THE-VOID-GENERATION-ROOT")
            {
                await runner.AssertCallOfTheVoidGenerationRootAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-GENERATION-CLOSURE-AUDIT")
            {
                runner.AssertCompactGenerationClosureAudit(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-PAGESTORM-NATIVE")
            {
                await runner.AssertCompactPagestormAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-SHARED-FATE-NATIVE")
            {
                await runner.AssertCompactSharedFateAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PANACHE-INSTANCES")
            {
                await runner.AssertPanacheInstancesAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-PANACHE-NATIVE")
            {
                await runner.AssertCompactPanacheAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-PANACHE-TERMINAL" or "COMPACT-PANACHE-CARD-TERMINAL")
            {
                await runner.AssertCompactPanacheTerminalAsync(combatState, player, request.ScenarioId == "COMPACT-PANACHE-CARD-TERMINAL");
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-PANACHE-PLAYER-DEATH")
            {
                await runner.AssertCompactPanachePlayerDeathAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "HISTORY-COURSE-WINDOW")
            {
                await runner.AssertHistoryCourseWindowAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-LETHALITY-NATIVE")
            {
                await runner.AssertCompactLethalityAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DANSE-RESOLVED-NATIVE")
            {
                await runner.AssertDanseResolvedNativeAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-CARD-HOOKS-NATIVE")
            {
                await runner.AssertCompactCardHooksAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-SWIFT-AUTO")
            {
                await runner.AssertCompactSwiftAutoAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-SWIFT-EMPTY")
            {
                await runner.AssertCompactSwiftEmptyAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-SWIFT-TERMINAL")
            {
                await runner.AssertCompactSwiftTerminalAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-KEYWORD-GENERATED")
            {
                await runner.AssertCompactKeywordGeneratedAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-SNAP-TERMINAL" or "COMPACT-SCULPTING-STRIKE-TERMINAL")
            {
                await runner.AssertCompactKeywordTerminalAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-KEYWORD-EMPTY")
            {
                await runner.AssertCompactKeywordEmptyAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-KEYWORDS-NATIVE")
            {
                await runner.AssertCompactKeywordsAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-HANG-TERMINAL")
            {
                await runner.AssertCompactHangTerminalAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-HANG-NATIVE")
            {
                await runner.AssertCompactHangAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "HANG-CAP-NATIVE")
            {
                await runner.AssertHangCapAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-COST-POWERS-TERMINAL")
            {
                await runner.AssertCompactCostPowersTerminalAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId is "COMPACT-COST-POWERS-NATIVE" or "COMPACT-COST-POWERS-ROOT-STACKS")
            {
                await runner.AssertCompactCostPowersAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-NECRO-CARDS-NATIVE")
            {
                await runner.AssertCompactNecroCardsAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-CAPTURE-SPIRIT-TERMINAL" or "COMPACT-GRAVEBLAST-TERMINAL")
            {
                await runner.AssertCompactNecroTerminalAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-DIRGE-NATIVE")
            {
                await runner.AssertCompactDirgeAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-DRAW-EXHAUST-NATIVE")
            {
                await runner.AssertCompactDrawExhaustAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-OSTY-CAP")
            {
                await runner.AssertCompactOstyCapAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-OSTY-NATIVE" or "COMPACT-OSTY-TURN-NATIVE")
            {
                await runner.AssertCompactOstyAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "OSTY-STATE-LIFECYCLE")
            {
                await runner.AssertOstyStateLifecycleAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-DOOM-PLAYER" or "COMPACT-DOOM-ENEMY" or "COMPACT-OSTY-DEFEAT")
            {
                await runner.AssertCompactDoomAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-NEUROSURGE-NATIVE" or "COMPACT-NEUROSURGE-CHOICES")
            {
                await runner.AssertCompactNeurosurgeAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-SEARCH-LIFECYCLE" or "COMPACT-NEUROSURGE-SEARCH" or "COMPACT-OSTY-SEARCH" or "COMPACT-OSTY-TURN-SEARCH" or "COMPACT-DRAW-EXHAUST-SEARCH" or "COMPACT-DIRGE-SEARCH" or "COMPACT-NECRO-CARDS-SEARCH" or "COMPACT-COST-POWERS-SEARCH" or "COMPACT-HANG-SEARCH" or "COMPACT-KEYWORDS-SEARCH" or "COMPACT-CARD-HOOKS-SEARCH" or "COMPACT-LETHALITY-SEARCH" or "COMPACT-PANACHE-SEARCH" or "COMPACT-SHARED-FATE-SEARCH" or "COMPACT-PAGESTORM-SEARCH")
            {
                _ = ApplySettingsOverrides();
                await runner.AssertCompactSearchLifecycleAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-SEARCH-BACKEND")
            {
                _ = ApplySettingsOverrides();
                await runner.AssertCompactSearchBackendAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-FULL-ROUTE-NATIVE")
            {
                _ = ApplySettingsOverrides();
                await runner.AssertCompactFullRouteAsync(combatState, player);
                return Observation(combatEnded: !CombatManager.Instance.IsInProgress);
            }
            if (request.ScenarioId == "COMPACT-POWER-PHASES-NATIVE")
            {
                await runner.AssertCompactPowerPhasesAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "POWER-DURATION-APPLICATION-NATIVE")
            {
                await runner.AssertPowerDurationApplicationAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "POWER-DURATION-KEYS-NATIVE")
            {
                await runner.AssertPowerDurationKeysAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-ROUND-NATIVE")
            {
                await runner.AssertCompactRoundsAsync(combatState, player);
                return Observation(combatEnded: !CombatManager.Instance.IsInProgress);
            }
            if (request.ScenarioId == "COMPACT-MONSTER-AI-NATIVE")
            {
                await runner.AssertCompactMonsterCommandsAsync(combatState, player, advanceAi: true);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-MONSTER-COMMANDS-NATIVE")
            {
                await runner.AssertCompactMonsterCommandsAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-HAND-END-NATIVE")
            {
                await runner.AssertCompactHandEndAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PLAYER-DEATH-POWERS-NATIVE")
            {
                await runner.AssertPlayerDeathPowersAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-FULL-ROOT-NATIVE")
            {
                await runner.AssertCompactFullRootAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-ARTIFACT-NATIVE")
            {
                await runner.AssertCompactArtifactAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-RANDOM-COSTS-NATIVE")
            {
                await runner.AssertCompactRandomCostsAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-GENERATED-CARDS-NATIVE" or "COMPACT-INKY-CARDS-NATIVE")
            {
                await runner.AssertCompactGeneratedCardsAsync(combatState, player, request.ScenarioId == "COMPACT-INKY-CARDS-NATIVE");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-TEMPORARY-STRENGTH-NATIVE")
            {
                await runner.AssertTemporaryStrengthAsync(combatState, player, capped: false, compact: true);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "TEMPORARY-STRENGTH-ORDER-NATIVE" or "TEMPORARY-STRENGTH-CAP-NATIVE")
            {
                await runner.AssertTemporaryStrengthAsync(combatState, player, request.ScenarioId == "TEMPORARY-STRENGTH-CAP-NATIVE");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-DEFERRED-POWERS-NATIVE")
            {
                await runner.AssertDeferredBlockReturnAsync(combatState, player, compact: true);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DEFERRED-BLOCK-RETURN-NATIVE")
            {
                await runner.AssertDeferredBlockReturnAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-POWER-EXPRESSIONS-NATIVE")
            {
                await runner.AssertCompactPoisonTriggerAsync(combatState, player, powerExpressions: true);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-POWER-REACQUIRE-NATIVE")
            {
                await runner.AssertCompactPoisonTriggerAsync(combatState, player, nonDefaultLifetime: true);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-DISCARD-DRAW-NATIVE")
            {
                await runner.AssertCompactDiscardDrawAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-POISON-TRIGGER-NATIVE")
            {
                await runner.AssertCompactPoisonTriggerAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-DRAW-RETURN-NATIVE")
            {
                await runner.AssertCompactDrawReturnAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-CONDITIONAL-POWERS-NATIVE")
            {
                await runner.AssertCompactConditionalPowersAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-CARD-LIFECYCLE-NATIVE")
            {
                await runner.AssertCompactCardLifecycleAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-POWER-BOUNDARIES-NATIVE")
            {
                await runner.AssertCompactPowerBoundariesAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-ATTACKS-NATIVE" or "COMPACT-POWERS-NATIVE")
            {
                await runner.AssertCompactAttacksAsync(combatState, player, request.ScenarioId == "COMPACT-POWERS-NATIVE");
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "COMPACT-CREATURE-VALUES-NATIVE")
            {
                await runner.AssertCreatureValuesAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMPACT-KERNEL-NATIVE")
            {
                await runner.AssertCompactKernelAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "COMPACT-SHUFFLE-POWER-NATIVE" or "COMPACT-SHUFFLE-POWER-VALUES-NATIVE" or "COMPACT-EFFECT-PROGRAM-NATIVE")
            {
                await runner.AssertCompactShufflePowerAsync(combatState, player, request.ScenarioId == "COMPACT-EFFECT-PROGRAM-NATIVE");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "P5-ROUND-PREFIX-NATIVE-TOOLS" or "P5-ROUND-PREFIX-NATIVE-MAYHEM"
                or "P5-ROUND-PREFIX-PARALLEL")
            {
                await runner.AssertRoundPrefixNativeAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId.StartsWith("TURN-SETUP-UI-", StringComparison.Ordinal))
                return Observation(combatEnded: false);
            if (request.ScenarioId == "GROWTH-ANCIENT-POLICY")
            {
                await runner.AssertAncientGrowthPolicyAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "UI-COMPACT-QOL")
            {
                await SolverOverlay.ExerciseCompactQolForTesting(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PROCESS-DIAGNOSTICS")
            {
                await runner.AssertProcessDiagnosticsAsync();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "NODE-POOL-LIFETIME")
            {
                runner.AssertNodePoolLifetime();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DYNAMIC-VAR-METADATA")
            {
                runner.AssertDynamicVarMetadata();
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SINGLE-SEARCH-PROFILE")
            {
                await runner.AssertSingleSearchProfileAsync(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "THEFT-RECOVERY-POLICY")
            {
                await AssertTheftRecoveryPolicyAsync(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SEARCH-HP-TARGET-STOP")
            {
                await runner.AssertHpTargetStopAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "NATIVE-HAND-CHOICE-REPLAY")
            {
                await runner.AssertNativeHandChoiceReplayAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "UI-PRIORITY-FEEDBACK")
            {
                await SolverOverlay.ExercisePriorityUiForTesting(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "NORMALITY-AUTOPLAY" or "NORMALITY-AUTOPLAY-REPLAY")
            {
                await runner.AssertNormalityAutoPlayAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "UI-LOCALIZATION")
            {
                RunStatistics.Start(MegaCrit.Sts2.Core.Nodes.NGame.Instance!);
                if (MegaCrit.Sts2.Core.Nodes.NGame.Instance!.GetNodeOrNull("CombatSolverRunStatistics") != null)
                    throw new InvalidOperationException("Headless statistics must remain inactive.");
                await runner.AssertUiLocalizationAsync(combatState);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMBAT-DIAGNOSTIC-LOG")
            {
                await runner.AssertCombatDiagnosticLogAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "REPORT-V2-CONTRACT")
            {
                await runner.AssertBugReportUploadBoundariesAsync();
                string reportId = Guid.NewGuid().ToString("N");
                string archivePath = await CombatBugReportExporter.ExportCurrentAsync(
                    playerDescription: "结构化问题包验证", submissionId: reportId);
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                    AssertBugReportArchive(archive, "current", "solver_only");
                string metadata = CombatBugReportUploader.ReadMetadata(archivePath, reportId, "结构化问题包验证");
                using var document = System.Text.Json.JsonDocument.Parse(metadata);
                var combat = document.RootElement.GetProperty("combat");
                if (combat.GetProperty("encounterId").GetString() != combatState.Encounter!.Id.Entry
                    || combat.GetProperty("characterId").GetString() != player.Character.Id.Entry
                    || combat.GetProperty("monsters").GetArrayLength() == 0
                    || document.RootElement.GetProperty("hpLoss").ValueKind != System.Text.Json.JsonValueKind.Null)
                    throw new InvalidDataException("结构化问题包身份或未知战损不正确。");
                foreach (int after in new[] { 3, 10, 14 })
                {
                    using var compared = System.Text.Json.JsonDocument.Parse(CombatBugReportMetadata.Serialize(
                        reportId, string.Empty, null, new CombatBugReportClassificationSnapshot(0, 0, 0, 0, 0, []),
                        new ManualProjectionComparison(1, 2, 10, after, "test")));
                    if (compared.RootElement.GetProperty("hpLoss").GetProperty("reduction").GetInt32() != 10 - after)
                        throw new InvalidDataException("战损下降值的符号不正确。");
                }
                runner._completedChecks.Add($"ReportV2UploadAndArchive:{archivePath}");
                CombatBugReportUploadReceipt receipt = await CombatBugReportUploader.UploadAsync(
                    archivePath, "结构化问题包验证", string.Empty, reportId);
                if (receipt.ReportId != reportId) throw new InvalidDataException("V2 服务端未确认问题包身份。");
                runner._completedChecks.Add($"ReportV2ProductionTlsUpload:{reportId}");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SUMMON-DEATH-POWER-ORDER")
            {
                await runner.AssertSummonDeathPowerOrderAsync(combatState, player);
                runner._completedChecks.Add("SummonDeathPowerOrderNativeFork");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "TURN-START-DAMAGE-SPITE")
            {
                await runner.AssertTurnStartDamageSpiteAsync(combatState, player);
                runner._completedChecks.Add("TurnStartDamageSpiteNativeFork");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "TEAR-ASUNDER-DAMAGE-HISTORY")
            {
                await runner.AssertTurnStartDamageSpiteAsync(combatState, player, "TEAR_ASUNDER");
                runner._completedChecks.Add("TearAsunderDamageHistoryNativeFork");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "LIVING-FOG-SUMMON-INTENT")
            {
                await runner.AssertLivingFogSummonIntentAsync(combatState, player);
                runner._completedChecks.Add("LivingFogSummonIntentAndExplosionNativeFork");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "MODEL-STATE-INTEGRATION")
            {
                await runner.AssertModelStateIntegrationAsync(combatState, player);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "COMBAT-TIMING-LIFETIME")
            {
                await runner.AssertCombatTimingLifetimeAsync(combatState, player);
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "PERFORMANCE-RECORDING-LIFETIME")
            {
                PerformanceRecording.VerifyHostReattachmentForTesting();
                await Task.Delay(TimeSpan.FromSeconds(15));
                runner._completedChecks.Add("PerformanceObserverReattachmentKeepsProcessRecorder");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "ONLINE-PRESENCE-CONTRACT")
            {
                if (!OnlinePresence.IsHeadless()) throw new InvalidOperationException("Presence fixture requires headless isolation.");
                if (!new SolverSettingsData().OnlineStatisticsEnabled
                    || SolverSettings.RoundTripForTesting(new SolverSettingsData { OnlineStatisticsEnabled = false }).OnlineStatisticsEnabled)
                    throw new InvalidOperationException("Presence opt-out did not persist.");
                OnlinePresencePayload payload = OnlinePresence.Capture("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "test");
                if (payload.Floor != combatState.RunState.TotalFloor || payload.Character.Length == 0 || payload.Encounter.Length == 0 || payload.HpLoss != null)
                    throw new InvalidOperationException("Presence scalar snapshot differs from the current combat.");
                OnlinePresencePayload complete = payload with { HpLoss = 0, BattleUpdatedAt = 123 };
                OnlinePresencePayload idle = payload with { Character = "", Floor = null, Encounter = "", InCombat = false, InRun = false };
                OnlinePresencePayload cached = OnlinePresence.RetainLatestBattle(idle, complete);
                if (!payload.InRun || cached.InRun || cached.HpLoss != 0 || cached.Encounter != complete.Encounter || cached.Floor != complete.Floor || cached.InCombat || cached.BattleUpdatedAt != 123)
                    throw new InvalidOperationException("Presence lost the completed battle while idle.");
                OnlinePresencePayload next = payload with { Character = "next", Floor = payload.Floor + 1, Encounter = "next" };
                if (OnlinePresence.RetainLatestBattle(next, cached) != cached with { InCombat = true, InRun = true }
                    || OnlinePresence.RetainLatestBattle(next with { HpLoss = 3 }, cached) != next with { HpLoss = 3 }
                    || OnlinePresence.RetainLatestBattle(next, null).Encounter.Length != 0)
                    throw new InvalidOperationException("Presence mixed battles or fabricated an initial result.");
                await OnlinePresence.VerifyTransportForTestingAsync();
                runner._completedChecks.Add("PresenceDefaultOptOutSnapshotTlsAndInvalidPinRejection");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PR60-65-CONTRACT")
            {
                AssertVictoryWaitsForStockRespawn(combatState, player);
                AssertThirdPartyBasicCardRemoval(player);
                runner._completedChecks.Add("StockRespawnVictoryAndThirdPartyRemoval");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "LONG-TERM-RESOURCE-BEAM-CAP")
            {
                AssertLongTermResourceBeamCap(combatState, player);
                runner._completedChecks.Add("LongTermResourceBeamCap");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PR57-58-STATE-CONTRACT")
            {
                AssertVitalSparkKeepsStackedTaintedAmount(combatState, player);
                AssertPowerHiddenStateRegistration(combatState, player);
                runner._completedChecks.Add("VitalSparkStackingAndPowerHiddenStateRegistration");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "RAT-SUMMON-NEXT-INTENT")
            {
                runner.SetStage("rat_summon_next_intent");
                await runner.AssertRatSummonNextIntentAsync(combatState, player);
                runner._completedChecks.Add("RatSummonNextIntent");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "FUNERARY-MASK-BEFORE-DRAW")
            {
                runner.SetStage("funerary_mask_before_draw");
                await runner.AssertFuneraryMaskBeforeDrawAsync(combatState, player);
                runner._completedChecks.Add("FuneraryMaskBeforeDraw");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "CARD-ENERGY-GAIN-COMMAND")
            {
                runner.SetStage("card_energy_gain_command");
                await runner.AssertCardEnergyGainCommandAsync(combatState, player);
                runner._completedChecks.Add("CardEnergyGainCommand");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "MELANCHOLY-OSTY-DEATH")
            {
                runner.SetStage("melancholy_osty_death");
                await runner.AssertMelancholyOstyDeathAsync(combatState, player);
                runner._completedChecks.Add("MelancholyOstyDeath");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "QUEEN-INFERNO-MINION-DEATH" or "QUEEN-INFERNO-TERMINAL")
            {
                runner.SetStage("queen_inferno_minion_death");
                await runner.AssertQueenInfernoMinionDeathAsync(combatState, player);
                runner._completedChecks.Add("QueenInfernoMinionDeath");
                return Observation(combatEnded: !CombatManager.Instance.IsInProgress);
            }
            if (request.ScenarioId == "GAMBLING-CHIP-SLY-ORDER")
            {
                runner.SetStage("gambling_chip_sly_order");
                await runner.AssertGamblingChipSlyOrderAsync(combatState, player);
                runner._completedChecks.Add("GamblingChipSlyOrder");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "GALVANIC-GENERATED-POWER")
            {
                runner.SetStage("galvanic_generated_power");
                await runner.AssertGalvanicGeneratedPowerAsync(combatState, player);
                runner._completedChecks.Add("GalvanicGeneratedPower");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SLOW-TURN-RESET-FORK")
            {
                await runner.AssertSlowTurnResetForkAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "HELLRAISER-TURN-START-HISTORY")
            {
                await runner.AssertHellraiserTurnStartHistoryAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "SUMMONED-ALLY-POWER-ORDER")
            {
                await runner.AssertSummonedAllyPowerOrderAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "MIXED-POWER-ACQUISITION-ORDER" or "REMOVED-POWER-REAPPLICATION")
            {
                await runner.AssertReportPowerLifecycleAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "ENERGY-RESET-POWER-ORDER" or "ENERGY-RESET-POWER-ORDER-REVERSE"
                or "ENERGY-RESET-POWER-ORDER-REAPPLY" or "ENERGY-RESET-POWER-ORDER-OVERFLOW")
            {
                runner.SetStage("energy_reset_power_order");
                await runner.AssertEnergyResetPowerOrderAsync(combatState, player);
                runner._completedChecks.Add("EnergyResetPowerOrder");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "REPLAY-START-HISTORY" or "REPLAY-START-HISTORY-ECHO")
            {
                runner.SetStage("replay_start_history");
                await runner.AssertReplayStartHistoryAsync(combatState, player);
                runner._completedChecks.Add("ReplayStartHistory");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "TENDER-NESTED-SLY" or "TENDER-DISCARD-ALL-SLY")
            {
                await runner.AssertTenderNestedAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "FOREGONE-IMPLICIT-ORDER")
            {
                await runner.AssertForegoneSelectionAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "HISTORY-COURSE-EMPTY-TURN")
            {
                await runner.AssertHistoryCourseEmptyTurnAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId.StartsWith("REPORT-ROUND-", StringComparison.Ordinal))
            {
                await runner.AssertReportRoundAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId.StartsWith("REPORT-CARDS-", StringComparison.Ordinal))
            {
                await runner.AssertReportCardSequenceAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "TEST-SUBJECT-ORIGINAL-REPORT")
            {
                await runner.AssertTestSubjectReportAsync(combatState, player);
                runner._completedChecks.Add("TestSubjectOriginalReportTurn2");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "REPLAY-BOUNDARY-CONTRACT")
            {
                await runner.AssertReplayBoundaryContractAsync(player);
                runner._completedChecks.Add("ReplayLegacyHistoryAndBoundaryFailure");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "CLONE-EVENT-ISOLATION")
            {
                runner.AssertCloneEventIsolation(player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "NIGHTMARE-SELECTION-SNAPSHOT")
            {
                await runner.AssertNightmareSnapshotAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "MURDER-ROOT-HISTORY")
            {
                await runner.AssertMurderRootHistoryAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId is "STOCK-RESPAWN-HP" or "STOCK-THORNS-RESPAWN-HP" or "STOCK-REPORT-RESPAWN-HP")
            {
                await runner.AssertStockRespawnAsync(combatState, player);
                runner._completedChecks.Add(request.ScenarioId);
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "DEATH-EFFECTS-ONCE")
            {
                runner.SetStage("death_effects_once");
                await runner.AssertDeathEffectsOnceAsync(combatState, player);
                runner._completedChecks.Add("DeathEffectsOnce");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "FEED-THORNS-TERMINAL-DIFFERENTIAL")
            {
                runner.SetStage("feed_thorns_terminal_differential");
                await runner.AssertFeedThornsTerminalAsync(combatState, player);
                runner._completedChecks.Add("FeedThornsTerminalDifferential");
                return Observation(combatEnded: true);
            }
            if (request.ScenarioId == "ROOT-CAPTURE-ACTION-BARRIER")
            {
                _ = ApplySettingsOverrides();
                runner.SetStage("root_capture_action_barrier");
                await runner.AssertSearchWaitsForNativeActionAsync(combatState, player);
                runner._completedChecks.Add("RootCaptureActionBarrier");
                return Observation(combatEnded: false);
            }
            if (request.ScenarioId == "PR15-POTION-VALUE-TIERS")
                runner.AssertPotionValueTiers(combatState);
            if (request.ScenarioId == "PR18-FOREIGN-ONPLAY-BOUNDARY")
                runner.AssertForeignCardPatchBoundary(combatState);

            if (request.ScenarioId is "ROUTE-CACHE-RECORD-V0111" or "ROUTE-CACHE-RESTORE-V0111")
            {
                _ = ApplySettingsOverrides();
                runner.SetStage("solved_route_cache");
                bool restoreOnly = request.ScenarioId == "ROUTE-CACHE-RESTORE-V0111";
                await runner.RunSolvedRouteCacheAsync(combatState, player, restoreOnly);
                return Observation(combatEnded: restoreOnly);
            }

            if (request.ScenarioId.Equals("GC-CHECKPOINT-BACKGROUND-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("后台 GC 生命周期夹具不能混入战斗差分或正式搜索。");
                runner.SetStage("gc_checkpoint_background");
                await runner.RunGcCheckpointBackgroundFixtureAsync();
                return Observation(combatEnded: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-ROUTE-NATIVE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("Soul 原版对照不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_soul_route_native");
                int finishedTurn = await runner.RunKnownSoulRouteNativeAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知路径诊断不能混入其他差分或增量搜索请求。");
                _ = ApplySettingsOverrides();
                runner.SetStage("known_soul_path_trace_prepare");
                int finishedTurn = await runner.RunKnownSoulPathTraceAsync(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-VARIANT-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("Soul 替代路线观察不能混入其他差分或增量搜索请求。");
                _ = ApplySettingsOverrides();
                runner.SetStage("known_soul_variant_path_trace_prepare");
                int finishedTurn = await runner.RunKnownSoulVariantPathTraceAsync(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-RETAINED-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("Soul 实际保留别名观察不能混入其他差分或增量搜索请求。");
                _ = ApplySettingsOverrides();
                runner.SetStage("known_soul_retained_path_trace_prepare");
                int finishedTurn = await runner.RunKnownSoulVariantPathTraceAsync(
                    combatState, player, proveRetainedAlias: true);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-CUSTOM-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知路径诊断不能混入其他差分或增量搜索请求。");
                _ = ApplySettingsOverrides();
                runner.SetStage("known_custom_path_trace_prepare");
                int finishedTurn = await runner.RunKnownCustomPathTraceAsync(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-GENERATION-CONTEXT-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("生成上下文回放不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_soul_generation_context");
                int finishedTurn = runner.RunKnownSoulGenerationContext(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-GENERATION-SUFFIX-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("生成上下文完整后缀回放不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_soul_generation_suffix");
                int finishedTurn = runner.RunKnownSoulGenerationContext(combatState, player, fullKnownSuffix: true);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("落选恢复合同不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_custom_deferred_frontier");
                int finishedTurn = runner.RunKnownCustomDeferredFrontier(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals(RelicStatTerminalScenarioId, StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("遗物属性终局夹具不能混入其他差分或正式搜索请求。");
                runner.SetStage("relic_stat_terminal");
                int finishedTurn = await runner.RunRelicStatTerminalAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-SOUL-ROUTE-REPLAY-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知 Soul 路线重建不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_soul_route_replay");
                int finishedTurn = runner.RunKnownSoulRouteReplay(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知外骨骼虫路线重建不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_exoskeletons_route_replay");
                int finishedTurn = runner.RunKnownExoskeletonsRouteReplay(combatState, player);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-EXOSKELETONS-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase)
                || request.ScenarioId.Equals("KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("外骨骼虫路径观察不能混入其他差分或增量搜索请求。");
                _ = ApplySettingsOverrides();
                runner.SetStage("known_exoskeletons_path_trace");
                int retentionStep = request.ScenarioId.Equals(
                    "KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111", StringComparison.OrdinalIgnoreCase) ? 5 : 4;
                int finishedTurn = await runner.RunKnownExoskeletonsPathTraceAsync(
                    combatState, player, requiredRetentionStep: retentionStep);
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("外骨骼虫原版对照不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_exoskeletons_route_native");
                int finishedTurn = await runner.RunKnownExoskeletonsRouteNativeAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-CUSTOM-ROUTE-NATIVE-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知路线原版对照不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_custom_route_native");
                int finishedTurn = await runner.RunKnownCustomRouteNativeAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals("KNOWN-CUSTOM-ROUTE-REPLAY-V0111", StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("已知路线重建夹具不能混入其他差分或正式搜索请求。");
                runner.SetStage("known_custom_route_replay");
                int finishedTurn = runner.RunKnownCustomRouteReplay(combatState, player);
                // Only shadow replay was performed: the native combat remains untouched.
                return new ExecutionOutcome(false, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals(MercuryReattachScenarioId, StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("沙漏复活边界夹具不能混入其他差分或正式搜索请求。");
                runner.SetStage("mercury_reattach_differential");
                int finishedTurn = await runner.RunMercuryReattachDifferentialAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals(ForcedTurnTerminalScenarioId, StringComparison.OrdinalIgnoreCase)
                || request.ScenarioId.Equals(PotionForcedTurnTerminalScenarioId, StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("强制结束终局夹具不能混入其他差分或正式搜索请求。");
                runner.SetStage("forced_turn_terminal_differential");
                int finishedTurn = await runner.RunForcedTurnTerminalDifferentialAsync(combatState, player);
                return new ExecutionOutcome(true, finishedTurn, expectedCardPlayed, expectedPotionUsed,
                    expectedPlayerPowerObserved, InitialSearchHeld: false);
            }

            if (request.ScenarioId.Equals(ReturnToHandOrderScenarioId, StringComparison.OrdinalIgnoreCase))
            {
                if (scenario.OrbChecks.Count > 0 || scenario.PotionChecks.Count > 0
                    || scenario.MonsterMoveChecks.Count > 0 || request.VerifyIncrementalSearch)
                    throw new InvalidOperationException("回手顺序专用夹具不能混入其他差分或搜索请求。");
                runner.SetStage("return_to_hand_order_differential");
                await runner.RunReturnToHandOrderDifferentialAsync(combatState, player);
                runner._completedChecks.Add("ReturnToHandOrder:ActualContinuousForkRoot");
                return Observation(combatEnded: false);
            }

            if (request.VerifyTurnSetupSceneExitCancellation
                || request.VerifyTurnSetupControlsDuringInitialSearch)
                return Observation(combatEnded: false);

            if (scenario.OrbChecks.Count > 0)
            {
                for (int index = 0; index < scenario.OrbChecks.Count; index++)
                {
                    UnattendedOrbCheck orbCheck = scenario.OrbChecks[index];
                    runner.SetStage($"orb_differential_{index + 1}_of_{scenario.OrbChecks.Count}");
                    await runner.RunOrbDifferentialAsync(combatState, player, orbCheck);
                    runner._completedChecks.Add($"Orb:{orbCheck.OrbId}");
                }
                return Observation(combatEnded: false);
            }
            if (scenario.PotionChecks.Count > 0)
            {
                for (int index = 0; index < scenario.PotionChecks.Count; index++)
                {
                    UnattendedPotionCheck potionCheck = scenario.PotionChecks[index];
                    runner.SetStage($"potion_differential_{index + 1}_of_{scenario.PotionChecks.Count}");
                    await runner.RunPotionDifferentialAsync(combatState, player, potionCheck);
                    runner._completedChecks.Add($"Potion:{potionCheck.PotionId}");
                }
                return Observation(combatEnded: false);
            }
            if (scenario.MonsterMoveChecks.Count > 0)
            {
                for (int index = 0; index < scenario.MonsterMoveChecks.Count; index++)
                {
                    UnattendedMonsterMoveCheck check = scenario.MonsterMoveChecks[index];
                    foreach (string monsterId in request.AdditionalMonsterIds
                                 .Where(static id => !string.IsNullOrWhiteSpace(id))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        await EnsureMonsterExistsAsync(combatState, monsterId, null);
                    }
                    if (!string.IsNullOrWhiteSpace(check.MonsterId))
                    {
                        int existingCount = combatState.Enemies.Count(candidate =>
                            candidate.IsAlive
                            && candidate.Monster != null
                            && ModelMatches(candidate.Monster, check.MonsterId));
                        while (existingCount <= check.MonsterOccurrence)
                        {
                            await AddMonsterForTestAsync(
                                combatState,
                                check.MonsterId,
                                check.SpawnInitialMoveId);
                            existingCount++;
                        }
                    }
                    if (check.ExpectedSearchBoundary is { } expectedBoundary)
                    {
                        runner.SetStage(
                            $"monster_move_search_boundary_{index + 1}_of_{scenario.MonsterMoveChecks.Count}");
                        await runner.RunMonsterMoveSearchBoundaryAsync(combatState, check, expectedBoundary);
                    }
                    else
                    {
                        runner.SetStage(
                            $"monster_move_differential_{index + 1}_of_{scenario.MonsterMoveChecks.Count}");
                        await runner.RunMonsterMoveDifferentialAsync(combatState, player, check);
                    }
                    runner._completedChecks.Add($"{check.MonsterId}:{check.MoveId}");
                    if (index + 1 < scenario.MonsterMoveChecks.Count)
                    {
                        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                }
                return Observation(combatEnded: false);
            }
            if (request.StopAfterCombatRootSnapshotAssertion)
                return Observation(combatEnded: runner.HasNativeRecording && !CombatManager.Instance.IsInProgress);
            if (request.VerifyTurnSetupManualRecalculate)
                return Observation(combatEnded: false);
            if (request.StopAfterInitialSetupAssertion)
            {
                runner.SetStage("assert_initial_setup_result");
                await runner.AssertInitialSolverResultAsync(startedTurn);
                runner._completedChecks.Add("InitialSetupChoices");
                return Observation(combatEnded: false);
            }

            runner.SetStage("full_auto");
            FastModeType? fastModeBeforeDeployment = ApplySettingsOverrides();
            if (SolverController.LastTurnSetupResultForTesting == null
                && !request.PreserveNativeCombatStateForTest && !runner.HasNativeRecording)
                SolverController.BeginCombat(combatState);
            if (request.TheftPolicyForTest is { } theftPolicy)
                SolverController.SetTheftPolicyForTesting(combatState, theftPolicy);
            SolverController.SetStopFullAutoOnCombatEnd(false, persist: false);
            SolverController.SetStopFullAutoOnDeathTurn(
                request.ExpectedFullAutoPausedAtDeathTurn
                    || request.ExpectedFullAutoPausedAtLiveRisk,
                persist: false);
            SolverController.SetStopFullAutoOnWorseRecalculation(
                request.EnableStopOnWorseRecalculationForTest
                    || request.ExpectedFullAutoPausedAfterWorseRecalculation
                    || request.ExpectedFullAutoPausedAtLiveRisk,
                persist: false);
            runner._protocolHost.EnableAutomaticTurnSearch();
            if (request.HoldAfterInitialSearch
                || request.ManualEndTurnAfterInitialSearch
                || request.SingleStepAfterInitialSearch
                || request.StopAfterInitialSolverResultAssertion)
                SolverController.RequestSearch(runner._host, combatState, SearchReason.Manual);
            else
                SolverController.SetFullAuto(runner._host, combatState, enabled: true);

            if (runner.HasInitialSolverExpectation()
                || request.StopAfterInitialSolverResultAssertion)
                await runner.AssertInitialSolverResultAsync(startedTurn);

            if (request.StopAfterInitialSolverResultAssertion)
                return Observation(combatEnded: false);

            if (request.HoldAfterInitialSearch)
            {
                if (!runner.HasInitialSolverExpectation())
                    await runner.AssertInitialSolverResultAsync(startedTurn);
                await Task.Delay(2000);
                await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_hold",
                    forceCollection: true);
                return Observation(combatEnded: false, initialSearchHeld: true);
            }

            if (request.SingleStepAfterInitialSearch)
            {
                if (!runner.HasInitialSolverExpectation())
                    await runner.AssertInitialSolverResultAsync(startedTurn);
                runner.SetStage("single_step_next_turn_choice");
                SolverController.RequestDeploy(runner._host, combatState);
                int expectedTurn = startedTurn + 1;
                while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding)
                {
                    bool waitingForPlayer = player.PlayerCombatState is
                        {
                            TurnNumber: var turn,
                            Phase: PlayerTurnPhase.Start,
                        }
                        && turn == expectedTurn
                        && NPlayerHand.Instance?.IsInCardSelection == true;
                    if (waitingForPlayer)
                    {
                        if (SolverController.FullAutoEnabled)
                            throw new InvalidOperationException("单步执行进入下一回合选牌页时仍处于全自动模式。");
                        bool solverSelected = NativeChoiceRuntime.TraceSnapshotForTesting.Any(trace =>
                            trace.Owner == $"turn_setup:{expectedTurn}"
                            && trace.Stage == "Selected");
                        if (solverSelected)
                            throw new InvalidOperationException("单步执行越过边界并替玩家完成了下一回合选牌。");
                        if (SolverOverlay.CurrentSnapshotTurnForTesting != expectedTurn)
                        {
                            throw new InvalidOperationException(
                                $"下一回合选牌页已显示，但路线 UI 仍停在第 " +
                                $"{SolverOverlay.CurrentSnapshotTurnForTesting?.ToString() ?? "-"} 回合；" +
                                $"预期第 {expectedTurn} 回合。");
                        }
                        runner._completedChecks.Add(
                            $"SingleStepBoundary:Turn={expectedTurn}:Surface=Hand:Selected=0:UiTurn={expectedTurn}");
                        Entry.Logger.Info(
                            $"[CombatSolver/Test] SINGLE_STEP_BOUNDARY turn={expectedTurn} " +
                            $"surface=Hand waiting_for_player=true solver_selected=false ui_turn={expectedTurn}");
                        if (request.SingleStepResumeModeForTest is not { } resumeMode)
                            return Observation(combatEnded: false);

                        long previousDeploymentStartedAt =
                            SolverController.LastDeployedActionStartedAtMillisecondsForTesting;
                        if (resumeMode == SingleStepResumeMode.ExecuteCurrentTurn)
                            SolverController.RequestDeploy(runner._host, combatState);
                        else
                            SolverController.SetFullAuto(runner._host, combatState, enabled: true);

                        while (SolverController.LastDeployedActionStartedAtMillisecondsForTesting
                               <= previousDeploymentStartedAt)
                        {
                            runner.EnsureWithinDeadline();
                            await runner.NextFrameAsync();
                        }

                        NativeChoiceTrace selectedTrace = NativeChoiceRuntime.TraceSnapshotForTesting
                            .Where(trace => trace.Owner == $"turn_setup:{expectedTurn}"
                                && trace.Stage == "Selected")
                            .OrderByDescending(trace => trace.Order)
                            .FirstOrDefault()
                            ?? throw new InvalidOperationException(
                                "接管单步回合开始页面后没有完成计划选牌。");
                        long deploymentStartedAt =
                            SolverController.LastDeployedActionStartedAtMillisecondsForTesting;
                        long actualDelay = deploymentStartedAt - selectedTrace.OccurredAtMilliseconds;
                        if (request.ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast
                            is { } minimumDelay
                            && actualDelay < minimumDelay)
                        {
                            throw new InvalidOperationException(
                                $"回合开始选牌到下一张牌只间隔 {actualDelay} ms，低于预期 {minimumDelay} ms。");
                        }
                        if (SolverController.LastCompletedResultForTesting is not
                            {
                                WasReused: true,
                                StartTurnNumber: var reusedTurn,
                            }
                            || reusedTurn != expectedTurn)
                        {
                            throw new InvalidOperationException(
                                $"接管第 {expectedTurn} 回合选牌后没有复用既有路线。");
                        }
                        if (SolverController.UnexpectedReplanCountForTesting != 0)
                            throw new InvalidOperationException("接管单步选牌页后发生了计划外重算。");
                        runner._completedChecks.Add(
                            $"SingleStepTakeover:Mode={resumeMode}:Turn={expectedTurn}:" +
                            $"DelayMs={actualDelay}:Reused=true:UnexpectedReplans=0");
                        return Observation(combatEnded: false);
                    }
                    runner.EnsureWithinDeadline();
                    await runner.NextFrameAsync();
                }
                throw new InvalidOperationException("单步执行在下一回合原生选牌页出现前结束了战斗。");
            }

            if (request.ManualEndTurnAfterInitialSearch)
            {
                if (!runner.HasInitialSolverExpectation())
                    await runner.AssertInitialSolverResultAsync(startedTurn);
                runner.SetStage("manual_end_turn");
                int manualTurn = player.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException("手操偏离测试没有玩家回合状态。");
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, manualTurn));
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (request.EnableFullAutoAfterManualEndTurn)
                {
                    CombatState resumedState = await runner.WaitForPlayableCombatAsync();
                    SolverController.SetFullAuto(runner._host, resumedState, enabled: true);
                }
            }

            runner.SetStage("wait_combat_end");
            bool expectedReuseObserved = !request.ExpectedReusedTurn.HasValue;
            bool ObserveExpectedReuse()
            {
                SolverResult? latestResult = SolverController.LastCompletedResultForTesting;
                int? observedReusedTurn = SolverController.LastReusedTurnForTesting
                    ?? (latestResult?.WasReused == true
                        ? latestResult.StartTurnNumber
                        : null);
                if (observedReusedTurn != request.ExpectedReusedTurn)
                    return false;
                int observedProjectedBattleHpLost =
                    SolverController.LastReusedProjectedBattleHpLostForTesting
                    ?? latestResult?.ProjectedBattleHpLost
                    ?? throw new InvalidOperationException("复用测试没有记录整场预计战损。");
                if (request.ExpectedReusedProjectedBattleHpLost is { } expectedReusedLoss
                    && observedProjectedBattleHpLost != expectedReusedLoss)
                {
                    throw new InvalidOperationException(
                        $"第 {observedReusedTurn} 回合复用路线预计整场掉血 " +
                        $"{observedProjectedBattleHpLost}，预期为 {expectedReusedLoss}。");
                }
                return true;
            }
            bool stoppedAfterExpectedReuse = false;
            bool stoppedAfterExpectedPower = false;
            bool stoppedAfterDeathTurnPause = false;
            bool stoppedAfterWorseRecalculationPause = false;
            bool stoppedAfterLiveRiskPause = false;
            bool stoppedAfterManualDivergence = false;
            bool stoppedAfterNoGcRollover = false;
            bool stoppedAfterExpectedUnexpectedReplan = false;
            int maximumUnexpectedReplans = 0;
            while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding)
            {
                expectedCardPlayed |= runner.WasExpectedCardPlayed();
                expectedPotionUsed |= runner.WasExpectedPotionUsed();
                expectedPlayerPowerObserved |= runner.HasExpectedPlayerPower(player);
                maximumUnexpectedReplans = Math.Max(
                    maximumUnexpectedReplans,
                    SolverController.UnexpectedReplanCountForTesting);
                if (request.StopAfterExpectedUnexpectedReplan
                    && request.ExpectedUnexpectedReplansAtLeast is { } minimumUnexpectedReplans
                    && maximumUnexpectedReplans >= minimumUnexpectedReplans)
                {
                    if (request.ExpectedUnexpectedReplanWarning
                        && !SolverOverlay.UnexpectedReplanWarningVisibleForTesting)
                    {
                        throw new InvalidOperationException("已发生计划外重算，但标题右侧没有显示反馈告警。");
                    }
                    if (request.ExportBugReportAfterUnexpectedReplan)
                    {
                        string directory = ProjectSettings.GlobalizePath("user://combat-solver-test-bug-reports");
                        string archivePath = await CombatBugReportExporter.ExportCurrentAsync(directory);
                        using ZipArchive archive = ZipFile.OpenRead(archivePath);
                        AssertBugReportArchive(archive, "current", request.ExpectedBugReportControlMode);
                        runner._completedChecks.Add(
                            $"BugReportAtUnexpectedReplan:{Path.GetFileName(archivePath)}");
                    }
                    stoppedAfterExpectedUnexpectedReplan = true;
                    runner._completedChecks.Add($"UnexpectedReplanWarning:{maximumUnexpectedReplans}");
                    break;
                }
                if (!expectedReuseObserved)
                    expectedReuseObserved = ObserveExpectedReuse();
                if (request.StopAfterExpectedReuse && expectedReuseObserved)
                {
                    stoppedAfterExpectedReuse = true;
                    break;
                }
                if (request.StopAfterExpectedPlayerPower && expectedPlayerPowerObserved)
                {
                    stoppedAfterExpectedPower = true;
                    runner._completedChecks.Add($"PlayerPower:{request.ExpectedObservedPlayerPowerId}");
                    break;
                }
                if (request.ExpectedFullAutoPausedAtDeathTurn
                    && !SolverController.FullAutoEnabled
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying)
                {
                    stoppedAfterDeathTurnPause = true;
                    runner._completedChecks.Add($"FullAutoPaused:DeathTurn={startedTurn}");
                    break;
                }
                if (request.ExpectedFullAutoPausedAfterWorseRecalculation
                    && SolverController.LastFullAutoStoppedForWorseRecalculationForTesting
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying)
                {
                    stoppedAfterWorseRecalculationPause = true;
                    runner._completedChecks.Add("FullAutoPaused:WorseRecalculation");
                    break;
                }
                if (request.ExpectedFullAutoPausedAtLiveRisk
                    && SolverController.LastFullAutoStoppedAtLiveRiskForTesting
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying)
                {
                    stoppedAfterLiveRiskPause = true;
                    runner._completedChecks.Add("FullAutoPaused:LiveEndTurnRisk");
                    break;
                }
                if (request.ExpectedManualDivergencesAtLeast is { } minimumManualDivergences
                    && SolverController.ManualDivergenceCountForTesting >= minimumManualDivergences
                    && (!request.ExpectedNoGcRegionRolloversAtLeast.HasValue
                        || SolverController.NoGcRegionRolloverCountForTesting
                            >= request.ExpectedNoGcRegionRolloversAtLeast.Value))
                {
                    stoppedAfterManualDivergence = true;
                    runner._completedChecks.Add(
                        $"ManualDivergence:{SolverController.ManualDivergenceCountForTesting}");
                    if (request.ExpectedNoGcRegionRolloversAtLeast.HasValue)
                    {
                        stoppedAfterNoGcRollover = true;
                        runner._completedChecks.Add(
                            $"NoGcRegionRollovers:{SolverController.NoGcRegionRolloverCountForTesting}");
                    }
                    break;
                }
                if (request.ExpectedNoGcRegionRolloversAtLeast is { } minimumRollovers
                    && SolverController.NoGcRegionRolloverCountForTesting >= minimumRollovers
                    && (!request.ExpectedManualDivergencesAtLeast.HasValue
                        || SolverController.ManualDivergenceCountForTesting
                            >= request.ExpectedManualDivergencesAtLeast.Value))
                {
                    stoppedAfterNoGcRollover = true;
                    runner._completedChecks.Add(
                        $"NoGcRegionRollovers:{SolverController.NoGcRegionRolloverCountForTesting}");
                    if (request.ExpectedManualDivergencesAtLeast.HasValue)
                    {
                        stoppedAfterManualDivergence = true;
                        runner._completedChecks.Add(
                            $"ManualDivergence:{SolverController.ManualDivergenceCountForTesting}");
                    }
                    break;
                }
                runner.EnsureWithinDeadline();
                await runner.NextFrameAsync();
            }
            expectedCardPlayed |= runner.WasExpectedCardPlayed();
            expectedPotionUsed |= runner.WasExpectedPotionUsed();
            expectedPlayerPowerObserved |= runner.HasExpectedPlayerPower(player);
            maximumUnexpectedReplans = Math.Max(
                maximumUnexpectedReplans,
                SolverController.UnexpectedReplanCountForTesting);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (SolverController.IsDeploying)
            {
                runner.EnsureWithinDeadline();
                await runner.NextFrameAsync();
            }
            if (!expectedReuseObserved)
                expectedReuseObserved = ObserveExpectedReuse();
            if (expectedReuseObserved && request.ExpectedReusedTurn.HasValue)
                runner._completedChecks.Add($"Reuse:Turn={request.ExpectedReusedTurn}");
            if (request.AssertDeploymentSpeedRestored
                && fastModeBeforeDeployment is { } expectedFastMode)
            {
                long restoreDeadline = System.Environment.TickCount64 + 5_000;
                while (SaveManager.Instance.PrefsSave.FastMode != expectedFastMode)
                {
                    runner.EnsureWithinDeadline();
                    if (System.Environment.TickCount64 >= restoreDeadline)
                    {
                        throw new InvalidOperationException(
                            $"自动执行后游戏速度为 {SaveManager.Instance.PrefsSave.FastMode}，" +
                            $"5 秒内没有恢复为 {expectedFastMode}。");
                    }
                    await runner.NextFrameAsync();
                }
            }
            if (request.AssertDeploymentSpeedRestored && fastModeBeforeDeployment.HasValue)
                runner._completedChecks.Add($"DeploymentSpeedRestored:{fastModeBeforeDeployment}");
            if (!expectedReuseObserved)
            {
                throw new InvalidOperationException(
                    $"没有观察到第 {request.ExpectedReusedTurn} 回合复用首轮预测状态。");
            }
            if (request.ExpectedUnexpectedReplansAtMost is { } maximumReplans
                && maximumUnexpectedReplans > maximumReplans)
            {
                throw new InvalidOperationException(
                    $"战斗出现 {maximumUnexpectedReplans} 次非预期重算，超过上限 {maximumReplans}。");
            }
            if (request.ExpectedUnexpectedReplansAtMost.HasValue)
                runner._completedChecks.Add($"UnexpectedReplans:{maximumUnexpectedReplans}");
            if (request.ExpectedUnexpectedReplansAtLeast is { } minimumReplans
                && maximumUnexpectedReplans < minimumReplans)
            {
                throw new InvalidOperationException(
                    $"战斗只出现 {maximumUnexpectedReplans} 次非预期重算，低于预期下限 {minimumReplans}。");
            }
            if (request.ExpectedUnexpectedReplanWarning
                && maximumUnexpectedReplans > 0
                && !stoppedAfterExpectedUnexpectedReplan
                && !SolverOverlay.UnexpectedReplanWarningVisibleForTesting)
            {
                throw new InvalidOperationException("已发生计划外重算，但标题右侧没有显示反馈告警。");
            }

            if (!stoppedAfterExpectedReuse
                && !stoppedAfterExpectedPower
                && !stoppedAfterDeathTurnPause
                && !stoppedAfterWorseRecalculationPause
                && !stoppedAfterLiveRiskPause
                && !stoppedAfterManualDivergence
                && !stoppedAfterNoGcRollover
                && !stoppedAfterExpectedUnexpectedReplan)
            {
                if (request.ExpectedPlayerDeath)
                {
                    if (!player.Creature.IsDead)
                        throw new InvalidOperationException("预期玩家死亡，但战斗结束时玩家仍存活。");
                }
                else if (!combatState.Enemies.All(static enemy => enemy.IsDead))
                {
                    throw new InvalidOperationException("战斗结束，但仍存在未死亡敌人。");
                }
            }
            bool combatEnded = !stoppedAfterExpectedReuse
                && !stoppedAfterExpectedPower
                && !stoppedAfterDeathTurnPause
                && !stoppedAfterWorseRecalculationPause
                && !stoppedAfterLiveRiskPause
                && !stoppedAfterExpectedUnexpectedReplan;
            return Observation(combatEnded);

            ExecutionOutcome Observation(bool combatEnded, bool initialSearchHeld = false)
                => new(
                    combatEnded,
                    player.PlayerCombatState?.TurnNumber ?? startedTurn,
                    expectedCardPlayed,
                    expectedPotionUsed,
                    expectedPlayerPowerObserved,
                    initialSearchHeld);
        }

        private FastModeType? ApplySettingsOverrides()
        {
            UnattendedTestRequest request = runner._request;
            if (runner._checkpointImport == null
                && !request.PerformancePresetForTest.HasValue
                && !request.ShortMaxCardBranchesPerNodeForTest.HasValue
                && !request.DeepMaxCardBranchesPerNodeForTest.HasValue
                && !request.PotionPolicyForTest.HasValue
                && !request.EnableNoGcRegionForTest.HasValue
                && !request.NoGcRegionBudgetGigabytesForTest.HasValue
                && !request.DeploymentFastModeForTest.HasValue
                && !request.DeploymentInterActionDelaySecondsForTest.HasValue
                && !request.EnableDetailedDiagnosticLogsForTest.HasValue)
            {
                return null;
            }

            _settingsBeforeTest ??= SolverSettings.Current;
            SolverSettingsData recordedSettings = runner.ApplyRecordedCheckpointPolicy(_settingsBeforeTest);
            SolverSettingsData testSettings = request.PerformancePresetForTest is { } preset
                ? SolverSettings.ApplyPerformancePreset(recordedSettings, preset)
                : recordedSettings;
            bool hasCustomPerformanceOverride = request.ShortMaxCardBranchesPerNodeForTest.HasValue
                || request.DeepMaxCardBranchesPerNodeForTest.HasValue;
            if (request.NoGcRegionBudgetGigabytesForTest is { } noGcBudget)
            {
                testSettings = testSettings with
                {
                    NoGcRegionBudgetGigabytes = noGcBudget,
                };
            }
            if (request.ShortMaxCardBranchesPerNodeForTest is { } shortMaxCardBranches)
            {
                testSettings = testSettings with
                {
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchMaxCardBranchesPerNode = shortMaxCardBranches,
                };
            }
            if (request.DeepMaxCardBranchesPerNodeForTest is { } deepMaxCardBranches)
            {
                testSettings = testSettings with
                {
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchMaxCardBranchesPerNode = deepMaxCardBranches,
                };
            }
            SolverSettings.ApplyForTesting(testSettings with
            {
                EnableNoGcRegion = request.EnableNoGcRegionForTest
                    ?? testSettings.EnableNoGcRegion,
                DeploymentFastMode = request.DeploymentFastModeForTest
                    ?? _settingsBeforeTest.DeploymentFastMode,
                DeploymentInterActionDelaySeconds = request.DeploymentInterActionDelaySecondsForTest
                    ?? _settingsBeforeTest.DeploymentInterActionDelaySeconds,
                EnableDetailedDiagnosticLogs = request.EnableDetailedDiagnosticLogsForTest
                    ?? _settingsBeforeTest.EnableDetailedDiagnosticLogs,
                PotionPolicy = request.PotionPolicyForTest
                    ?? testSettings.PotionPolicy,
            });
            FastModeType fastModeBeforeDeployment = SaveManager.Instance.PrefsSave.FastMode;
            if (request.PerformancePresetForTest is { } expectedPreset)
            {
                runner.AssertPerformancePreset(
                    hasCustomPerformanceOverride
                        ? SolverPerformancePreset.Custom
                        : expectedPreset);
            }
            SolverSettingsSnapshot snapshot = SolverSettings.Capture();
            if (runner._writer.ReplayVerification != null)
                runner._writer.ReplayVerification["executedPolicy"] = System.Text.Json.JsonSerializer.SerializeToNode(
                    new { snapshot.PotionPolicy, SolverSettings.Current.PotionDirectives,
                        snapshot.ActTransitionBossHpStrategy, snapshot.FinalBossHpStrategy,
                        snapshot.Profile, snapshot.SearchMaxDegreeOfParallelism },
                    UnattendedTestFiles.JsonOptions);
            if (request.EnableNoGcRegionForTest is { } expectedNoGcEnabled
                && snapshot.EnableNoGcRegion != expectedNoGcEnabled)
            {
                throw new InvalidOperationException(
                    $"No-GC 开关为 {snapshot.EnableNoGcRegion}，预期 {expectedNoGcEnabled}。");
            }
            if (request.ShortMaxCardBranchesPerNodeForTest is { } expectedShortBranches
                && snapshot.Profile.MaxCardBranchesPerNode != expectedShortBranches)
            {
                throw new InvalidOperationException(
                    $"短搜单节点出牌分支为 {snapshot.Profile.MaxCardBranchesPerNode}，" +
                    $"预期 {expectedShortBranches}。");
            }
            if (request.DeepMaxCardBranchesPerNodeForTest is { } expectedDeepBranches
                && snapshot.Profile.MaxCardBranchesPerNode != expectedDeepBranches)
            {
                throw new InvalidOperationException(
                    $"深搜单节点出牌分支为 {snapshot.Profile.MaxCardBranchesPerNode}，" +
                    $"预期 {expectedDeepBranches}。");
            }
            if (request.NoGcRegionBudgetGigabytesForTest is { } expectedNoGcGigabytes)
            {
                long expectedNoGcBytes = checked((long)Math.Round(
                    expectedNoGcGigabytes * 1_000_000_000d,
                    MidpointRounding.AwayFromZero));
                if (snapshot.NoGcRegionBudgetBytes != expectedNoGcBytes)
                {
                    throw new InvalidOperationException(
                        $"No-GC 预算为 {snapshot.NoGcRegionBudgetBytes}，预期 {expectedNoGcBytes}。");
                }
            }
            return fastModeBeforeDeployment;
        }
    }
}
