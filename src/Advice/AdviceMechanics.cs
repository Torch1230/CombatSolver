namespace CombatSolver;

[Flags]
internal enum AdviceRole
{
    None = 0, DiscardSource = 1, DiscardPayoff = 2,
    ExhaustSource = 4, ExhaustPayoff = 8, SelfExhaust = 16, StopsDraw = 32,
    SoulSource = 64, SoulPlayPayoff = 128, SoulExhaustPayoff = 256,
    SummonSource = 512, OstyAttack = 1024, ForgeSource = 2048, Blade = 4096,
    FocusSource = 8192, FocusOrbSource = 16384, PlasmaSource = 32768, ShivSource = 65536, ShivPayoff = 131072,
    PoisonSource = 262144, PoisonPayoff = 524288, DoomSource = 1048576, DoomPayoff = 2097152, StrengthSource = 4194304, StrengthPayoff = 8388608,
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
        "BLADE_DANCE" or "CLOAK_AND_DAGGER" or "FAN_OF_KNIVES" or "INFINITE_BLADES" => AdviceRole.ShivSource,
        "INFLAME" => AdviceRole.StrengthSource,
        "TWIN_STRIKE" or "RIP_AND_TEAR" or "SWORD_BOOMERANG" => AdviceRole.StrengthPayoff,
        "ACCURACY" => AdviceRole.ShivPayoff,
        "DEADLY_POISON" or "NOXIOUS_FUMES" => AdviceRole.PoisonSource,
        "ACCELERANT" => AdviceRole.PoisonPayoff,
        "END_OF_DAYS" or "NO_ESCAPE" => AdviceRole.DoomSource | AdviceRole.DoomPayoff,
        "BATTLE_TRANCE" => AdviceRole.StopsDraw,
        _ => AdviceRole.None,
    };

    // Values are captured with local modifiers; no future combat state is assumed.
    internal static double FaceDamage(string id, double damage, double calculationBase, double repeat) => id switch
    {
        "UNLEASH" or "SOUL_STORM" => Math.Max(0, calculationBase),
        "TWIN_STRIKE" or "RIP_AND_TEAR" => damage * 2,
        "SWORD_BOOMERANG" or "SOVEREIGN_BLADE" => damage * Math.Max(0, repeat),
        _ => damage,
    };

    internal static double StarGain(string id, double amount) => id is
        "GLOW" or "GATHER_LIGHT" or "SHINING_STRIKE" or "SOLAR_STRIKE" or "BIG_BANG" ? amount : 0;

    internal static double Value(AdviceContext context, AdviceCard card, List<string> reasons)
    {
        double value = PairValue(context, card, AdviceRole.DiscardSource, AdviceRole.DiscardPayoff,
            "补充弃牌入口", "配合弃牌触发收益", "缺少已识别的主动弃牌入口", reasons);
        value += PairValue(context, card, AdviceRole.ExhaustSource | AdviceRole.SelfExhaust,
            AdviceRole.ExhaustPayoff, "补充消耗触发机会", "配合消耗触发收益", "缺少已识别的消耗触发机会", reasons);
        value += PairValue(context, card, AdviceRole.ShivSource, AdviceRole.ShivPayoff,
            "补充小刀生成来源", "配合小刀增幅", "缺少已识别的小刀生成来源", reasons);
        value += PairValue(context, card, AdviceRole.PoisonSource, AdviceRole.PoisonPayoff,
            "补充毒的施加来源", "配合毒的重复触发", "缺少已识别的施毒来源", reasons);
        value += PairValue(context, card, AdviceRole.DoomSource, AdviceRole.DoomPayoff,
            "补充灾厄施加来源", "配合灾厄叠加或结算", "缺少已识别的灾厄来源", reasons);
        value += PairValue(context, card, AdviceRole.StrengthSource, AdviceRole.StrengthPayoff,
            "力量支持多段攻击", "多段攻击放大力量收益", "缺少已识别的力量来源", reasons);
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
            double support = context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Energy));
            double setupCost = Math.Max(0, card.Cost + 2 - context.BaseEnergy - Math.Min(2, support));
            value += 3d / (1 + sources * 0.5) - Math.Min(4, setupCost * 2);
            if (setupCost > 0) reasons.Add("锻造与剑的费用可能需要分回合支付");
            reasons.Add("锻造收益需要后续打出剑来兑现");
        }
        if (card.Roles.HasFlag(AdviceRole.Blade)
            && context.Deck.Any(c => c.Roles.HasFlag(AdviceRole.ForgeSource)))
        {
            value += 2;
            reasons.Add("配合已识别的锻造来源");
        }
        if (card.Roles.HasFlag(AdviceRole.SoulSource)
            && context.Deck.Any(c => c.Roles.HasFlag(AdviceRole.StopsDraw)))
        {
            value -= Math.Min(4, SourceWeight(card) * 2);
            reasons.Add("灵魂抽牌可能受到抽牌限制影响");
        }
        if (card.Roles.HasFlag(AdviceRole.StopsDraw))
        {
            int otherDraw = context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Draw)
                && !c.Roles.HasFlag(AdviceRole.StopsDraw));
            value -= Math.Min(8, otherDraw * 2);
            reasons.Add("打出后限制本回合继续抽牌");
        }
        if (card.Stars > 0 && context.Deck.Any(c => c.StarCost > 0 || c.StarsX))
        {
            MechanismBalance profile = DeckMechanismProfile.Stars(context);
            value += profile.Marginal(StarSupply(card), 0);
            reasons.Add("补充星星供给");
        }
        if (card.EnergyX) reasons.Add("X 能量效果取决于实际投入，不按固定费用评级");
        if (card.StarsX)
        {
            double supply = context.StartingStars + context.Deck.Sum(StarSupply);
            value += Math.Min(3, supply);
            reasons.Add(supply > 0 ? "X 星星效果取决于投入，已识别产星支持" : "X 星星效果尚无已识别资源支持");
        }
        if (card.StarCost > 0)
        {
            bool supplied = context.Deck.Any(c => StarSupply(c) > 0 && !c.SingleUse);
            bool finiteSupply = context.StartingStars + context.Deck.Sum(StarSupply) >= card.StarCost;
            bool initiallyAffordable = context.StartingStars >= card.StarCost;
            value += supplied ? 2 : initiallyAffordable || finiteSupply ? 0 : -4;
            reasons.Add(supplied ? "牌组具备产星来源" : finiteSupply
                ? "一次性产星可支持有限打出" : initiallyAffordable
                ? "初始星星可支持一次打出，持续供给未确认" : "缺少已识别的持续产星来源");
        }
        return value;
    }

    internal static double StarSupply(AdviceCard card) => Math.Max(0, card.Stars)
        * Math.Clamp(card.Availability, 0, 1) * (card.SingleUse ? 0.65 : 1);

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
        MechanismBalance profile = DeckMechanismProfile.Capture(context, source, payoff);
        double addedSupply = (card.Roles & source) != 0 ? SourceWeight(card) : 0;
        double addedPayoff = (card.Roles & payoff) != 0 ? Math.Clamp(card.PayoffWeight, 0, 4) : 0;
        double value = profile.Marginal(addedSupply, addedPayoff);
        if (addedSupply > 0 && profile.Payoffs > 0)
            reasons.Add(sourceReason);
        if (addedPayoff > 0)
        {
            bool missing = profile.Supply + addedSupply <= 0;
            if (missing) value -= 4;
            reasons.Add(missing ? missingReason : payoffReason);
        }
        return value;
    }
}
