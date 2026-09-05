using System.Runtime.CompilerServices;
using CombatSolver;
using Buffer = CombatSolver.OwnedExpansionBatch<Resource, Resource, Resource>;

CheckCrossThreadReturnAndOldLease();
CheckPartialTransferFailure();
CheckTransferredOwnership();
CheckPoolBoundAndClear();
CheckCapacityRejection();
CheckConcurrentPoolUse();
CheckClearedReferences();
Console.WriteLine("Passed 7 expansion batch checks using the actual production storage/lease source.");

static Buffer.Pool CreatePool() => new(static resource => resource.Release());

static void CheckCrossThreadReturnAndOldLease()
{
    Buffer.Pool pool = CreatePool();
    TestBatch old = new(pool);
    Resource first = new();
    old.Add(first);
    Buffer.Storage storage = old.CurrentStorage;
    Task.Run(old.Dispose).GetAwaiter().GetResult();
    Require(first.Releases == 1 && storage.Cards.Count == 0 && storage.Owned.Count == 0,
        "Cross-thread return did not release and clear owned entries.");
    using TestBatch current = new(pool);
    Require(ReferenceEquals(storage, current.CurrentStorage), "Returned storage was not reused.");
    Resource next = new();
    current.Add(next);
    old.Dispose();
    Task.Run(old.Dispose).GetAwaiter().GetResult();
    Require(next.Releases == 0 && current.Cards.Count == 1 && current.CurrentStorage.Owned.Contains(next),
        "An old lease disposed the next renter's storage.");
    current.Dispose();
    Require(next.Releases == 1, "The current lease did not release its own resource exactly once.");
}

static void CheckPartialTransferFailure()
{
    Buffer.Pool pool = CreatePool();
    using TestBatch source = new(pool);
    using TestBatch target = new(pool);
    Resource moved = new();
    Resource pending = new();
    source.Add(moved);
    source.Add(pending);
    source.MoveTo(target, moved);
    Require(!source.CurrentStorage.Owned.Contains(moved) && target.CurrentStorage.Owned.Contains(moved),
        "A successful transfer left duplicate ownership.");
    target.Dispose();
    bool failed = false;
    try { source.MoveTo(target, pending); }
    catch (ObjectDisposedException) { failed = true; }
    Require(failed && source.CurrentStorage.Owned.Contains(pending),
        "A failed later transfer lost the source's ownership.");
    source.Dispose();
    Require(moved.Releases == 1 && pending.Releases == 1,
        "Partial transfer failure leaked or double-released a resource.");
}

static void CheckTransferredOwnership()
{
    Buffer.Pool pool = CreatePool();
    using TestBatch batch = new(pool);
    Resource accepted = new();
    batch.Add(accepted);
    batch.Transfer(accepted);
    batch.Transfer(accepted);
    bool rejected = false;
    try { batch.Release(accepted); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "A transferred resource was still releasable by its old owner.");
    batch.Dispose();
    Require(accepted.Releases == 0, "Disposal released a transferred candidate.");
    accepted.Release();
}

static void CheckPoolBoundAndClear()
{
    Buffer.Pool pool = CreatePool();
    TestBatch[] leases = [new(pool), new(pool), new(pool)];
    Buffer.Storage[] original = leases.Select(batch => batch.CurrentStorage).ToArray();
    foreach (TestBatch lease in leases)
        lease.Dispose();
    TestBatch[] next = [new(pool), new(pool), new(pool)];
    Require(next.Count(batch => original.Any(storage => ReferenceEquals(storage, batch.CurrentStorage))) == 2,
        "The pool retained more or fewer than two idle containers.");
    foreach (TestBatch lease in next)
        lease.Dispose();
    pool.Clear();
    using TestBatch afterClear = new(pool);
    Require(!next.Any(batch => ReferenceEquals(batch, afterClear)), "A lease wrapper was pooled.");
    Require(!original.Any(storage => ReferenceEquals(storage, afterClear.CurrentStorage)),
        "Checkpoint clear retained idle storage.");
    Resource active = new();
    afterClear.Add(active);
    pool.Clear();
    Require(active.Releases == 0 && afterClear.Cards.Count == 1, "Clearing idle storage affected an active lease.");
}

static void CheckCapacityRejection()
{
    Action<Buffer.Storage>[] enlarge =
    [
        storage => storage.Cards.EnsureCapacity(4097),
        storage => storage.Potions.EnsureCapacity(4097),
        storage => storage.EndTurns.EnsureCapacity(4097),
        storage => storage.Owned.EnsureCapacity(4097),
        storage => storage.Transferred.EnsureCapacity(4097),
    ];
    foreach (Action<Buffer.Storage> grow in enlarge)
    {
        Buffer.Pool pool = CreatePool();
        TestBatch oversized = new(pool);
        Buffer.Storage storage = oversized.CurrentStorage;
        grow(storage);
        oversized.Dispose();
        using TestBatch next = new(pool);
        Require(!ReferenceEquals(storage, next.CurrentStorage), "An oversized container was retained after Clear.");
    }
}

static void CheckConcurrentPoolUse()
{
    Buffer.Pool pool = CreatePool();
    Parallel.For(0, 512, _ =>
    {
        using TestBatch batch = new(pool);
        Resource resource = new();
        batch.Add(resource);
        Thread.Yield();
        Require(batch.Cards.Count == 1 && ReferenceEquals(batch.Cards[0], resource),
            "Concurrent renters shared the same mutable storage.");
        batch.Dispose();
        Require(resource.Releases == 1, "Concurrent return released a resource incorrectly.");
    });
}

static void CheckClearedReferences()
{
    Buffer.Pool pool = CreatePool();
    WeakReference<Resource>[] weak = ReturnReferencedStorage(pool);
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    Require(weak.All(reference => !reference.TryGetTarget(out _)), "Idle storage retained candidate references.");
    GC.KeepAlive(pool);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference<Resource>[] ReturnReferencedStorage(Buffer.Pool pool)
{
    using TestBatch batch = new(pool);
    Resource card = new(), potion = new(), endTurn = new(), transferred = new();
    batch.Add(card);
    batch.Potion(potion);
    batch.EndTurn(endTurn);
    batch.Add(transferred);
    batch.Transfer(transferred);
    return [new(card), new(potion), new(endTurn), new(transferred)];
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class Resource
{
    private int _releases;
    public int Releases => Volatile.Read(ref _releases);
    public void Release() => Interlocked.Increment(ref _releases);
}

internal sealed class TestBatch(Buffer.Pool pool) : Buffer(pool)
{
    public void Add(Resource resource) => AddCard(resource, resource);
    public void Potion(Resource resource) => AddPotion(resource, resource);
    public void EndTurn(Resource resource) => AddEndTurn(resource, resource);
    public void MoveTo(TestBatch target, Resource resource) => TransferTo(target, resource, resource);
}
