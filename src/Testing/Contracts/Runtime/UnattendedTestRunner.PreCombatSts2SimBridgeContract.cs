using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Api;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task<ScenarioContext> RunPreCombatSts2SimBridgeContractAsync(
        RunState runState,
        EncounterModel encounter,
        CharacterModel character)
    {
        return _request.ScenarioId switch
        {
            "PRECOMBAT-STS2SIM-ABSENT-CONTRACT" => await RunPreCombatSts2SimAbsentContractAsync(runState, encounter, character),
            "PRECOMBAT-STS2SIM-BRIDGE-CONTRACT" => await RunPreCombatSts2SimFullContractAsync(runState, encounter, character),
            "PRECOMBAT-STS2SIM-PARITY-CONTRACT" => await RunPreCombatSts2SimParityContractAsync(runState, encounter, character),
            _ => throw new InvalidOperationException($"Unknown pre-combat bridge contract: {_request.ScenarioId}."),
        };
    }

    private async Task<ScenarioContext> RunPreCombatSts2SimAbsentContractAsync(
        RunState runState,
        EncounterModel encounter,
        CharacterModel character)
    {
        SetStage("verify_sts2sim_absent");
        if (PreCombatForecastWorker.HasInstalledInProcessBackend)
            throw new InvalidOperationException("sts2sim/ 缺席合同期望未安装同进程后端。");
        if (!OperatingSystem.IsWindows() && PreCombatForecastApi.IsAvailable)
            throw new InvalidOperationException("Linux 下缺少 sts2sim/ 时 PreCombatForecastApi.IsAvailable 必须为 false。");

        int workerStartsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
        int inProcessStartsBefore = PreCombatForecastWorker.InProcessStartCountForTesting;
        int activationsBefore = PreCombatSts2SimBridge.ActivationAttemptCountForTesting;
        MapPoint row0Target = FindRow0Target(runState);
        PreCombatForecastOptions options = new()
        {
            SearchBudgetMilliseconds = 3_000, OverallTimeoutMilliseconds = 90_000,
            MaxDegreeOfParallelism = 1, ForceRefresh = true,
        };

        if (OperatingSystem.IsWindows())
        {
            if (!PreCombatForecastApi.IsAvailable)
                throw new InvalidOperationException("Windows 下缺少 sts2sim/ 时 PreCombatForecastApi.IsAvailable 必须为 true（应可回退传统子进程）。");

            try
            {
                PreCombatForecastResult winResult = await PreCombatForecastApi.ForecastAsync(
                    runState, encounter, row0Target.coord.row + 1, row0Target.coord.col,
                    PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: options);
                if (!winResult.IsSuccess || winResult.ProducingBackend != PreCombatBackendKind.LegacyProcess
                    || PreCombatForecastWorker.WorkerStartCountForTesting != workerStartsBefore + 1
                    || PreCombatForecastWorker.InProcessStartCountForTesting != inProcessStartsBefore
                    || PreCombatSts2SimBridge.ActivationAttemptCountForTesting != activationsBefore)
                {
                    throw new InvalidOperationException(
                        $"Windows 缺少 sts2sim/ 回退传统子进程失败：status={winResult.Status} backend={winResult.ProducingBackend} " +
                        $"workerStarts={PreCombatForecastWorker.WorkerStartCountForTesting - workerStartsBefore} " +
                        $"inProcessStarts={PreCombatForecastWorker.InProcessStartCountForTesting - inProcessStartsBefore} " +
                        $"activations={PreCombatSts2SimBridge.ActivationAttemptCountForTesting - activationsBefore} error={winResult.Error}");
                }
                _completedChecks.Add(
                    $"PreCombatSts2SimAbsent:Mode={runState.GameMode}:LoadedCleanly:WindowsLegacySucceeded:WorkerStarts=1:Backend={winResult.ProducingBackend}");
            }
            finally { await PreCombatForecastApi.StopWorkerAsync(); }

            return await EnterContractCombatAsync(runState, row0Target, encounter, character);
        }

        PreCombatForecastResult result = await PreCombatForecastApi.ForecastAsync(
            runState, encounter, row0Target.coord.row + 1, row0Target.coord.col,
            PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: options);
        if (result.Status != PreCombatForecastStatus.Unsupported)
        {
            throw new InvalidOperationException(
                $"缺少 sts2sim/ 时预检未返回 Unsupported：status={result.Status} error={result.Error}。");
        }
        if (runState.GameMode == GameMode.Dread
            && result.Error?.Contains("dread_mode", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException($"缺少 sts2sim/ 时恐惧模式未保留 dread_mode 原因：{result.Error}");
        }
        if (PreCombatForecastWorker.WorkerStartCountForTesting != workerStartsBefore)
            throw new InvalidOperationException("缺少 sts2sim/ 时 Linux 端意外拉起了子进程。");

        _completedChecks.Add($"PreCombatSts2SimAbsent:Mode={runState.GameMode}:LoadedCleanly:ForecastUnsupported");
        return await EnterContractCombatAsync(runState, row0Target, encounter, character);
    }

    private async Task<ScenarioContext> RunPreCombatSts2SimFullContractAsync(
        RunState runState,
        EncounterModel encounter,
        CharacterModel character)
    {
        SetStage("verify_sts2sim_bridge_contract");
        if (!PreCombatForecastWorker.HasInstalledInProcessBackend)
            throw new InvalidOperationException("sts2sim 合同要求已安装 sts2sim/ 后端。");
        if (!PreCombatForecastApi.IsAvailable)
            throw new InvalidOperationException("已安装 sts2sim/ 后 PreCombatForecastApi.IsAvailable 必须为 true。");

        PreCombatEligibilityResult liveEligibility = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
        if (!liveEligibility.IsEligible)
        {
            throw new InvalidOperationException(
                $"标准单人跑局未通过 Sts2Sim 资格检查：{liveEligibility.FailureCode} ({liveEligibility.Detail})。");
        }

        SolverSettingsData savedSettings = SolverSettings.Current;
        MapPoint row0Target = FindRow0Target(runState);
        int targetFloor = row0Target.coord.row + 1;
        int targetCol = row0Target.coord.col;
        int workerStartsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
        int inProcessStartsBefore = PreCombatForecastWorker.InProcessStartCountForTesting;

        try
        {
            SolverSettings.ApplyForTesting(BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16));
            PreCombatSimulationOptions simOptions = new()
            {
                SearchBudgetMilliseconds = 3_000,
                OverallTimeoutMilliseconds = 30_000,
                MaxDegreeOfParallelism = 1,
                SampleSeed = 0UL,
            };

            SerializableRun baseSave = RunManager.Instance.ToSave(null);

            SerializableRun customSave = CloneSave(baseSave);
            customSave.IsCustomGame = true;
            await AssertPlanningUnsupportedAsync(
                runState, customSave, encounter, targetFloor, targetCol, "custom_game", simOptions);

            SerializableRun preFinishedSave = CloneSave(baseSave);
            preFinishedSave.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Monster };
            await AssertPlanningUnsupportedAsync(
                runState, preFinishedSave, encounter, targetFloor, targetCol, "planning_pre_finished_room", simOptions);

            SerializableRun spoilsSave = CloneSave(baseSave);
            spoilsSave.Players[0].Deck.Add(new SerializableCard { Id = new ModelId("CARD", "SPOILS_MAP") });
            await AssertPlanningUnsupportedAsync(
                runState, spoilsSave, encounter, targetFloor, targetCol, "planning_spoils_map_unsaved", simOptions);

            SerializableRun revisitedSave = CloneSave(baseSave);
            revisitedSave.VisitedMapCoords.Add(row0Target.coord);
            await AssertPlanningUnsupportedAsync(
                runState, revisitedSave, encounter, targetFloor, targetCol, "planning_revisited_coord", simOptions);

            PreCombatForecastOptions contractOpts = new()
            {
                SearchBudgetMilliseconds = 3_000, OverallTimeoutMilliseconds = 90_000,
                MaxDegreeOfParallelism = 1, ForceRefresh = true,
            };
            int policyFallbacksBefore = PreCombatForecastWorker.FallbackCountForTesting;
            int policyWorkersBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
            int policyInProcessBefore = PreCombatForecastWorker.InProcessStartCountForTesting;
            SolverSettings.ApplyForTesting(
                BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16) with { UseNoveltyPortfolio = true });
            PreCombatForecastResult policyRejected = await PreCombatForecastApi.ForecastAsync(
                runState, encounter, targetFloor, targetCol,
                PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: contractOpts);
            if (OperatingSystem.IsWindows())
            {
                if (!policyRejected.IsSuccess || policyRejected.ProducingBackend != PreCombatBackendKind.LegacyProcess
                    || PreCombatForecastWorker.FallbackCountForTesting != policyFallbacksBefore + 1
                    || PreCombatForecastWorker.WorkerStartCountForTesting != policyWorkersBefore + 1
                    || PreCombatForecastWorker.InProcessStartCountForTesting != policyInProcessBefore)
                {
                    throw new InvalidOperationException(
                        $"Windows 下 UseNoveltyPortfolio=true 未正确回退传统子进程：status={policyRejected.Status} backend={policyRejected.ProducingBackend} " +
                        $"fallbacks={PreCombatForecastWorker.FallbackCountForTesting - policyFallbacksBefore} " +
                        $"workerStarts={PreCombatForecastWorker.WorkerStartCountForTesting - policyWorkersBefore} " +
                        $"inProcessStarts={PreCombatForecastWorker.InProcessStartCountForTesting - policyInProcessBefore} error={policyRejected.Error}");
                }
                await PreCombatForecastApi.StopWorkerAsync();
                if (PreCombatForecastApi.GetWorkerStatus().IsRunning)
                    throw new InvalidOperationException("Windows 策略回退测试后 StopWorkerAsync 未停止遗留子进程。");
                workerStartsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
            }
            else if (policyRejected.Status != PreCombatForecastStatus.Unsupported
                     || policyRejected.Error?.Contains("policy_unsupported", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException(
                    $"UseNoveltyPortfolio=true 未保留 policy_unsupported：{policyRejected.Status} {policyRejected.Error}");
            }

            SolverSettings.ApplyForTesting(BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16));

            SerializableRun modifiedSave = CloneSave(baseSave);
            Player runPlayer = LocalContext.GetMe(runState) ?? throw new InvalidOperationException("找不到本地玩家。");
            int modifiedHp = Math.Max(1, runPlayer.Creature.MaxHp - 15);
            modifiedSave.Players[0].CurrentHp = modifiedHp;
            modifiedSave.Players[0].ExtraFields.CardShopRemovalsUsed = 2;
            PreCombatForecastResult modifiedPlanResult = await PreCombatForecastApi.SimulatePlanningAsync(
                runState, modifiedSave, encounter, targetFloor, targetCol,
                PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, simOptions);
            AssertBridgeSucceeded(modifiedPlanResult, "ModifiedPlanningZeroSeed", modifiedHp);

            PreCombatForecastResult highBitSeedResult = await PreCombatForecastApi.SimulatePlanningAsync(
                runState, modifiedSave, encounter, targetFloor, targetCol,
                PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, simOptions with { SampleSeed = 0xF00D_CAFE_1234_5678UL });
            AssertBridgeSucceeded(highBitSeedResult, "ModifiedPlanningHighBitSeed", modifiedHp);

            PreCombatForecastResult unknownResult = await PreCombatForecastApi.ForecastAsync(
                runState, encounter, targetFloor, targetCol,
                PreCombatRoomKind.Normal, PreCombatMapPointKind.Unknown, options: contractOpts);
            AssertBridgeSucceeded(unknownResult, "UnknownMapPointExplicitEncounter");

            await VerifyHostAndRoutingEdgeContractsAsync(runState, baseSave, encounter, targetFloor, targetCol, savedSettings);
            SolverSettings.ApplyForTesting(BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16));

            await InjectRelicAsync(runPlayer, new UnattendedRelicInjection { RelicId = "MERCURY_HOURGLASS" });
            await InjectRelicAsync(runPlayer, new UnattendedRelicInjection { RelicId = "TOOLBOX" });
            InjectPotionForTest(runPlayer, "FIRE_POTION");
            SolverSettings.ApplyForTesting(SolverSettings.ApplyPotionDirective(
                BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16), 0, "FIRE_POTION", SolverPotionDirective.Force));
            EncounterModel mawler = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", "MAWLER_NORMAL"));
            PreCombatForecastResult richOpenerResult = await PreCombatForecastApi.ForecastAsync(
                runState, mawler, targetFloor, targetCol, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
                options: contractOpts with { SearchBudgetMilliseconds = 4_000 });
            AssertBridgeSucceeded(richOpenerResult, "ToolboxMercuryFirePotion");
            string expectedFireTitle = ModelDb.GetById<PotionModel>(new ModelId("POTION", "FIRE_POTION"))
                .Title.GetFormattedText();
            PreCombatPotionUse? usedFire = richOpenerResult.PotionUses.FirstOrDefault(static use => use.Id == "FIRE_POTION");
            if (usedFire is null
                || !string.Equals(usedFire.Title, expectedFireTitle, StringComparison.Ordinal)
                || string.Equals(usedFire.Title, "FIRE_POTION", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"药水标题本地化校验失败：used={usedFire} expectedTitle='{expectedFireTitle}'。");
            }
            SolverSettings.ApplyForTesting(BuildDeterministicBridgeSettings(savedSettings, maxNodes: 1_200, beamWidth: 16));

            List<MapPoint> pathToAfterMid = FindPathWithInterveningNonCombatStep(runState);
            for (int i = 0; i < pathToAfterMid.Count - 2; i++)
            {
                MapPoint step = pathToAfterMid[i];
                runState.AddVisitedMapCoord(step.coord);
                runState.ActFloor = step.coord.row + 1;
                runState.AppendToMapPointHistory(step.PointType, ToHistoryRoomType(step.PointType), null);
            }
            MapPoint midStep = pathToAfterMid[^2];
            MapPoint afterMid = pathToAfterMid[^1];
            RoomType midRoomType = midStep.PointType switch
            {
                MapPointType.Shop => RoomType.Shop,
                MapPointType.Treasure => RoomType.Treasure,
                _ => RoomType.RestSite,
            };
            PreCombatRoomKind afterRoomKind = afterMid.PointType == MapPointType.Elite
                ? PreCombatRoomKind.Elite
                : PreCombatRoomKind.Normal;
            PreCombatMapPointKind afterPointKind = afterMid.PointType == MapPointType.Elite
                ? PreCombatMapPointKind.Elite
                : PreCombatMapPointKind.Normal;
            EncounterModel afterEncounter = afterMid.PointType == MapPointType.Elite
                ? ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", "BYGONE_EFFIGY_ELITE"))
                : encounter;
            PreCombatForecastResult interveningResult = await PreCombatForecastApi.ForecastAsync(
                runState,
                afterEncounter,
                afterMid.coord.row + 1,
                afterMid.coord.col,
                afterRoomKind,
                afterPointKind,
                options: new()
                {
                    InterveningMapPoints = [new PreCombatMapStep(midStep.coord, midRoomType, midStep.PointType)],
                    SearchBudgetMilliseconds = 3_000,
                    OverallTimeoutMilliseconds = 30_000,
                    MaxDegreeOfParallelism = 1,
                    ForceRefresh = true,
                });
            AssertBridgeSucceeded(interveningResult, $"Intervening_{midRoomType}");

            List<MapPoint> tailToPreBoss = FindPathToPreBoss(afterMid, runState.Map.BossMapPoint);
            foreach (MapPoint tailStep in new[] { midStep }.Concat(tailToPreBoss))
            {
                runState.AddVisitedMapCoord(tailStep.coord);
                runState.ActFloor = tailStep.coord.row + 1;
                runState.AppendToMapPointHistory(tailStep.PointType, ToHistoryRoomType(tailStep.PointType), null);
            }
            EncounterModel bossEncounter = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", "THE_KIN_BOSS"));
            MapCoord bossCoord = runState.Map.BossMapPoint.coord;
            PreCombatForecastResult secondBossResult = await PreCombatForecastApi.ForecastAsync(
                runState,
                bossEncounter,
                bossCoord.row + 1,
                bossCoord.col,
                PreCombatRoomKind.Boss,
                PreCombatMapPointKind.Boss,
                isSecondBoss: true,
                options: new()
                {
                    SearchBudgetMilliseconds = 3_000,
                    OverallTimeoutMilliseconds = 30_000,
                    MaxDegreeOfParallelism = 1,
                    ForceRefresh = true,
                });
            AssertBridgeSucceeded(secondBossResult, "Act1IsSecondBoss");

            SolverOverlay.Show(NGame.Instance!, "PreCombatBridgeCheck");
            if (!SolverOverlay.PreCombatBackendBadgeTextForTesting.Contains("Sts2Sim", StringComparison.Ordinal)
                || !SolverOverlay.PreCombatBackendBadgeTooltipForTesting.Contains("Sts2Sim", StringComparison.Ordinal))
                throw new InvalidOperationException($"战前桥接 UI 标识未显示 Sts2Sim：{SolverOverlay.PreCombatBackendBadgeTextForTesting}");
            SolverOverlay.Hide();

            int workerStarts = PreCombatForecastWorker.WorkerStartCountForTesting - workerStartsBefore;
            int inProcessStarts = PreCombatForecastWorker.InProcessStartCountForTesting - inProcessStartsBefore;
            if (workerStarts != 0 || inProcessStarts < 1)
            {
                throw new InvalidOperationException(
                    $"桥接合同期间工作进程计数异常：workerStarts={workerStarts} inProcessStarts={inProcessStarts}。");
            }

            _completedChecks.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"PreCombatSts2SimBridgeContract:Checks=AllPassed:InProcessStarts={inProcessStarts}:" +
                $"WorkerStarts={workerStarts}:LocalizedPotion={usedFire.Title}:Intervening={midRoomType}:Act1SecondBoss=Passed"));

            await PreCombatForecastApi.StopWorkerAsync();
            return await EnterContractCombatAsync(runState, runState.Map.BossMapPoint, bossEncounter, character);
        }
        finally
        {
            SolverSettings.ApplyForTesting(savedSettings);
            await PreCombatForecastApi.StopWorkerAsync();
        }
    }

    private async Task<ScenarioContext> RunPreCombatSts2SimParityContractAsync(
        RunState runState,
        EncounterModel encounter,
        CharacterModel character)
    {
        SetStage("verify_sts2sim_parity_and_portfolio");
        SolverSettingsData savedSettings = SolverSettings.Current;
        MapPoint target = FindRow0Target(runState);
        int targetFloor = target.coord.row + 1;
        int targetCol = target.coord.col;
        int workerStartsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;

        try
        {
            await PreCombatForecastApi.StopWorkerAsync();
            bool hasInProcessBackend = PreCombatForecastWorker.HasInstalledInProcessBackend;
            int activationsBefore = PreCombatSts2SimBridge.ActivationAttemptCountForTesting;
            workerStartsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;

            int requestedDop = _request.SearchMaxDegreeOfParallelismForTest is { } dop and > 0 ? dop : 1;
            SolverSettingsData settings = BuildDeterministicBridgeSettings(
                savedSettings,
                maxNodes: 5_000,
                beamWidth: 16,
                maxDegreeOfParallelism: requestedDop);
            SolverSettings.ApplyForTesting(settings);

            PreCombatForecastOptions parityOptions = new()
            {
                SearchBudgetMilliseconds = 10_000,
                OverallTimeoutMilliseconds = 90_000,
                MaxDegreeOfParallelism = requestedDop,
                ForceRefresh = true,
            };
            PreCombatRoomKind parityRoomKind = encounter.RoomType == RoomType.Elite
                ? PreCombatRoomKind.Elite
                : PreCombatRoomKind.Normal;

            string tokenBefore = PreCombatForecastApi.CaptureLiveStateToken(runState);
            long coldAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch coldWatch = Stopwatch.StartNew();
            PreCombatForecastResult bridgeResult = await PreCombatForecastApi.ForecastAsync(
                runState,
                encounter,
                targetFloor,
                targetCol,
                parityRoomKind,
                PreCombatMapPointKind.Normal,
                options: parityOptions);
            coldWatch.Stop();
            long coldAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - coldAllocBefore;

            if (bridgeResult.Status != PreCombatForecastStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    $"战前桥接同根对比失败：{bridgeResult.Status} {bridgeResult.Error}");
            }

            PreCombatWorkerStatus afterColdStatus = PreCombatForecastApi.GetWorkerStatus();

            long warmAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch warmWatch = Stopwatch.StartNew();
            PreCombatForecastResult warmResult = await PreCombatForecastApi.ForecastAsync(
                runState,
                encounter,
                targetFloor,
                targetCol,
                parityRoomKind,
                PreCombatMapPointKind.Normal,
                options: parityOptions);
            warmWatch.Stop();
            long warmAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - warmAllocBefore;

            if (warmResult.Status != PreCombatForecastStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    $"战前预热复测失败：{warmResult.Status} {warmResult.Error}");
            }
            if (bridgeResult.ProjectedHpLoss != warmResult.ProjectedHpLoss
                || bridgeResult.FinalHp != warmResult.FinalHp
                || bridgeResult.CombatEndedTurn != warmResult.CombatEndedTurn
                || !string.Equals(bridgeResult.SearchBoundary, warmResult.SearchBoundary, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"冷启动与预热复用预测结果不一致：cold=({bridgeResult.ProjectedHpLoss},{bridgeResult.FinalHp},{bridgeResult.CombatEndedTurn},{bridgeResult.SearchBoundary}) " +
                    $"warm=({warmResult.ProjectedHpLoss},{warmResult.FinalHp},{warmResult.CombatEndedTurn},{warmResult.SearchBoundary})。");
            }

            int workerStartsDelta = PreCombatForecastWorker.WorkerStartCountForTesting - workerStartsBefore;
            if (hasInProcessBackend)
            {
                if (bridgeResult.ProducingBackend != PreCombatBackendKind.InProcess
                    || warmResult.ProducingBackend != PreCombatBackendKind.InProcess
                    || workerStartsDelta != 0)
                {
                    throw new InvalidOperationException(
                        $"同进程后端同根对账异常：coldBackend={bridgeResult.ProducingBackend} warmBackend={warmResult.ProducingBackend} workerStarts={workerStartsDelta}。");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                if (bridgeResult.ProducingBackend != PreCombatBackendKind.LegacyProcess
                    || warmResult.ProducingBackend != PreCombatBackendKind.LegacyProcess
                    || workerStartsDelta != 1
                    || PreCombatSts2SimBridge.ActivationAttemptCountForTesting != activationsBefore)
                {
                    throw new InvalidOperationException(
                        $"Windows 遗留子进程同根对账异常：coldBackend={bridgeResult.ProducingBackend} warmBackend={warmResult.ProducingBackend} " +
                        $"workerStarts={workerStartsDelta} activations={PreCombatSts2SimBridge.ActivationAttemptCountForTesting - activationsBefore}。");
                }
            }
            else if (workerStartsDelta != 0)
            {
                throw new InvalidOperationException("同根对账期间意外拉起了独立子进程。");
            }

            if (!string.Equals(tokenBefore, PreCombatForecastApi.CaptureLiveStateToken(runState), StringComparison.Ordinal))
                throw new InvalidOperationException("战前桥接预测改变了主进程跑局状态或 RNG。");

            await PreCombatForecastApi.StopWorkerAsync();

            ScenarioContext context = await EnterContractCombatAsync(runState, target, encounter, character);
            CombatState combatState = context.CombatState;
            CombatRootSnapshot nativeRoot = CombatRootSnapshot.Capture(combatState);
            SearchPolicySnapshot nativePolicy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(),
                combatState,
                includeTurnSetup: false,
                TheftEncounterStrategy.IsApplicable(combatState) ? SolverTheftPolicy.PreserveResources : null) with
            {
                DisableSharedPrimaryIncumbentsForTesting = true,
            };

            SolverDisplayNames displayNames = SolverDisplayNames.Capture(combatState);
            BattleDamageSnapshot damageTracker = BattleDamageTracker.Observe(combatState);
            string encounterEntry = encounter.Id.Entry;
            Player local = context.Player;
            bool hasPowerInDeck = local.Deck.Cards.Any(
                static card => PowerCardValuationModels.Registry.ContainsCardId(card.Id.Entry));
            long nativeAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            SolverResult nativeResult = await Task.Run(() => CombatSearchCoordinator.Solve(
                nativeRoot,
                displayNames,
                damageTracker,
                nativePolicy,
                CancellationToken.None,
                progressCallback: null));
            long nativeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - nativeAllocBefore;
            if (!MegaCrit.Sts2.Core.Nodes.NGame.IsMainThread())
                throw new InvalidOperationException("Parity contract continuation left the Godot main thread.");

            int bridgeLoss = bridgeResult.ProjectedHpLoss ?? -1;
            int nativeLoss = nativeResult.ProjectedBattleHpLost;
            AssertParityAgainstNativeWin(bridgeResult, nativeResult);
            AssertBoundedNonWinningRejectedAgainstWin(bridgeResult, nativeResult);

            _completedChecks.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"PreCombatSts2SimParity:Encounter={encounterEntry}:HasPower={hasPowerInDeck}:Backend={bridgeResult.ProducingBackend}:" +
                $"ColdWallMs={coldWatch.Elapsed.TotalMilliseconds:F1}:WarmWallMs={warmWatch.Elapsed.TotalMilliseconds:F1}:" +
                $"ColdSearchMs={bridgeResult.SearchElapsedMilliseconds ?? -1:F1}:WarmSearchMs={warmResult.SearchElapsedMilliseconds ?? -1:F1}:" +
                $"ColdTotalMs={bridgeResult.TotalElapsedMilliseconds ?? -1:F1}:WarmTotalMs={warmResult.TotalElapsedMilliseconds ?? -1:F1}:" +
                $"BridgeLoss={bridgeLoss}:BridgeFinalHp={bridgeResult.FinalHp}:BridgeTurn={bridgeResult.CombatEndedTurn?.ToString(CultureInfo.InvariantCulture) ?? "null"}:" +
                $"BridgeBoundary={bridgeResult.SearchBoundary}:ColdAlloc={coldAllocatedBytes}:WarmAlloc={warmAllocatedBytes}:" +
                $"ChildWorkingSetBytes={afterColdStatus.WorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? "null"}:" +
                $"ChildPrivateMemoryBytes={afterColdStatus.PrivateMemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? "null"}:" +
                $"ChildPeakWorkingSetBytes={afterColdStatus.PeakWorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? "null"}:" +
                $"NativeLoss={nativeLoss}:NativeTurn={nativeResult.CombatEndedTurn?.ToString(CultureInfo.InvariantCulture) ?? "null"}:" +
                $"NativeBoundary={nativeResult.BoundaryReason}:NativeNodes={nativeResult.TotalExpandedNodes}:NativeMs={nativeResult.TotalSearchElapsed.TotalMilliseconds:F1}:" +
                $"NativeAlloc={nativeAllocatedBytes}:WorkerStarts={workerStartsDelta}:" +
                $"RequestedDop={requestedDop}:EffectiveDop={bridgeResult.EffectiveDegreeOfParallelism ?? -1}"));

            return context;
        }
        finally
        {
            SolverSettings.ApplyForTesting(savedSettings);
            await PreCombatForecastApi.StopWorkerAsync();
        }
    }

    private async Task<ScenarioContext> EnterContractCombatAsync(
        RunState runState,
        MapPoint target,
        EncounterModel encounter,
        CharacterModel character)
    {
        SetStage("enter_contract_combat");
        await RunManager.Instance.EnterMapCoordDebug(
            target.coord,
            encounter.RoomType,
            target.PointType,
            encounter.ToMutable(),
            showTransition: false);
        CombatState combatState = await WaitForPlayableCombatAsync();
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        Player player = LocalContext.GetMe(combatState)
            ?? throw new InvalidOperationException("合同进入战斗后找不到本地玩家。");
        int startedTurn = player.PlayerCombatState!.TurnNumber;
        return new ScenarioContext(character, encounter, combatState, player, startedTurn, [], [], []);
    }

    private static SolverSettingsData BuildDeterministicBridgeSettings(
        SolverSettingsData baseline,
        int maxNodes,
        int beamWidth,
        int maxDegreeOfParallelism = 1) =>
        baseline with
        {
            PerformancePreset = SolverPerformancePreset.Custom,
            SearchBeamWidth = beamWidth,
            SearchMaxExpandedNodes = maxNodes,
            SearchMaxCardBranchesPerNode = 12,
            SearchMaxPileChoiceBranchesPerAction = 8,
            SearchMaxHandChoiceBranchesPerAction = 8,
            SearchTimeLimitSeconds = 10,
            UseBeamWidthPortfolio = true,
            UseNoveltyPortfolio = false,
            UseEarlyTurnExploration = false,
            SearchMaxDegreeOfParallelism = maxDegreeOfParallelism,
            EnableNoGcRegion = false,
        };

    private static MapPoint FindRow0Target(RunState runState) =>
        (runState.CurrentMapPoint?.Children.Count > 0
            ? runState.CurrentMapPoint.Children
            : runState.Map.GetPointsInRow(1))
        .OrderBy(static point => point.coord.col)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("当前地图第 1 行没有可用的测试坐标。");

    private static List<MapPoint> FindPathWithInterveningNonCombatStep(RunState runState)
    {
        var queue = new Queue<List<MapPoint>>();
        var visited = new HashSet<MapCoord>();
        foreach (MapPoint start in runState.Map.GetPointsInRow(1).OrderBy(static p => p.coord.col))
        {
            if (visited.Add(start.coord))
                queue.Enqueue([start]);
        }

        while (queue.Count > 0)
        {
            List<MapPoint> path = queue.Dequeue();
            MapPoint tail = path[^1];
            foreach (MapPoint child in tail.Children.OrderBy(static p => p.coord.col))
            {
                if (path.Count >= 2
                    && tail.PointType is MapPointType.RestSite or MapPointType.Shop or MapPointType.Treasure
                    && child.PointType is MapPointType.Monster or MapPointType.Elite)
                {
                    return [.. path, child];
                }
                if (visited.Add(child.coord))
                    queue.Enqueue([.. path, child]);
            }
        }

        throw new InvalidOperationException("当前生成的地图中未找到非战斗中间节点衔接战斗节点的路径。");
    }

    private static List<MapPoint> FindPathToPreBoss(MapPoint start, MapPoint boss)
    {
        var queue = new Queue<List<MapPoint>>();
        var visited = new HashSet<MapCoord>();
        if (visited.Add(start.coord))
            queue.Enqueue([start]);

        while (queue.Count > 0)
        {
            List<MapPoint> path = queue.Dequeue();
            MapPoint tail = path[^1];
            if (tail.Children.Any(child => child.coord == boss.coord))
                return path;
            foreach (MapPoint child in tail.Children.OrderBy(static p => p.coord.col))
            {
                if (child.coord != boss.coord && visited.Add(child.coord))
                    queue.Enqueue([.. path, child]);
            }
        }

        throw new InvalidOperationException("无法从当前地图路径到达 Boss 前置节点。");
    }

    private static RoomType ToHistoryRoomType(MapPointType pointType) => pointType switch
    {
        MapPointType.Shop => RoomType.Shop,
        MapPointType.RestSite => RoomType.RestSite,
        MapPointType.Treasure => RoomType.Treasure,
        MapPointType.Elite => RoomType.Elite,
        MapPointType.Boss => RoomType.Boss,
        _ => RoomType.Monster,
    };

    private static SerializableRun CloneSave(SerializableRun save)
    {
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        JsonNode node = JsonSerializer.SerializeToNode(save, typeInfo)!;
        return JsonSerializer.Deserialize(node, typeInfo)!;
    }

    private static async Task AssertPlanningUnsupportedAsync(
        RunState liveRun,
        SerializableRun plannedSave,
        EncounterModel encounter,
        int targetFloor,
        int targetCol,
        string expectedReasonFragment,
        PreCombatSimulationOptions options)
    {
        PreCombatEligibilityResult eligibility = PreCombatLiveStateSnapshot
            .Capture(liveRun)
            .WithPlanningRun(
                plannedSave,
                RunState.FromSerializable(plannedSave),
                targetFloor,
                targetCol,
                isSecondBoss: false)
            .Eligibility;
        if (eligibility.IsEligible
            || !string.Equals(eligibility.FailureCode, expectedReasonFragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"WithPlanningRun 预期返回 '{expectedReasonFragment}'，" +
                $"实际为 eligible={eligibility.IsEligible} code={eligibility.FailureCode} ({eligibility.Detail})。");
        }
        if (OperatingSystem.IsWindows())
            return;

        PreCombatForecastResult result = await PreCombatForecastApi.SimulatePlanningAsync(
            liveRun,
            plannedSave,
            encounter,
            targetFloor,
            targetCol,
            PreCombatRoomKind.Normal,
            PreCombatMapPointKind.Normal,
            options);
        if (result.Status != PreCombatForecastStatus.Unsupported
            || result.Error?.Contains(expectedReasonFragment, StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                $"SimulatePlanningAsync 预期返回带 '{expectedReasonFragment}' 的 Unsupported，" +
                $"实际为 status={result.Status} error={result.Error}。");
        }
    }

    private static async Task VerifyHostAndRoutingEdgeContractsAsync(
        RunState runState,
        SerializableRun baseSave,
        EncounterModel encounter,
        int targetFloor,
        int targetCol,
        SolverSettingsData savedSettings)
    {
        var previousMocks = AssemblyInfo.MockTypes;
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        var harmony = new Harmony("CombatSolver.Testing.Sts2SimHostAudit." + Guid.NewGuid().ToString("N"));
        try
        {
            var cosmeticMod = new Mod
            {
                path = "test-cosmetic-rng-probe",
                manifest = new ModManifest
                {
                    id = "CosmeticGameplayProbe",
                    name = "Cosmetic Probe",
                    affectsGameplay = false,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (cosmeticMod, false);
            MethodInfo nextIntMethod = AccessTools.Method(typeof(Rng), nameof(Rng.NextInt), [typeof(int), typeof(int)])
                ?? throw new MissingMethodException(typeof(Rng).FullName, nameof(Rng.NextInt));
            harmony.Patch(
                nextIntMethod,
                prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));

            PreCombatEligibilityResult cosmeticPatchHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
            PreCombatEligibilityResult cosmeticPatchLive = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
            if (cosmeticPatchHost.IsEligible || cosmeticPatchHost.FailureCode != "gameplay_patch"
                || cosmeticPatchLive.IsEligible || cosmeticPatchLive.FailureCode != "gameplay_patch")
            {
                throw new InvalidOperationException(
                    $"宣称不影响玩法的 Mod 补丁 Rng.NextInt 未被拒绝：host={cosmeticPatchHost.FailureCode}, live={cosmeticPatchLive.FailureCode}。");
            }
            harmony.UnpatchAll(harmony.Id);
            AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch));

            var cardPatchMod = new Mod
            {
                path = "test-planning-card-probe",
                manifest = new ModManifest
                {
                    id = "PlanningCardGameplayProbe",
                    name = "Planning Card Probe",
                    affectsGameplay = true,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (cardPatchMod, false);
            MethodInfo angerOnPlay = AccessTools.Method(typeof(Anger), "OnPlay")
                ?? throw new MissingMethodException(typeof(Anger).FullName, "OnPlay");
            harmony.Patch(
                angerOnPlay,
                prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));
            SerializableRun angerPlanSave = CloneSave(baseSave);
            angerPlanSave.Players[0].Deck.Add(new SerializableCard { Id = new ModelId("CARD", "ANGER") });
            await AssertPlanningUnsupportedAsync(
                runState,
                angerPlanSave,
                encounter,
                targetFloor,
                targetCol,
                "gameplay_patch",
                new PreCombatSimulationOptions
                {
                    SearchBudgetMilliseconds = 3_000,
                    OverallTimeoutMilliseconds = 30_000,
                    MaxDegreeOfParallelism = 1,
                    SampleSeed = 0UL,
                });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            AssemblyInfo.MockTypes = previousMocks;
        }

        await VerifyPreCombatModEligibilityAuditAsync(runState, baseSave, encounter, targetFloor, targetCol);

        await PreCombatForecastApi.StopWorkerAsync();
        PreCombatWorkerStatus restartedStatus = await PreCombatForecastApi.RestartWorkerAsync(
            runState,
            idleTimeoutMilliseconds: 120_000);
        if (!restartedStatus.IsRunning
            || restartedStatus.IsBusy
            || restartedStatus.Backend != PreCombatBackendKind.InProcess)
        {
            throw new InvalidOperationException(
                $"RestartWorkerAsync 应在释放租约与忙碌标记后返回非忙碌状态：{restartedStatus}。");
        }
        await PreCombatForecastApi.StopWorkerAsync();

        try
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            PreCombatSts2SimBridge.PrewarmHookForTesting = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            };

            using var callerCts = new CancellationTokenSource();
            Task<PreCombatWorkerStatus> callerCancelRestart = PreCombatForecastApi.RestartWorkerAsync(
                runState,
                idleTimeoutMilliseconds: null,
                callerCts.Token);
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("RestartWorkerAsync 未进入 Prewarm 测试钩子。");
            callerCts.Cancel();
            release.Set();
            await ExpectWorkerOperationCanceledAsync(callerCancelRestart, "CallerCancelDuringPrewarm");
            PreCombatWorkerStatus afterCallerCancel = PreCombatForecastApi.GetWorkerStatus();
            if (afterCallerCancel.IsRunning || afterCallerCancel.IsBusy || afterCallerCancel.Backend != PreCombatBackendKind.None)
                throw new InvalidOperationException($"调用方取消 Restart 后不应发布就绪会话：{afterCallerCancel}。");

            entered.Reset();
            release.Reset();
            long epochBeforeStop = PreCombatForecastWorker.CaptureAdmissionEpoch();
            PreCombatSts2SimBridge.PrewarmHookForTesting = () =>
            {
                entered.Set();
                SpinWait.SpinUntil(
                    () => PreCombatForecastWorker.CaptureAdmissionEpoch() != epochBeforeStop
                          || release.IsSet,
                    TimeSpan.FromSeconds(5));
            };
            Task<PreCombatWorkerStatus> stopCancelRestart = PreCombatForecastApi.RestartWorkerAsync(
                runState,
                idleTimeoutMilliseconds: null);
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("StopWorkerAsync 测试未进入 Prewarm 钩子。");
            Task stopTask = PreCombatForecastApi.StopWorkerAsync();
            await ExpectWorkerOperationCanceledAsync(stopCancelRestart, "StopWorkerDuringPrewarm");
            await stopTask;
            PreCombatWorkerStatus afterStopCancel = PreCombatForecastApi.GetWorkerStatus();
            if (afterStopCancel.IsRunning || afterStopCancel.IsBusy || afterStopCancel.Backend != PreCombatBackendKind.None)
                throw new InvalidOperationException($"StopWorkerAsync 后不应发布就绪会话：{afterStopCancel}。");
            PreCombatSts2SimBridge.PrewarmHookForTesting = null;

            int fallbacksBefore = PreCombatForecastWorker.FallbackCountForTesting;
            int workersBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
            PreCombatForecastOptions edgeOptions = new()
            {
                SearchBudgetMilliseconds = 2_000, OverallTimeoutMilliseconds = 30_000,
                MaxDegreeOfParallelism = 1, ForceRefresh = true,
            };
            PreCombatSts2SimBridge.RunHookForTesting = static (_, _) =>
                Task.FromException<Sts2SimBridgeResponseDto>(new OperationCanceledException("unrelated-internal-cancel"));
            PreCombatForecastResult unexpectedOce = await PreCombatForecastApi.ForecastAsync(
                runState, encounter, targetFloor, targetCol, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: edgeOptions);
            if (unexpectedOce.Status != PreCombatForecastStatus.Failed
                || PreCombatForecastWorker.FallbackCountForTesting != fallbacksBefore
                || PreCombatForecastWorker.WorkerStartCountForTesting != workersBefore)
            {
                throw new InvalidOperationException(
                    $"未到期期限内的无关取消应返回 Failed 且零回退：status={unexpectedOce.Status}, " +
                    $"fallbacks={PreCombatForecastWorker.FallbackCountForTesting - fallbacksBefore}, error={unexpectedOce.Error}。");
            }

            PreCombatSts2SimBridge.RunHookForTesting = static (_, _) =>
            {
                byte[] badMissJson = System.Text.Encoding.UTF8.GetBytes(
                    "{\"abiVersion\":1,\"outcome\":\"CapabilityMiss\",\"failureCode\":\"encounter_unmapped\"," +
                    "\"error\":\"metric-bearing\",\"expandedNodes\":12,\"effectiveDegreeOfParallelism\":1,\"totalElapsedMilliseconds\":1.0}");
                return Task.FromResult(PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(badMissJson));
            };
            PreCombatForecastResult metricMissResult = await PreCombatForecastApi.ForecastAsync(
                runState, encounter, targetFloor, targetCol, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal, options: edgeOptions);
            if (metricMissResult.Status != PreCombatForecastStatus.Failed
                || PreCombatForecastWorker.FallbackCountForTesting != fallbacksBefore
                || PreCombatForecastWorker.WorkerStartCountForTesting != workersBefore)
            {
                throw new InvalidOperationException(
                    $"携带指标的 CapabilityMiss 应返回 Failed 且零回退：status={metricMissResult.Status}, " +
                    $"fallbacks={PreCombatForecastWorker.FallbackCountForTesting - fallbacksBefore}。");
            }
        }
        finally
        {
            PreCombatSts2SimBridge.PrewarmHookForTesting = null;
            PreCombatSts2SimBridge.RunHookForTesting = null;
            await PreCombatForecastApi.StopWorkerAsync();
        }
    }

    private static async Task ExpectWorkerOperationCanceledAsync(Task<PreCombatWorkerStatus> task, string label)
    {
        try
        {
            PreCombatWorkerStatus unexpected = await task;
            throw new InvalidOperationException($"[{label}] 预期取消异常，却返回成功状态：{unexpected}。");
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static void AssertParityAgainstNativeWin(
        PreCombatForecastResult bridgeResult,
        SolverResult nativeResult) =>
        AssertParityAgainstNativeWin(
            bridgeResult,
            nativeResult.OnlyDeathRoutesFound,
            nativeResult.CombatEndedTurn,
            nativeResult.ProjectedBattleHpLost);

    internal static void AssertParityAgainstNativeWin(
        PreCombatForecastResult bridgeResult,
        bool nativeOnlyDeathRoutes,
        int? nativeCombatEndedTurn,
        int nativeLoss)
    {
        int bridgeLoss = bridgeResult.ProjectedHpLoss ?? -1;
        bool bridgeDeathOnly = bridgeResult.Confidence == PreCombatForecastConfidence.DeathOnly;
        bool nativeCompleteWin = !nativeOnlyDeathRoutes && nativeCombatEndedTurn.HasValue;
        if (!nativeCompleteWin)
            return;

        if (bridgeDeathOnly)
        {
            throw new InvalidOperationException(
                $"战前桥接只找到死亡路线而原生搜出第 {nativeCombatEndedTurn} 回合胜利 " +
                $"(bridgeLoss={bridgeLoss}, nativeLoss={nativeLoss})。");
        }
        if (!bridgeResult.CombatEndedTurn.HasValue)
        {
            throw new InvalidOperationException(
                $"原生搜出第 {nativeCombatEndedTurn} 回合胜利，而战前桥接未搜出胜利终局 " +
                $"(confidence={bridgeResult.Confidence}, boundary={bridgeResult.SearchBoundary}, bridgeLoss={bridgeLoss}, nativeLoss={nativeLoss})。");
        }
        if (bridgeLoss > nativeLoss)
        {
            throw new InvalidOperationException(
                $"战前桥接胜利战损高于原生：bridgeLoss={bridgeLoss}, nativeLoss={nativeLoss}。");
        }
    }

    private static void AssertBoundedNonWinningRejectedAgainstWin(
        PreCombatForecastResult bridgeResult,
        SolverResult nativeResult)
    {
        PreCombatForecastResult boundedNonWinning = bridgeResult with
        {
            ProjectedHpLoss = 0,
            FinalHp = Math.Max(1, bridgeResult.FinalHp ?? 50),
            CombatEndedTurn = null,
            Confidence = PreCombatForecastConfidence.Bounded,
            SearchBoundary = "NodeLimit",
        };
        try
        {
            AssertParityAgainstNativeWin(
                boundedNonWinning,
                nativeOnlyDeathRoutes: false,
                nativeCombatEndedTurn: nativeResult.CombatEndedTurn ?? 2,
                nativeLoss: nativeResult.ProjectedBattleHpLost);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("有界未终局的桥接结果本应被原生胜利对账哨兵拒绝。");
    }

    private static void AssertBridgeSucceeded(
        PreCombatForecastResult result,
        string label,
        int? expectedEntryHp = null)
    {
        if (result.Status != PreCombatForecastStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"[{label}] 预检桥接未返回 Succeeded：{result.Status} {result.Error}");
        }
        if (expectedEntryHp is { } entryHp && result.ProjectedHpLoss is { } loss
            && result.FinalHp != Math.Max(0, entryHp - loss))
        {
            throw new InvalidOperationException(
                $"[{label}] 预计战损与最终生命不一致：entry={entryHp} loss={loss} final={result.FinalHp}。");
        }
    }
}
