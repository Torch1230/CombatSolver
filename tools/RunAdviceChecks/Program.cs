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
Console.WriteLine($"RUN_ADVICE_CHECKS_OK checks={checks}");
