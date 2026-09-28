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
        Require(HarnessOptions.Parse(["--search-mode", "Coordinator", "--dop", "1",
            "--potion-policy", "Disabled", "--observe-ordering", "4000"]).OrderingObservationLimit == 4000,
            "bounded serial coordinator observation is accepted");
        foreach (string[] invalid in new[]
        {
            new[] { "--search-mode", "Coordinator", "--observe-ordering", "4000" },
            new[] { "--search-mode", "Coordinator", "--dop", "2", "--potion-policy", "Disabled", "--observe-ordering", "4000" },
            new[] { "--observe-ordering", "100001" },
            new[] { "--observe-ordering", "10", "--observe-ordering-states-only" },
        })
        {
            bool rejected = false;
            try { HarnessOptions.Parse(invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "coordinator observation preserves its policy and resource bounds");
        }
        string orderingChecks = Path.Combine(Path.GetTempPath(), "ordering-contract-" + Guid.NewGuid());
        Directory.CreateDirectory(orderingChecks);
        try
        {
            string watchedFile = Path.Combine(orderingChecks, "states.json");
            StateFingerprint watchedKey = new(1, 2), absentKey = new(3, 4);
            File.WriteAllText(watchedFile, System.Text.Json.JsonSerializer.Serialize(new[] { watchedKey }));
            using var observations = new OrderingObservations(orderingChecks, 4, watchedFile, watchedOnly: true);
            Require(observations.Observer.WantsState(watchedKey) && !observations.Observer.WantsState(absentKey),
                "focused observations preserve exact watched-state membership");
            Require(observations.Observer.WantsRetentionPool(watchedKey)
                && !observations.Observer.WantsRetentionPool(absentKey),
                "focused diagnostics copy only pools containing the watched state");
        }
        finally { Directory.Delete(orderingChecks, recursive: true); }
        RootOutcomeCache bounded = new();
        for (int i = 0; i < RootOutcomeCache.MaximumEvents + 10; i++)
            bounded.Observe(witness with { FirstAction = action with { CardOccurrence = i } });
        Require(bounded.Events == RootOutcomeCache.MaximumEvents && !bounded.Observer.WantsObservation(), "event bound");
        Require(bounded.Best.Count == RootOutcomeCache.MaximumActions, "entry bound");
        SolverSearchProfile profile = SolverSearchProfile.Default with
            { MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 1000 };
        Require(AutomaticSearchBudget.Remaining(profile, 0, 100) == null, "exhausted nodes cannot get a reserve");
        Require(AutomaticSearchBudget.Remaining(profile, 1000, 0) == null, "exhausted time cannot get a reserve");
        Require(AutomaticSearchBudget.Remaining(profile, 1200, 120) == null, "soft deadline overrun does not restart a member");
        var shared = AutomaticSearchBudget.Remaining(profile, 100, 40, 80, 4)!;
        Require(shared.MaxExpandedNodes == 10 && shared.SoftTimeBudgetMilliseconds == 225,
            "actual work, refinement limit and remaining members share allowance");
        var clamped = AutomaticSearchBudget.Remaining(profile, 0, 40, 10000)!;
        Require(clamped.MaxExpandedNodes == 60, "optional refinement cap cannot enlarge request");
        int calls = 0;
        var cappedPower = BeamWidthPortfolio.Run(
            new BeamWidthPortfolioMemberSpec[] { new(24), new(24, AggressivePowerCommitment: true) },
            100, profile, member =>
            {
                calls++;
                return new BeamWidthPortfolioRun<int>(1, 100, 200, "NodeLimit", true, true, 5, 0);
            }, (a, b) => false, allowDedicatedPowerReserve: false);
        Require(calls == 1 && cappedPower.TotalExpandedNodes == 100,
            "power member cannot restart exhausted automatic node budget");
        bool invalidAuto = false;
        try { HarnessOptions.Parse(["--automatic-search", "--search-mode", "Coordinator", "--novelty-portfolio"]); }
        catch (ArgumentException) { invalidAuto = true; }
        Require(invalidAuto, "automatic scheduler cannot stack old novelty mode");
        Require(HarnessOptions.Parse(["--automatic-search", "--search-mode", "Coordinator"]).UseAutomaticSearch,
            "automatic coordinator accepted without old switches");
        Console.WriteLine($"Outcome cache: {checks} assertions passed.");
        return 0;
    }
}
