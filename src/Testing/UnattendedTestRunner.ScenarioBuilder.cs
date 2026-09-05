using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

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
            ModifierModel[] modifiers = request.ModifierIds
                .Select(id => ResolveUnique(
                    ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers),
                    id,
                    "自定义规则").ToMutable())
                .ToArray();

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

            runner.SetStage("inject_run_relics");
            RunState runState = RunManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("创建跑局后找不到 RunState。");
            if (request.ActIndexForTest != 0)
            {
                if ((uint)request.ActIndexForTest >= (uint)runState.Acts.Count)
                    throw new InvalidOperationException($"测试幕索引超出范围：{request.ActIndexForTest}。");
                await RunManager.Instance.SetActInternal(request.ActIndexForTest);
            }
            if (request.MarkEncounterAsSecondBossForTest)
                runState.Act.SetSecondBossEncounter(encounter);
            Player runPlayer = LocalContext.GetMe(runState)
                ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");
            foreach (UnattendedRelicInjection injection in request.Relics)
                await InjectRelicAsync(runPlayer, injection);
            if (!string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                await ApplyRunSnapshotAsync(runState, runPlayer, request.RunSnapshotPath);
            if (request.ClearRunDeck)
                ClearRunDeck(runState, runPlayer);
            foreach (UnattendedCardInjection injection in request.RunCards)
                await InjectRunCardAsync(runState, runPlayer, injection);

            runner.SetStage("enter_encounter");
            EncounterModel mutableEncounter = encounter.ToMutable();
            await RunManager.Instance.EnterRoomDebug(
                RoomType.Monster,
                MapPointType.Unassigned,
                mutableEncounter);

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
    }
}
