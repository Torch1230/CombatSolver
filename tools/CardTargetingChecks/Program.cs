using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

int checks = 0;
foreach ((CardModel model, Type power) in new (CardModel, Type)[]
         { (new SovereignBlade(), typeof(SeekingEdgePower)), (new Shiv(), typeof(FanOfKnivesPower)) })
{
    PredictedCard card = new(model);
    SimulatedCombatState branch = new();
    CombatPredictionSimulator simulator = new(branch);
    model.Owner.Creature.LivePowers.Add(power);
    Require(simulator.GetTargetType(card) == TargetType.AnyEnemy,
        $"{model.GetType().Name}: absent branch power inherited live all-enemy targeting");

    int nativeReads = model.NativeTargetReads;
    model.RejectNativeRead = true;
    foreach (int amount in new[] { 0, 1, 2 })
    foreach (bool livePresent in new[] { false, true })
    {
        branch.Powers[(model.Owner.Creature, power)] = amount;
        if (livePresent) model.Owner.Creature.LivePowers.Add(power);
        else model.Owner.Creature.LivePowers.Remove(power);
        Require(simulator.GetTargetType(card) == (amount > 0 ? TargetType.AllEnemies : TargetType.AnyEnemy),
            "Branch target type depends on live power");
        Require(model.NativeTargetReads == nativeReads, "Native getter was evaluated");
    }
    branch.Powers.Clear();
    branch.Powers[(new Creature(), power)] = 1;
    Require(simulator.GetTargetType(card) == TargetType.AnyEnemy, "Another owner's power changed targeting");
    branch.Powers[(model.Owner.Creature, power)] = 1;
    SimulatedCombatState sibling = new();
    CombatPredictionSimulator siblingSimulator = new(sibling);
    Require(simulator.GetTargetType(card) == TargetType.AllEnemies
        && siblingSimulator.GetTargetType(card) == TargetType.AnyEnemy, "Independent branches share target state");
    branch.Powers.Remove((model.Owner.Creature, power));
    Require(simulator.GetTargetType(card) == TargetType.AnyEnemy, "Removed branch power retained all-enemy targeting");

    model.RejectNativeRead = false;
    CombatPredictionSimulator fallback = new(new object());
    foreach (bool livePresent in new[] { false, true })
    {
        if (livePresent) model.Owner.Creature.LivePowers.Add(power);
        else model.Owner.Creature.LivePowers.Remove(power);
        Require(fallback.GetTargetType(card) == (livePresent ? TargetType.AllEnemies : TargetType.AnyEnemy),
            "Non-shadow fallback changed");
    }
}
CardModel ordinary = new();
Require(new CombatPredictionSimulator(new SimulatedCombatState()).GetTargetType(new(ordinary)) == TargetType.Self
    && ordinary.NativeTargetReads == 1, "Unrelated card fallback changed");
Console.WriteLine($"Passed {checks} card targeting checks using the production selector.");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
