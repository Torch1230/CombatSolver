namespace CombatSolver;

// One root/policy/pass only. No simulator or continuation survives a member.
internal sealed class OpeningActionCollector
{
    internal SortedDictionary<string, PlanAction> Actions { get; } = new(StringComparer.Ordinal);
    internal SearchOpeningActionObserver Observer { get; }
    private bool observed;

    internal OpeningActionCollector()
        => Observer = new(() => !observed, actions =>
        {
            observed = true;
            foreach (PlanAction action in actions.Take(RootOutcomeCache.MaximumActions))
                Actions.TryAdd(RootOutcomeCache.ActionKey(action), action);
        });
}
