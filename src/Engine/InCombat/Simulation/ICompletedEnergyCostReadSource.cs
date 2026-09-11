using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

/// <summary>
/// Current costs supplied by a lane-owned completed-state reader. The source must
/// cover every card queried by that evaluation; unknown identities are errors.
/// It is derived read data and is neither executed nor copied into simulator forks.
/// </summary>
internal interface ICompletedEnergyCostReadSource
{
    int ReadEnergyCost(PredictedCard card);
}
