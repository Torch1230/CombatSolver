using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunSolvedRouteCacheAsync(CombatState combat, Player player, bool restoreOnly)
    {
        NGame host = NGame.Instance!;
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, SolverController.ResolveTheftPolicy(combat));
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolvedRouteCache cache = SolvedRouteCache.Capture(combat, root, policy, damage);
        if (restoreOnly)
        {
            if (cache.Read(root.Forecast) == null)
                throw new InvalidOperationException($"上一个游戏进程记录的路线不存在：{cache.Path}");
        }
        else
        {
            if (File.Exists(cache.Path))
                File.Delete(cache.Path);
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            SolverResult original = await AwaitResultAsync();
            if (original.WasRestoredFromCache)
                throw new InvalidOperationException("手动重算使用了缓存。");
            IntentForecast freshForecast = CombatRootSnapshot.Capture(combat).Forecast;
            SolverResult copy = new SolvedRouteCache(cache.Path).Read(freshForecast)
                ?? throw new InvalidOperationException("首次路线没有写入磁盘。");
            if (ReferenceEquals(original.BestNode, copy.BestNode)
                || !ReferenceEquals(copy.Forecast, freshForecast)
                || SerializePlan(original) != SerializePlan(copy))
                throw new InvalidOperationException("路线、预测或选择在写盘恢复后发生变化。");
            byte[] firstBytes = File.ReadAllBytes(cache.Path);
            cache.StoreFirst(original);
            if (!firstBytes.SequenceEqual(File.ReadAllBytes(cache.Path)))
                throw new InvalidOperationException("后续结果覆盖了首次路线记录。");
            if (SolvedRouteCache.Capture(combat, root,
                    policy with { AcceptableBattleHpLoss = policy.AcceptableBattleHpLoss + 1 }, damage).Path == cache.Path)
                throw new InvalidOperationException("路线缓存没有隔离策略变化。");
            if (SolvedRouteCache.Capture(combat, root,
                    policy with { UseBeamWidthPortfolio = !policy.UseBeamWidthPortfolio }, damage).Path == cache.Path)
                throw new InvalidOperationException("路线缓存没有隔离多宽度精炼设置变化。");
            if (SolvedRouteCache.Capture(combat, root,
                    policy with { UseNoveltyPortfolio = !policy.UseNoveltyPortfolio }, damage).Path == cache.Path)
                throw new InvalidOperationException("路线缓存没有隔离多策略搜索设置变化。");
            int hp = player.Creature.CurrentHp;
            await CreatureCmd.SetCurrentHp(player.Creature, hp - 1);
            CombatRootSnapshot changed = CombatRootSnapshot.Capture(combat);
            if (SolvedRouteCache.Capture(combat, changed, policy, damage).Path == cache.Path)
                throw new InvalidOperationException("路线缓存没有隔离真实战斗状态变化。");
            await CreatureCmd.SetCurrentHp(player.Creature, hp);
            _completedChecks.Add("SolvedRouteDiskCopyAndIdentity");
        }

        SolverController.Reset("route_cache_reload_test");
        await SolverController.LastCombatReferenceReleaseForTesting;
        SolverController.BeginCombat(combat);
        SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
        SolverResult restored = await AwaitResultAsync();
        if (!restored.WasRestoredFromCache
            || !SolverController.ReplanAuditForBugReport.Contains("searches=0", StringComparison.Ordinal))
            throw new InvalidOperationException("战斗会话重建后没有直接恢复路线。");
        _completedChecks.Add(restoreOnly ? "SolvedRouteNewProcessRestore" : "SolvedRouteSessionResetRestore");
        if (!restoreOnly)
        {
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            if ((await AwaitResultAsync()).WasRestoredFromCache)
                throw new InvalidOperationException("已有缓存时手动重算没有启动新搜索。");
            _completedChecks.Add("SolvedRouteManualRecalculation");
        }
        if (restoreOnly)
        {
            _protocolHost.EnableAutomaticTurnSearch();
            SolverController.SetFullAuto(host, combat, true);
            long deadline = Environment.TickCount64 + 30_000;
            while (CombatManager.Instance.IsInProgress)
            {
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("缓存路线没有在 30 秒内完成部署。");
                if (SolverController.IsSearching)
                    throw new InvalidOperationException("缓存路线部署期间触发了额外搜索。");
                await NextFrameAsync();
            }
            if (player.Creature.CurrentHp <= 0 || SolverController.LastSearchFailureForTesting != null)
                throw new InvalidOperationException("缓存路线部署失败。");
            _completedChecks.Add("SolvedRouteRestoredDeployment");
        }

        async Task<SolverResult> AwaitResultAsync()
        {
            long deadline = Environment.TickCount64 + 30_000;
            while (SolverController.LastCompletedResultForTesting == null || SolverController.IsSearching)
            {
                if (SolverController.LastSearchFailureForTesting is { } failure)
                    throw new InvalidOperationException($"缓存测试搜索失败：{failure}");
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("缓存测试等待结果超过 30 秒。");
                await NextFrameAsync();
            }
            return SolverController.LastCompletedResultForTesting;
        }
    }

    private static string SerializePlan(SolverResult result) => JsonSerializer.Serialize(new
    {
        result.ResultScope, result.StartTurnNumber, result.TurnSetupChoices, result.TurnSetupPlayState,
        result.BestNode, result.Snapshot, result.Continuations, result.KillsAfterAction,
        result.HpLostByTurn, result.HpRecoveredByTurn, result.PotionCountByTurn,
        result.PotionHpSaved, result.PotionHpRequired, result.PostCombatRelicHeal,
        result.CombatEndedTurn, result.ProjectedBattleHpLost,
    });
}
