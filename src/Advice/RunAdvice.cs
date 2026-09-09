namespace CombatSolver;

[Flags]
internal enum AdviceTag
{
    None = 0, Draw = 1, Energy = 2, Area = 4, Scaling = 8,
    Discard = 16, Exhaust = 32, Poison = 64, Orb = 128, Doom = 256, Shiv = 512,
}

internal enum AdviceKind { Card, Relic, Potion, Removal, Skip }

internal sealed record AdviceCard(
    string Id, double Damage, double Block, double Draw, int Cost, bool Attack,
    bool Basic, bool Curse, bool Removable, AdviceTag Tags, bool Known = true, int UpgradeLevel = 0,
    AdviceRole Roles = AdviceRole.None, double Stars = 0, int StarCost = 0);

internal sealed record AdviceContext(
    IReadOnlyList<AdviceCard> Deck, IReadOnlySet<string> Relics,
    int Gold, int Hp, int MaxHp, int Act, int EmptyPotionSlots,
    double StartingStars = 0, double SummonSupply = 0, double InitialFocusOrbs = 0);

internal sealed record AdviceOffer(
    string Key, AdviceKind Kind, string Id, int Cost = 0,
    AdviceCard? Card = null, bool Available = true, double? FallbackValue = null);

internal sealed record AdviceRating(
    AdviceOffer Offer, double Score, int Rank, bool Available, bool Known,
    IReadOnlyList<string> Reasons, string? RemovalCardId = null, int? RemovalDeckIndex = null);

// Marginal deck improvement, not a forecast of an entire run. No live models or RNG.
internal static class RunAdvice
{
    internal static AdviceRating[] Rank(AdviceContext context, IReadOnlyList<AdviceOffer> offers, bool shop)
    {
        AdviceRating[] results = offers.Select(offer => Rate(context, offer, shop)).ToArray();
        var ordered = results.Where(r => r.Available && (r.Known || r.Offer.FallbackValue.HasValue))
            .OrderByDescending(r => r.Score).ToArray();
        for (int i = 0; i < results.Length; i++)
        {
            AdviceRating rating = results[i];
            int rank = rating.Available && (rating.Known || rating.Offer.FallbackValue.HasValue)
                ? 1 + ordered.Count(other => other.Score > rating.Score + 0.001) : 0;
            results[i] = rating with { Rank = rank };
        }
        return results;
    }

    private static AdviceRating Rate(AdviceContext context, AdviceOffer offer, bool shop)
    {
        List<string> reasons = [];
        bool known = true;
        string? removalId = null;
        int? removalIndex = null;
        double value;
        switch (offer.Kind)
        {
            case AdviceKind.Card:
                known = offer.Card?.Known == true;
                value = offer.Card is { } card ? CardValue(context, card, reasons) : 0;
                break;
            case AdviceKind.Relic:
                (value, known) = RelicValue(context, offer.Id, reasons);
                break;
            case AdviceKind.Potion:
                (value, known) = PotionValue(context, offer.Id, reasons);
                break;
            case AdviceKind.Removal:
                var removable = context.Deck.Select((c, index) => (Card: c, Index: index))
                    .Where(c => c.Card.Removable)
                    .Select(c => (c.Card, c.Index, Value: RemovalValue(context, c.Index)))
                    .OrderByDescending(c => c.Value).FirstOrDefault();
                value = removable.Card is null ? -100 : removable.Value;
                removalId = removable.Card?.Id;
                removalIndex = removable.Card is null ? null : removable.Index;
                reasons.Add(removable.Card?.Curse == true ? "移除诅咒" : "精简牌组");
                break;
            default:
                value = 0;
                reasons.Add(shop ? "保留金币" : "保持牌组精简");
                break;
        }
        if (!known && offer.FallbackValue is { } fallback)
        {
            value = fallback;
            reasons.Add("通用稀有度参考");
        }
        bool available = offer.Available && (!shop || offer.Cost <= context.Gold);
        if (shop && offer.Cost > context.Gold) reasons.Add("金币不足");
        if (offer.Kind == AdviceKind.Potion && context.EmptyPotionSlots == 0)
        {
            available = false;
            reasons.Add("药水栏已满");
        }
        if (offer.Kind == AdviceKind.Removal && removalId is null) available = false;
        if (shop && offer.Kind != AdviceKind.Skip)
        {
            int cost = Math.Max(0, offer.Cost);
            int surplusSpend = Math.Min(cost, Math.Max(0, context.Gold - 150));
            value -= surplusSpend * 0.06 + (cost - surplusSpend) * 0.09;
            reasons.Add("已考虑价格与留钱");
        }
        if (!known) reasons.Add("效果规则未覆盖");
        return new AdviceRating(offer, value, 0, available, known, reasons, removalId, removalIndex);
    }

    internal static double CardValue(AdviceContext context, AdviceCard card, List<string> reasons)
    {
        if (card.Curse) { reasons.Add("增加牌组负担"); return -25; }
        int size = Math.Max(1, context.Deck.Count);
        double attacks = context.Deck.Count(c => c.Attack) / (double)size;
        double blocks = context.Deck.Count(c => c.Block > 0) / (double)size;
        double value = Math.Min(14, card.Damage * 0.8 + card.Block * 0.7) - 6;
        if (card.Damage > 0 && attacks < 0.4) { value += 7; reasons.Add("补充输出"); }
        if (card.Block > 0 && blocks < 0.3) { value += 7; reasons.Add("补充防御"); }
        if (card.Tags.HasFlag(AdviceTag.Draw))
        {
            value += 5 + Math.Min(5, card.Draw * 2);
            reasons.Add("改善抽牌");
        }
        if (card.Tags.HasFlag(AdviceTag.Energy))
        {
            value += context.Deck.Count(c => c.Cost >= 2) >= 4 ? 12 : 6;
            reasons.Add("补充能量");
        }
        if (card.Tags.HasFlag(AdviceTag.Area))
        {
            value += context.Deck.Any(c => c.Tags.HasFlag(AdviceTag.Area)) ? 3 : 10;
            reasons.Add("补充群体伤害");
        }
        if (card.Tags.HasFlag(AdviceTag.Scaling))
        {
            value += context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Scaling)) < 2 ? (context.Act == 0 ? 10 : 12) : 3;
            reasons.Add("提供持续效果");
        }
        AdviceTag[] archetypes = [AdviceTag.Poison, AdviceTag.Doom, AdviceTag.Shiv];
        double synergy = 0;
        foreach (AdviceTag tag in archetypes)
        {
            if (!card.Tags.HasFlag(tag)) continue;
            int support = context.Deck.Count(c => c.Tags.HasFlag(tag));
            if (support >= 2) synergy = Math.Max(synergy, Math.Min(7, support * 1.5));
        }
        if (synergy > 0) { value += synergy; reasons.Add("契合现有体系"); }
        value += AdviceMechanics.Value(context, card, reasons);
        if ((context.Relics.Contains("KUNAI") || context.Relics.Contains("SHURIKEN")
                || context.Relics.Contains("ORNAMENTAL_FAN")) && card.Attack && card.Cost <= 1)
        { value += 4; reasons.Add("配合连续攻击遗物"); }
        if (context.Relics.Contains("SNECKO_EYE") && card.Cost >= 2)
        { value += 5; reasons.Add("配合费用随机遗物"); }
        else if (card.Cost >= 2 && !context.Deck.Any(c => c.Tags.HasFlag(AdviceTag.Energy)))
        { value -= 3 * (card.Cost - 1); reasons.Add("费用偏高"); }
        int copies = context.Deck.Count(c => c.Id == card.Id);
        if (copies > 0 && card.Id != "CLAW") { value -= copies * 3; reasons.Add("已有同名牌"); }
        if (copies > 0 && card.Id == "CLAW") { value += Math.Min(6, copies * 2); reasons.Add("同名爪击共享本战成长"); }
        if (card.Basic) { value -= 7; reasons.Add("基础牌收益有限"); }
        if (size > 20 && !card.Tags.HasFlag(AdviceTag.Draw))
        { value -= Math.Min(8, (size - 20) * 0.5); reasons.Add("牌组已经偏厚"); }
        if (reasons.Count == 0) reasons.Add("通用牌面价值");
        return value;
    }

    internal static double RemovalValue(AdviceContext context, int index)
    {
        AdviceCard card = context.Deck[index];
        AdviceContext remaining = context with
        {
            Deck = context.Deck.Where((_, i) => i != index).ToArray(),
        };
        if (card.Curse) return context.Relics.Contains("DU_VU_DOLL") ? 18 : 28;
        double value;
        if (card.Basic)
        {
            bool scarce = card.Attack
                ? remaining.Deck.Count(c => c.Attack && !c.Basic) < 2
                : remaining.Deck.Count(c => c.Block > 0 && !c.Basic) < 2;
            value = (scarce ? 3 : 15) - card.UpgradeLevel * 4;
        }
        else
            value = Math.Max(-8, -CardValue(remaining, card, []) * 0.5);
        // Compare each surviving component with itself excluded on both sides.
        // This accounts for breaking the last source, rather than treating removal
        // as merely the opposite of buying one more copy.
        double synergyChange = 0;
        for (int i = 0; i < context.Deck.Count; i++)
        {
            if (i == index) continue;
            AdviceCard survivor = context.Deck[i];
            AdviceContext before = context with
            {
                Deck = context.Deck.Where((_, j) => j != i).ToArray(),
            };
            AdviceContext after = context with
            {
                Deck = context.Deck.Where((_, j) => j != i && j != index).ToArray(),
            };
            synergyChange += AdviceMechanics.Value(after, survivor, [])
                - AdviceMechanics.Value(before, survivor, []);
        }
        return value + Math.Clamp(synergyChange, -20, 20);
    }

    private static (double, bool) RelicValue(AdviceContext context, string id, List<string> reasons)
    {
        double value = id switch
        {
            "VAJRA" or "ODDLY_SMOOTH_STONE" => 20,
            "ANCHOR" or "HORN_CLEAT" or "CAPTAINS_WHEEL" => 22,
            "BAG_OF_PREPARATION" or "LANTERN" or "HAPPY_FLOWER" => 25,
            "KUNAI" or "SHURIKEN" or "ORNAMENTAL_FAN" =>
                context.Deck.Count(c => c.Attack && c.Cost <= 1) >= 6 ? 34 : 14,
            "PEN_NIB" or "AKABEKO" => context.Deck.Count(c => c.Attack) >= 5 ? 23 : 10,
            "LETTER_OPENER" => context.Deck.Count(c => !c.Attack) >= 8 ? 26 : 12,
            "TOUGH_BANDAGES" or "TINGSHA" =>
                context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Discard)) >= 3 ? 34 : 5,
            "CHARONS_ASHES" or "DEAD_BRANCH" =>
                context.Deck.Count(c => c.Tags.HasFlag(AdviceTag.Exhaust)) >= 3 ? 34 : 10,
            "BLOOD_VIAL" or "MEAT_ON_THE_BONE" => context.Hp * 2 < context.MaxHp ? 26 : 17,
            "STRAWBERRY" or "PEAR" or "MANGO" => context.Hp * 2 < context.MaxHp ? 22 : 13,
            "MEMBERSHIP_CARD" => context.Act < 2 ? 32 : 12,
            "SMILING_MASK" => context.Deck.Count(c => c.Basic || c.Curse) >= 6 ? 22 : 6,
            "POTION_BELT" => context.EmptyPotionSlots == 0 ? 20 : 10,
            _ => double.NaN,
        };
        if (double.IsNaN(value)) return (0, false);
        reasons.Add("遗物与当前牌组配合");
        return (value, true);
    }

    private static (double, bool) PotionValue(AdviceContext context, string id, List<string> reasons)
    {
        double value = id switch
        {
            "FIRE_POTION" or "EXPLOSIVE_POTION" or "BLOCK_POTION" => 13,
            "STRENGTH_POTION" or "DEXTERITY_POTION" or "ENERGY_POTION" or "SWIFT_POTION" => 15,
            "GAMBLERS_BREW" or "COLORLESS_POTION" or "SKILL_POTION" or "ATTACK_POTION" => 12,
            "POWER_POTION" or "DUPLICATOR" or "LIQUID_MEMORIES" => 18,
            "FAIRY_IN_A_BOTTLE" or "GHOST_IN_A_JAR" => 26,
            "REGEN_POTION" or "FRUIT_JUICE" => 20,
            "POISON_POTION" => context.Deck.Any(c => c.Tags.HasFlag(AdviceTag.Poison)) ? 18 : 11,
            "FOCUS_POTION" => AdviceMechanics.SourceSupply(context, AdviceRole.FocusOrbSource) > 0 ? 20 : -10,
            _ => double.NaN,
        };
        if (double.IsNaN(value)) return (0, false);
        if (context.Hp * 2 < context.MaxHp) { value += 8; reasons.Add("低生命时优先保命"); }
        else reasons.Add("提供一次性战斗资源");
        return (value, true);
    }
}
