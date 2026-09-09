namespace CombatSolver;

internal readonly record struct MechanismBalance(double Supply, double Payoffs)
{
    // Both sides are necessary. Saturation allows multiple supported mechanisms
    // without committing the player to one exclusive archetype.
    internal double Readiness => Supply / (1 + Supply) * Payoffs / (1 + Payoffs);
    internal double Marginal(double supply, double payoffs) =>
        16 * (new MechanismBalance(Supply + supply, Payoffs + payoffs).Readiness - Readiness);
}

internal static class DeckMechanismProfile
{
    internal static MechanismBalance Capture(AdviceContext context, AdviceRole source, AdviceRole payoff) => new(
        AdviceMechanics.SourceSupply(context, source),
        context.Deck.Count(c => (c.Roles & payoff) != 0));
}
