using Checks;
using System.Text.Json;
Random random = new(20260912);
int cases = 0;
foreach (int size in new[] { 0, 1, 2, 8, 9, 16, 64, 256, 1024 })
foreach (int survivorCount in new[] { 0, 1, 2, 8, 9, 16, 64, 256 })
for (int trial = 0; trial < 12; trial++)
{
    SimulationSnapshot[] snapshots = Enumerable.Range(0, Math.Max(1, size + survivorCount)).Select(i => new SimulationSnapshot(i)).ToArray();
    SearchNode[] candidates = Enumerable.Range(0, size).Select(_ => new SearchNode(snapshots[random.Next(snapshots.Length)])).ToArray();
    SearchNode[] survivors = Enumerable.Range(0, survivorCount).Select(_ => new SearchNode(snapshots[random.Next(snapshots.Length)])).ToArray();
    // Distinct nodes can share a snapshot; retained-only snapshots and repeated
    // candidate/survivor references must preserve the exact release call sequence.
    List<int> expected = [];
    foreach (SearchNode node in candidates)
        if (!survivors.Any(s => ReferenceEquals(s.Snapshot, node.Snapshot)))
            expected.Add(node.Snapshot.Id);
    SimulationSnapshot.Released.Clear();
    Production.ReleaseDroppedSnapshots(candidates, survivors);
    if (!expected.SequenceEqual(SimulationSnapshot.Released))
        throw new InvalidOperationException($"Release sequence differs: {size}/{survivorCount}/{trial}");
    cases++;
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, cases, checks = "exact reference-based release call sequence; shared snapshots, duplicate references, empty and threshold pools, colliding value equality" }));
