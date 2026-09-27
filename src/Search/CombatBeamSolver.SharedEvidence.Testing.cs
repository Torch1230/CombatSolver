namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Native contract: a second solver must reuse the first solver's probe, and
    // VerifyIncrementalSearch checks that hit against an actual full replay.
    internal StandPatEvaluation ProbeSharedRootForTesting()
    {
        SimulationSnapshot snapshot = Replay([]);
        try { return EvaluateStandPat(CreateOpeningSearchSeed(snapshot)); }
        finally { snapshot.ReleaseSimulator(); }
    }
}
