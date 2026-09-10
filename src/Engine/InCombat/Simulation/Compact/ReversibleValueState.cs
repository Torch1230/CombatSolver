namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// An exclusively owned value workspace. Execution frames and semantic state use the same
/// slots, so rolling back a choice restores both data and the next instruction to execute.
/// No model, delegate, or mutable simulator reference may be stored in these slots.
/// </summary>
internal sealed partial class ReversibleValueState
{
    private readonly object _rootIdentity;
    private long[] _values;
    private ValuePage[] _pages;
    private bool[] _dirtyPages;
    private int _count;
    private readonly List<UndoEntry> _undo = new(128);
    private readonly List<Checkpoint> _checkpoints = new(8);
    private long _nextSequence;
    private int _journalSlotCount;

    private readonly record struct UndoEntry(int Slot, long Previous);

    internal readonly record struct Checkpoint
    {
        internal ReversibleValueState Owner { get; }
        internal long Sequence { get; }
        internal int UndoIndex { get; }
        internal int SlotCount { get; }

        internal Checkpoint(ReversibleValueState owner, long sequence, int undoIndex, int slotCount)
        {
            Owner = owner;
            Sequence = sequence;
            UndoIndex = undoIndex;
            SlotCount = slotCount;
        }
    }

    public ReversibleValueState(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        _rootIdentity = new object();
        _count = slotCount;
        _values = new long[slotCount];
        _pages = new ValuePage[PageCount(slotCount)];
        Array.Fill(_pages, ValuePage.Empty);
        _dirtyPages = new bool[_pages.Length];
    }

    private ReversibleValueState(object rootIdentity, long[] ownedValues)
    {
        _rootIdentity = rootIdentity;
        _count = ownedValues.Length;
        _values = ownedValues;
        _pages = new ValuePage[PageCount(ownedValues.Length)];
        Array.Fill(_pages, ValuePage.Empty);
        _dirtyPages = new bool[_pages.Length];
    }

    public int Count => _count;
    public int CheckpointDepth => _checkpoints.Count;
    public long WriteAttempts { get; private set; }
    public long ValueChanges { get; private set; }
    public long UndoEntriesWritten { get; private set; }
    public int PeakUndoEntries { get; private set; }
    public long this[int slot]
    {
        get { RequireSlot(slot); return _values[slot]; }
    }

    // Slot identity is local to this branch. Append-only allocation participates in
    // checkpoints, so rollback removes generated state and cannot expose stale values
    // when a sibling reuses the same indices. Retained capacity is workspace-owned.
    public int Allocate(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
        int start = Count;
        Resize(checked(start + slotCount));
        return start;
    }

    public int Append(long value)
    {
        int slot = Count;
        int count = checked(slot + 1);
        if (count > _values.Length) Resize(count);
        else _count = count;
        // Newly allocated values need no undo entry. Unused capacity is always zero,
        // and the containing page's bitmap already encodes an appended zero correctly.
        WriteAttempts++;
        if (value != 0)
        {
            _values[slot] = value;
            _dirtyPages[slot / PageWidth] = true;
            ValueChanges++;
        }
        return slot;
    }

    public void Write(int slot, long value)
    {
        // Validate the slot before touching diagnostics or the journal.
        RequireSlot(slot);
        long previous = _values[slot];
        WriteAttempts++;
        if (previous == value)
            return;
        // Slots appended after the nearest mark disappear with its length rollback.
        // Only slots that existed at that mark need their previous values journaled.
        if (slot < _journalSlotCount)
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
        Checkpoint checkpoint = new(this, checked(++_nextSequence), _undo.Count, Count);
        _checkpoints.Add(checkpoint);
        _journalSlotCount = Count;
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
        Resize(checkpoint.SlotCount);
        _checkpoints.RemoveAt(_checkpoints.Count - 1);
        _journalSlotCount = _checkpoints.Count == 0 ? 0 : _checkpoints[^1].SlotCount;
    }

    public FrozenValues Freeze()
    {
        for (int page = 0; page < PageCount(Count); page++)
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
        if (!source.HasRoot(_rootIdentity))
            throw new InvalidOperationException("Compact restore requires the same immutable root.");
        if (_checkpoints.Count != 0)
            throw new InvalidOperationException("Compact restore requires an idle workspace journal.");
        source.RestoreInto(this);
    }

    private void RequireSlot(int slot)
    {
        if ((uint)slot >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private void Resize(int count)
    {
        if (count == Count) return;
        int previousCount = Count;
        if (count > _values.Length)
        {
            int capacity = (int)Math.Min(Array.MaxLength,
                Math.Max((long)count, Math.Max(64L, (long)_values.Length * 2)));
            // Array.Resize preserves the runtime's normal allocation failure behavior.
            // Do not turn an unrepresentable state into a truncated successful branch.
            Array.Resize(ref _values, Math.Max(count, capacity));
            int oldPages = _pages.Length;
            Array.Resize(ref _pages, PageCount(_values.Length));
            Array.Fill(_pages, ValuePage.Empty, oldPages, _pages.Length - oldPages);
            Array.Resize(ref _dirtyPages, _pages.Length);
        }
        if (count < previousCount)
        {
            Array.Clear(_values, count, previousCount - count);
            int firstRemovedPage = PageCount(count);
            Array.Fill(_pages, ValuePage.Empty, firstRemovedPage, PageCount(previousCount) - firstRemovedPage);
            Array.Clear(_dirtyPages, firstRemovedPage, PageCount(previousCount) - firstRemovedPage);
        }
        _count = count;
        // Shrinking a partial page removes values from its bitmap. Growing adds only
        // zeros, which every previously captured page already represents correctly.
        if (count < previousCount && count % PageWidth != 0)
            _dirtyPages[count / PageWidth] = true;
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
        // Allocation creates observable zero-valued slots too. Include the entire new
        // suffix, whose rollback is represented by length rather than undo entries.
        for (int slot = checkpoint.SlotCount; slot < Count; slot++) slots.Add(slot);
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
