using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task CheckNoveltyControlsAsync(CombatState combat, Player player,
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured)
    {
        var enemies = combat.Enemies.ToArray();
        var actual = enemies.Select(enemy => CaptureActual(combat, player, enemy)).ToArray();
        var shadow = CaptureKnownRouteRootStates(root, player, enemies);
        var profile = captured.Profile with { MaxExpandedNodes = 4000, SoftTimeBudgetMilliseconds = 30000 };
        var common = captured with { Profile = profile, BudgetOverrideMilliseconds = 30000,
            FixedBudget = true, UseNoveltyPortfolio = true, StopAtAcceptableBattleHpLoss = false,
            MaxDegreeOfParallelism = 2 };
        foreach (bool route in new[] { false, true })
        {
            SearchInteractionState interaction = new();
            bool requested = false;
            SolverRouteAdoptionSeed? seed = null;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            SolverResult adopted = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                common with { Interaction = interaction }, deadline.Token, progress =>
                {
                    if (requested) return;
                    if (route && progress.RouteAdoptionSeed is { } candidate)
                    {
                        seed = candidate;
                        requested = interaction.RequestAdoptRoute(candidate);
                    }
                    else if (!route && progress.CurrentTurnPreview != null)
                        requested = interaction.RequestApplyCurrentTurn();
                }), deadline.Token);
            if (!requested || adopted.BestNode.ActionCount == 0
                || route && (seed == null || !adopted.BestNode.Actions.SequenceEqual(seed.Actions))
                || !route && adopted.ResultScope != SolverResultScope.CurrentTurnAdoption)
                throw new InvalidOperationException("Novelty search failed to honor the displayed-route takeover.");
            _writer.CaptureSolverResult(adopted);
            AssertRoot();
            _completedChecks.Add(route ? "Novelty:AdoptDisplayedRoute:ExactActions" : "Novelty:ApplyCurrentTurn");
        }
        using CancellationTokenSource cancelled = new(TimeSpan.FromSeconds(30));
        SearchRequestWorkTotals work = new();
        bool cancellationRequested = false;
        try
        {
            await Task.Run(() => new CombatBeamSolver(root, names, damage,
                common with { NoveltySearch = new(), RequestWorkTotals = work }, cancelled.Token,
                progress =>
                {
                    if (progress.ExpandedNodes > 0)
                    {
                        cancellationRequested = true;
                        cancelled.Cancel();
                    }
                }, profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve(), cancelled.Token);
            throw new InvalidOperationException("Novelty cancellation did not propagate.");
        }
        catch (OperationCanceledException) when (cancellationRequested && cancelled.IsCancellationRequested)
        {
            SearchRequestWorkSnapshot cancelledWork = work.Snapshot();
            if (cancelledWork.RecordedSolverCount != 1 || cancelledWork.ExpandedNodes <= 0
                || cancelledWork.TransitionCount <= 0)
                throw new InvalidOperationException("Cancelled novelty work was not accounted exactly once.");
            AssertRoot();
            _completedChecks.Add("Novelty:Cancel:WorkCounted:LiveAndShadowRootUnchanged");
        }

        void AssertRoot()
        {
            var after = CaptureKnownRouteRootStates(root, player, enemies);
            for (int i = 0; i < enemies.Length; i++)
            {
                AssertSnapshotEqual(actual[i], CaptureActual(combat, player, enemies[i]), "NoveltyControls", "live_root");
                AssertSnapshotEqual(shadow[i], after[i], "NoveltyControls", "shadow_root");
            }
        }
    }
}
