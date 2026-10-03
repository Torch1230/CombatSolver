using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSmartAuditPotionBaselineAsync(CombatState live, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true,
            VerifyIncrementalSearch = true,
            MaxDegreeOfParallelism = 1,
            UseBeamWidthPortfolio = false,
            UseNoveltyPortfolio = false,
            BudgetOverrideMilliseconds = null,
            StopAtAcceptableBattleHpLoss = false,
            IgnoreLongTermRewards = true,
            RelicTargets = [],
            PotionPolicy = SolverPotionPolicy.Smart,
            PotionStrategy = new(SolverPotionPolicy.Smart, []),
        };
        policy = policy with { Profile = policy.Profile with
        {
            BeamWidth = 12,
            MaxExpandedNodes = 800,
            SoftTimeBudgetMilliseconds = 20_000,
            StopPortfolioAtHpTarget = false,
        } };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        SolverResult control = await Task.Run(() => CombatSearchCoordinator.Solve(
            root, names, damage, policy, deadline.Token, null));
        int carriedPotionResults = 0;
        // A continuation preserves the actual potion action but drops the insertion marker.
        // Reproduce that handoff at the real primary-result publication boundary.
        void CarryPotionWithoutInsertionMarker(SolverResult result)
        {
            if (result.ExplicitPotionCount > 0 && result.DeterministicBlockPotionInserted)
            {
                result.DeterministicBlockPotionInserted = false;
                carriedPotionResults++;
            }
        }
        Stopwatch clock = Stopwatch.StartNew();
        SearchBudgetLedger ledger = new(clock, policy);
        SearchPolicySnapshot observed = policy with
        {
            RequestWorkTotals = ledger.WorkTotals,
            PortfolioTelemetry = new BeamWidthPortfolioTelemetry(),
        };
        MethodInfo solveCore = typeof(CombatSearchCoordinator).GetMethod(
            "SolveCore", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CombatSearchCoordinator), "SolveCore");
        SolverResult candidate = await Task.Run(() =>
        {
            try
            {
                return (SolverResult)solveCore.Invoke(null,
                [root, names, damage, observed, ledger, new SearchPlanDiscoveryState(),
                    deadline.Token, null, (Action<SolverResult>)CarryPotionWithoutInsertionMarker,
                    new List<PlanAction[]>(), (Func<SolverResult, SolverResult>)(result => result)])!;
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        });
        if (carriedPotionResults == 0
            || !candidate.Snapshot.AllEnemiesDead || candidate.Snapshot.PlayerDead
            || candidate.BoundaryReason != SearchBoundaryReason.None
            || candidate.ExplicitPotionCount != 1
            || candidate.ProjectedBattleHpLost != control.ProjectedBattleHpLost
            || candidate.ExplicitPotionCount != control.ExplicitPotionCount
            || candidate.CombatEndedTurn != control.CombatEndedTurn
            || candidate.BestNode.Actions.Count(action => action.Kind == PlanActionKind.UsePotion) != 1)
            throw new InvalidOperationException("Smart audit must compare a potion-carrying primary against a fresh potion-free baseline and retain the valid winning route.");
        if (ContinuationStamp.CaptureLive(live).StateText != before
            || root.ContinuationStamp.StateText != before)
            throw new InvalidOperationException("Smart audit changed the native combat or frozen root.");
        _completedChecks.Add($"SmartCarriedPotionResults:{carriedPotionResults}");
        _completedChecks.Add($"SmartAuditQuality:hp={candidate.ProjectedBattleHpLost}/potions={candidate.ExplicitPotionCount}/turn={candidate.CombatEndedTurn}");
    }
}
