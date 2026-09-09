using CombatSolver;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
int checks = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
CardModel Card(string id, params (string Name, decimal Value)[] vars)
{
    var card = new CardModel { Id = new(id), Type = CardType.Skill };
    foreach (var (name, value) in vars) card.DynamicVars[name] = new() { BaseValue = value };
    return card;
}
var twin = Card("TWIN_STRIKE", ("Damage", 5));
Check(RunAdviceCapture.Card(twin).Damage == 10, "Capture must use total fixed-hit damage");
var storm = Card("SOUL_STORM", ("CalculationBase", 9));
Check(RunAdviceCapture.Card(storm).Damage == 9, "Capture must preserve dynamic base damage");
var reave = Card("REAVE", ("Cards", 2)); reave.Keywords.Add(CardKeyword.Exhaust);
var captured = RunAdviceCapture.Card(reave);
Check(captured.SourceAmount == 2 && captured.Availability == 0.5 && captured.SingleUse, "Capture must carry quantity, delay and single use");
reave.DynamicVars["Cards"].BaseValue = 99;
Check(captured.SourceAmount == 2, "Captured values must not follow live mutation");
Check(RunAdviceCapture.Card(Card("SEVERANCE")).SourceAmount == 3, "Severance generates three Souls");
var unknownPower = Card("UNKNOWN_POWER"); unknownPower.Type = CardType.Power;
Check(!RunAdviceCapture.Card(unknownPower).Tags.HasFlag(AdviceTag.Scaling), "Unknown Power must not imply scaling");
var player = new Player { MaxEnergy = 4 };
foreach (var (id, variable, amount) in new[] { ("DIVINE_RIGHT", "Stars", 3m), ("BOUND_PHYLACTERY", "Summon", 1m), ("CRACKED_CORE", "Lightning", 1m) })
{
    var relic = new RelicModel { Id = new(id) }; relic.DynamicVars[variable] = new() { BaseValue = amount }; player.Relics.Add(relic);
}
var context = RunAdviceCapture.Capture(player);
Check(context.StartingStars == 3 && context.SummonSupply == 1 && context.InitialFocusOrbs == 1 && context.BaseEnergy == 4, "Capture starter supplies and base energy");
Check(RunAdviceCapture.Card(Card("UNKNOWN")).Coverage == AdviceCoverage.Unreviewed, "Unknown vanilla card must not claim reviewed coverage");
var xCard = Card("X_CARD"); xCard.EnergyCost.CostsX = true; xCard.HasStarCostX = true;
var xSnapshot = RunAdviceCapture.Card(xCard);
Check(xSnapshot.EnergyX && xSnapshot.StarsX && xSnapshot.Cost == 0 && xSnapshot.StarCost == 0,
    "Capture X identity without fabricated fixed prices");
var blades = Card("INFINITE_BLADES"); blades.Type = CardType.Power;
var bladeSupply = RunAdviceCapture.Card(blades);
Check(bladeSupply.Roles.HasFlag(AdviceRole.ShivSource) && !bladeSupply.SingleUse && bladeSupply.Availability == 0.5,
    "Persistent Shiv generation is recurring but delayed, despite a single Power play");
var corruption = Card("CORRUPTION"); corruption.Type = CardType.Power;
Check(!RunAdviceCapture.Card(corruption).SingleUse, "Corruption enables repeated exhaust triggers after setup");
foreach (var (id, variable, baseline, upgraded, source) in new[]
{
    ("ACCURACY", "AccuracyPower", 4m, 6m, AdviceRole.ShivSource),
    ("FEEL_NO_PAIN", "Power", 3m, 4m, AdviceRole.ExhaustSource),
    ("REFLEX", "Cards", 2m, 3m, AdviceRole.DiscardSource),
    ("TACTICIAN", "Energy", 1m, 2m, AdviceRole.DiscardSource),
    ("HAUNT", "HpLoss", 7m, 9m, AdviceRole.SoulSource),
    ("DEVOUR_LIFE", "DevourLifePower", 1m, 2m, AdviceRole.SoulSource),
    ("DEFRAGMENT", "FocusPower", 1m, 2m, AdviceRole.FocusOrbSource),
    ("ACCELERANT", "Accelerant", 1m, 2m, AdviceRole.PoisonSource),
})
{
    var native = Card(id, (variable, baseline));
    var normal = RunAdviceCapture.Card(native);
    native.DynamicVars[variable].BaseValue = upgraded;
    var upgrade = RunAdviceCapture.Card(native);
    Check(normal.PayoffWeight == 1 && upgrade.PayoffWeight > normal.PayoffWeight,
        $"{id}: effect upgrades must survive capture without mutating the old snapshot");
    var supply = Card("REVIEWED_SOURCE");
    var supplied = context with { Relics = new HashSet<string>(), StartingStars = 0,
        InitialFocusOrbs = 0, SummonSupply = 0,
        Deck = [RunAdviceCapture.Card(supply) with { Roles = source }] };
    AdviceOffer[] offers = [new("normal", AdviceKind.Card, id, Card: normal),
        new("upgraded", AdviceKind.Card, id, Card: upgrade)];
    foreach (bool shop in new[] { false, true })
    {
        var ranked = RunAdvice.Rank(supplied, offers, shop);
        Check(ranked[1].Score > ranked[0].Score && ranked[1].Parts!.Synergy > ranked[0].Parts!.Synergy,
            $"{id}: upgrades must improve supported reward and equal-price shop scores");
    }
    var before = DeckMechanismProfile.Capture(supplied with { Deck = [supplied.Deck[0], normal] });
    var after = DeckMechanismProfile.Capture(supplied with { Deck = [supplied.Deck[0], upgrade] });
    Check(after.Mechanisms.Sum(a => a.Balance.Readiness) > before.Mechanisms.Sum(a => a.Balance.Readiness),
        $"{id}: profile and offer evaluation must use the same upgraded effect");
    var absent = supplied with { Deck = [] };
    var unsupported = RunAdvice.Rank(absent, offers, false);
    Check(unsupported[1].Parts!.Synergy == unsupported[0].Parts!.Synergy,
        $"{id}: larger payoffs must not invent a missing source");
}
foreach (var (id, variable, count, block) in new[]
{
    ("BLADE_DANCE", "Cards", 3m, 0m),
    ("CLOAK_AND_DAGGER", "Cards", 1m, 6m),
    ("FAN_OF_KNIVES", "Shivs", 4m, 0m),
})
{
    var generator = Card(id, (variable, count), ("Block", block));
    var normal = RunAdviceCapture.Card(generator);
    generator.DynamicVars[variable].BaseValue++;
    var upgraded = RunAdviceCapture.Card(generator);
    Check(normal.Damage == (double)count * 4 && upgraded.Damage == normal.Damage + 4 && normal.Block == (double)block,
        $"{id}: retain generated base output and direct block without losing quantity upgrades");
    Check(RunAdvice.CardValue(context, normal, []) > RunAdvice.CardValue(context, normal with { Damage = 0 }, []),
        $"{id}: standalone generated damage must affect advice even without an amplifier");
}
Check(RunAdviceCapture.Card(Card("INFINITE_BLADES")).Damage == 0,
    "Delayed recurring supply must not be treated as an immediate guaranteed Shiv batch");
Console.WriteLine($"RUN_ADVICE_CAPTURE_CHECKS_OK checks={checks}");
