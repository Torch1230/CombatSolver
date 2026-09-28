namespace CombatSolver;

// Detached, actually observed prefixes used only by offline continuation teachers.
// No simulator, model, SearchNode or mutable scheduling lease survives collection.
internal sealed record SearchWitnessPrefix(SearchWitnessPrefix.Step[] Steps)
{
    internal const int MaximumActions = 96;
    internal sealed record Step(PlanAction Action, StateFingerprint State, int Turn,
        int HpCost, int PotionCost, int FutureSoldHp, SearchRouteTraits Traits, TurnOutcome? Outcome);

    internal PlanAction[] Actions => Steps.Select(step => step.Action).ToArray();

    internal static SearchWitnessPrefix Capture(SearchNode node)
    {
        if (node.ActionCount is < 1 or > MaximumActions)
            throw new InvalidOperationException("Continuation prefix exceeds its action bound.");
        List<Step> steps = [];
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (cursor.TurnSetupChoices is { Count: > 0 })
                throw new InvalidOperationException("Continuation teacher requires an already resolved root setup.");
            if (cursor.Action is not { } action) continue;
            var snapshot = cursor.Snapshot;
            steps.Add(new(CombatBeamSolver.CopyObservedAction(action), cursor.StateKey, cursor.Turn,
                HpCost(snapshot), cursor.PotionStrategicCost, cursor.FutureSoldHp, cursor.Traits, cursor.Outcome));
        }
        if (steps.Count != node.ActionCount)
            throw new InvalidOperationException("Continuation prefix has an incomplete parent chain.");
        steps.Reverse();
        return new(steps.ToArray());
    }

    internal static int HpCost(SimulationSnapshot snapshot)
        => snapshot.CumulativePlayerHpLost - snapshot.RecoveredPlayerHp - snapshot.StrategicHpCredit;
}
