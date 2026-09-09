namespace CombatSolver;

[Flags]
internal enum AdviceRole
{
    None = 0, DiscardSource = 1, DiscardPayoff = 2,
    ExhaustSource = 4, ExhaustPayoff = 8, SelfExhaust = 16, StopsDraw = 32,
}

// Explicitly reviewed vanilla roles. This is advice metadata, not combat simulation.
internal static class AdviceMechanics
{
    internal static AdviceRole Roles(string id) => id switch
    {
        "ACROBATICS" or "PREPARED" or "CALCULATED_GAMBLE" or "DAGGER_THROW" or "SURVIVOR"
            => AdviceRole.DiscardSource,
        "REFLEX" or "TACTICIAN" => AdviceRole.DiscardPayoff,
        "BURNING_PACT" or "TRUE_GRIT" or "FIEND_FIRE" or "SECOND_WIND" or "CORRUPTION"
            => AdviceRole.ExhaustSource,
        "DARK_EMBRACE" or "FEEL_NO_PAIN" => AdviceRole.ExhaustPayoff,
        "BATTLE_TRANCE" => AdviceRole.StopsDraw,
        _ => AdviceRole.None,
    };

    internal static double StarGain(string id, double amount) => id is
        "GLOW" or "GATHER_LIGHT" or "SHINING_STRIKE" or "SOLAR_STRIKE" or "BIG_BANG" ? amount : 0;

    internal static double Value(AdviceContext context, AdviceCard card, List<string> reasons)
    {
        double value = PairValue(context, card, AdviceRole.DiscardSource, AdviceRole.DiscardPayoff,
            "补充弃牌入口", "配合弃牌触发收益", "缺少已识别的主动弃牌入口", reasons);
        value += PairValue(context, card, AdviceRole.ExhaustSource | AdviceRole.SelfExhaust,
            AdviceRole.ExhaustPayoff, "补充消耗触发机会", "配合消耗触发收益", "缺少已识别的消耗触发机会", reasons);
        if (card.Roles.HasFlag(AdviceRole.StopsDraw))
        {
            int otherDraw = context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Draw)
                && !c.Roles.HasFlag(AdviceRole.StopsDraw));
            value -= Math.Min(8, otherDraw * 2);
            reasons.Add("打出后限制本回合继续抽牌");
        }
        if (card.Stars > 0 && context.Deck.Any(c => c.StarCost > 0))
        {
            double supply = context.Deck.Sum(c => c.Stars);
            value += Math.Min(6, card.Stars * (supply < context.Deck.Sum(c => c.StarCost) ? 3 : 1));
            reasons.Add("补充星星供给");
        }
        if (card.StarCost > 0)
        {
            bool supplied = context.Deck.Any(c => c.Stars > 0);
            value += supplied ? 2 : -4;
            reasons.Add(supplied ? "牌组具备产星来源" : "缺少已识别的持续产星来源");
        }
        return value;
    }

    private static double PairValue(AdviceContext context, AdviceCard card,
        AdviceRole source, AdviceRole payoff, string sourceReason, string payoffReason,
        string missingReason, List<string> reasons)
    {
        double value = 0;
        int sources = context.Deck.Count(c => (c.Roles & source) != 0);
        int payoffs = context.Deck.Count(c => (c.Roles & payoff) != 0);
        if ((card.Roles & source) != 0 && payoffs > 0)
        {
            value += Math.Min(6, payoffs * 2d) / (1 + sources * 0.5);
            reasons.Add(sourceReason);
        }
        if ((card.Roles & payoff) != 0)
        {
            value += sources == 0 ? -4 : Math.Min(8, sources * 2d) / (1 + payoffs * 0.5);
            reasons.Add(sources == 0 ? missingReason : payoffReason);
        }
        return value;
    }
}
