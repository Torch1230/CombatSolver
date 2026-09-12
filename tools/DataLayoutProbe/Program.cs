using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation.Compact;

// Allocation/layout evidence only. Synthetic shapes are not substitutes for game benchmarks.
const int iterations = 10_000;
const int blocks = 5;
object[] identities = Enumerable.Range(0, 16).Select(_ => new object()).ToArray();
int[] seed = Enumerable.Range(1, 16).ToArray();
var currentRoot = new ForkableList<int>(seed);
var mergedRoot = new MergedList<int>(seed);
var rows = new List<object>();
Add("class_one_int", () => new IntCounter());
Add("class_one_short", () => new ShortCounter());
Add("boxed_struct_int", () => new CounterValue(1));
Add("wrapper_three_refs_two_bools", () => new CardWrapperShape());
Add("wrapper_three_refs_byte_flags", () => new CardWrapperFlagsShape());
Add("long_array_64", () => new long[64]);
Add("int_array_64", () => new int[64]);
Add("short_array_64", () => new short[64]);
Add("bool_array_64", () => new bool[64]);
Add("bit_array_64", () => new BitArray(64));
Add("int_array_1024", () => new int[1024]);
Add("short_array_1024", () => new short[1024]);
Add("bool_array_1024", () => new bool[1024]);
Add("bit_array_1024", () => new BitArray(1024));
foreach (int count in new[] { 1, 4, 16 })
{
    Add($"state_dictionary_{count}", () =>
    {
        var map = new Dictionary<(object, Type), object>(count);
        for (int i = 0; i < count; i++) map.Add((identities[i], typeof(IntCounter)), identities[i]);
        return map;
    });
    Add($"state_entries_array_{count}", () =>
    {
        var entries = new StateEntry[count];
        for (int i = 0; i < count; i++) entries[i] = new(identities[i], typeof(IntCounter), identities[i]);
        return entries;
    });
}
Add("forkable_list_empty_current", () => new ForkableList<int>());
Add("forkable_list_empty_merged", () => new MergedList<int>());
Add("forkable_list_seed16_current", () => new ForkableList<int>(seed));
Add("forkable_list_seed16_merged", () => new MergedList<int>(seed));
Add("forkable_list_fork_current", () => currentRoot.Fork());
Add("forkable_list_fork_merged", () => mergedRoot.Fork());
Add("forkable_list_fork_write_current", () => { var child = currentRoot.Fork(); child[0] = 99; return child; });
Add("forkable_list_fork_write_merged", () => { var child = mergedRoot.Fork(); child[0] = 99; return child; });

CheckListOwnership();
var vitals = new CreatureVitals(1, 1, 0);
vitals.SetMaxHp(999_999_999);
vitals.GainBlock(999_999_999m);
Require(vitals.MaxHp > ushort.MaxValue && vitals.Block > ushort.MaxValue, "Native range cannot fit ushort.");
var instance = new CardInstanceValue(42, 999_999_999, CardKeywordFlags.Retain, true);
Require(CardInstanceValue.Decode(instance.Data) == instance && (long)(int)instance.Data != instance.Data,
    "Packed card identity must retain its upper 32 bits.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    kind = "StandaloneLayoutAndAllocationProbe",
    gameBenchmark = false,
    runtime = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    os = RuntimeInformation.OSDescription,
    iterationsPerBlock = iterations,
    blocks,
    warmupIterations = 1024,
    managedValueSizes = new Dictionary<string, int>
    {
        ["int"] = Unsafe.SizeOf<int>(), ["short"] = Unsafe.SizeOf<short>(),
        ["object_type_key"] = Unsafe.SizeOf<(object, Type)>(),
        ["object_byte_key"] = Unsafe.SizeOf<(object, byte)>(),
        ["dictionary_object_int_entry"] = DictionaryEntrySize(new Dictionary<object, int>(1)),
        ["dictionary_object_byte_entry"] = DictionaryEntrySize(new Dictionary<object, byte>(1)),
        ["dictionary_object_type_object_entry"] = DictionaryEntrySize(new Dictionary<(object, Type), object>(1)),
        ["flat_state_entry"] = Unsafe.SizeOf<StateEntry>(),
        ["creature_vitals"] = Unsafe.SizeOf<CreatureVitals>(),
        ["card_instance_value"] = Unsafe.SizeOf<CardInstanceValue>()
    },
    checks = new[] { "parent_sibling_isolation", "captured_enumerator_storage", "missing_remove", "mutation_version",
        "native_vital_range", "packed_card_roundtrip" },
    allocations = rows
}, new JsonSerializerOptions { WriteIndented = true }));

void Add(string name, Func<object> factory)
{
    for (int i = 0; i < 1024; i++) ProbeSink.Value = factory();
    var samples = new long[blocks];
    for (int block = 0; block < blocks; block++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) ProbeSink.Value = factory();
        samples[block] = GC.GetAllocatedBytesForCurrentThread() - before;
    }
    rows.Add(new { name, allocatedBytes = samples, bytesPerOperation = samples.Select(v => (double)v / iterations).ToArray() });
}

static int DictionaryEntrySize<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
{
    Array entries = (Array)typeof(Dictionary<TKey, TValue>).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(dictionary)!;
    return (int)typeof(ProbeSink).GetMethod(nameof(ProbeSink.ManagedSize))!.MakeGenericMethod(entries.GetType().GetElementType()!)
        .Invoke(null, null)!;
}

static void CheckListOwnership()
{
    var original = new ForkableList<int>(new[] { 1, 2, 3 });
    var merged = new MergedList<int>(new[] { 1, 2, 3 });
    var originalA = original.Fork(); var originalB = original.Fork();
    var mergedA = merged.Fork(); var mergedB = merged.Fork();
    var beforeOriginal = originalA.GetEnumerator(); var beforeMerged = mergedA.GetEnumerator();
    Require(beforeOriginal.MoveNext() && beforeMerged.MoveNext(), "Initial iterator missing.");
    originalA[1] = 99; mergedA[1] = 99;
    originalB.Add(4); mergedB.Add(4);
    original.Remove(1); merged.Remove(1);
    Require(original.SequenceEqual(merged) && originalA.SequenceEqual(mergedA) && originalB.SequenceEqual(mergedB), "Fork pollution.");
    Require(beforeOriginal.MoveNext() && beforeMerged.MoveNext() && beforeOriginal.Current == 2 && beforeMerged.Current == 2,
        "Captured iterator changed when detached storage was written.");
    Require(!originalA.Remove(-1) && !mergedA.Remove(-1), "Missing removal changed result.");
    var afterOriginal = originalA.GetEnumerator(); var afterMerged = mergedA.GetEnumerator();
    originalA.Add(7); mergedA.Add(7);
    bool oldThrew = false, newThrew = false;
    try { afterOriginal.MoveNext(); } catch (InvalidOperationException) { oldThrew = true; }
    try { afterMerged.MoveNext(); } catch (InvalidOperationException) { newThrew = true; }
    Require(oldThrew && newThrew, "Owned mutation must invalidate a captured iterator.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static class ProbeSink
{
    public static object? Value;
    public static int ManagedSize<T>() => Unsafe.SizeOf<T>();
}
sealed class IntCounter { public int Value; }
sealed class ShortCounter { public short Value; }
readonly record struct CounterValue(int Value);
readonly record struct StateEntry(object Model, Type StateType, object State);
sealed class CardWrapperShape { public object? Storage, Owner, Observer; public bool Observe, Isolate; }
sealed class CardWrapperFlagsShape { public object? Storage, Owner, Observer; public byte Flags; }

// Research-only candidate: merge the shared-storage wrapper into the List object.
// Deliberately limited to operations exercised above; this is not a production replacement.
sealed class MergedList<T> : IReadOnlyList<T>
{
    private sealed class Storage : List<T>
    {
        public volatile bool Shared;
        public Storage() { }
        public Storage(IEnumerable<T> values) : base(values) { }
    }
    private Storage _storage;
    public MergedList() => _storage = new Storage();
    public MergedList(IEnumerable<T> values) => _storage = new Storage(values);
    private MergedList(Storage storage) => _storage = storage;
    public int Count => _storage.Count;
    public T this[int index] { get => _storage[index]; set { EnsureWritable(); _storage[index] = value; } }
    public MergedList<T> Fork() { _storage.Shared = true; return new(_storage); }
    public void Add(T value) { EnsureWritable(); _storage.Add(value); }
    public bool Remove(T value)
    {
        if (_storage.Shared) { if (!_storage.Contains(value)) return false; EnsureWritable(); }
        return _storage.Remove(value);
    }
    private void EnsureWritable() { if (_storage.Shared) _storage = new Storage(_storage); }
    public List<T>.Enumerator GetEnumerator() => _storage.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
