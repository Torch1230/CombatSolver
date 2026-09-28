using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // Independent six-row storage prevents the ordinary observation/group caps
    // from excluding late selected queries. No node or simulator is retained.
    private sealed record CorrectionPoint(ObservationKey Key, CorrectionQuery Query, Observation Observation);
    private sealed record CorrectionPair(CorrectionPoint Retained, CorrectionPoint Dropped);
    private SearchCorrectionSampler<CorrectionPair>? _corrections;

    internal sealed record CorrectionQuery(SearchWitnessPrefix Replay, StateFingerprint State, int HpCost, int PotionCost)
    {
        internal PlanAction[] Prefix => Replay.Actions;
        internal int OrdinaryStatesAtSelection { get; init; }
        internal int OrdinaryPoolsAtSelection { get; init; }
    }

    internal void ObserveCorrectionBoundary(IReadOnlyList<SearchNode> pool, IReadOnlyList<SearchNode> selected,
        Player player, SearchOutcomeValueModel predictor)
    {
        if (_frozen || _correcting) return;
        var retained = selected.LastOrDefault(Eligible);
        if (retained == null || _corrections?.CanObserve(retained.Turn) == false) return;
        int depth = CurrentTurnDepth(retained);
        var selectedKeys = selected.Select(Key).ToHashSet();
        var dropped = pool.Where(n => Eligible(n) && n.Turn == retained.Turn && n.ActionCount == retained.ActionCount
                && CurrentTurnDepth(n) == depth && !selectedKeys.Contains(Key(n)))
            .OrderByDescending(n => predictor.PredictPriority(n, player)).FirstOrDefault();
        if (dropped == null) return;
        if (_corrections == null)
        {
            SearchNode origin = retained;
            while (origin.Parent is { } parent) origin = parent;
            var key = origin.StateKey;
            int seed = unchecked((int)(key.First ^ (key.First >> 32) ^ key.Second ^ (key.Second >> 32)));
            _corrections = new(seed);
        }
        _corrections.Offer(retained.Turn, depth, slot => new(
            CaptureCorrection(retained, player, slot), CaptureCorrection(dropped, player, slot)));

        static bool Eligible(SearchNode node)
        {
            if (node.ActionCount is < 1 or > SearchWitnessPrefix.MaximumActions || node.IsTerminal || node.HasPredictionRisk
                || node.BoundaryReason != SearchBoundaryReason.None || !node.Snapshot.HasSimulator) return false;
            for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
                if (cursor.TurnSetupChoices is { Count: > 0 }) return false;
            return true;
        }
    }

    private static int CurrentTurnDepth(SearchNode node)
    {
        int depth = 0;
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
            if (cursor.Action is { } action && action.Turn == node.Turn) depth++;
        return depth;
    }

    private CorrectionPoint CaptureCorrection(SearchNode node, Player player, int group)
    {
        var key = Key(node);
        var observation = new Observation(Features(node, player));
        observation.Groups.Add(group);
        if (_observations.TryGetValue(key, out var prior) && prior.Outcome is { } outcome)
            UpdateWitness(observation, outcome, prior.RemainingActions);
        // A replacement can retain an already-labelled endpoint. Preserve its
        // best feasible witness without joining unrelated feature-equal states.
        if (_corrections != null)
            for (int slot = 0; slot < _corrections.Count; slot++)
            {
                // During the first offer, the new turn's slot has no value yet.
                if (_corrections.GetSample(slot) is not { } pair) continue;
                CopyPrior(pair.Retained); CopyPrior(pair.Dropped);
            }
        return new(key, new(SearchWitnessPrefix.Capture(node), key.State, key.HpCost, key.PotionCost)
        {
            OrdinaryStatesAtSelection = _observations.Count, OrdinaryPoolsAtSelection = _groups,
        }, observation);

        void CopyPrior(CorrectionPoint point)
        {
            if (point.Key == key && point.Observation.Outcome is { } previous)
                UpdateWitness(observation, previous, point.Observation.RemainingActions);
        }
    }

    internal CorrectionQuery[] BeginCorrections()
    {
        _correcting = true;
        return CorrectionPoints().Select(point => point.Query).ToArray();
    }

    internal SolverInterimResult? CorrectionWitness(CorrectionQuery query)
    {
        var key = new ObservationKey(query.State, query.HpCost, query.PotionCost);
        return CorrectionPoints().First(point => point.Key == key).Observation.Outcome;
    }

    private IEnumerable<CorrectionPoint> CorrectionPoints()
    {
        if (_corrections == null) yield break;
        for (int slot = 0; slot < _corrections.Count; slot++)
        {
            yield return _corrections[slot].Retained;
            yield return _corrections[slot].Dropped;
        }
    }

    private void ObserveCorrectionWitness(ObservationKey key, SolverInterimResult quality, int actions)
    {
        if (_corrections == null) return;
        for (int slot = 0; slot < _corrections.Count; slot++)
        {
            var pair = _corrections[slot];
            if (pair.Retained.Key == key) UpdateWitness(pair.Retained.Observation, quality, actions);
            if (pair.Dropped.Key == key) UpdateWitness(pair.Dropped.Observation, quality, actions);
        }
    }

    internal TrainingRow[] ExportCorrectionRows() => CorrectionPoints().Where(p => p.Observation.Outcome != null)
        .Select(p => new TrainingRow(p.Observation.Features, p.Observation.Outcome!, p.Observation.RemainingActions,
            p.Observation.Groups.ToArray(), !p.Observation.Outcome!.Won, FeatureSchema)).ToArray();

    internal object DescribeCorrectionSampling() => new
    {
        strategy = "turn-depth-reservoir", maximumQueries = SearchCorrectionSampler<CorrectionPair>.MaximumTurns * 2,
        seed = _corrections?.Seed,
        turns = _corrections?.Describe() ?? [],
    };
}
