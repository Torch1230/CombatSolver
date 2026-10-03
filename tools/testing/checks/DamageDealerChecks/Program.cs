using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;

int checks = 0;
foreach (int overload in Enumerable.Range(0, 5))
foreach (bool branchDead in new[] { true, false })
foreach (bool liveDead in new[] { false, true })
{
    Creature dealer = new() { LiveDead = liveDead };
    Creature first = new(), second = new();
    CombatPredictionSimulator simulator = new();
    simulator.State.Creatures[dealer] = new() { IsDead = branchDead };
    var result = Invoke(simulator, overload, dealer, first, second);
    int targetCount = overload < 2 ? 1 : 2;
    Require(result.Count == (branchDead ? 0 : targetCount),
        $"Overload {overload}: branchDead={branchDead}, liveDead={liveDead} used wrong dealer liveness");
    Require(simulator.ProcessCalls == (branchDead ? 0 : 1), "Dead dealer entered post-damage hooks");
    Require(simulator.Targets.Count == (branchDead ? 0 : targetCount), "Dead dealer entered damage resolution");
    Require(dealer.LiveReads == 0, "Damage read live dealer state");
}
foreach (int overload in Enumerable.Range(0, 5))
{
    Creature dealer = new() { RejectLiveRead = true };
    Creature target = new();
    CombatPredictionSimulator parent = new(), sibling = new();
    parent.State.Creatures[dealer] = new() { IsDead = true };
    sibling.State.Creatures[dealer] = new() { IsDead = false };
    Require(Invoke(parent, overload, dealer, target, target).Count == 0, "Dead parent dealt damage");
    Require(Invoke(sibling, overload, dealer, target, target).Count > 0, "Live sibling suppressed damage");
    parent.State.Creatures[dealer].IsDead = false;
    Require(Invoke(parent, overload, dealer, target, target).Count > 0, "Revived dealer remained blocked");
    Require(Invoke(new(), overload, null, target, target).Count > 0, "Null source damage was blocked");
}
CombatPredictionSimulator empty = new();
Require(empty.Damage(Array.Empty<Creature>(), 2, ValueProp.Unpowered, null).Count == 0
    && empty.ProcessCalls == 0, "Empty target list entered hooks");
Console.WriteLine($"Passed {checks} production damage entry checks.");

static IReadOnlyList<DamageResult> Invoke(CombatPredictionSimulator sim, int overload,
    Creature? dealer, Creature first, Creature second) => overload switch
{
    0 => sim.Damage(first, 2, ValueProp.Unpowered, dealer),
    1 => sim.Damage(first, new DamageVar(), dealer),
    2 => sim.Damage(new[] { first, second }, 2, ValueProp.Unpowered, dealer),
    3 => sim.Damage(new[] { first, second }, new DamageVar(), dealer),
    4 => sim.Damage(new[] { first, second }, 2, ValueProp.Unpowered, dealer, new PredictedCard(), new()),
    _ => throw new ArgumentOutOfRangeException(nameof(overload)),
};
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
