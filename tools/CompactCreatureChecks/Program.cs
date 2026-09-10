using CombatSolver.Engine.InCombat.Simulation.Compact;

(int Hp, int Block, decimal Amount, bool Unblockable, int ExpectedHp, int ExpectedBlock, decimal Blocked, int Lost, bool Killed, int Overkill)[] damageCases =
{
    (10, 5, 3.75m, false, 10, 2, 3.75m, 0, false, 0),
    (10, 5, 5m, false, 10, 0, 5m, 0, false, 0),
    (10, 5, 8.25m, false, 7, 0, 5m, 3, false, 0),
    (10, 5, 6.5m, true, 4, 5, 0m, 6, false, 0),
    (10, 0, 9.99m, false, 1, 0, 0m, 9, false, 0),
    (10, 0, 10.01m, false, 0, 0, 0m, 10, true, 0),
    (10, 0, 12.75m, false, 0, 0, 0m, 10, true, 2),
    (0, 0, 12.75m, false, 0, 0, 0m, 0, false, 0),
    (999_999_999, 999_999_999, decimal.MaxValue, true, 0, 999_999_999, 0m, 999_999_999, true, 0),
};
foreach (var test in damageCases)
{
    var value = new CreatureVitals(test.Hp, 999_999_999, test.Block);
    decimal blocked = value.DamageBlock(test.Amount, test.Unblockable);
    var loss = value.LoseHp(Math.Max(0m, test.Amount - blocked));
    if (value.CurrentHp != test.ExpectedHp || value.Block != test.ExpectedBlock || blocked != test.Blocked
        || loss != new HpLossValues(test.Lost, test.Killed, test.Overkill))
        throw new InvalidOperationException($"Damage boundary differs: {test} / {value} / {loss}");
}
var healing = new CreatureVitals(10, 20, 999_999_998);
healing.Heal(2.99m);
if (healing.CurrentHp != 12) throw new InvalidOperationException("Fractional healing changed.");
healing.Heal(100m);
healing.GainBlock(100m);
if (healing.CurrentHp != 20 || healing.Block != 999_999_999) throw new InvalidOperationException("Scalar caps changed.");
healing.SetMaxHp(0);
if (healing.CurrentHp != 1 || healing.MaxHp != 1) throw new InvalidOperationException("Max HP clamp changed.");
healing.SetMaxHp(int.MaxValue);
if (healing.MaxHp != 999_999_999 || healing.CurrentHp != 1) throw new InvalidOperationException("Max HP growth healed.");
Expect<ArgumentException>(() => healing.Heal(-1));
Expect<ArgumentException>(() => healing.GainBlock(-1));

// Deliberately cross the 64-slot page boundary and retain states on both sides of removal.
var state = new ReversibleValueState(62);
var first = CreatureValueSlots.Allocate(state, new(23, 30, 7), true);
var second = CreatureValueSlots.Allocate(state, new(61, 70, 3), true);
var root = state.Freeze();
var outer = state.Mark();
var damaged = first.Read(state);
damaged.DamageBlock(9, false); damaged.LoseHp(2);
first.Write(state, damaged);
var nonlethal = state.Freeze();
var inner = state.Mark();
damaged.LoseHp(100); first.Write(state, damaged);
if (!first.IsPresent(state)) throw new InvalidOperationException("Zero HP implicitly removed roster membership.");
first.SetPresent(state, false);
var dead = state.Freeze();
state.Rollback(inner);
if (!state.Freeze().ContentEquals(nonlethal)) throw new InvalidOperationException("Death values failed to roll back.");
state.Rollback(outer);
if (!state.Freeze().ContentEquals(root)) throw new InvalidOperationException("Damage values failed to roll back.");
Parallel.For(0, 8, lane =>
{
    var worker = root.CreateWorkspace();
    foreach (var retained in new[] { dead, root, nonlethal, dead, root })
    {
        worker.Restore(retained);
        if (!worker.Freeze().ContentEquals(retained)) throw new InvalidOperationException("Worker restored stale creature slots.");
        var mark = worker.Mark();
        var values = second.Read(worker);
        values.LoseHp(lane + 1);
        second.Write(worker, values);
        first.SetPresent(worker, lane % 2 == 0);
        worker.Rollback(mark);
        if (!worker.Freeze().ContentEquals(retained)) throw new InvalidOperationException("Sibling rollback changed retained creature values.");
    }
});
if (!state.Freeze().ContentEquals(root) || dead[first.Offset] != 0 || dead[first.Offset + 3] != 0)
    throw new InvalidOperationException("Worker changed a frozen creature state.");
Console.WriteLine($"COMPACT_CREATURE_CHECKS_OK damage_vectors={damageCases.Length} healing_caps=true journal=true frozen_workers=8");

static void Expect<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
