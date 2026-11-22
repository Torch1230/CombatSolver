using System.Diagnostics;
using System.Globalization;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal async Task RunInCombatSts2SimContractAsync(
        CombatState combatState,
        Player player)
    {
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("战斗内 Sts2Sim 合同测试要求活动宿主 NGame。");
        if (!PreCombatSts2SimBridge.HasInstalledBackendArtifact())
            throw new InvalidOperationException("战斗内 Sts2Sim 合同测试要求已安装 sts2sim/ 后端。");

        int startedTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("玩家缺少战斗状态。");

        SolverSettingsData savedSettings = SolverSettings.Current;
        bool prevAutoTurn = _protocolHost.AutomaticTurnSearchEnabledForTesting;
        try
        {
            SolverSettingsData contractSettings = savedSettings with
            {
                SolverDisabled = false,
                AutomaticCalculationEnabled = false,
                AutoEnableFullAuto = false,
                UseSts2SimInCombat = true,
                SearchMaxDegreeOfParallelism = 4,
                PerformancePreset = SolverPerformancePreset.Custom,
                SearchBeamWidth = 24,
                SearchMaxExpandedNodes = 4_000,
                SearchMaxCardBranchesPerNode = 32,
                SearchMaxPileChoiceBranchesPerAction = 18,
                SearchMaxHandChoiceBranchesPerAction = 24,
                SearchTimeLimitSeconds = 15d,
                UseBeamWidthPortfolio = true,
                UseNoveltyPortfolio = false,
                UseEarlyTurnExploration = false,
            };
            SolverSettings.ApplyForTesting(contractSettings);

            SetStage("verify_in_combat_sts2sim_turn1");
            long successBeforeTurn1 = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn1Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));

            if (turn1Result.StartTurnNumber != startedTurn || turn1Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 返回无效路线：turn={turn1Result.StartTurnNumber}, actions={turn1Result.BestNode.Actions.Count}。");
            }
            turn1Result.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.Sts2Sim
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeTurn1 + 1)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合未使用 Sts2Sim 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }
            int expectedMinDop = System.Environment.ProcessorCount >= 2 ? 2 : 1;
            int actualDop = InCombatSolverBackendState.LastEffectiveParallelismForTesting ?? 0;
            if (actualDop < expectedMinDop || actualDop > 4)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 有效并行度超出预期：{actualDop}（期望 {expectedMinDop}..4）。");
            }
            SolverOverlay.RefreshControls();
            if (!SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Sts2Sim", StringComparison.Ordinal)
                || !SolverOverlay.InCombatBackendBadgeTooltipForTesting.Contains("Sts2Sim", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"战斗内 Sts2Sim 徽章文本未显示 Sts2Sim：text='{SolverOverlay.InCombatBackendBadgeTextForTesting}'");
            }

            SetStage("verify_in_combat_sts2sim_deploy_turn1");
            int cachedContinuationCount = turn1Result.Continuations.Count;
            if (turn1Result.SearchedTurns >= 2 && cachedContinuationCount == 0)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 搜索规划了 {turn1Result.SearchedTurns} 回合，但未标注跨回合续用点。");
            }
            await SolverController.DeployResolvedTurnForTesting(host, combatState, turn1Result);
            await WaitForPlayableTurnAsync(host, combatState, player, startedTurn + 1, TimeSpan.FromSeconds(25));
            if (SolverController.UnexpectedReplanCountForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"Turn 1 Sts2Sim 部署发生计划外重算：count={SolverController.UnexpectedReplanCountForTesting}。");
            }

            SetStage("verify_in_combat_sts2sim_turn2_transplant");
            long successBeforeTurn2 = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn2Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            if (turn2Result.StartTurnNumber != startedTurn + 1 || turn2Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植 Sts2Sim 返回无效路线：turn={turn2Result.StartTurnNumber}, actions={turn2Result.BestNode.Actions.Count}。");
            }
            turn2Result.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.Sts2Sim
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeTurn2 + 1)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植未使用 Sts2Sim 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }

            SetStage("verify_in_combat_user_disabled_fallback");
            long successBeforeDisabled = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverSettings.ApplyForTesting(contractSettings with { UseSts2SimInCombat = false });
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult builtInResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            builtInResult.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.BuiltIn
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeDisabled)
            {
                throw new InvalidOperationException(
                    $"关闭 UseSts2SimInCombat 后未切回内置模拟器：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
            }
            SolverOverlay.RefreshControls();
            if (SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Sts2Sim", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"关闭 Sts2Sim 后界面徽章仍显示 Sts2Sim：'{SolverOverlay.InCombatBackendBadgeTextForTesting}'");
            }

            SetStage("verify_in_combat_policy_fallback");
            SolverSettings.ApplyForTesting(contractSettings with
            {
                UseSts2SimInCombat = true,
                UseNoveltyPortfolio = true,
            });
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult policyFallbackResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            policyFallbackResult.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.BuiltIn)
            {
                throw new InvalidOperationException(
                    $"开启 UseNoveltyPortfolio 时未回退内置模拟器：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
            }

            _completedChecks.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"InCombatSts2SimContract:Turn1Dop={actualDop}:Turn1Continuations={cachedContinuationCount}:Turn2Transplant=Sts2Sim:DisabledFallback=BuiltIn:PolicyFallback=BuiltIn"));
        }
        finally
        {
            SolverSettings.ApplyForTesting(savedSettings);
            _protocolHost.SetAutomaticTurnSearchEnabledForTesting(prevAutoTurn);
        }
    }

    private static async Task<SolverResult> WaitForManualSearchResultAsync(NGame host, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (SolverController.LastSearchFailureForTesting is { } failure)
                throw new InvalidOperationException($"战斗内搜索失败：{failure}");
            if (!SolverController.IsSearching)
            {
                if (SolverController.LastCompletedResultForTesting is { } result)
                    return result;
                throw new InvalidOperationException(
                    $"战斗内搜索提前结束但未产生结果：exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }
            await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException(
            $"等待战斗内搜索完成超时（{timeout.TotalSeconds:0.#} 秒）：active={InCombatSolverBackendState.ActiveBackendForTesting}, " +
            $"last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
            $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}, " +
            $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
    }

    private static async Task WaitForPlayableTurnAsync(
        NGame host,
        CombatState combatState,
        Player player,
        int expectedTurn,
        TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
                throw new InvalidOperationException($"等待第 {expectedTurn} 回合期间战斗提前结束。");
            if (ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combatState)
                && combatState.CurrentSide == CombatSide.Player
                && player.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play
                && player.PlayerCombatState.TurnNumber == expectedTurn
                && !SolverController.IsDeploying
                && !CombatManager.Instance.PlayerActionsDisabled
                && !RunManager.Instance.ActionExecutor.IsRunning
                && RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            {
                return;
            }
            await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException($"等待进入第 {expectedTurn} 回合超时（{timeout.TotalSeconds:0.#} 秒）。");
    }
}
