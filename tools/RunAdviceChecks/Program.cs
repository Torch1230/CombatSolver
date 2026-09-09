using CombatSolver;

AdviceContext context = new([], new HashSet<string>(), 0, 50, 80, 0, 1);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
double Score(int gold, int cost) => RunAdvice.Rank(context with { Gold = gold },
    [new("relic", AdviceKind.Relic, "VAJRA", cost)], true)[0].Score;

Check(Math.Abs(Score(149, 100) - Score(150, 100)) < 1e-9,
    "One gold at the reserve threshold must not cause a score jump");
Check(Math.Abs(Score(200, 100) - 12.5) < 1e-9,
    "A purchase crossing the reserve must price both portions");
for (int cost = 0; cost <= 300; cost++)
{
    for (int gold = cost; gold < 500; gold++)
    {
        double change = Score(gold + 1, cost) - Score(gold, cost);
        Check(change >= -1e-9 && change <= 0.030000001,
            "Additional gold must improve scores continuously");
    }
    if (cost < 300)
        Check(Score(500, cost + 1) < Score(500, cost), "Higher prices must reduce scores");
}
var ratings = RunAdvice.Rank(context with { Gold = 99 },
    [new("relic", AdviceKind.Relic, "VAJRA", 100), new("save", AdviceKind.Skip, "save")], true);
Check(!ratings[0].Available && ratings[0].Rank == 0, "Unaffordable offers remain unranked");
Check(ratings[1].Score == 0 && ratings[1].Rank == 1, "Saving remains the zero baseline");
Check(Score(200, -10) == Score(200, 0), "Negative prices must not invent a bonus");
AdviceCard plain = new("TEST", 0, 0, 0, 1, false, false, false, true, AdviceTag.None);
double Mechanic(AdviceContext c, AdviceCard card) => AdviceMechanics.Value(c, card, []);
foreach (var pair in new[] {
    (AdviceRole.DiscardSource, AdviceRole.DiscardPayoff),
    (AdviceRole.ExhaustSource, AdviceRole.ExhaustPayoff),
    (AdviceRole.SelfExhaust, AdviceRole.ExhaustPayoff) })
{
    var source = plain with { Roles = pair.Item1 };
    var payoff = plain with { Roles = pair.Item2 };
    var supported = context with { Deck = [source] };
    Check(Mechanic(supported, payoff) > Mechanic(context, payoff), "Payoffs require sources");
    Check(Mechanic(context with { Deck = [payoff] }, source) > Mechanic(context, source), "Sources need payoffs");
    Check(Mechanic(context with { Deck = [payoff, source, source] }, source)
        < Mechanic(context with { Deck = [payoff] }, source), "Extra sources have diminishing returns");
    Check(Mechanic(context with { Deck = [payoff, payoff] }, payoff) < 0,
        "Payoffs alone cannot supply their own trigger");
}
var drawCard = plain with { Tags = AdviceTag.Draw };
var stopsDraw = drawCard with { Roles = AdviceRole.StopsDraw };
Check(Mechanic(context with { Deck = [drawCard, drawCard] }, stopsDraw) < Mechanic(context, stopsDraw),
    "NoDraw conflicts with other draw cards");
var stars = plain with { Stars = 1 }; var spender = plain with { StarCost = 2 };
Check(Mechanic(context with { Deck = [spender] }, stars) > Mechanic(context, stars), "Star supply matches demand");
Check(Mechanic(context with { Deck = [stars] }, spender) > Mechanic(context, spender), "Star cost considers supply");
Check(AdviceMechanics.StarGain("GLOW", 2) == 2 && AdviceMechanics.StarGain("UNKNOWN", 2) == 0,
    "A Stars variable alone is not a production rule");
Check(AdviceMechanics.Roles("REFLEX") == AdviceRole.DiscardPayoff
    && AdviceMechanics.Roles("ACROBATICS") == AdviceRole.DiscardSource,
    "Sly payoff and active discard differ");
Check(AdviceMechanics.Roles("FEEL_NO_PAIN") == AdviceRole.ExhaustPayoff
    && AdviceMechanics.Roles("TRUE_GRIT") == AdviceRole.ExhaustSource, "Exhaust source and payoff differ");
var poison = plain with { Tags = AdviceTag.Poison };
var shiv = plain with { Tags = AdviceTag.Shiv };
var mixed = context with { Deck = [poison, poison, shiv, shiv, shiv, shiv] };
Check(RunAdvice.CardValue(mixed, plain with { Tags=AdviceTag.Poison|AdviceTag.Shiv }, [])
    == RunAdvice.CardValue(mixed, shiv, []), "Stronger synergy must not depend on tag enumeration order");
var claw = plain with { Id="CLAW", Damage=3, Attack=true, Cost=0 };
Check(RunAdvice.CardValue(context with { Deck=[claw] }, claw, []) > RunAdvice.CardValue(context with { Deck=[claw with { Id="OTHER_ATTACK" }] }, claw, []),
    "Claw copies must not receive the generic duplicate penalty");
foreach (var pair in new[] {
    (AdviceRole.SoulSource, AdviceRole.SoulPlayPayoff),
    (AdviceRole.SoulSource, AdviceRole.SoulExhaustPayoff),
    (AdviceRole.SummonSource, AdviceRole.OstyAttack),
    (AdviceRole.FocusOrbSource, AdviceRole.FocusSource) })
{
    var source = plain with { Roles = pair.Item1 };
    var payoff = plain with { Roles = pair.Item2 };
    Check(Mechanic(context with { Deck = [source] }, payoff) > Mechanic(context, payoff), "Complex payoff needs a source");
    Check(Mechanic(context with { Deck = [payoff] }, source) > Mechanic(context, source), "Complex source supports payoff");
}
var focus = plain with { Roles = AdviceRole.FocusSource, Tags = AdviceTag.Orb };
var plasma = plain with { Roles = AdviceRole.PlasmaSource, Tags = AdviceTag.Orb };
Check(RunAdvice.CardValue(context with { Deck = [plasma, plasma] }, focus, [])
    == RunAdvice.CardValue(context with { Deck = [plain, plain] }, focus, []), "Plasma cannot create Focus synergy through generic Orb tags");
var forge = plain with { Roles = AdviceRole.ForgeSource };
Check(Mechanic(context, forge) > 0, "Forge supplies its own Blade");
Check(Mechanic(context with { Deck = [forge, forge] }, forge) < Mechanic(context, forge), "Forge sources saturate");
Check(AdviceMechanics.Roles("UNKNOWN") == AdviceRole.None, "Unknown cards have no inferred mechanics");
var soul = plain with { Roles = AdviceRole.SoulSource };
Check(Mechanic(context with { Deck = [plain with { Roles = AdviceRole.SoulPlayPayoff | AdviceRole.SoulExhaustPayoff }] }, soul)
    == Mechanic(context with { Deck = [plain with { Roles = AdviceRole.SoulPlayPayoff }] }, soul), "Soul source synergy is not double counted");
var discardSource = plain with { Id = "SOURCE", Roles = AdviceRole.DiscardSource };
var discardPayoff = plain with { Id = "PAYOFF", Roles = AdviceRole.DiscardPayoff };
Check(RunAdvice.RemovalValue(context with { Deck = [discardSource, discardPayoff] }, 0)
    < RunAdvice.RemovalValue(context with { Deck = [discardSource, plain] }, 0),
    "Removing the only source must account for the surviving payoff");
Check(RunAdvice.RemovalValue(context with { Deck = [discardSource, discardPayoff, discardSource] }, 0)
    > RunAdvice.RemovalValue(context with { Deck = [discardSource, discardPayoff, plain] }, 0),
    "A redundant source is safer to remove than the last source");
Check(RunAdvice.RemovalValue(context with { Deck = [plain] }, 0) == 3,
    "Removal must not include a duplicate penalty for the card being removed");
var upgradedBasic = plain with { Basic = true, UpgradeLevel = 1 };
Check(RunAdvice.RemovalValue(context with { Deck = [upgradedBasic, upgradedBasic with { UpgradeLevel = 0 }] }, 0)
    < RunAdvice.RemovalValue(context with { Deck = [upgradedBasic, upgradedBasic with { UpgradeLevel = 0 }] }, 1),
    "Removal uses the exact deck index, including upgrades");
Check(Mechanic(context with { StartingStars = 3 }, spender) > Mechanic(context, spender),
    "Starting Stars can cover an initial play");
Check(Mechanic(context with { StartingStars = 3 }, spender) < Mechanic(context with { Deck = [stars] }, spender),
    "Starting Stars do not imply sustainable production");
Check(Mechanic(context with { SummonSupply = 1 }, plain with { Roles = AdviceRole.OstyAttack })
    > Mechanic(context, plain with { Roles = AdviceRole.OstyAttack }), "Starter summon supports Osty attacks");
Check(Mechanic(context with { InitialFocusOrbs = 1 }, focus) > Mechanic(context, focus),
    "Initial orbs support Focus without a card source");
Check(AdviceMechanics.Roles("DARKNESS") == AdviceRole.FocusOrbSource,
    "Dark orb sources must not be omitted from Focus advice");
Console.WriteLine($"RUN_ADVICE_CHECKS_OK checks={checks}");
