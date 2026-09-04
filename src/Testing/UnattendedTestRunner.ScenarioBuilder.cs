using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using CombatSolver.Api;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record ScenarioContext(
        CharacterModel Character,
        EncounterModel Encounter,
        CombatState CombatState,
        Player Player,
        int StartedTurn,
        IReadOnlyList<UnattendedOrbCheck> OrbChecks,
        IReadOnlyList<UnattendedPotionCheck> PotionChecks,
        IReadOnlyList<UnattendedMonsterMoveCheck> MonsterMoveChecks);

    private sealed class ScenarioBuilder(UnattendedTestRunner runner)
    {
        public CombatState? CombatState { get; private set; }
        public int StartedTurn { get; private set; }

        public async Task<ScenarioContext> BuildAsync()
        {
            UnattendedTestRequest request = runner._request;
            runner.SetStage("game_startup");
            await runner._host.GameStartupComplete;
            runner.ApplyHeadlessFastModeOverride();
            runner.EnsureWithinDeadline();
            if (RunManager.Instance.IsInProgress)
                throw new InvalidOperationException("无人测试要求从无进行中跑局的独立游戏进程启动。");

            CharacterModel character = ResolveUnique(ModelDb.AllCharacters, request.CharacterId, "角色");
            EncounterModel encounter = ResolveUnique(ModelDb.AllEncounters, request.EncounterId, "遭遇");
            AssertExpectedLoadedMods(request.ExpectedLoadedMods);
            ModifierModel[] modifiers = request.ModifierIds
                .Select(id => ResolveUnique(
                    ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers),
                    id,
                    "自定义规则").ToMutable())
                .ToArray();

            RunState runState;
            if (request.LoadRunSnapshotDirectly)
            {
                runState = await RestoreRunSnapshotDirectlyAsync(request);
                if (!ModelMatches(runState.Players.Single().Character, request.CharacterId))
                    throw new InvalidOperationException("完整跑局快照的角色与请求角色不一致。");
            }
            else
            {
                runner.SetStage("start_run");
                await runner._host.StartNewSingleplayerRun(
                    character,
                    shouldSave: false,
                    ActModel.GetDefaultList(),
                    modifiers,
                    request.Seed,
                    GameMode.Standard,
                    request.Ascension);
                runner.EnsureWithinDeadline();
                runState = RunManager.Instance.DebugOnlyGetState()
                    ?? throw new InvalidOperationException("创建跑局后找不到 RunState。");
            }

            runner.SetStage("inject_run_relics");
            if (!request.LoadRunSnapshotDirectly && request.ActIndexForTest != 0)
            {
                if ((uint)request.ActIndexForTest >= (uint)runState.Acts.Count)
                    throw new InvalidOperationException($"测试幕索引超出范围：{request.ActIndexForTest}。");
                await RunManager.Instance.SetActInternal(request.ActIndexForTest);
            }
            if (request.MarkEncounterAsSecondBossForTest)
                runState.Act.SetSecondBossEncounter(encounter);
            if (request.LoadRunSnapshotDirectly)
                ApplyPreCombatInterveningMapPoints(runState, request.PreCombatInterveningMapPoints);
            if (request.TargetActFloor is { } targetActFloor)
                runState.ActFloor = targetActFloor;
            Player runPlayer = LocalContext.GetMe(runState)
                ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");
            if (request.PreCombatPlayerCurrentHpOverride is { } preCombatPlayerHp)
            {
                if (preCombatPlayerHp < 1 || preCombatPlayerHp > runPlayer.Creature.MaxHp)
                {
                    throw new InvalidOperationException(
                        $"战前玩家生命覆盖必须在 1 到 {runPlayer.Creature.MaxHp} 之间，收到 {preCombatPlayerHp}。");
                }
                runPlayer.Creature.SetCurrentHpInternal(preCombatPlayerHp);
                runner._completedChecks.Add($"PreCombatPlayerHp:{preCombatPlayerHp}");
            }
            foreach (UnattendedRelicInjection injection in request.Relics)
                await InjectRelicAsync(runPlayer, injection);
            if (!request.LoadRunSnapshotDirectly && !string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                await ApplyRunSnapshotAsync(runState, runPlayer, request.RunSnapshotPath);
            if (request.ClearRunDeck)
                ClearRunDeck(runState, runPlayer);
            foreach (UnattendedCardInjection injection in request.RunCards)
                await InjectRunCardAsync(runState, runPlayer, injection);
            if (request.VerifyPreCombatForecastApi)
                await VerifyPreCombatForecastApiAsync(runState, encounter);

            runner.SetStage("enter_encounter");
            EncounterModel mutableEncounter = encounter.ToMutable();
            MapPointType targetMapPointType = request.TargetMapPointType != MapPointType.Unassigned
                ? request.TargetMapPointType
                : request.TargetRoomType switch
                {
                    RoomType.Monster => MapPointType.Monster,
                    RoomType.Elite => MapPointType.Elite,
                    RoomType.Boss => MapPointType.Boss,
                    _ => throw new InvalidOperationException(
                        $"无人测试仅支持战斗房间，收到 {request.TargetRoomType}。"),
                };
            if (request.LoadRunSnapshotDirectly)
            {
                if (request.TargetActFloor is not { } directTargetFloor
                    || request.TargetMapColumn is not { } directTargetColumn)
                {
                    throw new InvalidOperationException(
                        "完整跑局恢复请求需要目标楼层和地图列坐标。");
                }
                await RunManager.Instance.EnterMapCoordDebug(
                    new MapCoord(directTargetColumn, directTargetFloor - 1),
                    request.TargetRoomType,
                    targetMapPointType,
                    mutableEncounter,
                    showTransition: false);
            }
            else
            {
                await RunManager.Instance.EnterRoomDebug(
                    RoomType.Monster,
                    MapPointType.Unassigned,
                    mutableEncounter);
            }

            runner.SetStage("wait_player_turn");
            if (request.VerifyTurnSetupSceneExitCancellation)
            {
                CombatState = await runner.WaitForPendingTurnSetupChoiceAsync();
                Player sceneExitPlayer = LocalContext.GetMe(CombatState)
                    ?? throw new InvalidOperationException("场景退出测试找不到本地玩家。");
                StartedTurn = sceneExitPlayer.PlayerCombatState!.TurnNumber;
                int cancellationCount = NativeChoiceRuntime.SceneExitCancellationCountForTesting;
                runner.SetStage("turn_setup_scene_exit");
                await runner._host.ReturnToMainMenu();
                if (NativeChoiceRuntime.SceneExitCancellationCountForTesting != cancellationCount + 1)
                    throw new InvalidOperationException("返回主菜单前没有取消仍在等待的回合开始手牌选择。");
                runner._completedChecks.Add(
                    $"TurnSetupSceneExitCancellation:Turn={StartedTurn}:Canceled=1");
                return new ScenarioContext(
                    character,
                    encounter,
                    CombatState,
                    sceneExitPlayer,
                    StartedTurn,
                    [],
                    [],
                    []);
            }
            CombatState = await runner.WaitForPlayableCombatAsync();
            Player player = LocalContext.GetMe(CombatState)
                ?? throw new InvalidOperationException("进入战斗后找不到本地玩家。");
            StartedTurn = player.PlayerCombatState!.TurnNumber;

            runner.SetStage("inject_state");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            IReadOnlyList<UnattendedOrbCheck> orbChecks = request.OrbChecks;
            IReadOnlyList<UnattendedPotionCheck> potionChecks = runner.GetPotionChecks();
            IReadOnlyList<UnattendedMonsterMoveCheck> monsterMoveChecks = runner.GetMonsterMoveChecks();
            foreach (string monsterId in request.AdditionalMonsterIds
                         .Where(static id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await EnsureMonsterExistsAsync(CombatState, monsterId, null);
            }
            if (!string.IsNullOrWhiteSpace(request.ReplayStatePath))
            {
                await ApplyReplayStateAsync(
                    CombatState,
                    player,
                    request.ReplayStatePath,
                    request.RunSnapshotPath);
                StartedTurn = player.PlayerCombatState!.TurnNumber;
                await runner.NextFrameAsync();
                return new ScenarioContext(
                    character,
                    encounter,
                    CombatState,
                    player,
                    StartedTurn,
                    orbChecks,
                    potionChecks,
                    monsterMoveChecks);
            }
            foreach (IGrouping<string, UnattendedMonsterMoveCheck> group in monsterMoveChecks
                         .Where(static check => !string.IsNullOrWhiteSpace(check.MonsterId))
                         .GroupBy(static check => check.MonsterId, StringComparer.OrdinalIgnoreCase))
            {
                string[] initialMoveIds = group
                    .Select(static check => check.SpawnInitialMoveId)
                    .Where(static moveId => !string.IsNullOrWhiteSpace(moveId))
                    .Distinct(StringComparer.Ordinal)
                    .Cast<string>()
                    .ToArray();
                if (initialMoveIds.Length > 1)
                    throw new InvalidOperationException($"怪物 {group.Key} 配置了多个出生初始行动。");
                string? initialMoveId = initialMoveIds.SingleOrDefault();
                await EnsureMonsterExistsAsync(CombatState, group.Key, initialMoveId);
                int requiredCount = group.Max(static check => check.MonsterOccurrence) + 1;
                int existingCount = CombatState.Enemies.Count(candidate =>
                    candidate.Monster != null && ModelMatches(candidate.Monster, group.Key));
                while (existingCount < requiredCount)
                {
                    await AddMonsterForTestAsync(CombatState, group.Key, initialMoveId);
                    existingCount++;
                }
            }
            if (request.InitialEnemyCurrentHps.Length > 0)
            {
                if (request.InitialEnemyCurrentHps.Length != CombatState.Enemies.Count)
                {
                    throw new InvalidOperationException(
                        $"逐敌生命数量 {request.InitialEnemyCurrentHps.Length} 与敌人数 {CombatState.Enemies.Count} 不同。");
                }
                for (int enemyIndex = 0; enemyIndex < CombatState.Enemies.Count; enemyIndex++)
                {
                    Creature enemy = CombatState.Enemies[enemyIndex];
                    await CreatureCmd.SetCurrentHp(
                        enemy,
                        Math.Clamp(request.InitialEnemyCurrentHps[enemyIndex], 0, enemy.MaxHp));
                }
            }
            else
            {
                foreach (Creature enemy in CombatState.Enemies.Where(static enemy => !enemy.IsDead))
                    await CreatureCmd.SetCurrentHp(enemy, Math.Min(request.EnemyCurrentHp, enemy.MaxHp));
            }
            runner.ForceInitialEnemyMoves(CombatState);
            runner.ForceInitialEnemyStateLogs(CombatState);
            if (request.InitialPlayerMaxHp is { } initialPlayerMaxHp)
                await CreatureCmd.SetMaxHp(player.Creature, initialPlayerMaxHp);
            if (request.InitialPlayerHp is { } initialPlayerHp)
            {
                await CreatureCmd.SetCurrentHp(
                    player.Creature,
                    Math.Clamp(initialPlayerHp, 1, player.Creature.MaxHp));
            }
            if (request.InitialPlayerBlock is { } initialPlayerBlock)
                await SetBlockAsync(player.Creature, initialPlayerBlock);
            if (request.InitialPlayerEnergy is { } initialPlayerEnergy)
                SetEnergy(player, initialPlayerEnergy);
            if (request.InitialPlayerStars is { } initialPlayerStars)
                SetStars(player, initialPlayerStars);
            if (request.InitialRoundNumber is { } initialRoundNumber)
                CombatState.RoundNumber = initialRoundNumber;
            if (request.InitialPlayerTurnNumber is { } initialPlayerTurnNumber)
            {
                PlayerCombatState playerState = player.PlayerCombatState!;
                if (initialPlayerTurnNumber < playerState.TurnNumber)
                {
                    throw new InvalidOperationException(
                        $"玩家测试回合号不能从 {playerState.TurnNumber} 回退到 {initialPlayerTurnNumber}。");
                }
                while (playerState.TurnNumber < initialPlayerTurnNumber)
                    playerState.IncrementTurnNumber();
            }

            if (request.ClearPlayerPiles)
                await ClearPlayerPilesAsync(player);
            else if (request.ClearPlayerHand)
            {
                await CardCmd.Discard(
                    new BlockingPlayerChoiceContext(),
                    player.PlayerCombatState!.Hand.Cards.ToArray());
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            foreach (UnattendedCardInjection injection in request.Cards)
                await InjectCardAsync(CombatState, player, injection);
            foreach (UnattendedOrbInjection injection in request.Orbs)
                await InjectOrbAsync(player, injection);
            foreach (UnattendedPotionInjection injection in request.Potions)
                InjectPotionForTest(player, injection.PotionId);
            foreach (UnattendedRelicInjection injection in request.CombatRelics)
                await InjectRelicAsync(player, injection);
            if (request.ClearAllPowers)
            {
                foreach (PowerModel power in CombatState.Creatures
                             .SelectMany(creature => creature.Powers)
                             .ToArray())
                {
                    await PowerCmd.Remove(power);
                }
            }
            foreach (UnattendedPowerInjection injection in request.Powers)
                await InjectPowerAsync(CombatState, player, injection);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            if (request.ReloadRunRngAfterStateInjection)
            {
                if (string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                    throw new InvalidOperationException("战斗状态注入后回载 RNG 需要跑局快照。");
                ReloadRunSnapshotRng(runState, player, request.RunSnapshotPath);
            }
            StartedTurn = player.PlayerCombatState!.TurnNumber;
            await runner.NextFrameAsync();

            return new ScenarioContext(
                character,
                encounter,
                CombatState,
                player,
                StartedTurn,
                orbChecks,
                potionChecks,
                monsterMoveChecks);
        }

        private async Task VerifyPreCombatForecastApiAsync(
            RunState runState,
            EncounterModel encounter)
        {
            runner.SetStage("verify_precombat_api");
            VerifyPreCombatNormalization();
            int forecastEntryHp = Math.Max(1, runState.Players.Single().Creature.CurrentHp - 1);
            string before = PreCombatForecastApi.CaptureLiveStateToken(runState);
            await PreCombatForecastApi.StopWorkerAsync();
            int startsBefore = PreCombatForecastWorker.WorkerStartCountForTesting;
            int reusesBefore = PreCombatForecastWorker.WorkerReuseCountForTesting;
            PreCombatForecastResult result;
            try
            {
                var options = new PreCombatForecastOptions
                {
                    SearchBudgetMilliseconds = 3_000,
                    OverallTimeoutMilliseconds = 45_000,
                    MaxDegreeOfParallelism = 1,
                    PlayerCurrentHpOverride = forecastEntryHp,
                    CancelWorkerWhenCallerCancels = true,
                    ForceRefresh = true,
                };
                int targetActFloor = Math.Max(1, runState.ActFloor + 1);
                int targetMapColumn = ResolveNextMapColumn(runState);
                result = await PreCombatForecastApi.ForecastAsync(
                    runState,
                    encounter,
                    targetActFloor,
                    targetMapColumn,
                    PreCombatRoomKind.Normal,
                    PreCombatMapPointKind.Normal,
                    options: options);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"战前 API 返回 {result.Status}: {result.Error} log={result.DiagnosticLogPath}");
                }

                PreCombatForecastResult repeated = await PreCombatForecastApi.ForecastAsync(
                    runState,
                    encounter,
                    targetActFloor,
                    targetMapColumn,
                    PreCombatRoomKind.Normal,
                    PreCombatMapPointKind.Normal,
                    options: options);
                if (!repeated.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"战前 API 复用请求返回 {repeated.Status}: {repeated.Error} log={repeated.DiagnosticLogPath}");
                }
                if (result.ProjectedHpLoss != repeated.ProjectedHpLoss
                    || result.FinalHp != repeated.FinalHp
                    || result.CombatEndedTurn != repeated.CombatEndedTurn
                    || result.SearchBoundary != repeated.SearchBoundary
                    || !result.PotionUses.SequenceEqual(repeated.PotionUses))
                {
                    throw new InvalidOperationException("相同跑局快照的复用 worker 返回了不同的战前预测结果。");
                }

                int workerStarts = PreCombatForecastWorker.WorkerStartCountForTesting - startsBefore;
                int workerReuses = PreCombatForecastWorker.WorkerReuseCountForTesting - reusesBefore;
                if (workerStarts != 1 || workerReuses != 1)
                {
                    throw new InvalidOperationException(
                        $"战前 worker 生命周期不符合预期：starts={workerStarts}, reuses={workerReuses}。");
                }
                if (!PreCombatForecastWorker.ActiveSessionAudioMutedForTesting)
                    throw new InvalidOperationException("隔离战前 worker 没有应用静音用户设置。");

                string after = PreCombatForecastApi.CaptureLiveStateToken(runState);
                if (!before.Equals(after, StringComparison.Ordinal))
                    throw new InvalidOperationException("战前 API 调用改变了主进程跑局状态或 RNG。");
                if (result.ProjectedHpLoss is { } hpLoss
                    && result.FinalHp != Math.Max(0, forecastEntryHp - hpLoss))
                {
                    throw new InvalidOperationException(
                        $"战前 HP 覆盖没有反映在结果中：entry={forecastEntryHp} loss={hpLoss} final={result.FinalHp}。");
                }
                runner._completedChecks.Add(
                    $"PreCombatForecastApi:EntryHp={forecastEntryHp}:HpLoss={result.ProjectedHpLoss}:Potions={result.PotionUses.Count}:" +
                    $"Boundary={result.SearchBoundary}:LiveStateUnchanged=1:WorkerStarts={workerStarts}:" +
                    $"WorkerReuses={workerReuses}:AudioMuted=1");
            }
            finally
            {
                await PreCombatForecastApi.StopWorkerAsync();
            }
        }

        private void VerifyPreCombatNormalization()
        {
            JsonObject emptyVariables = new()
            {
                ["variables"] = new JsonObject(),
            };
            JsonObject populatedVariables = new()
            {
                ["variables"] = new JsonObject
                {
                    ["kept"] = "value",
                },
            };
            JsonObject root = new()
            {
                ["map_point_history"] = new JsonArray
                {
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["player_stats"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["event_choices"] = new JsonArray
                                    {
                                        emptyVariables,
                                        populatedVariables,
                                    },
                                },
                            },
                        },
                    },
                },
            };

            PreCombatRunSerialization.NormalizeRoot(root);
            if (emptyVariables.ContainsKey("variables")
                || populatedVariables["variables"]?["kept"]?.GetValue<string>() != "value")
            {
                throw new InvalidOperationException(
                    "战前快照规范化未正确删除空事件变量，或误删了非空事件变量。");
            }
            runner._completedChecks.Add("PreCombatSnapshotNormalization:EmptyEventVariablesRemoved:PopulatedRetained");
        }

        private static int ResolveNextMapColumn(RunState runState)
        {
            int targetRow = Math.Max(0, runState.ActFloor);
            return runState.CurrentMapPoint?.Children
                       .OrderBy(static point => point.coord.col)
                       .FirstOrDefault()?.coord.col
                   ?? runState.Map.GetPointsInRow(targetRow)
                       .OrderBy(static point => point.coord.col)
                       .FirstOrDefault()?.coord.col
                   ?? runState.Map.BossMapPoint.coord.col;
        }

        private void ApplyPreCombatInterveningMapPoints(
            RunState runState,
            IReadOnlyList<UnattendedPreCombatMapStep> steps)
        {
            int previousRow = runState.CurrentMapCoord?.row ?? runState.ActFloor - 1;
            foreach (UnattendedPreCombatMapStep step in steps)
            {
                if (step.Coordinate.row != previousRow + 1)
                {
                    throw new InvalidOperationException(
                        $"战前中间地图点不连续：previousRow={previousRow} next={step.Coordinate}。");
                }
                if (runState.Map.GetPoint(step.Coordinate)?.PointType != step.MapPointType)
                    throw new InvalidOperationException($"战前中间地图点与恢复地图不一致：{step.Coordinate}。");
                if (step.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss or RoomType.Event)
                    throw new InvalidOperationException($"不能跳过会改变战斗序列的中间房间：{step.RoomType}。");

                runState.AddVisitedMapCoord(step.Coordinate);
                runState.ActFloor = step.Coordinate.row + 1;
                runState.AppendToMapPointHistory(step.MapPointType, step.RoomType, null);
                previousRow = step.Coordinate.row;
            }
            if (steps.Count > 0)
                runner._completedChecks.Add($"PreCombatInterveningMapPoints:{steps.Count}");
        }

        private async Task<RunState> RestoreRunSnapshotDirectlyAsync(UnattendedTestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                throw new InvalidOperationException("完整跑局恢复请求缺少 RunSnapshotPath。");
            if (!File.Exists(request.RunSnapshotPath))
                throw new FileNotFoundException("找不到完整跑局快照。", request.RunSnapshotPath);
            if (request.TargetActFloor is not { } targetFloor || targetFloor < 1)
                throw new InvalidOperationException("完整跑局恢复请求需要正数 TargetActFloor。");

            runner.SetStage("restore_run_snapshot");
            byte[] sourceBytes = await File.ReadAllBytesAsync(request.RunSnapshotPath);
            SerializableRun save = JsonSerializer.Deserialize(
                sourceBytes,
                JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("完整跑局快照为空。");
            if (save.CurrentActIndex != request.ActIndexForTest)
            {
                throw new InvalidOperationException(
                    $"快照幕索引 {save.CurrentActIndex} 与请求 {request.ActIndexForTest} 不一致。");
            }

            RunState restored = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(restored, save);
            await PreloadManager.LoadRunAssets(restored.Players.Select(static player => player.Character));
            await PreloadManager.LoadActAssets(restored.Act);
            RunManager.Instance.Launch();
            runner._host.RootSceneContainer.SetCurrentScene(NRun.Create(restored));
            await RunManager.Instance.GenerateMap();
            runner.EnsureWithinDeadline();

            byte[] restoredBytes = PreCombatRunSerialization.SerializeNormalized(
                RunManager.Instance.ToSave(null));
            if (!SHA256.HashData(sourceBytes).SequenceEqual(SHA256.HashData(restoredBytes)))
            {
                string restoredPath = Path.Combine(
                    Path.GetDirectoryName(request.RunSnapshotPath)!,
                    "run.restored.json");
                await File.WriteAllBytesAsync(restoredPath, restoredBytes);
                throw new InvalidOperationException(
                    $"完整跑局恢复或地图加载改变了快照状态；为保护 RNG，已拒绝预测。restored={restoredPath}");
            }
            runner._completedChecks.Add("DirectRunSnapshot:ExactStateRestored");
            return restored;
        }

        private static void AssertExpectedLoadedMods(IReadOnlyList<string> expected)
        {
            if (expected.Count == 0)
                return;
            string[] actual = ModManager.GetLoadedMods()
                .Where(static mod => mod.manifest?.id != null)
                .Select(static mod => $"{mod.manifest!.id}@{mod.manifest.version}")
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            string[] orderedExpected = expected.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"隔离进程 Mod 集合不一致。expected=[{string.Join(",", orderedExpected)}] " +
                    $"actual=[{string.Join(",", actual)}]");
            }
        }
    }
}
