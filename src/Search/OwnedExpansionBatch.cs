namespace CombatSolver;

/// <summary>Reusable containers only; every published batch has its own disposable lease.</summary>
internal class OwnedExpansionBatch<TSnapshot, TCard, TNode> : IDisposable
    where TSnapshot : class
{
    internal sealed class Storage
    {
        internal readonly HashSet<TSnapshot> Owned = new(ReferenceEqualityComparer.Instance);
        internal readonly HashSet<TSnapshot> Transferred = new(ReferenceEqualityComparer.Instance);
        internal readonly List<TCard> Cards = new(16);
        internal readonly List<TNode> Potions = [];
        internal readonly List<TNode> EndTurns = [];

        internal bool FitsCapacity(int limit)
            => Cards.Capacity <= limit && Potions.Capacity <= limit && EndTurns.Capacity <= limit
                && Owned.EnsureCapacity(0) <= limit && Transferred.EnsureCapacity(0) <= limit;

        internal void Clear(Action<TSnapshot> release)
        {
            foreach (TSnapshot snapshot in Owned)
                release(snapshot);
            Owned.Clear();
            Transferred.Clear();
            Cards.Clear();
            Potions.Clear();
            EndTurns.Clear();
        }
    }

    internal sealed class Pool(Action<TSnapshot> release)
    {
        private const int MaximumIdleCount = 2;
        private const int MaximumContainerCapacity = 4096;
        private readonly Lock _gate = new();
        private readonly List<Storage> _idle = new(MaximumIdleCount);

        internal void ReleaseSnapshot(TSnapshot snapshot) => release(snapshot);

        internal Storage Rent()
        {
            lock (_gate)
            {
                if (_idle.Count > 0)
                {
                    Storage storage = _idle[^1];
                    _idle.RemoveAt(_idle.Count - 1);
                    return storage;
                }
            }
            return new Storage();
        }

        internal void Return(Storage storage)
        {
            // Clearing outside the lock is safe: this lease still exclusively owns storage.
            // A failed cleanup never publishes partially cleared containers to a later renter.
            storage.Clear(release);
            if (!storage.FitsCapacity(MaximumContainerCapacity))
                return;
            lock (_gate)
            {
                if (_idle.Count < MaximumIdleCount)
                    _idle.Add(storage);
            }
        }

        public void Clear()
        {
            lock (_gate)
                _idle.Clear();
        }
    }

    private readonly Pool _pool;
    private Storage? _storage;

    public OwnedExpansionBatch(Pool pool)
    {
        _pool = pool;
        _storage = pool.Rent();
    }

    internal Storage CurrentStorage => _storage
        ?? throw new ObjectDisposedException(GetType().Name);
    public List<TCard> Cards => CurrentStorage.Cards;
    public List<TNode> Potions => CurrentStorage.Potions;
    public List<TNode> EndTurns => CurrentStorage.EndTurns;

    protected void AddCard(TSnapshot snapshot, TCard candidate)
    {
        Own(snapshot);
        Cards.Add(candidate);
    }

    private void AdoptCard(TSnapshot snapshot, TCard candidate)
    {
        Own(snapshot);
        try { Cards.Add(candidate); }
        catch
        {
            CurrentStorage.Owned.Remove(snapshot);
            throw;
        }
    }

    protected void TransferTo(OwnedExpansionBatch<TSnapshot, TCard, TNode> target,
        TSnapshot snapshot, TCard candidate)
    {
        if (!CurrentStorage.Owned.Contains(snapshot))
            throw new InvalidOperationException("并行展开快照没有可移交的所有权。");
        target.AdoptCard(snapshot, candidate);
        if (!CurrentStorage.Owned.Remove(snapshot))
        {
            target.Release(snapshot);
            throw new InvalidOperationException("并行展开快照移交时丢失所有权。");
        }
    }

    protected void AddPotion(TSnapshot snapshot, TNode candidate)
    {
        Own(snapshot);
        Potions.Add(candidate);
    }

    protected void AddEndTurn(TSnapshot snapshot, TNode candidate)
    {
        Own(snapshot);
        EndTurns.Add(candidate);
    }

    public void Transfer(TSnapshot snapshot)
    {
        Storage storage = CurrentStorage;
        if (storage.Owned.Contains(snapshot))
        {
            // Register first: if growing this set fails, disposal still owns the snapshot.
            storage.Transferred.Add(snapshot);
            storage.Owned.Remove(snapshot);
            return;
        }
        if (!storage.Transferred.Contains(snapshot))
            throw new InvalidOperationException("并行展开快照没有可移交的所有权。");
    }

    public void Release(TSnapshot snapshot)
    {
        if (!CurrentStorage.Owned.Remove(snapshot))
            throw new InvalidOperationException("并行展开快照被重复释放或已经移交。");
        _pool.ReleaseSnapshot(snapshot);
    }

    public void Dispose()
    {
        // Old outcomes may Dispose again after these containers have been rented elsewhere.
        // Their distinct lease can no longer reach the returned storage.
        if (Interlocked.Exchange(ref _storage, null) is { } storage)
            _pool.Return(storage);
    }

    private void Own(TSnapshot snapshot)
    {
        if (!CurrentStorage.Owned.Add(snapshot))
            throw new InvalidOperationException("并行展开生成了共享的子快照所有权。");
    }
}
