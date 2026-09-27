namespace CombatSolver;

internal enum SolverPotionDirective
{
    Smart,
    Force,
    Disabled,
}

internal enum PotionStrategyPreset
{
    AllSmart,
    AllProtected,
    AllForced,
    OnlyForced,
}

internal readonly record struct PotionSlotDirective(
    int Slot,
    string PotionId,
    SolverPotionDirective Directive);

internal readonly record struct ForcedPotionUseEvaluation(
    bool AllForcedUsesSatisfied,
    int ForcedUseCount,
    int ForcedStrategicHpCost,
    int ForcedAmbergrisCount);

internal sealed class PotionStrategySnapshot
{
    private readonly Dictionary<(int Slot, string PotionId), SolverPotionDirective> _directives;
    private readonly bool _onlyForcedUses;

    public PotionStrategySnapshot(
        SolverPotionPolicy defaultPolicy,
        IEnumerable<PotionSlotDirective> directives)
        : this(defaultPolicy, directives, onlyForcedUses: false) { }

    private PotionStrategySnapshot(
        SolverPotionPolicy defaultPolicy,
        IEnumerable<PotionSlotDirective> directives,
        bool onlyForcedUses)
    {
        DefaultPolicy = defaultPolicy;
        _onlyForcedUses = onlyForcedUses;
        _directives = directives.ToDictionary(
            directive => (directive.Slot, directive.PotionId),
            directive => directive.Directive);
        Directives = _directives
            .Select(item => new PotionSlotDirective(item.Key.Slot, item.Key.PotionId, item.Value))
            .OrderBy(item => item.Slot)
            .ToArray();
    }

    public SolverPotionPolicy DefaultPolicy { get; }
    public IReadOnlyList<PotionSlotDirective> Directives { get; }
    public bool HasForcedDirectives
        => Directives.Any(directive => directive.Directive == SolverPotionDirective.Force);

    public int ForcedDirectiveCount
        => Directives.Count(directive => directive.Directive == SolverPotionDirective.Force);

    public PotionStrategySnapshot ForForcedBaseline()
        => new(DefaultPolicy, Directives, onlyForcedUses: true);

    public SolverPotionDirective Resolve(int slot, string potionId)
        => _directives.GetValueOrDefault(
            (slot, potionId),
            DefaultPolicy == SolverPotionPolicy.Disabled
                ? SolverPotionDirective.Disabled
                : SolverPotionDirective.Smart);

    public bool AllowsExplicitUse(
        int slot,
        string potionId,
        SolverPotionPolicy effectivePolicy,
        bool forceAllDisabled)
    {
        if (forceAllDisabled)
            return false;
        if (_onlyForcedUses)
            return _directives.TryGetValue((slot, potionId), out SolverPotionDirective forced)
                && forced == SolverPotionDirective.Force;
        return _directives.TryGetValue((slot, potionId), out SolverPotionDirective directive)
            ? directive != SolverPotionDirective.Disabled
            : effectivePolicy != SolverPotionPolicy.Disabled;
    }

    public ForcedPotionUseEvaluation EvaluateForcedUses(
        IReadOnlyList<PlanAction> actions,
        bool renewablePotionShapedRock,
        PotionStrategicCostLookup? strategicCosts = null)
    {
        PotionSlotDirective[] forced = Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .ToArray();
        int count = 0;
        int strategicCost = 0;
        int ambergrisCount = 0;
        foreach (PotionSlotDirective directive in forced)
        {
            bool used = actions.Any(action =>
                action.Kind == PlanActionKind.UsePotion
                && action.PotionSlot == directive.Slot
                && string.Equals(action.PotionId, directive.PotionId, StringComparison.Ordinal));
            if (!used)
                continue;
            count++;
            strategicCost += strategicCosts != null
                ? strategicCosts.Get(directive.PotionId, renewablePotionShapedRock)
                : PotionUsePolicy.StrategicHpCost(directive.PotionId, renewablePotionShapedRock);
            if (string.Equals(directive.PotionId, "AMBERGRIS", StringComparison.Ordinal))
                ambergrisCount++;
        }
        return new ForcedPotionUseEvaluation(
            count == forced.Length,
            count,
            strategicCost,
            ambergrisCount);
    }

    public ForcedPotionUseEvaluation EvaluateForcedUses(SearchNode node)
    {
        int count = 0;
        int strategicCost = 0;
        int ambergrisCount = 0;
        foreach (PotionSlotDirective directive in Directives)
        {
            if (directive.Directive != SolverPotionDirective.Force)
                continue;
            SearchNode? useNode = null;
            for (SearchNode? cursor = node; cursor?.Action is { } action; cursor = cursor.Parent)
            {
                if (action.Kind == PlanActionKind.UsePotion
                    && action.PotionSlot == directive.Slot
                    && string.Equals(action.PotionId, directive.PotionId, StringComparison.Ordinal))
                {
                    useNode = cursor;
                    break;
                }
            }
            if (useNode == null)
                continue;
            count++;
            strategicCost += useNode.Snapshot.ExplicitPotionStrategicCost
                - useNode.Parent!.Snapshot.ExplicitPotionStrategicCost;
            if (directive.PotionId == "AMBERGRIS")
                ambergrisCount++;
        }
        return new ForcedPotionUseEvaluation(count == ForcedDirectiveCount,
            count, strategicCost, ambergrisCount);
    }

    public ForcedPotionUseEvaluation EvaluateForcedUses(
        IReadOnlyList<PredictedPotionUse> potionUses)
    {
        int count = 0;
        int strategicCost = 0;
        int ambergrisCount = 0;
        foreach (PotionSlotDirective directive in Directives)
        {
            if (directive.Directive != SolverPotionDirective.Force)
                continue;
            PredictedPotionUse? use = null;
            for (int index = potionUses.Count - 1; index >= 0; index--)
            {
                PredictedPotionUse candidate = potionUses[index];
                if (candidate.Automatic || candidate.Slot != directive.Slot
                    || !string.Equals(candidate.PotionId, directive.PotionId,
                        StringComparison.Ordinal))
                    continue;
                use = candidate;
                break;
            }
            if (use is not { } matched)
                continue;
            count++;
            strategicCost += matched.StrategicHpCost;
            if (directive.PotionId == "AMBERGRIS")
                ambergrisCount++;
        }
        return new ForcedPotionUseEvaluation(count == ForcedDirectiveCount,
            count, strategicCost, ambergrisCount);
    }

    public string DescribeForcedUses()
        => string.Join(", ", Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .Select(directive => $"{directive.PotionId}@{directive.Slot}"));
}
