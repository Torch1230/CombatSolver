using CombatSolver;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace OfflineSearchHarness;

internal static class OutcomeCacheChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Outcome cache: " + name);
            checks++;
        }
        PlanAction action = new(PlanActionKind.PlayCard, 1, CardId: "A", TargetCombatId: 1,
            CardStateKey: "physical-state", CardTitle: "title");
        SolverInterimResult quality = new(true, 0, 5, 5, 0, 0, 0, 123, 3) { Survives = true };
        SearchCompletedOutcome witness = new(action, quality, false, false, SearchBoundaryReason.None);
        RootOutcomeCache cache = new();
        cache.Observe(witness);
        Require(cache.HasWitnessAtLeastAsGood(action, quality), "equal witness reused");
        Require(cache.HasWitnessAtLeastAsGood(action with { CardTitle = "another title" }, quality), "display ignored");
        Require(!cache.HasWitnessAtLeastAsGood(action with { TargetCombatId = 2 }, quality), "target identity");
        Require(!cache.HasWitnessAtLeastAsGood(action with { CardStateOccurrence = 1 }, quality), "physical occurrence");
        Require(!new RootOutcomeCache().HasWitnessAtLeastAsGood(action, quality), "root isolation");
        cache.Observe(witness with { Quality = quality with { ProjectedBattleHpLost = 8, StrategicHpDeficit = 8 } });
        Require(cache.Best.Values.Single().ProjectedBattleHpLost == 5, "worse witness does not overwrite");
        cache.Observe(witness with { Quality = quality with { ProjectedBattleHpLost = 2, StrategicHpDeficit = 2 } });
        Require(cache.Best.Values.Single().ProjectedBattleHpLost == 2, "better witness backed up");
        Require(cache.Best.Values.Single().Score == 0, "legacy score excluded");
        foreach (SearchCompletedOutcome invalid in new[]
        {
            witness with { HasRootChoices = true }, witness with { HasPredictionRisk = true },
            witness with { BoundaryReason = SearchBoundaryReason.TimeLimit },
            witness with { Quality = quality with { Won = false } },
        })
        {
            RootOutcomeCache rejected = new(); rejected.Observe(invalid);
            Require(rejected.Best.Count == 0, "unknown/risky outcome remains unknown");
        }
        PlanCardChoice choice = new(PlanChoiceEffect.Discard, PileType.Hand,
            [new("TOKEN", 0, "state", 0, 0, "title")]);
        PlanAction chosen = action with { Choice = choice };
        Require(RootOutcomeCache.ActionKey(chosen) == RootOutcomeCache.ActionKey(chosen with
            { Choice = choice with { Cards = [choice.Cards[0] with { Title = "translated" }] } }), "choice display ignored");
        Require(RootOutcomeCache.ActionKey(chosen) != RootOutcomeCache.ActionKey(chosen with
            { Choice = choice with { Cards = [choice.Cards[0] with { StateKey = "other" }] } }), "choice identity retained");
        PlanAction[] scheduled = cache.Schedule([action, action with { CardId = "B" },
            action with { CardId = "B", CardOccurrence = 1 }, action with { CardId = "C" }], 3);
        Require(scheduled.Select(a => a.CardId).SequenceEqual(["A", "B", "C"]), "incumbent retained; unknown explored; groups diversified");
        Require(OutcomeProbes.IsUsableWitness(quality, false, SearchBoundaryReason.None,
            SearchBoundaryReason.NodeLimit, SolverResultScope.SearchCompletion, true), "complete victory survives search node cap");
        Require(!OutcomeProbes.IsUsableWitness(quality with { Won = false }, false, SearchBoundaryReason.None,
            SearchBoundaryReason.NodeLimit, SolverResultScope.SearchCompletion, true), "unfinished node cap is not a witness");
        Require(!OutcomeProbes.IsUsableWitness(quality, false, SearchBoundaryReason.PendingChoice,
            SearchBoundaryReason.NodeLimit, SolverResultScope.SearchCompletion, true), "snapshot boundary cannot be masked");
        Require(!OutcomeProbes.IsUsableWitness(quality, true, SearchBoundaryReason.None,
            SearchBoundaryReason.NodeLimit, SolverResultScope.SearchCompletion, true), "risk cannot be masked");
        Require(!OutcomeProbes.IsUsableWitness(quality, false, SearchBoundaryReason.None,
            SearchBoundaryReason.None, SolverResultScope.CurrentTurnAdoption, true), "partial adoption is not a completed route");
        Require(!OutcomeProbes.IsUsableWitness(quality, false, SearchBoundaryReason.None,
            SearchBoundaryReason.MemoryNoProgress, SolverResultScope.SearchCompletion, true), "memory truncation is excluded");
        Require(!OutcomeProbes.IsUsableWitness(quality, false, SearchBoundaryReason.None,
            SearchBoundaryReason.NodeLimit, SolverResultScope.SearchCompletion, false), "legacy probe rule preserved");
        bool rejectedSmart = false;
        try
        {
            HarnessOptions.Parse(["--search-mode", "Coordinator", "--outcome-probes", "4", "--selective-outcome-probes"]);
        }
        catch (ArgumentException) { rejectedSmart = true; }
        Require(rejectedSmart, "coordinator probes cannot bypass potion audits");
        Require(HarnessOptions.Parse(["--search-mode", "Coordinator", "--outcome-probes", "4",
            "--selective-outcome-probes", "--potion-policy", "Disabled"]).SelectiveOutcomeProbes,
            "disabled-potion coordinator experiment is accepted");
        RootOutcomeCache bounded = new();
        for (int i = 0; i < RootOutcomeCache.MaximumEvents + 10; i++)
            bounded.Observe(witness with { FirstAction = action with { CardOccurrence = i } });
        Require(bounded.Events == RootOutcomeCache.MaximumEvents && !bounded.Observer.WantsObservation(), "event bound");
        Require(bounded.Best.Count == RootOutcomeCache.MaximumActions, "entry bound");
        Console.WriteLine($"Outcome cache: {checks} assertions passed.");
        return 0;
    }
}
