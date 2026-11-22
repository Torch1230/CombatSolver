using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    internal static bool TryReplaySingleRouteForContinuation(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        IReadOnlyList<PlanAction> actions,
        CancellationToken cancellationToken,
        out IReadOnlyList<CachedContinuation> continuations)
    {
        if (actions.Count <= 1)
        {
            continuations = [];
            return true;
        }

        List<(int ActionIndex, int Turn)> boundaries = [];
        for (int index = 0; index < actions.Count - 1; index++)
        {
            PlanAction action = actions[index];
            int nextTurn = actions[index + 1].Turn;
            if ((action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)
                && nextTurn > action.Turn)
            {
                boundaries.Add((index, nextTurn));
            }
        }

        if (boundaries.Count == 0)
        {
            continuations = [];
            return true;
        }

        try
        {
            CombatBeamSolver solver = new(root, displayNames, battleDamage, policy, cancellationToken);
            ContinuationCapture capture = new(boundaries);
            SimulationSnapshot? snapshot = null;
            try
            {
                snapshot = solver.Replay(actions, continuationCapture: capture);
                continuations = capture.Complete();
                return true;
            }
            finally
            {
                snapshot?.ReleaseSimulator();
            }
        }
        catch (InvalidOperationException)
        {
            continuations = [];
            return false;
        }
        catch (PredictionUnsupportedException)
        {
            continuations = [];
            return false;
        }
        catch (NotSupportedException)
        {
            continuations = [];
            return false;
        }
    }
}
