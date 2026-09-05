using System.Text.Json;
using CombatSolver;

foreach (int count in new[] { 0, 1, 16, 32, 33, 64, 96, 128, 1024 })
{
    List<object> source = Enumerable.Range(0, count).Select(_ => new object()).ToList();
    IReadOnlyList<object> snapshot = ImmutableListenerList<object>.Capture(source);
    Require(snapshot.SequenceEqual(source), "Capture changed order or identities.");
    if (count == 0)
    {
        Require(ReferenceEquals(snapshot, ImmutableListenerList<object>.Replace(snapshot, 0, [])), "Empty update allocated.");
        continue;
    }

    object original = source[0];
    source[0] = new object();
    Require(ReferenceEquals(snapshot[0], original), "Source mutation escaped into a published snapshot.");
    object replacement = new();
    IReadOnlyList<object> changed = ImmutableListenerList<object>.Replace(snapshot, count - 1, [replacement]);
    Require(ReferenceEquals(changed[count - 1], replacement) && !ReferenceEquals(snapshot[count - 1], replacement),
        "Sparse replacement changed the previous snapshot.");
    Require(changed.Take(count - 1).SequenceEqual(snapshot.Take(count - 1)), "Sparse replacement changed neighbors.");
    Require(ReferenceEquals(changed, ImmutableListenerList<object>.Replace(changed, count - 1, [replacement])),
        "Identity-preserving update changed snapshot identity.");

    Dictionary<object, object> mapping = new(ReferenceEqualityComparer.Instance);
    foreach (int index in Enumerable.Range(0, count).Where(index => index % 31 == 0))
        mapping.Add(snapshot[index], new object());
    IReadOnlyList<object> mapped = ImmutableListenerList<object>.Remap(
        snapshot, mapping, static (map, item) => map.GetValueOrDefault(item, item));
    for (int index = 0; index < count; index++)
        Require(ReferenceEquals(mapped[index], mapping.GetValueOrDefault(snapshot[index], snapshot[index])), "Remap lost order/identity.");
    Require(ReferenceEquals(snapshot, ImmutableListenerList<object>.Remap(snapshot, 0, static (_, item) => item)),
        "No-op remap did not share its immutable input.");

    if (count >= 34)
    {
        object[] three = [new(), new(), new()];
        IReadOnlyList<object> crossing = ImmutableListenerList<object>.Replace(snapshot, 31, three);
        Require(crossing.Skip(31).Take(3).SequenceEqual(three), "Replacement across page boundary failed.");
        Require(crossing.Take(31).SequenceEqual(snapshot.Take(31))
            && crossing.Skip(34).SequenceEqual(snapshot.Skip(34)), "Boundary replacement reordered its neighbors.");
        // Keep an enumerator on the old snapshot while deriving another branch.
        using IEnumerator<object> reader = snapshot.GetEnumerator();
        Require(reader.MoveNext() && ReferenceEquals(reader.Current, snapshot[0]), "Old snapshot enumerator failed.");
        _ = ImmutableListenerList<object>.Replace(crossing, 30, [new object()]);
        for (int index = 1; index < count; index++)
            Require(reader.MoveNext() && ReferenceEquals(reader.Current, snapshot[index]), "New branch changed an in-flight enumeration.");
    }
}
foreach (int count in new[] { 0, 1, 16, 32, 33, 64, 96, 128, 1024 })
{
    List<object> privateBuffer = Enumerable.Range(0, count).Select(_ => new object()).ToList();
    object[] original = privateBuffer.ToArray();
    IReadOnlyList<object> owned = ImmutableListenerList<object>.TakeOwnership(privateBuffer);
    Require(owned.SequenceEqual(original), "Ownership transfer changed order or identities.");
    Require(count <= 32 ? ReferenceEquals(owned, privateBuffer) : ReferenceEquals(StorageOf(owned), privateBuffer),
        "Ownership transfer copied the rebuild buffer.");
    Require(ReferenceEquals(owned, ImmutableListenerList<object>.Replace(owned, 0, [])), "Empty owned update allocated.");
    Require(ReferenceEquals(owned, ImmutableListenerList<object>.Remap(owned, 0, static (_, item) => item)),
        "No-op owned remap changed snapshot identity.");
    if (count > 32)
        Require(ReferenceEquals(StorageOf(owned), privateBuffer), "No-op operation eagerly paged its owned buffer.");
    if (count == 0)
        continue;

    using IEnumerator<object> reader = owned.GetEnumerator();
    Require(reader.MoveNext() && ReferenceEquals(reader.Current, original[0]), "Owned enumeration did not begin correctly.");
    object replacement = new();
    int changedAt = count >= 34 ? 31 : count - 1;
    object[] replacements = count >= 34 ? [replacement, new(), new()] : [replacement];
    IReadOnlyList<object> changed = ImmutableListenerList<object>.Replace(owned, changedAt, replacements);
    Require(owned.SequenceEqual(original), "Owned promotion changed the source snapshot.");
    for (int index = 0; index < count; index++)
    {
        object expected = index >= changedAt && index < changedAt + replacements.Length
            ? replacements[index - changedAt]
            : original[index];
        Require(ReferenceEquals(changed[index], expected), "Owned promotion changed a neighbor or lost a replacement.");
    }
    object? promoted = count > 32 ? StorageOf(owned) : null;
    if (count > 32)
        Require(promoted is object[][], "The first real owned update did not promote its source for sibling reuse.");
    IReadOnlyList<object> sibling = ImmutableListenerList<object>.Replace(owned, 0, [new object()]);
    Require(sibling.Skip(1).SequenceEqual(original.Skip(1)), "Sibling replacement changed untouched identities.");
    if (count > 32)
        Require(ReferenceEquals(StorageOf(owned), promoted), "A later sibling rebuilt its source's page table.");
    for (int index = 1; index < count; index++)
        Require(reader.MoveNext() && ReferenceEquals(reader.Current, original[index]), "Promotion changed an in-flight owned enumeration.");
    Require(!reader.MoveNext(), "Owned enumeration grew after promotion.");

    IReadOnlyList<object> remapSource = ImmutableListenerList<object>.TakeOwnership(original.ToList());
    int visited = 0;
    IReadOnlyList<object> remapped = ImmutableListenerList<object>.Remap(
        remapSource, replacement, (mapped, item) =>
        {
            Require(ReferenceEquals(item, original[visited]), "Owned remap changed traversal order or repeated a callback.");
            return visited++ == changedAt ? mapped : item;
        });
    Require(visited == count, "Owned remap omitted an entry.");
    for (int index = 0; index < count; index++)
        Require(ReferenceEquals(remapped[index], index == changedAt ? replacement : original[index]), "Owned remap lost identity/order.");
    Require(remapSource.SequenceEqual(original), "Owned remap mutated the source snapshot.");
    if (count > 32)
        Require(StorageOf(remapSource) is object[][], "A changed owned remap did not publish reusable pages.");
}

{
    object[] original = Enumerable.Range(0, 128).Select(_ => new object()).ToArray();
    IReadOnlyList<object> source = ImmutableListenerList<object>.TakeOwnership(original.ToList());
    using IEnumerator<object> reader = source.GetEnumerator();
    Require(reader.MoveNext(), "Concurrent promotion source was empty.");
    using Barrier ready = new(2);
    object leftReplacement = new();
    object rightReplacement = new();
    Task<IReadOnlyList<object>> left = Task.Run(() =>
    {
        ready.SignalAndWait();
        return ImmutableListenerList<object>.Replace(source, 31, [leftReplacement]);
    });
    Task<IReadOnlyList<object>> right = Task.Run(() =>
    {
        ready.SignalAndWait();
        return ImmutableListenerList<object>.Remap(source, rightReplacement,
            (replacement, item) => ReferenceEquals(item, original[96]) ? replacement : item);
    });
    Task.WaitAll(left, right);
    for (int index = 0; index < original.Length; index++)
    {
        Require(ReferenceEquals(source[index], original[index]), "Concurrent promotion changed its source.");
        Require(ReferenceEquals(left.Result[index], index == 31 ? leftReplacement : original[index]), "Concurrent replacement crossed into its sibling.");
        Require(ReferenceEquals(right.Result[index], index == 96 ? rightReplacement : original[index]), "Concurrent remap crossed into its sibling.");
        if (index > 0)
            Require(reader.MoveNext() && ReferenceEquals(reader.Current, original[index]), "Concurrent promotion changed a retained enumeration.");
    }
    Require(!reader.MoveNext(), "Concurrent promotion changed enumeration length.");
}
Console.WriteLine("PASS: capture copying, ownership transfer, lazy promotion, sibling page reuse, small/wide snapshots, sparse updates, page boundaries, no-op sharing, remap order, retained/concurrent enumeration");

if (args.Contains("--ownership-allocations", StringComparer.Ordinal))
{
    const int rebuildIterations = 2_000;
    foreach (int count in new[] { 16, 32, 96, 256, 1024 })
    {
        object[] original = Enumerable.Range(0, count).Select(_ => new object()).ToArray();
        object replacement = new();
        foreach (bool deriveSiblings in new[] { false, true })
        {
            _ = MeasureRebuilds(original, replacement, 100, false, deriveSiblings);
            _ = MeasureRebuilds(original, replacement, 100, true, deriveSiblings);
            long captureBytes = MeasureRebuilds(original, replacement, rebuildIterations, false, deriveSiblings);
            long ownershipBytes = MeasureRebuilds(original, replacement, rebuildIterations, true, deriveSiblings);
            Console.WriteLine(JsonSerializer.Serialize(new { count, iterations = rebuildIterations, deriveSiblings,
                captureBytesPerRebuild = captureBytes / rebuildIterations,
                ownershipBytesPerRebuild = ownershipBytes / rebuildIterations }));
        }
    }
    return;
}
if (args.Contains("--checks-only", StringComparer.Ordinal))
    return;

const int iterations = 20_000;
foreach (int count in new[] { 16, 32, 96, 256, 1024 })
{
    List<object> source = Enumerable.Range(0, count).Select(_ => new object()).ToList();
    object[] array = source.ToArray();
    IReadOnlyList<object> snapshot = ImmutableListenerList<object>.Capture(source);
    object replacement = new();
    for (int warm = 0; warm < 100; warm++)
        GC.KeepAlive(ImmutableListenerList<object>.Replace(snapshot, count / 2, [replacement]));
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < iterations; index++)
    {
        object[] copy = (object[])array.Clone();
        copy[count / 2] = replacement;
        GC.KeepAlive(copy);
    }
    long arrayBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    before = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < iterations; index++)
        GC.KeepAlive(ImmutableListenerList<object>.Replace(snapshot, count / 2, [replacement]));
    long snapshotBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine(JsonSerializer.Serialize(new { count, iterations, arrayBytes, snapshotBytes,
        arrayBytesPerUpdate = arrayBytes / iterations, snapshotBytesPerUpdate = snapshotBytes / iterations }));
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

// Lazy promotion is an allocation contract as well as a semantic one. Inspect the actual
// production representation here so a no-op cannot silently acquire a full page table.
static object StorageOf(IReadOnlyList<object> source)
    => typeof(ImmutableListenerList<object>)
        .GetField("_storage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(source)!;

static long MeasureRebuilds(object[] original, object replacement, int iterations, bool takeOwnership, bool deriveSiblings)
{
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < iterations; index++)
    {
        List<object> privateBuffer = new(original);
        IReadOnlyList<object> snapshot = takeOwnership
            ? ImmutableListenerList<object>.TakeOwnership(privateBuffer)
            : ImmutableListenerList<object>.Capture(privateBuffer);
        if (deriveSiblings)
        {
            GC.KeepAlive(ImmutableListenerList<object>.Replace(snapshot, original.Length / 2, [replacement]));
            GC.KeepAlive(ImmutableListenerList<object>.Replace(snapshot, 0, [replacement]));
        }
        GC.KeepAlive(snapshot);
    }
    return GC.GetAllocatedBytesForCurrentThread() - before;
}
