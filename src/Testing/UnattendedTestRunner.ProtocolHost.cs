using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class ProtocolHost
    {
        private bool _requestLoopStarted;
        private int _acceptedRequestCount;
        private int _injectPlayerHpLossTurn;
        private int _injectPlayerHpLossAmount;
        private int _injectedPlayerHpLoss;
        private int _clearPlayerBlockBeforeEndTurn;
        private int _clearedPlayerBlock;

        public bool IsActive { get; private set; }
        public bool AutomaticTurnSearchEnabled { get; private set; } = true;
        public bool VerifyIncrementalSearch { get; private set; }
        public bool FixedSearchBudget { get; private set; }
        public void ApplyRecordedShortSearchMode(bool enabled) => FixedSearchBudget = enabled;
        public bool? Act3BossStrategyOverride { get; private set; }
        public void ApplyAct3BossStrategyOverride(bool? enabled) => Act3BossStrategyOverride = enabled;
        public bool MeasureSearchPhases { get; private set; }
        public int? SearchMaxDegreeOfParallelismOverride { get; private set; }
        public int? SearchBudgetOverrideMilliseconds { get; private set; }

        public void TryStart(NGame? host)
        {
            if (_requestLoopStarted || host == null)
                return;

            _requestLoopStarted = true;
            TaskHelper.RunSafely(RunRequestLoopAsync(host));
            Entry.Logger.Info("[CombatSolver/Unattended] REQUEST_LOOP_STARTED reuse_process=true");
        }

        public void EnableAutomaticTurnSearch()
            => AutomaticTurnSearchEnabled = true;

        public async Task ApplyScheduledStateDriftAsync(CombatState state, int turn)
        {
            if (!IsActive
                || turn != _injectPlayerHpLossTurn
                || _injectPlayerHpLossAmount <= 0
                || Interlocked.Exchange(ref _injectedPlayerHpLoss, 1) != 0)
            {
                return;
            }

            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("状态漂移测试找不到本地玩家。");
            int before = player.Creature.CurrentHp;
            int after = Math.Max(1, before - _injectPlayerHpLossAmount);
            await CreatureCmd.SetCurrentHp(player.Creature, after);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] INJECT_STATE_DRIFT turn={turn} field=hp before={before} after={after}");
        }

        public async Task ApplyScheduledPreEndTurnDriftAsync(CombatState state, int turn)
        {
            if (!IsActive
                || turn != _clearPlayerBlockBeforeEndTurn
                || Interlocked.Exchange(ref _clearedPlayerBlock, 1) != 0)
            {
                return;
            }

            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("结束回合漂移测试找不到本地玩家。");
            int before = player.Creature.Block;
            await SetBlockAsync(player.Creature, 0);
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] INJECT_PRE_END_TURN_DRIFT turn={turn} field=block before={before} after=0");
        }

        private async Task RunRequestLoopAsync(NGame host)
        {
            string runningPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.RunningUri);
            string requestPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.RequestUri);
            try
            {
                while (true)
                {
                    if (!File.Exists(requestPath))
                    {
                        for (int frame = 0; frame < 10; frame++)
                            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                        continue;
                    }

                    string json = File.ReadAllText(requestPath);
                    UnattendedTestRequest request = JsonSerializer.Deserialize<UnattendedTestRequest>(
                        json,
                        UnattendedTestFiles.JsonOptions)
                        ?? throw new InvalidOperationException("无人测试请求为空。");
                    if (request.SchemaVersion != 1)
                        throw new InvalidOperationException($"不支持的无人测试协议版本 {request.SchemaVersion}。");
                    if (request.HoldAfterInitialSearch && request.ExitOnComplete)
                    {
                        throw new InvalidOperationException(
                            "无人测试请求不能同时暂停初始搜索并在完成后退出。");
                    }

                    File.Move(requestPath, runningPath, true);
                    Activate(request);
                    int requestSequence = ++_acceptedRequestCount;
                    Entry.Logger.Info(
                        $"[CombatSolver/Unattended] REQUEST_ACCEPTED run_id={request.RunId} " +
                        $"scenario={request.ScenarioId} process_sequence={requestSequence} reused_process={requestSequence > 1}");
                    RunCompletion completion;
                    try
                    {
                        completion = await new UnattendedTestRunner(host, request, this).RunAsync();
                    }
                    finally
                    {
                        Reset();
                    }
                    if (completion == RunCompletion.Failed)
                    {
                        UnattendedAsyncActivityTracker.AbortRequest();
                        Entry.Logger.Warn(
                            "[CombatSolver/Unattended] PROCESS_NOT_REUSABLE reason=failed_request exit=true");
                        host.GetTree().Quit(1);
                        return;
                    }
                    if (completion == RunCompletion.InitialSearchHeld)
                    {
                        if (!request.HoldAfterInitialSearch || request.ExitOnComplete)
                        {
                            throw new InvalidOperationException(
                                "执行器暂停了初始搜索，但请求没有声明合法的暂停生命周期。");
                        }
                        // The launcher intentionally keeps this live combat attached to a profiler
                        // until its release marker is written, then terminates the owned process.
                        await WaitUntilHeldAsync(host);
                        WriteReady(request.RunId, held: true);
                        return;
                    }
                    if (completion is not (RunCompletion.Passed or RunCompletion.FailedReusable))
                        throw new InvalidOperationException($"未知的无人测试完成状态 {completion}。");
                    if (request.HoldAfterInitialSearch)
                    {
                        throw new InvalidOperationException(
                            "请求暂停初始搜索，但执行器已在未暂停搜索的情况下完成。该进程不可复用。");
                    }
                    if (request.ExitOnComplete)
                    {
                        UnattendedAsyncActivityTracker.AbortRequest();
                        return;
                    }
                    await WaitUntilReusableAsync(host);
                    WriteReady(request.RunId, held: false);
                }
            }
            catch (Exception ex)
            {
                Reset();
                UnattendedAsyncActivityTracker.AbortRequest();
                Entry.Logger.Error(
                    $"[CombatSolver/Unattended] PROCESS_NOT_REUSABLE exit=true exception={ex}");
                host.GetTree().Quit(1);
            }
        }

        private static async Task WaitUntilReusableAsync(NGame host)
        {
            const int quiescenceTimeoutMilliseconds = 90_000;
            long deadline = System.Environment.TickCount64 + quiescenceTimeoutMilliseconds;
            int consecutiveIdleFrames = 0;
            bool reclaimedAfterQuiescence = false;
            while (System.Environment.TickCount64 < deadline)
            {
                bool gameIdle = !RunManager.Instance.IsInProgress
                    && !RunManager.Instance.IsCleaningUp
                    && !RunManager.Instance.ActionExecutor.IsRunning
                    && RunManager.Instance.ActionQueueSet.IsEmpty
                    && !CombatManager.Instance.IsStarting
                    && !CombatManager.Instance.IsInProgress
                    && CombatManager.Instance.DebugOnlyGetState() == null
                    && CardSelectCmd.Selector == null
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying
                    && host.RootSceneContainer.CurrentScene is NMainMenu;
                bool idle = UnattendedAsyncActivityTracker.IsIdle && gameIdle;
                if (!idle)
                {
                    consecutiveIdleFrames = 0;
                    reclaimedAfterQuiescence = false;
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                consecutiveIdleFrames++;
                if (consecutiveIdleFrames < 2)
                {
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                if (!reclaimedAfterQuiescence)
                {
                    int remainingMilliseconds = checked((int)Math.Max(
                        1,
                        deadline - System.Environment.TickCount64));
                    consecutiveIdleFrames = 0;
                    await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                            "unattended_reuse",
                            forceCollection: true,
                            includeCombatLifecyclePressure: false,
                            Task.CompletedTask,
                            static () => { })
                        .WaitAsync(TimeSpan.FromMilliseconds(remainingMilliseconds));
                    reclaimedAfterQuiescence = true;
                    continue;
                }

                if (UnattendedAsyncActivityTracker.TryEndRequest())
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Unattended] PROCESS_QUIESCENT reuse_process=true");
                    return;
                }
                consecutiveIdleFrames = 0;
                reclaimedAfterQuiescence = false;
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            throw new TimeoutException(
                $"无人测试进程在 {quiescenceTimeoutMilliseconds} ms 内没有完成场景清理。");
        }

        private static async Task WaitUntilHeldAsync(NGame host)
        {
            const int quiescenceTimeoutMilliseconds = 90_000;
            long deadline = System.Environment.TickCount64 + quiescenceTimeoutMilliseconds;
            int consecutiveIdleFrames = 0;
            while (System.Environment.TickCount64 < deadline)
            {
                bool idle = UnattendedAsyncActivityTracker.IsIdle
                    && RunManager.Instance.IsInProgress
                    && !RunManager.Instance.IsCleaningUp
                    && !RunManager.Instance.ActionExecutor.IsRunning
                    && RunManager.Instance.ActionQueueSet.IsEmpty
                    && !CombatManager.Instance.IsStarting
                    && CombatManager.Instance.IsInProgress
                    && !CombatManager.Instance.IsOverOrEnding
                    && CombatManager.Instance.DebugOnlyGetState() != null
                    && CardSelectCmd.Selector == null
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying;
                if (!idle)
                {
                    consecutiveIdleFrames = 0;
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                consecutiveIdleFrames++;
                if (consecutiveIdleFrames < 2)
                {
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }
                if (UnattendedAsyncActivityTracker.TryEndRequest())
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Unattended] PROCESS_QUIESCENT held_search=true");
                    return;
                }
                consecutiveIdleFrames = 0;
            }

            throw new TimeoutException(
                $"无人测试暂停进程在 {quiescenceTimeoutMilliseconds} ms 内没有完成异步活动。");
        }

        private static void WriteReady(string runId, bool held)
        {
            string readyPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.ReadyUri);
            string tempPath = readyPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    new { SchemaVersion = 1, RunId = runId, Held = held },
                    UnattendedTestFiles.JsonOptions));
            File.Move(tempPath, readyPath, true);
        }

        private void Activate(UnattendedTestRequest request)
        {
            // Exit requests never expose this process for reuse, so tracking their background
            // continuations would add work without strengthening the process boundary.
            if (!request.ExitOnComplete)
                UnattendedAsyncActivityTracker.BeginRequest();
            IsActive = true;
            _injectPlayerHpLossTurn = request.InjectPlayerHpLossBeforeAutoSearchTurn ?? 0;
            _injectPlayerHpLossAmount = request.InjectPlayerHpLossAmount;
            _injectedPlayerHpLoss = 0;
            _clearPlayerBlockBeforeEndTurn = request.ClearPlayerBlockBeforeEndTurnForTest ?? 0;
            _clearedPlayerBlock = 0;
            AutomaticTurnSearchEnabled = false;
            VerifyIncrementalSearch = request.VerifyIncrementalSearch;
            FixedSearchBudget = request.FixedSearchBudget;
            MeasureSearchPhases = request.MeasureSearchPhases;
            if (request.SearchMaxDegreeOfParallelismForTest is { } maxDegreeOfParallelism
                && (maxDegreeOfParallelism < 1
                    || maxDegreeOfParallelism > SolverWeights.MaximumSearchMaxDegreeOfParallelism))
            {
                throw new InvalidOperationException(
                    $"搜索并行度必须在 1..{SolverWeights.MaximumSearchMaxDegreeOfParallelism} 之间，" +
                    $"实际为 {maxDegreeOfParallelism}。");
            }
            SearchMaxDegreeOfParallelismOverride = request.SearchMaxDegreeOfParallelismForTest;
            SearchBudgetOverrideMilliseconds = request.SearchBudgetOverrideMilliseconds
                ?? (request.FixedSearchBudget
                    ? request.LegacyShortSearchBudgetMilliseconds ?? request.LegacyDeepSearchBudgetMilliseconds
                    : request.LegacyDeepSearchBudgetMilliseconds ?? request.LegacyShortSearchBudgetMilliseconds);
        }

        private void Reset()
        {
            IsActive = false;
            AutomaticTurnSearchEnabled = true;
            VerifyIncrementalSearch = false;
            FixedSearchBudget = false;
            MeasureSearchPhases = false;
            SearchMaxDegreeOfParallelismOverride = null;
            Act3BossStrategyOverride = null;
            _injectPlayerHpLossTurn = 0;
            _injectPlayerHpLossAmount = 0;
            _injectedPlayerHpLoss = 0;
            _clearPlayerBlockBeforeEndTurn = 0;
            _clearedPlayerBlock = 0;
            SearchBudgetOverrideMilliseconds = null;
        }
    }
}
