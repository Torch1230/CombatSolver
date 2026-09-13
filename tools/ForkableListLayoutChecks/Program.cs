using System.Collections;
using System.Text.Json;
using CombatSolver;

CheckSemantics();
var source = Enumerable.Range(0, 16).ToArray();
var shared = new ForkableList<int>(source);
var rows = new Dictionary<string, long[]>();
foreach (var (name, create) in new (string, Func<object>)[]
{
    ("empty", () => new ForkableList<int>()),
    ("sixteen", () => new ForkableList<int>(source)),
    ("fork", () => shared.Fork()),
    ("forkThenWrite", () => { var child = shared.Fork(); child[0] = 99; return child; }),
})
{
    for (int i = 0; i < 1024; i++) Keep.Value = create();
    rows[name] = Enumerable.Range(0, 5).Select(_ =>
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Keep.Value = create();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10000;
    }).ToArray();
}
Console.WriteLine(JsonSerializer.Serialize(new { contracts = "Passed", runtime = Environment.Version.ToString(),
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), bytesPerOperation = rows },
    new JsonSerializerOptions { WriteIndented = true }));

static void CheckSemantics()
{
    var root = new ForkableList<int>([1, 2, 3]);
    var a = root.Fork(); var b = root.Fork();
    var old = a.GetEnumerator();
    a.Insert(1, 9); a.RemoveAt(0); a.Add(4); a[0] = 8;
    Assert(a.SequenceEqual([8, 2, 3, 4]), "mutations");
    Assert(root.SequenceEqual([1, 2, 3]) && b.SequenceEqual(root), "sibling isolation");
    var oldValues = new List<int>(); while (old.MoveNext()) oldValues.Add(old.Current);
    Assert(oldValues.SequenceEqual(root), "old shared enumerator");
    Assert(a.IndexOf(3) == 2 && a.Contains(8) && !a.Contains(77), "queries");
    var current = a.GetEnumerator(); a.Add(5);
    bool invalidated = false;
    try { current.MoveNext(); } catch (InvalidOperationException) { invalidated = true; }
    Assert(invalidated, "exclusive enumerator invalidation");
    Assert(!b.Remove(77) && b.Remove(2) && b.SequenceEqual([1, 3]), "remove");
    Assert(((IEnumerable)root).Cast<int>().SequenceEqual(root), "non-generic enumeration");
    var random = new Random(271828);
    var branches = new List<(ForkableList<int> Actual, List<int> Expected)> { (new ForkableList<int>([1, 2, 3]), [1, 2, 3]) };
    for (int i = 0; i < 10000; i++)
    {
        var branch = branches[random.Next(branches.Count)];
        int index = random.Next(branch.Expected.Count + 1), value = random.Next(100);
        switch (random.Next(6))
        {
            case 0:
                if (branches.Count < 64) branches.Add((branch.Actual.Fork(), new(branch.Expected)));
                break;
            case 1: branch.Actual.Insert(index, value); branch.Expected.Insert(index, value); break;
            case 2: branch.Actual.Add(value); branch.Expected.Add(value); break;
            case 3:
                Assert(branch.Actual.Remove(value) == branch.Expected.Remove(value), "random remove"); break;
            case 4 when index < branch.Expected.Count:
                branch.Actual[index] = value; branch.Expected[index] = value; break;
            case 5 when index < branch.Expected.Count:
                branch.Actual.RemoveAt(index); branch.Expected.RemoveAt(index); break;
        }
        foreach (var item in branches) Assert(item.Actual.SequenceEqual(item.Expected), "random branch isolation");
    }
    var lanes = Enumerable.Range(0, 8).Select(_ => root.Fork()).ToArray();
    Parallel.For(0, lanes.Length, lane =>
    {
        var copy = lanes[lane];
        for (int i = 0; i < 1000; i++) { copy.Insert(0, lane); copy.RemoveAt(1); }
        Assert(copy[0] == lane && copy.Count == 3, "independent worker");
    });
    Assert(root.SequenceEqual([1, 2, 3]), "root after workers");
}
static void Assert(bool value, string name)
{ if (!value) throw new InvalidOperationException(name); }
static class Keep { public static object? Value; }
