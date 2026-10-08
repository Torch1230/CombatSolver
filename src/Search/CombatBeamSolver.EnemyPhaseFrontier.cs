namespace CombatSolver;

internal sealed record EnemyPhaseFrontierCandidate(
    PlanAction[] Actions,
    int Turn,
    int ProjectedPlayerHp,
    int EnemyHp);

internal sealed partial class CombatBeamSolver
{
    // Freeze only a safe, actually retained turn boundary. No simulator or
    // mutable search graph escapes the member that discovered the prefix.
    private static IReadOnlyList<EnemyPhaseFrontierCandidate> SelectEnemyPhaseFrontier(
        IReadOnlyList<SearchNode> frontier)
        => frontier.Where(IsImmediateEnemyPhaseBoundary)
            .OrderByDescending(node => node.Snapshot.ProjectedPlayerHp)
            .ThenBy(node => node.Snapshot.EnemyHp)
            .ThenByDescending(node => node.Score)
            .Take(1)
            .Select(node => new EnemyPhaseFrontierCandidate(node.Actions.ToArray(),
                node.Turn, node.Snapshot.ProjectedPlayerHp,
                node.Snapshot.EnemyHp)).ToArray();

    private static bool IsImmediateEnemyPhaseBoundary(SearchNode node)
    {
        if (node.Action?.Kind != PlanActionKind.EndTurn || node.IsTerminal
            || node.Snapshot.HasRisk || node.Snapshot.PlayerDead
            || node.BoundaryReason != SearchBoundaryReason.None
            || node.Snapshot.ProjectedPlayerHp <= 0 || node.PotionCount != 0)
            return false;
        int completedTurn = node.Action.Turn;
        for (SearchNode? cursor = node; cursor?.Parent is { } parent;
             cursor = parent)
        {
            if (cursor.Action?.Turn != completedTurn)
                break;
            ulong before = parent.Snapshot.AliveEnemyMask;
            ulong after = cursor.Snapshot.AliveEnemyMask;
            if ((before & ~after) != 0 && (after & ~before) != 0)
                return true;
        }
        return false;
    }
}
