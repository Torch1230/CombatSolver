namespace CombatSolver;

/// <summary>
/// Raised when hard potion constraints (explicitly forced root potion slots or
/// <see cref="SolverPotionPolicy.RequireAtLeastOne"/>) cannot be satisfied by any explored route.
/// </summary>
internal sealed class PotionPolicyUnsatisfiedException(string message) : InvalidOperationException(message);
