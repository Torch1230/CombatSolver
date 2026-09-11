using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactSearchLifecycleAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Deck.Cards.Count != 30 || player.PlayerCombatState!.AllCards.Count() != 30)
            throw new InvalidOperationException("Compact lifecycle requires the original complete 30-card root.");
        var root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var original = CaptureActual(combat, player, combat.Enemies.Single());
        CompactCombatRoot compact;
        using (SimulationNotificationIsolation.Enter())
        {
            if (!CompactCombatRoot.TryCreate(root.ForkSimulator(), player, out var admitted, out string rejected))
                throw new InvalidOperationException($"Original compact root was rejected: {rejected}");
            compact = admitted;
            var potionRoot = root.ForkSimulator();
            if (!((SimulatedCombatState)potionRoot.State.CombatState).TryProcurePotion(player, ModelDb.Potion<FirePotion>()))
                throw new InvalidOperationException("Compact admission fixture could not add its unsupported potion.");
            if (CompactCombatRoot.TryCreate(potionRoot, player, out _, out rejected)
                || rejected != "Compact combat has unrepresented potion effects.")
                throw new InvalidOperationException("Unsupported potion root was not explicitly assigned to the old backend.");
        }
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        policy = policy with
        {
            ShortProfile = policy.ShortProfile with { MaxExpandedNodes = 250, SoftTimeBudgetMilliseconds = 120_000 },
            ForceShortOnly = true,
            VerifyIncrementalSearch = false,
            DetailedDiagnostics = false,
            MeasurePhasePerformance = false,
            ShortBudgetOverrideMilliseconds = null,
            DeepBudgetOverrideMilliseconds = null
        };
        var compactPolicy = policy with { CompactRoot = compact };
        await AssertParallelExpansionFailureDrainAsync(root, display, damage, compactPolicy);
        var baseline = await Solve(policy, 1);
        var serial = await Solve(compactPolicy, 1);
        var parallel = await Solve(compactPolicy, 2);
        AssertEquivalentSearchResults(baseline, serial, "Compact lifecycle old/new DOP1");
        AssertEquivalentSearchResults(serial, parallel, "Compact lifecycle DOP1/DOP2 after cancel/error");
        if (parallel.MaxParallelExpansionConcurrency < 2 || parallel.ParallelExpansionWorkItems < 2
            || serial.MaxParallelExpansionConcurrency != 0 || compact.Counts.PendingReplays == 0)
            throw new InvalidOperationException("Compact lifecycle failed to exercise concurrent and suspended candidates.");
        AssertSnapshotEqual(original, CaptureActual(combat, player, combat.Enemies.Single()), "CompactLifecycle", "ActualUnchanged");
        _completedChecks.Add($"CompactSearchLifecycle:250Nodes:LegacyEqualsCompact:DOP1EqualsDOP2:Concurrency{parallel.MaxParallelExpansionConcurrency}:CancelAndFailureDrained:RootReusable:UnsupportedPotionRejected:ActualUnchanged");

        Task<SolverResult> Solve(SearchPolicySnapshot selectedPolicy, int degree)
            => Task.Run(() => CombatSearchCoordinator.Solve(root, display, damage,
                selectedPolicy with { MaxDegreeOfParallelism = degree }, default, null));
    }
}
