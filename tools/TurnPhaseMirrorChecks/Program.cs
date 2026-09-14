using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); } catch (T) { checks++; return; }
    throw new Exception(message);
}
bool Run(CombatPredictionSimulator simulator, CombatSide side = CombatSide.Player, params Creature[] participants)
    => HookMirrors.AfterSideTurnEndLate(simulator, side, participants);

if (args.Contains("--allocation"))
{
    Creature deadOwner = new() { IsAlive = false };
    CombatPredictionSimulator single = new()
    {
        Listeners = [new DisintegrationPower { Owner = deadOwner, Amount = 4 }]
    };
    Creature[] participants = [deadOwner];
    for (int i = 0; i < 20; i++) Run(single, CombatSide.Player, participants);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) Run(single, CombatSide.Player, participants);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine($"TURN_PHASE_SINGLE_ALLOCATED bytes={allocated} calls=1000");
    // The contract's interface enumerator also allocates; allow it plus the context, not a list/array.
    Check(allocated <= 100000, "Single-listener dispatch allocated a receiver collection.");
    return;
}

if (args.Contains("--seal"))
{
    AfterSideTurnEndLateMirrors.Seal();
    Throws<InvalidOperationException>(() => AfterSideTurnEndLateMirrors.Register<TestRelic>((_, _) => { }),
        "Root seal allowed registration before first dispatch.");
    Console.WriteLine($"TURN_PHASE_SEAL_OK checks={checks}");
    return;
}

Throws<ArgumentNullException>(() => AfterSideTurnEndLateMirrors.Register<TestRelic>(null!), "Null handler accepted.");
Throws<ArgumentException>(() => AfterSideTurnEndLateMirrors.Register<AbstractRelic>((_, _) => { }), "Abstract receiver accepted.");
Throws<InvalidOperationException>(() => AfterSideTurnEndLateMirrors.Register<RelicModel>((_, _) => { }), "Non-override accepted.");
AfterSideTurnEndLateMirrors.Register<TestRelic>((relic, context) =>
{
    context.Simulator.Events.Add(relic.Label);
    relic.Callback?.Invoke(context);
});
AfterSideTurnEndLateMirrors.Register<TestModifier>((_, context) =>
    context.Simulator.Events.Add($"modifier:{context.Side}:{context.Participants.Count}"));
AfterSideTurnEndLateMirrors.Register<TestCard>((card, context) => context.Simulator.Events.Add(card.Label));
Throws<ArgumentException>(() => AfterSideTurnEndLateMirrors.Register<TestRelic>((_, _) => { }), "Duplicate accepted.");

var descriptor = ((IMethodMirrorRegistryDescriptorProvider)typeof(AfterSideTurnEndLateMirrors)
    .GetField("Registry", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).DescribeMirrorSupport();
Check(descriptor.BaseMethod.Name == nameof(AbstractModel.AfterSideTurnEndLate), "Wrong method metadata.");
Check(descriptor.Registrations.Count == 4 && descriptor.Registrations.All(r => r.Kind == MethodMirrorRegistrationKind.Handled),
    "Coverage descriptor lost a handler.");

CombatPredictionSimulator simulator = new() { Listeners = [new TestRelic("first"), new TestModifier(), new TestRelic("last")] };
Check(Run(simulator, CombatSide.Enemy), "Empty participants should still dispatch.");
Check(simulator.Events.SequenceEqual(["first", "modifier:Enemy:0", "last"]), "Order or side changed.");
Throws<InvalidOperationException>(() => AfterSideTurnEndLateMirrors.Register<TestCard>((_, _) => { }), "Late registration accepted.");

simulator = new() { Listeners = [new AbstractModel()] };
Check(Run(simulator) && simulator.Risks == 0, "Base no-op recorded risk.");
simulator = new() { Listeners = [new UnknownRelic(), new TestRelic("must not run")] };
Throws<NotSupportedException>(() => Run(simulator), "Unknown override silently skipped.");
Check(simulator.Risks == 1 && simulator.Events.Count == 0, "Unknown override continued dispatch.");
simulator = new() { Listeners = [new DerivedRelic()] };
Throws<NotSupportedException>(() => Run(simulator), "Registration incorrectly inherited by derived model.");

simulator = new() { HasPendingChoice = true, Listeners = [new TestRelic("must not run")] };
Check(!Run(simulator) && simulator.Events.Count == 0, "Pending entry executed a callback.");
simulator = new() { Listeners = [new TestRelic("pause", c => c.Simulator.HasPendingChoice = true), new TestRelic("later")] };
Check(!Run(simulator) && simulator.Events.SequenceEqual(["pause"]), "Pending choice did not suspend later listeners.");
simulator = new() { Listeners = [new TestRelic("throw", _ => throw new ArithmeticException()), new TestRelic("later")] };
Throws<ArithmeticException>(() => Run(simulator), "Handler failure was swallowed.");
Check(simulator.Events.SequenceEqual(["throw"]), "Failure continued dispatch.");

simulator = new() { Listeners = [new TestRelic("mutate", c => c.Simulator.Listeners.Clear()), new TestRelic("captured")] };
Check(Run(simulator) && simulator.Events.SequenceEqual(["mutate", "captured"]), "Listener membership was not frozen.");
simulator = new() { Listeners = [new TestRelic("terminal", c => c.Simulator.IsOverOrEnding = true), new TestRelic("captured")] };
Check(Run(simulator) && simulator.Events.SequenceEqual(["terminal", "captured"]), "Inserted a mid-phase terminal exit.");
simulator = new() { IsOverOrEnding = true, Listeners = [new TestRelic("must not run")] };
Check(Run(simulator) && simulator.Events.Count == 0, "Phase entry ignored terminal state.");

TestCard oldCard = new() { Label = "old" }, newCard = new() { Label = "new" };
PredictedCard predicted = new() { Preview = oldCard };
simulator = new() { Listeners = [new TestRelic("cow", _ => predicted.Preview = newCard), oldCard] };
simulator.State.Player.Cards[oldCard] = predicted;
Check(Run(simulator) && simulator.Events.SequenceEqual(["cow", "new"]), "Card COW passed a stale receiver.");

Creature owner = new(), other = new();
DisintegrationPower power = new() { Owner = owner, Amount = 4 };
simulator = new() { Listeners = [power] };
Check(Run(simulator, CombatSide.Player, other) && simulator.DamageCalls == 0, "Damaged a nonparticipant.");
Check(Run(simulator, CombatSide.Player, owner) && simulator.DamageCalls == 1 && simulator.DamageTotal == 4,
    "Disintegration must execute exactly once.");
power.Amount = 7;
Check(Run(simulator, CombatSide.Enemy, owner) && simulator.DamageCalls == 2 && simulator.DamageTotal == 11,
    "Enemy phase did not consume current branch amount.");
owner.IsAlive = false;
Check(Run(simulator, CombatSide.Enemy, owner) && simulator.DamageCalls == 2, "Dead owner damaged.");
Console.WriteLine($"TURN_PHASE_MIRRORS_OK checks={checks}");

class TestRelic(string label = "base", Action<AfterSideTurnEndLateMirrorContext>? callback = null) : RelicModel
{
    public string Label = label;
    public Action<AfterSideTurnEndLateMirrorContext>? Callback = callback;
    public override Task AfterSideTurnEndLate(PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => throw new Exception("Native hook invoked.");
}
class DerivedRelic : TestRelic;
abstract class AbstractRelic : TestRelic;
class UnknownRelic : TestRelic;
class TestModifier : ModifierModel
{
    public override Task AfterSideTurnEndLate(PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => throw new Exception("Native hook invoked.");
}
class TestCard : CardModel
{
    public string Label = "";
    public override Task AfterSideTurnEndLate(PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => throw new Exception("Native hook invoked.");
}
