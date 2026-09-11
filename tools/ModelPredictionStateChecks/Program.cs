using System.Globalization;
using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

int checks = 0;
if (args.Contains("--cards"))
{
    CardReferenceChecks.Run();
    return;
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}
static (StateFingerprint Fingerprint, string Text) Predicted(CombatPredictionSimulator simulator, SimulatedCombatState combat)
{
    StateFingerprintBuilder fingerprint = new();
    fingerprint.Add("existing-state");
    StringBuilder text = new("existing-state");
    ModelPredictionStateMirrors.AppendPredicted(ref fingerprint, text, simulator, combat);
    return (fingerprint.Finish(), text.ToString());
}
static string Live(SimulatedCombatState combat)
{
    StringBuilder text = new("existing-state");
    ModelPredictionStateMirrors.AppendLiveContinuation(text, combat);
    return text.ToString();
}

Player owner = new(17);
SimulatedCombatState empty = new([owner], []);
CombatPredictionSimulator root = new();
if (args.Contains("--empty"))
{
    StateFingerprintBuilder baseline = new();
    baseline.Add("existing-state");
    Check(Predicted(root, empty) == (baseline.Finish(), "existing-state"), "Empty registry changed predicted state.");
    Check(Live(empty) == "existing-state", "Empty registry changed live continuation.");
    Check(!ModelPredictionStateMirrors.HasAny, "Empty registry reports adapters.");
    Console.WriteLine($"MODEL_STATE_EMPTY_OK checks={checks}");
    return;
}

static void WriteValues(int count, bool enabled, IReadOnlyList<int> values, string? label,
    ref ModelPredictionStateWriter writer)
{
    writer.Add("count", (long)count);
    writer.Add("enabled", enabled);
    writer.Add("length", (long)values.Count);
    for (int index = 0; index < values.Count; index++) writer.Add("value", (long)values[index]);
    writer.Add("label", label);
}
static void WriteLiveRelic(TestRelic relic, ref ModelPredictionStateWriter writer)
    => WriteValues(relic.Count, relic.Enabled, relic.Values, relic.Label, ref writer);
static void WriteState(CounterState state, ref ModelPredictionStateWriter writer)
    => WriteValues(state.Count, state.Enabled, state.Values, state.Label, ref writer);
static CounterState Capture(TestRelic relic)
    => new() { Count = relic.Count, Enabled = relic.Enabled, Values = [.. relic.Values], Label = relic.Label };

ModelPredictionStateMirrors.RegisterRelic<TestRelic, CounterState>("counter-v1",
    (_, relic) => Capture(relic), WriteLiveRelic, WriteState);
Throws<ArgumentException>(() => ModelPredictionStateMirrors.RegisterRelic<TestRelic, CounterState>(
    "duplicate", (_, relic) => Capture(relic), WriteLiveRelic, WriteState), "Duplicate registration accepted.");
ModelPredictionStateMirrors.RegisterModifier<TestModifier, CounterState>("modifier-v1",
    (_, modifier) => new() { Count = modifier.Count },
    (TestModifier modifier, ref ModelPredictionStateWriter writer) => writer.Add("count", (long)modifier.Count),
    (CounterState state, ref ModelPredictionStateWriter writer) => writer.Add("count", (long)state.Count));
ModelPredictionStateMirrors.RegisterRelic<ReferenceRelic, ReferenceState>("reference-v1",
    (_, relic) => new(relic.Reference),
    (ReferenceRelic relic, ref ModelPredictionStateWriter writer) => writer.Add("id", relic.Reference.Id),
    (ReferenceState state, ref ModelPredictionStateWriter writer) => writer.Add("id", state.Reference.Id));
ModelPredictionStateMirrors.RegisterRelic<BadForkRelic, BadForkState>("bad-fork",
    (_, relic) => new(relic.ReturnWrongType),
    (BadForkRelic _, ref ModelPredictionStateWriter _) => { },
    (BadForkState _, ref ModelPredictionStateWriter _) => { });
ModelPredictionStateMirrors.RegisterRelic<NullRelic, CounterState>("null",
    (_, _) => null!, (NullRelic _, ref ModelPredictionStateWriter _) => { }, WriteState);
ModelPredictionStateMirrors.RegisterRelic<SlicedForkRelic, BaseForkState>("runtime-type",
    (_, _) => new DerivedForkState(),
    (SlicedForkRelic _, ref ModelPredictionStateWriter writer) => writer.Add("value", 2L),
    (BaseForkState state, ref ModelPredictionStateWriter writer) => writer.Add("value", state.Value));

if (args.Contains("--fork-type"))
{
    SlicedForkRelic sliced = new();
    ModelPredictionStateMirrors.CaptureRootState(root, sliced, sliced);
    Throws<InvalidOperationException>(() => root.StateStore.Fork(new()), "Fork silently sliced a derived runtime state into its base type.");
    Console.WriteLine("MODEL_STATE_FORK_TYPE_OK");
    return;
}

TestRelic liveA = new() { Count = 2, Enabled = true, Values = [4, 9], Label = "a;|:=\\\n\ud800" };
TestRelic liveB = new() { Count = 0 };
TestRelic cloneA = new(), cloneB = new();
TestModifier liveModifier = new() { Count = 7 }, cloneModifier = new();
Player liveOwner = new(17);
liveOwner.Relics.AddRange([liveA, liveB]);
owner.Relics.AddRange([cloneA, cloneB]);
SimulatedCombatState live = new([liveOwner], [liveModifier]);
SimulatedCombatState predicted = new([owner], [cloneModifier]);
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.Get<CounterState>(root, cloneA), "Uncaptured state read succeeded.");
ModelPredictionStateMirrors.CaptureRootState(root, cloneA, liveA);
ModelPredictionStateMirrors.CaptureRootState(root, cloneB, liveB);
ModelPredictionStateMirrors.CaptureRootState(root, cloneModifier, liveModifier);
if (args.Contains("--allocation"))
{
    static StateFingerprint HashOnly(CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        StateFingerprintBuilder fingerprint = new();
        ModelPredictionStateMirrors.AppendPredicted(ref fingerprint, null, simulator, combat);
        return fingerprint.Finish();
    }
    StateFingerprint expected = HashOnly(root, predicted);
    for (int index = 0; index < 1000; index++) _ = HashOnly(root, predicted) == expected;
    long before = GC.GetAllocatedBytesForCurrentThread();
    bool equal = true;
    for (int index = 0; index < 1000; index++) equal &= HashOnly(root, predicted) == expected;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(equal, "Hash-only observation changed state.");
    Check(allocated == 0, $"Hash-only adapter traversal allocated {allocated} bytes in 1000 calls.");
    Console.WriteLine($"MODEL_STATE_ALLOCATION_OK calls=1000 allocated_bytes={allocated}");
    return;
}
Check(Predicted(root, predicted).Text == Live(live), "Root live/predicted state differs.");
var initial = Predicted(root, predicted);
Check(initial.Text.Split(';').Length == 4, "Adapter text broke continuation field boundaries.");
Check(Predicted(root, predicted) == initial, "Repeated observation mutated state.");
SimulatedCombatState noMatchingModels = new([new Player(17)], []);
StateFingerprintBuilder unmatchedBaseline = new();
unmatchedBaseline.Add("existing-state");
Check(Predicted(root, noMatchingModels) == (unmatchedBaseline.Finish(), "existing-state"), "Unused registrations changed state.");
liveA.Count = 99;
liveA.Values[0] = 88;
Check(Predicted(root, predicted) == initial, "Live mutation leaked into frozen state.");
Check(Live(live) != initial.Text, "Continuation missed live-only changes.");
liveA.Count = 2;
liveA.Values[0] = 4;

CounterState parentA = ModelPredictionStateMirrors.Get<CounterState>(root, cloneA);
CounterState parentB = ModelPredictionStateMirrors.Get<CounterState>(root, cloneB);
parentA.Count = 0;
parentB.Count = 2;
Check(Predicted(root, predicted).Fingerprint != initial.Fingerprint, "Same-type swapped counters collided.");
parentA.Count = 2;
parentB.Count = 0;
Check(Predicted(root, predicted) == initial, "Restored state did not restore identity.");
parentB.Count = 1;
Check(Predicted(root, predicted).Fingerprint != initial.Fingerprint, "Zero-valued instance was omitted.");
parentB.Count = 0;
parentA.Values.Reverse();
Check(Predicted(root, predicted).Fingerprint != initial.Fingerprint, "Ordered collection was treated as a set.");
parentA.Values.Reverse();
parentB.Label = "";
Check(Predicted(root, predicted).Fingerprint != initial.Fingerprint
    && Predicted(root, predicted).Text != initial.Text, "Null and empty string were conflated.");
parentB.Label = null;

CombatPredictionSimulator child = new(root.StateStore.Fork(new()));
Check(Predicted(child, predicted) == initial, "Fork changed state identity.");
CounterState childA = ModelPredictionStateMirrors.Get<CounterState>(child, cloneA);
childA.Count++;
childA.Values[0]++;
ModelPredictionStateMirrors.Get<CounterState>(child, cloneModifier).Count++;
Check(Predicted(root, predicted) == initial, "Child mutation leaked to parent.");
Check(Predicted(child, predicted).Fingerprint != initial.Fingerprint, "Child changes absent from fingerprint.");
liveA.Count++;
liveA.Values[0]++;
liveModifier.Count++;
Check(Predicted(child, predicted).Text == Live(live), "Independent live/branch updates differ.");
CombatPredictionSimulator restored = new();
ModelPredictionStateMirrors.CaptureRootState(restored, cloneA, liveA);
ModelPredictionStateMirrors.CaptureRootState(restored, cloneB, liveB);
ModelPredictionStateMirrors.CaptureRootState(restored, cloneModifier, liveModifier);
Check(Predicted(restored, predicted) == Predicted(child, predicted), "Recapture did not reproduce continued state.");

parentA.Pending = true;
Throws<InvalidOperationException>(() => root.StateStore.Fork(new()), "Pending transaction was forked.");
parentA.Pending = false;
ReferenceRelic reference = new(new("card-1"));
CombatPredictionSimulator references = new();
ModelPredictionStateMirrors.CaptureRootState(references, reference, reference);
Throws<InvalidOperationException>(() => references.StateStore.Fork(new()), "Missing reference mapping was ignored.");
PredictionForkContext context = new();
Identity forkIdentity = new("card-1");
context.Register(reference.Reference, forkIdentity);
CombatPredictionSimulator remapped = new(references.StateStore.Fork(context));
Check(ReferenceEquals(ModelPredictionStateMirrors.Get<ReferenceState>(remapped, reference).Reference, forkIdentity), "Reference did not remap.");

foreach (bool wrongType in new[] { false, true })
{
    CombatPredictionSimulator bad = new();
    BadForkRelic relic = new(wrongType);
    ModelPredictionStateMirrors.CaptureRootState(bad, relic, relic);
    Throws<InvalidOperationException>(() => bad.StateStore.Fork(new()), "Invalid state fork was accepted.");
}
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.CaptureRootState(root, cloneA, liveA), "Duplicate capture accepted.");
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.CaptureRootState(root, cloneA, liveModifier), "Mismatched capture accepted.");
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.CaptureRootState(root, new NullRelic(), new NullRelic()), "Null capture accepted.");
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.RegisterRelic<TestRelic, CounterState>(
    "late", (_, relic) => Capture(relic), WriteLiveRelic, WriteState), "Late registration accepted.");
DerivedRelic derived = new();
ModelPredictionStateMirrors.CaptureRootState(root, derived, derived);
Throws<InvalidOperationException>(() => ModelPredictionStateMirrors.Get<CounterState>(root, derived), "Base adapter implicitly matched derived type.");

var beforeCulture = Predicted(root, predicted);
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
Check(Predicted(root, predicted) == beforeCulture, "Culture changed state identity.");
Parallel.For(0, 32, _ =>
{
    if (Predicted(root, predicted) != beforeCulture)
        throw new InvalidOperationException("Concurrent read changed state identity.");
});
checks++;
static (StateFingerprint, string) TypedValue(bool boolean)
{
    StringBuilder text = new();
    ModelPredictionStateWriter writer = new(new StateFingerprintBuilder(), text);
    if (boolean) writer.Add("value", true);
    else writer.Add("value", 1L);
    return (writer.Fingerprint.Finish(), text.ToString());
}
Check(TypedValue(true) != TypedValue(false), "Typed field boundaries were lost.");
CombatPredictionSimulator slicedSimulator = new();
SlicedForkRelic slicedRelic = new();
ModelPredictionStateMirrors.CaptureRootState(slicedSimulator, slicedRelic, slicedRelic);
Throws<InvalidOperationException>(() => slicedSimulator.StateStore.Fork(new()), "Fork changed the state's concrete runtime type.");
Console.WriteLine($"MODEL_STATE_CONTRACTS_OK checks={checks}");

internal class TestRelic : RelicModel
{
    public int Count;
    public bool Enabled;
    public List<int> Values = [];
    public string? Label;
}
internal sealed class DerivedRelic : TestRelic;
internal sealed class TestModifier : ModifierModel { public int Count; }
internal sealed class NullRelic : RelicModel;
internal sealed class SlicedForkRelic : RelicModel;
internal class BaseForkState : IPredictionStateForkable
{
    public virtual long Value => 1;
    public object Fork(PredictionForkContext context) => new BaseForkState();
}
internal sealed class DerivedForkState : BaseForkState { public override long Value => 2; }
internal sealed class CounterState : IPredictionStateForkable, IPredictionForkBoundary
{
    public int Count;
    public bool Enabled;
    public List<int> Values = [];
    public string? Label;
    public bool Pending;
    public void AssertForkable()
    {
        if (Pending) throw new InvalidOperationException("Pending transaction.");
    }
    public object Fork(PredictionForkContext context)
        => new CounterState { Count = Count, Enabled = Enabled, Values = [.. Values], Label = Label };
}
internal sealed record Identity(string Id);
internal sealed class ReferenceRelic(Identity reference) : RelicModel { public Identity Reference => reference; }
internal sealed class ReferenceState(Identity reference) : IPredictionStateForkable
{
    public Identity Reference => reference;
    public object Fork(PredictionForkContext context) => new ReferenceState(context.RequireRemap(reference));
}
internal sealed class BadForkRelic(bool wrongType) : RelicModel { public bool ReturnWrongType => wrongType; }
internal sealed class BadForkState(bool wrongType) : IPredictionStateForkable
{
    public object Fork(PredictionForkContext context) => wrongType ? new object() : this;
}
