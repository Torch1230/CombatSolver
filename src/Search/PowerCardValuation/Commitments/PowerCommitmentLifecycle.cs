namespace CombatSolver;

internal enum PowerCommitmentDisposition
{
    Active,
    Expired,
    Realized,
}

internal readonly record struct PowerCommitmentAdvanceResult(
    PowerCommitment? Commitment,
    PowerCommitmentDisposition Disposition);

internal static class PowerCommitmentLifecycle
{
    internal static PowerCommitment Create(
        in PowerCommitmentDescriptor descriptor,
        int turn,
        int actionCount,
        int historyEntryCount,
        int investment,
        int provisionalPotential)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(historyEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(investment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(provisionalPotential);
        if (descriptor.Family == PowerCommitmentFamily.None
            || string.IsNullOrEmpty(descriptor.CardId))
        {
            throw new ArgumentException("能力承诺需要已登记的机制族和卡牌身份。", nameof(descriptor));
        }
        return new(
            descriptor.Family,
            descriptor.Priority,
            [descriptor.CardId],
            turn,
            actionCount,
            historyEntryCount,
            0,
            turn,
            investment,
            provisionalPotential,
            0,
            0,
            1);
    }

    internal static PowerCommitment AddPower(
        PowerCommitment commitment,
        in PowerCommitmentDescriptor descriptor,
        int investment,
        int provisionalPotential,
        int progressEvidence,
        int realizedEvidence,
        int turn)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentOutOfRangeException.ThrowIfNegative(investment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(provisionalPotential);
        ArgumentOutOfRangeException.ThrowIfNegative(progressEvidence);
        ArgumentOutOfRangeException.ThrowIfNegative(realizedEvidence);
        int unrealizedPriorPotential = Math.Max(
            0,
            commitment.ProvisionalPotential - realizedEvidence);
        return commitment with
        {
            Family = commitment.Family | descriptor.Family,
            Priority = descriptor.Priority > commitment.Priority
                ? descriptor.Priority
                : commitment.Priority,
            Cards = AppendCard(commitment.Cards, descriptor.CardId),
            Investment = SaturatingAdd(commitment.Investment, investment),
            ProvisionalPotential = SaturatingAdd(
                unrealizedPriorPotential,
                provisionalPotential),
            ProgressEvidence = SaturatingAdd(commitment.ProgressEvidence, progressEvidence),
            RealizedEvidence = SaturatingAdd(commitment.RealizedEvidence, realizedEvidence),
            LastEvidenceTurn = progressEvidence > 0 || realizedEvidence > 0
                ? turn
                : commitment.LastEvidenceTurn,
            PowerCardsPlayed = checked(commitment.PowerCardsPlayed + 1),
        };
    }

    internal static PowerCommitmentAdvanceResult Advance(
        PowerCommitment commitment,
        int parentTurn,
        int childTurn,
        int maximumTransitions,
        int progressEvidence,
        int realizedEvidence,
        bool terminal)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTransitions);
        ArgumentOutOfRangeException.ThrowIfNegative(progressEvidence);
        ArgumentOutOfRangeException.ThrowIfNegative(realizedEvidence);
        if (terminal)
            return new(null, PowerCommitmentDisposition.Expired);

        int roundTransitions = commitment.RoundTransitions
            + Math.Max(0, childTurn - parentTurn);
        bool hasEvidence = progressEvidence > 0 || realizedEvidence > 0;
        int lastEvidenceTurn = hasEvidence ? childTurn : commitment.LastEvidenceTurn;
        if (roundTransitions > maximumTransitions
            || roundTransitions > 0 && childTurn - lastEvidenceTurn > 1)
        {
            return new(null, PowerCommitmentDisposition.Expired);
        }

        int remainingPotential = Math.Max(
            0,
            commitment.ProvisionalPotential - realizedEvidence);
        if (remainingPotential == 0 && realizedEvidence > 0)
            return new(null, PowerCommitmentDisposition.Realized);

        return new(
            commitment with
            {
                RoundTransitions = roundTransitions,
                LastEvidenceTurn = lastEvidenceTurn,
                ProgressEvidence = SaturatingAdd(
                    commitment.ProgressEvidence,
                    progressEvidence),
                RealizedEvidence = SaturatingAdd(
                    commitment.RealizedEvidence,
                    realizedEvidence),
                ProvisionalPotential = remainingPotential,
            },
            PowerCommitmentDisposition.Active);
    }

    private static IReadOnlyList<string> AppendCard(
        IReadOnlyList<string> cards,
        string cardId)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (string.Equals(cards[index], cardId, StringComparison.Ordinal))
                return cards;
        }
        string[] result = new string[cards.Count + 1];
        for (int index = 0; index < cards.Count; index++)
            result[index] = cards[index];
        result[^1] = cardId;
        return result;
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
