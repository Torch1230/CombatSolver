namespace CombatSolver;

internal sealed record PowerCommitment(
    PowerCommitmentFamily Family,
    SilentPowerCardIdentity Cards,
    int OpenedTurn,
    int OpenedActionCount,
    int OpenedHistoryEntryCount,
    int RoundTransitions,
    int LastEvidenceTurn,
    int Investment,
    int ProvisionalPotential,
    int ProgressEvidence,
    int RealizedEvidence,
    int PowerCardsPlayed)
{
    public int NetUnrealizedValue => (int)Math.Clamp(
        (long)ProvisionalPotential - Investment,
        int.MinValue,
        int.MaxValue);
}
