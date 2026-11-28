using System.Diagnostics;
using System.Globalization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal async Task RunInCombatRustKernelContractAsync(
        CombatState combatState,
        Player player)
    {
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("战斗内 Rust 内核合同测试要求活动宿主 NGame。");
        if (!InCombatRustKernelBridge.HasInstalledBackendArtifact())
            throw new InvalidOperationException("战斗内 Rust 内核合同测试要求已安装 rust-kernel/ 本地库。");

        int startedTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("玩家缺少战斗状态。");

        int totalEnemyHp = combatState.Enemies.Where(static e => e.IsAlive).Sum(static e => e.CurrentHp);
        if (totalEnemyHp < 60)
        {
            foreach (Creature enemy in combatState.Enemies)
            {
                if (!enemy.IsAlive)
                    continue;
                int targetHp = Math.Max(80, enemy.MaxHp);
                await CreatureCmd.SetMaxHp(enemy, targetHp);
                await CreatureCmd.Heal(enemy, targetHp);
            }
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }

        SolverSettingsData savedSettings = SolverSettings.Current;
        bool prevAutoTurn = _protocolHost.AutomaticTurnSearchEnabled;
        _protocolHost.SetAutomaticTurnSearchEnabledForTesting(false);
        try
        {
            SolverSettingsData contractSettings = savedSettings with
            {
                SolverDisabled = false,
                AutomaticCalculationEnabled = false,
                AutoEnableFullAuto = false,
                UseRustCombatKernel = true,
                SearchMaxDegreeOfParallelism = 4,
                PerformancePreset = SolverPerformancePreset.Custom,
                SearchBeamWidth = 24,
                SearchMaxExpandedNodes = 4_000,
                SearchMaxCardBranchesPerNode = 32,
                SearchMaxPileChoiceBranchesPerAction = 18,
                SearchMaxHandChoiceBranchesPerAction = 24,
                SearchTimeLimitSeconds = 15d,
            };
            SolverSettings.ApplyForTesting(contractSettings);

            // Probe corrupt-DLL fallback BEFORE the first valid native activation so this test
            // never requires unloading a library that owns persistent Rayon worker threads.
            SetStage("verify_in_combat_rust_kernel_corrupt_fallback");
            string corruptScratch = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".local",
                "tool-tasks",
                "rust-kernel",
                "corrupt-" + Guid.NewGuid().ToString("N"));
            string? prevOverride = InCombatRustKernelBridge.ModDirectoryOverrideForTesting;
            try
            {
                Directory.CreateDirectory(Path.Combine(corruptScratch, "rust-kernel"));
                File.WriteAllText(
                    Path.Combine(corruptScratch, "rust-kernel", InCombatRustKernelBridge.NativeLibraryFileName),
                    "corrupt-native-binary-bytes");
                InCombatRustKernelBridge.ResetForTesting();
                InCombatRustKernelBridge.ModDirectoryOverrideForTesting = corruptScratch;
                SolverSettings.ApplyForTesting(contractSettings with { UseRustCombatKernel = true });

                long successBeforeCorrupt = InCombatSolverBackendState.RustKernelSuccessCountForTesting;
                SolverController.RequestSearch(host, combatState, SearchReason.Manual);
                SolverResult corruptFallbackResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
                corruptFallbackResult.AssertCompleteTurnOutcomes();

                if (!InCombatRustKernelBridge.IsCorrupt)
                    throw new InvalidOperationException("损坏的 rust-kernel 本地库未标记为 IsCorrupt。");
                if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.HotKernel
                    || InCombatSolverBackendState.RustKernelSuccessCountForTesting != successBeforeCorrupt)
                {
                    throw new InvalidOperationException(
                        $"损坏 rust-kernel 后未回退到 HotKernel：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
                }
            }
            finally
            {
                InCombatRustKernelBridge.ModDirectoryOverrideForTesting = prevOverride;
                InCombatRustKernelBridge.ResetForTesting();
                if (Directory.Exists(corruptScratch))
                    Directory.Delete(corruptScratch, recursive: true);
            }

            SetStage("verify_in_combat_rust_kernel_turn1");
            long successBeforeTurn1 = InCombatSolverBackendState.RustKernelSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn1Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));

            if (turn1Result.StartTurnNumber != startedTurn || turn1Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Rust 内核返回无效路线：turn={turn1Result.StartTurnNumber}, actions={turn1Result.BestNode.Actions.Count}。");
            }
            turn1Result.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.RustKernel
                || InCombatSolverBackendState.RustKernelSuccessCountForTesting != successBeforeTurn1 + 1)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合未使用 RustKernel 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }
            int expectedMinDop = System.Environment.ProcessorCount >= 2 ? 2 : 1;
            int actualDop = InCombatSolverBackendState.LastEffectiveParallelismForTesting ?? 0;
            if (actualDop < expectedMinDop || actualDop > 4)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 RustKernel 有效并行度超出预期：{actualDop}（期望 {expectedMinDop}..4）。");
            }
            SolverOverlay.RefreshControls();
            if (!SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Rust", StringComparison.Ordinal)
                || !SolverOverlay.InCombatBackendBadgeTooltipForTesting.Contains("Rust", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"战斗内 Rust 内核徽章文本未显示 Rust：text='{SolverOverlay.InCombatBackendBadgeTextForTesting}', tooltip='{SolverOverlay.InCombatBackendBadgeTooltipForTesting}'");
            }

            SetStage("verify_in_combat_rust_kernel_deploy_turn1");
            await SolverController.DeployResolvedTurnForTesting(host, combatState, turn1Result);
            await WaitForPlayableTurnAsync(host, combatState, player, startedTurn + 1, TimeSpan.FromSeconds(25));
            if (SolverController.UnexpectedReplanCountForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"Turn 1 RustKernel 部署发生计划外重算：count={SolverController.UnexpectedReplanCountForTesting}。");
            }

            SetStage("verify_in_combat_rust_kernel_turn2_transplant");
            long successBeforeTurn2 = InCombatSolverBackendState.RustKernelSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn2Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            if (turn2Result.StartTurnNumber != startedTurn + 1 || turn2Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植 RustKernel 返回无效路线：turn={turn2Result.StartTurnNumber}, actions={turn2Result.BestNode.Actions.Count}。");
            }
            turn2Result.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.RustKernel
                || InCombatSolverBackendState.RustKernelSuccessCountForTesting != successBeforeTurn2 + 1)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植未使用 RustKernel 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }

            SetStage("verify_in_combat_rust_kernel_disabled_fallback");
            long successBeforeDisabled = InCombatSolverBackendState.RustKernelSuccessCountForTesting;
            SolverSettings.ApplyForTesting(contractSettings with { UseRustCombatKernel = false });
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult disabledFallbackResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            disabledFallbackResult.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.HotKernel
                || InCombatSolverBackendState.RustKernelSuccessCountForTesting != successBeforeDisabled)
            {
                throw new InvalidOperationException(
                    $"关闭 UseRustCombatKernel 后未回退到 HotKernel：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
            }
            SolverOverlay.RefreshControls();
            if (SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Rust", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"关闭 Rust 内核后界面徽章仍显示 Rust：'{SolverOverlay.InCombatBackendBadgeTextForTesting}'");
            }

            _completedChecks.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"InCombatRustKernelContract:Turn1Dop={actualDop}:Turn2Transplant=RustKernel:DisabledFallback={InCombatSolverBackendState.LastUsedBackendForTesting}:CorruptFallback=Verified"));
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
        throw new TimeoutException($"等待玩家第 {expectedTurn} 回合可操作边界超时。");
    }
}
