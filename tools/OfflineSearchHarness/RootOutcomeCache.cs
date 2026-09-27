using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>One frozen root and policy only. Stores feasible outcome witnesses, never bounds.</summary>
internal sealed class RootOutcomeCache
{
    internal const int MaximumEvents = 2048;
    internal const int MaximumActions = 128;
    internal SortedDictionary<string, SolverInterimResult> Best { get; } = new(StringComparer.Ordinal);
    internal int Events { get; private set; }
    internal int Rejected { get; private set; }
    internal SearchCompletedOutcomeObserver Observer { get; }

    internal RootOutcomeCache() => Observer = new(() => Events < MaximumEvents, Observe);

    internal void Observe(SearchCompletedOutcome outcome)
    {
        if (Events >= MaximumEvents) return;
        Events++;
        if (outcome.HasRootChoices || outcome.HasPredictionRisk
            || outcome.BoundaryReason != SearchBoundaryReason.None
            || !outcome.Quality.Won || !outcome.Quality.Survives)
        {
            Rejected++;
            return;
        }
        string key = ActionKey(outcome.FirstAction);
        SolverInterimResult quality = outcome.Quality with { Score = 0 };
        if (Best.TryGetValue(key, out SolverInterimResult? previous))
        {
            if (Better(quality, previous)) Best[key] = quality;
        }
        else if (Best.Count < MaximumActions) Best.Add(key, quality);
    }

    internal bool HasWitnessAtLeastAsGood(PlanAction action, SolverInterimResult incumbent)
        => Best.TryGetValue(ActionKey(action), out SolverInterimResult? quality)
            && !Better(incumbent with { Score = 0 }, quality);

    internal PlanAction[] Schedule(IEnumerable<PlanAction> actions, int limit)
    {
        // Feasible outcomes are hints, never upper bounds on what a prefix can do.
        // Alternate exploitation and exploration; even an incumbent's prefix can
        // yield a better continuation with a different beam. Group for diversity
        // only: choices and physical identities remain distinct action keys.
        IComparer<PlanAction> order = Comparer<PlanAction>.Create((a, b) =>
        {
            string ka = ActionKey(a), kb = ActionKey(b);
            bool knownA = Best.TryGetValue(ka, out SolverInterimResult? qa);
            bool knownB = Best.TryGetValue(kb, out SolverInterimResult? qb);
            if (knownA && knownB)
            {
                if (Better(qa!, qb!)) return -1;
                if (Better(qb!, qa!)) return 1;
            }
            else if (knownA != knownB) return knownA ? -1 : 1;
            return StringComparer.Ordinal.Compare(ka, kb);
        });
        var groups = actions.GroupBy(a => (a.CardId, a.TargetCombatId))
            .Select(g => g.OrderBy(a => a, order).ToArray())
            .OrderBy(g => g[0], order).ToArray();
        var known = new Queue<PlanAction[]>(groups.Where(g => Best.ContainsKey(ActionKey(g[0]))));
        var unknown = new Queue<PlanAction[]>(groups.Where(g => !Best.ContainsKey(ActionKey(g[0]))));
        List<PlanAction[]> alternating = [];
        while (known.Count > 0 || unknown.Count > 0)
        {
            if (known.TryDequeue(out PlanAction[]? group)) alternating.Add(group);
            if (unknown.TryDequeue(out group)) alternating.Add(group);
        }
        List<PlanAction> selected = [];
        for (int round = 0; selected.Count < limit; round++)
        {
            bool added = false;
            foreach (PlanAction[] group in alternating)
            {
                if (round >= group.Length) continue;
                selected.Add(group[round]);
                added = true;
                if (selected.Count == limit) break;
            }
            if (!added) break;
        }
        return selected.ToArray();
    }

    private static bool Better(SolverInterimResult candidate, SolverInterimResult current)
    {
        if (candidate.TheftPolicy != current.TheftPolicy)
            throw new InvalidOperationException("Outcome cache cannot mix policies.");
        return CombatSearchCoordinator.IsBetterPotionPolicyResult(candidate.TheftPolicy, candidate, current);
    }

    internal static string ActionKey(PlanAction action)
        => JsonSerializer.Serialize(action with
        {
            RelicEffects = null, CardTitle = "", TargetName = "", PotionTitle = "",
            Choice = CanonicalChoice(action.Choice),
            NestedChoices = action.NestedChoices?.Select(c => CanonicalChoice(c)!).ToArray(),
            TurnStartChoices = action.TurnStartChoices?.Select(c => CanonicalChoice(c)!).ToArray(),
        }, UnattendedTestFiles.JsonOptions);

    private static PlanCardChoice? CanonicalChoice(PlanCardChoice? choice)
        => choice == null ? null : choice with
        { Cards = choice.Cards.Select(c => c with { Title = "" }).ToArray() };
}
