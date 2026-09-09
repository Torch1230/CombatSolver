namespace CombatSolver;

internal readonly record struct MechanismBalance(double Supply, double Payoffs)
{
    // Both sides are necessary. Saturation allows multiple supported mechanisms
    // without committing the player to one exclusive archetype.
    internal double Readiness => Supply / (1 + Supply) * Payoffs / (1 + Payoffs);
    internal double Marginal(double supply, double payoffs) =>
        16 * (new MechanismBalance(Supply + supply, Payoffs + payoffs).Readiness - Readiness);
}

internal sealed record DeckMechanismAxis(string Name, MechanismBalance Balance);
internal sealed record DeckProfileSnapshot(
    IReadOnlyList<DeckMechanismAxis> Mechanisms, int DeckSize,
    int DrawCards, int EnergyCards, int DefensiveCards, int ExpensiveCards,
    int ReviewedCards)
{
    internal bool DrawShortage => DeckSize >= 15 && DrawCards < DeckSize * 0.15;
    internal bool EnergyShortage => ExpensiveCards >= 4 && EnergyCards == 0;
    internal bool DefenseShortage => DeckSize > 0 && DefensiveCards < DeckSize * 0.3;
}

internal static class DeckMechanismProfile
{
    internal static DeckProfileSnapshot Capture(AdviceContext context)
    {
        DeckMechanismAxis[] axes =
        [
            new("星星供需", Stars(context)),
            new("锻造兑现", Forge(context)),
            new("小刀配合", Capture(context, AdviceRole.ShivSource, AdviceRole.ShivPayoff)),
            new("弃牌配合", Capture(context, AdviceRole.DiscardSource, AdviceRole.DiscardPayoff)),
            new("消耗配合", Capture(context, AdviceRole.ExhaustSource | AdviceRole.SelfExhaust, AdviceRole.ExhaustPayoff)),
            new("灵魂配合", Capture(context, AdviceRole.SoulSource, AdviceRole.SoulPlayPayoff | AdviceRole.SoulExhaustPayoff)),
            new("召唤攻击配合", Capture(context, AdviceRole.SummonSource, AdviceRole.OstyAttack)),
            new("集中产球配合", Capture(context, AdviceRole.FocusOrbSource, AdviceRole.FocusSource)),
        ];
        return new(axes, context.Deck.Count,
            context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Draw)),
            context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Energy)),
            context.Deck.Count(c => c.Block > 0),
            context.Deck.Count(c => !c.EnergyX && c.Cost >= 2),
            context.Deck.Count(c => c.Coverage == AdviceCoverage.Partial));
    }

    internal static MechanismBalance Stars(AdviceContext context) => new(
        context.StartingStars + context.Deck.Sum(AdviceMechanics.StarSupply),
        context.Deck.Count(c => c.StarCost > 0 || c.StarsX));

    internal static MechanismBalance Forge(AdviceContext context)
    {
        double supply = AdviceMechanics.SourceSupply(context, AdviceRole.ForgeSource);
        // Forge creates a Blade. Requiring a permanent-deck Blade would invent a missing component.
        double attacks = supply > 0 ? Math.Clamp(context.BaseEnergy / 2d, 0, 1) : 0;
        return new(supply, attacks);
    }

    internal static double DrawDemand(int deckSize, int drawCards) =>
        1d / (1 + drawCards / Math.Max(1d, deckSize * 0.15));

    internal static double EnergyDemand(int expensiveCards, int energyCards) =>
        6 + 6d * expensiveCards / (expensiveCards + 2d * energyCards + 4);

    internal static MechanismBalance Capture(AdviceContext context, AdviceRole source, AdviceRole payoff) => new(
        AdviceMechanics.SourceSupply(context, source),
        context.Deck.Count(c => (c.Roles & payoff) != 0));
}
