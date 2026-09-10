namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// An exclusively owned value workspace. Execution frames and semantic state use the same
/// slots, so rolling back a choice restores both data and the next instruction to execute.
/// No model, delegate, or mutable simulator reference may be stored in these slots.
/// </summary>
internal sealed partial class ReversibleValueState
{
    private readonly object _rootIdentity;
    private readonly long[] _values;
    private readonly ValuePage[] _pages;
    private readonly bool[] _dirtyPages;
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

    public ReversibleValueState(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        _rootIdentity = new object();
        _values = new long[slotCount];
        _pages = new ValuePage[PageCount(slotCount)];
        Array.Fill(_pages, ValuePage.Empty);
        _dirtyPages = new bool[_pages.Length];
    }

    private ReversibleValueState(object rootIdentity, long[] ownedValues)
    {
        _rootIdentity = rootIdentity;
        _values = ownedValues;
        _pages = new ValuePage[PageCount(ownedValues.Length)];
        Array.Fill(_pages, ValuePage.Empty);
        _dirtyPages = new bool[_pages.Length];
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
        _dirtyPages[slot / PageWidth] = true;
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
            _dirtyPages[entry.Slot / PageWidth] = true;
        }
        _undo.RemoveRange(checkpoint.UndoIndex, _undo.Count - checkpoint.UndoIndex);
        _checkpoints.RemoveAt(_checkpoints.Count - 1);
    }

    public FrozenValues Freeze()
    {
        for (int page = 0; page < _pages.Length; page++)
        {
            if (!_dirtyPages[page]) continue;
            _pages[page] = ValuePage.Capture(PageValues(page), _pages[page]);
            _dirtyPages[page] = false;
        }
        return FrozenValues.Capture(this);
    }

    public void Restore(FrozenValues source)
    {
        // Reject before any write. A suspended execution is stored in value slots, but
        // the destination journal must be idle: restore cannot invalidate active marks.
        if (!source.HasRoot(_rootIdentity) || source.Count != Count)
            throw new InvalidOperationException("Compact restore requires the same immutable root.");
        if (_checkpoints.Count != 0)
            throw new InvalidOperationException("Compact restore requires an idle workspace journal.");
        source.RestoreInto(this);
    }

    private Span<long> PageValues(int page)
        => _values.AsSpan(page * PageWidth, Math.Min(PageWidth, Count - page * PageWidth));

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
