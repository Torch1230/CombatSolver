using CombatSolver.Engine.InCombat.Simulation.Compact;

PowerChecks.Run();
EffectProgramChecks.Run();
CardLifecycleChecks.Run();
ConditionalPowerChecks.Run();
PoisonDiscardChecks.Run();
PowerExpressionChecks.Run();
DeferredPowerChecks.Run();
TemporaryStrengthChecks.Run();
GeneratedCardChecks.Run();
RandomCostChecks.Run();
ArtifactChecks.Run();
HandEndChecks.Run();
MonsterCommandChecks.Run();
MonsterAiChecks.Run();
RoundChecks.Run();
NeurosurgeChecks.Run();
OneShotEnchantmentChecks.Run();
AttackStartChecks.Run();

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
// Resume after Begin must retain the exact target, and the final native pile gate leaves
// the killing card in Play until teardown. Terminal commitment is a separate safe point.
var attacks = new ResumableDiscardProgram(
    [new(0, new([new(CardInstructionKind.AttackTarget, 0)])), new(1, new([new(CardInstructionKind.AttackTarget, 6)])),
     new(1, new([new(CardInstructionKind.AttackTarget, 9)])), new(1, new([new(CardInstructionKind.AttackTarget, 6)])),
     new(1, new([new(CardInstructionKind.GainBlock, 5)]))],
    [new[] { 0, 1, 2, 3, 4 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()],
    5, 3, 0, creatures: [new(50, 60, 3), new(6, 30, 6), new(4, 30, 2)]);
var attackRoot = attacks.Freeze();
foreach (int target in new[] { -1, 0, 3, int.MaxValue })
{
    Expect<InvalidOperationException>(() => attacks.Begin(1, target));
    if (!attacks.State.Freeze().ContentEquals(attackRoot.Open().State.Freeze()))
        throw new InvalidOperationException("Invalid attack changed its root.");
}
var cancelled = attacks.State.Mark();
attacks.Begin(1, 1);
Expect<OperationCanceledException>(() => attacks.Run(new CancellationToken(true)));
attacks.State.Rollback(cancelled);
attacks.Begin(0, 2); attacks.Run();
var zeroHit = Enumerable.Range(0, attacks.EventCount).Select(attacks.EventAt)
    .Single(e => e.Kind == ResumableDiscardProgram.EventKind.Damage);
if (zeroHit.Target != 2 || zeroHit.Value != 0 || zeroHit.Flags != 4 || attacks.Energy != 5)
    throw new InvalidOperationException("Zero powered hit lost target, fully blocked flag or zero cost.");
attacks.Begin(1, 1);
var suspendedAttack = attacks.Freeze();
Parallel.For(0, 8, _ =>
{
    var lane = suspendedAttack.Open(); lane.Run();
    if (lane.Creature(1) != new CreatureVitals(6, 30, 0) || lane.Creature(2) != new CreatureVitals(4, 30, 2))
        throw new InvalidOperationException("Frozen attack resumed against the wrong target.");
    lane.Begin(2, 1); lane.Run();
    if (lane.CreaturePresent(1) || !lane.CreatureDeathCompleted(1) || lane.Ending || lane.CheckWinCondition())
        throw new InvalidOperationException("Partial death ended combat or missed lifecycle.");
    var survivor = lane.Freeze();
    var mark = lane.State.Mark();
    lane.Begin(3, 2); lane.Run();
    if (!lane.Ending || lane.Terminal || lane.Count(ResumableDiscardProgram.Pile.Play) != 1)
        throw new InvalidOperationException("Final hit bypassed the native ending/pile gate.");
    Expect<InvalidOperationException>(() => lane.Begin(4));
    if (!lane.CheckWinCondition() || !lane.Terminal)
        throw new InvalidOperationException("Safe point failed to commit victory.");
    var victory = lane.Freeze();
    lane.State.Rollback(mark);
    if (!lane.State.Freeze().ContentEquals(survivor.Open().State.Freeze()))
        throw new InvalidOperationException("Final kill failed to roll back.");
    victory.RestoreInto(lane);
    if (!lane.Terminal || lane.CreaturePresent(2)) throw new InvalidOperationException("Victory restore lost terminal/death state.");
    attackRoot.RestoreInto(lane);
    if (lane.Terminal || lane.Ending || !lane.CreaturePresent(1)) throw new InvalidOperationException("Root restore retained death state.");
});
Console.WriteLine($"COMPACT_CREATURE_CHECKS_OK damage_vectors={damageCases.Length} healing_caps=true journal=true frozen_workers=8 attack_targets=true ending_pile_gate=true attack_resume_rollback=true");

static void Expect<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
