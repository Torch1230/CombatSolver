using System.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // The no_gc_enabled=false path used to disable every allocation bound, so a long combat
    // on a low-memory host grew without a limit until the next request failed. A default-GC
    // search request must keep a system-memory-derived bound and a reclaim/resume checkpoint
    // while never starting a No-GC region.
    private async Task AssertB013DefaultGcAllocationLimitAsync(CombatState combat, Player player)
    {
        AssertRequiredPotionAuditSelectionAndTotals();
        const long budgetBytes = 1_000_000_000L;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_default_gc_limit_setup",
            forceCollection: true);
        GCLatencyMode initialLatencyMode = GCSettings.LatencyMode;
        SearchMemoryPressureSignal signal = new();
        IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: false,
            budgetBytes,
            signal,
            deadline.Token);
        try
        {
            if (!signal.IsEnabled
                || signal.AllocationLimitBytes <= 0
                || signal.AllocationLimitBytes > budgetBytes
                || signal.SystemMemoryLimitBytes <= 0
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
                || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                throw new InvalidOperationException(
                    $"默认 GC 搜索没有建立系统内存上限：" +
                    $"limit={signal.AllocationLimitBytes} " +
                    $"system_limit={signal.SystemMemoryLimitBytes} " +
                    $"region_budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting}。");
            }

            int generation2Before = GC.CollectionCount(GC.MaxGeneration);
            signal.ReclaimAndContinue(deadline.Token);
            if (!signal.IsEnabled
                || signal.AllocationLimitBytes <= 0
                || signal.ReclaimCount != 1
                || GC.CollectionCount(GC.MaxGeneration) <= generation2Before
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
                || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                throw new InvalidOperationException(
                    $"默认 GC 回收续搜没有保持上限或完成回收：" +
                    $"limit={signal.AllocationLimitBytes} checkpoints={signal.ReclaimCount} " +
                    $"region_budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting}。");
            }
        }
        finally
        {
            scope.Dispose();
        }
        if (GCSettings.LatencyMode != initialLatencyMode || signal.IsEnabled)
        {
            throw new InvalidOperationException(
                $"默认 GC 搜索退出后没有清理作用域信号：" +
                $"latency={GCSettings.LatencyMode} enabled={signal.IsEnabled}。");
        }
        _completedChecks.Add(
            "DefaultGcAllocationLimit:Bounded:SystemLimitReclaimKept:RegionFree:ClearedOnExit");
    }
}
