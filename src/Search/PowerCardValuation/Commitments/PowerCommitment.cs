namespace CombatSolver;

internal sealed record PowerCommitment(
    PowerCommitmentFamily Family,
    PowerRoutePriority Priority,
    IReadOnlyList<string> Cards,
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

    public bool HasCard(string cardId)
    {
        for (int index = 0; index < Cards.Count; index++)
        {
            if (string.Equals(Cards[index], cardId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
