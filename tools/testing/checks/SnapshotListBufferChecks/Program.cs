using System.Runtime.CompilerServices;
using CombatSolver;

CheckNestedLeaseIsolation();
CheckExceptionalReturn();
CheckSingleIdleSlotAndReset();
CheckOversizedCapacityIsNotRetained();
CheckDisposedLeaseCannotClearNextRental();
CheckIndependentOwners();
CheckClearedReferences();
Console.WriteLine("Passed 7 snapshot list checks using the actual production buffer source.");

static void CheckNestedLeaseIsolation()
{
    SnapshotListBuffer<object> buffer = new();
    object first = new();
    object second = new();
    using SnapshotListBuffer<object>.Lease outer = buffer.Rent();
    outer.Items.Add(first);
    outer.Items.Add(second);
    List<object> innerItems;
    using (SnapshotListBuffer<object>.Lease inner = buffer.Rent())
    {
        innerItems = inner.Items;
        Require(!ReferenceEquals(outer.Items, inner.Items), "Nested snapshots shared a list.");
        inner.Items.Add(second);
        inner.Items.Add(first);
        inner.Items.Reverse();
        Require(outer.Items.Count == 2 && ReferenceEquals(outer.Items[0], first)
            && ReferenceEquals(outer.Items[1], second), "Nested work reordered the outer snapshot.");
    }
    Require(innerItems.Count == 0 && outer.Items.Count == 2,
        "Returning nested storage cleared the active outer snapshot or retained inner references.");
    using SnapshotListBuffer<object>.Lease nextInner = buffer.Rent();
    Require(ReferenceEquals(nextInner.Items, innerItems), "Nested storage was not reusable.");
}

static void CheckExceptionalReturn()
{
    SnapshotListBuffer<object> buffer = new();
    List<object>? partial = null;
    ProbeException failure = new();
    bool propagated = false;
    try
    {
        using SnapshotListBuffer<object>.Lease lease = buffer.Rent();
        partial = lease.Items;
        lease.Items.AddRange(FailAfterOneItem(failure));
    }
    catch (ProbeException error) when (ReferenceEquals(error, failure))
    {
        propagated = true;
    }
    Require(propagated && partial is { Count: 0 },
        "A failed population did not preserve its exception and clear partially added references.");
    using SnapshotListBuffer<object>.Lease recovered = buffer.Rent();
    Require(ReferenceEquals(partial, recovered.Items) && recovered.Items.Count == 0,
        "Failed work left the list unavailable or dirty for the next snapshot.");
}

static IEnumerable<object> FailAfterOneItem(ProbeException failure)
{
    yield return new object();
    throw failure;
}

static void CheckSingleIdleSlotAndReset()
{
    SnapshotListBuffer<object> buffer = new();
    SnapshotListBuffer<object>.Lease first = buffer.Rent();
    SnapshotListBuffer<object>.Lease second = buffer.Rent();
    List<object> firstItems = first.Items;
    List<object> secondItems = second.Items;
    first.Items.Add(new object());
    second.Items.Add(new object());
    second.Dispose();
    first.Dispose();
    Require(firstItems.Count == 0 && secondItems.Count == 0,
        "A discarded nested buffer retained references.");
    List<object> cachedBeforeReset;
    using (SnapshotListBuffer<object>.Lease cached = buffer.Rent())
    using (SnapshotListBuffer<object>.Lease uncached = buffer.Rent())
    {
        Require(ReferenceEquals(cached.Items, secondItems), "The one idle list was not reused.");
        Require(!ReferenceEquals(uncached.Items, firstItems)
            && !ReferenceEquals(uncached.Items, secondItems), "More than one idle list was retained.");
        cachedBeforeReset = uncached.Items;
        uncached.Items.Add(new object());
        buffer.Clear();
        Require(uncached.Items.Count == 1, "Checkpoint clearing modified an active lease.");
    }
    buffer.Clear();
    using SnapshotListBuffer<object>.Lease afterReset = buffer.Rent();
    Require(!ReferenceEquals(afterReset.Items, cachedBeforeReset),
        "Checkpoint clearing retained idle storage.");
}

static void CheckOversizedCapacityIsNotRetained()
{
    SnapshotListBuffer<object> buffer = new();
    List<object> allowed;
    using (SnapshotListBuffer<object>.Lease lease = buffer.Rent())
    {
        allowed = lease.Items;
        allowed.EnsureCapacity(4096);
        allowed.Add(new object());
    }
    List<object> oversized;
    using (SnapshotListBuffer<object>.Lease lease = buffer.Rent())
    {
        Require(ReferenceEquals(allowed, lease.Items), "A list at the capacity limit was discarded.");
        oversized = lease.Items;
        oversized.EnsureCapacity(4097);
        oversized.Add(new object());
    }
    Require(oversized.Count == 0 && oversized.Capacity > 4096,
        "The oversized test did not exercise actual capacity after clearing.");
    using SnapshotListBuffer<object>.Lease next = buffer.Rent();
    Require(!ReferenceEquals(next.Items, oversized), "An oversized empty list stayed in the cache.");
}

static void CheckDisposedLeaseCannotClearNextRental()
{
    SnapshotListBuffer<object> buffer = new();
    SnapshotListBuffer<object>.Lease previous = buffer.Rent();
    SnapshotListBuffer<object>.Lease copied = previous;
    List<object> previousItems = previous.Items;
    previous.Dispose();
    using SnapshotListBuffer<object>.Lease current = buffer.Rent();
    Require(ReferenceEquals(current.Items, previousItems), "The prior list was not actually reused.");
    current.Items.Add(new object());
    previous.Dispose();
    Require(current.Items.Count == 1, "Disposing an already returned lease cleared a later rental.");
    copied.Dispose();
    Require(current.Items.Count == 1, "A copied stale lease cleared a later rental.");
    bool rejected = false;
    try { _ = previous.Items; }
    catch (ObjectDisposedException) { rejected = true; }
    Require(rejected, "An ended lease still exposed its list.");
}

static void CheckIndependentOwners()
{
    SnapshotListBuffer<object> coordinator = new();
    SnapshotListBuffer<object> worker = new();
    using SnapshotListBuffer<object>.Lease coordinatorLease = coordinator.Rent();
    using SnapshotListBuffer<object>.Lease workerLease = worker.Rent();
    Require(!ReferenceEquals(coordinatorLease.Items, workerLease.Items), "Run owners shared storage.");
    coordinatorLease.Items.Add(new object());
    worker.Clear();
    Require(coordinatorLease.Items.Count == 1, "Clearing another run touched the coordinator list.");
}

static void CheckClearedReferences()
{
    SnapshotListBuffer<object> buffer = new();
    WeakReference<object> reference = PopulateAndRelease(buffer);
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    Require(!reference.TryGetTarget(out _), "An idle list retained a released snapshot element.");
    GC.KeepAlive(buffer);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference<object> PopulateAndRelease(SnapshotListBuffer<object> buffer)
{
    object item = new();
    using SnapshotListBuffer<object>.Lease lease = buffer.Rent();
    lease.Items.Add(item);
    return new WeakReference<object>(item);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class ProbeException : Exception { }
