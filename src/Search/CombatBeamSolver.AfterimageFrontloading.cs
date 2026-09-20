using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed record AfterimageFrontloading(
        SearchNode Node,
        RouteAnnotations Annotations,
        int HpSaved);

    // Only refine a route which already plays Afterimage. This never adds a Power
    // to a route or changes its potion policy; the full reordered route is replayed.
    private AfterimageFrontloading? TryFrontloadAfterimages(
        SearchNode original,
        RouteAnnotations originalAnnotations,
        SolverResultScope resultScope)
    {
        if (resultScope != SolverResultScope.SearchCompletion
            || !SolverInterimResultOrdering.IsCompleteVictory(
                original.ActionCount,
                original.Snapshot.AllEnemiesDead,
                original.Snapshot.PlayerDead,
                original.Snapshot.ProjectedPlayerHp))
        {
            return null;
        }

        PlanAction[] actions = original.Actions.ToArray();
        if (!HasDelayedAfterimage(actions))
            return null;

        SearchNode? candidate = ReplayAdjustedRoute(
            actions,
            original.GetTurnSetupChoices(),
            original.GetTurnSetupPlayState(),
            originalAnnotations,
            frontloadAfterimages: true);
        if (candidate == null)
            return null;

        bool samePolicy = candidate.PotionCount == original.PotionCount
            && candidate.PotionStrategicCost == original.PotionStrategicCost
            && candidate.Snapshot.ProjectedDeathSaveUseCount
                <= original.Snapshot.ProjectedDeathSaveUseCount;
        bool lessDamage = candidate.Snapshot.CumulativePlayerHpLost
            < original.Snapshot.CumulativePlayerHpLost;
        bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
            candidate.ActionCount,
            candidate.Snapshot.AllEnemiesDead,
            candidate.Snapshot.PlayerDead,
            candidate.Snapshot.ProjectedPlayerHp);
        if (!samePolicy || !lessDamage || !completeVictory)
        {
            candidate.Snapshot.ReleaseSimulator();
            return null;
        }

        FinalPlanSelection comparison;
        try
        {
            comparison = FinalOrdering.Select(
                [(original, original.Snapshot), (candidate, candidate.Snapshot)],
                root.InitialPlayerHp,
                emitDiagnostics: false);
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            candidate.Snapshot.ReleaseSimulator();
            return null;
        }
        if (!ReferenceEquals(comparison.Candidate.Node, candidate))
        {
            candidate.Snapshot.ReleaseSimulator();
            return null;
        }

        int hpSaved = original.Snapshot.CumulativePlayerHpLost
            - candidate.Snapshot.CumulativePlayerHpLost;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] AFTERIMAGE_ROUTE_FRONTLOADED " +
            $"hp_saved={hpSaved} expanded_nodes_added=0");
        return new AfterimageFrontloading(
            candidate,
            BuildRouteAnnotations(candidate),
            hpSaved);
    }

    private static bool IsAfterimageAction(PlanAction action)
        => action.Kind == PlanActionKind.PlayCard
            && string.Equals(action.CardId, "AFTERIMAGE", StringComparison.Ordinal);

    private static bool HasDelayedAfterimage(IReadOnlyList<PlanAction> actions)
    {
        int turn = int.MinValue;
        bool priorAction = false;
        foreach (PlanAction action in actions)
        {
            if (action.Turn != turn)
            {
                turn = action.Turn;
                priorAction = false;
            }
            if (IsAfterimageAction(action) && priorAction)
                return true;
            priorAction |= !IsAfterimageAction(action);
        }
        return false;
    }

    private bool FrontloadAvailableAfterimages(
        SimulationSnapshot turnStart,
        PlanAction[] actions,
        int start)
    {
        int end = start;
        while (end < actions.Length && actions[end].Turn == turnStart.Turn)
            end++;

        PlanAction[] afterimages = actions[start..end]
            .Where(IsAfterimageAction)
            .ToArray();
        if (afterimages.Length == 0
            || actions.AsSpan(start, afterimages.Length).ToArray().All(IsAfterimageAction))
        {
            return false;
        }

        CombatPredictionSimulator simulator = turnStart.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
        List<PredictedCard> available = [.. player.Hand.Cards];
        int energyNeeded = 0;
        int starsNeeded = 0;
        foreach (PlanAction action in afterimages)
        {
            PredictedCard? card = FindCardForReplay(available, action);
            if (card == null || !combat.CanPlayCard(simulator, card))
                return false;
            energyNeeded += Math.Max(0, card.GetEnergyCostWithModifiers(simulator, player));
            starsNeeded += Math.Max(0, card.GetStarCostWithModifiers(simulator, player));
            available.Remove(card);
        }
        if (energyNeeded > player.Energy || starsNeeded > player.Stars)
            return false;

        PlanAction[] remainder = actions[start..end]
            .Where(action => !IsAfterimageAction(action))
            .ToArray();
        afterimages.CopyTo(actions, start);
        remainder.CopyTo(actions, start + afterimages.Length);
        return true;
    }
}
