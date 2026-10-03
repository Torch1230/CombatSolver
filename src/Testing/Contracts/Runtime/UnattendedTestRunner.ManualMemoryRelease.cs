using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunManualMemoryReleaseAutoContractAsync(CombatState combatState)
    {
        SetStage("manual_memory_release_before_auto");
        SolverResult result = SolverController.CurrentResultForBugReport
            ?? throw new InvalidOperationException("手动释放内存合同缺少计算完成的路线。");
        if (!result.BestNode.Actions.Any(action => action.CardId == "QUASAR"))
            throw new InvalidOperationException("手动释放内存合同没有生成卡牌路线。");
        int searchesBefore = SolverController.SearchesStartedForTesting;
        string liveBefore = ContinuationStamp.CaptureLive(combatState).StateText;
        await SearchGcPolicy.ForceManualProcessMemoryRelease();
        if (!ReferenceEquals(result, SolverController.CurrentResultForBugReport)
            || liveBefore != ContinuationStamp.CaptureLive(combatState).StateText)
            throw new InvalidOperationException("手动释放内存改变了保留路线或真实战斗状态。");
        SolverController.SetFullAuto(_host, combatState, enabled: true);
        if (SolverController.SearchesStartedForTesting != searchesBefore)
            throw new InvalidOperationException("释放内存后的全自动重新启动了搜索。");
        _completedChecks.Add($"ManualMemoryReleaseAuto:retainedPlan:liveUnchanged:searches={searchesBefore}:noNewSearch");
    }
}
