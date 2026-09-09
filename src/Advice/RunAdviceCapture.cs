using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

// Called only on the UI thread. The evaluator receives values, never live model references.
internal static class RunAdviceCapture
{
    private static readonly HashSet<string> DrawCards = new(StringComparer.Ordinal)
    {
        "SHRUG_IT_OFF", "POMMEL_STRIKE", "BATTLE_TRANCE", "OFFERING", "BURNING_PACT",
        "ACROBATICS", "BACKFLIP", "ADRENALINE", "PREPARED", "DAGGER_THROW", "EXPERTISE",
        "CALCULATED_GAMBLE", "REFLEX", "ESCAPE_PLAN", "PREDATOR", "FINESSE", "FLASH_OF_STEEL",
        "COOLHEADED", "COMPILE_DRIVER", "SKIM", "SWEEPING_BEAM", "OVERCLOCK", "HEATSINKS",
        "GRAVEBLAST", "SOUL", "NEUROSURGE", "GUIDING_STAR", "GLOW", "RELAX",
    };

    internal static AdviceContext Capture(Player player) => new(
        player.Deck.Cards.Select(Card).ToArray(),
        player.Relics.Select(r => r.Id.Entry).ToHashSet(StringComparer.Ordinal),
        player.Gold, player.Creature.CurrentHp, player.Creature.MaxHp,
        player.RunState.CurrentActIndex, player.PotionSlots.Count(p => p is null),
        RelicValue(player, "DIVINE_RIGHT", "Stars"),
        RelicValue(player, "BOUND_PHYLACTERY", "Summon"),
        RelicValue(player, "CRACKED_CORE", "Lightning"), player.MaxEnergy);

    private static double RelicValue(Player player, string id, string variable) => player.Relics
        .Where(r => r.GetType().Assembly == typeof(RelicModel).Assembly && r.Id.Entry == id)
        .Sum(r => r.DynamicVars.TryGetValue(variable, out var value) ? (double)value.BaseValue : 0);

    internal static AdviceCard Card(CardModel card)
    {
        double Value(string name) => card.DynamicVars.TryGetValue(name, out var value)
            ? (double)value.BaseValue : 0;
        string id = card.Id.Entry;
        AdviceTag tags = AdviceTag.None;
        double draw = DrawCards.Contains(id) ? Math.Max(1, Value("Cards")) : 0;
        if (draw > 0) tags |= AdviceTag.Draw;
        if (Value("Energy") > 0) tags |= AdviceTag.Energy;
        if (card.TargetType == TargetType.AllEnemies && Value("Damage") > 0) tags |= AdviceTag.Area;
        // Only reviewed persistent effects receive a scaling bonus.
        if (card.Type == CardType.Power && id is "DARK_EMBRACE" or "FEEL_NO_PAIN"
            or "CORRUPTION" or "ACCURACY" or "HAUNT" or "DEVOUR_LIFE" or "DEFRAGMENT"
            or "NOXIOUS_FUMES" or "INFINITE_BLADES") tags |= AdviceTag.Scaling;
        if (Value("Poison") > 0) tags |= AdviceTag.Poison;
        if (Value("Doom") > 0) tags |= AdviceTag.Doom;
        if (card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Exhaust)) tags |= AdviceTag.Exhaust;
        tags |= id switch
        {
            "ACROBATICS" or "PREPARED" or "CALCULATED_GAMBLE" or "DAGGER_THROW" or "SURVIVOR"
                or "REFLEX" or "TACTICIAN" => AdviceTag.Discard,
            "BLADE_DANCE" or "CLOAK_AND_DAGGER" or "INFINITE_BLADES" or "ACCURACY" => AdviceTag.Shiv,
            "CATALYST" or "NOXIOUS_FUMES" or "DEADLY_POISON" or "BOUNCING_FLASK" => AdviceTag.Poison,
            "DARK_EMBRACE" or "FEEL_NO_PAIN" or "CORRUPTION" => AdviceTag.Exhaust,
            "ZAP" or "DUALCAST" or "BALL_LIGHTNING" or "COOLHEADED" or "GLACIER"
                or "DEFRAGMENT" or "CAPACITOR" or "FOCUS" or "LOOP" => AdviceTag.Orb,
            "ADRENALINE" or "OFFERING" or "BLOODLETTING" or "TURBO" => AdviceTag.Energy,
            _ => AdviceTag.None,
        };
        bool vanilla = card.GetType().Assembly == typeof(CardModel).Assembly;
        AdviceRole roles = vanilla ? AdviceMechanics.Roles(id) : AdviceRole.None;
        if (card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Exhaust))
            roles |= AdviceRole.SelfExhaust;
        double amount = id switch
        {
            "GRAVE_WARDEN" or "REAVE" => Value("Cards"),
            "BLADE_DANCE" or "CLOAK_AND_DAGGER" or "FAN_OF_KNIVES"
                => CardMechanismFacts.ImmediateShivSupply(id, (int)Value("Cards"), (int)Value("Shivs")),
            "SEVERANCE" => 3,
            "GLACIER" => 2,
            "ICE_LANCE" or "CONSUMING_SHADOW" => Value("Repeat"),
            _ => 1,
        };
        double availability = id switch
        {
            "GRAVE_WARDEN" or "REAVE" => 0.5,
            "SEVERANCE" => (1 + 0.5 + 0.25) / 3,
            "INFINITE_BLADES" or "NOXIOUS_FUMES" => 0.5, // Supply starts at a later BeforeHandDraw.
            "CHILL" => 0.5, // Unknown future enemy count; do not assume a crowd.
            _ => 1,
        };
        return new AdviceCard(id, vanilla
            ? AdviceMechanics.FaceDamage(id, Value("Damage"), Value("CalculationBase"), Value("Repeat"))
            : Value("Damage"), Value("Block"), draw,
            card.EnergyCost.CostsX ? 0 : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local)),
            card.Type == CardType.Attack, card.IsBasicStrikeOrDefend,
            card.Type is CardType.Curse or CardType.Status, card.IsRemovable, tags,
            vanilla, card.CurrentUpgradeLevel, roles, vanilla ? AdviceMechanics.StarGain(id, Value("Stars")) : 0,
            card.HasStarCostX ? 0 : Math.Max(0, card.CurrentStarCost),
            vanilla ? amount : 1, vanilla ? availability : 1,
            roles.HasFlag(AdviceRole.SelfExhaust)
                || card.Type == CardType.Power && !(vanilla && id is "INFINITE_BLADES" or "CORRUPTION" or "NOXIOUS_FUMES"),
            vanilla && (roles != AdviceRole.None || tags != AdviceTag.None || card.IsBasicStrikeOrDefend)
                ? AdviceCoverage.Partial : AdviceCoverage.Unreviewed, card.EnergyCost.CostsX, card.HasStarCostX);
    }

    internal static AdviceOffer Offer(MerchantEntry entry, int index) => entry switch
    {
        MerchantCardEntry card => new($"shop:{index}", AdviceKind.Card,
            card.CreationResult?.Card.Id.Entry ?? "", entry.Cost,
            card.CreationResult is { } creation ? Card(creation.Card) : null, entry.IsStocked),
        MerchantRelicEntry relic => new($"shop:{index}", AdviceKind.Relic,
            relic.Model?.Id.Entry ?? "", entry.Cost, Available: entry.IsStocked,
            FallbackValue: relic.Model is { } r && r.GetType().Assembly == typeof(RelicModel).Assembly
                ? r.Rarity.ToString() switch { "Rare" => 28, "Uncommon" => 23, "Shop" => 23, _ => 18 } : null),
        MerchantPotionEntry potion => new($"shop:{index}", AdviceKind.Potion,
            potion.Model?.Id.Entry ?? "", entry.Cost, Available: entry.IsStocked,
            FallbackValue: potion.Model is { } p && p.GetType().Assembly == typeof(PotionModel).Assembly
                ? p.Rarity.ToString() switch { "Rare" => 18, "Uncommon" => 14, _ => 11 } : null),
        MerchantCardRemovalEntry => new($"shop:{index}", AdviceKind.Removal, "remove",
            entry.Cost, Available: entry.IsStocked),
        _ => new($"shop:{index}", AdviceKind.Relic, "unsupported", entry.Cost, Available: false),
    };
}
