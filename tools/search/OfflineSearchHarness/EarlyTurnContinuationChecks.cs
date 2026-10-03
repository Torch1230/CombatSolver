using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>Pure early-turn incumbent contracts; no game state or search startup.</summary>
internal static class EarlyTurnContinuationChecks
{
    internal static int Run()
    {
        int checks = 0;
        PrimarySearchIncumbent original = new(12, 4);

        // Cover every combination of policy gates with both an available incumbent
        // and the absent incumbent used by growth, relic, revival and incomplete results.
        for (int hasIncumbent = 0; hasIncumbent <= 1; hasIncumbent++)
        {
            for (int flags = 0; flags < 64; flags++)
            {
                bool preserveResources = (flags & 1) != 0;
                bool selectedForcedUsesSatisfied = (flags & 2) != 0;
                bool selectedHasExplicitPotionUses = (flags & 4) != 0;
                bool prefixUsesPotion = (flags & 8) != 0;
                bool selectedHasStrategicCredit = (flags & 16) != 0;
                bool selectedHasRisk = (flags & 32) != 0;
                PrimarySearchIncumbent? selected =
                    CombatSearchCoordinator.SelectEarlyTurnContinuationIncumbent(
                        hasIncumbent == 1 ? original : null,
                        preserveResources,
                        selectedForcedUsesSatisfied,
                        selectedHasExplicitPotionUses,
                        prefixUsesPotion,
                        selectedHasStrategicCredit,
                        selectedHasRisk);
                bool eligible = hasIncumbent == 1
                    && !preserveResources
                    && selectedForcedUsesSatisfied
                    && !selectedHasStrategicCredit
                    && !selectedHasRisk
                    && (!selectedHasExplicitPotionUses || prefixUsesPotion);
                Require(selected.HasValue == eligible,
                    $"Policy gates: incumbent={hasIncumbent}, flags={flags}");
                if (selected is { } bound)
                {
                    Require(bound.StrategicHpDeficit == original.StrategicHpDeficit,
                        $"Strategic HP preserved: flags={flags}");
                    Require(bound.CombatEndedTurn == int.MaxValue,
                        $"Equal-HP secondary objectives preserved: flags={flags}");
                }
            }
        }

        PrimarySearchIncumbent conservative =
            CombatSearchCoordinator.SelectEarlyTurnContinuationIncumbent(
                original,
                preserveResources: false,
                selectedForcedUsesSatisfied: true,
                selectedHasExplicitPotionUses: true,
                prefixUsesPotion: true,
                selectedHasStrategicCredit: false,
                selectedHasRisk: false)
            ?? throw new InvalidOperationException("Qualified potion prefix lost its incumbent.");
        Require(original.CombatEndedTurn == 4,
            "Selecting the bound does not mutate the original incumbent");
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(13, 1, conservative),
            "Strictly worse strategic HP is pruned");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(12, 5, conservative),
            "Equal strategic HP at a later turn survives");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(12, int.MaxValue, conservative),
            "Equal strategic HP survives at the maximum turn");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(11, int.MaxValue, conservative),
            "Strictly better strategic HP survives");
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(12, 5, original),
            "The original incumbent would prune the equal-HP later-turn branch");

        PrimarySearchIncumbent healingBound = conservative with { StrategicHpDeficit = -3 };
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(-2, 1, healingBound),
            "Negative strategic HP remains a strict comparison");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(-3, 5, healingBound),
            "Equal negative strategic HP survives");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(-4, 5, healingBound),
            "Better negative strategic HP survives");

        Console.WriteLine(
            $"EARLY_TURN_CONTINUATION_BOUND_CHECKS status=Passed assertions={checks}");
        return 0;

        void Require(bool passed, string name)
        {
            if (!passed)
                throw new InvalidOperationException("Early-turn continuation contract: " + name);
            checks++;
        }
    }
}
