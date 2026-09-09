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
Console.WriteLine($"RUN_ADVICE_CAPTURE_CHECKS_OK checks={checks}");
