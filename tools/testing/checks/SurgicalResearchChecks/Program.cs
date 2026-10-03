using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;

PredictionStateStore store = new();
AbstractModel model = new() { Value = 17 };
List<object> results = [];
const int repetitions = 100_000;

Measure("empty iterator", () => ReadEmpty(store));
Measure("guarded empty iterator", () => ReadGuarded(store));
Measure("missing Peek scalar", () => ReadPeek(store, model));
Measure("missing TryGet scalar", () => ReadScalar(store, model));
if (store.HasEntries<ScalarState>()) throw new Exception("Read inserted state");
store.Get(model, static m => new ScalarState(m)).Value = 29;
if (ReadPeek(store, model) != 29 || ReadScalar(store, model) != 29)
    throw new Exception("Stored value ignored");
Measure("present Peek scalar", () => ReadPeek(store, model));
Measure("present TryGet scalar", () => ReadScalar(store, model));
Type[] types = [typeof(int), typeof(long), typeof(string), typeof(bool), typeof(byte), typeof(short), typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(object), typeof(DateTime), typeof(Guid), typeof(Type), typeof(Array), typeof(Exception)];
foreach (int count in new[] { 1, 2, 4, 8, 16 })
{
    Measure($"count table growing {count}", () => MakeTable(types, count, false));
    Measure($"count table capacity {count}", () => MakeTable(types, count, true));
}
Console.WriteLine(JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), repetitions, results }, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string name, Func<int> run)
{
    int checksum = 0;
    for (int i = 0; i < 10_000; i++) checksum += run();
    double[] bytes = new double[3];
    for (int block = 0; block < bytes.Length; block++)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < repetitions; i++) checksum += run();
        bytes[block] = (GC.GetAllocatedBytesForCurrentThread() - start) / (double)repetitions;
    }
    results.Add(new { name, bytesPerOperation = bytes, checksum });
}

[MethodImpl(MethodImplOptions.NoInlining)]
static int ReadEmpty(PredictionStateStore store)
{
    int sum = 0;
    foreach (var (_, state) in store.ReadEntries<ScalarState>()) sum += state.Value;
    return sum;
}
[MethodImpl(MethodImplOptions.NoInlining)]
static int ReadGuarded(PredictionStateStore store)
    => store.HasEntries<ScalarState>() ? ReadEmpty(store) : 0;
[MethodImpl(MethodImplOptions.NoInlining)]
static int ReadPeek(PredictionStateStore store, AbstractModel model)
    => store.Peek(model, static m => new ScalarState(m)).Value;
[MethodImpl(MethodImplOptions.NoInlining)]
static int ReadScalar(PredictionStateStore store, AbstractModel model)
    => store.TryGetReadOnly(model, out ScalarState? value) ? value!.Value : model.Value;
[MethodImpl(MethodImplOptions.NoInlining)]
static int MakeTable(Type[] types, int count, bool capacity)
{
    Dictionary<Type, int> table = capacity ? new(count) : new();
    for (int i = 0; i < count; i++) table[types[i]] = i + 1;
    GC.KeepAlive(table);
    return table.Count;
}

internal sealed class ScalarState(AbstractModel model) : IPredictionStateForkable
{
    public int Value = model.Value;
    public object Fork(PredictionForkContext context) => throw new NotSupportedException();
}
