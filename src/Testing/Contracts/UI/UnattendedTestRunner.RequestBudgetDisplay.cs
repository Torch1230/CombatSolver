using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRequestBudgetDisplayAsync(CombatState live, Player player)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 3 });
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Draw", Count = 5 });
        var root = CombatRootSnapshot.Capture(live);
        var names = SolverDisplayNames.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            Profile = SolverSearchProfile.Default with { MaxExpandedNodes = 3, SoftTimeBudgetMilliseconds = 5000 },
            FixedBudget = true, BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false, UseBeamWidthPortfolio = false, UseBossTempoSearch = false,
            StopAtAcceptableBattleHpLoss = false,
        };
        var result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
            policy, CancellationToken.None, null));
        if (result.BoundaryReason != SearchBoundaryReason.NodeLimit
            || result.RequestBudget?.LimitReason != SearchBoundaryReason.NodeLimit
            || result.RequestBudget.ExpandedNodes != result.TotalExpandedNodes)
            throw new InvalidOperationException("Coordinator did not attach the exhausted request budget.");
        int memberNodes = result.ExpandedNodes;
        var originalPlan = result.BestNode;
        var profile = policy.Profile with { MaxExpandedNodes = 500000, SoftTimeBudgetMilliseconds = 300000 };
        var ledger = new SearchBudgetLedger(new Stopwatch(), policy with { Profile = profile });
        ledger.WorkTotals.Record(default(SearchSolverWorkContribution) with { ExpandedNodes = 2500 },
            directSearchPurpose: DirectSearchPurpose.NoveltyExploration);
        ledger.WorkTotals.Record(default(SearchSolverWorkContribution) with { ExpandedNodes = 8012 },
            directSearchPurpose: DirectSearchPurpose.PrimaryBeam);
        result.TotalExpandedNodes = 10512;
        result.RequestBudget = ledger.Capture(profile);
        string language = LocManager.Instance.Language;
        try
        {
            foreach (string locale in new[] { "zhs", "zht", "eng" })
            {
                LocManager.Instance.SetLanguage(locale);
                result.RequestBudget = ledger.Capture(profile);
                var display = SolverOverlaySnapshot.Capture(result, false);
                if (display.SearchLimitWarningText != null || !display.DetailsText.Contains("10512")
                    || display.DetailsText.Contains(locale == "eng" ? "Node limit" : "节点上限"))
                    throw new InvalidOperationException($"Member limit leaked into request display: {locale}.");
                result.RequestBudget = new(500000, 500000, 2617, 300000);
                display = SolverOverlaySnapshot.Capture(result, false);
                if (display.SearchLimitWarningText != SolverOverlaySnapshot.BuildSearchLimitWarning(SearchBoundaryReason.NodeLimit))
                    throw new InvalidOperationException($"Request node limit was hidden: {locale}.");
                result.RequestBudget = new(10512, 500000, 300000, 300000);
                display = SolverOverlaySnapshot.Capture(result, false);
                if (display.SearchLimitWarningText != SolverOverlaySnapshot.BuildSearchLimitWarning(SearchBoundaryReason.TimeLimit))
                    throw new InvalidOperationException($"Request time limit was hidden: {locale}.");
            }
            var expired = new SearchBudgetLedger(new Stopwatch(), policy with { Profile = profile, BudgetOverrideMilliseconds = 0 });
            if (expired.Capture(profile).LimitReason != SearchBoundaryReason.TimeLimit)
                throw new InvalidOperationException("Request time override was omitted from reporting.");
            result.RequestBudget = null;
            if (SolverOverlaySnapshot.SearchBoundaryForDisplay(result) != SearchBoundaryReason.NodeLimit
                || result.ExpandedNodes != memberNodes || !ReferenceEquals(originalPlan, result.BestNode))
                throw new InvalidOperationException("Request reporting changed the selected solver or route.");
        }
        finally { LocManager.Instance.SetLanguage(language); }
        _completedChecks.Add("RequestBudgetDisplay:Coordinator:MemberLimit:RequestNodeLimit:RequestTimeLimit:Override:TotalNodes:ZhsZhtEng:SelectedRoutePreserved");
    }
}
