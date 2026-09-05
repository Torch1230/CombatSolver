using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;

#if CHECK_ENTRY_COUNTS
if (args is ["--entry-count-only"])
{
    StoreChecks.CheckEntryCounts();
    Console.WriteLine("PASS: type-entry counts after capture, Peek, removal and Fork");
    return;
}
#endif
StoreChecks.Run();
int iterations = args.Length == 0 ? 20_000 : int.Parse(args[0]);
ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
foreach ((string name, PredictionStateStore store) in StoreChecks.BenchmarkStores())
{
    // Reuse and warm the test context to isolate store/state allocation from remap-table growth.
    PredictionForkContext context = new();
    for (int index = 0; index < 100; index++)
    {
        context.Clear();
        GC.KeepAlive(store.Fork(context));
    }
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    for (int index = 0; index < iterations; index++)
    {
        context.Clear();
        GC.KeepAlive(store.Fork(context));
    }
    long elapsedTicks = Stopwatch.GetTimestamp() - started;
    long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario = name,
        iterations,
        allocatedBytes,
        bytesPerFork = (double)allocatedBytes / iterations,
        elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency,
    }));
}

internal static class StoreChecks
{
    public static void Run()
    {
        CheckReadAndPeek();
#if CHECK_ENTRY_COUNTS
        CheckEntryCounts();
#endif
        CheckForkIsolationAndOrder();
        CheckInterleavedForkOrder();
        CheckAliasesAndRemap();
        CheckSharedStateRemap();
        CheckBoundariesAndFailure();
        Console.WriteLine("PASS: read/peek, isolation/order, alias/model remap, shared-state remap, boundaries/failures");
    }

    private static void CheckReadAndPeek()
    {
        PredictionStateStore store = new();
        AbstractModel model = new("a");
        int creates = 0;
        Counter NewCounter() => new() { Value = ++creates };
        Counter peek = store.Peek(model, NewCounter);
        Counter otherPeek = store.Peek(model, NewCounter);
        Require(!ReferenceEquals(peek, otherPeek) && creates == 2, "Peek must not register projections.");
        Require(!store.TryGetReadOnly<Counter>(model, out _) && !store.ReadEntries<Counter>().Any(), "Peek changed membership.");
        Counter readOnly = store.GetReadOnly(model, NewCounter);
        Require(ReferenceEquals(readOnly, store.Get(model, NewCounter)) && creates == 3, "Read-only capture must register once.");
        Require(ReferenceEquals(readOnly, store.Peek(model, NewCounter)), "Peek lost the owned state.");
        Require(store.Remove<Counter>(model) && !store.Remove<Counter>(model), "Remove membership changed.");
        Require(!store.ReadEntries<Counter>().Any(), "Removed type still enumerates entries.");
        Counter fresh = store.Get<Counter>(model);
        Require(!ReferenceEquals(fresh, readOnly) && fresh.Value == 0, "Remove/recreate retained old state.");
        Require(new PredictionStateStore().Get(model, 42, static value => new Counter { Value = value }).Value == 42,
            "Argument factory changed.");
        Require(new PredictionStateStore().GetReadOnly(model, static value => new Counter { Value = value.Name.Length }).Value == 1,
            "Model factory changed.");
    }

#if CHECK_ENTRY_COUNTS
    public static void CheckEntryCounts()
    {
        PredictionStateStore store = new();
        AbstractModel model = new("type-count");
        Require(!store.HasEntries<Counter>() && !store.HasEntries<OtherCounter>(), "Empty store reports an owned type.");
        _ = store.Peek(model, static () => new Counter());
        Require(!store.HasEntries<Counter>(), "Peek registered a type count.");
        _ = store.GetReadOnly(model, static () => new Counter());
        _ = store.Get<OtherCounter>(model);
        Require(store.HasEntries<Counter>() && store.HasEntries<OtherCounter>(), "Captured state is absent from type count.");
        PredictionStateStore child = store.Fork(new());
        store.Remove<Counter>(model);
        Require(!store.HasEntries<Counter>() && store.HasEntries<OtherCounter>() && child.HasEntries<Counter>(),
            "Remove changed another type or branch count.");
        PredictionStateStore removedFork = store.Fork(new());
        Require(!removedFork.HasEntries<Counter>() && removedFork.HasEntries<OtherCounter>(), "Fork restored a zero-count type.");
        store.Remove<OtherCounter>(model);
        PredictionStateStore emptyFork = store.Fork(new());
        Require(!emptyFork.HasEntries<Counter>() && !emptyFork.HasEntries<OtherCounter>(), "Empty Fork kept type entries.");
    }
#endif

    private static void CheckForkIsolationAndOrder()
    {
        AbstractModel a = new("a"), b = new("b"), c = new("c"), d = new("d");
        PredictionStateStore store = new();
        Counter borrowedBeforeFork = store.Get<Counter>(a);
        borrowedBeforeFork.Value = 1;
        store.Get<OtherCounter>(b).Value = 2;
        store.Get<Counter>(c).Value = 3;
        PredictionStateStore child = store.Fork(new());
        PredictionStateStore sibling = store.Fork(new());
        borrowedBeforeFork.Value = 100;
        child.Get<Counter>(a).Value = 10;
        Require(sibling.Get<Counter>(a).Value == 1 && store.Get<Counter>(a).Value == 100,
            "Borrowed parent reference or child write changed another branch.");
        Require(child.ReadEntries<Counter>().Select(entry => entry.Model.Name).SequenceEqual(["a", "c"]),
            "Fork changed per-type insertion order.");
        store.Remove<Counter>(a);
        store.Get<Counter>(d).Value = 4;
        Require(store.ReadEntries<Counter>().Select(entry => entry.Model.Name).SequenceEqual(["d", "c"]),
            "Dictionary free-slot enumeration order changed.");
        Require(child.ReadEntries<Counter>().Select(entry => entry.Model.Name).SequenceEqual(["a", "c"]),
            "Parent membership mutation reached child.");
        store.Remove<Counter>(c);
        store.Remove<Counter>(d);
        PredictionStateStore removedFork = store.Fork(new());
        Require(!removedFork.ReadEntries<Counter>().Any() && removedFork.ReadEntries<OtherCounter>().Count() == 1,
            "Zero-count type leaked across Fork.");
    }

    private static void CheckAliasesAndRemap()
    {
        AbstractModel original = new("original"), moved = new("moved"), final = new("final");
        AbstractModel unrelated = new("unrelated"), mappedFinal = new("mapped-final"), dependency = new("dependency");
        AbstractModel mappedDependency = new("mapped-dependency");
        PredictionStateStore store = new();
        store.Get(original, () => new LinkedState(dependency)).Value = 7;
        store.Get<Counter>(unrelated).Value = 8;
        store.RemapModel(original, moved);
        store.RemapModel(moved, final);
        Require(ReferenceEquals(store.GetReadOnly<LinkedState>(original, () => throw new Exception()),
            store.GetReadOnly<LinkedState>(final, () => throw new Exception())), "Alias chain lost owned state.");
        PredictionForkContext context = new();
        context.Register(final, mappedFinal);
        context.Register(dependency, mappedDependency);
        PredictionStateStore fork = store.Fork(context);
        LinkedState state = fork.GetReadOnly<LinkedState>(original, () => throw new Exception());
        Require(ReferenceEquals(state.Target, mappedDependency) && state.Value == 7, "State references were not remapped.");
        Require(ReferenceEquals(state, fork.GetReadOnly<LinkedState>(mappedFinal, () => throw new Exception())),
            "Forked model key and alias disagree.");
        fork.Remove<LinkedState>(moved);
        Require(!fork.TryGetReadOnly<LinkedState>(original, out _) && store.TryGetReadOnly<LinkedState>(final, out _),
            "Alias remove escaped the child.");
        PredictionStateStore aliasOnly = new();
        aliasOnly.RemapModel(original, final);
        PredictionStateStore aliasOnlyFork = aliasOnly.Fork(new());
        aliasOnlyFork.Get<Counter>(original).Value = 9;
        Require(aliasOnlyFork.Get<Counter>(final).Value == 9 && !aliasOnly.TryGetReadOnly<Counter>(final, out _),
            "Empty alias-only store lost mapping/isolation.");
        PredictionStateStore collision = new();
        collision.Get<Counter>(original);
        collision.Get<Counter>(final);
        ExpectInvalidOperation(() => collision.RemapModel(original, final), "remap collided");
    }

    private static void CheckInterleavedForkOrder()
    {
        PredictionStateStore store = new();
        List<string> order = [];
        AbstractModel first = new("first"), second = new("second"), third = new("third");
        store.Get(first, () => new OrderedState<int>(first.Name, order));
        store.Get(second, () => new OrderedState<bool>(second.Name, order));
        store.Get(third, () => new OrderedState<int>(third.Name, order));
        store.Fork(new());
        Require(order.SequenceEqual(["first", "second", "third"]), "Fork reordered interleaved state types.");
    }

    private static void CheckSharedStateRemap()
    {
        AbstractModel a = new("a"), b = new("b");
        Counter common = new() { Value = 4 };
        PredictionStateStore store = new();
        store.Get(a, () => common);
        store.Get(b, () => common);
        PredictionStateStore child = store.Fork(new());
        Require(common.Forks == 1, "Shared state was forked twice.");
        Require(child.TryGetReadOnly<Counter>(a, out Counter? first) && child.TryGetReadOnly<Counter>(b, out Counter? second)
            && ReferenceEquals(first, second) && !ReferenceEquals(first, common), "Shared identity was not remapped consistently.");
        PredictionForkContext preMapped = new();
        Counter externallyForked = new() { Value = 8 };
        preMapped.Register(common, externallyForked);
        PredictionStateStore externalChild = store.Fork(preMapped);
        Require(ReferenceEquals(externalChild.Get<Counter>(a), externallyForked) && common.Forks == 1,
            "Already-forked state was cloned again.");
    }

    private static void CheckBoundariesAndFailure()
    {
        AbstractModel model = new("a"), dependency = new("unmapped");
        PredictionStateStore store = new();
        BoundaryState borrowed = store.Get<BoundaryState>(model);
        borrowed.Pending = true;
        ExpectInvalidOperation(() => store.Fork(new()), "pending transaction");
        borrowed.Pending = false;
        PredictionStateStore child = store.Fork(new());
        Require(!child.Get<BoundaryState>(model).Pending && !ReferenceEquals(borrowed, child.Get<BoundaryState>(model)),
            "Stable boundary failed to clone.");
        PredictionStateStore strict = new();
        strict.Get(model, () => new LinkedState(dependency));
        ExpectInvalidOperation(() => strict.Fork(new()), "Required mapping");
        PredictionStateStore empty = new();
        ExpectInvalidOperation(() => empty.Get<Counter>(model, () => null!), "factory returned null");
        ExpectInvalidOperation(() => empty.GetReadOnly<Counter>(model, () => null!), "factory returned null");
        ExpectInvalidOperation(() => empty.Peek<Counter>(model, () => null!), "factory returned null");
        Require(!empty.ReadEntries<Counter>().Any(), "Failed factory inserted a state.");
    }

    public static IEnumerable<(string Name, PredictionStateStore Store)> BenchmarkStores()
    {
        yield return ("empty", new());
        PredictionStateStore one = new();
        one.Get<Counter>(new("one"));
        yield return ("one-state", one);
        PredictionStateStore sixteen = new();
        PredictionStateStore mixed = new();
        for (int index = 0; index < 16; index++)
        {
            AbstractModel model = new(index.ToString());
            sixteen.Get<Counter>(model);
            if ((index & 1) == 0)
                mixed.Get<Counter>(model);
            else
                mixed.Get<OtherCounter>(model);
        }
        yield return ("sixteen-same-type", sixteen);
        yield return ("sixteen-two-types", mixed);
        PredictionStateStore aliases = new();
        for (int index = 0; index < 16; index++)
        {
            AbstractModel original = new($"original-{index}"), replacement = new($"replacement-{index}");
            aliases.Get<Counter>(original);
            aliases.RemapModel(original, replacement);
        }
        yield return ("sixteen-remapped-models", aliases);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectInvalidOperation(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error) when (error.Message.Contains(expected, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"Expected failure containing '{expected}'.");
    }

    private sealed class Counter : IPredictionStateForkable
    {
        public int Value { get; set; }
        public int Forks { get; private set; }
        public object Fork(PredictionForkContext context)
        {
            Forks++;
            return new Counter { Value = Value };
        }
    }

    private sealed class OtherCounter : IPredictionStateForkable
    {
        public int Value { get; set; }
        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }

    private sealed class LinkedState(AbstractModel target) : IPredictionStateForkable
    {
        public AbstractModel Target { get; } = target;
        public int Value { get; set; }
        public object Fork(PredictionForkContext context) => new LinkedState(context.RequireRemap(Target)) { Value = Value };
    }

    private sealed class BoundaryState : IPredictionStateForkable, IPredictionForkBoundary
    {
        public bool Pending { get; set; }
        public object Fork(PredictionForkContext context) => MemberwiseClone();
        public void AssertForkable()
        {
            if (Pending)
                throw new InvalidOperationException("pending transaction");
        }
    }

    private sealed class OrderedState<T>(string label, List<string> order) : IPredictionStateForkable
    {
        public object Fork(PredictionForkContext context)
        {
            order.Add(label);
            return new OrderedState<T>(label, order);
        }
    }
}
