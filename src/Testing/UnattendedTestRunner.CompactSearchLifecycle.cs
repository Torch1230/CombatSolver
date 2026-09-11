using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactSearchLifecycleAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        bool neurosurge = _request.ScenarioId == "COMPACT-NEUROSURGE-SEARCH";
        bool ostyTurns = _request.ScenarioId == "COMPACT-OSTY-TURN-SEARCH";
        bool osty = ostyTurns || _request.ScenarioId == "COMPACT-OSTY-SEARCH";
        bool drawExhaust = _request.ScenarioId == "COMPACT-DRAW-EXHAUST-SEARCH";
        bool dirge = _request.ScenarioId == "COMPACT-DIRGE-SEARCH";
        bool keywords = _request.ScenarioId == "COMPACT-KEYWORDS-SEARCH";
        bool hang = _request.ScenarioId == "COMPACT-HANG-SEARCH";
        bool costPowers = _request.ScenarioId == "COMPACT-COST-POWERS-SEARCH";
        bool necroCards = _request.ScenarioId == "COMPACT-NECRO-CARDS-SEARCH";
        if (keywords) await PrepareCompactKeywordsAsync(combat, player, 1);
        else if (hang) await PrepareCompactHangAsync(combat, player, 0);
        else if (costPowers) await PrepareCompactCostPowersAsync(combat, player, 0);
        else if (necroCards) await PrepareCompactNecroCardsAsync(combat, player, 0);
        else if (dirge) await PrepareCompactDirgeAsync(combat, player, 0);
        else if (drawExhaust) await PrepareCompactDrawExhaustAsync(combat, player, 0);
        else if (osty)
        {
            await PrepareCompactOstyAsync(combat, player, 0, ostyTurns);
            if (!ostyTurns) await PowerCmd.Apply<ToolsOfTheTradePower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
        }
        else if (neurosurge)
            await PrepareCompactNeurosurgeAsync(combat, player, choices: true);
        else if (player.Deck.Cards.Count != 30 || player.PlayerCombatState!.AllCards.Count() != 30)
            throw new InvalidOperationException("Compact lifecycle requires the original complete 30-card root.");
        var root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var original = CaptureActual(combat, player, combat.Enemies.Single());
        CompactCombatRoot compact;
        using (SimulationNotificationIsolation.Enter())
        {
            if (!CompactCombatRoot.TryCreate(root.ForkSimulator(), player, out var admitted, out string rejected))
                throw new InvalidOperationException($"Compact lifecycle root was rejected: {rejected}");
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
        _completedChecks.Add($"CompactSearchLifecycle:Keywords{keywords}:Hang{hang}:CostPowers{costPowers}:NecroCards{necroCards}:Dirge{dirge}:DrawExhaust{drawExhaust}:Neurosurge{neurosurge}:Osty{osty}:TurnRelic{ostyTurns}:250Nodes:LegacyEqualsCompact:DOP1EqualsDOP2:Concurrency{parallel.MaxParallelExpansionConcurrency}:CancelAndFailureDrained:RootReusable:UnsupportedPotionRejected:ActualUnchanged");

        Task<SolverResult> Solve(SearchPolicySnapshot selectedPolicy, int degree)
            => Task.Run(() => CombatSearchCoordinator.Solve(root, display, damage,
                selectedPolicy with { MaxDegreeOfParallelism = degree }, default, null));
    }
}
