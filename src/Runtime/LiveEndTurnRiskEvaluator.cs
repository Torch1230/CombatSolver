using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal readonly record struct LiveEndTurnRiskProjection(
    int HpBefore,
    int HpAfter,
    int HpLost,
    bool PlayerDead,
    string MonsterMoves);

internal static class LiveEndTurnRiskEvaluator
{
    public static LiveEndTurnRiskProjection Evaluate(
        CombatState state,
        IReadOnlyList<PlanCardChoice>? turnStartChoices = null)
        => HotEndTurnRiskEvaluator.Evaluate(state, turnStartChoices);
}
