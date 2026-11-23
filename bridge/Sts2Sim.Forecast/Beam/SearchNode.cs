using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

public sealed record BeamBudget(int BeamWidth, int NodeLimit, int MaxCardBranches,
    int MaxPileChoiceBranches, int MaxHandChoiceBranches, TimeSpan TimeLimit)
{
    public int? PotionFreeBeamWidth { get; init; }
    public int? PotionBeamWidth { get; init; }
}

public sealed record PlannedAction(int Turn, string Kind, string? CardId, int CardOccurrence,
    int TargetIndex, string? PotionId, int PotionSlot,
    IReadOnlyList<IReadOnlyList<int>> ChoiceIndices, IReadOnlyList<string> ChoiceCardIds);

public sealed record BeamResult(IReadOnlyList<PlannedAction> Actions, bool Won, int HpLost,
    long ExpandedNodes, bool BudgetTruncated);

public sealed record BeamReplayResult(bool Won, int HpLost, bool CombatEnded);

/// <summary>One independently cloned Core state and its route link. Method: CombatBeamSolver.Models.cs @ d231e9e5.</summary>
internal sealed class SearchNode
{
    private SearchNode(CombatState state, SearchNode? parent, PlannedAction? action,
        double score, CombatStateDescriptionDigest fingerprint, int potionUses,
        int forcedPotionUses, int brightestFlameMaxHpLost, GrowthCounts growth,
        int deathSaveHpRestored, int deathSaveUseCount, int escapedTheft,
        ForecastSearchBoundary nodeBoundary)
    {
        State = state;
        Parent = parent;
        Action = action;
        Score = score;
        PotionUses = potionUses;
        ForcedPotionUses = forcedPotionUses;
        BrightestFlameMaxHpLost = brightestFlameMaxHpLost;
        Growth = growth;
        DeathSaveHpRestored = deathSaveHpRestored;
        DeathSaveUseCount = deathSaveUseCount;
        EscapedTheft = escapedTheft;
        NodeBoundary = nodeBoundary;
        Fingerprint = fingerprint;
        Depth = (parent?.Depth ?? -1) + 1;
    }

    public CombatState State { get; }
    public SearchNode? Parent { get; }
    public PlannedAction? Action { get; }
    public double Score { get; }
    public CombatStateDescriptionDigest Fingerprint { get; }
    public int Depth { get; }
    public int PotionUses { get; }
    public int ForcedPotionUses { get; }
    public int BrightestFlameMaxHpLost { get; }
    public GrowthCounts Growth { get; }
    public int DeathSaveHpRestored { get; }
    public int DeathSaveUseCount { get; }
    public int EscapedTheft { get; }
    public ForecastSearchBoundary NodeBoundary { get; }
    public bool Ended => State.Engine is { IsInProgress: false };
    public bool Won => State.Engine?.Won == true;
    public int Turn => State.Players[0].PlayerCombatState?.TurnNumber ?? 0;

    public static SearchNode Create(CombatState state, SearchNode? parent, PlannedAction? action,
        StateEvaluator evaluator, int branchBrightestFlameMaxHpLost = 0,
        GrowthCounts branchGrowth = default, int branchDeathSaveHpRestored = 0,
        int branchDeathSaveUseCount = 0, ForecastSearchBoundary nodeBoundary = ForecastSearchBoundary.None)
    {
        var builder = new CombatStateDescriptionBuilder();
        CombatStateDescription.AppendExactState(ref builder, state);
        int uses = (parent?.PotionUses ?? 0) + (action?.Kind == "UsePotion" ? 1 : 0);
        int forced = (parent?.ForcedPotionUses ?? 0)
            + (action is not null && evaluator.IsForced(action) ? 1 : 0);
        GrowthCounts growth = (parent?.Growth ?? default).Add(branchGrowth);
        int deathSaveHpRestored = (parent?.DeathSaveHpRestored ?? 0) + branchDeathSaveHpRestored;
        int deathSaveUseCount = (parent?.DeathSaveUseCount ?? 0) + branchDeathSaveUseCount;
        int escapedTheft = (parent?.EscapedTheft ?? 0) +
            (parent is null ? 0 : TheftLedger.NewlyEscaped(parent.State, state));
        return new SearchNode(state, parent, action,
            evaluator.Score(state, parent?.Depth + 1 ?? 0, uses, forced, growth,
                deathSaveHpRestored, escapedTheft),
            builder.Build(), uses, forced,
            (parent?.BrightestFlameMaxHpLost ?? 0) + branchBrightestFlameMaxHpLost,
            growth, deathSaveHpRestored, deathSaveUseCount, escapedTheft, nodeBoundary);
    }

    public IReadOnlyList<PlannedAction> Route()
    {
        var route = new List<PlannedAction>(Depth);
        for (SearchNode? current = this; current?.Action is not null; current = current.Parent)
            route.Add(current.Action);
        route.Reverse();
        return route;
    }
}
