namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// An exclusively owned value workspace. Execution frames and semantic state use the same
/// slots, so rolling back a choice restores both data and the next instruction to execute.
/// No model, delegate, or mutable simulator reference may be stored in these slots.
/// </summary>
internal sealed class ReversibleValueState
{
    private readonly object _rootIdentity;
    private readonly long[] _values;
    private readonly List<UndoEntry> _undo = new(128);
    private readonly List<Checkpoint> _checkpoints = new(8);
    private long _nextSequence;

    private readonly record struct UndoEntry(int Slot, long Previous);

    internal readonly record struct Checkpoint
    {
        internal ReversibleValueState Owner { get; }
        internal long Sequence { get; }
        internal int UndoIndex { get; }

        internal Checkpoint(ReversibleValueState owner, long sequence, int undoIndex)
        {
            Owner = owner;
            Sequence = sequence;
            UndoIndex = undoIndex;
        }
    }

    internal sealed class FrozenValues
    {
        private readonly object _rootIdentity;
        private readonly long[] _values;

        private FrozenValues(object rootIdentity, long[] values)
        {
            _rootIdentity = rootIdentity;
            _values = values;
        }

        public int Count => _values.Length;
        public long this[int slot] => _values[slot];
        public ReadOnlySpan<long> Values => _values;

        public ReversibleValueState CreateWorkspace()
            => new(_rootIdentity, (long[])_values.Clone());

        internal bool HasSameRoot(FrozenValues other)
            => ReferenceEquals(_rootIdentity, other._rootIdentity);

        internal static FrozenValues Capture(object rootIdentity, long[] values)
            => new(rootIdentity, (long[])values.Clone());
    }

    public ReversibleValueState(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        _rootIdentity = new object();
        _values = new long[slotCount];
    }

    private ReversibleValueState(object rootIdentity, long[] ownedValues)
    {
        _rootIdentity = rootIdentity;
        _values = ownedValues;
    }

    public int Count => _values.Length;
    public int CheckpointDepth => _checkpoints.Count;
    public long WriteAttempts { get; private set; }
    public long ValueChanges { get; private set; }
    public long UndoEntriesWritten { get; private set; }
    public int PeakUndoEntries { get; private set; }
    public long this[int slot] => _values[slot];

    public void Write(int slot, long value)
    {
        // Validate the slot before touching diagnostics or the journal.
        long previous = _values[slot];
        WriteAttempts++;
        if (previous == value)
            return;
        if (_checkpoints.Count != 0)
        {
            _undo.Add(new UndoEntry(slot, previous));
            UndoEntriesWritten++;
            PeakUndoEntries = Math.Max(PeakUndoEntries, _undo.Count);
        }
        _values[slot] = value;
        ValueChanges++;
    }

    public Checkpoint Mark()
    {
        Checkpoint checkpoint = new(this, checked(++_nextSequence), _undo.Count);
        _checkpoints.Add(checkpoint);
        return checkpoint;
    }

    public void Rollback(Checkpoint checkpoint)
    {
        RequireTop(checkpoint);
        for (int index = _undo.Count - 1; index >= checkpoint.UndoIndex; index--)
        {
            UndoEntry entry = _undo[index];
            _values[entry.Slot] = entry.Previous;
        }
        _undo.RemoveRange(checkpoint.UndoIndex, _undo.Count - checkpoint.UndoIndex);
        _checkpoints.RemoveAt(_checkpoints.Count - 1);
    }

    public FrozenValues Freeze() => FrozenValues.Capture(_rootIdentity, _values);

    internal bool HasSameRoot(ReversibleValueState other)
        => ReferenceEquals(_rootIdentity, other._rootIdentity);

    internal int DistinctWrittenSlots(Checkpoint checkpoint)
    {
        if (!ReferenceEquals(checkpoint.Owner, this) || !_checkpoints.Contains(checkpoint))
            throw new InvalidOperationException("Write-density observation requires an active owned checkpoint.");
        HashSet<int> slots = [];
        for (int index = checkpoint.UndoIndex; index < _undo.Count; index++) slots.Add(_undo[index].Slot);
        return slots.Count;
    }

    private void RequireTop(Checkpoint checkpoint)
    {
        if (!ReferenceEquals(checkpoint.Owner, this)
            || _checkpoints.Count == 0
            || _checkpoints[^1] != checkpoint)
        {
            throw new InvalidOperationException(
                "A compact execution checkpoint must be restored once, by its owner, in reverse order.");
        }
    }
}
