namespace CombatSolver;

/// <summary>
/// One cleared temporary list per search run/lane. Rent removes the idle list, so a nested
/// snapshot receives independent storage. Access and checkpoint clearing require an idle lane
/// or its owning thread; this is not a shared concurrent pool.
/// </summary>
internal sealed class SnapshotListBuffer<T>
{
    private const int MaximumRetainedCapacity = 4096;
    private Storage? _idle;

    internal sealed class Storage
    {
        public readonly List<T> Items = [];
        public long Generation;
        public bool IsRented;
    }

    public Lease Rent()
    {
        Storage storage = _idle ?? new();
        _idle = null;
        storage.Generation = checked(storage.Generation + 1);
        storage.IsRented = true;
        return new Lease(this, storage, storage.Generation);
    }

    public void Clear() => _idle = null;

    private void Return(Storage storage, long generation)
    {
        if (!storage.IsRented || storage.Generation != generation)
            return;
        // Clear even discarded oversized/nested buffers before ending their ownership scope.
        storage.Items.Clear();
        storage.IsRented = false;
        if (storage.Items.Capacity <= MaximumRetainedCapacity && _idle == null)
            _idle = storage;
    }

    // Stack-only leases add no per-rental allocation. A generation check also protects a later
    // rental if a caller accidentally copies this value before disposing the original.
    public readonly ref struct Lease
    {
        private readonly SnapshotListBuffer<T> _owner;
        private readonly Storage _storage;
        private readonly long _generation;

        internal Lease(SnapshotListBuffer<T> owner, Storage storage, long generation)
            => (_owner, _storage, _generation) = (owner, storage, generation);

        public List<T> Items => _storage.IsRented && _storage.Generation == _generation
            ? _storage.Items
            : throw new ObjectDisposedException(nameof(Lease));

        public void Dispose() => _owner.Return(_storage, _generation);
    }
}
