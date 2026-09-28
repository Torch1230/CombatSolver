using CombatSolver;

namespace OfflineSearchHarness;

internal static class OutcomeCorrectionSamplingChecks
{
    private sealed record Sample(int Turn, int Depth, int Competition, int Slot);

    internal static void Run(Action<bool, string> check)
    {
        var bounded = new SearchCorrectionSampler<Sample>(17);
        int captures = 0;
        for (int turn = 2; turn <= 4; turn++)
            bounded.Offer(turn, 0, slot => { captures++; return new(turn, 0, 0, slot); });
        check(bounded.Count == 3 && captures == 3
            && Enumerable.Range(0, 3).All(slot => bounded[slot].Turn == slot + 2 && bounded[slot].Slot == slot),
            "corrective slots retain one detached competition for each of the first three observed turns");
        check(!bounded.Offer(5, 0, _ => throw new InvalidOperationException("Fourth turn was captured"))
            && bounded.Count == 3 && !bounded.CanObserve(5) && bounded.CanObserve(3),
            "later turns cannot expand the six-query bound; existing turns can replace their samples");

        bool reproducible = true, densityIndependent = true, selectedSamplesMatch = true;
        int deepestSelected = 0, shallowSelected = 0, representativeReplacements = 0;
        for (int seed = 0; seed < 128; seed++)
        {
            var sparse = new SearchCorrectionSampler<Sample>(seed);
            var dense = new SearchCorrectionSampler<Sample>(seed);
            var repeated = new SearchCorrectionSampler<Sample>(seed);
            for (int depth = 0; depth <= 6; depth++)
            {
                sparse.Offer(2, depth, slot => new(2, depth, 0, slot));
                for (int competition = 0; competition < (depth == 0 ? 200 : 5); competition++)
                {
                    bool first = dense.Offer(2, depth, slot => new(2, depth, competition, slot));
                    bool again = repeated.Offer(2, depth, slot => new(2, depth, competition, slot));
                    reproducible &= first == again && dense[0] == repeated[0];
                }
            }
            var denseState = dense.Describe()[0];
            densityIndependent &= denseState.SelectedDepth == sparse.Describe()[0].SelectedDepth;
            selectedSamplesMatch &= dense[0].Depth == denseState.SelectedDepth
                && denseState.DistinctDepths == 7 && denseState.CompetitionsByDepth[0] == 200;
            if (denseState.SelectedDepth == 6) deepestSelected++;
            if (denseState.SelectedDepth == 0) shallowSelected++;
            if (dense[0].Competition > 0) representativeReplacements++;
        }
        check(reproducible, "same local seed and competition stream reproduce both replacement events and final samples");
        check(densityIndependent, "repeated shallow competitions do not alter which action depth is selected");
        check(selectedSamplesMatch, "selected payloads and exported depth counters describe the actual observed competition");
        check(deepestSelected > 0 && shallowSelected > 0,
            "both turn-start and later within-turn depths retain selection opportunities across fixed seeds");
        check(representativeReplacements > 0,
            "competition sampling can replace the first candidate within an already selected depth");

        var copied = bounded.Describe();
        copied[0].CompetitionsByDepth[0] = 999;
        check(bounded.Describe()[0].CompetitionsByDepth[0] == 1,
            "exported sampling metadata cannot mutate the live reservoir");
        check(bounded.Offer(2, SearchWitnessPrefix.MaximumActions, slot => new(2, 96, 0, slot))
                || bounded.Describe()[0].DistinctDepths == 2,
            "maximum bounded witness depth remains a legitimate sampling stratum");
        Reject(() => bounded.Offer(0, 0, slot => new(0, 0, 0, slot)));
        Reject(() => bounded.Offer(2, -1, slot => new(2, -1, 0, slot)));
        Reject(() => bounded.Offer(2, SearchWitnessPrefix.MaximumActions + 1, slot => new(2, 97, 0, slot)));

        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "invalid correction turn/depth is explicitly rejected");
        }
    }
}
