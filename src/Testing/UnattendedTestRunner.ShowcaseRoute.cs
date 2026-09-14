using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;

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
}
