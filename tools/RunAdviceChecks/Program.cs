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
var sourceDeck = context with { Deck = [plain with { Roles = AdviceRole.SoulPlayPayoff }] };
Check(Mechanic(sourceDeck, soul with { SourceAmount = 2 }) > Mechanic(sourceDeck, soul),
    "Higher source production supports a payoff more");
Check(Mechanic(sourceDeck, soul with { Availability = 0.5 }) < Mechanic(sourceDeck, soul),
    "Delayed generation is discounted relative to immediate generation");
Check(Mechanic(sourceDeck, soul with { SingleUse = true }) < Mechanic(sourceDeck, soul),
    "Single-use generation is not valued as repeatable supply");
var partsRating = RunAdvice.Rank(sourceDeck with { Gold = 500 },
    [new("soul", AdviceKind.Card, "SOURCE", 75, soul)], true)[0];
Check(partsRating.Parts is { } parts && Math.Abs(parts.Base + parts.Synergy + parts.Price - partsRating.Score) < 1e-9,
    "Visible score components must sum to the score including price");
Check(partsRating.Parts!.Synergy > 0 && partsRating.Parts.Price < 0,
    "Synergy and purchase cost must be separately visible");
Check(RunAdvice.Rank(context, [new("unknown", AdviceKind.Card, "UNKNOWN", Card: plain with { Known = false })], false)[0].Rank == 0,
    "Coverage labels must not grant unsupported cards a rank");
Check(AdviceMechanics.FaceDamage("TWIN_STRIKE", 5, 0, 0) == 10, "Fixed multihit damage captures both hits");
Check(AdviceMechanics.FaceDamage("SWORD_BOOMERANG", 3, 0, 4) == 12, "Variable multihit damage uses captured repeat");
Check(AdviceMechanics.FaceDamage("SOUL_STORM", 0, 9, 0) == 9, "Dynamic cards retain their unconditional base damage");
Check(AdviceMechanics.FaceDamage("UNREVIEWED", 5, 100, 4) == 5, "Unknown dynamic variables do not invent damage");
Check(Mechanic(context, forge with { Cost = 3 }) < Mechanic(context, forge with { Cost = 0 }),
    "Forge setup cost affects its payoff window");
Check(Mechanic(context with { Deck = [plain with { Tags = AdviceTag.Energy }] }, forge with { Cost = 2 })
    > Mechanic(context, forge with { Cost = 2 }), "Energy support eases Forge setup pressure");
Check(Mechanic(context with { Deck = [stopsDraw] }, soul) < Mechanic(context, soul),
    "Soul generation conflicts with NoDraw even without direct draw tags");
Check(Mechanic(context with { BaseEnergy = 4 }, forge with { Cost = 2 }) > Mechanic(context with { BaseEnergy = 3 }, forge with { Cost = 2 }),
    "Forge must use captured base energy rather than a fixed three-energy assumption");
var largeAttack = plain with { Damage = 30, Attack = true };
Check(RunAdvice.CardValue(context, largeAttack with { Damage = 40 }, []) > RunAdvice.CardValue(context, largeAttack, []),
    "Large attacks must remain distinguishable above the old cap");
double highGain = RunAdvice.CardValue(context, largeAttack with { Damage = 50 }, []) - RunAdvice.CardValue(context, largeAttack with { Damage = 40 }, []);
double lowGain = RunAdvice.CardValue(context, largeAttack with { Damage = 40 }, []) - RunAdvice.CardValue(context, largeAttack, []);
Check(highGain > 0 && highGain < lowGain, "High face values have diminishing, positive returns");
var genericPair = RunAdvice.Rank(context with { Deck = [poison, poison] },
    [new("poison", AdviceKind.Card, "POISON", Card: poison)], false)[0];
Check(genericPair.Parts!.Synergy == 3, "Generic archetype synergy belongs in the synergy component");
var relicPair = RunAdvice.Rank(context with { Relics = new HashSet<string> { "KUNAI" } },
    [new("attack", AdviceKind.Card, "ATTACK", Card: largeAttack)], false)[0];
Check(relicPair.Parts!.Synergy == 4, "Relic synergy belongs in the synergy component");
var finiteStars = stars with { Stars = 4, SingleUse = true };
List<string> finiteReasons = [];
AdviceMechanics.Value(context with { Deck = [finiteStars] }, spender, finiteReasons);
Check(!finiteReasons.Contains("牌组具备产星来源") && finiteReasons.Contains("一次性产星可支持有限打出"),
    "Single-use Stars must not be described as repeatable production");
Check(Mechanic(context with { Deck = [spender] }, stars with { SingleUse = true }) < Mechanic(context with { Deck = [spender] }, stars),
    "Single-use Stars have less supply value");
Check(AdviceMechanics.StarSupply(stars with { Availability = 0.5 }) < AdviceMechanics.StarSupply(stars),
    "Delayed Stars are discounted");
AdviceCard[] attackDeck = Enumerable.Range(0, 6).Select(i => largeAttack with { Id = "ATTACK_" + i }).ToArray();
Check(RunAdvice.RemovalValue(context with { Deck = attackDeck, Relics = new HashSet<string> { "KUNAI" } }, 0)
    < RunAdvice.RemovalValue(context with { Deck = attackDeck }, 0),
    "Removal must account for crossing an owned relic's deck-support threshold");
var shuffledDeck = attackDeck.Reverse().ToArray();
Check(RunAdvice.RemovalValue(context with { Deck = attackDeck }, 0)
    == RunAdvice.RemovalValue(context with { Deck = shuffledDeck }, 5),
    "Removal depends on the selected card and deck composition, not ordering");
Check(RunAdvice.RemovalValue(context with { Deck = [poison, poison with { Id = "P2" }, poison with { Id = "P3" }] }, 0)
    < RunAdvice.RemovalValue(context with { Deck = [poison, plain with { Id = "P2" }, plain with { Id = "P3" }] }, 0),
    "Removal must include generic archetype losses in surviving cards");
Check(Mechanic(context with { Deck = [stars with { Availability = 0 }] }, spender) == Mechanic(context, spender),
    "An unavailable recurring source cannot satisfy Star demand");
Check(Mechanic(context with { StartingStars = 1, Deck = [stars with { Stars = 2, SingleUse = true }] }, spender)
    > Mechanic(context, spender), "Initial and finite Star supplies combine");
Check(new MechanismBalance(4, 0).Readiness == 0 && new MechanismBalance(0, 4).Readiness == 0,
    "Sources or payoffs alone are not a functioning mechanism");
Check(new MechanismBalance(1, 4).Marginal(1, 0) > new MechanismBalance(4, 1).Marginal(1, 0),
    "A source should be more valuable when supply is the bottleneck");
Check(new MechanismBalance(4, 1).Marginal(0, 1) > new MechanismBalance(1, 4).Marginal(0, 1),
    "A payoff should be more valuable when payoffs are the bottleneck");
Check(Math.Abs(new MechanismBalance(1, 2).Marginal(1, 0) + new MechanismBalance(2, 2).Marginal(0, 1)
    - new MechanismBalance(1, 2).Marginal(0, 1) - new MechanismBalance(1, 3).Marginal(1, 0)) < 1e-9,
    "Deck readiness gains must be independent of acquisition order");
var mixedProfile = DeckMechanismProfile.Capture(context with { Deck = [discardSource, discardPayoff, soul, plain with { Roles = AdviceRole.SoulPlayPayoff }] });
Check(mixedProfile.Mechanisms.Count(a => a.Balance.Readiness > 0) == 2,
    "A deck can have multiple active mechanisms without an exclusive label");
var resourceProfile = DeckMechanismProfile.Capture(context with { Deck = [drawCard, stars, plain with { Tags = AdviceTag.Energy, Cost = 2 }, plain with { Block = 5 }] });
Check(resourceProfile.DrawCards == 1 && resourceProfile.EnergyCards == 1 && resourceProfile.DefensiveCards == 1 && resourceProfile.ExpensiveCards == 1,
    "Operating capabilities must be counted separately from mechanisms and Stars");
Check(DeckMechanismProfile.DrawDemand(20, 1) > DeckMechanismProfile.DrawDemand(20, 5), "Draw support has diminishing demand");
Check(DeckMechanismProfile.DrawDemand(30, 2) > DeckMechanismProfile.DrawDemand(15, 2), "Larger decks need more draw support");
Check(DeckMechanismProfile.EnergyDemand(6, 0) > DeckMechanismProfile.EnergyDemand(6, 3), "Energy supply reduces the expensive-card bottleneck");
var shortageProfile = DeckMechanismProfile.Capture(context with { Deck = Enumerable.Repeat(plain with { Cost = 2 }, 20).ToArray() });
Check(shortageProfile.DrawShortage && shortageProfile.EnergyShortage && shortageProfile.DefenseShortage,
    "A thick expensive deck without draw, energy or block should expose all three gaps");
var xAttack = largeAttack with { Cost = 0, EnergyX = true };
var xRelicRating = RunAdvice.Rank(context with { Relics = new HashSet<string> { "KUNAI" } }, [new("x", AdviceKind.Card, "X", Card: xAttack)], false)[0];
Check(xRelicRating.Parts!.Synergy == 0, "X costs must not masquerade as cheap attacks for relic synergy");
Check(DeckMechanismProfile.Capture(context with { Deck = [xAttack with { Cost = 2 }] }).ExpensiveCards == 0, "X costs are not fixed expensive costs");
Check(Mechanic(context with { StartingStars = 3 }, plain with { StarsX = true }) > Mechanic(context, plain with { StarsX = true }), "X Stars recognize available support without inventing a fixed price");
Console.WriteLine($"RUN_ADVICE_CHECKS_OK checks={checks}");
