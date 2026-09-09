namespace CombatSolver;

[Flags]
internal enum AdviceRole
{
    None = 0, DiscardSource = 1, DiscardPayoff = 2,
    ExhaustSource = 4, ExhaustPayoff = 8, SelfExhaust = 16, StopsDraw = 32,
    SoulSource = 64, SoulPlayPayoff = 128, SoulExhaustPayoff = 256,
    SummonSource = 512, OstyAttack = 1024, ForgeSource = 2048, Blade = 4096,
    FocusSource = 8192, FocusOrbSource = 16384, PlasmaSource = 32768,
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
        "GRAVE_WARDEN" or "REAVE" or "SEVERANCE" => AdviceRole.SoulSource,
        "HAUNT" or "DEVOUR_LIFE" => AdviceRole.SoulPlayPayoff,
        "SOUL_STORM" => AdviceRole.SoulExhaustPayoff,
        "BODYGUARD" or "AFTERLIFE" or "REANIMATE" or "CLEANSE" or "SPUR" or "PULL_AGGRO"
            => AdviceRole.SummonSource,
        "UNLEASH" => AdviceRole.OstyAttack,
        "BIG_BANG" or "SPOILS_OF_BATTLE" or "WROUGHT_IN_WAR" => AdviceRole.ForgeSource,
        "SOVEREIGN_BLADE" => AdviceRole.Blade,
        "DEFRAGMENT" => AdviceRole.FocusSource,
        "ZAP" or "BALL_LIGHTNING" or "COOLHEADED" or "GLACIER"
            or "COLD_SNAP" or "DARKNESS" or "CONSUMING_SHADOW" or "ICE_LANCE" or "CHILL" => AdviceRole.FocusOrbSource,
        "FUSION" or "METEOR_STRIKE" => AdviceRole.PlasmaSource,
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
        // Soul play and exhaust payoffs share a source; count its synergy only once.
        value += PairValue(context, card, AdviceRole.SoulSource,
            AdviceRole.SoulPlayPayoff | AdviceRole.SoulExhaustPayoff,
            "补充灵魂生成来源", "配合灵魂打出或消耗收益", "缺少已识别的灵魂生成来源", reasons);
        value += PairValue(context, card, AdviceRole.SummonSource, AdviceRole.OstyAttack,
            "召唤支持奥斯蒂攻击", "配合召唤来源", "缺少已识别的召唤来源", reasons);
        value += PairValue(context, card, AdviceRole.FocusOrbSource, AdviceRole.FocusSource,
            "充能球可以受益于集中", "配合受集中影响的产球来源", "缺少已识别的受集中影响的产球来源", reasons);
        if (card.Roles.HasFlag(AdviceRole.ForgeSource))
        {
            // Forge creates its own Blade. Existing Blade is not a prerequisite.
            int sources = context.Deck.Count(c => c.Roles.HasFlag(AdviceRole.ForgeSource));
            value += 3d / (1 + sources * 0.5);
            reasons.Add("锻造收益需要后续打出剑来兑现");
        }
        if (card.Roles.HasFlag(AdviceRole.Blade)
            && context.Deck.Any(c => c.Roles.HasFlag(AdviceRole.ForgeSource)))
        {
            value += 2;
            reasons.Add("配合已识别的锻造来源");
        }
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
            bool initiallyAffordable = context.StartingStars >= card.StarCost;
            value += supplied ? 2 : initiallyAffordable ? 0 : -4;
            reasons.Add(supplied ? "牌组具备产星来源" : initiallyAffordable
                ? "初始星星可支持一次打出，持续供给未确认" : "缺少已识别的持续产星来源");
        }
        return value;
    }

    internal static double SourceWeight(AdviceCard card) =>
        Math.Clamp(card.SourceAmount, 0, 4) * Math.Clamp(card.Availability, 0, 1)
        * (card.SingleUse ? 0.65 : 1);

    internal static double SourceSupply(AdviceContext context, AdviceRole source) =>
        context.Deck.Where(c => (c.Roles & source) != 0).Sum(SourceWeight)
        + ((source & AdviceRole.SummonSource) != 0 ? Math.Min(2, context.SummonSupply) : 0)
        + ((source & AdviceRole.FocusOrbSource) != 0 ? Math.Min(2, context.InitialFocusOrbs) : 0);

    private static double PairValue(AdviceContext context, AdviceCard card,
        AdviceRole source, AdviceRole payoff, string sourceReason, string payoffReason,
        string missingReason, List<string> reasons)
    {
        double value = 0;
        double sources = SourceSupply(context, source);
        int payoffs = context.Deck.Count(c => (c.Roles & payoff) != 0);
        if ((card.Roles & source) != 0 && payoffs > 0)
        {
            value += Math.Min(6, payoffs * 2d * SourceWeight(card)) / (1 + sources * 0.5);
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
