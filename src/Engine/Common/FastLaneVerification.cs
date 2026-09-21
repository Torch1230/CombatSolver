namespace CombatSolver.Engine.Common;

/// <summary>
/// Switch for the reconciliation checks that guard route-preserving fast lanes.
/// </summary>
/// <remarks>
/// A fast lane replaces an expensive general path with a cheap one that is supposed to produce the
/// same result. "Supposed to" is not evidence, so every such lane carries a verification mode that
/// re-runs the original path alongside it and throws on the first divergence. The checks are far
/// too expensive to leave on, so they are opt-in through the environment and read once.
/// <para>
/// <c>COMBATSOLVER_VERIFY_HOOK_MASK</c> is kept as an alias because the hook listener mask lane
/// shipped with it first.
/// </para>
/// </remarks>
internal static class FastLaneVerification
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("COMBATSOLVER_VERIFY_FAST_LANES") == "1"
        || Environment.GetEnvironmentVariable("COMBATSOLVER_VERIFY_HOOK_MASK") == "1";
}
