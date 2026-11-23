using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;
using CombatSolver.Beam.Novelty;
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

// A retained route needs the actions and same-turn fingerprints of its ancestors.
// Keeping their SearchNodes would keep all their cloned combat graphs alive.
internal sealed class SearchRouteStep(SearchRouteStep? previous, PlannedAction? action,
    int turn, CombatStateDescriptionDigest fingerprint)
{
    public SearchRouteStep? Previous { get; } = previous;
    public PlannedAction? Action { get; } = action;
    public int Turn { get; } = turn;
    public CombatStateDescriptionDigest Fingerprint { get; } = fingerprint;
    public int Depth { get; } = (previous?.Depth ?? -1) + 1;
}

/// <summary>One independently cloned Core state and its route link.</summary>
internal sealed class SearchNode
{
    private readonly NodeEvalMetrics _metrics;
    private CombatStateDescriptionDigest _retentionKey;
    private int _hasRetentionKey;

    private SearchNode(CombatState state, SearchRouteStep path, double score, int potionUses,
        int forcedPotionUses, int optionalPotionCost, int optionalAmbergrisCount,
        StateEvaluator? policyEvaluator, int brightestFlameMaxHpLost, GrowthCounts growth,
        int deathSaveHpRestored, int deathSaveUseCount, int escapedTheft,
        ForecastSearchBoundary nodeBoundary, PowerCommitment? powerCommitment,
        in NodeEvalMetrics metrics, uint? openingTargetCombatId, CombatSolver.Beam.Novelty.SearchRouteTraits routeTraits)
    {
        State = state;
        Path = path;
        Score = score;
        PotionUses = potionUses;
        ForcedPotionUses = forcedPotionUses;
        OptionalPotionCost = optionalPotionCost;
        OptionalAmbergrisCount = optionalAmbergrisCount;
        PolicyEvaluator = policyEvaluator;
        BrightestFlameMaxHpLost = brightestFlameMaxHpLost;
        Growth = growth;
        DeathSaveHpRestored = deathSaveHpRestored;
        DeathSaveUseCount = deathSaveUseCount;
        EscapedTheft = escapedTheft;
        NodeBoundary = nodeBoundary;
        PowerCommitment = powerCommitment;
        _metrics = metrics;
        OpeningTargetCombatId = openingTargetCombatId;
        RouteTraits = routeTraits;
    }

    public CombatState State { get; }
    public SearchRouteStep Path { get; }
    public PlannedAction? Action => Path.Action;
    public double Score { get; }
    public CombatStateDescriptionDigest Fingerprint => Path.Fingerprint;
    public int Depth => Path.Depth;
    public int PotionUses { get; }
    public int ForcedPotionUses { get; }
    public int OptionalPotionCost { get; }
    public int OptionalAmbergrisCount { get; }
    internal StateEvaluator? PolicyEvaluator { get; }
    public int BrightestFlameMaxHpLost { get; }
    public GrowthCounts Growth { get; }
    public int DeathSaveHpRestored { get; }
    public int DeathSaveUseCount { get; }
    public int EscapedTheft { get; }
    public ForecastSearchBoundary NodeBoundary { get; }
    internal PowerCommitment? PowerCommitment { get; }
    public int ProjectedHp => _metrics.ProjectedHp;
    public int PersistentBuffValue => _metrics.PersistentBuffValue;
    public int FutureResourceValue => _metrics.FutureResourceValue;
    public int LatentSetupValue => _metrics.LatentSetupValue;
    public int ReachableHandValue => _metrics.ReachableHandValue;
    public int ZeroCostPlayableCount => _metrics.ZeroCostPlayableCount;
    public int DelayedDamageValue => _metrics.DelayedDamageValue;
    public int ReactiveDamageValue => _metrics.ReactiveDamageValue;
    public int ReplayPotentialValue => _metrics.ReplayPotentialValue;
    public int LongTermResourceValue => _metrics.LongTermResourceValue;
    public int SandpitRemaining => _metrics.SandpitRemaining;
    public int EnemyStrengthSuppression => _metrics.EnemyStrengthSuppression;
    public int EnemyWeakTurns => _metrics.EnemyWeakTurns;
    public int EnemyVulnerableTurns => _metrics.EnemyVulnerableTurns;
    public int LiveDeckClutter => _metrics.LiveDeckClutter;
    public int LiveCardCount => _metrics.LiveCardCount;
    public uint? FocusTargetCombatId => _metrics.FocusTargetCombatId;
    public bool HasPositiveOrbEngine => _metrics.HasPositiveOrbEngine;
    public uint? OpeningTargetCombatId { get; }
    internal CombatSolver.Beam.Novelty.SearchRouteTraits RouteTraits { get; }
    public bool Ended => State.Engine is { IsInProgress: false };
    public bool Won => State.Engine?.Won == true;
    public int Turn => State.Players[0].PlayerCombatState?.TurnNumber ?? 0;

    internal CombatStateDescriptionDigest GetOrComputeRetentionKey()
    {
        if (Volatile.Read(ref _hasRetentionKey) != 0)
            return _retentionKey;
        CombatStateDescriptionDigest key = ForecastRetentionKey.ComputeUncached(this);
        _retentionKey = key;
        Volatile.Write(ref _hasRetentionKey, 1);
        return key;
    }

    internal void PrecomputeRetentionKey()
    {
        if (Volatile.Read(ref _hasRetentionKey) == 0)
        {
            _retentionKey = ForecastRetentionKey.ComputeUncached(this);
            Volatile.Write(ref _hasRetentionKey, 1);
        }
    }

    public static SearchNode Create(CombatState state, SearchNode? parent, PlannedAction? action,
        StateEvaluator evaluator, int branchBrightestFlameMaxHpLost = 0,
        GrowthCounts branchGrowth = default, int branchDeathSaveHpRestored = 0,
        int branchDeathSaveUseCount = 0, ForecastSearchBoundary nodeBoundary = ForecastSearchBoundary.None,
        IReadOnlyList<ObservedCardPlay>? playedCards = null,
        bool hasImpureHistory = false)
    {
        var builder = new CombatStateDescriptionBuilder();
        CombatStateDescription.AppendExactState(ref builder, state);
        bool isPotion = action?.Kind == "UsePotion";
        int uses = (parent?.PotionUses ?? 0) + (isPotion ? 1 : 0);
        bool isForced = isPotion && action is not null && evaluator.IsForced(action);
        int forced = (parent?.ForcedPotionUses ?? 0) + (isForced ? 1 : 0);
        int optionalCost = parent?.OptionalPotionCost ?? 0;
        int optionalAmbergris = parent?.OptionalAmbergrisCount ?? 0;
        if (isPotion && !isForced && action!.PotionId is { } potionId)
        {
            optionalCost += evaluator.GetPotionStrategicCost(potionId);
            if (potionId == "AMBERGRIS")
                optionalAmbergris++;
        }
        GrowthCounts growth = (parent?.Growth ?? default).Add(branchGrowth);
        int deathSaveHpRestored = (parent?.DeathSaveHpRestored ?? 0) + branchDeathSaveHpRestored;
        int deathSaveUseCount = (parent?.DeathSaveUseCount ?? 0) + branchDeathSaveUseCount;
        int escapedTheft = (parent?.EscapedTheft ?? 0) +
            (parent is null ? 0 : TheftLedger.NewlyEscaped(parent.State, state));
        int depth = (parent?.Depth ?? -1) + 1;
        double score = evaluator.EvaluateForNode(
            state,
            depth,
            uses,
            forced,
            growth,
            deathSaveHpRestored,
            escapedTheft,
            out NodeEvalMetrics metrics);

        uint? openingTargetCombatId = parent is null
            ? null
            : parent.Depth == 0
                ? ResolveActionTargetCombatId(parent.State, action)
                : parent.OpeningTargetCombatId;

        CombatSolver.Beam.Novelty.SearchRouteTraits routeTraits;
        if (parent is null)
        {
            routeTraits = CombatSolver.Beam.Novelty.SearchRouteTraits.None;
        }
        else if (isPotion)
        {
            routeTraits = CoreNoveltyFeatures.ClassifyPotionTraits(
                parent.RouteTraits, in parent._metrics, in metrics, parent.State, state);
        }
        else if (action?.Kind == "PlayCard")
        {
            bool declinedExtraTurn = HasUnusedPaelsEye(parent.State);
            bool isPureCardAction = !hasImpureHistory
                && action.ChoiceIndices.Count == 0
                && action.ChoiceCardIds.Count == 0
                && IsPureCardPlay(parent.State, state, playedCards);
            routeTraits = CoreNoveltyFeatures.ClassifyCardTraits(
                parent.RouteTraits,
                in parent._metrics,
                in metrics,
                parent.State,
                state,
                isPureCardAction,
                declinedExtraTurn);
        }
        else
        {
            routeTraits = parent.RouteTraits;
        }
        if (parent is not null && (state.Players[0].PlayerCombatState?.TurnNumber ?? 0) > parent.Turn)
            routeTraits = CoreNoveltyFeatures.ClassifyRoundTransitionTraits(routeTraits, parent.State, state);

        int childBattleHpLost = Math.Max(
            0,
            state.Players[0].Creature.CumulativeHpLost - evaluator.InitialCumulativeHpLost);
        PowerCommitment? powerCommitment = PowerCommitmentEvaluator.Update(
            state,
            parent,
            action,
            playedCards,
            evaluator,
            childBattleHpLost,
            metrics.ProjectedHp,
            metrics.ReachableHandValue,
            metrics.ZeroCostPlayableCount,
            metrics.PersistentBuffValue,
            metrics.FutureResourceValue,
            metrics.LatentSetupValue);

        var path = new SearchRouteStep(parent?.Path, action,
            state.Players[0].PlayerCombatState?.TurnNumber ?? 0, builder.Build());
        return new SearchNode(state, path, score, uses, forced, optionalCost, optionalAmbergris, evaluator,
            (parent?.BrightestFlameMaxHpLost ?? 0) + branchBrightestFlameMaxHpLost,
            growth, deathSaveHpRestored, deathSaveUseCount, escapedTheft, nodeBoundary,
            powerCommitment, in metrics, openingTargetCombatId, routeTraits);
    }

    internal static SearchNode CreateContinuationStart(SearchNode prefixNode, StateEvaluator evaluator)
    {
        CombatState cloned = prefixNode.State.Clone();
        double score = evaluator.EvaluateForNode(
            cloned,
            prefixNode.Depth,
            prefixNode.PotionUses,
            prefixNode.ForcedPotionUses,
            prefixNode.Growth,
            prefixNode.DeathSaveHpRestored,
            prefixNode.EscapedTheft,
            out NodeEvalMetrics metrics);
        return new SearchNode(
            cloned,
            prefixNode.Path,
            score,
            prefixNode.PotionUses,
            prefixNode.ForcedPotionUses,
            prefixNode.OptionalPotionCost,
            prefixNode.OptionalAmbergrisCount,
            evaluator,
            prefixNode.BrightestFlameMaxHpLost,
            prefixNode.Growth,
            prefixNode.DeathSaveHpRestored,
            prefixNode.DeathSaveUseCount,
            prefixNode.EscapedTheft,
            prefixNode.NodeBoundary,
            prefixNode.PowerCommitment,
            in metrics,
            prefixNode.OpeningTargetCombatId,
            prefixNode.RouteTraits);
    }

    private static uint? ResolveActionTargetCombatId(CombatState beforeState, PlannedAction? action)
    {
        if (action is null)
            return null;
        if (action.TargetIndex >= 0 && action.TargetIndex < beforeState.Enemies.Count)
            return beforeState.Enemies[action.TargetIndex].CombatId;
        if (action.TargetIndex <= -2)
        {
            int allyIndex = -2 - action.TargetIndex;
            if (allyIndex < beforeState.Allies.Count)
                return beforeState.Allies[allyIndex].CombatId;
        }
        return null;
    }

    public IReadOnlyList<PlannedAction> Route()
    {
        var route = new List<PlannedAction>(Depth);
        for (SearchRouteStep? current = Path; current?.Action is not null; current = current.Previous)
            route.Add(current.Action);
        route.Reverse();
        return route;
    }

    private static bool HasUnusedPaelsEye(CombatState beforeState)
    {
        IReadOnlyList<RelicModel> relics = beforeState.Players[0].Relics;
        for (int i = 0; i < relics.Count; i++)
        {
            if (relics[i] is PaelsEye eye && !eye.IsMelted && !eye.IsUsedThisCombat)
                return true;
        }
        return false;
    }

    private static bool IsPureCardPlay(
        CombatState beforeState,
        CombatState afterState,
        IReadOnlyList<ObservedCardPlay>? playedCards)
    {
        if (afterState.SemanticHistory.CardsDrawnThisCombat > beforeState.SemanticHistory.CardsDrawnThisCombat
            || afterState.SemanticHistory.OrbsChanneledThisCombat > beforeState.SemanticHistory.OrbsChanneledThisCombat)
        {
            return false;
        }

        int powerPlays = 0;
        if (playedCards is not null)
        {
            for (int i = 0; i < playedCards.Count; i++)
            {
                ObservedCardPlay play = playedCards[i];
                if (play.IsAutoPlay)
                    return false;
                if (play.CardType == CardType.Power)
                    powerPlays++;
            }
        }

        CountPlayerCards(beforeState, out int beforeTotal, out int beforeAfflicted);
        CountPlayerCards(afterState, out int afterTotal, out int afterAfflicted);
        return afterTotal + powerPlays <= beforeTotal && afterAfflicted <= beforeAfflicted;
    }

    private static void CountPlayerCards(CombatState state, out int totalCards, out int afflictedCards)
    {
        totalCards = 0;
        afflictedCards = 0;
        PlayerCombatState? pcs = state.Players[0].PlayerCombatState;
        if (pcs is null)
            return;
        AccumulatePile(pcs.Hand.Cards, ref totalCards, ref afflictedCards);
        AccumulatePile(pcs.DrawPile.Cards, ref totalCards, ref afflictedCards);
        AccumulatePile(pcs.DiscardPile.Cards, ref totalCards, ref afflictedCards);
        AccumulatePile(pcs.ExhaustPile.Cards, ref totalCards, ref afflictedCards);
        AccumulatePile(pcs.PlayPile.Cards, ref totalCards, ref afflictedCards);

        static void AccumulatePile(IReadOnlyList<CardModel> cards, ref int total, ref int afflicted)
        {
            total += cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Affliction is not null)
                    afflicted++;
            }
        }
    }
}
