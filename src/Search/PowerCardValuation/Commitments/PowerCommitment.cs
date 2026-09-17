namespace CombatSolver;

internal sealed record PowerCommitment(
    PowerCommitmentFamily Family,
    int OpenedTurn,
    int OpenedActionCount,
    int RoundTransitions,
    int LastEvidenceTurn,
    int Investment,
    int ProvisionalPotential,
    int RealizedEvidence,
    int PowerCardsPlayed)
{
    public int NetUnrealizedValue => (int)Math.Clamp(
        (long)ProvisionalPotential - Investment,
        int.MinValue,
        int.MaxValue);
}
