using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertShowcaseRouteImport(CombatState combatState)
    {
        string routePath = _request.ShowcaseRoutePath
            ?? throw new InvalidOperationException("录像路线导入夹具缺少 ShowcaseRoutePath。");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(routePath));
        JsonElement route = document.RootElement;
        byte[] serializedResult = Convert.FromBase64String(RequiredString(route, "solverResult"));
        IntentForecast forecast = CombatRootSnapshot.Capture(combatState).Forecast;
        SolverResult result = SolvedRouteCache.DeserializeRoute(serializedResult, forecast);
        if (result.StartTurnNumber != route.GetProperty("startTurnNumber").GetInt32()
            || result.CombatEndedTurn != route.GetProperty("combatEndedTurn").GetInt32()
            || !result.Snapshot.AllEnemiesDead)
            throw new InvalidDataException("录像包路线摘要与预计算结果不一致。");
        int before = SolverController.SearchesStartedForShowcase;
        SolverController.AcceptShowcaseRoute(_host, combatState, result);
        int localSearchStarts = SolverController.SearchesStartedForShowcase - before;
        if (localSearchStarts != 0)
            throw new InvalidOperationException("录像路线导入夹具意外启动本地搜索。");
        _completedChecks.Add($"ShowcaseRouteImport:Actions={result.BestNode.Actions.Count}:LocalSearches=0");
    }

    private async Task ReturnShowcaseToMainMenuThroughTerminalRewardsAsync()
    {
        NRewardsScreen? rewards = null;
        long deadline = System.Environment.TickCount64 + 15_000;
        while (rewards?.IsComplete != true)
        {
            EnsureWithinDeadline();
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("录像对局结束后 15 秒内没有显示可前进的原生终端奖励页。");
            rewards = NOverlayStack.Instance?.GetChildren().OfType<NRewardsScreen>().SingleOrDefault();
            await NextFrameAsync();
        }

        NProceedButton proceed = rewards.GetNode<NProceedButton>("ProceedButton");
        if (!proceed.IsEnabled)
            throw new InvalidOperationException("录像对局原生终端前进按钮未启用。");
        proceed.EmitSignal(NClickableControl.SignalName.Released, proceed);

        deadline = System.Environment.TickCount64 + 15_000;
        while (RunManager.Instance.IsInProgress || _host.MainMenu == null || _host.Transition.InTransition)
        {
            EnsureWithinDeadline();
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("录像对局点击原生终端前进按钮后 15 秒内没有完成主菜单转场。");
            await NextFrameAsync();
        }
        if (RunManager.Instance.debugAfterCombatRewardsOverride != null)
            throw new InvalidOperationException("录像对局返回主菜单后仍残留终端奖励覆盖入口。");
        _completedChecks.Add("ShowcaseTerminalProceed:NativeButton:MainMenu:NativeTransition");
    }
}
